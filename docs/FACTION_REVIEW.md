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
| III | **Κόκκινος Ουρανός** | 3 | orbital strike ability: a targeted kinetic strike, long cooldown, heavy area damage (no longer +20 % armour) | **built** |
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

Two signature mechanics, both now built:

- **Άδεια Παραγωγής (licence production)** — they build a Soviet design at
  Chinese speed, which is how the alliance answers Western technology without
  breaking the "no high era" identity.
- **Αριθμητική Συνοχή (numerical cohesion)** — morale rises with the number of
  nearby friends, making the swarm steadier than its individual units look. It is
  the designed counter to Western per-unit morale, and the only bonus in the game
  that scales with *how many* units are present rather than how good they are. The
  bonus is +0.02 morale per friendly within scan range, capped at +0.20, so a large
  swarm is steady but never unbreakable.

### 2.2.1 Their most advanced weapons: robots and drones — **built**

The Chinese cannot out-tech anyone, so their *advanced* hardware is **automation**:
machines instead of people. This is the faction's identity stated as hardware — a
manufacturing giant with lagging technology does not build better weapons, it
builds weapons that do not need soldiers.

| Tier | Unit | Role | Mechanics |
|---|---|---|---|
| 3 | **Ρομποτικό Πεζικό** | automaton infantry | **no morale** — it cannot rout; **no water cost** (no crews to drink or feed); high energy cost |
| 3 | **Ντρόουν** | expendable aircraft | cheap, low damage, **no morale**, produced in numbers |

Why this is the right top end for them:

- **It answers their own weakness.** The Κινέζοι morale floor is 0.80 and their
  units are individually the weakest, so a bad engagement cascades. Automata are
  immune to the cascade — the one thing the swarm could not buy with numbers.
- **It is a direct counter to the Δυτικοί**, whose entire system is per-unit
  morale. Against robots, the Western advantage evaporates; what is left is an
  expensive army fighting a cheap one.
- **It costs them the resource that represents people.** Water is crews, food and
  cooling. Robots drink no water and eat no food, but they need *energy* — so the
  Chinese economy shifts from a balanced base to an energy-hungry one, which is a
  real build-order decision rather than a stat swap.
- **It stays inside their ceiling.** Tier 3 is their maximum, so their most
  advanced weapons are era III and there is no tier 4 for them. The ceiling still
  means something.

Built as `UnitDefinition.IsAutomaton` and `UnitDefinition.OnlyFor`: `MoraleSystem`
skips automata entirely (they never drift and never rout), `CombatSystem` gives them
the catalogue reload rate with no morale modifier in either direction, and
`UnitCatalog.IsUnlocked`/`BuildableBy` honour `OnlyFor` so no other faction can
field them. Air is now decided by `MovementClass.Air` rather than by the `Aircraft`
role, so drones fly, are ignored by terrain, and can only be engaged by anti-air.

**Not needed after all:** the two "unlock" projects (Ρομποτική Παραγωγή, Σμήνη
Ντρόουν) are unnecessary. Units gate on tech tier, not on named projects, so both
automata arrive with tier 3 like everything else in that era. A named-project
unlock would need a per-unit unlock effect that does not exist yet, and adding one
only for these two units would be machinery without a purpose.

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
| I | **Οδικές Μεταφορές** | 1 | +10 % movement speed — cheap and early | **built** (applies to units built after it) |
| II | Λαϊκή Πολιτοφυλακή | 2 | +15 % armour | built |
| II | Τακτική Πλήθους | 2 | +10 % damage | built |
| II | Επίπεδο 3: Αεροπορία | 2 | → tier 3 | built |
| II | **Αριθμητική Συνοχή** | 2 | +0.02 morale per nearby friend, capped at +0.20 | **built** |
| II | **Αντιαεροπορικό Δίκτυο** | 2 | +15 % anti-air damage | proposed |
| III | — | 3 | Ρομποτικό Πεζικό and Ντρόουν arrive with the tier; no project needed | **built** |

Every project is cheap and none is deep, which is the point: they are the faction
whose research is broad and shallow, and whose real scaling comes from parallel
slots rather than from quality.

### 2.4 Open questions

- Πολιτοφυλακή as a separate unit kind, or a cheaper infantry variant?
- Does «Αριθμητική Συνοχή» stack with the faction morale floor? (It does: it is
  added to the target, and the sum is clamped.)
- Ground pressure 1250 vs the AI ally: does the Chinese AI still reach the fight in
  mud-heavy maps?
- The production ordering decided in `DESIGN.md` §7 (Κινέζοι > Σοβιετικοί >
  Δυτικοί throughput, Δυτικοί rich but unproductive) is now **applied**, including
  `IncomePermille`. What is still missing from the Σοβιετικοί two-tier cost is the
  *cap* on advanced prototypes.

## 3. Δυτικοί

Intent, from `DESIGN.md` §§2, 3 and 5: **the best technology and the strongest
economy, undermined by greed**. Their units are the most expensive in the game by a
wide margin, their production is the slowest, and their morale is the only one that
breaks — the faction that should win on paper and loses on nerve.

### 3.1 Economy — **applied**

| | Value | Consequence |
|---|---|---|
| Parallel slots | 3 | the slowest factories of the three |
| Build speed | ×0.80 | even a slot that is running is slow |
| Unit cost | ×2.20 | *questionably expensive*: a Western tank costs 330 Π against 105 for a Κινέζοι one |
| Resource income | ×2.50 | they never run short of money |
| Research speed | ×1.00 | fastest ceiling, ordinary speed |
| Tech ceiling | 5 | the only faction with a route to era V |

The point of the split is that **money is not their bottleneck, factory time is**.
A Western player will routinely see a full treasury and an idle factory, which is
deliberate — and which means income and throughput have to be shown separately in
the interface or it reads as a bug.

### 3.2 Era IV and V — **built**

The ceilings of 4 and 5 existed with no content to reach them, which made them
inert: a faction cannot research past the last project it owns. Both eras now exist.

| Era | Project | Tier req | Effect |
|---|---|---|---|
| IV | Επίπεδο 4: Αυτόνομα Συστήματα | 3 | → tier 4 |
| IV | Αυτόνομα Συστήματα | 4 | +20 % production speed |
| IV | Προηγμένα Υλικά | 4 | +20 % armour |
| V | Επίπεδο 5: Διαστημική Επιτήρηση | 4 | → tier 5 |
| V | Διαστημική Επιτήρηση | 5 | +40 % sight radius |

`NoFactionCanResearchPastItsCeiling` now checks both directions — no project above
a ceiling, and no ceiling above the faction's own content — so this class of bug
cannot come back silently.

### 3.3 What is still missing

Their two signature mechanics are now **built**:

- **Contract units (μισθοφόροι)** — the best infantry in the game, and the only
  unit that has to be paid every tick. `UnitDefinition.WagePerTick` and a per-team
  wage bill; when the treasury cannot cover it, the contract lapses and the unit
  **down-tools** — it routs and will not fight until it is paid. That is not a
  morale penalty bolted on, it is the existing rout machinery doing exactly what it
  was built for.
- **Propaganda upkeep** — a recurring bill of one material per eight armed units
  per tick, set by `FactionProfile.PropagandaDivisor`. Funded, the army's morale
  baseline is **+0.10** above its floor; unfunded, **−0.20**. With a floor of 0.50
  that is the difference between an army that holds and one that is already halfway
  to breaking before the first shot.

Both are paid in `EconomySystem.PayUpkeep`, wages first — contract troops are the
first to notice an empty treasury — and the funded/unfunded flags are hashed, so a
replay reproduces the same collapse on the same tick. The status panel shows the
bill and says plainly which obligation went unpaid.

This gives the West the texture the weakness needed: the richest faction in the
game finally has somewhere for the money to go, and the greed that was only
narrative is now a per-tick bill.

### 3.4 Tier-4 and tier-5 capabilities: nukes and stealth — **nuke built, stealth not**

Two capabilities that belong to the high eras, and one prerequisite that makes them
a strategic commitment rather than a button:

| Capability | Era | Who | Prerequisite | Status |
|---|---|---|---|---|
| **Τροχιακό Πλήγμα** (orbital strike) | 3 | Σοβιετικοί | Κόκκινος Ουρανός project + design bureau | **built** |
| **Τακτικό πυρηνικό όπλο** | 4 | Σοβιετικοί **and** Δυτικοί | a **Πυρηνικός Σταθμός** | **built** |
| **Αποφυγή ανίχνευσης** (stealth) | 5 | Δυτικοί only | — | **built** |
| **Έλεγχος Καιρού** (weather control) | 4 | Σοβιετικοί | — | blocked on mutable terrain |

- **Tactical nukes are shared by the superpowers, not the West's alone.** Both the
  Σοβιετικοί and the Δυτικοί reach era IV; the Κινέζοι never do, because their
  ceiling is 3. That is the cleanest possible expression of the setting: the two
  empires that over-invested in the Cold War have the bomb, and the manufacturing
  power does not.
- **The nuclear plant is the interesting part.** Requiring a Πυρηνικός Σταθμός
  before the weapon exists turns it into a strategic commitment: you must build a
  large, expensive, obvious structure and *defend* it. Losing the plant takes the
  capability away with it, which makes it the first structure in the game whose
  value is not its income — and a target worth raiding.
- **A nuke does not distinguish friend from foe.** The orbital strike does, because
  it is aimed; a nuclear weapon is not. That is the whole tactical cost.
- **Stealth is the West's era-V edge** and the natural counterpart to few,
  expensive, excellent units: you cannot shoot what you cannot see. Built as
  `UnitDefinition.Stealthy` and the **Καταδρομέας** (fast, hard-hitting, fragile,
  Δυτικοί-only, era V). A stealthed unit is hidden from an enemy team until it
  fires — a shot sets `Entity.RevealedUntilTick` for 100 ticks — or until one of
  that team's units comes within 40 m. Hidden units are not targetable and are not
  drawn, so the mechanic applies to the simulation and the interface alike.

Abilities are data in `AbilityCatalog` and resolved by `SimWorld.TryUseAbility`,
which validates faction, era, prerequisite project, prerequisite structure,
cooldown and cost before applying damage in ascending slot order. `CanUseAbility`
is separate from execution so the interface can grey a button out for the right
reason, and it says which one in Greek. The support panel lists what the player's
faction can call in; a click on the button arms the ability and the next left-click
on the ground is the target.

**Έλεγχος Καιρού remains the one blocked capability**, because terrain is generated
once from the seed and nothing writes to it at runtime.

### 3.5 Roster

Same principle as the Κινέζοι: no new roles, better versions of the same ones. The
MBT is the best tank in the game, the self-propelled gun the most accurate, and the
air defence the longest-ranged — each at a price that makes losing one a disaster
and a morale hit on top. **Μισθοφόρος** is the exception: a new role, the best
infantry in the game, and the only unit with a wage.

### 3.6 Open questions

- Morale floor 0.50 with a possible −0.20 when propaganda is unfunded: is the rout
  cascade too punishing now that their units cost 2.2×? The floor and the penalty
  were designed separately and now stack.
- How does the nuclear plant interact with the existing energy economy? If it is
  also a power plant, the West's energy problem is solved by the same building that
  gives them the bomb, which may be too neat.
- Should losing the nuclear plant remove the nuke, or merely stop production of
  more? Removal makes the plant a raid target; stopping production makes it a
  one-time investment.
- Stealth needs a detection rule. Simplest: stealthed units are invisible beyond a
  short detection radius and are revealed for a few seconds after firing. Anything
  more elaborate needs the ability system anyway.
