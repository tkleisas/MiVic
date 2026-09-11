using MiVic.Core.Numerics;
using MiVic.Core.Terrain;

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
/// buildings it is missing, raise a defensive line and choose where it stands, spend
/// spare materials on research, keep the army growing, gather it, then push once it is
/// large enough — and only at a place its force can answer.
/// </para>
/// <para>
/// <b>The line is placed, not offset.</b> Emplacements and radar stations are the one
/// thing in the game whose position is a decision — a Πυροβολείο reaches 200 m from a
/// ridge and the same 200 m into a hillside, and the ground it stands on is what decides
/// how much of the shot aimed at it lands — so the AI asks for a site the way a player's
/// click does (<see cref="SimCommand.Structure"/>, judged by
/// <see cref="SimWorld.TryPlanStructure"/>) instead of dropping the building at an offset
/// from whatever made it. What the AI chooses is only *where*; it cannot place better than
/// a player, because it is held to the same rule.
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

    /// <summary>
    /// Gun emplacements the AI's line is built from. Two rather than one because a single
    /// emplacement covers one bearing, and a base that can only be approached from one
    /// direction is a base whose other flank is free.
    /// </summary>
    public const int GunEmplacements = 2;

    /// <summary>
    /// Extra units the AI wants for every enemy gun that can already shoot the position it is
    /// attacking. See <see cref="RequiredArmyAgainst"/> for the whole of the rule.
    /// </summary>
    public const int UnitsPerDefendingGun = 2;

    /// <summary>
    /// How far from home the AI answers an enemy it can see, in millimetres. It is the reach of
    /// the gun emplacement its own line is built from — the ground its line is bought to hold —
    /// rather than a number of its own, so a longer-ranged line would widen the answer with it.
    /// </summary>
    public static readonly int HomeDefenceRadiusMm = UnitCatalog.Get(UnitKind.GunEmplacement).AttackRangeMm;

    /// <summary>
    /// Points the line exists to protect, at most: the base, and the harvesters. Eight is a
    /// command centre plus seven collectors, which is more ore traffic than this AI has ever
    /// had, and a fixed bound keeps the site search allocation-free.
    /// </summary>
    private const int MaxProtectedPoints = 8;

    /// <summary>
    /// Sites kept for the placement rule to judge. The search scores thousands of cells and the
    /// rule is the expensive half of asking — a footprint patch and a scan of everything
    /// standing — so only the best few are ever put to it.
    /// </summary>
    private const int ShortlistedSites = 8;

    /// <summary>
    /// What the line is worth protecting, per protected point, in permille. See
    /// <see cref="SiteScore"/>: it is larger than the whole of the ground term, so a site that
    /// can do its job for one more thing worth defending beats a site on better ground that
    /// cannot.
    /// </summary>
    private const int ProtectedPointPermille = 1_000;

    /// <summary>
    /// The defensive line, in the order it is bought, and how many of each the line wants.
    /// <para>
    /// <b>Why this order.</b> A gun comes first because it is the only part of a line that does
    /// anything on its own: from the tick it is up it reaches 200 m, whatever else is standing.
    /// The radar comes second because it multiplies guns rather than being one — a Πυροβολείο
    /// sees 170 m and shoots 200, so a radar is worth exactly nothing until there is a gun to
    /// give the other 30 m to, and 240 Π spent on one before the first emplacement is a set
    /// watching an empty field. The second gun comes third, so that the radar it was bought
    /// with serves two bearings rather than one. The anti-aircraft emplacement comes last
    /// because it is behind a tech tier and answers a threat — aircraft — that does not exist
    /// until era III; until then an emplacement that cannot fire at anything on the ground is
    /// 200 Π of nothing, which is exactly what the catalogue says it is.
    /// </para>
    /// </summary>
    private static readonly (UnitKind Kind, int Wanted)[] DefensiveLine =
    [
        (UnitKind.GunEmplacement, 1),
        (UnitKind.RadarStation, 1),
        (UnitKind.GunEmplacement, GunEmplacements),
        (UnitKind.AntiAirEmplacement, 1),
    ];

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
        //
        // A dark radar is a power problem too, and it is the one the ledger reports
        // directly: generation that cannot cover the load takes the dishes off the air before
        // it touches anything else, so a team whose radar is dark rebuilds its generation
        // rather than carrying on with a line whose guns have lost 30 m of reach.
        bool hasPower = state.EnergyPerTick > 0 || HasBuilding(world, team, UnitKind.PowerPlant);
        bool plantQueued = IsQueued(world, team, UnitKind.PowerPlant);

        if ((!hasPower || state.RadarsDark > 0) && !plantQueued &&
            TryFindBuilding(world, team, UnitKind.CommandCentre, out EntityId powerBuilder, out _))
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

        // 3. The defensive line, before the projects and the armour: a base that has been
        // overrun has no economy to spend and no army to send, so the thing that keeps it
        // alive is bought out of the first spare material the loop has. It is a single
        // structure per decision and the attempt costs nothing when it fails, so an AI that
        // cannot afford its next emplacement yet carries on producing in the meantime.
        if (ExtendDefences(world, team))
        {
            return;
        }

        // 4. Research whenever there is spare material and a bureau to run it.
        if (!state.IsResearching && TryFindBuilding(world, team, UnitKind.DesignBureau, out EntityId bureau, out _))
        {
            TechId project = ChooseResearch(state, faction);

            if (project != TechId.None && TechCatalog.TryGet(project, out TechProject details) &&
                state.Materials >= details.Cost + 400)
            {
                world.Enqueue(SimCommand.Research(bureau, project, world.Tick + 1, team));
            }
        }

        // 5. Keep the army growing.
        if (CountCombatUnits(world, team) < TargetArmySize)
        {
            ProduceArmy(world, team, faction);
        }

        // 6. Gather, defend, or push.
        CommandArmy(world, team, faction);
    }

    /// <summary>
    /// Raises the next structure of the AI's defensive line, and says whether one was ordered.
    /// <para>
    /// The line is a priority list rather than a menu: the first entry the team does not yet
    /// have is the only one it tries to build, so an AI that cannot afford a radar yet does not
    /// quietly buy the anti-aircraft emplacement behind it instead. A building site counts as
    /// had — it is a structure the team has paid for and is waiting on — which is what keeps
    /// this from ordering the same emplacement once a second until the first one is up.
    /// </para>
    /// </summary>
    private static bool ExtendDefences(SimWorld world, int team)
    {
        foreach ((UnitKind kind, int wanted) in DefensiveLine)
        {
            if (CountStructures(world, team, kind) >= wanted)
            {
                continue;
            }

            // A radar is the one structure here that is load as well as capability, and the
            // grid sheds detection first: an AI that adds a dish to a base that cannot run it
            // has made itself worse than one that never built it, because it has paid 240 Π
            // for a building that switched itself off. So the generation is bought first —
            // in the same decision, from the same ledger the lighting pass uses — and the
            // radar follows once the plant is standing.
            if (kind == UnitKind.RadarStation && !PowerSystem.HasRoomForRadar(world, team))
            {
                return TryQueuePowerPlant(world, team);
            }

            return TryRaise(world, team, kind);
        }

        return false;
    }

    /// <summary>
    /// Raises one structure at a site the AI chose, through the same path a player's click
    /// takes: the plan is asked first, and the order names the cell the plan approved.
    /// <para>
    /// The site is re-judged when the command executes, on the next tick, and a refusal there
    /// costs nothing — <see cref="SimWorld"/> does not charge for a plan it will not carry
    /// out. That is the honest way round: the AI is entitled to try, not to place.
    /// </para>
    /// </summary>
    private static bool TryRaise(SimWorld world, int team, UnitKind kind)
    {
        if (!world.CanBuildStructure(team, kind, out _))
        {
            return false;
        }

        if (!TryChooseSite(world, team, kind, out WorldPos site))
        {
            return false;
        }

        world.Enqueue(SimCommand.Structure(kind, site, world.Tick + 1, team));
        return true;
    }

    /// <summary>
    /// Where the AI puts a defensive structure: the cell around what it is defending that is
    /// worth the most, judged by the placement rule like anybody else's.
    /// <para>
    /// <b>What "worth the most" means here, in one line:</b> the number of things worth
    /// defending this building can do its job for, plus how much cover the ground under it
    /// gives it — see <see cref="SiteScore"/>. The candidates are the cells within the
    /// building's own reach of a protected point, which is the ground from which it can do
    /// anything at all, so the search is as wide as the building's job rather than a ring of
    /// somebody's choosing. The result is not a ring around the headquarters: a site that also
    /// covers a collector beats a site on better ground that covers the base alone, and among
    /// sites that cover the same things the ground decides.
    /// </para>
    /// <para>
    /// Only the best few cells are put to <see cref="SimWorld.TryPlanStructure"/>, in score
    /// order, and the first that the rule accepts is the site. That is deliberate: the rule is
    /// the expensive half of the question and it answers the same way for every cell in a
    /// neighbourhood, so asking it about eight cells instead of two thousand changes nothing
    /// except the cost. A site the rule refuses — the base's own ground, a cell a building
    /// already stands on, the lake — is simply not chosen, and the next best is asked about.
    /// </para>
    /// <para>
    /// Deterministic: cells are scanned in ascending index order around each protected point,
    /// the shortlist breaks ties in favour of the site scanned first, and nothing but the
    /// terrain, the standing structures and the tick is read.
    /// </para>
    /// </summary>
    private static bool TryChooseSite(SimWorld world, int team, UnitKind kind, out WorldPos site)
    {
        site = default;

        Span<WorldPos> protectedPoints = stackalloc WorldPos[MaxProtectedPoints];
        int points = CollectProtectedPoints(world, team, protectedPoints);

        if (points == 0)
        {
            return false;
        }

        int reachCells = CoverRadiusMm(kind) / world.Navigation.CellSizeMm;
        Span<WorldPos> shortlist = stackalloc WorldPos[ShortlistedSites];
        Span<int> shortlistScore = stackalloc int[ShortlistedSites];
        int shortlisted = 0;

        for (int point = 0; point < points; point++)
        {
            int centre = world.Navigation.IndexOfWorld(protectedPoints[point]);
            int centreX = world.Navigation.CellX(centre);
            int centreZ = world.Navigation.CellZ(centre);

            for (int dz = -reachCells; dz <= reachCells; dz++)
            {
                for (int dx = -reachCells; dx <= reachCells; dx++)
                {
                    int cell = world.Navigation.IndexOf(centreX + dx, centreZ + dz);

                    if (cell < 0)
                    {
                        continue;
                    }

                    WorldPos candidate = world.Navigation.CentreOf(cell);

                    RememberSite(
                        shortlist,
                        shortlistScore,
                        ref shortlisted,
                        candidate,
                        SiteScore(world, kind, candidate, protectedPoints));
                }
            }
        }

        for (int i = 0; i < shortlisted; i++)
        {
            if (world.TryPlanStructure(team, kind, shortlist[i], out WorldPos planned, out _))
            {
                site = planned;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What one cell is worth to a defensive structure of this role, in permille: what it can
    /// protect, plus the ground it stands on.
    /// <para>
    /// <b>The ground term is <see cref="TerrainLayer.CoverPermille"/>, asked of the same call
    /// the combat system asks.</b> Cover is the one number in this engine that says whether a
    /// piece of ground is worth standing on, and it is the same number that decides how much of
    /// every shot aimed at the building lands: a Πυροβολείο in a basin takes 125 ‰ less than
    /// the same gun on a plain, and one on a crest takes 100 ‰ more, because a crest is ground
    /// that falls away at the cell and nothing stands between the gun and anyone. Nothing else
    /// about the shape of the land is modelled — there is no line of sight in this engine — so
    /// a second term for height would be a second answer invented here rather than read from
    /// the terrain.
    /// </para>
    /// <para>
    /// The cover term is deliberately smaller than one protected point: a site that covers the
    /// base *and* a collector is worth more than a site on perfect ground that covers one of
    /// them, and the ground decides among sites that cover the same things. That is the order
    /// of the two questions — a gun that cannot see the point it was built to defend is not
    /// made good by where it stands.
    /// </para>
    /// </summary>
    private static int SiteScore(SimWorld world, UnitKind kind, WorldPos candidate, ReadOnlySpan<WorldPos> protectedPoints)
    {
        int reach = CoverRadiusMm(kind);
        long reachSquared = (long)reach * reach;
        int score = 0;

        for (int point = 0; point < protectedPoints.Length; point++)
        {
            if (candidate.DistanceSquaredTo(protectedPoints[point]) <= reachSquared)
            {
                score += ProtectedPointPermille;
            }
        }

        int cell = world.TerrainTypes.IndexOfWorld(candidate.X, candidate.Z);
        int cover = world.TerrainTypes.CoverAt(cell, UnitCatalog.Get(kind).Movement);

        return score + (TerrainLayer.NoCoverPermille - cover);
    }

    /// <summary>
    /// The radius a structure acts over, in millimetres: its gun if it has one, its eyes if it
    /// has not. A Πυροβολείο reaches 200 m and a Σταθμός Ραντάρ watches 260, and each is the
    /// ground its own purchase is about — which is why the site search is this wide and not
    /// some fixed number of cells.
    /// </summary>
    private static int CoverRadiusMm(UnitKind kind)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        return Math.Max(definition.AttackRangeMm, VisionSystem.SightRadiusMm(kind));
    }

    /// <summary>
    /// The points the AI's defences exist to protect: its command centres, and every collector
    /// it owns. A raid is sent at the base and at the ore traffic, and this is where the AI
    /// says so — a line placed around the headquarters alone is a line around the wrong thing
    /// on the day a collector is what is being shot at.
    /// <para>
    /// Ascending slot order, like every other scan here, so two runs choose the same points in
    /// the same order and therefore the same sites.
    /// </para>
    /// </summary>
    private static int CollectProtectedPoints(SimWorld world, int team, Span<WorldPos> destination)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity && count < destination.Length; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || entity.Kind is not (UnitKind.CommandCentre or UnitKind.Harvester))
            {
                continue;
            }

            destination[count++] = entity.Position;
        }

        return count;
    }

    /// <summary>
    /// Keeps the highest-scoring <see cref="ShortlistedSites"/> cells seen so far, in descending
    /// score, keeping the earliest of two equal scores. The tie-break is the same one every
    /// other scan in this game uses — ascending cell order wins — and it is what makes the
    /// second emplacement land on the second-best cell when the best one is already taken.
    /// </summary>
    private static void RememberSite(
        Span<WorldPos> sites,
        Span<int> scores,
        ref int count,
        WorldPos candidate,
        int score)
    {
        if (count == sites.Length)
        {
            if (score <= scores[count - 1])
            {
                return;
            }

            count--;
        }

        int at = count;

        while (at > 0 && scores[at - 1] < score)
        {
            scores[at] = scores[at - 1];
            sites[at] = sites[at - 1];
            at--;
        }

        scores[at] = score;
        sites[at] = candidate;
        count++;
    }

    /// <summary>
    /// How many structures of a role a team has committed to: standing, or rising. A building
    /// site counts, because it is a structure the team has already paid for — an order that
    /// counted only finished buildings would buy a second emplacement every second until the
    /// first one was up.
    /// </summary>
    private static int CountStructures(SimWorld world, int team, UnitKind kind)
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

            if (entity.TeamId == team && entity.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Buys the generation the line is short of, from the team's own headquarters. One at a
    /// time: a plant in the queue is generation the team has already decided to buy, and a
    /// projection that could not see the queue would order another one every second until the
    /// first arrived.
    /// </summary>
    private static bool TryQueuePowerPlant(SimWorld world, int team)
    {
        if (IsQueued(world, team, UnitKind.PowerPlant) ||
            !TryFindBuilding(world, team, UnitKind.CommandCentre, out EntityId builder, out Faction faction))
        {
            return false;
        }

        return TryQueue(world, builder, faction, UnitKind.PowerPlant, team);
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
            UnitKind.ElectroPrototype, UnitKind.Tank, UnitKind.Artillery, UnitKind.RocketArtillery,
            UnitKind.AntiAir, UnitKind.Drone, UnitKind.RobotInfantry, UnitKind.Aircraft, UnitKind.Infantry,
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

        if (state.IsPrototyping || !UnitCatalog.IsUnlocked(SimWorld.FactionOfTeam(team), kind, state.TechTier, state.TechMask))
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
    /// Sends idle combat units at an enemy worth going to, or holds them at home when there is
    /// none — see <see cref="ChooseTarget"/>, which is where "respecting a defended position"
    /// lives. Until the army is big enough to attack anything it gathers near home, so that it
    /// arrives together rather than in the order it was built.
    /// </summary>
    private static void CommandArmy(SimWorld world, int team, Faction faction)
    {
        if (!TryFindBuilding(world, team, out EntityId headquarters, out _))
        {
            return;
        }

        ref Entity home = ref world.GetRefBySlot(headquarters.Slot);
        int army = CountCombatUnits(world, team);
        int targetSlot = ChooseTarget(world, team, home.Position, army);
        bool attack = targetSlot >= 0;

        // The rally is ground like any other, and a gathering point nothing can reach is an
        // army standing still with a move order on it: a headquarters near a shore would
        // otherwise hold its own armour in the lake.
        WorldPos rally = attack
            ? default
            : world.LegalSpawnSite(new WorldPos(
                home.Position.X + (faction == Faction.Western ? RallyDistanceMm : -RallyDistanceMm),
                0,
                home.Position.Z + RallyDistanceMm));

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

    /// <summary>
    /// What the army is sent at this second, or -1 when there is nothing it should be sent at.
    /// <para>
    /// <b>First: an enemy standing on the AI's own ground, if the AI can see it.</b> A raider
    /// inside the radius the AI's own emplacements cover is the raid the line exists to answer,
    /// so it is answered by the army as well as by the guns, and without counting anything:
    /// a base being shot at is not a position to be assaulted, it is a position to be defended.
    /// </para>
    /// <para>
    /// <b>Second: the enemy position that is cheapest to take.</b> Candidates are the enemy
    /// structures the team is allowed to attack at all (see <see cref="IsAttackable"/>), and each
    /// is scored by how many enemy guns can already shoot it — asked of
    /// <see cref="CombatSystem.EngagementRadiusMm"/>, which is the number the gun itself will
    /// use, so an emplacement whose radar has been bombed flat really is cheaper to attack and
    /// the AI is not refusing an assault it could make. A position defended by more guns than
    /// the force is bought to answer is not attacked at all: the army waits, and the whole of
    /// that rule is <see cref="RequiredArmyAgainst"/>.
    /// </para>
    /// <para>
    /// Among the positions it can answer, the least defended is chosen, and the nearest of
    /// those. That is the preference that matters: given a headquarters behind three guns and a
    /// power plant behind none, the AI takes the power plant — the ground it can actually
    /// stand on — instead of feeding itself into the line it cannot answer.
    /// </para>
    /// </summary>
    private static int ChooseTarget(SimWorld world, int team, WorldPos home, int army)
    {
        if (TryFindRaid(world, team, home, out int raid))
        {
            return raid;
        }

        if (army < AttackArmySize)
        {
            return -1;
        }

        int best = -1;
        int fewestGuns = int.MaxValue;
        long bestDistance = long.MaxValue;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(slot);

            if (!UnitCatalog.Get(candidate.Kind).IsBuilding || !IsAttackable(world, team, slot))
            {
                continue;
            }

            // The guns that can already shoot this place, and the force it takes to go in
            // anyway. A position this army cannot answer is not a target at all.
            int guns = CountGunsCovering(world, team, candidate.Position);

            if (army < RequiredArmyAgainst(guns))
            {
                continue;
            }

            long distance = home.DistanceSquaredTo(candidate.Position);

            if (guns < fewestGuns || (guns == fewestGuns && distance < bestDistance))
            {
                fewestGuns = guns;
                bestDistance = distance;
                best = slot;
            }
        }

        return best;
    }

    /// <summary>
    /// The nearest enemy the team can see standing inside <see cref="HomeDefenceRadiusMm"/> of
    /// its base, as a job for the army. Units only: a structure does not raid anybody, and the
    /// structures of an enemy that has walked into the base are the assault's business. The
    /// question asked of each candidate is <see cref="IsAttackable"/> rather than the fog alone,
    /// because what follows is an attack order — a man the team cannot see is not a target here
    /// either, however well the ground he is standing on is watched.
    /// </summary>
    private static bool TryFindRaid(SimWorld world, int team, WorldPos home, out int slot)
    {
        long radiusSquared = (long)HomeDefenceRadiusMm * HomeDefenceRadiusMm;
        int best = -1;
        long bestDistance = long.MaxValue;
        int capacity = world.Capacity;

        for (int candidate = 0; candidate < capacity; candidate++)
        {
            if (!world.IsAliveSlot(candidate))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(candidate);

            if (UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            long distance = home.DistanceSquaredTo(entity.Position);

            if (distance > radiusSquared || !IsAttackable(world, team, candidate))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        slot = best;
        return best >= 0;
    }

    /// <summary>
    /// How many enemy guns can already shoot a position: the number of emplacements the AI
    /// would have to answer before it could stand there. Asked of the engagement radius rather
    /// than of the catalogue's range, so a gun whose radar has gone dark is counted for the
    /// 170 m it really has.
    /// </summary>
    private static int CountGunsCovering(SimWorld world, int team, WorldPos position)
    {
        int count = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity gun = ref world.GetRefBySlot(slot);

            if (IsFriendly(world, team, gun.TeamId) || !UnitCatalog.Get(gun.Kind).IsBuilding)
            {
                continue;
            }

            int reach = CombatSystem.EngagementRadiusMm(world, slot);

            if (reach <= 0)
            {
                continue;
            }

            int dx = gun.Position.X - position.X;
            int dz = gun.Position.Z - position.Z;

            if (((long)dx * dx) + ((long)dz * dz) <= (long)reach * reach)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The army the AI wants before it will attack a position defended by this many guns: the
    /// ordinary attacking force, plus <see cref="UnitsPerDefendingGun"/> units for each gun
    /// that can already shoot the place.
    /// <para>
    /// <b>Capped at the army the AI actually keeps</b>, which is the part that matters. Without
    /// the cap a line of four emplacements would demand twenty units from a team that stops
    /// producing at eighteen, and the AI would never attack anything again — a defence it
    /// cannot answer would have made it passive rather than careful, which is a worse opponent
    /// than the one that walks in. With the cap, a heavily defended position makes the AI wait
    /// for its whole army rather than for one it can never raise: it comes with everything it
    /// has, or it does not come.
    /// </para>
    /// </summary>
    public static int RequiredArmyAgainst(int guns)
        => AttackArmySize + Math.Min(guns * UnitsPerDefendingGun, TargetArmySize - AttackArmySize);

    /// <summary>
    /// True when the AI may order an attack on a slot: it is alive, it is not a friend, and it
    /// is not something the team cannot see.
    /// <para>
    /// <b>A target the AI cannot see is an order that fails on arrival.</b> Detection is part
    /// of the question rather than a clause beside it: <see cref="SimWorld.IsHiddenFrom"/> is
    /// the world's own answer for a stealthed enemy — the same answer that decides whether the
    /// guns can shoot it at all — and the fog is the answer for everything else. A mobile enemy
    /// has to be in sight, because an order against one is an order against where it was: a
    /// Καταδρομέας walking towards a base with no radar is not a target the AI has, so its army
    /// is not sent to an empty piece of ground, and the same man under a lit radar is a target
    /// like any other.
    /// </para>
    /// <para>
    /// <b>A structure is the one exception, and it is a decision rather than an oversight.</b>
    /// A building does not move, so an order against one is an order against a place: the army
    /// marches to where the enemy base is standing and the base is still there when it arrives.
    /// Requiring live eyes on it would not make the AI more honest, it would make it passive —
    /// its base is six hundred metres from the enemy's, so nothing is ever in sight of anything
    /// until an army has already been sent, and no army could ever be sent. What the AI
    /// remembers is exactly "where the enemy's buildings stand", which is the one thing an
    /// opponent is entitled to know; it has no scouting memory of its own, and building one is
    /// a different feature from this one.
    /// </para>
    /// </summary>
    private static bool IsAttackable(SimWorld world, int team, int slot)
    {
        if (!world.IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity target = ref world.GetRefBySlot(slot);

        if (IsFriendly(world, team, target.TeamId) || world.IsHiddenFrom(team, slot))
        {
            return false;
        }

        return UnitCatalog.Get(target.Kind).IsBuilding || IsInSight(world, team, target.Position);
    }

    /// <summary>
    /// True when the team has eyes on a point right now: the fog the client draws and the
    /// detection channel that finds a hidden enemy, both written by
    /// <see cref="VisionSystem"/> and read here rather than re-derived. A radar that is on the
    /// air widens this, which is half of what a radar is for.
    /// </summary>
    private static bool IsInSight(SimWorld world, int team, WorldPos position)
    {
        int cell = world.TerrainTypes.IndexOfWorld(position.X, position.Z);

        return cell >= 0 && world.Visibility.IsVisible(team, cell);
    }

    /// <summary>
    /// True when two teams are on the same side. The alliance is not this system's rule —
    /// <see cref="SimWorld.AreAllied"/> is — but an AI that ignored it would send its army at
    /// its own ally's headquarters, which is what "nearest enemy building" means when the
    /// word <em>enemy</em> was never actually asked.
    /// </summary>
    private static bool IsFriendly(SimWorld world, int team, int other) => SimWorld.AreAllied(team, other);

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
