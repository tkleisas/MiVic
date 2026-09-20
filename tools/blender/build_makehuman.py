"""Build the briefing figure from MakeHuman, using MPFB the way its own guide says.

The guide is `script_samples/` in the mpfb2 repository, and reading it would have saved
several rounds of guessing at signatures. What it says, and what this follows:

  * `HumanService.create_human()` returns the basemesh.
  * The body is asked for by setting **HumanObjectProperties** — gender, age, weight,
    muscle, height, proportions — and then calling
    **`TargetService.reapply_macro_details(basemesh)`**, which "calculates the required
    targets from the current macro info, loads any missing targets, and sets all target
    values". Nothing else does that. `load_target` on its own takes a `weight` that
    defaults to zero, and `bake_targets` bakes whatever happens to be in the stack, so
    both were no-ops: every earlier build produced the base mesh and nothing else.
  * `gender` is 1.0 for male and 0.0 for female.
  * **The rig goes on before the assets are added.** `add_mhclo_asset` looks for a
    skeleton amongst the basemesh's nearest relatives; finding one it runs
    `ClothesService.set_up_rigging`, which interpolates weights and adds the armature
    modifier, and finding none it does `clothes.parent = basemesh` and stops. Assets
    added before the rig are in the file as *unskinned* meshes, which a skinned renderer
    does not draw.
  * The visible body is a **proxy**, `male_generic.proxy`. `base.obj` is a low-poly
    stand-in that is never meant to be seen — it is what the targets morph and what the
    proxies fit to. It is removed before export; see below.
  * The skin is `HumanService.set_character_skin(mhmat, basemesh, bodyproxy=proxy)` with
    `skin_type="GAMEENGINE"`, which assigns the basemesh's skin material to the proxy.
    `_check_add_proxy` passes `material_type="NONE"` for the proxy on purpose: it is not
    meant to have a material of its own, it inherits the body's.
  * Eyes, teeth, eyebrows and clothes are `.mhclo` assets, found with
    `AssetService.find_asset_absolute_path` and applied with
    `HumanService.add_mhclo_asset(path, basemesh, asset_type=...)`.
  * The rig is `HumanService.add_builtin_rig(basemesh, rig_name)`; the names are the
    `rig.*.json` files under `data/rigs/standard`, so `game_engine` not `standard`.

The asset root is *not* the extension root. Code is `extensions/blender_org/mpfb`, the
asset pack is `.user/blender_org/mpfb/data`, and only the latter has `clothes/` in it.

Run:
    /home/tkleisas/blender/blender-5.2.2-linux-x64/blender --background \\
        --python tools/blender/build_makehuman.py -- --out <dir> \\
            [--garment male_casualsuit05 ...] [--skin old_caucasian_male]
"""

import argparse
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import makehuman_face  # noqa: E402
import makehuman_pipe  # noqa: E402
import makehuman_uniform  # noqa: E402
import mpfb_paint  # noqa: E402

#: The body: male, old, heavy, average muscle, stocky proportions.
BODY = {
    "gender": 1.0,        # 1.0 male, 0.0 female
    "age": 0.82,
    "weight": 0.72,
    "muscle": 0.42,
    "height": 0.42,
    "proportions": 0.42,  # 0.0 wide hips, 1.0 wide shoulders
}

#: The face, pushed towards the reference with detail targets after the macros have
#: run. The reference is a heavy, round, old face: full cheeks that have sagged into
#: jowls, a broad strong nose, a wide chin. Each entry is a `.target.gz` under the
#: targets root and a weight — `load_target` takes the weight as a keyword because it
#: defaults to zero, and a target loaded at zero is a target that was never loaded.
DETAIL_TARGETS = [
    ("head/head-age-incr.target.gz", 0.55),
    ("head/head-round.target.gz", 0.35),
    ("cheek/l-cheek-volume-incr.target.gz", 0.45),
    ("cheek/r-cheek-volume-incr.target.gz", 0.45),
    ("cheek/l-cheek-trans-down.target.gz", 0.30),
    ("cheek/r-cheek-trans-down.target.gz", 0.30),
    ("chin/chin-width-incr.target.gz", 0.25),
    ("nose/nose-width2-incr.target.gz", 0.30),
    ("nose/nose-hump-incr.target.gz", 0.20),
]

#: Where the bones point in a standing pose, as directions in Blender's armature space:
#: +X is the figure's left, +Z is up. The rest pose is a wide A-pose — the arms hang
#: about 48 degrees below horizontal, measured from the skeleton rather than estimated —
#: and at a briefing distance that reads as a bind pose, not as a man standing in a room.
#: So each bone is *aimed* at a direction instead of being rotated by guessed angles: the
#: rotation that takes a bone from where it points to where it should point is one
#: `rotation_difference`, and it cannot get a sign wrong.
STANDING = {
    "upperarm_l": (0.23, 0.03, -1.0),
    "upperarm_r": (-0.23, 0.03, -1.0),
    "lowerarm_l": (0.20, 0.20, -1.0),
    "lowerarm_r": (-0.20, 0.20, -1.0),
    "hand_l": (0.10, 0.12, -1.0),
    "hand_r": (-0.10, 0.12, -1.0),
    "thigh_l": (-0.06, 0.0, -1.0),
    "thigh_r": (0.06, 0.0, -1.0),
    "calf_l": (0.02, 0.0, -1.0),
    "calf_r": (-0.02, 0.0, -1.0),
}

#: What moves between the two ends of the idle loop, added to the directions above. Small
#: enough to read as breathing rather than as an action, which is all a briefing needs.
BREATHE = {
    "upperarm_l": (0.02, 0.0, 0.0),
    "upperarm_r": (-0.02, 0.0, 0.0),
    "spine_02": (0.0, -0.02, 0.0),
    "head": (0.0, -0.03, 0.0),
}

#: The pipe grasp: where the head and the free arm point while the pipe is at his mouth.
#: The *left* arm is not in here: a direction that looks right at the shoulder cannot put a
#: hand on a point, and aiming one at the mouth folded the forearm through the chest —
#: measured, the fingertips stood 69 mm from the pipe's grip with the sleeve laid across the
#: tunic. The left arm is solved instead; see LEFT_ARM below.
GRASP = {
    "upperarm_r": (-0.30, 0.02, -1.0),
    "head": (0.03, -0.06, 1.0),
    "spine_02": (0.015, -0.02, 1.0),
}

#: A small answer from the free arm while the hand is at the pipe — he is making a
#: point, not standing to attention with one arm raised.
PUFF = {
    "upperarm_r": (-0.30, -0.28, -0.85),
    "lowerarm_r": (-0.35, -0.10, -0.80),
}

#: The explaining gesture: the right forearm comes up to chest height, open hand,
#: as if laying out the front on an invisible map. GESTURE_B is the hand turned a
#: touch outward at the second beat of it — the difference between a hand held up
#: and a hand making a point.
GESTURE = {
    "upperarm_r": (-0.20, -0.45, -0.75),
    "lowerarm_r": (-0.15, -0.75, 0.25),
    "hand_r": (-0.05, -0.90, 0.20),
}

GESTURE_B = {
    "hand_r": (0.30, -0.85, -0.10),
    "lowerarm_r": (-0.10, -0.80, 0.15),
}

#: The pipe held out while he talks over it: the head straightens — he is addressing the
#: room now, not the pipe. The left arm is solved; see LEFT_ARM.
HELD = {
    "head": (0.04, -0.03, 1.0),
    "spine_02": (0.01, -0.01, 1.0),
}

#: Two beats of talk while the pipe is held: small turns of the head, as if weighing the
#: words, and small moves of the hand — see LEFT_ARM_TALK below for those.
TALK_A = {
    "head": (0.07, -0.02, 1.0),
}

TALK_B = {
    "head": (0.02, -0.06, 1.0),
}

#: <b>The left arm, solved rather than aimed.</b> Everything else in this file points a bone
#: at a direction, which is exact and cannot get a sign wrong — but a direction is not a
#: place. The hand has to close on the pipe, and the first attempt at that aimed the upper
#: arm, the forearm and the hand at three directions that each looked reasonable and put the
#: fingertips 69 mm from the grip with the sleeve lying across the chest: the arm had folded
#: *through* the body to get where it was pointing.
#:
#: So the left arm is a two-bone chain solved to a target: the wrist goes where the pipe is
#: and the elbow goes where an elbow goes — out and down, on the pole below. The path from
#: his side to his mouth is bowed away from the body, because the straight line between those
#: two points runs through his belly, and the bow is what makes the hand travel around him.
#:
#: All of it is metres, in armature space, measured off the rig this file builds.
LEFT_ARM = {
    # Where the pipe's grip is, from the mouth anchor the pipe bone rides: the bowl hangs
    # two centimetres forward of it and two and a half below.
    "grip_from_mouth": Vector((0.008, -0.020, -0.024)),
    # The hand's own direction when it is on the pipe: up, a little inward and forward.
    "grasp_hand": Vector((-0.22, -0.20, 0.95)),
    # The wrist when the pipe is held out to talk over: out from the shoulder, forward, a
    # hand's width below it, with the hand turned a little further up than at the mouth.
    "held_wrist": Vector((0.06, -0.26, -0.12)),
    "held_hand": Vector((-0.20, -0.45, 0.87)),
    # What the two talk beats add to the held position: two centimetres of hand, which is
    # the difference between holding a pipe and thinking with one.
    "talk_a": Vector((0.0, -0.02, 0.012)),
    "talk_b": Vector((0.012, -0.035, -0.012)),
    # Which way the elbow points. Down and out is where an elbow goes when a hand comes up
    # to a face; the pole is a direction from the shoulder, not a position.
    "pole": Vector((0.55, 0.30, -0.78)),
    # How far the hand's path bows away from the straight line to the pipe at the middle of
    # the lift — out to his left and forward, which is the way round his own chest.
    "lift": Vector((0.62, -0.72, 0.30)),
    "bow": 0.09,
}

#: The mouth anchor the pipe rides, as a name, so the solve reads where the pipe *is* on
#: the frame it is solving rather than where a constant thought it would be.
PIPE_MOUTH_BONE = "pipe_mouth"

#: One loop of the scene, 24 s at 24 fps, deliberately slow: stand, breathe, and the
#: hand comes up unhurried; the pipe leaves his mouth and he talks over it for a
#: while — two beats of the hand, the right hand joining for the point that matters —
#: then it goes back the way it came, and the loop closes where it opened. Each entry
#: is (frame, weights); the weights blend between the standing directions and the
#: gestures', so a bone never snaps. `held` also drives the pipe swap: above 0.5 the
#: mouth pipe scales away and the hand pipe scales in, both of them at the mouth while
#: the hand is on the bowl, so the swap happens where the two already coincide.
IDLE_KEYS = [
    (1, {}),
    (72, {"breathe": 1.0}),
    (144, {}),
    (192, {"grasp": 0.45}),
    (228, {"grasp": 0.85}),
    (252, {"grasp": 1.0}),
    (276, {"grasp": 1.0, "held": 1.0}),
    (312, {"held": 1.0, "holdpose": 1.0, "talka": 1.0}),
    (360, {"held": 1.0, "holdpose": 1.0, "talkb": 1.0}),
    (408, {"held": 1.0, "holdpose": 1.0, "talka": 0.6, "gesture": 0.9}),
    (432, {"held": 1.0, "holdpose": 1.0, "gesture": 1.0, "gestureb": 1.0}),
    (456, {"held": 1.0, "holdpose": 1.0}),
    (480, {"held": 1.0, "grasp": 1.0}),
    (504, {"grasp": 1.0}),
    (528, {}),
    (552, {"breathe": 1.0}),
    (576, {}),
]

IDLE_LAST_FRAME = 576

#: The pipe's two anchors, as bones: `pipe_mouth` rides the head, `pipe_held` is a
#: free bone the clip drives directly (a child of the hand would inherit the hand's
#: rotation as a constant tilt; driven, it is placed by measurement instead). The two
#: pipes share one geometry and swap visibility by scale keys — a pipe in the hand is
#: the same pipe, not a second prop.
PIPE_BONES = ("pipe_mouth", "pipe_held")

#: Every bone the loop poses. `head` and `spine_02` are here for the gesture; their
#: standing direction is the rig's own rest, captured at build time, so a weight of
#: zero is exactly the figure that shipped before the gesture existed.
POSE_BONES = tuple(dict.fromkeys(list(STANDING) + list(GRASP) + list(PUFF)
                                 + list(GESTURE) + list(GESTURE_B)
                                 + list(HELD) + list(TALK_A) + list(TALK_B)))

#: The body and its features. Subfolder under the asset root, filename, asset type.
#: eyebrow008 because the reference's brows are thick, straight and dark, and it is
#: the bushiest of the twelve the pack ships; 001 is a groomed line that reads as
#: drawn-on at briefing distance.
FEATURES = [
    ("proxymeshes/male_generic", "male_generic.proxy", "Proxymeshes"),
    ("eyes", "low-poly.mhclo", "Eyes"),
    ("eyebrows", "eyebrow008.mhclo", "Eyebrows"),
    ("teeth", "teeth_base.mhclo", "Teeth"),
]

#: A skin whose face is the right age for the part. The pack ships this one and the
#: name says what it is, which is why it was picked over the alternatives: an aged
#: caucasian male. It carries its own diffuse map, embedded in the glb on export.
SKIN = "old_caucasian_male"

RIG = "game_engine"

#: The skin map is multiplied by this before it is exported. The pack's skin is authored
#: for MakeHuman's own lighting — mean (228, 176, 142) — and under this project's ambient
#: plus directional light a face at that brightness arrives clipped: 254 on the forehead
#: and the cheek, which is a man with no shading left in his face rather than a man lit by
#: a window. The generated models are painted for this light and need no such correction.
SKIN_GAIN = 0.78

#: The longest side a bought texture is allowed to keep. MakeHuman's assets are authored
#: for rendering and a 23 MB glb for one figure is not a game asset: the images were 20.65
#: of 22.29 MB, and the geometry only 1.64. Cloth, hair and teeth all survive at 1024 —
#: this is a figure seen whole, three metres away — and the skin is exempt because a face
#: is the reason the figure exists at all.
TEXTURE_LIMIT = 1024

#: What each bought asset is repainted to, as linear RGB.
#:
#: The pack's clothes are modern — a zip field jacket over a shirt with denim jeans, and
#: ankle boots — and this figure is not. The geometry is what we came for and the colour
#: is not, so the colour is replaced and the cloth's own light and shade are kept. The
#: jacket is the closest thing in the pack to a tunic: hip length, a collar, and a closed
#: front with a vertical line down the chest, which under one colour stops reading as an
#: open jacket over a shirt and starts reading as a placket. Brown for the cloth — the
#: reference tunic is a warm khaki-brown, not the khaki of the first pass nor the grey of
#: the second — and near-black for the boots.
#:
#: The hair, brows and moustache are one dark mass — the reference's hair is dark
#: going grey only at the temples, and the strands' own white highlights survived a
#: merely-dark target at the default detail, reading as grey streaks and speckle.
#: So they are flattened (see DETAIL) to a dark brown-black, the same near-black the
#: moustache wears.
REPAINTED = {
    "male_casualsuit05": (0.360, 0.325, 0.255),
    "male_elegantsuit01": (0.360, 0.325, 0.255),
    "shoes03": (0.070, 0.062, 0.055),
    "shoes04": (0.070, 0.062, 0.055),
    "short01": (0.105, 0.095, 0.085),
    "short02": (0.105, 0.095, 0.085),
    "short04": (0.105, 0.095, 0.085),
    "grinsegold_moustache": (0.085, 0.078, 0.070),
    "eyebrow008": (0.075, 0.068, 0.060),
}


#: How much of the original light and shade each repainted asset keeps, where it
#: is not the default 0.30. The tunic gets 0.15: its texture is a dark jacket over
#: a pale shirt, and at 0.30 that survives as a dark suit and a white shirt — a
#: businessman. At 0.15 the cloth flattens into one colour and the garment reads as
#: what the reference wears: a uniform, buttoned to the collar. The hair, brows and
#: moustache are flattened for their own reason, named above.
DETAIL = {
    "male_casualsuit05": 0.15,
    "male_elegantsuit01": 0.15,
    "short01": 0.15,
    "grinsegold_moustache": 0.15,
    "eyebrow008": 0.20,
}


def enable_mpfb():
    import addon_utils

    addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)


def add_worn(AssetService, HumanService, human, subdir, name, asset_type, label):
    """Add one `.mhclo` from `subdir/<name>/` and repaint it if the table says so.

    Clothes and hair both live in a folder named after themselves, which is a convention
    of the asset pack and not of the API, so it is resolved here rather than assumed.
    """
    path = AssetService.find_asset_absolute_path(
        name + ".mhclo", asset_subdir=os.path.join(subdir, name))
    if path is None:
        print(f"  skipped {label} {name}: not in the asset pack")
        return None

    worn = HumanService.add_mhclo_asset(path, human, asset_type=asset_type)
    print(f"  {label}: {name}")

    target = REPAINTED.get(name)
    if target is None:
        return worn

    # One `mhclo` carries a whole outfit — `male_casualsuit05` is the jacket *and* the
    # trousers, in one mesh with one map — so a single recolour dresses both, in the same
    # cloth. That is what a period uniform was anyway: tunic and breeches in one khaki.
    image = mpfb_paint.material_image(worn)
    if image is None:
        print(f"    no single diffuse image on {name}; left as the pack painted it")
        return worn
    texels = mpfb_paint.recolour(image, target, detail=DETAIL.get(name, 0.30))
    print(f"    repainted {image.name} to "
          f"({target[0]:.2f}, {target[1]:.2f}, {target[2]:.2f}), {texels} texels")
    return worn


def shape_hairline(hair, proxy, temple=0.042, sigma=0.014, depth=0.009, band=0.025):
    """Give the hairline the reference's temple peaks.

    A short back-and-sides cut ships with a hairline that is one straight
    tangent across the forehead. The reference's is not: it steps forward at
    each temple, two curves meeting in a point — the double tangent a combed-back
    hairline makes. So the hair's edge verts are pulled down (and a touch
    forward) by a gaussian peaked at each temple, measured against the
    hairline the asset actually shipped with rather than a constant: the centre
    of the forehead keeps its z, the temples gain `depth` millimetres of point.
    Down is safe — the hair overlaps the forehead skin, so a lowered edge can
    never open a gap.
    """
    import math as _math

    # The forehead's front: the most forward skin in the band between the nose
    # and the hairline, on this body roughly the 1.50–1.62 m window. Measured
    # rather than assumed, but the window is this body's — a shorter figure
    # would want it re-measured.
    forehead_y = min(v.co.y for v in proxy.data.vertices
                     if abs(v.co.x) < 0.05 and 1.50 < v.co.z < 1.62)
    edge = [v.co.z for v in hair.data.vertices
            if abs(v.co.x) < 0.02 and v.co.y < forehead_y + 0.03]
    if not edge:
        print("    hairline: no hair found over the forehead; left straight")
        return 0
    line_z = min(edge)

    moved = 0
    for vertex in hair.data.vertices:
        if vertex.co.z > line_z + band or vertex.co.y > forehead_y + 0.04:
            continue
        bump = _math.exp(-((abs(vertex.co.x) - temple) / sigma) ** 2)
        if bump < 0.05:
            continue
        vertex.co.z -= depth * bump
        vertex.co.y -= depth * 0.4 * bump
        moved += 1
    return moved


def widen_moustache(obj, widen=1.15, droop=0.008, reach=0.055, cover=1.6):
    """Widen the moustache mesh, drop its ends and deepen it over the lip.

    The grinsegold moustache is a good chevron but a neat one; the reference's is
    wider than the mouth, its ends hang, and it hides the upper lip entirely —
    no skin shows between the nose and the mouth line. So the bottom edge is
    stretched down by `cover` from the mesh's own midline, as well as widened.
    Vertex weights are per index, so moving the vertices after rigging keeps the
    skinning — the mesh just covers more lip.
    """
    centre_z = sum(v.co.z for v in obj.data.vertices) / len(obj.data.vertices)
    moved = 0
    for vertex in obj.data.vertices:
        vertex.co.x *= widen
        vertex.co.z -= droop * min(1.0, abs(vertex.co.x) / reach) ** 2
        if vertex.co.z < centre_z:
            vertex.co.z = centre_z + (vertex.co.z - centre_z) * cover
        moved += 1
    return moved


def aim_bone(armature, name, direction):
    """Point a bone along `direction` (armature space), whatever the rest pose was.

    Assigning `pose_bone.matrix` rather than a rotation is what makes this work through a
    chain: Blender converts the armature-space matrix into the bone's local basis for us,
    so a bone aimed after its parent has already moved still ends up where it was asked
    to point. `view_layer.update()` between bones is what makes `head`/`tail` current.
    """
    pose_bone = armature.pose.bones.get(name)
    if pose_bone is None:
        return

    bpy.context.view_layer.update()
    head = pose_bone.head.copy()
    current = pose_bone.tail - pose_bone.head
    if current.length < 1e-6:
        return

    delta = current.normalized().rotation_difference(Vector(direction).normalized())
    matrix = delta.to_matrix().to_4x4() @ pose_bone.matrix
    matrix.translation = head
    pose_bone.matrix = matrix
    bpy.context.view_layer.update()


def solve_arm(armature, side, wrist_target, pole):
    """Put an arm's wrist on `wrist_target` with the elbow swinging towards `pole`.

    Two-bone inverse kinematics, in closed form: the elbow lies on a circle around the
    line from shoulder to wrist, and the pole says which point of that circle to take.
    Aiming the two bones at the two directions this returns is exact — the wrist lands
    on the target to floating-point — which is the difference between "the hand is
    near the pipe" and "the hand is on it".

    The angles are the law of cosines, so nothing here is tuned: the bone lengths are
    the rig's own and the target is a place.
    """
    upper = armature.pose.bones.get(f"upperarm_{side}")
    lower = armature.pose.bones.get(f"lowerarm_{side}")
    upper_bone = armature.data.bones.get(f"upperarm_{side}")
    lower_bone = armature.data.bones.get(f"lowerarm_{side}")

    if upper is None or lower is None or upper_bone is None or lower_bone is None:
        return False

    bpy.context.view_layer.update()
    shoulder = upper.head.copy()
    first = upper_bone.length
    second = lower_bone.length

    to_target = wrist_target - shoulder
    # A target at or past the arm's reach is not reachable: the elbow straightens, and
    # clamping here is what keeps the solve from asking for a negative cosine.
    reach = min(to_target.length, (first + second) * 0.999)

    if reach < 1e-5:
        return False

    forward = to_target.normalized()

    # The pole, flattened onto the plane the elbow swings in: only the part of it across
    # the shoulder-to-wrist line says anything about where the elbow goes.
    across = pole - shoulder
    across -= forward * across.dot(forward)

    if across.length < 1e-6:
        across = Vector((0.0, 0.0, -1.0)) - forward * forward.z
    if across.length < 1e-6:
        return False

    across.normalize()

    cosine = (first * first + reach * reach - second * second) / (2.0 * first * reach)
    angle = math.acos(max(-1.0, min(1.0, cosine)))
    elbow = shoulder + (forward * math.cos(angle) + across * math.sin(angle)) * first

    aim_bone(armature, f"upperarm_{side}", elbow - shoulder)
    aim_bone(armature, f"lowerarm_{side}", wrist_target - elbow)
    return True


def add_pipe_bones(armature, mouth):
    """Two extra bones for the pipe, so it can leave his mouth and come back.

    `pipe_mouth` is a child of `head` at the corner of the mouth: the pipe in his
    teeth rides the head, as it always has. `pipe_held` is a *root* bone at the same
    spot, driven by the clip directly — a child of the hand would inherit the hand's
    rotation as a constant tilt, and the difference between a pipe that turns with
    the wrist and a pipe that stays level is exactly the difference between a prop
    and a held thing. Both rest at the mouth, so the two pipes coincide on the frame
    the swap happens.
    """
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.mode_set(mode="EDIT")
    for name, parent_name in (("pipe_mouth", "head"), ("pipe_held", None)):
        bone = armature.data.edit_bones.new(name)
        # Along +Y: a bone whose rest direction is armature-Y has an identity rest
        # rotation, so the clip's rotation keys are armature-space rotations as read.
        bone.head = mouth.copy()
        bone.tail = mouth + Vector((0.0, 0.03, 0.0))
        if parent_name is not None:
            bone.parent = armature.data.edit_bones[parent_name]
    bpy.ops.object.mode_set(mode="OBJECT")


def author_idle(armature, fps=24, keys=IDLE_KEYS, last_frame=IDLE_LAST_FRAME):
    """Pose the figure through the briefing loop and keyframe it into `Idle`.

    A clip rather than a static pose, because the director asks for `Idle` by name and a
    figure with no clips falls back to the bind pose — which is the A-pose, and the A-pose
    is the thing being fixed. The loop is one standing pose, a breath, the pipe taken out
    and talked over, and back: the first and last keys are identical, so it loops without
    a seam. Each key aims every posed bone at a direction blended from the pose sets by
    the key's weights; a bone whose direction is not in any of them rests at its own rest
    direction, captured before anything moves it.

    The pipe bones are keyed too: `pipe_mouth` and `pipe_held` swap visibility by their
    scale (the swap sits inside the crossfade where both stand at the mouth), and
    `pipe_held`'s location and rotation are keyed from *measurement* in a second pass —
    the hand's position at each key is read off the pose the first pass just wrote, so
    the pipe sits in the palm rather than where a constant thought it would be.
    """
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    for pose_bone in armature.pose.bones:
        pose_bone.rotation_mode = "QUATERNION"

    rest = {}
    for name in POSE_BONES:
        bone = armature.data.bones.get(name)
        if bone is not None:
            rest[name] = (bone.tail_local - bone.head_local).normalized()

    armature.animation_data_create()
    action = bpy.data.actions.new("Idle")
    armature.animation_data.action = action

    blends = (("grasp", GRASP), ("puff", PUFF), ("holdpose", HELD),
              ("talka", TALK_A), ("talkb", TALK_B),
              ("gesture", GESTURE), ("gestureb", GESTURE_B))

    # The left arm's own measurements, taken once off the standing pose: where its wrist
    # hangs relative to its shoulder, and which way the hand points there. Everything the
    # solve does afterwards is a move from that, so the clip starts and ends on the figure
    # the aimed pass would have drawn.
    for name in POSE_BONES:
        base = STANDING.get(name, rest.get(name))
        if base is not None:
            aim_bone(armature, name, Vector(base))

    bpy.context.view_layer.update()
    l_shoulder = armature.pose.bones["upperarm_l"].head.copy()
    l_wrist_offset = armature.pose.bones["hand_l"].head.copy() - l_shoulder
    l_hand_dir = (armature.pose.bones["hand_l"].tail - armature.pose.bones["hand_l"].head).normalized()
    l_hand_length = armature.data.bones["hand_l"].length

    grip_from_mouth = LEFT_ARM["grip_from_mouth"]
    grasp_hand = LEFT_ARM["grasp_hand"].normalized()
    held_hand = LEFT_ARM["held_hand"].normalized()
    pole = LEFT_ARM["pole"].normalized()
    lift_dir = LEFT_ARM["lift"].normalized()

    def solve_left_arm(weights):
        """Place the left wrist for one key: stand, at the pipe, or held out to talk."""
        bpy.context.view_layer.update()
        shoulder = armature.pose.bones["upperarm_l"].head.copy()

        grasp = weights.get("grasp", 0.0)
        hold = weights.get("holdpose", 0.0)

        # The grip is asked of the pipe's own bone, so it goes wherever the head has
        # taken it on this frame: the head tips towards the pipe as he takes it.
        mouth = armature.pose.bones[PIPE_MOUTH_BONE]
        head_pose = armature.pose.bones["head"]
        head_delta = head_pose.matrix @ armature.data.bones["head"].matrix_local.inverted()
        grip = mouth.head + (head_delta.to_3x3() @ grip_from_mouth)

        hand_dir = l_hand_dir.lerp(grasp_hand, grasp).lerp(held_hand, hold).normalized()
        at_pipe = grip - hand_dir * l_hand_length
        held_wrist = shoulder + LEFT_ARM["held_wrist"] + LEFT_ARM["talk_a"] * weights.get("talka", 0.0) \
            + LEFT_ARM["talk_b"] * weights.get("talkb", 0.0)

        target = (shoulder + l_wrist_offset).lerp(at_pipe, grasp).lerp(held_wrist, hold)

        # The bow: the straight line from his side to his mouth is through his belly, so
        # the hand is pushed out and forward through the middle of the move and lands on
        # the line at either end. `sin` is zero at both stations and one between them.
        lift = max(grasp, hold)
        target += lift_dir * (LEFT_ARM["bow"] * math.sin(math.pi * min(1.0, lift)))

        solve_arm(armature, "l", target, shoulder + pole)
        aim_bone(armature, "hand_l", hand_dir)

    for frame, weights in keys:
        # frame_set FIRST: it re-evaluates the action and overwrites the pose with
        # the interpolation of the keys so far. Aiming after it poses on top of that;
        # aiming before it — the order this loop shipped with — keyed the reverted
        # pose, which is why three "breathe" keys once exported as three standings.
        bpy.context.scene.frame_set(frame)

        breathe = weights.get("breathe", 0.0)
        held = weights.get("held", 0.0)

        for name in POSE_BONES:
            base = STANDING.get(name, rest.get(name))
            if base is None:
                continue

            # The left arm is solved, not aimed — see solve_left_arm. Aiming it here and
            # solving it afterwards would be two answers to one question.
            if name in ("upperarm_l", "lowerarm_l", "hand_l"):
                continue

            target = Vector(base)
            if breathe:
                target += Vector(BREATHE.get(name, (0.0, 0.0, 0.0))) * breathe
            for weight_name, pose in blends:
                weight = weights.get(weight_name, 0.0)
                if weight and name in pose:
                    target = target.lerp(Vector(pose[name]), weight)
            aim_bone(armature, name, target)

        # After everything it hangs from has been posed, and before the key is written.
        solve_left_arm(weights)

        for pose_bone in armature.pose.bones:
            pose_bone.keyframe_insert("rotation_quaternion", frame=frame)

        # The visibility swap. Both pipes stand at the mouth through the crossfade's
        # middle, so the hand has the pipe in it on every frame the eye could catch.
        for name, scale in (("pipe_mouth", 1.0 if held < 0.5 else 0.0),
                            ("pipe_held", 0.0 if held < 0.5 else 1.0)):
            pose_bone = armature.pose.bones.get(name)
            if pose_bone is not None:
                pose_bone.scale = (scale, scale, scale)
                pose_bone.keyframe_insert("scale", frame=frame)

    # Second pass: the held pipe's own motion. Every key of it is measured off the
    # pose the first pass wrote — the palm when the pipe is the hand's, the mouth when
    # it is not, so a slow swap never shows the pipe travelling without the hand.
    held_bone = armature.pose.bones.get("pipe_held")
    mouth_bone = armature.pose.bones.get("pipe_mouth")
    if held_bone is not None and mouth_bone is not None:
        held_rest = armature.data.bones["pipe_held"].head_local.copy()
        for frame, weights in keys:
            bpy.context.scene.frame_set(frame)
            bpy.context.view_layer.update()

            if weights.get("held", 0.0) >= 0.5 and weights.get("grasp", 0.0) < 1.0:
                # Held out: the shank rests across the fingers, the bowl stands up
                # and a touch forward — a pipe being talked over, not one being
                # smoked. The mesh's bowl hangs forward-down of the bone at rest, so
                # -140° about X stands it up; the offset sets the grip into the palm
                # rather than at its root.
                anchor = (armature.matrix_world @ armature.pose.bones["hand_l"].head) \
                    + Vector((0.0, 0.015, 0.025))
                rotation = Matrix.Rotation(math.radians(-140.0), 4, "X").to_quaternion()
            else:
                anchor = armature.matrix_world @ mouth_bone.head
                rotation = Quaternion()

            held_bone.location = anchor - held_rest
            held_bone.rotation_quaternion = rotation
            held_bone.keyframe_insert("location", frame=frame)
            held_bone.keyframe_insert("rotation_quaternion", frame=frame)

    armature.animation_data.action = action
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = last_frame
    bpy.context.scene.render.fps = fps

    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    # Not `action.fcurves`: Blender 5 actions carry slots and layers, and the channel
    # count is no longer on the action. What was keyed is visible in the export anyway.
    print(f"posed: Idle on {len(armature.pose.bones)} bones, "
          f"{len(keys)} keys over frames 1-{last_frame} at {fps} fps")


def _diagnose(proxy):
    """Print what the face painter is about to measure against.

    Written because the first run of the painter picked a point at x = 0.4786, z = 0.9612
    as the most forward vertex of the head — off the midline and at hip height, which is a
    hand — and then painted into a 1024-square image that is not the skin. Both are
    questions about objects, so both are answered by printing the objects.
    """
    matrix = proxy.matrix_world
    raw = [v.co for v in proxy.data.vertices]
    world = [matrix @ co for co in raw]
    print(f"diagnose: object {proxy.name!r}, {len(raw)} verts")
    print(f"diagnose: matrix_world translation {tuple(round(c, 4) for c in matrix.translation)}, "
          f"scale {tuple(round(c, 4) for c in matrix.to_scale())}")
    print(f"diagnose: raw bbox z {min(c.z for c in raw):.4f}..{max(c.z for c in raw):.4f}")
    print(f"diagnose: world bbox z {min(c.z for c in world):.4f}..{max(c.z for c in world):.4f}, "
          f"y {min(c.y for c in world):.4f}..{max(c.y for c in world):.4f}, "
          f"x {min(c.x for c in world):.4f}..{max(c.x for c in world):.4f}")
    evaluated = proxy.evaluated_get(bpy.context.evaluated_depsgraph_get())
    if evaluated is not proxy:
        mesh = evaluated.to_mesh()
        posed = [evaluated.matrix_world @ v.co for v in mesh.vertices]
        print(f"diagnose: evaluated bbox z {min(c.z for c in posed):.4f}..{max(c.z for c in posed):.4f}, "
              f"y {min(c.y for c in posed):.4f}..{max(c.y for c in posed):.4f}")
        print(f"diagnose: evaluated most-forward vertex y {min(c.y for c in posed):.4f}")
        evaluated.to_mesh_clear()

    print("diagnose: images loaded:")
    for image in bpy.data.images:
        print(f"    {image.name!r} size={tuple(image.size)} channels={image.channels}")

    print("diagnose: meshes (outward fraction is the share of faces whose normal points "
          "away from the object's own centre: below 0.5 means the mesh is inside out)")
    for obj in sorted(bpy.context.scene.objects, key=lambda o: o.name):
        if obj.type != "MESH":
            continue
        mesh = obj.data
        centre = sum((v.co for v in mesh.vertices), Vector((0.0, 0.0, 0.0))) / len(mesh.vertices)
        outward = 0
        uv_min = [1e9, 1e9]
        uv_max = [-1e9, -1e9]
        uv_layer = mesh.uv_layers.active
        for polygon in mesh.polygons:
            to_face = (polygon.center - centre)
            if to_face.length > 1e-9 and polygon.normal.dot(to_face.normalized()) > 0.0:
                outward += 1
        if uv_layer is not None:
            for loop_uv in uv_layer.data:
                uv_min[0] = min(uv_min[0], loop_uv.uv[0])
                uv_min[1] = min(uv_min[1], loop_uv.uv[1])
                uv_max[0] = max(uv_max[0], loop_uv.uv[0])
                uv_max[1] = max(uv_max[1], loop_uv.uv[1])
        fraction = outward / float(len(mesh.polygons)) if len(mesh.polygons) else 0.0
        print(f"    {obj.name:26} faces={len(mesh.polygons):6} outward={fraction:5.2f} "
              f"uv=[{uv_min[0]:.3f},{uv_min[1]:.3f}]..[{uv_max[0]:.3f},{uv_max[1]:.3f}] "
              f"mats={','.join(s.material.name for s in obj.material_slots if s.material) or 'none'}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument(
        "--name", default="personality_elder_skinned",
        help="the .glb's name, which is the asset name a cutscene names")
    parser.add_argument("--skin", default=SKIN)
    parser.add_argument("--garment", action="append", default=[],
                        help="a folder under clothes/, repeatable")
    parser.add_argument(
        "--keep-standin", action="store_true",
        help="export the base stand-in as well; it is deleted by default")
    parser.add_argument(
        "--no-pose", action="store_true",
        help="leave the figure in the rest pose instead of authoring Idle")
    parser.add_argument(
        "--no-trim", action="store_true",
        help="keep every image the pack loaded, including the ones nothing reads")
    parser.add_argument(
        "--no-face", action="store_true",
        help="leave the skin map alone instead of painting the moustache into it")
    parser.add_argument(
        "--hair", action="append", default=[],
        help="a folder under hair/, repeatable")
    parser.add_argument(
        "--no-dress", action="store_true",
        help="leave the garments the colours the asset pack painted them")
    parser.add_argument(
        "--diagnose", action="store_true",
        help="print what the face painter measures before it paints")
    parser.add_argument(
        "--flatten-alpha", action="store_true",
        help="force every worn texture's alpha to 1 — the game draws these parts opaque, "
             "so this changes nothing on screen unless the alpha channel itself is the bug "
             "being hunted")
    parser.add_argument(
        "--no-pipe", action="store_true",
        help="do not add the pipe at the corner of the mouth")
    parser.add_argument(
        "--no-insignia", action="store_true",
        help="do not add the collar tabs and buttons to the tunic")
    args = parser.parse_args(argv)

    if args.no_dress:
        # The table is emptied rather than threaded through every call; there is one place
        # that reads it and one place that decides whether it applies.
        REPAINTED.clear()

    enable_mpfb()

    from bl_ext.blender_org.mpfb.entities.objectproperties import HumanObjectProperties
    from bl_ext.blender_org.mpfb.services.assetservice import AssetService
    from bl_ext.blender_org.mpfb.services.humanservice import HumanService
    from bl_ext.blender_org.mpfb.services.targetservice import TargetService

    bpy.ops.wm.read_factory_settings(use_empty=True)

    human = HumanService.create_human()
    if human is None:
        raise RuntimeError("create_human returned nothing")
    print(f"basemesh: {len(human.data.vertices)} vertices")

    for name, value in BODY.items():
        HumanObjectProperties.set_value(name, value, entity_reference=human)
    TargetService.reapply_macro_details(human)
    print("macro details reapplied: " + ", ".join(f"{k}={v}" for k, v in BODY.items()))

    # The detail targets shape the face the macros only sketch. They load onto the
    # basemesh now — before the rig and the assets — so everything fitted afterwards
    # follows the face they make. The targets root is the extension's own data, not
    # the asset pack's: it sits next to `services/`, not next to `clothes/`.
    import bl_ext.blender_org.mpfb as mpfb_ext
    targets_root = os.path.join(os.path.dirname(mpfb_ext.__file__), "data", "targets")
    applied = 0
    for rel, weight in DETAIL_TARGETS:
        path = os.path.join(targets_root, rel)
        if not os.path.exists(path):
            print(f"  skipped target {rel}: not under {targets_root}")
            continue
        TargetService.load_target(human, path, weight=weight)
        applied += 1
    print(f"detail targets: {applied} of {len(DETAIL_TARGETS)} applied")

    # The rig goes on **before** the assets, and that order is the whole story.
    # `add_mhclo_asset` looks for a skeleton amongst the basemesh's nearest relatives and,
    # finding one, runs `ClothesService.set_up_rigging`, which interpolates the weights
    # from the basemesh, loads any custom weights, and calls
    # `RigService.ensure_armature_modifier`. Finding none it does
    # `clothes.parent = basemesh` and stops — plain object parenting, no armature
    # modifier, no vertex groups. Added before the rig, the body proxy, eyes, eyebrows and
    # teeth were in the glb as *unskinned* meshes: five meshes written by the exporter,
    # five `MESH` objects in the scene, and only the base stand-in carrying JOINTS_0 and
    # WEIGHTS_0 — so the skinned renderer drew the stand-in's robe and nothing else.
    HumanService.add_builtin_rig(human, RIG)
    for arm in [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]:
        print(f"rig: {arm.name}, {len(arm.data.bones)} bones")

    #: Everything bought or fitted for this figure, for the texture trim at the end.
    worn = []
    proxy = None
    for subdir, filename, asset_type in FEATURES:
        path = AssetService.find_asset_absolute_path(filename, asset_subdir=subdir)
        if path is None:
            print(f"  skipped {asset_type}: {filename} not in the asset pack")
            continue
        created = HumanService.add_mhclo_asset(path, human, asset_type=asset_type)
        print(f"  added {asset_type}")
        if created is not None:
            worn.append(created)
            # Features repaint too: the brows are the same dark mass as the hair.
            stem = filename.split(".")[0]
            target = REPAINTED.get(stem)
            if target is not None:
                image = mpfb_paint.material_image(created)
                if image is not None:
                    texels = mpfb_paint.recolour(image, target, detail=DETAIL.get(stem, 0.30))
                    print(f"    repainted {image.name}, {texels} texels")
        if asset_type == "Proxymeshes":
            proxy = created

    # The skin is applied to the basemesh and inherited by the proxy. Without this the
    # proxy has no material at all — which is what `material_type="NONE"` means — and the
    # body renders as whatever flat colour the viewer falls back to.
    skin_path = AssetService.find_asset_absolute_path(
        args.skin + ".mhmat", asset_subdir=os.path.join("skins", args.skin))
    if skin_path is None:
        print(f"  no skin '{args.skin}' in the asset pack; body left untextured")
    else:
        HumanService.set_character_skin(skin_path, human, bodyproxy=proxy,
                                       skin_type="GAMEENGINE")
        print(f"  skin: {args.skin}")
        if not args.no_dress:
            skin_image = mpfb_paint.material_image(proxy)
            if skin_image is None:
                print("    the body's material names no single skin image; gain not applied")
            else:
                texels = mpfb_paint.scale(skin_image, SKIN_GAIN)
                print(f"    scaled {skin_image.name} by {SKIN_GAIN}, {texels} texels")

    for name in args.hair:
        added = add_worn(AssetService, HumanService, human, "hair", name, "Hair", "hair")
        if added is not None:
            worn.append(added)
            if proxy is not None:
                shaped = shape_hairline(added, proxy)
                print(f"    hairline: temple peaks on {shaped} verts")

    suit = None
    for name in args.garment:
        added = add_worn(AssetService, HumanService, human, "clothes", name, "clothes", "garment")
        if added is not None:
            worn.append(added)
            if "moustache" in name:
                moved = widen_moustache(added)
                print(f"    widened moustache: {moved} verts")
            if "suit" in name:
                suit = added

    # The stand-in goes last, after everything has been fitted to it. In Blender the proxy
    # is hidden *behind* the stand-in rather than replacing it: `_check_add_proxy` puts a
    # `MASK` modifier on the basemesh and the proxy is fitted a hair outside it. Exporting
    # with `export_apply=False` — which skinning requires, since the exporter must leave
    # the armature modifier alone — discards that mask, so the stand-in exported at full
    # strength and the first frame with a working proxy was still a mannequin in a robe.
    # `base.obj` is never meant to be seen; the proxy is the body. So it is removed.
    if not args.keep_standin:
        bpy.data.objects.remove(human, do_unlink=True)

    # The nose tip anchors the moustache paint, the pipe and its two bones, so it is
    # found before any of them. The head is found from the rig, not from a height
    # fraction: the rest pose has the hands further forward than the face, so "most
    # forward vertex of the whole body" is a knuckle. The head bone is at the base of
    # the skull and the nose is 12 cm in front of it, which is well inside a sphere
    # that the hands are outside of.
    nose = None
    nose_index = -1
    armature = next((o for o in bpy.context.scene.objects if o.type == "ARMATURE"), None)
    if proxy is not None and armature is not None:
        head_bone = armature.data.bones.get("head")
        if head_bone is None:
            print("  no 'head' bone on the rig; skipping the face, the pipe")
        else:
            anchor = proxy.matrix_world.inverted() @ armature.matrix_world @ head_bone.head_local
            nose, nose_index = makehuman_face.find_nose_tip(proxy, anchor)

    # The pipe's bones go on before the posing: the clip keys them, and a bone added
    # after the keys would hold its first key's value for the whole clip.
    if not args.no_pipe and nose is not None and armature is not None:
        mouth = nose + Vector((makehuman_pipe.MOUTH_OFF_MIDLINE,
                               makehuman_pipe.MOUTH_BEHIND_NOSE,
                               -makehuman_pipe.MOUTH_BELOW_NOSE))
        add_pipe_bones(armature, mouth)
        print(f"  pipe bones: pipe_mouth on head, pipe_held free, at "
              f"({mouth.x:.3f}, {mouth.y:.3f}, {mouth.z:.3f})")

    # Posed on the rig rather than on the meshes, so the bind pose stays what
    # the exporter needs and the pose travels as a clip.
    if not args.no_pose:
        armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        author_idle(armature)

    # The face is painted into the skin map, in the Blender session, before the export —
    # so the edited image is what the exporter embeds and the figure needs no sidecar.
    if proxy is not None and args.diagnose:
        _diagnose(proxy)

    # The face is painted into the skin map, in the Blender session, before the export —
    # so the edited image is what the exporter embeds and the figure needs no sidecar.
    if not args.no_face and nose is not None:
        box = makehuman_face.moustache_box(nose)
        image = mpfb_paint.material_image(proxy)
        if image is None:
            print("  the body's material names no single skin image; nothing to paint")
        else:
            texels = makehuman_face.paint_skin(proxy, image, box)
            print(f"  moustache: nose tip v{nose_index} at "
                  f"({nose.x:.4f}, {nose.y:.4f}, {nose.z:.4f}), "
                  f"lip z {box['lip_z']:.4f}, {texels} texels of {image.size[0]}x{image.size[1]}")
            shadowed = makehuman_face.paint_shadow(proxy, image, makehuman_face.scalp_box(nose))
            print(f"  scalp shadow: {shadowed} texels")

    # The pipe rides the head bone at the corner of the mouth — smoked, not held, so it
    # needs no animation of its own. Deliberately not in `worn`: its colours are
    # generated four-pixel images with no 'diffuse' in their names, and the trim below
    # would unlink them as unread.
    if not args.no_pipe and proxy is not None and nose is not None:
        armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        pipes = makehuman_pipe.build_pipe(armature, nose)
        print(f"  pipe: {len(pipes[0].data.vertices)} verts × 2 (mouth and held) "
              f"at the corner of the mouth")

        # The gesture is aimed by number, not by eye: where the fingertips stand at the
        # grasp's peak against the pipe's grip, and how close the arm comes to the body on
        # the way there. The first version of this pose read as a hand inside the chest,
        # and the numbers behind it were a 69 mm gap and a sleeve lying across the tunic —
        # so both are printed, and the second is the one that says "inside".
        #
        # The grip is read off the *posed* pipe, not off a rest-pose constant: the head
        # tips towards the pipe as he takes it, and the spine straightens by eighteen
        # degrees, which moves the mouth fifteen centimetres forward. A constant compared
        # against a moving pipe is a number that lies, and this one did — it read 161 mm
        # for a hand that was nine millimetres from the pipe.
        if not args.no_pose:
            hand = armature.pose.bones.get("hand_l")
            mouth_bone = armature.pose.bones.get(PIPE_MOUTH_BONE)

            bpy.context.scene.frame_set(252)
            bpy.context.view_layer.update()

            if hand is not None and mouth_bone is not None:
                tip = armature.matrix_world @ hand.tail
                head_pose = armature.pose.bones["head"]
                head_delta = head_pose.matrix @ armature.data.bones["head"].matrix_local.inverted()
                grip = armature.matrix_world @ (
                    mouth_bone.head + (head_delta.to_3x3() @ LEFT_ARM["grip_from_mouth"]))
                print(f"  gesture: fingertips {tuple(round(c, 3) for c in tip)}, "
                      f"grip {tuple(round(c, 3) for c in grip)}, "
                      f"gap {(tip - grip).length * 1000:.0f} mm")

            # The body he must not put his hand in, as a capsule round the spine: the
            # torso is about 0.13 m deep and 0.20 m wide where the arm passes it, and a
            # wrist inside 0.14 m of the spine axis between hip and chest is inside the man.
            spine = [armature.pose.bones.get(name) for name in ("spine_01", "spine_03")]
            worst = (1e9, 0)
            inside = 0

            for frame in range(1, IDLE_LAST_FRAME + 1):
                bpy.context.scene.frame_set(frame)
                bpy.context.view_layer.update()

                if hand is None or any(bone is None for bone in spine):
                    break

                base = armature.matrix_world @ spine[0].head
                top = armature.matrix_world @ spine[1].tail
                point = armature.matrix_world @ hand.head
                span = top - base
                along = max(0.0, min(1.0, (point - base).dot(span) / span.length_squared))
                gap = (point - (base + span * along)).length

                if gap < 0.14:
                    inside += 1
                if gap < worst[0]:
                    worst = (gap, frame)

            print(f"  gesture: wrist closest to the spine axis {worst[0] * 1000:.0f} mm "
                  f"at frame {worst[1]}, frames inside 140 mm: {inside} of {IDLE_LAST_FRAME}")
            bpy.context.scene.frame_set(1)

    # The tunic's insignia is measured off the suit the same way the pipe is measured
    # off the face. Also not in `worn`, for the same reason as the pipe.
    if not args.no_insignia and suit is not None:
        armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        makehuman_uniform.build_insignia(suit, armature)
    elif not args.no_insignia:
        print("  insignia: no suit among the garments; skipped")

    # The renderer reads base colour and nothing else, so nothing else is worth carrying.
    # The renderer draws these parts with BlendState.Opaque, so an alpha channel is
    # dead weight at best and a bug farm at worst: with one in the texture, the
    # hair, brows and moustache rendered their strands WHITE no matter what the RGB
    # held. Every worn texture's alpha is flattened to 1 — the strand shape then
    # comes from the card geometry alone, which is what the renderer was already
    # drawing. (`--flatten-alpha` remains as the off switch's ghost: it is now the
    # default, and the flag is accepted and ignored so old commands keep working.)
    if True:
        import numpy as _np
        flattened = 0
        for obj in worn + ([proxy] if proxy else []):
            for image in mpfb_paint.material_images(obj):
                if image.channels != 4:
                    continue
                buffer = _np.empty(image.size[0] * image.size[1] * 4, dtype=_np.float32)
                image.pixels.foreach_get(buffer)
                buffer[3::4] = 1.0
                image.pixels.foreach_set(buffer)
                image.update()
                flattened += 1
        print(f"  flattened alpha on {flattened} image(s)")

    if not args.no_trim:
        keep = {image for image in (mpfb_paint.material_image(o) for o in worn) if image}
        skin_image = mpfb_paint.material_image(proxy) if proxy is not None else None
        if skin_image is not None:
            keep.add(skin_image)

        dropped = mpfb_paint.drop_unread_images(worn + ([proxy] if proxy else []), keep)
        print(f"  trimmed: dropped {len(set(dropped))} unread image(s) "
              + ", ".join(sorted({i.name for i in dropped})) if dropped else "  trimmed: nothing to drop")

        shrunk = []
        for image in sorted(keep, key=lambda i: i.name):
            if image is skin_image:
                continue
            before = tuple(image.size)
            if mpfb_paint.limit_size(image, TEXTURE_LIMIT):
                shrunk.append(f"{image.name} {before[0]}x{before[1]}->{image.size[0]}x{image.size[1]}")
        if shrunk:
            print("  trimmed: " + "; ".join(shrunk))

    # What is actually in the scene at the moment of export. Every previous theory about
    # the missing body was formed without looking at this list, and the question it
    # settles is narrow and has two different fixes: either MPFB never made the proxy,
    # eyes, eyebrows and teeth into *meshes*, or it made them and the exporter drops them.
    print("scene before export:")
    for obj in sorted(bpy.context.scene.objects, key=lambda o: o.name):
        verts = len(obj.data.vertices) if obj.type == "MESH" else "-"
        parent = obj.parent.name if obj.parent else "-"
        mods = ",".join(f"{m.type}:{getattr(m, 'object', None).name if getattr(m, 'object', None) else '-'}"
                        for m in obj.modifiers) or "none"
        mats = ",".join(s.material.name for s in obj.material_slots if s.material) or "none"
        print(f"  {obj.type:9} {obj.name:26} verts={verts!s:8} parent={parent} "
              f"materials={mats} modifiers={mods}")

    os.makedirs(args.out, exist_ok=True)
    out = os.path.join(args.out, args.name + ".glb")
    # The whole scene, not a selection. With `use_selection=True` and a `select_all`, the
    # proxy, eyes, eyebrows and teeth were created and appeared in the file as four extra
    # *nodes* — and exported no meshes at all, so the figure kept the base mesh's robe and
    # the body proxy never reached a frame. Exporting everything removes the question of
    # what MPFB put where.
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=False,
        export_skins=True,
        export_animations=True,
        export_apply=False,
    )
    print(f"wrote {os.path.basename(out)}  ({os.path.getsize(out) / 1024:.1f} KB)")


if __name__ == "__main__":
    main()
