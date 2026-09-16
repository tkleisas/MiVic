"""Generate MiVic's cutscene personalities with Blender, headlessly.

    blender --background --python tools/blender/build_personalities.py -- --out <dir>

Separate from build_figures.py because a personality is a different problem from
a soldier, exactly as a soldier is a different problem from a tank. A soldier is
one of hundreds, seen from above, and is judged by his helmet, his shoulders and
the diagonal of his rifle. A personality is the only thing on screen for half a
minute, seen at two to five metres, and is judged by posture, by the shape of a
silhouette, and by two or three props that say who he is without a caption.

**Nothing here is named.** The file names, the part names and the comments
describe a build — "elder", "statesman", "the man at the desk" — and never a
real person. The campaign's fiction is an alternate history and these figures
are recognisable archetypes inside it; the game says who they evoke through a
greatcoat, a moustache and a pipe, and never through a name. That is a
deliberate rule of the fiction and not a naming preference, so keep it when
adding another.

The part contract, which the renderer animates, is the same one soldiers use:

    LegLeft/LegRight    thighs, pivoting at the hip
    ShinLeft/ShinRight  shins, inheriting the thigh and bending at the knee
    ArmLeft/ArmRight    upper arms, pivoting at the shoulder
    Forearm*/Hand*      riding the upper arm, so a forearm can be raised alone
    Body, Head          the parts that move, turn and breathe
    Hair/Moustache/Pipe riding the head, so they turn with it
    Coat/CoatSkirt      the long coat, riding the torso

The director animates the arms and the head and nothing else: a staged diorama
gestures, it does not perform. Parts are therefore built hanging *below* their
own origin, which puts the origin at the joint for free — the renderer measures
the top of a limb to find where it pivots, and a mesh built around its joint
needs no skeleton to query.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# The mesh helpers and the material table are shared with every other generator;
# there is exactly one definition of what "steel" is.
from build_vehicles import MATERIALS, box, clear_scene, cylinder, dome, export, join  # noqa: E402
from build_vehicles import paint  # noqa: E402
from build_vehicles import _box_geo, _cyl_geo, _dome_geo, _frustum_geo, _link, _spin_geo, merge  # noqa: E402
import face_uv  # noqa: E402


# --------------------------------------------------------------------------
# One man, in metres, against a 1.78 m figure. Stocky rather than tall: the
# archetype reads as heavy, and weight is the one thing a low-poly body can say
# from across a room.
# --------------------------------------------------------------------------

ANKLE = 0.09
KNEE = 0.47
HIP = 0.90
SHOULDER = 1.36
# The neck's joint, which is where the head's own coordinates start: the head is
# authored from z = 0 at the neck and hangs off it to 0.289. Everything on the
# torso is measured up towards it, and the one number that matters is the gap
# between the top of the collar and the chin — at 1.43 and 1.45 there is 1.8 cm of
# neck, and at 1.43 and 1.40 the collar is over the man's mouth.
HEAD_BASE = 1.506

# Where the head's own warp put its features. `_head_surface` runs `v` from 0 at
# the crown to 1 under the chin along `z = 0.150 + cos(pi * v) * 0.139`, so these
# are that curve read at the brow ridge, the eye sockets, the nose and the lip.
# Everything stuck on the face afterwards hangs off them: a moustache at the
# brow's height is a moustache on the forehead.
BROW_Z = 0.180      # v = 0.43
EYE_Z = 0.164       # the eye line: 45 % of the way down the head
NOSE_TIP_Z = 0.121  # 62 % down
LIP_Z = 0.095       # 72 % down, under the nose
EAR_Z = 0.146       # v = 0.53
MOUTH_Z = 0.077     # 79 % down, the crease between the lips

FLESH = (0.80, 0.63, 0.47, 0.00)
FLESH_SHADE = (0.60, 0.45, 0.33, 0.00)


def smooth(*objects, angle_degrees=32.0):
    """Smooth-shades the curved faces of these parts and leaves their corners crisp.

    A dome of eight rings drawn flat reads as a faceted ball; the same dome smoothed reads
    as a skull. Auto-smooth keeps a box's own edges sharp by angle, which is what lets one
    merged part carry both a cranium and a collar tab.
    """
    bpy.ops.object.select_all(action="DESELECT")

    for obj in objects:
        obj.select_set(True)

    if not objects:
        return

    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.shade_auto_smooth(angle=math.radians(angle_degrees))
    bpy.ops.object.select_all(action="DESELECT")


# --------------------------------------------------------------------------
# Surfaces of revolution that are not solids of revolution: a sphere grid with a
# warp function applied to it, and an optional `keep` that leaves part of it out.
# The head is the first; the hair shell is the second, and it is the same grid
# with a bigger radius and everything in front of the hairline cut away.
# --------------------------------------------------------------------------

def _grid_mesh(name, segments, rings, warp, keep=None):
    """A sphere grid, warped per point, with quads kept where `keep(u, v)` says.

    `u` runs around the head from 0 to 1 with 0.25 at the face; `v` runs from the
    crown at 0 to under the chin at 1. Vertices are shared between the quads that
    survive, so the surface stays smooth where it is kept and its boundary is the
    kept edge — which is how a hairline is a shape rather than a decal.
    """
    index = {}
    verts = []
    uvs = []
    faces = []

    def vertex(ring, seg):
        key = (ring, seg % segments)

        if key not in index:
            u = (seg % segments) / segments
            v = ring / rings
            index[key] = len(verts)
            verts.append(warp(u, v))

            # The grid's own coordinates are the texture's, remapped so the face
            # gets the middle of the map and most of its width: a face is a fifth
            # of the way round a head, and a linear map spends four fifths of the
            # texture on hair and the back of a skull nobody looks at.
            uvs.append((face_uv.wrap_u(u), v))

        return index[key]

    for ring in range(rings):
        for seg in range(segments):
            u = (seg + 0.5) / segments
            v = (ring + 0.5) / rings

            if keep is not None and not keep(u, v):
                continue

            a = vertex(ring, seg)
            b = vertex(ring + 1, seg)
            c = vertex(ring + 1, seg + 1)
            d = vertex(ring, seg + 1)

            if ring == 0:
                faces.append((a, c, d))
            elif ring == rings - 1:
                faces.append((a, b, d))
            else:
                faces.append((a, b, c, d))

    return _link(name, verts, faces, uvs)


def _head_surface(segments=36, rings=26, crown_taper=True):
    """One face, as a function of where you are on a head."""
    half_x, half_y, half_z = 0.089, 0.116, 0.136
    centre_z = 0.150

    def warp(u, v):
        phi = math.pi * v
        theta = 2.0 * math.pi * u

        # A head is not an egg, and an ellipsoid is exactly an egg: round in every
        # direction, curving away from the light everywhere at once. A skull is a
        # box of bone with the corners taken off, and two exponents are what turn
        # one into the other.
        #
        # The first keeps the crown and the temples *full* instead of letting them
        # fall away — a sine reaches its width at one height and curves off either
        # side of it, and a skull holds its width across the whole parietal.
        radius = math.sin(phi) ** 0.62

        # The crown itself comes to a point rather sooner than the parietal does.
        if crown_taper and v < 0.14:
            radius *= 0.55 + (0.45 * (v / 0.14) ** 0.6)

        # The second flattens the plan from a circle into a rounded rectangle, so
        # the face is a face and not the front of a ball.
        cosine, sine = math.cos(theta), math.sin(theta)
        squircle = (abs(cosine) ** 2.0 + abs(sine) ** 2.0) ** (-1.0 / 2.0)

        nx = radius * cosine * squircle
        ny = radius * sine * squircle
        nz = math.cos(phi)

        x = nx * half_x
        y = ny * half_y
        z = centre_z + (nz * half_z)

        dx = nx                     # -1 left .. +1 right
        front = max(0.0, ny)        # 1 at the face, 0 at the sides and back

        # ---- the face: cheekbones, then a jaw, then a chin ------------------
        # A head is not an egg. It is a box of bone with the corners taken off: a
        # cheekbone that catches the light, a jaw that keeps its width out to a
        # corner and only then turns in, and a chin under a crease. Without those
        # three the surface is smooth all the way round and reads as a ball, which
        # is what every version of this head did before this one.
        for side in (-1.0, 1.0):
            # Measured off the reference rather than guessed: its cranium is at
            # three quarters of full width two rows below the crown, its temples
            # pinch in at the eye line, and its cheekbones flare back out below
            # them. Mine was a smooth cone from the crown to the jaw.
            temple = math.exp(-(((v - 0.520) / 0.075) ** 2))
            x -= side * 0.012 * temple * (abs(dx) ** 0.5)

            crown = math.exp(-(((v - 0.20) / 0.22) ** 2))
            x += side * 0.008 * crown * (abs(dx) ** 0.5)

            zygomatic = math.exp(-((((dx - (side * 0.62)) / 0.26) ** 2) + (((v - 0.600) / 0.070) ** 2)))
            x += side * 0.020 * zygomatic
            y += 0.011 * zygomatic * front

            corner = math.exp(-((((abs(dx) - 0.70) / 0.30) ** 2) + (((v - 0.800) / 0.090) ** 2)))
            x += math.copysign(0.010 * corner, dx) if abs(dx) > 1e-9 else 0.0

        # Below the corner of the jaw the bone turns in towards the chin.
        # Measured, not guessed. The reference's skull is at full width high up
        # over the parietal, narrows gradually to about five sixths of that at the
        # temples and the jaw, and holds that to the chin — it never comes to a
        # point. Mine was widest at the cheekbones and tapered to nothing below,
        # which is what made the lower third of the face a long blank.
        middle = max(0.0, min(1.0, (v - 0.44) / 0.28))
        x *= 1.0 - (0.17 * middle)

        if v > 0.58:
            taper = 1.0 - (0.06 * ((v - 0.58) / 0.42) ** 1.4)
            x *= taper
            y *= 0.72 + (0.28 * taper)

        # Brow ridge: a shelf over the eyes, and the sockets cut in under it.
        brow = math.exp(-(((v - 0.405) / 0.075) ** 2))
        y += 0.016 * brow * front

        for side in (-1.0, 1.0):
            socket = math.exp(-((((dx - (side * 0.40)) / 0.30) ** 2) + (((v - 0.466) / 0.082) ** 2)))
            y -= 0.024 * socket

        # The nose. Not a ridge with a bulge on it: a bridge that narrows towards
        # the nasion, a tip that is its own ball of cartilage on the end of it, two
        # wings that flare at the bottom, a crease behind each of them, and an
        # underside that turns back in. A gaussian across the whole thing is a
        # smear, and a smear with paint on it is what the first four versions of
        # this face were.
        bridge = math.exp(-((dx / 0.185) ** 2))
        rise = 0.056 * math.exp(-(((v - 0.556) / 0.082) ** 2))
        y += rise * bridge * max(0.15, ny)

        tip = math.exp(-(((dx / 0.150) ** 2) + (((v - 0.578) / 0.030) ** 2)))
        y += 0.020 * tip * max(0.15, ny)

        for side in (-1.0, 1.0):
            wing = math.exp(-((((dx - (side * 0.225)) / 0.100) ** 2) + (((v - 0.604) / 0.040) ** 2)))
            y += 0.018 * wing * front

            crease = math.exp(-((((dx - (side * 0.355)) / 0.070) ** 2) + (((v - 0.606) / 0.052) ** 2)))
            y -= 0.012 * crease * front

        under = math.exp(-(((v - 0.632) / 0.018) ** 2)) * math.exp(-((dx / 0.30) ** 2))
        y -= 0.015 * under * front

        # The fine planes a face is actually made of, which a grid this size can
        # now hold: the crease above each lid, the philtrum under the nose, the
        # dimple in the chin, and the hollow at each temple.
        for side in (-1.0, 1.0):
            lid = math.exp(-((((dx - (side * 0.36)) / 0.20) ** 2) + (((v - 0.452) / 0.020) ** 2)))
            y -= 0.004 * lid * front

            wing_groove = math.exp(-((((dx - (side * 0.300)) / 0.045) ** 2) + (((v - 0.620) / 0.045) ** 2)))
            y -= 0.006 * wing_groove * front

        philtrum = math.exp(-(((dx / 0.055) ** 2) + (((v - 0.665) / 0.026) ** 2)))
        y -= 0.005 * philtrum * front

        dimple = math.exp(-(((dx / 0.075) ** 2) + (((v - 0.862) / 0.022) ** 2)))
        y -= 0.005 * dimple * front

        for side in (-1.0, 1.0):
            hollow = math.exp(-((((abs(dx) - 0.70) / 0.22) ** 2) + (((v - 0.430) / 0.070) ** 2)))
            y -= 0.007 * hollow * front

        # Lips, with the crease between them, and a chin under both.
        mouth = math.exp(-(((v - 0.691) / 0.043) ** 2)) * math.exp(-((dx / 0.42) ** 2))
        y += 0.008 * mouth * front
        y -= 0.011 * math.exp(-(((v - 0.716) / 0.016) ** 2)) * math.exp(-((dx / 0.34) ** 2))

        chin = math.exp(-(((v - 0.880) / 0.060) ** 2)) * math.exp(-((dx / 0.48) ** 2))
        y += 0.018 * chin * front

        # The crease under the lower lip, which is what makes a chin a chin
        # instead of the place the jaw stops.
        y -= 0.009 * math.exp(-(((v - 0.805) / 0.021) ** 2)) * math.exp(-((dx / 0.30) ** 2))

        # The neck opening: the underside converges on the neck rather than ending
        # in a flat lid.
        if v > 0.94:
            pull = (v - 0.94) / 0.06
            x *= 1.0 - (0.55 * pull)
            y *= 0.55 * (1.0 - (0.35 * pull))

        return (x, y, z)

    return warp


def _hair_shell(segments=36, rings=18):
    """The hair, as the same head with a bigger radius and the face left open."""
    warp = _head_surface(segments, rings, crown_taper=False)

    def warped(u, v):
        x, y, z = warp(u, v)
        # Push the shell out from the head's centre, and sweep the back up into a
        # comb-back: a receding hairline is the shape that says "old" without a caption.
        cz = 0.150

        # Hair lies on a skull. It is not a mass standing off it and it is not a
        # shell the same thickness everywhere — the first is a toupee and the
        # second is a moulding, and both of them were tried here. What this man has
        # is hair combed back from a high forehead that is *thinner over the crown*
        # than at the sides and the nape, because that is what a receding head of
        # hair does: it goes on top first.
        thickness = 1.030 + (0.055 * (1.0 - math.exp(-(((v - 0.02) / 0.30) ** 2))))
        x *= thickness
        y *= thickness
        z = cz + ((z - cz) * 1.01)
        y -= 0.004 * (1.0 - v)

        return (x, y, z)

    return warp


def _hair_shell(segments=36, rings=18):
    """The hair, as the same head with a bigger radius and the face left open."""
    warp = _head_surface(segments, rings, crown_taper=False)

    def warped(u, v):
        x, y, z = warp(u, v)
        # Push the shell out from the head's centre, and sweep the back up into a
        # comb-back: a receding hairline is the shape that says "old" without a caption.
        cz = 0.150

        # Thick over the crown, thin at the hairline and the temples. A shell the
        # same thickness everywhere is a moulding, and that is exactly what this
        # read as; hair lies on the skull at the edges and stands off it on top.
        thickness = 1.035 + (0.075 * math.exp(-(((v - 0.03) / 0.26) ** 2)))
        x *= thickness
        y *= thickness
        z = cz + ((z - cz) * 1.02)
        y -= 0.004 * (1.0 - v)

        # Standing up. The reference's hair is a mass lifted off the skull and
        # brushed back, not a cap painted on it, and a cap is what this was: the
        # shell sat 2 per cent outside the head and followed every curve of it.
        # The lift is strongest over the crown and fades back down the sides, and
        # the front is lifted more than the nape, which is what "brushed up" means.
        lift = math.exp(-(((v - 0.02) / 0.34) ** 2))
        z += 0.024 * lift
        y -= 0.010 * lift * max(0.0, math.sin(math.pi * v) * math.sin(2.0 * math.pi * u))

        # and it stands off the skull rather than on it
        z += 0.014 * max(0.0, 0.6 - v)
        return (x, y, z)

    def keep(u, v):
        # The hairline, measured around the head rather than across the face: 0 at
        # the face, 1 at the nape. `face_uv.hairline` is the same function, and the
        # painter paints to it — one definition, because two would end in two
        # different hairlines with a band of forehead between them.
        return v < face_uv.hairline(u)

    return warped, keep


def _capped_mesh(name, segments, rings, warp, cap):
    """A grid from the pole down to a boundary that follows a curve.

    `_grid_mesh` keeps whole quads, so its boundary is a staircase: the hairline it
    drew stepped across the forehead in four-millimetre risers, which at three
    metres is a visible zigzag and the single most obviously synthetic thing on the
    head. Here the grid runs from the crown to the boundary *by construction* — `v`
    is a fraction of `cap(u)` rather than of the whole sphere — so the edge is the
    curve itself, at whatever resolution the curve is sampled at, and no quad is
    ever thrown away.
    """
    verts = []
    uvs = []
    faces = []

    for ring in range(rings + 1):
        for seg in range(segments):
            u = seg / segments
            v = (ring / rings) * cap(u)
            verts.append(warp(u, v))
            uvs.append((face_uv.wrap_u(u), v))

    for ring in range(rings):
        for seg in range(segments):
            nxt = (seg + 1) % segments
            here = (ring * segments) + seg
            below = ((ring + 1) * segments) + seg

            faces.append((here, below, ((ring + 1) * segments) + nxt, (ring * segments) + nxt))

    return _link(name, verts, faces, uvs)


def _pin_uv(obj, uv):
    """Gives a part one texture coordinate for all of its vertices.

    The parts of a figure share one texture, and a part that carries no layout of
    its own samples the corner of it — which is the back of the head, so the
    moustache was coming out the colour of hair in shadow. Pinning it to the place
    on the map that was painted for it is the whole fix.
    """
    mesh = obj.data
    attribute = mesh.uv_layers.get("UVMap") or mesh.uv_layers.new(name="UVMap")

    for loop in range(len(mesh.loops)):
        attribute.data[loop].uv = (uv[0], 1.0 - uv[1])

    return obj


def _wrap_uv(obj, metres_per_tile=0.34):
    """Wraps a part in a cylindrical projection, so cloth tiles on a body.

    A tunic is a surface of revolution about the spine and a sleeve is one about
    the arm, so a projection around the part's own z is the projection that does
    not stretch: the texture goes round the body once and up it as many times as
    the garment is tiles tall.
    """
    mesh = obj.data
    layer = mesh.uv_layers.get("UVMap") or mesh.uv_layers.new(name="UVMap")

    for poly in mesh.polygons:
        for loop in poly.loop_indices:
            vertex = mesh.vertices[mesh.loops[loop].vertex_index].co
            # Three tiles round the body against one up it. Around a chest is
            # about 1.4 m and a tile is 0.22 m, so a single wrap stretches the
            # weave six to one and the tunic comes out corduroy.
            layer.data[loop].uv = (
                math.atan2(vertex.y, vertex.x) / (2.0 * math.pi) * 2.0,
                vertex.z / metres_per_tile,
            )

    return obj


def _face_dome(name, radius, width, height, depth, at, droop=0.0, sweep=0.0, rings=5):
    """A dome that bulges out of the face, sized in the face's own axes.

    `_dome_geo` bulges along +Z and the head looks along +Y, so a feature written
    straight from the kit points at the ceiling. A quarter turn about X brings the
    bulge forward and leaves the dome's own X as the face's width and its own Y as
    the face's height — which is what `width`, `height` and `depth` mean here.
    `droop` then rolls the piece about the forward axis, so a moustache wing hangs,
    and `sweep` turns it about the vertical, so it follows the cheek.
    """
    part = merge(name, [_dome_geo(radius, (width, height, depth), segments=12, rings=rings)])
    part.rotation_euler = (
        math.radians(-90.0),
        math.radians(droop),
        math.radians(sweep),
    )
    part.location = at
    return part


def _prism_geo(plan, top, height, offset=(0.0, 0.0, 0.0), segments=20, power=0.45):
    """`_frustum_geo` with as many sides as you like, and the corners taken off.

    Four corners is the right amount of corner for a turret and far too much for a
    greatcoat: a coat is a body of revolution pressed flat, so its cross-section is
    a rectangle with its corners rounded off. That shape is a superellipse, and
    `power` is how far towards a rectangle it is — 1.0 is an ellipse, 0.45 is a
    flat front with round shoulders at the corners.
    """
    ox, oy, oz = offset
    sx, sy = plan[0] * 0.5, plan[1] * 0.5
    tx, ty = sx * top[0], sy * top[1]
    verts = []
    faces = []

    for i in range(segments):
        angle = (2.0 * math.pi * i) / segments
        cosine, sine = math.cos(angle), math.sin(angle)
        cx = math.copysign(abs(cosine) ** power, cosine)
        cy = math.copysign(abs(sine) ** power, sine)
        verts.append((cx * sx + ox, cy * sy + oy, oz))
        verts.append((cx * tx + ox, cy * ty + oy, oz + height))

    for i in range(segments):
        nxt = ((i + 1) % segments) * 2
        here = i * 2
        faces.append((here, nxt, nxt + 1, here + 1))

    faces.append(tuple(range(segments * 2 - 2, -1, -2)))
    faces.append(tuple(range(1, segments * 2, 2)))
    return verts, faces


def build_elder():
    """The man at the desk: an old soldier in a plain tunic, a moustache and a pipe.

    Built from the same kit the soldiers are and round where a soldier is square:
    a domed skull over a tapered jaw, a trunk that widens into a domed shoulder
    line, a greatcoat flaring to a rounded hem, and cylindrical limbs. A soldier is
    read at forty metres as a helmet and a shoulder line, and every corner he has
    survives that. A personality is the whole frame at three metres, and at three
    metres every corner is a corner.
    """
    root = bpy.data.objects.new("elder", None)
    bpy.context.collection.objects.link(root)

    parts = []

    shoulders = 0.50
    bulk = 1.18
    leg_half = shoulders * 0.25

    # The arms sit just outside the coat's shoulder, near enough to the body that
    # the coat reads as something he is wearing: a gesture is the only motion a
    # rigid-part figure has, and an arm out in the air has none to give it weight.
    arm_x = (shoulders * 0.40)

    # Sampled off the reference the brief was modelled from: an olive tunic with
    # gold buttons, and a man whose hair, brows and moustache are all one dark
    # grey-brown. An old soldier, not a white-haired one — the first version got
    # that wrong and painted him with the hair of a man twenty years older.
    tunic = (0.44, 0.43, 0.22, 0.60)
    tunic_dark = (0.33, 0.32, 0.16, 0.34)
    trouser = (0.38, 0.37, 0.20, 0.30)
    boot = (0.10, 0.10, 0.10, 0.05)
    belt_colour = (0.14, 0.11, 0.08, 0.10)
    collar_red = (0.46, 0.08, 0.06, 0.00)
    gold = (0.74, 0.58, 0.22, 0.00)
    hair_colour = (0.22, 0.18, 0.15, 0.03)
    hair_dark = (0.14, 0.11, 0.09, 0.03)

    # ---- legs: tapered cylinders, two parts each so a stance can shift --------
    for side, tag in ((-1, "Left"), (1, "Right")):
        thigh = merge(f"Leg{tag}", [
            _cyl_geo(0.098 * bulk, HIP - KNEE, segments=10, axis="z", offset=(0.0, 0.0, -(HIP - KNEE) * 0.5)),
        ])
        thigh.location = (side * leg_half, 0.0, HIP)
        _wrap_uv(thigh)
        parts.append(thigh)
        paint(thigh, trouser)

        shin = merge(f"Shin{tag}", [
            _cyl_geo(0.084 * bulk, KNEE - ANKLE, segments=10, axis="z", offset=(0.0, 0.0, -(KNEE - ANKLE) * 0.5)),
        ])
        shin.parent = thigh
        shin.location = (0.0, 0.0, -(HIP - KNEE))
        _wrap_uv(shin)
        parts.append(shin)
        paint(shin, trouser)

        foot = merge("Boot", [
            _box_geo((0.17 * bulk, 0.29, 0.115), offset=(0.0, 0.055, -(KNEE - ANKLE) - 0.058), taper=0.82),
        ])
        foot.parent = shin
        parts.append(foot)
        paint(foot, boot, variation=0.04)

    # ---- torso: a fitted tunic ----------------------------------------------
    # A greatcoat is one silhouette from the shoulder to the hem, and at three
    # metres that silhouette is a barrel with a head on it — which is what made
    # the first version read as a toy. A service tunic has a waist and a chest
    # and two of them are different widths, so the figure has a middle.
    skirt = merge("Tunic", [
        _prism_geo((shoulders * 0.80, 0.29 * bulk), (0.94, 0.96), 0.42, offset=(0.0, 0.0, HIP - 0.30), power=0.52),
    ])
    _wrap_uv(skirt)
    parts.append(skirt)
    paint(skirt, tunic, variation=0.09)

    belt = merge("Belt", [
        _prism_geo((shoulders * 0.77, 0.30 * bulk), (1.0, 1.0), 0.055, offset=(0.0, 0.0, HIP + 0.10), power=0.52),
    ])
    _wrap_uv(belt)
    parts.append(belt)
    paint(belt, belt_colour)

    trunk = _prism_geo((shoulders * 0.78, 0.30 * bulk), (1.16, 1.06), 0.24, offset=(0.0, 0.0, HIP + 0.155), power=0.52)
    crown = _dome_geo(shoulders * 0.52, (1.0, 0.62, 0.26), segments=20, rings=6, offset=(0.0, 0.0, HIP + 0.35))
    chest = merge("Body", [trunk, crown])
    _wrap_uv(chest)
    parts.append(chest)
    paint(chest, tunic, variation=0.09)

    # The placket: the strip the buttons sit on, standing a few millimetres proud
    # of the tunic, because a row of buttons floating on a flat chest reads as
    # beads rather than as a fastening.
    placket = box("Placket", (0.062, 0.020, 0.60), offset=(0.0, 0.0, 0.0))
    placket.location = (0.0, 0.030, HIP + 0.16)
    parts.append(placket)
    paint(placket, tunic_dark, variation=0.03)

    # The stand collar, with the two red tabs that make it a uniform.
    collar = merge("Collar", [
        _prism_geo((0.138, 0.136), (0.94, 0.94), 0.048, offset=(0.0, 0.0, 0.0), power=0.60, segments=18),
    ])
    collar.location = (0.0, 0.0, SHOULDER - 0.008)
    _wrap_uv(collar)
    parts.append(collar)
    paint(collar, tunic, variation=0.03)

    for side in (-1, 1):
        tab = box("CollarTab", (0.030, 0.016, 0.036), offset=(0.0, 0.0, 0.0))
        tab.location = (side * 0.030, 0.062, SHOULDER + 0.016)
        parts.append(tab)
        paint(tab, collar_red, variation=0.02)

        # A shoulder board on each shoulder, laid along it and tipped outward.
        board = box("Board", (0.050, 0.130, 0.016), offset=(0.0, 0.0, 0.0))
        board.location = (side * shoulders * 0.28, -0.008, HIP + 0.478)
        board.rotation_euler = (0.0, math.radians(side * 14.0), 0.0)
        parts.append(board)
        paint(board, gold, variation=0.03)

    # A gold star on the left breast: the one bright note on the chest.
    star = merge("Star", [
        _cyl_geo(0.028, 0.012, segments=5, axis="y", offset=(0.0, 0.0, 0.0)),
    ])
    star.location = (-shoulders * 0.22, 0.196, HIP + 0.36)
    parts.append(star)
    paint(star, gold, variation=0.02)

    ribbon = box("Ribbon", (0.034, 0.012, 0.026), offset=(0.0, 0.0, 0.0))
    ribbon.location = (-shoulders * 0.22, 0.194, HIP + 0.40)
    parts.append(ribbon)
    paint(ribbon, collar_red, variation=0.02)

    # ---- arms: sleeves off the shoulder, with the right forearm carried up
    # towards the pipe. The director can raise either one from here. ---------
    for side, tag in ((-1, "Left"), (1, "Right")):
        upper = merge("Arm" + tag, [
            _cyl_geo(0.062 * bulk, 0.30, segments=12, axis="z", offset=(0.0, 0.0, -0.15)),
            # A shoulder cap, so the sleeve grows out of the tunic instead of
            # being a tube parked beside it.
            _dome_geo(0.062 * bulk, (1.0, 1.0, 0.55), segments=12, rings=4),
        ])
        upper.location = (side * arm_x, 0.0, SHOULDER + 0.010)
        _wrap_uv(upper)
        parts.append(upper)
        paint(upper, tunic, variation=0.04)

        fore = merge("Forearm" + tag, [
            _cyl_geo(0.056 * bulk, 0.26, segments=12, axis="z", offset=(0.0, 0.0, -0.13)),
            # The cuff: one ring, and the sleeve becomes a sleeve.
            _cyl_geo(0.066 * bulk, 0.035, segments=12, axis="z", offset=(0.0, 0.0, -0.245)),
        ])
        fore.parent = upper
        fore.location = (0.0, 0.0, -0.30)

        if side == 1:
            fore.rotation_euler = (math.radians(-58.0), 0.0, 0.0)

        _wrap_uv(fore)
        parts.append(fore)
        paint(fore, tunic, variation=0.04)

        # The fist: a cylinder with a dome on the end of it. `_dome_geo` bulges
        # along +Z and the arm hangs along -Z, so the dome is spun half a turn
        # about the wrist to cap the knuckles — written straight from the kit it
        # capped the wrist and left the fingers flat, which is what "the joint on
        # the hand is reversed" was.
        hand = merge("Hand" + tag, [
            _cyl_geo(0.050, 0.070, segments=12, axis="z", offset=(0.0, 0.0, -0.035)),
            _spin_geo(
                _dome_geo(0.050, (1.0, 0.86, 0.80), segments=12, rings=4),
                pitch=180.0,
                pivot=(0.0, 0.0, -0.035),
            ),
        ])
        hand.parent = fore
        hand.location = (0.0, 0.0, -0.275)
        parts.append(hand)
        paint(hand, FLESH, variation=0.03)

    # ---- head: one sculpted surface, not a stack of boxes. The features are the
    # warp of the grid, and the rest of the face is the texture the head samples —
    # the two together are what a face is read from at three metres. The moustache
    # is the one feature that is geometry, because it stands off the lip. Geometry
    # is in the neck's space. ---------------------------------------------------
    neck = merge("Neck", [
        _cyl_geo(0.058, 0.17, segments=14, axis="z", offset=(0.0, 0.0, 0.01)),
    ])
    neck.location = (0.0, 0.0, HEAD_BASE - 0.075)
    parts.append(neck)
    paint(neck, FLESH_SHADE)

    # A stand collar on the shoulders. It has to clear the chin, which on a sculpted
    # head is only 1.7 cm above the neck's own base — a collar sized for a soldier's
    # block head rises past the mouth and the moustache ends up behind it.
    collar = merge("Collar", [
        _frustum_geo((0.100, 0.108), (0.86, 0.86), 0.062, offset=(0.0, 0.0, 0.015)),
    ])
    collar.parent = neck
    _wrap_uv(collar)
    parts.append(collar)
    paint(collar, tunic)

    for side in (-1, 1):
        tab = box("CollarTab", (0.046, 0.022, 0.040), offset=(0.0, 0.0, 0.0))
        tab.parent = neck
        tab.location = (side * 0.040, 0.070, 0.052)
        parts.append(tab)
        paint(tab, collar_red, variation=0.02)

    head = _grid_mesh("Head", 88, 62, _head_surface(88, 62))
    head.parent = neck
    parts.append(head)

    # White, not flesh: the head's colour comes from its texture, and a vertex
    # colour multiplied into a painted face darkens it twice over. The vertex
    # colour multiplies the texel, so the one thing it must be is neutral.
    paint(head, (1.0, 1.0, 1.0, 0.0), variation=0.0)

    # The hair is capped at the hairline rather than cut at it: the head's own
    # surface, pushed out, running from the crown down to a curve.
    warped, _ = _hair_shell(88, 34)
    hair = _capped_mesh("Hair", 72, 22, warped, face_uv.hairline)
    hair.parent = head
    parts.append(hair)

    # White, like the head: the hair samples its own painted region, and a vertex
    # colour on top of that is a second coat of paint.
    paint(hair, (1.0, 1.0, 1.0, 0.0), variation=0.0)

    # The face — eyes, brows, the fold beside a nostril, the lip — is a texture, not
    # geometry. Modelled out of domes it read as a mask at three metres, which is
    # what the first version of this head was; painted, it is what a face is made
    # of. The warp still cuts the sockets and the ridge those features sit in, so a
    # painted eye is an eye in a socket and not a decal on a ball.
    for side in (-1, 1):
        # An ear, flattened against the skull: the bulge turned outward, so the
        # dome's own X is its height and its own Y is its depth.
        ear = merge("Ear", [
            _dome_geo(0.030, (0.92, 0.50, 0.40), segments=12, rings=5),
        ])
        ear.parent = head
        ear.rotation_euler = (0.0, math.radians(side * 90.0), 0.0)
        ear.location = (side * 0.098, -0.010, EAR_Z)
        _pin_uv(ear, face_uv.face_uv_across(side * 0.086, EAR_Z))
        parts.append(ear)
        paint(ear, (1.0, 1.0, 1.0, 0.0), variation=0.0)

    # The pipe: a thin stem out of the corner of the mouth and a small bowl at the
    # far end of it. Measured against the head, not against a hand.
    stem = cylinder("Pipe", 0.006, 0.115, segments=8, axis="y")
    stem.parent = head
    stem.location = (0.034, 0.150, LIP_Z - 0.032)
    stem.rotation_euler = (math.radians(-35.0), 0.0, 0.0)
    parts.append(stem)
    paint(stem, MATERIALS["gun_dark"])

    bowl = merge("PipeBowl", [
        _cyl_geo(0.016, 0.028, segments=10, axis="z"),
        _dome_geo(0.016, (1.0, 1.0, 0.40), segments=10, rings=4, offset=(0.0, 0.0, 0.028)),
    ])
    bowl.parent = head
    bowl.location = (0.034, 0.196, LIP_Z - 0.076)
    parts.append(bowl)
    paint(bowl, MATERIALS["gun"])

    # ---- uniform trim: the button row the reference wears ------------------
    for button in range(5):
        stud = merge("Button", [
            _cyl_geo(0.0135, 0.014, segments=10, axis="y", offset=(0.0, 0.0, 0.0)),
        ])
        stud.location = (0.0, 0.196, HIP + 0.36 - (button * 0.100))
        parts.append(stud)
        paint(stud, gold, variation=0.02)

    # Every curved face smoothed, every corner left sharp.
    smooth(*parts)

    join(root, parts)
    return root

def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate MiVic cutscene personalities.")
    parser.add_argument("--out", required=True, help="Directory to write .glb files into.")
    args = parser.parse_args(argv)

    os.makedirs(args.out, exist_ok=True)
    written = []

    def emit(name, builder):
        clear_scene()
        builder()
        path = os.path.join(args.out, f"{name}.glb")
        export(path)
        written.append(path)
        print(f"wrote {os.path.basename(path)}  ({os.path.getsize(path) / 1024:.1f} KB)")

    # The file name describes a build, never a person. See the module docstring.
    emit("personality_elder", build_elder)

    print(f"done: {len(written)} personalities")


if __name__ == "__main__":
    main()
