using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Random;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// The authoritative simulation state: entities, tick counter, RNG and the
/// command queue.
/// <para>
/// Everything here is deterministic. There are no dictionaries, no LINQ, no
/// floating point and no wall-clock reads in <see cref="Step"/>; iteration is
/// always in ascending slot order so that two machines running the same commands
/// produce bit-identical state.
/// </para>
/// </summary>
public sealed class SimWorld
{
    private readonly Entity[] _entities;
    private readonly int[] _freeSlots;
    private readonly int[] _pathCells;
    private readonly ProductionJob[] _jobs;
    private readonly TeamState[] _teams;
    private readonly PathFinder _pathFinder;
    private readonly SpatialIndex _spatial;
    private readonly VisibilityGrid _visibility;
    private readonly List<SimCommand> _commandQueue = new();
    private readonly List<SimCommandRecord> _recordedCommands = [];
    private ObjectiveState[] _objectives = [];
    private MissionDefinition? _mission;
    private bool _recording;
    private bool _insideStep;
    private int _freeCount;

    /// <summary>Creates a world with the maximum entity capacity.</summary>
    public SimWorld(ulong seed)
        : this(seed, SimConstants.MaxEntities)
    {
    }

    /// <summary>Creates a world with an explicit capacity, for tests.</summary>
    public SimWorld(ulong seed, int capacity)
    {
        if (capacity <= 0 || capacity > SimConstants.MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be in (0, MaxEntities].");
        }

        Seed = seed;
        _entities = new Entity[capacity];
        _freeSlots = new int[capacity];
        _freeCount = capacity;

        // Reverse order so the first spawn lands in slot 0.
        for (int i = 0; i < capacity; i++)
        {
            _freeSlots[i] = capacity - 1 - i;
        }

        Rng = new Pcg32(seed);

        // Terrain is a pure function of the seed, so it never needs to be part of
        // the per-tick state hash — only of the initial world hash.
        Terrain = HeightMap.Generate(seed, SimConstants.TerrainResolution, SimConstants.MapExtentMm, SimConstants.TerrainMaxHeightMm);
        Navigation = NavGrid.Build(Terrain, SimConstants.MaxSlopePermille, SimConstants.NavGridStride);
        TerrainTypes = TerrainLayer.Build(Terrain, Navigation, seed);
        _pathFinder = new PathFinder(Navigation.CellCount);
        _pathCells = new int[capacity * SimConstants.MaxPathCells];
        _jobs = new ProductionJob[capacity * SimConstants.MaxQueueLength];
        _teams = new TeamState[SimConstants.TeamCount];
        _spatial = new SpatialIndex(capacity, SimConstants.MapExtentMm);
        _visibility = new VisibilityGrid(Navigation.Size);

        // Every faction starts able to build its tier-1 hardware: infantry and
        // structures. Research raises the tier from there.
        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            _teams[team].TechTier = 1;
            _teams[team].AbilityReadyTick = new long[AbilityCatalog.Count];
            ResearchSystem.RefreshModifiers(ref _teams[team]);
        }
    }

    /// <summary>The battlefield height field.</summary>
    public HeightMap Terrain { get; }

    /// <summary>Walkability and cost derived from <see cref="Terrain"/>.</summary>
    public NavGrid Navigation { get; }

    /// <summary>
    /// Surface types and their movement costs, on the same lattice as
    /// <see cref="Navigation"/>. Like the height field it is a pure function of the
    /// seed, so it is reproducible without being hashed every tick.
    /// </summary>
    public TerrainLayer TerrainTypes { get; }

    /// <summary>Uniform grid of live entities, rebuilt each tick for proximity queries.</summary>
    public SpatialIndex Spatial => _spatial;

    /// <summary>Per-team visibility and explored map.</summary>
    public VisibilityGrid Visibility => _visibility;

    /// <summary>How the battle ended; <see cref="GameOutcome.Ongoing"/> while it lasts.</summary>
    public GameOutcome Outcome { get; private set; }

    /// <summary>
    /// The campaign mission this world is playing, or null for a skirmish.
    /// A mission replaces the last-team-standing victory rule with objectives.
    /// </summary>
    public MissionDefinition? Mission => _mission;

    /// <summary>True when a campaign mission is attached.</summary>
    public bool HasMission => _mission is not null;

    /// <summary>Runtime state of the mission's objectives, in definition order.</summary>
    public ReadOnlySpan<ObjectiveState> Objectives => _objectives;

    /// <summary>Mutable objective states, for <see cref="MissionSystem"/>.</summary>
    internal Span<ObjectiveState> ObjectiveStatesSpan => _objectives;

    /// <summary>
    /// Attaches a mission and resets its objective state. Called once, when the
    /// world is built, before the first tick.
    /// </summary>
    public void AttachMission(MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        if (_mission is not null)
        {
            throw new InvalidOperationException("A mission is already attached.");
        }

        _mission = mission;
        _objectives = new ObjectiveState[mission.Objectives.Count];
    }

    /// <summary>Sets the final outcome. Called by <see cref="VictorySystem"/>.</summary>
    internal void SetOutcome(GameOutcome outcome) => Outcome = outcome;

    /// <summary>Per-system timing, for diagnosing frame hitches.</summary>
    public SimProfiler Profiler { get; } = new();

    /// <summary>Seed this world was created with.</summary>
    public ulong Seed { get; }

    /// <summary>Ticks elapsed. Zero before the first <see cref="Step"/>.</summary>
    public long Tick { get; private set; }

    /// <summary>Number of live entities.</summary>
    public int AliveCount { get; private set; }

    /// <summary>Number of slots, live or free.</summary>
    public int Capacity => _entities.Length;

    /// <summary>Direct access to a team's resources and research progress.</summary>
    public ref TeamState TeamRef(int team) => ref _teams[team];

    /// <summary>Copy of a team's resources.</summary>
    public TeamState Team(int team) => _teams[team];

    /// <summary>Production jobs queued at an entity.</summary>
    public ReadOnlySpan<ProductionJob> JobsOf(int slot)
        => _jobs.AsSpan(slot * SimConstants.MaxQueueLength, _entities[slot].QueueLength);

    /// <summary>Mutable reference to one job in an entity's queue.</summary>
    public ref ProductionJob JobRef(int slot, int index)
        => ref _jobs[(slot * SimConstants.MaxQueueLength) + index];

    /// <summary>Appends a job to a building's queue. Returns false when full.</summary>
    public bool AddJob(int slot, UnitKind kind, int totalTicks)
    {
        ref Entity e = ref _entities[slot];

        if (e.QueueLength >= SimConstants.MaxQueueLength)
        {
            return false;
        }

        ref ProductionJob job = ref JobRef(slot, e.QueueLength);
        job.Kind = kind;
        job.TotalTicks = totalTicks;
        job.RemainingTicks = totalTicks;
        e.QueueLength++;
        return true;
    }

    /// <summary>Removes the most recently queued job, refunding nothing.</summary>
    public bool RemoveLastJob(int slot)
    {
        ref Entity e = ref _entities[slot];

        if (e.QueueLength == 0)
        {
            return false;
        }

        e.QueueLength--;
        return true;
    }

    /// <summary>Removes a specific job and shifts the rest forward.</summary>
    public void RemoveJobAt(int slot, int index)
    {
        ref Entity e = ref _entities[slot];

        for (int i = index; i < e.QueueLength - 1; i++)
        {
            JobRef(slot, i) = JobRef(slot, i + 1);
        }

        if (e.QueueLength > 0)
        {
            e.QueueLength--;
        }
    }

    /// <summary>
    /// Simulation random source. Declared as a field, not a property: mutating
    /// methods on a struct must not be invoked through a property, or the
    /// advanced state would be silently discarded.
    /// </summary>
    public Pcg32 Rng;

    /// <summary>Commands waiting to execute, in issue order.</summary>
    public int PendingCommandCount => _commandQueue.Count;

    /// <summary>True while external commands are being logged for a replay.</summary>
    public bool IsRecording => _recording;

    /// <summary>
    /// External commands recorded so far, in issue order.
    /// <para>
    /// Only commands that arrive from outside <see cref="Step"/> are logged: the
    /// AI is part of the simulation and re-derives its own orders from the world
    /// state, so recording them would double them on replay.
    /// </para>
    /// </summary>
    public IReadOnlyList<SimCommandRecord> RecordedCommands => _recordedCommands;

    /// <summary>Begins logging external commands. Clears any previous log.</summary>
    public void StartRecording()
    {
        _recordedCommands.Clear();
        _recording = true;
    }

    /// <summary>Stops logging external commands.</summary>
    public void StopRecording() => _recording = false;

    /// <summary>Queues a command. It runs when its execute tick is reached.</summary>
    public void Enqueue(in SimCommand command)
    {
        if (command.ExecuteTick < Tick)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.ExecuteTick, "Cannot schedule a command in the past.");
        }

        if (_recording && !_insideStep)
        {
            _recordedCommands.Add(new SimCommandRecord(Tick, command));
        }

        _commandQueue.Add(command);
    }

    /// <summary>Queues a move order for the next tick.</summary>
    public void OrderMove(EntityId target, WorldPos destination, int issuerTeam)
        => Enqueue(SimCommand.Move(target, destination, Tick + 1, issuerTeam));

    /// <summary>True when <paramref name="slot"/> holds a live entity.</summary>
    public bool IsAliveSlot(int slot) => (uint)slot < (uint)_entities.Length && _entities[slot].Alive;

    /// <summary>
    /// Direct slot access for systems and for the renderer's read pass.
    /// The caller must have checked <see cref="IsAliveSlot"/> first.
    /// </summary>
    public ref Entity GetRefBySlot(int slot) => ref _entities[slot];

    /// <summary>Resolves a handle to a live entity, or false if it is stale.</summary>
    public bool TryGet(EntityId id, out Entity entity)
    {
        if (TryResolve(id, out int slot))
        {
            entity = _entities[slot];
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>Resolves a handle to a live entity reference.</summary>
    public bool TryGetRef(EntityId id, out EntityId resolved, out int slot)
    {
        if (TryResolve(id, out slot))
        {
            resolved = id;
            return true;
        }

        resolved = EntityId.None;
        return false;
    }

    /// <summary>True when the handle still refers to the entity it was made for.</summary>
    public bool IsValid(EntityId id) => TryResolve(id, out _);

    /// <summary>
    /// Creates an entity in the first free slot. Slot order is deterministic,
    /// so spawn order is part of the replay contract.
    /// </summary>
    public EntityId Spawn(Faction faction, int teamId, UnitKind kind, WorldPos position, Fix32 speedMmPerTick, int health)
    {
        if (_freeCount == 0)
        {
            throw new InvalidOperationException("Entity capacity exhausted.");
        }

        int slot = _freeSlots[--_freeCount];
        ref Entity e = ref _entities[slot];
        int generation = e.Generation;

        e.Alive = true;
        e.Generation = generation;
        e.Faction = faction;
        e.TeamId = teamId;
        e.Kind = kind;
        e.AltitudeMm = UnitCatalog.Flies(kind) ? 60_000 : 0;

        UnitDefinition definition = UnitCatalog.Get(kind);
        int spawnX = position.X;
        int spawnZ = position.Z;

        // A mobile unit that would appear in a lake is moved to the nearest dry
        // ground. Spawning inside impassable terrain would leave it unable to path
        // anywhere at all, which reads as a broken unit rather than a terrain rule.
        if (!definition.IsBuilding && definition.Movement != MovementClass.Air)
        {
            PathContext context = new(definition.Movement, UnitCatalog.GroundPressure(faction, kind));
            int cell = Navigation.IndexOfWorld(new WorldPos(spawnX, 0, spawnZ));

            if (!TerrainTypes.IsPassable(cell, definition.Movement))
            {
                int dry = FindFreeCell(cell, definition.Movement);

                if (dry >= 0)
                {
                    WorldPos centre = Navigation.CentreOf(dry);
                    spawnX = centre.X;
                    spawnZ = centre.Z;
                }
            }
        }

        e.Position = new WorldPos(spawnX, Terrain.SampleHeightMm(spawnX, spawnZ) + e.AltitudeMm, spawnZ);
        e.MoveGoal = e.Position;
        e.HasMoveGoal = false;

        // Completed research is baked in at spawn: armour into hit points, speed
        // into the movement envelope.
        TeamState owner = (uint)teamId < SimConstants.TeamCount ? _teams[teamId] : default;
        int armor = owner.ArmorPermille > 0 ? owner.ArmorPermille : 1_000;
        int speedScale = owner.SpeedPermille > 0 ? owner.SpeedPermille : 1_000;

        e.SpeedMmPerTick = Fix32.FromRaw((int)(((long)speedMmPerTick.Raw * speedScale) / 1_000));
        e.Morale = FactionProfile.For(faction).MoraleFloor;
        e.Health = (health * armor) / 1_000;
        e.Heading = 0;
        e.PathLength = 0;
        e.PathCursor = 0;
        e.QueueLength = 0;
        e.TargetSlot = -1;
        e.AttackCooldown = 0;
        e.HasAttackOrder = false;
        e.Routed = false;
        e.MoraleTargetRaw = e.Morale.Raw;
        e.NeedsPath = false;
        e.PathFailures = 0;

        AliveCount++;
        return new EntityId(slot, generation);
    }

    /// <summary>
    /// Nearest cell a mover can stand on that is not already taken.
    /// <para>
    /// When a whole formation spawns inside a lake, the nearest dry cell is the
    /// same cell for every unit in it, and the whole formation stacks on one point
    /// — which then breaks picking, because the units are literally co-located.
    /// Spreading them out over the first free cells keeps the formation readable.
    /// </para>
    /// </summary>
    private int FindFreeCell(int cell, MovementClass movement)
    {
        int cx = Navigation.CellX(Math.Max(cell, 0));
        int cz = Navigation.CellZ(Math.Max(cell, 0));

        for (int radius = 0; radius <= Navigation.Size; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = Navigation.IndexOf(cx + dx, cz + dz);

                    if (candidate < 0 || !Navigation.IsWalkable(candidate) ||
                        !TerrainTypes.IsPassable(candidate, movement) ||
                        IsCellOccupied(candidate))
                    {
                        continue;
                    }

                    return candidate;
                }
            }
        }

        return -1;
    }

    /// <summary>True when a live entity already stands in this cell.</summary>
    private bool IsCellOccupied(int cell)
    {
        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (_entities[slot].Alive && Navigation.IndexOfWorld(_entities[slot].Position) == cell)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a faction's version of a role moves, for terrain cost and passability.
    /// </summary>
    public static PathContext PathContextFor(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        return new PathContext(definition.Movement, UnitCatalog.GroundPressure(faction, kind));
    }

    /// <summary>
    /// A team's version of the same context, after its completed research. The
    /// mobility doctrine «Βαθιά Μάχη» lowers effective ground pressure, which is
    /// how the Σοβιετικοί turn the mud from a tax into an advantage.
    /// </summary>
    public PathContext PathContextOf(int team, Faction faction, UnitKind kind)
    {
        PathContext context = PathContextFor(faction, kind);

        if ((uint)team >= SimConstants.TeamCount || context.GroundPressurePermille <= 0)
        {
            return context;
        }

        int resistance = _teams[team].TerrainResistancePermille;

        return resistance > 0
            ? context with { GroundPressurePermille = (context.GroundPressurePermille * resistance) / 1_000 }
            : context;
    }

    /// <summary>Waypoints of an entity's current path, in walking order.</summary>
    public ReadOnlySpan<int> PathOf(int slot)
    {
        ref Entity e = ref _entities[slot];
        return _pathCells.AsSpan(slot * SimConstants.MaxPathCells, Math.Min(e.PathLength, SimConstants.MaxPathCells));
    }

    /// <summary>
    /// Recomputes a path from the entity's current position to its move goal.
    /// Called when a long route is walked in legs.
    /// </summary>
    /// <returns>True when at least one waypoint was found.</returns>
    public bool RepathFrom(int slot)
    {
        ref Entity e = ref _entities[slot];

        if (!e.HasMoveGoal)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return false;
        }

        int start = Navigation.IndexOfWorld(e.Position);
        PathContext context = PathContextOf(e.TeamId, e.Faction, e.Kind);
        int goal = Navigation.NearestWalkable(Navigation.IndexOfWorld(e.MoveGoal), TerrainTypes, context);

        if (goal < 0)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return false;
        }

        // Arriving inside the goal cell needs no path at all.
        if (start == goal)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return true;
        }

        Span<int> path = _pathCells.AsSpan(slot * SimConstants.MaxPathCells, SimConstants.MaxPathCells);
        int length = _pathFinder.FindPath(Navigation, TerrainTypes, context, start, goal, path);

        if (length > 1)
        {
            // Straighten the route: raw A* output follows cell centres and reads
            // as a zigzag. Smoothing writes back over the same buffer.
            length = Navigation.Smooth(path, length, start, path, TerrainTypes, context);
        }

        e.PathLength = length;
        e.PathCursor = 0;
        return length > 0;
    }

    /// <summary>Destroys an entity and recycles its slot with a new generation.</summary>
    public bool Despawn(EntityId id)
    {
        if (!TryResolve(id, out int slot))
        {
            return false;
        }

        ref Entity e = ref _entities[slot];

        // Mission objectives ask "how many enemy structures have you destroyed",
        // so the count is kept as state rather than inferred from a baseline:
        // that way a rebuilt structure does not undo the progress.
        if (UnitCatalog.Get(e.Kind).IsBuilding && (uint)e.TeamId < SimConstants.TeamCount)
        {
            _teams[e.TeamId].StructuresLost++;
        }

        e.Alive = false;
        e.Generation = id.Generation + 1;
        _freeSlots[_freeCount++] = slot;
        AliveCount--;
        return true;
    }

    /// <summary>Applies every queued command whose execute tick has arrived.</summary>
    public int ApplyPendingCommands()
    {
        int applied = 0;
        int write = 0;
        int count = _commandQueue.Count;

        // Single pass with a write cursor: commands that are not due yet are
        // compacted to the front, keeping their relative order. Removing with
        // RemoveAt inside the loop would be quadratic in the queue length, which
        // matters once a player can order hundreds of units in one tick.
        for (int read = 0; read < count; read++)
        {
            SimCommand command = _commandQueue[read];

            if (command.ExecuteTick > Tick)
            {
                _commandQueue[write++] = command;
                continue;
            }

            if (Apply(command))
            {
                applied++;
            }
        }

        if (write < count)
        {
            _commandQueue.RemoveRange(write, count - write);
        }

        return applied;
    }

    /// <summary>Advances the simulation by exactly one tick.</summary>
    public void Step()
    {
        // Tick is incremented first so that a command stamped "next tick" at
        // Tick N executes on the very next Step, and so that Tick always counts
        // the number of ticks already simulated.
        Tick++;
        Profiler.Begin();

        // Commands issued from inside a tick come from systems such as the AI and
        // are not recorded: they are re-derived on replay from the same state.
        _insideStep = true;

        try
        {
            ApplyPendingCommands();
            Profiler.Mark(ref Profiler.Commands, ref Profiler.WorstCommands);
            AiSystem.Tick(this);
            Profiler.Mark(ref Profiler.Ai, ref Profiler.WorstAi);
            _spatial.Rebuild(this);
            Profiler.Mark(ref Profiler.Spatial, ref Profiler.WorstSpatial);
            VisionSystem.Tick(this);
            Profiler.Mark(ref Profiler.Vision, ref Profiler.WorstVision);
            EconomySystem.Tick(this);
            Profiler.Mark(ref Profiler.Economy, ref Profiler.WorstEconomy);

            if (TerrainTypes.HasWeather)
            {
                TerrainTypes.ExpireWeather(Tick);
            }

            ConstructionSystem.Tick(this);

            ChurnSystem.Tick(this);
            HazardSystem.Tick(this);
            ResearchSystem.Tick(this);
            Profiler.Mark(ref Profiler.Research, ref Profiler.WorstResearch);
            PrototypeSystem.Tick(this);
            Profiler.Mark(ref Profiler.Research, ref Profiler.WorstResearch);
            ProductionSystem.Tick(this);
            Profiler.Mark(ref Profiler.Production, ref Profiler.WorstProduction);
            PathingSystem.Tick(this);
            Profiler.Mark(ref Profiler.Pathing, ref Profiler.WorstPathing);
            MoraleSystem.Tick(this);
            Profiler.Mark(ref Profiler.Morale, ref Profiler.WorstMorale);
            CombatSystem.Tick(this);
            Profiler.Mark(ref Profiler.Combat, ref Profiler.WorstCombat);
            MovementSystem.Tick(this);
            Profiler.Mark(ref Profiler.Movement, ref Profiler.WorstMovement);

            // A mission decides its own outcome; the skirmish rule only applies
            // when there is no mission attached.
            if (_mission is not null)
            {
                MissionSystem.Tick(this);
            }
            else
            {
                VictorySystem.Tick(this);
            }

            Profiler.EndStep(Tick);
        }
        finally
        {
            _insideStep = false;
        }
    }

    /// <summary>Advances the simulation by <paramref name="count"/> ticks.</summary>
    public void RunTicks(long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Must not be negative.");
        }

        for (long i = 0; i < count; i++)
        {
            Step();
        }
    }

    private bool Apply(in SimCommand command)
    {
        // Off-map support arrives from outside the battlefield and has no target
        // entity at all, so it is dispatched before the resolution check that every
        // other command needs.
        if (command.Kind == SimCommandKind.UseAbility)
        {
            return TryUseAbility(command.Ability, command.Destination, command.IssuerTeam);
        }

        // A bridge is engineering work on the ground, not an order to a unit.
        if (command.Kind == SimCommandKind.BuildBridge)
        {
            return TryBuildBridge(command.Destination, command.IssuerTeam);
        }

        if (!TryResolve(command.Target, out int slot))
        {
            return false;
        }

        ref Entity e = ref _entities[slot];

        // A licence names a building owned by the *recipient*, so it cannot go
        // through the "your own units only" check below.
        if (command.Kind == SimCommandKind.LicenceProduction)
        {
            return TryLicence(command.Target, command.UnitKind, command.IssuerTeam);
        }

        // A team may only command its own units.
        if (e.TeamId != command.IssuerTeam)
        {
            return false;
        }

        switch (command.Kind)
        {
            case SimCommandKind.Move:
                // Ground units are pinned to the terrain surface at the destination;
                // aircraft keep the altitude they were ordered to.
                int goalY = UnitCatalog.Flies(e.Kind)
                    ? command.Destination.Y
                    : Terrain.SampleHeightMm(command.Destination.X, command.Destination.Z) + e.AltitudeMm;

                e.MoveGoal = new WorldPos(command.Destination.X, goalY, command.Destination.Z);
                e.HasMoveGoal = true;
                e.PathLength = 0;
                e.PathCursor = 0;
                e.PathFailures = 0;

                // Route on a later tick: path searches are budgeted.
                e.NeedsPath = true;
                return true;

            case SimCommandKind.Stop:
                e.HasMoveGoal = false;
                e.MoveGoal = e.Position;
                e.PathLength = 0;
                e.PathCursor = 0;
                e.NeedsPath = false;
                return true;

            case SimCommandKind.QueueUnit:
                return TryQueueUnit(slot, ref e, command.UnitKind);

            case SimCommandKind.CancelProduction:
                return RemoveLastJob(slot);

            case SimCommandKind.Research:
                return TryStartResearch(ref e, command.Tech);

            case SimCommandKind.Attack:
                return TryAttack(slot, ref e, command.AttackTarget);

            case SimCommandKind.ApproveDesign:
                return TryApproveDesign(ref e, command.UnitKind);

            default:
                return false;
        }
    }

    /// <summary>The faction that owns a team slot.</summary>
    public static Faction FactionOfTeam(int team) => team switch
    {
        0 => Faction.Soviet,
        1 => Faction.Chinese,
        2 => Faction.Western,
        _ => Faction.None,
    };

    /// <summary>True when two teams may not attack each other.</summary>
    public static bool AreAllied(int a, int b) => a == b || (a is 0 or 1 && b is 0 or 1);

    /// <summary>
    /// True when a team may build a role: either it researched the tier, or an
    /// ally licensed the design to it. A licence deliberately bypasses the
    /// faction's tech ceiling — that is the whole point of the alliance.
    /// <para>
    /// Σοβιετικοί factory output needs one more thing: the design bureau must have
    /// run the prototype first. Infantry is exempt because it comes from the
    /// command centre, and a licence is exempt because an ally has already proven
    /// the design. This is what stops a Σοβιετικοί army from pivoting.
    /// </para>
    /// </summary>
    public bool CanBuild(int team, UnitKind kind)
    {
        if ((uint)team >= SimConstants.TeamCount || !UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        ref TeamState state = ref _teams[team];
        uint bit = 1u << (int)kind;

        if ((state.LicenceMask & bit) != 0)
        {
            return true;
        }

        if (!UnitCatalog.IsUnlocked(FactionOfTeam(team), kind, state.TechTier, state.TechMask))
        {
            return false;
        }

        // A capped design is a capability, not a unit type: the team may field a
        // limited number, counting whatever is already on the way.
        if (UnitCatalog.Get(kind).MaxAlive > 0 && CountOf(team, kind) >= UnitCatalog.Get(kind).MaxAlive)
        {
            return false;
        }

        return FactionOfTeam(team) != Faction.Soviet
            || definition.ProducedAt != UnitKind.Factory
            || (state.ApprovedMask & bit) != 0;
    }

    /// <summary>
    /// Live and queued entities of a role for a team. Both count against a cap:
    /// otherwise a player could queue ten of a prototype in one tick and only be
    /// stopped once they started appearing.
    /// </summary>
    public int CountOf(int team, UnitKind kind)
    {
        int total = 0;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (_entities[slot].Alive && _entities[slot].TeamId == team && _entities[slot].Kind == kind)
            {
                total++;
            }

            ReadOnlySpan<ProductionJob> queued = JobsOf(slot);

            for (int job = 0; job < queued.Length; job++)
            {
                if (queued[job].Kind == kind)
                {
                    total++;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// True when a team could call in an ability right now: right faction, era
    /// reached, prerequisite project and structure in place, off cooldown and
    /// affordable. Split out from the execution so the interface can grey out a
    /// button for the right reason.
    /// </summary>
    public bool CanUseAbility(int team, AbilityId ability, out string reason)
    {
        reason = string.Empty;

        if ((uint)team >= SimConstants.TeamCount || !AbilityCatalog.TryGet(ability, out AbilityDefinition definition))
        {
            reason = "άγνωστη ικανότητα";
            return false;
        }

        ref TeamState state = ref _teams[team];
        Faction faction = FactionOfTeam(team);

        if (definition.Faction != Faction.None && definition.Faction != faction)
        {
            reason = "δεν ανήκει σε αυτή την παράταξη";
            return false;
        }

        if (state.TechTier < definition.RequiredTechTier)
        {
            reason = $"χρειάζεται τεχνολογία {definition.RequiredTechTier}";
            return false;
        }

        if (definition.RequiredTech != TechId.None && !TechCatalog.IsCompleted(state.TechMask, definition.RequiredTech))
        {
            reason = "χρειάζεται έρευνα";
            return false;
        }

        if (definition.RequiredStructure != UnitKind.None && !HasStructure(team, definition.RequiredStructure))
        {
            reason = $"χρειάζεται {definition.RequiredStructure}";
            return false;
        }

        int index = AbilityCatalog.IndexOf(ability);

        if (index >= 0 && state.AbilityReadyTick is not null && Tick < state.AbilityReadyTick[index])
        {
            reason = $"σε αναμονή {(state.AbilityReadyTick[index] - Tick) / 20}δ";
            return false;
        }

        if (state.Materials < definition.MaterialCost)
        {
            reason = $"λείπουν {definition.MaterialCost - state.Materials} Π";
            return false;
        }

        return true;
    }

    /// <summary>How close a hostile unit must be to spot a stealthed one, in millimetres.</summary>
    public const int DetectionRadiusMm = 40_000;

    /// <summary>Ticks a stealthed unit stays visible after it fires.</summary>
    public const int StealthRevealTicks = 100;

    /// <summary>
    /// True when <paramref name="slot"/> cannot be seen by <paramref name="viewerTeam"/>:
    /// it is stealthed, it is not the viewer's own, it has not just fired, and no
    /// unit of the viewer's team is close enough to detect it.
    /// </summary>
    public bool IsHiddenFrom(int viewerTeam, int slot)
    {
        if (!IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity target = ref _entities[slot];

        if (target.TeamId == viewerTeam || !UnitCatalog.Get(target.Kind).Stealthy)
        {
            return false;
        }

        if (target.RevealedUntilTick > Tick)
        {
            return false;
        }

        return !HasDetectorNear(viewerTeam, target.Position);
    }

    /// <summary>True when any live unit of the team is within detection range.</summary>
    private bool HasDetectorNear(int team, WorldPos position)
    {
        long radiusSquared = (long)DetectionRadiusMm * DetectionRadiusMm;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (!IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity watcher = ref _entities[slot];

            if (watcher.TeamId != team)
            {
                continue;
            }

            if (watcher.Position.DistanceSquaredTo(position) <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the slot holds a structure that has finished being raised.</summary>
    public bool IsComplete(int slot)
        => IsAliveSlot(slot) && _entities[slot].ConstructionTicksRemaining <= 0;

    /// <summary>True when a team has at least one live structure of a role.</summary>
    public bool HasStructure(int team, UnitKind kind)
    {
        for (int slot = 0; slot < _entities.Length; slot++)
        {
            ref Entity entity = ref _entities[slot];

            // An unfinished structure does not count: it is not doing anything yet,
            // so a team that has one still needs the finished article.
            if (entity.Alive && entity.TeamId == team && entity.Kind == kind &&
                entity.ConstructionTicksRemaining <= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves an off-map strike. The blast is applied in ascending slot order so
    /// the outcome is independent of the order entities happen to be stored in.
    /// </summary>
    private bool TryUseAbility(AbilityId ability, WorldPos target, int team)
    {
        if (!CanUseAbility(team, ability, out _))
        {
            return false;
        }

        AbilityCatalog.TryGet(ability, out AbilityDefinition definition);

        ref TeamState state = ref _teams[team];
        state.Materials -= definition.MaterialCost;

        int index = AbilityCatalog.IndexOf(ability);

        if (index >= 0 && state.AbilityReadyTick is not null)
        {
            state.AbilityReadyTick[index] = Tick + definition.CooldownTicks;
        }

        if (definition.WeatherDurationTicks > 0)
        {
            TerrainTypes.ApplyWeather(
                target.X,
                target.Z,
                definition.RadiusMm,
                TerrainType.Mud,
                Tick + definition.WeatherDurationTicks);
        }

        if (definition.Damage <= 0)
        {
            return true;
        }

        long radiusSquared = (long)definition.RadiusMm * definition.RadiusMm;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (!IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity victim = ref _entities[slot];

            if (!definition.DamagesFriendlies && victim.TeamId == team)
            {
                continue;
            }

            int dx = victim.Position.X - target.X;
            int dz = victim.Position.Z - target.Z;

            if (((long)dx * dx) + ((long)dz * dz) > radiusSquared)
            {
                continue;
            }

            if (victim.Health <= definition.Damage)
            {
                Despawn(new EntityId(slot, victim.Generation));
            }
            else
            {
                victim.Health -= definition.Damage;
            }
        }

        return true;
    }

    /// <summary>Materials a bridge costs.</summary>
    public const int BridgeMaterials = 150;

    /// <summary>Energy a bridge costs.</summary>
    public const int BridgeEnergy = 40;

    /// <summary>Water a bridge costs — concrete needs a great deal of it.</summary>
    public const int BridgeWater = 30;

    /// <summary>Furthest a span will reach from where it is placed, in cells.</summary>
    public const int MaxBridgeSpan = 24;

    /// <summary>
    /// True when a team could build a crossing at this position, and why not if it
    /// could not. Split out from the work so the interface can grey a button out for
    /// the right reason.
    /// </summary>
    public bool CanBuildBridge(int team, WorldPos target, out string reason)
    {
        reason = string.Empty;

        if ((uint)team >= SimConstants.TeamCount)
        {
            reason = "άγνωστη ομάδα";
            return false;
        }

        // Bridges are made in a factory. A team with no industry cannot span a river.
        if (!HasStructure(team, UnitKind.Factory))
        {
            reason = "χρειάζεται εργοστάσιο";
            return false;
        }

        int cell = TerrainTypes.IndexOfWorld(target.X, target.Z);

        if (cell < 0)
        {
            reason = "έξω από τον χάρτη";
            return false;
        }

        if (!IsWater(TerrainTypes.TypeAt(cell)))
        {
            reason = "χρειάζεται νερό";
            return false;
        }

        ref TeamState state = ref _teams[team];

        if (state.Materials < BridgeMaterials || state.Energy < BridgeEnergy || state.Water < BridgeWater)
        {
            reason = "λείπουν πόροι";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Spans the water at a position.
    /// <para>
    /// The span runs along whichever axis stays wet longest, so placing it on a river
    /// crosses the river rather than running along the bank. Every water cell it
    /// covers becomes a ford, which is the same surface the generator carves for its
    /// own crossings — a bridge is a ford a player chose the site of.
    /// </para>
    /// <para>
    /// Permanent once built. A bridge is engineering work, not a unit: there is
    /// nothing to shoot that would put the river back, and modelling demolition would
    /// need the original depths stored per span for no gameplay the design asks for.
    /// </para>
    /// </summary>
    private bool TryBuildBridge(WorldPos target, int team)
    {
        if (!CanBuildBridge(team, target, out _))
        {
            return false;
        }

        int cell = TerrainTypes.IndexOfWorld(target.X, target.Z);
        int x = Navigation.CellX(cell);
        int z = Navigation.CellZ(cell);

        int across = 1 + SpanAlong(x, z, 1, 0) + SpanAlong(x, z, -1, 0);
        int down = 1 + SpanAlong(x, z, 0, 1) + SpanAlong(x, z, 0, -1);

        // Span the narrow way: a crossing should cross.
        bool horizontal = across <= down;
        int stepX = horizontal ? 1 : 0;
        int stepZ = horizontal ? 0 : 1;
        int cells = horizontal ? across : down;

        if (cells > MaxBridgeSpan)
        {
            return false;
        }

        ref TeamState state = ref _teams[team];
        state.Materials -= BridgeMaterials;
        state.Energy -= BridgeEnergy;
        state.Water -= BridgeWater;

        for (int direction = -1; direction <= 1; direction += 2)
        {
            int cx = x;
            int cz = z;

            for (int step = 0; step <= MaxBridgeSpan; step++)
            {
                int index = Navigation.IndexOf(cx, cz);

                if (index < 0 || !IsWater(TerrainTypes.TypeAt(index)))
                {
                    break;
                }

                TerrainTypes.SetType(index, TerrainType.ShallowWater);

                cx += stepX * direction;
                cz += stepZ * direction;
            }
        }

        return true;
    }

    /// <summary>How many water cells lie in a direction before dry land.</summary>
    private int SpanAlong(int cellX, int cellZ, int stepX, int stepZ)
    {
        int count = 0;
        int x = cellX + stepX;
        int z = cellZ + stepZ;

        for (int i = 0; i < MaxBridgeSpan; i++)
        {
            int index = Navigation.IndexOf(x, z);

            if (index < 0 || !IsWater(TerrainTypes.TypeAt(index)))
            {
                break;
            }

            count++;
            x += stepX;
            z += stepZ;
        }

        return count;
    }

    private static bool IsWater(TerrainType type)
        => type is TerrainType.ShallowWater or TerrainType.DeepWater;

    /// <summary>How far a spawn site may be pushed to find solid ground, in cells.</summary>
    private const int SpawnSearchRadius = 8;

    /// <summary>
    /// The same radius, for tests that want to assert a site was moved to somewhere
    /// near rather than somewhere arbitrary.
    /// </summary>
    public const int MaxSpawnSearchCells = SpawnSearchRadius;

    /// <summary>
    /// Moves a spawn position onto solid ground: the nearest cell that is neither
    /// water, nor lava, nor a cliff too steep for the navigation grid, searched
    /// outwards from where it was wanted.
    /// <para>
    /// Produced structures and units are placed at a fixed offset from whatever made
    /// them, and that offset knows nothing about the map — so a factory on a shoreline
    /// would eventually put its next building in the lake, on whichever tick the
    /// offset happened to point that way. A building in the sea is not merely odd: it
    /// is a structure the player cannot reach, cannot defend and cannot use.
    /// </para>
    /// <para>
    /// Nearest first, ring by ring, so a site pushed off a shore lands as close to
    /// where it was meant to be as the ground allows. Deterministic: it reads the
    /// terrain and the grid and nothing else.
    /// </para>
    /// <para>
    /// Bridges are unaffected in the way that matters — a bridge cell is shallow
    /// water, which is a ford rather than solid ground, so nothing is ever built on
    /// one.
    /// </para>
    /// </summary>
    public WorldPos LegalSpawnSite(WorldPos wanted)
    {
        int cell = Navigation.IndexOfWorld(wanted);

        if (cell < 0 || IsSolidGround(cell))
        {
            return wanted;
        }

        int centreX = Navigation.CellX(cell);
        int centreZ = Navigation.CellZ(cell);

        for (int radius = 1; radius <= SpawnSearchRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // The ring only: everything inside it was searched already.
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = Navigation.IndexOf(centreX + dx, centreZ + dz);

                    if (candidate >= 0 && IsSolidGround(candidate))
                    {
                        return Navigation.CentreOf(candidate);
                    }
                }
            }
        }

        // Nowhere solid within reach. Returning the wanted position keeps the
        // behaviour visible rather than silently moving the unit somewhere arbitrary.
        return wanted;
    }

    /// <summary>
    /// Radius of the patch of ground a base is judged on, in navigation cells.
    /// <para>
    /// Five cells, and the number comes from the base rather than from taste: the four
    /// structures a scenario plants around a base stand 45 m from its centre, and a
    /// navigation cell is 9 375 mm across, so the factory and the power plant are 4.8
    /// cells out. A patch of radius five is the smallest square that holds all four of
    /// them, the ground between them, and a cell of margin around the outside — which
    /// is the room a player needs for what they will build next.
    /// </para>
    /// </summary>
    public const int BaseSiteRadiusCells = 5;

    /// <summary>
    /// Radius of the core of that patch, in cells, which must be solid all through.
    /// <para>
    /// The patch is allowed to carry a pond at its edge; the yard is not. Two cells is
    /// 18.75 m of ground in every direction from the centre — where the headquarters
    /// itself stands and where its units deploy — and a base with water in the middle
    /// of that is a base whose buildings cannot be reached even though "most" of its
    /// surroundings are dry.
    /// </para>
    /// </summary>
    public const int BaseSiteCoreCells = 2;

    /// <summary>
    /// How much of the patch has to be solid, in permille.
    /// <para>
    /// Nine tenths. Requiring every one of the 121 cells was tried first, and it fails
    /// outright on two bases out of the hundred and twenty a forty-seed sweep builds:
    /// where the intended corner is a large lake there is no all-land patch within reach
    /// at all, and a base that finds nothing is worse off than one with a pond at the
    /// edge of its yard. At the other end, a threshold loose enough to let a lake run
    /// through the middle of the patch is how a base ends up split in two by water it
    /// cannot cross. The core above is what keeps water off the base itself.
    /// </para>
    /// </summary>
    public const int BaseSiteMinSolidPermille = 900;

    /// <summary>
    /// How far a base site may be pushed to find that patch, in navigation cells.
    /// <para>
    /// Thirty-two cells is 300 m, half the map: far enough to clear the widest lake the
    /// generator makes, and no further. Past that the base is no longer where the
    /// faction is supposed to be, and a search that keeps walking would quietly redraw
    /// the match — which is why it stops and says so instead.
    /// </para>
    /// </summary>
    public const int BaseSearchRadiusCells = 32;

    /// <summary>Cells in the patch a base is judged on.</summary>
    public static int BaseSitePatchCells
        => ((BaseSiteRadiusCells * 2) + 1) * ((BaseSiteRadiusCells * 2) + 1);

    /// <summary>How many cells of a base's patch are solid ground.</summary>
    public int BaseSiteSolidCells(WorldPos centre) => SolidPatchCells(centre, out _);

    /// <summary>
    /// True when the ground around a position will hold a base: a solid core, and most
    /// of the patch around it solid too.
    /// </summary>
    public bool IsBaseSite(WorldPos centre)
    {
        int solid = SolidPatchCells(centre, out bool coreSolid);

        return coreSolid && (solid * 1_000) >= (BaseSitePatchCells * BaseSiteMinSolidPermille);
    }

    /// <summary>
    /// Finds a site where a base can actually stand, searching outwards from
    /// <paramref name="wanted"/> ring by ring, and returns false when there is none
    /// within <see cref="BaseSearchRadiusCells"/>.
    /// <para>
    /// A scenario asks for a base at a hardcoded position, and the terrain underneath
    /// it comes from the seed — so whether the faction's headquarters stands on land
    /// is a matter of luck, and on the standard seed the luck runs out: the Soviet
    /// base and its power plant, factory and design bureau all stand in deep water,
    /// which is a base the player can neither reach nor use. Nearest first, so a base
    /// pushed off a shoreline lands as close to where it was meant to be as the ground
    /// allows.
    /// </para>
    /// <para>
    /// Deterministic: it reads the terrain, the navigation grid and nothing else — no
    /// RNG, no clock — so the same seed rebuilds the same bases in the same places.
    /// </para>
    /// </summary>
    /// <param name="wanted">Where the base was asked to stand.</param>
    /// <param name="site">
    /// Where it can stand. When the answer is false this is the nearest single cell of
    /// solid ground rather than the water it was asked for, which is what the rest of
    /// the game falls back to.
    /// </param>
    public bool TryFindBaseSite(WorldPos wanted, out WorldPos site)
        => TryFindBaseSite(wanted, BaseSearchRadiusCells, out site);

    /// <summary>
    /// The same search with an explicit reach, for callers that want to say how far a
    /// base may be moved — and for the test that checks what happens when nothing is
    /// found.
    /// </summary>
    public bool TryFindBaseSite(WorldPos wanted, int searchRadiusCells, out WorldPos site)
    {
        if (IsBaseSite(wanted))
        {
            // The position asked for is good, so it is kept exactly: a base nudged to
            // the nearest cell centre for no reason is a scenario that moves for every
            // seed, and a hash that moves with it.
            site = wanted;
            return true;
        }

        int cell = Navigation.IndexOfWorld(wanted);
        int centreX = Navigation.CellX(Math.Max(cell, 0));
        int centreZ = Navigation.CellZ(Math.Max(cell, 0));

        for (int radius = 1; radius <= searchRadiusCells; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // The ring only: everything inside it was searched already.
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = Navigation.IndexOf(centreX + dx, centreZ + dz);

                    if (candidate < 0)
                    {
                        continue;
                    }

                    WorldPos centre = Navigation.CentreOf(candidate);

                    if (IsBaseSite(centre))
                    {
                        site = centre;
                        return true;
                    }
                }
            }
        }

        // Nowhere with room within reach. Falling back to the nearest solid cell keeps
        // the base out of the water, and returning false is the caller's notice that
        // the patch it wanted was not there.
        site = LegalSpawnSite(wanted);
        return false;
    }

    /// <summary>
    /// Counts the solid cells of the patch around a position, and reports whether its
    /// core was solid in full.
    /// </summary>
    private int SolidPatchCells(WorldPos centre, out bool coreSolid)
    {
        int cell = Navigation.IndexOfWorld(centre);
        int centreX = Navigation.CellX(Math.Max(cell, 0));
        int centreZ = Navigation.CellZ(Math.Max(cell, 0));
        int solid = 0;

        coreSolid = true;

        for (int dz = -BaseSiteRadiusCells; dz <= BaseSiteRadiusCells; dz++)
        {
            for (int dx = -BaseSiteRadiusCells; dx <= BaseSiteRadiusCells; dx++)
            {
                if (IsSolidGround(Navigation.IndexOf(centreX + dx, centreZ + dz)))
                {
                    solid++;
                }
                else if (Math.Abs(dx) <= BaseSiteCoreCells && Math.Abs(dz) <= BaseSiteCoreCells)
                {
                    coreSolid = false;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// True when a navigation cell is ground a unit can stand on: not water, not lava,
    /// and not a cliff.
    /// <para>
    /// A cliff is dry ground and still no place for a headquarters, and a mover that
    /// cannot enter the cell cannot leave it either — which is why walkability is part
    /// of the question rather than left to the surface type. The two grids are not the
    /// same size, so the cell's own centre locates the terrain cell underneath it:
    /// indexing one grid with the other's number is the kind of mistake that reads as
    /// working code on a square map.
    /// </para>
    /// </summary>
    private bool IsSolidGround(int navCell)
    {
        if (navCell < 0 || !Navigation.IsWalkable(navCell))
        {
            return false;
        }

        WorldPos centre = Navigation.CentreOf(navCell);
        int terrainCell = TerrainTypes.IndexOfWorld(centre.X, centre.Z);

        if (terrainCell < 0)
        {
            return false;
        }

        TerrainType type = TerrainTypes.TypeAt(terrainCell);

        return type is not (TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava);
    }

    /// <summary>Cost and time of a prototype run, as a multiple of the unit's own.</summary>
    public const int PrototypeCostPermille = 2_000;

    /// <summary>
    /// Starts a prototype run for a design at a design bureau. Σοβιετικοί only,
    /// and only for factory output: infantry comes from the command centre and
    /// needs no proving. On completion the design is approved for production.
    /// </summary>
    private bool TryApproveDesign(ref Entity bureau, UnitKind kind)
    {
        if (bureau.Kind != UnitKind.DesignBureau || (uint)bureau.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        if (bureau.Faction != Faction.Soviet || !UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        if (definition.ProducedAt != UnitKind.Factory)
        {
            return false;
        }

        ref TeamState team = ref _teams[bureau.TeamId];
        uint bit = 1u << (int)kind;

        if ((team.ApprovedMask & bit) != 0 || team.IsPrototyping)
        {
            return false;
        }

        // A licensed design is already proven by the ally who gave it.
        if ((team.LicenceMask & bit) == 0 && !UnitCatalog.IsUnlocked(bureau.Faction, kind, team.TechTier))
        {
            return false;
        }

        int materials = (UnitCatalog.MaterialCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;
        int energy = (UnitCatalog.EnergyCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;
        int water = (UnitCatalog.WaterCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;

        if (team.Materials < materials || team.Energy < energy || team.Water < water)
        {
            return false;
        }

        team.Materials -= materials;
        team.Energy -= energy;
        team.Water -= water;

        int ticks = (UnitCatalog.BuildTicks(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;

        team.PrototypeKind = kind;
        team.PrototypeTicksTotal = Math.Max(1, ticks);
        team.PrototypeTicksRemaining = team.PrototypeTicksTotal;
        return true;
    }

    /// <summary>Grants an allied team a licence for a design the issuer already has.</summary>
    private bool TryLicence(EntityId recipientBuilding, UnitKind kind, int issuerTeam)
    {
        if (!TryResolve(recipientBuilding, out int slot) || (uint)issuerTeam >= SimConstants.TeamCount)
        {
            return false;
        }

        ref Entity building = ref _entities[slot];

        if (issuerTeam == building.TeamId || !AreAllied(issuerTeam, building.TeamId))
        {
            return false;
        }

        if (!UnitCatalog.TryGet(kind, out _))
        {
            return false;
        }

        // The licensor must actually possess the design.
        if (!UnitCatalog.IsUnlocked(FactionOfTeam(issuerTeam), kind, _teams[issuerTeam].TechTier))
        {
            return false;
        }

        _teams[building.TeamId].LicenceMask |= 1u << (int)kind;
        return true;
    }

    /// <summary>
    /// Locks a unit onto an enemy. If the target is out of range the unit is sent
    /// towards it, and the combat system keeps the approach updated as the enemy
    /// moves.
    /// </summary>
    private bool TryAttack(int slot, ref Entity attacker, EntityId victim)
    {
        UnitDefinition weapon = UnitCatalog.Get(attacker.Kind);

        if (!weapon.IsArmed || !TryResolve(victim, out int victimSlot))
        {
            return false;
        }

        ref Entity target = ref _entities[victimSlot];

        if (target.TeamId == attacker.TeamId)
        {
            return false;
        }

        attacker.TargetSlot = victimSlot;
        attacker.HasAttackOrder = true;

        long dx = attacker.Position.X - target.Position.X;
        long dz = attacker.Position.Z - target.Position.Z;
        bool inRange = ((dx * dx) + (dz * dz)) <= ((long)weapon.AttackRangeMm * weapon.AttackRangeMm);

        if (!inRange)
        {
            attacker.MoveGoal = target.Position;
            attacker.HasMoveGoal = true;
            attacker.PathLength = 0;
            attacker.PathCursor = 0;
            attacker.PathFailures = 0;
            attacker.NeedsPath = true;
        }

        return true;
    }

    /// <summary>
    /// Queues a unit if the building can make it, the team has reached the
    /// required tech tier, and it can pay. Costs are taken up front so a queue
    /// cannot be filled with resources the team does not have.
    /// </summary>
    private bool TryQueueUnit(int slot, ref Entity building, UnitKind kind)
    {
        if (!UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        if (definition.ProducedAt != building.Kind || (uint)building.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        ref TeamState team = ref _teams[building.TeamId];

        if (!CanBuild(building.TeamId, kind))
        {
            return false;
        }

        int materials = UnitCatalog.MaterialCost(building.Faction, kind);
        int energy = UnitCatalog.EnergyCost(building.Faction, kind);
        int water = UnitCatalog.WaterCost(building.Faction, kind);

        // Every job costs all three: minerals for the hull, power for the tools
        // and water for the cooling and the crews. Running one dry stops
        // construction rather than silently producing for free.
        if (team.Materials < materials || team.Energy < energy || team.Water < water)
        {
            return false;
        }

        int ticks = UnitCatalog.BuildTicks(building.Faction, kind);
        int production = team.ProductionPermille > 0 ? team.ProductionPermille : 1_000;
        ticks = Math.Max(1, (ticks * 1_000) / production);

        if (!AddJob(slot, kind, ticks))
        {
            return false;
        }

        team.Materials -= materials;
        team.Energy -= energy;
        team.Water -= water;
        return true;
    }

    /// <summary>Starts a research project at a design bureau.</summary>
    private bool TryStartResearch(ref Entity building, TechId tech)
    {
        if (building.Kind != UnitKind.DesignBureau || (uint)building.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        if (!TechCatalog.TryGet(tech, out TechProject project) || project.Faction != building.Faction)
        {
            return false;
        }

        ref TeamState team = ref _teams[building.TeamId];

        if (team.IsResearching || TechCatalog.IsCompleted(team.TechMask, tech) || project.RequiredTier > team.TechTier)
        {
            return false;
        }

        if (project.Prerequisite != TechId.None && !TechCatalog.IsCompleted(team.TechMask, project.Prerequisite))
        {
            return false;
        }

        if (team.Materials < project.Cost)
        {
            return false;
        }

        team.Materials -= project.Cost;
        team.ResearchingTech = tech;
        team.ResearchTargetTier = project.RequiredTier;
        team.ResearchTicksTotal = TechCatalog.TicksFor(building.Faction, project);
        team.ResearchTicksRemaining = team.ResearchTicksTotal;
        return true;
    }

    private bool TryResolve(EntityId id, out int slot)
    {
        slot = id.Slot;

        if (id.IsNone || (uint)slot >= (uint)_entities.Length)
        {
            return false;
        }

        ref Entity e = ref _entities[slot];
        return e.Alive && e.Generation == id.Generation;
    }
}
