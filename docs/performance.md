# Performance (1440p)

Where the frame goes at 2560x1440 (RTX 5070 Ti, trace scale 0.5, DX12, `-force-gfx-jobs legacy`), what was changed for
speed, and what is left. Numbers come from exclusive `-Perf` runs (both game slots held), with relight and pathtrace
interleaved every 0.75 s within one pass (`bench/suites/perf-1440p.json`), so the two modes see the same scene state.
Cross-launch variance is about ±0.2 ms of GPU time; comparisons between builds below are in **one launch**: the shader
bundle hot-reloads, so `deploy.ps1 -Stage <dir>` + `bench.ps1 -KeepOpen` swaps builds without restarting the game.

## Result (2026-11-15, branch `t3/perf-1440p`)

Master (72d5b34) against this branch, same lock hold, each build warmed up with a short run first
(`bench/results/20261115-043857_perf-1440p` vs `20261115-044301_perf-1440p`, both with the same plugin DLL apart from the
volumetrics hit pass; game.log free of 4551):

| Pose | Mode | master ms (fps) | branch ms (fps) | fps | GPU total | GPU dispatch | main-thread render |
|---|---|---|---|---|---|---|---|
| 0-1 metal | relight | 6.40 (156) | 5.55 (180) | **+15%** | 5.60 → 4.67 | 3.54 → 2.65 | 2.52 → 2.58 |
| 0-1 metal | pathtrace | 7.19 (139) | 6.78 (147) | +6% | 6.45 → 6.09 | 3.79 → 3.47 | 2.69 → 2.69 |
| 1-4 lamps | relight | 4.76 (210) | 4.52 (221) | +5% | 3.26 → 2.76 | 1.81 → 1.35 | 2.82 → 2.89 |
| 1-4 lamps | pathtrace | 4.96 (202) | 4.88 (205) | +2% | 3.90 → 3.74 | 1.38 → 1.26 | 3.33 → 3.51 |
| 0-3 ghosts | relight | 6.71 (149) | 6.20 (161) | +8% | 5.79 → 4.81 | 3.65 → 2.68 | 2.89 → 3.04 |
| 0-3 ghosts | pathtrace | 6.72 (149) | 6.49 (154) | +4% | 5.94 → 5.68 | 3.18 → 2.94 | 2.98 → 3.28 |
| 6-2 start | relight | 6.41 (156) | 5.69 (176) | **+13%** | 5.49 → 4.47 | 3.42 → 2.43 | 2.98 → 2.93 |
| 6-2 start | pathtrace | 6.16 (162) | 5.83 (172) | +6% | 5.35 → 5.00 | 2.60 → 2.29 | 3.11 → 3.08 |
| 0-5 lava | relight | 6.98 (143) | 6.05 (165) | **+15%** | 6.03 → 4.91 | 3.83 → 2.75 | 3.00 → 2.83 |
| 0-5 lava | pathtrace | 7.12 (141) | 6.93 (144) | +3% | 6.29 → 6.09 | 3.46 → 3.29 | 3.25 → 3.09 |
| 8-2 portal side 1 | relight | 14.74 (68) | 12.93 (77) | **+14%** | 6.60 → 5.19 | 4.13 → 2.80 | 11.10 → 9.53 |
| 8-2 portal side 1 | pathtrace | 12.63 (79) | 12.49 (80) | +1% | 6.24 → 5.69 | 3.13 → 2.64 | 9.13 → 8.83 |
| 8-3 b | relight | 18.14 (55) | 16.50 (61) | **+10%** | 6.18 → 4.95 | 4.04 → 2.89 | 14.12 → 12.75 |
| 8-3 b | pathtrace | 16.31 (61) | 15.92 (63) | +2% | 5.98 → 5.55 | 3.19 → 2.83 | 12.53 → 12.21 |

`off` in the same runs: 8-2 5.12 / 5.32 ms, 8-3 9.70 / 9.75 ms (the game alone; unchanged code). Relight gains 1.0-1.3 ms
of GPU everywhere; pathtrace 0.2-0.6 ms. 8-2 and 8-3 are bound by the render thread (below), so their pathtrace frame
barely moves.

**Look**: unchanged. Raw RT images (`*_raw.png`, the traced image before the game's transparents, post and HUD) of 0-1
metal, 6-2 start, 1-4 lamps in relight / pathtrace / hybrid match master within run-to-run noise (relight and pathtrace: mean
luma within 0.2%, 9x9-blurred difference ≤ 1/255; hybrid's dim 1-4 shot ±2%, as between two master runs); the final shots differ from master exactly as much as two master runs differ from each
other (animated smoke, HUD, the flickering 1-4 lamp). The skipped calls only advanced random streams, so the noise
pattern of a given frame changes but its statistics do not. `bench/suites/perf-visual.json`,
`bench/suites/perf-volpass-visual.json`.

## Profile (master, before these changes)

GPU per pass (ms, native timer slots; `bench/results/20261115-041519_perf-1440p`):

| relight | 0-1 metal | 1-4 lamps | 0-3 ghosts | 6-2 start | 0-5 lava | 8-2 portal | 8-3 b |
|---|---|---|---|---|---|---|---|
| **total** | 6.02 | 3.25 | 5.79 | 5.53 | 6.11 | 6.57 | 6.19 |
| dispatch (PathTrace RayGen) | 3.97 | 1.80 | 3.64 | 3.47 | 3.90 | 4.10 | 4.05 |
| denoise | 0.61 | 0.34 | 0.59 | 0.57 | 0.59 | 0.57 | 0.59 |
| cache (update + resolve) | 0.31 | 0.23 | 0.32 | 0.32 | 0.30 | 0.47 | 0.49 |
| volumetrics | 0.29 | 0.31 | 0.38 | 0.36 | 0.39 | 0.58 | 0.28 |
| viewmodel | 0.29 | 0.31 | 0.31 | 0.29 | 0.30 | 0.35 | 0.33 |
| upsample (TAAU) | 0.23 | 0.19 | 0.23 | 0.23 | 0.22 | 0.23 | 0.23 |
| rtas | 0.16 | 0.11 | 0.16 | 0.20 | 0.21 | 0.28 | 0.20 |
| particles | 0.12 | 0.13 | 0.12 | 0.12 | 0.13 | 0.16 | 0.16 |
| vfxlate + vollate + exposure + blit + prealpha | 0.28 | 0.25 | 0.28 | 0.27 | 0.32 | 0.27 | 0.27 |

| pathtrace | 0-1 metal | 1-4 lamps | 0-3 ghosts | 6-2 start | 0-5 lava | 8-2 portal | 8-3 b |
|---|---|---|---|---|---|---|---|
| **total** | 7.06 | 3.90 | 5.99 | 5.81 | 6.66 | 6.21 | 5.98 |
| dispatch (RayGenPT) | 4.27 | 1.39 | 3.21 | 3.06 | 3.82 | 3.11 | 3.18 |
| rr (DLSS RR) | 1.74 | 1.55 | 1.62 | 1.61 | 1.61 | 1.60 | 1.61 |
| volumetrics | 0.29 | 0.31 | 0.39 | 0.37 | 0.39 | 0.57 | 0.28 |
| viewmodel | 0.28 | 0.31 | 0.30 | 0.29 | 0.29 | 0.35 | 0.33 |
| cache | 0.19 | 0.20 | 0.20 | 0.20 | 0.20 | 0.30 | 0.32 |
| rtas, particles, the rest | 0.53 | 0.48 | 0.53 | 0.58 | 0.64 | 0.58 | 0.55 |

CPU (ms per frame; main thread from PlayerLoop markers, plugin sections from Perf.cs; relight, pathtrace within 0.1):

| | 0-1 metal | 1-4 lamps | 0-3 ghosts | 6-2 start | 0-5 lava | 8-2 portal | 8-3 b |
|---|---|---|---|---|---|---|---|
| frame (relight) | 6.84 | 4.95 | 6.80 | 6.72 | 7.08 | 14.80 | 18.31 |
| main: scripts / render | 1.08 / 2.78 | 0.80 / 3.05 | 1.09 / 3.03 | 1.06 / 3.38 | 0.99 / 3.08 | 3.55 / 11.22 | 3.86 / 14.43 |
| `off` scripts / render | | | | | | 2.00 / 3.14 | 2.68 / 7.06 |
| plugin `trace` (all sections) | 0.62 | 0.50 | 0.63 | 0.64 | 0.59 | 1.19 | 1.44 |
| static / dynamic / motion | 0.14 / 0.09 / 0.05 | 0.13 / 0.07 / 0.03 | 0.12 / 0.10 / 0.06 | 0.10 / 0.09 / 0.03 | 0.12 / 0.09 / 0.04 | 0.31 / 0.15 / 0.04 | 0.16 / 0.19 / 0.06 |
| emissive / lights / lightsamp | 0.08 / 0.08 / 0.01 | 0.05 / 0.06 / 0.01 | 0.08 / 0.09 / 0.02 | 0.09 / 0.10 / 0.02 | 0.06 / 0.08 / 0.02 | 0.12 / 0.21 / 0.12 | 0.34 / 0.25 / 0.14 |
| managed heap growth (KB/frame, off / relight) | 2.5 / 5 | | | | | 5 / 6 | 53 / 100 |

Hotspots, ranked:

1. **GPU, typical scenes (GPU-bound at 140-200 fps)**: the PathTrace dispatch, 60-65% of our GPU time in relight,
   55-65% in pathtrace. About 1.1 ms of relight's was waste (next sections).
2. **Render thread, 8-2 and 8-3 (CPU-bound at 55-80 fps)**: RT adds 7-8 ms of main-thread render time against 1.2-1.5 ms
   of plugin code and ~6 ms of GPU: Unity's per-`DispatchRays` shader table build ("Render thread" below).
3. **DLSS RR** (pathtrace): 1.6-1.75 ms, fixed by output resolution.
4. **Fixed passes** (both modes, ~1.6 ms together): denoise 0.6, volumetrics 0.3-0.6, viewmodel 0.3, cache 0.2-0.5,
   upsample 0.23, rtas 0.1-0.3, particles 0.15.
5. **Plugin CPU**: 0.5-0.65 ms in small levels, 1.2-1.5 ms in 8-2/8-3 (emissive, lights, static/dynamic sync).
6. **Allocations** in 8-3: ~47 KB/frame more than `off` (GC pauses); not attributed yet ("What's left").

## Short-circuit: HLSL 2018 evaluates both sides

Unity 2022.3.29f1 compiles ray tracing shaders with its bundled DXC (1.6.2106) and passes no `-HV` flag
(`UnityShaderCompiler.exe` passes `-enable-16bit-types`, `-Zpc`, `-Qstrip_reflect`, ... as UTF-16 strings; `-HV` is not
among them), so every shader is **HLSL 2018**. In HLSL 2018 `a ? b : c`, `a && b` and `a || b` do **not** short-circuit:
both operands are evaluated, then selected. For pure arithmetic that is only wasted ALU, but a function call that traces a
ray is executed every time. Checked with the SDK's dxc on a minimal ray generation shader:

```hlsl
float v = Cached(o, c) ? c : Shadow(o);   // Shadow() calls TraceRay
```

`-HV 2018` emits the `traceRay` call unconditionally followed by a `select`; `-HV 2021` emits a `br` around it.

Sites that traced rays (or read textures/buffers) for results that were then thrown away:

| Where | Expression | Waste per evaluation |
|---|---|---|
| `PathTrace.raytrace`, diffuse bounce hit | `CacheQueryTotal(...) ? cached : DirectOne(...)` | RIS candidates + 1 shadow ray at every bounce hit the radiance cache already answered |
| `UltraRTLights.hlsl`, `SampleVisible` | `emissive ? EmissiveVisible(...) : LightVisible(...)` | a second shadow ray for every final light sample (primary, bounce, cache update, particle light, viewmodel) |
| `UltraRTLights.hlsl`, `RestirMerge` re-test | same pattern, plus `!vis && ... && !LightCentreVisible(...)` | two to three shadow rays per re-tested history instead of one |
| `UltraRTLights.hlsl`, `DirectOne` | `W > 0.0 && SampleVisible(...)` | shadow rays (two, see above) when no light was selected |
| `UltraRTLights.hlsl`, `LightEvalRaw` | `area ? LightAreaClosest(...) : l0.xyz` | the area-light closest-point math (octahedral decode, normalises) for every light evaluation: every RIS candidate, every fill light |
| `UltraRTLights.hlsl`, `LightFrom`, reference `DirectLighting` | `!LightUnshadowed(i) && !LightVisible(...)` | shadow rays toward unshadowed lights (reference mode) |
| `UltraRTLights.hlsl`, `DirectLighting` | `temporal && !RestirReuse(...)` | ReSTIR's reprojection in RIS mode (it returned at once there: `_RestirHistoryValid` is 0) |
| `PathTrace.raytrace`, end of `RayGen` | `_TransParams.y > 0.5 ? TraceTransparent(...) : c` | the water/glass ray (and its reflection ray where it hits water) in every mode, also where nothing reads it |
| `PathTrace.raytrace`, `ReflectionHitLight` | `_ReflCache.x > 0.5 && CacheLookup(...)` | cache lookup with the cache off |
| `Proxy.shader`, closest hit | `wantDetail && DetailSample(...)`, `wantMotion ? HitMotion(...) : 0`, `_UseAlbedoAsEmissive ? albedo : _EmissiveTex.Sample(...)` | the detail texture read, the motion buffer reads and the emission texture read at **every** hit (bounce, reflection, cache, volumetric rays), not only where used |
| `Viewmodel.raytrace` | `shadows ? DirectOne(...) : FillLighting(...)` | the shadow ray the checkerboard mode meant to skip ("no extra shadow ray: the pass is latency-bound") |
| `UltraRTPathTraceFull.hlsl` | `specular ? albedo * StainTint(...)`, `last && cacheTail && CacheQueryTotal(...)` | the stain lookup and the cache lookup at every path vertex |
| `Volumetrics.raytrace` | `... \|\| !Unoccluded(...)`, `tB > tA && ClipSpot(...)` | the emitter sample's shadow ray when the sample was already rejected |

All were rewritten as `if`/`else` (`[branch]` where the skipped side traces). The skipped calls had no side effects
other than advancing the per-pixel random streams (`rng`, `g_ShadowRng`), so results are statistically identical; the
noise pattern of a given frame differs.

**Rule for new shader code:** never put a call that traces, samples a texture or reads a buffer behind `?:`, `&&` or
`||`. Use an `if`. (Pure arithmetic is fine, and often cheaper as a select.)

Measured alone (all fixes but the trace-site change below, against master in one launch,
`bench/results/20261115-041519_perf-1440p` vs `-041230_perf-1440p`): relight dispatch −0.9 to −1.2 ms in every scene
(0-1 metal 3.97 → 2.82, 8-3 b 4.05 → 2.97); pathtrace ±0.3 ms (its dispatch varies that much between runs).

## The light-overrides merge regression

Reported: ee52438 (light overrides, area lights) added 0.2-0.3 ms of GPU in every scene, also in levels without an
override file. It is real, and larger than that in relight: one launch, master vs master with only the two shader files
that merge changed (`UltraRTLights.hlsl`, `Exposure.compute`) reverted to 33c2399 (`bench/results/20261115-041759_perf-1440p-short`
vs `-041519_perf-1440p`):

| relight dispatch (ms) | master | pre-overrides shaders | all fixes |
|---|---|---|---|
| 0-1 metal | 3.97 | 2.83 | 2.82 |
| 6-2 start | 3.47 | 2.54 | 2.55 |
| 8-3 b | 4.05 | 2.95 | 2.97 |

(pathtrace: 4.27 / 4.18, 3.06 / 2.74, 3.18 / 2.98). The cross-launch merge-regress comparison saw less of it.

Cause: the area-light code, through the short-circuit problem.
- `LightEvalRaw`: `area ? LightAreaClosest(...) : l0.xyz` ran the closest-point math for every light evaluation. Moving
  only that behind an `if` recovered 0.07-0.13 ms (`bench/results/20261115-040521_perf-1440p`).
- `LightVisible` got a second `TraceRay` site (the area branch), and `LightVisible` is inlined everywhere, including both
  sides of `SampleVisible`'s `?:`. That was the bulk.

Not a cost: the override lookups on the CPU (`lights` 0.06-0.10 ms in small levels), and the Exposure.compute fit branch
(gated by a uniform). `bench/suites/lights-perf.json` compared overrides on/off at run time, with the same compiled shader
both ways, so it could not see a compile-time cost.

## One shadow-ray trace site

Each `TraceRay` call site in a function that is inlined into a ray generation shader costs, even when it rarely runs (the
area-light regression above). The light visibility code now computes the ray first and traces once:
`LightShadowSetup` (point / spot / sun jitter, area-light sample, fixture clearance, player-light mask) and
`EmissiveShadowSetup` return the direction, length and instance mask; `VisibleRaw` holds the only shadow `TraceRay`;
`VisibleTo(code, ...)` serves both lights and emitters (`SampleVisible`, the ReSTIR re-test). Same random draws in the same
order, so results are bit-for-bit those of the previous step. In one launch against the previous step
(`bench/results/20261115-043011_perf-1440p` vs `-042643_perf-1440p`): dispatch −0.04 to −0.15 ms in relight, −0.15 to
−0.63 ms in pathtrace (0-1 metal 4.54 → 3.91).

## Render thread (8-2, 8-3)

In 8-2 and 8-3 the frame is the main thread's render time (scripts + render = frame), and RT adds 7-8 ms to it while our
managed code is 1.2-1.5 ms and the GPU ~6 ms. Unity rebuilds the ray tracing shader table at **every `DispatchRays`**:
one hit record per enabled submesh in the RTAS (457 in 8-3), each with its material's constants and textures. A frame
has five dispatches against the scene RTAS (PathTrace, radiance cache update, volumetrics, particle light, viewmodel).
`perf-dispatch-diag` adds 1x1 dispatches of the debug view (`plugin._diagDispatches=N`), with the real hit pass or with a
pass no material has (`plugin._diagNoHit=1`, no hit groups) (`bench/results/20261115-041952_perf-dispatch-diag`):

| | 4 extra dispatches | the same, no hit groups | viewmodel RT off (`plugin._vmEnabled=0`) |
|---|---|---|---|
| 0-1 metal | +0.82 ms frame | +0.14 | −0.18 |
| 8-2 portal side 1 | +2.56 | +0.62 | −0.87 |
| 8-3 b | +2.00 | +0.31 | −1.43 |

So a dispatch costs 0.5-0.65 ms of render thread in 8-x, almost all of it the material records. Switching any one pass
off saves 0.7-1.0 ms of frame there (`perf-8-3-diag`: volumetrics, cache, particle light), far more than its GPU time.
Relight renders ~1 ms slower than pathtrace in 8-x for other reasons (TAAU −0.35 ms when off; ReSTIR vs RIS no change).

**Volumetrics on a slim hit pass**: volumetrics only traces visibility rays (`SKIP_CLOSEST_HIT`, mask 0x05, where every
instance has an `UltraRT/Proxy` material), so Proxy.shader has a second ray tracing pass, `UltraRTShadow`, with only the
alpha test (`SampleAlbedo`'s alpha: main texture, atlas blend texture, `_Color.a` against `_Cutoff`), and Volumetrics.cs
dispatches with it (`vol.shadowPass`, default on). In one launch (`perf-volpass`): 8-3 relight −0.27 ms, 8-2 relight
−0.18 ms, elsewhere within noise; the 1-4 lamp halo alternating on/off at four times shows no systematic difference
(halo luma on − off: +1.9, +1.5, −1.4, −1.5; the lamp flickers by ±3 on its own). Smaller than hoped: a slim record is
still a record.

## Profiling (2026-11-17, branch `t3/perf-profile`)

Tools added for the 120 Hz pathtrace / 240 Hz relight work (docs/plan-look-and-perf.md, 5.1). They answer three
questions the numbers above could not: is a frame bound by the GPU, the main thread or the render thread; what does
each pass cost on each of the three; and where do the slow frames come from.

| Tool | What it gives |
|---|---|
| timer slot `frame` | The whole frame's rendering, the game included, in every mode (also `off`): GPU span from the first to the last render command. Close to the frame time = GPU-bound |
| render-thread ms per slot | The render thread's own time between a pass's begin and end events (`UltraRT_GetCpuTimings`, docs/gpu-timer.md): shader tables, state, copies. Summary table "GPU ms / render-thread ms per pass", `results.json` `RenderThreadMs` |
| main-thread ms per pass | Perf sections `cache`, `particles`, `denoise`, `volumetrics`, `vfx`, `viewmodel`: recording the pass's commands |
| `bench.ps1 -Floor` | `-Perf` at trace scale 0.2: every pass still runs with little GPU work, so the frame is what the CPU side costs |
| `bench.ps1 -Etw` + `tools/perf/etw_report.py <run>` | A CPU trace of the launch (Windows Performance Recorder), cut to each mode's measured frames: per thread, CPU by function (self and inclusive, with Unity's public symbols) and blocked time by what it waited in. `tools/perf/folded.py` drills into one function |
| `tools/perf/hitches.py <run>` | Every frame over 12 ms attributed to scripts, rendering, a plugin section, or waiting |
| suites `perf-core`, `perf-etw` | The standard poses: 0-1 metal (calm), 0-3 ghosts (enemies), the 0-2 run with enemies and gunfire |

### ETW
A kernel CPU trace needs administrator rights. `scripts\etw-setup.ps1` asks for them once and registers three scheduled
tasks that run `wpr.exe` elevated with fixed arguments (start, stop to `D:\UltraRT-Bench\etw\ultrart.etl`, cancel);
`scripts\etw.ps1` and `bench.ps1 -Etw` use them with no further prompt (`etw-setup.ps1 -Remove` deletes them). The
trace is system-wide while it records and stays on the machine (`trace.etl` in the run's folder, gitignored; never
commit or upload one). It grows by about 50 MB per second of a running game (4.4 GB for a 90 s launch) and converting
it takes about ten minutes, so use the short `perf-etw` suite. Symbols come from Microsoft's and Unity's public symbol
servers (`UnityPlayer.dll` resolves fully for this build; NVIDIA's driver modules have none).
**Tracing slows the game**: `off` 1.82 -> 3.21 ms, relight 5.81 -> 7.23 ms at 0-1 (stack walks on every context
switch). Read a trace for shares and call paths, and take frame times from an untraced run.

### Where the frame goes (untraced, `perf-core`, 2560x1440, trace scale 0.5, RTX 5070 Ti)

| Scenario | Mode | frame ms | GPU span of the frame | main thread (scripts + render) | bound by |
|---|---|---|---|---|---|
| 0-1 metal | off | 1.82 | 0.24 | 1.80 | main thread |
| 0-1 metal | relight | 5.81 | 5.63 | 3.55 | **GPU** |
| 0-1 metal | pathtrace | 8.06 | 7.96 | 3.87 | **GPU** |
| 0-3 ghosts | relight | 6.95 | 5.65 | 4.69 | GPU, then main thread |
| 0-3 ghosts | pathtrace | 7.79 | 7.66 | 4.74 | **GPU** |
| 0-2 run, combat | off | 2.89 | 0.33 | 2.86 | main thread |
| 0-2 run, combat | relight | 8.95 | 6.80 | 6.67 | **main thread** (and GPU) |
| 0-2 run, combat | pathtrace | 10.10 | 9.54 | 7.10 | **GPU** |

The game's own GPU work is 0.2-0.3 ms; everything else on the GPU is ours. The plan's reading that relight is
CPU-bound holds only once its GPU time is under the CPU floor (3.9 ms calm, `-Floor`): at trace scale 0.5 a calm
relight frame is GPU-bound, and in combat the main thread and the GPU are both near the frame time.

Per pass, relight at 0-1 (GPU / render thread / main-thread record, ms; combat in brackets where it differs):

| Pass | GPU | render thread | main thread |
|---|---|---|---|
| main dispatch | 2.89 (3.24) | 0.25 (0.40) | (in `trace`) |
| denoise + upsample | 0.82 | 0.06 | 0.04 |
| cache update | 0.29 | 0.31 (0.50) | 0.01 |
| volumetrics | 0.30 (0.38) | 0.17 (0.25) | 0.03 |
| viewmodel | 0.28 (0.64) | 0.32 (0.47) | 0.16 |
| particle light | 0.11 | 0.23 (0.37) | 0.01 |
| RTAS build | 0.16 | 0.20 (0.35) | |
| DLSS RR (pathtrace) | 1.62 | 0.64 (0.77) | 0.05 |
| all of ours | 4.92 (5.48) | 1.32 (2.05) | 0.63 (1.06) + 0.16 |

The four auxiliary dispatches cost the render thread 1.0 ms (1.6 in combat) for 1.0 ms of GPU work; RR's render-thread
cost (0.64) was not known before.

### What the trace shows (0-1 metal and the 0-2 run; shares, not absolute times)

- **The render thread is not the limit in 0-x.** In relight it works 4.4 ms of a 7.2 ms traced frame and waits 2.9 ms
  for commands from the main thread (`ThreadedStreamBuffer::HandleOutOfBufferToReadFrom`). The main thread works 5.6 ms
  and waits 1.5 ms for the swap chain (`SwapChainWaitingContext::WaitOnSwapChain`): the GPU. In combat the main thread
  works 8.9 of 10.4 ms.
- **Main thread, what relight adds over `off`** (+2.5 ms traced): managed code +1.3 (our camera callbacks 1.0), executing
  our command buffers 0.6, of which `RayTracingShader::DispatchRays -> BeforeDispatch ->
  RayTracingAccelerationStructure::SetInstanceShaders` is 0.33 (five dispatches; each schedules a parallel job over
  every instance's hit-group shaders and waits for it), the game's UI canvas rebuild +0.19, physics +0.27.
- **Render thread** (4.4 ms): `DispatchRayTracingProgram` 1.30 (`D3D12RayTracingState::Transition` 0.65,
  `WriteHitGroupShaderRecords` 0.58), present 0.92 (`QueueExecute` 0.46), `BufferD3D12::InitializeResource` 0.23: a
  vertex buffer is created anew every frame under `GeometryJobTasks::ScheduleGeometryJobs` (a mesh written each frame
  that is not marked dynamic; to find and fix), `BuildRayTracingAccelerationStructure` 0.21.
- **Job workers**: eleven workers spend 1-2 ms each per frame, about a third in `SetHitGroupShadersJob` and as much in
  waking each other (`lane_wake_buddies`): the per-dispatch shader setup again.
- **A driver thread compiles all the time**: one NVIDIA thread (`nvrtum64` -> `nvgpucomp64`) uses 90 % of a core
  during the whole run, in `off` too. It is the driver's background optimisation of the ray-tracing pipelines after a
  new shader bundle (the "warm up after a new bundle" trap below); whether it ever finishes within a session, and what
  the frame costs before and after, is not measured yet.

### Hitches
`hitches.py` on the 0-2 run (untraced): the frames over 12 ms are **the game's script phase when an explosion
spawns**. The bench spawns one every 0.6 s there, and `off` has the same frames (16 of 3043, +13 ms each); with RT on
they are +15 ms, and because an RT frame is three times longer the same hitches are a larger share of the frames, which
is what moves p99 from 5.8 ms to 21 ms. Of 53 slow relight frames 35 are scripts, 11 waiting, 4 rendering, 3 inside
`Plugin.Trace` (the `lights` section's 6-9 ms frame, once per level). So the p99 of the combat run mostly measures the
game's explosion instantiate; what RT adds per hitch is about 2 ms.

### What this changes in the plan
- Relight 240 Hz: GPU first (4.9 ms of ours against a 4.17 ms frame), then the main thread; the render thread only in
  8-x. The per-dispatch cost is on **three** threads (main 0.07, render 0.25, workers), which makes fewer dispatches
  (C2, C3) worth more than estimated.
- New candidates: the per-frame vertex buffer creation (0.2 ms render thread), RR's 0.64 ms render-thread cost (what
  in it is ours: the feature inputs or the evaluate call), the UI canvas rebuild the game does more of with RT on.
- Hitches are mostly not ours; the `lights` frame is.

## How these were measured (and traps)

- Suites: `perf-1440p` (seven poses, relight + pathtrace interleaved, 8-2/8-3 with `off`), `perf-1440p-short`,
  `perf-8-3-diag`, `perf-dispatch-diag`, `perf-volpass`, `perf-visual`, `perf-volpass-visual`.
- Builds compared in one hold: `build.ps1 -StageDir stage-x` per variant, then `deploy.ps1 -Stage stage-x` +
  `bench.ps1 -KeepOpen` per run. A bundle-only difference hot-reloads; a DLL difference relaunches.
- **Don't build during your own perf run.** A dotnet build during one run raised every frame time 1-3 ms, `off` included
  (GPU slots unaffected).
- **Warm up after a new bundle.** The first run after a launch with a new bundle had frame times 0.3-2 ms off (the driver
  compiles the RT pipelines in the background); GPU slots were fine. Run `perf-1440p-short` first and discard it.
- Pathtrace dispatch varies ±0.3 ms between runs of the same build; relight ±0.05.
- `build.ps1 -SkipShaders` stages whatever bundle was built last (another stage's, if you built one).
- Edits to `UltraRTLights.hlsl` / `Proxy.shader` (mixed line endings in git) with tools that rewrite the whole file turn
  every line CRLF: restore the original endings before committing.
- `GC.GetAllocatedBytesForCurrentThread` returns 0 in this Mono (Boehm GC): allocations can only be seen as heap growth.

## What's left

1. **Render thread in 8-x** (largest remaining item, 55-80 fps there): about 0.5 ms per DispatchRays. Ideas, by expected
   gain: fewer shader-table records (merge dynamic proxies that never move into static groups; 8-3 has 311 dynamic records
   in 252 instances), fewer full-material dispatches (the particle light could trace its few closest-hit rays from the
   radiance cache instead and use the slim pass; the viewmodel pass could reuse the frame's cache instead of tracing the
   scene), or one dispatch for several passes (Unity's 8-UAV limit makes that hard).
2. **PathTrace dispatch** (2.4-2.9 ms relight, 2.3-3.5 ms pathtrace): register pressure of the one big RayGen. Next
   experiments: compile-time specialisation of the relight path (`_Mode` is a uniform; hybrid-only and reference-only code
   is compiled into every mode), fewer `TraceScene` sites (bounce, reflection and transparent rays each inline the portal
   loop).
3. **Allocations in 8-3**: ~47 KB/frame more than `off` (GC pauses every few seconds there). Needs a sampling approach
   (heap growth per section over many frames) since the per-thread counter is not available.
4. **Denoiser + TAAU** (0.8 ms relight): the a-trous passes at trace resolution; fold the TAAU resolve into the composite.
5. A one-off ~5-6 ms `lights` frame at the start of every level (also without an override file).
