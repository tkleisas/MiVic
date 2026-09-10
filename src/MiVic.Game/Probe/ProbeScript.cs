using System.Globalization;
using System.Text;

namespace MiVic.Game.Probe;

/// <summary>
/// Something a probe command could not do: an unknown verb, a missing or unparseable
/// argument, a slot with nothing alive in it.
/// <para>
/// A distinct type because a probe command that fails must not abort the script: the
/// runner catches this, writes an <c>error:</c> line and runs the next command, so a
/// typo on line four of a fifty-line script costs one line rather than the other
/// forty-six.
/// </para>
/// </summary>
public sealed class ProbeException : Exception
{
    public ProbeException(string message)
        : base(message)
    {
    }
}

/// <summary>One line of a probe script, already split into a verb and its arguments.</summary>
public sealed class ProbeCommand
{
    public ProbeCommand(int line, string text, string verb, string[] arguments)
    {
        Line = line;
        Text = text;
        Verb = verb;
        Arguments = arguments;
    }

    /// <summary>Line number in the script, for error messages that can be acted on.</summary>
    public int Line { get; }

    /// <summary>The line as written, echoed into the transcript so it is navigable.</summary>
    public string Text { get; }

    /// <summary>The command word, matched case-insensitively.</summary>
    public string Verb { get; }

    /// <summary>Everything after the verb.</summary>
    public string[] Arguments { get; }

    /// <summary>How many arguments were given.</summary>
    public int ArgumentCount => Arguments.Length;

    /// <summary>A required argument.</summary>
    public string Argument(int index, string expected, string usage)
    {
        if (index >= Arguments.Length)
        {
            throw new ProbeException($"'{Verb}' needs {expected} — usage: {usage}");
        }

        return Arguments[index];
    }

    /// <summary>An optional argument, or null when it was not given.</summary>
    public string? Optional(int index) => index < Arguments.Length ? Arguments[index] : null;

    /// <summary>A required number.</summary>
    public float Number(int index, string expected, string usage)
    {
        string text = Argument(index, expected, usage);

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new ProbeException($"'{Verb}' needs {expected}, and '{text}' is not a number — usage: {usage}");
    }

    /// <summary>An optional number, or the fallback when the argument is absent.</summary>
    public float OptionalNumber(int index, float fallback, string expected, string usage)
        => index < Arguments.Length ? Number(index, expected, usage) : fallback;

    /// <summary>
    /// A required whole number, range-checked.
    /// <para>
    /// The range is checked rather than trusted because these arguments are a tick count
    /// and a slot index: a minus sign typed in front of a tick count, or a slot number
    /// from a transcript of a different run, would otherwise index off the end of an
    /// array or spin a script for a million ticks before anyone noticed.
    /// </para>
    /// </summary>
    public int Whole(int index, string expected, string usage, int minimum, int maximum)
    {
        string text = Argument(index, expected, usage);

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new ProbeException($"'{Verb}' needs {expected}, and '{text}' is not a whole number — usage: {usage}");
        }

        return value < minimum || value > maximum
            ? throw new ProbeException($"'{Verb}' needs {expected} in {minimum}..{maximum}, and {value} is outside it — usage: {usage}")
            : value;
    }
}

/// <summary>A script as loaded: its commands, or why it could not be read.</summary>
/// <param name="Path">The script's full path.</param>
/// <param name="Commands">The commands, in file order.</param>
/// <param name="Error">Why the file could not be read, or null when it was.</param>
public sealed record ProbeScriptFile(string Path, ProbeCommand[] Commands, string? Error);

/// <summary>
/// Reads a probe script.
/// <para>
/// The format is deliberately the one a shell-minded reader already expects: one command
/// per line, blank lines and <c>#</c> comments ignored, arguments separated by spaces and
/// double-quoted when they contain a space. A format with a grammar would be a format to
/// learn; this one is a list, which is what a test script is.
/// </para>
/// </summary>
public static class ProbeScript
{
    /// <summary>Loads a script, reporting a read failure rather than throwing it.</summary>
    public static ProbeScriptFile Load(string path)
    {
        try
        {
            string[] lines = File.ReadAllLines(path);
            var commands = new List<ProbeCommand>(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                if (TryParse(lines[i], i + 1, out ProbeCommand? command))
                {
                    commands.Add(command!);
                }
            }

            return new ProbeScriptFile(path, [.. commands], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProbeScriptFile(path, [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>Parses one line, returning false for a blank line or a comment.</summary>
    public static bool TryParse(string line, int number, out ProbeCommand? command)
    {
        command = null;

        var tokens = new List<string>();
        var token = new StringBuilder();
        bool quoted = false;
        bool started = false;

        foreach (char character in line)
        {
            if (quoted)
            {
                if (character == '"')
                {
                    quoted = false;
                    continue;
                }

                token.Append(character);
                continue;
            }

            if (character == '"')
            {
                quoted = true;
                started = true;
                continue;
            }

            // A `#` that starts a token is a trailing comment, which is what makes
            // `tick 60   # three seconds` readable. A `#` inside a token is not, so a
            // file named with one still works.
            if (character == '#' && !started)
            {
                break;
            }

            if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    started = false;
                }

                continue;
            }

            started = true;
            token.Append(character);
        }

        if (started)
        {
            tokens.Add(token.ToString());
        }

        if (tokens.Count == 0)
        {
            return false;
        }

        command = new ProbeCommand(
            number,
            line.Trim(),
            tokens[0].ToLowerInvariant(),
            [.. tokens.Skip(1)]);

        return true;
    }
}
