namespace G502Tool.Core.Model;

/// <summary>11-byte onboard LED effect (libratbag hidpp20_internal_led). Only Off and Solid are written by this app.</summary>
public static class Lighting
{
    public const byte ModeOff = 0x00, ModeSolid = 0x01, ModeCycle = 0x03, ModeWave = 0x04,
        ModeStarlight = 0x05, ModeBreathing = 0x0A, ModeRipple = 0x0B, ModeCustom = 0x0C;

    public static byte[] Off() => new byte[11];

    public static byte[] Solid(byte r, byte g, byte b)
    {
        var d = new byte[11];
        d[0] = ModeSolid; d[1] = r; d[2] = g; d[3] = b;
        return d;
    }

    public static string Describe(byte[] led) => led[0] switch
    {
        ModeOff => "Off",
        ModeSolid => $"Solid #{led[1]:X2}{led[2]:X2}{led[3]:X2}",
        ModeCycle => "Color cycle",
        ModeWave => "Color wave",
        ModeStarlight => "Starlight",
        ModeBreathing => $"Breathing #{led[1]:X2}{led[2]:X2}{led[3]:X2}",
        ModeRipple => "Ripple",
        ModeCustom => "Custom animation",
        _ => $"Effect 0x{led[0]:X2}",
    };
}
