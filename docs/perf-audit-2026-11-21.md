# UltraRT Performance Audit & SOTA Light-Transport Research (2026-11-21)

**Commit audited:** `master` (`1a240ae`, clean stage build with real DLSS Ray Reconstruction `nvngx_dlssd.dll`)
**Test machine:** NVIDIA GeForce RTX 5070 Ti (Blackwell), 2560×1440, Windows D3D12 DXR, Unity 2022.3.29f1
**Profiling runs (exclusive `-Perf` lock, zero GPU sharing):**
- `20261121-162600_perf-core` (`warm-up`, `0-1 metal`, `0-3 ghosts`, `0-2 run combat`)
- `20261121-162600_perf-combat` (`warm-up`, `8-3 combat` in `off`, `pathtrace`, `relight`)
- `20261121-163408_perf-1440p` (`warm-up`, `0-1`, `1-4`, `0-3`, `6-2`, `0-5`, `8-2 portal side 1`, `8-3 b`)
- `20261121-163708_perf-core` (`-Floor`, trace scale 0.2 CPU/RT-thread floor)
- Companion SOTA literature survey: Claude Sonnet 5.5 (`effort: low`) research brief on 2023–2026 real-time light transport.

---

## Executive Summary

On an RTX 5070 Ti at 1440p, UltraRT's default `pathtrace` (1/3 scale + DLSS RR) runs at **119–164 fps** in calm early-game rooms, **91 fps** in `0-2` combat, **69 fps** in `8-2` portals, and drops to **56–60 fps** (with **12 fps 1% lows** and **779 frames >16.7 ms**) in `8-3` (`8-3 b` and `8-3 combat`). Crucially, **UltraRT is NOT primarily GPU-bound in its worst scenes — it is overwhelmingly Render-Thread CPU-bound and Main-Thread Hitch-bound:**

| Regime | Representative Scene (`pathtrace`) | Avg ms (fps) | 1% low | UltraRT GPU total | Whole-frame RT-CPU | Primary Bottleneck |
|---|---|---:|---:|---:|---:|---|
| Calm early | `0-1 metal` / `0-3 ghosts` | `8.02–8.42 ms` (`119–125 fps`) | `81–83 fps` | `6.74–7.07 ms` | `6.31–7.56 ms` | **GPU balanced** (`dispatch 3.4 ms`, `rr 1.9 ms`) + 5 s `lights` hitch |
| Early combat | `0-2 run combat` | `11.00 ms` (`91 fps`) | `30 fps` | `7.44 ms` | `9.16 ms` | **Mixed CPU/RT-CPU + scripts spikes** (`-Floor` relight = `8.44 ms`) |
| Layer 8 portals | `8-2 portal side 1` / `8-3 b` | `14.49 / 17.97 ms` (`69 / 56 fps`) | `57 / 27 fps` | `6.78 / 6.20 ms` | **`12.83 / 16.14 ms`** | **Render-thread SBT rebuilds** (6 full-scene `DispatchRays`) + portal cams |
| Heavy combat + portals | `8-3 combat` | `16.64 ms` (`60 fps`) | **`12 fps`** | `6.77 ms` | **`14.39 ms`** | **RT-CPU SBT rebuilds (`5.9 ms`) + RR flush (`1.3 ms`) + main-thread hitches** |

### Six Biggest Empirical Findings
1. **Per-dispatch Shader Binding Table (SBT) rebuilds consume `4.6–5.6 ms` of render-thread CPU in `8-2`/`8-3`:** Every `DispatchRays` against the full scene RTAS forces Unity 2022.3 to rebuild hit-group shader records across all active RTAS instances (`PathTrace`, `CacheUpdate`, `ParticleLight`, `Volumetrics`, `Viewmodel`, plus an unslotted 6th dispatch **`VeilTrace` at 1280×720** in `0-3`, `8-2`, `8-3`!). In `8-3 combat`, `CacheUpdate` (`1.12 ms RT-CPU` vs `0.29 ms GPU`) and `ParticleLight` (`0.86 ms RT-CPU` vs `0.19 ms GPU`) spend **4–4.5× more time on the render thread than on the GPU**.
2. **99% of active dynamic RTAS proxies in `8-3 b` never move:** `8-3 b` keeps `364` active dynamic proxies in the RTAS (hundreds of individual SBT records), yet only **`1.9` rigid transforms/frame** actually change. Even in `8-3 combat` (`604–624` active dynamic proxies), `~480` are stationary. Merging non-moving dynamic proxies into static groups (**C4**) cuts total SBT records by ~50–65%.
3. **Unity 2022.3.29f1 empirically lacks managed `RayQuery` compute bindings — but `UltraRT.Native.dll` can unlock DXR 1.1 inline `RayQuery`:** Reflection on `UnityEngine.CoreModule.dll` confirms `ComputeShader` and `CommandBuffer` expose zero compute RTAS binding methods (and Unity bundles DXC `1.6.2106.3`), while Windows SDK `dxc.exe` (`1.9.2602.17`) compiles `cs_6_5` `RayQuery` cleanly. Because `UltraRT.Native.dll` already hooks `IUnityGraphicsD3D12v7`, native D3D12 `RayQuery` compute dispatches can eliminate SBT rebuilds completely for visibility/auxiliary passes (`Volumetrics`, `ParticleLight`, `CutoutTrace`, `VeilTrace`).
4. **DXIL disassembly proves extreme register/code bloat in `PathTrace.raytrace`:** Compiling with `dxc -T lib_6_3 -HV 2018 -O3` shows **`RayGenPTImg` has 30,039 DXIL instructions, 37 inlined `TraceRay` sites, 245 `log2` + 262 `exp2` (~250 `pow()` calls), 123 trig ops, and 2,368 B of stack allocas** (`RayGenPT`: 27,748 inst / 37 `TraceRay`). Even `Viewmodel::RayGen` is 14,281 inst / 13 `TraceRay` sites. Major causes: unstripped `_LightSampling <= 0` reference branches inlined into every entry point, `PtPath`/`TraceSceneCone` duplicated across diffuse + specular + transparents, and `UltraRTLights.hlsl::LightEvalRaw` executing **3 `pow()` calls per out-of-range RIS candidate before range-culling**.
5. **Hidden 5-second hitch bug across the ENTIRE game:** `Plugin.Noise.cs:65–78` (`BakedLightMask`) calls `FindObjectsOfType<StaticSceneOptimizer>()` **every 5.0 seconds forever** inside `GatherLights()`, causing a **`6.2–14.8 ms` main-thread `lights` freeze** in every single level (`warm-up` through `8-3 combat`, responsible for `40–62` spikes in `8-3 combat`). Two companion bugs: `LitParticles.KeepOrbGlows` (`LitParticles.cs:262`) scans all scene `MeshRenderer`s (`37.2 ms` in `8-3`, `11.5 ms` in `0-2`), and `Plugin.LightOverrides.cs:247` (`ResolveAddBinds`) scans all `Renderer`s every 2 s if any `"bind"` path is missing (`7-4`).
6. **Huge combat & portal wastes hidden in plain sight:**
   - `Volumetrics.raytrace:517,525` evaluates `LightTarget()` (with quadratic spot clipping + 2 `atan()` calls) **twice per light** when `count <= 128` — up to **256 calls / 512 `atan()` per low-res pixel** in `8-2`/`8-3`. Single-pass weighted reservoir sampling halves this ALU with zero visual change.
   - `8-3 combat` removes and re-adds **`2,306` rigid instances**, of which **`1,921` (`83.3%`)** already had their mesh in `_meshesInRtas`.
   - `8-3 b` allocates **`89–102 KB/frame`** on the managed heap and bakes **`20` distant candle `FireSprite` proxies (`63–67 m` away)** into the RTAS every frame.
   - `SceneProxies.Portals.cs:551–577` (`BindPortalBuffer`) runs `2 × Matrix4x4.inverse` per portal side + two `ComputeBuffer.SetData` uploads every frame even when no portal moved.
   - `ReflectionFilter.Execute` runs full `Temporal` + `Spatial` + `Resolve` (5 RenderTextures + 3 compute dispatches) **even under DLSS RR**, feeding RR pre-filtered reflections instead of raw 1-spp reflections.

---

## Phase 1: Fresh Baseline Data (`master` `1a240ae`)

### 1.1 Whole-Frame & CPU/GPU Summary Tables (2560×1440, RTX 5070 Ti)

#### `perf-core` & `perf-combat` (Trace scale 0.5 for `relight`, 0.333 + DLSS RR for `pathtrace`)
| Scenario | Mode | Avg ms | fps | 1% low | p99 ms | Worst ms | >16.7 ms | Delta vs off | GPU total / dispatch / denoise | Main scripts / render | Plugin avg / worst | Alloc KB/f |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|
| `warm-up` (0-1) | `relight` | 6.72 | 149 | 125 | 7.13 | 10.3 | 0 | — | 5.83 / 3.64 / 0.64 | 0.91 / 2.85 | 0.74 / 6.93 | 4.7 |
| `warm-up` (0-1) | `pathtrace` | 7.40 | 135 | 114 | 7.81 | 11.3 | 0 | — | 6.49 / 3.35 / 0.05 | 0.93 / 3.04 | 0.81 / 8.15 | 6.5 |
| `0-1 metal` | `off` | 2.34 | 427 | 180 | 3.84 | 27.4 | 1 | — | — | 0.82 / 1.50 | 0.00 / 0.00 | — |
| `0-1 metal` | `relight` | 7.37 | 136 | 95 | 9.37 | 14.1 | 0 | +5.03 | 5.94 / 3.56 / 0.68 | 1.24 / 3.59 | 0.94 / 9.77 | 4.8 |
| `0-1 metal` | `pathtrace` | 8.02 | 125 | 81 | 9.98 | 19.5 | 1 | +5.67 | 6.74 / 3.45 / 0.05 | 1.34 / 3.74 | 0.97 / 2.12 | 5.1 |
| `0-3 ghosts` | `off` | 3.00 | 334 | 173 | 4.99 | 11.0 | 0 | — | — | 1.14 / 1.83 | 0.00 / 0.00 | — |
| `0-3 ghosts` | `relight` | 8.28 | 121 | 82 | 10.50 | 14.1 | 0 | +5.28 | 6.29 / 3.44 / 0.69 | 1.55 / 4.06 | 1.13 / 9.52 | 6.6 |
| `0-3 ghosts` | `pathtrace` | 8.42 | 119 | 83 | 10.73 | 13.4 | 0 | +5.43 | 7.07 / 3.36 / 0.05 | 1.67 / 4.63 | 1.19 / 7.72 | 7.7 |
| `0-2 run combat` | `off` | 3.90 | 257 | 49 | 13.34 | 37.2 | 19 | — | — | 1.79 / 2.09 | 0.00 / 0.00 | — |
| `0-2 run combat` | `relight` | 10.41 | 96 | 35 | 24.28 | 47.8 | 29 | +6.52 | 6.30 / 3.79 / 0.72 | 3.22 / 5.28 | 1.47 / 7.81 | 10.4 |
| `0-2 run combat` | `pathtrace` | 11.00 | 91 | 30 | 27.42 | 56.9 | 38 | +7.11 | 7.44 / 4.07 / 0.05 | 3.61 / 5.82 | 1.58 / 10.21 | 12.1 |
| `8-3 combat` | `off` | 8.02 | 125 | 33 | 28.06 | 41.6 | 102 | — | — | 4.06 / 3.92 | 0.00 / 0.00 | — |
| `8-3 combat` | `pathtrace` | **16.64** | **60** | **12** | **37.04** | **336.9** | **779** | +8.63 | 6.77 / 2.64 / 0.05 | **6.29 / 10.15** | 2.84 / 20.66 | **33.6** |
| `8-3 combat` | `relight` | **18.52** | **54** | **14** | **35.74** | **217.2** | **1722** | +10.50 | 6.89 / 3.75 / 0.70 | **6.13 / 12.07** | 2.56 / 20.80 | **24.6** |

#### `perf-1440p` (Expanded Level & Layer 8 Portal Coverage)
| Scenario | Mode | Avg ms | fps | 1% low | p99 ms | Worst ms | >16.7 ms | GPU total / dispatch | Main scripts / render | Plugin avg / worst | Alloc KB/f |
|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|
| `1-4 lamps` | `relight` / `pathtrace` | 5.60 / 6.12 | 179 / 164 | 137 / 133 | 7.10 / 6.64 | 8.0 / 11.1 | 0 / 0 | 3.70/1.87 vs 4.80/1.19 | 0.87/3.31 vs 0.91/3.55 | 0.70/4.46 vs 0.80/7.08 | 5.8 / 4.8 |
| `6-2 start` | `relight` / `pathtrace` | 7.28 / 7.12 | 137 / 140 | 101 / 101 | 9.29 / 8.71 | 10.9 / 13.9 | 0 / 0 | 5.54/3.08 vs 5.74/2.23 | 1.18/3.93 vs 1.19/4.39 | 0.91/5.10 vs 0.96/9.29 | 5.5 / 4.5 |
| `0-5 lava` | `relight` / `pathtrace` | 7.45 / 7.39 | 134 / 135 | 117 / 100 | 8.42 / 8.60 | 8.8 / 13.2 | 0 / 0 | 5.98/3.45 vs 6.09/2.46 | 1.01/3.49 vs 1.04/3.83 | 0.81/1.39 vs 0.89/6.05 | 4.2 / 6.2 |
| `8-2 portal side 1` | `off` | 5.43 | 184 | 156 | 6.29 | 6.5 | 0 | — | 2.07 / 3.33 | 0.00 / 0.00 | — |
| `8-2 portal side 1` | `relight` | **16.96** | **59** | 47 | 21.13 | 21.1 | 58 | 6.85 / 3.35 | 4.02 / **12.90** | 2.08 / 11.01 | 11.2 |
| `8-2 portal side 1` | `pathtrace` | **14.49** | **69** | 57 | 17.66 | 17.7 | 3 | 6.78 / 2.16 | 3.79 / **10.66** | 2.14 / 3.32 | 13.4 |
| `8-3 b` | `off` | 9.14 | 109 | 84 | 11.24 | 12.4 | 0 | — | 2.69 / 6.42 | 0.00 / 0.00 | — |
| `8-3 b` | `relight` | **20.24** | **49** | 46 | 21.63 | 21.6 | 44 (100%) | 6.59 / 3.50 | 4.47 / **15.73** | 2.45 / 3.21 | **101.9** |
| `8-3 b` | `pathtrace` | **17.97** | **56** | **27** | 37.31 | 37.3 | 62 (97%) | 6.20 / 2.14 | 4.32 / **13.61** | 2.94 / 23.97 | **89.4** |

#### `perf-core -Floor` (CPU/Render-Thread Floor, `relight` at trace scale 0.2)
| Scenario (`relight` @ 0.2) | Avg ms (fps) | GPU `total` / `dispatch` | Whole-frame GPU | Whole-frame RT-CPU | Main `scripts` / `render` | Implied CPU/RT Floor vs `off` |
|---|---:|---:|---:|---:|---:|---|
| `warm-up` (0-1) | 4.43 (226) | 2.17 / 0.66 | 3.04 | **3.67** | 0.78 / 2.99 | +2.26 ms fixed overhead |
| `0-1 metal` | 5.07 (197) | 2.39 / 0.65 | 3.53 | **4.20** | 0.99 / 3.36 | +2.90 ms fixed overhead |
| `0-3 ghosts` | 6.89 (145) | 2.63 / 0.61 | 3.64 | **5.94** | 1.44 / 4.89 | **+4.29 ms** (includes `VeilTrace`!) |
| `0-2 run combat` | **8.44 (118)** | 2.33 / 0.69 | 4.11 | **7.16** | 2.98 / 5.12 | **+4.86 ms** (cannot break 118 fps even with 0.69 ms GPU trace!) |

### 1.2 Per-Pass GPU ms / Render-Thread CPU ms Breakdown (`pathtrace` & `relight`)

| Scenario (Mode) | Frame ms | `total` | `rtas` | `dispatch` | `cache` | `particles` | `volumetrics` | `vm-rays` (vm tot) | `rr` (`rr-eval`) | `vfx+late` | Unslotted in `total` (~`Veil`+`Refl`) | Whole `frame` GPU / RT-CPU |
|---|---:|---|---|---|---|---|---|---|---|---|---|---|
| `0-1 metal` (PT) | 8.02 | 6.74/2.79 | 0.16/0.24 | 3.45/0.37 | 0.20/0.45 | 0.14/0.35 | 0.35/0.27 | 0.17/0.34 (0.29/0.47) | 1.94/0.88 (1.27/0.73) | 0.33/0.07 | 0.17/0.11 | 7.81 / 6.31 |
| `0-3 ghosts` (PT) | 8.42 | 7.06/3.69 | 0.17/0.32 | 3.36/0.46 | 0.22/0.58 | 0.15/0.43 | 0.45/0.31 | 0.17/0.50 (0.30/0.62) | 1.92/0.90 (1.26/0.77) | 0.30/0.07 | **0.46/0.55** (`VeilTrace`!) | 8.10 / 7.56 |
| `0-2 combat` (PT) | 11.00 | 7.44/4.08 | 0.21/0.42 | 4.07/0.59 | 0.22/0.76 | 0.14/0.56 | 0.40/0.39 | **0.38/0.60** (0.63/0.73) | 1.93/1.09 (1.27/0.89) | 0.35/0.07 | 0.14/0.14 | 9.23 / 9.16 |
| `8-2 portal` (PT) | 14.49 | 6.78/5.43 | 0.27/0.31 | 2.16/0.79 | 0.35/0.98 | 0.19/0.73 | **0.93/0.43** | 0.21/0.68 (0.34/0.79) | 1.93/1.18 (1.24/0.92) | 0.32/0.06 | **0.59/0.92** (`VeilTrace`!) | 14.16 / **12.83** |
| `8-3 b` (PT) | 17.97 | 6.20/5.76 | **0.20/1.00** | 2.14/0.69 | 0.36/0.81 | 0.16/0.69 | 0.69/0.44 | 0.21/0.64 (0.34/0.76) | 1.90/1.22 (1.23/0.98) | 0.34/0.06 | **0.37/0.81** (`VeilTrace`!) | 17.30 / **16.14** |
| `8-3 combat` (PT) | 16.64 | 6.77/6.66 | 0.21/0.57 | 2.64/1.23 | **0.29/1.12** | **0.19/0.86** | 0.70/0.52 | **0.48/0.87** (0.75/1.00) | 2.06/1.31 (1.37/1.04) | 0.36/0.07 | **0.33/0.91** (`VeilTrace`!) | 15.15 / **14.39** |
| `8-3 combat` (RL) | 18.52 | 6.89/5.13 | 0.20/0.50 | 3.75/1.19 | 0.47/1.09 | 0.19/0.83 | 0.78/0.45 | 0.48/0.80 (0.74/0.92) | — (`denoise` 0.70/0.07) | 0.34/0.07 | **0.38/0.88** (`VeilTrace`!) | 17.66 / **15.83** |

> **Key observation on `VeilTrace`:** In `0-3` (`coneCover 1.50`), `8-2` (18 portal sides), and `8-3` (22 portal sides), `Plugin.Veils.cs:62–112` dispatches a **6th full-scene `DispatchRays`** (`VeilTrace.raytrace`, 1280×720) inside `SlotTotal` after `SlotBlit`, outside `SlotVolumetrics`. It accounts for **`~0.55–0.92 ms` of render-thread CPU** and **`~0.33–0.59 ms` of GPU** in those levels — invisible in per-slot tables because it has no GPU timer slot!

### 1.3 Scene Proxy & RTAS Record Census (`game.log`)

| Level / Scenario | Static Groups (level) | Active Static Inst / Recs | Active Dynamic Proxies | Rigid Updates/f | Skinned Copies/f (verts) | New Rigid Re-adds (New Mesh) | EffectGeo Inst | Portal Sides / Images | Veils |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `0-1 metal` | lvl 2 (`168` grp) | `77 / 113` | `113` | `11.2` | `12` (`11.9k`) | `105` (`74`) | `0–1` | `0 / 0` | `0` |
| `1-4 lamps` | lvl 0 (`236` grp) | `98 / 132` | `117` | `0.0` | `3` (`8.0k`) | `114` (`114`) | `0` | `0 / 0` | `0` |
| `0-3 ghosts` | lvl 0 (`169` grp) | `98 / 150` | `125` | `0.0` | `17` (`17.3k`) | `108` (`105`) | `0` | `0 / 0` | `20` (coneCover) |
| `6-2 start` | lvl 0 (`106` grp) | `88 / 168` | `74` | `19.7` | `3` (`8.6k`) | `72` (`67`) | `0` | `0 / 0` | `0` |
| `0-5 lava` | lvl 0 (`246` grp) | `180 / 222` | `101` | `5.0` | `7` (`10.4k`) | `94` (`84`) | `0` | `0 / 0` | `0` |
| `8-2 portal` | lvl 4 (`44` grp) | `~45 / 508 max` | `~250+` | low | — | — | `0` | `18 / 16` | `3` |
| `8-3 b` | lvl 4 (`54` grp) | `46 / 179` (`493 max`) | **`364`** | **`1.9`** | `12` (`22.2k`) | `369` (`249`) | **`20`** (63–67 m!) | `22 / 16` | `11` |
| `8-3 combat` | lvl 4 (`54` grp) | `46 / 179` (`493 max`) | **`604–624`** | `73–124` | `5–6` (`15.7k`) | **`2,306`** (**`385`**) | `6–21` | `22 / 16` | `11` |

### 1.4 Empirical DXIL Shader Complexity (`dxc.exe` 1.9.2602.17, `-T lib_6_3 -HV 2018 -O3`)

Compiled directly from `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/*.raytrace` with Unity's exact target & HLSL version:

| Shader :: Entry Point | DXIL Inst | `TraceRay` Sites | Tex Samples | Buffer Loads | `log2` | `exp2` | Trig (`sin/cos/atan`) | `alloca` Count | Est. Stack Bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **`PathTrace.raytrace::RayGenPTImg`** | **30,039** | **37** | **24** | **453** | **245** | **262** | **123** | **37** | **2,368 B** |
| **`PathTrace.raytrace::RayGenPT`** | **27,748** | **37** | **24** | **348** | **245** | **262** | **123** | **37** | **2,368 B** |
| `PathTrace.raytrace::RayGenImg` | 23,698 | 30 | 19 | 391 | 177 | 195 | 82 | 30 | 1,920 B |
| `PathTrace.raytrace::RayGen` | 21,865 | 30 | 19 | 307 | 177 | 195 | 82 | 30 | 1,920 B |
| `Viewmodel.raytrace::RayGen` | **14,281** | **13** | 9 | 266 | 130 | 133 | 32 | 13 | 832 B |
| `CacheUpdate.raytrace::UpdateRayGen` | **13,754** | **15** | 1 | 250 | 112 | 113 | 27 | 9 | 432 B |
| `Volumetrics.raytrace::VolumeRayGen` | 3,085 | 5 | 1 | 58 | 15 | 20 | 18 | 5 | 320 B |
| `ParticleLight.raytrace::ParticleLightRayGen` | 2,403 | 2 | 1 | 46 | 23 | 25 | 8 | 2 | 128 B |
| `VeilTrace.raytrace::RayGen` | 459 | 3 | 0 | 5 | 0 | 4 | 0 | 3 | 192 B |
| `CutoutTrace.raytrace::RayGen` | 173 | 1 | 1 | 0 | 0 | 0 | 0 | 1 | 64 B |

### 1.5 Empirical Unity 2022.3.29f1 + DXR 1.1 `RayQuery` Verification
- **Managed Unity API (`UnityEngine.CoreModule.dll` reflection):**
  - `ComputeShader`: exposes ONLY `Dispatch` / `DispatchIndirect` / keywords — **NO `SetRayTracingAccelerationStructure`**.
  - `CommandBuffer`: exposes `SetRayTracingAccelerationStructure(RayTracingShader, ...)` ONLY — **NO compute or global RTAS binding**.
  - `SystemInfo`: exposes `supportsRayTracing`, **NO `supportsInlineRayTracing`** (added in Unity 2023.1 / Unity 6).
  - Unity bundled compiler (`Editor/Data/Tools/dxcompiler.dll`): **version `1.6.2106.3`** (supports DXIL 1.6 / SM 6.6 at the compiler level, so DXC itself supports `cs_6_5` `RayQuery`, but Unity's C# runtime will not bind a TLAS SRV to a ComputeShader!).
- **Windows SDK `dxc.exe` (`1.9.2602.17`):** Compiled a standalone `cs_6_5` `RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE>` compute shader with **exit code 0**.
- **Architectural conclusion:** Inline `RayQuery` cannot be invoked through Unity 2022.3's `ComputeShader` API, **but CAN be invoked from `UltraRT.Native.dll`**, which already has `IUnityGraphicsD3D12v7` (`GetDevice`, `GetCommandQueue`, `CommandRecordingState` -> live `ID3D12GraphicsCommandList*`) and registers render events (`GpuTimer.cpp`, `Dlss.cpp`, `Hdr.cpp`).

### 1.6 Bottleneck Attribution Matrix & Hitch Anatomy (`hitches.py`)

| Bottleneck Source | Calm (`0-1`/`0-3`) | Combat (`0-2`) | Portals (`8-2`/`8-3 b`) | Heavy Combat (`8-3 combat`) |
|---|---|---|---|---|
| **Render-thread SBT rebuilds** (`5–6` dispatches) | `1.5–2.8 ms` | `2.9–3.3 ms` | **`4.1–4.5 ms`** | **`5.5–5.9 ms`** |
| **Render-thread `DlssRR` (`OwnCommandList` flush)** | `0.8–0.9 ms` | `1.0–1.1 ms` | `1.2 ms` | `1.3 ms` |
| **Render-thread job-worker & portal-cam contention** | `1.5–2.2 ms` | `2.3–3.5 ms` | **`6.0–9.0 ms`** | **`5.5–7.0 ms`** |
| **GPU `PathTrace` dispatch** | **`3.3–3.5 ms`** | **`4.07 ms`** | `2.14–2.16 ms` | `2.64 ms` |
| **GPU `DlssRR` (`Prepare+Eval+Finish`)** | `1.9–2.1 ms` | `1.93 ms` | `1.90–1.93 ms` | `2.06 ms` |
| **GPU Auxiliary (`Volumetrics` + `Viewmodel` + `Cache` + `Particles` + `Veils` + `Vfx`)** | `1.3–1.8 ms` | `1.7–1.8 ms` | **`2.5–2.8 ms`** | **`2.6–2.9 ms`** |
| **Main-thread Plugin CPU (`Trace` + `Viewmodel`)** | `0.9–1.3 ms` | `1.8 ms` | `2.3–3.1 ms` | **`3.1 ms`** |
| **Main-thread Hitches (>16.7 ms)** | 5 s `BakedLightMask` (`6–9 ms`), rare `312 ms` render stall | `scripts` (`+5 ms`), `render` (`+4.6 ms`), 5 s `lights` (`+8.9 ms`) | `lights` (`+9–14.8 ms`), `trace` (`+21 ms`), mode-switch `281–293 ms` | `scripts` (`520` frames), `render` (`168` frames + **4× `217–337 ms`**), `lights` (**`40` frames, max `14.5 ms`**), `emissive` (`17` frames, max `4.3 ms`), `KeepOrbGlows` (**`37.2 ms`**) |

---

## Bucket 1 — Pure Performance Wins (Zero Visual Loss)

Every item in Bucket 1 produces an **identical or strictly cleaner image** while recovering frame time or eliminating hitches.

### B1.1 — Eliminate the 5-Second `BakedLightMask` Full-Scene Scan Bug (`Plugin.Noise.cs:65–78`)
- **Measured cost:** **`6.2–14.8 ms` main-thread hitch every 5.00 seconds in EVERY level** (`warm-up 6.4 ms`, `0-1 8.9 ms`, `1-4 6.2 ms`, `0-3 8.3 ms`, `6-2 8.3 ms`, `0-5 5.3 ms`, `8-2 9.4 ms`, `8-3 14.8 ms`; **40–62 hitches** in `8-3 combat`).
- **Root cause:** `BakedLightMask(Light l)` runs inside `GatherLights()` and executes `FindObjectsOfType<StaticSceneOptimizer>()` every 5 seconds forever, scanning every `MonoBehaviour` in the scene. `StaticSceneOptimizer` is baked at level load time and never spawns dynamically.
- **Implementation:** Scan `StaticSceneOptimizer` **once** when `_proxies` changes (`!ReferenceEquals(_globalLightsOwner, _proxies)`), and remove the 5-second timer (optionally re-scan only if a checkpoint room clone contains one, via `RegisterRoot` in `StaticGroups.cs:857`).
- **Estimated win:** Eliminates 100% of periodic `6–15 ms` `lights` hitches across the entire game (`p99` and `1% low` jump dramatically).
- **Complexity / Risk:** Trivial (5 lines) / Zero risk.

### B1.2 — Eliminate Remaining `FindObjectsOfType` Main-Thread Hitch Scans (`LitParticles.cs`, `Plugin.LightOverrides.cs`)
- **Measured cost:**
  - `LitParticles.KeepOrbGlows` (`LitParticles.cs:252–288`): **`37.22 ms` stall in `8-3`**, **`11.48 ms` in `0-2`** (`FindObjectsOfType<MeshRenderer>(true)` across 27,000+ renderers!).
  - `LitParticles.Scan()` (`LitParticles.cs:182–227`, every 5 s): **`2.1–2.9 ms` stall** (`Resources.FindObjectsOfTypeAll<Material>()` + `FindObjectsOfType<ParticleSystemRenderer>()`).
  - `Plugin.LightOverrides.cs:247–269` (`ResolveAddBinds`): runs `FindObjectsOfType<Renderer>(true)` every 2.0 s forever whenever any `"bind"` entry in a level JSON is missing/unloaded (`7-4`).
- **Implementation:**
  1. Pass `SceneProxies`'s already-scanned `MeshRenderer` list (from `Build()`) into `KeepOrbGlows` instead of calling `FindObjectsOfType<MeshRenderer>(true)`.
  2. Feed newly spawned `ParticleSystemRenderer`s and `Material`s from `Patches.PendingSpawns` / `EffectSpawns` incrementally instead of polling `FindObjectsOfTypeAll<Material>` + `FindObjectsOfType<ParticleSystemRenderer>` every 5 s.
  3. Resolve `ResolveAddBinds()` from `SceneProxies`'s build list + `RegisterRoot` clones (exponential backoff or stop after 1 retry on miss).
- **Estimated win:** Eliminates **`37 ms` orb-glow hitches** and **`2–14 ms` periodic `scripts`/`lights` hitches**.
- **Complexity / Risk:** Low / Very low.

### B1.3 — Move `VeilTrace` to Its Own Tiny RTAS (Like `CutoutRtas`) or Fold Into `PathTrace`
- **Measured cost:** **`~0.55–0.92 ms` RT-CPU** + **`~0.33–0.59 ms` GPU** in `0-3`, `8-2`, and `8-3`.
- **Root cause:** `Plugin.Veils.cs:86,103` binds the **entire 500–800-instance scene RTAS** (`_proxies.Rtas`) to `VeilTrace.raytrace` (`1280×720`) just to intersect **`3–20` veil proxies** (`3` in `8-2`, `11` in `8-3`, `20` in `0-3`) and `0–22` portal quads! Unity rebuilds the entire scene's 500–800 SBT records solely for this pass.
- **Implementation:**
  - Notice `VeilTrace.raytrace` needs: (a) the primary ray's opaque hit distance (ALREADY in `_gNormalDepth`!), (b) portal hops (max `22` portal quads), and (c) `3–20` veil proxies!
  - Build a dedicated `VeilRtas` containing ONLY the `3–20` veil proxies + `0–22` portal quads (`<= 42` total instances!), passing `_gNormalDepth` (or game depth) as `TMax`! Exactly the same pattern `CutoutRtas` (`3–15` instances, `<0.04 ms` RT-CPU) already uses!
- **Estimated win:** **`-0.50 to -0.88 ms` RT-CPU** AND **`-0.15 to -0.30 ms` GPU** (tiny BVH traversal!) in `0-3`, `8-2`, `8-3`, and `8-3 combat` (`+4–6 fps` in `8-2`/`8-3` alone!).
- **Complexity / Risk:** Low–Medium (~100 lines, mirrors `SceneProxies.CutoutPass.cs`) / Very low.

### B1.4 — C4: Merge Non-Moving Dynamic Proxies Into Static Groups (SBT Record Collapse)
- **Measured data:**
  - `8-3 b`: `46` static instances (`179` records) + **`364` active dynamic proxies** (~`400+` SBT records) — only **`1.9` rigid updates/frame**!
  - `8-3 combat`: `46` static instances (`179` records) + **`604–624` active dynamic proxies** — ~`480` are stationary props/furniture/static doors/unbatched room meshes!
  - `0-2 combat` / `0-3` / `1-4`: `113–125` active dynamic proxies, `0–11` moving/frame.
  - Because every `DispatchRays` rebuilds hit-group records linearly in total active records (`~1.5–2.2 µs` per record × `5–6` dispatches = `~10 µs` RT-CPU per record per frame!), 350 redundant dynamic records cost **`~3.0–3.5 ms/frame` of RT-CPU** in `8-3`!
- **Implementation:**
  - At `SceneProxies.Build()`, identify non-skinned `MeshRenderer`s not in a Unity static batch that have NO `Rigidbody`, NO `Animator`/`Animation`, NO `EnemyIdentifier`/`Door`/`MovingPlatform` ancestor, and are not displaced/scrolling/portal-tracked.
  - Bake their world-space geometry into the same `StaticGroups` per-room/per-material merged meshes (with watcher-based activation on their toggle ancestor), and keep an "unmerge/promote to dynamic" fallback if a transform ever changes (`transform.hasChanged` check on a slow poll or `StaticGroups` watcher).
  - Alternatively (simpler first step): merge **read-only static-tagged or no-Rigidbody props** per room into `StaticGroups` at level load.
- **Estimated win:** Eliminates **250–380 SBT records** in `8-2`/`8-3` and **70–100** in `0-1`/`0-3`/`0-2` -> **`-1.5 to -2.8 ms` RT-CPU** in `8-2`/`8-3` (`+10 to +18 fps` in `8-3`!) and **`-0.5 to -0.9 ms` RT-CPU** in `0-1`/`0-2`/`0-3`.
- **Complexity / Risk:** Medium (2–3 days, reuses existing `StaticGroups` machinery) / Low–Medium (must handle rare props moved by scripts via fallback promotion).

### B1.5 — Native D3D12 Inline `RayQuery` (`cs_6_5`) in `UltraRT.Native.dll` (Bypassing Unity SBT Rebuilds Completely)
- **Measured cost:** In `8-3 combat`, the 3 visibility/auxiliary `DispatchRays` (`Volumetrics` `0.52 ms`, `ParticleLight` `0.86 ms`, `VeilTrace` `~0.90 ms`) + `CacheUpdate` (`1.12 ms`) + `Viewmodel` (`0.87 ms`) consume **`4.27 ms` of render-thread CPU** purely in Unity's `SetInstanceShaders` / `WriteHitGroupShaderRecords` + 11-worker `SetHitGroupShadersJob` contention.
- **Empirical feasibility:**
  - `UltraRT.Native.dll` already acquires `ID3D12Device*` and live `ID3D12GraphicsCommandList*` on Unity's render thread (`GpuTimer.cpp:221–231`).
  - **How to get Unity's TLAS GPU Virtual Address cleanly:** On `ID3D12GraphicsCommandList4` (queried from Unity's active command list once at init), hook `BuildRaytracingAccelerationStructure` (vtable slot on `ID3D12GraphicsCommandList4`, or hook `DispatchRays` to capture the TLAS descriptor/GPUVA bound by `PathTrace`). Whenever Unity builds `_proxies.Rtas`, `pDesc->DestAccelerationStructureData` gives the exact `D3D12_GPU_VIRTUAL_ADDRESS` of Unity's live TLAS for that frame! (No custom BLAS/TLAS maintenance required — Unity keeps building the acceleration structure!)
  - Dispatch pre-compiled `cs_6_5` `RayQuery` compute shaders directly on Unity's command list via `IssuePluginEventAndData` (with a lightweight root signature binding the TLAS SRV at `t0` + Unity's existing `RenderTexture`/`ComputeBuffer` `GetNativeTexturePtr()`/`GetNativeBufferPtr()` resources).
- **Best Phase-1 targets for native `RayQuery` (zero hit-shader materials needed!):**
  1. **`Volumetrics` shadow rays** (`SKIP_CLOSEST_HIT_SHADER`, opaque geometry + optional alpha-test or conservative opaque): eliminates `0.24–0.52 ms` RT-CPU immediately!
  2. **`ParticleLight` direct shadow ray** (and pair with `RadianceCache` lookup for indirect, removing `ParticleLight.raytrace` entirely!): eliminates **`0.32–0.86 ms` RT-CPU**!
  3. **`CutoutTrace` / `VeilTrace`** (if not moved to mini-RTAS): eliminates another `0.5–0.9 ms` RT-CPU.
- **Estimated win:** **`-1.2 to -2.4 ms` RT-CPU** in `8-2`/`8-3` (`-0.6 to -1.1 ms` in `0-1`/`0-2`) + eliminates 11-thread job-worker starvation of Unity's main culling/rendering jobs (which currently adds another `+1.5–2.5 ms` indirect render-thread slowdown in `8-3`!).
- **Complexity / Risk:** High (native D3D12 interop, resource barrier tracking with Unity, alpha-test handling for non-opaque shadow casters requires a small per-instance material/UV table or `RAY_FLAG_FORCE_OPAQUE` for volumetrics/particles). Still **the single highest-ceiling architectural win in the entire project**.

### B1.6 — `Volumetrics.raytrace`: Single-Pass Weighted Reservoir Sampling (`-50%` Light Loop ALU)
- **Measured cost:** `Volumetrics` GPU is **`0.93 ms` in `8-2`**, **`0.69–0.78 ms` in `8-3`**, **`0.43–0.47 ms` in `0-3`/`6-2`/`0-5`**.
- **Root cause (`Volumetrics.raytrace:512–534`):** When `count <= 128` (almost every level, including `8-3` with `110–124` lights!), `VolumeRayGen` runs TWO full loops over all `count` lights:
  - Pass 1 (lines 516–519): calls `LightTarget(i, o, dir, tEnd)` for all `124` lights to compute `total`.
  - Pass 2 (lines 523–527): calls `LightTarget(i, o, dir, tEnd)` **a SECOND time** for every light up to the chosen index (average 62–124 more calls)!
  - Every `LightTarget` call executes `LightSegment` + `ClipSpot` (quadratic solver) + **`2 × atan()`** (`18` trig ops in DXIL). That's **`~180–250 LightTarget` calls and `~360–500 atan()` calls per low-res pixel**!
- **Implementation:** Replace the 2-pass CDF inversion with **1-pass Weighted Reservoir Sampling (WRS)** (`wSum += w; if (Rand(rng) * wSum < w) chosen = i;`), or keep 2 stratified reservoirs in 1 pass when `nL == 2` (`nL = 1` is the default when `count > 1`? Check `_VolSamples.y` — default `nL = 1`!). For `nL = 1`, 1-pass WRS produces the **exact identical probability distribution** (`P(i) = w_i / sum(w)`) in half the evaluations, AND avoids recomputing `LightSegment` + `atan` a 3rd time inside `EvalLight` by caching `(a, b, s, d2)` of the winning reservoir!
- **Estimated win:** **`-0.25 to -0.40 ms` GPU** in `8-2`/`8-3` and **`-0.12 to -0.20 ms` GPU** in `0-3`/`6-2`/`0-5`/`0-2`.
- **Complexity / Risk:** Trivial (~25 lines in `Volumetrics.raytrace`) / Zero visual change.

### B1.7 — `PathTrace` Shader Refactoring: Cut 30,039 DXIL Inst & 250 `pow()` Calls (`-0.5 to -1.1 ms` GPU)
Our DXC 1.9 disassembly exposed **four massive zero-visual-loss GPU wins** inside `PathTrace.raytrace`, `UltraRTPathTraceFull.hlsl`, and `UltraRTLights.hlsl`:

1. **Range Early-Out BEFORE `pow()` in `UltraRTLights.hlsl::LightEvalRaw` (lines 568–598):**
   - Currently, for EVERY point/spot light RIS candidate (`16` primary + `8` bounce = `24` candidates/px), `LightEvalRaw` computes `game`, `ToLight(game)` (`pow`), `phys`, `ToLight(3*game + 0.05)` (`pow`), `Spot()` (`pow`), and `ToLight(l1.rgb)` (`pow`) **BEFORE** checking `if (atten < 2.5e-7) return false;`!
   - Out-of-range lights (`d >= 2.0 * r` where `game == 0` and `reach == 0`, OR `d2` too far) execute **3–4 `pow()` (`6–8` transcendental `log2`/`exp2`) ops** for nothing!
   - **Fix:** Add early-outs for backface (`NdotL <= 0`) and range/spot cone (`dot(-L, dir) < cosHalf` BEFORE `pow(spot, 2.2)`!), AND **pre-linearize `_Lights[i*4+1].rgb` on the CPU in `GatherLights()`** when `LinearActive` is true (eliminating `ToLight(l1.rgb * scale)` inside every single RIS candidate across all shaders!).
   - **Estimated win:** **`-0.25 to -0.45 ms` GPU** on `PathTrace` (`dispatch`), plus faster `Viewmodel` and `CacheUpdate`.
2. **Compile Out `_LightSampling <= 0` Brute-Force Reference Branches (`UltraRTLights.hlsl:929–945, 1000–1007`):**
   - Every single raygen entry point (`RayGen`, `RayGenPT`, `RayGenImg`, `RayGenPTImg`, `Viewmodel::RayGen`, `CacheUpdate::UpdateRayGen`) currently compiles the entire brute-force `for (i = 0; i < _LightCount; i++) LightVisible(...)` + `EmissiveDirect(...)` fallback path directly inline, adding **`~8–10` dead `TraceRay` sites** and thousands of DXIL instructions that inflate I-cache footprint, VGPR/stack spills (`2,368 B`), and PSO compile time (`16–33 s`).
   - **Fix:** Gate the reference fallback under a separate entry point (`RayGenRef`) or `#ifdef ULTRART_REF_LIGHTS` compile variant (only compiled/used for `-ultrart-ls-mode reference`).
   - **Estimated win:** **`-0.15 to -0.35 ms` GPU** (lower VGPR pressure / higher wave occupancy on Blackwell SM) + **30–40% faster PSO warm-up**.
3. **Deduplicate `PtPath` & `TraceSceneCone` Inlining (`UltraRTPathTraceFull.hlsl` & `PathTrace.raytrace`):**
   - `PtPath` is currently called twice (once from `PtIndirect`, once from `PtReflection`), and `TraceSceneCone` (with the 3-hop portal loop!) is inlined into 5+ call sites -> **37 total `TraceRay` sites** in `RayGenPT`.
   - **Fix:** Run `PtPath` as a single unified state-machine loop (`for (int lobe = 0; lobe < 2; lobe++)` or shared trace helper with `[loop]`), cutting `TraceRay` sites from **37 -> ~10–14** and halving stack bytes (`2,368 B -> ~1,000 B`).
   - **Estimated win:** **`-0.15 to -0.35 ms` GPU** on `PathTrace`.
4. **Eliminate `_gLitWeight` (`ARGBInt`, 128-bit/px!) UAV & Pack 8-bit `litWeight` Into `_gInstanceId` or `_gSurface`:**
   - `Plugin.cs:1020`: `_gLitWeight` is a **16-byte-per-pixel `ARGBInt` UAV** storing ONLY `w & 0xFF` (`8` bits — `120` bits completely wasted!).
   - Pack that 1 byte into `_gNormalDepth` / `_gSurface` / `_gInstanceId` (or at worst `R8`), saving `~14 MB/frame` G-buffer write bandwidth AND freeing a **2nd UAV slot** out of Unity's 8-UAV limit (enabling Bucket 3's diffuse hit-distance guide for DLSS RR!).
   - **Estimated win:** **`-0.05 to -0.10 ms` GPU** + unlocks UAV budget.

### B1.8 — Optimize DLSS Ray Reconstruction Overhead (`rr`: `1.93–2.06 ms` GPU / `0.88–1.31 ms` RT-CPU)
- **Measured breakdown:**
  - `rr-eval` (native NGX evaluate): `1.24–1.40 ms` GPU / **`0.67–1.04 ms` RT-CPU**!
  - `rr` minus `rr-eval` (`Demod` + `_demodUp` + `Prepare` + `Finish` + optional `Cutout`): **`0.65–0.70 ms` GPU / `0.15–0.27 ms` RT-CPU**.
- **Concrete wins:**
  1. **Benchmark & fix `OwnCommandList` mid-frame D3D12 flush (`Dlss.cpp:458–504`, `Plugin.Dlss.cs:24`):** Default `_rrOwnList = true` uses `kUnityD3D12EventConfigFlag_FlushCommandBuffers`, forcing Unity to close, submit, and stall/split its active D3D12 command list mid-frame, then submit a second list via `ExecuteCommandList`. In `8-3 combat`, `rr-eval` RT-CPU balloons to **`1.04 ms`** (up from `0.66 ms` in `0-1`)! Re-evaluating `UnityList` (`-ultrart-rr-unitylist`, recording NGX directly onto Unity's active command list with explicit resource-state restoration) or removing the `FlushCommandBuffers` sync overhead can recover **`0.3–0.6 ms` RT-CPU**!
  2. **Optimize `RayReconstruction.compute::Finish` (runs at full 2560×1440 = `3.69M` pixels!):**
     - Currently calls `Dir(k)` (full 4×4 matrix multiply + `normalize`) **9 times per screen pixel** in the 3×3 match loop (`33M` matrix multiplies/frame!), plus 9 taps of `_Emission` in `EmitShare` even when no emitters are near!
     - **Fix:** Replace `Dir(k)` inside the 3×3 loop with linear ray-basis interpolation (`camFwd + rayX * ndc.x + rayY * ndc.y`, just like `Vfx.shader:168`!), and track a 1-bit per-tile or `_NormalDepth`/flag hint so `EmitShare` skips the 9-tap `_Emission` loop on 95% of non-emissive screen pixels. Also eliminate the duplicate `i.viewProj.inverse` calls on `DlssRR.cs:401,427`.
     - **Estimated win:** **`-0.15 to -0.25 ms` GPU** on `Finish` at 1440p.
  3. **Bypass `ReflectionFilter.Temporal` + `Spatial` under DLSS RR (Zero-Cost / Quality-Neutral Verification):**
     - Currently `Plugin.cs:1587–1598` runs `ReflectionFilter.Execute` (`Temporal` 9-tap + 2× bilinear history + `Spatial` 25-tap a-trous + `Resolve`, allocating 5 RenderTextures) **every frame even in `pathtrace` with DLSS RR!** (This also runs outside `SlotDenoise`, inflating unslotted `total` GPU/RT-CPU by `~0.15–0.25 ms`.)
     - Under RR, feeding raw `_gReflection` directly into `Prepare` (computing `albedo`/`emission`/`specW`/`keep` inline from `_gSurface`, or running `Resolve` only without `Temporal`/`Spatial`) saves **`~0.15–0.25 ms` GPU**, 5 trace-res RenderTextures, and gives RR temporally uncorrelated specular input (as NVIDIA's RR spec recommends!). Verify A/B on `0-1 metal` and `8-2` water before switching default.

### B1.9 — Dirty-Gate `BindPortalBuffer()`, Fix Dynamic Vertex Buffers, & Eliminate `8-3 b`'s `100 KB/f` Heap Alloc
- **`SceneProxies.Portals.cs:551–577` (`BindPortalBuffer`):** Currently runs `2 × Matrix4x4.inverse` per portal side (`44` matrix inversions/frame in `8-3`!) and uploads 2 `ComputeBuffer.SetData` buffers every single frame even when `_portalRowsDirty == false`. Gate on `_portalRowsDirty` (and cache the inverse per side). Saves `~0.08–0.15 ms` main-thread CPU + eliminates redundant GPU buffer copies in `8-2`/`8-3`.
- **`BufferD3D12::InitializeResource` (`0.23 ms/frame` ETW in `docs/performance.md`) & `8-3 b` `89–102 KB/f` managed allocs:**
  - Call `e.Mesh.MarkDynamic()` in `EffectGeometry.Create()` (`EffectGeometry.cs:326`) and cache `Sprite` vertex/UV/triangle arrays per `Sprite` reference (`EffectGeometry.cs:300–302` calls `sp.vertices`, `sp.uv`, `sp.triangles` which each allocate a fresh managed array on every animation frame of animated `FireSprite` candles — 20 animated fire sprites in `8-3 b`!).
  - Audit remaining `31–33 KB/f` allocs in `8-3 combat` (`Plugin.cs` `List`/`LINQ` in `GatherLights`/`Dynamic` sync, `SceneProxies.Portals` logging/collections).
- **Estimated win:** **`-0.25 to -0.45 ms` CPU/RT-CPU** + fewer Gen-0 GC collections (`5 -> 0–1` in `8-3 combat`).

### B1.10 — Diagnose & Eliminate the `200–337 ms` Mid-Run `render` Stalls
- **Measured:** `0-1 metal` (`311.8 ms` at `t=3.25 s`), `0-2` (`214.7 ms`), `8-3 combat pathtrace` (**4 stalls: `336.9`, `328.4`, `306.2`, `217.5 ms`** at `t=2.49, 2.71, 5.48, 5.88 s`), `8-3 combat relight` (`201.8`, `178.5`, `217.2 ms`), `8-3 b` mode switches (`281–293 ms`).
- **Diagnostic signature:** Notice in `frames_*.csv` that on every single `200–337 ms` frame, `trace_ms` is only `1–7 ms`, `gc0 == 0`, and **`render_ms` is `193–323 ms`** — meaning the stall occurs inside Unity's native `Render` / D3D12 command submission (`WaitForGfxThread` waiting on a render-thread stall during RTAS BLAS/TLAS build, shader/PSO variant compilation when a new enemy/effect material or `EffectProxy` / `LavaProxy` / `RayGenImg` hits the RTAS for the first time, or `DynamicGeometry` BLAS allocation).
- **Mitigation plan:**
  1. Pre-warm all proxy shader passes (`UltraRTHit`, `UltraRTShadow`, `EffectProxy`, `LavaProxy`, `PortalProxy`, `VeilProxy`, `CutoutTrace`) on a 1×1 dummy dispatch at startup / level load (not only `RayGenPT` and `RayGenPTImg`!).
  2. Stop creating brand-new `Material` instances per `EffectGeometry` entry (`EffectGeometry.cs:332` calls `new Material(_shader)` on first sight of each glowing effect — each new material triggers Unity's raytracing shader-table & PSO validation on the render thread!). Pool a fixed set of `EffectGeometry` materials with `MaterialPropertyBlock`s instead!
  3. Capture one targeted ETW / `RenderThreadMs` per-slot log on `8-3 combat` `t=2–6 s` to confirm which slot (`rtas` vs `dispatch` vs `cache` vs Unity's own render pass) absorbs the 300 ms spike.
- **Estimated win:** Eliminates the catastrophic **`200–337 ms` freezes** that currently drag `8-3 combat` `1% low` down to **`12 fps`** (pushing `1% low` from `12 -> 35–45+ fps`!).

---

## Bucket 2 — Situational Wins (Combat, Explosions, Portals, Heavy Levels)

### B2.1 — Gate `ProcessReAdds()` on Truly New RTAS Meshes (`SceneProxies.cs`)
- **Measured:** In `8-3 combat`, `ProcessReAdds()` removed and re-added **`2,306` new rigid instances**, of which only **`385` (`16.7%`)** actually had a mesh new to the RTAS (`1,921` = **`83.3%` were unnecessary!**).
- **Implementation:** Only enqueue `QueueReAdd` when `!_meshesInRtas.Contains(mesh)` (recording meshes once they survive 2 frames in the RTAS, and verifying visually on 8-3 chairs/debris).
- **Estimated win:** Eliminates 83% of combat RTAS remove/add churn -> **`-0.15 to -0.35 ms` main/RT-CPU** during combat bursts and smoother TLAS builds.
- **Complexity / Risk:** Trivial (10 lines) / Low (test against the original `HotelChair` invisible-mesh bug in `docs/scene-proxies.md`).

### B2.2 — Separate Static vs Dynamic Emissive Alias Tables (`EmissiveLights.cs`)
- **Measured:** In `8-3 combat`, `EmissiveLights` performed **`107` full alias-table rebuilds (`70.7 ms` per 600 frames)**, spiking up to **`4.25 ms` on a single frame** and causing **`17` >16.7 ms hitches** (`144` in `relight`).
- **Root cause:** Every time a single dynamic combat emitter toggles visibility, `UpdateActive()` loops over all ~20,000 static+dynamic triangles and rebuilds the entire Vose alias table.
- **Implementation:** Split `_emissive` into (a) a **static level alias table** (rebuilt ONLY on room watcher flips, almost never during arena fights) and (b) a tiny **dynamic/combat emitter table** (`<500` triangles, rebuilt in `<0.03 ms`!), choosing between the two in `RisCandidates` by total flux ratio (just like light vs emissive `share`). Also check visibility via watcher events instead of calling `e.Src.Visible()` on all 618 emitters every frame (`61.6 ms` / 600 frames in `8-3 combat`!).
- **Estimated win:** Cuts `emissive` CPU from **`0.51 ms avg / 4.25 ms worst`** down to **`~0.08 ms avg / <0.3 ms worst`** in `8-3 combat`!
- **Complexity / Risk:** Medium / Zero visual loss.

### B2.3 — `EffectGeometry` Combat & Distant Candle Culling + Static Sprite Batching (`EffectGeometry.cs`)
- **Measured:** In `8-3 b` (calm!), **`20` animated candle `FireSprite` instances `63–67 m` away** are active in the RTAS (`0.314 ms/frame` CPU + 20 `DynamicGeometry` BLAS/SBT entries!). In `8-3 combat`, up to `21` active baked particle/line/trail meshes rebuild BLASes every frame.
- **Implementation:**
  1. Tighten distance/importance culling: ignore small `<1.2 m` sprites beyond `30–35 m` (a 0.5 m candle 65 m away is sub-pixel in reflections!).
  2. Batch stationary level `FireSprite`s into a single pooled mesh (or static emissive triangles) instead of 20 separate RTAS instances.
  3. Throttle `BakeMesh` updates on effects >`20 m` away to 30 Hz (alternating odd/even entries) and cap combat active `EffectGeometry` proxies to `12` (down from `48`) sorted by `(size * brightness / dist^2)`.
- **Estimated win:** **`-0.25 to -0.45 ms` CPU + RT-CPU** in `8-3 b` and `8-3 combat`, plus fewer `DynamicGeometry` BLAS rebuilds (`rtas` RT-CPU in `8-3 b` is currently **`1.00 ms`**!).

### B2.4 — Portal-Heavy Levels (`8-2`, `8-3`): Room Culling, Portal-Scoped `RayGenImg`, & Portal-Camera Budget
- **Measured:** `8-2` (`14.49 ms` PT / `16.96 ms` RL) and `8-3 b` (`17.97 ms` PT / `20.24 ms` RL) suffer a **+8.8 to +11.5 ms delta over `off`**, with **`12.8–18.0 ms` whole-frame RT-CPU** and `RayGenPTImg` carrying `+2,291` extra DXIL instructions & `+105` extra buffer loads on every pixel of the screen.
- **Concrete wins:**
  1. **Portal-aware culling of static groups & dynamic proxies:** In `8-2` (`508` static records across 44 groups) and `8-3` (`493` records), groups farther than `120 m` from BOTH the camera AND every active portal exit (and not in the active arena) can be removed from the RTAS until approached.
  2. **Clamp `PortalHops` on secondary rays (`2` for primary, `1` for bounce/reflection, `0` for cache update):** Secondary rays rarely contribute visibly after 2–3 portal teleports, yet `PathTrace` loops `_PortalHops` (`3`) inside every bounce and reflection ray.
  3. **Gate portal-image shadow evaluation per pixel:** In `RayGenPTImg`, only evaluate portal-image lights (`l3.w < 0`) in RIS when the hit point is within `range` of at least one portal exit (cheap 1-float AABB/sphere check against a CPU-uploaded bounding sphere of active portal exits!).
  4. **Dynamic Proxy Gib/Debris Culling in Combat (`SceneProxies.cs`):** Skip adding tiny short-lived `ExplosionChunk` / gib / shell-casing `MeshRenderer`s (`bounds.size.magnitude < 0.25 m` and `>15 m` away, or cap simultaneous gib proxies at `40`). In `8-3 combat`, dynamic proxies jump from `364 -> 624` (`+260` instances!), adding **`~2 ms` of SBT rebuild overhead** across 6 dispatches!
- **Estimated win:** **`-1.2 to -2.5 ms` RT-CPU + `-0.3 to -0.5 ms` GPU** in `8-2`, `8-3 b`, and `8-3 combat`.

### B2.5 — `Viewmodel` Combat Regression Fix (`0.48 ms GPU / 0.87 ms RT-CPU` in `8-3 combat` vs `0.17 / 0.34 ms` in `0-1`)
- **Measured:** During combat (`0-2` and `8-3 combat`), `vm-rays` GPU **triples** (`0.17 -> 0.42–0.48 ms`) and `vm-rays` RT-CPU jumps to **`0.87 ms`**!
- **Root cause:**
  1. In `Plugin.Viewmodel.cs:426–435`, whenever weapon recoil/punch animation touches the edge of `_vmGpuDispatch`, `ViewmodelRect` falls back to the loose skinned-arm CPU bounds (often **the entire 1440p screen!**), tripling traced viewmodel pixels!
  2. `Plugin.Viewmodel.cs:41` sets `_vmCandidates = 32` (2× world `PathTrace`'s `16`!), and `Viewmodel::RayGen` is **14,281 DXIL inst / 13 `TraceRay` sites**!
- **Implementation:**
  - Use a persistent padded bounding box around the weapon corner (or clip loose arm bounds to the lower-75% / weapon-side screen half rather than falling back to 100% fullscreen) + run a tiny compute/depth-prepass tile mask (`8×8` tiles) so `Viewmodel.raytrace` only shades occupied gun tiles.
  - Lower `_vmCandidates` from `32 -> 16` (or `12` with Bucket 3's world-space light grid), and strip `_LightSampling <= 0` from `Viewmodel.raytrace`.
- **Estimated win:** **`-0.25 to -0.32 ms` GPU** in `0-2` and `8-3 combat` (`vm-rays` back down to `~0.16–0.20 ms`).

### B2.6 — `WetBlood` Half-Res Mask & Skip-When-Empty (`BloodWet.cs`)
- **Measured / Architectural:** Draws up to `500+` instanced decals into a full-res `2560×1440` `R8` RT and rebinds 3 MRTs every frame in combat.
- **Implementation:** Render blood wetness at **trace resolution** (not full 1440p screen resolution — `PathTrace` only ever samples it at trace resolution anyway!) — **9× fewer rasterized pixels** in `pathtrace`! And skip the pass entirely when 0 fresh stains intersect the camera frustum.
- **Estimated win:** **`-0.10 to -0.25 ms` GPU** during bloody arena fights, with zero visual loss (traced at trace res regardless!).

### B2.7 — Adaptive Load Shedding During Multi-Explosion / CPU Spikes
- **Mechanism:** Track an EMA of whole-frame RT-CPU (`GpuTimer` `SlotFrame` CPU ms) and main-thread CPU (`Perf`). When smoothed frame time exceeds a target budget (e.g., `>11 ms` for 90 fps or `>14 ms` for 60 fps):
  1. Step `CacheUpdate` to every 2nd frame (`_auxEvery = 2`, saves **`0.56 ms` RT-CPU + `0.15 ms` GPU** in `8-3 combat`, virtually invisible during fast combat motion).
  2. Halve `Volumetrics` & `ParticleLight` update rate (temporal reprojection holds smooth air/smoke across 1 skipped frame, saving another **`~0.7 ms` RT-CPU**!).
  3. Temporarily cap `EffectGeometry` to `8` and `PortalLightImages` to `8` during intense explosion bursts.
- **Estimated win:** **`-1.2 to -1.8 ms` RT-CPU** specifically during the heaviest `8-3 combat` / explosion frames, protecting `1% lows` without touching calm-scene quality.

---

## Bucket 3 — Quality-for-Speed Tradeoffs & Novel / SOTA Real-Time Path Tracing Ideas

Combining our codebase telemetry with the **2023–2026 real-time light transport survey** (Sonnet 5.5 brief + Blackwell hardware capabilities), below are the highest-leverage SOTA techniques tailored specifically to **ULTRAKILL's unique advantages** (coarse low-poly geometry, crisp point-sampled pixel-art textures, fast combat, DLSS Ray Reconstruction, and RTX 40/50-series hardware). Note that several of these deliver **BOTH speed AND a major visual-quality upgrade!**

### B3.1 — World-Space Light Grid (ReGIR-Lite / RTXDI-Style Spatial Light Grid)
> **Win type:** **Performance + Major Visual Quality Upgrade** *(Rank #2 in SOTA brief)*
- **Reference:** Boksansky et al. (*Ray Tracing Gems II*, ReGIR) & NVIDIA RTXDI 2.x / RTX Kit (2024–2026).
- **Why it fits UltraRT uniquely well:**
  - UltraRT removed screen-space ReSTIR DI (2026-11-19) because per-pixel temporal reservoir correlation fought DLSS RR and lagged on moving combat lights.
  - A **World-Space Light Grid** is rebuilt fresh every frame (zero temporal correlation bias, 100% DLSS RR friendly!) using camera-nested multi-level cells (reusing `RadianceCache`'s existing spatial hash!).
  - Each active cell stores `16–32` pre-resampled light + emissive-triangle reservoirs chosen by unshadowed contribution at jittered points inside the cell (filled by a cheap `0.15–0.25 ms` compute pass — **no RTAS or SBT needed!**).
  - `PathTrace`, `CacheUpdate`, `Volumetrics`, `ParticleLight`, and `Viewmodel` all sample `4–6` candidates from their local cell instead of `16–32` candidates from the global 150-light + 20,000-emissive-triangle alias tables!
- **Performance gain:** Cuts `RisCandidates` from `16 -> 4–6` (`-0.3 to -0.5 ms` GPU in `PathTrace` + `-0.15 ms` in `Viewmodel`/`Volumetrics`) and **eliminates the `107` CPU emissive alias-table rebuilds**!
- **Visual quality gain:** Dramatically lower variance in multi-room levels (`8-2`, `8-3`, `6-2`), near-zero fireflies from distant lights or 20k-triangle lava/neon strips, instant response to muzzle flashes/explosions, and cleaner volumetric shafts (since volumetrics share the same spatial light grid!).
- **Phase 2 extension (Visibility-Aware Grid):** Track 1-bit/rolling visibility feedback per cell-light slot from primary shadow rays so lights occluded by room walls stop wasting shadow rays entirely (huge win in multi-room arenas and behind portals!).
- **Portals note:** Key cells by `(portalRegionId, cellCoord)` or transform portal-image lights into the exit room's grid cells.

### B3.2 — SHaRC-Style Radiance Cache Upgrades (Sparse Path Backpropagation + Directional L1 SH)
> **Win type:** **Eliminates `CacheUpdate` `DispatchRays` (`-1.12 ms` RT-CPU!) + Major Visual Quality Upgrade** *(Rank #3 in SOTA brief)*
- **Reference:** NVIDIA RTXGI 2.0 **SHaRC** (Spatial Hash Radiance Cache, 2024–2025) & directional radiance caching.
- **Mechanism & fit for UltraRT:**
  1. **Fold cache updates into `PathTrace` (SHaRC backpropagation):** Instead of running a separate `CacheUpdate.raytrace` (`13,754` DXIL inst, `15` `TraceRay` sites, **`0.45–1.12 ms` RT-CPU**!), select `1 in 16` primary screen pixels (checkerboard/blue-noise) during `PathTrace` to backpropagate path radiance into every cache cell visited along the 1–2 bounce path, then run a pure-compute `CacheResolve` pass (zero `DispatchRays`!).
  2. **Normal-aware cell key + L1 Spherical Harmonics (or 6–8 octahedral directional bins):** Adds `~3 MB` VRAM (`64k` cells × directional bins) to eliminate light leaking across thin ULTRAKILL walls AND lets rough glossy reflections query directional multi-bounce radiance instead of flat ambient!
- **Performance gain:** **Eliminates `CacheUpdate.raytrace` SBT rebuild completely (`-0.45 to -1.12 ms` RT-CPU, `-0.18 to -0.36 ms` GPU)**!
- **Visual quality gain:** Deeper effective bounce depth (richer colored room-to-room GI), elimination of thin-wall cache leaks, and **free colored multi-bounce glossy reflections** (querying the directional lobe at a reflection hit instead of tracing a 2nd reflection bounce).

### B3.3 — Stochastic Single-Lobe BSDF Selection + Diffuse Hit-Distance Guide for DLSS RR
> **Win type:** **Big GPU Win (`-0.5 to -0.9 ms` GPU) + Better DLSS RR Denoising** *(Rank #4 in SOTA brief)*
- **Mechanism:** Currently, every glossy pixel in `PathTrace` (`0-1 metal`, `8-2` water, `7-3` wet floors, metallic arenas) traces **BOTH** a full diffuse path (shadow + bounce) **AND** a full reflection path (`PtReflection` + effect-geometry ray). With DLSS Ray Reconstruction active, use Russian-roulette / MIS single-lobe selection: choose diffuse OR specular with probability proportional to `(diffAlbedo * keep)` vs `(specWeight)`, weight the chosen lobe by `1 / p`, and trace **only one secondary path per pixel**!
- **RR Synergy:** Use the UAV slot freed by removing `_gLitWeight` (`B1.7`) to pass accurate **diffuse hit distance** (and verify virtual-image specular motion vectors for planar mirrors/water) so RR separates specular reflections from diffuse surfaces with less ghosting.
- **Performance gain:** **-15% to -25% GPU on `PathTrace` dispatch** (`-0.5 to -0.9 ms` GPU in `0-1 metal`, `0-2`, `0-3`, `8-2`).
- **Visual tradeoff:** Slightly higher raw 1-spp variance on glossy pixels during fast camera sweeps (needs A/B burst-sigma verification on `0-1 metal` and `8-2` puddles; ideal default for `High` / `Performance` presets, optional toggle for `Ultra`).

### B3.4 — ULTRAKILL-Specific Breakthrough R&D: Face-Space / Texel-Space Irradiance Caching
> **Win type:** **Ultra-High GI Stability & Crisp Pixel-Art Alignment + Massive Bounce Speedup** *(Novel / Art-Tailored)*
- **Why this may never have been shipped in a real-time path-traced mod before:**
  - Modern AAA games have millions of micro-triangles (Nanite/photogrammetry) where per-face or per-texel caching is impractical.
  - **ULTRAKILL is the exact opposite:** Coarse low-poly architectural quads (often 2–20 m wide, flat normals!) textured with low-res point-sampled pixel art (`32×32` to `128×128`), where UltraRT already demodulates albedo at texel granularity!
  - Parametrizing static level quads (`StaticGroups`) into a lightweight **per-face irradiance tile atlas** (e.g. `4×4` to `16×16` texels per quad, or texel-aligned world grid keyed by `(staticGroupId, facePlaneId)`) creates a surface-bound irradiance cache with:
    - **Zero light leaking through walls** (every texel belongs strictly to one face).
    - **Zero spatial-hash collisions** and **pixel-crisp contact shadows/GI edges** matching ULTRAKILL's art style!
    - **Near-instant convergence:** Primary hits can read converged multi-bounce indirect GI straight from the face cache (updating only 5–10% of visible face texels per frame!), eliminating 80–90% of per-pixel diffuse bounce rays!
- **Performance / Visual potential:** Could cut `PathTrace` GPU by **`-1.0 to -1.5 ms`** while making indirect GI look cleaner than 4 spp path tracing!
- **Risks / Scope:** Long-term R&D branch — dynamic geometry (enemies, doors, moving platforms) still falls back to hash cache / real bounce, and unique face parameterization needs a clean atlas packer at `StaticGroups.Build()`.

### B3.5 — Wavefront Path Tracing, Software Ray Sorting, & Blackwell DXR 1.2 (SER + OMM)
> **Win type:** **GPU Ray Throughput (`+15–35%` on secondary rays)**
- **Software SER via Native `RayQuery` Wavefront Compute (`UltraRT.Native.dll`):**
  - Split `PathTrace` (or secondary bounce/reflection rays) into: (1) Primary G-buffer pass -> (2) Compact & radix-sort secondary rays by `(spatialHashCell, octantDirection)` -> (3) Coherent `RayQuery` trace pass!
  - Eliminates warp divergence across portals, glossy vs diffuse hits, and sky misses.
- **Hardware SER (`HitObject` / NVAPI) & Opacity Micromaps (OMM) on RTX 5070 Ti (Blackwell):**
  - RTX 5070 Ti natively accelerates both **Shader Execution Reordering** (reordering divergent material/portal/water hits in hardware) and **Opacity Micromaps** (evaluating alpha-tested grates/foliage/grass in the RT core without invoking any-hit shaders!).
  - Both require a native D3D12 raytracing pipeline in `UltraRT.Native.dll` (not exposed by Unity 2022.3) — best pursued **after** B1.5's native `RayQuery` bridge is proven.
- **Note on Neural Radiance Caching (NRC / SM 6.9 Cooperative Vectors):** Evaluated and **explicitly NOT recommended** for UltraRT — NRC adds online training passes that compete with DLSS RR for Tensor Core time, while ULTRAKILL's compact low-poly scenes are served far better and cheaper by SHaRC + World-Space Light Grids + Face-Space Caching.

### B3.6 — Additional Quality-for-Speed Knobs & Auxiliary Pass Replacements
| Technique | Mechanism | Est. Savings | Visual Impact |
|---|---|---|---|
| **Checkerboard secondary bounces** | Trace diffuse bounce on 50% of pixels (checkerboard alternating), feed direct + cache on the other 50%, let DLSS RR reconstruct | `-0.5 to -0.8 ms` GPU | Very subtle on matte walls; minor softness in fast-moving indirect shadows |
| **Cache-backed `ParticleLight` (Compute-only)** | Replace `ParticleLight.raytrace` (`2` rays/froxel) with a pure compute pass sampling `RadianceCache` + unshadowed/coarse-grid direct lights | **`-0.32 to -0.86 ms` RT-CPU**, `-0.14 to -0.19 ms` GPU | Softer shadow boundaries on smoke/dust; **zero SBT rebuild!** |
| **Cache-backed `Viewmodel` bounce + mirror-only refl** | Replace `Viewmodel`'s diffuse bounce ray with `RadianceCache` lookup; keep only 1 shadow ray + 1 mirror ray (or native `RayQuery` shadow!) | `-0.10 to -0.25 ms` GPU (and **`-0.87 ms` RT-CPU** if moved to native `RayQuery`!) | Virtually indistinguishable on fast-moving guns |
| **Half-rate `Volumetrics` with temporal reprojection** | Run `Volumetrics` every 2nd frame (reprojecting froxel/screen scatter smoothly with motion vectors) | `-0.22 to -0.45 ms` GPU, `-0.15 to -0.26 ms` RT-CPU | Negligible for smooth sun shafts/fog; slight 1-frame lag on instant explosion halos |
| **Lower trace scale (`0.25` = 4× exact grid w/ RR)** | `640×360 -> 2560×1440` with exact 4×4 remodulation grid | **`-1.0 to -1.5 ms` GPU** on `PathTrace` | Remodulation keeps pixel-art textures razor-sharp! Lighting shadows/reflections slightly softer |
| **Portal bounce hops = `1` (primary = `2`)** | Cap portal teleports to 1 on bounce/reflection rays | `-0.15 to -0.35 ms` GPU in `8-2`/`8-3` | Only affects deep mirror-tunnel / double-portal indirect light |

### B3.7 — Proposed Configurable Quality Presets (2560×1440, RTX 5070 Ti)

Assuming **Phase A + Phase B** (Bucket 1 + Bucket 2 zero-loss/situational fixes) land first, here is how four user-facing presets map onto the engine:

| Setting | **Ultra** (Showcase) | **High** (Recommended Default) | **Performance** | **Competitive (144+ fps)** |
|---|---|---|---|---|
| Mode / Denoiser | `pathtrace` + DLSS RR | `pathtrace` + DLSS RR | `pathtrace` + DLSS RR | `pathtrace` + DLSS RR |
| Trace Scale (Exact Grid) | `0.50` (2×2) or `0.333` (3×3) | `0.333` (3×3) | `0.333` (3×3) | `0.25` (4×4) |
| Diffuse + Specular Path | Full dual-lobe (2 bounces) | Single-lobe RR (1–2 bounces) | Single-lobe RR (1 bounce -> SHaRC) | Checkerboard 1 bounce -> SHaRC |
| Direct Light Sampling | World Light Grid (`8` cand) | World Light Grid (`6` cand) | World Light Grid (`4` cand) | World Light Grid (`4` cand) |
| `CacheUpdate` | Folded SHaRC (every frame) | Folded SHaRC (every frame) | Folded SHaRC (sparse 1/32) | Folded SHaRC (every 2nd frame) |
| `Volumetrics` | Native `RayQuery`, 1-pass WRS, every frame | Native `RayQuery`, 1-pass WRS, every frame | Native `RayQuery`, half-rate temporal | Game fog + sun shafts only (no local light march) |
| `ParticleLight` | Native `RayQuery` | Native `RayQuery` | Compute cache-backed (0 rays) | Compute cache-backed (0 rays) |
| `Viewmodel` RT | Full RT (tile-culled) | Shadow + refl + cache bounce | Shadow + cache bounce (0 refl rays) | Off (or cache-only compute) |
| `EffectGeometry` / `PortalHops` | Cap `16` / `3` hops | Cap `12` / `2` primary, `1` bounce | Cap `6` / `2` primary, `0` bounce | Off / `1` primary, `0` bounce |
| **Est. Calm (`0-1`/`0-3`)** | **135–160 fps** (`6.2–7.4 ms`) | **175–200 fps** (`5.0–5.7 ms`) | **210–240 fps** (`4.2–4.8 ms`) | **250+ fps** (`<4.0 ms`) |
| **Est. Combat (`0-2`)** | **115–135 fps** (`7.4–8.7 ms`) | **140–165 fps** (`6.0–7.1 ms`) | **165–190 fps** (`5.3–6.0 ms`) | **185–210 fps** (`4.8–5.4 ms`) |
| **Est. Portals (`8-2`/`8-3 b`)** | **90–115 fps** (`8.7–11.1 ms`) | **115–135 fps** (`7.4–8.7 ms`) | **130–155 fps** (`6.5–7.7 ms`) | **145–170 fps** (`5.9–6.9 ms`) |
| **Est. Heavy Combat (`8-3 combat`)** | **85–105 fps** (`9.5–11.8 ms`) | **100–125 fps** (`8.0–10.0 ms`) | **115–140 fps** (`7.1–8.7 ms`) | **130–155 fps** (`6.5–7.7 ms`, near `off`'s 125 fps ceiling!) |

*(Note: `8-3 combat off` itself runs at `8.02 ms / 125 fps` due to ULTRAKILL's own `4.06 ms scripts + 3.92 ms render` CPU cost; with Bucket 1 + Bucket 2 eliminating UltraRT's SBT rebuilds and hitches, `pathtrace` approaches within `1.5–2.5 ms` of the unmodded game's CPU ceiling!)*

---

## Prioritized Implementation Roadmap

### Phase A — High-ROI Quick Wins (1–3 days, Zero Visual Risk, Immediate Impact)
1. **Fix `BakedLightMask` 5 s `FindObjectsOfType<StaticSceneOptimizer>()` (`Plugin.Noise.cs:65–78`)** -> eliminates `6–15 ms` hitches across EVERY level.
2. **Fix `LitParticles.KeepOrbGlows` (`37.2 ms`) & `ResolveAddBinds` (`Plugin.LightOverrides.cs:247`)** -> eliminates remaining full-scene `FindObjectsOfType` hitches.
3. **Move `VeilTrace` to a tiny dedicated `VeilRtas` (`3–20` instances)** -> **`-0.55 to -0.92 ms` RT-CPU** + **`-0.25 ms` GPU** in `0-3`, `8-2`, `8-3`.
4. **`Volumetrics.raytrace` 1-pass WRS** -> **`-0.25 to -0.40 ms` GPU** in `8-2`/`8-3` (`-0.15 ms` elsewhere).
5. **`UltraRTLights.hlsl` range/cone early-out + CPU pre-linearized light color + strip `_LightSampling <= 0` from production entry points** -> **`-0.40 to -0.80 ms` GPU** + faster PSO compile.
6. **Gate `ProcessReAdds()` on new RTAS meshes + `BindPortalBuffer()` dirty flag + `EffectGeometry` `MarkDynamic()` & sprite caching/distance culling + `WetBlood` at trace res** -> eliminates `83%` of combat re-adds, `8-3 b`'s `100 KB/f` alloc, and `20` distant candle BLASes.

**Combined Phase A expected gain:**
- `0-1` / `0-3` (`pathtrace`): `8.0–8.4 ms` -> **`~6.4–6.8 ms` (`147–156 fps`)**, zero 5 s hitches.
- `8-2` / `8-3 b` / `8-3 combat` (`pathtrace`): `14.5 / 18.0 / 16.6 ms` -> **`~11.8 / 14.5 / 12.8 ms` (`69–85 fps`)**, huge `1% low` jump.

### Phase B — Structural Render-Thread & Shader Overhaul (1–2 weeks)
1. **C4: Merge non-moving dynamic proxies into `StaticGroups`** -> collapses `250–380` dynamic SBT records in `8-2`/`8-3` (**`-1.5 to -2.8 ms` RT-CPU**).
2. **Fold `CacheUpdate` into `PathTrace` (SHaRC sparse backpropagation + normal-aware key + L1 SH)** -> removes 1 full scene `DispatchRays` (**`-0.45 to -1.12 ms` RT-CPU**, **`-0.2 to -0.35 ms` GPU**) + visual GI/glossy upgrade.
3. **Audit & fix `DlssRR` `OwnCommandList` mid-frame flush (`-0.3 to -0.6 ms` RT-CPU), `Finish` 3×3 ray-basis interp (`-0.2 ms` GPU), and bypass `ReflectionFilter.Temporal/Spatial` under RR (`-0.2 ms` GPU)** + pool `EffectGeometry` materials to eliminate `200–337 ms` stalls.
4. **Split static vs dynamic `EmissiveLights` alias tables** + fix `ViewmodelRect` combat fallback & pack `_LitWeight` (`-0.3 ms` GPU/CPU in combat).
5. **Deduplicate `PtPath` / `TraceSceneCone` in `PathTrace.raytrace`** (37 -> ~12 `TraceRay` sites).

**Combined Phase A + B expected gain:**
- `0-1` / `0-3` (`pathtrace`): **`~5.2–5.8 ms` (`172–192 fps`)**
- `0-2 combat`: **`~7.0–7.8 ms` (`128–143 fps`)**
- `8-2` / `8-3 b` / `8-3 combat`: **`~8.5–10.5 ms` (`95–118 fps`)**, `1% lows` **`50–70+ fps`**!

### Phase C — SOTA Breakthroughs & Presets (R&D Track)
1. **Native D3D12 `cs_6_5` `RayQuery` compute bridge in `UltraRT.Native.dll`** (capture Unity TLAS GPUVA -> run `Volumetrics`, `ParticleLight`, `Viewmodel` shadows with **zero SBT rebuilds**).
2. **World-Space Light Grid (ReGIR-lite) + visibility feedback** (shared by `PathTrace`, `Volumetrics`, `Viewmodel`, `ParticleLight`) — **both faster AND dramatically cleaner multi-room/combat lighting**.
3. **Stochastic Single-Lobe BSDF Selection + Diffuse Hit-Distance Guide** for DLSS RR + ship the 4 configurable **Quality Presets** (`Ultra`, `High`, `Performance`, `Competitive 144+`).
4. **Long-term SOTA exploration:** Face-space / texel-space static irradiance caching for ULTRAKILL's low-poly quads, and native Blackwell DXR 1.2 SER + OMM once the native ray pipeline matures.
