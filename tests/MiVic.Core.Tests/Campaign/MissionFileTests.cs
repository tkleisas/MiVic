using MiVic.Core.Campaign;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// The mission file format, asked the question any format must answer: can everything the
/// campaign ships go through it and come back the same mission? Every shipped mission —
/// including the one with the richest script — is written to a file, read back, and compared
/// element by element, because the format exists to carry exactly this. And the loader's
/// verdict is the validator's own: a file whose mission cannot be won is refused, with the
/// validator's sentences in the refusal.
/// </summary>
public sealed class MissionFileTests
{
    private readonly string _path;

    public MissionFileTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mivic-mission-{Guid.NewGuid():N}.mission.json");
    }

    [Fact]
    public void EveryShippedMissionRoundTrips()
    {
        foreach (MissionDefinition shipped in MissionCatalog.All)
        {
            MissionFile.Save(shipped, _path);
            MissionDefinition loaded = MissionFile.Load(_path);

            AssertMission(shipped, loaded);
        }
    }

    [Fact]
    public void TheRichestScriptRoundTripsFieldByField()
    {
        // m4_pass carries the vocabulary: triggers with flags, a denial, a scripted
        // objective, a two-faction roster, and the opening-world declaration. The
        // generic round trip above covers it as data; this one reads the pieces the
        // next author is going to edit, so a format that silently lost one would fail
        // here by name.
        MissionDefinition shipped = MissionCatalog.Require("m4_pass");

        MissionFile.Save(shipped, _path);
        MissionDefinition loaded = MissionFile.Load(_path);

        Assert.Equal(shipped.Id, loaded.Id);
        Assert.Equal(shipped.GreekTitle, loaded.GreekTitle);
        Assert.Equal(shipped.GreekBriefing, loaded.GreekBriefing);
        Assert.Equal(shipped.Seed, loaded.Seed);
        Assert.Equal(shipped.TimeLimitTicks, loaded.TimeLimitTicks);
        Assert.Equal(shipped.Roster, loaded.Roster);
        Assert.Equal(shipped.Triggers.Count, loaded.Triggers.Count);

        for (int i = 0; i < shipped.Triggers.Count; i++)
        {
            Assert.Equal(shipped.Triggers[i].Id, loaded.Triggers[i].Id);
            Assert.Equal(shipped.Triggers[i].Condition, loaded.Triggers[i].Condition);
            Assert.Equal(shipped.Triggers[i].DependsOnOpeningWorld, loaded.Triggers[i].DependsOnOpeningWorld);
            Assert.Equal(shipped.Triggers[i].Actions.Count, loaded.Triggers[i].Actions.Count);

            for (int a = 0; a < shipped.Triggers[i].Actions.Count; a++)
            {
                Assert.Equal(shipped.Triggers[i].Actions[a], loaded.Triggers[i].Actions[a]);
            }
        }
    }

    [Fact]
    public void AFileFromAnotherVersionIsRefused()
    {
        MissionDefinition shipped = MissionCatalog.Require("m1_bridgehead");

        MissionFile.Save(shipped, _path);

        string[] other = File.ReadAllLines(_path);
        other[1] = "  \"version\": 999,";

        File.WriteAllLines(_path, other);

        Assert.Throws<InvalidDataException>(() => MissionFile.Load(_path));
    }

    [Fact]
    public void AFileThatIsNotAMissionIsRefused()
    {
        File.WriteAllText(_path, "{ \"mission\": \"NotOurs\", \"version\": 1 }");

        Assert.Throws<InvalidDataException>(() => MissionFile.Load(_path));
    }

    [Fact]
    public void ARefusalNamesTheProblemsTheValidatorFound()
    {
        // The mission the validator refuses: an objective that measures a team the
        // match does not declare. The refusal is the loader's, and it carries the
        // validator's own sentence, because those are sentences an author can act on.
        MissionDefinition broken = MissionCatalog.Require("m1_bridgehead") with
        {
            Objectives =
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Δοκιμή.",
                    TargetTeam: 3,
                    TargetCount: 1,
                    DeadlineTick: 7_200),
            ],
        };

        MissionFile.Save(broken, _path);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => MissionFile.Load(_path));

        Assert.Contains("problems", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not declare", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compares two missions member by member: a record's own equality compares its
    /// collection members by reference, which is exactly the comparison a round trip
    /// must not be judged by.
    /// </summary>
    private static void AssertMission(MissionDefinition expected, MissionDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.GreekTitle, actual.GreekTitle);
        Assert.Equal(expected.GreekBriefing, actual.GreekBriefing);
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.PlayerBase, actual.PlayerBase);
        Assert.Equal(expected.AllyBase, actual.AllyBase);
        Assert.Equal(expected.EnemyBase, actual.EnemyBase);
        Assert.Equal(expected.PlayerUnits, actual.PlayerUnits);
        Assert.Equal(expected.AllyUnits, actual.AllyUnits);
        Assert.Equal(expected.EnemyUnits, actual.EnemyUnits);
        Assert.Equal(expected.TimeLimitTicks, actual.TimeLimitTicks);
        Assert.Equal(expected.Roster, actual.Roster);

        Assert.Equal(expected.Objectives.Count, actual.Objectives.Count);

        for (int i = 0; i < expected.Objectives.Count; i++)
        {
            Assert.Equal(expected.Objectives[i], actual.Objectives[i]);
        }

        Assert.Equal(expected.Triggers.Count, actual.Triggers.Count);

        for (int i = 0; i < expected.Triggers.Count; i++)
        {
            TriggerDefinition expectedTrigger = expected.Triggers[i];
            TriggerDefinition actualTrigger = actual.Triggers[i];

            // Record equality on the trigger compares its action list by reference, so
            // two lists with the same contents are not "equal" — the comparison walks
            // the actions instead.
            Assert.Equal(expectedTrigger.Id, actualTrigger.Id);
            Assert.Equal(expectedTrigger.Condition, actualTrigger.Condition);
            Assert.Equal(expectedTrigger.Note, actualTrigger.Note);
            Assert.Equal(expectedTrigger.DependsOnOpeningWorld, actualTrigger.DependsOnOpeningWorld);
            Assert.Equal(expectedTrigger.Actions.Count, actualTrigger.Actions.Count);

            for (int a = 0; a < expectedTrigger.Actions.Count; a++)
            {
                Assert.Equal(expectedTrigger.Actions[a], actualTrigger.Actions[a]);
            }
        }
    }
}
