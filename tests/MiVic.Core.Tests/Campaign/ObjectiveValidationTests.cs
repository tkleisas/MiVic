using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// <b>The objectives, asked of the world the mission opens in — and of the match that is playing
/// it.</b>
/// <para>
/// A trigger that fires early is a scene in the wrong place. An objective is a win condition, so
/// an objective the world has <em>already decided</em> is the mission itself: one that is decided
/// against the player cannot be won at all, and one decided for them is handed over before the
/// first tick. Neither is visible to anything static, because both are facts about the map the
/// scenario lays out, which is why the validator builds that map and asks every objective of it
/// through the mission system's own evaluation — see <see cref="MissionSystem.Verdict"/>.
/// </para>
/// <para>
/// Every test here is written on a <b>mission built for the case</b> rather than on a shipped one
/// bent to fit, the way the trigger layer's tests are: an objective is a whole mission's win
/// condition, and a campaign mission brings its own objectives, its own match and its own
/// layout to a test about one of them. The four missions this repository does ship are checked
/// as they are, at the bottom, because the first honest run of a validator should pass on the
/// content that exists.
/// </para>
/// </summary>
public sealed class ObjectiveValidationTests
{
    private const int Capacity = 1024;

    /// <summary>
    /// A mission with this repository's own layout and the objectives a test writes: a small force
    /// on each side of a two-faction match, no time limit, and no triggers, so that the only thing
    /// the validator can complain about is the objective under test.
    /// </summary>
    private static MissionDefinition Mission(params ObjectiveDefinition[] objectives) => new(
        Id: "objective_test",
        GreekTitle: "Δοκιμή στόχων",
        GreekBriefing: "Μια αποστολή γραμμένη για τη δοκιμή που την κρίνει.",
        Seed: 20250104UL,
        PlayerBase: new WorldPos(-180_000, 0, -180_000),
        AllyBase: default,
        EnemyBase: new WorldPos(0, 0, 200_000),
        PlayerUnits: 6,
        AllyUnits: 0,
        EnemyUnits: 8,
        Objectives: objectives,
        TimeLimitTicks: 0)
    {
        Roster = MatchRoster.Duel,
    };

    private static SimWorld Build(MissionDefinition mission)
    {
        var world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, Capacity, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    /// <summary>
    /// Where a team's headquarters actually stands, asked of the world the validator will build for
    /// itself: the scenario searches for a base site, so the coordinates a mission asks for are a
    /// wish, and a circle has to be put where the army was put.
    /// </summary>
    private static WorldPos Headquarters(SimWorld world, int team)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == UnitKind.CommandCentre)
            {
                return entity.Position;
            }
        }

        throw new InvalidOperationException($"Team {team} has no headquarters.");
    }

    private static string Joined(IReadOnlyList<string> problems) => string.Join(" | ", problems);

    /// <summary>
    /// The circle a formation is gathered in, for a test that needs the opening army inside one.
    /// The ranks are fourteen columns of 5.5 m and a couple of rows, so the formation reaches about
    /// 37 m from its centre; a hundred and twenty covers the whole of it wherever the jitter of the
    /// seed and the ground rule have moved a unit.
    /// </summary>
    private const int FormationRadiusMm = 120_000;

    // ---------------------------------------------------------------- decided against the player

    /// <summary>
    /// <b>The unwinnable mission: a denial whose circle the enemy already stands in.</b>
    /// <para>
    /// This is the case the whole sweep exists for. The circle a mission writes is a place, and a
    /// mission that writes it over the enemy's own camp has shipped a denial that has already
    /// failed on the check that first asks it — so the mission is lost before the player has done
    /// anything, and its author finds out by losing. The validator names the objective, the count
    /// the map answered with and the number of them that fail it, and the mission itself is then
    /// run to prove the complaint is about the game rather than about the validator: the objective
    /// fails on its first check and the outcome is defeat.
    /// </para>
    /// </summary>
    [Fact]
    public void ADenialTheEnemyAlreadyStandsInIsAnUnwinnableMission()
    {
        MissionDefinition template = Mission(new ObjectiveDefinition(
            ObjectiveKind.DenyArea,
            "Οι Δυτικοί δεν πρέπει να φτάσουν στο σημείο διαφυγής.",
            Team: 0,
            TargetTeam: 2,
            TargetCount: 4,
            DeadlineTick: 600));

        SimWorld opening = TriggerSystem.OpeningWorld(template);

        // The circle is put on the enemy's own headquarters, which is where its opening formation
        // gathers. Four of them fail the objective, and the whole camp is standing in it.
        WorldPos camp = Headquarters(opening, 2);
        int inside = opening.CountUnitsInArea(2, camp.X, camp.Z, FormationRadiusMm);

        Assert.True(inside >= 4, $"The test's own layout puts only {inside} of the enemy in the circle.");

        MissionDefinition mission = template with
        {
            Objectives =
            [
                template.Objectives[0] with
                {
                    CentreX = camp.X,
                    CentreZ = camp.Z,
                    RadiusMm = FormationRadiusMm,
                },
            ],
        };

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        Assert.Single(problems);
        Assert.Contains("objective 0 (DenyArea)", problems[0]);
        Assert.Contains("has already failed in the world the mission opens in", problems[0]);
        Assert.Contains($"{inside} units inside the circle and 4 of them fail it", problems[0]);
        Assert.Contains("the mission is unwinnable", problems[0]);

        // And the game agrees with the complaint, which is the only thing that makes the complaint
        // worth having: the same objective, in a world of the same mission, fails on the very first
        // check and loses the mission.
        SimWorld world = Build(mission);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsFailed, "The denial the validator refused did not fail in the mission.");
        Assert.True(world.Objectives[0].Progress >= 4, "The objective failed without the intrusion that fails it.");
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    /// <summary>
    /// The two kinds whose decision is a failure of the army rather than a threshold: a team the
    /// match does not declare stands in no structures and owns no command centre, so the first
    /// check fails both. They are the only way the opening world can decide these two kinds — a
    /// team that <em>is</em> playing always opens with a base — and the sweep asks them anyway,
    /// which is what catches this. The roster complaint beside it is the same mistake seen from the
    /// other end: the two lines are the team that is not playing and the objective that fails
    /// because of it.
    /// </summary>
    [Fact]
    public void ASurvivalObjectiveForATeamThatIsNotPlayingFailsOnItsFirstCheck()
    {
        MissionDefinition mission = Mission(new ObjectiveDefinition(
            ObjectiveKind.SurviveTicks,
            "Επιβιώστε με τους συμμάχους.",
            Team: 1,
            DeadlineTick: 600));

        string problems = Joined(TriggerSystem.Validate(mission));

        Assert.Contains("objective 0 survives with team 1, which this mission's match does not declare", problems);
        Assert.Contains("objective 0 (SurviveTicks) has already failed in the world the mission opens in", problems);
        Assert.Contains("team 1 stands in 0 structures", problems);
    }

    /// <summary>The same shape on the other kind that asks about an army rather than a number.</summary>
    [Fact]
    public void ACommandCentreObjectiveForATeamThatIsNotPlayingFailsOnItsFirstCheck()
    {
        MissionDefinition mission = Mission(new ObjectiveDefinition(
            ObjectiveKind.ProtectCommandCentre,
            "Το κέντρο διοίκησης των συμμάχων πρέπει να επιβιώσει.",
            Team: 1,
            DeadlineTick: 600));

        string problems = Joined(TriggerSystem.Validate(mission));

        Assert.Contains(
            "objective 0 protects the command centre of team 1, which this mission's match does not declare",
            problems);
        Assert.Contains(
            "objective 0 (ProtectCommandCentre) has already failed in the world the mission opens in",
            problems);
        Assert.Contains("team 1 has 0 command centres", problems);
    }

    // ---------------------------------------------------------------- decided for the player

    /// <summary>
    /// <b>A hold the opening formation already meets.</b> Six units of the side asked to hold a
    /// circle that the six units it opens with are standing in: the hold clock is running from the
    /// first evaluation, so the objective completes on time with nothing done. The complaint says
    /// so, and the mission then shows it — one check later the clock has started and the objective
    /// is still open, which is exactly "free in half a minute" rather than "already won".
    /// </summary>
    [Fact]
    public void AHoldTheOpeningFormationAlreadyMeets()
    {
        MissionDefinition template = Mission(new ObjectiveDefinition(
            ObjectiveKind.HoldArea,
            "Κρατήστε έξι μονάδες στο σημείο για τριάντα δευτερόλεπτα.",
            Team: 0,
            TargetCount: 6,
            HoldTicks: 600,
            DeadlineTick: 9_000));

        SimWorld opening = TriggerSystem.OpeningWorld(template);
        WorldPos camp = Headquarters(opening, 0);
        int inside = opening.CountUnitsInArea(0, camp.X, camp.Z, FormationRadiusMm);

        Assert.True(inside >= 6, $"The test's own layout puts only {inside} of the army in the circle.");

        MissionDefinition mission = template with
        {
            Objectives =
            [
                template.Objectives[0] with
                {
                    CentreX = camp.X,
                    CentreZ = camp.Z,
                    RadiusMm = FormationRadiusMm,
                },
            ],
        };

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        Assert.Single(problems);
        Assert.Contains("objective 0 (HoldArea)", problems[0]);
        Assert.Contains("is already satisfied by the world the mission opens in", problems[0]);
        Assert.Contains($"team 0 already has {inside} units inside the circle and the hold asks for 6", problems[0]);
        Assert.Contains("completes with nothing done", problems[0]);

        // The game's own evidence for the same reading: the clock is running on the first check.
        SimWorld world = Build(mission);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsPending);
        Assert.Equal(MissionSystem.CheckInterval, world.Objectives[0].HoldProgress);
    }

    /// <summary>
    /// <b>A stockpile the mission hands the player.</b> The target is not a number the test picked
    /// out of the air: it is what the scenario gives the team, so the objective is the author's own
    /// wish "stockpile what you already have" written as one.
    /// </summary>
    [Fact]
    public void AMaterialsTargetTheSideStartsWith()
    {
        MissionDefinition template = Mission(new ObjectiveDefinition(
            ObjectiveKind.AccumulateMaterials,
            "Συγκεντρώστε πόρους.",
            Team: 0,
            DeadlineTick: 12_000));

        int start = TriggerSystem.OpeningWorld(template).TeamRef(0).Materials;
        Assert.True(start > 0, "The scenario gave the team nothing to start with.");

        MissionDefinition mission = template with
        {
            Objectives = [template.Objectives[0] with { MaterialsTarget = start }],
        };

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        Assert.Single(problems);
        Assert.Contains("objective 0 (AccumulateMaterials)", problems[0]);
        Assert.Contains($"starts with {start} materials and the objective asks for {start}", problems[0]);
    }

    /// <summary>
    /// <b>A tier the side already has — and the number is one, not nought.</b> Every team is
    /// created at tier 1, which is what "reach a tier" has to beat, so a target of 1 is met before
    /// the first tick and a target of 0 could never be anything else. The tier is asked of the
    /// world rather than written into the test, because it is the world's answer that decides the
    /// objective and the test's job is to know it when it sees it.
    /// </summary>
    [Fact]
    public void ATierTheSideAlreadyHas()
    {
        MissionDefinition template = Mission(new ObjectiveDefinition(
            ObjectiveKind.ReachTechTier,
            "Φτάστε σε τεχνολογικό επίπεδο.",
            Team: 0,
            DeadlineTick: 12_000));

        int tier = TriggerSystem.OpeningWorld(template).TeamRef(0).TechTier;
        Assert.True(tier > 0, "The side opens below tier one, which this test is written against.");

        MissionDefinition mission = template with
        {
            Objectives = [template.Objectives[0] with { TierTarget = tier }],
        };

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        Assert.Single(problems);
        Assert.Contains("objective 0 (ReachTechTier)", problems[0]);
        Assert.Contains($"already has tech tier {tier} and the objective asks for {tier}", problems[0]);
    }

    /// <summary>
    /// <b>A structure count of zero.</b> The ledger the kind reads starts where the scenario left it
    /// — at nought, in a world where nothing has been destroyed — so "destroy none of them" is a
    /// wish already granted. The count of one beside it is the control: the same objective with a
    /// number in it is a mission, and the sweep says nothing about it.
    /// </summary>
    [Fact]
    public void AStructureTargetOfNone()
    {
        ObjectiveDefinition none = new(
            ObjectiveKind.DestroyStructures,
            "Καταστρέψτε μηδέν δυτικές θέσεις.",
            TargetTeam: 2,
            TargetCount: 0);

        IReadOnlyList<string> problems = TriggerSystem.Validate(Mission(none));

        Assert.Single(problems);
        Assert.Contains("objective 0 (DestroyStructures)", problems[0]);
        Assert.Contains("team 2 has lost 0 structures and the objective asks for 0", problems[0]);

        // The control, and it is the same objective: one structure destroyed is a mission the
        // player has to fight for, and the validator has nothing to say about it.
        Assert.Empty(TriggerSystem.Validate(Mission(none with { TargetCount = 1 })));
    }

    /// <summary>
    /// <b>The one kind no world can decide.</b> A scripted objective is brought to complete by the
    /// mission and by nothing else — no count, no deadline, no circle — so the sweep asks it and
    /// finds nothing, which is what makes the sweep a sweep rather than a list of the kinds
    /// somebody remembered. It is checked here as a mission that <em>is</em> complete: a scripted
    /// objective that a trigger completes, beside a tier the side does not have, validates empty.
    /// </summary>
    [Fact]
    public void AScriptedObjectiveIsTheOneKindNoWorldCanDecide()
    {
        MissionDefinition mission = Mission(
            new ObjectiveDefinition(ObjectiveKind.Scripted, "Διαλύστε την ενέδρα."),
            new ObjectiveDefinition(
                ObjectiveKind.ReachTechTier,
                "Προαιρετικά: φτάστε σε τεχνολογικό επίπεδο 3.",
                Team: 0,
                TierTarget: 3,
                IsPrimary: false))
        with
        {
            Triggers =
            [
                new TriggerDefinition(
                    "broken",
                    new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 300),
                    [new TriggerAction(TriggerActionKind.CompleteObjective, Objective: 0)]),
            ],
        };

        Assert.Equal(
            OpeningVerdict.Undecided,
            MissionSystem.Verdict(TriggerSystem.OpeningWorld(mission), mission.Objectives[0]));

        Assert.Empty(TriggerSystem.Validate(mission));
    }

    // ---------------------------------------------------------------- the match, not the map

    /// <summary>
    /// <b>An objective about a team the match does not declare, which is a case two-faction matches
    /// made expressible.</b>
    /// <para>
    /// The mission is written the way every mission this repository shipped before a match could
    /// declare its cast was written — the Δυτικοί are team 2, which is what the campaign's own
    /// objectives name — and it is then fought against the Κινέζοι, with the Δυτικοί absent. Under
    /// the skirmish it was written for it is a mission and there is nothing wrong with it; under
    /// the roster it is actually fought with, its two objectives are about a team that is not
    /// playing, and the two are dead in opposite directions. A destruction count that can never
    /// rise can never be completed, and a denial nobody can ever intrude on can never be failed:
    /// it is completed by its own deadline with the player having done nothing, so the roster
    /// decides the mission rather than the battle.
    /// </para>
    /// </summary>
    [Fact]
    public void AnObjectiveAboutATeamTheMatchDoesNotDeclareIsRefused()
    {
        MissionDefinition authored = Mission(
            new ObjectiveDefinition(
                ObjectiveKind.DestroyStructures,
                "Καταστρέψτε δύο δυτικές θέσεις.",
                TargetTeam: 2,
                TargetCount: 2),
            new ObjectiveDefinition(
                ObjectiveKind.DenyArea,
                "Οι Δυτικοί δεν πρέπει να φτάσουν στο σημείο διαφυγής.",
                Team: 0,
                TargetTeam: 2,
                TargetCount: 4,
                CentreX: -270_000,
                CentreZ: -270_000,
                RadiusMm: 45_000,
                DeadlineTick: 3_600));

        // The match it was written for declares team 2, so both objectives are about somebody.
        Assert.Empty(TriggerSystem.Validate(authored with { Roster = MatchRoster.StandardSkirmish }));

        IReadOnlyList<string> problems = TriggerSystem.Validate(authored with { Roster = MatchRoster.Rivals });

        Assert.Equal(2, problems.Count);
        Assert.Contains(
            "objective 0 counts the structures lost by team 2, which this mission's match does not declare",
            problems[0]);
        Assert.Contains("can never be completed", problems[0]);
        Assert.Contains(
            "objective 1 denies the area to team 2, which this mission's match does not declare",
            problems[1]);
        Assert.Contains("can never be failed", problems[1]);
    }

    /// <summary>
    /// <b>The four missions this repository ships validate clean, and that is a test rather than a
    /// remark.</b>
    /// <para>
    /// The validator's first honest run has to pass on the content that exists, or it is a check
    /// nobody keeps: a mission the campaign ships is played, and a complaint about one of them is
    /// either a bug in the mission or a bug in the check. It is also the reason the sweep reports
    /// rather than assumes: m2 asks for six units at the middle of the map and its army opens in a
    /// corner, m4's denial is 570 m from the enemy's base, and m3's tier and materials targets are
    /// above what the side starts with — all of which are now <em>said</em> by the code rather than
    /// checked by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void NoShippedMissionHasAnObjectiveTheOpeningWorldDecides()
    {
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            Assert.DoesNotContain(
                TriggerSystem.Validate(mission),
                problem => problem.Contains("objective", StringComparison.Ordinal));
        }
    }
}
