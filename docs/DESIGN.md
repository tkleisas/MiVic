# MiVic — Σχεδιασμός / Design

Design decisions taken during the initial design pass, recorded so that the code
and the intent stay aligned. Player-facing text is Greek; this document is in
English so the reasoning is reviewable.

---

## 1. Core principle: asymmetry must be mechanical

"Advanced weapons but few units" is a stat block. If the factions differ only in
numbers, they play the same and the fantasy collapses. Each faction therefore
gets one **signature system** the others do not have, and one built-in
frustration the player must learn to live with.

The asymmetry rests on three axes:

| | Παραγωγή (throughput) | Τεχνολογία (ceiling) | Συνοχή (cohesion) |
|---|---|---|---|
| **Σοβιετικοί** | low | high | very high |
| **Κινέζοι** | very high | low | high |
| **Δυτικοί** | high | very high | low |

Each faction's win condition is another faction's failure mode: Soviets win on
quality per loss and never breaking; Chinese win by never running out; Δυτικοί
win by out-teching and out-building *if* cohesion holds.

## 2. Signature mechanics

### Σοβιετικοί — «Σχεδιαστικό Γραφείο» (Design Bureau)
Factories cannot build units directly. Units come from a design bureau that
produces a limited run of a prototype; only after ministry approval can regular
factories produce it. Consequence: army composition is decided minutes before the
battle, and **you cannot pivot**. The real Soviet weakness is inflexibility, not
slowness.

### Κινέζοι — «Μαζική Παραγωγή» (Mass Production)
Production scales with infrastructure: each factory adds parallel build slots and
builds in batches. Their tech tree is **wide and shallow** — many cheap upgrades,
no high-tier units of their own. Their answer to better technology is the
alliance (§4).

### Δυτικοί — «Ηθικό» (Morale)
The best units and the strongest economy, but morale is a per-unit, continuously
simulated value. This is the game's most distinctive system.

## 3. Morale — the Western weakness, and why greed causes it

Per-unit morale (0–100) is modified by:

- casualties among nearby friendlies (heavy weight — one bad engagement cascades)
- local force ratio (outnumbered → decay)
- being flanked or isolated from friendlies
- time under fire without returning fire
- supply and leadership auras
- faction modifiers

Effects scale smoothly: fire rate → accuracy → reaction latency → **refusal**. At
the bottom, a unit retreats or routs, and a routed unit that is not rallied is
lost.

The "greed" hook, as designed:

- **Contract units (μισθοφόροι)** — excellent stats, but a morale floor tied to
  payment. If the treasury dips, they down-tools or defects.
- **Propaganda upkeep** — a recurring drain that sets the morale baseline. Stop
  paying and the army stops wanting to fight.

Counters: Soviets get **επίτροποι (commissars)** whose aura makes morale
immovable at the cost of initiative; Chinese get **αριθμητική συνοχή**, where
morale rises with nearby friendly count, making the swarm steadier than it looks.

## 4. The alliance as a mechanic

- **Άδεια Παραγωγής (Licence Production):** the Soviets sell a design to China.
  China builds it at *Chinese* speed but at *−1 tech tier*. The Soviets get
  resources or research back.
- Player-facing tension: the Soviets dislike arming a future rival; the Chinese
  dislike needing to. A real strategic and narrative lever that gives the Chinese
  a mid-game answer to Western technology without breaking their identity.

## 5. Tech tree: the alternate-Soviet timeline

The tree reads as history, not a shopping list. Four eras, with explicit
divergence points:

| Era | Name | Content |
|---|---|---|
| I | **Διαρκής Επανάσταση** (1920s–40s) | No engineer purges; Trotskyist emphasis on mobile, exported revolution. Deep battle, partisan support. |
| II | **Ο Δρόμος προς τα Άστρα** (1950s–60s) | The space programme is never cancelled. Recon satellites → orbital fire support → «Κόκκινος Ουρανός» kinetic rods. |
| III | **Κυβερνητική** (1960s–80s) | An OGAS-style national computer network. Automated command, drones, and a **Ηλεκτροτεχνία** branch (arc weapons, EMP, power-grid warfare). |
| IV | **Κόκκινος Λογισμός** (1980s+) | AI command, weather control, particle weapons. |

Tree shape encodes identity: **narrow and deep** for the Soviets, **wide and
shallow** for the Chinese, **deep and broad but expensive** for Δυτικοί.

## 6. Vertical slice

**Scenario: 2v1.** The player commands Σοβιετικοί, an AI commands the Κινέζοι
ally, and Δυτικοί are the opponent — the slice's premise *is* the game's premise,
so narrative and mechanics validate each other from day one.

| | Σοβιετικοί | Κινέζοι | Δυτικοί |
|---|---|---|---|
| Signature | design bureau (prototype gating) | parallel slots, batch build | per-unit morale |
| Resources | Πόροι + Ενέργεια | Πόροι + Ενέργεια (cheap) | Πόροι + Ενέργεια (abundant) + morale funding |
| Ground | rugged light/medium tank, electro-artillery, AA | infantry, light tank, artillery, AA, **robots** | heavy MBT, self-propelled gun, air defence |
| Air | few, powerful + orbital strike | many, cheap, **drones** | few, excellent |
| Win | cheap capable mass + a few irreplaceable prototypes | never run out of bodies | out-tech and out-spend, if morale holds |

Scope guard: 1 map, ~5 buildings and 5 units per faction, 2 resources, 1 utility
AI, no campaign, no naval, no diplomacy. Destroy the enemy HQ to win.

## 7. Technical constraints that shaped the design

Full 3D was chosen (free camera, multi-level terrain, air units), which raises the
bar on five systems:

| System | Approach |
|---|---|
| Camera | free yaw and pitch, strategic zoom (unit icons when far out) |
| Pathfinding | heightmap grid + A\*, slope costs, impassable cliffs, partial-path fallback |
| Line of sight | coarse heightmap raycast at unit height |
| Air layer | an altitude scalar on the same entity; only AA can target it |
| Rendering | instanced draws grouped by mesh + material, LOD, single shadow map |

### Terrain and pathfinding (M1)

- **Height field.** 129 × 129 samples over 600 m, up to 42 m of relief, built
  from three octaves of integer value noise hashed from the world seed. Same
  seed, same terrain, on every machine — terrain feeds pathfinding, pathfinding
  feeds movement, and movement is in the state hash.
- **Navigation grid.** The same lattice, with a cost per cell derived from slope
  and cells above 900 ‰ marked impassable. Cliffs are obstacles, not slow ground.
- **A\*** uses integer costs, a binary heap that breaks ties on cell index, and
  fixed reusable arrays — no hash sets, no allocation, no ambiguity. An expansion
  cap returns the partial route to the closest node reached, so one pathological
  order cannot stall a tick.
- **Long routes are walked in legs.** The per-entity path buffer holds 96
  waypoints; when it runs out the unit re-searches from where it stands rather
  than following a truncated route into a wall.
- **Movement is horizontal.** Steering in 3D is a trap: a waypoint's height comes
  from the terrain lattice while a unit's height is the bilinearly sampled
  surface, so on a slope the difference reaches hundreds of millimetres. Including
  it made units climb instead of advance, and the per-tick terrain snap then undid
  the climb, freezing them just short of their waypoint. Height belongs to the
  terrain, not to the steering.

### Selection (M1)

Click and box selection project each unit's world position to screen space and
pick the nearest within 30 px. The self-test round-trips the two: project every
player unit, pick at that exact pixel, and require the same unit back. That
caught a real bug class — a broken projection would otherwise only show up as
"clicking feels wrong".

### Economy and production (M2)

The faction asymmetry is data, not special cases. Every building runs
`ProductionSlots` jobs in parallel and each job takes
`BaseBuildTicks * 1000 / BuildSpeedPermille`, so a Κινέζοι factory turns out six
cheap hulls at a time while a Δυτικοί factory works on three very expensive ones.

| | Σοβιετικοί | Κινέζοι | Δυτικοί |
|---|---|---|---|
| Parallel slots | 4 | 6 | 3 |
| Build speed | ×1.10 | ×1.50 | ×0.80 |
| Unit cost | ×0.90 | ×0.70 | ×2.20 |
| Resource income | ×1.00 | ×0.90 | ×2.50 |
| Research speed | ×1.25 | ×0.70 | ×1.00 |
| Tech ceiling | 4 | 3 | 5 |
| Ground pressure | ×0.75 | ×1.25 | ×1.10 |

The ordering is *units produced*, not wealth: per factory the Κινέζοι turn out
roughly 12.9 units' worth per cycle against 4.9 for the Σοβιετικοί and 1.1 for the
Δυτικοί. The Δυτικοί are rich rather than productive — a ×2.50 income against a
×2.20 unit cost means money is never their bottleneck, factory time is. That is why
they can field gold-plated, over-engineered equipment nobody else could afford, and
why every loss stings.

- **Resources.** Πόροι come from command centres, Ενέργεια from power plants;
  every other structure draws energy as upkeep, and a team that cannot pay its
  upkeep stops producing. Costs are taken when a unit is queued, so a queue can
  never hold resources the team does not have. **Income scales with the faction's
  wealth multiplier; upkeep does not** — a rich faction should be able to afford
  more, not run cheaper.
- **Tech gating.** Units declare a required tier. Research at the design bureau
  raises the team's tier, capped by the faction ceiling, so no amount of money can
  brute-force past it. The ceiling caps the *era*, and it is 3 for the Κινέζοι:
  they do reach the aircraft tier, which is what §6's "many, cheap" air already
  promised, but they never reach the high eras that the Σοβιετικοί (4) and
  Δυτικοί (5) climb into. Their ×0.70 research speed means those aircraft arrive
  late even so, so the swarm has a window before it exists.
- **Everything is data.** `UnitCatalog` holds base numbers and `FactionProfile`
  holds the multipliers; no system contains a faction-specific branch.
- **Production ordering is Κινέζοι > Σοβιετικοί > Δυτικοί — applied.** See the
  table above. The Σοβιετικοί are second in throughput because their designs are
  cheap and simple, not because their industry is large — a hull with few parts and
  no electronics is fast to build in any factory, which is the T-34 lesson applied
  to the factory floor rather than the mud. The Κινέζοι stay first but their units
  are individually the weakest and their tech ceiling is the lowest, so volume is
  their only lever. `IncomePermille` is a separate axis from `CostPermille`: the
  Δυτικοί are rich and unproductive, and `EconomySystem` scales income but not
  upkeep, so a rich faction can afford more rather than run cheaper.
- **Σοβιετικοί cost is two-tier, not flat — applied.** Standard hulls are cheap
  (×0.90) and the design bureau gates advanced ones behind a prototype run. The gap
  that remained — nothing stopping a player fielding twenty of an approved
  prototype — is closed by `UnitDefinition.MaxAlive`: **Ηλεκτροπυροβόλο «Τόξο»** is
  a tier-2 role that additionally requires the Ηλεκτροτεχνία project, is
  Σοβιετικοί-only, and **no team may have more than two**, counting whatever is
  already in the queue. It hits harder than anything else on the field, so it is a
  capability the faction owns rather than a unit type it can spam. What cannot be
  mass-produced is the *technology*, not the tank — and the cap is what makes that
  true in the numbers rather than only in the description.
- **Δυτικοί lose by attrition, not by being out-built in a burst**, and their
  counterplay is to avoid trading units at all. Income and throughput therefore have
  to be shown separately in the UI, or a Δυτικοί player will see a full bank and an
  idle factory and read it as a bug.
- **The Δυτικοί army is a bill, not a possession — applied.** Propaganda costs one
  material per eight armed units per tick and sets the morale baseline (+0.10
  funded, −0.20 unfunded); **Μισθοφόρος** contract infantry cost a wage per tick and
  down-tools when the treasury cannot cover it. `EconomySystem.PayUpkeep` pays wages
  first and records whether each bill was met; the flags are hashed, so a replay
  reproduces the same collapse on the same tick.
- **Tactical nukes are era IV for both superpowers, gated behind a nuclear power
  plant — decided, not built.** The Κινέζοι never get them: their ceiling is 3.
  Requiring a Πυρηνικός Σταθμός makes the weapon a strategic commitment rather than
  a button, and gives the game its first structure whose value is not its income.
  **Stealth is the Δυτικοί era-V edge.** Both need the ability system the orbital
  strike is also waiting on — see `docs/FACTION_REVIEW.md` §3.4.
- **Κινέζοι tech ceiling is 3, not 2 — applied.** This was a correction rather
  than a buff: §6 already lists Κινέζοι air as "many, cheap", but a ceiling of 2
  meant they could never build an aircraft at all. Three changes landed together:
  `FactionProfile.Chinese.TechCeiling` is 3; a new `TechId.ChineseAdvance3` project
  ("Επίπεδο 3: Αεροπορία", required tier 2, prerequisite `ChineseAdvance2`) gives
  the tree a route to tier 3, without which the ceiling change was inert; and
  `EconomyTests.TheChineseTreeHasNoThirdEra`, which asserted the opposite, was
  replaced by one that asserts tier 3 *is* reachable and tier 4 is not. The ordering
  assertion `Chinese.TechCeiling < Soviet.TechCeiling` still holds (3 &lt; 4). See
  `docs/FACTION_REVIEW.md` §2.1.

### Combat and morale (M3)

**Combat** is integer and order-independent: every unit keeps a sticky target
until it dies or leaves range, so the per-tick cost stays linear, and target
acquisition always scans slots in ascending order. Range is measured
horizontally, so a tank on a hill can still shoot into the valley and anti-air can
reach aircraft at altitude. Only anti-air and aircraft can engage aircraft;
everything else ignores the sky entirely.

**Morale** is the Δυτικοί signature. Each armed unit carries a value in [0, 1]
that drifts towards a target set by three things:

1. the faction floor (Σοβιετικοί 0.90, Κινέζοι 0.80, Δυτικοί 0.50),
2. local force balance within 120 m, worth up to ±0.25,
3. the team's recent casualty count, worth up to −0.50.

Below 0.25 a unit **routs**: it stops firing, drops its target and falls back
directly away from the nearest enemy. It will not fight again until morale
recovers past 0.40. Morale also scales reload time by up to ×1.5, so a shaken
unit is not merely closer to breaking — it is already measurably worse.

Casualties decay one every two seconds, which keeps a bad engagement painful long
enough to matter without making one loss permanent.

The result is the intended asymmetry: the Δυτικοί field the best hardware and
still lose engagements where they take losses faster than they expected, because
a collapsing line stops shooting back.

**Proximity queries cost real time.** Morale needs friends and enemies within
120 m of every unit. Scanning the entity array per unit is O(n²) — 250,000
distance checks per tick at 500 units, which on its own pushed the frame from
9 ms to 19 ms and failed the 60 fps budget in the self-test. A uniform grid
(`SpatialIndex`) is rebuilt once per tick in O(n) and a radius query then touches
a handful of cells; frame time came back to ~11 ms. Cells are filled in ascending
slot order, so queries are still reproducible.


Click and box selection project each unit's world position to screen space and
pick the nearest within 30 px. The self-test round-trips the two: project every
player unit, pick at that exact pixel, and require the same unit back. That
caught a real bug class — a broken projection would otherwise only show up as
"clicking feels wrong".


**Determinism is the hard constraint.** Floating point is not reproducible across
machines, so:

- integer millimetres for world positions (`WorldPos`)
- Q16.16 fixed point for everything else (`Fix32`)
- a fixed 20 Hz tick; rendering interpolates
- PCG32 seeded per entity, never `System.Random`
- camera and UI live entirely in the render layer

This is a discipline cost paid once. It is the difference between "multiplayer
later" being a feature and being a rewrite.

## 8. Performance: what a hitch actually costs

The renderer reports frame times, but a hitch usually comes from the tick that
ran inside it. `SimProfiler` therefore times every system, and `--selftest`
reports both the average and the worst single sample per system. That instrument
found four separate problems, none of which were where they appeared to be:

| Symptom | Cause | Fix |
|---|---|---|
| 474 ms hitches | ~86 units ordered on the same tick, each running a full A\* search | path searches are **budgeted** at 4/tick; units wait for a route instead of stalling the frame |
| 25 ms per tick | combat re-acquired targets for every idle unit **every tick** — my retry throttle ran after the acquisition instead of before it | acquisition is throttled, and it queries the spatial index instead of every entity |
| 13 ms per tick | the navigation grid used the terrain's full resolution | navigation samples every second height-map cell: a quarter of the search space |
| visible zigzag | A\* paths follow cell centres | routes are **smoothed** to their turning points with a line-of-sight check |
| fog mask upload | the mask was rebuilt every frame instead of on the visibility interval | it is rebuilt every 10 ticks: **0.5 ms average**, 5.5 ms once, for the first texture upload |
| 16 ms hitch twice a second | vision re-stamped all 510 units on one tick, ~100 k cell marks every half second | stamping is **staggered**: each tick handles the entities whose slot matches the tick's phase, and a cell stays visible until its unit comes round again — worst sample **28 ms → 3–7 ms** |
| path searches costing 4 ms each | the A\* inner loop round-tripped index → x/z → index for every neighbour, ~80 integer divisions per expansion | the neighbour index is now a fixed offset from the current cell and the heuristic takes coordinates — **no divisions at all** in the hot loop, and the test suite got 25 % faster |

The result: worst frame 474 ms → **20–40 ms**, average 11 ms → **3.2 ms**, with
272 tests still green. The remaining worst frame is always an early simulation
tick (the first vision pass over the whole grid), not the renderer, and the last
spikes that survive move between systems from run to run — they are the operating
system pre-empting the process, not the simulation: only three gen-0 collections
and 6 MB of allocation happen over a 200-tick run, so it is not the collector
either. Everything is deterministic — budgets are counts, never durations,
because a time-based budget would make the simulation depend on how fast the
machine is.

### What the profiling pass found that is not a performance bug

Nothing in the simulation reads the visibility grid except the system that writes
it. That means fog of war is currently **presentation only**: the AI plans against
the whole map, and combat acquisition ignores whether a target is visible. It
also means the staggering above could not change the outcome of a single tick —
the golden hashes did not move. Making the AI respect fog is a fairness change,
not a performance one, and belongs with the next balance pass.



## 9. Greek text — four problems, all solved

Getting Greek on screen took four separate fixes. Each one looked like "the font
is wrong" but was something else entirely, so they are recorded here.

1. **ImGui glyph ranges.** ImGui's default atlas covers only Basic Latin, so the
   controller passes an explicit range table (Latin + Latin-1 + Greek and Coptic
   + punctuation + arrows + maths). Without it, everything is tofu.

2. **Blend state.** ImGui's atlas stores *straight* alpha: RGB is white and the
   alpha channel holds glyph coverage. MonoGame's `BlendState.AlphaBlend` is
   *premultiplied* (`One`, `InvSrcAlpha`), so every glyph quad drew as a solid
   white rectangle — the second, more convincing flavour of tofu, since the
   glyphs were positioned correctly. ImGui's OpenGL backend uses
   `glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE,
   GL_ONE_MINUS_SRC_ALPHA)`, which is exactly `BlendState.NonPremultiplied`.

3. **Window title marshalling.** MonoGame creates the window through
   `SDL_CreateWindow`, whose title parameter is a plain `string` and therefore
   marshalled as **ANSI**. A Greek title reaches SDL as invalid bytes and Windows
   draws replacement diamonds in the title bar. `Sdl.Window.SetTitle` is fine —
   it encodes UTF-8 explicitly — but `GameWindow.Title` ignores an assignment
   that does not change the value, so the title must be left unset until the
   native window exists and assigned only afterwards.

4. **SpriteFont ranges.** For text that lives in the world (faction labels over
   command centres), the font goes through the MonoGame content pipeline, and the
   `.spritefont` needs the Greek character regions added by hand. The default
   template is Basic Latin only.

### How each one is verified

Guessing was what caused the delays, so every check is now automated:

| Check | Mechanism |
|---|---|
| ImGui glyphs really rasterised | `FontCoverage` uses `FindGlyphNoFallback` and requires a non-zero advance; `FindGlyph` alone falls back and proves nothing |
| Atlas really has glyph data | `--inspect-ui` prints an alpha histogram of the atlas texture |
| SpriteFont has Greek | `WorldLabelRenderer` scans `SpriteFont.Glyphs` for Σ, ο, Δ, ί |
| Window title survived marshalling | `WindowTitleProbe` reads the title back out of SDL with `SDL_GetWindowTitle` and the self-test fails if it differs from the managed string |
| Text actually renders | `--font-sample` draws a large sample, and screenshots are analysed pixel by pixel |

All of these are asserted in `--selftest`, so a font regression fails the build
rather than shipping as boxes on screen.


## 10. Art direction

Models are **low-poly, stylised, and faction-tinted**. A vertex colour is a material,
and its alpha is the faction paint mask: 1 means the owning faction's colour covers
that surface, 0 means the material is drawn as authored. The shader blends the two,
shading the faction colour by the material's own brightness, so one red tint still
shows dark rubber tracks, light deck plates and gun metal. Terrain opts out of the
tint entirely; procedural meshes (markers, particles, health bars) are all tint.

Every model is generated by the scripts in `tools/blender/` — one family per
generator, all of them committed — so a fresh clone needs nothing downloaded and an
art change produces a reviewable diff. See
`src/MiVic.Game/Content/Models/README.md` for the generators, the animation contract
and how to review a model by eye.

Known limitation: models render in their **bind pose**. The node hierarchy is
animated by part name instead — wheels, turrets, radar dishes, and a walk cycle with
knees — so there is no skeleton and no skinning. Skinning is future work.


## 11. Fog of war and the outcome banner

Both of these were first built the obvious way and both looked wrong on screen,
so the reason each one changed is worth recording.

### Fog of war is an overlay, not tiles

The first version put one dark box on every navigation cell the player could not
see. It was cheap, it was correct, and it looked exactly like what it was: a
32-metre grid of boxes. On slopes neighbouring boxes stood on different heights,
so their side faces poked through the ground and the map read as stairs.

The current version duplicates the terrain's own vertices — so the fog hugs every
ridge instead of stepping over it — and textures that surface with a per-team
mask:

| Channel | Meaning |
|---|---|
| red | how much fog covers the cell: `0` visible, `150` remembered, `232` never seen |
| green | how much of the cell the team remembers |

`VisibilityGrid.BuildFogMask` writes that mask at navigation resolution, the
client blurs it with a 1-2-1 kernel and uploads it as a 65 × 65 texture, and the
shader samples it with **linear** filtering. That filtering is the whole trick: it
turns a boolean cell grid into a soft boundary, so the player never sees the
lattice the simulation reasons about. Cell centres map exactly onto texel centres,
so a mask texel covers precisely the ground the simulation evaluated.

The fog is drawn last, with depth reads but no depth writes, so it darkens terrain
and units alike while never floating above the ground. Enemy units in unseen
cells are not drawn at all — that decision lives in the client's instance
collection, not in the overlay.

### The outcome banner needs a scrim

The banner is the last thing a match shows, and the first version was just a
translucent ImGui window over the battlefield: the headline was legible and the
sentence underneath it disappeared into tanks. It now draws a full-screen scrim
first, then a near-opaque bordered panel of fixed width, with the headline
centred in a second, much larger atlas entry (ImGui's per-window font scale is
deprecated and had no effect) and the reason wrapped underneath. The same
treatment — darker, bordered panels — was applied to the HUD as a whole, because
every panel sits on top of a bright, moving scene.

### How both are verified

`read_image` is unavailable in this environment, so the visuals were checked by
analysing the rendered PNGs pixel by pixel:

- **Fog edge smoothness.** Adjacent-pixel luminance jumps across the fog boundary
  are a few units over tens of pixels, which is what a filtered edge looks like;
  the old tile version stepped by tens of units in a single pixel every 32 metres.
- **Mask alignment.** A shot centred on the player's base shows the base unfogged
  and the fog closing in around it, which is only true if the mesh UVs, the
  navigation grid and the texture agree.
- **Banner layout.** Text rows are located by scanning for bright pixels: the
  headline is centred on the display midpoint and the detail line stays inside
  the panel.


## 12. Replays: a match is its inputs

The determinism contract has always said "the same seed and command log must
produce the same world". Replays turn that claim into a feature and a test at the
same time: a file that contains a seed, a scenario, an entity capacity and the
external command log must replay into the identical state hash.

Three decisions shaped the format:

1. **Record the issue tick, not the execute tick.** A command is stamped with the
   tick it was enqueued on. Replaying means re-issuing it at that same moment,
   after which the simulation's own scheduling does the rest — including the
   commands a system scheduled for a later tick.
2. **Log only external commands.** The AI enqueues orders from inside
   `SimWorld.Step`, and those orders are a function of world state. Recording
   them would apply each one twice on replay. A flag set for the duration of
   `Step` keeps them out, which also keeps a 500-unit battle's replay at a few
   kilobytes.
3. **The world builder lives in the simulation.** `Scenario.Build` replaced the
   client's private skirmish setup, because a replay has to rebuild the starting
   world with no client involved. It returns the positions each unit was
   *requested* at, not the entity's own position: `Spawn` replaces the Y
   component with the terrain height, and the wander orders are generated in the
   plane they were requested in.

That last point is not theoretical. The refactor initially stored the entity's
own position, and the client's state hash changed — the wander destinations moved
by a few metres, which was invisible on screen but wrong. The hash caught it
immediately, and a golden test on the skirmish's initial state now pins it.

### How it is verified

| Check | Mechanism |
|---|---|
| A recording reproduces its match | `ReplayFile.Verify` rebuilds the world and compares state hashes; `--replay` exposes it as an exit code |
| The log really is complete | dropping a command from the log must make the replay diverge, asserted in the tests |
| The AI is not double-applied | a match with no external commands at all still verifies, because the AI's orders are re-derived |
| Malformed files are refused | bad magic, truncation, unknown version, out-of-range capacity and unknown scenario all throw rather than replay something wrong |
| Playback agrees with the recording | `--watch` drives the world purely from the log and the self-test asserts it lands on the recorded hash |
| The starting world is stable | `ScenarioTests` pins a golden hash of the skirmish's initial state |

The self-test's `replay round-trip` line runs this end to end on every build: it
records the live skirmish, replays it in-process and reports the verdict.


## 13. Procedural music

There are no audio files in this repository. Every note is generated: a small
chip synthesizer, a set of musical idioms per faction, and a deterministic
sequencer. That is partly aesthetic — an early-80s home computer could not have
recorded an orchestra either — and partly engineering: generated music is
reproducible, diffable in principle, testable without ears, and adds no licence
obligations to the repository.

### The sound

`ChipSynth` renders one note at a time into a float mix: pulse (with duty),
triangle, saw and a 15-bit LFSR noise channel for drums, each with an ADSR
envelope. That is deliberately the palette of an AY-3-8910 or a SID: square
leads, triangle basses, noise percussion. Clipping is handled once, at the end,
by normalising the finished mix to 90 % of full scale, so voices can be layered
without tracking headroom.

### The idioms

The factions are separated by music theory, not by mixing:

| Faction | Scale | Tempo | What makes it sound like itself |
|---|---|---|---|
| Σοβιετικοί | harmonic minor | 96 BPM | dotted march rhythm, stepwise phrases with fourth/fifth leaps, snare on the backbeat, a raised seventh at cadences |
| Κινέζοι | anhemitonic pentatonic (gong mode) | 84 BPM | no semitones at all, grace notes before phrase peaks, root-and-fifth plucks, flowing 4/4 |
| Δυτικοί | blues scale | 152 BPM | twelve-bar blues, boogie-woogie left hand, backbeat snare, detuned saw "guitars", a bubblegum hook |

The Russian material leans on the minor modes because that is where the folk and
patriotic tradition lives: natural minor for the song, harmonic minor for the
cadence that makes a march sound Slavic, Dorian for the heroic variant. Chinese
patriotic song is overwhelmingly pentatonic, which is why the generator cannot
produce a semitone in that mode. The Western theme is cheap on purpose: the saws
are detuned by 14–16 cents and the lead wobbles, because the joke is that the
empire's culture is mass-produced.

### Why it is deterministic

The generator uses the simulation's own PCG32, so `(faction, seed)` fixes the
waveform exactly. That makes the music testable in the ordinary way: the tests
assert that the same seed gives the same samples, that different seeds give
different music, that each faction differs from the others, that no sample clips,
and that the scales really are what they claim (a pentatonic with a semitone in
it fails the test).

`--render-audio <dir>` writes one 30-second WAV per faction plus a report of
scale, tempo, note count, peak and RMS, which is how the music gets reviewed
without ears. The measured themes are 1.3 MB each at 22 kHz mono — period-correct
and small enough to generate on demand.

### Combat sound effects

Weapons use the same chip and the same reasoning: a tank firing is a pitched
sweep from 200 Hz down to 60 Hz under a noise burst, an explosion is a sub-bass
drop under a long noise tail with crackle on top, and an armour impact is two
detuned partials ringing against a short crack. `SoundBank` renders ten of them —
rifle, tank gun, artillery, anti-air burst, two explosion sizes, impact, alarm,
interface click and a looping engine — in a few hundred kilobytes and no files.

Two decisions are worth recording:

1. **Impacts are detected by diffing health, not by watching cooldowns.** A unit's
   `AttackCooldown` also jumps when it fails to acquire a target (the search-retry
   value), so a cooldown-based trigger would play gunshots from units that never
   fired. Health only moves when a round actually lands.
2. **The mixer is budgeted.** A five-hundred-unit battle produces far more events
   than a sound card can mix, so `SfxDirector` starts at most six effects per
   frame and drops the rest. Distance attenuation scales with the camera: at
   strategic zoom the audible radius grows with it, or half the battlefield would
   be silent.

`--render-sfx <dir>` exports every effect as a WAV with its measured duration,
peak and RMS, and the self-test reports how many were played and dropped.


## 14. Three input bugs that all looked like "nothing happens"

The player reported that clicking a unit did not select it, that right-clicking
did not move anything, and that the wheel zoom was a jump cut. They turned out to
be three unrelated faults, which is why each is recorded here with the check that
now pins it.

### 1. Screen pixels were being fed in as normalised device coordinates

`RtsCamera.ScreenToGround` unprojected `(mouseX, mouseY, 0)` and `(mouseX, mouseY, 1)`
through the inverse view-projection. NDC is [-1, 1]; a pixel is [0, width]. Every
ground order therefore resolved to an arbitrary point, or missed the ground
entirely and was silently dropped. The diagnostic that proved it printed a
centre-screen ray pointing *up* and 3,000 km into the distance.

`TryGetRay` now converts pixels to NDC first, and the self-test projects a point
on the ground, unprojects that pixel, and fails if the two disagree by more than
two metres. It currently reports **0.00 m** of error.

### 2. Ground picking ignored the terrain

Even with correct coordinates, intersecting the y = 0 plane is wrong: the plane is
the *lowest* point of a height field that rises 42 m, so a click on a hillside
resolved tens of metres past it, and a click near the horizon missed and dropped
the order. `MiVicGame.TryScreenToGround` now marches the ray against the height
field with a 2 m step and refines the crossing by bisection, falling back to the
plane only when the ray never meets the terrain.

### 3. A focused HUD window swallowed the mouse

Input was gated on `WantsMouse || WantsKeyboard`. With ImGui's keyboard navigation
enabled, any focused window reports `WantCaptureKeyboard`, so once a panel had
focus the player could neither select nor order — while drag-selection still
worked, which is what made the report confusing. Mouse actions are now gated only
on `WantsMouse`, the panels carry `NoNavFocus` so they cannot take focus, and the
self-test reports ImGui's capture state every run.

### The zoom cut

`HandleZoom` computed `0.88^scrollWheelDelta`, but MonoGame reports 120 per wheel
detent, so one click evaluated `0.88^120` and slammed the camera to its minimum
distance. It now converts the delta to detents, moves about 7 % per detent, and
eases toward the target over roughly a tenth of a second.

### Feedback

Move orders now drop a ring at the destination that expands and fades over
0.9 s. Without it there is no way to tell an accepted order from a click that
landed on a cliff and was refused, which is exactly the ambiguity that made this
bug report hard to pin down.

### The production panel was off the bottom of the screen

The build panel was positioned at a fixed `y = 430`. With its queue, unit buttons
and research section it is 250–400 pixels tall, so in a 720-pixel window every
button sat below the bottom edge: the player could see the panel's title and
click nothing in it. It is now anchored to the bottom-left corner (pivot
`(0, 1)`), grows upwards, and caps its height at 62 % of the display so it
scrolls rather than overflows. The help panel moved to the bottom-right corner
for the same reason, and the panel now shows a hint — "select one of your
buildings to produce units" — when no building is selected, instead of vanishing.

### Particles, and the same one-way rule

Explosions and smoke follow the rule the whole client follows: the simulation
reports what happened, the client decides what it looks like. `SimBridge` diffs
the alive set each tick and emits a death event with the entity's faction, kind
and last position; `MiVicGame` turns those into a spark burst, a fireball and
debris, and keeps a slow plume over structures whose health has dropped. Enemy
deaths the player cannot see emit nothing, so the fog stays honest. Particles use
their own generator and a wall-clock delta — neither can reach the simulation —
and a fixed-size ring buffer caps the cost at 4096 particles no matter how much
explodes at once.
