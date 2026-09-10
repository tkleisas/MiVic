using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Where a scenario's bases and their starting forces are allowed to stand.
/// <para>
/// A base is the one thing in a match that cannot be moved afterwards: the whole
/// opening is played from it, so a headquarters in a lake is not a cosmetic problem,
/// it is a match that cannot be played. The positions are hardcoded and the terrain
/// comes from the seed, so whether a base lands on dry ground is luck — and on the
/// standard seed the luck runs out, which is what this file exists to keep from
/// happening again.
/// </para>
/// </summary>
public sealed class BasePlacementTests
{
    private const int Capacity = 1024;

    /// <summary>
    /// The seeds the sweep runs over. The standard seed is the one the bug was found
    /// on; the rest are there because one seed proves very little when the positions
    /// are hardcoded and the ground is generated. The last two have large lakes on
    /// them — the standard seed's deep water is a puddle next to seed 7's — so a search
    /// that only works on a shoreline fails here.
    /// </summary>
    private static readonly ulong[] Seeds =
    [
        20250101UL, 20250102UL, 20250103UL, 20250104UL, 20250105UL, 7UL, 99UL,
    ];

    /// <summary>
    /// How far a base may be moved before the move stops being "the nearest ground that
    /// fits" and becomes a different part of the map. A quarter of the battlefield: the
    /// three factions start in three corners, and a base that has walked a quarter of
    /// the way across is no longer in its corner.
    /// </summary>
    private const int MaxSensibleMoveMm = 150_000;

    /// <summary>
    /// The bug. On the standard seed every base was asked for a position in deep water
    /// and nothing checked: the Soviet headquarters, its power plant, its factory and
    /// its design bureau all stood in a lake, and the base was unreachable for the whole
    /// match because no ground unit could get to it.
    /// </summary>
    [Fact]
    public void TheStandardSeedsBasesStandOnGround()
    {
        SimWorld world = Build(20250101);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

        Assert.Equal(3, setup.BaseSites.Count);

        foreach (BaseSitePlacement baseSite in setup.BaseSites)
        {
            Assert.True(
                IsSolidGround(world, baseSite.Placed),
                $"{baseSite.Faction}'s base stands at ({baseSite.Placed.X},{baseSite.Placed.Z}) mm on " +
                $"{SurfaceAt(world, baseSite.Placed)}, not on ground: it was asked for " +
                $"({baseSite.Requested.X},{baseSite.Requested.Z}) mm, which is {SurfaceAt(world, baseSite.Requested)}");

            Assert.True(
                baseSite.PatchFound,
                $"{baseSite.Faction}'s base found no patch of ground within {SimWorld.BaseSearchRadiusCells} cells " +
                "and had to fall back to the nearest single solid cell");

            // Moved, but not flung: the search takes the nearest ground that fits, and on
            // this seed that is inside a quarter of the map.
            int moved = IntMath.Distance(
                baseSite.Placed.X - baseSite.Requested.X, 0, baseSite.Placed.Z - baseSite.Requested.Z);

            Assert.True(
                moved <= MaxSensibleMoveMm,
                $"{baseSite.Faction}'s base moved {moved / 1_000.0:F1} m from where it was asked for, further than " +
                $"the {MaxSensibleMoveMm / 1_000} m a base may be pushed on this seed");
        }
    }

    /// <summary>
    /// A base needs room, not a point: the structures it starts with and the ground to
    /// add more on. This asserts the patch rather than the centre, because one dry cell
    /// in the middle of a lake satisfies every check about the centre alone — which is
    /// the shape of the bug being fixed.
    /// </summary>
    [Fact]
    public void EachBaseSiteHasRoomForABase()
    {
        SimWorld world = Build(20250101);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

        int required = RequiredSolidCells;

        Assert.True(required * 10 >= SimWorld.BaseSitePatchCells * 9, "the patch threshold is no longer 'most' of it");

        foreach (BaseSitePlacement baseSite in setup.BaseSites)
        {
            int solid = CountPatch(world, baseSite.Placed, SimWorld.BaseSiteRadiusCells);
            int core = CountPatch(world, baseSite.Placed, SimWorld.BaseSiteCoreCells);

            Assert.True(
                solid >= required,
                $"{baseSite.Faction}'s base at ({baseSite.Placed.X},{baseSite.Placed.Z}) mm has {solid} solid cells " +
                $"of {SimWorld.BaseSitePatchCells} in its patch, and a base needs {required}");

            Assert.True(
                core == CoreCells,
                $"{baseSite.Faction}'s base has {core} solid cells of {CoreCells} in the core of its patch, and the " +
                "yard the base and its units stand on has to be solid in full");

            // The four structures of the base, where the scenario actually put them: the
            // patch is a claim about the ground, and this is the claim being cashed.
            int structures = 0;

            foreach (SpawnedEntity spawned in setup.Spawned)
            {
                if (!world.TryGet(spawned.Id, out Entity entity) || entity.TeamId != TeamOf(baseSite))
                {
                    continue;
                }

                if (entity.Kind is not (UnitKind.CommandCentre or UnitKind.PowerPlant or UnitKind.Factory or UnitKind.DesignBureau))
                {
                    continue;
                }

                structures++;

                Assert.True(
                    IsSolidGround(world, entity.Position),
                    $"{baseSite.Faction}'s {entity.Kind} stands at ({entity.Position.X},{entity.Position.Z}) mm on " +
                    $"{SurfaceAt(world, entity.Position)}");

                // Nudged is not the same as scattered: a structure snapped across the
                // shore leaves the base it was laid out for, and a base whose buildings
                // are a hundred metres apart is not the base the scenario described.
                int fromOffset = IntMath.Distance(
                    entity.Position.X - baseSite.Placed.X - OffsetOf(entity.Kind).dx,
                    0,
                    entity.Position.Z - baseSite.Placed.Z - OffsetOf(entity.Kind).dz);

                Assert.True(
                    fromOffset <= MaxStructureDisplacementMm,
                    $"{baseSite.Faction}'s {entity.Kind} stands {fromOffset / 1_000.0:F1} m from the offset the " +
                    $"scenario lays it out at, further than the {MaxStructureDisplacementMm / 1_000} m a structure " +
                    "may be nudged to reach ground");
            }

            Assert.Equal(4, structures);
        }
    }

    /// <summary>
    /// Every unit of every team starts on ground its own movement class can occupy.
    /// <para>
    /// This is the test that would have caught the whole class of bug: a tank that
    /// starts in deep water is a tank that is stuck for the rest of the match, and a
    /// headquarters that starts in it is a base nobody can reach. Aircraft are the
    /// exception and are counted separately — they fly, so water under them is nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryStartingUnitStandsOnGroundItCanOccupy()
    {
        SimWorld world = Build(20250101);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

        int ground = 0;
        int air = 0;
        string report = CheckStartingForces(world, setup, out ground, out air);

        Assert.True(report.Length == 0, report);

        // A check that looked at nothing passes for the wrong reason.
        Assert.Equal((4 + Scenario.UnitsPerFaction) * 3, ground + air);
        Assert.True(air > 0, "the scenario has no aircraft in it, so the exception is not being exercised");
        Assert.True(ground > air, "far more of a starting force is on the ground than in the air");
    }

    /// <summary>
    /// And the same on other seeds. Hardcoded positions against generated terrain means
    /// the standard seed's bases being on land proves very little: it was the unlucky
    /// seed, and a fix that works only there is not a fix.
    /// </summary>
    [Fact]
    public void BasesAndForcesStandOnGroundOnOtherSeedsToo()
    {
        int bases = 0;
        int ground = 0;
        int air = 0;

        foreach (ulong seed in Seeds)
        {
            SimWorld world = Build(seed);
            ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

            foreach (BaseSitePlacement baseSite in setup.BaseSites)
            {
                bases++;

                Assert.True(
                    world.IsBaseSite(baseSite.Placed),
                    $"seed {seed}: {baseSite.Faction}'s base at ({baseSite.Placed.X},{baseSite.Placed.Z}) mm has " +
                    $"{world.BaseSiteSolidCells(baseSite.Placed)} solid cells of {SimWorld.BaseSitePatchCells} " +
                    $"in its patch, and it was asked for ({baseSite.Requested.X},{baseSite.Requested.Z}) mm");

                Assert.True(
                    baseSite.PatchFound,
                    $"seed {seed}: {baseSite.Faction}'s base found no patch within {SimWorld.BaseSearchRadiusCells} " +
                    $"cells of ({baseSite.Requested.X},{baseSite.Requested.Z}) mm");
            }

            string report = CheckStartingForces(world, setup, out int groundHere, out int airHere);

            Assert.True(report.Length == 0, $"seed {seed}: {report}");

            ground += groundHere;
            air += airHere;
        }

        Assert.Equal(Seeds.Length * 3, bases);
        Assert.Equal(Seeds.Length * (4 + Scenario.UnitsPerFaction) * 3, ground + air);
    }

    /// <summary>
    /// A base that moved moved because it had to, and not far: it is put down on the
    /// nearest ground that fits, so a seed whose hardcoded position is already good
    /// keeps the layout it always had, and a base that did move is still within a
    /// quarter of the map of where it was meant to be.
    /// </summary>
    [Fact]
    public void ABaseOnlyMovesWhenTheGroundLeavesItNoChoice()
    {
        foreach (ulong seed in Seeds)
        {
            SimWorld world = Build(seed);
            ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

            foreach (BaseSitePlacement baseSite in setup.BaseSites)
            {
                if (baseSite.Placed == baseSite.Requested)
                {
                    continue;
                }

                Assert.False(
                    world.IsBaseSite(baseSite.Requested),
                    $"seed {seed}: {baseSite.Faction}'s base was moved off " +
                    $"({baseSite.Requested.X},{baseSite.Requested.Z}) mm, which had room for it all along");

                int distance = IntMath.Distance(
                    baseSite.Placed.X - baseSite.Requested.X,
                    0,
                    baseSite.Placed.Z - baseSite.Requested.Z);

                int wantedCell = world.Navigation.IndexOfWorld(baseSite.Requested);
                int placedCell = world.Navigation.IndexOfWorld(baseSite.Placed);
                int cells = Math.Max(
                    Math.Abs(world.Navigation.CellX(placedCell) - world.Navigation.CellX(wantedCell)),
                    Math.Abs(world.Navigation.CellZ(placedCell) - world.Navigation.CellZ(wantedCell)));

                Assert.True(
                    cells <= SimWorld.BaseSearchRadiusCells,
                    $"seed {seed}: {baseSite.Faction}'s base moved {cells} cells, past the search radius of " +
                    $"{SimWorld.BaseSearchRadiusCells}");

                // "Not absurdly far", without a number picked out of the air: the three
                // factions start in three different places, so a base belongs to whichever
                // of them it ended up nearest to. A base that walked across the map until
                // it was closer to somebody else's corner than to its own is no longer that
                // faction's start.
                foreach (BaseSitePlacement other in setup.BaseSites)
                {
                    if (other.Faction == baseSite.Faction)
                    {
                        continue;
                    }

                    int toOther = IntMath.Distance(
                        baseSite.Placed.X - other.Requested.X,
                        0,
                        baseSite.Placed.Z - other.Requested.Z);

                    Assert.True(
                        distance < toOther,
                        $"seed {seed}: {baseSite.Faction}'s base moved {distance / 1_000.0:F1} m and is now " +
                        $"{toOther / 1_000.0:F1} m from where {other.Faction} was meant to start — nearer to " +
                        "another faction's corner than to its own");
                }
            }
        }
    }

    /// <summary>
    /// A search that finds nothing says so. The contract is a returned false and the
    /// nearest single cell of solid ground rather than the water it was asked for: the
    /// silent version of this is a base standing in a lake with nothing to say about it.
    /// </summary>
    [Fact]
    public void ASiteThatCannotBeFoundIsReported()
    {
        SimWorld world = Build(20250101);

        int water = FindSurface(world, TerrainType.DeepWater);

        Assert.True(water >= 0, "the standard map has no deep water on it, so this test proves nothing");

        WorldPos wanted = CentreOf(world, water);

        Assert.False(world.IsBaseSite(wanted), "the middle of a lake is not a base site");

        Assert.False(
            world.TryFindBaseSite(wanted, searchRadiusCells: 0, out WorldPos site),
            "a base asked for the middle of a lake reported that it found a site on the spot");

        Assert.True(
            IsSolidGround(world, site),
            $"a base that found no site was left at ({site.X},{site.Z}) mm, which is {SurfaceAt(world, site)}");
    }

    /// <summary>
    /// Walks a scenario's starting forces and describes the first unit standing where it
    /// cannot, or returns an empty string when every one of them can. The counts come
    /// back so a caller can assert how many units were actually looked at.
    /// </summary>
    private static string CheckStartingForces(SimWorld world, ScenarioSetup setup, out int ground, out int air)
    {
        ground = 0;
        air = 0;

        foreach (SpawnedEntity spawned in setup.Spawned)
        {
            Assert.True(world.TryGet(spawned.Id, out Entity entity), "a spawned entity is not alive");

            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (definition.Movement == MovementClass.Air)
            {
                air++;
                continue;
            }

            ground++;

            int cell = world.Navigation.IndexOfWorld(entity.Position);

            if (cell < 0 || !world.Navigation.IsWalkable(cell) ||
                !world.TerrainTypes.IsPassable(cell, definition.Movement))
            {
                return
                    $"unit {ground + air} of {setup.Spawned.Count} (team {entity.TeamId} {entity.Faction} " +
                    $"{entity.Kind}, {definition.Movement}) stands at ({entity.Position.X},{entity.Position.Z}) mm on " +
                    $"{SurfaceAt(world, entity.Position)}, which it cannot occupy — {ground} ground units and " +
                    $"{air} aircraft checked";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The team a faction plays as in the skirmish. The scenario hands them out in the
    /// order the three powers are listed, which is the game's premise: the two socialist
    /// powers allied against the Δυτικοί.
    /// </summary>
    private static int TeamOf(BaseSitePlacement baseSite) => baseSite.Faction switch
    {
        Faction.Soviet => 0,
        Faction.Chinese => 1,
        Faction.Western => 2,
        _ => -1,
    };

    /// <summary>
    /// Where the scenario plants each structure of a starting base, relative to its
    /// centre: the headquarters at the middle and the three support buildings on three
    /// of the four corners.
    /// </summary>
    private static (int dx, int dz) OffsetOf(UnitKind kind) => kind switch
    {
        UnitKind.PowerPlant => (45_000, 45_000),
        UnitKind.Factory => (-45_000, 45_000),
        UnitKind.DesignBureau => (45_000, -45_000),
        _ => (0, 0),
    };

    /// <summary>
    /// How far a structure may end up from the offset it was laid out at. The patch is
    /// mostly solid rather than entirely solid, so a corner of the base can land on the
    /// pond the patch tolerates and be nudged to the nearest ground; a hundred metres is
    /// where a nudge stops being one and the base stops being one base.
    /// </summary>
    private const int MaxStructureDisplacementMm = 100_000;

    /// <summary>Solid cells a base's patch has to offer.</summary>
    private static int RequiredSolidCells
        => ((SimWorld.BaseSitePatchCells * SimWorld.BaseSiteMinSolidPermille) + 999) / 1_000;

    /// <summary>Cells in the core of a patch, which has to be solid in full.</summary>
    private static int CoreCells => ((SimWorld.BaseSiteCoreCells * 2) + 1) * ((SimWorld.BaseSiteCoreCells * 2) + 1);

    /// <summary>
    /// Solid cells in the square of the given radius around a site, walked here rather
    /// than asked of the simulation, so that the test states the requirement instead of
    /// repeating the implementation's own answer back at it.
    /// </summary>
    private static int CountPatch(SimWorld world, WorldPos centre, int radius)
    {
        int cell = world.Navigation.IndexOfWorld(centre);
        int centreX = world.Navigation.CellX(Math.Max(cell, 0));
        int centreZ = world.Navigation.CellZ(Math.Max(cell, 0));
        int solid = 0;

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (IsSolidGround(world, world.Navigation.IndexOf(centreX + dx, centreZ + dz)))
                {
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>True when a navigation cell is dry, walkable ground rather than water, lava or a cliff.</summary>
    private static bool IsSolidGround(SimWorld world, int cell)
        => cell >= 0 && world.Navigation.IsWalkable(cell) && IsSolidGround(world, world.Navigation.CentreOf(cell));

    /// <summary>True when the ground under a position is neither water nor lava.</summary>
    private static bool IsSolidGround(SimWorld world, WorldPos position)
    {
        int cell = world.TerrainTypes.IndexOfWorld(position.X, position.Z);

        if (cell < 0)
        {
            return false;
        }

        TerrainType type = world.TerrainTypes.TypeAt(cell);

        return type is not (TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava);
    }

    private static TerrainType SurfaceAt(SimWorld world, WorldPos position)
    {
        int cell = world.TerrainTypes.IndexOfWorld(position.X, position.Z);

        return cell < 0 ? TerrainType.Grass : world.TerrainTypes.TypeAt(cell);
    }

    private static WorldPos CentreOf(SimWorld world, int terrainCell)
    {
        int size = world.TerrainTypes.Size;
        int x = terrainCell % size;
        int z = terrainCell / size;

        return new WorldPos(
            world.TerrainTypes.OriginMm + (x * world.TerrainTypes.CellSizeMm) + (world.TerrainTypes.CellSizeMm / 2),
            0,
            world.TerrainTypes.OriginMm + (z * world.TerrainTypes.CellSizeMm) + (world.TerrainTypes.CellSizeMm / 2));
    }

    private static int FindSurface(SimWorld world, TerrainType wanted)
    {
        for (int cell = 0; cell < world.TerrainTypes.CellCount; cell++)
        {
            if (world.TerrainTypes.TypeAt(cell) == wanted)
            {
                return cell;
            }
        }

        return -1;
    }

    /// <summary>An empty world with its terrain generated; each test builds its own scenario.</summary>
    private static SimWorld Build(ulong seed) => new(seed, Capacity);
}
