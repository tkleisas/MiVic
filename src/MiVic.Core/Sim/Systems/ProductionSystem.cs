using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// Advances production queues and spawns finished units.
/// <para>
/// This is the heart of the faction asymmetry. Every building runs
/// <see cref="FactionProfile.ProductionSlots"/> jobs in parallel and each job
/// takes <c>BaseBuildTicks * 1000 / BuildSpeedPermille</c>, so a Κινέζοι factory
/// turns out six cheap units at a time while a Σοβιετικοί factory works on two
/// expensive ones. Nothing here is special-cased per faction: the numbers come
/// from the profile.
/// </para>
/// </summary>
public static class ProductionSystem
{
    /// <summary>Runs one production tick.</summary>
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

            ref Entity building = ref world.GetRefBySlot(slot);

            if (building.QueueLength == 0)
            {
                continue;
            }

            // A structure under construction has no tools to build with yet.
            if (building.ConstructionTicksRemaining > 0)
            {
                continue;
            }

            // A team actually in energy deficit has no power to build with. A team
            // merely at zero, but not running a deficit, still can — otherwise a
            // base with no power plant could never build one.
            ref TeamState team = ref world.TeamRef(building.TeamId);

            if (team.EnergyPerTick < 0 && team.Energy <= 0)
            {
                continue;
            }

            int slots = FactionProfile.For(building.Faction).ProductionSlots + team.BonusSlots;
            int parallel = Math.Min(slots, building.QueueLength);

            for (int i = 0; i < parallel; i++)
            {
                ref ProductionJob job = ref world.JobRef(slot, i);

                if (job.RemainingTicks > 0)
                {
                    job.RemainingTicks--;
                }
            }

            // Complete from the back so removal cannot invalidate earlier indices.
            for (int i = parallel - 1; i >= 0; i--)
            {
                ref ProductionJob job = ref world.JobRef(slot, i);

                if (job.RemainingTicks > 0)
                {
                    continue;
                }

                UnitKind kind = job.Kind;
                world.RemoveJobAt(slot, i);
                SpawnProduced(world, slot, kind);
            }
        }
    }

    /// <summary>
    /// Places a finished unit just outside its factory. The offset varies with
    /// the tick so a batch does not stack on one point, and it is derived from
    /// the tick rather than a random source so replays stay identical.
    /// </summary>
    private static void SpawnProduced(SimWorld world, int slot, UnitKind kind)
    {
        ref Entity building = ref world.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(kind);

        int spread = 14_000 + ((int)(world.Tick % 4) * 4_000);
        int direction = (int)(world.Tick % 4);

        int offsetX = direction switch { 0 => spread, 1 => -spread, 2 => 0, _ => 0 };
        int offsetZ = direction switch { 0 => 0, 1 => 0, 2 => spread, _ => -spread };

        WorldPos spawn = new(building.Position.X + offsetX, 0, building.Position.Z + offsetZ);

        // The offset knows nothing about the map, so a factory on a shoreline would
        // eventually put its next building in the lake. Nothing is ever placed on
        // water or lava.
        spawn = world.LegalSpawnSite(spawn);

        EntityId created = world.Spawn(
            building.Faction,
            building.TeamId,
            kind,
            spawn,
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);

        // A structure does not pop into existence fully formed. It is raised over
        // the last part of its build time and does nothing until it is up — which is
        // what the client's construction animation is showing.
        if (definition.IsBuilding)
        {
            ref Entity structure = ref world.GetRefBySlot(created.Slot);

            int rise = Math.Max(20, definition.BuildTicks / 3);
            structure.ConstructionTicksTotal = rise;
            structure.ConstructionTicksRemaining = rise;
        }
    }
}
