using MiVic.Game.Data;
using MiVic.Game.Rendering;
using MiVic.Map;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Probe;

/// <summary>
/// The bridge between the client's colours and the map's.
/// <para>
/// The map writer deliberately owns no palette: it takes the tables it is given, so a picture of
/// a match is drawn in the same reds, ambers and blues the player is looking at rather than in a
/// second set invented by a diagnostic tool. This is the one place the two meet, and it is three
/// lines long on purpose — the shorter it is, the less room there is for the map and the renderer
/// to disagree about what a Σοβιετικοί tank looks like.
/// </para>
/// </summary>
internal static class ProbeMapColours
{
    /// <summary>The client's own colours, as the map writer wants them.</summary>
    public static MapPalette Palette { get; } = MapPalette.Of(
        surface => TerrainMeshBuilder.SurfaceColor(surface).ToMapRgb(),
        (faction, kind) => FactionPalette.ForUnit(faction, kind).ToMapRgb());

    /// <summary>Three channels, dropped from a colour that also carries an alpha nobody asked for.</summary>
    public static MapRgb ToMapRgb(this Color colour) => new(colour.R, colour.G, colour.B);
}
