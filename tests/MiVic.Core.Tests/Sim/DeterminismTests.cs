using MiVic.Core.Numerics;
using MiVic.Core.Random;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The determinism contract: identical seed plus identical command log must
/// produce an identical state hash, every run, on every machine. These tests are
/// the reason the simulation avoids floating point and dictionaries.
/// </summary>
public sealed class DeterminismTests
{
    private const int UnitCount = 60;
    private const int OrdersPerUnit = 5;
    private const int ScenarioTicks = 500;
    private const int TicksPerOrderRound = ScenarioTicks / OrdersPerUnit;

    /// <summary>Mutates an entity in place; <see cref="Action{T}"/> cannot carry a <c>ref</c> parameter.</summary>
    private delegate void EntityMutator(ref Entity entity);

    /// <summary>
    /// Builds a fixed, reproducible scenario: three factions with mixed speeds.
    /// Spawning consumes no randomness, so the world RNG stays untouched and the
    /// command log is the only input.
    /// </summary>
    private static SimWorld BuildScenario(ulong seed)
    {
        SimWorld world = new(seed, capacity: 256);

        for (int i = 0; i < UnitCount; i++)
        {
            Faction faction = FactionProfile.All[i % 3].Faction;
            int team = i % 3;
            WorldPos start = WorldPos.GroundMetres((i % 10) * 20, (i / 10) * 20);
            int speed = 400 + ((i * 7) % 300);

            world.Spawn(faction, team, UnitKind.Tank, start, Fix32.FromInt(speed), 100 + i);
        }

        // Each team gets an economy, so the golden hash actually depends on income,
        // costs and build times. Without buildings the scenario spawns units
        // directly and the whole production system is invisible to this test — the
        // production-ordering change did not move the hash at all.
        for (int team = 0; team < FactionProfile.All.Length; team++)
        {
            Faction faction = FactionProfile.All[team].Faction;
            int x = -200 + (team * 150);

            world.Spawn(faction, team, UnitKind.CommandCentre, WorldPos.GroundMetres(x, -200), Fix32.Zero, 5_000);
            world.Spawn(faction, team, UnitKind.PowerPlant, WorldPos.GroundMetres(x, -160), Fix32.Zero, 1_200);
        }

        return world;
    }

    /// <summary>
    /// Produces a deterministic command log. Order generation uses its own
    /// generator so that replaying the log does not perturb the world's RNG.
    /// </summary>
    private static List<SimCommand> GenerateOrders(ulong seed)
    {
        List<SimCommand> commands = [];
        Pcg32 orderRng = new(seed);
        long tick = 0;

        for (int round = 0; round < OrdersPerUnit; round++)
        {
            for (int slot = 0; slot < UnitCount; slot++)
            {
                int x = orderRng.NextInt(0, 400);
                int z = orderRng.NextInt(0, 400);

                commands.Add(SimCommand.Move(
                    new EntityId(slot, 0),
                    WorldPos.GroundMetres(x, z),
                    executeTick: tick + 1,
                    issuerTeam: slot % 3));
            }

            tick += TicksPerOrderRound;
        }

        return commands;
    }

    private static SimWorld RunScenario(ulong seed, IReadOnlyList<SimCommand> commands)
    {
        SimWorld world = BuildScenario(seed);

        foreach (SimCommand command in commands)
        {
            world.Enqueue(command);
        }

        world.RunTicks(ScenarioTicks);
        return world;
    }

    private static ulong HashScenario(ulong seed) => StateHash.Compute(RunScenario(seed, GenerateOrders(seed)));

    [Fact]
    public void SameSeed_ProducesIdenticalHashes()
        => Assert.Equal(HashScenario(20250101), HashScenario(20250101));

    [Fact]
    public void DifferentSeed_ProducesDifferentHashes()
        => Assert.NotEqual(HashScenario(1), HashScenario(2));

    [Fact]
    public void ReplayOfTheSameCommandLog_ReproducesTheState()
    {
        // Capture a command log once, then replay exactly that log on a fresh
        // world. This is the same guarantee a saved replay or a lockstep peer
        // relies on.
        List<SimCommand> log = GenerateOrders(999);

        ulong original = StateHash.Compute(RunScenario(999, log));
        ulong replayed = StateHash.Compute(RunScenario(999, log));

        Assert.Equal(original, replayed);
    }

    [Fact]
    public void ReplayOnAWorldBuiltLater_MatchesTheOriginal()
    {
        // The world RNG state is part of the hash, so a replay must be built
        // with the same seed; the command log alone is not enough.
        List<SimCommand> log = GenerateOrders(999);

        ulong first = StateHash.Compute(RunScenario(999, log));
        GC.Collect();
        ulong second = StateHash.Compute(RunScenario(999, log));

        Assert.Equal(first, second);
    }

    [Fact]
    public void UnitPositions_AreIdenticalAcrossRuns()
    {
        List<SimCommand> log = GenerateOrders(555);

        SimWorld a = RunScenario(555, log);
        SimWorld b = RunScenario(555, log);

        for (int slot = 0; slot < a.Capacity; slot++)
        {
            if (!a.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity ea = ref a.GetRefBySlot(slot);
            ref Entity eb = ref b.GetRefBySlot(slot);

            Assert.Equal(ea.Position, eb.Position);
            Assert.Equal(ea.Heading, eb.Heading);
            Assert.Equal(ea.HasMoveGoal, eb.HasMoveGoal);
        }
    }

    [Fact]
    public void ReorderingTheQueue_DoesNotChangeTheResult()
    {
        // Commands carry an explicit execute tick, so the order they sit in the
        // queue must not matter. That is what makes lockstep robust to packets
        // arriving in a different order.
        List<SimCommand> log = GenerateOrders(42);
        List<SimCommand> shuffled = [.. log];
        (shuffled[0], shuffled[7]) = (shuffled[7], shuffled[0]);

        Assert.Equal(StateHash.Compute(RunScenario(42, log)), StateHash.Compute(RunScenario(42, shuffled)));
    }

    [Fact]
    public void ChangingADestination_ChangesTheState()
    {
        // A focused check: two different destinations must produce different
        // states after a single tick, before any unit can give up on a route.
        static ulong HashAfterOneTick(WorldPos destination)
        {
            SimWorld world = new(seed: 42, capacity: 4);
            WorldPos start = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
            EntityId unit = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, start, Fix32.FromInt(400), 100);

            world.OrderMove(unit, destination, 0);
            world.Step();

            return StateHash.Compute(world);
        }

        Assert.NotEqual(
            HashAfterOneTick(WorldPos.GroundMetres(0, 0)),
            HashAfterOneTick(WorldPos.GroundMetres(80, 80)));
    }

    [Fact]
    public void StateHash_ChangesForEverySimulatedField()
    {
        static ulong HashWith(EntityMutator mutate)
        {
            SimWorld world = new(seed: 42, capacity: 4);
            world.Spawn(Faction.Western, 2, UnitKind.Tank, WorldPos.FromMetres(1, 0, 1), Fix32.FromInt(5), 100);
            mutate(ref world.GetRefBySlot(0));
            return StateHash.Compute(world);
        }

        ulong baseline = HashWith(static (ref Entity _) => { });

        ulong[] variants =
        [
            HashWith(static (ref Entity e) => e.Position = WorldPos.FromMetres(2, 0, 1)),
            HashWith(static (ref Entity e) => e.MoveGoal = WorldPos.FromMetres(9, 0, 9)),
            HashWith(static (ref Entity e) => e.HasMoveGoal = true),
            HashWith(static (ref Entity e) => e.SpeedMmPerTick = Fix32.FromInt(6)),
            HashWith(static (ref Entity e) => e.Morale = Fix32.FromDouble(0.25)),
            HashWith(static (ref Entity e) => e.Health = 101),
            HashWith(static (ref Entity e) => e.Heading = 12345),
            HashWith(static (ref Entity e) => e.TeamId = 1),
            HashWith(static (ref Entity e) => e.Faction = Faction.Soviet),
            HashWith(static (ref Entity e) => e.Kind = UnitKind.Infantry),
        ];

        Assert.All(variants, v => Assert.NotEqual(baseline, v));
    }

    [Fact]
    public void StateHash_IsOrderSensitive()
    {
        SimWorld a = new(seed: 7, capacity: 4);
        SimWorld b = new(seed: 7, capacity: 4);

        a.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.FromMetres(1, 0, 0), Fix32.One, 10);
        a.Spawn(Faction.Chinese, 1, UnitKind.Infantry, WorldPos.FromMetres(2, 0, 0), Fix32.One, 10);

        b.Spawn(Faction.Chinese, 1, UnitKind.Infantry, WorldPos.FromMetres(2, 0, 0), Fix32.One, 10);
        b.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.FromMetres(1, 0, 0), Fix32.One, 10);

        Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));
    }

    [Fact]
    public void TickCount_IsPartOfTheHash()
    {
        SimWorld world = new(seed: 3, capacity: 2);
        world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 10);

        ulong before = StateHash.Compute(world);
        world.RunTicks(1);

        Assert.NotEqual(before, StateHash.Compute(world));
    }

    [Fact]
    public void EmptyWorld_HashIsStable()
        => Assert.Equal(StateHash.Compute(new SimWorld(1, 8)), StateHash.Compute(new SimWorld(1, 8)));

    /// <summary>
    /// Golden hash of the fixed scenario. Regenerate only on a deliberate balance or
    /// system change.
    /// <para>
    /// Last changed by woodland: forests are a new surface, and they change both where
    /// units can go and how much damage they take when they get there.
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenScenarioHash_IsStable()
        => Assert.Equal(901504124632583463UL, HashScenario(20250101));
}
