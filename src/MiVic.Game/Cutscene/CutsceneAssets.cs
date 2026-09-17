using MiVic.Game.Cutscene.Skinning;
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
/// <param name="Skin">
/// Set when the asset is a skinned mesh rather than a set of rigid parts. A rigged figure
/// is a different kind of thing: its geometry is bound to a skeleton and its pose comes
/// from a clip, so it has no parts to accumulate transforms through — <see cref="Parts"/>
/// is empty for it and the director draws it through <c>SkinnedEffect</c> instead.
/// </param>
public readonly record struct CutsceneModel(
    string Asset,
    CutscenePart[] Parts,
    Matrix ModelTransform,
    SkinnedModel? Skin = null);

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
    private readonly List<SkinnedModel> _skinned = [];
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

        // Skinned or not cannot be told from the name, so it is asked. The cost is one
        // extra parse for the assets that are not skinned — and only once, because the
        // answer is cached with the model, and a cutscene loads two or three assets in a
        // process. The parts path below stays the one that reports a real failure.
        try
        {
            SkinnedModel candidate = SkinnedModel.Load(_device, path);
            if (candidate.JointCount > 0)
            {
                // SkinnedEffect has no vertex-colour channel, so a rigged model is coloured
                // by a texture exactly as a rigid one is. The same naming convention applies:
                // one map for the face, one for the cloth, one for the metal.
                Texture2D? rigFace = LoadTexture(asset, string.Empty);
                Texture2D? rigCloth = LoadTexture(asset, "_cloth");
                Texture2D? rigMetal = LoadTexture(asset, "_metal");

                foreach (SkinnedMeshPart part in candidate.Parts)
                {
                    part.Texture = part.Name switch
                    {
                        var n when IsMetal(n) => rigMetal ?? rigFace,
                        var n when IsCloth(n) => rigCloth ?? rigFace,
                        _ => rigFace,
                    };
                }

                _skinned.Add(candidate);
                var rigged = new CutsceneModel(
                    asset, [], Matrix.Identity, candidate);
                _cache[asset] = rigged;
                return rigged;
            }
        }
        catch (Exception)
        {
            // Not a skinned file. Fall through to the parts loader.
        }

        ModelData data = GltfLoader.LoadModel(path, Raw);
        Texture2D? face = LoadTexture(asset, string.Empty);
        Texture2D? cloth = LoadTexture(asset, "_cloth");
        Texture2D? metal = LoadTexture(asset, "_metal");
        var parts = new CutscenePart[data.Parts.Count];

        for (int i = 0; i < parts.Length; i++)
        {
            ModelPart part = data.Parts[i];

            // A figure is not one material. A face is one of a kind and a tunic is
            // a surface that repeats, so they are two images, and which part gets
            // which is decided by what the part is rather than by the file.
            Texture2D? texture = part.Name switch
            {
                var n when IsMetal(n) => metal ?? face,
                var n when IsCloth(n) => cloth ?? face,
                _ => face,
            };
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
    /// <summary>Whether a part is metal, and so samples a surface with no colour in it.</summary>
    private static bool IsMetal(string part) =>
        part.StartsWith("Board", StringComparison.Ordinal)
        || part.StartsWith("Button", StringComparison.Ordinal)
        || part.StartsWith("CollarEdge", StringComparison.Ordinal)
        || part.StartsWith("Star", StringComparison.Ordinal)
        || part.StartsWith("Ribbon", StringComparison.Ordinal)
        || part.StartsWith("Pipe", StringComparison.Ordinal);

    /// <summary>Whether a part is made of cloth, and so samples the cloth map.</summary>
    private static bool IsCloth(string part) =>
        part.StartsWith("Tunic", StringComparison.Ordinal)
        || part.StartsWith("Collar", StringComparison.Ordinal)
        || part.StartsWith("Belt", StringComparison.Ordinal)
        || part.StartsWith("Arm", StringComparison.Ordinal)
        || part.StartsWith("Forearm", StringComparison.Ordinal)
        || part.StartsWith("Leg", StringComparison.Ordinal)
        || part.StartsWith("Shin", StringComparison.Ordinal)
        || part == "Body";

    private Texture2D? LoadTexture(string asset, string suffix)
    {
        string path = Path.Combine(_baseDirectory, ModelRoot, Folder, asset + suffix + ".png");

        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        Texture2D flat = Texture2D.FromStream(_device, stream);
        Texture2D texture = TextureTools.MakeMipmapped(_device, flat);
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
