using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// A move order the unit can never finish, which used to cost it the rest of the match.
/// <para>
/// An attack order sets a move goal at the target's position. Where the target stands is not
/// always ground the attacker may enter — water, lava, and anywhere the grid refuses a hull — and
/// the route search clips such a goal to the nearest cell the mover <em>can</em> enter. The goal
/// itself was left where it was ordered, so the arrival test and the route answered about two
/// different places: the unit walked to the clipped cell, never arrived at the ordered one, asked
/// for the route again on the next tick, was told by the route search that it was already in the
/// goal cell — which is <em>success</em>, so <c>PathFailures</c> was reset to zero — and repeated
/// that forever. `path 0 cells at 0, waiting for a route, 0 failures`, travel frozen, nothing
/// anywhere reporting a fault, because nothing was failing.
/// </para>
/// <para>
/// These tests pin the three answers: an order that can be carried out is still carried out, an
/// order that cannot is not marched at, and a route that ends short ends <em>honestly</em> —
/// <c>move none</c> at the nearest ground the unit could reach, rather than a goal it is not
/// pursuing.
/// </para>
/// </summary>
public sealed class UnreachableGoalTests
{
    /// <summary>The capacity the standard skirmish is laid out in, as the client builds it.</summary>
    private const int SkirmishCapacity = 1024;

    /// <summary>
    /// The line across the standard map with no lava and no snow on it, which is the lane the
    /// sensor scenario uses for the same reason: a test that fights in a crater is pinning the
    /// weather rather than the rule.
    /// </summary>
    private static WorldPos Lane(int xMetres) => WorldPos.GroundMetres(xMetres, -70);

    private static SimWorld Skirmish()
    {
        SimWorld world = new(20250101UL, SkirmishCapacity, MatchRoster.For(ScenarioKind.Skirmish));
        Scenario.Build(world, ScenarioKind.Skirmish);
        return world;
    }

    /// <summary>
    /// The probe's own reading: a route was asked for and there is nothing in front of the unit.
    /// This is the state a stalled unit is found in, so it is the state the counting test counts.
    /// </summary>
    private static int WaitingForARoute(SimWorld world)
    {
        int waiting = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity e = ref world.GetRefBySlot(slot);

            if (e.NeedsPath && e.PathLength == 0)
            {
                waiting++;
            }
        }

        return waiting;
    }

    /// <summary>
    /// A cell a tracked mover cannot enter, and the cell beside it the mover can.
    /// <para>
    /// The clipped cell is asked of the world rather than chosen here, because it is the answer the
    /// route search will give: the first enterable cell in <see cref="NavGrid.NearestWalkable"/>'s
    /// own scan order. A test that picked its own "nearest" cell would be testing a second opinion.
    /// </para>
    /// </summary>
    private static (int Blocked, int Clipped) BlockedCell(SimWorld world, PathContext context)
    {
        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            if (world.TerrainTypes.IsPassable(cell, context.Movement))
            {
                continue;
            }

            int clipped = world.Navigation.NearestWalkable(cell, world.TerrainTypes, context);

            if (clipped >= 0)
            {
                return (cell, clipped);
            }
        }

        throw new InvalidOperationException("This map has no impassable cell with a passable neighbour.");
    }

    /// <summary>
    /// <b>The counting test.</b> The standard skirmish, six hundred ticks, and not one unit left
    /// asking for a route it is not going to get.
    /// <para>
    /// Measured before the fix, on this seed and this tick: <b>200</b> units reading `waiting for a
    /// route` at tick 600, of which 116 had held that state for more than a hundred consecutive
    /// ticks and <b>40</b> had held a move goal for more than half the match without moving a single
    /// millimetre. The number to beat is zero, and the failure prints the count so the next person
    /// reads it rather than measuring for it.
    /// </para>
    /// <para>
    /// It was measured down to <b>0</b>, and the two authors of the 200 were separate. The first was
    /// the route budget: the approach loop re-asked for a route every ten ticks for every ordered
    /// attacker that was out of reach, which threw away a route that was still good and asked for
    /// another — 6.8 searches a tick against a budget of four — and the budget was spent from slot
    /// zero every tick, so slots above 386 were never served once in six hundred ticks. The second
    /// was the clipped goal above, which is what the probe's `0.0 m to go, waiting for a route`
    /// reads.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStandardSkirmishLeavesNoUnitWaitingForARoute()
    {
        SimWorld world = Skirmish();
        world.RunTicks(600);

        int waiting = WaitingForARoute(world);

        Assert.True(
            waiting == 0,
            $"{waiting} units are waiting for a route at tick {world.Tick} of the standard skirmish " +
            "(200 before the clipped goal and the route budget were fixed).");
    }

    /// <summary>
    /// The same question asked of a long match, because a slower version of the same loop would
    /// pass the six-hundred-tick test and fail here.
    /// <para>
    /// Two answers rather than one. The count at the end is the state the world is left in; the
    /// longest a unit ever spent waiting with a live goal is the loop, which a snapshot at one tick
    /// can miss entirely — a unit that waits thirty ticks out of every forty is stalled by any
    /// measure a player would use and by none that a single tick can see. Measured after the fix:
    /// no unit waits more than <b>42</b> consecutive ticks, which is a route queue draining at four
    /// searches a tick rather than a loop; before it, 116 units were over a hundred.
    /// </para>
    /// </summary>
    [Fact]
    public void NoUnitIsLeftWaitingForARouteInALongerMatch()
    {
        SimWorld world = Skirmish();

        int[] consecutive = new int[world.Capacity];
        int worst = 0;

        for (int tick = 0; tick < 1_800; tick++)
        {
            world.Step();

            for (int slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.IsAliveSlot(slot))
                {
                    continue;
                }

                ref Entity e = ref world.GetRefBySlot(slot);

                if (e.NeedsPath && e.PathLength == 0 && e.HasMoveGoal)
                {
                    consecutive[slot]++;

                    if (consecutive[slot] > worst)
                    {
                        worst = consecutive[slot];
                    }
                }
                else
                {
                    consecutive[slot] = 0;
                }
            }
        }

        int waiting = WaitingForARoute(world);

        Assert.True(waiting == 0, $"{waiting} units are waiting for a route at tick {world.Tick} of a long match.");
        Assert.True(
            worst < 100,
            $"a unit spent {worst} consecutive ticks waiting for a route while holding a move goal " +
            "(116 units were over a hundred before the fix; a route queue drains in tens of ticks).");
    }

    /// <summary>
    /// An attack order on a target it can reach is still an attack order: the unit drives there and
    /// opens fire. This is the case the fix must not cost, so it is checked on its own.
    /// <para>
    /// The target has no speed so that the distance closing is the attacker's doing and not the
    /// target's: the AI plays every team that is not the player's, and a mobile enemy here would be
    /// marched about by it — which is how the first version of this test measured the wrong unit.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAttackOrderOnAReachableTargetStillClosesAndFires()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 16);

        EntityId attacker = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, Lane(60), Fix32.FromInt(600), 5_000);
        EntityId victim = world.Spawn(Faction.Western, 2, UnitKind.Tank, Lane(-160), Fix32.Zero, 5_000);

        int range = UnitCatalog.Get(UnitKind.Tank).AttackRangeMm;
        int health = world.GetRefBySlot(victim.Slot).Health;

        Assert.True(
            world.GetRefBySlot(attacker.Slot).Position.HorizontalDistanceTo(world.GetRefBySlot(victim.Slot).Position) > range,
            "the target was already in range, so this proves nothing about closing the distance");

        world.Enqueue(SimCommand.Attack(attacker, victim, world.Tick + 1, 0));

        // The order is taken: the unit has somewhere to go.
        world.Step();

        ref Entity marching = ref world.GetRefBySlot(attacker.Slot);
        Assert.True(marching.HasAttackOrder, "the attack order was not taken.");
        Assert.True(marching.HasMoveGoal, "a unit ordered onto a reachable target was not sent towards it.");

        world.RunTicks(600);

        ref Entity e = ref world.GetRefBySlot(attacker.Slot);
        ref Entity t = ref world.GetRefBySlot(victim.Slot);

        Assert.True(
            e.Position.HorizontalDistanceTo(t.Position) <= range,
            $"the unit never closed to its {range / 1_000.0:F0} m range: " +
            $"{e.Position.HorizontalDistanceTo(t.Position) / 1_000.0:F1} m away after 600 ticks.");
        Assert.True(t.Health < health, $"the unit closed the distance and never fired: {t.Health} of {health} left.");
    }

    /// <summary>
    /// <b>A target standing where the unit cannot go is an order to wait, not an order to walk.</b>
    /// The unit keeps the attack — it is what will fire when the enemy comes into reach — and is
    /// given no move goal at all, because a goal on ground it cannot enter is the trap this whole
    /// file is about.
    /// <para>
    /// And the goal is dropped <em>only</em> while it is unsatisfiable: the ground under the target
    /// then becomes one the attacker can cross — which is what a bridge does, cell by cell — and the
    /// approach loop asks the same question again ten ticks later, sends the unit in, and it
    /// finishes the enemy off. Both halves are in the one test because either alone is passable by
    /// a unit that does nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAttackOrderOnATargetItCannotOccupyKeepsTheOrderAndDropsTheGoal()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 16);

        EntityId attacker = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, Lane(60), Fix32.FromInt(600), 5_000);

        // No speed, so the target cannot be marched out of the water by the AI while the test
        // watches — it is the standing enemy the case is about.
        EntityId victim = world.Spawn(Faction.Western, 2, UnitKind.Infantry, Lane(-160), Fix32.Zero, 120);

        // The ground under the target becomes one a tracked mover cannot enter, set after the spawn
        // because Spawn moves a unit that would appear somewhere it could not stand.
        WorldPos standing = world.GetRefBySlot(victim.Slot).Position;
        int cell = world.Navigation.IndexOfWorld(standing);
        world.TerrainTypes.SetType(cell, TerrainType.DeepWater);

        Assert.False(world.CanStandAt(attacker.Slot, standing), "the target's cell is one the attacker could enter");

        world.Enqueue(SimCommand.Attack(attacker, victim, world.Tick + 1, 0));

        for (int tick = 0; tick < 40; tick++)
        {
            world.Step();
        }

        ref Entity waiting = ref world.GetRefBySlot(attacker.Slot);

        Assert.True(waiting.HasAttackOrder, "the attack order was thrown away instead of kept.");
        Assert.Equal(victim.Slot, waiting.TargetSlot);
        Assert.False(waiting.HasMoveGoal, "the unit was sent at a target standing where it cannot go.");
        Assert.Equal(0, waiting.PathLength);
        Assert.False(waiting.NeedsPath);
        Assert.Equal(0, waiting.PathFailures);

        // Nothing about the unit moved: it stands where it was ordered to stand and waits.
        Assert.Equal(Lane(60).X, waiting.Position.X);
        Assert.Equal(Lane(60).Z, waiting.Position.Z);

        int health = world.GetRefBySlot(victim.Slot).Health;

        // The ground becomes crossable. The approach loop asks again on the next decision tick.
        world.TerrainTypes.SetType(cell, TerrainType.Grass);
        world.RunTicks(600);

        ref Entity e = ref world.GetRefBySlot(attacker.Slot);
        ref Entity t = ref world.GetRefBySlot(victim.Slot);

        Assert.True(t.Health < health || !world.IsAliveSlot(victim.Slot), "the unit never fired once the target came into reach.");
        Assert.True(
            e.Position.HorizontalDistanceTo(t.Position) < 40_000,
            $"the unit stayed {e.Position.HorizontalDistanceTo(t.Position) / 1_000.0:F1} m away once the way was open.");
    }

    /// <summary>
    /// <b>A goal that is clipped to the cell the unit is standing on.</b> The unit is ordered onto
    /// the middle of ground it cannot enter from a cell right beside it, so the route's answer is
    /// "you are already there" — the exact shape the wander orders were fixed for one layer up.
    /// <para>
    /// Before the fix this was the tightest version of the loop: <c>RepathFrom</c> answered success
    /// with an empty route, <c>PathFailures</c> was cleared by that success, and the movement step
    /// set <c>NeedsPath</c> again on every tick. The unit read `waiting for a route` forever with
    /// nine hundred millimetres of nothing in front of it. It now ends the order on the tick the
    /// route is served: no goal, no route, no failures, no movement — the honest `move none`.
    /// </para>
    /// </summary>
    [Fact]
    public void AGoalSnappedToTheUnitsOwnCellDoesNotRepathForever()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 8);
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);

        (int blocked, int clipped) = BlockedCell(world, context);

        WorldPos start = world.Navigation.CentreOf(clipped);
        WorldPos goal = world.Navigation.CentreOf(blocked);

        EntityId unit = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            new WorldPos(start.X, world.Terrain.SampleHeightMm(start.X, start.Z), start.Z),
            Fix32.FromInt(400),
            320);

        world.OrderMove(unit, goal, 0);
        world.RunTicks(10);

        ref Entity e = ref world.GetRefBySlot(unit.Slot);

        Assert.False(e.HasMoveGoal, "the order was still being pursued after it had nowhere to go.");
        Assert.False(e.NeedsPath, "the unit is still waiting for a route to its own cell.");
        Assert.Equal(0, e.PathLength);
        Assert.Equal(0, e.PathFailures);

        // And it stays that way: the loop this replaced set NeedsPath again on every single tick.
        world.RunTicks(200);

        ref Entity e2 = ref world.GetRefBySlot(unit.Slot);

        Assert.False(e2.NeedsPath, "the unit asked for a route again after arriving at the nearest cell it could enter.");
        Assert.False(e2.HasMoveGoal);
        Assert.Equal(0, e2.PathLength);
        Assert.Equal((start.X, start.Z), (e2.Position.X, e2.Position.Z));
    }

    /// <summary>
    /// The same goal from far away: the unit closes the distance, stops at the nearest ground it can
    /// enter, and the order ends there rather than being thrown away or walked forever.
    /// <para>
    /// This is the case a player produces by clicking on water. Standing at the shore with
    /// <c>move none</c> is the state the world already understands; the alternative on offer — drop
    /// the order where it was given — refuses to walk a unit towards a click it can plainly see,
    /// and the other one — walk at the lake forever — is what was wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void AMoveOrderOntoGroundTheUnitCannotEnterEndsAtTheNearestCellItCan()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 8);
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);
        PathFinder finder = new(world.Navigation.CellCount);
        int[] buffer = new int[SimConstants.MaxPathCells];

        (int blocked, int clipped) = BlockedCell(world, context);
        int start = -1;

        for (int cell = 0; cell < world.Navigation.CellCount && start < 0; cell++)
        {
            if (!world.TerrainTypes.IsPassable(cell, context.Movement) || cell == clipped)
            {
                continue;
            }

            int manhattan = Math.Abs(world.Navigation.CellX(cell) - world.Navigation.CellX(clipped)) +
                Math.Abs(world.Navigation.CellZ(cell) - world.Navigation.CellZ(clipped));

            if (manhattan is < 6 or > 20)
            {
                continue;
            }

            if (finder.FindPath(world.Navigation, world.TerrainTypes, context, cell, clipped, buffer) > 0)
            {
                start = cell;
            }
        }

        Assert.True(start >= 0, "no start cell with a route to the clipped cell was found.");

        WorldPos from = world.Navigation.CentreOf(start);
        EntityId unit = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            new WorldPos(from.X, world.Terrain.SampleHeightMm(from.X, from.Z), from.Z),
            Fix32.FromInt(600),
            320);

        world.OrderMove(unit, world.Navigation.CentreOf(blocked), 0);
        world.RunTicks(2_000);

        ref Entity e = ref world.GetRefBySlot(unit.Slot);
        WorldPos shore = world.Navigation.CentreOf(clipped);

        Assert.False(e.HasMoveGoal, "the unit is still pursuing a goal it cannot reach.");
        Assert.False(e.NeedsPath);
        Assert.Equal(0, e.PathLength);
        Assert.Equal(0, e.PathFailures);

        // It stopped on the cell the route was clipped to, not short of it and not inside the water.
        Assert.Equal((shore.X, shore.Z), (e.Position.X, e.Position.Z));
        Assert.True(e.DistanceTravelledMm > 0, "the unit never walked towards the order at all.");
    }

    /// <summary>
    /// An order the gun can never carry out is refused where it is given, in the words the player is
    /// shown — the same treatment an order to attack an ally has always had, because it is the same
    /// kind of mistake.
    /// <para>
    /// A tank ordered onto an aeroplane used to be accepted and then ignored forever: the unit held
    /// the attack order, held a move goal at the aircraft, and chased it across the map re-asking
    /// for a route every ten ticks, never firing a shot. That is an order that can never be
    /// satisfied, and it is how a unit becomes a free kill.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(UnitKind.Tank, UnitKind.Aircraft, "δεν βάλλει κατά αέρος")]
    [InlineData(UnitKind.AntiAirEmplacement, UnitKind.Tank, "δεν βάλλει κατά εδάφους")]
    [InlineData(UnitKind.Harvester, UnitKind.Tank, "άοπλη μονάδα")]
    public void AnAttackOrderAGunCanNeverCarryOutIsRefused(UnitKind gunner, UnitKind victim, string refusal)
    {
        SimWorld world = new(seed: 20250101UL, capacity: 8);

        EntityId attacker = world.Spawn(Faction.Soviet, 0, gunner, Lane(0), Fix32.FromInt(400), 320);
        EntityId target = world.Spawn(Faction.Western, 2, victim, Lane(80), Fix32.FromInt(400), 320);

        Assert.False(world.CanAttack(attacker, target, out string why));
        Assert.Equal(refusal, why);

        world.Enqueue(SimCommand.Attack(attacker, target, world.Tick + 1, 0));
        world.RunTicks(40);

        ref Entity e = ref world.GetRefBySlot(attacker.Slot);

        Assert.False(e.HasAttackOrder, "an order the gun will refuse was taken anyway.");
        Assert.Equal(-1, e.TargetSlot);
        Assert.False(e.HasMoveGoal);
        Assert.False(e.NeedsPath);
    }

    /// <summary>
    /// An order to attack an ally is refused with the same words, and this is the rule the others
    /// were written beside: an order can never be issued for a target the gun will refuse.
    /// </summary>
    [Fact]
    public void AnAttackOrderOnAnAllyIsRefused()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 8);

        // Teams 0 and 1 are allied in the standard skirmish, which is why this pair is hostile to
        // nobody rather than to each other.
        EntityId attacker = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, Lane(0), Fix32.FromInt(400), 320);
        EntityId ally = world.Spawn(Faction.Chinese, 1, UnitKind.Tank, Lane(80), Fix32.FromInt(400), 320);

        Assert.False(world.CanAttack(attacker, ally, out string why));
        Assert.Equal("είναι σύμμαχος", why);

        world.Enqueue(SimCommand.Attack(attacker, ally, world.Tick + 1, 0));
        world.RunTicks(40);

        ref Entity e = ref world.GetRefBySlot(attacker.Slot);

        Assert.False(e.HasAttackOrder);
        Assert.Equal(-1, e.TargetSlot);
        Assert.False(e.HasMoveGoal);
    }

    /// <summary>
    /// The approach loop does not ask for a route it already has.
    /// <para>
    /// This is the demand half of the fix, counted rather than described: the loop used to re-issue
    /// a goal every ten ticks for every ordered attacker that was out of reach, which reset
    /// <c>PathLength</c> and asked for another search. Across the standard skirmish that alone was
    /// 4 061 searches in six hundred ticks — 6.8 a tick against a budget of four — and because the
    /// budget was spent from slot zero, the units that never got one were the ones spawned last.
    /// A single unit here asks once, and asks again only when its target leaves the cell its route
    /// is going to, so the count over two hundred ticks is a handful rather than twenty.
    /// </para>
    /// </summary>
    [Fact]
    public void TheApproachDoesNotAskForTheSameRouteEveryTenTicks()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 16);

        EntityId attacker = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, Lane(60), Fix32.FromInt(600), 5_000);
        EntityId victim = world.Spawn(Faction.Western, 2, UnitKind.Tank, Lane(-160), Fix32.Zero, 5_000);

        world.Enqueue(SimCommand.Attack(attacker, victim, world.Tick + 1, 0));

        int requests = 0;
        bool wasAsking = false;

        for (int tick = 0; tick < 200; tick++)
        {
            world.Step();

            ref Entity e = ref world.GetRefBySlot(attacker.Slot);

            if (e.NeedsPath && !wasAsking)
            {
                requests++;
            }

            wasAsking = e.NeedsPath;
        }

        Assert.True(
            requests <= 3,
            $"the unit asked for a route {requests} times in 200 ticks; the loop this replaced asked " +
            "every ten ticks whether or not it already had one.");
    }
}
