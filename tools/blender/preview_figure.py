"""Draw the MakeHuman briefing figure as the game will see it: textures, posed.

`preview_model.py` draws the generated models the way the renderer stores them —
flat vertex colours — which is the right tool for a tank and the wrong one for a
figure whose whole face is in a texture. The MakeHuman figure carries its colour
in embedded images and its stance in an `Idle` clip, so this renders with
Workbench's TEXTURE colour mode on a frame of that clip instead of the bind pose.

    blender --background --python tools/blender/preview_figure.py -- \
        artifacts/mh-baseline/personality_elder_skinned.glb \
        --out artifacts/mh-baseline/preview --views front,three-quarter --zoom 0.30
"""

import argparse
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import preview_model  # noqa: E402


# The MakeHuman figure faces **-Y**, measured in `makehuman_face.py`, which is the
# opposite of the generated models `preview_model.VIEWS` is named for. So the view
# that shows this figure's face is the one named for his front, not for the axis.
VIEWS = dict(preview_model.VIEWS)
VIEWS["front"] = (0.0, -1.0, 0.0)
VIEWS["back"] = (0.0, 1.0, 0.0)
VIEWS["three-quarter"] = (0.75, -1.0, 0.35)


def posed_bounds(meshes):
    """The model's box measured from the *evaluated* meshes, pose included.

    `preview_model.bounds` reads `obj.data.vertices`, which is the bind mesh: for a
    skinned figure it frames the A-pose the file stores, not the man the camera will
    see, and the frame lands on his chest. The depsgraph has the posed vertices once
    a frame is set, which is what `pose_idle` just did.
    """
    low = Vector((math.inf, math.inf, math.inf))
    high = Vector((-math.inf, -math.inf, -math.inf))
    depsgraph = bpy.context.evaluated_depsgraph_get()

    for obj in meshes:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        for vertex in mesh.vertices:
            world = evaluated.matrix_world @ vertex.co
            for axis in range(3):
                low[axis] = min(low[axis], world[axis])
                high[axis] = max(high[axis], world[axis])
        evaluated.to_mesh_clear()

    return low, high


def pose_idle(frame=1):
    """Put the armature on a frame of its clip, so the figure is standing.

    The glTF importer brings `Idle` in as an action assigned to the armature; a
    `frame_set` is then all it takes for the meshes to follow, because the
    armature modifiers evaluate against the pose at that frame.
    """
    for obj in bpy.data.objects:
        if obj.type == "ARMATURE" and obj.animation_data and obj.animation_data.action:
            bpy.context.scene.frame_set(frame)
            bpy.context.view_layer.update()
            return True
    return False


def configure_texture_render(width, height):
    preview_model.configure_render(None, width, height)
    display = bpy.context.scene.display.shading
    display.color_type = "TEXTURE"
    display.show_cavity = False
    display.show_shadows = True
    # The studio light's specular highlight is the preview lying about shine: the
    # game has no specular on skinned parts, so neither does this.
    display.show_specular_highlight = False


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Render the briefing figure with textures.")
    parser.add_argument("model", help="Path to the .glb to draw.")
    parser.add_argument("--out", required=True, help="Directory for the PNGs.")
    parser.add_argument("--views", default="front,three-quarter")
    parser.add_argument("--width", type=int, default=520)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--zoom", type=float, default=0.0,
                        help="0 draws the whole model; 0.3 draws the head and shoulders.")
    parser.add_argument("--frame", type=int, default=1,
                        help="which frame of the clip to pose on; 176 is mid-grasp.")
    args = parser.parse_args(argv)

    if not os.path.exists(args.model):
        print(f"no model at '{args.model}'")
        return 1

    preview_model.clear_scene()
    meshes = preview_model.import_model(args.model)
    if not meshes:
        print(f"'{args.model}' imported no meshes")
        return 1

    posed = pose_idle(args.frame)
    print(f"posed on frame {args.frame}: {posed}")

    low, high = posed_bounds(meshes)
    centre = (low + high) * 0.5
    size = high - low

    if args.zoom > 0.0:
        keep = size.z * args.zoom
        centre.z = high.z - (keep * 0.5)
        size.z = keep
        # Framing reads the largest dimension, and something in the posed scene is
        # 1.9 m wide — so a head crop has to narrow x and y as well, or the camera
        # keeps framing the phantom width and the head stays a postage stamp.
        size.x = min(size.x, keep)
        size.y = min(size.y, keep)

    os.makedirs(args.out, exist_ok=True)
    stem = os.path.splitext(os.path.basename(args.model))[0]
    configure_texture_render(args.width, args.height)

    written = []
    for name in args.views.split(","):
        name = name.strip()
        if name not in VIEWS:
            print(f"unknown view '{name}' — try one of {', '.join(VIEWS)}")
            continue

        for camera in [o for o in bpy.data.objects if o.type == "CAMERA"]:
            bpy.data.objects.remove(camera, do_unlink=True)

        bpy.context.scene.camera = preview_model.place_camera(
            f"cam_{name}", centre, size, VIEWS[name])
        path = os.path.join(args.out, f"{stem}-{name}.png")
        bpy.context.scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        written.append(path)
        print(f"wrote {path}")

    print(f"done: {len(written)} views of {len(meshes)} mesh(es), "
          f"{size.x:.3f} x {size.y:.3f} x {size.z:.3f} m")
    return 0


if __name__ == "__main__":
    sys.exit(main())
