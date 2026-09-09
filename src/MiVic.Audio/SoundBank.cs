using MiVic.Core.Random;

namespace MiVic.Audio;

/// <summary>A procedurally generated sound effect.</summary>
public enum SoundEffectKind : byte
{
    /// <summary>Infantry small arms: a short crack.</summary>
    RifleShot = 0,

    /// <summary>Tank main gun: a hard, low report.</summary>
    TankGun = 1,

    /// <summary>Artillery: deeper, longer, with a rumble tail.</summary>
    ArtilleryLaunch = 2,

    /// <summary>Anti-air: a rapid triple burst.</summary>
    AntiAirBurst = 3,

    /// <summary>An infantry-sized unit or a vehicle going up.</summary>
    ExplosionSmall = 4,

    /// <summary>A structure collapsing: layered noise and a sub sweep.</summary>
    ExplosionLarge = 5,

    /// <summary>Interface click.</summary>
    UiClick = 6,

    /// <summary>Two-tone alarm, for a base under attack.</summary>
    Alarm = 7,

    /// <summary>Looping engine rumble.</summary>
    EngineLoop = 8,

    /// <summary>A round striking armour or ground: a short metallic clang.</summary>
    Impact = 9,
}

/// <summary>
/// Generates weapon, explosion and interface sounds from noise and simple
/// oscillators.
/// <para>
/// Same reasoning as the music: an early-80s machine had one noise channel and
/// one or two tone channels, so a convincing "tank firing" is a pitched sweep
/// plus a noise burst, not a recording. The result is short, cheap, and exactly
/// the kind of sound this game's look implies. Everything is a pure function of
/// (kind, seed), so it is testable and reproducible.
/// </para>
/// </summary>
public static class SoundBank
{
    /// <summary>Sample rate of generated effects.</summary>
    public const int SampleRate = MusicGenerator.SampleRate;

    /// <summary>Renders one effect as a mono float buffer in [-1, 1].</summary>
    public static float[] Generate(SoundEffectKind kind, ulong seed)
    {
        var rng = new Pcg32(seed ^ (((ulong)kind + 1UL) * 0x9E37_79B9_7F4A_7C15UL));

        float[] buffer = kind switch
        {
            SoundEffectKind.RifleShot => RifleShot(rng),
            SoundEffectKind.TankGun => TankGun(rng),
            SoundEffectKind.ArtilleryLaunch => ArtilleryLaunch(rng),
            SoundEffectKind.AntiAirBurst => AntiAirBurst(rng),
            SoundEffectKind.ExplosionSmall => Explosion(rng, 0.55, 90d),
            SoundEffectKind.ExplosionLarge => Explosion(rng, 1.7, 55d),
            SoundEffectKind.UiClick => UiClick(),
            SoundEffectKind.Alarm => Alarm(),
            SoundEffectKind.Impact => Impact(rng),
            _ => EngineLoop(rng),
        };

        Normalise(buffer, 0.85f);
        return buffer;
    }

    /// <summary>Renders one effect as 16-bit PCM.</summary>
    public static short[] GeneratePcm16(SoundEffectKind kind, ulong seed)
    {
        float[] samples = Generate(kind, seed);
        var pcm = new short[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            pcm[i] = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
        }

        return pcm;
    }

    /// <summary>How long an effect lasts, in seconds.</summary>
    public static double Duration(SoundEffectKind kind) => kind switch
    {
        SoundEffectKind.RifleShot => 0.09,
        SoundEffectKind.TankGun => 0.38,
        SoundEffectKind.ArtilleryLaunch => 0.62,
        SoundEffectKind.AntiAirBurst => 0.28,
        SoundEffectKind.ExplosionSmall => 0.55,
        SoundEffectKind.ExplosionLarge => 1.7,
        SoundEffectKind.UiClick => 0.06,
        SoundEffectKind.Alarm => 0.9,
        SoundEffectKind.Impact => 0.16,
        _ => 1d,
    };

    /// <summary>
    /// A round hitting armour: two detuned high partials ringing against a noise
    /// crack. Metal is bright and short, which is why this is nothing like the
    /// explosion.
    /// </summary>
    private static float[] Impact(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.Impact))];

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double decay = Math.Exp(-t * 70d);

            double ring = (Math.Sin(2d * Math.PI * 1150d * t) * 0.5d) +
                          (Math.Sin(2d * Math.PI * 1730d * t) * 0.35d);

            buffer[i] = (float)((ring * decay) + (Noise(rng) * Math.Exp(-t * 220d) * 0.7d));
        }

        return buffer;
    }

    private static float[] RifleShot(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.RifleShot))];
        double decay = 55d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double envelope = Math.Exp(-t * decay);

            // A click plus a noise burst: the click gives it a muzzle, the noise
            // gives it a body.
            double click = Math.Sin(2d * Math.PI * 900d * t) * Math.Exp(-t * 400d);
            buffer[i] = (float)((Noise(rng) * 0.9d * envelope) + (click * 0.6d));
        }

        return buffer;
    }

    private static float[] TankGun(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.TankGun))];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / Duration(SoundEffectKind.TankGun);

            // The barrel note drops from a bark to a thud.
            double frequency = 200d - (140d * progress);
            phase += frequency / SampleRate;

            double body = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 7d);
            double blast = Noise(rng) * Math.Exp(-t * 26d);

            buffer[i] = (float)((body * 0.9d) + (blast * 0.7d));
        }

        return buffer;
    }

    private static float[] ArtilleryLaunch(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.ArtilleryLaunch))];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / Duration(SoundEffectKind.ArtilleryLaunch);

            double frequency = 130d - (90d * progress);
            phase += frequency / SampleRate;

            double body = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 4.5d);
            double rumble = Noise(rng) * Math.Exp(-t * 9d) * 0.8d;

            buffer[i] = (float)(body + rumble);
        }

        return buffer;
    }

    private static float[] AntiAirBurst(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.AntiAirBurst))];
        double decay = 120d;

        // Three quick reports, 70 ms apart.
        for (int shot = 0; shot < 3; shot++)
        {
            int offset = Samples(shot * 0.07d);

            for (int i = offset; i < buffer.Length; i++)
            {
                double t = (double)(i - offset) / SampleRate;
                double envelope = Math.Exp(-t * decay);
                buffer[i] += (float)(Noise(rng) * envelope * (0.85d - (shot * 0.15d)));
            }
        }

        return buffer;
    }

    private static float[] Explosion(Pcg32 rng, double seconds, double subFrequency)
    {
        float[] buffer = new float[Samples(seconds)];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / seconds;

            // A sub-bass drop under a long noise tail: the difference between a
            // firecracker and a building coming down.
            double frequency = subFrequency * (1d - (0.6d * progress));
            phase += frequency / SampleRate;

            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * (3.5d / seconds));
            double noise = Noise(rng) * Math.Exp(-t * (5.5d / seconds)) * 0.9d;
            double crackle = Noise(rng) * Noise(rng) * Math.Exp(-t * (2.2d / seconds)) * 0.35d;

            buffer[i] = (float)((sub * 0.8d) + noise + crackle);
        }

        return buffer;
    }

    private static float[] UiClick()
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.UiClick))];

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            buffer[i] = (float)(Math.Sin(2d * Math.PI * 1400d * t) * Math.Exp(-t * 260d));
        }

        return buffer;
    }

    private static float[] Alarm()
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.Alarm))];

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;

            // Two tones alternating twice a second, gated by a square envelope.
            double tone = Math.Sin(2d * Math.PI * (t % 0.5d < 0.25d ? 620d : 880d) * t);
            double gate = Math.Sin(2d * Math.PI * 2d * t) > 0d ? 1d : 0.15d;
            double fade = Math.Min(1d, Math.Min(t * 40d, (Duration(SoundEffectKind.Alarm) - t) * 40d));

            buffer[i] = (float)(tone * gate * fade * 0.8d);
        }

        return buffer;
    }

    private static float[] EngineLoop(Pcg32 rng)
    {
        float[] buffer = new float[Samples(1d)];
        double phase = 0d;

        // 63 Hz is 63 whole cycles in a one-second buffer, so the tone loops
        // without a discontinuity at the seam.
        const double Frequency = 63d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;

            // A slightly wobbly saw plus filtered noise reads as a diesel engine.
            phase += (Frequency * (1d + (0.02d * Math.Sin(2d * Math.PI * 7d * t)))) / SampleRate;

            if (phase >= 1d)
            {
                phase -= 1d;
            }

            double saw = (2d * phase) - 1d;
            buffer[i] = (float)((saw * 0.55d) + (Noise(rng) * 0.25d));
        }

        // Crossfade the ends so the loop point is inaudible.
        int fade = Samples(0.05d);

        for (int i = 0; i < fade; i++)
        {
            double mix = (double)i / fade;
            int tail = buffer.Length - fade + i;

            buffer[i] = (float)((buffer[i] * mix) + (buffer[tail] * (1d - mix)));
        }

        return buffer;
    }

    private static int Samples(double seconds) => Math.Max(1, (int)(seconds * SampleRate));

    /// <summary>White noise in [-1, 1] from the generator.</summary>
    private static double Noise(Pcg32 rng) => ((rng.NextUInt() / (double)uint.MaxValue) * 2d) - 1d;

    private static void Normalise(float[] buffer, float peakTarget)
    {
        float peak = 0f;

        foreach (float sample in buffer)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak <= 0.0001f)
        {
            return;
        }

        float gain = peakTarget / peak;

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= gain;
        }
    }
}
