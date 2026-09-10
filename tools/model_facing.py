"""Which way a .glb faces, on the loader's own terms.

`--inspect-models` reports a model *after* the loader's automatic alignment and
the catalogue's yaw offset, and its `nose` figure is the direction of the single
furthest vertex — which for a swept-wing aircraft is a wingtip rather than the
nose. This replays the loader's rules on the file as Blender exported it and
measures a *named* part instead, which is the thing that actually says which way
the model faces: a tank's `muzzle`, an aircraft's `Nose`, a drone's `Camera`, a
harvester's `Bucket`.

    python tools/model_facing.py <model.glb> [reference-part-regex]
    python tools/model_facing.py --table src/MiVic.Game/Data/ModelCatalog.cs <model root>
"""

import json
import math
import os
import re
import struct
import sys


def load(path):
    with open(path, "rb") as handle:
        data = handle.read()

    magic, _, _ = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67, "not a glb"

    offset = 12
    while offset < len(data):
        length, kind = struct.unpack_from("<II", data, offset)
        chunk = data[offset + 8:offset + 8 + length]
        if kind == 0x4E4F534A:
            return json.loads(chunk.decode("utf-8"))
        offset += 8 + length

    raise SystemExit("no JSON chunk")


def mat_identity():
    return [[1.0, 0, 0, 0], [0, 1.0, 0, 0], [0, 0, 1.0, 0], [0, 0, 0, 1.0]]


def mat_mul(a, b):
    """Row-vector convention: a point travels through `a` and then through `b`."""
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def mat_apply(m, p):
    x, y, z = p
    return (
        (x * m[0][0]) + (y * m[1][0]) + (z * m[2][0]) + m[3][0],
        (x * m[0][1]) + (y * m[1][1]) + (z * m[2][1]) + m[3][1],
        (x * m[0][2]) + (y * m[1][2]) + (z * m[2][2]) + m[3][2],
    )


def node_matrix(node):
    if "matrix" in node:
        m = node["matrix"]
        # glTF matrices are column-major, XNA's are row-major.
        return [[m[0], m[4], m[8], m[12]], [m[1], m[5], m[9], m[13]],
                [m[2], m[6], m[10], m[14]], [m[3], m[7], m[11], m[15]]]

    t = node.get("translation", [0.0, 0.0, 0.0])
    s = node.get("scale", [1.0, 1.0, 1.0])
    q = node.get("rotation", [0.0, 0.0, 0.0, 1.0])
    x, y, z, w = q

    rotation = [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0],
        [0, 0, 0, 1],
    ]
    scale = [[s[0], 0, 0, 0], [0, s[1], 0, 0], [0, 0, s[2], 0], [0, 0, 0, 1]]
    translate = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [t[0], t[1], t[2], 1]]
    return mat_mul(mat_mul(scale, rotation), translate)


def rotate_y(point, degrees):
    """The loader's own Y rotation, applied to a point (row-vector convention).

    `Matrix.CreateRotationY(t)` maps a direction at angle `a` — measured from +X
    towards +Z — to `a - t`, so a model facing -Z lands on +X after the loader's
    `-90` alignment turn.
    """
    radians = math.radians(degrees)
    c, s = math.cos(radians), math.sin(radians)
    x, y, z = point
    return ((x * c) + (z * s), y, (-x * s) + (z * c))


def walk(doc, index, parent, out):
    node = doc["nodes"][index]
    transform = mat_mul(node_matrix(node), parent)
    out.append((node.get("name", "?"), node.get("mesh"), transform))
    for child in node.get("children", []):
        walk(doc, child, transform, out)


def measure(path, reference):
    doc = load(path)
    scene = doc.get("scenes", [{}])[doc.get("scene", 0)]
    nodes = []
    for index in scene.get("nodes", range(len(doc.get("nodes", [])))):
        walk(doc, index, mat_identity(), nodes)

    lo = [1e9] * 3
    hi = [-1e9] * 3
    parts = []

    for name, mesh_index, transform in nodes:
        if mesh_index is None:
            continue

        for primitive in doc["meshes"][mesh_index].get("primitives", []):
            accessor = doc["accessors"][primitive["attributes"]["POSITION"]]
            corners = [
                (a, b, c)
                for a in (accessor["min"][0], accessor["max"][0])
                for b in (accessor["min"][1], accessor["max"][1])
                for c in (accessor["min"][2], accessor["max"][2])
            ]
            points = [mat_apply(transform, corner) for corner in corners]

            part_lo = [min(p[i] for p in points) for i in range(3)]
            part_hi = [max(p[i] for p in points) for i in range(3)]
            parts.append((name, part_lo, part_hi))

            for i in range(3):
                lo[i] = min(lo[i], part_lo[i])
                hi[i] = max(hi[i], part_hi[i])

    size = [hi[i] - lo[i] for i in range(3)]
    aligned = size[2] > size[0]

    found = None
    for name, part_lo, part_hi in parts:
        if reference.search(name):
            found = (name, [(part_lo[i] + part_hi[i]) * 0.5 for i in range(3)])
            break

    return size, aligned, found


def nose_bearing(middle, aligned, offset):
    point = middle
    if aligned:
        point = rotate_y(point, -90.0)
    point = rotate_y(point, offset)
    return math.degrees(math.atan2(point[2], point[0])), point


def detail(path, reference):
    size, aligned, found = measure(path, reference)

    print(f"{path}")
    print(f"  raw size x,y,z   {size[0]:7.2f} {size[1]:7.2f} {size[2]:7.2f}")
    print(f"  longest-horizontal-axis alignment fires: {aligned}"
          f"   (raw Z {size[2]:.2f} {'>' if aligned else '<='} raw X {size[0]:.2f})")

    if found is None:
        print("  no reference part matched")
        return

    name, middle = found
    print(f"  part {name:<14} raw centre x,z {middle[0]:7.2f} {middle[2]:7.2f}")

    for offset in (0.0, 90.0, 180.0, -90.0):
        bearing, point = nose_bearing(middle, aligned, offset)
        print(f"      offset {offset:6.1f}  ->  part at x,z {point[0]:7.2f} {point[2]:7.2f}"
              f"   bearing {bearing:7.1f} deg")


SPEC = re.compile(
    r"\[CacheKeyOf\(Faction\.(?P<faction>\w+), UnitKind\.(?P<kind>\w+)\)\] = "
    r"new\(\"(?P<folder>[^\"]+)\", \"(?P<file>[^\"]+)\", (?P<size>[\d.]+)f"
    r"(?:, (?P<offset>[A-Za-z0-9]+))?\)")

CONSTANT = re.compile(r"private const float (?P<name>\w+) = (?P<value>-?[\d.]+)f;")

REFERENCE = {
    "Tank": r"^muzzle$|^barrel$",
    "Artillery": r"^muzzle$|^barrel$",
    "AntiAir": r"^muzzle$|^barrel$",
    "ElectroPrototype": r"^emitter$|^muzzle$",
    "RocketArtillery": r"^Bonnet$|^Radiator$|^Bumper$",
    "Aircraft": r"^Nose$",
    "Drone": r"^Camera$",
    "Harvester": r"^Bucket$",
    "CommandCentre": r"^main_doorway$|^podium_doorway$",
    "Factory": r"^hall_doorway$|^hall_shutter$",
    "DesignBureau": r"^base_doorway$|^lab0_doorway$",
    "Infantry": r"^Visor$|^Peak$",
    "Commissar": r"^Visor$|^Peak$",
    "RobotInfantry": r"^Visor$|^Peak$",
    "Mercenary": r"^Visor$|^Peak$",
    "StealthRecon": r"^Visor$|^Peak$",
}


def table(catalog_path, root):
    source = open(catalog_path, encoding="utf-8-sig").read()
    constants = {m.group("name"): float(m.group("value")) for m in CONSTANT.finditer(source)}

    print(f"{'slot':<30} {'rawX':>6} {'rawZ':>6} {'align':<6} {'now':>6} {'nose':>7} {'right':>6}  verdict")
    print("-" * 100)

    for match in SPEC.finditer(source):
        faction = match.group("faction")
        kind = match.group("kind")
        name = match.group("offset")
        offset = constants.get(name, 0.0) if name else 0.0

        pattern = REFERENCE.get(kind)
        path = os.path.join(root, match.group("folder"), match.group("file"))

        if not os.path.exists(path):
            print(f"{faction + '/' + kind:<30} missing {path}")
            continue

        size, aligned, found = measure(path, re.compile(pattern or "nothing-matches"))

        # Every generated model is authored front-first along Blender's +Y, which
        # the exporter turns into -Z. The loader turns a model that is longer than
        # it is wide by -90 about Y, which lands that -Z front on +X — so the turn
        # the catalogue has to make up is the one that did *not* happen.
        right = -90.0 if not aligned else 0.0

        if found is None:
            print(f"{faction + '/' + kind:<30} {size[0]:6.2f} {size[2]:6.2f} {str(aligned):<6} "
                  f"{offset:6.1f} {'--':>7} {right:6.1f}  (no reference part)")
            continue

        bearing_now, _ = nose_bearing(found[1], aligned, offset)
        verdict = "ok" if abs(bearing_now) < 1.0 else f"WRONG by {bearing_now:+.0f} deg"

        print(f"{faction + '/' + kind:<30} {size[0]:6.2f} {size[2]:6.2f} {str(aligned):<6} "
              f"{offset:6.1f} {bearing_now:7.1f} {right:6.1f}  {verdict}")


def main():
    if sys.argv[1] == "--table":
        table(sys.argv[2], sys.argv[3])
        return

    reference = re.compile(sys.argv[2] if len(sys.argv) > 2 else "muzzle|Nose|Camera|Bucket")
    detail(sys.argv[1], reference)


main()
