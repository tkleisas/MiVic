"""Render a cutscene to MP4, GIF and a contact sheet, with its subtitles burned in.

The game draws a cutscene for a viewer: it advances the director from the wall
clock and puts the subtitle on screen through ImGui. Neither survives a headless
run — the probe drives its own clock, and ImGui inside a probe segfaults — so the
frames come from the probe and the words are composited afterwards. That is the
whole reason this is a script and not a switch on the game: a briefing is a piece
of the game's presentation that has to be looked at, and looking at it should not
need a person sitting through it in real time.

    python3 tools/render_cutscene.py --cutscene m1_briefing --out artifacts/cutscene

Frames are the probe's, not the wall clock's: the director is seeked to each frame
time explicitly, so a re-render of an unchanged scene is frame-for-frame identical
and a diff means the scene changed.
"""

import argparse
import json
import math
import os
import shutil
import subprocess
import sys

CHARACTERS_PER_SECOND = 42.0
GAME = "src/MiVic.Game/bin/Debug/net9.0/MiVic.Game"
CONTENT = "src/MiVic.Game/bin/Debug/net9.0/Content"
FONT = "src/MiVic.Game/Content/Fonts/NotoSans-Regular.ttf"


def load_cutscene(cutscene_id):
    """The scene file the game will load, so the words match the ones spoken."""
    path = os.path.join(CONTENT, "Cutscenes", f"{cutscene_id}.cutscene.json")

    if not os.path.exists(path):
        raise SystemExit(f"no cutscene at '{path}' — build the game first")

    with open(path, encoding="utf-8") as handle:
        return json.load(handle)["body"]


def line_timings(lines):
    """Each line as (start_ms, end_ms, speaker, text).

    The script is a sequence, not a schedule: a line begins where the one before it
    ended, which is what the director does too. Writing the start times into the
    file as well would let the two disagree.
    """
    start = 0
    timed = []

    for line in lines:
        end = start + int(line["milliseconds"])
        timed.append((start, end, line.get("speaker", ""), line["greekText"]))
        start = end

    return timed


def visible_characters(text, elapsed_ms):
    """How much of a line has been typed by `elapsed_ms`.

    The director types; the compositor has to type at the same speed, or the words
    in the video drift ahead of the ones in the game.
    """
    typed = int((elapsed_ms / 1000.0) * CHARACTERS_PER_SECOND)
    return text[:max(0, min(len(text), typed))]


def build_probe(timed, frames_dir, step_ms, duration_ms):
    """A probe script that seek-and-shoots every frame in one process."""
    lines = []

    for index in range(0, duration_ms + step_ms, step_ms):
        moment = min(index, duration_ms)
        path = os.path.join(frames_dir, f"raw{index // step_ms:04d}.png")
        lines.append(f"cutscene seek {moment}")
        lines.append(f"shot {path}")

    return "\n".join(lines) + "\n"


def run_probe(probe_path, report_path, cutscene_id):
    command = [
        GAME,
        "--cutscene", cutscene_id,
        "--probe", probe_path,
        "--probe-out", report_path,
    ]
    environment = dict(os.environ, LIBGL_ALWAYS_SOFTWARE="1", DOTNET_ROLL_FORWARD="Major")
    prefix = ["xvfb-run", "-a", "-s", "-screen 0 1280x720x24"]

    if shutil.which("xvfb-run") is None:
        prefix = []

    result = subprocess.run(prefix + command, env=environment, capture_output=True, text=True)

    if result.returncode != 0:
        print(result.stdout[-2000:])
        print(result.stderr[-2000:])
        raise SystemExit(f"the game failed to render the cutscene (exit {result.returncode})")


def wrap(draw, text, font, width):
    """The line broken into the fewest rows that fit the frame."""
    words = text.split()
    rows = []
    current = ""

    for word in words:
        candidate = f"{current} {word}".strip()

        if draw.textlength(candidate, font=font) <= width or not current:
            current = candidate
        else:
            rows.append(current)
            current = word

    if current:
        rows.append(current)

    return rows


def composite(raw_path, out_path, timed, moment_ms, width, height):
    from PIL import Image, ImageDraw, ImageFont

    image = Image.open(raw_path).convert("RGB")

    if image.size != (width, height):
        image = image.resize((width, height), Image.LANCZOS)

    draw = ImageDraw.Draw(image, "RGBA")
    font = ImageFont.truetype(FONT, max(16, height // 26))

    # The letterbox is the game's: a briefing is a film and the frame says so.
    bar = int(height * 0.06)
    draw.rectangle([0, 0, width, bar], fill=(0, 0, 0))
    draw.rectangle([0, height - bar, width, height], fill=(0, 0, 0))

    speaker = None

    for start, end, name, text in timed:
        if start <= moment_ms < end:
            speaker = (name, visible_characters(text, moment_ms - start))
            break

    if speaker is not None and speaker[1]:
        rows = wrap(draw, speaker[1], font, int(width * 0.82))
        line_height = font.size + 6
        top = height - bar - 18 - (line_height * len(rows))

        for index, row in enumerate(rows):
            x = int(width * 0.09)
            y = top + (index * line_height)

            # A shadow, because a subtitle over a lit desk is unreadable without one.
            draw.text((x + 2, y + 2), row, font=font, fill=(0, 0, 0, 180))
            draw.text((x, y), row, font=font, fill=(238, 238, 232, 255))

    image.save(out_path)


def encode(out_dir, fps, name, width, height, crf):
    """The composited frames, in order, as MP4 and as GIF.

    `frame%04d.png` reads them from zero without a list, which is the only reason
    the frames are numbered rather than named after their moment.
    """
    pattern = os.path.join(out_dir, "frame%04d.png")

    subprocess.run([
        "ffmpeg", "-y", "-loglevel", "error",
        "-framerate", f"{fps}", "-i", pattern,
        "-vf", f"scale={width}:{height}:flags=lanczos",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", str(crf),
        os.path.join(out_dir, f"{name}.mp4"),
    ], check=True)

    subprocess.run([
        "ffmpeg", "-y", "-loglevel", "error",
        "-framerate", f"{fps}", "-i", pattern,
        "-vf", f"scale={width // 2}:{height // 2}:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse",
        os.path.join(out_dir, f"{name}.gif"),
    ], check=True)


def contact_sheet(count, out_dir, columns, name):
    from PIL import Image

    picks = [max(0, round(index * (count - 1) / (columns * 3 - 1)))
             for index in range(columns * 3)]
    thumbs = [Image.open(os.path.join(out_dir, f"frame{index:04d}.png")) for index in picks]
    cell = (thumbs[0].width // 2, thumbs[0].height // 2)
    rows = math.ceil(len(thumbs) / columns)
    sheet = Image.new("RGB", (cell[0] * columns, cell[1] * rows), (10, 10, 11))

    for index, thumb in enumerate(thumbs):
        sheet.paste(thumb.resize(cell, Image.LANCZOS), ((index % columns) * cell[0], (index // columns) * cell[1]))

    sheet.save(os.path.join(out_dir, f"{name}.png"))


def main():
    parser = argparse.ArgumentParser(description="Render a cutscene to video.")
    parser.add_argument("--cutscene", required=True, help="Cutscene id, e.g. m1_briefing.")
    parser.add_argument("--out", required=True, help="Directory for the video and frames.")
    parser.add_argument("--fps", type=float, default=6.67,
                        help="Frames per second of the output; the probe seeks, so this is a choice, not a limit.")
    parser.add_argument("--width", type=int, default=960)
    parser.add_argument("--height", type=int, default=540)
    parser.add_argument("--columns", type=int, default=4)
    parser.add_argument("--crf", type=int, default=20)
    parser.add_argument("--keep-raw", action="store_true", help="Keep the un-subtitled probe frames.")
    args = parser.parse_args()

    body = load_cutscene(args.cutscene)
    timed = line_timings(body["lines"])
    duration = timed[-1][1] if timed else 0
    step = int(round(1000.0 / args.fps))
    count = (duration // step) + 1

    out_dir = os.path.abspath(args.out)
    raw_dir = os.path.join(out_dir, "raw")
    os.makedirs(raw_dir, exist_ok=True)

    probe_path = os.path.join(out_dir, "frames.probe")
    report_path = os.path.join(out_dir, "frames.txt")
    with open(probe_path, "w", encoding="utf-8") as handle:
        handle.write(build_probe(timed, raw_dir, step, duration))

    print(f"{args.cutscene}: {len(timed)} lines, {duration / 1000:.1f}s, {count} frames at {args.fps} fps")
    run_probe(probe_path, report_path, args.cutscene)

    for index in range(count):
        moment = min(index * step, duration)
        composite(
            os.path.join(raw_dir, f"raw{index:04d}.png"),
            os.path.join(out_dir, f"frame{index:04d}.png"),
            timed, moment, args.width, args.height,
        )

    encode(out_dir, args.fps, args.cutscene, args.width, args.height, args.crf)
    contact_sheet(count, out_dir, args.columns, "contact-sheet")
    print(f"wrote {args.cutscene}.mp4, {args.cutscene}.gif and contact-sheet.png in {out_dir}")

    if not args.keep_raw:
        shutil.rmtree(raw_dir)

    return 0


if __name__ == "__main__":
    sys.exit(main())
