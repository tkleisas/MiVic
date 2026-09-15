using MiVic.Audio;

namespace MiVic.Audio;

/// <summary>
/// The seven kit voices and three pitched families, synthesised.
/// <para>
/// Every voice is a function of its own physics and nothing else: no samples, no textures, no
/// seed — the same instruments for every faction, and the factions differ by what they play on
/// them. A kick is a membrane dropping pitch; a snare is a shell resonating under a stream of
/// wires; a hi-hat is a stack of metal rings, which is why its sound is a stack of inharmonic
/// oscillators through a high-pass and not a filtered noise burst. A synth bass is a saw with a
/// filter that closes as the note ages; strings are an ensemble of slightly-detuned saws; horns
/// are a square with a bite on the attack, in unison, because a solo horn is a horn and a section
/// is an instrument.
/// </para>
/// </summary>
public static class Instruments
{
    /// <summary>Kit voices and their envelopes, one sampler per voice.</summary>
    public static float[] Kit(KitVoice voice, float velocity, double seconds)
    {
        int samples = (int)(seconds * SoundBank.SampleRate);
        return voice switch
        {
            KitVoice.BassDrum => BassDrum(velocity, samples),
            KitVoice.Snare => Snare(velocity, samples),
            KitVoice.Tom1 => Tom(velocity, samples, 210d, 130d),
            KitVoice.Tom2 => Tom(velocity, samples, 150d, 88d),
            KitVoice.HiHatClosed => HiHat(velocity, samples, 0.055d),
            KitVoice.HiHatOpen => HiHat(velocity, samples, 0.42d),
            KitVoice.Tambourine => Tambourine(velocity, samples),
            _ => new float[samples],
        };
    }

    /// <summary>
    /// A kick: the pitch of a membrane dropped from above, 110 Hz falling to 45 over the first
    /// eighth of a second, with the beater's click riding the first ten milliseconds. The sweep
    /// is what separates a drum from a bass note — a bass note that fell in pitch would be a
    /// mistake; a drum that does not is a bounce.
    /// </summary>
    private static float[] BassDrum(float velocity, int samples)
    {
        var buffer = new float[samples];
        double phase = 0d;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SoundBank.SampleRate;
            double pitch = 45d + (65d * Math.Exp(-t / 0.08d));
            double decay = Math.Exp(-t / 0.13d);
            phase += 2d * Math.PI * pitch / SoundBank.SampleRate;

            double body = Math.Sin(phase) * decay;
            double click = Math.Exp(-t / 0.006d) * 0.55d;
            buffer[i] = (float)((body * 0.85d) + click) * velocity;
        }

        return buffer;
    }

    /// <summary>
    /// A snare: the shell's two tones under the sizzle of wires across the bottom head. The
    /// tones die first — a tenth of a second — and the wires ring after them, which is why a
    /// snare hit is one sound the ear separates into two.
    /// </summary>
    private static float[] Snare(float velocity, int samples)
    {
        var buffer = new float[samples];
        var noise = new PcgNoise();

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SoundBank.SampleRate;
            double shell = Math.Exp(-t / 0.045d) * 0.5d;
            double tone = (Math.Sin(2d * Math.PI * 185d * t) + (0.7d * Math.Sin(2d * Math.PI * 330d * t))) * shell;
            double wires = noise.NextSigned() * Math.Exp(-t / 0.09d) * 0.9d;
            double attack = Math.Exp(-t / 0.002d) * 0.4d;

            buffer[i] = (float)(tone + wires + attack);
        }

        return Scale(buffer, velocity);
    }

    /// <summary>A tom: the same membrane the kick has, pitched and tuned to the kit's shell.</summary>
    private static float[] Tom(float velocity, int samples, double fromHz, double toHz)
    {
        var buffer = new float[samples];
        double phase = 0d;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SoundBank.SampleRate;
            double pitch = toHz + ((fromHz - toHz) * Math.Exp(-t / 0.09d));
            double decay = Math.Exp(-t / 0.115d);
            phase += 2d * Math.PI * pitch / SoundBank.SampleRate;

            buffer[i] = (float)Math.Sin(phase) * (float)decay;
        }

        return Scale(buffer, velocity);
    }

    /// <summary>
    /// A hi-hat: a clutch of cymbal rings, each its own inharmonic square, through a first-order
    /// high-pass. Open and closed are the same stack with the same decay law and a different
    /// time constant — a closed hat is the pedal down, an open one is the foot off it.
    /// </summary>
    private static float[] HiHat(float velocity, int samples, double seconds)
    {
        var buffer = new float[samples];

        // The 808's own ratios: six squares that never agree on a period, which is what a
        // cymbal's clang is.
        double[] ratios = [2d, 3d, 4.16d, 5.43d, 6.79d, 8.21d];
        double fundamental = 40d;
        double decaySeconds = seconds / 5d;
        double previousIn = 0d;
        double previousOut = 0d;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SoundBank.SampleRate;
            double sum = 0d;

            foreach (double ratio in ratios)
            {
                double square = Math.Sin(2d * Math.PI * fundamental * ratio * t) >= 0d ? 1d : -1d;
                sum += square;
            }

            double env = Math.Exp(-t / decaySeconds);
            double raw = (sum / ratios.Length) * env;

            // One-pole high-pass: what passes is the clang's top, which is the part a hat
            // carries; what a low-pass would keep is the racket of six square waves, and that
            // is not a hat.
            double passed = 0.92d * (previousOut + raw - previousIn);
            previousIn = raw;
            previousOut = passed;

            buffer[i] = (float)(passed * 3.2d);
        }

        return Scale(buffer, velocity);
    }

    /// <summary>
    /// A tambourine: two jingles a hand's width apart in time, each a band of metal ringing —
    /// which is band-passed noise with a high centre, not a filtered hiss.
    /// </summary>
    private static float[] Tambourine(float velocity, int samples)
    {
        var buffer = new float[samples];
        var noise = new PcgNoise();
        double y1 = 0d;
        double y2 = 0d;

        // A resonator at the jingle's ring: a sinewave that only answers what excites it at
        // its own frequency, which is what a struck metal disc is.
        double coeff = 2d * Math.Cos(2d * Math.PI * 7600d / SoundBank.SampleRate);

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SoundBank.SampleRate;

            // Two jingles: the hand's first shake, and its second, shorter and quieter.
            double env = Math.Exp(-t / 0.05d) + (0.45d * Math.Exp(-Math.Pow(t - 0.021d, 2d) / 0.000045d));
            double struck = noise.NextSigned() * env;

            double resonated = (struck * 0.6d) + (coeff * y1) - y2;
            y2 = y1;
            y1 = resonated * 0.9998d;

            buffer[i] = (float)(resonated * env * 0.4d);
        }

        return Scale(buffer, velocity);
    }

    /// <summary>Normalises a voice's peak to its velocity, so a pattern's accents are the mix.</summary>
    private static float[] Scale(float[] buffer, float velocity)
    {
        float peak = 0f;

        foreach (float sample in buffer)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        if (peak < 1e-6f)
        {
            return buffer;
        }

        float gain = 0.92f * velocity / peak;

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= gain;
        }

        return buffer;
    }
}

/// <summary>Which drum of the kit.</summary>
public enum KitVoice : byte
{
    BassDrum = 0,
    Snare = 1,
    Tom1 = 2,
    Tom2 = 3,
    HiHatClosed = 4,
    HiHatOpen = 5,
    Tambourine = 6,
}

/// <summary>Deterministic white noise, for the wires and the jingles.</summary>
public sealed class PcgNoise
{
    private ulong _state;

    /// <summary>Seeds the stream; the default is as good as any, and one is as good as another.</summary>
    public PcgNoise(ulong state = 0x853c49e6748fea9bUL)
    {
        _state = state;
    }

    /// <summary>The next sample, uniform over -1..1.</summary>
    public double NextSigned()
    {
        _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;

        // The top thirty-two bits, scaled by their own width. Taking thirty-three and
        // dividing by 2^32 — which is what this did — leaves a value in [0, 0.5), so the
        // "uniform over -1..1" sample could never be positive: the snare wires and the
        // tambourine jingle carried a permanent -0.5 offset and no positive excursion.
        ulong bits = _state >> 32;

        return (bits / (double)(1UL << 32) * 2d) - 1d;
    }
}
