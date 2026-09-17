"""Build a rigged, animatable human in the same metres and palette as the briefing figure.

Why this exists: the briefing figure is rigid parts, animated by per-part transforms in C#,
and that is the end of what it can do — no elbow bends, no authored clip, no walk. NoPasaranFC
already renders skinned glTF through MonoGame's SkinnedEffect and MiVic now ports it, so what
was missing was an asset, and an asset is a thing Blender can make here.

Two decisions worth stating.

*The armature is built from the figure's own constants.* HIP, SHOULDER, HEAD_BASE and the rest
are already the joint positions — the rigid version needed them to place its parts — so the
skeleton is those numbers rather than a second guess at where a human bends.

*The weights are computed here rather than solved by Blender.* Bone heat weighting is the
usual way and it fails on the geometry this project makes: limbs are separate primitives, and
the solver needs a manifold surface to diffuse heat through. Distance to the bone segment is
dull, deterministic, and good enough for a low-poly figure — and it cannot fail on a mesh it
does not like.

Run:  blender --background --python tools/blender/build_rigged.py -- --out <dir>
"""

import argparse
import math
import os
import sys

import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# The figure's own joint heights, in metres above the floor. Shared rather than repeated: a
# skeleton that disagrees with the body it drives is a body that tears.
HIP = 0.90
SHOULDER = 1.36
CHEST = 1.16
NECK = 1.44
HEAD_BASE = 1.506
KNEE = 0.47
ANKLE = 0.09
SHOULDER_X = 0.185
HIP_X = 0.085

FLESH = (0.80, 0.63, 0.47, 1.0)
TUNIC = (0.24, 0.25, 0.18, 1.0)


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def capsule(name, radius, start, end, colour, rings=6, segments=12):
    """A tube between two points, capped. Limbs are tubes; this is the whole of a limb."""
    a = mathutils.Vector(start)
    b = mathutils.Vector(end)
    direction = b - a
    length = direction.length
    if length < 1e-6:
        raise ValueError(f"{name}: zero-length limb")

    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)

    vertices, faces = [], []
    for ring in range(rings + 1):
        t = ring / rings
        centre = a + (direction * t)
        # Taper towards the far end so a limb is not a pipe.
        r = radius * (1.0 - (0.25 * t))
        for segment in range(segments):
            angle = 2.0 * math.pi * segment / segments
            vertices.append((
                centre.x + (r * math.cos(angle)),
                centre.y + (r * math.sin(angle)),
                centre.z,
            ))

    for ring in range(rings):
        for segment in range(segments):
            nxt = (segment + 1) % segments
            base = ring * segments
            faces.append((base + segment, base + nxt,
                          base + segments + nxt, base + segments + segment))

    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    paint(obj, colour)

    # Stand the limb along the line it was given rather than along Z.
    rotate_to(obj, a, b)
    return obj


def rotate_to(obj, a, b):
    direction = (mathutils.Vector(b) - mathutils.Vector(a))
    quat = mathutils.Vector((0.0, 0.0, 1.0)).rotation_difference(direction.normalized())
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = quat
    obj.location = mathutils.Vector(a)


def block(name, size, centre, colour):
    """A box. The torso and the head are boxes in this pass; the measured head replaces the
    box later, and a rig does not care which of the two it drives."""
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)

    x, y, z = (s * 0.5 for s in size)
    vertices = [(-x, -y, -z), (x, -y, -z), (x, y, -z), (-x, y, -z),
                (-x, -y, z), (x, -y, z), (x, y, z), (-x, y, z)]
    faces = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1),
             (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj.location = centre
    paint(obj, colour)
    return obj


def paint(obj, colour):
    mesh = obj.data
    if not mesh.vertex_colors:
        mesh.vertex_colors.new(name="Col")
    layer = mesh.vertex_colors["Col"]
    for loop in mesh.loops:
        layer.data[loop.index].color = colour


def build_body():
    """A man in the figure's own proportions, as one mesh of many primitives."""
    pieces = [
        block("Hips", (0.30, 0.20, 0.22), (0.0, 0.0, HIP - 0.02), TUNIC),
        block("Chest", (0.36, 0.23, 0.30), (0.0, 0.0, CHEST + 0.06), TUNIC),
        block("Neck", (0.10, 0.10, 0.10), (0.0, 0.0, NECK), FLESH),
        block("Head", (0.20, 0.23, 0.27), (0.0, 0.0, HEAD_BASE + 0.13), FLESH),
    ]

    for side, tag in ((-1.0, "Left"), (1.0, "Right")):
        pieces += [
            capsule(f"UpperArm{tag}", 0.055,
                    (side * SHOULDER_X, 0.0, SHOULDER),
                    (side * (SHOULDER_X + 0.05), 0.0, SHOULDER - 0.28), TUNIC),
            capsule(f"LowerArm{tag}", 0.048,
                    (side * (SHOULDER_X + 0.05), 0.0, SHOULDER - 0.28),
                    (side * (SHOULDER_X + 0.07), 0.04, SHOULDER - 0.54), TUNIC),
            capsule(f"Hand{tag}", 0.042,
                    (side * (SHOULDER_X + 0.07), 0.04, SHOULDER - 0.54),
                    (side * (SHOULDER_X + 0.08), 0.05, SHOULDER - 0.64), FLESH),
            capsule(f"UpperLeg{tag}", 0.070,
                    (side * HIP_X, 0.0, HIP - 0.06),
                    (side * HIP_X, 0.0, KNEE), TUNIC),
            capsule(f"LowerLeg{tag}", 0.058,
                    (side * HIP_X, 0.0, KNEE),
                    (side * HIP_X, 0.0, ANKLE), TUNIC),
            capsule(f"Foot{tag}", 0.048,
                    (side * HIP_X, 0.0, ANKLE),
                    (side * HIP_X, 0.16, ANKLE - 0.02), (0.10, 0.10, 0.10, 1.0)),
        ]

    return pieces


#: Bone name, head, tail, parent. The chain a human bends along, at the figure's own joints.
def skeleton():
    bones = [("Hips", (0, 0, HIP), (0, 0, CHEST), None),
             ("Spine", (0, 0, CHEST), (0, 0, NECK), "Hips"),
             ("Neck", (0, 0, NECK), (0, 0, HEAD_BASE + 0.06), "Spine"),
             ("Head", (0, 0, HEAD_BASE + 0.06), (0, 0, HEAD_BASE + 0.30), "Neck")]

    for side, tag in ((-1.0, "Left"), (1.0, "Right")):
        bones += [
            (f"UpperArm{tag}", (side * SHOULDER_X, 0, SHOULDER),
             (side * (SHOULDER_X + 0.05), 0, SHOULDER - 0.28), "Spine"),
            (f"LowerArm{tag}", (side * (SHOULDER_X + 0.05), 0, SHOULDER - 0.28),
             (side * (SHOULDER_X + 0.07), 0.04, SHOULDER - 0.54), f"UpperArm{tag}"),
            (f"Hand{tag}", (side * (SHOULDER_X + 0.07), 0.04, SHOULDER - 0.54),
             (side * (SHOULDER_X + 0.08), 0.05, SHOULDER - 0.64), f"LowerArm{tag}"),
            (f"UpperLeg{tag}", (side * HIP_X, 0, HIP - 0.06), (side * HIP_X, 0, KNEE), "Hips"),
            (f"LowerLeg{tag}", (side * HIP_X, 0, KNEE), (side * HIP_X, 0, ANKLE),
             f"UpperLeg{tag}"),
            (f"Foot{tag}", (side * HIP_X, 0, ANKLE), (side * HIP_X, 0.16, ANKLE - 0.02),
             f"LowerLeg{tag}"),
        ]

    return bones


def build_armature(bones):
    armature = bpy.data.armatures.new("Rig")
    rig = bpy.data.objects.new("Rig", armature)
    bpy.context.scene.collection.objects.link(rig)

    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for name, head, tail, parent in bones:
        bone = armature.edit_bones.new(name)
        bone.head = head
        bone.tail = tail
        if parent:
            bone.parent = armature.edit_bones[parent]
            # Connected where they meet, so a chain rotates as a chain.
            bone.use_connect = (mathutils.Vector(head)
                                - armature.edit_bones[parent].tail).length < 1e-6
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def distance_to_segment(point, a, b):
    ab = b - a
    length_squared = ab.dot(ab)
    if length_squared < 1e-12:
        return (point - a).length
    t = max(0.0, min(1.0, (point - a).dot(ab) / length_squared))
    return (point - (a + (ab * t))).length


def bind(body, rig, bones):
    """Weight every vertex to the nearest two bones, by distance to the bone segment.

    Two rather than one so that a shoulder or a knee blends across the joint instead of
    tearing; the falloff is the reciprocal of distance, which is crude and monotone and has
    no failure mode on geometry a heat solver would refuse.
    """
    groups = {}
    for name, _, _, _ in bones:
        groups[name] = body.vertex_groups.new(name=name)

    segments = [(name, mathutils.Vector(head), mathutils.Vector(tail))
                for name, head, tail, _ in bones]

    for vertex in body.data.vertices:
        world = body.matrix_world @ vertex.co
        scored = sorted(
            ((distance_to_segment(world, head, tail), name) for name, head, tail in segments),
            key=lambda pair: pair[0],
        )[:2]

        near, second = scored[0], scored[1]
        first_weight = 1.0 / max(near[0], 1e-4)
        second_weight = 1.0 / max(second[0], 1e-4)

        # Only blend when the two are actually close, or a vertex on the torso picks up a
        # fingertip at a third of its weight.
        if second[0] > near[0] * 1.6:
            second_weight = 0.0

        total = first_weight + second_weight
        groups[near[1]].add([vertex.index], first_weight / total, "REPLACE")
        if second_weight > 0.0:
            groups[second[1]].add([vertex.index], second_weight / total, "REPLACE")

    body.parent = rig
    modifier = body.modifiers.new(name="Armature", type="ARMATURE")
    modifier.object = rig


def key(rig, frame, pose):
    """One keyframe: bone name to (euler degrees, location offset)."""
    for name, (rotation, offset) in pose.items():
        bone = rig.pose.bones[name]
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = [math.radians(a) for a in rotation]
        bone.location = offset
        bone.keyframe_insert(data_path="rotation_euler", frame=frame)
        bone.keyframe_insert(data_path="location", frame=frame)


def animate(rig):
    """Two clips: a standing idle with a breath in it, and a seated pose.

    Authored as keyframes rather than computed, because the point of a rig is that a human
    can pose it — and a clip is the thing the engine now knows how to ask for by name.
    """
    scene = bpy.context.scene

    # Idle: three seconds of chest and head, looping on the first and last frame.
    scene.frame_start, scene.frame_end = 1, 90
    action = bpy.data.actions.new("Idle")
    rig.animation_data_create()
    rig.animation_data.action = action
    for frame, lean, turn in ((1, 0.0, 0.0), (45, 1.6, 2.5), (90, 0.0, 0.0)):
        key(rig, frame, {
            "Spine": ((lean, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "Head": ((-lean * 0.6, 0.0, turn), (0.0, 0.0, 0.0)),
            "UpperArmLeft": ((0.0, 0.0, -2.0 - lean), (0.0, 0.0, 0.0)),
            "UpperArmRight": ((0.0, 0.0, 2.0 + lean), (0.0, 0.0, 0.0)),
        })
    action.use_fake_user = True

    # Sit: hips down onto a chair, thighs forward, knees folded. A chair seat is about 45 cm,
    # so the hips drop half a metre and the whole chain follows.
    action = bpy.data.actions.new("Sit_Chair_Idle")
    rig.animation_data.action = action
    for frame in (1, 90):
        key(rig, frame, {
            "Hips": ((0.0, 0.0, 0.0), (0.0, 0.45, 0.0)),
            "UpperLegLeft": ((78.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "UpperLegRight": ((78.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "LowerLegLeft": ((-76.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "LowerLegRight": ((-76.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "FootLeft": ((10.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "FootRight": ((10.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
            "Spine": ((-6.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
        })
    action.use_fake_user = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])

    clear_scene()

    pieces = build_body()
    bpy.ops.object.select_all(action="DESELECT")
    for piece in pieces:
        piece.select_set(True)
    bpy.context.view_layer.objects.active = pieces[0]
    bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    body.name = "Body"
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    print(f"body: {len(body.data.vertices)} vertices, {len(body.data.polygons)} faces")

    bones = skeleton()
    rig = build_armature(bones)
    print(f"rig: {len(bones)} bones")

    bind(body, rig, bones)
    animate(rig)

    out = os.path.join(args.out, "rigged_elder.glb")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        export_skins=True,
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_apply=False,
    )
    print(f"wrote {os.path.basename(out)}  ({os.path.getsize(out) / 1024:.1f} KB)")


if __name__ == "__main__":
    main()
