using Xunit;

namespace MiVic.Audio.Tests;

public sealed class ScoreTests
{
    /// <summary>A score file's path, from the repository root — the test project's own directory is not the game's.</summary>
    internal static string ScorePath(string faction)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scores")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("no scores/ directory up from the test's own");
        }

        return Path.Combine(directory.FullName, "scores", $"{faction}.score.json");
    }

    [Fact]
    public void TheThreeFactionScoresLoad()
    {
        foreach (string faction in new[] { "soviet", "chinese", "western" })
        {
            Score score = Score.Load(ScorePath(faction));

            Assert.True(score.Tempo is >= 40 and <= 240);
            Assert.True(score.Grid is >= 4 and <= 64);
            Assert.NotEmpty(score.Patterns);
            Assert.NotEmpty(score.Leitmotivs);

            // Every cue resolves: the ladder the director reads is written in leitmotiv names
            // the same file defines.
            Assert.NotNull(score.LeitmotivByName(score.Cues.Victory));
            Assert.NotNull(score.LeitmotivByName(score.Cues.Defeat));
            Assert.NotNull(score.LeitmotivByName(score.Cues.Combat));
            Assert.NotNull(score.LeitmotivByName(score.Cues.Alert));
            Assert.NotNull(score.LeitmotivByName(score.Cues.Calm));
        }
    }

    [Fact]
    public void EveryLeitmotivRendersWholeAndLoops()
    {
        foreach (string faction in new[] { "soviet", "chinese", "western" })
        {
            Score score = Score.Load(ScorePath(faction));

            foreach (Leitmotiv leitmotiv in score.Leitmotivs)
            {
                short[] pcm = Sequencer.RenderLeitmotiv(score, leitmotiv);

                int samplesPerStep = (int)(score.SecondsPerStep * SoundBank.SampleRate);
                int expected = leitmotiv.Bars * score.Grid * samplesPerStep;

                // A whole multiple of the bar: the loop boundary is the leitmotiv's own, not
                // an arbitrary cut.
                Assert.Equal(expected, pcm.Length);
                Assert.NotEqual(0, pcm.Max());
                Assert.NotEqual(0, pcm.Min());
            }
        }
    }

    [Fact]
    public void TheSameScoreRendersTheSamePcm()
    {
        Score score = Score.Load(ScorePath("soviet"));
        Leitmotiv march = score.LeitmotivByName("march")!;

        Assert.Equal(Sequencer.RenderLeitmotiv(score, march), Sequencer.RenderLeitmotiv(score, march));
    }

    [Fact]
    public void AStepStringOfTheWrongLengthIsRefused()
    {
        string path = WriteScore(
            """
            {
              "tempo": 112, "grid": 16, "root": 45, "scale": [0, 2, 3, 5, 7, 8, 11],
              "patterns": {
                "d": { "bassdrum": "X..x..X...x..X." }
              },
              "leitmotivs": { "m": { "bars": 2, "patterns": ["d"] } },
              "cues": { "victory": "m", "defeat": "m", "combat": "m", "alert": "m", "calm": "m" }
            }
            """);

        var refused = Assert.Throws<ScoreException>(() => Score.Load(path));

        Assert.Contains("15 βήματα", refused.Message);
    }

    [Fact]
    public void ALeitmotivNamingAMissingPatternIsRefused()
    {
        string path = WriteScore(
            """
            {
              "tempo": 112, "grid": 16, "root": 45, "scale": [0, 2, 3, 5, 7, 8, 11],
              "patterns": { "d": { "bassdrum": "X..............." } },
              "leitmotivs": { "m": { "bars": 2, "patterns": ["no-such-pattern"] } },
              "cues": { "victory": "m", "defeat": "m", "combat": "m", "alert": "m", "calm": "m" }
            }
            """);

        var refused = Assert.Throws<ScoreException>(() => Score.Load(path));

        Assert.Contains("no-such-pattern", refused.Message);
    }

    [Fact]
    public void ACueNamingAMissingLeitmotivIsRefused()
    {
        string path = WriteScore(
            """
            {
              "tempo": 112, "grid": 16, "root": 45, "scale": [0, 2, 3, 5, 7, 8, 11],
              "patterns": { "d": { "bassdrum": "X..............." } },
              "leitmotivs": { "m": { "bars": 2, "patterns": ["d"] } },
              "cues": { "victory": "nowhere", "defeat": "m", "combat": "m", "alert": "m", "calm": "m" }
            }
            """);

        var refused = Assert.Throws<ScoreException>(() => Score.Load(path));

        Assert.Contains("nowhere", refused.Message);
    }

    [Fact]
    public void AFillRendersOneBar()
    {
        Score score = Score.Load(ScorePath("soviet"));
        ScoreFill fill = score.Fill("to-battle")!;

        short[] pcm = Sequencer.RenderFill(score, fill);
        int expected = score.Grid * (int)(score.SecondsPerStep * SoundBank.SampleRate);

        Assert.Equal(expected, pcm.Length);
    }

    private static string WriteScore(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"score-{Guid.NewGuid():N}.score.json");

        File.WriteAllText(path, json);
        return path;
    }
}

public sealed class InstrumentTests
{
    [Theory]
    [InlineData(KitVoice.BassDrum)]
    [InlineData(KitVoice.Snare)]
    [InlineData(KitVoice.Tom1)]
    [InlineData(KitVoice.Tom2)]
    [InlineData(KitVoice.HiHatClosed)]
    [InlineData(KitVoice.HiHatOpen)]
    [InlineData(KitVoice.Tambourine)]
    public void EveryKitVoiceDecaysToNearSilence(KitVoice voice)
    {
        float[] sample = Instruments.Kit(voice, 1f, 0.5d);

        float tail = MathF.Abs(sample[^1]);

        // A drum that has not died half a second after it was struck is a fault — the hit
        // would smear across every step that follows it.
        Assert.True(tail < 0.02f, $"{voice}'s tail is {tail:0.000}");
        Assert.NotEqual(0f, sample.Max(MathF.Abs));
    }

    [Fact]
    public void AnAccentIsLouderThanANormalHit()
    {
        float[] accent = Instruments.Kit(KitVoice.Snare, 1f, 0.5d);
        float[] normal = Instruments.Kit(KitVoice.Snare, Score.NormalVelocity, 0.5d);

        double accentPeak = accent.Max(s => Math.Abs(s));
        double normalPeak = normal.Max(s => Math.Abs(s));

        Assert.True(accentPeak > normalPeak * 1.2d);
    }

    [Fact]
    public void TheSameVoiceRendersTheSameSample()
    {
        float[] first = Instruments.Kit(KitVoice.HiHatOpen, 1f, 0.5d);
        float[] second = Instruments.Kit(KitVoice.HiHatOpen, 1f, 0.5d);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PitchedVoicesRingForTheirLengthAndStop()
    {
        float[][] samples =
        [
            PitchedInstruments.Bass(110d, 0.5d),
            PitchedInstruments.Strings(220d, 1d),
            PitchedInstruments.Horns(440d, 0.7d),
        ];

        foreach (float[] sample in samples)
        {
            // The envelope closes the note: the tail is silent because the note was asked to
            // be, not because the render ran out of patience.
            float tail = MathF.Abs(sample[^1]);

            Assert.True(tail < 0.05f, $"a pitched note's tail is {tail:0.000}, which rings past its own length");
        }
    }

    [Fact]
    public void StringsSwellRatherThanStart()
    {
        float[] strings = PitchedInstruments.Strings(220d, 1.2d);

        double opening = strings.Take(strings.Length / 10).Average(s => Math.Abs(s));
        double sustain = strings.Skip(strings.Length / 2).Take(strings.Length / 4).Average(s => Math.Abs(s));

        // The swell is the instrument: an attack that starts loud is an organ, not a section.
        Assert.True(sustain > opening * 2d, $"opening {opening:0.000}, sustain {sustain:0.000}");
    }
}
