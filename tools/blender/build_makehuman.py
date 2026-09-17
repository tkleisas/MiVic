"""Build the briefing figure from a MakeHuman base mesh, rigged, as skinned glTF.

MPFB2 is installed for the Blender at /home/tkleisas/blender/blender-5.2.2-linux-x64 and
bundles what it needs to make a base human: `data/3dobjs/base.obj`, twenty-six targets and a
standard rig. The clothes and skin packs are a separate download and are not needed here —
the uniform is this project's own painted map, and the body under it is what was missing.

Why this instead of the procedural figure: it is a real human mesh with human topology, which
is the thing a hand-built stack of domes and prisms cannot be, and the user said so plainly
when comparing the two. The measured head, the painted map and the uniform are separate work
that survives on top of it.

Run:
    BLENDER=/home/tkleisas/blender/blender-5.2.2-linux-x64/blender
    $BLENDER --background --python tools/blender/build_makehuman.py -- --out <dir>
"""

import argparse
import os
import sys

import bpy

#: MakeHuman's own macrodetail: race, gender and age in one file. This is the documented
#: way to ask for a body, and the alternative — a dictionary of macro sliders — turned out
#: not to shape anything at all.
MACRO_DETAIL = "caucasian-male-old"

#: The body, as MakeHuman's own macro sliders. These are the parameters the whole mesh is
#: generated from, and the default of 0.5 everywhere is what produced a woman in a dress.
#: ``gender`` runs 0 male to 1 female; age, weight and muscle run 0 to 1 low to high.
MACRO = {
    "gender": 1.0,
    "age": 0.82,
    "muscle": 0.42,
    "weight": 0.72,
    "proportions": 0.42,
    "height": 0.42,
    "cupsize": 0.0,
    "firmness": 0.0,
    "race": {"asian": 0.0, "caucasian": 1.0, "african": 0.0},
}


def enable_mpfb():
    import addon_utils

    addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)


def describe_macro():
    """The sliders actually used, so a build log says what kind of body was asked for."""
    return ", ".join(f"{k}={v}" for k, v in MACRO.items() if not isinstance(v, dict))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--rig", default="game_engine")
    args = parser.parse_args(argv)

    enable_mpfb()

    from bl_ext.blender_org.mpfb.services.humanservice import HumanService

    bpy.ops.wm.read_factory_settings(use_empty=True)
    HumanService.create_human()

    human = bpy.data.objects.get("Human")
    if human is None:
        raise RuntimeError("MPFB made no Human object")
    print(f"human: {len(human.data.vertices)} vertices, {len(human.data.polygons)} faces")
    print(f"build: {describe_macro()}")

    # The body is a target stack, not a dictionary. `load_target` takes a **weight that
    # defaults to zero**, so loading a target and baking changes nothing at all — which is
    # why every earlier attempt produced the same mesh whatever the macro dictionary said.
    # The macrodetail file below is MakeHuman's own "caucasian male old" and it does the
    # whole body in one go: gender, age, muscle and weight are what a macrodetail is.
    from bl_ext.blender_org.mpfb.services.locationservice import LocationService
    from bl_ext.blender_org.mpfb.services.targetservice import TargetService

    target = os.path.join(LocationService.get_mpfb_data(), "targets", "macrodetails",
                          MACRO_DETAIL + ".target.gz")
    if not os.path.exists(target):
        raise RuntimeError(f"no macrodetail target at {target}")

    TargetService.load_target(human, target, weight=1.0)
    TargetService.bake_targets(human)
    print(f"macrodetail loaded at full weight: {MACRO_DETAIL}")

    # The rig: MPFB's own standard skeleton, which is the whole reason to use this path
    # rather than the procedural one. Weights come with it.
    HumanService.add_builtin_rig(human, args.rig)
    print(f"rigged: {args.rig}")

    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    for arm in armatures:
        print(f"armature: {arm.name}, {len(arm.data.bones)} bones")

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
