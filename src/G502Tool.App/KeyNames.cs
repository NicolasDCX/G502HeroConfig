using System.Text;
using System.Windows.Input;
using G502Tool.App.Hid;
using G502Tool.Core.Model;

namespace G502Tool.App;

/// <summary>Converts between WPF keys and HID usages via scan codes, so shortcuts follow the physical key on any layout.</summary>
static class KeyNames
{
    public static byte? HidFromKey(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;
        uint sc = Native.MapVirtualKeyW((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        if (sc == 0) return null;
        return ScanCodes.ToHid((int)sc);
    }

    public static byte ModifiersFromKeyboard()
    {
        var m = Keyboard.Modifiers;
        byte r = 0;
        if (m.HasFlag(ModifierKeys.Control)) r |= HidKeys.Ctrl;
        if (m.HasFlag(ModifierKeys.Shift)) r |= HidKeys.Shift;
        if (m.HasFlag(ModifierKeys.Alt)) r |= HidKeys.Alt;
        if (m.HasFlag(ModifierKeys.Windows)) r |= HidKeys.Win;
        return r;
    }

    public static string KeyName(byte usage)
    {
        if (ScanCodes.FromHid(usage) is int sc)
        {
            int lParam = ((sc & 0xFF) << 16) | ((sc & 0xFF00) != 0 ? 1 << 24 : 0);
            var sb = new StringBuilder(64);
            if (Native.GetKeyNameTextW(lParam, sb, sb.Capacity) > 0) return sb.ToString();
        }
        return HidKeys.KeyName(usage);
    }

    public static string Describe(byte mods, byte usage)
    {
        var parts = new List<string>();
        if ((mods & 0x11) != 0) parts.Add("Ctrl");
        if ((mods & 0x22) != 0) parts.Add("Shift");
        if ((mods & 0x44) != 0) parts.Add("Alt");
        if ((mods & 0x88) != 0) parts.Add("Win");
        if (usage != 0) parts.Add(KeyName(usage));
        return parts.Count == 0 ? "No key" : string.Join("+", parts);
    }

    public static string Describe(ButtonBinding b)
        => b.Kind == BindingKind.Keyboard ? Describe(b.Modifiers, b.KeyUsage) : b.Describe();

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;
}
