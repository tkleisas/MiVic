"""The tunic's insignia: red collar tabs and gold buttons, as geometry.

The reference tunic is read by three things at briefing distance: the grey
cloth, the red collar tabs with their gold piping, and the line of gold
buttons. The cloth is a recolour; the tabs and buttons are geometry, because
geometry places itself by measuring the mesh in front of it and shading comes
for free, where paint would have to guess the garment's UV layout a second
time.

Everything here is measured off the suit rather than constant-placed:

  * The chest opening (the jacket's V) is found by walking up the midline and
    watching the front surface jump backwards — the closed cloth ends and the
    throat begins, and that jump is the top of the V.
  * The buttons march down the placket from there, each pushed out of the
    cloth by a fixed offset along the forward axis.
  * The tabs sit on the collar above the V, angled to lean with its edges,
    gold behind red so the piping is the gold showing around the red.

All of it rides the chest bone, so the insignia moves when the man breathes.

Run through `build_makehuman.py`, which imports this.
"""

import math

import bmesh
import bpy
from mathutils import Matrix, Vector

from makehuman_pipe import _fill_uvs, _solid_material

#: Insignia red and uniform gold, as linear RGB. The red is a flag red taken
#: down to cloth; the gold is dull — bullion, not brass.
RED = (0.400, 0.045, 0.035)
GOLD = (0.480, 0.330, 0.070)
CLOTH = (0.318, 0.287, 0.225)   # the tunic's brown, taken down a shade so the
                                # unshaded strip blends into the shaded jacket.
                                # Keeps pace with REPAINTED in build_makehuman.py.

BUTTON_RADIUS = 0.0072
BUTTON_DEPTH = 0.0045
BUTTON_COUNT = 7
TAB_SIZE = (0.020, 0.046)      # width, length
TAB_LEAN = math.radians(22.0)  # how far the top leans toward the midline


def _front_y(suit, x, z, half_x=0.035, half_z=0.030):
    """The most forward y of the suit near (x, z) — forward is -Y, measured."""
    ys = [v.co.y for v in suit.data.vertices
          if abs(v.co.x - x) < half_x and abs(v.co.z - z) < half_z]
    return min(ys) if ys else None


def _find_opening_top(suit):
    """The z where the midline's front surface jumps back: the top of the V.

    Below the opening the midline's nearest surface is the closed jacket; above
    it is the throat, several centimetres behind. The band where that distance
    jumps is the collar's top edge, and it is found per build because the body
    it wraps is shaped by targets that change between passes.
    """
    bands = []
    z = 1.15
    while z < 1.55:
        y = _front_y(suit, 0.0, z, half_x=0.030, half_z=0.015)
        bands.append((z, y))
        z += 0.015

    best = None
    for (z0, y0), (z1, y1) in zip(bands, bands[1:]):
        if y0 is None or y1 is None:
            continue
        jump = y1 - y0          # forward is -Y, so a jump back is positive
        if best is None or jump > best[1]:
            best = ((z0 + z1) * 0.5, jump)
    return best[0] if best else 1.32


def _new_object(name, armature):
    """An empty object hung on the rig. Geometry and material come later, in
    that order: the UV fill and the material slot must land on the finished
    mesh, not on the empty one it replaces — a UV layer written before
    `bm.to_mesh` is written for zero loops, and the part exports white."""
    obj = bpy.data.objects.new(name, bpy.data.meshes.new(name))
    bpy.context.collection.objects.link(obj)
    obj.parent = armature
    modifier = obj.modifiers.new("Armature", "ARMATURE")
    modifier.object = armature
    return obj


def _finish(obj, material, bone):
    """Geometry is in; now give it its material, UVs and the chest-bone weight."""
    obj.data.materials.append(material)
    _fill_uvs(obj.data)
    group = obj.vertex_groups.new(name=bone)
    group.add(list(range(len(obj.data.vertices))), 1.0, "REPLACE")


def _add_button(bm, centre):
    """One gold button: a squat cylinder with a domed face, axis along Y."""
    segments = 12
    # Two rings and a centre point make the dome; the back is a flat cap.
    back = [bm.verts.new(centre + Vector((BUTTON_RADIUS * math.cos(2 * math.pi * k / segments),
                                          0.0,
                                          BUTTON_RADIUS * math.sin(2 * math.pi * k / segments))))
            for k in range(segments)]
    dome_ring = [bm.verts.new(centre + Vector((BUTTON_RADIUS * 0.82 * math.cos(2 * math.pi * k / segments),
                                               -BUTTON_DEPTH * 0.6,
                                               BUTTON_RADIUS * 0.82 * math.sin(2 * math.pi * k / segments))))
                 for k in range(segments)]
    top = bm.verts.new(centre + Vector((0.0, -BUTTON_DEPTH, 0.0)))
    back_cap = bm.verts.new(centre + Vector((0.0, BUTTON_DEPTH * 0.2, 0.0)))

    for k in range(segments):
        a, b = back[k], back[(k + 1) % segments]
        c, d = dome_ring[(k + 1) % segments], dome_ring[k]
        bm.faces.new((a, b, c, d))
        bm.faces.new((dome_ring[k], c, top))
        bm.faces.new((back_cap, b, a))


def _add_tab(bm, centre, lean, size, depth):
    """One collar tab: a thin box leaning with the collar's edge."""
    width, length = size
    # The top of the tab leans toward the midline: on the figure's left (+X)
    # that is a negative rotation about Y, and the mirror on the right.
    rotation = Matrix.Rotation(-lean if centre.x > 0 else lean, 4, "Y")
    corners = []
    for dx in (-width / 2, width / 2):
        for dy in (-depth / 2, depth / 2):
            for dz in (-length / 2, length / 2):
                corners.append(centre + rotation @ Vector((dx, dy, dz)))

    verts = [bm.verts.new(c) for c in corners]
    # Box vertices: dx, dy, dz each ± — faces by index pairs.
    def v(dx, dy, dz):
        return verts[(0 if dx < 0 else 4) + (0 if dy < 0 else 2) + (0 if dz < 0 else 1)]
    bm.faces.new((v(-1, -1, -1), v(-1, -1, 1), v(-1, 1, 1), v(-1, 1, -1)))
    bm.faces.new((v(1, -1, -1), v(1, 1, -1), v(1, 1, 1), v(1, -1, 1)))
    bm.faces.new((v(-1, -1, -1), v(1, -1, -1), v(1, -1, 1), v(-1, -1, 1)))
    bm.faces.new((v(-1, 1, -1), v(-1, 1, 1), v(1, 1, 1), v(1, 1, -1)))
    bm.faces.new((v(-1, -1, 1), v(1, -1, 1), v(1, 1, 1), v(-1, 1, 1)))
    bm.faces.new((v(-1, -1, -1), v(-1, 1, -1), v(1, 1, -1), v(1, -1, -1)))


def _add_placket(bm, suit, opening_top, half_width=0.028, below=0.36, above=0.020):
    """The strip that closes the jacket's V: a ribbon of tunic cloth from the
    collar to the belt, following the jacket's own front edges.

    The pack's jacket is cut open, and the reference's tunic is buttoned to the
    collar. Paint cannot close a hole and recolour cannot hide one either — the
    V stays a darker wedge no matter what colour it is given. So the front is
    closed with geometry instead: a strip in the tunic's own grey, laid at the
    depth of the lapel edges so the lapels read as folds over a closed front.
    The strip narrows at the top, because the V does: a rectangle's corners
    would poke past the lapels at the collar.
    """
    rows = []
    steps = int((above + below) / 0.02) + 1
    for i in range(steps):
        z = opening_top + above - i * 0.02
        left = _front_y(suit, half_width + 0.018, z)
        right = _front_y(suit, -half_width - 0.018, z)
        if left is None or right is None:
            continue
        y = min(left, right) - 0.0012
        taper = 1.0 if i > 2 else 0.80 + 0.10 * i
        rows.append((Vector((-half_width * taper, y, z)),
                     Vector((half_width * taper, y, z))))

    for left, right in rows:
        bm.verts.new(left)
        bm.verts.new(right)
    bm.verts.ensure_lookup_table()
    verts = bm.verts
    for i in range(len(rows) - 1):
        a, b = verts[i * 2], verts[i * 2 + 1]
        c, d = verts[i * 2 + 3], verts[i * 2 + 2]
        # Wound so the normal points forward, down -Y: the back is never seen.
        bm.faces.new((a, d, c, b))
    return len(rows)


def build_insignia(suit, armature):
    """Create the buttons and collar tabs, measured off the suit. Returns them."""
    bone = "spine_03" if armature.pose.bones.get("spine_03") else "spine_02"
    opening_top = _find_opening_top(suit)

    gold = _solid_material("insignia_gold", GOLD)
    red = _solid_material("insignia_red", RED)
    cloth = _solid_material("tunic_cloth", CLOTH)

    placket = _new_object("Human.placket", armature)
    bm = bmesh.new()
    rows = _add_placket(bm, suit, opening_top)
    bm.to_mesh(placket.data)
    bm.free()
    _finish(placket, cloth, bone)

    buttons = _new_object("Human.buttons", armature)
    bm = bmesh.new()
    placed = 0
    for i in range(BUTTON_COUNT):
        z = opening_top + 0.018 - i * 0.052
        # On the placket now, not on whatever is behind it: the button marches
        # down the strip that closes the front.
        left = _front_y(suit, 0.046, z)
        right = _front_y(suit, -0.046, z)
        if left is None or right is None:
            continue
        y = min(left, right) - 0.0012
        _add_button(bm, Vector((0.0, y - BUTTON_DEPTH * 0.6, z)))
        placed += 1
    bm.to_mesh(buttons.data)
    bm.free()
    _finish(buttons, gold, bone)

    tabs_gold = _new_object("Human.tabs_gold", armature)
    tabs_red = _new_object("Human.tabs_red", armature)
    for name, obj, material, size, push in (
            ("gold", tabs_gold, gold, (TAB_SIZE[0] + 0.004, TAB_SIZE[1] + 0.004), 0.0010),
            ("red", tabs_red, red, TAB_SIZE, 0.0030)):
        bm = bmesh.new()
        made = 0
        for side in (1.0, -1.0):
            # On the placket at the base of the collar, not on the collar
            # itself: the pack's collar is an open lapel, and a tab pinned to
            # it disappears behind the fold from the front — which is the only
            # angle a briefing is watched from. The reference's tabs sit where
            # a closed collar's would, so these sit on the closed front.
            z = opening_top + 0.018
            left = _front_y(suit, 0.046, z)
            right = _front_y(suit, -0.046, z)
            if left is None or right is None:
                continue
            y = min(left, right) - 0.0012
            _add_tab(bm, Vector((side * 0.026, y - push, z)), TAB_LEAN, size, 0.0024)
            made += 1
        bm.to_mesh(obj.data)
        bm.free()
        _finish(obj, material, bone)
        if made == 0:
            print(f"  insignia: no collar surface found for the {name} tabs")

    print(f"  insignia: opening top z {opening_top:.3f}, placket {rows} rows, "
          f"{placed} buttons, bone {bone}")
    return placket, buttons, tabs_gold, tabs_red
