namespace MiVic.Core.Sim;

/// <summary>
/// Raises newly built structures.
/// <para>
/// A structure arrives on the map unfinished and does nothing until it is up. This
/// is the simulation half of the construction animation: the client can draw a
/// building rising out of the ground, but only because the simulation says it is
/// not finished yet.
/// </para>
/// <para>
/// Deliberately not a resource sink. The cost was already paid when the job was
/// queued, so this is a period of vulnerability and visibility rather than a second
/// bill — a structure is a target from the moment it appears, before it can shoot
/// back or pay for itself.
/// </para>
/// </summary>
public static class ConstructionSystem
{
    /// <summary>Runs one construction tick.</summary>
    public static void Tick(SimWorld world)
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

            if (entity.ConstructionTicksRemaining > 0)
            {
                entity.ConstructionTicksRemaining--;
            }
        }
    }
}
