# Materials and ray-traced reflections

Glossy, metal and ULTRAKILL `REFLECTION` surfaces trace one reflection ray per pixel. The result is filtered and then composited in every lit path (`pt`, `pt-dn`, `relight`, `pathtrace`; also `hybrid` until its removal on 2026-11-19). Rough surfaces trace nothing, so the cost follows how much of the screen is glossy, and specular weights stay low so the flat PSX look survives.

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
| `uvHalf` | half(uv.x) \| half(uv.y) << 16, debug views only (`PayloadUV()`); camera hits with `ULTRART_FLAG_CAUSTIC` (flags bit 31): the caustic's r, g (docs/caustics.md, section 5) |
| `primitive` | debug views only; camera hits with a detail normal: the vertex normal (bits 0-23); with `ULTRART_FLAG_CAUSTIC`: 1 + the caustic volume (bits 24-31). The caustic's b is in `emissionHalf.y`'s high half |
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

Since 2026-11-19 (hybrid and ReSTIR removed) the list is 7: `_HybridLight` is `_LitWeight` (w: the lit weight only) and `_ReservoirCur` is gone, so one UAV slot is free. The game-cube bits of `_Surface` below (x bit 31, z bits 24-31) are unused since then.

`_Surface` packing (`PackSurface` in `UltraRTMaterial.hlsl`):
- x: albedo RGB unorm8 \| class << 24 \| mode << 29 \| game cube removed << 31
- y: half emission r \| g << 16
- z: half emission b \| reflection strength << 16 \| game cube keep factor k << 24
- w: the payload's material word

The precision is the same as before: albedo was ARGB32 and emission ARGBHalf.

All consumers now read the unpacked textures produced by the **resolve** pass (`Reflections.compute`, kernel `Resolve`), which writes `_gAlbedo` (ARGB32) and `_gEmission` (ARGBHalf):
- the denoiser's `albedo` and `emission` inputs
- the hybrid composite's `_UltraRTAlbedo` (until 2026-11-19)
- the debug `SaveOutput` of denoised modes

The resolve runs in every denoised mode, even with reflections off. The non-denoised `pt*` modes don't need it, because the ray generation shader composites `_Output` itself. `UnpackHybrid.compute` was unchanged (it went with the hybrid mode on 2026-11-19).

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
  - (Hybrid scaled the cached indirect part by `_GIStrength`, the weight of its bounce on the game's colour, until the mode was removed on 2026-11-19.)
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
| `hybrid` (removed 2026-11-19) | game colour × RT shadow ratio + bounce (Composite.shader) | dielectrics `1` (pure add), metals `1 − w·metalness` |

- **Full RT**: the keep term is energy-conserving, a lerp between the PSX diffuse and a physically-based metal.
- **Hybrid**: a deliberate deviation from "always add" for metals. Adding albedo-tinted reflections on top of the game's already-lit metal brightens it noticeably, so metals partly replace the game colour instead.

**`blend`** (REFLECTION, `_CubeMode` 0): `specW = s`, `keep = 1 − s`, with `s = saturate(RS × reflStrength)`. The result is `lerp(base, Lrefl, s)`.

**`additive`** (`_CubeMode` ≠ 0): `specW = RS² × reflStrength`, `keep = 1`. The result is `base + RS²·Lrefl` (the game scales its cube sample by RS twice, see below).

### Replacing the game's cubemap (hybrid, removed 2026-11-19)

This subsection is history except for the game's formula and **Pattern cubes**, which still apply: the removal below
went with the hybrid mode on 2026-11-19 (`ULTRART_FLAG_GAMECUBE`, the `_Surface` keep bits, `maxCubeKeep`,
`-ultrart-refl-keepcube`). Every mode that remains replaces the game's image, so there is no game cube to remove.

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

**Pattern cubes** (2026-11-14): `DiamondCubemap` (3-2's big eye `M_BigEye*`, `Eye`, `DiamondRed`, `Water 6 Reflection`)
and `SwordCubemap` (Gabriel's swords) are stylised patterns, not environment captures (`GameReflection.IsPatternCube`).
An RT reflection in their place lost the pattern: the eye (additive, RS 0.5, `_LightingContribution` 1) became a flat
pink blob. Their materials now classify as `none` ("pattern cube") and the proxy gets `_GameCubeLit` 1: every ray adds
the game's own term in the hit shader, as `albedo·k + C·lc` (lit by RT lighting) plus `C·(1 − lc)` as emission, `lc` =
`_LightingContribution` (until 2026-11-19 hybrid primaries were the exception: their game colour already held the cube). Remaining difference on the eye in relight: its underside, lit from below by the
mouth lights, clips to a flat bright pink where the game's per-vertex lighting stays dimmer and shows the pattern; that
is light intensity / exposure, not the material.

**Where it can't be done, the game's term stays and ours is skipped**: materials our classification leaves at `none`, baked atlas geometry (no cube to remove), F4 off, or `-ultrart-refl-keepcube`. Known inexact parts: the game saturates its output, so a clipped pixel can't be fully recovered; `C` is at trace resolution while the game's cube is per screen pixel, so high-frequency cube detail leaves a faint residue; bilinear vs the game's 3-tap filter differs slightly at cube texel edges.

Where each path applies it:
- **pt-dn**: the denoiser's composite kernel computes `albedo·lighting·spec.a + emission + spec.rgb` (`DenoiserInputs.specular`, optional).
- **hybrid** (until 2026-11-19): `Composite.shader` computed `result = result·spec.a + spec.rgb` after the shadow and bounce terms. `_gSpecular` is at trace resolution, so reflections were as blocky as the bounce.

## 8. Settings, flags and keys

`ReflectionSettings` (Plugin field `_reflSettings`):

| Flag / key | Field | Default |
|---|---|---|
| `-ultrart-noreflections`, **F4** toggle (resets the reflection history, shows a toast) | `enabled` | on |
| `-ultrart-spec-weight X` | `specWeight` (global weight of specular classes) | 0.5 |
| `-ultrart-refl-strength X` | `reflStrength` (scales REFLECTION materials' RS) | 1.0 |
| `-ultrart-matdump` | `SceneProxies.DumpMaterialClasses` | off |
| `-ultrart-refl-cache off\|auto\|always` | `cacheShading` (radiance cache at reflection hits), `cacheFootprint` (0.35 cell) | auto |
| — | `threshold` (minimum luma(specW) for a ray), `maxRadiance` (8) | 0.015 |
| — | `temporal`, `spatial`, `alphaMirror`/`alphaRough` (0.3/0.08), `clampMirror`/`clampRough` (1.25/2.5), `maxHistory` (32), `spatialMinRoughness` (0.08) | |

**Debug views** (Page Up / Page Down cycle the view modes; F8 did once; appended after `primitive`, so existing indices are unchanged):
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
- **`_ColorContribution` / `_LightingContribution` tint only the game's term** (pattern cubes), not our RT reflection (the RT radiance is real light).
- **Unlit Master materials get no specular**, by design. Override them to opt in.
- **Footstep-clone fallback**: `PhysicsSceneStateEnforcer.matchingObject` is read by reflection (private field). If the game renames it, only the fallback is lost.

### 0-5: does the metal reflect the lava? (2026-11-22, playtest pose 153, check only)

User, playtest of 2026-11-22, 0-5 pose 1 (`bench/recordings/u1122-0-5-1.json`, the lava channel, looking down along the
left wall): "should I expect to see lava reflecting in the metal here?" Yes, and it does, but as an orange sheen, not as
a mirror image of the lava. Measured on the alt install (run `20261122-004359_tmp-flm-a_alt`, 2560x1440, `pathtrace`):

- **The wall is reflective metal.** `spec-params` shows smoothness 0.8, metalness 1 and weight 1 over the whole wall:
  class MetalHigh, roughness 0.20, from the game's footstep `SurfaceType` (0-5's `Metal Decoration 1` / `1B` / `4` and
  their `Orange` variants, textures `floordecoration1`, `1b`, `4`; `-ultrart-matdump`). Its F0 is its own albedo (raised to
  luma 0.5), so it reflects about half the light it sees, tinted by its own orange-brown.
- **The reflection rays see the lava.** `refl-raw` shows the lava's scrolling emission (the lava proxy, mask 0x08, docs/lava.md)
  in the lower band of the wall: (240, 151, 13) there against (21, 15, 7) higher up, where the mirror direction looks into
  the dim girders and ceiling.
- **Why it reads as a sheen.** The lava is orange-yellow, the metal tints what it reflects orange, and the wall's own
  diffuse colour is orange too. The lobe at roughness 0.2 (GGX alpha 0.04) plus the detail maps' per-texel normals spread
  the image over many texels. With reflections off (`pathtrace+refl=0`) the wall is brighter higher up (it loses the
  dark girders it reflects) and slightly duller at the bottom: mean |difference| 13.5 of 255 over the wall, mean colour
  (141, 69, 17) without against (119, 57, 15) with. That is the reflection: real, but it doesn't look like lava.
- Not changed: a sharper, less tinted reflection would need a lower roughness or a less coloured F0 for these materials
  (an override in `ultrart-materials.json`). The `Metal Decoration` materials are used in other levels too (section 11
  lists them for 0-2 and 1-x), so that would be a look change well beyond this wall; this is the case of "Metals in
  single-colour rooms" above.

## 11. What to test in game

- **1-1** marble floors: `FloorPanelSmall*` (REFLECTION blend, RS 0.025–0.05, roughness 0.15), `FloorPanelBig` (additive 0.1) and Marble-class `Floor`. Check:
  - `refl` and `refl-raw` look right;
  - `relight` shows subtle floor reflections of the room;
  - `materials` shows the striped panels;
  - reflections stay put while strafing (virtual reprojection).
- **Water in early levels** (`Water 1C`, `Water 10A–D`, `Limbo Water*`): roughness-0 mirror reflections of the sky and geometry; sky walls should reflect as sky.
- **4-1**: emissive-lit interiors. Reflected emitters should be visible in glossy floors and metals; check `pt-dn` for energy and that nothing over-brightens.
- **0-5**: lava (`Lava*`, Wet class, typically EMISSIVE). Check it doesn't turn into a mirror and that reflections of lava glow on nearby metal.
- **Metal in 0-2 / 1-x** (`DPC*`/`DPM*`/`DPS*` MetalHigh, `Metal Decoration *`, doors and grates as MetalGrill). Check:
  - noise and ghosting in `refl` while moving;
  - cost with the GPU timer: `-ultrart-gputimer`, comparing dispatch time against F4 off;
  - whether metals look too dark or too bright in `relight` (tune `-ultrart-spec-weight`).
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
3. **Trace** (`PathTrace.raytrace`, `TraceTransparent`, at the end of `RayGen`): in hybrid and denoised modes, only while a transparent instance is active (`_TransParams.y`), the primary pixel also traces a ray with mask 0x10 and `TMax` = the opaque hit. At the water/glass hit it traces one mirror reflection ray (`TraceReflection`, the reflection ray's mask) about the rippled normal, shaded from the radiance cache (`cacheSpread` forces the cache path: no shadow-ray noise). Since 2026-11-19 a temporal filter runs on the result before the overlay reads it ("Displaced water and the temporal filter" below). It writes `_Output` = (radiance, distance to the surface; < 0 none) instead of the composited colour, which those modes never read. So there is no new UAV, no new binding and no second `DispatchRays`. A first version used a second ray generation shader; its dispatch rebuilt the shader table and cost +0.2–0.3 ms of frame time beyond the GPU-timed work. Nothing is traced while the camera is inside a water renderer's bounds (see the overlay).
4. **Ripples** (`UltraRTWater.hlsl`): four directional sines in the surface plane, deep-water dispersion (slowed), slope scale `-ultrart-water-waves`, fading out by 60 m. The same function runs in the ray generation shader and in the overlay, so reflection direction and Fresnel agree.
5. **Overlay** (`TransparentOverlay.shader`, `TransparentOverlays`): appended as an extra material to each water/glass renderer (Unity draws the last submesh again with it), with the source material's render queue, so it draws right after the game's surface and before any particle in front of it. This is why it is not a camera command buffer: at `AfterForwardAlpha` everything transparent is already drawn (and PostProcessV2_Handler strips that event's buffers on reinit). Three passes, `ZTest LEqual`, no depth writes:
   - `RefractAdd`/`RefractSub` (water): screen-space refraction. The game produced `W a + B (1 - a)`; the overlay adds `(1 - a)(B(uv') - B(uv))` (signed, hence two blended passes), where `B` = `_UltraRTPreAlpha` (a copy of the colour target taken right after the RT composite, before transparents) and `uv'` follows the rippled refraction offset scaled by the depth below the surface (`_UltraRTGameDepth`). Offsets that land on something in front of the water are rejected. `a` is estimated from the source's texture alpha × `_Color.a` × `_Opacity`.
   - `Reflect`: `dst (1 - w) + w R` with `w` = Schlick Fresnel (f0 0.12 water, 0.08 glass, see below) at the rippled normal × strength, fog visibility, capped at 0.85. `R` is bilinear over the 2×2 trace texels whose transparent hit matches this fragment's distance, tone mapped like relight's composite when relight is on.
   - On water the game moves with `VERTEX_DISPLACEMENT` (6-2's blood pools, 3-1, 2-1's `Water 3 Calm`), the overlay's vertex shader displaces like the game since 2026-11-19 (below).
   - Water seen from below (camera inside a water renderer's bounds, or under a horizontal surface) is left to the game's own underwater look: a mirror there would need total internal reflection.
   Original material lists are restored when the RT scene is disposed. Nothing is drawn (all passes discard) unless the plugin traced this frame for this camera (`_UltraRTTransOn`, camera position match), so F7, debug modes and other cameras are unaffected.

### Settings

| Flag | Default |
|---|---|
| `-ultrart-notransparent` | on (F4 also switches the pass) |
| `-ultrart-nofrosted` (bench `trans.frosted=0`) | frosted transparents (ice) also in the scene: on |
| `-ultrart-transparent-strength X` (Fresnel weight multiplier) | 1 |
| `-ultrart-water-waves X` (ripple slope scale, 0 = flat mirror) | 1 |
| `-ultrart-water-refraction X` (0 = off, skips the colour copy) | 1 |
| bench `trans.temporal` (the reflection's temporal filter), `trans.temporalAlpha`, `trans.temporalClip` | on, 0.2, 1.5 |
| bench `trans.overlayDisplace` (the overlay follows the game's vertex displacement) | on |
| `-ultrart-nostaticlift` (static-batched displaced water stays a rest-pose slot of its static group) | lifted, displaced (since 2026-11-20) |
| bench `trans.portalGlass`, `trans.portalGlassStrength`, `trans.portalGlassHits` (water and glass behind portals, docs/portals.md section 13) | on, 1, 2 (since 2026-11-20) |
| `-ultrart-water-absorb X` (bench `trans.waterAbsorb`; Beer-Lambert absorption of what shows through water, per metre; 0 = off) | 0.35 (since 2026-11-20, "Water absorption" below) |
| `-ultrart-water-impulses X` (bench `water.impulses`, `[Effects] InteractiveWaterRipples`; rings from splashes, wakes and explosions; 0 = off) | 1 (since 2026-11-20, "Interactive ripples" below) |

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

### Late playtest fixes (2026-11-14, branch materials-late)

- **`psx/water`** is water too (`TransparentClassifier`): the older alpha-blended water shader (`_OpacScale`, no keywords
  or `_SurfaceType`), used by the museum (`CreditsMuseum2`: the courtyard fountain `Water fountain_water_1`..`4`, the
  pools, bird baths, `Calm Water vapor`) and by 0-2's crusher-arena blood tanks (`Blood`, classified as liquid blood by
  the blood regex). The overlay takes `_OpacScale` as `_Opacity`. The museum went from 0 to 13 batched + 6 dynamic water
  surfaces; the fountain now ripples and reflects the basin and V1 (pose `museum fountain`).
- **Frosted transparents** (`TransparentClassifier.Frosted`: Glass kind, `_SurfaceType` Glass, name `Ice*`; 0-E has 174
  `Ice` renderers): (1) the overlay's reflection weight is `TransparentSettings.frostedWeight` 0.25 instead of 1. The
  game draws the ice as a bright, fairly opaque frost; the full mirror-Fresnel reflection on top replaced it with the
  reflected (grey) room, and the ice read as clear glass ("too transparent"). (2) They are also scene geometry: a second
  dynamic proxy under `EmitterMask` 0x08 (camera, bounce, reflection and cache rays; no shadow rays) with
  `_CameraSkip` 1, so the Proxy any-hit lets camera rays through (the game draws the ice) and its submeshes are not
  `ClosestHitOnly`. Before, the ice was missing from every RT reflection: transparents are mask 0x10 and reflection rays
  use `ULTRART_MASK_SCENE` 0x6F, so 0-E's glass doors reflected the room without the ice. Clear glass and water stay
  out of those rays. `-ultrart-nofrosted` / bench variant `trans.frosted=0` (runtime) takes them out of the scene again.
  Static-batched ice is not handled (none in 0-E).

### Waves smaller than a pixel (2026-11-18, `t3/ghosting`)

User: 8-2's pool (pose 1) reflects as noise. Far from the camera a wave of the water normal is shorter than the
pixel's footprint on the surface, so each sample of a pixel reflects in a different direction, and one sample per
pixel cannot average that. `UltraRTWater.hlsl` `WaterNormalBand` fades each wave out as the footprint (distance x
pixel angle / cosine to the surface) nears a quarter of its length: gone at 4 footprints per wave, full at 8.
`TraceTransparent` passes the footprint. Bench `plugin._waterBand=0` = all waves everywhere.

Measured (`bench/suites/ghost-water.json`, 16-frame burst, sigma over the pool region): pathtrace 2.30 -> 1.84,
relight 2.16 -> 1.94. The far half of the pool is a clear reflection now; the near water still mottles (waves several
pixels long, reflecting a bright and a dark part of the room in turn: that is real, and only more samples or a
rougher water surface would calm it). Sheets `bench/results/review/ghost/pool_before_after.png`, `pool_variants.png`
(`trans.waves=0` = the clean mirror, `trans.cacheSpread` = no effect).

### 8-2's pool: still water, and limbo water without its fade (2026-11-18, `t3/playtest-materials`)

- **Still water.** The user (playtest of 2026-11-18, 8-2 pose 1): the pool should be still, a calm mirror-like
  surface, not a rough one. The near water's mottling is the ripples themselves (waves several pixels long reflecting
  bright and dark parts of the room in turn). A level's light file can scale the traced water's ripples:
  `"exposure": { "waterWaves": F }` (`AutoExposure`'s level block, read in `Plugin.Transparent.cs`; bench
  `ae.levelOverride=0` = without). 8-2 ships 0.25: pool sigma (16-frame burst, region of the pool) 2.12 -> 1.85;
  `trans.waves` 0.5 / 0.25 / 0 gave 1.95 / 1.58 / 1.66 in one launch (the flat mirror is no calmer than 0.25: what is
  left is the reflection's shading, one sample per pixel). Sheets `bench/results/review/ptm/pool_crop.png`
  (game, 1, 0.5 | 0.25, 0), runs `20261118-234335_playtest-materials` and `20261118-235641_playtest-materials`.
  A temporal filter on the reflection was not needed for the user's ask and is not done.
- **Limbo water fade.** The game fades `LIMBO_WATER_FADE` water around the player and enemies:
  alpha = saturate(n + (d - near) / (far - near)), n = length(rg) of its `_DitherTexture` (`bayer16_offset_xy`) at
  uv x 4, so within the band the surface shows the texture's squares (1-1's fountain, pose 2: the user's
  "checkerboard"). The user: "drop the fade out on the water too". While frames are traced the dither is a
  half-float texture with n = 1000 on the water's materials and our overlays (`TransparentOverlays.SetFlatFade`): the
  water is drawn at full strength at every distance; the game's texture is back within 30 frames of tracing stopping.
  The game instances these materials after the overlay is attached (`Limbo Water 2 (Instance)`, ours too), so the
  instances are looked up on the renderers every 30 frames (why the earlier attempt on the shared materials found
  none). Bench `plugin._flatWaterFade=0` = the game's fade. Sheet `bench/results/review/ptm/final_1-1_pose_2.png`.

### 1-4's bridge water (2026-11-19, check only)

The user: on the 1-4 bridge the water "looks not wildly different from stock". Run `20261119-193810_caustics-rt_alt`
(`caustics-rt.json`, "1-4 bridge", the `1-4-drift-a` route) and `20261119-194530_caustics-iso` ("1-4 bridge down");
sheets `bench/results/review/caustics-rt/sheet_1-4.png`, `crop_1-4_water.png`, `sheet_1-4_down.png`.

- **Which water**: the strips beside the bridge are `2 - Bridge/2 Nonstuff/Start Side/Plane` (`Limbo Water 2`:
  TRANSPARENCY, LIMBO_WATER_FADE, SCROLLING, texture `water`, colour alpha about 0.37) and the end side's `Cube`s
  (`Water`, SCROLLING). Both are classified as water and traced (mask 0x10, overlay attached); not an opaque look-alike.
  The log shows the transparent ray active with `Plane` the widest at 46-48 % of the view.
- **What the overlay adds there**: little. Over the left strip (pathtrace against `refl.enabled=0`, which also switches
  the pass off) the mean difference is 2 / 255, p99 14. The reflection is Fresnel-weighted (f0 0.12) at a steep angle and
  mirrors a dark night sky and dark hills, so it adds almost nothing and slightly darkens the relit floor seen through
  the water. Refraction moves only a shallow, thin strip. What reads as "stock" is the game's own faint water drawn over
  our relit floor.
- **Tried**: `trans.strength=3` and `trans.waterF0=0.3` only make the strips darker and more navy (more of the dark sky).
  A stronger Fresnel is not the lever in a night level.
- **What would make it read ray traced**, with trade-offs:
  1. (Built and removed on 2026-11-19: "Glints" below.) Bright things to reflect: the bridge lamps and the moon. A glint on rippled water needs the reflection to hit them;
     today the reflection hit is shaded from the radiance cache and the lamp fixtures are small. Specular highlights of
     the lights on the water (an NEE term in the overlay's reflection, GGX at the ripple normal) would put moving lamp
     and moon streaks on the water: the most "RT" cue at night, about one more shadow ray per water pixel.
  2. Lower the game's water opacity while traced (draw `Limbo Water*` at a fraction of its alpha) so the traced view
     through and off the water dominates: a clearer, more glassy look, but the game's water colour and its scrolling
     texture fade, and the floor under it (relit, brighter than the game's) shows more.
  3. Traced refraction and depth absorption (section 4 of caustics.md): a real bent view of the floor and darker,
     bluer deep water; the biggest change, the most work (RR guide buffers for the refracted surface).
  4. Rougher, wider reflection lobe: softer, more visible sheen under the sky gradient; costs sharpness and is noisier
     (one ray, no temporal filter on this pass).

### Glints (added and removed 2026-11-19)

Option 1 above was built on `t3/water-wet-specular-glints` (merged 2026-11-19): next-event estimated GGX highlights of
the lights on water, glass, liquid blood and wet opaque surfaces, `Plugin.Glints.cs`, `[Effects] Glints`,
`-ultrart-glints` / `-ultrart-noglints`, bench `glint.*`. The user's playtest the same evening: "far too obvious,
let's drop them entirely". It was removed on `t3/remove-glints-blood-arena-fix`, code, settings, flags, bench keys and
the `glints*` suites (the code is in commit `1c90777` if it is ever wanted again). An old `[Effects] Glints` line in a
config file is left alone as an orphaned entry: BepInEx keeps unknown keys and nothing reads it. A bench variant
`glint.*` now only logs `Burst: unknown variant`.

**Overlay gamma fix (kept).** The same branch fixed a separate bug that stays fixed: since the sky/GI merge
(2026-11-14) the overlay converted the traced water reflection to display units a second time under linear transport
(`^(1/2.2)` of a value `TraceReflection` had already converted), so a night sky at 0.05 showed as 0.26 and dark water
reflections read hazy. That conversion is gone (the `trans.legacyGamma` A/B toggle went with the glints). Water
reflections in relight and pathtrace are darker and clearer than before 2026-11-19 (8-2's pool, 1-4's strips).

### Displaced water and the temporal filter (2026-11-19, `t3/remove-glints-blood-arena-fix`)

The user's playtest: 6-2's blood "flickers" with pathtrace on, visible in the trailer's 6-2 entry shot. The blood is
6-2's two Grand Hall pools beside the entryway (`1 - Entryway/Grand Hall/Water/Cube` and `Cube (5)`, `Water 5`: colour
(1, 0, 0), SCROLLING, `VERTEX_DISPLACEMENT` mode 0 with amplitude 1, so up to 0.5 m along the flow direction), water
kind, not `TransparentKind.Blood`.

Measured on the trailer's take itself (`bench/suites/blood-cine.json`: `bench/cinematics/final-6-2-entry.json` live at
60 fps, one take per variant; metric: the second temporal difference, `|f(k) - (f(k-1) + f(k+1)) / 2|`, which cancels the
slow dolly, over the pools: per pixel, and of the region's mean for whole-patch "pops"). The trailer's frames
(`trailer-final-final-3`, 2560x1440) showed the pools popping 4-7 times as much as the game's own image, the walls not
at all. Two causes, both in the overlay's reflection (`trans.strength=0` brings the pools back to the game's level):

1. **The overlay sat at the rest pose.** The game displaces `Water 5`'s vertices in its vertex shader and writes depth;
   our traced copy is displaced too (docs/scene-proxies.md, "Vertex displacement"), but the overlay, drawn on the same
   renderer with its own vertex shader, was not. So it was depth-tested (`ZTest LEqual`) against the game's wavy
   surface and failed wherever a wave stood above the rest plane, and its distance no longer matched the traced hit.
   Whole triangles of the water mesh dropped in and out of the reflection as the camera moved. The worse the trace
   resolution, the worse it got: pathtrace's 1/3 scale more than relight's 1/2, RR at scale 1 least. **Fix**: the
   game's displacement function moved into `UltraRTDisplace.hlsl`, shared by `Displace.compute` and the overlay's
   vertex shader; `TransparentOverlays.CopyDisplacement` copies the water material's values onto the overlay under the
   game's property names (so a renderer's property block reaches them too), and the overlay displaces with Unity's
   `_Time`, as the game does. Bench `trans.overlayDisplace=0`: the old rest pose.
2. **No history.** The reflection is one ray per trace texel on the jittered trace grid, over rippled water, so each
   frame showed a different sample of the ripples: fine shimmer. **Fix**: `TransparentTemporal` (`Reflections.compute`,
   driven by `ReflectionFilter.ExecuteTransparent` right before the overlay is bound): the transparent hit is
   reprojected into last frame's trace (same-surface test on the distance, 5 % + 0.2 m), the history is clipped to the
   current 3x3 neighbourhood (mean +- 1.5 sigma, same-surface texels only) and blended with 0.2 of the new sample. No
   history across a frame without the pass, a portal crossing, or the reflection filter's resets. Bench
   `trans.temporal=0`: the raw signal.

Pool regions of the trailer take (`20261119-232748_blood-cine`, 1280x720, pathtrace with RR at its default 1/3
scale; left / right pool; game frames are `off`):

| Take | pop p95 | per-pixel 2nd difference |
|---|---|---|
| game (`off`) | 0.37 / 0.21 | 0.80 / 0.41 |
| before (rest-pose overlay, no filter) | 3.26 / 0.58 | 1.74 / 1.19 |
| displaced overlay only (`trans.temporal=0`) | 0.33 / 0.17 | 1.30 / 1.15 |
| both (shipped) | 0.34 / 0.17 | 1.00 / 0.86 |
| overlay reflection off (`trans.strength=0`) | 0.32 / 0.16 | 0.86 / 0.72 |
| relight before -> after | 1.66 / 0.67 -> 0.35 / 0.18 | 1.52 / 1.23 -> 1.10 / 0.94 |

A repeat in the final build (`20261119-233340_blood-cine_alt`): pathtrace before 1.75 / 0.43 pops and 1.48 / 1.19 per
pixel, after 0.33 / 0.19 and 1.00 / 0.85; relight before 3.12 / 0.24 and 1.67 / 1.15, after 0.34 / 0.19 and
1.09 / 0.92 (the size of the "before" pops varies with where the camera crosses the facets). Bursts at the user's
pose 1 (`20261119-233340_blood-arena_alt`, region over the blood floor, unmasked sigma, pathtrace): static 1.62 ->
1.11, slow sweep 2.46 -> 1.35.

The pools now carry an even reflection over the whole surface instead of a patchwork of triangles (they read a little
darker and glossier: the reflection is there everywhere). The glints, removed the same day, were not the cause: the
take still popped on the glint-free build before these fixes (`20261119-231443_blood-cine_alt`: left pool pop 3.35 in
pathtrace, 2.51 in relight), and static and slow-sweep bursts at the user's 6-2 pose 1 (`bench/suites/blood-arena.json`)
read the same with and without them (pathtrace masked sigma 0.53 / 0.53 static, 0.71 / 0.73 sweep). Cost: one 8x8 compute pass at trace resolution over the
transparent texels, only while the transparent pass runs.

Not covered: the overlay copies the water material's displacement values once, when the overlay material is made; a
level that animates them on the material (not through a property block) would drift. (Until 2026-11-20 this said that
static-batched water keeps the rest pose in the trace and is not displaced by the game either. The second half was
wrong, and the first is fixed: see the next section.)

### Static-batched displaced water (2026-11-20, `t3/water-portal-glass`)

The user, on the v5c trailer's 2-1 shot (`bench/cinematics/final-2-1-water.json`): "the same water clipping issues we
saw in" 6-2 (the pools above). 2-1's channel is `1 - New Opener/1 Nonstuff/Floor/Plane-clone` (and `Plane` in the
opener), `Water 3` with `VERTEX_DISPLACEMENT` mode 0, amplitude 1, and **static-batched**. The game's vertex shader
displaces in world space, so a batched surface moves like any other. The overlay has followed it since the 6-2 fix, but
the traced copy was a slot of a static group and kept the rest pose (`keeps the rest pose` in the log). The overlay's
reflection only applies where the traced hit's distance matches the fragment's, so whole triangles dropped in and out
again, as on 6-2 before.

**Fix** (`StaticGroups.Lifted.cs`): after the combined mesh is read back, every transparent slot (water, blood or
glass) whose material has `VERTEX_DISPLACEMENT` leaves the static group. Its triangles become a small readable mesh of
their own (the slot's vertex range of every stream, indices rebased), traced as a dynamic transparent proxy of its
source renderer, parented to the batch root. `AddDynamic` then gives it the displaced copy any dynamic proxy with the
keyword gets (docs/scene-proxies.md, "Vertex displacement"), with the same 40 m freeze. Checkpoint clones of such a
renderer get their own proxy from the same mesh. The overlay, the under-water test and the screen rectangle work as
for any static-batched transparent. Log: `Static displaced transparent: '<path>' Water 'Water 3' submesh 8 of
'Combined Mesh (root: scene) 4' lifted, 32 verts, displaced` and a `Static displaced transparents: N slots lifted`
summary. `-ultrart-nostaticlift`: the old rest-pose slots. Without a CPU copy of the combined mesh (readback failed)
the slot stays as before.

**Result** (`bench/suites/water-portal-glass.json`, scenario `2-1 water`: the trailer's pose and cinematic, 0.5 s
live takes, pathtrace with RR at scale 1; run `bench/results/20261120-131456_water-portal-glass_alt`, frame 9, `crop-2-1.jpg`): `pt-rest` (`plugin._displace=0`: the
traced copy at the rest pose, which is what static-batched water had before) shows the patchwork, straight-edged
regions with and without the reflection; `pt` (lifted and displaced) has one even reflection over the whole channel.
`pt-flat` puts the overlay at the rest pose too: its reflection is cut off in a straight line along the shore where
the game's displaced surface lies above the rest plane, which is the evidence that the game does displace batched
water. 2-1 lifts 2 slots (32 and 60 vertices); 8-3 has 2 static-batched water slots without the keyword (nothing to
lift). Cost: two more small DynamicGeometry BLAS in 2-1 while within 40 m; not measured separately.

### Water absorption (2026-11-20, `t3/water-absorption-caustics`)

The game blends its water colour over the background, `W a + B (1 - a)`, the same at every depth, and our relit floor
under it is brighter than the game's, so deep pools read as shallow and evenly lit (7-1's pool: the game's floor is
black, relight's a light grey slab). The overlay's `Absorb` pass (multiplicative, before `Reflect`) now also runs on
water (`_Kind` 1) as Beer-Lambert absorption over the path to the game's opaque depth:

- path = (scene eye depth - water eye depth) x distance / eye depth (the length of the view ray inside the water, so a
  grazing look crosses more), capped at 20 m; the water's eye depth is now taken from the **displaced** vertex (the
  overlay's `o.eye` came from the rest pose before; the refraction passes use it too), so 6-2/2-1's waves don't open
  gaps against the game's displaced depth;
- T = lerp(deep, 1, exp(-sigma x path)), deep = floor x hue, hue = the water's average colour (its texture at mip 8 x
  `_Color`, normalised to its largest channel, `waterAbsorbTint` of the way from white);
- dst x (1 - k (1 - T)), k = (1 - a) x fog visibility x the LIMBO_WATER_FADE coverage: only the share the game lets
  through is absorbed, the game's own surface colour stays.

Shallow edges (a few cm) stay clear, deep water darkens and takes the water's hue, and it never brightens, so darks
stay dark. A deep-water floor of 0.35 stands in for in-scattered light (the water itself lit). Left out: red "water"
(average colour r > 1.5 x max(g, b): 6-2's blood pools keep their look), blood (`_Kind` 2, its own absorption above),
glass, water seen from below (the game's underwater look), portal glass (it is a layer of the traced hit, docs/portals.md
section 13). At 8-2's pool the game's depth at portal pixels is the portal plane 0.2 m below, so the upside-down street
dims only slightly (about 0.5 m of path at an oblique view).

| Setting | Default |
|---|---|
| Strength: sigma per metre of path (`[Effects] WaterAbsorption`, Advanced page; `-ultrart-water-absorb X`; bench `trans.waterAbsorb=X`; 0 = off, as before) | 0.35 |
| bench `trans.waterAbsorbFloor` (transmittance of very deep water), `trans.waterAbsorbTint` (how much of the water's hue), `trans.waterAbsorbMaxPath` (m) | 0.35, 0.6, 20 |

Measured (runs of docs/caustics.md section 6; mean 0-255 over the water, pathtrace / relight, off -> on): 7-1 pool from
above 19.9 -> 16.4 / 63.4 -> 58.9 (the game: 15.6), 2-1 channel 29.1 -> 26.9 / 29.5 -> 29.0, 1-3 water hall 131.5 ->
120.5 / 131.6 -> 121.1. The 2-1 channel reads a little deeper and bluer, its shallow sides unchanged (`crop_2-1.jpg`).
Cost: one more fragment pass on visible water (it shares the `Absorb` pass); with the ripple caustics, under 0.06 ms
GPU total at the three poses measured (docs/caustics.md section 6, "Cost").

### Interactive ripples (2026-11-20, `t3/water-interactive-ripples`)

Splashes, wakes and explosions now ring the water: expanding circular waves on top of the four `kWaterWaves`, in the
reflection normal (`WaterNormalBand`, so the traced reflection and the overlay's Fresnel and refraction) and in the
ripple caustics under the water (docs/caustics.md section 6, "Interactive rings"). They are an analytic height field
only. The traced mesh, the vertex displacement, the overlay's displaced depth and the reflection's temporal filter are
untouched, so the same ring reaches every consumer and none of the 6-2 / 2-1 triangle dropouts can come back.

**The ring** (`UltraRTWater.hlsl`, `WaterImpulseRing`): h(r) = (a / k) E(r) sin(k (r - 1.5 R)), R = speed x age; the
envelope E = exp(-age / decay) x exp(-((r - R) / sigma)^2) x (1 + r / 2)^-1/2 x r^2 / (r^2 + eps^2), sigma = 0.6
wavelengths + 0.1 R (the packet widens as it travels), eps = 0.35 wavelengths. The crests run at 1.5 x the packet (deep
water: 2 x), so they rise at its back and fade at its front. The last factor makes h ~ r^2 at the centre: the slope is 0
there and the curvature finite (the radial `r / sqrt(r^2 + eps^2)` would leave a cone point). The slope and the Hessian
are exact (checked against finite differences to 1e-9 offline). The Hessian is h'' along the radius and h' / r across it.
Each ring is band-limited by the footprint like the waves (gone at 4 footprints per wavelength in the reflection, 6 in the
caustics), fades with the ripples' 60 m view fade, and is cut at 3 sigma from its crest (the loop skips it). It applies
only to horizontal surfaces (|n.y| > 0.9) whose height is within 2.5 m of the ring's surface, so a splash in an upper
pool never ripples a lower one. Rings ignore the level's `waterWaves`: 8-2's still pool still rings when something lands
in it.

**Sources** (`Plugin.WaterRipples.cs`):
- The game's own splashes: a Harmony postfix on `Water.TrySpawnSplash(WaterObject)` (the game's splash comes from a
  pool, so the spawn hooks never see it), with the game's own big/small rule. That covers V1, enemies, corpses and gibs,
  and any rigidbody projectile, on entry and on exit. The slope is 0.05 + 0.006 x impact speed (|v.y| + 0.25 x horizontal
  speed), capped at 0.24, times the body's size (V1 about 1.1). A fall faster than 25 m/s gets at least 0.2. The wavelength
  is 0.45-0.9 m with size, speed 1.8-2.6 m/s, decay 1.6 s.
- Wakes: a postfix on `Water.UpdateContinuousSplash` (each FixedUpdate for the player and enemies standing at the
  surface, not submerged) drops a ring every 0.2 s at walking speed and every 0.15 s at 20 m/s, above 1.5 m/s. Slope
  0.025 + 0.003 x speed, capped at 0.09 (enemies x 0.7), wavelength 0.5 m, speed 1.5 m/s, decay 0.9 s.
- Explosions: `EffectLights.Spawned` (beside the shockwaves of `Plugin.Vfx.cs`). An `Explosion` within 4.5 m of a water
  surface, over its rectangle give or take its size, makes a ring of slope 0.12 + 0.015 x size (0.12-0.3, less the
  higher above the water), wavelength 0.7-1.4 m, speed 2.8 m/s, decay 2.2 s.
- The surface is the ripple-caustic water list (`SceneProxies.WaterSurfaceAt`: the top of the renderer bounds). Nothing
  is made further than 80 m from the camera, or over water that is not traced (6-2's red pools).

**Buffer**: 16 rings. When the buffer is full, the ring with the least left (slope now, divided by 1 + distance / 30 m) is
replaced, unless the new one has even less. Each frame the C# side sends the age, not the birth time (`Time.time`: game time,
so pauses and hitstop freeze the rings, and no clock wraps in the shader): `_UltraRTWaterImpulses[16]` = (x, z, age, slope
x strength), `_UltraRTWaterImpulses2[16]` = (surface y, speed, wavelength, decay), `_UltraRTWaterImpulse.x` = count.
These are immediate global arrays, read by `PathTrace.raytrace` and the overlays. With no ring the count is 0 and both
shader paths are the code from before (a branch on it). Log: `Water ripples: strength S, game splash hooks on`, and
`Water ripples: <splash (V1) | big splash (V1) | splash (enemy) | explosion '...'> at (x, y, z) slope A, wavelength L m, N ring(s)`.

| Setting | Default |
|---|---|
| Strength (`[Effects] InteractiveWaterRipples`, Advanced page; `-ultrart-water-impulses X`; bench `water.impulses=X`; 0 = off) | 1 |
| bench `water.splashes`, `water.wakes`, `water.explosions`, `water.enemies`, `water.maxDistance` | on, on, on, on, 80 m |
| bench `water.testRipple=1`: one ring frozen at `water.testAge` (0.65 s), slope `water.testAmplitude` (0.25), wavelength 0.7 m, where the view (tilted down 0-21 degrees) meets water nearest 6 m away; `water.testRings=N`: N of them on a spiral (cost of a full buffer) | off |

**Validation** (main slot, which the user allowed for visual runs; 2560x1440, 2026-11-20; suite `bench/suites/water-rings.json`, runs
`bench/results/20261120-172622_tmp-water-rings` and `20261120-173119_tmp-water-rings`, run under the suite's working name; sheets in each run's `sheets/`):
- 5-1 shaft pool with flat water (`trans.waves=0`, so only the ring moves the surface): crisp concentric rings at ages
  0.3, 0.65 and 1.3 s, expanding, no spike or hole at the centre, the same in relight and pathtrace (`crop_5-1_ring.jpg`).
  Two rings show: the surface's reflected ring, and, offset by the parallax of the floor 1 m below, the bright caustic
  ring on the pool floor. With the waves on, the ring is a soft disturbance in the wave caustics, not a separate effect.
- 16 overlapping rings (`water.testRings=16`): an interference pattern of caustic lines, no blow-up, speckle or boiling
  (`5-1_16.jpg`).
- 2-1 channel and 1-3 water hall: the ring shows in the reflection (subtle in 2-1's dark channel). No NaN, no exception
  in the logs. `tools/lights/punch.py` over 1-3 and 2-1: highlights, p95, saturation and contrast within 0.02 of the
  ringless frames (the views have no game-dark pixels for the dark test).
- Live hooks (run `20261120-173555_tmp-water-live`, scenario `5-1 shaft explosion` of `water-rings.json`: the bench's
  harmless `explosion` 6 m ahead, 0.9 s before the shot): the explosion makes its ring (slope 0.21, wavelength 1.08 m),
  and its debris chunks falling into the pool come through the game's `TrySpawnSplash` as a dozen small splashes
  (slope 0.07-0.09), which fill the buffer to 16. The pool floor's caustics and the reflection then show soft, expanding
  rings that overlap. The effect is clear but gentle, not a burst of sparkle. A revolver coin thrown into the water made
  no splash (its collider does not trigger the game's water), so V1's own splash and wakes were not exercised in a bench
  run: they use the same two hooks.

**Cost** (perf hold, main, 2560x1440, `bench/suites/water-rings-perf.json`, run `20261120-173435_tmp-water-rings-perf`,
alternating 1.5 s segments; a full buffer of 16 overlapping rings around the test point against none). GPU total ms:
5-1 shaft pool, pathtrace (RR) 4.81 -> 4.88 (dispatch 1.94 -> 2.01); 2-1 channel, pathtrace 5.90 -> 6.00 (dispatch
2.83 -> 2.93), relight 5.54 -> 5.64. (The 5-1 relight segments recorded no frames in that run.) So up to +0.1 ms with
16 rings on water that fills the view: the cost is in the dispatch (reflection normal and caustics), and the overlay's
share is too small to see. With no ring it is zero: the count is 0, and the shaders take the old path.

### Limits

- **Camera inside a large water volume** (7-1's pool, 5-x swimming): dispatch +0.4 ms although nothing transparent is traced. The water box's instance sits in the same RTAS as the scene, and masking it out (0xEF then, 0x6F now: `ULTRART_MASK_SCENE`) apparently does not stop it from slowing traversal of every ray that starts inside it. A separate RTAS for mask 0x10 would remove that; not done.
- **One ray per trace texel**, filtered over time since 2026-11-19 (below), not in space. Reflection hits are shaded from the radiance cache, so they are smooth. Where a cache cell is missing, the hit falls back to `DirectOne` and shows speckle; ripples hide most of it.
- **Game geometry vs. RT mesh**: SCROLLING UVs are not reproduced on water/glass (LIMBO_WATER_FADE is, see above), so the alpha estimate ignores scrolling; opaque scrolling surfaces (5-2's sea) do scroll since 2026-11-15 (docs/scene-proxies.md, "Scrolling textures"). VERTEX_DISPLACEMENT waves are, since 2026-11-15, and on static-batched water and glass since 2026-11-20 ("Static-batched displaced water"; docs/scene-proxies.md, "Vertex displacement"). The refraction delta assumes the game's blend is exactly `W a + B (1 - a)`.
- **The game's own cubemap stays** on REFLECTION water (`Water 10A-D`, `1C`, `6 Still/Reflection`, `Limbo Water ... V2Arena`), on top of ours. For opaque materials it is removed (section 7), but here it is baked into the game's transparent draw.
- **Renderers whose last material slot is not water/glass** get no overlay (logged as `without overlay`). Unity repeats only the last submesh for an extra material.
- **Total internal reflection, glass thickness**: not done (frosted glass is, see "Late playtest fixes"). Glass is a single mirror-Fresnel surface with no refraction. What refracts where today and the options: [caustics.md](caustics.md). The game's own caustic pattern (5-1) is traced since 2026-11-19 (caustics.md, section 5); caustics of our ripples on what lies under water since 2026-11-20 (caustics.md, section 6, an analytic factor, not traced light).
- **Relight tone mapping** is replicated in the overlay (`Tonemap`, from `Denoise.compute`). If the exposure/tone-mapping work changes that curve, update both.
- Water/glass are now invisible to shadow, bounce, reflection and cache rays (mask 0xEF at the time, now `ULTRART_MASK_SCENE` 0x6F). Before, they were absent from the RTAS, except the two queue-2999 glass materials, which were traced as opaque cutouts and cast RT shadows. Those no longer do.

## 13. The player in reflections, and fake mirrors

ULTRAKILL's player prefab carries a full third-person V1: `Player/GroundCheck/v1_combined` (`PlayerAnimations`, an `Animator`), with the skinned body `v1_mdl` and one third-person copy of every weapon on the right forearm (`StandardRevolver_NewArm/Revolver_Body`, `ShotgunNewAnims/Shotgun_New`, ...; `PlayerAnimations.UpdateWeapon` activates the current one). All of it is on layer 30, which only portal views draw: that is how V1 shows in 8-x's portal mirrors. The first-person viewmodel (arms and guns under `Main Camera`, layer 13) is a separate set of renderers and is not in the RTAS; it is lit by its own pass ([viewmodel.md](viewmodel.md)).

Files: `src/UltraRT/SceneProxies.Player.cs` (finding the model, mask, animator, shadow box), `SceneProxies.cs` (registration), `Plugin.cs` (settings and globals), `UltraRTCommon.hlsl`, `UltraRTPortals.hlsl` (`PlayerRayMask`, `TraceScene`), `UltraRTLights.hlsl` (`VisibleMasked`), `PathTrace.raytrace`, `CacheUpdate.raytrace`.

### Instance mask 0x80

Every renderer under `PlayerAnimations` (except layer 13) gets a normal dynamic proxy (skinned or rigid; the skinned ones share the bones and follow the root bone, docs/scene-proxies.md "Skinned meshes") with instance mask `SceneProxies.PlayerMask` = **0x80**, the last free bit (0x01 geometry, 0x02 sky walls, 0x04 fixtures, 0x08 emitters, 0x10 water/glass, 0x20 portals, 0x40 layer-30 props). `ULTRART_MASK_SCENE` dropped it (0xEF → **0x6F**), so no ray sees V1 unless it adds the bit:

| Ray | Sees V1 | Why |
|---|---|---|
| Camera (primary) rays, all modes and debug views | **never** before a portal hop | the camera sits inside V1's head and the third-person gun arm reaches in front of it (`v1_mdl`'s bounds contain the camera) |
| Camera rays after a hop through a portal with `canSeePortalLayer` | yes (bit 8) | what the game's portal views draw: 8-x mirrors |
| Camera rays in a third-person cinematic shot | yes (bit 16, set only by the bench's `ShowPlayer`: docs/cinematics.md) | the camera is outside the model there |
| Reflection rays (incl. the water/glass pass) | yes (bit 1) | the point of the feature |
| Bounce rays (PathTrace) | yes (bit 2) | V1 occludes and reflects bounce light near the player |
| Shadow rays (PathTrace: direct light, sky, emissive, reflection hits) | yes (bit 4) | V1 casts a shadow, as in Quake II RTX |
| Radiance cache rays and shadow rays (CacheUpdate) | no | the cache is world-space and temporally accumulated: a moving player would leave stale shadows in it |
| Volumetrics shadow rays | no | they march from the camera, i.e. from inside the model, and don't cull back faces |
| Water/glass pass (mask 0x10) | no | |

The bits are Plugin's `_playerRays` (default 15 = all four), uploaded every frame as the global `_UltraRTPlayerRays`: `-ultrart-player-rays N`, bench `plugin._playerRays=N` (0 = V1 nowhere, the A/B baseline). `-ultrart-noplayer` leaves the model out of the RTAS altogether (read at scene build; layer-30 renderers then fall back to the portal-layer mask as before). In `TraceScene`, a ray that crosses a portal keeps V1 only if that portal can see layer 30, like layer-30 props; a camera ray gains it there.

**Self-occlusion.** Rays never start at the camera except camera rays, which exclude V1. Shadow, bounce and reflection rays start at surfaces and cull back faces, so a ray that starts inside the model passes out through it. The one real trap is the player's own lights: muzzle flashes and other effect lights sit at the first-person gun, which is inside the third-person arm and gun. A shadow ray toward such a light would be blocked by V1's arm and cast its shadow across the room with every shot. So such lights are exempt while they lie inside `_UltraRTPlayerBoxMin/Max` (since 2026-11-14 late only spawned lights and emitters, see "The player's own lights" below): a 2 m box around the camera, grown to hold the active third-person weapon's bounds plus 0.3 m (`SceneProxies.PlayerShadowBox`; the body is left out, its bounds are about 2.4 x 3.9 x 3.1 m with the wings and would exempt every nearby light). Measured with the bench's revolver effect in 1-1's terminal room (`1-1 terminal shots`): the image with V1 differs from `_playerRays=0` only by V1's body shadow on the far wall and noise, no gun shadow.

**The player's own lights** (2026-11-14 pm, user report: 7-3's flashlight cast V1's shadow down the path ahead). The box only covers lights whose shadow-ray end point lands near the camera; the 7-3 flashlight (`Player/Main Camera/Flashlight`, a spot light, range 120, angle 35) and soft-shadow samples at a light's edge did not always. Every packed light now carries a flag in `_Lights[i*4+1].w` (`Plugin.FlagPlayerLights`, `src/UltraRT/Plugin.PlayerLights.cs`): 1 when the light is in the player's or the camera's hierarchy (the flashlight, a held item such as 4-3's torch, weapon lights, muzzle flashes) or within 0.75 m of the eye (Torch.Update moves a held torch's light to the player). `LightVisible` (UltraRTLights.hlsl) clears `g_ShadowPlayerMask` for flagged lights, so V1 never shadows them, wherever they are. The hierarchy test is cached per light and refreshed with the 4 Hz active-light scan; cost is one `IsChildOf` per light per refresh. Screenshot: `bench/results/20261114-184932_lights-pm-check/7-3_flashlight_2.0s_relight.png` (no V1 shadow; before: `20261114-182905_lights-pm-diag/7-3_flashlight_2.0s_relight.png`, matching the user's).

**Level lights near the head, and the shadow split** (2026-11-14 late, user reports; `bench/suites/v1-late.json`, runs `bench/results/20261114-215426_v1-late-diag_alt` = master, `20261114-220040_v1-late_alt` = fix):
- *0-2 first room, V1's shadow missing at one spot* ("0-2 v1 shadow a"): the room's main light, `FirstRoom/Room/Point Light` (range 25, intensity 8), hangs at (0, 2.5, -45), 0.6 m above and 0.7 m behind the eye at that spot. It was inside the 2 m camera box, so the shader treated it as a muzzle flash and V1 never shadowed it; two metres on ("shadow b") it was outside and V1's shadow (spread wings: the user's "four shadows") showed. Fix: the box is no longer tested in the shader for point and spot lights. `Plugin.FlagPlayerLights` decides once per light per frame: flag 1 for the player/camera hierarchy and the eye (as before) and, new, for *spawned* lights (not present at scene build, `SceneProxies.IsBuildLight`; muzzle flashes, explosions, effect and virtual lights) inside the box. The level's own lights never get it for being near the player. Emitter samples (`EmissiveVisible`) keep the box test (`InPlayerBox`). At pose a V1's shadow now lies under the player (the light is almost overhead), as the game geometry implies.
- *7-3 flashlight still cast V1's shadow* ("7-3 flashlight shadow"): the flashlight was flagged, and `LightVisible` honoured it, but the player shadow split's V1-only ray (docs/denoiser.md, "Moving shadows") tested only the box, and `Player/Main Camera/Flashlight` sits about 10 m behind and 1.5 m above the camera (a range-120 spot aimed past the player), far outside it. That ray now uses the same rule as `SampleVisible`: the flag for lights, the box for emitters. With `plugin._playerSplit=0` master already showed no shadow, which is how it was found.
- `1-1 terminal shots` (revolver muzzle flashes) still shows no gun-arm shadow.

**V1 lit like the scene in reflections** (2026-11-14 late, user report "P-2 v1 reflection": in the dark red hall V1 reflected greyish white with bright yellow wings). V1's materials are all ULTRAKILL/Master without VERTEX_LIGHTING: body `V1` (`_USEALBEDOASEMISSIVE_ON` but no EMISSIVE keyword), wings `V1Wing` (EMISSIVE), third-person guns `Pistol New Unlit`, `Shotgun New Unlit`, ... The proxy rule for that ("unlit = fullbright, like the game", `_Unlit` 1) made V1 fullbright, which is how the game draws it in its own portal mirrors but stands out wherever the scene is dark. Player proxies now get lit copies of their materials (`SceneProxies.PlayerProxyOrDisabled`, `_Unlit` 0, also on runtime material swaps; other users of the same materials keep theirs). The body takes the room's colour; the wings keep the game's own yellow emission (the stamina glow), now on top of a lit, dark-red albedo. Switch: `SceneProxies.PlayerModelLit` (read when the model's proxies are made). No extra rays: cost unchanged.

**V1's own blue** (2026-11-17, `t3/trailer-fixes`; the trailer's "V1 reads grey" in 1-1's terminal room). The
separating capture (`bench/suites/diag-v1-grey.json`: the trailer's third-person shot in pathtrace and the debug
views; the debug ray generation does not trace the player, so only pathtrace and `dn-lighting` show it) says it is
physics: the body's albedo is blue, the room's light is orange, and a blue surface returns almost none of it, so the
body is near black with grey highlights from its gloss. By the user's decision 7 (docs/archive/plan-look-and-perf.md) V1
keeps a small unlit share so the blue reads: `SceneProxies.PlayerModelUnlit` = 0.2 of the game's fullbright
(`-ultrart-v1-unlit F`; 0 = lit by the room only, read when the model's proxies are made). Before / after at the
same frame: `bench/results/review/v1-unlit-share.png` (`20261117-201615_diag-v1-grey_alt` against
`20261117-201813_trailer-fixes_alt`). The guns V1 holds get the same share.

**V1's glow** (2026-11-21, `t3/reflections-and-p2-emissives`; user, playtest of 2026-11-20: "the railgun doesn't glow in
reflections (1-4 pose 2)" and "should V1's wings glow more in the terminal reflection?"). `SceneProxies.Player.cs`,
`UpdatePlayerGlow`, every frame after the dynamic sync:
- *The held gun.* The game's third-person guns have no EMISSIVE keyword (`M_Railcannon Unlit`, `Pistol New Unlit`, ...;
  the game only draws them fullbright in 8-x's portal mirrors), so the railcannon's pips were matte in every RT
  reflection while the viewmodel's glowed. `Railcannon.SetMaterialIntensity` drives only the viewmodel: body and pips get
  `_EmissiveColor` = the variation colour and `_EmissiveIntensity` = the charge (body charge / 5 while recharging, each
  pip its own part, 1 when full) in their property blocks. Each third-person renderer is now linked to the viewmodel
  renderer of the held weapon (`GunControl.currentWeapon`) with the same name (`Railcannon/BigPip`, `Pips_1`-`4`,
  `Railcannon`), and where that renderer's material is EMISSIVE (with the same main texture when the emission is shaped
  by it: `_EmissiveMask`, albedo as emissive) the proxy gets its own material copy that follows the viewmodel's live
  emission (block first, then material) x `PlayerWeaponGlow` (1). Links are rebuilt on a weapon switch; materials that
  go out of use are retired. Log: `V1 weapon glow: '<weapon>' N linked renderer(s): ...`. Plugin `_v1WeaponGlow`,
  `-ultrart-v1-weapon-glow F`, bench `plugin._v1WeaponGlow=F` (0 = as before).
- *Custom gun colours.* `GunColorGetter` swaps the third-person guns to `... CustomColor` materials (CUSTOM_COLORS) like
  the viewmodel's (the user plays with alternate colours). `Proxy.shader` now has the game's CUSTOM_COLORS albedo
  (`_IDTex.a` blended towards `_CustomColor1`-`3` by its rgb masks, at most 80 %, as `ViewmodelGBuffer.shader`), set
  per renderer on the same linked copies (colours from the third-person renderer's block, else the viewmodel's, else
  the material). Other proxies leave `_CustomColors` at 0.
- *The wings.* V1's own emissive materials on the body (`V1Wing`, the game's yellow at emission 1) glow x
  `PlayerEmissionGain` = **2.5** in RT (Plugin `_v1Glow`, `-ultrart-v1-glow F`, bench `plugin._v1Glow=F`; 1 = the game's).
  A terminal's CRT glass reflects about a quarter head-on, so at the game's emission the wings read as faint smudges.

Measured (main install, the user's save with the railcannon, SDR, trace scale 0.5; `20261121-020810_tmp-rp2-final`,
before = `plugin._v1WeaponGlow=0+plugin._v1Glow=1` in the same launch): 1-4 pose 2's mirror shows the pips as cyan rings
(before: dark grey); at this angle the barrel side carries no emissive mask and stays as it was. 1-1's terminal from 6 m
(`(6.8, -0.5, 261)`, closer the shop UI covers the screen) shows the wings clearly yellow against the dark glass,
the screen itself not washed out. No extra rays: the cost is a few property reads per frame for the held gun's 1-6
renderers (perf lock, `20261121-021902_tmp-rp2-perf`, 1-4 lamps: GPU 4.20 -> 4.20 ms, plugin CPU 0.76 -> 0.81 ms
average, within the run-to-run spread of that column). Sheets: `bench/results/review/reflections-and-p2/1-4_pose_2_railcannon_mirror.jpg`,
`1-1_terminal_V1_wings.jpg`. Not verified: HDR (the display was not in HDR mode during the run), other weapons' glows
(the revolver's charge, the nailgun's heat) beyond the link log.

**V1's camera eye** (2026-11-21, `t3/playtest-visual-fixes`; user, playtest of 2026-11-21: "should the camera on v1's
head glow?"). The lens on V1's head is painted into the body texture (`v1colour_tex`, 256x256) of the fullbright `V1`
material, which RT lights like the room with a 20 % unlit share (`PlayerModelUnlit`), so in a dark room's reflection it
read as a dull ochre dot beside the glowing wings. `SceneProxies.Player.cs` (`BuildEyeMask`, `ApplyEyeGlow`): once per
body material, the main texture is read back (Blit + ReadPixels) and its saturated yellow texels (min(r, g) - b over
0.25-0.4, max(r, g) over 0.35-0.5) become an emission texture (the texel's own colour, black elsewhere, point filtered)
on the lit copy, x `PlayerEyeGlow` = **2.5**, the wings' gain. 346 texels (0.53 % of the texture, mean colour (0.97,
0.94, 0.00)): the lens and the two yellow slits on the chest (both yellow lights in the game's own fullbright V1); a
texture with nothing to key, or more than 4 %, glows nowhere (logged). Plugin `_v1EyeGlow`, `-ultrart-v1-eye-glow F`,
bench `plugin._v1EyeGlow=F` (0 = before). Log: `V1 eye glow: 'V1' texture 'v1colour_tex' 256x256: 346 lens texels ...`.

Measured (alt install, SDR, trace scale 0.5, run `20261121-154810_tmp-pvf-eye`, before/after in one launch): in 1-1's
terminal glass from 6 m (`1-1-terminal-glancing.json` at (6.8, -0.5, 261)) the lens is a small yellow dot above the
crosshair in `pathtrace` and `relight`; with `plugin._v1EyeGlow=0` it is not visible. Brightest pixel of the head patch
(38, 26, 16) -> (51, 44, 17) `pathtrace`, (64, 58, 21) `relight`: a glint in the CRT glass (it reflects about a quarter),
not a hot spot. The game's own 8-x portal mirrors draw V1 fullbright (8-3's secret mirror: the lens at (235, 231, 4) in
both, unchanged). Sheet: `bench/results/review/playtest-visual-fixes/1-1_terminal_V1_eye.png` (the game, before, after `pathtrace`,
after `relight`). Cost: one readback per body material at the scene build; nothing per frame beyond a property check.

**Water tint and P-2's puddle** (2026-11-17). The trailer's yellow-green patch on P-2's street is the game's `Puddle`
object, which is traced as water (`Water 6 Still`: colour (0.22, 0, 0), alpha 0.5, texture `Fixed Water Texture`).
In pathtrace a path that passes through water is tinted by `lerp(1, albedo, 0.48)` whatever the game's alpha is
(`UltraRTPathTraceFull.hlsl`). `trans.alphaTint=1` makes that follow the game's blend alpha (texture alpha x colour
alpha x `_Opacity` / `_OpacScale`, full tint from 0.5 up; the proxy gets `_TransOpacity`, and each water material's
values are logged as `Transparent proxy: water ...`). At five water poses (`bench/suites/water-tint.json`,
`20261117-202126_water-tint_alt`: P-2 puddle, 5-2 sea, 1-3 start, 8-2 pool, 7-1 pool) it changes little (mean
difference 0.7-7 of 255), and **the yellow-green patch did not reproduce** at the plan's pose in today's build with
it off or on: the puddle is a red, mottled reflection there. So the switch is off, and the item stays open until
the patch is seen again (the trailer's take was a moving first-person run under flickering lamps). What that pose
does show: the street is x2.7 brighter than the game there, floor and all.

Second try, at the pose the patch was first seen from (the end of the trailer's hall crane,
`bench/cinematics/trailer-p-2-hall-reveal.json`; `bench/suites/p-2-hall-floor.json`,
`bench/results/20261117-222625_p-2-hall-floor`, sheet `bench/results/review/water-tint-p-2-hall.png`): the puddle at
the bottom right is red in every mode, not yellow-green. Mean colour of the patch: the game (163, 13, 0), pathtrace
(58, 8, 4), with `trans.alphaTint=1` (73, 7, 4), rain wetness off (68, 7, 3), blood wetness off (25, 6, 4). The alpha
tint moves it a little towards the game's red; the hue is not the fault in today's build. What is off at this pose is
the level: the game's floor is two to three times brighter (P-2's lighting also changes over time, docs/vfx.md's
trap). The switch stays off.

### Animation

`PlayerAnimations.LateUpdate` drives the model from `NewMovement` (velocity, sliding, dodging, in air, jump/land, shoot/punch/coin triggers) and turns the head, chest and right arm to the camera every frame. The Animator ships with `cullingMode = CullUpdateTransforms`; its renderers are on layer 30, which the main camera never draws, and our proxies never render, so outside portal views Unity could stop updating the bones. `SceneProxies.PlayerAlwaysAnimate` sets `AlwaysAnimate` while the RT scene exists and restores the original on dispose (`-ultrart-player-noanimate` skips it). The 600-frame log line `Player model: ...` reports the proxies in the RTAS, the animator's culling mode, state and normalized time (it advances: 22.4 → 26.0 → 32.6 over successive logs in one level), and where the root bone and bounds are relative to the camera. The RT pose is the skinned pose (`DynamicGeometry` re-reads it every build) and the instance follows the root bone.

Not verified: running, sliding and dodging poses. The benchmark takes the player over (kinematic rigidbody, zero velocity), so every bench shot shows the idle pose with wings spread and the head and gun arm following the camera (which they do: the mirror image looks where the camera looks). A recording replays the camera path the same way. Checking locomotion needs real play in front of a mirror.

### The held item (2026-11-21, `t3/level-8-3-visual-polish`)

User, playtest of 2026-11-21: in 8-3's mirrors V1 held nothing while the player held a skull. A held item
(`FistControl.heldObject`, an `ItemIdentifier`) is moved under the first-person `Punch` holder on layer 13
(`FistControl.ResetHeldItemPosition`), so it leaves the RTAS like the rest of the viewmodel (`HideHeldItems`), and the
third-person model has nothing to show it with: `PlayerAnimations` has no item attachment. **The game itself never shows
a held item on V1**, in its portal mirrors either (decompiled `FistControl` and `PlayerAnimations`, 2026-11-21;
and the game's own mirror in `off`: V1 with empty hands, `bench/results/20261121-222502_tmp-83p-diag6`, `8-3 mirror held`).

`SceneProxies.HeldItem.cs` adds a copy: at the scene build it finds V1's left hand (`PlayerAnimations.leftArm`, then
its first descendant named like a hand: `hand.L`, scale 2.1). While an item is held, every traced `MeshRenderer` of the
item gets a copy under the hand (forceRenderingOff, never drawn by the game), registered as a dynamic proxy with the
player mask 0x80, so it shows wherever V1 does (reflections, portal views, shadows) and never to camera rays. The copies
follow the item: each part's active state (a skull has a mesh per season, one active), its shared materials and its
property block (a skull's glow), and the item's rotation; the copy is centred on the hand. When the item is put down,
thrown or dropped the copy is turned off; a copy is built once per item. Size: the item's own scale is for the first-
person view and the ground (a skull is 0.91 m across on the ground), far too big for V1's hand (the user: "far
too big"), so the copy is scaled uniformly to `PlayerHeldSize` across its largest side, 0.35 m by default
(`-ultrart-v1-held-size X`, bench `plugin._v1HeldSize=X`, 0 = the ground size). `-ultrart-v1-noheld` / bench
`plugin._v1HeldItem=0`: no copy, as before.

Measured at 8-3's secret mirror (`8-3 mirror held` in `bench/suites/playtest-8-3.json`, `pathtrace+plugin._rtMirrors=1`,
the bench's `holditem` effect holding a blue skull; run `20261121-222502_tmp-83p-diag6`): the copy logs `size 0.537 local
= 0.91 m on the ground, 0.35 m in V1's hand` and shows as a blue skull a little larger than V1's fist in the left hand,
in `_raw` and the final image; 0.25 m reads as small; with `_v1HeldItem=0` the hand is empty. The first version used the
ground size, 0.91 m (the user's "far too big"). Cost: the item's active parts as player-mask dynamic proxies; not
measured.

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

**Liquid blood** (added 2026-11-14): transparent blood liquids (0-2's blood river `Mulch`, other alpha-blended `Blood*` Master materials) are a third kind, `TransparentKind.Blood`. It is traced like water/glass but reads as thick and opaque: the reflection ray treats it as flat (Glass class, no ripples), the overlay skips refraction (`_Kind` 2) and uses F0 0.06 with the reflection weight capped at 0.7 (`TransparentSettings.bloodF0`, `bloodWeight`). 6-2's blood is drawn with a water material (`Water 5`, red, with `VERTEX_DISPLACEMENT`) and stays water: its flicker in pathtrace (2026-11-19) is in section 12, "Displaced water and the temporal filter". Opaque `BloodPool` and particle/decal blood are unaffected.
