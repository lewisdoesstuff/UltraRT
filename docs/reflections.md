# Materials and ray-traced reflections

Glossy, metal and ULTRAKILL `REFLECTION` surfaces trace one reflection ray per pixel. The result is filtered and then composited in all three paths (`pt`, `pt-dn`, `hybrid`). Rough surfaces trace nothing, so the cost follows how much of the screen is glossy, and specular weights stay low so the flat PSX look survives.

Real mirrors (layer-8 `Portal` objects with `mirror == true`) are **out of scope**; they belong to the portal work. Transparent water and glass have their own pass (section 12).

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
   - Water (`Water 10A`, `Limbo Water …`, `M_7S_Water`, `M_WaterUps`, but not signs like `WaterControl1`): roughness 0. This only reaches opaque water: the transparent water materials are traced by the water/glass pass (section 12).
   - A Master water material without the keyword: `specular`, f0 0.02, weight 1, so it reflects at grazing angles through Fresnel.
4. **Unlit Master** (no `VERTEX_LIGHTING`, 226 materials: screens, signs, sky props): none.
5. **`_SurfaceType` table.** With `VERTEX_BLENDING`, `_SecondarySurfaceType` becomes the secondary set. The hit shader selects it where vertex colour r < 0.5; on atlas geometry it uses the blend weight `|normal.w| − 1` instead (unverified equivalence).
6. **Unity `Standard`**: only when `_Metallic` > 0.5 or `_Glossiness` > 0.7. Roughness = 1 − glossiness.
7. **Anything else**: class `Other`, none.
8. **Overrides** (see below) are applied last, in file order, and every matching rule applies.

Default table. The weight is relative: the global `-ultrart-spec-weight` (default 0.5) multiplies it. Dielectrics use 0.6 of the metals' weight, so their effective weight stays at the old 0.3 while metals get 0.5 (v2 tuning: metals should read as clearly reflective).

| Class | Mode | Roughness | Metalness | F0 | Weight |
|---|---|---|---|---|---|
| Generic, Carpet, Dirt, Sand, Snow, Grass, Gravel, Rubble, Crunchy, Hologram, Other | none | | | | |
| Wood, WoodThin | specular | 0.75 | 0 | 0.03 | 0.3 |
| Flesh | specular | 0.45 | 0 | 0.03 | 0.36 |
| Marble | specular | 0.25 | 0 | 0.04 | 0.6 |
| Glass | specular | 0.05 | 0 | 0.04 | 0.6 |
| Wet | specular | 0.10 | 0 | 0.03 | 0.6 |
| MetalHigh | specular | 0.20 | 1.0 | (albedo, luma ≥ 0.5) | 1 |
| MetalLow | specular | 0.35 | 0.8 | | 1 |
| MetalThin | specular | 0.30 | 0.7 | | 1 |
| MetalStumpy, MetalGrill | specular | 0.40 | 0.6 | | 1 |
| Mirror | blend 0.6 | 0 | | | |

SurfaceType is a footstep-sound tag, so it is noisy. The shipped `ultrart-materials.json` corrects the worst cases from the research scan:
- `Concrete*` and `CaveRock*` are tagged Marble.
- Lamp fixtures and screens are tagged Glass.
- `M_ShopTerminal` / `M_TestamentTerminal`: CRT glass, specular, roughness 0.08, f0 0.1 (about 5 % head-on, Fresnel towards grazing). The game's `_ReflectionMask` limits it to the screen.
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
| 6 | `_HybridLight` | ARGBInt | w: lit weight unorm8 \| game cube term rgb unorm8 << 8 (section 7) |
| 7 | `_Reflection` | ARGBHalf | **new**: rgb reflected radiance, a = hit distance (sky 1000, −1 = not traced) |
| 8 | `_ReservoirCur` | RWStructuredBuffer<uint4> | ReSTIR |

`_Surface` packing (`PackSurface` in `UltraRTMaterial.hlsl`):
- x: albedo RGB unorm8 \| class << 24 \| mode << 29 \| game cube removed << 31
- y: half emission r \| g << 16
- z: half emission b \| reflection strength << 16 \| game cube keep factor k << 24
- w: the payload's material word

The precision is the same as before: albedo was ARGB32 and emission ARGBHalf.

All consumers now read the unpacked textures produced by the **resolve** pass (`Reflections.compute`, kernel `Resolve`), which writes `_gAlbedo` (ARGB32) and `_gEmission` (ARGBHalf):
- the denoiser's `albedo` and `emission` inputs
- the hybrid composite's `_UltraRTAlbedo`
- the debug `SaveOutput` of denoised modes

The resolve runs in every denoised mode, even with reflections off. The non-denoised `pt*` modes don't need it, because the ray generation shader composites `_Output` itself. `UnpackHybrid.compute` is unchanged.

## 5. The reflection ray (PathTrace RayGen)

At a primary hit, `SpecularTerms(material, albedo, N·V)` gives `specW` (rgb) and `keep`. These are the same weights the composites use (section 7). The pixel traces a ray only if reflections are on, the mode is not `none`, and `luma(specW) ≥ threshold` (default 0.015). This means:
- **Dielectric specular classes** (effective weight 0.3 × F) trace mostly at grazing angles.
  - Marble: 0.3 × 0.04 = 0.012 at normal incidence, below the threshold.
  - Wood: 0.15 × F, so rays only at grazing angles.
- **Metals**: F ≈ albedo (raised to luma ≥ 0.5), so 0.5 × that. They trace almost everywhere.
- **`REFLECTION` materials**: specW = RS × `-ultrart-refl-strength`. For example, FloorPanelSmall (0.025) traces and FloorPanelSmallShine (0.05) does too.

How the ray is traced:
- **Direction**: the exact mirror direction when roughness < 0.03. Otherwise a GGX VNDF sample (Heitz 2018, alpha = roughness²). If the sample ends up below the surface, it falls back to the mirror direction.
- **Estimator**: the radiance along the ray, with no BRDF weight. The composite applies F (split-sum style), and G2/G1 is taken as 1.
- **Origin and flags**: origin `pos + n·0.02`, back faces culled, mask `0xFF`. Sky walls stop the ray and report sky, as on camera rays.
- **Shading at the hit**: `albedo × lerp(1, light + ambient·_BounceAmbient, litWeight) + emission` (`ReflectionHitLight`). Emission is counted in full, because emissive light sampling only covers the diffuse lobe. `light` comes from the radiance cache ([radiance-cache.md](radiance-cache.md)) where it can:
  - **Footprint ≥ 0.35 cell** (`-ultrart-refl-cache auto`, default): the cell's total lighting. No shadow ray, no light-selection speckle, multi-bounce light. Footprint = `hitDist·2·roughness² + (t + hitDist)·pixelAngle`, so rough metals and distant hits use the cache.
  - **Smaller footprint** (mirrors, sharp gloss, near hits): `DirectOne` (one shadow ray, sharp shadows in the reflection) plus the cell's indirect light.
  - **Cell missing or below `minSamples`**: `DirectOne` only, as before.
  - Hybrid scales the cached indirect part by `_GIStrength`, the same weight the bounce gets on the game's colour, so reflected surfaces match the directly seen ones.
  - `always` uses the cell whenever it is valid; `off` restores v1 (`DirectOne` + ambient).
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
- `F = f0' + (max(1 − roughness, f0') − f0')·(1 − N·V)^5`, with `f0' = lerp(f0, metalF0, metalness)`. Metals are tinted by albedo, and roughness damps the grazing peak. `metalF0` is the albedo scaled up to a luminance of at least 0.5 (`ULTRART_METAL_F0_MIN`): ULTRAKILL's metal textures are dark painted diffuse colours (luma 0.1–0.2), and with F = albedo their reflections were nearly invisible.
- `w = materialWeight × specWeight` (global, 0.5).
- `specW = F·w`.

| Path | base | keep |
|---|---|---|
| `pt`, `pt-dn` (full RT) | albedo · lighting (diffuse only; emission stays additive) | `1 − w·lerp(luma(F), 1, metalness)` |
| `hybrid` | game colour × RT shadow ratio + bounce (Composite.shader) | dielectrics `1` (pure add), metals `1 − w·metalness` |

- **Full RT**: the keep term is energy-conserving, a lerp between the PSX diffuse and a physically-based metal.
- **Hybrid**: a deliberate deviation from "always add" for metals. Adding albedo-tinted reflections on top of the game's already-lit metal brightens it noticeably, so metals partly replace the game colour instead.

**`blend`** (REFLECTION, `_CubeMode` 0): `specW = s`, `keep = 1 − s`, with `s = saturate(RS × reflStrength)`. The result is `lerp(base, Lrefl, s)`.

**`additive`** (`_CubeMode` ≠ 0): `specW = RS² × reflStrength`, `keep = 1`. The result is `base + RS²·Lrefl` (the game scales its cube sample by RS twice, see below).

### Replacing the game's cubemap (hybrid)

The hybrid game colour already contains ULTRAKILL's cubemap reflection. It is now **reconstructed and removed** wherever we apply a reflection of our own, so e.g. the shop terminal at a glancing angle shows the RT reflection instead of the stock cube.

**The game's formula**, from the DXBC of `ULTRAKILL/Master`'s `REFLECTION` variants (`$Globals` = `_CubeMode`, `_ReflectionStrength`, `_LightingContribution`, `_ColorContribution`):
- `lit` = the normal shaded colour, including emission and per-vertex fog.
- `R` = per-vertex `reflect(−V, N)` (world space, from the camera), interpolated. The cube is sampled at LOD 0 with a 3-tap (N64-style) filter.
- `C' = cube(R) · lerp(1, _Color, _ColorContribution) · lerp(1, vertexLighting, _LightingContribution)`; `m = _ReflectionMask.r` at the main texture's uv.
- Blend (`_CubeMode` 0): `saturate(lit·(1 − m·RS) + m²·RS·C')`. Additive: `saturate(lit + m·RS²·C')`. The cube term is added after fog.
- So `game = lit·k + C`, with `k = 1 − m·RS` (blend) or 1 (additive).

`_ReflectionMask` **is** used: the terminals' masks cover only the screen glass. Ours follows the mask too (it scales RS for blend/additive and the specular weight otherwise), so the terminal's yellow body no longer reflects.

**Pipeline** (no new UAV, payload unchanged):
1. `GameReflection.Apply` (`MaterialClasses.cs`) copies `_CubeTex`, `_ReflectionMask` and `_GameRefl` = (mode, RS, lighting contribution, colour contribution) to the proxy, regardless of our own classification (an override may change our mode, but the game still draws its cube). Atlas proxies get none: the baked Stationary shader has no cubemap.
2. Hybrid primary rays set the request bit `ULTRART_FLAG_GAMECUBE` (payload flags bit 31) before `TraceRay`. The proxy closest-hit shader recomputes the per-vertex reflection vectors from the triangle's vertices and `WorldRayOrigin()`, samples the cube (one bilinear fetch for the 3-tap filter) and the mask, and returns `C` (3 halves) and `k` plus the lighting contribution in the debug-only `uvHalf`/`primitive` words. Other rays never pay for it.
3. PathTrace applies the lighting tint (`UnshadowedAll + _Ambient`, only if the contribution is > 0) and stores `C` in `_HybridLight.w` (lit weight unorm8 | C rgb unorm8 << 8, previously a half) and `k` in `_Surface` (x bit 31 flag, z bits 24–31). This happens only where our mode is not `none`.
4. Composite: `result = max(game − C, 0)·lerp(1, shadow, lit) + GI`, i.e. the cube is removed before the shadow ratio (it is not lit). Then `result·spec.a + spec.rgb` as before.
5. Resolve: for flagged pixels, `spec.a = min(keep / k, maxCubeKeep = 4)`, since `game − C` still carries the game's `(1 − m·RS)`. With `s = RS` this gives exactly `game − C + s·Lrefl`. The removal also applies where no ray was traced (specular weight below the threshold), so the stock cube does not pop in and out at the threshold.

**Where it can't be done, the game's term stays and ours is skipped**: materials our classification leaves at `none`, baked atlas geometry (no cube to remove), F4 off, or `-ultrart-refl-keepcube`. Known inexact parts: the game saturates its output, so a clipped pixel can't be fully recovered; `C` is at trace resolution while the game's cube is per screen pixel, so high-frequency cube detail leaves a faint residue; bilinear vs the game's 3-tap filter differs slightly at cube texel edges.

Where each path applies it:
- **pt-dn**: the denoiser's composite kernel computes `albedo·lighting·spec.a + emission + spec.rgb` (`DenoiserInputs.specular`, optional).
- **hybrid**: `Composite.shader` computes `result = result·spec.a + spec.rgb` after the shadow and bounce terms. `_gSpecular` is at trace resolution, so reflections are as blocky as the bounce.

## 8. Settings, flags and keys

`ReflectionSettings` (Plugin field `_reflSettings`):

| Flag / key | Field | Default |
|---|---|---|
| `-ultrart-noreflections`, **F4** toggle (resets the reflection history, shows a toast) | `enabled` | on |
| `-ultrart-spec-weight X` | `specWeight` (global weight of specular classes) | 0.5 |
| `-ultrart-refl-strength X` | `reflStrength` (scales REFLECTION materials' RS) | 1.0 |
| `-ultrart-matdump` | `SceneProxies.DumpMaterialClasses` | off |
| `-ultrart-refl-cache off\|auto\|always` | `cacheShading` (radiance cache at reflection hits), `cacheFootprint` (0.35 cell) | auto |
| `-ultrart-refl-keepcube` | `replaceGameCube` = false (hybrid keeps the game's cubemap) | replace |
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

## 9. Cost

**Measured (v2, RTX 5070 Ti, 1920x1080, trace scale 0.5, GPU timer), main ee9095e → reflections-v2:**

| Scenario | hybrid GPU total ms | pt-dn GPU total ms |
|---|---|---|
| short: 1-1 pose | 0.433 → 0.441 | 0.363 → 0.371 |
| short: 1-1 doors run | 1.815 → 1.790 | 1.550 → 1.525 |
| 1-1 terminal (MetalThin room) | 1.919 → 1.822 | 1.699 → 1.597 |
| 1-1 indoor a / b / c | +0.04 / +0.01 / +0.05 | +0.03 / +0.03 / +0.04 |
| 4-1 indoor A | 1.544 → 1.575 | 1.305 → 1.322 |

The cache lookup replaces `DirectOne`'s shadow ray on most glossy hits, so the extra metal coverage (higher weight → more pixels above the ray threshold) is about free. The terminal room got cheaper because the reflection mask stops the terminal body from tracing. The game-cube reconstruction only runs on hybrid primary hits of REFLECTION materials.

The v1 estimates below are kept for reference.

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

- **Tested in game** (v2) on 1-1 and 4-1 poses (`bench/suites/debug-refl-v2.json`): terminal, MetalThin room, indoor stone and wood.
- **Metals in single-colour rooms** (1-1's red MetalThin room) read as a sheen and blurred light-strip reflections, not a mirror: the room reflects itself. Lower roughness via overrides for sharper metal.
- **Reflected objects that move** reproject with the camera only, so moving enemies in mirrors lag by the history.
  - Glossy reflections on curved or non-planar surfaces: the virtual point is wrong, and the code falls back to surface reprojection, which smears a little in motion.
  - The neighbourhood clip limits ghosting, but at 1 spp with rough metals it is loose.
- **Sharp glossy reflections remain somewhat noisy** at 1 spp where the footprint is below the cache threshold (near hits, roughness below about 0.25): those keep the 1-sample direct light. Rough and distant hits read the cache and are smooth. The spatial pass is display-only and small. If it is too noisy: lower `alphaRough` (longer history), lower `-ultrart-spec-weight`, or classify more metals as rough via overrides.
- **SurfaceType is noisy.** Expect odd glossy objects until the override list grows (`-ultrart-matdump`, `materials` view).
- **Atlas slots take one dominant class per source renderer.** A secondary type on baked geometry relies on the unverified `|normal.w| − 1` blend weight.
- **Hybrid cube removal** is approximate at saturated pixels and at trace resolution (section 7).
- **`_ColorContribution` / `_LightingContribution` tint only the removed game term**, not our RT reflection (the RT radiance is real light).
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

## 12. Water and glass

ULTRAKILL's water and glass are `ULTRAKILL/Master` materials with the `TRANSPARENCY` keyword: alpha blended (`_BlendMode` 2), in the transparent queue (3000; two glass variants use 2999/3001), almost all without depth writes. They were never in the RTAS (`Traceable` rejects queue ≥ 3000; the two 2999 glass materials were traced as opaque cutouts and cast RT shadows). Now they are traced, but only by a dedicated pass, and composited by an extra material drawn right after the game's own.

### Survey (offline, UnityPy over `materials.bundle` and the level bundles; renderer counts per level, active or not)

| Kind | Materials | Where (renderers) |
|---|---|---|
| Water | `Limbo Water*` (LIMBO_WATER_FADE, SCROLLING), `Water`, `Water 2`-`9`, `Water 3 Calm`, `Water 10A`-`D` and `1C` (REFLECTION cube, RAIN), `Sea water`, `Water GMod*`, `Slow Flowing Water`; most with VERTEX_DISPLACEMENT waves | 1-1 (7), 1-2 (20), 1-3 (55), 1-4 (7), 2-1 (6), 2-2 (23), 2-3 (21), 2-4 (47), 3-1 (12), 4-x (1-3), 5-1 (18), 5-3 (14), 6-1/6-2 (7/9), 7-1 (13), 7-2 (17), 7-3 (18), 8-1 (11), 8-2 (4, `Water 10A-D`), 8-3 (11) |
| Glass | `Glass` (breakable panes, the most common), `GlassUnbreakable*`, `GlassNoCull`, `GlassLighter`, `StainedGlass1A 10-18` (coloured windows), `StainedGlassBigTransparent`, `Ice*` | 0-1 (49), 0-2 (28), 0-3 (22), 0-4 (29), 1-2 (48), 1-3 (148), 1-4 (25), 2-2 (179), 2-3 (16), 4-2 (76), 5-1 (40), 5-2 (64), 5-3 (84), 7-2 (37), 7-4 (10), 8-1 (98), 8-2 (218), 8-3 (285), 8-4 (21) |
| Not water/glass | lava and acid (`_BlendMode` 3, additive, `Lava` writes depth), waterfalls and light shafts (DISTANCE_FADE), `WaterForceField`, ghost curtains, `Puppet` (ENEMY), `Succulent_*`, `Metal Light 21`; opaque look-alikes (`Lava Opaque`, `Water Fake`, `GlassFake`, `StainedGlassBlue/Red/Big`, `BloodPool`) are already traced as opaque | |

Blood pools (`BloodPool`) are opaque Master materials (Wet class, already glossy). Blood decals are particles/sprites.

### Pipeline

1. **Classification** (`TransparentSurfaces.cs`, `TransparentClassifier.Of`): Master + `TRANSPARENCY` + `_BlendMode` 2, not DISTANCE_FADE/LIMBO_WALLS/ENEMY, not excluded names; water = `Water`/`Sea` name with SurfaceType Wet (or plain `Water`), glass = `*Glass*` or `Ice*`. `Traceable` now rejects these, so they never enter the opaque scene.
2. **Proxies** (`SceneProxies.Transparent.cs`): a second dynamic proxy per renderer with only its water/glass submeshes, instance mask `TransparentMask` 0x10, material class Wet (water) or Glass, roughness 0. Static-batched transparents are skipped (none in the survey). Every other ray now uses `ULTRART_MASK_SCENE` (0xEF), so water and glass still neither block light nor appear in the G-buffer. Emissive-light collection skips them.
3. **Trace** (`PathTrace.raytrace`, `TraceTransparent`, at the end of `RayGen`): in hybrid and denoised modes, only while a transparent instance is active (`_TransParams.y`), the primary pixel also traces a ray with mask 0x10 and `TMax` = the opaque hit. At the water/glass hit it traces one mirror reflection ray (`TraceReflection`, mask 0xEF) about the rippled normal, shaded from the radiance cache (`cacheSpread` forces the cache path: no shadow-ray noise, since there is no temporal filter here). It writes `_Output` = (radiance, distance to the surface; < 0 none) instead of the composited colour, which those modes never read. So there is no new UAV, no new binding and no second `DispatchRays`. A first version used a second ray generation shader; its dispatch rebuilt the shader table and cost +0.2–0.3 ms of frame time beyond the GPU-timed work. Nothing is traced while the camera is inside a water renderer's bounds (see the overlay).
4. **Ripples** (`UltraRTWater.hlsl`): four directional sines in the surface plane, deep-water dispersion (slowed), slope scale `-ultrart-water-waves`, fading out by 60 m. The same function runs in the ray generation shader and in the overlay, so reflection direction and Fresnel agree.
5. **Overlay** (`TransparentOverlay.shader`, `TransparentOverlays`): appended as an extra material to each water/glass renderer (Unity draws the last submesh again with it), with the source material's render queue, so it draws right after the game's surface and before any particle in front of it. This is why it is not a camera command buffer: at `AfterForwardAlpha` everything transparent is already drawn (and PostProcessV2_Handler strips that event's buffers on reinit). Three passes, `ZTest LEqual`, no depth writes:
   - `RefractAdd`/`RefractSub` (water): screen-space refraction. The game produced `W a + B (1 - a)`; the overlay adds `(1 - a)(B(uv') - B(uv))` (signed, hence two blended passes), where `B` = `_UltraRTPreAlpha` (a copy of the colour target taken right after the RT composite, before transparents) and `uv'` follows the rippled refraction offset scaled by the depth below the surface (`_UltraRTGameDepth`). Offsets that land on something in front of the water are rejected. `a` is estimated from the source's texture alpha × `_Color.a` × `_Opacity`.
   - `Reflect`: `dst (1 - w) + w R` with `w` = Schlick Fresnel (f0 0.12 water, 0.08 glass, see below) at the rippled normal × strength, fog visibility, capped at 0.85. `R` is bilinear over the 2×2 trace texels whose transparent hit matches this fragment's distance, tone mapped like relight's composite when relight is on.
   - Water seen from below (camera inside a water renderer's bounds, or under a horizontal surface) is left to the game's own underwater look: a mirror there would need total internal reflection.
   Original material lists are restored when the RT scene is disposed. Nothing is drawn (all passes discard) unless the plugin traced this frame for this camera (`_UltraRTTransOn`, camera position match), so F7, debug modes and other cameras are unaffected.

### Settings

| Flag | Default |
|---|---|
| `-ultrart-notransparent` | on (F4 also switches the pass) |
| `-ultrart-transparent-strength X` (Fresnel weight multiplier) | 1 |
| `-ultrart-water-waves X` (ripple slope scale, 0 = flat mirror) | 1 |
| `-ultrart-water-refraction X` (0 = off, skips the colour copy) | 1 |

F0 is 0.12 (water) and 0.08 (glass), above the physical 0.02/0.04: the game's water and glass textures are bright and fairly opaque, and at physical strength the reflection only showed at grazing angles. GPU timer slot `prealpha`: the colour copy. The trace itself is part of `dispatch`.

### Validation and cost

`bench/suites/water-glass.json` (1-1 fountain from two poses, 1-4 garden glass planter, 7-1 dark pool, 7-1 under water). Run it twice for before/after: `-Extra '-ultrart-notransparent'`, then default. RTX 5070 Ti, 1920×1080, trace scale 0.5, GPU timer:

| Pose | Mode | dispatch ms (before → after) | GPU total ms | prealpha copy |
|---|---|---|---|---|
| 1-1 fountain | hybrid / relight | 0.978 → 1.014 / 0.972 → 1.009 | 1.717 → 1.765 / 1.495 → 1.545 | 0.010 |
| 1-1 fountain top | hybrid / relight | 0.965 → 0.989 / 0.960 → 0.980 | 1.702 → 1.766 / 1.487 → 1.519 | 0.010 |
| 1-4 garden glass | hybrid / relight | 0.838 → 0.855 / 0.838 → 0.863 | 1.481 → 1.474 / 1.257 → 1.292 | 0.010 |
| 7-1 pool (water fills the lower half of the screen) | hybrid / relight | 0.905 → 0.993 / 0.867 → 0.978 | 1.855 → 1.865 / 1.491 → 1.608 | 0.010 |
| 7-1 under water (nothing traced) | hybrid / relight | 0.871 → 1.269 / 0.860 → 1.248 | 1.720 → 2.129 / 1.479 → 1.862 | 0 |

So about +0.02–0.1 ms where water or glass is on screen, plus 0.01 ms for the copy; frame times are within run-to-run noise. The short suite is unchanged (1-1 pose hybrid GPU total 0.486 → 0.481 ms, doors run 1.922 → 1.984 ms, within noise). The under-water row is the exception, see below. Overlay raster cost is three extra draws per visible water/glass renderer; they discard early when nothing applies.

### Limits

- **Camera inside a large water volume** (7-1's pool, 5-x swimming): dispatch +0.4 ms although nothing transparent is traced. The water box's instance sits in the same RTAS as the scene, and masking it out (0xEF) apparently does not stop it from slowing traversal of every ray that starts inside it. A separate RTAS for mask 0x10 would remove that; not done.
- **Static-batched transparents are skipped**: 4-2 has 54 batched glass renderers, 2-1/2-4/7-1 a few. They would need StaticGroups support.
- **No temporal filter**: one ray per trace texel. Reflection hits are shaded from the radiance cache, so they are smooth. Where a cache cell is missing, the hit falls back to `DirectOne` and shows speckle; ripples hide most of it.
- **Game geometry vs. RT mesh**: VERTEX_DISPLACEMENT waves, SCROLLING UVs and LIMBO_WATER_FADE are not reproduced, so the reflection covers the undisplaced surface and the alpha estimate ignores scrolling. The refraction delta assumes the game's blend is exactly `W a + B (1 - a)`.
- **The game's own cubemap stays** on REFLECTION water (`Water 10A-D`, `1C`, `6 Still/Reflection`, `Limbo Water ... V2Arena`), on top of ours. For opaque materials it is removed (section 7), but here it is baked into the game's transparent draw.
- **Renderers whose last material slot is not water/glass** get no overlay (logged as `without overlay`). Unity repeats only the last submesh for an extra material.
- **Total internal reflection, caustics, glass thickness, frosted glass**: not done. Glass is a single mirror-Fresnel surface with no refraction.
- **Relight tone mapping** is replicated in the overlay (`Tonemap`, from `Denoise.compute`). If the exposure/tone-mapping work changes that curve, update both.
- Water/glass are now invisible to shadow, bounce, reflection and cache rays (mask 0xEF). Before, they were absent from the RTAS, except the two queue-2999 glass materials, which were traced as opaque cutouts and cast RT shadows. Those no longer do.
