using MiVic.Core.Sim;
using MiVic.Game.Rendering;
using MiVic.Game.Rendering.Gltf;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Data;

/// <summary>
/// Resolves the mesh used for a faction's unit of a given role.
/// <para>
/// Every lookup falls back to procedural geometry when the model file is
/// missing, so the game always runs: art can arrive one asset at a time without
/// ever breaking a build or a play test. Failures are recorded rather than
/// thrown, and the client reports them.
/// </para>
/// </summary>
public sealed class ModelCatalog : IDisposable
{
    /// <summary>Folder under the executable that holds imported models.</summary>
    public const string ModelRoot = "Content/Models";

    private readonly InstancedRenderer _renderer;
    private readonly string _baseDirectory;
    private readonly Dictionary<int, InstancedRenderer.Mesh> _cache = [];
    private readonly Dictionary<int, ModelParts> _partsCache = [];
    private readonly Dictionary<int, InstancedRenderer.Mesh> _wholeCache = [];
    private readonly List<InstancedRenderer.Mesh> _owned = [];
    private readonly List<string> _loaded = [];
    private readonly List<string> _failed = [];

    public ModelCatalog(InstancedRenderer renderer, string baseDirectory)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
    }

    /// <summary>Model files that were imported successfully.</summary>
    public IReadOnlyList<string> LoadedModels => _loaded;

    /// <summary>Model files that were missing or failed to import, with the reason.</summary>
    public IReadOnlyList<string> FailedModels => _failed;

    /// <summary>
    /// Lists every configured model slot with its import options, so diagnostics
    /// can validate exactly what the game will load.
    /// </summary>
    public static IEnumerable<(Faction Faction, UnitKind Kind, string RelativePath, ModelImportOptions Options)> Enumerate()
        => ModelSpec.EnumerateSpecs();

    /// <summary>Gets the mesh for a faction and role, importing or building it on first use.</summary>
    public InstancedRenderer.Mesh Get(Faction faction, UnitKind kind)
    {
        int key = CacheKey(faction, kind);

        if (_cache.TryGetValue(key, out InstancedRenderer.Mesh? cached))
        {
            return cached;
        }

        InstancedRenderer.Mesh mesh = LoadOrBuild(faction, kind);
        _cache[key] = mesh;
        return mesh;
    }

    /// <summary>
    /// Gets every part of a faction's role, each with its place in the model.
    /// <para>
    /// A model that imports as a single mesh — a procedural fallback, or an asset
    /// with no node hierarchy — comes back as one part with an identity transform,
    /// so callers never need a special case for "this model has no parts".
    /// </para>
    /// </summary>
    public ModelParts GetParts(Faction faction, UnitKind kind)
    {
        int key = CacheKey(faction, kind);

        if (_partsCache.TryGetValue(key, out ModelParts cached))
        {
            return cached;
        }

        ModelParts parts = LoadParts(faction, kind);
        _partsCache[key] = parts;
        return parts;
    }

    private ModelParts LoadParts(Faction faction, UnitKind kind)
    {
        ModelData? model = null;

        if (ModelSpec.TryGet(faction, kind, out ModelSpec spec))
        {
            string path = Path.Combine(_baseDirectory, ModelRoot, spec.Folder, spec.FileName);

            if (File.Exists(path))
            {
                try
                {
                    model = GltfLoader.LoadModel(path, spec.ToImportOptions());
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
                {
                    _failed.Add($"{spec.Folder}/{spec.FileName}: {exception.Message}");
                }
            }
        }

        if (model is null)
        {
            // No model: the procedural mesh becomes the whole model, with an
            // identity transform, so the renderer has exactly one shape to handle.
            InstancedRenderer.Mesh fallback = Get(faction, kind);
            return new ModelParts(
                [new PartMesh("model", fallback, Matrix.Identity, -1, Vector3.Zero)],
                Matrix.Identity,
                0f);
        }

        PartMesh[] parts = new PartMesh[model.Value.Parts.Count];
        float wheelRadius = 0f;
        float modelScale = model.Value.ModelTransform.M11;

        for (int i = 0; i < parts.Length; i++)
        {
            ModelPart part = model.Value.Parts[i];

            InstancedRenderer.Mesh mesh = _renderer.CreateMesh(part.Mesh);
            _owned.Add(mesh);

            (Vector3 min, Vector3 max) = MeshBounds(part.Mesh);

            // Parent-relative, so the renderer can compose the chain per entity and
            // rotate one part without freezing its children in place.
            parts[i] = new PartMesh(part.Name, mesh, part.LocalTransform, part.ParentIndex, max);

            if (wheelRadius <= 0f && part.Name.StartsWith("wheel_", StringComparison.Ordinal))
            {
                Vector3 size = max - min;

                // A wheel is a disc: its radius is half of whichever cross-section
                // is smaller, so the axle direction does not matter.
                wheelRadius = MathF.Min(size.Y, size.Z) * 0.5f * modelScale;
            }
        }

        _loaded.Add($"{ModelSpecLabel(faction, kind)} ({parts.Length} parts)");
        return new ModelParts(parts, model.Value.ModelTransform, wheelRadius);
    }

    private static (Vector3 Min, Vector3 Max) MeshBounds(MeshData mesh)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        foreach (VertexPositionNormal vertex in mesh.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        return (min, max);
    }

    private static string ModelSpecLabel(Faction faction, UnitKind kind)
        => ModelSpec.TryGet(faction, kind, out ModelSpec spec)
            ? $"{spec.Folder}/{spec.FileName}"
            : $"{faction}/{kind}";

    /// <summary>A role's parts plus the transform that normalises the whole model.</summary>
    /// <param name="Parts">Parts, parents before children.</param>
    /// <param name="ModelTransform">Alignment, scale, centring and grounding.</param>
    /// <param name="WheelRadiusMetres">
    /// Radius of a road wheel, measured from the model itself. A wheel spun by
    /// distance travelled needs its own radius, and measuring it here means a new
    /// model does not also need a number typed into a table.
    /// </param>
    public readonly record struct ModelParts(
        PartMesh[] Parts,
        Matrix ModelTransform,
        float WheelRadiusMetres);

    /// <summary>One part's GPU mesh and where it sits relative to its parent.</summary>
    /// <param name="Name">The part contract name, which is what animation keys on.</param>
    /// <param name="Mesh">The geometry to draw.</param>
    /// <param name="LocalTransform">Placement inside the parent's space.</param>
    /// <param name="ParentIndex">Index of the enclosing part, or -1 for a root part.</param>
    /// <param name="BoundsMax">
    /// Top of the part in its own space. A limb swings from the joint at its top,
    /// and since an imported asset does not come with a skeleton, measuring where
    /// the top is beats assuming the mesh origin is the hip.
    /// </param>
    public readonly record struct PartMesh(
        string Name,
        InstancedRenderer.Mesh Mesh,
        Matrix LocalTransform,
        int ParentIndex,
        Vector3 BoundsMax);

    /// <summary>
    /// Radius of this role's road wheels in metres, measured from the model, or
    /// zero when it has none. Wheels are spun by distance travelled, and a wheel
    /// that does not know its own size cannot roll at the right speed.
    /// </summary>
    public float WheelRadius(Faction faction, UnitKind kind)
        => GetParts(faction, kind).WheelRadiusMetres;

    /// <summary>
    /// How big this role's model is, as the length of its longest axis in metres.
    /// <para>
    /// Presentation uses it to scale things that happen *to* a unit — where a
    /// muzzle flash sits, how big a shell is, how far a wreck throws its debris —
    /// so an infantryman's rifle does not fire a tank round's effect.
    /// </para>
    /// </summary>
    public static float NominalSizeMetres(Faction faction, UnitKind kind)
        => ModelSpec.TryGet(faction, kind, out ModelSpec spec) ? spec.TargetSizeMetres : 2f;

    private static int CacheKey(Faction faction, UnitKind kind) => ((int)faction << 8) | (int)kind;

    /// <summary>
    /// Gets the whole model as a single mesh, for something that draws a role as one
    /// piece of geometry rather than as the parts the animator moves.
    /// <para>
    /// A placement ghost is the caller this exists for: the player is choosing where a
    /// building will stand, so what has to be on screen is the building, and a ghost
    /// assembled from a dozen animated parts would be a dozen instances of a mesh none of
    /// which knows where the others are. This is the loader's own merge of the same file the
    /// parts come from, with the model's normalisation — alignment, scale, centring,
    /// grounding — already in the vertices, so the caller has only to place it.
    /// </para>
    /// <para>
    /// It is kept out of <see cref="LoadedModels"/> and <see cref="FailedModels"/> on purpose:
    /// those are the import report, one line per model slot, and a second line for a file the
    /// client has already reported reading would read as a model loaded twice.
    /// </para>
    /// </summary>
    public InstancedRenderer.Mesh Whole(Faction faction, UnitKind kind)
    {
        int key = CacheKey(faction, kind);

        if (_wholeCache.TryGetValue(key, out InstancedRenderer.Mesh? cached))
        {
            return cached;
        }

        InstancedRenderer.Mesh mesh = LoadOrBuild(faction, kind, report: false);
        _wholeCache[key] = mesh;
        return mesh;
    }

    private InstancedRenderer.Mesh LoadOrBuild(Faction faction, UnitKind kind, bool report = true)
    {
        if (ModelSpec.TryGet(faction, kind, out ModelSpec spec))
        {
            string path = Path.Combine(_baseDirectory, ModelRoot, spec.Folder, spec.FileName);

            if (File.Exists(path))
            {
                try
                {
                    MeshData data = GltfLoader.Load(path, spec.ToImportOptions());
                    InstancedRenderer.Mesh imported = _renderer.CreateMesh(data);
                    _owned.Add(imported);

                    if (report)
                    {
                        _loaded.Add($"{spec.Folder}/{spec.FileName}");
                    }

                    return imported;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
                {
                    if (report)
                    {
                        _failed.Add($"{spec.Folder}/{spec.FileName}: {exception.Message}");
                    }
                }
            }
            else if (report)
            {
                _failed.Add($"{spec.Folder}/{spec.FileName}: file not found");
            }
        }

        InstancedRenderer.Mesh fallback = _renderer.CreateMesh(ProceduralMesh(kind));
        _owned.Add(fallback);
        return fallback;
    }

    /// <summary>Placeholder geometry, used until a model exists for the role.</summary>
    private static MeshData ProceduralMesh(UnitKind kind) => kind switch
    {
        UnitKind.Infantry => MeshBuilder.Box(1.2f, 1.8f, 1.2f),
        UnitKind.Tank => MeshBuilder.Box(3.2f, 1.6f, 5f),
        UnitKind.Artillery => MeshBuilder.Box(3f, 1.4f, 5.5f),
        UnitKind.RocketArtillery => MeshBuilder.Box(3.1f, 1.5f, 6f),
        UnitKind.Commissar => MeshBuilder.Box(1.1f, 1.9f, 1.1f),
        UnitKind.AntiAir => MeshBuilder.Cylinder(1.8f, 2.4f),
        UnitKind.Aircraft => MeshBuilder.Wedge(4f, 1.6f, 7f),
        UnitKind.Drone => MeshBuilder.Wedge(2.2f, 0.9f, 3.2f),
        UnitKind.RobotInfantry => MeshBuilder.Box(1.1f, 1.7f, 1.1f),
        UnitKind.Mercenary => MeshBuilder.Box(1.3f, 1.9f, 1.3f),
        UnitKind.StealthRecon => MeshBuilder.Wedge(1.6f, 1.4f, 2.2f),
        UnitKind.CommandCentre => MeshBuilder.Box(14f, 9f, 14f),
        UnitKind.PowerPlant => MeshBuilder.Box(9f, 7f, 9f),
        UnitKind.NuclearPlant => MeshBuilder.Cylinder(11f, 14f),
        UnitKind.Factory => MeshBuilder.Box(16f, 8f, 12f),
        UnitKind.DesignBureau => MeshBuilder.Box(10f, 11f, 10f),
        UnitKind.Harvester => MeshBuilder.Box(3f, 2f, 4f),

        // The emplacements, until their models arrive: a low square pit with a gun on it and
        // a taller round one. Two different placeholder shapes on purpose — a fallback that
        // made a gun emplacement and an anti-aircraft emplacement look alike would hide the
        // fact that one of the two files failed to load.
        UnitKind.GunEmplacement => MeshBuilder.Box(9f, 3.4f, 10f),
        UnitKind.AntiAirEmplacement => MeshBuilder.Cylinder(4.6f, 5f),

        // And the radar station, until its model arrives: a mast, because the one thing that
        // has to survive a missing file is the silhouette. A player who cannot see that the
        // thing on the ridge turns has no way to tell a radar that is working from one whose
        // power has been cut, and the sweep is the whole of that signal.
        UnitKind.RadarStation => MeshBuilder.Cylinder(2.2f, 8f),
        _ => MeshBuilder.Box(1f, 1f, 1f),
    };

    /// <summary>Where a role's model lives and how to fit it into the world.</summary>
    /// <param name="Folder">Faction subfolder under the model root.</param>
    /// <param name="FileName">Model file name.</param>
    /// <param name="TargetSizeMetres">Length of the longest axis after import.</param>
    /// <param name="YawOffsetDegrees">
    /// Extra rotation after automatic forward alignment. Every generator here
    /// authors a model front-first, so this is the turn the loader's own alignment
    /// did <em>not</em> make: none when it turned the model, a quarter turn when it
    /// did not.
    /// </param>
    /// <param name="MeshNameContains">Optional mesh-name filter for modular assets.</param>
    private readonly record struct ModelSpec(
        string Folder,
        string FileName,
        float TargetSizeMetres,
        float YawOffsetDegrees = 0f,
        string? MeshNameContains = null)
    {
        /// <summary>
        /// A generated model that the loader already turns: no correction at all.
        /// <para>
        /// Every generator in <c>tools/blender</c> builds a vehicle, an aircraft, a
        /// figure or a structure front-first along Blender's +Y, which the glTF
        /// exporter writes as -Z. The loader then turns a model whose longest
        /// horizontal axis is Z a quarter turn about Y, and that turn is what lands
        /// the exporter's -Z front on the +X the simulation calls forward. So a model
        /// the loader turns needs nothing further.
        /// </para>
        /// <para>
        /// This constant used to be a half turn, on the belief that the alignment
        /// left the nose 180° out. It does not: it puts the nose on +X, and the half
        /// turn put every generated model on -X instead. A tank therefore drove
        /// backwards with its gun over the engine deck, a harvester pushed its bucket
        /// along behind it — and a turret, which is animated inside the model's own
        /// frame and so inherits the model's facing, pointed exactly away from
        /// whatever it was shooting at.
        /// </para>
        /// </summary>
        private const float GeneratedYaw = 0f;

        /// <summary>
        /// A generated model the loader does <em>not</em> turn, because it is wider
        /// across than it is long: a figure's shoulders, the drone's rotor span, a
        /// headquarters whose frontage exceeds its depth. The loader's quarter turn
        /// never fires for those, so the exporter's -Z front is still on -Z and the
        /// quarter turn is the table's to make.
        /// </summary>
        /// <remarks>
        /// Which of the two constants a model wants is decided by its proportions,
        /// so a model that changes shape enough to cross that line flips between
        /// them — the drone is 2.64 m across and 2.36 m long, which is not a wide
        /// margin. <c>--inspect-models</c> reports the axis each model ends up
        /// longest along, which is what changes when the alignment does, and
        /// <c>tools/model_facing.py --table</c> says which side of the line every
        /// slot is on and where its muzzle, nose or bucket ends up.
        /// </remarks>
        private const float AcrossYaw = -90f;

        private static readonly Dictionary<int, ModelSpec> Specs = new()
        {
            // ---- Σοβιετικοί: heavy armour, industrial structures ----
            [CacheKeyOf(Faction.Soviet, UnitKind.Infantry)] = new("Generated", "soviet_infantry.glb", 1.86f, AcrossYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Tank)] = new("Generated", "soviet_tank.glb", 6.4f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Artillery)] = new("Generated", "soviet_artillery.glb", 6.0f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.AntiAir)] = new("Generated", "soviet_antiair.glb", 4.2f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Aircraft)] = new("Generated", "soviet_aircraft.glb", 11f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.RocketArtillery)] = new("Generated", "soviet_katyusha.glb", 6.0f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Harvester)] = new("Generated", "soviet_harvester.glb", 6.4f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.ElectroPrototype)] = new("Generated", "soviet_electro.glb", 6.6f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Commissar)] = new("Generated", "soviet_commissar.glb", 1.9f, AcrossYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.CommandCentre)] = new("Generated", "soviet_hq.glb", 20f, AcrossYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.PowerPlant)] = new("Generated", "soviet_power.glb", 12f, AcrossYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Factory)] = new("Generated", "soviet_factory.glb", 16f, AcrossYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.DesignBureau)] = new("Generated", "soviet_bureau.glb", 14f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.NuclearPlant)] = new("Generated", "soviet_nuclear.glb", 20f, GeneratedYaw),

            // The two emplacements. `gun` and `aa` are the defensive structures; the mobile
            // anti-aircraft mount owns `soviet_antiair.glb`, and the two files are different
            // models of different things.
            //
            // `GeneratedYaw` for all six because the generator authors each one with its barrel
            // along +Y and its revetment deeper than it is wide, so the loader's own quarter turn
            // fires and lands that barrel on +X — the forward the simulation means. Verified
            // rather than assumed: `tools/model_facing.py --table` measures the `barrel` part of
            // every one of these and prints 0°/0 m forward.
            [CacheKeyOf(Faction.Soviet, UnitKind.GunEmplacement)] = new("Generated", "soviet_gun.glb", 11.5f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.AntiAirEmplacement)] = new("Generated", "soviet_aa.glb", 11.5f, GeneratedYaw),

            // The radar station, which is a mast with a dish on it rather than a position.
            // `GeneratedYaw` like the emplacements beside it and for the same reason: the
            // generator authors it front-first along +Y with its compound deeper than it is
            // wide, so the loader's own quarter turn puts its front on +X. Its dish is named
            // `radar`, which is the part the client turns — and stops turning when the grid
            // cannot run the set.
            [CacheKeyOf(Faction.Soviet, UnitKind.RadarStation)] = new("Generated", "soviet_radar.glb", 13f, GeneratedYaw),

            // ---- Κινέζοι: light hulls, mass-produced patterns ----
            [CacheKeyOf(Faction.Chinese, UnitKind.Infantry)] = new("Generated", "chinese_infantry.glb", 1.92f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Tank)] = new("Generated", "chinese_tank.glb", 5.4f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Artillery)] = new("Generated", "chinese_artillery.glb", 5.0f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.AntiAir)] = new("Generated", "chinese_antiair.glb", 3.8f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Aircraft)] = new("Generated", "chinese_aircraft.glb", 10f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Harvester)] = new("Generated", "chinese_harvester.glb", 5.8f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.RobotInfantry)] = new("Generated", "chinese_robot.glb", 1.9f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Drone)] = new("Generated", "chinese_drone.glb", 3.0f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.CommandCentre)] = new("Generated", "chinese_hq.glb", 18f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.PowerPlant)] = new("Generated", "chinese_power.glb", 11f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Factory)] = new("Generated", "chinese_factory.glb", 15f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.DesignBureau)] = new("Generated", "chinese_bureau.glb", 13f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.NuclearPlant)] = new("Generated", "chinese_nuclear.glb", 18f, AcrossYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.GunEmplacement)] = new("Generated", "chinese_gun.glb", 11f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.AntiAirEmplacement)] = new("Generated", "chinese_aa.glb", 11f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.RadarStation)] = new("Generated", "chinese_radar.glb", 13f, GeneratedYaw),

            // ---- Δυτικοί: the most refined vehicles and buildings ----
            [CacheKeyOf(Faction.Western, UnitKind.Infantry)] = new("Generated", "western_infantry.glb", 1.82f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Tank)] = new("Generated", "western_tank.glb", 6.6f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Artillery)] = new("Generated", "western_artillery.glb", 6.2f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.AntiAir)] = new("Generated", "western_antiair.glb", 4.0f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Aircraft)] = new("Generated", "western_aircraft.glb", 12f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Harvester)] = new("Generated", "western_harvester.glb", 6.8f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Mercenary)] = new("Generated", "western_mercenary.glb", 1.9f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.StealthRecon)] = new("Generated", "western_stalker.glb", 2.0f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.CommandCentre)] = new("Generated", "western_hq.glb", 22f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.PowerPlant)] = new("Generated", "western_power.glb", 13f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Factory)] = new("Generated", "western_factory.glb", 17f, AcrossYaw),
            [CacheKeyOf(Faction.Western, UnitKind.DesignBureau)] = new("Generated", "western_bureau.glb", 15f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.NuclearPlant)] = new("Generated", "western_nuclear.glb", 22f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.GunEmplacement)] = new("Generated", "western_gun.glb", 12f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.AntiAirEmplacement)] = new("Generated", "western_aa.glb", 12f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.RadarStation)] = new("Generated", "western_radar.glb", 13f, GeneratedYaw),
        };

        private static int CacheKeyOf(Faction faction, UnitKind kind) => ((int)faction << 8) | (int)kind;

        public static bool TryGet(Faction faction, UnitKind kind, out ModelSpec spec)
            => Specs.TryGetValue(CacheKeyOf(faction, kind), out spec);

        /// <summary>Builds the import options this spec describes.</summary>
        public ModelImportOptions ToImportOptions()
            => new(TargetSizeMetres, YawOffsetDegrees, FlipNormals: false, MeshNameContains);

        /// <summary>
        /// Lists every configured model slot with its import options, so
        /// diagnostics can validate exactly what the game will load.
        /// </summary>
        public static IEnumerable<(Faction Faction, UnitKind Kind, string RelativePath, ModelImportOptions Options)> EnumerateSpecs()
        {
            foreach ((int key, ModelSpec spec) in Specs)
            {
                Faction faction = (Faction)(key >> 8);
                UnitKind kind = (UnitKind)(key & 0xFF);

                yield return (faction, kind, $"{spec.Folder}/{spec.FileName}", spec.ToImportOptions());
            }
        }
    }

    public void Dispose()
    {
        foreach (InstancedRenderer.Mesh mesh in _owned)
        {
            mesh.Dispose();
        }

        _owned.Clear();
        _cache.Clear();
        _partsCache.Clear();
        _wholeCache.Clear();
    }
}
