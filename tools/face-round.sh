#!/usr/bin/env bash
# One round of the figure: regenerate, paint, build, render the face, crop it.
#
# The game is the only judge that matters — the preview tool draws vertex colours
# and cannot sample the face texture — so every round of this ends in a rendered
# frame from the real renderer, at the real lighting, from the real camera.
#
#   tools/face-round.sh [--wide]
#
# Writes artifacts/probe/face-front-crop.png, which is the image to look at.
set -euo pipefail

cd "$(dirname "$0")/.."

BLENDER=${BLENDER:-/home/tkleisas/blender/blender-5.2.2-linux-x64/blender}
GAME=src/MiVic.Game/bin/Debug/net9.0/MiVic.Game
CONTENT=src/MiVic.Game/Content/Models/Generated
CUTSCENES=src/MiVic.Game/bin/Debug/net9.0/Content/Cutscenes

"$BLENDER" --background --python tools/blender/build_personalities.py -- --out "$CONTENT" 2>&1 |
    grep -E 'wrote |Error|Traceback|line [0-9]' || true

if ! python3 tools/paint_personalities.py --out "$CONTENT"; then
    echo "the painter failed — the texture was not regenerated" >&2
    exit 1
fi

mkdir -p "$CUTSCENES"
cat > "$CUTSCENES/zz_face.cutscene.json" <<'JSON'
{
  "cutscene": "MiVicCutscene", "version": 1,
  "body": {
    "id": "zz_face", "kind": "Briefing", "faction": "Soviet", "set": "set_study", "music": "bridge",
    "camera": [
      { "milliseconds": 0,     "x": 60,  "y": 1600, "z": 1100, "targetX": 0, "targetY": 1590, "targetZ": 1700 },
      { "milliseconds": 4000,  "x": 250, "y": 1250, "z": 500,  "targetX": 0, "targetY": 1200, "targetZ": 1700 },
      { "milliseconds": 8000,  "x": 260, "y": 1540, "z": -600, "targetX": 0, "targetY": 1440, "targetZ": 1600 }
    ],
    "figures": [ { "asset": "personality_elder", "x": 0, "z": 1700, "facingDegrees": 0 } ],
    "lines": [ { "speaker": "personality_elder", "greekText": "Κάθισε.", "milliseconds": 2800 } ]
  }
}
JSON

printf 'cutscene seek 200\nshot artifacts/probe/face-front.png\ncutscene seek 3900\nshot artifacts/probe/torso.png\ncutscene seek 7900\nshot artifacts/probe/wide.png\n' \
    > artifacts/probe/face.probe

dotnet build src/MiVic.Game/MiVic.Game.csproj -c Debug 2>&1 | grep -E 'error|Elapsed' | head -3

LIBGL_ALWAYS_SOFTWARE=1 DOTNET_ROLL_FORWARD=Major timeout 300 \
    xvfb-run -a -s "-screen 0 1280x720x24" "$GAME" \
    --cutscene zz_face --probe artifacts/probe/face.probe --probe-out artifacts/probe/face.txt \
    > /dev/null 2>&1 || true

python3 - "$@" <<'PY'
import sys
from PIL import Image, ImageDraw, ImageFont

wide = "--wide" in sys.argv
front = Image.open("artifacts/probe/face-front.png").convert("RGB")
w, h = front.size

# Three distances in one image, every round. The uniform's metal rendered brown for
# weeks because nothing looked at the torso, and the head is only ever seen at the
# briefing's own framing — a strip that shows all three is the check that would have
# caught it, and it costs one extra shot.
face = front.crop((int(w * 0.30), int(h * 0.02), int(w * 0.70), int(h * 0.98)))

torso = Image.open("artifacts/probe/torso.png").convert("RGB")
tw, th = torso.size
torso = torso.crop((int(tw * 0.30), int(th * 0.02), int(tw * 0.70), int(th * 0.98)))

wideshot = Image.open("artifacts/probe/wide.png").convert("RGB")
ww, wh = wideshot.size
wideshot = wideshot.crop((int(ww * 0.20), 0, int(ww * 0.80), wh))

height = 660
panels = []
for image in (face, torso, wideshot):
    scale = height / image.height
    panels.append(image.resize((max(1, int(image.width * scale)), height), Image.LANCZOS))

labels = ("face 0.6 m", "torso 1.2 m", "briefing 2.5 m")
width = sum(p.width for p in panels) + (20 * (len(panels) - 1))
strip = Image.new("RGB", (width, height + 30), (22, 22, 26))
x = 0
draw = ImageDraw.Draw(strip)
font = ImageFont.truetype("src/MiVic.Game/Content/Fonts/NotoSans-Regular.ttf", 20)

for panel, label in zip(panels, labels):
    strip.paste(panel, (x, 30))
    draw.text((x + 6, 5), label, font=font, fill=(220, 220, 210))
    x += panel.width + 20

strip.save("artifacts/probe/round.png")

if wide:
    ref = Image.open("/home/tkleisas/.dsh/attachments/v1/objects/4c/"
                     "4caafdc36c78cc5bfcb502efad7b258dad0c20b4acc36e09baaf32355880b2c0").convert("RGB")
    head = ref.crop((880, 150, 1160, 400)).resize((400, 357), Image.LANCZOS)
    mine = front.crop((int(w * 0.30), int(h * 0.02), int(w * 0.70), int(h * 0.98)))
    mine = mine.resize((int(mine.width * 357 / mine.height), 357), Image.LANCZOS)
    sheet = Image.new("RGB", (head.width + mine.width + 30, 400), (24, 24, 28))
    sheet.paste(head, (10, 40))
    sheet.paste(mine, (head.width + 20, 40))
    sheet_draw = ImageDraw.Draw(sheet)
    sheet_draw.text((10, 10), "reference", font=font, fill=(225, 225, 215))
    sheet_draw.text((head.width + 20, 10), "built", font=font, fill=(225, 225, 215))
    sheet.save("artifacts/preview/compare.png")

print("face-front-crop.png and body-crop.png written")
PY
