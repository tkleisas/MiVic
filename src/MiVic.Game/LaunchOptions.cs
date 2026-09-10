namespace MiVic.Game;

/// <summary>Command-line options for the client.</summary>
public sealed record LaunchOptions
{
    /// <summary>Simulation seed. The whole world derives from it.</summary>
    public ulong Seed { get; init; } = 20250101UL;

    /// <summary>When greater than zero, run this many frames, report, and exit.</summary>
    public int SelfTestFrames { get; init; }

    /// <summary>When set, save a PNG of the scene and exit.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Frame at which the screenshot is taken, giving the scene time to settle.</summary>
    public int ScreenshotFrame { get; init; } = 45;

    /// <summary>Optional camera distance for a screenshot, in metres.</summary>
    public float? ScreenshotZoom { get; init; }

    /// <summary>Optional camera yaw for a screenshot, in radians.</summary>
    public float? ScreenshotYaw { get; init; }

    /// <summary>Back buffer width, in pixels.</summary>
    public int WindowWidth { get; init; } = 1280;

    /// <summary>Back buffer height, in pixels.</summary>
    public int WindowHeight { get; init; } = 720;

    /// <summary>Starts fullscreen, filling the display's current mode.</summary>
    public bool FullScreen { get; init; }

    /// <summary>Optional camera pitch for a screenshot, in radians.</summary>
    public float? ScreenshotPitch { get; init; }

    /// <summary>Optional camera target X for a screenshot, in metres.</summary>
    public float? ScreenshotTargetX { get; init; }

    /// <summary>Optional camera target Z for a screenshot, in metres.</summary>
    public float? ScreenshotTargetZ { get; init; }

    /// <summary>Draws a large sample of the compiled SpriteFont, for font diagnostics.</summary>
    public bool FontSample { get; init; }

    /// <summary>Selects the player's headquarters at startup, so the build panel is visible.</summary>
    public bool SelectHeadquarters { get; init; }

    /// <summary>When set, render a gallery of every model and exit.</summary>
    public string? ModelGalleryPath { get; init; }

    /// <summary>True when the run is a model gallery.</summary>
    /// <summary>True when the model gallery is being shown instead of a match.</summary>
    public bool IsModelGallery => ModelGalleryPath is not null;

    /// <summary>
    /// Model fixture. One model at a time, on a locked camera, so a model can be
    /// judged on its own instead of guessed at from a screenshot of a battle.
    /// </summary>
    public bool Viewer { get; init; }

    /// <summary>Which model the viewer opens on, as <c>Faction/Kind</c> or a bare kind name.</summary>
    public string? ViewerModel { get; init; }

    /// <summary>Fixed camera yaw for the viewer, in degrees, for reproducible shots.</summary>
    public float? ViewerAngle { get; init; }

    /// <summary>Angle of the viewer camera above the horizon, in degrees.</summary>
    public float ViewerPitch { get; init; } = 62f;

    /// <summary>Distance the viewer camera sits from the model, in metres.</summary>
    public float ViewerDistance { get; init; } = 14f;

    /// <summary>When set, the viewer renders one frame to this file and exits.</summary>
    public string? ViewerShotPath { get; init; }

    /// <summary>Removes every non-player structure at startup, to show the victory banner.</summary>
    public bool VictoryDemo { get; init; }

    /// <summary>Starts a small battle already in weapon range, for looking at the shooting.</summary>
    public bool CombatDemo { get; init; }

    /// <summary>When set, every external command is logged and the match is saved here on exit.</summary>
    public string? RecordPath { get; init; }

    /// <summary>When set, verify this replay headlessly and exit.</summary>
    public string? ReplayPath { get; init; }

    /// <summary>When set, play this recorded match back in the client.</summary>
    public string? WatchPath { get; init; }

    /// <summary>Campaign mission to play, by id.</summary>
    public string? MissionId { get; init; }

    /// <summary>
    /// True when this run is one of the inspection fixtures rather than a match.
    /// <para>
    /// Fixtures are not games: a firing range with one soldier on it is a draw and an
    /// effect catalogue with no enemy is a victory, and both of those banners cover the
    /// thing the fixture exists to show.
    /// </para>
    /// </summary>
    public bool IsFixture => Viewer || IsModelGallery || ParticleDemo || NukeDemo || FireDemo || CombatDemo || LavaDemo || ForestDemo || GroundDemo || TurretDemo || FlightDemo || IsProbe;

    /// <summary>
    /// When set, run the probe script in this file and exit.
    /// <para>
    /// A probe is a scripted inspection channel rather than a fixture: it steps the
    /// simulation from its own commands, answers questions about the world and about what
    /// the renderer is drawing, takes as many screenshots as it is asked for, and exits with
    /// a status that says whether anything failed. It exists because the alternative was a
    /// process launch, a screenshot and a guess per question.
    /// </para>
    /// </summary>
    public string? ProbeScript { get; init; }

    /// <summary>Where a probe writes its transcript. Defaults to <c>probe-report.txt</c>.</summary>
    public string? ProbeOutputPath { get; init; }

    /// <summary>True when this run is a probe script.</summary>
    public bool IsProbe => ProbeScript is not null;

    /// <summary>Spawns a burst of explosions at startup, so a screenshot can show the particle system.</summary>
    public bool ParticleDemo { get; init; }

    /// <summary>Detonates a tactical nuke at startup, framed for a screenshot.</summary>
    public bool NukeDemo { get; init; }

    /// <summary>Fires one of every weapon on a repeating cycle, so rounds can be photographed.</summary>
    public bool FireDemo { get; init; }

    /// <summary>Points the camera at the nearest lava on the map, to look at the surface.</summary>
    public bool LavaDemo { get; init; }

    /// <summary>Points the camera at the densest wood on the map, to look at the trees.</summary>
    public bool ForestDemo { get; init; }

    /// <summary>Points the camera at the most varied ground on the map, to look at the surfaces.</summary>
    public bool GroundDemo { get; init; }

    /// <summary>Frames two tanks shooting at each other, to look at where the turrets point.</summary>
    public bool TurretDemo { get; init; }

    /// <summary>Frames a flyer of every model crossing the map, to look at which way they travel.</summary>
    public bool FlightDemo { get; init; }

    /// <summary>
    /// Names one surface for the ground fixture to frame instead of the most varied
    /// ground: "Mud", "Sand", "Rock" and so on. A treatment is judged from a frame that
    /// is mostly the surface it belongs to, which the varied frame is not.
    /// </summary>
    public string? GroundSurface { get; init; }

    /// <summary>When set, export one WAV per faction theme and exit.</summary>
    public string? RenderAudioPath { get; init; }

    /// <summary>When set, export one WAV per sound effect and exit.</summary>
    public string? RenderSfxPath { get; init; }

    /// <summary>Disables the procedural soundtrack.</summary>
    public bool NoAudio { get; init; }

    /// <summary>UI font size in pixels.</summary>
    public float FontSize { get; init; } = 17f;

    /// <summary>Whether the controls panel starts visible.</summary>
    public bool ShowHelp { get; init; } = true;

    /// <summary>Whether the run should report performance and exit.</summary>
    public bool IsSelfTest => SelfTestFrames > 0;

    /// <summary>Usage text shown by <c>--help</c>.</summary>
    public const string Usage = """
        MiVic — Στρατηγική Πραγματικού Χρόνου

        Χρήση: MiVic.Game [επιλογές]

          --seed <αριθμός>      Σπόρος προσομοίωσης (προεπιλογή 20250101)
          --font-size <μέγεθος> Μέγεθος γραμματοσειράς σε pixel (προεπιλογή 17)
          --no-help             Απόκρυψη του πίνακα χειριστηρίων
          --screenshot <αρχείο> Αποθήκευση στιγμιότυπου PNG και έξοδος
          --record <αρχείο>     Καταγραφή του αγώνα σε αρχείο replay
          --replay <αρχείο>     Επαλήθευση αρχείου replay και έξοδος
          --watch <αρχείο>      Αναπαραγωγή καταγεγραμμένου αγώνα
          --mission <id>        Εκκίνηση αποστολής εκστρατείας
          --mission-list        Λίστα αποστολών
          --particle-demo       Επίδειξη σωματιδίων (εκρήξεις, καπνός)
          --turret-demo         Δύο άρματα που πυροβολούνται, για τον πύργο
          --flight-demo         Αεροσκάφη σε πτήση, για την κατεύθυνση της πλώρης
          --render-audio <dir>  Εξαγωγή θεμάτων μουσικής σε αρχεία WAV
          --render-sfx <dir>    Εξαγωγή ηχητικών εφέ σε αρχεία WAV
          --probe <σενάριο>     Εκτέλεση σεναρίου διερεύνησης και έξοδος
          --probe-out <αρχείο>  Αρχείο καταγραφής της διερεύνησης
          --no-audio            Χωρίς μουσική
          --selftest [καρέ]     Εκτέλεση δοκιμής απόδοσης και έξοδος (προεπιλογή 600)
          --help                Αυτό το μήνυμα
        """;

    /// <summary>Parses command-line arguments.</summary>
    public static LaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        LaunchOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--seed":
                    options = options with { Seed = ParseUlong(NextValue(args, ref i, arg)) };
                    break;

                case "--font-size":
                    options = options with { FontSize = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--no-help":
                    options = options with { ShowHelp = false };
                    break;

                case "--screenshot":
                    options = options with { ScreenshotPath = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--screenshot-frame":
                    // Which frame to capture. A battle is a sequence of events, not a
                    // still life: whether a shot is in flight depends entirely on when
                    // the picture is taken, so it has to be askable for.
                    options = options with { ScreenshotFrame = int.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture) };
                    break;

                case "--screenshot-zoom":
                    options = options with { ScreenshotZoom = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--screenshot-yaw":
                    options = options with { ScreenshotYaw = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--screenshot-pitch":
                    options = options with { ScreenshotPitch = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--screenshot-target-x":
                    options = options with { ScreenshotTargetX = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--screenshot-target-z":
                    options = options with { ScreenshotTargetZ = ParseFloat(NextValue(args, ref i, arg)) };
                    break;

                case "--font-sample":
                    options = options with { FontSample = true, ShowHelp = false };
                    break;

                case "--select-hq":
                    options = options with { SelectHeadquarters = true, ShowHelp = false };
                    break;

                case "--victory-demo":
                    // The victory check runs once a second, so the screenshot has
                    // to wait for it.
                    options = options with { VictoryDemo = true, ShowHelp = false, ScreenshotFrame = 260 };
                    break;

                case "--model-gallery":
                {
                    string path = NextValue(args, ref i, arg);

                    // A gallery is always rendered straight to a file and exits.
                    options = options with
                    {
                        ModelGalleryPath = path,
                        ScreenshotPath = path,
                        ScreenshotFrame = 60,
                        ShowHelp = false,
                    };

                    break;
                }

                case "--viewer":
                    options = options with { Viewer = true, ShowHelp = false };
                    break;

                case "--fullscreen":
                    options = options with { FullScreen = true, ShowHelp = false };
                    break;

                case "--width":
                    // The default 1280x720 is a size to play at, not a size to
                    // inspect a model at: a soldier is forty pixels tall in it.
                    options = options with
                    {
                        WindowWidth = int.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture),
                    };
                    break;

                case "--height":
                    options = options with
                    {
                        WindowHeight = int.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture),
                    };
                    break;

                case "--viewer-model":
                    options = options with { Viewer = true, ViewerModel = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--viewer-angle":
                    options = options with
                    {
                        Viewer = true,
                        ViewerAngle = float.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture),
                        ShowHelp = false,
                    };
                    break;

                case "--viewer-pitch":
                    options = options with
                    {
                        Viewer = true,
                        ViewerPitch = float.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture),
                        ShowHelp = false,
                    };
                    break;

                case "--viewer-distance":
                    // A structure twenty metres long does not fit in a frame
                    // framed for a tank, and the RTS camera clamps how close it
                    // will come, so the fixture has to say how far back to stand.
                    options = options with
                    {
                        Viewer = true,
                        ViewerDistance = float.Parse(NextValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture),
                        ShowHelp = false,
                    };
                    break;

                case "--viewer-shot":
                {
                    // The headless twin of the fixture: one model, one angle, one file,
                    // so a model can be checked without a person at the keyboard.
                    string path = NextValue(args, ref i, arg);

                    options = options with
                    {
                        Viewer = true,
                        ViewerShotPath = path,
                        ScreenshotPath = path,
                        ScreenshotFrame = 30,
                        ShowHelp = false,
                    };

                    break;
                }

                case "--record":
                    options = options with { RecordPath = NextValue(args, ref i, arg) };
                    break;

                case "--replay":
                    options = options with { ReplayPath = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--watch":
                    options = options with { WatchPath = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--particle-demo":
                    // The fireball is brightest a third of a second in.
                    options = options with { ParticleDemo = true, ShowHelp = false, ScreenshotFrame = 20 };
                    break;

                case "--nuke-demo":
                    // Late enough that the column has climbed and the cap has formed.
                    options = options with { NukeDemo = true, ShowHelp = false, ScreenshotFrame = 150 };
                    break;

                case "--fire-demo":
                    options = options with { FireDemo = true, ShowHelp = false, ScreenshotFrame = 40 };
                    break;

                case "--lava-demo":
                    options = options with { LavaDemo = true, ShowHelp = false, ScreenshotFrame = 60 };
                    break;

                case "--forest-demo":
                    options = options with { ForestDemo = true, ShowHelp = false, ScreenshotFrame = 60 };
                    break;

                case "--ground-demo":
                    options = options with { GroundDemo = true, ShowHelp = false, ScreenshotFrame = 60 };
                    break;

                case "--ground-surface":
                    options = options with
                    {
                        GroundDemo = true,
                        GroundSurface = NextValue(args, ref i, arg),
                        ShowHelp = false,
                        ScreenshotFrame = 60,
                    };

                    break;

                case "--combat-demo":
                    // Long enough for the first shots to have crossed the gap and for
                    // the salvo weapons to have fired twice.
                    options = options with { CombatDemo = true, ShowHelp = false, ScreenshotFrame = 150 };
                    break;

                case "--turret-demo":
                    // The tank's reload is a bit over a second, so this lands on the
                    // second or third shot rather than on the first.
                    options = options with { TurretDemo = true, ShowHelp = false, ScreenshotFrame = 120 };
                    break;

                case "--flight-demo":
                    // Four seconds of flight. The flyers start at the western edge,
                    // so an early frame has them out of the camera's window and a late
                    // one has them past it; this lands them in the middle of it.
                    options = options with { FlightDemo = true, ShowHelp = false, ScreenshotFrame = 220 };
                    break;

                case "--render-audio":
                    options = options with { RenderAudioPath = NextValue(args, ref i, arg) };
                    break;

                case "--render-sfx":
                    options = options with { RenderSfxPath = NextValue(args, ref i, arg) };
                    break;

                case "--no-audio":
                    options = options with { NoAudio = true };
                    break;

                case "--probe":
                    // The HUD is off for the same reason the fixtures turn it off: a probe
                    // reads the world, and a panel over the frame is a panel over the answer.
                    options = options with { ProbeScript = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--probe-out":
                    options = options with { ProbeOutputPath = NextValue(args, ref i, arg) };
                    break;

                case "--mission":
                    options = options with { MissionId = NextValue(args, ref i, arg), ShowHelp = false };
                    break;

                case "--mission-list":
                    Console.WriteLine(MiVic.Core.Campaign.MissionCatalog.Describe());
                    Environment.Exit(0);
                    break;

                case "--selftest":
                case "--self-test":
                    int frames = 600;

                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed) && parsed > 0)
                    {
                        frames = parsed;
                        i++;
                    }

                    options = options with { SelfTestFrames = frames, ShowHelp = false };
                    break;

                case "--help":
                case "-h":
                    Console.WriteLine(Usage);
                    Environment.Exit(0);
                    break;

                default:
                    throw new ArgumentException($"Unknown option '{arg}'.\n\n{Usage}", nameof(args));
            }
        }

        return options;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{option}' requires a value.", nameof(args));
        }

        index++;
        return args[index];
    }

    private static ulong ParseUlong(string value)
        => ulong.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not a valid unsigned integer.");

    private static float ParseFloat(string value)
        => float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : throw new ArgumentException($"'{value}' is not a valid number.");
}
