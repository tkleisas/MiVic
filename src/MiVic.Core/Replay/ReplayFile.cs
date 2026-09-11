using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Replay;

/// <summary>
/// A recorded match: the inputs, not the state.
/// <para>
/// Because the simulation is deterministic, a seed, a scenario, an entity
/// capacity and the external command log are enough to reproduce a match
/// exactly. The file also stores the state hash it ended on, so verification is a
/// single comparison rather than a guess.
/// </para>
/// <para>
/// The format is little-endian and versioned. It is deliberately plain: a
/// replay is a debugging tool as much as a feature, and a format that can be
/// read in a hex dump is worth more than one that is compact.
/// </para>
/// </summary>
public sealed class ReplayFile
{
    /// <summary>File magic: <c>MVICRPLY</c> as ASCII.</summary>
    public const ulong Magic = 0x594C_5052_4349_564DUL;

    /// <summary>Format version. Bump whenever the layout changes.</summary>
    public const int Version = 2;

    private ReplayFile(
        ulong seed,
        int capacity,
        ScenarioKind scenario,
        string? missionId,
        long finalTick,
        ulong finalHash,
        List<SimCommandRecord> commands)
    {
        Seed = seed;
        Capacity = capacity;
        Scenario = scenario;
        MissionId = missionId;
        FinalTick = finalTick;
        FinalHash = finalHash;
        Commands = commands;
    }

    /// <summary>Seed the world was created with.</summary>
    public ulong Seed { get; }

    /// <summary>Entity capacity, which is part of the state hash.</summary>
    public int Capacity { get; }

    /// <summary>Which scenario was played.</summary>
    public ScenarioKind Scenario { get; }

    /// <summary>Campaign mission played, or null for a skirmish or gallery.</summary>
    public string? MissionId { get; }

    /// <summary>Tick the recording ended on.</summary>
    public long FinalTick { get; }

    /// <summary>State hash at <see cref="FinalTick"/>.</summary>
    public ulong FinalHash { get; }

    /// <summary>External commands in issue order.</summary>
    public IReadOnlyList<SimCommandRecord> Commands { get; }

    /// <summary>
    /// Captures the current world as a replay. The world must have been
    /// recording, otherwise the log is empty and the replay will not match.
    /// </summary>
    public static ReplayFile Capture(SimWorld world, ScenarioKind scenario)
    {
        ArgumentNullException.ThrowIfNull(world);

        return new ReplayFile(
            world.Seed,
            world.Capacity,
            world.Mission is null ? scenario : ScenarioKind.Mission,
            world.Mission?.Id,
            world.Tick,
            StateHash.Compute(world),
            [.. world.RecordedCommands]);
    }

    /// <summary>
    /// Creates a replay from its parts. Exposed for tooling and tests that need
    /// to trim or hand-build a command log; prefer <see cref="Capture"/> when
    /// recording a live match.
    /// </summary>
    public static ReplayFile Create(
        ulong seed,
        int capacity,
        ScenarioKind scenario,
        long finalTick,
        ulong finalHash,
        IEnumerable<SimCommandRecord> commands,
        string? missionId = null)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (capacity <= 0 || capacity > SimConstants.MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity is out of range.");
        }

        if (finalTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(finalTick), finalTick, "Must not be negative.");
        }

        return new ReplayFile(seed, capacity, scenario, missionId, finalTick, finalHash, [.. commands]);
    }

    /// <summary>Writes the replay to a file, replacing any existing one.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.Create(path);
        Save(stream);
    }

    /// <summary>Writes the replay to a stream.</summary>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(Seed);
        writer.Write(Capacity);
        writer.Write((byte)Scenario);
        writer.Write(MissionId ?? string.Empty);
        writer.Write(FinalTick);
        writer.Write(FinalHash);
        writer.Write(Commands.Count);

        foreach (SimCommandRecord record in Commands)
        {
            writer.Write(record.Tick);

            SimCommand command = record.Command;
            writer.Write((byte)command.Kind);
            writer.Write(command.Target.Slot);
            writer.Write(command.Target.Generation);
            writer.Write(command.Destination.X);
            writer.Write(command.Destination.Y);
            writer.Write(command.Destination.Z);
            writer.Write(command.ExecuteTick);
            writer.Write(command.IssuerTeam);
            writer.Write((byte)command.UnitKind);
            writer.Write(command.AttackTarget.Slot);
            writer.Write(command.AttackTarget.Generation);
            writer.Write((byte)command.Tech);
        }
    }

    /// <summary>Reads a replay from a file.</summary>
    public static ReplayFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Reads a replay from a stream. Throws <see cref="InvalidDataException"/>
    /// for anything that is not a replay this build understands, because a
    /// half-read replay that silently replays wrong is worse than a refusal.
    /// </summary>
    public static ReplayFile Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        try
        {
            return Read(reader);
        }
        catch (EndOfStreamException exception)
        {
            // A truncated file is a bad file, not an I/O accident: report it the
            // same way as any other malformed replay.
            throw new InvalidDataException("Replay file is truncated.", exception);
        }
    }

    private static ReplayFile Read(BinaryReader reader)
    {
        ulong magic = reader.ReadUInt64();

        if (magic != Magic)
        {
            throw new InvalidDataException($"Not a MiVic replay (magic 0x{magic:X16}).");
        }

        int version = reader.ReadInt32();

        if (version != Version)
        {
            throw new InvalidDataException($"Replay version {version} is not supported; this build writes and reads version {Version}.");
        }

        ulong seed = reader.ReadUInt64();
        int capacity = reader.ReadInt32();
        var scenario = (ScenarioKind)reader.ReadByte();
        string missionId = reader.ReadString();
        long finalTick = reader.ReadInt64();
        ulong finalHash = reader.ReadUInt64();
        int count = reader.ReadInt32();

        if (capacity <= 0 || capacity > SimConstants.MaxEntities)
        {
            throw new InvalidDataException($"Replay capacity {capacity} is out of range.");
        }

        if (count < 0 || count > 100_000_000)
        {
            throw new InvalidDataException($"Replay command count {count} is out of range.");
        }

        if (!Enum.IsDefined(scenario))
        {
            throw new InvalidDataException($"Replay scenario {(byte)scenario} is unknown.");
        }

        if (scenario == ScenarioKind.Mission && MissionCatalog.Find(missionId) is null)
        {
            throw new InvalidDataException($"Replay names unknown mission '{missionId}'.");
        }

        var commands = new List<SimCommandRecord>(count);

        for (int i = 0; i < count; i++)
        {
            long tick = reader.ReadInt64();
            var kind = (SimCommandKind)reader.ReadByte();
            var target = new EntityId(reader.ReadInt32(), reader.ReadInt32());
            var destination = new WorldPos(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            long executeTick = reader.ReadInt64();
            int issuerTeam = reader.ReadInt32();
            var unitKind = (UnitKind)reader.ReadByte();
            var attackTarget = new EntityId(reader.ReadInt32(), reader.ReadInt32());
            var tech = (TechId)reader.ReadByte();

            commands.Add(new SimCommandRecord(
                tick,
                new SimCommand(kind, target, destination, executeTick, issuerTeam, unitKind, attackTarget, tech)));
        }

        return new ReplayFile(
            seed,
            capacity,
            scenario,
            string.IsNullOrEmpty(missionId) ? null : missionId,
            finalTick,
            finalHash,
            commands);
    }

    /// <summary>
    /// Rebuilds the world from scratch, re-issues the recorded commands at the
    /// ticks they were issued on, and compares the resulting state hash.
    /// </summary>
    public ReplayResult Verify() => Run(this);

    /// <summary>Replays <paramref name="replay"/> into a fresh world.</summary>
    public static ReplayResult Run(ReplayFile replay)
    {
        ArgumentNullException.ThrowIfNull(replay);

        // The match is rebuilt with the world rather than after it: which teams are playing is
        // part of what the scenario lays out — a two-faction match has two bases on the map — and
        // the scenario refuses to lay anything out in a world whose sides disagree with it.
        // A mission carries its own layout and its own sides, so it is rebuilt from its id.
        MissionDefinition? mission = replay.MissionId is { } id ? MissionCatalog.Require(id) : null;

        var world = new SimWorld(replay.Seed, replay.Capacity, MatchRoster.For(replay.Scenario, mission));

        if (mission is not null)
        {
            // Fully qualified: inside this class, "Scenario" binds to the
            // property of the same name rather than to the builder type.
            MiVic.Core.Sim.Scenario.BuildMission(world, mission);
        }
        else
        {
            MiVic.Core.Sim.Scenario.Build(world, replay.Scenario);
        }

        int applied = 0;
        int next = 0;
        IReadOnlyList<SimCommandRecord> commands = replay.Commands;

        // Commands are re-issued before the step that takes the world off the
        // tick they were issued on, which is the same position in the tick they
        // occupied in the original run.
        for (long tick = 0; tick < replay.FinalTick; tick++)
        {
            while (next < commands.Count && commands[next].Tick == tick)
            {
                world.Enqueue(commands[next].Command);
                next++;
                applied++;
            }

            world.Step();
        }

        ulong actual = StateHash.Compute(world);

        return new ReplayResult(replay.FinalTick, replay.FinalHash, actual, world.Tick, applied);
    }
}

/// <summary>Outcome of replaying a recorded match.</summary>
/// <param name="ExpectedTick">Tick the recording ended on.</param>
/// <param name="ExpectedHash">Hash the recording ended with.</param>
/// <param name="ActualHash">Hash the replay produced.</param>
/// <param name="ActualTick">Tick the replay reached.</param>
/// <param name="CommandsApplied">Commands re-issued from the log.</param>
public readonly record struct ReplayResult(
    long ExpectedTick,
    ulong ExpectedHash,
    ulong ActualHash,
    long ActualTick,
    int CommandsApplied)
{
    /// <summary>True when the replay reproduced the recorded match exactly.</summary>
    public bool Matches => ExpectedHash == ActualHash && ExpectedTick == ActualTick;
}
