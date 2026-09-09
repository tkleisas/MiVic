using System.Runtime.InteropServices;
using System.Text;
using MiVic.Core.Replay;

namespace MiVic.Game;

/// <summary>
/// Headless replay verification, behind <c>--replay &lt;file&gt;</c>.
/// <para>
/// A windowed executable has no console, so the verdict is written to a file as
/// well as to any attached console and returned as the process exit code. That
/// makes a replay check usable from a build script: 0 means the recording was
/// reproduced exactly.
/// </para>
/// </summary>
public static class ReplayTool
{
    /// <summary>File the verdict is written to, next to the executable.</summary>
    public const string ReportName = "replay-report.txt";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>Loads, replays and verifies <paramref name="path"/>.</summary>
    public static int Run(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        StringBuilder report = new();

        try
        {
            ReplayFile replay = ReplayFile.Load(path);
            ReplayResult result = replay.Verify();

            report.AppendLine("MiVic replay report");
            report.AppendLine("===================");
            report.AppendLine($"file                 : {path}");
            report.AppendLine($"seed                 : {replay.Seed}");
            report.AppendLine($"scenario             : {replay.Scenario}");
            report.AppendLine($"capacity             : {replay.Capacity}");
            report.AppendLine($"commands             : {replay.Commands.Count} recorded, {result.CommandsApplied} applied");
            report.AppendLine($"ticks                : {result.ExpectedTick} expected, {result.ActualTick} replayed");
            report.AppendLine($"state hash expected  : {result.ExpectedHash}");
            report.AppendLine($"state hash replayed  : {result.ActualHash}");
            report.AppendLine($"RESULT               : {(result.Matches ? "PASS (replay reproduced the match)" : "FAIL (state hash differs)")}");

            Write(report.ToString());
            return result.Matches ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            report.AppendLine("MiVic replay report");
            report.AppendLine("===================");
            report.AppendLine($"file                 : {path}");
            report.AppendLine($"RESULT               : FAIL ({exception.GetType().Name}: {exception.Message})");

            Write(report.ToString());
            return 2;
        }
    }

    private static void Write(string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ReportName), text);
        }
        catch (IOException)
        {
            // The console output below is still worth attempting.
        }

        if (AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }
    }
}
