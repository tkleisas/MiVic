using System.Text.Json;
using MiVic.Core.Numerics;
using System.Text.Json.Serialization;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// A map on disk: the seed, the list of edits over the ground that seed generates, and the
/// mission the ground is shaped for. The companion of <see cref="MissionFile"/>, and its
/// superset: a map file carries everything a mission file carries, with the ground before
/// it, because that is the order the world resolves in.
/// <para>
/// <b>The loader is the environment, not a second opinion.</b> The height edits are applied,
/// the derived passes are re-run from the edited ground — that is where the guarantees live —
/// and then the placements are asked the same questions the game asks a player's
/// construction: <c>CanPlaceStructure</c> and the site rules, on the edited ground, with the
/// reason the refusal gives the author being the sentence a player would be shown. An edit
/// that invalidates the author's own placement is met at load, where the author is looking.
/// </para>
/// <para>
/// The re-derivation is the honest default the section argued for: the guarantees live in
/// the derived passes, so the passes are re-run and what they produce is the ground the
/// battle is fought on. What the loader cannot yet do is the part an <em>interactive</em>
/// editor owes its author — showing which placements an edit just invalidated and why —
/// because there is no editor yet. What exists today is the file, the application, and the
/// refusal.
/// </para>
/// </summary>
public static class MapFile
{
    /// <summary>File magic, as the envelope spells it.</summary>
    public const string Magic = "MiVicMap";

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
    /// Loads a map from a file. The mission it carries is asked the same questions a
    /// campaign mission is asked, and then the placements are asked the placement rules on
    /// the edited ground — because a placement that is legal on the ground the seed
    /// generates may not be legal on the ground the author shaped.
    /// </summary>
    public static MapDefinition Load(string path)
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

            if (!root.TryGetProperty("map", out JsonElement magic) || !magic.ValueEquals(Magic))
            {
                throw new InvalidDataException($"'{path}' is not a MiVic map file.");
            }

            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                versionElement.GetInt32() != Version)
            {
                string found = root.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32().ToString()
                    : "none";
                throw new InvalidDataException(
                    $"'{path}' is map format version {found}, and this build writes and reads version {Version}.");
            }

            MapDefinition map;

            try
            {
                map = root.GetProperty("body").Deserialize<MapDefinition>(Options)
                    ?? throw new InvalidDataException($"'{path}' carries no map.");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException($"'{path}' carries a map this build cannot read: {failure.Message}", failure);
            }

            Validate(map, path);

            return map;
        }
    }

    /// <summary>Writes a map to a file, replacing any existing one.</summary>
    public static void Save(MapDefinition map, string path)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var envelope = new MapEnvelope(Magic, Version, map);

        File.WriteAllText(path, JsonSerializer.Serialize(envelope, Options));
    }

    private static void Validate(MapDefinition map, string path)
    {
        if (map.Mission is null)
        {
            throw new InvalidDataException($"'{path}' carries no mission.");
        }

        MissionFile.Validate(map.EffectiveMission, path);
    }

    /// <summary>The envelope the body sits in.</summary>
    private sealed record MapEnvelope(string Map, int Version, MapDefinition Body);

    /// <summary>Reused from the mission format: the match declaration and the position array.</summary>
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

    /// <summary>Reads and writes the match declaration, as <see cref="MissionFile"/> does.</summary>
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
}
