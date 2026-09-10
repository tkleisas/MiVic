using MiVic.Core.Numerics;
using MiVic.Core.Terrain;
using MiVic.Game.Rendering.Gltf;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering.Props;

/// <summary>
/// Draws the woodland the simulation's surface layer carries.
/// <para>
/// Trees are the one thing on the map that belongs to no unit: they have no
/// faction, no owner and no slot in the entity store, and <c>ModelCatalog</c> is
/// keyed by <c>(Faction, UnitKind)</c>, so a tree has nowhere to live in it. They
/// are loaded here instead, as their own meshes with their own instance arrays —
/// which is also what makes six species affordable, since each one is a single
/// draw call for every tree of that species on the map.
/// </para>
/// <para>
/// A wood is decoration over walkable ground. The forest surface costs armour
/// speed and gives infantry cover, and these trees are drawn on top of it without
/// being solid: units drive through them, and that is the correct behaviour for
/// this game rather than an omission.
/// </para>
/// </summary>
public sealed class ForestRenderer : IDisposable
{
    /// <summary>The generated tree meshes, in the order they are picked between.</summary>
    private static readonly string[] Species =
    [
        "tree_conifer_tall.glb",
        "tree_conifer_broad.glb",
        "tree_conifer_slim.glb",
        "tree_broadleaf_wide.glb",
        "tree_broadleaf_tall.glb",
        "tree_broadleaf_slim.glb",
    ];

    /// <summary>Most trees one forest cell can carry, which is what sizes the placement.</summary>
    private const int MaxTreesPerCell = 3;
    /// <summary>
    /// How far a tree is pushed into the ground, in metres. The base is placed at
    /// the height sampled under the trunk, so on a slope the trunk's own footprint
    /// would hang over the downhill side; a few centimetres of trunk buried in the
    /// dirt costs nothing at this scale and removes the gap entirely.
    /// </summary>
    private const float SinkMetres = 0.20f;

    private static readonly string ModelFolder = Path.Combine("Content", "Models", "Generated");

    private readonly InstancedRenderer _renderer;
    private readonly List<string> _loaded = [];
    private readonly List<string> _missing = [];

    private InstancedRenderer.Mesh[] _meshes = [];
    private float[] _heights = [];

    /// <summary>
    /// One instance array per shape, each exactly as long as the trees it holds.
    /// Per shape rather than one array for the wood, because an instanced draw call
    /// takes a contiguous run: trees of the same species have to be gathered
    /// together, and that is also what makes a wood of several hundred trees six
    /// draw calls instead of one per tree.
    /// </summary>
    private InstanceData[]?[] _instances = [];

    private int[] _counts = [];

    public ForestRenderer(InstancedRenderer renderer, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _renderer = renderer;
        Load(baseDirectory);
    }

    /// <summary>How many tree shapes were loaded, which is also the forest's draw-call count.</summary>
    public int ShapeCount => _meshes.Length;

    /// <summary>
    /// The shapes that loaded, each with its triangle count and its height in metres.
    /// The height is measured off the imported mesh rather than read from the model
    /// table, because it is what the wind is weighted against: a tree that imported
    /// at the wrong size would sway from halfway up its trunk, and this is where that
    /// would be visible.
    /// </summary>
    public IReadOnlyList<string> LoadedShapes => _loaded;

    /// <summary>Trees standing on the map, or zero before the first placement.</summary>
    public int TreeCount { get; private set; }

    /// <summary>Cells of the surface layer that carry woodland.</summary>
    public int ForestCells { get; private set; }

    /// <summary>
    /// A hash of every tree's place, size, facing and species.
    /// <para>
    /// Placement being deterministic is a claim that cannot be checked by looking at
    /// a screenshot — two runs of the same map look alike whether or not they are the
    /// same wood — so it is compressed into a number the fixtures can print. It is
    /// the whole of the guarantee: the same map grows the same wood, and this says so
    /// without a test having to reach inside the renderer.
    /// </para>
    /// </summary>
    public ulong PlacementDigest { get; private set; }

    /// <summary>One line describing what is drawn, for the fixtures and the report.</summary>
    public string Summary => _meshes.Length == 0
        ? $"{_loaded.Count + _missing.Count} tree shapes unavailable"
        : $"{_meshes.Length} tree shapes, {TreeCount} trees over {ForestCells} forest cells" +
          (_missing.Count > 0 ? $", {_missing.Count} missing" : string.Empty);

    /// <summary>
    /// Loads each species and bakes it down to a single grounded mesh.
    /// <para>
    /// A tree is imported as a model and drawn as one mesh. It has no turret to
    /// traverse and no wheel to spin — the only thing that moves on it is the wind,
    /// and the shader does that to the whole thing — so keeping a node hierarchy
    /// would buy nothing and cost a matrix multiply per tree per frame. What the
    /// bake buys is that the mesh's origin is its base once the model transform is
    /// in, which is exactly what the wind's height weighting is measured against.
    /// </para>
    /// </summary>
    private void Load(string baseDirectory)
    {
        // No rescaling: the trees are modelled in metres, at the size they are
        // meant to stand on the map, and a fit-to-target-size pass would resize a
        // tree by its width instead of its height the moment one grew wider than
        // it was tall.
        ModelImportOptions options = new(
            TargetSizeMetres: 0f,
            AlignLongestHorizontalAxis: false);

        List<InstancedRenderer.Mesh> meshes = [];
        List<float> heights = [];

        foreach (string file in Species)
        {
            string path = Path.Combine(baseDirectory, ModelFolder, file);

            if (!File.Exists(path))
            {
                _missing.Add(file);
                continue;
            }

            try
            {
                ModelData model = GltfLoader.LoadModel(path, options);
                MeshData baked = ModelBake.Flatten(model, out float height);

                meshes.Add(_renderer.CreateMesh(baked));
                heights.Add(height);
                _loaded.Add($"{file} ({baked.PrimitiveCount} triangles, {height:0.0} m)");
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
            {
                _missing.Add($"{file}: {exception.Message}");
            }
        }

        _meshes = [.. meshes];
        _heights = [.. heights];
        _instances = new InstanceData[]?[_meshes.Length];
        _counts = new int[_meshes.Length];
    }

    /// <summary>
    /// Places the trees the surface layer asks for.
    /// <para>
    /// Deterministic from the cell coordinates alone: the offset within the cell,
    /// the yaw, the size and which species stands there are all read out of an
    /// integer hash of the cell. Nothing here consults <see cref="Random"/> or the
    /// clock, so two runs on one seed grow the identical wood, in the identical
    /// places, facing the identical way — which is the only property that makes a
    /// screenshot of a map mean anything.
    /// </para>
    /// <para>
    /// Three trees to a cell at most, spread off the cell's centre and off its
    /// edges: the surface layer's lattice is nine metres across, and trees planted
    /// on its corners would draw the grid on the ground in timber.
    /// </para>
    /// </summary>
    public void Rebuild(HeightMap map, TerrainLayer terrain)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(terrain);

        TreeCount = 0;
        ForestCells = 0;
        PlacementDigest = 0xCBF29CE484222325UL;
        Array.Clear(_counts);

        if (_meshes.Length == 0)
        {
            return;
        }

        // One pass to count, one to fill. Counting first is what lets each instance
        // array be exactly the size of what it holds, since the trees are divided
        // between six shapes by a hash that is only known tree by tree.
        int[] wanted = new int[_meshes.Length];

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                if (terrain.TypeAtCell(x, z) != TerrainType.Forest)
                {
                    continue;
                }

                ForestCells++;
                int trees = TreesIn(x, z);

                for (int tree = 0; tree < trees; tree++)
                {
                    wanted[SpeciesAt(x, z, tree)]++;
                }
            }
        }

        for (int i = 0; i < _meshes.Length; i++)
        {
            // Kept when it is already big enough. A rebuild follows the terrain, and
            // terrain changes: replacing six arrays every time weather control moves
            // a boundary would be six allocations to throw away a frame later.
            if (_instances[i] is null || _instances[i]!.Length < wanted[i])
            {
                _instances[i] = new InstanceData[wanted[i]];
            }
        }

        float cellMetres = terrain.CellSizeMm * WorldPos.MmToMetres;
        float originMetres = terrain.OriginMm * WorldPos.MmToMetres;

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                if (terrain.TypeAtCell(x, z) != TerrainType.Forest)
                {
                    continue;
                }

                int trees = TreesIn(x, z);

                for (int tree = 0; tree < trees; tree++)
                {
                    uint salt = (uint)tree * 4u;
                    int species = SpeciesAt(x, z, tree);

                    // Spread across the cell but kept clear of its edges, so a wood
                    // reads as trees standing in it rather than as a grid of trees
                    // marking it out.
                    float fx = 0.14f + (0.72f * Random01(x, z, SaltOffsetX + salt));
                    float fz = 0.14f + (0.72f * Random01(x, z, SaltOffsetZ + salt));

                    float worldX = originMetres + ((x + fx) * cellMetres);
                    float worldZ = originMetres + ((z + fz) * cellMetres);

                    float ground = map.SampleHeightMm(
                        (int)(worldX * WorldPos.MmPerMetre),
                        (int)(worldZ * WorldPos.MmPerMetre)) * WorldPos.MmToMetres;

                    float scale = MinScale + ((MaxScale - MinScale) * Random01(x, z, SaltScale + salt));
                    float yaw = MathHelper.TwoPi * Random01(x, z, SaltYaw + salt);

                    // Row-vector convention: scaled, then turned, then stood on the
                    // ground. The mesh is already grounded at its base, so a tree's
                    // matrix is a placement and nothing else.
                    Matrix transform =
                        Matrix.CreateScale(scale) *
                        Matrix.CreateRotationY(yaw) *
                        Matrix.CreateTranslation(worldX, ground - SinkMetres, worldZ);

                    _instances[species]![_counts[species]++] = new InstanceData(transform, Tint(x, z, salt));
                    TreeCount++;

                    // FNV-1a over the numbers that place the tree. The tint is left
                    // out: it is drawn from the same hash, so it would only repeat
                    // what is already folded in here.
                    PlacementDigest = Fold(Fold(Fold(PlacementDigest, (uint)species), (uint)((x * 256) + z)), worldX);
                    PlacementDigest = Fold(Fold(Fold(PlacementDigest, worldZ), scale), yaw);
                }
            }
        }
    }

    /// <summary>Draws every tree, one instanced call per species.</summary>
    /// <param name="strategicZoom">
    /// True when the camera is far enough out that trees are specks. A wood at that
    /// zoom is already legible from the ground colour — the surface layer paints it
    /// a darker green than the grass around it — and several hundred trunks at four
    /// pixels each add nothing to it but a fringe of noise along every treeline.
    /// </param>
    /// <returns>
    /// What was submitted: the draw calls issued and the instances in them, so the
    /// client's own totals include the wood. A forest that draws nothing is still
    /// part of the frame, and a report that quietly left it out would be measuring a
    /// different game.
    /// </returns>
    public (int DrawCalls, int Instances) Draw(bool strategicZoom)
    {
        if (strategicZoom)
        {
            return (0, 0);
        }

        int calls = 0;
        int instances = 0;

        for (int i = 0; i < _meshes.Length; i++)
        {
            if (_counts[i] == 0 || _instances[i] is not { } batch)
            {
                continue;
            }

            // Per mesh, because the sway is weighted by height above the base: a
            // seven-metre tree and a ten-metre one drawn against the same height
            // would bend in different places.
            _renderer.BeginFoliage(_heights[i]);
            _renderer.Draw(_meshes[i], batch, _counts[i]);

            calls++;
            instances += _counts[i];
        }

        if (calls > 0)
        {
            _renderer.EndFoliage();
        }

        return (calls, instances);
    }

    /// <summary>How many trees stand in a cell: two, and three where the hash says so.</summary>
    private static int TreesIn(int x, int z)
        => 2 + (int)(Mix(x, z, SaltCount) % (MaxTreesPerCell - 1));

    /// <summary>Which shape grows in a cell, by its own hash, so a wood is mixed.</summary>
    private int SpeciesAt(int x, int z, int tree)
        => (int)(Mix(x, z, SaltSpecies + (uint)tree * 17u) % (uint)_meshes.Length);

    /// <summary>
    /// The shade of this tree, as a multiplier on its material colour rather than a
    /// colour of its own: a wood is a hundred greens, and the geometry says which
    /// family each one belongs to.
    /// </summary>
    private static Vector4 Tint(int x, int z, uint salt)
    {
        float shade = 0.84f + (0.32f * Random01(x, z, SaltShade + salt));

        // Warmer or cooler as well as lighter or darker, so the variation does not
        // read as one wood shaded by distance from the sun.
        float warmth = (Random01(x, z, SaltWarmth + salt) - 0.5f) * 0.20f;

        return new Vector4(
            Math.Clamp(shade * (1f + warmth), 0.4f, 1.6f),
            Math.Clamp(shade, 0.4f, 1.6f),
            Math.Clamp(shade * (1f - warmth), 0.4f, 1.6f),
            1f);
    }

    /// <summary>How big a tree is, as a fraction of the shape it was built at.</summary>
    private const float MinScale = 0.84f;

    private const float MaxScale = 1.18f;

    /// <summary>A deterministic 0..1 value for one tree in one cell.</summary>
    private static float Random01(int x, int z, uint salt) => (Mix(x, z, salt) >> 8) * (1f / 16777216f);

    /// <summary>Folds one float into the placement digest.</summary>
    private static ulong Fold(ulong digest, float value)
        => (digest ^ BitConverter.SingleToUInt32Bits(value)) * 0x100000001B3UL;

    /// <summary>Folds one integer into the placement digest.</summary>
    private static ulong Fold(ulong digest, uint value)
        => (digest ^ value) * 0x100000001B3UL;

    // Each use gets its own salt, so the offset, the yaw, the size and the species
    // of the same tree are four independent numbers rather than one number read
    // four ways.
    private const uint SaltCount = 0x51ED2701;
    private const uint SaltSpecies = 0x2C1B3C6Du;
    private const uint SaltOffsetX = 0x9E3779B9u;
    private const uint SaltOffsetZ = 0x85EBCA6Bu;
    private const uint SaltYaw = 0xC2B2AE35u;
    private const uint SaltScale = 0x27D4EB2Fu;
    private const uint SaltShade = 0x165667B1u;
    private const uint SaltWarmth = 0xD3A2646Cu;

    /// <summary>
    /// A splitmix-style integer hash of a lattice coordinate and a salt.
    /// <para>
    /// Here rather than in the simulation, and deliberately not
    /// <see cref="Random"/>: a wood is presentation, so it must not be able to
    /// reach the state hash — but it must also be reproducible, because a
    /// screenshot of a map that grows different trees each run is a screenshot of
    /// nothing in particular. A plain integer mix gives both: no state, no clock,
    /// and the same wood on every machine.
    /// </para>
    /// </summary>
    private static uint Mix(int x, int z, uint salt)
    {
        uint value = ((uint)x * 0x9E3779B9u) + ((uint)z * 0x85EBCA6Bu) + (salt * 0xC2B2AE35u);

        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;

        return value;
    }

    public void Dispose()
    {
        foreach (InstancedRenderer.Mesh mesh in _meshes)
        {
            mesh.Dispose();
        }

        _meshes = [];
        _instances = [];
        _counts = [];
    }
}
