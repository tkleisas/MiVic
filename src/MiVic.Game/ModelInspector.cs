using System.Globalization;
using MiVic.Game.Data;
using MiVic.Game.Rendering;
using MiVic.Game.Rendering.Gltf;
using Microsoft.Xna.Framework;

namespace MiVic.Game;

/// <summary>
/// Headless model diagnostics: loads every glTF file through the real loader and
/// reports its geometry.
/// <para>
/// This exists because orientation and scale cannot be eyeballed reliably during
/// development. A model that is authored Z-up, or that faces the wrong way, shows
/// up here as an implausible bounding box (a soldier wider than he is tall, a
/// tank longer across than along) instead of as a mystery in the render.
/// </para>
/// </summary>
public static class ModelInspector
{
    /// <summary>Loads every configured model slot and prints a summary.</summary>
    public static int Run(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
        {
            Console.Error.WriteLine($"No model directory at '{baseDirectory}'.");
            return 1;
        }

        Console.WriteLine($"Inspecting configured model slots under {baseDirectory}");
        Console.WriteLine("slot                          verts   tris    size (x,y,z metres)     up  long  note");
        Console.WriteLine("---------------------------------------------------------------------------------------");

        int failures = 0;
        int missing = 0;

        foreach ((Core.Sim.Faction faction, Core.Sim.UnitKind kind, string relative, ModelImportOptions options) in ModelCatalog.Enumerate())
        {
            string path = Path.Combine(baseDirectory, relative);
            string slot = $"{faction}/{kind}";

            if (!File.Exists(path))
            {
                missing++;
                Console.WriteLine($"{slot,-29} --      --      missing {relative}");
                continue;
            }

            try
            {
                MeshData mesh = GltfLoader.Load(path, options);
                (Vector3 min, Vector3 max) = Bounds(mesh);
                Vector3 size = max - min;

                string up = size.Y >= size.X && size.Y >= size.Z ? "Y" : size.Z >= size.X ? "Z" : "X";
                string longest = size.X >= size.Y && size.X >= size.Z ? "X" : size.Y >= size.Z ? "Y" : "Z";

                // A unit must face +X. Two measurements help decide: the angle of
                // the vertex furthest from the centroid (reliable when one feature
                // protrudes, like a gun barrel), and the width of the silhouette at
                // each end of X (a nose tapers, a tail usually does not).
                float facing = FacingDegrees(mesh);
                (float plusTip, float minusTip) = TipWidths(mesh);

                string note = string.Empty;
                string expected = ExpectedLongestAxis(kind);

                if (longest != expected)
                {
                    note = $"! long axis is {longest}, expected {expected}";
                }

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{slot,-29} {mesh.Vertices.Length,6} {mesh.PrimitiveCount,6}   {size.X,6:0.00},{size.Y,6:0.00},{size.Z,6:0.00}       {up}   {longest}   nose {facing,6:0.0}  tip {plusTip,5:0.00}/{minusTip,5:0.00}  {note}"));
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"{slot,-29} FAILED: {exception.Message}");
            }
        }

        PrintParts(baseDirectory);

        Console.WriteLine();
        Console.WriteLine($"failures={failures} missing={missing}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Lists the named parts of each model, which is what animation needs.
    /// <para>
    /// A model whose parts are baked into one mesh has no parts to list, and a
    /// part-driven animation would silently do nothing to it. This is the check
    /// that catches that: the names here are exactly the ones the renderer can
    /// address, and <c>turret</c>, <c>barrel</c>, <c>wheel_l01</c> and <c>radar</c>
    /// are the contract.
    /// </para>
    /// </summary>
    private static void PrintParts(string baseDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("named parts (animation contract)");
        Console.WriteLine("---------------------------------------------------------------------------------------");

        foreach ((Core.Sim.Faction faction, Core.Sim.UnitKind kind, string relative, ModelImportOptions options) in ModelCatalog.Enumerate())
        {
            string path = Path.Combine(baseDirectory, relative);

            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                ModelData model = GltfLoader.LoadModel(path, options);
                string names = string.Join(", ", model.Parts.Select(part => part.Name));

                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{faction}/{kind,-24} {model.Parts.Count,3} parts  {names}"));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{faction}/{kind,-24} parts FAILED: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Which axis a role's longest dimension should land on once imported.
    /// <para>
    /// Almost everything is longest along +X, because that is forward and a unit
    /// faces +X. The exceptions are not errors: a standing figure is taller than it
    /// is deep, and a structure whose frontage exceeds its depth is left alone by
    /// the loader's own turn and then turned by the catalogue's, which puts that
    /// frontage along Z. Warning about those would train the reader to ignore the
    /// warning.
    /// </para>
    /// <para>
    /// Two of these are also a check worth having. A model crosses the loader's
    /// alignment line — see <c>ModelCatalog.GeneratedYaw</c> — by being wider than
    /// it is long, and that is exactly when the axis in this table changes; so an
    /// aircraft or a structure reported on the wrong axis is the signal that its
    /// yaw offset may need to change with it.
    /// </para>
    /// </summary>
    private static string ExpectedLongestAxis(Core.Sim.UnitKind kind) => kind switch
    {
        // Every aircraft is ours now, and all three are longer nose to tail than
        // they are across: the borrowed models this used to expect "Z" for are gone.
        Core.Sim.UnitKind.Aircraft => "X",

        // The drone is the one flyer that is wider across its rotors than it is long,
        // so the loader does not turn it and the catalogue does.
        Core.Sim.UnitKind.Drone => "Z",

        Core.Sim.UnitKind.Infantry => "Y",
        Core.Sim.UnitKind.Commissar => "Y",
        Core.Sim.UnitKind.RobotInfantry => "Y",
        Core.Sim.UnitKind.Mercenary => "Y",
        Core.Sim.UnitKind.StealthRecon => "Y",

        // A chimney stack and a research tower are taller than they are wide.
        Core.Sim.UnitKind.PowerPlant => "Y",
        Core.Sim.UnitKind.DesignBureau => "Y",
        Core.Sim.UnitKind.NuclearPlant => "Y",

        // A headquarters and a factory are wider than they are deep, which is the
        // turn the loader does not make for them.
        Core.Sim.UnitKind.CommandCentre => "Z",
        Core.Sim.UnitKind.Factory => "Z",
        _ => "X",
    };

    /// <summary>
    /// Angle of the mesh's most distant vertex from +X, in degrees. For a tank
    /// that vertex is the barrel tip, so 0° means the model faces forward and
    /// 180° means it faces backwards.
    /// </summary>
    private static float FacingDegrees(MeshData mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            return 0f;
        }

        Vector3 centroid = Vector3.Zero;

        foreach (VertexPositionNormal vertex in mesh.Vertices)
        {
            centroid += vertex.Position;
        }

        centroid /= mesh.Vertices.Length;

        Vector3 farthest = Vector3.Zero;
        float best = -1f;

        foreach (VertexPositionNormal vertex in mesh.Vertices)
        {
            Vector3 offset = vertex.Position - centroid;
            float distance = (offset.X * offset.X) + (offset.Z * offset.Z);

            if (distance > best)
            {
                best = distance;
                farthest = offset;
            }
        }

        return MathHelper.ToDegrees(MathF.Atan2(farthest.Z, farthest.X));
    }

    /// <summary>
    /// Z-extent of the vertices in the outermost tenth of each end of the X axis.
    /// A nose tapers to a narrow tip; a tail is usually broad, so the narrower
    /// end is the front.
    /// </summary>
    private static (float Plus, float Minus) TipWidths(MeshData mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            return (0f, 0f);
        }

        (Vector3 min, Vector3 max) = Bounds(mesh);
        float band = Math.Max((max.X - min.X) * 0.1f, 0.01f);

        float plusMin = float.MaxValue;
        float plusMax = float.MinValue;
        float minusMin = float.MaxValue;
        float minusMax = float.MinValue;

        foreach (VertexPositionNormal vertex in mesh.Vertices)
        {
            if (vertex.Position.X >= max.X - band)
            {
                plusMin = MathF.Min(plusMin, vertex.Position.Z);
                plusMax = MathF.Max(plusMax, vertex.Position.Z);
            }

            if (vertex.Position.X <= min.X + band)
            {
                minusMin = MathF.Min(minusMin, vertex.Position.Z);
                minusMax = MathF.Max(minusMax, vertex.Position.Z);
            }
        }

        float plus = plusMin <= plusMax ? plusMax - plusMin : 0f;
        float minus = minusMin <= minusMax ? minusMax - minusMin : 0f;

        return (plus, minus);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(MeshData mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        Vector3 min = mesh.Vertices[0].Position;
        Vector3 max = mesh.Vertices[0].Position;

        foreach (VertexPositionNormal vertex in mesh.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }
}
