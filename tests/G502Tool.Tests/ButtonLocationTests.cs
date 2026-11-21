using G502Tool.Core;
using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;
using Xunit;

namespace G502Tool.Tests;

public class ButtonLocationTests
{
    [Fact]
    public void Every_factory_button_maps_to_a_distinct_physical_button()
    {
        var s = new G502Session(new HidppDevice(new FakeG502(factoryState: true)) { TimeoutMs = 50 });
        var snap = s.ReadSnapshot("G502 HERO");
        Assert.NotNull(snap.Factory);
        var names = new HashSet<string>();
        for (int i = 0; i < snap.ButtonCount; i++)
        {
            var loc = ButtonLocations.ForFactory(snap.Factory!.GetButton(i));
            Assert.NotNull(loc);
            Assert.True(names.Add(loc!.Name), $"two slots map to {loc.Name}");
        }
        Assert.Equal(11, names.Count);
    }

    [Fact]
    public void Known_buttons_point_at_the_right_place()
    {
        Assert.StartsWith("G4", ButtonLocations.ForFactory(ButtonBinding.Mouse(4))!.Name);
        Assert.StartsWith("G5", ButtonLocations.ForFactory(ButtonBinding.Mouse(5))!.Name);
        Assert.StartsWith("G8", ButtonLocations.ForFactory(ButtonBinding.Special(0x03))!.Name);
        Assert.StartsWith("G7", ButtonLocations.ForFactory(ButtonBinding.Special(0x04))!.Name);
        Assert.Equal(MouseView.Side, ButtonLocations.ForFactory(ButtonBinding.Special(0x07))!.View);
    }

    [Fact]
    public void Unknown_or_missing_factory_binding_gives_no_location()
    {
        Assert.Null(ButtonLocations.ForFactory(null));
        Assert.Null(ButtonLocations.ForFactory(ButtonBinding.Disabled()));
        Assert.Null(ButtonLocations.ForFactory(ButtonBinding.Key(0, 0x04)));
        Assert.Null(ButtonLocations.ForFactory(ButtonBinding.Special(0x0B)));
    }
}
