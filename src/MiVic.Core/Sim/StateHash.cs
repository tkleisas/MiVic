using MiVic.Core.Campaign;

namespace MiVic.Core.Sim;

/// <summary>
/// FNV-1a 64-bit hash of the entire simulation state.
/// <para>
/// This is the backbone of determinism testing: run the same seed and command
/// log twice and the hashes must match, and run them on two machines and the
/// hashes must still match. Every field that can influence future ticks must be
/// folded in here — if a field is missing, desyncs become invisible.
/// </para>
/// </summary>
public static class StateHash
{
    internal const ulong OffsetBasis = 14695981039346656037UL;
    internal const ulong Prime = 1099511628211UL;

    /// <summary>Computes the state hash of a world.</summary>
    public static ulong Compute(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        ulong hash = OffsetBasis;
        Mix(ref hash, world.Tick);
        Mix(ref hash, world.AliveCount);
        Mix(ref hash, world.Rng.State);
        Mix(ref hash, world.PendingCommandCount);
        Mix(ref hash, (byte)world.Outcome);

        // Terrain used to be a pure function of the seed and therefore needed no
        // hashing. Weather control can now write to it, so the surface is state.
        ReadOnlySpan<byte> terrain = world.TerrainTypes.RawTypes;

        for (int i = 0; i < terrain.Length; i += 4)
        {
            int chunk = terrain[i];

            if (i + 1 < terrain.Length)
            {
                chunk |= terrain[i + 1] << 8;
            }

            if (i + 2 < terrain.Length)
            {
                chunk |= terrain[i + 2] << 16;
            }

            if (i + 3 < terrain.Length)
            {
                chunk |= terrain[i + 3] << 24;
            }

            Mix(ref hash, chunk);
        }

        // Ground wear is state too: two matches that diverged only in how churned
        // the mud is would otherwise hash the same.
        ReadOnlySpan<byte> churn = world.TerrainTypes.RawChurn;

        for (int i = 0; i < churn.Length; i += 4)
        {
            int chunk = churn[i];

            if (i + 1 < churn.Length)
            {
                chunk |= churn[i + 1] << 8;
            }

            if (i + 2 < churn.Length)
            {
                chunk |= churn[i + 2] << 16;
            }

            if (i + 3 < churn.Length)
            {
                chunk |= churn[i + 3] << 24;
            }

            Mix(ref hash, chunk);
        }

        // Ground attributes are state for the same reason the two above are, and more of
        // it: canopy density and moisture are what the fire, crushing and regrowth steps
        // write. They are a pure function of the seed today, so hashing them changes
        // nothing yet — which is exactly why they go in now, while leaving them out is
        // still a decision rather than an oversight.
        ReadOnlySpan<uint> attributes = world.TerrainTypes.RawAttributes;

        for (int i = 0; i < attributes.Length; i++)
        {
            Mix(ref hash, (long)attributes[i]);
        }

        // The mission's identity and objective progress decide the outcome, so
        // they are as much part of the state as any unit's position.
        Mix(ref hash, world.Mission?.Id);
        ReadOnlySpan<ObjectiveState> objectives = world.Objectives;

        for (int i = 0; i < objectives.Length; i++)
        {
            ref readonly ObjectiveState objective = ref objectives[i];
            Mix(ref hash, (byte)objective.Status);
            Mix(ref hash, objective.Progress);
            Mix(ref hash, objective.HoldProgress);
        }

        int capacity = world.Capacity;
        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity e = ref world.GetRefBySlot(slot);

            Mix(ref hash, slot);
            Mix(ref hash, e.Generation);
            Mix(ref hash, (byte)e.Faction);
            Mix(ref hash, e.TeamId);
            Mix(ref hash, (byte)e.Kind);
            Mix(ref hash, e.Position.X);
            Mix(ref hash, e.Position.Y);
            Mix(ref hash, e.Position.Z);
            Mix(ref hash, e.MoveGoal.X);
            Mix(ref hash, e.MoveGoal.Y);
            Mix(ref hash, e.MoveGoal.Z);
            Mix(ref hash, e.HasMoveGoal ? 1 : 0);
            Mix(ref hash, e.SpeedMmPerTick.Raw);
            Mix(ref hash, e.Morale.Raw);
            Mix(ref hash, e.Health);
            Mix(ref hash, e.Heading);
            Mix(ref hash, e.AltitudeMm);
            Mix(ref hash, e.PathLength);
            Mix(ref hash, e.PathCursor);
            Mix(ref hash, e.QueueLength);
            Mix(ref hash, e.TargetSlot);
            Mix(ref hash, e.AttackCooldown);
            Mix(ref hash, e.HasAttackOrder ? 1 : 0);
            Mix(ref hash, e.Routed ? 1 : 0);
            Mix(ref hash, e.RevealedUntilTick);
            Mix(ref hash, e.DistanceTravelledMm);
            Mix(ref hash, e.ConstructionTicksRemaining);
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            Mix(ref hash, state.Materials);
            Mix(ref hash, state.Energy);
            Mix(ref hash, state.Water);
            Mix(ref hash, state.TechTier);
            Mix(ref hash, state.ResearchTicksRemaining);
            Mix(ref hash, state.ResearchTargetTier);
            Mix(ref hash, state.RecentCasualties);
            Mix(ref hash, (long)state.LicenceMask);
            Mix(ref hash, (long)state.TechMask);
            Mix(ref hash, (int)state.ResearchingTech);
            Mix(ref hash, state.DamagePermille);
            Mix(ref hash, state.ArmorPermille);
            Mix(ref hash, state.SpeedPermille);
            Mix(ref hash, state.ProductionPermille);
            Mix(ref hash, state.VisionPermille);
            Mix(ref hash, state.TerrainResistancePermille);
            Mix(ref hash, state.BonusSlots);
            Mix(ref hash, state.CohesionPerFriendRaw);
            Mix(ref hash, state.PropagandaPaid ? 1 : 0);
            Mix(ref hash, state.WagesPaid ? 1 : 0);

            if (state.AbilityReadyTick is not null)
            {
                for (int i = 0; i < state.AbilityReadyTick.Length; i++)
                {
                    Mix(ref hash, state.AbilityReadyTick[i]);
                }
            }
            Mix(ref hash, state.MoraleBonusRaw);
            Mix(ref hash, state.StructuresLost);
            Mix(ref hash, (long)state.ApprovedMask);
            Mix(ref hash, (int)state.PrototypeKind);
            Mix(ref hash, state.PrototypeTicksRemaining);
        }

        return hash;
    }

    /// <summary>
    /// Folds a string into the hash with a stable algorithm.
    /// <para>
    /// <see cref="string.GetHashCode()"/> is randomised per process, so using it
    /// here would make the state hash differ between runs of the same build —
    /// exactly the bug this whole file exists to prevent.
    /// </para>
    /// </summary>
    public static void Mix(ref ulong hash, string? value)
    {
        if (value is null)
        {
            Mix(ref hash, -1);
            return;
        }

        Mix(ref hash, value.Length);

        foreach (char character in value)
        {
            Mix(ref hash, (byte)character);
            Mix(ref hash, (byte)(character >> 8));
        }
    }

    /// <summary>Folds a 64-bit value into the hash.</summary>
    public static void Mix(ref ulong hash, long value)
    {
        unchecked
        {
            ulong v = (ulong)value;
            for (int i = 0; i < 8; i++)
            {
                hash ^= (byte)(v >> (i * 8));
                hash *= Prime;
            }
        }
    }

    /// <summary>Folds a 64-bit unsigned value into the hash.</summary>
    public static void Mix(ref ulong hash, ulong value) => Mix(ref hash, unchecked((long)value));

    /// <summary>Folds a 32-bit value into the hash.</summary>
    public static void Mix(ref ulong hash, int value) => Mix(ref hash, (long)value);

    /// <summary>Folds a byte into the hash.</summary>
    public static void Mix(ref ulong hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= Prime;
        }
    }
}
