using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using MiVic.Game.Data;

namespace MiVic.Game.Probe;

/// <summary>
/// Names for the enums a probe prints.
/// <para>
/// A transcript says <c>Tank (Άρμα)</c> rather than <c>2</c>, and <c>south (2)</c> rather
/// than <c>2</c>, because the reader has to recognise the answer: the numbers are stable
/// identifiers and the names are what the game calls them, and a probe that printed only
/// the number would be a probe whose answers need a second lookup table to read.
/// </para>
/// </summary>
public static class ProbeLabels
{
    /// <summary>A surface's English and Greek names, because Core's table has no English one.</summary>
    public static string Surface(TerrainType type) => $"{type} ({SurfaceGreek(type)})";

    /// <summary>
    /// A surface's player-facing name.
    /// <para>
    /// Core names every surface but woodland, and Core is not this tool's to change: a
    /// probe that printed "Άγνωστο" for the one surface the forest fixture exists to show
    /// would be lying about the answer to its own question.
    /// </para>
    /// </summary>
    public static string SurfaceGreek(TerrainType type)
        => type == TerrainType.Forest ? "Δάσος" : TerrainLayer.GreekName(type);

    /// <summary>
    /// The direction ground falls towards, as a compass point.
    /// <para>
    /// The order is <see cref="TerrainShape"/>'s own: 0 is +X, which is east, and each
    /// step turns 45° towards +Z, which is south. Spelled out here rather than derived,
    /// because deriving it would be a second definition of the convention.
    /// </para>
    /// </summary>
    public static string Aspect(int aspect) => aspect switch
    {
        0 => "east",
        1 => "south-east",
        2 => "south",
        3 => "south-west",
        4 => "west",
        5 => "north-west",
        6 => "north",
        7 => "north-east",
        _ => "flat",
    };

    /// <summary>What kind of place a cell sits in, from <see cref="TerrainShape"/>'s ids.</summary>
    public static string Landform(int landform) => landform switch
    {
        1 => "slope",
        2 => "ridge",
        3 => "valley",
        4 => "pass",
        5 => "plateau",
        6 => "basin",
        7 => "shelf",
        _ => "plain",
    };

    /// <summary>The damage flags a cell is carrying, or "none".</summary>
    public static string Flags(TerrainAttributes attributes)
    {
        var names = new List<string>();

        if (attributes.IsBurning)
        {
            names.Add("burning");
        }

        if (attributes.IsBurned)
        {
            names.Add("burned");
        }

        if (attributes.IsCratered)
        {
            names.Add("cratered");
        }

        if (attributes.IsRubble)
        {
            names.Add("rubble");
        }

        if (attributes.IsFlooded)
        {
            names.Add("flooded");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>A role's name and the label the build panel gives it.</summary>
    public static string KindName(UnitKind kind) => $"{kind} ({FactionPalette.UnitLabel(kind)})";

    /// <summary>
    /// Why an entity senses nothing, as the clause a transcript reads.
    /// <para>
    /// <see cref="VisionSystem.SensorOf"/> answers with a <see cref="SensorRefusal"/> and a
    /// transcript needs a sentence, which is the whole of what this adds. It is not a second copy of
    /// the rule: the reasons are the enum's own cases, and a probe that decided for itself that a
    /// dark radar is dark would be the interface inventing a brown-out again.
    /// </para>
    /// </summary>
    public static string Sensor(SensorRefusal refusal) => refusal switch
    {
        SensorRefusal.NoEntity => "the slot holds nothing alive",
        SensorRefusal.NoTeam => "it is on no team the simulation has",
        SensorRefusal.UnderConstruction => "a building site is not watching yet",
        SensorRefusal.RadarDark => "the grid cannot run it, so the set is dark",
        _ => "it is watching",
    };

    /// <summary>Reads a faction from a script argument: the name, or the team it fights for.</summary>
    public static bool TryFaction(string text, out Faction faction)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "soviet" or "σοβιετικοι" or "σοβιετικοί" or "1":
                faction = Faction.Soviet;
                return true;
            case "chinese" or "κινεζοι" or "κινέζοι" or "2":
                faction = Faction.Chinese;
                return true;
            case "western" or "δυτικοι" or "δυτικοί" or "3":
                faction = Faction.Western;
                return true;
            default:
                faction = Faction.None;
                return false;
        }
    }

    /// <summary>Reads a role from a script argument, by enum name or by Greek label.</summary>
    public static bool TryKind(string text, out UnitKind kind)
    {
        string wanted = text.Trim();

        // Not a number: Enum.TryParse reads "2" as the second role, and a script that
        // wrote a team number where a role belongs would then quietly count tanks.
        if (int.TryParse(wanted, out _))
        {
            kind = UnitKind.None;
            return false;
        }

        if (Enum.TryParse(wanted, ignoreCase: true, out kind) && kind != UnitKind.None)
        {
            return true;
        }

        foreach (UnitKind candidate in Enum.GetValues<UnitKind>())
        {
            if (string.Equals(FactionPalette.UnitLabel(candidate), wanted, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }
}
