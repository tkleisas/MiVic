"""Rig the briefing figure: the measured man, given a skeleton and authored clips.

The first version of this built its own body out of boxes and tubes and looked like a
mannequin made of boxes, because that is what it was. The figure that already exists —
the head measured against the portrait, the uniform with its collar and boards, the painted
map — already looks like a man and has no skeleton. So this rigs *that*, and the boxes are
gone.

Two things make it tractable that were not obvious from outside:

  * The parts are **named**, and the names are the bones: ArmLeft, ForearmLeft, HandLeft,
    LegLeft, ShinLeft, Head. So each part is weighted to its bone by name. There is no
    distance heuristic to get wrong and no bone-heat solver to fail on geometry that is
    made of separate primitives rather than a manifold surface.
  * The parts are kept **separate** rather than joined. A rig does not need one mesh, and
    keeping them means the runtime still has named parts — which is what the face/cloth/
    metal texture assignment keys on, so the painted map survives the change of rig.

Bones are derived from the parts' own bounding boxes rather than from a second table of
numbers, so the skeleton cannot drift from the body it drives: the arm bone is where the arm
is, because it is measured from the arm.

Run:  blender --background --python tools/blender/build_rigged.py -- --out <dir>
"""

import argparse
import math
import os
import sys

import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build_personalities as figure  # noqa: E402

#: Which part drives which bone. The names are the contract the figure already keeps, so this
#: is a translation rather than a guess. Two-ended parts (Boot, Ear) are split by their own
#: position when there is one on each side.
BONE_PARTS = [
    ("Hips", "Tunic"),
    ("Spine", "Body"),
    ("Neck", "Neck"),
    ("Head", "Head"),
    ("UpperArmLeft", "ArmLeft"),
    ("UpperArmRight", "ArmRight"),
    ("LowerArmLeft", "ForearmLeft"),
    ("LowerArmRight", "ForearmRight"),
    ("HandLeft", "HandLeft"),
    ("HandRight", "HandRight"),
    ("UpperLegLeft", "LegLeft"),
    ("UpperLegRight", "LegRight"),
    ("LowerLegLeft", "ShinLeft"),
    ("LowerLegRight", "ShinRight"),
    ("FootLeft", "Boot"),
    ("FootRight", "Boot"),
]

#: Parent of each bone, so the chain rotates as a chain.
PARENTS = {
    "Spine": "Hips", "Neck": "Spine", "Head": "Neck",
    "UpperArmLeft": "Spine", "UpperArmRight": "Spine",
    "LowerArmLeft": "UpperArmLeft", "LowerArmRight": "UpperArmRight",
    "HandLeft": "LowerArmLeft", "HandRight": "LowerArmRight",
    "UpperLegLeft": "Hips", "UpperLegRight": "Hips",
    "LowerLegLeft": "UpperLegLeft", "LowerLegRight": "UpperLegRight",
    "FootLeft": "LowerLegLeft", "FootRight": "LowerLegRight",
}

#: How far from a joint a vertex blends towards the parent bone, as a fraction of the limb.
#: Rigid weights leave a hard crease at every joint; a little blending rounds it without
#: needing the parts welded together.
BLEND = 0.18


def parts_by_name():
    """Every mesh part, keyed by the name stem the figure gave it."""
    found = {}
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        stem = obj.name.split(".")[0]
        found.setdefault(stem, []).append(obj)
    return found


def world_bounds(obj):
    points = [obj.matrix_world @ mathutils.Vector(corner) for corner in obj.bound_box]
    lo = mathutils.Vector((min(p.x for p in points),
                           min(p.y for p in points),
                           min(p.z for p in points)))
    hi = mathutils.Vector((max(p.x for p in points),
                           max(p.y for p in points),
                           max(p.z for p in points)))
    return lo, hi


def pick(objects, side):
    """The one of a pair that is on `side` (-1 left, +1 right), by its own position."""
    if len(objects) == 1:
        return objects[0]

    def centre_x(obj):
        lo, hi = world_bounds(obj)
        return (lo.x + hi.x) * 0.5

    ordered = sorted(objects, key=centre_x)
    return ordered[0] if side < 0 else ordered[-1]


def bone_for(parts, bone, part_name):
    side = -1 if bone.endswith("Left") else (1 if bone.endswith("Right") else 0)
    if part_name not in parts:
        return None
    return pick(parts[part_name], side)


def build_skeleton(parts):
    """One bone per named part, measured from that part's own bounds.

    A limb bone runs from the top of its part to the bottom, which is where the joint is; the
    head bone runs the other way, because a head is above its joint. Nothing here is a
    constant, so the skeleton is the body.
    """
    bones = {}
    for bone, part_name in BONE_PARTS:
        obj = bone_for(parts, bone, part_name)
        if obj is None:
            raise RuntimeError(f"no part called {part_name!r} for bone {bone!r}")
        lo, hi = world_bounds(obj)
        centre_x = (lo.x + hi.x) * 0.5
        centre_y = (lo.y + hi.y) * 0.5
        if bone == "Head":
            bones[bone] = (mathutils.Vector((centre_x, centre_y, lo.z)),
                           mathutils.Vector((centre_x, centre_y, hi.z)), obj)
        else:
            bones[bone] = (mathutils.Vector((centre_x, centre_y, hi.z)),
                           mathutils.Vector((centre_x, centre_y, lo.z)), obj)
    return bones


def build_armature(bones):
    armature = bpy.data.armatures.new("Rig")
    rig = bpy.data.objects.new("Rig", armature)
    bpy.context.scene.collection.objects.link(rig)

    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")

    # Parents before children, so a parent always exists when a child asks for it.
    ordered = sorted(bones, key=lambda b: 0 if PARENTS.get(b) is None else 1)
    for _ in range(3):
        for name in ordered:
            if name in armature.edit_bones:
                continue
            parent = PARENTS.get(name)
            if parent is not None and parent not in armature.edit_bones:
                continue
            head, tail, _ = bones[name]
            bone = armature.edit_bones.new(name)
            bone.head = head
            bone.tail = tail
            if parent is not None:
                bone.parent = armature.edit_bones[parent]
                bone.use_connect = False  # limbs meet at a point, not at a shared tail
    bpy.ops.object.mode_set(mode="OBJECT")

    missing = [b for b in bones if b not in armature.bones]
    if missing:
        raise RuntimeError(f"bones never created: {missing}")
    return rig


def weight(parts, bones, rig):
    """Every part to exactly one bone.

    *Every* part, not just the limbs: the first version assigned only the names in
    BONE_PARTS and left the hair, the ears, the collar, the boards, the belt and the pipe
    with no armature at all, so most of the uniform stood still while the body moved.

    And rigidly, with no blend across the joint. A blend sounds like an improvement and it
    is for a welded mesh; on this figure the head is one dense surface of eleven thousand
    triangles weighted to the head bone, and blending its lower edge towards the neck pulled
    it into stacked slabs. Separate primitives want rigid weights; the joints are where the
    primitives already meet.
    """
    centres = {}
    for bone, (head, tail, _) in bones.items():
        centres[bone] = (head + tail) * 0.5

    by_name = {}
    for bone, part_name in BONE_PARTS:
        side = -1 if bone.endswith("Left") else (1 if bone.endswith("Right") else 0)
        obj = bone_for(parts, bone, part_name)
        if obj is not None:
            by_name[obj.name] = bone

    for obj in [o for o in bpy.context.scene.objects if o.type == "MESH"]:
        if obj.name in by_name:
            bone = by_name[obj.name]
        else:
            # Anything the table does not name — hair, ears, collar, boards, belt, the pipe
            # — goes to the bone it sits on, measured from its own centre.
            lo, hi = world_bounds(obj)
            centre = (lo + hi) * 0.5
            bone = min(centres, key=lambda b: (centre - centres[b]).length)

        group = obj.vertex_groups.new(name=bone)
        group.add([v.index for v in obj.data.vertices], 1.0, "REPLACE")

        obj.parent = rig
        obj.matrix_parent_inverse = rig.matrix_world.inverted()
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = rig


def key(rig, frame, pose):
    for name, (rotation, offset) in pose.items():
        bone = rig.pose.bones[name]
        bone.rotation_mode = "XYZ"
        bone.rotation_euler = [math.radians(a) for a in rotation]
        bone.location = offset
        bone.keyframe_insert(data_path="rotation_euler", frame=frame)
        bone.keyframe_insert(data_path="location", frame=frame)


def animate(rig):
    """A standing idle with a breath in it, and a seated pose. Authored, not computed."""
    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = 1, 90

    rig.animation_data_create()

    idle = bpy.data.actions.new("Idle")
    rig.animation_data.action = idle
    for frame, lean, turn in ((1, 0.0, 0.0), (45, 1.8, 2.6), (90, 0.0, 0.0)):
        key(rig, frame, {
            "Spine": ((lean, 0.0, 0.0), (0, 0, 0)),
            "Head": ((-lean * 0.6, 0.0, turn), (0, 0, 0)),
            "UpperArmLeft": ((0.0, 0.0, -2.0 - lean), (0, 0, 0)),
            "UpperArmRight": ((0.0, 0.0, 2.0 + lean), (0, 0, 0)),
        })
    idle.use_fake_user = True

    # Seated at a desk: hips down onto the chair, thighs forward, knees folded under.
    sit = bpy.data.actions.new("Sit_Chair_Idle")
    rig.animation_data.action = sit
    for frame in (1, 45, 90):
        breathe = 0.0 if frame == 45 else 0.0
        key(rig, frame, {
            "Hips": ((0.0, 0.0, 0.0), (0.0, 0.46, 0.0)),
            "UpperLegLeft": ((-80.0, 0.0, 0.0), (0, 0, 0)),
            "UpperLegRight": ((-80.0, 0.0, 0.0), (0, 0, 0)),
            "LowerLegLeft": ((72.0 + breathe, 0.0, 0.0), (0, 0, 0)),
            "LowerLegRight": ((72.0 + breathe, 0.0, 0.0), (0, 0, 0)),
            "FootLeft": ((-12.0, 0.0, 0.0), (0, 0, 0)),
            "FootRight": ((-12.0, 0.0, 0.0), (0, 0, 0)),
            "Spine": ((5.0, 0.0, 0.0), (0, 0, 0)),
            "Head": ((-4.0, 0.0, 0.0), (0, 0, 0)),
        })
    sit.use_fake_user = True


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    figure.clear_scene()
    figure.build_elder()

    parts = parts_by_name()
    print("parts: " + ", ".join(sorted(parts)))

    bones = build_skeleton(parts)
    rig = build_armature(bones)
    print(f"rig: {len(bones)} bones")

    weight(parts, bones, rig)
    animate(rig)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    os.makedirs(args.out, exist_ok=True)
    out = os.path.join(args.out, "personality_elder_rigged.glb")

    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    for mesh in meshes:
        mesh.select_set(True)
    bpy.context.view_layer.objects.active = rig

    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=True,
        export_skins=True,
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_apply=False,
    )
    print(f"wrote {os.path.basename(out)}  ({os.path.getsize(out) / 1024:.1f} KB)")


if __name__ == "__main__":
    main()
