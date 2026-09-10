using MiVic.Audio;
using MiVic.Core.Sim;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering.Combat;

/// <summary>How a shot travels, which is most of what tells two weapons apart.</summary>
public enum FireStyle : byte
{
    /// <summary>A rifle round: a thin bright streak, gone almost immediately.</summary>
    Bullet = 0,

    /// <summary>A tank round: a fat tracer with a hot core, flat and fast.</summary>
    Shell = 1,

    /// <summary>A howitzer shell: slower, thrown in an arc, visible in flight.</summary>
    ArcShell = 2,

    /// <summary>A rocket: leaves a smoke trail and wobbles.</summary>
    Rocket = 3,

    /// <summary>An anti-air round: very fast, very bright, bursts in the air.</summary>
    Flak = 4,

    /// <summary>A guided missile: a smoke trail and a flat, fast track.</summary>
    Missile = 5,

    /// <summary>An electric arc: not a projectile at all, a bolt drawn at once.</summary>
    Bolt = 6,
}

/// <summary>What a shot does where it lands.</summary>
public enum FireImpact : byte
{
    /// <summary>A spark and a puff: small arms, or a round on armour.</summary>
    Sparks = 0,

    /// <summary>A small dirty burst: autocannon and machine-gun fire.</summary>
    SmallBurst = 1,

    /// <summary>A hard crack with a flash and thrown dirt: a tank round.</summary>
    ShellBurst = 2,

    /// <summary>A deep crater burst with a long dust column: artillery.</summary>
    GroundBurst = 3,

    /// <summary>A puff of black smoke in the air: a fused anti-air round.</summary>
    Airburst = 4,

    /// <summary>A big high-explosive burst: rockets and missiles.</summary>
    HeBurst = 5,

    /// <summary>A crackling electric discharge.</summary>
    Electric = 6,
}

/// <summary>
/// Everything that makes one weapon's fire look like itself.
/// <para>
/// This is presentation, and only presentation: the simulation resolved the damage
/// on the tick the shot was fired, exactly as before. A projectile here is a
/// drawing of a shot that has already happened, which is why flight times can be
/// chosen for looks — a round that took a second to cross 200 m would be a slow
/// round, but a round that takes a second to be *drawn* crossing 200 m reads as a
/// tracer, and the two are different jobs.
/// </para>
/// </summary>
/// <param name="Style">How it travels.</param>
/// <param name="Speed">Drawn speed in metres per second. Short flights only.</param>
/// <param name="Length">Length of the drawn round, in metres.</param>
/// <param name="Width">Thickness of the drawn round, in metres.</param>
/// <param name="Color">Colour of the round and its light.</param>
/// <param name="Arc">How far the drawn path bows upwards, as a fraction of range.</param>
/// <param name="Trail">Smoke puffs emitted per second while in flight.</param>
/// <param name="Shots">Rounds drawn per shot: a twin mount draws two, a salvo more.</param>
/// <param name="Spread">Lateral spread between those rounds, in metres.</param>
/// <param name="Muzzle">Size of the muzzle flash, in metres. Zero means none.</param>
/// <param name="Impact">What happens where it lands.</param>
/// <param name="Launch">Sound of the weapon firing.</param>
/// <param name="Shake">Screen shake at the firing end, in metres of camera offset.</param>
public readonly record struct FireProfile(
    FireStyle Style,
    float Speed,
    float Length,
    float Width,
    Vector3 Color,
    float Arc,
    float Trail,
    int Shots,
    float Spread,
    float Muzzle,
    FireImpact Impact,
    SoundEffectKind Launch,
    float Shake);

/// <summary>
/// The weapon table: which of the game's units fires what, and how it looks.
/// <para>
/// Keyed by the shooter's <see cref="UnitKind"/> rather than by its damage, because
/// the catalogue's numbers describe how hard something hits and this describes what
/// it looks like hitting — a rifle and a tank gun differ in both, but the Κατιούσα
/// and the howitzer differ mostly in the second.
/// </para>
/// </summary>
public static class FireProfiles
{
    private static readonly Vector3 Hot = new(1.0f, 0.86f, 0.48f);
    private static readonly Vector3 White = new(1.0f, 0.96f, 0.82f);
    private static readonly Vector3 Cold = new(0.85f, 0.90f, 1.0f);
    private static readonly Vector3 Arc = new(0.65f, 0.72f, 0.85f);

    /// <summary>The profile for a shooter, or <c>null</c> when it has no weapon.</summary>
    public static FireProfile? For(UnitKind kind) => kind switch
    {
        // Small arms: a thin streak and a spark. Cheap, fast, and there are a lot
        // of them on screen at once, so they are drawn as briefly as possible.
        UnitKind.Infantry => new FireProfile(
            FireStyle.Bullet, 620f, 3.2f, 0.05f, Hot, 0f, 0f, 1, 0f, 0.30f,
            FireImpact.Sparks, SoundEffectKind.RifleShot, 0f),

        UnitKind.Commissar => new FireProfile(
            FireStyle.Bullet, 520f, 2.2f, 0.05f, Hot, 0f, 0f, 1, 0f, 0.22f,
            FireImpact.Sparks, SoundEffectKind.RifleShot, 0f),

        UnitKind.Mercenary => new FireProfile(
            FireStyle.Bullet, 680f, 3.4f, 0.055f, Hot, 0f, 0f, 2, 0.5f, 0.32f,
            FireImpact.Sparks, SoundEffectKind.RifleShot, 0f),

        UnitKind.RobotInfantry => new FireProfile(
            FireStyle.Bullet, 700f, 3.4f, 0.055f, Hot, 0f, 0f, 2, 0.5f, 0.30f,
            FireImpact.Sparks, SoundEffectKind.RifleShot, 0f),

        UnitKind.StealthRecon => new FireProfile(
            FireStyle.Bullet, 560f, 2.6f, 0.045f, Cold, 0f, 0f, 1, 0f, 0.16f,
            FireImpact.Sparks, SoundEffectKind.RifleShot, 0f),

        // The main gun: a fat tracer, a real flash and a hard crack.
        UnitKind.Tank => new FireProfile(
            FireStyle.Shell, 780f, 7f, 0.20f, White, 0f, 0f, 1, 0f, 1.10f,
            FireImpact.ShellBurst, SoundEffectKind.TankGun, 0.30f),

        // A howitzer throws its shell rather than firing it flat, so the drawn path
        // bows and the shell is visible on the way.
        UnitKind.Artillery => new FireProfile(
            FireStyle.ArcShell, 260f, 5f, 0.18f, Arc, 0.10f, 0f, 1, 0f, 1.50f,
            FireImpact.GroundBurst, SoundEffectKind.ArtilleryLaunch, 0.45f),

        // A salvo: several rockets, all wobbling, all trailing smoke. The whole
        // point of the Κατιούσα is what it looks like when it fires.
        UnitKind.RocketArtillery => new FireProfile(
            FireStyle.Rocket, 320f, 4.5f, 0.22f, new Vector3(1.0f, 0.62f, 0.28f), 0.16f, 22f, 4, 1.1f, 2.4f,
            FireImpact.HeBurst, SoundEffectKind.ArtilleryLaunch, 0.40f),

        // Twin mounts, fast and bright, bursting in the air.
        UnitKind.AntiAir => new FireProfile(
            FireStyle.Flak, 900f, 4.5f, 0.09f, new Vector3(1.0f, 0.94f, 0.62f), 0f, 0f, 2, 0.7f, 0.45f,
            FireImpact.Airburst, SoundEffectKind.AntiAirBurst, 0.10f),

        UnitKind.Aircraft => new FireProfile(
            FireStyle.Missile, 420f, 3.6f, 0.16f, new Vector3(1.0f, 0.90f, 0.70f), 0.04f, 30f, 2, 1.4f, 0f,
            FireImpact.HeBurst, SoundEffectKind.ArtilleryLaunch, 0f),

        UnitKind.Drone => new FireProfile(
            FireStyle.Missile, 380f, 2.6f, 0.12f, new Vector3(1.0f, 0.88f, 0.66f), 0.03f, 24f, 2, 1.0f, 0f,
            FireImpact.SmallBurst, SoundEffectKind.ArtilleryLaunch, 0f),

        // Not a projectile: an arc between two points, drawn for a fraction of a second.
        // Wide on purpose — a real lightning channel is centimetres across and a
        // centimetre at three hundred metres is a fraction of a pixel, so a physically
        // sized arc is an invisible one. Drawn thin enough to read as a bolt rather
        // than as a beam.
        UnitKind.ElectroPrototype => new FireProfile(
            FireStyle.Bolt, 4_000f, 0f, 0.80f, new Vector3(0.62f, 0.85f, 1.0f), 0.02f, 0f, 1, 0f, 1.6f,
            FireImpact.Electric, SoundEffectKind.TankGun, 0.25f),

        _ => null,
    };

    /// <summary>True when this kind of unit shoots at all.</summary>
    public static bool HasWeapon(UnitKind kind) => For(kind) is not null;
}
