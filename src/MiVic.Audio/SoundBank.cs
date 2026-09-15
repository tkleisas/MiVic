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
/// plus a noise burst, not a recording. Everything is a pure function of
/// (kind, take-seed), so it is testable and reproducible.
/// </para>
/// <para>
/// <b>The model, and what made the first one weak.</b> A shot is not a tone with a
/// decay and an explosion is not a tone with a longer decay — both are *noise shaped
/// by an envelope*, and the first bank shaped its noise with an exponential decay,
/// which is the shape a beep has: loud for a twentieth of a second and gone. The
/// sounds are built now on one envelope, the ADSR a studio would ask for — attack
/// measured in milliseconds, the body held at full, a release that runs for a
/// fraction of a second on a shot and for seconds on an explosion — over noise the
/// filters have already given the character to: a high-pass for the crack, a low-pass
/// for the body. Under all of it sits a small synthetic reverb, four comb filters
/// and two all-passes, because an open field does not answer a rifle with silence
/// and a dry crack is exactly what a beep is.
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
            SoundEffectKind.ExplosionSmall => Explosion(rng, Duration(SoundEffectKind.ExplosionSmall), 90d),
            SoundEffectKind.ExplosionLarge => Explosion(rng, Duration(SoundEffectKind.ExplosionLarge), 55d),
            SoundEffectKind.UiClick => UiClick(),
            SoundEffectKind.Alarm => Alarm(),
            SoundEffectKind.Impact => Impact(rng),
            SoundEffectKind.ConstructionComplete => ConstructionComplete(),
            SoundEffectKind.UnitComplete => UnitComplete(rng),
            SoundEffectKind.NuclearDetonation => NuclearDetonation(rng),
            SoundEffectKind.BridgeComplete => BridgeComplete(),
            _ => EngineLoop(rng),
        };

        // The room answers: the wet share is the kind's own — a rifle in an open field
        // has a slap, a detonation owns a tail, and a UI click lives in the player's
        // hand, not in a hall.
        buffer = Reverb(buffer, ReverbWet(kind));

        // The buffer ends where the kind says it does, and a reverb tail cut in the
        // middle of ringing would end the sound with a click; a short fade takes the
        // seam off every kind the same way.
        FadeOut(buffer, 0.012d);

        Normalise(buffer, 0.9f);
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
        SoundEffectKind.RifleShot => 0.28,
        SoundEffectKind.TankGun => 0.7,
        SoundEffectKind.ArtilleryLaunch => 0.75,
        SoundEffectKind.AntiAirBurst => 0.32,
        SoundEffectKind.ExplosionSmall => 0.8,
        SoundEffectKind.ExplosionLarge => 2.4,
        SoundEffectKind.UiClick => 0.06,
        SoundEffectKind.Alarm => 0.9,
        SoundEffectKind.Impact => 0.2,
        SoundEffectKind.ConstructionComplete => 0.75,
        SoundEffectKind.UnitComplete => 1.15,
        SoundEffectKind.NuclearDetonation => 3.2,
        SoundEffectKind.BridgeComplete => 1.1,
        _ => 1d,
    };

    // ------------------------------------------------------------------ the envelope

    /// <summary>
    /// The ADSR the whole bank is shaped by: fast attack, the body held at full, a
    /// release that runs for the rest. An exponential decay — what the first bank used —
    /// is a beep's shape; this one is the shape a physical event has, because the air
    /// the gun moved does not vanish, it runs out.
    /// </summary>
    private static double Envelope(double t, double attack, double hold, double release, double total)
    {
        if (t < attack)
        {
            return t / attack;
        }

        if (t < attack + hold)
        {
            return 1d;
        }

        // The release runs for as long as the kind's own length allows, clamped so a
        // long release on a short buffer is a release that ends when the buffer does.
        double releaseSpan = Math.Min(release, Math.Max(0d, total - attack - hold));
        double released = (t - attack - hold) / Math.Max(1e-9, releaseSpan);

        return Math.Max(0d, 1d - released);
    }

    // ------------------------------------------------------------------ the filters

    /// <summary>A one-pole low-pass, in place: <c>alpha</c> of the input per sample.</summary>
    private static void Lowpass(float[] buffer, double alpha)
    {
        double state = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            state += alpha * (buffer[i] - state);
            buffer[i] = (float)state;
        }
    }

    /// <summary>A one-pole high-pass, in place: the input minus what the low-pass keeps.</summary>
    private static void Highpass(float[] buffer, double alpha)
    {
        double lowState = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            lowState += alpha * (buffer[i] - lowState);
            buffer[i] -= (float)lowState;
        }
    }

    /// <summary>
    /// A small synthetic reverb: four comb filters at the classic spacings, then two
    /// all-passes to wash the early pattern out. Deterministic — fixed delays, fixed
    /// gains — so the same kind is the same room every run, and cheap: a few adds per
    /// sample over a buffer that is generated once.
    /// </summary>
    private static float[] Reverb(float[] buffer, double wet)
    {
        if (wet <= 0d || buffer.Length < 4)
        {
            return buffer;
        }

        int[] combDelays =
        [
            (int)(0.0297d * SampleRate),
            (int)(0.0371d * SampleRate),
            (int)(0.0411d * SampleRate),
            (int)(0.0437d * SampleRate),
        ];

        var combs = new float[combDelays.Length][];
        var combWrite = new int[combDelays.Length];

        for (int c = 0; c < combDelays.Length; c++)
        {
            combs[c] = new float[combDelays[c]];
        }

        int[] allpassDelays = [(int)(0.005d * SampleRate), (int)(0.0017d * SampleRate)];
        var allpasses = new float[allpassDelays.Length][];
        var allpassWrite = new int[allpassDelays.Length];

        for (int a = 0; a < allpassDelays.Length; a++)
        {
            allpasses[a] = new float[allpassDelays[a]];
        }

        const double Feedback = 0.774d;
        const double AllPassGain = 0.7d;

        var tail = new float[buffer.Length];

        for (int i = 0; i < buffer.Length; i++)
        {
            double sum = 0d;

            // The combs: a circular line of past outputs. Read the oldest, feed it
            // back with the input, write the sum where the oldest was.
            for (int c = 0; c < combs.Length; c++)
            {
                float[] line = combs[c];
                int write = combWrite[c];
                double delayed = line[write];

                line[write] = (float)(buffer[i] + (delayed * Feedback));
                combWrite[c] = (write + 1) % combDelays[c];

                sum += delayed;
            }

            double washed = sum / combs.Length;

            // The all-passes, in the form Freeverb uses: read the oldest output, the
            // new output is it minus the input, and the line takes input plus the
            // damped oldest back.
            for (int a = 0; a < allpasses.Length; a++)
            {
                float[] line = allpasses[a];
                int write = allpassWrite[a];
                double delayed = line[write];
                double output = (delayed - buffer[i]) * 0.7d;

                line[write] = (float)(buffer[i] + (delayed * 0.5d));
                allpassWrite[a] = (write + 1) % allpassDelays[a];

                washed = (washed * (1d - AllPassGain)) + (output * AllPassGain);
            }

            tail[i] = (float)washed;
        }

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)((buffer[i] * (1d - wet)) + (tail[i] * wet));
        }

        return buffer;
    }

    // ------------------------------------------------------------------ the weapons

    /// <summary>
    /// A rifle: a high-passed crack that stings, a low-passed body the muzzle blast
    /// carries, and the stock's thump. There is no sine in it — a sine is where the
    /// beep came from.
    /// </summary>
    private static float[] RifleShot(Pcg32 rng)
    {
        double seconds = Duration(SoundEffectKind.RifleShot);
        float[] buffer = new float[Samples(seconds)];

        double lowState = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double raw = Noise(ref rng);

            // One noise source, split in the loop: what the low-pass keeps is the
            // blast's body, on the envelope; what it leaves is the crack. A whole-buffer
            // high-pass would have taken the body with the ring, which is what left the
            // first cut as a spike and a hiss.
            lowState += 0.18d * (raw - lowState);
            double crack = (raw - lowState) * Math.Exp(-t * 95d) * 1.6d;
            double body = lowState * Envelope(t, 0.002d, 0.05d, seconds * 0.7d, seconds);
            double slap = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * 24d) * 0.45d;

            buffer[i] = (float)(crack + (body * 1.6d) + slap);
        }

        return buffer;
    }

    /// <summary>
    /// A tank gun: the report is a low-passed blast on the ADSR with a sub that the
    /// ground carries — the barrel note the first bank swept was the beep, and the
    /// sweep is what the tone of a real report never does.
    /// </summary>
    private static float[] TankGun(Pcg32 rng)
    {
        double seconds = Duration(SoundEffectKind.TankGun);
        float[] buffer = new float[Samples(seconds)];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / seconds;

            // The sub sweeps down under the blast — the note the ground answers — and
            // the body is the muzzle blast held on the envelope.
            double frequency = 110d - (60d * Math.Min(1d, progress * 1.6d));
            phase += frequency / SampleRate;

            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 5d) * 1.1d;
            double body = Noise(ref rng) * Envelope(t, 0.003d, seconds * 0.22d, seconds * 0.7d, seconds) * 1.2d;
            double slap = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * 14d) * 0.5d;

            buffer[i] = (float)(sub + body + slap);
        }

        Lowpass(buffer, 0.42d);
        return buffer;
    }

    /// <summary>
    /// An artillery piece firing: the same report the tank has, deeper, with the
    /// charge's longer burn and a rumble the ground answers for longer.
    /// </summary>
    private static float[] ArtilleryLaunch(Pcg32 rng)
    {
        double seconds = Duration(SoundEffectKind.ArtilleryLaunch);
        float[] buffer = new float[Samples(seconds)];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / seconds;

            double frequency = 82d - (40d * Math.Min(1d, progress * 1.4d));
            phase += frequency / SampleRate;

            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * 3.6d) * 1.15d;
            double body = Noise(ref rng) * Envelope(t, 0.004d, seconds * 0.18d, seconds * 0.8d, seconds);
            double rumble = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * 8d) * 0.6d;

            buffer[i] = (float)(sub + (body * 1.1d) + rumble);
        }

        Lowpass(buffer, 0.35d);
        return buffer;
    }

    private static float[] AntiAirBurst(Pcg32 rng)
    {
        double seconds = Duration(SoundEffectKind.AntiAirBurst);
        float[] buffer = new float[Samples(seconds)];

        // Three quick reports, 70 ms apart, each a crack over a short body.
        for (int shot = 0; shot < 3; shot++)
        {
            int offset = Samples(shot * 0.07d);
            double loudness = 0.85d - (shot * 0.15d);
            double lowState = 0d;

            for (int i = offset; i < buffer.Length; i++)
            {
                double t = (double)(i - offset) / SampleRate;
                double raw = Noise(ref rng);

                lowState += 0.25d * (raw - lowState);
                double crack = (raw - lowState) * Math.Exp(-t * 70d);
                double body = lowState * Envelope(t, 0.002d, 0.02d, 0.09d, seconds);

                buffer[i] += (float)((crack + (body * 1.4d)) * loudness);
            }
        }

        return buffer;
    }

    // ------------------------------------------------------------------ the explosions

    /// <summary>
    /// An explosion: a crack, then noise held at full on the ADSR — fast attack, the
    /// body sustained, a release that runs most of the sound — with the sub sweeping
    /// down under it and the ground's rumble answering late. The envelope is why a
    /// detonation reads as a detonation: the air is pushed and then runs out, rather
    /// than decaying like a struck bell.
    /// </summary>
    private static float[] Explosion(Pcg32 rng, double seconds, double subFrequency)
    {
        float[] buffer = new float[Samples(seconds)];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double progress = t / seconds;

            double frequency = subFrequency * (1d - (0.55d * Math.Min(1d, progress * 1.5d)));
            phase += frequency / SampleRate;

            double crack = Noise(ref rng) * Math.Exp(-t * (55d / seconds)) * 1.3d;
            double sub = Math.Sin(2d * Math.PI * phase) * Math.Exp(-t * (2.4d / seconds)) * 0.9d;
            double body = Noise(ref rng) * Envelope(t, 0.005d, seconds * 0.28d, seconds * 0.85d, seconds) * 1.15d;
            double rumble = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * (1.6d / seconds)) * 0.6d;
            double crackle = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * (1.4d / seconds)) * 0.3d;

            buffer[i] = (float)(crack + sub + body + rumble + crackle);
        }

        Lowpass(buffer, 0.5d);
        return buffer;
    }

    /// <summary>
    /// The tactical nuke: the same envelope the explosions use, dropped two registers
    /// and held long — the crack, a roar that owns the first second, and a sub the
    /// ground carries for seconds after. The release is the point: a detonation's tail
    /// is the sound arriving from further and further away.
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

            double frequency = 60d - (42d * Math.Min(1d, progress * 1.3d));
            phase += frequency / SampleRate;

            double crack = Noise(ref rng) * Math.Exp(-t * 70d) * 1.6d;
            double sub = Math.Sin(2d * Math.PI * phase) * Envelope(t, 0.004d, seconds * 0.2d, seconds * 0.8d, seconds) * 1.1d;
            double roar = Noise(ref rng) * Envelope(t, 0.003d, seconds * 0.12d, seconds * 0.85d, seconds);
            double rumble = Noise(ref rng) * Noise(ref rng) * Math.Exp(-t * 0.7d) * 0.8d;

            buffer[i] = (float)(crack + sub + roar + rumble);
        }

        Lowpass(buffer, 0.3d);
        return buffer;
    }

    // ------------------------------------------------------------------ the interfaces and the reports

    /// <summary>A round hitting armour: two detuned high partials against a noise crack,
    /// with less of the ring than the first bank gave it — the ring was the beep again.</summary>
    private static float[] Impact(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.Impact))];

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double decay = Math.Exp(-t * 40d);

            double ring = (Math.Sin(2d * Math.PI * 1150d * t) * 0.3d) +
                           (Math.Sin(2d * Math.PI * 1730d * t) * 0.22d);

            buffer[i] = (float)((ring * decay) + (Noise(ref rng) * Math.Exp(-t * 90d) * 1.1d));
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
    /// the two-note chime the machine raises when it is ready to work.
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

                sum += (ring + (Noise(ref rng) * Math.Exp(-local * 180d) * 0.7d)) * Math.Exp(-local * 26d);
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
    /// engine revs. The rev lives in the two-to-three hundred hertz a small speaker
    /// can carry — an idle at seventy is a rumble the laptop's cones cannot move, and
    /// that is why the first version was not audible.
    /// </summary>
    private static float[] UnitComplete(Pcg32 rng)
    {
        float[] buffer = new float[Samples(Duration(SoundEffectKind.UnitComplete))];
        double phase = 0d;

        for (int i = 0; i < buffer.Length; i++)
        {
            double t = (double)i / SampleRate;
            double sum;

            if (t < 0.4d)
            {
                // The crank: three chuffs a fraction of a second apart, each a noise
                // burst against the body's own note.
                double local = t % 0.135d;
                double pulse = Math.Exp(-local * 55d);
                sum = (Noise(ref rng) * pulse * 0.8d) + (Math.Sin(2d * Math.PI * 120d * t) * pulse * 0.55d);
            }
            else
            {
                // The catch and the rev: the saw's rate climbs from the idle to the
                // working engine, and the noise floor rises with it.
                double up = Math.Min(1d, (t - 0.4d) / 0.65d);
                double rate = 130d + (190d * up);
                phase += rate / SampleRate;

                double saw = (2d * phase) - 1d;
                sum = (saw * (0.55d + (0.45d * up))) + (Noise(ref rng) * 0.3d);
            }

            double gate = Math.Min(1d, t * 300d) *
                Math.Min(1d, Math.Max(0d, (Duration(SoundEffectKind.UnitComplete) - t) * 25d));

            buffer[i] = (float)(sum * gate);
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
        var rng = new Pcg32(0x0E1A_CEB0_0055_0002UL);

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

                sum += (ring + (Noise(ref rng) * Math.Exp(-local * 140d) * 0.8d)) * Math.Exp(-local * 22d);
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
            buffer[i] = (float)((saw * 0.55d) + (Noise(ref rng) * 0.25d));
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

    /// <summary>How much of a kind is room: the weapons answer an open field, the
    /// detonation owns a valley, and the interfaces live in the player's hand.</summary>
    private static double ReverbWet(SoundEffectKind kind) => kind switch
    {
        SoundEffectKind.RifleShot => 0.28d,
        SoundEffectKind.TankGun => 0.4d,
        SoundEffectKind.ArtilleryLaunch => 0.42d,
        SoundEffectKind.AntiAirBurst => 0.3d,
        SoundEffectKind.ExplosionSmall => 0.45d,
        SoundEffectKind.ExplosionLarge => 0.55d,
        SoundEffectKind.NuclearDetonation => 0.6d,
        SoundEffectKind.Impact => 0.25d,
        SoundEffectKind.BridgeComplete => 0.35d,
        SoundEffectKind.UnitComplete => 0.2d,
        SoundEffectKind.ConstructionComplete => 0.15d,
        SoundEffectKind.Alarm => 0.1d,
        _ => 0.05d,
    };

    private static int Samples(double seconds) => Math.Max(1, (int)(seconds * SampleRate));

    /// <summary>
    /// White noise in [-1, 1] from the generator.
    /// <para>
    /// <b>The generator is taken by <c>ref</c>, and that is the whole point.</b>
    /// <see cref="Pcg32"/> is a mutable struct: passed by value, every call advanced a
    /// copy and threw it away, so every "noise" sample in a buffer came back as the
    /// same constant and the whole bank was an envelope-shaped DC thump — a rifle with
    /// no crack and an explosion with no roar. Taking it by reference is what makes the
    /// noise a noise.
    /// </para>
    /// </summary>
    private static double Noise(ref Pcg32 rng) => ((rng.NextUInt() / (double)uint.MaxValue) * 2d) - 1d;

    /// <summary>Fades the buffer's last milliseconds to zero, so no kind ends on a step.</summary>
    private static void FadeOut(float[] buffer, double seconds)
    {
        int fade = Math.Min(buffer.Length, Samples(seconds));

        for (int i = 0; i < fade; i++)
        {
            buffer[buffer.Length - fade + i] *= (float)(1d - ((double)i / fade));
        }
    }

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
