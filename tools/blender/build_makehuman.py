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
  * Eyes, teeth and clothes are `.mhclo` assets, found with
    `AssetService.find_asset_absolute_path` and applied with
    `HumanService.add_mhclo_asset(path, basemesh, asset_type=...)`.
  * The rig is `HumanService.add_builtin_rig(basemesh, rig_name)`; the names are the
    `rig.*.json` files under `data/rigs/standard`, so `game_engine` not `standard`.

Run:
    /home/tkleisas/blender/blender-5.2.2-linux-x64/blender --background \\
        --python tools/blender/build_makehuman.py -- --out <dir>
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

#: Everything that goes on him. Subfolder under the asset root, filename, asset type.
ASSETS = [
    ("eyes", "low-poly.mhclo", "Eyes"),
    ("eyebrows", "eyebrow001.mhclo", "Eyebrows"),
    ("teeth", "teeth_base.mhclo", "Teeth"),
]

RIG = "game_engine"


def enable_mpfb():
    import addon_utils

    addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
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

    for subdir, filename, asset_type in ASSETS:
        path = AssetService.find_asset_absolute_path(filename, asset_subdir=subdir)
        if path is None:
            print(f"  skipped {asset_type}: {filename} not in the asset pack")
            continue
        HumanService.add_mhclo_asset(path, human, asset_type=asset_type)
        print(f"  added {asset_type}")

    HumanService.add_builtin_rig(human, RIG)
    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    for arm in armatures:
        print(f"rig: {arm.name}, {len(arm.data.bones)} bones")

    os.makedirs(args.out, exist_ok=True)
    out = os.path.join(args.out, "makehuman_elder.glb")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(
        filepath=out,
        export_format="GLB",
        use_selection=True,
        export_skins=True,
        export_animations=True,
        export_apply=False,
    )
    print(f"wrote {os.path.basename(out)}  ({os.path.getsize(out) / 1024:.1f} KB)")


if __name__ == "__main__":
    main()
