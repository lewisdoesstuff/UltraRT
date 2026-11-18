# HDR display output

A prototype (2026-11-18), off by default. With it on, the game is presented as HDR10. The ray-traced highlights of
`relight` and `pathtrace` go above SDR white, up to the display's peak, and so do glowing surfaces (light fixtures,
screens, lava), the game's effects (explosions, fire, beams, sparks) and the glow of bloom around them, and the gun's
glowing parts (since 2026-11-18 evening, "Effects and bloom"). Everything else (the HUD, menus, the gun's muzzle flash,
the rest of the image) is the SDR image at paper white.

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
| `HDREmissive` | `-ultrart-hdr-emissive X` | 2 (1.5 before 2026-11-18 evening) | glowing surfaces and the gun's glowing parts: x their SDR value (display gamma; 2 is about 4.6x the light). 1 = as in SDR |
| `HDRLava` | `-ultrart-hdr-lava X` | 2 (was 1.5) | the same for lava and acid, which the game draws itself (DLSS RR path) |
| `HDRSky` | `-ultrart-hdr-sky X` | 1 | the same for the bright parts of the game's sky (DLSS RR path) |
| `HDREffects` | `-ultrart-hdr-effects X` | 2.5 | bright pixels the game's transparents made (explosions, fire, beams, coins, sparks; the lava overlay): x their SDR value (display gamma), rolled off towards the peak. 1 = as in SDR. Needs screen effects on |
| `HDRBloom` | `-ultrart-hdr-bloom X` | 1 | room above SDR white for bloom's glow, in display units (1 = up to twice SDR white in display gamma, or the peak). 0 = as in SDR |
| `HDRHighlights` | `-ultrart-hdr-highlights on\|off` | true | the gain map as a whole; off = the SDR image at paper white |
| | `-ultrart-hdr-white X` | 0 | composite value (display gamma, 1 = SDR white) that reaches the peak; 0 = the tone curve's white point (3). Lower = harsher |
| | `-ultrart-hdr-gamma srgb\|2.2` | 2.2 | how the SDR image is decoded |
| | `-ultrart-hdr-boost X` | 0 | frames without a gain map (hybrid, off, menus): highlights above 0.7 scaled up to 1 + X |
| | `-ultrart-hdr-debug` | | tints the pixels that got gain magenta |
| | `-ultrart-hdr-capture [raw]` | | bench: statistics of the HDR10 frame at every screenshot; `raw` also writes it |

Everything but the switch applies live (the settings menu's **HDR display** group). A config written before 2026-11-18
evening that still holds the first prototype's defaults (`HDREmissive` 1.5, `HDRLava` 1.5) is moved to the new ones
once, at the first start that is not a bench run (`[View] ConfigVersion` 2); values set afterwards stick. The native side's lines are in
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

### Effects and bloom (the late map, 2026-11-18)
The game draws its transparents (explosions, fire, muzzle beams, coins, sparks, projectiles, the lava overlay) after the
composite, into the SDR target, so the composite's map can't know them: where one was drawn over a pixel the colour test
turns that pixel's gain off. They are the most HDR-worthy content ULTRAKILL has, so a second map covers them. Rendering
the game camera in float was not an option (the palette pass and everything after it expect SDR).

The screen-effects pass ([vfx.md](vfx.md)) already runs after the transparents and has a copy of the target from right
after the composite (`_VfxPre`, before the transparents) and one after them (`_VfxLate`, before the volumetric haze). With
HDR on, its final pass is pass 4 of `Vfx.shader`: the same code as pass 3, which also writes, through a UAV, a screen-size
`ARGBHalf` map in the composite map's layout (rgb gain, a = the colour it wrote, packed):
- **Effects** (`HDREffects`): the share of the pixel's light the transparents added (luminance after over before, in
  linear light) times how bright the result is (brightest channel from 0.6 to 1, squared) scales the pixel by
  `HDREffects^2.2`. A dim glow over a bright wall has a small share and leaves the wall alone; smoke and blood darken and
  get nothing.
- **Bloom** (`HDRBloom`): the SDR bloom adds its glow under a soft shoulder that ends at white. The HDR glow is the same
  shoulder with `HDRBloom` more room (up to the peak), and the gain is the HDR value over the SDR one, only where the SDR
  result is near white (brightest channel 0.75-1) and the glow itself is strong (it added 0.03-0.15 or more). Without the
  second condition a faint glow over a brightly lit floor lifted the whole floor above white.
- **The gun** (`HDREmissive`): the viewmodel composite ([viewmodel.md](viewmodel.md)) runs later, on the HUD camera. With
  a late map this frame it draws with its pass 2, which writes the gain of the gun's glowing parts (the revolver's charge
  pips, the railcannon's) into the same map, as the composite does for glowing surfaces (`UltraRTHdrEmissive`). Gun
  pixels also get their own packed colour, so the world's gain behind them can't leak onto them.

The map is announced with the same plugin event as the composite's (`HdrFrame.flags` bit 1), from the effects pass's
command buffer; the present pass keeps the two apart and shows each pixel at the brighter of the two results. Its colour
test uses half the darkening tolerance: the map covers large areas (bloom, lava), and the game's translucent HUD panel
drawn over them must fail it. Fixed factors know nothing of the display, so the late map's result and the game-pixel
branch (`HDRLava`, `HDRSky`) are rolled off towards the peak (`SoftPeak`: the brightest channel is untouched up to halfway
between paper white and the peak, then approaches the peak) instead of clipping there, so a lava crust or a fireball
keeps its gradation. The composite's own gain already rolls off with the tone curve and is unchanged.

The SDR image does not change: pass 3 and the viewmodel's pass 0 compile to the same code as before (checked with fxc:
`fragFinal`'s and `fragPrefilter`'s bytecode are identical), pass 4 computes the same colour, and with HDR off nothing new
runs (no UAV, no extra copy).

The muzzle flash is not covered: it is a transparent on the HUD camera, drawn after the viewmodel composite, and a diff
of that camera's transparents would also catch whatever HUD the game draws there. The beam, its sparks and its light on
the world are covered.

### Per mode
| Mode | HDR content |
|---|---|
| `pathtrace`, `relight`, `pt-dn` | gain map: ray-traced surfaces and glowing surfaces up to the peak; lava and sky with DLSS RR. Late map: effects, bloom, the gun's glowing parts |
| `hybrid` | late map: effects and bloom (not the gun: hybrid's viewmodel pass blends) |
| `off`, menus, debug views | the SDR image at paper white (`-ultrart-hdr-boost` expands its highlights) |

### Auto exposure
While HDR output is active the auto exposure's key is x1.3 (docs/relight.md, "No darkening towards the game"): a
bright scene is darkened less, since its highlights have the display's headroom. SDR is unchanged.

## Differences from a native HDR pipeline
- The base image is 8 bits; only highlights get more range. Shadows have the SDR image's gradation.
- What the game draws after our composite gets its HDR from the late map, which can only scale what the SDR image shows:
  an effect that clipped to white is made brighter as a whole, not given back the colour it lost. The muzzle flash and the
  HUD stay at paper white.
- The game's palette / colour compression runs on the SDR image and the gain goes on top.
- Something drawn over a highlight in the same colour and at least as bright keeps the gain (a white HUD element over
  a white light).
- How bright glowing art and effects are in HDR is a choice (`HDREmissive`, `HDREffects`), not a measurement.
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
  without the flag has no `HDR output` line at all. Captures at paper white 240, with the defaults of then (`HDREmissive` and
  `HDRLava` 1.5, no late map; the current ones are measured under "Defaults"):

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

## Defaults (2026-11-18 evening, `t3/hdr-more`)
The user's display: 2560x1440, peak 1170 nits, paper white 250, so highlights can be 4.7x paper white in light (2.0x in
display gamma, the units of the factors). `bench/suites/hdr-defaults.json` and `hdr-fx.json` (alt and main installs,
`pathtrace` with DLSS RR, `-ultrart-hdr-paperwhite 250`, captures decoded with `tools/hdr/hdr10-view.py`; each pose in one
launch, the variants alternating). Shares are of all pixels; "at peak" = brightest channel at 99% of the peak or more,
where the gradation is gone:

| Pose | Variant | > paper white | > 2x | >= 90% of peak | at peak |
|---|---|---|---|---|---|
| 1-1 fire (pooled fire at 6 m) | no late map, emissive 1.5 | 1.19% | 0.12% | 0.062% | 0.053% |
| | effects 2, emissive 1.5 | 2.93% | 0.76% | 0.056% | 0.046% |
| | **effects 2.5, emissive 2** | 3.43% | 1.97% | 0.434% | 0.033% |
| | effects 3, emissive 2.5 | 3.71% | 2.33% | 0.875% | 0.009% |
| 1-3 pose 1 (lava river) | no late map | 0.85% | 0.04% | 0% | 0% |
| | effects 2 | 11.88% | 2.39% | 0% | 0% |
| | **effects 2.5** | 12.68% | 4.78% | 0.069% | 0% |
| | effects 3 | 13.10% | 5.67% | 0.202% | 0% |
| 0-5 lava | no late map, lava 1.5 | 4.94% | 2.54% | 0% | 0% |
| | effects 2.5, lava 1.5 | 9.44% | 7.41% | 0.173% | 0% |
| | **effects 2.5, lava 2** | 9.50% | 7.38% | 0.174% | 0% |
| | effects 2.5, lava 2.5 | 9.46% | 7.48% | 2.176% | 0% |
| 1-1 explosion (12 m) | effects 2 / **2.5** / 3 | 17.3 / 18.3 / 19.9% | 5.8 / 7.0 / 10.0% | 0.47 / 0.69 / 1.50% | 0.25 / 0.24 / 0.24% |
| 0-1 pose 1 (statue room) | emissive 1.5 / **2** / 2.5 | 1.69 / 1.72 / 1.71% | 0.53 / 0.59 / 0.60% | 0 / 0.111 / 0.194% | 0 / 0.028 / 0.083% |
| 1-4 pose 1 (lamps) | emissive 1.5 / **2** / 2.5 | 0.05% | 0.00 / 0.02 / 0.02% | 0% | 0% |
| 1-1 revolver (shot, `hdr-fx`, effects 2) | no late map / effects only / both | 2.20 / 2.87 / 2.71% | 0.17 / 0.19 / 0.35% | 0.06% | - |
| 6-2 pose 1 (red windows, first run) | either | 0% | 0% | 0% | 0% |

- **`HDREffects` 2.5**: effect cores land at 90-100% of this display's peak and the roll-off keeps them from flattening
  (the share at peak is the same at 2 and 3: what is there comes from the composite). At 2 the 1-3 lava river and the
  fire reach only about 1000 nits; 3 doubles the area within 10% of the peak. On a display with less headroom the
  roll-off scales it down by itself.
- **`HDREmissive` 2**: fixtures and screens reach the peak on small areas (0-1: 0.11% within 10% of the peak, 0.03% at
  it), so glowing art clearly reads brighter than paper white; 2.5 triples the flattened share for little more light.
  Large lamps (1-4) were already rolled off by the composite and barely change.
- **`HDRLava` 2**: with the late map most lava pixels get `HDREffects` (the game's lava overlay is a transparent; the
  colour test used to turn `HDRLava` off on it, which is why 1-3's river had gain only at its edges before). 2 matches
  `HDREmissive`; 2.5 puts 2.2% of 0-5's frame within 10% of the peak.
- **`HDRSky` 1** (unchanged): the sky is not glowing art and covers half the frame outdoors; not measured further.
- **`HDRBloom` 1**: adds 1-2 points of "above paper white" around explosions and fire (1-1 explosion 16.7% -> 18.1%,
  fire within 10% of the peak 0.05% -> 0.30%) without lifting lit surfaces.
- Saturated reds (6-2's windows, 0-3's corridor) hardly count in these luminance shares: a pure red at the peak is a
  fifth of it in luminance.

Not changed: auto exposure and the tone curve (out of scope). The explosion's first capture of a level is always much
brighter than the later ones (39% above paper white without the late map against 17-20% with it), in both runs: that
capture comes first after the level load, so treat that row as a confound, not as a loss.

**Cost** (RTX 5070 Ti, 1440p, from the same runs): the present pass takes 0.08-0.10 ms of GPU time with both maps
against 0.05-0.06 ms with the composite's alone; the screen-effects pass 0.16-0.23 ms against 0.11-0.19 ms (pass 4 and
its UAV write); the viewmodel composite is unchanged at 0.03 ms. Together about 0.07-0.09 ms per frame. None of it runs
with HDR off.

## Known gaps
- Not judged by eye yet: everything above is measured from the frame handed to DXGI, including the 2026-11-18 effects,
  bloom and defaults work (the development display is the user's, but nobody looked at it during these runs).
- The muzzle flash and other transparents the HUD camera draws on the gun stay at paper white (see "Effects and bloom").
- The late map can only make an effect brighter, not give back colour the SDR image clipped away.
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
- Lava and sky get their factor only with DLSS RR; with UltraRT's denoiser they stay at paper white, except where the
  lava's transparent overlay gets `HDREffects` from the late map (any denoiser). With the game's
  resolution downscale setting, `mainTex` is smaller than the screen: not tested.
