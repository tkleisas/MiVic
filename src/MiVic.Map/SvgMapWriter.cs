using System.Globalization;
using System.Text;

namespace MiVic.Map;

/// <summary>
/// The map as SVG: text, diffable, exact, and written by a plain string builder.
/// <para>
/// This is the primary format for a reason that has nothing to do with how it looks. A picture
/// of a simulation should be checkable the way the simulation is: two runs of the same world
/// produce two files that <c>diff</c> clean, a change to a layer shows up as a change to a group
/// of lines and not as a different arrangement of bytes, and a reader who wants to know where a
/// unit actually was can read the number instead of measuring a pixel.
/// </para>
/// <para>
/// Every number is written in the invariant culture and rounded to a tenth of a pixel. The
/// rounding is not a shortcut: the lattice is 9.4 metres a cell and a tenth of a pixel is seven
/// centimetres at two pixels a metre, so nothing a reader can act on is lost, and what is gained
/// is that a coordinate is a short stable string rather than seventeen digits of a division.
/// </para>
/// </summary>
public static class SvgMapWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Renders a scene to an SVG document.</summary>
    public static string Render(MapScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var svg = new StringBuilder(4_096 + (scene.Shapes.Count() * 96));

        svg.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        svg.Append(Invariant,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{scene.Width}\" height=\"{scene.Height}\" " +
            $"viewBox=\"0 0 {scene.Width} {scene.Height}\">\n");

        svg.Append("  <title>").Append(Escape(scene.Title)).Append("</title>\n");
        svg.Append("  <desc>Drawn from simulation state by MiVic.Map. One group per layer, in paint order.</desc>\n");
        svg.Append(Invariant,
            $"  <rect x=\"0\" y=\"0\" width=\"{scene.Width}\" height=\"{scene.Height}\" fill=\"{scene.Background.Hex}\"/>\n");

        foreach (MapGroup group in scene.Groups)
        {
            // The terrain is a lattice of abutting rectangles, and a renderer that antialiases
            // their shared edges draws a grid of hairlines over ground that has no seams in the
            // world. Crisp edges are the honest rendering of a cell, not a cosmetic preference.
            string crisp = group.Name == "terrain" ? " shape-rendering=\"crispEdges\"" : string.Empty;

            svg.Append(Invariant, $"  <g id=\"{Escape(group.Name)}\"{crisp}>\n");
            svg.Append(Invariant, $"    <!-- {group.Shapes.Count} shapes -->\n");

            foreach (MapShape shape in group.Shapes)
            {
                Write(svg, shape);
            }

            svg.Append("  </g>\n");
        }

        svg.Append("</svg>\n");

        return svg.ToString();
    }

    /// <summary>Renders a scene and writes it to a file, creating the directory if it is missing.</summary>
    public static void Write(MapScene scene, string path)
    {
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A line ending that does not depend on the platform: two machines drawing the same world
        // must produce the same bytes, and a line ending is bytes like any other.
        File.WriteAllText(full, Render(scene), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Write(StringBuilder svg, MapShape shape)
    {
        switch (shape)
        {
            case MapRect rect:
                svg.Append(Invariant,
                    $"    <rect x=\"{Num(rect.X)}\" y=\"{Num(rect.Y)}\" width=\"{Num(rect.W)}\" " +
                    $"height=\"{Num(rect.H)}\" fill=\"{rect.Colour.Hex}\"{Opacity("fill", rect.Alpha)}/>\n");
                break;

            case MapDisc disc:
                svg.Append(Invariant, $"    <circle cx=\"{Num(disc.X)}\" cy=\"{Num(disc.Y)}\" r=\"{Num(disc.R)}\" ");

                if (disc.Hollow)
                {
                    svg.Append(Invariant,
                        $"fill=\"none\" stroke=\"{disc.Colour.Hex}\" stroke-width=\"{Num(disc.Thickness)}\"" +
                        $"{Opacity("stroke", disc.Alpha)}/>\n");
                }
                else
                {
                    svg.Append(Invariant, $"fill=\"{disc.Colour.Hex}\"{Opacity("fill", disc.Alpha)}/>\n");
                }

                break;

            case MapLine line:
                svg.Append(Invariant,
                    $"    <line x1=\"{Num(line.X0)}\" y1=\"{Num(line.Y0)}\" x2=\"{Num(line.X1)}\" y2=\"{Num(line.Y1)}\" " +
                    $"stroke=\"{line.Colour.Hex}\" stroke-width=\"{Num(line.Thickness)}\" stroke-linecap=\"round\"" +
                    $"{Opacity("stroke", line.Alpha)}{Dash(line.Dashed)}/>\n");
                break;

            case MapPolyline polyline when polyline.Points.Count >= 4:
                svg.Append("    <polyline points=\"");

                for (int i = 0; i < polyline.Points.Count; i += 2)
                {
                    if (i > 0)
                    {
                        svg.Append(' ');
                    }

                    svg.Append(Invariant, $"{Num(polyline.Points[i])},{Num(polyline.Points[i + 1])}");
                }

                svg.Append(Invariant,
                    $"\" fill=\"none\" stroke=\"{polyline.Colour.Hex}\" stroke-width=\"{Num(polyline.Thickness)}\" " +
                    $"stroke-linecap=\"round\" stroke-linejoin=\"round\"" +
                    $"{Opacity("stroke", polyline.Alpha)}{Dash(polyline.Dashed)}/>\n");
                break;

            case MapDots dots when dots.Points.Count >= 2:
                // A zero-length stroked segment with a round cap is a disc, so a scatter of marks
                // is one path: `M x,y h0` per point. Thirty thousand <circle> elements would be
                // three megabytes of the same picture.
                svg.Append(Invariant,
                    $"    <path d=\"M{Num(dots.Points[0])},{Num(dots.Points[1])}h0");

                for (int i = 2; i < dots.Points.Count; i += 2)
                {
                    svg.Append(Invariant, $"M{Num(dots.Points[i])},{Num(dots.Points[i + 1])}h0");
                }

                svg.Append(Invariant,
                    $"\" fill=\"none\" stroke=\"{dots.Colour.Hex}\" stroke-width=\"{Num(dots.Radius * 2)}\" " +
                    $"stroke-linecap=\"round\"{Opacity("stroke", dots.Alpha)}/>\n");
                break;

            case MapText text:
                svg.Append(Invariant,
                    $"    <text x=\"{Num(text.X)}\" y=\"{Num(text.Y)}\" font-family=\"Consolas,'DejaVu Sans Mono',monospace\" " +
                    $"font-size=\"{Num(text.Size)}\" fill=\"{text.Colour.Hex}\"{Opacity("fill", text.Alpha)}>" +
                    $"{Escape(text.Text)}</text>\n");
                break;
        }
    }

    /// <summary>A tenth of a pixel, with no trailing zero: <c>14</c>, <c>14.5</c>, <c>1218.8</c>.</summary>
    private static string Num(double value)
        => Math.Round(value, 1, MidpointRounding.AwayFromZero)
            .ToString("0.#", Invariant);

    private static string Opacity(string attribute, byte alpha)
        => alpha >= 255
            ? string.Empty
            : string.Create(Invariant, $" {attribute}-opacity=\"{alpha / 255.0:0.###}\"");

    private static string Dash(bool dashed) => dashed ? " stroke-dasharray=\"5 3\"" : string.Empty;

    private static string Escape(string text)
    {
        if (text.IndexOfAny(['&', '<', '>', '"']) < 0)
        {
            return text;
        }

        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}
