# HDR display output

A prototype (2026-11-18), off by default. With it on, the game is presented as HDR10. The ray-traced highlights of
`relight` and `pathtrace` go above SDR white, up to the display's peak, and so do glowing surfaces (light fixtures,
screens, lava). Everything else (the game's palette pass, HUD, menus, the viewmodel, particles) is the SDR image at
paper white.

See also: [relight.md](relight.md) ("HDR displays": why Unity itself can't do it; the tone curve), [vfx.md](vfx.md)
(bloom has its own HDR source), [gpu-timer.md](gpu-timer.md) (the native DLL and its plugin events).

## Turning it on
- Windows HDR must be on for the display the game is on (Settings > System > Display > Use HDR). UltraRT never changes
  that. With it off, the game runs exactly as without the feature.
- `[Display] HDR = true` in `BepInEx/config/ooo.lew.ultrart.cfg` (or **HDR display > HDR output** in the settings
  menu), then restart the game. `-ultrart-hdr [on|off]` on the command line wins over the config; `-ultrart-hdr force`
  uses the HDR10 swapchain even when Windows reports HDR off (testing).
- `BepInEx/patchers/UltraRT/UltraRT.Preload.dll` must be deployed (`deploy.ps1` copies it from `stage\patchers`).

| Setting (`[Display]`) | Flag | Default | |
|---|---|---|---|
| `HDR` | `-ultrart-hdr [on\|off\|force]` | false | read when the game starts |
| `HDRPaperWhiteNits` | `-ultrart-hdr-paperwhite N` | 0 | nits of SDR white: HUD, menus, everything below the highlights. 0 = what Windows shows SDR content at on this display (its "SDR content brightness"), so the game is as bright as with HDR output off |
| `HDRPeak` | `-ultrart-hdr-peak N` | 0 | nits of the brightest highlight; 0 = the peak the display reports |
| `HDREmissive` | `-ultrart-hdr-emissive X` | 1.5 | glowing surfaces: x their SDR value (display gamma; 1.5 is about 2.4x the light). 1 = as in SDR |
| `HDRLava` | `-ultrart-hdr-lava X` | 1.5 | the same for lava and acid, which the game draws itself (DLSS RR path) |
| `HDRSky` | `-ultrart-hdr-sky X` | 1 | the same for the bright parts of the game's sky (DLSS RR path) |
| `HDRHighlights` | `-ultrart-hdr-highlights on\|off` | true | the gain map as a whole; off = the SDR image at paper white |
| | `-ultrart-hdr-white X` | 0 | composite value (display gamma, 1 = SDR white) that reaches the peak; 0 = the tone curve's white point (3). Lower = harsher |
| | `-ultrart-hdr-gamma srgb\|2.2` | 2.2 | how the SDR image is decoded |
| | `-ultrart-hdr-boost X` | 0 | frames without a gain map (hybrid, off, menus): highlights above 0.7 scaled up to 1 + X |
| | `-ultrart-hdr-debug` | | tints the pixels that got gain magenta |
| | `-ultrart-hdr-capture [raw]` | | bench: statistics of the HDR10 frame at every screenshot; `raw` also writes it |

Everything but the switch applies live (the settings menu's **HDR display** group). The native side's lines are in
`BepInEx/ultrart/hdr.log` and, prefixed `HDR output:`, in the BepInEx log.

**Headroom.** What HDR can add is the ratio of peak to paper white. The development display reports a 456 nit peak and
Windows shows SDR content at 240 nits there, so highlights can be 1.9x white (0.9 stops). A lower paper white (150:
3x) or a display mode with a higher peak buys more; `HDRPeak` overrides a display that under-reports.

## How it works
Unity never knows. ULTRAKILL is built with `allowHDRDisplaySupport = false`, so Unity creates an 8-bit swapchain and
`HDROutputSettings` stays unavailable ([relight.md](relight.md), "HDR displays"). Instead the swapchain is replaced
underneath it, the way RenoDX's swapchain proxy and Special K retrofit HDR:

1. **Before the graphics device exists.** A BepInEx *preloader patcher* (`src/UltraRT.Preload`, in `BepInEx\patchers`)
   runs when Mono starts, which is before Unity creates its D3D12 device and swapchain. (Player.log suggests otherwise:
   BepInEx's lines appear there after `GfxDevice: creating device client`, but they are replayed into that log later.
   The hook does see Unity's swapchain being created.) If HDR is asked for, the patcher loads `UltraRT.Native.dll` and
   calls `UltraRT_Hdr_Install`, which patches two slots of the DXGI factory's vtable (`CreateSwapChain`,
   `CreateSwapChainForHwnd`). It patches no assembly, and with HDR off it loads nothing.
2. **The swapchain.** When Unity creates its swapchain (2560x1440, `R8G8B8A8_UNORM`, 3 buffers, flip model), the hook
   (`native/UltraRT.Native/Hdr.cpp`) checks that it is a flip-model D3D12 swapchain and that the window's display has
   HDR on (`IDXGIOutput6::GetDesc1`), then creates the real one as `R10G10B10A2_UNORM` with
   `SetColorSpace1(RGB_FULL_G2084_NONE_P2020)` (HDR10: PQ, Rec.2020) and hands Unity a `ProxySwapChain`. Any check
   failing means Unity gets its plain swapchain and nothing else runs.
3. **Unity keeps its 8-bit world.** The proxy's `GetBuffer` returns ordinary `R8G8B8A8_UNORM` render-target textures in
   the `PRESENT` state. Unity renders, transitions and "presents" them as before; `GetDesc` reports its own format.
   No Unity render target view, pipeline state or game file changes, and `ScreenCapture` (bench screenshots, the
   cinematic renderer) still reads the SDR image.
4. **Present.** One fullscreen pass on Unity's command queue, in a command list of our own: back buffer texture -> linear
   (gamma 2.2) -> x paper white -> x the frame's gain map -> clamp to the peak -> Rec.2020 -> PQ, into the real back
   buffer. Then the real `Present`, with Unity's sync interval and flags (tearing included).

### The gain map
The composite has the image before the tone curve (`Denoise.compute` Composite, `RayReconstruction.compute` Finish), so
real HDR radiance exists there; the tone curve's shoulder is what squeezes it under 1. With HDR on, the composite also
writes `UltraRTHdrGain` (`UltraRTToneCurve.hlsl`) to a float texture:
- rgb = the HDR colour over the SDR colour, per channel, in linear light. The HDR colour is the same shoulder with more
  room: identical up to the knee (0.75), then it rolls off so that the tone curve's white point (3) lands on the display
  peak instead of on SDR white. Below the knee the gain is exactly 1, so shadows and midtones are the SDR image. Per
  channel, so a highlight that clipped to white in SDR gets its colour back.
  With the `film` tone curve (the default since 2026-11-18, docs/relight.md "Highlight rolloff") the HDR colour is the
  same function with the display peak as its top: a saturated highlight keeps its pure colour for as long as the
  display has room and whitens only above that. The gain is still at least 1, so the little the SDR rolloff added to
  the other two channels stays; in linear light it is next to nothing beside the raised channel. The packed colour is
  the rolled-off SDR colour, so the present pass's hue test is unaffected. Not yet looked at on an HDR display.
- a = the SDR colour the composite wrote, packed into the one channel (`UltraRTHdrPack`: 6 bits of luma, 5 bits of
  r - b).

The plugin (`Plugin.Hdr.cs`) announces the map with a plugin event recorded right after the kernel, so the `Present`
of that same frame uses it; a frame without the event (menus, hybrid, off, debug views) has none. The present pass
multiplies the gain in, except where the finished frame is darker than the packed colour (by 0.06-0.25 in luma) or
another hue (r - b off by 0.15-0.4): there something was drawn in front (HUD, the gun, smoke), and it stays SDR.

With DLSS RR the map is at screen resolution; with UltraRT's denoiser it is at trace resolution (point sampled).

### Glowing surfaces
The game's emissive art is authored for SDR: a light fixture, a screen or lava is "white" at 1, the same as a sheet of
paper, and emission is not exposed, so the curve has nothing to expand there. In the first runs the fixtures of 1-1's
start room and 0-5's lava stayed at paper white. Two HDR-only scales fix that, neither changes the SDR image:
- `HDREmissive` (`UltraRTHdrEmissive`): where the trace sample behind a pixel is emissive (brightest channel 0.5-0.9)
  and the pixel itself is that bright, the pixel is scaled before the HDR curve. Dark texels of a glowing surface are
  left alone.
- `HDRLava`, `HDRSky`: pixels the composite doesn't write are the game's own. The camera's rays skip lava and acid
  ([lava.md](lava.md)), so those and the sky are such pixels. Finish marks them (a negative alpha: the packed colour of
  the game's frame before the composite, from a copy taken for this) with a factor for their bright part (above 0.7),
  and the present pass applies it under the same colour test, so a HUD bar over lava stays SDR. DLSS RR path only.

### Per mode
| Mode | HDR content |
|---|---|
| `pathtrace`, `relight`, `pt-dn` | gain map: ray-traced surfaces and glowing surfaces up to the peak; lava and sky with DLSS RR |
| `hybrid`, `off`, menus, debug views | the SDR image at paper white (`-ultrart-hdr-boost` expands its highlights) |

### Auto exposure
While HDR output is active the auto exposure's key is x1.3 (docs/relight.md, "No darkening towards the game"): a
bright scene is darkened less, since its highlights have the display's headroom. SDR is unchanged.

## Differences from a native HDR pipeline
- The base image is 8 bits; only highlights get more range. Shadows have the SDR image's gradation.
- What the game draws after our composite is capped at paper white: its particles, explosions, muzzle flashes, the
  viewmodel, and UltraRT's bloom (added in SDR; it has an HDR source that could feed the present pass later).
- The game's palette / colour compression runs on the SDR image and the gain goes on top.
- Something drawn over a highlight in the same colour and at least as bright keeps the gain (a white HUD element over
  a white light).
- How bright glowing art is in HDR is a choice (`HDREmissive`), not a measurement.
- Colours are Rec.709 inside the Rec.2020 container.

## Verifying
- `native\UltraRT.Native\build.ps1 -Test` builds `obj\HdrTest.exe`; `HdrTest.exe bin\UltraRT.Native.dll <folder>` runs
  the proxy without the game: it installs the hooks, makes a D3D12 device and an `R8G8B8A8` swapchain on a small window
  as Unity does, presents grey (0.5) and white, resizes, and prints the proxy's log and a capture.
- `bench.ps1 -Suite hdr-quick,hdr-look -Slot alt -Extra '-ultrart-hdr','on','-ultrart-hdr-capture','raw'` logs, for
  every screenshot, statistics of the R10G10B10A2 frame that was handed to DXGI (read back from the real back buffer
  after the pass) and the pass's cost, and writes the frame as `<screenshot>.png.hdr10.bin`.
- `python tools/hdr/hdr10-view.py <file> --paper <nits> --sdr <screenshot.png>` decodes one: luminance percentiles, the
  share of pixels above paper white, a false-colour map of where they are, and the difference to the game's own
  screenshot.

## Measured (2026-11-18, RTX 5070 Ti, 2560x1440, the display in HDR: peak 456 nits, Windows SDR white 240 nits)
- **Stand-alone test**: the app sees `R8G8B8A8`, the capture reads 240 nits for white and a 145.9 nit mean for half grey
  half white (expected 240 and 146.2), `ResizeBuffers` works, no device removal.
- **In the game** (alt and main installs, 7 launches with HDR on, 2 with it off): no crash or device removal; a launch
  without the flag has no `HDR output` line at all. Captures at paper white 240:

  | Pose | Mode | Max luminance | Above paper white | Within 10% of peak |
  |---|---|---|---|---|
  | all | `off`, `hybrid` | 240 nits | 0% | 0% |
  | 0-1 metal | `relight` | 451 | 0.145% | 0.009% |
  | 0-1 metal | `pathtrace` (DLSS RR) | 354 | 0.040% | 0% |
  | 0-1 metal | `pathtrace`, UltraRT's denoiser | 346 | 0.039% | 0% |
  | 1-1 start room | `pathtrace` | 456 | 0.98% (fixtures, the terminal's screen) | 0.059% |
  | 1-1 start room | `relight` | 456 | 1.16% | 0.057% |
  | 0-5 lava | `pathtrace` | 430 | 4.6% (the lava) | 3.1% |
  | 1-4 lamps | `pathtrace` | 456 | 0.089% (the lamp head) | 0.066% |
  | 6-2 start | `pathtrace` | 456 (one channel) | 0% | 0% |

  Before `HDREmissive` and `HDRLava`: 1-1 start room 0.15%, 0-5 lava 0% above paper white.
- **Unchanged below white**: decoded back to SDR, the HDR frame differs from the game's screenshot by 0.13-0.25 / 255
  where it is at or below paper white (10-bit PQ rounding).
- **HUD**: in the false-colour maps the HUD stays at paper white, also the health bar where it overlaps the boosted
  lava (with the luma-only test of the first version, that part of the bar was boosted).
- **Cost**: the present pass takes 0.04 ms of GPU time without a gain map and 0.055-0.066 ms with one, and 0.06-0.10 ms
  of the render thread (its own timestamps, 120-7,500 frames each). Frame times under the exclusive lock, `hdr-quick
  -Perf`, two launches each way, mean of the nine scenario-mode averages: 4.31 ms off, 4.35 ms on; single rows differ
  by up to 0.6 ms between two launches of the same build, so the difference is inside the noise.

## Known gaps
- Not judged by eye yet: everything above is measured from the frame handed to DXGI.
- HDR switched on or off in Windows while the game runs is not followed (the choice is made when the swapchain is
  created); neither is moving the window to another display.
- Exclusive fullscreen, alt-tab and in-game resolution changes go through the proxy (`SetFullscreenState`,
  `ResizeBuffers`), but only `ResizeBuffers` was exercised, and only in the stand-alone test. The bench runs are
  borderless at one resolution.
- No HDR10 metadata is set (`SetHDRMetaData`); Windows composes from the colour space alone.
- Overlays that hook `Present` (Steam, NVIDIA) draw into the HDR10 buffer themselves; not tested.
- The DXGI factory hook is a vtable patch in `dxgi.dll`'s memory for the process: another tool that wraps the factory
  (a `dxgi.dll` proxy such as ReShade or Special K in the game folder) may keep it from seeing the swapchain.
- DLSS RR is untouched (it runs on Unity's textures before all this). The offline cinematic renderer and screenshots
  capture the SDR image.
- Lava and sky get their factor only with DLSS RR; with UltraRT's denoiser they stay at paper white. With the game's
  resolution downscale setting, `mainTex` is smaller than the screen: not tested.
