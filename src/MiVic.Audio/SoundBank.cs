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

    /// <summary>A structure's construction finished: rivets, then the ready chime.</summary>
    ConstructionComplete = 10,

    /// <summary>A factory rolled a vehicle off the line: ignition, then the rev.</summary>
    UnitComplete = 11,

    /// <summary>The tactical nuke: a crack, a roar, and a sub the ground carries.</summary>
    NuclearDetonation = 12,

    /// <summary>A crossing's last block laid: rivets going in, then the low resolve.</summary>
    BridgeComplete = 13,
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
    // The sound bank's own rate: 22 kHz, the era's sample rate, kept independently of the
// soundtrack — the soundtrack is bit-music now and runs at its own 8 kHz.
public const int SampleRate = 22050;

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
            SoundEffectKind.ExplosionSmall => Explosion(rng, 0.7, 90d),
            SoundEffectKind.ExplosionLarge => Explosion(rng, 2.1, 55d),
            SoundEffectKind.UiClick => UiClick(),
            SoundEffectKind.Alarm => Alarm(),
            SoundEffectKind.Impact => Impact(rng),
            SoundEffectKind.ConstructionComplete => ConstructionComplete(),
            SoundEffectKind.UnitComplete => UnitComplete(),
            SoundEffectKind.NuclearDetonation => NuclearDetonation(rng),
            SoundEffectKind.BridgeComplete => BridgeComplete(),
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
        SoundEffectKind.RifleShot => 0.16,
        SoundEffectKind.TankGun => 0.55,
        SoundEffectKind.ArtilleryLaunch => 0.62,
        SoundEffectKind.AntiAirBurst => 0.28,
        SoundEffectKind.ExplosionSmall => 0.7,
        SoundEffectKind.ExplosionLarge => 2.1,
        SoundEffectKind.UiClick => 0.06,
        SoundEffectKind.Alarm => 0.9,
        SoundEffectKind.Impact => 0.16,
        SoundEffectKind.ConstructionComplete => 0.75,
        SoundEffectKind.UnitComplete => 1.15,
        SoundEffectKind.NuclearDetonation => 3.2,
        SoundEffectKind.BridgeComplete => 1.1,
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

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;

            // Three layers, because a rifle report is one *event*, not a click: the
            // muzzle's crack (a fast decayed noise, the part that stings), the crack's
            // own pitch body dropping as the gas expands, and the low thump the
            // stock carries back to the shoulder. A click alone reads as a door latch.
            double crack = Noise(rng) * Math.Exp(-t * 150d) * 1.2d;
            double pitch = Math.Sin(2d * Math.PI * ((700d * Math.Exp(-t * 9d)) + 110d) * t) * Math.Exp(-t * 55d);
            double thump = Math.Sin(2d * Math.PI * 78d * t) * Math.Exp(-t * 40d) * 0.55d;
            double click = Math.Sin(2d * Math.PI * 900d * t) * Math.Exp(-t * 400d) * 0.5d;

            buffer[i] = (float)(crack + (pitch * 0.8d) + thump + click);
        }

        return buffer;
    }

    private static float[] TankGun(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.TankGun))];
        double phase = 0d;
        double subPhase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / Duration(SoundEffectKind.TankGun);

            // The barrel note drops from a bark to a thud; the sub carries the report
            // into the chest, which is the part a small speaker loses and the part
            // that made the old take feel thin.
            double frequency = 200d - (140d * progress);
            phase += frequency / SampleRate;

            double body = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 6d);
            double sub = Math.Sin(2d * Math.PI * subPhase) * Math.Exp(-t * 3.5d) * 0.85d;
            subPhase += 52d / SampleRate;

            double blast = Noise(rng) * Math.Exp(-t * 22d);
            double slap = Noise(rng) * Noise(rng) * Math.Exp(-t * 10d) * 0.5d;

            buffer[i] = (float)((body * 0.85d) + sub + (blast * 0.8d) + slap);
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
            // firecracker and a building coming down. The opening crack is the blast
            // front — the first fifth of the tail carries the sharp part, then the
            // roar owns the rest — and the ground's answer is a second, lower noise
            // wave the listener feels rather than hears.
            double frequency = subFrequency * (1d - (0.6d * progress));
            phase += frequency / SampleRate;

            double crack = Noise(rng) * Math.Exp(-t * (60d / seconds)) * 1.4d;
            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * (3.5d / seconds));
            double noise = Noise(rng) * Math.Exp(-t * (5.5d / seconds)) * 0.9d;
            double rumble = Noise(rng) * Noise(rng) * Math.Exp(-t * (1.8d / seconds)) * 0.55d;
            double crackle = Noise(rng) * Noise(rng) * Math.Exp(-t * (2.2d / seconds)) * 0.35d;

            buffer[i] = (float)(crack + (sub * 0.8d) + noise + rumble + crackle);
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

    /// <summary>
    /// A structure's construction finished: three rivet strikes walking the frame, then
    /// the two-note chime the machine raises when it is ready to work. The knocks come
    /// first because the builders sign the work; the chime is for the player who is not
    /// looking at the building when it finishes.
    /// </summary>
    private static float[] ConstructionComplete()
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.ConstructionComplete))];
        var rng = new Pcg32(0x4A11_7E00_0001_0001UL);

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double sum = 0d;

            for (int strike = 0; strike < 3; strike++)
            {
                double at = strike * 0.13d;

                if (t < at)
                {
                    continue;
                }

                double local = t - at;
                double ring = (Math.Sin(2d * Math.PI * (1450d + (strike * 190d)) * local) * 0.45d) +
                    (Math.Sin(2d * Math.PI * (2180d + (strike * 260d)) * local) * 0.3d);

                sum += (ring + (Noise(rng) * Math.Exp(-local * 180d) * 0.7d)) * Math.Exp(-local * 26d);
            }

            // The chime: two rising fifths, the machine saying "ready".
            double chime = 0d;

            if (t > 0.42d)
            {
                double c = t - 0.42d;
                double note = c < 0.16d ? 660d : 990d;
                chime = Math.Sin(2d * Math.PI * note * c) * Math.Exp(-c * 9d) * 0.5d;
            }

            buffer[i] = (float)(sum + chime);
        }

        return buffer;
    }

    /// <summary>
    /// A factory rolled a vehicle off the line: the starter cranks, catches, and the
    /// engine revs up. The crank is what sells it — three low chuffs before the rise,
    /// the shape every cold engine has.
    /// </summary>
    private static float[] UnitComplete()
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.UnitComplete))];
        var rng = new Pcg32(0x6E0C_7A7E_5A1F_0001UL);
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double sum;

            if (t < 0.42d)
            {
                // The crank: three chuffs a fraction of a second apart, each a noise
                // burst against the body's own low note.
                double local = t % 0.14d;
                double pulse = Math.Exp(-local * 60d);
                sum = (Noise(rng) * pulse * 0.7d) + (Math.Sin(2d * Math.PI * 85d * t) * pulse * 0.5d);
            }
            else
            {
                // The catch and the rev: the saw's rate climbs from the idle to the
                // working engine, and the noise floor rises with it.
                double up = Math.Min(1d, (t - 0.42d) / 0.7d);
                double rate = 70d + (160d * up);
                phase += rate / SampleRate;

                double saw = (2d * phase) - 1d;
                sum = (saw * (0.4d + (0.5d * up))) + (Noise(rng) * 0.22d);
            }

            double gate = Math.Min(1d, t * 220d) *
                Math.Min(1d, Math.Max(0d, (Duration(SoundEffectKind.UnitComplete) - t) * 30d));

            buffer[i] = (float)(sum * gate);
        }

        return buffer;
    }

    /// <summary>
    /// The tactical nuke: the blast front's crack, the roar that owns the first second
    /// and a half, and a sub the drop of which the ground carries for seconds after.
    /// An explosion the size of a building shares its palette; this one owns a register
    /// nothing else on the field reaches into.
    /// </summary>
    private static float[] NuclearDetonation(Pcg32 rng)
    {
        double seconds = Duration(SoundEffectKind.NuclearDetonation);
        float[] buffer = new float[Samples(seconds)];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / seconds;

            // The sub drops from the explosion's own register to a floor no other sound
            // in the game visits, and stays there — the long tail is what a detonation
            // has that an explosion does not.
            double frequency = 55d - (40d * Math.Min(1d, progress * 1.4d));
            phase += frequency / SampleRate;

            double crack = Noise(rng) * Math.Exp(-t * 110d) * 1.6d;
            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 0.8d) * 1.2d;
            double roar = Noise(rng) * Math.Exp(-t * 2.2d);
            double rumble = Noise(rng) * Noise(rng) * Math.Exp(-t * 0.7d) * 0.8d;

            buffer[i] = (float)(crack + sub + roar + rumble);
        }

        return buffer;
    }

    /// <summary>
    /// A crossing's last block laid: the rivet gun signs the deck with three strikes
    /// walking the span, and a low resolve says the work is whole.
    /// </summary>
    private static float[] BridgeComplete()
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.BridgeComplete))];
        var rng = new Pcg32(0x0E1A_CEB_0055_0002UL);

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double sum = 0d;

            for (int strike = 0; strike < 3; strike++)
            {
                double at = strike * 0.11d;

                if (t < at)
                {
                    continue;
                }

                double local = t - at;
                double strikeFrequency = 900d - (strike * 180d);
                double ring = Math.Sin(2d * Math.PI * strikeFrequency * local) * 0.4d;

                sum += (ring + (Noise(rng) * Math.Exp(-local * 140d) * 0.8d)) * Math.Exp(-local * 22d);
            }

            // The resolve: the deck's own note, a low round tone under the whole span.
            double resolve = 0d;

            if (t > 0.4d)
            {
                double r = t - 0.4d;
                resolve = Math.Sin(2d * Math.PI * 96d * r) * Math.Exp(-r * 6d) * 0.6d;
            }

            buffer[i] = (float)(sum + resolve);
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
