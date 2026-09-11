namespace MiVic.Map;

/// <summary>
/// A picture being painted: four bytes a pixel, no graphics device, no window, no framework.
/// <para>
/// Deliberately not a renderer. There is no depth, no transform, no blending mode, and no
/// antialiasing — the marks on this map are one to nine pixels across and a blur on them costs
/// more legibility than it buys. What there is instead is a rasteriser whose every decision is
/// integer or a single division, so the same scene paints the same pixels on any machine, which
/// is the same property the simulation itself is held to.
/// </para>
/// </summary>
public sealed class RasterCanvas
{
    private readonly byte[] _pixels;

    /// <summary>A blank canvas of one colour.</summary>
    public RasterCanvas(int width, int height, MapRgb background)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A canvas needs a positive size.");
        }

        Width = width;
        Height = height;
        _pixels = new byte[width * height * 4];

        Fill(0, 0, width, height, background, 255);
    }

    /// <summary>Canvas width in pixels.</summary>
    public int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; }

    /// <summary>The raw RGBA bytes, row major, top row first.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    /// Paints a scene: background, then every group in order. The order is the whole of what the
    /// scene's grouping means to a raster, which is why the writers share the scene rather than
    /// each deciding what goes on top.
    /// </summary>
    public static RasterCanvas Paint(MapScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var canvas = new RasterCanvas(scene.Width, scene.Height, scene.Background);

        foreach (MapGroup group in scene.Groups)
        {
            foreach (MapShape shape in group.Shapes)
            {
                canvas.Draw(shape);
            }
        }

        return canvas;
    }

    /// <summary>Paints one shape.</summary>
    public void Draw(MapShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        switch (shape)
        {
            case MapRect rect:
                FillRect(rect.X, rect.Y, rect.W, rect.H, rect.Colour, rect.Alpha);
                break;

            case MapDisc disc:
                Disc(disc.X, disc.Y, disc.R, disc.Colour, disc.Alpha, disc.Hollow, disc.Thickness);
                break;

            case MapLine line:
                Segment(line.X0, line.Y0, line.X1, line.Y1, line.Thickness, line.Colour, line.Alpha, line.Dashed);
                break;

            case MapPolyline polyline:
                Polyline(polyline.Points, polyline.Thickness, polyline.Colour, polyline.Alpha, polyline.Dashed);
                break;

            case MapDots dots:
                for (int i = 0; i + 1 < dots.Points.Count; i += 2)
                {
                    Disc(dots.Points[i], dots.Points[i + 1], dots.Radius, dots.Colour, dots.Alpha, false, 0);
                }

                break;

            case MapText text:
                Text(text.X, text.Y, text.Size, text.Text, text.Colour, text.Alpha);
                break;
        }
    }

    /// <summary>An opaque fill of the whole canvas.</summary>
    private void Fill(int x, int y, int w, int h, MapRgb colour, byte alpha)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int column = x; column < x + w; column++)
            {
                Blend(column, row, colour, alpha);
            }
        }
    }

    private void FillRect(double x, double y, double w, double h, MapRgb colour, byte alpha)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = (int)Math.Ceiling(x + w);
        int y1 = (int)Math.Ceiling(y + h);

        for (int row = Math.Max(0, y0); row < Math.Min(Height, y1); row++)
        {
            for (int column = Math.Max(0, x0); column < Math.Min(Width, x1); column++)
            {
                Blend(column, row, colour, alpha);
            }
        }
    }

    private void Disc(double cx, double cy, double radius, MapRgb colour, byte alpha, bool hollow, double thickness)
    {
        if (radius <= 0)
        {
            return;
        }

        double outer = hollow ? radius + (thickness / 2) : radius;

        // A filled disc has no hole at all, and zero is the right bound for that rather than a
        // negative one: the test below is on squared distance, so a sentinel of -1 would become
        // an inner radius of one pixel and punch the middle out of every dot on the map.
        double inner = hollow ? Math.Max(0, radius - (thickness / 2)) : 0;
        double outerSquared = outer * outer;
        double innerSquared = inner * inner;

        int x0 = (int)Math.Floor(cx - outer);
        int y0 = (int)Math.Floor(cy - outer);
        int x1 = (int)Math.Ceiling(cx + outer);
        int y1 = (int)Math.Ceiling(cy + outer);

        for (int row = Math.Max(0, y0); row < Math.Min(Height, y1); row++)
        {
            double dy = row + 0.5 - cy;
            double dySquared = dy * dy;

            for (int column = Math.Max(0, x0); column < Math.Min(Width, x1); column++)
            {
                double dx = column + 0.5 - cx;
                double distance = (dx * dx) + dySquared;

                if (distance > outerSquared || distance < innerSquared)
                {
                    continue;
                }

                Blend(column, row, colour, alpha);
            }
        }
    }

    private void Polyline(IReadOnlyList<double> points, double thickness, MapRgb colour, byte alpha, bool dashed)
    {
        for (int i = 0; i + 3 < points.Count; i += 2)
        {
            Segment(points[i], points[i + 1], points[i + 2], points[i + 3], thickness, colour, alpha, dashed);
        }
    }

    /// <summary>
    /// A straight mark as the set of pixels within half its width of the line's own segment.
    /// <para>
    /// The distance test rather than a Bresenham walk, because it gives round caps and joins for
    /// nothing: two segments meeting at an angle leave no notch, and the end of a heading arrow
    /// is a cap rather than a corner. It costs a square root's worth of multiplies per candidate
    /// pixel, which at these sizes is a few hundred operations per mark.
    /// </para>
    /// </summary>
    private void Segment(
        double x0,
        double y0,
        double x1,
        double y1,
        double thickness,
        MapRgb colour,
        byte alpha,
        bool dashed)
    {
        double half = Math.Max(0.5, thickness / 2);
        double dx = x1 - x0;
        double dy = y1 - y0;
        double lengthSquared = (dx * dx) + (dy * dy);
        double length = Math.Sqrt(lengthSquared);

        int left = (int)Math.Floor(Math.Min(x0, x1) - half - 1);
        int top = (int)Math.Floor(Math.Min(y0, y1) - half - 1);
        int right = (int)Math.Ceiling(Math.Max(x0, x1) + half + 1);
        int bottom = (int)Math.Ceiling(Math.Max(y0, y1) + half + 1);

        for (int row = Math.Max(0, top); row < Math.Min(Height, bottom); row++)
        {
            for (int column = Math.Max(0, left); column < Math.Min(Width, right); column++)
            {
                double px = column + 0.5 - x0;
                double py = row + 0.5 - y0;

                double t = lengthSquared > 0 ? Math.Clamp(((px * dx) + (py * dy)) / lengthSquared, 0, 1) : 0;
                double nx = px - (t * dx);
                double ny = py - (t * dy);

                if ((nx * nx) + (ny * ny) > half * half)
                {
                    continue;
                }

                if (dashed && length > 0 && (t * length) % 8 >= 5)
                {
                    continue;
                }

                Blend(column, row, colour, alpha);
            }
        }
    }

    /// <summary>
    /// A word, drawn with the writer's own face at the largest whole multiple of its natural
    /// size that fits the size asked for. Whole multiples only: a 5x7 face scaled by 1.5 has
    /// columns of one pixel next to columns of two, which reads as a smudge at legend size.
    /// </summary>
    private void Text(double x, double y, double size, string text, MapRgb colour, byte alpha)
    {
        int scale = Math.Max(1, (int)Math.Round(size / MapFont.Height, MidpointRounding.AwayFromZero));
        int pen = (int)Math.Round(x);

        // The baseline is the bottom of the glyph, which is where SVG puts y too.
        int top = (int)Math.Round(y) - (MapFont.Height * scale);

        foreach (char character in text)
        {
            if (character == ' ')
            {
                pen += MapFont.Advance * scale;
                continue;
            }

            MapFont.Glyph glyph = MapFont.TryGet(character, out MapFont.Glyph found) ? found : MapFont.Unknown;

            for (int row = 0; row < MapFont.Height; row++)
            {
                for (int column = 0; column < MapFont.Width; column++)
                {
                    if (!glyph.At(column, row))
                    {
                        continue;
                    }

                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            Blend(pen + (column * scale) + sx, top + (row * scale) + sy, colour, alpha);
                        }
                    }
                }
            }

            pen += MapFont.Advance * scale;
        }
    }

    /// <summary>
    /// One pixel, blended by its alpha. Integer arithmetic throughout: a shade that lands one
    /// byte either way on two machines is a different picture, and the whole point of drawing
    /// from the simulation rather than from a renderer is that the picture is a function of the
    /// world rather than of the machine.
    /// </summary>
    private void Blend(int x, int y, MapRgb colour, byte alpha)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || alpha == 0)
        {
            return;
        }

        int index = ((y * Width) + x) * 4;
        int a = alpha;

        if (a == 255)
        {
            _pixels[index + 0] = colour.R;
            _pixels[index + 1] = colour.G;
            _pixels[index + 2] = colour.B;
            _pixels[index + 3] = 255;
            return;
        }

        _pixels[index + 0] = (byte)(((colour.R * a) + (_pixels[index + 0] * (255 - a))) / 255);
        _pixels[index + 1] = (byte)(((colour.G * a) + (_pixels[index + 1] * (255 - a))) / 255);
        _pixels[index + 2] = (byte)(((colour.B * a) + (_pixels[index + 2] * (255 - a))) / 255);
        _pixels[index + 3] = 255;
    }
}
