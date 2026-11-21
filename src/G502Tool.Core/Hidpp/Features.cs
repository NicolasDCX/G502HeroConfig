using System.Text;

namespace G502Tool.Core.Hidpp;

public static class FeatureId
{
    public const ushort Root = 0x0000;
    public const ushort DeviceInfo = 0x0003;
    public const ushort DeviceName = 0x0005;
    public const ushort AdjustableDpi = 0x2201;
    public const ushort ReportRate = 0x8060;
    public const ushort ColorLedEffects = 0x8070;
    public const ushort OnboardProfiles = 0x8100;
}

static class Be
{
    public static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    public static void Put(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
}

public static class DeviceInfoFeature
{
    public static string? GetName(HidppDevice d)
    {
        if (!d.Has(FeatureId.DeviceName)) return null;
        int len = d.Call(FeatureId.DeviceName, 0, Array.Empty<byte>())[0];
        var sb = new StringBuilder();
        while (sb.Length < len && sb.Length < 64)
        {
            var p = d.Call(FeatureId.DeviceName, 1, new[] { (byte)sb.Length });
            foreach (var c in p)
            {
                if (sb.Length >= len || c == 0) break;
                sb.Append((char)c);
            }
            if (p[0] == 0) break;
        }
        return sb.ToString();
    }

    /// <summary>Main application firmware version, e.g. "U1 46.00.B0011".</summary>
    public static string? GetFirmware(HidppDevice d)
    {
        if (!d.Has(FeatureId.DeviceInfo)) return null;
        int entities = d.Call(FeatureId.DeviceInfo, 0, Array.Empty<byte>())[0];
        for (int e = 0; e < entities && e < 8; e++)
        {
            var p = d.Call(FeatureId.DeviceInfo, 1, new[] { (byte)e });
            if ((p[0] & 0x0F) != 0) continue; // type 0 = main application
            string prefix = Encoding.ASCII.GetString(p, 1, 3).TrimEnd('\0', ' ');
            return $"{prefix} {p[4]:X2}.{p[5]:X2}.B{p[6]:X2}{p[7]:X2}";
        }
        return null;
    }
}

/// <summary>DPI ranges as reported by 0x2201 getSensorDpiList (either discrete values or min/step/max).</summary>
public sealed record DpiCapabilities(IReadOnlyList<(int Min, int Max, int Step)> Ranges, IReadOnlyList<int> Discrete)
{
    public int Min => Ranges.Count > 0 ? Ranges.Min(r => r.Min) : Discrete.DefaultIfEmpty(0).Min();
    public int Max => Ranges.Count > 0 ? Ranges.Max(r => r.Max) : Discrete.DefaultIfEmpty(0).Max();

    public bool IsValid(int dpi)
    {
        foreach (var r in Ranges)
            if (dpi >= r.Min && dpi <= r.Max && (r.Step <= 0 || (dpi - r.Min) % r.Step == 0)) return true;
        return Discrete.Contains(dpi);
    }

    public string Describe()
    {
        if (Ranges.Count > 0)
            return string.Join(", ", Ranges.Select(r => $"{r.Min}–{r.Max} in steps of {r.Step}"));
        return string.Join(", ", Discrete);
    }

    public static DpiCapabilities Parse(byte[] p)
    {
        // p[0] = sensor index, then big-endian u16 values terminated by 0.
        // A value >= 0xE000 is a step marker: previous value .. next value in (value-0xE000) increments.
        var vals = new List<int>();
        for (int i = 1; i + 1 < p.Length; i += 2)
        {
            int v = Be.U16(p, i);
            if (v == 0) break;
            vals.Add(v);
        }
        var ranges = new List<(int, int, int)>();
        var discrete = new List<int>();
        for (int i = 0; i < vals.Count; i++)
        {
            if (i + 2 < vals.Count && vals[i + 1] >= 0xE000)
            {
                ranges.Add((vals[i], vals[i + 2], vals[i + 1] - 0xE000));
                i += 2;
            }
            else if (vals[i] < 0xE000) discrete.Add(vals[i]);
        }
        return new DpiCapabilities(ranges, discrete);
    }
}

public static class AdjustableDpiFeature
{
    public static DpiCapabilities GetCapabilities(HidppDevice d, byte sensor = 0)
        => DpiCapabilities.Parse(d.Call(FeatureId.AdjustableDpi, 1, new[] { sensor }));

    /// <summary>Current live sensor DPI (what the mouse is using right now).</summary>
    public static int GetDpi(HidppDevice d, byte sensor = 0)
        => Be.U16(d.Call(FeatureId.AdjustableDpi, 2, new[] { sensor }), 1);

    /// <summary>Temporary (RAM-only) DPI change.</summary>
    public static void SetDpi(HidppDevice d, int dpi, byte sensor = 0)
    {
        var p = new byte[3];
        p[0] = sensor;
        Be.Put(p, 1, (ushort)dpi);
        d.Call(FeatureId.AdjustableDpi, 3, p, idempotent: true);
    }
}

public static class ReportRateFeature
{
    /// <summary>Supported intervals in ms (bit n set = (n+1) ms).</summary>
    public static IReadOnlyList<int> GetSupportedIntervalsMs(HidppDevice d)
    {
        byte flags = d.Call(FeatureId.ReportRate, 0, Array.Empty<byte>())[0];
        var list = new List<int>();
        for (int i = 0; i < 8; i++) if ((flags & (1 << i)) != 0) list.Add(i + 1);
        return list;
    }

    public static int GetIntervalMs(HidppDevice d) => d.Call(FeatureId.ReportRate, 1, Array.Empty<byte>())[0];
}

public sealed record OnboardProfilesInfo(
    byte MemoryModel, byte ProfileFormat, byte MacroFormat, int ProfileCount, int RomProfileCount,
    int ButtonCount, int SectorCount, int SectorSize, byte MechanicalLayout, byte VariousInfo)
{
    public static OnboardProfilesInfo Parse(byte[] p) => new(p[0], p[1], p[2], p[3], p[4], p[5], p[6], Be.U16(p, 7), p[9], p[10]);
}

public enum OnboardMode { Unknown = 0, Onboard = 1, Host = 2 }

public static class OnboardProfilesFeature
{
    public const ushort DirectorySector = 0x0000;
    public const ushort RomBase = 0x0100;

    public static OnboardProfilesInfo GetInfo(HidppDevice d) => OnboardProfilesInfo.Parse(d.Call(FeatureId.OnboardProfiles, 0, Array.Empty<byte>()));

    public static OnboardMode GetMode(HidppDevice d) => (OnboardMode)d.Call(FeatureId.OnboardProfiles, 2, Array.Empty<byte>())[0];

    public static void SetMode(HidppDevice d, OnboardMode mode) => d.Call(FeatureId.OnboardProfiles, 1, new[] { (byte)mode });

    /// <summary>Active profile as a sector address: 0x0001..0x00NN = user profile N, 0x01NN = ROM profile.</summary>
    public static ushort GetCurrentProfile(HidppDevice d) => Be.U16(d.Call(FeatureId.OnboardProfiles, 4, Array.Empty<byte>()), 0);

    public static void SetCurrentProfile(HidppDevice d, ushort sector)
    {
        var p = new byte[2];
        Be.Put(p, 0, sector);
        d.Call(FeatureId.OnboardProfiles, 3, p);
    }

    public static int GetCurrentDpiIndex(HidppDevice d) => d.Call(FeatureId.OnboardProfiles, 11, Array.Empty<byte>())[0];

    public static void SetCurrentDpiIndex(HidppDevice d, int index) => d.Call(FeatureId.OnboardProfiles, 12, new[] { (byte)index });

    public static byte[] ReadSector(HidppDevice d, ushort sector, int size)
    {
        var data = new byte[size];
        var p = new byte[4];
        Be.Put(p, 0, sector);
        for (int offset = 0; offset < size; offset += 16)
        {
            // Firmware rejects reads past size-16, so the last chunk is read overlapping.
            int o = size - offset < 16 ? size - 16 : offset;
            Be.Put(p, 2, (ushort)o);
            var chunk = d.Call(FeatureId.OnboardProfiles, 5, p);
            Array.Copy(chunk, 0, data, o, 16);
        }
        return data;
    }

    /// <summary>
    /// Reads a sector and checks that the bytes are consistent, because a sector is read in 16-byte pieces and a
    /// piece can be wrong (for example a stale reply while other software such as G HUB talks to the mouse).
    /// Accepted when the stored CRC matches, or, for sectors without a valid CRC (factory ROM profiles on some
    /// firmware, never-written sectors), when two reads in a row are identical. <c>CrcValid</c> tells which.
    /// Throws if the reads keep disagreeing, so callers never build on mixed-up data.
    /// </summary>
    public static (byte[] Bytes, bool CrcValid) ReadSectorChecked(HidppDevice d, ushort sector, int size)
    {
        const int maxReads = 4;
        byte[]? previous = null;
        for (int read = 1; read <= maxReads; read++)
        {
            var data = ReadSector(d, sector, size);
            if (HasValidCrc(data)) return (data, true);
            if (previous != null && previous.AsSpan().SequenceEqual(data)) return (data, false);
            if (previous != null) d.Trace.Note($"Sector 0x{sector:X4}: read {read} differs from read {read - 1}; reading again.");
            previous = data;
        }
        throw new HidppException(
            $"Reading onboard memory sector 0x{sector:X4} gave different data {maxReads} times in a row. " +
            "Nothing was changed. Quit G HUB (it may be talking to the mouse at the same time), then reload.");
    }

    public static bool HasValidCrc(byte[] sector)
        => sector.Length >= 3 && Crc.Ccitt(sector.AsSpan(0, sector.Length - 2)) == ((sector[^2] << 8) | sector[^1]);

    /// <summary>Write a whole sector. Caller must already have placed the CRC. Not retried (not idempotent).</summary>
    public static void WriteSector(HidppDevice d, ushort sector, byte[] data)
    {
        int size = data.Length;
        var start = new byte[6];
        Be.Put(start, 0, sector);
        Be.Put(start, 2, 0);
        Be.Put(start, 4, (ushort)size);
        d.Call(FeatureId.OnboardProfiles, 6, start, idempotent: false);
        var chunk = new byte[16];
        for (int o = 0; o < size; o += 16)
        {
            Array.Clear(chunk);
            Array.Copy(data, o, chunk, 0, Math.Min(16, size - o));
            d.Call(FeatureId.OnboardProfiles, 7, chunk, idempotent: false);
        }
        d.Call(FeatureId.OnboardProfiles, 8, Array.Empty<byte>(), idempotent: false);
    }
}

public static class Crc
{
    /// <summary>CRC-CCITT (seed 0xFFFF) used by Logitech onboard memory sectors.</summary>
    public static ushort Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            ushort temp = (ushort)((crc >> 8) ^ b);
            crc <<= 8;
            ushort quick = (ushort)(temp ^ (temp >> 4));
            crc ^= quick;
            quick <<= 5;
            crc ^= quick;
            quick <<= 7;
            crc ^= quick;
        }
        return crc;
    }
}
