using System.Text;

namespace MiVic.Game.Ui;

/// <summary>
/// Which font has to carry a symbol, and therefore which path draws it.
/// </summary>
public enum SymbolPath
{
    /// <summary>
    /// ImGui text, from the Noto Sans entries in the atlas — the same font, and the same
    /// ranges, as the Greek.
    /// </summary>
    AtlasText,

    /// <summary>
    /// ImGui text, from the symbol subset merged into the atlas beside the text font. Only
    /// for codepoints Noto Sans has no glyph for: the merge asks for nothing the text font
    /// can already draw, so a symbol in both cannot quietly change appearance.
    /// </summary>
    AtlasSymbol,

    /// <summary>World-space text, drawn by the compiled <c>SpriteFont</c>.</summary>
    SpriteFont,
}

/// <summary>One non-ASCII mark the interface draws.</summary>
/// <param name="Character">The codepoint, as it is written in the UI string.</param>
/// <param name="Name">The Unicode name, so the marks stay distinguishable in a diff.</param>
/// <param name="Path">Which font has to carry it, and which path draws it.</param>
/// <param name="Usage">Where the interface draws it.</param>
public readonly record struct UiSymbol(char Character, string Name, SymbolPath Path, string Usage);

/// <summary>
/// Every non-ASCII symbol the interface draws, in one place, so the self-test can prove the
/// fonts can actually draw them.
/// <para>
/// <b>Why this exists.</b> A symbol with no glyph throws nothing and logs nothing: ImGui
/// substitutes its fallback glyph, and the row renders as the same box a missing Greek letter
/// would. That is how <c>√</c> in the mission panel and <c>▶</c> in the production panel sat
/// in a shipped interface unnoticed until somebody looked at a screenshot, months apart. The
/// catalogue is what the check iterates; the check is what makes the third one loud. The
/// Greek check proves the text ranges, and this proves the symbols, which live outside them.
/// </para>
/// <para>
/// Symbols the probe writes into a transcript rather than a font — the arithmetic in
/// <c>ProbeRunner</c>, <c>²</c> and <c>÷</c> and the like — are deliberately absent: nothing
/// rasterises them, so no glyph can be missing from.
/// </para>
/// </summary>
public static class UiSymbols
{
    /// <summary>
    /// The catalogue, in codepoint order, because these become glyph ranges and ImGui wants
    /// ranges sorted.
    /// </summary>
    public static readonly IReadOnlyList<UiSymbol> All =
    [
        new('«', "LEFT-POINTING DOUBLE ANGLE QUOTATION MARK", SymbolPath.AtlasText,
            "GameHud — the objective title quoted in the defeat line, and the production panel named in the help"),

        new('°', "DEGREE SIGN", SymbolPath.AtlasText,
            "MiVicGame — the camera's yaw in the model viewer"),

        new('·', "MIDDLE DOT", SymbolPath.AtlasText,
            "GameHud — a production job that is queued but not running"),

        new('»', "RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK", SymbolPath.AtlasText,
            "GameHud — the pair to the quotation mark above"),

        new('×', "MULTIPLICATION SIGN", SymbolPath.AtlasText,
            "GameHud — a failed objective, a refused structure site, a refused bridge site"),

        new('—', "EM DASH", SymbolPath.AtlasText,
            "GameHud and MiVicGame — the separator inside panel titles and the note lines beneath them"),

        new('•', "BULLET", SymbolPath.AtlasText,
            "GameHud — an objective still open, and the structure row that is armed and waiting for a site"),

        new('‰', "PER MILLE SIGN", SymbolPath.AtlasText,
            "GameHud — a unit's armour out of a thousand, beside the same figure as a percentage"),

        new('←', "LEFTWARDS ARROW", SymbolPath.AtlasSymbol,
            "MiVicGame — the model viewer's previous-model key"),

        new('→', "RIGHTWARDS ARROW", SymbolPath.AtlasSymbol,
            "MiVicGame — the model viewer's next-model key"),

        new('−', "MINUS SIGN", SymbolPath.AtlasSymbol,
            "GameHud — the upkeep a player is paying every tick"),

        new('√', "SQUARE ROOT", SymbolPath.AtlasSymbol,
            "GameHud — a completed objective, drawn in green"),

        new('⌀', "DIAMETER SIGN", SymbolPath.AtlasSymbol,
            "MiVicGame — a model's wheel diameter in the model viewer"),

        new('▶', "BLACK RIGHT-POINTING TRIANGLE", SymbolPath.AtlasSymbol,
            "GameHud — the production job that is running, and the structure row armed for placement"),
    ];

    /// <summary>Symbols drawn through the ImGui atlas, as one string to measure.</summary>
    public static string AtlasSample { get; } = Sample(symbol => symbol.Path != SymbolPath.SpriteFont);

    /// <summary>
    /// Symbols the merged symbol font has to carry, in codepoint order. The atlas asks for
    /// these and no others, so the font and this list cannot drift into disagreeing about
    /// which symbols it is there for.
    /// </summary>
    public static string SymbolFontSample { get; } = Sample(symbol => symbol.Path == SymbolPath.AtlasSymbol);

    /// <summary>Symbols drawn in world space through the compiled SpriteFont.</summary>
    public static string SpriteFontSample { get; } = Sample(symbol => symbol.Path == SymbolPath.SpriteFont);

    /// <summary>
    /// Turns what each font path is missing into a verdict on the catalogue.
    /// </summary>
    /// <param name="missingFromAtlas">From <see cref="ImGuiController.MissingGlyphs"/>.</param>
    /// <param name="missingFromSpriteFont">From <see cref="WorldLabelRenderer.MissingGlyphs"/>.</param>
    public static UiSymbolCoverage Measure(string missingFromAtlas, string missingFromSpriteFont)
    {
        ArgumentNullException.ThrowIfNull(missingFromAtlas);
        ArgumentNullException.ThrowIfNull(missingFromSpriteFont);

        return new UiSymbolCoverage(
            AtlasSample.Length,
            missingFromAtlas,
            SpriteFontSample.Length,
            missingFromSpriteFont);
    }

    private static string Sample(Func<UiSymbol, bool> wanted)
    {
        StringBuilder text = new();

        foreach (UiSymbol symbol in All)
        {
            if (wanted(symbol))
            {
                text.Append(symbol.Character);
            }
        }

        return text.ToString();
    }
}

/// <summary>How much of <see cref="UiSymbols.All"/> each font path can actually draw.</summary>
/// <param name="AtlasTotal">Symbols drawn through the ImGui atlas.</param>
/// <param name="AtlasMissing">Those of them at least one font in the atlas has no glyph for.</param>
/// <param name="SpriteTotal">Symbols drawn through the compiled SpriteFont.</param>
/// <param name="SpriteMissing">Those of them the compiled font has no glyph for.</param>
public readonly record struct UiSymbolCoverage(
    int AtlasTotal,
    string AtlasMissing,
    int SpriteTotal,
    string SpriteMissing)
{
    /// <summary>Symbols the atlas rasterised.</summary>
    public int AtlasCovered => AtlasTotal - AtlasMissing.Length;

    /// <summary>Symbols the compiled font carries.</summary>
    public int SpriteCovered => SpriteTotal - SpriteMissing.Length;

    /// <summary>True when every symbol has a glyph on every path that draws it.</summary>
    /// <remarks>
    /// A path with nothing on it counts as satisfied: the SpriteFont draws no symbol today,
    /// and a rule demanding one would be a rule about the content rather than about the fonts.
    /// An empty catalogue is <i>not</i> satisfied, because a check with nothing to check
    /// passes silently, which is the failure the catalogue exists to prevent.
    /// </remarks>
    public bool IsComplete => AtlasTotal > 0 && AtlasMissing.Length == 0 && SpriteMissing.Length == 0;
}
