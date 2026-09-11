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
    private readonly PowerSystem.RadarNetwork _radars;
    private readonly List<SimCommand> _commandQueue = new();
    private readonly List<SimCommandRecord> _recordedCommands = [];
    private readonly List<MissionMessage> _missionMessages = [];
    private ObjectiveState[] _objectives = [];
    private TriggerState[] _triggers = [];
    private uint[] _missionFlags = [];
    private MissionDefinition? _mission;
    private bool _recording;
    private bool _insideStep;
    private int _freeCount;

    /// <summary>Creates a world with the maximum entity capacity, playing the standard skirmish.</summary>
    public SimWorld(ulong seed)
        : this(seed, SimConstants.MaxEntities, MatchRoster.StandardSkirmish)
    {
    }

    /// <summary>Creates a world with an explicit capacity, playing the standard skirmish.</summary>
    public SimWorld(ulong seed, int capacity)
        : this(seed, capacity, MatchRoster.StandardSkirmish)
    {
    }

    /// <summary>Creates a world with an explicit capacity and an explicit match.</summary>
    /// <param name="seed">Seed every generated thing derives from.</param>
    /// <param name="capacity">Entity slots the world is built with.</param>
    /// <param name="roster">
    /// Who is playing: which teams are in the match, what faction each one plays and which side each
    /// one is on. It is fixed for the life of the world — an alliance that could change mid-match
    /// would need a command and a state field, and nothing in the game asks for one yet.
    /// </param>
    public SimWorld(ulong seed, int capacity, MatchRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        if (capacity <= 0 || capacity > SimConstants.MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be in (0, MaxEntities].");
        }

        Seed = seed;
        Roster = roster;
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

        // The deck tables are sized by the lattice the bridges are built on, so they are made here
        // rather than in a field initialiser that would run before the grid exists.
        Bridgeworks = new Bridgeworks(Navigation.CellCount, roster);
        _pathFinder = new PathFinder(Navigation.CellCount);
        _pathCells = new int[capacity * SimConstants.MaxPathCells];
        _jobs = new ProductionJob[capacity * SimConstants.MaxQueueLength];
        _teams = new TeamState[SimConstants.TeamCount];
        _spatial = new SpatialIndex(capacity, SimConstants.MapExtentMm);
        _visibility = new VisibilityGrid(Navigation.Size);
        _radars = new PowerSystem.RadarNetwork(capacity);

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

    /// <summary>
    /// The crossings a team has built, block by block, with the work still going into them. The
    /// client draws the decks from here: a ford on the map could have been carved by the generator,
    /// so the terrain alone cannot say where a bridge is.
    /// </summary>
    public Bridgeworks Bridgeworks { get; }

    /// <summary>Uniform grid of live entities, rebuilt each tick for proximity queries.</summary>
    public SpatialIndex Spatial => _spatial;

    /// <summary>Per-team visibility and explored map.</summary>
    public VisibilityGrid Visibility => _visibility;

    /// <summary>
    /// The radar stations each team has on the air, and the coverage they project.
    /// Rebuilt every tick from the structures standing and the generation available, so
    /// what a radar covers is never stored state that a replay could disagree about.
    /// </summary>
    public PowerSystem.RadarNetwork Radars => _radars;

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
    /// What each of the mission's triggers remembers, in definition order — and empty for a
    /// mission that declares none, which is every mission that does not use the trigger layer.
    /// <para>
    /// It is simulation state and it is hashed: see <see cref="TriggerState"/> for why a
    /// trigger's memory goes in the hash while a derived count does not. Nothing outside the
    /// mission systems may write it.
    /// </para>
    /// </summary>
    public ReadOnlySpan<TriggerState> TriggerStates => _triggers;

    /// <summary>Mutable trigger states, for <see cref="TriggerSystem"/>.</summary>
    internal Span<TriggerState> TriggerStatesSpan => _triggers;

    /// <summary>
    /// The mission's flags, one word per flag it declares — empty for a mission that declares
    /// none or has no mission at all.
    /// <para>
    /// A flag is the mission's own memory of something that happened: a trigger raises one so
    /// that a later trigger can wait on it. That makes it state like any other, so it is hashed
    /// alongside the fired ticks; a mission that uses no flags allocates no words and mixes
    /// none, which is what keeps a match that does not use this layer hashing exactly what it
    /// hashed before.
    /// </para>
    /// </summary>
    public ReadOnlySpan<uint> MissionFlags => _missionFlags;

    /// <summary>
    /// The mission's scripted messages, oldest first, capped at
    /// <see cref="MaxMissionMessages"/>.
    /// <para>
    /// A presentation ledger rather than simulation state, and deliberately not hashed: it is
    /// read only by the interface and the probe, it changes nothing about what happens next, and
    /// it is derived anyway from the fired state that <em>is</em> hashed — two peers cannot
    /// disagree about what a mission has said without disagreeing about a trigger first. It is
    /// capped so that a mission which talks a great deal cannot grow the world.
    /// </para>
    /// </summary>
    public IReadOnlyList<MissionMessage> MissionMessages => _missionMessages;

    /// <summary>Lines a mission may have shown the player before the oldest is dropped.</summary>
    public const int MaxMissionMessages = 16;

    /// <summary>True when the mission has raised this flag.</summary>
    public bool IsMissionFlagSet(int flag)
        => (uint)flag < (uint)_missionFlags.Length && _missionFlags[flag] != 0;

    /// <summary>Raises a mission flag. Called by <see cref="TriggerSystem"/>.</summary>
    internal void SetMissionFlag(int flag)
    {
        if ((uint)flag < (uint)_missionFlags.Length)
        {
            _missionFlags[flag] = 1;
        }
    }

    /// <summary>
    /// Brings an objective to complete on the mission's behalf, which is the whole of what a
    /// <see cref="TriggerActionKind.CompleteObjective"/> action does.
    /// </summary>
    /// <returns>False when there is no such objective, or when it is no longer pending.</returns>
    internal bool CompleteObjective(int index)
    {
        if ((uint)index >= (uint)_objectives.Length)
        {
            return false;
        }

        ref ObjectiveState state = ref _objectives[index];

        // An objective that has already failed stays failed. A trigger is a scripted event, not
        // an undo button: the deadline that ran out or the army that was wiped out is a fact the
        // mission told the player about, and a later message saying the opposite would be a lie
        // the panel cannot resolve.
        if (state.IsComplete || state.IsFailed)
        {
            return false;
        }

        state.Status = ObjectiveStatus.Complete;
        state.LastEvaluatedTick = (int)Tick;
        return true;
    }

    /// <summary>
    /// Records a line the mission showed the player. Called by <see cref="TriggerSystem"/>.
    /// </summary>
    internal void RaiseMissionMessage(string greekText)
    {
        if (string.IsNullOrEmpty(greekText))
        {
            return;
        }

        if (_missionMessages.Count >= MaxMissionMessages)
        {
            _missionMessages.RemoveAt(0);
        }

        _missionMessages.Add(new MissionMessage(Tick, greekText));
    }

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

        // One state per trigger, and one word per flag the mission declares. Both are sized by
        // the mission, so a mission that declares none allocates nothing, hashes nothing, and is
        // therefore bit-identical to the same world built before the layer existed.
        _triggers = new TriggerState[mission.Triggers.Count];
        _missionFlags = new uint[mission.DeclaredFlagCount];
    }

    /// <summary>Sets the final outcome. Called by <see cref="VictorySystem"/>.</summary>
    internal void SetOutcome(GameOutcome outcome) => Outcome = outcome;

    /// <summary>Per-system timing, for diagnosing frame hitches.</summary>
    public SimProfiler Profiler { get; } = new();

    /// <summary>Seed this world was created with.</summary>
    public ulong Seed { get; }

    /// <summary>
    /// Who is playing this match: the teams in it, the faction each one plays and the side each one
    /// is on. Fixed when the world is built — see <see cref="MatchRoster"/> for why it is not state.
    /// </summary>
    public MatchRoster Roster { get; }

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

    /// <summary>
    /// Queues a move order for the next tick.
    /// <para>
    /// <b>The one call a move order is given through, whichever hand gave it.</b> The player's
    /// right-click ends here and so does the probe's <c>order move</c>, which is the point: an order
    /// spelled out at each call site is two orders, and the day one of them learns something the
    /// other does not — a validation, a different tick — a script and a player are playing different
    /// games while the transcript says they are not.
    /// </para>
    /// </summary>
    public void OrderMove(EntityId target, WorldPos destination, int issuerTeam)
        => Enqueue(SimCommand.Move(target, destination, Tick + 1, issuerTeam));

    /// <summary>
    /// Queues an attack order for the next tick, and is to <see cref="OrderMove"/> what attacking is
    /// to moving: the same one call for the same one reason, so the click that locks a gun onto a
    /// target and a script that does are issuing the same command on the same tick.
    /// </summary>
    public void OrderAttack(EntityId attacker, EntityId victim, int issuerTeam)
        => Enqueue(SimCommand.Attack(attacker, victim, Tick + 1, issuerTeam));

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

    /// <summary>
    /// Whether the entity in a slot could stand at a point: the grid allows the cell and the
    /// surface allows this mover's movement class.
    /// <para>
    /// The two questions the route search asks before it will plan through a cell —
    /// <see cref="NavGrid.IsWalkable"/> and <see cref="TerrainLayer.IsPassable"/> — asked about one
    /// point instead of a route. It is here rather than in each caller because the answer decides
    /// whether an order is a goal or a trap: a goal the mover cannot enter is clipped by
    /// <see cref="RepathFrom"/> to the nearest cell it can, so a unit ordered onto it walks part of
    /// the way and then has nowhere left to go, which is a state the world should refuse to enter
    /// when it is cheap to know better.
    /// </para>
    /// <para>
    /// Flying is not asked about the ground: an airborne mover may stand anywhere, and the answer
    /// comes from the same <see cref="PathContextOf"/> the pathfinder reads rather than from a
    /// second opinion about water.
    /// </para>
    /// </summary>
    public bool CanStandAt(int slot, WorldPos position)
    {
        if (!IsAliveSlot(slot))
        {
            return false;
        }

        int cell = Navigation.IndexOfWorld(position);

        if (cell < 0 || !Navigation.IsWalkable(cell))
        {
            return false;
        }

        ref Entity e = ref _entities[slot];
        PathContext context = PathContextOf(e.TeamId, e.Faction, e.Kind);

        return TerrainTypes.IsPassable(cell, context.Movement);
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
    /// <para>
    /// <b>An ordered point the mover cannot enter is clipped to the nearest cell it can, and the
    /// goal is rewritten to that cell.</b> The route has always been planned to the clipped cell —
    /// that is what <see cref="NavGrid.NearestWalkable"/> is for — but the goal itself was left
    /// where it was ordered, and the two disagreed from then on. The unit walked to the clipped
    /// cell, and the arrival test in <see cref="MovementSystem"/> compared its position with the
    /// point nobody could stand on, so it never arrived: the next tick asked for the same route,
    /// the route search answered "you are already in the goal cell" and reported success, and
    /// <see cref="Entity.PathFailures"/> was reset to zero by that success. `path 0 cells at 0,
    /// waiting for a route, 0 failures` — travel frozen for the rest of the match, with nothing
    /// anywhere reporting a fault, because nothing was failing. The unit was doing exactly what it
    /// was told, forever.
    /// </para>
    /// <para>
    /// Rewriting the goal is what makes the state honest rather than the loop merely bounded: the
    /// route, the arrival test and the point the interface prints are then about one place, the
    /// unit finishes the walk it started, and the move order ends in the state the world already
    /// understands — <c>move none</c>, standing at the nearest ground it could reach — instead of
    /// a goal it is not pursuing. Where the ordered point was reachable after all, nothing here
    /// changes: the clipped cell is the ordered point's own cell.
    /// </para>
    /// </summary>
    /// <returns>True when the route is in hand: a path, or the cell the unit is already standing on.</returns>
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
        int ordered = Navigation.IndexOfWorld(e.MoveGoal);
        int goal = Navigation.NearestWalkable(ordered, TerrainTypes, context);

        if (goal < 0)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return false;
        }

        if (goal != ordered)
        {
            // The height is kept from the ordered point rather than taken from the grid: a mover
            // that flies was never clipped in the first place, and anything else is snapped to the
            // surface by the movement step on the same tick.
            WorldPos centre = Navigation.CentreOf(goal);
            e.MoveGoal = new WorldPos(centre.X, e.MoveGoal.Y, centre.Z);
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

            // Power is decided before anything that depends on it: a radar that the grid
            // cannot run stamps no coverage into the fog and extends nobody's reach, and
            // the vision system is what stamps it.
            PowerSystem.Tick(this);
            VisionSystem.Tick(this);
            Profiler.Mark(ref Profiler.Vision, ref Profiler.WorstVision);
            EconomySystem.Tick(this);
            Profiler.Mark(ref Profiler.Economy, ref Profiler.WorstEconomy);

            if (TerrainTypes.HasWeather)
            {
                TerrainTypes.ExpireWeather(Tick);
            }

            ConstructionSystem.Tick(this);

            // Engineering works are construction too, and for the same reason: the cost was
            // paid when the order was given, and what follows is a period in which the work
            // can be seen to be happening.
            Bridgeworks.Tick(TerrainTypes, Tick);

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
                // The script runs before the objectives, and both after everything that moves,
                // fights or builds. A trigger may complete an objective, and the objective check
                // that follows has to see that on the same tick rather than a tenth of a second
                // later; and a trigger that spawns or orders something does so into a world that
                // has finished its tick, so what it creates is first seen by the systems on the
                // next one, in the order every other spawn is seen in.
                TriggerSystem.Tick(this);
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

        // Nor is a structure: it is raised on a site the player picked, so it names no target
        // and the building it is raised from is only what unlocked it.
        if (command.Kind == SimCommandKind.BuildStructure)
        {
            return TryBuildStructure(command.UnitKind, command.Destination, command.IssuerTeam);
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

    /// <summary>
    /// The faction a team plays, as this match declares it. A team's faction is an assignment
    /// rather than its number — see <see cref="MatchTeam"/> — and the match is what says so.
    /// </summary>
    public Faction FactionOfTeam(int team) => Roster.FactionOf(team);

    /// <summary>True when a team is playing this match.</summary>
    public bool IsTeamInPlay(int team) => Roster.IsInPlay(team);

    /// <summary>True when two teams are on the same side, as this match declares the sides.</summary>
    /// <remarks>
    /// The rule is written once, in <see cref="MatchRoster.AreAllied"/>; this is the world's own
    /// answer to it, which is the question every caller should be asking. It used to be a static
    /// function of two team ids — <c>0 and 1 against the rest</c> — which is a statement about a
    /// particular match written as though it were a law of the engine.
    /// </remarks>
    public bool AreAllied(int a, int b) => Roster.AreAllied(a, b);

    /// <summary>
    /// <b>The one question every damage path asks: may a thing owned by
    /// <paramref name="attackerTeam"/> hurt a thing owned by <paramref name="victimTeam"/>?</b>
    /// <para>
    /// It is the negation of <see cref="AreAllied"/>, and it is deliberately nothing else: the
    /// alliance itself is defined once, in the match the world was built with, and every path that
    /// damages, targets or classifies a friend asks this rather than comparing team ids. A
    /// comparison of ids answers "same team" and quietly reports an ally as an enemy, which is
    /// what this replaces: direct fire, artillery splash, the attack order and off-map support
    /// all asked <c>TeamId == TeamId</c>, so the allied pair in the standard skirmish — teams 0
    /// and 1 — shot each other in every match while the crossing rule beside them spared an
    /// ally's bridge because it had been written by asking the real question.
    /// </para>
    /// <para>
    /// <b>The sides come from the match, not from the numbers.</b> Which teams are allies is now
    /// declared by <see cref="Roster"/> rather than baked into this method, so a match of two teams
    /// with no ally at all, or of the two teams that were always allied, resolves through this same
    /// line. A team the match does not declare is nobody's ally and is hostile to every side in it,
    /// which is what the fourth slot has always been.
    /// </para>
    /// <para>
    /// <b>Not every effect asks it, and the ones that do not are the ones that do not care.</b>
    /// Lava burns whoever stands in it and an ability documented as
    /// <see cref="AbilityDefinition.DamagesFriendlies"/> hits its own side on purpose: both are
    /// callers that have no team question to ask, rather than callers that asked it and got the
    /// wrong answer. What may never happen is a <em>weapon</em> pointed at an ally, and that is
    /// what this answers.
    /// </para>
    /// <para>
    /// It is symmetric — alliance here is a shared side rather than an attitude — but it takes
    /// the attacker first because at every call site the direction is what is being decided.
    /// </para>
    /// </summary>
    public bool IsHostile(int attackerTeam, int victimTeam) => !AreAllied(attackerTeam, victimTeam);

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
    /// <b>The whole of the production rule, in one question: could this building put this role on
    /// its pad right now, and why not if it could not?</b>
    /// <para>
    /// It is one function rather than a clause at each call site because the three callers are the
    /// three ways the same rule is met: the command that would carry the order out
    /// (<see cref="TryQueueUnit"/>), the panel row that offers it, and the probe. A row that worked
    /// the answer out for itself would be a second copy, and the first thing a copy does is drift —
    /// the greyed-out row that does nothing when pressed is the bug this shape prevents. That is the
    /// same argument <see cref="CanBuildStructure"/> and <see cref="CanBuildAnyBridge"/> are written
    /// from.
    /// </para>
    /// <para>
    /// The clauses are in the order a player meets them: whether this building makes that role at
    /// all, then the design (<see cref="CanBuild"/> — technology, a licence, a prototype's own cap
    /// and the Σοβιετικοί rule that a factory design be proven), then
    /// <b>the command capacity</b>, then what it costs. Naming the shortfall rather than saying
    /// "not available" is what the panel has always done, and the capacity refusal joins that list
    /// in the same voice: <c>λείπει δυναμικότητα 174</c>.
    /// </para>
    /// <para>
    /// <b>A structure is never refused for want of capacity, and that is load-bearing.</b> A side
    /// over its ceiling that could not raise a building would be a side that could never raise the
    /// building that would lift the ceiling — a deadlock, and a plausible one, because a stalemate
    /// is exactly the state in which nobody is dying and nothing else would bring the army back
    /// under. So the ceiling is the one clause here that asks the role's own
    /// <see cref="UnitDefinition.IsBuilding"/> first, and the answer for a building is that the
    /// question does not apply to it: structures are what capacity comes from, so they can never be
    /// what capacity refuses. <c>ACappedTeamCanStillRaiseItsCeiling</c> is the test that pins it.
    /// </para>
    /// </summary>
    /// <param name="building">The structure the work would happen at.</param>
    /// <param name="kind">The role to put on its pad.</param>
    /// <param name="reason">Empty when allowed, otherwise why not, in the player's own language.</param>
    public bool CanProduce(EntityId building, UnitKind kind, out string reason)
    {
        reason = string.Empty;

        if (!TryResolve(building, out int slot))
        {
            reason = "δεν υπάρχει τέτοιο κτίριο";
            return false;
        }

        if (!UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            reason = "άγνωστο σχέδιο";
            return false;
        }

        ref Entity producer = ref _entities[slot];

        if (definition.ProducedAt != producer.Kind)
        {
            reason = $"χρειάζεται {UnitCatalog.GreekName(definition.ProducedAt)}";
            return false;
        }

        if ((uint)producer.TeamId >= SimConstants.TeamCount)
        {
            reason = "άγνωστη ομάδα";
            return false;
        }

        if (!CanBuild(producer.TeamId, kind))
        {
            reason = ReasonUnbuildable(producer.TeamId, kind);
            return false;
        }

        // The supply gate. A role that is not a building is what fields against the ceiling; a
        // building is what grants it, so this clause cannot reach one — see the note above.
        if (!definition.IsBuilding)
        {
            int over = OverCapacity(producer.TeamId);

            if (over > 0)
            {
                reason = CapacitySystem.OverCapacityReason(over);
                return false;
            }
        }

        ref TeamState team = ref _teams[producer.TeamId];
        Faction faction = producer.Faction;
        int materials = UnitCatalog.MaterialCost(faction, kind);
        int energy = UnitCatalog.EnergyCost(faction, kind);
        int water = UnitCatalog.WaterCost(faction, kind);

        // Every job costs all three: minerals for the hull, power for the tools and water for the
        // cooling and the crews. Running one dry stops construction rather than silently producing
        // for free.
        if (team.Materials < materials)
        {
            reason = $"λείπουν {materials - team.Materials} Π";
            return false;
        }

        if (team.Energy < energy)
        {
            reason = $"λείπουν {energy - team.Energy} Ε";
            return false;
        }

        if (team.Water < water)
        {
            reason = $"λείπουν {water - team.Water} Ν";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Live and queued entities of a role for a team. Both count against a cap:
    /// otherwise a player could queue ten of a prototype in one tick and only be
    /// stopped once they started appearing.
    /// <para>
    /// <b>This is not the command capacity and the two must not be read as one rule.</b> What this
    /// answers is <see cref="UnitDefinition.MaxAlive"/> — how many of one <em>role</em> a team may
    /// ever have, which is what makes a prototype a prototype. Capacity is a ceiling on the whole
    /// army, granted by structures and spent by every unit a side fields, and it lives in
    /// <see cref="CapacitySystem"/>. A team can be inside one and outside the other, and a rule
    /// that confused them would either let a side field two hundred electro prototypes or refuse it
    /// a second rifleman.
    /// </para>
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
    /// Live structures of a team, optionally of one role: what is <em>standing</em>, as opposed
    /// to what has been built. Queued or half-raised work is deliberately included — a site with
    /// a building on it is a structure the enemy has to knock down — and nothing in a production
    /// queue is, which is the difference between this and <see cref="CountOf"/>.
    /// <para>
    /// A derived count rather than a remembered one, and that matters for the hash: this is a
    /// pure function of the entities the hash already walks, so it is not hashed itself. The
    /// ledger <see cref="TeamState.StructuresLost"/> beside it is the opposite kind of number —
    /// it remembers something the world cannot recompute — and it <em>is</em> hashed.
    /// </para>
    /// </summary>
    public int CountStructures(int team, UnitKind role = UnitKind.None)
    {
        int total = 0;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (!_entities[slot].Alive || _entities[slot].TeamId != team)
            {
                continue;
            }

            if (role != UnitKind.None && _entities[slot].Kind != role)
            {
                continue;
            }

            if (UnitCatalog.Get(_entities[slot].Kind).IsBuilding)
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>
    /// Units of a team inside a circle, buildings excluded: what "the army is here" means, and
    /// the one implementation of it — <see cref="MissionSystem"/> asks it for a held area and for
    /// a denied one, and the trigger layer asks it for an ambush.
    /// </summary>
    public int CountUnitsInArea(int team, int centreX, int centreZ, int radiusMm)
    {
        int count = 0;
        long radius = radiusMm;
        long radiusSquared = radius * radius;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (!_entities[slot].Alive || _entities[slot].TeamId != team)
            {
                continue;
            }

            if (UnitCatalog.Get(_entities[slot].Kind).IsBuilding)
            {
                continue;
            }

            long dx = _entities[slot].Position.X - centreX;
            long dz = _entities[slot].Position.Z - centreZ;

            if ((dx * dx) + (dz * dz) <= radiusSquared)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the live units of a team inside a circle into <paramref name="destination"/> as
    /// slots, in ascending order, and returns how many were written. The group half of
    /// <see cref="TriggerActionKind.OrderGroup"/>: the count above answers "is anybody there",
    /// and an order needs to know <em>who</em>.
    /// </summary>
    internal int UnitsInAreaInto(int team, int centreX, int centreZ, int radiusMm, Span<int> destination)
    {
        int written = 0;
        long radius = radiusMm;
        long radiusSquared = radius * radius;

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (written >= destination.Length)
            {
                break;
            }

            if (!_entities[slot].Alive || _entities[slot].TeamId != team)
            {
                continue;
            }

            if (UnitCatalog.Get(_entities[slot].Kind).IsBuilding)
            {
                continue;
            }

            long dx = _entities[slot].Position.X - centreX;
            long dz = _entities[slot].Position.Z - centreZ;

            if ((dx * dx) + (dz * dz) <= radiusSquared)
            {
                destination[written++] = slot;
            }
        }

        return written;
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

    /// <summary>Ticks a stealthed unit stays visible after it fires.</summary>
    public const int StealthRevealTicks = 100;

    /// <summary>
    /// True when <paramref name="slot"/> cannot be seen by <paramref name="viewerTeam"/>:
    /// it is stealthed, it is not the viewer's own, it has not just fired, and no sensor of
    /// the viewer's team has looked closely enough at the ground it is standing on.
    /// <para>
    /// <b>The last clause is a lookup and not a search.</b> This used to walk every live
    /// entity of the team and test a fixed radius against each, which meant the answer to
    /// "can team 0 see the stalker" came from a different place than the answer to "can team
    /// 0 see that cell" — and the two were free to disagree the moment anything changed how
    /// far a unit could see. <see cref="VisionSystem"/> now stamps the close-detection disc
    /// into the same grid it stamps the sight disc into, from the same radius, on the same
    /// tick, so a radar that lights a cell is a radar that can find a man standing on it,
    /// and there is exactly one place in the engine that decides what a team knows.
    /// </para>
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

        int cell = Navigation.IndexOfWorld(target.Position);

        return cell < 0 || !_visibility.IsDetected(viewerTeam, cell);
    }

    /// <summary>
    /// True when a radar station is on the air: it exists, and the team's generation is
    /// still covering it. The one question the client asks about a dish.
    /// </summary>
    public bool IsRadarLit(int slot)
        => IsAliveSlot(slot) &&
           _entities[slot].Kind == UnitKind.RadarStation &&
           _radars.IsLit(_entities[slot].TeamId, slot);

    /// <summary>True when the slot holds a structure that has finished being raised.</summary>
    public bool IsComplete(int slot)
        => IsAliveSlot(slot) && _entities[slot].ConstructionTicksRemaining <= 0;

    /// <summary>
    /// How many units a team's structures support, and what its army is costing them. Both are
    /// sums over what the team owns, answered by <see cref="CapacitySystem"/> when they are asked
    /// rather than kept anywhere — see that class for why nothing here is state.
    /// </summary>
    public int CommandCapacity(int team) => CapacitySystem.CapacityOf(this, team);

    /// <summary>What a team's live units cost against its command capacity.</summary>
    public int ArmySupply(int team) => CapacitySystem.SupplyOf(this, team);

    /// <summary>
    /// How far over its command capacity a team is, in places, or zero when it is within it. The
    /// question the production gate is closed by and the question the status panel draws.
    /// </summary>
    public int OverCapacity(int team) => CapacitySystem.OverCapacityOf(this, team);

    /// <summary>
    /// True when a team has at least one live structure of a role. Only finished ones count: a
    /// building site is not doing anything yet.
    /// </summary>
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
    /// <para>
    /// The target is a position on the ground rather than an entity, which is the one
    /// shape of attack that must never be refused for want of an enemy standing there:
    /// a strike called on a crossroads, on a bridge or on empty ground is a legitimate
    /// order, and the only thing the friend-or-foe rule does here is decide who inside
    /// the radius is caught by it.
    /// </para>
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

        // Off-map support is the other way a crossing comes down. A strike that lands on a bridge
        // takes it with the units on it — and an ability documented as hitting everybody takes the
        // crossing its own side is using as well, which is the same promise it makes about units.
        Bridgeworks.DamageArea(
            TerrainTypes,
            target.X,
            target.Z,
            definition.RadiusMm,
            definition.Damage,
            team,
            sparesFriends: !definition.DamagesFriendlies);

        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (!IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity victim = ref _entities[slot];

            // A strike that distinguishes friend from foe spares the whole side, not the
            // team: an ally standing in the radius is exactly as much a friendly as the
            // caster's own, and before this it was hit while its own team was not. The
            // clause is skipped entirely for an ability that does not care who it hits —
            // that is a design decision, not a bug — which is what keeps
            // AbilityDefinition.DamagesFriendlies working; see ANukeDoesNotDistinguishFriendFromFoe,
            // which pins it.
            if (!definition.DamagesFriendlies && !IsHostile(team, victim.TeamId))
            {
                continue;
            }

            int dx = victim.Position.X - target.X;
            int dz = victim.Position.Z - target.Z;

            if (((long)dx * dx) + ((long)dz * dz) > radiusSquared)
            {
                continue;
            }

            // Off-map support asks the same question a rifle does. It used to apply the
            // catalogue's damage raw, which made an orbital strike the one weapon in the game that
            // ignored the ground the target was standing on and the armour it was made of — and a
            // rule that holds for every damage path except one is not a rule, it is a special
            // case waiting to be discovered by whoever balances the next ability. The nuke is
            // included, which is a deliberate cost rather than an oversight: a bank of earth is
            // not what saves a tank from one, but one rule that every path follows is worth more
            // than a hand-written exception, and two damage rules are two rules a reader has to
            // hold in their head at once.
            int hit = DamageRules.Against(this, slot, definition.Damage);

            if (victim.Health <= hit)
            {
                Despawn(new EntityId(slot, victim.Generation));
            }
            else
            {
                victim.Health -= hit;
            }
        }

        return true;
    }

    /// <summary>Furthest a span will reach from where it is placed, in cells.</summary>
    public const int MaxBridgeSpan = 24;

    /// <summary>
    /// Most cells one crossing can cover: the site itself, plus a full span in each
    /// direction. The size of the buffer <see cref="TryPlanBridge"/> fills.
    /// </summary>
    public const int MaxBridgeCells = (2 * MaxBridgeSpan) + 1;

    /// <summary>
    /// True when a team could build a crossing anywhere at all, and why not if it could
    /// not: the half of the rule that does not depend on where the player clicks.
    /// <para>
    /// It exists so that the button and the command cannot disagree. A panel that tested
    /// the resources itself would be a second copy of this rule, and the first thing a
    /// copy does is drift: the button that looks available and does nothing when pressed
    /// is the bug this shape prevents.
    /// </para>
    /// <para>
    /// What it measures is the cheapest crossing there is, one cell, because that is the
    /// question a button can answer before it knows the site. Whether a <em>particular</em>
    /// span is affordable is a question about the span, and it is answered where the span is
    /// measured.
    /// </para>
    /// </summary>
    public bool CanBuildAnyBridge(int team, out string reason)
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

        return Affordable(team, Bridgeworks.Cost(1), out reason);
    }

    /// <summary>
    /// True when a team has the resources for a crossing of this price, and which one it is
    /// short of when it has not. Naming the resource and the shortfall rather than saying
    /// "resources" is what the build panel has always done for a structure, and a player
    /// choosing between a ditch and a hundred metres of water needs the difference.
    /// </summary>
    private bool Affordable(int team, BridgeCost cost, out string reason)
    {
        reason = string.Empty;
        ref TeamState state = ref _teams[team];

        if (state.Materials < cost.Materials)
        {
            reason = $"λείπουν {cost.Materials - state.Materials} Π";
            return false;
        }

        if (state.Energy < cost.Energy)
        {
            reason = $"λείπουν {cost.Energy - state.Energy} Ε";
            return false;
        }

        if (state.Water < cost.Water)
        {
            reason = $"λείπουν {cost.Water - state.Water} Ν";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when a team could build a crossing at this position, and why not if it
    /// could not. Split out from the work so the interface can grey a button out for
    /// the right reason.
    /// </summary>
    public bool CanBuildBridge(int team, WorldPos target, out string reason)
        => TryPlanBridge(team, target, default, out _, out reason);

    /// <summary>
    /// The crossing a site would produce: whether it is allowed, why not when it is
    /// not, and the cells a span placed here would turn into ford.
    /// <para>
    /// <b>The one place the rule lives.</b> Validation, the command that spends the
    /// resources and carves the ground, and the client's preview of it all ask this same
    /// question, so a ghost that promises a crossing the command would refuse is not a
    /// thing that can happen — and a refusal can always name its reason, because the
    /// reason is computed here rather than thrown away at the call site.
    /// </para>
    /// <para>
    /// <paramref name="cells"/> may be empty, in which case only the verdict is
    /// answered. Otherwise the first <paramref name="count"/> entries are the cells, in
    /// the order they are walked, and a caller offering a shorter span than
    /// <see cref="MaxBridgeCells"/> simply gets a prefix — enough to ask where the
    /// crossing starts, not enough to draw all of it.
    /// </para>
    /// </summary>
    /// <param name="team">Team paying for the work.</param>
    /// <param name="target">Cell the player clicked.</param>
    /// <param name="cells">Where the span's cells are written, if there is room.</param>
    /// <param name="count">How many cells the span would cover.</param>
    /// <param name="reason">Empty when allowed, otherwise why not.</param>
    public bool TryPlanBridge(int team, WorldPos target, Span<int> cells, out int count, out string reason)
    {
        count = 0;

        if (!CanBuildAnyBridge(team, out reason))
        {
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

        int x = Navigation.CellX(cell);
        int z = Navigation.CellZ(cell);

        int across = 1 + SpanAlong(x, z, 1, 0) + SpanAlong(x, z, -1, 0);
        int down = 1 + SpanAlong(x, z, 0, 1) + SpanAlong(x, z, 0, -1);

        // Span the narrow way: a crossing should cross.
        bool horizontal = across <= down;
        int stepX = horizontal ? 1 : 0;
        int stepZ = horizontal ? 0 : 1;

        count = horizontal ? across : down;

        // The span is measured from where the player clicked, both ways, so a site in
        // open water asks for a bridge that would end in open water. Refusing here is
        // the difference between a crossing and a pier, and the reason has to say so:
        // it is the one refusal a player is likely to meet on a lake as opposed to a
        // river, and without the words it is indistinguishable from a click that never
        // arrived.
        if (count > MaxBridgeSpan)
        {
            reason = "πολύ φαρδύ πέρασμα";
            return false;
        }

        // Engineering works are finite, and the client draws every crossing there is. A limit
        // with no words of its own would be the silent refusal this whole method exists to
        // prevent, even where a match is unlikely ever to reach it.
        if (!Bridgeworks.HasRoom)
        {
            reason = "όριο γεφυρών";
            return false;
        }

        // What it costs is a question about the span, so it is asked here rather than before the
        // site was measured: a ditch and a hundred metres of water are not the same undertaking,
        // and a team that can afford the first may not be able to afford the second.
        if (!Affordable(team, Bridgeworks.Cost(count), out reason))
        {
            return false;
        }

        if (cells.Length == 0)
        {
            return true;
        }

        // Listed from one bank to the other, because that is the order the work reaches them:
        // the deck the client draws grows across the water rather than outwards from a point in
        // the middle of it. It starts at the bank nearer the site the player picked, so the
        // crossing builds away from them — a bridge that goes up from the far bank first reads
        // as somebody else's. Walking the two arms from the target put the clicked cell first
        // and built the crossing from the centre out at both ends.
        int step = horizontal ? 1 : Navigation.Size;
        int low = cell - (SpanAlong(x, z, horizontal ? -1 : 0, horizontal ? 0 : -1) * step);
        int high = cell + (SpanAlong(x, z, stepX, stepZ) * step);
        bool upwards = cell - low <= high - cell;
        int from = upwards ? low : high;
        int direction = upwards ? step : -step;
        int written = 0;

        for (int i = 0; i <= (high - low) / step && written < cells.Length; i++)
        {
            cells[written++] = from + (i * direction);
        }

        return true;
    }

    /// <summary>
    /// Starts a crossing at a position: the site is surveyed, the resources are spent and the
    /// work is recorded. The deck itself goes up over the following ticks, cell by cell, in the
    /// order <see cref="TryPlanBridge"/> listed them — see <see cref="Bridgeworks"/>.
    /// <para>
    /// The span runs along whichever axis stays wet longest, so placing it on a river
    /// crosses the river rather than running along the bank. Every water cell the work
    /// reaches becomes a ford, which is the same surface the generator carves for its
    /// own crossings — a bridge is a ford a player chose the site of.
    /// </para>
    /// <para>
    /// The cells are the cells <see cref="TryPlanBridge"/> returned, rather than a walk
    /// repeated here. The walk was duplicated once and the two copies disagreed: the width of
    /// the span was checked in this method but not in the check the button and the preview
    /// read, so a site the interface offered was quietly refused here with nothing to tell the
    /// player why.
    /// </para>
    /// <para>
    /// Permanent once built. A bridge is engineering work, not a unit: there is
    /// nothing to shoot that would put the river back, and modelling demolition would
    /// need the original depths stored per span for no gameplay the design asks for.
    /// </para>
    /// </summary>
    private bool TryBuildBridge(WorldPos target, int team)
    {
        Span<int> cells = stackalloc int[MaxBridgeCells];

        if (!TryPlanBridge(team, target, cells, out int count, out _))
        {
            return false;
        }

        // The span is recorded before the resources are taken, so that an order that cannot be
        // recorded — a map already carrying every crossing it may carry — costs nothing. The
        // plan refuses that case too; the order here is what makes the two agree.
        if (!Bridgeworks.Begin(cells[..count], Tick, team))
        {
            return false;
        }

        ref TeamState state = ref _teams[team];
        BridgeCost cost = Bridgeworks.Cost(count);
        state.Materials -= cost.Materials;
        state.Energy -= cost.Energy;
        state.Water -= cost.Water;

        // Paid for now, built over the next few seconds, which is how a structure works in this
        // game: the cost is the order, and the construction is a period of visibility.
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

    /// <summary>
    /// How much of the core of a base's patch has to be solid, in permille: all of it. The patch
    /// itself may carry a pond at its edge, and the ground the headquarters stands on may not.
    /// </summary>
    public const int BaseSiteCoreSolidPermille = 1_000;

    /// <summary>How many cells of a base's patch are solid ground.</summary>
    public int BaseSiteSolidCells(WorldPos centre) => SolidPatchCells(centre, BaseSiteRadiusCells);

    /// <summary>
    /// True when the ground around a position will hold a base: a solid core, and most of the
    /// patch around it solid too.
    /// <para>
    /// The two clauses are the same question asked twice with different numbers — a square patch of
    /// ground that is solid enough, to a threshold — which is why they are two calls to
    /// <see cref="IsSolidPatch"/> rather than a walk with a special case in it. A base needs a big
    /// yard and it needs the middle of that yard to be ground: the yard is five cells of radius
    /// because the four structures a scenario plants stand 45 m from the centre, and the core is two
    /// cells of radius, all solid, because that is where the headquarters itself stands.
    /// </para>
    /// </summary>
    public bool IsBaseSite(WorldPos centre)
        => IsSolidPatch(centre, BaseSiteRadiusCells, BaseSiteMinSolidPermille)
        && IsSolidPatch(centre, BaseSiteCoreCells, BaseSiteCoreSolidPermille);

    /// <summary>
    /// How many cells of the square patch of this radius around a position are solid ground.
    /// </summary>
    public int SolidPatchCells(WorldPos centre, int radiusCells)
    {
        int cell = Navigation.IndexOfWorld(centre);
        int centreX = Navigation.CellX(Math.Max(cell, 0));
        int centreZ = Navigation.CellZ(Math.Max(cell, 0));
        int solid = 0;

        for (int dz = -radiusCells; dz <= radiusCells; dz++)
        {
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
            {
                if (IsSolidGround(Navigation.IndexOf(centreX + dx, centreZ + dz)))
                {
                    solid++;
                }
            }
        }

        return solid;
    }

    /// <summary>
    /// True when a square patch of ground of this radius around a position is solid enough, to this
    /// permille.
    /// <para>
    /// <b>The one primitive under every placement rule.</b> A base and a factory ask different
    /// questions about the ground — how big a yard, and how much of it may be broken — but they are
    /// the same question, and this is where it is asked. Two predicates that each walked their own
    /// patch would be two notions of buildable ground, and the first thing a second notion does is
    /// disagree with the first: a cell a scenario calls a base and a player's click calls rough.
    /// </para>
    /// <para>
    /// A threshold rather than "all of it" because the caller is the one that knows whether a pond
    /// at the edge matters: a base's 11 × 11 yard may carry one, a building's own footprint may not.
    /// </para>
    /// </summary>
    /// <param name="centre">Middle of the patch.</param>
    /// <param name="radiusCells">Half-width of the patch, in navigation cells.</param>
    /// <param name="minSolidPermille">How much of the patch has to be solid, in permille.</param>
    private bool IsSolidPatch(WorldPos centre, int radiusCells, int minSolidPermille)
    {
        int total = ((radiusCells * 2) + 1) * ((radiusCells * 2) + 1);

        return (SolidPatchCells(centre, radiusCells) * 1_000) >= (total * minSolidPermille);
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
    /// True when a navigation cell is ground a unit can stand on: not water, not lava,
    /// and not a cliff.
    /// <para>
    /// A cliff is dry ground and still no place for a headquarters, and a mover that
    /// cannot enter the cell cannot leave it either — which is why walkability is part
    /// of the question rather than left to the surface type. Which piece of ground is
    /// under the cell is <see cref="SurfaceUnder"/>'s question, because two grids of
    /// different sizes are easy to index with each other's numbers.
    /// </para>
    /// </summary>
    private bool IsSolidGround(int navCell)
    {
        if (navCell < 0 || !Navigation.IsWalkable(navCell))
        {
            return false;
        }

        TerrainType type = SurfaceUnder(navCell);

        return type is not (TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava);
    }

    /// <summary>
    /// The surface of the terrain cell under a navigation cell's own centre.
    /// <para>
    /// The two grids are not the same size, so this is the one place the conversion is made:
    /// indexing one grid with the other's number is the kind of mistake that reads as working
    /// code on a square map. A cell with no terrain under it answers deep water, which is the
    /// answer that keeps everything asking "may something stand here" saying no.
    /// </para>
    /// </summary>
    private TerrainType SurfaceUnder(int navCell)
    {
        WorldPos centre = Navigation.CentreOf(navCell);
        int terrainCell = TerrainTypes.IndexOfWorld(centre.X, centre.Z);

        return terrainCell < 0 ? TerrainType.DeepWater : TerrainTypes.TypeAt(terrainCell);
    }

    // ----------------------------------------------------------- structures on a chosen site

    /// <summary>
    /// True when a team could raise a structure of this role anywhere at all, and why not if it
    /// could not: the half of the placement rule that does not depend on where the player
    /// clicked.
    /// <para>
    /// It exists so that the panel's row and the command cannot disagree. A row that worked the
    /// resources and the tech tier out for itself would be a second copy of this rule, and the
    /// first thing a copy does is drift: a row that looks available and does nothing when
    /// pressed is the bug this shape prevents, and the bridge button is greyed by
    /// <see cref="CanBuildAnyBridge"/> for exactly this reason.
    /// </para>
    /// <para>
    /// The building a structure is raised from is an <em>unlock</em> rather than the place the
    /// work happens: a team needs a finished one, and after that it may raise the structure
    /// wherever it likes. A build radius — "within so many metres of something of your own" —
    /// was considered and deliberately left out: this is a feature about choosing a site, and a
    /// build radius is a rule about not choosing one.
    /// </para>
    /// </summary>
    /// <param name="team">Team paying for the work.</param>
    /// <param name="kind">Role to raise.</param>
    /// <param name="reason">Empty when allowed, otherwise why not.</param>
    public bool CanBuildStructure(int team, UnitKind kind, out string reason)
    {
        reason = string.Empty;

        if ((uint)team >= SimConstants.TeamCount)
        {
            reason = "άγνωστη ομάδα";
            return false;
        }

        if (!UnitCatalog.TryGet(kind, out UnitDefinition definition) || !definition.IsBuilding)
        {
            reason = "δεν είναι κατασκευή";
            return false;
        }

        // Nothing raises itself: without a finished building of the role that makes this one,
        // the team has no yard to raise it from. An unfinished one does not count, which is what
        // HasStructure already says about a building site.
        if (!HasStructure(team, definition.ProducedAt))
        {
            reason = $"χρειάζεται {UnitCatalog.GreekName(definition.ProducedAt)}";
            return false;
        }

        // The tech tier, a licence, the per-role cap and the Σοβιετικοί prototype rule are all
        // one question, and it is asked in one place; only the words come from here.
        if (!CanBuild(team, kind))
        {
            reason = ReasonUnbuildable(team, kind);
            return false;
        }

        // There is one finite thing left that a plan can know about, and it is the world itself:
        // an order that cannot be recorded must be refused rather than half carried out, which is
        // the same reason Bridgeworks.HasRoom exists. Spawning is what would fail, and a command
        // that throws instead of refusing would take the client down with it.
        if (AliveCount >= Capacity)
        {
            reason = "ο χάρτης είναι γεμάτος";
            return false;
        }

        ref TeamState state = ref _teams[team];
        Faction faction = FactionOfTeam(team);

        // Naming the resource and the shortfall rather than saying "resources" is what the
        // build panel has always done, and it is the same three sentences a crossing is
        // refused with.
        int materials = UnitCatalog.MaterialCost(faction, kind);
        int energy = UnitCatalog.EnergyCost(faction, kind);
        int water = UnitCatalog.WaterCost(faction, kind);

        if (state.Materials < materials)
        {
            reason = $"λείπουν {materials - state.Materials} Π";
            return false;
        }

        if (state.Energy < energy)
        {
            reason = $"λείπουν {energy - state.Energy} Ε";
            return false;
        }

        if (state.Water < water)
        {
            reason = $"λείπουν {water - state.Water} Ν";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whatever <see cref="CanBuild"/> refused a role for, said in the words the build panel
    /// uses. The verdict is <see cref="CanBuild"/>'s; this only reads back which of its clauses
    /// said no, in the same order, so that the first one to fail is the one named.
    /// </summary>
    private string ReasonUnbuildable(int team, UnitKind kind)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        TeamState state = _teams[team];
        uint bit = 1u << (int)kind;

        if (!UnitCatalog.IsUnlocked(FactionOfTeam(team), kind, state.TechTier, state.TechMask) &&
            (state.LicenceMask & bit) == 0)
        {
            return definition.RequiredTech != TechId.None &&
                   !TechCatalog.IsCompleted(state.TechMask, definition.RequiredTech)
                ? "χρειάζεται έρευνα"
                : $"χρειάζεται τεχνολογία {definition.RequiredTechTier}";
        }

        // A capped design is a capability rather than a type: the team may field a limited
        // number, counting whatever is already standing or on the way.
        if (definition.MaxAlive > 0 && CountOf(team, kind) >= definition.MaxAlive)
        {
            return $"όριο {definition.MaxAlive}";
        }

        // What is left is the Σοβιετικοί rule that a factory design be proven first.
        return "χρειάζεται πρωτότυπο στο σχεδιαστικό γραφείο";
    }

    /// <summary>
    /// How much of a structure's own footprint has to be solid ground, in permille: all of it.
    /// <para>
    /// Unlike a base's yard, a building has nowhere to put a pond. A base is four buildings and the
    /// ground between them, and its patch is allowed to carry water at the edge for the reason the
    /// constant above gives; the ground a factory itself stands on is either ground or it is not,
    /// and a factory with one corner in the lake is a factory in the lake. The footprint is small
    /// for the same reason the threshold is total — the question is what is under this building,
    /// not whether the neighbourhood is pleasant.
    /// </para>
    /// </summary>
    public const int StructureFootprintMinSolidPermille = 1_000;

    /// <summary>
    /// True when a patch of ground will hold a structure of this role, and why not if it will not.
    /// <para>
    /// This is the site half of the rule — the same split as <see cref="CanBuildAnyBridge"/>
    /// against the span it is followed by — and it asks the question of the same primitive a
    /// scenario asks before it stands a base up, rather than inventing a second notion of good
    /// enough ground: <see cref="IsSolidPatch"/> is where this game says what a square patch of
    /// ground is worth. What differs is the patch, not the rule. A base keeps its eleven-cell yard,
    /// and a building asks for its own footprint — a factory the size of a factory rather than a
    /// parade square — because a player standing a building next to their own headquarters was
    /// being refused as <c>ανώμαλο έδαφος</c> for failing a test meant for a base.
    /// </para>
    /// <para>
    /// The clauses are in the order a player meets them. The cell they are pointing at comes
    /// first, and each way it can be wrong has its own words — water, lava, a cliff — because
    /// "the ground here will not do" is not something a player can act on, and the cell under
    /// the cursor is the only part of the answer they can move. What is left is the footprint
    /// around it, which is a question about the building rather than about the click.
    /// </para>
    /// <para>
    /// What stands on the ground is a different question with a different answer and is asked
    /// separately: <see cref="IsSiteClear"/>. This predicate is what "the ground here would hold
    /// this building" means, and it has to keep meaning that for a building that is already
    /// standing — the client asks it about a structure it has just paid for to see whether the
    /// order arrived, and a rule that refused a building its own site would answer no to every
    /// one of them.
    /// </para>
    /// </summary>
    /// <param name="kind">Role that would stand there, which is what decides the footprint.</param>
    /// <param name="site">Where the structure would stand.</param>
    /// <param name="reason">Empty when allowed, otherwise why not.</param>
    public bool CanPlaceStructure(UnitKind kind, WorldPos site, out string reason)
    {
        reason = string.Empty;

        // Asked of the terrain layer rather than of the navigation grid, because the navigation
        // grid clamps a position to its own edge: a site a hundred metres off the map would come
        // back as the corner cell and be judged as ground. The bridge's own site check asks the
        // same question the same way.
        if (TerrainTypes.IndexOfWorld(site.X, site.Z) < 0)
        {
            reason = "έξω από τον χάρτη";
            return false;
        }

        int cell = Navigation.IndexOfWorld(site);

        // The cell itself, in the order a player would recognise it: the surface they can see,
        // and then whether anything can reach the cell at all. The three of them are the ways a
        // single cell is not solid ground — the same question IsSolidGround asks — and asking it
        // in three sentences is the difference between "not here" and "not on the lake".
        TerrainType surface = SurfaceUnder(cell);

        if (surface is TerrainType.ShallowWater or TerrainType.DeepWater)
        {
            reason = "χρειάζεται στεριά";
            return false;
        }

        if (surface == TerrainType.Lava)
        {
            reason = "λάβα";
            return false;
        }

        // A cliff is dry ground and still no place for a building: a mover that cannot enter the
        // cell cannot reach whatever stands on it.
        if (!Navigation.IsWalkable(cell))
        {
            reason = "πολύ απότομο έδαφος";
            return false;
        }

        // And the footprint: the cell is good, the ground around it is not. How much ground that is
        // comes from the role — a nuclear plant needs more of it than a power plant — and it is a
        // radius in cells, so the patch is a square of (2r+1)² cells centred on the one clicked.
        if (!IsSolidPatch(site, UnitCatalog.FootprintRadiusCells(kind), StructureFootprintMinSolidPermille))
        {
            reason = "ανώμαλο έδαφος";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The lowest-numbered structure already standing on ground this site would take, or -1 when
    /// there is none.
    /// <para>
    /// Two footprints overlap when they share a cell, which is the same arithmetic a patch of ground
    /// is counted with: a building occupies the square of cells its own role asks for, and a site is
    /// refused when any of them is occupied. Two buildings in the same cell were possible before
    /// this, and two models rendering inside each other is what that looked like.
    /// </para>
    /// <para>
    /// Ascending slot order, like every other scan in this file, and the first structure found is
    /// the one named — so two orders in the same tick resolve the same way on every machine, and the
    /// refusal a player reads does not depend on the order the entities happen to be stored in.
    /// </para>
    /// </summary>
    private int StructureUnderSite(UnitKind kind, WorldPos site)
    {
        int radius = UnitCatalog.FootprintRadiusCells(kind);
        int cell = Navigation.IndexOfWorld(site);
        int x = Navigation.CellX(Math.Max(cell, 0));
        int z = Navigation.CellZ(Math.Max(cell, 0));

        for (int slot = 0; slot < Capacity; slot++)
        {
            if (!IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref _entities[slot];

            if (!UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            // An unfinished structure is a structure: it has been paid for, its model is standing on
            // the ground, and a second building raised through it would be the same two models in
            // one place.
            int other = Navigation.IndexOfWorld(entity.Position);
            int reach = radius + UnitCatalog.FootprintRadiusCells(entity.Kind);

            if (Math.Abs(Navigation.CellX(other) - x) <= reach && Math.Abs(Navigation.CellZ(other) - z) <= reach)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>
    /// True when nothing is already standing where a structure of this role would go, and why not
    /// if something is.
    /// <para>
    /// The other half of the placement rule, and the half that took a bug to notice: the site rule
    /// judged the ground and never what was standing on it, so a building could be ordered on top of
    /// another one and the two models rendered inside each other. The refusal names the structure in
    /// the way, because that is the part a player can act on — "there is a factory there" is a
    /// sentence you can move away from, and "the site is occupied" is not.
    /// </para>
    /// <para>
    /// <b>Mobile units do not block construction, and none is harmed by it.</b> A unit is the one
    /// thing on the map that is on its way somewhere: a tank standing on a site today is a tank
    /// twenty metres away in ten seconds, and a rule that refused the site until it moved would make
    /// placement a question about the traffic of the moment — the player would have to order units
    /// out of the way of their own building, one cell at a time, for no gain. Units do not block
    /// movement anywhere else in this game either, and nothing is destroyed or pushed: the building
    /// rises around whoever is standing there and they drive out from under it when they are next
    /// ordered. What is refused is the one thing that will still be there tomorrow.
    /// </para>
    /// </summary>
    /// <param name="kind">Role that would stand there, which is what decides the footprint.</param>
    /// <param name="site">Where the structure would stand.</param>
    /// <param name="reason">Empty when clear, otherwise what is standing there.</param>
    public bool IsSiteClear(UnitKind kind, WorldPos site, out string reason)
    {
        reason = string.Empty;

        int blocker = StructureUnderSite(kind, site);

        if (blocker < 0)
        {
            return true;
        }

        reason = $"επικαλύπτεται με {UnitCatalog.GreekName(_entities[blocker].Kind)}";
        return false;
    }

    /// <summary>
    /// The nearest site to <paramref name="wanted"/> where a structure of this role may actually be
    /// raised: the ground rule and the occupancy rule both.
    /// <para>
    /// The AI has no way to choose a site and does not need one — it keeps the offset placement,
    /// where a finished structure appears just outside whatever made it — but an offset is now a
    /// site like any other and has to pass both halves of the rule. An offset of fourteen metres
    /// from a factory is inside that factory's own footprint, so the offset alone is no longer
    /// enough: the site is searched outwards for the nearest patch that will hold the building,
    /// exactly as a scenario searches for the ground its bases stand on.
    /// </para>
    /// <para>
    /// A site that is already good is kept to the millimetre rather than snapped to a cell centre:
    /// this is the path every AI structure has always taken, and moving them all by half a cell to
    /// tidy the arithmetic would be a change to the game rather than a fix to it.
    /// </para>
    /// <para>
    /// Deterministic: it reads the terrain, the grid and the standing structures and nothing else —
    /// no RNG, no clock — so the same seed rebuilds the same sites in the same places.
    /// </para>
    /// </summary>
    /// <param name="kind">Role that would stand there, which is what decides the footprint.</param>
    /// <param name="wanted">Where the offset put it.</param>
    /// <param name="site">
    /// Where it can stand. When the answer is false this is the nearest solid ground rather than the
    /// offset it was asked for, which is what the rest of the game falls back to.
    /// </param>
    public bool TryFindStructureSite(UnitKind kind, WorldPos wanted, out WorldPos site)
    {
        if (CanPlaceStructure(kind, wanted, out _) && IsSiteClear(kind, wanted, out _))
        {
            site = wanted;
            return true;
        }

        int cell = Navigation.IndexOfWorld(wanted);
        int centreX = Navigation.CellX(Math.Max(cell, 0));
        int centreZ = Navigation.CellZ(Math.Max(cell, 0));

        for (int radius = 1; radius <= StructureSearchRadiusCells; radius++)
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

                    if (CanPlaceStructure(kind, centre, out _) && IsSiteClear(kind, centre, out _))
                    {
                        site = centre;
                        return true;
                    }
                }
            }
        }

        // Nowhere with room within reach. Falling back to the nearest solid cell keeps the structure
        // out of the water, and returning false is the caller's notice that the site it wanted was
        // not there — the same promise TryFindBaseSite makes.
        site = LegalSpawnSite(wanted);
        return false;
    }

    /// <summary>
    /// How far a structure site may be pushed from the offset that produced it, in navigation cells.
    /// <para>
    /// Twenty-four cells is 225 m: far enough to walk out of a built-up base and find open ground,
    /// and no further. The offset is always within thirty metres of the building that made it, so a
    /// search that reaches this far has already passed every plausible site; past it something else
    /// is wrong, and the caller is told so rather than handed a position on the other side of the
    /// map.
    /// </para>
    /// </summary>
    public const int StructureSearchRadiusCells = 24;

    /// <summary>
    /// The structure a site would produce: whether it is allowed, why not when it is not, and
    /// where it would stand.
    /// <para>
    /// <b>The one place the rule lives.</b> The panel that arms the row, the ghost the player
    /// aims with, and the command that spends the resources all ask this same question, so a
    /// ghost that promises a building the order would refuse is not a thing that can happen —
    /// and a refusal can always name its reason, because the reason is computed here rather
    /// than thrown away at the call site.
    /// </para>
    /// <para>
    /// The site it returns is the <em>cell centre</em> rather than the millimetre the cursor
    /// resolved to. The footprint that was judged is the cell's, so the cell centre is the
    /// position that was judged; a building stood four metres off the patch it was checked
    /// against would be a second, unasked question about the ground. It also means the ghost
    /// sits exactly where the structure will for every pixel inside one cell, which is what
    /// makes a plan and its order comparable cell for cell.
    /// </para>
    /// <para>
    /// Both halves of the site are asked, in the order a player meets them: the ground
    /// (<see cref="CanPlaceStructure"/>) and then what is standing on it
    /// (<see cref="IsSiteClear"/>). The second one is what makes two orders for the same cell in one
    /// tick resolve the way they read: commands are applied in the order they were issued, and the
    /// first raises its building on the spot, so the second finds it there and is refused by name.
    /// </para>
    /// </summary>
    /// <param name="team">Team paying for the work.</param>
    /// <param name="kind">Role to raise.</param>
    /// <param name="site">Cell the player clicked, in millimetres.</param>
    /// <param name="planned">Where the structure would stand; default when refused.</param>
    /// <param name="reason">Empty when allowed, otherwise why not.</param>
    public bool TryPlanStructure(int team, UnitKind kind, WorldPos site, out WorldPos planned, out string reason)
    {
        planned = default;

        if (!CanBuildStructure(team, kind, out reason))
        {
            return false;
        }

        if (!CanPlaceStructure(kind, site, out reason))
        {
            return false;
        }

        if (!IsSiteClear(kind, site, out reason))
        {
            return false;
        }

        planned = Navigation.CentreOf(Navigation.IndexOfWorld(site));
        return true;
    }

    /// <summary>
    /// Raises a structure at a site the player chose: the ground is surveyed, the resources are
    /// spent, and the structure starts there rather than at an offset from whatever made it.
    /// <para>
    /// It arrives as a building site and rises over the time the catalogue quotes for it, doing
    /// nothing until it is up — the same shape a produced structure has, and the same shape a
    /// crossing has. The cost is the order and the construction is a period the player can see,
    /// which is what makes the site worth having chosen: a building that appeared finished would
    /// hide the one stretch of the game where the choice could still be judged.
    /// </para>
    /// </summary>
    private bool TryBuildStructure(UnitKind kind, WorldPos site, int team)
    {
        if (!TryPlanStructure(team, kind, site, out WorldPos planned, out _))
        {
            return false;
        }

        // Raised before it is paid for, for the same reason a crossing's span is recorded before
        // the resources are taken: a step that cannot happen must not have cost anything. The
        // plan has already refused a world with no room in it, so nothing here can fail.
        Faction faction = FactionOfTeam(team);
        EntityId created = Spawn(faction, team, kind, planned, Fix32.Zero, UnitCatalog.Get(kind).Health);
        ref Entity structure = ref _entities[created.Slot];

        int ticks = UnitCatalog.BuildTicks(faction, kind);
        structure.ConstructionTicksTotal = ticks;
        structure.ConstructionTicksRemaining = ticks;

        ref TeamState state = ref _teams[team];
        state.Materials -= UnitCatalog.MaterialCost(faction, kind);
        state.Energy -= UnitCatalog.EnergyCost(faction, kind);
        state.Water -= UnitCatalog.WaterCost(faction, kind);
        return true;
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

        // A completed prototype run delivers a real unit — see <see cref="PrototypeSystem"/> — so it
        // is a way of fielding something, and the ceiling applies to it for the same reason it
        // applies to a factory queue. The check is here rather than at delivery because the
        // materials are charged here: a run that was paid for and could not deliver would be a unit
        // bought and thrown away, and the command is refused before anything is spent.
        int over = OverCapacity(bureau.TeamId);

        if (over > 0)
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
    /// Whether an attack order from one entity against another would be accepted, and the words
    /// for the refusal when it would not.
    /// <para>
    /// <b>An order a gun can never carry out is refused where it is given.</b> A tank gun cannot be
    /// pointed at an aeroplane and nothing may fire on its own side, and both of those are already
    /// <see cref="CombatSystem"/>'s rules — but the order used to be taken anyway and then quietly
    /// ignored for the rest of the match, which left the unit holding an attack order and a move
    /// goal on a target it could never engage, re-asking for the route every ten ticks and marching
    /// at it while it did. An order that can never be satisfied is a free kill, and it reads as a
    /// broken unit rather than as a rule.
    /// </para>
    /// <para>
    /// <b>It is deliberately not the whole of the question the gun asks.</b> Reach and sight are
    /// not decided here, because neither is settled: a target further off than the weapon reaches
    /// is a target to march at, and one behind a hill now may be in the open in ten seconds. What
    /// is asked is only what no later tick can change — is there a gun at all, is the target an
    /// enemy, and can this weapon's kind of shell touch this kind of target — which is the same
    /// pair of questions <see cref="CombatSystem"/> asks again before it fires.
    /// </para>
    /// </summary>
    /// <param name="attacker">The entity the order would be given to.</param>
    /// <param name="victim">What it would be told to attack.</param>
    /// <param name="refusal">The reason, in the words the player is shown, when it would not be.</param>
    public bool CanAttack(EntityId attacker, EntityId victim, out string refusal)
    {
        if (!TryResolve(attacker, out int slot) || !TryResolve(victim, out int victimSlot))
        {
            refusal = "δεν υπάρχει τέτοιος στόχος";
            return false;
        }

        ref Entity gunner = ref _entities[slot];
        UnitDefinition weapon = UnitCatalog.Get(gunner.Kind);

        if (!weapon.IsArmed)
        {
            refusal = "άοπλη μονάδα";
            return false;
        }

        ref Entity target = ref _entities[victimSlot];

        // An order to attack an ally is refused, not obeyed and then ignored: the order would
        // otherwise sit on the unit setting HasAttackOrder and a move goal towards its friend, and
        // a unit marching at an ally is the mistake this rule exists to stop, whether a player or a
        // script made it. Compare CombatSystem, which asks the same question, so an order can never
        // be issued for a target the gun will refuse.
        if (!IsHostile(gunner.TeamId, target.TeamId))
        {
            refusal = "είναι σύμμαχος";
            return false;
        }

        if (!(UnitCatalog.Flies(target.Kind) ? weapon.CanHitAir : weapon.CanHitGround))
        {
            refusal = UnitCatalog.Flies(target.Kind) ? "δεν βάλλει κατά αέρος" : "δεν βάλλει κατά εδάφους";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// Locks a unit onto an enemy. If the target is out of range the unit is sent
    /// towards it, and the combat system keeps the approach updated as the enemy
    /// moves.
    /// <para>
    /// A structure is locked on but not sent anywhere. It has no speed and no route, so
    /// the order it can be given is a standing one — this is the target, engage it if it
    /// comes into reach — rather than a march: <c>HasAttackOrder</c> is what the approach
    /// loop keys on, and an emplacement that set it would be handed a move goal every ten
    /// ticks for a path it can never walk. The target is sticky either way, and when it
    /// dies or leaves, automatic acquisition picks up whatever came next, which is the
    /// same behaviour the building had before anyone clicked on it.
    /// </para>
    /// <para>
    /// <b>A target out of range is marched at only where the unit could stand.</b> A goal on ground
    /// this mover cannot enter is not a goal, it is a trap: the route search clips it to the nearest
    /// cell the mover can enter, so a unit sent onto it closes part of the distance and then has
    /// nowhere left to go. The order is kept and the unit stays where it is, which is a state a
    /// player can read — a unit standing with a target locked is waiting for it to come into reach,
    /// and one walking at a target it can never touch is not. <see cref="CombatSystem"/> asks the
    /// same question again before every later approach, because a target that was on ground this
    /// mover could cross can walk onto ground it cannot.
    /// </para>
    /// </summary>
    private bool TryAttack(int slot, ref Entity attacker, EntityId victim)
    {
        if (!CanAttack(new EntityId(slot, attacker.Generation), victim, out _))
        {
            return false;
        }

        UnitDefinition weapon = UnitCatalog.Get(attacker.Kind);

        if (!TryResolve(victim, out int victimSlot))
        {
            return false;
        }

        ref Entity target = ref _entities[victimSlot];

        attacker.TargetSlot = victimSlot;

        if (weapon.IsBuilding)
        {
            return true;
        }

        attacker.HasAttackOrder = true;

        long dx = attacker.Position.X - target.Position.X;
        long dz = attacker.Position.Z - target.Position.Z;
        bool inRange = ((dx * dx) + (dz * dz)) <= ((long)weapon.AttackRangeMm * weapon.AttackRangeMm);

        if (!inRange && CanStandAt(slot, target.Position))
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
    /// required tech tier, the team is inside its command capacity and it can pay.
    /// Costs are taken up front so a queue cannot be filled with resources the team
    /// does not have.
    /// <para>
    /// The verdict is <see cref="CanProduce"/>'s and this method only carries it out, so the order
    /// a click sends, the row a panel draws and the answer an AI gets are one rule. What is left
    /// here is the arithmetic the question does not do: how long the job takes for this faction's
    /// build speed, and the job itself.
    /// </para>
    /// </summary>
    private bool TryQueueUnit(int slot, ref Entity building, UnitKind kind)
    {
        if (!CanProduce(new EntityId(slot, building.Generation), kind, out _))
        {
            return false;
        }

        ref TeamState team = ref _teams[building.TeamId];

        int ticks = UnitCatalog.BuildTicks(building.Faction, kind);
        int production = team.ProductionPermille > 0 ? team.ProductionPermille : 1_000;
        ticks = Math.Max(1, (ticks * 1_000) / production);

        if (!AddJob(slot, kind, ticks))
        {
            return false;
        }

        team.Materials -= UnitCatalog.MaterialCost(building.Faction, kind);
        team.Energy -= UnitCatalog.EnergyCost(building.Faction, kind);
        team.Water -= UnitCatalog.WaterCost(building.Faction, kind);
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
