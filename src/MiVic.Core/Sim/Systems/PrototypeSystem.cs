using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// Advances a prototype run at a design bureau.
/// <para>
/// A completed run does two things: it approves the design for ordinary factory
/// production, and it delivers the prototype itself as a real unit. That is the
/// Σοβιετικοί signature — the army's composition is decided by what has been
/// proven, so it cannot pivot late.
/// </para>
/// </summary>
public static class PrototypeSystem
{
    /// <summary>Runs one prototype tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            if (!state.IsPrototyping)
            {
                continue;
            }

            state.PrototypeTicksRemaining--;

            if (state.PrototypeTicksRemaining > 0)
            {
                continue;
            }

            UnitKind kind = state.PrototypeKind;
            state.PrototypeTicksRemaining = 0;
            state.PrototypeKind = UnitKind.None;
            state.ApprovedMask |= 1u << (int)kind;

            Deliver(world, team, kind);
        }
    }

    /// <summary>Places the finished prototype next to the bureau that built it.</summary>
    private static void Deliver(SimWorld world, int team, UnitKind kind)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity building = ref world.GetRefBySlot(slot);

            if (building.TeamId != team || building.Kind != UnitKind.DesignBureau)
            {
                continue;
            }

            UnitDefinition definition = UnitCatalog.Get(kind);

            WorldPos spawn = new(building.Position.X + 16_000, 0, building.Position.Z + 16_000);

            world.Spawn(
                building.Faction,
                team,
                kind,
                spawn,
                Fix32.FromInt(definition.SpeedMmPerTick),
                definition.Health);

            return;
        }
    }
}
