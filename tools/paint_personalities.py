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
SKIN = (178, 124, 82)
SKIN_LIT = (216, 164, 116)
SKIN_SHADE = (112, 70, 46)
SKIN_WARM = (200, 108, 72)
HAIR = (62, 56, 50)
HAIR_LIT = (88, 82, 74)
HAIR_DARK = (38, 34, 30)
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
        light += 0.08 * max(0.0, (0.55 - v) / 0.55)

        # The warm band across the middle of the face: cheeks and nose.
        warmth = math.exp(-(((v - 0.44) / 0.16) ** 2))

        for x in range(WIDTH):
            u = x / WIDTH
            grid_u = face_uv.unwrap_u(u)
            across = (grid_u - 0.25) / 0.25 if grid_u < 0.5 else (0.75 - grid_u) / 0.25

            shade = light * (1.0 - (0.50 * min(1.0, abs(across)) ** 1.4))
            shade *= 1.0 - (0.22 * max(0.0, across))

            # Warm in the light, cool in the shadow, and both of them only a little:
            # a face painted hard in two colours is a clown's.
            local_warm = warmth * (1.0 - (0.5 * min(1.0, abs(across))))
            red = SKIN[0] * shade * (1.0 + (0.06 * local_warm))
            green = SKIN[1] * shade * (1.0 - (0.02 * local_warm))
            blue = SKIN[2] * shade * (1.0 - (0.10 * local_warm))
            blue = blue * (1.0 + (0.10 * max(0.0, across))) + (6.0 * max(0.0, across))

            mottle = noise(x / 47.0, y / 31.0, 17.0) + noise(x / 19.0, y / 13.0, 41.0)
            grain = 0.955 + (0.06 * noise(x // 5, y // 5, 3.1)) + (0.035 * (mottle - 1.0))
            red *= grain
            green *= grain
            blue *= grain

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

        # One lock's own colour, and how far across it this pixel is: a lock is
        # lit down its middle and dark where it meets its neighbours.
        # Locks of uneven width, and each one its own tone out of three: a head of
        # hair is not one colour with a highlight on it, it is dark strands and
        # grey ones lying together, and which is which changes lock by lock.
        lock = int(x // LOCK_PIXELS + (4.0 * noise(x // 53, 0.0, 23.0)))
        lock_tone = noise(lock, 0.0, 5.7)
        lock_grey = noise(lock, 3.3, 11.1)
        across = ((x % LOCK_PIXELS) / LOCK_PIXELS) - 0.5
        rounded = 1.0 - ((abs(across) * 2.0) ** 1.5)

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
            roots = math.exp(-(((y - edge) / 26.0) ** 2))

            if lock_grey > 0.88:
                base = HAIR_LIT
            elif lock_tone < 0.42:
                base = HAIR_DARK
            else:
                base = HAIR

            tone = 0.64 + (0.46 * lock_tone) + (0.32 * rounded) + (0.42 * sheen) - (0.20 * roots)

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

        edge = face_uv.hairline_at(u) * HEIGHT
        length = 6.0 + (13.0 * (stray - 0.82) / 0.18)
        fringe_draw.line([(x, edge - 3), (x, edge + length)], fill=int(150 + (100 * stray)), width=1)

    mask = ImageChops.lighter(mask, fringe)

    image.paste(overlay, (0, 0), blur(mask, 4.5))


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
    shade = layer()
    draw = ImageDraw.Draw(shade)

    # Cheekbones, catching the light, and the hollow under them.
    for side in (-1, 1):
        ellipse(draw, side * 0.058, 0.108, 0.026, 0.022, (*SKIN_WARM, 40))
        ellipse(draw, side * 0.046, 0.086, 0.019, 0.015, (*SHADOW, 112))

    # The sockets, which the warp cut into the skull and the light has to find.
    for side in (-1, 1):
        ellipse(draw, side * 0.037, 0.1605, 0.028, 0.0175, (*SHADOW, 172))
        ellipse(draw, side * 0.030, 0.176, 0.026, 0.0080, (*SHADOW, 128))

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
    ellipse(draw, 0.0115, 0.118, 0.0060, 0.026, (*SHADOW, 200))
    ellipse(draw, -0.0090, 0.120, 0.0080, 0.023, (*SKIN_WARM, 140))
    for side in (-1, 1):
        # The wing, the crease behind it, and the nostril under the tip.
        ellipse(draw, side * 0.0245, 0.1080, 0.0080, 0.0062, (*SHADOW, 215))
        ellipse(draw, side * 0.0360, 0.1060, 0.0058, 0.0090, (*SHADOW, 170))
        ellipse(draw, side * 0.0130, 0.1045, 0.0046, 0.0034, (40, 24, 18, 240))

    ellipse(draw, 0.0, 0.126, 0.0050, 0.026, (*SKIN_LIT, 190))
    ellipse(draw, 0.0, 0.1180, 0.0098, 0.0052, (*SKIN_LIT, 140))

    over(image, shade, 16.0)


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
        x, z = side * 0.037, 0.164

        ellipse(draw, x, z, 0.0108, 0.0058, (*EYE_WHITE, 255))

        # The iris, with a limbal ring: an iris that fades into the white has no
        # edge, and an eye without an edge is a hole.
        ellipse(draw, x - (side * 0.0008), z + 0.0000, 0.0080, 0.0078, (46, 32, 22, 255))
        ellipse(draw, x - (side * 0.0008), z + 0.0000, 0.0069, 0.0069, (*IRIS, 255))
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0030, 0.0030, (*PUPIL, 255))
        ellipse(draw, x - (side * 0.0036), z + 0.0032, 0.0014, 0.0014, (255, 255, 255, 230))

    over(image, eyes, 1.0)

    lids = layer()
    draw = ImageDraw.Draw(lids)

    for side in (-1, 1):
        x, z = side * 0.037, 0.164

        # A heavy hooded lid, sitting on the top third of the eye and reaching the
        # outer corner: this is where the age is, more than in any line.
        ellipse(draw, x, z + 0.0088, 0.0148, 0.0050, (*LID, 244))
        # The crease above the lid, and the shadow it throws into the socket.
        ellipse(draw, x, z + 0.0142, 0.0170, 0.0028, (74, 50, 38, 215))
        ellipse(draw, x, z + 0.0176, 0.0186, 0.0030, (*SHADOW, 120))
        # The lash line, which is what gives an eye an edge.
        ellipse(draw, x, z + 0.0086, 0.0154, 0.0013, (40, 28, 22, 240))
        ellipse(draw, x, z - 0.0092, 0.0136, 0.0011, (*LID, 175))

    over(image, lids, 1.1)


def paint_brows(image):
    """Two heavy brows, long and straight, dropping at the outer end.

    The reference's brows are the second darkest thing on the face after the
    moustache and they run most of the way across it. Short ones read as a raised
    eyebrow — surprise — and this man is not surprised by anything.
    """
    brows = layer()
    draw = ImageDraw.Draw(brows)

    for side in (-1, 1):
        for step in range(22):
            t01 = step / 21.0
            dx = 0.010 + (t01 * 0.056)
            # A straight brow that drops at the outer end, thickest a third of the
            # way along: a brow drawn as a row of dots is a row of dots.
            # An arch, not a line: it rises from the inner end to a peak just
            # outside the middle of the eye and falls away to the outer end, and
            # the outer end finishes lower than the inner one started. A brow
            # drawn as a taper is a bar, and a bar is not an eyebrow.
            dz = 0.1800 + (0.0066 * math.sin(math.pi * (t01 ** 0.72))) - (0.0072 * t01)
            half_width = 0.0050
            half_height = 0.0050 - (0.0022 * t01)
            ellipse(draw, side * dx, dz, half_width, half_height, (*BROW, 254))

    over(image, brows, 2.0)


def paint_mouth(image):
    """Lips, and the crease between them: the upper one under the moustache."""
    mouth = layer()
    draw = ImageDraw.Draw(mouth)

    # The upper lip, nearly all of which the moustache covers.
    ellipse(draw, 0.0, 0.0880, 0.0165, 0.0032, (*LIP, 195))
    # The crease.
    ellipse(draw, 0.0, 0.0798, 0.0192, 0.0015, (*LIP_DARK, 250))
    # The lower lip, and the shadow under it.
    ellipse(draw, 0.0, 0.0738, 0.0175, 0.0038, (*LIP, 205))
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


MOUSTACHE = (62, 54, 48)


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
        (side * 0.000, 0.1130),
        (side * 0.017, 0.1125),
        (side * 0.032, 0.1085),
        (side * 0.044, 0.1010),
        (side * 0.050, 0.0910),
        (side * 0.046, 0.0805),
        (side * 0.033, 0.0790),
        (side * 0.017, 0.0830),
        (side * 0.000, 0.0855),
    ]


def paint_moustache_shadow(image):
    """The moustache: the darkest, thickest thing on the face, with an edge."""
    shadow = layer()
    draw = ImageDraw.Draw(shadow)

    for side in (-1, 1):
        polygon(draw, moustache_outline(side), (*MOUSTACHE, 255))

    # A parting down the middle and a shadow under the whole of it, so the mass has
    # a middle and sits on the lip instead of floating over it.
    ellipse(draw, 0.0, 0.1010, 0.0042, 0.0090, (34, 29, 25, 220))
    ellipse(draw, 0.0, 0.0815, 0.0400, 0.0040, (*SHADOW, 150))

    over(image, shadow, 0.5)

    # Strands inside it, at the scale of hair rather than of a gradient.
    strands = layer()
    draw = ImageDraw.Draw(strands)
    mask = Image.new("L", image.size, 0)
    mask_draw = ImageDraw.Draw(mask)

    for side in (-1, 1):
        polygon(mask_draw, moustache_outline(side), 255)

        for step in range(30):
            t01 = step / 29.0
            ellipse(
                mask_draw,
                side * (0.003 + (t01 * 0.056)),
                0.1055 - (t01 * 0.023),
                0.0012,
                0.0075,
                145,
            )

    over(image, strands, 0.4)
    # The strands inside it are lighter than the mass, so the moustache reads as
    # hair rather than as a shadow under the nose.
    lighter = Image.new("RGBA", image.size, (126, 114, 102, 255))
    image.paste(lighter, (0, 0), blur(Image.composite(mask, Image.new("L", image.size, 0), mask), 0.5))
    over(image, Image.composite(strands, Image.new("RGBA", image.size, (0, 0, 0, 0)), mask), 0.35)


def build_face(path):
    """Paints the head texture and writes it out."""
    image = Image.new("RGB", (WIDTH, HEIGHT), SKIN)

    paint_skin(image)
    paint_hair(image)
    paint_form(image)
    paint_age(image)
    paint_moustache_shadow(image)
    paint_eyes(image)
    paint_brows(image)
    paint_mouth(image)

    # Quantised. A face is a few hundred distinct colours and an adaptive palette
    # holds them all; the 24-bit version of this image is ten times the size for
    # differences no one can see on a head two hundred pixels tall.
    image = image.quantize(colors=256, method=Image.MEDIANCUT, dither=Image.NONE)
    image.save(path, optimize=True)
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


def main():
    parser = argparse.ArgumentParser(description="Paint the cutscene personality textures.")
    parser.add_argument("--out", required=True, help="Directory to write the PNGs into.")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    path = build_face(os.path.join(args.out, "personality_elder.png"))
    print(f"wrote {os.path.basename(path)}  ({os.path.getsize(path) / 1024:.1f} KB)")

    cloth = build_cloth(os.path.join(args.out, "personality_elder_cloth.png"))
    print(f"wrote {os.path.basename(cloth)}  ({os.path.getsize(cloth) / 1024:.1f} KB)")
    print("done: 1 personality texture")


if __name__ == "__main__":
    main()
