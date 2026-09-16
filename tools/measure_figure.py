#!/usr/bin/env python3
"""Measure the briefing figure against a baseline, as numbers rather than as a look.

This exists because the same measurements were written and thrown away about thirty
times while the figure was being built, and three of those throwaway scripts were
wrong in ways that produced confident, meaningless numbers:

  * The crop ran on past the chin into the neck, so the "widest row" of the head was
    the neck and every other row was a fraction of that. The model was then changed to
    match, and made worse.
  * The background was told apart from the figure by brightness, and in the briefing's
    close-up the study's brown wall is neither clearly figure nor clearly ground, so
    every row measured the wall.
  * The moustache was found by looking for dark pixels, and the reference's moustache
    is grey with dark edges, so it came back as a fragment.

Each of those is fixed here by construction rather than by care:

  * The crop is crown to chin and the tool owns it. `render` frames the head with the
    same camera the art round uses and cuts the same fractions, so the frame is a
    property of the tool and not of whoever is measuring that day.
  * The background is removed by flooding in from the four corners, which separates the
    figure from whatever is behind it whatever colour that happens to be.
  * Colour is sampled from a patch at a stated position inside the crop, never searched
    for by colour. Establish where the thing is, then measure inside it.

Usage
-----
    # Once, wherever the reference image is: record what the figure should measure.
    tools/measure_figure.py capture --image REF.png --box 45,27,397,347 \\
        --out tools/figure_baseline.json

    # Every round, and in CI: render the committed assets and compare.
    tools/measure_figure.py check

    # Against a crop something else already wrote (tools/face-round.sh writes one).
    tools/measure_figure.py check --crop artifacts/probe/face-chin-crop.png --no-render

The baseline holds the *reference's* numbers, not the reference's pixels, so CI needs
neither the reference image nor Blender: it builds, renders the committed .glb and .png
through the real renderer, and compares twelve widths and a colour against the file.

Exit status is 0 within tolerance and 1 outside it, so it can be a build gate.
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

#: Crown to chin, as fractions of the close-up frame. The head is centred in that
#: frame and 263 mm tall in a 415 mm one, so it runs from 0.17 to 0.83; the crop takes
#: a little margin either side so a slightly taller head is not clipped.
CROP = (0.28, 0.17, 0.72, 0.83)

#: The camera the figure is looked at from: 0.6 m, level with the head, face on. The
#: same framing tools/face-round.sh renders, so the tool and the art round measure the
#: same picture.
FACE_CAMERA = {"milliseconds": 0, "x": 60, "y": 1600, "z": 1100,
               "targetX": 0, "targetY": 1590, "targetZ": 1700}

#: Where inside the crop each colour is read from, as a fraction of the crop. Only the
#: forehead is gated by default: it is the one patch that is bare skin in both the
#: reference and the built figure at this framing. The others are recorded for
#: information, because a patch that lands on hair in one of the two images is not a
#: measurement of anything.
PATCHES = {
    "forehead": (0.440, 0.322),
    "cheek": (0.341, 0.611),
    "chin": (0.526, 0.884),
}

#: How far the built figure is allowed to drift from the reference before this fails.
#: Set from what the figure actually achieves (mean 5.7 points, worst 10) with room to
#: work, and tight enough to catch the twenty-seven-point errors that started this.
TOLERANCE_MEAN = 8.0
TOLERANCE_WORST = 16.0
TOLERANCE_RED_GREEN = 12

#: The reference portrait's own crown-to-chin box, so `capture` can be run with no
#: arguments by anyone who has the image.
PORTRAIT_BOX = (45, 27, 397, 347)


def _clear_background(work, thresh=40):
    """Flood the background out from the four corners.

    Corner seeding, and not seeding all the way round the border, which was tried: a
    seed placed every few pixels along the edges puts seeds next to the figure, and where
    the figure meets its ground softly — the shadowed jaw against the briefing's dim room
    — the fill walks in. It ate row ten of twelve and reported the chin at 31 per cent of
    the face's width, which is a number that looks like a measurement and is not one.

    Corners are far enough from a figure that fills the frame that they are always
    ground, and they are clean on the render this tool exists to check. They are not
    enough on the painted portrait, whose ground is a gradient: see `cmd_capture`, which
    refuses what this cannot measure rather than measuring it anyway.
    """
    w, h = work.size
    for corner in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)):
        ImageDraw.floodfill(work, corner, (255, 0, 255), thresh=thresh)


def silhouette_widths(image):
    """The figure's width on every row, with the background flooded away, and which rows
    ran off the side of the frame.

    A brightness test cannot do this: the briefing has a brown wall behind the head and
    the portrait has its own ground, and both read as "dark" next to hair.

    The clipping report matters more than it looks. The profile is normalised to its own
    widest row, so a crop too narrow to hold the head does not distort the shape much —
    the rows that are clipped are the ones setting the maximum, and everything else
    stays roughly in proportion. Measured that way, a frame that cut the head off at the
    ears scored a mean of 5.4 and passed. A row that touches the edge is not a
    measurement, whatever it says, so it is reported as one.
    """
    work = image.copy()
    _clear_background(work)
    w, h = work.size

    pixels = work.load()
    widths, clipped, background = [], [], 0
    for y in range(h):
        xs = [x for x in range(w) if pixels[x, y] != (255, 0, 255)]
        background += w - len(xs)
        if len(xs) > 3:
            widths.append(max(xs) - min(xs))
            if min(xs) <= 0 or max(xs) >= w - 1:
                clipped.append(y)
        else:
            widths.append(0)
    return widths, clipped, background / float(w * h)


def profile(widths, steps):
    """The widths at `steps` evenly spaced rows from crown to chin, as fractions of the
    widest of them, and which of those rows were clipped at the edge of the frame."""
    span = len(widths) - 1
    rows = [int(s * span / (steps - 1)) for s in range(steps)]
    picked = [widths[r] for r in rows]
    widest = max(picked) or 1
    return [v / widest for v in picked], picked.index(max(picked)), rows


def patch_colour(image, fx, fy, radius):
    w, h = image.size
    cx, cy = int(w * fx), int(h * fy)
    x0, x1 = max(0, cx - radius), min(w, cx + radius)
    y0, y1 = max(0, cy - radius), min(h, cy + radius)
    pixels = image.load()
    n = r = g = b = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            q = pixels[x, y]
            r += q[0]
            g += q[1]
            b += q[2]
            n += 1
    return [round(r / n), round(g / n), round(b / n)] if n else [0, 0, 0]


def measure(image, steps):
    """Everything this tool knows about one crown-to-chin image."""
    widths, clipped, background = silhouette_widths(image)
    prof, widest_row, rows = profile(widths, steps)
    radius = max(6, int(image.width * 0.03))

    return {
        "profile": prof,
        "widest_row": widest_row,
        "clipped_rows": [i for i, r in enumerate(rows) if r in set(clipped)],
        "background_fraction": background,
        "patches": {name: patch_colour(image, fx, fy, radius)
                    for name, (fx, fy) in PATCHES.items()},
    }


# --------------------------------------------------------------------------- capture

def cmd_capture(args):
    """Record what the figure should measure, from a reference image or from the
    figure itself.

    Both modes exist because they answer different questions and only one of them is
    always answerable.

    `--image` measures a reference and gives a *distance from the reference*. It needs a
    reference whose figure separates cleanly from its ground, and it refuses when it does
    not: the painted portrait this figure was built against has a ground that is a
    gradient and a crown that is grey, so a flood fill dies partway down it and a warmth
    test loses the top of the head. Measured anyway, it produced a head-shaped profile
    that was wrong — which is the worst kind of number.

    `--from-crop` freezes what the figure measures now and gives a *distance from
    itself*. That is a regression gate rather than a target, it needs no reference at
    all, and it is what CI can actually run.
    """
    origin = args.from_crop or args.image
    if origin is None:
        print("capture needs --image (a reference) or --from-crop (the figure now)",
              file=sys.stderr)
        return 2

    image = Image.open(origin).convert("RGB")
    box = None
    if args.from_crop:
        crop = image
    else:
        box = tuple(int(v) for v in args.box.split(",")) if args.box else PORTRAIT_BOX
        crop = image.crop(box)

    result = measure(crop, args.steps)

    # Refuse a measurement whose frame cut the figure off, and refuse one whose
    # background would not come out. The second is the silent one: a fill that dies on a
    # gradient leaves the ground in the picture, and the widths that come back are the
    # crop's rather than the head's. A profile like that still looks like a head.
    if result["clipped_rows"]:
        rows = result["clipped_rows"]
        print(f"refusing to capture: the frame cuts the figure off on rows {rows} of "
              f"{args.steps}. Widen the box or use --from-crop.", file=sys.stderr)
        return 1
    if args.image:
        steps = len(result["profile"])
        widest = result["widest_row"]
        edge = max(1, (steps - 1) // 7)
        if widest < edge or widest > steps - 1 - edge:
            print(f"refusing to capture: the widest of {steps} rows is row {widest}, at "
                  f"the very top or bottom of the head. A crown is narrow and a chin is "
                  f"narrow; a profile that is widest at either end is the frame being "
                  f"measured rather than the figure. This is what a background that will "
                  f"not come out looks like, and it looks like a head.", file=sys.stderr)
            return 1
        if result["background_fraction"] < 0.05:
            print(f"refusing to capture: only "
                  f"{result['background_fraction'] * 100:.1f}% of the frame was "
                  f"background, so the fill died. Use --from-crop, or a reference with a "
                  f"ground a flood fill can cross.", file=sys.stderr)
            return 1

    baseline = {
        "steps": args.steps,
        "source": os.path.basename(origin),
        "mode": "frozen" if args.from_crop else "reference",
        "box": list(box) if box else None,
        "note": ("A frozen measurement of the figure itself: this is a regression gate, "
                 "not a distance from a reference."
                 if args.from_crop else
                 "Measured from the reference's own crown-to-chin box. The tool compares "
                 "against these numbers, so CI needs neither this image nor Blender."),
        "profile": result["profile"],
        "widest_row": result["widest_row"],
        "patches": result["patches"],
        "tolerances": {
            "mean": args.tolerance_mean if args.tolerance_mean is not None
            else TOLERANCE_MEAN,
            "worst": args.tolerance_worst if args.tolerance_worst is not None
            else TOLERANCE_WORST,
            "red_green": TOLERANCE_RED_GREEN,
            "gated_patches": ["forehead"],
        },
    }

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(baseline, handle, indent=2)
        handle.write("\n")

    print(f"wrote {os.path.relpath(args.out, ROOT)}   ({baseline['mode']})")
    print("  profile    " + " ".join(f"{v * 100:3.0f}" for v in result["profile"]))
    print(f"  widest row {result['widest_row']}")
    for name, rgb in result["patches"].items():
        print(f"  {name:10s} {rgb}  r-g {rgb[0] - rgb[1]}")
    return 0


# ---------------------------------------------------------------------------- render

def configuration_paths(configuration):
    binary = os.path.join(ROOT, "src", "MiVic.Game", "bin", configuration, "net9.0")
    return binary, os.path.join(binary, "Content", "Cutscenes")


def cmd_render(args):
    """Photograph the committed assets at the face framing, with no Blender involved.

    CI has no Blender and must not regenerate art — the .glb and .png in the repository
    are the artifact. This renders exactly those.
    """
    binary, cutscenes = configuration_paths(args.configuration)
    game = os.path.join(binary, "MiVic.Game")

    if not os.path.exists(game):
        print(f"no build at {os.path.relpath(game, ROOT)} — run dotnet build first",
              file=sys.stderr)
        return 2

    os.makedirs(cutscenes, exist_ok=True)
    os.makedirs(os.path.join(ROOT, "artifacts", "probe"), exist_ok=True)

    # The scene, written into the *output* content directory so nothing a check needs
    # ends up in the shipped content.
    scene = {
        "cutscene": "MiVicCutscene",
        "version": 1,
        "body": {
            "id": "zz_figure_check",
            "kind": "Briefing",
            "faction": "Soviet",
            "set": "set_study",
            "music": "bridge",
            "camera": [FACE_CAMERA,
                       dict(FACE_CAMERA, milliseconds=4000, x=250, y=1250, z=500,
                            targetY=1200)],
            "figures": [{"asset": "personality_elder", "x": 0, "z": 1700,
                         "facingDegrees": 0}],
            "lines": [{"speaker": "personality_elder", "greekText": "Κάθισε.",
                       "milliseconds": 2800}],
        },
    }
    scene_path = os.path.join(cutscenes, "zz_figure_check.cutscene.json")
    with open(scene_path, "w", encoding="utf-8") as handle:
        json.dump(scene, handle, indent=2)

    shot = os.path.join(ROOT, "artifacts", "probe", "figure-check.png")
    probe = os.path.join(ROOT, "artifacts", "probe", "figure-check.probe")
    with open(probe, "w", encoding="utf-8") as handle:
        handle.write("cutscene seek 200\n")
        handle.write(f"shot {os.path.relpath(shot, ROOT)}\n")

    env = dict(os.environ, LIBGL_ALWAYS_SOFTWARE="1", DOTNET_ROLL_FORWARD="Major")
    command = ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24", game,
               "--cutscene", "zz_figure_check", "--probe", probe,
               "--probe-out", os.path.join(ROOT, "artifacts", "probe", "figure-check.txt")]

    try:
        subprocess.run(command, cwd=ROOT, env=env, timeout=args.timeout,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
    except FileNotFoundError:
        # No xvfb (a desktop that already has a display): run it directly.
        subprocess.run(command[4:], cwd=ROOT, env=env, timeout=args.timeout,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)

    if not os.path.exists(shot):
        print("the game produced no frame — is the cutscene content built?", file=sys.stderr)
        return 2

    image = Image.open(shot).convert("RGB")
    w, h = image.size
    crop = image.crop((int(w * CROP[0]), int(h * CROP[1]),
                       int(w * CROP[2]), int(h * CROP[3])))
    crop.save(args.crop)
    print(f"rendered {os.path.relpath(args.crop, ROOT)}")
    return 0


# ----------------------------------------------------------------------------- check

def cmd_check(args):
    if not args.no_render:
        status = cmd_render(args)
        if status != 0:
            return status

    if not os.path.exists(args.crop):
        print(f"no crop at {os.path.relpath(args.crop, ROOT)} — render it first",
              file=sys.stderr)
        return 2

    with open(args.baseline, encoding="utf-8") as handle:
        baseline = json.load(handle)

    steps = baseline.get("steps", 12)
    tolerances = baseline.get("tolerances", {})
    mean_limit = args.tolerance_mean if args.tolerance_mean is not None \
        else tolerances.get("mean", TOLERANCE_MEAN)
    worst_limit = args.tolerance_worst if args.tolerance_worst is not None \
        else tolerances.get("worst", TOLERANCE_WORST)
    rg_limit = tolerances.get("red_green", TOLERANCE_RED_GREEN)

    result = measure(Image.open(args.crop).convert("RGB"), steps)
    reference = baseline["profile"]
    built = result["profile"]
    deltas = [abs(a - b) * 100 for a, b in zip(reference, built)]
    mean = sum(deltas) / len(deltas)
    worst = max(deltas)

    print(f"baseline {os.path.basename(args.baseline)}"
          f"  ({baseline.get('source', '?')}, box {baseline.get('box')})")
    print(f"crop     {os.path.relpath(args.crop, ROOT)}")
    print()
    print(f"  {'from crown to chin':<22}{'reference':>10}{'built':>8}{'delta':>8}")
    for i, (a, b, d) in enumerate(zip(reference, built, deltas)):
        flag = "  <- over" if d > worst_limit else ""
        print(f"  {i:>2} of {steps:<18}{a * 100:>9.0f}%{b * 100:>7.0f}%{d:>7.1f}{flag}")
    print(f"  {'mean':<22}{'':>10}{'':>8}{mean:>7.1f}")
    print(f"  {'worst':<22}{'':>10}{'':>8}{worst:>7.1f}"
          f"   (limit {worst_limit:.0f}, mean limit {mean_limit:.0f})")

    failures = []
    if result["clipped_rows"]:
        # A frame that cuts the head off is not a measurement of the head. This is
        # checked before the profile because a clipped profile can still score well,
        # and did: a crop 20 mm too narrow at the ears passed at a mean of 5.4.
        rows = ", ".join(str(i) for i in result["clipped_rows"])
        failures.append(f"the crop cuts the figure off at the edge (rows {rows})")
        print()
        print(f"  CLIPPED at rows {rows} — the frame is too narrow, so every width "
              f"below is a fraction of a width that was cut off")
    if mean > mean_limit:
        failures.append(f"mean {mean:.1f} over {mean_limit:.0f}")
    if worst > worst_limit:
        failures.append(f"worst {worst:.1f} over {worst_limit:.0f}")

    print()
    print("  colour")
    for name, rgb in result["patches"].items():
        want = baseline["patches"].get(name)
        if not want:
            continue
        rg, want_rg = rgb[0] - rgb[1], want[0] - want[1]
        gated = name in tolerances.get("gated_patches", [])
        delta = abs(rg - want_rg)
        note = ""
        if gated:
            note = "  gated" if delta <= rg_limit else "  <- over"
            if delta > rg_limit:
                failures.append(f"{name} r-g {rg} against {want_rg}")
        print(f"  {name:<22}{str(want):>16}{str(rgb):>16}"
              f"   r-g {want_rg:>3} -> {rg:>3}{note}")

    print()
    if failures:
        print("FAIL  " + "; ".join(failures))
        return 1
    print("PASS  the figure measures as it did")
    return 0


# ------------------------------------------------------------------------------- cli

def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Measure the briefing figure against a baseline.")
    sub = parser.add_subparsers(dest="command", required=True)

    capture = sub.add_parser("capture", help="record what the figure should measure")
    capture.add_argument("--image", default=None,
                         help="a reference image to measure against")
    capture.add_argument("--from-crop", default=None,
                         help="freeze the figure's own measurement (a regression gate)")
    capture.add_argument("--box", default=None,
                         help=f"crown-to-chin box, x0,y0,x1,y1 (default {PORTRAIT_BOX})")
    capture.add_argument("--out", default=os.path.join(ROOT, "tools", "figure_baseline.json"))
    capture.add_argument("--steps", type=int, default=12)
    capture.add_argument("--tolerance-mean", type=float, default=None)
    capture.add_argument("--tolerance-worst", type=float, default=None)
    capture.set_defaults(func=cmd_capture)

    for name, handler, help_text in (
        ("render", cmd_render, "photograph the committed assets at the face framing"),
        ("check", cmd_check, "measure and compare against the baseline"),
    ):
        command = sub.add_parser(name, help=help_text)
        command.add_argument("--configuration", default="Release")
        command.add_argument("--crop", default=os.path.join(
            ROOT, "artifacts", "probe", "figure-check-crop.png"))
        command.add_argument("--baseline",
                             default=os.path.join(ROOT, "tools", "figure_baseline.json"))
        command.add_argument("--timeout", type=int, default=300)
        if name == "check":
            command.add_argument("--no-render", action="store_true",
                                 help="measure a crop something else wrote")
            command.add_argument("--tolerance-mean", type=float, default=None)
            command.add_argument("--tolerance-worst", type=float, default=None)
        command.set_defaults(func=handler)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
