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
questions about them. `--duel` and `--rivals` compose with it the same way and are not fixtures: they
choose a *match* — two factions instead of three — and the probe asks questions about it.

Two fixtures exist because a probe cannot place a unit. They are `--emplacement-demo`, which
clears the field and puts a command centre and three enemies on the clearest ground the map
has, and `--detection-demo`, which lays a defensive post, a radar and a base up the map's centre
column with a tank held at 190 m and a Καταδρομέας walking down it — see
`tools/probe/detection.probe` below for what that one is for. A third, `--alliance-demo`, puts two
allied tanks inside each other's killing range with an enemy further out, because no match puts them
there: see `tools/probe/alliance-demo.probe`.

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
| `bridges` | every crossing on the map: the cells it spans, how much of it the work has reached, how many of its blocks still stand, whether it has been cut, and the tick it will be whole on |
| `structure <kind> <x> <z> [team] [build]` | whether a structure could be raised at that cell, the reason when it could not, the cell it would stand on, what it costs and how long it takes to rise — and with `build`, the order that raises it |
| `sites <kind> <x> <z> [radius m] [team]` | a census of the placement rule around a point: how many cells on the map would take that building, how many the ground refuses and for which reasons, how many the buildings already standing there refuse, and the nearest cell that would take it |
| `structures [team]` | every structure a team has: its role, the cell it stands on, how much of it is up, and its hit points |
| `block <x> <z>` | what deck stands on one cell: how much is left of it, which team owns it, which way it runs — including whether it is a **junction**, which is a fact about the cell rather than about any crossing — and which crossings pass through it |
| `blast <x> <z> [radius] [damage] [team]` | drops a blast on the ground, as a salvo or a strike does, and reports how many blocks of deck it knocked out and what is left of the one at the centre |
| `range <slot>` | the sensor chain for one entity: the weapon's range, its own eyes, the radius those eyes find a hidden enemy at, whether a powered radar is covering it, and **the furthest it can engage anything at** — which is the smaller of the first two until a radar changes the answer |
| `power [team]` | one team's power ledger: energy generated, energy drawn by the structures that are on, the surplus, how many radars are lit and how many the grid had to shed, and the reason the interface gives for a brown-out, in Greek |
| `detect <team> <slot>` | whether one team can see one entity, and by which channel: hidden, the cell's sight, the cell's detection, and whether the target has revealed itself by firing |
| `exposure <team> <x> <z>` | what one team knows about a point on the ground: whether a powered radar covers it, whether the cell is visible, and whether it is detected — the query to walk a boundary across one reading at a time |

### What the player would see

A question about a click cannot be answered from the world: the world only knows that nothing
happened, and *why* is spread across the client's projection, the interface and the rules.
These four commands walk that path in the order a player does — arm, aim, click, look — and
each of them prints the answer the client itself worked out.

| Command | Answer |
|---|---|
| `arm bridge` | what pressing Γέφυρα does, through the HUD's own command path, and whether the client is now waiting for a site |
| `arm <role>` | the same for a structure: what pressing its row — `Factory`, `PowerPlant`, `CommandCentre`, `DesignBureau`, `NuclearPlant`, by catalogue name or by the Greek label on the button — does, and whether the client is now waiting for a site to raise one on |
| `hover <x> <z> [y]` | puts the script's cursor on a world point: the pixel it projects to, what the client resolves that pixel back to, how far that is from where it was aimed, and — when a placement is armed — whether the ghost is green or red, what is being placed, and how many cells it takes |
| `click [x z]` | releases the left button at the script's cursor, through the client's own click path: what it resolved to, whether an order was issued, and the words the player is shown when it was refused |
| `hud [on\|off]` | whether probe frames draw the HUD. Off by default; on when the answer *is* the panel, and a shot then carries it |

`hover` aims at the surface the renderer draws at that point — the water line over water, the
height field elsewhere — because that is what a cursor is over. Give `y` to aim at something
that is not on the ground, as `focus` allows.

`build` on `bridge` is the first of two commands in the tool that change the world the script is
looking at, and it exists because a crossing cannot be inspected until it has been built: the cells
it turns into ford are the answer, and there is no other way to ask for them. `build` on
`structure` is the second, and for the same reason: a structure ordered at a site is a building
site for the whole of its construction, so a script that wants to know whether the building came up
where it was aimed has to order one. Everything else here reads. A bridge takes time to build and a
structure takes time to rise, so a script that wants to see either finished advances the ticks
itself or asks `bridges` — or `structures` — how much is left.

`blast` is the third, and it is damage rather than an order: a crossing that can be knocked
down cannot be inspected either, and there is no other way to ask what a hole in one looks
like. It calls the same area damage a Κατιούσα salvo and an off-map strike call, so what a
script breaks is what a battlefield breaks.

`block` exists because a junction cannot be found in the `bridges` list. Two crossings may share a
cell — the simulation allows it, and thousands of pairs of sites on this map do — and the deck
there is one block that both of them run through. Asking `bridges` twice tells you two spans
overlap somewhere; asking `block` tells you the cell, which is the thing that can be looked at:

```
cmd: block -89.1 -295.3
query: cell cell 22,0 of 65, index 22, ShallowWater (Νερό) at (x -89.1, z -295.3) m
query:   deck       200/200 left, owner team 0, runs Junction
query:   crossing   #0 runs through it, at cell 13 of 15
query:   crossing   #1 runs through it, at cell 0 of 2
```

One block at 200 hit points rather than two at 200 each, running both ways, with both crossings
through it: knock that block out and neither crossing can be used, because there is one deck under
them and it is gone.

### Render-state queries

| Command | Answer |
|---|---|
| `parts <slot> [name]` | every part of that entity: its declared transform, what the animator did to it, the axis and angle of that rotation, the per-tick rotation where the script has sampled it twice, and its final world transform and bearing |
| `model <faction>/<kind>` | the model entry a role resolves to: file, import options, fitted scale, wheel radius, every part it declares, and **which other roles import the same file** |
| `visible` | what a frame submits, where the camera is and what ground it covers, how many entities project inside the viewport, and a surface census of the ground in frame |

### Events

| Command | Answer |
|---|---|
| `events [n]` | the most recent simulation events the client has seen: shots with the direction fired and the range, hits with the damage, deaths with the position. Every line names the team as well as the faction, because the question a reader of the stream is asking is usually a question about sides |
| `teams` | who is on whose side and what has actually passed between them: **what the match declares** — which teams are playing, the faction each one plays, the side each one is on and which slots are not in the match at all — then each team with what is alive on it, the alliance of every pair asked of `SimWorld.AreAllied` — the same question a weapon asks — **the victory rule's own verdict** and which sides it was reached from, and a running ledger of shots by pair of teams, health lost per team, and **whether any weapon has aimed at, fired at or damaged an ally**. It records a check of its own, so a run that reads a violation fails |

Firing is not re-detected here: the events come from `SimBridge`'s own cooldown diffing, so
a shot in the transcript and a tracer on screen are the same shot.

### Checks

| Command | Effect |
|---|---|
| `expect <label> <actual> <expected> [tolerance]` | record a check. Two numbers compare numerically, anything else as text; a tolerance covers answers that are themselves rounded. |

`expect` takes literals, not query results: it is a ledger of values you measured once and
want to keep true. `expect "the map has lava" 55 55` fails the run the day lava stops being
generated — which has happened once already. A command whose answer is an *invariant* rather than a
measurement records its own check instead, with the same two prefixes and the same exit status:
`teams` fails the run when some weapon has aimed at, fired at or damaged an ally, because a
transcript that reads a violation and exits 0 is a transcript that hides it.

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

## Worked example: where does the building I ordered go?

A structure used to be produced by another structure and to appear at a fixed offset from whatever
made it, which is a position nobody chose and nobody could see before paying for it.
`tools/probe/structure.probe`, run against the default skirmish, trimmed to the answers:

```
cmd: structure Factory -248.4 -89.1
query: structure Factory (Εργοστάσιο) at (x -248.4, z -89.1) m — cell 5,22 of 65, index 1435, Sand (Άμμος)
query:   verdict    accepted for team 0 — it would stand at (x -248.4, z -89.1) m, cell 5,22 of 65, index 1435, Sand (Άμμος)
query:   work       272 ticks (13.6 s) of construction, rising out of the ground over the whole of it
query:   cost       252 Π, 0 Ε, 108 Ν, 2000 hit points
cmd: structure Factory -257.0 -210.0
query: structure Factory (Εργοστάσιο) at (x -257.0, z -210.0) m — cell 4,9 of 65, index 589, DeepWater (Βαθύ νερό)
query:   verdict    refused — χρειάζεται στεριά
query:   player     Εργοστάσιο: χρειάζεται στεριά.
cmd: structure Factory -248.4 -117.2
query: structure Factory (Εργοστάσιο) at (x -248.4, z -117.2) m — cell 5,19 of 65, index 1240, Forest (Δάσος)
query:   verdict    refused — επικαλύπτεται με Κέντρο Διοίκησης
query:   player     Εργοστάσιο: επικαλύπτεται με Κέντρο Διοίκησης.
cmd: arm Factory
query: arm factory — armed Εργοστάσιο — the next left click picks the site
cmd: hover -248.4 -89.1
query:   ground     resolved to (-248.0, 7.5, -89.0) m — cell 5,22 of 65, index 1435, Sand (Άμμος), 0.4 m from where it was aimed
query:   placement  accepted — the ghost is the Εργοστάσιο on 9 cells of ground and is green
cmd: hover -245.3 -95.3
query:   ground     resolved to (-245.0, 6.3, -95.0) m — cell 5,21 of 65, index 1370, Sand (Άμμος), 0.4 m from where it was aimed
query:   placement  refused — επικαλύπτεται με Κέντρο Διοίκησης; the ghost is red and the panel says so
cmd: click
query:   result     nothing was issued
query:   notice     the player is told "Εργοστάσιο: επικαλύπτεται με Κέντρο Διοίκησης."
cmd: click
query: click — armed yes, resolved (-248.0, 7.5, -89.0) m
query:   ground     cell 5,22 of 65, index 1435, Sand (Άμμος)
query:   result     an order was issued
cmd: tick 1
cmd: structures 0
query:   slot  510 Factory (Εργοστάσιο) at (x -248.4, z -89.1) m, cell 5,22 — building — 271 of 272 ticks left, 13.6 s, 2000 hit points
cmd: tick 160
cmd: structures 0
query:   slot  510 Factory (Εργοστάσιο) at (x -248.4, z -89.1) m, cell 5,22 — whole, 2000 hit points
```

Seven facts, and each of them was a different question before this command existed:

- **the simulation's own verdict**, per cell, with the reason: water and lava are refused by name,
  so is a role that is not a structure at all (`structure Tank …` answers `δεν είναι κατασκευή`
  in the full transcript), and so is a cell a building is already standing on
  (`επικαλύπτεται με Κέντρο Διοίκησης`);
- **the cell it would stand on**, which is the cell centre rather than the millimetre the cursor
  resolved to, because the patch of ground that was judged is the cell's — the click at
  `(-248.4, -89.1)` is a building on cell 5,22;
- **the ghost**, which is the building's own model rather than a footprint rectangle, green where
  the plan accepts and red where it refuses — over the water, and over the headquarters, which are
  the two cases a player most needs to see refused. `hover` reports the ground it stands on, 9 cells
  for a factory, because that is what the plan asked about and what it refuses on;
- **the refusal the player reads**, in the notice a refused click raises and beside the armed row
  in the panel, which is what `hud on` photographs;
- **the order**, which is issued by the same click path a mouse release takes and changes nothing
  until it is;
- **the building site**, standing on the cell the plan named on the next tick and rising there over
  the thirteen seconds the catalogue quotes. `structures` is the command for that last one: `count`
  says how many factories a team has and `units` buries one line among five hundred, so neither
  answers "where did it go, and is it up yet"; and
- **the two refusals that are not about the map**. Eighteen metres north of the headquarters is the
  site this script used to build on: the ground there is sand and the plan still refuses it, because
  a cell is 9.4 m and a building is 28 m of ground — two of them eighteen metres apart are two
  buildings sharing a wall. `sites` is the command that says how much ground is left.

## Worked example: is the placement rule usable near a base?

"No site near my own base is ever accepted" is not a question any single `structure` line can
answer: one cell is refused, and why *that* cell is refused says nothing about the other four
hundred. `sites` asks the rule about a neighbourhood and prints the census — the same script, the
first command in it:

```
cmd: sites Factory -248.4 -117.2 100
query: sites for Factory (Εργοστάσιο) within 100.0 m of (x -248.4, z -117.2) m — 391 cells on the map at 9.4 m each
query:   accepted   81 of 391 (20.7%) would take Εργοστάσιο, for team 0
query:   nearest    (x -220.3, z -117.2) m, 28.1 m from the centre of the search
query:   footprint  140 of 391 cells pass the ground rule for a 3 x 3 footprint (9 cells of ground, radius 1)
query:      168 cells  χρειάζεται στεριά
query:       83 cells  ανώμαλο έδαφος
query:   standing   59 of the 140 would find a structure already on the ground
query:       25 cells  επικαλύπτεται με Κέντρο Διοίκησης
query:       18 cells  επικαλύπτεται με Σταθμός Παραγωγής
query:       10 cells  επικαλύπτεται με Εργοστάσιο
query:        6 cells  επικαλύπτεται με Γραφείο Σχεδιασμού
```

Five numbers, and each one answers a different half of the question:

- **81 of 391** is the answer, and the number to watch. The same box accepted **15** cells while a
  structure was judged on a base's yard — an 11 × 11 patch with 90% of it solid and a solid 2 × 2
  core — and all fifteen of them were inside the base's own yard, because the only ground within a
  hundred metres that would hold an entire base was the base. Every other direction was
  `ανώμαλο έδαφος`, which is how a factory twenty metres west of the player's own headquarters came
  to be refused for failing a test meant for a base;
- **168 cells are water** and 83 are ground with a hole in it, so 251 of the 391 are the map's
  answer rather than the rule's: a base beside a lake has a lake beside it, and no rule makes a
  lake buildable;
- **140** is what the rule thinks of the 140 cells that are ground: the footprint is the building's
  own, three cells by three, and all nine of them have to be solid. That is what "a building needs
  its own ground" means, and it is why this number is not 391;
- **59 of those 140 are standing on a building**: 25 cells are inside the headquarters' own
  footprint, and the rest belong to the power plant, the factory and the design bureau the scenario
  planted around it. A base is a crowded place by design, and the census names which building is in
  the way rather than saying "occupied";
- and **28.1 m** is how close the base's own headquarters will let a factory come. Three cells is
  the distance at which two 28 m footprints stop sharing a cell, and it is the number a player
  discovers by moving the cursor.

Which clause is doing the work is the whole reason for printing a census rather than a total: a
change to the footprint moves the third line, a change to the occupancy rule moves the fourth, and
the first two are the map.


## Worked example: does a defensive structure shoot on its own?

A building that has to be told to fire never fires in a real game, because the player has no
reason to think of it. So the question is not "does the weapon work" but "does it choose", and
it cannot be answered by an assertion: the answer is a stream of shots that nobody ordered,
against a target that was picked rather than named.

`tools/probe/emplacement.probe`, run against `--emplacement-demo` — a fixture that clears the
field and puts a tank and an aircraft 72 m from the middle of the clearest ground on the map, a
tank 150 m out, and a command centre behind them to raise the buildings from. Trimmed to the
answers (`…` marks lines cut out of the middle):

```
cmd: structure GunEmplacement 23 -192 0 build
query:   verdict    accepted for team 0 — it would stand at (x 23.4, z -192.2) m, cell 34,11 of 65, index 749
query:   work       163 ticks (8.2 s) of construction, rising out of the ground over the whole of it
ok: Πυροβολείο ordered for team 0 at (x 23.4, z -192.2) m, executing on tick 2
cmd: tick 180
cmd: structures 0
query:   slot  505 GunEmplacement (Πυροβολείο) at (x 23.4, z -192.2) m, cell 34,11 — whole, 889 hit points
cmd: tick 2
cmd: events 16
query:   #249 tick 161 hit       slot  505 soviet/GunEmplacement … took 28 damage
query:   #250 tick 161 shot      slot  507 western/Aircraft … firing at soviet/GunEmplacement (slot 505) …
query:   #251 tick 164 shot      slot  505 soviet/GunEmplacement at (23.4, 7.7, -192.2) m firing at western/Tank (slot 506), direction (0.83, 0.03, 0.56) bearing 34.0°, 71.9 m away
query:   #252 tick 164 hit       slot  506 western/Tank … took 44 damage
cmd: unit 505
query:   attack     slot 506 (western/Tank at 71.9 m), cooldown 23 ticks of 50, order automatic, 45 damage out to 200.0 m
query:   move       none, path 0 cells at 0, 0 failures
cmd: events 8
query:   #250 tick 475 shot      slot  504 soviet/AntiAirEmplacement … firing at western/Aircraft (slot 507), direction (0.56, 0.52, -0.64) bearing -48.7°, 90.4 m away
query:   #251 tick 475 hit       slot  507 western/Aircraft … took 30 damage
query:   #256 tick 488 destroyed slot  507 western/Aircraft at (83.0, 68.0, -232.0) m
query:   slot  506 western Tank             team 2 at (x 83.0, z -152.0) m heading 0.0° health 12/320 target 505 building no
```

Five facts, and each of them is a different failure mode:

- **the emplacement finished rising on tick 163 and fired on tick 164** — one tick later, with
  no order given to it in between. The `structure … build` line is the last thing that touched
  it, and that line only chose the site;
- **`order automatic`** in the `unit` line, which is the client reading
  `Entity.HasAttackOrder` off the simulation: the gun is engaging something it picked;
- **the target is the tank at 71.9 m, not the aircraft at 71.7 m** — and the aircraft is firing
  at the emplacement throughout (`#250`, and every fourth line after it). An enemy that is
  nearer, visible and shooting it is still not a target, which is what an anti-aircraft clause
  that leaked into a gun would look like when it went wrong;
- **the aircraft dies to slot 504, the anti-aircraft emplacement**, at tick 488 — and the same
  stream contains no shot by 504 at either tank, because that weapon cannot touch the ground;
- **the shot bearing is 34.0° and the direction it fired in is `(0.83, 0.03, 0.56)`** — the
  muzzle and the shot agreeing, which is the check every turret in this game lives or dies by.
  `parts 505 turret` prints the same bearing from the other side of the engine.

The `events` stream is the evidence rather than a summary of it: those lines come from
`SimBridge` watching `AttackCooldown`, so a shot in the transcript and a tracer on screen are
the same shot.

## Worked example: does a radar give the guns behind it their reach?

Detection is not firing range in this game. A Πυροβολείο's weapon reaches 200 m and its own
eyes reach 170, so a tank at 190 m is inside the first and outside the second — engaged only
while something else is looking, which is what a Σταθμός Ραντάρ is for. None of that can be
asserted into existence: the answer is a distance that changes, and the change has to be read
off a running world.

`tools/probe/detection.probe`, run against `--detection-demo` — a fixture that lays a
defensive post and a base up the map's centre column, holds a tank 190 m from the gun, walks a
Καταδρομέας down the column, and lets the probe build one more factory than the grid can run.
Trimmed to the answers (`…` marks lines cut out of the middle):

```
cmd: power 0
query:   generation 16 Ε per tick, from the structures standing
query:   draw       15 Ε per tick, including the radars that are on
query:   radars     1 lit, 0 dark, 0 Ε short of running them all
cmd: range 503
query:   gun        200.0 m, 45 damage every 50 ticks
query:   eyes       170.0 m — as far as its own sensors reach
query:   stealth    85.0 m — as far as they find a hidden enemy
query:   radar      under coverage, team 0 has 1 radar on the air
query:   reach      200.0 m — the furthest it can engage anything at
cmd: detect 0 500
query: detect 500 western/StealthRecon (Καταδρομέας) at (x 0.1, z -264.6) m against team 0
query:   hidden     yes — no weapon of team 0 may engage it
cmd: tick 20
cmd: detect 0 500
query: detect 500 western/StealthRecon (Καταδρομέας) at (x 2.4, z -256.5) m against team 0
query:   hidden     yes — no weapon of team 0 may engage it
cmd: tick 10
cmd: detect 0 500
query: detect 500 western/StealthRecon (Καταδρομέας) at (x 3.5, z -252.5) m against team 0
query:   hidden     no
query:   revealed   no, own team False
…
query:   #255 tick 103 shot      slot  503 soviet/GunEmplacement … firing at western/Tank (slot 502), direction (0.00, 0.00, 1.00) bearing 90.0°, 190.0 m away
cmd: structure Factory 200 -140 0 build
ok: Εργοστάσιο ordered for team 0 at (x 201.5, z -136.0) m, executing on tick 232
cmd: tick 300
cmd: power 0
query:   radars     0 lit, 1 dark, 3 Ε short of running them all
query:   brown-out  λείπει ισχύς 3 Ε
cmd: range 503
query:   radar      not under coverage, team 0 has 0 radars on the air
query:   reach      170.0 m — the furthest it can engage anything at
cmd: hud
query: hud — the HUD is drawn into probe frames, notice "Σταθμός Ραντάρ: λείπει ισχύς 3 Ε."
query:   #255 tick 460 hit       slot  502 western/Tank … took 44 damage
cmd: tick 60
query:   health     99560/320 (31112%)
```

Five facts, and each of them is a different failure mode:

- **the same gun reports 200.0 m and then 170.0 m**, with nothing about the gun changing: at
  tick 1 it is under a lit radar's coverage and at tick 531 the grid has shed that radar. The
  difference between the two numbers is 30 m of reach and it exists only because something
  stopped looking;
- **the tank at 190 m is shot for as long as that is true and not afterwards** — hits at ticks
  52, 103, … 460, and then 99560 hit points at tick 531 and the same 99560 sixty ticks later.
  `unit 503` still names the target after the brown-out, which is the other half of the rule:
  a gun that has lost sight of what it was shooting keeps hold of it rather than forgetting;
- **the Καταδρομέας crosses the line between two readings** — hidden at 145 m and at 133 m from
  the radar, seen at 128 m. That is half of the radar's 260 m, and it is the radar doing it:
  `revealed no` on every line says the stalker never fired, so nothing here is a firing reveal
  wearing a detection's clothes;
- **`draw 15` against `generation 16`** — one more factory is 19 against 16, the ledger sheds
  the radar, and `brown-out λείπει ισχύς 3 Ε` is the same sentence the player is shown. The
  dish stops where it stood, which `parts 504 radar` reports as `did not turn over the last 40
  ticks` on a part that was turning at 1.1° a tick;
- **the two shots** (`detection-covered.png`, `detection-dark.png`) are the same gun from the
  same camera with the HUD on: the ring on the ground is the reach, and it is visibly smaller
  in the second one.

## Worked example: does the computer opponent build a defensive line?

Whether an AI *knows* about defence is not a question any assertion answers: it is a question about
what a base looks like a minute into a match, on ground nobody chose in advance. The emplacement,
radar and power questions were all asked above about structures a script had placed; this one is
asked about structures the AI decided on, with no script touching them.

`tools/probe/ai-line.probe`, run against the default skirmish — teams 1 and 2 played by `AiSystem`,
nobody playing team 0 — trimmed to the answers (`…` marks lines cut out of the middle):

```
cmd: tick 60
cmd: structures 1
query: structures: 7 structures for team 1, tick 62
query:   slot  170 CommandCentre (Κέντρο Διοίκησης) at (x 145.3, z -98.5) m, cell 47,21 — whole, 5000 hit points
…
query:   slot  510 GunEmplacement (Πυροβολείο) at (x 42.2, z 4.7) m, cell 36,32 — building — 78 of 120 ticks left, 3.9 s, 1400 hit points
query:   slot  511 RadarStation (Σταθμός Ραντάρ) at (x 173.4, z 4.7) m, cell 50,32 — building — 124 of 146 ticks left, 6.2 s, 900 hit points
query:   slot  513 GunEmplacement (Πυροβολείο) at (x 4.7, z -61.0) m, cell 32,25 — building — 118 of 120 ticks left, 5.9 s, 1400 hit points
cmd: attributes 42.2 4.7
query: attributes at (x 42.2, z 4.7) m — cell 36,32 of 65, index 2116
query:   surface    Forest (Δάσος), churn 0/255
query:   ground     vegetation 246/255, moisture 7/15, aspect flat (8), landform valley (3), fuel 0, flags none
query:   cover      foot 540 ‰, tracked 810 ‰, wheeled 810 ‰, air 1000 ‰   (damage that lands: 1000 ‰ is no cover at all)
cmd: attributes 145.3 -98.5
query:   cover      foot 927 ‰, tracked 978 ‰, wheeled 978 ‰, air 1000 ‰   (damage that lands: 1000 ‰ is no cover at all)
cmd: range 510
query:   gun        200.0 m, 45 damage every 50 ticks
query:   eyes       170.0 m — as far as its own sensors reach
query:   radar      under coverage, team 1 has 1 radar on the air
query:   reach      200.0 m — the furthest it can engage anything at
cmd: unit 510
query:   health     746/1400 (53%)
query:   attack     slot 340 (western/CommandCentre at 193.2 m), cooldown 36 ticks of 50, order automatic, 45 damage out to 200.0 m
cmd: power 1
query:   generation 16 Ε per tick, from the structures standing
query:   draw       11 Ε per tick, including the radars that are on
query:   surplus    5 Ε per tick
query:   radars     1 lit, 0 dark, 0 Ε short of running them all
query:   brown-out  none
cmd: events 12
query:   #250 tick 548 shot      slot  510 chinese/GunEmplacement at (42.2, 18.1, 4.7) m firing at western/DesignBureau (slot 343), direction (-0.01, -0.01, 1.00) bearing 90.8°, 142.5 m away
```

Five facts, and each of them was a different failure mode before this was built:

- **three building sites exist by tick 62 that no script ordered** — a gun, the radar that gives it
  its reach, and a second gun — and none of them is at an offset from anything: the AI asked for a
  site through the same plan a click is judged by, so `structures` prints the cell each one chose;
- **the ground under it is a choice, and the answer is visible**: the emplacement stands in closed
  woodland in a valley at `cover foot 540 ‰`, against `927 ‰` under the headquarters it was bought to
  defend. That is the same number the combat system scales every hit on that building by, so the
  ground the AI picked is 39 % off every shot that lands on it. An AI that scored its sites by
  distance alone would have both guns on the headquarters' own kind of ground;
- **`reach 200.0 m` against `eyes 170.0 m`** is the radar being worth its price, and `unit 510`
  is the proof of what that is for: the gun is engaging a headquarters **193 m away**,
  `order automatic`, which is past the 170 m it could see without one. The kill it is in the middle
  of — 746 of 1400 hit points — is a fight it would not have been in at all;
- **`generation 16` against `draw 11`, `1 lit, 0 dark, brown-out none`** is the failure mode this
  feature can create, answered: an AI that raises a dish its grid cannot run has paid for a building
  that switches itself off, and detection is what the ledger sheds first. This one bought the
  generation it needed before it bought the radar, and the ledger is still solvent a minute later;
- **the shot in `events`** is the same emplacement, at 142.5 m, with the bearing on the shot and the
  bearing of the building's own model agreeing — a defensive line that fights rather than one that
  stands.

The script carries the numbers as checks, so the day the AI stops building a line, or starts
shedding the radar it built, the probe fails rather than reporting something else.

## Worked example: do allied forces damage each other?

Teams 0 and 1 are on the same side in every match this game ships, so "an ally is not a target" is a
rule the engine either keeps or breaks, and no single line of a transcript is the answer: it takes
the sides, the fire, and the health lost, over a match. `tools/probe/alliance.probe`, run against the
standard skirmish with nobody playing, trimmed to the answers (`…` marks lines cut out of the
middle):

```
cmd: tick 200
cmd: teams
query: teams: 3 teams in play of 4 slots at tick 200
query:   team 0 soviet   170 alive, 4 structures
query:   team 1 chinese  172 alive, 6 structures
query:   team 2 western  173 alive, 7 structures
query:   sides      0+1 allied, 0+2 hostile, 1+2 hostile — SimWorld.AreAllied, which is what every weapon asks
query:   shots      0 at 1: 0, 0 at 2: 36, 1 at 0: 0, 1 at 2: 1, 2 at 1: 195 — since the script started, …
query:   damage     team 0 0 hits, 0 losses, 0 health; team 1 74 hits, 3 losses, 3665 health; team 2 20 hits, 0 losses, 846 health
query:   friendly   none — no weapon held an ally as a target, none fired at one, and no damage went unexplained
check: PASS 'no weapon aimed, fired or damaged an ally' — 0 ticks with an ally held as a target, 0 shots at an ally, 0 damage events unexplained
query:   in reach   0 armed units have a unit of an allied team inside the reach they can engage at: …
```

Five facts, and the fourth is the one the command fails the run over:

- **`sides` comes from the world, not from the script**: `SimWorld.AreAllied` is the same predicate
  a weapon, a salvo, an attack order, a bridge and an off-map strike ask, so this line cannot
  disagree with what the guns did;
- **`shots` names the allied pairs whether or not they are zero**, so `0 at 1: 0` is a reading
  rather than an omission — where a hostile pair that has never fired is simply left out;
- **the damage continues in the direction it should**: team 1 loses 3 665 health and 3 units to team
  2, team 2 loses 846 to the allies, and team 0 — the team nobody is playing — is untouched;
- **`friendly` is three numbers that all have to be zero, and the command records them as a check**,
  not as a line to compare by eye: a weapon *holding* an ally as a target on any tick, a shot fired
  at one, and a damage event or a loss in a tick where no team hostile to the victim fired. The last
  is the door artillery comes through — a scattered salvo catches an ally without ever naming one —
  so no check on targets alone can see it;
- **`in reach` reads zero here, and that is worth being exact about.** The two allied armies never
  come within weapon reach of each other in this match at all, so the zeros above say that nothing
  passed between them and not that anything would have.

Forcing the case needs a scene no match provides, which is what `--alliance-demo` is:
`tools/probe/alliance-demo.probe` against it, with the tanks' five thousand hit points showing as
`5000/320` because `units` prints the role's catalogue health after the slash:

```
cmd: tick 1
cmd: teams
query:   sides      0+1 allied, 0+2 hostile, 1+2 hostile — SimWorld.AreAllied, which is what every weapon asks
query:   shots      0 at 1: 0, 0 at 2: 1, 1 at 0: 0, 1 at 2: 1, 2 at 1: 1 — …
query:   in reach   4 armed units have a unit of an allied team inside the reach they can engage at:
                     0 aimed at an ally, 2 at an enemy, 2 at nothing, and 1 had the ally nearer than the target they
                     were firing at — e.g. … slot 509 team 0 with an ally at 60.0 m aiming at western/Tank (slot 507,
                     team 2) at 100.0 m
cmd: unit 509
query: unit 509 soviet/Tank Tank (Άρμα), team 0, generation 1
query:   attack     slot 507 (western/Tank at 100.0 m), cooldown 14 ticks of 24, order automatic, 35 damage out to 110.0 m
cmd: unit 506
query:   attack     none, cooldown 5 ticks of 24, order automatic, 35 damage out to 110.0 m
```

Three facts:

- **the Σοβιετικοί tank at slot 509 is firing at a Δυτικοί tank a hundred metres away while a Κινέζοι
  ally of its own stands at sixty** — the ally is the *nearer* of the two candidates, so nothing but
  the side they are on can have decided it, and `1 had the ally nearer than the target they were
  firing at` is that fact counted;
- **the tank at slot 506 has an ally sixty metres away and no enemy in reach at all**, and it holds
  no target — an ally is not a target even when it is the only thing there is;
- **the same run against the engine before this rule was fixed** reads `attack slot 508
  (chinese/Tank at 60.0 m)` for slot 509 and `attack slot 505 (chinese/Tank at 60.0 m)` for slot 506:
  `603 ticks with an ally held as a target, 32 shots fired at an ally, 29 damage events unexplained`,
  two failed checks and an exit status of 1. The same script, the same fixture, the same seed — which
  is what makes it an instrument rather than an illustration.

## Worked example: can a match have two factions?

Every match this game shipped put three factions on the map and decided it by asking after teams 0, 1
and 2 by number, so "Σοβιετικοί against Κινέζοι with the Δυτικοί absent" was not a match the engine
could describe, let alone finish. A match now **declares its teams** — which ones are playing, the
faction each one plays and the side each one is on — and the victory rule, the AI, the interface and
this command all read that declaration rather than the numbers.

`--rivals` starts such a match: Σοβιετικοί (team 0, the player) against Κινέζοι (team 1, the
computer), with the Δυτικοί nowhere on the map. `tools/probe/two-faction.probe` runs it, trimmed here
to the answers (`…` marks lines cut out of the middle):

```
cmd: teams
query: teams: 2 teams in play of 4 slots at tick 0
query:   match      2 teams declared: 0 soviet side 0, 1 chinese side 1; not in the match: team 2 (western), team 3 (none) — MatchRoster, which is what the victory check and the AI read
query:   team 0 soviet   170 alive, 4 structures
query:   team 1 chinese  170 alive, 4 structures
query:   sides      0+1 hostile — SimWorld.AreAllied, which is what every weapon asks
query:   outcome    ongoing; still holding structures: team 0, team 1
…
cmd: tick 1000
cmd: teams
query:   shots      0 at 1: 16, 1 at 0: 7 — since the script started, read from the world's own cooldowns …
query:   damage     team 0 7 hits, 0 losses, 260 health; team 1 12 hits, 8 losses, 500 health — the health lost by the team that lost it
cmd: events 10
query:   #39 tick 1000 shot      slot  178 chinese/AntiAir team 1 at (-67.6, 2.9, -45.5) m firing at soviet/PowerPlant (slot 1, team 0), direction (-0.98, 0.02, -0.19) bearing -168.9°, 138.4 m away
query:   #40 tick 1003 shot      slot  128 soviet/AntiAir team 0 at (-219.2, 8.1, -100.4) m firing at chinese/Aircraft (slot 203, team 1), direction (0.93, 0.36, 0.09) bearing 5.7°, 139.4 m away
```

Four facts, and each of them used to be an assumption:

- **`match` is the declaration, not an inference**: two teams playing, each with the faction it plays
  and the side it is on, and the two slots that are *not* in this match named as such. It is printed
  from `MatchRoster`, which is the one place the victory check, the AI, the interface and the client's
  palette and labels all read;
- **`sides` reads `0+1 hostile`** — teams 0 and 1, which are allied in every other match this game
  ships, and which a predicate over team numbers could never have put at war. There is exactly one
  entry because there is exactly one pair in the match;
- **`outcome` is the rule itself**: *the player's side has no enemies left*, asked of the sides the
  match declares. It reads `ongoing` while both teams hold structures and stops reading `ongoing` the
  moment the enemy side has none — see the second transcript;
- **the war is real rather than declared**: fire in both directions, health lost on both teams, and
  units destroyed on team 1, in a match where the only thing that changed is who the roster says is
  playing. The Δυτικοί hold nothing, and are never asked about.

`tools/probe/two-faction-decided.probe`, run with `--rivals --victory-demo` — the fixture that knocks
out every structure the player does not own, so a match of 170 units against 170 is decided within a
second instead of being a war of attrition a transcript cannot sit through:

```
cmd: teams
query:   team 1 chinese  166 alive, 0 structures
query:   outcome    ongoing; still holding structures: team 0
cmd: tick 40
cmd: teams
query:   outcome    victory — the player's side is the last one holding structures; still holding structures: team 0
check: PASS 'the enemy side still owns a structure' — 0 is within 0 of 0
```

The enemy still has 166 units alive and the match is over, because a side is out when it has no
structures left to rebuild from. The Δυτικοί are not in the match, so nothing about them is required —
and the rule that used to decide this match on its *first* check, `HasStructures(0) || HasStructures(1)`
against `HasStructures(2)`, in which a team that is not on the map has no structures and a missing
enemy reads as a dead one, is what these two transcripts are here to show the absence of.

`--duel` is the other two-faction match: Σοβιετικοί against Δυτικοί with no ally at all, which is the
one where the interface has a licence panel and nobody to offer it to.

Those two frames are worth a look: `artifacts/probe/two-faction-panel.png` is the status panel of a
two-faction match, with a row for Σοβιετικοί and a row for Κινέζοι and no third row for a faction that
is not on the map, and `artifacts/probe/two-faction-victory.png` is the banner the verdict raises.

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
