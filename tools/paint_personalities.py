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

from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender"))

import face_uv  # noqa: E402

WIDTH = 2048
HEIGHT = 1536

# The same palette the generator paints the head's vertex colours with, so the
# painted face and the shaded skull agree where the texture ends.
SKIN = (198, 148, 106)
SKIN_LIT = (228, 182, 138)
SKIN_SHADE = (138, 94, 66)
SKIN_WARM = (214, 132, 96)
HAIR = (68, 62, 56)
HAIR_LIT = (104, 96, 88)
HAIR_DARK = (44, 40, 36)
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


def blur(layer, radius):
    return layer.filter(ImageFilter.GaussianBlur(radius))


def noise(x, y, salt=0.0):
    """A stable 0..1 value for a pixel, so skin has grain and hair has strands."""
    value = math.sin((x * 12.9898) + (y * 78.233) + salt) * 43758.5453

    return value - math.floor(value)


def paint_skin(image):
    """Base tone, the light falling across a head, and grain.

    The reference is lit from the upper left and carries a shadow down the whole of
    its right side. Baking that in is what a painted face is for: it is the one
    thing the renderer cannot do, because it lights a surface by its normal and
    every normal across a cheekbone points almost the same way.
    """
    pixels = image.load()

    for y in range(HEIGHT):
        v = y / HEIGHT

        # Lit from above, falling into shadow under the jaw.
        light = 1.0 - (0.34 * max(0.0, (v - 0.52) / 0.48) ** 1.0)
        light += 0.08 * max(0.0, (0.55 - v) / 0.55)

        for x in range(WIDTH):
            u = x / WIDTH
            grid_u = face_uv.unwrap_u(u)

            # And from one side: the key comes from the model's own left, which is
            # the left of the image, so the far cheek turns away and the near one
            # catches it. A face lit from straight on is a mask.
            across = (grid_u - 0.25) / 0.25 if grid_u < 0.5 else (0.75 - grid_u) / 0.25

            shade = light * (1.0 - (0.30 * min(1.0, abs(across)) ** 1.7))
            shade *= 1.0 - (0.16 * max(0.0, across))

            grain = 0.965 + (0.06 * noise(x // 5, y // 5, 3.1))

            value = shade * grain
            pixels[x, y] = (
                min(255, int(SKIN[0] * value)),
                min(255, int(SKIN[1] * value)),
                min(255, int(SKIN[2] * value)),
            )


def paint_hair(image):
    """Hair above the hairline, with strands and the sheen of light on it."""
    pixels = image.load()
    overlay = Image.new("RGB", image.size, HAIR)
    overlay_pixels = overlay.load()
    mask = Image.new("L", image.size, 0)
    mask_pixels = mask.load()

    for x in range(WIDTH):
        u = x / WIDTH
        hairline = face_uv.hairline_at(u)
        edge = hairline * HEIGHT

        for y in range(HEIGHT):
            # A soft boundary rather than a cut: the hair mesh's own edge is stepped
            # by its grid, and a hard painted edge would draw attention to it.
            depth = (edge - y) / 14.0

            if depth <= 0.0:
                continue

            mask_pixels[x, y] = int(min(255.0, depth * 255.0))

            strand = (0.75 * noise(x * 0.06, y * 1.1, 5.7)) + (0.25 * noise(x * 0.03, y * 3.0, 9.3))
            sheen = math.exp(-(((y - (edge - 46.0)) / 34.0) ** 2))

            base = HAIR_DARK if strand < 0.45 else HAIR
            tone = 0.86 + (0.26 * strand) + (0.42 * sheen)

            overlay_pixels[x, y] = (
                min(255, int(base[0] * tone)),
                min(255, int(base[1] * tone)),
                min(255, int(base[2] * tone)),
            )

    image.paste(overlay, (0, 0), blur(mask, 2.0))


def layer():
    """A transparent layer the size of the texture, to paint one feature on."""
    return Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))


def over(image, painted, radius=0.0):
    """Composites a transparent layer, optionally softened, onto the face.

    Softening blurs the alpha as well as the colour, which is the whole reason
    these are RGBA layers and not a colour image with a separate mask: a soft edge
    on a shape is a soft edge on its own coverage, not a translucent wash of the
    layer's black background over the whole face.
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
        ellipse(draw, side * 0.037, 0.1605, 0.027, 0.0165, (*SHADOW, 148))
        ellipse(draw, side * 0.030, 0.176, 0.026, 0.0080, (*SHADOW, 128))

    # The temples, the jaw and the jowls an old man carries.
    for side in (-1, 1):
        ellipse(draw, side * 0.076, 0.182, 0.018, 0.026, (*SHADOW, 86))
        ellipse(draw, side * 0.052, 0.062, 0.018, 0.016, (*SHADOW, 96))
        ellipse(draw, side * 0.060, 0.092, 0.012, 0.017, (*SHADOW, 62))

    # The crease under the lip, and the shadow under the jaw.
    ellipse(draw, 0.0, 0.0600, 0.024, 0.0075, (*SHADOW, 150))
    ellipse(draw, 0.0, 0.0300, 0.028, 0.0065, (*SHADOW, 138))
    ellipse(draw, 0.0, 0.0440, 0.013, 0.0090, (*SKIN_LIT, 90))

    # The nose: a shadow down the far side and beside each wing, a lit bridge,
    # and the two dark nostrils under the tip. It is the largest thing on this
    # face in the reference and it is what the light is arranged around.
    ellipse(draw, 0.0115, 0.116, 0.0060, 0.025, (*SHADOW, 190))
    ellipse(draw, -0.0090, 0.118, 0.0080, 0.022, (*SKIN_WARM, 130))
    for side in (-1, 1):
        # The wing, the crease behind it, and the nostril under the tip.
        ellipse(draw, side * 0.0250, 0.1060, 0.0080, 0.0068, (*SHADOW, 205))
        ellipse(draw, side * 0.0350, 0.1020, 0.0064, 0.0095, (*SHADOW, 140))
        ellipse(draw, side * 0.0135, 0.1035, 0.0048, 0.0034, (44, 26, 20, 235))

    ellipse(draw, 0.0, 0.126, 0.0055, 0.025, (*SKIN_LIT, 175))
    ellipse(draw, 0.0, 0.1085, 0.0105, 0.0060, (*SKIN_LIT, 110))

    over(image, shade, 18.0)


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

        ellipse(draw, x, z, 0.0136, 0.0072, (*EYE_WHITE, 255))

        # The iris, with a limbal ring: an iris that fades into the white has no
        # edge, and an eye without an edge is a hole.
        ellipse(draw, x - (side * 0.0011), z + 0.0003, 0.0074, 0.0074, (46, 32, 22, 255))
        ellipse(draw, x - (side * 0.0011), z + 0.0003, 0.0064, 0.0064, (*IRIS, 255))
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0030, 0.0030, (*PUPIL, 255))
        ellipse(draw, x - (side * 0.0036), z + 0.0032, 0.0014, 0.0014, (255, 255, 255, 230))

    over(image, eyes, 1.0)

    lids = layer()
    draw = ImageDraw.Draw(lids)

    for side in (-1, 1):
        x, z = side * 0.037, 0.164

        # A heavy hooded lid, sitting on the top third of the eye and reaching the
        # outer corner: this is where the age is, more than in any line.
        ellipse(draw, x, z + 0.0112, 0.0164, 0.0044, (*LID, 236))
        # The crease above the lid, and the shadow it throws into the socket.
        ellipse(draw, x, z + 0.0150, 0.0176, 0.0026, (74, 50, 38, 200))
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
            dx = 0.009 + (t01 * 0.064)
            # A straight brow that drops at the outer end, thickest a third of the
            # way along: a brow drawn as a row of dots is a row of dots.
            dz = 0.1810 - (0.010 * (t01 ** 2.4))
            half_width = 0.0058
            half_height = 0.0062 - (0.0026 * t01)
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


def paint_moustache_shadow(image):
    """The moustache: the darkest, thickest thing on the face."""
    shadow = layer()
    draw = ImageDraw.Draw(shadow)

    # One band under the nose, plus a wing either side of it dropping towards the
    # corner of the mouth. The geometry is what stands proud; this is what stops a
    # rim of skin showing between it and the lip.
    ellipse(draw, 0.0, 0.0985, 0.0350, 0.0130, (*MOUSTACHE, 255))

    for side in (-1, 1):
        for dx, dz, half_width, half_height in (
            (0.016, 0.0965, 0.0110, 0.0102),
            (0.030, 0.0925, 0.0104, 0.0094),
            (0.043, 0.0865, 0.0088, 0.0078),
            (0.053, 0.0790, 0.0066, 0.0060),
            (0.060, 0.0715, 0.0044, 0.0042),
        ):
            ellipse(draw, side * dx, dz, half_width, half_height, (*MOUSTACHE, 255))

    over(image, shadow, 0.3)


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
    image = image.quantize(colors=256, method=Image.MEDIANCUT, dither=Image.FLOYDSTEINBERG)
    image.save(path, optimize=True)
    return path


def main():
    parser = argparse.ArgumentParser(description="Paint the cutscene personality textures.")
    parser.add_argument("--out", required=True, help="Directory to write the PNGs into.")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    path = build_face(os.path.join(args.out, "personality_elder.png"))
    print(f"wrote {os.path.basename(path)}  ({os.path.getsize(path) / 1024:.1f} KB)")
    print("done: 1 personality texture")


if __name__ == "__main__":
    main()
