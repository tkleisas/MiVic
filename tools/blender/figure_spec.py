"""Where the figure's features are. One definition, read by the mesh and the paint.

This module exists because the same feature height was written down in three places
and they disagreed. The generator carried `EYE_Z = 0.164` at the top of the file, the
painter carried `EYE_Z = 0.152`, and the head's own warp used the bare literal
`v = 0.466` which is a third number again — 0.1645 in metres. So the eye sockets cut
into the surface were twelve millimetres above the eyes painted on it, the nose was ten
millimetres out, and the brow was three.

None of those were visible as a bug, because each was right where it stood. They were
only wrong together, and nothing in the repository compared them — the generator's
constants were not even read by the generator; they were a comment with a name, kept in
step by hand and therefore not kept in step.

Two rules follow, and they are the reason this is a module rather than a tidy-up:

1. **A feature is defined once, in metres above the neck.** Both consumers derive from
   it — the painter reads the height directly, and the warp converts it to the `v` its
   surface runs along with `face_uv.head_v`. The painter's numbers were measured off the
   frontal portrait, so they are the ones that survived; the warp's literals were tuned
   by eye and are now derived instead.

2. **A feature that is positioned relative to another is defined as the difference.**
   The brow sits a measured gap above the eye, not at a height of its own. The gap is
   what the portrait gives, and when the eye moved down the brow had to move with it —
   which it did not, because they were two independent literals. Writing the gap is
   what makes that impossible to get wrong again.
"""

import face_uv

#: The eye line: 51 per cent of the way from the crown to the chin, measured off the
#: portrait's own dark runs at eye height.
EYE_Z = 0.152

#: How far above the eye the brow sits: 12.2 per cent of the head's height, measured
#: the same way. A brow on top of an eye is a startled face rather than a heavy one.
BROW_GAP = 0.0321
BROW_Z = EYE_Z + BROW_GAP

#: The tip of the nose: 68 per cent down.
NOSE_TIP_Z = 0.111

#: The crease between the lips, and the lip line the pipe hangs from.
MOUTH_Z = 0.080
LIP_Z = 0.095

#: The ear, which is the one feature that is geometry rather than paint.
EAR_Z = 0.146

#: Across the face: the centre of an eye, and the brow's arch and its fall at the
#: outer end.
EYE_X = 0.030
BROW_ARCH = 0.0070
BROW_FALL = 0.0076


def v(z):
    """The `v` the head's surface runs along at height `z` above the neck.

    The warp is parameterised from the crown at 0 to under the chin at 1, so a feature
    placed by height has to be converted rather than written down twice.
    """
    return face_uv.head_v(z)
