using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MiVic.Core.Sim;
using MiVic.Game.Sim;
using MiVic.Game.Ui;

namespace MiVic.Game;

/// <summary>
/// Performance and correctness report produced by <c>--selftest</c>.
/// <para>
/// The client is a windowed executable, so it has no console by default. The
/// report is therefore written to a file, which makes the milestone claim
/// ("500 instances at 60 fps, Greek text renders") verifiable from a build log
/// rather than by eye.
/// </para>
/// </summary>
public static class SelfTestReport
{
    /// <summary>File the report is written to, next to the executable.</summary>
    public const string FileName = "selftest-report.txt";

    /// <summary>Greek sample text that must render for the UI to be considered valid.</summary>
    public const string GreekSample = "Σοβιετικοί Κινέζοι Δυτικοί Τικ Μονάδες Ζουμ";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>Writes the report and returns its text.</summary>
    public static string Write(
        LaunchOptions options,
        IReadOnlyList<double> frameTimes,
        SimBridge simulation,
        int instances,
        int drawCalls,
        int glyphCount,
        int fontCount,
        bool greekGlyphsOk,
        string fontPath,
        IReadOnlyList<string> loadedModels,
        IReadOnlyList<string> failedModels,
        FontCoverage fontCoverage,
        bool spriteFontHasGreek,
        string windowTitleManaged,
        string? windowTitleSdl,
        int pickHits,
        int pickTotal,
        string hudCommandCheck,
        string clickCheck,
        string moveOrderCheck,
        string combatCheck,
        string aiCheck,
        string replayCheck,
        string missionCheck,
        string particleCheck,
        string sfxCheck,
        string gcCheck,
        string captureCheck,
        double worstFogMaskMilliseconds,
        double averageFogMaskMilliseconds,
        int worstFrameIndex,
        SimProfiler profiler)
    {
        ArgumentNullException.ThrowIfNull(frameTimes);
        ArgumentNullException.ThrowIfNull(loadedModels);
        ArgumentNullException.ThrowIfNull(failedModels);

        double total = 0d;
        double worst = 0d;
        double best = double.MaxValue;

        foreach (double frame in frameTimes)
        {
            total += frame;
            worst = Math.Max(worst, frame);
            best = Math.Min(best, frame);
        }

        double average = frameTimes.Count > 0 ? total / frameTimes.Count : 0d;
        double averageFps = average > 0d ? 1000d / average : 0d;

        // A 60 fps budget is 16.67 ms per frame.
        bool frameBudgetMet = average > 0d && average < 16.67d;

        StringBuilder report = new();
        Append(report, $"MiVic self-test report");
        Append(report, $"=========================");
        Append(report, $"seed                 : {options.Seed}");
        Append(report, $"frames measured      : {frameTimes.Count}");
        Append(report, $"average frame        : {average:0.000} ms");
        Append(report, $"best frame           : {(best == double.MaxValue ? 0d : best):0.000} ms");
        Append(report, $"worst frame          : {worst:0.000} ms (frame {worstFrameIndex})");        Append(report, $"average fps          : {averageFps:0.0}");
        Append(report, $"60fps budget met     : {frameBudgetMet}");
        Append(report, $"entities alive       : {simulation.World.AliveCount}");
        Append(report, $"instances submitted  : {instances}");
        Append(report, $"instanced draw calls : {drawCalls}");
        Append(report, $"fog mask rebuild     : {averageFogMaskMilliseconds:0.000} ms average, {worstFogMaskMilliseconds:0.000} ms worst");
        Append(report, $"simulation ticks     : {simulation.World.Tick}");
        Append(report, $"outcome              : {simulation.World.Outcome}");
        Append(report, $"state hash           : {StateHash.Compute(simulation.World)}");
        Append(report, $"ui font              : {fontPath}");
        Append(report, $"spritefont greek     : {spriteFontHasGreek}");
        bool windowTitleMatches = windowTitleManaged == windowTitleSdl;

        Append(report, $"window title (managed): {windowTitleManaged}");
        Append(report, $"window title (sdl)   : {windowTitleSdl ?? "<unreadable>"}");
        Append(report, $"window title matches : {windowTitleMatches}");

        double pickRate = pickTotal > 0 ? (double)pickHits / pickTotal : 0d;
        Append(report, $"pick round-trip      : {pickHits}/{pickTotal} ({pickRate:P0})");
        Append(report, $"hud command path     : {hudCommandCheck}");
        Append(report, $"click selection      : {clickCheck}");
        Append(report, $"move order path      : {moveOrderCheck}");
        Append(report, $"combat order path    : {combatCheck}");
        Append(report, $"ai activity          : {aiCheck}");
        Append(report, $"replay round-trip    : {replayCheck}");
        Append(report, $"mission objectives   : {missionCheck}");
        Append(report, $"particles            : {particleCheck}");
        Append(report, $"sound effects        : {sfxCheck}");
        Append(report, $"garbage collection   : {gcCheck}");
        Append(report, $"imgui capture        : {captureCheck}");

        // The victory demo exists to prove the banner path end to end, so it
        // asserts the outcome instead of the AI checks it deliberately breaks.
        bool victoryDemoOk = !options.VictoryDemo || simulation.World.Outcome == GameOutcome.AllianceVictory;

        if (options.VictoryDemo)
        {
            Append(report, $"victory demo         : {(victoryDemoOk ? "OK (AllianceVictory)" : $"FAIL (outcome {simulation.World.Outcome})")}");
        }

        report.AppendLine("--- simulation profile (ms per tick) ---");
        Append(report, $"ticks profiled       : {profiler.Ticks}");
        Append(report, $"worst tick           : {profiler.WorstStepMilliseconds:0.000} ms (tick {profiler.WorstStepTick})");
        Append(report, $"commands             : {profiler.MillisecondsPerTick(profiler.Commands):0.000}");
        Append(report, $"ai                   : {profiler.MillisecondsPerTick(profiler.Ai):0.000}");
        Append(report, $"spatial              : {profiler.MillisecondsPerTick(profiler.Spatial):0.000}");
        Append(report, $"vision               : {profiler.MillisecondsPerTick(profiler.Vision):0.000}");
        Append(report, $"economy              : {profiler.MillisecondsPerTick(profiler.Economy):0.000}");
        Append(report, $"research             : {profiler.MillisecondsPerTick(profiler.Research):0.000}");
        Append(report, $"production           : {profiler.MillisecondsPerTick(profiler.Production):0.000}");
        Append(report, $"pathing              : {profiler.MillisecondsPerTick(profiler.Pathing):0.000}");
        Append(report, $"morale               : {profiler.MillisecondsPerTick(profiler.Morale):0.000}");
        Append(report, $"combat               : {profiler.MillisecondsPerTick(profiler.Combat):0.000}");
        Append(report, $"movement             : {profiler.MillisecondsPerTick(profiler.Movement):0.000}");
        report.AppendLine("--- worst single sample per system (ms) ---");
        Append(report, $"commands             : {SimProfiler.ToMilliseconds(profiler.WorstCommands):0.000}");
        Append(report, $"ai                   : {SimProfiler.ToMilliseconds(profiler.WorstAi):0.000}");
        Append(report, $"spatial              : {SimProfiler.ToMilliseconds(profiler.WorstSpatial):0.000}");
        Append(report, $"vision               : {SimProfiler.ToMilliseconds(profiler.WorstVision):0.000}");
        Append(report, $"production           : {SimProfiler.ToMilliseconds(profiler.WorstProduction):0.000}");
        Append(report, $"pathing              : {SimProfiler.ToMilliseconds(profiler.WorstPathing):0.000}");
        Append(report, $"morale               : {SimProfiler.ToMilliseconds(profiler.WorstMorale):0.000}");
        Append(report, $"combat               : {SimProfiler.ToMilliseconds(profiler.WorstCombat):0.000}");
        Append(report, $"movement             : {SimProfiler.ToMilliseconds(profiler.WorstMovement):0.000}");
        Append(report, $"glyphs in atlas      : {glyphCount}");
        Append(report, $"greek glyphs present : {greekGlyphsOk}");
        Append(report, $"font atlas           : {fontCoverage.AtlasWidth}x{fontCoverage.AtlasHeight}, size {fontCoverage.FontSize:0}, ascent {fontCoverage.Ascent:0}");
        Append(report, $"fonts loaded         : {fontCount}");
        Append(report, $"greek coverage       : {fontCoverage.Covered}/{fontCoverage.Total}");

        if (!fontCoverage.IsComplete)
        {
            Append(report, $"greek missing        : {fontCoverage.Missing}");
        }
        Append(report, $"models imported      : {loadedModels.Count}");

        foreach (string model in loadedModels)
        {
            Append(report, $"  model              : {model}");
        }

        Append(report, $"models unavailable   : {failedModels.Count}");

        foreach (string failure in failedModels)
        {
            Append(report, $"  missing            : {failure}");
        }

        bool passed = frameBudgetMet && greekGlyphsOk && spriteFontHasGreek && windowTitleMatches &&
                      pickTotal > 0 && pickRate > 0.5 && Passed(hudCommandCheck) && Passed(clickCheck) &&
                      Passed(moveOrderCheck) && Passed(combatCheck) && Passed(aiCheck) && Passed(replayCheck) &&
                      victoryDemoOk && simulation.World.AliveCount > 0;
        Append(report, $"RESULT               : {(passed ? "PASS" : "FAIL")}");

        string text = report.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, FileName), text);

        // Best effort: show the report when launched from a console.
        if (AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }

        Environment.ExitCode = passed ? 0 : 1;
        return text;
    }

    /// <summary>
    /// A check counts as satisfied when it passed, or when a mode deliberately
    /// made it inapplicable — those report <c>SKIPPED</c> with the reason.
    /// </summary>
    private static bool Passed(string check)
        => check.StartsWith("OK", StringComparison.Ordinal) ||
           check.StartsWith("SKIPPED", StringComparison.Ordinal);

    /// <summary>
    /// Appends a line formatted with the invariant culture, so numbers never
    /// pick up a locale-specific decimal separator.
    /// </summary>
    private static void Append(StringBuilder builder, FormattableString line)
        => builder.AppendLine(line.ToString(CultureInfo.InvariantCulture));

    /// <summary>Starts a frame timer, for coarse wall-clock measurement.</summary>
    public static Stopwatch StartFrameTimer() => Stopwatch.StartNew();
}
