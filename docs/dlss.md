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

(SCOPE)
