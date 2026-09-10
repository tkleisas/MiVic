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
        "turret_scale": (1.55, 0.62, 1.65),
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
        "turret_scale": (1.45, 0.75, 1.55),
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
        "turret_scale": (1.75, 0.58, 1.95),
        "barrel_length": 4.2,
        "barrel_radius": 0.095,
        "muzzle_brake": False,
        "wheels": 7,
        "wheel_radius": 0.38,
        "track_width": 0.58,
    },
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
    """A sloped-plate turret: the Δυτικοί wedge."""
    sx, sy, sz = scale
    verts = [
        (-sx, -sy, 0.0), (sx, -sy, 0.0), (sx, sy * 0.75, 0.0), (-sx, sy * 0.75, 0.0),
        (-sx * 0.62, -sy * 0.85, sz), (sx * 0.62, -sy * 0.85, sz),
        (sx * 0.55, sy * 0.55, sz * 0.55), (-sx * 0.55, sy * 0.55, sz * 0.55),
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


def paint(obj, rgba):
    """Bakes a flat colour into the mesh's vertex colours."""
    mesh = obj.data
    attribute = mesh.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")

    for entry in attribute.data:
        entry.color = rgba


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
        turret = dome("turret", 1.0, (sx, sy, sz * 2.0))
    elif profile["turret"] == "wedge":
        turret = wedge_turret("turret", (sx, sy, sz * 2.0))
    else:
        turret = box("turret", (sx * 2.0, sy * 2.0, sz * 1.7), offset=(0.0, 0.0, sz * 0.85))

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
    barrel.location = (0.0, sy * 0.5, sz * 1.0)
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
        paint(brake, (0.16, 0.16, 0.17, 1.0))

    paint(hull, (0.42, 0.45, 0.40, 1.0))
    paint(turret, (0.38, 0.41, 0.37, 1.0))
    paint(barrel, (0.30, 0.31, 0.30, 1.0))

    join(root, parts)

    for part in parts:
        if part.name.startswith(("track_", "wheel_")):
            paint(part, (0.15, 0.15, 0.16, 1.0))

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

    paint(base, (0.35, 0.37, 0.36, 1.0))
    paint(block, (0.40, 0.42, 0.41, 1.0))
    paint(dish, (0.55, 0.57, 0.56, 1.0))
    paint(mast, (0.30, 0.31, 0.30, 1.0))

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
    paint(base, (0.34, 0.36, 0.35, 1.0))

    if kind == "factory":
        # A long hall with a gantry over it.
        hall = box("turret", (width * 0.7, depth * 0.5, height * 0.5), offset=(0.0, 0.0, height * 1.2))
        parts.append(hall)
        paint(hall, (0.40, 0.42, 0.40, 1.0))

        gantry = box("barrel", (width * 0.9, 1.2, 0.8), offset=(0.0, 0.0, height * 1.7))
        parts.append(gantry)
        paint(gantry, (0.28, 0.29, 0.29, 1.0))

        # A crane that could travel along the gantry.
        crane = box("radar", (1.6, 1.6, 1.0), offset=(width * 0.3, 0.0, height * 1.9))
        parts.append(crane)
        paint(crane, (0.22, 0.23, 0.24, 1.0))

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
            paint(stack, (0.46, 0.46, 0.45, 1.0))

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
            paint(fan, (0.20, 0.21, 0.22, 1.0))

    elif kind == "nuclear":
        # A containment dome between two cooling towers: unmistakable from above,
        # which matters because it is the one structure worth raiding.
        dome_mesh = dome("turret", width * 0.42, (1.0, 1.0, 0.9), segments=14, rings=6)
        dome_mesh.location = (0.0, 0.0, height)
        parts.append(dome_mesh)
        paint(dome_mesh, (0.62, 0.63, 0.62, 1.0))

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
            paint(tower, (0.50, 0.50, 0.49, 1.0))

        vent = cylinder("radar", width * 0.2, 0.6, segments=12, axis="z", offset=(0.0, 0.0, height * 1.9))
        parts.append(vent)
        paint(vent, (0.24, 0.25, 0.26, 1.0))

    else:  # design bureau
        tower = box("turret", (width * 0.5, depth * 0.5, height * 1.1), offset=(0.0, 0.0, height * 1.55))
        parts.append(tower)
        paint(tower, (0.44, 0.46, 0.45, 1.0))

        dish = cylinder("radar", width * 0.22, 0.5, segments=14, axis="z", offset=(0.0, 0.0, height * 2.3))
        parts.append(dish)
        paint(dish, (0.58, 0.60, 0.60, 1.0))

        mast = cylinder("barrel", 0.3, height * 0.6, segments=8, axis="z", offset=(width * 0.18, 0.0, height * 2.5))
        parts.append(mast)
        paint(mast, (0.28, 0.29, 0.29, 1.0))

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
    paint(body, (0.40, 0.42, 0.38, 1.0))

    cab = box("turret", (2.4, 1.8, 1.4), offset=(0.0, -2.0, 2.7))
    parts.append(cab)
    paint(cab, (0.44, 0.46, 0.42, 1.0))

    # The rack, angled up. This is the whole silhouette.
    rack = box("barrel", (2.2, 0.6, 4.2), offset=(0.0, 0.6, 3.4))
    rack.rotation_euler = (math.radians(-28.0), 0.0, 0.0)
    parts.append(rack)
    paint(rack, (0.26, 0.27, 0.26, 1.0))

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
            paint(wheel, (0.15, 0.15, 0.16, 1.0))

    join(root, parts)
    return root


def build_figure(faction, kind, scale, palette):
    """A humanoid with a genuinely two-part leg rig.

    The borrowed soldier ships one mesh for both legs, so it can only march. These
    have `LegLeft` and `LegRight`, which the renderer can put out of phase with each
    other — an alternating stride, from parts and no skeleton.
    """
    root = bpy.data.objects.new(f"{faction}_{kind}", None)
    bpy.context.collection.objects.link(root)

    w, h, bulk = scale
    parts = []

    hip = h * 0.46
    leg_len = hip

    for side, tag in ((-1, "Left"), (1, "Right")):
        leg = box(
            f"Leg{tag}",
            (w * 0.32, w * 0.32, leg_len),
            offset=(0.0, 0.0, leg_len * 0.5),
        )
        leg.location = (side * w * 0.22, 0.0, hip)
        parts.append(leg)
        paint(leg, palette["legs"])

        foot = box(f"Foot{tag}", (w * 0.34, w * 0.55, leg_len * 0.12), offset=(0.0, w * 0.08, 0.0))
        foot.parent = leg
        parts.append(foot)
        paint(foot, palette["gear"])

    torso = box("Body", (w * 0.62, w * 0.44, h * 0.34), offset=(0.0, 0.0, h * 0.30))
    torso.location = (0.0, 0.0, hip)
    parts.append(torso)
    paint(torso, palette["body"])

    head = box("Head", (w * 0.34, w * 0.34, h * 0.15), offset=(0.0, 0.0, h * 0.08))
    head.location = (0.0, 0.0, hip + (h * 0.34))
    parts.append(head)
    paint(head, palette["gear"])

    if bulk > 1.0:
        # Heavier shoulders read as better armour at a glance.
        shoulders = box("Shoulders", (w * 0.86, w * 0.42, h * 0.10), offset=(0.0, 0.0, h * 0.28))
        shoulders.location = (0.0, 0.0, hip)
        parts.append(shoulders)
        paint(shoulders, palette["body"])

    join(root, parts)
    return root


def build_drone(faction):
    """Κινέζοι drone: a small body with a rotor that spins."""
    root = bpy.data.objects.new(f"{faction}_drone", None)
    bpy.context.collection.objects.link(root)

    parts = []

    body = box("Body", (0.7, 1.5, 0.45), offset=(0.0, 0.0, 0.0))
    parts.append(body)
    paint(body, (0.38, 0.40, 0.38, 1.0))

    for side in (-1, 1):
        arm = box(f"Arm{'L' if side < 0 else 'R'}", (0.12, 1.1, 0.12), offset=(0.0, 0.0, 0.0))
        arm.location = (side * 0.85, 0.0, 0.05)
        parts.append(arm)
        paint(arm, (0.24, 0.25, 0.25, 1.0))

        rotor = cylinder(f"radar_{'l' if side < 0 else 'r'}", 0.75, 0.06, segments=14, axis="z")
        rotor.location = (side * 0.85, 0.0, 0.18)
        parts.append(rotor)
        paint(rotor, (0.18, 0.19, 0.20, 1.0))

    nose = box("Head", (0.4, 0.5, 0.3), offset=(0.0, 0.9, -0.05))
    parts.append(nose)
    paint(nose, (0.55, 0.57, 0.56, 1.0))

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
    paint(body, (0.40, 0.43, 0.46, 1.0))

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
        paint(wing, (0.44, 0.47, 0.50, 1.0))

    tail = box("Tail", (wingspan * 0.34, length * 0.14, 0.18), offset=(0.0, 0.0, 0.0))
    tail.location = (0.0, -length * 0.44, 0.0)
    parts.append(tail)
    paint(tail, (0.44, 0.47, 0.50, 1.0))

    fin = box("Fin", (0.16, length * 0.16, 0.7), offset=(0.0, 0.0, 0.35))
    fin.location = (0.0, -length * 0.44, 0.0)
    parts.append(fin)
    paint(fin, (0.38, 0.41, 0.44, 1.0))

    nose = box("Nose", (0.8, length * 0.14, 0.7), offset=(0.0, length * 0.46, 0.0))
    parts.append(nose)
    paint(nose, (0.60, 0.62, 0.64, 1.0))

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

    # Faction-unique roles: these had no model at all and rendered as a raw box.
    emit("soviet_katyusha", lambda: build_katyusha("soviet"))
    emit("soviet_commissar", lambda: build_figure(
        "soviet", "commissar", (1.0, 1.85, 0.9),
        {"body": (0.30, 0.30, 0.32, 1.0), "legs": (0.24, 0.24, 0.26, 1.0), "gear": (0.52, 0.16, 0.14, 1.0)}))

    emit("chinese_robot", lambda: build_figure(
        "chinese", "robot", (0.95, 1.8, 0.8),
        {"body": (0.46, 0.47, 0.44, 1.0), "legs": (0.30, 0.31, 0.30, 1.0), "gear": (0.62, 0.52, 0.18, 1.0)}))
    emit("chinese_drone", lambda: build_drone("chinese"))

    emit("western_mercenary", lambda: build_figure(
        "western", "mercenary", (1.1, 1.9, 1.25),
        {"body": (0.32, 0.35, 0.40, 1.0), "legs": (0.26, 0.28, 0.32, 1.0), "gear": (0.55, 0.58, 0.62, 1.0)}))
    emit("western_stalker", lambda: build_figure(
        "western", "stalker", (0.9, 1.8, 0.95),
        {"body": (0.20, 0.22, 0.26, 1.0), "legs": (0.16, 0.18, 0.21, 1.0), "gear": (0.34, 0.38, 0.44, 1.0)}))

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
