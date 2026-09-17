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
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import makehuman_face  # noqa: E402
import mpfb_paint  # noqa: E402

#: The man: male, old, heavy, average muscle, stocky proportions.
BODY = {
    "gender": 1.0,        # 1.0 male, 0.0 female
    "age": 0.82,
    "weight": 0.72,
    "muscle": 0.42,
    "height": 0.42,
    "proportions": 0.42,  # 0.0 wide hips, 1.0 wide shoulders
}

#: Where the bones point in a standing pose, as directions in Blender's armature space:
#: +X is the figure's left, +Z is up. The rest pose is a wide A-pose — the arms hang
#: about 48 degrees below horizontal, measured from the skeleton rather than estimated —
#: and at a briefing distance that reads as a bind pose, not as a man standing in a room.
#: So each bone is *aimed* at a direction instead of being rotated by guessed angles: the
#: rotation that takes a bone from where it points to where it should point is one
#: `rotation_difference`, and it cannot get a sign wrong.
STANDING = {
    "upperarm_l": (0.17, 0.0, -1.0),
    "upperarm_r": (-0.17, 0.0, -1.0),
    "lowerarm_l": (0.13, 0.10, -1.0),
    "lowerarm_r": (-0.13, 0.10, -1.0),
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

#: The body and its features. Subfolder under the asset root, filename, asset type.
FEATURES = [
    ("proxymeshes/male_generic", "male_generic.proxy", "Proxymeshes"),
    ("eyes", "low-poly.mhclo", "Eyes"),
    ("eyebrows", "eyebrow001.mhclo", "Eyebrows"),
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

#: What each bought asset is repainted to, as linear RGB.
#:
#: The pack's clothes are modern — a zip field jacket over a shirt with denim jeans, and
#: ankle boots — and this figure is not. The geometry is what we came for and the colour
#: is not, so the colour is replaced and the cloth's own light and shade are kept. The
#: jacket is the closest thing in the pack to a tunic: hip length, a collar, and a closed
#: front with a vertical line down the chest, which under one colour stops reading as an
#: open jacket over a shirt and starts reading as a placket. Khaki for the cloth,
#: near-black for the boots.
#:
#: The hair is here for the same reason and one more: the pack's ten hair assets are all
#: dark, and an old man's hair is not. Recolouring keeps the strands' own light and shade,
#: which is what makes it read as hair rather than as a helmet.
REPAINTED = {
    "male_casualsuit05": (0.430, 0.400, 0.275),
    "male_elegantsuit01": (0.430, 0.400, 0.275),
    "shoes03": (0.070, 0.062, 0.055),
    "shoes04": (0.070, 0.062, 0.055),
    "short02": (0.520, 0.500, 0.470),
    "short04": (0.520, 0.500, 0.470),
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
    texels = mpfb_paint.recolour(image, target)
    print(f"    repainted {image.name} to "
          f"({target[0]:.2f}, {target[1]:.2f}, {target[2]:.2f}), {texels} texels")
    return worn


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


def author_idle(armature, fps=24, last_frame=72):
    """Pose the figure standing and keyframe it into an `Idle` action.

    A clip rather than a static pose, because the director asks for `Idle` by name and a
    figure with no clips falls back to the bind pose — which is the A-pose, and the A-pose
    is the thing being fixed. Three keys: the standing pose, a breath, and the standing
    pose again, so it loops without a seam.
    """
    bpy.context.view_layer.objects.active = armature
    armature.select_set(True)
    for pose_bone in armature.pose.bones:
        pose_bone.rotation_mode = "QUATERNION"

    armature.animation_data_create()
    action = bpy.data.actions.new("Idle")
    armature.animation_data.action = action

    middle = last_frame // 2
    for frame in (1, middle, last_frame):
        breathing = frame == middle
        for name, direction in STANDING.items():
            target = Vector(direction)
            if breathing:
                target += Vector(BREATHE.get(name, (0.0, 0.0, 0.0)))
            aim_bone(armature, name, target)

        bpy.context.scene.frame_set(frame)
        for pose_bone in armature.pose.bones:
            pose_bone.keyframe_insert("rotation_quaternion", frame=frame)

    armature.animation_data.action = action
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = last_frame
    bpy.context.scene.render.fps = fps
    # Not `action.fcurves`: Blender 5 actions carry slots and layers, and the channel
    # count is no longer on the action. What was keyed is visible in the export anyway.
    print(f"posed: Idle on {len(armature.pose.bones)} bones, frames 1-{last_frame} at {fps} fps")


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

    proxy = None
    for subdir, filename, asset_type in FEATURES:
        path = AssetService.find_asset_absolute_path(filename, asset_subdir=subdir)
        if path is None:
            print(f"  skipped {asset_type}: {filename} not in the asset pack")
            continue
        created = HumanService.add_mhclo_asset(path, human, asset_type=asset_type)
        print(f"  added {asset_type}")
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
        add_worn(AssetService, HumanService, human, "hair", name, "Hair", "hair")

    for name in args.garment:
        add_worn(AssetService, HumanService, human, "clothes", name, "clothes", "garment")

    # The stand-in goes last, after everything has been fitted to it. In Blender the proxy
    # is hidden *behind* the stand-in rather than replacing it: `_check_add_proxy` puts a
    # `MASK` modifier on the basemesh and the proxy is fitted a hair outside it. Exporting
    # with `export_apply=False` — which skinning requires, since the exporter must leave
    # the armature modifier alone — discards that mask, so the stand-in exported at full
    # strength and the first frame with a working proxy was still a mannequin in a robe.
    # `base.obj` is never meant to be seen; the proxy is the body. So it is removed.
    if not args.keep_standin:
        bpy.data.objects.remove(human, do_unlink=True)

    # Posed last, and on the rig rather than on the meshes, so the bind pose stays what
    # the exporter needs and the pose travels as a clip.
    if not args.no_pose:
        armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        author_idle(armature)

    # The face is painted into the skin map, in the Blender session, before the export —
    # so the edited image is what the exporter embeds and the figure needs no sidecar.
    if proxy is not None and args.diagnose:
        _diagnose(proxy)

    if not args.no_face and proxy is not None:
        # The head is found from the rig, not from a height fraction: the rest pose has the
        # hands further forward than the face, so "most forward vertex of the whole body" is
        # a knuckle. The head bone is at the base of the skull and the nose is 12 cm in
        # front of it, which is well inside a sphere that the hands are outside of.
        armature = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        head_bone = armature.data.bones.get("head")
        if head_bone is None:
            print("  no 'head' bone on the rig; skipping the face")
        else:
            anchor = proxy.matrix_world.inverted() @ armature.matrix_world @ head_bone.head_local
            nose, nose_index = makehuman_face.find_nose_tip(proxy, anchor)
            box = makehuman_face.moustache_box(nose)
            image = mpfb_paint.material_image(proxy)
            if image is None:
                print("  the body's material names no single skin image; nothing to paint")
            else:
                texels = makehuman_face.paint_skin(proxy, image, box)
                print(f"  moustache: nose tip v{nose_index} at "
                      f"({nose.x:.4f}, {nose.y:.4f}, {nose.z:.4f}), "
                      f"lip z {box['lip_z']:.4f}, {texels} texels of {image.size[0]}x{image.size[1]}")

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
    out = os.path.join(args.out, "makehuman_elder.glb")
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
