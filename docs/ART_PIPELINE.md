# MiVic — art, animation and terrain pipeline

Decisions recorded from the design discussion. Nothing here is implemented yet
except where noted; this file is the plan of record.

## 1. Model fixture: `--viewer`

An in-game turntable, so models can be judged without guessing from screenshots.

- **Mode:** a dedicated `--viewer` launch option. `F3` opens the same view over a
  running game.
- **One model at a time.** The stage reuses the gallery world
  (`Scenario.BuildGallery` already lays out every faction × role); the selected
  model is drawn normally and the rest are faded to silhouette.
- **Camera:** distance ~14 m, low pitch, target locked to the model's bounding
  box centre. Auto-orbit ~30°/s toggled with `Space`; `Q`/`E` rotate manually,
  wheel zooms, `W`/`S` change pitch. Nothing else can move the camera, so a given
  view is reproducible.
- **Selection:** `←`/`→` step through the catalogue, `1`/`2`/`3` jump to a
  faction. The panel names the model in Greek and its source `.glb`.
- **Orientation:** axis markers reuse `CollectGalleryAxes` — cyan +X is forward,
  magenta +Z is right — and the panel prints the measured nose angle that
  `--inspect-models` already computes. The fixture is the fastest way to re-check
  a model after re-exporting it.
- **Animation panel:** lists the clips in the model and shows the current one
  with a playhead. `Enter` cycles `idle` / `walk` / `fire`, `P` pauses, `[`/`]`
  scrub. A "parts" readout lists the named nodes the renderer drives (`turret`,
  `barrel`, `wheel_*`, `radar`, `door`). Until the Blender pipeline produces
  clips, the panel states that the model has none.
- **Silhouette check:** `V` forces the flat-silhouette view, so a model can be
  judged as pure outline. This is the acceptance test for §2.1.
- **Headless twin:** `--viewer-shot <png> --viewer-model <id> --viewer-angle <deg>`
  renders one model from a fixed angle for CI, and `--viewer --selftest` reports
  per model: node names, triangle and vertex counts, clip names and durations,
  and the nose angle.

## 2. Model and animation plan

Blender **5.2.1 LTS** is installed at
`C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`, headless `bpy`
works (Python 3.13.13) and the glTF exporter addon is present. Models are
therefore generated as code:

```
pwsh ./tools/blender/build_all.ps1
```

**Status: every model in the game is generated.** All 46 `(faction, role)` slots in
`ModelCatalog` point at `Content/Models/Generated/`, and `--inspect-models` reports
`failures=0 missing=0`. The generator is split by family, because a soldier is a
different problem from a building and one file that owned both could not be worked on
in parallel:

| Script | Owns |
|---|---|
| `build_vehicles.py` | the shared mesh and material kit, tanks, self-propelled guns, anti-air, Συλλέκτης, Κατιούσα, the electro prototype, the drone, the aircraft |
| `build_figures.py` | soldiers: line infantry, commissar, robot infantry, mercenary, stealth reconnaissance |
| `build_buildings.py` | headquarters, factory, power plant, nuclear plant, design bureau, gun emplacement, anti-aircraft emplacement |
| `build_props.py` | woodland: six tree species, drawn instanced |
| `build_bridge.py` | the bridge block, and one panel of its rail |

The last two are not roles: a tree and a bridge have no faction and no slot in `ModelCatalog`, so
they are loaded by the renderer that draws them (`ForestRenderer`, `BridgeRenderer`) rather than
resolved through the catalogue.

A bridge is **one block repeated**: the whole of a crossing — two cells or twenty-four — is the
same 120-triangle block placed once per cell of the span, and a hole blown in a crossing is a block
that is simply not drawn. Its **rails are separate**, and the client places one along each side of
a block that leads nowhere, asking the terrain layer whether a vehicle could drive off there. That
is why a span has rails along the water and none where it meets the bank, a crossroads has none
across the way through, and the dead side of a T is closed: the rule is the simulation's own
answer about passability, so a rail can never wall off a way a unit is willing to take. Baking the
rails into the block could express none of it — the rail is authored on the deck's edge inside the
block's own frame, and is imported with `CentreOnOwnBounds: false` for exactly that reason.

The silhouettes come out measurably distinct, which is the point of §2.1:

| | width | height | length | wheels/side | turret |
|---|---|---|---|---|---|
| Σοβιετικοί | 4.44 | **3.77** (lowest) | 6.17 | 5 | cast dome |
| Κινέζοι | **3.50** (narrowest) | **4.72** (tallest) | 5.70 | 5 | welded box |
| Δυτικοί | **4.86** (widest) | 4.55 | **7.90** (longest) | 7 | sloped wedge |

### 2.0 Loader limits — resolved

Both are fixed:

1. **The loader keeps parts.** `GltfLoader.LoadModel` returns each named node as its
   own mesh, with a **parent-relative** transform and its parent's index. Parent
   relative matters: an accumulated transform would freeze a barrel in place the
   moment the turret turned. The alignment, scale, centring and grounding are
   returned as one `ModelTransform` applied outside the parts, so a part's pivot
   stays where the pivot is.
2. **`COLOR_0` passes through as RGB**, so a model's own palette reaches the shader
   and the faction tint composites over it rather than replacing it.

`--inspect-models` lists each model's parts, which is the animation contract and
the check that catches a model whose parts are baked into one mesh. It showed that
the borrowed assets are already parts-rigged in most roles — `Soldier_Head/Legs/
Feet/Body`, `Tank_Turret/Tank_Gun/TrackMesh.L/R`, `Turret_*_Base/Top` — so
animation is not limited to the generated models.

**Tier 1 is built.** `turret`, `wheel_*` and `radar` are animated in the renderer,
each as a pure function of simulation state:

| Part | Driven by |
|---|---|
| `turret` | bearing to `entity.TargetSlot`, relative to the hull's heading |
| `wheel_*` | `Entity.DistanceTravelledMm` ÷ the wheel's own measured radius |
| `radar` | the tick, so a dish sweeps identically in a replay |

The radius is measured from the model rather than typed into a table — the wheel's own
cross-section, times the scale the loader fitted it at — so a new vehicle needs no number
entered by hand. The scale is read as the **length of the model transform's first basis row**,
never as `M11`: the transform carries the turn that aligns the model, so `M11` is the cosine of
that turn, and for every tank in the game it is `cos(-90°)` — four times ten to the minus eight,
which multiplied every road wheel's radius down to nothing and left every one of them drawn
still. A wheel radius of zero is skipped by the animator, so the symptom was silence rather
than a wrong speed: `parts <slot>` reported `wheel radius none` for vehicles that have ten of
them, which is what the query is for.

The odometer lives in the simulation rather than the client precisely so that a
replay spins the wheels the same way; deriving it from frame-to-frame movement in
the client would make the animation frame-rate dependent. It accumulates Euclidean
distance, not the sum of the step components — a diagonal step is 400 mm of travel,
not the 566 mm its two parts add up to, and a wheel spun by the wrong number slides
instead of rolling.

### 2.0b Structures are raised, not spawned

A structure arrives unfinished and **does nothing until it is up**: no income, no
production, no research, and `HasStructure` does not count it — so an ability that
needs a standing design bureau does not fire at a building site. The client uses
that state for three effects, all of them presentation only:

| Effect | Driven by |
|---|---|
| The building rises out of the ground | `ConstructionTicksRemaining / ConstructionTicksTotal`, scaled on Y |
| Dust while it is being built, a heavier puff when it finishes | the same fraction; the puff fires on the transition |
| **Working smoke from the chimney** | emitted at the world position of the named `stack_*` or `barrel` part, while the structure is healthy |

Smoke coming out of the chimney rather than out of the middle of the roof is what
the part contract buys: `TryPartWorldPosition` composes a part's parent chain and
returns where it actually is, so a new building smokes correctly by exporting a part
called `stack`.

Three tiers of animation, cheapest first:

1. **Moving parts, no new pipeline.** Vehicles split into hull / turret / barrel /
   wheels as separate nodes, each drawn as its own instanced batch. Turret yaw
   comes from `entity.Heading` versus target bearing; wheel rotation from
   distance travelled ÷ radius. Both are pure functions of simulation state.
   Buildings get the same treatment: rotating radar dish, factory crane and
   doors, power-plant fans.
**Tier 2 is built.** Limbs are matched by contract name — `Leg*`, `Shin*`, `Arm*`,
`Body`, `Head` — and the generated figures ship exactly those parts, so a stride is a
rotation rather than a skeleton. The phase comes from the odometer, so the legs keep
step with the ground rather than with the frame rate, and a unit that has stopped
stands still. Limbs swing about the **top of their own mesh**, measured at load: an
imported rig has no skeleton to query, so measuring where the joint is beats assuming
the mesh origin is the hip. The generators therefore build a limb hanging *below* its
own origin, which puts the origin at the joint for free.

The knees bend, and only on the forward swing: a stiff-legged walk is the most
obvious way for a low-poly figure to look wrong. `Leg*`/`Shin*` are separate parts so
the shin inherits the thigh's swing and adds its own bend behind it, and the two legs
run in antiphase. Arms swing against the leg on the same side.

Known limitation: the legs are rigid below the knee — there is no ankle and no foot
roll. A shin bend plus a boot is enough at the distances the game is played at; the
next honest step up is skinning (tier 3).

2. **Parts-rigged infantry.** Torso, head, arms and legs as separate nodes with a
   procedural walk cycle driven by `world.Tick` phase. No skinning, no weight
   painting, and it reads as a late-80s/90s RTS.
3. **Skinning and tread scrolling.** A skinned vertex format (joint indices and
   weights), a bone-matrix palette and animation sampling, plus a UV-scrolling
   tread shader. Only worth it if close-up infantry matter.

Animation phase is driven by the tick and interpolation alpha, never the wall
clock, so replays look identical. Fire and death animations hang off the
`UnitHit` / `UnitDestroyed` events the client already receives.

**Style:** low-poly, but colourful and textured. Vertex colours carry the base look,
and they now carry **two** things: a material colour and, in its alpha, the faction
paint mask. The shader blends the material against the faction colour — shaded by the
material's own brightness — so a Σοβιετικοί tank is red *and* has black rubber tracks,
a gunmetal barrel and a light grey radar dish. Getting this wrong is what makes every
unit of a faction one flat silhouette, and it is worth stating the two ways the old
pipeline did exactly that: per-mesh normalisation to the brightest channel erased the
difference between dark and light materials, and a clamp at 0.35 threw away the
bottom third of the range.

Still future work: a shared 256² per-faction palette texture for panel detail, decals
and hazard stripes, and an emissive channel for windows, headlights, engine glow and
lava. That needs a texture and sampler in `InstancedMesh.fx` and UVs in
`VertexPositionNormal` — one draw call per mesh, so the cost is negligible.

**Licensing:** every model in the game is generated by `tools/blender/` and is ours,
so it is committed and a fresh clone looks right. The QAL Quaternius set that the
game used to borrow from is no longer referenced by any slot; `tools/fetch-assets.ps1`
still downloads it, and those files stay out of the repository.

**Pipeline changes:** `ModelCatalog` gains per-kind part specs (node names, wheel
radius, turret pivot, clip names); `InstancedRenderer` gains a node-transform
path and later a skinned technique; `--inspect-models` grows from nose angle to
node names, joint counts, clip durations and per-part bounds.

**Order of work:** generator for one tank in all three faction silhouettes plus
one building → node-transform animation → inspector extension → parts-rigged
infantry → palette texture and emissive channel. The tank goes first in three
variants rather than one, so the profile-driven builder and the shared part
contract are proven before there is more than one role to migrate.

### 2.1 Silhouettes: decided

**Each faction gets genuinely different geometry.** A recoloured hull is out.
Colour alone is not enough: in this game the player must identify an enemy
faction at a glance, at RTS zoom, and the three factions are asymmetric — so a
Soviet tank and a Chinese tank are different machines, not the same machine in a
different paint.

The silhouette language, applied to every role:

| | Σοβιετικοί | Κινέζοι | Δυτικοί |
|---|---|---|---|
| Read as | compact, rugged, low-slung | utilitarian, mass-produced | large, sophisticated, heavy |
| Hull | small, low, sloped all round | tall, narrow, slab-sided | long, wide, high — it has the volume because it has the engine |
| Turret | cast dome, rounded | flat welded box | wedge, sloped plates |
| Tracks | wide, few big road wheels (5) | narrow, exposed | wide, but many road wheels (7–8) |
| Barrel | short, thick, muzzle brake | plain tube | long, thin, thermal sleeve |
| Ground pressure | lowest — light hull, wide track | highest — light hull, narrow track | high, but spread over many road wheels |
| Detail | fuel drums, rivets, tow hooks | seams, handholds, stowage | optics, antennae, sensors |
| Buildings | brutalist concrete, chimneys, monumental scale | modular repeated bays, tiled roofs, banners | glass and steel, clean curves, spires |

Rules that keep the difference readable rather than merely present:

- **The shared part contract is fixed; the geometry is not.** Node names
  (`hull`, `turret`, `barrel`, `wheel_*`, `radar`, `door`) and pivot conventions
  are identical across factions, so `ModelCatalog`, the node-transform path and
  the animation code stay faction-agnostic. Only the mesh behind each node
  differs. This is what keeps three variants affordable.
- **Silhouette must survive greyscale.** Identity comes from proportion and
  outline, not hue. The acceptance test is the `--viewer` silhouette-fade view
  and a 12-pixel-tall render: if two factions are indistinguishable there, the
  geometry failed and gets re-cut.
- **Exaggerate by 10–20 %.** Real proportions read as mush at RTS scale; distinct
  features (dome vs wedge, wide skirts vs exposed road wheels) are pushed until
  they read at distance.
- **Palette and decals layer on top,** they do not carry identity: vertex
  colours plus the per-faction palette texture and a faction badge decal.

The ground-pressure row is not decoration — it is a real mechanic (§3.1) and the
reason the Soviet silhouette is *small and wide* rather than *big and heavy*. The
historical basis: the T-34/76 ran at 0.64–0.68 kg/cm² nominal ground pressure — a
26-tonne hull on wide tracks — against roughly 0.74–1.05 for the German heavies,
which is why the spring and autumn *rasputitsa* hurt the invader more than the
defender. Soviet armour philosophy was compact, sloped and light-tracked to keep
moving where the heavies sank. The generator must therefore treat **mass and track
width as design parameters per faction**, not just hull shape.

**Cheap by design, not by scale.** The T-34 was also *cheap*: deliberately
simplified, built in tractor plants, turned out in tens of thousands. That is the
other half of the silhouette decision and it applies to the whole standard Soviet
vehicle line.

- **Standard Soviet units are simple and cheap** — few parts, no electronics, low
  tech requirement, fast to build. The hull is cheap because the *design* is crude
  and rugged, not because the factory is enormous.
- **Advanced Soviet units are the opposite** — design-bureau prototypes,
  electro-artillery, the exotic stuff: expensive, slow, and capped in number.
  That is what "cannot mass-produce" means. It is the *technology* that cannot be
  mass-produced, not the tank.

This is the reconciliation the current balance table is missing; the concrete
production ordering, cost and income numbers are decided in `docs/DESIGN.md` §7.
In short: Σοβιετικοί field a cheap but *capable* mass plus a few irreplaceable
wonder-weapons, Κινέζοι a cheap but *weak* mass with the highest throughput, and
Δυτικοί the fewest and most expensive units in the game — funded by an income
advantage rather than by productive capacity. Not yet applied to `UnitCatalog` or
`FactionProfile`.

Generator consequence: `build_vehicles.py` is one profile-driven builder per
role — hull ratios, turret shape enum, wheel count and radius, barrel length,
detail density — invoked once per faction. Wheels, tracks and small props are
shared meshes where sharing does not blur identity; hulls, turrets and barrels
never are. The cost is three variants per role, which is why the profile
structure lands in the very first generator rather than being retrofitted.

**No plumbing needed.** `ModelCatalog` already keys on `(Faction, UnitKind)` and
already resolves a different `.glb` per faction, so the renderer and every caller
are faction-aware today. The change is confined to the spec table's contents: the
borrowed Quaternius files (`tank_heavy` for Soviet, `tank_light` for Chinese,
`tank_medium` for Western) are replaced by generated per-faction models, and each
entry gains a part spec. The current table is also the proof that distinct
silhouettes cost nothing at runtime.

## 3. Terrain with per-unit difficulty

**Implemented.** `TerrainLayer` classifies nine surface types on the navigation
lattice from the height field and the seed; `PathContext` carries a mover's
movement class and ground pressure into `PathFinder`; costs are permille with zero
meaning impassable. Mud and snow are scaled by ground pressure, the generator
carves fords so the map cannot fragment, spawning avoids water, and the A* surface
lookup is memoised per search. `docs/README` describes the table and the
`TerrainTests` cover determinism, passability, ground-pressure ordering, aircraft
immunity, the ford rule and full-map connectivity.

The rest of this section is the plan for what is not built yet.

A parallel `TerrainLayer` grid, one byte per navigation cell, generated
deterministically from the world seed: `Grass, Mud, Sand, Snow, Rock,
ShallowWater, DeepWater, Lava, Mine`. Passability and cost become a lookup,
`cost[unitKind][terrainType]` in permille with `0` meaning impassable, so the
existing cost-driven A\* picks it up unchanged.

Starting table (multiplier, `—` impassable). Mud and snow are further modulated by
each unit's ground pressure (§3.1):

| | Mud | Snow | Sand | Water | Lava |
|---|---|---|---|---|---|
| Infantry | ×2.0 | ×1.5 | ×1.2 | — | — |
| Tank | ×2.5 | ×1.8 | ×1.5 | — | — |
| Artillery | ×3.0 | ×2.0 | ×1.6 | — | — |
| Aircraft | ×1.0 | ×1.0 | ×1.0 | ×1.0 | ×1.0 |
| Harvester | ×2.2 | ×1.6 | ×1.3 | — | — |

### 3.1 Ground pressure: mud is a contest, not a unit-kind tax

Every mobile unit carries a `GroundPressure` value (permille of an infantry
baseline, in `UnitCatalog`), and the mud and snow multipliers key off it instead of
off `UnitKind` alone. Historically this is the mechanic: the T-34/76 ran at
0.64–0.68 kg/cm² nominal ground pressure — a 26-tonne hull on wide tracks — against
roughly 0.74–1.05 for the German heavies, which is why the *rasputitsa* hurt the
invader more than the defender. Light hull, wide track, low pressure.

The honest caveat is worth encoding rather than hiding. Nominal pressure is a weak
predictor: by mean maximal pressure — which counts how many road wheels share the
load — the T-34/85 sat at ~2.15 kg/cm² against the Panther G's 1.63, and US crews in
1945 reported German tanks handling soft ground *better* than their Shermans
([Ogorkiewicz's figures via Christos military and intelligence corner](http://chris-intel-corner.blogspot.com/2013/01/more-on-t-34.html?m=0)).
Mud stopped T-34s too. So the rule is **light hull + wide track + many road
wheels**, and no faction gets a free pass. The silhouette table encodes exactly
that: the Soviet hull is light and wide-tracked with few big wheels, the Western
hull is heavy but spreads the load over 7–8, the Chinese hull is light on narrow
tracks and therefore the worst off.

Consequence for play: Σοβιετικοί armour barely slows in mud and snow; Δυτικοί
armour is the fastest on dry ground and becomes a liability in the wet season;
Κινέζοι narrow-tracked vehicles are worst affected and lean on infantry, which is
their doctrine anyway. A Western player attacking in mud is making a mistake, and
the season must be legible in the UI or the penalty reads as a bug.

- **Mud churn is built.** A per-cell wear counter rises as units drive over it — by
  the mover's ground pressure, so a heavy hull churns more than a light one — and
  settles over time. Worn ground costs everyone more, and because the surcharge
  multiplies the cost a unit already pays, the same churned field is a nuisance to
  infantry and a bog to a tank. This is what makes ground pressure dynamic rather
  than a table: a column of armour churns its own route and slows itself down.
  It is hashed like any other state, and drawn as the ground darkening towards mud,
  re-meshed on a 1.5 s throttle because churn changes every tick an army moves.
- **Lakes and rivers.** Lakes are basins below a water level; rivers are channels
  carved along a descent path, widening downstream. Ground units cannot cross
  water, which makes both real geography: either the generator guarantees a
  **ford**, or the player builds a **bridge** (a new structure, and a role for
  engineers). Leaning toward both — fords on narrow rivers, bridges on wide ones.
- **Mines are built.** Deposits are scattered as clusters over dry, passable ground,
  and a **Συλλέκτης** parked on one adds materials per tick. The ground is the
  income and the vehicle is what collects it, which is also what finally gives the
  harvester a role: it has no weapon and no place in a fight, so it is the unit an
  opponent raids rather than shoots.
- **Volcanoes are built.** Rock slopes with an impassable lava crater that burns
  whatever is standing in it — fast enough to be fatal, slow enough to escape.
  Aircraft are over the lava, not in it.
- **Order matters, and this cost a real bug.** Volcanoes are raised *before* the
  connectivity pass and deposits scattered after it, because anything impassable
  added after the ford pass silently breaks the guarantee that every patch of
  ground can be reached. Wired the other way round, A\* spent its whole expansion
  budget on goals that could not be reached: pathfinding went from 0.08 ms to
  3.8 ms per tick and the test suite from 21 s to 42 s. The ford pass now carves
  through lava as well as deep water, so a crater can never cut the map in two.
  `HazardTests.LavaAndDepositsDoNotStrandAnyone` is the regression test.
- **Snow** slows everything except aircraft and could shorten sight ranges, which
  hooks into the per-team visibility grid.

Open questions: fords versus bridges; static mud versus churned mud; harvester
mines versus a passive bonus; whether terrain affects vision at all.
