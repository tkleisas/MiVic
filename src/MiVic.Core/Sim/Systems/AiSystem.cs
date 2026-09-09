using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// A deterministic opponent.
/// <para>
/// The AI runs on the same tick clock as everything else and issues the same
/// commands a human would, so it needs no special cases in the simulation and a
/// replay of its decisions is byte-identical. Decisions are taken once a second
/// from world state alone — no randomness, no wall clock — which keeps the
/// determinism contract intact.
/// </para>
/// <para>
/// It plays a simple but complete loop: keep the power on, put up the production
/// buildings it is missing, spend spare materials on research, keep the army
/// growing, gather it, then push once it is large enough.
/// </para>
/// </summary>
public static class AiSystem
{
    /// <summary>Ticks between decisions. One second at 20 Hz.</summary>
    public const int DecisionInterval = 20;

    /// <summary>Army size the AI stops producing at.</summary>
    public const int TargetArmySize = 18;

    /// <summary>Army size at which the AI attacks instead of gathering.</summary>
    public const int AttackArmySize = 12;

    /// <summary>Distance from its own base where the AI gathers, in millimetres.</summary>
    public const int RallyDistanceMm = 90_000;

    /// <summary>
    /// Orders issued per decision. Without a cap the AI hands hundreds of units a
    /// new order in the same tick, and each one can trigger a path search — enough
    /// to blow the frame budget on its own.
    /// </summary>
    public const int MaxOrdersPerDecision = 8;

    /// <summary>Runs one AI decision step for every AI-controlled team.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Tick % DecisionInterval != 0)
        {
            return;
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (IsAiTeam(team))
            {
                Decide(world, team);
            }
        }
    }

    /// <summary>Teams the computer plays. The player owns team 0.</summary>
    public static bool IsAiTeam(int team) => team is 1 or 2;

    /// <summary>One team's decision cycle.</summary>
    private static void Decide(SimWorld world, int team)
    {
        TeamState state = world.Team(team);

        if (!TryFindBuilding(world, team, out EntityId headquarters, out Faction faction))
        {
            return;
        }

        // 1. Power first: without energy nothing else runs. Count plants already
        // under construction, or the AI queues a row of them before the first
        // one finishes.
        bool hasPower = state.EnergyPerTick > 0 || HasBuilding(world, team, UnitKind.PowerPlant) || IsQueued(world, team, UnitKind.PowerPlant);

        if (!hasPower && TryFindBuilding(world, team, UnitKind.CommandCentre, out EntityId powerBuilder, out _))
        {
            if (TryQueue(world, powerBuilder, faction, UnitKind.PowerPlant, team))
            {
                return;
            }
        }

        // 2. The buildings the loop needs.
        if (!HasBuilding(world, team, UnitKind.Factory) && !IsQueued(world, team, UnitKind.Factory) &&
            TryQueue(world, headquarters, faction, UnitKind.Factory, team))
        {
            return;
        }

        if (!HasBuilding(world, team, UnitKind.DesignBureau) && !IsQueued(world, team, UnitKind.DesignBureau) &&
            TryQueue(world, headquarters, faction, UnitKind.DesignBureau, team))
        {
            return;
        }

        // 3. Research whenever there is spare material and a bureau to run it.
        if (!state.IsResearching && TryFindBuilding(world, team, UnitKind.DesignBureau, out EntityId bureau, out _))
        {
            TechId project = ChooseResearch(state, faction);

            if (project != TechId.None && TechCatalog.TryGet(project, out TechProject details) &&
                state.Materials >= details.Cost + 400)
            {
                world.Enqueue(SimCommand.Research(bureau, project, world.Tick + 1, team));
            }
        }

        // 4. Keep the army growing.
        if (CountCombatUnits(world, team) < TargetArmySize)
        {
            ProduceArmy(world, team, faction);
        }

        // 5. Gather, or push.
        CommandArmy(world, team, faction);
    }

    /// <summary>
    /// Picks the next project: catalogue order is already a sensible priority —
    /// tier advances first, then the cheap permanent bonuses.
    /// </summary>
    private static TechId ChooseResearch(TeamState state, Faction faction)
    {
        foreach (TechProject project in TechCatalog.Available(faction, state.TechTier, state.TechMask))
        {
            return project.Id;
        }

        return TechId.None;
    }

    /// <summary>Queues the most capable affordable combat unit the team can field.</summary>
    private static void ProduceArmy(SimWorld world, int team, Faction faction)
    {
        ReadOnlySpan<UnitKind> preference =
        [
            UnitKind.Tank, UnitKind.Artillery, UnitKind.RocketArtillery, UnitKind.AntiAir,
            UnitKind.Aircraft, UnitKind.Infantry,
        ];

        foreach (UnitKind kind in preference)
        {
            UnitDefinition definition = UnitCatalog.Get(kind);

            if (!TryFindBuilding(world, team, definition.ProducedAt, out EntityId building, out _))
            {
                continue;
            }

            // A Σοβιετικοί factory cannot build a design the bureau has not
            // proven. Run the prototype instead of stalling on an empty queue.
            if (faction == Faction.Soviet && definition.ProducedAt == UnitKind.Factory && !world.CanBuild(team, kind))
            {
                if (TryPrototype(world, team, kind))
                {
                    return;
                }

                continue;
            }

            if (TryQueue(world, building, faction, kind, team))
            {
                return;
            }
        }
    }

    /// <summary>Starts a prototype run for a design the team has researched.</summary>
    private static bool TryPrototype(SimWorld world, int team, UnitKind kind)
    {
        ref TeamState state = ref world.TeamRef(team);

        if (state.IsPrototyping || !UnitCatalog.IsUnlocked(SimWorld.FactionOfTeam(team), kind, state.TechTier))
        {
            return false;
        }

        if (!TryFindBuilding(world, team, UnitKind.DesignBureau, out EntityId bureau, out _))
        {
            return false;
        }

        world.Enqueue(SimCommand.ApproveDesign(bureau, kind, world.Tick + 1, team));
        return true;
    }

    /// <summary>
    /// Sends idle combat units at the nearest enemy structure once the army is
    /// big enough; until then they gather near home so they arrive together.
    /// </summary>
    private static void CommandArmy(SimWorld world, int team, Faction faction)
    {
        if (!TryFindBuilding(world, team, out EntityId headquarters, out _))
        {
            return;
        }

        ref Entity home = ref world.GetRefBySlot(headquarters.Slot);
        int army = CountCombatUnits(world, team);
        bool attack = army >= AttackArmySize;

        int targetSlot = attack ? FindNearestEnemyBuilding(world, team, home.Position) : -1;
        WorldPos rally = attack
            ? default
            : new WorldPos(
                home.Position.X + (faction == Faction.Western ? RallyDistanceMm : -RallyDistanceMm),
                0,
                home.Position.Z + RallyDistanceMm);

        int capacity = world.Capacity;
        int issued = 0;

        for (int slot = 0; slot < capacity && issued < MaxOrdersPerDecision; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity unit = ref world.GetRefBySlot(slot);

            if (unit.TeamId != team || !UnitCatalog.Get(unit.Kind).IsArmed || unit.Routed)
            {
                continue;
            }

            // Leave units that are already busy alone.
            if (unit.HasMoveGoal || unit.HasAttackOrder)
            {
                continue;
            }

            var id = new EntityId(slot, unit.Generation);

            if (targetSlot >= 0)
            {
                ref Entity victim = ref world.GetRefBySlot(targetSlot);
                world.Enqueue(SimCommand.Attack(id, new EntityId(targetSlot, victim.Generation), world.Tick + 1, team));
            }
            else
            {
                world.Enqueue(SimCommand.Move(id, rally, world.Tick + 1, team));
            }

            issued++;
        }
    }

    /// <summary>Nearest enemy structure, preferring command centres.</summary>
    private static int FindNearestEnemyBuilding(SimWorld world, int team, WorldPos from)
    {
        int best = -1;
        long bestDistance = long.MaxValue;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(slot);

            if (candidate.TeamId == team || !UnitCatalog.Get(candidate.Kind).IsBuilding)
            {
                continue;
            }

            long distance = from.DistanceSquaredTo(candidate.Position);

            // Structures of an enemy team are all fair game; the nearest wins.
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = slot;
            }
        }

        return best;
    }

    /// <summary>Queues a unit if the team can afford it.</summary>
    private static bool TryQueue(SimWorld world, EntityId building, Faction faction, UnitKind kind, int team)
    {
        TeamState state = world.Team(team);

        if (!world.CanBuild(team, kind))
        {
            return false;
        }

        int materials = UnitCatalog.MaterialCost(faction, kind);
        int energy = UnitCatalog.EnergyCost(faction, kind);
        int water = UnitCatalog.WaterCost(faction, kind);

        if (state.Materials < materials + 200 || state.Energy < energy || state.Water < water)
        {
            return false;
        }

        world.Enqueue(SimCommand.QueueUnit(building, kind, world.Tick + 1, team));
        return true;
    }

    private static int CountCombatUnits(SimWorld world, int team)
    {
        int count = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsArmed)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool HasBuilding(SimWorld world, int team, UnitKind kind)
        => TryFindBuilding(world, team, kind, out _, out _);

    /// <summary>True when the team already has a job for this role in any queue.</summary>
    private static bool IsQueued(SimWorld world, int team, UnitKind kind)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team)
            {
                continue;
            }

            foreach (ProductionJob job in world.JobsOf(slot))
            {
                if (job.Kind == kind)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Finds a team's first building of a role, and its faction.</summary>
    private static bool TryFindBuilding(SimWorld world, int team, UnitKind kind, out EntityId building, out Faction faction)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == kind)
            {
                building = new EntityId(slot, entity.Generation);
                faction = entity.Faction;
                return true;
            }
        }

        building = EntityId.None;
        faction = Faction.None;
        return false;
    }

    /// <summary>Finds a team's command centre, which is its anchor point.</summary>
    private static bool TryFindBuilding(SimWorld world, int team, out EntityId headquarters, out Faction faction)
        => TryFindBuilding(world, team, UnitKind.CommandCentre, out headquarters, out faction);
}
