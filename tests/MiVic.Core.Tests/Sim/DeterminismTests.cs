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
    /// Last changed by a goal the mover cannot reach being dropped rather than walked at forever, and
    /// by the route budget becoming a queue. This field used to leave units reading `waiting for a
    /// route` in the hundreds — at tick six hundred, two hundred of them, forty of which had held a
    /// move goal for more than half the match without moving a millimetre — because the approach loop
    /// re-asked for a route every ten ticks for every ordered attacker that was out of reach, threw
    /// away the route it already had to do it, and the four searches a tick were spent from slot zero
    /// so the units spawned last never got one at all. Sixty tanks and three headquarters is most of
    /// two armies, so the whole battle now runs on a different clock: units that stood still are
    /// driving, arriving, and shooting. The clipped goal is inside this number for the same reason —
    /// an ordered point a mover cannot enter is no longer pursued to the end of the match, so an
    /// attack order on a target that walks onto water ends at the water's own edge. The third part,
    /// an order the gun can never carry out being refused where it is given, turns out to move
    /// nothing here: the AI has never ordered a tank at an aeroplane, so that half is pinned by
    /// <c>UnreachableGoalTests</c> instead.
    /// </para>
    /// <para>
    /// Before that it was armour and by mud costing a speed. Every structure and every machine on
    /// this field now reduces each hit by a percentage — Σοβιετικοί buildings most, Δυτικοί
    /// vehicles most, in opposite directions — so the same shots that used to kill a tank leave it
    /// alive, and the eighteen tanks a side spend longer in contact and fire more often before
    /// they die. Ground pressure, which was already per-faction and already priced mud for the
    /// pathfinder, now scales the per-tick step as well, so any unit that crosses a soft surface
    /// here takes longer to do it and the whole battle runs on a different clock. Splash damage is
    /// in this number too, for a reason of its own: a salvo used to scale every victim in its
    /// radius by the cover of the one thing it was aimed at, and it now asks the question of each
    /// victim, so artillery does different damage to a formation than it did.
    /// </para>
    /// <para>
    /// Before that it was allied forces ceasing to damage each other. Teams 0 and 1 are allied in
    /// this scenario and in every match the game ships, and every weapon, blast, attack order and
    /// off-map strike asked whether a target was on the <em>same team</em> rather than on the same
    /// side — so the two allies shot each other whenever their formations met, and the crossing
    /// rule beside them spared an ally's bridge only because it had been written by asking the real
    /// question. This scenario has a hundred and eighty tanks across all three teams and the AI
    /// plays two of them, so the fix moves it in both directions: the allied pair stops killing its
    /// own, and the enemy meets an army that is still intact. The morale system counted friends and
    /// enemies the same wrong way — an ally standing beside a unit was counted as an enemy, and a
    /// unit that feels outnumbered is a unit that reloads slower — so that correction is inside this
    /// number as well. <c>AllianceTests</c> pins each path, including a four-hundred-tick run of the
    /// standard skirmish that fails with a hundred and twenty allied targets on the old rule; the
    /// sensor scenario below is untouched by any of it, because it has no allied pair on it.
    /// </para>
    /// <para>
    /// Before that it was the AI learning what a defended position is. Teams 1 and 2 in this scenario
    /// are played by <see cref="AiSystem"/>, and it now raises a Πυροβολείο, a Σταθμός Ραντάρ and an
    /// Αντιαεροπολικό Πυροβολείο on sites it chooses and scores for itself — two more structures on
    /// the ground beside the two AI headquarters here — and it commands the armour the scenario
    /// spawns by a different rule than it did: an enemy the team cannot see is not a target for an
    /// attack order, an ally is never one, and a position covered by enemy guns is not assaulted by
    /// a force that cannot answer them. Both halves are inside this scenario rather than beside it,
    /// so both are part of what moved the number.
    /// </para>
    /// <para>
    /// Before that it was the production queues becoming state: which role a building is making, how
    /// long the job was always going to take, how far along it is and how many are waiting are
    /// folded in for every live slot — whether or not it is building anything, because an empty
    /// queue is the number zero rather than an absence, the same rule the crossings carry. That
    /// entry's own note — that this scenario's queues move but that it never exercised the AI
    /// raising a <em>structure</em> — is no longer true of the path: the AI raises structures here
    /// now, which is most of why this number moved.
    /// </para>
    /// <para>
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
    /// <para>
    /// <b>It did not move when detection and power arrived, and that was recorded rather than
    /// assumed; it moved when the AI began to use them, which is not the same event.</b> This
    /// scenario still contains nothing that anybody <em>placed</em>: it is sixty tanks, three
    /// headquarters and three power plants, and a tank sees 130 m and shoots 110 — the one case
    /// where reach and sight already agreed — so the arrival of the sensor chain had nothing here
    /// to decide differently. What the AI spends the material it earns on is a fact about this
    /// scenario though, and the emplacements and the radar standing beside two of these
    /// headquarters by tick five hundred are the sensor chain deciding something. The systems
    /// themselves are pinned by <see cref="GoldenSensorScenarioHash_IsStable"/>, which is a
    /// scenario built to exercise them.
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenScenarioHash_IsStable()
        => Assert.Equal(2975277282099602117UL, HashScenario(20250101));

    /// <summary>
    /// A fixed defensive scene with the whole sensor chain in it: a Σοβιετικοί line of two gun
    /// emplacements with a radar station and a power plant behind them, industry competing for
    /// the same generation, and a Δυτικοί tank and a Καταδρομέας walking in on it.
    /// <para>
    /// It exists because the older golden scenario does not contain a single thing the sensor
    /// chain changes — see the note on it — so a change that broke detection, radar coverage or
    /// the power ledger would move no hash at all. This one does: the tank is engaged at 190 m
    /// only while the radar has power, the stalker flickers in and out of view as the fraction
    /// of the radius catches it and loses it, and whether the second gun emplacement is lit
    /// depends on what the factory took off the grid first.
    /// </para>
    /// <para>
    /// Last changed by armour and by mud costing a speed, which is the second time this number has
    /// moved and for the first time from something other than the sensor chain. The gun here is a
    /// Πυροβολείο firing at a Δυτικοί tank, so the 45-damage shell now arrives composed: the cover
    /// of the lane it lands on, then the tank's 850 for being a Δυτικοί machine and its 1 000 for
    /// being an ordinary one. The tank therefore lives longer, keeps its 190 m of reach for longer,
    /// and the ticks on which the radar is doing the work are not the ticks they were. The mud lane
    /// this scenario was built to avoid is still avoided — everything stands on the one line across
    /// the map with no lava and no snow — so the movement change is not in this number, which is
    /// worth knowing rather than assuming.
    /// </para>
    /// <para>
    /// <b>It did not move when the unreachable goal was fixed, which is worth recording rather than
    /// assuming.</b> This scene is one tank and one stalker, both ordered onto ground they can stand
    /// on, both arriving inside the first hundred ticks, and no target in it ever stands where the
    /// other cannot go — so neither the clipped goal nor the route budget had anything here to decide
    /// differently, and the number is the one the sensor chain left.
    /// </para>
    /// <para>
    /// Before that it was the arrival of exactly that: a detection radius per role, radar coverage
    /// stamped into the fog, stealth detected at a fraction of it, and a power ledger that sheds
    /// radars before anything else. That was its first value.
    /// </para>
    /// </summary>
    [Fact]
    public void GoldenSensorScenarioHash_IsStable()
    {
        Assert.Equal(HashSensorScenario(20250101), HashSensorScenario(20250101));
        Assert.Equal(3928059992715472755UL, HashSensorScenario(20250101));
    }

    /// <summary>
    /// The sensor scenario. Everything stands on the lane at z = −70 m, which is the one line
    /// across the standard map with no lava and no snow on it: a hash taken over a fight in a
    /// crater or a snowdrift would be pinning the weather rather than the sensor chain.
    /// <para>
    /// The line-up is chosen so the radar is doing real work rather than being scenery. The
    /// tank arrives at 180 m from the nearest Πυροβολείο, which is inside the gun's 200 m and
    /// outside the 170 m it can see — so it is engaged on the first tick *because the radar is
    /// looking*, and would not be engaged at all without one. The Καταδρομέας crosses the same
    /// ground under the umbrella and is found by it at half the radius, which puts both ends of
    /// the sensor chain in the hash. The base sits one unit of load below the point where the
    /// grid would shed a radar, so any move in the power numbers moves this number too.
    /// </para>
    /// </summary>
    private static ulong HashSensorScenario(ulong seed)
    {
        SimWorld world = new(seed, capacity: 64);

        WorldPos Stand(int xMetres) => WorldPos.GroundMetres(xMetres, -70);

        world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, Stand(200), Fix32.Zero, 5_000);
        world.Spawn(Faction.Soviet, 0, UnitKind.PowerPlant, Stand(140), Fix32.Zero, 1_200);
        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, Stand(80), Fix32.Zero, 2_000);
        world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, Stand(20), Fix32.Zero, 1_500);

        // Two radars and two guns: generation 16 against a load of 15, so both sets run and
        // one more factory would put one of them out.
        world.Spawn(Faction.Soviet, 0, UnitKind.RadarStation, Stand(-60), Fix32.Zero, 900);
        world.Spawn(Faction.Soviet, 0, UnitKind.RadarStation, Stand(-160), Fix32.Zero, 900);
        world.Spawn(Faction.Soviet, 0, UnitKind.GunEmplacement, Stand(-140), Fix32.Zero, 1_400);
        world.Spawn(Faction.Soviet, 0, UnitKind.GunEmplacement, Stand(-220), Fix32.Zero, 1_400);

        EntityId tank = world.Spawn(
            Faction.Western, 2, UnitKind.Tank, Stand(40), Fix32.FromInt(400), 320);
        EntityId stalker = world.Spawn(
            Faction.Western, 2, UnitKind.StealthRecon, WorldPos.GroundMetres(150, -70), Fix32.FromInt(420), 120);

        world.OrderMove(tank, Stand(-140), 0);
        world.OrderMove(stalker, WorldPos.GroundMetres(-60, -150), 0);
        world.RunTicks(500);

        return StateHash.Compute(world);
    }
}
