namespace MiVic.Game.Ui;

/// <summary>How much of a sample text the loaded UI font can actually draw.</summary>
/// <param name="Covered">Characters with a rasterised glyph.</param>
/// <param name="Total">Characters tested.</param>
/// <param name="Missing">The characters that had no glyph.</param>
/// <param name="AtlasWidth">Font atlas width in pixels.</param>
/// <param name="AtlasHeight">Font atlas height in pixels.</param>
/// <param name="FontSize">Loaded font size in pixels.</param>
/// <param name="Ascent">Font ascent in pixels.</param>
public readonly record struct FontCoverage(
    int Covered,
    int Total,
    string Missing,
    int AtlasWidth,
    int AtlasHeight,
    float FontSize,
    float Ascent)
{
    /// <summary>True when every tested character has a real glyph.</summary>
    public bool IsComplete => Total > 0 && Covered == Total;
}
