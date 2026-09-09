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

            Assert.Equal(expected, samples.Length);
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
        // The click and the alarm are pure tones and do not use the generator;
        // everything else must respond to the seed.
        SoundEffectKind[] seeded =
        [
            SoundEffectKind.RifleShot, SoundEffectKind.TankGun, SoundEffectKind.ArtilleryLaunch,
            SoundEffectKind.AntiAirBurst, SoundEffectKind.ExplosionSmall, SoundEffectKind.ExplosionLarge,
            SoundEffectKind.EngineLoop, SoundEffectKind.Impact,
        ];

        foreach (SoundEffectKind kind in seeded)
        {
            Assert.NotEqual(SoundBank.Generate(kind, 1UL), SoundBank.Generate(kind, 2UL));
        }
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
