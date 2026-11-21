using System.Collections.Concurrent;
using System.Diagnostics;
using G502Tool.App.Hid;
using G502Tool.Core;
using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;

namespace G502Tool.App;

/// <summary>Runs all device I/O on one background thread, in order. The UI thread never blocks on USB.</summary>
sealed class DeviceWorker : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;

    public DeviceWorker()
    {
        _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); })
        { IsBackground = true, Name = "Device worker" };
        _thread.Start();
    }

    public Task<T> Run<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { tcs.SetResult(f()); }
                catch (Exception e) { tcs.SetException(e); }
            });
        }
        catch (InvalidOperationException) { tcs.SetCanceled(); } // shutting down
        return tcs.Task;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1500);
    }
}

enum ConnState { Searching, Connected, NotFound, Unsupported, Error, Disconnected }

sealed record ConnStatus(ConnState State, string Title, string Detail);

sealed class DeviceController : IDisposable
{
    readonly DeviceWorker _worker = new();
    WinHidTransport? _transport;
    G502Session? _session;
    volatile bool _disposed;

    public HidppTrace? Trace => _session?.Device.Trace;
    public bool IsConnected => _session != null;
    public string? DevicePath => _transport?.Path;

    /// <summary>Raised on the worker thread.</summary>
    public event Action<ConnStatus>? StatusChanged;
    public event Action<DeviceSnapshot>? SnapshotLoaded;

    public static bool IsGHubRunning()
    {
        foreach (var n in new[] { "lghub", "lghub_agent", "lghub_system_tray", "lghub_updater" })
        {
            var p = Process.GetProcessesByName(n);
            bool any = p.Length > 0;
            foreach (var x in p) x.Dispose();
            if (any) return true;
        }
        return false;
    }

    /// <summary>Look for the mouse and load its configuration. Safe to call repeatedly.</summary>
    public Task<bool> ConnectAsync(bool quiet) => _worker.Run(() => ConnectCore(quiet));

    bool ConnectCore(bool quiet)
    {
        if (_disposed) return false;
        if (_session != null) return true;
        if (!quiet) Raise(ConnState.Searching, "Looking for the G502 HERO…", "");

        List<HidInterfaceInfo> all;
        try { all = HidEnumerator.Enumerate(KnownDevices.LogitechVid); }
        catch (Exception e)
        {
            Raise(ConnState.Error, "Could not list USB HID devices", $"{e.Message}\nNext step: click Retry. If this keeps happening, restart Windows' HID service by unplugging and reconnecting the mouse.");
            return false;
        }

        var mine = all.Where(i => i.Pid == KnownDevices.G502HeroPid).ToList();
        if (mine.Count == 0)
        {
            var others = all.Select(i => KnownDevices.Find(i.Pid)).Where(k => k != null && !k.Supported).Distinct().ToList();
            if (others.Count > 0)
                Raise(ConnState.Unsupported, $"Found {others[0]!.Name}, which this tool does not support",
                    $"USB ID 046D:{others[0]!.ProductId:X4}. {others[0]!.Note} This tool only configures the wired G502 HERO (046D:C08B) and will not write to other models.");
            else
                Raise(ConnState.NotFound, "No G502 HERO connected",
                    "Plug the mouse's USB cable into the PC (directly, not through a KVM switch). It will be detected automatically.");
            return false;
        }

        var hidpp = mine.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x0002 && i.OutputLength >= HidppDevice.LongLength);
        if (hidpp == null)
        {
            string seen = string.Join(", ", mine.Select(i => $"{i.UsagePage:X4}/{i.Usage:X4}"));
            Raise(ConnState.Error, "The mouse's configuration interface is not visible",
                $"Windows shows the G502 HERO, but not its HID++ collection (usage page FF00, usage 0002). Collections seen: {seen}.\n" +
                "Next step: open Device Manager → Human Interface Devices and make sure every 'HID-compliant vendor-defined device' is enabled. " +
                "If another mouse tool installed its own filter driver, uninstall that tool, then unplug and reconnect the mouse.");
            return false;
        }

        WinHidTransport t;
        var shortCol = mine.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x0001);
        try { t = WinHidTransport.Open(hidpp, shortCol); }
        catch (DeviceAccessException e)
        {
            string next = e.Win32Error is 5 or 32
                ? "Another program has the mouse's configuration interface open exclusively. Quit Logitech G HUB (tray icon → Quit) and any other mouse utility, then click Retry. Administrator rights are not needed."
                : "Unplug and reconnect the mouse, then click Retry.";
            Raise(ConnState.Error, "Windows refused access to the mouse", $"{e.Message}\nNext step: {next}");
            return false;
        }

        var session = new G502Session(new HidppDevice(t));
        try
        {
            session.Device.Trace.Note($"Opened {hidpp.Path} ({hidpp.Product}) in={hidpp.InputLength} out={hidpp.OutputLength}");
            var snap = session.ReadSnapshot(hidpp.Product ?? "G502 HERO");
            _transport = t;
            _session = session;
            t.Faulted += _ => _worker.Run(() => { HandleDisconnect(); return 0; });
            Raise(ConnState.Connected, $"{snap.DeviceName} connected",
                $"USB 046D:C08B · HID++ {snap.ProtocolMajor}.{snap.ProtocolMinor}" + (snap.Firmware != null ? $" · firmware {snap.Firmware}" : ""));
            SnapshotLoaded?.Invoke(snap);
            return true;
        }
        catch (Exception e) when (e is DeviceTimeoutException or DeviceDisconnectedException or HidppException)
        {
            t.Dispose();
            string next = e is HidppException
                ? "Next step: click Retry. If it fails again, use 'Copy diagnostics' and keep the text for troubleshooting."
                : "Next step: unplug the mouse, wait 2 seconds, plug it back in. It will be detected automatically.";
            Raise(ConnState.Error, "The mouse did not respond correctly", $"{e.Message}\n{next}");
            return false;
        }
    }

    void HandleDisconnect()
    {
        if (_session == null) return;
        _transport?.Dispose();
        _transport = null;
        _session = null;
        Raise(ConnState.Disconnected, "Mouse disconnected", "Plug it back in; it will be detected automatically. Nothing needs to be restarted.");
    }

    /// <summary>Run an operation against the connected mouse on the worker thread.</summary>
    public Task<T> RunAsync<T>(Func<G502Session, T> op) => _worker.Run(() =>
    {
        var s = _session ?? throw new DeviceDisconnectedException("The mouse is not connected.");
        try { return op(s); }
        catch (DeviceDisconnectedException) { HandleDisconnect(); throw; }
    });

    public Task<DeviceSnapshot> ReloadAsync() => RunAsync(s =>
    {
        var snap = s.ReadSnapshot("G502 HERO");
        SnapshotLoaded?.Invoke(snap);
        return snap;
    });

    void Raise(ConnState s, string title, string detail) => StatusChanged?.Invoke(new ConnStatus(s, title, detail));

    public void Dispose()
    {
        _disposed = true;
        _worker.Run(() => { _transport?.Dispose(); _transport = null; _session = null; return 0; });
        _worker.Dispose();
    }
}
