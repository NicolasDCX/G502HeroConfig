using System.Text;
using G502Tool.Core.Hidpp;

namespace G502Tool.Core.Model;

/// <summary>
/// One onboard profile sector, kept as the exact bytes read from the mouse.
/// Edits touch only the bytes of the edited field; everything else (macros pointers,
/// G-shift bindings, timeouts, unknown/reserved bytes) is written back unchanged.
///
/// Layout (Logitech "G402-family" profile format, as documented by libratbag hidpp20.h):
///   0      report interval in ms (1 = 1000 Hz)
///   1      default DPI index      2  DPI-shift ("sniper") index
///   3..12  5 x DPI, little-endian u16 (0 or 0xFFFF = stage unused)
///   13..15 profile colour RGB   16 power mode   17 angle snapping   18..27 reserved
///   28..31 power-save / power-off timeouts
///   32..95  16 x button binding (4 bytes)       96..159 16 x G-shift binding
///   160..207 profile name (48 bytes)
///   208..229 2 x LED effect (11 bytes)          230..251 2 x alternate LED effect
///   size-2..size-1 CRC-CCITT, big-endian
/// </summary>
public sealed class ProfileSector
{
    public const int DpiSlots = 5;
    public const int ButtonOffset = 32;
    public const int NameOffset = 160;
    public const int NameLength = 48;
    public const int LedOffset = 208;
    public const int AltLedOffset = 230;
    public const int LedSize = 11;
    public const int MinSupportedSize = 254;

    public byte[] Bytes { get; }
    public int Size => Bytes.Length;

    public ProfileSector(byte[] bytes)
    {
        if (bytes.Length < MinSupportedSize)
            throw new ArgumentException($"Profile sector is {bytes.Length} bytes; at least {MinSupportedSize} are required for this layout.");
        Bytes = bytes;
    }

    public ProfileSector Clone() => new((byte[])Bytes.Clone());

    public ushort StoredCrc => (ushort)((Bytes[Size - 2] << 8) | Bytes[Size - 1]);
    public ushort ComputedCrc => Crc.Ccitt(Bytes.AsSpan(0, Size - 2));
    public bool CrcValid => StoredCrc == ComputedCrc;

    public void UpdateCrc()
    {
        ushort c = ComputedCrc;
        Bytes[Size - 2] = (byte)(c >> 8);
        Bytes[Size - 1] = (byte)c;
    }

    public int ReportIntervalMs { get => Bytes[0]; set => Bytes[0] = (byte)value; }
    public int DefaultDpiIndex { get => Bytes[1]; set => Bytes[1] = (byte)value; }
    public int ShiftDpiIndex { get => Bytes[2]; set => Bytes[2] = (byte)value; }

    public int GetDpi(int slot) => Bytes[3 + 2 * slot] | (Bytes[4 + 2 * slot] << 8);

    public void SetDpi(int slot, int dpi)
    {
        Bytes[3 + 2 * slot] = (byte)dpi;
        Bytes[4 + 2 * slot] = (byte)(dpi >> 8);
    }

    public static bool IsStageUsed(int dpi) => dpi != 0 && dpi != 0xFFFF;

    public ButtonBinding GetButton(int index) => new(Bytes.AsSpan(ButtonOffset + 4 * index, 4).ToArray());

    public void SetButton(int index, ButtonBinding b) => b.Raw.CopyTo(Bytes, ButtonOffset + 4 * index);

    public byte[] GetLed(int zone) => Bytes.AsSpan(LedOffset + LedSize * zone, LedSize).ToArray();

    public void SetLed(int zone, byte[] led)
    {
        led.CopyTo(Bytes, LedOffset + LedSize * zone);
        led.CopyTo(Bytes, AltLedOffset + LedSize * zone); // keep the alternate slot consistent (same as libratbag)
    }

    /// <summary>Profile name, or null when the mouse uses its default name (all 0xFF).</summary>
    public string? Name
    {
        get
        {
            var raw = Bytes.AsSpan(NameOffset, NameLength);
            if (raw.IndexOfAnyExcept((byte)0xFF) < 0) return null;
            bool utf16 = raw.Length >= 4 && raw[1] == 0 && raw[0] != 0 && raw[3] == 0;
            string s = utf16 ? Encoding.Unicode.GetString(raw) : Encoding.ASCII.GetString(raw);
            int end = s.IndexOfAny(new[] { '\0', '￿' });
            s = end >= 0 ? s[..end] : s;
            s = new string(s.Where(c => !char.IsControl(c) && c < 0xFFFD).ToArray()).Trim();
            return s.Length == 0 ? null : s;
        }
    }

    /// <summary>Offsets of bytes that differ (for "preserve unrelated settings" checks and diagnostics).</summary>
    public static IReadOnlyList<int> Diff(byte[] a, byte[] b)
    {
        var d = new List<int>();
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (i >= a.Length || i >= b.Length || a[i] != b[i]) d.Add(i);
        return d;
    }
}

/// <summary>Profile directory (sector 0x0000): 4-byte entries {addr hi, addr lo, enabled, 0}, terminated by 0xFFFF.</summary>
public sealed class ProfileDirectory
{
    public sealed record Entry(ushort Sector, bool Enabled);

    public byte[] Bytes { get; }
    public bool CrcValid { get; }
    public List<Entry> Entries { get; } = new();

    public ProfileDirectory(byte[] bytes)
    {
        Bytes = bytes;
        ushort stored = (ushort)((bytes[^2] << 8) | bytes[^1]);
        CrcValid = stored == Crc.Ccitt(bytes.AsSpan(0, bytes.Length - 2));
        if (!CrcValid) return;
        for (int i = 0; i + 3 < bytes.Length - 2; i += 4)
        {
            ushort addr = (ushort)((bytes[i] << 8) | bytes[i + 1]);
            if (addr == 0xFFFF) break;
            Entries.Add(new Entry(addr, bytes[i + 2] != 0));
        }
    }

    public static byte[] Build(int sectorSize, IEnumerable<Entry> entries)
    {
        var b = Enumerable.Repeat((byte)0xFF, sectorSize).ToArray();
        int o = 0;
        foreach (var e in entries)
        {
            b[o++] = (byte)(e.Sector >> 8);
            b[o++] = (byte)e.Sector;
            b[o++] = (byte)(e.Enabled ? 1 : 0);
            b[o++] = 0;
        }
        b[o++] = 0xFF;
        b[o] = 0xFF;
        ushort c = Crc.Ccitt(b.AsSpan(0, sectorSize - 2));
        b[^2] = (byte)(c >> 8);
        b[^1] = (byte)c;
        return b;
    }
}
