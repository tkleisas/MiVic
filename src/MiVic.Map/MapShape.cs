namespace MiVic.Map;

/// <summary>
/// One thing to paint, in device pixels.
/// <para>
/// The scene is built in millimetres and projected to pixels once, by the builder, so both
/// writers rasterise the same numbers in the same order. A picture is therefore a pure
/// function of the scene, and the SVG and the PNG cannot disagree about where a unit is:
/// they are the same list of shapes through two encoders.
/// </para>
/// <para>
/// Alpha is a byte on every shape rather than a colour with an alpha channel, because the
/// two formats want it in different places — SVG has <c>fill-opacity</c> beside a colour,
/// a raster has four channels per pixel — and one field is the only way both get it right.
/// </para>
/// </summary>
public abstract record MapShape;

/// <summary>A filled rectangle. Terrain cells and swatches are rectangles.</summary>
public sealed record MapRect(double X, double Y, double W, double H, MapRgb Colour, byte Alpha = 255) : MapShape;

/// <summary>A disc, filled or as a ring. Coverage, dots, sample ticks and rings are discs.</summary>
/// <param name="Thickness">Ring width in pixels; ignored when <paramref name="Hollow"/> is false.</param>
public sealed record MapDisc(
    double X,
    double Y,
    double R,
    MapRgb Colour,
    byte Alpha = 255,
    bool Hollow = false,
    double Thickness = 1.0) : MapShape;

/// <summary>A single straight mark: a heading arrow, a tick, an event cross.</summary>
public sealed record MapLine(
    double X0,
    double Y0,
    double X1,
    double Y1,
    double Thickness,
    MapRgb Colour,
    byte Alpha = 255,
    bool Dashed = false) : MapShape;

/// <summary>A run of connected points: an intended route, or a trail.</summary>
/// <param name="Points">Flat x,y pairs. Fewer than two pairs draws nothing.</param>
public sealed record MapPolyline(
    IReadOnlyList<double> Points,
    double Thickness,
    MapRgb Colour,
    byte Alpha = 255,
    bool Dashed = false) : MapShape;

/// <summary>
/// A scatter of marks of one size and colour: the sampled positions a trail is drawn from.
/// <para>
/// A shape of its own rather than a list of discs, and it is not tidiness. A trail is sixty-four
/// marks per unit and a match is five hundred units, so the whole layer is one shape here and
/// thirty thousand of them if each mark is its own — which in SVG is thirty thousand elements and
/// three megabytes of text for one layer of one picture. One path of zero-length stroked segments
/// draws the same dots exactly, because a round cap on a zero-length segment is a disc.
/// </para>
/// </summary>
/// <param name="Points">Flat x,y pairs.</param>
/// <param name="Radius">Radius of each mark in pixels.</param>
public sealed record MapDots(
    IReadOnlyList<double> Points,
    double Radius,
    MapRgb Colour,
    byte Alpha = 255) : MapShape;

/// <summary>A word. The SVG uses a real font; the raster uses the writer's own 5x7 face.</summary>
/// <param name="X">Left edge of the first glyph.</param>
/// <param name="Y">Baseline of the text.</param>
/// <param name="Size">Nominal glyph height in pixels, from which both writers pick their face.</param>
public sealed record MapText(double X, double Y, double Size, string Text, MapRgb Colour, byte Alpha = 255) : MapShape;

/// <summary>
/// A named group of shapes: one layer, in the order it must be painted.
/// <para>
/// The name is what makes the SVG navigable — a reader can turn a layer off in a viewer — and
/// what lets a test ask for "the trails" rather than for "the sixteenth polyline". The raster
/// ignores it, because by then the order is the only thing left that matters.
/// </para>
/// </summary>
public sealed record MapGroup(string Name, IReadOnlyList<MapShape> Shapes);

/// <summary>
/// A picture ready to be written: a canvas size, a background, and the layers in paint order.
/// <para>
/// Everything here is already in pixels. The scene knows nothing about the world it came from,
/// which is what makes a determinism check on it meaningful and a test on it cheap.
/// </para>
/// </summary>
/// <param name="Width">Canvas width in pixels.</param>
/// <param name="Height">Canvas height in pixels.</param>
/// <param name="Background">Painted before every group.</param>
/// <param name="Groups">Layers, bottom first. A group with no shapes is still listed.</param>
/// <param name="Title">One line drawn in the frame, and written into the SVG's metadata.</param>
public sealed record MapScene(
    int Width,
    int Height,
    MapRgb Background,
    IReadOnlyList<MapGroup> Groups,
    string Title)
{
    /// <summary>Every shape in the scene, in paint order.</summary>
    public IEnumerable<MapShape> Shapes => Groups.SelectMany(group => group.Shapes);

    /// <summary>The shapes of one named group, or nothing when the scene has no such layer.</summary>
    public IReadOnlyList<MapShape> Group(string name)
        => Groups.FirstOrDefault(group => group.Name == name)?.Shapes ?? [];
}
