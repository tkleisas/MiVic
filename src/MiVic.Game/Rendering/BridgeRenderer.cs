using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Draws the decks of the crossings the simulation has built.
/// <para>
/// A bridge used to be invisible: the order turned water into ford in one tick, and the only
/// trace of it on screen was a paler patch of lake — which is not a bridge to a player who has
/// just spent 150 materials on one. The deck is drawn from the simulation's own record of where
/// each span runs and how much of it is up, so the thing on screen is the thing that was paid
/// for, and it grows across the water as the work is done rather than appearing all at once.
/// </para>
/// <para>
/// One box mesh and one instanced draw call for every deck on the map: a deck segment, and a
/// rail along each side of it. The rails are what make it read as a bridge rather than as a
/// ribbon of ground, which is the whole difference from the ford underneath it.
/// </para>
/// </summary>
public sealed class BridgeRenderer : IDisposable
{
    /// <summary>Thickness of the deck itself, in metres.</summary>
    private const float DeckThickness = 0.35f;

    /// <summary>
    /// How far the deck floats above the water line, in metres. Enough to read as a span
    /// crossing the water rather than as a raft lying on it.
    /// </summary>
    private const float DeckClearance = 0.6f;

    /// <summary>Height of the rails above the deck, in metres. Visible from a strategic camera.</summary>
    private const float RailHeight = 0.75f;

    /// <summary>Width of a rail, in metres.</summary>
    private const float RailWidth = 0.5f;

    /// <summary>Timber for the deck: a bridge is engineering, not scenery.</summary>
    private static readonly Vector4 DeckTint = new(0.36f, 0.28f, 0.20f, 1f);

    /// <summary>Paler rails, so the edges of the span read against the water at any zoom.</summary>
    private static readonly Vector4 RailTint = new(0.58f, 0.50f, 0.38f, 1f);

    private readonly InstancedRenderer _renderer;
    private readonly InstancedRenderer.Mesh _box;
    private readonly InstanceData[] _instances;

    public BridgeRenderer(InstancedRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        _renderer = renderer;

        // A unit box, sitting on its base, so an instance's scale is the size of the piece and
        // its translation is where that piece stands.
        _box = renderer.CreateMesh(MeshBuilder.Box(1f, 1f, 1f));

        // Deck and two rails for every cell of every crossing there can be.
        _instances = new InstanceData[Bridgeworks.MaxBridges * SimWorld.MaxBridgeCells * 3];
    }

    /// <summary>
    /// Collects one instance per deck block and per rail, for every cell of the map that carries
    /// deck. Returns how many instances were written.
    /// <para>
    /// Walked by cell rather than by crossing, because a block belongs to the cell: that is what
    /// makes a destroyed block a hole in the deck (the block is simply not there any more) and a
    /// crossing of two spans one junction block rather than two decks drawn through each other.
    /// </para>
    /// </summary>
    public int Collect(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        Bridgeworks bridgeworks = world.Bridgeworks;
        NavGrid navigation = world.Navigation;
        float cell = navigation.CellSizeMm / (float)WorldPos.MmPerMetre;
        float water = world.TerrainTypes.WaterLevelMm / (float)WorldPos.MmPerMetre;
        float deckBase = water + DeckClearance;
        float railBase = deckBase + DeckThickness;
        int written = 0;

        for (int index = 0; index < bridgeworks.CellCount && written + 3 <= _instances.Length; index++)
        {
            if (!bridgeworks.TryGetBlock(index, out BridgeBlock block))
            {
                continue;
            }

            float x0 = (navigation.OriginMm + (navigation.CellX(index) * navigation.CellSizeMm)) / (float)WorldPos.MmPerMetre;
            float z0 = (navigation.OriginMm + (navigation.CellZ(index) * navigation.CellSizeMm)) / (float)WorldPos.MmPerMetre;

            written = Add(written, x0 + (cell * 0.5f), deckBase, z0 + (cell * 0.5f), cell, DeckThickness, cell, DeckTint);

            // A junction is open on all four sides: rails across it would say the deck only goes one
            // way, which is the one thing a crossroads is not. The block model has a junction piece
            // with its rails broken where the other span comes in; until it does, no rails.
            if (block.Axis == BridgeAxis.Junction)
            {
                continue;
            }

            if (block.Axis == BridgeAxis.AlongX)
            {
                written = Add(written, x0 + (cell * 0.5f), railBase, z0 + (RailWidth * 0.5f), cell, RailHeight, RailWidth, RailTint);
                written = Add(written, x0 + (cell * 0.5f), railBase, z0 + cell - (RailWidth * 0.5f), cell, RailHeight, RailWidth, RailTint);
            }
            else
            {
                written = Add(written, x0 + (RailWidth * 0.5f), railBase, z0 + (cell * 0.5f), RailWidth, RailHeight, cell, RailTint);
                written = Add(written, x0 + cell - (RailWidth * 0.5f), railBase, z0 + (cell * 0.5f), RailWidth, RailHeight, cell, RailTint);
            }
        }

        return written;
    }

    /// <summary>Draws the collected decks as one instanced call.</summary>
    public void Draw(int count)
    {
        if (count > 0)
        {
            _renderer.Draw(_box, _instances, count);
        }
    }

    private int Add(int index, float x, float y, float z, float width, float height, float depth, Vector4 tint)
    {
        _instances[index] = new InstanceData(
            Matrix.CreateScale(width, height, depth) * Matrix.CreateTranslation(x, y, z),
            tint);

        return index + 1;
    }

    public void Dispose() => _box.Dispose();
}
