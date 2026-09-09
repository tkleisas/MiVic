using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Off-map support: the orbital strike and the tactical nuke. Both are targeted
/// abilities with a cooldown, and both are gated by data rather than by a branch.
/// </summary>
public sealed class AbilityTests
{
    private static SimWorld SovietWithOrbital()
    {
        SimWorld world = new(seed: 4711, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.TechTier = 3;
        state.TechMask |= 1UL << (int)TechId.SovietOrbital;

        return world;
    }

    [Fact]
    public void AnOrbitalStrikeNeedsItsProjectAndItsStructure()
    {
        SimWorld world = SovietWithOrbital();

        Assert.True(world.CanUseAbility(0, AbilityId.OrbitalStrike, out _));

        // The project is what unlocks it: without the research the ability is inert
        // even at the right era.
        world.TeamRef(0).TechMask = 0;

        Assert.False(world.CanUseAbility(0, AbilityId.OrbitalStrike, out string reason));
        Assert.Contains("έρευνα", reason);
    }

    [Fact]
    public void AnOrbitalStrikeDamagesEnemiesAndSparesFriends()
    {
        SimWorld world = SovietWithOrbital();
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        // Unarmed structures, so the only thing that can possibly damage them is
        // the strike itself. With tanks on both sides the two of them would shoot
        // at each other and the test would pass whether or not the ability ran.
        EntityId friend = world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, centre, Fix32.Zero, 5_000);
        EntityId enemy = world.Spawn(
            Faction.Western,
            2,
            UnitKind.CommandCentre,
            new WorldPos(centre.X + 20_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        world.Enqueue(SimCommand.UseAbility(AbilityId.OrbitalStrike, centre, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(5_000, world.GetRefBySlot(friend.Slot).Health);
        Assert.True(world.GetRefBySlot(enemy.Slot).Health < 5_000, "The strike missed the enemy.");
    }

    [Fact]
    public void AnAbilityGoesOnCooldownAndCostsMaterials()
    {
        SimWorld world = SovietWithOrbital();
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        int before = world.Team(0).Materials;

        world.Enqueue(SimCommand.UseAbility(AbilityId.OrbitalStrike, centre, world.Tick + 1, 0));
        world.Step();

        AbilityCatalog.TryGet(AbilityId.OrbitalStrike, out AbilityDefinition definition);

        Assert.Equal(before - definition.MaterialCost, world.Team(0).Materials);
        Assert.False(world.CanUseAbility(0, AbilityId.OrbitalStrike, out string reason));
        Assert.Contains("αναμονή", reason);
    }

    [Fact]
    public void TheChineseCanNeverCallInANuke()
    {
        // Their ceiling is era III and a nuke is era IV, so this is not a balance
        // number that could be tuned — it is unreachable by construction.
        SimWorld world = new(seed: 12, capacity: 16);

        ref TeamState state = ref world.TeamRef(1);
        state.TechTier = 3;
        state.Materials = 10_000;

        Assert.False(world.CanUseAbility(1, AbilityId.TacticalNuke, out string reason));
        Assert.Contains("τεχνολογία 4", reason);
    }

    [Fact]
    public void ANukeNeedsAStandingNuclearPlant()
    {
        SimWorld world = new(seed: 13, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        ref TeamState state = ref world.TeamRef(2);
        state.TechTier = 4;
        state.Materials = 10_000;

        Assert.False(world.CanUseAbility(2, AbilityId.TacticalNuke, out string reason));
        Assert.Contains("NuclearPlant", reason);

        EntityId plant = world.Spawn(Faction.Western, 2, UnitKind.NuclearPlant, centre, Fix32.Zero, 4_000);

        Assert.True(world.CanUseAbility(2, AbilityId.TacticalNuke, out _));

        // Losing the plant takes the capability away with it, which is what makes
        // the plant worth raiding.
        world.Despawn(plant);

        Assert.False(world.CanUseAbility(2, AbilityId.TacticalNuke, out _));
    }

    [Fact]
    public void ANukeDoesNotDistinguishFriendFromFoe()
    {
        SimWorld world = new(seed: 14, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Western, 2, UnitKind.NuclearPlant, centre, Fix32.Zero, 4_000);

        ref TeamState state = ref world.TeamRef(2);
        state.TechTier = 4;
        state.Materials = 10_000;

        EntityId friend = world.Spawn(Faction.Western, 2, UnitKind.CommandCentre, centre, Fix32.Zero, 5_000);
        EntityId enemy = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.CommandCentre,
            new WorldPos(centre.X + 20_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        world.Enqueue(SimCommand.UseAbility(AbilityId.TacticalNuke, centre, world.Tick + 1, 2));
        world.Step();

        Assert.True(world.GetRefBySlot(friend.Slot).Health < 5_000, "The nuke spared its own side.");
        Assert.True(world.GetRefBySlot(enemy.Slot).Health < 5_000, "The nuke missed the enemy.");
    }
}
