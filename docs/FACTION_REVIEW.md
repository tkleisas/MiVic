# MiVic — faction review

A working review of each faction's units and tech tree, one faction at a time.
Σοβιετικοί is decided; the other two are still to come. Decisions that change
numbers the simulation reads are marked **decided, not yet applied** until the
code matches them.

## 1. Σοβιετικοί

The design intent, from `DESIGN.md` §§2, 3 and 5 plus the terrain decision in
`ART_PIPELINE.md` §3.1: **cheap, rugged, mobile standard designs; a few
irreplaceable advanced weapons; unbreakable morale; and the mud is theirs.**

### 1.1 Roster

Seven roles, two of them new. Standard units are cheap and simple (few parts, no
electronics); the one prototype is expensive, capped, and gated behind the design
bureau.

| Tier | Unit | Role | Cost tier | Notes |
|---|---|---|---|---|
| 1 | Πεζικό | line infantry | cheap | baseline; produced at the command centre |
| 1 | **Κομισάριος** | unarmed support | cheap | morale aura: nearby friendly units get an immovable morale floor — *implemented* |
| 2 | Άρμα «Τ-44» | workhorse MBT | cheap | light hull, wide tracks, sloped armour — the mud-mobile tank |
| 2 | **Κατιούσα** | rocket artillery | cheap | very damaging, very inaccurate, area saturation — *implemented* |
| 2 | Αντιαεροπορικό | anti-air | cheap | |
| 3 | **Ηλεκτροπυροβόλο «Τόξο»** | electro prototype | capped, expensive | arc/EMP; the Ηλεκτροτεχνία payoff |
| 3 | Αεροσκάφος | aircraft | expensive | few and powerful |

- **Κομισάριος.** The §3 counter to Western morale, made into a unit rather than
  an aura on a building: it is cheap, unarmed, and worth killing, so protecting it
  is a real decision. Its aura makes friendly morale immovable at the cost of
  initiative — the units it steadies react more slowly, which is the historical
  trade and the reason it is not a straight buff.
- **Κατιούσα.** The Soviet answer to having no heavy gun: cheap tubes on a truck,
  fired in salvos. Very high damage per volley over an area, but the salvo lands
  scattered, so it is a weapon against *formations and buildings*, not against a
  single moving tank. This is what makes the Soviet army want the enemy to come to
  it in the open. Implemented as two new weapon properties: a **scatter radius**
  (the salvo centre lands up to 26 m off target) and **splash damage** (everything
  hostile within 22 m takes full damage). The offset is a deterministic integer
  hash of the tick and the two slots, so a salvo scatters identically in a replay.
  First-pass numbers: 95 damage per salvo on a 90-tick cooldown at 260 m, against
  the howitzer's 60 on 60 ticks at 220 m — more than half again the punch per
  salvo, and it can miss entirely.
- **No generic howitzer.** The Σοβιετικοί artillery slot *is* the Κατιούσα.
  Western and Chinese artillery stay conventional, accurate and shorter-ranged,
  which is a real asymmetry rather than a stat difference.

Scope note: the slice guard in `DESIGN.md` §6 says ~5 units per faction. This
roster is 7 roles (6 combat + support), so either the guard moves to 7 or the
Κομισάριος folds back into an aura. Flagged, not resolved.

### 1.2 Design bureau: limited run and approval — **implemented**

`DESIGN.md` §2 states the signature mechanic; the simulation did not implement it.
Today a Σοβιετικοί factory cannot build a design the bureau has not proven:

1. **Factories cannot build a role directly.** For Σοβιετικοί, a factory-produced
   role additionally requires its design to be **approved** for that team.
2. **The design bureau runs the prototype first.** `SimCommand.ApproveDesign`
   starts a prototype job costing ×2 the unit's material, energy and water cost
   and taking ×2 its build time. One prototype runs at a time per team.
3. **On completion the design is approved** and the prototype is delivered as a
   real unit next to the bureau.
4. **Infantry is exempt**, because it is produced at the command centre. The rule
   is precisely "factory output needs a proven design".
5. **A licence bypasses approval**, exactly as it bypasses the tech ceiling.
6. **Only Σοβιετικοί are gated.** Κινέζοι and Δυτικοί build directly; the
   asymmetry is data, not a branch in the systems.

Implemented as `TeamState.ApprovedMask` plus prototype fields (all hashed), a
`SimCommandKind`, `SimWorld.TryApproveDesign`, the `CanBuild` gate,
`PrototypeSystem`, AI support so a Σοβιετικοί AI does not stall, and a
«Πρωτότυπο» section in the design-bureau panel. Golden hashes were regenerated;
`PrototypeTests` covers the gate, the run, the tier requirement, one-at-a-time,
the infantry exemption and the other factions. Consequence, and the reason it was
worth the code: army composition is decided minutes before the battle and
**cannot be pivoted** — you cannot switch from tanks to artillery on reaction,
because the new design needs a prototype run first.

### 1.3 Tech tree

Shape stays narrow and deep. The chain is the identity; the changes below give each
project a mechanic instead of a flat percentage, and fill Era IV. **Bold** entries
are new or reworked.

| Era | Project | Tier req | Effect | Status |
|---|---|---|---|---|
| I | **Βαθιά Μάχη** | 1 | mobility doctrine: the team's ground units pay **half** the mud and snow penalty | **built** |
| I | Επίπεδο 2: Τεθωρακισμένα | 1 | → tier 2 | built |
| I | **Παρτιζάνοι** | 1 | partisan support: team sight +25 % | **built** (mine reveal waits on mines) |
| II | Ηλεκτροτεχνία | 2 | **unlocks the Ηλεκτροπυροβόλο prototype** (arc/EMP), instead of +25 % damage | damage bonus built; the unit is not |
| II | Επίπεδο 3: Αεροπορία | 2 | → tier 3 | built |
| II | **Αναγνωριστικοί Δορυφόροι** | 2 | reconnaissance: team sight radius +30 % | **built** |
| III | Κυβερνητική (OGAS) | 3 | command automation: **+1 parallel slot per factory** | **built** |
| III | **Κόκκινος Ουρανός** | 3 | orbital strike ability: a targeted kinetic strike, long cooldown, heavy area damage (no longer +20 % armour) | **blocked** — needs abilities |
| IV | **Επίπεδο 4: Κόκκινος Λογισμός** | 3 | → tier 4 | **built** |
| IV | **AI Διοίκηση** | 4 | automated command: **+1 parallel slot** | **built** (reaction latency is not) |
| IV | **Έλεγχος Καιρού** | 4 | weather control: creates deep mud over a target area for a fixed number of ticks — the *rasputitsa* as a weapon, aimed at whoever has the worst ground pressure | **blocked** — terrain is generated once and never written to |
| IV | **Σωματιδιακά Όπλα** | 4 | unlocks the tier-4 Σωματιδιακό Άρμα, a capped prototype | **blocked** — needs a prototype cap |

Three new `TechEffect`s carry these: `Vision` (multiplies sight radius),
`TerrainResistance` (multiplies the mud and snow penalty, so two such projects
compound to a quarter) and `ParallelSlots` (adds slots per building, read by
`ProductionSystem` and shown as `+N` in the status panel). Effective ground
pressure is computed per team by `SimWorld.PathContextOf`, so a researched
Σοβιετικοί tank runs at 375 ‰ of baseline pressure in mud — half of its already
light 750 ‰. Era IV also needed its own `AdvanceTier` project: the ceiling of 4
existed with no content to reach it.

- **Βαθιά Μάχη** is the keystone change. It makes the terrain decision in
  `ART_PIPELINE.md` §3.1 a *Soviet* mechanic from the first research, rather than a
  generic speed bump: the faction that handles mud best is the faction that can
  research its way to ignoring it.
- **Έλεγχος Καιρού** is the payoff of the whole ground-pressure system. The Soviets
  weaponise the mud against an enemy whose armour has the worst ground pressure —
  the historical *rasputitsa*, deliberately invoked.
- **Κόκκινος Ουρανός** was giving +20 % armour while being named after kinetic rods
  from orbit. It becomes the strike it always claimed to be.
- The tree currently stops at tier 3, so the Σοβιετικοί ceiling of 4 was
  unreachable in content. Era IV fixes that; Δυτικοί ceiling 5 still has no content
  and needs the same treatment in their review.

### 1.4 What the tree needs before it can be built

The reworked tree is not one change: four of its entries depend on systems that do
not exist yet, so building them out of order would produce dead buttons.

| Project | Needs | Status |
|---|---|---|
| Αναγνωριστικοί Δορυφόροι | a `TechEffect.Vision` folded into `VisionSystem.SightRadiusMm` | **ready** — self-contained |
| Κυβερνητική (OGAS) | a per-team parallel-slot bonus instead of the faction constant | **ready** — self-contained |
| Επίπεδο 4 / AI Διοίκηση | an `AdvanceTier` 4 project and its content | **ready** — data only |
| Ηλεκτροτεχνία | the Ηλεκτροπυροβόλο unit kind to unlock | **ready** once the unit exists |
| Βαθιά Μάχη (mud doctrine) | the `TerrainLayer` — **now built** — plus a per-team mud-cost modifier | **ready** — terrain landed |
| Έλεγχος Καιρού | the `TerrainLayer` — **now built** — plus a way to write mud into it at runtime | **ready** once terrain is mutable |
| Κόκκινος Ουρανός | a targeted-ability system with a cooldown | **blocked on abilities** |
| Σωματιδιακά Όπλα | a tier-4 prototype unit and its cap | **blocked on the cap mechanism** |

Recommended order: **terrain first** — done. It unblocked Βαθιά Μάχη and, almost,
Έλεγχος Καιρού: the *rasputitsa* is now a real mechanic, so the two projects that
weaponise it are data plus one hook each. What remains genuinely blocking is the
ability system (orbital strike) and the prototype cap.

### 1.5 Open questions

- Slice scope: 7 roles per faction, or fold Κομισάριος into an aura?
- Παρτιζάνοι as a vision bonus, or as an actual partisan unit?
- Orbital strike and weather control are *targeted abilities* — the simulation has
  no ability system yet. Both need a command plus a cooldown in `TeamState`, which
  is a bigger change than a modifier and should be scheduled deliberately.
- Does the Κατιούσα scatter apply to buildings too, or only to units?

## 2. Κινέζοι

Intent, from `DESIGN.md` §§2, 3 and 5 plus the decisions: **volume**. Cheap,
individually weak units produced in enormous parallel batches; a tech tree that is
**wide and shallow** — many cheap upgrades, no high era; and, because they cannot
out-tech anyone, the alliance is their answer to technology.

### 2.1 Tech ceiling: 3 — **applied**

This was recorded in §7 of `DESIGN.md` as decided but not applied, and it was a
correction rather than a buff: §6 already lists Κινέζοι air as "many, cheap", but a
ceiling of 2 with **no tier-3 advance project at all** meant they could never build
an aircraft. Both halves are now fixed: `FactionProfile.Chinese.TechCeiling` is 3,
and `TechId.ChineseAdvance3` ("Επίπεδο 3: Αεροπορία", required tier 2, prerequisite
`ChineseAdvance2`) gives the tree a route to it.

The old test asserted the opposite — `TheChineseTreeHasNoThirdEra` — and has been
replaced by one that asserts tier 3 is reachable and tier 4 is not. A second test,
`NoFactionCanResearchPastItsCeiling`, now checks the structural invariant that bit
the Σοβιετικοί: a ceiling with no content to reach it is inert, and content above
the ceiling is unreachable.

What the ceiling still buys: they reach the aircraft era and stop. No Era IV, no
tier-4/5 hardware, and their ×0.70 research speed means the aircraft arrive late
enough that there is a window where the swarm does not exist yet.

### 2.2 Roster

Their roster should stay **the same roles at lower quality and higher volume** —
new Chinese-specific units would undercut the identity. The differentiation is the
production numbers, the ground pressure and one signature:

| Tier | Unit | Chinese version |
|---|---|---|
| 1 | Πεζικό | the cheapest infantry in the game; their core, not a screen |
| 1 | **Πολιτοφυλακή** (militia) | cheaper and weaker still, produced in bulk; the «Λαϊκή Πολιτοφυλακή» payoff |
| 2 | Άρμα | light, cheap, **narrow tracks** — worst ground pressure of the three, so mud is their enemy |
| 2 | Πυροβολικό | conventional and accurate, in contrast to the Κατιούσα |
| 2 | Αντιαεροπορικό | cheap, plentiful |
| 3 | Αεροσκάφος | now reachable, and meant to be fielded in numbers |

Two signature mechanics, one already built:

- **Άδεια Παραγωγής (licence production)** — *built*. They build a Soviet design at
  Chinese speed, which is how the alliance answers Western technology without
  breaking the "no high era" identity.
- **Αριθμητική Συνοχή (numerical cohesion)** — *not built*. Morale rises with the
  number of nearby friends, making the swarm steadier than its individual units
  look. This is the designed counter to Western per-unit morale and needs a hook in
  `MoraleSystem.Scan`.

Ground pressure 1250 makes mud punishing for them, which is deliberate — but they
are the AI ally in the vertical slice, so it is worth checking that the AI ally
does not bog down and stop participating.

### 2.3 Tech tree

Shape: wide and shallow. Many cheap projects, none of them an era. The ceiling is
now aligned with the content.

| Era | Project | Tier req | Effect | Status |
|---|---|---|---|---|
| I | Μαζική Επιστράτευση | 1 | +20 % production speed | built |
| I | Επίπεδο 2: Τεθωρακισμένα | 1 | → tier 2 | built |
| I | **Οδικές Μεταφορές** | 1 | +10 % movement speed — cheap and early | proposed |
| II | Λαϊκή Πολιτοφυλακή | 2 | +15 % armour | built |
| II | Τακτική Πλήθους | 2 | +10 % damage | built |
| II | Επίπεδο 3: Αεροπορία | 2 | → tier 3 | **built** |
| II | **Αντιαεροπορικό Δίκτυο** | 2 | +15 % anti-air damage | proposed |
| II | **Αριθμητική Συνοχή** | 2 | morale floor rises with nearby friendly count | proposed — needs the morale hook |

Every project is cheap and none is deep, which is the point: they are the faction
whose research is broad and shallow, and whose real scaling comes from parallel
slots rather than from quality.

### 2.4 Open questions

- Πολιτοφυλακή as a separate unit kind, or a cheaper infantry variant?
- Does «Αριθμητική Συνοχή» replace or stack with the faction morale floor?
- Ground pressure 1250 vs the AI ally: does the Chinese AI still reach the fight in
  mud-heavy maps?
- The production ordering decided in `DESIGN.md` §7 (Κινέζοι > Σοβιετικοί >
  Δυτικοί throughput, Δυτικοί rich but unproductive) is **still not applied**. It
  changes every cost and build-time number, so it should land as one deliberate
  change with the hashes regenerated.
