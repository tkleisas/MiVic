"""Generate the MiVic tree models with Blender, headlessly.

    blender --background --python tools/blender/build_props.py -- --out <dir>

Trees, and only trees, for now: the map grows woodland — `TerrainType.Forest`,
which slows armour and hides infantry — and until this generator existed that
woodland was a patch of darker grass with nothing standing on it.

A wood is read at RTS zoom as a mass, not as a plant. The player is a hundred
metres away looking down at forty degrees, so all a tree has to deliver is a
trunk and a canopy heavy enough to sit on top of it, in a colour that says
"woodland" from across the map. Nothing finer than that survives the distance,
which is why there are no leaves here — there are facets, and the facets are the
style.

**Six shapes rather than one.** A wood is drawn instanced: every tree of a
species is one mesh, repeated with a different position, yaw and scale, so two
trees of the same species are the *same geometry*. One shape in a wood is one
prop repeated, and the eye finds that immediately. The six differ in the things
that survive the distance — tier count, height, canopy width, and how much bare
trunk there is underneath — and the client picks between them from the hash of
the cell each tree stands in.

**Every one is under a hundred triangles**, against a budget of about 120. A tree
is drawn several hundred times over a few square kilometres of ground, and the
whole point of the instanced renderer is that the cost is per shape rather than
per tree; a canopy that was round rather than faceted would cost three times as
much to say exactly the same thing at the distance it is seen from.

The part contract is deliberately empty: a tree is one node holding one mesh. It
has no turret to traverse and no wheels for the renderer to spin, and the only
thing on it that moves — the wind — is done in the vertex shader, where it costs
nothing at all.

The kit's mesh helpers, its material table, its deterministic noise and its
exporter are imported rather than copied, exactly as the figures and the
buildings do it.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from build_vehicles import MATERIALS, _noise, clear_scene, export, merge  # noqa: E402
from build_vehicles import paint  # noqa: E402


# --------------------------------------------------------------------------
# Materials
#
# Every tree colour is (red, green, blue, paint mask) and every mask is 0. The
# mask is the fraction of the owning faction's colour that replaces the material,
# and a tree is owned by nobody: at anything above zero a wood would be painted
# in whichever team happened to be drawing it, which is a bug nobody would find
# by looking at a screenshot of a single-player match. `paint_tree` below refuses
# to paint a tree with anything else.
#
# The greens are dark on purpose. A canopy is seen against lit grass, and a
# canopy as bright as the ground under it reads as a pale smudge rather than as
# something casting shade.
# --------------------------------------------------------------------------

BARK = MATERIALS["bark"]
BARK_PALE = MATERIALS["bark_pale"]
NEEDLE_DARK = MATERIALS["needle_dark"]
NEEDLE = MATERIALS["needle"]
NEEDLE_LIT = MATERIALS["needle_lit"]
LEAF_DARK = MATERIALS["leaf_dark"]
LEAF = MATERIALS["leaf"]
LEAF_LIT = MATERIALS["leaf_lit"]


def paint_tree(obj, rgba, variation=0.14):
    """Paints a tree, refusing any material that carries a paint mask."""
    if rgba[3] != 0.0:
        raise ValueError(f"{obj.name}: a tree material must have mask 0, got {rgba[3]}")

    paint(obj, rgba, variation)


# --------------------------------------------------------------------------
# Geometry
#
# Built from vertex lists like the rest of the kit: bpy.ops depends on context,
# and the headless context is the least predictable thing in Blender. Winding
# matches the kit's, because the normals come from it — a canopy wound the wrong
# way round is lit from inside and reads as a black hole in the tree.
# --------------------------------------------------------------------------


def _ring(count, radius, height, offset, wobble=0.0, salt=0, yaw=0.0):
    """One ring of `count` vertices at `height`, faceted and optionally irregular.

    `wobble` jitters each vertex's radius by up to that fraction. A ring at an
    exactly constant radius is a machined cone, and a wood of machined cones
    looks manufactured however good the proportions are; the jitter is what makes
    a canopy read as foliage that grew rather than as a shape that was turned.
    """
    ox, oy, oz = offset
    verts = []

    for i in range(count):
        angle = ((2.0 * math.pi * i) / count) + yaw
        radius_here = radius * (1.0 + (wobble * _noise((i * 7) + (salt * 131), salt)))

        verts.append((
            ox + (math.cos(angle) * radius_here),
            oy + (math.sin(angle) * radius_here),
            oz + height,
        ))

    return verts


def _cone_geo(radius, top_radius, height, segments=6, base_cap=True, top_cap=False,
              offset=(0.0, 0.0, 0.0), wobble=0.0, salt=0, yaw=0.0):
    """A faceted cone, standing on its base at `offset`.

    `top_radius` of zero gives a true cone with an apex, which is what a
    conifer's crown is; anything else gives a frustum, which is what the tiers
    under it are. One helper for both, because the difference between a cone and
    a frustum is a number and not a shape.

    The base cap is optional and the top cap is off by default: tier three of a
    fir has its base inside tier two and its top inside tier four, and neither of
    those caps is ever on screen from a camera looking down.
    """
    bottom = _ring(segments, radius, 0.0, offset, wobble, salt, yaw)
    verts = list(bottom)
    faces = []

    if top_radius > 1e-6:
        top = _ring(segments, top_radius, height, offset, wobble, salt + 3, yaw * 1.7)
        base = len(verts)
        verts.extend(top)
        top_indices = [base + i for i in range(segments)]
    else:
        apex = len(verts)
        ox, oy, oz = offset
        verts.append((ox, oy, oz + height))
        top_indices = [apex] * segments

    for i in range(segments):
        j = (i + 1) % segments

        if top_indices[i] == top_indices[j]:
            faces.append((i, j, top_indices[i]))
        else:
            faces.append((i, j, top_indices[j], top_indices[i]))

    if base_cap:
        faces.append(tuple(range(segments - 1, -1, -1)))

    if top_cap:
        faces.append(tuple(top_indices))

    return verts, faces


def _trunk_geo(radius, top_radius, height, segments=6, offset=(0.0, 0.0, 0.0),
               lean=(0.0, 0.0), wobble=0.02, salt=0):
    """An open tapered tube: the trunk, with no caps at either end.

    The bottom is under the ground and the top is inside the canopy, so caps here
    would be triangles nobody can see. `lean` walks the top ring sideways, which
    is the whole difference between a tree and a post.
    """
    ox, oy, oz = offset
    dx, dy = lean

    bottom = _ring(segments, radius, 0.0, offset, wobble, salt)
    top = _ring(segments, top_radius, height, (ox + dx, oy + dy, oz), wobble, salt + 5)

    verts = list(bottom)
    base = len(verts)
    verts.extend(top)
    faces = []

    for i in range(segments):
        j = (i + 1) % segments
        faces.append((i, j, base + j, base + i))

    return verts, faces


def _blob_geo(radius, height, segments=6, offset=(0.0, 0.0, 0.0), waist=0.74,
              wobble=0.14, salt=0, yaw=0.0, round_crown=True):
    """One lump of a broadleaf crown, `offset` to the bottom of the lump.

    Deliberately not the kit's `dome`. That one is a turret roof: it starts at
    its widest ring and stops there, so its underside is open — invisible on a
    tank, where a hull is directly underneath, and a hole in the tree on a
    canopy, which is the one part of a tree a camera that has come in close
    looks up at. This one closes underneath with a narrower ring and a cap.

    `round_crown` adds the second band above the waist. The main lump of a crown
    gets it, because a single cone above the widest ring reads as a hexagonal
    plate seen edge-on — which is exactly how the first pass at these looked. The
    smaller lumps around it do not: they are mostly hidden by the main one, and a
    wood is several hundred of these.
    """
    ox, oy, oz = offset

    lower = _ring(segments, radius * waist, 0.0, offset, wobble, salt, yaw)
    middle = _ring(segments, radius, height * (0.42 if round_crown else 0.55),
                   offset, wobble, salt + 3, yaw * 1.3)

    verts = list(lower)
    base = len(verts)
    verts.extend(middle)
    faces = [tuple(range(segments - 1, -1, -1))]

    for i in range(segments):
        j = (i + 1) % segments
        faces.append((i, j, base + j, base + i))

    if round_crown:
        upper = _ring(segments, radius * 0.58, height * 0.78, offset, wobble, salt + 9, yaw * 0.7)
        shoulder = len(verts)
        verts.extend(upper)

        for i in range(segments):
            j = (i + 1) % segments
            faces.append((base + i, base + j, shoulder + j, shoulder + i))

        spread = shoulder
    else:
        spread = base

    apex = len(verts)
    verts.append((ox, oy, oz + height))

    for i in range(segments):
        faces.append((spread + i, spread + ((i + 1) % segments), apex))

    return verts, faces


def _stack(name, geos, material, variation=0.14):
    """Merges several shapes into one node and paints the lot."""
    obj = merge(name, geos)
    paint_tree(obj, material, variation)
    return obj


# --------------------------------------------------------------------------
# Conifers
#
# A conifer is tiers: rings of canopy that widen and then narrow, stacked so each
# one is inside the one below. That is a silhouette nothing else on the map has,
# and it is what reads as "pine" from above at a glance — a broadleaf from the
# same camera is a lumpy ball on a stick.
#
# The three differ by the numbers that survive the distance: how many tiers, how
# high the tree stands, how wide the canopy is at the bottom, and how much bare
# trunk there is underneath it.
# --------------------------------------------------------------------------


def build_conifer(name, height, spread, tiers, trunk_height, trunk_radius,
                  trunk_lean=0.0, needle=NEEDLE, spike=0.35):
    """A tiered conifer: a tapered trunk under a stack of narrowing skirts.

    The tiers are laid out from a single `height` rather than from three
    independent numbers, because the one thing that must never happen is a
    canopy that stops short of the top of the tree: `spike` is the fraction of
    the height given to the bare crown cone above the last skirt.
    """
    geos = []

    trunk_top = _trunk_geo(
        trunk_radius,
        trunk_radius * 0.55,
        trunk_height,
        segments=6,
        lean=(trunk_lean, trunk_lean * 0.6),
        salt=int(height * 13) % 97,
    )
    geos.append(trunk_top)

    # The skirts run from the bottom of the canopy to the top of the last one;
    # the crown cone owns everything above that.
    crown = height * spike
    canopy_top = height - crown
    first = trunk_height * 0.62
    span = canopy_top - first

    for tier in range(tiers):
        # Each skirt stands taller than its share of the span, so it overlaps the
        # one below and no trunk shows through between them.
        step = span / tiers
        base = first + (tier * step)
        tier_height = step * (1.0 + (1.0 / tiers))
        bottom_radius = spread * (1.0 - (0.62 * (tier / max(1, tiers))))
        top_radius = bottom_radius * (0.34 if tier < tiers - 1 else 0.30)

        geos.append(_cone_geo(
            bottom_radius,
            top_radius,
            tier_height,
            segments=6,
            # Only the lowest skirt shows its underside, and only to a camera
            # that has come in under the tree.
            base_cap=tier == 0,
            offset=(0.0, 0.0, base),
            wobble=0.10,
            salt=tier + int(height),
            # Each tier is turned a little against the one below it, so the
            # facets never line up into a single seam down the side of the tree.
            yaw=(tier * 0.42),
        ))

    # The crown: a narrow cone to a point, which is the top of the tree.
    geos.append(_cone_geo(
        spread * 0.38 * (1.0 - (0.62 * ((tiers - 1) / max(1, tiers)))),
        0.0,
        crown + (span / tiers * 0.6),
        segments=6,
        base_cap=False,
        offset=(0.0, 0.0, canopy_top - (span / tiers * 0.6)),
        wobble=0.12,
        salt=tiers + 31,
        yaw=0.9,
    ))

    return _stack(name, geos, needle, variation=0.15)


# --------------------------------------------------------------------------
# Broadleaves
#
# A broadleaf is a mass: two or three faceted blobs overlapping into one lumpy
# crown, high enough on the trunk to walk a tank under. The canopy is built from
# blobs rather than from one dome because a single dome is a hemisphere, and a
# wood of hemispheres reads as a field of green mushrooms.
# --------------------------------------------------------------------------


def build_broadleaf(name, trunk_height, spread, blobs, trunk_radius,
                    leaf=LEAF, trunk=BARK_PALE):
    """A broadleaf: a bare trunk carrying a crown of overlapping lumps.

    The tree's height is *derived*, not asked for. The crown's extent comes from
    the size of its lumps and the trunk from where the crown has to start, so the
    two can never disagree — which is what the first pass got wrong. It took a
    total height and a crown depth and laid the lumps out by their undersides: the
    crown ended up hanging a metre above the top of its own trunk and growing
    through the top of the height it had been asked for, and a tree that is two
    pieces at three hundred metres is a tree that reads as two pieces.

    `trunk_height` and the crown's mass are what separates the three broadleaves:
    a tree whose crown starts at a third of its height is a different object from
    one bare most of the way up, at the same width.
    """
    ox, oy = 0.0, 0.0

    # The main lump, half again as tall as it is wide, and the extra lumps sized
    # against it. The crown's depth is then a multiple of that rather than an
    # independent number: two lumps stacked make a shallower crown than three,
    # and the tree is as tall as the two of them add up to.
    lump = spread * 0.80 * 1.35
    depth = lump * (1.64 if blobs >= 3 else 1.25)
    height = trunk_height + depth

    geos = []

    for index in range(blobs):
        if index == 0:
            # The heavy central lump, its top at the top of the tree. It is the
            # one lump whose crown is rounded, because it is the only one with
            # sky above it.
            dx, dy, centre, size = ox, oy, height - (lump * 0.5), 0.80
            round_crown = True
        else:
            angle = ((index - 1) * (2.0 * math.pi / max(1, blobs - 1))) + 0.7

            dx = math.cos(angle) * spread * 0.30
            dy = math.sin(angle) * spread * 0.30
            # Inside the main lump's own vertical span, so the crown is one mass
            # with shoulders rather than a stack of separate plates.
            centre = trunk_height + (depth * (0.28 if index % 2 else 0.80))
            size = 0.62 if index % 2 else 0.52
            round_crown = False

        radius = spread * size
        lump_height = radius * (1.35 if round_crown else 1.20)

        geos.append(_blob_geo(
            radius,
            lump_height,
            segments=6,
            offset=(dx, dy, centre - (lump_height * 0.5)),
            wobble=0.15,
            salt=index + 3,
            yaw=index * 0.55,
            round_crown=round_crown,
        ))

    # The trunk last, and measured up into the middle of the main lump rather
    # than to the underside of the crown: a trunk that stops where the foliage
    # starts is a trunk with a visible seam around it.
    geos.insert(0, _trunk_geo(
        trunk_radius,
        trunk_radius * 0.62,
        height - (lump * 0.5),
        segments=6,
        lean=(trunk_radius * 1.2, -trunk_radius * 0.6),
        salt=int(height * 7) % 89,
    ))

    return _stack(name, geos, leaf, variation=0.16)


# --------------------------------------------------------------------------
# The wood
#
# Six trees: three conifers and three broadleaves. The client draws each one
# instanced, so this table is also the whole draw cost of a forest — six meshes
# for several hundred trees.
# --------------------------------------------------------------------------

def build_conifer_tall():
    """The tallest thing in the wood: 4 skirts, 10 m, a mid-width canopy.

    Conical and evenly tiered, which is the shape the eye reads as "fir" first.
    """
    return build_conifer(
        "tree_conifer_tall",
        height=10.0,
        spread=2.25,
        tiers=4,
        trunk_height=1.9,
        trunk_radius=0.30,
        needle=NEEDLE_DARK,
        spike=0.30,
    )


def build_conifer_broad():
    """Short and wide: 3 fat skirts on a fat trunk, 8 m by nearly 6 m across.

    A spruce that grew in the open rather than in a crowd. The width is the whole
    point of it — next to the tall one it is the difference between a spire and a
    bush with a trunk.
    """
    return build_conifer(
        "tree_conifer_broad",
        height=8.0,
        spread=2.95,
        tiers=3,
        trunk_height=1.5,
        trunk_radius=0.36,
        needle=NEEDLE,
        spike=0.26,
    )


def build_conifer_slim():
    """Five narrow skirts up a long bare trunk: a 9.5 m spire, barely 3 m wide.

    The conifer for a dense wood: it is mostly vertical, so a stand of these
    reads as depth rather than as a wall, and its long trunk shows between the
    canopies of the two shorter trees.
    """
    return build_conifer(
        "tree_conifer_slim",
        height=9.5,
        spread=1.60,
        tiers=5,
        trunk_height=3.1,
        trunk_radius=0.22,
        trunk_lean=0.10,
        needle=NEEDLE_LIT,
        spike=0.22,
    )


def build_broadleaf_wide():
    """The wide low one: a 2.4 m trunk under the broadest crown of the six.

    Nearly six metres of crown on two and a half of trunk, which is the lowest
    and widest thing in the set and the tree that carries a wood's mass. A wood
    needs one tree that is a mass rather than a shape, and this is it.
    """
    return build_broadleaf(
        "tree_broadleaf_wide",
        trunk_height=2.4,
        spread=3.15,
        blobs=3,
        trunk_radius=0.38,
        leaf=LEAF_DARK,
    )


def build_broadleaf_tall():
    """The tall oak: a 4.6 m bare trunk under a deep crown of three lumps.

    A high heavy crown on a long trunk — the opposite read from the wide one, and
    the tree that gives a wood a second storey above the conifers' skirts.
    """
    return build_broadleaf(
        "tree_broadleaf_tall",
        trunk_height=4.6,
        spread=2.75,
        blobs=3,
        trunk_radius=0.32,
        leaf=LEAF,
    )


def build_broadleaf_slim():
    """A slender pale-trunked one: two lumps, the smallest crown of the six.

    Birch-coloured bark and a crown made of two lumps rather than three, so a
    wood has one tree that is neither broad nor heavy: it reads as light and thin
    against the dark canopies around it, and it is the only crown in the set with
    a shoulder on one side and not the other.
    """
    return build_broadleaf(
        "tree_broadleaf_slim",
        trunk_height=4.2,
        spread=2.30,
        blobs=2,
        trunk_radius=0.21,
        leaf=LEAF_LIT,
    )


SHAPES = (
    ("tree_conifer_tall", build_conifer_tall),
    ("tree_conifer_broad", build_conifer_broad),
    ("tree_conifer_slim", build_conifer_slim),
    ("tree_broadleaf_wide", build_broadleaf_wide),
    ("tree_broadleaf_tall", build_broadleaf_tall),
    ("tree_broadleaf_slim", build_broadleaf_slim),
)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate the MiVic tree models.")
    parser.add_argument("--out", required=True, help="Directory to write .glb files into.")
    args = parser.parse_args(argv)

    os.makedirs(args.out, exist_ok=True)
    written = []

    for name, builder in SHAPES:
        clear_scene()
        obj = builder()

        path = os.path.join(args.out, f"{name}.glb")
        export(path)

        triangles = sum(len(face.vertices) - 2 for face in obj.data.polygons)
        width = max(v.co.x for v in obj.data.vertices) - min(v.co.x for v in obj.data.vertices)
        tall = max(v.co.z for v in obj.data.vertices) - min(v.co.z for v in obj.data.vertices)

        written.append((path, triangles, width, tall))

    for path, triangles, width, tall in written:
        size = os.path.getsize(path)
        print(
            f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB, "
            f"{triangles} triangles, {width:.1f} x {tall:.1f} m)"
        )

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
