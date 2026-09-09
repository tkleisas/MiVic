using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The Δυτικοί signature: an army that has to be paid for. Propaganda sets the
/// morale baseline; contract troops stop fighting the moment the money stops.
/// </summary>
public sealed class UpkeepTests
{
    /// <summary>
    /// A Western team with no structures at all, so it has no income and the bill
    /// is the only thing that moves the treasury. A command centre would earn 7 a
    /// tick and quietly pay every bill, which is exactly the confound these tests
    /// exist to avoid.
    /// </summary>
    private static SimWorld WesternBase(int materials)
    {
        SimWorld world = new(seed: 808, capacity: 64);

        ref TeamState state = ref world.TeamRef(2);
        state.Materials = materials;
        state.Energy = 10_000;
        state.Water = 10_000;

        return world;
    }

    private static WorldPos SpawnPoint(SimWorld world, int index)
    {
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        return new WorldPos(centre.X + ((index + 1) * 12_000), 0, centre.Z);
    }

    [Fact]
    public void PropagandaIsBilledPerArmedUnit()
    {
        SimWorld world = WesternBase(materials: 1_000);

        // No army, no bill.
        world.Step();
        Assert.Equal(0, world.Team(2).UpkeepPerTick);
        Assert.True(world.Team(2).PropagandaPaid);

        // Eight armed units is one point of propaganda.
        for (int i = 0; i < 8; i++)
        {
            world.Spawn(Faction.Western, 2, UnitKind.Infantry, SpawnPoint(world, i), Fix32.Zero, 1_000);
        }

        world.Step();

        Assert.Equal(1, world.Team(2).UpkeepPerTick);
    }

    [Fact]
    public void AnUnfundedArmyLosesMorale()
    {
        static int MoraleTarget(int materials)
        {
            SimWorld world = WesternBase(materials);

            for (int i = 0; i < 8; i++)
            {
                world.Spawn(Faction.Western, 2, UnitKind.Infantry, SpawnPoint(world, i), Fix32.Zero, 1_000);
            }

            world.RunTicks(MoraleSystem.ScanInterval + 1);
            return world.GetRefBySlot(0).MoraleTargetRaw;
        }

        int funded = MoraleTarget(materials: 1_000);
        int unfunded = MoraleTarget(materials: 0);

        Assert.True(
            unfunded < funded,
            $"An unfunded army was as willing as a funded one ({unfunded} vs {funded}).");
    }

    [Fact]
    public void MercenariesCostAWageAndOnlyTheWestCanHireThem()
    {
        UnitDefinition mercenary = UnitCatalog.Get(UnitKind.Mercenary);

        Assert.True(mercenary.WagePerTick > 0, "A mercenary costs no wage.");
        Assert.Equal(Faction.Western, mercenary.OnlyFor);
        Assert.False(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.Mercenary, techTier: 5));
        Assert.False(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.Mercenary, techTier: 3));

        SimWorld world = WesternBase(materials: 1_000);
        world.Spawn(Faction.Western, 2, UnitKind.Mercenary, SpawnPoint(world, 0), Fix32.Zero, 1_000);
        world.Step();

        Assert.Equal(mercenary.WagePerTick, world.Team(2).WagesPerTick);
        Assert.True(world.Team(2).WagesPaid);
    }

    [Fact]
    public void UnpaidMercenariesDownTools()
    {
        // Wages are owed from the first tick, so a treasury of zero means the
        // contract lapses immediately and the unit stops fighting.
        SimWorld world = WesternBase(materials: 0);

        EntityId mercenary = world.Spawn(Faction.Western, 2, UnitKind.Mercenary, SpawnPoint(world, 0), Fix32.Zero, 4_000);

        world.RunTicks(400);

        ref Entity entity = ref world.GetRefBySlot(mercenary.Slot);

        Assert.False(world.Team(2).WagesPaid);
        Assert.True(entity.Routed, "An unpaid mercenary kept fighting.");
    }
}
