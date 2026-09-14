using MiVic.Core.Replay;

namespace MiVic.Game.Data;

/// <summary>
/// Where a player's campaign lives: the progress file and the saved matches.
/// <para>
/// Progress has to survive the process, which means a file, which means a place.
/// The place is the user's own profile rather than the game's directory, because a
/// player's campaign is theirs — reinstalling or updating the build must not take
/// it — and <c>--profile</c> overrides it, because a test or a probe must never
/// write into a real player's campaign.
/// </para>
/// </summary>
public static class MiVicPaths
{
    private static string? _profile;

    /// <summary>
    /// The profile directory: progress and saves beneath it. Set once from the
    /// launch options before anything reads it; the default is the platform's own
    /// per-user application data, which is where an operating system says
    /// per-user files go.
    /// </summary>
    public static string Profile
    {
        get
        {
            if (_profile is null)
            {
                string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                _profile = Path.Combine(baseDirectory, "MiVic");
            }

            return _profile;
        }
    }

    /// <summary>Sets the profile directory, before anything has read it.</summary>
    public static void UseProfile(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _profile = directory;
        Directory.CreateDirectory(_profile);
    }

    /// <summary>The campaign progress file.</summary>
    public static string ProgressFile => Path.Combine(Profile, "campaign.txt");

    /// <summary>The directory saved matches live in, created when asked for.</summary>
    public static string SavesDirectory
    {
        get
        {
            string directory = Path.Combine(Profile, "saves");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>Every saved match, newest first. A directory that does not exist yet is an empty list.</summary>
    public static IReadOnlyList<SaveEntry> SavedMatches()
    {
        var entries = new List<SaveEntry>();

        if (!Directory.Exists(SavesDirectory))
        {
            return entries;
        }

        foreach (string path in Directory.EnumerateFiles(SavesDirectory, "*.mvsav"))
        {
            try
            {
                ReplayFile replay = ReplayFile.Load(path);
                entries.Add(new SaveEntry(
                    Path.GetFileNameWithoutExtension(path),
                    path,
                    replay.FinalTick,
                    File.GetLastWriteTime(path),
                    replay.MissionId));
            }
            catch (InvalidDataException)
            {
                // A file this build cannot read is not a save: it is skipped rather
                // than offered, because a menu entry that crashes the game on click
                // is worse than a save that is quietly absent. A corrupt save is also
                // why the entry carries the tick and mission it claims — the reader
                // has already been checked before the name reaches the list.
            }
        }

        entries.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return entries;
    }
}

/// <summary>One saved match, as the menu lists it.</summary>
/// <param name="Name">The file's own name, without the extension.</param>
/// <param name="Path">Where it lives.</param>
/// <param name="Tick">The tick it was taken on.</param>
/// <param name="SavedAt">When it was written.</param>
/// <param name="MissionId">The campaign mission it was playing, or null for a skirmish.</param>
public readonly record struct SaveEntry(
    string Name,
    string Path,
    long Tick,
    DateTime SavedAt,
    string? MissionId);
