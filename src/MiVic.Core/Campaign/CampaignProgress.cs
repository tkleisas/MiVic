namespace MiVic.Core.Campaign;

/// <summary>
/// What a player has done in the campaign: which missions they have won. It is the
/// answer to the front end's first real question — "what is campaign progress?" — and
/// the answer is a file, which means a format, which means a version.
/// <para>
/// The format is deliberately plain, like the replay's: line-oriented, readable in a
/// text editor, and versioned, because the first release a progress file lies about
/// its own shape is the first release a player's campaign silently resets. Missing
/// file means nothing played yet — the honest reading, not an error. A file whose
/// header disagrees with this build is a refusal rather than a guess, because a
/// progress file that is half-read would hand a player missions they have not earned
/// or take ones they have.
/// </para>
/// <para>
/// Winning has to be remembered outside the process, so missions are identified by
/// their id — the same id the catalog, the probe and the replay all name them by —
/// and nothing else about a mission is remembered: the campaign is a list of missions
/// won, and the titles are the catalog's business.
/// </para>
/// </summary>
public sealed class CampaignProgress
{
    /// <summary>File magic as ASCII, for a file a reader can recognise in a text editor.</summary>
    public const string Magic = "MiVicCampaign";

    /// <summary>Format version. Bump whenever the layout changes.</summary>
    public const int Version = 1;

    private readonly List<string> _won = [];

    /// <summary>Every mission won, in the order it was won.</summary>
    public IReadOnlyList<string> WonMissions => _won;

    /// <summary>Reads a progress file. A file that does not exist is a fresh campaign.</summary>
    /// <exception cref="InvalidDataException">When the file exists and is not one this build understands.</exception>
    public static CampaignProgress Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new CampaignProgress();
        }

        string[] lines = File.ReadAllLines(path);

        if (lines.Length < 2 || !lines[0].StartsWith(Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{path}' is not a MiVic campaign progress file.");
        }

        string[] header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (header.Length < 2 || !int.TryParse(header[1], out int version) || version != Version)
        {
            throw new InvalidDataException(
                $"'{path}' is campaign progress version {header.ElementAtOrDefault(1)}, " +
                $"and this build writes and reads version {Version}.");
        }

        var progress = new CampaignProgress();

        foreach (string line in lines.Skip(1))
        {
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            string[] fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length == 2 && fields[0] == "won")
            {
                progress.MarkWon(fields[1]);
            }
            else
            {
                throw new InvalidDataException($"'{path}' carries a line this build does not understand: '{trimmed}'.");
            }
        }

        return progress;
    }

    /// <summary>
    /// Marks a mission won. Marking a mission won twice leaves one entry, because a
    /// campaign remembers that it happened, not that it happened twice.
    /// </summary>
    public void MarkWon(string missionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(missionId);

        if (!_won.Contains(missionId, StringComparer.Ordinal))
        {
            _won.Add(missionId);
        }
    }

    /// <summary>True when the player has won this mission, or when the campaign has none.</summary>
    public bool HasWon(string missionId)
        => !string.IsNullOrWhiteSpace(missionId) && _won.Contains(missionId, StringComparer.Ordinal);

    /// <summary>Clears every record: the start of a new campaign.</summary>
    public void Reset() => _won.Clear();

    /// <summary>The first mission of the campaign the player has not won yet, or null when it is over.</summary>
    /// <param name="campaignOrder">The campaign in order, which is the catalog's business.</param>
    public static string? NextUnwon(IEnumerable<string> campaignOrder, IReadOnlyList<string> won)
    {
        ArgumentNullException.ThrowIfNull(campaignOrder);

        foreach (string id in campaignOrder)
        {
            if (!won.Contains(id, StringComparer.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Writes the progress file, replacing any existing one.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var writer = new StreamWriter(File.Create(path));

        writer.WriteLine($"{Magic} {Version}");

        foreach (string id in _won)
        {
            writer.WriteLine($"won {id}");
        }
    }
}
