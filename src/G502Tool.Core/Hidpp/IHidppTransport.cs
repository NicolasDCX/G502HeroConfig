namespace G502Tool.Core.Hidpp;

/// <summary>
/// Raw HID report pipe to the device's HID++ (vendor 0xFF00) collections.
/// Reports always start with the report ID (0x10 short, 0x11 long).
/// Implementations: WinHidTransport (Windows HID driver) and FakeG502 (tests).
/// </summary>
public interface IHidppTransport : IDisposable
{
    bool SupportsShortReports { get; }
    bool SupportsLongReports { get; }

    /// <summary>Send one full report (report ID first). Throws DeviceDisconnectedException.</summary>
    void Write(byte[] report);

    /// <summary>Next input report, or null when nothing arrived before the timeout.</summary>
    byte[]? Read(int timeoutMs);

    /// <summary>Discard queued input reports (stale notifications).</summary>
    void DrainInput();
}

public sealed class DeviceDisconnectedException : Exception
{
    public DeviceDisconnectedException(string message, Exception? inner = null) : base(message, inner) { }
}
