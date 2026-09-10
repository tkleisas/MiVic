"""Generate the MiVic bridge blocks with Blender, headlessly.

    blender --background --python tools/blender/build_bridge.py -- --out <dir>

A crossing is not modelled here. **One block is**, at the size of one navigation
cell, and the client lays that same block down once per cell of the span — so a
fifteen-cell crossing and a two-cell one are the same object repeated, and the
whole of a bridge's look is decided by the geometry in this file.

That is a design decision with a consequence worth stating: the block has to tile.
Its length is the cell exactly, its kerbs run the full length so that two blocks in
a row read as one continuous rail, and nothing sticks out past the cell's footprint
— a block with a wider lip than its neighbour's would draw a sawtooth down the side
of every crossing. The piers sit in the middle of the block rather than at its
ends, so a chain of them is a trestle every nine metres instead of a forest of legs
at every joint.

**The deck is above the water, and the model is grounded at the water line.** The
loader lifts a model so its lowest point sits at y = 0, and the client then stands
the block on the water line. So nothing here is modelled below z = 0: the pier
stubs stop at it, and what they continue into is the lake, which is opaque and
drawn first. A pier modelled down to the bed would be several metres of geometry
per block that is behind the water surface from every camera in the game.

**The kerbs are painted in the owner's colour.** This is the one part of the model
that uses the kit's paint mask — alpha 1 lets the faction colour replace the
material — and it is what tells a player at a glance whose crossing they are
looking at, without a flag or a label. The deck itself is bare timber and keeps its
own colour: a bridge painted end to end in team colour would read as a solid bar
from the air, and the thing that makes a deck read as a deck is the pale top and
the dark kerb along each side of it.

Two pieces come out of this file. `bridge_block` is the crossing itself.
`bridge_junction` is the block for the cell where two spans meet: the same deck
with no kerbs across it and a bollard at each corner, because a deck that can be
driven through in two directions cannot have a rail down the side of either.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from build_vehicles import _box_geo, _move_geo, clear_scene, export, merge, paint  # noqa: E402


# --------------------------------------------------------------------------
# The cell
#
# A navigation cell is the height map's sample spacing doubled: the map covers 600 m
# over 128 intervals at 4 687 mm, and the grid this game paths on is every second
# sample. 9.374 m is therefore not a number chosen for looks — it is the distance
# between two cells, and the block has to be exactly it or a crossing of many blocks
# accumulates a gap at every joint.
# --------------------------------------------------------------------------

CELL = 9.374

# How much of the cell the deck covers, across the span. Not the whole of it: a few
# centimetres of water down each side is what shows that the deck is a thing lying
# over the water rather than a painted stripe of ground, and at RTS zoom it is the
# only cue the sides give at all.
DECK_WIDTH = 8.60

# Height above the water line, and the deck's own thickness. The clearance is what
# makes a span read as a span; below about half a metre a bridge looks like a raft,
# and above a metre and a half it looks like a viaduct.
CLEARANCE = 0.56
DECK_THICKNESS = 0.34

# The kerb: the low rail along each side, and its height above the deck.
KERB_WIDTH = 0.34
KERB_HEIGHT = 1.05
CHORD_DEPTH = 0.20
POST = 0.24

# The pier: two legs under the middle of the block, and the tie between them.
LEG = 0.48
LEG_SPACING = 3.55
TIE_HEIGHT = 0.26
TIE_THICKNESS = 0.20

# The stringers: the beams under the deck, running the length of the block. They are
# what the deck is laid on, and from a camera looking down at forty degrees they are
# the only thing on the underside that is ever visible.
STRINGER_WIDTH = 0.52
STRINGER_DEPTH = 0.22
STRINGER_SPACING = 2.70

# How many planks the deck is laid in, across its width, and the gap between them.
# Plank lines are the one piece of detail on a bridge that survives being looked at
# closely — the deck is the largest surface in the model and a single slab of it is
# the flattest thing in the game.
PLANKS = 5
PLANK_LIFT = 0.015


# --------------------------------------------------------------------------
# Materials
#
# The bridge owns its palette rather than borrowing the vehicle kit's, and the reason
# is measurable: the kit's armour materials are deliberately dark — a tank's `hull` is
# 0.34, 0.37, 0.29 — and the renderer shades the faction colour by the material's own
# luminance, so a rail painted in `hull` comes out at about half the team colour. The
# first pass did exactly that and the kerbs read as black leather, which is not what
# "painted in the owner's colour" is supposed to look like from two hundred metres.
# A pale painted board takes the team colour nearly whole.
#
#   plank   pale weathered timber, no paint mask — the deck the player drives on
#   stringer dark creosoted timber — the beams underneath it
#   rail    pale painted board, 78 % mask — the kerbs, in the owner's colour
#   steel   grey metal, 4 % mask — the piers and their tie
# --------------------------------------------------------------------------

PLANK_MATERIAL = (0.58, 0.46, 0.30, 0.00)
STRINGER_MATERIAL = (0.26, 0.20, 0.15, 0.00)
RAIL_MATERIAL = (0.74, 0.72, 0.64, 0.78)
STEEL_MATERIAL = (0.45, 0.46, 0.48, 0.04)


def _planked_deck():
    """The deck, laid in boards across its width.

    The boards abut and each sits a centimetre or so proud of its neighbour, which is
    what a deck actually looks like and what tells the eye it is laid rather than cast.
    The first pass left a gap between boards instead and a low sun turned every gap into
    a bright stripe the length of the bridge: against a light direction this flat, the
    inside face of a gap is the brightest thing on the model, and five of them read as
    lane markings. A step cannot do that — one face of it is lit and the other is shaded,
    which is a seam rather than a highlight.
    """
    span = DECK_WIDTH / PLANKS
    geos = []

    for plank in range(PLANKS):
        centre = (-DECK_WIDTH * 0.5) + (span * (plank + 0.5))
        lift = PLANK_LIFT if plank % 2 else -PLANK_LIFT

        geos.append(_box_geo(
            (CELL, span, DECK_THICKNESS),
            (0.0, centre, CLEARANCE + (DECK_THICKNESS * 0.5) + lift),
        ))

    return geos


def _stringers():
    """The two beams the deck is laid on, running the length of the block.

    Their tops sit just below the *lowest* board rather than on the deck's nominal
    underside, because the boards step by a centimetre either way: a beam that only met the
    average would stand a centimetre and a half inside every board that was laid low, and
    two solids occupying the same space is a fault whether or not it is visible.
    """
    top = CLEARANCE - PLANK_LIFT

    return [
        _box_geo(
            (CELL, STRINGER_WIDTH, STRINGER_DEPTH),
            (0.0, side * STRINGER_SPACING, top - (STRINGER_DEPTH * 0.5)),
        )
        for side in (-1.0, 1.0)
    ]


def _rake_geo(geo, angle, pivot=(0.0, 0.0, 0.0)):
    """Rotates a shape about the Y axis, so a member can lean along the span.

    Written here rather than imported: the kit rotates about X and Z, which is every
    direction a vehicle's plates lean in, and a truss member is the one shape in this
    project that leans across those two — the deck runs along X and the rail stands up in
    Z, so the web between them rakes about the axis the kit has no helper for.
    """
    px, py, pz = pivot
    cos_a = math.cos(angle)
    sin_a = math.sin(angle)
    verts = []

    for x, y, z in geo[0]:
        dx = x - px
        dz = z - pz

        verts.append((
            (dx * cos_a) - (dz * sin_a) + px,
            y,
            (dx * sin_a) + (dz * cos_a) + pz,
        ))

    return verts, geo[1]


def _rail(side=1.0):
    """One side of the truss: a top chord, posts under it, and a raked member between each pair.

    Full length, so a chain of blocks is one unbroken rail — the posts land at the block's
    own quarter points and the chords butt end to end, which is what makes a crossing read
    as a single structure rather than as a row of objects.
    """
    rail_y = side * ((DECK_WIDTH - KERB_WIDTH) * 0.5)
    deck_top = CLEARANCE + DECK_THICKNESS
    chord_z = deck_top + KERB_HEIGHT - (CHORD_DEPTH * 0.5)
    geos = [
        # The top chord: the rail the player sees against the water from above.
        _box_geo((CELL, KERB_WIDTH, CHORD_DEPTH), (0.0, rail_y, chord_z)),
        # A bottom chord just above the deck, so the web has something to stand between.
        _box_geo((CELL, KERB_WIDTH * 0.8, CHORD_DEPTH * 0.8), (0.0, rail_y, deck_top + (CHORD_DEPTH * 0.4))),
    ]

    panels = 3
    spacing = CELL / panels
    web = KERB_HEIGHT - (CHORD_DEPTH * 1.2)

    for panel in range(panels):
        x = (-CELL * 0.5) + (spacing * (panel + 0.5))
        geos.append(_box_geo((POST, KERB_WIDTH * 0.7, web), (x, rail_y, deck_top + (web * 0.5))))

        # The raked member: from the foot of this post to the head of the next, which is the
        # zig-zag a truss is read by. Its length is the panel's diagonal and its angle is the
        # panel's slope, so the member lands on the posts rather than near them.
        if panel < panels - 1:
            rise = web
            run = spacing
            length = math.hypot(run, rise)
            angle = math.atan2(rise, run)

            raked = _box_geo((length, POST * 0.8, POST * 0.8))
            geos.append(_rake_geo(
                raked,
                angle,
                pivot=(0.0, 0.0, 0.0),
            ))
            geos[-1] = _move_geo(
                geos[-1],
                (x + (run * 0.5), rail_y, deck_top + (web * 0.5)),
            )

    return geos


def _rails():
    """Both rails of a block, in the frame the block itself is built in."""
    return _rail(-1.0) + _rail(1.0)


def _pier():
    """Two legs under the middle of the block, standing on the water line, with their tie.

    In the middle rather than at the ends: a leg at each joint would put two piers
    side by side at every block boundary, and a crossing would look like a fence
    standing in the lake. One pier per block is a trestle.
    """
    geos = []

    for side in (-1.0, 1.0):
        geos.append(_box_geo((LEG, LEG, CLEARANCE), (0.0, side * LEG_SPACING, CLEARANCE * 0.5)))

    geos.append(_box_geo(
        (TIE_THICKNESS, LEG_SPACING * 2.0, TIE_HEIGHT),
        (0.0, 0.0, TIE_HEIGHT * 0.5),
    ))

    return geos


def _bollards():
    """Four corner posts: the junction's whole vocabulary.

    A crossroads deck is open on all four sides, so it has no rail to run and nothing
    to say where the edges are — a post at each diagonal corner does that without
    closing the way through.
    """
    geos = []

    for side_x in (-1.0, 1.0):
        for side_y in (-1.0, 1.0):
            geos.append(_box_geo(
                (0.42, 0.42, KERB_HEIGHT),
                (side_x * 3.60, side_y * ((DECK_WIDTH - KERB_WIDTH) * 0.5), CLEARANCE + DECK_THICKNESS + (KERB_HEIGHT * 0.5)),
            ))

    return geos


def _build(name, parts):
    """Assembles one block from (part name, geometry, material) triples."""
    root = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(root)

    for part_name, geos, material in parts:
        obj = merge(f"{name}_{part_name}", geos)
        paint(obj, material, variation=0.10)
        obj.parent = root

    return root


def build_block():
    """The crossing itself: a board deck on stringers, with a pier under its edges.

    No rails, and that is the structural pass rather than an omission. Rails used to be part
    of the block, so every block carried two and a deck could only ever have them along its
    own axis: where two crossings met, the shared block had an unbroken rail across the way
    through, and where a span ended against open water it had none at all. The rail is now
    its own piece, placed by the client on whichever sides of a block lead nowhere, so what
    a player sees is a consequence of the map — rails along the water, none where the deck
    meets land or another span, and one closing the dead side of a T.

    The other changes were all made by looking at it: the rail was a painted slab and read
    as a stripe, the boards were gapped and a low sun turned the gaps into five bright lines
    the length of the bridge, and the pier was amidships where no camera can see it.
    """
    return _build("bridge_block", [
        ("planks", _planked_deck(), PLANK_MATERIAL),
        ("stringers", _stringers(), STRINGER_MATERIAL),
        ("pier", _pier(), STEEL_MATERIAL),
    ])


def build_rail():
    """One panel of the truss rail, on the deck's edge, in the block's own frame.

    Built on the +Y edge of the frame the block is built in, so the client reaches any of a
    block's four sides by turning it a quarter turn at a time: one mesh, four sides, and no
    rail variant for a T-junction, a crossroads, or the end of a span.
    """
    return _build("bridge_rail", [
        ("rail", _rail(1.0), RAIL_MATERIAL),
    ])


SHAPES = (
    ("bridge_block", build_block),
    ("bridge_rail", build_rail),
)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate the MiVic bridge block models.")
    parser.add_argument("--out", required=True, help="Directory to write .glb files into.")
    args = parser.parse_args(argv)

    os.makedirs(args.out, exist_ok=True)
    written = []

    for name, builder in SHAPES:
        clear_scene()
        _ = builder()

        path = os.path.join(args.out, f"{name}.glb")
        export(path)

        triangles = 0
        tallest = 0.0

        for obj in bpy.data.objects:
            if obj.type != "MESH":
                continue

            triangles += sum(len(face.vertices) - 2 for face in obj.data.polygons)
            tallest = max(tallest, max(vertex.co.z for vertex in obj.data.vertices))

        written.append((path, triangles, tallest))

    for path, triangles, tallest in written:
        size = os.path.getsize(path)
        print(
            f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB, "
            f"{triangles} triangles, {CELL:.3f} x {tallest:.2f} m)"
        )

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
