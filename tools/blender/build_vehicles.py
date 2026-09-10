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
    """Parents each child to a part so the exporter writes a node hierarchy."""
    for child in children:
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
            wheel = cylinder(
                f"wheel_{'l' if side < 0 else 'r'}{i + 1:02d}",
                profile["wheel_radius"],
                profile["track_width"] * 0.9,
                segments=10,
                axis="x",
                offset=(side * track_x, t * span, profile["wheel_radius"]),
            )
            parts.append(wheel)

    turret_z = profile["wheel_radius"] + body
    sx, sy, sz = profile["turret_scale"]

    if profile["turret"] == "dome":
        turret = dome("turret", 1.0, (sx, sy, sz * 2.0))
        turret.location = (0.0, -0.25, turret_z)
    elif profile["turret"] == "wedge":
        turret = wedge_turret("turret", (sx, sy, sz * 2.0))
        turret.location = (0.0, -0.35, turret_z)
    else:
        turret = box("turret", (sx * 2.0, sy * 2.0, sz * 1.7), offset=(0.0, -0.3, turret_z + (sz * 0.85)))
        turret.location = (0.0, 0.0, 0.0)

    parts.append(turret)

    barrel_z = turret_z + (sz * 1.05)
    barrel = cylinder(
        "barrel",
        profile["barrel_radius"],
        profile["barrel_length"],
        segments=10,
        axis="y",
        offset=(0.0, profile["barrel_length"] * 0.5, 0.0),
    )
    barrel.location = (0.0, sy * 0.5, barrel_z)
    parts.append(barrel)

    if profile["muzzle_brake"]:
        brake = cylinder(
            "muzzle",
            profile["barrel_radius"] * 1.8,
            profile["barrel_radius"] * 3.0,
            segments=10,
            axis="y",
            offset=(0.0, profile["barrel_length"], 0.0),
        )
        brake.location = (0.0, sy * 0.5, barrel_z)
        brake.parent = barrel
        barrel.children  # noqa: B018 - keeps the reference explicit
        join(barrel, [brake])
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

    for faction, profile in PROFILES.items():
        clear_scene()
        build_tank(faction, profile)
        path = os.path.join(args.out, f"{faction}_tank.glb")
        export(path)
        written.append(path)

    # One building, to prove the same part contract carries over: a radar dish
    # that rotates is `radar`, exactly as a tank's turret is `turret`.
    for faction, profile in PROFILES.items():
        clear_scene()
        build_command_centre(faction, profile)
        path = os.path.join(args.out, f"{faction}_hq.glb")
        export(path)
        written.append(path)

    for path in written:
        size = os.path.getsize(path)
        print(f"wrote {os.path.basename(path)}  ({size / 1024:.1f} KB)")

    print(f"done: {len(written)} models")


if __name__ == "__main__":
    main()
