"""Generate MiVic vehicle and building models with Blender, headlessly.

    blender --background --python tools/blender/build_vehicles.py -- --out <dir>

Why code and not modelling: the art direction is *per-faction silhouettes* built
from one shared part contract. A generator encodes that contract once — every
faction produces the same node names with different proportions — where three
hand-made models would drift apart the first time one of them was edited.

The part contract, identical for every faction and every role:

    hull        the body
    turret      rotates independently of the hull
    barrel      elevates independently of the turret
    wheel_01..  road wheels, named so the renderer can spin them by distance
    radar       rotating dish on buildings

Only the geometry behind each name differs between factions.
"""

import argparse
import math
import os
import sys

import bpy


# --------------------------------------------------------------------------
# Silhouettes
#
# Proportions come from docs/ART_PIPELINE.md section 2.1. Identity is carried by
# outline, not by hue, because colour is the one thing the renderer already
# applies per faction.
#
# `turret_scale` is (half width, half length, height) in the same metres as
# everything else, so a turret can be checked against its own hull: it should be
# roughly the hull's height again above the ring, not twice it.
#
# `glacis` is the fraction of the hull length the nose plate runs back over. It is
# the whole front silhouette, and the first pass had it far too large — 0.55 on a
# 6 m hull is a 16° ramp covering more than half the deck, which is why the tank
# read from above as one enormous featureless plate. A tank's nose plate is
# steep: 0.10–0.28 puts it between 30° and 55° from the horizontal and gives the
# deck back to the engine, the hatches and the stowage.
# --------------------------------------------------------------------------

PROFILES = {
    # Compact, low, sloped all round, few big road wheels, wide tracks. Light
    # hull on a broad footprint: the mud-mobile tank.
    "soviet": {
        "hull_length": 6.0,
        "hull_width": 3.2,
        "hull_height": 0.95,
        "glacis": 0.20,
        "turret": "dome",
        "turret_scale": (1.22, 1.45, 1.05),
        "barrel_length": 3.1,
        "barrel_radius": 0.115,
        "muzzle_brake": True,
        "wheels": 5,
        "wheel_radius": 0.42,
        "track_width": 0.62,

        # Fittings. Placement, not proportion, is what separates the three
        # factions once the silhouettes are all "a tank": the Σοβιετικοί carry
        # fuel drums and a log on the right fender, and their exhaust runs down
        # the left one.
        "skirt": "full",
        "exhaust_side": -1,
        "fuel": "drums",
        "grille_slats": 4,
        "cupola": (-0.62, -0.72),
        "basket": False,
        "smoke": 0,
    },

    # Tall, narrow, slab-sided, few exposed road wheels, plain tube. Boxy and
    # utilitarian: cheap to build in enormous numbers.
    "chinese": {
        "hull_length": 5.6,
        "hull_width": 2.7,
        "hull_height": 1.15,
        "glacis": 0.10,
        "turret": "box",
        "turret_scale": (1.02, 1.24, 1.12),
        "barrel_length": 3.0,
        "barrel_radius": 0.10,
        "muzzle_brake": False,
        "wheels": 5,
        "wheel_radius": 0.36,
        "track_width": 0.40,

        # No skirts, external fuel cells on the rear deck, the exhaust and its
        # muffler on the right, and the infantry telephone on the back plate.
        "skirt": "none",
        "exhaust_side": 1,
        "fuel": "cells",
        "grille_slats": 5,
        "cupola": (0.0, -0.80),
        "basket": False,
        "smoke": 0,
    },

    # Long, wide, high, many road wheels, wedge turret, long thin barrel. Large
    # and sophisticated — and the heaviest of the three.
    "western": {
        "hull_length": 7.4,
        "hull_width": 3.7,
        "hull_height": 1.30,
        "glacis": 0.28,
        "turret": "wedge",
        "turret_scale": (1.48, 1.82, 1.05),
        "barrel_length": 4.8,
        "barrel_radius": 0.095,
        "muzzle_brake": False,
        "wheels": 7,
        "wheel_radius": 0.38,
        "track_width": 0.58,

        # Side skirts as separate panels, a stowage basket round the turret and
        # smoke dischargers on it: the fittings are as angular as the armour.
        "skirt": "panels",
        "exhaust_side": 1,
        "fuel": "none",
        "grille_slats": 5,
        "cupola": (0.66, -0.66),
        "basket": True,
        "smoke": 4,
    },
}


# --------------------------------------------------------------------------
# Materials
#
# Every entry is (red, green, blue, paint mask). The mask is the fraction of the
# faction colour that replaces the material, so it decides which parts of a model
# say "this belongs to the Σοβιετικοί" and which say "this is a rubber track".
#
# The brightness of the material matters as much as its hue: the renderer shades
# the faction colour by the material's own luminance, so a dark track stays dark
# and a light deck plate stays light whatever colour the faction is.
# --------------------------------------------------------------------------

MATERIALS = {
    # Armour. Mostly faction colour, with enough camouflage underneath to keep
    # the plates from being one perfectly flat hue.
    "hull":     (0.34, 0.37, 0.29, 0.82),
    "turret":   (0.40, 0.43, 0.34, 0.86),
    "deck":     (0.47, 0.49, 0.40, 0.78),
    "skirt":    (0.26, 0.29, 0.23, 0.72),

    # Mechanical parts keep their own colour. They are what stops a vehicle
    # reading as a single flat silhouette at RTS zoom.
    "rubber":   (0.10, 0.10, 0.11, 0.08),
    "tyre":     (0.14, 0.14, 0.15, 0.08),
    "steel":    (0.44, 0.45, 0.47, 0.06),
    "gun":      (0.25, 0.26, 0.28, 0.10),
    "gun_dark": (0.16, 0.16, 0.18, 0.08),
    "glass":    (0.09, 0.15, 0.20, 0.00),
    "lamp":     (0.95, 0.90, 0.58, 0.00),
    "rust":     (0.36, 0.22, 0.13, 0.00),
    "hazard":   (0.90, 0.72, 0.16, 0.00),
    "panel":    (0.62, 0.64, 0.66, 0.00),

    # Vehicle fittings, added by the vehicle pass. A grille has to be nearly
    # black or it does not read as a hole in the deck, and a tarp has to keep
    # some of its own colour or a rolled tarpaulin looks like a painted pipe.
    "grille":   (0.13, 0.14, 0.14, 0.05),
    "canvas":   (0.38, 0.36, 0.27, 0.22),
    "fuel":     (0.31, 0.29, 0.23, 0.40),
    "optics":   (0.10, 0.24, 0.32, 0.00),
    "ore":      (0.30, 0.26, 0.22, 0.00),
    "concrete": (0.56, 0.56, 0.54, 0.12),
    "coil":     (0.26, 0.60, 0.86, 0.00),
    "crate":    (0.46, 0.35, 0.21, 0.00),

    # People and machines.
    "uniform":  (0.30, 0.30, 0.32, 0.55),
    "flesh":    (0.72, 0.55, 0.42, 0.00),
    "robot":    (0.42, 0.45, 0.48, 0.70),
    "robot_leg": (0.28, 0.29, 0.31, 0.30),

    # Woodland, used by build_props.py. **Every mask here is zero**, and that is
    # the one thing about these entries that matters: a tree belongs to nobody,
    # and a mask above zero would paint a wood in whichever team happened to be
    # drawing it. The renderer's foliage pass is what varies a canopy from tree
    # to tree, by multiplying the material by a per-tree tint, so these are the
    # shades of one wood rather than the colours of one species.
    "bark":       (0.25, 0.19, 0.14, 0.00),
    "bark_pale":  (0.56, 0.54, 0.48, 0.00),
    "needle_dark": (0.09, 0.19, 0.12, 0.00),
    "needle":     (0.14, 0.27, 0.14, 0.00),
    "needle_lit": (0.19, 0.34, 0.17, 0.00),
    "leaf_dark":  (0.17, 0.30, 0.13, 0.00),
    "leaf":       (0.23, 0.37, 0.16, 0.00),
    "leaf_lit":   (0.31, 0.44, 0.19, 0.00),
}


# --------------------------------------------------------------------------
# Mesh helpers. Built from vertex lists rather than bpy.ops, because operators
# depend on context and the headless context is the least predictable thing in
# Blender.
# --------------------------------------------------------------------------


def _link(name, verts, faces):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def box(name, size, offset=(0.0, 0.0, 0.0), taper=1.0):
    """An axis-aligned box, optionally tapered towards +Y (a wedge)."""
    sx, sy, sz = (s * 0.5 for s in size)
    ox, oy, oz = offset
    tx = sx * taper
    tz = sz

    verts = [
        (-sx, -sy, -sz), (sx, -sy, -sz), (sx, -sy, sz), (-sx, -sy, sz),
        (-tx, sy, -tz), (tx, sy, -tz), (tx, sy, tz), (-tx, sy, tz),
    ]
    verts = [(x + ox, y + oy, z + oz) for x, y, z in verts]

    faces = [
        (0, 1, 2, 3),  # back
        (5, 4, 7, 6),  # front
        (4, 0, 3, 7),  # left
        (1, 5, 6, 2),  # right
        (3, 2, 6, 7),  # top
        (4, 5, 1, 0),  # bottom
    ]
    return _link(name, verts, faces)


def sloped_hull(name, length, width, height, glacis):
    """A hull whose front plate is sloped, which is what reads as a silhouette.

    Built as an eight-vertex solid: the front bottom edge is pulled back by
    ``glacis``, so a large value gives the long sloped nose of a Δυτικοί tank and
    a small one gives the near-vertical slab of a Κινέζοι one.
    """
    sx = width * 0.5
    sy = length * 0.5
    sz = height * 0.5
    nose = sy - (length * glacis)

    verts = [
        # back (-Y)
        (-sx, -sy, -sz), (sx, -sy, -sz), (sx, -sy, sz), (-sx, -sy, sz),
        # front (+Y), bottom pulled back to make the glacis
        (-sx, nose, -sz), (sx, nose, -sz), (sx, sy, sz), (-sx, sy, sz),
    ]

    faces = [
        (0, 1, 2, 3),
        (5, 4, 7, 6),
        (4, 0, 3, 7),
        (1, 5, 6, 2),
        (3, 2, 6, 7),
        (4, 5, 1, 0),
    ]
    return _link(name, verts, faces)


def cylinder(name, radius, length, segments=12, axis="y", offset=(0.0, 0.0, 0.0)):
    """A capped cylinder along one axis."""
    ox, oy, oz = offset
    half = length * 0.5
    verts = []
    faces = []

    for end in (-1, 1):
        for i in range(segments):
            angle = (2.0 * math.pi * i) / segments
            a = math.cos(angle) * radius
            b = math.sin(angle) * radius

            if axis == "y":
                verts.append((a + ox, end * half + oy, b + oz))
            elif axis == "x":
                verts.append((end * half + ox, a + oy, b + oz))
            else:
                verts.append((a + ox, b + oy, end * half + oz))

    for i in range(segments):
        nxt = (i + 1) % segments
        faces.append((i, nxt, nxt + segments, i + segments))

    faces.append(tuple(range(segments - 1, -1, -1)))
    faces.append(tuple(range(segments, segments * 2)))

    return _link(name, verts, faces)


def dome(name, radius, scale, segments=10, rings=5):
    """A squashed hemisphere: the cast turret of a Σοβιετικοί tank."""
    verts = []
    faces = []
    sx, sy, sz = scale

    verts.append((0.0, 0.0, radius * sz))

    for ring in range(1, rings + 1):
        phi = (math.pi * 0.5 * ring) / rings
        r = math.cos(phi) * radius
        z = math.sin(phi) * radius * sz

        for i in range(segments):
            angle = (2.0 * math.pi * i) / segments
            verts.append((math.cos(angle) * r * sx, math.sin(angle) * r * sy, z))

    for i in range(segments):
        faces.append((0, 1 + i, 1 + ((i + 1) % segments)))

    for ring in range(rings - 1):
        base = 1 + (ring * segments)
        nxt = base + segments

        for i in range(segments):
            j = (i + 1) % segments
            faces.append((base + i, nxt + i, nxt + j, base + j))

    return _link(name, verts, faces)


def wedge_turret(name, scale):
    """A sloped-plate turret: the Δυτικοί wedge.

    A base rectangle with a smaller, inset top plate: every side slopes inward and
    the front plate rakes back, which is what "wedge" means on a tank. The top is
    flat, because a turret whose roof slopes up towards the rear reads as a sail,
    not as armour.
    """
    sx, sy, sz = scale
    tx = sx * 0.60
    ty = sy * 0.88
    fy = sy * 0.30

    verts = [
        (-sx, -sy, 0.0), (sx, -sy, 0.0), (sx, sy, 0.0), (-sx, sy, 0.0),
        (-tx, -ty, sz), (tx, -ty, sz), (tx, fy, sz), (-tx, fy, sz),
    ]
    faces = [
        (0, 1, 2, 3),
        (7, 6, 5, 4),
        (0, 4, 5, 1),
        (1, 5, 6, 2),
        (2, 6, 7, 3),
        (3, 7, 4, 0),
    ]
    return _link(name, verts, faces)


def paint(obj, rgba, variation=0.07):
    """Bakes a flat material colour into the mesh's vertex colours.

    The **alpha is the faction paint mask** the renderer reads: 1 lets the
    faction colour cover the surface, 0 leaves the material exactly as authored.
    That is the whole reason a tank can be red and still have black rubber tracks
    and a gunmetal barrel.

    A little per-face brightness noise goes on top. Two adjacent plates painted
    the same colour are indistinguishable from one another, and a model made of
    identical flat panels reads as a single blob however good its silhouette is.
    """
    mesh = obj.data
    attribute = mesh.color_attributes.get("Col")

    if attribute is None:
        attribute = mesh.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")

    r, g, b, mask = rgba

    for poly in mesh.polygons:
        shade = 1.0 + (variation * _noise(poly.index))

        entry = (
            min(1.0, max(0.0, r * shade)),
            min(1.0, max(0.0, g * shade)),
            min(1.0, max(0.0, b * shade)),
            mask,
        )

        for loop in poly.loop_indices:
            attribute.data[loop].color = entry


def _noise(index, salt=0):
    """A deterministic -1..1 value for a face index.

    Deterministic matters: the models are committed, and a generator that
    produced different vertex colours on every run would leave a diff behind
    each time it was run.
    """
    x = ((index + 1) * 1103515245 + 12345 + (salt * 2654435761)) & 0x7FFFFFFF
    x = (x ^ (x >> 13)) * 1274126177
    return (((x >> 8) % 2001) / 1000.0) - 1.0


def join(parent, children):
    """Parents each child to a part so the exporter writes a node hierarchy.

    Parts that are animated as a unit are parented deliberately at the call site
    (the barrel to the turret, the muzzle to the barrel); this is for the ones that
    simply hang off the model root.
    """
    for child in children:
        if child.parent is None:
            child.parent = parent


# --------------------------------------------------------------------------
# Composing one part out of several shapes
#
# The helpers above each return one object with one shape. A turret cannot be
# built that way: the renderer animates the part called `turret` as a unit, so a
# cast turret body, its roof, its bustle and its ring have to be a *single*
# object — while still being a dozen primitives. These build vertex lists rather
# than objects, so several of them can be merged into one animatable part.
#
# They deliberately duplicate the arithmetic of `box`, `cylinder` and `dome`
# rather than refactoring them: three generators import those, and a shared
# helper that quietly changed shape would move every building in the game.
# --------------------------------------------------------------------------


def _side(side):
    """The `l` / `r` suffix for a mirrored part name."""
    return "l" if side < 0 else "r"


def _root(name):
    root = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(root)
    return root


def _box_geo(size, offset=(0.0, 0.0, 0.0), taper=1.0):
    """Vertex and face lists for an axis-aligned box, tapered towards +Y."""
    sx, sy, sz = (s * 0.5 for s in size)
    ox, oy, oz = offset
    tx = sx * taper

    verts = [
        (-sx + ox, -sy + oy, -sz + oz), (sx + ox, -sy + oy, -sz + oz),
        (sx + ox, -sy + oy, sz + oz), (-sx + ox, -sy + oy, sz + oz),
        (-tx + ox, sy + oy, -sz + oz), (tx + ox, sy + oy, -sz + oz),
        (tx + ox, sy + oy, sz + oz), (-tx + ox, sy + oy, sz + oz),
    ]
    faces = [(0, 1, 2, 3), (5, 4, 7, 6), (4, 0, 3, 7), (1, 5, 6, 2), (3, 2, 6, 7), (4, 5, 1, 0)]
    return verts, faces


def _cyl_geo(radius, length, segments=12, axis="y", offset=(0.0, 0.0, 0.0)):
    """Vertex and face lists for a capped cylinder, matching `cylinder`."""
    ox, oy, oz = offset
    half = length * 0.5
    verts = []
    faces = []

    for end in (-1, 1):
        for i in range(segments):
            angle = (2.0 * math.pi * i) / segments
            a = math.cos(angle) * radius
            b = math.sin(angle) * radius

            if axis == "y":
                verts.append((a + ox, end * half + oy, b + oz))
            elif axis == "x":
                verts.append((end * half + ox, a + oy, b + oz))
            else:
                verts.append((a + ox, b + oy, end * half + oz))

    for i in range(segments):
        nxt = (i + 1) % segments
        faces.append((i, nxt, nxt + segments, i + segments))

    faces.append(tuple(range(segments - 1, -1, -1)))
    faces.append(tuple(range(segments, segments * 2)))
    return verts, faces


def _dome_geo(radius, scale, segments=10, rings=5, offset=(0.0, 0.0, 0.0)):
    """Vertex and face lists for a squashed hemisphere, matching `dome`."""
    sx, sy, sz = scale
    ox, oy, oz = offset
    verts = [(ox, oy, (radius * sz) + oz)]
    faces = []

    for ring in range(1, rings + 1):
        phi = (math.pi * 0.5 * ring) / rings
        r = math.cos(phi) * radius
        z = math.sin(phi) * radius * sz

        for i in range(segments):
            angle = (2.0 * math.pi * i) / segments
            verts.append((math.cos(angle) * r * sx + ox, math.sin(angle) * r * sy + oy, z + oz))

    for i in range(segments):
        faces.append((0, 1 + i, 1 + ((i + 1) % segments)))

    for ring in range(rings - 1):
        base = 1 + (ring * segments)
        nxt = base + segments

        for i in range(segments):
            j = (i + 1) % segments
            faces.append((base + i, nxt + i, nxt + j, base + j))

    return verts, faces


def _frustum_geo(plan, top, height, offset=(0.0, 0.0, 0.0), top_shift=(0.0, 0.0)):
    """A box standing on its base with a smaller, optionally shifted top plate.

    `plan` is the base (width, length) and `top` is the top plate as a fraction of
    it, so (1, 1) is a plain box and (0.6, 0.5) is a turret with sloped sides all
    round. Shifting the top plate rearwards rakes the front plate and leaves the
    rear overhanging, which is what makes a Δυτικοί wedge read as a wedge instead
    of as a box with the corners knocked off.
    """
    sx, sy = plan[0] * 0.5, plan[1] * 0.5
    tx, ty = sx * top[0], sy * top[1]
    ox, oy, oz = offset
    mx, my = top_shift

    verts = [
        (-sx + ox, -sy + oy, oz), (sx + ox, -sy + oy, oz),
        (sx + ox, sy + oy, oz), (-sx + ox, sy + oy, oz),
        (-tx + ox + mx, -ty + oy + my, oz + height), (tx + ox + mx, -ty + oy + my, oz + height),
        (tx + ox + mx, ty + oy + my, oz + height), (-tx + ox + mx, ty + oy + my, oz + height),
    ]
    faces = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    return verts, faces


def _spin_geo(geo, pitch=0.0, yaw=0.0, pivot=(0.0, 0.0, 0.0)):
    """Rotates a geometry pair about X and then Z, through `pivot`.

    Tilted plates are everywhere on a vehicle — a glacis applique, a raked
    casemate front, an elevated rocket rack — and a plate is always a box that has
    been rotated about the point it is hinged on.
    """
    px, py, pz = pivot
    pitch_rad = math.radians(pitch)
    yaw_rad = math.radians(yaw)
    cp, sp = math.cos(pitch_rad), math.sin(pitch_rad)
    cy, sy = math.cos(yaw_rad), math.sin(yaw_rad)

    verts = []

    for x, y, z in geo[0]:
        x -= px
        y -= py
        z -= pz

        y, z = (y * cp) - (z * sp), (y * sp) + (z * cp)
        x, y = (x * cy) - (y * sy), (x * sy) + (y * cy)
        verts.append((x + px, y + py, z + pz))

    return verts, geo[1]


def _scale_geo(geo, scale, pivot=(0.0, 0.0, 0.0)):
    """Stretches a geometry pair along each axis, about `pivot`.

    A cylinder is round; a turret ring under an elliptical cast turret is not.
    """
    px, py, pz = pivot
    sx, sy, sz = scale
    verts = [(((x - px) * sx) + px, ((y - py) * sy) + py, ((z - pz) * sz) + pz) for x, y, z in geo[0]]
    return verts, geo[1]


def _move_geo(geo, offset):
    """Translates a geometry pair. Used to put a scaled shape where it belongs."""
    ox, oy, oz = offset
    return [(x + ox, y + oy, z + oz) for x, y, z in geo[0]], geo[1]


def merge(name, geos):
    """One mesh from several primitives, so a part animates as a single piece."""
    verts = []
    faces = []

    for geo in geos:
        base = len(verts)
        verts.extend(geo[0])
        faces.extend(tuple(index + base for index in face) for face in geo[1])

    return _link(name, verts, faces)


def bars(name, count, slat, step, offset=(0.0, 0.0, 0.0), axis="y", taper=1.0):
    """A comb of thin slats as one mesh.

    Engine grilles, skirt panels, basket frames and headlight guards are all the
    same shape — a row of thin plates — and building each as one part instead of
    six keeps the part list readable.
    """
    step_axis = {"x": 0, "y": 1, "z": 2}[axis]
    verts = []
    faces = []

    for i in range(count):
        place = list(offset)
        place[step_axis] += (i - ((count - 1) * 0.5)) * step
        geo = _box_geo(slat, tuple(place), taper)
        base = len(verts)
        verts.extend(geo[0])
        faces.extend(tuple(index + base for index in face) for face in geo[1])

    return _link(name, verts, faces)


# --------------------------------------------------------------------------
# Roles
# --------------------------------------------------------------------------


def build_tracked_hull(faction, profile, parts):
    """Hull, running gear and hull fittings — everything below the superstructure.

    Shared by the tank, the self-propelled gun and the anti-air vehicle, because
    all three are the same chassis with a different thing bolted to the deck. It
    returns the heights the superstructure has to agree with, `deck` above all:
    a turret ring, a casemate and an AA mount all sit on the same plate.
    """
    radius = profile["wheel_radius"]
    body = profile["hull_height"]
    length = profile["hull_length"]
    width = profile["hull_width"]
    track_w = profile["track_width"]

    track_x = (width * 0.5) + (track_w * 0.5)
    deck = radius + body
    track_top = radius * 1.62
    fender_z = radius * 1.95

    rig = {
        "radius": radius,
        "body": body,
        "length": length,
        "width": width,
        "track_w": track_w,
        "track_x": track_x,
        "deck": deck,
        "track_top": track_top,
        "fender_z": fender_z,
    }

    hull = sloped_hull("hull", length, width, body, profile["glacis"])
    hull.location = (0.0, 0.0, radius + (body * 0.5))
    parts.append(hull)
    paint(hull, MATERIALS["hull"])

    # --- running gear -------------------------------------------------------
    # Tracks: wide and few-wheeled for the Σοβιετικοί, narrow and exposed for the
    # Κινέζοι, wide over many wheels for the Δυτικοί. The width is the single
    # clearest faction cue from above, so the profile insists on it.
    for side in (-1, 1):
        track = box(
            f"track_{_side(side)}",
            (track_w, length * 0.98, track_top),
            offset=(side * track_x, 0.0, track_top * 0.5),
        )
        parts.append(track)
        paint(track, MATERIALS["rubber"])

        # A drive sprocket at the front and an idler at the rear. Two wheels
        # standing proud of the run at either end are what make a band of track
        # read as a track rather than as a rubber mat, and they are the reason a
        # tracked vehicle does not look like a lorry with the wheels boxed in.
        for name, roller_radius, y in (
            ("sprocket", radius * 0.90, length * 0.43),
            ("idler", radius * 0.86, -length * 0.43),
        ):
            roller = cylinder(f"{name}_{_side(side)}", roller_radius, track_w * 0.92, segments=12, axis="x")
            roller.location = (side * track_x, y, roller_radius)
            parts.append(roller)
            paint(roller, MATERIALS["gun"])

    count = profile["wheels"]
    span = length * 0.68

    for side in (-1, 1):
        for i in range(count):
            t = 0.0 if count == 1 else (i / (count - 1)) - 0.5

            # The cylinder is built around its own origin and *placed* with the
            # object transform. Baking the offset into the vertices instead would
            # put the mesh origin at the middle of the tank, and a wheel cannot
            # spin about an axis that is not through its own hub.
            wheel = cylinder(
                f"wheel_{_side(side)}{i + 1:02d}",
                radius,
                track_w * 0.88,
                segments=12,
                axis="x",
            )
            wheel.location = (side * track_x, t * span, radius)
            parts.append(wheel)
            paint(wheel, MATERIALS["tyre"])

    # --- fenders and skirts -------------------------------------------------
    for side in (-1, 1):
        fender = box(
            f"fender_{_side(side)}",
            (track_w * 1.10, length * 1.03, 0.07),
            offset=(side * track_x, 0.0, fender_z),
        )
        parts.append(fender)
        paint(fender, MATERIALS["deck"])

        # A mudflap behind each fender. A tracked vehicle without one has an
        # unfinished back end, and the first pass had none at all.
        flap = box(
            f"mudflap_{_side(side)}",
            (track_w * 1.08, 0.06, radius * 0.55),
            offset=(side * track_x, -length * 0.515, fender_z - (radius * 0.32)),
        )
        parts.append(flap)
        paint(flap, MATERIALS["rubber"])

    # Side skirts hang *from* the fenders, which is what the first pass got
    # wrong: they floated clear of the tracks as horizontal planks. These are
    # vertical panels whose top edge meets the fender and whose bottom edge stops
    # just above the ground. The Δυτικοί hang five separate panels a side.
    panels = {"full": 1, "panels": 5}.get(profile.get("skirt", "none"), 0)

    if panels > 0:
        skirt_h = fender_z * 0.93
        run = length * 0.88
        slat = (0.06, (run / panels) * (0.80 if panels > 1 else 1.0), skirt_h)

        for side in (-1, 1):
            skirt = bars(
                f"skirt_{_side(side)}",
                panels,
                slat,
                step=run / panels,
                offset=(side * (track_x + (track_w * 0.60)), 0.0, skirt_h * 0.5),
            )
            parts.append(skirt)
            paint(skirt, MATERIALS["skirt"])

    # --- engine deck --------------------------------------------------------
    # A raised plate across the rear of the hull with grille slats on top. This is
    # the detail that turns the back half of a tank from a lid into an engine, and
    # it is the first thing a top-down camera sees of a vehicle's deck.
    deck_y = -length * 0.30
    engine = box("engine_deck", (width * 0.98, length * 0.36, 0.16), offset=(0.0, deck_y, deck + 0.08))
    parts.append(engine)
    paint(engine, MATERIALS["deck"])

    slats = profile["grille_slats"]
    grille = bars(
        "grille",
        slats,
        (width * 0.58, 0.075, 0.07),
        step=(length * 0.34) / slats,
        offset=(0.0, deck_y, deck + 0.19),
    )
    parts.append(grille)
    paint(grille, MATERIALS["grille"], variation=0.04)

    # The engine deck need not be painted the faction colour at all: dark slats
    # on a mid deck are what read from above, and the raised plate gives the
    # silhouette a step where the first pass had one flat lid.
    _hull_fittings(faction, profile, parts, rig)

    return rig


def _hull_fittings(faction, profile, parts, rig):
    """The fittings that say *whose* chassis this is.

    The first pass gave all three factions the same parts in the same places, so
    they still read as one tank in three colours however different the hulls
    were. The fix is placement rather than proportion: the exhaust is on a
    different side, the fuel is a drum on one faction and a cell on another, and
    the stowage sits somewhere else on each.
    """
    length = rig["length"]
    width = rig["width"]
    deck = rig["deck"]
    radius = rig["radius"]
    body = rig["body"]
    track_x = rig["track_x"]
    track_w = rig["track_w"]
    fender_z = rig["fender_z"]

    # --- driver's hatch -----------------------------------------------------
    # On the deck in front of the ring, on the driver's side. The first pass put
    # a light grey plate here and it read as a decal: a hatch is a painted lid
    # with a raised rim and a hinge, and it is *armour*, not a bright square.
    hatch_x = -width * 0.26
    hatch_y = length * 0.30
    hatch = merge(
        "driver_hatch",
        [
            _box_geo((0.72, 0.66, 0.06), (hatch_x, hatch_y, deck + 0.03)),
            _box_geo((0.60, 0.54, 0.09), (hatch_x, hatch_y, deck + 0.055)),
            _box_geo((0.50, 0.07, 0.05), (hatch_x, hatch_y - 0.27, deck + 0.09)),
        ],
    )
    parts.append(hatch)
    paint(hatch, MATERIALS["hull"])

    # Periscopes: three glass blocks ahead of the hatch, which is exactly what a
    # driver sees through and reads as a pair of dark eyes from any angle.
    for i in range(3):
        scope = box(
            f"periscope_driver{i + 1}",
            (0.16, 0.09, 0.13),
            offset=(hatch_x + ((i - 1) * 0.24), hatch_y + 0.38, deck + 0.065),
        )
        parts.append(scope)
        paint(scope, MATERIALS["optics"])

    # --- bow machine gun ----------------------------------------------------
    gun_x = width * 0.24
    gun_y = length * 0.40
    bow = merge(
        "bow_mg",
        [
            _cyl_geo(0.17, 0.34, segments=12, axis="y", offset=(gun_x, gun_y, deck + 0.05)),
            _spin_geo(
                _cyl_geo(0.05, 0.62, segments=8, axis="y", offset=(gun_x, gun_y + 0.44, deck + 0.05)),
                pitch=-2.0,
                pivot=(gun_x, gun_y, deck + 0.05),
            ),
        ],
    )
    parts.append(bow)
    paint(bow, MATERIALS["gun_dark"])

    # --- headlights and their guards ---------------------------------------
    for side in (-1, 1):
        lamp = box(
            f"lamp_{_side(side)}",
            (0.24, 0.20, 0.22),
            offset=(side * width * 0.34, (length * 0.5) - 0.32, deck + 0.05),
        )
        parts.append(lamp)
        paint(lamp, MATERIALS["lamp"])

        guard = bars(
            f"lamp_guard_{_side(side)}",
            3,
            (0.30, 0.04, 0.30),
            step=0.12,
            offset=(side * width * 0.34, (length * 0.5) - 0.16, deck + 0.06),
            axis="x",
        )
        parts.append(guard)
        paint(guard, MATERIALS["steel"], variation=0.03)

    # --- exhaust ------------------------------------------------------------
    # One muffler and one thin pipe down one fender. The first pass had two fat
    # brown boxes lying on the engine deck, which read as a pair of sausages.
    exhaust_x = profile["exhaust_side"] * (track_x - (track_w * 0.18))

    muffler = box("muffler", (track_w * 0.60, length * 0.16, 0.17), offset=(exhaust_x, -length * 0.20, fender_z + 0.13))
    parts.append(muffler)
    paint(muffler, MATERIALS["rust"], variation=0.05)

    pipe = cylinder("exhaust", 0.055, length * 0.42, segments=8, axis="y", offset=(exhaust_x, length * 0.02, fender_z + 0.16))
    parts.append(pipe)
    paint(pipe, MATERIALS["gun_dark"])

    # --- tow hooks ----------------------------------------------------------
    hooks = merge(
        "tow_hooks",
        [
            _box_geo((0.20, 0.36, 0.16), (-width * 0.32, -(length * 0.5) - 0.10, radius + (body * 0.22))),
            _box_geo((0.20, 0.36, 0.16), (width * 0.32, -(length * 0.5) - 0.10, radius + (body * 0.22))),
        ],
    )
    parts.append(hooks)
    paint(hooks, MATERIALS["gun_dark"])

    # --- fuel, stowage and the rest, per faction ---------------------------
    if profile["fuel"] == "drums":
        # Σοβιετικοί: drums along the fender opposite the exhaust, the roundest
        # thing on an otherwise rectangular vehicle.
        drum_side = -profile["exhaust_side"]

        for i in range(2):
            drum = cylinder(f"fuel_drum{i + 1}", 0.20, length * 0.15, segments=14, axis="y")
            drum.location = (
                drum_side * track_x,
                (-length * 0.32) + (i * length * 0.17),
                fender_z + 0.24,
            )
            parts.append(drum)
            paint(drum, MATERIALS["fuel"])

        # An unditching log on the other fender: cheap, and unmistakably Soviet.
        log = cylinder("log", radius * 0.40, length * 0.24, segments=10, axis="y")
        log.location = (-drum_side * track_x, length * 0.26, fender_z + 0.11)
        log.rotation_euler = (0.0, 0.0, math.radians(3.0))
        parts.append(log)
        paint(log, MATERIALS["canvas"], variation=0.09)

        tarp = box("stowage", (track_w * 1.0, length * 0.11, 0.16), offset=(drum_side * track_x, length * 0.06, fender_z + 0.10))
        parts.append(tarp)
        paint(tarp, MATERIALS["canvas"], variation=0.08)

    elif profile["fuel"] == "cells":
        # Κινέζοι: external fuel cells on the rear deck, a row of spare track
        # links on the glacis, and the infantry telephone on the back plate.
        for side in (-1, 1):
            cell = box(
                f"fuel_cell_{_side(side)}",
                (width * 0.30, length * 0.22, 0.26),
                offset=(side * width * 0.32, -length * 0.33, deck + 0.29),
            )
            parts.append(cell)
            paint(cell, MATERIALS["fuel"])

        links = bars(
            "spare_track",
            6,
            (width * 0.46, 0.11, 0.07),
            step=0.17,
            offset=(0.0, (length * 0.5) - 0.62, deck + 0.035),
        )
        parts.append(links)
        paint(links, MATERIALS["gun_dark"], variation=0.05)

        phone = merge(
            "telephone",
            [
                _box_geo((0.34, 0.12, 0.46), (width * 0.30, -(length * 0.5) - 0.06, radius + (body * 0.58))),
                _box_geo((0.16, 0.08, 0.22), (width * 0.30, -(length * 0.5) - 0.13, radius + (body * 0.58))),
            ],
        )
        parts.append(phone)
        paint(phone, MATERIALS["panel"])

        crate = box("stowage", (track_w * 0.95, length * 0.16, 0.20), offset=(-track_x, -length * 0.14, fender_z + 0.14))
        parts.append(crate)
        paint(crate, MATERIALS["crate"], variation=0.06)

    else:
        # Δυτικοί: no external fuel on a vehicle whose engine deck is already
        # crowded with electronics. A stowage box a side and a cable drum.
        for side in (-1, 1):
            crate = box(
                f"stowage_{_side(side)}",
                (track_w * 1.0, length * 0.14, 0.22),
                offset=(side * track_x, length * 0.10, fender_z + 0.15),
            )
            parts.append(crate)
            paint(crate, MATERIALS["crate"], variation=0.06)

        drum = cylinder("cable_drum", 0.26, 0.20, segments=12, axis="x")
        drum.location = (-(width * 0.34), -length * 0.34, deck + 0.28)
        parts.append(drum)
        paint(drum, MATERIALS["gun_dark"])

        optics = box("gunner_optics", (0.34, 0.26, 0.18), offset=(width * 0.36, length * 0.34, deck + 0.07))
        parts.append(optics)
        paint(optics, MATERIALS["optics"])


def build_turret(faction, profile, parts, rig):
    """A turret: a ring to sit on, a body, a roof full of fittings, and a gun.

    Everything inside is measured from the ring, because the turret is built
    around its own origin — that is the pivot the renderer traverses — so the
    roof is at `sz` and the hull's deck never appears in here. The barrel is
    parented to the turret, so the gun follows it for free.
    """
    sx, sy, sz = profile["turret_scale"]
    kind = profile["turret"]

    # --- the ring -----------------------------------------------------------
    # The first pass had none, so the turret grew out of the hull rather than
    # sitting on it. A dark race a little wider than the turret's base, sunk into
    # the deck, is the entire fix and it costs two cylinders.
    ring = merge(
        "turret_ring",
        [
            _scale_geo(_cyl_geo(1.0, 0.05, segments=18, axis="z"), (sx * 1.12, sy * 1.12, 1.0)),
            _scale_geo(_cyl_geo(1.0, 0.18, segments=18, axis="z"), (sx * 1.04, sy * 1.04, 1.0)),
        ],
    )
    ring.location = (0.0, 0.0, rig["deck"] + 0.02)
    parts.append(ring)
    paint(ring, MATERIALS["gun"], variation=0.03)

    # --- the body -----------------------------------------------------------
    geos = []

    if kind == "dome":
        # Σοβιετικοί: a cast turret. An elliptical drum with a dome on top and a
        # rounded bustle behind. The first pass was a bare squashed hemisphere,
        # which read as a mushroom; the straight-sided drum under the dome and
        # the overhang at the back are what make it read as armour.
        drum_h = sz * 0.46
        geos.append(_scale_geo(_cyl_geo(1.0, drum_h, segments=16, axis="z", offset=(0.0, 0.0, drum_h * 0.5)), (sx, sy, 1.0)))
        geos.append(
            _scale_geo(
                _dome_geo(1.0, (1.0, 1.0, 1.0), segments=16, rings=4, offset=(0.0, 0.0, drum_h * 0.90)),
                (sx, sy, sz - (drum_h * 0.90)),
            )
        )
        geos.append(_move_geo(_scale_geo(_cyl_geo(1.0, sz * 0.62, segments=14, axis="z"), (sx * 0.64, sy * 0.30, 1.0)), (0.0, -sy * 1.06, sz * 0.31)))
        # A cast turret's roof is not flat: a low crown plate finishes it off.
        geos.append(_move_geo(_scale_geo(_cyl_geo(1.0, 0.07, segments=14, axis="z"), (sx * 0.72, sy * 0.62, 1.0)), (0.0, -sy * 0.12, sz * 1.01)))

    elif kind == "box":
        # Κινέζοι: welded, not cast. Near-vertical sides, a flat roof and a front
        # plate bolted on at an angle — the first pass was a plain cuboid, which
        # is why it read as a box rather than as a turret.
        geos.append(_frustum_geo((sx * 2.0, sy * 2.0), (0.88, 0.86), sz, top_shift=(0.0, -sy * 0.07)))
        geos.append(
            _spin_geo(
                _box_geo((sx * 1.86, 0.20, sz * 0.74), offset=(0.0, sy * 0.86, sz * 0.34)),
                pitch=-34.0,
                pivot=(0.0, sy * 0.86, sz * 0.06),
            )
        )
        geos.append(_move_geo(_box_geo((sx * 1.68, sy * 1.30, 0.09)), (0.0, -sy * 0.16, sz * 0.99)))
        # A rectangular bustle box on the back: the welded turret's answer to the
        # cast one's overhang, and where the crew's kit goes.
        geos.append(_frustum_geo((sx * 1.72, sy * 0.92), (0.84, 0.76), sz * 0.50, offset=(0.0, -sy * 1.26, sz * 0.04)))

    else:
        # Δυτικοί: a wedge. Near-vertical sides with a strongly raked front plate
        # and an overhanging rear, plus two angular cheeks leaning in from the
        # front corners. The first pass was a flat plate lying on the hull
        # because it was barely taller than the deck it sat on.
        geos.append(_frustum_geo((sx * 2.0, sy * 2.0), (0.88, 0.40), sz, top_shift=(0.0, -sy * 0.34)))
        geos.append(_move_geo(_box_geo((sx * 1.76, sy * 0.92, 0.10)), (0.0, -sy * 0.44, sz * 0.99)))

        for side in (-1, 1):
            cheek = _box_geo((0.20, sy * 1.10, sz * 0.76), offset=(side * sx * 0.84, sy * 0.34, sz * 0.38))
            geos.append(_spin_geo(cheek, yaw=-side * 24.0, pivot=(side * sx * 0.84, sy * 0.34, sz * 0.38)))

        geos.append(_frustum_geo((sx * 1.86, sy * 0.96), (0.88, 0.80), sz * 0.50, offset=(0.0, -sy * 1.32, sz * 0.06)))

    turret = merge("turret", geos)
    turret.location = (0.0, 0.0, rig["deck"] + 0.11)
    parts.append(turret)
    paint(turret, MATERIALS["turret"])

    # --- the gun ------------------------------------------------------------
    # The mantlet is the most important single piece on the whole model: it is
    # what the barrel comes out of. It is parented to the *barrel* rather than to
    # the turret, so it rises and falls with the gun and covers the joint at every
    # elevation instead of only at rest. The first pass had none at all, which is
    # why the barrel appeared to grow out of thin air above the hull.
    gun_y = sy * 0.84
    gun_z = sz * 0.52
    bore = profile["barrel_radius"]

    barrel = cylinder(
        "barrel",
        bore,
        profile["barrel_length"],
        segments=12,
        axis="y",
        offset=(0.0, profile["barrel_length"] * 0.5, 0.0),
    )
    barrel.parent = turret
    barrel.location = (0.0, gun_y, gun_z)
    barrel.rotation_euler = (math.radians(profile.get("gun_elevation", 2.0)), 0.0, 0.0)
    parts.append(barrel)
    paint(barrel, MATERIALS["gun"])

    if kind == "dome":
        mantlet_geos = [
            _cyl_geo(bore * 3.4, 0.70, segments=14, axis="y", offset=(0.0, 0.10, 0.0)),
            _cyl_geo(bore * 2.0, 0.34, segments=12, axis="y", offset=(0.0, 0.52, 0.0)),
        ]
        mantlet_paint = MATERIALS["turret"]
    elif kind == "box":
        mantlet_geos = [
            _box_geo((bore * 7.0, 0.56, bore * 5.6), offset=(0.0, 0.10, 0.0)),
            _cyl_geo(bore * 1.9, 0.30, segments=12, axis="y", offset=(0.0, 0.46, 0.0)),
        ]
        mantlet_paint = MATERIALS["gun_dark"]
    else:
        mantlet_geos = [
            _box_geo((bore * 9.0, 0.64, bore * 6.0), offset=(0.0, 0.10, 0.0)),
            _cyl_geo(bore * 1.8, 0.40, segments=12, axis="y", offset=(0.0, 0.52, 0.0)),
            # The recoil cylinder above the barrel: two tubes are what makes a
            # modern gun look like a gun rather than a pipe.
            _cyl_geo(bore * 1.3, 1.30, segments=10, axis="y", offset=(0.0, 0.95, bore * 4.2)),
        ]
        mantlet_paint = MATERIALS["gun_dark"]

    mantlet = merge("mantlet", mantlet_geos)
    mantlet.parent = barrel
    parts.append(mantlet)
    paint(mantlet, mantlet_paint)

    if profile["muzzle_brake"]:
        brake = cylinder("muzzle", bore * 1.9, bore * 4.0, segments=12, axis="y")
        brake.parent = barrel
        brake.location = (0.0, profile["barrel_length"] - (bore * 1.6), 0.0)
        parts.append(brake)
        paint(brake, MATERIALS["gun_dark"])

    if kind == "wedge":
        # Thermal sleeve: a fatter section over the rear half of the barrel.
        sleeve = cylinder("thermal_sleeve", bore * 1.9, profile["barrel_length"] * 0.30, segments=12, axis="y")
        sleeve.parent = barrel
        sleeve.location = (0.0, profile["barrel_length"] * 0.24, 0.0)
        parts.append(sleeve)
        paint(sleeve, MATERIALS["canvas"], variation=0.04)

    _roof_fittings(faction, profile, parts, turret, (sx, sy, sz))

    return turret


def _roof_fittings(faction, profile, parts, turret, scale):
    """Everything on the turret roof, which is what a top-down camera sees.

    A turret seen from 700 m is a rectangle or an ellipse; the cupola, the
    periscopes, the hatches and the machine gun are the only things that say
    which way it is facing and whose it is.
    """
    sx, sy, sz = scale
    cupola_x, cupola_y = profile["cupola"]

    # --- commander's cupola -------------------------------------------------
    cupola = merge(
        "cupola",
        [
            _cyl_geo(0.38, 0.30, segments=14, axis="z", offset=(cupola_x, cupola_y, sz + 0.15)),
            _cyl_geo(0.31, 0.07, segments=14, axis="z", offset=(cupola_x, cupola_y, sz + 0.32)),
        ],
    )
    cupola.parent = turret
    parts.append(cupola)
    paint(cupola, MATERIALS["turret"])

    # Periscopes round the cupola: four dark glass blocks, which is what a
    # commander actually looks through and reads as a ring of eyes from above.
    for i in range(4):
        angle = math.radians(40.0 + (i * 90.0))
        scope = box(
            f"periscope_c{i + 1}",
            (0.15, 0.13, 0.15),
            offset=(cupola_x + (math.sin(angle) * 0.36), cupola_y + (math.cos(angle) * 0.36), sz + 0.08),
        )
        scope.parent = turret
        parts.append(scope)
        paint(scope, MATERIALS["optics"])

    # --- loader's hatch -----------------------------------------------------
    # Named `hatch` and painted as armour. The first pass made it light grey and
    # it read as a decal stuck on the roof rather than as a lid in it.
    hatch_x = -cupola_x * 0.85 if abs(cupola_x) > 0.1 else sx * 0.55
    hatch_y = sy * 0.18
    hatch = merge(
        "hatch",
        [
            _box_geo((0.70, 0.66, 0.06), (hatch_x, hatch_y, sz + 0.03)),
            _box_geo((0.58, 0.54, 0.09), (hatch_x, hatch_y, sz + 0.06)),
            _box_geo((0.16, 0.16, 0.06), (hatch_x, hatch_y - 0.34, sz + 0.08)),
        ],
    )
    hatch.parent = turret
    parts.append(hatch)
    paint(hatch, MATERIALS["turret"])

    # --- stowage basket -----------------------------------------------------
    # Δυτικοί only: a cage round the back of the turret. From above it is a frame
    # of thin lines where the other two factions have plain armour, and it is the
    # clearest single difference between the three from the game's camera.
    if profile["basket"]:
        geos = []
        back = -sy * 1.52
        outer = sx * 1.22

        for level in (0.30, 0.58, 0.86):
            geos.append(_box_geo((outer * 2.0, 0.06, 0.06), (0.0, back, sz * level)))

            for side in (-1, 1):
                geos.append(_box_geo((0.06, sy * 0.94, 0.06), (side * outer, -sy * 0.98, sz * level)))

        for x in (-outer, 0.0, outer):
            geos.append(_box_geo((0.06, 0.06, sz * 0.60), (x, back, sz * 0.58)))

        for side in (-1, 1):
            geos.append(_box_geo((0.06, 0.06, sz * 0.60), (side * outer, -sy * 0.52, sz * 0.58)))

        basket = merge("basket", geos)
        basket.parent = turret
        parts.append(basket)
        paint(basket, MATERIALS["gun"], variation=0.04)

    # --- smoke dischargers --------------------------------------------------
    if profile["smoke"]:
        for side in (-1, 1):
            geos = []

            for i in range(profile["smoke"]):
                geos.append(
                    _cyl_geo(0.075, 0.32, segments=8, axis="y", offset=(side * sx * 0.94, sy * 0.10 + (i * 0.20) - 0.30, sz * 0.60))
                )

            bank = merge(f"smoke_{_side(side)}", geos)
            bank.parent = turret
            parts.append(bank)
            paint(bank, MATERIALS["gun_dark"], variation=0.05)

    # --- roof machine gun ---------------------------------------------------
    # Mounted on the cupola for the Σοβιετικοί and on a ring at the rear of the
    # roof for the Κινέζοι, so even the machine gun is in a different place.
    if faction in ("soviet", "chinese"):
        if faction == "soviet":
            mount_x, mount_y = cupola_x, cupola_y - 0.30
        else:
            mount_x, mount_y = 0.0, -sy * 0.72

        mg = merge(
            "roof_mg",
            [
                _cyl_geo(0.07, 0.34, segments=8, axis="z", offset=(mount_x, mount_y, sz + 0.26)),
                _box_geo((0.16, 0.38, 0.16), (mount_x, mount_y, sz + 0.44)),
                _cyl_geo(0.035, 1.05, segments=8, axis="y", offset=(mount_x, mount_y + 0.62, sz + 0.46)),
            ],
        )
        mg.parent = turret
        parts.append(mg)
        paint(mg, MATERIALS["gun_dark"])

    # --- antennas -----------------------------------------------------------
    # One whip for the two mass-production factions, two plus a sensor mast for
    # the Δυτικοί, whose whole identity in this game is electronics.
    whips = 2 if faction == "western" else 1

    for i in range(whips):
        antenna = cylinder(f"antenna{i + 1}", 0.025, 1.30 - (i * 0.20), segments=6, axis="z")
        antenna.parent = turret
        antenna.location = ((sx * (0.55 - (i * 1.1))), -sy * 0.95, sz + (0.65 - (i * 0.10)))
        parts.append(antenna)
        paint(antenna, MATERIALS["gun_dark"])

    if faction == "western":
        mast = merge(
            "sensor_mast",
            [
                _cyl_geo(0.06, 0.70, segments=8, axis="z", offset=(sx * 0.40, sy * 0.50, sz + 0.35)),
                _box_geo((0.30, 0.22, 0.20), (sx * 0.40, sy * 0.50, sz + 0.78)),
            ],
        )
        mast.parent = turret
        parts.append(mast)
        paint(mast, MATERIALS["panel"], variation=0.04)


def build_tank(faction, profile):
    """The main battle tank: the chassis with a full traversing turret on it."""
    root = _root(f"{faction}_tank")

    parts = []
    rig = build_tracked_hull(faction, profile, parts)
    build_turret(faction, profile, parts, rig)

    join(root, parts)
    return root


def build_electro(faction, profile):
    """Σοβιετικοί electro prototype: a tank hull under a coil emitter.

    Deliberately unlike every other turret in the game — a stack of rings rather
    than a box or a dome — because there are only ever two of them and the player
    has to be able to pick them out at a glance.
    """
    root = build_tank(faction, profile)
    root.name = f"{faction}_electro"

    for obj in list(root.children):
        if obj.name in ("barrel", "muzzle"):
            obj.scale.y = 0.4
            obj.rotation_euler = (math.radians(2.0), 0.0, 0.0)

    turret = next((o for o in root.children if o.name == "turret"), None)

    if turret is not None:
        for i in range(3):
            ring = cylinder("coil", 0.5 + (i * 0.16), 0.22, segments=14, axis="y")
            ring.parent = turret
            ring.location = (0.0, 0.7 + (i * 0.55), 1.05)
            ring.rotation_euler = (math.radians(90.0), 0.0, 0.0)
            paint(ring, MATERIALS["coil"])

        emitter = cylinder("emitter", 0.34, 1.5, segments=12, axis="y")
        emitter.parent = turret
        emitter.location = (0.0, 1.6, 1.05)
        paint(emitter, MATERIALS["coil"])

    return root


# Structures — the headquarters, the factory, the power plant, the nuclear plant
# and the design bureau — are generated by tools/blender/build_buildings.py, which
# imports this file's material table, its noise and its exporters. A building is
# read from above and a vehicle from three-quarters, and once the structures
# outgrew a base box with a turret on it they wanted their own file.


def build_katyusha(faction):
    """Σοβιετικοί rocket artillery: a lorry with a bank of rocket tubes on the bed.

    Wheeled rather than tracked, which is both true to the vehicle and a
    silhouette nobody else has.

    The first pass had its cab at the *back* — the model is authored facing +Y and
    the cab was at -Y, so the lorry drove backwards — no bed, no mudguards, no
    bumper, no lights and no fuel tanks, and the launcher was one big flat plate
    with four tubes hiding behind it. The rack here is a frame the tubes are
    visibly *in*: two rows of four, muzzles forward, each one a dark circle in a
    bank you can count from above.
    """
    root = _root(f"{faction}_katyusha")

    parts = []

    wheel_radius = 0.62
    half = 1.02
    frame_z = 1.02

    # --- chassis and bed ----------------------------------------------------
    frame = box("Chassis", (2.10, 6.10, 0.34), offset=(0.0, -0.10, frame_z))
    parts.append(frame)
    paint(frame, MATERIALS["gun_dark"], variation=0.04)

    bed_y = -1.55
    bed_l = 3.30

    bed = merge(
        "Bed",
        [
            _box_geo((2.34, bed_l, 0.14), (0.0, bed_y, frame_z + 0.24)),
            _box_geo((0.12, bed_l, 0.62), (-half * 1.02, bed_y, frame_z + 0.58)),
            _box_geo((0.12, bed_l, 0.62), (half * 1.02, bed_y, frame_z + 0.58)),
            _box_geo((2.34, 0.12, 0.78), (0.0, bed_y - (bed_l * 0.5), frame_z + 0.66)),
        ],
    )
    parts.append(bed)
    paint(bed, MATERIALS["crate"], variation=0.07)

    # --- bonnet, cab and bumper --------------------------------------------
    bonnet = box("Bonnet", (2.06, 1.45, 0.86), offset=(0.0, 2.42, frame_z + 0.57))
    parts.append(bonnet)
    paint(bonnet, MATERIALS["hull"])

    grille = bars(
        "Radiator",
        4,
        (1.90, 0.08, 0.58),
        step=0.17,
        offset=(0.0, 3.16, frame_z + 0.57),
        axis="z",
    )
    parts.append(grille)
    paint(grille, MATERIALS["grille"], variation=0.05)

    cab = box("Cab", (2.24, 1.46, 1.36), offset=(0.0, 0.98, frame_z + 0.86))
    parts.append(cab)
    paint(cab, MATERIALS["deck"])

    screen = box("Screen", (2.02, 0.10, 0.62), offset=(0.0, 1.72, frame_z + 1.10))
    parts.append(screen)
    paint(screen, MATERIALS["glass"])

    for side in (-1, 1):
        window = box(
            f"Window_{_side(side)}",
            (0.08, 0.90, 0.52),
            offset=(side * 1.13, 0.98, frame_z + 1.10),
        )
        parts.append(window)
        paint(window, MATERIALS["glass"])

    roof = box("CabRoof", (2.30, 1.52, 0.10), offset=(0.0, 0.98, frame_z + 1.58))
    parts.append(roof)
    paint(roof, MATERIALS["hull"])

    bumper = box("Bumper", (2.30, 0.18, 0.24), offset=(0.0, 3.36, frame_z - 0.06))
    parts.append(bumper)
    paint(bumper, MATERIALS["steel"], variation=0.04)

    # --- fuel tanks, spare and stowage -------------------------------------
    for side in (-1, 1):
        tank = cylinder(f"FuelTank_{_side(side)}", 0.24, 1.10, segments=12, axis="y")
        tank.location = (side * 1.22, -0.30, frame_z - 0.34)
        parts.append(tank)
        paint(tank, MATERIALS["fuel"], variation=0.05)

    spare = cylinder("Spare", 0.56, 0.24, segments=14, axis="z")
    spare.location = (0.0, -2.70, frame_z - 0.30)
    parts.append(spare)
    paint(spare, MATERIALS["tyre"])

    tools = box("Stowage", (0.34, 1.60, 0.30), offset=(1.28, 0.40, frame_z + 0.30))
    parts.append(tools)
    paint(tools, MATERIALS["crate"], variation=0.06)

    # --- the launcher -------------------------------------------------------
    # Named "turret" because it is the part that traverses to face what the unit
    # is shooting at, which is exactly what a rocket rack does. The cab is named
    # "Cab": a lorry whose cab swung round to aim would be a very strange sight.
    rack_y = -2.90
    rack_pitch = 34.0
    tube_len = 3.70
    tubes = []

    for (x, z) in ((-0.78, -0.20), (-0.26, -0.20), (0.26, -0.20), (0.78, -0.20),
                   (-0.78, 0.20), (-0.26, 0.20), (0.26, 0.20), (0.78, 0.20)):
        tubes.append(_cyl_geo(0.155, tube_len, segments=10, axis="y", offset=(x, tube_len * 0.5, z)))

    frame_geos = [
        _box_geo((0.16, tube_len * 1.04, 0.16), (-1.02, tube_len * 0.5, 0.0)),
        _box_geo((0.16, tube_len * 1.04, 0.16), (1.02, tube_len * 0.5, 0.0)),
        _box_geo((2.20, 0.16, 0.16), (0.0, tube_len * 0.12, 0.0)),
        _box_geo((2.20, 0.16, 0.16), (0.0, tube_len * 0.88, 0.0)),
        _box_geo((0.16, 0.16, 0.52), (-1.02, tube_len * 0.12, 0.0)),
        _box_geo((0.16, 0.16, 0.52), (1.02, tube_len * 0.12, 0.0)),
    ]

    # Dark muzzles at the forward end of every tube: eight circles in a bank,
    # which is what makes this read as a rocket launcher rather than a crate of
    # pipes even at 700 m.
    muzzles = [
        _cyl_geo(0.175, 0.16, segments=10, axis="y", offset=(x, tube_len - 0.06, z))
        for (x, z) in ((-0.78, -0.20), (-0.26, -0.20), (0.26, -0.20), (0.78, -0.20),
                       (-0.78, 0.20), (-0.26, 0.20), (0.26, 0.20), (0.78, 0.20))
    ]

    rack = merge("turret", tubes + frame_geos + muzzles)
    rack.rotation_euler = (math.radians(rack_pitch), 0.0, 0.0)
    rack.location = (0.0, rack_y, frame_z + 0.42)
    parts.append(rack)
    paint(rack, MATERIALS["gun"], variation=0.05)

    # Two elevation rams under the rack and a shield for the crew at the back.
    for side in (-1, 1):
        ram = cylinder(f"Ram_{_side(side)}", 0.10, 1.30, segments=10, axis="y")
        ram.location = (side * 0.92, rack_y + 0.70, frame_z + 0.42)
        ram.rotation_euler = (math.radians(58.0), 0.0, 0.0)
        parts.append(ram)
        paint(ram, MATERIALS["steel"], variation=0.04)

    shield = box("Shield", (2.20, 0.10, 0.60), offset=(0.0, rack_y - 0.30, frame_z + 1.02))
    parts.append(shield)
    paint(shield, MATERIALS["hull"])

    # --- wheels and mudguards ----------------------------------------------
    # Single tyres at the front, twinned at the back: the cheapest way to say
    # "lorry" rather than "car" from above.
    tyre_w = 0.30

    for side in (-1, 1):
        axles = ((2.52, 1), (-1.50, 2), (-2.62, 2))

        for index, (y, twin) in enumerate(axles):
            for wheel_index in range(twin):
                wheel = cylinder(
                    f"wheel_{_side(side)}{index + 1:02d}{'ab'[wheel_index]}",
                    wheel_radius,
                    tyre_w,
                    segments=14,
                    axis="x",
                )
                wheel.location = (side * (half + (wheel_index * 0.30)), y, wheel_radius)
                parts.append(wheel)
                paint(wheel, MATERIALS["tyre"])

        for index, (y, twin) in enumerate(axles):
            # A mudguard hugs its wheels. The first attempt at these was a wide
            # flat plate either side of the bed, which read as a pair of planks
            # exactly the way the tank's side skirts had.
            guard = box(
                f"Mudguard_{_side(side)}{index + 1}",
                (0.42 if twin == 1 else 0.62, wheel_radius * 2.20 if twin == 1 else wheel_radius * 2.9, 0.09),
                offset=(side * (half + (0.02 if twin == 1 else 0.16)), y, wheel_radius * 2.10),
            )
            parts.append(guard)
            paint(guard, MATERIALS["deck"], variation=0.05)

        lamp = box(
            f"lamp_{_side(side)}",
            (0.24, 0.20, 0.22),
            offset=(side * 0.82, 3.16, wheel_radius * 2.30),
        )
        parts.append(lamp)
        paint(lamp, MATERIALS["lamp"])

        guard = bars(
            f"lamp_guard_{_side(side)}",
            3,
            (0.30, 0.04, 0.30),
            step=0.12,
            offset=(side * 0.82, 3.30, wheel_radius * 2.30),
            axis="x",
        )
        parts.append(guard)
        paint(guard, MATERIALS["steel"], variation=0.03)

    join(root, parts)
    return root


def build_harvester(faction, length, width, wheel_radius):
    """Συλλέκτης: a wheeled ore truck with a front loader and a hopper.

    It has no weapon and no place in a fight, so its silhouette has to say what it
    is for from across the map: a big open bucket at the front and an ore hopper at
    the back. Nothing else in the game has either.

    Three things were wrong with the first pass. The wheels were built
    `width * 0.22` thick, which on a 2.9 m wide truck is a 0.64 m wide drum on a
    0.62 m radius tyre — as wide as it was round, so it read as a roller rather
    than a wheel. The bucket was small, low and tucked behind the front wheel
    where nobody could see it. And the cab was a single plate with one dark panel
    on the front.
    """
    root = _root(f"{faction}_harvester")

    parts = []

    # Tyres, not drums: a third of the old width, and the outer pair at the back
    # is doubled, which is what a loader's rear axle looks like.
    tyre_w = width * 0.13

    # The chassis rides above the wheels, not through them.
    deck = wheel_radius + 0.50
    body = box("hull", (width, length * 0.94, 1.15), offset=(0.0, -length * 0.02, deck + 0.20))
    parts.append(body)
    paint(body, MATERIALS["hull"])

    # --- the bucket ---------------------------------------------------------
    # Big, high and well forward of the front axle. This is the whole silhouette,
    # and the first pass hid it behind the wheel.
    bucket_z = wheel_radius * 1.45
    bucket_y = length * 0.60

    bucket = merge(
        "Bucket",
        [
            _box_geo((width * 1.30, length * 0.30, 0.16), (0.0, 0.0, -0.42)),
            _box_geo((width * 1.30, 0.18, 0.94), (0.0, -length * 0.15, 0.0)),
            _box_geo((0.18, length * 0.30, 0.94), (-width * 0.57, 0.0, 0.0)),
            _box_geo((0.18, length * 0.30, 0.94), (width * 0.57, 0.0, 0.0)),
        ],
    )
    bucket.rotation_euler = (math.radians(-16.0), 0.0, 0.0)
    bucket.location = (0.0, bucket_y, bucket_z)
    parts.append(bucket)
    paint(bucket, MATERIALS["steel"], variation=0.05)

    # A cutting edge in hazard yellow along the bottom lip, and a load of ore
    # sitting in it: a bucket carrying nothing reads as a scoop.
    lip = box("BucketLip", (width * 1.32, 0.20, 0.16), offset=(0.0, length * 0.145, -0.44))
    lip.parent = bucket
    parts.append(lip)
    paint(lip, MATERIALS["hazard"], variation=0.03)

    load = merge(
        "BucketLoad",
        [
            _scale_geo(_dome_geo(1.0, (1.0, 1.0, 1.0), segments=12, rings=3), (width * 0.52, length * 0.13, 0.40), pivot=(0.0, 0.0, 0.0)),
        ],
    )
    load.parent = bucket
    load.location = (0.0, -0.02, -0.28)
    parts.append(load)
    paint(load, MATERIALS["ore"], variation=0.12)

    # --- loader linkage -----------------------------------------------------
    # Two arms and two rams, named `Boom` rather than `Arms`: the renderer swings
    # anything whose name starts with "Arm", and a loader whose linkage flapped
    # about as it drove would be a strange thing to watch.
    for side in (-1, 1):
        boom = box(
            f"Boom_{_side(side)}",
            (0.22, length * 0.46, 0.30),
            offset=(side * width * 0.42, length * 0.16, wheel_radius * 1.35),
        )
        boom.rotation_euler = (math.radians(-19.0), 0.0, 0.0)
        parts.append(boom)
        paint(boom, MATERIALS["gun_dark"], variation=0.05)

        ram = cylinder(f"Ram_{_side(side)}", 0.11, length * 0.32, segments=10, axis="y")
        ram.location = (side * width * 0.42, length * 0.24, wheel_radius * 2.35)
        ram.rotation_euler = (math.radians(24.0), 0.0, 0.0)
        parts.append(ram)
        paint(ram, MATERIALS["steel"], variation=0.04)

    tower = merge(
        "BoomTower",
        [
            _box_geo((width * 0.96, 0.34, 1.10), (0.0, length * 0.26, deck + 0.42)),
            _box_geo((0.26, 0.26, 0.60), (-width * 0.42, length * 0.26, deck + 1.00)),
            _box_geo((0.26, 0.26, 0.60), (width * 0.42, length * 0.26, deck + 1.00)),
        ],
    )
    parts.append(tower)
    paint(tower, MATERIALS["hull"])

    # --- the cab ------------------------------------------------------------
    # A box with glass on four sides and a roof, on the left of the centre line
    # like a real loader's. The first pass was one plate with a windscreen.
    cab_x = -width * 0.22
    cab_y = -length * 0.04
    cab_z = deck + 1.55
    cab_w = width * 0.54
    cab_l = length * 0.24

    cab = box("Cab", (cab_w, cab_l, 1.16), offset=(cab_x, cab_y, cab_z))
    parts.append(cab)
    paint(cab, MATERIALS["deck"])

    for side in (-1, 1):
        window = box(
            f"CabGlass_{_side(side)}",
            (0.05, cab_l * 0.86, 0.56),
            offset=(cab_x + (side * cab_w * 0.5), cab_y, cab_z + 0.14),
        )
        parts.append(window)
        paint(window, MATERIALS["glass"])

    for end, name in ((1, "Front"), (-1, "Rear")):
        window = box(
            f"CabGlass{name}",
            (cab_w * 0.80, 0.05, 0.56),
            offset=(cab_x, cab_y + (end * cab_l * 0.5), cab_z + 0.14),
        )
        parts.append(window)
        paint(window, MATERIALS["glass"])

    roof = box("CabRoof", (cab_w * 1.10, cab_l * 1.10, 0.10), offset=(cab_x, cab_y, cab_z + 0.63))
    parts.append(roof)
    paint(roof, MATERIALS["hull"])

    # A rotating beacon: the one part of a support vehicle that should move while
    # it works, and the reason a harvester is not mistaken for a parked lorry.
    beacon = merge(
        "radar",
        [
            _cyl_geo(0.10, 0.20, segments=10, axis="z", offset=(0.0, 0.0, -0.10)),
            _cyl_geo(0.19, 0.24, segments=10, axis="z", offset=(0.0, 0.0, 0.12)),
        ],
    )
    beacon.location = (cab_x, cab_y, cab_z + 0.85)
    parts.append(beacon)
    paint(beacon, MATERIALS["hazard"])

    # --- engine deck and exhaust -------------------------------------------
    hood = box("Hood", (width * 0.86, length * 0.30, 0.86), offset=(0.0, -length * 0.26, deck + 0.72))
    parts.append(hood)
    paint(hood, MATERIALS["hull"])

    vents = bars(
        "HoodVents",
        5,
        (width * 0.60, 0.09, 0.06),
        step=length * 0.045,
        offset=(0.0, -length * 0.26, deck + 1.17),
    )
    parts.append(vents)
    paint(vents, MATERIALS["grille"], variation=0.05)

    stack = merge(
        "Exhaust",
        [
            _cyl_geo(0.10, 1.40, segments=10, axis="z", offset=(width * 0.30, -length * 0.12, deck + 1.10)),
            _cyl_geo(0.14, 0.18, segments=10, axis="z", offset=(width * 0.30, -length * 0.12, deck + 1.84)),
        ],
    )
    parts.append(stack)
    paint(stack, MATERIALS["rust"], variation=0.06)

    # --- the hopper ---------------------------------------------------------
    # Open-topped, with sides high enough to see the ore sitting in it from above.
    hop_y = -length * 0.38
    hop_w = width * 0.94
    hop_l = length * 0.34
    hop_z = deck + 0.85

    floor = box("HopperFloor", (hop_w, hop_l, 0.14), offset=(0.0, hop_y, hop_z - 0.55))
    parts.append(floor)
    paint(floor, MATERIALS["grille"], variation=0.05)

    for side in (-1, 1):
        wall = box(
            f"Hopper_{_side(side)}",
            (0.12, hop_l, 1.10),
            offset=(side * hop_w * 0.5, hop_y, hop_z),
        )
        parts.append(wall)
        paint(wall, MATERIALS["crate"], variation=0.09)

    for end, name in ((1, "Front"), (-1, "Tail")):
        wall = box(
            f"Hopper{name}",
            (hop_w, 0.12, 1.10 if end < 0 else 0.80),
            offset=(0.0, hop_y + (end * hop_l * 0.5), hop_z - (0.0 if end < 0 else 0.15)),
        )
        parts.append(wall)
        paint(wall, MATERIALS["crate"], variation=0.09)

    ore = merge(
        "Ore",
        [
            _scale_geo(_dome_geo(1.0, (1.0, 1.0, 1.0), segments=14, rings=4), (hop_w * 0.46, hop_l * 0.46, 0.62)),
        ],
    )
    ore.location = (0.0, hop_y, hop_z + 0.42)
    parts.append(ore)
    paint(ore, MATERIALS["ore"], variation=0.13)

    # --- wheels, guards and lights -----------------------------------------
    axles = ((length * 0.30, 1), (-length * 0.30, 2))

    for side in (-1, 1):
        for index, (y, twin) in enumerate(axles):
            for wheel_index in range(twin):
                outward = 1.0 + (wheel_index * 0.92)
                wheel = cylinder(
                    f"wheel_{_side(side)}{index + 1:02d}{'ab'[wheel_index]}",
                    wheel_radius,
                    tyre_w,
                    segments=14,
                    axis="x",
                )
                wheel.location = (side * (width * 0.5) * outward, y, wheel_radius)
                parts.append(wheel)
                paint(wheel, MATERIALS["tyre"])

        # Mudguards over both axles: a wheeled vehicle with bare wheels above the
        # chassis looks unfinished from three-quarters.
        for index, (y, twin) in enumerate(axles):
            guard = box(
                f"Mudguard_{_side(side)}{index + 1}",
                (width * (0.44 if twin == 1 else 0.62), wheel_radius * 2.30, 0.08),
                offset=(side * width * 0.52, y, wheel_radius * 2.05),
            )
            parts.append(guard)
            paint(guard, MATERIALS["deck"], variation=0.05)

    for side in (-1, 1):
        lamp = box(
            f"lamp_{_side(side)}",
            (0.22, 0.18, 0.20),
            offset=(side * width * 0.30, length * 0.44, wheel_radius * 2.28),
        )
        parts.append(lamp)
        paint(lamp, MATERIALS["lamp"])

    # A fuel tank slung under the chassis on one side, and steps on the other.
    tank = cylinder("FuelTank", width * 0.16, length * 0.26, segments=12, axis="y")
    tank.location = (width * 0.62, -length * 0.08, wheel_radius * 1.30)
    parts.append(tank)
    paint(tank, MATERIALS["fuel"], variation=0.05)

    steps = bars(
        "Steps",
        3,
        (width * 0.16, 0.10, 0.05),
        step=0.34,
        offset=(-width * 0.66, -length * 0.02, wheel_radius * 1.10),
        axis="z",
    )
    parts.append(steps)
    paint(steps, MATERIALS["steel"], variation=0.04)

    join(root, parts)
    return root


def _ring_geo(inner, outer, thickness, segments=16, offset=(0.0, 0.0, 0.0)):
    """A flat annulus in the XY plane: a rotor guard ring, in one shell.

    Two concentric circles swept to a thickness, which is the only shape in this
    file that a solid cylinder cannot stand in for — a ring needs a hole.
    """
    ox, oy, oz = offset
    verts = []
    faces = []
    half = thickness * 0.5

    for z in (-half, half):
        for radius in (inner, outer):
            for i in range(segments):
                angle = (2.0 * math.pi * i) / segments
                verts.append((math.cos(angle) * radius + ox, math.sin(angle) * radius + oy, z + oz))

    inner_bottom = 0
    outer_bottom = segments
    inner_top = segments * 2
    outer_top = segments * 3

    for i in range(segments):
        j = (i + 1) % segments

        # Top and bottom faces, then the two walls.
        faces.append((outer_top + i, outer_top + j, inner_top + j, inner_top + i))
        faces.append((inner_bottom + i, inner_bottom + j, outer_bottom + j, outer_bottom + i))
        faces.append((outer_bottom + i, outer_bottom + j, outer_top + j, outer_top + i))
        faces.append((inner_top + i, inner_top + j, inner_bottom + j, inner_bottom + i))

    return verts, faces


def build_drone(faction):
    """Κινέζοι drone: four thin rotors on a small fuselage.

    The first pass gave it one solid disc a side, six centimetres thick and three
    quarters of a metre across, and those two black plates were the entire model.
    A rotor that reads as a rotor is two thin crossed blades on a hub — 20 mm of
    blade, not 60 mm of plate — and everything else here exists to say which way
    the machine is pointing: a tapered nose, a camera ball under it and skids.

    The booms are named `Boom_*` rather than `Arm*` because the renderer swings
    anything starting with "Arm" as a walking limb, which had the drone flapping.
    """
    root = _root(f"{faction}_drone")

    parts = []

    # --- fuselage -----------------------------------------------------------
    body = merge(
        "Fuselage",
        [
            _frustum_geo((0.62, 1.20), (0.72, 1.0), 0.30, offset=(0.0, -0.16, 0.0)),
            _spin_geo(
                _box_geo((0.62, 0.52, 0.30), offset=(0.0, 0.62, 0.15)),
                pitch=12.0,
                pivot=(0.0, 0.42, 0.15),
            ),
            _box_geo((0.30, 0.40, 0.16), (0.0, -0.86, 0.06)),
        ],
    )
    parts.append(body)
    paint(body, MATERIALS["robot"])

    # A dark canopy over the sensor bay, which is what makes a small drone look
    # like a machine rather than a model aircraft.
    canopy = merge(
        "Canopy",
        [
            _scale_geo(_dome_geo(1.0, (1.0, 1.0, 1.0), segments=12, rings=3), (0.24, 0.34, 0.20)),
        ],
    )
    canopy.location = (0.0, 0.30, 0.28)
    parts.append(canopy)
    paint(canopy, MATERIALS["glass"])

    # --- camera ball --------------------------------------------------------
    # Under the nose, hanging below the fuselage where it can see the ground. The
    # first pass had a glass box on top of the nose instead, which is the one
    # place a reconnaissance camera cannot be.
    ball = merge(
        "Camera",
        [
            _scale_geo(_dome_geo(1.0, (1.0, 1.0, 1.0), segments=12, rings=3), (0.17, 0.17, 0.17)),
            _spin_geo(_scale_geo(_dome_geo(1.0, (1.0, 1.0, 1.0), segments=12, rings=3), (0.17, 0.17, 0.17)), pitch=180.0),
            _cyl_geo(0.13, 0.14, segments=12, axis="z", offset=(0.0, 0.0, 0.06)),
            _cyl_geo(0.09, 0.16, segments=10, axis="y", offset=(0.0, 0.02, -0.13)),
        ],
    )
    ball.location = (0.0, 0.72, -0.16)
    parts.append(ball)
    paint(ball, MATERIALS["gun_dark"], variation=0.05)

    # --- booms --------------------------------------------------------------
    booms = []

    for (ix, iy) in ((-1, 1), (1, 1), (-1, -1), (1, -1)):
        reach = math.hypot(ix * 0.72, iy * 0.58)
        angle = math.degrees(math.atan2(ix * 0.72, iy * 0.58))

        booms.append(
            _spin_geo(
                _box_geo((0.11, reach * 1.02, 0.11), offset=(0.0, reach * 0.5, 0.0)),
                yaw=-angle,
                pivot=(0.0, 0.0, 0.0),
            )
        )

    boom = merge("Booms", booms)
    boom.location = (0.0, 0.0, 0.10)
    parts.append(boom)
    paint(boom, MATERIALS["gun_dark"], variation=0.05)

    # --- rotors -------------------------------------------------------------
    # Two crossed blades and a hub inside a thin guard ring. Named `radar_*`: a
    # rotor is the one thing on a drone that genuinely turns, and that is the name
    # the contract reserves for a part that turns continuously. The ring is what
    # gives each rotor an edge from above — bare blades alone read as a pair of
    # crossed sticks at the distance the game is played at.
    blade = 0.92

    for (ix, iy) in ((-1, 1), (1, 1), (-1, -1), (1, -1)):
        rotor = merge(
            f"radar_{'f' if iy > 0 else 'r'}{_side(ix)}",
            [
                _cyl_geo(0.10, 0.12, segments=10, axis="z", offset=(0.0, 0.0, 0.0)),
                _box_geo((blade, 0.15, 0.022), (0.0, 0.0, 0.06)),
                _spin_geo(_box_geo((blade, 0.15, 0.022), (0.0, 0.0, 0.06)), yaw=90.0),
                _ring_geo(0.54, 0.60, 0.028, segments=16, offset=(0.0, 0.0, 0.05)),
                _cyl_geo(0.055, 0.06, segments=8, axis="z", offset=(0.0, 0.0, 0.09)),
            ],
        )
        rotor.location = (ix * 0.72, iy * 0.58, 0.16)
        parts.append(rotor)
        paint(rotor, MATERIALS["gun_dark"], variation=0.04)

    # --- landing skids ------------------------------------------------------
    for side in (-1, 1):
        skid = merge(
            f"Skid_{_side(side)}",
            [
                _box_geo((0.09, 1.30, 0.07), (0.0, 0.0, -0.42)),
                _box_geo((0.06, 0.40, 0.07), (0.0, -0.10, -0.42)),
                _box_geo((0.06, 0.07, 0.34), (0.0, 0.34, -0.26)),
                _box_geo((0.06, 0.07, 0.34), (0.0, -0.34, -0.26)),
            ],
        )
        skid.location = (side * 0.34, 0.06, 0.0)
        parts.append(skid)
        paint(skid, MATERIALS["steel"], variation=0.04)

    # --- navigation lights --------------------------------------------------
    for side in (-1, 1):
        light = box(f"Nav_{_side(side)}", (0.08, 0.08, 0.06), offset=(side * 0.72, 0.58, 0.25))
        parts.append(light)
        paint(light, MATERIALS["lamp"])

    join(root, parts)
    return root


# How the self-propelled gun's casemate is cut, per faction. It is the same
# superstructure in three shapes — the differences are where it sits, how tall it
# is and what is bolted to it — which is the same rule the turrets follow.
CASEMATE = {
    # Low and mid-mounted with a cast, rounded front plate, like an SU-85: the
    # Σοβιετικοί casemate is the smallest of the three and sits furthest forward.
    "soviet": {"length": 0.46, "y": -0.19, "height": 0.86, "front": "rounded", "mg": True, "sight": "small"},

    # Tallest and flattest, at the very back, carrying spare track links on its
    # front plate: welded plate, mass produced.
    "chinese": {"length": 0.48, "y": -0.24, "height": 1.04, "front": "flat", "mg": True, "sight": "small"},

    # The longest, with a rear door, a range-finder blister on each side of the
    # front plate and a big shielded sight on the roof edge.
    "western": {"length": 0.56, "y": -0.22, "height": 0.96, "front": "raked", "mg": False, "sight": "large"},
}


def build_artillery(faction, profile):
    """Self-propelled gun: a casemate at the rear of the hull, gun in its front.

    Deliberately *not* a tank with a longer barrel, which is what the first pass
    was. The superstructure is a different object: a raised, open-topped box on
    the back half of the hull with the gun in a mount at its front face. From
    above that is a rectangle with a dark open interior and a barrel leaving the
    front of it; from the side it is a step up at the back rather than a lump in
    the middle. Nothing about it can be mistaken for a turret.

    The mount is called `turret`, and it is the one thing here that genuinely
    traverses: the renderer turns it to face the target. It is built at the front
    *face* of the casemate, so a full traverse swings the gun above and in front
    of the side plates instead of through them.
    """
    cut = CASEMATE[faction]

    root = _root(f"{faction}_artillery")
    parts = []
    rig = build_tracked_hull(faction, profile, parts)

    length = rig["length"]
    width = rig["width"]
    deck = rig["deck"]

    case_len = length * cut["length"]
    case_w = width * (0.90 if faction != "western" else 0.96)

    # The casemate is a fraction of the hull's height, so the three chassis keep
    # their own proportions: a Σοβιετικοί casemate is low, a Κινέζοι one is tall.
    case_h = max(cut["height"] * (profile["hull_height"] / 1.15), profile["hull_height"] * 0.78)
    case_y = length * cut["y"]
    front_y = case_y + (case_len * 0.5)
    rear_y = case_y - (case_len * 0.5)

    # --- the box ------------------------------------------------------------
    for side in (-1, 1):
        plate = box(
            f"casemate_{_side(side)}",
            (0.12, case_len, case_h),
            offset=(side * case_w * 0.5, case_y, deck + (case_h * 0.5)),
        )
        parts.append(plate)
        paint(plate, MATERIALS["hull"])

    rear = box("casemate_rear", (case_w, 0.14, case_h), offset=(0.0, rear_y, deck + (case_h * 0.5)))
    parts.append(rear)
    paint(rear, MATERIALS["hull"])

    # The front plate leans back as it rises, so the casemate has a face rather
    # than a wall. A rounded cast one for the Σοβιετικοί, a plain raked plate for
    # the other two.
    rake = {"rounded": 20.0, "flat": 34.0, "raked": 30.0}[cut["front"]]
    front = box("casemate_front", (case_w, 0.14, case_h * 1.30), offset=(0.0, 0.0, 0.0))
    front.rotation_euler = (math.radians(rake), 0.0, 0.0)
    front.location = (0.0, front_y - 0.12, deck + (case_h * 0.50))
    parts.append(front)
    paint(front, MATERIALS["hull"])

    # --- the open top -------------------------------------------------------
    # A dark floor and a rim. Without both of these the casemate is a closed box
    # and reads as a turret again, which is exactly what the first pass did.
    floor = box(
        "casemate_floor",
        (case_w - 0.26, case_len - 0.26, 0.05),
        offset=(0.0, case_y, deck + 0.04),
    )
    parts.append(floor)
    paint(floor, MATERIALS["grille"], variation=0.05)

    rim_z = deck + case_h + 0.04
    rim = merge(
        "casemate_rim",
        [
            _box_geo((case_w, 0.16, 0.12), (0.0, rear_y, rim_z)),
            _box_geo((case_w, 0.16, 0.12), (0.0, front_y - 0.28, rim_z)),
            _box_geo((0.16, case_len, 0.12), (-case_w * 0.5, case_y, rim_z)),
            _box_geo((0.16, case_len, 0.12), (case_w * 0.5, case_y, rim_z)),
        ],
    )
    parts.append(rim)
    paint(rim, MATERIALS["hull"])

    # --- inside -------------------------------------------------------------
    racks = box(
        "ammo_racks",
        (case_w * 0.30, case_len * 0.62, case_h * 0.55),
        offset=(case_w * 0.26, case_y - (case_len * 0.10), deck + (case_h * 0.30)),
    )
    parts.append(racks)
    paint(racks, MATERIALS["crate"], variation=0.08)

    breech = merge(
        "breech",
        [
            _box_geo((0.72, 0.80, 0.62), (-case_w * 0.16, front_y - 1.10, deck + (case_h * 0.62))),
            _box_geo((0.46, 0.50, 0.30), (-case_w * 0.16, front_y - 1.60, deck + (case_h * 0.62))),
        ],
    )
    parts.append(breech)
    paint(breech, MATERIALS["gun_dark"])

    for i in range(2):
        seat = box(
            f"crew_{i + 1}",
            (0.42, 0.16, 0.44),
            offset=((-case_w * 0.20) + (i * 0.30), case_y - (case_len * 0.30), deck + (case_h * 0.42)),
        )
        parts.append(seat)
        paint(seat, MATERIALS["uniform"], variation=0.06)

    # --- the gun ------------------------------------------------------------
    mount_z = deck + (case_h * 0.80)
    bore = profile["barrel_radius"] * 1.15
    barrel_len = profile["barrel_length"] * 1.45
    mount_y = front_y - 0.30

    if cut["front"] == "rounded":
        mount_geos = [
            _cyl_geo(bore * 3.2, 0.86, segments=14, axis="y", offset=(0.0, 0.10, 0.0)),
            _scale_geo(_dome_geo(0.44, (1.0, 0.7, 1.0), segments=12, rings=3), (1.0, 1.0, 1.0)),
        ]
    else:
        mount_geos = [
            _box_geo((1.16, 0.72, 0.72), (0.0, 0.10, 0.0)),
            _box_geo((1.42, 0.24, 0.92), (0.0, 0.44, 0.0)),
            _cyl_geo(0.16, 1.30, segments=12, axis="x", offset=(0.0, 0.10, 0.0)),
        ]

    mount = merge("turret", mount_geos)
    mount.location = (0.0, mount_y, mount_z)
    parts.append(mount)
    paint(mount, MATERIALS["turret"])

    barrel = cylinder(
        "barrel",
        bore,
        barrel_len,
        segments=12,
        axis="y",
        offset=(0.0, (barrel_len * 0.5) + 0.30, 0.0),
    )
    barrel.parent = mount
    barrel.rotation_euler = (math.radians(6.0), 0.0, 0.0)
    parts.append(barrel)
    paint(barrel, MATERIALS["gun"])

    recoil = cylinder("recoil", bore * 1.4, barrel_len * 0.34, segments=10, axis="y")
    recoil.parent = barrel
    recoil.location = (0.0, barrel_len * 0.34, bore * 4.4)
    parts.append(recoil)
    paint(recoil, MATERIALS["gun_dark"])

    brake = cylinder("muzzle", bore * 2.4, bore * 4.6, segments=12, axis="y")
    brake.parent = barrel
    brake.location = (0.0, barrel_len - (bore * 2.0), 0.0)
    parts.append(brake)
    paint(brake, MATERIALS["gun_dark"])

    # --- what marks it out from the other two -------------------------------
    if cut["mg"]:
        mg = merge(
            "roof_mg",
            [
                _cyl_geo(0.07, 0.34, segments=8, axis="z", offset=(-case_w * 0.34, front_y - 0.52, rim_z + 0.30)),
                _box_geo((0.16, 0.36, 0.16), (-case_w * 0.34, front_y - 0.52, rim_z + 0.48)),
                _cyl_geo(0.04, 1.10, segments=8, axis="y", offset=(-case_w * 0.34, front_y + 0.08, rim_z + 0.50)),
            ],
        )
        parts.append(mg)
        paint(mg, MATERIALS["gun_dark"])

    if cut["sight"] == "large":
        sight = merge(
            "sight",
            [
                _box_geo((0.58, 0.44, 0.34), (case_w * 0.30, front_y - 0.60, rim_z + 0.20)),
                _box_geo((0.40, 0.10, 0.22), (case_w * 0.30, front_y - 0.38, rim_z + 0.22)),
            ],
        )
        parts.append(sight)
        paint(sight, MATERIALS["optics"])

        for side in (-1, 1):
            blister = box(
                f"rangefinder_{_side(side)}",
                (0.34, 0.44, 0.26),
                offset=(side * case_w * 0.44, front_y - 0.40, deck + (case_h * 0.70)),
            )
            parts.append(blister)
            paint(blister, MATERIALS["optics"])

        door = box("casemate_door", (case_w * 0.44, 0.10, case_h * 0.60), offset=(0.0, rear_y - 0.10, deck + (case_h * 0.32)))
        parts.append(door)
        paint(door, MATERIALS["hull"])

        antenna = cylinder("antenna1", 0.025, 1.30, segments=6, axis="z")
        antenna.location = (case_w * 0.40, rear_y + 0.30, rim_z + 0.65)
        parts.append(antenna)
        paint(antenna, MATERIALS["gun_dark"])

    else:
        scope = merge(
            "sight",
            [
                _cyl_geo(0.11, 0.34, segments=10, axis="y", offset=(case_w * 0.26, front_y - 0.44, rim_z + 0.10)),
                _box_geo((0.20, 0.14, 0.16), (case_w * 0.26, front_y - 0.24, rim_z + 0.10)),
            ],
        )
        parts.append(scope)
        paint(scope, MATERIALS["optics"])

        if faction == "chinese":
            links = bars(
                "casemate_links",
                5,
                (case_w * 0.44, 0.11, 0.07),
                step=0.17,
                offset=(0.0, front_y - 0.06, deck + (case_h * 0.62)),
            )
            parts.append(links)
            paint(links, MATERIALS["gun_dark"], variation=0.05)

        box_on_rim = box("stowage", (case_w * 0.34, 0.34, 0.26), offset=(-case_w * 0.30, rear_y + 0.30, rim_z + 0.14))
        parts.append(box_on_rim)
        paint(box_on_rim, MATERIALS["crate"], variation=0.07)

    # A recoil spade at the back: the plate an SPG lowers into the ground, and a
    # shape nothing else in the game has.
    spade = box("spade", (width * 0.52, 0.16, 0.62), offset=(0.0, 0.0, 0.0))
    spade.rotation_euler = (math.radians(-38.0), 0.0, 0.0)
    spade.location = (0.0, -(length * 0.5) - 0.16, rig["radius"] * 0.55)
    parts.append(spade)
    paint(spade, MATERIALS["gun"], variation=0.05)

    join(root, parts)
    return root


def build_antiair(faction, profile):
    """Anti-air: an open mount with twin elevated tubes on a wider footprint.

    The first pass shortened the tank's barrel and called it done, which left a
    tank with a needle gun: the twins were invisible and nothing was pointed at
    the sky. This is the opposite — no turret at all, a pedestal mount with an
    open cradle, two tubes visible as two tubes, a sight head and a radar that
    actually sweeps, and outriggers putting the vehicle's feet wider than its
    tracks.
    """
    root = _root(f"{faction}_antiair")
    parts = []
    rig = build_tracked_hull(faction, profile, parts)

    length = rig["length"]
    width = rig["width"]
    deck = rig["deck"]
    radius = rig["radius"]

    mount_y = length * (0.10 if faction == "soviet" else -0.02)

    # --- outriggers ---------------------------------------------------------
    # Deployed legs, angled out and down with a pad on the ground. From above
    # they double the width of the vehicle's footprint and there is nothing else
    # in the fleet shaped like that.
    for side in (-1, 1):
        leg = box(f"outrigger_{_side(side)}", (0.90, 0.30, 0.24), offset=(side * (width * 0.5 + 0.30), -length * 0.28, radius * 0.86))
        leg.rotation_euler = (0.0, math.radians(side * -18.0), 0.0)
        parts.append(leg)
        paint(leg, MATERIALS["gun"], variation=0.04)

        pad = cylinder(f"outrigger_pad_{_side(side)}", 0.26, 0.10, segments=12, axis="z")
        pad.location = (side * (width * 0.5 + 0.72), -length * 0.28, 0.05)
        parts.append(pad)
        paint(pad, MATERIALS["gun_dark"])

    # --- pedestal -----------------------------------------------------------
    pedestal = merge(
        "mount_base",
        [
            _cyl_geo(0.96, 0.46, segments=18, axis="z", offset=(0.0, 0.0, 0.23)),
            _box_geo((2.30, 2.10, 0.16), (0.0, 0.0, 0.50)),
            _box_geo((0.34, 0.34, 0.30), (0.0, -1.30, 0.32)),
        ],
    )
    pedestal.location = (0.0, mount_y, deck)
    parts.append(pedestal)
    paint(pedestal, MATERIALS["deck"])

    # --- the mount ----------------------------------------------------------
    # An open cradle: a floor, two side plates and a shield at the front, so the
    # tubes are visible between the plates from every angle. It is the part
    # called `turret` because it is the part that traverses.
    cradle_z = deck + 0.58
    cradle = merge(
        "turret",
        [
            _box_geo((2.10, 1.90, 0.14), (0.0, 0.0, 0.0)),
            _box_geo((0.14, 1.90, 0.72), (-1.05, 0.0, 0.40)),
            _box_geo((0.14, 1.90, 0.72), (1.05, 0.0, 0.40)),
            _box_geo((2.10, 0.14, 0.72), (0.0, -0.95, 0.40)),
            _spin_geo(
                _box_geo((2.16, 0.12, 0.86), offset=(0.0, 0.96, 0.48)),
                pitch=26.0,
                pivot=(0.0, 0.96, 0.14),
            ),
        ],
    )
    cradle.location = (0.0, mount_y, cradle_z)
    parts.append(cradle)
    paint(cradle, MATERIALS["deck"])

    # Ammunition bins on the outside of the side plates: from above, two pale
    # boxes either side of the mount.
    for side in (-1, 1):
        bin_box = box(
            f"ammo_{_side(side)}",
            (0.30, 1.30, 0.46),
            offset=(side * 1.24, mount_y, cradle_z + 0.30),
        )
        parts.append(bin_box)
        paint(bin_box, MATERIALS["panel"], variation=0.05)

    # --- the twins ----------------------------------------------------------
    # Both tubes in *one* part called `barrel`, so the pair can never be
    # animated apart and is impossible to miss from any angle: two tubes and two
    # flash hiders at the top of the mount.
    bore = profile["barrel_radius"] * 1.15
    tube_len = profile["barrel_length"] * (0.72 if faction != "western" else 0.82)
    spread = 0.34 if faction != "western" else 0.42

    geos = []

    for side in (-1, 1):
        x = side * spread
        geos.append(_cyl_geo(bore, tube_len, segments=10, axis="y", offset=(x, tube_len * 0.5, 0.0)))
        geos.append(_cyl_geo(bore * 1.9, bore * 3.4, segments=10, axis="y", offset=(x, tube_len - (bore * 1.2), 0.0)))
        # A perforated jacket over the rear third: two fat sections are what make
        # a pair of thin tubes read from 700 m.
        geos.append(_cyl_geo(bore * 1.7, tube_len * 0.30, segments=10, axis="y", offset=(x, tube_len * 0.22, 0.0)))

    # The cradle between them, so the tubes are visibly carried rather than
    # floating.
    geos.append(_box_geo((spread * 2.0 + bore * 2.0, 0.60, 0.34), (0.0, tube_len * 0.22, -bore * 0.4)))

    twins = merge("barrel", geos)
    twins.parent = cradle
    twins.location = (0.0, -0.30, 0.52)
    twins.rotation_euler = (math.radians(24.0), 0.0, 0.0)
    parts.append(twins)
    paint(twins, MATERIALS["gun"])

    # --- sight head ---------------------------------------------------------
    sight = merge(
        "sight",
        [
            _cyl_geo(0.14, 0.30, segments=10, axis="z", offset=(0.72, 0.44, 0.62)),
            _box_geo((0.34, 0.30, 0.26), (0.72, 0.44, 0.98)),
            _box_geo((0.22, 0.08, 0.18), (0.72, 0.60, 1.00)),
        ],
    )
    sight.parent = cradle
    parts.append(sight)
    paint(sight, MATERIALS["optics"])

    # --- radar --------------------------------------------------------------
    # Named `radar` and genuinely turning: the renderer sweeps anything with that
    # name, and an AA vehicle is the one other place in the fleet where a
    # continuously moving part is the honest answer. Its mesh is built around its
    # own origin, so it turns about its own mast instead of orbiting the vehicle.
    if faction == "western":
        radar_geos = [
            _box_geo((0.14, 1.30, 0.09), (0.0, 0.0, 0.34)),
            _box_geo((0.10, 0.16, 0.62), (0.0, 0.0, 0.14)),
            _cyl_geo(0.06, 0.70, segments=8, axis="z", offset=(0.0, 0.0, -0.35)),
        ]
    else:
        radar_geos = [
            _cyl_geo(0.52, 0.10, segments=14, axis="z", offset=(0.0, 0.0, 0.40)),
            _cyl_geo(0.10, 0.62, segments=8, axis="z", offset=(0.0, 0.0, 0.14)),
            _cyl_geo(0.24, 0.16, segments=10, axis="z", offset=(0.0, 0.0, 0.34)),
        ]

    radar = merge("radar", radar_geos)
    radar.parent = cradle
    radar.location = (-0.66, -0.60, 0.80)
    parts.append(radar)
    paint(radar, MATERIALS["steel"], variation=0.05)

    # --- crew seats and a canvas --------------------------------------------
    for i in range(2):
        seat = box(
            f"crew_{i + 1}",
            (0.44, 0.16, 0.46),
            offset=((-0.52) + (i * 1.04), mount_y - 0.52, cradle_z + 0.30),
        )
        parts.append(seat)
        paint(seat, MATERIALS["uniform"], variation=0.06)

    join(root, parts)
    return root


def build_aircraft(faction, wingspan, length, swept):
    """Aircraft: fuselage along +Y, wings across X, swept back by `swept`.

    Plain but acceptable in the first pass, and four things kept it there: no
    intakes, no canopy frame, no navigation lights, and pylons so shallow they
    vanished into the wing. All four are added here, and each faction gets its own
    intake arrangement, so the three aircraft differ by more than wingspan.

    The names stay clear of the animation contract's limb prefixes — nothing here
    may start with "Arm", "Leg" or "Shin" — because the renderer swings anything
    that does, and an aeroplane whose wings flapped would be memorable for the
    wrong reason.
    """
    root = _root(f"{faction}_aircraft")

    parts = []

    body_h = 0.92

    # --- fuselage -----------------------------------------------------------
    body = merge(
        "hull",
        [
            _frustum_geo((1.20, length * 0.88), (0.86, 1.0), body_h, offset=(0.0, -length * 0.30, -body_h * 0.5)),
            # A nose that tapers in plan rather than stopping square, with a
            # radome in front of it.
            _box_geo((1.14, length * 0.16, body_h * 0.92), offset=(0.0, length * 0.36, 0.0), taper=0.55),
        ],
    )
    parts.append(body)
    paint(body, MATERIALS["hull"])

    nose = merge(
        "Nose",
        [
            _box_geo((0.62, length * 0.10, 0.56), offset=(0.0, length * 0.50, -0.02), taper=0.35),
            _cyl_geo(0.14, 0.60, segments=10, axis="y", offset=(0.0, length * 0.54, -0.02)),
        ],
    )
    parts.append(nose)
    paint(nose, MATERIALS["gun_dark"], variation=0.04)

    # --- canopy -------------------------------------------------------------
    # A framed canopy: glass, a spine and a windscreen bow. One dark slab was the
    # whole of the first pass's cockpit.
    cockpit = length * 0.10
    canopy = merge(
        "Canopy",
        [
            _spin_geo(
                _box_geo((0.86, cockpit * 1.30, 0.52), offset=(0.0, length * 0.20, body_h * 0.42)),
                pitch=-14.0,
                pivot=(0.0, length * 0.20, body_h * 0.42),
            ),
        ],
    )
    parts.append(canopy)
    paint(canopy, MATERIALS["glass"])

    frame = merge(
        "CanopyFrame",
        [
            _box_geo((0.94, 0.10, 0.46), (0.0, length * 0.255, body_h * 0.44)),
            _box_geo((0.12, cockpit * 1.30, 0.14), (0.0, length * 0.20, body_h * 0.70)),
            _box_geo((0.12, 0.12, 0.44), (0.0, length * 0.145, body_h * 0.42)),
        ],
    )
    parts.append(frame)
    paint(frame, MATERIALS["hull"])

    # --- intakes ------------------------------------------------------------
    # Per faction, because an intake is the single most recognisable feature of
    # a jet from three-quarters: two at the wing roots for the Σοβιετικοί, one
    # under the nose for the Κινέζοι, two half-buried in the fuselage for the
    # Δυτικοί.
    intakes = []

    if faction == "chinese":
        intakes.append(_box_geo((0.78, 1.10, 0.44), (0.0, length * 0.30, -body_h * 0.62), taper=0.9))
        intakes.append(_box_geo((0.62, 0.18, 0.34), (0.0, length * 0.355, -body_h * 0.62)))
    elif faction == "soviet":
        for side in (-1, 1):
            intakes.append(_box_geo((0.44, length * 0.24, 0.62), (side * 0.78, length * 0.06, -0.05)))
            intakes.append(_box_geo((0.34, 0.14, 0.50), (side * 0.78, length * 0.18, -0.05)))
    else:
        for side in (-1, 1):
            intakes.append(_box_geo((0.60, length * 0.20, 0.52), (side * 0.62, length * 0.02, 0.06)))
            intakes.append(_box_geo((0.46, 0.14, 0.40), (side * 0.62, length * 0.12, 0.06)))

    intake = merge("Intakes", intakes)
    parts.append(intake)
    paint(intake, MATERIALS["gun_dark"], variation=0.05)

    # --- wings, pylons and stores ------------------------------------------
    # The wings are one box each so the sweep is visible in plan view, which is how
    # an RTS camera sees an aircraft.
    for side in (-1, 1):
        tag = "L" if side < 0 else "R"
        yaw = -swept * 30.0 * side

        wing = box(
            f"Wing{tag}",
            (wingspan * 0.5, length * 0.24, 0.26),
            offset=(0.0, -swept * length * 0.5, 0.0),
        )
        wing.location = (side * wingspan * 0.25, 0.0, -0.10)
        wing.rotation_euler = (0.0, 0.0, math.radians(yaw))
        parts.append(wing)
        paint(wing, MATERIALS["deck"])

        # Two pylons a wing, tall enough to see from above and from the side. The
        # first pass had none, so the stores appeared to float.
        pylons = []
        stores = []

        for reach in (0.52, 0.78):
            x = side * wingspan * reach * 0.5
            y = -swept * length * 0.5 * reach

            pylons.append(_box_geo((0.18, length * 0.10, 0.32), (x, y, -0.30)))
            stores.append(
                _spin_geo(
                    _box_geo((0.24, length * 0.19, 0.24), offset=(x, y + 0.20, -0.52)),
                    pitch=-3.0,
                    pivot=(x, y, -0.52),
                )
            )
            stores.append(_box_geo((0.10, 0.24, 0.10), (x, y + (length * 0.19), -0.52), taper=0.4))

        pylon = merge(f"Pylons{tag}", pylons)
        parts.append(pylon)
        paint(pylon, MATERIALS["gun_dark"], variation=0.04)

        store = merge(f"Store{tag}", stores)
        parts.append(store)
        paint(store, MATERIALS["steel"], variation=0.05)

        # Wingtip rails: a rail and a missile at each tip, which is what gives a
        # wing its end from above.
        tip_x = side * wingspan * 0.5
        tip_y = -swept * length * 0.5

        rail = merge(
            f"Rail{tag}",
            [
                _box_geo((0.16, length * 0.20, 0.14), (tip_x, tip_y, -0.02)),
                _cyl_geo(0.09, length * 0.16, segments=8, axis="y", offset=(tip_x, tip_y, -0.14)),
            ],
        )
        parts.append(rail)
        paint(rail, MATERIALS["gun_dark"], variation=0.04)

        light = box(f"Nav_{tag}", (0.14, 0.16, 0.12), offset=(tip_x, tip_y - (length * 0.10), -0.02))
        parts.append(light)
        paint(light, MATERIALS["lamp"])

    # --- tail ---------------------------------------------------------------
    tail = box("Tail", (wingspan * 0.36, length * 0.15, 0.20), offset=(0.0, 0.0, 0.0))
    tail.location = (0.0, -length * 0.45, 0.0)
    parts.append(tail)
    paint(tail, MATERIALS["deck"])

    fins = []

    if faction == "western":
        # Twin fins, canted outwards: the Δυτικοί silhouette.
        for side in (-1, 1):
            fins.append(
                _spin_geo(
                    _box_geo((0.14, length * 0.17, 0.80), offset=(side * 0.62, 0.0, 0.48)),
                    yaw=side * -16.0,
                    pivot=(side * 0.62, 0.0, 0.0),
                )
            )
    else:
        fins.append(_box_geo((0.16, length * 0.17, 0.84), (0.0, 0.0, 0.46)))

    fin = merge("Fin", fins)
    fin.location = (0.0, -length * 0.45, 0.0)
    parts.append(fin)
    paint(fin, MATERIALS["hull"])

    # Engine nozzles: two dark circles at the back, which is what a jet has
    # instead of a tail.
    nozzles = []

    for side in (-1, 1) if faction != "chinese" else (0,):
        nozzles.append(_cyl_geo(0.30, 0.66, segments=12, axis="y", offset=(side * 0.42, -length * 0.50, -0.12)))
        nozzles.append(_cyl_geo(0.22, 0.20, segments=12, axis="y", offset=(side * 0.42, -length * 0.56, -0.12)))

    nozzle = merge("Nozzles", nozzles)
    parts.append(nozzle)
    paint(nozzle, MATERIALS["gun_dark"], variation=0.04)

    join(root, parts)
    return root


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------


def clear_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)

    for mesh in list(bpy.data.meshes):
        bpy.data.meshes.remove(mesh)


def export(path):
    """Exports the whole scene, asking for vertex colours when the build supports it."""
    common = {
        "filepath": path,
        "export_format": "GLB",
        "export_yup": True,
        "export_apply": True,
        "use_selection": False,
    }

    for extra in (
        {"export_vertex_color": "ACTIVE", "export_all_vertex_colors": True},
        {"export_vertex_color": "ACTIVE"},
        {},
    ):
        try:
            bpy.ops.export_scene.gltf(**common, **extra)
            return
        except TypeError:
            continue

    raise RuntimeError("glTF export failed with every supported option set.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate MiVic models.")
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

    for faction, profile in PROFILES.items():
        emit(f"{faction}_tank", lambda f=faction, p=profile: build_tank(f, p))
        emit(f"{faction}_artillery", lambda f=faction, p=profile: build_artillery(f, p))
        emit(f"{faction}_antiair", lambda f=faction, p=profile: build_antiair(f, p))

    # Aircraft: wingspan and sweep are the whole silhouette, so they are the only
    # numbers that differ per faction.
    emit("soviet_aircraft", lambda: build_aircraft("soviet", 12.0, 11.0, 0.25))
    emit("chinese_aircraft", lambda: build_aircraft("chinese", 9.5, 9.0, 0.05))
    emit("western_aircraft", lambda: build_aircraft("western", 13.5, 12.5, 0.45))

    # Συλλέκτης: it had no model at all and rendered as a bare box, in a game where
    # it is the unit an opponent raids.
    emit("soviet_harvester", lambda: build_harvester("soviet", 6.2, 2.9, 0.62))
    emit("chinese_harvester", lambda: build_harvester("chinese", 5.6, 2.5, 0.54))
    emit("western_harvester", lambda: build_harvester("western", 6.6, 3.2, 0.60))

    # Faction-unique roles: these had no model at all and rendered as a raw box.
    emit("soviet_katyusha", lambda: build_katyusha("soviet"))
    emit("soviet_electro", lambda: build_electro("soviet", PROFILES["soviet"]))

    # The Κινέζοι drone: a builder existed but nothing ever called it, so
    # `chinese_drone.glb` was a committed file no run of this generator could
    # reproduce. It is emitted here so the model in the game is the model in the
    # source, fixable by editing this file.
    emit("chinese_drone", lambda: build_drone("chinese"))

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
