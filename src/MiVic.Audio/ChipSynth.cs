namespace MiVic.Audio;

/// <summary>Waveforms a chip can produce.</summary>
public enum Waveform
{
    /// <summary>Pulse wave. Duty 0.5 is a square, 0.125 is thin and nasal.</summary>
    Pulse = 0,

    /// <summary>Triangle: softer, used for bass and flute-like leads.</summary>
    Triangle = 1,

    /// <summary>15-bit LFSR noise: drums, snare, hi-hat.</summary>
    Noise = 2,

    /// <summary>Sawtooth, for a rougher "electric" lead.</summary>
    Saw = 3,
}

/// <summary>How a note's volume moves over its life.</summary>
public readonly record struct Envelope(double Attack, double Decay, double Sustain, double Release)
{
    /// <summary>Plucked or percussive: instant attack, quick decay to silence.</summary>
    public static readonly Envelope Pluck = new(0.002, 0.10, 0.10, 0.06);

    /// <summary>Organ-like: sustained while held.</summary>
    public static readonly Envelope Organ = new(0.01, 0.05, 0.85, 0.08);

    /// <summary>Brass: slow attack, strong sustain, so a march reads as horns.</summary>
    public static readonly Envelope Brass = new(0.06, 0.12, 0.80, 0.12);

    /// <summary>Percussion: instant on, linear decay, no sustain.</summary>
    public static readonly Envelope Percussive = new(0.001, 0.05, 0.0, 0.02);
}

/// <summary>
/// A tiny chip synthesizer in the spirit of an early-80s home computer.
/// <para>
/// Everything is generated sample by sample: no samples, no tables, no external
/// audio files. The constraint is the point — an AY-3-8910 or SID could not
/// record a real orchestra either, and the resulting square-wave marches and
/// beeping arpeggios are exactly the sound this game wants.
/// </para>
/// <para>
/// Rendering is additive into a <c>float</c> mix buffer. Clipping is handled
/// once at the end, so instruments can be layered without worrying about order.
/// </para>
/// </summary>
public sealed class ChipSynth
{
    private readonly int _sampleRate;
    private uint _lfsr = 0x7FFF;

    public ChipSynth(int sampleRate = 22050)
    {
        if (sampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be at least 8000 Hz.");
        }

        _sampleRate = sampleRate;
    }

    /// <summary>Samples per second this synth renders at.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Adds one note to <paramref name="mix"/>. Samples outside the buffer are
    /// ignored, so a note may ring past the end of the track.
    /// </summary>
    /// <param name="mix">Destination mix buffer.</param>
    /// <param name="startSample">First sample of the note.</param>
    /// <param name="durationSeconds">Length of the note, excluding release tail.</param>
    /// <param name="midiNote">Pitch as a MIDI note number.</param>
    /// <param name="volume">Peak amplitude, typically 0.05–0.35.</param>
    /// <param name="waveform">Oscillator shape.</param>
    /// <param name="envelope">Volume envelope.</param>
    /// <param name="duty">Pulse duty cycle, ignored by other waveforms.</param>
    /// <param name="vibrato">Vibrato depth in semitones; zero disables it.</param>
    /// <param name="detuneCents">
    /// Detune in cents. Cheap keyboards and cheap guitarists are both slightly
    /// out of tune, and that is part of the Western sound.
    /// </param>
    public void AddNote(
        float[] mix,
        int startSample,
        double durationSeconds,
        int midiNote,
        float volume,
        Waveform waveform,
        Envelope envelope,
        double duty = 0.5,
        double vibrato = 0d,
        double detuneCents = 0d)
    {
        ArgumentNullException.ThrowIfNull(mix);

        double frequency = Scales.Frequency(midiNote) * Math.Pow(2d, detuneCents / 1200d);
        double total = durationSeconds + envelope.Release;
        int sampleCount = (int)(total * _sampleRate);

        if (sampleCount <= 0 || volume <= 0f)
        {
            return;
        }

        double phase = 0d;
        double phaseStep = frequency / _sampleRate;

        for (int i = 0; i < sampleCount; i++)
        {
            int index = startSample + i;

            if (index < 0)
            {
                continue;
            }

            if (index >= mix.Length)
            {
                break;
            }

            double time = (double)i / _sampleRate;
            float amplitude = (float)EnvelopeAt(envelope, time, durationSeconds);

            if (amplitude <= 0f)
            {
                continue;
            }

            double currentFrequency = frequency;

            if (vibrato > 0d)
            {
                // 5.5 Hz, the speed of a singer's or violinist's wobble.
                currentFrequency *= 1d + (vibrato * Math.Sin(2d * Math.PI * 5.5d * time));
            }

            phase += currentFrequency / _sampleRate;

            if (phase >= 1d)
            {
                phase -= Math.Floor(phase);
            }

            mix[index] += (float)Sample(waveform, phase, duty) * amplitude * volume;
        }
    }

    /// <summary>
    /// Adds a drum hit: a pitched kick, a noisy snare, or a bright hi-hat. The
    /// 80s home-computer drum kit was one noise channel and a pitch sweep.
    /// </summary>
    public void AddDrum(float[] mix, int startSample, DrumKind kind, float volume)
    {
        ArgumentNullException.ThrowIfNull(mix);

        double duration = kind switch
        {
            DrumKind.Kick => 0.12,
            DrumKind.Snare => 0.11,
            DrumKind.Hat => 0.035,
            _ => 0.08,
        };

        int sampleCount = (int)(duration * _sampleRate);
        double startFrequency = kind switch
        {
            DrumKind.Kick => 140d,
            DrumKind.Snare => 220d,
            DrumKind.Hat => 8000d,
            _ => 200d,
        };

        double phase = 0d;

        for (int i = 0; i < sampleCount; i++)
        {
            int index = startSample + i;

            if (index >= mix.Length)
            {
                break;
            }

            double t = (double)i / _sampleRate;
            double decay = Math.Exp(-t / (duration * 0.35d));
            float value;

            switch (kind)
            {
                case DrumKind.Kick:
                {
                    // Pitch sweep from a thump to a thud.
                    double frequency = startFrequency * (1d - (0.75d * (t / duration)));
                    phase += frequency / _sampleRate;
                    value = (float)(Math.Sin(2d * Math.PI * phase) * decay);
                    break;
                }

                case DrumKind.Snare:
                {
                    double tone = Math.Sin(2d * Math.PI * startFrequency * t) * 0.35d;
                    value = (float)((NextNoise() + tone) * decay * 0.8d);
                    break;
                }

                default:
                {
                    value = (float)(NextNoise() * decay * 0.6d);
                    break;
                }
            }

            if (index >= 0)
            {
                mix[index] += value * volume;
            }
        }
    }

    /// <summary>
    /// A 15-bit LFSR noise source, the same trick the NES and SID used: a shift
    /// register with tapped feedback, which sounds like white noise but costs
    /// one XOR.
    /// </summary>
    private double NextNoise()
    {
        uint bit = ((_lfsr >> 0) ^ (_lfsr >> 1)) & 1u;
        _lfsr = (_lfsr >> 1) | (bit << 14);

        return ((_lfsr & 1u) == 1u ? 1d : -1d) * 0.5d;
    }

    private static double Sample(Waveform waveform, double phase, double duty)
        => waveform switch
        {
            Waveform.Pulse => phase < duty ? 1d : -1d,
            Waveform.Triangle => 1d - (4d * Math.Abs(phase - 0.5d)),
            Waveform.Saw => (2d * phase) - 1d,
            Waveform.Noise => 0d,
            _ => 0d,
        };

    private static double EnvelopeAt(Envelope envelope, double time, double duration)
    {
        if (time < envelope.Attack)
        {
            return envelope.Attack > 0d ? time / envelope.Attack : 1d;
        }

        double afterAttack = time - envelope.Attack;

        if (afterAttack < envelope.Decay)
        {
            double t = afterAttack / envelope.Decay;
            return 1d + ((envelope.Sustain - 1d) * t);
        }

        double held = afterAttack - envelope.Decay;

        if (held < duration)
        {
            return envelope.Sustain;
        }

        double released = held - duration;

        if (released >= envelope.Release || envelope.Release <= 0d)
        {
            return 0d;
        }

        return envelope.Sustain * (1d - (released / envelope.Release));
    }
}

/// <summary>Drum voices.</summary>
public enum DrumKind
{
    /// <summary>Low thump on the beat.</summary>
    Kick = 0,

    /// <summary>Noisy crack on the backbeat.</summary>
    Snare = 1,

    /// <summary>Short bright tick.</summary>
    Hat = 2,
}
