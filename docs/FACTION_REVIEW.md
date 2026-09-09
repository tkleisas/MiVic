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
| 1 | **Κομισάριος** | unarmed support | cheap | morale aura: nearby friendly units get an immovable morale floor |
| 2 | Άρμα «Τ-44» | workhorse MBT | cheap | light hull, wide tracks, sloped armour — the mud-mobile tank |
| 2 | **Κατιούσα** | rocket artillery | cheap | very damaging, very inaccurate, area saturation |
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
  it in the open. Mechanically it needs two things the combat system does not have
  yet: a **scatter radius** (the salvo centre lands off-target) and **splash
  damage** (everything inside a small radius takes damage).
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
project a mechanic instead of a flat percentage, and fill Era IV.

| Era | Project | Tier req | Effect |
|---|---|---|---|
| I | **Βαθιά Μάχη** | 1 | mobility doctrine: the team's ground units ignore half of the mud and snow penalty |
| I | Επίπεδο 2: Τεθωρακισμένα | 1 | → tier 2 |
| I | **Παρτιζάνοι** | 1 | partisan support: infantry sight +25 % and mine cells are revealed |
| II | Ηλεκτροτεχνία | 2 | **unlocks the Ηλεκτροπυροβόλο prototype** (arc/EMP), instead of +25 % damage |
| II | Επίπεδο 3: Αεροπορία | 2 | → tier 3 |
| II | **Αναγνωριστικοί Δορυφόροι** | 2 | reconnaissance: team sight radius +30 %, fog cleared in a large radius around the command centre |
| III | Κυβερνητική (OGAS) | 3 | command automation: **+1 parallel slot per factory** |
| III | **Κόκκινος Ουρανός** | 3 | orbital strike ability: a targeted kinetic strike, long cooldown, heavy area damage (no longer +20 % armour) |
| IV | **Επίπεδο 4: Κόκκινος Λογισμός** | 3 | → tier 4 |
| IV | **AI Διοίκηση** | 4 | automated command: +1 parallel slot and shorter reaction latency |
| IV | **Έλεγχος Καιρού** | 4 | weather control: creates deep mud over a target area for a fixed number of ticks — the *rasputitsa* as a weapon, aimed at whoever has the worst ground pressure |
| IV | **Σωματιδιακά Όπλα** | 4 | unlocks the tier-4 Σωματιδιακό Άρμα, a capped prototype |

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

### 1.4 Open questions

- Slice scope: 7 roles per faction, or fold Κομισάριος into an aura?
- Παρτιζάνοι as a vision bonus, or as an actual partisan unit?
- Orbital strike and weather control are *targeted abilities* — the simulation has
  no ability system yet. Both need a command plus a cooldown in `TeamState`, which
  is a bigger change than a modifier and should be scheduled deliberately.
- Does the Κατιούσα scatter apply to buildings too, or only to units?
