namespace MiVic.Map;

/// <summary>
/// One colour, as a picture needs it: three bytes and nothing else.
/// <para>
/// Not <c>Microsoft.Xna.Framework.Color</c>, and the reason is the whole point of this project.
/// The map is drawn from simulation data rather than by the renderer, so it has to work where
/// there is no graphics device — in a test host and on a machine with no GPU. A type from the
/// framework the renderer uses would tie the writer to the renderer for the sake of a struct
/// with three bytes in it.
/// </para>
/// <para>
/// Written out rather than declared as a positional record, and this one is not a matter of
/// taste: a record's generated <c>PrintMembers</c> walks every readable property, so a record
/// with a computed colour on it prints itself by printing itself.
/// </para>
/// </summary>
public readonly struct MapRgb : IEquatable<MapRgb>
{
    public MapRgb(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Red.</summary>
    public byte R { get; }

    /// <summary>Green.</summary>
    public byte G { get; }

    /// <summary>Blue.</summary>
    public byte B { get; }

    /// <summary>Clamps three channels, so a shade that overshoots cannot wrap to black.</summary>
    public static MapRgb FromChannels(int r, int g, int b) => new(Clamp(r), Clamp(g), Clamp(b));

    /// <summary>Scales a colour by permille, the way the palette's own shades are scaled.</summary>
    public MapRgb Shade(int permille) => FromChannels(
        (R * permille) / 1_000,
        (G * permille) / 1_000,
        (B * permille) / 1_000);

    /// <summary>Moves towards another colour by permille of the way. Used to lighten a mark and relief.</summary>
    public MapRgb Towards(MapRgb other, int permille) => FromChannels(
        R + (((other.R - R) * permille) / 1_000),
        G + (((other.G - G) * permille) / 1_000),
        B + (((other.B - B) * permille) / 1_000));

    /// <summary>The form SVG wants and a diff can read: <c>#rrggbb</c>.</summary>
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public bool Equals(MapRgb other) => R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is MapRgb other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B);

    public override string ToString() => Hex;

    public static bool operator ==(MapRgb left, MapRgb right) => left.Equals(right);

    public static bool operator !=(MapRgb left, MapRgb right) => !left.Equals(right);

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
