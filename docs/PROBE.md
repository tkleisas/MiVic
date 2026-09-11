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
there: see `tools/probe/alliance-demo.probe`. Two more exist for armour: `--armour-demo` lays three
headquarters of one role thirty metres apart, one per power, each with the same gun beside it — the
scene no match can contain, because a match is *between* powers and this is a demonstration *across*
them — and `--mud-demo` stands a Σοβιετικοί, a Δυτικοί and a Κινέζοι tank at the near end of one
lane each on ground that is grass until a script calls the weather down on it. See
`tools/probe/armour.probe` and `tools/probe/mud.probe` below.

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
| `unit <slot>` | the same in detail: move goal and distance to go, path state, what it is attacking and from how far, cooldown, morale, distance travelled, construction, whether the client is drawing it — and **what it is made of and what the ground under it is doing to it**, which are the two numbers that decide how a fight and a march go |
| `routes` | the map's routing state: how many units hold a route, how many are waiting for one and how many of those are holding a move goal, the longest any unit has waited with a live goal since the script started, and a line for each unit that is waiting now — **the command for "is anything left stalled"**, and it records a check that fails the run when a unit has waited longer than a route queue can explain |
| `count <kind>` | how many of a role are alive, per faction |
| `armour <kind> <x> <z> [damage]` | the composition rule for one role at one cell, per faction: the ground's cover, then the role's own armour times the owner's, and the damage a hit of that size is left with — the table the `events` stream is checked against |
| `bridge <x> <z> [team] [build]` | whether a crossing at that cell would be accepted, the reason when it would not, the span it would cover, what it costs and how long the work takes — and with `build`, the order that starts it |
| `bridges` | every crossing on the map: the cells it spans, how much of it the work has reached, how many of its blocks still stand, whether it has been cut, and the tick it will be whole on |
| `structure <kind> <x> <z> [team] [build]` | whether a structure could be raised at that cell, the reason when it could not, the cell it would stand on, what it costs and how long it takes to rise — and with `build`, the order that raises it |
| `sites <kind> <x> <z> [radius m] [team]` | a census of the placement rule around a point: how many cells on the map would take that building, how many the ground refuses and for which reasons, how many the buildings already standing there refuse, and the nearest cell that would take it |
| `structures [team]` | every structure a team has: its role, the cell it stands on, how much of it is up, and its hit points |
| `block <x> <z>` | what deck stands on one cell: how much is left of it, which team owns it, which way it runs — including whether it is a **junction**, which is a fact about the cell rather than about any crossing — and which crossings pass through it |
| `blast <x> <z> [radius] [damage] [team]` | drops a blast on the ground, as a salvo or a strike does, and reports how many blocks of deck it knocked out and what is left of the one at the centre |
| `range <slot>` | the sensor chain for one entity: the weapon's range, its own eyes, the radius those eyes find a hidden enemy at, whether a powered radar is covering it, and **the furthest it can engage anything at** — which is the smaller of the first two until a radar changes the answer |
| `power [team]` | one team's power ledger: energy generated, energy drawn by the structures that are on, the surplus, how many radars are lit and how many the grid had to shed, and the reason the interface gives for a brown-out, in Greek |
| `capacity [team]` | one team's command capacity: what its finished structures support and what each of them is worth, what its live units cost against it per role, how far over it is, and the words the refusal uses — the ceiling and the army that spends it, from the same functions the production gate asks |
| `detect <team> <slot>` | whether one team can see one entity, and by which channel: hidden, the cell's sight, the cell's detection, and whether the target has revealed itself by firing |
| `exposure <team> <x> <z>` | what one team knows about a point on the ground: whether a powered radar covers it, whether the cell is visible, and whether it is detected — the query to walk a boundary across one reading at a time |

### The mission's script

A mission can now say *when* something happens — see `docs/ROADMAP.md` §8 and
`Campaign/TriggerSystem` — and a script is the one thing in a mission that cannot be read off the
world it produced: after the fact, a spawned force looks like a force and a revealed ridge looks
like ground somebody walked over. These three commands answer it.

| Command | Answer |
|---|---|
| `triggers` | the mission's whole script: every trigger in the order the simulation evaluates it, what it waits for, what it does, whether it has fired and **on which tick** — then the flags it has raised, and a check of the mission's own integrity, which fails the run when a trigger waits on something that can never happen |
| `messages` | what the mission has shown the player, oldest first, with the tick and how long ago |
| `objectives` | every objective the mission is judged by: kind, status, primary or bonus, the progress behind it, and the numbers it is asking — the same state the state hash folds in |

`triggers` answers "did the second act of this mission happen at all", which is the failure a mission
is most likely to ship: a trigger that exists and never fires. A transcript can read for forty lines
without noticing one, so the command records a check of its own — *the mission's script can fire* —
and prints the tick each trigger fired on, because a trigger that fired forty seconds late is a
mission whose pacing is somewhere other than where its author put it.

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

`order` and `ability` are the fourth and fifth, and they are the two orders a player gives most: a
move, an attack, and a call for off-map support. Both go through the simulation's own command queue,
which is the same queue a click writes to and the same one a replay records, so a column a script
marches across a bog is a column a player marched rather than one a fixture placed and nudged. They
exist for the same reason the other three do — a distance cannot be inspected until something covers
it, and a surface cannot be inspected at the place that matters until somebody chooses the place —
and they are why `--mud-demo` can be a demonstration of the weather ability rather than a fixture
with mud already in it.

| Command | What it does |
|---|---|
| `order <slot> move <x> <z>` | issues a move order for one unit, executing on the next tick |
| `order <slot> attack <slot>` | issues an attack order, and says whether the two are hostile — an order that is not is thrown away rather than obeyed |
| `queue <slot> <role>` | puts a role on a building's production pad through the simulation's own command queue, and prints the verdict the player would be given — accepted with its cost and its time on the pad, or refused in the same words the build panel uses beside a greyed-out row (`λείπει δυναμικότητα 174`, `λείπουν 120 Π`, `όριο 2`) |
| `ability <name> <x> <z> [team]` | calls in an off-map ability at a point: what it costs, what it does, and the world's own verdict, in the words the player would be shown when it is refused |

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
query:   model    Generated/soviet_tank.glb, scale 0.837 x 0.837 x 0.837, 54 parts, wheel radius 0.4 m, filtered to 'turret'
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
forever. Two things had to meet for that: something has to hand out a goal on ground nothing
can stand on, and a route that never arrives has to leave a unit waiting rather than drop the
order. The first of those was the wander orders, which picked a destination without asking
whether anything could stand there — they now ask the navigation grid and the surface the same
question the pathfinder asks, and refuse the wish when the answer is no.

**The second was then measured, and it was not what this paragraph used to say.** It claimed a
600-tick skirmish left 35 units in that state, all of them holding a goal an attack order had set
at a target on lava or at a building nothing could occupy. The census says otherwise. Asking the
question of every slot in the match — `unit <slot>` for 0..530, which is `routes` below said the
slow way — at tick 600 gives **206** units, not 35, and the goal of nearly all of them is
ordinary grass: the Δυτικοί Γραφείο Σχεδιασμού at (x 40.3, z 147.1) m below stands on ground
anything can cross, and *nothing in this engine makes a building's own cell impassable* — there
is no unit collision at all, so a building is no harder to reach than a field. What those units
were waiting for was not an impossible goal but the route budget: the approach loop re-asked for
a route every ten ticks for every ordered attacker that was out of reach, which threw away a
route that was still good to ask for it, and the four searches a tick were spent from slot zero,
so the highest slot ever served in six hundred ticks was 386. 96 % of requests were starved.
Nothing was failing, so nothing said anything: they read as `waiting for a route`, which is what
a unit that has just been given an order reads as too.

Three fixes, all in `MiVic.Core`:

- **an ordered point the mover cannot enter is clipped *and the goal rewritten to it*.** The route
  was always planned to the nearest cell the mover can enter; the goal was not, so the arrival
  test and the route answered about two different places and the order could never be completed.
  This is the `0.0 m to go, waiting for a route` reading — the goal is the cell the unit is
  standing on and the unit is still asking for a route to it;
- **an attack order is only marched at where the unit could stand**, asked again every ten ticks
  because a target can walk onto water after the order is given. The order is kept, the unit
  stands where it is, and it fires when the target comes into reach;
- **a route in hand is not thrown away.** The approach loop re-plans only when the target has left
  the cell the route already leads to, and the budget's scan starts where the previous tick left
  off, so a fixed budget is a queue rather than a race.

Measured after: the same census, at the same tick, on the same seed, is **0**.
`UnreachableGoalTests.TheStandardSkirmishLeavesNoUnitWaitingForARoute` asks the same question of
the same match from the tests, and `routes` asks it inside a running client — see the next
section. That is what this query is for: a screenshot of a tank standing on mud says nothing at
all.

## Worked example: is anything left stalled?

`tools/probe/stall.probe`, run against the default skirmish and trimmed to the two answers:

```
cmd: tick 600
cmd: unit 259
query: unit 259 chinese/Tank …
query:   move       goal (x 220.3, z 135.9) m, 186.8 m to go, path 2 cells at 0, 0 failures
query:   travel     78.9 m covered at 8.0 m per second (0.4 m per tick)
cmd: routes
query: routes: 495 entities alive, 275 units holding a route, 0 units waiting for one (0 of them with a move goal), 0 requests outstanding
query:   worst      slot 494 waited 59 ticks (3.0 s) with a live move goal, seen on tick 554 — a route queue drains at 4 searches a tick
check: PASS 'no unit is left waiting for a route' — the longest wait with a live move goal is 59 ticks (3.0 s)
```

The same unit at the same tick, against the build before the fix — the reading the bug was found
in, and the one this file used to quote as a goal on deep water:

```
cmd: unit 259
query:   move       goal (x 40.3, z 147.1) m, 205.1 m to go, path 0 cells at 0, waiting for a route, 0 failures
query:   attack     slot 343 (western/DesignBureau at 205.1 m), cooldown 2 ticks of 24, order explicit, 35 damage out to 110.0 m
query:   travel     58.2 m covered at 8.0 m per second (0.4 m per tick)
```

Two hundred and five metres to go, no route, no failures, an explicit attack order on a building,
and fifty-eight metres covered in six hundred ticks. Four hundred ticks later the same unit reads
`92.8 m to go, path 3 cells at 1` — so it is not frozen, it is *starved*, which is exactly why
nothing reported a fault: a unit waiting its turn and a unit waiting forever are the same line
until somebody counts the ticks. After the fix the same unit at the same tick is walking a route
of its own (186.8 m to go, 78.9 m covered, 238.5 m covered by tick 1000 and 46.6 m from its
goal). Its goal is a *different* goal, because the match is a different match: two hundred units
that were standing still are fighting in this one.

`routes` is the whole-map half of the same question, and the reason it can answer it is that a
single reading cannot: a route queue drains at four searches a tick, so a unit waiting nine ticks
is a unit the world is serving and a unit waiting nine hundred is a unit it has forgotten. The
streak is kept per slot on every tick a script runs, the census prints who is waiting now with
the goal they are not reaching, and the check it records is what fails the run. The two runs above
are `artifacts/probe/stall-before.txt` and `artifacts/probe/stall.txt`.

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
query:   health     607/1400 (43%)
query:   attack     slot 346 (western/Tank at 136.8 m), cooldown 36 ticks of 50, order automatic, 45 damage out to 200.0 m
cmd: unit 513
query:   attack     slot 348 (western/AntiAir at 175.3 m), cooldown 25 ticks of 50, order automatic, 45 damage out to 200.0 m
cmd: power 1
query:   generation 46 Ε per tick, from the structures standing
query:   draw       11 Ε per tick, including the radars that are on
query:   surplus    35 Ε per tick
query:   radars     1 lit, 0 dark, 0 Ε short of running them all
query:   brown-out  none
cmd: events 12
query:   #256 tick 562 hit       slot  510 chinese/GunEmplacement team 1 at (42.2, 18.1, 4.7) m took 12 damage
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
- **`reach 200.0 m` against `eyes 170.0 m`** is the radar being worth its price, and `unit 513` is the
  proof of what that is for: the gun is engaging an anti-aircraft mount **175.3 m away**,
  `order automatic`, which is past the 170 m it could see without one. The kill `unit 510` is in the
  middle of — 607 of 1400 hit points — is a fight it would not have been in at all;
- **`generation 46` against `draw 11`, `1 lit, 0 dark, brown-out none`** is the failure mode this
  feature can create, answered — and this number has moved, for a reason worth reading: an AI that
  raises a dish its grid cannot run has paid for a building that switches itself off, and detection is
  what the ledger sheds first. Team 1 is over its command ceiling at the opening — see
  `capacity.probe` — so it has spent the same three hundred ticks on the generation and the yards that
  lift the ceiling, and three power plants and a second yard are inside the 46. The dish is still on
  the air, the ledger is solvent by 35 a tick, and the line was bought anyway;
- **the hits in `events`** are the same emplacements, taking and giving fire with nobody having
  ordered either, on ground that is `540 ‰` and `563 ‰` cover — a defensive line that fights rather
  than one that stands.

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
query:   model    Generated/soviet_tank.glb, scale 0.837 x 0.837 x 0.837, 54 parts, wheel radius 0.4 m, filtered to 'turret'
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
query:   model    Generated/soviet_hq.glb, scale 0.526 x 0.526 x 0.526, 35 parts, wheel radius none — this model has no wheel_ part, filtered to 'radar'
query:   entity   at (-180.0, 0.0, -180.0) m, heading 0.0°, which the entity transform turns into (1.00, 0.00, 0.00) bearing 0.0°
query:   turret   the turret part points 0.0° off the hull (world bearing 0.0°, hull 0.0°); the animator's own TurretYaw is 0.000 rad (0.0°) in the model's frame
query:   note     local = as the model declares it, motion = what the animator applied, world = the chain composed; nose is the image of the model's own -Z, which is the front every generated model is authored with
query:   [21] 'radar' parent -  local T(-1.00, 31.30, 3.00) m X(1.00, 0.00, 0.00) Y(0.00, 1.00, 0.00) Z(0.00, 0.00, 1.00)  motion rotates about (0.00, 1.00, 0.00) by -45.8°; turned -45.8° about (0.00, 1.00, 0.00) over 40 ticks = -1.1°/tick  bounds max (2.9, 3.5, 1.8) m  world T(-181.58, 16.47, -180.53) m X(0.38, 0.00, 0.37) Y(0.00, 0.53, 0.00) Z(-0.37, 0.00, 0.38)  nose (0.70, 0.00, -0.72) bearing -45.8°
query:   1 part listed of 35
```

A model has tens of parts and most of them never move, so `parts <slot>` prints one line per
part and `parts <slot> <name>` narrows it to the ones a name contains. The counts at the end
say how much was shown and how much there was.

## Worked example: does the same weapon do less to a Σοβιετικοί building than to a Κινέζοι one?

Armour is damage reduction per hit and not a bigger health pool, so the claim is not "the Soviet
building survives longer" — it is "the same 45-damage shell arrives as a different number", and a
health bar cannot show that on its own. It also cannot be shown inside a match: a match is between
powers, and this is a statement *across* them, so `--armour-demo` lays three headquarters of one role
thirty metres apart — Σοβιετικοί, Κινέζοι, Δυτικοί — with the same Σοβιετικοί Πυροβολείο
thirty-five metres east of each, on ground levelled to one surface with no canopy and no landform.
`tools/probe/armour.probe` against it, trimmed to the answers (`…` marks lines cut out):

```
cmd: attributes 4 -184
query:   surface    Sand (Άμμος), churn 0/255
query:   ground     vegetation 0/255, moisture 12/15, aspect south (2), landform plain (0), fuel 0, flags none
query:   cover      foot 1000 ‰, tracked 1000 ‰, wheeled 1000 ‰, air 1000 ‰   (damage that lands: 1000 ‰ is no cover at all)
cmd: tick 60
cmd: events 6
query:   #4 tick 52 shot      slot  504 soviet/GunEmplacement team 2 at (39.0, 20.6, -124.0) m firing at western/CommandCentre (slot 505, team 3), direction (-1.00, 0.03, 0.00) bearing 180.0°, 35.0 m away
query:   #5 tick 52 hit       slot  505 western/CommandCentre team 3 at (4.0, 21.8, -124.0) m took 32 damage
query:   #6 tick 52 shot      slot  506 soviet/GunEmplacement team 2 at (39.0, 16.1, -154.0) m firing at chinese/CommandCentre (slot 507, team 3), direction (-0.99, -0.14, 0.00) bearing 180.0°, 35.0 m away
query:   #7 tick 52 hit       slot  507 chinese/CommandCentre team 3 at (4.0, 11.2, -154.0) m took 35 damage
query:   #8 tick 52 shot      slot  508 soviet/GunEmplacement team 2 at (39.0, 9.8, -184.0) m firing at soviet/CommandCentre (slot 509, team 3), direction (-1.00, -0.07, 0.00) bearing 180.0°, 35.0 m away
query:   #9 tick 52 hit       slot  509 soviet/CommandCentre team 3 at (4.0, 7.3, -184.0) m took 27 damage
…
cmd: unit 509
query:   health     4838/5000 (96%)
query:   armour     structure — role 850 ‰ × faction 720 ‰ = 612 ‰, so a 45-damage hit here lands as 27
…
cmd: armour CommandCentre 4 -184 45
query:   rule       damage × cover ÷ 1000 × armour ÷ 1000, floored at 1 — DamageRules.Compose, the one place a hit becomes damage
query:   cover      1000 ‰ for foot — what the ground lets through, and 1000 is ground that hides nobody
query:   Σοβιετικοί   role 850 ‰ × faction 720 ‰ = 612 ‰ → 27 damage, 18 turned away
query:   Κινέζοι      role 850 ‰ × faction 930 ‰ = 790 ‰ → 35 damage, 10 turned away
query:   Δυτικοί      role 850 ‰ × faction 840 ‰ = 714 ‰ → 32 damage, 13 turned away
…
cmd: armour Tank 4 -154 95
query:   cover      1000 ‰ for tracked — what the ground lets through, and 1000 is ground that hides nobody
query:   Σοβιετικοί   role 1000 ‰ × faction 970 ‰ = 970 ‰ → 92 damage, 3 turned away
query:   Κινέζοι      role 1000 ‰ × faction 960 ‰ = 960 ‰ → 91 damage, 4 turned away
query:   Δυτικοί      role 1000 ‰ × faction 850 ‰ = 850 ‰ → 80 damage, 15 turned away
```

Four facts, and three of them are the same fact from different sides:

- **the same gun, the same shell, the same building, three owners — 27, 32, 35.** All three
  headquarters stand on cells whose own `attributes` line reads `cover foot 1000 ‰`, so the ground
  lets every point of every hit through and the difference between the three numbers is the owner
  and nothing else. The events stream and the rule agree exactly, which is what makes the second a
  proof of the first rather than a restatement of it;
- **`4838/5000` after six shells, against `4790/5000` for the Κινέζοι one** — 162 points of damage
  against 210, aimed by the same guns in the same window. Armour is not more hit points: both
  buildings have five thousand, and one of them has heard fewer of them;
- **`Σοβιετικοί` 612 is a role figure times a faction figure**, 850 for being a command centre and
  720 for being Soviet concrete. The two multiply because they answer different questions — a
  reactor is not a shed, and a Σοβιετικοί shed is not a Κινέζοι one — and neither alone would say
  what the transcript shows;
- **the vehicle table runs the other way, on the same rule.** At a Κατιούσα's 95-damage shell a
  Σοβιετικοί hull keeps 92 and a Δυτικοί one 80, with the Κινέζοι a point behind at 91: heavy where
  it does not move, light where it does. What the light hull buys is *speed in mud*, and that is the
  next transcript.

## Worked example: is the rasputitsa a Σοβιετικοί advantage?

Ground pressure has been a per-faction figure for a long time and mud has always been a surface with
a movement cost; what was missing was that nothing on the *movement* path read that cost, so the
pressure steered a route and did not set a speed. The fix is not a new number, so the demonstration
has to be a measurement rather than a table: `--mud-demo` stands a Σοβιετικοί, a Δυτικοί and a
Κινέζοι tank at the near end of one lane each on ground it has levelled to grass, and the mud is
called down by the *script*, through the world's own ability path, after the script has shown the
ground without it. `tools/probe/mud.probe` against it, trimmed to the answers:

```
cmd: units
query:   slot  506 soviet DesignBureau     team 0 at (x 99.0, z -201.0) m heading 0.0° health 1500/1500 target - building yes
query:   slot  507 chinese Tank             team 3 at (x -23.0, z -257.0) m heading 0.0° health 320/320 target - building no
query:   slot  508 western Tank             team 3 at (x -51.0, z -257.0) m heading 0.0° health 320/320 target - building no
query:   slot  509 soviet Tank             team 3 at (x -79.0, z -257.0) m heading 0.0° health 320/320 target - building no
cmd: attributes -51 -237
query:   surface    Grass (Γρασίδι), churn 0/255
query:   going      foot 100 ‰, tracked 100 ‰, wheeled 100 ‰, air 100 ‰   (flat ground is 100 ‰)
cmd: ability WeatherControl -51 -237 0
query: ability Έλεγχος Καιρού at (x -51, z -237) m for team 0
query:   costs      500 Π, ready again 1800 ticks (90.0 s) after it lands
query:   arrives    0 damage inside 70.0 m, and 1200 ticks (60.0 s) of mud
ok: Έλεγχος Καιρού called down at (x -51, z -237) m for team 0, executing on tick 1
cmd: attributes -51 -237
query:   surface    Mud (Λάσπη), churn 0/255
query:   going      foot 200 ‰, tracked 250 ‰, wheeled 300 ‰, air 100 ‰   (flat ground is 100 ‰)
cmd: order 509 move -79 -182
ok: Tank (Άρμα) at slot 509 ordered to move, executing on tick 2 — `tick 1` issues it and `unit 509` reads what came of it
cmd: order 508 move -51 -182
ok: Tank (Άρμα) at slot 508 ordered to move, executing on tick 2 …
cmd: order 507 move -23 -182
ok: Tank (Άρμα) at slot 507 ordered to move, executing on tick 2 …
cmd: tick 200
cmd: unit 509
query:   armour     vehicle — role 1000 ‰ × faction 970 ‰ = 970 ‰, so a 45-damage hit here lands as 43
query:   ground     Mud (Λάσπη), tracked at 750 ‰ pressure — costs 216 ‰ of the baseline, so 184 of its 400 mm per tick (462 ‰ of its speed)
query:   travel     38.7 m covered at 3.7 m per second (0.2 m per tick), against 8.0 m per second (0.4 m per tick) on clear ground
cmd: unit 508
query:   armour     vehicle — role 1000 ‰ × faction 850 ‰ = 850 ‰, so a 45-damage hit here lands as 37
query:   ground     Mud (Λάσπη), tracked at 1100 ‰ pressure — costs 281 ‰ of the baseline, so 142 of its 400 mm per tick (355 ‰ of its speed)
query:   travel     23.2 m covered at 2.8 m per second (0.1 m per tick), against 8.0 m per second (0.4 m per tick) on clear ground
cmd: unit 507
query:   armour     vehicle — role 1000 ‰ × faction 960 ‰ = 960 ‰, so a 45-damage hit here lands as 37
query:   ground     Mud (Λάσπη), tracked at 1250 ‰ pressure — costs 460 ‰ of the baseline, so 86 of its 400 mm per tick (217 ‰ of its speed)
query:   travel     20.3 m covered at 1.7 m per second (0.1 m per tick), against 8.0 m per second (0.4 m per tick) on clear ground
```

Four facts, and the first is why the fixture exists at all:

- **the mud is the ability's doing.** The same cell reads `Grass (Γρασίδι)` with `going tracked
  100 ‰`, then `Mud (Λάσπη)` with `going tracked 250 ‰`, and the only thing that happened in between
  is the `ability` line. A demonstration with mud already in the fixture would be showing the
  fixture;
- **three identical tanks, one distance, three answers: 38.7 m, 23.2 m, 20.3 m in ten seconds.**
  All three are the same role at the same 400 mm per tick, ordered the same distance in the same
  straight line by the same script, so nothing about the order or the destination favours anybody;
- **the ground line is the whole arithmetic and it is checkable.** `tracked at 750 ‰ pressure — costs
  216 ‰ of the baseline, so 184 of its 400 mm per tick (462 ‰ of its speed)`: 250 for mud times 0.75
  for a Σοβιετικοί hull is 187, the extra 29 is the churn the column has already laid under itself,
  and the speed is the baseline over the cost. The Δυτικοί column pays 281 and the Κινέζοι 460, which
  is the entire reason their columns are 15 and 18 m behind;
- **`travel` now prints two speeds**, the one it is making and the one its catalogue figure says,
  because a number that said `at 8.0 m per second` beside `38.7 m covered` over ten seconds would be
  the sort of transcript a reader stops trusting.

And the ledger is what makes it a test rather than a picture: the run fails if the Σοβιετικοί column
stops out-distancing the Δυτικοί one, if the heaviest school stops being the slowest, or if the
weather strike is refused.

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
- **No reactive orders.** A probe can march a unit and call down an ability — `order` and
  `ability` — but it cannot decide *from an answer* to do either, because there is no "if" in
  the language. A script that needs that is two scripts, or a script with the numbers written
  in as expected values. It also cannot place a unit, which is what the fixtures are for.
- **No `MiVic.Core` knowledge of its own.** The probe is a client tool: it reads the
  simulation and the renderer and changes neither. Five commands enqueue anything —
  `bridge … build`, `structure … build`, `blast`, `order` and `ability` — and every one of them
  enqueues the command a click or a button would, which is why they go through the world's own
  queue and validation rather than writing to the state directly.
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

## Worked example: what does a side over its command capacity get refused?

Every side opens a standard match with 166 units and 446 places of supply against the 272, 320 or 368
that its four starting buildings support, so the question is not academic: it is the first thing a
player meets. `tools/probe/capacity.probe`, run against the default skirmish with nobody playing team
0, trimmed to the answers (`…` marks lines cut out of the middle):

```
cmd: tick 2
cmd: capacity 0
query: capacity team 0 Σοβιετικοί — 446 places fielded against 272 supported, tick 2
query:   rule       a structure grants capacity when it supports an army rather than being one, and a man is one place, a vehicle four, an aircraft six — CapacitySystem and UnitCatalog.SupplyCost
query:   ceiling    272 places from 4 structures at 850‰ of what they are worth — a building site grants nothing until it is up
query:      Κέντρο Διοίκησης         ×1    200 each =   200
query:      Σταθμός Παραγωγής        ×1     30 each =    30
query:      Εργοστάσιο               ×1     60 each =    60
query:      Γραφείο Σχεδιασμού       ×1     30 each =    30
query:   supply     446 places fielded by 166 units
query:      Πεζικό                   ×82     1 each =    82
query:      Άρμα                     ×42     4 each =   168
query:      Πυροβολικό               ×14     4 each =    56
query:      Αντιαεροπορικό           ×14     4 each =    56
query:      Αεροσκάφος               ×14     6 each =    84
query:   verdict    over the ceiling by 174 — no unit may be queued until 174 places of army are gone or built for
query:   refusal    λείπει δυναμικότητα 174
cmd: queue 0 Infantry
query:   verdict    refused — λείπει δυναμικότητα 174
query:   player     Πεζικό: λείπει δυναμικότητα 174.
cmd: structure Factory -248.4 -89.1 0 build
query:   verdict    accepted for team 0 — it would stand at (x -248.4, z -89.1) m, cell 5,22 of 65, index 1435, Sand (Άμμος)
ok: Εργοστάσιο ordered for team 0 at (x -248.4, z -89.1) m, executing on tick 3 — `tick 1` starts it and `tick 272` finishes it
cmd: tick 400
cmd: capacity 0
query: capacity team 0 Σοβιετικοί — 446 places fielded against 323 supported, tick 402
query:      Εργοστάσιο               ×2     60 each =   120
query:   verdict    over the ceiling by 123 — no unit may be queued until 123 places of army are gone or built for
…
cmd: tick 1000
cmd: capacity 1
query: capacity team 1 Κινέζοι — 231 places fielded against 540 supported, tick 1402
query:      Κέντρο Διοίκησης         ×1    200 each =   200
query:      Σταθμός Παραγωγής        ×4     30 each =   120
query:      Εργοστάσιο               ×2     60 each =   120
query:      Γραφείο Σχεδιασμού       ×1     30 each =    30
query:      Πυροβολείο               ×2      0 each =     0   — it is the army, not the thing that supports it
query:      Αντιαεροπορικό Πυροβολείο ×1      0 each =     0   — it is the army, not the thing that supports it
query:      Σταθμός Ραντάρ           ×1      0 each =     0   — it is the army, not the thing that supports it
query:   supply     231 places fielded by 109 units
query:   verdict    within the ceiling, 309 places to spare — units may be queued
query:   refusal    none
cmd: count Commissar
query: count Commissar: 0 alive (soviet 0, chinese 0, western 0)
cmd: queue 170 Commissar
query:   verdict    accepted — 42 Π, 0 Ε, 7 Ν, 33 ticks (1.7 s) on the pad
ok: Κομισάριος ordered at slot 170 for team 1, executing on tick 1403 — `tick 1` starts it and `tick 33` finishes it
cmd: tick 60
cmd: count Commissar
query: count Commissar: 1 alive (soviet 0, chinese 1, western 0)
cmd: teams
query:   team 0 soviet   171 alive, 5 structures
query:   team 1 chinese  122 alive, 12 structures
query:   team 2 western  121 alive, 3 structures
query:   damage     team 0 0 hits, 0 losses, 0 health; team 1 512 hits, 57 losses, 5778 health; team 2 302 hits, 52 losses, 11014 health — the health lost by the team that lost it
```

What the four parts of that transcript prove, in the order the feature was decided:

- **the refusal names the rule and says how far over the side is.** `λείπει δυναμικότητα 174` is the
  Σοβιετικοί's own number, and it is not about money: the team at that moment holds 2 506 Π. The
  ledger above it is the arithmetic — a man is one place, a tank four, an aeroplane six, and four
  buildings support 272 — so a player can see why they are at their limit rather than being told;
- **a structure is not refused, and that is the guard the whole rule needs.** A side over its ceiling
  that could not raise a building could never raise the building that lifts its ceiling, and a
  stalemate with nothing dying is exactly when that would happen. The yard is ordered through the
  placement rule a click is judged by, stands whole four hundred ticks later, and the ceiling it was
  refused against has gone from 272 to 323 — while the side is still over it;
- **attrition opens the gate, and the same order then goes through.** The Κινέζοι are the side whose
  army is actually being spent — 57 units lost to the Δυτικοί's 52, and 512 hits taken — and their
  supply has fallen from 446 places to 231. The order the script was refused at the top is accepted,
  and it is asked of a role the match does not otherwise contain: a Κομισάριος comes from no
  scenario and is on nobody's production preference, so a count that goes from none to one is that
  order and nothing else;
- **and the AI is doing the same thing on its own.** Team 1's ceiling did not stay at 368: four power
  plants, a second yard and its line are inside the 540, all of them bought by `AiSystem` while its
  army was over the ceiling — see `ai-line.probe`, where the extra generation shows up in a defensive
  line's power ledger.

One check in that run used to fail, and it was the tool's rather than the engine's. `teams` records an
invariant of its own — no weapon held an ally as a target, fired at one, or damaged one without a shot
to explain it — and it reported one damage event unexplained, which failed the check and left
`capacity.probe` as the one red script in a suite where everything else was green. The two numbers
beside it are what passed, and they are the ones the clause exists for: **0 ticks with an ally held as
a target, 0 shots fired at an ally.** The third clause had caught a lava burn instead, and it caught it
by asking the wrong cell. The hazard step runs before the movement step, so a unit standing in lava
when it burns and stepping off it on the same tick was reported at the cell it stood on *afterwards* —
and `IsTerrainDamage` asks the surface under the position the event carries. 6 damage a tick is exactly
`HazardSystem.LavaDamagePerTick`, and the damage lands on team 1's own column threading that field on
its way to the enemy, which is a fact about the map and not about the ceiling.

It is fixed rather than written down: a `UnitHit` event now carries the position its victim was
standing on when the damage landed, which is the position it entered the tick with, because everything
that damages — the hazard step and the combat step — runs before the movement step that moved it. A
burn in lava and a death in lava are now attributed to the same cell instead of to two different ones.
The same run reads `0 damage events unexplained` and exits 0.

## Worked example: does a mission's script actually happen?

A mission used to be a seed, a layout, a time limit and a list of objectives — enough for "destroy
this" and nothing else, because it could not say *when* anything happened (see `docs/ROADMAP.md` §8).
It can now, and the question that immediately follows is the one this project keeps having to ask:
**does the scripted event ever actually occur?** A trigger that is authored and never fires is a
feature that exists and does nothing, which is the shape of the volcano line above every cell, the
sand band below the mud line and the mud mechanic that was inert.

`tools/probe/triggers.probe`, run against the demonstration mission — a pass the Δυτικοί are
reconnoitring, an ambush waiting on the rock above it, and a road out of the valley the enemy must
not reach — and trimmed to the answers (`…` marks lines cut out of the middle):

```
cmd: triggers
query: triggers: 6 triggers in 'm4_pass', 0 fired, at tick 0
query:   #0 preparation      waiting
query:       when       tick 1 is reached (0.1 s in)
query:       then       spawn 2 of GunEmplacement (Πυροβολείο) for team 2 at (x -75.0, z 60.0) m
cmd: tick 5
cmd: triggers
query:   #0 preparation      FIRED on tick 1 (0.1 s in)
cmd: structures 2
query:   slot   64 GunEmplacement (Πυροβολείο) at (x -82.0, z 60.0) m, cell 23,38 — whole, 1400 hit points
query:   slot   65 GunEmplacement (Πυροβολείο) at (x -75.0, z 60.0) m, cell 24,38 — whole, 1400 hit points
cmd: exposure 0 -75 10
query:   sight      the cell is not visible to team 0
cmd: tick 395
cmd: triggers
query:   #1 warning          FIRED on tick 400 (20.0 s in)
cmd: messages
query:   #0 tick 400 (0.0 s ago) — Οι πρόσκοποι αναφέρουν κίνηση βόρεια του περάσματος. Ο αυχένας είναι ύποπτα ήσυχος.
cmd: exposure 0 -75 10
query:   sight      the cell is visible to team 0
cmd: exposure 0 -75 60
query:   sight      the cell is not visible to team 0
…
cmd: tick 250
cmd: triggers
query: triggers: 6 triggers in 'm4_pass', 4 fired, at tick 850
query:   #2 ambush           FIRED on tick 845 (42.3 s in)
query:   #3 counterattack    FIRED on tick 845 (42.3 s in)
query:   flags      flag 0 SET
cmd: units western 10
query:   slot   35 western Tank             team 2 at (x -143.0, z 0.0) m heading 0.0° health 264/320 target - building no
cmd: exposure 0 -75 60
query:   sight      the cell is visible to team 0
cmd: tick 450
cmd: triggers
query: triggers: 6 triggers in 'm4_pass', 6 fired, at tick 1300
query:   #4 first-gun        FIRED on tick 1090 (54.5 s in)
query:   #5 ambush-broken    FIRED on tick 1255 (62.8 s in)
cmd: structures 2
query: structures: 6 structures for team 2, tick 1300
query:   slot   30 CommandCentre (Κέντρο Διοίκησης) at (x 30.0, z 200.0) m, cell 35,53 — whole, 5000 hit points
query:   slot   50 AntiAirEmplacement (Αντιαεροπορικό Πυροβολείο) at (x 107.8, z 70.3) m, cell 43,39 — whole, 1200 hit points
cmd: objectives
query: objectives: 2 objectives in 'm4_pass', outcome ongoing, at tick 1300
query:   #0 DenyArea             pending  primary progress 0, hold 0
query:       numbers    team 0, team 2 must not get 4 units into (x -270.0, z -270.0) m within 45.0 m, by tick 3600 (180.0 s in)
query:   #1 Scripted             complete primary progress 0, hold 0
cmd: tick 2500
cmd: objectives
query: objectives: 2 objectives in 'm4_pass', outcome victory, at tick 3800
query:   #0 DenyArea             complete primary progress 0, hold 0
query:   #1 Scripted             complete primary progress 0, hold 0
check: PASS 'the mission's script can fire' — every trigger waits on something that can happen, and every scripted objective is completed by one
probe: 60 commands, 60 ok, 0 errors, 0 checks failed
```

Six facts, and each one is a different half of the layer:

- **the script is printed before it runs**, with what each trigger waits for and what it does, so a
  reader can check the mission against its own writing rather than against its consequences;
- **every trigger fires, in order, and the ticks say when**: preparation on the first tick, the
  warning on 400, the ambush and the counter-attack on the *same tick* 845, the first gun on 1090,
  the ambush broken on 1255. The two on 845 are the flag doing its work — the list's order is the
  order of the events, so a trigger can raise a flag its successor reads on the tick it fired;
- **each firing has a visible consequence, and the transcript shows it rather than asserting it**:
  two guns standing on the rock at tick 5 that were not there at tick 0, a pass that is not visible
  to team 0 and then is, a Western tank on the flank at `(x -143.0, z 0.0) m` that the spawn put
  there, an objective that reads `pending` and then `complete`;
- **the reveal is a disc and the number is the design**: at tick 400 the pass is watched and the
  rock fifty metres above it is *not*, because the warning's disc is forty-five metres across — so
  the guns stay in the fog until the ambush springs and its own, larger disc takes the whole
  crossing out of it;
- **the denial is decided by the clock and not by a predicate**: the objective the campaign could
  not express before — *the enemy must not* — reads `progress 0` throughout, because they never got
  four units to the road, and completes at its deadline. With four of them on the road it fails the
  instant they arrive, which is the same objective deciding the other way;
- **the fight is the simulation's, not the script's.** Nothing orders the guns to fire or the column
  to answer: the script marches eight units to the pass and the rest is the combat system, the
  ground, and the range of a gun on a rock. The two triggers that wait on the guns falling are the
  mission's, and they fire because the guns really did fall.

The one thing that *is* the script's is the march itself, through the same `order` command a click
issues, because the computer does not play team 0 — a probe cannot make a player, so it plays one.

