# Path tracing mode: plan

> **Archived 2026-11-19.** The pre-implementation plan of the `pathtrace` mode (sections 1-5) and its experiments (section 6, still cited by [path-tracing.md](../path-tracing.md), [dlss.md](../dlss.md) and [light-sampling.md](../light-sampling.md)). The current design: [path-tracing.md](../path-tracing.md).

Working plan for the `pathtrace` mode, DLSS Ray Reconstruction as its denoiser, and tracing what the game still
rasterises. The mode's own documentation is [path-tracing.md](../path-tracing.md); results land there and in
[dlss.md](../dlss.md). This file records what was there, what was tried first and why the design is what it is.

## 1. What exists (master f4be515)

*A snapshot from before the work below; section 6 records what changed (relight's default sampler is RIS now, effects, blood stains and water/glass are traced in the paths).*

| Mode | What it traces | Light model | Output |
|---|---|---|---|
| `relight` (default) | primary hit; ReSTIR DI (1 shadow ray); 1 sky sample; 1 cosine bounce whose hit reads the radiance cache; 1 reflection ray on glossy pixels, shaded from the cache or one light sample | physical falloff, linear transport, sky, sun disk, auto exposure, colour match | denoiser + reflection filter + TAAU, or DLSS RR (Ctrl+F8) |
| `pt-dn` | the same trace | the game's (gamma, ULTRAKILL falloff, flat ambient) | the same |
| `hybrid` | the same trace, `_Mode` 3 | the game's | RT ratios applied to the game's raster |
| `pt` / `pt-direct` / `pt-indirect` | the same ray generation, not denoised: full / direct + ambient only / bounce only | the game's | 1 spp, or accumulated with `-ultrart-accumulate` |

So the "older path-tracing modes" are not path tracers: `pt-dn` is relight's one-bounce trace with the game's light
model, and `pt`, `pt-direct`, `pt-indirect` are its undenoised parts, kept as debug views. Nothing traces more than
one bounce; everything past the first bounce hit is the radiance cache.

Everything hangs off one ray generation shader (`PathTrace.raytrace`, `RayGen`) with exactly 8 UAVs (Unity's limit) and
a 64-byte payload, and one G-buffer: `_Radiance` (demodulated diffuse lighting, a = V1 shadow factor + mover flag),
`_Surface` (packed albedo, emission, material), `_NormalDepth`, `_InstanceId` (instance, motion), `_HybridLight` (lit
weight), `_Reflection` (reflected radiance, hit distance), `_Output` (water/glass reflection for the overlay) and the
ReSTIR reservoirs. The denoiser, the reflection filter, TAAU, RR's `Prepare`, auto exposure, volumetrics, the viewmodel
pass and lit particles all read that G-buffer.

Still drawn by the game or missing from the RT scene (inventory from the docs and `SceneProxies.Traceable`):

| What | Today | In reflections / bounces |
|---|---|---|
| Fire, explosions, muzzle flashes, beams, additive glows (particles, trails, lines, sprites) | raster on top; a light source through `EffectLights` (real or virtual point lights) | **missing** (0-4's terminal doesn't reflect the fire) |
| Lit particles (blood drops, dust, debris, smoke) | raster, lit by the froxel volume | missing |
| Blood stains (`ULTRAKILL/Bloodstain*` meshes, the blood/oil stain buffer) | raster, multiplicative, after the RT composite | missing (untraced on purpose: traced as geometry they were white quads) |
| Sprites (`SpriteRenderer`, shaders with "Sprite") | raster | missing |
| Water, glass, blood surfaces | raster by the game + a traced mirror reflection overlay (mask 0x10) | invisible to every other ray (frosted ice excepted) |
| Lava | traced (emitter mask 0x08), the game's additive layer on top | traced |
| Grates, fences and other queue >= 3000 cut-outs | raster | missing: they cast no shadow |
| Viewmodel | own pass (not in the RTAS) | V1's third-person model stands in (mask 0x80) |
| HUD, UI | raster | n/a (stays) |

## 2. Experiments first

They need no code, only suites (`bench/suites/pt-exp-rr.json`, `pt-exp-rr2.json`, `pt-exp-scale.json`):

1. **RR with decorrelated light sampling** on relight's signal (0-1 metal, 8-2 portal side 1, 6-2 start; motion on the
   0-2 walk). Relight already runs ReSTIR without spatial reuse, so the variants remove the temporal reuse instead:
   `lights=ris` (no reuse), `ls.historyLimit=2` (short history), `lights=ref` (no light-selection noise at all),
   `cache=off` (is the radiance cache's slow change what RR passes through as "boil"?), both together. Each RR variant
   is repeated in mirrored order, because the first RR burst after a non-RR mode measured noisier.
2. **Preset D / E** after the motion-vector fix, in balanced order; **settle length**: the same RR burst three times
   with `BurstSettle` 480 instead of 120 (does the "first burst" effect disappear when RR gets time to converge?).
3. **RR at 1/3 render scale** (`plugin._scale=0.3333`, 853x480 to 1440p) on 0-1, two 7-3 poses and 6-2: screenshots
   for texture detail on the point-sampled art, bursts for noise, against RR at 1/2 and our TAAU at both scales.

What they decide:
- If RR gets clearly quieter without temporal reuse: the PT mode gives RR plain RIS (more candidates, one sample per
  path), no reservoirs. If not: keep ReSTIR for the primary hit in both denoiser paths.
- If 1/3 scale keeps the texel grid: PT + RR renders at 1/3. If not: the best scale that does (0.4 or 0.5), paid for
  with fewer paths or bounces.

Results: section 6.

## 3. Design

### 3.1 The mode

A new view mode **`pathtrace`** next to `relight`, with relight's light model and look (physical falloff, linear
transport, sky and sun, auto exposure, game and colour match, tone curve, volumetrics, viewmodel pass, lit particles).
Relight itself is untouched: same ray generation, same defaults.

A second ray generation shader `RayGenPT` in the same `PathTrace.raytrace` (file `UltraRTPathTraceFull.hlsl`): the
same RayTracingShader object, so every binding `Trace()` makes (lights, alias tables, reservoirs, emissive triangles,
sky, cache, portals, transparents) is reused, the 8 UAVs stay the same 8, and the hit shaders are shared. `Trace()`
only picks the raygen name. It writes the **same G-buffer**, so both denoiser paths, the composite and every later
pass work unchanged.

Per pixel:
1. **Primary hit**, exactly as relight (portals, mirrors kept, per-texel detail normal, motion, lava, V1 hidden).
2. **Direct light at the primary hit**: `DirectLighting` (fill lights + RIS or ReSTIR over lights and emissive
   triangles, one shadow ray, V1 shadow split, mover flag) + one sky sample. With RR the sampling mode is whatever
   experiment 1 picks.
3. **N diffuse paths** (1-2): cosine direction from the primary hit, then up to B bounce rays (2-3). At every path
   vertex:
   - emission (0 for emitters the light sampling already covers, full after a specular bounce);
   - **next-event estimation**: fill lights + RIS (lights and emissive triangles, lava included) with one shadow ray,
     and one MIS-weighted sky sample with one visibility ray;
   - the next direction: the surface's specular lobe with probability by its weight (mirror or GGX, same
     `SpecularTerms` as the composite), else cosine; fullbright surfaces end the path;
   - Russian roulette on the throughput from the second vertex.
   The **last vertex** takes its lighting from the radiance cache (`total`: direct + all further bounces) when the
   cell is valid, else from NEE alone. That is the only use of the cache in the path: it replaces the tail after B
   real bounces instead of everything after the first hit.
4. **N specular paths** on glossy pixels: the reflection ray, then the same path loop from its hit (so reflections show
   bounce-lit, shadowed scenes instead of cache cells). Written to `_Reflection` as today (display units, hit distance
   of the first path).
5. **Transparents**: the camera ray's water/glass overlay as today, but its reflection is a specular path too.
   Path rays see water and glass (stochastic Fresnel reflection or tinted transmission) instead of passing through
   untouched, so pools and windows appear in mirrors and reflections.
6. Portals teleport every path segment (`TraceScene`), V1's model is seen by path rays and shadow rays as today.

Settings (`PathTraceSettings`, bench prefix `ptx.`): paths, bounces, cache tail, roulette, light sampling for RR,
trace scale with RR and with our denoiser, firefly clamp.

### 3.2 Denoising

- **DLSS RR** (when `nvngx_dlssd.dll` is there and the PT denoiser setting allows it): render at the PT scale, RR
  denoises and upscales to the screen. Changes to the RR path, for relight too where they are fixes:
  - area colour match applied to RR's input like the composite applies it (`UltraRTPixelBalance`);
  - jitter phases follow the scale (8 x ratio^2, NVIDIA's rule), quality mode Ultra Performance at 1/3;
  - the "first burst" effect understood and, if it is ours, fixed;
  - guides: nothing transparent or particle-like is in RR's input (the game draws those after RR, at output
    resolution), so `ColorBeforeTransparency` has no source in this pipeline; documented, not bound.
- **Our denoiser** as the fallback: the same G-buffer through the SVGF denoiser, reflection filter and TAAU at trace
  scale 0.5 (TAAU's jitter pattern is exact only there). Lower quality or lower frame rate than RR is accepted.

### 3.3 Tracing what is still raster

One new mechanism, the **effects pass**: after a specular ray found its hit, a second ray over the same segment sees
only "effect" geometry, whose any-hit shader accumulates into the payload and ignores the hit:
`radiance = radiance x transmittance + added`. Effect instances share mask 0x10 with water/glass (all 8 mask bits are
taken); they are non-opaque while water/glass is opaque, so `RAY_FLAG_CULL_OPAQUE` separates them, and the any-hit
ignores rays that didn't ask for effects. Camera rays never use it (the game draws effects itself, at full resolution,
which keeps the pixel look); shadow rays don't either.

- **Additive glows** (fire, explosions, flashes, beams): the particle systems, trails, lines and sprites that
  `EffectLights` already recognises as glowing get a billboard mesh in the RTAS (`ParticleSystemRenderer.BakeMesh`
  and friends, rebuilt per frame, capped), emission = texture x vertex colour.
- **Blood stains / decals**: multiplicative quads; transmittance = their texture.
- **Sprites and alpha-blended particles**: coverage x colour, lit from the froxel volume.

Order: glows first (the user's 0-4 case), then decals, then the rest, each measured.

## 4. Stages

| # | Stage | Check |
|---|---|---|
| 0 | Experiments (section 2) | bursts, strips |
| 1 | `pathtrace` mode: RayGenPT (multi-bounce, NEE, cache tail, specular paths), settings, mode plumbing, our denoiser | strips vs off / relight; energy vs relight; bursts; perf |
| 2 | RR for the PT mode: scale, sampling per experiment, colour match, jitter phases | bursts, strips, perf at 1440p |
| 3 | Transparents in path; viewmodel/particles/volumetrics checks in PT | static-water poses |
| 4 | Effects pass: additive glows in reflections | 0-4 terminal fire, effects suite, cost |
| 5 | Decals, sprites, lit particles in reflections | 0-2 blood, cost |
| 6 | Quality/perf sweep (paths x bounces x scale), settings menu, docs | perf suite at 1440p |

## 5. Expected costs

From docs/dlss.md (c): one path segment with its NEE costs 1.3-1.8 ns per pixel; relight is about two segments.
`pathtrace` with 2 paths x 2 bounces + cache tail is about 5 segments, 2.5x relight per pixel; at 1/3 scale that is
0.44x the pixels, so about 1.1x relight's dispatch (3-4 ms at 1440p), plus RR 2 ms and fixed passes 1 ms: 6-7 ms of
GPU, inside 8.3 ms. At 1/2 scale with our denoiser the same settings are about 2.5x relight's dispatch (7-9 ms):
the fallback needs 1 path x 2 bounces to stay near 120 fps. Section 6 replaces these with measurements.

## 6. Results

### 6.0 Summary

| Stage | Outcome |
|---|---|
| 0 Experiments | RR wants RIS without reuse (35-45% quieter); 1/3 scale keeps noise but loses texels; default preset is F; no "first burst" effect (6.1) |
| 1 `pathtrace` mode | done: `RayGenPT`, energy within 0-5% of relight on the same sampler ([path-tracing.md](../path-tracing.md)) |
| 2 RR for the mode | done: RIS, remodulation, exact 3x3 jitter grid at 1/3 scale, colour and game match. 10-45% quieter than relight at rest, 4-11% noisier moving, same GPU cost, 132-195 fps at 1440p |
| 3 Transparents in the path | done: Fresnel reflection or tinted pass-through at water / glass / blood; viewmodel, particles, volumetrics run unchanged |
| 4 Effects pass | done for additive glows (particles, sprites, trails, lines): visible to every specular ray in relight, hybrid and pathtrace |
| 5 Decals, lit particles, alpha-blended sprites in reflections | blood stains done (2026-11-15: a world-space grid of the game's stains, the game's composite at reflection hits; [path-tracing.md](../path-tracing.md), "Blood stains in reflections"); particles and sprites not done (6.2) |
| 6 Sweep, settings, docs | done: defaults 1 path x 2 bounces x 1 light sample; the settings menu's "Path tracing" section |

The cost estimate of section 5 was wrong in one way that mattered: a path segment past the primary hit costs about
twice what the primary's does (incoherent rays, RIS candidates at every vertex), so 2 paths x 2 bounces at 1/3 scale is
9-12 ms, not 3-4. One path with one light sample is what fits, and under RR it measures as quiet as two.

### 6.2 Not done, and why

- **Blood stains in reflections**: done since (2026-11-15). Not as geometry: a hash grid of the stains near the camera,
  built each frame from the game's buffers with the stamp shader's box, rotation and atlas cell (ported for wet blood
  already), looked up at reflection hits ([path-tracing.md](../path-tracing.md), "Blood stains in reflections").
- **Alpha-blended particles and sprites** (blood drops, dust, smoke, splashes) in reflections: they need lighting at the
  hit (the froxel volume is camera-aligned and could be sampled) and order-dependent blending; the any-hit
  accumulation is order-independent. Additive-only for now.
- **Grates and fences** (queue 3000 cut-outs) still cast no shadows: they are not in the RTAS in any mode.

### 6.1 Experiments (2026-11-14, 2560x1440, RTX 5070 Ti; `bench/results/20261114-223629_pt-exp-*`)

Masked burst sigma (48 frames, luma 0-255; lower is better). "RR" = `relight+plugin._rrEnabled=1`, trace scale 0.5
unless noted. Repeats are the same mode again at the end of the list.

**Decorrelated light sampling.**

| | relight (ours) | ours, `lights=ris` | RR | RR `lights=ris` | RR `ls.historyLimit=2` | RR `lights=ref` | RR `cache=off` | RR `ris` + `cache=off` |
|---|---|---|---|---|---|---|---|---|
| 8-2 portal side 1 | 0.35 | 0.31 | 0.67 / 0.76 | **0.39 / 0.38** | 0.53 | 0.34 | 0.60 | 0.40 |
| 6-2 start | 1.10 | 0.50 | 0.98 / 0.88 | **0.59 / 0.58** | 0.60 | 0.54 | 0.86 | 0.69 |
| 0-2 walk, motion sigma (interior) | 2.13 (1.66) | | 2.52 (2.22) | 2.43 (2.30) | 2.40 (2.27) | | | |

- Without temporal reservoir reuse RR is 35-45% quieter at a held pose, and reaches the level of "no light-selection
  noise at all" (`lights=ref`). The hypothesis in docs/dlss.md holds: ReSTIR's frame-to-frame correlation is what RR
  could not remove. A short history (M cap 16) gets most of it. The radiance cache is not the cause (`cache=off`
  changes little).
- Our own denoiser is quieter with plain RIS too at these two poses (6-2: 0.50 against 1.10). Both have few lights
  that matter per pixel; ReSTIR earns its keep where many lights compete (8-3) and on fresh pixels. Not changed for
  relight here; worth a look by whoever owns the denoiser.
- In motion RR stays 15-20% noisier than ours with any sampling: there the difference is the denoiser, not the samples.
- **Decision:** `pathtrace` gives RR plain RIS at the primary hit (more candidates, one or two samples), no reservoirs.

**Presets and the "first burst".** NGX logs `Using Default Overriden Preset Preset_F` for every quality mode: the
default is F, not E.

| | default (F) | E (`_rrPreset=5`) | D (`_rrPreset=4`) |
|---|---|---|---|
| 0-1 metal | 1.62 / 1.64 | 1.62 / 1.62 | 2.12 / 2.08 |
| 8-2 portal side 1 | 0.70 / 0.79 | 1.44 / 1.37 | 1.23 / 1.38 |

- D and E are never better and up to 2x worse: the default stays.
- With `BurstSettle` 480 instead of 120, three RR bursts on 0-1 measure 1.51, 1.51, 1.57, and the first and last RR
  bursts of the 120-frame lists differ by under 10% in either direction (0.67 / 0.76, 0.98 / 0.88, 1.62 / 1.64). There
  is no order effect; RR converges a little further between 120 and 480 frames (1.62 to 1.51). What does happen once per
  process: NGX initialises on the first RR frame, which takes seconds, and the scenario that triggers it loses its
  captures. Suites that use RR start with a warm-up scenario.

**Render scale 1/3 (853x480 to 1440p).**

| | relight (TAAU 0.5) | RR 0.5 | RR 1/3 | TAAU 1/3 |
|---|---|---|---|---|
| 0-1 metal | 0.59 | 1.68 | 1.40 | 0.50 |
| 7-3 tree a | 0.26 | 0.43 | 0.41 | 0.25 |
| 7-3 flashlight | 0.35 | 0.41 | 0.37 | 0.33 |
| GPU total, 0-1 / 7-3 flashlight (not exclusive) | 7.05 / 7.05 | 8.34 / 8.70 | 5.32 / 4.58 | |

- Noise does not get worse at 1/3 (fewer, larger pixels per output pixel average more).
- Texture detail does: RR at 1/3 smears the texel grid of 0-1's floor and walls and 6-2's tiles
  (`scratchpad` strips; in the results folder compare `0-1_metal_2.0s_relight.png` with
  `..._plugin__rrEnabled_1_plugin__scale_0_3333.png`). At 0.5 it is slightly softer than our TAAU in the distance.
- **Decision:** render at 1/3, and take the textures out of RR's hands: RR gets the image divided by each surface's
  "white light" colour, and the exact texels are put back at screen resolution (docs/dlss.md, "Remodulation").
- The area colour match was indeed missing under RR (7-3: a grey-white floor where relight shows the room's tint);
  fixed with the game match (docs/dlss.md).
