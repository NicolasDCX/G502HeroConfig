namespace G502Tool.Core.Model;

public sealed record KnownDevice(ushort ProductId, string Name, bool Supported, string Note);

/// <summary>
/// USB product IDs (vendor 0x046D) cross-checked against libratbag data/devices/*.device and
/// Solaar descriptors.py. Only the wired G502 HERO is supported; the others are listed so the
/// app can say precisely why it will not touch them.
/// </summary>
public static class KnownDevices
{
    public const ushort LogitechVid = 0x046D;
    public const ushort G502HeroPid = 0xC08B;

    public static readonly IReadOnlyList<KnownDevice> All = new[]
    {
        new KnownDevice(0xC08B, "G502 HERO (wired)", true, "Supported."),
        new KnownDevice(0xC08D, "G502 LIGHTSPEED (USB cable)", false, "Different device (wireless model, profile index quirks differ)."),
        new KnownDevice(0xC539, "LIGHTSPEED receiver (G502 LIGHTSPEED)", false, "Receiver; the mouse behind it is a different device."),
        new KnownDevice(0x407F, "G502 LIGHTSPEED (via receiver)", false, "Different device."),
        new KnownDevice(0xC099, "G502 X (wired)", false, "Different device and profile format."),
        new KnownDevice(0xC098, "G502 X LIGHTSPEED", false, "Different device."),
        new KnownDevice(0xC095, "G502 X PLUS", false, "Different device (LED layout differs)."),
        new KnownDevice(0xC07D, "G502 Proteus Core", false, "Older model."),
        new KnownDevice(0xC332, "G502 Proteus Spectrum", false, "Older model."),
    };

    public static KnownDevice? Find(ushort pid) => All.FirstOrDefault(d => d.ProductId == pid);
}
