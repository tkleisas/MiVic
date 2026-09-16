"""Generate MiVic's cutscene sets with Blender, headlessly.

    blender --background --python tools/blender/build_sets.py -- --out <dir>

A set is a diorama, not a level. Nothing walks through it, nothing collides with
it and no simulation ever sees it: it exists to be looked at from one side, from
a camera two to five metres away, for half a minute. That is why it is built
here rather than out of buildings — a dacha's study needs a desk, a lamp and a
window, not a footprint rule and a navmesh.

**The fourth wall is missing on purpose.** Every set is built against -Y and
open towards +Y, where the camera sits, so the room reads as a room rather than
as a box the camera is locked out of. Nothing is built on +Y except a lintel
across the top of the opening, which frames the shot the way a doorway does.

Coordinates are Blender's: metres, Z up, +Y towards the camera. The exporter
converts to glTF's Y-up, and the director places the set with an identity
transform, so a set is authored in the space it is looked at in.

Nothing here is named after a person. A set is "the study", "the office", "the
map room"; the props suggest a life — a desk lamp, a wall map, a telephone, a
bust on a plinth — and the fiction never captions them.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from build_vehicles import MATERIALS, box, clear_scene, cylinder, dome, export, join  # noqa: E402
from build_vehicles import paint  # noqa: E402


# --------------------------------------------------------------------------
# Palette. Warmer and browner than the battlefield: this is a room lit by one
# lamp and a window, not ground seen from above.
# --------------------------------------------------------------------------

WOOD = (0.24, 0.16, 0.10, 0.00)
WOOD_DARK = (0.15, 0.10, 0.06, 0.00)
WALL = (0.30, 0.28, 0.24, 0.00)
WALL_TRIM = (0.22, 0.20, 0.17, 0.00)
FLOOR = (0.20, 0.15, 0.11, 0.00)
RUG = (0.26, 0.09, 0.09, 0.00)
RUG_TRIM = (0.38, 0.30, 0.16, 0.00)
PAPER = (0.78, 0.76, 0.68, 0.00)
BRASS = (0.62, 0.48, 0.20, 0.00)
GLOW = (0.96, 0.90, 0.68, 0.00)
DAYLIGHT = (0.62, 0.70, 0.78, 0.00)
PLASTER = (0.52, 0.49, 0.44, 0.00)
LEATHER = (0.16, 0.12, 0.09, 0.00)
MAP_LAND = (0.36, 0.33, 0.24, 0.00)
MAP_LAND_ALT = (0.34, 0.30, 0.22, 0.00)
MAP_RED = (0.55, 0.14, 0.12, 0.00)
FOLDER = (0.42, 0.30, 0.16, 0.00)
BAKELITE = (0.12, 0.12, 0.12, 0.00)
BAKELITE_DARK = (0.09, 0.09, 0.09, 0.00)
BOOKS = (0.30, 0.20, 0.13, 0.00)


def slab(name, size, at, colour, variation=0.04):
    """One painted box, placed by its centre — the whole of what a set is made of."""
    part = box(name, size, offset=at)
    paint(part, colour, variation=variation)
    return part


def build_study():
    """A leader's study: a desk, a lamp, a wall map, a window and a chair.

    Deliberately small — 5.2 m by 4.4 m — because the camera never leaves the
    room and a larger one would only push the far wall out of the light.
    """
    root = bpy.data.objects.new("study", None)
    bpy.context.collection.objects.link(root)

    parts = []

    half_width = 2.6
    depth = 2.2
    height = 2.9

    # ---- the room: a floor and three walls ----------------------------------
    parts.append(slab("Floor", (half_width * 2, depth * 2, 0.10), (0.0, 0.0, -0.05), FLOOR))
    parts.append(slab("WallBack", (half_width * 2, 0.16, height), (0.0, -depth - 0.08, height * 0.5), WALL))
    parts.append(slab("WallLeft", (0.16, depth * 2, height), (-half_width - 0.08, 0.0, height * 0.5), WALL))
    parts.append(slab("WallRight", (0.16, depth * 2, height), (half_width + 0.08, 0.0, height * 0.5), WALL))

    # A lintel across the opening. The camera looks under it, so the top of the
    # frame is the room's own edge rather than a wall the camera is outside of.
    parts.append(slab("Lintel", (half_width * 2 + 0.3, 0.22, 0.30), (0.0, depth + 0.05, height - 0.15), WALL_TRIM))

    # Skirting, which is the cheapest way to say "this is a room and not a box".
    parts.append(slab("SkirtingBack", (half_width * 2, 0.06, 0.16), (0.0, -depth + 0.04, 0.08), WALL_TRIM))
    parts.append(slab("SkirtingLeft", (0.06, depth * 2, 0.16), (-half_width + 0.04, 0.0, 0.08), WALL_TRIM))
    parts.append(slab("SkirtingRight", (0.06, depth * 2, 0.16), (half_width - 0.04, 0.0, 0.08), WALL_TRIM))

    # ---- the window, and the cold light it is the only source of ------------
    parts.append(slab("WindowGlow", (1.35, 0.04, 1.35), (1.35, -depth + 0.02, 1.72), DAYLIGHT, variation=0.02))
    parts.append(slab("WindowSill", (1.55, 0.14, 0.08), (1.35, -depth + 0.09, 1.02), WALL_TRIM))
    parts.append(slab("WindowHead", (1.55, 0.14, 0.08), (1.35, -depth + 0.09, 2.42), WALL_TRIM))
    parts.append(slab("WindowJambLeft", (0.08, 0.14, 1.48), (0.61, -depth + 0.09, 1.72), WALL_TRIM))
    parts.append(slab("WindowJambRight", (0.08, 0.14, 1.48), (2.09, -depth + 0.09, 1.72), WALL_TRIM))
    parts.append(slab("WindowBar", (1.35, 0.10, 0.05), (1.35, -depth + 0.08, 1.72), WALL_TRIM, variation=0.02))

    # ---- the wall map: a board and the coloured shapes pinned to it ---------
    parts.append(slab("MapBoard", (1.60, 0.05, 1.05), (-1.35, -depth + 0.06, 1.80), WOOD_DARK))
    parts.append(slab("MapSheet", (1.44, 0.02, 0.90), (-1.35, -depth + 0.10, 1.80), PAPER, variation=0.06))
    parts.append(slab("MapLandA", (0.44, 0.02, 0.30), (-1.62, -depth + 0.13, 1.94), MAP_LAND, variation=0.05))
    parts.append(slab("MapLandB", (0.30, 0.02, 0.22), (-1.05, -depth + 0.13, 1.62), MAP_LAND_ALT, variation=0.05))
    parts.append(slab("MapArrow", (0.06, 0.02, 0.44), (-1.22, -depth + 0.14, 1.84), MAP_RED, variation=0.02))
    parts.append(slab("MapArrowHead", (0.16, 0.02, 0.10), (-1.22, -depth + 0.14, 2.08), MAP_RED, variation=0.02))

    # ---- the desk, and what is on it ----------------------------------------
    parts.append(slab("Desk", (2.40, 1.05, 0.08), (0.0, -1.15, 0.77), WOOD))
    parts.append(slab("DeskApron", (2.24, 0.06, 0.20), (0.0, -0.66, 0.66), WOOD_DARK))
    parts.append(slab("DeskPanelLeft", (0.10, 0.95, 0.72), (-1.10, -1.15, 0.38), WOOD_DARK))
    parts.append(slab("DeskPanelRight", (0.10, 0.95, 0.72), (1.10, -1.15, 0.38), WOOD_DARK))
    parts.append(slab("DeskBack", (2.24, 0.06, 0.55), (0.0, -1.62, 0.30), WOOD_DARK))

    parts.append(slab("DeskPapers", (0.38, 0.28, 0.03), (-0.62, -1.10, 0.83), PAPER, variation=0.05))
    parts.append(slab("DeskFolder", (0.30, 0.40, 0.05), (-0.15, -1.18, 0.84), FOLDER, variation=0.04))
    parts.append(slab("DeskTelephone", (0.24, 0.20, 0.12), (0.72, -1.10, 0.87), BAKELITE))
    parts.append(slab("DeskTelephoneHandset", (0.26, 0.06, 0.05), (0.72, -1.02, 0.96), BAKELITE_DARK))
    parts.append(slab("DeskTray", (0.30, 0.22, 0.06), (0.72, -1.45, 0.84), LEATHER, variation=0.04))

    # ---- the desk lamp: the room's warm light, and its brightest object -----
    parts.append(cylinder("LampBase", 0.11, 0.04, segments=14, axis="z", offset=(-0.92, -1.44, 0.83)))
    parts.append(cylinder("LampStem", 0.018, 0.42, segments=10, axis="z", offset=(-0.92, -1.44, 1.06)))
    parts.append(cylinder("LampShade", 0.13, 0.16, segments=14, axis="z", offset=(-0.92, -1.44, 1.32)))
    parts.append(slab("LampGlow", (0.16, 0.16, 0.02), (-0.92, -1.44, 1.23), GLOW, variation=0.02))

    for part in parts[-4:]:
        paint(part, GLOW if part.name == "LampGlow" else BRASS, variation=0.03)

    # ---- the chair, mostly hidden behind the desk ---------------------------
    parts.append(slab("ChairSeat", (0.54, 0.52, 0.09), (0.30, -1.95, 0.47), LEATHER))
    parts.append(slab("ChairBack", (0.54, 0.10, 0.62), (0.30, -2.18, 0.80), LEATHER))
    parts.append(slab("ChairPost", (0.10, 0.10, 0.44), (0.30, -1.95, 0.23), WOOD_DARK))

    # ---- a bookcase against the left wall -----------------------------------
    parts.append(slab("Bookcase", (0.42, 1.80, 2.05), (-2.30, -0.55, 1.02), WOOD_DARK))
    for shelf in range(4):
        z = 0.32 + (shelf * 0.52)
        parts.append(slab("BookShelf", (0.36, 1.68, 0.05), (-2.24, -0.55, z), WOOD))
        parts.append(slab("Books", (0.26, 1.40, 0.30), (-2.26, -0.55, z + 0.19), BOOKS, variation=0.10))

    # ---- the rug, and the bust on its plinth --------------------------------
    parts.append(slab("Rug", (2.70, 3.10, 0.02), (0.10, 0.35, 0.01), RUG, variation=0.05))
    parts.append(slab("RugTrim", (2.86, 3.26, 0.01), (0.10, 0.35, 0.005), RUG_TRIM, variation=0.04))

    parts.append(slab("Plinth", (0.34, 0.34, 0.95), (1.95, -1.85, 0.48), PLASTER, variation=0.04))
    bust = dome("Bust", 0.15, (0.16, 0.15, 0.24), segments=12, rings=5)
    bust.location = (1.95, -1.85, 1.09)
    parts.append(bust)
    paint(bust, PLASTER, variation=0.05)

    join(root, parts)
    return root


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate MiVic cutscene sets.")
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

    # A set is named for what it is, never for whose it is. See the module docstring.
    emit("set_study", build_study)

    print(f"done: {len(written)} sets")


if __name__ == "__main__":
    main()
