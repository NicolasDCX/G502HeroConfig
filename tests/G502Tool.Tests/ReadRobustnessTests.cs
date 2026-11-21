using G502Tool.Core;
using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;
using Xunit;

namespace G502Tool.Tests;

/// <summary>
/// Reproduces the shifted factory profile seen on a real G502 HERO: a 16-byte memory-read reply that arrives
/// late was taken as the reply to the next read, so button slots 5-8 showed slots 1-4 again.
/// </summary>
public class ReadRobustnessTests
{
    const ushort RomSector = 0x0101;
    const ushort ButtonsOffset = 32; // slots 0-3; the next chunk (48) holds slots 4-7

    static (FakeG502 fake, HidppDevice dev, G502Session session) Open(bool factory = true)
    {
        var fake = new FakeG502(factory);
        var dev = new HidppDevice(fake) { TimeoutMs = 50 };
        var s = new G502Session(dev);
        s.ReadSnapshot("G502 HERO"); // looks up the features
        return (fake, dev, s);
    }

    [Fact]
    public void Late_reply_is_not_taken_for_the_next_chunk()
    {
        var (fake, dev, _) = Open();
        fake.LateReply = (RomSector, ButtonsOffset);
        var data = OnboardProfilesFeature.ReadSector(dev, RomSector, fake.SectorSize);
        Assert.Equal(fake.Memory[RomSector], data);
    }

    [Fact]
    public void Factory_buttons_are_correct_despite_a_late_reply()
    {
        var (fake, _, session) = Open();
        fake.LateReply = (RomSector, ButtonsOffset);
        var snap = session.ReadSnapshot("G502 HERO");
        Assert.Equal(ButtonBinding.Mouse(5), snap.Factory!.GetButton(4));  // Forward, not "Left click" again
        Assert.Equal(ButtonBinding.Special(0x07), snap.Factory.GetButton(5));
        Assert.Equal(fake.Memory[RomSector], snap.Factory.Bytes);
    }

    [Fact]
    public void Inconsistent_reads_stop_with_an_error_instead_of_using_mixed_data()
    {
        var (fake, _, session) = Open();
        fake.FlakySector = RomSector;
        var e = Assert.Throws<HidppException>(() => session.ReadSnapshot("G502 HERO"));
        Assert.Contains("G HUB", e.Message);
    }

    [Fact]
    public void Stable_sector_without_valid_crc_is_accepted_and_flagged()
    {
        var (fake, dev, _) = Open();
        var (bytes, crcValid) = OnboardProfilesFeature.ReadSectorChecked(dev, 0x0003, fake.SectorSize); // never written: all FF
        Assert.False(crcValid);
        Assert.All(bytes, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Valid_crc_sector_is_read_once()
    {
        var (fake, dev, _) = Open();
        var (bytes, crcValid) = OnboardProfilesFeature.ReadSectorChecked(dev, RomSector, fake.SectorSize);
        Assert.True(crcValid);
        Assert.Equal(fake.Memory[RomSector], bytes);
    }
}
