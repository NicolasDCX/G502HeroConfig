namespace G502Tool.Core.Model;

public enum BindingKind { MouseButton, Keyboard, Consumer, Special, Macro, Disabled, Unknown }

/// <summary>4-byte onboard-profile button binding. Unknown and macro bindings are preserved byte-for-byte.</summary>
public sealed class ButtonBinding : IEquatable<ButtonBinding>
{
    public byte[] Raw { get; }

    public ButtonBinding(byte[] raw)
    {
        if (raw.Length != 4) throw new ArgumentException("binding must be 4 bytes");
        Raw = raw;
    }

    public BindingKind Kind => Raw[0] switch
    {
        0x80 when Raw[1] == 0x01 => BindingKind.MouseButton,
        0x80 when Raw[1] == 0x02 => BindingKind.Keyboard,
        0x80 when Raw[1] == 0x03 => BindingKind.Consumer,
        0x90 => BindingKind.Special,
        0x00 => BindingKind.Macro,
        0xFF => BindingKind.Disabled,
        _ => BindingKind.Unknown,
    };

    /// <summary>1-based mouse button number (1 left, 2 right, 3 middle, 4 back, 5 forward).</summary>
    public int MouseButtonNumber
    {
        get
        {
            int mask = (Raw[2] << 8) | Raw[3];
            for (int i = 0; i < 16; i++) if (mask == 1 << i) return i + 1;
            return 0;
        }
    }

    public byte Modifiers => Raw[2];
    public byte KeyUsage => Raw[3];
    public byte SpecialCode => Raw[1];

    public static ButtonBinding Mouse(int number)
    {
        int mask = 1 << (number - 1);
        return new(new byte[] { 0x80, 0x01, (byte)(mask >> 8), (byte)mask });
    }

    public static ButtonBinding Key(byte modifiers, byte usage) => new(new byte[] { 0x80, 0x02, modifiers, usage });
    public static ButtonBinding Special(byte code) => new(new byte[] { 0x90, code, 0x00, 0x00 });
    public static ButtonBinding Disabled() => new(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

    // Special function codes (libratbag hidpp20.h HIDPP20_BUTTON_SPECIAL_*)
    public static readonly IReadOnlyDictionary<byte, string> SpecialNames = new Dictionary<byte, string>
    {
        [0x01] = "Scroll left (tilt)",
        [0x02] = "Scroll right (tilt)",
        [0x03] = "DPI up",
        [0x04] = "DPI down",
        [0x05] = "DPI cycle",
        [0x06] = "DPI default",
        [0x07] = "DPI shift (sniper)",
        [0x08] = "Next profile",
        [0x09] = "Previous profile",
        [0x0A] = "Cycle profiles",
        [0x0B] = "G-shift",
    };

    public string Describe()
    {
        switch (Kind)
        {
            case BindingKind.MouseButton:
                return MouseButtonNumber switch
                {
                    1 => "Left click",
                    2 => "Right click",
                    3 => "Middle click",
                    4 => "Back",
                    5 => "Forward",
                    0 => $"Mouse buttons (mask {Raw[2]:X2}{Raw[3]:X2})",
                    var n => $"Mouse button {n}",
                };
            case BindingKind.Keyboard:
                return HidKeys.Describe(Modifiers, KeyUsage);
            case BindingKind.Consumer:
                int cc = (Raw[2] << 8) | Raw[3];
                return cc switch
                {
                    0xE9 => "Volume up",
                    0xEA => "Volume down",
                    0xE2 => "Mute",
                    0xCD => "Play/Pause",
                    0xB5 => "Next track",
                    0xB6 => "Previous track",
                    _ => $"Media key 0x{cc:X4}",
                };
            case BindingKind.Special:
                return SpecialNames.TryGetValue(SpecialCode, out var s) ? s : $"Special function 0x{SpecialCode:X2}";
            case BindingKind.Macro:
                return "Macro (kept as is)";
            case BindingKind.Disabled:
                return "Disabled";
            default:
                return $"Unknown ({Convert.ToHexString(Raw)}, kept as is)";
        }
    }

    public bool Equals(ButtonBinding? other) => other != null && Raw.AsSpan().SequenceEqual(other.Raw);
    public override bool Equals(object? obj) => Equals(obj as ButtonBinding);
    public override int GetHashCode() => BitConverter.ToInt32(Raw);
    public override string ToString() => Describe();
}

/// <summary>USB HID keyboard usage names (usage page 0x07) and modifier bits.</summary>
public static class HidKeys
{
    public const byte Ctrl = 0x01, Shift = 0x02, Alt = 0x04, Win = 0x08;

    static readonly Dictionary<byte, string> Names = Build();

    static Dictionary<byte, string> Build()
    {
        var d = new Dictionary<byte, string>();
        for (int i = 0; i < 26; i++) d[(byte)(0x04 + i)] = ((char)('A' + i)).ToString();
        for (int i = 0; i < 9; i++) d[(byte)(0x1E + i)] = ((char)('1' + i)).ToString();
        d[0x27] = "0";
        d[0x28] = "Enter"; d[0x29] = "Esc"; d[0x2A] = "Backspace"; d[0x2B] = "Tab"; d[0x2C] = "Space";
        d[0x2D] = "-"; d[0x2E] = "="; d[0x2F] = "["; d[0x30] = "]"; d[0x31] = "\\"; d[0x33] = ";";
        d[0x34] = "'"; d[0x35] = "`"; d[0x36] = ","; d[0x37] = "."; d[0x38] = "/"; d[0x39] = "Caps Lock";
        for (int i = 0; i < 12; i++) d[(byte)(0x3A + i)] = $"F{i + 1}";
        d[0x46] = "Print Screen"; d[0x47] = "Scroll Lock"; d[0x48] = "Pause"; d[0x49] = "Insert";
        d[0x4A] = "Home"; d[0x4B] = "Page Up"; d[0x4C] = "Delete"; d[0x4D] = "End"; d[0x4E] = "Page Down";
        d[0x4F] = "Right"; d[0x50] = "Left"; d[0x51] = "Down"; d[0x52] = "Up";
        for (int i = 0; i < 12; i++) d[(byte)(0x68 + i)] = $"F{i + 13}";
        return d;
    }

    public static string KeyName(byte usage) => Names.TryGetValue(usage, out var n) ? n : $"Key 0x{usage:X2}";

    public static string Describe(byte mods, byte usage)
    {
        var parts = new List<string>();
        if ((mods & (Ctrl | 0x10)) != 0) parts.Add("Ctrl");
        if ((mods & (Shift | 0x20)) != 0) parts.Add("Shift");
        if ((mods & (Alt | 0x40)) != 0) parts.Add("Alt");
        if ((mods & (Win | 0x80)) != 0) parts.Add("Win");
        if (usage != 0) parts.Add(KeyName(usage));
        return parts.Count == 0 ? "No key" : string.Join("+", parts);
    }
}
