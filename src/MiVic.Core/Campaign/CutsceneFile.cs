using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiVic.Core.Campaign;

/// <summary>
/// A cutscene on disk: the set, the cast, the camera and the script.
/// <para>
/// The companion of <see cref="MissionFile"/> and <see cref="MapFile"/>, and deliberately the
/// same shape: JSON, indented, enums spelled as their names, versioned by an envelope that
/// says what the file is. A scene is content, so it is a file an author can diff — which is
/// the whole reason the campaign's missions became files in the first place.
/// </para>
/// <para>
/// <b>The loader refuses what a director could not play.</b> A scene with no lines, a camera
/// whose keys go backwards, a line spoken by somebody who is not standing in the room, a
/// figure named after a mission that does not exist: each is a sentence the loader returns
/// where the author is looking, rather than a black screen at the front of a mission.
/// </para>
/// </summary>
public static class CutsceneFile
{
    /// <summary>File magic, as the envelope spells it.</summary>
    public const string Magic = "MiVicCutscene";

    /// <summary>Envelope version. Bump whenever the layout changes.</summary>
    public const int Version = 1;

    /// <summary>The extension a cutscene file carries, which is what the catalogue scans for.</summary>
    public const string Extension = ".cutscene.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads one scene, refusing anything a director could not play.</summary>
    public static CutsceneDefinition Load(string path)
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

            if (!root.TryGetProperty("cutscene", out JsonElement magic) || !magic.ValueEquals(Magic))
            {
                throw new InvalidDataException($"'{path}' is not a MiVic cutscene file.");
            }

            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out int version) ||
                version != Version)
            {
                string found = root.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number
                    ? v.ToString()
                    : "none";

                throw new InvalidDataException(
                    $"'{path}' is cutscene format version {found}, and this build writes and reads version {Version}.");
            }

            CutsceneDefinition scene;

            try
            {
                scene = root.GetProperty("body").Deserialize<CutsceneDefinition>(Options)
                    ?? throw new InvalidDataException($"'{path}' carries no cutscene.");
            }
            catch (JsonException failure)
            {
                throw new InvalidDataException($"'{path}' carries a cutscene this build cannot read: {failure.Message}", failure);
            }
            catch (KeyNotFoundException failure)
            {
                throw new InvalidDataException($"'{path}' carries no cutscene body.", failure);
            }

            IReadOnlyList<string> problems = Problems(scene);

            if (problems.Count > 0)
            {
                throw new InvalidDataException(
                    $"'{path}' ({scene.Id}) is a cutscene this build refuses:{Environment.NewLine}  - " +
                    string.Join($"{Environment.NewLine}  - ", problems));
            }

            return scene;
        }
    }

    /// <summary>Writes a scene to a file, replacing any existing one.</summary>
    public static void Save(CutsceneDefinition cutscene, string path)
    {
        ArgumentNullException.ThrowIfNull(cutscene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var envelope = new CutsceneEnvelope(Magic, Version, cutscene);

        File.WriteAllText(path, JsonSerializer.Serialize(envelope, Options));
    }

    /// <summary>
    /// Loads every scene in a directory, sorted by id.
    /// <para>
    /// A directory that does not exist is no scenes rather than an error, the way a missing
    /// progress file is a fresh campaign: a build that has no cutscenes is a build whose
    /// missions open on a briefing panel, not a build that will not start. A file that is
    /// there and wrong is still an error, and two files claiming one id are refused because
    /// which of them a mission played would otherwise depend on the order the disk listed them.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CutsceneDefinition> LoadAll(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        List<CutsceneDefinition> scenes = [];

        if (!Directory.Exists(directory))
        {
            return scenes;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            scenes.Add(Load(path));
        }

        scenes.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));

        for (int i = 1; i < scenes.Count; i++)
        {
            if (string.Equals(scenes[i].Id, scenes[i - 1].Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Two cutscene files both claim the id '{scenes[i].Id}'; which one a mission plays would depend on the order the disk listed them.");
            }
        }

        return scenes;
    }

    /// <summary>
    /// The scene that briefs or debriefs a mission, or null when none does. The lowest id
    /// wins when more than one matches, so the answer does not depend on file order.
    /// </summary>
    public static CutsceneDefinition? ForMission(
        IReadOnlyList<CutsceneDefinition> scenes,
        string missionId,
        CutsceneKind kind)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentException.ThrowIfNullOrWhiteSpace(missionId);

        CutsceneDefinition? found = null;

        foreach (CutsceneDefinition scene in scenes)
        {
            if (scene.Kind != kind || !string.Equals(scene.MissionId, missionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is null || string.CompareOrdinal(scene.Id, found.Id) < 0)
            {
                found = scene;
            }
        }

        return found;
    }

    /// <summary>
    /// Everything wrong with a scene, in the loader's own sentences, or an empty list. Shared
    /// with the editor and the probe so a scene that cannot be played is found where it is
    /// authored rather than when a mission opens on it.
    /// </summary>
    public static IReadOnlyList<string> Problems(CutsceneDefinition scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(scene.Id))
        {
            problems.Add("the scene has no id.");
        }

        if (string.IsNullOrWhiteSpace(scene.Set))
        {
            problems.Add($"scene '{scene.Id}' names no set to stand in.");
        }

        if (string.IsNullOrWhiteSpace(scene.Music))
        {
            problems.Add($"scene '{scene.Id}' names no music; a silent scene is a scene with a track chosen anyway.");
        }

        if (scene.MissionId is { Length: > 0 } missionId && MissionCatalog.Find(missionId) is null)
        {
            problems.Add($"scene '{scene.Id}' names mission '{missionId}', which this build does not have.");
        }

        if (!Enum.IsDefined(scene.Faction))
        {
            problems.Add($"scene '{scene.Id}' is scored for {(byte)scene.Faction}, which is not a faction.");
        }

        CheckCamera(scene, problems);
        CheckFigures(scene, problems);
        CheckLines(scene, problems);

        return problems;
    }

    private static void CheckCamera(CutsceneDefinition scene, List<string> problems)
    {
        if (scene.Camera.Count < 2)
        {
            // One key would be a locked-off shot, which is legal but is also what a still is
            // for. Two is the smallest thing that is a scene rather than a picture.
            problems.Add($"scene '{scene.Id}' has {scene.Camera.Count} camera key(s); a scene needs at least two, the first at zero.");
            return;
        }

        if (scene.Camera[0].Milliseconds != 0)
        {
            problems.Add($"scene '{scene.Id}' starts its camera at {scene.Camera[0].Milliseconds} ms; the first key is the shot at zero.");
        }

        for (int i = 0; i < scene.Camera.Count; i++)
        {
            CutsceneCameraKey key = scene.Camera[i];

            if (key.Milliseconds < 0)
            {
                problems.Add($"scene '{scene.Id}' has a camera key at a negative time ({key.Milliseconds} ms).");
            }

            if (i > 0 && key.Milliseconds < scene.Camera[i - 1].Milliseconds)
            {
                problems.Add(
                    $"scene '{scene.Id}' puts camera key {i} at {key.Milliseconds} ms, before key {i - 1} at {scene.Camera[i - 1].Milliseconds} ms.");
            }
        }
    }

    private static void CheckFigures(CutsceneDefinition scene, List<string> problems)
    {
        if (scene.Figures.Count == 0)
        {
            problems.Add($"scene '{scene.Id}' has nobody in it.");
        }

        for (int i = 0; i < scene.Figures.Count; i++)
        {
            CutsceneFigure figure = scene.Figures[i];

            if (string.IsNullOrWhiteSpace(figure.Asset))
            {
                problems.Add($"scene '{scene.Id}' has a figure {i} with no asset name.");
            }

            if (figure.FacingDegrees is < 0 or > 359)
            {
                problems.Add($"scene '{scene.Id}' turns figure {i} to {figure.FacingDegrees}°, which is not a heading.");
            }

            // Null is a figure of rigid parts and is the ordinary case. A clip is matched
            // against the asset's own clip names, so one that is empty or padded would never
            // match anything and the figure would quietly stand in its bind pose — a T-pose
            // on screen, and no error anywhere. This format refuses what cannot be played.
            if (figure.Clip is { } clip && (clip.Length == 0 || clip != clip.Trim()))
            {
                problems.Add(
                    $"scene '{scene.Id}' gives figure {i} a clip name that is empty or padded with "
                    + "spaces; a clip is matched by its exact name.");
            }
        }
    }

    private static void CheckLines(CutsceneDefinition scene, List<string> problems)
    {
        if (scene.Lines.Count == 0)
        {
            problems.Add($"scene '{scene.Id}' has no lines; a scene nobody says anything in is a camera move.");
        }

        for (int i = 0; i < scene.Lines.Count; i++)
        {
            CutsceneLine line = scene.Lines[i];

            if (string.IsNullOrWhiteSpace(line.GreekText))
            {
                problems.Add($"scene '{scene.Id}' line {i} is empty.");
            }

            if (line.Milliseconds <= 0)
            {
                problems.Add($"scene '{scene.Id}' line {i} is held for {line.Milliseconds} ms; a line needs time to read.");
            }

            if (line.Speaker is { Length: > 0 } speaker && !scene.HasFigure(speaker))
            {
                problems.Add(
                    $"scene '{scene.Id}' line {i} is spoken by '{speaker}', who is not standing in the scene.");
            }
        }
    }

    /// <summary>The envelope the body sits in.</summary>
    private sealed record CutsceneEnvelope(string Cutscene, int Version, CutsceneDefinition Body);
}
