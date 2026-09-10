using MiVic.Core.Sim;
using MiVic.Game.Rendering;
using MiVic.Game.Rendering.Gltf;

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

    private static int CacheKey(Faction faction, UnitKind kind) => ((int)faction << 8) | (int)kind;

    private InstancedRenderer.Mesh LoadOrBuild(Faction faction, UnitKind kind)
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
                    _loaded.Add($"{spec.Folder}/{spec.FileName}");
                    return imported;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
                {
                    _failed.Add($"{spec.Folder}/{spec.FileName}: {exception.Message}");
                }
            }
            else
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
        _ => MeshBuilder.Box(1f, 1f, 1f),
    };

    /// <summary>Where a role's model lives and how to fit it into the world.</summary>
    /// <param name="Folder">Faction subfolder under the model root.</param>
    /// <param name="FileName">Model file name.</param>
    /// <param name="TargetSizeMetres">Length of the longest axis after import.</param>
    /// <param name="YawOffsetDegrees">
    /// Extra rotation after automatic forward alignment. Tanks are authored
    /// facing -X, so they need a half turn to face +X like everything else.
    /// </param>
    /// <param name="MeshNameContains">Optional mesh-name filter for modular assets.</param>
    private readonly record struct ModelSpec(
        string Folder,
        string FileName,
        float TargetSizeMetres,
        float YawOffsetDegrees = 0f,
        string? MeshNameContains = null)
    {
        /// <summary>Tanks are modelled nose-first along -X; everything else follows glTF's -Z.</summary>
        private const float TankYaw = 180f;

        /// <summary>
        /// Infantry is modelled with the arm span along X and the body depth
        /// along Z, so it has to be turned a quarter turn to face +X.
        /// </summary>
        private const float InfantryYaw = -90f;

        /// <summary>
        /// The three aircraft are different models with different authoring
        /// orientations: two face -X and one faces +Z. Measured with
        /// <c>--inspect-models</c>, which reports the nose angle of each import.
        /// </summary>
        private const float AircraftYawSovietAndChinese = 180f;

        private const float AircraftYawWestern = 90f;

        /// <summary>
        /// The generated models are built nose-first along Blender's +Y, which the
        /// glTF exporter turns into -Z. The loader then aligns the longest
        /// horizontal axis to +X, so the nose lands 180° out and needs one half
        /// turn — the same correction the borrowed tank models needed, for a
        /// different reason. Measured with <c>--inspect-models</c>.
        /// </summary>
        private const float GeneratedYaw = 180f;

        private static readonly Dictionary<int, ModelSpec> Specs = new()
        {
            // ---- Σοβιετικοί: heavy armour, industrial structures ----
            [CacheKeyOf(Faction.Soviet, UnitKind.Infantry)] = new("Soviet", "soldier.glb", 1.9f, InfantryYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Tank)] = new("Generated", "soviet_tank.glb", 6.4f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.Artillery)] = new("Soviet", "tank_medium.glb", 6.0f, TankYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.AntiAir)] = new("Soviet", "turret.glb", 4.2f),
            [CacheKeyOf(Faction.Soviet, UnitKind.Aircraft)] = new("Soviet", "aircraft.glb", 11f, AircraftYawSovietAndChinese),
            [CacheKeyOf(Faction.Soviet, UnitKind.CommandCentre)] = new("Generated", "soviet_hq.glb", 20f, GeneratedYaw),
            [CacheKeyOf(Faction.Soviet, UnitKind.PowerPlant)] = new("Soviet", "hq.glb", 12f),
            [CacheKeyOf(Faction.Soviet, UnitKind.Factory)] = new("Soviet", "hq.glb", 16f),
            [CacheKeyOf(Faction.Soviet, UnitKind.DesignBureau)] = new("Soviet", "hq.glb", 14f),

            // ---- Κινέζοι: light hulls, mass-produced patterns ----
            [CacheKeyOf(Faction.Chinese, UnitKind.Infantry)] = new("Chinese", "soldier.glb", 1.9f, InfantryYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Tank)] = new("Generated", "chinese_tank.glb", 5.4f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.Artillery)] = new("Chinese", "tank_apc.glb", 5.0f, TankYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.AntiAir)] = new("Chinese", "turret.glb", 3.8f),
            [CacheKeyOf(Faction.Chinese, UnitKind.Aircraft)] = new("Chinese", "aircraft.glb", 10f, AircraftYawSovietAndChinese),
            [CacheKeyOf(Faction.Chinese, UnitKind.CommandCentre)] = new("Generated", "chinese_hq.glb", 18f, GeneratedYaw),
            [CacheKeyOf(Faction.Chinese, UnitKind.PowerPlant)] = new("Chinese", "hq.glb", 11f),
            [CacheKeyOf(Faction.Chinese, UnitKind.Factory)] = new("Chinese", "hq.glb", 15f),
            [CacheKeyOf(Faction.Chinese, UnitKind.DesignBureau)] = new("Chinese", "hq.glb", 13f),

            // ---- Δυτικοί: the most refined vehicles and buildings ----
            [CacheKeyOf(Faction.Western, UnitKind.Infantry)] = new("Western", "soldier.glb", 1.9f, InfantryYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Tank)] = new("Generated", "western_tank.glb", 6.6f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.Artillery)] = new("Western", "tank_heavy.glb", 6.2f, TankYaw),
            [CacheKeyOf(Faction.Western, UnitKind.AntiAir)] = new("Western", "turret.glb", 4.0f),
            [CacheKeyOf(Faction.Western, UnitKind.Aircraft)] = new("Western", "aircraft.glb", 12f, AircraftYawWestern),
            [CacheKeyOf(Faction.Western, UnitKind.CommandCentre)] = new("Generated", "western_hq.glb", 22f, GeneratedYaw),
            [CacheKeyOf(Faction.Western, UnitKind.PowerPlant)] = new("Western", "hq.glb", 13f),
            [CacheKeyOf(Faction.Western, UnitKind.Factory)] = new("Western", "hq.glb", 17f),
            [CacheKeyOf(Faction.Western, UnitKind.DesignBureau)] = new("Western", "hq.glb", 15f),
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
    }
}
