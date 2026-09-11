using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// What the world a mission <em>opens</em> in has already decided about one objective.
/// <para>
/// It is the answer to a question the mission validator asks of every objective before the first
/// tick is run, and it exists because an objective is a win condition rather than a scene: one that
/// the map has already decided is not a slow objective, it is a mission that is over. See
/// <see cref="MissionSystem.Verdict"/> for the question and
/// <see cref="TriggerSystem.Validate"/> for what is done about each answer.
/// </para>
/// </summary>
public enum OpeningVerdict : byte
{
    /// <summary>The objective is the player's to do or to fail, which is what an objective is for.</summary>
    Undecided = 0,

    /// <summary>Already complete before the first tick: it completes with nothing done.</summary>
    Satisfied = 1,

    /// <summary>Already failed before the first tick: the mission is lost however it is played.</summary>
    Failed = 2,
}

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
            world.SetOutcome(GameOutcome.Defeat);
            return;
        }

        if (!primaryPending)
        {
            world.SetOutcome(GameOutcome.Victory);
            return;
        }

        if (mission.TimeLimitTicks > 0 && world.Tick >= mission.TimeLimitTicks)
        {
            world.SetOutcome(GameOutcome.Defeat);
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
    /// <b>What a world already says about one objective, asked without ticking it.</b>
    /// <para>
    /// Asked of the world a mission <em>opens</em> in — the scenario laid out and nothing run — the
    /// answer is whether the objective has been decided before the player has done anything: an
    /// objective already satisfied completes with nothing done, and one already failed is a mission
    /// that cannot be won. Asked of a world that has been played, it is simply what that world says
    /// now, because the question and the tick loop's question are the same question.
    /// </para>
    /// <para>
    /// <b>It is the tick loop's own evaluation and not a second reading of an objective.</b> The
    /// mission validator asks through here so that it cannot disagree with the game about what an
    /// objective means — a validator that answered differently from the simulation would be worse
    /// than no validator at all, since its complaint would be about a rule nobody plays by.
    /// </para>
    /// <para>
    /// A hold is the one kind whose decision is not a status: the loop advances the hold clock on
    /// every evaluation the opening formation already meets, so a clock that has moved at all is a
    /// hold the world has already granted — it completes on time with nothing done. Nothing else
    /// can move that field, which is why reading it here is the same evaluation rather than a
    /// guess about one.
    /// </para>
    /// </summary>
    public static OpeningVerdict Verdict(SimWorld world, ObjectiveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(definition);

        // A fresh state, so nothing the objective remembers from a mission already under way can
        // colour the answer: what is being asked is what this world says about an objective nobody
        // has looked at yet.
        var state = default(ObjectiveState);

        Evaluate(world, definition, ref state);

        if (state.IsFailed)
        {
            return OpeningVerdict.Failed;
        }

        if (state.IsComplete || (definition.Kind == ObjectiveKind.HoldArea && state.HoldProgress > 0))
        {
            return OpeningVerdict.Satisfied;
        }

        return OpeningVerdict.Undecided;
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
                int held = CountUnitsInArea(world, definition.Team, definition);
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

            case ObjectiveKind.DenyArea:
            {
                // The mirrored half of the objective vocabulary: not "get your units in" but
                // "the enemy must not get theirs in". It counts the team being denied, and it
                // fails the instant they succeed rather than after a hold clock, because the
                // thing being denied is an arrival — the units that got through got through,
                // and no amount of clearing the ground afterwards undoes it.
                int intruders = CountUnitsInArea(world, definition.TargetTeam, definition);

                // Progress is the high-water mark of the intrusion, which is the number a
                // player watching the panel wants: "2/4 of them are through" is a warning,
                // and it must not fall back to zero when they leave.
                if (intruders > state.Progress)
                {
                    state.Progress = intruders;
                }

                if (intruders >= definition.TargetCount)
                {
                    state.Status = ObjectiveStatus.Failed;
                    return false;
                }

                // Denial is decided by the clock: the deadline arriving with the area still
                // clear is the fact that the enemy never made it. A denial with no deadline
                // therefore has nothing that could ever satisfy it, and the validation test
                // refuses to ship one.
                return definition.DeadlineTick > 0 && world.Tick >= definition.DeadlineTick;
            }

            case ObjectiveKind.Scripted:
            {
                // Judged by the mission and not by the world: only a trigger action brings this
                // to complete. Returning false here is not a stub — it is the whole of the
                // rule, and the deadline below still applies to it, so a scripted objective
                // that the mission never gets round to can still run out of time.
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

    /// <summary>
    /// Number of a team's units inside the objective's circle.
    /// <para>
    /// The team is a parameter rather than the objective's own <see cref="ObjectiveDefinition.Team"/>,
    /// because the two area objectives ask it of two different sides: holding counts your own
    /// units, denial counts the enemy's. The count itself is
    /// <see cref="SimWorld.CountUnitsInArea"/> — the same walk the trigger layer asks, so that
    /// "how many of the enemy are standing on the objective" has one answer in the engine.
    /// </para>
    /// </summary>
    private static int CountUnitsInArea(SimWorld world, int team, ObjectiveDefinition definition)
        => world.CountUnitsInArea(team, definition.CentreX, definition.CentreZ, definition.RadiusMm);

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
