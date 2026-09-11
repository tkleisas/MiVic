using MiVic.Core.Sim;

namespace MiVic.Map;

/// <summary>A scene and the reading of it, together: what to paint, and what was in it.</summary>
public sealed record MapDrawing(MapScene Scene, MapCensus Census);

/// <summary>
/// Writing a picture of a match to disk, which is the whole of this tool's interface.
/// <para>
/// Two files are written for one command, and that is on purpose. The SVG is the primary format
/// — exact, diffable, a plain text writer — and the PNG is the same scene through the raster
/// encoder, because a picture whose only form is a format the reader cannot open is a picture
/// nobody read. Writing both from one call means they cannot drift: there is one scene, and the
/// two writers agree about it by construction rather than by discipline.
/// </para>
/// </summary>
public sealed record MapPictureResult(
    MapCensus Census,
    string SvgPath,
    string PngPath,
    long SvgBytes,
    long PngBytes);

/// <summary>Draws a match and writes it out.</summary>
public static class MapPicture
{
    /// <summary>
    /// Builds the scene, writes the SVG at <paramref name="svgPath"/> and the PNG beside it with
    /// the same stem, and returns what was drawn.
    /// </summary>
    public static MapPictureResult Write(
        SimWorld world,
        MapTrails trails,
        MapPalette palette,
        MapRequest request,
        IReadOnlyList<MapEventMark> events,
        string svgPath)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(svgPath);

        MapDrawing drawing = MapSceneBuilder.Build(world, trails, palette, request, events);

        string svg = Path.GetFullPath(svgPath);
        string png = Path.ChangeExtension(svg, ".png");

        SvgMapWriter.Write(drawing.Scene, svg);
        PngMapWriter.Write(RasterCanvas.Paint(drawing.Scene), png);

        return new MapPictureResult(
            drawing.Census,
            svg,
            png,
            new FileInfo(svg).Length,
            new FileInfo(png).Length);
    }
}
