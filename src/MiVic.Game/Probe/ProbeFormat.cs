using System.Globalization;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Probe;

/// <summary>
/// Numbers, vectors and matrices as a probe writes them.
/// <para>
/// Every number carries its unit and is rounded to the few digits that decide the
/// question: the reader of a probe transcript is a language model reading a text file,
/// and <c>34.2 m</c> answers "how far away is it" where <c>34.249996185302734</c> only
/// adds something to mis-read. Rounding is also what makes two runs comparable by eye
/// without a diff tool.
/// </para>
/// <para>
/// Everything is invariant culture. A transcript that wrote <c>34,2 m</c> on a machine
/// whose locale uses a comma for a decimal point would be a different file for the same
/// script, which is exactly what the probe exists to avoid.
/// </para>
/// </summary>
public static class ProbeFormat
{
    /// <summary>Millimetres as metres, one decimal: the resolution a unit's position is read at.</summary>
    public static string Millimetres(int millimetres)
        => string.Create(CultureInfo.InvariantCulture, $"{millimetres / (float)WorldPos.MmPerMetre:0.0} m");

    /// <summary>Metres, one decimal.</summary>
    public static string Metres(float metres)
        => string.Create(CultureInfo.InvariantCulture, $"{metres:0.0} m");

    /// <summary>A render-space point in metres, as <c>(x, y, z) m</c>.</summary>
    public static string Point(Vector3 point)
        => string.Create(CultureInfo.InvariantCulture, $"({point.X:0.0}, {point.Y:0.0}, {point.Z:0.0}) m");

    /// <summary>A simulation position, flattened to the two axes anything on the ground moves in.</summary>
    public static string Ground(WorldPos position)
        => string.Create(CultureInfo.InvariantCulture, $"(x {position.X / (float)WorldPos.MmPerMetre:0.0}, z {position.Z / (float)WorldPos.MmPerMetre:0.0}) m");

    /// <summary>
    /// A direction or basis vector, two decimals: enough to see which axis it is.
    /// <para>
    /// A component that rounds to nothing is written as nothing. A rotation about Y arrives
    /// out of the matrix arithmetic with a few millionths of X and Z on it, and printing
    /// those as <c>-0.00</c> turns "turns about the vertical" into a reader's doubt about
    /// whether it is nearly vertical or exactly so.
    /// </para>
    /// </summary>
    public static string Vector(Vector3 vector)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"({Snap(vector.X):0.00}, {Snap(vector.Y):0.00}, {Snap(vector.Z):0.00})");

    /// <summary>
    /// A horizontal direction as a compass bearing, in degrees.
    /// <para>
    /// The same convention the simulation's heading uses: zero is +X, which is east, and it
    /// grows towards +Z, which is south. A shot's bearing and a gun's bearing are therefore
    /// directly comparable, which is the whole point of printing one.
    /// </para>
    /// </summary>
    public static string Bearing(Vector3 direction)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{MathHelper.ToDegrees(MathF.Atan2(direction.Z, direction.X)):0.0}°");

    /// <summary>An offset in metres, snapped like a direction so that a zero is a zero.</summary>
    public static string Shift(Vector3 offset)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"({Snap(offset.X):0.00}, {Snap(offset.Y):0.00}, {Snap(offset.Z):0.00}) m");

    private static float Snap(float value) => MathF.Abs(value) < 0.005f ? 0f : value;

    /// <summary>Radians as degrees, one decimal.</summary>
    public static string Degrees(float radians)
        => string.Create(CultureInfo.InvariantCulture, $"{MathHelper.ToDegrees(radians):0.0}°");

    /// <summary>An angle in both units, radians first because that is what the code takes.</summary>
    public static string Angle(float radians)
        => string.Create(CultureInfo.InvariantCulture, $"{radians:0.000} rad ({MathHelper.ToDegrees(radians):0.0}°)");

    /// <summary>Signed degrees, so a direction is readable without a sign convention note.</summary>
    public static string SignedDegrees(float radians)
        => string.Create(CultureInfo.InvariantCulture, $"{MathHelper.ToDegrees(radians):+0.0;-0.0;0.0}°");

    /// <summary>A whole number with its noun, pluralised.</summary>
    public static string Count(int value, string singular, string? plural = null)
        => string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? singular : plural ?? singular + "s")}");

    /// <summary>Ticks with the wall-clock time they are worth, because 20 Hz is not obvious.</summary>
    public static string Ticks(long ticks)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{ticks} ticks ({ticks / (double)SimConstants.TickRate:0.0} s)");

    /// <summary>Seconds, one decimal.</summary>
    public static string Seconds(double seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0} s");

    /// <summary>Seconds, in milliseconds: a frame is 16.7 ms and "0.0 s" is not a frame time.</summary>
    public static string Milliseconds(float seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{seconds * 1000f:0.0} ms");

    /// <summary>Permille with the plain-language meaning of the scale attached.</summary>
    public static string Permille(int permille)
        => string.Create(CultureInfo.InvariantCulture, $"{permille} ‰");

    /// <summary>A file size, because "did the shot happen" is answered by the PNG existing and having bytes.</summary>
    public static string Bytes(long bytes)
        => bytes < 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} kB");

    /// <summary>
    /// A transform as its translation and its three basis vectors.
    /// <para>
    /// Written out rather than summarised as a position, because the question a transform
    /// answers here is which way a part points: a turret whose position is right and whose
    /// basis is rotated a quarter turn is a turret pointing the wrong way, and a line that
    /// printed only a position could not say so.
    /// </para>
    /// </summary>
    public static string Transform(Matrix matrix)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"T({matrix.M41:0.00}, {matrix.M42:0.00}, {matrix.M43:0.00}) m " +
            $"X({matrix.M11:0.00}, {matrix.M12:0.00}, {matrix.M13:0.00}) " +
            $"Y({matrix.M21:0.00}, {matrix.M22:0.00}, {matrix.M23:0.00}) " +
            $"Z({matrix.M31:0.00}, {matrix.M32:0.00}, {matrix.M33:0.00})");

    /// <summary>The scale a transform carries, read off its three basis lengths.</summary>
    public static string Scale(Matrix matrix)
    {
        float x = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
        float y = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
        float z = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();

        return string.Create(CultureInfo.InvariantCulture, $"{x:0.000} x {y:0.000} x {z:0.000}");
    }
}
