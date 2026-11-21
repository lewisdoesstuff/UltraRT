# Path tracing mode (`pathtrace`)

`pathtrace` is a view mode next to `relight`: the same light model and look (physical falloff, linear transport, sky and
sun, auto exposure, game and colour match, tone curve, volumetrics, viewmodel pass, lit particles; [relight.md](relight.md)),
but the indirect light and the reflections come from real multi-bounce paths instead of one bounce into the radiance
cache. DLSS Ray Reconstruction denoises and upscales it when it is available ([dlss.md](dlss.md)); UltraRT's own
denoiser is the fallback. Relight is unchanged and stays the default.

How the design was arrived at (the experiments, the alternatives): [pt-plan.md](archive/pt-plan.md).

| File | Role |
|---|---|
| `unity/.../UltraRTPathTraceFull.hlsl` | The paths: `PtPath`, `PtIndirect`, `PtReflection`, `PtMoreDirect` |
| `unity/.../PathTrace.raytrace` | `RayGenBody(full)`: the shared primary-hit code; entry points `RayGen` (every other mode) and `RayGenPT` |
| `unity/.../UltraRTEffects.hlsl`, `EffectProxy.shader` | The effects pass: glowing effects along specular rays |
| `src/UltraRT/Plugin.PathTrace.cs` | `PathTraceSettings`, flags, the per-frame binds |
| `src/UltraRT/EffectGeometry.cs`, `Plugin.Effects.cs` | Effect billboards in the RTAS |
| `src/UltraRT/DlssRR.cs`, `Plugin.Dlss.cs`, `RayReconstruction.compute` | RR for the mode: remodulation, the exact jitter grid, the warm-up |
| `src/UltraRT/Plugin.Remod.cs` | Remodulation for UltraRT's own denoiser (the fallback, at trace scale 1/3) |
| `src/UltraRT/Plugin.Stains.cs`, `unity/.../StainGrid.compute`, `UltraRTStains.hlsl` | Blood stains at reflection hits |
| `bench/suites/pt-*.json` | `pt-quick` (look), `pt-noise`, `pt-perf`, `pt-fx`, `pt-exp-*` (the experiments) |

## Use

- **Page Up / Page Down** to `pathtrace`, or the settings menu (View mode), or `[View] DefaultMode = pathtrace` (the default since 2026-11-17 where DLSS RR is present: README, "View modes").
- Settings menu, section "Path tracing": denoiser, render resolution with RR, paths, bounces, light samples, cache tail,
  sky sample at every bounce. Config section `[PathTrace]` ([settings.md](settings.md)).
- Related: the cache tail is [radiance-cache.md](radiance-cache.md); the offline renderer (paths, bounces, accumulation, 16-bit
  frames) is [cinematics.md](cinematics.md); texture LOD for the detail maps is [materials.md](materials.md); frame costs at
  1440p are [performance.md](performance.md); portals in paths are [portals.md](portals.md); the first-person gun, lit
  particles and screen effects in this mode are [viewmodel.md](viewmodel.md), [particles.md](particles.md), [vfx.md](vfx.md).
- Flags: `-ultrart-pt-paths N`, `-ultrart-pt-bounces N`, `-ultrart-pt-nocache`, `-ultrart-pt-footprint F`, `-ultrart-pt-roulette F`,
  `-ultrart-pt-clamp F`, `-ultrart-pt-scale F`, `-ultrart-pt-denoiser auto|rr|ultrart`.
- Bench variants: `pathtrace+ptx.<field>=value` for any field of `PathTraceSettings`, e.g.
  `pathtrace+ptx.paths=2+ptx.bounces=3`, `pathtrace+ptx.denoiser=UltraRT`.
- DLSS RR needs the NVIDIA runtime next to the plugin (`scripts\fetch-dlss.ps1`, [dlss.md](dlss.md)). Without it, or on
  another vendor's GPU, the mode runs on UltraRT's denoiser by itself (trace scale 1/3 with remodulation, below).
  Nothing waits on the first use any more ("Warm-up" below): NGX starts in the background when the game starts, the
  mode's ray tracing pipeline is compiled on the first RT frame, and RR's feature is created in the background when the
  mode is entered.

## Warm-up (2026-11-15)

The first switch to `pathtrace` in a session used to freeze the game, and a bench scenario could come back with no
frames (the lead's merge-regress on master 9fd12e3, '0-1 metal'). Three separate costs were in it:

| What | First use cost (measured) | Now |
|---|---|---|
| NGX initialisation (loads `nvngx_dlssd.dll`, the models) | 2.4-3.1 s | on a worker thread from frame 3 of the game (`[RR] NGX ready ... init 2927 ms on the warm-up thread`) |
| NGX's kernels, on a process's first RR evaluate | 130-230 ms | a throw-away 320x180 feature evaluated once on the worker at start-up, then released on the render thread |
| RR's feature for the current sizes | 31-82 ms create + its first evaluate | created and evaluated once on the worker when the mode is entered; meanwhile (17-18 frames, about 150 ms) the frames use UltraRT's denoiser |
| **RayGenPT's ray tracing pipeline** | **16-33 s** after a new shader bundle (the driver's shader cache misses), about 0.2 s on a cache hit | compiled by a 1x1 dispatch on the session's first trace in any mode, usually at the main menu (`ptx.prewarm`, `-ultrart-pt-noprewarm`) |

- Since 2026-11-20 PathTrace has two more ray generations, `RayGenImg` / `RayGenPTImg` (frames with lights imaged
  through portals; [performance.md](performance.md), "2026-11-20 batch"). They are warmed the same way: a 1x1 dispatch
  on the session's first trace (the mode in use; RayGenPTImg also when pathtrace is wanted) and on the first frame with
  portal sides, both under `ptx.prewarm`.
- The 16-33 s was the pipeline, not NGX: it measured the same with UltraRT's denoiser (`View mode relight -> pathtrace
  ... frame ms 18276.3`, `bench/results/20261115-022925_pt2-i_alt`) and only after a shader change (runs that reused
  the same bundle took 223 ms). Unity compiles each raygen's pipeline on its first dispatch, so relight never paid for
  RayGenPT. After an update the wait now happens once at the menu.
- Frame times when RR's feature is created mid-game (relight -> pathtrace, `bench/results/20261115-022749_pt2-h_alt`):
  38.7, 14.4, 4.7, 8.4, 5.0, 6.8 ms. The first frame is any mode switch's (relight -> pathtrace with the feature ready:
  15-22 ms; off -> relight: 27 ms).
- The bench waits for RR before it measures a mode that uses it (`WaitForRR`, at most 20 s, logged as `Benchmark: waited
  N ms`), and frames that ran UltraRT's denoiser while RR warmed up are not counted.
- A worker-thread release of a feature it created froze the render thread for 16 s inside NGX; releases only happen on
  the render thread now ([dlss.md](dlss.md), "Warm-up").

## What a pixel traces

The ray generation is relight's with two parts swapped (`RayGenBody(full)`; `full` is a compile-time constant, so
`RayGen` compiles to what it was).

1. **Primary hit**: as relight (the camera ray comes from `UltraRTCameraRay`, which stays finite with the far plane at
   infinity: docs/scene-proxies.md, "Cameras with a far plane at infinity"). Portals teleport the ray, portal mirrors keep the game's view, the per-texel detail
   normal applies, lava ends the ray, V1's model is invisible to camera rays.
2. **Direct light at the primary hit**: `DirectLighting` (fill lights, then lights and emissive triangles through RIS,
   one shadow ray; V1's shadow split and the moving-occluder flag as in relight) and one sky sample.
   - With RR: plain RIS (`lightsRR` 1, `candidatesRR` 16), no reservoirs, `directSamplesRR` samples averaged
     (`PtMoreDirect`). RR wants samples that are independent from frame to frame; see [dlss.md](dlss.md).
   - With UltraRT's denoiser: whatever F6 selects (RIS by default since 2026-11-15, or the reference; ReSTIR was optional until its removal on 2026-11-19), one sample.
3. **Diffuse paths** (`PtIndirect`, `paths` per pixel): a cosine direction from the primary hit, then up to `bounces`
   segments (`PtPath`). At each vertex:
   - **emission**: zero for emitters the light sampling covers (as relight's bounce), in full after a specular bounce;
   - **next-event estimation**: `DirectOne` (fill lights + RIS over lights and emissive triangles, lava included, one
     shadow ray), and with `skyAtVertices` one MIS-weighted sky sample;
   - **the next direction**: the surface's specular lobe with probability by its weight (the composite's own
     `SpecularTerms`: metal, mirrors, REFLECTION materials), else cosine. Fullbright surfaces show their albedo and
     end the path;
   - **Russian roulette** below a throughput of `roulette`.
   The **last vertex** reads the radiance cache (`cacheTail`): direct light plus every further bounce, from the cell
   it lands in. A missing cell falls back to next-event estimation. This is the only place the cache enters a path;
   relight reads it at the first bounce hit already.
4. **Specular paths** (`PtReflection`) where relight traces its reflection ray: the same path loop from the reflection
   hit, so a reflection shows a scene with real shadows and bounce light at any distance. A mirror takes one path
   (it has one direction), a glossy surface `paths`.
5. **Effects** in front of every specular segment (below).
6. **Water and glass**: the overlay's mirror reflection as in relight (it has no denoiser, so it stays cache shaded).

Everything is written to relight's G-buffer (`_Radiance`, `_Surface`, `_NormalDepth`, `_InstanceId`, `_LitWeight` (`_HybridLight` before 2026-11-19),
`_Reflection`, `_Output`), so the reflection filter, the denoiser (both, until hybrid's went on 2026-11-19), auto exposure, volumetrics, the viewmodel pass and
lit particles run unchanged.

Energy against relight (radiance statistics, mean lighting, linear): 6-2 start 0.661 relight, 0.635 `pathtrace` with RR,
0.662 with UltraRT's denoiser. Two real bounces plus the cache tail carry what relight's one bounce into the cache
carries, as they should: the cache is energy-correct ([relight.md](relight.md), "Cache energy"). What changes is where
the light comes from: contact shadows and colour bleeding within the first two bounces are traced, not read from
0.5 m cells.

## Settings (`PathTraceSettings`)

| Field | Default | |
|---|---|---|
| `paths` | 1 | diffuse (and glossy) paths per pixel, 1-4 |
| `bounces` | 2 | segments per path, 1-4 |
| `cacheTail` | on | the last vertex reads the radiance cache |
| `footprint` | 1 | a vertex whose ray cone is this many cache cells wide ends the path in the cache (0 = off; "Footprint") |
| `roulette` | 0.2 | throughput below which paths are thinned (0 = off) |
| `maxRadiance` | 12 | firefly clamp on one diffuse path (lighting units; 0 = off) |
| `specularLobes` | on | specular lobes at path vertices |
| `skyAtVertices` | off | a direct sky sample at every path vertex (the primary hit always takes one) |
| `denoiser` | Auto | Auto (RR when available), UltraRT, RayReconstruction |
| `scaleRR` | 1/3 | trace scale with RR (853x480 at 1440p) |
| `scale` | 1/3 | trace scale with UltraRT's denoiser (was 0.5; settings menu "Render resolution with UltraRT's denoiser") |
| `pathsOwn` | 1 | paths per pixel with UltraRT's denoiser (0 = `paths`) |
| `remodulate`, `remodExactGrid` | on, on | UltraRT's denoiser remodulates as RR does, with the exact N x N jitter grid at 1/3 (or 1/5) |
| `remodSharpness`, `remodMaxWeight` | 1, 6 | the remodulated lighting's upsampler: sample weight exp(-x d^2), history cap |
| `prewarm` | on | compile RayGenPT's pipeline on the session's first trace (Warm-up), only when the session starts in pathtrace or a benchmark runs (relight players get no menu freeze; their first switch to pathtrace compiles it instead) |
| `lightsRR`, `candidatesRR`, `directSamplesRR` | 1 (RIS), 16, 1 | light sampling at the primary hit under RR (`lightsRR` 0 = reference, 1 = RIS; 2 was ReSTIR until 2026-11-19); with the World-Space Light Grid active (`ls.grid=1`), RIS uses `risCandidatesGrid` = 8 candidates |
| `singleLobe` | off | stochastic single-lobe BSDF selection at glossy primary hits (1 secondary path instead of separate diffuse + specular paths, weighted by `1/p` for unbiased energy; on in the `Performance` and `Competitive` quality presets; bench `ptx.singleLobe=1`) |

## Effects in reflections

The game draws fire, explosions, muzzle flashes, beams and other additive glows after the RT image, so no ray saw
them: a screen next to a fire reflected an empty room (0-4's terminal). Now:

- `EffectLights` already classifies every particle system, sprite, trail and line by material (additive blend, queue
  >= 2450, not smoke / dust / mist...). Each one found glowing is handed to `EffectGeometry` (`GlowSink`), trails and
  lines included; rain streaks and heat shimmer are left out by name.
- Each frame the nearest active ones (within 80 m, at most 48 instances and 1,200 billboards, at most 400 particles
  per system) get their mesh rebuilt with the renderer's own `BakeMesh` for the main camera (sprites: the sprite's
  mesh), on a hidden proxy in the RTAS: instance mask 0x10, hit shader `UltraRT/EffectProxy`.
- The hit shader is an any-hit that adds `texture x vertex colour x tint` (times alpha for `SrcAlpha One`) to the
  payload and ignores the hit. One ray per specular segment (`EffectsAlong`: mask 0x10, `RAY_FLAG_CULL_OPAQUE`, so the
  opaque water/glass proxies under the same mask are skipped) collects every billboard in front of the hit.
- Used by relight's (and, until 2026-11-19, hybrid's) reflection ray, the water/glass overlay's reflection and every specular segment of a
  path. Camera rays don't (the game draws the effect itself, at full resolution); cosine bounces don't (effects light
  the scene through their point lights already; counting the billboards too would light it twice).
- Tracked effects that are off are polled in turn (one in 12 per frame): most of a level's glowing effects are pooled
  fires and unlit torches. CPU 0.02-0.03 ms per frame with 200-500 tracked.

- Knobs, neutral by default: `fxg.glow` (1 = as the game draws them), `fxg.roughWeight` (fade on rough lobes, 1 = none)
  and `fxg.coverFade` (0 = off: less of the effects in the reflections of pixels the effect itself covers, since the
  camera-facing billboards mirror onto the pixels right behind them; costs one more any-hit ray per pixel while effects
  are active).
- Bench trap: `Effects` are spawned once per mode at a screenshot and pooled fires keep burning, so in one scenario
  each later mode shows one more flame stacked on the last (the flame looked "blown out" by the reflections; it was
  three flames). Compare effect looks with one mode per scenario, or only between the first two modes.

`fxg.enabled=0` (bench), `-ultrart-noeffectgeometry`, settings menu "Fire and explosions in reflections".

Limits: billboards face the main camera, so a reflection that looks along a billboard's plane sees it thin; the
any-hit can run twice for one billboard (DXR allows it), which would double its glow in that pixel; effects through a
portal are collected on the last segment only.

## Transparents in the path

In relight every ray but the overlay's passes through water, glass and liquid blood untouched (mask 0x10 is outside
`ULTRART_MASK_SCENE`). Path rays (`ptx.transparents`, on) include the mask: the proxy hit shader reports the kind in
the payload flags (bits 4-5, `_Transparent` on the proxy material), and `PtPath` then either reflects off the surface
like a mirror, with the Fresnel probability from the kind's F0, or passes straight through with its throughput tinted
by the surface colour (`transparentTint` 0.8; water and glass take 0.6 of it, blood all). Neither counts as a bounce;
after three such surfaces a path ignores the rest. So pools, windows and the blood river show up in reflections and
tint the light that bounces through them. The camera ray is unchanged: the game draws the surface, and the overlay
adds its reflection. Frosted ice stays the opaque frosted surface it is in the scene.

## Footprint (2026-11-17, branch `t3/perf-paths`)

A path vertex whose ray cone is at least `footprint` radiance-cache cells wide there ends the path in the cache
(`PtPath`, `_PtParams3.z`; it needs the cache tail and a valid cell, otherwise the path goes on as before). The cache
holds lighting per 0.5 m cell near the camera, so a second segment from a vertex the cone already covers as widely
adds little the cache does not have. After a cosine bounce the cone opens by the diffuse spread (0.3 rad): at the
default of 1 cell a diffuse path traces its second segment only when the first hit is nearer than about 1.7 m, which
is where contact shadows and colour bleeding between close surfaces are. Glossy cones open with roughness, so rough
lobes end at their first hit and mirrors keep both segments. `footprint = 0` is the behaviour before this change.

GPU ms of the main dispatch / frame ms, 2560x1440, RR at 1/3, exclusive lock (`perf-paths`,
`bench/results/20261117-144613_perf-paths`). An earlier launch, `20261117-143829_perf-paths`, ran slower as a whole
(0-1 metal 4.37 -> 2.60 dispatch, 7.82 -> 6.09 ms frame) with the same saving; compare within one launch:

| | footprint 0 (before) | **1 (default)** | 0.5 | 2 | bounces=1 (the bound) |
|---|---|---|---|---|---|
| 0-1 metal | 3.47 / 6.91 (145 fps) | **2.23 / 5.68 (176 fps)** | 2.09 / 5.78 | 2.54 / - | 1.59 / 5.63 |
| 0-3 ghosts | 4.10 / 7.88 | **2.30 / 7.43** | 2.01 / 7.87 | 2.69 / 7.50 | 1.45 / 7.22 |
| 0-2 run, combat | 3.72 / 8.49 | **3.17 / 8.65** | 2.74 / 8.70 | 3.66 / 9.21 | 2.04 / 8.45 |

- Calm, GPU-bound scenes gain what the dispatch gains: 1.2 ms at 0-1 (145 -> 176 fps). 0-3 and the combat run are
  bound by the main thread (docs/performance.md, "Where the frame goes"), so 0.5-1.8 ms less GPU work shows as
  0-0.4 ms of frame time there; it becomes frame time once the CPU side is shorter (`t3/perf-dispatch`).
- 0.5 cells gains another 0.1-0.4 ms of dispatch and nothing in frame time; kept at 1. The 0-1 frame time at 2 cells
  holds a 300 ms stall of that run and is left out.
- **Look** (`paths-look`: start room, 0-1 metal, 1-4 lamps, 6-2 hall; `bench/results/20261117-144036_paths-look_alt`,
  sheet `bench/results/perf-paths/look.png`): on the scoreboard the default is within run-to-run spread of full paths
  (start room dark delta +8.1 -> +8.4, against +12.0 with `bounces=1`; 6-2 hall x1.26 -> x1.30), and the difference
  images show the animated smoke and gun, no edges or corners.
- **Noise** (`paths-noise`, masked burst sigma / block sigma, `bench/results/20261117-144241_paths-noise_alt`): 6-2
  start 0.48 / 0.36 -> 0.31 / 0.19 (the cache is smoother than a traced segment), 0-1 metal 0.75 / 0.46 -> 0.78 / 0.47.

Setting: menu "End wide paths in the cache (cells; 0 = off)", `[PathTrace] Footprint`, `-ultrart-pt-footprint F`,
bench `ptx.footprint`. The cost table below (2026-11-14) was measured without it.

## Cost (2026-11-14, RTX 5070 Ti, 2560x1440, exclusive perf lock)

GPU ms per frame (native timer: everything UltraRT records) and frames per second of the whole game. "1p/2b/1d" =
paths / bounces / light samples at the primary hit. RR at trace scale 1/3 (854x480), UltraRT's denoiser at 0.5 unless
noted. `bench/results/20261114-234716_pt-perf2` (the defaults) and `20261114-231051_pt-perf` (the knobs).

| | relight | **pathtrace (default: RR, 1p/2b/1d)** | RR, 2p/2b/2d | RR, 3 bounces | UltraRT denoiser 0.5 | UltraRT denoiser 1/3 |
|---|---|---|---|---|---|---|
| 0-1 metal | 6.01 / 146 fps | **6.68 / 132** | 12.47 / 65 | 9.60 / 96 | 13.03 / 72 | 6.48 / 136 |
| 6-2 start | 4.92 / 156 | **5.18 / 165** | 8.51 / 107 | 5.76 / 152 | 7.77 / 116 | 4.22 / 156 |
| 0-5 lava | 5.94 / 141 | **6.12 / 143** | 10.77 / 86 | 7.41 / 121 | 10.02 / 92 | 5.27 / 138 |
| 7-3 flashlight | 6.49 / 137 | **6.79 / 132** | 11.59 / 81 | 8.52 / 108 | 11.05 / 84 | 5.72 / 151 |
| 1-1 start (outdoors) | 5.38 / 164 | **5.73 / 156** | 9.52 / 98 | 7.70 / 119 | 9.98 / 94 | 5.03 / 172 |
| 0-2 walk (recording) | 4.76 / 182 | **5.57 / 160** | | | 9.29 / 101 | |
| 1-4 lamps (first run) | 3.31 / 190 | 3.71 / 195 | 4.69 / 178 | 3.97 / 200 | 4.07 / 178 | |
| 8-2 portal side 1 (first run) | 5.88 / 70 | 5.70 / 82 | 8.49 / 66 | 6.77 / 76 | 8.09 / 61 | |

- **The default costs what relight costs** (within 0.8 ms of GPU time) and runs at **132-195 fps** wherever the GPU is
  the limit: the 120 fps target holds in every scene measured. 8-2 is limited by the game's own CPU frame (14 ms in
  relight too) and stays at 70-82 fps in both modes.
- **It misses the target** with 2 paths and 2 light samples (65-107 fps), with a third bounce in the heavy scenes
  (96-121 fps), at RR render scale 1/2 (0-1 metal: 13.6 ms, 70 fps) and on UltraRT's denoiser at its 0.5 scale
  (72-116 fps). `ptx.scale=0.3333` brings the fallback to 136-172 fps, at the temporal upsampler's 1/3-scale texture
  blur ([pt-plan.md](archive/pt-plan.md), 6.1).
- Where the time goes (default, 0-1 metal): `dispatch` 4.11 ms, RR 1.57 ms (Prepare + evaluate + Finish + the
  demodulation pass; 1.32 ms without remodulation), radiance cache 0.19, volumetrics 0.30, viewmodel 0.43.
- What the knobs cost in `dispatch` (0-1 metal / 7-3 flashlight): a second path +4.0 / +3.1 ms (the
  reflection paths double too), a second light sample +1.4 / +1.3 (16 more candidates and one ray), a third bounce
  +2.2 / +1.7, specular lobes at
  path vertices +0.5 / -0.1, a sky sample at every vertex 0 / +0.9 (outdoors only). Without the cache tail the last
  vertex takes a light sample instead: +1.3 / +1.1 ms, so the tail is a saving as well as the missing bounces.
- `relight+fxg.enabled=0` is within 0.1 ms of relight everywhere: the effects pass costs nothing while no effect is
  near.

## Noise (masked burst sigma, 48 frames held, luma 0-255; block = 8-frame means; `bench/results/20261114-231511_pt-noise_alt`)

| | relight | **pathtrace default** | 2p/2b/2d | 3 bounces | Halton jitter | no remodulation | UltraRT denoiser |
|---|---|---|---|---|---|---|---|
| 0-1 metal | 0.64 (0.45) | **0.56 (0.34)** | 0.53 (0.30) | 0.55 | 0.74 | 0.71 | 0.78 |
| 6-2 start | 0.98 (0.75) | **0.56 (0.36)** | 0.55 (0.35) | 0.55 | 0.61 | 0.58 | 0.54 |
| 7-3 flashlight | 0.33 (0.24) | **0.27 (0.18)** | 0.26 (0.16) | 0.27 | 0.32 | 0.27 | 0.30 |
| 8-2 portal side 1 | 0.37 (0.28) | **0.33 (0.18)** | 0.30 (0.16) | 0.33 | 0.40 | 0.37 | 0.39 |

- At a held pose the default is 10-45% quieter than relight, and its block sigma (what survives averaging over 8
  frames: slow boiling) is 25-50% lower. For comparison, RR on relight's signal measured 1.62 on 0-1 metal.
- More samples buy little here (2 paths and 2 light samples: 0.02-0.03), which is why the default is one of each.
- The exact jitter grid and remodulation are each worth 0-25% on their own.
- Mean screen luma is 0-5% under relight's (0-1: 67.8 against 70.7); see "Energy" below.

In motion (replay sigma / 4x4 block sigma, `vol=0`, 360 Hz steps; `bench/results/20261114-232815_pt-motion_alt`):

| | relight | pathtrace default | Halton jitter | 2p/2b/2d | UltraRT denoiser |
|---|---|---|---|---|---|
| 0-2 v1 shadow walk | 2.10 / 1.90 | 2.18 / 2.02 | 2.35 / 2.02 | 2.35 / 2.22 | 1.79 / 1.65 |
| 1-1 spawn sweep (60 deg/s) | 1.74 / 1.38 | 1.93 / 1.68 | 2.32 / 1.91 | 2.27 / 2.03 | 1.98 / 1.59 |

- Moving, RR is 4-11% noisier than relight's denoiser (it was 20% noisier on relight's signal); the exact grid is worth
  7-17% here too. Fresh pixels are where RR is better (0-2: fresh 1.11 against 3.10, young 1.84 against 9.38): it
  has no history to wait for.
- More samples did not measure quieter in motion either; the replay metric's run-to-run spread is about 0.2.

## UltraRT's denoiser at 1/3 (remodulation, 2026-11-15)

Without RR the mode used to trace at 0.5 scale (72-116 fps: over budget) or at 1/3 with the temporal upsampler's texture
blur. It now does what RR's path does ([dlss.md](dlss.md), "Remodulation"), in `Plugin.Remod.cs`:

1. Before the denoiser, the `Demod` kernel writes each surface's demodulation colour D (what it shows under white
   light: albedo x (1 - specular) + specular weight + emission) at trace resolution.
2. The denoiser's composite also writes `composite / D`: lighting only, no texture.
3. Two temporal upsamplers bring both to the screen: the lighting with `remodSharpness` / `remodMaxWeight`, D with
   exact hits (sharpness 30, `ExactReplace` 0.08: a texel that disagrees replaces the history).
4. `RemodFinish` multiplies them back together at screen resolution and applies the tone curve.

At 1/3 the trace is sized for the exact 3 x 3 jitter grid that RR uses (`remodExactGrid`): one trace pixel is exactly
three screen pixels and nine frames visit every screen pixel centre, so D converges to the game's texels.

Masked burst sigma, 0-1 metal (48 frames held; `bench/results/20261115-015835_pt2-d`, run without RR):

| UltraRT denoiser | sigma (block) |
|---|---|
| 1/3, plain | 1.24 |
| 1/3, remodulated, lighting upsampler 4 / 3 (the plain one's) | 1.38 |
| **1/3, remodulated, 1 / 6 (default)** | **1.20 (0.74)** |
| 1/3, remodulated, no exact grid | 1.38 |

- Texture detail at 1/3 now matches RR's and the 0.5 fallback's; plain 1/3 blurs it (crops of 0-1 metal in
  `bench/results/20261115-013855_pt2-b`). The softer lighting upsampler (1 / 6) gives the noise back that sharp
  remodulated samples cost (0.5 / 4 measured 1.90).
- In motion the remodulated fallback measured 1.73 (replay sigma, two-window mean) against RR's 1.96.
- Cost: +0.27 ms GPU over plain 1/3 (0-1 metal 7.12 against 6.85 ms; 6-2 4.59 against 4.32 ms).
- While RR's feature is created (a few frames) the plain upsampler runs instead: the remodulation's first use
  allocates two screen-size upsamplers, which was a 130 ms frame.
- At 0.5 (`ptx.scale=0.5`) it remodulates without the exact grid (0.5 is not an integer upscale); there remodulation
  measured noisier (1.30 against 1.17), so 0.5 users can turn `remodulate` off.

## Motion noise under RR (2026-11-15)

Pathtrace was 4-11% noisier than relight in motion. Measured with the replay metric (`vol=0`, sigma averaged over the
windows; run-to-run spread 0.2-0.35), `bench/results/20261115-014817_pt2-c` and `20261115-020657_pt2-e_alt`:

| 0-2 v1 shadow walk | sigma |
|---|---|
| relight | 1.38-1.59 |
| pathtrace (default) | 1.59-1.96 |
| RR render guard (`plugin._rrGuard=1`) | 2.02 |
| ReSTIR under RR (`ptx.lightsRR=2`; removed 2026-11-19, the value is clamped to 1) | 2.28 |
| 32 RIS candidates | 1.92 |
| RR at 0.5 scale | 1.67 |
| 2 paths | 1.75 |
| 2 light samples | 1.83 |

- The excess is in interior pixels (not edges or disocclusions), with the 4 x 4 block sigma about equal to the pixel
  sigma: low-frequency blotches that RR lets through while the view moves, not missing history. Fresh pixels stay
  RR's strength (1.11 against relight's 3.10).
- None of the inputs move it beyond the metric's spread: more samples, more candidates, a higher render scale, RR seeing
  the guard band. It is RR's temporal behaviour on this signal. What is left to try: diffuse hit distances as an RR guide
  (RR uses them to size its filter; we pass only the specular ones), which needs one more trace output.
- `plugin._rrGuard` (RR evaluates the guard band too, off by default) stays as an option.

## The game's pixels in the traced image (2026-11-18, `t3/ghosting`)

DLSS RR's last step (`RayReconstruction.compute`, kernel `Finish`) decides per screen pixel whether RR's output is
used. A pixel counts as traced only when a trace sample in the 3x3 around it lies at the game's depth (`_Match`:
3 % or 0.05 m). Otherwise the game's own pixel is kept: that is how sprites, the HUD's world parts and anything the
RTAS does not hold stay visible. Where the game's picture is much brighter than the traced one (7-1's white halls)
every kept pixel shows, as white rows, dashes or sparkle. The user's playtest of bc987a8 had three such cases, and
each got its own rule:

| Case | Why no sample matched | Rule | Switch (bench) |
|---|---|---|---|
| Grazing floors (7-1 poses 3, 4): rows of game pixels across a landing | along a floor seen at a low angle, depth changes by more than 3 % from one trace sample to the next | **plane test**: when no sample matched and the centre sample and its 4 neighbours lie on one plane (second difference of 1/depth within `GrazingMatch` = 2 %), predict the depth at the pixel from that plane and compare | `plugin._grazingMatch=0` |
| Sloped surfaces in general | the same, less extreme | **slope tolerance**: each sample's tolerance grows by the depth change across one trace texel, from the sample's normal against its ray (`SlopeTexels` = 1) | with `_thinMatch` |
| Thin geometry (7-1 pose 2 grass, grates) | a blade is narrower than a trace texel: the game drew it, no sample hit it | **thin match**: a pixel with no match that RR itself drew at a hit (`a == 0` but the remodulated sample is a surface) is taken as traced | `plugin._thinMatch=0` |

Measured (`bench/suites/ghost-graze.json`, pathtrace, 1440p, DLSS RR; sheets `bench/results/review/ghost/`):
- 7-1 poses 3 and 4: the rows across the landings are gone (`graze_before_after.png`). **Left**: short dashes on the
  top edge of ledges, where the 3x3 holds two surfaces and neither rule applies. *Gone on master by 2026-11-19 (see
  "Kept-pixel debug view" below).*
- 7-1 pose 2 grass: kept game pixels in the grass crop 8.73 % -> 6.18 %; 16-frame burst sigma over the crop 2.85 ->
  1.93 (all pixels), 0.84 -> 0.65 (8-frame block means). It still sparkles: the remaining kept pixels are blades with
  no traced counterpart in the 3x3 at all. **Not solved**; the real fix is tracing at a resolution where the blade
  exists, or taking the blade's game pixel and relighting it.
- 1-3 pose 1 (control, sprites and lava): no visible change.

**Nearest-depth fallback (2026-11-18, `t3/unlit-lit`; off by default).** The grass is not fullbright: `GrassSprite*`
is lit Master (`VERTEX_LIGHTING`, `ALPHA_TEST`), relit with weight 1 (docs/materials.md, "Lit weight"). The white is
the game's own blade pixels, lit by its white fog (screen luma 235 in the game against 161 traced on pose 2).
`DlssRR.ThinNear` (`plugin._thinNear`, `_Match2.x`) tried one more rule: an unmatched pixel takes the 5x5's trace
sample nearest the game's depth (within that fraction, and not behind the game's pixel by more than 1 %) and is drawn
as traced. On 7-1 pose 2 (`bench/suites/grass-thin.json`, `bench/results/20261118-222544_grass-thin_alt`) game-identical
pixels in the grass crop went 5.17 % -> 1.34 % and burst sigma (all pixels) 2.25 -> 1.84, but 1-3 pose 1's lava turned
bright yellow (also with the in-front restriction): depth alone cannot tell a blade no ray hit from something the game
draws over a traced surface. So it stays off (`ThinNear = 0`). The way out is to identify the blade pixels instead of
guessing from depth: trace the cutout instances (their own instance mask) at screen resolution, or mark the pixels the
game keeps on purpose (lava, acid, untraced sprites) in a screen mask. The cutout pass below does the first.

Cost: not measured with a perf run yet (the rules reuse the 3x3 the kernel already reads, plus one normal read per sample).

**Kept-pixel debug view (2026-11-19, `t3/ledge-dashes`).** `plugin._matchDebug=1` (`DlssRR.MatchDebug`, `_Match2.z` = 2)
paints every pixel `Finish` leaves to the game, by the 5x5's trace sample nearest the game's depth: red when it lies
behind the game's surface, blue in front (full colour at 10 %), dark green when there is no sample (sky). Off, it costs
nothing (a branch on a constant). On 1-3 pose 1 it paints the lava (pink: samples on the bed behind and on the banks in
front), the sky and a blood decal, as expected. On 7-1 poses 3 and 4 (`bench/suites/ledge-dashes.json`,
`bench/results/20261119-002056_ledge-dashes_alt`, two shots each) it paints **no pixel outside the gun and HUD**: the
ledge-top dashes above no longer happen on master (6a46c93, after the cutout pass and the thin match). A one-sided
plane test for ledge edges (any sample of the 3x3 as the anchor, the slope to one neighbour per axis) was built first
and measured with five shots per mode: no difference, since there was nothing left to match. It was dropped.
Measurement trap: counting "game-identical pixels" (|traced - game| <= 1) in a crop is not a count of kept pixels.
On 7-1's grey stairs about 1 % of traced pixels equal the game's by chance. Use this view instead.

### Cutout pass (2026-11-18, `t3/cutout-pass`; on by default)

The pixels the rules above leave are blades the trace has no sample for. The cutout pass traces them at screen
resolution, against the cutout instances only (`src/UltraRT/Plugin.Cutout.cs`, `SceneProxies.CutoutPass.cs`,
`CutoutTrace.raytrace`, kernel `CutoutPrep` in `RayReconstruction.compute`; recorded by `DlssRR.Execute` just before
`Finish`, so DLSS RR only):

1. **Cutout RTAS.** A second acceleration structure holds only the alpha-tested submeshes (`_AlphaTest` proxy
   materials) of the instances in the main RTAS: dynamic proxies (not skinned ones) and static group parts, with the
   main structure's ids, culling and transforms. It is synced every traced frame (add, remove, transforms of dynamic
   proxies; a changed mesh or slot set re-adds) and built only on frames the pass runs. Levels have few: 8 to 15
   instances in 7-1, 8-3, 1-3. Why its own structure: the instance mask has no free bit (0x01-0x80 are all taken),
   and Unity builds the shader table of a DispatchRays from every instance of the structure it traces. Against the
   whole scene that cost 0.45-0.9 ms of render thread a frame, more than the rays.
2. **Gate.** The pass runs only while a cutout instance is in the camera's frustum within `CutoutRange` (150 m;
   cached bounds, dynamic ones refreshed 32 per frame). The log says `Cutout pass: on|idle|off (N cutout instances in
   its RTAS, none in view)`.
3. **`CutoutPrep`** (compute, screen res) marks the pixels that need a ray: not the game's sky, the nearest trace
   sample not an emitter or fullbright (any emission: lava, sprites, screens; without this 1-3's lava sent a ray from
   every pixel), and no trace sample of the 3x3 at the game's depth (the plain match). It writes the game's view depth
   there (ray tracing shaders can't read the depth buffer).
4. **`CutoutTrace`** (one ray per marked pixel, through the trace's jittered camera, as `Finish` maps the pixel)
   looks for an alpha-tested surface up to the game's depth plus the match tolerance. The first one the any-hit's
   alpha test accepts ends the search (`RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH`). Output: the blade's albedo and
   `-(depth + 8)`, or a reason code for the debug view.
5. **`Finish`**: a still-unmatched pixel with a blade is drawn as traced: RR's lighting times the blade's albedo as
   the demodulation colour (`_Match2.y`).

Why a blade **in front of** the game's depth counts too: the debug view on 7-1 pose 2 showed most of the white was
the reverse of a missed blade. Our blade lies in front of the game's depth, and the game shows the ground there
(white in the fog): its alpha test samples the mipmapped grass texture, so distant blades thin out, while ours tests
mip 0. The pixel then shows our blade, as the trace samples around it do. Lava, sprites, walls and anything else the
game draws nearer stop the ray, so they keep the game's pixel.

Debug view `plugin._cutoutDebug=1` (bench variant): green = drawn by the pass; kept pixels blue (no blade in front of
the game's surface), yellow (fullbright blade), cyan (emitter), grey (no ray: prep skipped it). `plugin._cutoutFlip=1`
flips the rays' y (a check of the ray mapping: the blades then miss). Off: `[PathTrace] CutoutPass = false`,
`-ultrart-cutout-pass off`, bench `plugin._cutoutPass=0`.

Measured (2026-11-18, pathtrace, 1440p, DLSS RR; `bench/suites/cutout-look.json`,
`bench/results/20261118-231309_cutout-look`):
- 7-1 pose 2: the white grass sparkle is gone. Burst sigma (all pixels, 16 frames) 2.12 without the pass -> 1.51
  with it; block sigma 0.83 -> 0.78. Without the thin match it is 1.69 / 0.82, so **the thin match stays** (the pass
  does not beat it everywhere: the thin match also catches non-cutout thin geometry).
- 1-3 pose 1: lava unchanged (game-identical pixels 13.3 % without, 13.8 % with).
- 8-3 pose 1, 1-1 grass: no visible change.

Cost (`-Perf`, exclusive lock, RTX 5070 Ti, `bench/suites/cutout-perf.json`, `bench/results/20261118-231937_cutout-perf`;
off -> on; rr = the RR pass, GPU / render thread):

| Pose | avg frame ms | GPU total ms | rr GPU | rr render thread |
|---|---|---|---|---|
| 7-1 pose 2 | 6.87 -> 7.23 | 5.53 -> 5.91 | 1.66 -> 2.03 | 0.69 -> 0.78 |
| 8-3 pose 1 | 13.87 -> 14.56 (noisy: an earlier run measured 14.74 -> 14.71) | 5.74 -> 5.91 | 1.70 -> 1.85 | 1.06 -> 1.27 |
| 1-3 pose 1 | 10.96 -> 11.20 | 4.30 -> 4.52 | 1.64 -> 1.83 | 1.03 -> 1.14 |

The first version traced the whole scene and sent a ray from every unmatched pixel, lava included: 7-1 +0.33 ms,
8-3 +0.71 ms, 1-3 +0.94 ms a frame, mostly render thread (`bench/results/20261118-230113_cutout-perf`). What is left
is about 0.15-0.4 ms of GPU: the full-screen prep and ray launch, plus the rays through the grass. The first-hit flag
made no measurable difference. Levels with no cutout in view pay nothing.

## Blood stains in reflections

The game draws its blood stains as screen-space decals multiplied over the finished frame (`CompositeBlood`), so no ray
saw them. `Plugin.Stains.cs` builds a world-space hash grid of the stains near the camera each frame from the game's own
GPU buffers (`StainGrid.compute`: 6 m cells, 16,384 buckets of 32, stains within `wet.stainDistance` 80 m), with each
stain's box, axes, atlas cell and clip state exactly as the game's stamp shader computes them (BloodWet.shader's port).
`StainTint` (UltraRTStains.hlsl) applies the game's composite at a hit: x (1, 1 - t, 1 - t), t fading with distance over
the fog range, darker where the stain is wet (BloodWet.cs's birth times).

- Used at relight's (and, until 2026-11-19, hybrid's) reflection hits and at the specular vertices of pathtrace's paths. Not at the camera
  hit: there the game's own decal is already in the image.
- Cost (0-1 metal with stains spawned, `bench/results/20261115-021448_pt2-perf`): relight +0.13 ms GPU, pathtrace
  +0.33 ms, CPU +0.06 ms.
- `wet.stainReflections` (settings menu "Blood stains in reflections", `-ultrart-stain-reflections on|off`);
  `wet.stainDebug=1` (bench) paints every stain the lookup finds bright green, at camera hits too, to check it lines up
  with the game's decals, and logs a readback of the game's stain slots, our records and the grid every 2 s (`Stain
  probe: ...`). Verified that way on 0-1 metal (`bench/results/20261115-023727_pt2-l_alt`): the green covers the game's
  floor blood in relight and pathtrace, and the side wall's reflection of it turns green.
- Trap: the game stores a stain's normal pointing into the surface ((0, -1, 0) for a floor stain); its stamp shader's
  clip test compares the surface normal with the quad mesh's own normal (`BloodStainMesh`, z = -1), not with the stored
  one. Comparing with the stored normal rejected every clipped stain (nothing showed until the readback made it plain).
  StainGrid.compute flips the axis by the mesh normal's sign (read from the mesh, logged as `side -1`).

## 8-3 in pathtrace

Cost (GPU ms / fps; `bench/results/20261115-021448_pt2-perf`, exclusive):

| | off | relight | pathtrace | hybrid |
|---|---|---|---|---|
| 8-3 portals | 148 fps | 6.90 / 59 | 6.28 / 70 | 5.41 / 70 |
| 8-3 b | 113 fps | 5.85 / 57 | 5.73 / 63 | 5.37 / 61 |
| 8-3 route (recording, 96 s) | | 6.05 / 69 (1,876 frames over 16.7 ms) | 6.11 / 80 (122 over) | |

- 8-3 is CPU-bound (the game's render thread at 10-13 ms), and pathtrace runs faster than relight there: the main
  thread's render time is 10.2 against 12.6 ms and UltraRT's `lights` section 0.25 against 0.57 ms. Not investigated
  further; it is relight's cost, not pathtrace's.
- Portals and mirrors show the same in pathtrace as in relight: no wrong sides, no missing portal views in the shots.
  Shots of 8-3 can't be compared pixel by pixel across modes: the false-start slab and the arena strips move on the wall
  clock, not the game clock.
- **Remaining defect**: the sliding false-start slab smears and shows a stipple of sharp texels under RR with
  remodulation (UltraRT's denoiser with remodulation too), and is clean with `plugin._rrRemodulate=0` and in relight.
  The remodulation's D upsampler keeps a texel history that the slab's motion should move; the motion debug view shows
  no object motion on the slab in the one frame captured, so its motion is either missing (static-batched geometry
  moved through its batch root has motion slot 0, [denoiser.md](denoiser.md), "Limits") or the slab was at rest.
  Next: a burst of the motion view on the slab; if the motion is missing, give static proxies under a moving batch root a
  motion slot.

## P-2: the orange floor with rain

Reported: P-2's floor looks brighter and more orange in pathtrace with rain wetness. Six shots per mode at the vent pose
(`bench/results/20261115-013047_pt2-a`, `-013855_pt2-b`, `-015835_pt2-d`), floor lighting from the radiance statistics:

| | relight | pathtrace |
|---|---|---|
| wet (rain) | 0.11 | 0.11 |
| dry | 0.14 | 0.10 |

- Bright orange spikes (1.0-1.9) show in 4 of 18 wet shots and none of 12 dry ones, in both modes: the chance of that
  split by luck is about 19%, so it is not tied to wetness either.
- They are P-2's two orange searchlights (colour (1, 0.31, 0), range 200 m) sweeping across the floor on their own clock
  (the bench can't hold them; replays differ by 2-13 luma), plus bloom tinting the gun when one passes. Neither
  pathtrace nor the rain wetness makes the floor brighter or more orange. No change.

## Energy: RIS, ReSTIR and the reference

Mean demodulated lighting at the primary hit (radiance statistics, linear; `bench/results/20261114-234509_pt-energy_alt`):

| | reference (`lights=ref`) | RIS, 16 candidates | relight (ReSTIR) | pathtrace (RR: RIS) | pathtrace, `ptx.lightsRR=2` (ReSTIR) |
|---|---|---|---|---|---|
| 0-5 lava | 0.812 | 0.810 | 0.939 | 0.778 | 0.900 |
| 2-2 floor (blue channel) | 0.466 (1.18) | 0.472 (1.20) | 0.502 (1.73) | 0.410 (1.18) | 0.513 (1.75) |

- RIS without reuse matches the reference within 1%. Relight's ReSTIR (the biased 1/M combine,
  [light-sampling.md](light-sampling.md)) reads 8-16% brighter, and 47% bluer in 2-2: reuse favours the lights whose
  samples survive.
- So under RR `pathtrace` shows slightly less direct light than relight in rooms with many competing lights (lava
  room -16%, 2-2 less blue, 1-4's lamps +17% the other way), and it is the one that agrees with the reference. Relight
  was not changed at the time (its look was the accepted one: it used ReSTIR then; see the next bullet for today's
  default); `ptx.lightsRR=2` gave `pathtrace` ReSTIR, at RR's correlated-noise cost (until ReSTIR went on 2026-11-19; 2 is now clamped to RIS).
- Since 2026-11-15 relight defaults to RIS and its ReSTIR no longer reuses visibility; both match the reference
  within about 2 % (docs/light-sampling.md, "Bias and noise").
- The rest of the difference (pathtrace 0.778 against RIS relight 0.810 in the lava room; 0-5% of screen luma) is the
  path itself: a path vertex on metal splits into a diffuse and a specular lobe, where relight's single bounce treats
  every hit as fully diffuse, and its last vertex is two bounces away instead of one.
