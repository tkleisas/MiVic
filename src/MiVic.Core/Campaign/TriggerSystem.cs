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

            case TriggerConditionKind.StructureDestroyed:
                // The ledger the DestroyStructures objective reads, and read the same way: it
                // counts losses rather than comparing against a starting total, so a structure
                // the enemy rebuilds does not undo the progress.
                return (uint)condition.Team < SimConstants.TeamCount &&
                       world.TeamRef(condition.Team).StructuresLost >= condition.Count;

            case TriggerConditionKind.StructuresBelow:
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
    /// <b>Everything wrong with a mission's script that can be seen without running it, as a list
    /// of complaints; empty means the script is at least self-consistent.</b>
    /// <para>
    /// It exists for one failure above all others: <em>a trigger that is authored and can never
    /// fire</em>. This project has shipped a volcano line above every cell, a sand band below the
    /// mud line and an entire mud mechanic that was inert, and a mission whose second act never
    /// happens is that same bug wearing a script. What can be checked statically is checked here —
    /// a flag nothing raises, an objective nothing completes, a denial with no clock to be decided
    /// by, a completion pointing at an objective that does not exist, a condition counting on a
    /// team that is not in the match. What can only be checked against a running world — whether a
    /// count is already below its threshold on the opening tick, whether a trigger ever really
    /// fires — is what the mission tests do with a world in their hands.
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
        }

        return problems;
    }

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
            case TriggerConditionKind.StructuresBelow:
            case TriggerConditionKind.StructureDestroyed:
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

        if (condition.Kind == TriggerConditionKind.StructuresBelow &&
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
