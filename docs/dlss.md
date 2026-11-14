# DLSS Ray Reconstruction (prototype)

NVIDIA DLSS Ray Reconstruction (RR, NGX feature "DLSS-D") can replace UltraRT's denoiser ([denoiser.md](denoiser.md)) and
temporal upsampler in `relight` and `pt-dn`. We call the NGX SDK directly from our native plugin. The Unity DLSS package
is HDRP-only and has no RR. Hybrid keeps our own path.

| File | Role |
|---|---|
| `scripts/fetch-dlss.ps1` | Downloads the SDK parts we need into `lib\dlss\` (gitignored) |
| `native/UltraRT.Native/Dlss.cpp` | NGX init, RR feature, evaluate inside Unity plugin events (`DlssStub.cpp` when the SDK is missing) |
| `native/UltraRT.Native/GpuTimer.cpp` | Unchanged except three hooks: device events, render events, unload (`Native.h`) |
| `unity/.../RayReconstruction.compute` | `Prepare` (RR inputs from our G-buffer), `Finish` (tone curve, RT mask), `ExposureInput` |
| `src/UltraRT/DlssRR.cs` | Managed wrapper: native exports, RR textures, jitter, passes |
| `src/UltraRT/Plugin.Dlss.cs` | Settings, Ctrl+F8, hooks into `Trace()` |
| `bench/suites/dlss-smoke.json`, `dlss-eval.json`, `dlss-3-1.json` | Test suites (see "Evaluation") |

## Use

- `scripts\fetch-dlss.ps1` once (by running it you accept NVIDIA's license, see "License"). Then build as usual.
  `native\UltraRT.Native\build.ps1` links `nvsdk_ngx_s.lib` when it finds the SDK. Without it, it builds the stub and RR
  reports itself unavailable. `build.ps1 -NoDeploy` stages `nvngx_dlssd.dll`, and `deploy.ps1` copies it next to
  `UltraRT.Native.dll` in `BepInEx\plugins\UltraRT\`. Nothing goes into the game's own folders.
- Config `[DLSS] RayReconstruction` (default **off**), `-ultrart-rr` / `-ultrart-norr`, and **Ctrl+F8** in game (plain F8
  opens the game's console). RR only applies in `relight` and `pt-dn`. Every other mode keeps our denoiser.
- Flags: `-ultrart-rr-preset N` (0 = default, 4 = D, 5 = E, 6 = F), `-ultrart-rr-unitylist` (record into Unity's command
  list instead of our own), `-ultrart-rr-jitter-sign S`, `-ultrart-rr-nohitdist`, `-ultrart-rr-gamma`.
- Bench variants: `relight+plugin._rrEnabled=1`, plus `plugin._rrPreset`, `_rrOwnList`, `_rrJitterSign`, `_rrSpecHit`,
  `_rrLinear`. GPU timer slots `rr` (Prepare + RR + Finish) and `rr-eval` (the plugin event only).
- Logs: NGX's log callback is forwarded to `LogOutput.log` as `[RR]` lines, minus its start-up chatter. NGX's own files
  go to `BepInEx\ultrart\ngx\` (`nvngx_dlssd_<ver>.log` has the feature's log). NGX also creates
  `C:\ProgramData\NVIDIA\NGX\models` (its system-wide model cache), as any DLSS game does.

## How it works

### Native side (Dlss.cpp)

- **Device.** We use Unity's `ID3D12Device` from `IUnityGraphicsD3D12v7` (GpuTimer.cpp already registers the DLL with
  Unity, see [gpu-timer.md](gpu-timer.md)). NGX starts lazily on the first evaluate, on the render thread:
  `NVSDK_NGX_D3D12_Init_with_ProjectID` with a made-up project GUID and engine type CUSTOM. `PathListInfo` points at the
  plugin folder, so NGX finds `nvngx_dlssd.dll` next to our DLL, and the log callback is set. Next come
  `GetCapabilityParameters` (`SuperSamplingDenoising.Available`) and `AllocateParameters`.
- **Feature.** `NGX_D3D12_CREATE_DLSSD_EXT` with render size = the visible trace (guard band cropped) and output size =
  the screen. Flags `IsHDR | MVLowRes` (motion vectors at render resolution, unjittered), roughness packed in
  `normals.w`, linear depth, PerfQuality picked from the ratio (0.5 = Performance), and the preset hint set for every
  quality mode. It is recreated when sizes or the preset change. It is released through a plugin event after 5 s
  without RR, after waiting for the frame fence of its last evaluate, so the bench's alternating modes don't rebuild it
  every segment.
- **Events.** IDs 94 to 96 (the timer uses 91 to 93), configured at device init:
  - **94, Unity's command list** (`CommandRecordingState`), flag `ModifiesCommandBuffersState`, because NGX binds its own
    descriptor heaps, root signature and PSO. We transition the inputs UAV -> NON_PIXEL_SHADER_RESOURCE, evaluate, then
    transition back, with UAV barriers on the output.
  - **95, our own command list (default).** A ring of 8 allocators. The list is submitted with
    `IUnityGraphicsD3D12v7::ExecuteCommandList` and per-resource `{expected, after}` state declarations: inputs
    NON_PIXEL_SHADER_RESOURCE, output UNORDERED_ACCESS. Flag `FlushCommandBuffers` makes Unity submit its earlier work
    first, so our list runs after the trace. Unity's command-list state is never touched.
  - **96** releases the feature.
- **Resource states.** DLSS wants inputs in NON_PIXEL_SHADER_RESOURCE and the output in UNORDERED_ACCESS, and leaves them
  so. Unity tracks states itself, and the state query of the obsolete `IUnityGraphicsD3D12` (v1) interface is **not
  available** in this player (logged at start-up). So every resource the event uses is written as a UAV by the
  `Prepare` dispatch just before it, including a one-pixel "touch" of the output. Unity's tracked state is therefore
  UNORDERED_ACCESS for all of them, and both event variants put them back that way (94) or tell Unity what they expect
  (95).
- **Frames.** C# writes a `DlssFrame` (native pointers, sizes, jitter, matrices, flags) into a 16-entry ring with
  `UltraRT_Dlss_SetFrame` and passes the slot as the event's data. The render thread copies it at execution time.

### Inputs (RayReconstruction.compute, Prepare)

Prepare reads only existing outputs, so PathTrace is unchanged (still 8 UAVs, 64-byte payload). It runs at render
resolution: the visible part of the trace, with the guard band cropped, so DLSS needs no sub-rectangles.

| RR input | From | Notes |
|---|---|---|
| Color (RGBA16F) | The composite's terms per trace pixel | See "Colour space" |
| Diffuse albedo | `_Albedo * keep` (linearised) | Sky 0.5 |
| Specular albedo | `SpecularTerms(material)` = F * weight, the same factor the reflection resolve applies | 0 where reflections are off |
| Normals + roughness (RGBA16F) | `_NormalDepth.xyz` (world), `SurfaceMat.roughness` | Sky 0 |
| Depth (R32F, linear) | `t * dot(dir, camForward)` | Sky = far plane |
| Motion (RG16F) | World position + `UnpackMotion(_InstanceId.y)`, projected with last frame's jittered viewProj, last jitter removed, minus the unjittered pixel centre (trace px, previous - current) | Sky: direction only |
| Specular hit distance (R16F) | Raw `_Reflection.a` | Sky 65504; with the world-to-view / view-to-clip matrices (row vectors, view +z forward) |
| Jitter | Our own Halton(2,3), 64 phases (RR guide: at least 32) | Sign: see "Evaluation" |

**Colour space.** With relight's linear transport the lighting `L` is linear, and the composite is
`(albedo * L^(1/2.2) * keep + spec) * e + emission` in the game's gamma units (e = auto exposure on the lit part). RR
wants linear HDR, so each term is linearised on its own:
`(albedo * keep * e)^2.2 * L + (spec * e)^2.2 + emission^2.2`. The diffuse term is exactly linear in the noisy `L`, so
RR averages what our denoiser averages. `Finish` raises the result back to `^(1/2.2)`. Gamma transport (`pt-dn`, or
`-ultrart-rr-gamma`) feeds the gamma terms as they are. Exposure is applied **before** RR (pre-exposed input; RR
ignores exposure parameters).

### Output

- **Finish** (screen res): back to display space, relight's tone curve (`UltraRTToneMap`, so the shoulder and white point
  match), saturate. a = 1 where any of the 3x3 nearest trace samples matches the game's depth (3% + 5 cm, portal planes
  included). The composite reads it as `_UltraRTUpsampled`, exactly like the temporal upsampler's output, so the
  volumetrics' "is this an RT pixel" test works unchanged.
- **Auto exposure**: the denoiser's filtered lighting doesn't exist in RR frames. `ExposureInput` gives `Exposure.compute`
  the unexposed lit part of the RR result at trace resolution, `(RR - emission) / e`, with albedo 1 and no specular.
  It measures the same quantity as before, one frame behind as before.

## Evaluation

(EVAL)

## License

The DLSS SDK is under the "NVIDIA RTX SDKs" license (`lib\dlss\LICENSE.txt`; fetched, not committed). For this repo:
- **Don't commit or redistribute NVIDIA's files.** You may distribute the SDK only "incorporated in object code format
  into a software application" with "material additional functionality" and terms at least as protective as NVIDIA's,
  and never under an open-source license. `fetch-dlss.ps1` keeps the binaries out of git.
- **Release builds.** NVIDIA must be notified before a commercial release. Shipping the SDK requires NVIDIA attribution
  (marks on a splash screen, about box or credits). The RR guide says the `dev` and `rel` DLLs in the public SDK are for
  development and that a distributable DLL comes from NVIDIA. Public distribution of a mod with RR therefore needs
  NVIDIA's involvement. Fine for local experiments; not something to ship in a release zip as is.
- The NGX helper headers' notice ("This software contains source code provided by NVIDIA Corporation.") is in
  `Dlss.cpp`.

## Scope: what else NVIDIA's stack could do here (write-up only)

### (a) DLSS Super Resolution for the game's own raster

**What it would take.**
- ULTRAKILL renders forward into `PostProcessV2_Handler`'s `mainTex` + `depthBuffer` at screen resolution, and
  `ULTRAKILL/PostProcessV2` then reads `Main` ([ultrakill-rendering.md](ultrakill-rendering.md)). SR needs the world
  camera to render at the render size, so we would patch the handler (Harmony) to allocate its targets at render size
  and run SR into a screen-size target before the post pass.
- Camera jitter: set `Camera.projectionMatrix` per frame and keep `nonJitteredProjectionMatrix` for culling. The HUD
  camera (viewmodel) and the UI must keep rendering at output resolution after SR, and the HUD camera shares the main
  camera's colour/depth buffers today.
- Full-scene motion vectors. The game has no motion-vector pass (forward rendering, no depth texture). The built-in
  pipeline's `DepthTextureMode.MotionVectors` only covers shaders with a MotionVectors pass, which ULTRAKILL's Master and
  Stationary shaders lack. Everything else would get camera motion only, so enemies would ghost. **Our traced G-buffer
  already has per-pixel world motion** (`_InstanceId.y`) at exactly the SR Performance render size (trace scale 0.5),
  so for pixels the trace covers we could hand our motion vectors and depth to SR. Particles, sprites and the sky would
  need camera-only vectors from the game's depth.
- Texture LOD bias: the game point-samples low-res textures, so the mip bias matters little, but SR's reconstruction of
  point-filtered texel edges would need a look.

**Value: low.** The game's raster is cheap: `off` runs at 2-3 ms per frame and is CPU-bound. In relight/pt-dn the image
is already ours and RR (or our TAAU) upscales it, so SR would only help hybrid, whose base is the game's full-res
colour. Not worth the invasive render-target patching. If we ever want it, "DLSS-SR on the game's raster with our
traced motion vectors" is the angle.

### (b) DLSS Frame Generation (Streamline) and Reflex

**What it would take.**
- DLSS-G works on the swapchain: Streamline's `sl.interposer.dll` must intercept the DXGI factory/swapchain *creation*.
  Unity creates its D3D12 device and swapchain during player start-up, before BepInEx's chainloader runs, so a plugin is
  too late. Getting there means a proxy `dxgi.dll` / `d3d12.dll` (or Streamline's interposer) in the game folder, the
  route OptiScaler-style mods take. That breaks our "nothing in the game's folders" rule and touches the user's install.
- Inputs at output resolution every frame: depth, motion vectors, a hudless colour (the frame before the HUD camera and
  UI), the UI colour + alpha (or `hudless` alone), camera matrices and jitter. ULTRAKILL's HUD is large and animated, so
  without a hudless buffer FG warps the HUD. Hudless = `PostProcessV2`'s output before the HUD camera, which is
  reachable from a camera event. Full-scene motion vectors have the same gap as (a).
- Reflex is mandatory with FG: `sl.reflex` markers (simulation start/end, render submit start/end, present) from the
  player loop, which we can hook from C# (PlayerLoop insertion), plus `NvAPI_D3D_SetSleepMode`/`Sleep` on Unity's device.
  **Reflex alone, without FG, is feasible from our native plugin** (NVAPI only, no swapchain interception) and would cut
  latency when GPU-bound. It is worth a separate small experiment.
- FG adds about one frame of latency on top of what Reflex removes. ULTRAKILL is a twitch shooter, and the user's
  monitor is 360 Hz: going from about 120 to 240-360 fps with multi-frame generation would look smoother, but input
  latency would stay at the 120 fps level or worse.

**Verdict:** possible only as a game-folder injection mod; high risk (Unity's own present path and frame pacing),
moderate value. Reflex-only is the cheap, useful part.

### (c) A path-tracing mode with RR doing denoise + upscale: budget at 1440p, 120 Hz (8.3 ms)

Measured on an exclusive bench lock (`dlss-cost`, -Perf), at render 1280x720 (+8% guard band), i.e. exactly the user's
1440p trace at scale 0.5:

| | 3-1 room | 6-2 start | 0-1 metal | 8-2 portal | 0-2 walk |
|---|---|---|---|---|---|
| relight `dispatch` (all rays + shading of the current trace) | 2.53 | 2.73 | 3.59 | 2.99 | 2.79 |
| RR 720p -> 1440p (Prepare + RR + Finish) | 1.97 | 2.02 | 2.02 | 1.99 | 2.02 |
| rtas + cache + volumetrics + viewmodel + particles + rest | ~0.9 | ~1.0 | ~0.9 | ~1.2 | ~0.8 |

The current relight trace is about two path segments per pixel: a primary hit with one ReSTIR shadow ray, a sky
visibility ray and a reflection ray where specular, plus one bounce whose hit reads the radiance cache. That puts one
segment with its next-event estimation at **about 1.3-1.8 ns per pixel** (2.5-3.6 ms over 1.0 M pixels), shading,
alpha-tested any-hits and texture fetches included.

Path tracing with 2 paths per pixel and 2-3 bounces = 1 shared primary + 2 x (2..3) segments = **5-7 segments, 2.5-3.5x
today's per-pixel cost** (optimistic: deeper bounces are less coherent than the primary, so add 20-40%):

| Render resolution (pixels incl. guard) | Trace (2 paths, 2 bounces) | Trace (2 paths, 3 bounces) | + RR to 1440p + fixed passes | GPU total |
|---|---|---|---|---|
| 1/2: 1280x720 (1.0 M) | 6.3-9.0 ms | 8.8-12.6 ms | +3.0 ms | **9.3-15.6 ms**: over budget |
| 1/3: 853x480 (0.44 M) | 2.8-4.0 ms | 3.9-5.5 ms | +2.9 ms (RR cost is mostly output-bound) | **5.7-8.4 ms** |

So at 120 Hz only the 1/3-resolution variant fits, with 2 bounces comfortably and 3 bounces at the edge, leaving
little for the game's own GPU work (raster, post, UI). With the current CPU frame (1.3-2 ms main thread + 0.4-0.6 ms
plugin), CPU and GPU overlap, so the GPU number is what limits. Notes:
- RR alone is about 2 ms of the 8.3 ms at 1440p output. At 1/3 render resolution (Ultra Performance, 3x) RR has to
  invent more, and texture detail on ULTRAKILL's point-sampled art would suffer.
- 8-3 (100+ lights) and outdoor sky-heavy views cost more per segment. ReSTIR already keeps light count out of the
  primary.
- The radiance cache would still terminate paths early (as it ends today's bounce), which is what makes 2 bounces
  cheap. "Full" PT without it costs more per segment.
- RR's guide asks for uncorrelated samples. ReSTIR's temporal/spatial reuse violates that, so a PT mode should
  randomise or permute the reuse (or skip spatial reuse) for RR, and measure.
