using System.Diagnostics;

namespace G502Tool.Core.Hidpp;

public sealed class HidppException : Exception
{
    public byte ErrorCode { get; }
    public HidppException(byte code, ushort featureId, byte function)
        : base($"Device returned HID++ error 0x{code:X2} ({ErrorName(code)}) for feature 0x{featureId:X4} function {function}.")
    {
        ErrorCode = code;
    }

    public HidppException(string message) : base(message) { }

    public static string ErrorName(byte code) => code switch
    {
        0x00 => "no error",
        0x01 => "unknown",
        0x02 => "invalid argument",
        0x03 => "out of range",
        0x04 => "hardware error",
        0x05 => "Logitech internal",
        0x06 => "invalid feature index",
        0x07 => "invalid function",
        0x08 => "busy",
        0x09 => "unsupported",
        _ => "undocumented",
    };
}

public sealed class DeviceTimeoutException : Exception
{
    public DeviceTimeoutException(string message) : base(message) { }
}

/// <summary>
/// HID++ 2.0 request/response for a corded device (device index 0xFF).
/// Not thread-safe: callers serialize access (DeviceWorker in the app).
/// </summary>
public sealed class HidppDevice
{
    public const byte ReportShort = 0x10;
    public const byte ReportLong = 0x11;
    public const int ShortLength = 7;
    public const int LongLength = 20;
    public const byte CordedDeviceIndex = 0xFF;
    // Software ID in the low nibble of the function byte. The device echoes it in the reply, so it is
    // the only thing that ties a reply to its request (a memory-read reply does not repeat its offset).
    // It changes on every request and retry, cycling 1..15 (0 is reserved for notifications): a reply
    // that arrives after its request timed out then no longer matches the next request.
    public const byte FirstSoftwareId = 0x01, LastSoftwareId = 0x0F;
    byte _swId = LastSoftwareId;

    byte NextSoftwareId() => _swId = _swId >= LastSoftwareId ? FirstSoftwareId : (byte)(_swId + 1);

    readonly IHidppTransport _t;
    readonly Dictionary<ushort, byte> _featureIndex = new();
    readonly Dictionary<byte, ushort> _featureById = new();

    public int TimeoutMs { get; set; } = 1500;
    public HidppTrace Trace { get; } = new();
    public byte DeviceIndex { get; } = CordedDeviceIndex;

    public HidppDevice(IHidppTransport transport) => _t = transport;

    public IReadOnlyDictionary<ushort, byte> Features => _featureIndex;

    public bool Has(ushort featureId) => _featureIndex.ContainsKey(featureId);

    /// <summary>Ping + protocol version. Returns (major, minor).</summary>
    public (int Major, int Minor) GetProtocolVersion()
    {
        var p = Call(0x0000, 0x00, 1, new byte[] { 0, 0, 0x5A }, idempotent: true);
        if (p[2] != 0x5A) throw new HidppException("Ping byte not echoed; this is not a HID++ 2.0 device.");
        return (p[0], p[1]);
    }

    /// <summary>Look up a feature's index via IRoot.getFeature. 0 = not present.</summary>
    public byte LookupFeature(ushort featureId)
    {
        if (_featureIndex.TryGetValue(featureId, out var idx)) return idx;
        var p = Call(0x0000, 0x00, 0, new byte[] { (byte)(featureId >> 8), (byte)featureId }, idempotent: true);
        idx = p[0];
        if (idx != 0)
        {
            _featureIndex[featureId] = idx;
            _featureById[idx] = featureId;
        }
        return idx;
    }

    /// <summary>Call function <paramref name="function"/> of a feature by ID (feature must have been looked up).</summary>
    public byte[] Call(ushort featureId, byte function, ReadOnlySpan<byte> parameters, bool idempotent = true)
    {
        byte idx;
        if (featureId == 0x0000) idx = 0;
        else if (!_featureIndex.TryGetValue(featureId, out idx))
            throw new HidppException($"Feature 0x{featureId:X4} is not supported by this device.");
        return Call(featureId, idx, function, parameters, idempotent);
    }

    byte[] Call(ushort featureId, byte featureIdx, byte function, ReadOnlySpan<byte> parameters, bool idempotent)
    {
        // Long reports are used for everything when available: the reply then arrives on the
        // long collection, which keeps Windows' per-collection handles simple.
        bool useLong = _t.SupportsLongReports;
        if (!useLong && parameters.Length > 3)
            throw new HidppException("Device has no long HID++ report; cannot send this command.");

        var report = new byte[useLong ? LongLength : ShortLength];
        report[0] = useLong ? ReportLong : ReportShort;
        report[1] = DeviceIndex;
        report[2] = featureIdx;
        parameters.CopyTo(report.AsSpan(4));

        int attempts = idempotent ? 2 : 1;
        for (int attempt = 1; ; attempt++)
        {
            report[3] = (byte)((function << 4) | NextSoftwareId());
            _t.DrainInput();
            Trace.Add(true, report);
            _t.Write(report);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int left = TimeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0) break;
                var r = _t.Read(left);
                if (r == null) break;
                Trace.Add(false, r);
                if (r.Length < 7 || (r[0] != ReportShort && r[0] != ReportLong) || r[1] != DeviceIndex) continue;
                if (r[2] == featureIdx && r[3] == report[3])
                {
                    var result = new byte[16]; // short replies are zero-padded
                    r.AsSpan(4, Math.Min(16, r.Length - 4)).CopyTo(result);
                    return result;
                }
                // HID++ 2.0 error: FF <featIdx> <fn|sw> <code>; HID++ 1.0 error: 8F <subId> <addr> <code>
                if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == featureIdx && r[4] == report[3])
                    throw new HidppException(r[5], featureId, function);
            }
            if (attempt >= attempts)
                throw new DeviceTimeoutException(
                    $"The mouse did not answer feature 0x{featureId:X4} function {function} within {TimeoutMs} ms.");
        }
    }
}

/// <summary>Ring buffer of raw HID++ traffic for the "Copy diagnostics" button. Never leaves the PC unless the user pastes it.</summary>
public sealed class HidppTrace
{
    readonly object _lock = new();
    readonly Queue<string> _lines = new();
    const int Max = 600;

    public void Add(bool outgoing, byte[] data)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {(outgoing ? ">>" : "<<")} {Convert.ToHexString(data)}";
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Max) _lines.Dequeue();
        }
    }

    public void Note(string text)
    {
        lock (_lock)
        {
            _lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} -- {text}");
            while (_lines.Count > Max) _lines.Dequeue();
        }
    }

    public string Dump()
    {
        lock (_lock) return string.Join(Environment.NewLine, _lines);
    }
}
