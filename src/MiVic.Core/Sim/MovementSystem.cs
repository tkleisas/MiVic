using MiVic.Core.Numerics;

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
                int step = Math.Max(1, e.SpeedMmPerTick.ToIntRound());
                StepToward(world, ref e, waypoint);

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
        bool inGoalCell = world.Navigation.IndexOfWorld(e.Position) == world.Navigation.IndexOfWorld(e.MoveGoal);

        if (inGoalCell || e.Position.HorizontalDistanceTo(e.MoveGoal) <= SimConstants.ArrivalRadiusMm)
        {
            // Settle exactly on the ordered point so repeated orders to the same
            // place leave the unit in the same spot.
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
    /// Steps towards a waypoint in the horizontal plane only.
    /// <para>
    /// Steering in three dimensions is a trap: a waypoint's height comes from the
    /// terrain lattice while a unit's height is the bilinearly sampled surface, so
    /// on a slope the two differ by hundreds of millimetres. Including that
    /// difference in the step direction made units climb instead of advance, and
    /// the per-tick terrain snap then undid the climb — a unit would freeze just
    /// short of its waypoint forever. Height is the terrain's job, not the
    /// steering's.
    /// </para>
    /// </summary>
    private static void StepToward(SimWorld world, ref Entity e, WorldPos target)
    {
        long dx = (long)target.X - e.Position.X;
        long dz = (long)target.Z - e.Position.Z;

        if (dx == 0 && dz == 0)
        {
            return;
        }

        int step = e.SpeedMmPerTick.ToIntRound();

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
