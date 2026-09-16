using ImGuiNET;
using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using MiVic.Game.Sim;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game.Ui;

/// <summary>What the cursor is armed with. One at a time, the way an armed placement is.</summary>
public enum EditorTool : byte
{
    /// <summary>Nothing armed: the cursor looks, nothing changes.</summary>
    None = 0,

    /// <summary>Raise the ground under the cursor, while the button is held.</summary>
    Raise = 1,

    /// <summary>Lower the ground under the cursor, while the button is held.</summary>
    Lower = 2,

    /// <summary>Paint the surface under the cursor, while the button is held.</summary>
    Paint = 3,

    /// <summary>Place a structure at the click, asked the same rules a player's order is.</summary>
    Structure = 4,

    /// <summary>Remove the nearest placement — a structure or a unit.</summary>
    Delete = 5,

    /// <summary>Place a mobile unit at the click, at the exact position.</summary>
    Unit = 6,
}

/// <summary>What the editor's panels asked the client to do.</summary>
public enum EditorCommandKind : byte
{
    /// <summary>Nothing requested.</summary>
    None = 0,

    /// <summary>Build the map as it stands and hand it to the match, so the author can play what they shaped.</summary>
    Play = 1,

    /// <summary>Back to the front end; the session stays where the client left it.</summary>
    ToMenu = 2,
}

/// <summary>A request raised by an editor panel, applied by the client.</summary>
public readonly record struct EditorCommand(EditorCommandKind Kind);

/// <summary>
/// The map under authoring, the live world it is authored on, the armed tool, and the
/// panels. The client calls into it; the ground answers through the simulation.
/// <para>
/// <b>The world is a function of the list.</b> Every edit is recorded and applied to the
/// live ground the moment it is made — heights re-derive the passes, paints write the
/// surface — so what the author sees is what the file will say. The undo is the list: pop
/// the last edit, rebuild the world from what remains. The world is never carried as state;
/// it is rebuilt whenever the list is rewound, because the ground is what the edits say it
/// is and nothing else.
/// </para>
/// <para>
/// <b>The report is what the editor owes.</b> After every ground change, the placements
/// still standing are asked the placement rules again, on the ground that now exists. A
/// placement the edit invalidated is listed with the reason — the same sentence a player's
/// construction is shown — and stays on the map until the author resolves it. The save
/// refuses while the report is non-empty: a file the loader would refuse is a file the
/// editor does not write, because an author whose work silently refused to load six hours
/// later is the failure §10 exists to prevent.
/// </para>
/// <para>
/// <b>What the editor does not do is the mission.</b> The body it carries comes from the
/// file it opened or from the new-map template; objectives and triggers are edited as data
/// in the file itself. What the editor authors is the ground, the surfaces and the
/// placements — the part a cursor is for.
/// </para>
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>Default seed for a new map: the campaign's own, shaped from there.</summary>
    private const ulong NewMapSeed = 20250101;

    /// <summary>The map under authoring, as lists the author is building.</summary>
    private readonly List<TerrainEdit> _edits = [];
    private readonly List<StructurePlacement> _structures = [];
    private readonly List<UnitPlacement> _units = [];

    private MissionDefinition _mission = null!;
    private ulong _seed;

    /// <summary>The live world: the map applied, the placements standing.</summary>
    public SimBridge World { get; private set; } = null!;

    /// <summary>Starts on a new map, which is what the editor is for when nothing has been opened.</summary>
    public MapEditor()
    {
        NewMap();
    }

    /// <summary>The tool the cursor is armed with, and the numbers a brush asks for.</summary>
    public EditorTool Tool { get; private set; } = EditorTool.None;

    public TerrainType PaintType { get; private set; } = TerrainType.Mud;

    public UnitKind PlacementKind { get; private set; } = UnitKind.GunEmplacement;

    public int PlacementTeam { get; private set; }

    public int BrushRadius { get; private set; }

    public int BrushStrengthMm { get; private set; } = 2_000;

    /// <summary>The placements the current ground refuses, with its reasons — the report §10 asks for.</summary>
    public IReadOnlyList<(string What, string Reason)> Invalid => _invalid;

    /// <summary>
    /// True when the placements are the whole starting force rather than an addition to the
    /// generated one: nothing is laid out for any team, so the map is exactly what is on it.
    /// </summary>
    public bool ExactForce { get; private set; }

    /// <summary>True once something has been authored that the file does not hold.</summary>
    public bool Dirty { get; private set; }

    /// <summary>What the last action did or refused, shown under the panels.</summary>
    public string Notice { get; private set; } = string.Empty;

    /// <summary>How many edits the author has made — the number the panel shows.</summary>
    public int EditCount => _edits.Count;

    /// <summary>How many units the author has placed — the number the panel shows.</summary>
    public int UnitCount => _units.Count;

    /// <summary>How many structures the author has placed — the number the panel shows.</summary>
    public int StructureCount => _structures.Count;

    private readonly List<(string What, string Reason)> _invalid = [];

    /// <summary>A fresh map: the standard duel over new ground, nothing shaped yet.</summary>
    public void NewMap()
    {
        _seed = NewMapSeed;
        _mission = MissionCatalog.Require("m1_bridgehead") with
        {
            Id = "m_authored",
            Objectives =
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε μία εχθρική θέση.",
                    Team: 0,
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 7_200),
            ],
            TimeLimitTicks = 7_200,
        };

        _edits.Clear();
        _structures.Clear();
        _units.Clear();
        ExactForce = false;
        Notice = "Νέος χάρτης.";
        Dirty = false;
        RebuildWorld();
    }

    /// <summary>Opens a map file and rebuilds the world from it.</summary>
    public void Open(string path)
    {
        MapDefinition map = MapFile.Load(path);
        _seed = map.Seed;
        _mission = map.Mission;
        _edits.Clear();
        _edits.AddRange(map.TerrainEdits);
        _structures.Clear();
        _structures.AddRange(map.Structures);
        _units.Clear();
        _units.AddRange(map.Units);
        ExactForce = map.ExactForce;
        Notice = $"Ανοίχτηκε: {path}";
        Dirty = false;
        RebuildWorld();
    }

    /// <summary>Writes the map. Refused while the ground refuses a placement, because a file the loader would refuse is a file the editor does not write.</summary>
    public void Save()
    {
        if (_invalid.Count > 0)
        {
            Notice = $"Δεν αποθηκεύτηκε — {_invalid.Count} τοποθέτηση(σεις) δεν γίνονται δεκτές από το έδαφος.";
            return;
        }

        if (_missionProblems.Count > 0)
        {
            Notice = $"Δεν αποθηκεύτηκε — η αποστολή αρνείται: {_missionProblems[0]}";
            return;
        }

        string name = MapFileName.Length > 0 ? MapFileName : $"authored-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine("maps", name + ".map.json");

        MapFile.Save(ToDefinition(), path);
        SetFileName(name);
        Notice = $"Αποθηκεύτηκε: {path}";
        Dirty = false;
    }

    /// <summary>The name the save is written to, as the author typed it or as the hour names it.</summary>
    public string MapFileName { get; private set; } = string.Empty;

    /// <summary>Keeps the name the author typed in the panel's own field.</summary>
    public void SetFileName(string name) => MapFileName = name.Trim();

    /// <summary>
    /// Arms a tool. Arming clears nothing else, because each answer is its own — except the
    /// role the placement tools share: a structure tool cannot place a unit and a unit tool
    /// cannot place a structure, so arming one picks a role it can actually place rather than
    /// leaving the previous tool's choice armed.
    /// </summary>
    public void UseTool(EditorTool tool)
    {
        Tool = tool;

        if (tool == EditorTool.Structure && !UnitCatalog.Get(PlacementKind).IsBuilding)
        {
            PlacementKind = UnitKind.CommandCentre;
        }
        else if (tool == EditorTool.Unit && UnitCatalog.Get(PlacementKind).IsBuilding)
        {
            PlacementKind = UnitKind.Tank;
        }
    }

    /// <summary>Sets the brush's reach and the height step it moves the ground by.</summary>
    public void UseBrush(int radius, int strengthMm)
    {
        BrushRadius = radius;
        BrushStrengthMm = strengthMm;
    }

    /// <summary>Sets what the paint tool paints.</summary>
    public void UsePaint(TerrainType type) => PaintType = type;

    /// <summary>Sets what the structure tool places, and for which side.</summary>
    public void UsePlacement(UnitKind kind, int team)
    {
        PlacementKind = kind;
        PlacementTeam = team;
    }

    /// <summary>Says the editor has a message for its author.</summary>
    public void Notify(string notice) => Notice = notice;

    /// <summary>The map as it stands, for the file and for a test-play match.</summary>
    public MapDefinition ToDefinition() => new(
        _seed,
        [.. _edits],
        [.. _structures],
        _mission,
        ExactForce)
    {
        Units = [.. _units],
    };

    /// <summary>
    /// Says whether the placements are the whole force or an addition to the generated one.
    /// Rebuilds the live world, because the two modes are different worlds: the author should
    /// see the difference the moment they ask for it.
    /// </summary>
    public void UseExactForce(bool exact)
    {
        if (ExactForce == exact)
        {
            return;
        }

        ExactForce = exact;
        Notice = exact
            ? "Ακριβής σύνθεση: ό,τι τοποθετηθεί είναι όλη η δύναμη."
            : "Παραγόμενη σύνθεση: οι τοποθετήσεις προστίθενται σε ό,τι παράγει η αποστολή.";
        Dirty = true;
        RebuildWorld();
    }

    /// <summary>
    /// One application of the armed terrain tool at a resolved sample. The edit is recorded
    /// and applied to the live ground, and the passes re-derive from it. Returned so the
    /// client can hold it while a drag is running and undo the whole stroke.
    /// <para>
    /// The two lattices differ — the height field runs finer than the navigation grid the
    /// surface layer sits on — so the edit carries <em>metres</em> and each pass resolves
    /// the cell in its own lattice. A paint written in terrain cells would land past the
    /// layer's edge and read as a paint off the map, which is what a brush over a lava
    /// field just did.
    /// </summary>
    public TerrainEdit ApplyBrush(int cellX, int cellZ)
    {
        int originX = World.World.Terrain.OriginMm;
        int originZ = World.World.Terrain.OriginMm;
        int size = World.World.Terrain.Size;
        int cellSize = World.World.Terrain.CellSizeMm;

        var at = new WorldPos(
            originX + (cellX * cellSize) + (cellSize / 2),
            0,
            originZ + (cellZ * cellSize) + (cellSize / 2));

        var edit = new TerrainEdit(
            Kind: Tool == EditorTool.Paint ? TerrainEditKind.Paint : TerrainEditKind.AdjustHeight,
            X: at.X,
            Z: at.Z,
            DeltaMm: Tool == EditorTool.Lower ? -BrushStrengthMm : BrushStrengthMm,
            Type: PaintType,
            RadiusCells: BrushRadius);

        _edits.Add(edit);
        Scenario.ApplyMapGround(World.World, [edit]);
        RevalidatePlacements();
        Dirty = true;
        return edit;
    }

    /// <summary>Places a structure at a resolved site, asked the same rules a player's order is.</summary>
    public bool PlaceStructure(UnitKind kind, WorldPos site)
    {
        if (!World.World.CanPlaceStructure(kind, site, out string reason) ||
            !World.World.IsSiteClear(kind, site, out reason))
        {
            Notice = $"{UnitCatalog.GreekName(kind)}: {reason}";
            return false;
        }

        var placement = new StructurePlacement(kind, site.X, site.Z, PlacementTeam);
        _structures.Add(placement);
        SpawnPlacement(placement);
        RevalidatePlacements();
        Dirty = true;
        return true;
    }

    /// <summary>
    /// Places a unit at the exact position, asked the ground rule for its own movement class.
    /// Unlike the scenario's formations, the position is not nudged towards the nearest legal
    /// cell: what the author clicked is what the file says, and a spot the unit cannot stand
    /// on is refused where they are looking rather than silently moved.
    /// </summary>
    public bool PlaceUnit(UnitKind kind, WorldPos site)
    {
        if (!World.World.CanPlaceUnit(kind, site, out string reason))
        {
            Notice = $"{UnitCatalog.GreekName(kind)}: {reason}";
            return false;
        }

        var placement = new UnitPlacement(kind, site.X, site.Z, PlacementTeam);
        _units.Add(placement);
        SpawnUnit(placement);
        RevalidatePlacements();
        Dirty = true;
        return true;
    }

    /// <summary>
    /// Removes the nearest placement — a structure or a unit, whichever is closer — since
    /// the map and the picture of it cannot be allowed to disagree and a unit the author
    /// cannot delete is a unit they would have to edit the file to be rid of.
    /// </summary>
    public bool DeleteNearest(WorldPos site)
    {
        long best = long.MaxValue;
        int bestStructure = -1;
        int bestUnit = -1;

        for (int i = 0; i < _structures.Count; i++)
        {
            StructurePlacement candidate = _structures[i];

            if (DistanceSquared(candidate.X, candidate.Z, site) < best)
            {
                best = DistanceSquared(candidate.X, candidate.Z, site);
                bestStructure = i;
                bestUnit = -1;
            }
        }

        for (int i = 0; i < _units.Count; i++)
        {
            UnitPlacement candidate = _units[i];

            if (DistanceSquared(candidate.X, candidate.Z, site) < best)
            {
                best = DistanceSquared(candidate.X, candidate.Z, site);
                bestUnit = i;
                bestStructure = -1;
            }
        }

        if (bestStructure >= 0)
        {
            StructurePlacement removed = _structures[bestStructure];
            _structures.RemoveAt(bestStructure);
            DespawnNearest(removed.Kind, removed.Team, removed.X, removed.Z);
            RevalidatePlacements();
            Dirty = true;
            return true;
        }

        if (bestUnit >= 0)
        {
            UnitPlacement removed = _units[bestUnit];
            _units.RemoveAt(bestUnit);
            DespawnNearest(removed.Kind, removed.Team, removed.X, removed.Z);
            RevalidatePlacements();
            Dirty = true;
            return true;
        }

        return false;
    }

    private static long DistanceSquared(int x, int z, WorldPos site)
    {
        long dx = x - site.X;
        long dz = z - site.Z;

        return (dx * dx) + (dz * dz);
    }

    /// <summary>
    /// Takes the entity the placement put in the world back out again, matched by role, team
    /// and position rather than by an id the list does not carry.
    /// </summary>
    private void DespawnNearest(UnitKind kind, int team, int x, int z)
    {
        int slot = SlotOf(kind, team, x, z);

        if (slot >= 0)
        {
            World.World.Despawn(new EntityId(slot, World.World.GetRefBySlot(slot).Generation));
        }
    }

    /// <summary>
    /// The live slot of a placement's own entity, matched by role, team and position, or -1 when
    /// it is not standing. The editor's lists carry no ids on purpose — they are the file's own
    /// shape — so an entity is found the way the file finds it: by what it is and where.
    /// </summary>
    private int SlotOf(UnitKind kind, int team, int x, int z)
    {
        for (int slot = 0; slot < World.World.Capacity; slot++)
        {
            if (!World.World.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref World.World.GetRefBySlot(slot);

            if (entity.Kind == kind &&
                entity.TeamId == team &&
                Math.Abs(entity.Position.X - x) < 20_000 &&
                Math.Abs(entity.Position.Z - z) < 20_000)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Undoes the last edit or placement, by rebuilding the world from what remains.</summary>
    public void Undo()
    {
        if (_edits.Count > 0)
        {
            _edits.RemoveAt(_edits.Count - 1);
        }
        else if (_structures.Count > 0)
        {
            _structures.RemoveAt(_structures.Count - 1);
        }
        else if (_units.Count > 0)
        {
            _units.RemoveAt(_units.Count - 1);
        }
        else
        {
            return;
        }

        RebuildWorld();
        Dirty = true;
        Notice = "Αναιρέθηκε το τελευταίο.";
    }

    /// <summary>Asks every standing placement the question the ground now answers.</summary>
    private void RevalidatePlacements()
    {
        _invalid.Clear();

        foreach (StructurePlacement placement in _structures)
        {
            var site = new WorldPos(placement.X, 0, placement.Z);

            // The placement's own entity is left out of the overlap question: it is standing on
            // the site the question is about, and a structure occupies its own footprint. Without
            // this every authored building was reported as the thing in its own way, so the save
            // refused a map the author had every right to write.
            int own = SlotOf(placement.Kind, placement.Team, placement.X, placement.Z);

            if (!World.World.CanPlaceStructure(placement.Kind, site, out string reason) ||
                !World.World.IsSiteClear(placement.Kind, site, own, out reason))
            {
                _invalid.Add((Describe(placement.Kind, placement.X, placement.Z), reason));
            }
        }

        foreach (UnitPlacement placement in _units)
        {
            var site = new WorldPos(placement.X, 0, placement.Z);

            if (!World.World.CanPlaceUnit(placement.Kind, site, out string reason))
            {
                _invalid.Add((Describe(placement.Kind, placement.X, placement.Z), reason));
            }
        }

        // And the half that needs no ground under it: a placement on the wrong side of the
        // structure/unit divide, a team the match does not declare, an exact force with no
        // headquarters. Recomputed here so the report and the save stay in step with the
        // placements rather than only with the last rebuild.
        foreach (string problem in MapFile.ForceProblems(ToDefinition()))
        {
            _invalid.Add(("σύνθεση δύναμης", problem));
        }
    }

    private static string Describe(UnitKind kind, int x, int z)
        => $"{UnitCatalog.GreekName(kind)} ({x / WorldPos.MmPerMetre}m, {z / WorldPos.MmPerMetre}m)";

    /// <summary>Raises one placed structure in the live world.</summary>
    private void SpawnPlacement(StructurePlacement placement)
    {
        UnitDefinition definition = UnitCatalog.Get(placement.Kind);

        World.World.Spawn(
            World.World.FactionOfTeam(placement.Team),
            placement.Team,
            placement.Kind,
            World.World.LegalSpawnSite(new WorldPos(placement.X, 0, placement.Z)),
            Fix32.Zero,
            definition.Health);
    }

    /// <summary>Raises one placed unit in the live world, at the position it was placed on.</summary>
    private void SpawnUnit(UnitPlacement placement)
    {
        UnitDefinition definition = UnitCatalog.Get(placement.Kind);

        World.World.Spawn(
            World.World.FactionOfTeam(placement.Team),
            placement.Team,
            placement.Kind,
            new WorldPos(placement.X, 0, placement.Z),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);
    }

    /// <summary>
    /// Rebuilds the live world from the map as it stands: ground, re-derived passes,
    /// placements. The world is a function of the list, and this is the function. The
    /// refusals are reported into the map's own sink, so an invalid placement is a line in
    /// the report rather than a refusal to open.
    /// </summary>
    private void RebuildWorld()
    {
        MapDefinition map = ToDefinition();
        map.RefusalsSink = _invalid;
        _invalid.Clear();

        World = new SimBridge(map);

        // The half of the force rule that needs no ground under it: an exact map owes every
        // judged side a headquarters, and a placement has to be on the right side of the
        // structure/unit divide. Reported here so the author meets it while placing, and so
        // the save refuses a file the loader would refuse.
        foreach (string problem in MapFile.ForceProblems(map))
        {
            _invalid.Add(("σύνθεση δύναμης", problem));
        }
    }

    /// <summary>Says where the author's brush is, as the terrain sees it: the sample a world position resolves to, or -1.</summary>
    public int SampleAt(WorldPos target)
    {
        int x = (target.X - World.World.Terrain.OriginMm) / World.World.Terrain.CellSizeMm;
        int z = (target.Z - World.World.Terrain.OriginMm) / World.World.Terrain.CellSizeMm;

        return x >= 0 && x < World.World.Terrain.Size && z >= 0 && z < World.World.Terrain.Size
            ? (z * World.World.Terrain.Size) + x
            : -1;
    }

    // ------------------------------------------------------------------ the panels

    /// <summary>
    /// The editor's panels: the file row, the tools, the tool's own numbers, the report of
    /// what the ground currently refuses, and the doors — play, and back. What they raise,
    /// the client acts on; the ground itself answers through the live world.
    /// </summary>
    public EditorCommand? Draw()
    {
        EditorCommand? raised = null;

        ImGui.SetNextWindowPos(new NVec2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new NVec2(360f, 0f));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.020f, 0.030f, 0.050f, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(0.30f, 0.38f, 0.48f, 0.85f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(14f, 12f));

        if (ImGui.Begin("##editor", PanelFlags))
        {
            ImGui.TextColored(new NVec4(0.90f, 0.88f, 0.80f, 1f), "Συντελεστής Χάρτη");
            ImGui.SameLine();

            if (Dirty)
            {
                ImGui.TextColored(new NVec4(0.85f, 0.65f, 0.35f, 1f), "— αλλαγές όχι αποθηκευμένες");
            }

            ImGui.Separator();

            if (ImGui.InputText("##name", ref _nameText, 64))
            {
                SetFileName(_nameText);
            }

            if (ImGui.Button("Αποθήκευση", new NVec2(-1f, 0f)))
            {
                Save();
            }

            if (ImGui.Button("Νέος χάρτης", new NVec2(-1f, 0f)))
            {
                NewMap();
            }

            ImGui.Separator();

            // The one decision that changes what the map is rather than what is on it: whether
            // the placements are the whole force or an addition to the generated one.
            bool exact = ExactForce;

            if (ImGui.Checkbox("Ακριβής σύνθεση — μόνο ό,τι τοποθετηθεί", ref exact))
            {
                UseExactForce(exact);
            }

            ImGui.TextDisabled($"Κτίρια {StructureCount} · Μονάδες {UnitCount}");

            ImGui.Separator();
            ImGui.Text("Εργαλεία:");

            foreach ((EditorTool tool, string label) in Tools)
            {
                bool armed = Tool == tool;

                if (ImGui.SmallButton((armed ? "> " : string.Empty) + label))
                {
                    UseTool(tool);
                }

                if (tool is EditorTool.None or EditorTool.Structure or EditorTool.Unit or EditorTool.Delete)
                {
                    ImGui.NewLine();
                }
                else
                {
                    ImGui.SameLine();
                }
            }

            if (Tool is EditorTool.Raise or EditorTool.Lower)
            {
                int radius = BrushRadius;
                int strength = BrushStrengthMm / 1_000;

                if (ImGui.SliderInt("Ακτίνα", ref radius, 0, 6))
                {
                    UseBrush(radius, BrushStrengthMm);
                }

                int strengthMetres = BrushStrengthMm / 1_000;

                if (ImGui.SliderInt("Ισχύς (m)", ref strengthMetres, 1, 30))
                {
                    UseBrush(BrushRadius, strengthMetres * 1_000);
                }
            }

            if (Tool == EditorTool.Paint)
            {
                int radius = BrushRadius;

                if (ImGui.SliderInt("Ακτίνα", ref radius, 0, 6))
                {
                    UseBrush(radius, BrushStrengthMm);
                }

                if (ImGui.BeginCombo("Επιφάνεια", GreekSurface(PaintType)))
                {
                    foreach (TerrainType type in PaintableSurfaces)
                    {
                        if (ImGui.Selectable(GreekSurface(type), type == PaintType))
                        {
                            UsePaint(type);
                        }
                    }

                    ImGui.EndCombo();
                }
            }

            if (Tool is EditorTool.Structure or EditorTool.Unit)
            {
                bool placingStructure = Tool == EditorTool.Structure;
                string role = placingStructure ? "Κτίριο" : "Μονάδα";

                if (ImGui.BeginCombo(role, UnitCatalog.GreekName(PlacementKind)))
                {
                    foreach (UnitDefinition definition in UnitCatalog.All)
                    {
                        if (definition.IsBuilding != placingStructure)
                        {
                            continue;
                        }

                        if (ImGui.Selectable(UnitCatalog.GreekName(definition.Kind), definition.Kind == PlacementKind))
                        {
                            UsePlacement(definition.Kind, PlacementTeam);
                        }
                    }

                    ImGui.EndCombo();
                }

                int team = PlacementTeam;
                ImGui.SliderInt("Πλευρά", ref team, 0, SimConstants.TeamCount - 1);
                if (team != PlacementTeam)
                {
                    UsePlacement(PlacementKind, team);
                }
            }

            // The bindings, written where the tools are — a binding a player has to
            // discover is a binding they will report as missing.
            ImGui.TextDisabled("Τροχός: ζουμ · WASD ή βέλη: μετακίνηση · Q/E ή μεσαίο κλικ: περιστροφή");

            if (ImGui.Button("Αναίρεση", new NVec2(-1f, 0f)))
            {
                Undo();
            }

            if (_invalid.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new NVec4(0.95f, 0.55f, 0.45f, 1f), "Το έδαφος αρνείται:");

                foreach ((string what, string reason) in _invalid)
                {
                    ImGui.BulletText($"{what}: {reason}");
                }
            }

            if (Notice.Length > 0)
            {
                ImGui.Separator();
                ImGui.TextWrapped(Notice);
            }

            ImGui.Separator();

            if (ImGui.Button("Δοκιμή παιχνιδιού", new NVec2(-1f, 0f)))
            {
                raised = new EditorCommand(EditorCommandKind.Play);
            }

            if (ImGui.Button("Πίσω στο μενού", new NVec2(-1f, 0f)))
            {
                raised = new EditorCommand(EditorCommandKind.ToMenu);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        return raised;
    }

    private string _nameText = string.Empty;

    /// <summary>The tools, in row order: none first, the two that share a line together.</summary>
    private static readonly (EditorTool Tool, string Label)[] Tools =
    [
        (EditorTool.None, "Καμία"),
        (EditorTool.Raise, "Ανύψωση"),
        (EditorTool.Paint, "Χρώμα"),
        (EditorTool.Delete, "Διαγραφή"),
        (EditorTool.Lower, "Κάθοδος"),
        (EditorTool.Structure, "Κτίριο"),
        (EditorTool.Unit, "Μονάδα"),
    ];

    /// <summary>The surfaces a brush may paint: the ground a battle is fought over, and the ore an economy works.</summary>
    private static readonly TerrainType[] PaintableSurfaces =
    [
        TerrainType.Grass, TerrainType.Mud, TerrainType.Sand, TerrainType.Snow,
        TerrainType.Rock, TerrainType.Forest, TerrainType.Mine,
    ];

    private static string GreekSurface(TerrainType type) => type switch
    {
        TerrainType.Grass => "Γρασίδι",
        TerrainType.Mud => "Λάσπη",
        TerrainType.Sand => "Άμμος",
        TerrainType.Snow => "Χιόνι",
        TerrainType.Rock => "Βράχος",
        TerrainType.Forest => "Δάσος",
        TerrainType.Mine => "Κοίτασμα",
        TerrainType.ShallowWater => "Ρηχά νερά",
        TerrainType.DeepWater => "Βαθιά νερά",
        TerrainType.Lava => "Λάβα",
        _ => type.ToString(),
    };

    private static string ToolLabel(EditorTool tool) => tool switch
    {
        EditorTool.None => "Καμία",
        EditorTool.Raise => "Ανύψωση",
        EditorTool.Lower => "Κάθοδος",
        EditorTool.Paint => "Χρώμα",
        EditorTool.Structure => "Κτίριο",
        EditorTool.Unit => "Μονάδα",
        EditorTool.Delete => "Διαγραφή",
        _ => tool.ToString(),
    };

    private const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;
}
