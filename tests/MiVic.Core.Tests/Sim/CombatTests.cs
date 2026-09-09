using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class CombatTests
{
    private static SimWorld World() => new(seed: 20250101, capacity: 64);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(faction, team, kind, new WorldPos(x, 0, z), Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick), UnitCatalog.Get(kind).Health);

    [Fact]
    public void UnitsEngageEnemiesInRange()
    {
        SimWorld world = World();
        EntityId soviet = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        EntityId western = Spawn(world, Faction.Western, 2, UnitKind.Tank, 30_000, 0);

        world.RunTicks(60);

        Assert.True(world.TryGet(western, out Entity target));
        Assert.True(target.Health < UnitCatalog.Get(UnitKind.Tank).Health, "The Western tank took no damage.");
        Assert.True(world.TryGet(soviet, out _));
    }

    [Fact]
    public void OutOfRangeUnitsDoNotFire()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        EntityId western = Spawn(world, Faction.Western, 2, UnitKind.Tank, 300_000, 0);

        world.RunTicks(60);

        Assert.True(world.TryGet(western, out Entity target));
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, target.Health);
    }

    [Fact]
    public void SustainedFireDestroysAUnit()
    {
        SimWorld world = World();

        // Four tanks against one infantryman: 4 * 35 damage every 24 ticks.
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, -20_000, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 20_000);
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 20_000, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, -20_000);

        EntityId victim = Spawn(world, Faction.Western, 2, UnitKind.Infantry, 0, 0);

        world.RunTicks(120);

        Assert.False(world.IsValid(victim), "The infantryman survived four tanks.");
    }

    [Fact]
    public void TanksCannotHitAircraft()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        EntityId aircraft = Spawn(world, Faction.Western, 2, UnitKind.Aircraft, 20_000, 0);

        world.RunTicks(60);

        Assert.True(world.TryGet(aircraft, out Entity plane));
        Assert.Equal(UnitCatalog.Get(UnitKind.Aircraft).Health, plane.Health);
    }

    [Fact]
    public void AntiAirCanHitAircraft()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.AntiAir, 0, 0);
        EntityId aircraft = Spawn(world, Faction.Western, 2, UnitKind.Aircraft, 20_000, 0);

        world.RunTicks(60);

        Assert.True(world.TryGet(aircraft, out Entity plane));
        Assert.True(plane.Health < UnitCatalog.Get(UnitKind.Aircraft).Health, "Anti-air failed to damage an aircraft.");
    }

    [Fact]
    public void FriendlyUnitsAreNeverTargeted()
    {
        SimWorld world = World();
        EntityId ally = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 20_000, 0);

        world.RunTicks(60);

        Assert.True(world.TryGet(ally, out Entity friendly));
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, friendly.Health);
    }

    [Fact]
    public void AttackOrderClosesTheDistance()
    {
        SimWorld world = World();
        EntityId attacker = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        EntityId victim = Spawn(world, Faction.Western, 2, UnitKind.Infantry, 250_000, 0);

        world.Enqueue(SimCommand.Attack(attacker, victim, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.TryGet(attacker, out Entity unit));
        Assert.True(unit.HasAttackOrder);
        Assert.Equal(victim.Slot, unit.TargetSlot);

        int startDistance = unit.Position.HorizontalDistanceTo(world.GetRefBySlot(victim.Slot).Position);
        world.RunTicks(100);
        world.TryGet(attacker, out Entity moved);

        Assert.True(
            moved.Position.HorizontalDistanceTo(world.GetRefBySlot(victim.Slot).Position) < startDistance,
            "The attacker never moved towards its target.");
    }

    [Fact]
    public void KillingUnitsRaisesTheTeamsCasualtyCount()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);
        EntityId victim = Spawn(world, Faction.Western, 2, UnitKind.Infantry, 10_000, 0);

        // The count decays over time, so check it at the moment of the kill.
        bool raised = false;

        for (int tick = 0; tick < 200 && !raised; tick++)
        {
            world.Step();

            if (!world.IsValid(victim))
            {
                raised = world.Team(2).RecentCasualties > 0;
            }
        }

        Assert.False(world.IsValid(victim), "The infantryman was never killed.");
        Assert.True(raised, "The kill did not raise the team's casualty count.");
    }

    [Fact]
    public void LowMoraleSlowsReloading()
    {
        static int CooldownAfterShot(int moraleRaw)
        {
            SimWorld world = World();
            EntityId attacker = Spawn(world, Faction.Western, 2, UnitKind.Tank, 0, 0);
            Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 30_000, 0);

            world.GetRefBySlot(attacker.Slot).Morale = Fix32.FromRaw(moraleRaw);

            // One tick to acquire and fire.
            world.Step();
            world.Step();

            return world.GetRefBySlot(attacker.Slot).AttackCooldown;
        }

        int steady = CooldownAfterShot(65_536);

        // Above the rout threshold, but well below full: the unit still fights,
        // just slower.
        int shaken = CooldownAfterShot(40_000);

        Assert.True(steady > 0, "The steady unit never fired.");
        Assert.True(shaken > steady, $"Shaken cooldown {shaken} was not longer than steady {steady}.");
    }
}

public sealed class MoraleTests
{
    private static SimWorld World() => new(seed: 20250101, capacity: 64);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(faction, team, kind, new WorldPos(x, 0, z), Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick), UnitCatalog.Get(kind).Health);

    /// <summary>
    /// Places a lone armoured unit among three enemies. The subject is a tank so
    /// it survives the measurement window; the point is its morale, not its life.
    /// </summary>
    private static int MoraleOfOutnumberedUnit(Faction faction, int team, int enemyTeam, Faction enemyFaction, int ticks)
    {
        SimWorld world = World();
        EntityId subject = Spawn(world, faction, team, UnitKind.Tank, 0, 0);

        for (int i = 0; i < 3; i++)
        {
            Spawn(world, enemyFaction, enemyTeam, UnitKind.Infantry, 20_000 + (i * 5_000), 0);
        }

        world.RunTicks(ticks);

        return world.TryGet(subject, out Entity unit) ? unit.Morale.Raw : 0;
    }

    [Fact]
    public void OutnumberedUnitsLoseMorale()
    {
        SimWorld world = World();
        EntityId lonely = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);

        int start = world.GetRefBySlot(lonely.Slot).Morale.Raw;

        for (int i = 0; i < 3; i++)
        {
            Spawn(world, Faction.Western, 2, UnitKind.Infantry, 20_000 + (i * 5_000), 0);
        }

        world.RunTicks(40);

        Assert.True(world.TryGet(lonely, out Entity unit), "The subject died during the measurement window.");
        Assert.True(unit.Morale.Raw < start, $"Morale did not fall: {start} -> {unit.Morale.Raw}");
    }

    [Fact]
    public void WesternMoraleIsMoreFragileThanSoviet()
    {
        // Identical pressure, different factions: this is the Δυτικοί weakness.
        int soviet = MoraleOfOutnumberedUnit(Faction.Soviet, 0, 2, Faction.Western, 40);
        int western = MoraleOfOutnumberedUnit(Faction.Western, 2, 0, Faction.Soviet, 40);

        Assert.True(western < soviet, $"Δυτικοί morale {western} was not below Σοβιετικοί {soviet}.");
    }

    [Fact]
    public void MoraleRecoversWithoutEnemies()
    {
        SimWorld world = World();
        EntityId unit = Spawn(world, Faction.Western, 2, UnitKind.Infantry, 0, 0);

        world.GetRefBySlot(unit.Slot).Morale = Fix32.FromRaw(10_000);
        world.RunTicks(120);

        Assert.True(world.TryGet(unit, out Entity recovered));
        Assert.True(recovered.Morale.Raw > 10_000);
    }

    [Fact]
    public void BrokenUnitsRout()
    {
        SimWorld world = World();
        EntityId unit = Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 0, 0);
        Spawn(world, Faction.Western, 2, UnitKind.Infantry, 30_000, 0);

        world.GetRefBySlot(unit.Slot).Morale = Fix32.FromRaw(1_000);

        // Morale is evaluated on its scan interval, not every tick.
        world.RunTicks(MoraleSystem.ScanInterval);

        Assert.True(world.TryGet(unit, out Entity broken));
        Assert.True(broken.Routed, "A unit at 0.015 morale did not rout.");
    }

    [Fact]
    public void RoutedUnitsDoNotFire()
    {
        SimWorld world = World();
        EntityId attacker = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 0, 0);

        world.GetRefBySlot(attacker.Slot).Morale = Fix32.FromRaw(1_000);
        world.RunTicks(MoraleSystem.ScanInterval);

        Assert.True(world.GetRefBySlot(attacker.Slot).Routed);
        Assert.Equal(-1, world.GetRefBySlot(attacker.Slot).TargetSlot);

        // The victim appears only after the unit has broken.
        EntityId victim = Spawn(world, Faction.Western, 2, UnitKind.Infantry, 30_000, 0);

        // Hold the unit in panic: a lone enemy nearby would otherwise let a
        // Σοβιετικοί unit rally back and fire, which is the intended behaviour.
        for (int tick = 0; tick < 40; tick++)
        {
            world.GetRefBySlot(attacker.Slot).Morale = Fix32.FromRaw(1_000);
            world.Step();
        }

        Assert.True(world.GetRefBySlot(attacker.Slot).Routed, "The unit rallied while held in panic.");
        Assert.True(world.TryGet(victim, out Entity target));
        Assert.Equal(UnitCatalog.Get(UnitKind.Infantry).Health, target.Health);
    }

    [Fact]
    public void RoutingSendsTheUnitAwayFromTheEnemy()
    {
        SimWorld world = World();
        EntityId unit = Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 0, 0);
        Spawn(world, Faction.Western, 2, UnitKind.Infantry, 40_000, 0);

        world.GetRefBySlot(unit.Slot).Morale = Fix32.FromRaw(1_000);
        world.RunTicks(MoraleSystem.ScanInterval);

        Assert.True(world.TryGet(unit, out Entity broken));
        Assert.True(broken.HasMoveGoal, "A routed unit did not receive a retreat order.");

        int startX = broken.Position.X;
        world.RunTicks(120);

        Assert.True(world.TryGet(unit, out Entity fled));
        Assert.True(fled.Position.X < startX, "The unit did not fall back away from the enemy.");
    }

    [Fact]
    public void BuildingsDoNotHaveMoralePressure()
    {
        SimWorld world = World();
        EntityId headquarters = Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 0, 0);

        world.RunTicks(100);

        Assert.True(world.TryGet(headquarters, out Entity building));
        Assert.False(building.Routed);
    }
}
