using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// The map file format: a seed, edits over the ground the seed generates, and the mission
/// the ground is shaped for. Three things are under test. That the format carries what the
/// demonstration author wrote, through a file and back. That the application runs in the
/// order the world resolves in — heights, then the re-derived passes, then paints, then
/// placements — which is why a placement legal on generated ground can be refused on the
/// shaped ground. And that every refusal is the environment's own sentence: the placement
/// rules, asked on the edited land, in the words a player's construction would be shown.
/// </summary>
public sealed class MapFileTests
{
    private const int Capacity = 1024;

    private readonly string _path;

    public MapFileTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mivic-map-{Guid.NewGuid():N}.map.json");
    }

    /// <summary>A minimal map: the standard duel, no edits, no placements.</summary>
    private static MapDefinition Map() => MapFrom(MissionCatalog.Require("m1_bridgehead"));

    private static MapDefinition MapFrom(MissionDefinition mission) => new(
        Seed: mission.Seed,
        TerrainEdits: [],
        Structures: [],
        Mission: mission);

    [Fact]
    public void AMapRoundTrips()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        MapDefinition map = new(
            mission.Seed,
            [
                new TerrainEdit(TerrainEditKind.AdjustHeight, CellX: 30, CellZ: 30, DeltaMm: -8_000),
                new TerrainEdit(TerrainEditKind.Paint, X: 100_000, Z: 100_000, Type: TerrainType.Mud),
                new TerrainEdit(TerrainEditKind.AdjustHeight, X: -50_000, Z: 50_000, DeltaMm: 12_000),
            ],
            [new StructurePlacement(UnitKind.RadarStation, X: 200_000, Z: -200_000, Team: 0)],
            mission);

        MapFile.Save(map, _path);
        MapDefinition loaded = MapFile.Load(_path);

        Assert.Equal(map.Seed, loaded.Seed);
        Assert.Equal(map.TerrainEdits.Count, loaded.TerrainEdits.Count);

        for (int i = 0; i < map.TerrainEdits.Count; i++)
        {
            Assert.Equal(map.TerrainEdits[i], loaded.TerrainEdits[i]);
        }

        Assert.Equal(map.Structures, loaded.Structures);
        Assert.Equal(mission.Id, loaded.Mission.Id);
        Assert.Equal(map.EffectiveMission.Seed, loaded.EffectiveMission.Seed);
    }

    [Fact]
    public void TheCarriersSeedIsTheOneThatWins()
    {
        // The ground and the battle are one world, and the file carries the seed once: a
        // mission body inside a map that carries a different seed is overridden, because
        // the terrain was generated from the map's.
        MissionDefinition mission = MissionCatalog.Require("m1_bridgehead");

        MapDefinition map = MapFrom(mission with { Seed = 999 });
        MapFile.Save(map, _path);
        MapDefinition loaded = MapFile.Load(_path);

        Assert.Equal(map.Seed, loaded.EffectiveMission.Seed);
    }

    [Fact]
    public void AFileFromAnotherVersionIsRefused()
    {
        MapFile.Save(MapFrom(MissionCatalog.Require("m1_bridgehead")), _path);

        string[] lines = File.ReadAllLines(_path);
        lines[1] = "  \"version\": 999,";

        File.WriteAllLines(_path, lines);

        Assert.Throws<InvalidDataException>(() => MapFile.Load(_path));
    }

    [Fact]
    public void AnEditWhollyOutsideTheMapContributesNothing()
    {
        MapDefinition map = new(
            MissionCatalog.Require("m1_bridgehead").Seed,
            [new TerrainEdit(TerrainEditKind.AdjustHeight, CellX: 9999, CellZ: 9999, DeltaMm: -5_000)],
            [],
            MissionCatalog.Require("m1_bridgehead"));

        MapFile.Save(map, _path);
        MapFile.Load(_path);

        SimWorld world = new(map.Seed, Capacity, map.Mission.Roster);

        // The edit's centre is off the map, so its coverage is empty: an empty edit
        // contributes nothing rather than crashing the build. The file loads, the
        // application runs, and the world is the ground the seed generates — an edit that
        // says nothing is the one case the loader tolerates, and the test pins which case
        // that is.
        Scenario.BuildMap(world, map);

        SimWorld plain = new(map.Seed, Capacity, map.Mission.Roster);
        Scenario.BuildMap(plain, MapFrom(map.Mission));

        Assert.Equal(
            world.TerrainTypes.RawTypes.ToArray(),
            plain.TerrainTypes.RawTypes.ToArray());
    }

    [Fact]
    public void APlacementOnWaterIsRefusedInTheEnvironmentsOwnWords()
    {
        // The ground is shaped first — a bowl deep enough that the bands re-run and the
        // centre comes out water — and the placement that follows is asked the question a
        // player's construction is asked, on the edited land. The refusal is the
        // environment's own sentence, which is what makes this the environment and not a
        // second opinion.
        MissionDefinition mission = MissionCatalog.Require("m1_bridgehead");

        MapDefinition map = new(
            mission.Seed,
            [
                new TerrainEdit(TerrainEditKind.AdjustHeight, CellX: 30, CellZ: 30, DeltaMm: -TerrainMaxHeight),
            ],
            [new StructurePlacement(UnitKind.GunEmplacement, X: 0, Z: 0, Team: 0)],
            mission);

        MapFile.Save(map, _path);
        MapFile.Load(_path);

        SimWorld world = new(map.Seed, Capacity, map.Mission.Roster);

        // The bowl is at cell (30, 30), and the placement is on that cell's ground: the
        // world position the edit resolves to is where the question is asked.
        int sampleX = world.Terrain.OriginMm + (30 * world.Terrain.CellSizeMm) + (world.Terrain.CellSizeMm / 2);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(
            () => Scenario.BuildMap(world, map with
            {
                Structures = [new StructurePlacement(UnitKind.GunEmplacement, X: sampleX, Z: sampleX, Team: 0)],
            }));

        Assert.Contains("GunEmplacement", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditedGroundIsWhatTheBattleIsFoughtOn()
    {
        // The whole point of shaping first: a cell raised above the water line and a cell
        // lowered below it read as what they now are, through the same passes the world
        // was built with — and two worlds built from the same map read the same.
        MissionDefinition mission = MissionCatalog.Require("m1_bridgehead");

        MapDefinition map = new(
            mission.Seed,
            [
                new TerrainEdit(TerrainEditKind.AdjustHeight, CellX: 20, CellZ: 20, DeltaMm: TerrainMaxHeight),
                new TerrainEdit(TerrainEditKind.Paint, CellX: 20, CellZ: 20, Type: TerrainType.Snow),
            ],
            [],
            mission);

        SimWorld world = new(map.Seed, Capacity, map.Mission.Roster);
        int sampleBefore = world.Terrain.HeightAtIndex(20 * world.Terrain.Size + 20);

        Scenario.BuildMap(world, map);

        int sampleAfter = world.Terrain.HeightAtIndex(20 * world.Terrain.Size + 20);

        Assert.True(sampleAfter > sampleBefore, $"The raise moved {sampleAfter - sampleBefore} mm; the edit did nothing.");

        int painted = (20 * world.TerrainTypes.Size) + 20;
        Assert.Equal(TerrainType.Snow, world.TerrainTypes.TypeAt(painted));

        // And the same map built twice is the same ground, which is the determinism the
        // whole format rests on.
        SimWorld second = new(map.Seed, Capacity, map.Mission.Roster);
        Scenario.BuildMap(second, map);

        Assert.Equal(
            world.TerrainTypes.RawTypes.ToArray(),
            second.TerrainTypes.RawTypes.ToArray());
    }

    private static int TerrainMaxHeight => SimConstants.TerrainMaxHeightMm;

    // ---------------------------------------------------------------- the authored force

    /// <summary>
    /// A map may name its whole force: the composition and the position of every building and
    /// every unit, instead of the layout the mission would have generated. These tests pin the
    /// four things that make that a feature rather than a different bug — that the file carries
    /// it, that an exact force is the <em>whole</em> force, that the generated mode still adds
    /// to what it always generated, and that a force which cannot be played is refused where
    /// the author is looking.
    /// </summary>
    [Fact]
    public void AnAuthoredForceRoundTrips()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        MapDefinition map = new(
            mission.Seed,
            [],
            [
                new StructurePlacement(UnitKind.CommandCentre, X: 10_000, Z: 10_000, Team: 0),
                new StructurePlacement(UnitKind.CommandCentre, X: -10_000, Z: -10_000, Team: 2),
            ],
            mission,
            ExactForce: true)
        {
            Units =
            [
                new UnitPlacement(UnitKind.Tank, X: 20_000, Z: 20_000, Team: 0),
                new UnitPlacement(UnitKind.Infantry, X: 25_000, Z: 20_000, Team: 0),
            ],
        };

        MapFile.Save(map, _path);
        MapDefinition loaded = MapFile.Load(_path);

        Assert.True(loaded.ExactForce);
        Assert.Equal(map.Structures, loaded.Structures);
        Assert.Equal(map.Units, loaded.Units);
        Assert.True(loaded.HasAuthoredForce);
    }

    [Fact]
    public void AnExactForceIsTheWholeForce()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        // A generated build first, only to learn where this seed puts each team's base: those
        // are legal command-centre sites, so the authored force can reuse them.
        SimWorld probe = new(mission.Seed, Capacity, mission.Roster);
        ScenarioSetup generated = Scenario.BuildMission(probe, mission);
        WorldPos playerSite = generated.BaseSites[0].Placed;
        WorldPos enemySite = generated.BaseSites[1].Placed;

        SimWorld world = new(mission.Seed, Capacity, mission.Roster);
        WorldPos tankSite = LegalUnitSite(world, UnitKind.Tank, playerSite);

        MapDefinition map = new(
            mission.Seed,
            [],
            [
                new StructurePlacement(UnitKind.CommandCentre, playerSite.X, playerSite.Z, Team: 0),
                new StructurePlacement(UnitKind.CommandCentre, enemySite.X, enemySite.Z, Team: 2),
            ],
            mission,
            ExactForce: true)
        {
            Units = [new UnitPlacement(UnitKind.Tank, tankSite.X, tankSite.Z, Team: 0)],
        };

        ScenarioSetup setup = Scenario.BuildMap(world, map);

        // Exactly three things stand: the generator laid out nothing at all. Not one tank of
        // the mission's eighteen, not the power plant or factory a base comes with.
        Assert.Equal(3, world.AliveCount);
        Assert.Equal(3, setup.Spawned.Count);
        Assert.Equal(2, setup.CommandCentres.Count);
        Assert.Equal(1, world.CountOf(0, UnitKind.Tank));
        Assert.Equal(0, world.CountOf(0, UnitKind.Infantry));
        Assert.Equal(0, world.CountOf(0, UnitKind.Factory));

        // The mission is still the mission: objectives and triggers need it, and a team whose
        // force the author wrote still has an economy to use it with.
        Assert.Equal(mission.Id, world.Mission?.Id);
        Assert.True(world.Team(0).Materials > 0, "An authored force opened with an empty treasury.");
    }

    [Fact]
    public void AGeneratedMapAddsTheAuthoredForce()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        SimWorld generatedWorld = new(mission.Seed, Capacity, mission.Roster);
        Scenario.BuildMission(generatedWorld, mission);
        int baseline = generatedWorld.AliveCount;

        SimWorld world = new(mission.Seed, Capacity, mission.Roster);
        WorldPos playerSite = generatedWorld.Navigation.CentreOf(generatedWorld.Navigation.NearestWalkable(0));
        WorldPos tankSite = LegalUnitSite(world, UnitKind.Tank, playerSite);

        // ExactForce is false, which is the default and what every map did before the flag
        // existed: the mission's layout, and then the author's placements on top of it.
        MapDefinition map = new(mission.Seed, [], [], mission)
        {
            Units = [new UnitPlacement(UnitKind.Tank, tankSite.X, tankSite.Z, Team: 0)],
        };

        Scenario.BuildMap(world, map);

        Assert.Equal(baseline + 1, world.AliveCount);
    }

    [Fact]
    public void AnExactForceWithoutAHeadquartersIsRefused()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        // Team 0 has a headquarters and team 2 does not, and both are judged: a side that
        // cannot be beaten is not a side, so the loader refuses the file rather than opening a
        // match that cannot be decided.
        MapDefinition map = new(
            mission.Seed,
            [],
            [new StructurePlacement(UnitKind.CommandCentre, X: 10_000, Z: 10_000, Team: 0)],
            mission,
            ExactForce: true);

        MapFile.Save(map, _path);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => MapFile.Load(_path));

        Assert.Contains("command centre", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlacementInTheWrongListIsRefused()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        MapDefinition map = new(
            mission.Seed,
            [],
            [
                new StructurePlacement(UnitKind.CommandCentre, X: 10_000, Z: 10_000, Team: 0),
                // A tank moves, so it belongs in 'units'; the two lists are one concept split by
                // which rule applies, and a file that crosses them is told so.
                new StructurePlacement(UnitKind.Tank, X: 20_000, Z: 20_000, Team: 0),
                new StructurePlacement(UnitKind.CommandCentre, X: -10_000, Z: -10_000, Team: 2),
            ],
            mission,
            ExactForce: true);

        IReadOnlyList<string> problems = MapFile.ForceProblems(map);

        Assert.Contains(problems, problem => problem.Contains("belongs in 'units'", StringComparison.Ordinal));
    }

    [Fact]
    public void APlacementForATeamTheMatchDoesNotDeclareIsRefused()
    {
        // m4_pass is a duel: teams 0 and 2, no Κινέζοι ally. A placement for team 1 names a
        // side this match does not have, and the force is refused rather than laid out for a
        // team that is not in the battle.
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        MapDefinition map = new(
            mission.Seed,
            [],
            [new StructurePlacement(UnitKind.RadarStation, X: 10_000, Z: 10_000, Team: 1)],
            mission);

        IReadOnlyList<string> problems = MapFile.ForceProblems(map);

        Assert.Contains(problems, problem => problem.Contains("team 1", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAuthoredUnitOffTheMapIsRefused()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");

        SimWorld probe = new(mission.Seed, Capacity, mission.Roster);
        ScenarioSetup generated = Scenario.BuildMission(probe, mission);

        MapDefinition map = new(
            mission.Seed,
            [],
            [
                new StructurePlacement(UnitKind.CommandCentre, generated.BaseSites[0].Placed.X, generated.BaseSites[0].Placed.Z, Team: 0),
                new StructurePlacement(UnitKind.CommandCentre, generated.BaseSites[1].Placed.X, generated.BaseSites[1].Placed.Z, Team: 2),
            ],
            mission,
            ExactForce: true)
        {
            // Five kilometres off a map six hundred metres across.
            Units = [new UnitPlacement(UnitKind.Tank, X: 5_000_000, Z: 0, Team: 0)],
        };

        SimWorld world = new(mission.Seed, Capacity, mission.Roster);

        InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => Scenario.BuildMap(world, map));

        Assert.Contains("έξω από τον χάρτη", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An authored structure is raised with its role's own hit points.
    /// <para>
    /// It used to be raised with none: the map path passed a health of zero, which the spawn
    /// took literally, so the gun emplacement on the shipped demonstration map stood at nought
    /// hit points and the first rifle round that touched it destroyed it. A layout passes the
    /// numbers it was balanced with; an author has no number to give, so the role's own is the
    /// only honest one.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAuthoredStructureIsRaisedWithItsOwnHitPoints()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");
        SimWorld world = new(mission.Seed, Capacity, mission.Roster);
        WorldPos site = LegalStructureSite(world, UnitKind.GunEmplacement);

        MapDefinition map = new(
            mission.Seed,
            [],
            [new StructurePlacement(UnitKind.GunEmplacement, site.X, site.Z, Team: 0)],
            mission);

        Scenario.BuildMap(world, map);

        int expected = UnitCatalog.Get(UnitKind.GunEmplacement).Health;
        bool raised = false;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.Kind == UnitKind.GunEmplacement)
            {
                Assert.Equal(expected, entity.Health);
                raised = true;
            }
        }

        Assert.True(raised, "The authored emplacement was not raised at all.");
    }

    /// <summary>
    /// The first position on the map a structure of this role may be founded on, scanning a
    /// coarse lattice so the test does not depend on a hard-coded cell.
    /// </summary>
    private static WorldPos LegalStructureSite(SimWorld world, UnitKind kind)
    {
        for (int x = -280_000; x <= 280_000; x += 20_000)
        {
            for (int z = -280_000; z <= 280_000; z += 20_000)
            {
                var candidate = new WorldPos(x, 0, z);

                if (world.CanPlaceStructure(kind, candidate, out _) && world.IsSiteClear(kind, candidate, out _))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException($"No ground on this seed will hold a {kind}.");
    }

    /// <summary>
    /// The first position near <paramref name="near"/> the role's own movement class can stand
    /// on, scanning by navigation cells. The tests use it so an authored position is legal
    /// ground on whatever seed the mission carries rather than on a hard-coded cell.
    /// </summary>
    private static WorldPos LegalUnitSite(SimWorld world, UnitKind kind, WorldPos near)
    {
        for (int step = 0; step <= 40; step++)
        {
            var candidate = new WorldPos(near.X + (step * 9_000), 0, near.Z);

            if (world.CanPlaceUnit(kind, candidate, out _))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No ground a {kind} can stand on near ({near.X}, {near.Z}).");
    }
}
