using System.Runtime.InteropServices;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// The ghost of something the player is about to place: a mesh, where it would stand, and
/// whether the site would be accepted.
/// <para>
/// There is one of these because there is one question. A player choosing a site needs to
/// see the footprint they are about to commit to and whether the game will take it, and
/// until now the client had no way to say either: a site the simulation refused produced
/// nothing at all on screen, which is indistinguishable from a click that never arrived.
/// The ghost is the answer, and it is coloured rather than merely drawn — a red footprint
/// where the player expected green teaches the rule, which a line of text alone does not.
/// </para>
/// <para>
/// It takes a mesh and a transform rather than knowing anything about bridges, because
/// structure placement needs exactly this: a footprint on the ground, a validity flag, and
/// some way to see both before paying for them. Nothing here reads or writes the
/// simulation — the caller decides what the mesh is and what "valid" means, and this
/// component only draws it — which is what keeps a preview from being able to change the
/// world it is previewing.
/// </para>
/// <para>
/// The mesh is the caller's, and stays uploaded: a preview that rebuilt and re-uploaded its
/// geometry every frame would allocate on the render path for no reason, and a footprint
/// changes only when the cell under the cursor does.
/// </para>
/// </summary>
public sealed class PlacementPreview
{
    /// <summary>
    /// How far above the surface the ghost floats, in metres. The liquid surfaces are lifted
    /// five centimetres so they clear the terrain, so this has to beat that as well as depth
    /// precision, or a bridge preview z-fights with the water it is spanning.
    /// </summary>
    public const float LiftMetres = 0.15f;

    /// <summary>Tint of a site the simulation would accept.</summary>
    private static readonly Vector4 ValidTint = new(0.35f, 1.00f, 0.48f, DefaultOpacity);

    /// <summary>Tint of a site it would refuse. Red says it will fail; the reason says why.</summary>
    private static readonly Vector4 InvalidTint = new(1.00f, 0.28f, 0.24f, DefaultOpacity);

    /// <summary>
    /// How solid a ghost is unless the caller says otherwise.
    /// <para>
    /// Four tenths, which is what a flat diagram of the ground needs: the footprint is one surface
    /// lying on the terrain, and anything more opaque hides the ground the player is choosing
    /// between. A ghost that is a whole building seen from outside is a different proposition, and
    /// its caller raises this — see <see cref="Show"/>.
    /// </para>
    /// </summary>
    public const float DefaultOpacity = 0.42f;

    private readonly GraphicsDevice _device;
    private readonly InstancedRenderer _renderer;
    private readonly InstanceData[] _instance = new InstanceData[1];

    private InstancedRenderer.Mesh? _mesh;
    private Matrix _transform = Matrix.Identity;
    private Vector4 _tint = ValidTint;
    private bool _staged;

    public PlacementPreview(GraphicsDevice device, InstancedRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderer);

        _device = device;
        _renderer = renderer;
    }

    /// <summary>True while there is a ghost to draw.</summary>
    public bool IsShowing => _staged && _mesh is not null;

    /// <summary>The mesh currently ghosted, for a caller that has to know whether to rebuild it.</summary>
    public InstancedRenderer.Mesh? Mesh => _mesh;

    /// <summary>
    /// Stages a ghost for this frame. Calling it every frame is the intended use: a preview
    /// follows the cursor, so where it is has to be re-stated rather than latched.
    /// </summary>
    /// <param name="mesh">The footprint, already uploaded. Owned by the caller.</param>
    /// <param name="transform">Where it would stand.</param>
    /// <param name="valid">Whether the site would be accepted, which is the colour.</param>
    /// <param name="opacity">
    /// How solid to draw it, or <see cref="DefaultOpacity"/>. A building is a volume: its walls and
    /// its roof all lie between the eye and the ground, so a tint that reads clearly on one flat
    /// quad washes out to a pale blur on a model standing over water — and "pale" is not the answer
    /// a refused site has to give.
    /// </param>
    public void Show(InstancedRenderer.Mesh mesh, in Matrix transform, bool valid, float opacity = DefaultOpacity)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        Vector4 tint = valid ? ValidTint : InvalidTint;

        _mesh = mesh;
        _transform = transform;
        _tint = new Vector4(tint.X, tint.Y, tint.Z, opacity);
        _staged = true;
    }

    /// <summary>
    /// Stages a mesh in a colour the caller chooses, for the overlays that are not a verdict.
    /// <para>
    /// A placement ghost is green or red because it is answering one question — may this go
    /// here — and colouring it any other way would be a lie about that answer. A coverage ring
    /// answers a different question: how far this gun reaches. It has no valid state and no
    /// invalid state, it has a size, so it picks its own colour instead of borrowing one of
    /// the two.
    /// </para>
    /// </summary>
    public void Show(InstancedRenderer.Mesh mesh, in Matrix transform, Vector4 tint)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        _mesh = mesh;
        _transform = transform;
        _tint = tint;
        _staged = true;
    }

    /// <summary>Stages nothing, so nothing is drawn. Used when the player is not placing.</summary>
    public void Hide() => _staged = false;

    /// <summary>
    /// Draws the staged ghost. Call inside a begun pass, after the terrain and the liquids,
    /// so a footprint over water is drawn over the water rather than under it.
    /// </summary>
    /// <returns>True when a draw call was issued.</returns>
    public bool Draw()
    {
        if (!IsShowing)
        {
            return false;
        }

        _instance[0] = new InstanceData(_transform, _tint);

        // Depth read but not written: a ghost behind a hill is hidden, as it should be, but
        // two cells of the same footprint overlapping must not take bites out of each other.
        DepthStencilState depth = _device.DepthStencilState;
        _device.DepthStencilState = DepthStencilState.DepthRead;

        _renderer.BeginGhost();
        _renderer.Draw(_mesh!, _instance, 1);
        _renderer.EndGhost();

        _device.DepthStencilState = depth;
        return true;
    }

    /// <summary>
    /// A flat quad per cell of a footprint: the ground a placement would take up, which is
    /// what the player is actually choosing between.
    /// <para>
    /// One quad per cell and no outline or volume, because this is a diagram of a footprint
    /// rather than a picture of the thing going on it. A bridge crosses several cells and the
    /// rule that decides whether it may be built is about the whole span, so previewing a
    /// single tile would show a shape that is not the one being built.
    /// </para>
    /// </summary>
    /// <param name="navigation">The lattice the cells are indices into.</param>
    /// <param name="cells">Cells the footprint covers, in the order they will be taken.</param>
    /// <param name="heightOfCell">
    /// Height to lay each cell's quad at, in millimetres. A bridge passes the water line for
    /// every cell, because that is the surface the player can see and the one the water is
    /// drawn on; something standing on the land would pass the terrain height of each cell
    /// instead, so the ghost drapes over the bumps it would sit on.
    /// </param>
    public static MeshData Footprint(NavGrid navigation, ReadOnlySpan<int> cells, Func<int, int> heightOfCell)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(heightOfCell);

        if (cells.IsEmpty)
        {
            return MeshData.Empty;
        }

        var vertices = new VertexPositionNormal[cells.Length * 4];
        var indices = new ushort[cells.Length * 6];

        float cellSize = navigation.CellSizeMm / (float)WorldPos.MmPerMetre;
        int vertex = 0;
        int index = 0;

        foreach (int cell in cells)
        {
            float x0 = (navigation.OriginMm + (navigation.CellX(cell) * navigation.CellSizeMm)) / (float)WorldPos.MmPerMetre;
            float z0 = (navigation.OriginMm + (navigation.CellZ(cell) * navigation.CellSizeMm)) / (float)WorldPos.MmPerMetre;
            float y = (heightOfCell(cell) / (float)WorldPos.MmPerMetre) + LiftMetres;

            // White, because the ghost's colour comes from the instance tint: a footprint mesh
            // that carried its own colour could only ever be drawn in that one colour, and
            // valid and invalid are the two states this mesh has to be able to be.
            vertices[vertex + 0] = new VertexPositionNormal(new Vector3(x0, y, z0), Vector3.Up, Color.White);
            vertices[vertex + 1] = new VertexPositionNormal(new Vector3(x0 + cellSize, y, z0), Vector3.Up, Color.White);
            vertices[vertex + 2] = new VertexPositionNormal(new Vector3(x0 + cellSize, y, z0 + cellSize), Vector3.Up, Color.White);
            vertices[vertex + 3] = new VertexPositionNormal(new Vector3(x0, y, z0 + cellSize), Vector3.Up, Color.White);

            // The exact winding the terrain and the fog overlay use, cell for cell: (v, v+2, v+3)
            // and (v, v+1, v+2) over the four corners in grid order. Getting it wrong does not
            // draw the ghost the wrong way round — it draws no ghost at all, because the cull
            // stage discards it, and a preview that is culled looks exactly like a preview that
            // was never staged.
            indices[index++] = (ushort)vertex;
            indices[index++] = (ushort)(vertex + 2);
            indices[index++] = (ushort)(vertex + 3);

            indices[index++] = (ushort)vertex;
            indices[index++] = (ushort)(vertex + 1);
            indices[index++] = (ushort)(vertex + 2);

            vertex += 4;
        }

        return new MeshData(vertices, indices);
    }

    /// <summary>
    /// A flat quad per cell of an <em>annulus</em>: the ring that shows how far something
    /// reaches. The same mesh as <see cref="Footprint"/> with a hole in it, and for the same
    /// reason it is built out of cells — it drapes over the ground it crosses instead of
    /// floating through a hillside.
    /// </summary>
    /// <param name="navigation">The lattice the cells are indices into.</param>
    /// <param name="centre">What the ring is drawn around, in world millimetres.</param>
    /// <param name="radiusMm">Distance from the centre to the middle of the band.</param>
    /// <param name="bandMm">
    /// How thick the band is. A cell is 9.4 m across and the ring is a diagram rather than a
    /// measurement, so a band of about two cells is what reads as a line from the camera the
    /// game is played at; a hairline one cell wide is a dotted suggestion, and a wide one is a
    /// filled disc with a hole in it.
    /// </param>
    /// <param name="heightOfCell">Height to lay each cell's quad at, in millimetres.</param>
    public static MeshData CoverageRing(
        NavGrid navigation,
        WorldPos centre,
        int radiusMm,
        int bandMm,
        Func<int, int> heightOfCell)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(heightOfCell);

        if (radiusMm <= 0 || bandMm <= 0)
        {
            return MeshData.Empty;
        }

        int size = navigation.Size;
        int cell = navigation.CellSizeMm;
        int origin = navigation.OriginMm;
        int half = cell / 2;

        long inner = Math.Max(0, radiusMm - (bandMm / 2));
        long outer = radiusMm + (bandMm / 2);
        long innerSquared = inner * inner;
        long outerSquared = outer * outer;

        // The band is not a filled disc: only the cells whose centres fall between the two
        // radii go in, which is why this is a census rather than a fill. It is a few thousand
        // cells for a 260 m ring, built once and cached, so the cost never lands in a frame.
        List<int> cells = [];

        int minCellX = Math.Max(0, (centre.X - radiusMm - bandMm - origin) / cell);
        int maxCellX = Math.Min(size - 1, (centre.X + radiusMm + bandMm - origin) / cell);
        int minCellZ = Math.Max(0, (centre.Z - radiusMm - bandMm - origin) / cell);
        int maxCellZ = Math.Min(size - 1, (centre.Z + radiusMm + bandMm - origin) / cell);

        for (int z = minCellZ; z <= maxCellZ; z++)
        {
            long dz = (origin + (z * cell) + half) - (long)centre.Z;

            for (int x = minCellX; x <= maxCellX; x++)
            {
                long dx = (origin + (x * cell) + half) - (long)centre.X;
                long squared = (dx * dx) + (dz * dz);

                if (squared >= innerSquared && squared <= outerSquared)
                {
                    cells.Add((z * size) + x);
                }
            }
        }

        return Footprint(navigation, CollectionsMarshal.AsSpan(cells), heightOfCell);
    }
}
