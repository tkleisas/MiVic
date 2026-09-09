# 3D models

These files are **not** stored in the repository. Run the fetch script to
recreate the exact set:

```pwsh
pwsh ./tools/fetch-assets.ps1          # skip files that already exist
pwsh ./tools/fetch-assets.ps1 -Force   # re-download everything
```

Every model lands in a per-faction folder (`Soviet/`, `Chinese/`, `Western/`)
under the name the catalog expects, for example `Soviet/tank_heavy.glb`.

## Why they are not committed

The models are by **Quaternius** and are distributed under the
[Quaternius Asset License (QAL) v1.0](https://quaternius.com/license.html).
That licence permits use inside a finished game — commercial, no attribution
required — but forbids redistributing the assets themselves as assets. A public
repository containing the `.glb` files would do exactly that, so the build
fetches them instead. Poly Pizza lists several of these models as CC0; the QAL
is the stricter and therefore the governing statement.

If MiVic is ever published, the models may ship **inside the game build**; they
just must not be published as a standalone asset pack.

## Slots

| Role | Soviet | Chinese | Western |
|---|---|---|---|
| Infantry | `soldier.glb` | `soldier.glb` (same model, licence-produced kit) | `soldier.glb` |
| Tank | `tank_heavy.glb` | `tank_light.glb` | `tank_medium.glb` |
| Artillery | `tank_medium.glb` | `tank_apc.glb` | `tank_heavy.glb` |
| Anti-air | `turret.glb` | `turret.glb` | `turret.glb` |
| Aircraft | `aircraft.glb` | `aircraft.glb` | `aircraft.glb` |
| Command centre | `hq.glb` | `hq.glb` | `hq.glb` |

## Known limitations

- Models render in their **bind pose**. The character assets are T-posed and the
  vehicles' animations are ignored, because the loader reads static geometry
  only. Skeletal animation is future work.
- A missing file is not an error: `ModelCatalog` falls back to procedural
  geometry so the game always runs.

## Orientation

MiVic renders every unit facing **+X**. glTF models usually face **−Z**, so the
loader rotates a model whose longest horizontal axis is Z so that axis points at
+X. Tanks in this set are authored facing **−X** and need an extra half turn,
which is why `ModelCatalog` gives them `TankYaw = 180`.

To check any model, use the headless inspector — it loads models through the real
loader and reports their dimensions:

```pwsh
./MiVic.Game.exe --inspect-models
pwsh ./tools/inspect-model.ps1 src/MiVic.Game/Content/Models/Soviet/tank_heavy.glb
```

A tank whose long axis is not X, or an infantry model that is not taller than it
is wide, is a sign of a wrong rotation.
