namespace MiVic.Audio;

/// <summary>
/// The scales and modes each faction writes its music in.
/// <para>
/// This is the heart of the whole audio feature: the difference between a Soviet
/// march and cheap Western rock'n'roll is mostly which notes are available. The
/// Russian tradition leans on the minor modes — natural minor for folk song,
/// harmonic minor for the raised leading tone that makes a cadence sound
/// "Slavic", Dorian for the heroic variant. Chinese patriotic song is built on
/// the anhemitonic pentatonic, the five-note scale with no semitones, in either
/// its major (gong) or minor (yu) form. Western pop is the major scale and the
/// blues scale, which is all rock'n'roll ever needed.
/// </para>
/// </summary>
public static class Scales
{
    /// <summary>Natural minor / Aeolian: the backbone of Russian folk song.</summary>
    public static readonly int[] NaturalMinor = [0, 2, 3, 5, 7, 8, 10];

    /// <summary>Harmonic minor: the raised seventh that gives a Slavic cadence.</summary>
    public static readonly int[] HarmonicMinor = [0, 2, 3, 5, 7, 8, 11];

    /// <summary>Dorian: minor with a bright sixth, used for heroic themes.</summary>
    public static readonly int[] Dorian = [0, 2, 3, 5, 7, 9, 10];

    /// <summary>Major pentatonic (gong mode): the common Chinese patriotic scale.</summary>
    public static readonly int[] PentatonicMajor = [0, 2, 4, 7, 9];

    /// <summary>Minor pentatonic (yu mode): the wistful Chinese variant.</summary>
    public static readonly int[] PentatonicMinor = [0, 3, 5, 7, 10];

    /// <summary>Ionian: do-re-mi, the bubblegum pop scale.</summary>
    public static readonly int[] Major = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>Blues scale: the six notes rock'n'roll is made of.</summary>
    public static readonly int[] Blues = [0, 3, 5, 6, 7, 10];

    /// <summary>
    /// Degree to semitones, wrapping into higher octaves. Degree may be negative
    /// or beyond the scale length, which is what lets a melody walk up and down
    /// without the caller tracking octaves.
    /// </summary>
    public static int Semitones(int[] scale, int degree)
    {
        ArgumentNullException.ThrowIfNull(scale);

        if (scale.Length == 0)
        {
            throw new ArgumentException("Scale must have at least one degree.", nameof(scale));
        }

        int length = scale.Length;
        int octave = (int)Math.Floor(degree / (double)length);
        int index = degree - (octave * length);

        return scale[index] + (octave * 12);
    }

    /// <summary>True when a MIDI note belongs to the scale rooted at <paramref name="root"/>.</summary>
    public static bool Contains(int[] scale, int root, int midiNote)
    {
        int relative = ((midiNote - root) % 12 + 12) % 12;

        foreach (int semitone in scale)
        {
            if (semitone == relative)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Converts a MIDI note number to frequency in Hz (A4 = 440).</summary>
    public static double Frequency(int midiNote) => 440d * Math.Pow(2d, (midiNote - 69) / 12d);
}
