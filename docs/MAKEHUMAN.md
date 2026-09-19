# MakeHuman, for the cutscene figure

The briefing and debriefing figures are built by MakeHuman through **MPFB2**, not by hand.
This is how to install it, how to run the generator, and — more usefully — the API facts
that cost time to establish and are not obvious from the outside.

`tools/blender/build_makehuman.py` is the generator. It is the only file that needs to run;
everything else here is why it says what it says. This is the command that produces the
figure the briefing stands on:

```bash
/home/tkleisas/blender/blender-5.2.2-linux-x64/blender --background \
    --python tools/blender/build_makehuman.py -- \
    --out src/MiVic.Game/Content/Models/Generated \
    --name personality_elder_skinned \
    --hair short01 \
    --garment male_casualsuit05 \
    --garment shoes03 \
    --garment grinsegold_moustache
```

`--name` is the asset name, and it is the name `m1_briefing.cutscene.json` asks for and
the name it credits its lines to, because a cutscene's speaker must be standing in the
room. The script defaults it to the same value; it is spelled out here so the command and
the committed file cannot drift apart. The same is true of the garments and the hair:
they are the script's defaults only in the sense that this command is the one that built
the committed figure — a closed-front field jacket that a recolour and a placket turn
into the tunic, ankle boots, and the moustache mesh from the bodyparts06 pack (see §1).

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

### The moustache comes from a second pack

The system pack has no facial hair of any kind (see §2), and the painted moustache the
first figure wore read as a smudge at briefing distance. The community pack
**bodyparts06** ("a set of beards and moustaches") ships `grinsegold_moustache` — a
proper chevron that covers the upper lip — and that is what the figure wears. The pack
page is `static.makehumancommunity.org/assets/assetpacks/bodyparts06.html`; the zip is
`files.makehumancommunity.org/asset_packs/bodyparts06/bodyparts06_cc-by.zip` (note the
hyphen in `cc-by`; guessing `ccby` is a 404). Its `clothes/grinsegold_moustache` folder
copies straight into the asset root's `clothes/` next to the suits, and MPFB treats it
as a garment — "these end up as clothes in MPFB2", as the pack page warns.

The pack is **CC-BY**, where the system assets are CC0: grinsegold is credited in the
README's licence section, and the credit must survive any rebuild that keeps the mesh.
(The mhclo's own header says AGPL, a MakeClothes default; the pack page's CC-BY is the
licence the author published it under, and attribution covers both readings.)

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
* **`drop_unread_images(objects, keep)`** and **`limit_size(image, limit)`** — what is worth
  exporting at all. See the end of this section.

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

### The moustache is a mesh; the skin map is its backing

The figure's moustache is `grinsegold_moustache` (§1), fitted and rigged like any
garment and then **edited**: `widen_moustache` in `build_makehuman.py` scales it
15 per cent wider and drops its ends, because the asset ships a neat chevron and
the reference's is wider than the mouth with hanging ends. Vertex weights are per
index, so moving the vertices after rigging keeps the skinning.

Two things keep it honest. Its diffuse is named `Moustache_black_diff.png` — not
`diffuse` — and the first build's name-matcher missed it twice over: the recolour
never ran, and the trim below concluded the texture was unread and **unlinked it**,
so the glb exported an opaque flat blob with no texture at all. `material_image`
now matches `("diffuse", "_diff")` and excludes the maps nothing reads (`_hn`,
`normal`, `_ao`, `rough`). And the strands are painted black, so the mesh is
repainted a dark grey — the first attempt at a mid grey read as ash, not hair.

Under the mesh, `tools/blender/makehuman_face.py` still paints the skin, because a
card moustache over bare lip shows skin through the gaps between its strands. The
paint is the same measured rasteriser as before, now the colour of the mesh and
wide enough to back it — and a second pass, `paint_shadow`, darkens the scalp above
the brow line and the skin under the brows, because skin under hair is in shadow
and a lit scalp reads as white streaks through the hair's cards.

### Alpha is not carried

The renderer draws every skinned part with `BlendState.Opaque` — and with an alpha
channel in the texture, the hair, brows and moustache rendered their strands
**white**, whatever the RGB held (measured dark everywhere: file, bytes, GPU). No
amount of repainting touches that; the alpha channel itself was the artefact, found
by bisecting the figure one part at a time (no hair, no eyes, no smoke, flat alpha —
only the last changed anything). So the build flattens every worn texture's alpha to
1 before export: the strand shape comes from the card geometry alone, which is what
the renderer was drawing all along.

A red herring worth recording: the MPFB materials do *not* carry a live clearcoat —
`Coat Weight` is 0 and the `KHR_materials_clearcoat` extension in the glb is inert.
The sheen visible in Blender previews is the Workbench studio light's own specular
highlight, which `preview_figure.py` now switches off, because a preview that lies
about shine gets reflections chased in the wrong renderer.

* **Forward is −Y.** Measured: the most forward vertex of the head is on the midline at
  `x = 0.0`, which is what a nose is.
* **The nose tip** is the furthest vertex along −Y *within 16 cm of the head bone*.
  Searching the whole mesh is the bug the first version had: in this rig's rest pose the
  fingers reach further forward than the nose — `y = −0.34` against `−0.16` — so it
  returned a knuckle at hip height and called it a face.
* **The paint region** is the set of mesh triangles inside one box around the lip,
  rasterised through their own UVs. No UV layout is hard-coded, so a different skin atlas
  needs no change here.

The upper lip is taken as 24 mm below the nose tip. The same nose tip anchors the pipe
(§4a): the mouth is 30 mm below it and 14 mm behind it, which is the only anatomy the
pipe's placement does not measure.

### The face is shaped by targets, the hairline by hand

`DETAIL_TARGETS` in `build_makehuman.py` loads nine `.target.gz` files with weights after
the macros run — age, roundness, cheek volume and sag, chin width, nose breadth and
bridge — because the reference is a heavier, older face than the macros alone make. Two
traps from §2 apply: `load_target`'s weight defaults to zero, and the targets root is the
*extension's* `data/targets`, next to `services/`, not the asset pack's root. The
eyebrows are `eyebrow008`: the reference's brows are thick and straight, and 008 is the
busiest of the twelve the pack ships.

The hair is `short01` repainted a dark grey — `short02` read as a pale cap, `short04`'s
sideburns reached the jaw. Its hairline ships as one straight tangent across the
forehead, and the reference's steps forward at each temple, so `shape_hairline` pulls the
edge verts down by a gaussian peaked at each temple, measured against the hairline the
asset actually shipped with. Down is safe: the hair overlaps the forehead skin, so a
lowered edge can never open a gap.

### What is worth exporting

The figure came out at **22.29 MB**, which is not a game asset — the whole rest of the
project's models are 15 MB. The breakdown is the argument for what was cut:

| | |
|---|---|
| images | 20.65 MB |
| geometry | 1.64 MB |

And of the images, one was **8.86 MB**: `male_casualsuit05_normal`, a normal map. The
renderer cannot sample it. `SkinnedModel` asks a primitive for its `baseColorTexture` and
`SkinnedEffect` has one sampler slot, so an ambient-occlusion map, a normal map or a
roughness map is bytes nothing will ever read.

So `drop_unread_images` unlinks every image that is not a base colour — the node is removed
rather than the image deleted, because the exporter follows references and an unlinked
image is simply not written. Then `limit_size` caps the rest at `TEXTURE_LIMIT = 1024` on
the longest side: cloth, hair and teeth all survive at 1024 for a figure seen whole at
three metres. **The skin is exempt** and keeps its 2048, because a face is the reason the
figure exists at all and the project already commits a 3 MB face map for the generated one.

22.29 MB → **8.83 MB**, of which the skin is 4.30. That number is a decision, not an
accident, and it is written down here so the next person can disagree with it.

The trim has one blind spot worth naming: it builds its keep-set from `material_image`,
so an asset whose diffuse is not named `diffuse` — the moustache's `_diff` — was once
trimmed *as unread* and exported as a flat blob. The matcher now knows `_diff`, but the
pipe, placket, buttons and tabs are also kept out of the trim's object list on purpose:
their colours are generated four-pixel images with no `diffuse` in their names.

---

## 4a. The pipe

There is no pipe in any pack — the equipment packs are weapons, bags and tools — so
`tools/blender/makehuman_pipe.py` authors one: a shank swept along a bent path and a bowl
lathed from a profile, three solid-colour materials (briar, vulcanite, char) carried as
4×4 generated images, because the skinned renderer samples a texture and a bare
base-colour factor is not one. It hangs at the corner of the mouth, 11 mm off the
midline, measured from the nose tip.

Every vertex is weighted to the pipe's own bone alone — `pipe_mouth`, a child of the
head, for the pipe in his teeth, and `pipe_held`, a free bone the clip drives, for the
pipe in his hand (see §4 for the swap). The smoke's markers work the same split:
`pipe_bowl` rides the head, and `pipe_bowl_held` rides the hand, placed at a hold frame
rather than at bind — a bone-parented empty bakes its parent's *evaluated* transform
into itself, and the held bone's bind is a scale of zero, so a marker placed at bind
reads from inside his hip. The director gates the two by the pipe joints' scales, and
the smoke follows whichever pipe is visible (`CutsceneDirector.DrawPipeSmoke`;
`cutscene state` in a probe reports both markers and their scales). Two game-side notes
from the same round: `SkinnedModel.FindNodeIndex` matches exact names before prefixes
now, because `"pipe_bowl"` prefix-matched `pipe_bowl_held` and the smoke followed the
hidden pipe; and the held bone is free rather than parented to the hand, because a
child of the wrist would inherit its turn as a constant tilt.

The bowl's tilt is a `Matrix.Rotation` about X — a forward pitch. The first version built
it with `rotation_difference` from the X axis, which yaws the bowl 78° and leaves the
chamber facing the camera: a pipe with its bowl turned on its side.

## 4b. The tunic: placket, buttons and tabs

`tools/blender/makehuman_uniform.py`. The reference tunic is buttoned to the collar, and
the pack's jacket is cut open — paint cannot close a hole, and a recolour cannot hide one
either. So the front is closed with geometry: a **placket**, a ribbon of the tunic's grey
laid at the depth of the lapel edges from the collar to the belt, found by walking up the
midline and watching the front surface jump backwards where the closed cloth ends and the
throat begins. The **buttons** march down the placket, and the **collar tabs** — gold
behind red, so the piping is the gold showing around the red — sit on the placket at the
base of the collar. Pinned to the collar itself they disappeared behind the lapel from
the front, which is the only angle a briefing is watched from.

Two construction notes. A tab leans toward the midline, which is a *negative* rotation
about Y on the figure's left — the first pass leaned them outwards. And material, UVs and
weights must land on the finished mesh, in that order: written onto the empty mesh before
`bm.to_mesh`, the UV layer has zero loops and the part exports white. The whole set rides
`spine_03`, so the insignia moves when the man breathes.

The jacket's own texture is flattened for the same reason the placket exists: at the
default `detail` of 0.30 the recolour keeps a dark jacket over a pale shirt, which is a
businessman. `DETAIL` drops it to 0.15 for the tunic, and one grey cloth plus gold
buttons is a uniform.

---

## 4. The standing pose

The rig's rest pose is a wide A-pose. Measured from the skeleton, the upper arms hang
**48 degrees below horizontal**, which at briefing distance is a bind pose rather than a man
standing in a room. The arms are aimed with a slight bend — elbows flexed, palms in —
because arms aimed straight down read as a attention stance rather than a man at ease.

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
that clip by name and a figure with no clips falls back to the bind pose. The loop is
twenty-four seconds, seventeen keys, deliberately slow: stand, breathe, the hand comes up
unhurried, and the pipe **leaves his mouth** — he talks over it for a while, two beats of
the hand and one of the free arm, then it goes back the way it came. Each key blends
between named pose sets (`STANDING`, `BREATHE`, `GRASP`, `HELD`, `GESTURE`) by weight, so
a bone never snaps, and bones not in any set are aimed at their own captured rest
direction — a weight of zero is exactly the figure that shipped before the gestures
existed.

A pipe that leaves the mouth is two pipes, and the clip swaps them: `Human.pipe` rides
`pipe_mouth`, a bone parented to the head, and `Human.pipe_held` rides `pipe_held`, a
free bone the clip drives by *measured* position — the hand's place at each key is read
off the pose the pass just wrote, never a constant. The swap is a scale crossfade keyed
while the hand is on the bowl, where the two pipes already coincide.

Two traps were found by number and are worth keeping. **`frame_set` before aiming, never
after**: setting the frame re-evaluates the action and overwrites the pose with the keys
already in it, so a loop that aimed and *then* set the frame keyed the reverted pose —
which is how the old three-key loop exported "stand, stand, stand" and called it
breathing. And the gesture is checked numerically at build time: the build prints the
fingertip position at the grasp's peak against the pipe's grip point, so a weak reach
reads as millimetres of gap rather than as a screenshot somebody squints at. 165
channels, 24.0 s.

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

**`tools/blender/preview_figure.py`**, between the two. It draws the glb with Workbench's
TEXTURE colour mode on a frame of `Idle` — `preview_model.py`'s vertex colours are the
right tool for a tank and the wrong one for a figure whose face is a texture — and it
measures the posed meshes, not the bind pose, so the frame lands on the man rather than
his chest. One honest difference to remember: Workbench's studio light is darker than the
briefing room's, so a colour that reads black in a preview can read grey in the game —
the hair was dialled down twice from Workbench evidence before the probe said otherwise.
Calibrate colours against the probe, shape against the preview.

A cutscene camera is in **millimetres** and the world is in metres — the director divides by
1000. So a figure standing at `z: 700` with its head at 1.50 m takes a camera at `y: 1500`,
and half a metre in front of that face is `z: 200` looking at `targetZ: 700`.

### Which build you are looking at

The two configurations hold separate copies of the content, and they are not kept in step
by anything except a build. `--probe` and `--inspect-skinned` are usually run from
`bin/Release/…`; `tools/render_cutscene.py` and the README's screenshot commands are pinned
to `bin/Debug/…`. A model is copied with `PreserveNewest`, so a figure regenerated after
the last Debug build **is not in the Debug output** — and a cutscene rendered from Debug
will show the figure as it was before the regeneration, correctly and without complaint.
This cost one round of "the new figure did not appear in the render" that had nothing to
do with the figure.

### And measure, do not look

Five separate confident descriptions in one session were wrong, and every one of them was
caught by counting pixels or reading a file, never by reasoning. The hair in particular:
"a white cap" was an over-bright forehead in front of dark hair, and the hair's own pixels
were mean `(74, 69, 60)`. When a colour or a count matters, diff two frames or read the
texture — a small image on a screen is not evidence.
