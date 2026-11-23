# DLSS Ray Reconstruction (prototype)

NVIDIA DLSS Ray Reconstruction (RR, NGX feature "DLSS-D") can replace UltraRT's denoiser ([denoiser.md](denoiser.md)) and
temporal upsampler in `relight` and `pt-dn`, and it is the default denoiser of the path tracing mode
([path-tracing.md](path-tracing.md)). We call the NGX SDK directly from our native plugin. The Unity DLSS package
is HDRP-only and has no RR. (The hybrid mode kept our own path until it was removed on 2026-11-19.)

The first half of this document is the prototype on relight's signal (2026-11-14, where RR lost to our denoiser); the
section "RR for path tracing" is what changed for the `pathtrace` mode, where it wins.

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

- Settings menu: "Path tracing" and the DLSS entries ([settings.md](settings.md)). The offline renderer and the trailer use RR at full resolution ([cinematics.md](cinematics.md), "Trailer assembly"); the cache tail is [radiance-cache.md](radiance-cache.md).
- `scripts\fetch-dlss.ps1` once (by running it you accept NVIDIA's license, see "License"). Then build as usual.
  `native\UltraRT.Native\build.ps1` links `nvsdk_ngx_s.lib` when it finds the SDK. Without it, it builds the stub and RR
  reports itself unavailable. `scripts\build.ps1 -NoDeploy` stages `nvngx_dlssd.dll`, and `deploy.ps1` copies it next to
  `UltraRT.Native.dll` in `BepInEx\plugins\UltraRT\`. Nothing goes into the game's own folders.
- Config `[DLSS] RayReconstruction` (default **off**), `-ultrart-rr` / `-ultrart-norr`, and **Ctrl+F8** in game (plain F8
  opens the game's console): RR in `relight` and `pt-dn`. `pathtrace` has its own choice (`[PathTrace] Denoiser`, default
  Auto = RR when it can run). Every other mode keeps our denoiser.
- `-ultrart-rr-noremod` (bench `plugin._rrRemodulate=0`) turns remodulation off, `plugin._rrExactJitter=0` the exact
  jitter grid (both below, "RR for path tracing").
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
  Unity, see [gpu-timer.md](gpu-timer.md)). NGX starts on a worker thread when the game starts (see "Warm-up"; without
  the warm-up, lazily on the first evaluate on the render thread): `NVSDK_NGX_D3D12_Init_with_ProjectID` with a made-up project GUID, engine type CUSTOM and engine version `UltraRT-<Version.props>` (since 2026-11-20; `UltraRT-0.1` before; [release.md](release.md)). `PathListInfo` points at the
  plugin folder, so NGX finds `nvngx_dlssd.dll` next to our DLL, and the log callback is set. Next come
  `GetCapabilityParameters` (`SuperSamplingDenoising.Available`) and `AllocateParameters`.
- **Feature.** `NGX_D3D12_CREATE_DLSSD_EXT` with render size = the visible trace (guard band cropped) and output size =
  the screen. Flags `IsHDR | MVLowRes` (motion vectors at render resolution, unjittered), roughness packed in
  `normals.w`, linear depth, PerfQuality picked from the ratio (0.5 = Performance), and the preset hint set for every
  quality mode. It is recreated when sizes or the preset change, on the warm-up worker (see "Warm-up"). It is released
  through a plugin event after 5 s without RR, after waiting for the frame fence of its last evaluate, so the bench's
  alternating modes don't rebuild it every segment.
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
| Normals + roughness (RGBA16F) | `_NormalDepth.xyz` (world), `SurfaceMat.roughness` | Sky 0. Per-texel detail at the ray cone's mip, with its Toksvig roughness ([materials.md](materials.md), "Texture LOD"): sub-pixel bumps arrive averaged, not as per-frame texel picks |
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

## RR for path tracing

Four changes made RR the better denoiser for the `pathtrace` mode. The measurements behind them are in
[path-tracing.md](path-tracing.md) and below.

### Independent samples

RR measured 1.1-2x noisier than our denoiser on relight's signal (below). The cause is ReSTIR's temporal reuse: with
plain RIS (`lights=ris`) or a short history (`ls.historyLimit=2`) the same RR bursts are 35-45% quieter and match
"no light-selection noise at all" (`lights=ref`); the radiance cache makes no difference. `pathtrace` therefore
samples lights at the primary hit with RIS and no reservoirs while RR denoises (`ptx.lightsRR`, `candidatesRR`,
`directSamplesRR`). Relight under RR (Ctrl+F8) keeps whatever F6 selects; relight's default (RIS since 2026-11-15, formerly ReSTIR, hence
`-ultrart-lights ris` at the time) is the setting to pair with it, ReSTIR is the correlated case.

### Remodulation

RR reconstructs texture detail from a half to a ninth of the screen's pixels. On ULTRAKILL's point-sampled art that
smears the texel grid, clearly so at 1/3 scale. So RR no longer sees textures:

- `Demod` (trace res) computes what each surface shows under white light, **D = albedo x keep + specular weight +
  emission** (the diffuse and specular reflectance the composite uses, plus what the surface emits; sky: 1).
- `Prepare` gives RR the image divided by it, **X = c / D**: an "effective lighting" with no texture in it. The
  albedo guides become each lobe's share of D (they sum to 1 where nothing is emitted), so RR has no texture to
  reconstruct. Fullbright art and pure emitters become X = 1.
- D is noise-free, so it is accumulated to screen resolution by a second instance of the temporal upsampler
  (`TemporalUpsampler`, `TemporalUpsampler.cs`, instantiated in `DlssRR.cs`; the game's full-resolution depth is the geometry). Weights are sharp
  (`exp(-30 d^2)`, d = distance from the pixel centre in screen px): the sample nearest a pixel's centre wins. There is
  no clip box (it would cut texels the 9 samples of a frame happen to miss); instead a sample on the pixel centre that
  differs from the history replaces it, which follows animated textures and re-sharpens history that reprojection
  blurred.
- `Finish` multiplies RR's output by the screen-resolution D (the nearest matching trace sample's where the upsampler
  has nothing yet), then tone maps as before. Auto exposure's input is remodulated at trace resolution.

Textures and silhouettes converge to the game's own texel grid at any render scale, and RR only reconstructs
lighting, which is smooth. (Since 2026-11-21 the emission is no longer part of D for RR's signal: "Emission outside RR"
below.) Cost: 0.25 ms at 1440p (the `rr` slot goes from 1.32 to 1.57 ms at 1/3 scale). It is on
for relight and pt-dn under RR too.

### Emission outside RR (2026-11-21)

User, P-2 pose 1 (playtest of 2026-11-20): "strange staircase-y aliasing on some slanted emissive surfaces". The
light bars on P-2's slanted corridor wall had a stair-stepped bright seam along their edges in pathtrace; relight and
the game were clean. Not the depth match (`plugin._matchDebug=1` marked no pixel there), not front emitters
(`plugin._rrFrontEmitters=0` the same), not bloom or haze (`vfx.enabled=0`, `vol.strength=0` the same). The cause was
the remodulation itself: with the emission inside both the signal and D, an emitter's X = (lit + E) / (reflectance + E)
differs from the unlit wall's X next to it (here X about 2 on the bar against about 1.1 on the wall, `_rrFinishDebug=2`).
RR rebuilds that step at trace resolution, and the crisp screen-resolution D multiplied the bar's high X into the
border texels next to it: a seam that steps with the trace grid wherever an emitter's edge is slanted on screen.

Fix (`DlssRR.EmissionOutside`, on; `-ultrart-rr-emit-inside`, bench `plugin._rrEmitOutside=0` = before; `_RRParams2.w`):
- `Prepare`: RR's signal is the lit part only, over the reflectance only:
  X = ((albedo x keep + w) x Disp(L) + spec) x e / (albedo x keep + specW + w), with w = `kEmitWhite` 0.05 where the
  sample emits (0 elsewhere: unchanged): an emitter's X is the light that reaches it, continuous with the surface around
  it. The albedo guides are the lobes' shares of that denominator.
- `Finish`: final = Disp(X) x (D - E) + E with E = D x the emissive share of the trace sample whose D is nearest the
  pixel's screen-resolution D (`EmitShare`, 3x3; the emission alone is read first, D only where something emits). The
  share follows the edges of the screen-resolution D, so an emitter keeps the texel edge the game draws. Bloom's and the
  HDR boost's emission use that screen-resolution E too (they used the trace sample's).
- `ExposureInput`: the lit part is X x (D - E).
Emission is noise-free, so nothing is lost by keeping it out of RR; fullbright surfaces now show D exactly.

Measured (main install, SDR, trace scale 0.5, `20261121-020426_tmp-rp2-c`, before/after in one launch): P-2 pose 1's bar edge
is a straight texel edge like the game's (sheet `bench/results/review/reflections-and-p2/P-2_pose_1_slanted_emissive.jpg`); whole-frame
means before -> after: P-2 pose 1 122.0 -> 121.8, 1-4 lamps 40.1 -> 40.1, 0-5 lava 72.1 -> 72.1, 6-2 start 33.8 -> 34.2
(mean absolute differences 2.4-4.8 of 255, from the gun's bob and the lava's animation between the captures). Cost
(exclusive perf lock, 2560x1440, trace scale 0.5, `20261121-021902_tmp-rp2-perf`, before/after alternating in one pass):
P-2 pose 1 GPU total 7.24 -> 7.23 ms, `rr` slot 1.83 -> 1.82 ms; 1-4 lamps 4.20 -> 4.20 ms, `rr` 1.95 -> 1.95 ms: none
measurable. Not verified in HDR (the display was not in HDR mode during the runs; the HDR path uses the same
screen-resolution emission).

2026-11-21: 0-3's emissive wall strips showing through the yellow light pillars (playtest of 2026-11-21) were not this
change: `plugin._rrEmitOutside=0` showed the same strips, as did `relight`. They were the game's additive cones over a
traced room brighter than the game's (docs/lighting-overrides.md, "Light cones over bright emitters"). P-2 pose 1 is
unchanged by that fix (frame mean 133.4 -> 133.6).

### Exact jitter grid

With Halton jitter a screen pixel rarely gets a sample on its centre at 3x. At an integer upscale N (trace scale 1/2,
1/3, 1/4) the jitter is the N x N pattern that puts the trace samples on the screen pixel centres in turn
(`DlssRR.ExactGrid`), so every screen pixel gets its own texel once per N^2 frames. The screen is rarely a multiple of
N (2560 / 3), so the trace is sized to a whole number of trace pixels of exactly N screen pixels each, overscanning the
screen by a fraction of a trace pixel per side (`Plugin.ExactGridTrace`: `_guardScale` carries the exact ratio), and
RR's output is N x its render size, cropped to the screen by a whole number of pixels in `Finish`. RR itself measured
quieter on the 9-phase grid than on 72 Halton phases (0-1 metal: masked sigma 0.56 against 0.74).

### Emitters in front of the game's depth (2026-11-20)

`Finish` matches each screen pixel to a trace sample at the game's depth, and the remodulation's D is accumulated
against that depth too. Something the game draws but leaves out of its depth buffer is then invisible to both: its
samples lie in front of the game's surface and are rejected, the surface behind matches, and the pixel becomes RR's
lighting x the colour of what lies behind. Gabriel's swords in 6-2 are such a case: the organ showed through the blades
(docs/ultrakill-rendering.md, "Gabriel's swords").

Rule (`DlssRR.FrontEmitters`, on; `-ultrart-rr-nofrontemitters`, bench `plugin._rrFrontEmitters=0`; `_RRParams2.z`):
when the trace sample that covers the pixel emits (any channel above 0.05) and lies nearer than the game's surface by more
than the match tolerance, the pixel takes that sample and its own D (not the screen-resolution history). Only emitters:
what the game draws over a traced surface must keep the game's pixel, and a general nearest-depth rule turned 1-3's lava
yellow (docs/path-tracing.md, "Nearest-depth fallback"). With remodulation only. The covering sample alone is used, so
the blade does not grow by a trace texel; at the play scale (1/3) such an emitter is blocky by 3x3 screen pixels.

Checked (`bench/suites/front-emitters.json`, pt-quick's 6-2 start, 1-4 lamps, 0-5 lava, 8-2 portal, 7-3 flashlight,
on against off; `20261120-155041_front-emitters_alt`): no visible change. The pairs differ by 1.2-2.5 mean (0-5: 5.2) from
capture timing (gun bob, animated lava, particles), not at emitters.

Diagnostics: `plugin._rrFinishDebug=1` shows the screen-resolution D alone (tone mapped), `2` RR's lighting X alone
(x 0.5, so X = 1 is mid grey); `DlssRR.FinishDebug`, `_Match2.w`.

### V1's shadow before RR (2026-11-15)

User report (1-1, pathtrace): "strange shifting on things, a slight flicker in the x direction; it depends exactly where
I'm standing; in some positions none, in some it flickers more to one side". Bursts at the user's pose
(`bench/suites/mgw-check5.json`, 72 frames, masked sigma / p95): pathtrace 0.75 / 2.25, relight 0.41, pathtrace with V1
out of the shadow rays (`plugin._playerRays=11`) 0.53 / 1.43. The flicker map (`*_burst_pathtrace_sigma.png`) is V1's
shadow outline on the floor. It is where V1's shadow falls, so it depends on where the player stands and which side the
light is on.

Cause: with the player shadow split (docs/denoiser.md, "Moving shadows") V1's shadow is a factor in `_Radiance.a`, one
shadow ray per trace pixel. Our denoiser filters it (5x5 same-surface mean plus history) before relight's composite;
`Prepare` multiplied it raw into RR's input. At 1/3 scale on the exact 3x3 jitter grid the shadow edge is a 0/1 step
that lands on different screen pixels every frame, and RR shows it as a shifting outline. Not the cause (all measured at
the same pose): the trace-to-screen mapping (the RT frame lines up with the game's to within 0.01 px in x and y at the
left and centre of the screen), material normal strength (x2 / x0 / maps off: 0.75 / 0.74 / 0.79), the exact grid
(Halton 0.91).

Now `Prepare` gives RR a 3x3 same-surface tent of the factor (`PlayerShadowFactor`; neighbours within 5 % depth and
normal dot 0.9), and RR's history does the temporal part: 0.55 / 1.74, about the same as with no V1 shadow at all. Cost:
9 loads per trace pixel at 1/3 scale; GPU total 4.204 against 4.205 ms without (`bench/suites/mgw-perf.json`, `-Perf`,
exclusive slots, 2560x1440). Switch:
`DlssRR.PlayerFilter`, bench `plugin._rrPlayerFilter=0` (raw, as before). The rest of pathtrace's extra flicker over
relight (0.55 vs 0.41) is on silhouettes and texel edges at 1/3 scale, which RR reconstructs from every 9th frame.

### Area colour and game match

`Prepare` now applies the area colour match gain like the denoiser's Composite does, and the RR path binds the game's
frame for auto exposure (`BindGameMatch`), so the game match and the colour match work as without RR (7-3's floor was
grey-white under RR, the room's tint without).

### Presets, guides, the "first burst"

- NGX's default preset is **F** for every quality mode (its log says so). D and E are never better and up to 2x
  noisier (0-1 metal 1.62 default / 1.62 E / 2.12 D; 8-2 portal 0.70-0.79 / 1.37-1.44 / 1.23-1.38). The default stays.
- `ColorBeforeTransparency` and the particle guides have no source here: RR runs at the injection point, before the
  game draws transparents and particles at output resolution on top of its result. Nothing transparent is in RR's
  input (the water/glass reflection overlay is applied by the game's own transparent draw, later).
- The "first RR burst after another mode is noisier" effect did not reproduce in balanced lists (first and last RR
  bursts within 10% either way; identical with a 480-frame settle). What does happen once per process: NGX
  initialised on the first RR frame, which took seconds; a bench scenario that triggered it lost its captures. Now NGX
  warms up in the background at start and the bench waits for the feature before a capture ("Warm-up").

### Warm-up

The first RR frame of a session used to stop the game for about 3 s: NGX's initialisation (loading `nvngx_dlssd.dll`,
its model and its CUDA kernels) ran inside the first evaluate on Unity's render thread. Then every feature creation
(entering the mode after the 5 s release, a resolution change) and every new feature's first evaluate stalled it again:
measured 130-230 ms frames (`bench/results/20261115-013047_pt2-a`: 148 ms at the first switch to `pathtrace`, 135 ms
on P-2, 194 ms on 8-3).

Now (`UltraRT_Dlss_Warm`, `Dlss.cpp`; `DlssRR.WarmInit` / `EnsureFeatureAsync`; `Plugin.Dlss.cs`):
- **At start** (frame 3, whenever RR could be used: the `pathtrace` denoiser is Auto or RR, or RR in relight is on), a
  native worker thread initialises NGX (2.4-3.1 s, during the boot and the main menu) and creates a small throw-away
  feature (320x180 -> 960x540) that it evaluates once: NGX loads its kernels on the first evaluate of the process, and
  that stalled the render thread even when the worker did it mid-game. The C# side then releases that probe through the
  render-thread event (`[RR] start-up probe released on the render thread`).
- **Every frame that wants RR** first checks that the feature for this frame's exact sizes exists. If not, the worker
  creates it with its creation commands on a D3D12 queue of its own (31-82 ms), plus one evaluate on zero-filled inputs
  of the right sizes (6-30 ms): NGX's per-feature lazy work. Any other feature is released first, by the render-thread
  event: the worker releasing a feature it had created on its since-destroyed queue froze the render thread for 16 s
  inside NGX's ReleaseFeature. Meanwhile the frames use UltraRT's denoiser (the plain upsampler, no remodulation targets
  to allocate): 14-18 frames, 125-200 ms in all, at normal frame times (38.7, 14.4, 4.7, 8.4, 5.0, 6.8 ms after a
  relight -> pathtrace switch; the first is any mode switch's). The first real frame resets RR's history.
- A critical section serialises NGX between the worker and the render thread. A render event never waits for it: an
  evaluate that finds the worker busy is skipped (the C# side doesn't ask for one then), a release is done at the next
  event.
- `-ultrart-rr-nowarm` (bench `plugin._rrWarm=0`): the old lazy behaviour. The bench waits for the feature after a mode
  switch before screenshots, bursts and motion passes (`WaitForRR`).
- Log lines: `[RR] NGX ready ... (init N ms on the warm-up thread)`, `feature ... (created in N ms on the warm-up
  thread)`, `warm-up: first evaluate N ms`, and `DLSS Ray Reconstruction: ready after N ms of warm-up`; while it waits,
  once a second, `still warming up ... (worker busy | releasing the old feature | warm-up requested)`. `View mode A -> B
  at frame N: frame ms ...` logs the first six frame times after every mode switch.
- The longest first-use freeze was not NGX at all: the `pathtrace` mode's ray tracing pipeline (RayGenPT) compiles on
  its first dispatch, 16-32 s after a new shader bundle. It is now compiled on the session's first trace
  ([path-tracing.md](path-tracing.md), "Warm-up").

Trap: Windows' Smart App Control blocked one build of `UltraRT.Native.dll` (`LoadLibrary` error 4551, CodeIntegrity
event 3077 "did not meet the Enterprise signing level requirements"), and every rebuild of that same source; the
previous source loaded. RR then reported itself unavailable and `pathtrace` silently ran on UltraRT's denoiser for a
whole bench run. Check `game.log` for `DLSS Ray Reconstruction unavailable` after a native change; a quick
`LoadLibraryW` of the staged DLL from PowerShell shows the block before any game run.

## Evaluation (relight's signal, the prototype)

All runs were on the RTX 5070 Ti. The installs ran at 1920x1080 during this work: their display prefs had been
overwritten, and master now restores them. So "1080p" below means 960x540 -> 1920x1080.

**The 1440p numbers come from a cost probe.** `plugin._scale=0.6667` traces at 1280x720, exactly the user's 1440p trace.
`plugin._rrOutScale=1.3333` makes RR output 2560x1440, which is point-sampled back to the 1080p screen. That is the
user's real RR configuration, 720p -> 1440p.

Results are in `bench/results/<time>_dlss-*` (not committed). Suites: `dlss-smoke`, `dlss-eval`, `dlss-jitter`,
`dlss-cost`, `dlss-3-1`, `dlss-final`.

### Bugs found on the way (worth knowing for any RR integration here)

1. **Per-term linearisation darkened the image.** Feeding `sum(term^2.2)` and taking `^(1/2.2)` afterwards loses
   brightness wherever terms overlap, because (a^2.2 + b^2.2)^(1/2.2) < a + b. Reflective metal lost about 15% luma
   (0-1 metal: 40.5 vs 48.9). Linearising the whole composite instead fixes it (0-1 now 51.1 vs 50.4).
2. **The motion vectors carried the jitter.** Our jittered projection maps an unjittered position u to texel u - j.
   Removing last frame's jitter therefore needs +prevJ, and the current position is the sample's own u = s + 0.5 + J.
   The first version produced MVs of J - 2 prevJ on a still camera. Every edge and texel boundary shimmered (the burst
   sigma maps lit up along all edges), and motion noise was 3-4x ours. After the fix, 1-1 blast went from 4.09 to 1.05.
3. **Jitter sign.** Our J is the sample offset; DLSS takes the projection offset, so it gets -J. A balanced-order burst
   test (`dlss-jitter`, each sign twice, alternating) settled it: 0-1 metal sigma 1.02 / 1.04 with -1, 2.22 / 2.28
   with +1.
   - Unbalanced comparisons misled: in four runs, **the first RR mode after a non-RR mode in a burst list measured
     noisier**. Compare RR variants only in balanced or repeated order.
4. **V1 shadow.** Main's shadow split (docs/denoiser.md, "Moving shadows") moves V1's shadow out of the radiance into
   `_Radiance.a`. Prepare multiplies it back into RR's noisy input.

### Integration

- NGX initialises on Unity's device. The feature is created and evaluated from plugin events in both modes:
  - our own command list via `ExecuteCommandList` with state declarations (the default);
  - Unity's own list with `ModifiesCommandBuffersState`.
- No device removal, no resource-state problems, no broken composite. The GPU timer's old pitfall, where
  `ModifiesCommandBuffersState` dropped render-target bindings, did not show up: Prepare, Finish and the composite all
  follow the event and draw correctly in both modes.
- NGX loads `nvngx_dlssd.dll` 310.9.16 from the plugin folder. The driver also offers a built-in copy; ours took
  precedence.

### Image and temporal noise (`dlss-final`, rebased on master with its portal and noise fixes)

How to read the table:
- **Burst**: 48 frames held still, masked sigma (luma 0-255; lower is better).
- **Motion**: replay sigma on the 0-2 walk at 360 Hz, with `vol=0`.
- RR columns: the default, a repeat of it at the end of the mode list (to expose the order effect), and 64 Halton
  phases.

| Pose | relight (ours) | RR | RR repeat | RR, 64 phases |
|---|---|---|---|---|
| 3-1 noisy room (checkpoint load) | 0.33 | 0.34 | 0.35 | 0.35 |
| 0-1 metal | 0.58 | 1.17 | 1.27 | 1.08 |
| 8-2 portal side 1 | 0.40 | 0.61 | 0.79 | 0.76 |
| 8-2 portal side 2 | 0.97 | 1.08 | 1.18 | 1.09 |
| 0-2 v1 shadow walk, motion | 0.75 (block4 0.62) | 1.59 (1.25) | | |
| 1-1 spawn blast, motion (`dlss-eval`, alt) | 0.89 | 1.05 | | |
| 6-2 start (`dlss-eval`, alt) | 1.18 | 0.96 (4 phases) to 1.76 | | |

- **Noise.** RR is about at parity in dark, low-detail rooms (3-1) and with flashes (1-1 blast). It is 1.1-2x noisier
  than our denoiser on detailed or metallic surfaces (0-1, 8-2), and 2x in motion (0-2 walk, interior pixels included).
  Its sigma maps show broad low-frequency boil on lit walls rather than grain. ReSTIR's correlated temporal/spatial
  reuse is the likely cause; the RR guide calls it out explicitly.
- **Look.** RR resolves the pixel-art textures more crisply, with higher local contrast (7-3 floor, 0-1 walls), than our
  TAAU's exact-hit accumulation.
  - Mean luma matches within 1-2%. 7-3 was about 10% darker in `dlss-eval`: mostly floor texture contrast, plus a
    slightly different auto-exposure meter.
  - No ghosting seen on the 0-2 walk or the 1-1 blasts.
  - Thin geometry and portal planes are masked as before (`Finish`'s depth test).
  - The 8-2 portal speckle in the earlier runs was the portal noise since fixed on master; it is gone in `dlss-final`.
- **Jitter pattern.** 4 phases (our TAAU's exact 2x pattern) vs 64 Halton phases showed no consistent difference once
  order was controlled: 6-2 favoured 4, 0-1 favoured 64, the rest were within repeat noise. The default is NVIDIA's 64.
- **Preset E** (`plugin._rrPreset=5`) was not better than the default (8-2 side 1: 3.12 vs 2.05; 6-2: 1.35-1.74). Both
  were measured before the MV fix and not re-tested after it.
- **Specular hit distance** on vs off made little difference (0-1 metal: 1.09 vs 1.23 after the MV fix). It stays on.

Screenshots (game / ours / RR) are `<pose>_<t>_off.png`, `_relight.png` and `_relight_plugin__rrEnabled_1.png`:
- `20261114-194447_dlss-final`: 3-1, 0-1, 8-2 sides;
- `20261114-192659_dlss-eval_alt`: 6-2, 7-3 tree, 0-2 walk, 1-1 blast.

Sigma heat maps are `_burst_<mode>_sigma.png`.

### GPU cost (exclusive perf lock, `dlss-cost`)

| | 3-1 | 6-2 | 0-1 | 8-2 | 0-2 walk |
|---|---|---|---|---|---|
| 1080p: ours (denoise + upsample) | 0.38 | 0.39 | 0.39 | 0.38 | 0.39 |
| 1080p: RR, Prepare + eval + Finish (eval alone) | 1.18 (1.11) | 1.21 (1.14) | 1.20 (1.12) | 1.23 (1.16) | 1.20 (1.13) |
| 1080p: GPU total, ours -> RR | 2.58 -> 3.42 | 2.83 -> 3.69 | 3.32 -> 4.20 | 3.23 -> 4.08 | 2.88 -> 3.65 |
| **1440p probe**: ours (denoise at 720p + upsample) | 0.56 | 0.58 | 0.59 | 0.58 | 0.58 |
| **1440p probe**: RR (eval alone) | **1.97 (1.84)** | **2.02 (1.88)** | **2.02 (1.87)** | **1.99 (1.87)** | **2.02 (1.87)** |
| 1440p probe: GPU total, ours -> RR | 3.87 -> 5.38 | 4.27 -> 5.78 | 5.19 -> 6.53 | 4.74 -> 6.24 | 4.32 -> 5.69 |

- **RR costs about +1.35-1.5 ms of GPU time per frame at the user's 1440p.** The evaluate alone is 1.84-1.88 ms,
  against 0.6-0.7 ms for our denoiser and upsampler. NVIDIA's table (RTX 5070, 1440p Performance: 2.58-2.72 ms)
  agrees.
- In the probe our upsampler runs at 1080p (about 0.115 ms; about 0.2 ms at real 1440p), so the real gap is about
  0.1 ms smaller.
- Frame time grows less wherever the CPU is the limit (0-2 walk at 1080p: 3.72 -> 3.99 ms).

### Verdict

**Feasible, and it works.** NGX runs on Unity's D3D12 device from a BepInEx plugin, with no files in the game's own
folders and no resource-state or binding problems. Ctrl+F8 switches it live.

**For relight as it was, it is not a win.** For the relight signal our denoiser + TAAU is equal or quieter, about
three times cheaper, and closer to the game's pixel-art look. That signal is 1 spp, ReSTIR-correlated, and already
low-noise thanks to the radiance cache.

**For path tracing it is** (see "RR for path tracing" above and [path-tracing.md](path-tracing.md)): with independent
samples, remodulation and the exact grid, `pathtrace` under RR at 1/3 scale is quieter than relight at a held pose
and costs the same GPU time. RR in relight stays an option, off by default.

Problems and limits:
- Hybrid was never wired to RR (it would have needed the game's colour as input); the mode went on 2026-11-19.
- NGX's start-up (3 s) and each feature's creation run on a worker thread ("Warm-up"); before that, the first RR
  frame of a session froze the game for seconds.
- Unity-list mode needs `ModifiesCommandBuffersState`. It worked here, but given the GPU timer's history with that flag
  the own-list mode stays the default.
- Licensing blocks redistribution without NVIDIA's involvement (see "License").

## License

The DLSS SDK is under the "NVIDIA RTX SDKs" license (`lib\dlss\LICENSE.txt`; fetched, not committed). For this repo:
- **Don't commit or redistribute NVIDIA's files.** You may distribute the SDK only "incorporated in object code format
  into a software application" with "material additional functionality" and terms at least as protective as NVIDIA's,
  and never under an open-source license. `fetch-dlss.ps1` keeps the binaries out of git.
- **Release builds.** NVIDIA must be notified before a *commercial* release (license, "Notification for the DLSS SDK";
  form at developer.nvidia.com/sw-notification). Shipping the SDK requires NVIDIA attribution (marks on a splash
  screen, about box or credits), and the DLL's third-party notices go in the product's documentation. (The notices are
  Programming Guide section 9.6 "3rd Party Software", 31 March 2026 edition: curl, an SGI bitmap font, d3dx12.h, xml,
  npy, stb, DirectX-Graphics-Samples, Vulkan-Headers; 9.5 is NVIDIA's own disclaimer. This doc said 9.5 until
  2026-11-19.) The release package carries them as `NVIDIA-DLSS-THIRD-PARTY-NOTICES.txt` and NVIDIA's license as
  `NVIDIA-RTX-SDKs-LICENSE.txt` beside the DLL; the attribution is in its README and the settings page's Credits
  ([release.md](release.md)).
- **Which DLL.** `fetch-dlss.ps1` fetches `lib/Windows_x86_64/rel/nvngx_dlssd.dll` (48.3 MB at v310.9.16; the main
  checkout's `lib\dlss\version.txt` says `v310.9.16 rel`); `-Dev` fetches the `dev` one (93.5 MB, with the debug
  overlay, Ctrl+Alt+F12). What NVIDIA's documents in that SDK (checked 2026-11-19, tag v310.9.16) say about shipping:
  - *DLSS Programming Guide* (SR, 310.6.0, section 4.2 "Distributable Libraries", and the deployment checklist in 2.5):
    the release library is the one in `./lib/<plat>_<arch>/rel/`; only the `dev` DLL "MUST NOT be included or
    distributed in any public release"; the checklist asks for the "full production non-watermarked" library. The
    watermark (3.18) is in the SDK's development library, "not included in the production library".
  - *DLSS-RR Integration Guide* (August 2026), section 5.1: "The release DLL should be used in the final version of
    the application (in the rel folder)" and "do not release / ship a develop DLL". But its section 4.2 says that
    *both* the dev and rel DLLs "included in the NDA SDK package must not be included or distributed in any public
    release ... To obtain a distributable DLL, please contact NVIDIA" (the wording of the NDA-era RR package; the same
    section also calls `rel` "the release library").
  - So our earlier note ("a distributable DLL comes from NVIDIA") is what RR section 4.2 says, but the same guide and
    the SR guide name the public `rel` DLL as the one to ship. The `rel` DLL shows no watermark
    in our captures (`bench/results/*_detail-pack_alt`, pathtrace frames, 2026-11-19). Until NVIDIA answers
    (nvidia-rtx-license-questions@nvidia.com, license section 19), the safe reading for a public release: ship the
    `rel` DLL unmodified and signed, never `dev`, with the attribution and notices above, or ask NVIDIA first.
- **Open-source terms.** The license forbids putting the SDK under terms that make it redistributable as source or
  "at no charge" as a condition (restriction e), so the DLL can't sit inside a release that is licensed as a whole
  under an open-source license; a separate download or a release with its own terms for the NVIDIA files avoids that.
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
