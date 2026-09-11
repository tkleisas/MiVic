namespace MiVic.Core.Sim;

/// <summary>
/// Computes routes, a few per tick.
/// <para>
/// Path searches are the single most expensive thing the simulation does, and
/// orders arrive in bursts: a hundred units told to attack at once would run a
/// hundred A* searches inside one tick, which shows up as a multi-hundred
/// millisecond hitch. Requests are therefore queued on the entities and drained
/// at a fixed rate. Units wait a few ticks for their route instead of stalling
/// the frame — a unit without a path simply holds position.
/// </para>
/// <para>
/// The budget is a count, never a duration: a time-based budget would make the
/// simulation depend on how fast the machine is, and that would break replays.
/// </para>
/// </summary>
public static class PathingSystem
{
    /// <summary>Routing attempts before an order is treated as unreachable.</summary>
    public const int MaxFailuresBeforeGivingUp = 3;

    /// <summary>Runs one routing tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int capacity = world.Capacity;
        int budget = SimConstants.MaxPathsPerTick;

        // <b>The scan starts where the previous tick's left off, not at slot zero.</b> A fixed
        // budget spent in ascending slot order is a budget the lowest slots can spend on their own,
        // and a queue that is scanned from the front every tick is not a queue: whoever is asking
        // last never asks at all. Measured on the standard skirmish while the approach loop was
        // re-asking for a route every ten ticks, slots above 386 were not served once in six
        // hundred ticks — forty units held a move goal for the whole match and never moved a
        // millimetre, which is what `waiting for a route` looks like from the inside. Rotating the
        // start makes the budget round-robin: a unit waits its turn, and the wait is bounded by how
        // many units are asking rather than by where they were spawned.
        //
        // The offset is the tick, so it is state the replay already carries and two machines still
        // agree about who was routed when.
        int start = (int)(world.Tick % capacity);

        for (int step = 0; step < capacity && budget > 0; step++)
        {
            int slot = start + step;

            if (slot >= capacity)
            {
                slot -= capacity;
            }

            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (!entity.NeedsPath)
            {
                continue;
            }

            entity.NeedsPath = false;

            if (world.RepathFrom(slot))
            {
                entity.PathFailures = 0;
            }
            else if (++entity.PathFailures >= MaxFailuresBeforeGivingUp)
            {
                // Unreachable: stop retrying and drop the order.
                entity.HasMoveGoal = false;
                entity.PathLength = 0;
                entity.PathCursor = 0;
                entity.PathFailures = 0;
            }

            budget--;
        }
    }
}
