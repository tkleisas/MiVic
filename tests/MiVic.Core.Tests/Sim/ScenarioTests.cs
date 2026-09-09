using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The scenario builder is the simulation's notion of "the start of a match".
/// Replays depend on it producing a byte-identical world for a given seed, so it
/// is pinned here as well as exercised through the replay tests.
/// </summary>
public sealed class ScenarioTests
{
    private const int Capacity = 1024;

    private static SimWorld Build(ulong seed, ScenarioKind kind, out ScenarioSetup setup)
    {
        var world = new SimWorld(seed, Capacity);
        setup = Scenario.Build(world, kind);
        return world;
    }

    [Fact]
    public void SkirmishIsIdenticalForTheSameSeed()
    {
        SimWorld first = Build(20250101, ScenarioKind.Skirmish, out _);
        SimWorld second = Build(20250101, ScenarioKind.Skirmish, out _);

        Assert.Equal(StateHash.Compute(first), StateHash.Compute(second));
    }

    [Fact]
    public void DifferentSeedsProduceDifferentBattlefields()
    {
        SimWorld first = Build(20250101, ScenarioKind.Skirmish, out _);
        SimWorld second = Build(20250102, ScenarioKind.Skirmish, out _);

        Assert.NotEqual(StateHash.Compute(first), StateHash.Compute(second));
    }

    /// <summary>
    /// Golden hash of the skirmish's initial state. Regenerate only on a
    /// deliberate change to the starting layout: this is what catches a refactor
    /// that quietly moves a unit, which a replay would then reproduce wrongly.
    /// </summary>
    [Fact]
    public void SkirmishInitialHash_IsStable()
        => Assert.Equal(13858934364992318583UL, StateHash.Compute(Build(20250101, ScenarioKind.Skirmish, out _)));

    [Fact]
    public void SkirmishLaysOutThreeForcesOfTheRightSize()
    {
        SimWorld world = Build(20250101, ScenarioKind.Skirmish, out ScenarioSetup setup);

        Assert.Equal(3, setup.CommandCentres.Count);
        Assert.Equal((4 + Scenario.UnitsPerFaction) * 3, setup.Spawned.Count);
        Assert.Equal(setup.Spawned.Count, world.AliveCount);
    }

    [Fact]
    public void EachForceStartsWithACommandCentreAndABase()
    {
        SimWorld world = Build(20250101, ScenarioKind.Skirmish, out ScenarioSetup setup);

        Assert.Equal(3, setup.CommandCentres.Count);

        foreach (EntityId centre in setup.CommandCentres)
        {
            Assert.True(world.TryGet(centre, out Entity entity), "Command centre is not alive.");
            Assert.Equal(UnitKind.CommandCentre, entity.Kind);

            int structures = 0;

            foreach (SpawnedEntity spawned in setup.Spawned)
            {
                if (!world.TryGet(spawned.Id, out Entity candidate) || candidate.TeamId != entity.TeamId)
                {
                    continue;
                }

                if (candidate.Kind is UnitKind.CommandCentre or UnitKind.PowerPlant or UnitKind.Factory or UnitKind.DesignBureau)
                {
                    structures++;
                }
            }

            Assert.Equal(4, structures);
        }
    }

    [Fact]
    public void TeamsStartWithResourcesAndTechTierOne()
    {
        SimWorld world = Build(20250101, ScenarioKind.Skirmish, out _);

        for (int team = 0; team < 3; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            Assert.Equal(2_500, state.Materials);
            Assert.Equal(400, state.Energy);
            Assert.Equal(1, state.TechTier);
        }
    }

    [Fact]
    public void SpawnedPositionsAreTheRequestedOnes()
    {
        SimWorld world = Build(20250101, ScenarioKind.Skirmish, out ScenarioSetup setup);

        // Spawn() rewrites Y with the terrain height, so the recorded request is
        // the only way to know where a unit was meant to start.
        foreach (SpawnedEntity spawned in setup.Spawned)
        {
            Assert.True(world.TryGet(spawned.Id, out Entity entity));
            Assert.Equal(spawned.RequestedPosition.X, entity.Position.X);
            Assert.Equal(spawned.RequestedPosition.Z, entity.Position.Z);
        }
    }

    [Fact]
    public void GallerySpawnsEveryFactionAndRole()
    {
        SimWorld world = Build(20250101, ScenarioKind.ModelGallery, out ScenarioSetup setup);

        // The gallery is a parade of the nine modelled roles, not of every
        // UnitKind the catalogue knows (harvesters have no model yet).
        UnitKind[] kinds =
        [
            UnitKind.Infantry, UnitKind.Tank, UnitKind.Artillery, UnitKind.AntiAir,
            UnitKind.Aircraft, UnitKind.CommandCentre, UnitKind.PowerPlant, UnitKind.Factory, UnitKind.DesignBureau,
        ];

        Assert.Equal(kinds.Length * FactionProfile.All.Count(), setup.Spawned.Count);
        Assert.Equal(setup.Spawned.Count, world.AliveCount);

        foreach (FactionProfile profile in FactionProfile.All)
        {
            foreach (UnitKind kind in kinds)
            {
                bool found = false;

                foreach (SpawnedEntity spawned in setup.Spawned)
                {
                    if (world.TryGet(spawned.Id, out Entity entity) &&
                        entity.Faction == profile.Faction &&
                        entity.Kind == kind)
                    {
                        found = true;
                        break;
                    }
                }

                Assert.True(found, $"{profile.GreekName} has no {kind} in the gallery.");
            }
        }
    }

    [Fact]
    public void GalleryIsVisibleToThePlayer()
    {
        SimWorld world = Build(20250101, ScenarioKind.ModelGallery, out ScenarioSetup setup);

        foreach (SpawnedEntity spawned in setup.Spawned)
        {
            Assert.True(world.TryGet(spawned.Id, out Entity entity));
            Assert.Equal(0, entity.TeamId);
        }
    }
}
