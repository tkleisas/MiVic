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

    /// <summary>
    /// Envelope version. Bump whenever the layout changes.
    /// <para>
    /// Version 2 adds the authored force: <c>units</c>, and the <c>exactForce</c> flag that
    /// makes the placements the whole starting force rather than an addition to the generated
    /// one. A version-1 file has neither, and is refused the way any other version is —
    /// <c>maps/demo-isthmus.map.json</c> was regenerated for this version, and an author's own
    /// v1 map needs its version field changed and nothing else, because both new fields are
    /// optional and default to "none" and "generated".
    /// </para>
    /// </summary>
    public const int Version = 2;

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

        // A map whose force the author wrote skips the mission's opening-world checks: those
        // interrogate the generated layout this map replaces, so asking them would be
        // answering about a battle nobody will fight. See TriggerSystem.Validate.
        MissionFile.Validate(map.EffectiveMission, path, againstOpeningWorld: !map.ExactForce);

        IReadOnlyList<string> problems = ForceProblems(map);

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"'{path}' carries a force this build refuses:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", problems));
        }
    }

    /// <summary>
    /// What is wrong with a map's placements and, when the force is exact, with its order of
    /// battle — the half of the check that needs no ground under it.
    /// <para>
    /// Exposed so the editor can ask the same question before it writes: a file the loader
    /// would refuse is a file the editor does not write, and an author who is placing a force
    /// should meet the requirement while the cursor is still on the map rather than at save.
    /// The other half — is each placement on ground it can occupy — needs the terrain
    /// generated and is asked by <see cref="Scenario.BuildMap"/>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ForceProblems(MapDefinition map)
    {
        ArgumentNullException.ThrowIfNull(map);

        List<string> problems = [];
        ValidatePlacements(map, problems);

        if (map.ExactForce)
        {
            ValidateExactForce(map, problems);
        }

        return problems;
    }

    /// <summary>
    /// The two lists are one concept split by what the placement rules ask: a role that is a
    /// building is founded, a role that moves is placed. A file that puts one in the other's
    /// list is refused rather than reinterpreted, because guessing would silently apply the
    /// wrong rule to it.
    /// </summary>
    private static void ValidatePlacements(MapDefinition map, List<string> problems)
    {
        foreach (StructurePlacement placement in map.Structures)
        {
            if (!UnitCatalog.TryGet(placement.Kind, out UnitDefinition definition))
            {
                problems.Add($"a placed structure names a role this build does not have: {placement.Kind}.");
            }
            else if (!definition.IsBuilding)
            {
                problems.Add($"{UnitCatalog.GreekName(placement.Kind)} moves, so it belongs in 'units', not 'structures'.");
            }

            CheckPlacementTeam(map, placement.Team, problems);
        }

        foreach (UnitPlacement placement in map.Units)
        {
            if (!UnitCatalog.TryGet(placement.Kind, out UnitDefinition definition))
            {
                problems.Add($"a placed unit names a role this build does not have: {placement.Kind}.");
            }
            else if (definition.IsBuilding)
            {
                problems.Add($"{UnitCatalog.GreekName(placement.Kind)} is a structure, so it belongs in 'structures', not 'units'.");
            }

            CheckPlacementTeam(map, placement.Team, problems);
        }
    }

    /// <summary>
    /// What an authored order of battle owes the match it is played as.
    /// <para>
    /// The terrain half — whether each placement stands on ground it can occupy — cannot be
    /// answered without generating the ground, so it is asked when the map is built, by
    /// <see cref="Scenario.BuildMap"/>, through the same rules a player's construction is
    /// refused by, and the editor reports those refusals live. What is asked here is the half a
    /// list of coordinates can answer on its own. The headquarters requirement is the one only
    /// an exact force can get wrong: the generator always laid one down, and a side with no
    /// command centre can neither build nor be beaten.
    /// </para>
    /// </summary>
    private static void ValidateExactForce(MapDefinition map, List<string> problems)
    {
        if (!map.HasAuthoredForce)
        {
            problems.Add("'exactForce' is set and nothing is placed: a map with no army on it is not a battle.");
            return;
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (!map.EffectiveMission.Roster.IsInPlay(team) || !map.EffectiveMission.Roster.IsJudged(team))
            {
                continue;
            }

            bool hasHeadquarters = false;

            foreach (StructurePlacement placement in map.Structures)
            {
                if (placement.Team == team && placement.Kind == UnitKind.CommandCentre)
                {
                    hasHeadquarters = true;
                    break;
                }
            }

            if (!hasHeadquarters)
            {
                problems.Add($"team {team} is judged but the map places no command centre for it.");
            }
        }
    }

    private static void CheckPlacementTeam(MapDefinition map, int team, List<string> problems)
    {
        if ((uint)team >= SimConstants.TeamCount || !map.EffectiveMission.Roster.IsInPlay(team))
        {
            problems.Add($"a placement names team {team}, which this map's match does not declare.");
        }
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
                if (value.GreekNameOf(team) != FactionProfile.For(value.FactionOf(team)).GreekName)
                {
                    writer.WriteString("greekNameOverride", value.GreekNameOf(team));
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
