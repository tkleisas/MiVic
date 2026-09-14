namespace MiVic.Audio;

/// <summary>
/// The three pitched families: synth bass, strings, horns.
/// <para>
/// These are not drums — a note has a pitch, a length and a shape over that length, so each
/// family renders a <em>note</em> and the sequencer places the render. All three are
/// subtractive at heart: a waveform rich enough to filter, a filter that moves, an envelope.
/// What separates them is the shape and the filter's temper — a bass is bright on the attack
/// and closes quickly, strings swell and hold, horns bite and soften.
/// </para>
/// </summary>
public static class PitchedInstruments
{
    /// <summary>
    /// The synth bass: a saw and a square an octave below it, through a one-pole filter whose
    /// cutoff rides the note's own envelope — open on the attack, closed by the body — so the
    /// note barks when it lands and purrs underneath the strings.
    /// </summary>
    public static float[] Bass(double frequency, double seconds, int sampleRate = SoundBank.SampleRate)
    {
        int length = (int)(seconds * sampleRate);
        var buffer = new float[length];
        double sawPhase = 0d;
        double squarePhase = 0d;
        double filterState = 0d;

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / sampleRate;

            sawPhase += 2d * Math.PI * frequency / sampleRate;
            squarePhase += Math.PI * frequency / sampleRate;

            double saw = 2d * ((sawPhase / (2d * Math.PI)) - Math.Floor(0.5d + (sawPhase / (2d * Math.PI))));
            double square = Math.Sin(squarePhase) >= 0d ? 0.5d : -0.5d;
            double raw = (0.62d * saw) + square;

            // The filter's ride: the cutoff opens over the first thirty milliseconds and falls
            // back over the next quarter of a second, which is the note's own shape, played
            // with a knob rather than with a bow.
            double open = Math.Min(1d, t / 0.03d);
            double cutoff = 180d + (2200d * open * Math.Exp(-t / 0.22d));
            double alpha = Math.Min(1d, cutoff * 2d * Math.PI / sampleRate);

            filterState += alpha * (raw - filterState);

            double tail = Math.Max(0d, Math.Min(1d, (seconds - t) / 0.05d));

            buffer[i] = (float)(filterState * tail * 0.55d);
        }

        return buffer;
    }

    /// <summary>
    /// The strings: an ensemble of three detuned saws through a gentle low-pass — wide enough
    /// to read as a section, narrow enough that the ensemble is one instrument. The swell is
    /// the point: strings do not begin, they arrive.
    /// </summary>
    public static float[] Strings(double frequency, double seconds, int sampleRate = SoundBank.SampleRate)
    {
        int length = (int)(seconds * sampleRate);
        var buffer = new float[length];

        // The detune is what makes an ensemble out of one oscillator: three saws a few cents
        // apart drift against each other, and the drift is the sheen.
        double[] detune = [0.9965d, 1d, 1.0035d];
        double[] phases = new double[detune.Length];
        double filterState = 0d;

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / sampleRate;

            double attack = Math.Min(1d, t / 0.22d);
            attack *= attack;
            double release = t > seconds - 0.4d ? Math.Max(0d, (seconds - t) / 0.4d) : 1d;
            double env = Math.Min(attack, release);

            double sum = 0d;

            for (int voice = 0; voice < detune.Length; voice++)
            {
                phases[voice] += 2d * Math.PI * frequency * detune[voice] / sampleRate;
                double phase = phases[voice];
                sum += 2d * ((phase / (2d * Math.PI)) - Math.Floor(0.5d + (phase / (2d * Math.PI))));
            }

            filterState += 0.35d * ((sum / detune.Length) - filterState);

            buffer[i] = (float)(filterState * env * 0.16d);
        }

        return buffer;
    }

    /// <summary>
    /// The horns: a section of two squares five cents apart, through a filter that opens on
    /// the attack — the bite a brass attack has — and settles to a mellow body; vibrato
    /// arrives late, as a player's does.
    /// </summary>
    public static float[] Horns(double frequency, double seconds, int sampleRate = SoundBank.SampleRate)
    {
        int length = (int)(seconds * sampleRate);
        var buffer = new float[length];
        double[] detune = [0.9995d, 1.0005d];
        double[] phases = new double[detune.Length];
        double filterState = 0d;

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / sampleRate;

            // The bite: the first fifty milliseconds let the square speak with the filter open,
            // then the cutoff closes to the body.
            double cutoff = 900d + (2400d * Math.Exp(-t / 0.12d));
            double vibrato = 1d + (0.004d * Math.Sin(2d * Math.PI * 5.2d * Math.Max(0d, t - 0.15d)));

            double sum = 0d;

            for (int voice = 0; voice < detune.Length; voice++)
            {
                phases[voice] += 2d * Math.PI * frequency * detune[voice] * vibrato / sampleRate;
                sum += Math.Sign(Math.Sin(phases[voice])) * 0.5d;
            }

            double alpha = Math.Min(1d, cutoff * 2d * Math.PI / sampleRate);
            filterState += alpha * ((sum / detune.Length) - filterState);

            double attack = Math.Min(1d, t / 0.045d);
            double release = t > seconds - 0.09d ? Math.Max(0d, (seconds - t) / 0.09d) : 1d;

            buffer[i] = (float)(filterState * attack * release * 0.30d);
        }

        return buffer;
    }
}
