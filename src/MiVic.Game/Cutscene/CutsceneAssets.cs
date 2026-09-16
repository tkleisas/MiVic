using MiVic.Game.Rendering;
using MiVic.Game.Rendering.Gltf;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Cutscene;

/// <summary>
/// One part of a cutscene model, with the transform that places it inside its parent.
/// <para>
/// The same shape as <c>ModelCatalog.PartMesh</c> and deliberately not that type: a unit is
/// looked up by faction and role, and a cutscene asset is looked up by a name from the scene
/// file. Pushing a set or a personality into <c>UnitKind</c> would put something the
/// simulation has no opinion about into the enum the simulation is built on.
/// </para>
/// </summary>
/// <param name="Name">Part name from the model, which is what a pose keys on.</param>
/// <param name="Mesh">Geometry to draw.</param>
/// <param name="LocalTransform">Where the part sits inside its parent.</param>
/// <param name="ParentIndex">Index of the enclosing part, or -1 for a root part.</param>
public readonly record struct CutscenePart(
    string Name,
    InstancedRenderer.Mesh Mesh,
    Matrix LocalTransform,
    int ParentIndex);

/// <summary>A loaded cutscene model: its parts, and the transform that places the whole thing.</summary>
/// <param name="Asset">The name it was loaded by.</param>
/// <param name="Parts">Every part, parents before children.</param>
/// <param name="ModelTransform">Applied outside the parts, after their own transforms.</param>
public readonly record struct CutsceneModel(
    string Asset,
    CutscenePart[] Parts,
    Matrix ModelTransform);

/// <summary>
/// Loads the models a cutscene stands on — sets and personalities — by name.
/// <para>
/// <b>Loaded raw, at the scale they were authored in.</b> A unit's model is normalised to a
/// target size and centred on its own bounds before it is drawn, because a tank on a map is
/// a thing of a certain footprint. A set is a room measured in metres and a personality is a
/// man measured in metres, and both are placed by the scene file in the same space the camera
/// moves through, so any normalisation would move the desk away from the man standing at it.
/// </para>
/// </summary>
public sealed class CutsceneAssets : IDisposable
{
    /// <summary>Where generated models live, the same folder the units come from.</summary>
    private const string ModelRoot = Data.ModelCatalog.ModelRoot;

    private const string Folder = "Generated";

    /// <summary>
    /// Importer options that change nothing: no scaling, no rotation to the longest axis, no
    /// re-centring. A cutscene asset arrives in its own coordinates.
    /// </summary>
    private static readonly ModelImportOptions Raw = new(
        TargetSizeMetres: 0f,
        AlignLongestHorizontalAxis: false,
        CentreOnOwnBounds: false);

    private readonly InstancedRenderer _renderer;
    private readonly GraphicsDevice _device;
    private readonly string _baseDirectory;
    private readonly Dictionary<string, CutsceneModel> _cache = [];
    private readonly List<InstancedRenderer.Mesh> _owned = [];
    private readonly List<Texture2D> _textures = [];

    public CutsceneAssets(InstancedRenderer renderer, GraphicsDevice device, string baseDirectory)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
    }

    /// <summary>
    /// Loads a model by asset name, or returns the one already loaded. A missing file is an
    /// <see cref="InvalidDataException"/> carrying the path: a scene that names a set nobody
    /// generated is a content mistake, and it should say which name is wrong.
    /// </summary>
    public CutsceneModel Load(string asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);

        if (_cache.TryGetValue(asset, out CutsceneModel cached))
        {
            return cached;
        }

        string path = Path.Combine(_baseDirectory, ModelRoot, Folder, asset + ".glb");

        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Cutscene asset '{asset}' is not in the build: no file at {path}.");
        }

        ModelData data = GltfLoader.LoadModel(path, Raw);
        Texture2D? texture = LoadTexture(asset);
        var parts = new CutscenePart[data.Parts.Count];

        for (int i = 0; i < parts.Length; i++)
        {
            ModelPart part = data.Parts[i];
            InstancedRenderer.Mesh mesh = _renderer.CreateMesh(part.Mesh, texture);
            _owned.Add(mesh);

            parts[i] = new CutscenePart(part.Name, mesh, part.LocalTransform, part.ParentIndex);
        }

        var model = new CutsceneModel(asset, parts, data.ModelTransform);
        _cache[asset] = model;
        return model;
    }

    /// <summary>
    /// The model's texture, if one was painted for it, or null if it is drawn in
    /// flat material colour.
    /// <para>
    /// Found by the model's own name rather than declared by the glTF material. The
    /// generators write geometry and vertex colours and nothing else, and a material
    /// graph in a generator file is a second place for the art to be, and one that
    /// cannot be read without opening Blender. A file next to the model with the
    /// model's name is a rule that can be checked by looking in the folder.
    /// </para>
    /// </summary>
    private Texture2D? LoadTexture(string asset)
    {
        string path = Path.Combine(_baseDirectory, ModelRoot, Folder, asset + ".png");

        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        Texture2D texture = Texture2D.FromStream(_device, stream);
        _textures.Add(texture);
        return texture;
    }

    /// <summary>
    /// The world transform of every part of a model, accumulated through its parents.
    /// <para>
    /// Row-vector convention, exactly as the unit renderer uses it: a vertex travels through
    /// the part's own transform, then its ancestors', then the model's. <paramref name="partLocals"/>
    /// is supplied by the caller so a pose can be folded in before accumulation — a posed part
    /// is <c>pose * LocalTransform</c>, which rotates it about its own origin, and a generated
    /// limb hangs below its origin, so that origin is the joint.
    /// </para>
    /// </summary>
    /// <param name="model">The model to place.</param>
    /// <param name="entityTransform">Where the whole model stands in the world.</param>
    /// <param name="partLocals">One local transform per part, already posed.</param>
    /// <param name="world">Receives one world transform per part.</param>
    public static void Accumulate(
        CutsceneModel model,
        Matrix entityTransform,
        ReadOnlySpan<Matrix> partLocals,
        Span<Matrix> world)
    {
        for (int i = 0; i < model.Parts.Length; i++)
        {
            CutscenePart part = model.Parts[i];
            Matrix accumulated = partLocals[i];

            for (int parent = part.ParentIndex; parent >= 0 && parent < model.Parts.Length; parent = model.Parts[parent].ParentIndex)
            {
                accumulated *= partLocals[parent];
            }

            world[i] = accumulated * model.ModelTransform * entityTransform;
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
