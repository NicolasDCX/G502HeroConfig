using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;

namespace G502Tool.Tests;

/// <summary>
/// Simulated wired G502 HERO speaking HID++ 2.0 over long reports.
/// Behaviour follows libratbag's implementation of features 0x2201/0x8060/0x8100.
/// It validates this app's logic, NOT the real firmware.
/// </summary>
public sealed class FakeG502 : IHidppTransport
{
    public const byte IdxFeatureSet = 1, IdxInfo = 2, IdxName = 3, IdxDpi = 4, IdxRate = 5, IdxProfiles = 6;
    static readonly Dictionary<ushort, byte> FeatureTable = new()
    {
        [0x0001] = IdxFeatureSet, [0x0003] = IdxInfo, [0x0005] = IdxName,
        [0x2201] = IdxDpi, [0x8060] = IdxRate, [0x8100] = IdxProfiles,
    };

    readonly Queue<byte[]> _out = new();
    public Dictionary<ushort, byte[]> Memory { get; } = new();
    public int SectorSize { get; }
    public int ProfileCount { get; } = 5;
    public int RomProfiles { get; } = 1;
    public int ButtonCount { get; } = 11;

    public OnboardMode Mode { get; set; } = OnboardMode.Onboard;
    public ushort CurrentProfile { get; set; }
    public int DpiIndex { get; set; }
    public int SensorDpi { get; set; }
    public int IntervalMs { get; set; } = 1;

    public bool Unplugged { get; set; }
    public bool CorruptNextSectorWrite { get; set; }
    public int SectorWrites { get; private set; }

    ushort _writeSector, _writeCount, _writeOffset;
    byte[]? _writeBuffer;

    public bool SupportsShortReports => true;
    public bool SupportsLongReports => true;

    public FakeG502(bool factoryState, int sectorSize = 255)
    {
        SectorSize = sectorSize;
        Memory[0x0101] = FactoryProfile();
        Memory[0x0000] = Fill(0xFF); // factory: directory never written (invalid CRC)
        if (!factoryState)
        {
            var entries = new List<ProfileDirectory.Entry>();
            for (int i = 0; i < ProfileCount; i++)
            {
                var p = FactoryProfile();
                var s = new ProfileSector(p);
                s.SetDpi(0, 400 + 100 * i);
                s.SetDpi(1, 800);
                s.SetDpi(2, 1600);
                s.SetDpi(3, 3200);
                s.SetDpi(4, 0); // stage 5 unused
                // a macro binding on button 8 and a G-shift binding, both must survive edits
                s.SetButton(8, new ButtonBinding(new byte[] { 0x00, 0x05, 0x00, 0x10 }));
                s.Bytes[96] = 0x80; s.Bytes[97] = 0x02; s.Bytes[98] = 0x01; s.Bytes[99] = 0x06;
                s.Bytes[20] = 0x5A; // a "reserved" byte with an unknown value
                s.UpdateCrc();
                Memory[(ushort)(i + 1)] = p;
                entries.Add(new((ushort)(i + 1), i < 3));
            }
            Memory[0x0000] = ProfileDirectory.Build(SectorSize, entries);
            CurrentProfile = 0x0001;
        }
        else CurrentProfile = 0x0101;
        LoadProfile(defaultIndex: true);
    }

    byte[] Fill(byte v) => Enumerable.Repeat(v, SectorSize).ToArray();

    byte[] FactoryProfile()
    {
        var b = Fill(0xFF);
        var s = new ProfileSector(b);
        s.ReportIntervalMs = 1;
        s.DefaultDpiIndex = 1;
        s.ShiftDpiIndex = 0;
        int[] dpi = { 400, 800, 1600, 3200, 6400 };
        for (int i = 0; i < 5; i++) s.SetDpi(i, dpi[i]);
        for (int i = 13; i < 32; i++) b[i] = 0;
        var bindings = new[]
        {
            ButtonBinding.Mouse(1), ButtonBinding.Mouse(2), ButtonBinding.Mouse(3), ButtonBinding.Mouse(4), ButtonBinding.Mouse(5),
            ButtonBinding.Special(0x07), ButtonBinding.Special(0x03), ButtonBinding.Special(0x04),
            ButtonBinding.Special(0x01), ButtonBinding.Special(0x02), ButtonBinding.Special(0x0A),
        };
        for (int i = 0; i < bindings.Length; i++) s.SetButton(i, bindings[i]);
        Lighting.Solid(0, 0xAA, 0xFF).CopyTo(b, ProfileSector.LedOffset);
        Lighting.Solid(0, 0xAA, 0xFF).CopyTo(b, ProfileSector.LedOffset + 11);
        s.UpdateCrc();
        return b;
    }

    ProfileSector Active => new(Memory[CurrentProfile]);

    void LoadProfile(bool defaultIndex)
    {
        var s = Active;
        if (defaultIndex) DpiIndex = s.DefaultDpiIndex;
        SensorDpi = s.GetDpi(DpiIndex);
        IntervalMs = s.ReportIntervalMs;
    }

    public void Replug()
    {
        Unplugged = false;
        Mode = OnboardMode.Onboard;
        var dir = new ProfileDirectory(Memory[0]);
        if (!dir.CrcValid) CurrentProfile = 0x0101;
        else if ((CurrentProfile & 0xFF00) != 0) CurrentProfile = dir.Entries.First(e => e.Enabled).Sector;
        LoadProfile(defaultIndex: true);
    }

    public void Write(byte[] report)
    {
        if (Unplugged) throw new DeviceDisconnectedException("unplugged");
        if (report[0] != 0x11 || report.Length != 20) throw new InvalidOperationException("Fake only accepts long reports");
        byte idx = report[2], fnsw = report[3];
        int fn = fnsw >> 4;
        var p = report.AsSpan(4).ToArray();
        byte[]? reply;
        try { reply = Handle(idx, fn, p); }
        catch (FakeError e) { _out.Enqueue(new byte[] { 0x11, 0xFF, 0xFF, idx, fnsw, e.Code, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); return; }
        var r = new byte[20];
        r[0] = 0x11; r[1] = 0xFF; r[2] = idx; r[3] = fnsw;
        reply.CopyTo(r, 4);

        // A reply withheld earlier arrives now, ahead of this request's own reply.
        if (_late != null && --_lateWritesLeft == 0) { _out.Enqueue(_late); _late = null; }
        if (idx == IdxProfiles && fn == 5 && LateReply is (ushort ls, ushort lo) && U16(p, 0) == ls && U16(p, 2) == lo)
        {
            // Too slow: the host times out and retries; this reply turns up during the request after the retry.
            LateReply = null; _late = r; _lateWritesLeft = 2;
            return;
        }
        _out.Enqueue(r);
    }

    /// <summary>Fault: the reply to the first memory read of (sector, offset) arrives late, during a later request.</summary>
    public (ushort Sector, ushort Offset)? LateReply { get; set; }
    byte[]? _late;
    int _lateWritesLeft;

    /// <summary>Fault: every memory read of this sector returns slightly different, wrong data.</summary>
    public ushort? FlakySector { get; set; }
    int _flakyCount;

    sealed class FakeError : Exception { public byte Code; public FakeError(byte c) { Code = c; } }

    static ushort U16(byte[] p, int o) => (ushort)((p[o] << 8) | p[o + 1]);

    byte[] Handle(byte idx, int fn, byte[] p)
    {
        var o = new byte[16];
        switch (idx)
        {
            case 0:
                if (fn == 0) { ushort f = U16(p, 0); o[0] = FeatureTable.TryGetValue(f, out var i) ? i : (byte)0; return o; }
                if (fn == 1) { o[0] = 4; o[1] = 2; o[2] = p[2]; return o; }
                break;
            case IdxInfo:
                if (fn == 0) { o[0] = 1; return o; }
                if (fn == 1) { o[0] = 0; o[1] = (byte)'U'; o[2] = (byte)'1'; o[3] = (byte)'2'; o[4] = 0x46; o[5] = 0x00; o[6] = 0x00; o[7] = 0x11; return o; }
                break;
            case IdxName:
                const string name = "G502 HERO Gaming Mouse";
                if (fn == 0) { o[0] = (byte)name.Length; return o; }
                if (fn == 1) { for (int i = 0; i < 16 && p[0] + i < name.Length; i++) o[i] = (byte)name[p[0] + i]; return o; }
                break;
            case IdxDpi:
                if (fn == 1) { o[0] = 0; o[1] = 0x00; o[2] = 0x64; o[3] = 0xE0; o[4] = 0x32; o[5] = 0x64; o[6] = 0x00; return o; }
                if (fn == 2) { o[0] = 0; o[1] = (byte)(SensorDpi >> 8); o[2] = (byte)SensorDpi; o[3] = 0x03; o[4] = 0x20; return o; }
                if (fn == 3)
                {
                    int dpi = U16(p, 1);
                    if (dpi < 100 || dpi > 25600) throw new FakeError(0x02);
                    SensorDpi = dpi; o[0] = 0; o[1] = p[1]; o[2] = p[2]; return o;
                }
                break;
            case IdxRate:
                if (fn == 0) { o[0] = 0x8B; return o; } // 1, 2, 4, 8 ms
                if (fn == 1) { o[0] = (byte)IntervalMs; return o; }
                break;
            case IdxProfiles:
                switch (fn)
                {
                    case 0:
                        o[0] = 1; o[1] = 3; o[2] = 1; o[3] = (byte)ProfileCount; o[4] = (byte)RomProfiles; o[5] = (byte)ButtonCount;
                        o[6] = 16; o[7] = (byte)(SectorSize >> 8); o[8] = (byte)SectorSize; o[9] = 0x0A; o[10] = 1; return o;
                    case 1: Mode = (OnboardMode)p[0]; return o;
                    case 2: o[0] = (byte)Mode; return o;
                    case 3:
                        {
                            ushort s = U16(p, 0);
                            var dir = new ProfileDirectory(Memory[0]);
                            if (!dir.CrcValid || !dir.Entries.Any(e => e.Sector == s && e.Enabled)) throw new FakeError(0x02);
                            CurrentProfile = s; LoadProfile(defaultIndex: true); return o;
                        }
                    case 4: o[0] = (byte)(CurrentProfile >> 8); o[1] = (byte)CurrentProfile; return o;
                    case 5:
                        {
                            ushort s = U16(p, 0), off = U16(p, 2);
                            if (!Memory.TryGetValue(s, out var m)) m = Fill(0xFF);
                            if (off > SectorSize - 16) throw new FakeError(0x02);
                            Array.Copy(m, off, o, 0, 16);
                            if (s == FlakySector) o[0] ^= (byte)++_flakyCount; // a different wrong value on every read
                            return o;
                        }
                    case 6:
                        _writeSector = U16(p, 0); _writeOffset = U16(p, 2); _writeCount = U16(p, 4);
                        if ((_writeSector & 0xFF00) != 0 || _writeCount > SectorSize) throw new FakeError(0x02);
                        _writeBuffer = new byte[(_writeCount + 15) / 16 * 16]; _pos = 0; return o;
                    case 7:
                        if (_writeBuffer == null || _pos >= _writeBuffer.Length) throw new FakeError(0x02);
                        Array.Copy(p, 0, _writeBuffer, _pos, 16); _pos += 16; return o;
                    case 8:
                        {
                            if (_writeBuffer == null) throw new FakeError(0x02);
                            var m = Memory.TryGetValue(_writeSector, out var e) ? e : Fill(0xFF);
                            Array.Copy(_writeBuffer, 0, m, _writeOffset, _writeCount);
                            if (CorruptNextSectorWrite) { m[10] ^= 0xFF; CorruptNextSectorWrite = false; }
                            Memory[_writeSector] = m; _writeBuffer = null; SectorWrites++;
                            o[0] = 0; return o;
                        }
                    case 11: o[0] = (byte)DpiIndex; return o;
                    case 12:
                        if (p[0] > 4) throw new FakeError(0x02);
                        DpiIndex = p[0]; SensorDpi = Active.GetDpi(DpiIndex); return o;
                }
                break;
        }
        throw new FakeError(0x07);
    }

    int _pos;

    public byte[]? Read(int timeoutMs)
    {
        if (Unplugged) throw new DeviceDisconnectedException("unplugged");
        return _out.Count > 0 ? _out.Dequeue() : null;
    }

    public void DrainInput() => _out.Clear();
    public void Dispose() { }
}
