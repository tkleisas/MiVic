using MiVic.Core.Terrain;

namespace MiVic.Core.Pathfinding;

/// <summary>
/// Deterministic A* over a <see cref="NavGrid"/>.
/// <para>
/// Everything about this class is chosen for reproducibility rather than raw
/// speed: integer costs, a binary heap that breaks ties on cell index, fixed
/// arrays reused across calls, and no hash sets or dictionaries. Two machines
/// running the same orders therefore expand the same nodes in the same order and
/// produce the same path.
/// </para>
/// </summary>
public sealed class PathFinder
{
    /// <summary>Cost multiplier for a diagonal step, roughly 100 * sqrt(2).</summary>
    public const int DiagonalCost = 141;

    private static readonly int[] NeighbourX = [1, -1, 0, 0, 1, 1, -1, -1];
    private static readonly int[] NeighbourZ = [0, 0, 1, -1, 1, -1, 1, -1];

    private readonly int[] _gScore;
    private readonly int[] _cameFrom;
    private readonly int[] _stamp;
    private readonly int[] _scratch;
    private readonly int[] _heapNode;
    private readonly int[] _heapPriority;
    private readonly int[] _cellCost;
    private readonly int[] _cellCostStamp;

    private int _openCount;
    private int _stampCounter;

    public PathFinder(int cellCount)
    {
        if (cellCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Must be positive.");
        }

        _gScore = new int[cellCount];
        _cameFrom = new int[cellCount];
        _stamp = new int[cellCount];
        _scratch = new int[cellCount];
        _heapNode = new int[cellCount + 1];
        _heapPriority = new int[cellCount + 1];
        _cellCost = new int[cellCount];
        _cellCostStamp = new int[cellCount];
    }

    /// <summary>Nodes expanded by the most recent search, for diagnostics.</summary>
    public int LastExpandedNodes { get; private set; }

    /// <summary>
    /// Upper bound on nodes expanded per search. When a route is very long or the
    /// goal is unreachable, the search stops and returns the partial route to the
    /// closest node it found, which keeps one pathological order from stalling a
    /// tick while still moving the unit in a sensible direction.
    /// </summary>
    public int MaxExpansions { get; init; } = 1_200;

    /// <summary>
    /// Finds a path from <paramref name="start"/> to <paramref name="goal"/>.
    /// <para>
    /// The returned length counts the cells written to <paramref name="path"/>,
    /// starting with the first step after <paramref name="start"/>. A result of
    /// zero means no path exists, or that the path did not fit the buffer — in
    /// which case the caller should walk the partial path and search again.
    /// </para>
    /// </summary>
    public int FindPath(NavGrid grid, int start, int goal, Span<int> path)
        => FindPath(grid, terrain: null, PathContext.Default, start, goal, path);

    /// <summary>
    /// Finds a path for a specific mover, honouring terrain that the grid alone
    /// cannot express — water a tank may not cross but an aircraft may, and mud
    /// that costs a heavy hull more than a light one.
    /// </summary>
    /// <param name="grid">Walkability and slope cost.</param>
    /// <param name="terrain">Surface layer, or null to ignore surfaces entirely.</param>
    /// <param name="context">How the mover travels and how hard it presses.</param>
    /// <param name="start">Cell the unit occupies.</param>
    /// <param name="goal">Cell to reach.</param>
    /// <param name="path">Buffer the route is written to.</param>
    public int FindPath(
        NavGrid grid,
        TerrainLayer? terrain,
        PathContext context,
        int start,
        int goal,
        Span<int> path)
    {
        ArgumentNullException.ThrowIfNull(grid);

        LastExpandedNodes = 0;

        // Bumped before any cost lookup: the memo arrays are zero-initialised, and a
        // counter that is also zero on the first search would make every cell look
        // cached and blocked.
        _stampCounter++;

        if (path.Length == 0 || start < 0 || goal < 0 || start >= grid.CellCount || goal >= grid.CellCount)
        {
            return 0;
        }

        if (!Walkable(grid, terrain, start, context) || !Walkable(grid, terrain, goal, context))
        {
            return 0;
        }

        if (start == goal)
        {
            return 0;
        }

        _openCount = 0;

        int size = grid.Size;
        int goalX = grid.CellX(goal);
        int goalZ = grid.CellZ(goal);

        _stamp[start] = _stampCounter;
        _gScore[start] = 0;
        _cameFrom[start] = -1;
        Push(start, Heuristic(grid.CellX(start), grid.CellZ(start), goalX, goalZ));

        int closest = start;
        int closestHeuristic = Heuristic(grid.CellX(start), grid.CellZ(start), goalX, goalZ);

        while (_openCount > 0)
        {
            int current = Pop();

            if (current == goal)
            {
                return Reconstruct(start, goal, path);
            }

            LastExpandedNodes++;

            int currentHeuristic = Heuristic(grid.CellX(current), grid.CellZ(current), goalX, goalZ);
            if (currentHeuristic < closestHeuristic)
            {
                closestHeuristic = currentHeuristic;
                closest = current;
            }

            if (LastExpandedNodes >= MaxExpansions)
            {
                // Give up gracefully: head for the best node found so far.
                return closest == start ? 0 : Reconstruct(start, closest, path);
            }

            int cellX = grid.CellX(current);
            int cellZ = grid.CellZ(current);
            int currentG = _gScore[current];
            int currentCost = ResolveCost(grid, terrain, current, context);

            for (int direction = 0; direction < NeighbourX.Length; direction++)
            {
                int stepX = NeighbourX[direction];
                int stepZ = NeighbourZ[direction];
                int nx = cellX + stepX;
                int nz = cellZ + stepZ;

                if ((uint)nx >= (uint)size || (uint)nz >= (uint)size)
                {
                    continue;
                }

                // The neighbour index is a fixed offset from the current cell.
                // Going through IndexOf would cost two integer divisions per
                // neighbour, and this loop runs thousands of times per search.
                int neighbour = current + stepX + (stepZ * size);

                if (!Walkable(grid, terrain, neighbour, context))
                {
                    continue;
                }

                bool diagonal = stepX != 0 && stepZ != 0;

                // Diagonals may not squeeze between two blocked cells.
                if (diagonal &&
                    (!Walkable(grid, terrain, current + stepX, context) ||
                     !Walkable(grid, terrain, current + (stepZ * size), context)))
                {
                    continue;
                }

                int stepCost = diagonal ? DiagonalCost : 100;
                int terrainCost = (currentCost + ResolveCost(grid, terrain, neighbour, context)) / 2;
                int tentative = currentG + ((stepCost * terrainCost) / NavGrid.BaseCost);

                if (_stamp[neighbour] == _stampCounter && tentative >= _gScore[neighbour])
                {
                    continue;
                }

                _stamp[neighbour] = _stampCounter;
                _gScore[neighbour] = tentative;
                _cameFrom[neighbour] = current;
                Push(neighbour, tentative + Heuristic(nx, nz, goalX, goalZ));
            }
        }

        return 0;
    }

    /// <summary>True when a mover may enter a cell: the grid allows it and so does the surface.</summary>
    private bool Walkable(NavGrid grid, TerrainLayer? terrain, int index, in PathContext context)
        => ResolveCost(grid, terrain, index, context) > 0;

    /// <summary>
    /// Cost of entering a cell, combining the grid's slope cost with the surface's
    /// multiplier. Open ground is exactly the grid cost, so a map with no terrain
    /// features produces the same routes it always did.
    /// <para>
    /// Memoised per search: a cell is looked at once as a node but up to eight
    /// times as a neighbour, and the surface lookup is a switch and a division.
    /// </para>
    /// </summary>
    private int ResolveCost(NavGrid grid, TerrainLayer? terrain, int index, in PathContext context)
    {
        if ((uint)index >= (uint)grid.CellCount)
        {
            return 0;
        }

        if (_cellCostStamp[index] == _stampCounter)
        {
            return _cellCost[index];
        }

        int cost = 0;

        if (grid.IsWalkable(index))
        {
            cost = grid.CostAt(index);

            if (terrain is not null)
            {
                int permille = terrain.CostPermille(index, context.Movement, context.GroundPressurePermille);
                cost = permille == 0 ? 0 : (cost * permille) / TerrainLayer.BasePermille;
            }
        }

        _cellCostStamp[index] = _stampCounter;
        _cellCost[index] = cost;
        return cost;
    }

    /// <summary>Octile distance to the goal, scaled by the cheapest possible step cost.</summary>
    private static int Heuristic(int fromX, int fromZ, int toX, int toZ)
    {
        int dx = Math.Abs(fromX - toX);
        int dz = Math.Abs(fromZ - toZ);
        int diagonal = Math.Min(dx, dz);
        int straight = Math.Max(dx, dz) - diagonal;

        return (straight * NavGrid.BaseCost) + (diagonal * DiagonalCost);
    }

    /// <summary>
    /// Walks the came-from chain back from the goal and writes as many steps as
    /// fit into <paramref name="path"/>, starting with the first step after the
    /// start cell.
    /// <para>
    /// A route longer than the caller's buffer yields a partial route rather than
    /// a failure, so the caller can walk the first leg and search again from
    /// there. Failing outright would leave long orders silently ignored.
    /// </para>
    /// </summary>
    private int Reconstruct(int start, int goal, Span<int> path)
    {
        int length = 0;
        int node = goal;

        while (node != start)
        {
            if (node < 0 || length >= _scratch.Length)
            {
                return 0;
            }

            _scratch[length++] = node;
            node = _cameFrom[node];
        }

        int take = Math.Min(length, path.Length);

        for (int i = 0; i < take; i++)
        {
            path[i] = _scratch[length - 1 - i];
        }

        return take;
    }

    private void Push(int node, int priority)
    {
        int index = _openCount++;
        _heapNode[index] = node;
        _heapPriority[index] = priority;

        while (index > 0)
        {
            int parent = (index - 1) / 2;

            if (Compare(index, parent) >= 0)
            {
                break;
            }

            Swap(index, parent);
            index = parent;
        }
    }

    private int Pop()
    {
        int result = _heapNode[0];

        _openCount--;

        if (_openCount == 0)
        {
            return result;
        }

        _heapNode[0] = _heapNode[_openCount];
        _heapPriority[0] = _heapPriority[_openCount];

        int index = 0;

        while (true)
        {
            int left = (2 * index) + 1;
            int right = left + 1;
            int smallest = index;

            if (left < _openCount && Compare(left, smallest) < 0)
            {
                smallest = left;
            }

            if (right < _openCount && Compare(right, smallest) < 0)
            {
                smallest = right;
            }

            if (smallest == index)
            {
                break;
            }

            Swap(index, smallest);
            index = smallest;
        }

        return result;
    }

    /// <summary>Orders by priority, then by node index so ties are never ambiguous.</summary>
    private int Compare(int a, int b)
    {
        int byPriority = _heapPriority[a].CompareTo(_heapPriority[b]);
        return byPriority != 0 ? byPriority : _heapNode[a].CompareTo(_heapNode[b]);
    }

    private void Swap(int a, int b)
    {
        (_heapNode[a], _heapNode[b]) = (_heapNode[b], _heapNode[a]);
        (_heapPriority[a], _heapPriority[b]) = (_heapPriority[b], _heapPriority[a]);
    }
}
