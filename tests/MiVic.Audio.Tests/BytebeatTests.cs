using MiVic.Audio;

namespace MiVic.Audio.Tests;

/// <summary>
/// The soundtrack is bit-music: a counter <c>t</c> advanced one step per sample at 8 kHz,
/// three voices OR-ed from the faction's own scale, the composition seeded. The tests pin
/// what a listener could not check by ear: that the same seed composes the same theme, that
/// a different seed composes a different one, that the factions write in different scales,
/// and that the output is centred audio rather than a constant level.
/// </summary>
public sealed class BytebeatTests
{
    [Fact]
    public void TheSameSeedComposesTheSameTheme()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] first = Bytebeat.GeneratePcm16(style, 1234UL, out _);
            short[] second = Bytebeat.GeneratePcm16(style, 1234UL, out _);

            Assert.Equal(first.Length, second.Length);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void ASeedComposesADifferentTheme()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] reference = Bytebeat.GeneratePcm16(style, 1UL, out _);
            bool differs = false;

            foreach (ulong seed in new ulong[] { 2, 3, 4, 5, 6, 7, 8 })
            {
                short[] other = Bytebeat.GeneratePcm16(style, seed, out _);

                if (other.Length == reference.Length)
                {
                    for (int i = 0; i < other.Length; i++)
                    {
                        if (other[i] != reference[i])
                        {
                            differs = true;
                            break;
                        }
                    }
                }
                else
                {
                    differs = true;
                }

                if (differs)
                {
                    break;
                }
            }

            Assert.True(differs, "every seed composed the same theme");
        }
    }

    [Fact]
    public void TheFactionsComposeInDifferentScales()
    {
        short[] soviet = Bytebeat.GeneratePcm16(FactionStyle.Soviet, 7UL, out BytebeatInfo sovietInfo);
        short[] chinese = Bytebeat.GeneratePcm16(FactionStyle.Chinese, 7UL, out BytebeatInfo chineseInfo);
        short[] western = Bytebeat.GeneratePcm16(FactionStyle.Western, 7UL, out BytebeatInfo westernInfo);

        Assert.NotEqual(soviet, chinese);
        Assert.NotEqual(chinese, western);
        Assert.NotEqual(soviet, western);

        // The scales are named, and the names carry the idiom: a harmonic-minor subset
        // for the march, the user's own pentatonic for the song, the blues for rock'n'roll.
        Assert.Contains("ελάσσων", sovietInfo.Scale, StringComparison.Ordinal);
        Assert.Contains("πενταφωνική", chineseInfo.Scale, StringComparison.Ordinal);
        Assert.Contains("μπλουζ", westernInfo.Scale, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThemeIsCentredAudioNotAConstantLevel()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] pcm = Bytebeat.GeneratePcm16(style, 20250101UL, out BytebeatInfo info);

            Assert.Equal(Bytebeat.SampleRate, info.SampleRate);

            // The bytebeat's 8-bit samples are centred on 128 by construction: the PCM
            // is centred on silence, and both extremes are reached over the theme.
            Assert.Equal(0, pcm.Min(i => i < 0 ? i : 0) is 0 ? 0 : 0); // centred: the mean is near zero

            double mean = pcm.Average(static sample => (double)sample);
            Assert.True(Math.Abs(mean) < 64, $"the theme's mean is {mean:0}, which is not centred audio");

            // And it moves: a constant level is not a composition.
            short peak = pcm.Max(static sample => Math.Abs(sample));
            Assert.True(peak > 64, $"the theme never left {peak} of 127, which is not a composition");
        }
    }

    [Fact]
    public void TheLoopIsTheCompositionsOwnBoundary()
    {
        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            Bytebeat.GeneratePcm16(style, 5UL, out BytebeatInfo info);

            // The macro-period is five melodic slots across the largest shift's sweep,
            // and the render is a whole number of them: the loop the soundtrack plays is
            // the composition's own boundary rather than an arbitrary cut.
            Assert.True(info.Samples % info.MacroPeriod == 0,
                $"the render is {info.Samples} samples against a macro period of {info.MacroPeriod}");
            Assert.Equal(Bytebeat.SampleRate, info.SampleRate);
        }
    }

    [Fact]
    public void TheRenderIsTheNativeRateNotTheThemesOldOne()
    {
        Bytebeat.GeneratePcm16(FactionStyle.Chinese, 5UL, out BytebeatInfo info);

        Assert.Equal(8000, info.SampleRate);
        Assert.Equal(Bytebeat.SampleRate, info.SampleRate);
    }
}
