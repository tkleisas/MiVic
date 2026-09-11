using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Map;

/// <summary>
/// What the map looks like: two tables the client owns, and the marks the writer owns.
/// <para>
/// The two tables are delegates rather than values because the client already has them —
/// <c>FactionPalette</c> for the powers and <c>TerrainMeshBuilder.SurfaceColor</c> for the
/// ground — and a map that invented its own copy would be a second answer to "what colour
/// is a Σοβιετικοί tank", free to disagree with the one on screen. The rest of the colours
/// here belong to this tool: a route, a trail, a warning and a legend are marks on a
/// diagnostic drawing, and nothing in the game has an opinion about them.
/// </para>
/// </summary>
public sealed record MapPalette
{
    /// <summary>Colour of a ground surface, which the client bakes into its terrain mesh.</summary>
    public required Func<TerrainType, MapRgb> Surface { get; init; }

    /// <summary>Colour of an entity, which the client tints its models with.</summary>
    public required Func<Faction, UnitKind, MapRgb> Unit { get; init; }

    /// <summary>Behind everything, where the map's own ground does not reach.</summary>
    public MapRgb Background { get; init; } = new(14, 16, 14);

    /// <summary>The border, the scale bar and the legend's furniture.</summary>
    public MapRgb Frame { get; init; } = new(96, 104, 96);

    /// <summary>Legend and title text.</summary>
    public MapRgb Text { get; init; } = new(228, 232, 228);

    /// <summary>
    /// The mark for a unit that is holding a route and not moving along it — the reading this
    /// whole layer exists for, so it is the one colour in the palette chosen to be impossible
    /// to miss against every surface the game generates.
    /// </summary>
    public MapRgb Warning { get; init; } = new(255, 72, 200);

    /// <summary>The second warning: a unit that holds a goal and no route at all.</summary>
    public MapRgb Stall { get; init; } = new(255, 208, 48);

    /// <summary>Rings, crosses and the outline every mark carries so it reads over any ground.</summary>
    public MapRgb Marker { get; init; } = new(12, 12, 12);

    /// <summary>Where something was hit. Not a faction colour: a hit belongs to the ground it landed on.</summary>
    public MapRgb Strike { get; init; } = new(255, 246, 214);

    /// <summary>An objective that is neither satisfied nor failed, which is where most of them are.</summary>
    public MapRgb ObjectiveOpen { get; init; } = new(214, 224, 236);

    /// <summary>An objective that has been satisfied.</summary>
    public MapRgb ObjectiveDone { get; init; } = new(104, 220, 128);

    /// <summary>An objective that has been failed, which on a mission is the end of it.</summary>
    public MapRgb ObjectiveFailed { get; init; } = new(232, 80, 72);

    /// <summary>Opacity of a coverage disc, out of 255. Deliberately faint: they overlap.</summary>
    public byte CoverageAlpha { get; init; } = 30;

    /// <summary>Opacity of the line joining a unit's sampled positions.</summary>
    public byte TrailAlpha { get; init; } = 150;

    /// <summary>Opacity of a sampled position itself, which is what makes pace legible.</summary>
    public byte TrailTickAlpha { get; init; } = 220;

    /// <summary>Opacity of the intended route. Solid where the trail is faint, so the pair reads.</summary>
    public byte PathAlpha { get; init; } = 235;

    /// <summary>Opacity of an objective's circle.</summary>
    public byte AreaAlpha { get; init; } = 150;

    /// <summary>Opacity of an event mark.</summary>
    public byte EventAlpha { get; init; } = 190;

    /// <summary>
    /// The pair of tables and nothing else: what a caller with its own colours supplies, with
    /// every diagnostic mark left at the writer's defaults.
    /// </summary>
    public static MapPalette Of(
        Func<TerrainType, MapRgb> surface,
        Func<Faction, UnitKind, MapRgb> unit) => new()
        {
            Surface = surface,
            Unit = unit,
        };
}
