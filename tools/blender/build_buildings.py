"""Generate the MiVic structure models with Blender, headlessly.

    blender --background --python tools/blender/build_buildings.py -- --out <dir>

A structure is not a vehicle with a roof on it. The player sees one from 25 to 700
metres away and between 30 and 60 degrees above the horizon, so what they actually
look at is the roof and the footprint: a building is read as a plan first and as a
facade a distant second. Everything here is therefore built outward from the roof —
a dark fascia, a tinted deck that overhangs the walls, a parapet around the edge
and plant standing on it — and the walls carry rows of small windows rather than
one band, because a row of windows is what the eye reads as "many rooms".

Faction is carried by architecture and only then by tint. Σοβιετικοί buildings are
poured concrete: battered plinths, deep window reveals, cornices, fat flues, built
to look like one pour. Κινέζοι buildings are mass production: long low sheds,
sawtooth or barrel-vault roofs, corrugated ribs, more identical bays than anyone
counted. Δυτικοί buildings are refined: glazed curtain walls in bands, thin
mullions and clean edges, flat roofs with neat plant on them.

The five roles have to be separable from above as well as from the side, so each
one owns a shape: the headquarters is a stepped compound, the factory is one long
hall with a gate and chimney, the power plant is a hall under fat stacks, the
nuclear plant is a containment dome beside cooling towers, and the design bureau is
a tower with a dish over a test hall.

The part contract, which the renderer animates:

    turret      a mount that swings to face whatever the building is engaging
    radar       a part that turns continuously: a dish, an extractor fan, a crane
    barrel      the flue a working factory or bureau smokes out of
    stack_l/r   flues; the game pulls power-plant smoke out of `stack_l`

A part rotates about its own origin, so anything named `turret` or `radar` is built
around its origin and placed with ``Part.place``: baking the offset into the
vertices instead would swing a dish around the middle of the building.

Why the mesh helpers are local rather than imported: the shared kit builds one box
per object, which cannot express a wall with a dozen genuine window openings. A
`Part` here is a merged mesh whose faces each carry their own material, so a whole
wall, its glazing and its sills export as one node. The kit's material table, its
deterministic noise, its parenting rule and its exporter are all reused as they are.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from build_vehicles import MATERIALS, _noise, clear_scene, export, join  # noqa: E402


# --------------------------------------------------------------------------
# Materials
#
# Every entry is (red, green, blue, paint mask), exactly as in the vehicles kit: the
# mask is the fraction of the faction colour that replaces the material. Concrete,
# glass, steel and brick stay their own colour (a mask near 0.1), and the surfaces
# that should say *whose* building this is — the roof deck, painted steelwork,
# trims, gates — sit at 0.5 and above.
#
# The brightness still matters as much as the mask, because the renderer shades the
# faction colour by the material's own luminance. That is what lets a red roof stay
# lighter than a dark parapet on the same building.
# --------------------------------------------------------------------------

BRICK = (0.42, 0.26, 0.20, 0.10)        # cheap infill walls; stays brick-coloured
ROOF = (0.50, 0.52, 0.55, 0.58)         # roof decks: mostly the faction colour
PAINT = (0.52, 0.54, 0.56, 0.70)        # painted steelwork and wall bands
TRIM = (0.30, 0.31, 0.33, 0.66)         # fascia, parapets, cornices: a dark tint
GLASS = MATERIALS["glass"]              # (0.09, 0.15, 0.20, 0.00)
GLASS_LIT = (0.36, 0.54, 0.62, 0.04)    # the lit pane, used sparingly
CONCRETE = MATERIALS["concrete"]        # (0.56, 0.56, 0.54, 0.12)
PANEL = MATERIALS["panel"]              # (0.62, 0.64, 0.66, 0.00)
STEEL = MATERIALS["steel"]
DARK = MATERIALS["gun_dark"]
RUST = MATERIALS["rust"]
HAZARD = MATERIALS["hazard"]
LAMP = MATERIALS["lamp"]


def _clamp(value):
    return min(1.0, max(0.0, value))


# --------------------------------------------------------------------------
# Geometry
#
# Vertex order matches the kit's `_link`/`box` windings exactly, because the
# normals come from the winding: get it backwards and a wall is lit from inside.
# Each appender takes the material for the faces it adds, so one object can hold
# grey piers, dark reveals and blue glass without becoming three objects.
# --------------------------------------------------------------------------


class Part:
    """One exported node: a merged mesh whose faces keep their own materials.

    Merging is what keeps a building affordable. A wall with a dozen window
    openings is one node holding a dozen boxes rather than a dozen nodes, and
    because the material rides on the face, that one node still reads as stone,
    glass and shadow. `mark`/`rotate` exist for the parts that turn: a radar dish
    tilts, the mast carrying it does not.
    """

    def __init__(self, name):
        self.name = name
        self.verts = []
        self.faces = []
        self.mats = []
        self.location = None

    # -- placement ---------------------------------------------------------

    def place(self, location):
        """Sets the object's origin. Required for anything the game rotates."""
        self.location = tuple(float(v) for v in location)
        return self

    def mark(self):
        return len(self.verts)

    def rotate(self, start, angle_degrees, axis="x", about=(0.0, 0.0, 0.0)):
        """Turns the vertices added since `start` about a point on one axis."""
        angle = math.radians(angle_degrees)
        c, s = math.cos(angle), math.sin(angle)
        ox, oy, oz = about

        for i in range(start, len(self.verts)):
            x, y, z = self.verts[i]
            x, y, z = x - ox, y - oy, z - oz

            if axis == "x":
                y, z = (y * c) - (z * s), (y * s) + (z * c)
            elif axis == "y":
                x, z = (x * c) + (z * s), (z * c) - (x * s)
            else:
                x, y = (x * c) - (y * s), (x * s) + (y * c)

            self.verts[i] = (x + ox, y + oy, z + oz)

    # -- primitives --------------------------------------------------------

    def _face(self, indices, material, variation):
        self.faces.append(tuple(indices))
        self.mats.append((material[0], material[1], material[2], material[3], variation))

    def box(self, centre, size, material, variation=0.07):
        cx, cy, cz = centre
        hx = max(size[0], 0.02) * 0.5
        hy = max(size[1], 0.02) * 0.5
        hz = max(size[2], 0.02) * 0.5
        base = len(self.verts)

        self.verts += [
            (cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz),
            (cx - hx, cy + hy, cz - hz), (cx + hx, cy + hy, cz - hz),
            (cx - hx, cy - hy, cz + hz), (cx + hx, cy - hy, cz + hz),
            (cx - hx, cy + hy, cz + hz), (cx + hx, cy + hy, cz + hz),
        ]

        for index in ((0, 1, 5, 4), (3, 2, 6, 7), (2, 0, 4, 6),
                      (1, 3, 7, 5), (4, 5, 7, 6), (2, 3, 1, 0)):
            self._face([base + i for i in index], material, variation)

        return self

    def frustum(self, centre, bottom, top, height, material, variation=0.07):
        """A truncated pyramid: a battered plinth, a stepped roof block."""
        cx, cy, cz = centre
        bx, by = bottom[0] * 0.5, bottom[1] * 0.5
        tx, ty = top[0] * 0.5, top[1] * 0.5
        base = len(self.verts)

        self.verts += [
            (cx - bx, cy - by, cz), (cx + bx, cy - by, cz),
            (cx - bx, cy + by, cz), (cx + bx, cy + by, cz),
            (cx - tx, cy - ty, cz + height), (cx + tx, cy - ty, cz + height),
            (cx - tx, cy + ty, cz + height), (cx + tx, cy + ty, cz + height),
        ]

        for index in ((0, 1, 5, 4), (3, 2, 6, 7), (2, 0, 4, 6),
                      (1, 3, 7, 5), (4, 5, 7, 6), (2, 3, 1, 0)):
            self._face([base + i for i in index], material, variation)

        return self

    def extrude(self, profile, axis, a0, a1, material, variation=0.07, caps=True):
        """Extrudes a closed 2D profile along one axis.

        The profile is (u, v) with v always the height and u the horizontal axis the
        extrusion is *not* along: (y, z) for an extrusion along X, (x, z) for one
        along Y. Winding is normalised here rather than at every call site, because
        an inverted profile turns a whole roof inside out.
        """
        area = 0.0

        for i in range(len(profile)):
            u0, v0 = profile[i]
            u1, v1 = profile[(i + 1) % len(profile)]
            area += (u0 * v1) - (u1 * v0)

        points = list(profile)

        if (area < 0.0) if axis == "x" else (area > 0.0):
            points.reverse()

        count = len(points)
        base = len(self.verts)

        for a in (a0, a1):
            for (u, v) in points:
                self.verts.append((a, u, v) if axis == "x" else (u, a, v))

        for i in range(count):
            j = (i + 1) % count
            self._face((base + i, base + j, base + count + j, base + count + i), material, variation)

        if caps:
            order = list(range(count)) if axis == "y" else list(range(count - 1, -1, -1))
            self._face([base + i for i in order], material, variation)
            self._face([base + count + i for i in order], material, variation)

        return self

    def revolve(self, profile, segments, material, offset=(0.0, 0.0, 0.0),
                variation=0.07, start=0.0, sweep=360.0):
        """A surface of revolution about the vertical axis, profile given bottom-up
        as (radius, z). Stacks, silos, cooling towers, dishes and fan hubs."""
        ox, oy, oz = offset
        rings = []

        for (radius, z) in profile:
            ring = []

            for i in range(segments):
                angle = math.radians(start + (sweep * i / segments))
                ring.append(len(self.verts))
                self.verts.append(
                    (ox + (math.cos(angle) * radius), oy + (math.sin(angle) * radius), oz + z))

            rings.append(ring)

        for k in range(len(rings) - 1):
            low, high = rings[k], rings[k + 1]

            for i in range(segments):
                j = (i + 1) % segments
                self._face((low[i], low[j], high[j], high[i]), material, variation)

        self._face(list(reversed(rings[0])), material, variation)
        self._face(list(rings[-1]), material, variation)
        return self

    # -- export ------------------------------------------------------------

    def build(self):
        mesh = bpy.data.meshes.new(self.name)
        mesh.from_pydata(self.verts, [], self.faces)
        mesh.validate()

        if len(mesh.polygons) != len(self.faces):
            raise RuntimeError(
                f"{self.name}: built {len(self.faces)} faces, {len(mesh.polygons)} survived validate()")

        attribute = mesh.color_attributes.get("Col")

        if attribute is None:
            attribute = mesh.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")

        for poly in mesh.polygons:
            r, g, b, mask, variation = self.mats[poly.index]
            shade = 1.0 + (variation * _noise(poly.index))
            entry = (_clamp(r * shade), _clamp(g * shade), _clamp(b * shade), mask)

            for loop in poly.loop_indices:
                attribute.data[loop].color = entry

        obj = bpy.data.objects.new(self.name, mesh)
        bpy.context.collection.objects.link(obj)

        if self.location is not None:
            obj.location = self.location

        return obj


class Model:
    """Collects the named parts of one structure and writes them out in order.

    Parts are looked up by name and created on first use, so four walls, their
    glazing and their sills can be added by four calls that all name the same three
    parts. The root is what the export hangs everything off, which is also what the
    game parents `stack_*` flues to.
    """

    def __init__(self, name):
        self.name = name
        self._parts = {}
        self._order = []

    def part(self, name):
        if name not in self._parts:
            self._parts[name] = Part(name)
            self._order.append(name)

        return self._parts[name]

    def build(self):
        root = bpy.data.objects.new(self.name, None)
        bpy.context.collection.objects.link(root)
        objects = [self._parts[name].build() for name in self._order]
        join(root, objects)
        return root


# --------------------------------------------------------------------------
# Walls
# --------------------------------------------------------------------------


def _piece(part, face, plane, a0, a1, z0, z1, d0, d1, material, variation=0.07):
    """One panel of a wall: [a0,a1] along it, [z0,z1] up it, [d0,d1] inwards from the
    outer surface. A negative depth projects, which is how the same call builds a
    wall, a sill and a lintel.
    """
    mid = (d0 + d1) * 0.5
    depth = abs(d1 - d0)
    a_mid = (a0 + a1) * 0.5
    z_mid = (z0 + z1) * 0.5

    if face == "+x":
        centre, size = (plane - mid, a_mid, z_mid), (depth, a1 - a0, z1 - z0)
    elif face == "-x":
        centre, size = (plane + mid, a_mid, z_mid), (depth, a1 - a0, z1 - z0)
    elif face == "+y":
        centre, size = (a_mid, plane - mid, z_mid), (a1 - a0, depth, z1 - z0)
    elif face == "-y":
        centre, size = (a_mid, plane + mid, z_mid), (a1 - a0, depth, z1 - z0)
    else:
        raise ValueError(f"unknown face {face}")

    part.box(centre, size, material, variation)


def facade(model, name, face, plane, span, z0, z1, rows, pitch=3.0, win=2.0,
           thickness=0.9, reveal=0.45, wall=CONCRETE, glass=GLASS, sill=0.0,
           sill_mat=STEEL, glass_name=None, dress_name=None, mullion=None,
           variation=0.07, lit_every=0):
    """One wall of a building: real window openings, or a plain panel if it is too
    small for any.

    The wall is a sill band, a head band and the piers between the openings, with
    the glazing set in *behind* the piers by `reveal`. That recess is the whole
    difference between a Σοβιετικοί wall — a metre of concrete in front of every
    window — and a Δυτικοί curtain wall, where the glass is flush and the wall is
    nothing but mullions.

    `rows` is a list of (bottom, top) window bands, so a wall can have two or three
    storeys of windows with spandrels between them, which is what makes a large
    building read as many rooms rather than one big flat face.
    """
    a0, a1 = span
    length = a1 - a0

    if length < 1.2 or z1 - z0 < 1.0:
        _piece(model.part(name), face, plane, a0, a1, z0, z1, 0.0, thickness, wall, variation)
        return

    rows = sorted(rows)
    count = max(1, int(round(length / pitch)))
    bay = length / count
    width = min(win, max(0.6, bay - (mullion if mullion is not None else 0.8)))

    wall_part = model.part(name)
    glass_part = model.part(glass_name or f"{name}_glass")
    dress_part = model.part(dress_name or f"{name}_dress") if sill > 0.0 else None

    # Bands: everything that is not a window band, across the whole span.
    edges = [z0]
    bands = []

    for (r0, r1) in rows:
        if r0 - edges[-1] > 0.05:
            bands.append((edges[-1], r0))

        edges.append(r1)

    if z1 - edges[-1] > 0.05:
        bands.append((edges[-1], z1))

    for (b0, b1) in bands:
        _piece(wall_part, face, plane, a0, a1, b0, b1, 0.0, thickness, wall, variation)

    # Piers: a half bay at each end, one between every pair of windows.
    cuts = [a0]
    centres = []

    for i in range(count):
        c = a0 + (bay * (i + 0.5))
        centres.append(c)
        cuts += [c - (width * 0.5), c + (width * 0.5)]

    cuts.append(a1)

    for i in range(0, len(cuts), 2):
        p0, p1 = cuts[i], cuts[i + 1]

        if p1 - p0 < 0.06:
            continue

        for (r0, r1) in rows:
            _piece(wall_part, face, plane, p0, p1, r0, r1, 0.0, thickness, wall, variation)

    for index, c in enumerate(centres):
        pane = GLASS_LIT if lit_every and (index % lit_every == 0) else glass

        for (r0, r1) in rows:
            _piece(glass_part, face, plane, c - (width * 0.5), c + (width * 0.5),
                   r0 + 0.06, r1 - 0.06, reveal, reveal + 0.16, pane, 0.05)

            if dress_part is not None:
                _piece(dress_part, face, plane, c - (width * 0.5) - sill, c + (width * 0.5) + sill,
                       r0 - (sill * 0.5), r0, -sill * 0.45, 0.14, sill_mat)


def walls(model, tag, cx, cy, w, d, z0, z1, rows, faces="xy", gap=None, **options):
    """Runs `facade` around a rectangular mass, sharing one part per material.

    The ±X walls run the full depth and the ±Y walls are inset by the wall
    thickness, so the corners are filled by solid piers rather than by two
    coincident faces fighting over the same pixels. `gap` opens a doorway: it is
    (face, a0, a1) and it splits that one wall into two spans around the opening.
    """
    thickness = options.get("thickness", 0.9)
    half_w, half_d = w * 0.5, d * 0.5

    layout = []

    if "x" in faces:
        layout += [("+x", cx + half_w, (cy - half_d, cy + half_d)),
                   ("-x", cx - half_w, (cy - half_d, cy + half_d))]

    if "y" in faces:
        layout += [("+y", cy + half_d, (cx - half_w + thickness, cx + half_w - thickness)),
                   ("-y", cy - half_d, (cx - half_w + thickness, cx + half_w - thickness))]

    for (face, plane, span) in layout:
        spans = [span]

        if gap is not None and gap[0] == face:
            spans = [(span[0], gap[1]), (gap[2], span[1])]

        for piece_span in spans:
            if piece_span[1] - piece_span[0] < 1.2:
                continue

            facade(model, f"{tag}_wall", face, plane, piece_span, z0, z1, rows,
                   wall=options.get("wall", CONCRETE),
                   glass=options.get("glass", GLASS),
                   sill=options.get("sill", 0.0),
                   sill_mat=options.get("sill_mat", STEEL),
                   pitch=options.get("pitch", 3.0),
                   win=options.get("win", 2.0),
                   thickness=thickness,
                   reveal=options.get("reveal", 0.45),
                   mullion=options.get("mullion"),
                   lit_every=options.get("lit_every", 0),
                   glass_name=f"{tag}_glass",
                   dress_name=f"{tag}_sill")


def collar(model, name, cx, cy, w, d, z, height, out, material, variation=0.07):
    """A band that projects past the wall it wraps: a string course, a cornice, a
    plinth cap. One box, because only its four sides are ever visible."""
    model.part(name).box((cx, cy, z + (height * 0.5)), (w + (2 * out), d + (2 * out), height),
                         material, variation)


def ribbons(model, name, cx, cy, w, d, z0, z1, spacing, out, material, variation=0.07):
    """Horizontal form-tie bands up a concrete wall. Brutalism is poured in lifts,
    and the lifts show: this is the cheapest way to say 'poured, not built'."""
    part = model.part(name)
    z = z0 + spacing

    while z < z1 - 0.2:
        part.box((cx, cy, z), (w + (2 * out), d + (2 * out), 0.18), material, variation)
        z += spacing


def ribs(model, name, cx, cy, w, d, z0, z1, spacing, width, out, material, faces="xy",
         variation=0.07):
    """Vertical ribs at a regular pitch on the outside of a wall.

    The cheapest possible way to say "this wall is a hundred identical bays": it
    costs one box per rib and it survives being seen from 300 metres, where the
    windows have long since blurred into nothing.
    """
    part = model.part(name)
    half_w, half_d = w * 0.5, d * 0.5

    def run(face, plane, a0, a1):
        count = max(2, int(round((a1 - a0) / spacing)))
        step = (a1 - a0) / count

        for i in range(count + 1):
            a = a0 + (i * step)
            _piece(part, face, plane, a - (width * 0.5), a + (width * 0.5), z0, z1,
                   -out, 0.05, material, variation)

    if "x" in faces:
        run("+x", cx + half_w, cy - half_d, cy + half_d)
        run("-x", cx - half_w, cy - half_d, cy + half_d)

    if "y" in faces:
        run("+y", cy + half_d, cx - half_w, cx + half_w)
        run("-y", cy - half_d, cx - half_w, cx + half_w)


def plinth(model, name, cx, cy, w, d, z0, height, batter, material, cap=TRIM, cap_out=0.25):
    """A battered foundation, wider than the walls it carries.

    The plinth is the single most useful landmark a building has from above: it is
    a light apron around a dark roof, so the footprint of a structure stays legible
    even when the roof itself is cluttered with plant.
    """
    model.part(name).frustum((cx, cy, z0), (w, d), (w - (2 * batter), d - (2 * batter)),
                             height, material)

    if cap is not None:
        collar(model, f"{name}_cap", cx, cy, w - (2 * batter), d - (2 * batter),
               z0 + height, 0.45, cap_out, cap)


def apron(model, name, cx, cy, w, d, height, rim, material=CONCRETE, rim_mat=PAINT):
    """The hard standing a building stands on, with a painted border.

    Painted, because from directly above the apron is most of what a small
    structure occupies and a border of it is the cheapest square metre of faction
    colour on the whole model. It is a ring rather than a slab: painting the whole
    apron would leave a building standing on a coloured field.
    """
    model.part(name).box((cx, cy, height * 0.5), (w, d, height), material, 0.05)
    parapet_ring(model, f"{name}_rim", cx, cy, w, d, height, 0.24, rim, rim_mat)


def roof(model, tag, cx, cy, w, d, z, overhang=0.9, parapet=1.1, mat=ROOF, fascia=TRIM,
         deck=0.4, fascia_h=0.6):
    """The top of a building: a dark fascia, a tinted deck that overhangs it, and a
    parapet around the edge.

    This is the most important geometry in the file. From 30 to 60 degrees above the
    horizon the deck is the largest single surface the player sees, so it is the
    surface that carries the faction colour, and the overhang is what makes the top
    of the wall read as an edge instead of as a place where the model stops.
    """
    model.part(f"{tag}_fascia").box((cx, cy, z + (fascia_h * 0.5)),
                                   (w + (2 * overhang), d + (2 * overhang), fascia_h), fascia)

    inner_w = w + (2 * overhang * 0.88)
    inner_d = d + (2 * overhang * 0.88)

    model.part(f"{tag}_deck").box((cx, cy, z + fascia_h + (deck * 0.5)), (inner_w, inner_d, deck), mat)

    if parapet > 0.0:
        parapet_ring(model, f"{tag}_parapet", cx, cy, inner_w, inner_d,
                     z + fascia_h + deck, parapet, 0.4, fascia)


def parapet_ring(model, name, cx, cy, w, d, z, height, thickness, material):
    part = model.part(name)
    half_w, half_d = w * 0.5, d * 0.5

    part.box((cx + half_w - (thickness * 0.5), cy, z + (height * 0.5)),
             (thickness, d, height), material, 0.05)
    part.box((cx - half_w + (thickness * 0.5), cy, z + (height * 0.5)),
             (thickness, d, height), material, 0.05)
    part.box((cx, cy + half_d - (thickness * 0.5), z + (height * 0.5)),
             (w - (2 * thickness), thickness, height), material, 0.05)
    part.box((cx, cy - half_d + (thickness * 0.5), z + (height * 0.5)),
             (w - (2 * thickness), thickness, height), material, 0.05)


# --------------------------------------------------------------------------
# Openings
# --------------------------------------------------------------------------


def doorway(model, tag, face, plane, centre, width, height, z0, depth=1.6,
            door=DARK, frame=CONCRETE, canopy=0.0, steps=0, hazard=False):
    """A door at the foot of a wall: a recessed opening, a leaf, a frame and an
    optional canopy and steps.

    A building with no door is a box with windows, and a player reads an entrance
    as "this is where units come out" long before they read anything else.
    """
    half = width * 0.5
    inner = model.part(f"{tag}_doorway")
    siding = model.part(f"{tag}_wall")

    # The opening itself: the wall is missing here, so a dark box stands in for the
    # hall behind it and the jambs become real reveals.
    _piece(inner, face, plane, centre - half, centre + half, z0, z0 + height,
           depth - 0.2, depth, door, 0.04)
    _piece(inner, face, plane, centre - half + 0.12, centre + half - 0.12, z0, z0 + (height * 0.82),
           0.02, depth - 0.3, DARK, 0.05)

    # A frame standing proud of the wall, and a lintel over the opening.
    _piece(siding, face, plane, centre - half - 0.45, centre - half, z0, z0 + height + 0.45,
           -0.35, 0.55, frame, 0.05)
    _piece(siding, face, plane, centre + half, centre + half + 0.45, z0, z0 + height + 0.45,
           -0.35, 0.55, frame, 0.05)
    _piece(siding, face, plane, centre - half - 0.45, centre + half + 0.45,
           z0 + height, z0 + height + 0.45, -0.35, 0.55, frame, 0.05)

    if hazard:
        _piece(model.part(f"{tag}_hazard"), face, plane, centre - half, centre + half,
               z0 + height + 0.45, z0 + height + 0.75, -0.3, 0.2, HAZARD, 0.03)

    if canopy > 0.0:
        _piece(model.part(f"{tag}_canopy"), face, plane, centre - half - 1.1, centre + half + 1.1,
               z0 + height + 0.45, z0 + height + 0.85, -canopy, 0.3, STEEL, 0.05)

    for i in range(steps):
        _piece(model.part(f"{tag}_steps"), face, plane, centre - half - 0.8, centre + half + 0.8,
               z0 - ((i + 1) * 0.22), z0 - (i * 0.22), 0.0, 0.9 + (i * 0.75), CONCRETE, 0.05)


def gate(model, tag, face, plane, centre, width, height, z0, depth=1.6, frame=CONCRETE):
    """A factory gate: wide enough for a tank column, with hazard posts and a
    roller shutter that is visibly rolled up."""
    doorway(model, tag, face, plane, centre, width, height, z0, depth=depth,
            door=DARK, frame=frame, canopy=1.4, hazard=True)

    part = model.part(f"{tag}_shutter")

    for offset in (-1.0, 1.0):
        _piece(part, face, plane, centre + (offset * (width * 0.5)) - 0.35,
               centre + (offset * (width * 0.5)) + 0.35, z0 + height - 1.4, z0 + height + 0.4,
               -0.42, -0.05, HAZARD, 0.03)


def dock_doors(model, tag, face, plane, centres, width, height, z0, depth=1.2):
    """A row of loading bays along one wall, each one a recess with its own apron."""
    part = model.part(f"{tag}_bays")
    apron_part = model.part(f"{tag}_apron")

    for centre in centres:
        half = width * 0.5
        _piece(part, face, plane, centre - half, centre + half, z0, z0 + height,
               depth - 0.25, depth, DARK, 0.05)
        _piece(part, face, plane, centre - half - 0.3, centre - half, z0 - 0.2, z0 + height + 0.5,
               -0.4, 0.5, STEEL, 0.05)
        _piece(part, face, plane, centre + half, centre + half + 0.3, z0 - 0.2, z0 + height + 0.5,
               -0.4, 0.5, STEEL, 0.05)
        _piece(part, face, plane, centre - half - 0.3, centre + half + 0.3, z0 + height,
               z0 + height + 0.5, -0.4, 0.5, STEEL, 0.05)
        _piece(apron_part, face, plane, centre - half - 0.6, centre + half + 0.6, z0 - 0.35, z0,
               0.0, 1.6, CONCRETE, 0.05)


# --------------------------------------------------------------------------
# Roofs
# --------------------------------------------------------------------------


def sawtooth(model, tag, x0, x1, cy, depth, z, bays, rise, mat=ROOF, glass=GLASS,
             base=CONCRETE):
    """A sawtooth roof: a slope with a glazed clerestory standing on its back edge,
    repeated along Y. The mass-production roof, and from above it is a row of
    parallel stripes that nothing else in the game has."""
    part = model.part(f"{tag}_saw")
    glass_part = model.part(f"{tag}_saw_glass")
    bay = depth / bays

    for i in range(bays):
        y0 = cy - (depth * 0.5) + (i * bay)
        y1 = y0 + bay
        part.extrude([(y0, z), (y1, z), (y1, z + rise)], "x", x0, x1, mat)
        part.extrude([(y0 - 0.08, z), (y0, z), (y0, z + rise * 0.55)], "x", x0, x1, base, 0.05)
        glass_part.box(((x0 + x1) * 0.5, y1 + 0.06, z + (rise * 0.42)),
                       (x1 - x0 - 0.4, 0.12, rise * 0.7), glass, 0.05)


def vault(model, tag, x0, x1, cy, radius, z, segments=12, mat=ROOF, glass=GLASS):
    """A barrel-vault roof: a half cylinder along X, on a low springing wall with a
    strip of glazing along each side."""
    part = model.part(f"{tag}_vault")
    profile = [(cy - radius, z), (cy + radius, z)]

    for i in range(1, segments):
        angle = math.pi * i / segments
        profile.append((cy + (math.cos(angle) * radius), z + (math.sin(angle) * radius)))

    part.extrude(profile, "x", x0, x1, mat)
    model.part(f"{tag}_vault_glass").box(
        ((x0 + x1) * 0.5, cy + radius - 0.02, z + radius * 0.28),
        (x1 - x0 - 0.5, 0.12, radius * 0.4), glass, 0.05)
    model.part(f"{tag}_vault_glass").box(
        ((x0 + x1) * 0.5, cy - radius + 0.02, z + radius * 0.28),
        (x1 - x0 - 0.5, 0.12, radius * 0.4), glass, 0.05)


def roof_monitor(model, tag, cx, cy, w, d, z, height, mat=ROOF, glass=GLASS):
    """A raised strip along a ridge, glazed down both sides: the classic industrial
    roof light, and from above a bright line down the middle of a dark roof."""
    model.part(f"{tag}_monitor").frustum((cx, cy, z), (w, d), (w - (d * 0.35), d * 0.62),
                                        height, mat)
    model.part(f"{tag}_monitor_glass").box((cx, cy + (d * 0.28), z + (height * 0.5)),
                                           (w - 1.2, 0.14, height * 0.7), glass, 0.05)
    model.part(f"{tag}_monitor_glass").box((cx, cy - (d * 0.28), z + (height * 0.5)),
                                           (w - 1.2, 0.14, height * 0.7), glass, 0.05)


def skylights(model, tag, cx, cy, w, d, z, rows, columns, mat=GLASS_LIT):
    """A grid of roof lights. Seen from above it is a grid of pale rectangles, which
    is what tells the player this roof is a working shed and not a pavement."""
    part = model.part(f"{tag}_skylights")
    step_x = w / columns
    step_y = d / rows

    for i in range(columns):
        for j in range(rows):
            part.box((cx - (w * 0.5) + (step_x * (i + 0.5)), cy - (d * 0.5) + (step_y * (j + 0.5)),
                      z + 0.1), (step_x * 0.55, step_y * 0.5, 0.22), mat, 0.05)


# --------------------------------------------------------------------------
# Plant: what stands on a roof
# --------------------------------------------------------------------------


def hvac_row(model, name, centre, z, count, spacing, size, mat=STEEL, axis="x", base=CONCRETE):
    part = model.part(name)

    for i in range(count):
        offset = (i - ((count - 1) * 0.5)) * spacing
        x = centre[0] + (offset if axis == "x" else 0.0)
        y = centre[1] + (offset if axis == "y" else 0.0)
        part.box((x, y, z + (size[2] * 0.5) + 0.15), size, mat, 0.06)
        part.box((x, y, z + 0.07), (size[0] + 0.3, size[1] + 0.3, 0.16), base, 0.05)


def vents(model, name, positions, z, radius, height, mat=STEEL, cap=DARK):
    part = model.part(name)

    for (x, y) in positions:
        part.revolve([(radius, 0.0), (radius, height * 0.72), (radius * 1.25, height)], 10,
                     mat, offset=(x, y, z))
        part.revolve([(radius * 1.3, height), (radius * 1.3, height + 0.22)], 10, cap,
                     offset=(x, y, z))


def tank(model, name, x, y, z, radius, height, mat=PANEL, legs=True, band=TRIM):
    """A water tank on legs: a cylinder with a lid, which from above is an
    unmistakable circle on an otherwise rectangular roof."""
    part = model.part(name)
    lag = 1.1

    if legs:
        for i in range(6):
            angle = math.radians(60.0 * i)
            part.box((x + (math.cos(angle) * radius * 0.72), y + (math.sin(angle) * radius * 0.72),
                      z + (lag * 0.5)), (0.22, 0.22, lag), STEEL, 0.05)

    base = z + (lag if legs else 0.0)
    part.revolve([(radius, base), (radius, base + height), (radius * 0.92, base + height + 0.3)],
                 14, mat, offset=(x, y, 0.0))
    part.revolve([(radius * 0.99, base + (height * 0.72)), (radius * 1.06, base + (height * 0.72)),
                  (radius * 1.06, base + (height * 0.8)), (radius * 0.99, base + (height * 0.8))],
                 14, band, offset=(x, y, 0.0))


def pipes(model, name, y, z, x0, x1, radius, count, gap, mat=STEEL, axis="x"):
    """A run of parallel pipes lying on a roof, along X or along Y."""
    part = model.part(name)
    length = x1 - x0
    mid = (x0 + x1) * 0.5

    for i in range(count):
        offset = (i - ((count - 1) * 0.5)) * gap
        origin = (mid, y + offset, z) if axis == "x" else (mid + offset, y, z)
        start = part.mark()
        part.revolve([(radius, -length * 0.5), (radius, length * 0.5)], 8, mat, offset=origin)
        part.rotate(start, 90.0, "y" if axis == "x" else "x", about=origin)


def mast(model, name, x, y, z, height, radius=0.16, mat=STEEL, arms=2, girder=DARK):
    """A lattice mast: a column, a couple of cross-trees and a lamp on top."""
    part = model.part(name)
    part.revolve([(radius, 0.0), (radius * 0.7, height)], 8, mat, offset=(x, y, z))

    for i in range(arms):
        level = z + height * (0.55 + (0.2 * i))
        part.box((x, y, level), (radius * 7.0, 0.16, 0.16), girder, 0.05)
        part.box((x, y, level), (0.16, radius * 7.0, 0.16), girder, 0.05)

    part.box((x, y, z + height + 0.16), (0.34, 0.34, 0.32), LAMP, 0.03)


def flag(model, name, x, y, z, height, mat=PAINT, pole=STEEL):
    """A flag on a mast. Small from above, obvious from the game's angle, and the
    cheapest way for a headquarters to say whose it is."""
    part = model.part(name)
    part.revolve([(0.09, 0.0), (0.06, height)], 6, pole, offset=(x, y, z))
    part.box((x + 0.95, y, z + height - 0.75), (1.8, 0.06, 1.3), mat, 0.08)


def chimney(model, name, x, y, z0, height, radius, mat=CONCRETE, crown=DARK, taper=0.72,
            bands=3, mouth=True, band_mat=TRIM):
    """A tapered flue whose origin sits at the mouth.

    The origin matters: the game pulls working smoke out of a part named `barrel` or
    `stack_l`, and it puts the plume at that part's origin, so a flue built with its
    origin on the ground smokes from the ground.
    """
    part = model.part(name).place((x, y, z0 + height))
    top = radius * taper
    part.revolve([(radius, -height), (radius * 0.94, -height * 0.55), (top, 0.0)], 14, mat)

    for i in range(1, bands + 1):
        level = -height + (height * i / (bands + 1))
        r = radius + ((top - radius) * (level + height) / height)
        part.revolve([(r * 0.98, level - 0.16), (r * 1.12, level - 0.16),
                      (r * 1.12, level + 0.16), (r * 0.98, level + 0.16)], 14, band_mat,
                     variation=0.05)

    if mouth:
        part.revolve([(top * 1.12, -0.75), (top * 1.12, 0.0)], 14, crown, variation=0.05)
        part.revolve([(top * 0.74, -0.35), (top * 0.74, -0.3)], 12, DARK, variation=0.02)

    return part


def cooling_tower(model, tag, x, y, z0, radius_base, radius_waist, radius_top, height,
                  segments=18, mat=CONCRETE, legs=True):
    """A hyperbolic cooling tower: the shape that says "power station" from any
    angle, and from above a big ring with a dark throat in the middle."""
    part = model.part(f"{tag}_tower")
    profile = []
    steps = 7

    for i in range(steps + 1):
        t = i / steps
        z = height * t

        if t <= 0.42:
            u = (t / 0.42) ** 1.7
            r = radius_base + ((radius_waist - radius_base) * u)
        else:
            u = ((t - 0.42) / 0.58) ** 1.5
            r = radius_waist + ((radius_top - radius_waist) * u)

        profile.append((r, z))

    part.revolve(profile, segments, mat, offset=(x, y, z0))

    throat = model.part(f"{tag}_throat")
    throat.revolve([(radius_top * 0.94, height - 0.9), (radius_top * 0.94, height - 0.75)], segments,
                   DARK, offset=(x, y, z0))

    if legs:
        # The shell stands on a ring of legs, which is what a real hyperbolic tower
        # does and what puts a dotted ring around the base from above.
        leg_part = model.part(f"{tag}_tower_legs")

        for i in range(segments):
            angle = math.radians(360.0 * i / segments)
            leg_part.box((x + (math.cos(angle) * radius_base * 0.94),
                          y + (math.sin(angle) * radius_base * 0.94),
                          z0 + (height * 0.07)),
                         (0.7, 0.7, height * 0.14), mat, 0.04)


def dome_shell(model, tag, x, y, z0, radius, height, segments=16, rings=5, mat=CONCRETE,
               ring=None):
    """A containment dome: a smooth shell closed at the crown, on an optional
    projecting ring at the springing line."""
    profile = []

    for i in range(rings + 1):
        angle = math.pi * 0.5 * i / rings
        profile.append((max(radius * math.cos(angle), 0.05), height * math.sin(angle)))

    model.part(f"{tag}_dome").revolve(profile, segments, mat, offset=(x, y, z0))

    if ring is not None:
        model.part(f"{tag}_dome_ring").revolve(
            [(radius * 1.05, -0.55), (radius * 1.05, 0.35)], segments, ring, offset=(x, y, z0))


def fan(model, tag, x, y, z, radius, blades=6, housing=STEEL, hub=DARK, powered=True):
    """A big extractor fan: a housing you can see into, and a set of blades.

    The blades are the rotating part and are named `radar`, which is the contract's
    name for anything that turns continuously. A fan that turns is the only thing on
    a power station that legitimately moves.
    """
    ring = model.part(f"{tag}_fan_ring")
    ring.revolve([(radius, 0.0), (radius, 0.85), (radius * 0.86, 1.0)], 16, housing, offset=(x, y, z))
    ring.revolve([(radius * 0.9, 0.1), (radius * 0.9, 0.18)], 16, DARK, offset=(x, y, z))

    rotor = model.part("radar").place((x, y, z + 0.55)) if powered else model.part(f"{tag}_fan_blades")
    rotor.revolve([(radius * 0.2, -0.22), (radius * 0.2, 0.22)], 10, hub)

    for i in range(blades):
        start = rotor.mark()
        rotor.box((radius * 0.55, 0.0, 0.0), (radius * 0.85, radius * 0.34, 0.1), hub, 0.05)
        rotor.rotate(start, 360.0 * i / blades, "z")
        rotor.rotate(start, 22.0, "y")

    return rotor


# --------------------------------------------------------------------------
# Moving parts
# --------------------------------------------------------------------------


def radar_dish(model, tag, x, y, z, radius, tilt=32.0, mast=2.6, yoke=1.2, mat=PANEL,
               frame=DARK):
    """A dish on a yoke, built around its own origin so the game can sweep it.

    This is the part named `radar`: it turns continuously, and because the whole
    assembly is one node, the yoke, the dish and the feed horn all turn together.
    """
    part = model.part("radar").place((x, y, z))
    part.revolve([(0.45, 0.0), (0.3, mast)], 10, frame)
    part.box((0.0, 0.0, mast), (1.5, 0.5, 0.3), frame, 0.05)

    start = part.mark()
    profile = [(0.03, 0.0)]

    for i in range(5):
        t = (i + 1) / 5.0
        profile.append((radius * math.sin(math.radians(70.0 * t)), (radius * 0.55) * (1.0 - math.cos(math.radians(70.0 * t)))))

    part.revolve(profile, 18, mat, offset=(0.0, 0.0, yoke), start=0.0, sweep=360.0)
    part.revolve([(radius * 0.06, yoke + radius * 0.5), (radius * 0.06, yoke + radius * 0.95)], 8,
                 frame)
    part.rotate(start, -tilt, "x", about=(0.0, 0.0, yoke))
    return part


def turret_mount(model, tag, x, y, z, scale=1.0, mat=MATERIALS["turret"], gun=DARK):
    """A small anti-aircraft mount for a headquarters roof.

    Named `turret`, so if a headquarters is ever given something to shoot at, the
    mount traverses onto it. With nothing to shoot at it sits at rest, which is what
    the renderer does with an untargeted turret.
    """
    part = model.part("turret").place((x, y, z))
    part.revolve([(1.15 * scale, 0.0), (1.15 * scale, 0.3), (0.95 * scale, 0.4)], 12, mat)
    part.box((0.0, 0.0, 0.95 * scale), (2.1 * scale, 2.4 * scale, 0.9 * scale), mat, 0.06)
    part.box((0.0, 0.0, 1.5 * scale), (1.5 * scale, 1.7 * scale, 0.35 * scale), mat, 0.06)

    for offset in (-0.42, 0.42):
        start = part.mark()
        part.revolve([(0.11 * scale, 0.0), (0.11 * scale, 2.5 * scale)], 8, gun,
                     offset=(offset * scale, 0.2 * scale, 1.6 * scale))
        part.rotate(start, 90.0, "x", about=(offset * scale, 0.2 * scale, 1.6 * scale))
    return part


def gantry_crane(model, tag, x0, x1, y, z, beam=STEEL, trolley=HAZARD, legs=None):
    """A bridge crane on rails over a hall: two rails, a bridge, a trolley and a
    hoist hook. It is what makes a long hall read as a factory and not a warehouse.
    """
    part = model.part(f"{tag}_gantry")

    for offset in (-1.0, 1.0):
        part.box(((x0 + x1) * 0.5, y + (offset * 3.6), z + 0.25), (x1 - x0, 0.4, 0.5), beam, 0.05)
        part.box(((x0 + x1) * 0.5, y + (offset * 3.6), z + 0.05), (x1 - x0, 0.7, 0.4), DARK, 0.05)

    part.box((x0 * 0.35, y, z + 1.1), (1.1, 7.6, 1.1), beam, 0.05)
    part.box((x0 * 0.35, y, z + 1.9), (0.5, 0.5, 0.7), trolley, 0.04)
    part.box((x0 * 0.35, y, z + 0.9), (0.14, 0.14, 2.0), DARK, 0.04)

    if legs:
        part.box((x0, y, z * 0.5), (0.6, 8.4, z), beam, 0.05)
        part.box((x1, y, z * 0.5), (0.6, 8.4, z), beam, 0.05)


def slewing_crane(model, tag, x, y, z, height, jib, tower=STEEL, jib_mat=HAZARD):
    """A tower crane: a fixed mast and a jib that slews about it.

    The jib is the rotating part and so is named `radar` — the contract's name for
    anything that turns continuously — while the mast stays where it was built.
    """
    mast = model.part(f"{tag}_crane_mast")
    mast.revolve([(0.5, 0.0), (0.42, height)], 8, tower, offset=(x, y, z))
    mast.box((x, y, z + height * 0.55), (2.0, 2.0, 0.35), tower, 0.05)

    jib_part = model.part("radar").place((x, y, z + height))
    jib_part.box((jib * 0.34, 0.0, 0.0), (jib, 0.5, 0.6), jib_mat, 0.05)
    jib_part.box((-jib * 0.16, 0.0, 0.0), (jib * 0.32, 0.7, 0.9), tower, 0.05)
    jib_part.box((jib * 0.62, 0.0, -1.1), (0.16, 0.16, 2.2), DARK, 0.04)
    return jib_part


# --------------------------------------------------------------------------
# Structures
# --------------------------------------------------------------------------

# --------------------------------------------------------------------------
# Σοβιετικοί: poured concrete. Deep reveals, cornices, battered plinths, flues.
# Every one of them is built to look like it was shuttered in a single pour.
# --------------------------------------------------------------------------


def build_hq_soviet():
    """Σοβιετικοί headquarters: a concrete ziggurat on a battered plinth.

    Three setbacks, each one's roof a terrace behind a parapet, so the plan view is
    a set of nested rectangles — the only footprint in the game that reads as a step
    pyramid from above. Two fat boiler flues on the rear terrace and a rotating
    array on the tower finish the silhouette.
    """
    m = Model("soviet_hq")

    apron(m, "apron", 0.0, 0.0, 38.0, 30.0, 0.4, 1.5, CONCRETE, PAINT)
    plinth(m, "plinth", 0.0, 0.0, 34.0, 27.0, 0.0, 3.2, 1.8, CONCRETE, TRIM, 0.5)

    # Main block. Two storeys of windows set three quarters of a metre back into
    # the wall, with a projecting sill under every one of them.
    walls(m, "main", 0.0, 0.0, 26.0, 19.0, 3.7, 15.0, ((5.8, 8.0), (10.4, 12.6)),
          pitch=3.1, win=1.8, thickness=1.1, reveal=0.62, wall=CONCRETE, glass=GLASS,
          sill=0.35, gap=("+y", -3.6, 3.6))
    doorway(m, "main", "+y", 9.5, 0.0, 7.2, 4.4, 3.7, depth=1.9, canopy=2.2, steps=5, hazard=True)
    collar(m, "main_band_low", 0.0, 0.0, 26.0, 19.0, 4.4, 0.5, 0.25, CONCRETE)
    collar(m, "main_band_high", 0.0, 0.0, 26.0, 19.0, 13.3, 0.5, 0.25, CONCRETE)

    roof(m, "main", 0.0, 0.0, 26.0, 19.0, 15.0, overhang=1.0, parapet=1.3, mat=ROOF, fascia=TRIM)

    # Second setback, and the tower on top of that.
    walls(m, "tier", 0.0, -1.0, 19.0, 13.0, 16.4, 22.0, ((18.2, 20.4),), pitch=3.0, win=1.9,
          thickness=0.9, reveal=0.55, wall=CONCRETE, glass=GLASS, sill=0.3)
    roof(m, "tier", 0.0, -1.0, 19.0, 13.0, 22.0, overhang=0.8, parapet=1.2, mat=ROOF, fascia=TRIM)

    walls(m, "tower", -1.0, -3.0, 10.0, 9.0, 23.4, 30.0, ((25.2, 26.6), (27.8, 29.2)),
          pitch=2.6, win=1.1, thickness=0.7, reveal=0.4, wall=CONCRETE, glass=GLASS)
    roof(m, "tower", -1.0, -3.0, 10.0, 9.0, 30.0, overhang=0.7, parapet=1.4, mat=ROOF, fascia=TRIM)

    radar_dish(m, "tower_radar", -1.0, -3.0, 31.3, 3.1, tilt=34.0, mast=2.4, yoke=1.0)
    flag(m, "flag", 3.4, -0.4, 31.3, 4.4, PAINT)

    # Fat flues flanking the setback: the brutalist chimney, and the reason the
    # building looks heavy from every angle.
    chimney(m, "flue_l", -11.4, -6.2, 16.0, 9.6, 1.45, CONCRETE, TRIM, bands=2)
    chimney(m, "flue_r", 11.4, -6.2, 16.0, 9.6, 1.45, CONCRETE, TRIM, bands=2)

    vents(m, "main_vents", [(-8.0, 7.0), (-3.0, 7.6), (2.0, 7.6), (7.0, 7.0)], 16.0, 0.6, 2.2)
    tank(m, "tank", 11.4, -7.0, 16.0, 1.8, 3.4, PANEL)
    hvac_row(m, "main_plant", (0.0, 6.9), 16.0, 2, 5.2, (3.2, 2.2, 1.7), STEEL)

    # An anti-aircraft mount on the corner of the roof: the part named `turret`.
    turret_mount(m, "aa", 8.0, 7.0, 16.0, scale=1.3)

    return m.build()


def build_factory_soviet():
    """Σοβιετικοί factory: one enormous hall, riveted, between two fat flues.

    A single 42-metre shed rather than a row of bays, because the point of the
    faction is that it builds in one pour and does not care what it costs. Pilasters
    every four metres, a deep reveal behind each one, a gate big enough for a tank
    column, and a gantry crane over the whole span.
    """
    m = Model("soviet_factory")

    apron(m, "apron", 0.0, -1.0, 46.0, 36.0, 0.35, 1.8, CONCRETE, PAINT)
    plinth(m, "plinth", 0.0, 0.0, 44.0, 22.0, 0.0, 1.4, 1.2, CONCRETE, TRIM, 0.4)

    walls(m, "hall", 0.0, 0.0, 42.0, 20.0, 1.4, 13.5, ((4.0, 6.6), (8.6, 11.2)),
          pitch=3.0, win=1.9, thickness=1.3, reveal=0.7, wall=CONCRETE, glass=GLASS,
          sill=0.4, gap=("+y", -6.5, 2.5))
    gate(m, "hall", "+y", 10.0, -2.0, 9.0, 6.2, 1.4, depth=2.0)

    # Pilasters on the same 3 m module as the windows, so every one of them lands
    # on a pier instead of cutting a window in half.
    ribs(m, "hall_piers", 0.0, 0.0, 42.0, 20.0, 1.4, 13.5, 3.0, 1.2, 0.35, CONCRETE)

    roof(m, "hall", 0.0, 0.0, 42.0, 20.0, 13.5, overhang=1.2, parapet=1.5, mat=ROOF, fascia=TRIM)
    roof_monitor(m, "mon_l", 0.0, -7.0, 34.0, 5.2, 14.5, 1.9, ROOF, GLASS)
    roof_monitor(m, "mon_r", 0.0, 7.0, 34.0, 5.2, 14.5, 1.9, ROOF, GLASS)
    gantry_crane(m, "crane", -18.0, 18.0, 0.0, 14.5)

    # The boiler house at the back, with the two flues rising out of it. The left
    # one is named `barrel`, which is the part the game pulls factory smoke from,
    # and its origin is at the mouth so the plume leaves the chimney and not the roof.
    plinth(m, "boiler_plinth", -11.0, -14.0, 15.0, 10.0, 0.0, 1.0, 0.8, CONCRETE, TRIM, 0.3)
    walls(m, "boiler", -11.0, -14.0, 13.0, 8.5, 1.0, 9.0, ((3.0, 5.4),), pitch=2.8, win=1.6,
          thickness=0.9, reveal=0.5, wall=CONCRETE, glass=GLASS, sill=0.3)
    roof(m, "boiler", -11.0, -14.0, 13.0, 8.5, 9.0, overhang=0.7, parapet=1.0, mat=ROOF, fascia=TRIM)
    chimney(m, "barrel", -14.2, -14.5, 9.6, 20.4, 2.0, CONCRETE, TRIM, bands=3)
    chimney(m, "flue_r", -8.0, -14.5, 9.6, 20.4, 2.0, CONCRETE, TRIM, bands=3)

    # The yard: a coal heap at the front where it can be seen, a water tank and a
    # row of crates, which is what gives a 42-metre shed a scale to read.
    m.part("coal").frustum((17.0, 15.0, 0.35), (11.0, 8.0), (4.0, 3.5), 5.4,
                           (0.30, 0.24, 0.19, 0.0), 0.11)
    tank(m, "hall_tank", 18.0, 7.0, 14.5, 2.2, 3.6, PANEL)
    hvac_row(m, "hall_plant", (-6.0, 7.6), 14.5, 3, 4.4, (3.0, 2.0, 1.6), STEEL)
    vents(m, "hall_vents", [(-14.0, -7.6), (-10.0, -7.6), (-6.0, -7.6), (-2.0, -7.6)], 14.5, 0.7, 2.4)

    # An extractor that turns on the hall roof: the rotating part, named `radar`
    # because the contract's name for "this turns continuously" is that one.
    fan(m, "hall_fan", -14.0, 0.0, 14.5, 2.0, blades=6, powered=True)

    for i in range(4):
        m.part("crates").box((-19.0 + (i * 1.4), 15.0, 1.4), (1.2, 1.2, 2.4),
                             MATERIALS["crate"], 0.09)

    return m.build()


def build_power_soviet():
    """Σοβιετικοί power plant: a turbine hall under three fat stacks.

    The stacks are the tallest thing the faction owns and they lean on nothing: a
    coal bunker and a conveyor feed the boiler house from one end, and the hall is a
    single massive box with the window rows of a cathedral.
    """
    m = Model("soviet_power")

    plinth(m, "plinth", 0.0, 0.0, 30.0, 26.0, 0.0, 1.6, 1.3, CONCRETE, TRIM, 0.45)

    walls(m, "hall", 2.0, 2.0, 22.0, 15.0, 1.6, 12.0, ((3.4, 5.8), (7.6, 10.0)),
          pitch=2.9, win=1.8, thickness=1.1, reveal=0.6, wall=CONCRETE, glass=GLASS, sill=0.35)
    roof(m, "hall", 2.0, 2.0, 22.0, 15.0, 12.0, overhang=1.0, parapet=1.4, mat=ROOF, fascia=TRIM)
    roof_monitor(m, "mon", 2.0, 2.0, 16.0, 6.0, 13.0, 2.2, ROOF, GLASS)
    vents(m, "hall_vents", [(-4.0, -3.0), (1.0, -3.0), (6.0, -3.0)], 13.0, 0.9, 2.6)

    # Three fat stacks in a row behind the hall. `stack_l` is the flue the game
    # smokes a power plant out of, and its origin is at the mouth. They are fat on
    # purpose: this plant is scaled to 12 metres in the world, so a slim stack
    # becomes a hairline and stops reading as a chimney at all.
    chimney(m, "stack_l", -6.0, -9.0, 1.6, 30.0, 2.1, CONCRETE, TRIM, bands=3)
    chimney(m, "stack_c", 0.0, -9.0, 1.6, 30.0, 2.1, CONCRETE, TRIM, bands=3)
    chimney(m, "stack_r", 6.0, -9.0, 1.6, 30.0, 2.1, CONCRETE, TRIM, bands=3)

    # The boiler house, ribbed like a cooling jacket.
    walls(m, "boiler", -10.5, 3.0, 9.0, 13.0, 1.6, 18.0, ((5.0, 8.0), (11.0, 14.0)),
          pitch=3.2, win=1.6, thickness=1.0, reveal=0.55, wall=CONCRETE, glass=GLASS)
    roof(m, "boiler", -10.5, 3.0, 9.0, 13.0, 18.0, overhang=0.8, parapet=1.2, mat=ROOF, fascia=TRIM)
    ribs(m, "boiler_ribs", -10.5, 3.0, 9.0, 13.0, 1.6, 18.0, 3.6, 0.9, 0.4, CONCRETE)

    # Coal bunker and conveyor: the plant has to be fed from somewhere.
    m.part("bunker").box((-11.5, -8.0, 6.0), (6.0, 11.0, 12.0), CONCRETE, 0.08)
    m.part("bunker").frustum((-11.5, -8.0, 1.6), (6.6, 11.6), (4.6, 9.0), 2.6, TRIM)
    m.part("conveyor").extrude([(-4.0, 10.0), (1.4, 15.0), (1.4, 14.0), (-4.0, 9.0)],
                               "x", -13.0, -10.0, STEEL)
    m.part("conveyor").extrude([(-4.0, 10.8), (1.4, 15.8), (1.4, 15.0), (-4.0, 10.0)],
                               "x", -12.6, -10.4, DARK)

    # The induced-draught fan on the hall roof: the one part of a power station
    # that genuinely turns.
    fan(m, "hall_fan", 6.5, 6.5, 13.0, 3.0, blades=7, powered=True)
    hvac_row(m, "hall_plant", (-4.0, 7.0), 13.0, 2, 4.0, (3.0, 2.0, 1.6), STEEL)
    mast(m, "mast", 11.0, -9.0, 1.6, 12.0, 0.18, STEEL, arms=2)

    return m.build()


def build_nuclear_soviet():
    """Σοβιετικοί nuclear plant: a containment dome on a reactor block, between two
    cooling towers.

    Unmistakable from above, which matters: it is the one structure worth raiding,
    so a player has to be able to find it at a glance. A polar crane slews on the
    containment roof — the part named `radar` — because that is what actually turns
    up there.
    """
    m = Model("soviet_nuclear")

    plinth(m, "plinth", 0.0, -1.0, 30.0, 26.0, 0.0, 1.8, 1.4, CONCRETE, TRIM, 0.5)

    # Reactor block: brutalist, with the window rows of an office and the mass of a
    # bunker.
    walls(m, "reactor", 0.0, 0.0, 18.0, 16.0, 1.8, 16.0,
          ((4.0, 6.6), (8.6, 11.2), (12.6, 14.8)),
          pitch=3.0, win=1.8, thickness=1.1, reveal=0.6, wall=CONCRETE, glass=GLASS, sill=0.35)
    roof(m, "reactor", 0.0, 0.0, 18.0, 16.0, 16.0, overhang=1.0, parapet=0.0, mat=ROOF, fascia=TRIM)

    # Containment: a cylinder under a dome, ringed at the springing line, and tall
    # enough to be the peak of the plant — the dome is what says "nuclear", so it
    # cannot be the thing hidden behind a cooling tower.
    m.part("containment").revolve([(7.0, 16.0), (7.0, 26.0)], 24, CONCRETE)
    m.part("hazard").revolve([(7.15, 16.0), (7.15, 17.1)], 24, HAZARD, variation=0.03)
    dome_shell(m, "shield", 0.0, 0.0, 26.0, 7.0, 9.0, segments=24, rings=6, mat=CONCRETE,
               ring=TRIM)

    slewing_crane(m, "polar", 0.0, 0.0, 34.4, 1.6, 9.0)
    mast(m, "containment_mast", 6.5, 6.5, 17.0, 8.0, 0.16, STEEL, arms=2)

    # Two cooling towers set back behind the containment, so the dome reads in
    # front of them rather than between them.
    cooling_tower(m, "cool_l", -10.0, -12.5, 1.2, 4.6, 2.9, 3.3, 31.0)
    cooling_tower(m, "cool_r", 10.0, -12.5, 1.2, 4.6, 2.9, 3.3, 31.0)

    # Turbine hall and a switchyard in front.
    walls(m, "turbine", 0.0, 11.5, 20.0, 8.0, 1.8, 9.0, ((3.4, 5.6),), pitch=3.0, win=1.8,
          thickness=0.9, reveal=0.5, wall=CONCRETE, glass=GLASS, sill=0.3)
    roof(m, "turbine", 0.0, 11.5, 20.0, 8.0, 9.0, overhang=0.8, parapet=1.1, mat=ROOF, fascia=TRIM)
    hvac_row(m, "turbine_plant", (0.0, 11.5), 10.0, 4, 4.0, (2.6, 2.0, 1.5), STEEL)
    vents(m, "turbine_vents", [(-6.0, 13.5), (0.0, 13.5), (6.0, 13.5)], 10.0, 0.6, 2.0)

    return m.build()


def build_bureau_soviet():
    """Σοβιετικοί design bureau: a research tower over a wind tunnel.

    The bureau is the tallest thing the faction builds — a stepped concrete shaft
    carrying a rotating array — with a test hall beside it whose circular intake is
    the one round hole in a faction made of rectangles.
    """
    m = Model("soviet_bureau")

    plinth(m, "plinth", 0.0, -2.0, 26.0, 22.0, 0.0, 2.4, 1.5, CONCRETE, TRIM, 0.5)

    walls(m, "base", 0.0, -3.0, 21.0, 16.0, 2.4, 8.0, ((4.4, 7.0),), pitch=3.0, win=1.9,
          thickness=1.0, reveal=0.6, wall=CONCRETE, glass=GLASS, sill=0.35,
          gap=("+y", -3.0, 3.0))
    doorway(m, "base", "+y", 5.0, 0.0, 6.0, 3.6, 2.4, depth=1.6, canopy=1.8, steps=4, hazard=True)
    roof(m, "base", 0.0, -3.0, 21.0, 16.0, 8.0, overhang=0.9, parapet=1.3, mat=ROOF, fascia=TRIM)

    # The shaft: deep slot windows between pilasters, stepped crown on top.
    walls(m, "shaft", 0.0, -4.0, 13.0, 12.0, 9.0, 22.0, ((10.6, 13.4), (15.0, 17.8), (19.2, 21.4)),
          pitch=2.6, win=1.4, thickness=0.9, reveal=0.5, wall=CONCRETE, glass=GLASS)
    ribs(m, "shaft_piers", 0.0, -4.0, 13.0, 12.0, 9.0, 22.0, 3.2, 1.4, 0.35, CONCRETE)
    roof(m, "shaft", 0.0, -4.0, 13.0, 12.0, 22.0, overhang=0.8, parapet=1.2, mat=ROOF, fascia=TRIM)

    m.part("crown").frustum((0.0, -4.0, 23.2), (9.0, 8.0), (6.4, 5.6), 4.6, CONCRETE)
    m.part("crown").box((0.0, -4.0, 28.2), (7.0, 6.2, 0.6), TRIM, 0.05)
    radar_dish(m, "crown_radar", 0.0, -4.0, 28.5, 3.4, tilt=30.0, mast=2.2, yoke=1.0)

    # The wind tunnel: a low hall with a bellmouth intake at one end.
    walls(m, "tunnel", -1.0, 12.0, 20.0, 9.0, 2.4, 8.0, ((5.2, 7.2),), pitch=3.0, win=1.6,
          thickness=0.9, reveal=0.5, wall=CONCRETE, glass=GLASS)
    roof(m, "tunnel", -1.0, 12.0, 20.0, 9.0, 8.0, overhang=0.8, parapet=1.1, mat=ROOF, fascia=TRIM)
    m.part("intake").revolve([(3.4, 2.4), (3.4, 3.2), (2.9, 3.4)], 20, PANEL, offset=(-11.5, 12.0, 0.0))
    m.part("intake").revolve([(2.85, 2.5), (2.85, 3.1)], 20, DARK, offset=(-11.5, 12.0, 0.0))
    tank(m, "tunnel_tank", 6.0, 12.0, 9.0, 1.8, 3.0, PANEL)
    hvac_row(m, "bureau_plant", (8.8, -1.0), 9.0, 2, 4.6, (2.6, 2.0, 1.5), STEEL, "y")
    mast(m, "bureau_mast", 8.6, -8.0, 9.0, 9.0, 0.16, STEEL, arms=2)

    return m.build()


# --------------------------------------------------------------------------
# Κινέζοι: mass production. Long low sheds, sawtooth and barrel-vault roofs,
# corrugated ribs, repeating bays, and always more of them than anyone counted.
# --------------------------------------------------------------------------


def build_hq_chinese():
    """Κινέζοι headquarters: a walled compound of identical sheds.

    Mass production applied to a headquarters. Rather than one monument there is a
    perimeter wall with the same pier repeated forty times, three identical
    administration sheds inside it, and one central drum carrying the array — which
    from above reads as a rectangle inside a rectangle, a plan nothing else has.
    """
    m = Model("chinese_hq")

    apron(m, "apron", 0.0, 0.0, 38.0, 30.0, 0.35, 1.4, CONCRETE, PAINT)

    # Perimeter wall: brick infill, concrete piers, a capped head. The gate in the
    # front wall splits it into two runs.
    runs = [("+x", 18.0, (-14.5, 14.5)), ("-x", -18.0, (-14.5, 14.5)),
            ("-y", -14.0, (-17.5, 17.5)),
            ("+y", 14.0, (-17.5, -6.0)), ("+y", 14.0, (6.0, 17.5))]

    for (face, plane, span) in runs:
        _piece(m.part("wall"), face, plane, span[0], span[1], 0.35, 4.1, 0.0, 1.0, BRICK)
        _piece(m.part("wall_cap"), face, plane, span[0], span[1], 4.1, 4.55, -0.18, 1.18, TRIM)
        count = max(2, int((span[1] - span[0]) / 3.0))
        step = (span[1] - span[0]) / count

        for i in range(count + 1):
            a = span[0] + (i * step)
            _piece(m.part("wall_piers"), face, plane, a - 0.6, a + 0.6, 0.35, 4.9, -0.28, 1.28,
                   CONCRETE)

    # The gatehouse: a drum over the gate, with the only vaulted roof in the game.
    walls(m, "gatehouse", 0.0, 13.6, 13.0, 5.5, 0.35, 8.4, ((2.0, 4.6),), pitch=3.2, win=1.8,
          thickness=0.8, reveal=0.3, wall=BRICK, glass=GLASS, sill=0.25,
          gap=("+y", -3.6, 3.6))
    doorway(m, "gatehouse", "+y", 16.35, 0.0, 7.0, 5.0, 0.35, depth=1.6, frame=CONCRETE,
            canopy=1.6, hazard=True)
    vault(m, "gate_vault", -6.5, 6.5, 13.6, 3.4, 8.4, segments=10)

    # Three identical administration sheds along the back of the compound.
    for i, x in enumerate((-11.0, 0.0, 11.0)):
        walls(m, f"shed{i}", x, -9.0, 10.0, 6.0, 0.35, 4.6, ((1.6, 3.4),), pitch=2.6, win=1.6,
              thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, sill=0.2)
        ribs(m, f"shed{i}_ribs", x, -9.0, 10.0, 6.0, 0.5, 4.6, 1.2, 0.22, 0.16, PANEL)
        vault(m, f"shed{i}_vault", x - 5.0, x + 5.0, -9.0, 3.0, 4.6, segments=10)

    # The central drum: the block the headquarters is recognised by.
    walls(m, "drum", 0.0, 2.0, 13.0, 10.0, 0.35, 11.0, ((2.4, 5.0), (6.6, 9.2)), pitch=3.0,
          win=1.8, thickness=0.8, reveal=0.3, wall=BRICK, glass=GLASS, sill=0.25)
    ribs(m, "drum_ribs", 0.0, 2.0, 13.0, 10.0, 0.5, 11.0, 1.3, 0.24, 0.18, PANEL)
    roof(m, "drum", 0.0, 2.0, 13.0, 10.0, 11.0, overhang=0.7, parapet=1.0, mat=ROOF, fascia=TRIM)
    vault(m, "drum_vault", -6.5, 6.5, 2.0, 3.0, 12.0, segments=12)
    radar_dish(m, "drum_radar", 0.0, 2.0, 16.0, 2.8, tilt=32.0, mast=2.2, yoke=0.9)

    # Cheap plant: a water tower in the courtyard, vents on the roofs, a boiler
    # flue at the back corner and a flag over the gate.
    tank(m, "water_tower", 15.0, 5.0, 0.35, 2.2, 3.6, PANEL)
    vents(m, "drum_vents", [(-4.5, -2.6), (0.0, -2.6), (4.5, -2.6)], 12.0, 0.5, 1.6)
    hvac_row(m, "drum_plant", (0.0, -2.6), 12.0, 2, 5.0, (2.4, 1.8, 1.4), STEEL)
    chimney(m, "boiler", -15.4, -11.0, 0.35, 12.0, 0.8, BRICK, TRIM, bands=2)
    flag(m, "flag", 6.2, 11.6, 9.2, 3.6, PAINT)

    # Stores in the courtyard corners: an empty compound reads as an unfinished
    # one, and four boxes are cheaper than another building.
    for (px, py) in ((-14.0, 8.5), (14.0, -3.5), (-14.0, -3.5)):
        m.part("crates").box((px, py, 1.5), (2.6, 2.6, 2.6), MATERIALS["crate"], 0.09)
        m.part("crates").box((px, py, 3.2), (1.8, 1.8, 1.0), MATERIALS["crate"], 0.09)

    return m.build()


def build_factory_chinese():
    """Κινέζοι factory: three identical long sheds with sawtooth roofs.

    The read from above is the whole point: three parallel striped roofs in a row,
    each one the same, each one ribbed every 1.1 metres, with a row of slim stacks
    at the end and a curtain of loading bays along the front. The faction does not
    build a factory, it stamps one out.
    """
    m = Model("chinese_factory")

    apron(m, "apron", -4.0, 0.0, 50.0, 32.0, 0.3, 1.6, CONCRETE, PAINT)

    for i, y in enumerate((-9.0, 0.0, 9.0)):
        walls(m, f"shed{i}", 0.0, y, 42.0, 8.0, 0.3, 6.4, ((1.5, 3.3), (3.9, 5.7)),
              pitch=2.8, win=1.7, thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, sill=0.2)
        ribs(m, f"shed{i}_ribs", 0.0, y, 42.0, 8.0, 0.45, 6.4, 1.4, 0.24, 0.15, PANEL)
        # Two deep teeth rather than six shallow ones: a sawtooth that is only a
        # texture from 300 metres says nothing, and the roof is what the player sees.
        sawtooth(m, f"shed{i}", -21.0, 21.0, y, 8.0, 6.4, 2, 2.8, ROOF, GLASS, CONCRETE)

    # A row of slim stacks at the far end, and the boiler house they come out of:
    # the same bay repeated, the same stack repeated, three times over.
    walls(m, "boiler", -23.5, 0.0, 7.0, 22.0, 0.3, 6.0, ((2.0, 4.2),), pitch=3.0, win=1.8,
          thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, faces="y")
    ribs(m, "boiler_ribs", -23.5, 0.0, 7.0, 22.0, 0.45, 6.0, 1.2, 0.22, 0.15, PANEL)
    sawtooth(m, "boiler", -27.0, -20.0, 0.0, 22.0, 6.0, 2, 1.9, ROOF, GLASS, CONCRETE)

    chimney(m, "barrel", -25.5, -7.0, 7.0, 15.0, 0.85, BRICK, TRIM, bands=2)
    chimney(m, "flue_b", -25.5, 0.0, 7.0, 15.0, 0.85, BRICK, TRIM, bands=2)
    chimney(m, "flue_c", -25.5, 7.0, 7.0, 15.0, 0.85, BRICK, TRIM, bands=2)

    # Loading bays along the front shed, and a hard standing for the lorries.
    dock_doors(m, "front", "+y", 13.0, (-16.0, -9.0, -2.0, 5.0, 12.0), 5.0, 4.2, 0.3)
    m.part("yard_paint").box((-2.0, 14.6, 0.42), (40.0, 2.6, 0.14), PAINT, 0.05)
    tank(m, "water_tower", 16.0, -13.0, 0.3, 2.4, 4.0, PANEL)
    hvac_row(m, "yard_plant", (-18.0, 14.6), 0.5, 5, 2.2, (1.6, 1.4, 1.2), PANEL)

    # A yard crane that slews: the part named `radar`, and the only thing on a
    # Chinese factory that moves.
    slewing_crane(m, "crane", 14.0, 12.0, 0.3, 9.0, 8.0)

    return m.build()


def build_power_chinese():
    """Κινέζοι power plant: one long low hall under a comb of five slim stacks.

    Five stacks rather than three fat ones, spaced evenly along the back — a comb
    from above, where the Σοβιετικοί plant is three fat columns and the Δυτικοί one
    is a field of fan circles. Three cooling fans stand on the annex roof and a
    switchyard of identical lattice masts sits in front.
    """
    m = Model("chinese_power")

    plinth(m, "plinth", 0.0, 0.0, 34.0, 26.0, 0.0, 1.2, 1.0, CONCRETE, TRIM, 0.4)

    walls(m, "hall", 0.0, 3.0, 30.0, 14.0, 1.2, 8.6, ((3.0, 5.2),), pitch=2.8, win=1.7,
          thickness=0.7, reveal=0.28, wall=BRICK, glass=GLASS, sill=0.22)
    ribs(m, "hall_ribs", 0.0, 3.0, 30.0, 14.0, 1.3, 8.6, 1.2, 0.22, 0.16, PANEL)
    roof(m, "hall", 0.0, 3.0, 30.0, 14.0, 8.6, overhang=0.8, parapet=0.9, mat=ROOF, fascia=TRIM)

    for i, x in enumerate((-12.0, -6.0, 0.0, 6.0, 12.0)):
        chimney(m, "stack_l" if i == 2 else f"stack_{i}", x, -6.5, 1.2, 38.0, 0.9,
                BRICK, TRIM, bands=2)

    # The turbine annex in front, with three fans on it; the middle one turns.
    walls(m, "annex", -2.0, 14.0, 20.0, 8.0, 1.2, 6.4, ((2.6, 4.6),), pitch=3.0, win=1.8,
          thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, sill=0.2)
    ribs(m, "annex_ribs", -2.0, 14.0, 20.0, 8.0, 1.3, 6.4, 1.2, 0.22, 0.16, PANEL)
    sawtooth(m, "annex", -12.0, 8.0, 14.0, 8.0, 6.4, 2, 2.2, ROOF, GLASS, CONCRETE)

    fan(m, "fan_a", -9.0, 14.0, 7.4, 2.2, blades=6, powered=False)
    fan(m, "fan_b", -2.0, 14.0, 7.4, 2.2, blades=6, powered=True)
    fan(m, "fan_c", 5.0, 14.0, 7.4, 2.2, blades=6, powered=False)

    # Switchyard in front: a painted compound and a grid of identical masts.
    m.part("yard_paint").box((0.0, 21.0, 0.16), (30.0, 10.0, 0.2), PAINT, 0.05)

    for i in range(4):
        mast(m, f"pylon_{i}", -12.0 + (i * 8.0), 21.0, 1.4, 7.0, 0.14, STEEL, arms=1)

    pipes(m, "busbars", 21.0, 7.0, -12.0, 12.0, 0.1, 3, 0.9, STEEL)
    tank(m, "water_tower", 15.0, -4.0, 1.2, 2.2, 3.4, PANEL)

    return m.build()


def build_nuclear_chinese():
    """Κινέζοι nuclear plant: two identical reactor units, four cooling towers.

    Two of everything, side by side, because that is cheaper than one of anything
    cleverer. From above it is a pair of domes with a row of towers behind them,
    where the Σοβιετικοί plant is one dome between two towers.
    """
    m = Model("chinese_nuclear")

    plinth(m, "plinth", 0.0, -2.0, 30.0, 28.0, 0.0, 1.4, 1.1, CONCRETE, TRIM, 0.45)

    for side, x in ((-1, -8.5), (1, 8.5)):
        walls(m, f"unit{'l' if side < 0 else 'r'}", x, 1.0, 13.0, 11.0, 1.4, 8.0,
              ((3.0, 5.2),), pitch=2.8, win=1.7, thickness=0.7, reveal=0.3, wall=BRICK,
              glass=GLASS, sill=0.22, gap=("+y", 5.2, 6.8))
        roof(m, f"unit{'l' if side < 0 else 'r'}", x, 1.0, 13.0, 11.0, 8.0, overhang=0.7,
             parapet=0.0, mat=ROOF, fascia=TRIM)
        m.part(f"containment{'l' if side < 0 else 'r'}").revolve([(4.6, 8.0), (4.6, 14.0)], 18,
                                                                CONCRETE, offset=(x, 1.0, 0.0))
        m.part(f"hazard{'l' if side < 0 else 'r'}").revolve([(4.72, 8.0), (4.72, 8.9)], 18,
                                                            HAZARD, offset=(x, 1.0, 0.0), variation=0.03)
        dome_shell(m, f"shield{'l' if side < 0 else 'r'}", x, 1.0, 14.0, 4.6, 5.6,
                   segments=18, rings=5, mat=CONCRETE, ring=TRIM)
        vents(m, f"unit_vents{'l' if side < 0 else 'r'}",
              [(x - 3.0, -4.0), (x + 3.0, -4.0)], 9.0, 0.6, 2.0)

    # A connecting corridor between the units, and a row of four cooling towers.
    m.part("corridor").box((0.0, 1.0, 3.6), (5.0, 6.0, 4.4), BRICK, 0.07)
    vault(m, "corridor_vault", -2.5, 2.5, 1.0, 2.6, 5.8, segments=8)

    for i, x in enumerate((-13.0, -4.5, 4.5, 13.0)):
        cooling_tower(m, f"cool{i}", x, -12.5, 1.0, 3.6, 2.2, 2.6, 33.0, segments=16)

    # An auxiliary shed with the plant on it, and the fan that turns on top.
    walls(m, "aux", 0.0, 12.5, 24.0, 7.0, 1.4, 6.0, ((3.0, 4.6),), pitch=3.0, win=1.8,
          thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, sill=0.2)
    ribs(m, "aux_ribs", 0.0, 12.5, 24.0, 7.0, 1.5, 6.0, 1.2, 0.22, 0.16, PANEL)
    roof(m, "aux", 0.0, 12.5, 24.0, 7.0, 6.0, overhang=0.7, parapet=0.8, mat=ROOF, fascia=TRIM)
    fan(m, "aux_fan", -6.0, 12.5, 7.0, 1.8, blades=6, powered=True)
    tank(m, "aux_tank", 8.0, 12.5, 7.0, 1.8, 3.0, PANEL)

    return m.build()


def build_bureau_chinese():
    """Κινέζοι design bureau: a compound of identical laboratories round one tower.

    Three of the same lab shed, a wall around them, and a single block tall enough
    to carry the big dish — the bureau as an industrial estate rather than as an
    institution.
    """
    m = Model("chinese_bureau")

    plinth(m, "plinth", 0.0, -1.0, 28.0, 24.0, 0.0, 1.2, 1.0, CONCRETE, TRIM, 0.4)

    for i, x in enumerate((-8.5, 0.0, 8.5)):
        walls(m, f"lab{i}", x, -8.0, 8.0, 6.5, 1.2, 5.4, ((2.4, 4.2),), pitch=2.8, win=1.7,
              thickness=0.6, reveal=0.25, wall=BRICK, glass=GLASS, sill=0.2)
        ribs(m, f"lab{i}_ribs", x, -8.0, 8.0, 6.5, 1.35, 5.4, 1.1, 0.2, 0.15, PANEL)
        vault(m, f"lab{i}_vault", x - 4.0, x + 4.0, -8.0, 3.2, 5.4, segments=10)

    # The test stand: a hall with a lattice gantry over one end.
    walls(m, "stand", 0.0, 6.0, 20.0, 9.0, 1.2, 7.0, ((3.0, 5.4),), pitch=3.0, win=1.8,
          thickness=0.7, reveal=0.28, wall=BRICK, glass=GLASS, sill=0.22)
    ribs(m, "stand_ribs", 0.0, 6.0, 20.0, 9.0, 1.35, 7.0, 1.2, 0.22, 0.16, PANEL)
    sawtooth(m, "stand", -10.0, 10.0, 6.0, 9.0, 7.0, 4, 2.2, ROOF, GLASS, CONCRETE)
    gantry_crane(m, "stand_gantry", -9.0, 9.0, 6.0, 8.4, STEEL, HAZARD, legs=True)

    # The one block tall enough to carry the dish: the bureau as a factory estate
    # with a radio mast, which is what a Κινέζοι research institute looks like.
    walls(m, "tower", 0.0, 0.0, 12.0, 10.0, 1.2, 22.0,
          ((3.0, 5.4), (7.4, 9.8), (12.0, 14.4), (16.6, 19.0), (20.4, 21.6)),
          pitch=2.8, win=1.7, thickness=0.7, reveal=0.28, wall=BRICK, glass=GLASS, sill=0.22)
    ribs(m, "tower_ribs", 0.0, 0.0, 12.0, 10.0, 1.35, 22.0, 1.2, 0.22, 0.16, PANEL)
    roof(m, "tower", 0.0, 0.0, 12.0, 10.0, 22.0, overhang=0.6, parapet=0.9, mat=ROOF, fascia=TRIM)
    vault(m, "tower_vault", -6.0, 6.0, 0.0, 3.0, 23.0, segments=12)
    radar_dish(m, "tower_radar", 0.0, 0.0, 26.6, 4.2, tilt=30.0, mast=2.6, yoke=1.0)

    chimney(m, "barrel", 11.5, -10.0, 1.2, 14.0, 0.8, BRICK, TRIM, bands=2)
    tank(m, "tower_tank", -12.0, 3.0, 1.2, 2.0, 3.2, PANEL)
    hvac_row(m, "bureau_plant", (0.0, 4.4), 23.0, 3, 3.6, (2.2, 1.6, 1.4), PANEL)

    return m.build()


# --------------------------------------------------------------------------
# Δυτικοί: refined. Glazed curtain walls in bands, thin mullions, clean edges,
# flat roofs with plant standing in neat rows on them.
# --------------------------------------------------------------------------


def build_hq_western():
    """Δυτικοί headquarters: a glazed slab on a podium, with a helipad.

    The podium roof is a terrace, and the terrace is the read from above: a big
    light deck with a marked helipad at one end, plant in a neat row along the
    other, and a curtain-walled tower standing on it. Nothing else in the game has
    a helipad, which makes the headquarters findable from the far side of the map.
    """
    m = Model("western_hq")

    apron(m, "apron", 0.0, 0.0, 38.0, 28.0, 0.35, 1.5, CONCRETE, TRIM)
    plinth(m, "plinth", 0.0, 0.0, 36.0, 26.0, 0.0, 1.6, 0.8, CONCRETE, TRIM, 0.3)

    # Podium: a glazed band along the top of each wall, which is what a curtain
    # wall is — the glass is flush and the wall is nothing but mullions.
    walls(m, "podium", 0.0, 0.0, 34.0, 24.0, 1.6, 7.0, ((1.4, 4.6),), pitch=2.4, win=2.0,
          thickness=0.7, reveal=0.1, wall=CONCRETE, glass=GLASS, mullion=0.42,
          gap=("+y", -5.0, 5.0), lit_every=4)
    doorway(m, "podium", "+y", 12.0, 0.0, 9.0, 4.6, 1.6, depth=1.4, frame=STEEL, canopy=2.6)
    roof(m, "podium", 0.0, 0.0, 34.0, 24.0, 7.0, overhang=1.2, parapet=1.2, mat=ROOF, fascia=TRIM,
         deck=0.35)

    # The helipad, marked with an H: the one piece of paint on the model that is
    # not the faction colour, and the reason a Δυτικοί headquarters is findable
    # from the far side of the map.
    m.part("helipad").revolve([(5.5, 7.95), (5.5, 8.3)], 26, DARK)
    m.part("helipad").box((-13.0, 4.0, 8.45), (0.5, 5.0, 0.16), LAMP, 0.03)
    m.part("helipad").box((-11.0, 4.0, 8.45), (0.5, 5.0, 0.16), LAMP, 0.03)
    m.part("helipad").box((-12.0, 4.0, 8.45), (2.5, 0.5, 0.16), LAMP, 0.03)

    # Tower: six glazed bands up a slender slab, set back on the podium so the
    # terrace stays visible from above.
    walls(m, "tower", 4.0, -4.0, 20.0, 11.0, 8.5, 26.0,
          ((9.4, 11.4), (13.0, 15.0), (16.6, 18.6), (20.2, 22.2), (23.4, 25.2)),
          pitch=2.2, win=1.9, thickness=0.6, reveal=0.08, wall=PANEL, glass=GLASS, mullion=0.4,
          lit_every=5)
    roof(m, "tower", 4.0, -4.0, 20.0, 11.0, 26.0, overhang=0.9, parapet=1.3, mat=ROOF, fascia=TRIM)
    radar_dish(m, "tower_radar", 4.0, -4.0, 27.2, 3.0, tilt=28.0, mast=2.2, yoke=0.9)
    mast(m, "tower_mast", 12.0, -4.0, 27.2, 7.0, 0.16, STEEL, arms=2)

    # Neat plant in a row on both roofs: HVAC units, a chiller, a water tank, and
    # a grid of roof lights on the part of the terrace the tower does not cover.
    hvac_row(m, "tower_plant", (4.0, 0.6), 27.2, 4, 4.4, (2.6, 2.0, 1.6), PANEL)
    hvac_row(m, "podium_plant", (10.0, 11.5), 7.95, 3, 3.2, (2.2, 1.8, 1.4), PANEL)
    tank(m, "podium_tank", -16.0, -8.0, 7.95, 1.6, 2.8, PANEL)
    pipes(m, "tower_pipes", 1.0, 27.4, -3.0, 11.0, 0.18, 3, 0.55, STEEL)
    skylights(m, "podium_skylights", 2.0, 7.0, 26.0, 5.0, 7.95, 1, 6)

    return m.build()


def build_factory_western():
    """Δυτικοί factory: a clean glazed shed with a curtain-walled office block.

    Flat roof, crisp fascia, a grid of roof lights and plant in rows: the faction
    that designs its buildings. Two slim stacks at the back, five loading bays along
    the front, and an office block on the end that is taller than the shed.
    """
    m = Model("western_factory")

    apron(m, "apron", 0.0, -1.0, 50.0, 32.0, 0.3, 1.4, CONCRETE, TRIM)
    plinth(m, "plinth", 0.0, 0.0, 36.0, 24.0, 0.0, 1.0, 0.7, CONCRETE, TRIM, 0.25)

    walls(m, "hall", 0.0, 0.0, 34.0, 22.0, 1.0, 12.0, ((9.4, 11.6),), pitch=3.0, win=1.8,
          thickness=0.7, reveal=0.12, wall=CONCRETE, glass=GLASS, mullion=0.4, lit_every=6,
          gap=("+y", -14.0, -4.0))
    dock_doors(m, "hall", "+y", 11.0, (-13.0, -8.5, -4.0, 0.5, 5.0), 3.6, 4.0, 1.0)
    roof(m, "hall", 0.0, 0.0, 34.0, 22.0, 12.0, overhang=1.3, parapet=1.2, mat=ROOF, fascia=TRIM)
    roof_monitor(m, "mon", 0.0, 0.0, 30.0, 7.0, 13.0, 2.0, ROOF, GLASS)
    skylights(m, "skylights", 0.0, 0.0, 26.0, 14.0, 13.4, 3, 6)
    hvac_row(m, "hall_plant", (-11.0, -8.0), 13.4, 4, 4.6, (3.0, 2.2, 1.7), PANEL)
    hvac_row(m, "hall_plant2", (11.0, -8.0), 13.4, 3, 4.4, (3.0, 2.2, 1.7), PANEL)
    tank(m, "hall_tank", -16.0, 8.6, 13.4, 2.2, 3.4, PANEL)
    pipes(m, "hall_pipes", 8.6, 13.5, -8.0, 12.0, 0.2, 3, 0.6, STEEL)

    # A roof extractor that turns: the factory's one moving part.
    fan(m, "hall_fan", 16.0, 6.0, 13.4, 1.9, blades=6, powered=True)

    # The office block on the +X end: glazed in bands, taller than the shed.
    walls(m, "office", 21.0, -3.0, 8.0, 14.0, 1.0, 17.0,
          ((2.6, 4.6), (6.2, 8.2), (9.8, 11.8), (13.4, 15.4)), pitch=2.2, win=1.9,
          thickness=0.6, reveal=0.08, wall=PANEL, glass=GLASS, mullion=0.4, lit_every=6)
    roof(m, "office", 21.0, -3.0, 8.0, 14.0, 17.0, overhang=0.7, parapet=1.1, mat=ROOF, fascia=TRIM)
    hvac_row(m, "office_plant", (21.0, -3.0), 18.2, 2, 5.0, (2.6, 2.0, 1.6), PANEL)

    # Two slim stacks at the back, one of them the flue the game smokes a factory
    # out of.
    chimney(m, "barrel", -17.0, -15.0, 1.0, 23.0, 1.0, PANEL, DARK, taper=0.8, bands=2)
    chimney(m, "flue_r", -11.0, -15.0, 1.0, 23.0, 1.0, PANEL, DARK, taper=0.8, bands=2)
    m.part("stack_base").box((-14.0, -15.0, 1.6), (10.0, 4.0, 3.2), CONCRETE, 0.06)

    return m.build()


def build_power_western():
    """Δυτικοί power plant: a turbine hall under a grid of six fans.

    Where the Κινέζοι put a row of stacks and the Σοβιετικοί three fat columns, the
    Δυτικοί put an air-cooled condenser: six big fan housings in a grid on the hall
    roof, which from above is a field of circles. One of them turns.
    """
    m = Model("western_power")

    plinth(m, "plinth", 0.0, 2.0, 30.0, 24.0, 0.0, 1.0, 0.8, CONCRETE, TRIM, 0.3)

    walls(m, "hall", 0.0, 2.0, 24.0, 16.0, 1.0, 11.0, ((3.0, 5.2), (7.0, 9.2)), pitch=2.6,
          win=1.9, thickness=0.7, reveal=0.12, wall=CONCRETE, glass=GLASS, mullion=0.42,
          lit_every=5)
    roof(m, "hall", 0.0, 2.0, 24.0, 16.0, 11.0, overhang=1.2, parapet=1.1, mat=ROOF, fascia=TRIM)
    skylights(m, "skylights", 0.0, 2.0, 20.0, 3.0, 12.4, 1, 5)

    for i, x in enumerate((-7.0, 0.0, 7.0)):
        for j, y in enumerate((-2.0, 6.0)):
            fan(m, f"fan_{i}{j}", x, y, 12.4, 2.3, blades=6, powered=(i == 1 and j == 1))

    # Two slim stacks and the heat-recovery block behind the hall.
    chimney(m, "stack_l", -5.0, -11.0, 1.0, 30.0, 1.05, PANEL, DARK, taper=0.82, bands=2)
    chimney(m, "flue_r", 5.0, -11.0, 1.0, 30.0, 1.05, PANEL, DARK, taper=0.82, bands=2)
    m.part("hrsg").box((10.0, -10.0, 11.0), (8.0, 7.0, 20.0), PANEL, 0.06)
    m.part("hrsg").box((10.0, -10.0, 21.2), (8.6, 7.6, 0.6), TRIM, 0.05)
    m.part("hrsg_ladder").box((6.4, -10.0, 11.0), (0.3, 0.9, 20.0), STEEL, 0.05)

    # A control building at the front, glazed, with its own plant.
    walls(m, "control", -10.0, 13.0, 10.0, 6.0, 1.0, 6.4, ((2.6, 4.8),), pitch=2.2, win=1.8,
          thickness=0.6, reveal=0.1, wall=PANEL, glass=GLASS, mullion=0.4, lit_every=4,
          faces="xy")
    roof(m, "control", -10.0, 13.0, 10.0, 6.0, 6.4, overhang=0.7, parapet=1.0, mat=ROOF, fascia=TRIM)
    hvac_row(m, "control_plant", (-10.0, 13.0), 7.4, 2, 3.4, (2.2, 1.8, 1.4), PANEL)
    pipes(m, "hall_pipes", 9.0, 12.5, -10.0, 8.0, 0.2, 3, 0.6, STEEL)

    return m.build()


def build_nuclear_western():
    """Δυτικοί nuclear plant: one large containment between two big cooling towers.

    Cleaner and larger than the Κινέζοι pair of small units, and with a single
    dome rather than two: from above, a big circle, a dome and a long turbine hall
    at the front, all of it crisply edged. A polar crane turns on the containment
    roof.
    """
    m = Model("western_nuclear")

    plinth(m, "plinth", 0.0, -1.0, 30.0, 30.0, 0.0, 1.2, 0.9, CONCRETE, TRIM, 0.35)

    # The reactor building: a square base, a cylinder and a dome.
    walls(m, "reactor", 0.0, 0.0, 18.0, 16.0, 1.2, 6.0, ((2.6, 4.6),), pitch=2.6, win=1.9,
          thickness=0.7, reveal=0.12, wall=CONCRETE, glass=GLASS, mullion=0.42, lit_every=5)
    roof(m, "reactor", 0.0, 0.0, 18.0, 16.0, 6.0, overhang=0.9, parapet=0.0, mat=ROOF, fascia=TRIM)
    m.part("containment").revolve([(7.0, 6.0), (7.0, 22.0)], 24, PANEL)
    m.part("hazard").revolve([(7.12, 6.0), (7.12, 6.9)], 24, HAZARD, variation=0.03)
    m.part("containment_band").revolve([(7.14, 20.6), (7.14, 21.4)], 24, TRIM, variation=0.04)
    dome_shell(m, "shield", 0.0, 0.0, 22.0, 7.0, 8.0, segments=24, rings=6, mat=PANEL, ring=TRIM)
    slewing_crane(m, "polar", 0.0, 0.0, 29.6, 1.4, 9.5)

    # Two large cooling towers behind, and a glazed turbine hall in front.
    cooling_tower(m, "cool_l", -11.5, -12.5, 1.2, 5.0, 3.2, 3.7, 36.0, segments=20, mat=PANEL)
    cooling_tower(m, "cool_r", 11.5, -12.5, 1.2, 5.0, 3.2, 3.7, 36.0, segments=20, mat=PANEL)

    walls(m, "turbine", 0.0, 11.0, 24.0, 10.0, 1.2, 10.0, ((4.0, 6.4),), pitch=2.4, win=2.0,
          thickness=0.7, reveal=0.1, wall=CONCRETE, glass=GLASS, mullion=0.4, lit_every=4)
    roof(m, "turbine", 0.0, 11.0, 24.0, 10.0, 10.0, overhang=1.0, parapet=1.1, mat=ROOF, fascia=TRIM)
    skylights(m, "turbine_skylights", 0.0, 11.0, 20.0, 7.0, 11.0, 2, 5)
    hvac_row(m, "turbine_plant", (-8.0, 11.0), 11.0, 3, 3.6, (2.4, 2.0, 1.5), PANEL)
    tank(m, "turbine_tank", 9.0, 11.0, 11.0, 1.6, 2.6, PANEL)

    return m.build()


def build_bureau_western():
    """Δυτικοί design bureau: a glazed tower over a wind tunnel.

    The tallest Δυτικοί building after the headquarters, with six bands of curtain
    wall, clean plant on the roof and the dish on top. The test hall beside it ends
    in a bellmouth intake, which is the shape that says "research" rather than
    "office".
    """
    m = Model("western_bureau")

    plinth(m, "plinth", 0.0, -2.0, 26.0, 22.0, 0.0, 1.2, 0.8, CONCRETE, TRIM, 0.3)

    walls(m, "base", 0.0, -3.0, 14.0, 17.0, 1.2, 6.0, ((2.8, 4.8),), pitch=2.4, win=1.9,
          thickness=0.6, reveal=0.1, wall=PANEL, glass=GLASS, mullion=0.4, lit_every=4,
          gap=("+y", -2.6, 2.6))
    doorway(m, "base", "+y", 5.5, 0.0, 5.2, 4.0, 1.2, depth=1.2, frame=STEEL, canopy=2.2)

    walls(m, "tower", 0.0, -3.0, 14.0, 13.0, 6.0, 27.0,
          ((7.0, 9.0), (10.6, 12.6), (14.2, 16.2), (17.8, 19.8), (21.4, 23.4), (24.6, 26.4)),
          pitch=2.2, win=1.9, thickness=0.6, reveal=0.08, wall=PANEL, glass=GLASS, mullion=0.38,
          lit_every=5)
    roof(m, "tower", 0.0, -3.0, 14.0, 13.0, 27.0, overhang=0.9, parapet=1.2, mat=ROOF, fascia=TRIM)
    radar_dish(m, "tower_radar", 0.0, -3.0, 28.2, 3.2, tilt=28.0, mast=2.2, yoke=0.9)
    hvac_row(m, "tower_plant", (0.0, 0.0), 28.2, 3, 4.0, (2.4, 2.0, 1.5), PANEL)
    mast(m, "tower_mast", 5.0, -7.0, 28.2, 6.0, 0.15, STEEL, arms=2)

    # The wind tunnel: a low glazed hall ending in a bellmouth.
    walls(m, "tunnel", 2.0, 11.0, 20.0, 8.0, 1.2, 7.0, ((4.4, 6.4),), pitch=2.4, win=1.9,
          thickness=0.6, reveal=0.1, wall=PANEL, glass=GLASS, mullion=0.4, lit_every=4)
    roof(m, "tunnel", 2.0, 11.0, 20.0, 8.0, 7.0, overhang=0.9, parapet=1.0, mat=ROOF, fascia=TRIM)
    m.part("intake").revolve([(3.2, 1.2), (3.2, 2.0), (2.7, 2.2)], 20, PANEL, offset=(-9.4, 11.0, 0.0))
    m.part("intake").revolve([(2.66, 1.4), (2.66, 1.9)], 20, DARK, offset=(-9.4, 11.0, 0.0))
    hvac_row(m, "tunnel_plant", (4.0, 11.0), 8.0, 3, 3.4, (2.2, 1.8, 1.4), PANEL)
    pipes(m, "tunnel_pipes", 14.0, 8.1, -6.0, 10.0, 0.18, 2, 0.6, STEEL)

    return m.build()


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------

STRUCTURES = (
    ("hq", build_hq_soviet, build_hq_chinese, build_hq_western),
    ("factory", build_factory_soviet, build_factory_chinese, build_factory_western),
    ("power", build_power_soviet, build_power_chinese, build_power_western),
    ("nuclear", build_nuclear_soviet, build_nuclear_chinese, build_nuclear_western),
    ("bureau", build_bureau_soviet, build_bureau_chinese, build_bureau_western),
)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate MiVic structure models.")
    parser.add_argument("--out", required=True, help="Directory to write .glb files into.")
    args = parser.parse_args(argv)

    os.makedirs(args.out, exist_ok=True)
    written = []

    def emit(name, builder):
        clear_scene()
        builder()
        path = os.path.join(args.out, f"{name}.glb")
        export(path)
        written.append(path)

    # Three factions, five roles, and no two of the fifteen are the same building:
    # the file names and the roles they serve are the contract with ModelCatalog.
    for (kind, soviet, chinese, western) in STRUCTURES:
        emit(f"soviet_{kind}", soviet)
        emit(f"chinese_{kind}", chinese)
        emit(f"western_{kind}", western)

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} structures")


if __name__ == "__main__":
    main()
