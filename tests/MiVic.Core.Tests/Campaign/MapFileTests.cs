using MiVic.Core.Campaign;
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
    public void AHeightEditOutsideTheMapIsRefused()
    {
        MapDefinition map = new(
            MissionCatalog.Require("m1_bridgehead").Seed,
            [new TerrainEdit(TerrainEditKind.AdjustHeight, CellX: 9999, CellZ: 9999, DeltaMm: -5_000)],
            [],
            MissionCatalog.Require("m1_bridgehead"));

        MapFile.Save(map, _path);
        MapFile.Load(_path);

        SimWorld world = new(map.Seed, Capacity, map.Mission.Roster);

        Assert.Throws<InvalidDataException>(() => Scenario.BuildMap(world, map));
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
}
