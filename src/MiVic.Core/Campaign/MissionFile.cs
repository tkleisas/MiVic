using System.Text.Json;
using System.Text.Json.Serialization;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// A mission on disk: the format the campaign's authoring reads and writes.
/// <para>
/// A mission was compiled-in data — <see cref="MissionDefinition"/> records written in C# —
/// which made the campaign a thing only a programmer could edit, and left every validator
/// the simulation owns answering questions nobody was asking outside a build. The format
/// is the first missing piece §10 names, and it is deliberately the least of what is
/// possible: a mission is a seed, a roster, a list of objectives and a list of triggers,
/// all of it data already, so the file is a shape around data rather than a language.
/// JSON, indented, enums spelled out as their names, because the missions in this
/// repository are meant to stay readable by anyone editing them, and a diff that says
/// which sentence of the briefing changed is worth more than one that says which byte did.
/// </para>
/// <para>
/// <b>Everything a file loads is asked the simulation's own questions.</b> The loader runs
/// <see cref="TriggerSystem.Validate"/> — the script that can never fire, the objective the
/// opening world has already decided, the side that stands in nothing — and refuses the
/// mission with the validator's own sentences, because an authored mission that cannot be
/// won is the worst thing this layer can ship and the author should meet the refusal where
/// they are working, not in a lost match. Placement stays where it has always been: the
/// scenario build lays the mission out through the same functions a campaign mission uses,
/// so a file that names an impossible base is refused by the build, not by a second rule
/// here.
/// </para>
/// <para>
/// <b>The version exists because a format nobody versioned is a format nobody can change.</b>
/// A file from another version is a refusal that names both versions, the same answer a
/// replay and a progress file give.
/// </para>
/// </summary>
public static class MissionFile
{
    /// <summary>File magic, as the envelope spells it.</summary>
    public const string Magic = "MiVicMission";

    /// <summary>Envelope version. Bump whenever the layout changes.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(),
            new WorldPosConverter(),
            new MatchRosterConverter(),
        },
    };

    /// <summary>
    /// Loads a mission from a file. The envelope is checked first — a file from another
    /// version is a refusal that names both versions — and the validator's verdict is
    /// the loader's: a mission with problems is a file this build refuses, with the
    /// problems in the message, because they are sentences an author can act on.
    /// </summary>
    public static MissionDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"'{path}' is not valid JSON: {failure.Message}", failure);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("mission", out JsonElement magic) ||
                !magic.ValueEquals(Magic))
            {
                throw new InvalidDataException($"'{path}' is not a MiVic mission file.");
            }

            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                versionElement.GetInt32() != Version)
            {
                string found = root.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32().ToString()
                    : "none";
                throw new InvalidDataException(
                    $"'{path}' is mission format version {found}, and this build writes and reads version {Version}.");
            }

            MissionDefinition mission;

            try
            {
                mission = root.GetProperty("body").Deserialize<MissionDefinition>(Options)
                    ?? throw new InvalidDataException($"'{path}' carries no mission.");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException($"'{path}' carries a mission this build cannot read: {failure.Message}", failure);
            }

            Validate(mission, path);

            return mission;
        }
    }

    /// <summary>Writes a mission to a file, replacing any existing one.</summary>
    public static void Save(MissionDefinition mission, string path)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var envelope = new MissionEnvelope(Magic, Version, mission);

        File.WriteAllText(path, JsonSerializer.Serialize(envelope, Options));
    }

    /// <summary>The validator's verdict, in its own words, at the place the mission is authored.</summary>
    internal static void Validate(MissionDefinition mission, string path)
    {
        if (string.IsNullOrWhiteSpace(mission.Id))
        {
            throw new InvalidDataException($"'{path}' carries a mission with no id.");
        }

        IReadOnlyList<string> problems = TriggerSystem.Validate(mission);

        if (problems.Count > 0)
        {
            var message = new System.Text.StringBuilder(
                $"'{path}' ({mission.Id}) is a mission this build refuses, with {problems.Count} problems:");

            foreach (string problem in problems)
            {
                message.Append("\n  - ").Append(problem);
            }

            throw new InvalidDataException(message.ToString());
        }
    }

    /// <summary>The envelope the body sits in: what the file is, and which version of it.</summary>
    private sealed record MissionEnvelope(string Mission, int Version, MissionDefinition Body)
    {
        [JsonPropertyName("id")] public string Id => Body.Id;
    }

    /// <summary>
    /// Reads and writes the match declaration: who is playing, as the roster's own
    /// <c>Declare</c> builds it. A mission without one in the file takes the definition's
    /// default, which is the standard skirmish, so the roster key is optional the way every
    /// other defaulted field is.
    /// </summary>
    private sealed class MatchRosterConverter : JsonConverter<MatchRoster>
    {
        public override MatchRoster Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A match declaration is an object with a teams list.");
            }

            List<MatchTeam> teams = [];

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!;

                    if (name == "teams")
                    {
                        reader.Read();

                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw new JsonException("The match's teams are an array.");
                        }

                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            teams.Add(JsonSerializer.Deserialize<MatchTeam>(ref reader, options));
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }

            return MatchRoster.Declare([.. teams]);
        }

        public override void Write(Utf8JsonWriter writer, MatchRoster value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("teams");
            writer.WriteStartArray();

            Span<int> playing = stackalloc int[SimConstants.TeamCount];
            int count = value.TeamsInPlayInto(playing);

            for (int i = 0; i < count; i++)
            {
                int team = playing[i];
                writer.WriteStartObject();
                writer.WriteNumber("team", team);
                writer.WriteString("faction", value.FactionOf(team).ToString());
                writer.WriteNumber("side", value.SideOf(team));
                writer.WriteBoolean("judged", value.IsJudged(team));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    /// <summary>Reads and writes a position as a three-number array: [x, y, z] in millimetres.</summary>
    private sealed class WorldPosConverter : JsonConverter<WorldPos>
    {
        public override WorldPos Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("A position is an array of three numbers.");
            }

            int[] values = new int[3];

            for (int i = 0; i < 3; i++)
            {
                reader.Read();

                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
                {
                    throw new JsonException("A position is an array of three whole numbers.");
                }

                values[i] = value;
            }

            reader.Read();

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("A position is an array of three numbers.");
            }

            return new WorldPos(values[0], values[1], values[2]);
        }

        public override void Write(Utf8JsonWriter writer, WorldPos value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }
}
