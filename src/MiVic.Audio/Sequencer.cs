namespace MiVic.Audio;

/// <summary>
/// Renders a score: patterns into bars, bars into leitmotivs, leitmotivs into PCM.
/// <para>
/// <b>The render is a whole multiple of the bar, always.</b> A leitmotiv of four bars is a
/// buffer exactly four bars long, and a note that rings past the boundary wraps into the loop's
/// own start — a sustain chord held over the edge is what the loop plays again, which is why the
/// loop is seamless by construction rather than by luck. The bytebeat earned this lesson with a
/// macro-period; a bar-quantised score gets it for free and owes it to the same listener.
/// </para>
/// <para>
/// The kit's hits are placed by step; the pitched voices are placed by step and ring for their
/// length. Nothing here reads a clock: a render is a pure function of the score, so the same
/// file is the same music forever, and a test may hash it.
/// </para>
/// </summary>
public static class Sequencer
{
    /// <summary>Renders one leitmotiv to PCM at the sound bank's rate.</summary>
    public static short[] RenderLeitmotiv(Score score, Leitmotiv leitmotiv)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(leitmotiv);

        int samplesPerStep = SamplesPerStep(score);
        int totalSamples = score.Grid * leitmotiv.Bars * samplesPerStep;
        var mix = new float[totalSamples];

        foreach (ScorePattern pattern in leitmotiv.Patterns)
        {
            RenderKit(pattern.Kit, mix, samplesPerStep, totalSamples, score.Grid, leitmotiv.Bars);
            RenderPitched(score, pattern.Pitched, mix, samplesPerStep, totalSamples, score.Grid, leitmotiv.Bars);
        }

        return ToPcm(mix);
    }

    /// <summary>Renders one fill bar.</summary>
    public static short[] RenderFill(Score score, ScoreFill fill)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(fill);

        int samplesPerStep = SamplesPerStep(score);
        int totalSamples = score.Grid * samplesPerStep;
        var mix = new float[totalSamples];

        RenderKit(fill.Kit, mix, samplesPerStep, totalSamples, score.Grid, bars: 1);

        return ToPcm(mix);
    }

    private static int SamplesPerStep(Score score) => (int)(score.SecondsPerStep * SoundBank.SampleRate);

    private static void RenderKit(
        KitHit[][] voices,
        float[] mix,
        int samplesPerStep,
        int totalSamples,
        int grid,
        int bars)
    {
        for (int voice = 0; voice < voices.Length; voice++)
        {
            var kind = (KitVoice)voice;
            double seconds = VoiceSeconds(kind);

            foreach (KitHit hit in voices[voice])
            {
                float[] sample = Instruments.Kit(kind, hit.Accent ? 1f : Score.NormalVelocity, seconds);

                // The pattern is one bar; the leitmotiv repeats it for every bar it declares.
                for (int bar = 0; bar < bars; bar++)
                {
                    int start = ((bar * grid) + hit.Step) * samplesPerStep;

                    for (int i = 0; i < sample.Length; i++)
                    {
                        // The tail wraps: a hat still ringing at the bar's edge is still ringing
                        // at the loop's first step, which is what a real room does with a loop.
                        int index = (start + i) % totalSamples;
                        mix[index] += sample[i];
                    }
                }
            }
        }
    }

    /// <summary>How long a kit voice's render runs for. A fill bar must contain the whole hit.</summary>
    private static double VoiceSeconds(KitVoice voice) => voice switch
    {
        KitVoice.HiHatClosed => 0.09d,
        KitVoice.HiHatOpen => 0.5d,
        KitVoice.Tambourine => 0.3d,
        KitVoice.BassDrum => 0.45d,
        KitVoice.Snare => 0.3d,
        KitVoice.Tom1 or KitVoice.Tom2 => 0.45d,
        _ => 0.3d,
    };

    private static void RenderPitched(
        Score score,
        PitchedVoice[] voices,
        float[] mix,
        int samplesPerStep,
        int totalSamples,
        int grid,
        int bars)
    {
        foreach (PitchedVoice voice in voices)
        {
            foreach (PitchedEvent note in voice.Events)
            {
                double seconds = note.Length * score.SecondsPerStep;

                foreach (int degree in note.Degrees)
                {
                    int semitones = Scales.Semitones(score.Scale, degree) + (12 * note.Octave);
                    double frequency = Scales.Frequency(score.Root + semitones);

                    float[] sample = voice.Family switch
                    {
                        "bass" => PitchedInstruments.Bass(frequency, seconds),
                        "strings" => PitchedInstruments.Strings(frequency, seconds),
                        _ => PitchedInstruments.Horns(frequency, seconds),
                    };

                    float share = 1f / note.Degrees.Length;

                    // The pattern is one bar; the leitmotiv repeats it for every bar it
                    // declares, and a note held past the loop's edge wraps into its start.
                    for (int bar = 0; bar < bars; bar++)
                    {
                        int start = ((bar * grid) + note.Step) * samplesPerStep;

                        for (int i = 0; i < sample.Length; i++)
                        {
                            int index = (start + i) % totalSamples;
                            mix[index] += sample[i] * share;
                        }
                    }
                }
            }
        }
    }

    private static short[] ToPcm(float[] mix)
    {
        var pcm = new short[mix.Length];

        for (int i = 0; i < mix.Length; i++)
        {
            // A soft clip rather than a hard one: a section landing on the same beat as the
            // bass is louder than either alone, and a clipped corner is a click the ear hears
            // as a fault. tanh keeps the wave's shape while keeping the peak honest.
            double shaped = Math.Tanh(mix[i] * 1.1d);
            pcm[i] = (short)Math.Clamp(shaped * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return pcm;
    }
}
