# MiVic — audit

An honest assessment of the project: what was verified to work, what is broken, what
is merely stale, and what is only suspected. It exists because the rest of the
documentation in this repository is written in a confident, declarative register, and
several of those declarative claims turned out not to match the code. A specification
you cannot trust is worse than no specification, so this file records the evidence for
each claim and labels the claims that are only reasoned.

- **Commit audited:** `5e80107`
- **Scope:** the whole repository — `MiVic.Core`, `MiVic.Game`, `MiVic.Audio`,
  `MiVic.Map`, the tests, the probes, the CI workflows and the documentation.
- **Method:** build, full test suite, the client under `xvfb` + software GL, all 32
  probes driven the way CI drives them, `--inspect-models`, `--render-sfx` with
  waveform analysis, and a line-by-line read of the code.
- **Labels:** **Verified** = read the code and, where noted, reproduced the effect.
  **Suspected** = reasoned from the code but not reproduced.

## Verdict

A genuinely strong project with a real determinism architecture, an excellent probe
harness and disciplined resource handling — carrying one shipped-critical defect, a
handful of high-severity correctness and robustness bugs, and a systemic pattern of
documentation that describes intent rather than behaviour.

| Check | Result |
|---|---|
| `dotnet build MiVic.sln` | Clean — 0 warnings, 0 errors |
| `dotnet test MiVic.sln` | 698 passed (633 core, 42 audio, 23 map) |
| Probe suite, CI-style under `xvfb` | 32 / 32 pass |
| `--inspect-models` | 0 failures, 0 missing |
| `--selftest` on Linux | **Broken** — see A2 |

---

## A. Critical

### A1. The noise-based sound bank is DC, not noise — **Verified, reproduced**
`src/MiVic.Audio/SoundBank.cs:736`

```csharp
private static double Noise(Pcg32 rng) => ((rng.NextUInt() / (double)uint.MaxValue) * 2d) - 1d;
```

`Pcg32` is a mutable struct whose own documentation warns that calling it through a
copy "silently discards the advanced state" (`src/MiVic.Core/Random/Pcg32.cs:11-13`).
Every `Noise(rng)` call copies the generator, advances the copy and throws it away, so
all ~20 call sites return one constant per buffer. Nothing else in the file advances
the caller's state.

Reproduced by rendering the bank (`--render-sfx`) and measuring the waveforms:

| File | RMS | ΔRMS | Zero crossings |
|---|---|---|---|
| `rifleshot.wav` | 19190 | 83 | **0** |
| `explosionsmall.wav` | 12828 | 84 | **0** |
| `antiairburst.wav` | 16421 | 148 | 1 |
| `tankgun.wav` | 12622 | 109 | 6 |
| `alarm.wav` (oscillator) | 15304 | 2717 | 1324 |

A rifle crack with zero zero-crossings is an envelope-shaped DC thump. The README's
claim that effects are "rendered from noise and oscillators" held for only half a
sentence.

## B. High

### B1. `--selftest` is broken on Linux and is never run by CI — **Verified, reproduced**
`src/MiVic.Game/SelfTestReport.cs:28,230` P/Invokes `kernel32.dll` with no
`OperatingSystem.IsWindows()` guard, unlike `ReplayTool.cs:32` and
`AudioExporter.cs:203`, which do guard it. On Linux it dies with
`DllNotFoundException`, written only to `bin/.../crash.log`.

Even with that fixed it reports `RESULT: FAIL`, because
`WindowTitleProbe.ReadFromSdl` (`WindowTitleProbe.cs:66-77`) only searches for
`SDL2.dll` on Windows paths, so `window title (sdl): <unreadable>` and
`windowTitleMatches: false`.

`.github/workflows/ci.yml` runs probes and `dotnet test` but never `--selftest`, so
neither failure is caught. The README leans on `--selftest` as the automated
verification for the Greek-text fixes, the symbol coverage, the health bars and the
performance numbers.

The run also surfaced a number the performance section does not mention:
`fog mask rebuild: 14.7 ms average, 19.1 ms worst`, every ten simulation ticks. Some
of that is the software rasteriser's texture upload, so the absolute value deserves
caution — but the cost is not accounted for anywhere.

### B2. Recycled entity slots leak stale state — **Verified**
`SimWorld.Spawn` (`src/MiVic.Core/Sim/SimWorld.cs:619-689`) sets ~23 fields and omits
`ConstructionTicksRemaining`, `ConstructionTicksTotal`, `RevealedUntilTick`,
`DistanceTravelledMm`, `SpawnedCount` and `NextSpawnTick`. `Despawn` (`:904-926`)
clears only `Alive` and `Generation`, and `_freeSlots` recycles the slot.

The consequence is observable, because `ConstructionTicksRemaining > 0` means
"building site":

- no vision — `VisionSystem.cs:245-249` returns `SensorRefusal.UnderConstruction`;
- no economy — skipped at `EconomySystem.cs:89-92`;
- no supply cost — skipped at `CapacitySystem.cs:99`.

A tank spawned into a slot last used by an unfinished structure is blind, free and off
the books. A stealth unit can inherit a future `RevealedUntilTick`; a
`DerelictFactory` inherits a spawn count and cadence. Peers agree on the wrong value,
so the state hash cannot see it.

### B3. The state hash is not the complete fingerprint it claims — **Verified**
`StateHash.cs` states that "every field that can influence future ticks must be folded
in here". It omits:

- **the path waypoints** — only `PathLength`/`PathCursor` are mixed
  (`StateHash.cs:229-230`), though `MovementSystem.cs:142` walks `_pathCells`;
- **`Entity.MoraleTargetRaw`, `NeedsPath`, `PathFailures`** (`Entity.cs:84,90,93`),
  all mutable and all gating later behaviour;
- **pending command payloads** — only `PendingCommandCount` is mixed
  (`StateHash.cs:28`).

`DeterminismTests.StateHash_ChangesForEverySimulatedField` exercises only ten fields,
so nothing enforces coverage. The comment at `StateHash.cs:143-150` claiming explored
fog is "recomputed" is factually wrong: `VisibilityGrid._explored` is monotonic memory.

### B4. The glTF parser trusts untrusted input — **Verified**
`src/MiVic.Game/Rendering/Gltf/GltfLoader.cs`:

- **no cycle guard** (`:357-360`, `:524-527`) — `nodes[0].children = [0]` recurses to
  an uncatchable `StackOverflowException`;
- **unbounded, overflow-prone allocation** (`:847`) — `new float[accessor.Count *
  components]` from a file-supplied `int`; NORMAL and COLOR_0 have no count check;
- **unvalidated offsets** (`:930`) — `view.ByteOffset + accessor.ByteOffset` is summed
  with no in-buffer check, and `GltfBufferView.ByteLength` is parsed and never read;
- **arbitrary file read** (`:485-486`) — a `"uri": "../../../../etc/passwd"` buffer is
  read verbatim.

Related: `ModelCatalog`'s "the game always runs" fallback is not true
(`Data/ModelCatalog.cs:101,289` catches only `IOException|InvalidDataException|
NotSupportedException`). The loader throws `JsonException` and `FormatException`, which
escape — one corrupt model ceases startup instead of falling back to procedural
geometry.

### B5. Menu and pause never refresh previous input state — **Verified**
`MiVicGame.Update` returns early for the menu (`MiVicGame.cs:1153-1160`) and for pause
(`:1042-1055`) before the only refresh of `_previousKeyboard`/`_previousMouse` at
`:1261-1263`. The key handlers at `:1087-1113` run earlier and `Pressed()` (`:6311`)
compares against the frozen state, so while a key is held it fires every frame. Holding
F11 in the menu calls `ToggleFullScreen()` → `_graphics.ApplyChanges()` at frame rate.
`MiVicGame.Editor.cs:131-138` fixed this exact bug, with a comment describing the same
symptom, and the fix was never applied to the other two early returns.

### B6. Two more synthesis defects — **Verified**
- `Instruments.cs:232-239` — `PcgNoise.NextSigned()` shifts `_state >> 33` and divides
  by `1UL << 32`, so its range is `[-1, 0)`. It can never return a positive value;
  snare wires and tambourine carry a permanent −0.5 bias.
- `ChipSynth.cs:98-99,260-267` — the buffer is `durationSeconds + Release` but sustain
  runs to `Attack + Decay + duration`, so the release is truncated whenever
  `Attack + Decay > Release`. Brass and Pluck never render their release and end at
  full sustain, which is an audible click on every pitched note.

## C. Medium

| # | Finding | Location | Status |
|---|---|---|---|
| C1 | `Fix32` multiplication mis-rounds negatives (adds `-HalfRaw` then floors); `-1.0 × 1.0` gives raw `-65537`, not `-65536`. Contradicts its own documented contract and the sibling `DivRoundToInt`. | `Numerics/Fix32.cs:99-104` | Verified |
| C2 | `CountOf` counts jobs in dead buildings' queues and enemy queues against `MaxAlive`. | `SimWorld.cs:1475-1498` | Verified |
| C3 | `ReplayFile.Load` pre-sizes a list from a file-supplied count capped at 100,000,000, and reads an unbounded string. | `Replay/ReplayFile.cs:212-237` | Verified |
| C4 | `ReplayForward` silently drops an unsorted log instead of refusing it. | `Replay/ReplayFile.cs:377-394` | Verified |
| C5 | Map writers use unchecked `width*height*4`, reachable from the documented `scale=` probe option; `Math.Clamp(x, 40, palette.EventAlpha)` throws for a legal `EventAlpha < 40`. | `RasterCanvas.cs:26`, `PngMapWriter.cs:80`, `MapSceneBuilder.cs:964-967` | Verified |
| C6 | `WavWriter.ReadCore` loops forever on a malformed `fmt` chunk (`Position += size - 16` with `size < 16` seeks backwards). | `Audio/WavWriter.cs:96` | Verified |
| C7 | `Score.Load` leaks `FormatException`/`InvalidOperationException` past its `ScoreException` contract. | `Audio/Score.cs:318,498-503` | Verified |
| C8 | A failed save on exit is swallowed with no log, notice or exit code. | `MiVicGame.cs:5671` | Verified |
| C9 | `_scoreDirector` is never disposed; seven GPU meshes are never disposed. | `MiVicGame.cs:6317-6341` | Verified |
| C10 | `_live` accounting drifts in the projectile/particle ring buffers, then pins at capacity. | `ProjectileSystem.cs:204-217`, `ParticleSystem.cs:1005-1016` | Suspected |
| C11 | Energy may be charged twice in a deficit (power shortfall drain plus per-structure load). | `PowerSystem.cs:178-186`, `EconomySystem.cs:163-187` | Suspected |
| C12 | The A\* binary heap may overflow its fixed `cellCount+1` array; `Push` is unbounded and there is no decrease-key. | `Pathfinding/PathFinder.cs:46-47,302-306` | Suspected |
| C13 | `MapSceneBuilder` centres a trigger mark on the last spatial action but sizes it with the largest radius. | `MapSceneBuilder.cs:562-585` | Verified |
| C14 | `MapTrails` sizes a `stackalloc` from an unvalidated public constructor argument. | `MapTrails.cs:70-84,173` | Verified |

Also verified and lower impact: `SaveScreenshot`/`SaveMatch` do unchecked file I/O;
production can throw at entity capacity where structure placement correctly refuses
(`SimWorld.CanProduce` lacks the `AliveCount >= Capacity` guard); `PrototypeSystem`
delivers with no placement validation; `RemoveJobAt`/`RemoveLastJob` lack bounds
checks; `MatchRoster.Declare` does not validate the `Faction` enum; malformed JSON
shape leaks `KeyNotFoundException` from the map and mission loaders.

## D. Documentation drift

The README's status table does not match the client's own output.

| README claim | Measured reality |
|---|---|
| Tests: 385 (368 core, 17 audio) | 698 (633 core, 42 audio, 23 map); `MiVic.Map.Tests` is omitted entirely |
| "374 determinism, terrain, placement and maths tests" | 633 in `MiVic.Core.Tests` |
| Instanced draw calls: 38 | 1094, from `--selftest` |
| Models imported: 34 generated | 31 imported; 49 model entries and 57 `.glb` files on disk |
| 200 ticks allocate 6 MB, three gen-0 collections | ~11.8 MB equivalent from 51.9 MB / 876 ticks and five gen-0 |
| Roadmap "Next: make the AI respect fog of war" | `AiSystem.IsAttackable` already gates mobile targets on `IsInSight`/`IsHiddenFrom` (`AiSystem.cs:990-1016`); only structure locations are deliberately global |

The pattern is not isolated. The prose is written as intent at the moment of writing
and is not revisited, so a declarative claim in a comment or a doc should be treated as
something to verify rather than as a specification.

## E. Process

- **Floating dependency versions.** `MonoGame.Framework.DesktopGL` and
  `MonoGame.Content.Builder.Task` are `Version="3.8.*"` while the tool manifest pins
  `dotnet-mgcb` to `3.8.5.1`, so a build is not reproducible and the framework and its
  content tool can drift apart silently.
- **Tests do not roll forward; the game does.** `MiVic.Game.csproj` sets
  `<RollForward>Major</RollForward>` but the test projects do not, so on a machine with
  only the .NET 10 runtime — which the README blesses — `dotnet test MiVic.sln` fails
  outright.
- **CI's probe launcher is a brittle grep** over each probe's comments to guess its
  flags. It silently drops `--rivals` for `two-faction*.probe` and cannot express
  `--editor` or `--seed`. The probes pass either way today, so the defect is latent.
- **Probes write into the real user profile.** `--profile` is optional and CI does not
  pass it, while a probe that wins a mission writes campaign progress. The roadmap's
  rule that a probe must never touch a real player's campaign is a convention, not an
  enforced one.

## F. Maintainability

- `MiVicGame.cs` is 6,424 lines (7,524 across partials) and owns the world, camera,
  renderer, model catalogue, HUD, ImGui, audio, particles, projectiles, previews,
  control groups and the self-test. `LoadContent()` alone is ~519 lines, including a
  ~25-branch camera chain and a ~35-line nested-ternary simulation constructor.
- `SimWorld.cs` 3,354 · `ProbeRunner.cs` 4,947 · `MapSceneBuilder.cs` 1,314.
- `SelfTestReport.Write` takes **35 positional parameters**, many of the same type.
- `WorldPosConverter`/`MatchRosterConverter` are duplicated verbatim between `MapFile`
  and `MissionFile`; the probe and the game duplicate the render-target setup.
- Dead code: `UnitCatalog.DamagePerTick`, `Fix32.FromDouble`, `SpawnerSystem.Configure`,
  `ParticleSystem.Clear` (uncalled, and it resets only half its counters).

## G. What is genuinely good

- **The determinism work is real.** Integer millimetres and Q16.16, a PCG32 with hashed
  state, integer trigonometry with an initialisation hash, and golden-hash tests. A
  deliberate hunt for float, RNG or dictionary-iteration leaks into simulation state
  found none.
- **The probe harness is the best thing here.** `IProbeHost` makes the probe call the
  client's own drawing, animation and picking code rather than reimplementing it, and
  32 scripted probes exercise the real renderer in CI. All 32 pass.
- **Resource hygiene is mostly careful.** Instance buffers grow instead of thrashing,
  the per-frame `Collect*` paths are allocation-free, and `SimBridge` (2,055 lines) has
  no per-frame collections or LINQ at all.
- **Culture and encoding discipline.** Invariant culture throughout the diagnostics;
  the PNG and WAV encoders were checked byte-for-byte and are correct.
- **An honest engineering culture in places.** Several comments document a bug that was
  fixed, with its symptom; the catches are filtered and justified; there is not a single
  `TODO`, `FIXME` or commented-out block in the whole source tree.

## H. Limit of this audit

Everything in sections A–D is verified by reading the code; A1, B1 and B5 were also
reproduced at runtime. C10, C11 and C12 are reasoned from the code and are explicitly
not reproduced. Nothing here is a security assessment of the network layer, because
there is no network layer yet.

## I. Fix log

Every finding above was addressed in the same pass, in priority order. Each row names the
fix and how it was verified.

| # | Fix | Verified by |
|---|---|---|
| A1 | `SoundBank.Noise` takes `Pcg32` by `ref`, so the generator advances and the bank is noise | Re-rendered `--render-sfx`: `rifleshot.wav` went from **0** to **1470** zero-crossings and ΔRMS 83 → 4196; audio tests pass |
| B1 | `SelfTestReport.AttachConsole` guarded by `OperatingSystem.IsWindows()`; `WindowTitleProbe` reads the SDL window pointer and the Linux/macOS SDL library; a `--selftest` job added to CI | `--selftest 600` on Linux now exits **0** with `window title matches: True` and `RESULT: PASS` |
| B2 | `SimWorld.Spawn` clears the whole entity (`e = default`) and restores the generation | Full suite; `DeterminismTests.StateHash_ChangesForEveryEntityField` added |
| B3 | `StateHash` folds in `MoraleTargetRaw`, `NeedsPath`, `PathFailures`, `ConstructionTicksTotal` and the route's own waypoints | New reflection test fails if any `Entity` field is ever unhashed; generator fields pinned separately |
| B4 | glTF loader: node-depth (cycle) guard, bounded accessor counts, validated offsets and buffer ranges, bounded data URIs, path-traversal check, `JsonException`/`FormatException` converted to `InvalidDataException`; `ModelCatalog` catches every non-OOM loader failure | Fed a cycle, malformed JSON, a truncated accessor and a negative offset to `--inspect-models`: all four refused cleanly, no crash; shipped models still load (`failures=0`) |
| B5 | `RememberInput` helper called on the menu and pause early returns, and by the editor path | Build + probes; the held-key-per-frame path is gone |
| B6 | `PcgNoise.NextSigned` reads the top 32 bits; `ChipSynth` buffer is `Attack + Decay + duration + Release` | Audio tests pass; the bank re-renders |
| C1 | `Fix32` multiplication rounds with a truncating division, as its header documents | New `Multiplication_RoundsNegativesAwayFromZero` theory (6 cases) |
| C2 | `CountOf` skips dead buildings and other teams' queues | New `AnEnemysQueueDoesNotCountAgainstTheCap`, `ADestroyedBuildingsQueueStopsCounting` |
| C3 | `ReplayFile.Load` bounds the command count by the remaining stream bytes and caps the mission-id string | Full suite; replay round-trip in `--selftest` |
| C4 | `ReplayFile` refuses an out-of-order or negative-tick log; `EnqueueRecord` converts a command refusal to `InvalidDataException` | Full suite |
| C5 | `RasterCanvas` caps its side at `MaxSide`; `MapSceneBuilder` clamps against `Math.Min(40, EventAlpha)` | New `AnOversizedCanvasIsRefusedRatherThanAllocated`, `AnEventMarkWithNoAlphaDoesNotTakeThePictureDown` |
| C6 | `WavWriter` refuses a `fmt` chunk shorter than sixteen bytes and a negative chunk size | New `AShortFormatChunkIsRejectedRatherThanReadForever` (this hung before the fix) |
| C7 | `Score` uses `TryGetInt32` and a `StringOf` helper, so a non-integer or non-string field is a `ScoreException` | New `ANonIntegerTempoIsRefused` |
| C8 | A failed recording or save is written to stderr and shown in the HUD instead of being swallowed | Build; reviewed |
| C9 | `_scoreDirector` and the six remaining meshes are disposed in `UnloadContent` | Build; reviewed |
| — | Probes no longer write into a player's real profile: `--probe` and `--selftest` default to a scratch profile under the temp directory unless `--profile` says otherwise | Build; probes run |
| — | CI's probe launcher reads the flags from the probe's own documented command line instead of a grep pattern that silently dropped `--rivals`, `--seed` and `--editor` | Extraction checked against all 34 probes |
| D | README status table, roadmap and command table corrected against measurement (see the README itself) | The measurements in section D above |

### Verification after the pass

| Check | Result |
|---|---|
| `dotnet build MiVic.sln` | Clean — 0 warnings, 0 errors |
| `dotnet test MiVic.sln` | **712 passed** (643 core, 44 audio, 25 map) |
| Probe suite, CI-style under `xvfb` | **32 / 32 pass** |
| `--selftest 600` on Linux | **PASS**, exit 0 |
| `--inspect-models` | 0 failures, 0 missing |
| Golden hashes | Regenerated once, deliberately, and annotated in the tests |

