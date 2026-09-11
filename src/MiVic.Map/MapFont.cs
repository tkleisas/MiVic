namespace MiVic.Map;

/// <summary>
/// The face the raster writer draws with: five columns by seven rows, one bit per pixel.
/// <para>
/// A bitmap font rather than a font file, and the reason is the constraint the whole project is
/// built around. The PNG has to be produced on a machine with no graphics device and no font
/// stack — from a test host, from the probe, from a build server — and a text renderer that
/// needed a system font would be a text renderer that sometimes drew nothing. Ninety-odd glyphs
/// of hand-written bits cost less than the alternative and cannot fail to load.
/// </para>
/// <para>
/// The glyphs are written out as pictures rather than as hex, because a hex table is a table
/// nobody can proofread and a picture is a table anybody can. Lower case is folded to upper,
/// which is what lets a label built from an enum's own name render whatever case the enum
/// happens to use.
/// </para>
/// </summary>
public static class MapFont
{
    /// <summary>Columns per glyph, before scaling.</summary>
    public const int Width = 5;

    /// <summary>Rows per glyph, before scaling.</summary>
    public const int Height = 7;

    /// <summary>
    /// What a caller draws a string with: the glyph, its scale, and the pen's starting column.
    /// Rows are the low seven bits of each entry; bit four is the leftmost column.
    /// </summary>
    /// <param name="Glyph">Seven rows, top first, five bits each.</param>
    public readonly record struct Glyph(byte[] Rows)
    {
        /// <summary>True when the pixel at a column and row of the glyph is set.</summary>
        public bool At(int column, int row)
            => (Rows[row] & (1 << (Width - 1 - column))) != 0;
    }

    private static readonly Dictionary<char, Glyph> Face = Build();

    /// <summary>The glyph for a character, folding case and refusing nothing.</summary>
    /// <param name="character">The character asked for.</param>
    /// <param name="glyph">The picture to draw.</param>
    /// <returns>False when the character has no glyph, in which case a box is drawn instead.</returns>
    public static bool TryGet(char character, out Glyph glyph)
    {
        if (Face.TryGetValue(character, out glyph))
        {
            return true;
        }

        if (char.IsLower(character) && Face.TryGetValue(char.ToUpperInvariant(character), out glyph))
        {
            return true;
        }

        return false;
    }

    /// <summary>Columns one character advances the pen, before scaling.</summary>
    public const int Advance = Width + 1;

    /// <summary>
    /// The glyph drawn for a character the face does not have: a hollow box. Visible rather than
    /// skipped, because a word with a character missing from the middle of it reads as a
    /// different word, and a box reads as a box.
    /// </summary>
    public static readonly Glyph Unknown = new(
    [
        0b11111,
        0b10001,
        0b10001,
        0b10001,
        0b10001,
        0b10001,
        0b11111,
    ]);

    private static Dictionary<char, Glyph> Build()
    {
        var face = new Dictionary<char, Glyph>(96);

        void Add(char character, params byte[] rows)
        {
            if (rows.Length != Height)
            {
                throw new InvalidOperationException($"'{character}' is drawn with {rows.Length} rows, not {Height}.");
            }

            face[character] = new Glyph(rows);
        }

        Add(' ', 0, 0, 0, 0, 0, 0, 0);

        Add('A', 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        Add('B', 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110);
        Add('C', 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110);
        Add('D', 0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100);
        Add('E', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111);
        Add('F', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000);
        Add('G', 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111);
        Add('H', 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        Add('I', 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        Add('J', 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100);
        Add('K', 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001);
        Add('L', 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111);
        Add('M', 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001);
        Add('N', 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001);
        Add('O', 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        Add('P', 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000);
        Add('Q', 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101);
        Add('R', 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001);
        Add('S', 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110);
        Add('T', 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        Add('U', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        Add('V', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100);
        Add('W', 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001);
        Add('X', 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001);
        Add('Y', 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100);
        Add('Z', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111);

        Add('0', 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110);
        Add('1', 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        Add('2', 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111);
        Add('3', 0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110);
        Add('4', 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010);
        Add('5', 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110);
        Add('6', 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110);
        Add('7', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000);
        Add('8', 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110);
        Add('9', 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100);

        Add('#', 0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010);
        Add('@', 0b01110, 0b10001, 0b10111, 0b10101, 0b10111, 0b10000, 0b01110);
        Add('-', 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000);
        Add('_', 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111);
        Add('.', 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100);
        Add(',', 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100, 0b01000);
        Add(':', 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000);
        Add(';', 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b01000);
        Add('/', 0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000);
        Add('\\', 0b10000, 0b01000, 0b01000, 0b00100, 0b00010, 0b00010, 0b00001);
        Add('|', 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        Add('+', 0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000);
        Add('=', 0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000);
        Add('*', 0b00000, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00000);
        Add('%', 0b11001, 0b11010, 0b00010, 0b00100, 0b01000, 0b01011, 0b10011);
        Add('(', 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010);
        Add(')', 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000);
        Add('[', 0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110);
        Add(']', 0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110);
        Add('<', 0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010);
        Add('>', 0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000);
        Add('!', 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100);
        Add('?', 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100);
        Add('\'', 0b00100, 0b00100, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000);
        Add('"', 0b01010, 0b01010, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000);
        Add('^', 0b00100, 0b01010, 0b10001, 0b00000, 0b00000, 0b00000, 0b00000);
        Add('~', 0b00000, 0b00000, 0b01000, 0b10101, 0b00010, 0b00000, 0b00000);

        return face;
    }
}
