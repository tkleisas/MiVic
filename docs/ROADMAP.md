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

### The test case: Operation Paperclip

A good mission to design the layer against, because it needs most of the vocabulary at once. Δυτικοί
agents must move scientists out of a remnant outpost to an aircraft and fly them away; the Σοβιετικοί
must stop it. It exercises a **non-player force** that is neither of the two player factions, a
**moving objective** rather than a place, an **extraction** as a win condition, a **timer** that
escalates, and an **asymmetric pair of objectives**: the West wins by getting the scientists out, the
Soviets win by preventing it. That last one is a kind of objective the game does not have — all four
existing kinds are things you do to the enemy, and none of them is *denial*.

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
