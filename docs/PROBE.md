# Probe — scripted inspection of a running client

`--probe` turns the client into a tool you can ask questions. It reads a **script file**,
executes the commands in it against a running match, writes a **text transcript**, takes as
many screenshots as the script asks for, and exits with a status that says whether every
command succeeded.

```pwsh
$exe = "src/MiVic.Game/bin/Debug/net9.0/MiVic.Game.exe"

$p = Start-Process -FilePath $exe -Wait -PassThru -NoNewWindow -ArgumentList `
    '--probe', 'tools/probe/turret.probe', `
    '--probe-out', 'artifacts/probe/turret.txt'

$p.ExitCode      # 0 = every command ran and every check held
```

| Option | Effect |
|---|---|
| `--probe <script>` | run this script and exit |
| `--probe-out <file>` | where the transcript goes (default `probe-report.txt`, next to the executable) |

`--probe` composes with the fixtures, because a probe is a way of *looking*, not a scenario:
`--turret-demo --probe tools/probe/turret.probe` puts two tanks 70 m apart and then answers
questions about them.

**PowerShell does not wait for this executable.** The client is a `WinExe`, so `& $exe …`
returns immediately and `$LASTEXITCODE` is empty. Use `Start-Process -Wait -PassThru` (as
above), `cmd /c`, or read the transcript file after waiting. This is the only piece of
process plumbing the tool needs, and getting it wrong looks exactly like a probe that
answered nothing.

## Why a batch script and not an interactive session

**The agent driving this tool runs every shell command in a fresh process.** A prompt
waiting on stdin would be dead before the second command could be typed, so an interactive
REPL is not a slower version of this tool — it is not a tool at all here. Every question
would cost a process launch, a world build and a screenshot, which is exactly the loop the
probe exists to break.

A script file is therefore the only shape the channel can take:

- **One process, fifty answers.** Loading models, generating the world and starting the
  renderer costs about a second; the commands after that cost milliseconds. A transcript
  with sixteen commands and two screenshots takes one launch.
- **The script is the record.** A question that mattered is a file in the repository, run
  again by anyone, with its expected numbers written into it as checks.
- **Nothing depends on timing.** The script advances the simulation itself, so a run is
  reproducible rather than a race against the frame rate.

The one thing this shape costs is that a command cannot react to the answer before it —
there is no "if". A script that needs that is two scripts, or a script with the numbers
written in as `expect` checks, which is usually the honest version of the same thing.

## Running a script: the language

One command per line. Blank lines are ignored, `#` starts a comment (at the start of a
line or after a space), arguments are separated by spaces and double-quoted when they
contain one. Every argument that is not a name is a number in invariant culture.

Relative paths in a script — a `shot` file, `--probe-out` — resolve against **the directory
the process was started in**, not the executable, because a script is a test artifact and
lives with the repository.

### Control

| Command | What it does |
|---|---|
| `tick <n>` | advance the simulation n ticks, one tick at a time through the client's own tick path |
| `settle [frames]` | run the client's per-frame presentation work: effects, smoke, rounds in flight, the clock the liquid shaders read. Default 15 frames of 16.7 ms. **Never advances the simulation.** |
| `shot <file.png>` | render the current state to a PNG |
| `focus <x> <z> [y]` | aim the camera, in metres; a height is honoured for things that are not on the ground |
| `zoom <metres>` | set the camera distance (clamped by the camera, and it says so when it clamps) |
| `pitch <rad>` | set the downward tilt; negative looks down |
| `yaw <rad>` | set the rotation about the vertical |

`tick` and `settle` are separate on purpose. The simulation changes only from a tick and
effects change only from a frame, so a script can ask what the world looked like between
two of them:

```
tick 60            # three seconds of battle
events 10          # what the simulation reported, with the tick each event happened on
settle             # turn those events into tracers, smoke and impacts
shot out/frame.png # photograph it
tick 40            # the rounds have flown; some of them have landed
events 10
settle
shot out/frame-later.png
```

### World queries

| Command | Answer |
|---|---|
| `surfaces` | a census of every `TerrainType` on the map, **including the types no cell received** |
| `attributes <x> <z>` | what the ground is at a cell: surface, height against the water line, vegetation, moisture, aspect, landform, flags, cover and movement cost for each movement class |
| `units [faction\|team] [limit]` | one line per live entity: slot, faction, kind, team, position, heading, health, target, whether it is a building |
| `unit <slot>` | the same in detail: move goal and distance to go, path state, what it is attacking and from how far, cooldown, morale, distance travelled, construction, and whether the client is drawing it |
| `count <kind>` | how many of a role are alive, per faction |
| `bridge <x> <z> [team] [build]` | whether a crossing at that cell would be accepted, the reason when it would not, the span it would cover, what it costs and how long the work takes — and with `build`, the order that starts it |
| `bridges` | every crossing on the map: the cells it spans, how much of the deck is up, which bank the work started from, and the tick it will be whole on |

### What the player would see

A question about a click cannot be answered from the world: the world only knows that nothing
happened, and *why* is spread across the client's projection, the interface and the rules.
These four commands walk that path in the order a player does — arm, aim, click, look — and
each of them prints the answer the client itself worked out.

| Command | Answer |
|---|---|
| `arm bridge` | what pressing Γέφυρα does, through the HUD's own command path, and whether the client is now waiting for a site |
| `hover <x> <z> [y]` | puts the script's cursor on a world point: the pixel it projects to, what the client resolves that pixel back to, how far that is from where it was aimed, and — when a placement is armed — whether the ghost is green or red and how many cells it takes |
| `click [x z]` | releases the left button at the script's cursor, through the client's own click path: what it resolved to, whether an order was issued, and the words the player is shown when it was refused |
| `hud [on\|off]` | whether probe frames draw the HUD. Off by default; on when the answer *is* the panel, and a shot then carries it |

`hover` aims at the surface the renderer draws at that point — the water line over water, the
height field elsewhere — because that is what a cursor is over. Give `y` to aim at something
that is not on the ground, as `focus` allows.

`build` on `bridge` is the one command in the tool that changes the world the script is
looking at, and it exists because a crossing cannot be inspected until it has been built: the
cells it turns into ford are the answer, and there is no other way to ask for them. Everything
else here reads. A bridge takes time to build, so a script that wants to see it finished
either advances the ticks itself or asks `bridges` how much work is left.

### Render-state queries

| Command | Answer |
|---|---|
| `parts <slot> [name]` | every part of that entity: its declared transform, what the animator did to it, the axis and angle of that rotation, the per-tick rotation where the script has sampled it twice, and its final world transform and bearing |
| `model <faction>/<kind>` | the model entry a role resolves to: file, import options, fitted scale, wheel radius, every part it declares, and **which other roles import the same file** |
| `visible` | what a frame submits, where the camera is and what ground it covers, how many entities project inside the viewport, and a surface census of the ground in frame |

### Events

| Command | Answer |
|---|---|
| `events [n]` | the most recent simulation events the client has seen: shots with the direction fired and the range, hits with the damage, deaths with the position |

Firing is not re-detected here: the events come from `SimBridge`'s own cooldown diffing, so
a shot in the transcript and a tracer on screen are the same shot.

### Checks

| Command | Effect |
|---|---|
| `expect <label> <actual> <expected> [tolerance]` | record a check. Two numbers compare numerically, anything else as text; a tolerance covers answers that are themselves rounded. |

`expect` takes literals, not query results: it is a ledger of values you measured once and
want to keep true. `expect "the map has lava" 55 55` fails the run the day lava stops being
generated — which has happened once already.

## Output format

One record per line, prefixed so a transcript can be grepped and so a reader can tell an
answer from a failure:

| Prefix | Meaning |
|---|---|
| `cmd:` | the command as written, echoed before its results |
| `ok:` | a control command that ran, with what it did |
| `query:` | one line of an answer |
| `check:` | a check that held |
| `fail:` | a check that did not |
| `error:` | a command that could not run — the script carries on with the next line |
| `probe:` | the last line: `probe: N commands, N ok, N errors, N checks failed` |

Numbers are rounded and carry their units — `34.2 m`, `5 cells`, `3.7 s`, `870 ‰` — because
the reader is a language model reading a text file, and `34.249996185302734` is a number
nobody can compare against anything.

A command that cannot run never aborts the script:

```
cmd: parts 1
error: line 4: slot 1 holds nothing alive — parts <slot> [name]; `units` lists what does
cmd: events 6
query: events: showing the last 6 of 256 seen since the script started, tick 40
```

## Worked example: is the turret pointing at what it fired at?

The question is unanswerable from a still frame: the barrel and the tracer are both in the
picture and neither says which way the other went. `tools/probe/turret.probe`, run against
`--turret-demo`, trimmed to the two answers:

```
cmd: tick 40
ok: ran 40 ticks (2.0 s), simulation now at tick 40
cmd: parts 509 turret
query: parts slot 509 soviet/Tank team 0, tick 40
query:   model    Generated/soviet_tank.glb, scale 0.837 x 0.837 x 0.837, 54 parts, wheel radius none — any wheel_ part on this model is drawn still, filtered to 'turret'
query:   entity   at (-7.0, 13.9, -212.0) m, heading 0.0°, which the entity transform turns into (1.00, 0.00, 0.00) bearing 0.0°
query:   turret   the turret part points +33.7° off the hull (world bearing 33.7°, hull 0.0°); the animator's own TurretYaw is -0.588 rad (-33.7°) in the model's frame
query:   [31] 'turret' parent -  local T(0.00, 1.48, 0.00) m X(1.00, 0.00, 0.00) Y(0.00, 1.00, 0.00) Z(0.00, 0.00, 1.00)  motion rotates about (0.00, 1.00, 0.00) by +33.7°  bounds max (1.2, 1.1, 2.0) m  world T(-7.46, 15.16, -212.00) m X(-0.46, 0.00, 0.70) Y(0.00, 0.84, 0.00) Z(-0.70, 0.00, -0.46)  nose (0.83, 0.00, 0.55) bearing 33.7°
cmd: events 4
query: events: showing the last 4 of 256 seen since the script started, tick 40
query:   #256 tick 33 shot      slot  509 soviet/Tank at (-7.0, 13.9, -212.0) m firing at western/Tank (slot 508), direction (0.83, 0.00, 0.55) bearing 33.7°, 72.1 m away
```

Two bearings, from two different systems, agreeing to a tenth of a degree: the turret's own
part points at 33.7° and the shot went at 33.7°. Before this query the only way to ask was
to photograph the muzzle and the tracer and hope.

The three turret numbers are one rotation written in three frames, which is why the signs
differ: the world bearing (`33.7°`), the angle off the hull (`+33.7°`, hull at `0.0°`), and
`TurretYaw` (`-33.7°`), which is the renderer's own output *inside the model's frame* — the
model's own alignment is a rotation the turret inherits, so the same turn has the opposite
sign there.

## Worked example: what axis does the radar dish turn about, and where is it?

A dish sweeping about the wrong axis climbs over its own tower, goes through the roof and
comes back out. At any single moment that is a picture of a dish. `tools/probe/radar.probe`,
trimmed to the part's own lines (`…` marks the numbers cut out of the middle):

```
cmd: units soviet 1
query:   slot    0 soviet CommandCentre    team 0 at (x -180.0, z -180.0) m heading 0.0° health 5000/5000 target - building yes
cmd: parts 0 radar
query: parts slot 0 soviet/CommandCentre team 0, tick 0
query:   [21] 'radar' parent -  local T(-1.00, 31.30, 3.00) m X(1.00, 0.00, 0.00) …  motion none (drawn exactly as the model declares it)  world T(-181.58, 16.47, -180.53) m …  nose (1.00, 0.00, 0.00) bearing 0.0°
cmd: tick 40
ok: ran 40 ticks (2.0 s), simulation now at tick 40
cmd: parts 0 radar
query: parts slot 0 soviet/CommandCentre team 0, tick 40
query:   [21] 'radar' parent -  local T(-1.00, 31.30, 3.00) m X(1.00, 0.00, 0.00) …  motion rotates about (0.00, 1.00, 0.00) by -45.8°; turned -45.8° about (0.00, 1.00, 0.00) over 40 ticks = -1.1°/tick  bounds max (2.9, 3.5, 1.8) m  world T(-181.58, 16.47, -180.53) m …  nose (0.70, 0.00, -0.72) bearing -45.8°
```

Three facts, in three lines:

- the axis is **`(0.00, 1.00, 0.00)`** — vertical, not `Z`;
- the world position is **`(-181.58, 16.47, -180.53) m`** and it is *the same at tick 0 and
  tick 40*: the dish sweeps where it stands rather than climbing;
- the rate is **`-1.1°/tick`**, which is the `0.02 rad` per tick the animator asks for.

The sign is the one convention worth knowing: a rotation is reported as a signed angle about
the axis given, right-handed, so a positive angle about `+Y` turns `+X` towards `-Z`. The
animator's `CreateRotationY(+0.02)` therefore reads back as `-1.1°/tick` about `+Y`.

## Worked example: how much of each surface, and is any lava in frame?

`tools/probe/surfaces.probe`, trimmed:

```
cmd: surfaces
query: surfaces: 65 x 65 cells of 9.4 m = 4225 cells over 600.0 m
query:   Grass          1032 cells   24.4%  Γρασίδι
query:   Mud             170 cells    4.0%  Λάσπη
query:   Sand            607 cells   14.3%  Άμμος
query:   Snow             83 cells    1.9%  Χιόνι
query:   Rock            155 cells    3.6%  Βράχος
query:   ShallowWater    134 cells    3.1%  Νερό
query:   DeepWater       866 cells   20.4%  Βαθύ νερό
query:   Lava             55 cells    1.3%  Λάβα
query:   Mine            147 cells    3.4%  Κοίτασμα
query:   Forest          976 cells   23.1%  Δάσος
cmd: visible
query: visible: 354 draw calls, 9387 instances, 0 particles, 0 rounds in flight, 84 health bars
query:   note     instances are submitted to the GPU, not clipped by the client; the ground and entity counts below are what is in the frustum
query:   camera   1280x720 px at (74.0, 146.4, 74.0) m looking at (0.0, 0.0, 0.0) m, 180.0 m out, pitch -54.4°, yaw 45.0°
query:   covers   x -225.3 .. 122.3 m, z -225.3 .. 122.3 m, about 120807 m² in a box around it
query:   entities 1 of 510 alive project inside the viewport (this counts through fog)
query:   ground   grass 221, mud 18, sand 100, snow 13, rock 26, shallowwater 18, deepwater 32, lava 4, mine 2, forest 143
query:   absent   none
```

Two surfaces have gone missing here in the past — sand, and then lava — each with a
movement cost, a cover value, a colour and a shader treatment already built for it. A
screenshot cannot show a surface that no cell received; a census prints the zero. The
`absent` line is the same question asked of one frame: the surfaces that are *not* under the
camera, so "the lava is invisible" becomes `lava 0` or `lava 4` rather than an argument.

The scene here is why a screenshot of it is nearly empty, and the query says so: **one**
entity of 510 projects inside the viewport, because the camera is looking at the middle of
the map and the fighting is at the bases in its corners. The instance count above it (9387)
is what the client *submitted* — this renderer culls nothing on the processor and lets the
GPU discard what is off screen — so that number is a cost and the frustum count is the
answer to "what is on screen".

## Worked example: why is this tank not moving?

A unit standing still with a move order is three possible bugs — no route, no budget for a
route, or a goal that cannot be reached — and three commands against the default skirmish
(`tick 300`, `unit 28`, `attributes -155.7 -201.9`) tell them apart:

```
cmd: unit 28
query:   move       goal (x -155.7, z -201.9) m, 72.8 m to go, path 0 cells at 0, waiting for a route, 0 failures
query:   travel     13.1 m covered at 8.0 m per second (0.4 m per tick)
cmd: attributes -155.7 -201.9
query: attributes at (x -155.7, z -201.9) m — cell 15,10 of 65, index 665
query:   surface    DeepWater (Βαθύ νερό), churn 0/255
query:   going      foot impassable, tracked impassable, wheeled impassable, air 100 ‰
```

The goal is deep water: no route exists, `PathFailures` never counts it, and the unit waits
forever. (Both halves of that are worth a look: the wander orders pick a destination
without asking whether anything can stand on it, and a route that never arrives leaves a
unit stuck rather than dropped. That is what this query is for — a screenshot of a tank
standing on mud says nothing at all.)

## Worked example: why did clicking on the water do nothing?

The report was "I click on Γέφυρα and then on the water, and nothing happens", and nothing
happening has three possible authors: the button never armed, the click never resolved to a
cell, or the simulation refused the site. All three look exactly like a lake in a screenshot.
`tools/probe/bridge.probe` walks the player's own path against the default skirmish — the
lake south-west of the player's base, whose shoreline is at x = -262 m — and this is what it
answered while the bug was live:

```
cmd: bridge -257 -210
query: bridge at (x -257.0, z -210.0) m — cell 4,9 of 65, index 589, DeepWater (Βαθύ νερό)
query:   verdict    accepted for team 0 — 7 cells would become ford
cmd: arm bridge
query: arm bridge — the client did not arm a bridge: clicking the button had no effect
query:   result     nothing is waiting for a click
cmd: hover -257 -210
query: hover at (x -257.0, z -210.0) m on the drawn surface (3.5 m) — pixel (640.0, 348.7) of 1280x720
query:   ground     resolved to (-258.0, 3.5, -211.0) m — cell 4,9 of 65, index 589, DeepWater (Βαθύ νερό), 1.4 m from where it was aimed
query:   placement  nothing armed, so no ghost is drawn
cmd: click
query: click — armed no, resolved (-258.0, 3.5, -211.0) m
query:   result     nothing was issued
query:   notice     the player is told nothing
cmd: attributes -257 -210
query:   surface    DeepWater (Βαθύ νερό), churn 0/255
query:   going      foot impassable, tracked impassable, wheeled impassable, air 100 ‰
```

Three answers, and the first is the one that mattered: **the simulation would have taken the
site** — every water cell within ninety metres of that base is one it accepts. The refusal was
the interface's, and the `click` line says it in full: nothing was issued and the player was
told nothing. Four lines of transcript and the question is settled, which is the entire point
of the tool; the same four questions cost four launches, four screenshots and an argument.

The `hover` line is the other half. The click resolved 1.4 m from where it was aimed, because
the client marched its ray against the lake bed instead of the water surface the player can
see — one to three metres of error on every click on water, always towards the bank. Aimed at
the surface, the same pixel comes back to the metre:

```
cmd: hover -257 -210
query:   ground     resolved to (-257.0, 3.5, -210.0) m — cell 4,9 of 65, index 589, DeepWater (Βαθύ νερό), 0.0 m from where it was aimed
query:   placement  accepted — the ghost takes 7 cells and is green
cmd: click
query: click — armed yes, resolved (-257.0, 3.5, -210.0) m
query:   result     an order was issued
cmd: bridges
query: crossings: 0 crossings on the map, tick 0
cmd: tick 2
cmd: bridges
query: crossings: 1 crossing on the map, tick 2
query:   #0 7 cells along z, (4,4) to (4,10), (x -257.8, z -257.8) m to (x -257.8, z -201.6) m — 0/7 up from (4,10), 0 ‰, 239 ticks (12.0 s) of work left, started on tick 1, whole on tick 241
cmd: tick 90
cmd: bridges
query:   #0 7 cells along z, (4,4) to (4,10) … — 2/7 up from (4,10), 285 ‰, 149 ticks (7.5 s) of work left, started on tick 1, whole on tick 241
cmd: shot artifacts/probe/bridge-building.png
cmd: tick 160
cmd: bridges
query:   #0 7 cells along z, (4,4) to (4,10), (x -257.8, z -257.8) m to (x -257.8, z -201.6) m — whole, started on tick 1, whole on tick 241
cmd: attributes -257 -210
query:   surface    ShallowWater (Νερό), churn 0/255
query:   going      foot 250 ‰, tracked 300 ‰, wheeled 450 ‰, air 100 ‰
```

That is a bridge built by a script out of the same three calls a mouse makes, and it is a
bridge a player can watch: the order is recorded on tick 1 and paid for, the deck goes up from
the bank the player clicked nearest — one cell every 1.5 s, twelve seconds for this one — and
the ford appears under each cell as the work reaches it, which is why `attributes` on the
clicked cell reports water at tick 92 only if the deck got there first. `bridges` is the
command that answers "is anything happening", and it exists because for a while nothing was:
a crossing used to be a surface change applied in one tick, with no deck on screen and no
progress anywhere.

## `parts <slot>` in full

### A tank's turret

```
query: parts slot 509 soviet/Tank team 0, tick 40
query:   model    Generated/soviet_tank.glb, scale 0.837 x 0.837 x 0.837, 54 parts, wheel radius none — any wheel_ part on this model is drawn still, filtered to 'turret'
query:   entity   at (-7.0, 13.9, -212.0) m, heading 0.0°, which the entity transform turns into (1.00, 0.00, 0.00) bearing 0.0°
query:   turret   the turret part points +33.7° off the hull (world bearing 33.7°, hull 0.0°); the animator's own TurretYaw is -0.588 rad (-33.7°) in the model's frame
query:   note     local = as the model declares it, motion = what the animator applied, world = the chain composed; nose is the image of the model's own -Z, which is the front every generated model is authored with
query:   [31] 'turret' parent -  local T(0.00, 1.48, 0.00) m X(1.00, 0.00, 0.00) Y(0.00, 1.00, 0.00) Z(0.00, 0.00, 1.00)  motion rotates about (0.00, 1.00, 0.00) by +33.7°  bounds max (1.2, 1.1, 2.0) m  world T(-7.46, 15.16, -212.00) m X(-0.46, 0.00, 0.70) Y(0.00, 0.84, 0.00) Z(-0.70, 0.00, -0.46)  nose (0.83, 0.00, 0.55) bearing 33.7°
query:   [43] 'turret_ring' parent -  local T(0.00, 1.39, 0.00) m X(1.00, 0.00, 0.00) Y(0.00, 1.00, 0.00) Z(0.00, 0.00, 1.00)  motion none (drawn exactly as the model declares it)  world T(-7.46, 15.09, -212.00) m X(-0.00, 0.00, 0.84) Y(0.00, 0.84, 0.00) Z(-0.84, 0.00, -0.00)  nose (1.00, 0.00, 0.00) bearing 0.0°
query:   2 parts listed of 54
```

### A building with a rotating part

```
query: parts slot 0 soviet/CommandCentre team 0, tick 40
query:   model    Generated/soviet_hq.glb, scale 0.526 x 0.526 x 0.526, 35 parts, wheel radius none — any wheel_ part on this model is drawn still, filtered to 'radar'
query:   entity   at (-180.0, 0.0, -180.0) m, heading 0.0°, which the entity transform turns into (1.00, 0.00, 0.00) bearing 0.0°
query:   turret   the turret part points 0.0° off the hull (world bearing 0.0°, hull 0.0°); the animator's own TurretYaw is 0.000 rad (0.0°) in the model's frame
query:   note     local = as the model declares it, motion = what the animator applied, world = the chain composed; nose is the image of the model's own -Z, which is the front every generated model is authored with
query:   [21] 'radar' parent -  local T(-1.00, 31.30, 3.00) m X(1.00, 0.00, 0.00) Y(0.00, 1.00, 0.00) Z(0.00, 0.00, 1.00)  motion rotates about (0.00, 1.00, 0.00) by -45.8°; turned -45.8° about (0.00, 1.00, 0.00) over 40 ticks = -1.1°/tick  bounds max (2.9, 3.5, 1.8) m  world T(-181.58, 16.47, -180.53) m X(0.38, 0.00, 0.37) Y(0.00, 0.53, 0.00) Z(-0.37, 0.00, 0.38)  nose (0.70, 0.00, -0.72) bearing -45.8°
query:   1 part listed of 35
```

A model has tens of parts and most of them never move, so `parts <slot>` prints one line per
part and `parts <slot> <name>` narrows it to the ones a name contains. The counts at the end
say how much was shown and how much there was.

## Determinism

The same script against the same seed produces the same transcript and the same PNGs, byte
for byte, including scripts that settle particles and render frames — the client is stepped
from the script's own clock rather than the wall clock, a probe frame is photographed at a
tick boundary rather than between two ticks, and the particle system's generator is seeded.
No line in a transcript carries a time or a duration measured from the machine.

Verified by running `tools/probe/batch.probe` twice and comparing hashes:

```
batch-det1.txt  4C7ED6E5F6B639CFEB838DE0460A3456…
batch-det2.txt  4C7ED6E5F6B639CFEB838DE0460A3456…
batch-a-1.png   246D61C9975F1467F9208891E0A61B28…
batch-a-2.png   246D61C9975F1467F9208891E0A61B28…
```

The exception is the *scenario*: a fixture's own setup decides what is on the map, so a
script written for `--turret-demo` answers for the two tanks that fixture places.

## What it does not do

- **No reactivity.** `expect` compares literals; it cannot compare two query results. A
  script that needs "is the turret's bearing the same as the shot's" writes the bearing into
  the script as an expected value.
- **No general orders.** A probe cannot march a column anywhere or make a unit attack. It can
  do exactly one thing the player can do — arm a placement and click — and only through the
  functions the mouse itself goes through, which is why `arm`/`hover`/`click` are three
  commands rather than one and why none of them writes a command of its own. Everything else
  the fixtures and the AI put into the world.
- **No `MiVic.Core` knowledge of its own.** The probe is a client tool: it reads the
  simulation and the renderer and changes neither. `bridge … build` is the one command that
  enqueues anything, and it enqueues the command a click would.
- **Not a game.** It skips the HUD, as the fixtures do, because a panel over the frame is a
  panel over the answer — unless a script asks for it with `hud on`.

## Design note: the probe asks the renderer

Everything that could drift between the answer and the picture is asked for rather than
worked out again:

| Question | Answered by |
|---|---|
| a part's animated transform | `MiVicGame.AnimatePart` — the same call the submission loop makes |
| the entity transform a part hangs off | `MiVicGame.EntityTransform`, shared with the submission loop |
| the chain a part's world transform composes | `MiVicGame.PartWorldTransform`, shared with the submission loop |
| the turret's angle | `MiVicGame.TurretYaw`, the renderer's own aiming |
| the frame a shot is taken of | `DrawScene` and `DrawWorldLabels`, the same scene `Draw` submits |
| whether an entity is drawn | the client's own fog and stealth test |

The rules this keeps: a probe that reimplemented the turret's aim could disagree with the
turret, and a report that disagrees with the picture is worse than no report — it is a
report that sends someone to fix the wrong file. The same reason is why
`Probe/MiVicGame.Probe.cs` is a partial of the client: the answers are only trustworthy
because they come out of the client's own code.
