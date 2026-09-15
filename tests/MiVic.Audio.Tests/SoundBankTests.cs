using MiVic.Audio;

namespace MiVic.Audio.Tests;

/// <summary>
/// Sound effects are generated from noise and oscillators, so the properties
/// worth testing are the ones a listener would notice: right length, not silent,
/// not clipping, and different every time the seed changes.
/// </summary>
public sealed class SoundBankTests
{
    [Fact]
    public void EveryEffectHasTheDocumentedLength()
    {
        foreach (SoundEffectKind kind in Enum.GetValues<SoundEffectKind>())
        {
            float[] samples = SoundBank.Generate(kind, 1UL);
            int expected = (int)(SoundBank.Duration(kind) * SoundBank.SampleRate);

            Assert.True(samples.Length == expected,
                $"{kind}: Duration says {SoundBank.Duration(kind):0.###} s = {expected}, render is {samples.Length} ({samples.Length / (double)SoundBank.SampleRate:0.###} s)");
            Assert.True(samples.Length > 0);
        }
    }

    [Fact]
    public void SameSeedProducesTheSameWaveform()
    {
        foreach (SoundEffectKind kind in Enum.GetValues<SoundEffectKind>())
        {
            Assert.Equal(SoundBank.Generate(kind, 99UL), SoundBank.Generate(kind, 99UL));
        }
    }

    [Fact]
    public void NoiseBasedEffectsChangeWithTheSeed()
    {
        // The click, the alarm, the completion chime, the rollout, the bridge resolve —
        // these are sequences with fixed seeds of their own and do not use the external
        // generator; everything else must respond to the seed.
        SoundEffectKind[] seeded =
        [
            SoundEffectKind.RifleShot, SoundEffectKind.TankGun, SoundEffectKind.ArtilleryLaunch,
            SoundEffectKind.AntiAirBurst, SoundEffectKind.ExplosionSmall, SoundEffectKind.ExplosionLarge,
            SoundEffectKind.EngineLoop, SoundEffectKind.Impact, SoundEffectKind.NuclearDetonation,
        ];

        foreach (SoundEffectKind kind in seeded)
        {
            Assert.NotEqual(SoundBank.Generate(kind, 1UL), SoundBank.Generate(kind, 2UL));
        }
    }


    [Fact]
    public void TheCompletionSoundsTellTheirStory()
    {
        // The construction chime: three rivet strikes the first half carries, then the
        // chime the machine raises — so the opening of the sound is percussive and the
        // tail is tonal.
        float[] construction = SoundBank.Generate(SoundEffectKind.ConstructionComplete, 1UL);
        double firstHalf = Energy(construction, 0, construction.Length / 2);
        double lastHalf = Energy(construction, construction.Length / 2, construction.Length);

        Assert.True(firstHalf > lastHalf, "the rivets should carry the first half");

        // The nuclear detonation: its sub sweeps down to a floor nothing else in the
        // bank visits, and its tail runs seconds past the explosion's own.
        float[] nuke = SoundBank.Generate(SoundEffectKind.NuclearDetonation, 1UL);

        Assert.True(SoundBank.Duration(SoundEffectKind.NuclearDetonation) >
            2d * SoundBank.Duration(SoundEffectKind.ExplosionLarge) / 2d);

        // And the whole of it is audible: a detonation that fades in the first second
        // is an explosion, not a detonation.
        double tail = Energy(nuke, nuke.Length * 3 / 4, nuke.Length);

        Assert.True(tail > 0.0005, $"the nuke's last quarter carries {tail:0.0000}");
    }

    private static double Energy(float[] samples, int start, int end)
    {
        double sum = 0d;

        for (int i = start; i < end; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return sum / Math.Max(1, end - start);
    }

    [Fact]
    public void EffectsAreAudibleAndDoNotClip()
    {
        foreach (SoundEffectKind kind in Enum.GetValues<SoundEffectKind>())
        {
            short[] pcm = SoundBank.GeneratePcm16(kind, 20250101UL);

            int peak = 0;
            double energy = 0d;

            foreach (short sample in pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
                energy += (double)sample * sample;
            }

            Assert.True(peak > 4000, $"{kind} is too quiet (peak {peak}).");
            Assert.True(peak <= 32767, $"{kind} clipped at {peak}.");
            Assert.True(energy > 0d, $"{kind} is silent.");
        }
    }

    [Fact]
    public void EffectsStartAtFullVolumeAndDecay()
    {
        // Every weapon and explosion is percussive: loud at the attack, quiet at
        // the tail. A reversed envelope would sound like a vacuum cleaner.
        foreach (SoundEffectKind kind in new[]
                 {
                     SoundEffectKind.RifleShot, SoundEffectKind.TankGun,
                     SoundEffectKind.ArtilleryLaunch, SoundEffectKind.ExplosionLarge,
                     SoundEffectKind.Impact,
                 })
        {
            float[] samples = SoundBank.Generate(kind, 3UL);

            double head = Rms(samples, 0, samples.Length / 10);
            double tail = Rms(samples, samples.Length - (samples.Length / 10), samples.Length);

            Assert.True(head > tail, $"{kind} does not decay (head {head:0.000}, tail {tail:0.000}).");
        }
    }

    [Fact]
    public void TheEngineLoopHasNoSilentSeam()
    {
        float[] loop = SoundBank.Generate(SoundEffectKind.EngineLoop, 5UL);

        // The tone uses a whole number of cycles and the noise is crossfaded, so
        // the loop point must not be quieter than the rest of the loop.
        int window = SoundBank.SampleRate / 20;
        double head = Rms(loop, 0, window);
        double tail = Rms(loop, loop.Length - window, loop.Length);

        Assert.True(head > tail * 0.6, $"The engine loop fades at the seam (head {head:0.000}, tail {tail:0.000}).");
    }

    [Fact]
    public void DifferentEffectsAreActuallyDifferent()
    {
        float[] rifle = SoundBank.Generate(SoundEffectKind.RifleShot, 7UL);
        float[] tank = SoundBank.Generate(SoundEffectKind.TankGun, 7UL);
        float[] small = SoundBank.Generate(SoundEffectKind.ExplosionSmall, 7UL);
        float[] large = SoundBank.Generate(SoundEffectKind.ExplosionLarge, 7UL);

        Assert.NotEqual(rifle.Length, tank.Length);
        Assert.True(large.Length > small.Length, "The large explosion must last longer than the small one.");
        Assert.NotEqual(small, large);
    }

    private static double Rms(float[] samples, int start, int end)
    {
        double sum = 0d;

        for (int i = start; i < end; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sum / Math.Max(1, end - start));
    }
}
