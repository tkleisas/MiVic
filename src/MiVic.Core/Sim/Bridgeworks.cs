using MiVic.Core.Numerics;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>Which way the deck at a cell runs. Two spans crossing at one cell make a junction.</summary>
public enum BridgeAxis : byte
{
    /// <summary>No deck here.</summary>
    None = 0,

    /// <summary>A span running along X.</summary>
    AlongX = 1,

    /// <summary>A span running along Z.</summary>
    AlongZ = 2,

    /// <summary>Two spans crossing: a deck that can be driven through in both directions.</summary>
    Junction = 3,
}

/// <summary>One block of deck: the cell it stands on, how much of it is left, and which way it runs.</summary>
/// <param name="Cell">Navigation cell it occupies.</param>
/// <param name="Health">What is left of it; zero means the block is gone.</param>
/// <param name="Team">Team whose work laid it.</param>
/// <param name="Axis">Which way the deck runs at this cell.</param>
public readonly record struct BridgeBlock(int Cell, int Health, int Team, BridgeAxis Axis);

/// <summary>How far along one crossing is, and whether it is still whole.</summary>
/// <param name="Total">Cells in the span.</param>
/// <param name="Built">Cells the work has reached.</param>
/// <param name="Standing">Cells of the span that still carry a block.</param>
/// <param name="StartTick">Tick the order was carried out on.</param>
/// <param name="ReadyTick">Tick the last cell of the span is placed on.</param>
public readonly record struct BridgeState(int Total, int Built, int Standing, long StartTick, long ReadyTick)
{
    /// <summary>True once the work has reached every cell, whether or not any are still standing.</summary>
    public bool Complete => Built >= Total;

    /// <summary>True when a block has been destroyed: the crossing has a hole in it.</summary>
    public bool Cut => Standing < Built;

    /// <summary>True when the whole span is deck and none of it has been knocked out.</summary>
    public bool Intact => Standing >= Total;

    /// <summary>How much of the work is done, in permille.</summary>
    public int ProgressPermille => Total <= 0 ? 1_000 : (Built * 1_000) / Total;

    /// <summary>Ticks of work left, or zero once the work has reached every cell.</summary>
    public long RemainingTicks(long tick) => Math.Max(0, ReadyTick - tick);
}

/// <summary>What one crossing costs, in each resource.</summary>
/// <param name="Materials">Steel, timber and concrete.</param>
/// <param name="Energy">Plant, welding and pumping.</param>
/// <param name="Water">Concrete needs a great deal of it.</param>
public readonly record struct BridgeCost(int Materials, int Energy, int Water);

/// <summary>
/// The crossings on the map: the blocks of deck they are made of, the work still going into them,
/// and what one costs.
/// <para>
/// <b>The deck is a cell at a time.</b> A bridge is not one object: it is a chain of identical
/// blocks, one per cell of the span, and everything interesting is a fact about a block. That is
/// what makes a crossing destructible in a way a player can use — artillery does not delete a
/// bridge, it knocks a hole in one, and what is left either side of the hole is still standing and
/// still useless, because a route over water is only as good as its weakest block.
/// </para>
/// <para>
/// It is also what makes two crossings at once work. A block belongs to the <em>cell</em>, not to
/// the bridge that laid it: a span crossing an existing one shares the cells they meet at, the deck
/// there is laid once and runs both ways — a junction — and knocking that block out cuts both
/// crossings, because there is one deck under them and it is gone. The alternative was a rule
/// forbidding spans from crossing, and a rule that exists to avoid a second mesh variant is a rule
/// that costs the player a crossroads over a lake for nothing.
/// </para>
/// <para>
/// A block remembers the water it was built over, so losing one gives the river back. A cell is
/// ford while a block stands on it and no longer, which is also what makes the gap passable again
/// to a second order: the water that comes back is water, and a bridge can be built across it.
/// </para>
/// <para>
/// Deterministic: the work done is a function of the tick and the span, damage arrives only from
/// the simulation's own deterministic damage events, and nothing here reads the clock or the RNG.
/// </para>
/// </summary>
public sealed class Bridgeworks
{
    /// <summary>Crossings one map may carry. Engineering is finite, and the client draws every one.</summary>
    public const int MaxBridges = 64;

    /// <summary>
    /// Ticks of work before the first cell is placed: survey, piling and materials on site.
    /// Without it a one-cell crossing would be instant and a long one would look like it
    /// started before the order was given.
    /// </summary>
    public const int SetupTicks = 30;

    /// <summary>Ticks of work each cell of the span costs. The deck grows one cell every 1.5 s.</summary>
    public const int TicksPerCell = 30;

    /// <summary>What one block of deck takes before it is knocked out.</summary>
    public const int BlockHealth = 200;

    /// <summary>
    /// Share of a weapon's damage that lands on the deck under the unit it hit, in permille.
    /// <para>
    /// Half, because a shot aimed at a man on a bridge is aimed at the man: the deck is what is
    /// behind him. It has to be more than nothing — a tank column firing at infantry crossing a
    /// bridge should be chewing the bridge — and less than the whole, so that artillery, which
    /// lands on the structure rather than on somebody standing on it, stays the tool for cutting a
    /// crossing.
    /// </para>
    /// </summary>
    public const int DeckDamagePermille = 500;

    /// <summary>Materials the site itself costs, before any span is laid across it.</summary>
    public const int SetupMaterials = 30;

    /// <summary>Materials per cell of span: the deck, the rails and the piling under them.</summary>
    public const int MaterialsPerCell = 20;

    /// <summary>Energy the site itself costs.</summary>
    public const int SetupEnergy = 10;

    /// <summary>Energy per cell: plant running, and welding that does not stop for twelve seconds.</summary>
    public const int EnergyPerCell = 4;

    /// <summary>Water the site itself costs. Concrete is poured from the first cell onward.</summary>
    public const int SetupWater = 8;

    /// <summary>Water per cell.</summary>
    public const int WaterPerCell = 3;

    /// <summary>Owner recorded for a cell with no deck on it.</summary>
    private const byte NoTeam = 255;

    // The deck, cell by cell. One block per cell rather than one record per bridge, because a cell
    // is what a block physically is: two spans meeting at one have one deck there, not two.
    private readonly int[] _health;
    private readonly byte[] _original;
    private readonly byte[] _owner;
    private readonly BridgeAxis[] _axis;

    // The crossings themselves: where each runs, whose it is, and how much of the work is done.
    private readonly int[] _cells;
    private readonly int[] _length;
    private readonly int[] _built;
    private readonly long[] _startTick;
    private readonly int[] _team;
    private readonly BridgeAxis[] _direction;

    /// <summary>
    /// The sides the deck belongs to, so that "an ally's crossing is not a target" is asked of the
    /// same match the units standing on it are asked about. Held rather than passed in, because the
    /// deck is asked the friend-or-foe question from two systems and a fixture, and a caller that
    /// forgot to hand the match over would answer it with a default.
    /// </summary>
    private readonly MatchRoster _roster;

    private int _count;

    /// <summary>Creates the deck tables for a lattice of this many cells.</summary>
    /// <param name="cells">Cells in the navigation lattice.</param>
    /// <param name="roster">The match whose sides own the deck.</param>
    public Bridgeworks(int cells, MatchRoster roster)
    {
        if (cells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cells), cells, "The lattice must have cells.");
        }

        _roster = roster ?? throw new ArgumentNullException(nameof(roster));

        _health = new int[cells];
        _original = new byte[cells];
        _owner = new byte[cells];
        _axis = new BridgeAxis[cells];

        for (int cell = 0; cell < cells; cell++)
        {
            _owner[cell] = NoTeam;
        }

        _cells = new int[MaxBridges * SimWorld.MaxBridgeCells];
        _length = new int[MaxBridges];
        _built = new int[MaxBridges];
        _startTick = new long[MaxBridges];
        _team = new int[MaxBridges];
        _direction = new BridgeAxis[MaxBridges];
    }

    /// <summary>Cells on the lattice, so a caller can walk the deck table.</summary>
    public int CellCount => _health.Length;

    /// <summary>How many crossings are on the map, finished or not.</summary>
    public int Count => _count;

    /// <summary>True while there is room for another crossing.</summary>
    public bool HasRoom => _count < MaxBridges;

    /// <summary>Ticks of work a span of this many cells takes, from the order to the last cell.</summary>
    public static int TicksFor(int cells) => SetupTicks + (Math.Max(0, cells) * TicksPerCell);

    /// <summary>
    /// What a crossing of this many cells costs. The cheapest crossing a player can order is a
    /// single cell — <see cref="SetupMaterials"/> and the two other setup costs, plus one cell of
    /// span — and that is what a team has to be able to afford before the button is worth
    /// pressing, since a button cannot know the site.
    /// </summary>
    public static BridgeCost Cost(int cells)
    {
        int span = Math.Max(0, cells);

        return new BridgeCost(
            SetupMaterials + (span * MaterialsPerCell),
            SetupEnergy + (span * EnergyPerCell),
            SetupWater + (span * WaterPerCell));
    }

    /// <summary>Deck damage a weapon of this damage does to the block under what it hit.</summary>
    public static int DeckDamage(int damage) => Math.Max(1, (damage * DeckDamagePermille) / 1_000);

    /// <summary>The cells of one crossing, in the order the work reaches them: bank to bank.</summary>
    public ReadOnlySpan<int> Cells(int bridge) => _cells.AsSpan(bridge * SimWorld.MaxBridgeCells, _length[bridge]);

    /// <summary>The team whose work is building one crossing.</summary>
    public int Team(int bridge) => _team[bridge];

    /// <summary>How far along one crossing is, and whether it is still whole.</summary>
    public BridgeState State(int bridge)
    {
        int standing = 0;

        for (int i = 0; i < _built[bridge]; i++)
        {
            if (_health[_cells[(bridge * SimWorld.MaxBridgeCells) + i]] > 0)
            {
                standing++;
            }
        }

        return new BridgeState(
            _length[bridge],
            _built[bridge],
            standing,
            _startTick[bridge],
            _startTick[bridge] + TicksFor(_length[bridge]));
    }

    /// <summary>True when a cell carries deck.</summary>
    public bool HasBlock(int cell) => (uint)cell < (uint)_health.Length && _health[cell] > 0;

    /// <summary>The block on a cell, for the client to draw and the probe to report.</summary>
    public bool TryGetBlock(int cell, out BridgeBlock block)
    {
        if (!HasBlock(cell))
        {
            block = default;
            return false;
        }

        block = new BridgeBlock(cell, _health[cell], _owner[cell], _axis[cell]);
        return true;
    }

    /// <summary>What a cell's block is worth, or zero when there is none.</summary>
    public int BlockHealthAt(int cell) => (uint)cell < (uint)_health.Length ? _health[cell] : 0;

    /// <summary>
    /// Starts a crossing. The span is already validated and paid for; this only records it, with no
    /// block placed yet — the work begins on the next <see cref="Tick"/>.
    /// </summary>
    internal bool Begin(ReadOnlySpan<int> cells, long tick, int team)
    {
        if (!HasRoom || cells.IsEmpty || cells.Length > SimWorld.MaxBridgeCells)
        {
            return false;
        }

        cells.CopyTo(_cells.AsSpan(_count * SimWorld.MaxBridgeCells, cells.Length));

        _length[_count] = cells.Length;
        _built[_count] = 0;
        _startTick[_count] = tick;
        _team[_count] = team;
        _direction[_count] = DirectionOf(cells);
        _count++;
        return true;
    }

    /// <summary>
    /// Which way a span runs, read off the span itself: consecutive cells of a span along X differ
    /// by one in the lattice index and along Z by a whole row. The sign does not matter — the work
    /// starts at the bank nearer the site the player clicked, so a span may be listed either way —
    /// and reading it without the absolute value made every descending span claim to run the other
    /// way, which then made a re-bridge along the same corridor look like a crossroads. A one-cell
    /// crossing has no direction of its own and lays a square block, so it reads as along X.
    /// </summary>
    private static BridgeAxis DirectionOf(ReadOnlySpan<int> cells)
        => cells.Length < 2 || Math.Abs(cells[1] - cells[0]) == 1 ? BridgeAxis.AlongX : BridgeAxis.AlongZ;

    /// <summary>
    /// Advances every crossing's work and lays the deck on whatever ground it has reached.
    /// <para>
    /// The completion of a cell is a function of the tick it started on, so a replay rebuilds the
    /// same bridge on the same tick without any of it being replayed command by command.
    /// </para>
    /// </summary>
    public void Tick(TerrainLayer terrain, long tick)
    {
        ArgumentNullException.ThrowIfNull(terrain);

        for (int bridge = 0; bridge < _count; bridge++)
        {
            int total = _length[bridge];
            int built = _built[bridge];

            if (built >= total)
            {
                continue;
            }

            long elapsed = tick - _startTick[bridge];
            int reached = elapsed <= 0 ? 0 : (int)Math.Min((elapsed - SetupTicks) / TicksPerCell, total);
            int offset = bridge * SimWorld.MaxBridgeCells;

            // One cell at a time, so the ford and the deck appear together: the tick a cell
            // becomes crossable is the tick the work reached it.
            while (built < reached)
            {
                LayBlock(terrain, _cells[offset + built], _team[bridge], _direction[bridge]);
                built++;
            }

            _built[bridge] = built;
        }
    }

    /// <summary>
    /// Lays one block: the cell becomes ford, remembers what it was, and takes the direction of the
    /// span that reached it. A cell that already carries deck takes a second direction instead —
    /// which is what makes two crossings at one cell a junction rather than a fight over a mesh.
    /// </summary>
    private void LayBlock(TerrainLayer terrain, int cell, int team, BridgeAxis direction)
    {
        if ((uint)cell >= (uint)_health.Length)
        {
            return;
        }

        // The water the *first* deck here was built over, so the last one to be destroyed gives
        // back what was there before any of this: a lake, or the ford the generator carved.
        if (_health[cell] == 0)
        {
            _original[cell] = (byte)terrain.TypeAt(cell);
            _owner[cell] = (byte)team;
            _axis[cell] = direction;
        }
        else if (_axis[cell] != direction && _axis[cell] != BridgeAxis.Junction)
        {
            _axis[cell] = BridgeAxis.Junction;
        }

        _health[cell] = BlockHealth;
        terrain.SetType(cell, TerrainType.ShallowWater);
    }

    /// <summary>
    /// Damages the deck under a position, as a shot that landed on somebody standing there does.
    /// Returns true when a block was knocked out.
    /// </summary>
    public bool DamageAt(TerrainLayer terrain, WorldPos position, int damage, int attackerTeam)
        => Damage(terrain, terrain.IndexOfWorld(position.X, position.Z), damage, attackerTeam);

    /// <summary>
    /// Damages every block of an enemy's deck inside a radius, as artillery and off-map support do.
    /// Returns how many blocks were knocked out.
    /// </summary>
    /// <param name="sparesFriends">
    /// True when the blast spares the attacker's own side, which is how direct fire works; false for
    /// the weapons that are documented as hitting everybody, so a nuke dropped on your own bridge
    /// takes it with the rest.
    /// </param>
    public int DamageArea(
        TerrainLayer terrain,
        int centreX,
        int centreZ,
        int radiusMm,
        int damage,
        int attackerTeam,
        bool sparesFriends = true)
    {
        ArgumentNullException.ThrowIfNull(terrain);

        if (damage <= 0 || radiusMm <= 0)
        {
            return 0;
        }

        int cellSize = terrain.CellSizeMm;
        int size = terrain.Size;
        long radiusSquared = (long)radiusMm * radiusMm;
        int knockedOut = 0;

        // Walked by cell rather than by block: a blast covers the ground it covers whether or not a
        // bridge happens to be there, and the cost is a few dozen lookups. The alternative is an
        // index of blocks kept in step with the lattice for no other reason than this loop.
        int minCellX = Math.Max(0, (centreX - radiusMm - terrain.OriginMm) / cellSize);
        int maxCellX = Math.Min(size - 1, (centreX + radiusMm - terrain.OriginMm) / cellSize);
        int minCellZ = Math.Max(0, (centreZ - radiusMm - terrain.OriginMm) / cellSize);
        int maxCellZ = Math.Min(size - 1, (centreZ + radiusMm - terrain.OriginMm) / cellSize);

        for (int cz = minCellZ; cz <= maxCellZ; cz++)
        {
            int cellZ = terrain.OriginMm + (cz * cellSize) + (cellSize / 2);
            int dz = cellZ - centreZ;

            for (int cx = minCellX; cx <= maxCellX; cx++)
            {
                int cell = (cz * size) + cx;

                if (_health[cell] == 0)
                {
                    continue;
                }

                // Distance to the *cell centre*: a blast either covers a block or it does not,
                // rather than covering a corner of one.
                int cellX = terrain.OriginMm + (cx * cellSize) + (cellSize / 2);
                int dx = cellX - centreX;

                if (((long)dx * dx) + ((long)dz * dz) > radiusSquared)
                {
                    continue;
                }

                if (sparesFriends && !_roster.IsHostile(attackerTeam, _owner[cell]))
                {
                    continue;
                }

                if (Damage(terrain, cell, damage, attackerTeam))
                {
                    knockedOut++;
                }
            }
        }

        return knockedOut;
    }

    /// <summary>
    /// Applies damage to the block on a cell. A block that has had enough is gone: the cell goes
    /// back to the water it was built over, and every crossing through it is cut.
    /// </summary>
    /// <returns>True when this damage knocked the block out.</returns>
    public bool Damage(TerrainLayer terrain, int cell, int damage, int attackerTeam)
    {
        ArgumentNullException.ThrowIfNull(terrain);

        if ((uint)cell >= (uint)_health.Length || damage <= 0 || _health[cell] == 0)
        {
            return false;
        }

        // Your own deck is not a target, and neither is an ally's: the same rule the splash damage
        // on units follows, for the same reason — a stray shell should not cut the crossing your own
        // army is using. It is asked of the match's own sides — the same question, and the same
        // answer, that the men standing on the deck are asked about — so the two cannot answer
        // differently: the two used to be written separately, one of them as an alliance and the
        // other as a comparison of team ids, and that is exactly how an artillery salvo came to
        // spare the crossing and kill the unit on it.
        if (!_roster.IsHostile(attackerTeam, _owner[cell]))
        {
            return false;
        }

        _health[cell] -= damage;

        if (_health[cell] > 0)
        {
            return false;
        }

        _health[cell] = 0;
        _owner[cell] = NoTeam;
        _axis[cell] = BridgeAxis.None;
        terrain.SetType(cell, (TerrainType)_original[cell]);
        return true;
    }
}
