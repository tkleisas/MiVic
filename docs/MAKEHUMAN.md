# MakeHuman, for the cutscene figure

The briefing and debriefing figures are built by MakeHuman through **MPFB2**, not by hand.
This is how to install it, how to run the generator, and — more usefully — the API facts
that cost time to establish and are not obvious from the outside.

`tools/blender/build_makehuman.py` is the generator. It is the only file that needs to run;
everything else here is why it says what it says.

```bash
/home/tkleisas/blender/blender-5.2.2-linux-x64/blender --background \
    --python tools/blender/build_makehuman.py -- \
    --out artifacts/skinned \
    --hair short02 \
    --garment male_casualsuit05 \
    --garment shoes03
```

**Run Blender by absolute path.** There is more than one Blender on this machine, and the
one on `PATH` is not necessarily the one with the extension installed.

---

## 1. Installing it

Two separate things, in two separate places, and only one of them is the extension.

**The extension** — MPFB 2.0.17, from the Blender extensions platform. It lands in

```
~/.config/blender/5.2/extensions/blender_org/mpfb/
```

and *that* is where the code is: `services/`, `entities/`, and the rigs and poses under
`data/rigs/` and `data/poses/`.

**The asset pack** — the system assets, CC0, 280,737,770 bytes:

```
https://files.makehumancommunity.org/asset_packs/makehuman_system_assets/makehuman_system_assets_cc0.zip
```

It does **not** land next to the extension. It lands in a second root:

```
~/.config/blender/5.2/extensions/.user/blender_org/mpfb/data/
```

So there are two `mpfb/data` directories and only the second one has `clothes/` in it. This
cost an hour once: `ls .../blender_org/mpfb/data/clothes` returns nothing, and the
conclusion "the pack has no clothes" is wrong.

Use `files.makehumancommunity.org`. The mirror `files2.makehumancommunity.org` serves the
same file at roughly 17 KB/s, which is about ten minutes for the pack; the first host
served 281 MB in 28 seconds.

The pack is 287 MB unpacked and contains, as directory counts:

| | | | |
|---|---|---|---|
| `clothes` 20 | `hair` 10 | `skins` 23 | `proxymeshes` 7 |
| `eyebrows` 12 | `teeth` 6 | `eyes` 3 | |

Notice what is **not** there: there is no facial hair of any kind, and no poses beyond a
`t-pose.json` per rig. Both gaps are dealt with below.

MPFB needs to write logs and a cache under `~/.config/blender/5.2/mpfb/`. If it cannot, it
fails to register at all, and a sandbox that denies those writes looks exactly like a
broken installation.

### Where the documentation is

`script_samples/` and `docs/` in the `makehumancommunity/mpfb2` repository. Reading
`docs/services/humanservice.md` and `docs/services/targetservice.md` first would have
avoided most of a long detour. The API is also readable directly — it is Python, and it is
installed:

```bash
grep -n "def add_mhclo_asset" -A 12 \
    ~/.config/blender/5.2/extensions/blender_org/mpfb/services/humanservice.py
```

That is faster and more reliable than guessing at a signature, and the docstrings are
thorough.

---

## 2. The calls that matter, and the traps in them

This is the whole sequence, in order. The order is not a style choice; two of the steps are
silent no-ops if they come at the wrong time.

```python
HumanService.create_human()                              # 1. the basemesh
HumanObjectProperties.set_value("gender", 1.0, entity_reference=human)
TargetService.reapply_macro_details(human)               # 2. actually shapes it
HumanService.add_builtin_rig(human, "game_engine")       # 3. the rig, BEFORE the assets
HumanService.add_mhclo_asset(proxy, human, asset_type="Proxymeshes")     # 4. the body
HumanService.set_character_skin(mhmat, human, bodyproxy=proxy,
                                skin_type="GAMEENGINE")  # 5. the skin
HumanService.add_mhclo_asset(clothes, human, asset_type="clothes")       # 6. clothes
bpy.data.objects.remove(human, do_unlink=True)           # 7. drop the stand-in
```

### `gender` is 1.0 for male

`0.0` is female. This is the opposite of what `docs/services/humanservice.md` says in one
comment, and the difference is not subtle: 1.724 m against 1.586 m.

### Setting the macro properties does not shape anything

`set_value` stores a number. **`TargetService.reapply_macro_details(basemesh)` is the call
that "calculates the required targets from the current macro info, loads any missing
targets, and sets all target values".** Without it the body comes out as the unmodified
base mesh, every time, byte for byte.

Two near-misses worth naming, because both look like they should work:

* `TargetService.load_target(obj, path, *, weight=0.0)` — the weight **defaults to zero**,
  so loading a target and not passing one does nothing.
* `TargetService.bake_targets(obj)` — bakes whatever is already in the stack, which is
  nothing if nothing was loaded.

Also: targets live in *subdirectories* of `data/targets`, and there are 1258 `.target.gz`
files. The extension is `.target.gz`, not `.target`.

### The rig goes on before the assets

`add_mhclo_asset` looks for a skeleton amongst the basemesh's nearest relatives. Finding one
it runs `ClothesService.set_up_rigging`, which interpolates the weights from the basemesh
and calls `RigService.ensure_armature_modifier`. Finding none it does

```python
clothes.parent = basemesh
```

and stops. There is no warning. Plain object parenting is a legitimate outcome.

So assets added before the rig are exported **unskinned**: present in the glb as meshes,
with `POSITION`, `NORMAL` and `TEXCOORD_0`, and no `JOINTS_0`, no `WEIGHTS_0` and no
`skin`. A skinned renderer does not draw them, and the symptom is a figure that looks
exactly as it did before you added anything.

### The visible body is a proxy; `base.obj` is a stand-in

MakeHuman's `base.obj` is a low-poly form that the targets morph and the proxies fit to. It
is never meant to be seen. The nude body you look at is a **proxy**:

```
proxymeshes/male_generic/male_generic.proxy
proxymeshes/female_generic/   female1605/   female_muscle_13442/
```

added with `asset_type="Proxymeshes"`.

In Blender the proxy is hidden *behind* the stand-in rather than replacing it:
`_check_add_proxy` puts a `MASK` modifier on the basemesh and fits the proxy a hair
outside it. Exporting with `export_apply=False` — which skinning requires, because the
exporter must leave the armature modifier alone — **throws that mask away**, so the
stand-in exports at full strength. Hence step 7: delete the basemesh after everything has
been fitted to it.

### The proxy has no material, on purpose

`_check_add_proxy` passes `material_type="NONE"`, because the proxy is not meant to have a
material of its own. It inherits the body's:

```python
HumanService.set_character_skin(mhmat, basemesh, bodyproxy=proxy, skin_type="GAMEENGINE")
```

Skip it and the body exports with no material at all and renders as whatever the viewer
falls back to. The skins are `data/skins/<name>/<name>.mhmat`, one per age, sex and
appearance, and `old_caucasian_male` is the aged one.

### The rig is `game_engine`, not `standard`

`standard` is the *folder*. The names are the `rig.*.json` files inside it:
`rig.game_engine.json`, `rig.mixamo.json`, `rig.rigify.…`. `game_engine` is 53 bones and
is the one to use for a game.

```python
HumanService.add_builtin_rig(human, "game_engine")   # two positional arguments
```

### There are no poses and no facial hair

`data/poses/` exists and contains one `t-pose.json` per rig, and that is all the system
assets ship. So a standing pose has to be authored; see §4.

The pack has ten hair assets and no moustache, beard or stubble. Hence §3.

---

## 3. Editing the pack's images

The generator does not ship sidecar textures. MPFB points each asset's node tree at the
pack's own images, the glTF exporter embeds them in the glb, and the game reads them from
there. So an edit made in the Blender session, on the image datablock, is an edit to what
the figure wears.

`tools/blender/mpfb_paint.py` does that:

* **`material_image(obj)`** — the base-colour image of an object's own material. Ask the
  material, never `bpy.data.images`. With the garments on there are eight images loaded —
  eyes, eyebrows, a suit's diffuse, ambient-occlusion and normal maps, shoes, teeth — and
  "the first one whose name ends in `.png`" selects `brown_eye.png`, which is 1024 square
  and renders the suit as an eyeball.
* **`recolour(image, target, detail)`** — replace the hue, keep the light and shade. A flat
  fill would throw away the weave, the seams and the folds that make a garment read as
  cloth. Each texel keeps its luminance as a multiplier on the target.
* **`scale(image, gain)`** — for art calibrated against another renderer. See below.

### The palette

`REPAINTED` in `build_makehuman.py` maps an asset name to a target colour. The clothes in
the pack are modern — a zip field jacket over a shirt with denim jeans, and ankle boots —
and the geometry is worth keeping while the colour is not.

One asset carries a whole outfit: `male_casualsuit05` is the jacket **and** the trousers,
one mesh with one map. So a single recolour dresses both in one cloth, which is what a
period uniform was anyway. The jacket is also the closest thing in the pack to a tunic —
hip length, a collar, a closed front with a vertical line down the chest — and under one
colour that line stops reading as an open jacket over a shirt and starts reading as a
placket.

### The skin arrives clipped

The pack's skin is painted for MakeHuman's own lighting: mean `(228, 176, 142)`. Under this
project's ambient plus directional light, **13.4 per cent of a face close-up arrived at
255** — a man with no shading left in his face rather than a man lit by a window. Scaling
the map by `SKIN_GAIN = 0.78` takes that to 1.0 per cent.

The lighting is the project's and is right for the models the project generated. A texture
authored elsewhere is the thing that does not fit.

### The moustache is painted into the skin map

`tools/blender/makehuman_face.py`. Paint rather than geometry: it needs a UV and nothing
else, it cannot come loose from the skin, and it deforms with the face because it *is* the
face. Geometry would need a position, a weight to the head bone and a material of its own.

Every pixel is placed by measuring the mesh in front of it, because the mapping from the
face to the texture is the pack's business:

* **Forward is −Y.** Measured: the most forward vertex of the head is on the midline at
  `x = 0.0`, which is what a nose is.
* **The nose tip** is the furthest vertex along −Y *within 16 cm of the head bone*.
  Searching the whole mesh is the bug the first version had: in this rig's rest pose the
  fingers reach further forward than the nose — `y = −0.34` against `−0.16` — so it
  returned a knuckle at hip height and called it a face.
* **The paint region** is the set of mesh triangles inside one box around the lip,
  rasterised through their own UVs. No UV layout is hard-coded, so a different skin atlas
  needs no change here.

The upper lip is taken as 24 mm below the nose tip, and the moustache is a wide ellipse
with the ends dropped. That is the one constant in the file that is not read off the mesh.

---

## 4. The standing pose

The rig's rest pose is a wide A-pose. Measured from the skeleton, the upper arms hang
**48 degrees below horizontal**, which at briefing distance is a bind pose rather than a man
standing in a room.

So each bone is **aimed** at a direction in armature space, and Blender supplies the
rotation that gets it there:

```python
delta = current.normalized().rotation_difference(Vector(direction).normalized())
```

That is one call and it cannot get a sign wrong, which guessing at Euler angles for a bone
whose local axes are unknown certainly can. Assigning `pose_bone.matrix` rather than a local
rotation is what carries it through a chain: a bone aimed after its parent has moved still
ends up pointing where it was asked to. `bpy.context.view_layer.update()` between bones is
what keeps `head`/`tail` current.

The result is keyframed into an action named **`Idle`**, because `CutsceneDirector` asks for
that clip by name and a figure with no clips falls back to the bind pose. Three keys —
standing, a breath, standing again — so it loops without a seam. 159 channels, 3.0 s.

---

## 5. Checking the work

Four instruments, in increasing order of how much they tell you.

**The scene dump.** `build_makehuman.py` prints every object in the scene with its type,
vertex count, parent, materials and modifiers, immediately before the export. Every wrong
theory about the missing body was formed without reading this list.

**`--diagnose`** prints what the face painter is about to measure against: the proxy's
bounding boxes raw and world, the evaluated (posed) bounding box, the per-mesh UV range and
outward-facing fraction, and every image loaded with its size. It exists because two bugs
were found by printing objects and none by reasoning about them.

**`--inspect-skinned <dir>`** loads a glb through the same `SkinnedModel` the game uses and
reports nodes, joints, parts, triangles and clips — and each part's texture as
`WxH mean r,g,b`, read back off the GPU. That last column is what settled whether a dark
hair texture was arriving dark. It was.

**The probe**, for pixels:

```bash
LIBGL_ALWAYS_SOFTWARE=1 DOTNET_ROLL_FORWARD=Major \
  xvfb-run -a -s "-screen 0 1280x720x24" \
  ./src/MiVic.Game/bin/Release/net9.0/MiVic.Game \
  --cutscene zz_mh --probe artifacts/probe/mh.probe --probe-out artifacts/probe/mh.txt
```

A cutscene camera is in **millimetres** and the world is in metres — the director divides by
1000. So a figure standing at `z: 700` with its head at 1.50 m takes a camera at `y: 1500`,
and half a metre in front of that face is `z: 200` looking at `targetZ: 700`.

### And measure, do not look

Five separate confident descriptions in one session were wrong, and every one of them was
caught by counting pixels or reading a file, never by reasoning. The hair in particular:
"a white cap" was an over-bright forehead in front of dark hair, and the hair's own pixels
were mean `(74, 69, 60)`. When a colour or a count matters, diff two frames or read the
texture — a small image on a screen is not evidence.
