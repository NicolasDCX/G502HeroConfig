using G502Tool.Core;
using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;
using Xunit;

namespace G502Tool.Tests;

public class SessionTests
{
    static (FakeG502 fake, G502Session session, DeviceSnapshot snap) Open(bool factory = false, int sectorSize = 255)
    {
        var fake = new FakeG502(factory, sectorSize);
        var s = new G502Session(new HidppDevice(fake) { TimeoutMs = 50 });
        return (fake, s, s.ReadSnapshot("G502 HERO"));
    }

    [Fact]
    public void Crc_matches_known_vector()
    {
        // CRC-16/CCITT-FALSE("123456789") = 0x29B1 (same algorithm as libratbag hidpp_crc_ccitt)
        Assert.Equal(0x29B1, Crc.Ccitt("123456789"u8));
    }

    [Fact]
    public void Dpi_list_parses_range()
    {
        var caps = DpiCapabilities.Parse(new byte[] { 0, 0x00, 0x64, 0xE0, 0x32, 0x64, 0x00, 0, 0 });
        Assert.Equal(100, caps.Min);
        Assert.Equal(25600, caps.Max);
        Assert.True(caps.IsValid(1650));
        Assert.False(caps.IsValid(1625));
        Assert.False(caps.IsValid(25650));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void Snapshot_reads_profiles_and_live_state(int size)
    {
        var (_, _, snap) = Open(sectorSize: size);
        Assert.Equal("G502 HERO Gaming Mouse", snap.DeviceName);
        Assert.Equal(5, snap.Profiles.Count);
        Assert.True(snap.DirectoryValid);
        Assert.Equal(0x0001, snap.ActiveSector);
        Assert.Equal(400, snap.Profiles[0].Sector.GetDpi(0));
        Assert.Equal(800, snap.LiveDpi); // default index 1
        Assert.Equal(new[] { 1, 2, 4, 8 }, snap.SupportedIntervalsMs);
        Assert.False(snap.Profiles[0].FromFactory);
        Assert.True(snap.Profiles[2].Enabled);
        Assert.False(snap.Profiles[3].Enabled);
    }

    [Fact]
    public void Save_dpi_stage_round_trips_and_preserves_everything_else()
    {
        var (fake, s, snap) = Open();
        var before = (byte[])fake.Memory[1].Clone();
        var others = Enumerable.Range(2, 4).ToDictionary(i => i, i => (byte[])fake.Memory[(ushort)i].Clone());

        var edit = new ProfileEdit();
        edit.Dpi[1] = 1250; // active stage (default index 1)
        var r = s.SaveProfile(snap, 0, edit, allowFactoryCopy: false);

        Assert.True(r.Success, string.Join("\n", r.Checks));
        Assert.Contains(r.Checks, c => c.What == "Live DPI" && c.Status == CheckStatus.Verified);
        Assert.Equal(1250, fake.SensorDpi);

        var after = fake.Memory[1];
        var diff = ProfileSector.Diff(before, after);
        Assert.NotEmpty(diff);
        Assert.All(diff, o => Assert.Contains(o, new[] { 5, 6, after.Length - 2, after.Length - 1 })); // DPI stage 2 bytes + CRC only
        Assert.True(new ProfileSector(after).CrcValid);
        foreach (var (i, b) in others) Assert.Equal(b, fake.Memory[(ushort)i]); // other profiles untouched
        Assert.Equal(1, fake.SectorWrites);

        // reconnect: value persists in "flash"
        fake.Replug();
        var snap2 = s.ReadSnapshot("x");
        Assert.Equal(1250, snap2.Profiles[0].Sector.GetDpi(1));
        Assert.Equal(1250, snap2.LiveDpi);
    }

    [Fact]
    public void Invalid_dpi_is_rejected_before_writing()
    {
        var (fake, s, snap) = Open();
        var edit = new ProfileEdit();
        edit.Dpi[0] = 1234;
        Assert.Throws<ArgumentException>(() => s.SaveProfile(snap, 0, edit, false));
        Assert.Equal(0, fake.SectorWrites);
    }

    [Fact]
    public void Unused_stage_cannot_be_written()
    {
        var (fake, s, snap) = Open();
        var edit = new ProfileEdit();
        edit.Dpi[4] = 1000; // stage 5 is 0 = unused in the fake's user profile
        Assert.Throws<ArgumentException>(() => s.SaveProfile(snap, 0, edit, false));
        Assert.Equal(0, fake.SectorWrites);
    }

    [Fact]
    public void Corrupted_write_is_reported_as_failure()
    {
        var (fake, s, snap) = Open();
        fake.CorruptNextSectorWrite = true;
        var edit = new ProfileEdit();
        edit.Dpi[0] = 500;
        var r = s.SaveProfile(snap, 0, edit, false);
        Assert.False(r.Success);
        Assert.Contains(r.Checks, c => c.Status == CheckStatus.Failed && c.What.Contains("onboard memory"));
    }

    [Fact]
    public void Concurrent_change_aborts_without_writing()
    {
        var (fake, s, snap) = Open();
        var m = fake.Memory[1];
        var sec = new ProfileSector(m);
        sec.SetDpi(0, 999 / 50 * 50);
        sec.UpdateCrc();
        var edit = new ProfileEdit();
        edit.Dpi[0] = 500;
        Assert.Throws<ConcurrentChangeException>(() => s.SaveProfile(snap, 0, edit, false));
        Assert.Equal(0, fake.SectorWrites);
    }

    [Fact]
    public void Host_mode_saves_but_does_not_claim_live_success()
    {
        var (fake, s, _) = Open();
        fake.Mode = OnboardMode.Host;
        var snap = s.ReadSnapshot("x");
        var edit = new ProfileEdit();
        edit.Dpi[1] = 900;
        var r = s.SaveProfile(snap, 0, edit, false);
        Assert.Contains(r.Checks, c => c.Status == CheckStatus.Verified && c.What.Contains("onboard memory"));
        Assert.Contains(r.Checks, c => c.Status == CheckStatus.NotChecked && c.What == "Live settings");
    }

    [Fact]
    public void Factory_state_requires_confirmation_then_creates_user_profiles()
    {
        var (fake, s, snap) = Open(factory: true);
        Assert.False(snap.DirectoryValid);
        Assert.True(snap.ActiveIsFactory);
        Assert.True(snap.Profiles[0].FromFactory);

        var edit = new ProfileEdit();
        edit.Dpi[1] = 1000;
        Assert.Throws<FactoryCopyRequiredException>(() => s.SaveProfile(snap, 0, edit, false));
        Assert.Equal(0, fake.SectorWrites);

        var r = s.SaveProfile(snap, 0, edit, true);
        Assert.True(r.Success, string.Join("\n", r.Checks));
        Assert.Equal(0x0001, fake.CurrentProfile);
        Assert.Equal(1000, fake.SensorDpi);
        var dir = new ProfileDirectory(fake.Memory[0]);
        Assert.True(dir.CrcValid);
        Assert.Equal(5, dir.Entries.Count);
        Assert.Equal(fake.Memory[0x0101], fake.Memory[2]); // other profiles are exact factory copies
    }

    [Fact]
    public void Temporary_dpi_is_read_back_and_not_persisted()
    {
        var (fake, s, _) = Open();
        var r = s.ApplyTemporaryDpi(2550);
        Assert.True(r.Success);
        Assert.Equal(2550, fake.SensorDpi);
        Assert.Equal(0, fake.SectorWrites);
        fake.Replug();
        Assert.Equal(800, fake.SensorDpi);
    }

    [Fact]
    public void Thumb_button_remap_and_polling_rate_and_led()
    {
        var (fake, s, snap) = Open();
        var macroBefore = new ProfileSector(fake.Memory[1]).GetButton(8);
        var edit = new ProfileEdit();
        edit.Buttons[3] = ButtonBinding.Key(HidKeys.Ctrl, 0x06); // Ctrl+C
        edit.Buttons[4] = ButtonBinding.Mouse(4);
        edit.ReportIntervalMs = 2;
        edit.Leds[0] = Lighting.Off();
        var r = s.SaveProfile(snap, 0, edit, false);
        Assert.True(r.Success, string.Join("\n", r.Checks));
        var p = new ProfileSector(fake.Memory[1]);
        Assert.Equal("Ctrl+C", p.GetButton(3).Describe());
        Assert.Equal("Back", p.GetButton(4).Describe());
        Assert.Equal(macroBefore, p.GetButton(8));
        Assert.Equal(0x80, p.Bytes[96]); // G-shift binding untouched
        Assert.Equal(0x5A, p.Bytes[20]); // unknown byte untouched
        Assert.Equal(2, fake.IntervalMs);
        Assert.Equal("Off", Lighting.Describe(p.GetLed(0)));
    }

    [Fact]
    public void Primary_button_is_protected()
    {
        var (fake, s, snap) = Open();
        var edit = new ProfileEdit();
        edit.Buttons[0] = ButtonBinding.Disabled();
        Assert.Throws<ArgumentException>(() => s.SaveProfile(snap, 0, edit, false));
        Assert.Equal(0, fake.SectorWrites);
    }

    [Fact]
    public void Activate_profile_reads_back()
    {
        var (fake, s, snap) = Open();
        var r = s.ActivateProfile(snap, 2);
        Assert.True(r.Success);
        Assert.Equal(3, fake.CurrentProfile);
        var r2 = s.ActivateProfile(snap, 4); // disabled
        Assert.False(r2.Success);
    }

    [Fact]
    public void Unplug_surfaces_as_disconnect()
    {
        var (fake, s, _) = Open();
        fake.Unplugged = true;
        Assert.Throws<DeviceDisconnectedException>(() => s.ReadSnapshot("x"));
    }

    [Fact]
    public void Device_error_is_decoded()
    {
        var (_, s, _) = Open();
        var ex = Assert.Throws<HidppException>(() => AdjustableDpiFeature.SetDpi(s.Device, 50));
        Assert.Contains("invalid argument", ex.Message);
    }
}
