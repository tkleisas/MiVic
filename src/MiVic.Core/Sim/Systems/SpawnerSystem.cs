using MiVic.Core.Numerics;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim.Systems;

/// <summary>
/// The monster generator: a structure on a team of its own that emits hostiles on a cadence,
/// belongs to nobody, and is at war with everybody. ROADMAP §9.
/// <para>
/// A generator makes a patch of the map a place rather than a space: neutral ground gets a
/// reason to matter, and a match gets a pressure neither player controls. The fiction needs no
/// new furniture — the setting already has automated industry, exclusion zones and things that
/// were done in remote places — so the whole mechanic is one spawner with three parameters and
/// a kind: <b>output</b> (what it emits), <b>interval</b> (how often, in ticks, because a tick
/// is what the clock is), and <b>count</b> (how many it will ever emit, with zero meaning
/// unlimited, the sentinel <c>MaxAlive</c> already uses). One output kind per generator, so a
/// zone that emits two things at two cadences is two generators at the same place — simpler
/// than a weighted table, and it composes the same way.
/// </para>
/// <para>
/// <b>The state is the interesting kind.</b> How many a generator has emitted and when it will
/// next emit are per-instance simulation state, and they are hashed — conditionally on the role,
/// which is deterministic because the role itself is hashed first, and because a match with no
/// generator must mix not one byte more than it did. A spawner whose count was not hashed is a
/// spawner two machines can disagree about while every other number agrees.
/// </para>
/// <para>
/// <b>No escalation, for now.</b> Constant cadence, and deliberately so: escalation is cheap to
/// add later if it is written as a function of the tick rather than as accumulated state — a
/// cadence derived from elapsed time needs nothing new hashed — while a spawn count that grows
/// needs hashing like any other state. The door is left open in the shape of the rule, not with
/// a field.
/// </para>
/// </summary>
public sealed class SpawnerSystem
{
    private readonly SpawnerConfig[] _config;

    /// <summary>Creates the per-slot configuration table for a lattice of slots.</summary>
    public SpawnerSystem() => _config = new SpawnerConfig[SimConstants.MaxEntities];

    /// <summary>
    /// Sets what the generator in a slot emits. Called once, by whoever placed it — the
    /// same fixture, mission or script that put the structure on the map — so a rebuild
    /// re-derives it from the same place the placement came from.
    /// </summary>
    public void Configure(int slot, UnitKind output, int intervalTicks, int count)
    {
        if ((uint)slot >= (uint)_config.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "No such slot.");
        }

        if (intervalTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalTicks), intervalTicks, "Ticks are not negative.");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Counts are not negative.");
        }

        // The output may be a role nobody builds — the zone is where wardens come
        // from, which is the whole of what NeverBuilt closes: the queue door, not
        // this one. What is refused is a role with no definition at all.
        if (output != UnitKind.None && !UnitCatalog.TryGet(output, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "No such role.");
        }

        _config[slot] = new SpawnerConfig(output, intervalTicks, count);
    }

    /// <summary>What the generator in a slot is set to emit, as configured or as nothing.</summary>
    public SpawnerConfig ConfigOf(int slot)
        => (uint)slot < (uint)_config.Length ? _config[slot] : default;

    /// <summary>
    /// Emits on every generator whose cadence has arrived. Called once per tick from
    /// <see cref="SimWorld.Step"/>, after production and before pathing, so a warden that
    /// arrives this tick asks for its route no earlier than anything else does.
    /// </summary>
    public void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.Kind != UnitKind.DerelictFactory)
            {
                continue;
            }

            // A structure that is still rising emits nothing, for the same reason a
            // half-built factory makes nothing: what the picture shows is not yet true.
            if (entity.ConstructionTicksRemaining > 0)
            {
                continue;
            }

            if (entity.NextSpawnTick == 0)
            {
                // First schedule. The cadence is measured from now, not from placement —
                // a zone the mission reveals late does not owe the wardens it could have
                // made while nobody was looking at it.
                entity.NextSpawnTick = world.Tick + SpawnIntervalTicks(world, slot);
                continue;
            }

            if (world.Tick < entity.NextSpawnTick)
            {
                continue;
            }

            SpawnOutput(world, slot, ref entity);
        }
    }

    /// <summary>
    /// Emits one unit and schedules the next. The bounds it answers to, in the order a reader
    /// should expect them to bite: the generator's own count, which is a fact about the zone;
    /// then the emitted role's <c>MaxAlive</c> per team, which a generator's team is subject
    /// to like anybody; and never the capacity ceiling, because a ceiling is a sum over the
    /// structures a side owns and the neutral team that owns this one owns nothing else —
    /// which is how a zone ends up breeding. A generator that emits a capped role stops
    /// quietly once the team is at the cap and starts again the tick a warden dies, which is
    /// correct and worth saying where it happens rather than letting a reader discover it.
    /// </summary>
    /// <summary>
    /// Emits one unit and schedules the next. The bounds a reader should expect to bite, and
    /// which does not: the zone's own <b>count</b> is the one that bites, because it is a fact
    /// about the zone's history; the emitted role's <c>MaxAlive</c> does not, because that cap
    /// is enforced on the queue path — <c>CanBuild</c> — and the spawner does not go through
    /// the queue. <b>A generator on the neutral team is therefore subject to no cap at all</b>,
    /// which is how a zone ends up breeding, and it is said here rather than discovered.
    /// </summary>
    private void SpawnOutput(SimWorld world, int slot, ref Entity generator)
    {
        SpawnerConfig config = ConfigOf(slot);

        if (config.Output == UnitKind.None)
        {
            // A generator with no output is a ruin: it stands, it lights the map, it emits
            // nothing. Valid as a fixture, useless as a rule — and the validation that keeps
            // a mission honest refuses it before a match ever loads it.
            return;
        }

        if (config.Count > 0 && generator.SpawnedCount >= config.Count)
        {
            // Spent, and the clock stops with it: a count is how many it will *ever*
            // emit, and a zone that has given everything it had is a place with a
            // history. Nothing is scheduled again, so this branch runs once.
            return;
        }

        UnitDefinition output = UnitCatalog.Get(config.Output);
        WorldPos site = world.LegalSpawnSite(generator.Position);

        if (world.TerrainTypes.IndexOfWorld(site.X, site.Z) >= 0)
        {
            world.Spawn(generator.Faction, generator.TeamId, config.Output, site,
                Fix32.FromInt(output.SpeedMmPerTick), output.Health);
            generator.SpawnedCount++;
        }

        // The next emission is scheduled whether or not this one got out: the clock
        // ticks rather than re-firing every tick, so the day the site is free again
        // the cadence is already where it should be.
        generator.NextSpawnTick = world.Tick + SpawnIntervalTicks(world, slot);
    }

    /// <summary>The instance's interval, with the zero case answered rather than divided by.</summary>
    private int SpawnIntervalTicks(SimWorld world, int slot)
    {
        int interval = ConfigOf(slot).IntervalTicks;

        return interval > 0 ? interval : SimConstants.SecondsToTicks(5);
    }
}

/// <summary>The three parameters and a kind, as the zone was authored.</summary>
/// <param name="Output">What it emits. <see cref="UnitKind.None"/> means the zone emits nothing.</param>
/// <param name="IntervalTicks">How often it emits, in ticks of the simulation's own clock.</param>
/// <param name="Count">
/// How many it will ever emit. Zero means unlimited — the sentinel <c>MaxAlive</c> already
/// uses, which is why the sentinel costs nothing and wants no explaining.
/// </param>
public readonly record struct SpawnerConfig(UnitKind Output, int IntervalTicks, int Count);
