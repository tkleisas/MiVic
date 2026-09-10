# The terrain model

What the ground is, and what it is going to be. Written before the change rather than
after, because the details are what keep it reproducible.

## What it is today

Two layers, one derived entirely from the other, plus two bolt-ons:

| Layer | Shape | Contents |
|---|---|---|
| `HeightMap` | 129×129, millimetres | heights over 600 m, about 42 m of relief. `HeightAt`, `SlopePermille`, bilinear sampling |
| `NavGrid` | 65×65, ~9.2 m cells | built from the height map: walkability and a per-cell height. What A\* runs on |
| `TerrainLayer` | same 65×65 | **one byte per cell**: the `TerrainType` |
| churn | second byte per cell | wear from traffic: additive, decaying |
| weather | override plus an expiry tick | a temporary type change |

The surface byte is chosen by **height and slope alone** — `Classify` is
`height → DeepWater / ShallowWater / Rock / Snow / Mud / Sand / Grass`, with slope
deciding only whether a cell is Rock. Everything else is derived from that one byte:
movement cost, cover, the colour the terrain mesh paints, and the shader treatment.

## Why that is not enough

1. **One byte means one thing.** A cell cannot be rocky ground with trees on it, or
   muddy woodland, or sand with scrub. `Forest` is a *surface*, so woodland cannot grow
   on mud or sand, cannot be sparse, and cannot change without changing what the cell
   fundamentally is.
2. **Churn is already a bolt-on**, which is the model saying it is full.
3. **Cover is a lookup on the surface**, so there is no cover that is not a terrain
   type: no rubble, no crater rims, no reverse slope.
4. **Sand and snow are altitude bands**, because altitude is all there is. Sand is
   coastal; snow is altitude *and aspect*. Two of the three "feature that silently never
   happens" bugs in this project were here.
5. **No aspect or landform**, so ridges, valleys, passes and reverse slopes are
   invisible to the simulation and nothing tactical can key on them.

## The model we are moving to

The surface byte **stays**. It is the *movement* classification, movement cost is a
categorical lookup on it, and pathfinding being cheap matters more than it being
expressive. New continuous and discrete attributes go in a **second, 32-bit word per
cell**, so attributes become a first-class part of the ground rather than another
parallel array.

### The attribute word

| Bits | Field | Meaning |
|---|---|---|
| 0–7 | `Vegetation` | canopy density, 0 bare to 255 closed |
| 8–11 | `Moisture` | how wet the ground is, 0–15 |
| 12–15 | `Aspect` | the way the ground faces: 0–7 compass points, 8 flat, 9–15 unused |
| 16–19 | `Landform` | 0 plain, 1 slope, 2 ridge, 3 valley, 4 pass, 5 plateau, 6 basin, 7 shelf |
| 20–23 | `Fuel` | how much there is left to burn, **scaled** from the vegetation because 8 bits do not fit in 4 — a closed canopy is 15 and bare ground is 0; falls as it burns |
| 24 | `Burning` | fire is in this cell now |
| 25 | `Burned` | it has been on fire and is charred; regrows far more slowly |
| 26 | `Cratered` | shelled ground: extra cover, worse going |
| 27 | `Rubble` | collapsed structure: extra cover, impassable to tracked |
| 28 | `Flooded` | standing water laid by weather control |
| 29–31 | spare | |

A `TerrainAttributes` wrapper with named accessors over the `uint`, never raw bit
twiddling at the call site.

**Stored versus derived:** `Vegetation`, `Moisture`, `Landform`, `Fuel` and the flags
are generated and stored. `Aspect` is stored because it is a *neighbourhood* fact —
every other field is a point fact, and recomputing aspect per query would mean reading
three cells to answer a question about one.

**The scales are anchored, because a threshold means nothing without one.** `Moisture`
is 15 at standing water — every water cell is 15, by rule — and falls to 0 at the
highest ground on the map, measured against the map's *relief* rather than its height
ceiling, which is the mistake the sand and volcano lines made. Drier ground drains
faster, so slope subtracts from it. The fire step's "low moisture" threshold is written
against that scale and not against a feeling. `Vegetation` is likewise anchored at both
ends: 0 is bare ground and 255 is closed canopy, and both values are reachable on a
generated map, or the top of the scale is a number that never happens.

### Vegetation is mutable

This is the mechanic, not a decoration:

- **Fire starts** where an explosion or a burning structure meets a cell with fuel and
  low moisture.
- **Fire spreads**, in deterministic order, to neighbours that have fuel; each step
  consumes fuel, cuts vegetation density, and eventually exhausts itself. A fire
  crossing a road stops at it. A fire in wet ground does not start.
- **Tracks crush** what they cross: vehicles reduce vegetation density in their cell the
  way they already add churn. A tank park becomes a clearing.
- **It regrows**, slowly, on the tick — and a `Burned` cell regrows far more slowly still,
  so a wood that has been through a battle looks like one for the rest of the match.

No new player command is needed for any of this: fire is a *consequence*, and it is
driven by the tick and the existing event stream. A deliberate incendiary strike would
be an ability, later, and would be the only part that needs a command.

### What attributes change

- **Cover** becomes derived rather than looked up: surface × vegetation density ×
  landform, with `Cratered` and `Rubble` adding cover of their own. A ridge still wants
  directionality — **directional cover is deliberately deferred**: it triples the cost
  of the cover query and needs a rule for what happens when a unit is shot from two
  directions at once.
- **Movement** keeps its categorical cost on the surface byte, so pathfinding stays
  cheap. Cratered and rubble ground get a surcharge on top, like churn already does.
- **Trees** are placed from vegetation density rather than from the `Forest` surface, so
  a thin wood is a few trees and a burned one is none.
- **The terrain shader** already classifies surfaces by vertex colour; density darkens
  and greens the ground, and char darkens it further. The terrain vertex **alpha channel
  is free** — it is the faction paint mask and is zero for all ground — so one 0–255
  field per vertex can ride in it without changing the vertex format. That is where
  vegetation density goes, and it is what lets the shader vary canopy dapple and wind
  strength by how thick the wood is. A continuous field interpolates across a cell
  boundary safely, unlike a categorical id.

## Determinism, which is the whole risk

`MiVic.Core` has no floating point in simulation state, and this change does not get an
exception:

- Attribute generation is a pure function of the height map, the seed and the grid.
- Aspect and landform come from integer height differences, not from `atan2` over
  floats.
- Fire spread, regrowth and crushing are integer steps driven by the tick. Any
  randomness comes from the project's fixed PCG32 stream in a fixed order, never from
  iteration order over a dictionary.
- **The attribute array goes into `StateHash`.** A field that is not hashed is a field
  two machines can disagree about without anyone noticing.
- Every one of these mutations moves the golden hashes. That is expected and each move
  gets recorded in the test that holds them, with which change moved it.

## The tests this needs

The lesson of this project is that a feature which exists and never happens is invisible
to every kind of test except one that **counts**. Three bugs so far — the volcano line,
the sand band, the snow line — and the answer each time was the same test. So:

- **Generation counts, per field.** Every landform appears on the standard map. Every
  aspect band appears. Vegetation density is not a single value across the map; woodland
  is denser than plain.
- **Fire behaves.** An explosion in dry vegetation starts a fire; the fire spreads to
  more than one cell over some number of ticks; burned cells lose vegetation; a fire
  stops when the fuel runs out. Two runs of the same seed produce the same fire.
- **Regrowth is bounded.** Vegetation recovers at the documented rate and never exceeds
  its cap; a `Burned` cell recovers more slowly than a merely cleared one.
- **Cover follows.** A cell with vegetation shelters infantry; the same cell after it has
  burned does not.
- **Attributes are hashed.** Changing one attribute bit changes the state hash.

## Order of work

Each step is independently verifiable and lands on its own:

1. The word itself: `TerrainAttributes`, generation of `Vegetation` and `Moisture`,
   hashing, and the counting tests.
2. `Aspect` and `Landform` from the height field, with the counting tests.
3. Cover derived from attributes rather than looked up, with tests. Movement surcharges
   for cratered ground.
4. Fire: ignition, spread, fuel, char. Regrowth.
5. Crushing by vehicles.
6. The client: density in the vertex alpha, the shader reading it, trees from density
   rather than from the surface, and flames and smoke over burning cells.
