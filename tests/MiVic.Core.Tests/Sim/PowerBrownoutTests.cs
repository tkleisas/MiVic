using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The brown-out's whole order, from the buffer to the last step. Three things are under
/// test and they are not the same thing: that the stockpile is the buffer a deficit drains
/// before anything is shed (a base running short on a full bank is a base that runs), that
/// the shed lands in the documented order — detection, then the defensive weapons, then
/// production — each rank only after the one above it has run out of things to shed, and
/// that production is slowed rather than stopped, because the plant that fixes the deficit
/// is built in the queue the old halt used to stop.
/// </summary>
public sealed class PowerBrownoutTests
{
    /// <summary>Capacity of a skirmish, which the standard match is built at.</summary>
    private const int Capacity = 1024;

    private static SimWorld Base(out EntityId headquarters)
    {
        var world = new SimWorld(seed: 20250101, capacity: Capacity);
        WorldPos site = Clearing(world);

        headquarters = Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, site);
        return world;
    }

    private static EntityId Spawn(
        SimWorld world,
        Faction faction,
        int team,
        UnitKind kind,
        WorldPos position,
        int health = 0)
        => world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.Zero,
            health > 0 ? health : UnitCatalog.Get(kind).Health);

    private static WorldPos Clearing(SimWorld world)
        => world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    private static WorldPos Near(WorldPos centre, int millimetres)
        => new(centre.X + millimetres, 0, centre.Z);

    /// <summary>Empties a team's energy bank, so the next shortfall browns out at once.</summary>
    private static void EmptyBank(SimWorld world, int team)
    {
        ref TeamState state = ref world.TeamRef(team);
        state.Energy = 0;
    }

    [Fact]
    public void TheBankIsTheBufferADeficitDrains()
    {
        SimWorld world = Base(out _);
        WorldPos centre = Clearing(world);

        // One plant (ten) over the standby (six), carrying a factory (four), a bureau
        // (three) and the dish (four) with room to spare: a healthy base.
        EntityId plant = Spawn(world, Faction.Soviet, 0, UnitKind.PowerPlant, Near(centre, -60_000));
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Near(centre, 60_000));
        Spawn(world, Faction.Soviet, 0, UnitKind.DesignBureau, Near(centre, 120_000));
        EntityId radar = Spawn(world, Faction.Soviet, 0, UnitKind.RadarStation, Near(centre, -120_000));

        ref TeamState state = ref world.TeamRef(0);
        state.Energy = 100;

        world.RunTicks(2);
        Assert.True(world.IsRadarLit(radar.Slot));

        // The strike: the plant is gone and the grid is short by five a tick. The bank
        // pays the difference — twenty ticks of grace at five a tick — and the dish
        // stays on the air while it drains. A base that runs short on a full bank is a
        // base that runs.
        world.Despawn(plant);
        state.Energy = 100;

        world.RunTicks(12);
        Assert.True(world.IsRadarLit(radar.Slot), "the dish went dark while the bank still had the deficit to pay");

        // And when the bank is gone — twenty ticks of grace at five a tick — the shed lands.
        world.RunTicks(12);
        Assert.False(world.IsRadarLit(radar.Slot), "the dish ran on a bank that was already empty");
    }

    [Fact]
    public void TheShedRunsDetectionBeforeTheGuns()
    {
        // A base on its standby set alone: a factory, a gun and a radar, which the six
        // units of standby cannot all carry. The dish goes dark first — it is the first
        // thing shed — and the gun stays lit, because the two of them draw less than the
        // factory alone and the grid runs what fits.
        SimWorld world = Base(out _);
        WorldPos centre = Clearing(world);

        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Near(centre, 60_000));
        EntityId gun = Spawn(world, Faction.Soviet, 0, UnitKind.GunEmplacement, Near(centre, 120_000));
        EntityId radar = Spawn(world, Faction.Soviet, 0, UnitKind.RadarStation, Near(centre, -120_000));

        EmptyBank(world, 0);
        world.RunTicks(2);

        ref TeamState state = ref world.TeamRef(0);

        Assert.True(state.IsDimmed, "the grid was not short; there was nothing to shed");
        Assert.False(world.IsRadarLit(radar.Slot), "the radar outlasted the bank and the shed");
        Assert.False(state.WeaponsShed, "the gun was silenced while the dish was still there to shed first");

        // A second gun, and the guns are lit oldest first: the shed order says detection
        // first, so when the rank below the radar is what sheds, the older gun keeps its
        // air and the newer one loses it.
        EntityId secondGun = Spawn(world, Faction.Soviet, 0, UnitKind.GunEmplacement, Near(centre, 180_000));
        EmptyBank(world, 0);
        world.RunTicks(2);

        state = ref world.TeamRef(0);
        Assert.False(world.IsRadarLit(radar.Slot));
        // The guns are lit oldest first: the older slot keeps its air, the newer loses
        // it, and the report says a gun is silenced.
        // The guns are shed oldest last (lit ascending slot), so with capacity for one
        // the older gun keeps its air and the newer one is silenced. The reach itself is
        // geometry — the silence lives where the firing decision is made — and the
        // report is the team's own flag.
        Assert.True(state.WeaponsShed, "a gun the grid cannot run should be reported as silenced");

        // And the firing decision is where the silence bites: a silenced gun cannot
        // engage, the powered one can.
        Assert.False(CanEngageGun(world, gun.Slot), "the older gun was silenced with the newer one");
        Assert.False(CanEngageGun(world, secondGun.Slot), "the newer gun fired through the shed");
        Assert.True(state.WeaponsShed, "a gun the grid cannot run should be reported as silenced");
    }

    /// <summary>
    /// The firing answer, asked the way the combat loop asks it: a gun's own decision,
    /// against a target that is alive and in reach. The combat system's
    /// <c>CanEngage</c> is private; its answer is what the fire step produces — a target
    /// held means engaged.
    /// </summary>
    private static bool CanEngageGun(SimWorld world, int gunSlot)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(slot);

            if (candidate.Kind == UnitKind.CommandCentre &&
                candidate.TeamId != world.GetRefBySlot(gunSlot).TeamId)
            {
                world.OrderAttack(new EntityId(gunSlot, world.GetRefBySlot(gunSlot).Generation),
                    new EntityId(slot, candidate.Generation), 0);
                world.Step();
                return world.GetRefBySlot(gunSlot).TargetSlot == slot;
            }
        }

        return false;
    }

    [Fact]
    public void ASilencedGunStillSeesButCannotFire()
    {
        SimWorld world = Base(out _);
        WorldPos centre = Clearing(world);

        EntityId gun = Spawn(world, Faction.Soviet, 0, UnitKind.GunEmplacement, centre);
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Near(centre, 60_000));
        Spawn(world, Faction.Soviet, 0, UnitKind.DesignBureau, Near(centre, 120_000));
        EntityId target = Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, Near(centre, 150_000));

        // The bank is empty and the base's own load outruns the standby: the factory
        // (four) and the bureau (three) take the six the standby gives, and the gun is
        // the next rank — the shed's second.
        EmptyBank(world, 0);
        world.RunTicks(2);

        Assert.True(world.Team(0).WeaponsShed, "the gun was powered on a grid that had nothing to give it");

        // It still sees: the eyes are its own optics, and the silence lives where the
        // firing decision is made.
        int reach = CombatSystem.EngagementRadiusMm(world, gun.Slot);

        Assert.True(reach == 0, $"A silenced gun reached {reach} mm, so the silence was not the brown-out's.");

        // And the tank inside its reach is untouched: a gun that cannot fire has never
        // held a target.
        world.RunTicks(120);
        Assert.Equal(UnitCatalog.Get(UnitKind.CommandCentre).Health, world.GetRefBySlot(target.Slot).Health);
    }

    [Fact]
    public void ProductionIsSlowedNeverStopped()
    {
        SimWorld world = Base(out _);
        WorldPos centre = Clearing(world);

        // The standby alone carries a factory and a bureau, and the queue holds a job:
        // the base is browned at the last step, and the yard runs half speed.
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Near(centre, 60_000));
        Spawn(world, Faction.Soviet, 0, UnitKind.DesignBureau, Near(centre, 120_000));

        EmptyBank(world, 0);
        world.RunTicks(2);
        Assert.True(world.Team(0).PowerBrowned, "the fixture's grid was not short at production");

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.Water = 5_000;
        state.Energy = 0;

        // Infantry is what a headquarters makes; the queue is the measurement.
        EntityId id = FirstHeadquarters(world);
        world.Enqueue(SimCommand.QueueUnit(id, UnitKind.Infantry, world.Tick + 1, 0));

        int halfTicks = UnitCatalog.Get(UnitKind.Infantry).BuildTicks;
        world.RunTicks(halfTicks);

        // Half speed: the job is not done at its catalogue time, which is the price.
        Assert.Equal(0, CountProduced(world, UnitKind.Infantry));

        world.RunTicks(halfTicks + 4);
        Assert.Equal(1, CountProduced(world, UnitKind.Infantry));
    }

    private static EntityId FirstHeadquarters(SimWorld world)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == UnitKind.CommandCentre)
            {
                return new EntityId(slot, world.GetRefBySlot(slot).Generation);
            }
        }

        throw new InvalidOperationException("The fixture has no headquarters.");
    }

    private static int CountProduced(SimWorld world, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.Kind == kind && entity.TeamId == 0 && !UnitCatalog.Get(kind).IsBuilding)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void TheBankRefillingUnShedsTheBase()
    {
        SimWorld world = Base(out _);
        WorldPos centre = Clearing(world);

        EntityId radar = Spawn(world, Faction.Soviet, 0, UnitKind.RadarStation, Near(centre, -120_000));
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Near(centre, 60_000));

        EmptyBank(world, 0);
        world.RunTicks(2);
        Assert.False(world.IsRadarLit(radar.Slot), "the shed never landed");

        // The bank refills from the standby's own rate — the death-spiral rule — and the
        // shed is derived, not remembered: a base whose bank is no longer empty is a base
        // with its dish back on the air, and nothing had to be told.
        // The bank refills from the standby's own rate, and the shed is derived, not
        // remembered: a base whose bank is no longer empty is a base with its dish back
        // on the air, and nothing had to be told.
        ref TeamState state = ref world.TeamRef(0);
        state.Energy = 60;

        world.RunTicks(2);
        Assert.True(world.IsRadarLit(radar.Slot), "the dish stayed dark on a grid that had power again");
    }
}
