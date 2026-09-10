using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Ground churn: the mechanic that makes ground pressure dynamic. A column of heavy
/// armour churns its own route into a bog; light infantry walking the same line
/// barely mark it.
/// </summary>
public sealed class ChurnTests
{
    private static SimWorld World(out WorldPos centre)
    {
        SimWorld world = new(seed: 31_337, capacity: 32);
        centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        return world;
    }

    /// <summary>Drives a unit back and forth so it wears the cells under it.</summary>
    private static void Wear(SimWorld world, EntityId unit, WorldPos centre, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            WorldPos goal = i % 2 == 0
                ? new WorldPos(centre.X + 60_000, 0, centre.Z)
                : centre;

            world.OrderMove(unit, goal, 0);
            world.RunTicks(60);
        }
    }

    [Fact]
    public void DrivingOverGroundWearsIt()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId unit = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, centre, Fix32.FromInt(400), 5_000);
        int cell = world.Navigation.IndexOfWorld(centre);

        Assert.Equal(0, world.TerrainTypes.ChurnAt(cell));

        Wear(world, unit, centre, 3);

        Assert.True(world.TerrainTypes.ChurnAt(cell) > 0, "Driving over the cell left no mark.");
    }

    [Fact]
    public void WornGroundCostsMore()
    {
        SimWorld world = World(out WorldPos centre);
        int cell = world.Navigation.IndexOfWorld(centre);

        TerrainLayer terrain = world.TerrainTypes;
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);

        int before = terrain.CostPermille(cell, context.Movement, context.GroundPressurePermille);

        terrain.AddChurn(cell, 200);

        int after = terrain.CostPermille(cell, context.Movement, context.GroundPressurePermille);

        Assert.True(after > before, $"Churn did not raise the cost ({before} -> {after}).");
    }

    [Fact]
    public void HeavierGroundPressureChurnsMore()
    {
        // The asymmetry made physical: the same distance, the same ground, two
        // different armies.
        static int WearFor(Faction faction)
        {
            SimWorld world = World(out WorldPos centre);
            EntityId unit = world.Spawn(faction, 0, UnitKind.Tank, centre, Fix32.FromInt(400), 5_000);

            Wear(world, unit, centre, 2);

            return world.TerrainTypes.ChurnAt(world.Navigation.IndexOfWorld(centre));
        }

        int light = WearFor(Faction.Soviet);   // 750 permille of baseline pressure
        int heavy = WearFor(Faction.Chinese);  // 1250 permille

        Assert.True(light > 0, "Even the light hull left no mark.");
        Assert.True(heavy > light, $"A heavy hull churned no more than a light one ({heavy} vs {light}).");
    }

    [Fact]
    public void ChurnSettlesOverTime()
    {
        SimWorld world = World(out WorldPos centre);
        int cell = world.Navigation.IndexOfWorld(centre);

        world.TerrainTypes.AddChurn(cell, 120);
        int worn = world.TerrainTypes.ChurnAt(cell);

        Assert.True(worn > 0);

        // Enough decay passes to erase it.
        world.RunTicks((ChurnSystem.DecayInterval * (worn + 2)) + 2);

        Assert.Equal(0, world.TerrainTypes.ChurnAt(cell));
    }

    [Fact]
    public void ChurnIsPartOfTheStateHash()
    {
        SimWorld a = World(out WorldPos centre);
        SimWorld b = World(out centre);

        Assert.Equal(StateHash.Compute(a), StateHash.Compute(b));

        a.TerrainTypes.AddChurn(a.Navigation.IndexOfWorld(centre), 90);

        Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));
    }
}
