"""Where a point on the head lands on the face texture — the one definition of it.

Two programs need this and they must not disagree. The generator writes the head's
texture coordinates from the grid it built the head on; the painter puts an eye
where the eye is. If the two disagree by a few per cent the eye is painted on the
cheek, and the symptom looks like a painting bug rather than a mapping bug.

The grid's own coordinates are `u` around the head — 0.25 at the face, 0.75 at the
back — and `v` from the crown at 0 to under the chin at 1. A face occupies a fifth
of the way round, so a linear map throws away four fifths of the texture on hair
and the back of a skull nobody looks at. `wrap_u` therefore expands the front and
squeezes the back, which is free resolution where it is wanted and none where it
is not.

The head is the ellipsoid `_head_surface` builds, before the jaw taper: the taper
only applies below the mouth, where the only thing painted is a chin.
"""

import math

HALF_X = 0.096
HALF_Y = 0.117
HALF_Z = 0.130
CENTRE_Z = 0.150

#: The skull's two shape exponents, which `_head_surface` also uses. They are the
#: difference between a head and an egg, and a painter working from the ellipsoid
#: instead of from these puts every feature in the wrong place — the face's width
#: at a given height is not a sine of it.
RADIUS_POWER = 0.97
PLAN_POWER = 2.0

#: How hard the front is expanded. 1 is a linear map; smaller is more face and a
#: more crushed back of the head.
FRONT_POWER = 0.5


def wrap_u(u):
    """The texture's x for a grid `u`, expanding the face and squeezing the back."""
    # Distance around from the face, in -0.5..0.5 with the back of the skull at the
    # far end. The wrap is what keeps u = 0 and u = 1 the same place.
    distance = ((u - 0.25 + 0.5) % 1.0) - 0.5
    magnitude = abs(distance) / 0.5
    expanded = math.copysign(0.5 * (magnitude ** FRONT_POWER), distance)

    return 0.5 + expanded


def head_v(z):
    """The grid `v` at a height on the head, in head-local metres."""
    cosine = (z - CENTRE_Z) / HALF_Z

    return math.acos(max(-1.0, min(1.0, cosine))) / math.pi


def head_z(v):
    """The height on the head at a grid `v`."""
    return CENTRE_Z + (math.cos(math.pi * v) * HALF_Z)


def head_half_width(v):
    """Half the head's width at a grid `v`, in metres."""
    return (math.sin(math.pi * v) ** RADIUS_POWER) * HALF_X


def head_x(theta, v):
    """How far round the head a point at `theta` and `v` sits, in metres."""
    radius = math.sin(math.pi * v) ** RADIUS_POWER
    cosine, sine = math.cos(theta), math.sin(theta)
    squircle = (abs(cosine) ** PLAN_POWER + abs(sine) ** PLAN_POWER) ** (-1.0 / PLAN_POWER)

    return radius * cosine * squircle * HALF_X


def face_uv(x, z):
    """Where a point on the front of the head lands on the texture.

    `x` is across the head in metres, positive to the model's side, and `z` is up
    from the neck. The answer is in 0..1 texture space with the origin at the top
    left, which is the order an image is written in.

    The angle is solved for rather than taken from an arc cosine, because the head
    is not an ellipse: its plan is a rounded rectangle, so the width at a given
    angle depends on the angle in a way that has no closed form. A short bisection
    is cheaper than the alternative, which is a painter and a generator that
    disagree about where a cheekbone is.
    """
    v = head_v(z)
    target = min(abs(x), head_x(0.0, v))
    low, high = 0.0, math.pi * 0.5

    for _ in range(28):
        middle = (low + high) * 0.5

        if head_x(middle, v) > target:
            low = middle
        else:
            high = middle

    u = (low + high) * 0.5 / (2.0 * math.pi)

    return wrap_u(u), v


def face_uv_across(x, z):
    """`face_uv` for a point off the middle, mirrored for the other side.

    Features are painted as a pair, and this is what makes them one: the head is
    symmetric, so an eye at -x is the same texture coordinate, mirrored, as an eye
    at +x.
    """
    u, v = face_uv(abs(x), z)

    return (1.0 - u if x < 0 else u), v


def unwrap_u(texture_u):
    """The grid `u` a texture coordinate came from: `wrap_u` backwards.

    Painting needs this and the generator does not, because the painter is handed a
    texture coordinate and the hairline is written in grid terms.
    """
    expanded = texture_u - 0.5
    magnitude = abs(expanded) / 0.5
    distance = math.copysign(0.5 * (magnitude ** (1.0 / FRONT_POWER)), expanded)

    return (0.25 + distance) % 1.0


def hairline(u):
    """The grid `v` the hair reaches down to, as the hair mesh's own `keep` computes it.

    Must match `_hair_shell`'s `keep`, or the painted hair and the hair mesh end in
    different places and the forehead wears a band of the wrong one.

    Read it as a height: `v` is 0 at the crown, so a *smaller* hairline is more
    forehead. The first version had the smallest value at the middle of the face,
    which is a widow's peak — hair coming down in the centre and receding at the
    sides. What the reference has is the opposite: a high forehead with the corners
    receding, so the middle is the lowest the hair comes and the temples are the
    highest.
    """
    around = min(abs(u - 0.25), 1.0 - abs(u - 0.25)) * 2.0

    peak = math.exp(-((around / 0.09) ** 2))
    return 0.29 + (0.42 * (around ** 0.8)) - (0.13 * math.exp(-(((around - 0.30) / 0.15) ** 2))) + (0.025 * peak)


def hairline_at(texture_u):
    """The hairline as a texture coordinate, for a painter working in image space."""
    return hairline(unwrap_u(texture_u))
