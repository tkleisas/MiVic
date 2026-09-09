namespace MiVic.Core.Numerics;

/// <summary>
/// Absolute position in the simulation world, measured in integer millimetres.
/// <para>
/// Integer millimetres are exact, cheap, and identical on every machine, which
/// is what makes deterministic lockstep multiplayer possible later. One metre is
/// 1000 units, so a 2 km x 2 km map spans +/-1,000,000 units — comfortably
/// inside <see cref="int"/>.
/// </para>
/// </summary>
public readonly struct WorldPos : IEquatable<WorldPos>
{
    /// <summary>Millimetres per metre.</summary>
    public const int MmPerMetre = 1000;

    /// <summary>Metres per world unit group, used when talking to the renderer.</summary>
    public const float MmToMetres = 1f / MmPerMetre;

    /// <summary>East/west axis, in millimetres.</summary>
    public readonly int X;

    /// <summary>Altitude, in millimetres. Zero is sea level of the map.</summary>
    public readonly int Y;

    /// <summary>North/south axis, in millimetres.</summary>
    public readonly int Z;

    public WorldPos(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly WorldPos Origin = new(0, 0, 0);

    /// <summary>Builds a position from whole metres, for map and data authoring.</summary>
    public static WorldPos FromMetres(int x, int y, int z)
        => new(x * MmPerMetre, y * MmPerMetre, z * MmPerMetre);

    /// <summary>Builds a ground position from metres, at the given altitude.</summary>
    public static WorldPos GroundMetres(int x, int z) => new(x * MmPerMetre, 0, z * MmPerMetre);

    public static WorldPos operator +(WorldPos a, WorldPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static WorldPos operator -(WorldPos a, WorldPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static WorldPos operator *(WorldPos a, int scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);

    public static bool operator ==(WorldPos a, WorldPos b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    public static bool operator !=(WorldPos a, WorldPos b) => !(a == b);

    /// <summary>Squared distance in mm^2. Cheaper than <see cref="DistanceTo"/> and exact.</summary>
    public long DistanceSquaredTo(WorldPos other)
    {
        long dx = (long)X - other.X;
        long dy = (long)Y - other.Y;
        long dz = (long)Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>Distance in millimetres, truncated towards zero.</summary>
    public int DistanceTo(WorldPos other) => (int)IntMath.SqrtLong(DistanceSquaredTo(other));

    /// <summary>Horizontal distance in millimetres, ignoring altitude.</summary>
    public int HorizontalDistanceTo(WorldPos other)
        => IntMath.Distance(X - other.X, 0, Z - other.Z);

    /// <summary>True when this position lies within <paramref name="radiusMm"/> of <paramref name="other"/>.</summary>
    public bool IsWithin(WorldPos other, int radiusMm)
        => DistanceSquaredTo(other) <= (long)radiusMm * radiusMm;

    /// <summary>Renders the position as metres, for display and for the renderer.</summary>
    public (float X, float Y, float Z) ToMetres()
        => (X * MmToMetres, Y * MmToMetres, Z * MmToMetres);

    public bool Equals(WorldPos other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is WorldPos other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString()
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"({X / MmPerMetre}.{Math.Abs(X % MmPerMetre):000},{Z / MmPerMetre}.{Math.Abs(Z % MmPerMetre):000})");
}
