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

        for (int slot = 0; slot < capacity && budget > 0; slot++)
        {
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
