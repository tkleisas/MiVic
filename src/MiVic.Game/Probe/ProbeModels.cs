using MiVic.Core.Sim;
using MiVic.Game.Data;
using MiVic.Game.Rendering.Gltf;

namespace MiVic.Game.Probe;

/// <summary>Which model file a faction's role resolves to, and who else resolves to it.</summary>
public static class ProbeModels
{
    /// <summary>The file and import options a role is configured with.</summary>
    public static bool TryFind(
        Faction faction,
        UnitKind kind,
        out string relativePath,
        out ModelImportOptions options)
    {
        foreach ((Faction candidate, UnitKind role, string relative, ModelImportOptions import) in ModelCatalog.Enumerate())
        {
            if (candidate == faction && role == kind)
            {
                relativePath = relative;
                options = import;
                return true;
            }
        }

        relativePath = string.Empty;
        options = default;
        return false;
    }

    /// <summary>
    /// Every other role configured with the same file.
    /// <para>
    /// The question this exists for: two entities sharing a model. Whether that is a
    /// mistake depends on which two, so the probe reports the fact and lets the reader
    /// decide; what it must not do is make the reader compare thirty-four rows of a table
    /// by eye to find out.
    /// </para>
    /// </summary>
    public static List<(Faction Faction, UnitKind Kind)> Sharers(Faction faction, UnitKind kind, string relativePath)
    {
        var sharers = new List<(Faction, UnitKind)>();

        foreach ((Faction candidate, UnitKind role, string relative, _) in ModelCatalog.Enumerate())
        {
            if (relative == relativePath && (candidate != faction || role != kind))
            {
                sharers.Add((candidate, role));
            }
        }

        return sharers;
    }

    /// <summary>Where a model file lives, relative to the executable the client runs from.</summary>
    public static string FullPath(string relativePath)
        => Path.Combine(AppContext.BaseDirectory, ModelCatalog.ModelRoot, relativePath);
}
