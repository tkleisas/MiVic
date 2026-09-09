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
    /// <summary>Length of each exported theme, in seconds.</summary>
    public const double ThemeSeconds = 30d;

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
        report.AppendLine($"sample rate          : {MusicGenerator.SampleRate} Hz, mono 16-bit");
        report.AppendLine($"theme length         : {ThemeSeconds:0} s");

        foreach (FactionStyle style in Enum.GetValues<FactionStyle>())
        {
            short[] pcm = MusicGenerator.GeneratePcm16(style, seed, ThemeSeconds, out MusicInfo info);
            string name = $"{style.ToString().ToLowerInvariant()}.wav";
            string path = Path.Combine(directory, name);

            WavWriter.Write(path, pcm, MusicGenerator.SampleRate);

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
            report.AppendLine($"    scale            : {info.Scale} (root MIDI {info.RootNote})");
            report.AppendLine($"    tempo            : {info.BeatsPerMinute} BPM");
            report.AppendLine($"    notes            : {info.NoteCount}");
            report.AppendLine($"    samples          : {pcm.Length} ({pcm.Length / (double)MusicGenerator.SampleRate:0.0} s)");
            report.AppendLine($"    peak             : {peak} / 32767 ({peak / 32767d:P0})");
            report.AppendLine($"    rms              : {rms:0}");
        }

        string text = report.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ReportName), text);

        if (AttachConsole(-1))
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

        if (AttachConsole(-1))
        {
            Console.WriteLine();
            Console.Write(text);
        }

        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
