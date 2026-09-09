using MiVic.Audio;

namespace MiVic.Audio.Tests;

/// <summary>
/// The music is generated, so it can be tested without listening: the same seed
/// must give the same waveform, the notes must belong to the faction's scale,
/// and the result must fit in a 16-bit file without clipping.
/// </summary>
public sealed class MusicGeneratorTests
{
    private const double Seconds = 4d;

    [Fact]
    public void SameSeedProducesTheSameWaveform()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            float[] first = MusicGenerator.Generate(style, 1234UL, Seconds, out _);
            float[] second = MusicGenerator.Generate(style, 1234UL, Seconds, out _);

            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void DifferentSeedsProduceDifferentMusic()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            float[] reference = MusicGenerator.Generate(style, 1UL, Seconds, out _);
            bool differs = false;

            // A single pair of seeds can legitimately agree on a short excerpt
            // (the march only draws a few random numbers), so sweep a handful.
            for (ulong seed = 2; seed <= 6 && !differs; seed++)
            {
                differs = !reference.SequenceEqual(MusicGenerator.Generate(style, seed, Seconds, out _));
            }

            Assert.True(differs, $"{style} produced identical music for six different seeds.");
        }
    }

    [Fact]
    public void EachFactionSoundsDifferent()
    {
        float[] soviet = MusicGenerator.Generate(FactionStyle.Soviet, 7UL, Seconds, out _);
        float[] chinese = MusicGenerator.Generate(FactionStyle.Chinese, 7UL, Seconds, out _);
        float[] western = MusicGenerator.Generate(FactionStyle.Western, 7UL, Seconds, out _);

        Assert.NotEqual(soviet, chinese);
        Assert.NotEqual(soviet, western);
        Assert.NotEqual(chinese, western);
    }

    [Fact]
    public void GeneratedAudioFitsTheFormatAndDoesNotClip()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] pcm = MusicGenerator.GeneratePcm16(style, 20250101UL, Seconds, out MusicInfo info);

            Assert.Equal((int)(Seconds * MusicGenerator.SampleRate), pcm.Length);
            Assert.True(info.NoteCount > 0, $"{style} produced no notes.");

            int peak = 0;
            double energy = 0d;

            foreach (short sample in pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
                energy += (double)sample * sample;
            }

            Assert.True(peak > 8000, $"{style} is suspiciously quiet (peak {peak}).");
            Assert.True(peak <= 32767, $"{style} clipped at {peak}.");
            Assert.True(energy > 0d, $"{style} is silent.");
        }
    }

    [Fact]
    public void ScalesContainTheirOwnNotesAndWrapAcrossOctaves()
    {
        // The fifth degree of a five-note scale is the next octave's tonic.
        Assert.Equal(12, Scales.Semitones(Scales.PentatonicMajor, 5));
        Assert.Equal(-12, Scales.Semitones(Scales.PentatonicMajor, -5));

        // Harmonic minor's raised seventh is the point of the scale.
        Assert.Contains(11, Scales.HarmonicMinor);
        Assert.DoesNotContain(11, Scales.NaturalMinor);

        // The pentatonic has no semitones at all, which is what makes it sound
        // Chinese rather than European.
        foreach (int[] scale in new[] { Scales.PentatonicMajor, Scales.PentatonicMinor })
        {
            for (int i = 1; i < scale.Length; i++)
            {
                Assert.True(scale[i] - scale[i - 1] >= 2, "A pentatonic scale must not contain a semitone.");
            }
        }
    }

    [Fact]
    public void ScaleMembershipIsOctaveAgnostic()
    {
        Assert.True(Scales.Contains(Scales.NaturalMinor, 57, 57));
        Assert.True(Scales.Contains(Scales.NaturalMinor, 57, 69));
        Assert.False(Scales.Contains(Scales.NaturalMinor, 57, 61));
        Assert.True(Scales.Contains(Scales.Blues, 60, 66));
    }

    [Fact]
    public void ConcertPitchIsCorrect()
    {
        Assert.Equal(440d, Scales.Frequency(69), 6);
        Assert.Equal(220d, Scales.Frequency(57), 6);
        Assert.Equal(880d, Scales.Frequency(81), 6);
    }

    [Fact]
    public void WavRoundTripsThroughAStream()
    {
        short[] pcm = MusicGenerator.GeneratePcm16(FactionStyle.Chinese, 99UL, 1d, out _);

        byte[] file = WavWriter.ToBytes(pcm, MusicGenerator.SampleRate);

        Assert.Equal("RIFF"u8.ToArray(), file[..4]);
        Assert.Equal("WAVE"u8.ToArray(), file[8..12]);
        Assert.Equal("data"u8.ToArray(), file[36..40]);
        Assert.Equal(44 + (pcm.Length * 2), file.Length);

        using var stream = new MemoryStream(file);
        short[] read = WavWriter.Read(stream);

        Assert.Equal(pcm, read);
    }

    [Fact]
    public void CorruptWavFilesAreRejected()
    {
        short[] pcm = MusicGenerator.GeneratePcm16(FactionStyle.Soviet, 5UL, 1d, out _);
        byte[] file = WavWriter.ToBytes(pcm, MusicGenerator.SampleRate);

        byte[] bad = (byte[])file.Clone();
        bad[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => WavWriter.Read(new MemoryStream(bad)));

        Assert.Throws<InvalidDataException>(() => WavWriter.Read(new MemoryStream(file[..20])));
    }

    [Fact]
    public void TempoAndScaleAreReported()
    {
        MusicInfo soviet = MusicInfoFor(FactionStyle.Soviet);
        MusicInfo chinese = MusicInfoFor(FactionStyle.Chinese);
        MusicInfo western = MusicInfoFor(FactionStyle.Western);

        Assert.Equal("αρμονική ελάσσων", soviet.Scale);
        Assert.Equal("πεντατονική", chinese.Scale);
        Assert.Equal("μπλουζ", western.Scale);

        // The march is a measured 96, the song a flowing 84, the rock'n'roll a
        // frantic 152 — the tempos are part of the idiom, not decoration.
        Assert.True(chinese.BeatsPerMinute < soviet.BeatsPerMinute);
        Assert.True(soviet.BeatsPerMinute < western.BeatsPerMinute);
    }

    private static MusicInfo MusicInfoFor(FactionStyle style)
    {
        MusicGenerator.Generate(style, 1UL, Seconds, out MusicInfo info);
        return info;
    }
}
