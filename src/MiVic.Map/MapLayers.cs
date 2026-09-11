namespace MiVic.Map;

/// <summary>
/// The layers a map can carry. Ordering is the writer's business; this is only membership.
/// </summary>
[Flags]
public enum MapLayers
{
    /// <summary>Nothing but the background: the honest way to ask what a layer is contributing.</summary>
    None = 0,

    /// <summary>The surface grid in its own colours.</summary>
    Terrain = 1 << 0,

    /// <summary>Vision and radar coverage as translucent discs.</summary>
    Coverage = 1 << 1,

    /// <summary>Objective circles and deadlines, and which triggers have fired.</summary>
    Script = 1 << 2,

    /// <summary>The ground each unit has actually covered, sampled over time.</summary>
    Trails = 1 << 3,

    /// <summary>The route the pathfinder actually returned — where each unit is trying to go.</summary>
    Paths = 1 << 4,

    /// <summary>Units as dots with a heading arrow, structures as squares.</summary>
    Units = 1 << 5,

    /// <summary>Who is stuck, who is idle, who is fighting — the state ring on each mark.</summary>
    Stuck = 1 << 6,

    /// <summary>Shots, hits and deaths as marks.</summary>
    Events = 1 << 7,

    /// <summary>Border, scale bar, orientation and legend.</summary>
    Frame = 1 << 8,

    /// <summary>Everything. What a probe draws when the script does not say.</summary>
    All = Terrain | Coverage | Script | Trails | Paths | Units | Stuck | Events | Frame,
}

/// <summary>Reads the layer list a script wrote, and says what it made of it.</summary>
public static class MapLayerText
{
    /// <summary>
    /// Every layer's name against its bit, in the order a legend should list them. One table
    /// rather than a switch in three places, because the parse, the description and the legend
    /// disagreeing about what a layer is called is exactly the kind of bug this tool is for.
    /// </summary>
    public static readonly (string Name, MapLayers Layer)[] Table =
    [
        ("terrain", MapLayers.Terrain),
        ("coverage", MapLayers.Coverage),
        ("script", MapLayers.Script),
        ("trails", MapLayers.Trails),
        ("paths", MapLayers.Paths),
        ("units", MapLayers.Units),
        ("stuck", MapLayers.Stuck),
        ("events", MapLayers.Events),
        ("frame", MapLayers.Frame),
    ];

    /// <summary>Every layer's name, for an error message that has to list them.</summary>
    public static readonly string[] Names = [.. Table.Select(entry => entry.Name)];

    /// <summary>
    /// Parses <c>all</c>, <c>none</c> or a comma-separated list. An unknown name is refused rather
    /// than ignored: a script that asked for <c>trail</c> and silently drew no trails would look
    /// exactly like a map with nothing to draw.
    /// </summary>
    public static MapLayers Parse(string text)
    {
        MapLayers layers = MapLayers.None;
        bool any = false;

        foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            any = true;

            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                layers |= MapLayers.All;
                continue;
            }

            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool known = false;

            foreach ((string name, MapLayers layer) in Table)
            {
                if (name.Equals(raw, StringComparison.OrdinalIgnoreCase))
                {
                    layers |= layer;
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new ArgumentException($"'{raw}' is not a layer — {string.Join(", ", Names)}, or all/none");
            }
        }

        if (!any)
        {
            throw new ArgumentException($"no layers given — {string.Join(", ", Names)}, or all/none");
        }

        return layers;
    }

    /// <summary>The names in a mask, for a transcript that has to say what it drew.</summary>
    public static string Describe(MapLayers layers)
    {
        var parts = new List<string>(Table.Length);

        foreach ((string name, MapLayers layer) in Table)
        {
            if ((layers & layer) != 0)
            {
                parts.Add(name);
            }
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }
}
