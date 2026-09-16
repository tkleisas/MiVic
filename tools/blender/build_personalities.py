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
import figure_spec  # noqa: E402


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

# Where the head's features are comes from figure_spec, which the painter reads too.
#
# This block used to hold its own copies — a brow at 0.180, an eye at 0.164, a nose tip
# at 0.121 — and the painter held different ones, and the warp below used bare `v`
# literals that were a third set again. None of the three was read by anything that
# could disagree with it, so the sockets cut into the surface drifted twelve millimetres
# above the eyes painted on it and nothing said so. These are now the same numbers the
# paint is drawn to, and `figure_spec.v` converts them to the `v` the surface runs along.
BROW_Z = figure_spec.BROW_Z
EYE_Z = figure_spec.EYE_Z
NOSE_TIP_Z = figure_spec.NOSE_TIP_Z
LIP_Z = figure_spec.LIP_Z
EAR_Z = figure_spec.EAR_Z
MOUTH_Z = figure_spec.MOUTH_Z

# The same three heights as `v`, which is what the warp below actually indexes by. Named
# here rather than written into the warp as literals, which is how they came to disagree:
# a number in a comment and a number in an expression do not drift together.
BROW_V = figure_spec.v(BROW_Z)
EYE_V = figure_spec.v(EYE_Z)
NOSE_TIP_V = figure_spec.v(NOSE_TIP_Z)

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
    half_x, half_y, half_z = 0.082, 0.116, 0.136
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
        # A sine reaches its widest at the middle of its span, and a skull does not:
        # measured against the reference, the widest point is at about a third of the
        # way down — the parietal — and the temples are already turning in by the
        # middle. This is that, as a boost that peaks where the bone does.
        radius = math.sin(phi) ** 0.40
        radius *= 1.0 + (0.24 * math.exp(-(((v - 0.34) / 0.40) ** 2)))

        # and the jaw holds its width to the corner rather than tapering from the
        # cheekbone down, which is the other thing the profile said
        radius *= 1.0 + (0.20 * math.exp(-(((v - 0.80) / 0.16) ** 2)))
        radius /= 1.365

        # The crown itself comes to a point rather sooner than the parietal does.
        if crown_taper and v < 0.14:
            radius *= 0.70 + (0.30 * (v / 0.14) ** 0.6)

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

            zygomatic = math.exp(-((((dx - (side * 0.62)) / 0.26) ** 2) + (((v - 0.640) / 0.165) ** 2)))
            x += side * 0.040 * zygomatic
            y += 0.011 * zygomatic * front

            corner = math.exp(-((((abs(dx) - 0.70) / 0.30) ** 2) + (((v - 0.800) / 0.090) ** 2)))
            x += math.copysign(0.010 * corner, dx) if abs(dx) > 1e-9 else 0.0

            # The jowls. An old heavy face carries its soft tissue at the corners of
            # the jaw, and without them a wide jaw is only a wide jaw.
            jowl = math.exp(-((((abs(dx) - 0.66) / 0.26) ** 2) + (((v - 0.860) / 0.055) ** 2)))
            x += math.copysign(0.011 * jowl, dx) if abs(dx) > 1e-9 else 0.0
            y += 0.009 * jowl * front

        # Below the corner of the jaw the bone turns in towards the chin.
        # Measured, not guessed. The reference's skull is at full width high up
        # over the parietal, narrows gradually to about five sixths of that at the
        # temples and the jaw, and holds that to the chin — it never comes to a
        # point. Mine was widest at the cheekbones and tapered to nothing below,
        # which is what made the lower third of the face a long blank.
        middle = max(0.0, min(1.0, (v - 0.44) / 0.28))
        x *= 1.0 - (0.03 * middle)

        if v > 0.78:
            taper = 1.0 - (0.34 * ((v - 0.78) / 0.22) ** 1.3)
            x *= taper
            y *= 0.72 + (0.28 * taper)

        # Brow ridge: a shelf over the eyes, and the sockets cut in under it.
        brow = math.exp(-(((v - BROW_V) / 0.075) ** 2))
        y += 0.016 * brow * front

        for side in (-1.0, 1.0):
            socket = math.exp(-((((dx - (side * 0.40)) / 0.30) ** 2) + (((v - EYE_V) / 0.082) ** 2)))
            y -= 0.024 * socket

        # The nose. Not a ridge with a bulge on it: a bridge that narrows towards
        # the nasion, a tip that is its own ball of cartilage on the end of it, two
        # wings that flare at the bottom, a crease behind each of them, and an
        # underside that turns back in. A gaussian across the whole thing is a
        # smear, and a smear with paint on it is what the first four versions of
        # this face were.
        narrow = 0.105 + (0.105 * max(0.0, min(1.0, (v - 0.492) / 0.110)))
        bridge = math.exp(-((dx / narrow) ** 2))
        rise = 0.060 * math.exp(-(((v - 0.574) / 0.086) ** 2))
        y += rise * bridge * max(0.15, ny)

        tip = math.exp(-(((dx / 0.150) ** 2) + (((v - NOSE_TIP_V) / 0.030) ** 2)))
        y += 0.026 * tip * max(0.15, ny)

        for side in (-1.0, 1.0):
            wing = math.exp(-((((dx - (side * 0.225)) / 0.100) ** 2) + (((v - 0.626) / 0.040) ** 2)))
            y += 0.018 * wing * front

            crease = math.exp(-((((dx - (side * 0.355)) / 0.070) ** 2) + (((v - 0.628) / 0.052) ** 2)))
            y -= 0.012 * crease * front

        under = math.exp(-(((v - 0.654) / 0.016) ** 2)) * math.exp(-((dx / 0.26) ** 2))
        y -= 0.019 * under * front

        # The fine planes a face is actually made of, which a grid this size can
        # now hold: the crease above each lid, the philtrum under the nose, the
        # dimple in the chin, and the hollow at each temple.
        for side in (-1.0, 1.0):
            lid = math.exp(-((((dx - (side * 0.36)) / 0.20) ** 2) + (((v - 0.452) / 0.020) ** 2)))
            y -= 0.004 * lid * front

            wing_groove = math.exp(-((((dx - (side * 0.300)) / 0.045) ** 2) + (((v - 0.642) / 0.045) ** 2)))
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

        # Skin is not smooth. At 88 by 62 the grid holds a feature about three
        # millimetres across, which is the scale of the unevenness that makes a
        # cheek catch the light in patches instead of in one clean sweep — and a
        # clean sweep is what "it looks smoother than a face" means. A millimetre of
        # it, crossed at two angles so it never reads as a pattern.
        if front > 0.0:
            ripple = (
                math.sin((dx * 41.0) + (v * 57.0))
                * math.sin((v * 63.0) - (dx * 37.0))
            )
            y += 0.0011 * ripple * front

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

        # Combed back, not up, and thick over the crown rather than at the sides.
        # The portrait's hair leaves the forehead, rises a little and sweeps away
        # over the top: the volume is *behind* the front of the head, not balanced
        # on it. Lifting without that sweep is a toupee, which is what the last
        # attempt at this became, and lying flat is a cap, which is what it is now.
        thickness = 1.050 + (0.165 * math.exp(-(((v - 0.05) / 0.30) ** 2)))
        x *= thickness
        y *= thickness
        z = cz + ((z - cz) * 1.02)

        # Flat across the top. The portrait's hair is brushed back and lies level
        # over the crown, so its head is already two thirds of its full width at the
        # very top; a shell that follows the skull's dome comes to a point there.
        # Enough to take the point off the crown, not enough to make a slab. The
        # width profile is happy either way — it samples twelve rows and cannot see
        # the shape between two of them — but the eye can, and at 0.55 this read as
        # a flat cap rather than as hair lying over a skull.
        crown_fill = 1.0 + (0.30 * math.exp(-((v / 0.19) ** 2)))
        x *= crown_fill
        y *= crown_fill

        sweep = math.exp(-(((v - 0.08) / 0.34) ** 2))
        back = max(0.0, -y) / 0.11
        y -= 0.024 * sweep * back
        z += 0.013 * sweep

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


def _ring_geo(plan, inner, top, height, offset=(0.0, 0.0, 0.0), segments=20, power=0.6):
    """A hollow band: a collar, a cuff, a rim, a belt.

    `_prism_geo` makes a solid, and a collar is not a solid — it is a band with the
    neck through the middle of it. A box with a hole needed in it is a box with a
    neck sticking out of both ends, which is exactly what the collar looked like.
    `inner` is the inside wall as a fraction of the outside one.
    """
    ox, oy, oz = offset
    verts = []
    faces = []

    for level in (0, 1):
        sx = plan[0] * 0.5 * (top[0] ** level)
        sy = plan[1] * 0.5 * (top[1] ** level)
        z = oz + (height * level)

        for i in range(segments):
            angle = (2.0 * math.pi * i) / segments
            cosine, sine = math.cos(angle), math.sin(angle)
            cx = math.copysign(abs(cosine) ** power, cosine)
            cy = math.copysign(abs(sine) ** power, sine)
            verts.append((cx * sx + ox, cy * sy + oy, z))
            verts.append((cx * sx * inner + ox, cy * sy * inner + oy, z))

    for i in range(segments):
        j = (i + 1) % segments
        outer_low, inner_low = i * 2, (i * 2) + 1
        outer_high = (segments * 2) + (i * 2)
        inner_high = (segments * 2) + (i * 2) + 1

        j_outer_low, j_inner_low = j * 2, (j * 2) + 1
        j_outer_high = (segments * 2) + (j * 2)
        j_inner_high = (segments * 2) + (j * 2) + 1

        faces.append((outer_low, j_outer_low, j_outer_high, outer_high))
        faces.append((inner_high, j_inner_high, j_inner_low, inner_low))
        faces.append((outer_high, j_outer_high, j_inner_high, inner_high))
        faces.append((inner_low, j_inner_low, j_outer_low, outer_low))

    return verts, faces


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


#: How many parts each name is allowed. Anything not named here is expected once.
#: `Ear` and `Hand` and the rest are built in a loop over two sides, so they are two.
expected_parts = {
    "Ear": 2,
    "HandLeft": 1,
    "HandRight": 1,
    "ArmLeft": 1,
    "ArmRight": 1,
    "ForearmLeft": 1,
    "ForearmRight": 1,
    "LegLeft": 1,
    "LegRight": 1,
    "ShinLeft": 1,
    "ShinRight": 1,
    "Boot": 2,
    "PlacketSeam": 2,
    "Board": 2,
    "BoardEdge": 4,
    "CollarTab": 2,
    "Collar": 2,
    "CollarEdge": 2,
    "Button": 5,
    "Star": 1,
    "Ribbon": 1,
}


def check_no_duplicate_definitions():
    """Refuses to build if this file defines the same function twice.

    It did: there were two complete `_hair_shell` functions, the second silently
    overrode the first, and every change written into the first — including the one
    that took the toupee off — went into code that nothing called. The part-count
    guard catches a duplicate *part*; this catches the duplicate *source* that
    produces one, and it would have caught the hair two rounds after it happened.
    """
    import re

    source = open(os.path.abspath(__file__), encoding="utf-8").read()
    seen = {}

    for name in re.findall(r"^def ([A-Za-z_][A-Za-z0-9_]*)", source, re.MULTILINE):
        seen[name] = seen.get(name, 0) + 1

    duplicates = sorted(name for name, count in seen.items() if count > 1)

    if duplicates:
        raise RuntimeError(
            "this file defines the same function more than once: "
            + ", ".join(duplicates)
            + ". The last definition wins and the others are dead code."
        )


def build_elder():
    """The man at the desk: an old soldier in a plain tunic, a moustache and a pipe.

    Built from the same kit the soldiers are and round where a soldier is square:
    a domed skull over a tapered jaw, a trunk that widens into a domed shoulder
    line, a greatcoat flaring to a rounded hem, and cylindrical limbs. A soldier is
    read at forty metres as a helmet and a shoulder line, and every corner he has
    survives that. A personality is the whole frame at three metres, and at three
    metres every corner is a corner.
    """
    check_no_duplicate_definitions()

    root = bpy.data.objects.new("elder", None)
    bpy.context.collection.objects.link(root)

    parts = []

    shoulders = 0.455
    bulk = 1.18
    leg_half = shoulders * 0.25

    # The arms sit just outside the coat's shoulder, near enough to the body that
    # the coat reads as something he is wearing: a gesture is the only motion a
    # rigid-part figure has, and an arm out in the air has none to give it weight.
    arm_x = (shoulders * 0.40) - 0.006

    # Sampled off the reference the brief was modelled from: an olive tunic with
    # gold buttons, and a man whose hair, brows and moustache are all one dark
    # grey-brown. An old soldier, not a white-haired one — the first version got
    # that wrong and painted him with the hair of a man twenty years older.
    tunic = (0.24, 0.25, 0.18, 0.60)
    tunic_lit = (0.30, 0.31, 0.23, 0.60)
    tunic_dark = (0.17, 0.18, 0.13, 0.34)
    trouser = (0.21, 0.22, 0.16, 0.30)
    boot = (0.10, 0.10, 0.10, 0.05)
    belt_colour = (0.14, 0.11, 0.08, 0.10)
    collar_red = (0.46, 0.08, 0.06, 0.00)
    gold = (0.66, 0.53, 0.21, 0.00)
    moustache_colour = (0.20, 0.16, 0.13, 0.00)
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

    trunk = _prism_geo((shoulders * 0.78, 0.33 * bulk), (1.10, 1.08), 0.22, offset=(0.0, 0.0, HIP + 0.155), power=0.52)
    crown = _dome_geo(shoulders * 0.46, (1.0, 0.66, 0.78), segments=22, rings=8, offset=(0.0, 0.0, HIP + 0.25))
    chest = merge("Body", [trunk, crown])
    _wrap_uv(chest)
    parts.append(chest)
    paint(chest, tunic, variation=0.09)

    # The placket: the strip the buttons sit on, standing a few millimetres proud
    # of the tunic, because a row of buttons floating on a flat chest reads as
    # beads rather than as a fastening.
    # The placket, and the two seams either side of it. The portrait's tunic front
    # carries a lit strip down the middle with a shadow each side of it, and that
    # vertical line is most of what says "a coat done up" rather than "a box".
    placket = box("Placket", (0.058, 0.020, 0.60), offset=(0.0, 0.0, 0.0))
    placket.location = (0.0, 0.032, HIP + 0.16)
    parts.append(placket)
    paint(placket, tunic_lit, variation=0.02)

    for edge in (-1, 1):
        seam = box("PlacketSeam", (0.009, 0.017, 0.60), offset=(0.0, 0.0, 0.0))
        seam.location = (edge * 0.033, 0.027, HIP + 0.16)
        parts.append(seam)
        paint(seam, tunic_dark, variation=0.02)

    # A fall collar, not a stand one. The tunic's collar turns down over the
    # shoulders and is open at the throat, with the two tabs laid on it — and those
    # tabs are the largest piece of colour on the chest, which is the thing that
    # says "officer" from across a room. A stand collar with two small squares on it
    # is a different garment entirely, which is what the portrait showed.
    for side in (-1, 1):
        angle = (math.radians(-30.0), math.radians(side * 22.0), math.radians(side * -14.0))

        # The collar has a cross-section, not just a shape: it stands up the neck,
        # folds over, and falls down the chest. Two boxes in one part — the stand at
        # the back of the fold and the fall in front of it — which is the last thing
        # the flap was missing. The fall is unchanged from the version that measures
        # right, so the fold can only add to it.
        flap = merge("Collar", [
            _box_geo((0.106, 0.082, 0.015), offset=(0.0, 0.0, 0.0), taper=0.70),
            _box_geo((0.098, 0.024, 0.020), offset=(0.0, 0.030, 0.020), taper=0.94),
        ])
        flap.location = (side * 0.030, 0.040, SHOULDER + 0.058)
        flap.rotation_euler = angle
        _wrap_uv(flap)
        parts.append(flap)
        paint(flap, tunic, variation=0.03)

        # The tab, with its gold edging as a thin strip along the outer side.
        tab = merge("CollarTab", [
            _box_geo((0.090, 0.062, 0.013), offset=(0.0, 0.0, 0.0), taper=0.74),
        ])
        tab.location = (side * 0.032, 0.048, SHOULDER + 0.064)
        tab.rotation_euler = angle
        parts.append(tab)
        paint(tab, collar_red, variation=0.02)

        edge = merge("CollarEdge", [
            _box_geo((0.092, 0.007, 0.014), offset=(0.0, 0.0, 0.0)),
        ])
        edge.location = (side * 0.032, 0.012, SHOULDER + 0.064)
        edge.rotation_euler = angle
        parts.append(edge)
        paint(edge, gold, variation=0.02)

        # A shoulder board: a strip that runs from the collar out over the shoulder,
        # narrowing towards the collar, tipped down the slope of the shoulder and
        # edged in the regiment's red. It was a flat plank of one width at one
        # height, which is a board lying on a shelf rather than on a man.
        board = merge("Board", [
            _box_geo((0.054, 0.148, 0.013), offset=(0.0, 0.0, 0.0), taper=0.66),
        ])
        board.location = (side * shoulders * 0.28, -0.010, HIP + 0.508)
        board.rotation_euler = (0.0, math.radians(side * 26.0), 0.0)
        _wrap_uv(board)
        parts.append(board)
        paint(board, gold, variation=0.03)

        for edge in (-1, 1):
            piping = merge("BoardEdge", [
                _box_geo((0.010, 0.150, 0.014), offset=(0.0, 0.0, 0.0), taper=0.66),
            ])
            piping.location = (
                side * (shoulders * 0.28 - (edge * 0.024)),
                -0.010,
                HIP + 0.508,
            )
            piping.rotation_euler = (0.0, math.radians(side * 26.0), 0.0)
            parts.append(piping)
            paint(piping, collar_red, variation=0.02)

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
        upper.location = (side * arm_x, 0.0, SHOULDER - 0.045)
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
        _cyl_geo(0.066, 0.15, segments=16, axis="z", offset=(0.0, 0.0, 0.01)),
    ])
    neck.location = (0.0, 0.0, HEAD_BASE - 0.075)
    parts.append(neck)
    paint(neck, FLESH_SHADE)

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

    # Every part that carries no texture layout of its own samples the corner of
    # whichever map it is given, and the face map's corner is the back of the head in
    # shadow. That is why the buttons, the shoulder boards and the star all rendered
    # brown: they are painted gold, and then multiplied by a texel that is nearly
    # black. Anything without a layout is pinned to the lit bridge of the nose, where
    # the map is brightest and a vertex colour therefore shows as it was authored.
    lit = face_uv.face_uv_across(0.0, 0.118)

    for part in parts:
        layer = part.data.uv_layers.get("UVMap")
        laid_out = layer is not None and any(
            abs(loop.uv[0]) > 1e-6 or abs(loop.uv[1]) > 1e-6 for loop in layer.data
        )

        if not laid_out:
            _pin_uv(part, lit)

    # Nothing on this figure is meant to appear twice. A block of this script that
    # is replaced and not deleted leaves a twin behind, and the model wears both —
    # which has happened three times now: the moustache, the uniform's trim, and a
    # second collar round the neck. The check costs nothing and it fails the build
    # rather than the render.
    counts = {}
    for part in parts:
        stem = part.name.split(".")[0]
        counts[stem] = counts.get(stem, 0) + 1

    for stem, count in counts.items():
        if count > expected_parts.get(stem, 1):
            raise RuntimeError(
                f"{count} parts named '{stem}'; at most {expected_parts.get(stem, 1)} "
                "is expected. A block that was replaced was probably not deleted."
            )

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
