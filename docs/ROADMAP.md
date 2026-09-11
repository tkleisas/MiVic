# Roadmap

Where MiVic is going that it has not got to yet. Anything already built is stated as
built, so this is a list of work rather than a list of wishes.

---

## 1. A front end

The game boots straight into a skirmish. There is no menu of any kind, and no way to
save a match in progress.

**Wanted:** an intro screen offering

- **Συνέχεια** — continue a campaign in progress
- **Νέα εκστρατεία** — start a campaign
- **Μεμονωμένες αποστολές** — play a single mission, once it has been won in the
  campaign, so a player can go back to one they liked
- **Μάχη** — a skirmish, which is what the game does today
- **Επεξεργαστής χάρτη** — a map editor
- **Αποθηκεύσεις** — saving and loading a match
- **Πολλαπλοί παίκτες** — multiplayer, in the future

**What already exists:** `MissionCatalog` with three missions and their objectives;
`--mission <id>` and `--mission-list` to launch and list them from the command line;
`ReplayFile`, which records a match as a seed plus the commands issued and can verify
it reproduces, and `--record` / `--replay` / `--watch` around it. A replay is not a
save — it can only replay a match from its start, and cannot be resumed part-way.

**The interesting problems**, none of which are the screen itself:

- *What is campaign progress?* Missions are currently defined in code with fixed
  seeds. Winning one has to be remembered somewhere outside the process, which means a
  file, which means a format, which means a version.
- *Saving a match.* The simulation is fully deterministic and hashable, so a save can
  be as small as the seed, the scenario, the tick, and the command log up to that point
  — but only if every piece of state is reconstructible from the command log, which is
  the same property replays already need. A save is a replay with a start tick.
- *A map editor* means terrain, which is currently generated from a seed. Editing it
  means a map is data rather than a seed, and every hash that depends on generation
  changes shape.

---

## 2. The campaign

Missions want titles that state the idea, the way the three that exist do. The
proposed titles, in order:

| # | Τίτλος | Θέμα |
|---|---|---|
| 1 | Quantity has a quality of its own | Κινέζοι: mass production against quality |
| 2 | Our blood will drown them | Σοβιετικοί: weight of numbers, and what it costs |
| 3 | Who dares wins | a raid, or a gamble that has to come off |
| 4 | Come and take our weapons | defence of a position, against the odds |
| 5 | Multi Polar World | the alliance at its widest |
| 6 | Black cat, white cat — as long as it catches a mouse | a mercenary or a pact with someone unpleasant |

*(Two obvious typos in the list as given — "wii" and "dears" — are corrected above.
The themes in the right-hand column are a reading of the titles and want confirming.)*

**What already exists:** three missions — `m1_bridgehead` (Το Προγεφύρωμα),
`m2_ridge` (Η Ραχηλιά), and a third — each with a Greek title, a briefing, primary and
optional objectives (`DestroyStructures`, `HoldArea`, `AccumulateMaterials`,
`ReachTechTier`), a time limit, and a fixed seed and starting force. `VictorySystem`
resolves them and the HUD shows progress against the objectives.

**What a mission needs to be worth playing:** the objectives exist, but nothing yet
ties a title to a *situation*. "Come and take our weapons" and "Who dares wins" are
different games from each other, and the difference has to be in the map, the forces
and the objective kinds — not only in the briefing text.

---

## 3. Procedural sound

**Most of this is built.** No audio files ship with the game: every sound and all the
music is synthesised at load in `src/MiVic.Audio/SoundBank.cs`, from a fixed seed, so
a build sounds identical every run. There is a per-faction soundtrack (a minor-key
Soviet march, a Κινέζοι pentatonic song, a Δυτικοί blues shuffle) and a set of effects:
`RifleShot`, `TankGun`, `ArtilleryLaunch`, `AntiAirBurst`, `ExplosionSmall`,
`ExplosionLarge`, `Impact`, `EngineLoop`, `UiClick`, `Alarm`. Weapons already play
their own firing sounds and hits play an impact.

**What is missing, and what "procedural" buys:**

- **Variation between takes.** The same shot fired twice should not be the same
  waveform twice. With synthesis this is free — a second seed, or a parameter jittered
  per shot — and it is the single biggest thing that stops a firefight sounding like a
  loop.
- **Explosions that match what exploded.** There are two explosion sounds for a game
  with cratering artillery, salvo rockets, a tactical nuke, and a tank brewing up.
- **Distance and occlusion.** Attenuation exists; a blast on the far side of a hill
  should be dulled as well as quietened.
- **A battlefield bed** — distant guns, wind over the terrain, the low rumble of an
  army moving — so silence between engagements is not silence.
- **A nuclear detonation**, which is a different sound from any other in the game and
  currently plays the generic large explosion.

---

## 4. Limits on what a faction can field

Two constraints, both asked for, and they interact — worth designing together rather than
one after the other.

**Command capacity.** Today `UnitDefinition.MaxAlive` exists and is enforced, but exactly
one role uses it: the Σοβιετικοί Ηλεκτροπυροβόλο is capped at two, which is the prototype
rule working as designed. Nothing limits a *team*. The proposal is capacity granted by
structures — a command centre supports so many units, a factory fewer — so that fielding
more means building more, and the factions differ on a new axis: the Κινέζοι field more
because mass production is their identity, the Σοβιετικοί fewer but better. Capacity derived
from live structures means no new hashed state, and the enforcement point already exists in
the queue path, so the AI inherits it.

The decision that has to be made first: **the skirmish currently opens with 166 units a
side.** A cap in the usual 60–80 range would make the opening force illegal on tick zero, so
either the cap accommodates the opening — and is therefore decorative — or the opening
forces shrink. The second is a real change to the skirmish and the campaign.

And the AI has to build capacity when it is capped, or it stalls at its limit producing
nothing.

**Built — the ceiling, and not the power half below.** The opening does not shrink and production is
gated instead: a side over its ceiling may field no unit at all, and may keep building, so the
mobilisation every match opens with becomes a supply-limited war as it is spent. The figures, and the
arithmetic that puts the standard match over them from tick one:

| a unit costs | places | |
|---|---|---|
| a man | 1 | Πεζικό, Κομισάριος, Ρομποτικό Πεζικό, Μισθοφόρος |
| an armoured vehicle | 4 | Άρμα, Πυροβολικό, Αντιαεροπορικό, Κατιούσα; a Συλλέκτης is 2 |
| an aircraft | 6 | a Ντρόουν is 3, a Καταδρομέας 2, a Ηλεκτροπυροβόλο 6 |
| a structure | 0 | a building is what supports an army rather than what is in it |

| a building supports | places | |
|---|---|---|
| Κέντρο Διοίκησης | 200 | the headquarters: the only structure whose whole purpose is an army |
| Εργοστάσιο, Πυρηνικός Σταθμός | 60 | a yard supports the armour it makes; a reactor is the largest industrial site outside a headquarters |
| Σταθμός Παραγωγής, Γραφείο Σχεδιασμού | 30 | they run a base rather than an army |
| Πυροβολείο, Αντιαεροπορικό Πυροβολείο, Σταθμός Ραντάρ | 0 | a gun is not a headquarters and a radar is not either |

The four buildings a skirmish starts every side with are worth 320, which is **272 for the
Σοβιετικοί** (850 ‰), **320 for the Δυτικοί** (1 000 ‰) and **368 for the Κινέζοι** (1 150 ‰) — mass
production stated as a number of places, which is the third axis the factions differ on. The opening
force is 82 infantry, 42 tanks, 14 artillery, 14 anti-aircraft mounts and 14 aircraft: 82 + 168 + 56 +
56 + 84 = **446 places**, so every side opens over its own ceiling by 174, 126 or 78 and the rule is
in force before a shot is fired. Nothing new is hashed: the ceiling is a sum over the buildings a team
owns, asked at the moment it is asked, exactly as the power ledger is derived every tick.
`tools/probe/capacity.probe` is the transcript — refused with `λείπει δυναμικότητα 174`, a yard
ordered and raised *while* over the ceiling (272 → 323), and the same order accepted once the army has
been spent (446 → 231 places) — and `CapacityTests` pins each clause, including the one that matters:
**a side over its ceiling can still raise its ceiling.** The AI buys the same ceiling, and buys the
generation before the load: a base whose energy rate goes negative stops building altogether, and the
power plant queued to fix it is in the queue the deficit has just stopped, so a yard is only ordered
when the rate would still be sound with a plant shot away.

**Power.** Energy exists as a stockpile that accrues and is spent on construction, with
power plants and a nuclear plant generating it. There is no persistent *draw*: nothing a
structure owns costs anything to keep running. The proposal is a per-structure draw, with
the command centre carrying a built-in generator so that a minimal base never blacks out —
which sidesteps the death spiral where losing your power plant switches off the ability to
rebuild it. When generation falls short of draw, the stockpile drains, and at zero the base
**browns out**: systems shut down in a documented priority order — detection first, then
defensive weapons, then production speed.

Two things to note. The economy needs rebalancing if this lands: income is a few units a
tick, and a base with five structures drawing one or two each would spend its whole income
on standing still. And **the payoff arrives with the defensive structures** — turrets, AA
guns, radar — which do not exist yet. What can be switched off today is production and the
command centre's dish, and a power system with nothing to brown out is a number rather than
a mechanic.

**Generation is going to be plural, and the variants are faction and terrain specific.**
Hydro plants must stand near water, which makes the ground under a power plant a decision
rather than a formality — and it is the first building whose placement the terrain work
actually constrains. Solar is available to the Κινέζοι and the Δυτικοί and not to the
Σοβιετικοί, which leaves the Soviets on nuclear and thermal: heavier, dearer, and reliable,
which is a fair description of how they already play.

One consequence worth having deliberately: solar is the only generator whose output the
weather could take away, and **weather control belongs to the faction that cannot build
solar panels at all.** A Soviet weather ability that puts cloud over a Δυτικοί solar farm
is a better use of that ability than anything it does today, and it makes the asymmetry
bite in both directions instead of reading as a list of who may build what.

Note also what hydro cannot be: the water in this game is a level rather than a body, so a
plant can be required to stand near it but cannot draw on a current, because there is no
flow to measure.

The dish is worth more than it looks here, incidentally: it is now a **visible** thing that
turns, so switching it off communicates itself without a UI widget, which is exactly what a
power system needs.

## 5. Detection, radar and stealth

A defensive structure has a firing range. It should also have a **detection** radius, and those are
two different numbers: you can only shoot what you can see. The effective engagement radius is
whichever is smaller.

**Radar coverage** is what makes that a system rather than a number. A radar structure projects
coverage over an area, and structures within it gain detection — so a radar is a force multiplier
for the guns around it rather than a lone sensor sitting in a corner. **Stealthy units are detected
at a smaller radius**, which is what makes stealth worth having: the Western powers get it at a
high tech tier, and it should mean something specific — a stealth aircraft inside radar coverage is
spotted at a fraction of the radius, and outside coverage it is nearly invisible until it is close.

**And the radar draws power, because it is active.** That closes a loop worth having: the power
system already proposes browning out detection *first*, and the radar's own draw is the same
mechanic from the other end. A strike that takes out a base's generation does not merely slow its
factories — it blinds its defences, and the guns around it go quiet at range while still shooting
anything that comes close. Counter-play follows: the radar is the thing to kill, and it is worth
killing precisely because it multiplies everything else.

Two consequences to design for rather than discover. **Vision should not become two systems** — the
fog of war already computes what a team can see from its units, so radar coverage belongs in that
same function, serving fog, acquisition and stealth detection together. And **coverage has to be
visible**: a range ring when a radar or a gun is selected, drawn from the placement-preview
machinery that already exists, because a radius a player cannot see is a radius a player cannot use.

## 6. Structural armour

Buildings should be **reinforced**, and reinforced unevenly: Σοβιετικοί structures hardest to
destroy, then the Δυτικοί, then the Κινέζοι. It is the same statement the faction table already
makes — unshakeable cohesion against bought-and-cheap mass production — expressed as a property of
the building rather than of the men inside it. The Soviet architecture is already poured concrete
with deep reveals; this is that made mechanical.

**Armour is not health, and the difference is the point.** Health is a larger pool, so a reinforced
building simply takes longer to kill. Armour is damage reduction per hit, which means a rifle
mostly stops mattering against it while artillery still works — and that is what makes a fixed
emplacement something you bring the right tool for.

**Use a percentage, not a flat subtraction.** A flat reduction makes small arms useless against
armour and turns every fight into a comparison of weapon classes; a permille keeps every weapon
relevant while making the heavy ones worth their cost, and it cannot produce the "immune to
rifles" case that a flat number invites.

**It composes with cover, and the composition has to be written down once.** Cover is *where a unit
stands* and armour is *what it is made of*; both are multipliers on the same damage path, so the
order they apply in, and the fact that the existing floor of one damage still holds, belong in a
single documented place. Getting two multipliers into one path without stating their order is how
a balance change becomes unreproducible reasoning six months later.

Worth deciding when it lands: whether armour stays a property of **structures** or extends to
vehicles. The request is about buildings, and vehicles already differ by health; extending it would
be a balance change of its own rather than a detail of this one. Also worth knowing that it shifts
mission balance — the AI's ability to break a Soviet base drops, and the campaign is already
untuned after the bases moved onto dry land.

**And the answer to that question is yes, with the opposite ordering — which is the whole design.**
Σοβιετικοί *structures* are the most reinforced of the three; Σοβιετικοί *vehicles* carry the least
armour of the three, on a par with the Κινέζοι and behind the Δυτικοί, and buy mobility with it.
Heavy where it does not move, light where it does. That is one sentence a player can hold in their
head, and it describes the faction better than any table of multipliers.

The point of the light vehicle is the **rasputitsa**, and most of the machinery for it already
exists. Ground pressure is already a per-role and per-faction figure, and it already sets what mud
costs a given vehicle; mud already exists as a surface; and weather control — a Σοβιετικοί ability —
already lays it. So the Soviet advantage in the mud season is not a new system: it is the numbers
those existing systems were always waiting for. A Western armoured push that meets a Soviet weather
strike should slow to a crawl exactly where Soviet light armour does not, and that is a faction
being played rather than a statistic being read.

Two notes. Western armour being heavy is only a weakness if the mud can reach it, so the balance
depends on weather control being usable on the offensive rather than only over one's own ground —
worth checking when this is tuned. And a Soviet vehicle that is light *and* cheap *and* faster has
to lose something else, or the mobility is free; the obvious place is survivability, which is what
the armour figure already says, so the temptation to soften it elsewhere should be resisted.

**Built.** Armour is a permille on the damage a hit does, per faction and per role, and it composes
with cover in one documented function that every damage path calls (`DamageRules`). The figures, in
permille and as *what a hit keeps*:

| | structures | vehicles | ground pressure | catalogue speed |
|---|---|---|---|---|
| Σοβιετικοί | **720** | 970 | 750 | same role figure as everyone |
| Δυτικοί | 840 | **850** | 1 100 | same role figure as everyone |
| Κινέζοι | 930 | 960 | 1 250 | same role figure as everyone |

The role figures multiply those and run both ways, which is what makes "a fact about the role rather
than about its owner" a claim the data can carry: a command centre is 850 because it is the biggest
building in the game, a nuclear plant 800 because a reactor is a containment ring and the power
plant it upgrades is a shed, a radar station 1 050 because the catalogue already called it
deliberately fragile, and an aircraft 1 100 because an airframe is not armour. A man on foot carries
neither figure: he is not plated, and his protection is the ground he stands on.

**The rasputitsa needed no new numbers, but it did need one that was doing nothing.** Ground
pressure was already per-faction and mud already cost a heavier mover more — except that nothing on
the *movement* path read the cost, so it steered a route and never set a speed, and a Σοβιετικοί and
a Δυτικοί column crossed the same bog at the same rate. Turning that cost into the per-tick step
(`TerrainLayer.SpeedPermilleAt`, read by `MovementSystem`) is the whole change: in mud a Σοβιετικοί
tank keeps 462 ‰ of its speed where a Δυτικοί one keeps 355 ‰, and on grass the two are identical —
which is the design, because a faction that is faster everywhere has not bought mobility, it has
bought a better tank. `tools/probe/mud.probe` lays the mud with the weather ability and measures it:
38.7 m against 23.2 m in ten seconds, same lane length, same order, same catalogue speed.

The opposite orderings are pinned as relations rather than as numbers in `ArmourTests`, and
`tools/probe/armour.probe` shows the same 45-damage shell arriving as 27, 32 and 35 on three
headquarters of one role. Both golden hashes moved; the mission and skirmish *starting* hashes did
not, because armour is a property of a role and an owner rather than a field of state.

## 7. Alliances that move

Alliances are currently fixed when the scenario is built. They should be **dynamic, and able to change
during a match** — which turns a table of who is on whose side into a mechanic, and a mechanic that
fits this game's fiction better than most: a coalition of convenience against a common enemy is the
whole premise, and the campaign titles already suggest it — *Multi Polar World*, and *Black cat,
white cat, as long as it catches a mouse*.

**What it requires, none of which is diplomacy itself:**

- **Hostility is asked, never cached.** Every decision — acquisition, a held target, a standing attack
  order, a bridge's owner, an ability's blast — has to ask the live question at the moment it acts.
  A cached ally set is a bug waiting for the first betrayal.
- **A held order must survive the flip correctly.** A unit ordered to attack someone who then stops
  being an enemy has to stop shooting them. The sensor chain already re-validates a held target every
  tick, so the machinery exists; the rule has to be stated and tested rather than assumed.
- **The alliance state belongs in the state hash.** It is simulation state the moment it can change,
  and this project has closed two holes of exactly this kind already — the production queues, and a
  field hashed only when non-empty.
- **Allied vision is a decision, and a good one.** Do allies share what they can see? If they do, an
  ally's radar lights your guns, which makes a coalition genuinely worth having — and it means the
  vision function has one more input rather than a second implementation. If they do not, say so, but
  the sensor chain makes the answer consequential either way.
- **The player has to be able to see it.** Who is allied to whom, changing when it changes. A
  betrayal nobody notices is indistinguishable from a bug.

**And the part that is actually design rather than plumbing:** who may propose an alliance, what it
costs, whether it can be refused, how the AI values it, and how it is announced. A scripted flip in a
mission is the cheap version and would already be worth having; a player-driven one is a feature of
its own. The AI's own ally-marching bug and the combat system's missing alliance check were both
found this week, which is a decent sign that the plumbing is worth getting right before the mechanic
is built on top of it.

## 8. Mission scripting, and maps that do not have three factions

A mission today is a seed, three base positions, unit counts, a time limit and a list of objectives.
That is enough for "destroy this" and nothing else. It cannot say *when* anything happens, which is
what a campaign is made of.

**Triggers are the missing layer**: a condition and an action, evaluated on the tick, in a fixed
order, deterministic like everything else. A trigger that fires once needs to remember that it fired,
which makes it state, which means it is hashed — the same rule the production queues and the alliance
state are subject to. The conditions worth having first are the ones a mission actually uses: time
elapsed, a unit entering an area, a structure destroyed, a count falling below a number, a flag set.
The actions are the vocabulary of a campaign: spawn units or structures, reveal ground, grant or
remove resources and technology, change an alliance, set or complete an objective, order a group to
attack or move, show the player a message, end the mission.

**A declarative list in the mission definition, not a scripting language.** Every mission then stays
data, replayable and hashable, and the missions in the repository stay readable to anyone editing
them. A language would be a project of its own and would buy nothing that a list of triggers does not.

**Unique characters and vehicles** need three things the archive does not have: a name attached to a
specific instance, stat overrides on one unit rather than on a role, and a rule about what happens
when it dies. The first two are small. The third is free — "if this unit dies, fail" is a trigger,
which is exactly the argument for building the trigger layer before building heroes.

### Built: the layer, and the slice of vocabulary it shipped with

**The trigger layer exists.** A mission carries a list of triggers — `MissionDefinition.Triggers` —
each one a condition and the actions it carries out, and `TriggerSystem` evaluates them **every tick,
in list order**, from `SimWorld.Step`, after everything that moves, fights or builds and before the
objectives. The list order *is* the order of the events: a trigger may raise a flag and its successor
may read it on the same tick, which is how a sequence of things that happen together is written as a
list of things that happen in order. Every trigger fires **at most once**.

**The vocabulary that shipped**, and what it cost:

- **conditions**: time elapsed · a count of a team's units inside an area · `StructuresLost`, how many
  of a team's structures have been destroyed (read off the loss ledger `DestroyStructures` already
  uses, so a rebuilt position does not un-do it) · `StructuresStandingBelow`, how many of a team's
  structures stand now, below a number, optionally of one role · a flag an earlier trigger raised. The
  two counting conditions read as a pair and their names carry their tense, because the same number
  means two different things to them: "they have lost two" is a ledger that starts at zero, and "fewer
  than two stand" is a count of the map that is already true of a side that never had two;
- **actions**: spawn units or structures at a place · reveal ground for a team · grant or remove
  materials, energy and water (one action with signed amounts: the arithmetic and the floor at zero
  are the same either way) · complete an objective · show the player a message · order a group of
  units to move to a place or to attack the nearest enemy to one · raise a flag.

**Deliberately not built yet**, and each one is a reason rather than an omission: changing an alliance
(§7 — alliances are not dynamic yet), heroes and named units (they need the three things above, and
they now have the third), the monster generators (§9), and ending a mission from a trigger (the last
two sections are a story whose ending is already the objectives' job).

**The fired state is hashed, and the rest of the layer is derived.** A trigger that fired once must
never fire again, so what each trigger remembers — *that* it fired, and *when* — is state, and it goes
in `StateHash` beside the objective progress and the production queues. What it does **not** need is a
field of its own: a reveal is stamped fresh every tick from the fired tick that is hashed, through the
same disc a unit's own eyes are stamped through, and a count like `StructuresStandingBelow` is
recomputed from entities the hash already walks. The distinction is the one the whole file turns on —
**what a system
remembers is hashed, and what it can recompute is not**, because hashing a derived number puts one
fact in the hash twice and makes the hash the thing that is wrong the day the two disagree. Both loops
are mixed with no header, exactly as the objectives are, so **a match that uses no triggers mixes not
one byte more than it did** — which is why the golden hashes of the skirmish and of the three campaign
missions did not move, and why 'm4_pass' below appended a new entry instead of regenerating three.

**One mission uses it, and it is the demonstration.** `m4_pass` — «Η Ενέδρα στο Πέρασμα» — is the
campaign's fourth mission: the Δυτικοί are reconnoitring a pass, two guns are already on the rock
above it, a warning comes twenty seconds in, the column walking into the pass springs the ambush (the
armour on the flank, the message, the flag), the flag is read on the same tick and the ambush is sent
in, and the guns falling is what authorises the counter-attack. It is fought by two sides and no ally,
deliberately: a demonstration mission is a script, and a script with a second army in it — the
campaign's Κινέζοι ally, played by the computer — is a mission whose second act depends on somebody
else's battle. `tools/probe/triggers.probe` is the transcript of it, and each trigger is watched
firing with the world changing under it.

**The failure this layer was most likely to ship is the one it now tests for, in both directions.** A
trigger that is authored and can never fire is the same bug as the volcano line above every cell and
the mud mechanic that was inert, so `TriggerSystem.Validate` refuses a script that waits on a flag
nothing raises, an objective nothing completes, a denial with no clock to be decided by, a condition
about a team the match does not declare — and the probe records that check, while
`EveryTriggerInTheShippedMissionFiresWhenItShould` counts the six triggers of the shipped mission and
asserts the tick each one fired on.

The mirror failure is a trigger that fires *before* it was meant to. `StructuresStandingBelow` counts
what is on the map rather than what has been lost — deliberately, because a remembered starting total
would be state and state is hashed — so "fewer than two of their guns stand" is already true of a side
that starts with one, or of a side that has not been given its guns yet, and the trigger fires on the
first tick where its author is looking at the opening seconds of the match. `UnitInArea` carries the
same trap for a unit that starts inside the circle; `TimeElapsed` (the clock starts at zero),
`FlagSet` (flags start clear) and `StructuresLost` (a ledger that starts at zero) do not. So the same
validator asks every condition of the world the mission *opens* in and reports the ones already
satisfied, and a trigger that means to fire at once — or that counts what an earlier trigger has just
spawned, which is the list-order dependency this layer was built with — declares it with
`DependsOnOpeningWorld`. `m4_pass`'s `first-gun` is that declaration: in the world the mission opens
in, no Western gun stands yet, and the two are put there by the trigger above it on the opening tick.

**The objectives are asked the same question of the same world, and one of the two answers is
fatal.** An objective is a win condition rather than a scene, so an objective the map has already
decided is the mission: decided *against* the player it cannot be won — a `DenyArea` whose circle the
denied team already stands in has failed on the check that first asks it, which is the worst thing
this whole layer can ship, because its author finds out by losing — and decided *for* them it is
handed over before the first tick, which is a hold the opening formation already meets, a stockpile or
a tier the side starts with, a structure count of zero. The two are reported as the different
complaints they are, and **neither has an acknowledgement**: unlike a trigger, an objective has no
reading in which being already satisfied is the design, so both are refusals and the author moves the
circle or changes the number. The question goes through `MissionSystem.Verdict`, which is the
evaluation the tick loop runs, so a validator cannot disagree with the game about what an objective
means. The same pass refuses an objective whose own evaluation reads a team the match does not
declare, which is a case two-faction matches made expressible: a `DestroyStructures` against an
absent faction can never be completed, and a denial of an absent faction can never be failed — it is
completed by its own deadline with the player having done nothing at all. None of the four missions
the campaign ships trips any of it, which is the point of the check and also the reason
`tools/probe/objectives.probe` runs against a purpose-built mission carried by `--objective-demo`.

### The objective the vocabulary was missing: denial

All four kinds of objective were things you *do to the enemy* — destroy, hold, accumulate, reach — and
none of them is **denial**: "the enemy must not achieve X". That is the shape a mission about getting
something out, and stopping it, is made of, and it was the half of the Operation Paperclip test case
that could not be written.

`ObjectiveKind.DenyArea` is the mirror of `HoldArea` and deliberately not its twin: holding is
something you keep doing, so the hold clock can be lost and started again, while denial is a fact about
what did or did not happen. It fails the **moment** the denied team has its units inside the circle —
the ones that got through got through — and it is completed by the deadline arriving with the area
still clear, which is why a denial with no deadline is refused by the script validation as an
objective nothing could ever satisfy. `m4_pass` hangs on it: the player wins by keeping four Δυτικοί
units out of the road behind the line until the clock runs out, and the objective's progress is the
high-water mark of the intrusion, so a player watching the panel sees "3/4 of them are through" and
knows the mission is one unit from lost.

`ObjectiveKind.Scripted` is its companion and the answer to "a conditional objective": an objective
that **no predicate in the world can satisfy** and that only a `CompleteObjective` trigger action
completes — the mission knows the ambush is broken, and the objective table does not. It is the one
kind that can be authored and never happen, which is why every shipped mission's scripted objectives
are checked against the triggers that complete them.

**The test case: Operation Paperclip**

A good mission to design the layer against, because it needs most of the vocabulary at once. Δυτικοί
agents must move scientists out of a remnant outpost to an aircraft and fly them away; the Σοβιετικοί
must stop it. It exercises a **non-player force** that is neither of the two player factions, a
**moving objective** rather than a place, an **extraction** as a win condition, a **timer** that
escalates, and an **asymmetric pair of objectives**: the West wins by getting the scientists out, the
Soviets win by preventing it.

**Two thirds of that is now built.** The layer exists and has the vocabulary this needs — the
extraction is a `DenyArea` over the aircraft's loading point (see below), the escalation is a time
trigger, and the asymmetric pair is one objective asked of each side. What is still missing is the
*non-player force*: the scientists are neither of the two player factions and are not an army, which
is the monster-generator work in §9 (a team in the match that belongs to nobody) rather than mission
scripting. A **moving objective** — an objective that follows a unit rather than sitting at a place —
is the other piece, and it needs a way to name an entity in mission data, which is what heroes need
too.

### Not every map has three factions

**Built.** A match declares its teams: `MatchRoster` says which team slots are playing, which faction
each one plays and which side each one is on, and the world holds the declaration it was built with
(`SimWorld.Roster`). Everything that needs to know asks it — the victory rule, the AI, the status
panel, the client's palette and labels, the probe — and nothing decides it by index any more.

What that bought, in the order the plan above asked for it:

- **participation rather than a fixed roster**: `ScenarioKind.Duel` (Σοβιετικοί against Δυτικοί, no
  ally) and `ScenarioKind.Rivals` (Σοβιετικοί against Κινέζοι, Δυτικοί absent), started with `--duel`
  and `--rivals`; `MatchRoster.Declare` for anything else, including a free-for-all. A mission declares
  its own match too — `MissionDefinition.Roster` — so the campaign's ally is a side the mission says it
  has rather than a column of the scenario builder, and a mission without one lays out two forces;
- **victory conditions that do not require destroying a faction that was never on the map**: the rule
  is now *the player's side has no enemies left*, asked of the sides the match declares, so zero, one
  and two enemies all resolve through one line. A team the match does not declare is not a side the
  check looks at — which is the same requirement the monster generator below has, and the reason both
  wanted this;
- **a HUD with a row per team that is playing**, labelled with the faction that team plays, and a
  licence panel that asks the match who the ally is instead of assuming team 1;
- **alliances that work in a two-team match**, including one with no ally at all, because the alliance
  is a side the match declares rather than a predicate over two team numbers.

Still open here, and deliberately so: alliances are fixed when the match is built (§7), so a match
still cannot *change* sides mid-game, and the roster is not yet part of the state hash for the same
reason — it cannot change yet.

## 9. Monster generators

A structure that produces hostiles on a cadence, belongs to nobody, and is hostile to everyone. The
classic third-party threat: it makes a patch of the map a place rather than a space, gives neutral
ground a reason to matter, and puts a pressure on a match that neither player controls.

**It fits this game's fiction better than it fits most.** The setting already has nuclear plants, a
lava surface with a damage rule, weather control that rewrites ground, and an alternate history in
which unpleasant things were done in remote places. A generator is an exclusion zone with something
still running inside it — no new fiction required, and the hazard machinery is already there.

**What it needs:**

- **Two kinds**, appended to the roster as always: the generator itself and what it emits. Perhaps
  more than one thing emitted, at different cadences, so a zone escalates rather than repeating.
- **A spawner system**, deterministic like every other: a cadence on the tick and the project's own
  generator for anything that varies, with the count hashed — a spawner whose state is not in the
  hash is a desync waiting to happen, and its state is the interesting kind (how many it has emitted,
  how long since the last one).
- **Neutral hostility, stated properly.** A generator on the neutral team is hostile to all three
  factions, which makes it the first real test of the alliance work: *"is this an enemy"* has to
  answer correctly for a team that is allied to nobody and at war with everybody. An undeclared team
  already answers that way — see §8 — so what is left is declaring the generator's own team in the
  match and giving it a side of its own.
- **Victory conditions that ignore it.** A faction that was never on the map, or one that exists only
  as an obstacle, must not be something the victory check requires destroying. **This is done**: the
  check walks the sides the match declares, so a team the match does not declare is never required of
  anybody, and a generator would be declared in it only if it were meant to be fought.
- **The AI has to cope.** Its emplacements will engage what comes at them, which is the right
  emergent answer. What it must not do is treat a generator as an objective worth an army, or be
  baited into a war of attrition against something that respawns.

**The design questions worth deciding before it is built.** Is the generator destructible — because
if it is, silencing a zone becomes a real tactical objective and the campaign can hang a mission on
it, and if it is not, the zone is a permanent feature of the map. Does it escalate over time, which
is a mission-pacing tool in disguise: a generator that grows turns a slow scenario into a race. And
who does it attack — everything in reach, or only what comes close enough to provoke it, which is the
difference between a hazard and a siege.

**Answered, and each one lands somewhere useful:**

- **Destructible is an attribute, not a rule.** Some zones can be silenced and some cannot, which is a
  property of the generator rather than of the mechanic. That means the roster needs an invulnerable
  kind of thing — and "cannot be killed" has to be honoured in the *same* place hostility is decided,
  so that acquisition never picks a target it cannot hurt in the first place. A generator a unit
  spends a minute shooting to no effect is worse than one it ignores.
- **No escalation for now.** Constant cadence. Worth noting that escalation is cheap to add later
  *if* it is written as a function of the tick rather than as accumulated state: a cadence derived
  from elapsed time needs nothing new hashed, while a spawn count that grows needs hashing like any
  other state. Leave the door open in the shape of the rule, not with a field.
- **What it attacks is definable** — filterable by unit class and by faction. So a zone can be
  indiscriminate, or aimed at one power, or blind to aircraft. **This is another input to the single
  hostility predicate** the alliance work is consolidating: *is this an enemy* is answered by
  alliance, then by the filter, then by reach and class. Which is the third reason in this document
  to do that consolidation first — it is the things alliances, neutrals and generators all plug into.

**The generator's parameters, which are three numbers and a kind:**

| | |
|---|---|
| **output** | what it emits — one kind |
| **interval** | how often it emits — **a time**, in seconds |
| **count** | how many it will ever emit, or nothing at all for unlimited |

One output kind per generator, so a zone that emits two things at two cadences is two generators at
the same place — simpler than a weighted table, and it composes the same way.

**The interval is a time, and a time in this simulation is a whole number of ticks.** The clock is
fixed at twenty ticks a second and contains no floating point, so storing ticks is not an
approximation of a duration — it *is* the duration, exactly, and it cannot drift the way a
"twelve-second" timer would in a game with a variable frame time. A designer asking for one and a
half seconds gets thirty ticks, which is the number the bridge already uses for one cell of deck.

What that costs is granularity: a cadence is a multiple of fifty milliseconds. That is a fine trade
for something nobody will perceive as off by a frame, and it is the trade the whole simulation
already makes — but it should be said once rather than discovered by someone asking for 1.7 seconds.

So: **ticks in the roster, seconds in the comment and in the interface**, which is the convention the
build times and the bridge's cadence already follow.

**Two notes on the count.** It is **state**: how many a generator has already emitted is per-instance
simulation state, so it belongs in the entity and therefore in the state hash — a spawner whose count
is not hashed is a spawner two machines can disagree about while every other number agrees, which is
the exact shape of the two holes closed this week. And **zero meaning unlimited** is already this
codebase's convention (`MaxAlive`), so the sentinel costs nothing and wants no explaining.

**The interval and the cap interact, and the interaction wants stating.** A generator emitting a
capped role will quietly stop once the team hits the cap, which is correct but invisible; and a
generator on the neutral team is subject to no cap at all, which is how a zone ends up breeding. Say
which of the two bounds a reader should expect to bite first, in the comment where the spawn happens.

## 10. A terrain and mission editor

**What a map should be: a seed plus a list of edits.** Not an authored height field, not a binary blob.
Terrain is already a pure function of a seed — heights, surface layer, volcanoes, forests, fords,
deposits, attributes, aspect, landform — and a designer who starts from generated ground and adjusts it
gets coherent terrain for free. An authored height field would mean authoring every one of those passes
by hand, including the ground-connectivity guarantee the pathfinder depends on, and it would be opaque,
undiffable and unreadable in a repository.

The edit list is data: raise and lower, paint a surface, place a structure or a unit, set a spawn, set
the roster, write the objectives and the triggers. `TerrainLayer.SetType` already exists and is already
used by bridges, weather control and the fixtures, so the mechanism to change ground is in place. What
does not exist is a persisted map.

**Why this is cheaper here than in most engines**, and it is the whole argument: missions are *already*
data (a roster, objectives including denial and scripted, a trigger list), the placement and validity
rules are *already* public and single-sourced (`TryPlanStructure`, `CanStandAt`, `IsBaseSite`), the probe
already proves the game can be driven and interrogated from outside, and replays already prove a mission
reproduces. **An editor is largely a UI over existing APIs rather than new engine work**, and every
validator it wants already exists — including `TriggerSystem.Validate`, which refuses a script that can
never fire.

**The design question that has to be answered first: does an edit re-derive, or does it freeze?**
Painting woodland onto a slope, or raising ground under an existing wood, means the derived passes are
either re-run — deterministic and coherent, but the author's own placements may be invalidated — or left
alone, which lets a map become internally inconsistent in ways the game's guarantees assume cannot
happen. Re-running is the honest default, because the guarantees live in those passes; the editor's job
is then to show the author what changed.

**What an editor must not do: become a second implementation of the rules.** Placement, connectivity,
base sites and script validity are all answered by the simulation, and an editor that answers them itself
will eventually disagree with the game — which is precisely the failure the bridge preview, the capacity
ledger and the path budget each had to be corrected for this month.

**Missing pieces, in the order that one blocks the next:** a mission *file format* and loader (missions
are still C# today), an edit list that survives load, re-derivation after a terrain edit, an authoring
UI, and a test-play loop that renders the author's mission and reports the validators to them. The
test-play half is nearly free already: `--mission <id>`, the probe, and the replay round trip cover it.

## Also outstanding, from the art and rendering work

Not on the list above, but open:

- **A shader for every ground surface.** Water and lava have their own animated
  shaders; grass and crops, mud, sand, snow, rock and ore do not. The plan is to
  classify the interpolated ground colour against the surface palette in a `Terrain`
  pixel shader and give each surface its treatment in one pass, with one draw call.
- **Canopy shading for woodland**, and tree geometry on top of it (in progress).
- **The lava shader has never been seen properly** — the terrain it sits on is outside
  the vision of any unit near it, and props and liquids are drawn under the fog.
- **Splash damage ignores cover.** Direct fire is reduced by trees; a rocket salvo is
  not. That may be right — artillery is *for* dug-in infantry — but it is currently
  where a call was put rather than a decision taken.
- **The shoreline is a staircase.** The sea's edge follows the coarse terrain cells;
  meshing the water from the height field instead would give it a finer edge.
