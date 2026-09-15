using System.Text.Json;

namespace MiVic.Audio;

/// <summary>One hit of one kit voice inside a bar: when it lands and how hard.</summary>
/// <param name="Step">Step within the bar, in whatever the score's grid counts.</param>
/// <param name="Accent">True when the hit is written X rather than x: the harder of the two velocities.</param>
public readonly record struct KitHit(int Step, bool Accent);

/// <summary>One pitched event: when it starts, how long it rings, and where it sits.</summary>
/// <param name="Step">Step within the bar the event starts on.</param>
/// <param name="Degrees">
/// Scale degrees from the root — the faction's own vocabulary. A list makes a chord; one entry
/// makes a line.
/// </param>
/// <param name="Length">Steps the event rings for, before its release.</param>
/// <param name="Octave">Which octave the degrees sit in, relative to the score's root.</param>
public readonly record struct PitchedEvent(int Step, int[] Degrees, int Length, int Octave);

/// <summary>One bar of one pitched family: which family, and the notes it plays.</summary>
public sealed record PitchedVoice(string Family, PitchedEvent[] Events);

/// <summary>
/// One bar of one voice. A pattern is a bar's worth of writing: the kit voices carry step
/// strings — <c>X..x</c>, accent, hit, rest — and the pitched voices carry event lists.
/// <para>
/// A pattern is one bar, and a leitmotiv repeats its patterns for as many bars as it declares.
/// That is a constraint the music barely notices and the author never feels — a variation is a
/// second pattern — and it keeps every leitmotiv loopable by construction, because a loop
/// boundary is a bar boundary and the render is a whole multiple of the bar.
/// </para>
/// </summary>
public sealed record ScorePattern(
    string Name,
    KitHit[][] Kit,
    PitchedVoice[] Pitched);

/// <summary>How a leitmotiv is laid out: the patterns it cycles and how many bars it runs.</summary>
/// <param name="Name">Stable name; triggers and fills name leitmotivs by this.</param>
/// <param name="Bars">How many bars the leitmotiv runs before it loops.</param>
/// <param name="Patterns">The patterns, layered; each repeats every bar.</param>
public sealed record Leitmotiv(string Name, int Bars, ScorePattern[] Patterns);

/// <summary>One bar of a fill: the kit writing that carries a transition into the next leitmotiv.</summary>
/// <param name="Name">Stable name.</param>
/// <param name="Kit">The hits, one bar long.</param>
public sealed record ScoreFill(string Name, KitHit[][] Kit);

/// <summary>What the game's own state asks the music to do, and which leitmotiv answers it.</summary>
/// <param name="Victory">The leitmotiv the win locks in.</param>
/// <param name="Defeat">The leitmotiv the loss locks in.</param>
/// <param name="Combat">The leitmotiv while the viewer's own units are firing or being fired on.</param>
/// <param name="Alert">The leitmotiv while the viewer's own structures are under fire.</param>
/// <param name="Calm">The leitmotiv when nothing else is being said.</param>
/// <param name="UpBars">
/// Bars a higher rung must hold before it takes the music: a skirmish that flares for a bar is
/// not a battle.
/// </param>
/// <param name="DownBars">Bars a lower rung must hold before the music comes down to it.</param>
public sealed record ScoreCues(
    string Victory,
    string Defeat,
    string Combat,
    string Alert,
    string Calm,
    int UpBars,
    int DownBars);

/// <summary>
/// A faction's whole score: the instruments are shared, the music is not.
/// <para>
/// <b>The file is data; the sound is code.</b> Nothing here is synthesised in advance — every
/// pattern is a bar of writing and every leitmotiv is a list of patterns, and the sequencer
/// renders the lot to PCM at the sound bank's rate on first ask. That keeps the repository free
/// of audio assets and keeps a change of tempo a diff rather than a re-record.
/// </para>
/// <para>
/// <b>Validation is the loader's job.</b> A pattern whose step string is the wrong length, a
/// leitmotiv that names a pattern that does not exist, a cue that names a leitmotiv that does
/// not exist — each is refused with the sentence that says why, because a score that plays
/// half of what it says is worse than one that refuses to play at all.
/// </para>
/// </summary>
public sealed record Score(
    int Tempo,
    int Grid,
    int Root,
    int[] Scale,
    ScorePattern[] Patterns,
    Leitmotiv[] Leitmotivs,
    ScoreFill[] Fills,
    ScoreCues Cues)
{
    /// <summary>The kit voices, in the order a pattern's step strings are read.</summary>
    public static readonly string[] KitVoices =
    [
        "bassdrum", "snare", "tom1", "tom2", "hihat", "hihat-open", "tambourine",
    ];

    /// <summary>Velocity of an ordinary hit. The accent is 1.</summary>
    public const float NormalVelocity = 0.72f;

    /// <summary>Finds a pattern by name, or null when there is none.</summary>
    public ScorePattern? Pattern(string name)
    {
        foreach (ScorePattern pattern in Patterns)
        {
            if (pattern.Name == name)
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>Finds a leitmotiv by name, or null when there is none.</summary>
    public Leitmotiv? LeitmotivByName(string name)
    {
        foreach (Leitmotiv leitmotiv in Leitmotivs)
        {
            if (leitmotiv.Name == name)
            {
                return leitmotiv;
            }
        }

        return null;
    }

    /// <summary>Finds a fill by name, or null when there is none.</summary>
    public ScoreFill? Fill(string name)
    {
        foreach (ScoreFill fill in Fills)
        {
            if (fill.Name == name)
            {
                return fill;
            }
        }

        return null;
    }

    /// <summary>Seconds one bar runs for, at the score's tempo.</summary>
    public double SecondsPerBar => 60d * Grid / 4d / Tempo;

    /// <summary>Seconds one step runs for.</summary>
    public double SecondsPerStep => SecondsPerBar / Grid;

    /// <summary>
    /// Resolves a faction's score file from wherever the process was started. The scores live
    /// with the repository — <c>scores/&lt;faction&gt;.score.json</c>, next to <c>missions/</c>
    /// and <c>maps/</c> — and a build's own directory is several flights up from them, so the
    /// lookup walks up until the directory that holds the scores is found.
    /// </summary>
    public static string PathFor(string faction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(faction);

        string relative = Path.Combine("scores", $"{faction}.score.json");

                string[] starts = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];

        foreach (string start in starts)
        {
            DirectoryInfo? directory = new(start);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, relative);

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new ScoreException($"παρτιτούρα: δεν υπάρχει scores/{faction}.score.file πάνω από τον κατάλογο εκτέλεσης");
    }

    /// <summary>
    /// Loads and validates a score file. The refusals are the author's — a pattern of the wrong
    /// length, a leitmotiv naming a pattern that does not exist, a cue naming a leitmotiv that
    /// does not exist — each in the sentence that says which line said what.
    /// </summary>
    public static Score Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ScoreException($"παρτιτούρα {path}: δεν υπάρχει αρχείο");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new ScoreException($"παρτιτούρα {path}: δεν είναι JSON — {exception.Message}");
        }

        using (document)
        {
            return FromJson(document.RootElement, path);
        }
    }

    private static Score FromJson(JsonElement root, string path)
    {
        JsonElement tempoElement = Require(root, "tempo", path);
        JsonElement gridElement = Require(root, "grid", path);
        JsonElement rootElement = Require(root, "root", path);
        JsonElement scaleElement = Require(root, "scale", path);

        int tempo = IntOf(tempoElement, "tempo", path);
        int grid = IntOf(gridElement, "grid", path);
        int rootNote = IntOf(rootElement, "root", path);
        int[] scale = [.. scaleElement.EnumerateArray().Select(e => IntOf(e, "scale[]", path))];

        if (tempo is < 40 or > 240)
        {
            throw new ScoreException($"παρτιτούρα {path}: το tempo {tempo} είναι εκτός 40..240");
        }

        if (grid is < 4 or > 64)
        {
            throw new ScoreException($"παρτιτούρα {path}: το grid {grid} είναι εκτός 4..64 βήματα ανά μέτρο");
        }

        if (scale.Length == 0)
        {
            throw new ScoreException($"παρτιτούρα {path}: η κλίμακα είναι άδεια");
        }

        var patterns = new List<ScorePattern>();

        foreach (JsonProperty property in Require(root, "patterns", path).EnumerateObject())
        {
            patterns.Add(ReadPattern(property, grid, path));
        }

        if (patterns.Count == 0)
        {
            throw new ScoreException($"παρτιτούρα {path}: κανένα μοτίβο");
        }

        var leitmotivs = new List<Leitmotiv>();

        foreach (JsonProperty property in Require(root, "leitmotivs", path).EnumerateObject())
        {
            leitmotivs.Add(ReadLeitmotiv(property, patterns, path));
        }

        if (leitmotivs.Count == 0)
        {
            throw new ScoreException($"παρτιτούρα {path}: κανένα θέμα");
        }

        var fills = new List<ScoreFill>();

        if (root.TryGetProperty("fills", out JsonElement fillsElement))
        {
            foreach (JsonProperty property in fillsElement.EnumerateObject())
            {
                fills.Add(ReadFill(property, grid, path));
            }
        }

        ScoreCues cues = ReadCues(Require(root, "cues", path), leitmotivs, path);

        return new Score(tempo, grid, rootNote, scale, [.. patterns], [.. leitmotivs], [.. fills], cues);
    }

    private static ScorePattern ReadPattern(JsonProperty property, int grid, string path)
    {
        var kit = new KitHit[KitVoices.Length][];

        for (int voice = 0; voice < kit.Length; voice++)
        {
            kit[voice] = [];
        }

        var pitched = new List<PitchedVoice>();

        foreach (JsonProperty voice in property.Value.EnumerateObject())
        {
            int kitIndex = Array.IndexOf(KitVoices, voice.Name);

            if (kitIndex >= 0)
            {
                kit[kitIndex] = ReadKitVoice(voice.Name, voice.Value, property.Name, grid, path);
                continue;
            }

            if (voice.Name is not ("bass" or "strings" or "horns"))
            {
                throw new ScoreException(
                    $"μοτίβο {property.Name}: η φωνή {voice.Name} δεν είναι του οργανολογίου — " +
                    $"{string.Join(", ", KitVoices)}, bass, strings, horns");
            }

            pitched.Add(new PitchedVoice(voice.Name, ReadPitched(voice.Name, voice.Value, property.Name, path)));
        }

        return new ScorePattern(property.Name, kit, [.. pitched]);
    }

    private static KitHit[] ReadKitVoice(string voice, JsonElement value, string pattern, int grid, string path)
    {
        string steps = StringOf(value, $"μοτίβο {pattern}, φωνή {voice}", path);

        if (steps.Length != grid)
        {
            throw new ScoreException(
                $"μοτίβο {pattern}, φωνή {voice}: {steps.Length} βήματα, το μέτρο θέλει {grid}");
        }

        var hits = new List<KitHit>();

        for (int step = 0; step < steps.Length; step++)
        {
            switch (steps[step])
            {
                case 'X':
                    hits.Add(new KitHit(step, Accent: true));
                    break;
                case 'x':
                    hits.Add(new KitHit(step, Accent: false));
                    break;
                case '.':
                    break;
                default:
                    throw new ScoreException(
                        $"μοτίβο {pattern}, φωνή {voice}: «{steps[step]}» δεν είναι χτύπημα — X, x ή .");
            }
        }

        return [.. hits];
    }

    private static PitchedEvent[] ReadPitched(string voice, JsonElement array, string pattern, string path)
    {
        var events = new List<PitchedEvent>();

        foreach (JsonElement element in array.EnumerateArray())
        {
            int step = IntOf(Require(element, "step", path), "step", path);
            var degrees = new List<int>();

            JsonElement degreesElement = Require(element, "degrees", path);

            if (degreesElement.ValueKind == JsonValueKind.Number)
            {
                degrees.Add(IntOf(degreesElement, "degrees", path));
            }
            else
            {
                degrees.AddRange(degreesElement.EnumerateArray().Select(e => IntOf(e, "degrees", path)));
            }

            int length = element.TryGetProperty("length", out JsonElement lengthElement)
                ? IntOf(lengthElement, "length", path)
                : 1;
            int octave = element.TryGetProperty("octave", out JsonElement octaveElement)
                ? IntOf(octaveElement, "octave", path)
                : 0;

            if (step < 0)
            {
                throw new ScoreException($"μοτίβο {pattern}, φωνή {voice}: βήμα {step} κάτω από το μηδέν");
            }

            if (length <= 0)
            {
                throw new ScoreException($"μοτίβο {pattern}, φωνή {voice}: μήκος {length} δεν είναι μήκος");
            }

            events.Add(new PitchedEvent(step, [.. degrees], length, octave));
        }

        return [.. events];
    }

    private static Leitmotiv ReadLeitmotiv(JsonProperty property, List<ScorePattern> patterns, string path)
    {
        JsonElement value = property.Value;
        int bars = IntOf(Require(value, "bars", path), "bars", path);

        if (bars is < 1 or > 64)
        {
            throw new ScoreException($"θέμα {property.Name}: {bars} μέτρα, εκτός 1..64");
        }

        var chosen = new List<ScorePattern>();

        foreach (JsonElement name in Require(value, "patterns", path).EnumerateArray())
        {
            string patternName = StringOf(name, $"θέμα {property.Name}: ένα μοτίβο", path);
            ScorePattern? found = patterns.Find(candidate => candidate.Name == patternName);

            if (found is null)
            {
                throw new ScoreException($"θέμα {property.Name}: το μοτίβο {patternName} δεν υπάρχει");
            }

            chosen.Add(found);
        }

        if (chosen.Count == 0)
        {
            throw new ScoreException($"θέμα {property.Name}: κανένα μοτίβο");
        }

        return new Leitmotiv(property.Name, bars, [.. chosen]);
    }

    private static ScoreFill ReadFill(JsonProperty property, int grid, string path)
    {
        var kit = new KitHit[KitVoices.Length][];

        for (int voice = 0; voice < kit.Length; voice++)
        {
            kit[voice] = [];
        }

        foreach (JsonProperty voice in property.Value.EnumerateObject())
        {
            int kitIndex = Array.IndexOf(KitVoices, voice.Name);

            if (kitIndex < 0)
            {
                throw new ScoreException(
                    $"γέμισμα {property.Name}: η φωνή {voice.Name} δεν είναι του κιτ — {string.Join(", ", KitVoices)}");
            }

            kit[kitIndex] = ReadKitVoice(voice.Name, voice.Value, property.Name, grid, path);
        }

        return new ScoreFill(property.Name, kit);
    }

    private static ScoreCues ReadCues(JsonElement element, List<Leitmotiv> leitmotivs, string path)
    {
        string name(string field)
        {
            JsonElement value = Require(element, field, path);
            string leitmotivName = StringOf(value, $"ενδείξεις: το πεδίο {field}", path);

            if (!leitmotivs.Any(candidate => candidate.Name == leitmotivName))
            {
                throw new ScoreException(
                    $"ενδείξεις: το πεδίο {field} καλεί το θέμα {leitmotivName}, που δεν υπάρχει");
            }

            return leitmotivName;
        }

        int upBars = element.TryGetProperty("up-bars", out JsonElement upElement)
            ? IntOf(upElement, "up-bars", path)
            : 2;
        int downBars = element.TryGetProperty("down-bars", out JsonElement downElement)
            ? IntOf(downElement, "down-bars", path)
            : 4;

        return new ScoreCues(
            name("victory"),
            name("defeat"),
            name("combat"),
            name("alert"),
            name("calm"),
            upBars,
            downBars);
    }

    private static JsonElement Require(JsonElement element, string field, string path)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out JsonElement found))
        {
            return found;
        }

        throw new ScoreException($"παρτιτούρα {path}: λείπει το πεδίο {field}");
    }

    private static int IntOf(JsonElement value, string field, string path)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new ScoreException($"παρτιτούρα {path}: το πεδίο {field} δεν είναι αριθμός");
        }

        // TryGetInt32 rather than GetInt32: the latter throws FormatException for a number
        // it cannot fit in an int — `"tempo": 112.5` is the everyday typo — and a
        // FormatException is not the ScoreException this loader defines, nor one the audio
        // director catches, so it escaped as an unhandled exception rather than a refusal.
        if (!value.TryGetInt32(out int number))
        {
            throw new ScoreException($"παρτιτούρα {path}: το πεδίο {field} δεν είναι ακέραιος");
        }

        return number;
    }

    /// <summary>
    /// Reads a string, refusing a value of another kind with this loader's own exception.
    /// <see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/> on
    /// a number or an object, which is not the exception this file promises its callers.
    /// </summary>
    private static string StringOf(JsonElement value, string what, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ScoreException($"παρτιτούρα {path}: {what} δεν είναι κείμενο");
        }

        return value.GetString()!;
    }
}

/// <summary>Why a score file was refused.</summary>
public sealed class ScoreException(string message) : Exception(message);
