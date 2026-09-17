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

#: Which optional targets to load, and how far, for an old man who has carried weight.
#: Names come from MPFB's bundled `data/targets`; a target that is not there is skipped
#: rather than fatal, because the bundled set is smaller than MakeHuman's full library.
TARGETS = [
    ("buttocks", 0.35),
    ("cheek", 0.30),
    ("arms", 0.25),
    ("asym", 0.10),
]


def enable_mpfb():
    import addon_utils

    addon_utils.enable("bl_ext.blender_org.mpfb", default_set=True)


def list_targets():
    from bl_ext.blender_org.mpfb.services.locationservice import LocationService

    found = {}
    for root in (LocationService.get_mpfb_data(), LocationService.get_user_data()):
        folder = os.path.join(root or "", "targets")
        if not os.path.isdir(folder):
            continue
        # One level down: the bundled set groups targets by body area, so a listing of the
        # top folder finds directories and no targets at all.
        for area in sorted(os.listdir(folder)):
            sub = os.path.join(folder, area)
            if not os.path.isdir(sub):
                continue
            for entry in sorted(os.listdir(sub)):
                if entry.endswith(".target"):
                    found.setdefault(entry[:-len(".target")], os.path.join(sub, entry))
    return found


def shape(human):
    """Push the bundled targets a little way towards a heavier, older body.

    Values are small on purpose. A target at 1.0 is the extreme of its slider and the
    extremes of several at once is a caricature; this figure has to read as a man at three
    metres, not as a caricature of one.
    """
    from bl_ext.blender_org.mpfb.services.locationservice import LocationService
    from bl_ext.blender_org.mpfb.services.targetservice import TargetService

    available = list_targets()
    if not available:
        return []

    applied = []
    for name, value in TARGETS:
        if name not in available:
            continue
        try:
            TargetService.load_target(human, available[name], value)
            applied.append(f"{name}={value}")
        except Exception as error:  # noqa: BLE001 - a missing slider is not fatal
            print(f"  target {name} refused: {type(error).__name__}: {error}")
    return applied


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
    print(f"targets available: {len(list_targets())}")

    applied = shape(human)
    print(f"shaped by: {', '.join(applied) if applied else 'nothing'}")

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
