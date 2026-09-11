using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// <b>The mission's script: one condition and its actions, evaluated on the tick, in list
/// order.</b>
/// <para>
/// A mission used to be a seed, a layout, a time limit and a list of objectives, which is enough
/// for "destroy this" and nothing else: it could not say <em>when</em> anything happened, and a
/// campaign is made of when. This is that layer, and it is a list of declarative triggers rather
/// than a scripting language for the reason the whole campaign is data — a replay is a seed, a
/// mission id and a command log, so anything a mission does has to be reproducible from those
/// three, and a language would be a project of its own that bought nothing this list does not.
/// </para>
/// <para>
/// <b>Where it runs, and why there.</b> It is ticked from <see cref="SimWorld.Step"/> at the end
/// of the tick, before <see cref="MissionSystem"/> and after everything that moves, fights or
/// builds. Two consequences, and both are deliberate: a trigger that spawns a force or gives an
/// order does so into a finished tick, so what it creates is first <em>seen</em> by the movement,
/// combat and economy systems on the next tick, exactly as any other spawn is; and a trigger that
/// completes an objective is followed immediately by the objective check on the same tick, so the
/// mission can be won by the scripted event rather than a tenth of a second later.
/// </para>
/// <para>
/// <b>The order of the list is the order of the events.</b> Each trigger is asked in turn and, if
/// it fires, its actions are carried out before the next one is asked. That is what makes this an
/// order rather than a set: an ambush can raise a flag that the next trigger reads on the same
/// tick, which is how a sequence of things that happen together is written as a list of things
/// that happen in order.
/// </para>
/// <para>
/// <b>Every trigger fires at most once, and the memory of having fired is simulation state.</b>
/// See <see cref="TriggerState"/> and <see cref="StateHash"/>, where it is hashed: two peers that
/// disagreed about which triggers had fired would be playing two different missions from that
/// tick on, one of them springing an ambush the other had already sprung. A condition that is
/// still true next tick does nothing at all, which is the difference between "when the enemy
/// reaches the ridge" and "every tick the enemy is on the ridge".
/// </para>
/// </summary>
public static class TriggerSystem
{
    /// <summary>Millimetres between two spawned units in a scripted formation.</summary>
    private const int SpawnSpacingMm = 7_000;

    /// <summary>Units per rank in a scripted formation.</summary>
    private const int SpawnColumns = 3;

    /// <summary>
    /// Evaluates the mission's triggers, if it has any, on this tick.
    /// <para>
    /// Every tick rather than on <see cref="MissionSystem.CheckInterval"/>'s tenth: an ambush is a
    /// thing that happens when a column crosses a line, and a condition asked five times a second
    /// would let it cross and leave again in between. There is nothing expensive here — a handful
    /// of integer comparisons against counts the world already keeps — so the fixed order is
    /// cheap enough to run at the tick rate, which is the only way a trigger can be said to fire
    /// <em>when</em> something happened.
    /// </para>
    /// </summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        MissionDefinition? mission = world.Mission;

        if (mission is null || mission.Triggers.Count == 0)
        {
            return;
        }

        // A decided mission has nothing left to script. The banner is up, and a message or a
        // reinforcement arriving after the player has already won would be the mission talking
        // about a match that is over.
        if (world.Outcome != GameOutcome.Ongoing)
        {
            return;
        }

        Span<TriggerState> states = world.TriggerStatesSpan;

        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            if (states[i].HasFired)
            {
                continue;
            }

            TriggerDefinition trigger = mission.Triggers[i];

            if (!Satisfied(world, trigger.Condition))
            {
                continue;
            }

            // The memory is written before the actions run, so a trigger whose own action makes
            // its condition true again — a spawn into the area it watches, say — is still a
            // trigger that fired once. This is the whole of what "fires once" means.
            states[i].FiredTick = world.Tick;
            Apply(world, trigger);
        }

        StampReveals(world, mission);
    }

    /// <summary>
    /// True when the condition holds for this world. Public because it is the question the tests ask
    /// directly — "is this trigger's condition true of this world" — rather than through a match.
    /// </summary>
    public static bool Satisfied(SimWorld world, in TriggerCondition condition)
    {
        ArgumentNullException.ThrowIfNull(world);

        switch (condition.Kind)
        {
            case TriggerConditionKind.TimeElapsed:
                return world.Tick >= condition.Tick;

            case TriggerConditionKind.UnitInArea:
                return world.CountUnitsInArea(
                    condition.Team, condition.CentreX, condition.CentreZ, condition.RadiusMm) >= condition.Count;

            case TriggerConditionKind.StructuresLost:
                // The ledger the DestroyStructures objective reads, and read the same way: it
                // counts losses rather than comparing against a starting total, so a structure
                // the enemy rebuilds does not undo the progress. It is also the past-tense half
                // of the pair, and starting at zero is what keeps it off the opening tick.
                return (uint)condition.Team < SimConstants.TeamCount &&
                       world.TeamRef(condition.Team).StructuresLost >= condition.Count;

            case TriggerConditionKind.StructuresStandingBelow:
                // The opposite kind of number: derived, not remembered. A team the match does not
                // declare stands in no structures at all, so the guard is what stops "fewer than
                // three of their buildings stand" from being true of a team that is not playing.
                return (uint)condition.Team < SimConstants.TeamCount &&
                       world.CountStructures(condition.Team, condition.Role) < condition.Count;

            case TriggerConditionKind.FlagSet:
                return world.IsMissionFlagSet(condition.Flag);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(condition), condition.Kind, "Unknown trigger condition.");
        }
    }

    /// <summary>Carries out a trigger's actions, in order.</summary>
    private static void Apply(SimWorld world, in TriggerDefinition trigger)
    {
        for (int i = 0; i < trigger.Actions.Count; i++)
        {
            Apply(world, trigger.Actions[i]);
        }
    }

    /// <summary>Carries out one action.</summary>
    private static void Apply(SimWorld world, in TriggerAction action)
    {
        switch (action.Kind)
        {
            case TriggerActionKind.Message:
                world.RaiseMissionMessage(action.GreekText);
                break;

            case TriggerActionKind.SetFlag:
                world.SetMissionFlag(action.Flag);
                break;

            case TriggerActionKind.Spawn:
                Spawn(world, action);
                break;

            case TriggerActionKind.Reveal:
                // Nothing to do here, and that is not an oversight: the reveal is not written
                // once, it is *kept* — the disc is stamped on every tick the reveal is running, in
                // StampReveals below, so that it lapses when its time is up without the world
                // having to remember a second thing about it.
                break;

            case TriggerActionKind.AdjustResources:
                AdjustResources(world, action);
                break;

            case TriggerActionKind.OrderGroup:
                OrderGroup(world, action);
                break;

            case TriggerActionKind.CompleteObjective:
                world.CompleteObjective(action.Objective);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Unknown trigger action.");
        }
    }

    /// <summary>
    /// Puts a scripted force on the map.
    /// <para>
    /// <b>A scripted spawn is not production.</b> It pays nothing, it asks no licence and no tier,
    /// and it is not refused for want of command capacity — a mission is telling the story of a
    /// battle, and an ambush that could not happen because the ambushing side was over its supply
    /// ceiling would be a mission that silently does nothing, which is the failure this project
    /// has been bitten by three times. What it cannot do is conjure a force for a team the match
    /// does not declare: an undeclared team is nobody's enemy and nobody's ally (see
    /// <see cref="MatchRoster"/>), so a mission with a stray team number in it drops the spawn
    /// rather than putting a third faction on the map.
    /// </para>
    /// <para>
    /// The pattern is a fixed grid with no jitter, and no draw from the world's generator: the
    /// generator's stream is part of what a replay reproduces, and an ambush that consumed a
    /// random number would make the layout of everything generated after it depend on when the
    /// player happened to walk into it. The ground rule is the world's own — a unit or a building
    /// that would appear in a lake is moved to the nearest solid ground — because a gun inside a
    /// lake is not a gun.
    /// </para>
    /// </summary>
    private static void Spawn(SimWorld world, in TriggerAction action)
    {
        if (!world.IsTeamInPlay(action.Team) || !UnitCatalog.TryGet(action.Role, out UnitDefinition definition))
        {
            return;
        }

        Faction faction = world.FactionOfTeam(action.Team);

        for (int i = 0; i < action.Count; i++)
        {
            if (world.AliveCount >= world.Capacity)
            {
                // The world has no room. Dropping the rest of the force is the only behaviour
                // that neither throws inside a tick nor overwrites something that is alive.
                return;
            }

            int column = i % SpawnColumns;
            int row = i / SpawnColumns;

            // The half-step is written as a half rather than as a fraction, the way the
            // scenario's own ranks are: an even column count puts the centre of the rank between
            // two units, and multiplying by two before dividing by two keeps that exact.
            int offsetX = (((2 * column) - (SpawnColumns - 1)) * SpawnSpacingMm) / 2;
            int offsetZ = row * SpawnSpacingMm;

            var wanted = new WorldPos(action.CentreX + offsetX, 0, action.CentreZ + offsetZ);

            // Aircraft are the exception and the reason this is not simply a call to
            // LegalSpawnSite: they fly, so water under them is nothing.
            WorldPos place = UnitCatalog.Flies(action.Role) ? wanted : world.LegalSpawnSite(wanted);

            world.Spawn(faction, action.Team, action.Role, place, Fix32.FromInt(definition.SpeedMmPerTick), definition.Health);
        }
    }

    /// <summary>
    /// Grants or removes stockpiles.
    /// <para>
    /// One action with three signed amounts rather than a grant and a removal: the arithmetic and
    /// the floor are the same either way, and the sign is the whole of the difference. A
    /// stockpile floors at zero, because a negative one is a debt nothing in the economy can
    /// express — the production gate would compare against it and refuse everything, which reads
    /// as a broken team rather than as a rule.
    /// </para>
    /// </summary>
    private static void AdjustResources(SimWorld world, in TriggerAction action)
    {
        if ((uint)action.Team >= SimConstants.TeamCount)
        {
            return;
        }

        ref TeamState team = ref world.TeamRef(action.Team);

        team.Materials = Clamp(team.Materials, action.Materials);
        team.Energy = Clamp(team.Energy, action.Energy);
        team.Water = Clamp(team.Water, action.Water);

        // Long arithmetic, so that a mission asking for an absurd amount saturates rather than
        // wrapping into a negative stockpile it would then be clamped away from.
        static int Clamp(int stockpile, int delta)
            => (int)Math.Clamp((long)stockpile + delta, 0L, int.MaxValue);
    }

    /// <summary>
    /// Gives every unit of a team inside the circle an order.
    /// <para>
    /// An attack names no entity, because a mission is data compiled before any entity exists:
    /// what it can name is a <em>place</em>, and the target is the nearest hostile thing to it.
    /// Ties go to the lowest slot, so the choice is a fact about the world rather than about the
    /// order a machine happened to visit it in. Sight is not consulted — an ambush is an order to
    /// go and engage, and the unit's own sensors decide what it may fire at when it gets there,
    /// which is the same division of labour a player's attack order has.
    /// </para>
    /// <para>
    /// The orders go through <see cref="SimWorld.Enqueue"/>, the command queue a click writes to
    /// and a replay records, rather than being applied to the entity directly. They execute on
    /// the next tick like every other order, and they are not recorded, because they are
    /// re-derived from the fired state on replay — the same reason the AI's orders are not.
    /// </para>
    /// </summary>
    private static void OrderGroup(SimWorld world, in TriggerAction action)
    {
        if (!world.IsTeamInPlay(action.Team))
        {
            return;
        }

        bool attack = action.Order == GroupOrder.Attack;
        EntityId victim = EntityId.None;

        if (attack && !TryFindNearestHostile(world, action.Team, action.TargetX, action.TargetZ, out victim))
        {
            // An attack order with nothing to attack is not an order. The group stands where it
            // is rather than marching at the point the mission named.
            return;
        }

        // One buffer per order rather than a scratch array pinned on the world: a scripted group
        // order happens a handful of times in a match, and a capacity-sized array that every
        // world carried for the rest of its life would be the worse trade.
        int[] group = new int[world.Capacity];
        int count = world.UnitsInAreaInto(
            action.Team, action.CentreX, action.CentreZ, action.RadiusMm, group);

        for (int i = 0; i < count; i++)
        {
            int slot = group[i];
            ref Entity unit = ref world.GetRefBySlot(slot);
            var id = new EntityId(slot, unit.Generation);

            if (attack)
            {
                world.Enqueue(SimCommand.Attack(id, victim, world.Tick + 1, action.Team));
                continue;
            }

            // A flyer's goal carries its own altitude: the move command pins a ground unit to the
            // terrain surface but leaves a wing at the height it was ordered to, so a destination
            // built with the terrain's height alone would bring it down to the ground.
            int goalY = UnitCatalog.Flies(unit.Kind)
                ? world.Terrain.SampleHeightMm(action.TargetX, action.TargetZ) + unit.AltitudeMm
                : 0;

            world.Enqueue(
                SimCommand.Move(id, new WorldPos(action.TargetX, goalY, action.TargetZ), world.Tick + 1, action.Team));
        }
    }

    /// <summary>
    /// The nearest entity hostile to <paramref name="team"/> to a point, by ascending slot order,
    /// ties to the lower slot.
    /// </summary>
    private static bool TryFindNearestHostile(SimWorld world, int team, int x, int z, out EntityId target)
    {
        long nearest = long.MaxValue;
        int found = -1;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (!world.IsHostile(team, entity.TeamId))
            {
                continue;
            }

            long dx = entity.Position.X - x;
            long dz = entity.Position.Z - z;
            long distance = (dx * dx) + (dz * dz);

            if (distance < nearest)
            {
                nearest = distance;
                found = slot;
            }
        }

        if (found < 0)
        {
            target = EntityId.None;
            return false;
        }

        target = new EntityId(found, world.GetRefBySlot(found).Generation);
        return true;
    }

    /// <summary>
    /// <b>Everything wrong with a mission that can be seen without running it, as a list of
    /// complaints; empty means the mission is at least self-consistent.</b>
    /// <para>
    /// It exists for one failure above all others: <em>something a mission is made of that is
    /// authored and can never happen</em>. This project has shipped a volcano line above every
    /// cell, a sand band below the mud line and an entire mud mechanic that was inert, and a
    /// mission whose second act never happens is that same bug wearing a script. What can be
    /// checked statically is checked here — of the script, a flag nothing raises, an objective
    /// nothing completes, a denial with no clock to be decided by, a completion pointing at an
    /// objective that does not exist, a condition counting on a team that is not in the match; and
    /// of the objectives, a scripted objective no trigger completes, a denial with no deadline, a
    /// survival or command-centre objective that is not a constraint and has no deadline either,
    /// and a deadline later than the mission's own time limit — the kinds that can never be
    /// satisfied by play.
    /// </para>
    /// <para>
    /// <b>And the failure from the other end, which is the same bug facing the other way:
    /// something that is decided before the player has touched it.</b> A condition that is already
    /// true of the world the mission opens in fires on the first tick whatever the player does,
    /// which is a message about the first gun falling arriving before the first shot — see
    /// <see cref="CheckTheOpeningWorld"/>. A mission that means it says so with
    /// <see cref="TriggerDefinition.DependsOnOpeningWorld"/>, which is what makes that a check with
    /// an opt-out rather than a refusal.
    /// </para>
    /// <para>
    /// <b>The objectives are asked the same question of the same world, and they are not the same
    /// case.</b> A trigger that fires early is a scene in the wrong place; an objective the world
    /// has already decided is the mission itself — and if it is decided <em>against</em> the player
    /// the mission cannot be won at all. See <see cref="CheckTheOpeningObjectives"/>, which reports
    /// the two answers separately and acknowledges neither.
    /// </para>
    /// <para>
    /// <b>And so is the victory rule, which is the third layer that reads a world and was never
    /// asked about the one it opens in.</b> It has been safe by construction rather than by being
    /// asked — the scenario lays a base down for every team a match declares, so no declared side
    /// could stand in nothing — and a mission staged around a non-player force is precisely the
    /// mission that breaks that construction. See <see cref="CheckTheOpeningSides"/>, which reports a
    /// declared side standing in nothing and passes over one the match declared as a side it does not
    /// judge. Beside those, two facts about an objective's own clock need no world at all and are
    /// asked in <see cref="CheckObjectiveClock"/>: an objective that nothing could ever complete, and
    /// one that asks for more time than the mission has.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Validate(MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        List<string> problems = [];
        var ids = new List<string>(mission.Triggers.Count);
        var completedObjectives = new List<int>();

        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            TriggerDefinition trigger = mission.Triggers[i];
            string where = $"trigger {i} '{trigger.Id}'";

            if (string.IsNullOrWhiteSpace(trigger.Id))
            {
                problems.Add($"trigger {i} has no id: a trigger is named by the test that counts its fires and by the transcript that shows it firing.");
            }
            else if (ids.Contains(trigger.Id))
            {
                problems.Add($"{where}: the id is used twice.");
            }
            else
            {
                ids.Add(trigger.Id);
            }

            if (trigger.Actions.Count == 0)
            {
                problems.Add($"{where} has no actions: it can only ever be a condition nobody reads.");
            }

            CheckCondition(mission, trigger, where, problems);

            for (int a = 0; a < trigger.Actions.Count; a++)
            {
                TriggerAction action = trigger.Actions[a];
                string at = $"{where}, action {a} ({action.Kind})";

                // Only the actions that name a team are asked about it: a message, a flag and a
                // completion do not carry one, and complaining about a field they ignore would be
                // the validator inventing problems rather than finding them.
                bool namesATeam = action.Kind is TriggerActionKind.Spawn
                    or TriggerActionKind.AdjustResources
                    or TriggerActionKind.OrderGroup
                    or TriggerActionKind.Reveal;

                if (namesATeam && !mission.Roster.IsInPlay(action.Team))
                {
                    problems.Add($"{at} names team {action.Team}, which this mission's match does not declare: the action would do nothing.");
                }

                switch (action.Kind)
                {
                    case TriggerActionKind.Spawn:
                        if (action.Count <= 0 || !UnitCatalog.TryGet(action.Role, out _))
                        {
                            problems.Add($"{at} spawns {action.Count} of {action.Role}: nothing would appear.");
                        }

                        break;

                    case TriggerActionKind.CompleteObjective:
                        if ((uint)action.Objective >= (uint)mission.Objectives.Count)
                        {
                            problems.Add($"{at} completes objective {action.Objective}, which this mission does not have.");
                        }
                        else
                        {
                            completedObjectives.Add(action.Objective);
                        }

                        break;

                    case TriggerActionKind.AdjustResources:
                        if (action.Materials == 0 && action.Energy == 0 && action.Water == 0)
                        {
                            problems.Add($"{at} changes no resource: it would do nothing.");
                        }

                        break;

                    case TriggerActionKind.Reveal:
                        if (action.RadiusMm <= 0)
                        {
                            problems.Add($"{at} reveals a disc of radius {action.RadiusMm}: no cell would be lit.");
                        }

                        break;

                    case TriggerActionKind.OrderGroup:
                        if (action.RadiusMm <= 0)
                        {
                            problems.Add($"{at} orders a group of radius {action.RadiusMm}: it would find no units.");
                        }

                        break;
                }
            }
        }

        // A scripted objective is completed by the mission and by nothing else, so a mission that
        // ships one and never completes it has shipped an objective that cannot be satisfied —
        // which loses the mission however well it is played.
        for (int i = 0; i < mission.Objectives.Count; i++)
        {
            ObjectiveDefinition objective = mission.Objectives[i];

            if (objective.Kind == ObjectiveKind.Scripted && !completedObjectives.Contains(i))
            {
                problems.Add(
                    $"objective {i} is scripted and no trigger completes it: it can never be satisfied, " +
                    "so a mission that requires it can never be won.");
            }

            // Denial is decided by the clock — the deadline arriving with the area still clear is
            // the fact that the enemy never arrived — so a denial without one is an objective with
            // nothing that could ever complete it.
            if (objective.Kind == ObjectiveKind.DenyArea && objective.DeadlineTick <= 0)
            {
                problems.Add(
                    $"objective {i} denies an area and has no deadline: nothing could ever complete it, " +
                    "and the mission would be lost by the time limit however well it was fought.");
            }

            // The mirror of the trigger layer's question about the match, and the case two-faction
            // matches made expressible: an objective whose own evaluation reads a team that is not
            // playing is an objective about nothing. Before a match could declare its cast, every
            // mission was fought by the same three teams and this could not be written down wrong.
            int reads = TheTeamItReads(objective);

            if (reads >= 0 && !mission.Roster.IsInPlay(reads))
            {
                problems.Add($"objective {i} {NothingToMeasure(objective, reads)}");
            }

            CheckObjectiveClock(mission, i, objective, problems);
        }

        // The half that needs a world, asked last: what the map the mission opens on has already
        // decided, asked of three layers rather than of one — every condition, every objective, and
        // the rule that decides a match with no objectives at all. One world is built for all three.
        // It is built for every mission rather than only for one with a script or an objective: a
        // match always declares a side, and "does this side stand in anything" is a question about
        // the layout, so the third layer always has something to ask.
        SimWorld opening = OpeningWorld(mission);

        CheckTheOpeningWorld(mission, opening, problems);
        CheckTheOpeningObjectives(mission, opening, problems);
        CheckTheOpeningSides(mission, opening, problems);

        return problems;
    }

    /// <summary>
    /// <b>The two facts about an objective's own clock that no world is needed to see: one that can
    /// never be completed because nothing would complete it, and one that asks for time the mission
    /// does not have.</b>
    /// <para>
    /// The first is the objective half of the failure this whole validator exists for, wearing the
    /// other kind of clock. A <see cref="ObjectiveKind.SurviveTicks"/> or a
    /// <see cref="ObjectiveKind.ProtectCommandCentre"/> is <em>completed by reaching its deadline</em>
    /// and by nothing else — that is what "reach tick N with your base still standing" means — so one
    /// with no deadline cannot be satisfied at all: a mission that requires it can only lose, and one
    /// that does not still reads as an objective the player can never finish. A
    /// <see cref="ObjectiveDefinition.Constraint"/> is the other case and stays exempt: "your command
    /// centre must survive" is never something the mission is won by, so it needs no clock to be
    /// completed by and m3 ships exactly that shape.
    /// </para>
    /// <para>
    /// The second is the same accident measured against the mission rather than against the
    /// objective: the mission is decided at its time limit, so a deadline past that limit is a moment
    /// the match never reaches. For the kinds the clock decides — this is the third of them, since a
    /// <see cref="ObjectiveKind.DenyArea"/> is completed by its deadline too — the objective can
    /// therefore never be satisfied and the mission can never be won; for the rest the extra time is
    /// simply time the author thought they had, and the objective is overtaken by the limit rather
    /// than decided by its own clock. m1 ships a deadline of 7 200 against a limit of 7 200, which is
    /// the last tick that is honest, and a test asserts it.
    /// </para>
    /// </summary>
    private static void CheckObjectiveClock(
        MissionDefinition mission,
        int index,
        ObjectiveDefinition objective,
        List<string> problems)
    {
        if (IsDecidedByItsClock(objective.Kind) && objective.DeadlineTick <= 0 && !objective.Constraint)
        {
            problems.Add(
                $"objective {index} is a {objective.Kind} that is not a constraint and has no deadline: " +
                "reaching the deadline is the only thing that completes it, so it can never be " +
                "satisfied, and a mission that requires it can only time out.");
        }

        if (mission.TimeLimitTicks > 0 && objective.DeadlineTick > mission.TimeLimitTicks)
        {
            problems.Add(
                $"objective {index} has a deadline of {objective.DeadlineTick} ticks and the mission's " +
                $"time limit is {mission.TimeLimitTicks}: the deadline arrives after the match is over, " +
                "so it can never be reached" +
                (IsDecidedByItsClock(objective.Kind)
                    ? ", and this is a kind the clock decides — so the objective can never be satisfied " +
                      "and the mission can never be won."
                    : " — the objective is decided at the limit instead, and the time its author gave " +
                      "it is time it never had."));
        }
    }

    /// <summary>
    /// True for the three kinds whose own deadline is what completes them, rather than merely the
    /// moment past which they have failed. The distinction matters to both halves of
    /// <see cref="CheckObjectiveClock"/>: a deadline these kinds do not have is an objective nothing
    /// can complete, and a deadline past the time limit is one that can never be satisfied.
    /// </summary>
    private static bool IsDecidedByItsClock(ObjectiveKind kind) => kind
        is ObjectiveKind.SurviveTicks
        or ObjectiveKind.ProtectCommandCentre
        or ObjectiveKind.DenyArea;

    /// <summary>
    /// <b>The team an objective reasons about: the one whose units, structures or stockpile its own
    /// evaluation reads.</b>
    /// <para>
    /// It is asked of the objective rather than of a field, because which team an objective is
    /// <em>about</em> is a fact about the kind: the two kinds that measure the enemy —
    /// <see cref="ObjectiveKind.DestroyStructures"/> counts the losses of
    /// <see cref="ObjectiveDefinition.TargetTeam"/> and <see cref="ObjectiveKind.DenyArea"/> counts
    /// that team's units in the circle — where every other kind measures
    /// <see cref="ObjectiveDefinition.Team"/>. The team an objective is merely <em>judged</em> for
    /// is not asked about: a <see cref="ObjectiveKind.Scripted"/> objective reads no team at all, so
    /// a stray one in it is a field nothing consults rather than an objective that can never happen.
    /// </para>
    /// </summary>
    /// <returns>The team slot, or -1 for an objective whose evaluation reads no team.</returns>
    private static int TheTeamItReads(ObjectiveDefinition objective) => objective.Kind switch
    {
        ObjectiveKind.DestroyStructures => objective.TargetTeam,
        ObjectiveKind.DenyArea => objective.TargetTeam,
        ObjectiveKind.Scripted => -1,
        _ => objective.Team,
    };

    /// <summary>
    /// Why an objective that reads a team the match does not declare is not an objective, in the
    /// words of what it asks that team for.
    /// <para>
    /// One sentence per kind rather than one for all of them, because the consequence is not the
    /// same: a count that can never rise is an objective that can never be completed, and the very
    /// same absence is a denial that can never be <em>failed</em> — it is completed by its own
    /// deadline with the player having done nothing at all.
    /// </para>
    /// </summary>
    private static string NothingToMeasure(ObjectiveDefinition objective, int team) => objective.Kind switch
    {
        ObjectiveKind.DestroyStructures =>
            $"counts the structures lost by team {team}, which this mission's match does not declare: " +
            "a team that is not playing loses nothing, so the objective can never be completed.",

        ObjectiveKind.DenyArea =>
            $"denies the area to team {team}, which this mission's match does not declare: " +
            "a team that is not playing can never arrive, so the objective can never be failed — " +
            "its deadline completes it with nothing done, and the roster decides the mission.",

        ObjectiveKind.HoldArea =>
            $"holds the area with team {team}, which this mission's match does not declare: " +
            "a team that is not playing stands nowhere, so the objective can never be completed.",

        ObjectiveKind.SurviveTicks =>
            $"survives with team {team}, which this mission's match does not declare: " +
            "a team that is not playing stands in nothing, so the objective fails on its first check.",

        ObjectiveKind.ProtectCommandCentre =>
            $"protects the command centre of team {team}, which this mission's match does not declare: " +
            "a team that is not playing has no command centre, so the objective fails on its first check.",

        ObjectiveKind.AccumulateMaterials =>
            $"stockpiles for team {team}, which this mission's match does not declare: " +
            "a team that is not playing earns nothing, so the objective can never be completed.",

        ObjectiveKind.ReachTechTier =>
            $"researches for team {team}, which this mission's match does not declare: " +
            "a team that is not playing researches nothing, so the objective can never be completed.",

        _ => $"asks team {team}, which this mission's match does not declare.",
    };

    /// <summary>The condition half of <see cref="Validate"/>, one kind at a time.</summary>
    private static void CheckCondition(
        MissionDefinition mission,
        TriggerDefinition trigger,
        string where,
        List<string> problems)
    {
        TriggerCondition condition = trigger.Condition;

        // A condition asked of a team that is not in the match is answered "no" every tick, which
        // is a trigger that can never fire wearing a plausible sentence.
        if (condition.Kind != TriggerConditionKind.TimeElapsed &&
            condition.Kind != TriggerConditionKind.FlagSet &&
            !mission.Roster.IsInPlay(condition.Team))
        {
            problems.Add($"{where} waits on team {condition.Team}, which this mission's match does not declare: it can never fire.");
        }

        switch (condition.Kind)
        {
            case TriggerConditionKind.TimeElapsed:
                if (condition.Tick <= 0)
                {
                    problems.Add($"{where} waits for {condition.Tick} ticks, which have already elapsed when the first tick is evaluated: it fires at once.");
                }

                break;

            case TriggerConditionKind.UnitInArea:
            case TriggerConditionKind.StructuresStandingBelow:
            case TriggerConditionKind.StructuresLost:
                if (condition.Count <= 0)
                {
                    problems.Add($"{where} counts to {condition.Count}: an area condition would hold with nobody in it, and a loss condition would never hold at all.");
                }

                break;
        }

        if (condition.Kind == TriggerConditionKind.UnitInArea && condition.RadiusMm <= 0)
        {
            problems.Add($"{where} watches a circle of radius {condition.RadiusMm}: no unit could ever be inside it.");
        }

        if (condition.Kind == TriggerConditionKind.StructuresStandingBelow &&
            condition.Role != UnitKind.None &&
            !UnitCatalog.TryGet(condition.Role, out _))
        {
            problems.Add($"{where} counts {condition.Role}, which is not a role in the catalogue.");
        }

        // A flag is raised by one trigger and waited on by another; one that raises its own and
        // then waits on it is a trigger that has already fired by the time the flag exists.
        if (condition.Kind == TriggerConditionKind.FlagSet)
        {
            bool raised = false;
            bool onlyByItself = true;

            foreach (TriggerDefinition other in mission.Triggers)
            {
                foreach (TriggerAction action in other.Actions)
                {
                    if (action.Kind == TriggerActionKind.SetFlag && action.Flag == condition.Flag)
                    {
                        raised = true;
                        onlyByItself &= ReferenceEquals(other, trigger);
                    }
                }
            }

            if (!raised)
            {
                problems.Add($"{where} waits on flag {condition.Flag}, which no trigger raises: it can never fire.");
            }
            else if (onlyByItself)
            {
                problems.Add($"{where} waits on flag {condition.Flag}, which only that same trigger raises — and its condition is asked before its actions, so it can never fire.");
            }
        }
    }

    /// <summary>
    /// <b>The other way a script goes wrong: a condition that is already true of the world the
    /// mission opens in, and so fires on the first tick whatever the player does.</b>
    /// <para>
    /// It is the mirror of the check above and it is not the same check. A condition that can
    /// <em>never</em> fire is a feature that exists and does nothing; a condition that is already
    /// true fires perfectly well, in the opening seconds, where its author is looking at
    /// something else — "the first gun has been silenced" arriving before the first shot. Nothing
    /// static can see it, because the answer is a fact about the map the scenario lays out, which
    /// is why this is one of the two checks in the validator that need a world — and why the world
    /// it is handed is the same one <see cref="CheckTheOpeningObjectives"/> asks, built once for
    /// both.
    /// </para>
    /// <para>
    /// <b>Every condition but the clock is asked, and the sweep is deliberately not a list of the
    /// kinds known to be able to fire early.</b> "Fewer than N of them stand now" is the shape
    /// that gets reported, and a circle with a unit already standing inside it is the same trap on
    /// a condition nobody has complained about; a list of the two known-traps would be a list that
    /// goes stale the day a condition kind is added, which is the failure this whole file is
    /// written against. The clock is the one exception, and not an oversight: the first evaluation
    /// happens on tick one with the clock at zero, nothing has elapsed, and the author's own way
    /// of saying "at once" — a tick of zero or less — is refused by <see cref="CheckCondition"/>
    /// already, as a complaint about the clock rather than a second complaint about this one.
    /// </para>
    /// <para>
    /// The two that remain cannot be true of an opening world, and they are asked anyway rather
    /// than excused: the loss ledger starts at zero, so "they have lost two" is false until two
    /// have been destroyed, and a mission's flags start clear, so "the flag is set" is false until
    /// an earlier trigger raises it.
    /// </para>
    /// <para>
    /// <b>Firing on the opening tick is occasionally the design</b> — a mission that branches on a
    /// weak opening force is a legitimate mission — so this reports and does not refuse. A trigger
    /// that means it carries <see cref="TriggerDefinition.DependsOnOpeningWorld"/>, which is the
    /// author's declaration that the condition is about the opening world rather than about what
    /// the player does with it.
    /// </para>
    /// </summary>
    private static void CheckTheOpeningWorld(MissionDefinition mission, SimWorld opening, List<string> problems)
    {
        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            TriggerDefinition trigger = mission.Triggers[i];

            if (trigger.DependsOnOpeningWorld || trigger.Condition.Kind == TriggerConditionKind.TimeElapsed)
            {
                continue;
            }

            if (!Satisfied(opening, trigger.Condition))
            {
                continue;
            }

            problems.Add(
                $"trigger {i} '{trigger.Id}' is already true of the world the mission opens in — " +
                $"{AnsweredByTheOpeningWorld(opening, trigger.Condition)} — so it fires on the first tick " +
                "whatever the player does; a trigger that means that says so with " +
                "DependsOnOpeningWorld = true.");
        }
    }

    /// <summary>
    /// <b>The same question asked of the objectives, and it is the worse of the two failures when
    /// the answer is no.</b>
    /// <para>
    /// A trigger that fires early is a scene in the wrong place — a message about the first gun
    /// falling arriving before the first shot. An objective is a win condition, so an objective the
    /// world has already decided is the mission itself, and there is no playing around it. The two
    /// answers are reported separately because they are not the same severity:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Already failed</b> — a <see cref="ObjectiveKind.DenyArea"/> whose circle the denied
    /// team already stands in is lost on the check that first asks it. The mission is unwinnable,
    /// and the author finds out by losing, which is the worst thing that can be shipped in this
    /// whole campaign layer.</item>
    /// <item><b>Already satisfied</b> — a hold the opening formation already meets, a stockpile or
    /// a tier the side starts with, a structure count of zero. The objective completes with
    /// nothing done, which is an authoring mistake rather than a design: it is not a mission the
    /// player cannot win, it is a mission the player wins for free.</item>
    /// </list>
    /// <para>
    /// <b>Every kind is asked, and the sweep is not a list of the kinds known to be able to be
    /// decided at tick zero</b>, for the reason the condition sweep gives: a list of the traps
    /// somebody happened to notice goes stale the day a kind is added. The kinds that cannot be
    /// decided <em>for</em> the player are asked rather than excused, and asking them is what says
    /// they cannot: a scripted objective is decided by the mission and by nothing else, and a team
    /// that is playing opens with its base standing, so no survival objective of its is already
    /// met. What the sweep does find against those two is the other answer — a team the match does
    /// not declare stands in nothing and owns no command centre, so its survival objective fails on
    /// the check that first asks it.
    /// </para>
    /// <para>
    /// <b>Neither answer is acknowledged, and that is different from the trigger layer on
    /// purpose.</b> <see cref="TriggerDefinition.DependsOnOpeningWorld"/> exists because a script
    /// legitimately branches on the world it opens in — "the force you start with is weak, and the
    /// mission says so" is a scene. An objective has no such reading: "you already hold this" is
    /// not a way to open a mission, it is an objective that has been handed over, and an
    /// acknowledgement would be a way of shipping a mission that cannot be won. So both answers are
    /// refusals, and the author's remedy is to move the circle or change the number.
    /// </para>
    /// <para>
    /// The question is <see cref="MissionSystem.Verdict"/> and the sentence is
    /// <see cref="AnsweredByTheOpeningWorld(SimWorld, ObjectiveDefinition)"/> — the game's own
    /// evaluation, and the numbers the world actually answered with, so that a validator cannot
    /// disagree with the match about what an objective means.
    /// </para>
    /// </summary>
    private static void CheckTheOpeningObjectives(
        MissionDefinition mission,
        SimWorld opening,
        List<string> problems)
    {
        for (int i = 0; i < mission.Objectives.Count; i++)
        {
            ObjectiveDefinition objective = mission.Objectives[i];

            // A team slot the simulation does not have cannot be asked about: an objective's own
            // evaluation indexes the team table, and the complaint has already been made above —
            // the roster does not declare a team that does not exist. The sweep stops there rather
            // than asking a world a question it would throw on.
            if ((uint)TheTeamItReads(objective) >= (uint)SimConstants.TeamCount)
            {
                continue;
            }

            OpeningVerdict verdict = MissionSystem.Verdict(opening, objective);

            if (verdict == OpeningVerdict.Failed)
            {
                problems.Add(
                    $"objective {i} ({objective.Kind}) has already failed in the world the mission opens " +
                    $"in — {AnsweredByTheOpeningWorld(opening, objective)} — so it fails on the first check " +
                    "and the mission is unwinnable: nothing the player does can recover it.");
            }
            else if (verdict == OpeningVerdict.Satisfied)
            {
                problems.Add(
                    $"objective {i} ({objective.Kind}) is already satisfied by the world the mission opens " +
                    $"in — {AnsweredByTheOpeningWorld(opening, objective)} — so it completes with nothing " +
                    "done, which is an authoring mistake rather than a design.");
            }
        }
    }

    /// <summary>
    /// <b>The third layer that reads the world, asked the same question of the same world: what does
    /// the last-side-standing rule say about the sides this mission opens with?</b>
    /// <para>
    /// The trigger layer was the first and the objectives the second, and both were built because
    /// something in a mission could already be decided on the tick the mission opens: a script that
    /// fires before its scene, an objective the map has granted or refused. The victory rule is the
    /// third, and it was safe until now <em>by construction</em> rather than by being asked — the
    /// scenario laid a base down for every team a match declared, so no declared side could stand in
    /// nothing, and a rule that asks about ground never met a side without any.
    /// </para>
    /// <para>
    /// <b>That construction is exactly what the non-player force needs to break, so this is a check
    /// with a declaration rather than a refusal.</b> A mission staged around a side that is not an
    /// army — the scientists of §8's Operation Paperclip, a remnant that exists to be reached — is a
    /// mission whose side has no base and no structures on purpose, and
    /// <see cref="Sim.MatchRoster"/> says so with <see cref="MatchTeam.Judged"/> false on that team.
    /// Without that word, a declared side standing in nothing is the accident: it is read as already
    /// beaten by the rule, and a match whose only other side is that one is won on the check that
    /// first asks it. So the sweep reports a side that stands in nothing and was not declared as such,
    /// and passes over one that was.
    /// </para>
    /// <para>
    /// The sentence names the side, the number the world answered with, and what the rule makes of
    /// it — and the verdict it quotes is <see cref="VictorySystem.Decide"/>'s own answer about this
    /// very world, so the validator cannot disagree with the game about what "already beaten" means.
    /// </para>
    /// </summary>
    private static void CheckTheOpeningSides(MissionDefinition mission, SimWorld opening, List<string> problems)
    {
        MatchRoster roster = mission.Roster;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            // One complaint per side, spoken where the side first appears: two teams on one side are
            // one answer to this question, and the same sentence twice would read as two mistakes.
            if (!roster.IsInPlay(team) || !IsFirstOfItsSide(roster, team))
            {
                continue;
            }

            int side = roster.SideOf(team);

            if (VictorySystem.SideHasStructures(opening, side))
            {
                continue;
            }

            // A side the match declares it does not judge is a non-player force: standing in nothing
            // is the whole of what it is, not a mistake about it.
            if (TheSidesTeamsAreAllUnjudged(roster, side))
            {
                continue;
            }

            GameOutcome verdict = VictorySystem.Decide(opening);

            problems.Add(
                $"side {side} of this mission's match stands in no structures in the world the mission " +
                $"opens in — {WhatTheOpeningWorldAnswered(opening, side)} — so the last-side-standing " +
                "rule reads the side as already beaten on the check that first asks it: a side holding " +
                "no ground is a side that rule cannot tell from one that has been destroyed" +
                (verdict == GameOutcome.Ongoing
                    ? "."
                    : $", and its verdict on that world is {verdict.ToString().ToLowerInvariant()} already.") +
                " A side that holds objectives rather than ground is a deliberate declaration, and a " +
                "match says so with Judged = false on its team.");
        }
    }

    /// <summary>True when no earlier team in the match is on this team's side.</summary>
    private static bool IsFirstOfItsSide(MatchRoster roster, int team)
    {
        for (int earlier = 0; earlier < team; earlier++)
        {
            if (roster.IsInPlay(earlier) && roster.SideOf(earlier) == roster.SideOf(team))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when every team the match declares on this side is one the victory rule does not judge,
    /// which is what "a side that holds objectives rather than ground" is written as.
    /// </summary>
    private static bool TheSidesTeamsAreAllUnjudged(MatchRoster roster, int side)
    {
        int declared = 0;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (!roster.IsInPlay(team) || roster.SideOf(team) != side)
            {
                continue;
            }

            declared++;

            if (roster.IsJudged(team))
            {
                return false;
            }
        }

        return declared > 0;
    }

    /// <summary>
    /// What the world the mission opens in answers about a side, in the words of the question: which
    /// teams are on it and how many structures they own between them. The numbers are measured rather
    /// than repeated, the same way the two sentences above it measure theirs — "stands in nothing" is
    /// a fact about a map, and the line exists to show which fact it is.
    /// </summary>
    private static string WhatTheOpeningWorldAnswered(SimWorld world, int side)
    {
        MatchRoster roster = world.Roster;
        var teams = new List<string>();

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (roster.IsInPlay(team) && roster.SideOf(team) == side)
            {
                teams.Add($"team {team} stands in {VictorySystem.CountStructures(world, team)}");
            }
        }

        return teams.Count == 1
            ? $"{teams[0]} structures"
            : $"{string.Join(" and ", teams)} structures between them";
    }

    /// <summary>
    /// What the opening world answers about an objective, in the words of what the objective asks.
    /// <para>
    /// The numbers are measured rather than repeated, the same way the condition half of this
    /// sentence is: "already decided" is a fact about a map, and the line exists to show which fact
    /// decided it — an author who asked for a hold of six against a formation that already stands
    /// in the circle is looking at the difference between their wish and the layout. Whether the
    /// objective is decided at all is <see cref="MissionSystem.Verdict"/>'s answer and not this
    /// one's: this is the sentence, not the verdict.
    /// </para>
    /// </summary>
    private static string AnsweredByTheOpeningWorld(SimWorld world, ObjectiveDefinition objective)
        => objective.Kind switch
        {
            ObjectiveKind.DestroyStructures =>
                $"team {objective.TargetTeam} has lost {world.TeamRef(objective.TargetTeam).StructuresLost} " +
                $"structures and the objective asks for {objective.TargetCount}",

            ObjectiveKind.HoldArea =>
                $"team {objective.Team} already has " +
                $"{world.CountUnitsInArea(objective.Team, objective.CentreX, objective.CentreZ, objective.RadiusMm)} " +
                $"units inside the circle and the hold asks for {objective.TargetCount}",

            // The denial's own numbers read downwards rather than upwards: what fails it is the
            // denied team being *in* the circle, so the count is measured against the number of
            // them that fail it rather than against a number that would satisfy it.
            ObjectiveKind.DenyArea =>
                $"team {objective.TargetTeam} already has " +
                $"{world.CountUnitsInArea(objective.TargetTeam, objective.CentreX, objective.CentreZ, objective.RadiusMm)} " +
                $"units inside the circle and {objective.TargetCount} of them fail it",

            ObjectiveKind.SurviveTicks =>
                $"team {objective.Team} stands in {world.CountStructures(objective.Team)} structures",

            ObjectiveKind.ProtectCommandCentre =>
                $"team {objective.Team} has " +
                $"{world.CountStructures(objective.Team, UnitKind.CommandCentre)} command centres",

            ObjectiveKind.AccumulateMaterials =>
                $"team {objective.Team} starts with {world.TeamRef(objective.Team).Materials} materials " +
                $"and the objective asks for {objective.MaterialsTarget}",

            ObjectiveKind.ReachTechTier =>
                $"team {objective.Team} already has tech tier {world.TeamRef(objective.Team).TechTier} " +
                $"and the objective asks for {objective.TierTarget}",

            _ => objective.Kind.ToString(),
        };

    /// <summary>
    /// What the opening world answers, in the words of the condition it was asked.
    /// <para>
    /// The numbers are read from the world rather than repeated from the condition, because
    /// "already true" is a fact about a map and this line exists to show which fact made it true:
    /// an author who wrote a threshold of two against a side that has none is looking at the
    /// difference between two and nought. Whether the condition is satisfied at all is
    /// <see cref="Satisfied"/>'s answer and not this one's — this is the sentence, not the
    /// verdict.
    /// </para>
    /// </summary>
    private static string AnsweredByTheOpeningWorld(SimWorld world, in TriggerCondition condition)
        => condition.Kind switch
        {
            TriggerConditionKind.UnitInArea =>
                $"team {condition.Team} already has " +
                $"{world.CountUnitsInArea(condition.Team, condition.CentreX, condition.CentreZ, condition.RadiusMm)} " +
                $"units inside the circle and the condition waits for {condition.Count}",

            TriggerConditionKind.StructuresStandingBelow =>
                $"team {condition.Team} stands in {world.CountStructures(condition.Team, condition.Role)} " +
                $"{RoleName(condition.Role)} and the condition asks for fewer than {condition.Count}",

            // The two below cannot be true of a world nothing has happened in, so they carry the
            // condition's own numbers: there is no measurement to make that would not be nought.
            TriggerConditionKind.StructuresLost =>
                $"team {condition.Team} has lost {condition.Count} structures",

            TriggerConditionKind.FlagSet => $"flag {condition.Flag} is set",

            _ => condition.Kind.ToString(),
        };

    /// <summary>A role as a reader would say it, with the count-everything role spelled out.</summary>
    private static string RoleName(UnitKind role) => role == UnitKind.None ? "structures" : role.ToString();

    /// <summary>
    /// <b>The world a mission opens in: the scenario laid out, the mission attached, and not one
    /// tick run.</b>
    /// <para>
    /// It is rebuilt here rather than handed in, because the question it answers — "has the world
    /// already decided this condition, or this objective" — is a property of the mission rather
    /// than of a match: the probe asks it of a world that is already forty seconds old, and neither
    /// a condition nor an objective can be "already decided" by a world that has been played. The
    /// layout is a pure function of the mission's seed and its definition, both of which the
    /// mission carries with it, so this is the same world the player is handed, rebuilt.
    /// </para>
    /// <para>
    /// The capacity is the world's own maximum, which is far more than any mission's opening force
    /// needs and is deliberate: the layout never runs out of slots here, so no count this check
    /// reads can be short because the check's own world was too small. The price is one world's
    /// memory on a path that runs when the probe is asked, which is the cheap half of the trade.
    /// </para>
    /// </summary>
    public static SimWorld OpeningWorld(MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        SimWorld world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, SimConstants.MaxEntities, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    /// <summary>
    /// Keeps the discs of the mission's reveals lit, and lets them go when their time is up.
    /// <para>
    /// <b>A reveal is not state, and this is why it does not need to be.</b> What the trigger
    /// remembers is the tick it fired on, and that tick is hashed; the ground it lights is
    /// recomputed from that memory every tick, through the same <see cref="VisionSystem"/> disc
    /// that a unit's own eyes are stamped through — because there is one place in this engine
    /// that decides what a team knows, and a mission reveal that wrote its own marks into the fog
    /// would be the second answer to that question which the whole vision system exists to
    /// prevent. A duration ends the watching without erasing it: the cells stay on the team's map
    /// as ground it has seen, which is exactly what the grid already does with ground a unit
    /// looked at once and walked away from.
    /// </para>
    /// <para>
    /// It is stamped after the trigger loop rather than inside it, so that a reveal which fires on
    /// this tick lights its ground on this tick, in the same pass that a unit's eyes are stamped
    /// in.
    /// </para>
    /// </summary>
    private static void StampReveals(SimWorld world, MissionDefinition mission)
    {
        Span<TriggerState> states = world.TriggerStatesSpan;

        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            if (!states[i].HasFired)
            {
                continue;
            }

            TriggerDefinition trigger = mission.Triggers[i];
            long fired = states[i].FiredTick;

            for (int a = 0; a < trigger.Actions.Count; a++)
            {
                TriggerAction action = trigger.Actions[a];

                if (action.Kind != TriggerActionKind.Reveal)
                {
                    continue;
                }

                if (action.Ticks > 0 && world.Tick >= fired + action.Ticks)
                {
                    continue;
                }

                VisionSystem.Stamp(
                    world,
                    action.Team,
                    new WorldPos(action.CentreX, 0, action.CentreZ),
                    action.RadiusMm,
                    stealth: false);
            }
        }
    }
}
