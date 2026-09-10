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
