using MiVic.Core.Terrain;

namespace MiVic.Core.Pathfinding;

/// <summary>
/// What a path search needs to know about the mover: how it moves, and how heavily
/// it presses on soft ground.
/// <para>
/// Passed by value so the search stays allocation-free, and deliberately expressed
/// in terms the terrain layer understands rather than in terms of unit roles — the
/// terrain must not have to know that a "tank" exists.
/// </para>
/// </summary>
/// <param name="Movement">How the unit travels.</param>
/// <param name="GroundPressurePermille">Nominal ground pressure, 1000 being baseline.</param>
public readonly record struct PathContext(MovementClass Movement, int GroundPressurePermille)
{
    /// <summary>A tracked vehicle at baseline pressure.</summary>
    public static readonly PathContext Default = new(MovementClass.Tracked, 1_000);
}
