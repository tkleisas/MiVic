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

        // Each team gets an economy, so the golden hash covers income, costs, build times and the
        // production queues as well as movement. Without buildings the scenario spawns units
        // directly and the whole production system is invisible to this test — the
        // production-ordering change did not move the hash at all.
        //
        // What that economy actually turns out is units, not structures, and this comment used to
        // claim otherwise. Measured over the five hundred ticks below: the building count is this
        // scenario's own six on every single tick, because no team's materials ever clear a factory
        // plus the reserve the AI keeps, while the two AI teams do produce infantry — 18 and 15 of
        // them, once the armour they start with has been shot away. So the queue half of the hash is
        // exercised here and the structure half is not. The AI's structure path is covered where it
        // can be watched instead: StructurePlacementTests gives a bare base an economy and then
        // checks every structure the AI raises against the placement rule
        // (TheAiStillRaisesStructuresOnItsOffsetPath, EveryStructureTheAiRaisesStandsSomewhereTheRuleAllows).
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

    /// <summary>
    /// The production queues are state, so they are hashed: two worlds that agree about everything
    /// else must disagree the moment one of them is building something the other is not.
    /// <para>
    /// Every other input to the hash is checked to be identical first, and that check is the test:
    /// a hash that moved for some other reason would prove nothing about the queues. What is left
    /// different between the two worlds when the assertion is made is a queue length, a job's role,
    /// and how far along that job is — the four things a queue is, one at a time.
    /// </para>
    /// </summary>
    [Fact]
    public void ProductionQueuesArePartOfTheStateHash()
    {
        static SimWorld Build(out int factory)
        {
            SimWorld world = new(seed: 4242, capacity: 8);

            factory = world.Spawn(
                Faction.Soviet, 0, UnitKind.Factory, WorldPos.GroundMetres(10, 10), Fix32.Zero, 2_000).Slot;

            return world;
        }

        SimWorld a = Build(out int slotA);
        SimWorld b = Build(out int slotB);

        Assert.Equal(StateHash.Compute(a), StateHash.Compute(b));

        // One job at the factory in `a`, and nothing at all in `b`. The queue length and the job are
        // what differ; the building itself is at the same place with the same hit points, which the
        // checks below pin down rather than assume.
        Assert.True(a.AddJob(slotA, UnitKind.Tank, totalTicks: 120));
        Assert.Equal(1, a.GetRefBySlot(slotA).QueueLength);
        Assert.Equal(0, b.GetRefBySlot(slotB).QueueLength);

        Assert.Equal(UnitKind.Tank, a.JobsOf(slotA)[0].Kind);
        Assert.Equal(120, a.JobsOf(slotA)[0].RemainingTicks);
        Assert.Empty(b.JobsOf(slotB).ToArray());

        // Nothing else moved: not the clock, not the RNG, not the resources, not the ground, and not
        // the building the job is queued at.
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.Rng.State, b.Rng.State);
        Assert.Equal(a.AliveCount, b.AliveCount);
        Assert.Equal(a.PendingCommandCount, b.PendingCommandCount);
        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Team(0).Materials, b.Team(0).Materials);
        Assert.Equal(a.Team(0).Energy, b.Team(0).Energy);
        Assert.Equal(a.Team(0).Water, b.Team(0).Water);
        Assert.Equal(a.Team(0).TechTier, b.Team(0).TechTier);
        Assert.Equal(a.GetRefBySlot(slotA).Position, b.GetRefBySlot(slotB).Position);
        Assert.Equal(a.GetRefBySlot(slotA).Kind, b.GetRefBySlot(slotB).Kind);
        Assert.Equal(a.GetRefBySlot(slotA).Health, b.GetRefBySlot(slotB).Health);
        Assert.Equal(a.GetRefBySlot(slotA).ConstructionTicksRemaining, b.GetRefBySlot(slotB).ConstructionTicksRemaining);
        Assert.True(a.TerrainTypes.RawTypes.SequenceEqual(b.TerrainTypes.RawTypes), "the surfaces differ, so this proves nothing");
        Assert.True(a.TerrainTypes.RawChurn.SequenceEqual(b.TerrainTypes.RawChurn), "the wear differs, so this proves nothing");
        Assert.True(a.TerrainTypes.RawAttributes.SequenceEqual(b.TerrainTypes.RawAttributes), "the attributes differ, so this proves nothing");

        Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));

        // And a queue is not merely how long it is. Two worlds with one job each, differing only in
        // which role is on the pad, are different states; so are two that differ only in how far
        // along the same job is.
        static SimWorld WithJob(UnitKind kind, int remainingTicks)
        {
            SimWorld world = Build(out int factory);
            world.AddJob(factory, kind, totalTicks: 120);
            world.JobRef(factory, 0).RemainingTicks = remainingTicks;
            return world;
        }

        ulong tank = StateHash.Compute(WithJob(UnitKind.Tank, 120));
        ulong artillery = StateHash.Compute(WithJob(UnitKind.Artillery, 120));
        ulong halfBuilt = StateHash.Compute(WithJob(UnitKind.Tank, 60));

        Assert.NotEqual(tank, artillery);
        Assert.NotEqual(tank, halfBuilt);

        // And a second job in the queue is another state again: how many are waiting is part of it.
        SimWorld two = Build(out int queued);
        two.AddJob(queued, UnitKind.Tank, totalTicks: 120);
        two.AddJob(queued, UnitKind.Tank, totalTicks: 120);

        Assert.NotEqual(tank, StateHash.Compute(two));
    }

    [Fact]
    public void EmptyWorld_HashIsStable()
        => Assert.Equal(StateHash.Compute(new SimWorld(1, 8)), StateHash.Compute(new SimWorld(1, 8)));

    /// <summary>
    /// Golden hash of the fixed scenario. Regenerate only on a deliberate balance or
    /// system change.
    /// <para>
    /// Last changed by the production queues becoming state: which role a building is making, how
    /// long the job was always going to take, how far along it is and how many are waiting are
    /// folded in for every live slot — whether or not it is building anything, because an empty
    /// queue is the number zero rather than an absence, the same rule the crossings carry. This
    /// scenario's queues do move: the AI fills them from its starting command centres, so this is
    /// not merely a shifted stream. What the scenario does not exercise is the AI raising a
    /// <em>structure</em> — see <see cref="BuildScenario"/>, where the measured counts are written
    /// down — so that path is covered by StructurePlacementTests instead.
    /// Before that it was the crossings a team builds becoming state: a bridge is engineering work
    /// now — the span is recorded when it is ordered, its deck goes up a cell at a time, and the
    /// ford appears as the work reaches it — so the spans, their start ticks and how much of each
    /// is up are folded into the hash. Nothing in this scenario builds one, which is precisely
    /// why the entry is mixed unconditionally: a field that is only hashed when it happens to be
    /// non-empty is a field a desync can hide in. Before that it was cover becoming a function of
    /// the ground — the surface as a base, the canopy density on it, then the shape of the ground
    /// — rather than a lookup on the surface byte. Both sides of this battle now take different
    /// damage in a wood than they did in the open, and on the ground the scenario happens to
    /// drive across, so a different set of units is alive at the end of five hundred ticks.
    /// Before that it was aspect and landform, which fill two more fields of the attribute word
    /// from the height field: the word is hashed, so a ground that faces somewhere new is a state
    /// that has moved. Before that it was the terrain attributes themselves — canopy density,
    /// moisture and the bits the fire step will write. Before *that* it was the terrain bands,
    /// which are cut from the map's relief and stacked in order so that sand and snow can appear
    /// at all.
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenScenarioHash_IsStable()
        => Assert.Equal(16140099771963057552UL, HashScenario(20250101));
}
