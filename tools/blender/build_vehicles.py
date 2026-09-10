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
# --------------------------------------------------------------------------

PROFILES = {
    # Compact, low, sloped all round, few big road wheels, wide tracks. Light
    # hull on a broad footprint: the mud-mobile tank.
    "soviet": {
        "hull_length": 6.0,
        "hull_width": 3.2,
        "hull_height": 0.95,
        "glacis": 0.55,
        "turret": "dome",
        "turret_scale": (1.30, 1.45, 1.00),
        "barrel_length": 3.0,
        "barrel_radius": 0.115,
        "muzzle_brake": True,
        "wheels": 5,
        "wheel_radius": 0.42,
        "track_width": 0.62,
    },

    # Tall, narrow, slab-sided, few exposed road wheels, plain tube. Boxy and
    # utilitarian: cheap to build in enormous numbers.
    "chinese": {
        "hull_length": 5.6,
        "hull_width": 2.7,
        "hull_height": 1.15,
        "glacis": 0.20,
        "turret": "box",
        "turret_scale": (1.10, 1.30, 1.35),
        "barrel_length": 2.9,
        "barrel_radius": 0.10,
        "muzzle_brake": False,
        "wheels": 5,
        "wheel_radius": 0.36,
        "track_width": 0.40,
    },

    # Long, wide, high, many road wheels, wedge turret, long thin barrel. Large
    # and sophisticated — and the heaviest of the three.
    "western": {
        "hull_length": 7.4,
        "hull_width": 3.7,
        "hull_height": 1.30,
        "glacis": 0.75,
        "turret": "wedge",
        "turret_scale": (1.55, 1.85, 0.90),
        "barrel_length": 4.2,
        "barrel_radius": 0.095,
        "muzzle_brake": False,
        "wheels": 7,
        "wheel_radius": 0.38,
        "track_width": 0.58,
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
    "concrete": (0.56, 0.56, 0.54, 0.12),
    "coil":     (0.26, 0.60, 0.86, 0.00),
    "crate":    (0.46, 0.35, 0.21, 0.00),

    # People and machines.
    "uniform":  (0.30, 0.30, 0.32, 0.55),
    "flesh":    (0.72, 0.55, 0.42, 0.00),
    "robot":    (0.42, 0.45, 0.48, 0.70),
    "robot_leg": (0.28, 0.29, 0.31, 0.30),
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
# Roles
# --------------------------------------------------------------------------


def build_tank(faction, profile):
    root = bpy.data.objects.new(f"{faction}_tank", None)
    bpy.context.collection.objects.link(root)

    body = profile["hull_height"]
    parts = []

    hull = sloped_hull(
        "hull",
        profile["hull_length"],
        profile["hull_width"],
        body,
        profile["glacis"],
    )
    hull.location = (0.0, 0.0, profile["wheel_radius"] + (body * 0.5))
    parts.append(hull)

    # Tracks: wide and skirted for the Σοβιετικοί, narrow and exposed for the
    # Κινέζοι. The width is the single clearest silhouette cue from above.
    track_x = (profile["hull_width"] * 0.5) + (profile["track_width"] * 0.5)
    for side in (-1, 1):
        track = box(
            f"track_{'l' if side < 0 else 'r'}",
            (profile["track_width"], profile["hull_length"] * 0.98, profile["wheel_radius"] * 1.6),
            offset=(side * track_x, 0.0, profile["wheel_radius"] * 0.8),
        )
        parts.append(track)

    count = profile["wheels"]
    span = profile["hull_length"] * 0.72
    for side in (-1, 1):
        for i in range(count):
            t = 0.0 if count == 1 else (i / (count - 1)) - 0.5

            # The cylinder is built around its own origin and *placed* with the
            # object transform. Baking the offset into the vertices instead would
            # put the mesh origin at the middle of the tank, and a wheel cannot
            # spin about an axis that is not through its own hub.
            wheel = cylinder(
                f"wheel_{'l' if side < 0 else 'r'}{i + 1:02d}",
                profile["wheel_radius"],
                profile["track_width"] * 0.9,
                segments=10,
                axis="x",
            )
            wheel.location = (side * track_x, t * span, profile["wheel_radius"])
            parts.append(wheel)

    turret_z = profile["wheel_radius"] + body
    sx, sy, sz = profile["turret_scale"]

    # Every turret is built around its own origin — the ring centre — so rotating
    # it later turns it about the ring rather than about the middle of the hull.
    if profile["turret"] == "dome":
        turret = dome("turret", 1.0, (sx, sy, sz))
    elif profile["turret"] == "wedge":
        turret = wedge_turret("turret", (sx, sy, sz))
    else:
        turret = box("turret", (sx * 2.0, sy * 2.0, sz), offset=(0.0, 0.0, sz * 0.5))

    turret.location = (0.0, 0.0, turret_z)
    parts.append(turret)

    # The barrel is a child of the turret, so the gun follows the turret for free
    # and only has to add its own elevation. Its location is relative to the
    # turret's origin, because that is what parenting means in Blender.
    barrel = cylinder(
        "barrel",
        profile["barrel_radius"],
        profile["barrel_length"],
        segments=10,
        axis="y",
        offset=(0.0, profile["barrel_length"] * 0.5, 0.0),
    )
    barrel.parent = turret
    barrel.location = (0.0, sy * 0.85, sz * 0.55)
    parts.append(barrel)

    if profile["muzzle_brake"]:
        brake = cylinder(
            "muzzle",
            profile["barrel_radius"] * 1.8,
            profile["barrel_radius"] * 3.0,
            segments=10,
            axis="y",
        )
        brake.parent = barrel
        brake.location = (0.0, profile["barrel_length"], 0.0)
        parts.append(brake)
        paint(brake, MATERIALS["gun_dark"])

    paint(hull, MATERIALS["hull"])
    paint(turret, MATERIALS["turret"])
    paint(barrel, MATERIALS["gun"])

    for part in parts:
        if part.name.startswith(("track_", "wheel_")):
            paint(part, MATERIALS["rubber"] if part.name.startswith("track_") else MATERIALS["tyre"])

    add_tank_details(root, profile, parts, turret)

    join(root, parts)
    return root


def add_tank_details(root, profile, parts, turret):
    """Bolts the small parts onto a tank that make it a vehicle, not a shape.

    A tank is almost entirely flat plate, and flat plate seen from an RTS camera
    is one coloured rectangle. Headlights, stowage, exhausts and a turret hatch
    cost a few boxes each and are the difference between "that is a tank" and
    "that is a red wedge".
    """
    body = profile["hull_height"]
    length = profile["hull_length"]
    width = profile["hull_width"]
    radius = profile["wheel_radius"]
    track_x = (width * 0.5) + (profile["track_width"] * 0.5)

    # Side skirts hanging over the tracks. They widen the vehicle's read from
    # above, which is the view the game is actually played from.
    for side in (-1, 1):
        skirt = box(
            f"skirt_{'l' if side < 0 else 'r'}",
            (0.10, length * 0.62, body * 0.46),
            offset=(side * (track_x + (profile["track_width"] * 0.55)), 0.0, radius + (body * 0.62)),
        )
        parts.append(skirt)
        paint(skirt, MATERIALS["skirt"])

    # Headlights on the glacis: two bright points at the front, the cheapest
    # possible cue for which way a stationary vehicle is facing.
    for side in (-1, 1):
        lamp = box(
            f"lamp_{'l' if side < 0 else 'r'}",
            (0.20, 0.16, 0.20),
            offset=(side * width * 0.30, (length * 0.5) - (length * profile["glacis"] * 0.30), radius + (body * 0.80)),
        )
        parts.append(lamp)
        paint(lamp, MATERIALS["lamp"])

    # Stowage on the rear deck, in a colour nothing else on the model uses.
    crate = box(
        "stowage",
        (width * 0.44, length * 0.16, body * 0.40),
        offset=(0.0, -length * 0.33, radius + body + (body * 0.18)),
    )
    parts.append(crate)
    paint(crate, MATERIALS["crate"])

    # Exhausts either side of the engine deck.
    for side in (-1, 1):
        pipe = box(
            f"exhaust_{'l' if side < 0 else 'r'}",
            (0.34, length * 0.20, 0.28),
            offset=(side * width * 0.34, -length * 0.42, radius + (body * 0.85)),
        )
        parts.append(pipe)
        paint(pipe, MATERIALS["rust"])

    # A hatch on the turret roof. Parented to the turret, so it turns with it.
    # Every turret type is `sz` tall above its ring, so the roof is at a known
    # height; only a dome, being curved, needs the hatch on its crown.
    sx, sy, sz = profile["turret_scale"]
    hatch_y = 0.0 if profile["turret"] == "dome" else -sy * 0.45

    hatch = box("hatch", (0.56, 0.56, 0.14), offset=(0.0, 0.0, 0.0))
    hatch.parent = turret
    hatch.location = (0.0, hatch_y, sz + 0.02)
    parts.append(hatch)
    paint(hatch, MATERIALS["steel"])


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
            obj.rotation_euler = (math.radians(-8.0), 0.0, 0.0)

    turret = next((o for o in root.children if o.name == "turret"), None)

    if turret is not None:
        for i in range(3):
            ring = cylinder("coil", 0.5 + (i * 0.16), 0.22, segments=14, axis="y")
            ring.parent = turret
            ring.location = (0.0, 0.7 + (i * 0.55), 0.55)
            ring.rotation_euler = (math.radians(90.0), 0.0, 0.0)
            paint(ring, MATERIALS["coil"])

        emitter = cylinder("emitter", 0.34, 1.5, segments=12, axis="y")
        emitter.parent = turret
        emitter.location = (0.0, 1.6, 0.55)
        paint(emitter, MATERIALS["coil"])

    return root


def build_command_centre(faction, profile):
    """A headquarters: a blocky base, a tower, and a rotating radar dish."""
    root = bpy.data.objects.new(f"{faction}_hq", None)
    bpy.context.collection.objects.link(root)

    parts = []

    base = box("hull", (14.0, 14.0, 6.0), offset=(0.0, 0.0, 3.0))
    parts.append(base)

    block = box("turret", (7.0, 7.0, 5.0), offset=(0.0, 0.0, 8.5))
    parts.append(block)

    dish = cylinder("radar", 2.4, 0.5, segments=14, axis="z", offset=(0.0, 0.0, 12.4))
    parts.append(dish)

    mast = cylinder("barrel", 0.32, 4.0, segments=8, axis="z", offset=(0.0, 0.0, 10.9))
    parts.append(mast)

    paint(base, MATERIALS["concrete"])
    paint(block, MATERIALS["hull"])
    paint(dish, MATERIALS["panel"])
    paint(mast, MATERIALS["steel"])

    join(root, parts)
    return root


def build_structure(faction, kind, profile, scale):
    """A factory, power plant, nuclear plant or design bureau.

    One builder with a size and a couple of switches, because these differ in
    silhouette rather than in structure: a hall with a crane, a hall with stacks, a
    dome with cooling towers, a tower with a dish.
    """
    root = bpy.data.objects.new(f"{faction}_{kind}", None)
    bpy.context.collection.objects.link(root)

    width, depth, height = scale
    parts = []

    base = box("hull", (width, depth, height), offset=(0.0, 0.0, height * 0.5))
    parts.append(base)
    paint(base, MATERIALS["concrete"])

    # A painted band around the base, in the faction's colour: it is what tells a
    # player at a glance whose building they are looking at, since the concrete
    # itself is deliberately not tinted.
    stripe = box(
        "stripe",
        (width * 1.02, depth * 1.02, height * 0.14),
        offset=(0.0, 0.0, height * 0.22),
    )
    parts.append(stripe)
    paint(stripe, MATERIALS["hull"])

    if kind == "factory":
        # A long hall with a gantry over it.
        hall = box("turret", (width * 0.7, depth * 0.5, height * 0.5), offset=(0.0, 0.0, height * 1.2))
        parts.append(hall)
        paint(hall, MATERIALS["hull"])

        # A window band down each side of the hall: dark glass against light
        # concrete is what makes a big flat box read as a building.
        for side in (-1, 1):
            windows = box(
                f"windows_{'l' if side < 0 else 'r'}",
                (0.20, depth * 0.40, height * 0.16),
                offset=(side * width * 0.35, 0.0, height * 1.18),
            )
            parts.append(windows)
            paint(windows, MATERIALS["glass"])

        gantry = box("barrel", (width * 0.9, 1.2, 0.8), offset=(0.0, 0.0, height * 1.7))
        parts.append(gantry)
        paint(gantry, MATERIALS["steel"])

        # A crane that could travel along the gantry.
        crane = box("radar", (1.6, 1.6, 1.0), offset=(width * 0.3, 0.0, height * 1.9))
        parts.append(crane)
        paint(crane, MATERIALS["hazard"])

    elif kind == "power":
        for side in (-1, 1):
            stack = cylinder(
                f"stack_{'l' if side < 0 else 'r'}",
                1.6,
                height * 1.6,
                segments=12,
                axis="z",
                offset=(side * width * 0.3, 0.0, height * 1.3),
            )
            parts.append(stack)
            paint(stack, MATERIALS["panel"])

            # Cooling fans, which are what rotate.
            fan = cylinder(
                f"radar_{'l' if side < 0 else 'r'}",
                1.4,
                0.4,
                segments=14,
                axis="z",
                offset=(side * width * 0.3, 0.0, height * 2.1),
            )
            parts.append(fan)
            paint(fan, MATERIALS["gun_dark"])

    elif kind == "nuclear":
        # A containment dome between two cooling towers: unmistakable from above,
        # which matters because it is the one structure worth raiding.
        dome_mesh = dome("turret", width * 0.42, (1.0, 1.0, 0.9), segments=14, rings=6)
        dome_mesh.location = (0.0, 0.0, height)
        parts.append(dome_mesh)
        paint(dome_mesh, MATERIALS["panel"])

        for side in (-1, 1):
            tower = cylinder(
                f"stack_{'l' if side < 0 else 'r'}",
                width * 0.26,
                height * 2.2,
                segments=14,
                axis="z",
                offset=(side * width * 0.42, 0.0, height * 1.1),
            )
            parts.append(tower)
            paint(tower, MATERIALS["panel"])

        vent = cylinder("radar", width * 0.2, 0.6, segments=12, axis="z", offset=(0.0, 0.0, height * 1.9))
        parts.append(vent)
        paint(vent, MATERIALS["gun_dark"])

        # A hazard band around the containment building. Nothing else in the game
        # is striped, so a glance is enough to find the one structure that ends
        # the game if it goes up.
        ring = box(
            "hazard",
            (width * 0.80, depth * 0.80, height * 0.12),
            offset=(0.0, 0.0, height * 0.95),
        )
        parts.append(ring)
        paint(ring, MATERIALS["hazard"])

    else:  # design bureau
        tower = box("turret", (width * 0.5, depth * 0.5, height * 1.1), offset=(0.0, 0.0, height * 1.55))
        parts.append(tower)
        paint(tower, MATERIALS["hull"])

        dish = cylinder("radar", width * 0.22, 0.5, segments=14, axis="z", offset=(0.0, 0.0, height * 2.3))
        parts.append(dish)
        paint(dish, MATERIALS["panel"])

        mast = cylinder("barrel", 0.3, height * 0.6, segments=8, axis="z", offset=(width * 0.18, 0.0, height * 2.5))
        parts.append(mast)
        paint(mast, MATERIALS["steel"])

    stack_parts = [p for p in parts if p.name.startswith("stack_")]
    join(root, [p for p in parts if p not in stack_parts])
    for stack in stack_parts:
        stack.parent = root

    return root


def build_katyusha(faction):
    """Σοβιετικοί rocket artillery: a truck with a raised launcher rack.

    Wheeled rather than tracked, which is both true to the vehicle and a
    silhouette nobody else has.
    """
    root = bpy.data.objects.new(f"{faction}_katyusha", None)
    bpy.context.collection.objects.link(root)

    parts = []

    body = box("hull", (2.6, 6.4, 1.5), offset=(0.0, 0.0, 1.5))
    parts.append(body)
    paint(body, MATERIALS["hull"])

    # Named "Cab", not "turret": the renderer swings anything called a turret round
    # to face whatever the unit is shooting at, and a lorry whose cab rotates is
    # the sort of thing a player notices immediately.
    cab = box("Cab", (2.4, 1.8, 1.4), offset=(0.0, -2.0, 2.7))
    parts.append(cab)
    paint(cab, MATERIALS["deck"])

    # A windscreen. One dark panel turns the cab from a box into a lorry.
    screen = box("Screen", (2.0, 0.16, 0.75), offset=(0.0, -2.86, 3.05))
    parts.append(screen)
    paint(screen, MATERIALS["glass"])

    # The rack, angled up, and named "turret" so it traverses like the launcher it
    # is. This is the whole silhouette.
    rack = box("turret", (2.2, 0.6, 4.2), offset=(0.0, 0.6, 3.4))
    rack.rotation_euler = (math.radians(-28.0), 0.0, 0.0)
    parts.append(rack)
    paint(rack, MATERIALS["gun"])

    # Two rows of tubes on the rack face, which is what makes it a rocket launcher
    # rather than a crate on a lorry.
    for ix in (-1, 1):
        for iz in (-1, 1):
            tube = cylinder("Tube", 0.22, 3.4, segments=8, axis="z")
            tube.parent = rack
            tube.location = (ix * 0.55, -0.45, 1.5 + (iz * 0.5))
            parts.append(tube)
            paint(tube, MATERIALS["gun_dark"], variation=0.04)

    for side in (-1, 1):
        for i in range(3):
            wheel = cylinder(
                f"wheel_{'l' if side < 0 else 'r'}{i + 1:02d}",
                0.62,
                0.5,
                segments=10,
                axis="x",
            )
            wheel.location = (side * 1.4, -2.2 + (i * 2.2), 0.62)
            parts.append(wheel)
            paint(wheel, MATERIALS["tyre"])

    join(root, parts)
    return root


def build_harvester(faction, length, width, wheel_radius):
    """Συλλέκτης: a wheeled ore truck with a front loader and a hopper.

    It has no weapon and no place in a fight, so its silhouette has to say what it
    is for from across the map: a big open bucket at the front and an ore hopper at
    the back. Nothing else in the game has either.
    """
    root = bpy.data.objects.new(f"{faction}_harvester", None)
    bpy.context.collection.objects.link(root)

    parts = []

    # The chassis rides above the wheels, not through them.
    deck = wheel_radius + 0.55
    body = box("hull", (width, length, 1.2), offset=(0.0, 0.0, deck))
    parts.append(body)
    paint(body, MATERIALS["hull"])

    # The cab, forward and offset to one side like a real loader's. Named "Cab",
    # not "turret": a cab that swings round to aim at the enemy would be a very
    # strange ore truck.
    cab = box("Cab", (width * 0.52, length * 0.24, 1.5), offset=(-width * 0.22, length * 0.26, deck + 1.3))
    parts.append(cab)
    paint(cab, MATERIALS["deck"])

    screen = box("Screen", (width * 0.44, 0.14, 0.8), offset=(-width * 0.22, (length * 0.26) + (length * 0.12), deck + 1.45))
    parts.append(screen)
    paint(screen, MATERIALS["glass"])

    # A rotating beacon: the one part of a support vehicle that should move while
    # it works, and the reason a harvester is not mistaken for a parked lorry.
    beacon = cylinder("radar", 0.22, 0.26, segments=10, axis="z", offset=(-width * 0.22, length * 0.26, deck + 2.18))
    parts.append(beacon)
    paint(beacon, MATERIALS["hazard"])

    # The hopper, open-topped and full of ore.
    hopper = box("Hopper", (width * 0.92, length * 0.34, 1.0), offset=(0.0, -length * 0.26, deck + 1.0))
    parts.append(hopper)
    paint(hopper, MATERIALS["crate"], variation=0.11)

    # The bucket, tipped down at the front. This is the whole silhouette.
    bucket = box("Bucket", (width * 1.24, length * 0.26, 0.5), offset=(0.0, length * 0.48, wheel_radius * 0.9))
    bucket.rotation_euler = (math.radians(-24.0), 0.0, 0.0)
    parts.append(bucket)
    paint(bucket, MATERIALS["steel"], variation=0.05)

    lip = box("BucketLip", (width * 1.24, length * 0.07, 0.16), offset=(0.0, length * 0.13, -0.30))
    lip.parent = bucket
    parts.append(lip)
    paint(lip, MATERIALS["hazard"], variation=0.03)

    arms = box("Arms", (width * 0.30, length * 0.34, 0.28), offset=(0.0, length * 0.22, 0.28))
    arms.parent = bucket
    parts.append(arms)
    paint(arms, MATERIALS["gun_dark"])

    for side in (-1, 1):
        for i in range(3):
            wheel = cylinder(
                f"wheel_{'l' if side < 0 else 'r'}{i + 1:02d}",
                wheel_radius,
                width * 0.22,
                segments=10,
                axis="x",
            )
            wheel.location = (side * (width * 0.5), (i - 1) * length * 0.32, wheel_radius)
            parts.append(wheel)
            paint(wheel, MATERIALS["tyre"])

    join(root, parts)
    return root


def build_figure(faction, kind, scale, palette, kit=None):
    """A humanoid: two-part leg rig, swinging arms, helmet and a personal weapon.

    The part contract is what makes this readable at the size it is actually seen
    at. An RTS camera turns a soldier into a dozen pixels, and at a dozen pixels
    the things that survive are the silhouette of the shoulders, the shape of the
    helmet and the diagonal of a weapon held across the chest. Everything else —
    webbing, pockets, a face — is decoration for the model viewer.

    `LegLeft` and `LegRight` are separate parts so the renderer can put them out of
    phase with each other for an alternating stride, and `ArmLeft`/`ArmRight` swing
    against the opposite leg, which is what stops a walking figure looking like a
    box on castors.
    """
    root = bpy.data.objects.new(f"{faction}_{kind}", None)
    bpy.context.collection.objects.link(root)

    kit = kit or {}
    w, h, bulk = scale
    parts = []

    # Proportions, top to bottom: legs to the hip at 0.47 of the height, torso to
    # 0.78, head to 0.93, helmet to about 0.98. Everything below is expressed
    # against that, so a taller figure is a taller figure rather than a stretched
    # one.
    hip = h * 0.47
    leg_len = hip
    torso_mid = h * 0.155
    head_mid = h * 0.385

    for side, tag in ((-1, "Left"), (1, "Right")):
        leg = box(
            f"Leg{tag}",
            (w * 0.30 * bulk, w * 0.30 * bulk, leg_len),
            offset=(0.0, 0.0, leg_len * 0.5),
        )
        leg.location = (side * w * 0.20, 0.0, hip)
        parts.append(leg)
        paint(leg, palette["legs"])

        # The boot sticks out towards the front, which is the only thing that
        # says which way a standing figure is facing.
        foot = box(f"Foot{tag}", (w * 0.32, w * 0.52, leg_len * 0.13), offset=(0.0, w * 0.10, 0.0))
        foot.parent = leg
        parts.append(foot)
        paint(foot, palette["boots"])

    torso = box("Body", (w * 0.60, w * 0.42 * bulk, h * 0.31), offset=(0.0, 0.0, torso_mid))
    torso.location = (0.0, 0.0, hip)
    parts.append(torso)
    paint(torso, palette["body"])

    # A greatcoat or a flak vest: the widest thing on the figure, and therefore
    # the part of the silhouette a player recognises first.
    if kit.get("coat"):
        coat = box("Coat", (w * 0.74, w * 0.50, h * 0.34), offset=(0.0, 0.0, h * 0.02))
        coat.location = (0.0, 0.0, hip)
        parts.append(coat)
        paint(coat, palette["legs"], variation=0.05)

    if kit.get("armour"):
        # A chest plate, not an apron: high on the torso and thin enough to read
        # as a plate rather than as a second body.
        vest = box("Armour", (w * 0.58, w * 0.11, h * 0.17), offset=(0.0, w * 0.24, torso_mid + (h * 0.05)))
        vest.location = (0.0, 0.0, hip)
        parts.append(vest)
        paint(vest, MATERIALS["steel"], variation=0.04)

    for side, tag in ((-1, "Left"), (1, "Right")):
        arm = box(
            f"Arm{tag}",
            (w * 0.16 * bulk, w * 0.16 * bulk, h * 0.32),
            offset=(0.0, 0.0, -h * 0.16),
        )
        arm.location = (side * w * 0.46, 0.0, hip + (h * 0.29))
        parts.append(arm)
        paint(arm, palette["body"])

    # Head, helmet and weapon are sized in metres rather than as fractions of the
    # shoulder width. A human head is about 16 cm across whatever the figure is
    # wearing, and tying it to the width instead produced a Δυτικοί with a
    # half-metre peaked cap and a rifle like a railway sleeper.
    head_w, head_d, head_h = 0.16, 0.20, 0.22

    head = box("Head", (head_w, head_d, head_h), offset=(0.0, 0.0, head_h * 0.5))
    head.location = (0.0, 0.0, hip + (h * 0.31))
    parts.append(head)
    paint(head, palette["skin"])

    # The helmet. A dome is a Σοβιετικοί pot helmet, a brimmed cap is Κινέζοι
    # canvas, and a visored shell is Δυτικοί composite — three silhouettes from
    # one line of difference.
    helmet = kit.get("helmet", "dome")
    head_z = hip + (h * 0.31)

    if helmet == "cap":
        # A crown over the top half of the head, with a peak at its front edge.
        # A cap worn as a band around the middle of a head reads as a headband.
        brim = box("Helmet", (0.21, 0.23, 0.10), offset=(0.0, 0.0, 0.17))
        brim.location = (0.0, 0.0, head_z)
        parts.append(brim)
        paint(brim, palette["helmet"])

        peak = box("Peak", (0.18, 0.11, 0.02), offset=(0.0, 0.155, 0.13))
        peak.parent = brim
        parts.append(peak)
        paint(peak, MATERIALS["gun_dark"])
    else:
        shell = dome("Helmet", 1.0, (0.095, 0.115, 0.095), segments=12, rings=3)
        shell.location = (0.0, 0.0, head_z + 0.115)
        parts.append(shell)
        paint(shell, palette["helmet"])

    if helmet == "visor":
        visor = box("Visor", (0.15, 0.05, 0.055), offset=(0.0, 0.10, 0.13))
        visor.location = (0.0, 0.0, head_z)
        parts.append(visor)
        paint(visor, MATERIALS["glass"])
    elif helmet != "cap":
        # A shadowed face slit: two pixels of dark under the helmet rim.
        slit = box("Visor", (0.13, 0.04, 0.035), offset=(0.0, 0.095, 0.115))
        slit.location = (0.0, 0.0, head_z)
        parts.append(slit)
        paint(slit, MATERIALS["gun_dark"])

    if kit.get("pack"):
        pack = box("Pack", (w * 0.46, w * 0.24, h * 0.26), offset=(0.0, -w * 0.30, torso_mid))
        pack.location = (0.0, 0.0, hip)
        parts.append(pack)
        paint(pack, palette["pack"])

    # The weapon, held diagonally across the chest. Port arms is not a pose here
    # so much as a legibility device: the diagonal is the one line that survives
    # every camera distance the game is played at.
    weapon = kit.get("weapon", "rifle")
    length = {"carbine": 0.86, "rifle": 1.05, "launcher": 0.92}.get(weapon, 1.05)

    gun = box("Rifle", (0.055, 0.075, length), offset=(0.0, 0.0, 0.0))
    gun.location = (w * 0.04, w * 0.30, hip + (h * 0.20))
    gun.rotation_euler = (0.0, math.radians(-34.0), 0.0)
    parts.append(gun)
    paint(gun, MATERIALS["gun"], variation=0.04)

    if kit.get("bayonet"):
        blade = box("Bayonet", (0.02, 0.03, 0.30), offset=(0.0, 0.0, (length * 0.5) + 0.15))
        blade.parent = gun
        parts.append(blade)
        paint(blade, MATERIALS["panel"], variation=0.03)

    if weapon == "launcher":
        tube = cylinder("Launcher", 0.075, 0.55, segments=10, axis="z")
        tube.parent = gun
        tube.location = (0.0, 0.0, length * 0.30)
        parts.append(tube)
        paint(tube, MATERIALS["gun_dark"], variation=0.04)

    join(root, parts)
    return root


def build_drone(faction):
    """Κινέζοι drone: a small body with a rotor that spins."""
    root = bpy.data.objects.new(f"{faction}_drone", None)
    bpy.context.collection.objects.link(root)

    parts = []

    body = box("Body", (0.7, 1.5, 0.45), offset=(0.0, 0.0, 0.0))
    parts.append(body)
    paint(body, MATERIALS["robot"])

    for side in (-1, 1):
        arm = box(f"Arm{'L' if side < 0 else 'R'}", (0.12, 1.1, 0.12), offset=(0.0, 0.0, 0.0))
        arm.location = (side * 0.85, 0.0, 0.05)
        parts.append(arm)
        paint(arm, MATERIALS["gun_dark"])

        rotor = cylinder(f"radar_{'l' if side < 0 else 'r'}", 0.75, 0.06, segments=14, axis="z")
        rotor.location = (side * 0.85, 0.0, 0.18)
        parts.append(rotor)
        paint(rotor, MATERIALS["gun_dark"])

    nose = box("Head", (0.4, 0.5, 0.3), offset=(0.0, 0.9, -0.05))
    parts.append(nose)
    paint(nose, MATERIALS["glass"])

    join(root, parts)
    return root


def build_artillery(faction, profile):
    """Self-propelled gun: the tank hull with the turret replaced by a long gun."""
    root = build_tank(faction, profile)
    root.name = f"{faction}_artillery"

    for obj in list(root.children):
        if obj.name in ("barrel", "muzzle"):
            obj.scale.y = 1.7
            obj.rotation_euler = (math.radians(-12.0), 0.0, 0.0)

    return root


def build_antiair(faction, profile):
    """Anti-air: a tank hull with a fast twin mount instead of a single gun."""
    root = build_tank(faction, profile)
    root.name = f"{faction}_antiair"

    for obj in list(root.children):
        if obj.name == "barrel":
            obj.scale.y = 0.55
            obj.rotation_euler = (math.radians(-18.0), 0.0, 0.0)

            for offset in (-0.6, 0.6):
                tube = cylinder("barrel", 0.09, 1.8, segments=8, axis="y", offset=(0.0, 1.0, 0.0))
                tube.parent = obj
                tube.location = (offset, 0.0, 0.0)
                paint(tube, (0.30, 0.31, 0.30, 1.0))

    return root


def build_aircraft(faction, wingspan, length, swept):
    """Aircraft: fuselage along +Y, wings across X, swept back by `swept`."""
    root = bpy.data.objects.new(f"{faction}_aircraft", None)
    bpy.context.collection.objects.link(root)

    parts = []

    body = box("hull", (1.2, length, 1.0), offset=(0.0, 0.0, 0.0))
    parts.append(body)
    paint(body, MATERIALS["hull"])

    # The wings are one box each so the sweep is visible in plan view, which is how
    # an RTS camera sees an aircraft.
    for side in (-1, 1):
        wing = box(
            f"Wing{'L' if side < 0 else 'R'}",
            (wingspan * 0.5, length * 0.22, 0.22),
            offset=(0.0, -swept * length * 0.5, 0.0),
        )
        wing.location = (side * wingspan * 0.25, 0.0, -0.1)
        wing.rotation_euler = (0.0, 0.0, math.radians(-swept * 30.0 * side))
        parts.append(wing)
        paint(wing, MATERIALS["deck"])

        # A missile slung under each wing: two small shapes that read as "armed"
        # from above, where a wing on its own is just a stripe.
        store = box(
            f"Store{'L' if side < 0 else 'R'}",
            (0.26, length * 0.22, 0.26),
            offset=(0.0, -swept * length * 0.5, 0.0),
        )
        store.location = (side * wingspan * 0.30, 0.0, -0.34)
        parts.append(store)
        paint(store, MATERIALS["panel"])

    tail = box("Tail", (wingspan * 0.34, length * 0.14, 0.18), offset=(0.0, 0.0, 0.0))
    tail.location = (0.0, -length * 0.44, 0.0)
    parts.append(tail)
    paint(tail, MATERIALS["deck"])

    fin = box("Fin", (0.16, length * 0.16, 0.7), offset=(0.0, 0.0, 0.35))
    fin.location = (0.0, -length * 0.44, 0.0)
    parts.append(fin)
    paint(fin, MATERIALS["hull"])

    nose = box("Nose", (0.8, length * 0.14, 0.7), offset=(0.0, length * 0.46, 0.0))
    parts.append(nose)
    paint(nose, MATERIALS["glass"])

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
        emit(f"{faction}_hq", lambda f=faction, p=profile: build_command_centre(f, p))

        # The other structures used to reuse the headquarters mesh at a different
        # scale, so a factory looked exactly like an HQ.
        emit(f"{faction}_factory", lambda f=faction: build_structure(f, "factory", None, (18.0, 13.0, 7.0)))
        emit(f"{faction}_power", lambda f=faction: build_structure(f, "power", None, (11.0, 9.0, 6.0)))
        emit(f"{faction}_bureau", lambda f=faction: build_structure(f, "bureau", None, (11.0, 11.0, 8.0)))
        emit(f"{faction}_nuclear", lambda f=faction: build_structure(f, "nuclear", None, (16.0, 16.0, 9.0)))

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

    # Infantry, one model per faction.
    #
    # These used to be a borrowed soldier for all three factions at once — the same
    # mesh, recoloured, and downloaded by a script rather than committed with the
    # rest of the art. Three powers whose entire visual identity is supposed to be
    # their silhouette cannot share a soldier, and at an RTS camera distance the
    # differences below are the ones that survive: a greatcoat and a pot helmet, a
    # light tunic and a peaked cap, armour plate and a visor.
    emit("soviet_infantry", lambda: build_figure(
        "soviet", "infantry", (0.72, 1.84, 1.18),
        {"body": (0.34, 0.35, 0.30, 0.62),
         "legs": (0.24, 0.25, 0.22, 0.42),
         "boots": (0.13, 0.12, 0.11, 0.05),
         "skin": MATERIALS["flesh"],
         "helmet": (0.34, 0.36, 0.30, 0.16),
         "pack": MATERIALS["crate"]},
        {"coat": True, "helmet": "dome", "weapon": "rifle", "pack": True}))

    emit("chinese_infantry", lambda: build_figure(
        "chinese", "infantry", (0.58, 1.90, 0.92),
        {"body": (0.44, 0.45, 0.36, 0.55),
         "legs": (0.28, 0.29, 0.25, 0.34),
         "boots": (0.17, 0.15, 0.12, 0.05),
         "skin": MATERIALS["flesh"],
         "helmet": (0.48, 0.45, 0.30, 0.45),
         "pack": (0.38, 0.33, 0.22, 0.10)},
        {"helmet": "cap", "weapon": "carbine", "bayonet": True, "pack": True}))

    emit("western_infantry", lambda: build_figure(
        "western", "infantry", (0.80, 1.80, 1.34),
        {"body": (0.30, 0.33, 0.38, 0.55),
         "legs": (0.21, 0.23, 0.27, 0.34),
         "boots": (0.14, 0.14, 0.15, 0.05),
         "skin": MATERIALS["flesh"],
         "helmet": (0.25, 0.28, 0.31, 0.20),
         "pack": (0.26, 0.28, 0.30, 0.10)},
        {"armour": True, "helmet": "visor", "weapon": "rifle", "pack": True}))

    # Specialists, on the same rig with different kit.
    emit("soviet_commissar", lambda: build_figure(
        "soviet", "commissar", (1.0, 1.88, 1.0),
        {"body": (0.32, 0.32, 0.33, 0.62),
         "legs": (0.22, 0.22, 0.24, 0.45),
         "boots": (0.16, 0.13, 0.10, 0.05),
         "skin": MATERIALS["flesh"],
         "helmet": (0.50, 0.14, 0.12, 0.35),
         "pack": MATERIALS["crate"]},
        {"coat": True, "helmet": "cap", "weapon": "carbine"}))

    emit("chinese_robot", lambda: build_figure(
        "chinese", "robot", (0.95, 1.80, 1.05),
        {"body": MATERIALS["robot"],
         "legs": MATERIALS["robot_leg"],
         "boots": MATERIALS["steel"],
         "skin": MATERIALS["gun_dark"],
         "helmet": MATERIALS["coil"],
         "pack": MATERIALS["steel"]},
        {"armour": True, "helmet": "dome", "weapon": "launcher"}))
    emit("chinese_drone", lambda: build_drone("chinese"))

    emit("western_mercenary", lambda: build_figure(
        "western", "mercenary", (1.05, 1.92, 1.40),
        {"body": (0.32, 0.35, 0.40, 0.55),
         "legs": (0.25, 0.27, 0.31, 0.40),
         "boots": (0.18, 0.15, 0.12, 0.05),
         "skin": MATERIALS["flesh"],
         "helmet": (0.30, 0.33, 0.36, 0.22),
         "pack": (0.34, 0.30, 0.24, 0.10)},
        {"armour": True, "helmet": "visor", "weapon": "launcher", "pack": True}))
    emit("western_stalker", lambda: build_figure(
        "western", "stalker", (0.92, 1.80, 0.98),
        {"body": (0.17, 0.19, 0.23, 0.22),
         "legs": (0.13, 0.15, 0.18, 0.16),
         "boots": (0.10, 0.10, 0.12, 0.05),
         "skin": MATERIALS["gun_dark"],
         "helmet": (0.11, 0.13, 0.17, 0.10),
         "pack": (0.14, 0.16, 0.19, 0.08)},
        {"helmet": "visor", "weapon": "carbine"}))

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
