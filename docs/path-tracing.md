# Path tracing mode (`pathtrace`)

`pathtrace` is a view mode next to `relight`: the same light model and look (physical falloff, linear transport, sky and
sun, auto exposure, game and colour match, tone curve, volumetrics, viewmodel pass, lit particles; [relight.md](relight.md)),
but the indirect light and the reflections come from real multi-bounce paths instead of one bounce into the radiance
cache. DLSS Ray Reconstruction denoises and upscales it when it is available ([dlss.md](dlss.md)); UltraRT's own
denoiser is the fallback. Relight is unchanged and stays the default.

How the design was arrived at (the experiments, the alternatives): [pt-plan.md](pt-plan.md).

| File | Role |
|---|---|
| `unity/.../UltraRTPathTraceFull.hlsl` | The paths: `PtPath`, `PtIndirect`, `PtReflection`, `PtMoreDirect` |
| `unity/.../PathTrace.raytrace` | `RayGenBody(full)`: the shared primary-hit code; entry points `RayGen` (every other mode) and `RayGenPT` |
| `unity/.../UltraRTEffects.hlsl`, `EffectProxy.shader` | The effects pass: glowing effects along specular rays |
| `src/UltraRT/Plugin.PathTrace.cs` | `PathTraceSettings`, flags, the per-frame binds |
| `src/UltraRT/EffectGeometry.cs`, `Plugin.Effects.cs` | Effect billboards in the RTAS |
| `src/UltraRT/DlssRR.cs`, `Plugin.Dlss.cs`, `RayReconstruction.compute` | RR for the mode: remodulation, the exact jitter grid |
| `bench/suites/pt-*.json` | `pt-quick` (look), `pt-noise`, `pt-perf`, `pt-fx`, `pt-exp-*` (the experiments) |

## Use

- **Page Up / Page Down** to `pathtrace`, or the settings menu (View mode), or `[View] DefaultMode = pathtrace`.
- Settings menu, section "Path tracing": denoiser, render resolution with RR, paths, bounces, light samples, cache tail,
  sky sample at every bounce. Config section `[PathTrace]`.
- Flags: `-ultrart-pt-paths N`, `-ultrart-pt-bounces N`, `-ultrart-pt-nocache`, `-ultrart-pt-roulette F`,
  `-ultrart-pt-clamp F`, `-ultrart-pt-scale F`, `-ultrart-pt-denoiser auto|rr|ultrart`.
- Bench variants: `pathtrace+ptx.<field>=value` for any field of `PathTraceSettings`, e.g.
  `pathtrace+ptx.paths=2+ptx.bounces=3`, `pathtrace+ptx.denoiser=UltraRT`.
- DLSS RR needs the NVIDIA runtime next to the plugin (`scripts\fetch-dlss.ps1`, [dlss.md](dlss.md)). Without it, or on
  another vendor's GPU, the mode runs on UltraRT's denoiser by itself. The first RR frame of a session takes a few
  seconds (NGX initialises).

## What a pixel traces

The ray generation is relight's with two parts swapped (`RayGenBody(full)`; `full` is a compile-time constant, so
`RayGen` compiles to what it was).

1. **Primary hit**: as relight. Portals teleport the ray, portal mirrors keep the game's view, the per-texel detail
   normal applies, lava ends the ray, V1's model is invisible to camera rays.
2. **Direct light at the primary hit**: `DirectLighting` (fill lights, then lights and emissive triangles through RIS,
   one shadow ray; V1's shadow split and the moving-occluder flag as in relight) and one sky sample.
   - With RR: plain RIS (`lightsRR` 1, `candidatesRR` 16), no reservoirs, `directSamplesRR` samples averaged
     (`PtMoreDirect`). RR wants samples that are independent from frame to frame; see [dlss.md](dlss.md).
   - With UltraRT's denoiser: whatever F6 selects (ReSTIR by default), one sample.
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

Everything is written to relight's G-buffer (`_Radiance`, `_Surface`, `_NormalDepth`, `_InstanceId`, `_HybridLight`,
`_Reflection`, `_Output`), so the reflection filter, both denoisers, auto exposure, volumetrics, the viewmodel pass and
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
| `roulette` | 0.2 | throughput below which paths are thinned (0 = off) |
| `maxRadiance` | 12 | firefly clamp on one diffuse path (lighting units; 0 = off) |
| `specularLobes` | on | specular lobes at path vertices |
| `skyAtVertices` | off | a direct sky sample at every path vertex (the primary hit always takes one) |
| `denoiser` | Auto | Auto (RR when available), UltraRT, RayReconstruction |
| `scaleRR` | 1/3 | trace scale with RR (853x480 at 1440p) |
| `scale` | 0.5 | trace scale with UltraRT's denoiser |
| `pathsOwn` | 1 | paths per pixel with UltraRT's denoiser (0 = `paths`) |
| `lightsRR`, `candidatesRR`, `directSamplesRR` | 1 (RIS), 16, 1 | light sampling at the primary hit under RR |

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
- Used by relight's and hybrid's reflection ray, the water/glass overlay's reflection and every specular segment of a
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
  blur ([pt-plan.md](pt-plan.md), 6.1).
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
  is not changed (its look is the accepted one); `ptx.lightsRR=2` gives `pathtrace` the same sampler, at RR's
  correlated-noise cost.
- The rest of the difference (pathtrace 0.778 against RIS relight 0.810 in the lava room; 0-5% of screen luma) is the
  path itself: a path vertex on metal splits into a diffuse and a specular lobe, where relight's single bounce treats
  every hit as fully diffuse, and its last vertex is two bounces away instead of one.
