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
from build_vehicles import _box_geo, _cyl_geo, _dome_geo, _frustum_geo, _link, merge  # noqa: E402


# --------------------------------------------------------------------------
# One man, in metres, against a 1.78 m figure. Stocky rather than tall: the
# archetype reads as heavy, and weight is the one thing a low-poly body can say
# from across a room.
# --------------------------------------------------------------------------

ANKLE = 0.09
KNEE = 0.47
HIP = 0.90
SHOULDER = 1.36
HEAD_BASE = 1.46

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

FLESH = MATERIALS["flesh"]
FLESH_SHADE = (0.52, 0.39, 0.30, 0.00)


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
    faces = []

    def vertex(ring, seg):
        key = (ring, seg % segments)

        if key not in index:
            u = (seg % segments) / segments
            v = ring / rings
            index[key] = len(verts)
            verts.append(warp(u, v))

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

    return _link(name, verts, faces)


def _head_surface(segments=36, rings=26):
    """One face, as a function of where you are on a head."""
    half_x, half_y, half_z = 0.099, 0.113, 0.139
    centre_z = 0.150

    def warp(u, v):
        phi = math.pi * v
        theta = 2.0 * math.pi * u

        nx = math.sin(phi) * math.cos(theta)
        ny = math.sin(phi) * math.sin(theta)
        nz = math.cos(phi)

        x = nx * half_x
        y = ny * half_y
        z = centre_z + (nz * half_z)

        dx = nx                     # -1 left .. +1 right
        front = max(0.0, ny)        # 1 at the face, 0 at the sides and back

        # The jaw narrows towards the chin, which is what makes a head a head
        # rather than an egg; the back of the skull keeps its width.
        if v > 0.62:
            taper = 1.0 - (0.30 * ((v - 0.62) / 0.38) ** 1.4)
            x *= taper
            y *= 0.55 + (0.45 * taper)

        # Brow ridge: a shelf over the eyes, and the sockets cut in under it.
        brow = math.exp(-(((v - 0.405) / 0.075) ** 2))
        y += 0.014 * brow * front

        for side in (-1.0, 1.0):
            socket = math.exp(-((((dx - (side * 0.40)) / 0.30) ** 2) + (((v - 0.495) / 0.085) ** 2)))
            y -= 0.020 * socket

            cheek = math.exp(-((((dx - (side * 0.52)) / 0.42) ** 2) + (((v - 0.615) / 0.115) ** 2)))
            y += 0.011 * cheek

        # The nose: a bridge that runs from between the brows to a tip, then the
        # underside turns back in.
        # A nose is a wedge, not a blade: the ridge is the width of a nose, and
        # narrower than that it renders as a fin down the middle of the face.
        if abs(dx) < 0.40:
            across = math.exp(-((dx / 0.235) ** 2))
            if v < 0.60:
                profile = 0.030 * math.exp(-(((v - 0.560) / 0.120) ** 2))
            else:
                # The tip, then the underside turning back in: a nose that runs on
                # past the tip hangs over the mouth and splits the moustache.
                profile = 0.040 * math.exp(-(((v - 0.622) / 0.050) ** 2))
            y += profile * across * max(0.15, ny)

        # The wings of the nose, either side of the tip.
        for side in (-1.0, 1.0):
            wing = math.exp(-((((dx - (side * 0.235)) / 0.13) ** 2) + (((v - 0.655) / 0.055) ** 2)))
            y += 0.010 * wing * front

        # Lips, with the crease between them, and a chin under both.
        mouth = math.exp(-(((v - 0.735) / 0.045) ** 2)) * math.exp(-((dx / 0.42) ** 2))
        y += 0.007 * mouth * front
        y -= 0.009 * math.exp(-(((v - 0.762) / 0.018) ** 2)) * math.exp(-((dx / 0.34) ** 2))

        chin = math.exp(-(((v - 0.905) / 0.075) ** 2)) * math.exp(-((dx / 0.55) ** 2))
        y += 0.014 * chin * front

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
    half_scale = 1.055

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
        # the face, 1 at the nape. It climbs from the brow to the neck, which is
        # what a receding hairline is, and a dip at the temples gives the two
        # corners that say "old" instead of "shaved".
        around = min(abs(u - 0.25), 1.0 - abs(u - 0.25)) * 2.0
        hairline = 0.13 + (0.52 * (around ** 0.85))
        hairline -= 0.06 * math.exp(-(((around - 0.30) / 0.16) ** 2))
        return v < hairline

    return warped, keep


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
    arm_x = (shoulders * 0.40) + (0.055 * bulk)

    tunic = (0.34, 0.37, 0.17, 0.60)
    tunic_dark = (0.24, 0.27, 0.11, 0.34)
    trouser = (0.28, 0.31, 0.14, 0.30)
    boot = (0.10, 0.10, 0.10, 0.05)
    belt = (0.17, 0.14, 0.11, 0.10)
    hair_colour = (0.62, 0.60, 0.57, 0.03)
    collar_red = (0.40, 0.07, 0.06, 0.00)
    eye_white = (0.80, 0.78, 0.74, 0.00)
    iris_colour = (0.16, 0.14, 0.12, 0.00)
    hair_shadow = (0.50, 0.48, 0.45, 0.03)
    gold = (0.74, 0.60, 0.24, 0.00)

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

    # ---- torso: a body of revolution pressed flat  --------------------------
    pelvis = merge("Pelvis", [
        _prism_geo((shoulders * 0.72, 0.28 * bulk), (1.04, 1.02), 0.20, offset=(0.0, 0.0, HIP + 0.02)),
    ])
    parts.append(pelvis)
    paint(pelvis, trouser)

    waist = merge("Waist", [
        _prism_geo((shoulders * 0.76, 0.30 * bulk), (1.02, 1.0), 0.065, offset=(0.0, 0.0, HIP + 0.20), segments=20),
    ])
    parts.append(waist)
    paint(waist, belt)

    # The trunk is the tunic the coat is worn open over: the same shape, a little
    # inside the coat, so the coat reads as a coat and not as the whole man.
    trunk = _prism_geo((shoulders * 0.82, 0.30 * bulk), (1.06, 1.04), 0.23, offset=(0.0, 0.0, HIP + 0.19))
    crown = _dome_geo(shoulders * 0.47, (1.0, 0.62, 0.40), segments=16, rings=5, offset=(0.0, 0.0, HIP + 0.42))
    chest = merge("Body", [trunk, crown])
    parts.append(chest)
    paint(chest, tunic, variation=0.05)

    # A greatcoat that flares to a rounded hem: wider at the bottom than at the
    # top, which is the one line on a figure that says "coat" and not "box".
    coat = merge("Coat", [
        _prism_geo((shoulders * 1.30, 0.46 * bulk), (0.72, 0.74), 0.78, offset=(0.0, 0.0, HIP - 0.44)),
    ])
    parts.append(coat)
    paint(coat, tunic, variation=0.05)

    skirt = merge("CoatSkirt", [
        _dome_geo(shoulders * 0.65, (1.0, 0.62, 0.22), segments=16, rings=3, offset=(0.0, 0.0, HIP - 0.42)),
    ])
    parts.append(skirt)
    paint(skirt, tunic_dark, variation=0.05)

    # Shoulder boards and one medal: two bright notes that say "rank" at a
    # distance, where a face says nothing at all.
    for side in (-1, 1):
        board = box("Board", (0.058, 0.145, 0.020), offset=(0.0, 0.0, 0.0))
        board.location = (side * shoulders * 0.30, -0.012, HIP + 0.475)
        board.rotation_euler = (0.0, math.radians(side * 11.0), 0.0)
        parts.append(board)
        paint(board, gold, variation=0.03)

    medal = box("Medal", (0.042, 0.016, 0.055), offset=(0.0, 0.0, 0.0))
    medal.location = (-shoulders * 0.20, 0.196, HIP + 0.34)
    parts.append(medal)
    paint(medal, gold, variation=0.02)

    # ---- arms: cylinders, with the right forearm carried forward as if it held
    # the pipe. The director can raise either one from here. -------------------
    for side, tag in ((-1, "Left"), (1, "Right")):
        upper = merge("Arm" + tag, [
            _cyl_geo(0.064 * bulk, 0.30, segments=10, axis="z", offset=(0.0, 0.0, -0.15)),
        ])
        upper.location = (side * arm_x, 0.0, SHOULDER - 0.02)
        parts.append(upper)
        paint(upper, tunic)

        fore = merge("Forearm" + tag, [
            _cyl_geo(0.056 * bulk, 0.28, segments=10, axis="z", offset=(0.0, 0.0, -0.14)),
        ])
        fore.parent = upper
        fore.location = (0.0, 0.0, -0.30)

        if side == 1:
            # The pipe hand: bent up and in, so the elbow reads as a corner and
            # not as a sleeve hanging straight.
            fore.rotation_euler = (math.radians(-58.0), 0.0, 0.0)

        parts.append(fore)
        paint(fore, tunic)

        hand = merge("Hand" + tag, [
            _frustum_geo((0.10, 0.125), (0.86, 0.90), 0.10, offset=(0.0, 0.0, -0.10)),
        ])
        hand.parent = fore
        hand.location = (0.0, 0.0, -0.28)
        parts.append(hand)
        paint(hand, FLESH, variation=0.04)

    # ---- head: one sculpted surface, not a stack of boxes. The features are the
    # warp of the grid; the eyes, brows and moustache are set into it as their own
    # parts so they can be their own colour. Geometry is in the neck's space. ----
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

    head = _grid_mesh("Head", 36, 26, _head_surface(36, 26))
    head.parent = neck
    parts.append(head)
    paint(head, FLESH, variation=0.0)

    warped, keep = _hair_shell(48, 30)
    hair = _grid_mesh("Hair", 48, 30, warped, keep)
    hair.parent = head
    parts.append(hair)
    paint(hair, hair_colour, variation=0.0)

    for side in (-1, 1):
        # An eyeball in the socket the head was warped to make: a sclera with an
        # iris a little proud of it. Two domes of two colours, because there is no
        # texture on this renderer and a single dark bead reads as a hole.
        sclera = _face_dome(
            "Eye", 0.023, 1.0, 0.92, 0.55,
            (side * 0.036, 0.085, EYE_Z),
        )
        sclera.parent = head
        parts.append(sclera)
        paint(sclera, eye_white, variation=0.02)

        iris = _face_dome(
            "Iris", 0.0100, 1.0, 1.0, 0.85,
            (side * 0.036, 0.095, EYE_Z),
            rings=4,
        )
        iris.parent = head
        parts.append(iris)
        paint(iris, iris_colour, variation=0.02)

        # A brow riding the ridge, thick at the nose and swept out over the eye.
        ridge = _face_dome(
            "Brow", 0.031, 1.15, 0.45, 0.45,
            (side * 0.043, 0.110, BROW_Z - 0.004),
            droop=side * 7.0,
            sweep=side * -13.0,
            rings=4,
        )
        ridge.parent = head
        parts.append(ridge)
        paint(ridge, hair_shadow, variation=0.03)

        # Half a moustache: a wing that meets its pair at the parting, runs out
        # over the lip and hangs at the tip.
        sweep_part = _face_dome(
            "Moustache", 0.034, 1.75, 0.44, 0.58,
            (side * 0.026, 0.088, LIP_Z + 0.012),
            droop=side * 12.0,
            sweep=side * -8.0,
            rings=4,
        )
        sweep_part.parent = head
        parts.append(sweep_part)
        paint(sweep_part, hair_colour, variation=0.03)

        # An ear, flattened against the skull, now that the hair has left the side
        # of the head alone.
        # An ear, flattened against the skull: the bulge turned outward, so the
        # dome's own X is its height and its own Y is its depth.
        ear = merge("Ear", [
            _dome_geo(0.030, (0.92, 0.50, 0.40), segments=12, rings=5),
        ])
        ear.parent = head
        ear.rotation_euler = (0.0, math.radians(side * 90.0), 0.0)
        ear.location = (side * 0.098, -0.010, EAR_Z)
        parts.append(ear)
        paint(ear, FLESH_SHADE, variation=0.0)

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

    # ---- uniform trim: a button row down the tunic and a star on the chest -----
    for button in range(6):
        stud = merge("Button", [
            _cyl_geo(0.014, 0.016, segments=10, axis="y", offset=(0.0, 0.0, 0.0)),
        ])
        stud.location = (0.0, 0.198, HIP + 0.34 - (button * 0.062))
        parts.append(stud)
        paint(stud, gold, variation=0.02)

    star = merge("Star", [
        _cyl_geo(0.030, 0.014, segments=5, axis="y", offset=(0.0, 0.0, 0.0)),
    ])
    star.location = (-shoulders * 0.26, 0.200, HIP + 0.30)
    parts.append(star)
    paint(star, gold, variation=0.02)

    ribbon = box("Ribbon", (0.042, 0.014, 0.030), offset=(0.0, 0.0, 0.0))
    ribbon.location = (-shoulders * 0.26, 0.198, HIP + 0.345)
    parts.append(ribbon)
    paint(ribbon, collar_red, variation=0.02)

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
