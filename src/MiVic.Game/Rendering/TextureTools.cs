using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Textures built rather than loaded.
/// </summary>
public static class TextureTools
{
    /// <summary>
    /// A texture with its mip chain filled in.
    /// <para>
    /// Mostly lifted from the cutscene asset loader, where it was written to fix the cloth
    /// aliasing: <c>Texture2D.FromStream</c> returns the top level and nothing else, and the
    /// textured technique asks for <c>MipFilter = Linear</c>, so without a chain the sampler
    /// reads full resolution at every distance. The skinned path needs the same thing for
    /// the same reason — a face atlas minified across a pitch — and a second copy of this
    /// would be one more pair of definitions to keep in step.
    /// </para>
    /// </summary>
    public static Texture2D MakeMipmapped(GraphicsDevice device, Texture2D source)
{
        int width = source.Width;
        int height = source.Height;
        SurfaceFormat format = source.Format;

        Color[] level = new Color[width * height];
        source.GetData(level);
        source.Dispose();

        int levels = 1;
        for (int w = width, h = height; w > 1 || h > 1; levels++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        Texture2D texture = new Texture2D(device, width, height, mipmap: true, format);
        texture.SetData(0, null, level, 0, level.Length);

        int previousWidth = width;
        int previousHeight = height;

        for (int mip = 1; mip < levels; mip++)
        {
            int w = Math.Max(1, previousWidth / 2);
            int h = Math.Max(1, previousHeight / 2);
            Color[] next = new Color[w * h];

            for (int y = 0; y < h; y++)
            {
                // Clamped rather than skipped: an odd-sized level (3 pixels wide, say)
                // has a last destination texel whose second source column is itself.
                int y0 = Math.Min(previousHeight - 1, y * 2);
                int y1 = Math.Min(previousHeight - 1, (y * 2) + 1);

                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(previousWidth - 1, x * 2);
                    int x1 = Math.Min(previousWidth - 1, (x * 2) + 1);

                    Color a = level[(y0 * previousWidth) + x0];
                    Color b = level[(y0 * previousWidth) + x1];
                    Color c = level[(y1 * previousWidth) + x0];
                    Color d = level[(y1 * previousWidth) + x1];

                    next[(y * w) + x] = new Color(
                        (a.R + b.R + c.R + d.R + 2) / 4,
                        (a.G + b.G + c.G + d.G + 2) / 4,
                        (a.B + b.B + c.B + d.B + 2) / 4,
                        (a.A + b.A + c.A + d.A + 2) / 4);
                }
            }

            texture.SetData(mip, null, next, 0, next.Length);
            level = next;
            previousWidth = w;
            previousHeight = h;
        }

        return texture;
    }
}
