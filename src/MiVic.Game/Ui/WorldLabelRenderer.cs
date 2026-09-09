using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Ui;

/// <summary>A label anchored to a point in the 3D world.</summary>
/// <param name="WorldPosition">Anchor position in metres.</param>
/// <param name="Text">Text to draw, in Greek.</param>
/// <param name="Color">Text colour.</param>
public readonly record struct WorldLabel(Vector3 WorldPosition, string Text, Color Color);

/// <summary>
/// Draws text that belongs to the battlefield rather than the screen: faction
/// names over their command centres, and later damage numbers and rally markers.
/// <para>
/// This uses the compiled <see cref="SpriteFont"/> rather than the ImGui path,
/// which is why the font has to go through the MonoGame content pipeline with
/// explicit Greek character regions. It also proves that pipeline works: if the
/// regions were wrong, the labels would render as empty boxes while ImGui text
/// still looked fine.
/// </para>
/// </summary>
public sealed class WorldLabelRenderer : IDisposable
{
    private static readonly Color ShadowColor = new(0, 0, 0, 200);

    private readonly SpriteFont _font;
    private readonly SpriteBatch _spriteBatch;

    public WorldLabelRenderer(GraphicsDevice device, SpriteFont font)
    {
        ArgumentNullException.ThrowIfNull(device);
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _spriteBatch = new SpriteBatch(device);

        // A font that only covers Basic Latin would silently draw boxes, so the
        // fact is recorded and reported rather than discovered on screen.
        HasGreekGlyphs = HasGlyph('Σ') && HasGlyph('ο') && HasGlyph('Δ') && HasGlyph('ί');
    }

    /// <summary>True when the compiled font actually carries Greek glyphs.</summary>
    public bool HasGreekGlyphs { get; }

    /// <summary>True when the compiled font contains a glyph for <paramref name="character"/>.</summary>
    public bool HasGlyph(char character)
    {
        foreach (SpriteFont.Glyph glyph in _font.Glyphs)
        {
            if (glyph.Character == character)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Draws every visible label.</summary>
    /// <param name="labels">Labels to draw.</param>
    /// <param name="view">Camera view matrix.</param>
    /// <param name="projection">Camera projection matrix.</param>
    /// <param name="viewport">Current viewport.</param>
    public void Draw(IReadOnlyList<WorldLabel> labels, in Matrix view, in Matrix projection, Viewport viewport)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            return;
        }

        Matrix viewProjection = view * projection;

        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);

        foreach (WorldLabel label in labels)
        {
            if (!TryProject(label.WorldPosition, viewProjection, viewport, out Vector2 screen))
            {
                continue;
            }

            Vector2 size = _font.MeasureString(label.Text);
            Vector2 origin = new(screen.X - (size.X * 0.5f), screen.Y - size.Y);

            _spriteBatch.DrawString(_font, label.Text, origin + new Vector2(1.5f, 1.5f), ShadowColor);
            _spriteBatch.DrawString(_font, label.Text, origin, label.Color);
        }

        _spriteBatch.End();
    }

    /// <summary>
    /// Draws a scaled sample of the font in screen space. Used by the diagnostic
    /// path to prove the compiled font really draws Greek letters.
    /// </summary>
    public void DrawSample(string text, Vector2 position, float scale, Color color)
    {
        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            RasterizerState.CullNone);

        _spriteBatch.DrawString(_font, text, position + new Vector2(2f, 2f), ShadowColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        _spriteBatch.DrawString(_font, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

        _spriteBatch.End();
    }

    /// <summary>Projects a world position to screen pixels, rejecting points behind the camera.</summary>
    private static bool TryProject(Vector3 worldPosition, in Matrix viewProjection, Viewport viewport, out Vector2 screen)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPosition, 1f), viewProjection);

        if (clip.W <= 0.001f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;

        screen = new Vector2(
            (ndcX * 0.5f + 0.5f) * viewport.Width,
            (-ndcY * 0.5f + 0.5f) * viewport.Height);

        return true;
    }

    public void Dispose() => _spriteBatch.Dispose();
}
