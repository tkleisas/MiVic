"""The pipe, built as code, because the asset packs do not ship one.

There is no pipe in the MakeHuman system assets nor in the community equipment
packs — weapons, bags and tools, but nothing to smoke. A pipe is two pieces
though, a bent shank and a lathed bowl, and this project already authors its
art as code, so it is authored rather than fetched.

The shape is a classic billiard: a shank that leaves the corner of the mouth
and drops, and a cup tilted a little forward at the end of it. Every vertex is
weighted to the `head` bone alone, so the pipe goes where the face goes — it
is smoked, not held, which is also what lets the `Idle` clip stay as small as
it is.

The placement is measured off the face the same way the moustache paint is:
the caller passes the nose tip, and the mouth is a lip's height below it. The
one number that is not measured is the mouth's setback behind the nose tip,
which is anatomy and not this mesh's business.

Run through `build_makehuman.py`, which imports this.
"""

import math

import bmesh
import bpy
from mathutils import Matrix, Vector

#: The mouth, relative to the nose tip, in metres. The pipe sits in the corner
#: of the mouth — a centimetre off the midline reads as held between the teeth
#: rather than glued to the middle of the face.
MOUTH_BELOW_NOSE = 0.030
MOUTH_BEHIND_NOSE = 0.014
MOUTH_OFF_MIDLINE = 0.011

#: Briar and vulcanite, as linear RGB. The bowl is a dark warm brown, the
#: mouthpiece near-black, and the chamber is char.
BRIAR = (0.140, 0.082, 0.045)
VULCANITE = (0.018, 0.016, 0.015)
CHAR = (0.020, 0.014, 0.010)


def _tube(bm, path, radii, sides=10):
    """Sweep a circle along `path`, capping both ends.

    `radii` is the tube's radius at each point, so the mouthpiece can taper
    where it meets the teeth. The tangent at a point comes from its neighbours,
    which is what bends the shank without kinks.
    """
    rings = []
    for i, point in enumerate(path):
        if i == 0:
            tangent = (path[1] - path[0]).normalized()
        elif i == len(path) - 1:
            tangent = (path[-1] - path[-2]).normalized()
        else:
            tangent = (path[i + 1] - path[i - 1]).normalized()

        side = tangent.cross(Vector((0.0, 0.0, 1.0)))
        if side.length < 1e-6:
            side = tangent.cross(Vector((1.0, 0.0, 0.0)))
        side.normalize()
        up = side.cross(tangent).normalized()

        ring = []
        for k in range(sides):
            angle = 2.0 * math.pi * k / sides
            offset = side * (radii[i] * math.cos(angle)) + up * (radii[i] * math.sin(angle))
            ring.append(bm.verts.new(point + offset))
        rings.append(ring)

    for i in range(len(rings) - 1):
        for k in range(sides):
            a, b = rings[i][k], rings[i][(k + 1) % sides]
            c, d = rings[i + 1][(k + 1) % sides], rings[i + 1][k]
            bm.faces.new((a, b, c, d))

    for ring, flip in ((rings[0], True), (rings[-1], False)):
        centre = bm.verts.new(sum((v.co for v in ring), Vector((0.0, 0.0, 0.0))) / len(ring))
        for k in range(sides):
            a, b = ring[k], ring[(k + 1) % sides]
            bm.faces.new((centre, b, a) if flip else (centre, a, b))

    return rings


def _bowl(bm, origin, tilt, sides=14, scale=1.0):
    """A cup at `origin`, tipped forward by `tilt` radians: outer wall, a rim,
    and a step down into the chamber, so the top reads as a bowl of tobacco
    and not as a solid peg. Returns the rim and chamber rings for material
    assignment.
    """
    # (radius, height) pairs, bottom to rim to chamber floor, before the tilt.
    profile = [
        (0.0125 * scale, -0.024 * scale),   # heel
        (0.0165 * scale, -0.010 * scale),   # belly
        (0.0175 * scale, 0.010 * scale),    # wall
        (0.0170 * scale, 0.020 * scale),    # rim outer
        (0.0130 * scale, 0.020 * scale),    # rim inner
        (0.0128 * scale, 0.010 * scale),    # chamber wall
        (0.0000, 0.004 * scale),            # chamber floor
    ]
    # Tip the cup forward — top toward the face's front, which is -Y. That is a
    # rotation about the X axis, not "the shortest arc from X to somewhere in
    # XZ": `rotation_difference` there yaws the bowl 78° and leaves the chamber
    # facing the camera, which is how the first build shipped a pipe with its
    # bowl turned on its side.
    rotation = Matrix.Rotation(tilt, 4, "X")

    rings = []
    for radius, height in profile:
        ring = []
        for k in range(sides):
            angle = 2.0 * math.pi * k / sides
            local = Vector((radius * math.cos(angle), radius * math.sin(angle), height))
            ring.append(bm.verts.new(origin + rotation @ local))
        rings.append(ring)

    # A radius of zero made `sides` coincident verts; weld the floor shut.
    for i in range(len(rings) - 1):
        for k in range(sides):
            a, b = rings[i][k], rings[i][(k + 1) % sides]
            c, d = rings[i + 1][(k + 1) % sides], rings[i + 1][k]
            bm.faces.new((a, b, c, d))
    return rings


def _solid_material(name, colour):
    """A principled material whose base colour is a 4×4 generated image.

    The skinned renderer samples a texture, so a bare base-colour factor is
    not enough — every other part of this figure carries an image, and the
    pipe carries four-pixel ones rather than argue with the sampler.
    """
    image = bpy.data.images.new(name, 4, 4)
    image.pixels = [channel for _ in range(16) for channel in (*colour, 1.0)]

    material = bpy.data.materials.new(name)
    material.use_nodes = True
    nodes = material.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.55
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    material.node_tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    return material


def _fill_uvs(mesh):
    """One corner of the 4×4 maps everything, so the UV is a formality."""
    uvs = mesh.uv_layers.new(name="UVMap")
    for loop in uvs.data:
        loop.uv = (0.5, 0.5)


def build_pipe(armature, nose_tip):
    """Create the pipe — twice — at the mouth below `nose_tip`. Returns both objects.

    One pipe is smoked and one is held, and they are one mesh: `Human.pipe` rides the
    `pipe_mouth` bone (a child of the head), `Human.pipe_held` rides the free
    `pipe_held` bone the clip drives. The clip swaps their visibility with scale keys
    on the frame the hand is on the bowl, where the two already coincide. Each gets a
    marker empty at the bowl's rim — `pipe_bowl` and `pipe_bowl_held` — bone-parented,
    so the cutscene's smoke can follow whichever pipe is the visible one.
    """
    mouth = Vector((nose_tip.x + MOUTH_OFF_MIDLINE,
                    nose_tip.y + MOUTH_BEHIND_NOSE,
                    nose_tip.z - MOUTH_BELOW_NOSE))

    shank_dir = Vector((0.28, -0.70, -0.85)).normalized()
    shank = [mouth + shank_dir * t for t in (0.0, 0.014, 0.029, 0.043)]
    radii = (0.0036, 0.0044, 0.0051, 0.0058)
    bowl_centre = shank[-1] + Vector((0.003, -0.005, -0.015))

    mesh = bpy.data.meshes.new("pipe")
    bm = bmesh.new()
    _tube(bm, shank, radii)
    _bowl(bm, bowl_centre, tilt=math.radians(12.0), scale=0.80)
    bm.to_mesh(mesh)
    bm.free()

    # Three materials, assigned per face: vulcanite for the mouthpiece's last
    # centimetre, briar for the rest, char for the chamber floor.
    materials = [_solid_material("pipe_briar", BRIAR),
                 _solid_material("pipe_vulcanite", VULCANITE),
                 _solid_material("pipe_char", CHAR)]
    for material in materials:
        mesh.materials.append(material)
    for polygon in mesh.polygons:
        centre = sum((mesh.vertices[i].co for i in polygon.vertices),
                     Vector((0.0, 0.0, 0.0))) / len(polygon.vertices)
        if centre.z > bowl_centre.z + 0.012:
            polygon.material_index = 2      # chamber and charred rim
        elif (centre - mouth).length < 0.012:
            polygon.material_index = 1      # vulcanite at the teeth

    made = []
    for name, bone_name in (("Human.pipe", "pipe_mouth"), ("Human.pipe_held", "pipe_held")):
        own_mesh = mesh if not made else mesh.copy()
        pipe = bpy.data.objects.new(name, own_mesh)
        bpy.context.collection.objects.link(pipe)

        # Every vertex rides the pipe's own bone and nothing else.
        group = pipe.vertex_groups.new(name=bone_name)
        group.add(list(range(len(own_mesh.vertices))), 1.0, "REPLACE")
        pipe.parent = armature
        modifier = pipe.modifiers.new("Armature", "ARMATURE")
        modifier.object = armature

        made.append(pipe)

    _fill_uvs(made[0].data)
    _fill_uvs(made[1].data)

    # A marker for the smoke at each pipe's bowl. The mouth pipe's marker rides the
    # head, as it always has. The held one's rides the *hand*, and it is placed at a
    # hold frame, not at bind: a bone-parented empty bakes its parent's evaluated
    # transform into its own, and the held bone's bind is a scale of zero — placed at
    # the frame the hand is on the pipe, the bake is honest. The director gates the
    # two markers by the pipe joints' scales, so the smoke follows the visible pipe.
    bowl_top = bowl_centre + Matrix.Rotation(math.radians(12.0), 4, "X") @ Vector((0.0, 0.0, 0.018))

    marker = bpy.data.objects.new("pipe_bowl", None)
    bpy.context.collection.objects.link(marker)
    marker.parent = armature
    marker.parent_type = "BONE"
    marker.parent_bone = "head"
    marker.matrix_world = Matrix.Translation(bowl_top)

    # Where the bowl stands in the hand: the palm, plus the grip offset the clip
    # drives the held bone by, plus the bowl's own offset from the bone turned by the
    # clip's 140° stand-up. Measured at frame 312, the middle of the talking beat.
    bpy.context.scene.frame_set(312)
    bpy.context.view_layer.update()
    hand = armature.pose.bones.get("hand_l")
    if hand is not None:
        grip = Vector((0.0, 0.015, 0.025))
        stood_up = Matrix.Rotation(math.radians(-140.0), 4, "X") @ (bowl_top - mouth)
        held_bowl = (armature.matrix_world @ hand.head) + grip + stood_up

        marker = bpy.data.objects.new("pipe_bowl_held", None)
        bpy.context.collection.objects.link(marker)
        marker.parent = armature
        marker.parent_type = "BONE"
        marker.parent_bone = "hand_l"
        marker.matrix_world = Matrix.Translation(held_bowl)

    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()
    return made
