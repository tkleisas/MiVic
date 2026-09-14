using MiVic.Core.Random;

namespace MiVic.Audio;

/// <summary>What was composed, for reporting and tests.</summary>
/// <param name="Style">The idiom.</param>
/// <param name="Scale">The scale the composition is written in.</param>
/// <param name="Samples">Length of the loop, in samples.</param>
/// <param name="SampleRate">The composition's own rate: 8 kHz, bit-music native.</param>
/// <param name="MacroPeriod">Samples per full melodic cycle.</param>
/// <param name="Seconds">Length of the loop, in seconds.</param>
public readonly record struct BytebeatInfo(
    FactionStyle Style,
    string Scale,
    int Samples,
    int SampleRate,
    int MacroPeriod,
    double Seconds);

/// <summary>
/// Writes a faction's soundtrack as bit-music: a composition over an integer
/// counter <c>t</c> that advances one step per sample, the way a demoscene
/// musician would write it.
/// <para>
/// <b>Why bit-music is the soundtrack.</b> A 1980s war machine's radio would
/// crackle; a composer with an 8 kHz counter and a handful of shifts and masks
/// writes melodies out of integer overflow itself. The result is a soundtrack
/// that sounds like the machine that generated it — one line of arithmetic per
/// sample, no instruments, no synthesiser, the whole composition a single
/// expression evaluated once per frame.
/// </para>
/// <para>
/// <b>The composition is the faction's scale, spoken in bit-arithmetic.</b> The
/// five just-intonation ratios are drawn from the faction's own scale — the
/// Σοβιετικοί write in a harmonic-minor subset, the Κινέζοι in the user's own
/// major-pentatonic chain («ο κινέζικος πίνακας», the composition that named
/// the style), the Δυτικοί in a blues subset. The same three voices run over
/// them: a melody multiplied out of the counter and masked, a second voice
/// windowed by the counter's own upper bits, and a bass line that is the
/// counter's lower half read aloud.
/// </para>
/// <para>
/// <b>The seed is the composer.</b> The two hash constants and the shifts are
/// jittered within ranges that keep the composition musical, so a faction's
/// theme differs between playthroughs while staying in its idiom — the same
/// promise the ChipSynth themes made, kept by a single expression.
/// </para>
/// <para>
/// <b>The loop is the composition's own.</b> The melodic macro-period is five
/// melodic slots times the largest shift's span; the theme renders a whole
/// multiple of it, so the loop point is a boundary the composition itself
/// repeats on, not an arbitrary cut.
/// </para>
/// </summary>
public static class Bytebeat
{
    /// <summary>
    /// The composition's own rate. Bit-music is written to be played at 8 kHz: the
    /// counter's shifts are calibrated for it, and the result is the texture the
    /// demoscene made this arithmetic famous for.
    /// </summary>
    public const int SampleRate = 8000;

    /// <summary>Melodic slots the composition draws from: the bytebeat's five.</summary>
    private const int SlotCount = 5;

    /// <summary>Renders a faction composition as 16-bit PCM, centred on silence.</summary>
    public static short[] GeneratePcm16(FactionStyle style, ulong seed, out BytebeatInfo info)
    {
        Composition composition = Compose(style, seed);
        (short[] samples, int macroPeriod) = Render(composition);
        info = new BytebeatInfo(style, composition.ScaleName, samples.Length, SampleRate, macroPeriod, samples.Length / (double)SampleRate);
        return samples;
    }

    /// <summary>
    /// The composition, as (scale, constants) the renderer evaluates. The constants are
    /// jittered from the seed within ranges the demoscene's own compositions kept to —
    /// the shifts between ten and fourteen (a bytebeat is calibrated by where its
    /// counter's high bits land), the additive offsets in the thousands, the mask
    /// window's rank between sixty and two hundred.
    /// </summary>
    internal sealed record Composition(
        FactionStyle Style,
        string ScaleName,
        double[] Ratios,
        int ShiftA,
        int ShiftB,
        int ConstantA,
        int ShiftC,
        int ShiftD,
        int ConstantB,
        int ShiftWindow,
        int WindowSize,
        int WindowFloor,
        int ShiftBass,
        int MacroPeriod,
        byte[] Samples)
    {
        /// <summary>The seed the composition was drawn from, for reporting.</summary>
        public ulong Seed { get; init; }
    }

    /// <summary>Builds a faction's composition: the scale from the idiom, the arithmetic from the seed.</summary>
    private static Composition Compose(FactionStyle style, ulong seed)
    {
        var rng = new Pcg32(seed ^ ((ulong)style * 0x9E37_79B9_7F4A_7C15UL));

        // The faction's five melodic slots, as semitone offsets from the tonic: the
        // just-intonation ratios are 2^(k/12) of them, the same arithmetic a bytebeat's
        // multiplier does to the counter.
        int[] degrees = style switch
        {
            // The harmonic minor's five-note subset: the tonic, the minor third, the
            // fifth, the flat sixth and the raised seventh — the "Slavic" cadence notes.
            FactionStyle.Soviet => [0, 3, 7, 8, 11],

            // The user's own composition: the major pentatonic on the second degree —
            // the set «τσίνα γουίντοους» was written in.
            FactionStyle.Chinese => [2, 4, 7, 9, 11],

            // The blues scale's five-note subset: the six notes rock'n'roll is made of,
            // minus one, because the bytebeat has five slots and the blue note is the
            // one that stays.
            _ => [0, 3, 6, 10, 7],
        };

        string scaleName = style switch
        {
            FactionStyle.Soviet => "αρμονική ελάσσων (πενταφωνική υποσύνολο)",
            FactionStyle.Chinese => "μείζον πενταφωνική (ρε γκόνγκ)",
            _ => "μπλουζ",
        };

        double[] ratios = [.. degrees.Select(degree => Math.Pow(2d, degree / 12d))];

        // The shifts, the additive constants and the window: jittered within the ranges
        // the compositions are calibrated for. The two chains are deliberately different
        // — the melody's index and the harmony's index must disagree, or both voices
        // walk the same five notes in the same slots and the composition becomes one
        // voice with an echo.
        int shiftA = rng.NextInt(10, 15);
        int shiftB = rng.NextInt(8, 13);
        int shiftC = rng.NextInt(9, 14);
        int shiftD = rng.NextInt(7, 12);
        int shiftWindow = rng.NextInt(13, 16);
        int shiftBass = rng.NextInt(3, 6);

        int constantA = rng.NextInt(2_000, 4_000);
        int constantB = rng.NextInt(1_500, 3_000);
        int windowSize = rng.NextInt(60, 200);
        int windowFloor = rng.NextInt(4, 16);

        var composition = new Composition(
            style,
            scaleName,
            ratios,
            shiftA, shiftB, constantA,
            shiftC, shiftD, constantB,
            shiftWindow, windowSize, windowFloor,
            shiftBass,
            MacroPeriod: 0,
            Samples: [])
        {
            Seed = seed,
        };

        return composition;
    }

    /// <summary>
    /// Samples the composition, one counter step per sample, and writes it to 16-bit
    /// PCM centred on silence.
    /// <para>
    /// The three voices are the composition the genre is made of, evaluated in C's own
    /// integer arithmetic so the result is the same on every machine: the melody is a
    /// scale slot multiplied by the counter and masked to a bit, the second voice is
    /// windowed by the counter's upper bits, and the bass is the counter's lower half
    /// read aloud — then the three are OR-ed, which is how bit-music mixes its voices:
    /// not summed, but overlaid.
    /// </para>
    /// <para>
    /// The render length is a whole multiple of the composition's macro-period, so the
    /// loop the soundtrack plays is the composition's own boundary rather than an
    /// arbitrary cut: five melodic slots across the largest shift's sweep.
    /// </para>
    /// </summary>
    private static (short[] Samples, int MacroPeriod) Render(Composition composition)
    {
        // The macro-period: five melodic slots × the largest shift's span, which is
        // where every voice's pattern has returned to its start. The render is a whole
        // number of these.
        int largestShift = Math.Max(
            Math.Max(composition.ShiftA, Math.Max(composition.ShiftB, composition.ShiftC)),
            Math.Max(composition.ShiftD, composition.ShiftWindow));

        int macroPeriod = SlotCount * (1 << largestShift);
        int loops = (int)Math.Max(1, (262144L) / macroPeriod); // ~33 s of theme
        int samples = macroPeriod * loops;

        var pcm = new short[samples];
        var raw = new int[samples];
        double[] ratios = composition.Ratios;

        for (int i = 0; i < samples; i++)
        {
            long t = i;

            // Voice 1 — the melody: a scale slot picked by two counter streams XOR-ed
            // and offset, multiplied by the counter itself, masked to its top bit. The
            // mask is what makes the output a square wave pitched by the scale.
            int slot1 = (int)(((t >> composition.ShiftA) ^ ((t >> composition.ShiftB) + composition.ConstantA)) % SlotCount);
            int voice1 = (int)(ratios[slot1] * t) & 128;

            // Voice 2 — the second voice: the same walk a step apart, windowed by the
            // counter's own upper bits — the window widening and narrowing is what gives
            // the composition its phrase structure.
            int slot2 = (int)(((t >> composition.ShiftC) ^ ((t >> composition.ShiftD) + composition.ConstantB)) % SlotCount);
            int voice2 = (int)(ratios[slot2] * t) & (int)((t >> composition.ShiftWindow) % composition.WindowSize + composition.WindowFloor);

            // Voice 3 — the bass: the counter's own lower half, read aloud.
            int voice3 = (int)(t >> composition.ShiftBass);

            int sample = voice1 | voice2 | voice3;

            // The bytebeat's 8-bit sample, kept raw through the correction: the OR of
            // the three unsigned voices leans upward, and the lean is removed in the
            // 8-bit domain where it belongs — before the stretch to 16 bits, which
            // otherwise clamps half the wave at one rail and leaves the mix with the
            // same lean it started with.
            raw[i] = (sample & 0xFF) - 128;
        }

        // The lean removed: the mean of the raw 8-bit samples, then the stretch to the
        // full 16-bit range the soundtrack plays at.
        long sum = 0;

        for (int i = 0; i < samples; i++)
        {
            sum += raw[i];
        }

        // Rounded: an integer mean leaves a residual of half a unit, which the stretch
        // to 16 bits amplifies by 32767/stretch — a rounding lean, and the clamp that
        // follows it would keep it. Rounded, the residual is zero.
        int mean = (int)Math.Round((double)sum / Math.Max(1, samples), MidpointRounding.AwayFromZero);

        // The corrected samples' own extremes: the wave is stretched by what the
        // corrected 8-bit range actually spans, not by a fixed 256 — a fixed stretch
        // clamps one rail and the clamp is the lean the correction was for.
        int lowest = int.MaxValue;
        int highest = int.MinValue;

        for (int i = 0; i < samples; i++)
        {
            int corrected = raw[i] - mean;
            lowest = Math.Min(lowest, corrected);
            highest = Math.Max(highest, corrected);
        }

        int stretch = Math.Max(1, Math.Max(-lowest, highest) * 2);

        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)Math.Clamp((raw[i] - mean) * 32767 / stretch, short.MinValue, short.MaxValue);
        }

        return (pcm, macroPeriod);
    }

    /// <summary>
    /// Writes the composition to a WAV file: 8 kHz, mono, 16-bit, centred on silence.
    /// </summary>
    public static void Write(string path, FactionStyle style, ulong seed, out BytebeatInfo info)
        => WavWriter.Write(path, GeneratePcm16(style, seed, out info), SampleRate);
}
