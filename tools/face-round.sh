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
python3 tools/paint_personalities.py --out "$CONTENT" 2>&1 | grep -E 'wrote |Error' || true

mkdir -p "$CUTSCENES"
cat > "$CUTSCENES/zz_face.cutscene.json" <<'JSON'
{
  "cutscene": "MiVicCutscene", "version": 1,
  "body": {
    "id": "zz_face", "kind": "Briefing", "faction": "Soviet", "set": "set_study", "music": "bridge",
    "camera": [
      { "milliseconds": 0,    "x": 150, "y": 1600, "z": 3000, "targetX": 0, "targetY": 1560, "targetZ": 4000 },
      { "milliseconds": 4000, "x": 700, "y": 1150, "z": 2900, "targetX": 0, "targetY": 1050, "targetZ": 4000 }
    ],
    "figures": [ { "asset": "personality_elder", "x": 0, "z": 4000, "facingDegrees": 0 } ],
    "lines": [ { "speaker": "personality_elder", "greekText": "Κάθισε.", "milliseconds": 2800 } ]
  }
}
JSON

printf 'cutscene seek 200\nshot artifacts/probe/face-front.png\ncutscene seek 3900\nshot artifacts/probe/body.png\n' \
    > artifacts/probe/face.probe

dotnet build src/MiVic.Game/MiVic.Game.csproj -c Debug 2>&1 | grep -E 'error|Elapsed' | head -3

LIBGL_ALWAYS_SOFTWARE=1 DOTNET_ROLL_FORWARD=Major timeout 300 \
    xvfb-run -a -s "-screen 0 1280x720x24" "$GAME" \
    --cutscene zz_face --probe artifacts/probe/face.probe --probe-out artifacts/probe/face.txt \
    > /dev/null 2>&1 || true

python3 - "$@" <<'PY'
import sys
from PIL import Image

wide = "--wide" in sys.argv
front = Image.open("artifacts/probe/face-front.png")
w, h = front.size
box = (int(w * 0.34), int(h * 0.04), int(w * 0.66), int(h * 0.78))
front.crop(box).resize((int((box[2] - box[0]) * 1.6), int((box[3] - box[1]) * 1.6)), Image.LANCZOS) \
    .save("artifacts/probe/face-front-crop.png")

body = Image.open("artifacts/probe/body.png")
w, h = body.size
body.crop((int(w * 0.22), int(h * 0.02), int(w * 0.78), int(h * 0.98))) \
    .save("artifacts/probe/body-crop.png")

if wide:
    ref = Image.open("/home/tkleisas/.dsh/attachments/v1/objects/4c/"
                     "4caafdc36c78cc5bfcb502efad7b258dad0c20b4acc36e09baaf32355880b2c0").convert("RGB")
    head = ref.crop((880, 150, 1160, 400)).resize((420, 375), Image.LANCZOS)
    mine = front.convert("RGB").crop((int(front.width * 0.37), int(front.height * 0.05), int(front.width * 0.63), int(front.height * 0.72))).resize((320, 470), Image.LANCZOS)
    sheet = Image.new("RGB", (790, 515), (24, 24, 28))
    sheet.paste(head, (10, 45))
    sheet.paste(mine, (450, 45))
    sheet.save("artifacts/preview/compare.png")

print("face-front-crop.png and body-crop.png written")
PY
