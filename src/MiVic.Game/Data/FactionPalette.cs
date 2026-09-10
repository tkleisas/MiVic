using Microsoft.Xna.Framework;
using MiVic.Core.Sim;

namespace MiVic.Game.Data;

/// <summary>
/// Presentation colours. The three powers are deliberately readable at a glance
/// from any camera angle: red for Σοβιετικοί, amber for Κινέζοι, blue for
/// Δυτικοί.
/// </summary>
public static class FactionPalette
{
    /// <summary>Ground colour.</summary>
    public static readonly Color Ground = new(44, 52, 42);

    /// <summary>Grid line colour.</summary>
    public static readonly Color GridLine = new(66, 76, 62);

    /// <summary>Primary colour of a faction.</summary>
    public static Color Primary(Faction faction) => faction switch
    {
        Faction.Soviet => new Color(200, 44, 36),
        Faction.Chinese => new Color(224, 174, 44),
        Faction.Western => new Color(50, 108, 210),
        _ => new Color(140, 140, 140),
    };

    /// <summary>Darkened variant, used so unit roles read differently at a glance.</summary>
    public static Color Shade(Color color, float amount) => new(
        (int)(color.R * amount),
        (int)(color.G * amount),
        (int)(color.B * amount));

    /// <summary>Colour for a specific unit role within a faction.</summary>
    public static Color ForUnit(Faction faction, UnitKind kind)
    {
        Color primary = Primary(faction);

        return kind switch
        {
            UnitKind.Infantry => Shade(primary, 1.15f),
            UnitKind.Tank => primary,
            UnitKind.Artillery => Shade(primary, 0.8f),
            UnitKind.RocketArtillery => Shade(primary, 0.72f),
            UnitKind.Commissar => Shade(primary, 1.05f),
            UnitKind.AntiAir => Shade(primary, 0.9f),
            UnitKind.Aircraft => Shade(primary, 1.3f),
            UnitKind.Drone => Shade(primary, 1.2f),
            UnitKind.RobotInfantry => Shade(primary, 1.0f),
            UnitKind.Mercenary => Shade(primary, 1.25f),
            UnitKind.StealthRecon => Shade(primary, 0.5f),
            UnitKind.ElectroPrototype => Shade(primary, 1.45f),
            UnitKind.CommandCentre => Shade(primary, 0.65f),
            UnitKind.NuclearPlant => Shade(primary, 0.55f),

            // The emplacements: a defensive structure is read against the ground rather than
            // against the sky, so both sit a little darker than the buildings they protect —
            // and the anti-aircraft mount a shade lighter than the gun, which is the same
            // distinction the two make in the catalogue.
            UnitKind.GunEmplacement => Shade(primary, 0.78f),
            UnitKind.AntiAirEmplacement => Shade(primary, 0.88f),
            _ => primary,
        };
    }

    /// <summary>Player-facing Greek label for a unit role, from the catalogue's own names.</summary>
    public static string UnitLabel(UnitKind kind) => UnitCatalog.GreekName(kind);
}
