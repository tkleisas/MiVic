using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// Evaluates a mission's objectives and decides the mission's outcome.
/// <para>
/// Every objective is a predicate over world state, checked on a fixed interval,
/// so the campaign stays inside the determinism contract: no callbacks, no wall
/// clock, no hidden state. When a mission is attached it replaces
/// <see cref="VictorySystem"/> — the last team standing is no longer the point,
/// the objectives are.
/// </para>
/// </summary>
public static class MissionSystem
{
    /// <summary>Ticks between evaluations. Twice a second is fine-grained enough
    /// for "hold this ground" and cheap enough for 500 entities.</summary>
    public const int CheckInterval = 10;

    /// <summary>Evaluates the mission if one is attached and this tick is due.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        MissionDefinition? mission = world.Mission;

        if (mission is null || world.Outcome != GameOutcome.Ongoing || world.Tick % CheckInterval != 0)
        {
            return;
        }

        Span<ObjectiveState> states = world.ObjectiveStatesSpan;
        bool primaryFailed = false;
        bool primaryPending = false;

        for (int i = 0; i < mission.Objectives.Count; i++)
        {
            ref ObjectiveState state = ref states[i];
            ObjectiveDefinition definition = mission.Objectives[i];

            if (state.IsPending)
            {
                Evaluate(world, definition, ref state);
            }

            if (!definition.IsPrimary)
            {
                continue;
            }

            if (state.IsFailed)
            {
                primaryFailed = true;
            }
            else if (state.IsPending && !definition.Constraint)
            {
                // A constraint only ever fails; it does not have to be satisfied
                // for the mission to be won.
                primaryPending = true;
            }
        }

        if (primaryFailed)
        {
            world.SetOutcome(GameOutcome.WesternVictory);
            return;
        }

        if (!primaryPending)
        {
            world.SetOutcome(GameOutcome.AllianceVictory);
            return;
        }

        if (mission.TimeLimitTicks > 0 && world.Tick >= mission.TimeLimitTicks)
        {
            world.SetOutcome(GameOutcome.WesternVictory);
        }
    }

    /// <summary>Advances one objective. Split out so tests can drive it directly.</summary>
    public static void Evaluate(SimWorld world, ObjectiveDefinition definition, ref ObjectiveState state)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(definition);

        state.LastEvaluatedTick = (int)world.Tick;

        bool satisfied = EvaluateKind(world, definition, ref state);

        // The condition itself can fail an objective outright: a wiped-out army
        // or a destroyed command centre can never come back.
        if (state.IsFailed)
        {
            return;
        }

        if (satisfied)
        {
            state.Status = ObjectiveStatus.Complete;
            return;
        }

        // Past the deadline only a survival objective could still be satisfied,
        // and that was checked above, so anything still open has run out of time.
        if (definition.DeadlineTick > 0 && world.Tick >= definition.DeadlineTick)
        {
            state.Status = ObjectiveStatus.Failed;
        }
    }

    /// <summary>
    /// Checks the objective's own condition. Returns true when it is satisfied,
    /// and may set <see cref="ObjectiveState.Status"/> to
    /// <see cref="ObjectiveStatus.Failed"/> when the condition can no longer be met.
    /// </summary>
    private static bool EvaluateKind(SimWorld world, ObjectiveDefinition definition, ref ObjectiveState state)
    {
        switch (definition.Kind)
        {
            case ObjectiveKind.DestroyStructures:
            {
                int destroyed = world.TeamRef(definition.TargetTeam).StructuresLost;
                state.Progress = destroyed;
                return destroyed >= definition.TargetCount;
            }

            case ObjectiveKind.HoldArea:
            {
                int held = CountUnitsInArea(world, definition);
                state.Progress = held;

                if (held >= definition.TargetCount)
                {
                    state.HoldProgress += CheckInterval;

                    if (state.HoldProgress >= definition.HoldTicks)
                    {
                        return true;
                    }
                }
                else
                {
                    // Losing the ground resets the clock: the objective is about
                    // control, not about having visited once.
                    state.HoldProgress = 0;
                }

                return false;
            }

            case ObjectiveKind.SurviveTicks:
            {
                if (!VictorySystem.HasStructures(world, definition.Team))
                {
                    state.Status = ObjectiveStatus.Failed;
                    return false;
                }

                state.Progress = (int)world.Tick;
                return definition.DeadlineTick > 0 && world.Tick >= definition.DeadlineTick;
            }

            case ObjectiveKind.ProtectCommandCentre:
            {
                if (!HasCommandCentre(world, definition.Team))
                {
                    state.Status = ObjectiveStatus.Failed;
                    return false;
                }

                return definition.DeadlineTick > 0 && world.Tick >= definition.DeadlineTick;
            }

            case ObjectiveKind.AccumulateMaterials:
            {
                int materials = world.TeamRef(definition.Team).Materials;
                state.Progress = materials;
                return materials >= definition.MaterialsTarget;
            }

            case ObjectiveKind.ReachTechTier:
            {
                int tier = world.TeamRef(definition.Team).TechTier;
                state.Progress = tier;
                return tier >= definition.TierTarget;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(definition), definition.Kind, "Unknown objective kind.");
        }
    }

    /// <summary>Number of a team's units inside the objective's circle.</summary>
    private static int CountUnitsInArea(SimWorld world, ObjectiveDefinition definition)
    {
        int count = 0;
        long radius = definition.RadiusMm;
        long radiusSquared = radius * radius;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != definition.Team || UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            long dx = entity.Position.X - definition.CentreX;
            long dz = entity.Position.Z - definition.CentreZ;

            if ((dx * dx) + (dz * dz) <= radiusSquared)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>True when a team still owns a command centre.</summary>
    public static bool HasCommandCentre(SimWorld world, int team)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == UnitKind.CommandCentre)
            {
                return true;
            }
        }

        return false;
    }
}
