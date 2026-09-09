using MiVic.Core.Random;

namespace MiVic.Audio;

/// <summary>Which faction's musical idiom to write in.</summary>
public enum FactionStyle
{
    /// <summary>Σοβιετικοί: patriotic march in the minor modes.</summary>
    Soviet = 0,

    /// <summary>Κινέζοι: patriotic song on the anhemitonic pentatonic.</summary>
    Chinese = 1,

    /// <summary>Δυτικοί: cheap rock'n'roll and bubblegum pop.</summary>
    Western = 2,
}

/// <summary>What was generated, for reporting and tests.</summary>
/// <param name="Style">The idiom.</param>
/// <param name="Scale">Scale used.</param>
/// <param name="RootNote">Tonic as a MIDI note number.</param>
/// <param name="BeatsPerMinute">Tempo.</param>
/// <param name="Seconds">Length of the loop.</param>
/// <param name="NoteCount">Notes rendered.</param>
public readonly record struct MusicInfo(
    FactionStyle Style,
    string Scale,
    int RootNote,
    int BeatsPerMinute,
    double Seconds,
    int NoteCount);

/// <summary>
/// Writes a faction's theme, sample by sample, from a seed.
/// <para>
/// Each faction is a different musical tradition rather than a different set of
/// instrument volumes. The Σοβιετικοί get a minor-key march: stepwise phrases
/// with fourth and fifth leaps, a dotted march rhythm and a harmonic-minor
/// cadence. The Κινέζοι get the anhemitonic pentatonic — no semitones at all —
/// with grace notes and fourths in the accompaniment. The Δυτικοί get a
/// twelve-bar blues shuffle with a boogie bass, backbeat snare, slightly detuned
/// sawtooths and a bubblegum hook, because their empire is loud, cheap and
/// pleased with itself.
/// </para>
/// <para>
/// Generation is deterministic: the same seed and faction always produce the
/// same waveform, which is what makes the audio testable without listening to it.
/// </para>
/// </summary>
public static class MusicGenerator
{
    /// <summary>Sample rate of generated audio. 22 kHz is period-correct for the era.</summary>
    public const int SampleRate = 22050;

    /// <summary>Renders a faction theme as a mono float mix, normalised to ±0.9.</summary>
    public static float[] Generate(FactionStyle style, ulong seed, double seconds, out MusicInfo info)
    {
        if (seconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Must be positive.");
        }

        var synth = new ChipSynth(SampleRate);
        var mix = new float[(int)(seconds * SampleRate)];
        var rng = new Pcg32(seed ^ ((ulong)style * 0x9E37_79B9_7F4A_7C15UL));

        info = style switch
        {
            FactionStyle.Soviet => SovietMarch(synth, mix, rng, seconds),
            FactionStyle.Chinese => ChineseSong(synth, mix, rng, seconds),
            _ => WesternRock(synth, mix, rng, seconds),
        };

        Normalise(mix);
        return mix;
    }

    /// <summary>Renders a theme and converts it to 16-bit PCM.</summary>
    public static short[] GeneratePcm16(FactionStyle style, ulong seed, double seconds, out MusicInfo info)
    {
        float[] mix = Generate(style, seed, seconds, out info);
        var pcm = new short[mix.Length];

        for (int i = 0; i < mix.Length; i++)
        {
            pcm[i] = (short)Math.Clamp((int)Math.Round(mix[i] * 32767f), short.MinValue, short.MaxValue);
        }

        return pcm;
    }

    /// <summary>
    /// A Soviet march: minor key, dotted military rhythm, brass-like pulse lead.
    /// </summary>
    private static MusicInfo SovietMarch(ChipSynth synth, float[] mix, Pcg32 rng, double seconds)
    {
        const int Root = 57;          // A3
        const int BeatsPerMinute = 96;
        int[] scale = Scales.HarmonicMinor;
        double beat = 60d / BeatsPerMinute;
        int beats = (int)(seconds / beat);
        int notes = 0;

        // i – VI – III – VII, the standard minor-key march cycle. Degree 0 is the
        // tonic, 5 the sixth, 2 the third and 6 the seventh.
        int[] progression = [0, 5, 2, 6];

        for (int bar = 0; bar < beats / 4; bar++)
        {
            double barStart = bar * 4 * beat;
            int degree = progression[bar % progression.Length];

            // Bass: root on one, fifth on three, an octave down.
            synth.AddNote(mix, Seconds(barStart), beat * 1.6, Root + Scales.Semitones(scale, degree) - 12,
                0.28f, Waveform.Triangle, Envelope.Brass);
            synth.AddNote(mix, Seconds(barStart + (2 * beat)), beat * 1.6, Root + Scales.Semitones(scale, degree + 4) - 12,
                0.24f, Waveform.Triangle, Envelope.Brass);

            // March snare: strong on two and four, with a sixteenth pickup.
            synth.AddDrum(mix, Seconds(barStart + beat), DrumKind.Snare, 0.22f);
            synth.AddDrum(mix, Seconds(barStart + (3 * beat)), DrumKind.Snare, 0.24f);
            synth.AddDrum(mix, Seconds(barStart + (3.75 * beat)), DrumKind.Snare, 0.12f);
            synth.AddDrum(mix, Seconds(barStart), DrumKind.Kick, 0.30f);
            synth.AddDrum(mix, Seconds(barStart + (2 * beat)), DrumKind.Kick, 0.26f);

            // Melody: mostly stepwise, with a fourth or fifth leap at the top of
            // the phrase — the gesture that makes a march sound like a hymn.
            int[] rhythm = [1, 1, 1, 1];
            int position = 0;

            // Three contours, chosen per bar, keep a long march from being one
            // repeated phrase while staying inside the idiom.
            int contour = rng.NextInt(0, 3);

            for (int step = 0; step < rhythm.Length; step++)
            {
                double start = barStart + (position * beat);
                double length = rhythm[step] * beat * (step == 3 ? 1.4 : 0.9);

                int leap = step == 1 && rng.Chance(1, 2) ? 3 : (step == 2 ? -1 : 1);
                int melodicDegree = degree + ((step * 2) % 5) + leap + contour;

                int note = Root + Scales.Semitones(scale, melodicDegree);
                synth.AddNote(mix, Seconds(start), length, note, 0.17f, Waveform.Pulse, Envelope.Brass, duty: 0.25);
                notes++;

                // A quiet harmony a third below, as a second horn.
                if (step % 2 == 0)
                {
                    synth.AddNote(mix, Seconds(start), length, Root + Scales.Semitones(scale, melodicDegree - 2),
                        0.08f, Waveform.Pulse, Envelope.Brass, duty: 0.5);
                    notes++;
                }

                position += rhythm[step];
            }
        }

        return new MusicInfo(FactionStyle.Soviet, "αρμονική ελάσσων", Root, BeatsPerMinute, seconds, notes);
    }

    /// <summary>
    /// A Chinese patriotic song: pentatonic, flowing, with grace notes and
    /// fourths in the accompaniment.
    /// </summary>
    private static MusicInfo ChineseSong(ChipSynth synth, float[] mix, Pcg32 rng, double seconds)
    {
        const int Root = 62;          // D4, gong mode
        const int BeatsPerMinute = 84;
        int[] scale = Scales.PentatonicMajor;
        double beat = 60d / BeatsPerMinute;
        int beats = (int)(seconds / beat);
        int notes = 0;

        // Pentatonic harmony moves in fourths and fifths: I, V, ii, vi-ish.
        int[] progression = [0, 4, 1, 3];

        for (int bar = 0; bar < beats / 4; bar++)
        {
            double barStart = bar * 4 * beat;
            int degree = progression[bar % progression.Length];

            // Plucked accompaniment: root and fifth on every beat, an octave apart.
            for (int beatIndex = 0; beatIndex < 4; beatIndex++)
            {
                double start = barStart + (beatIndex * beat);
                int accent = beatIndex == 0 || beatIndex == 2 ? 1 : 0;

                synth.AddNote(mix, Seconds(start), beat * 0.8, Root + Scales.Semitones(scale, degree) - 12,
                    (0.16f + (accent * 0.06f)), Waveform.Pulse, Envelope.Pluck, duty: 0.125);
                synth.AddNote(mix, Seconds(start + (beat * 0.5)), beat * 0.4,
                    Root + Scales.Semitones(scale, degree + 2) - 12, 0.09f, Waveform.Pulse, Envelope.Pluck, duty: 0.125);
                notes += 2;
            }

            // Light percussion: a tick on each beat, nothing martial.
            synth.AddDrum(mix, Seconds(barStart), DrumKind.Hat, 0.10f);
            synth.AddDrum(mix, Seconds(barStart + (2 * beat)), DrumKind.Hat, 0.08f);

            // Flute-like melody: long notes, stepwise motion, a grace note before
            // each phrase peak.
            int position = 0;

            while (position < 4)
            {
                int length = rng.Chance(1, 2) ? 1 : 2;

                if (position + length > 4)
                {
                    length = 4 - position;
                }

                int melodicDegree = degree + rng.NextInt(0, 5) + (position >= 2 ? 2 : 0);
                int note = Root + Scales.Semitones(scale, melodicDegree);
                double start = barStart + (position * beat);

                if (rng.Chance(1, 3))
                {
                    // Appoggiatura from the note above, the classic ornament.
                    synth.AddNote(mix, Seconds(start - (beat * 0.18)), beat * 0.18,
                        Root + Scales.Semitones(scale, melodicDegree + 1), 0.10f, Waveform.Triangle, Envelope.Pluck);
                    notes++;
                }

                synth.AddNote(mix, Seconds(start), length * beat * 0.95, note,
                    0.19f, Waveform.Triangle, Envelope.Organ, vibrato: 0.004d);
                notes++;

                position += length;
            }
        }

        return new MusicInfo(FactionStyle.Chinese, "πεντατονική", Root, BeatsPerMinute, seconds, notes);
    }

    /// <summary>
    /// Cheap Western rock'n'roll: twelve-bar blues, boogie bass, backbeat, and a
    /// bubblegum hook on top. Detuned, because it is cheap.
    /// </summary>
    private static MusicInfo WesternRock(ChipSynth synth, float[] mix, Pcg32 rng, double seconds)
    {
        const int Root = 60;          // C4
        const int BeatsPerMinute = 152;
        int[] scale = Scales.Blues;
        double beat = 60d / BeatsPerMinute;
        int beats = (int)(seconds / beat);
        int notes = 0;

        // Twelve-bar blues in scale degrees: I, IV and V.
        int[] progression = [0, 0, 0, 0, 3, 3, 0, 0, 4, 3, 0, 4];

        for (int bar = 0; bar < beats / 4; bar++)
        {
            double barStart = bar * 4 * beat;
            int degree = progression[bar % progression.Length];

            // Boogie-woogie left hand: root, third, fifth, sixth, flat seventh,
            // sixth, fifth, third on eighths.
            int[] boogie = [0, 2, 4, 5, 6, 5, 4, 2];

            for (int eighth = 0; eighth < 8; eighth++)
            {
                int step = Scales.Semitones(scale, degree + boogie[eighth]);
                double start = barStart + (eighth * beat * 0.5);

                synth.AddNote(mix, Seconds(start), beat * 0.42, Root + step - 24, 0.26f, Waveform.Triangle, Envelope.Pluck);
                notes++;
            }

            // Backbeat: kick on one and three, snare on two and four, hats on
            // eighths, swung slightly late on the offbeat.
            synth.AddDrum(mix, Seconds(barStart), DrumKind.Kick, 0.32f);
            synth.AddDrum(mix, Seconds(barStart + (2 * beat)), DrumKind.Kick, 0.28f);
            synth.AddDrum(mix, Seconds(barStart + beat), DrumKind.Snare, 0.26f);
            synth.AddDrum(mix, Seconds(barStart + (3 * beat)), DrumKind.Snare, 0.26f);

            for (int eighth = 0; eighth < 8; eighth++)
            {
                synth.AddDrum(mix, Seconds(barStart + (eighth * beat * 0.5)), DrumKind.Hat, 0.07f);
            }

            // Two detuned saws a fifth apart: a cheap electric guitar.
            for (int eighth = 0; eighth < 8; eighth += 2)
            {
                double start = barStart + (eighth * beat * 0.5);
                int power = Root + Scales.Semitones(scale, degree) - 12;

                synth.AddNote(mix, Seconds(start), beat * 0.45, power, 0.10f, Waveform.Saw, Envelope.Pluck, detuneCents: -14d);
                synth.AddNote(mix, Seconds(start), beat * 0.45, power + 7, 0.09f, Waveform.Saw, Envelope.Pluck, detuneCents: 16d);
                notes += 2;
            }

            // Bubblegum hook: a short repeated motif on the upper blues notes,
            // transposed per bar by the seed so six choruses are not one chorus.
            int hookShift = rng.NextInt(0, 3);

            for (int step = 0; step < 4; step++)
            {
                int melodicDegree = degree + 4 + ((step + hookShift) % 3);
                int note = Root + Scales.Semitones(scale, melodicDegree);
                double start = barStart + (step * beat);

                synth.AddNote(mix, Seconds(start), beat * 0.7, note, 0.13f, Waveform.Pulse,
                    Envelope.Pluck, duty: 0.5, vibrato: 0.01d, detuneCents: 6d);
                notes++;
            }

            // A drum fill every fourth bar, and only sometimes: a band that fills
            // every time is a drum machine.
            if (bar % 4 == 3 && rng.Chance(1, 2))
            {
                for (int sixteenth = 0; sixteenth < 4; sixteenth++)
                {
                    synth.AddDrum(mix, Seconds(barStart + (3 * beat) + (sixteenth * beat * 0.25)),
                        sixteenth % 2 == 0 ? DrumKind.Snare : DrumKind.Kick, 0.14f);
                }
            }
        }

        return new MusicInfo(FactionStyle.Western, "μπλουζ", Root, BeatsPerMinute, seconds, notes);
    }

    private static int Seconds(double seconds) => (int)(seconds * SampleRate);

    /// <summary>
    /// Scales the mix so it uses the full range without clipping. Normalising at
    /// the end rather than per voice keeps the balance the composition intended.
    /// </summary>
    private static void Normalise(float[] mix)
    {
        float peak = 0f;

        foreach (float sample in mix)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak <= 0.0001f)
        {
            return;
        }

        float gain = 0.9f / peak;

        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] *= gain;
        }
    }
}
