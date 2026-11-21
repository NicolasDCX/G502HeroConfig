namespace G502Tool.Core.Model;

public enum MouseView { Top, Side }

/// <summary>Where a programmable button sits on the G502 HERO, for the pictures in the Buttons section.</summary>
/// <param name="Name">Short physical name, e.g. "G4 (thumb, rear)".</param>
/// <param name="View">Which picture shows it.</param>
/// <param name="X">Marker centre in that picture's pixels (see <see cref="ButtonLocations.TopSize"/> / <see cref="ButtonLocations.SideSize"/>).</param>
/// <param name="Y">Marker centre in that picture's pixels.</param>
public sealed record ButtonLocation(string Name, MouseView View, double X, double Y);

/// <summary>
/// Maps a button slot to its physical button by the slot's <b>factory</b> binding (read from the mouse's ROM
/// profile), not by slot index: the slot order is not documented, but each G502 HERO button has a unique
/// factory function. Returns null when the factory binding is unknown, so the UI never points at a wrong button.
/// </summary>
public static class ButtonLocations
{
    /// <summary>Pixel sizes of Images\g502-top.png and Images\g502-side.png; the coordinates below refer to them.</summary>
    public static readonly (int W, int H) TopSize = (496, 908), SideSize = (918, 350);

    public static ButtonLocation? ForFactory(ButtonBinding? factory)
    {
        if (factory == null) return null;
        switch (factory.Kind)
        {
            case BindingKind.MouseButton:
                return factory.MouseButtonNumber switch
                {
                    1 => new("Left button", MouseView.Top, 182, 150),
                    2 => new("Right button", MouseView.Top, 382, 150),
                    3 => new("Wheel click", MouseView.Top, 283, 212),
                    4 => new("G4 (thumb, rear)", MouseView.Side, 590, 145),
                    5 => new("G5 (thumb, front)", MouseView.Side, 410, 165),
                    _ => null,
                };
            case BindingKind.Special:
                return factory.SpecialCode switch
                {
                    0x01 => new("Wheel tilt left", MouseView.Top, 208, 262),
                    0x02 => new("Wheel tilt right", MouseView.Top, 358, 262),
                    0x03 => new("G8 (next to left button, front)", MouseView.Top, 100, 222),
                    0x04 => new("G7 (next to left button, rear)", MouseView.Top, 92, 284),
                    0x07 => new("G6 (sniper, thumb)", MouseView.Side, 385, 245),
                    0x0A => new("G9 (behind the wheel)", MouseView.Top, 288, 438),
                    _ => null,
                };
            default:
                return null;
        }
    }
}
