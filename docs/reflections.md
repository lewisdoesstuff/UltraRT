# Materials and ray-traced reflections

Glossy, metal and ULTRAKILL `REFLECTION` surfaces trace one reflection ray per pixel. The result is filtered and then composited in all three paths (`pt`, `pt-dn`, `hybrid`). Rough surfaces trace nothing, so the cost follows how much of the screen is glossy, and specular weights stay low so the flat PSX look survives.

Real mirrors (layer-8 `Portal` objects with `mirror == true`) are **out of scope**; they belong to the portal work.

Files:
- `src/UltraRT/MaterialClasses.cs`: classification rules, the class table and the JSON overrides.
- `src/UltraRT/OriginalMaterials.cs`: original materials of baked (atlas) geometry. Its Harmony prefix is in `Patches.cs`.
- `src/UltraRT/SceneProxies.cs` and `StaticGroups.cs`: proxy material parameters and per-slot atlas variants.
- `src/UltraRT/ReflectionFilter.cs` and `unity/.../Reflections.compute`: temporal and spatial filters, plus the resolve pass.
- `unity/.../UltraRTMaterial.hlsl`: packing and the shared specular weights.
- `unity/.../PathTrace.raytrace`: the reflection ray.
- `unity/.../Proxy.shader`: material payload.
- `unity/.../Composite.shader` and `Denoise.compute`: composites.
- `src/UltraRT/ultrart-materials.json`: default overrides, deployed next to the DLL.

## 1. Classification (C#, per proxy material)

`MaterialClassifier.Classify(source material)` returns `MaterialParams`: class, mode (`none`, `specular`, `blend`, `additive`), roughness, metalness, f0, specular weight and reflection strength. `MaterialParams.Apply` writes these as proxy material properties (`_Roughness`, `_Metalness`, `_F0`, `_SpecWeight`, `_ReflMode`, `_ReflStrength`, `_MatClass`, plus a `*2` secondary set). Rules are checked in this order:

1. **Sky walls** (`LIMBO_WALLS`): none.
2. **`REFLECTION` keyword** with `_ReflectionStrength` (RS) > 0. The rule gates on the keyword, not the value: RS defaults to 1 on every other Master material.
   - `_CubeMode` 1 → `additive`, otherwise `blend`.
   - Roughness comes from RS: ≥ 0.3 → 0.02, ≥ 0.1 → 0.08, otherwise 0.15.
   - Allow-listed names (below) get roughness 0 instead.
   - RS = 0 (most 7-S materials) falls through to the other rules.
3. **Name allow-list.**
   - `Mirror`, `FerrymanMirror`, `MirrorTexture`, `GlassReflective`: class `Mirror`, roughness 0. Without the keyword (the two plain-texture fake mirrors) they also get `blend` with strength 0.6, which is a guess; override it if it looks wrong.
   - Water (`Water 10A`, `Limbo Water …`, `M_7S_Water`, `M_WaterUps`, but not signs like `WaterControl1`): roughness 0.
   - A Master water material without the keyword: `specular`, f0 0.02, weight 1, so it reflects at grazing angles through Fresnel.
4. **Unlit Master** (no `VERTEX_LIGHTING`, 226 materials: screens, signs, sky props): none.
5. **`_SurfaceType` table.** With `VERTEX_BLENDING`, `_SecondarySurfaceType` becomes the secondary set. The hit shader selects it where vertex colour r < 0.5; on atlas geometry it uses the blend weight `|normal.w| − 1` instead (unverified equivalence).
6. **Unity `Standard`**: only when `_Metallic` > 0.5 or `_Glossiness` > 0.7. Roughness = 1 − glossiness.
7. **Anything else**: class `Other`, none.
8. **Overrides** (see below) are applied last, in file order, and every matching rule applies.

Default table. The weight is relative: the global `-ultrart-spec-weight` (default 0.3) multiplies it, which keeps the effective weight at or below 0.3.

| Class | Mode | Roughness | Metalness | F0 | Weight |
|---|---|---|---|---|---|
| Generic, Carpet, Dirt, Sand, Snow, Grass, Gravel, Rubble, Crunchy, Hologram, Other | none | | | | |
| Wood, WoodThin | specular | 0.75 | 0 | 0.03 | 0.5 |
| Flesh | specular | 0.45 | 0 | 0.03 | 0.6 |
| Marble | specular | 0.25 | 0 | 0.04 | 1 |
| Glass | specular | 0.05 | 0 | 0.04 | 1 |
| Wet | specular | 0.10 | 0 | 0.03 | 1 |
| MetalHigh | specular | 0.30 | 1.0 | (albedo) | 1 |
| MetalLow | specular | 0.55 | 0.8 | | 1 |
| MetalThin | specular | 0.50 | 0.7 | | 1 |
| MetalStumpy, MetalGrill | specular | 0.60 | 0.6 | | 1 |
| Mirror | blend 0.6 | 0 | | | |

SurfaceType is a footstep-sound tag, so it is noisy. The shipped `ultrart-materials.json` corrects the worst cases from the research scan:
- `Concrete*` and `CaveRock*` are tagged Marble.
- Lamp fixtures and screens are tagged Glass.
- Credits portraits, signs and maze tiles are tagged MetalThin.

### Overrides: `ultrart-materials.json`

The classifier loads `BepInEx/plugins/UltraRT/ultrart-materials.json` (shipped defaults) and then `BepInEx/config/ultrart-materials.json` (personal rules, which win). Both are re-read at every scene build, so F9 or a level reload picks up edits.

```json
{ "rules": [
  { "material": "FloorPanelBig*", "reflStrength": 0.2 },
  { "texture": "*chrome*", "class": "MetalHigh", "roughness": 0.15 },
  { "material": "Water 10*", "reflMode": "blend", "reflStrength": 0.4 },
  { "shader": "psx/vertexlit/*", "material": "Tile*", "class": "Marble" }
] }
```

- **Match fields** (globs with `*` and `?`, case-insensitive; every field given must match): `material` (the name without ` (Instance)`), `texture` (the main texture's name) and `shader`.
- **Set fields**:
  - `class` resets to that class's defaults first.
  - `roughness`, `metalness`, `f0`, `specWeight` and `reflStrength` take values from 0 to 1.
  - `reflMode` takes `none`, `specular`, `blend` or `additive`.
- An override owns the material: it drops the secondary set.
- For baked geometry, rules match the **original** material.

### Logs and dump

- `Material classes: by class: …; reflection modes: none N, specular N, blend N, additive N; override hits N (rules: …)`. This counts proxy materials plus every atlas slot.
- `Baked (atlas) slots: S (originals from hook H, footstep clones F, unknown U; captured renderers C), V atlas material variants`.
- `Reflection material variants: V atlas variants; static records at level L: R (without variants R0; by level …)`.
- `-ultrart-matdump` writes `BepInEx/ultrart/material-classes-<scene>.tsv`, one row per classified material: name, shader, texture, params and the deciding rule. It also gives atlas geometry a variant for **every** class, not only reflective ones, so the `materials` view colours all of it. That costs extra shader-table records, so use it only for tuning.

## 2. Baked geometry: original materials and atlas variants

`StaticSceneOptimizer.SetupMeshes` (in `Start`) swaps every baked renderer to one atlas material, `Hidden/ULTRAKILL/ULTRAKILL-Stationary`, which has no surface type. Two sources recover the originals:
- **Harmony prefix** on `StaticSceneOptimizer.SetupMeshes`: records `staticMRends[i].sharedMaterials` and the original mesh's per-submesh index counts. This is the primary source. It sees every baked renderer, including ones without colliders.
- **Fallback**: the footstep physics clone. `SceneHelper` creates it in `sceneLoaded`, before `Start`, holding the original materials, and the source's `PhysicsSceneStateEnforcer.matchingObject` points to it. This covers `-ultrart-nohook` and renderers the prefix didn't see. It only exists for objects with a collider-derived physics copy.

SSO collapses a renderer's submeshes into **one slot**, so the slot takes the **dominant** original material: the traceable one with the most indices. A multi-material source therefore gets one class. Per-triangle remapping through the footstep clone's submesh ranges is possible later, but unverified.

**Variants.** Atlas slots have no per-vertex material data. Instead, each distinct `(atlas proxy, params, secondary)` gets a `new Material(atlasProxy)` with its own parameters. A slot whose class has no reflection keeps the plain atlas proxy and costs no extra record. Group meshes merge by material, so every reflective class present in a group adds one shader-table record.
- The existing `StaticRecordBudget` logic counts records with variants included, so grouping coarsens automatically if they push it over budget.
- The log line above shows the growth.
- Checkpoint room clones come after SSO and have no record of their originals. They reuse the slot material decided by the first, captured source, because captured sources are processed first.

## 3. Payload (64 bytes, unchanged size)

`RayPayload` is shared by every hit and miss shader, and Unity requires matching sizes. The debug-only `float2 uv` became one packed `uint`, which freed a dword for the material:

| Field | Content |
|---|---|
| albedo, alpha, normal, t, emission, instance | as before |
| `material` | roughness 8 \| metalness 8 << 8 \| f0 8 << 16 \| specular weight 8 << 24 (unorm8) |
| `uvHalf` | half(uv.x) \| half(uv.y) << 16, debug views only (`PayloadUV()`) |
| `primitive` | debug views only |
| `flags` | bit 0 geometric normal \| reflection mode 2 << 1 \| layer 8 << 8 \| reflection strength 8 << 16 \| class 5 << 24 |

## 4. UAVs: still exactly 8

Unity ray tracing shaders allow at most 8 UAVs. A 9th makes `DispatchRays` fail silently. PathTrace now binds:

| # | UAV | Format | Content |
|---|---|---|---|
| 1 | `_Output` | ARGBHalf | composited image and accumulation history |
| 2 | `_Radiance` | ARGBHalf | demodulated lighting |
| 3 | `_Surface` | ARGBInt (uint4) | **new**: replaces `_AlbedoOut` and `_EmissionOut` |
| 4 | `_NormalDepth` | ARGBHalf | |
| 5 | `_InstanceId` | RInt | |
| 6 | `_HybridLight` | ARGBInt | |
| 7 | `_Reflection` | ARGBHalf | **new**: rgb reflected radiance, a = hit distance (sky 1000, −1 = not traced) |
| 8 | `_ReservoirCur` | RWStructuredBuffer<uint4> | ReSTIR |

`_Surface` packing (`PackSurface` in `UltraRTMaterial.hlsl`):
- x: albedo RGB unorm8 \| class << 24 \| mode << 29
- y: half emission r \| g << 16
- z: half emission b \| reflection strength << 16
- w: the payload's material word

The precision is the same as before: albedo was ARGB32 and emission ARGBHalf.

All consumers now read the unpacked textures produced by the **resolve** pass (`Reflections.compute`, kernel `Resolve`), which writes `_gAlbedo` (ARGB32) and `_gEmission` (ARGBHalf):
- the denoiser's `albedo` and `emission` inputs
- the hybrid composite's `_UltraRTAlbedo`
- the debug `SaveOutput` of denoised modes

The resolve runs in every denoised mode, even with reflections off. The non-denoised `pt*` modes don't need it, because the ray generation shader composites `_Output` itself. `UnpackHybrid.compute` is unchanged.

## 5. The reflection ray (PathTrace RayGen)

At a primary hit, `SpecularTerms(material, albedo, N·V)` gives `specW` (rgb) and `keep`. These are the same weights the composites use (section 7). The pixel traces a ray only if reflections are on, the mode is not `none`, and `luma(specW) ≥ threshold` (default 0.015). This means:
- **Dielectric specular classes** (weight 0.3 × F) trace mostly at grazing angles.
  - Marble: 0.3 × 0.04 = 0.012 at normal incidence, below the threshold.
  - Wood: 0.15 × F, so rays only at grazing angles.
- **Metals**: F ≈ albedo, so 0.3 × albedo. They trace almost everywhere.
- **`REFLECTION` materials**: specW = RS × `-ultrart-refl-strength`. For example, FloorPanelSmall (0.025) traces and FloorPanelSmallShine (0.05) does too.

How the ray is traced:
- **Direction**: the exact mirror direction when roughness < 0.03. Otherwise a GGX VNDF sample (Heitz 2018, alpha = roughness²). If the sample ends up below the surface, it falls back to the mirror direction.
- **Estimator**: the radiance along the ray, with no BRDF weight. The composite applies F (split-sum style), and G2/G1 is taken as 1.
- **Origin and flags**: origin `pos + n·0.02`, back faces culled, mask `0xFF`. Sky walls stop the ray and report sky, as on camera rays.
- **Shading at the hit**: `albedo × lerp(1, DirectOne + ambient·_BounceAmbient, litWeight) + emission`.
  - `DirectOne` is the bounce's one-shadow-ray estimate (fill lights plus a RIS light or emitter sample).
  - Emission is counted in full, because emissive light sampling only covers the diffuse lobe.
- **Miss**: `SkyRadiance(dir)`.
- **Firefly clamp**: glossy samples are clamped to luminance `maxRadiance` (8). Mirrors are not clamped.
- **Non-denoised modes** (`pt`, `pt-direct`, accumulation): the ray generation shader applies the reflection directly, as `c = albedo·lighting·keep + emission + specW·Lrefl`. Accumulation therefore converges glossy reflections properly.
- **Modes without a ray**: `pt-indirect` (mode 2) traces none.

## 6. Filtering (Reflections.compute, ReflectionFilter.cs)

1. **Temporal.**
   - **Reprojection.** The history is reprojected from two candidate points:
     - the **virtual image point** `camPos + dir·(t + hitDist)`: the reflected hit seen through the surface, exact for planar mirrors, so mirror reflections stay put while the camera moves;
     - the **surface point**.
     Roughness below 0.5 tries the virtual point first, otherwise the surface point first. The other one is the fallback.
   - **Tap validation.** Each of the 2×2 bilinear taps is validated like the denoiser's: same instance (7 bits), normal dot ≥ 0.9, on the current surface's plane, and traced last frame.
   - **History clip.** The history is clipped to the current 3×3 mean ± k·σ. k = 1.25 for mirrors and 2.5 for rough reflections; the 3×3 uses traced pixels within 10 % depth.
   - **Blend.** `alpha = max(1/len, lerp(0.3, 0.08, saturate(2·roughness)))`, with history capped at 32.
2. **Spatial** (display only; the history stays unblurred).
   - Skipped below roughness 0.08, so mirrors stay sharp.
   - Otherwise a 5×5 B3 kernel, step 1, or 2 above roughness 0.35.
   - Weights: plane distance as in the à-trous filter, normal^32, and roughness similarity.
3. **Resolve.** Unpacks albedo and emission, then writes `_gSpecular` = (specW · filtered reflection, keep). An untraced pixel gets (0, 0, 0, 1).

**Why not a third Denoiser instance?** The denoiser reprojects with the surface motion only, which smears every reflection as soon as the camera moves. Its luminance edge-stopping is also tuned for diffuse lighting. A dedicated pass is cheaper and supports virtual-motion reprojection.

## 7. Composites

The shared form is `final = base·keep + specW·Lrefl` (`SpecularTerms` in `UltraRTMaterial.hlsl`).

**`specular` mode.**
- `F = f0' + (max(1 − roughness, f0') − f0')·(1 − N·V)^5`, with `f0' = lerp(f0, albedo, metalness)`. Metals are tinted by albedo, and roughness damps the grazing peak.
- `w = materialWeight × specWeight` (global, 0.3).
- `specW = F·w`.

| Path | base | keep |
|---|---|---|
| `pt`, `pt-dn` (full RT) | albedo · lighting (diffuse only; emission stays additive) | `1 − w·lerp(luma(F), 1, metalness)` |
| `hybrid` | game colour × RT shadow ratio + bounce (Composite.shader) | dielectrics `1` (pure add), metals `1 − w·metalness` |

- **Full RT**: the keep term is energy-conserving, a lerp between the PSX diffuse and a physically-based metal.
- **Hybrid**: a deliberate deviation from "always add" for metals. Adding albedo-tinted reflections on top of the game's already-lit metal brightens it noticeably, so metals partly replace the game colour instead.

**`blend`** (REFLECTION, `_CubeMode` 0): `specW = s`, `keep = 1 − s`, with `s = saturate(RS × reflStrength)`. The result is `lerp(base, Lrefl, s)`.

**`additive`** (`_CubeMode` 1): `specW = RS × reflStrength`, `keep = 1`. The result is `base + RS·Lrefl`.

The game's own cubemap reflection is already part of the hybrid game colour, and it is **not removed**. A blend surface therefore ends up as `(1 − s)·game + s·Lrefl`, where `game` already contains `s·cube`. This is acceptable for v1, and `-ultrart-refl-strength` scales s if it looks doubled. Removing it properly would need the cubemap and mask in the hit shader, or a raster pass without the keyword.

Where each path applies it:
- **pt-dn**: the denoiser's composite kernel computes `albedo·lighting·spec.a + emission + spec.rgb` (`DenoiserInputs.specular`, optional).
- **hybrid**: `Composite.shader` computes `result = result·spec.a + spec.rgb` after the shadow and bounce terms. `_gSpecular` is at trace resolution, so reflections are as blocky as the bounce.

## 8. Settings, flags and keys

`ReflectionSettings` (Plugin field `_reflSettings`):

| Flag / key | Field | Default |
|---|---|---|
| `-ultrart-noreflections`, **F4** toggle (resets the reflection history, shows a toast) | `enabled` | on |
| `-ultrart-spec-weight X` | `specWeight` (global weight of specular classes) | 0.3 |
| `-ultrart-refl-strength X` | `reflStrength` (scales REFLECTION materials' RS) | 1.0 |
| `-ultrart-matdump` | `SceneProxies.DumpMaterialClasses` | off |
| — | `threshold` (minimum luma(specW) for a ray), `maxRadiance` (8) | 0.015 |
| — | `temporal`, `spatial`, `alphaMirror`/`alphaRough` (0.3/0.08), `clampMirror`/`clampRough` (1.25/2.5), `maxHistory` (32), `spatialMinRoughness` (0.08) | |

**Debug views** (F8 cycles; appended after `primitive`, so existing indices are unchanged):
- **`refl`**: filtered reflected radiance.
- **`refl-raw`**: the one-ray radiance. Untraced pixels are dark grey.
- **`refl-spec`**: what gets added (specW × filtered).
- **`refl-history`**: history-length heat map.
- **`materials`**: a DebugView mode, cheap and with no PT. Each surface is drawn in its class colour, dimmed where the class has no reflection; REFLECTION materials get white diagonal stripes. Atlas geometry without a variant shows as `Other` (dark purple) unless `-ultrart-matdump` is used.
- **`spec-params`**: r = smoothness (1 − roughness) where specular, g = metalness, b = RS (REFLECTION) or weight (specular).

**Plugin.cs integration** (already done, listed for review):
- `Kind.Reflection`.
- The six modes above.
- The four flags.
- F4 in `OnUpdate`.
- `SceneProxies.MaterialOverrideFolders = { plugin folder, Paths.ConfigPath }` in `Awake`.
- `Reflections` is now a required asset in `LoadShaders`, which creates `_reflFilter`.
- `_reflFilter.Reset()` wherever the denoisers reset (F7, `ResetScene`).
- `EnsureTargets` adds `_gSurface` (ARGBInt), `_gReflection`, `_gSpecular` (ARGBHalf) and `_gReflDebug` (ARGB32).
- `Trace()` binds `_Surface`, `_Reflection`, `_ReflParams` and `_ReflMaxRadiance` instead of `_AlbedoOut`/`_EmissionOut`. In denoised and reflection modes it runs `_reflFilter.Execute` (sample marker `UltraRT.Reflections`) **before** `_denoiser.Execute`. It passes `specular = _gSpecular` to the main denoiser and sets the `_UltraRTSpecular` global for the hybrid composite.

## 9. Cost (estimated, not measured)

**Rays.** Each glossy pixel adds one reflection ray plus `DirectOne`'s shadow ray, and RIS evaluates `_BounceCandidates` candidates. That is about +2 rays on top of the roughly 3–4 rays per lit pixel today (primary, bounce, shadows). Expected dispatch growth:

| Scene | Expected growth |
|---|---|
| Rooms with marble or water floors (1-1, early-layer water) | 5–15 % |
| Metal-heavy scenes (MetalGrill and MetalThin are common) | 20–40 % |
| Rough-only areas | ≈ 0 % |

**Filters**, at the default 0.5 scale (640×360 at 720p):
- Temporal: about 9 + 8 fetches.
- Spatial: 25 taps × 3 fetches on rough pixels only.
- Resolve: about 4 fetches.

Together about 0.05–0.15 ms. The resolve runs even with reflections off (about 0.02 ms) because it now unpacks albedo and emission.

**Memory**: about 92 bytes per trace pixel (surface 16, reflection 8, specular 8, debug 4, history 2×8, geometry 2×16, spatial 8). That is about 21 MB at 640×360.

**Shader-table records**: one per reflective class per static group, shown in the log. Each record costs about 0.4 µs a frame.

## 10. Limitations and risks

- **Not tested in game yet** (the game was not launched for this change).
- **Reflected objects that move** reproject with the camera only, so moving enemies in mirrors lag by the history.
  - Glossy reflections on curved or non-planar surfaces: the virtual point is wrong, and the code falls back to surface reprojection, which smears a little in motion.
  - The neighbourhood clip limits ghosting, but at 1 spp with rough metals it is loose.
- **Glossy reflections remain somewhat noisy** at 1 spp, especially rough metals at roughness 0.5–0.6. The spatial pass is display-only and small. If it is too noisy: lower `alphaRough` (longer history), lower `-ultrart-spec-weight`, or classify more metals as rough via overrides.
- **SurfaceType is noisy.** Expect odd glossy objects until the override list grows (`-ultrart-matdump`, `materials` view).
- **Atlas slots take one dominant class per source renderer.** A secondary type on baked geometry relies on the unverified `|normal.w| − 1` blend weight.
- **The game's cubemap is not removed** in hybrid (see section 7).
- **The REFLECTION mask (`_ReflectionMask`) is ignored.** It is never assigned in the game, so it is white.
- **Unlit Master materials get no specular**, by design. Override them to opt in.
- **Footstep-clone fallback**: `PhysicsSceneStateEnforcer.matchingObject` is read by reflection (private field). If the game renames it, only the fallback is lost.

## 11. What to test in game

- **1-1** marble floors: `FloorPanelSmall*` (REFLECTION blend, RS 0.025–0.05, roughness 0.15), `FloorPanelBig` (additive 0.1) and Marble-class `Floor`. Check:
  - `refl` and `refl-raw` look right;
  - `hybrid` shows subtle floor reflections of the room;
  - `materials` shows the striped panels;
  - reflections stay put while strafing (virtual reprojection).
- **Water in early levels** (`Water 1C`, `Water 10A–D`, `Limbo Water*`): roughness-0 mirror reflections of the sky and geometry; sky walls should reflect as sky.
- **4-1**: emissive-lit interiors. Reflected emitters should be visible in glossy floors and metals; check `pt-dn` for energy and that nothing over-brightens.
- **0-5**: lava (`Lava*`, Wet class, typically EMISSIVE). Check it doesn't turn into a mirror and that reflections of lava glow on nearby metal.
- **Metal in 0-2 / 1-x** (`DPC*`/`DPM*`/`DPS*` MetalHigh, `Metal Decoration *`, doors and grates as MetalGrill). Check:
  - noise and ghosting in `refl` while moving;
  - cost with the GPU timer: `-ultrart-gputimer`, comparing dispatch time against F4 off;
  - whether metals look too dark or too bright in `hybrid` (tune `-ultrart-spec-weight`).
- **Logs**:
  - `Material classes`;
  - `Baked (atlas) slots` (expect hook ≫ unknown);
  - `Reflection material variants` (record growth);
  - no `DispatchRays` failure: "mismatching output texture dimension" means a UAV problem.
- **Tuning**: run with `-ultrart-matdump` and look at `BepInEx/ultrart/material-classes-<scene>.tsv`.
