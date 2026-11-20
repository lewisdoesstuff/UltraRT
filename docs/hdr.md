# HDR display output

A prototype (2026-11-18), off by default. With it on, the game is presented as HDR10. The ray-traced highlights of
`relight` and `pathtrace` go above SDR white, up to the display's peak, and so do glowing surfaces (light fixtures,
screens, lava), the game's effects (explosions, fire, beams, sparks) and the glow of bloom around them, and the gun's
glowing parts (since 2026-11-18 evening, "Effects and bloom"). Everything else (the HUD, menus, the gun's muzzle flash,
the rest of the image) is the SDR image at paper white.

Two ways to make the HDR image (2026-11-20): **native float HDR** (the default, `[Display] HDRNative`): the game's own
frame is rendered in floating point and our passes write scene light into it, with one display transform at the end
("Native float HDR"); and the older **gain map**: the 8-bit SDR frame times a per-pixel brightness factor ("How it
works" up to "Per mode"). `-ultrart-hdr-native off` (bench `plugin._hdrNative=0`) switches back to the gain map, so the
three can be compared: SDR | gain map | native.

See also: [relight.md](relight.md) ("HDR displays": why Unity itself can't do it; the tone curve), [vfx.md](vfx.md)
(bloom has its own HDR source), [gpu-timer.md](gpu-timer.md) (the native DLL and its plugin events).

## Turning it on
- Windows HDR must be on for the display the game is on (Settings > System > Display > Use HDR). UltraRT never changes
  that. With it off, the game runs exactly as without the feature.
- `[Display] HDR = true` in `BepInEx/config/ooo.lew.ultrart.cfg` (or **HDR display > HDR output** in the settings
  menu), then restart the game. `-ultrart-hdr [on|off]` on the command line wins over the config; `-ultrart-hdr force`
  uses the HDR10 swapchain even when Windows reports HDR off (testing).
- `BepInEx/patchers/UltraRT/UltraRT.Preload.dll` must be deployed (`deploy.ps1` copies it from `stage\patchers`; r2modman
  puts it in `BepInEx/patchers/<Team>-UltraRT/`, which works the same: BepInEx loads patchers from any subfolder).

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
| `HDRNative` | `-ultrart-hdr-native on\|off` | true (since 2026-11-20) | native float HDR ("Native float HDR"); off = the gain map. Applies live (the game's targets are rebuilt). Bench `plugin._hdrNative=0\|1` |
| | `-ultrart-hdr-tone channel\|max\|lum` | channel | native only: how the display transform rolls colours into the peak: per channel, by the brightest channel, or by luminance ("Native float HDR"). Bench `plugin._hdrToneMode=1\|0\|2` |
| | `-ultrart-hdr-white X` | 0 | composite value (display gamma, 1 = SDR white) that reaches the peak; 0 = the tone curve's white point (3). Lower = harsher. Native: the scene value that reaches the peak |
| | `-ultrart-hdr-hue sdr\|channel\|floor` | sdr | gain map only: colour of the composite's highlights: `sdr` = the SDR image's hue at the HDR curve's luminance and saturation (since 2026-11-20, "Highlight colour"); `channel` = the HDR curve's colour per channel; `floor` = per channel with no gain below 1 (before 2026-11-20). Bench `plugin._hdrGainMode=0\|2\|1` |
| | `-ultrart-hdr-saturation X` | 0.3 | gain map only, with `sdr`: share of the saturation the SDR rolloff took that HDR gives back (0 = the SDR colour's, just brighter; 1 = the HDR curve's). Bench `plugin._hdrSaturation` |
| | `-ultrart-hdr-gamma srgb\|2.2` | 2.2 | how the SDR image is decoded |
| | `-ultrart-hdr-boost X` | 0 | frames without a gain map (off, menus): highlights above 0.7 scaled up to 1 + X |
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
   The hook does see Unity's swapchain being created.) If HDR is asked for, the patcher finds `UltraRT.Native.dll` under
   `BepInEx\plugins` (up to 4 folders deep, preferring a folder that also holds `UltraRT.dll`, the copy the plugin loads
   later; with several installs the newest `UltraRT.dll` wins, with a warning; logged as `HDR output: using <path>`;
   until 2026-11-19 it only looked in `plugins\UltraRT\`, so a mod-manager install had no HDR), loads it and
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
  display has room and whitens only above that. Since 2026-11-20 the gain may be below 1 in a channel (what the SDR
  rolloff whitened it to does not stay), and the colour keeps the SDR image's hue ("Highlight colour" below). The packed
  colour is the rolled-off SDR colour, so the present pass's hue test is unaffected.
- a = the SDR colour the composite wrote, packed into the one channel (`UltraRTHdrPack`: 6 bits of luma, 5 bits of
  r - b).

The plugin (`Plugin.Hdr.cs`) announces the map with a plugin event recorded right after the kernel, so the `Present`
of that same frame uses it; a frame without the event (menus, off, debug views) has none. The present pass
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
  get nothing. Over a near-white image the share is replaced by how much of the room left above the pixel the effect
  filled, and pixels that show nothing of an effect inside it take its share from both sides (2026-11-20, "Effects over
  bright surfaces" below).
- **Bloom** (`HDRBloom`): the SDR bloom adds its glow under a soft shoulder that ends at white. The HDR glow is the same
  shoulder with `HDRBloom` more room (up to the peak), and the gain is the HDR value over the SDR one, only where the SDR
  result is near white (brightest channel 0.75-1) and the glow itself is strong (it added 0.03-0.15 or more). Without the
  second condition a faint glow over a brightly lit floor lifted the whole floor above white.
- **The gun** (`HDREmissive`): the viewmodel composite ([viewmodel.md](viewmodel.md)) runs later, on the HUD camera. With
  a late map this frame it draws with its pass 2, which writes the gain of the gun's glowing parts (the revolver's charge
  pips, the railcannon's) into the same map, as the composite does for glowing surfaces (`UltraRTHdrEmissive`). Gun
  pixels also get their own packed colour, so the world's gain behind them can't leak onto them.

The map is announced with the same plugin event as the composite's (`HdrFrame.flags` bit 1), from the effects pass's
command buffer; the present pass keeps the two apart and shows each pixel at the brighter of the two results, per channel,
where the late map has gain (fading in from 1 to 1.25; before 2026-11-20 on every pixel, so its result, the SDR image where
it has no gain, put back what the composite's gain had taken from a channel). Its colour
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

### Effects over bright surfaces (2026-11-20, `t3/0-3-beam-core-fix`)
The user, on the HDR display, at 0-3's beam room (`LightPillarNew` additive pillars, `coneGain` 1.6): the pillars were
flat yellow and see-through, the wall's stripes showing through them as if drawn in front, in `relight` and
`pathtrace`. SDR screenshots never showed it (bench screenshots are the SDR image; the user's were Steam F12 shots, which
Steam takes from the HDR10 swapchain and tone maps).

**Cause.** The traced room is far brighter than the game's, so behind the pillars the SDR image is near white (the hot
wall band and its strips, (255, 240-255, 190-230)). The pillar adds over that and clips, so its share of the pixel's
light, which set its `HDREffects` gain, came out small exactly there: measured in the HDR10 frame, a pillar pixel over a
dark wall got a uniform x4.4 (to the peak), over the band x4.3 / 2.4 / 2.4 (R / G / B). Red was the wall's own composite
gain (it lifts red most), green and blue the pillar's reduced effect gain, so the band showed through in pink at the
same brightness as the pillar around it.

**Change** (`Vfx.shader` pass 4, `EffectShare`; HDR only, the SDR image is unchanged):
- Where the image under the effect was bright (brightest channel 0.5-0.85 fading in), the share is at least the share of
  the room left above the pixel that the effect filled, in the channel where it filled most (`(late - pre) / (1 - pre)`,
  needing a change of 0.01-0.04 or more). A debug view of `late - pre` showed the pillar changing at least one channel
  almost everywhere over the band (green and blue; all three in its core).
- *Blind* pixels: where the image was so near white in the channels the effect adds to that nothing of it shows (a
  yellow pillar over a white strip), a pixel whose share is under 0.6, with `pre` above 0.85 and `late` above 0.9 in
  its brightest channel, takes the weaker side's share of the strongest of 12 opposite-pair taps (4 axes x 3 radii:
  0.8 %, 2.2 % and 5 % of the screen height). Inside a pillar both sides are pillar; a wall pixel beside one has it on one
  side only and keeps its own. Without this the near-camera pillars kept wall-coloured blotches.
- A first try counted only channels the effect drove to white (`late` >= 0.97); that left the pixels where it filled
  most but not all of the room, as blotches.

**Measured** (main install, the user's display: 1170 nit peak, paper white 250; `bench/suites/0-3-beams.json` 0-3 user
view / mid room / back wall and `0-3-beams-others.json` 1-4 lamps, 3-1 eye beams, 6-2 pose 1; `-ultrart-hdr-capture raw`,
before `bench/results/20261120-201923_0-3-beams` and `-202653_0-3-beams-others` (master e55513a), after
`-204914_*`). Mean luminance of the frame, nits / share above paper white / share within 10 % of the peak, before / after:

| Pose | Mode | Mean | > paper white % | >= 90 % of peak % |
|---|---|---|---|---|
| 0-3 user view | relight | 195 / 206 | 19.0 / 19.0 | 10.9 / 14.6 |
| | pathtrace | 190 / 200 | 19.1 / 19.1 | 11.8 / 15.1 |
| 0-3 mid room | pathtrace | 207 / 232 | 23.8 / 21.0 | 14.4 / 15.0 |
| 0-3 back wall (pillars beside the camera) | pathtrace | 404 / 468 | 44.2 / 44.3 | 28.4 / 41.1 |
| 1-4 pose 1 lamps | pathtrace | 11 / 11 | 0.07 / 0.07 | 0 / 0 |
| 3-1 eye beams | pathtrace | 69 / 85 | 11.7 / 12.3 | 1.4 / 5.0 |
| 6-2 pose 1 (window beams) | pathtrace | 10 / 10 | 0.02 / 0.03 | 0.04 / 0.04 |

The added area at the peak is the pillars over the walls (and 3-1's LightPillar beam over the lit floor), which now get
the same gain as the rest of the pillar. Pillar pixels over the band: x4.46 in all channels (was 4.3 / 2.4 / 2.4).
Relight and pathtrace behave the same. 1-4's glow sprites and 6-2's window beams don't change. The user confirmed the
pillars on the display ("the beam issues seem solved"). Stills: `bench/results/review/0-3-beams/`.

Left: a few faint whiter streaks in the pillars right beside the camera, where the wall behind was white in every
channel and wider than the largest tap; the pillar's core gradient is mostly gone in HDR as in SDR (the brighter traced
room clips its red and green; only its blue, the core's whiteness, varies).

### Highlight colour (2026-11-20, `t3/hdr-color-and-3-1-bright`)
The user, on the HDR display at 0-3's beam room: the hot walls pale pinkish-orange (coral) and very bright, beside the
saturated yellow pillars, where the SDR image and the game show them yellow-amber. Not a regression: a 30522b5 build
(before the day's highlight hue, flesh, beam and RR changes) gives the same HDR10 frame (upper-left wall panel 1128 / 476
/ 175 nits R / G / B, hue 18 degrees); the user had looked at 0-3 in SDR the day before. The highlight hue (0 or 0.5)
moves it by under a degree.

**Cause** (two parts, measured in the HDR10 frame; hue = the angle in linear Rec.709 light, 0 = red, 60 = yellow):
1. *The gain floor.* The composite's gain was clamped to 1 or more per channel, and the late map's result (the SDR image
   itself where it has no gain) floored every pixel at the SDR image. On a hot wall the SDR rolloff lifts blue to whiten
   it; the HDR curve, with more room, whitens far less, so its blue is below the SDR image's, and the floor kept the SDR
   blue (HDR / SDR on the wall panels: red x2.97, green x1.75, blue x1.04). Red plus that blue is the coral.
2. *Hue in linear light.* The curve works in display gamma. Its rolloff pulls a colour towards its luminance there and
   keeps the display-gamma hue, but in linear light (what the display emits) a strongly desaturated colour and a barely
   desaturated one of the same display hue are different hues: the panels' SDR colour is hue 32-33, the HDR curve's
   colour 21 (red-orange) once the floor is gone. The game's own frame shows them at 31.

**Change** (`UltraRTToneCurve.hlsl` `UltraRTHdrGain`, `Hdr.cpp` present pass; the SDR image is unchanged):
- The HDR colour keeps the SDR colour's hue (linear light, around its luminance; the highlight hue's warm turn included)
  and takes the HDR curve's luminance, plus a share (`-ultrart-hdr-saturation`, 0.3) of the saturation the SDR rolloff
  took and the HDR curve keeps (chroma around luminance, between the SDR colour's and the HDR colour's), inside the gamut
  and under the peak. Where the SDR colour is near neutral (a highlight that went to white) the HDR colour's own hue is
  used. Below the knee the two are the same colour, so nothing changes there.
- The share: all of it (first version, commit 0e8f410) made 0-3's walls rich amber but turned 3-1's beam-lit bone floor,
  cream in SDR and beige in the game, strong yellow (user). The two can't be told apart by a rule: their SDR colours are
  equally saturated (0.58 / 0.56) and HDR lifts them alike (luminance x2.1 / x1.5). The user picked 0.3 from 0 / 0.3 /
  0.6 / 1 (`bench/results/20261120-212849_hcf-s_alt`, sheets `review/hdr-color-fix/saturation-ab_*.png`).
- The composite's gain may go below 1 per channel (down to 0), and the late map's result counts only where it has gain.
  HUD, gun and smoke still fail the colour test and stay SDR.
- No brightness limit: the HDR luminance is the curve's, as before (wall panel p50 / p90 387 / 706 nits before and after).
  `-ultrart-hdr-hue channel` (per channel, no floor) and `floor` (the old map) remain for comparison.

**Measured** (alt install, 2560x1440 HDR10, Windows SDR white 380 nits, peak 1170; `pathtrace` with DLSS RR unless
noted; before = `plugin._hdrGainMode=1` in the same launch, `bench/results/20261120-211806_hcf-v_alt`, `channel` from
`-211413_hcf-a_alt`; stills `bench/results/review/hdr-color-fix/`). Hot half of each region, mean HDR colour:

| Region | Game (`off`) | SDR image | HDR before | HDR `channel` | HDR `sdr`, saturation 1 | HDR `sdr`, 0.3 (default) |
|---|---|---|---|---|---|---|
| 0-3 user view, upper-left wall panel | hue 30.6, sat 1.00 | hue 33.0, sat 0.58 | 1133 / 496 / 167 nits, hue 19.6, sat 0.85 | 1133 / 496 / 125, hue 21.4, sat 0.89 | 1004 / 540 / 111, hue 28.7, sat 0.89 | 852 / 559 / 234, hue 31.7, sat 0.73 |
| 0-3 user view, upper-right panel | 31.3 | 32.9 | hue 20.8 | 22.2 | 29.4 | 32.0, sat 0.71 |
| 0-3 user view, upper-left panel, `relight` | | 33.0 | | | 980 / 543 / 114, hue 29.7 | |
| 0-3 mid room, upper-left panel | | 31.8, sat 0.61 | 1145 / 491 / 147, hue 19.8 | | 1028 / 531 / 67, hue 28.8, sat 0.93 | 873 / 552 / 215, hue 30.8, sat 0.75 |
| 0-3 mid room, light-strip band | | 24.2 | hue 12.3 | | hue 24.0 | hue 24.2 |
| 3-1 eye beams, bone floor (lit) | | hue 30.6, sat 0.55 | 738 / 315 / 177, hue 13.6 (pink) | | 621 / 348 / 59, hue 30.9, sat 0.90 | 544 / 359 / 159, hue 31.4, sat 0.71 (0: 0.55, 0.6: 0.84) |

The HDR luminance is the same in every column (3-1 floor 384-385 nits; 0-3 panel p50 380-387).

The frames' "above paper white" shares move by under a point (0-3 user view 18.4 -> 17.7 %, 17.6 % at saturation 0.3;
mid room 22.9 -> 22.2 / 22.0 %; back wall 43.4 -> 43.2 %); 1-4 lamps, 6-2 pose 1 and 3-1 eye beams by under 0.1 (the
regression poses, the back wall and 1-1 fire were checked at saturation 1; 0.3 lies between that and the SDR colour). The pillars over the hot wall band stay
solid at the peak (back wall: as before). 3-1's bone bridge floor in front of the eye beam was pink in HDR and is now
a light cream like the SDR image. The HUD's pixels are as before (paper white). 1-1 fire: the effect's own gain is
unchanged (x3 over the flame); the composite's emissive strips lose the light the floor added (x2.6 -> x2.2 in
luminance), which was the floor's error, not the curve's.

### Per mode
| Mode | HDR content |
|---|---|
| `pathtrace`, `relight`, `pt-dn` | native (default): the whole frame in float, one display transform ("Native float HDR"). Gain map (`HDRNative` off): ray-traced surfaces and glowing surfaces up to the peak; lava and sky with DLSS RR. Late map: effects, bloom, the gun's glowing parts |
| `off`, menus, debug views | the SDR image at paper white (`-ultrart-hdr-boost` expands its highlights) |

### Auto exposure
While HDR output is active the auto exposure's key is x1.3 (docs/relight.md, "No darkening towards the game"): a
bright scene is darkened less, since its highlights have the display's headroom. SDR is unchanged.

## Native float HDR (2026-11-20, `t3/hdr-native-float`)
The gain map grafts HDR onto a finished 8-bit frame, and everything drawn after our composite needs its own rule to keep
its brightness (the late map, the effect floors, the hue and saturation fixes above). Native float HDR drops that: the
game's own frame is a half-float texture, every pass writes light into it without clamping, and one display transform
at the end decides how scene light lands on the display. The SDR path (HDR output off) does not change; nor does the
gain map (`HDRNative` off), which is kept for comparison.

**The game's target.** `PostProcessV2_Handler.SetupRTs` creates `mainTex` and `reusableBufferB` (which HeatWaves
copies to and from with `CopyTexture`, so the two must match) as `ARGB32`. A Harmony transpiler
(`HdrGameTarget` in `Plugin.Hdr.cs`, installed only when the HDR swapchain is) replaces the format constant before
those two `new RenderTexture(w, h, depth, format)` calls with a call that returns `ARGBHalf` while native HDR is wanted
and `ARGB32` otherwise. Switching (the setting, a bench variant, HDR highlights on or off) sets the handler's
`reinitializeTextures`, so the game rebuilds its targets the way it does after a resolution change: it drops the
command buffers on the main camera (ours are re-added from `onReinitialize`) and our own copies of the frame follow
its format. Portal views render straight into `mainTex`, so they are float too (8-3 checked: the portal views are
intact). Log: `HDR output: game target ARGBHalf (native float HDR)`.

**What is written into it: scene values.** Every writer puts light in display units (1 = paper white, gamma 2.2 like
the SDR image, nothing clamped at 1):
- The composite (UltraRT's denoiser, DLSS RR's finish and the remodulated path): the tone curve's toe only, so darks and
  midtones are exactly the SDR image's; above that the light itself, no shoulder. Glowing surfaces get `HDREmissive`,
  as in the gain map.
- Lava, acid and the sky (DLSS RR): the game's colour from before the composite, its bright part (brightest channel
  0.7 to 1) scaled by `HDRLava` / `HDRSky`.
- The gun (viewmodel composite pass 2), water and glass reflections (TransparentOverlay): the same scene values.
- The game's transparents (explosions, fire, beams, sparks) draw over that themselves, in float. Screen effects
  pass 5 (`Vfx.shader`, `fragFinalNative`) then expands what they added: the difference to the frame before them,
  `d`, gets `c += d * (HDREffects - 1) * t^2` with `t = smoothstep(0.6, 1, brightest channel of d)`, so only the hot
  part of an effect is pushed (a dim smoke wisp is untouched). Bloom is a plain linear add on top (linear light, no
  shoulder, no headroom cap); as in SDR (pass 3) there is less of it on pixels that are already bright, so the glow goes
  around a light rather than onto it.
- Every pass that reads the frame sanitises it with `min(max(x, 0), 64)` (D3D's min and max drop NaN; `isnan` can be
  compiled away by fxc), so no NaN, infinity or negative value from any game or mod shader reaches the display.

**The display transform** (`UltraRTHdrDisplay` in `UltraRTToneCurve.hlsl`), in linear light: identity up to the SDR
curve's knee (0.75 display), so everything below the highlights is the SDR image; above it an extended Reinhard
shoulder (slope 1 at the knee) that brings the tone curve's white point (3, `-ultrart-hdr-white`) to the display's peak.
What reached SDR white reaches the peak, and nothing passes it. Three ways to apply it to a colour
(`-ultrart-hdr-tone`):
- `channel` (default): each channel through the shoulder. Hot colours turn towards yellow and white, as they do in SDR
  and in the game: 0-3's beams stay yellow, walls keep their saturation.
- `max`: by the brightest channel, the colour scaled with it: hue and saturation kept exactly.
- `lum`: by luminance, a colour that would pass the peak pulled towards its luminance. Saturated highlights whiten:
  0-3's beams came out pure white (1163 nits in all three channels), so it is not the default.

**Hand-off.** At the game's Virtual Camera's `onPreRender` (camera depth 1, after the main camera with its outlines
and the HUD camera, before the game's final pass) the `HdrScene` kernel (`Denoise.compute`) runs the sanitise and the
display transform into `_hdrSceneTex`, which is copied back into `mainTex` for the game's final pass (its colour
compression and dither, then the 8-bit back buffer: screenshots and the cinematic renderer see that), and the texture is
handed to the native present pass with `HdrFrame` flag bit 2. Present (`Hdr.cpp`) reads both: where the finished 8-bit
pixel matches the float frame saturated (largest channel difference below 0.025, fully above 0.08) it shows the float
pixel, with the game's colour compression (`_ColorPrecision`) and its `_Gamma` applied the way the final pass does,
clamped to the peak with its hue kept; elsewhere something was drawn over the frame (HUD, menus, Screen Space Overlay
UI) and the 8-bit pixel shows at paper white, as with the gain map. The gain map and the late map are not made.

**What it fixes against the gain map** (see "Differences from a native HDR pipeline", which is about the gain map):
effects keep the colour SDR clipped away, since they are drawn in float; shadows and midtones have half-float
gradation (the output dither is off in native mode); no floor, hue or saturation rules are needed.

### Measured (2026-11-20, RTX 5070 Ti, 2560x1440, HDR10, peak 1170 nits, paper white 250)
0-3, `pathtrace` with DLSS RR, gain map and native in the same launch (run `20261120-222657_hdrn-a`; luminance of the
HDR10 frame):

| Pose | Mean, gain map / native | At least 90 % of the peak, gain map / native |
|---|---|---|
| user view | 202 / 193 nits | 3.0 / 4.6 % |
| mid room | 221 / 210 nits | 3.5 / 4.9 % |
| back wall | 468 / 444 nits | 15.3 / 18.2 % |

The rooms' midtones are the same as the gain map's (a few percent lower: no lift from the gain map's floor) and more
of the hot walls and beams reach the peak; the beams are yellow, the walls keep their colour. Review sheets (SDR | gain
map | native; peak-scaled and paper-white-exposed rows): `bench\results\review\hdr-native\final_*.jpg` and
`final_stats.txt` in the main checkout. 1-1 fire, 1-4 lamps and 8-3 portals were checked in earlier runs of the branch;
the first capture after a level load is brighter in both modes (see "Effects over bright surfaces"), so its row is a
confound.

**Cost** (exclusive lock, main install, GPU timer total, ms, same launch; runs `20261120-223227_hdrn-perf` and, SDR,
`20261120-223308_hdrn-perf` with `-ultrart-hdr off`):

| Pose | Mode | SDR | Gain map | Native |
|---|---|---|---|---|
| 0-3 user view | `pathtrace` | 5.28 | 5.37 | 5.44 |
| 0-3 user view | `relight` | 5.15 | 5.14 | 5.23 |
| 1-1 start | `pathtrace` | 4.93 | 5.21 | 5.26 |
| 1-1 start | `relight` | 5.23 | 5.24 | 5.32 |

Native costs about 0.05-0.09 ms more than the gain map: the `HdrScene` kernel and copy, the half-float target (twice
the bytes for every game pass that touches `mainTex`), and the present pass reading two textures (0.08-0.09 ms against
0.05 ms). The SDR column is another launch, so its difference to the HDR columns is within noise.

### Known gaps (native)
- Not judged on the display yet: everything is measured from captured HDR10 frames.
- Paper white: the captures used 250 nits while Windows shows SDR content at 380 nits on the display they were taken on,
  so HDR looks dimmer overall than SDR there. `HDRPaperWhiteNits` 0 (the default) follows Windows; set a value only on
  purpose.
- The game's 8-bit screenshot in native mode is the display-transformed float frame through the final pass (so
  highlights clip at SDR white): fine for the bench, but not the SDR image of an SDR run.
- The remodulated finish (`RemodFinish`, DLSS RR without the normal finish) writes scene values without `HDREmissive`.
- `PortalRenderV2`'s own composite colour target stays `ARGB32`; portal views are drawn into `mainTex` and were fine in
  8-3, so it was left alone.
- A full-screen flash, the hurt vignette or anything else drawn over the frame after the game's final pass falls back
  to the 8-bit pixel at paper white while it covers a pixel (the present pass sees a mismatch).
- `-ultrart-hdr-hue`, `-ultrart-hdr-saturation`, `HDRBloom`'s headroom and the late map do nothing in native mode.

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
- `python tools/hdr/hdr10-view.py <file> --paper <nits> --sdr <screenshot.png> [--peak <nits>]` decodes one: luminance
  percentiles, the share of pixels above paper white, a false-colour map of where they are, the difference to the game's
  own screenshot, and `<name>.peak.png`, the frame scaled so the peak is white (what is brighter than what on the
  display, with hue: an effect against the wall behind it).
- **The game must be on the HDR display.** The bench window opens on the display Unity remembers; on 2026-11-20 it opened
  on an SDR one at 1920x1080 (`hdr.log`: `HDR output not used: HDR is off for the game's display`) and wrote no HDR10
  frame. Add `'-monitor','1','-screen-width','2560','-screen-height','1440'` to `-Extra` (bench.ps1 puts the user's
  display prefs back afterwards). Pass `-Extra` from PowerShell: through bash, `powershell -File bench.ps1 -Extra
  'a','b'` arrives as one string and the flags are ignored.

## Measured (2026-11-18, RTX 5070 Ti, 2560x1440, the display in HDR: peak 456 nits, Windows SDR white 240 nits)
- **Stand-alone test**: the app sees `R8G8B8A8`, the capture reads 240 nits for white and a 145.9 nit mean for half grey
  half white (expected 240 and 146.2), `ResizeBuffers` works, no device removal.
- **In the game** (alt and main installs, 7 launches with HDR on, 2 with it off): no crash or device removal; a launch
  without the flag has no `HDR output` line at all. Captures at paper white 240, with the defaults of then (`HDREmissive` and
  `HDRLava` 1.5, no late map; the current ones are measured under "Defaults"):

  | Pose | Mode | Max luminance | Above paper white | Within 10% of peak |
  |---|---|---|---|---|
  | all | `off`, `hybrid` (removed 2026-11-19) | 240 nits | 0% | 0% |
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
- 0-3's coral walls: fixed 2026-11-20 ("Highlight colour"). An earlier try that kept the SDR hue but raised saturation
  only part of the way towards the HDR colour's was "very bright, but rather pale" (paper white 250 then); the fix
  takes the hue in linear light and 0.3 of the saturation, picked against 3-1's floor. How bright hot walls get (a fifth of 0-3's frame above paper white) is the curve's and was not changed: the user
  asked for no brightness cap. Not yet judged on the display.
- 3-1 (the eye-beam staircase, 2026-11-20) is too bright against the game in SDR as well, so it is not an HDR matter. It
  is the same in a 30522b5 build, and flesh scatter, the highlight hue and RR's front emitters change nothing there.
  Auto exposure's game match reads the traced surfaces 2.7 EV above the game's (`game -4.1 vs RT -1.45 log2`) and, with
  its down side off since 2026-11-18, does not correct it. Left for a separate look (the user: the game's denser haze
  there is part of it).
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
- Dither: the game's screen dither is off while traced and UltraRT's own +-0.5-step output dither applies in HDR too,
  since the image under the gain is 8-bit (measured: 0.4-4.7 % banded tiles in HDR10 captures without any dither,
  0.24-0.37 % with ours; [ultrakill-rendering.md](ultrakill-rendering.md), "Final post-process: dither").
- DLSS RR is untouched (it runs on Unity's textures before all this). The offline cinematic renderer and screenshots
  capture the SDR image.
- Lava and sky get their factor only with DLSS RR; with UltraRT's denoiser they stay at paper white, except where the
  lava's transparent overlay gets `HDREffects` from the late map (any denoiser). With the game's
  resolution downscale setting, `mainTex` is smaller than the screen: not tested.
