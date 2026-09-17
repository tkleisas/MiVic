using MiVic.Core.Campaign;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// The cutscene format: a set, a cast, a camera and a script.
/// <para>
/// Three things are under test. That the format carries a scene through a file and back. That
/// the loader refuses what a director could not play — no lines, a camera that runs backwards,
/// a line spoken by somebody who is not standing in the room — in its own sentences. And that
/// the catalogue finds the scene a mission belongs to without depending on the order the disk
/// happened to list its files in.
/// </para>
/// </summary>
public sealed class CutsceneFileTests
{
    private readonly string _directory;

    public CutsceneFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mivic-cutscene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string id) => Path.Combine(_directory, id + CutsceneFile.Extension);

    /// <summary>A playable scene: two figures' worth of room, a camera move, two lines.</summary>
    private static CutsceneDefinition Scene(string id = "m1_briefing") => new(
        Id: id,
        Set: "set_study",
        Music: "bridge",
        Camera:
        [
            new CutsceneCameraKey(0, X: 900, Y: 1_650, Z: 3_400, TargetX: 0, TargetY: 1_350, TargetZ: -1_100),
            new CutsceneCameraKey(4_000, X: 500, Y: 1_580, Z: 2_600, TargetX: 0, TargetY: 1_300, TargetZ: -1_200),
        ],
        Figures: [new CutsceneFigure("personality_elder", X: 0, Z: -1_700, FacingDegrees: 0)],
        Lines:
        [
            new CutsceneLine("personality_elder", "Κάθισε. Δεν θα κρατήσει πολύ.", 4_000),
            new CutsceneLine("personality_elder", "Οι Δυτικοί έστησαν φυλάκιο βόρεια της γραμμής.", 6_000),
        ],
        Kind: CutsceneKind.Briefing,
        MissionId: "m1_bridgehead");

    [Fact]
    public void ASceneRoundTrips()
    {
        CutsceneDefinition scene = Scene();

        CutsceneFile.Save(scene, PathFor(scene.Id));
        CutsceneDefinition loaded = CutsceneFile.Load(PathFor(scene.Id));

        // Element by element, not `Assert.Equal(scene, loaded)`: a record's own equality
        // compares its collections by reference, so that assertion would fail for a format
        // that round-trips perfectly and pass for the same instance — the lesson the mission
        // format's round-trip test already carries.
        Assert.Equal(scene.Id, loaded.Id);
        Assert.Equal(scene.Set, loaded.Set);
        Assert.Equal(scene.Music, loaded.Music);
        Assert.Equal(scene.Camera, loaded.Camera);
        Assert.Equal(scene.Figures, loaded.Figures);
        Assert.Equal(scene.Lines, loaded.Lines);
        Assert.Equal(CutsceneKind.Briefing, loaded.Kind);
        Assert.Equal("m1_bridgehead", loaded.MissionId);
        Assert.Equal(scene.ScriptMilliseconds, loaded.ScriptMilliseconds);
    }

    [Fact]
    public void TheScriptIsTheSumOfItsLines()
    {
        Assert.Equal(10_000, Scene().ScriptMilliseconds);
    }

    [Fact]
    public void AFigureWithAPaddedClipIsRefused()
    {
        // A clip is matched against the asset's own clip names, so " Idle " matches nothing
        // and the figure stands in its bind pose — a T-pose on screen and no error anywhere.
        CutsceneDefinition scene = Scene() with
        {
            Figures = [new CutsceneFigure("personality_elder", X: 0, Z: -1_700, FacingDegrees: 0, Clip: " Idle ")],
        };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        InvalidDataException refusal =
            Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(scene.Id)));

        Assert.Contains("padded", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFigureKeepsTheClipItWasGiven()
    {
        CutsceneDefinition scene = Scene() with
        {
            Figures = [new CutsceneFigure("personality_elder", X: 0, Z: -1_700, FacingDegrees: 0,
                                           Clip: "Sit_Chair_Idle")],
        };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        Assert.Equal("Sit_Chair_Idle", CutsceneFile.Load(PathFor(scene.Id)).Figures[0].Clip);
    }

    [Fact]
    public void AFigureOfRigidPartsHasNoClip()
    {
        // The ordinary case, and it has to survive as null rather than as an empty string:
        // null is a figure with no clips at all, and an empty name is a scene that meant to
        // name one.
        CutsceneDefinition scene = Scene();

        CutsceneFile.Save(scene, PathFor(scene.Id));

        Assert.Null(CutsceneFile.Load(PathFor(scene.Id)).Figures[0].Clip);
    }

    [Fact]
    public void ASceneWithNoLinesIsRefused()
    {
        CutsceneDefinition scene = Scene() with { Lines = [] };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(scene.Id)));

        Assert.Contains("no lines", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraThatRunsBackwardsIsRefused()
    {
        CutsceneDefinition scene = Scene();

        CutsceneDefinition backwards = scene with
        {
            Camera =
            [
                new CutsceneCameraKey(0, 900, 1_650, 3_400, 0, 1_350, -1_100),
                new CutsceneCameraKey(1_000, 500, 1_580, 2_600, 0, 1_300, -1_200),
                new CutsceneCameraKey(500, 400, 1_500, 2_400, 0, 1_300, -1_200),
            ],
        };

        CutsceneFile.Save(backwards, PathFor(backwards.Id));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(backwards.Id)));

        Assert.Contains("before key 1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraThatDoesNotStartAtZeroIsRefused()
    {
        CutsceneDefinition scene = Scene() with
        {
            Camera =
            [
                new CutsceneCameraKey(500, 900, 1_650, 3_400, 0, 1_350, -1_100),
                new CutsceneCameraKey(4_000, 500, 1_580, 2_600, 0, 1_300, -1_200),
            ],
        };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(scene.Id)));

        Assert.Contains("the shot at zero", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineSpokenBySomebodyNotInTheSceneIsRefused()
    {
        CutsceneDefinition scene = Scene() with
        {
            Lines = [new CutsceneLine("personality_someone_else", "Ποιος μιλάει;", 3_000)],
        };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(scene.Id)));

        Assert.Contains("not standing in the scene", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASceneNamingAnUnknownMissionIsRefused()
    {
        CutsceneDefinition scene = Scene() with { MissionId = "m_no_such_mission" };

        CutsceneFile.Save(scene, PathFor(scene.Id));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(PathFor(scene.Id)));

        Assert.Contains("m_no_such_mission", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileFromAnotherVersionIsRefused()
    {
        CutsceneFile.Save(Scene(), PathFor("m1_briefing"));

        string path = PathFor("m1_briefing");
        string[] lines = File.ReadAllLines(path);
        lines[1] = "  \"version\": 99,";
        File.WriteAllLines(path, lines);

        Assert.Throws<InvalidDataException>(() => CutsceneFile.Load(path));
    }

    [Fact]
    public void TheCatalogueSortsByIdAndFindsAMissionsScene()
    {
        CutsceneDefinition briefing = Scene("m1_briefing");
        CutsceneDefinition debrief = Scene("m1_debrief") with { Kind = CutsceneKind.Debrief, MissionId = "m1_bridgehead" };
        CutsceneDefinition other = Scene("m2_briefing") with { MissionId = "m2_ridge" };

        CutsceneFile.Save(briefing, PathFor(briefing.Id));
        CutsceneFile.Save(debrief, PathFor(debrief.Id));
        CutsceneFile.Save(other, PathFor(other.Id));

        IReadOnlyList<CutsceneDefinition> scenes = CutsceneFile.LoadAll(_directory);

        Assert.Equal(["m1_briefing", "m1_debrief", "m2_briefing"], scenes.Select(scene => scene.Id));

        Assert.Equal("m1_briefing", CutsceneFile.ForMission(scenes, "m1_bridgehead", CutsceneKind.Briefing)?.Id);
        Assert.Equal("m1_debrief", CutsceneFile.ForMission(scenes, "m1_bridgehead", CutsceneKind.Debrief)?.Id);
        Assert.Null(CutsceneFile.ForMission(scenes, "m3_industry", CutsceneKind.Briefing));
    }

    [Fact]
    public void TwoFilesClaimingOneIdAreRefused()
    {
        CutsceneFile.Save(Scene("m1_briefing"), PathFor("m1_briefing"));
        CutsceneFile.Save(Scene("m1_briefing"), Path.Combine(_directory, "copy" + CutsceneFile.Extension));

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => CutsceneFile.LoadAll(_directory));

        Assert.Contains("both claim the id", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryWithNoScenesIsNoScenes()
    {
        Assert.Empty(CutsceneFile.LoadAll(Path.Combine(_directory, "nothing-here")));
    }

    [Fact]
    public void ProblemsAreReportedWithoutThrowing()
    {
        CutsceneDefinition broken = Scene() with
        {
            Set = "",
            Figures = [],
            Lines = [new CutsceneLine("", "", 0)],
        };

        IReadOnlyList<string> problems = CutsceneFile.Problems(broken);

        Assert.Contains(problems, problem => problem.Contains("no set", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("nobody in it", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("line 0 is empty", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("time to read", StringComparison.Ordinal));
    }
}
