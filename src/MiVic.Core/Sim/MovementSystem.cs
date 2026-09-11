using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// Moves entities along their path and keeps them on the terrain surface.
/// <para>
/// All arithmetic is integer: the direction is normalised with an integer square
/// root and each axis is scaled by <c>delta * step / distance</c>. That keeps
/// diagonal travel from being faster than straight travel without ever
/// introducing a float.
/// </para>
/// <para>
/// Long routes are walked in legs. A path is capped at
/// <see cref="SimConstants.MaxPathCells"/> waypoints, so when the waypoints run
/// out the entity re-searches from wherever it stands rather than following a
/// truncated route into a wall.
/// </para>
/// </summary>
public static class MovementSystem
{
    /// <summary>Runs one movement tick over every live entity, in slot order.</summary>
    public static void Tick(SimWorld world)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity e = ref world.GetRefBySlot(slot);

            if (e.HasMoveGoal && TryGetWaypoint(world, slot, ref e, out WorldPos waypoint))
            {
                // How far this mover gets this tick, which is its own speed scaled by the ground
                // under it. The same step drives both the movement and the arrival test below, so
                // a unit crawling through a bog cannot tick a waypoint off the list faster than
                // it is actually covering the distance to it.
                int step = StepMmPerTick(world, ref e);

                StepToward(world, ref e, waypoint, step);

                // Reaching a waypoint advances the route.
                if (e.Position.HorizontalDistanceTo(waypoint) <= step)
                {
                    e.PathCursor++;
                }
            }

            SnapToTerrain(world, ref e);
        }
    }

    /// <summary>
    /// How far a slot's unit will move in one tick, after the ground under it.
    /// <para>
    /// Public because it is a question worth asking from outside the tick loop: "why is this column
    /// slow" has an answer — the surface, the pressure and the two of them together — and the probe
    /// and the interface should be able to read the same number the wheels will use rather than
    /// working it out from a second copy of the arithmetic. A slot holding nothing answers zero.
    /// </para>
    /// </summary>
    public static int StepMmPerTick(SimWorld world, int slot)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.IsAliveSlot(slot) ? StepMmPerTick(world, ref world.GetRefBySlot(slot)) : 0;
    }

    /// <summary>
    /// How far an entity moves in one tick: its own speed, scaled by what the ground under it does
    /// to a mover of its weight.
    /// <para>
    /// <b>This is where ground pressure finally costs something.</b> The figure has always been
    /// per-role and per-faction and <see cref="TerrainLayer.CostPermille"/> has always multiplied
    /// the soft surfaces by it — but the cost was only ever spent by the pathfinder, so a Δυτικοί
    /// and a Σοβιετικοί column both crossed the same bog at their catalogue speeds and the
    /// Σοβιετικοί advantage in the mud season existed only as a cheaper route. Reading the same
    /// cost here turns it into a speed, and the rasputitsa becomes something a player watches
    /// happen: in mud a Σοβιετικοί tank (750 pressure) keeps about half its speed while a Δυτικοί
    /// one (1 100) keeps about a third, and on grass the two are identical.
    /// </para>
    /// <para>
    /// The context comes from <see cref="SimWorld.PathContextOf"/>, which is the mobility context
    /// the pathfinder plans with — including the team's own research, so «Βαθιά Μάχη» lowers
    /// effective pressure on the wheels exactly as it already does on the route. Nothing is
    /// recomputed here that the world is not already willing to answer.
    /// </para>
    /// </summary>
    private static int StepMmPerTick(SimWorld world, ref Entity e)
    {
        // A mover with no speed of its own has none to scale, and the answer is zero rather than
        // the one millimetre a floor would give it: a stationary unit ordered somewhere stands
        // where it is, which is what SpeedMmPerTick of zero has always meant.
        int own = e.SpeedMmPerTick.ToIntRound();

        if (own <= 0)
        {
            return 0;
        }

        UnitDefinition definition = UnitCatalog.Get(e.Kind);

        if (definition.IsBuilding || definition.Movement == MovementClass.Air)
        {
            return own;
        }

        int cell = world.Navigation.IndexOfWorld(e.Position);

        if (cell < 0)
        {
            return own;
        }

        PathContext context = world.PathContextOf(e.TeamId, e.Faction, e.Kind);
        int ground = world.TerrainTypes.SpeedPermilleAt(cell, context.Movement, context.GroundPressurePermille);

        return ground >= 1_000 ? own : Math.Max(1, (own * ground) / 1_000);
    }

    /// <summary>
    /// Resolves the next waypoint. Returns false when the entity has no route and
    /// none can be found, in which case its move order is cancelled.
    /// </summary>
    private static bool TryGetWaypoint(SimWorld world, int slot, ref Entity e, out WorldPos waypoint)
    {
        // A route is being computed on a later tick; hold position until it lands.
        if (e.NeedsPath)
        {
            waypoint = default;
            return false;
        }

        if (e.PathCursor < e.PathLength)
        {
            waypoint = world.Navigation.CentreOf(world.PathOf(slot)[e.PathCursor]);
            return true;
        }

        // No waypoints left. Arriving inside the destination cell counts as
        // arrival: the route is planned between cells, so requiring the exact
        // ordered point would make units stop just short and then give up.
        //
        // The goal this is asked about is the one the route was planned to. An ordered point the
        // unit cannot enter was clipped by SimWorld.RepathFrom to the nearest cell it can, and the
        // goal rewritten to that cell with it, so this test and the route search are always asking
        // about the same place — see the note there for what the two disagreeing cost.
        bool inGoalCell = world.Navigation.IndexOfWorld(e.Position) == world.Navigation.IndexOfWorld(e.MoveGoal);

        if (inGoalCell || e.Position.HorizontalDistanceTo(e.MoveGoal) <= SimConstants.ArrivalRadiusMm)
        {
            // Settle exactly on the ordered point so repeated orders to the same
            // place leave the unit in the same spot. For a clipped goal that point is the
            // centre of the cell the unit can reach, which is the same guarantee one cell over.
            e.Position = e.MoveGoal;
            ClearRoute(ref e);
            waypoint = default;
            return false;
        }

        // The route ran out short of the goal; ask for another leg.
        e.NeedsPath = true;
        waypoint = default;
        return false;
    }

    private static void ClearRoute(ref Entity e)
    {
        e.HasMoveGoal = false;
        e.MoveGoal = e.Position;
        e.PathLength = 0;
        e.PathCursor = 0;
        e.NeedsPath = false;
        e.PathFailures = 0;
    }

    /// <summary>
    /// Steps towards a waypoint in the horizontal plane only, by <paramref name="step"/> millimetres.
    /// <para>
    /// Steering in three dimensions is a trap: a waypoint's height comes from the
    /// terrain lattice while a unit's height is the bilinearly sampled surface, so
    /// on a slope the two differ by hundreds of millimetres. Including that
    /// difference in the step direction made units climb instead of advance, and
    /// the per-tick terrain snap then undid the climb — a unit would freeze just
    /// short of its waypoint forever. Height is the terrain's job, not the
    /// steering's.
    /// </para>
    /// <para>
    /// The step is handed in rather than read from the entity, because it is no longer the
    /// entity's own figure: it is that figure after the ground. Callers get it from
    /// <see cref="StepMmPerTick"/>, so the arrival test in <see cref="Tick"/> and the movement
    /// here are the same number by construction instead of by both remembering to scale it.
    /// </para>
    /// </summary>
    private static void StepToward(SimWorld world, ref Entity e, WorldPos target, int step)
    {
        long dx = (long)target.X - e.Position.X;
        long dz = (long)target.Z - e.Position.Z;

        if (dx == 0 && dz == 0)
        {
            return;
        }

        if (step <= 0)
        {
            return;
        }

        if (step > SimConstants.MaxStepPerTickMm)
        {
            step = SimConstants.MaxStepPerTickMm;
        }

        long distanceSquared = (dx * dx) + (dz * dz);
        long distance = IntMath.SqrtLong(distanceSquared);

        if (distance <= step)
        {
            // Snap exactly onto the waypoint so positions stay reproducible rather
            // than asymptotically approaching it.
            e.Position = new WorldPos(target.X, e.Position.Y, target.Z);
            return;
        }

        int mx = (int)((dx * step) / distance);
        int mz = (int)((dz * step) / distance);

        // Integer truncation can round the whole step to zero on a nearly
        // axis-aligned approach; nudge the dominant axis so units never stall.
        if (mx == 0 && mz == 0)
        {
            if (Math.Abs(dx) >= Math.Abs(dz))
            {
                mx = IntMath.Sign((int)dx);
            }
            else
            {
                mz = IntMath.Sign((int)dz);
            }
        }

        e.Position = new WorldPos(e.Position.X + mx, e.Position.Y, e.Position.Z + mz);
        e.Heading = TrigTable.Atan2Brads(mz, mx);

        // Odometer: the distance a unit has actually covered, which is what turns a
        // wheel. Euclidean, not the Manhattan sum of the two step components — a
        // diagonal step is 400 mm of travel, not the 566 mm its parts add up to,
        // and a wheel spun by the wrong number slides instead of rolling.
        if (mx != 0 || mz != 0)
        {
            e.DistanceTravelledMm += IntMath.Distance(mx, 0, mz);
        }
    }

    /// <summary>Keeps a unit glued to the ground, or at its altitude above it.</summary>
    private static void SnapToTerrain(SimWorld world, ref Entity e)
    {
        int ground = world.Terrain.SampleHeightMm(e.Position.X, e.Position.Z);
        int targetY = ground + e.AltitudeMm;

        if (e.Position.Y != targetY)
        {
            e.Position = new WorldPos(e.Position.X, targetY, e.Position.Z);
        }
    }

    /// <summary>
    /// Facing in binary radians (65536 = full turn), measured from +X towards +Z.
    /// Delegates to the deterministic integer atan2 in <see cref="TrigTable"/>.
    /// </summary>
    public static ushort HeadingFromDirection(int dx, int dz) => TrigTable.Atan2Brads(dz, dx);
}
