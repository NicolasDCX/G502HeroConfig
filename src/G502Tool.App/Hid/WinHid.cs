using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using G502Tool.Core.Hidpp;
using Microsoft.Win32.SafeHandles;

namespace G502Tool.App.Hid;

public sealed record HidInterfaceInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, int InputLength, int OutputLength, string? Product);

/// <summary>Opening the HID++ collection failed; carries the Win32 error code.</summary>
public sealed class DeviceAccessException : Exception
{
    public int Win32Error { get; }
    public DeviceAccessException(int err, string message) : base(message) => Win32Error = err;
}

/// <summary>Enumerates HID collections through the standard Windows HID class driver (no custom driver).</summary>
public static unsafe class HidEnumerator
{
    public static List<HidInterfaceInfo> Enumerate(ushort vendorId)
    {
        var result = new List<HidInterfaceInfo>();
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            string vidTag = $"vid_{vendorId:x4}";
            for (uint i = 0; ; i++)
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(Native.SP_DEVICE_INTERFACE_DATA) };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData)) break;
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;
                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buf, needed, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    if (!path.Contains(vidTag, StringComparison.OrdinalIgnoreCase)) continue; // skip non-Logitech devices without opening them
                    var info = Query(path);
                    if (info != null) result.Add(info);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    static HidInterfaceInfo? Query(string path)
    {
        // Access 0 = query only; works even for collections Windows holds exclusively (mouse, keyboard).
        using var h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new Native.HIDD_ATTRIBUTES { Size = sizeof(Native.HIDD_ATTRIBUTES) };
        if (!Native.HidD_GetAttributes(h, ref attr)) return null;
        if (!Native.HidD_GetPreparsedData(h, out var pp)) return null;
        Native.HIDP_CAPS caps;
        try
        {
            if (Native.HidP_GetCaps(pp, out caps) != Native.HIDP_STATUS_SUCCESS) return null;
        }
        finally { Native.HidD_FreePreparsedData(pp); }
        string? product = null;
        var name = new char[128];
        fixed (char* p = name)
            if (Native.HidD_GetProductString(h, p, name.Length * 2)) product = new string(p).TrimEnd('\0');
        return new HidInterfaceInfo(path, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, product);
    }
}

/// <summary>
/// HID++ transport over one Windows HID collection (the vendor-defined long-report collection,
/// usage page 0xFF00 / usage 0x0002). Overlapped I/O; a background reader detects unplugging.
/// </summary>
public sealed unsafe class WinHidTransport : IHidppTransport
{
    readonly SafeFileHandle _h;
    readonly SafeFileHandle? _h2; // optional short-report collection, read only for replies
    readonly int _inLen, _outLen, _inLen2;
    readonly BlockingCollection<byte[]> _input = new(256);
    readonly Thread _reader;
    readonly Thread? _reader2;
    volatile bool _closed;
    volatile Exception? _fault;

    public bool SupportsShortReports { get; }
    public bool SupportsLongReports { get; }
    public string Path { get; }

    public event Action<Exception>? Faulted;

    WinHidTransport(SafeFileHandle h, HidInterfaceInfo info, SafeFileHandle? h2, HidInterfaceInfo? info2)
    {
        _h = h;
        Path = info.Path;
        _inLen = info.InputLength;
        _outLen = info.OutputLength;
        SupportsLongReports = info.Usage == 0x0002;
        SupportsShortReports = info.Usage == 0x0001;
        _reader = new Thread(() => ReaderLoop(_h, _inLen, primary: true)) { IsBackground = true, Name = "HID reader" };
        _reader.Start();
        if (h2 != null && info2 != null)
        {
            _h2 = h2;
            _inLen2 = info2.InputLength;
            _reader2 = new Thread(() => ReaderLoop(_h2, _inLen2, primary: false)) { IsBackground = true, Name = "HID reader (short)" };
            _reader2.Start();
        }
    }

    /// <summary>Open the long-report collection for I/O; also listen on the short one (if present) in case replies arrive there.</summary>
    public static WinHidTransport Open(HidInterfaceInfo info, HidInterfaceInfo? shortInfo)
    {
        var h = OpenHandle(info.Path);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new DeviceAccessException(err, $"Windows could not open the mouse's HID++ interface: {new Win32Exception(err).Message} (Win32 error {err}).");
        }
        SafeFileHandle? h2 = null;
        if (shortInfo != null && shortInfo.InputLength > 0)
        {
            h2 = OpenHandle(shortInfo.Path);
            if (h2.IsInvalid) { h2.Dispose(); h2 = null; }
        }
        return new WinHidTransport(h, info, h2, h2 != null ? shortInfo : null);
    }

    static SafeFileHandle OpenHandle(string path) => Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
        Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

    void ReaderLoop(SafeFileHandle h, int inLen, bool primary)
    {
        var buf = (byte*)NativeMemory.Alloc((nuint)inLen);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        using var ev = new ManualResetEvent(false);
        try
        {
            while (!_closed)
            {
                *ov = default;
                ov->EventHandle = ev.SafeWaitHandle.DangerousGetHandle();
                ev.Reset();
                if (!Native.ReadFile(h, buf, inLen, null, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) { if (primary) Fail(err); return; }
                }
                while (!ev.WaitOne(250))
                {
                    if (_closed)
                    {
                        Native.CancelIoEx(h, ov);
                        Native.GetOverlappedResult(h, ov, out _, true);
                        return;
                    }
                }
                if (!Native.GetOverlappedResult(h, ov, out int n, false))
                {
                    if (_closed) return;
                    int err = Marshal.GetLastWin32Error();
                    if (primary) Fail(err);
                    return;
                }
                if (n > 0)
                {
                    var report = new ReadOnlySpan<byte>(buf, n).ToArray();
                    if (!_input.TryAdd(report)) { _input.TryTake(out _); _input.TryAdd(report); }
                }
            }
        }
        finally
        {
            NativeMemory.Free(buf);
            NativeMemory.Free(ov);
        }
    }

    void Fail(int err)
    {
        if (_closed) return;
        var ex = new DeviceDisconnectedException($"The mouse stopped responding over USB: {new Win32Exception(err).Message} (Win32 error {err}).");
        _fault = ex;
        _input.TryAdd(Array.Empty<byte>()); // wake a waiting Read
        Faulted?.Invoke(ex);
    }

    public void Write(byte[] report)
    {
        if (_fault != null) throw _fault;
        if (_closed) throw new ObjectDisposedException(nameof(WinHidTransport));
        var data = new byte[Math.Max(_outLen, report.Length)];
        report.CopyTo(data, 0);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        using var ev = new ManualResetEvent(false);
        try
        {
            ov->EventHandle = ev.SafeWaitHandle.DangerousGetHandle();
            fixed (byte* p = data)
            {
                if (!Native.WriteFile(_h, p, data.Length, null, ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) throw Disconnected(err);
                }
                if (!ev.WaitOne(1000))
                {
                    Native.CancelIoEx(_h, ov);
                    Native.GetOverlappedResult(_h, ov, out _, true);
                    throw new DeviceTimeoutException("Writing to the mouse timed out after 1 s.");
                }
                if (!Native.GetOverlappedResult(_h, ov, out _, false))
                    throw Disconnected(Marshal.GetLastWin32Error());
            }
        }
        finally { NativeMemory.Free(ov); }
    }

    static DeviceDisconnectedException Disconnected(int err)
        => new($"Sending to the mouse failed: {new Win32Exception(err).Message} (Win32 error {err}).");

    public byte[]? Read(int timeoutMs)
    {
        if (_fault != null) throw _fault;
        if (!_input.TryTake(out var r, timeoutMs)) return null;
        if (r.Length == 0 && _fault != null) throw _fault;
        return r;
    }

    public void DrainInput()
    {
        while (_input.TryTake(out var r))
            if (r.Length == 0 && _fault != null) throw _fault;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _reader.Join(1000);
        _reader2?.Join(1000);
        _h.Dispose();
        _h2?.Dispose();
    }
}

static unsafe class Native
{
    public const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public UIntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle h, char* buffer, int bufferLength);

    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte* buf, int len, int* read, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte* buf, int len, int* written, NativeOverlapped* ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* ov, out int transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIoEx(SafeFileHandle h, NativeOverlapped* ov);

    [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameTextW(int lParam, StringBuilder buf, int size);
}
