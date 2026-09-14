using MiVic.Core.Campaign;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// Campaign progress is a file, which means a format, which means a version — and
/// the tests here are the format's: that a missing file is a fresh campaign rather
/// than an error, that the round trip survives a real write to a real path, that a
/// file from a different version is a refusal rather than a guess, and that the
/// next-unwon walk reads the catalog order the campaign is played in.
/// </summary>
public sealed class CampaignProgressTests
{
    private readonly string _path;

    public CampaignProgressTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mivic-progress-{Guid.NewGuid():N}.txt");
    }

    [Fact]
    public void AFileThatDoesNotExistIsAFreshCampaign()
    {
        CampaignProgress progress = CampaignProgress.Load(_path);

        Assert.Empty(progress.WonMissions);
        Assert.False(progress.HasWon("m1_bridgehead"));
    }

    [Fact]
    public void TheRoundTripSurvivesARealFile()
    {
        CampaignProgress progress = new();
        progress.MarkWon("m1_bridgehead");
        progress.MarkWon("m2_ridge");
        progress.Save(_path);

        CampaignProgress loaded = CampaignProgress.Load(_path);

        Assert.Equal(2, loaded.WonMissions.Count);
        Assert.Equal("m1_bridgehead", loaded.WonMissions[0]);
        Assert.Equal("m2_ridge", loaded.WonMissions[1]);
        Assert.True(loaded.HasWon("m2_ridge"));
        Assert.False(loaded.HasWon("m3_industry"));
    }

    [Fact]
    public void WinningTwiceLeavesOneEntry()
    {
        CampaignProgress progress = new();
        progress.MarkWon("m1_bridgehead");
        progress.MarkWon("m1_bridgehead");

        Assert.Single(progress.WonMissions);
    }

    [Fact]
    public void AFileFromAnotherVersionIsRefused()
    {
        File.WriteAllLines(_path, ["MiVicCampaign 999"]);

        Assert.Throws<InvalidDataException>(() => CampaignProgress.Load(_path));
    }

    [Fact]
    public void AFileThatIsNotProgressIsRefused()
    {
        File.WriteAllLines(_path, ["not a progress file", "won m1_bridgehead"]);

        Assert.Throws<InvalidDataException>(() => CampaignProgress.Load(_path));
    }

    [Fact]
    public void ResetIsTheStartOfANewCampaign()
    {
        CampaignProgress progress = new();
        progress.MarkWon("m1_bridgehead");
        progress.Reset();

        Assert.Empty(progress.WonMissions);
    }

    [Fact]
    public void TheNextUnwonWalkReadsTheCampaignOrder()
    {
        var won = new List<string>();

        Assert.Equal("m1_bridgehead", CampaignProgress.NextUnwon(
            ["m1_bridgehead", "m2_ridge", "m3_industry"], won));

        won.Add("m1_bridgehead");
        won.Add("m2_ridge");

        Assert.Equal("m3_industry", CampaignProgress.NextUnwon(
            ["m1_bridgehead", "m2_ridge", "m3_industry"], won));

        won.Add("m3_industry");

        Assert.Null(CampaignProgress.NextUnwon(
            ["m1_bridgehead", "m2_ridge", "m3_industry"], won));
    }

    [Fact]
    public void AHeaderOnlyFileIsAnEmptyCampaignRatherThanARefusal()
    {
        // The file a fresh campaign writes, and the one a player's next boot reads:
        // a header with nothing under it is the empty record, which is exactly what
        // it says it is, and refusing it would be refusing the campaign's own start.
        File.WriteAllLines(_path, ["MiVicCampaign 1"]);

        CampaignProgress progress = CampaignProgress.Load(_path);

        Assert.Empty(progress.WonMissions);
    }
}
