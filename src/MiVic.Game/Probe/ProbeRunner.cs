using System.Globalization;
using System.Text;
using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;using MiVic.Game.Data;
using MiVic.Game.Sim;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Probe;

/// <summary>
/// Runs a probe script: a list of commands, executed in order, in one process, against a
/// running client.
/// <para>
/// <b>Why a batch script and not an interactive session.</b> The agent driving this tool
/// runs every shell command in a fresh process, so a prompt waiting on stdin would be dead
/// before the second command could be typed. A script file is therefore the only shape a
/// diagnostic channel can take here: one launch loads the models, builds the world and
/// renders PNGs, and every question is asked inside it. Fifty commands in one process is
/// the entire point — a process launch per question is what this replaces.
/// </para>
/// <para>
/// The runner is <em>stepped</em> from the client's update loop, one command per frame. It
/// owns the simulation's clock while it runs: <c>tick</c> advances the world and nothing
/// else does, so the transcript is the same on a fast machine and a slow one. That is also
/// why a probe frame is photographed at a tick boundary rather than between two ticks —
/// a number in the transcript and a pixel in the PNG describe the same state.
/// </para>
/// </summary>
public sealed class ProbeRunner
{
    /// <summary>File the transcript is written to when <c>--probe-out</c> does not say.</summary>
    public const string DefaultReportName = "probe-report.txt";

    /// <summary>Ticks one <c>tick</c> command may ask for: 5000 seconds of game time.</summary>
    private const int MaxTicksPerCommand = 100_000;

    /// <summary>Largest slot index a script may name, so a stale transcript cannot index off the end.</summary>
    private const int MaxSlot = 8191;

    /// <summary>Presentation frames one <c>settle</c> runs when the script does not say.</summary>
    private const int DefaultSettleFrames = 15;

    /// <summary>
    /// Bins in a per-role tally: one per <see cref="UnitKind"/>, with room to spare, so a tally can
    /// be indexed by the enum without asking how many roles there are — the same reason
    /// <see cref="MaxSlot"/> is a bound rather than an exact count.
    /// </summary>
    private const int KindSlots = 32;

    /// <summary>Length of one presentation frame, in seconds. 60 Hz, so effects age as they would on screen.</summary>
    private const float SettleSeconds = 1f / 60f;

    /// <summary>Presentation events kept for the <c>events</c> query.</summary>
    private const int EventHistory = 256;

    private readonly IProbeHost _host;
    private readonly StringBuilder _report = new();
    private readonly List<ProbeEvent> _events = [];
    private readonly List<ProbeEvent> _tickEvents = [];
    private readonly List<int> _teamsThatFired = [];
    private readonly Dictionary<(int Slot, int Part), ProbePose> _poses = [];
    private readonly ProbeCommand[] _commands;
    private readonly string _outputPath;

    /// <summary>
    /// Shots between teams, as ordered pairs: index <c>(shooter * TeamCount) + target</c>. The
    /// alliance is a question about sides, so the ledger that answers "has anything passed
    /// between allies" is kept in the same terms — the same terms <c>teams</c> prints them in.
    /// </summary>
    private readonly int[] _shotsBetweenTeams = new int[SimConstants.TeamCount * SimConstants.TeamCount];

    /// <summary>Damage-carrying events, the health they took and the units lost, per victim team.</summary>
    private readonly int[] _hitsOnTeam = new int[SimConstants.TeamCount];
    private readonly int[] _damageOnTeam = new int[SimConstants.TeamCount];
    private readonly int[] _lossesOnTeam = new int[SimConstants.TeamCount];

    /// <summary>Shots fired by a team at a team it is not at war with. The number has to be zero.</summary>
    private int _shotsAtAllies;

    /// <summary>Damage and losses no hostile weapon fired for on the same tick.</summary>
    private int _unexplainedDamage;

    /// <summary>Ticks on which an armed unit was holding a target of a team it is not at war with.</summary>
    private int _alliedTargets;

    /// <summary>
    /// The world's own firing state, one entry per slot, read every tick.
    /// <para>
    /// The event stream is not enough to account for damage. A shot that kills what it was aimed at
    /// clears the target in the same tick, and the client's tracer detection — which needs a target
    /// still standing at the end of the tick — therefore reports no shot at all for it. That is fine
    /// for drawing and useless for accounting: the most damaging shot of a fight would be the one
    /// nobody fired. So the tick's fire is read from the same two fields the client reads it from —
    /// a cooldown that was zero and is not — plus the target it was aimed at a tick earlier.
    /// </para>
    /// </summary>
    private int[] _previousCooldown = [];
    private int[] _previousTarget = [];

    private int _next;
    private int _eventsSeen;
    private int _ok;
    private int _errors;
    private int _checks;
    private int _checksFailed;
    private bool _finished;

    /// <summary>
    /// Consecutive ticks each slot has spent waiting for a route while holding a move goal, and the
    /// worst any of them has reached since the script started. See <see cref="WatchRoutes"/>.
    /// </summary>
    private int[] _waitingStreak = [];
    private int _worstWaitingStreak;
    private int _worstWaitingSlot = -1;
    private long _worstWaitingTick;

    public ProbeRunner(IProbeHost host, ProbeScriptFile script, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(script);

        _host = host;
        _commands = script.Commands;
        _outputPath = outputPath;

        Emit("MiVic probe report");
        Emit("==================");
        Emit($"script   : {script.Path}");
        Emit($"seed     : {host.Simulation.World.Seed}");
        Emit($"scenario : {host.Simulation.Scenario}");
        Emit($"commands : {_commands.Length}");

        if (script.Error is { } error)
        {
            _errors++;
            Emit($"error: cannot read the script: {error}");
        }

        Emit(string.Empty);
    }

    /// <summary>True when any command failed or any check did not hold.</summary>
    public bool Failed => _errors > 0 || _checksFailed > 0;

    /// <summary>
    /// Runs one command, or finishes the script when there are none left. True means the
    /// client should write the report and exit.
    /// </summary>
    public bool Step()
    {
        if (_finished)
        {
            return true;
        }

        // A script that could not be read has no commands to run, but it still owes the
        // reader a transcript explaining itself.
        if (_commands.Length == 0)
        {
            Finish();
            return true;
        }

        ProbeCommand command = _commands[_next++];
        Emit($"cmd: {command.Text}");

        try
        {
            Execute(command);
            _ok++;
        }
        catch (ProbeException exception)
        {
            // One bad command is one line of the transcript. Aborting here would throw
            // away every answer the other forty-nine lines had already produced.
            _errors++;
            Emit($"error: line {command.Line}: {exception.Message}");
        }

        if (_next >= _commands.Length)
        {
            Finish();
            return true;
        }

        return false;
    }

    /// <summary>Writes the transcript, whether or not the script ran to the end.</summary>
    public void Write()
    {
        string full = Path.GetFullPath(_outputPath);

        try
        {
            string? directory = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(full, _report.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The transcript is the output: failing to write it is a failing run, and the
            // exit code has to say so even though there is nowhere left to say it in.
            _errors++;
            Console.WriteLine($"probe: could not write the report to '{full}': {exception.Message}");
        }
    }

    private void Finish()
    {
        _finished = true;
        Emit(string.Empty);
        Emit($"probe: {_next} commands, {_ok} ok, {_errors} errors, {_checksFailed} checks failed");
    }

    /// <summary>Writes one line to the transcript and to any console that is attached.</summary>
    private void Emit(string line)
    {
        _report.Append(line).Append('\n');

        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // A closed pipe is not a reason to stop answering questions.
        }
    }

    private void Execute(ProbeCommand command)
    {
        switch (command.Verb)
        {
            case "tick":
                Tick(command);
                break;
            case "settle":
                Settle(command);
                break;
            case "shot":
                Shot(command);
                break;
            case "focus":
                Focus(command);
                break;
            case "zoom":
                Zoom(command);
                break;
            case "pitch":
                Pitch(command);
                break;
            case "yaw":
                Yaw(command);
                break;
            case "surfaces":
                Surfaces(command);
                break;
            case "attributes":
                Attributes(command);
                break;
            case "units":
                Units(command);
                break;
            case "routes":
                Routes(command);
                break;
            case "unit":
                UnitDetail(command);
                break;
            case "count":
                Count(command);
                break;
            case "parts":
                Parts(command);
                break;
            case "model":
                Model(command);
                break;
            case "visible":
                Visible(command);
                break;
            case "events":
                Events(command);
                break;
            case "teams":
                Teams(command);
                break;
            case "bridge":
                Bridge(command);
                break;
            case "structure":
                Structure(command);
                break;
            case "structures":
                Structures(command);
                break;
            case "sites":
                Sites(command);
                break;
            case "bridges":
                Bridges(command);
                break;
            case "block":
                Block(command);
                break;
            case "blast":
                Blast(command);
                break;
            case "arm":
                Arm(command);
                break;
            case "hover":
                Hover(command);
                break;
            case "click":
                Click(command);
                break;
            case "hud":
                Hud(command);
                break;
            case "range":
                Range(command);
                break;
            case "power":
                Power(command);
                break;
            case "capacity":
                Capacity(command);
                break;
            case "queue":
                Queue(command);
                break;
            case "detect":
                Detect(command);
                break;
            case "exposure":
                Exposure(command);
                break;
            case "armour":
                Armour(command);
                break;
            case "order":
                Order(command);
                break;
            case "ability":
                Ability(command);
                break;
            case "triggers":
                Triggers(command);
                break;
            case "messages":
                Messages(command);
                break;
            case "objectives":
                Objectives(command);
                break;
            case "expect":
                Expect(command);
                break;
            default:
                throw new ProbeException(
                    $"unknown command '{command.Verb}' — tick, settle, shot, focus, zoom, pitch, yaw, " +
                    "surfaces, attributes, units, unit, count, routes, parts, model, visible, events, teams, bridge, " +
                    "structure, structures, sites, bridges, block, blast, arm, hover, click, hud, " +
                    "range, power, capacity, queue, detect, exposure, armour, order, ability, " +
                    "triggers, messages, objectives, expect");
        }
    }

    // ---------------------------------------------------------------- control

    /// <summary>
    /// Advances the simulation. One tick at a time through the client's own tick path, so a
    /// probe world is the same world a played match would have had, and so each tick's
    /// events can be stamped with the tick they happened on.
    /// </summary>
    private void Tick(ProbeCommand command)
    {
        int ticks = command.Whole(0, "a tick count", "tick <n>", 0, MaxTicksPerCommand);
        SimWorld world = _host.Simulation.World;

        for (int i = 0; i < ticks; i++)
        {
            _host.AdvanceTick();
            HarvestEvents(world.Tick);
            WatchRoutes(world);
        }

        Emit($"ok: ran {ProbeFormat.Ticks(ticks)}, simulation now at tick {world.Tick}");
    }

    /// <summary>
    /// Runs the client's per-frame presentation work, which <c>tick</c> deliberately does
    /// not: the simulation changes only from a tick, and effects only from a frame, and a
    /// probe that ran both together could never ask what the world looked like between them.
    /// </summary>
    private void Settle(ProbeCommand command)
    {
        int frames = (int)command.OptionalNumber(0, DefaultSettleFrames, "a frame count", "settle [frames]");

        if (frames < 1 || frames > 600)
        {
            throw new ProbeException($"settle takes a frame count in 1..600, and {frames} is outside it — usage: settle [frames]");
        }

        for (int i = 0; i < frames; i++)
        {
            _host.SettleFrame(SettleSeconds);
        }

        ProbeFrameStats stats = _host.Stats;

        Emit(
            $"ok: settled {ProbeFormat.Count(frames, "frame")} of {ProbeFormat.Milliseconds(SettleSeconds)} " +
            $"({ProbeFormat.Seconds(frames * SettleSeconds)} of presentation), simulation still at tick {_host.Simulation.World.Tick}, " +
            $"{ProbeFormat.Count(stats.Particles, "particle")} live, {ProbeFormat.Count(stats.Rounds, "round")} in flight");
    }

    private void Shot(ProbeCommand command)
    {
        string path = command.Argument(0, "a PNG path", "shot <file.png>");

        // Relative to the working directory the process was started in, not to the
        // executable: a probe script is a test artifact and lives with the repository, and
        // a shot that landed in bin/Debug is a shot nobody can find.
        string full = Path.GetFullPath(path);

        ProbeCamera camera = _host.ReadCamera();
        ProbeFrameStats stats = _host.RenderFrame();
        _host.SaveFrame(full);

        long bytes = File.Exists(full) ? new FileInfo(full).Length : 0;

        Emit(
            $"ok: shot {full} — {camera.Width}x{camera.Height}, {ProbeFormat.Bytes(bytes)}, " +
            $"{ProbeFormat.Count(stats.InstancesSubmitted, "instance")} in {ProbeFormat.Count(stats.DrawCalls, "draw call")}, " +
            $"tick {_host.Simulation.World.Tick}");
    }

    private void Focus(ProbeCommand command)
    {
        float x = command.Number(0, "an x in metres", "focus <x> <z> [y]");
        float z = command.Number(1, "a z in metres", "focus <x> <z> [y]");
        float? y = command.ArgumentCount > 2 ? command.Number(2, "a height in metres", "focus <x> <z> [y]") : null;

        _host.SetFocus(x, z, y);

        ProbeCamera camera = _host.ReadCamera();

        Emit($"ok: focus {ProbeFormat.Point(camera.Target)}");
    }

    private void Zoom(ProbeCommand command)
    {
        float wanted = command.Number(0, "a distance in metres", "zoom <metres>");
        ProbeCamera before = _host.ReadCamera();
        float took = _host.SetZoom(wanted);
        ProbeCamera camera = _host.ReadCamera();

        string clamped = MathF.Abs(took - wanted) > 0.05f
            ? $" — asked for {ProbeFormat.Metres(wanted)}, clamped to the camera's " +
              $"{ProbeFormat.Metres(before.MinDistance)}..{ProbeFormat.Metres(before.MaxDistance)}"
            : string.Empty;

        Emit($"ok: zoom {ProbeFormat.Metres(took)}{clamped}");
    }

    private void Pitch(ProbeCommand command)
    {
        float wanted = command.Number(0, "a pitch in radians", "pitch <rad>");
        float took = _host.SetPitch(wanted);

        string clamped = MathF.Abs(took - wanted) > 0.0005f
            ? $" — asked for {ProbeFormat.Angle(wanted)}, clamped by the camera's own limit"
            : string.Empty;

        Emit($"ok: pitch {ProbeFormat.Angle(took)}{clamped} (negative looks down)");
    }

    private void Yaw(ProbeCommand command)
    {
        float wanted = command.Number(0, "a yaw in radians", "yaw <rad>");
        float took = _host.SetYaw(wanted);

        Emit($"ok: yaw {ProbeFormat.Angle(took)}");
    }

    // ---------------------------------------------------------------- world queries

    /// <summary>
    /// Counts every surface on the map.
    /// <para>
    /// Every type is listed even at zero, and that is the point of the query: a surface
    /// with a cost, a colour and a shader treatment that no cell ever receives is a bug
    /// that a screenshot cannot show and a census shows in one line.
    /// </para>
    /// </summary>
    private void Surfaces(ProbeCommand command)
    {
        TerrainLayer terrain = _host.Simulation.World.TerrainTypes;
        int total = terrain.CellCount;

        Emit(
            $"query: surfaces: {terrain.Size} x {terrain.Size} cells of {ProbeFormat.Metres(terrain.CellSizeMm / (float)WorldPos.MmPerMetre)} " +
            $"= {ProbeFormat.Count(total, "cell")} over {ProbeFormat.Metres(SimConstants.MapExtentMm / (float)WorldPos.MmPerMetre)}");

        foreach (TerrainType type in Enum.GetValues<TerrainType>())
        {
            int count = terrain.CountOf(type);
            int permille = total > 0 ? (count * 1_000) / total : 0;

            Emit(
                $"query:   {type,-13} {count,5} cells  {permille / 10f,5:0.0}%  {ProbeLabels.SurfaceGreek(type)}");
        }
    }

    /// <summary>
    /// Everything the terrain layer knows about one cell: what it is, what is growing on
    /// it, how wet it is, which way it falls, what shape it is, and what all of that is
    /// worth to each movement class.
    /// </summary>
    private void Attributes(ProbeCommand command)
    {
        float x = command.Number(0, "an x in metres", "attributes <x> <z>");
        float z = command.Number(1, "a z in metres", "attributes <x> <z>");

        SimWorld world = _host.Simulation.World;
        TerrainLayer terrain = world.TerrainTypes;

        int index = terrain.IndexOfWorld(
            (int)(x * WorldPos.MmPerMetre),
            (int)(z * WorldPos.MmPerMetre));

        if (index < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — attributes <x> <z>, and the map is +-300 m");
        }

        TerrainType type = terrain.TypeAt(index);
        TerrainAttributes attributes = terrain.AttributesAt(index);
        int cellX = index % terrain.Size;
        int cellZ = index / terrain.Size;

        Emit($"query: attributes at (x {x:0.0}, z {z:0.0}) m — cell {cellX},{cellZ} of {terrain.Size}, index {index}");
        Emit($"query:   surface    {ProbeLabels.Surface(type)}, churn {terrain.ChurnAt(index)}/255");

        // Height against the water line, because a cell that is water is a cell whose ground
        // is below a line, and the two numbers are what turn "the base is in the sea" from an
        // argument about a picture into arithmetic.
        Emit(
            $"query:   height     {ProbeFormat.Millimetres(world.Navigation.HeightAt(index))} on the navigation lattice, " +
            $"{ProbeFormat.Millimetres(world.Terrain.SampleHeightMm((int)(x * WorldPos.MmPerMetre), (int)(z * WorldPos.MmPerMetre)))} on the height field, " +
            $"against a water line of {ProbeFormat.Millimetres(terrain.WaterLevelMm)} — below that line is water");
        Emit(
            $"query:   ground     vegetation {attributes.Vegetation}/255, moisture {attributes.Moisture}/{TerrainAttributes.MaxMoisture}, " +
            $"aspect {ProbeLabels.Aspect(attributes.Aspect)} ({attributes.Aspect}), landform {ProbeLabels.Landform(attributes.Landform)} ({attributes.Landform}), " +
            $"fuel {attributes.Fuel}, flags {ProbeLabels.Flags(attributes)}");
        Emit($"query:   cover      {Cover(terrain, index, attributes, type)}   (damage that lands: 1000 ‰ is no cover at all)");
        Emit($"query:   going      {Going(terrain, index)}   (flat ground is 100 ‰)");
    }

    private static string Cover(TerrainLayer terrain, int index, TerrainAttributes attributes, TerrainType type)
    {
        var parts = new List<string>();

        foreach (MovementClass movement in Enum.GetValues<MovementClass>())
        {
            if (movement == MovementClass.None)
            {
                continue;
            }

            parts.Add($"{movement.ToString().ToLowerInvariant()} {ProbeFormat.Permille(TerrainLayer.CoverPermille(movement, type, attributes))}");
        }

        return string.Join(", ", parts);
    }

    private static string Going(TerrainLayer terrain, int index)
    {
        var parts = new List<string>();

        foreach (MovementClass movement in Enum.GetValues<MovementClass>())
        {
            if (movement == MovementClass.None)
            {
                continue;
            }

            int cost = terrain.CostPermille(index, movement, 1_000);

            parts.Add(cost == 0
                ? $"{movement.ToString().ToLowerInvariant()} impassable"
                : $"{movement.ToString().ToLowerInvariant()} {ProbeFormat.Permille(cost)}");
        }

        return string.Join(", ", parts);
    }

    // --------------------------------------------------- placement and clicking

    /// <summary>
    /// What the simulation would make of a crossing at a cell, and — when the script asks for
    /// it — the command that builds one.
    /// <para>
    /// A refusal is an answer rather than a failure: the command succeeds, the run's exit code
    /// stays zero, and the transcript carries the reason the simulation gave. That is the point
    /// of the query. Before it, the only way to find out why a click on water had done nothing
    /// was to read the rule and guess which clause had fired.
    /// </para>
    /// </summary>
    private void Bridge(ProbeCommand command)
    {
        const string Usage = "bridge <x> <z> [team] [build]";

        float x = command.Number(0, "an x in metres", Usage);
        float z = command.Number(1, "a z in metres", Usage);

        int team = 0;
        bool build = false;

        // The team and the word `build` are both optional and either order reads naturally,
        // so they are taken by what they are rather than by where they are.
        for (int index = 2; index <= 3; index++)
        {
            if (command.Optional(index) is not { } argument)
            {
                continue;
            }

            if (argument.Equals("build", StringComparison.OrdinalIgnoreCase))
            {
                build = true;
                continue;
            }

            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out team) || (uint)team >= SimConstants.TeamCount)
            {
                throw new ProbeException($"'{argument}' is neither a team number nor 'build' — usage: {Usage}");
            }
        }

        SimWorld world = _host.Simulation.World;
        var target = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));
        int cell = world.TerrainTypes.IndexOfWorld(target.X, target.Z);

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        Span<int> cells = stackalloc int[SimWorld.MaxBridgeCells];
        bool allowed = world.TryPlanBridge(team, target, cells, out int count, out string reason);

        Emit($"query: bridge at (x {x:0.0}, z {z:0.0}) m — {DescribeCell(world, cell)}");

        if (!allowed)
        {
            Emit($"query:   verdict    refused — {reason}");
            Emit($"query:   player     Γέφυρα: {reason}.");
            return;
        }

        Emit($"query:   verdict    accepted for team {team} — {ProbeFormat.Count(count, "cell")} would become ford");
        Emit($"query:   span       {DescribeSpan(world, cells, count)}");
        Emit($"query:   work       {ProbeFormat.Ticks(Bridgeworks.TicksFor(count))} to build, one cell every {ProbeFormat.Ticks(Bridgeworks.TicksPerCell)}");

        BridgeCost cost = Bridgeworks.Cost(count);
        BridgeCost cheapest = Bridgeworks.Cost(1);
        TeamState state = world.Team(team);

        Emit(
            $"query:   cost       {cost.Materials} Π, {cost.Energy} Ε, {cost.Water} Ν for {ProbeFormat.Count(count, "cell")} " +
            $"({cheapest.Materials} Π, {cheapest.Energy} Ε, {cheapest.Water} Ν, plus " +
            $"{Bridgeworks.MaterialsPerCell} Π, {Bridgeworks.EnergyPerCell} Ε, {Bridgeworks.WaterPerCell} Ν per cell)");
        Emit($"query:   team       {team} has {state.Materials} Π, {state.Energy} Ε, {state.Water} Ν");

        if (!build)
        {
            return;
        }

        long executeTick = world.Tick + 1;
        world.Enqueue(SimCommand.Bridge(target, executeTick, team));
        Emit($"ok: bridge ordered for team {team}, executing on tick {executeTick} — `tick {(int)(executeTick - world.Tick)}` starts the work");
    }

    /// <summary>
    /// What the simulation would make of raising a structure at a cell, and — when the script
    /// asks for it — the order that raises one.
    /// <para>
    /// The shape of <c>bridge</c>, because the question is the same question about a different
    /// thing: would this building be accepted here, and if not, why not. A refusal is an answer
    /// rather than a failure — the command succeeds, the run's exit code stays zero, and the
    /// transcript carries the reason the simulation gave — and a script that wants to see the
    /// structure standing follows the order with the ticks it takes to rise.
    /// </para>
    /// </summary>
    private void Structure(ProbeCommand command)
    {
        const string Usage = "structure <kind> <x> <z> [team] [build]";

        UnitKind kind = ParseKind(command.Argument(0, "a role such as Factory, PowerPlant or CommandCentre", Usage));

        float x = command.Number(1, "an x in metres", Usage);
        float z = command.Number(2, "a z in metres", Usage);

        int team = 0;
        bool build = false;

        // The team and the word `build` are both optional and either order reads naturally,
        // so they are taken by what they are rather than by where they are.
        for (int index = 3; index <= 4; index++)
        {
            if (command.Optional(index) is not { } argument)
            {
                continue;
            }

            if (argument.Equals("build", StringComparison.OrdinalIgnoreCase))
            {
                build = true;
                continue;
            }

            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out team) ||
                (uint)team >= SimConstants.TeamCount)
            {
                throw new ProbeException($"'{argument}' is neither a team number nor 'build' — usage: {Usage}");
            }
        }

        SimWorld world = _host.Simulation.World;
        var site = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));
        int cell = world.TerrainTypes.IndexOfWorld(site.X, site.Z);

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        UnitDefinition definition = UnitCatalog.Get(kind);
        bool allowed = world.TryPlanStructure(team, kind, site, out WorldPos planned, out string reason);

        Emit($"query: structure {ProbeLabels.KindName(kind)} at (x {x:0.0}, z {z:0.0}) m — {DescribeCell(world, cell)}");

        if (!allowed)
        {
            Emit($"query:   verdict    refused — {reason}");
            Emit($"query:   player     {FactionPalette.UnitLabel(kind)}: {reason}.");
            return;
        }

        Emit(
            $"query:   verdict    accepted for team {team} — it would stand at " +
            $"{ProbeFormat.Ground(planned)}, {DescribeCell(world, world.TerrainTypes.IndexOfWorld(planned.X, planned.Z))}");

        int ticks = UnitCatalog.BuildTicks(world.FactionOfTeam(team), kind);

        Emit(
            $"query:   work       {ProbeFormat.Ticks(ticks)} of construction, rising out of the ground " +
            $"over the whole of it");

        TeamState state = world.Team(team);

        Emit(
            $"query:   cost       {UnitCatalog.MaterialCost(world.FactionOfTeam(team), kind)} Π, " +
            $"{UnitCatalog.EnergyCost(world.FactionOfTeam(team), kind)} Ε, " +
            $"{UnitCatalog.WaterCost(world.FactionOfTeam(team), kind)} Ν, " +
            $"{definition.Health} hit points");
        Emit($"query:   team       {team} has {state.Materials} Π, {state.Energy} Ε, {state.Water} Ν");

        if (!build)
        {
            return;
        }

        long executeTick = world.Tick + 1;
        world.Enqueue(SimCommand.Structure(kind, planned, executeTick, team));

        Emit(
            $"ok: {FactionPalette.UnitLabel(kind)} ordered for team {team} at " +
            $"{ProbeFormat.Ground(planned)}, executing on tick {executeTick} — " +
            $"`tick {(int)(executeTick - world.Tick)}` starts it and `tick {ticks}` finishes it");
    }

    /// <summary>
    /// A role name from a script: the catalogue's own name, or the Greek label the panels show,
    /// because a script that has just read a panel is likely to type what is on it.
    /// </summary>
    private static UnitKind ParseKind(string text)
    {
        if (Enum.TryParse(text, ignoreCase: true, out UnitKind kind) && UnitCatalog.TryGet(kind, out _))
        {
            return kind;
        }

        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (string.Equals(FactionPalette.UnitLabel(definition.Kind), text, StringComparison.OrdinalIgnoreCase))
            {
                return definition.Kind;
            }
        }

        throw new ProbeException($"'{text}' is not a role the catalogue knows");
    }

    /// <summary>
    /// Every crossing on the map, with the work done on it. A bridge is built a cell at a time
    /// over several seconds, so "is there a bridge" is not a yes or no question while it is
    /// going up — and this is where a dialogue about nothing appearing on screen gets settled.
    /// </summary>
    private void Bridges(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;
        Bridgeworks bridgeworks = world.Bridgeworks;

        Emit($"query: crossings: {ProbeFormat.Count(bridgeworks.Count, "crossing")} on the map, tick {world.Tick}");

        for (int bridge = 0; bridge < bridgeworks.Count; bridge++)
        {
            ReadOnlySpan<int> cells = bridgeworks.Cells(bridge);
            BridgeState state = bridgeworks.State(bridge);
            int size = world.TerrainTypes.Size;
            int first = cells[0];

            string condition = state.Cut ? "cut" : state.Complete ? "whole" : "under construction";
            string progress = state.Complete && !state.Cut
                ? condition
                : $"{condition} — {state.Built}/{state.Total} reached, {state.Standing} standing, " +
                  $"{ProbeFormat.Ticks(state.RemainingTicks(world.Tick))} of work left";

            Emit(
                $"query:   #{bridge} {DescribeSpan(world, cells, cells.Length)} — {progress}, " +
                $"started on tick {state.StartTick}, whole on tick {state.ReadyTick}, from ({first % size},{first / size})");
        }
    }

    /// <summary>
    /// Every structure a team has: what it is, where it stands, and how much of it is up.
    /// <para>
    /// The command that answers what a placement did. <c>count</c> says how many of a role exist
    /// and <c>units</c> buries one line among five hundred, so neither says where the building a
    /// player ordered ended up or whether it has finished rising — which is why a crossing has
    /// <c>bridges</c>. A structure ordered at a site is a building site for the whole of its
    /// construction, and that is a state worth being able to see rather than infer.
    /// </para>
    /// </summary>
    private void Structures(ProbeCommand command)
    {
        const string Usage = "structures [team]";

        int team = (int)command.OptionalNumber(0, 0f, "a team number", Usage);

        if ((uint)team >= SimConstants.TeamCount)
        {
            throw new ProbeException($"there is no team {team} — usage: {Usage}");
        }

        SimWorld world = _host.Simulation.World;
        int found = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).TeamId == team &&
                UnitCatalog.Get(world.GetRefBySlot(slot).Kind).IsBuilding)
            {
                found++;
            }
        }

        Emit($"query: structures: {ProbeFormat.Count(found, "structure")} for team {team}, tick {world.Tick}");

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || !UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            // The cell, not only the metres: a placement is an answer about a cell, and the
            // question this command is asked is whether the building came up where it was aimed.
            int cell = world.TerrainTypes.IndexOfWorld(entity.Position.X, entity.Position.Z);
            int size = world.TerrainTypes.Size;

            string where = cell >= 0 ? $", cell {cell % size},{cell / size}" : ", off the map";

            // A structure that has not finished rising is the state a placement produces, and it
            // is the one a transcript about placement has to be able to show.
            string state = entity.ConstructionTicksRemaining > 0
                ? $"building — {entity.ConstructionTicksRemaining} of {entity.ConstructionTicksTotal} ticks left, " +
                  $"{ProbeFormat.Seconds(entity.ConstructionTicksRemaining / (double)SimConstants.TickRate)}"
                : "whole";

            Emit(
                $"query:   slot {slot,4} {ProbeLabels.KindName(entity.Kind)} at {ProbeFormat.Ground(entity.Position)}" +
                $"{where} — {state}, {entity.Health} hit points");
        }
    }

    /// <summary>
    /// How much of the ground around a point would take a structure: a census of the placement rule,
    /// cell by cell, with the reason each refusal gives.
    /// <para>
    /// <c>structure</c> answers the question about one cell, which is what a player aiming asks. This
    /// answers it about a neighbourhood, which is what "is this rule usable" is: a rule that accepts
    /// fifteen cells within a hundred metres of a base is a rule a player cannot build under, and no
    /// single <c>structure</c> line can say so. The count is the instrument the footprint change was
    /// measured with, and it is here so that the next change to the rule is measured the same way
    /// rather than argued about.
    /// </para>
    /// <para>
    /// It is a census rather than a total, because a total hides which clause is doing the work: the
    /// ground the rule will not have, the buildings already standing there, and the cells that are
    /// not ground at all are three different problems with three different answers, and only one of
    /// them is the rule's fault. The simulation's own predicates are asked one cell at a time —
    /// <see cref="SimWorld.CanPlaceStructure"/> for the ground and <see cref="SimWorld.IsSiteClear"/>
    /// for what stands on it — so a transcript cannot disagree with a click.
    /// </para>
    /// </summary>
    private void Sites(ProbeCommand command)
    {
        const string Usage = "sites <kind> <x> <z> [radius in metres] [team]";

        UnitKind kind = ParseKind(command.Argument(0, "a role such as Factory, PowerPlant or CommandCentre", Usage));

        float x = command.Number(1, "an x in metres", Usage);
        float z = command.Number(2, "a z in metres", Usage);

        float radiusMetres = command.OptionalNumber(3, 100f, "a radius in metres", Usage);
        int team = (int)command.OptionalNumber(4, 0f, "a team number", Usage);

        if ((uint)team >= SimConstants.TeamCount)
        {
            throw new ProbeException($"there is no team {team} — usage: {Usage}");
        }

        if (radiusMetres <= 0f)
        {
            throw new ProbeException($"a radius of {radiusMetres} m is not a neighbourhood — usage: {Usage}");
        }

        SimWorld world = _host.Simulation.World;
        var centre = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));
        int centreCell = world.Navigation.IndexOfWorld(centre);
        int centreX = world.Navigation.CellX(centreCell);
        int centreZ = world.Navigation.CellZ(centreCell);
        int radiusCells = (int)MathF.Round(radiusMetres * WorldPos.MmPerMetre / world.Navigation.CellSizeMm);

        int cells = 0;
        int accepted = 0;
        int ground = 0;
        var groundReasons = new List<(string Reason, int Count)>();
        var occupiedReasons = new List<(string Reason, int Count)>();

        int nearest = int.MaxValue;
        WorldPos nearestSite = default;

        for (int dz = -radiusCells; dz <= radiusCells; dz++)
        {
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
            {
                int cell = world.Navigation.IndexOf(centreX + dx, centreZ + dz);

                if (cell < 0)
                {
                    continue;
                }

                WorldPos site = world.Navigation.CentreOf(cell);

                cells++;

                if (!world.CanPlaceStructure(kind, site, out string groundReason))
                {
                    Tally(groundReasons, groundReason);
                    continue;
                }

                ground++;

                if (!world.IsSiteClear(kind, site, out string occupiedReason))
                {
                    Tally(occupiedReasons, occupiedReason);
                    continue;
                }

                accepted++;

                int distance = site.HorizontalDistanceTo(centre);

                if (distance < nearest)
                {
                    nearest = distance;
                    nearestSite = site;
                }
            }
        }

        string percent = cells > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({accepted * 100.0 / cells:0.0}%)")
            : string.Empty;

        int side = (2 * UnitCatalog.FootprintRadiusCells(kind)) + 1;

        Emit(
            $"query: sites for {ProbeLabels.KindName(kind)} within {ProbeFormat.Metres(radiusMetres)} of " +
            $"{ProbeFormat.Ground(centre)} — {ProbeFormat.Count(cells, "cell")} on the map at {ProbeFormat.Metres(world.Navigation.CellSizeMm / (float)WorldPos.MmPerMetre)} each");

        Emit($"query:   accepted   {accepted} of {cells}{percent} would take {FactionPalette.UnitLabel(kind)}, for team {team}");

        if (accepted > 0)
        {
            Emit(
                $"query:   nearest    {ProbeFormat.Ground(nearestSite)}, {ProbeFormat.Millimetres(nearest)} from the centre of the search");
        }

        // The two rules, one after the other, because the numbers have to add up for the transcript
        // to be evidence: every cell is either refused for its ground, refused for what is standing
        // on it, or accepted, and the census says which and how many.
        Emit(
            $"query:   footprint  {ground} of {cells} cells pass the ground rule for a {side} x {side} footprint " +
            $"({side * side} cells of ground, radius {UnitCatalog.FootprintRadiusCells(kind)})");

        foreach ((string reason, int count) in Sorted(groundReasons))
        {
            Emit($"query:     {count,4} cells  {reason}");
        }

        Emit($"query:   standing   {ground - accepted} of the {ground} would find a structure already on the ground");

        foreach ((string reason, int count) in Sorted(occupiedReasons))
        {
            Emit($"query:     {count,4} cells  {reason}");
        }
    }

    /// <summary>Counts one more cell against a reason.</summary>
    private static void Tally(List<(string Reason, int Count)> tallies, string reason)
    {
        for (int i = 0; i < tallies.Count; i++)
        {
            if (tallies[i].Reason == reason)
            {
                tallies[i] = (reason, tallies[i].Count + 1);
                return;
            }
        }

        tallies.Add((reason, 1));
    }

    /// <summary>
    /// A census in a fixed order: most cells first, and the reason's own words to break a tie. A
    /// transcript that listed its reasons in whatever order they turned up would be a different file
    /// for the same world, which is the one thing a probe may not be.
    /// </summary>
    private static List<(string Reason, int Count)> Sorted(List<(string Reason, int Count)> tallies)
    {
        var copy = new List<(string Reason, int Count)>(tallies);
        copy.Sort((a, b) => a.Count != b.Count ? b.Count - a.Count : string.CompareOrdinal(a.Reason, b.Reason));
        return copy;
    }

    /// <summary>
    /// What deck stands on one cell: how much is left of it, whose it is, and which way it runs —
    /// including whether it is a junction, which is a fact about the cell rather than about any
    /// crossing and is therefore not in the <c>bridges</c> list.
    /// </summary>
    private void Block(ProbeCommand command)
    {
        const string Usage = "block <x> <z>";

        float x = command.Number(0, "an x in metres", Usage);
        float z = command.Number(1, "a z in metres", Usage);

        SimWorld world = _host.Simulation.World;
        TerrainLayer terrain = world.TerrainTypes;
        int cell = terrain.IndexOfWorld((int)(x * WorldPos.MmPerMetre), (int)(z * WorldPos.MmPerMetre));

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        Emit($"query: cell {DescribeCell(world, cell)} at (x {x:0.0}, z {z:0.0}) m");

        if (!world.Bridgeworks.TryGetBlock(cell, out BridgeBlock block))
        {
            Emit("query:   deck       none — no crossing covers this cell");
            return;
        }

        Emit(
            $"query:   deck       {block.Health}/{Bridgeworks.BlockHealth} left, owner team {block.Team}, " +
            $"runs {block.Axis}");

        // Which crossings pass through it, which is what makes a junction a junction.
        for (int bridge = 0; bridge < world.Bridgeworks.Count; bridge++)
        {
            ReadOnlySpan<int> cells = world.Bridgeworks.Cells(bridge);

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == cell)
                {
                    Emit($"query:   crossing   #{bridge} runs through it, at cell {i} of {cells.Length}");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Drops a blast on the ground, as a salvo or a strike does, so a script can watch a crossing
    /// come down. The one command here that damages anything: a bridge that cannot be knocked down
    /// cannot be inspected, and there is no other way to ask what a hole in one looks like.
    /// </summary>
    private void Blast(ProbeCommand command)
    {
        const string Usage = "blast <x> <z> [radius] [damage] [team]";

        float x = command.Number(0, "an x in metres", Usage);
        float z = command.Number(1, "a z in metres", Usage);
        float radius = command.OptionalNumber(2, 22f, "a radius in metres", Usage);
        int damage = (int)command.OptionalNumber(3, 95f, "damage", Usage);
        int team = (int)command.OptionalNumber(4, 2f, "a team number", Usage);

        if (radius <= 0f || damage <= 0)
        {
            throw new ProbeException($"a blast needs a positive radius and damage — usage: {Usage}");
        }

        SimWorld world = _host.Simulation.World;
        TerrainLayer terrain = world.TerrainTypes;
        int centreX = (int)(x * WorldPos.MmPerMetre);
        int centreZ = (int)(z * WorldPos.MmPerMetre);
        int cell = terrain.IndexOfWorld(centreX, centreZ);

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        int knockedOut = world.Bridgeworks.DamageArea(
            terrain,
            centreX,
            centreZ,
            (int)(radius * WorldPos.MmPerMetre),
            damage,
            team);

        Emit(
            $"ok: blast at (x {x:0.0}, z {z:0.0}) m, radius {ProbeFormat.Metres(radius)}, {damage} damage from team {team} — " +
            $"{ProbeFormat.Count(knockedOut, "block")} knocked out");

        if (world.Bridgeworks.TryGetBlock(cell, out BridgeBlock block))
        {
            Emit($"query:   deck       {DescribeCell(world, cell)} — {block.Health}/{Bridgeworks.BlockHealth} left, owner team {block.Team}, {block.Axis}");
        }
        else
        {
            Emit($"query:   deck       {DescribeCell(world, cell)} — no deck standing here");
        }
    }

    /// <summary>
    /// Arms a placement the way its button does, through the client's own HUD path, and says
    /// whether the client is now waiting for a site. A script that has to arm a placement
    /// before it can ask what a placement would do has to arm it the way a player does.
    /// <para>
    /// <c>bridge</c> is the support panel's button; anything else is a role name and is a row in
    /// the production panel, which arms a site for that structure rather than ordering one.
    /// </para>
    /// </summary>
    private void Arm(ProbeCommand command)
    {
        string what = command.Argument(0, "what to arm: 'bridge', or a structure role such as Factory", "arm <bridge|role>").ToLowerInvariant();

        if (what == "bridge")
        {
            bool armed = _host.ArmBridge(out string note);

            Emit($"query: arm bridge — {note}");
            Emit($"query:   result     {(armed ? "the next left click picks the site" : "nothing is waiting for a click")}");
            return;
        }

        UnitKind kind = ParseKind(what);
        bool structureArmed = _host.ArmStructure(kind, out string structureNote);

        Emit($"query: arm {what} — {structureNote}");
        Emit($"query:   result     {(structureArmed ? "the next left click picks the site" : "nothing is waiting for a click")}");
    }

    /// <summary>
    /// Puts the script's cursor over a world point. It reports the pixel that point projects to
    /// and then what the client resolves that pixel back to, which is the round trip every click
    /// makes — and when a placement is armed, the verdict and footprint of the ghost the player
    /// would be looking at.
    /// <para>
    /// The point is aimed at the surface the renderer draws, the water line included, because
    /// that is what a cursor is over: a player pointing at a lake is pointing at the water, not
    /// at the bed under it. A height can be given for something that is not on the ground, as
    /// <c>focus</c> allows.
    /// </para>
    /// </summary>
    private void Hover(ProbeCommand command)
    {
        const string Usage = "hover <x> <z> [y]";

        float x = command.Number(0, "an x in metres", Usage);
        float z = command.Number(1, "a z in metres", Usage);
        float y = command.OptionalNumber(2, _host.DrawnHeightMetres(x, z), "a y in metres", Usage);

        SimWorld world = _host.Simulation.World;

        if (!_host.TryGroundToPixel(new Vector3(x, y, z), out Vector2 pixel))
        {
            throw new ProbeException($"({x}, {z}) m does not project onto the screen — {Usage}");
        }

        Emit($"query: hover at (x {x:0.0}, z {z:0.0}) m on the drawn surface ({y:0.0} m) — pixel ({pixel.X:0.0}, {pixel.Y:0.0}) of {_host.ReadCamera().Width}x{_host.ReadCamera().Height}");

        ProbeCursor cursor = _host.SetCursor(pixel);

        if (!cursor.Resolved)
        {
            Emit("query:   ground     the client finds no ground under that pixel");
            return;
        }

        int cell = cursor.Cell;

        Emit(
            $"query:   ground     resolved to {ProbeFormat.Point(cursor.Ground)} — {DescribeCell(world, cell)}, " +
            $"{ProbeFormat.Metres(Vector2.Distance(new Vector2(cursor.Ground.X, cursor.Ground.Z), new Vector2(x, z)))} from where it was aimed");

        if (!cursor.Armed)
        {
            Emit("query:   placement  nothing armed, so no ghost is drawn");
            return;
        }

        // What is armed is part of the answer: a green ghost over a lake means one thing for a
        // crossing and another for a power plant, and a transcript that only said "accepted"
        // would leave the reader to guess which rule had just been satisfied. A structure's cells
        // are the ground its own footprint covers, which is what the plan judged and what its
        // refusal names.
        string what = cursor.ArmedKind == UnitKind.None
            ? "the ghost takes " + ProbeFormat.Count(cursor.Footprint, "cell") + " and is "
            : $"the ghost is the {FactionPalette.UnitLabel(cursor.ArmedKind)} on " +
              ProbeFormat.Count(cursor.Footprint, "cell") + " of ground and is ";

        Emit(cursor.SiteAllowed
            ? $"query:   placement  accepted — {what}green"
            : $"query:   placement  refused — {cursor.SiteReason}; the ghost is red and the panel says so");
    }

    /// <summary>
    /// Releases the left button at the script's cursor, down the client's own click path, and
    /// reports what the player would have seen: the order that was issued, or the reason it was
    /// not. This is the query that answers "I clicked on the water and nothing happened".
    /// </summary>
    private void Click(ProbeCommand command)
    {
        // An optional world point is a courtesy: the script can aim and click in one line
        // instead of hovering first. The click itself always happens at the script's cursor,
        // which is the point of the command being separate from `hover` at all.
        if (command.Optional(0) is not null)
        {
            Hover(command);
        }

        ProbeClick click = _host.ClickAtCursor();
        SimWorld world = _host.Simulation.World;

        Emit($"query: click — armed {(click.Armed ? "yes" : "no")}, resolved {(click.Resolved ? ProbeFormat.Point(click.Ground) : "nothing")}");

        if (click.Resolved)
        {
            Emit($"query:   ground     {DescribeCell(world, world.TerrainTypes.IndexOfWorld(click.GroundMm.X, click.GroundMm.Z))}");
        }

        Emit($"query:   result     {(click.Queued ? "an order was issued" : "nothing was issued")}");
        Emit($"query:   notice     {(click.Message.Length > 0 ? $"the player is told \"{click.Message}\"" : "the player is told nothing")}");
    }

    /// <summary>
    /// Whether probe frames draw the HUD. Off by default, because a probe reads the world and a
    /// panel over the frame is a panel over the answer — and on when the answer is the panel.
    /// </summary>
    private void Hud(ProbeCommand command)
    {
        string? state = command.Optional(0);

        if (state is null)
        {
            Emit($"query: hud — the HUD is {( _host.HudDrawn ? "drawn into probe frames" : "not drawn")}, notice \"{_host.HudNotice}\"");
            return;
        }

        bool on = state.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ProbeException($"'{state}' is not on or off — usage: hud [on|off]"),
        };

        _host.HudDrawn = on;
        Emit($"ok: the HUD is {(on ? "now" : "no longer")} drawn into probe frames");
    }

    /// <summary>A cell as the transcript names it: index, coordinate, surface.</summary>
    private static string DescribeCell(SimWorld world, int cell)
    {
        if (cell < 0)
        {
            return "off the map";
        }

        TerrainLayer terrain = world.TerrainTypes;
        TerrainType type = terrain.TypeAt(cell);

        return $"cell {cell % terrain.Size},{cell / terrain.Size} of {terrain.Size}, index {cell}, " +
               $"{ProbeLabels.Surface(type)}";
    }

    /// <summary>
    /// The two ends of a span and the axis it runs along. The ends are the extremes of the
    /// footprint rather than its first and last entry, because a span is written from the cell
    /// the player clicked — an end of it in neither direction.
    /// </summary>
    private static string DescribeSpan(SimWorld world, ReadOnlySpan<int> cells, int count)
    {
        if (count == 0)
        {
            return "no cells";
        }

        int size = world.TerrainTypes.Size;
        int low = cells[0];
        int high = cells[0];

        for (int i = 1; i < count; i++)
        {
            low = Math.Min(low, cells[i]);
            high = Math.Max(high, cells[i]);
        }

        // A span runs along one axis, so the two extremes share a row when it runs along x and
        // are a row apart when it runs along z.
        bool alongX = high - low < size;

        return $"{ProbeFormat.Count(count, "cell")} along {(alongX ? "x" : "z")}, " +
               $"({low % size},{low / size}) to ({high % size},{high / size}), " +
               $"{ProbeFormat.Ground(world.Navigation.CentreOf(low))} to {ProbeFormat.Ground(world.Navigation.CentreOf(high))}";
    }

    /// <summary>How long a unit may wait for a route before the wait stops being a queue.</summary>
    /// <remarks>
    /// Path searches are budgeted at <see cref="SimConstants.MaxPathsPerTick"/> a tick, so a hundred
    /// units ordered at once are served inside twenty-five ticks and a whole army inside a few
    /// hundred. A unit still waiting after this many consecutive ticks is not queued behind anybody:
    /// it is asking for something it is not going to get, which is the state this command exists to
    /// catch. It was <b>116</b> units and counting on the standard skirmish before the clipped goal
    /// and the route budget were fixed, and the worst wait after is <b>42</b> ticks in that skirmish
    /// and <b>59</b> in the client's own match, which carries the wander orders as well.
    /// </remarks>
    private const int RouteQueueGraceTicks = 100;

    /// <summary>
    /// <b>The map's routing state, which is how "is anything left stalled" is asked.</b> A unit
    /// waiting for a route is not by itself a fault — path searches are budgeted at four a tick and
    /// a hundred units ordered at once queue for their routes — so this counts the units waiting
    /// <em>with a live move goal</em>, says how long the worst of them has been doing it, and names
    /// them one by one with the goal it is not reaching and what it is chasing.
    /// <para>
    /// The streak is kept by <see cref="WatchRoutes"/>, which <c>tick</c> runs after every simulated
    /// tick, because a single reading cannot tell a queue from a loop: a unit that waits thirty ticks
    /// out of every forty is stalled by any measure a player would use and by none that one tick can
    /// see. This is the command that found `path 0 cells at 0, waiting for a route, 0 failures` on a
    /// unit that had not moved a millimetre in two hundred ticks — and it records a check of its own,
    /// so a transcript that reads a stall and exits 0 is a transcript that hides it.
    /// </para>
    /// </summary>
    private void Routes(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;

        int alive = 0;
        int withARoute = 0;
        int waiting = 0;
        int queued = 0;
        int waitingWithAGoal = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            alive++;

            if (entity.PathLength > 0)
            {
                withARoute++;
            }

            if (entity.NeedsPath)
            {
                queued++;
            }

            if (!entity.NeedsPath || entity.PathLength != 0)
            {
                continue;
            }

            waiting++;

            if (!entity.HasMoveGoal)
            {
                continue;
            }

            waitingWithAGoal++;

            Emit(
                $"query:   slot {slot,4} {entity.Faction.ToString().ToLowerInvariant()} {entity.Kind,-16} team {entity.TeamId} " +
                $"waiting {ProbeFormat.Ticks(_waitingStreak.Length > slot ? _waitingStreak[slot] : 0)} " +
                $"for goal {ProbeFormat.Ground(entity.MoveGoal)}, " +
                $"{ProbeFormat.Metres(entity.Position.HorizontalDistanceTo(entity.MoveGoal) / (float)WorldPos.MmPerMetre)} to go, " +
                $"{entity.PathFailures} failures" +
                (entity.TargetSlot >= 0
                    ? $", attacking slot {entity.TargetSlot} ({DescribeTarget(world, entity.TargetSlot)})"
                    : string.Empty) +
                (entity.HasAttackOrder ? ", order explicit" : string.Empty));
        }

        Emit(
            $"query: routes: {ProbeFormat.Count(alive, "entity", "entities")} alive, " +
            $"{ProbeFormat.Count(withARoute, "unit")} holding a route, {ProbeFormat.Count(waiting, "unit")} waiting for one " +
            $"({waitingWithAGoal} of them with a move goal), {queued} requests outstanding");
        Emit(
            "query:   worst      " +
            (_worstWaitingSlot < 0
                ? "no unit has waited for a route at all"
                : $"slot {_worstWaitingSlot} waited {ProbeFormat.Ticks(_worstWaitingStreak)} with a live move goal, seen on tick {_worstWaitingTick}") +
            $" — a route queue drains at {SimConstants.MaxPathsPerTick} searches a tick");

        RecordCheck(
            "no unit is left waiting for a route",
            _worstWaitingStreak < RouteQueueGraceTicks,
            _worstWaitingStreak < RouteQueueGraceTicks
                ? $"the longest wait with a live move goal is {ProbeFormat.Ticks(_worstWaitingStreak)}"
                : $"slot {_worstWaitingSlot} has waited {ProbeFormat.Ticks(_worstWaitingStreak)} for a route it is not getting, " +
                  $"since tick {_worstWaitingTick}; a queue drains in tens of ticks and this is a loop");
    }

    /// <summary>The target of one entity, for a line that has to say what it is chasing.</summary>
    private static string DescribeTarget(SimWorld world, int slot)
        => !world.IsAliveSlot(slot)
            ? "nothing alive"
            : $"{world.GetRefBySlot(slot).Faction.ToString().ToLowerInvariant()}/{world.GetRefBySlot(slot).Kind}";

    /// <summary>
    /// Keeps the per-slot count of consecutive ticks spent waiting for a route with a live move goal.
    /// Called for every tick a probe runs, because a stall is a fact about a stretch of ticks and not
    /// about one of them.
    /// </summary>
    private void WatchRoutes(SimWorld world)
    {
        if (_waitingStreak.Length < world.Capacity)
        {
            _waitingStreak = new int[world.Capacity];
        }

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                _waitingStreak[slot] = 0;
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.NeedsPath && entity.PathLength == 0 && entity.HasMoveGoal)
            {
                _waitingStreak[slot]++;

                if (_waitingStreak[slot] > _worstWaitingStreak)
                {
                    _worstWaitingStreak = _waitingStreak[slot];
                    _worstWaitingSlot = slot;
                    _worstWaitingTick = world.Tick;
                }
            }
            else
            {
                _waitingStreak[slot] = 0;
            }
        }
    }

    /// <summary>
    /// One line per live entity, which is the answer to "what is on the map at all".
    /// </summary>
    private void Units(ProbeCommand command)    {
        string? filter = command.Optional(0);
        string? limitText = command.Optional(1);

        Faction? faction = null;
        int? team = null;

        if (filter is not null)
        {
            if (ProbeLabels.TryFaction(filter, out Faction parsed))
            {
                faction = parsed;
            }
            else if (int.TryParse(filter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTeam))
            {
                team = parsedTeam;
            }
            else
            {
                throw new ProbeException(
                    $"'{filter}' is not a faction (soviet, chinese, western) or a team number — units [faction|team] [limit]");
            }
        }

        int limit = limitText is null
            ? int.MaxValue
            : ParseLimit(limitText, "units [faction|team] [limit]");

        SimWorld world = _host.Simulation.World;

        Emit($"query: units: {DescribeAlive(world)}");

        int shown = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if ((faction is { } wanted && entity.Faction != wanted) || (team is { } wantedTeam && entity.TeamId != wantedTeam))
            {
                continue;
            }

            if (shown >= limit)
            {
                continue;
            }

            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            Emit(
                $"query:   slot {slot,4} {entity.Faction.ToString().ToLowerInvariant()} {entity.Kind,-16} team {entity.TeamId} " +
                $"at {ProbeFormat.Ground(entity.Position)} heading {ProbeFormat.Degrees(SimBridge.HeadingRadians(entity.Heading))} " +
                $"health {entity.Health}/{definition.Health} target {(entity.TargetSlot < 0 ? "-" : entity.TargetSlot.ToString(CultureInfo.InvariantCulture))} " +
                $"building {(definition.IsBuilding ? "yes" : "no")}");

            shown++;
        }

        Emit($"query:   {ProbeFormat.Count(shown, "entity", "entities")} listed");
    }

    private static string DescribeAlive(SimWorld world)
    {
        var perFaction = new List<string>();
        int total = 0;

        foreach (Faction faction in new[] { Faction.Soviet, Faction.Chinese, Faction.Western })
        {
            int count = 0;

            for (int slot = 0; slot < world.Capacity; slot++)
            {
                if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Faction == faction)
                {
                    count++;
                }
            }

            total += count;
            perFaction.Add($"{faction.ToString().ToLowerInvariant()} {count}");
        }

        return $"{ProbeFormat.Count(total, "entity", "entities")} alive ({string.Join(", ", perFaction)}), {world.AliveCount} in the world's own count";
    }

    /// <summary>Everything about one entity, including what it is doing and what it is doing it to.</summary>
    private void UnitDetail(ProbeCommand command)
    {
        int slot = command.Whole(0, "a slot number", "unit <slot>", 0, MaxSlot);

        SimWorld world = _host.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — unit <slot>; `units` lists what does");
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(entity.Kind);
        int cell = world.Navigation.IndexOfWorld(entity.Position);
        int step = MovementSystem.StepMmPerTick(world, slot);
        ProbeCamera camera = _host.ReadCamera();
        bool visible = !_host.DrawsFogOfWar ||
            entity.TeamId == _host.ViewerTeam ||
            (!world.IsHiddenFrom(_host.ViewerTeam, slot) && world.Visibility.IsVisible(_host.ViewerTeam, cell));

        Emit($"query: unit {slot} {entity.Faction.ToString().ToLowerInvariant()}/{entity.Kind} {ProbeLabels.KindName(entity.Kind)}, team {entity.TeamId}, generation {entity.Generation}");
        Emit($"query:   position   {ProbeFormat.Point(SimBridge.ToMetres(entity.Position))}, cell {cell % world.Navigation.Size},{cell / world.Navigation.Size} on {ProbeLabels.Surface(world.TerrainTypes.TypeAt(cell))}");
        Emit($"query:   heading    {ProbeFormat.Angle(SimBridge.HeadingRadians(entity.Heading))}, {entity.Heading} brads of 65536");
        Emit($"query:   health     {entity.Health}/{definition.Health} ({(definition.Health > 0 ? entity.Health * 100 / definition.Health : 0)}%)");
        Emit($"query:   {DescribeArmour(world, ref entity, definition)}");
        Emit($"query:   {DescribeGround(world, cell, ref entity, definition, step)}");
        Emit($"query:   morale     {entity.Morale.ToFloat():0.000}{(entity.Routed ? ", routing" : ", steady")}");
        Emit(
            $"query:   move       {(entity.HasMoveGoal ? $"goal {ProbeFormat.Ground(entity.MoveGoal)}, {ProbeFormat.Metres(entity.Position.HorizontalDistanceTo(entity.MoveGoal) / (float)WorldPos.MmPerMetre)} to go" : "none")}, " +
            $"path {ProbeFormat.Count(entity.PathLength, "cell")} at {entity.PathCursor}{(entity.NeedsPath ? ", waiting for a route" : string.Empty)}, {entity.PathFailures} failures");
        Emit($"query:   attack     {DescribeAttack(world, ref entity, definition)}");
        Emit(
            $"query:   travel     {ProbeFormat.Metres(entity.DistanceTravelledMm / (float)WorldPos.MmPerMetre)} covered at " +
            $"{ProbeFormat.Metres(step * SimConstants.TickRate / (float)WorldPos.MmPerMetre)} per second " +
            $"({ProbeFormat.Metres(step / (float)WorldPos.MmPerMetre)} per tick)" +
            (step == entity.SpeedMmPerTick.ToIntRound()
                ? string.Empty
                : $", against {ProbeFormat.Metres(entity.SpeedMmPerTick.ToFloat() * SimConstants.TickRate / WorldPos.MmPerMetre)} per second " +
                  $"({ProbeFormat.Metres(entity.SpeedMmPerTick.ToFloat() / WorldPos.MmPerMetre)} per tick) on clear ground"));
        Emit($"query:   structure  building {(definition.IsBuilding ? "yes" : "no")}, queued {ProbeFormat.Count(entity.QueueLength, "job")}, construction {DescribeConstruction(ref entity)}");
        Emit($"query:   render     {(visible ? "drawn" : "not drawn (fog or stealth)")}, camera at {ProbeFormat.Metres(Vector3.Distance(camera.Position, SimBridge.ToMetres(entity.Position)))}");
    }

    /// <summary>
    /// The sensor chain for one entity: what it can see, what it can shoot, and how far a
    /// radar is pushing both.
    /// <para>
    /// This is the verb the detection work is read with. Detection and firing range are two
    /// different numbers in this engine and the difference between them is the whole of the
    /// radar, so a report that showed only the weapon's range would show a gun that is always
    /// at full reach — which is exactly the thing that is no longer true.
    /// </para>
    /// </summary>
    private void Range(ProbeCommand command)
    {
        int slot = command.Whole(0, "a slot number", "range <slot>", 0, MaxSlot);
        SimWorld world = _host.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — range <slot>; `units` lists what does");
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(entity.Kind);

        int eyes = VisionSystem.SensorRadiusMm(world, in entity);
        int finds = (eyes * VisionSystem.StealthDetectionPermille) / 1_000;
        int reach = CombatSystem.EngagementRadiusMm(world, slot);
        bool covered = world.Radars.Covers(world, entity.TeamId, entity.Position);

        Emit($"query: range {slot} {ProbeLabels.KindName(entity.Kind)} {ProbeFormat.Ground(entity.Position)}");
        Emit(
            $"query:   gun        {(definition.IsArmed ? ProbeFormat.Millimetres(definition.AttackRangeMm) : "unarmed")}, " +
            $"{definition.AttackDamage} damage every {ProbeFormat.Count(definition.AttackCooldownTicks, "tick")}");
        Emit($"query:   eyes       {ProbeFormat.Millimetres(eyes)} — as far as its own sensors reach");
        Emit($"query:   stealth    {ProbeFormat.Millimetres(finds)} — as far as they find a hidden enemy");
        Emit($"query:   radar      {(covered ? "under coverage" : "not under coverage")}, team {entity.TeamId} has " +
             $"{ProbeFormat.Count(world.Radars.Count(entity.TeamId), "radar")} on the air");
        Emit($"query:   reach      {ProbeFormat.Millimetres(reach)} — the furthest it can engage anything at");

        if (entity.Kind == UnitKind.RadarStation)
        {
            Emit(
                $"query:   coverage   {(world.IsRadarLit(slot) ? ProbeFormat.Millimetres(VisionSystem.RadarCoverageMm) : "none — the grid cannot run it")}");
        }
    }

    /// <summary>
    /// The power ledger for a team: what it generates, what its structures take, and which of
    /// its radars the grid can therefore run.
    /// </summary>
    private void Power(ProbeCommand command)
    {
        int team = (int)command.OptionalNumber(0, 0f, "a team number", "power [team]");

        if ((uint)team >= SimConstants.TeamCount)
        {
            throw new ProbeException($"team {team} is not one of the {SimConstants.TeamCount} — power [team]");
        }

        TeamState state = _host.Simulation.World.Team(team);

        Emit($"query: power team {team}");
        Emit($"query:   generation {state.PowerGeneration} Ε per tick, from the structures standing");
        Emit($"query:   draw       {state.PowerDraw} Ε per tick, including the radars that are on");
        Emit($"query:   surplus    {state.PowerSurplus} Ε per tick");
        Emit($"query:   radars     {state.RadarsLit} lit, {state.RadarsDark} dark, {state.PowerShortfall} Ε short of running them all");
        Emit($"query:   brown-out  {(state.IsDimmed ? PowerSystem.DimmedReason(state) : "none")}");
    }

    /// <summary>
    /// One team's command capacity, as a ledger: what its structures support, what its army costs
    /// against it, and the words the refusal uses when it is over.
    /// <para>
    /// A ledger rather than a number, for the reason <c>power</c> is one: "why can I not build
    /// anything" has three answers — the team has no ceiling, its army is bigger than its ceiling,
    /// or its army is within it and the reason is something else entirely — and only the breakdown
    /// tells them apart. The two totals are asked of <see cref="CapacitySystem"/> itself, the same
    /// functions the production gate and the panel ask, so a transcript cannot report a ceiling the
    /// game does not use; the lines under them are the arithmetic that produced them, per role,
    /// which is what lets a reader check the opening force against the ceiling by hand.
    /// </para>
    /// </summary>
    private void Capacity(ProbeCommand command)
    {
        int team = (int)command.OptionalNumber(0, 0f, "a team number", "capacity [team]");

        if ((uint)team >= SimConstants.TeamCount)
        {
            throw new ProbeException($"team {team} is not one of the {SimConstants.TeamCount} — capacity [team]");
        }

        SimWorld world = _host.Simulation.World;
        Faction faction = world.FactionOfTeam(team);
        int supply = world.ArmySupply(team);
        int ceiling = world.CommandCapacity(team);
        int over = world.OverCapacity(team);

        Span<int> units = stackalloc int[KindSlots];
        Span<int> structures = stackalloc int[KindSlots];

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || (int)entity.Kind >= KindSlots)
            {
                continue;
            }

            if (UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                structures[(int)entity.Kind]++;
            }
            else
            {
                units[(int)entity.Kind]++;
            }
        }

        Emit(
            $"query: capacity team {team} {(faction == Faction.None ? "no faction" : FactionProfile.For(faction).GreekName)} — " +
            $"{supply} places fielded against {ceiling} supported, tick {world.Tick}");
        Emit(
            "query:   rule       a structure grants capacity when it supports an army rather than being one, " +
            "and a man is one place, a vehicle four, an aircraft six — CapacitySystem and UnitCatalog.SupplyCost");

        int structuresStanding = 0;

        for (int kind = 0; kind < KindSlots; kind++)
        {
            if (structures[kind] > 0)
            {
                structuresStanding += structures[kind];
            }
        }

        int permille = faction == Faction.None ? 1_000 : FactionProfile.For(faction).CapacityPermille;

        Emit(
            $"query:   ceiling    {ceiling} places from {ProbeFormat.Count(structuresStanding, "structure")} at " +
            $"{permille}‰ of what they are worth — a building site grants nothing until it is up");

        for (int kind = 0; kind < KindSlots; kind++)
        {
            if (structures[kind] == 0)
            {
                continue;
            }

            int grant = CapacitySystem.GrantOf((UnitKind)kind);

            Emit(
                $"query:      {FactionPalette.UnitLabel((UnitKind)kind),-24} ×{structures[kind],-3} " +
                $"{grant,4} each = {grant * structures[kind],5}{(grant == 0 ? "   — it is the army, not the thing that supports it" : string.Empty)}");
        }

        int live = 0;

        for (int kind = 0; kind < KindSlots; kind++)
        {
            live += units[kind];
        }

        Emit($"query:   supply     {supply} places fielded by {ProbeFormat.Count(live, "unit")}");

        for (int kind = 0; kind < KindSlots; kind++)
        {
            if (units[kind] == 0)
            {
                continue;
            }

            int cost = UnitCatalog.SupplyCost((UnitKind)kind);

            Emit($"query:      {FactionPalette.UnitLabel((UnitKind)kind),-24} ×{units[kind],-3} {cost,4} each = {cost * units[kind],5}");
        }

        Emit(over > 0
            ? $"query:   verdict    over the ceiling by {over} — no unit may be queued until {over} places of army are gone or built for"
            : $"query:   verdict    within the ceiling, {ceiling - supply} places to spare — units may be queued");
        Emit($"query:   refusal    {(over > 0 ? CapacitySystem.OverCapacityReason(over) : "none")}");
    }

    /// <summary>
    /// Puts a role on a building's production pad, down the simulation's own command queue — the
    /// same queue a click writes to — and reports the verdict the player would be given.
    /// <para>
    /// It exists for the reason <c>build</c> on <c>bridge</c> and <c>structure</c> does: a refusal
    /// cannot be inspected without asking for the thing that is refused, and the refusal is the
    /// answer that matters here — a refused order is a sentence the player acts on, and
    /// <c>λείπει δυναμικότητα 174</c> is a different sentence from <c>λείπουν 120 Π</c>. The verdict
    /// and the words both come from <see cref="SimWorld.CanProduce"/>, which is the call the panel
    /// row and the order itself are judged by.
    /// </para>
    /// </summary>
    private void Queue(ProbeCommand command)
    {
        const string Usage = "queue <slot> <role>";

        int slot = command.Whole(0, "the slot of the building that would make it", Usage, 0, MaxSlot);
        UnitKind kind = ParseKind(command.Argument(1, "a role such as Infantry or Tank", Usage));

        SimWorld world = _host.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — {Usage}; `structures` lists what does");
        }

        ref Entity building = ref world.GetRefBySlot(slot);
        var id = new EntityId(slot, building.Generation);
        Faction faction = building.Faction;

        Emit(
            $"query: queue {ProbeLabels.KindName(kind)} at slot {slot} {faction.ToString().ToLowerInvariant()}/{building.Kind}, " +
            $"team {building.TeamId}");

        if (!UnitCatalog.Get(kind).IsBuilding)
        {
            Emit(
                $"query:   supply     {UnitCatalog.SupplyCost(kind)} places for this role; team {building.TeamId} fields " +
                $"{world.ArmySupply(building.TeamId)} against {world.CommandCapacity(building.TeamId)} supported");
        }

        if (!world.CanProduce(id, kind, out string reason))
        {
            Emit($"query:   verdict    refused — {reason}");
            Emit($"query:   player     {FactionPalette.UnitLabel(kind)}: {reason}.");
            return;
        }

        int ticks = UnitCatalog.BuildTicks(faction, kind);

        Emit(
            $"query:   verdict    accepted — {UnitCatalog.MaterialCost(faction, kind)} Π, " +
            $"{UnitCatalog.EnergyCost(faction, kind)} Ε, {UnitCatalog.WaterCost(faction, kind)} Ν, " +
            $"{ProbeFormat.Ticks(ticks)} on the pad");

        world.Enqueue(SimCommand.QueueUnit(id, kind, world.Tick + 1, building.TeamId));

        Emit(
            $"ok: {FactionPalette.UnitLabel(kind)} ordered at slot {slot} for team {building.TeamId}, executing on tick " +
            $"{world.Tick + 1} — `tick 1` starts it and `tick {ticks}` finishes it");
    }

    /// <summary>
    /// Whether one team can see one entity, and which of the three channels says so. The
    /// stealth check is the interesting one: the same entity can be inside a team's sight and
    /// outside its detection, which is what being stealthed means.
    /// </summary>
    private void Detect(ProbeCommand command)
    {
        int team = command.Whole(0, "a team number", "detect <team> <slot>", 0, SimConstants.TeamCount - 1);
        int slot = command.Whole(1, "a slot number", "detect <team> <slot>", 0, MaxSlot);
        SimWorld world = _host.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — detect <team> <slot>; `units` lists what does");
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        int cell = world.Navigation.IndexOfWorld(entity.Position);
        bool hidden = world.IsHiddenFrom(team, slot);
        bool visible = world.Visibility.IsVisible(team, cell);
        bool detected = world.Visibility.IsDetected(team, cell);
        bool friendly = entity.TeamId == team;

        Emit(
            $"query: detect {slot} {entity.Faction.ToString().ToLowerInvariant()}/{ProbeLabels.KindName(entity.Kind)} " +
            $"at {ProbeFormat.Ground(entity.Position)} against team {team}");
        Emit($"query:   hidden     {(hidden ? "yes — no weapon of team " + team + " may engage it" : "no")}");
        Emit($"query:   sight      the cell is {(visible ? "visible" : "not visible")} to team {team}");
        Emit($"query:   detection  the cell is {(detected ? "detected" : "not detected")} by team {team}, which is the channel stealth is hidden from");
        Emit($"query:   revealed   {(entity.RevealedUntilTick > world.Tick ? $"yes, until tick {entity.RevealedUntilTick} — it fired" : "no")}, own team {friendly}");

        if (hidden && entity.RevealedUntilTick == 0)
        {
            Emit($"query:   that means {(visible ? "the ground is watched and the unit is not on it" : "nobody of team " + team + " is looking closely enough")}");
        }
    }

    /// <summary>
    /// What one team knows about a point on the ground: whether it is watched, whether it is
    /// detected, and whether a powered radar of that team covers it. Asked of a place rather
    /// than of a unit, so a boundary can be walked across one query at a time.
    /// </summary>
    private void Exposure(ProbeCommand command)
    {
        int team = command.Whole(0, "a team number", "exposure <team> <x> <z>", 0, SimConstants.TeamCount - 1);
        float x = command.Number(1, "an x in metres", "exposure <team> <x> <z>");
        float z = command.Number(2, "a z in metres", "exposure <team> <x> <z>");
        SimWorld world = _host.Simulation.World;

        var point = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));
        int cell = world.Navigation.IndexOfWorld(point);

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — exposure <team> <x> <z>, and the map is +-300 m");
        }

        bool covered = world.Radars.Covers(world, team, point);

        Emit($"query: exposure ({x:0.#}, {z:0.#}) m against team {team}, cell {cell % world.Navigation.Size},{cell / world.Navigation.Size}");
        Emit($"query:   radar      {(covered ? "inside a powered radar's coverage" : "outside every radar team " + team + " has on the air")}");
        Emit($"query:   sight      the cell is {(world.Visibility.IsVisible(team, cell) ? "visible" : "not visible")} to team {team}");
        Emit($"query:   detection  the cell is {(world.Visibility.IsDetected(team, cell) ? "detected" : "not detected")} by team {team}");
    }

    /// <summary>
    /// <b>What armour does to one hit, per faction, with no battle in it.</b>
    /// <para>
    /// The composition rule in full, asked of a role and a cell: the ground's cover, then each
    /// power's armour — the role's own construction times that power's philosophy — and the damage
    /// a hit of a given size is left with. It exists because the claim this feature makes is a
    /// <em>difference between factions</em>, and a difference read off three health bars is a
    /// measurement of one battle while a difference read off this table is the rule itself.
    /// <c>events</c> is the other half, and the two agreeing is the proof.
    /// </para>
    /// <para>
    /// The arithmetic is asked of <see cref="DamageRules.Compose"/> and the figures of
    /// <see cref="UnitCatalog.ArmourPermille"/> rather than written out again here, so a transcript
    /// cannot report a rule the guns do not follow — which is the failure mode a second copy of the
    /// sums always has, and the reason the whole rule lives in one function.
    /// </para>
    /// </summary>
    private void Armour(ProbeCommand command)
    {
        const string Usage = "armour <kind> <x> <z> [damage]";

        UnitKind kind = ParseKind(command.Argument(0, "a role such as CommandCentre or Tank", Usage));
        float x = command.Number(1, "an x in metres", Usage);
        float z = command.Number(2, "a z in metres", Usage);
        int damage = (int)command.OptionalNumber(3, 45f, "damage per hit", Usage);

        SimWorld world = _host.Simulation.World;
        UnitDefinition definition = UnitCatalog.Get(kind);
        int cell = world.TerrainTypes.IndexOfWorld((int)(x * WorldPos.MmPerMetre), (int)(z * WorldPos.MmPerMetre));

        if (cell < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        int cover = world.TerrainTypes.CoverAt(cell, definition.Movement);
        ArmourClass armourClass = UnitCatalog.ArmourClassOf(kind);

        Emit(
            $"query: armour: a {damage}-damage hit on {ProbeLabels.KindName(kind)} at (x {x:0.#}, z {z:0.#}) m — " +
            $"{DescribeCell(world, cell)}, {armourClass.ToString().ToLowerInvariant()} armour");
        Emit("query:   rule       damage × cover ÷ 1000 × armour ÷ 1000, floored at 1 — DamageRules.Compose, the one place a hit becomes damage");
        Emit(
            $"query:   cover      {ProbeFormat.Permille(cover)} for {definition.Movement.ToString().ToLowerInvariant()} " +
            $"— what the ground lets through, and 1000 is ground that hides nobody");

        foreach (FactionProfile profile in FactionProfile.All)
        {
            Faction faction = profile.Faction;
            int factionArmour = profile.ArmourPermilleFor(armourClass);
            int armour = UnitCatalog.ArmourPermille(faction, kind);
            int hit = DamageRules.Compose(damage, cover, armour);

            Emit(
                $"query:   {Greek(faction),-11}  role {ProbeFormat.Permille(definition.RoleArmourPermille)} × " +
                $"faction {ProbeFormat.Permille(factionArmour)} = {ProbeFormat.Permille(armour)} → " +
                $"{hit} damage, {damage - hit} turned away");
        }
    }

    /// <summary>The faction's Greek name, padded for the column it is printed in.</summary>
    private static string Greek(Faction faction)
        => faction == Faction.None ? "—" : FactionProfile.For(faction).GreekName;

    /// <summary>
    /// <b>Gives one unit an order through the simulation's own command queue.</b>
    /// <para>
    /// A probe cannot place a unit, and until now it could not tell one to go anywhere either — so
    /// every question about a unit <em>moving</em> had to be answered by a fixture that ordered it,
    /// which made the order part of the premise instead of part of the demonstration. This is the
    /// same <see cref="SimCommand"/> a click issues, enqueued for the next tick, so a script that
    /// orders a column across a bog is driving the real thing: the order is validated where an
    /// order is validated, and a refusal is printed rather than assumed.
    /// </para>
    /// <para>
    /// It is the fourth command in this tool that changes the world, after the three that had to:
    /// a crossing cannot be inspected until it is built, a structure cannot be inspected until it
    /// is raised, and a distance cannot be inspected until something is told to cover it.
    /// </para>
    /// </summary>
    private void Order(ProbeCommand command)
    {
        const string Usage = "order <slot> move <x> <z> | order <slot> attack <slot>";

        int slot = command.Whole(0, "a slot number", Usage, 0, MaxSlot);
        string what = command.Argument(1, "'move' or 'attack'", Usage).ToLowerInvariant();
        SimWorld world = _host.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — {Usage}; `units` lists what does");
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        SimCommand issued;

        if (what == "move")
        {
            float x = command.Number(2, "an x in metres", Usage);
            float z = command.Number(3, "a z in metres", Usage);
            var goal = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));

            if (world.TerrainTypes.IndexOfWorld(goal.X, goal.Z) < 0)
            {
                throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
            }

            issued = SimCommand.Move(new EntityId(slot, entity.Generation), goal, world.Tick + 1, entity.TeamId);

            Emit($"query: order {slot} move to (x {x:0.#}, z {z:0.#}) m — {DescribeCell(world, world.TerrainTypes.IndexOfWorld(goal.X, goal.Z))}");
        }
        else if (what == "attack")
        {
            int victim = command.Whole(2, "a slot number", Usage, 0, MaxSlot);

            if (!world.IsAliveSlot(victim))
            {
                throw new ProbeException($"slot {victim} holds nothing alive to attack — {Usage}");
            }

            issued = SimCommand.Attack(new EntityId(slot, entity.Generation), new EntityId(victim, world.GetRefBySlot(victim).Generation), world.Tick + 1, entity.TeamId);

            // The world's own verdict rather than a second opinion here: an order that will be
            // refused is answered in the words the player is shown, so a transcript cannot report
            // an order standing that the simulation throws away. Same call the click makes.
            bool stands = world.CanAttack(
                new EntityId(slot, entity.Generation),
                new EntityId(victim, world.GetRefBySlot(victim).Generation),
                out string refusal);

            Emit($"query: order {slot} attack {victim} — {ProbeLabels.KindName(world.GetRefBySlot(victim).Kind)}, " +
                 (stands ? "hostile, so the order stands" : $"refused, so the order is thrown away rather than obeyed: {refusal}"));
        }
        else
        {
            throw new ProbeException($"'{what}' is not an order a probe can give — usage: {Usage}");
        }

        world.Enqueue(issued);

        Emit(
            $"ok: {ProbeLabels.KindName(entity.Kind)} at slot {slot} ordered to {what}, executing on tick {world.Tick + 1} — " +
            $"`tick 1` issues it and `unit {slot}` reads what came of it");
    }

    /// <summary>
    /// <b>Calls in an off-map ability at a point, through the world's own ability path.</b>
    /// <para>
    /// The signature verb of a faction that has one, and until now the only way to see an ability
    /// work was to build a world with its prerequisites already met and watch. That is fine for a
    /// nuke and useless for weather control, whose whole point is a surface the <em>player</em>
    /// chooses — the mud is laid where the enemy has to cross, so a script has to be able to say
    /// where that is.
    /// </para>
    /// <para>
    /// The verdict is the world's, not this command's: <see cref="SimWorld.CanUseAbility"/> is
    /// asked first and its refusal — in the words the player is shown — is printed, so a script
    /// that tries to call down a strike it has not researched reads the same sentence a player
    /// would.
    /// </para>
    /// </summary>
    private void Ability(ProbeCommand command)
    {
        const string Usage = "ability <name> <x> <z> [team]";

        string name = command.Argument(0, "an ability such as WeatherControl", Usage);

        if (!Enum.TryParse(name, ignoreCase: true, out AbilityId id) || !AbilityCatalog.TryGet(id, out AbilityDefinition definition))
        {
            throw new ProbeException($"'{name}' is not an ability the catalogue knows — {Usage}");
        }

        float x = command.Number(1, "an x in metres", Usage);
        float z = command.Number(2, "a z in metres", Usage);
        int team = (int)command.OptionalNumber(3, 0f, "a team number", Usage);

        if ((uint)team >= SimConstants.TeamCount)
        {
            throw new ProbeException($"team {team} is not one of the {SimConstants.TeamCount} the simulation has — {Usage}");
        }

        SimWorld world = _host.Simulation.World;
        var target = new WorldPos((int)(x * WorldPos.MmPerMetre), 0, (int)(z * WorldPos.MmPerMetre));

        if (world.TerrainTypes.IndexOfWorld(target.X, target.Z) < 0)
        {
            throw new ProbeException($"({x}, {z}) m is off the map — {Usage}, and the map is +-300 m");
        }

        Emit($"query: ability {definition.GreekName} at (x {x:0.#}, z {z:0.#}) m for team {team}");
        Emit($"query:   costs      {definition.MaterialCost} Π, ready again {ProbeFormat.Ticks(definition.CooldownTicks)} after it lands");
        Emit($"query:   arrives    {definition.Damage} damage inside {ProbeFormat.Millimetres(definition.RadiusMm)}" +
             (definition.WeatherDurationTicks > 0 ? $", and {ProbeFormat.Ticks(definition.WeatherDurationTicks)} of mud" : string.Empty));
        if (!world.CanUseAbility(team, id, out string reason))
        {
            Emit($"query:   verdict    refused — {reason}");

            // Not an error: a refusal is an answer, and the reason is the sentence the player
            // would be shown. A script that only ever calls abilities it has researched will
            // never read this line, and one that does has found out why.
            RecordCheck($"ability {id} available to team {team}", false, $"refused — {reason}");
            return;
        }

        world.Enqueue(SimCommand.UseAbility(id, target, world.Tick + 1, team));

        Emit($"ok: {definition.GreekName} called down at (x {x:0.#}, z {z:0.#}) m for team {team}, executing on tick {world.Tick + 1}");
    }

    /// <summary>
    /// One entity's armour, written the way the rule reads: the class it belongs to, the role's own
    /// figure, its owner's, and the product — which is the share of every hit that lands on it.
    /// </summary>
    private static string DescribeArmour(SimWorld world, ref Entity entity, UnitDefinition definition)
    {
        ArmourClass armourClass = UnitCatalog.ArmourClassOf(entity.Kind);

        if (armourClass == ArmourClass.None)
        {
            return "armour     none — a man on foot wears no plate, and the ground he stands on is his protection";
        }

        int faction = entity.Faction == Faction.None ? 1_000 : FactionProfile.For(entity.Faction).ArmourPermilleFor(armourClass);
        int armour = UnitCatalog.ArmourPermille(entity.Faction, entity.Kind);
        int cell = world.TerrainTypes.IndexOfWorld(entity.Position.X, entity.Position.Z);
        int example = DamageRules.Compose(45, world.TerrainTypes.CoverAt(cell, definition.Movement), armour);

        return
            $"armour     {armourClass.ToString().ToLowerInvariant()} — role {ProbeFormat.Permille(definition.RoleArmourPermille)} × " +
            $"faction {ProbeFormat.Permille(faction)} = {ProbeFormat.Permille(armour)}, so a 45-damage hit here lands as {example}";
    }

    /// <summary>
    /// What the ground under one unit is doing to it, which is the other half of why a column is
    /// slow. It is the answer to "the tanks are crawling and nothing is shooting at them": the
    /// surface, the pressure this school's hull puts on it, the cost those two make together, and
    /// the fraction of its own speed the unit is left with — all read from the same calls the
    /// movement system makes, so a transcript cannot report a cost the wheels do not pay.
    /// </summary>
    private static string DescribeGround(SimWorld world, int cell, ref Entity entity, UnitDefinition definition, int step)
    {
        if (definition.IsBuilding)
        {
            return "ground     does not move, so the ground it stands on costs it nothing";
        }

        if (cell < 0)
        {
            return "ground     off the map, so no terrain cost applies";
        }

        PathContext context = world.PathContextOf(entity.TeamId, entity.Faction, entity.Kind);

        if (context.Movement == MovementClass.Air)
        {
            return "ground     flies over it: 1000 ‰ of its speed on every surface";
        }

        int cost = world.TerrainTypes.CostPermille(cell, context.Movement, context.GroundPressurePermille);
        int permille = world.TerrainTypes.SpeedPermilleAt(cell, context.Movement, context.GroundPressurePermille);
        int nominal = Math.Max(0, entity.SpeedMmPerTick.ToIntRound());

        return
            $"ground     {ProbeLabels.Surface(world.TerrainTypes.TypeAt(cell))}, " +
            $"{context.Movement.ToString().ToLowerInvariant()} at {ProbeFormat.Permille(context.GroundPressurePermille)} pressure — " +
            $"costs {ProbeFormat.Permille(cost)} of the baseline, so {step} of its {nominal} mm per tick " +
            $"({ProbeFormat.Permille(permille)} of its speed)";
    }

    private static string DescribeAttack(SimWorld world, ref Entity entity, UnitDefinition definition)    {
        if (!definition.IsArmed)
        {
            return "unarmed";
        }

        string target = entity.TargetSlot < 0 ? "none" : $"slot {entity.TargetSlot}";

        if (entity.TargetSlot >= 0 && world.IsAliveSlot(entity.TargetSlot))
        {
            ref Entity victim = ref world.GetRefBySlot(entity.TargetSlot);
            target += $" ({victim.Faction.ToString().ToLowerInvariant()}/{victim.Kind} at {ProbeFormat.Metres(entity.Position.HorizontalDistanceTo(victim.Position) / (float)WorldPos.MmPerMetre)})";
        }

        return
            $"{target}, cooldown {ProbeFormat.Count(entity.AttackCooldown, "tick")} of {definition.AttackCooldownTicks}, " +
            $"order {(entity.HasAttackOrder ? "explicit" : "automatic")}, {definition.AttackDamage} damage out to {ProbeFormat.Millimetres(definition.AttackRangeMm)}";
    }

    private static string DescribeConstruction(ref Entity entity)
        => entity.ConstructionTicksTotal > 0
            ? $"{entity.ConstructionTicksRemaining} of {entity.ConstructionTicksTotal} ticks left"
            : "complete";

    /// <summary>How many of a role are alive, with the per-faction breakdown that says whether that is reasonable.</summary>
    private void Count(ProbeCommand command)
    {
        string text = command.Argument(0, "a unit role", "count <kind>");

        if (!ProbeLabels.TryKind(text, out UnitKind kind))
        {
            throw new ProbeException($"'{text}' is not a unit role — count <kind>, as in `count Tank`; `surfaces` counts terrain instead");
        }

        SimWorld world = _host.Simulation.World;
        var perFaction = new List<string>();
        int total = 0;

        foreach (Faction faction in new[] { Faction.Soviet, Faction.Chinese, Faction.Western })
        {
            int count = 0;

            for (int slot = 0; slot < world.Capacity; slot++)
            {
                if (world.IsAliveSlot(slot))
                {
                    ref Entity entity = ref world.GetRefBySlot(slot);

                    if (entity.Kind == kind && entity.Faction == faction)
                    {
                        count++;
                    }
                }
            }

            total += count;
            perFaction.Add($"{faction.ToString().ToLowerInvariant()} {count}");
        }

        Emit($"query: count {kind}: {ProbeFormat.Count(total, "alive", "alive")} ({string.Join(", ", perFaction)})");
    }

    // ---------------------------------------------------------------- render state

    /// <summary>
    /// Every part of one entity, as the renderer animates it.
    /// <para>
    /// This is the query that answers "what is this part doing": a part's declared
    /// transform, the transform the animator produced, the axis and angle of the rotation
    /// between them, the per-tick rotation where the script has asked twice with a tick in
    /// between, and the transform the part is finally drawn with. None of it is computed
    /// here: the animated transform comes from the renderer's own <c>AnimatePart</c> and the
    /// world transform from the same composition the submission loop uses, so an answer
    /// that disagrees with the picture is a bug in one of them rather than in the report.
    /// </para>
    /// </summary>
    private void Parts(ProbeCommand command)
    {
        int slot = command.Whole(0, "a slot number", "parts <slot> [name]", 0, MaxSlot);
        string? filter = command.Optional(1);

        if (!_host.TryDescribeParts(slot, out ProbeEntityParts described))
        {
            throw new ProbeException($"slot {slot} holds nothing alive — parts <slot> [name]; `units` lists what does");
        }

        long tick = _host.Simulation.World.Tick;
        int shown = 0;

        Emit($"query: parts slot {slot} {described.Faction.ToString().ToLowerInvariant()}/{described.Kind} team {described.TeamId}, tick {tick}");

        if (ProbeModels.TryFind(described.Faction, described.Kind, out string relative, out _))
        {
            float wheel = _host.Catalog.WheelRadius(described.Faction, described.Kind);

            // Which of the two ways a model can have no radius it is, because they are two
            // different facts and one of them used to be a bug: a headquarters genuinely has
            // no `wheel_` part, while every tank in the game reported `none` for years
            // because the radius was measured against a matrix's yawed M11.
            bool hasWheel = described.Parts.Any(part => part.Name.StartsWith("wheel_", StringComparison.Ordinal));
            string wheelText = wheel > 0f
                ? ProbeFormat.Metres(wheel)
                : hasWheel
                    ? "none — this model declares wheel_ parts that are drawn still"
                    : "none — this model has no wheel_ part";

            Emit(
                $"query:   model    {relative}, scale {ProbeFormat.Scale(described.ModelTransform)}, " +
                $"{ProbeFormat.Count(described.Parts.Length, "part")}, " +
                $"wheel radius {wheelText}" +
                $"{(filter is null ? string.Empty : $", filtered to '{filter}'")}");
        }

        Vector3 hull = HullForward(described.EntityTransform);

        Emit($"query:   entity   at {ProbeFormat.Point(described.Position)}, heading {ProbeFormat.Degrees(described.HeadingRadians)}, which the entity transform turns into {ProbeFormat.Vector(hull)} bearing {ProbeFormat.Bearing(hull)}");

        if (described.TurretYawRadians is { } turret)
        {
            // Three numbers for one rotation, because they are three different frames and a
            // reader who only saw one of them would think the others disagreed: where the
            // turret's own part points in the world, how far that is off the hull, and the
            // angle the animator asked for inside the model — which is the same rotation,
            // written in a frame the model's own alignment has turned.
            Vector3 aim = described.Parts
                .Where(part => part.Name.Equals("turret", StringComparison.Ordinal))
                .Select(part => Nose(part.World))
                .FirstOrDefault(hull);

            Emit(
                $"query:   turret   the turret part points {ProbeFormat.SignedDegrees(OffHull(hull, aim))} off the hull " +
                $"(world bearing {ProbeFormat.Bearing(aim)}, hull {ProbeFormat.Bearing(hull)}); " +
                $"the animator's own TurretYaw is {ProbeFormat.Angle(turret)} in the model's frame");
        }

        // One line saying how to read the three transforms below, because a transcript is
        // read without the source: `local` is what the model declares, `motion` is what the
        // animator did to it this tick, `world` is the chain composed, and a matrix is its
        // translation followed by the model's own X, Y and Z basis vectors.
        Emit("query:   note     local = as the model declares it, motion = what the animator applied, world = the chain composed; nose is the image of the model's own -Z, which is the front every generated model is authored with");

        foreach (ProbePart part in described.Parts)
        {
            if (filter is not null && !part.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string motion = Motion(slot, part, tick);
            bool moved = !motion.StartsWith("none", StringComparison.Ordinal);
            Vector3 nose = Nose(part.World);

            // One line per part. A model has tens of parts and a transcript is read whole:
            // three lines per part would bury the answer to "which part turns" under a
            // hundred lines that are all identical because most parts never move. The part's
            // own bounds are printed only for the parts that moved, because that is where
            // they are the joint a rotation is measured from rather than trivia.
            Emit(
                $"query:   [{part.Index,2}] '{part.Name}' parent {(part.ParentIndex < 0 ? "-" : part.ParentIndex.ToString(CultureInfo.InvariantCulture))}  " +
                $"local {ProbeFormat.Transform(part.Declared)}  " +
                $"motion {motion}  " +
                (moved ? $"bounds max {ProbeFormat.Point(part.BoundsMax)}  " : string.Empty) +
                $"world {ProbeFormat.Transform(part.World)}  nose {ProbeFormat.Vector(nose)} bearing {ProbeFormat.Bearing(nose)}");

            shown++;
        }

        Emit($"query:   {ProbeFormat.Count(shown, "part")} listed of {described.Parts.Length}");
    }

    /// <summary>
    /// What the animator did to a part, as an axis and an angle, plus how fast it is turning
    /// where the script has sampled it twice at two different ticks.
    /// <para>
    /// The axis is read back out of the matrix the animator produced rather than from a copy
    /// of the rules, so a part that has started turning about the wrong axis is reported as
    /// turning about the wrong axis. A rotation about Z where the contract says Y is exactly
    /// the radar bug this is built to catch, and it is invisible in a still frame.
    /// </para>
    /// </summary>
    private string Motion(int slot, ProbePart part, long tick)
    {
        var text = new StringBuilder();
        Matrix animation = Animation(part);

        if (!TryRotation(animation, out Vector3 axis, out float angle) || MathF.Abs(angle) < 1e-4f)
        {
            text.Append("none (drawn exactly as the model declares it)");
        }
        else
        {
            text.Append($"rotates about {ProbeFormat.Vector(axis)} by {ProbeFormat.SignedDegrees(angle)}");

            // A limb swings about the top of its own mesh rather than about the origin, which
            // shows up as a translation left over beside the rotation. Reporting only the
            // rotation would describe a swinging leg as a leg rotating in place.
            Vector3 shift = animation.Translation;

            if (shift.Length() > 0.005f)
            {
                text.Append($", moving it {ProbeFormat.Shift(shift)} from where it was declared");
            }
        }

        var key = (slot, part.Index);
        bool sampled = _poses.TryGetValue(key, out ProbePose previous);

        if (sampled && previous.Tick != tick)
        {
            long elapsed = tick - previous.Tick;
            float determinant = previous.Transform.Determinant();

            Matrix delta = MathF.Abs(determinant) > 1e-9f
                ? part.Animated * Matrix.Invert(previous.Transform)
                : Matrix.Identity;

            if (TryRotation(delta, out Vector3 deltaAxis, out float deltaAngle) && MathF.Abs(deltaAngle) > 1e-5f)
            {
                // The rotation per tick is the number that answers "is this turning, about
                // what, and how fast" without the reader dividing anything.
                text.Append(
                    $"; turned {ProbeFormat.SignedDegrees(deltaAngle)} about {ProbeFormat.Vector(deltaAxis)} over " +
                    $"{ProbeFormat.Count((int)elapsed, "tick")} = {ProbeFormat.SignedDegrees(deltaAngle / elapsed)}/tick");
            }
            else
            {
                text.Append($"; did not turn over the last {ProbeFormat.Count((int)elapsed, "tick")}");
            }
        }
        else if (sampled)
        {
            text.Append("; ask again after a tick to see the rotation per tick");
        }

        _poses[key] = new ProbePose(tick, part.Animated);
        return text.ToString();
    }

    /// <summary>The rotation the animator applied, as <c>animated * inverse(declared)</c>.</summary>
    private static Matrix Animation(ProbePart part)
    {
        Matrix declared = part.Declared;

        if (declared == part.Animated)
        {
            return Matrix.Identity;
        }

        float determinant = declared.Determinant();

        if (MathF.Abs(determinant) < 1e-9f)
        {
            return Matrix.Identity;
        }

        return part.Animated * Matrix.Invert(declared);
    }

    /// <summary>
    /// The rotation a matrix represents, as an axis whose dominant component is positive
    /// and a signed angle about it.
    /// <para>
    /// Written from the antisymmetric part rather than through a quaternion because a
    /// quaternion cannot say "a quarter turn about -Z" and "three quarters about +Z" apart,
    /// and this report is read by something looking for the axis.
    /// </para>
    /// </summary>
    private static bool TryRotation(Matrix matrix, out Vector3 axis, out float angle)
    {
        axis = Vector3.UnitY;
        angle = 0f;

        var skew = new Vector3(
            matrix.M32 - matrix.M23,
            matrix.M13 - matrix.M31,
            matrix.M21 - matrix.M12);

        float sin = skew.Length() * 0.5f;
        float cos = Math.Clamp(((matrix.M11 + matrix.M22 + matrix.M33) - 1f) * 0.5f, -1f, 1f);

        if (sin < 1e-6f)
        {
            if (cos > 0f)
            {
                return false;
            }

            // A half turn: the antisymmetric part vanishes, so the axis comes from the
            // diagonal instead.
            axis = Vector3.Normalize(new Vector3(
                MathF.Sqrt(MathF.Max(0f, (matrix.M11 + 1f) * 0.5f)),
                MathF.Sqrt(MathF.Max(0f, (matrix.M22 + 1f) * 0.5f)),
                MathF.Sqrt(MathF.Max(0f, (matrix.M33 + 1f) * 0.5f))));

            if (matrix.M12 + matrix.M21 < 0f)
            {
                axis.X = -axis.X;
            }

            angle = MathF.PI;
            return true;
        }

        axis = skew / (2f * sin);
        angle = MathF.Atan2(sin, cos);

        // One rotation can be written two ways round; the axis with the positive dominant
        // component is the one to print, so "about Y" does not alternate between samples.
        float dominant = MathF.Abs(axis.X) >= MathF.Abs(axis.Y) && MathF.Abs(axis.X) >= MathF.Abs(axis.Z)
            ? axis.X
            : MathF.Abs(axis.Y) >= MathF.Abs(axis.Z) ? axis.Y : axis.Z;

        if (dominant < 0f)
        {
            axis = -axis;
            angle = -angle;
        }

        return true;
    }

    /// <summary>
    /// How far one direction is off another, in radians, wrapped to a half turn either way:
    /// "the gun is 34 degrees off the hull" is a smaller number than a bearing difference of
    /// 326 degrees and says the same thing.
    /// </summary>
    private static float OffHull(Vector3 reference, Vector3 direction)
        => MathHelper.WrapAngle(MathF.Atan2(direction.Z, direction.X) - MathF.Atan2(reference.Z, reference.X));

    /// <summary>
    /// Which way an entity transform points a hull: the image of +X, which is the direction
    /// the simulation calls forward and the direction a heading of zero degrees faces.
    /// </summary>
    private static Vector3 HullForward(Matrix entityTransform)
    {
        var forward = new Vector3(entityTransform.M11, entityTransform.M12, entityTransform.M13);

        return forward.LengthSquared() > 1e-9f ? Vector3.Normalize(forward) : Vector3.UnitX;
    }

    /// <summary>
    /// Which way a transform points a part's nose.
    /// <para>
    /// The nose is the image of the model's own <b>-Z</b>, not of its +X. Every generator in
    /// <c>tools/blender</c> authors a model front-first along Blender's +Y, the glTF exporter
    /// writes that as -Z, and the loader's alignment or the catalogue's yaw offset is what
    /// turns that front onto the +X the simulation calls forward — a rotation that ends up in
    /// the model transform. So the model's +X is a sideways axis, and printing it as the
    /// facing would report every correctly oriented tank as facing ninety degrees off.
    /// </para>
    /// </summary>
    private static Vector3 Nose(Matrix matrix)
    {
        // The image of -Z, which is the third row of the basis with its sign flipped.
        var nose = new Vector3(-matrix.M31, -matrix.M32, -matrix.M33);

        return nose.LengthSquared() > 1e-9f ? Vector3.Normalize(nose) : Vector3.UnitX;
    }

    /// <summary>
    /// The model entry a role resolves to: which file, how it is fitted into the world, and
    /// who else is fitted from the same file.
    /// </summary>
    private void Model(ProbeCommand command)
    {
        string spec = command.Argument(0, "a role as Faction/Kind", "model <faction>/<kind>");

        string[] halves = spec.Split('/');

        if (halves.Length != 2 || !ProbeLabels.TryFaction(halves[0], out Faction faction) || !ProbeLabels.TryKind(halves[1], out UnitKind kind))
        {
            throw new ProbeException($"'{spec}' is not a role — model <faction>/<kind>, as in `model Soviet/Tank`");
        }

        Emit($"query: model {faction.ToString().ToLowerInvariant()}/{kind}");

        if (!ProbeModels.TryFind(faction, kind, out string relative, out Rendering.Gltf.ModelImportOptions options))
        {
            Emit($"query:   no model configured for {faction}/{kind} — the procedural box is what is drawn");
            return;
        }

        string path = ProbeModels.FullPath(relative);
        ModelCatalog.ModelParts parts = _host.Catalog.GetParts(faction, kind);

        Emit($"query:   file     {relative} ({(File.Exists(path) ? $"on disk, {ProbeFormat.Bytes(new FileInfo(path).Length)}" : "MISSING — the procedural fallback is drawn")})");
        Emit(
            $"query:   import   target size {ProbeFormat.Metres(options.TargetSizeMetres)}, yaw offset {options.YawOffsetDegrees:0.0}°, " +
            $"flip normals {(options.FlipNormals ? "yes" : "no")}, align longest axis {(options.AlignLongestHorizontalAxis ? "yes" : "no")}, " +
            $"mesh filter {options.MeshNameContains ?? "none"}");
        Emit(
            $"query:   fitted   scale {ProbeFormat.Scale(parts.ModelTransform)}, offset " +
            $"{ProbeFormat.Point(new Vector3(parts.ModelTransform.M41, parts.ModelTransform.M42, parts.ModelTransform.M43))}, " +
            $"wheel radius {(parts.WheelRadiusMetres > 0f ? ProbeFormat.Metres(parts.WheelRadiusMetres) : "none")}");
        Emit($"query:   declares {parts.Parts.Length} parts: {string.Join(", ", parts.Parts.Select(part => part.Name))}");

        List<(Faction Faction, UnitKind Kind)> sharers = ProbeModels.Sharers(faction, kind, relative);

        if (sharers.Count > 0)
        {
            string names = string.Join(
                ", ",
                sharers.Select(sharer => $"{sharer.Faction.ToString().ToLowerInvariant()}/{sharer.Kind}"));

            Emit($"query:   shared   {names} import this same file");
        }
    }

    /// <summary>
    /// What the frame draws, and what it covers.
    /// <para>
    /// The first line is counted by rendering a frame and reading the counters the renderer
    /// just incremented, so it cannot drift from what is submitted. The last two lines are
    /// what turns "the lava is not visible" from a guess into a number: the ground under the
    /// camera is censused by surface, and the surfaces that are <em>not</em> there are named.
    /// </para>
    /// </summary>
    private void Visible(ProbeCommand command)
    {
        ProbeFrameStats stats = _host.RenderFrame();
        ProbeCamera camera = _host.ReadCamera();
        SimWorld world = _host.Simulation.World;

        Emit(
            $"query: visible: {ProbeFormat.Count(stats.DrawCalls, "draw call")}, {ProbeFormat.Count(stats.InstancesSubmitted, "instance")}, " +
            $"{ProbeFormat.Count(stats.Particles, "particle")}, {ProbeFormat.Count(stats.Rounds, "round")} in flight, {ProbeFormat.Count(stats.HealthBars, "health bar")}");

        // The instance count is what was submitted, not what survived clipping: this client
        // culls nothing on the processor and lets the GPU discard what is off screen, so the
        // number below is a cost and the frustum count underneath it is the answer to "what
        // is on screen".
        Emit("query:   note     instances are submitted to the GPU, not clipped by the client; the ground and entity counts below are what is in the frustum");

        Emit(
            $"query:   camera   {camera.Width}x{camera.Height} px at {ProbeFormat.Point(camera.Position)} looking at {ProbeFormat.Point(camera.Target)}, " +
            $"{ProbeFormat.Metres(camera.Distance)} out, pitch {ProbeFormat.Degrees(camera.Pitch)}, yaw {ProbeFormat.Degrees(camera.Yaw)}");

        GroundCoverage(camera, out float minX, out float maxX, out float minZ, out float maxZ, out bool sky);

        Emit(
            $"query:   covers   x {minX:0.0} .. {maxX:0.0} m, z {minZ:0.0} .. {maxZ:0.0} m" +
            $"{((maxX - minX) * (maxZ - minZ) > 0f ? $", about {(maxX - minX) * (maxZ - minZ):0} m² in a box around it" : string.Empty)}" +
            $"{(sky ? ", and a corner of the view is pointing at the sky" : string.Empty)}");

        Emit($"query:   entities {CountInFrustum(world, camera)} of {world.AliveCount} alive project inside the viewport (this counts through fog)");
        Emit($"query:   ground   {SurfaceCensus(world, camera, minX, maxX, minZ, maxZ, present: true)}");
        Emit($"query:   absent   {SurfaceCensus(world, camera, minX, maxX, minZ, maxZ, present: false)}");
    }

    /// <summary>Where the four corners of the view meet the ground, as an axis-aligned box around them.</summary>
    private static void GroundCoverage(
        ProbeCamera camera,
        out float minX,
        out float maxX,
        out float minZ,
        out float maxZ,
        out bool sky)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        minZ = float.MaxValue;
        maxZ = float.MinValue;
        sky = false;

        Matrix inverse = Matrix.Invert(camera.View * camera.Projection);

        (float X, float Y)[] corners =
        [
            (0f, 0f),
            (camera.Width, 0f),
            (camera.Width, camera.Height),
            (0f, camera.Height),
        ];

        foreach ((float px, float py) in corners)
        {
            // Screen pixel to a ray: the inverse projection is how the camera's own
            // ScreenToGround does it, and the intersection is with the flat ground plane,
            // which is what "roughly which area" means for an RTS camera.
            float ndcX = ((px / camera.Width) * 2f) - 1f;
            float ndcY = 1f - ((py / camera.Height) * 2f);

            Vector4 near = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
            Vector4 far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);

            near /= near.W;
            far /= far.W;

            float dy = far.Y - near.Y;

            if (MathF.Abs(dy) < 1e-6f)
            {
                sky = true;
                continue;
            }

            float t = -near.Y / dy;

            if (t < 0f || t > 1f)
            {
                sky = true;
                continue;
            }

            float x = near.X + ((far.X - near.X) * t);
            float z = near.Z + ((far.Z - near.Z) * t);

            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }

        if (minX > maxX || minZ > maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
        }
    }

    /// <summary>How many living entities are inside the camera's frustum.</summary>
    private static int CountInFrustum(SimWorld world, ProbeCamera camera)
    {
        Matrix viewProjection = camera.View * camera.Projection;
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            if (InFrustum(SimBridge.ToMetres(world.GetRefBySlot(slot).Position), viewProjection))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when a world point projects inside the viewport and in front of the camera.
    /// <para>
    /// The W component has to be checked before the divide: a point behind the camera divides
    /// by a negative W and lands back inside the viewport, mirrored, which would count the
    /// battlefield behind the player as being on screen.
    /// </para>
    /// </summary>
    private static bool InFrustum(Vector3 world, Matrix viewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);

        return clip.W > 0f &&
            MathF.Abs(clip.X / clip.W) <= 1f &&
            MathF.Abs(clip.Y / clip.W) <= 1f;
    }

    /// <summary>
    /// The surfaces of the ground the camera can actually see, either the ones that are there
    /// or the ones that are not.
    /// <para>
    /// Tested cell by cell against the frustum rather than counted over the bounding box of
    /// the view: a camera at 45° covers a diamond, and a box around a diamond is twice the
    /// ground it is looking at. The box is where the loop starts, and the projection decides
    /// what is in it.
    /// </para>
    /// </summary>
    private static string SurfaceCensus(SimWorld world, ProbeCamera camera, float minX, float maxX, float minZ, float maxZ, bool present)
    {
        TerrainLayer terrain = world.TerrainTypes;
        Matrix viewProjection = camera.View * camera.Projection;
        var counts = new Dictionary<TerrainType, int>();

        foreach (TerrainType type in Enum.GetValues<TerrainType>())
        {
            counts[type] = 0;
        }

        // Clamped to the map, because a camera at the edge of the world sees past it.
        int minCellX = Math.Clamp((int)MathF.Floor((minX * WorldPos.MmPerMetre - terrain.OriginMm) / terrain.CellSizeMm), 0, terrain.Size - 1);
        int maxCellX = Math.Clamp((int)MathF.Ceiling((maxX * WorldPos.MmPerMetre - terrain.OriginMm) / terrain.CellSizeMm), 0, terrain.Size - 1);
        int minCellZ = Math.Clamp((int)MathF.Floor((minZ * WorldPos.MmPerMetre - terrain.OriginMm) / terrain.CellSizeMm), 0, terrain.Size - 1);
        int maxCellZ = Math.Clamp((int)MathF.Ceiling((maxZ * WorldPos.MmPerMetre - terrain.OriginMm) / terrain.CellSizeMm), 0, terrain.Size - 1);

        for (int z = minCellZ; z <= maxCellZ; z++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                int index = (z * terrain.Size) + x;
                var centre = new Vector3(
                    (terrain.OriginMm + (x * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
                    world.Navigation.HeightAt(index) / (float)WorldPos.MmPerMetre,
                    (terrain.OriginMm + (z * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

                if (!InFrustum(centre, viewProjection))
                {
                    continue;
                }

                counts[terrain.TypeAtCell(x, z)]++;
            }
        }

        var parts = new List<string>();

        foreach ((TerrainType type, int count) in counts)
        {
            if (present ? count > 0 : count == 0)
            {
                parts.Add(present
                    ? $"{type.ToString().ToLowerInvariant()} {count}"
                    : type.ToString().ToLowerInvariant());
            }
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// The simulation events the client has seen, most recent last.
    /// <para>
    /// Firing is not re-detected here. The events come from the bridge's own cooldown
    /// diffing — a cooldown that was zero and is now positive with a target is a shot — so
    /// the probe and the tracer on screen are reporting the same thing, and the direction
    /// this prints is the line from the shooter to the slot it was aiming at.
    /// </para>
    /// </summary>
    private void Events(ProbeCommand command)
    {
        int wanted = (int)command.OptionalNumber(0, 10, "a count", "events [n]");

        if (wanted < 1 || wanted > EventHistory)
        {
            throw new ProbeException($"events takes a count in 1..{EventHistory}, and {wanted} is outside it — usage: events [n]");
        }

        Emit($"query: events: showing the last {Math.Min(wanted, _events.Count)} of {_events.Count} seen since the script started, tick {_host.Simulation.World.Tick}");

        int from = Math.Max(0, _events.Count - wanted);

        for (int i = from; i < _events.Count; i++)
        {
            ProbeEvent history = _events[i];

            Emit($"query:   #{i + 1} tick {history.Tick} {history.Describe()}");
        }
    }

    /// <summary>
    /// <b>Who is on whose side, and what has actually passed between them.</b>
    /// <para>
    /// The ally question is the world's own — <see cref="SimWorld.AreAllied"/>, the same predicate
    /// a weapon, a salvo, an attack order, a bridge and an off-map strike all ask — so this line
    /// cannot disagree with what the guns did. Everything under it is the evidence rather than the
    /// rule: shots by ordered pair of teams, and the health and units each team has lost, counted
    /// from the same event stream <c>events</c> prints.
    /// </para>
    /// <para>
    /// Two numbers are the ones to read. A shot by a team at a team it is not at war with is a
    /// weapon pointed at a friend. A damage event or a loss that no hostile weapon fired for, on
    /// that same tick, is the other door: a scattered salvo catches an ally without ever naming
    /// one, and it is the reason a blast has to be asked about separately from a target. Lava has
    /// no owner and burns whoever stands in it, so a hit taken on a lava cell is counted as
    /// terrain.
    /// </para>
    /// </summary>
    private void Teams(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;
        int capacity = world.Capacity;

        var alive = new int[SimConstants.TeamCount];
        var structures = new int[SimConstants.TeamCount];

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if ((uint)entity.TeamId >= SimConstants.TeamCount)
            {
                continue;
            }

            alive[entity.TeamId]++;

            if (UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                structures[entity.TeamId]++;
            }
        }

        int inPlay = 0;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (alive[team] > 0 || structures[team] > 0)
            {
                inPlay++;
            }
        }

        Emit($"query: teams: {inPlay} teams in play of {SimConstants.TeamCount} slots at tick {world.Tick}");
        Emit($"query:   match      {DescribeMatch(world)} — MatchRoster, which is what the victory check and the AI read");

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (alive[team] == 0)
            {
                continue;
            }

            Emit(
                $"query:   team {team} {world.FactionOfTeam(team).ToString().ToLowerInvariant(),-8} " +
                $"{ProbeFormat.Count(alive[team], "alive", "alive")}, " +
                $"{ProbeFormat.Count(structures[team], "structure")}");
        }

        var allied = new List<string>();

        for (int a = 0; a < SimConstants.TeamCount; a++)
        {
            for (int b = a + 1; b < SimConstants.TeamCount; b++)
            {
                if (alive[a] == 0 || alive[b] == 0)
                {
                    continue;
                }

                allied.Add($"{a}+{b} {(world.AreAllied(a, b) ? "allied" : "hostile")}");
            }
        }

        Emit($"query:   sides      {string.Join(", ", allied)} — SimWorld.AreAllied, which is what every weapon asks");

        // The verdict, from the rule itself rather than from the banner: the outcome is simulation
        // state, so it is the same answer a replay reaches, and a match that has ended says so here.
        Emit($"query:   outcome    {DescribeOutcome(world)}");

        var shots = new List<string>();

        for (int shooter = 0; shooter < SimConstants.TeamCount; shooter++)
        {
            for (int target = 0; target < SimConstants.TeamCount; target++)
            {
                if (shooter == target)
                {
                    continue;
                }

                int count = _shotsBetweenTeams[(shooter * SimConstants.TeamCount) + target];

                // The allied pairs are printed whether or not they are zero, because the zero is
                // the reading; a hostile pair that has never fired at anything is just noise.
                if (count == 0 && world.IsHostile(shooter, target))
                {
                    continue;
                }

                shots.Add($"{shooter} at {target}: {count}");
            }
        }

        Emit(
            $"query:   shots      {(shots.Count == 0 ? "none" : string.Join(", ", shots))} — since the script " +
            "started, read from the world's own cooldowns so that a shot which killed its target is in the count");

        var damage = new List<string>();

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (alive[team] == 0 && _hitsOnTeam[team] == 0 && _lossesOnTeam[team] == 0)
            {
                continue;
            }

            damage.Add(
                $"team {team} {ProbeFormat.Count(_hitsOnTeam[team], "hit")}, " +
                $"{ProbeFormat.Count(_lossesOnTeam[team], "loss", "losses")}, {_damageOnTeam[team]} health");
        }

        Emit($"query:   damage     {string.Join("; ", damage)} — the health lost by the team that lost it");

        Emit(_shotsAtAllies == 0 && _alliedTargets == 0 && _unexplainedDamage == 0
            ? "query:   friendly   none — no weapon held an ally as a target, none fired at one, and no damage went unexplained"
            : $"query:   friendly   {ProbeFormat.Count(_alliedTargets, "tick")} with an ally held as a target, " +
              $"{ProbeFormat.Count(_shotsAtAllies, "shot")} fired at an ally, " +
              $"{ProbeFormat.Count(_unexplainedDamage, "damage event")} unexplained");

        // The three readings above are not measurements to look at, they are a rule being broken:
        // an engine in which a weapon can point at a friend has a bug, and a probe that reads one
        // and exits 0 is a probe that hides it. So they are recorded as a check rather than printed
        // for a reader to compare — which `expect` cannot do, because it takes the numbers written
        // in the script and these are read from the world.
        RecordCheck(
            "no weapon aimed, fired or damaged an ally",
            _shotsAtAllies == 0 && _alliedTargets == 0 && _unexplainedDamage == 0,
            $"{_alliedTargets} ticks with an ally held as a target, {_shotsAtAllies} shots at an ally, " +
            $"{_unexplainedDamage} damage events unexplained");

        // The demonstration rather than the ledger, and the one question a total cannot answer:
        // an armed unit with an ally of the other team inside its own weapon reach is a unit that
        // could have shot a friend — and what it is aimed at *instead* is the answer. Asked of the
        // world as it stands, now, rather than counted over the run.
        int inReach = 0;
        int nearer = 0;
        int aimingAtAlly = 0;
        int aimingAtEnemy = 0;
        int aimingAtNothing = 0;
        var examples = new List<string>();
        var idleExamples = new List<string>();

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            UnitDefinition weapon = UnitCatalog.Get(entity.Kind);

            if (!weapon.IsArmed || entity.Routed || (weapon.IsBuilding && !world.IsComplete(slot)))
            {
                continue;
            }

            int allyDistance = NearestAllyInReachMm(world, slot, ref entity, CombatSystem.EngagementRadiusMm(world, slot));

            if (allyDistance < 0)
            {
                continue;
            }

            inReach++;

            string aimedAt = "nothing";
            int targetDistance = -1;

            if (entity.TargetSlot >= 0)
            {
                ref Entity aim = ref world.GetRefBySlot(entity.TargetSlot);
                targetDistance = (int)entity.Position.HorizontalDistanceTo(aim.Position);

                aimedAt = $"{aim.Faction.ToString().ToLowerInvariant()}/{aim.Kind} (slot {entity.TargetSlot}, team {aim.TeamId}) " +
                    $"at {ProbeFormat.Millimetres(targetDistance)}";

                if (world.IsHostile(entity.TeamId, aim.TeamId))
                {
                    aimingAtEnemy++;
                }
                else
                {
                    aimingAtAlly++;
                }
            }
            else
            {
                aimingAtNothing++;
            }

            // The comparison that is the demonstration rather than the ledger: an ally *nearer*
            // than the enemy this weapon is firing at is a target that distance alone would have
            // picked the ally for, and the number of those is the answer to "did it choose the
            // enemy over the friend standing in front of it".
            string example =
                $"slot {slot} team {entity.TeamId} with an ally at {ProbeFormat.Millimetres(allyDistance)} aiming at {aimedAt}";

            if (targetDistance >= 0 && allyDistance < targetDistance)
            {
                nearer++;
            }

            // A unit that is shooting at somebody is the better example to quote; a unit with
            // nothing to shoot at only shows that there was nothing to shoot at.
            if (targetDistance < 0)
            {
                if (idleExamples.Count < 3)
                {
                    idleExamples.Add(example);
                }
            }
            else if (examples.Count < 3)
            {
                examples.Add(example);
            }
        }

        List<string> shown = examples.Count > 0 ? examples : idleExamples;

        Emit(
            $"query:   in reach   {ProbeFormat.Count(inReach, "armed unit")} have a unit of an allied team inside the " +
            $"reach they can engage at: {aimingAtAlly} aimed at an ally, " +
            $"{aimingAtEnemy} at an enemy, {aimingAtNothing} at nothing, and {nearer} had the ally nearer than the " +
            $"target they were firing at" +
            (shown.Count == 0 ? string.Empty : $" — e.g. {string.Join("; ", shown)}"));
    }

    /// <summary>
    /// What the match declares, in the terms the victory check and the AI read it: how many teams
    /// are playing, which faction each one plays, and which side each one is on. Teams the match
    /// does not declare are named too, because "the Δυτικοί are not in this match" is the fact a
    /// two-faction match turns on and an omission would leave it to the reader to infer.
    /// </summary>
    private static string DescribeMatch(SimWorld world)
    {
        MatchRoster roster = world.Roster;
        var playing = new List<string>();
        var absent = new List<string>();

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (roster.IsInPlay(team))
            {
                playing.Add($"{team} {roster.FactionOf(team).ToString().ToLowerInvariant()} side {roster.SideOf(team)}");
            }
            else
            {
                absent.Add($"team {team} ({roster.FactionOf(team).ToString().ToLowerInvariant()})");
            }
        }

        return $"{ProbeFormat.Count(roster.TeamsInPlay, "team")} declared: {string.Join(", ", playing)}" +
            $"; not in the match: {string.Join(", ", absent)}";
    }

    /// <summary>
    /// The verdict, and which declared teams it was reached from: the outcome is simulation state,
    /// so this is the same answer a replay arrives at, and the sides still holding structures are
    /// the reading rather than a summary of it. A match that has ended says so here.
    /// </summary>
    private static string DescribeOutcome(SimWorld world)
    {
        string verdict = world.Outcome switch
        {
            GameOutcome.Victory => "victory — the player's side is the last one holding structures",
            GameOutcome.Defeat => "defeat — the player's side holds no structures",
            GameOutcome.Draw => "draw — no side holds any",
            _ => "ongoing",
        };

        MatchRoster roster = world.Roster;
        var standing = new List<string>();

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (roster.IsInPlay(team) && VictorySystem.HasStructures(world, team))
            {
                standing.Add($"team {team}");
            }
        }

        return standing.Count == 0
            ? $"{verdict}; no declared team holds a structure"
            : $"{verdict}; still holding structures: {string.Join(", ", standing)}";
    }

    /// <summary>
    /// The distance to the nearest unit of the other allied team that stands inside
    /// <paramref name="reachMm"/>, or -1 when there is none: the encounter that makes "it did not
    /// shoot" mean something.
    /// <para>
    /// The reach passed in is <see cref="CombatSystem.EngagementRadiusMm"/> — the engine's own
    /// answer, which is the weapon's range capped by the shooter's eyes unless a powered radar
    /// covers both ends of the shot. Using the weapon's bare range instead was this line's first
    /// version and it lied: an Artillery reaches 220 m with its gun and 160 m with its eyes, so a
    /// formation 200 m away read as "in reach" when no weapon in the game could have touched it.
    /// </para>
    /// </summary>
    private static int NearestAllyInReachMm(SimWorld world, int slot, ref Entity shooter, int reachMm)
    {
        if (reachMm <= 0)
        {
            return -1;
        }

        SpatialIndex index = world.Spatial;
        int nearest = -1;
        int minX = index.CoordinateOf(shooter.Position.X - reachMm);
        int maxX = index.CoordinateOf(shooter.Position.X + reachMm);
        int minZ = index.CoordinateOf(shooter.Position.Z - reachMm);
        int maxZ = index.CoordinateOf(shooter.Position.Z + reachMm);

        for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                foreach (int other in index.Cell(index.IndexOf(cellX, cellZ)))
                {
                    if (other == slot || !world.IsAliveSlot(other))
                    {
                        continue;
                    }

                    ref Entity candidate = ref world.GetRefBySlot(other);

                    // An ally, and not a brother on the same team: the encounter that can turn
                    // into friendly fire is between two teams, not inside one. Which two is the
                    // match's answer — this used to be "a unit of team 0 or 1 looking at the
                    // other", which is a description of the standard skirmish rather than of an
                    // alliance, and in a match with no ally at all it would have gone looking for
                    // a friend that does not exist.
                    if (candidate.TeamId == shooter.TeamId || !world.AreAllied(shooter.TeamId, candidate.TeamId))
                    {
                        continue;
                    }

                    int dx = shooter.Position.X - candidate.Position.X;
                    int dz = shooter.Position.Z - candidate.Position.Z;
                    long distanceSquared = ((long)dx * dx) + ((long)dz * dz);

                    if (distanceSquared > (long)reachMm * reachMm)
                    {
                        continue;
                    }

                    int distance = (int)shooter.Position.HorizontalDistanceTo(candidate.Position);

                    if (nearest < 0 || distance < nearest)
                    {
                        nearest = distance;
                    }
                }
            }
        }

        return nearest;
    }

    /// <summary>
    /// Copies whatever the simulation has reported since the last look into the probe's own
    /// history, and accounts for it.
    /// <para>
    /// The bridge's queue is deliberately left alone rather than drained: the client turns
    /// its events into effects once a frame, and a frame is what <c>settle</c> asks for. A
    /// probe that ate them would leave <c>settle</c> with nothing to draw and no way for a
    /// script to photograph the shot it just asked about.
    /// </para>
    /// <para>
    /// <b>This runs on every tick, including the ones that reported nothing</b>, because the
    /// ledger watches the world's cooldowns as well as its events: a tick skipped as uneventful
    /// is a tick whose cooldowns were never read, and the shot two ticks later then looks like a
    /// cooldown that was already running rather than one that had just been set — which reads as
    /// damage nobody fired for. That mistake was made here once and the tally said 39.
    /// </para>
    /// </summary>
    private void HarvestEvents(long tick)
    {
        IReadOnlyList<SimEvent> events = _host.Simulation.Events;

        // The queue only grows between settles, so anything below the cursor is old news;
        // a shorter queue means the client drained it and the cursor starts again.
        if (events.Count < _eventsSeen)
        {
            _eventsSeen = 0;
        }

        SimWorld world = _host.Simulation.World;

        // This tick's events are collected before any of them is accounted for, because whether
        // a hit is explained by hostile fire is a question about the whole tick: the events
        // arrive in slot order, so the shot that caused a hit on slot 3 may come from slot 500.
        _tickEvents.Clear();

        for (int i = _eventsSeen; i < events.Count; i++)
        {
            ProbeEvent probeEvent = ProbeEvent.From(events[i], tick, world);

            _tickEvents.Add(probeEvent);
            _events.Add(probeEvent);

            if (_events.Count > EventHistory)
            {
                _events.RemoveAt(0);
            }
        }

        _eventsSeen = events.Count;

        AccountForTick(world);
    }

    /// <summary>
    /// Keeps the running tally of who fired at whom and who was hurt, which is what <c>teams</c>
    /// reads back. Run once per tick, from the world as it stands at the end of it.
    /// <para>
    /// Three things are counted, and they are the three doors damage can come through. A weapon
    /// <em>aimed</em> at a team it is not at war with is the bug itself, and it is checked on every
    /// armed unit on every tick rather than when it happens to fire, because a target is held across
    /// ticks and the one that matters is the one held. A <em>shot</em> at an ally is the same thing
    /// arriving: a held ally killed outright clears the target in the same tick, so the pair is read
    /// from where the weapon was aimed a tick earlier — the one approximation here, and it can only
    /// ever invent a pair that was real a tick ago, never an allied one, because an armed unit never
    /// holds an ally for the approximation to find. And a <em>damage event, or a loss, in a tick
    /// where no team hostile to the victim fired</em> is the door artillery comes through: a
    /// scattered salvo catches an ally without ever naming one, so no check on targets can see it.
    /// </para>
    /// <para>
    /// Lava is the exception and it is asked about rather than assumed: it has no owner and burns
    /// whoever stands in it, so a unit hurt on a lava cell is counted as terrain damage and not as a
    /// shot nobody fired.
    /// </para>
    /// </summary>
    private void AccountForTick(SimWorld world)
    {
        int capacity = world.Capacity;

        if (_previousCooldown.Length < capacity)
        {
            _previousCooldown = new int[capacity];
            _previousTarget = new int[capacity];

            for (int slot = 0; slot < capacity; slot++)
            {
                _previousTarget[slot] = -1;
            }
        }

        _teamsThatFired.Clear();

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                // A dead slot's cooldown is stale data waiting to be reused by the next unit
                // spawned into it, which would otherwise read as that unit firing on arrival.
                _previousCooldown[slot] = 0;
                _previousTarget[slot] = -1;
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition weapon = UnitCatalog.Get(entity.Kind);

            if (!weapon.IsArmed || entity.Routed || (weapon.IsBuilding && !world.IsComplete(slot)))
            {
                _previousCooldown[slot] = entity.AttackCooldown;
                _previousTarget[slot] = entity.TargetSlot;
                continue;
            }

            bool fired = _previousCooldown[slot] == 0 && entity.AttackCooldown > 0;
            int aim = entity.TargetSlot >= 0 ? entity.TargetSlot : (fired ? _previousTarget[slot] : -1);

            if (fired)
            {
                if (!_teamsThatFired.Contains(entity.TeamId))
                {
                    _teamsThatFired.Add(entity.TeamId);
                }

                if ((uint)aim < (uint)capacity)
                {
                    ref Entity target = ref world.GetRefBySlot(aim);
                    int aimTeam = target.TeamId;

                    if ((uint)entity.TeamId < SimConstants.TeamCount && (uint)aimTeam < SimConstants.TeamCount)
                    {
                        _shotsBetweenTeams[(entity.TeamId * SimConstants.TeamCount) + aimTeam]++;

                        if (!world.IsHostile(entity.TeamId, aimTeam))
                        {
                            _shotsAtAllies++;
                        }
                    }
                }
            }

            if (entity.TargetSlot >= 0 && (uint)entity.TeamId < SimConstants.TeamCount)
            {
                int heldTeam = world.GetRefBySlot(entity.TargetSlot).TeamId;

                if (!world.IsHostile(entity.TeamId, heldTeam))
                {
                    _alliedTargets++;
                }
            }

            _previousCooldown[slot] = entity.AttackCooldown;
            _previousTarget[slot] = entity.TargetSlot;
        }

        foreach (ProbeEvent probeEvent in _tickEvents)
        {
            if (probeEvent.Type == SimEventType.ShotFired ||
                (uint)probeEvent.TeamId >= SimConstants.TeamCount)
            {
                continue;
            }

            int victim = probeEvent.TeamId;

            if (probeEvent.Type == SimEventType.UnitHit)
            {
                _hitsOnTeam[victim]++;
                _damageOnTeam[victim] += probeEvent.Damage;
            }
            else
            {
                _lossesOnTeam[victim]++;
            }

            if (!IsTerrainDamage(world, probeEvent.PositionMm) && !ExplainedByHostileFire(victim))
            {
                _unexplainedDamage++;
            }
        }
    }

    /// <summary>True when some team that fired this tick is at war with the victim's.</summary>
    private bool ExplainedByHostileFire(int victimTeam)
    {
        foreach (int team in _teamsThatFired)
        {
            if (_host.Simulation.World.IsHostile(team, victimTeam))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the event happened on lava, which has no owner and burns anybody.</summary>
    private static bool IsTerrainDamage(SimWorld world, WorldPos position)
    {
        int cell = world.Navigation.IndexOfWorld(position);
        return cell >= 0 && world.TerrainTypes.TypeAt(cell) == TerrainType.Lava;
    }

    // ---------------------------------------------------------------- the mission's script

    /// <summary>
    /// <b>The mission's triggers, in the order the simulation evaluates them, with what each one
    /// waits for, what it does, and whether it has fired — and on which tick.</b>
    /// <para>
    /// A script is the one thing in a mission that cannot be read off the world it produced: after
    /// the fact, a spawned force looks like a force and a revealed ridge looks like ground somebody
    /// walked over. So the question "did the ambush happen, and when" has to be asked of the layer
    /// that decided it, and this is that question. The tick matters as much as the yes: a trigger
    /// that fired forty seconds late is a mission whose second act is in the wrong place, and only
    /// the tick can say so.
    /// </para>
    /// <para>
    /// It also records the mission's own integrity check — <see cref="TriggerSystem.Validate"/>
    /// — as a check rather than as a line of prose, so that a mission whose triggers <em>cannot</em>
    /// fire fails the run. That is the failure this project keeps meeting from the other side:
    /// a feature that exists and never happens, which a transcript can read for forty lines
    /// without noticing. The check answers the mirror question too, since the same validator asks
    /// every condition of the world the mission <em>opens</em> in and reports the ones already
    /// true of it: a script that fires on the first tick by accident fails the run for the same
    /// reason and with the same sentence naming the trigger.
    /// </para>
    /// </summary>
    private void Triggers(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;
        MissionDefinition? mission = world.Mission;

        if (mission is null)
        {
            Emit("query: triggers: this match has no mission attached — start one with --mission <id>");
            return;
        }

        ReadOnlySpan<TriggerState> states = world.TriggerStates;
        int fired = 0;

        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].HasFired)
            {
                fired++;
            }
        }

        Emit(
            $"query: triggers: {ProbeFormat.Count(mission.Triggers.Count, "trigger")} in '{mission.Id}', " +
            $"{fired} fired, at tick {world.Tick}");

        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            TriggerDefinition trigger = mission.Triggers[i];
            TriggerState state = i < states.Length ? states[i] : default;

            string status = state.HasFired
                ? $"FIRED on tick {state.FiredTick} ({ProbeFormat.Seconds(state.FiredTick / (double)SimConstants.TickRate)} in)"
                : "waiting";

            Emit($"query:   #{i} {trigger.Id,-16} {status}");
            Emit($"query:       when       {DescribeCondition(trigger.Condition)}");

            for (int a = 0; a < trigger.Actions.Count; a++)
            {
                Emit($"query:       then       {DescribeAction(trigger.Actions[a])}");
            }

            if (trigger.Note.Length > 0)
            {
                Emit($"query:       why        {trigger.Note}");
            }

            // The author's own declaration, printed where the script is rather than left to be
            // inferred from the check below: a trigger whose condition may already hold before the
            // first trigger has run says so, and the validation asks the same question of the
            // world the mission opens in.
            if (trigger.DependsOnOpeningWorld)
            {
                Emit("query:       opening    the author declares this condition a fact about the world the mission opens in");
            }
        }

        ReadOnlySpan<uint> flags = world.MissionFlags;

        if (flags.Length > 0)
        {
            var raised = new List<string>();

            for (int i = 0; i < flags.Length; i++)
            {
                raised.Add(flags[i] != 0 ? $"flag {i} SET" : $"flag {i} clear");
            }

            Emit($"query:   flags      {string.Join(", ", raised)}");
        }

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        RecordCheck(
            "the mission's script fires when it means to",
            problems.Count == 0,
            problems.Count == 0
                ? "every trigger waits on something that can happen, every scripted objective is completed by one, " +
                  "and no condition is already true of the world the mission opens in"
                : string.Join("; ", problems));
    }

    /// <summary>
    /// What the mission has said to the player, oldest first, with the tick and how long ago.
    /// The interface shows the fresh ones; this shows the ledger, which is what makes "the
    /// message action works" a fact about a running match rather than a reading of the code.
    /// </summary>
    private void Messages(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;
        IReadOnlyList<MissionMessage> messages = world.MissionMessages;

        Emit(
            $"query: messages: {ProbeFormat.Count(messages.Count, "line")} shown by the mission, " +
            $"at tick {world.Tick}");

        for (int i = 0; i < messages.Count; i++)
        {
            MissionMessage message = messages[i];
            double ago = (world.Tick - message.Tick) / (double)SimConstants.TickRate;

            Emit($"query:   #{i} tick {message.Tick} ({ProbeFormat.Seconds(ago)} ago) — {message.GreekText}");
        }

        if (messages.Count == 0)
        {
            Emit("query:   the mission has said nothing yet");
        }
    }

    /// <summary>
    /// The objectives the mission is judged by: kind, status, and the numbers behind it — the
    /// same state <see cref="StateHash"/> folds in, so a transcript of it and a replay agree by
    /// construction.
    /// </summary>
    private void Objectives(ProbeCommand command)
    {
        SimWorld world = _host.Simulation.World;
        MissionDefinition? mission = world.Mission;

        if (mission is null)
        {
            Emit("query: objectives: this match has no mission attached — start one with --mission <id>");
            return;
        }

        ReadOnlySpan<ObjectiveState> states = world.Objectives;

        Emit(
            $"query: objectives: {ProbeFormat.Count(mission.Objectives.Count, "objective")} in '{mission.Id}', " +
            $"outcome {world.Outcome.ToString().ToLowerInvariant()}, at tick {world.Tick}");

        for (int i = 0; i < mission.Objectives.Count && i < states.Length; i++)
        {
            ObjectiveDefinition definition = mission.Objectives[i];
            ObjectiveState state = states[i];

            Emit(
                $"query:   #{i} {definition.Kind,-20} {state.Status.ToString().ToLowerInvariant(),-8} " +
                $"{(definition.IsPrimary ? "primary" : "bonus")}{(definition.Constraint ? ", constraint" : string.Empty)} " +
                $"progress {state.Progress}, hold {state.HoldProgress}");

            Emit($"query:       asks       {definition.GreekDescription}");
            Emit($"query:       numbers    {DescribeObjective(definition)}");
        }
    }

    /// <summary>An objective's own parameters, so a status can be checked against what it was asking.</summary>
    private static string DescribeObjective(ObjectiveDefinition definition)
    {
        var parts = new List<string>
        {
            $"team {definition.Team}",
        };

        switch (definition.Kind)
        {
            case ObjectiveKind.DestroyStructures:
                parts.Add($"team {definition.TargetTeam} loses {definition.TargetCount}");
                break;

            case ObjectiveKind.HoldArea:
                parts.Add($"{definition.TargetCount} of team {definition.Team} in {Circle(definition.CentreX, definition.CentreZ, definition.RadiusMm)} for {ProbeFormat.Ticks(definition.HoldTicks)}");
                break;

            case ObjectiveKind.DenyArea:
                parts.Add($"team {definition.TargetTeam} must not get {definition.TargetCount} units into {Circle(definition.CentreX, definition.CentreZ, definition.RadiusMm)}");
                break;

            case ObjectiveKind.AccumulateMaterials:
                parts.Add($"{definition.MaterialsTarget} materials");
                break;

            case ObjectiveKind.ReachTechTier:
                parts.Add($"tier {definition.TierTarget}");
                break;
        }

        if (definition.DeadlineTick > 0)
        {
            parts.Add($"by tick {definition.DeadlineTick} ({ProbeFormat.Seconds(definition.DeadlineTick / (double)SimConstants.TickRate)} in)");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A trigger condition as a sentence, so the transcript reads as the mission's script. The two
    /// counting conditions are the pair whose names carry their tense, and the sentences say it out
    /// loud: "has lost" is a number of losses, "stand" is what is on the map now.
    /// </summary>
    private static string DescribeCondition(TriggerCondition condition) => condition.Kind switch
    {
        TriggerConditionKind.TimeElapsed =>
            $"tick {condition.Tick} is reached ({ProbeFormat.Seconds(condition.Tick / (double)SimConstants.TickRate)} in)",

        TriggerConditionKind.UnitInArea =>
            $"{ProbeFormat.Count(condition.Count, "unit")} of team {condition.Team} inside {Circle(condition.CentreX, condition.CentreZ, condition.RadiusMm)}",

        TriggerConditionKind.StructuresLost =>
            $"team {condition.Team} has lost {ProbeFormat.Count(condition.Count, "structure")}",

        TriggerConditionKind.StructuresStandingBelow =>
            $"fewer than {condition.Count} {DescribeRole(condition.Role)} of team {condition.Team} stand",

        TriggerConditionKind.FlagSet => $"flag {condition.Flag} is set",

        _ => condition.Kind.ToString(),
    };

    /// <summary>A trigger action as a sentence, with the numbers a reader would need to check it.</summary>
    private static string DescribeAction(TriggerAction action) => action.Kind switch
    {
        TriggerActionKind.Message => $"tell the player \"{action.GreekText}\"",

        TriggerActionKind.SetFlag => $"raise flag {action.Flag}",

        TriggerActionKind.Spawn =>
            $"spawn {action.Count} of {DescribeRole(action.Role)} for team {action.Team} at {Point(action.CentreX, action.CentreZ)}",

        TriggerActionKind.Reveal =>
            $"reveal {ProbeFormat.Metres(action.RadiusMm / (float)WorldPos.MmPerMetre)} around {Point(action.CentreX, action.CentreZ)} " +
            $"for team {action.Team}{(action.Ticks > 0 ? $" for {ProbeFormat.Ticks(action.Ticks)}" : " for the rest of the mission")}",

        TriggerActionKind.AdjustResources =>
            $"team {action.Team}: {Amounts(action)}",

        TriggerActionKind.OrderGroup =>
            $"the units of team {action.Team} inside {ProbeFormat.Metres(action.RadiusMm / (float)WorldPos.MmPerMetre)} of " +
            $"{Point(action.CentreX, action.CentreZ)} are ordered to " +
            (action.Order == GroupOrder.Attack
                ? $"attack the nearest enemy to {Point(action.TargetX, action.TargetZ)}"
                : $"move to {Point(action.TargetX, action.TargetZ)}"),

        TriggerActionKind.CompleteObjective => $"complete objective {action.Objective}",

        _ => action.Kind.ToString(),
    };

    private static string Amounts(in TriggerAction action)
    {
        var parts = new List<string>();

        if (action.Materials != 0)
        {
            parts.Add($"{action.Materials:+#;-#;0} Π");
        }

        if (action.Energy != 0)
        {
            parts.Add($"{action.Energy:+#;-#;0} Ε");
        }

        if (action.Water != 0)
        {
            parts.Add($"{action.Water:+#;-#;0} Ν");
        }

        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>A role, named and labelled the way the build panel labels it.</summary>
    private static string DescribeRole(UnitKind role)
        => role == UnitKind.None ? "structure" : ProbeLabels.KindName(role);

    /// <summary>A circle on the ground, as a centre and a radius.</summary>
    private static string Circle(int x, int z, int radiusMm)
        => $"{Point(x, z)} within {ProbeFormat.Metres(radiusMm / (float)WorldPos.MmPerMetre)}";

    private static string Point(int x, int z)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"(x {x / (float)WorldPos.MmPerMetre:0.0}, z {z / (float)WorldPos.MmPerMetre:0.0}) m");

    // ---------------------------------------------------------------- checks

    /// <summary>
    /// Records a check and reports whether it held.
    /// <para>
    /// This is what turns a script from something that prints into something that asserts:
    /// a number typed into the script is a claim about the world, and a claim that stops
    /// being true is a failed run rather than a line in a transcript that nobody compared
    /// against the last one. Two numbers compare numerically, anything else as text, and an
    /// optional tolerance covers the answers that are themselves rounded.
    /// </para>
    /// </summary>
    private void Expect(ProbeCommand command)
    {
        string label = command.Argument(0, "a label", "expect <label> <actual> <expected> [tolerance]");
        string actual = command.Argument(1, "an actual value", "expect <label> <actual> <expected> [tolerance]");
        string expected = command.Argument(2, "an expected value", "expect <label> <actual> <expected> [tolerance]");
        float? tolerance = command.ArgumentCount > 3
            ? command.Number(3, "a tolerance", "expect <label> <actual> <expected> [tolerance]")
            : null;

        _checks++;

        if (Matches(actual, expected, tolerance))
        {
            Emit($"check: PASS '{label}' — {actual}{(tolerance is { } t ? $" is within {t:0.###} of" : " ==")} {expected}");
            return;
        }

        _checksFailed++;
        Emit($"fail: FAIL '{label}' — got {actual}, expected {expected}{(tolerance is { } slack ? $" +- {slack:0.###}" : string.Empty)}");
    }

    /// <summary>
    /// Records a check whose numbers come from the world rather than from the script.
    /// <para>
    /// <c>expect</c> compares two literals the script carries, which is what makes a script a
    /// record of numbers somebody measured — and what makes it useless for an invariant. "No
    /// weapon may aim at an ally" is not a value to compare against a transcript: it is a rule
    /// that either held in this run or did not, and a probe that printed a violation and exited 0
    /// would be hiding the thing it exists to find. These checks go through the same counter and
    /// the same two prefixes as <c>expect</c>, so the run's exit status covers them.
    /// </para>
    /// </summary>
    private void RecordCheck(string label, bool held, string detail)
    {
        _checks++;

        if (held)
        {
            Emit($"check: PASS '{label}' — {detail}");
            return;
        }

        _checksFailed++;
        Emit($"fail: FAIL '{label}' — {detail}");
    }

    private static bool Matches(string actual, string expected, float? tolerance)
    {
        if (float.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out float left) &&
            float.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out float right))
        {
            return tolerance is { } slack ? MathF.Abs(left - right) <= slack : left.Equals(right);
        }

        return tolerance is null && string.Equals(actual, expected, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static int ParseLimit(string text, string usage)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) && limit > 0
            ? limit
            : throw new ProbeException($"'{text}' is not a limit — usage: {usage}");

    /// <summary>A pose of one part at one tick, so the next sample can say how fast it turned.</summary>
    private readonly record struct ProbePose(long Tick, Matrix Transform);

    /// <summary>One simulation event, with everything the probe needs to describe it.</summary>
    private readonly record struct ProbeEvent(
        long Tick,
        SimEventType Type,
        int Slot,
        Faction Faction,
        int TeamId,
        UnitKind Kind,
        Vector3 Position,
        WorldPos PositionMm,
        int Damage,
        int TargetSlot,
        int TargetTeamId,
        string Target,
        Vector3 Direction,
        bool HasDirection,
        float RangeMetres)
    {
        /// <summary>Reads an event, resolving the shot's direction while the target's position is still on hand.</summary>
        public static ProbeEvent From(SimEvent simEvent, long tick, SimWorld world)
        {
            var direction = Vector3.Zero;
            var range = 0f;
            var target = "-";
            int targetTeam = -1;
            bool hasDirection = false;

            if (simEvent.TargetSlot >= 0 && simEvent.TargetSlot < world.Capacity)
            {
                // The target's position whether or not it is still alive: a shot that killed
                // what it was aimed at is the one whose direction matters most, and the slot
                // still holds the last place that unit stood.
                ref Entity victim = ref world.GetRefBySlot(simEvent.TargetSlot);
                Vector3 point = SimBridge.ToMetres(victim.Position);

                targetTeam = victim.TeamId;
                target = $"{victim.Faction.ToString().ToLowerInvariant()}/{victim.Kind} (slot {simEvent.TargetSlot}, team {victim.TeamId}{(victim.Alive ? string.Empty : ", destroyed by this")})";

                Vector3 delta = point - simEvent.Position;
                range = new Vector2(delta.X, delta.Z).Length();

                if (delta.LengthSquared() > 1e-6f)
                {
                    direction = Vector3.Normalize(delta);
                    hasDirection = true;
                }
            }

            return new ProbeEvent(
                tick,
                simEvent.Type,
                simEvent.Slot,
                simEvent.Faction,
                simEvent.TeamId,
                simEvent.Kind,
                simEvent.Position,
                simEvent.PositionMm,
                simEvent.Damage,
                simEvent.TargetSlot,
                targetTeam,
                target,
                direction,
                hasDirection,
                range);
        }

        /// <summary>One line: what happened, where, and for a shot which way it went.</summary>
        /// <remarks>
        /// Every line names the team as well as the faction, because the question a reader of
        /// this stream is asking is almost always a question about sides — "did team 0 shoot at
        /// team 1" — and the pair of names is what answers it. See <c>teams</c>, which asks the
        /// world itself and counts the same events.
        /// </remarks>
        public string Describe()
        {
            string what = Type switch
            {
                SimEventType.ShotFired => "shot",
                SimEventType.UnitHit => "hit",
                _ => "destroyed",
            };

            var text = new StringBuilder();

            text.Append($"{what,-9} slot {Slot,4} {Faction.ToString().ToLowerInvariant()}/{Kind} team {TeamId} at {ProbeFormat.Point(Position)}");

            if (Type == SimEventType.ShotFired)
            {
                text.Append($" firing at {Target}");

                if (HasDirection)
                {
                    text.Append(
                        $", direction {ProbeFormat.Vector(Direction)} bearing {ProbeFormat.Bearing(Direction)}, " +
                        $"{ProbeFormat.Metres(RangeMetres)} away");
                }
            }
            else if (Type == SimEventType.UnitHit)
            {
                text.Append($" took {ProbeFormat.Count(Damage, "damage", "damage")}");
            }

            return text.ToString();
        }
    }
}
