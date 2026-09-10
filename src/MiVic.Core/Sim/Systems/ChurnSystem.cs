using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// Wears the ground down under the units driving over it.
/// <para>
/// This is what makes ground pressure a dynamic mechanic rather than a table. A
/// column of heavy armour churns its own route into a bog and slows itself down,
/// while light infantry walking the same line barely mark it — the *rasputitsa*
/// created by the army that suffers from it.
/// </para>
/// <para>
/// Churn is per cell, rises with the mover's ground pressure, and settles over
/// time. It is state like any other: deterministic, hashed, and identical in a
/// replay.
/// </para>
/// </summary>
public static class ChurnSystem
{
    /// <summary>Ticks between decay passes. Wear settles slowly, so this is cheap.</summary>
    public const int DecayInterval = 40;

    /// <summary>Churn added per tick per full unit of ground pressure.</summary>
    private const int ChurnPerTickPermille = 3;

    /// <summary>Runs one churn tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        TerrainLayer terrain = world.TerrainTypes;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            // Only ground units that are actually moving wear anything, and a unit
            // standing on a cell does not dig itself in.
            if (!entity.HasMoveGoal || entity.SpeedMmPerTick.Raw <= 0)
            {
                continue;
            }

            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (definition.Movement is MovementClass.Air or MovementClass.None || definition.IsBuilding)
            {
                continue;
            }

            int cell = world.Navigation.IndexOfWorld(entity.Position);

            if (cell < 0 || !terrain.IsPassable(cell, definition.Movement))
            {
                continue;
            }

            int pressure = UnitCatalog.GroundPressure(entity.Faction, entity.Kind);
            int amount = (pressure * ChurnPerTickPermille) / 1_000;

            // A mover always marks the ground a little, however light it is.
            terrain.AddChurn(cell, Math.Max(1, amount));
        }

        if (world.Tick % DecayInterval == 0 && terrain.HasChurn)
        {
            terrain.DecayChurn();
        }
    }
}
