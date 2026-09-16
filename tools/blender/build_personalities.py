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
EYE_Z = 0.151       # v = 0.50
NOSE_TIP_Z = 0.093  # v = 0.635
LIP_Z = 0.068       # v = 0.70
EAR_Z = 0.146       # v = 0.53
MOUTH_Z = 0.048     # v = 0.76, the crease between the lips

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


def _head_surface(segments=36, rings=26):
    """One face, as a function of where you are on a head."""
    half_x, half_y, half_z = 0.100, 0.118, 0.134
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

        # The second flattens the plan from a circle into a rounded rectangle, so
        # the face is a face and not the front of a ball.
        cosine, sine = math.cos(theta), math.sin(theta)
        squircle = (abs(cosine) ** 3.2 + abs(sine) ** 3.2) ** (-1.0 / 3.2)

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
            temple = math.exp(-((((dx - (side * 0.86)) / 0.30) ** 2) + (((v - 0.40) / 0.10) ** 2)))
            x -= side * 0.006 * temple

            zygomatic = math.exp(-((((dx - (side * 0.60)) / 0.24) ** 2) + (((v - 0.630) / 0.080) ** 2)))
            x += side * 0.011 * zygomatic
            y += 0.009 * zygomatic * front

            corner = math.exp(-((((abs(dx) - 0.70) / 0.30) ** 2) + (((v - 0.800) / 0.090) ** 2)))
            x += math.copysign(0.013 * corner, dx) if abs(dx) > 1e-9 else 0.0

        # Below the corner of the jaw the bone turns in towards the chin.
        if v > 0.80:
            taper = 1.0 - (0.34 * ((v - 0.80) / 0.20) ** 1.3)
            x *= taper
            y *= 0.66 + (0.34 * taper)

        # Brow ridge: a shelf over the eyes, and the sockets cut in under it.
        brow = math.exp(-(((v - 0.405) / 0.075) ** 2))
        y += 0.016 * brow * front

        for side in (-1.0, 1.0):
            socket = math.exp(-((((dx - (side * 0.40)) / 0.30) ** 2) + (((v - 0.495) / 0.085) ** 2)))
            y -= 0.024 * socket

        # The nose: a bridge that narrows towards the brow, a tip, and the
        # underside turning back in. A nose is a wedge, not a blade — narrower than
        # this it renders as a fin down the middle of the face.
        if abs(dx) < 0.34:
            across = math.exp(-((dx / 0.205) ** 2))
            if v < 0.58:
                profile = 0.027 * math.exp(-(((v - 0.545) / 0.110) ** 2))
            else:
                profile = 0.043 * math.exp(-(((v - 0.618) / 0.048) ** 2))
            y += profile * across * max(0.15, ny)

        # The wings of the nose, either side of the tip, and the crease beside
        # them that a nose sits in.
        for side in (-1.0, 1.0):
            wing = math.exp(-((((dx - (side * 0.225)) / 0.115) ** 2) + (((v - 0.648) / 0.050) ** 2)))
            y += 0.012 * wing * front

            fold = math.exp(-((((dx - (side * 0.400)) / 0.115) ** 2) + (((v - 0.690) / 0.075) ** 2)))
            y -= 0.009 * fold * front

        # Lips, with the crease between them, and a chin under both.
        mouth = math.exp(-(((v - 0.735) / 0.045) ** 2)) * math.exp(-((dx / 0.42) ** 2))
        y += 0.008 * mouth * front
        y -= 0.011 * math.exp(-(((v - 0.762) / 0.017) ** 2)) * math.exp(-((dx / 0.34) ** 2))

        chin = math.exp(-(((v - 0.900) / 0.062) ** 2)) * math.exp(-((dx / 0.48) ** 2))
        y += 0.018 * chin * front

        # The crease under the lower lip, which is what makes a chin a chin
        # instead of the place the jaw stops.
        y -= 0.009 * math.exp(-(((v - 0.845) / 0.022) ** 2)) * math.exp(-((dx / 0.30) ** 2))

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
    warp = _head_surface(segments, rings)
    half_scale = 1.05

    def warped(u, v):
        x, y, z = warp(u, v)
        # Push the shell out from the head's centre, and sweep the back up into a
        # comb-back: a receding hairline is the shape that says "old" without a caption.
        cz = 0.150
        x *= half_scale
        y *= half_scale
        z = cz + ((z - cz) * 1.02)
        y -= 0.004 * (1.0 - v)
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
    tunic = (0.36, 0.35, 0.17, 0.60)
    tunic_dark = (0.27, 0.27, 0.12, 0.34)
    trouser = (0.31, 0.30, 0.16, 0.30)
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
        parts.append(thigh)
        paint(thigh, trouser)

        shin = merge(f"Shin{tag}", [
            _cyl_geo(0.084 * bulk, KNEE - ANKLE, segments=10, axis="z", offset=(0.0, 0.0, -(KNEE - ANKLE) * 0.5)),
        ])
        shin.parent = thigh
        shin.location = (0.0, 0.0, -(HIP - KNEE))
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
    parts.append(skirt)
    paint(skirt, tunic, variation=0.04)

    belt = merge("Belt", [
        _prism_geo((shoulders * 0.77, 0.30 * bulk), (1.0, 1.0), 0.055, offset=(0.0, 0.0, HIP + 0.10), power=0.52),
    ])
    parts.append(belt)
    paint(belt, belt_colour)

    trunk = _prism_geo((shoulders * 0.78, 0.30 * bulk), (1.16, 1.06), 0.24, offset=(0.0, 0.0, HIP + 0.155), power=0.52)
    crown = _dome_geo(shoulders * 0.52, (1.0, 0.62, 0.26), segments=20, rings=6, offset=(0.0, 0.0, HIP + 0.35))
    chest = merge("Body", [trunk, crown])
    parts.append(chest)
    paint(chest, tunic, variation=0.04)

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
    collar.location = (0.0, 0.0, SHOULDER + 0.012)
    parts.append(collar)
    paint(collar, tunic, variation=0.03)

    for side in (-1, 1):
        tab = box("CollarTab", (0.030, 0.016, 0.036), offset=(0.0, 0.0, 0.0))
        tab.location = (side * 0.030, 0.062, SHOULDER + 0.036)
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
    parts.append(collar)
    paint(collar, tunic)

    for side in (-1, 1):
        tab = box("CollarTab", (0.046, 0.022, 0.040), offset=(0.0, 0.0, 0.0))
        tab.parent = neck
        tab.location = (side * 0.040, 0.070, 0.052)
        parts.append(tab)
        paint(tab, collar_red, variation=0.02)

    head = _grid_mesh("Head", 52, 36, _head_surface(52, 36))
    head.parent = neck
    parts.append(head)

    # White, not flesh: the head's colour comes from its texture, and a vertex
    # colour multiplied into a painted face darkens it twice over. The vertex
    # colour multiplies the texel, so the one thing it must be is neutral.
    paint(head, (1.0, 1.0, 1.0, 0.0), variation=0.0)

    # The hair is capped at the hairline rather than cut at it: the head's own
    # surface, pushed out, running from the crown down to a curve.
    warped, _ = _hair_shell(76, 30)
    hair = _capped_mesh("Hair", 72, 22, warped, face_uv.hairline)
    hair.parent = head
    parts.append(hair)
    paint(hair, hair_colour, variation=0.0)

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
        parts.append(ear)
        paint(ear, FLESH, variation=0.0)

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
