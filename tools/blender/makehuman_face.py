"""What the imported MakeHuman head is given that the asset packs do not supply.

The system pack ships clothes, hair, eyes, eyebrows, teeth and skins, and no facial
hair at all — ten hair assets and not one moustache. The figure this briefing is built
around is recognised by its moustache, so it is worn as a mesh from the community
bodyparts06 pack (see `docs/MAKEHUMAN.md` §1) — and this file paints the skin
*underneath* it. A card moustache over bare lip shows skin through the gaps between
its strands; painted backing in the mesh's own colour shows hair instead. Paint cannot
come loose from the skin, and it deforms with the face because it *is* the face.

Every pixel is placed by measuring the mesh in front of it, which matters because the
mapping from the face to the texture is an asset pack's business and not something to
assume:

  * Forward is **-Y**. Measured, not assumed: the most forward vertex of the head is on
    the midline at x = 0.0, which is what a nose is.
  * The nose tip is the vertex that is furthest along -Y, at z = 1.5131 on this body.
    The upper lip is 21 mm below it, which is the usual subnasale distance for an adult
    male and the only number here that is not read off the mesh.
  * The paint region is the set of mesh **triangles** whose vertices lie in one box around
    that lip. Their UVs are rasterised straight into the texture, so the moustache lands
    wherever the asset pack decided the lip's texels are — no UV layout is hard-coded, and
    a future skin with a different atlas needs no change here.

Run through `build_makehuman.py`, which imports this.
"""

import math

#: Where the moustache is, relative to the nose tip, in metres. A moustache this wide and
#: this deep is a big one, which is the point: it is the feature the figure is read by.
LIP_BELOW_NOSE = 0.024
HALF_WIDTH = 0.052
HALF_HEIGHT = 0.019
DROOP = 0.017              # how far the ends hang below the middle
FRONT_OF_FACE = 0.0        # only paint in front of this y; the head's back is behind it

#: The hair colour, and how far the paint is allowed to move the skin towards it.
#: The same near-black the mesh wears: the paint is the backing under the moustache
#: mesh, so gaps between its strands show this and not skin — or a lighter ghost of
#: the moustache's first grey.
COLOUR = (44.0, 40.0, 36.0)
OPACITY = 0.95

#: The scalp and brow shadow. The hair and brow meshes are sparse alpha cards drawn
#: opaque, so between the strands the renderer shows the skin *under* them — lit by
#: the lamp, a bare scalp reads as white streaks and white brows on a dark-haired man.
#: Skin under hair is in shadow, so it is painted in shadow: a dark band from the
#: brow line up over the crown's front, fading with height, and two strong patches
#: where the brows sit.
SHADOW_COLOUR = (38.0, 32.0, 27.0)
BROW_ABOVE_NOSE = 0.052
BROW_OFF_MIDLINE = 0.033
BROW_HALF_WIDTH = 0.024
BROW_HALF_HEIGHT = 0.010
SCALP_LOW = 0.040      # shadow starts just above the brows
SCALP_HIGH = 0.220     # and fades out toward the crown


def scalp_box(nose_tip):
    """The box of face that carries the scalp shadow: brow line to hairline and on."""
    return {
        "min_z": nose_tip.z + SCALP_LOW,
        "max_z": nose_tip.z + SCALP_HIGH,
        "half_x": 0.10,
    }


def scalp_coverage(point, box):
    """How much shadow the scalp gets at this point: strong at the brow line,
    fading toward the crown, and never past the sides of the forehead."""
    if point.y > FRONT_OF_FACE:
        return 0.0
    if point.z < box["min_z"] or point.z > box["max_z"]:
        return 0.0
    if abs(point.x) > box["half_x"]:
        return 0.0

    up = (point.z - box["min_z"]) / (box["max_z"] - box["min_z"])
    fade = (1.0 - up) * 0.55
    side = 1.0 - max(0.0, (abs(point.x) - 0.055) / 0.045)

    # The brows themselves stay darker: two short bands above the eyes.
    brow = 0.0
    if abs(point.z - (box["min_z"] + BROW_ABOVE_NOSE - SCALP_LOW)) < BROW_HALF_HEIGHT:
        for sign in (-1.0, 1.0):
            dx = abs(point.x - sign * BROW_OFF_MIDLINE)
            if dx < BROW_HALF_WIDTH:
                brow = max(brow, 0.85 * (1.0 - dx / BROW_HALF_WIDTH))

    return min(0.85, max(fade * side, brow))


def paint_shadow(proxy, image, box):
    """Rasterise the scalp shadow into the skin map — the same measured triangles
    as the moustache, blended toward SHADOW_COLOUR instead of the hair colour."""
    width, height = image.size
    pixels = list(image.pixels)
    uv_layer = proxy.data.uv_layers.active.data
    world = [vertex.co for vertex in proxy.data.vertices]

    painted = 0
    for polygon in proxy.data.polygons:
        indices = list(polygon.vertices)
        if len(indices) < 3:
            continue

        positions = [world[i] for i in indices]
        if any(p.y > FRONT_OF_FACE for p in positions):
            continue
        if min(p.z for p in positions) > box["max_z"] or max(p.z for p in positions) < box["min_z"]:
            continue
        if max(abs(p.x) for p in positions) > box["half_x"]:
            continue

        for corner in range(1, len(indices) - 1):
            triangle = (0, corner, corner + 1)
            loop_uvs = []
            for c in triangle:
                vertex_index = indices[c]
                found = None
                for loop_index in polygon.loop_indices:
                    if proxy.data.loops[loop_index].vertex_index == vertex_index:
                        found = uv_layer[loop_index].uv
                        break
                loop_uvs.append(found if found is not None else (0.0, 0.0))

            painted += _rasterise_shadow(pixels, width, height, loop_uvs,
                                         [positions[c] for c in triangle], box)

    image.pixels = pixels
    return painted


def _rasterise_shadow(pixels, width, height, uvs, positions, box):
    """Fill one UV triangle, blended toward the shadow colour by coverage."""
    us = [u[0] * width for u in uvs]
    vs = [u[1] * height for u in uvs]

    min_u = max(0, int(math.floor(min(us))))
    max_u = min(width - 1, int(math.ceil(max(us))))
    min_v = max(0, int(math.floor(min(vs))))
    max_v = min(height - 1, int(math.ceil(max(vs))))
    if max_u < min_u or max_v < min_v:
        return 0

    x0, y0 = us[0], vs[0]
    x1, y1 = us[1], vs[1]
    x2, y2 = us[2], vs[2]
    denominator = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
    if abs(denominator) < 1e-12:
        return 0

    touched = 0
    for v in range(min_v, max_v + 1):
        for u in range(min_u, max_u + 1):
            px, py = u + 0.5, v + 0.5
            a = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denominator
            b = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denominator
            c = 1.0 - a - b
            if a < -0.002 or b < -0.002 or c < -0.002:
                continue

            point = positions[0] * a + positions[1] * b + positions[2] * c
            alpha = scalp_coverage(point, box)
            if alpha <= 0.0:
                continue

            offset = (v * width + u) * 4
            for channel in range(3):
                old = pixels[offset + channel]
                pixels[offset + channel] = old + (SHADOW_COLOUR[channel] / 255.0 - old) * alpha
            touched += 1

    return touched


#: Deterministic grain, so two builds of the same body give the same face.
GRAIN_SEED = 0x9E3779B97F4A7C15
GRAIN_SCALE = 900.0


def _grain(x):
    """A stable value in [0, 1) from an integer — a cheap hash, not a random number.

    Deterministic on purpose: a texture that differs between two builds of the same body
    turns every later comparison into a coin toss.
    """
    x = (x * GRAIN_SEED) & 0xFFFFFFFFFFFFFFFF
    x ^= x >> 33
    x = (x * 0xFF51AFD7ED558CCD) & 0xFFFFFFFFFFFFFFFF
    x ^= x >> 29
    return (x & 0xFFFFFF) / float(0x1000000)


def find_nose_tip(proxy, anchor, radius=0.16):
    """The nose tip as (position, vertex index) in the mesh's own space.

    The most forward point **of the head**, which is a nose on any human — and the head is
    the part within `radius` of `anchor`, which the caller takes from the head bone.
    Searching every vertex instead is what the first version did, and in this rig's rest
    pose the hands reach further forward than the nose does (y = -0.34 against -0.16), so
    it returned a knuckle at hip height and called it a face. Found by measurement rather
    than a constant, because a constant belongs to whichever body it was measured on.
    """
    best = None
    for vertex in proxy.data.vertices:
        position = vertex.co
        if (position - anchor).length > radius:
            continue
        if best is None or position.y < best[0].y:
            best = (position, vertex.index)
    return best


def moustache_box(nose_tip):
    """The box of face, in world space, that the moustache occupies."""
    lip_z = nose_tip.z - LIP_BELOW_NOSE
    return {
        "lip_z": lip_z,
        "min_z": lip_z - HALF_HEIGHT * 2.2,
        "max_z": lip_z + HALF_HEIGHT * 2.2,
        "half_x": HALF_WIDTH * 1.45,
    }


def coverage(point, box):
    """How much moustache is at this point of the face, from 0 to 1.

    An ellipse with the ends dropped, so it reads as a moustache and not as a smear. The
    falloff is smooth because the paint is being laid into a photograph of a face: a hard
    edge would read as a decal.
    """
    if point.y > FRONT_OF_FACE:
        return 0.0
    if point.z < box["min_z"] or point.z > box["max_z"]:
        return 0.0
    if abs(point.x) > box["half_x"]:
        return 0.0

    across = point.x / HALF_WIDTH
    centre_z = box["lip_z"] - DROOP * across * across
    down = (point.z - centre_z) / HALF_HEIGHT
    radius = math.sqrt(across * across + down * down)
    if radius >= 1.0:
        return 0.0

    # Full in the middle, fading over the last third of the radius.
    return min(1.0, (1.0 - radius) / 0.34)


def paint_skin(proxy, image, box):
    """Rasterise the moustache into `image` through the mesh's own UVs.

    Per triangle, because a triangle is the smallest thing that both covers an area of
    texture and knows where on the face that area is. Painting the vertices alone would
    leave the texels between them untouched, which at this resolution is most of them.
    """
    width, height = image.size
    pixels = list(image.pixels)
    uv_layer = proxy.data.uv_layers.active.data
    # Mesh space, the same space `find_nose_tip` measured the nose in. The two must agree
    # or the paint lands somewhere else on the face; both read `vertex.co` and neither
    # applies an object transform.
    world = [vertex.co for vertex in proxy.data.vertices]

    painted = 0
    for polygon in proxy.data.polygons:
        indices = list(polygon.vertices)
        if len(indices) < 3:
            continue

        positions = [world[i] for i in indices]
        # Cheap rejection first: the whole triangle must be in the box before it is worth
        # rasterising, and the vast majority of the mesh is nowhere near the face.
        if any(p.y > FRONT_OF_FACE for p in positions):
            continue
        if min(p.z for p in positions) > box["max_z"] or max(p.z for p in positions) < box["min_z"]:
            continue
        if max(abs(p.x) for p in positions) > box["half_x"]:
            continue

        # Fan the polygon into triangles around its first vertex.
        for corner in range(1, len(indices) - 1):
            triangle = (0, corner, corner + 1)
            # Loops, not vertices: a vertex on a seam has one UV per side, and the side
            # that faces the camera is the one the paint has to follow.
            loop_uvs = []
            for c in triangle:
                vertex_index = indices[c]
                found = None
                for loop_index in polygon.loop_indices:
                    if proxy.data.loops[loop_index].vertex_index == vertex_index:
                        found = uv_layer[loop_index].uv
                        break
                loop_uvs.append(found if found is not None else (0.0, 0.0))

            painted += _rasterise(pixels, width, height, loop_uvs,
                                  [positions[c] for c in triangle], box)

    image.pixels = pixels
    return painted


def _rasterise(pixels, width, height, uvs, positions, box):
    """Fill one UV triangle into the texture, masked by the moustache shape."""
    us = [u[0] * width for u in uvs]
    vs = [u[1] * height for u in uvs]

    min_u = max(0, int(math.floor(min(us))))
    max_u = min(width - 1, int(math.ceil(max(us))))
    min_v = max(0, int(math.floor(min(vs))))
    max_v = min(height - 1, int(math.ceil(max(vs))))
    if max_u < min_u or max_v < min_v:
        return 0

    # Barycentric setup in UV space.
    x0, y0 = us[0], vs[0]
    x1, y1 = us[1], vs[1]
    x2, y2 = us[2], vs[2]
    denominator = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
    if abs(denominator) < 1e-12:
        return 0

    touched = 0
    for v in range(min_v, max_v + 1):
        for u in range(min_u, max_u + 1):
            px, py = u + 0.5, v + 0.5
            a = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denominator
            b = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denominator
            c = 1.0 - a - b
            if a < -0.002 or b < -0.002 or c < -0.002:
                continue

            point = positions[0] * a + positions[1] * b + positions[2] * c
            strength = coverage(point, box)
            if strength <= 0.0:
                continue

            grain = 0.72 + 0.28 * _grain(u * 73856093 ^ v * 19349663)
            alpha = min(1.0, strength * grain * OPACITY)
            offset = (v * width + u) * 4
            for channel in range(3):
                old = pixels[offset + channel]
                pixels[offset + channel] = old + (COLOUR[channel] / 255.0 - old) * alpha
            touched += 1

    return touched
