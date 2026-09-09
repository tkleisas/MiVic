using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Κινέζοι automata: robots and drones. Their defining property is not a stat, it is
/// that there is nobody aboard — so they never rout, they never drink, and they are
/// the only hardware restricted to a single faction.
/// </summary>
public sealed class AutomatonTests
{
    [Fact]
    public void OnlyTheChineseCanFieldAutomata()
    {
        Assert.True(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.RobotInfantry, techTier: 3));
        Assert.True(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.Drone, techTier: 3));

        Assert.False(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.RobotInfantry, techTier: 3));
        Assert.False(UnitCatalog.IsUnlocked(Faction.Western, UnitKind.Drone, techTier: 3));

        // And they are absent from the other factions' build lists entirely.
        Assert.DoesNotContain(
            UnitCatalog.BuildableBy(Faction.Western),
            definition => definition.Kind is UnitKind.RobotInfantry or UnitKind.Drone);
    }

    [Fact]
    public void AutomataCostEnergyInsteadOfWater()
    {
        foreach (UnitKind kind in (UnitKind[])[UnitKind.RobotInfantry, UnitKind.Drone])
        {
            UnitDefinition definition = UnitCatalog.Get(kind);

            Assert.True(definition.IsAutomaton, $"{kind} is not flagged as an automaton.");
            Assert.Equal(0, definition.WaterCost);
            Assert.True(definition.EnergyCost > 0, $"{kind} needs no power at all.");
        }
    }

    [Fact]
    public void DronesFlyAndOnlyAntiAirCanEngageThem()
    {
        Assert.True(UnitCatalog.Flies(UnitKind.Drone));
        Assert.False(UnitCatalog.Flies(UnitKind.RobotInfantry));

        SimWorld world = new(seed: 4242, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId drone = world.Spawn(
            Faction.Chinese,
            1,
            UnitKind.Drone,
            new WorldPos(centre.X + 60_000, 0, centre.Z),
            Fix32.Zero,
            500);

        world.Spawn(Faction.Western, 2, UnitKind.Tank, centre, Fix32.Zero, 5_000);

        world.RunTicks(200);

        Assert.Equal(500, world.GetRefBySlot(drone.Slot).Health);
    }

    [Fact]
    public void AutomataNeverRout()
    {
        // Surrounded, outnumbered and shot at: a manned unit's morale collapses and
        // it runs. A robot has no morale to collapse.
        SimWorld world = new(seed: 99, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId robot = world.Spawn(Faction.Chinese, 1, UnitKind.RobotInfantry, centre, Fix32.Zero, 4_000);
        int moraleAtSpawn = world.GetRefBySlot(robot.Slot).Morale.Raw;

        for (int i = 1; i <= 6; i++)
        {
            world.Spawn(
                Faction.Western,
                2,
                UnitKind.Infantry,
                new WorldPos(centre.X + (i * 8_000), 0, centre.Z),
                Fix32.Zero,
                1_000);
        }

        world.RunTicks(400);

        ref Entity entity = ref world.GetRefBySlot(robot.Slot);

        Assert.False(entity.Routed, "A robot routed.");
        Assert.Equal(moraleAtSpawn, entity.Morale.Raw);
    }
}
