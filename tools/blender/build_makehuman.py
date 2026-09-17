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

#: The man: male, old, heavy, average muscle, stocky proportions.
BODY = {
    "gender": 1.0,        # 1.0 male, 0.0 female
    "age": 0.82,
    "weight": 0.72,
    "muscle": 0.42,
    "height": 0.42,
    "proportions": 0.42,  # 0.0 wide hips, 1.0 wide shoulders
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


def enable_mpfb():
    import addon_utils

    addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)


def garment_path(AssetService, name):
    """The `.mhclo` inside `clothes/<name>/`, or None.

    Clothes live in a folder named after themselves, which is a convention of the
    asset pack and not of the API, so it is resolved here rather than assumed.
    """
    return AssetService.find_asset_absolute_path(
        name + ".mhclo", asset_subdir=os.path.join("clothes", name))


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
    args = parser.parse_args(argv)

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

    for name in args.garment:
        path = garment_path(AssetService, name)
        if path is None:
            print(f"  skipped garment {name}: not in the asset pack")
            continue
        HumanService.add_mhclo_asset(path, human, asset_type="clothes")
        print(f"  garment: {name}")

    # The stand-in goes last, after everything has been fitted to it. In Blender the proxy
    # is hidden *behind* the stand-in rather than replacing it: `_check_add_proxy` puts a
    # `MASK` modifier on the basemesh and the proxy is fitted a hair outside it. Exporting
    # with `export_apply=False` — which skinning requires, since the exporter must leave
    # the armature modifier alone — discards that mask, so the stand-in exported at full
    # strength and the first frame with a working proxy was still a mannequin in a robe.
    # `base.obj` is never meant to be seen; the proxy is the body. So it is removed.
    if not args.keep_standin:
        bpy.data.objects.remove(human, do_unlink=True)

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
