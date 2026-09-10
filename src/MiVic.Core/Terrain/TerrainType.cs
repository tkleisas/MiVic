namespace MiVic.Core.Terrain;

/// <summary>
/// Surface types on the navigation lattice.
/// <para>
/// Values are stable because they are written into replays and saved games.
/// </para>
/// </summary>
public enum TerrainType : byte
{
    /// <summary>Open ground. The baseline everything else is measured against.</summary>
    Grass = 0,

    /// <summary>Soft wet ground. Slows anything with ground pressure; slows it more the heavier it is.</summary>
    Mud = 1,

    /// <summary>Loose dry ground. Slightly slower than grass, worse for wheels than tracks.</summary>
    Sand = 2,

    /// <summary>Frozen ground. Slows everything except aircraft.</summary>
    Snow = 3,

    /// <summary>Steep bare rock. Passable but expensive.</summary>
    Rock = 4,

    /// <summary>Water at a depth ground units cannot cross, but aircraft ignore.</summary>
    ShallowWater = 5,

    /// <summary>Deep water: lakes. Impassable to everything on the ground.</summary>
    DeepWater = 6,

    /// <summary>Molten rock. Impassable and damaging.</summary>
    Lava = 7,

    /// <summary>A mineral deposit a harvester can work.</summary>
    Mine = 8,

    /// <summary>
    /// Woodland: hard going for anything on tracks or wheels, and cover for anything on
    /// foot. The one surface in the game that treats the movement classes as opposites
    /// rather than as a ranking.
    /// </summary>
    Forest = 9,
}

/// <summary>
/// How a unit moves, which is what terrain actually cares about.
/// <para>
/// The terrain layer deliberately knows nothing about unit roles or factions: a
/// tank and an artillery piece both run on tracks. Roles carry their movement
/// class and their ground pressure, and the cost table is keyed on those.
/// </para>
/// </summary>
public enum MovementClass : byte
{
    /// <summary>Not a mobile unit.</summary>
    None = 0,

    /// <summary>Legs. Narrow footprint, hard to bog down, slow everywhere.</summary>
    Foot = 1,

    /// <summary>Tracks. Wide contact patch, the best of the ground classes in mud.</summary>
    Tracked = 2,

    /// <summary>Wheels. Cheapest on roads and firm ground, worst in mud.</summary>
    Wheeled = 3,

    /// <summary>Flies. Terrain is irrelevant except that it cannot be walked into.</summary>
    Air = 4,
}
