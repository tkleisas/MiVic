using System.Runtime.InteropServices;
using MiVic.Game;

// A windowed executable has no console, so failures are written to a log file as
// well as to any console that happens to be attached. Without this, a startup
// failure looks like nothing happening at all.
const string CrashLogName = "crash.log";

try
{
    UseUtf8Console();

    // Model diagnostics run before any graphics device exists.
    if (args.Length >= 1 && args[0] is "--inspect-models")
    {
        string directory = args.Length >= 2
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "Content", "Models");

        int exitCode = ModelInspector.Run(directory);

        if (AttachConsole(-1))
        {
            // The inspector already wrote to the console.
        }

        return exitCode;
    }

    // ImGui draw-data diagnostics, also headless.
    if (args.Length >= 1 && args[0] is "--inspect-ui")
    {
        string fontFile = Path.Combine(AppContext.BaseDirectory, "Content", "Fonts", "NotoSans-Regular.ttf");
        return UiDiagnostics.Run(fontFile, 17f);
    }

    // Replay verification, also headless: no graphics device is needed to prove
    // that a recording reproduces its match.
    if (args.Length >= 2 && args[0] is "--replay")
    {
        return ReplayTool.Run(args[1]);
    }

    // Music export, also headless: the themes are generated, not loaded.
    if (args.Length >= 2 && args[0] is "--render-audio")
    {
        return MiVic.Game.Audio.AudioExporter.Run(args[1]);
    }

    // Sound-effect export, also headless.
    if (args.Length >= 2 && args[0] is "--render-sfx")
    {
        return MiVic.Game.Audio.AudioExporter.RunSfx(args[1]);
    }

    LaunchOptions options = LaunchOptions.Parse(args);
    using MiVicGame game = new(options);
    game.Run();
    return Environment.ExitCode;
}
catch (Exception exception)
{
    string message = $"MiVic failed to start:\n{exception}";

    try
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, CrashLogName), message);
    }
    catch (IOException)
    {
        // Nothing useful left to do if even the log cannot be written.
    }

    if (AttachConsole(-1))
    {
        Console.Error.WriteLine(message);
    }

    return 1;
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool AttachConsole(int processId);

/// <summary>
/// Makes the console able to print Greek.
/// <para>
/// A Windows console starts on a legacy code page, so every Greek title, mission name
/// and unit label this program prints arrived as rows of question marks and accented
/// rubbish — and it did so in redirected output too, which is how the screenshot
/// fixtures and the model inspector are read. A program whose entire interface is Greek
/// was unreadable in the one place its diagnostics live.
/// </para>
/// </summary>
static void UseUtf8Console()
{
    try
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
    catch (IOException)
    {
        // No console to configure — output is a pipe or a file, which is already
        // byte-oriented, and there is nothing to set. Not a reason to fail.
    }
}
