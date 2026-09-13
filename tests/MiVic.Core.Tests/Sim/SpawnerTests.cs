using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using MiVic.Core.Sim.Systems;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The monster generator: a structure on a team of its own that emits hostiles on a cadence.
/// Three things are under test, and they are not the same thing: that the cadence is exact
/// (ticks are what the clock is, so "every five seconds" is not an approximation), that the
/// two bounds a zone answers to are the ones §9 said — the zone's own count bites, and no
/// queue-side cap does — and that the invulnerability is honoured on both halves of the
/// question, targeting and damage, because a wall nothing happens to is a target nobody
/// should ever pick.
/// </summary>
public sealed class SpawnerTests
{
    /// <summary>Five seconds of the standard clock, in ticks.</summary>
    private const int Interval = 100;

    private static SimWorld Zone(int count, UnitKind output = UnitKind.Warden, int interval = Interval)
    {
        var world = new SimWorld(seed: 20250101, capacity: 64);
        WorldPos site = Clearing(world);

        EntityId factory = Spawn(world, Faction.Chinese, 3, UnitKind.DerelictFactory, site);
        world.Spawner.Configure(factory.Slot, output, interval, count);

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
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            health > 0 ? health : UnitCatalog.Get(kind).Health);

    private static WorldPos Clearing(SimWorld world)
        => world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    private static EntityId Factory(SimWorld world)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == UnitKind.DerelictFactory)
            {
                return new EntityId(slot, world.GetRefBySlot(slot).Generation);
            }
        }

        throw new InvalidOperationException("The fixture has no factory.");
    }

    private static int AliveOf(SimWorld world, UnitKind kind)
    {
        int alive = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == kind)
            {
                alive++;
            }
        }

        return alive;
    }

    [Fact]
    public void TheCadenceIsExactOnTheTick()
    {
        SimWorld world = Zone(count: 0);

        // Nothing is scheduled before the first tick has run: the clock starts from
        // now rather than from placement.
        ref Entity generator = ref world.GetRefBySlot(Factory(world).Slot);
        Assert.Equal(0, generator.SpawnedCount);
        Assert.Equal(0, generator.NextSpawnTick);

        world.Step();
        Assert.Equal(Interval + 1, generator.NextSpawnTick);

        // The warden lands on the tick the cadence names, not a frame either side.
        world.RunTicks(Interval);
        Assert.Equal(1, AliveOf(world, UnitKind.Warden));
        Assert.Equal(1, generator.SpawnedCount);
        Assert.Equal(2 * Interval + 1, generator.NextSpawnTick);
    }

    [Fact]
    public void ACountStopsTheZoneAndTheClockWithIt()
    {
        SimWorld world = Zone(count: 3);
        ref Entity generator = ref world.GetRefBySlot(Factory(world).Slot);

        // Three emissions, and no more: the zone has given everything it had.
        world.RunTicks(3 * Interval + 2);
        Assert.Equal(3, generator.SpawnedCount);

        long clockAtTheEnd = generator.NextSpawnTick;
        world.RunTicks(5 * Interval);
        Assert.Equal(3, generator.SpawnedCount);
        Assert.Equal(3, AliveOf(world, UnitKind.Warden));

        // The clock stopped when the count did, so nothing here fires every tick
        // looking for a ward it will never make.
        Assert.Equal(clockAtTheEnd, generator.NextSpawnTick);
    }

    [Fact]
    public void ZeroMeansUnlimited()
    {
        // The sentinel MaxAlive already uses, which is why it costs nothing and wants
        // no explaining: a zone with a count of zero breeds for as long as the match runs.
        SimWorld world = Zone(count: 0);

        world.RunTicks(10 * Interval + 1);

        ref Entity generator = ref world.GetRefBySlot(Factory(world).Slot);
        Assert.True(generator.SpawnedCount > 5, $"Only {generator.SpawnedCount} wardens in {5 * Interval + 2} ticks.");
    }

    [Fact]
    public void TheNeutralTeamIsSubjectToNoCap()
    {
        // The queue path enforces MaxAlive; the spawner does not go through the queue,
        // and the neutral team that owns the zone owns no ceiling either — which is how
        // a zone ends up breeding. An ordinary team's Harvester is capped at nothing
        // special, but the emitted role here is capped for the *players*: the reading is
        // of the team the count belongs to, and the zone's team is not anybody's.
        SimWorld world = Zone(count: 0, output: UnitKind.Warden);

        world.RunTicks(12 * Interval + 2);

        ref Entity generator = ref world.GetRefBySlot(Factory(world).Slot);
        Assert.True(generator.SpawnedCount >= 12, $"The zone stopped at {generator.SpawnedCount}, as if it were capped.");
    }

    [Fact]
    public void NobodyTargetsWhatNothingCanHurt()
    {
        // The targeting half of "cannot be killed": a tank inside the factory's zone,
        // acquisition free to choose, and the only building in reach is one no weapon
        // has any business pointing at. A CanEngage that ignored the attribute would
        // hold the factory as a target forever and fire nothing, which is worse than
        // ignoring it.
        SimWorld world = Zone(count: 6);
        EntityId tank = Spawn(world, Faction.Soviet, 0, UnitKind.Tank,
            Clearing(world) + new WorldPos(60_000, 0, 0));

        world.RunTicks(300);

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            Assert.True(entity.Kind != UnitKind.DerelictFactory || entity.Health == UnitCatalog.Get(UnitKind.DerelictFactory).Health,
                "Something damaged the factory, which nothing can hurt.");
        }

        // And nobody aimed at it: the wardens' own acquisition reads the same clause.
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot) || world.GetRefBySlot(slot).Kind != UnitKind.Warden)
            {
                continue;
            }

            int target = world.GetRefBySlot(slot).TargetSlot;

            if (target < 0)
            {
                continue;
            }

            Assert.NotEqual(UnitKind.DerelictFactory, world.GetRefBySlot(target).Kind);
        }
    }

    [Fact]
    public void TheZoneRemembersWhatItHasGiven()
    {
        // The count and the clock are simulation state, so the hash says so — and a
        // match with a zone that has emitted hashes differently from the same match
        // before the cadence moved, which is the whole of the desync guarantee. (The
        // rebuild half — a replay reproducing a fixture-placed zone — is a limit the
        // fixtures share with every scenario-laid piece of state: a replay rebuilds
        // the scenario's own layout, and a fixture's direct placement is not part of
        // it. The reconstructible route for campaign use is the trigger action, which
        // re-derives placement from the script.)
        SimWorld world = Zone(count: 6);
        world.RunTicks(2);

        ref Entity generator = ref world.GetRefBySlot(Factory(world).Slot);
        ulong before = StateHash.Compute(world);
        Assert.Equal(0, generator.SpawnedCount);

        world.RunTicks(Interval);
        Assert.Equal(1, generator.SpawnedCount);

        Assert.NotEqual(before, StateHash.Compute(world));
    }

    [Fact]
    public void TheZoneBelongsToNobodyAndTheVictoryRuleLetsItBe()
    {
        // The neutral team: an undeclared team is hostile to every side (nobody declares
        // it, so nobody is its ally) and invisible to the victory rule (the check walks
        // the sides the match declares), and the match plays on however many wardens the
        // zone has given.
        SimWorld world = Zone(count: 6);

        // The two sides the match declares, each holding ground so the verdict has
        // something to measure — a side is defeated by losing its structures, so the
        // fixture gives each one a factory: the zone is a third thing on the map, and
        // the outcome must not care.
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, Clearing(world) + new WorldPos(-200_000, 0, 0));
        Spawn(world, Faction.Western, 2, UnitKind.Factory, Clearing(world) + new WorldPos(200_000, 0, 0));

        Assert.True(world.IsHostile(0, 3));
        Assert.True(world.IsHostile(2, 3));

        world.RunTicks(6 * Interval + 2);
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
    }
}
