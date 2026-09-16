"""Paint the face texture the personality heads sample.

A face is not geometry. Modelled out of domes and boxes it reads as a mask, which is
what the first three versions of this figure were: an iris is a colour, an eyelid is a
line, a lip is a boundary between two reds, and none of those have a silhouette to
model. What a shape can carry is the skull — the brow shelf, the sockets under it, the
nose, the jaw — and that is what the generator builds. Everything else is here.

The image is in the head's own texture coordinates, which `face_uv` is the single
definition of: `u` runs around the head with the face at 0.5, `v` runs from the crown
at 0 to under the chin at 1. So a feature is placed by the head-local metres it sits
at — an eye is 3.7 cm to the left of the middle and 14.5 cm above the neck — and this
file never needs to know how the mesh was built.

    python3 tools/paint_personalities.py --out src/MiVic.Game/Content/Models/Generated

Deterministic: every value here is a constant or a function of the pixel, so a run
that changes nothing leaves the committed PNG byte for byte identical. The models are
committed the same way and for the same reason — a diff should mean the art changed.
"""

import argparse
import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender"))

import face_uv  # noqa: E402

WIDTH = 2048
HEIGHT = 1536

# The same palette the generator paints the head's vertex colours with, so the
# painted face and the shaded skull agree where the texture ends.
SKIN = (208, 124, 101)
SKIN_LIT = (234, 152, 128)
SKIN_SHADE = (138, 90, 72)
SKIN_WARM = (226, 112, 88)
HAIR = (50, 45, 40)
HAIR_LIT = (76, 70, 63)
HAIR_DARK = (30, 27, 24)
BROW = (58, 48, 40)
EYE_WHITE = (222, 216, 208)
IRIS = (74, 52, 34)
IRIS_DARK = (44, 30, 20)
PUPIL = (18, 14, 12)
LID = (128, 92, 70)
LIP = (122, 74, 66)
LIP_DARK = (96, 52, 46)
SHADOW = (104, 76, 60)


def px(texture_u, texture_v):
    """Image coordinates for a texture coordinate. The origin is the top left."""
    return texture_u * WIDTH, texture_v * HEIGHT


def at(x, z):
    """Image coordinates for a point on the face, in head-local metres."""
    return px(*face_uv.face_uv_across(x, z))


def ellipse(draw, x, z, half_width, half_height, fill):
    """A patch of the face, given in metres across and metres up from the neck."""
    cx, cy = at(x, z)
    _, top = at(x, z + half_height)
    _, bottom = at(x, z - half_height)
    left, _ = at(x + half_width, z)

    draw.ellipse([left, top, (2 * cx) - left, bottom], fill=fill)


def polygon(draw, points, fill):
    """A patch of the face with corners, given in metres.

    Ellipses are what most of a face is — a cheek, a socket, a bag under an eye —
    but a moustache is not: it has a top edge under the nose, a bottom edge over
    the lip and two corners at the ends, and a shape with corners drawn out of
    ellipses is a haze.
    """
    draw.polygon([at(x, z) for x, z in points], fill=fill)


def blur(layer, radius):
    return layer.filter(ImageFilter.GaussianBlur(radius))


def noise(x, y, salt=0.0):
    """A stable 0..1 value for a pixel, so skin has grain and hair has strands."""
    value = math.sin((x * 12.9898) + (y * 78.233) + salt) * 43758.5453

    return value - math.floor(value)


def paint_skin(image):
    """Skin: a base tone, the light across it, warmth where the blood is, and grain.

    Three things are going on and they are separable. The *light* falls from the
    upper left, so the far cheek turns away — the reference carries a shadow down
    the whole of its right side, and a surface lit by its own normal cannot, because
    every normal across a cheekbone points almost the same way. The *warmth* is
    where the blood is near the surface: the cheeks, the nose, the ears. And the
    *shadows* are cooler than the light, not merely darker — skin in shadow goes
    grey-blue, and skin shaded by turning a brown down is mud.
    """
    pixels = image.load()

    for y in range(HEIGHT):
        v = y / HEIGHT

        light = 1.0 - (0.34 * max(0.0, (v - 0.52) / 0.48) ** 1.0)
        light += 0.11 * max(0.0, (0.55 - v) / 0.55)

        # The warm band across the middle of the face: cheeks and nose.
        warmth = math.exp(-(((v - 0.44) / 0.16) ** 2))

        for x in range(WIDTH):
            u = x / WIDTH
            grid_u = face_uv.unwrap_u(u)
            across = (grid_u - 0.25) / 0.25 if grid_u < 0.5 else (0.75 - grid_u) / 0.25

            shade = light * (1.0 - (0.56 * min(1.0, abs(across)) ** 1.35))
            shade *= 1.0 - (0.28 * max(0.0, across))

            # Warm in the light, cool in the shadow, and both of them only a little:
            # a face painted hard in two colours is a clown's.
            local_warm = warmth * (1.0 - (0.5 * min(1.0, abs(across))))
            red = SKIN[0] * shade * (1.0 + (0.06 * local_warm))
            green = SKIN[1] * shade * (1.0 - (0.02 * local_warm))
            blue = SKIN[2] * shade * (1.0 - (0.10 * local_warm))
            blue = blue * (1.0 + (0.10 * max(0.0, across))) + (6.0 * max(0.0, across))

            # Skin varies at every scale, and one frequency of it is a texture
            # rather than a surface. There are four here: blotches across the whole
            # face, the mottling of a cheek, a fine grain, and the pixel noise that
            # keeps the whole thing from banding. The broad one is the one that
            # matters — it is what makes a face look lived in rather than filled in.
            broad = noise(x / 190.0, y / 130.0, 3.0) - 0.5
            mid = noise(x / 47.0, y / 31.0, 17.0) - 0.5
            fine = noise(x / 11.0, y / 9.0, 41.0) - 0.5
            speck = (noise(x // 3, y // 3, 61.0) - 0.5) * 0.5

            grain = 1.0 + (0.075 * broad) + (0.050 * mid) + (0.030 * fine) + (0.045 * speck)
            # and the broad blotches carry colour as well as brightness: a patch of
            # skin is redder or greyer than its neighbour, not only lighter
            red *= grain * (1.0 + (0.045 * broad))
            green *= grain
            blue *= grain * (1.0 - (0.035 * broad))

            pixels[x, y] = (
                min(255, max(0, int(red))),
                min(255, max(0, int(green))),
                min(255, max(0, int(blue))),
            )


LOCK_PIXELS = 15


def paint_hair(image):
    """Hair above the hairline, as locks rather than as a noise field.

    A head of hair is a few hundred separate locks laid side by side, each one
    catching the light along its own length and dark between itself and the next.
    Noise gives the opposite: a field that is statistically right and reads as
    static, because nothing in it is a thing. So the locks are explicit — a fixed
    width in the map, each with its own tone from a hash of its index, lit down the
    middle and dark at both edges, and dark again where it leaves the forehead.
    """
    pixels = image.load()
    overlay = Image.new("RGB", image.size, HAIR)
    overlay_pixels = overlay.load()
    mask = Image.new("L", image.size, 0)
    mask_pixels = mask.load()

    for x in range(WIDTH):
        u = x / WIDTH
        hairline = face_uv.hairline_at(u)
        edge = hairline * HEIGHT

        # The same key as the face. The hair was lit by a band of sheen over the
        # crown and by nothing else, so it read as a flat grey cap however good its
        # locks were: a surface with no dark side has no lit side either.
        grid_u = face_uv.unwrap_u(u)
        across = (grid_u - 0.25) / 0.25 if grid_u < 0.5 else (0.75 - grid_u) / 0.25
        key = 1.0 - (0.30 * min(1.0, abs(across)) ** 1.4) - (0.20 * max(0.0, across))

        # One lock's own colour, and how far across it this pixel is: a lock is
        # lit down its middle and dark where it meets its neighbours.
        # Locks of uneven width, and each one its own tone out of three: a head of
        # hair is not one colour with a highlight on it, it is dark strands and
        # grey ones lying together, and which is which changes lock by lock.
        width = LOCK_PIXELS * (0.55 + (0.95 * noise(x // 37, 0.0, 19.0)))
        # Locks wander a little across the head, so the grid they are cut on does
        # not line up with the middle of the forehead and draw a seam down it.
        lock = int((x + (5.0 * math.sin(edge * 0.07))) / width)
        lock_tone = noise(lock, 0.0, 5.7)
        lock_grey = noise(lock, 3.3, 11.1)

        # Hair clumps: five or six locks lying together share a tone, so the head
        # reads as a mass of clumps rather than as a hundred independent strands of
        # the same width. Uniformity at the lock scale is what made it look printed.
        clump = noise(lock // 6, 1.9, 31.0)
        across_lock = ((x % width) / width) - 0.5
        rounded = 1.0 - ((abs(across_lock) * 2.0) ** 1.5)

        for y in range(HEIGHT):
            # A soft boundary rather than a cut: the hair mesh's own edge is stepped
            # by its grid, and a hard painted edge draws attention to it.
            depth = (edge - y) / 14.0

            if depth <= 0.0:
                continue

            mask_pixels[x, y] = int(min(255.0, depth * 255.0))

            # The sheen over the crown, and the roots dark where the hair leaves
            # the forehead — which is what makes it read as combed back rather than
            # as a cap.
            sheen = math.exp(-(((y - (edge - 70.0)) / 52.0) ** 2))
            roots = math.exp(-(((y - edge) / 30.0) ** 2))
            length_light = 1.0 - (0.22 * math.exp(-(((y - (edge * 0.35)) / (edge * 0.45 + 1.0)) ** 2)))

            if lock_grey > 0.88:
                base = HAIR_LIT
            elif lock_tone < 0.42:
                base = HAIR_DARK
            else:
                base = HAIR

            tone = (0.72 + (0.22 * lock_tone) + (0.22 * clump) + (0.18 * rounded) + (0.46 * sheen) - (0.16 * roots)) * key * length_light

            overlay_pixels[x, y] = (
                min(255, int(base[0] * tone)),
                min(255, int(base[1] * tone)),
                min(255, int(base[2] * tone)),
            )

    # Loose hairs crossing the hairline, so the boundary breaks up instead of
    # reading as the edge of a cap. Long ones, and few: the first attempt drew one
    # on most locks and covered the forehead in hair.
    fringe = Image.new("L", image.size, 0)
    fringe_draw = ImageDraw.Draw(fringe)

    for x in range(WIDTH):
        u = x / WIDTH
        lock = int(x // LOCK_PIXELS + (4.0 * noise(x // 53, 0.0, 23.0)))
        stray = noise(lock, 7.7, 31.0)

        if stray < 0.82:
            continue

        # Longer and finer than the first attempt, which drew short stubs that read
        # as a dotted line ruled across the forehead rather than as stray hairs.
        edge = face_uv.hairline_at(u) * HEIGHT
        length = 10.0 + (22.0 * (stray - 0.82) / 0.18)

        if (x % 2) == 0:
            continue

        fringe_draw.line([(x, edge - 6), (x + 1, edge + length)], fill=int(90 + (90 * stray)), width=1)

    image.paste(overlay, (0, 0), blur(mask, 4.5))

    # The stray hairs are painted as themselves, in a hair colour, rather than by
    # widening the mask the whole overlay goes through — which painted the
    # overlay's flat fill across the forehead and gave the man two hairlines.
    image.paste(overlay, (0, 0), blur(fringe, 1.0))


def layer():
    """A transparent layer the size of the texture, to paint one feature on."""
    return Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))


def over(image, painted, radius=0.0):
    """Composites a transparent layer, optionally softened, onto the face.

    Softening blurs the alpha as well as the colour, which is the whole reason these
    are RGBA layers and not a colour image with a separate mask: a soft edge on a
    shape is a soft edge on its own coverage, not a translucent wash of the layer's
    black background over the whole face.
    """
    if radius > 0.0:
        painted = painted.filter(ImageFilter.GaussianBlur(radius))

    image.paste(painted, (0, 0), painted)


def paint_form(image):
    """The soft shadows that give the face its planes: cheekbones, sockets, the nose.

    Placed against the warp rather than by eye. Every height here is a `v` the
    generator uses — the cheekbone at v = 0.63, the wings of the nose at 0.65, the
    crease beside it at 0.69, the mouth at 0.76 — read through the same ellipsoid,
    so a shadow sits in the hollow it is shading.
    """
    blush = layer()
    draw = ImageDraw.Draw(blush)

    for side in (-1, 1):
        ellipse(draw, side * 0.050, 0.106, 0.038, 0.032, (206, 96, 74, 52))
        ellipse(draw, side * 0.072, 0.122, 0.022, 0.030, (198, 96, 78, 40))

    ellipse(draw, 0.0, 0.112, 0.022, 0.020, (206, 100, 78, 66))
    ellipse(draw, 0.0, 0.030, 0.022, 0.010, (198, 100, 80, 62))
    over(image, blush, 12.0)

    shade = layer()
    draw = ImageDraw.Draw(shade)

    # Cheekbones, catching the light, and the hollow under them.
    for side in (-1, 1):
        ellipse(draw, side * 0.058, 0.108, 0.026, 0.022, (*SKIN_WARM, 40))
        ellipse(draw, side * 0.046, 0.086, 0.019, 0.015, (*SHADOW, 112))

    # The sockets, which the warp cut into the skull and the light has to find.
    for side in (-1, 1):
        ellipse(draw, side * 0.030, 0.1485, 0.025, 0.0170, (*SHADOW, 172))
        ellipse(draw, side * 0.026, 0.164, 0.024, 0.0080, (*SHADOW, 128))

    # The temples, the jaw and the jowls an old man carries.
    for side in (-1, 1):
        ellipse(draw, side * 0.076, 0.182, 0.018, 0.026, (*SHADOW, 86))
        ellipse(draw, side * 0.052, 0.062, 0.018, 0.016, (*SHADOW, 96))
        ellipse(draw, side * 0.060, 0.092, 0.012, 0.017, (*SHADOW, 62))

    # The crease under the lip, the ball of the chin catching the light, and the
    # shadow the jaw casts on the neck. A chin is a shape, not the place the face
    # stops, and the lower third of this one was blank for four versions.
    ellipse(draw, 0.0, 0.0620, 0.0245, 0.0072, (*SHADOW, 185))
    ellipse(draw, 0.0, 0.0470, 0.0180, 0.0110, (*SKIN_LIT, 120))
    ellipse(draw, 0.0, 0.0300, 0.0290, 0.0070, (*SHADOW, 155))
    ellipse(draw, 0.0, 0.0160, 0.0300, 0.0055, (*SHADOW, 120))

    # The jowls, which are what makes a heavy jaw heavy.
    for side in (-1, 1):
        ellipse(draw, side * 0.047, 0.0520, 0.0140, 0.0150, (*SHADOW, 86))
        ellipse(draw, side * 0.038, 0.0620, 0.0110, 0.0120, (*SHADOW, 62))
    ellipse(draw, 0.0, 0.0440, 0.013, 0.0090, (*SKIN_LIT, 90))

    # The nose: a shadow down the far side and beside each wing, a lit bridge,
    # and the two dark nostrils under the tip. It is the largest thing on this
    # face in the reference and it is what the light is arranged around.
    ellipse(draw, 0.0125, 0.115, 0.0056, 0.030, (*SHADOW, 210))
    ellipse(draw, -0.0098, 0.117, 0.0072, 0.027, (*SKIN_WARM, 150))
    for side in (-1, 1):
        # The wing, the crease behind it, and the nostril under the tip.
        ellipse(draw, side * 0.0245, 0.0990, 0.0080, 0.0062, (*SHADOW, 215))
        ellipse(draw, side * 0.0360, 0.0970, 0.0058, 0.0090, (*SHADOW, 170))
        ellipse(draw, side * 0.0130, 0.0955, 0.0046, 0.0034, (40, 24, 18, 240))

    ellipse(draw, 0.0, NOSE_TIP_Z + 0.010, 0.0044, 0.030, (*SKIN_LIT, 210))
    ellipse(draw, 0.0, 0.1090, 0.0098, 0.0052, (*SKIN_LIT, 140))

    over(image, shade, 16.0)

    # Skin in shadow goes grey-blue; skin shaded by turning a brown down is mud.
    cool = layer()
    draw = ImageDraw.Draw(cool)
    ellipse(draw, 0.0, 0.020, 0.045, 0.020, (86, 96, 118, 44))
    for side in (-1, 1):
        ellipse(draw, side * 0.072, 0.150, 0.020, 0.040, (88, 98, 120, 38))
    over(image, cool, 14.0)


#: Where the eye is. One definition, because the outline and the iris and the lids
#: were using two: the outline had been moved in and down and the iris had not, so
#: every eye on this face was a pupil floating off its own socket with white showing
#: underneath it.
EYE_X = 0.030
EYE_Z = 0.152

#: The brow's line, the tip of the nose and the mouth, each named once. Every one of
#: these was written twice — once in the painter and once in the marker list — and the
#: eye had drifted between its own two halves because only one of them was edited.
#: Naming them is the fix and it is better than a check, because a check tells you
#: afterwards and a name cannot disagree with itself.
BROW_Z = 0.1770
BROW_ARCH = 0.0070
BROW_FALL = 0.0076
NOSE_TIP_Z = 0.1110
MOUTH_Z = 0.0798


def eye_outline(side, lift=0.0):
    """One eye's silhouette: an almond with a corner at each end.

    The eye was an ellipse, and an ellipse is a shape with no corners — which is why
    it read as a doll's. The inner corner is the pointed one and sits lower, the
    outer one is blunter, and the upper lid's peak is not at the middle but towards
    the outer end. `lift` moves the whole shape up the lid.
    """
    cx = side * EYE_X
    z = EYE_Z + lift

    return [
        (cx - (side * 0.0104), z - 0.0004),
        (cx - (side * 0.0072), z + 0.0034),
        (cx - (side * 0.0016), z + 0.0054),
        (cx + (side * 0.0038), z + 0.0050),
        (cx + (side * 0.0078), z + 0.0030),
        (cx + (side * 0.0104), z - 0.0004),
        (cx + (side * 0.0060), z - 0.0034),
        (cx, z - 0.0048),
        (cx - (side * 0.0060), z - 0.0036),
    ]


def paint_eyes(image):
    """Two eyes: a sclera, an iris, a pupil, a lid over them and a lash line.

    Placed at the height the head's own warp cut its sockets at — 15.2 cm up from
    the neck. The sclera is small and the upper lid covers a third of it, because
    an eye with white all round the iris is an eye that is alarmed, and this man
    has not been surprised by anything in thirty years.
    """
    eyes = layer()
    draw = ImageDraw.Draw(eyes)

    for side in (-1, 1):
        x, z = side * EYE_X, EYE_Z

        polygon(draw, eye_outline(side), (*EYE_WHITE, 255))

        # The iris, with a limbal ring: an iris that fades into the white has no
        # edge, and an eye without an edge is a hole.
        ellipse(draw, x - (side * 0.0007), z - 0.0002, 0.0074, 0.0072, (46, 32, 22, 255))
        ellipse(draw, x - (side * 0.0007), z - 0.0002, 0.0064, 0.0064, (*IRIS, 255))
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0030, 0.0030, (*PUPIL, 255))
        ellipse(draw, x - (side * 0.0036), z + 0.0032, 0.0014, 0.0014, (255, 255, 255, 230))

    over(image, eyes, 1.0)

    lids = layer()
    draw = ImageDraw.Draw(lids)

    for side in (-1, 1):
        x, z = side * EYE_X, EYE_Z

        # A heavy hooded lid, sitting on the top third of the eye and reaching the
        # outer corner: this is where the age is, more than in any line.
        ellipse(draw, x, z + 0.0074, 0.0140, 0.0052, (*LID, 248))
        # The crease above the lid, and the shadow it throws into the socket.
        ellipse(draw, x, z + 0.0142, 0.0170, 0.0028, (74, 50, 38, 215))
        ellipse(draw, x, z + 0.0176, 0.0186, 0.0030, (*SHADOW, 120))
        # The lash line, which is what gives an eye an edge.
        ellipse(draw, x, z + 0.0086, 0.0154, 0.0013, (40, 28, 22, 240))
        ellipse(draw, x, z - 0.0092, 0.0136, 0.0011, (*LID, 175))

    over(image, lids, 1.1)


def brow_outline(side):
    """One brow: a tapered arch with a top edge and a bottom one.

    It was a row of overlapping ellipses, which has no edges — the top of a brow is
    a line and so is the bottom, and a brow drawn out of blobs has neither. Thickest
    at the inner end, tapering outward, arching over the eye and finishing lower
    than it started.
    """
    top = []
    bottom = []

    for step in range(20):
        t01 = step / 19.0
        dx = 0.010 + (t01 * 0.052)
        dz = BROW_Z + (BROW_ARCH * math.sin(math.pi * (t01 ** 0.72))) - (BROW_FALL * t01)
        half = 0.0054 - (0.0026 * (t01 ** 0.85))

        top.append((side * dx, dz + half))
        bottom.append((side * dx, dz - half))

    return top + list(reversed(bottom))


def paint_brows(image):
    """Two heavy brows, long and arched, dropping at the outer end."""
    brows = layer()
    draw = ImageDraw.Draw(brows)

    for side in (-1, 1):
        polygon(draw, brow_outline(side), (*BROW, 254))

    # A few hairs standing off the top edge, so it is a brow and not a sticker.
    hairs = layer()
    hair_draw = ImageDraw.Draw(hairs)

    for side in (-1, 1):
        for step in range(7):
            t01 = 0.12 + (step * 0.13)
            dx = 0.010 + (t01 * 0.052)
            dz = BROW_Z + (BROW_ARCH * math.sin(math.pi * (t01 ** 0.72))) - (BROW_FALL * t01)
            half = 0.0054 - (0.0026 * (t01 ** 0.85))
            hair_draw.line(
                [at(side * dx, dz + half), at(side * dx + (side * 0.0016), dz + half + 0.0022)],
                fill=(*BROW, 150),
                width=1,
            )

    over(image, brows, 1.6)
    over(image, hairs, 0.8)


def paint_mouth(image):
    """Lips, and the crease between them: the upper one under the moustache."""
    mouth = layer()
    draw = ImageDraw.Draw(mouth)

    # The upper lip, nearly all of which the moustache covers.
    ellipse(draw, 0.0, 0.0880, 0.0150, 0.0028, (*LIP, 150))
    # The crease.
    ellipse(draw, 0.0, 0.0798, 0.0192, 0.0015, (*LIP_DARK, 250))
    # The lower lip, and the shadow under it.
    ellipse(draw, 0.0, 0.0738, 0.0160, 0.0034, (*LIP, 165))
    ellipse(draw, 0.0, 0.0668, 0.0150, 0.0026, (*LIP_DARK, 185))

    over(image, mouth, 1.6)


def paint_age(image):
    """The lines that say the man is old rather than merely painted."""
    lines = layer()
    draw = ImageDraw.Draw(lines)

    # The forehead, in three creases rather than one.
    for z, half_width in ((0.193, 0.036), (0.204, 0.032), (0.214, 0.026), (0.223, 0.020)):
        ellipse(draw, 0.0, z, half_width, 0.0018, (*SHADOW, 120))

    for side in (-1, 1):
        # The folds beside the nose, which carry more of the age than any other
        # line on a face, and they run from the wing of the nose to the corner of
        # the mouth rather than straight down.
        for step in range(9):
            t01 = step / 8.0
            ellipse(
                draw,
                side * (0.021 + (t01 * 0.026)),
                0.1015 - (t01 * 0.030),
                0.0026,
                0.0075,
                (*SHADOW, 118),
            )

        # The bag under the eye, and the hollow beside it.
        ellipse(draw, side * 0.034, 0.1470, 0.0130, 0.0042, (*SHADOW, 78))
        ellipse(draw, side * 0.048, 0.1520, 0.0080, 0.0090, (*SHADOW, 52))

        # Crow's feet.
        for step in range(3):
            ellipse(
                draw,
                side * (0.053 + (step * 0.006)),
                0.153 + (step * 0.006),
                0.0058,
                0.0014,
                (*SHADOW, 84),
            )

    over(image, lines, 3.2)


MOUSTACHE = (58, 50, 44)


MOUSTACHE_TOP = 0.1065
MOUSTACHE_WING = 0.0640


def moustache_outline(side):
    """Half the moustache, as the corners of its silhouette.

    Read from the reference: it leaves the middle of the lip, runs out and slightly
    up to a corner level with the wing of the nose, then turns down and back in. The
    top edge is the underside of the nose and the bottom edge is just above the lip
    — it is the mouth that is hidden, not the moustache that is vague.
    """
    return [
        (side * 0.000, 0.1040),
        (side * 0.017, 0.1035),
        (side * 0.032, 0.0995),
        (side * 0.050, 0.0905),
        (side * 0.058, 0.0790),
        (side * 0.048, 0.0670),
        (side * 0.032, 0.0600),
        (side * 0.016, 0.0590),
        (side * 0.000, 0.0588),
    ]


def paint_moustache_bed(image):
    """The moustache: one shape, drawn once.

    It was being drawn twice — a dark polygon and then a lighter wash over a mask
    that had grown past it, so the figure wore two moustaches at once, one grey and
    slightly below the other. Everything that touches this shape is clipped to the
    shape now, and there is exactly one of it.
    """
    shape = Image.new("L", image.size, 0)
    shape_draw = ImageDraw.Draw(shape)

    for side in (-1, 1):
        polygon(shape_draw, moustache_outline(side), 255)

    mass = layer()
    draw = ImageDraw.Draw(mass)
    draw.bitmap((0, 0), shape, fill=(*MOUSTACHE, 255))

    # The parting down the middle, and the strands running out along each wing.
    # Drawn into a layer that is masked by the shape, so a strand cannot lengthen
    # the moustache or sit beside it.
    detail = layer()
    detail_draw = ImageDraw.Draw(detail)

    # Light on the top of it and dark under the lip: a moustache is a mass with a
    # lit side, and one flat tone reads as a shape cut out of paper.
    for step in range(14):
        tone = 0.35 + (0.65 * (step / 13.0))
        ellipse(
            detail_draw,
            0.0,
            0.0855 + (step * 0.0018) + 0.0,
            0.0500,
            0.0016,
            (
                int(112 * tone),
                int(100 * tone),
                int(88 * tone),
                int(120 * (1.0 - (step / 13.0))),
            ),
        )

    detail_draw.line(
        [at(0.0, 0.1000), at(0.0, 0.0780)],
        fill=(30, 25, 22, 190),
        width=3,
    )

    for side in (-1, 1):
        for step in range(26):
            t01 = step / 25.0
            ellipse(
                detail_draw,
                side * (0.002 + (t01 * 0.058)),
                0.0955 - (t01 * 0.021),
                0.0010,
                0.0068,
                (110, 100, 90, 96),
            )

    detail = Image.composite(detail, Image.new("RGBA", image.size, (0, 0, 0, 0)), shape)

    # A shadow just under the lip end of it, so the moustache sits on the mouth
    # rather than floating above it.
    bed = layer()
    bed_draw = ImageDraw.Draw(bed)
    ellipse(bed_draw, 0.0, 0.0620, 0.0400, 0.0045, (*SHADOW, 140))
    over(image, bed, 1.5)

    over(image, mass, 0.5)
    over(image, detail, 0.5)


#: Every feature the painter places, as the head-local metres it is placed at. The
#: marker render draws a crosshair at each one, so a feature that does not land on
#: the geometry built for it is visible as a crosshair beside a brow rather than as
#: something to be argued about later.
MARKERS = [
    ("eye inner", lambda side: (side * (EYE_X - 0.0110), EYE_Z - 0.0004)),
    ("eye centre", lambda side: (side * EYE_X, EYE_Z)),
    ("eye outer", lambda side: (side * (EYE_X + 0.0104), EYE_Z - 0.0004)),
    ("brow inner", lambda side: (side * 0.0100, BROW_Z)),
    ("brow peak", lambda side: (side * 0.0320, BROW_Z + BROW_ARCH - (BROW_FALL * 0.32))),
    ("brow outer", lambda side: (side * 0.0640, BROW_Z + (BROW_ARCH * 0.10) - BROW_FALL)),
    ("nose tip", lambda side: (0.0, NOSE_TIP_Z)),
    ("nose wing", lambda side: (side * 0.0245, 0.0990)),
    ("nostril", lambda side: (side * 0.0130, 0.0955)),
    ("mouth", lambda side: (0.0, MOUTH_Z)),
    ("lip", lambda side: (0.0, 0.0600)),
    ("chin", lambda side: (0.0, 0.0300)),
    ("temple", lambda side: (side * 0.0580, 0.0980)),
    ("hairline", lambda side: (0.0, 0.2360)),
]


def pose_markers(image):
    """Draws a crosshair at every feature the painter places, and the hairline.

    This is the instrument the face work was missing. The eye and the brow were found
    to be the wrong shape by looking, and the texture's V axis was found to be
    inverted only by a marker render improvised when nothing else was working — and
    then thrown away. A feature that is painted somewhere other than where its
    geometry is looks like a painting mistake; it is a mapping mistake, and this is
    what tells the two apart.
    """
    draw = ImageDraw.Draw(image)

    for name, place in MARKERS:
        for side in ((-1, 1) if place(1)[0] != 0.0 or name in ("nose tip", "mouth", "lip", "chin", "hairline") else (1,)):
            x, z = place(side)
            cx, cy = at(x, z)
            colour = (255, 40, 40) if side < 0 else (40, 200, 255)

            draw.line([(cx - 14, cy), (cx + 14, cy)], fill=colour, width=3)
            draw.line([(cx, cy - 14), (cx, cy + 14)], fill=colour, width=3)

    # And the hairline, which is a curve rather than a point.
    for step in range(0, 101):
        u = step / 100.0
        v = face_uv.hairline_at(u)
        cx, cy = px(u, v)
        draw.line([(cx - 3, cy), (cx + 3, cy)], fill=(40, 255, 60), width=2)

    return image


def build_face(path):
    """Paints the head texture and writes it out."""
    image = Image.new("RGB", (WIDTH, HEIGHT), SKIN)

    paint_skin(image)
    paint_hair(image)
    paint_form(image)
    paint_age(image)
    paint_moustache_bed(image)
    paint_eyes(image)
    paint_brows(image)
    paint_mouth(image)

    # Twenty-four bit, and no palette. A 256-colour adaptive palette seemed enough
    # for a face and is not: it bands the one thing a face is made of, which is a
    # smooth gradient, and the bands run across the cheeks and the forehead exactly
    # where the light is. The full image is 1.9 MB against 0.8, which is a fair
    # price for the head of the only figure in the game looked at from three metres.
    image.save(path, optimize=True, compress_level=9)
    return path


CLOTH = (146, 142, 92)
CLOTH_DARK = (108, 104, 64)
CLOTH_LIGHT = (176, 172, 118)


def build_cloth(path):
    """The tunic's cloth: a woven field, seamless, that tiles round a body.

    Separate from the face, because a face is one of a kind and cloth is a surface
    that repeats. It is generated rather than drawn for the same reason everything
    else here is: the seams have to meet when it wraps, and a hand-drawn tile is a
    promise about its own edges that nothing checks.
    """
    image = Image.new("RGB", (512, 512), CLOTH)
    pixels = image.load()

    for y in range(512):
        for x in range(512):
            # A plain weave: two threads crossing, one over and one under, at a
            # scale that survives being tiled a few centimetres across.
            warp = math.sin(x * math.pi / 8.0)
            weft = math.sin(y * math.pi / 8.0)
            over = 1.0 if (warp * weft) >= 0.0 else 0.0

            thread = (0.5 * warp * over) + (0.5 * weft * (1.0 - over))
            slub = noise(x / 37.0, y / 23.0, 5.0) - 0.5
            fibre = (noise(x * 0.9, y * 0.9, 11.0) - 0.5) * 0.07

            tone = 1.0 + (0.030 * thread) + (0.055 * slub) + (fibre * 0.5)
            base = CLOTH_DARK if (slub < -0.18) else CLOTH

            pixels[x, y] = (
                min(255, max(0, int(base[0] * tone))),
                min(255, max(0, int(base[1] * tone))),
                min(255, max(0, int(base[2] * tone))),
            )

    # Softened before it is quantised. This renderer has no mipmaps, so a tunic
    # seen across a room minifies a 512-pixel tile into a couple of hundred pixels
    # with nothing to average it: every fine thread becomes a moire stripe, and the
    # tunic came out corduroy twice before this was the reason.
    image = image.filter(ImageFilter.GaussianBlur(1.1))
    image.quantize(colors=64, method=Image.MEDIANCUT, dither=Image.NONE).save(path, optimize=True)
    return path


def build_metal(path):
    """A flat neutral surface for everything metal on the figure.

    The trim was pinned to a texel of the *face* — the lit bridge of the nose, which
    is the brightest place on the map — and multiplied by it, so gold came out
    orange and brass came out orange-brown. Metal has no colour of its own beyond
    what the vertex colour says, so it samples a surface that has none.
    """
    image = Image.new("RGB", (8, 8), (198, 198, 198))
    image.save(path, optimize=True)
    return path


def main():
    parser = argparse.ArgumentParser(description="Paint the cutscene personality textures.")
    parser.add_argument("--out", required=True, help="Directory to write the PNGs into.")
    parser.add_argument("--markers", action="store_true",
                        help="Draw a crosshair at every feature and write that instead.")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    if args.markers:
        marked = pose_markers(Image.new("RGB", (WIDTH, HEIGHT), (58, 58, 62)))
        marked_path = os.path.join(args.out, "personality_elder.png")
        marked.save(marked_path, optimize=True, compress_level=9)
        print(f"wrote markers to {os.path.basename(marked_path)}")
        return 0

    path = build_face(os.path.join(args.out, "personality_elder.png"))
    print(f"wrote {os.path.basename(path)}  ({os.path.getsize(path) / 1024:.1f} KB)")

    cloth = build_cloth(os.path.join(args.out, "personality_elder_cloth.png"))
    print(f"wrote {os.path.basename(cloth)}  ({os.path.getsize(cloth) / 1024:.1f} KB)")

    metal = build_metal(os.path.join(args.out, "personality_elder_metal.png"))
    print(f"wrote {os.path.basename(metal)}  ({os.path.getsize(metal) / 1024:.1f} KB)")
    print("done: 1 personality texture")


if __name__ == "__main__":
    main()
