namespace MiVic.Core.Sim;

/// <summary>
/// A stable handle to a live entity: a slot index plus a generation counter.
/// <para>
/// The generation is bumped every time a slot is reused, so a stale handle from
/// before a unit died can never accidentally address whatever unit took its
/// place. This is what makes orders queued in flight safe.
/// </para>
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    /// <summary>Sentinel meaning "no entity".</summary>
    public static readonly EntityId None = new(-1, 0);

    /// <summary>Index into the world's entity storage.</summary>
    public readonly int Slot;

    /// <summary>Reuse counter for <see cref="Slot"/>.</summary>
    public readonly int Generation;

    public EntityId(int slot, int generation)
    {
        Slot = slot;
        Generation = generation;
    }

    /// <summary>True when this handle refers to nothing.</summary>
    public bool IsNone => Slot < 0;

    public bool Equals(EntityId other) => Slot == other.Slot && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Slot, Generation);

    public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);

    public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);

    public override string ToString()
        => IsNone ? "EntityId(none)" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"EntityId({Slot}#{Generation})");
}
