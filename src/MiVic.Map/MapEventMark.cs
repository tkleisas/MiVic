namespace MiVic.Map;

/// <summary>What an event mark on the map is.</summary>
public enum MapEventKind : byte
{
    /// <summary>A weapon fired. Drawn as a line from the shooter towards what it was aimed at.</summary>
    Shot = 0,

    /// <summary>Something took damage. Drawn as a cross at the point it was hit.</summary>
    Hit = 1,

    /// <summary>Something was destroyed. Drawn as a ringed cross where it stood.</summary>
    Death = 2,
}

/// <summary>
/// One thing that happened, at a place, with a tick on it.
/// <para>
/// The simulation's own event stream is the client's — a presentation type in metres with a
/// model effect hanging off it — and this is the map's: millimetres, no effect, and a tick so
/// the mark can fade with age. The translation happens at the probe, which is the only place
/// that has both.
/// </para>
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Tick">When.</param>
/// <param name="Faction">Whose event it is: the shooter's, or the victim's where there is no shooter.</param>
/// <param name="TeamId">The team that event belongs to.</param>
/// <param name="Role">The role of the entity the event was recorded against.</param>
/// <param name="Position">Where it happened.</param>
/// <param name="Target">What was aimed at, or null when nothing was.</param>
/// <param name="Damage">Damage taken, for a hit; zero otherwise.</param>
public readonly record struct MapEventMark(
    MapEventKind Kind,
    long Tick,
    MiVic.Core.Sim.Faction Faction,
    int TeamId,
    MiVic.Core.Sim.UnitKind Role,
    MiVic.Core.Numerics.WorldPos Position,
    MiVic.Core.Numerics.WorldPos? Target,
    int Damage);
