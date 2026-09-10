using System.Globalization;
using System.Text;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using MiVic.Game.Data;
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

    /// <summary>Length of one presentation frame, in seconds. 60 Hz, so effects age as they would on screen.</summary>
    private const float SettleSeconds = 1f / 60f;

    /// <summary>Presentation events kept for the <c>events</c> query.</summary>
    private const int EventHistory = 256;

    private readonly IProbeHost _host;
    private readonly StringBuilder _report = new();
    private readonly List<ProbeEvent> _events = [];
    private readonly Dictionary<(int Slot, int Part), ProbePose> _poses = [];
    private readonly ProbeCommand[] _commands;
    private readonly string _outputPath;

    private int _next;
    private int _eventsSeen;
    private int _ok;
    private int _errors;
    private int _checks;
    private int _checksFailed;
    private bool _finished;

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
            case "bridge":
                Bridge(command);
                break;
            case "bridges":
                Bridges(command);
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
            case "expect":
                Expect(command);
                break;
            default:
                throw new ProbeException(
                    $"unknown command '{command.Verb}' — tick, settle, shot, focus, zoom, pitch, yaw, " +
                    "surfaces, attributes, units, unit, count, parts, model, visible, events, bridge, " +
                    "bridges, arm, hover, click, hud, expect");
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

        TeamState state = world.Team(team);
        Emit(
            $"query:   cost       {SimWorld.BridgeMaterials} Π, {SimWorld.BridgeEnergy} Ε, {SimWorld.BridgeWater} Ν; " +
            $"team {team} has {state.Materials} Π, {state.Energy} Ε, {state.Water} Ν");

        if (!build)
        {
            return;
        }

        long executeTick = world.Tick + 1;
        world.Enqueue(SimCommand.Bridge(target, executeTick, team));
        Emit($"ok: bridge ordered for team {team}, executing on tick {executeTick} — `tick {(int)(executeTick - world.Tick)}` starts the work");
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

            string progress = state.Complete
                ? "whole"
                : $"{state.Built}/{state.Total} up from ({first % size},{first / size}), " +
                  $"{ProbeFormat.Permille(state.ProgressPermille)}, {ProbeFormat.Ticks(state.RemainingTicks(world.Tick))} of work left";

            Emit(
                $"query:   #{bridge} {DescribeSpan(world, cells, cells.Length)} — {progress}, " +
                $"started on tick {state.StartTick}, whole on tick {state.ReadyTick}");
        }
    }

    /// <summary>
    /// Arms the bridge the way the button does, through the client's own HUD path, and says
    /// whether the client is now waiting for a site. A script that has to arm a placement
    /// before it can ask what a placement would do has to arm it the way a player does.
    /// </summary>
    private void Arm(ProbeCommand command)
    {
        string what = command.Argument(0, "what to arm, which is 'bridge'", "arm bridge").ToLowerInvariant();

        if (what != "bridge")
        {
            throw new ProbeException($"'{what}' is not something that can be armed — usage: arm bridge");
        }

        bool armed = _host.ArmBridge(out string note);

        Emit($"query: arm bridge — {note}");
        Emit($"query:   result     {(armed ? "the next left click picks the site" : "nothing is waiting for a click")}");
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

        Emit(cursor.SiteAllowed
            ? $"query:   placement  accepted — the ghost takes {ProbeFormat.Count(cursor.Footprint, "cell")} and is green"
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

    /// <summary>One line per live entity, which is the answer to "what is on the map at all".</summary>
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
        ProbeCamera camera = _host.ReadCamera();
        bool visible = entity.TeamId == _host.ViewerTeam ||
            (!world.IsHiddenFrom(_host.ViewerTeam, slot) && world.Visibility.IsVisible(_host.ViewerTeam, cell));

        Emit($"query: unit {slot} {entity.Faction.ToString().ToLowerInvariant()}/{entity.Kind} {ProbeLabels.KindName(entity.Kind)}, team {entity.TeamId}, generation {entity.Generation}");
        Emit($"query:   position   {ProbeFormat.Point(SimBridge.ToMetres(entity.Position))}, cell {cell % world.Navigation.Size},{cell / world.Navigation.Size} on {ProbeLabels.Surface(world.TerrainTypes.TypeAt(cell))}");
        Emit($"query:   heading    {ProbeFormat.Angle(SimBridge.HeadingRadians(entity.Heading))}, {entity.Heading} brads of 65536");
        Emit($"query:   health     {entity.Health}/{definition.Health} ({(definition.Health > 0 ? entity.Health * 100 / definition.Health : 0)}%)");
        Emit($"query:   morale     {entity.Morale.ToFloat():0.000}{(entity.Routed ? ", routing" : ", steady")}");
        Emit(
            $"query:   move       {(entity.HasMoveGoal ? $"goal {ProbeFormat.Ground(entity.MoveGoal)}, {ProbeFormat.Metres(entity.Position.HorizontalDistanceTo(entity.MoveGoal) / (float)WorldPos.MmPerMetre)} to go" : "none")}, " +
            $"path {ProbeFormat.Count(entity.PathLength, "cell")} at {entity.PathCursor}{(entity.NeedsPath ? ", waiting for a route" : string.Empty)}, {entity.PathFailures} failures");
        Emit($"query:   attack     {DescribeAttack(world, ref entity, definition)}");
        Emit(
            $"query:   travel     {ProbeFormat.Metres(entity.DistanceTravelledMm / (float)WorldPos.MmPerMetre)} covered at " +
            $"{ProbeFormat.Metres(entity.SpeedMmPerTick.ToFloat() * SimConstants.TickRate / WorldPos.MmPerMetre)} per second " +
            $"({ProbeFormat.Metres(entity.SpeedMmPerTick.ToFloat() / WorldPos.MmPerMetre)} per tick)");
        Emit($"query:   structure  building {(definition.IsBuilding ? "yes" : "no")}, queued {ProbeFormat.Count(entity.QueueLength, "job")}, construction {DescribeConstruction(ref entity)}");
        Emit($"query:   render     {(visible ? "drawn" : "not drawn (fog or stealth)")}, camera at {ProbeFormat.Metres(Vector3.Distance(camera.Position, SimBridge.ToMetres(entity.Position)))}");
    }

    private static string DescribeAttack(SimWorld world, ref Entity entity, UnitDefinition definition)
    {
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

            Emit(
                $"query:   model    {relative}, scale {ProbeFormat.Scale(described.ModelTransform)}, " +
                $"{ProbeFormat.Count(described.Parts.Length, "part")}, " +
                $"wheel radius {(wheel > 0f ? ProbeFormat.Metres(wheel) : "none — any wheel_ part on this model is drawn still")}" +
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
    /// Copies whatever the simulation has reported since the last look into the probe's own
    /// history.
    /// <para>
    /// The bridge's queue is deliberately left alone rather than drained: the client turns
    /// its events into effects once a frame, and a frame is what <c>settle</c> asks for. A
    /// probe that ate them would leave <c>settle</c> with nothing to draw and no way for a
    /// script to photograph the shot it just asked about.
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

        for (int i = _eventsSeen; i < events.Count; i++)
        {
            _events.Add(ProbeEvent.From(events[i], tick, _host.Simulation.World));

            if (_events.Count > EventHistory)
            {
                _events.RemoveAt(0);
            }
        }

        _eventsSeen = events.Count;
    }

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
        UnitKind Kind,
        Vector3 Position,
        int Damage,
        int TargetSlot,
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
            bool hasDirection = false;

            if (simEvent.TargetSlot >= 0 && simEvent.TargetSlot < world.Capacity)
            {
                // The target's position whether or not it is still alive: a shot that killed
                // what it was aimed at is the one whose direction matters most, and the slot
                // still holds the last place that unit stood.
                ref Entity victim = ref world.GetRefBySlot(simEvent.TargetSlot);
                Vector3 point = SimBridge.ToMetres(victim.Position);

                target = $"{victim.Faction.ToString().ToLowerInvariant()}/{victim.Kind} (slot {simEvent.TargetSlot}{(victim.Alive ? string.Empty : ", destroyed by this")})";

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
                simEvent.Kind,
                simEvent.Position,
                simEvent.Damage,
                simEvent.TargetSlot,
                target,
                direction,
                hasDirection,
                range);
        }

        /// <summary>One line: what happened, where, and for a shot which way it went.</summary>
        public string Describe()
        {
            string what = Type switch
            {
                SimEventType.ShotFired => "shot",
                SimEventType.UnitHit => "hit",
                _ => "destroyed",
            };

            var text = new StringBuilder();

            text.Append($"{what,-9} slot {Slot,4} {Faction.ToString().ToLowerInvariant()}/{Kind} at {ProbeFormat.Point(Position)}");

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
