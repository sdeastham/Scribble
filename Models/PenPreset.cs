using System.Windows.Media;

namespace Scribble.Models;

public sealed record PenPreset(string Name, Color Color, double BaseWidth = 2.6)
{
    public static IReadOnlyList<PenPreset> Defaults { get; } =
    [
        new PenPreset("Black", Color.FromRgb(0x16, 0x18, 0x1D)),
        new PenPreset("Blue",  Color.FromRgb(0x1B, 0x5E, 0xD7)),
        new PenPreset("Red",   Color.FromRgb(0xD9, 0x2B, 0x2B)),
        new PenPreset("Green", Color.FromRgb(0x17, 0x8A, 0x45)),
    ];
}
