# MiVic

Ένα παιχνίδι στρατηγικής πραγματικού χρόνου (RTS) σε 3D, γραμμένο σε MonoGame,
στα ελληνικά. Σε ένα εναλλακτικό μέλλον όπου η Σοβιετική Ένωση υπάρχει ακόμη και
είναι σύμμαχος της Κίνας, ο στόχος είναι η ήττα της Δυτικής αυτοκρατορίας και η
οικοδόμηση ενός ειρηνικού, σοσιαλιστικού κόσμου.

A 3D real-time strategy game in MonoGame, with a Greek-language interface. Set in
an alternate future where the Soviet Union still exists and is allied with China;
the goal is the defeat of the Western empire.

## Factions / Παρατάξεις

| | Σοβιετικοί | Κινέζοι | Δυτικοί |
|---|---|---|---|
| Παραγωγή | χαμηλή | πολύ υψηλή | υψηλή |
| Τεχνολογία | υψηλή | χαμηλή | πολύ υψηλή |
| Συνοχή / Ηθικό | ακλόνητο | υψηλό | εύθραυστο |
| Ιδιαίτερο | Σχεδιαστικό Γραφείο | Μαζική παραγωγή | Ηθικό ανά μονάδα |

Design rationale and the alternate-history tech tree are in
[docs/DESIGN.md](docs/DESIGN.md).

## Requirements

- .NET 9 SDK (the project also builds with the .NET 10 SDK)
- Windows, Linux or macOS with an OpenGL 3.3 capable GPU
- MonoGame content tools are restored automatically by the build

## Build and run

```pwsh
dotnet build MiVic.sln
dotnet test tests/MiVic.Core.Tests          # 121 determinism and maths tests

pwsh ./tools/fetch-assets.ps1               # download the 3D models (one time)
dotnet run --project src/MiVic.Game
```

### Command line

| Option | Effect |
|---|---|
| `--seed <n>` | simulation seed (default `20250101`) |
| `--font-size <px>` | UI font size (default 17) |
| `--no-help` | hide the controls panel |
| `--screenshot <file>` | render one frame to PNG and exit |
| `--screenshot-zoom/-yaw/-pitch/-target-x/-target-z` | camera overrides for verification shots |
| `--font-sample` | draw a large SpriteFont sample, for font diagnostics |
| `--selftest [frames]` | measure performance, write `selftest-report.txt`, exit |
| `--inspect-models` | headless model diagnostics, no GPU required |
| `--inspect-ui` | headless ImGui draw-data and font-atlas diagnostics |
| `--model-gallery <file>` | render every model with axis markers, for orientation checks |
| `--select-hq` | select the player's command centre at start |
| `--victory-demo` | knock out the rival structures so the victory banner appears |
| `--record <file>` | log every external command and save the match as a replay |
| `--replay <file>` | verify a replay headlessly and exit (0 = the match was reproduced) |
| `--watch <file>` | play a recorded match back in the client |
| `--mission <id>` | start a campaign mission |
| `--mission-list` | list the campaign |
| `--particle-demo` | spawn a row of explosions, for screenshots |
| `--render-audio <dir>` | export one WAV per faction theme and exit |
| `--render-sfx <dir>` | export one WAV per sound effect and exit |
| `--no-audio` | no music or sound effects |
| `--help` | usage |

### Controls / Χειριστήρια

| Key | Action |
|---|---|
| `WASD` / arrows | pan |
| mouse wheel | zoom |
| `Q` / `E`, middle-drag | rotate |
| `Shift` + middle-drag | pan |
| left click | select a unit |
| left drag | box-select units |
| double click | select every unit of that kind on screen |
| `Shift` + click/drag | add to the selection |
| `Ctrl` + `1`…`9` | store the selection in a control group |
| `1`…`9` | recall a control group |
| right click | order selected units to move or attack |
| `F1` | toggle help panel |
| `M` | mute the music and effects |
| `Esc` | quit |

### Building / Παραγωγή

Production lives behind a selection: click one of your own buildings and the
**Παραγωγή** panel appears in the bottom-left corner with a button per unit that
building can make. Costs (`Π` materials, `Ε` energy) and build time in seconds are
on each button; a button is greyed out when the unit is locked behind a higher
tech tier or you cannot afford it. Jobs are queued, and the panel shows the queue
with the ticks left on the current item.

| Building | Produces |
|---|---|
| Κέντρο διοίκησης | πεζικό, and the structures |
| Εργοστάσιο | tanks, artillery, anti-air, aircraft |
| Σχεδιαστικό γραφείο | research projects that raise the tech tier |

With a design bureau selected, the same panel lists the research projects, and a
**Παραχώρηση άδειας** section hands one of your designs to the Κινέζοι — the
alliance's whole point, since a licensed unit bypasses their tech ceiling. When
nothing is selected the panel shows a short hint instead of disappearing.

## Project layout

| Project | Purpose |
|---|---|
| `src/MiVic.Core` | Deterministic simulation. **No graphics dependencies, no floating point.** |
| `src/MiVic.Audio` | Procedural chip music: scales, sequencing and synthesis. |
| `src/MiVic.Game` | MonoGame client: rendering, camera, ImGui UI, model import, particles. |
| `tests/MiVic.Core.Tests` | Determinism, fixed-point maths, RNG, pathfinding, campaign and replay tests. |
| `tests/MiVic.Audio.Tests` | Music generation, scales and WAV encoding. |

## Architecture rules

1. **The simulation is deterministic.** Integer millimetres for positions, Q16.16
   fixed point elsewhere, a fixed 20 Hz tick, and a PCG32 generator. No `float`,
   no `double`, no `System.Random`, no dictionary iteration in `MiVic.Core`.
2. **The client owns no game state.** It reads the simulation, interpolates
   between ticks for smooth motion, and submits instanced draws. Rendering can
   never change the outcome of a tick.
3. **Determinism is enforced by tests, not convention.** `DeterminismTests`
   contains golden hashes of a fixed scenario; if a system change alters the
   simulation, that test fails loudly.

## Status

Milestones **M0–M7 complete**: deterministic simulation core, heightmap terrain,
slope-aware A\* pathfinding, full-3D free camera, instanced rendering, Greek ImGui
UI, a glTF model pipeline, unit selection with move and attack orders, an economy
with the three factions' production systems, combat with the morale system,
licence production between allies, a deterministic AI opponent, per-team fog of
war, the victory and defeat conditions with a Greek end-of-match banner, recorded
replays that reproduce a match exactly, a three-mission campaign, a particle
system, and procedurally generated faction music.

| Metric | Value |
|---|---|
| Entities | 510 (3 factions × 167 units + 9 structures) |
| Terrain | 129 × 129 samples over 600 m, 42 m relief |
| Navigation | 65 × 65 cells, slope-costed |
| Instanced draw calls | 12 with fog hiding the enemy half of the map |
| Frame time | ~3.2 ms average (worst frame 20–40 ms, always an early simulation tick) |
| Models imported | 23 |
| Pick round-trip | 170/170 |
| Tests | 272 passing (255 core, 17 audio) |

### Performance

Vision stamping is staggered across ticks (each unit contributes once every ten
ticks) and the A\* inner loop avoids integer divisions entirely; together those
took the worst visibility sample from 28 ms to under 8 ms and the average frame
to 3.2 ms. A 200-tick run allocates 6 MB and triggers three gen-0 collections, so
the residual outliers are the operating system pre-empting the process rather
than the simulation or the collector. The self-test reports both the per-system
profile and the garbage-collection counters, so the next regression is visible in
a build log. See
[docs/DESIGN.md](docs/DESIGN.md#8-performance-what-a-hitch-actually-costs).

### The campaign

Three missions, each exercising a different objective kind, with Greek briefings
and a live objectives panel (`--mission-list` prints them):

| Mission | Objective |
|---|---|
| `m1_bridgehead` — Το Προγεφύρωμα | destroy the Western command centre |
| `m2_ridge` — Η Κορυφογραμμή | hold the centre with six units for thirty seconds |
| `m3_industry` — Η Βιομηχανία της Νίκης | reach tech tier 3 and stockpile 4000 materials, keeping your HQ alive |

Objectives are declarative predicates evaluated twice a second, not scripted
callbacks, so a mission is as deterministic and replayable as a skirmish. A
mission replaces the last-team-standing rule: the objectives decide the outcome.

### Particles

Explosions, smoke plumes and dust are CPU-simulated billboards drawn in two
instanced passes — additive for sparks, straight alpha for smoke and debris.
Deaths are reported by the simulation as events and turned into effects by the
client, which is strictly one-way: particles can never affect a tick, and an
enemy dying out of sight spawns nothing. Structures smoke in proportion to the
damage they have taken. `--particle-demo` fires a row of explosions of increasing
size for a screenshot.

### Music and sound effects

The soundtrack is generated, not recorded: no audio files ship with the game.
Each faction is written in its own musical tradition — the Σοβιετικοί get a
minor-key march with a harmonic-minor cadence, the Κινέζοι an anhemitonic
pentatonic song with grace notes, and the Δυτικοί a twelve-bar blues shuffle with
a boogie bass, backbeat and slightly detuned sawtooths. The generator is
deterministic, so a theme is reproducible and testable, and `--render-audio <dir>`
exports one WAV per faction for listening.

Combat audio comes from the same chip: rifle cracks, tank reports, artillery
launches, anti-air bursts, explosions, armour impacts, an alarm and a looping
engine, all rendered from noise and oscillators by `SoundBank` and exported with
`--render-sfx <dir>`. The client plays them at the position of the event, panned
by where it sits relative to the camera and attenuated by distance, with a budget
of six effects per frame so a five-hundred-unit battle cannot flood the mixer.
Impacts are detected by diffing entity health — the only reliable signal that a
shot landed, since a unit's cooldown also moves when it fails to find a target.
See [docs/DESIGN.md](docs/DESIGN.md#13-procedural-music).

### Fog of war

Each team has a visibility grid at navigation resolution: **visible** cells show
live enemy units, **explored** cells show remembered terrain, and everything else
is black. Enemies in cells the player cannot see are not drawn at all. The fog
itself is a terrain-shaped overlay textured by a per-team mask, not a grid of
dark tiles — see [docs/DESIGN.md](docs/DESIGN.md#11-fog-of-war-and-the-outcome-banner)
for why that distinction matters visually.

### Replays

A replay is the match's **inputs, not its state**: the seed, the scenario, the
entity capacity and every command the outside world issued, each stamped with the
tick it was issued on. Because the simulation is deterministic, replaying that
log into a fresh world reproduces the battle exactly — the file stores the state
hash it ended on, so verification is one comparison.

```
MiVic.Game.exe --record match.mvic          # play; the match is saved on exit
MiVic.Game.exe --selftest 600 --record m.mvic   # record headlessly
MiVic.Game.exe --replay match.mvic          # verify: exit code 0 means exact
MiVic.Game.exe --watch match.mvic           # watch it back
```

Commands the AI issues are deliberately **not** logged: the AI is part of the
simulation and re-derives its orders from the same state, so recording them would
apply each one twice. That keeps a 500-unit battle's replay at a few kilobytes.
`--selftest` records the live skirmish, replays it in-process and reports
`replay round-trip`, so a determinism regression fails the build rather than
showing up as a desync later. See
[docs/DESIGN.md](docs/DESIGN.md#12-replays-a-match-is-its-inputs).

### Victory and defeat

`VictorySystem` decides the match on a fixed interval: the alliance wins when the
Δυτικοί have no structures left, the Δυτικοί win when the Σοβιετικοί and Κινέζοι
are both reduced to nothing, and a mutual wipe-out is a draw. The outcome is part
of the state hash, so it is as deterministic as the rest of the simulation. The
client shows a centred Greek banner over a dimmed battlefield: **ΝΙΚΗ**, **ΗΤΤΑ**
or **ΙΣΟΠΑΛΙΑ**, with the reason underneath.

### Combat and morale

Units auto-engage enemies in range and can be ordered onto a specific target,
which they will chase. Range is horizontal; only anti-air and aircraft can engage
aircraft. Morale — the Δυτικοί weakness — falls with local force balance and
recent casualties, scales reload speed by up to ×1.5, and below 0.25 sends a unit
**routing** away from the enemy until it rallies above 0.40.

Two Σοβιετικοί roles exist purely because of that asymmetry. The **Κατιούσα** is
rocket artillery: a harder-hitting, longer-ranged salvo than a howitzer, but the
impact point scatters up to 26 m off target and everything hostile inside 22 m
takes full damage — devastating against a formation or a building, poor against one
moving tank. The **Κομισάριος** is unarmed, cheap and worth killing: it steadies
the morale of friends around it. (Its stated initiative cost is not modelled yet.)

### The alliance and the AI

The Σοβιετικοί can **licence** a design to the Κινέζοι from their build panel. A
licensed unit bypasses the Chinese tech ceiling entirely, which is how the
alliance fields hardware the Κινέζοι could never research. An AI plays teams 1
and 2: it manages power, builds what it lacks, researches, grows an army, gathers
it, and attacks once it is large enough — all through the same commands a human
issues, so it stays inside the determinism contract.

### Economy and production

| | Σοβιετικοί | Κινέζοι | Δυτικοί |
|---|---|---|---|
| Parallel production slots | 2 | 6 | 4 |
| Build speed | ×0.75 | ×1.50 | ×1.20 |
| Unit cost | ×1.35 | ×0.70 | ×1.10 |
| Research speed | ×1.25 | ×0.70 | ×1.00 |
| Tech ceiling | 4 | 2 | 5 |

Resources are **Πόροι** (materials), **Ενέργεια** (energy) and **Νερό** (water).
Command centres produce materials and water, power plants produce energy and water,
and every other structure draws energy as upkeep — a team that cannot pay its
upkeep stops producing. Every construction job and every unit costs all three, paid
when it is queued, so a queue can never be filled with resources the team does not
have.

Σοβιετικοί factories cannot build a design directly. The design bureau must first
run a **prototype** — twice the cost and twice the build time, one at a time — which
approves the design for production and delivers the prototype itself as a real
unit. Infantry is exempt, because it comes from the command centre. This is the
faction's real weakness: the army **cannot pivot late**, because a design the bureau
has not proven is not available. Research runs at the design bureau and raises the
tech tier, which is what keeps the Κινέζοι out of the aircraft tier entirely.

### Roadmap

Next: make the AI respect fog of war (it currently plans against the whole map),
then a balance pass over the branched alternate-history tech tree, and finally
campaign scripting for scripted events between missions.

## Greek text

Greek needed four separate fixes, all now verified automatically by `--selftest`
(see [docs/DESIGN.md](docs/DESIGN.md#8-greek-text--four-problems-all-solved)):

- ImGui glyph ranges must include Greek and Coptic.
- ImGui's atlas uses **straight** alpha, so it needs `BlendState.NonPremultiplied`.
  MonoGame's `AlphaBlend` is premultiplied and draws every glyph as a white box.
- The window title must be set *after* the window exists, because
  `SDL_CreateWindow` marshals its title argument as ANSI.
- The `.spritefont` needs explicit Greek character regions.

The UI font is [Noto Sans](https://fonts.google.com/noto) (SIL OFL), used both as
a runtime TTF for ImGui and, compiled through the content pipeline, as
`Content/Fonts/UiText.spritefont` for world-space labels.

## License

MiVic's source code is released under the [MIT License](LICENSE).

That covers the code, the generated documentation and any procedurally generated
art. It does **not** cover the third-party 3D models: those are fetched separately
by `tools/fetch-assets.ps1`, are not committed to this repository, and remain under
their own licence. See [src/MiVic.Game/Content/Models/README.md](src/MiVic.Game/Content/Models/README.md).
