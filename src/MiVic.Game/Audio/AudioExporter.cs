using System.Text;
using MiVic.Audio;

namespace MiVic.Game.Audio;

/// <summary>
/// Headless audio export, behind <c>--render-audio &lt;dir&gt;</c>.
/// <para>
/// Music is the one feature that cannot be verified by reading a log or counting
/// pixels, so the generator writes real WAV files and a report. That makes the
/// audio reviewable — and it is the same code path the game plays.
/// </para>
/// </summary>
public static class AudioExporter
{
    /// <summary>Length of each exported theme, in seconds. The bytebeat's own loop
    /// length is 30.72 seconds (327 680 samples at 8 kHz), rounded in the report.</summary>
    public const double ThemeSeconds = 30.72d;

    /// <summary>File the report is written to, next to the executable.</summary>
    public const string ReportName = "audio-report.txt";

    /// <summary>Writes one WAV per faction into <paramref name="directory"/>.</summary>
    public static int Run(string directory, ulong seed = 20250101UL)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        StringBuilder report = new();
        report.AppendLine("MiVic audio report");
        report.AppendLine("==================");
        report.AppendLine($"sample rate          : {Bytebeat.SampleRate} Hz, mono 16-bit");
        report.AppendLine($"theme length         : {ThemeSeconds:0.##} s");

        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] pcm = Bytebeat.GeneratePcm16(style, seed, out BytebeatInfo info);
            string name = $"{style.ToString().ToLowerInvariant()}.wav";
            string path = Path.Combine(directory, name);

            WavWriter.Write(path, pcm, Bytebeat.SampleRate);

            int peak = 0;
            double sumSquares = 0d;

            foreach (short sample in pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
                sumSquares += (double)sample * sample;
            }

            double rms = Math.Sqrt(sumSquares / Math.Max(1, pcm.Length));

            report.AppendLine();
            report.AppendLine($"  {name}");
            report.AppendLine($"    idiom            : {Describe(style)}");
            report.AppendLine($"    scale            : {info.Scale}");
            report.AppendLine($"    counter rate     : {info.SampleRate} Hz, macro period {info.MacroPeriod} samples");
            report.AppendLine($"    samples          : {pcm.Length} ({pcm.Length / (double)Bytebeat.SampleRate:0.0} s)");
            report.AppendLine($"    peak             : {peak} / 32767 ({peak / 32767d:P0})");
            report.AppendLine($"    rms              : {rms:0}");
        }

        string text = report.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ReportName), text);

        // Attaching to the parent console is a Windows courtesy; on Linux the process is
        // already attached, and asking kernel32 about it would end the export with an
        // exception after every file was written.
        if (OperatingSystem.IsWindows() && AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }

        return 0;
    }

    /// <summary>Writes one WAV per leitmotiv and fill of every faction's score.</summary>
    public static int RunScores(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        StringBuilder report = new();
        report.AppendLine("MiVic score report");
        report.AppendLine("==================");
        report.AppendLine($"sample rate          : {SoundBank.SampleRate} Hz, mono 16-bit");

                string[] factions = ["soviet", "chinese", "western"];

        foreach (string faction in factions)
        {
            Score score = Score.Load(Score.PathFor(faction));

            report.AppendLine();
            report.AppendLine($"  {faction} — {score.Tempo} bpm, root {score.Root}, {score.Grid} βήματα/μέτρο");

            foreach (Leitmotiv leitmotiv in score.Leitmotivs)
            {
                short[] pcm = Sequencer.RenderLeitmotiv(score, leitmotiv);
                string name = $"{faction}-{leitmotiv.Name}.wav";
                string path = Path.Combine(directory, name);

                WavWriter.Write(path, pcm, SoundBank.SampleRate);

                int peak = 0;
                double sumSquares = 0d;

                foreach (short sample in pcm)
                {
                    peak = Math.Max(peak, Math.Abs((int)sample));
                    sumSquares += (double)sample * sample;
                }

                double rms = Math.Sqrt(sumSquares / Math.Max(1, pcm.Length));

                report.AppendLine(
                    $"    {name,-28} {pcm.Length / (double)SoundBank.SampleRate:0.00} s " +
                    $"({leitmotiv.Bars} μέτρα)  peak {peak / 32767d:P0}  rms {rms:0}");
            }

            foreach (ScoreFill fill in score.Fills)
            {
                short[] pcm = Sequencer.RenderFill(score, fill);
                string name = $"{faction}-fill-{fill.Name}.wav";

                WavWriter.Write(Path.Combine(directory, name), pcm, SoundBank.SampleRate);

                report.AppendLine(
                    $"    {name,-28} {pcm.Length / (double)SoundBank.SampleRate:0.00} s (γέμισμα)");
            }
        }

        string text = report.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "score-report.txt"), text);

        // Attaching to the parent console is a Windows courtesy; on Linux the process is
        // already attached, and asking kernel32 about it would end the export with an
        // exception after every file was written.
        if (OperatingSystem.IsWindows() && AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }

        return 0;
    }

    /// <summary>The musical tradition each faction's theme is written in.</summary>
    public static string Describe(FactionStyle style) => style switch
    {
        FactionStyle.Soviet => "εμβατήριο σε αρμονική ελάσσονα",
        FactionStyle.Chinese => "πεντατονικό τραγούδι",
        FactionStyle.Western => "ροκ εν ρολ και μπλουζ",
        _ => "άγνωστο",
    };

    /// <summary>Writes one WAV per sound effect into <paramref name="directory"/>.</summary>
    public static int RunSfx(string directory, ulong seed = 20250101UL)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        StringBuilder report = new();
        report.AppendLine("MiVic sound effect report");
        report.AppendLine("=========================");
        report.AppendLine($"sample rate          : {SoundBank.SampleRate} Hz, mono 16-bit");

        foreach (SoundEffectKind kind in Enum.GetValues<SoundEffectKind>())
        {
            short[] pcm = SoundBank.GeneratePcm16(kind, seed);
            string name = $"{kind.ToString().ToLowerInvariant()}.wav";

            WavWriter.Write(Path.Combine(directory, name), pcm, SoundBank.SampleRate);

            int peak = 0;
            double sumSquares = 0d;

            foreach (short sample in pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
                sumSquares += (double)sample * sample;
            }

            report.AppendLine(
                $"  {name,-26} {pcm.Length / (double)SoundBank.SampleRate:0.000} s  " +
                $"peak {peak / 32767d:P0}  rms {Math.Sqrt(sumSquares / pcm.Length):0}");
        }

        string text = report.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sfx-report.txt"), text);

        // Attaching to the parent console is a Windows courtesy; on Linux the process is
        // already attached, and asking kernel32 about it would end the export with an
        // exception after every file was written.
        if (OperatingSystem.IsWindows() && AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }

        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
