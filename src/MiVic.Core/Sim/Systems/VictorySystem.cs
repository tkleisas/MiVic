namespace MiVic.Core.Sim;

/// <summary>How the battle ended.</summary>
public enum GameOutcome : byte
{
    /// <summary>Still fighting.</summary>
    Ongoing = 0,

    /// <summary>Σοβιετικοί and Κινέζοι have destroyed the Δυτικοί war machine.</summary>
    AllianceVictory = 1,

    /// <summary>The Δυτικοί have broken the socialist alliance.</summary>
    WesternVictory = 2,

    /// <summary>Both sides were wiped out.</summary>
    Draw = 3,
}

/// <summary>
/// Decides when the battle is over.
/// <para>
/// A side is defeated when it has no structures left. Losing a headquarters is
/// not the end — a player with a factory can still rebuild — so the check is on
/// every building, not just the command centre. The outcome is part of the
/// simulation state, which means a replay reaches the same verdict on the same
/// tick.
/// </para>
/// </summary>
public static class VictorySystem
{
    /// <summary>Ticks between checks. Once a second is plenty.</summary>
    public const int CheckInterval = 20;

    /// <summary>Runs one victory check if this tick is due.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Outcome != GameOutcome.Ongoing || world.Tick % CheckInterval != 0)
        {
            return;
        }

        bool allianceAlive = HasStructures(world, 0) || HasStructures(world, 1);
        bool westernAlive = HasStructures(world, 2);

        world.SetOutcome((allianceAlive, westernAlive) switch
        {
            (true, false) => GameOutcome.AllianceVictory,
            (false, true) => GameOutcome.WesternVictory,
            (false, false) => GameOutcome.Draw,
            _ => GameOutcome.Ongoing,
        });
    }

    /// <summary>True when a team still owns at least one structure.</summary>
    public static bool HasStructures(SimWorld world, int team)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Number of structures a team still owns.</summary>
    public static int CountStructures(SimWorld world, int team)
    {
        int count = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                count++;
            }
        }

        return count;
    }
}
