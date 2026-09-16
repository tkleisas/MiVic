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
SKIN = (222, 176, 138)
SKIN_LIT = (240, 198, 160)
SKIN_SHADE = (172, 130, 100)
SKIN_WARM = (230, 166, 132)
HAIR = (58, 48, 42)
HAIR_LIT = (86, 74, 66)
HAIR_DARK = (34, 28, 24)
BROW = (48, 38, 32)
EYE_WHITE = (222, 216, 208)
IRIS = (74, 52, 34)
IRIS_DARK = (44, 30, 20)
PUPIL = (18, 14, 12)
LID = (128, 92, 70)
LIP = (150, 92, 82)
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
    """Base tone, the shading of a head round a face, and grain."""
    pixels = image.load()

    for y in range(HEIGHT):
        v = y / HEIGHT

        # Lit from above, falling into shadow under the jaw. A face lit evenly
        # reads as paper, and the game's own light is already a hemisphere.
        light = 1.0 - (0.22 * max(0.0, (v - 0.58) / 0.42) ** 1.2)
        light += 0.10 * max(0.0, (0.55 - v) / 0.55)

        for x in range(WIDTH):
            u = x / WIDTH

            # And darker towards the sides: what the shader calls the hemisphere
            # term, baked in once so the texture has a form of its own.
            side = abs(face_uv.unwrap_u(u) - 0.25) / 0.25
            shade = light * (1.0 - (0.22 * min(1.0, side) ** 2))

            grain = 0.955 + (0.09 * noise(x // 3, y // 3, 3.1))

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

            strand = noise(x * 0.16, y * 0.9, 5.7)
            sheen = math.exp(-(((y - (edge - 46.0)) / 34.0) ** 2))

            base = HAIR_DARK if strand < 0.45 else HAIR
            tone = 0.82 + (0.36 * strand) + (0.55 * sheen)

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
        ellipse(draw, side * 0.054, 0.099, 0.019, 0.012, (*SKIN_WARM, 96))
        ellipse(draw, side * 0.044, 0.076, 0.018, 0.013, (*SHADOW, 86))

    # The sockets, which the warp cut into the skull and the light has to find.
    for side in (-1, 1):
        ellipse(draw, side * 0.037, 0.152, 0.028, 0.019, (*SHADOW, 150))

    # The temples, the jaw and the jowls an old man carries.
    for side in (-1, 1):
        ellipse(draw, side * 0.080, 0.184, 0.016, 0.022, (*SHADOW, 62))
        ellipse(draw, side * 0.052, 0.050, 0.018, 0.016, (*SHADOW, 96))
        ellipse(draw, side * 0.060, 0.080, 0.012, 0.017, (*SHADOW, 62))

    # The crease under the lip, and the shadow under the jaw.
    ellipse(draw, 0.0, 0.0335, 0.024, 0.0070, (*SHADOW, 110))
    ellipse(draw, 0.0, 0.0140, 0.026, 0.0060, (*SHADOW, 74))

    # The nose: a shadow down the far side and beside each wing, a lit bridge.
    ellipse(draw, 0.008, 0.105, 0.0042, 0.020, (*SHADOW, 130))
    ellipse(draw, -0.007, 0.104, 0.0050, 0.017, (*SKIN_WARM, 78))
    for side in (-1, 1):
        ellipse(draw, side * 0.0225, 0.0905, 0.0058, 0.0050, (*SHADOW, 165))
        ellipse(draw, side * 0.0300, 0.0860, 0.0050, 0.0075, (*SHADOW, 92))

    ellipse(draw, 0.0, 0.112, 0.0040, 0.022, (*SKIN_LIT, 180))

    over(image, shade, 5.0)


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
        x, z = side * 0.037, 0.152

        ellipse(draw, x, z, 0.0158, 0.0090, (*EYE_WHITE, 255))

        # The iris, with a limbal ring: an iris that fades into the white has no
        # edge, and an eye without an edge is a hole.
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0086, 0.0086, (46, 32, 22, 255))
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0076, 0.0076, (*IRIS, 255))
        ellipse(draw, x - (side * 0.0016), z + 0.0002, 0.0030, 0.0030, (*PUPIL, 255))
        ellipse(draw, x - (side * 0.0044), z + 0.0040, 0.0016, 0.0016, (255, 255, 255, 235))

    over(image, eyes, 0.8)

    lids = layer()
    draw = ImageDraw.Draw(lids)

    for side in (-1, 1):
        x, z = side * 0.037, 0.152

        # A heavy hooded lid, sitting on the top third of the eye and reaching the
        # outer corner: this is where the age is, more than in any line.
        ellipse(draw, x, z + 0.0106, 0.0190, 0.0064, (*LID, 244))
        ellipse(draw, x, z + 0.0152, 0.0200, 0.0036, (*SHADOW, 210))
        ellipse(draw, x, z + 0.0072, 0.0178, 0.0015, (52, 36, 26, 235))
        ellipse(draw, x, z - 0.0100, 0.0155, 0.0013, (*LID, 180))

    over(image, lids, 0.7)


def paint_brows(image):
    """Two heavy brows, thick at the nose and tapering out over the eye."""
    brows = layer()
    draw = ImageDraw.Draw(brows)

    for side in (-1, 1):
        # Four overlapping strokes rather than one: a brow is thickest at its inner
        # end, and it lies along the ridge instead of floating above the eye.
        for dx, dz, half_width, half_height in (
            (0.018, 0.184, 0.0095, 0.0058),
            (0.032, 0.189, 0.0090, 0.0052),
            (0.046, 0.188, 0.0080, 0.0044),
            (0.059, 0.182, 0.0060, 0.0033),
        ):
            ellipse(draw, side * dx, dz, half_width, half_height, (*BROW, 252))

    over(image, brows, 1.4)


def paint_mouth(image):
    """Lips, and the crease between them: the upper one under the moustache."""
    mouth = layer()
    draw = ImageDraw.Draw(mouth)

    # The upper lip, nearly all of which the moustache covers.
    ellipse(draw, 0.0, 0.0605, 0.0195, 0.0044, (*LIP, 245))
    # The crease.
    ellipse(draw, 0.0, 0.0538, 0.0192, 0.0015, (*LIP_DARK, 250))
    # The lower lip, and the shadow under it.
    ellipse(draw, 0.0, 0.0468, 0.0205, 0.0054, (*LIP, 245))
    ellipse(draw, 0.0, 0.0392, 0.0160, 0.0028, (*LIP_DARK, 175))

    over(image, mouth, 1.0)


def paint_age(image):
    """The lines that say the man is old rather than merely painted."""
    lines = layer()
    draw = ImageDraw.Draw(lines)

    # The forehead, in three creases rather than one.
    for z, half_width in ((0.196, 0.034), (0.208, 0.030), (0.219, 0.024)):
        ellipse(draw, 0.0, z, half_width, 0.0016, (*SHADOW, 96))

    for side in (-1, 1):
        # The folds beside the nose, which are the ones that carry the age.
        for step in range(3):
            ellipse(
                draw,
                side * (0.024 + (step * 0.005)),
                0.100 - (step * 0.011),
                0.0020,
                0.0105,
                (*SHADOW, 112),
            )

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

    over(image, lines, 2.4)


def paint_moustache_shadow(image):
    """Under the moustache, so no skin shows through the gap at its edge."""
    shadow = layer()
    draw = ImageDraw.Draw(shadow)

    # One band under the nose, plus a wing either side of it dropping towards the
    # corner of the mouth. The geometry is what stands proud; this is what stops a
    # rim of skin showing between it and the lip.
    ellipse(draw, 0.0, 0.0770, 0.0270, 0.0105, (*HAIR, 255))

    for side in (-1, 1):
        for step, (dx, dz, half_width, half_height) in enumerate((
            (0.014, 0.0755, 0.0100, 0.0092),
            (0.026, 0.0715, 0.0092, 0.0082),
            (0.037, 0.0655, 0.0074, 0.0068),
            (0.046, 0.0585, 0.0052, 0.0050),
        )):
            ellipse(draw, side * dx, dz, half_width, half_height, (*HAIR, 255))

    over(image, shadow, 1.2)


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
