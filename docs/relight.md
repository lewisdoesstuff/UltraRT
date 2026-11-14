# Relight mode

`relight` is full RT (`pt-dn`) with the game's light model swapped for a physical one. Nothing is tuned per light;
everything is derived from each light's colour, intensity and range, plus a few global knobs.

| Knob | Flag | Default | Effect |
|---|---|---|---|
| Falloff | `-ultrart-relight-falloff` | 1 | 0 = ULTRAKILL's curve, 1 = inverse-square, matched to the game's brightness at half the light's range, windowed smoothly to 2x range (where the game's lights end), clamped within 0.1 m |
| Fill cut | `-ultrart-relight-fill` | 0.25 | how much of the game's unshadowed fill lights (upward-facing directional "ground bounce" fakes) to remove; 1 removes them all. See "Fill lights" below |
| Ambient cut | `-ultrart-relight-ambient` | 1 | removes the game's flat ambient term |
| Exposure | `-ultrart-relight-exposure` | 1.5 | multiplier on every point/spot/directional light (emissive triangles keep their own scale) |
| White point | `-ultrart-relight-white` | 3 | shoulder tone map in the denoiser's composite: values below the knee are untouched, the white point maps to 1, so hotspots roll off instead of clipping |
| Tone curve | `-ultrart-relight-tonemap` | `lum` | `lum` scales by luminance (the original curve); `max` scales by the largest channel (keeps hue and saturation, never clips below the white point, but dims hot saturated colours such as torch-lit orange walls a little) |
| Knee | `-ultrart-relight-knee` | 0.75 | where the shoulder starts |
| Auto exposure | `-ultrart-relight-autoexposure on\|off` | on | see below; config `[Relight] AutoExposure`; **Ctrl+F4** toggles in game |

The radiance cache uses the same light model (it shares `UltraRTLights.hlsl`), so the bounce is consistent with
the relit direct light. Shader side: `_Relight` (float4, zero = the game's model) in `UltraRTLights.hlsl`,
`_UltraRTTonemapWhite` / `_UltraRTTonemapParams` (globals) and the exposure in `UltraRTExposure.hlsl`, used by
`Denoise.compute`'s Composite.

Not done yet: per-level/per-light JSON overrides, emissive exposure, a hybrid variant.

## Auto exposure
Physical falloff plus a sun makes outdoor areas several times brighter than torch-lit rooms (mean scene luminance about
0.35-0.4 indoors in 1-1, 0.6-1.8 outside), so one fixed exposure either blows outdoor areas out to white or darkens the
rooms. Relight therefore adapts the exposure to what's on screen, like an eye or a camera:

1. `Exposure.compute` `Histogram`, after the denoiser: a 64-bin log2-luminance histogram (2^-12 to 16) of the part of
   the image that is exposed, `albedo * filtered lighting * keep + specular`, on every second pixel in x and y. Sky
   pixels, fullbright surfaces (lit weight < 0.5, from PathTrace's `_HybridLight.w`) and emission are left out, because
   they aren't exposed. The game's UI and viewmodel are drawn later by the game, so they never get in.
2. `Adapt` (one 64-thread group): the mean log2 luminance of the pixels between the 40th and 95th percentile (bright
   specks and black corners don't move it), the target `EV = log2(key) - mean`, clamped to [min, max] EV, and the
   adapted EV: `ev += (target - ev) * (1 - exp(-dt * speed))`, with a separate speed for each direction. The first frame
   after a scene load or a switch into the mode snaps straight to the target.
3. The denoiser's Composite reads the result one frame later from a GPU buffer (no CPU round trip) and computes
   `(albedo * lighting * keep + specular) * lerp(1, exposure, litWeight) + emission`, then tone maps.

Exposure only scales the lit part. Fullbright art, emission (lava, screens, glowing fixtures) and the game's own sky keep
their brightness, so a dim outdoor scene doesn't dull the palette.

| Knob | Flag | Default | Effect |
|---|---|---|---|
| Key | `-ultrart-ae-key` | 0.45 | mean luminance that is left alone. Indoor 1-1/4-1/1-4 measure 0.13-0.39, so they stay at exactly 0 EV |
| Min / max EV | `-ultrart-ae-min` / `-ultrart-ae-max` | -4 / 0 | relative to the fixed look. Max 0 means it only ever darkens: dark rooms are never lifted into flat grey. Raise max (e.g. 1) to brighten dark areas too |
| Speed | `-ultrart-ae-speed UP[,DOWN]` | 1.5,3 | 1/s, exponential in EV. Up = exposure rising (into the dark, slower, like eyes); down = into the light |
| Band | `-ultrart-ae-band LO,HI` | 0.4,0.95 | histogram percentiles that are averaged |
| Compensation | `-ultrart-ae-comp` | 0 | EV added to the target |
| pt-dn | `-ultrart-ae-ptdn` | off | also apply auto exposure and the tone map to pt-dn. Off by default: pt-dn uses the game's light model and matches its brightness by design |

Measured (bench suite `exposure`, `bench/suites/exposure.json`): 1-1 start -0.39 EV, 1-1 grass/user a-c -0.6 to -0.7,
1-1 cerberus -1.44, 1-1 user d (sunlit courtyard) -2.0, 0-5 lava -0.24; 1-1 indoor a / torch hall, 1-4 lamps / drift
(night) and 4-1 indoor A stay at 0. The bench logs the state for every relight screenshot
(`Benchmark: '<scenario>' ... relight: exposure x0.369 (-1.44 EV, target -1.44), mean lum 1.220, ...`).

Cost (RTX 5070 Ti, 1920x1080, trace scale 0.5): 0.004-0.008 ms GPU for both kernels (GPU timer slot `exposure`), plus
one extra texture read per pixel in Composite. The histogram buffer is 256 bytes.

## Fill lights
Outdoor ULTRAKILL areas are lit by a sun plus one or two directional lights shining *up* from below the horizon, which
fake ground bounce and keep shaded sides readable. In 1-1 those are `Directional Light (3)` (I 1.5, warm) and `(4)`
(I 1.0, orange), and their culling mask (0x03000000 / 0x03002000) only covers the outdoor layers 24/25, so they never
touch the torch-lit rooms. With the fill cut at 1 (the old default) the real bounce from the ground is far weaker than
these fills (roughly a tenth), so 1-1's bronze cerberus statues went dark brown with black rims while pt-dn and hybrid
looked right. The default is now 0.25: three quarters of the fill stays, unshadowed, and ray-traced shadows and bounce
come on top. Indoor exposure measurements are identical for fill 1, 0.5 and 0.25.

## HDR displays
Not possible from the mod as things stand. Evidence (2026-11-14, RTX 5070 Ti on an HDR-capable display, D3D12):
- `HDROutputSettings.main.available = False`, even though `SystemInfo.hdrDisplaySupportFlags = Supported,
  RuntimeSwitchable, AutomaticTonemapping` (the GPU, driver and display can do it). The plugin logs this line at startup
  (`HDR display: ...`).
- The reason is the player build: ULTRAKILL's `globalgamemanagers` has `allowHDRDisplaySupport = False` and
  `useHDRDisplay = False` (read with UnityPy). Unity 2022.3 only creates an HDR swapchain, and only lets
  `RequestHDRModeChange` work, when the player was built with that setting, so a plugin can't switch it on at runtime.
- Even with an HDR swapchain, the image passes through LDR targets: the camera renders without HDR into
  `PostProcessV2_Handler.mainTex`, and its PostProcessV2 pass (palette / colour compression) writes the final image.
  Our composite draws into `mainTex` (and the denoiser's output is ARGB32, saturated), so values above 1 are lost
  before post-processing. HDR
  output would need a float `mainTex`, a PostProcessV2 that keeps values above 1, the UI composited at paper white and
  a final PQ/scRGB encode, i.e. replacing the game's post-processing.

`[Display] HDR = true` (config) is accepted but only logs why HDR is unavailable. Patching `globalgamemanagers` would
be an install-time change to the game's own data (undone by every Steam update or file verify) and still leaves the
LDR chain above, so it was not attempted. Auto exposure plus the shoulder tone map is the SDR answer.

## Light fixtures
Many ULTRAKILL lights sit *inside* their fixture (torch bowls, braziers). The game has no shadows, so it never
mattered; ray traced, the bowl shadows everything below its rim, which in relight (no fill or ambient left) turns the
lower half of a torch-lit room black. When the scene is built, `SceneProxies` records the bounds of every small
renderer (< 4 m) that encloses a point/spot light. `UpdateFixtureLifts` (Plugin.cs) then moves each such light to
0.15 m above its fixture's top and gives it a soft-shadow radius of the fixture's horizontal half-size (0.08-0.5 m),
like a fire filling the bowl: the bowl still casts a soft shadow under itself, but walls and floor around it are lit.
Lights with something solid within 1 m above the fixture (ceiling lamps; a physics raycast) or more than 1.5 m below
its top stay where they are. The per-light radius goes in `_Lights[i*4+2].w` (0 = the global `_LightRadius`).
This applies in every mode; `-ultrart-nofixturelift` turns it off, `-ultrart-nofixtureshadows` removes fixture
shadows altogether.
