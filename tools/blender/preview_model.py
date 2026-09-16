"""Draw a committed .glb from four sides, with vertex colours, without the game.

The game is the judge of what a model looks like, but it is a slow judge: a face
is three metres from a camera inside a scene, lit by the scene, and framed by
whatever the set puts in the way. When the question is only "is that nose where I
think it is", the answer should not need a rebuild and a probe run.

This renders the model the way the renderer stores it — flat vertex colours,
Workbench shading, an orthographic camera — so a part that is buried inside
another part, or a feature that is on the wrong axis, is visible as itself rather
than inferred from a finished frame.

    blender --background --python tools/blender/preview_model.py -- \
        src/MiVic.Game/Content/Models/Generated/personality_elder.glb \
        --out artifacts/preview --views front,side,back,three-quarter

The camera is orthographic and framed on the model's own bounds, so the same
command frames a head and a tank without a per-model number.
"""

import argparse
import math
import os
import sys

import bpy
from mathutils import Vector


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
        for item in list(block):
            block.remove(item)


def import_model(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    imported = [obj for obj in bpy.data.objects if obj not in before and obj.type == "MESH"]

    # `bound_box` is filled in by the dependency graph, not by the importer, so a
    # camera placed before this update frames the scene the importer left behind.
    bpy.context.view_layer.update()
    return imported


def bounds(objects):
    """The model's world-space box, measured from its vertices.

    `Object.bound_box` is a cache the dependency graph fills in, and on a freshly
    imported glTF it can still hold the placeholder box — a 1.70 m figure measured
    0.58 m tall, which framed every preview on his chest.
    """
    low = Vector((math.inf, math.inf, math.inf))
    high = Vector((-math.inf, -math.inf, -math.inf))

    for obj in objects:
        for vertex in obj.data.vertices:
            world = obj.matrix_world @ vertex.co

            for axis in range(3):
                low[axis] = min(low[axis], world[axis])
                high[axis] = max(high[axis], world[axis])

    return low, high


# Every view is a direction the camera sits in, in the model's own space. The
# names are what the picture shows, not what the axes are called.
VIEWS = {
    "front": (0.0, 1.0, 0.0),
    "back": (0.0, -1.0, 0.0),
    "left": (-1.0, 0.0, 0.0),
    "right": (1.0, 0.0, 0.0),
    "three-quarter": (0.75, 1.0, 0.35),
    "top": (0.0, 0.0, 1.0),
}


def place_camera(name, centre, size, direction, margin=1.08):
    """An orthographic camera looking along `-direction` at `centre`.

    Blender's own +Y is the model's front and +Z its up, which is what the
    generators author in; the glTF exporter turns that into the game's axes, so a
    view named "front" here is the view the game calls the front.
    """
    camera_data = bpy.data.cameras.new(name)
    camera_data.type = "ORTHO"
    camera = bpy.data.objects.new(name, camera_data)
    bpy.context.collection.objects.link(camera)

    # Framed on the largest of the model's three dimensions, so a tall model and a
    # wide one are both whole in the frame. The frame is square, so a portrait
    # render needs a wider scale than a landscape one to hold the same height.
    scene = bpy.context.scene
    aspect = scene.render.resolution_x / scene.render.resolution_y
    camera_data.ortho_scale = max(size.y if aspect < 1.0 else size.x, size.z) * margin
    camera_data.ortho_scale = max(camera_data.ortho_scale, size.z * margin / min(1.0, aspect))

    aim = Vector(direction).normalized()
    position = centre + (aim * (max(size) * 2.0))
    camera.location = position

    # Point the camera's -Z at the model, with +Y up.
    forward = (centre - position).normalized()
    camera.rotation_euler = forward.to_track_quat("-Z", "Y").to_euler()
    return camera


def configure_render(shading, width, height):
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"

    display = scene.display.shading
    display.light = "STUDIO"
    display.color_type = "VERTEX"
    display.show_shadows = True
    display.show_cavity = True
    display.cavity_type = "BOTH"
    display.curvature_ridge_factor = 1.4
    display.curvature_valley_factor = 1.4
    display.background_type = "VIEWPORT"
    display.background_color = (0.11, 0.11, 0.12)

    scene.display.render_aa = "16"


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Render a .glb from several sides.")
    parser.add_argument("model", help="Path to the .glb to draw.")
    parser.add_argument("--out", required=True, help="Directory for the PNGs.")
    parser.add_argument("--views", default="front,side,three-quarter")
    parser.add_argument("--width", type=int, default=520)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--zoom", type=float, default=0.0,
                        help="0 draws the whole model; 0.4 draws the top 40%% of it.")
    args = parser.parse_args(argv)

    if not os.path.exists(args.model):
        print(f"no model at '{args.model}'")
        return 1

    clear_scene()
    meshes = import_model(args.model)

    if not meshes:
        print(f"'{args.model}' imported no meshes")
        return 1

    low, high = bounds(meshes)
    centre = (low + high) * 0.5
    size = high - low

    if args.zoom > 0.0:
        # Zooming keeps the top of the model where it is and moves the bottom up,
        # which is what a face wants: the head is at the top of every figure.
        keep = size.z * args.zoom
        centre.z = high.z - (keep * 0.5)
        size.z = keep

    os.makedirs(args.out, exist_ok=True)
    stem = os.path.splitext(os.path.basename(args.model))[0]
    configure_render(None, args.width, args.height)

    written = []

    for name in args.views.split(","):
        name = name.strip()

        if name not in VIEWS:
            print(f"unknown view '{name}' — try one of {', '.join(VIEWS)}")
            continue

        for camera in [o for o in bpy.data.objects if o.type == "CAMERA"]:
            bpy.data.objects.remove(camera, do_unlink=True)

        bpy.context.scene.camera = place_camera(f"cam_{name}", centre, size, VIEWS[name])
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
