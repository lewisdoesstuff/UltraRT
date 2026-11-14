# Materials and ray-traced reflections

Glossy, metal and ULTRAKILL `REFLECTION` surfaces trace one reflection ray per pixel. The result is filtered and then composited in all three paths (`pt`, `pt-dn`, `hybrid`). Rough surfaces trace nothing, so the cost follows how much of the screen is glossy, and specular weights stay low so the flat PSX look survives.

Real mirrors (layer-8 `Portal` objects with `mirror == true`) are portals ([portals.md](portals.md)). Transparent water and glass have their own pass (section 12). V1's third-person model shows in reflections, mirrors and shadows, never in the first-person view (section 13).

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
   - `Mirror`, `MirrorTexture`, `GlassReflective`: class `Mirror`, roughness 0. Without the keyword (`MirrorTexture`, a painted fake reflection) they also get `blend` with strength 0.6; the shipped overrides raise `Mirror` to 0.9 and `MirrorTexture` to 0.85 (section 13, "Fake mirrors"). `FerrymanMirror` is not a mirror: it is 5-3's painting of the Ferryman at an easel, and is classified by its surface type like any other texture.
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
- `M_ShopTerminal` / `M_TestamentTerminal`: CRT glass, specular, roughness 0.04, f0 0.25 (about 25 % head-on, Fresnel towards grazing; was 0.08 / 0.1, the user asked for a clearer screen reflection, e.g. a thrown coin behind the player). The game's `_ReflectionMask` limits it to the screen. Up close the screen switches on and the shop UI covers it.
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
- **Origin and flags**: origin `pos + n·0.02`, back faces culled, mask `ULTRART_MASK_SCENE` (0x6F) plus the player model (0x80, section 13). Sky walls stop the ray and report sky, as on camera rays.
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
| Not water/glass | lava and acid (`_BlendMode` 3, additive, `Lava` writes depth; traced as emitters since, docs/lava.md), waterfalls and light shafts (DISTANCE_FADE), `WaterForceField`, ghost curtains, `Puppet` (ENEMY), `Succulent_*`, `Metal Light 21`; opaque look-alikes (`Lava Opaque`, `Water Fake`, `GlassFake`, `StainedGlassBlue/Red/Big`, `BloodPool`) are already traced as opaque | |

Blood pools (`BloodPool`) are opaque Master materials (Wet class, already glossy). Blood decals are particles/sprites.

### Pipeline

1. **Classification** (`TransparentSurfaces.cs`, `TransparentClassifier.Of`): Master + `TRANSPARENCY` + `_BlendMode` 2, not DISTANCE_FADE/LIMBO_WALLS/ENEMY, not excluded names; water = `Water`/`Sea` name with SurfaceType Wet (or plain `Water`), glass = `*Glass*` or `Ice*`. `Traceable` now rejects these, so they never enter the opaque scene.
2. **Proxies** (`SceneProxies.Transparent.cs`): a second dynamic proxy per renderer with only its water/glass submeshes, instance mask `TransparentMask` 0x10, material class Wet (water) or Glass, roughness 0. Static-batched ones are slots of the static groups instead (see "Static-batched water and glass" below). Every other ray now uses `ULTRART_MASK_SCENE` (0xEF, now 0x6F: section 13), so water and glass still neither block light nor appear in the G-buffer. Emissive-light collection skips them.
3. **Trace** (`PathTrace.raytrace`, `TraceTransparent`, at the end of `RayGen`): in hybrid and denoised modes, only while a transparent instance is active (`_TransParams.y`), the primary pixel also traces a ray with mask 0x10 and `TMax` = the opaque hit. At the water/glass hit it traces one mirror reflection ray (`TraceReflection`, the reflection ray's mask) about the rippled normal, shaded from the radiance cache (`cacheSpread` forces the cache path: no shadow-ray noise, since there is no temporal filter here). It writes `_Output` = (radiance, distance to the surface; < 0 none) instead of the composited colour, which those modes never read. So there is no new UAV, no new binding and no second `DispatchRays`. A first version used a second ray generation shader; its dispatch rebuilt the shader table and cost +0.2–0.3 ms of frame time beyond the GPU-timed work. Nothing is traced while the camera is inside a water renderer's bounds (see the overlay).
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

### Blood and limbo water (2026-11-14)

- **Blood** (0-2's `Mulch` river) read as thin water in RT: the game's blood is alpha blended, and the floor under it,
  relit, is much brighter and more contrasted than the game's, so it showed through. The overlay's `Absorb` pass (blood
  only, multiplicative, before `Reflect`) multiplies what shows through by `1 - (1 - a) s (1 - 0.6 tint)` (tint = the
  blood colour normalised, `s` = `bloodAbsorb` 0.9, scaled by the depth behind the surface over 0.1 m and by fog), and the
  sheen is weaker (`bloodWeight` 0.7 -> 0.4).
- **LIMBO_WATER_FADE** (`Water 2` over 1-3's floor and grass beds): `LimboWater` passes the player's and every enemy's
  position (`_FadePositions`), and the game fades the water out within `_NearClip`..`_FarClip` (3..4 m) of them with a
  `_DitherTexture` edge: alpha x `saturate(n + (d - near) / (far - near))`, n = length(rg) of the dither at uv x 4
  (decompiled). The overlay applies the same factor to its reflection and refraction. The water itself really is there
  (faint, colour alpha 0.37); our reflection makes it visible over the beds, which the user accepted.

### Static-batched water and glass (2026-11-14)

Before, renderers in a static batch were skipped (`skipped static-batched N` in the log): 8-2 171 (2 water, 169 glass),
4-2 54 glass, 0-2 8 blood (pieces of the `9-9B Tunnel` blood river), 2-1 2 water, 2-4 2 glass, 7-1 1 water. Now they are
traced, all with an overlay (`static-batched W water, B blood, G glass` in the same log line; the build log lists every
batched water/blood renderer and the first glass ones).

- **8-2's starting pool** is `Exteriors/1 - Opening Hallway/1 Nonstuff/Upside/Sprite/Sprite(Clone)`: a static-batched
  MeshRenderer (layer 24) with `Water 10B` (REFLECTION, TRANSPARENCY, VERTEX_LIGHTING), 15 x 60 m at y -12.3, 0.2 m
  above the floor portal (`.../Portal`, y -12.5) that shows the upside-down street copy at x +300 (the game's fake
  reflection). Its twin over the copy is `Downside/Sprite (1)/Sprite(Clone)` (`Water 10A`). With the water traced, the
  pool reflects V1 and the hallway (ripples, Fresnel) on top of the portal view, in relight and hybrid
  (`bench/suites/static-water.json`, poses `8-2 pool` straight down and `8-2 pool oblique`). Nothing portal-specific
  was needed: the transparent ray (mask 0x10, `TMax` = the opaque hit, which is past the portal) hits the water first,
  its reflection ray goes up (away from the portal) and sees V1 by bit 1 of `_playerRays`, and the overlay's depth
  test passes because the game's depth at portal pixels is the portal plane, below the water.
- **How**: transparent submeshes of a combined mesh become slots of the static groups (`StaticGroups.cs`) with part bit
  2 (`TransparentPart`, so groups have up to 8 parts: two-sided x sky wall x transparent). A transparent part is its own
  instance, mask `TransparentMask` 0x10, material the same transparent proxy as the dynamic ones (`TransparentProxy`),
  counted in `ActiveTransparent`. So room activation (watchers, the slice poll, checkpoint clones as late sources),
  partial group meshes and the merge by material work as for opaque slots. `ProcessMeshRenderer` lets batched renderers
  with only water/glass materials through to the batch list (`TransparentCandidate`), but not into `IsLightFixture`:
  lamp glass blocks no ray, and counting it as a "static housing" moved 8-2's street-light lifts and shaft lights (the
  look-up view got darker before that was fixed). Emissive collection (`CollectStaticSubmeshes`) and static material
  swaps skip transparent slots.
- **Overlay on a batched renderer**: appending the overlay material works as for unbatched ones. Unity draws the extra
  material with the renderer's own last submesh of the combined mesh, so the size check against `mesh.subMeshCount` is
  skipped for batched renderers (it is the combined mesh's). Checkpoint clones copy the material list with our overlay
  in it: `TransparentOverlays.Attach` adopts such a list (so Dispose removes it there too), and `CreateSource` ignores
  overlay materials (their index would land on the next source's slot).
- **Cost** (RTX 5070 Ti, 2560x1440, trace scale 0.5, relight, visual runs on the alt slot, dispatch ms master -> branch):
  8-2 pool straight down (water fills the screen) 3.52 -> 4.89 (+1.37), 8-2 pool oblique (about 60 %) 3.45 -> 4.26
  (+0.80), 4-2 glass roof filling the view 1.69 -> 2.01 (+0.32), 4-2 glass wall 2.15 -> 2.21; no change where none is in
  view (8-2 look up, 2-1, 2-4, 0-2 blood river, all of `water-glass.json`). It is the transparent hit plus one reflection
  ray per trace texel; 8-2's per-ray cost is higher than 7-1's (+0.1 ms for half a 1080p screen) in its bigger scene.
  CPU: 8-2's static build 240 -> 245 ms, +18 shader-table records (480 -> 498, level 4), 54 -> 64 part instances;
  4-2 +3 records. Per-frame static sync unchanged within noise.
- **Regression**: `water-glass.json` and the blood river match master at the same resolution (mean luma within 0.5).

### Limits

- **Camera inside a large water volume** (7-1's pool, 5-x swimming): dispatch +0.4 ms although nothing transparent is traced. The water box's instance sits in the same RTAS as the scene, and masking it out (0xEF) apparently does not stop it from slowing traversal of every ray that starts inside it. A separate RTAS for mask 0x10 would remove that; not done.
- **No temporal filter**: one ray per trace texel. Reflection hits are shaded from the radiance cache, so they are smooth. Where a cache cell is missing, the hit falls back to `DirectOne` and shows speckle; ripples hide most of it.
- **Game geometry vs. RT mesh**: VERTEX_DISPLACEMENT waves and SCROLLING UVs are not reproduced (LIMBO_WATER_FADE is, see above), so the reflection covers the undisplaced surface and the alpha estimate ignores scrolling. The refraction delta assumes the game's blend is exactly `W a + B (1 - a)`.
- **The game's own cubemap stays** on REFLECTION water (`Water 10A-D`, `1C`, `6 Still/Reflection`, `Limbo Water ... V2Arena`), on top of ours. For opaque materials it is removed (section 7), but here it is baked into the game's transparent draw.
- **Renderers whose last material slot is not water/glass** get no overlay (logged as `without overlay`). Unity repeats only the last submesh for an extra material.
- **Total internal reflection, caustics, glass thickness, frosted glass**: not done. Glass is a single mirror-Fresnel surface with no refraction.
- **Relight tone mapping** is replicated in the overlay (`Tonemap`, from `Denoise.compute`). If the exposure/tone-mapping work changes that curve, update both.
- Water/glass are now invisible to shadow, bounce, reflection and cache rays (mask 0xEF). Before, they were absent from the RTAS, except the two queue-2999 glass materials, which were traced as opaque cutouts and cast RT shadows. Those no longer do.

## 13. The player in reflections, and fake mirrors

ULTRAKILL's player prefab carries a full third-person V1: `Player/GroundCheck/v1_combined` (`PlayerAnimations`, an `Animator`), with the skinned body `v1_mdl` and one third-person copy of every weapon on the right forearm (`StandardRevolver_NewArm/Revolver_Body`, `ShotgunNewAnims/Shotgun_New`, ...; `PlayerAnimations.UpdateWeapon` activates the current one). All of it is on layer 30, which only portal views draw: that is how V1 shows in 8-x's portal mirrors. The first-person viewmodel (arms and guns under `Main Camera`, layer 13) is a separate set of renderers and is not in the RTAS; it is lit by its own pass ([viewmodel.md](viewmodel.md)).

Files: `src/UltraRT/SceneProxies.Player.cs` (finding the model, mask, animator, shadow box), `SceneProxies.cs` (registration), `Plugin.cs` (settings and globals), `UltraRTCommon.hlsl`, `UltraRTPortals.hlsl` (`PlayerRayMask`, `TraceScene`), `UltraRTLights.hlsl` (`VisibleMasked`), `PathTrace.raytrace`, `CacheUpdate.raytrace`.

### Instance mask 0x80

Every renderer under `PlayerAnimations` (except layer 13) gets a normal dynamic proxy (skinned or rigid; the skinned ones share the bones and follow the root bone, docs/scene-proxies.md "Skinned meshes") with instance mask `SceneProxies.PlayerMask` = **0x80**, the last free bit (0x01 geometry, 0x02 sky walls, 0x04 fixtures, 0x08 emitters, 0x10 water/glass, 0x20 portals, 0x40 layer-30 props). `ULTRART_MASK_SCENE` dropped it (0xEF → **0x6F**), so no ray sees V1 unless it adds the bit:

| Ray | Sees V1 | Why |
|---|---|---|
| Camera (primary) rays, all modes and debug views | **never** before a portal hop | the camera sits inside V1's head and the third-person gun arm reaches in front of it (`v1_mdl`'s bounds contain the camera) |
| Camera rays after a hop through a portal with `canSeePortalLayer` | yes (bit 8) | what the game's portal views draw: 8-x mirrors |
| Reflection rays (incl. the water/glass pass) | yes (bit 1) | the point of the feature |
| Bounce rays (PathTrace) | yes (bit 2) | V1 occludes and reflects bounce light near the player |
| Shadow rays (PathTrace: direct light, sky, emissive, reflection hits) | yes (bit 4) | V1 casts a shadow, as in Quake II RTX |
| Radiance cache rays and shadow rays (CacheUpdate) | no | the cache is world-space and temporally accumulated: a moving player would leave stale shadows in it |
| Volumetrics shadow rays | no | they march from the camera, i.e. from inside the model, and don't cull back faces |
| Water/glass pass (mask 0x10) | no | |

The bits are Plugin's `_playerRays` (default 15 = all four), uploaded every frame as the global `_UltraRTPlayerRays`: `-ultrart-player-rays N`, bench `plugin._playerRays=N` (0 = V1 nowhere, the A/B baseline). `-ultrart-noplayer` leaves the model out of the RTAS altogether (read at scene build; layer-30 renderers then fall back to the portal-layer mask as before). In `TraceScene`, a ray that crosses a portal keeps V1 only if that portal can see layer 30, like layer-30 props; a camera ray gains it there.

**Self-occlusion.** Rays never start at the camera except camera rays, which exclude V1. Shadow, bounce and reflection rays start at surfaces and cull back faces, so a ray that starts inside the model passes out through it. The one real trap is the player's own lights: muzzle flashes and other effect lights sit at the first-person gun, which is inside the third-person arm and gun. A shadow ray toward such a light would be blocked by V1's arm and cast its shadow across the room with every shot. So `VisibleMasked` adds the player bit only when the ray's end point (the light) lies outside `_UltraRTPlayerBoxMin/Max`: a 2 m box around the camera, grown to hold the active third-person weapon's bounds plus 0.3 m (`SceneProxies.PlayerShadowBox`; the body is left out, its bounds are about 2.4 x 3.9 x 3.1 m with the wings and would exempt every nearby light). Measured with the bench's revolver effect in 1-1's terminal room (`1-1 terminal shots`): the image with V1 differs from `_playerRays=0` only by V1's body shadow on the far wall and noise, no gun shadow.

**The player's own lights** (2026-11-14 pm, user report: 7-3's flashlight cast V1's shadow down the path ahead). The box only covers lights whose shadow-ray end point lands near the camera; the 7-3 flashlight (`Player/Main Camera/Flashlight`, a spot light, range 120, angle 35) and soft-shadow samples at a light's edge did not always. Every packed light now carries a flag in `_Lights[i*4+1].w` (`Plugin.FlagPlayerLights`, `src/UltraRT/Plugin.PlayerLights.cs`): 1 when the light is in the player's or the camera's hierarchy (the flashlight, a held item such as 4-3's torch, weapon lights, muzzle flashes) or within 0.75 m of the eye (Torch.Update moves a held torch's light to the player). `LightVisible` (UltraRTLights.hlsl) clears `g_ShadowPlayerMask` for flagged lights, so V1 never shadows them, wherever they are. The hierarchy test is cached per light and refreshed with the 4 Hz active-light scan; cost is one `IsChildOf` per light per refresh. Screenshot: `bench/results/20261114-184932_lights-pm-check/7-3_flashlight_2.0s_relight.png` (no V1 shadow; before: `20261114-182905_lights-pm-diag/7-3_flashlight_2.0s_relight.png`, matching the user's).

### Animation

`PlayerAnimations.LateUpdate` drives the model from `NewMovement` (velocity, sliding, dodging, in air, jump/land, shoot/punch/coin triggers) and turns the head, chest and right arm to the camera every frame. The Animator ships with `cullingMode = CullUpdateTransforms`; its renderers are on layer 30, which the main camera never draws, and our proxies never render, so outside portal views Unity could stop updating the bones. `SceneProxies.PlayerAlwaysAnimate` sets `AlwaysAnimate` while the RT scene exists and restores the original on dispose (`-ultrart-player-noanimate` skips it). The 600-frame log line `Player model: ...` reports the proxies in the RTAS, the animator's culling mode, state and normalized time (it advances: 22.4 → 26.0 → 32.6 over successive logs in one level), and where the root bone and bounds are relative to the camera. The RT pose is the skinned pose (`DynamicGeometry` re-reads it every build) and the instance follows the root bone.

Not verified: running, sliding and dodging poses. The benchmark takes the player over (kinematic rigidbody, zero velocity), so every bench shot shows the idle pose with wings spread and the head and gun arm following the camera (which they do: the mirror image looks where the camera looks). A recording replays the camera path the same way. Checking locomotion needs real play in front of a mirror.

### Fake mirrors

Survey (UnityPy over `materials.bundle` and every scene and prefab bundle, renderers by material):

| Material | Where | What it is | Now |
|---|---|---|---|
| `Mirror` (Master, REFLECTION blend 0.35, generic cubemap, no texture, white) | 1-4, `3TR - Shelf Room/3TR Nonstuff/Table/Cube`: a vanity mirror on the bedroom's dressing table (bounds centre (10.0, 2.9, 502.0), 1.6 x 2.8 m, faces +x), behind the game's crack-shard mesh | the "shiny glass" fake mirror | class Mirror, roughness 0, blend **0.9** (`ultrart-materials.json`), V1 visible |
| `MirrorTexture` (plain texture of a painted reflection) | credits museum bathroom sinks (`bathroom Portal`, 6 renderers), `gameprefabs` | painted fake mirror | blend **0.85** |
| `GlassReflective` (REFLECTION additive 0.5) | 8-1 rooftop glass walls | reflective glass | unchanged (roughness 0, additive) |
| `FerrymanMirror` | 5-3 paintings (`Painting`, `PaintingBig`) | **not a mirror**: a painting of the Ferryman at an easel | removed from the mirror allow-list: classified by its surface type like any texture (it used to be 60 % replaced by a reflection) |

All real mirrors are portals, only in 8-1 to 8-4 (see [portals.md](portals.md)). The build logs every mirror- or terminal-named surface (`Mirror surface '<path>' material ... bounds ... forward ...`).

### Validation

`bench/suites/self-reflection.json` (every pose A/B `plugin._playerRays=0` vs default), `self-reflection-8-3.json` (8-3 portal mirror, with the portal-depth overlay and `_portals=0`), `self-reflection-cost.json` (cost per ray kind). RTX 5070 Ti, 2560x1440, trace scale 0.5, GPU timer; results in `bench/results/20261114-125107_self-reflection`, `20261114-131836_self-reflection-8-3`, `20261114-131024_self-reflection-cost` (not committed).

**First person.** No V1 part shows in any first-person view. The `albedo` debug view (pure camera rays) looking straight down at 1-1's fountain has no legs, gun arm or wings; relight and hybrid show only V1's shadow on the fountain rim.

**What it looks like** (`20261114-125107_self-reflection`):
- 1-1 terminal room (`1-1 terminal`, `1-1 terminal head-on`): relight casts V1's shadow (head, body, spread wings) on the wall next to the terminal, from the room's ceiling lights; hybrid shows it faintly through the shadow ratio. The terminal screen reflects V1: clearly in `refl-raw` (dark body, yellow wings), faintly in the final image because the CRT glass override reflects about 10 % head-on. Up close, the shop's UI canvas is drawn over the screen anyway.
- 1-4 vanity mirror (`1-4 mirror`, `1-4 mirror far`): in relight and pt-dn, V1's yellow wings and blue body show in the mirror behind the game's crack-shard mesh; with `_playerRays=0` they don't. In hybrid the mirror shows the RT reflection of the unlit night bedroom: dark, with 1-spp sparkle (mirrors skip the spatial filter), in both A/B variants. At the game's 0.35 cube blend this was hidden; at 0.9 it is visible. The relight look is the intended one.
- 1-1 fountain, 0-1 metal room: V1's shadow where a light is behind the player; no change in the reflections at these poses (grazing views of floors and water reflect what is ahead, not the player).

**GPU cost** (RTX 5070 Ti, 2560x1440, trace scale 0.5, GPU timer; total ms, `_playerRays=0` → 15, same pass):

| Pose | hybrid | relight |
|---|---|---|
| 1-1 terminal (glancing) | 3.904 → 3.983 (+0.08) | 4.390 → 4.532 (+0.14) |
| 1-1 terminal head-on | 3.996 → 4.133 (+0.14) | 4.404 → 4.558 (+0.15) |
| 1-1 look down | 3.750 → 3.765 (+0.02) | 4.424 → 4.432 (+0.01) |
| 1-1 fountain top | 3.205 → 3.213 (+0.01) | 3.877 → 3.884 (+0.01) |
| 0-1 metal | 4.233 → 4.249 (+0.02) | 4.340 → 4.397 (+0.06) |
| 1-4 mirror | 3.111 → 3.119 (+0.01) | 3.784 → 3.861 (+0.08) |
| 8-3 mirror (Escher Entrance) | 4.366 → 4.512 (+0.15) | 4.818 → 4.973 (+0.16) |

All of it is in the dispatch: the BLAS was already built every frame (V1 was in the RTAS before, as a layer-30 prop that only portal hops saw), so the cost is traversal of V1's instance by the rays that now include it, mostly shadow rays toward lights behind the player in lit rooms. Plugin CPU is unchanged (3 extra instances out of 25 player renderers are active: body, revolver, cylinder). Per ray kind (`self-reflection-cost.json`, terminal head-on, relight, dispatch ms, one pass): none 2.822, reflection rays only 2.906 (+0.08), bounce only 2.859 (+0.04), shadow only 2.861 (+0.04), all 2.985 (+0.16). The room's walls are glossy metal (MetalThin), so most pixels trace a reflection ray, and many of those pass V1. `-ultrart-player-rays 1` (reflections and portal views only, no shadow or bounce) is the cheap setting. `merge-regress` matches master's images.

**8-x portal mirrors.** Best pose: `8-3 secret mirror` in `self-reflection-8-3.json`, in front of the Escher Entrance's `Secret/Mirror` (a bathroom, reached through the TV portal). The game's own mirror view there shows the whole V1. The other mirror, `CeilingSegment/Mirror`, hangs about 10 m above the room's floor, so a pose in front of it (`8-3 mirror` in `self-reflection.json`, portals.json's pose) puts half of V1 inside the ceiling geometry; don't judge V1 there.
- **hybrid**: V1 shows in the mirror, because hybrid keeps the game's portal view at portal pixels.
- **relight / pt-dn**: by default every mode keeps the game's own mirror view (docs/portals.md, "Mirrors keep the game's view"), so V1 shows as the game draws it. With `-ultrart-rtmirrors` (bench `plugin._rtMirrors=1`) camera rays reflect off the mirror and add 0x80 after the hop (bit 8), so the traced reflection shows V1, lit by the RT lights (often darker than the game's view). The afternoon of 2026-11-14 "RT misses portal quads" was not the resolution: portal quads added to the RTAS in the frame they were made were never hit until re-added, and `self-reflection-8-3.json` runs `_portals=0` last (docs/portals.md, "Portal quads added too early"; fixed).

**Liquid blood** (added 2026-11-14): transparent blood liquids (0-2's blood river `Mulch`, other alpha-blended `Blood*` Master materials) are a third kind, `TransparentKind.Blood`. It is traced like water/glass but reads as thick and opaque: the reflection ray treats it as flat (Glass class, no ripples), the overlay skips refraction (`_Kind` 2) and uses F0 0.06 with the reflection weight capped at 0.7 (`TransparentSettings.bloodF0`, `bloodWeight`). 6-2's blood is drawn with a water material and stays water. Opaque `BloodPool` and particle/decal blood are unaffected.
