using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The two Σοβιετικοί units added with the roster review: the Κατιούσα, whose
/// value is area saturation rather than accuracy, and the Κομισάριος, which
/// steadies the morale of friends around it.
/// </summary>
public sealed class SovietUnitTests
{
    private static SimWorld World(out WorldPos centre)
    {
        SimWorld world = new(seed: 31337, capacity: 64);
        centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        return world;
    }

    [Fact]
    public void TheKatyushaTradesAccuracyForDamage()
    {
        UnitDefinition katyusha = UnitCatalog.Get(UnitKind.RocketArtillery);
        UnitDefinition howitzer = UnitCatalog.Get(UnitKind.Artillery);

        Assert.True(katyusha.AttackDamage > howitzer.AttackDamage, "The Κατιούσα is not harder-hitting per salvo.");
        Assert.True(katyusha.AttackCooldownTicks > howitzer.AttackCooldownTicks, "The Κατιούσα reloads too quickly.");
        Assert.True(katyusha.HasSplash, "The Κατιούσα has no blast radius.");
        Assert.True(katyusha.ScatterMm > 0, "The Κατιούσα is perfectly accurate.");
    }

    [Fact]
    public void ASalvoDamagesEveryEnemyInTheBlast()
    {
        SimWorld world = World(out WorldPos centre);

        world.Spawn(Faction.Soviet, 0, UnitKind.RocketArtillery, centre, Fix32.Zero, 100_000);

        // Two enemies on the exact same spot, so any blast that reaches one
        // reaches both. Identical damage therefore proves the splash, not just a
        // pair of coincidences.
        WorldPos target = new(centre.X + 120_000, 0, centre.Z);
        EntityId first = world.Spawn(Faction.Western, 2, UnitKind.Infantry, target, Fix32.Zero, 1_000_000);
        EntityId second = world.Spawn(Faction.Western, 2, UnitKind.Infantry, target, Fix32.Zero, 1_000_000);

        world.RunTicks(2_000);

        int firstDamage = 1_000_000 - world.GetRefBySlot(first.Slot).Health;
        int secondDamage = 1_000_000 - world.GetRefBySlot(second.Slot).Health;

        Assert.True(firstDamage > 0, "No salvo ever reached the enemy stack.");
        Assert.Equal(firstDamage, secondDamage);
    }

    [Fact]
    public void NotEverySalvoLands()
    {
        SimWorld world = World(out WorldPos centre);

        world.Spawn(Faction.Soviet, 0, UnitKind.RocketArtillery, centre, Fix32.Zero, 100_000);

        WorldPos target = new(centre.X + 120_000, 0, centre.Z);
        EntityId victim = world.Spawn(Faction.Western, 2, UnitKind.Infantry, target, Fix32.Zero, 1_000_000);

        const int Window = 3_000;
        world.RunTicks(Window);

        int damage = 1_000_000 - world.GetRefBySlot(victim.Slot).Health;
        UnitDefinition katyusha = UnitCatalog.Get(UnitKind.RocketArtillery);

        // At most one shot per cooldown, and the cooldown can only shorten so far.
        int maxShots = (Window / katyusha.AttackCooldownTicks) + 1;

        Assert.True(damage > 0, "The Κατιούσα never hit anything at all.");
        Assert.True(
            damage < maxShots * katyusha.AttackDamage,
            "Every possible salvo landed, so the weapon is not scattering.");
    }

    [Fact]
    public void TheCommissarRaisesTheMoraleOfNearbyFriends()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId soldier = world.Spawn(Faction.Soviet, 0, UnitKind.Infantry, centre, Fix32.Zero, 5_000);

        world.RunTicks(200);
        int alone = world.GetRefBySlot(soldier.Slot).Morale.Raw;

        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Commissar,
            new WorldPos(centre.X + 10_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        world.RunTicks(200);
        int steadied = world.GetRefBySlot(soldier.Slot).Morale.Raw;

        Assert.True(steadied > alone, $"Morale did not rise near a commissar ({alone} -> {steadied}).");
    }
}
