# Relight mode

`relight` is full RT (`pt-dn`) with the game's light model swapped for a physical one. Nothing is tuned per light;
everything is derived from each light's colour, intensity and range, plus a few global knobs.

| Knob | Flag | Default | Effect |
|---|---|---|---|
| Falloff | `-ultrart-relight-falloff` | 1 | 0 = ULTRAKILL's curve, 1 = inverse-square, matched to the game's brightness at half the light's range, windowed smoothly to 2x range (where the game's lights end), clamped within 0.1 m |
| Fill cut | `-ultrart-relight-fill` | 1 | how much of the game's unshadowed fill lights (upward-facing directional "ground bounce" fakes) to remove; 1 removes them all (sky light and bounce replace them). See "Fill lights" below |
| Ambient cut | `-ultrart-relight-ambient` | 1 | removes the game's flat ambient term |
| Exposure | `-ultrart-relight-exposure` | 1.5 | multiplier on every point/spot/directional light (emissive triangles keep their own scale) |
| White point | `-ultrart-relight-white` | 3 | shoulder tone map in the denoiser's composite: values below the knee are untouched, the white point maps to 1, so hotspots roll off instead of clipping |
| Tone curve | `-ultrart-relight-tonemap` | `lum` | `lum` scales by luminance (the original curve); `max` scales by the largest channel (keeps hue and saturation, never clips below the white point, but dims hot saturated colours such as torch-lit orange walls a little) |
| Knee | `-ultrart-relight-knee` | 0.75 | where the shoulder starts |
| Toe | `-ultrart-relight-toe S[,END]` | 0 (end 0.25) | optional filmic toe below END: luminance v becomes `lerp(v, v^2/END * (2 - v/END), S)` (same value and slope at END, slope 1 - S at black), for deeper shadows. Off: at 0.3 it changed the bench poses by under 2% |
| Auto exposure | `-ultrart-relight-autoexposure on\|off` | on | see below; config `[Relight] AutoExposure`; **Ctrl+F4** toggles in game |
| Linear transport | `-ultrart-relight-linear on\|off` | on | light transport on linear values; see "Sky and GI" |
| Sun disk | `-ultrart-sun-angle DEG` | 0.5 | angular radius of shadowed directional lights (soft sun shadows); 0 = hard |
| Sky sampling | `-ultrart-sky-sampling on\|off` | on | one sky light sample with a visibility ray per pixel |
| Sky scale | `-ultrart-sky-scale F` | 1 | sky light multiplier (1 = the sky lights the level as brightly as it is drawn) |
| Light reference | `-ultrart-relight-ref F` | 0.3 | linear transport: the fraction of a light's range at which the inverse-square falloff equals the game's curve; see "Light reference distance" |

**Ctrl+F5** toggles linear transport, sun disk, sky sampling and sky scale together (A/B against the gamma-space
relight; the light reference only applies with linear transport).

The radiance cache uses the same light model (it shares `UltraRTLights.hlsl`), so the bounce is consistent with
the relit direct light. Shader side: `_Relight` (float4, zero = the game's model) in `UltraRTLights.hlsl`,
`_UltraRTTonemapWhite` / `_UltraRTTonemapParams` (globals) and the exposure in `UltraRTExposure.hlsl`, used by
`Denoise.compute`'s Composite. The tone curve itself is `UltraRTToneCurve.hlsl`, shared with the water/glass
reflection overlay (`TransparentOverlay.shader`), so the two can't drift apart.

Not done yet: per-level/per-light JSON overrides, emissive exposure, a hybrid variant. Per-area colour: see "Area colour".

## Auto exposure
Physical falloff plus a sun makes outdoor areas several times brighter than torch-lit rooms (mean scene luminance about
0.35-0.4 indoors in 1-1, 0.6-1.8 outside), so one fixed exposure either blows outdoor areas out to white or darkens the
rooms. Relight therefore adapts the exposure to what's on screen, like an eye or a camera:

1. `Exposure.compute` `Histogram`, after the denoiser: a 64-bin log2-luminance histogram (2^-12 to 16) of the part of
   the image that is exposed, `albedo * filtered lighting * keep + specular`, on every second pixel in x and y. Sky
   pixels, fullbright surfaces (lit weight < 0.5, from PathTrace's `_HybridLight.w`) and emission are left out, because
   they aren't exposed. The game's UI and viewmodel are drawn later by the game, so they never get in.
2. `Adapt` (one 64-thread group): the mean log2 luminance of the pixels between the 40th and 95th percentile (bright
   specks and black corners don't move it), smoothed over time (the *meter*, 4/s). The target:
   - meter above the key: `EV = log2(key) - meter` (full darkening: sunlit areas are pulled down to the key);
   - between the dark key and the key: 0 (rooms keep the look of their own lights);
   - below the dark key: `up * (log2(keyLow) - meter)`, part of the way up, so a very dark place gets readable but stays
     darker than a lit one;

   clamped to [min, max] EV. Then the adapted EV: `ev += aim * (1 - exp(-dt * speed))`, with a separate speed for each
   direction, where `aim` is the error minus a dead band (0.05 EV): a target that wanders by less than that (noise,
   flickering torches, small camera moves) never moves the exposure. The first frame after a scene load or a switch into
   the mode snaps to the target; for the next 0.5 s it follows at 8/s with no dead band (*settle*), because that first
   frame comes from a freshly reset denoiser and radiance cache (see "Flicker" below).
3. The denoiser's Composite reads the result one frame later from a GPU buffer (no CPU round trip) and computes
   `(albedo * lighting * keep + specular) * lerp(1, exposure, litWeight) + emission`, then tone maps.

Exposure only scales the lit part. Fullbright art, emission (lava, screens, glowing fixtures) and the game's own sky keep
their brightness, so a dim outdoor scene doesn't dull the palette.

| Knob | Flag | Default | Effect |
|---|---|---|---|
| Key | `-ultrart-ae-key` | 0.45 | mean luminance above which the exposure darkens (to the key) |
| Dark key | `-ultrart-ae-key-low` | 0.1 | mean luminance below which it brightens. Between the two nothing changes: indoor 1-1/4-1/1-4/0-1 measure 0.22-0.34 with the default light reference |
| Up strength | `-ultrart-ae-up` | 0.5 | fraction of the way to the dark key a darker scene is lifted (0 = never brightens) |
| Min / max EV | `-ultrart-ae-min` / `-ultrart-ae-max` | -4 / 1 | relative to the fixed look |
| Speed | `-ultrart-ae-speed UP[,DOWN]` | 1.5,3 | 1/s, exponential in EV. Up = exposure rising (into the dark, slower, like eyes); down = into the light |
| Dead band | `-ultrart-ae-deadband` | 0.05 | EV; the exposure stops this far short of the target |
| Meter | `-ultrart-ae-meter` | 4 | 1/s smoothing of the measured luminance before the target (0 = raw) |
| Settle | `-ultrart-ae-settle S[,SPEED]` | 0.5,8 | after a reset: seconds of fast adaptation (1/s), no dead band |
| Band | `-ultrart-ae-band LO,HI` | 0.4,0.95 | histogram percentiles that are averaged |
| Compensation | `-ultrart-ae-comp` | 0 | EV added to the target |
| pt-dn | `-ultrart-ae-ptdn` | off | also apply auto exposure and the tone map to pt-dn. Off by default: pt-dn uses the game's light model and matches its brightness by design |

Measured with the current defaults (bench suite `relight-polish`, the 13 poses of `exposure.json` plus 0-1 metal, 0-3
ghosts and 8-3 b): 1-1 start -0.19 EV, user a/b -0.3, user c -0.47, grass -0.5, cerberus -1.0, user d (sunlit
courtyard) -1.1, 0-5 lava -0.35, 0-3 ghosts -0.7, 8-3 b -0.4, 1-4 drift (moonlit bridge) -0.18; 1-1 torch hall /
indoor a, 1-4 lamps, 4-1 indoor A and 0-1 metal stay at 0 (mean 0.22-0.34). The bench logs the state for every relight
screenshot (`Benchmark: '<scenario>' ... relight: exposure x0.369 (-1.44 EV, target -1.44), mean lum 1.220, ...`), and
bursts log a per-frame trace (`Burst AE '<scenario>' <mode>: settle ... EV; capture N frames: EV mean sd [min,max]
step ...; target ...; band mean log2 ...; meter ...`).

### Game match
User reports (2026-11-14 evening): relight was "still a touch too bright" in many levels, while 8-2's start was "far too dark" against RT off. Both are global level differences against the game that one fixed key can't follow: 8-2's alley is lit in game by lights shining through walls (the game has no shadows; making its directional lights unshadowed changed almost nothing), torch rooms and 4-1 are brighter by the light reference and the dark-scene lift.

So auto exposure also meters **the game's own frame**: `BindGameMatch` (Plugin.Exposure.cs) copies `PostProcessV2_Handler.mainTex` at the RT injection point (the game's opaques and sky, before our composite), and `Exposure.compute`'s Histogram bins, next to the usual histogram, the RT luminance (before haze) and the game's luminance at the same pixels, near ones only (game fog factor under `gameMatchFog`, 0.3: farther, the game's fog replaces the colour). `Adapt` takes the band mean of both (same 40-95% percentiles) and compares the game's with the RT image as it will be exposed (RT mean + target). Beyond a dead band of `gameMatchBand` (0.35 EV), a share of the difference moves the target: `gameMatchUp` (0.75) where RT is darker than the game, `gameMatchDown` (0.4) where it is brighter, so lit rooms may stay brighter than the game (the RT look) but less so. The correction is smoothed like the meter and the target still clamps to [min, max] EV. It needs at least a quarter of the measured pixels near; otherwise there is no correction. Bench lines show it: `game match: game -1.98 vs RT -3.39 log2 over 123906 near px, correction +0.76 EV`. Flags: `-ultrart-ae-match UP[,DOWN]` (0 = off), bench `ae.gameMatchUp`, `ae.gameMatchDown`, `ae.gameMatchBand`. Cost: a copy of the colour target (only in relight) and two more histograms in the same pass.

Mean screen luma, game / relight before / relight now (game match and the haze rebalance of docs/volumetrics.md "Balance"; HUD and viewmodel left out; `bench/suites/exposure-pm2.json`, `exposure-pm3.json`):

| Pose | game | before | now | before / game | now / game |
|---|---|---|---|---|---|
| 0-2 blood river | 34.7 | 53.3 | 50.7 | 1.54 | 1.46 |
| 0-2 door red metal | 27.3 | 29.3 | 28.3 | 1.07 | 1.04 |
| 1-3 start water | 78.0 | 88.2 | 86.8 | 1.13 | 1.11 |
| 3-1 noisy room (checkpoint) | 19.3 | 24.5 | 21.2 | 1.27 | 1.10 |
| 4-4 v2 chair (checkpoint) | 36.7 | 75.9 | 60.9 | 2.07 | 1.66 |
| 6-2 gabriel wings | 15.0 | 29.3 | 25.0 | 1.95 | 1.67 |
| 6-2 start | 11.0 | 28.6 | 21.6 | 2.60 | 1.96 |
| 8-2 a | 61.4 | 34.9 | 46.6 | 0.57 | 0.76 |
| 8-2 b | 26.9 | 50.5 | 44.4 | 1.88 | 1.65 |
| 8-2 c | 92.3 | 73.4 | 82.2 | 0.80 | 0.89 |
| 8-2 portal side 1 | 56.8 | 95.2 | 83.7 | 1.68 | 1.47 |
| 1-1 start | 181.1 | 182.4 | 180.7 | 1.01 | 1.00 |
| 1-1 indoor a (torches) | 50.5 | 87.9 | 81.5 | 1.74 | 1.61 |
| 1-4 lamps | 23.4 | 31.2 | 27.9 | 1.33 | 1.19 |
| 1-4 drift | 34.0 | 53.5 | 43.8 | 1.57 | 1.29 |
| 4-1 indoor A | 13.9 | 49.4 | 36.5 | 3.55 | 2.63 |
| 7-3 flashlight | 21.9 | 28.6 | 27.3 | 1.31 | 1.25 |
| 7-3 tree a | 7.1 | 100.8 | 11.5 | 14.20 | 1.62 |
| 7-3 tree b | 16.2 | 58.9 | 31.9 | 3.64 | 1.97 |
| P-2 vent smoke | 18.8 | 21.8 | 16.0 | 1.16 | 0.85 |

"Before" is the same build with `ae.gameMatchUp=0+ae.gameMatchDown=0+vol.strength=1+vol.maxAdd=0` (and, for 7-3 and 1-4, `vol.darkFog=0`), measured in the same run. Torch rooms, 6-2's start and 1-4's lamps keep their RT look and stay brighter than the game (1.2x to 2x), just less so; 8-2's alley comes up from 0.55x to 0.73-0.76x; 4-1's green corridor (light reference, docs "Light reference distance") is still the brightest departure.

### Area colour
User reports (2026-11-14 late): relight lost the per-area colour of several levels. 2-2 (all of layer 2) had no purple
tint; 7-3's tree room was brighter than the game, with white walls instead of red; 6-1's candles were pure white and its
corridors less red; 8-2's sunny street was dark brown with hard shadows where the game shows pink-red and blue-violet
facades. Bench suites: `colour-diag` and `colour-diag2` (diagnostic variants), `colour-test` (the poses) and
`colour-regress` (1-1 start, 0-1 metal, 6-2 start, 0-5 lava).

**What it was not.** The suspected common cause, per-area `RenderSettings` read once at load, isn't there. Ambient is
black (flat mode) in every one of these levels. Fog colour and distances are read every frame (docs/volumetrics.md), the
sky material every second and the lights every frame. The game's colour-grading globals (`_LUT`, `_PaletteTex`,
`_HeightFog`, `_AmbientStrength`) are unset or 0 in all of them. Every bench screenshot now logs the area's lighting for
this kind of question (`LogAreaLighting`, Plugin.cs): `Area lighting '<pose>'` (ambient, sky colours, shader globals, fog,
skybox, RT sky), each directional light (`area sun`), the 10 point/spot lights that reach the camera most in the game's
model, and the game-unlit or emissive Master materials in view with their fog and emission settings.

**Causes, per report.**
- *2-2 purple, 7-3 red, 6-1 red.* The cause is lighting, not stale data. Every 2-2 light has zero green (magenta and
  violet directionals) and 6-1's are pure red, but relight adds neutral light that the game doesn't have there. The
  `colour-diag2` variants trace it to the direct light of whitish lights under the physical falloff: with
  `plugin._relightFalloff=0` (the game's curve) the mean green drops from 37 to 29 in 2-2 and from 21 to 13 in 7-3, while
  sky off, emissive off and cache off each change it by 0-3. Relight's inverse square with the light reference (see "Light
  reference distance") is 2-3x the game's brightness near the end of a light's range. So 7-3's white spot lights (range
  30-40, 30-40 m away) wash its walls white where the game's red tree light dominates. Changing the falloff to fix this
  would undo the torch rooms.
- *6-1 candles.* A material bug: see docs/scene-proxies.md, "Albedo as emissive in a lit material" (the candles were
  traced as white emitters) and "Emissive mask" (the furnace coffins glowed over their whole front).
- *8-2 time of day.* Two causes. First, the street's sky is `Exteriors/DaySkybox`, an `ULTRAKILL/Skybox/Skybox` dome
  with a cubemap, and relight only recognised `Skybox/Panoramic`, so the street had no sky light at all (the log's
  `sky table 'nightsky 2'` was a stale name left over from 7-3). Second, the game's day look comes from two directional
  lights 5 degrees above the horizon, orange from one side and blue-violet from the other, beside the pink sun (35
  degrees). Unshadowed in the game, they colour the two rows of facades. Relight shadowed them, the street canyon blocked
  both, and the colour was gone.

**Fixes.**
1. *Colour match* (auto exposure; relight only). Next to the game match's histograms, `Exposure.compute` sums the RT
   colour (pre-haze, `albedo x lighting x keep + specular`) and the game's colour over the same near pixels (fixed point,
   6 counters after the histograms). `Adapt` normalises both means to their luminance and compares them per channel in
   log2. Beyond a dead band (`colorMatchBand` 0.2), the difference becomes a per-channel gain on the lit part (strength
   `colorMatch` 1, clamped to `colorMatchMax` 2 log2). The gain is rescaled so the RT mean keeps its channel average:
   keeping its luminance instead pushed a red room's green back up, because green is 72% of luminance. A channel under 15%
   of the brightest one in both images is corrected in proportion: 0-5's blue and 6-2's green have large but meaningless
   log ratios, and correcting them fully would tint lit objects of another colour there. The gain is smoothed like the game
   match, held while nothing is measurable, and stored in `_Exposure[3]`. The denoiser's Composite multiplies
   `(albedo x lighting x keep + spec) x exposure` by it, weighted by the lit weight (`UltraRTPixelBalance`,
   UltraRTExposure.hlsl). Emission, fullbright art, the sky and the game's own pixels are never touched. Flags:
   `-ultrart-ae-color STRENGTH[,BAND[,MAX]]` (0 = off); bench `ae.colorMatch`, `ae.colorMatchBand`, `ae.colorMatchMax`.
   Bench exposure lines end with `colour match gain (r, g, b)`. The DLSS-RR path applies it too (docs/dlss.md).
2. *Lit albedo-as-emissive and emissive masks*, drawn as the game draws them (docs/scene-proxies.md).
3. *Sky.* `ULTRAKILL/Skybox/Panoramic` and `ULTRAKILL/Skybox/Skybox` in cubemap mode (cubemap x `_SkyColor`) now count as
   skies (`IsSkyMaterial`). A cubemap is converted once into a 1024x512 lat-long target (`Hidden/UltraRT/SkyConvert`), one
   per cubemap so the sky table rebuilds when the sky changes. It is then used like a panoramic sky for sky light, misses
   and reflections. When the active sky renderers use different materials, the one horizontally nearest the camera wins (0
   inside its footprint): 8-2's street gets the day dome, its far side the night backdrop (`MirrorHider`, a wall at x 167).
   Levels with a single sky material are unchanged.
4. *Low side lights.* A directional light less than 10 degrees above the horizon that isn't the level's main sun (the
   brightest directional from above) is unshadowed and kept by the fill cut (flag 0.75, as with `_sunsUnshadowed`), and
   volumetrics don't take it as their sun. 1-1's directionals (a sun at 50 degrees, two fills from below) and the indoor
   regress poses have none. Bench `plugin._lowSideLightDeg=0` gives the old model. This applies to hybrid too: 8-2 hybrid
   now colours both facades as well.

**Results.** Mean screen colour without the HUD and viewmodel: game / master relight / branch relight. Sources:
`colour-diag`, `colour-test` and `colour-regress`, plus merge-regress 20261114-200713 for master's regress poses.

| Pose | game | master | branch | gain |
|---|---|---|---|---|
| 2-2 floor noise purple | 53,21,57 | 51,37,61 | 59,26,63 | (1.21, 0.64, 1.04) |
| 6-1 candles | 39,1,1 | 48,9,9 | 56,2,1 | (1.14, 0.48, 0.49) |
| 6-1 coffins | 65,3,1 | 89,17,10 | 86,7,2 | (1.04, 0.76, 0.81) |
| 7-3 tree room tint | 49,6,3 | 55,21,20 | 73,16,10 | (1.48, 0.73, 0.45) |
| 8-2 intro day | 114,59,64 | 66,41,43 | 92,43,47 | (0.99, 0.99, 1.02) |
| 1-1 start | 179,193,176 | - | 177,191,178 | (1.00, 1.00, 1.00) |
| 0-1 metal | 96,36,16 | 129,47,19 | 127,46,19 | (1.00, 1.00, 1.00) |
| 6-2 start | 56,1,1 | 90,6,4 | 92,4,3 | (1.08, 0.48, 0.65) |
| 0-5 lava | 112,57,1 | 130,67,3 | 130,67,3 | (1.00, 1.00, 0.78) |

Without the colour match (`ae.colorMatch=0`, same run): 2-2 50,36,60; 7-3 55,21,20; 6-1 candles 52,3,3 (the material
fix alone); 8-2 88,41,45 (the sky and side lights alone; 59,34,38 with `plugin._lowSideLightDeg=0` as well). 8-2's street
is now 0.76x the game's luma instead of 0.65x, and its colour matches. 1-1 start, 0-1 metal, 0-5 lava and 6-2 start look
the same as master: 6-2's and 0-5's gains act on near-empty channels, so their mean colours move by at most 2. 7-3 keeps
its RT brightness (game match down 0.4, see "Game match") and its white-lit back wall. The colour match is one gain per
frame, so a room with both red and white light gets redder overall; it can't make the walls red under white spots.

Cost: two more fixed-point sums per measured pixel in the histogram pass and a multiply in the Composite (not timed
separately; the `exposure` GPU slot measured 0.004-0.008 ms before), plus one 1024x512 blit when a cubemap sky is first
used.

### Flicker
`-ultrart-ae-max 0.5` (with the old full brightening) measured as flicker on 1-4 drift (masked burst sigma 0.97 vs 0.26).
The per-frame trace shows what it was: the *target* is steady to 0.001 EV on every static pose tested (1-4 drift,
lamps, torch hall, indoor a, cerberus, 4-1; band mean sd 0.000-0.014 EV with the torches flickering), but the
*exposure* was still climbing during the capture (1-4 drift: +0.26 to +0.31 EV over the 64 frames, sd 0.015). The
bench resets every history before a burst mode, and auto exposure then snapped to the first frame's measurement. That
frame comes from a fresh denoiser: noise widens the histogram, and the 40-95% band of a noisy image reads too bright
(1-4 drift: +0.10 EV instead of +0.31). It then crept up at the slow up-speed. During a burst each captured frame takes
50-100 ms, and auto exposure ran on real time while the denoiser ran on the bench's 1/360 s clock, so the creep landed
inside the capture. Fixes: adapt on the same clock as the denoiser during bursts (`Denoiser.FrameTimeOverride`), and
settle after a reset (0.5 s at 8/s). With both, 1-4 drift at +0.5 EV measures EV sd 0.001 and masked sigma 0.35
(0.26 at 0 EV, and the image is 1.4x brighter). In play the same snap-then-creep showed as a slow brightening for a
second or two after a level load or Ctrl+F4; the settle removes it. The meter smoothing and the dead band are there for
moving cameras and flickering torches; at static poses the trace shows they never move the exposure.

Cost (RTX 5070 Ti, 1920x1080, trace scale 0.5): 0.004-0.008 ms GPU for both kernels (GPU timer slot `exposure`), plus
one extra texture read per pixel in Composite. The histogram buffer is 256 bytes.

## Sky and GI
Relight computes light transport on linear values, lights the scene with the sky, and gives the sun a disk. Code:
`UltraRTSky.hlsl` (sky radiance, sky table, `SkyDirect`, `SkyMissWeight`), `_SkyLight` and `ToLight`/`LightToDisplay`
in `UltraRTLights.hlsl`, `src/UltraRT/Plugin.SkyGI.cs` (knobs, sky table, `_UltraRTLinear`, radiance statistics).

**Linear transport.** The game lights in gamma space: `albedo * sum(lights)` on sRGB-encoded values. That is the
look of every forward-lit Unity game in gamma colour space, but it is wrong for GI. Averaging light over a hemisphere
(sky visibility, soft shadows, bounce) in gamma space crushes partial values: a surface that sees half the sky gets
0.5 of the sky, where the linear answer displays as 0.5^(1/2.2) = 0.73. Albedo products are the same either way
(`(a*b)^(1/2.2) = a^(1/2.2) * b^(1/2.2)`), so the only difference is how light *adds up*. With `_SkyLight.x` set,
every game colour that acts as light or reflectance (light colours after exposure and fill cut, falloff curves, spot
cones, emissive triangles, the sky, albedo and emission at bounce hits, ambient) is raised to ^2.2 first.
PathTrace, the cache and the denoiser then work on linear lighting. The denoiser's Composite and `Exposure.compute`
convert the *filtered* lighting back (`UltraRTDisplayLighting`), so the conversion never sees noisy samples (that
would bias dark). The physical falloff is inverse-square in linear light, equal to the game's curve at a fraction of
the range (0.3 by default, see "Light reference distance"), and falls off as `1/d^0.91` in display terms (it used to be
`1/d^2` in gamma, i.e. `1/d^4.4` in linear). Reflections convert each reflection sample's hit
lighting back to gamma, because the reflection filter works in display units. `pt` with `-ultrart-accumulate`
accumulates displayed colour, so it is slightly biased dark in linear mode. Use the radiance statistics (below) for
energy comparisons.

**Sky light.** The sky is the level's panoramic sky (`FindSkyMaterial`: an active Skybox/Panoramic sphere such as
1-1's `LimboSkySky` / `green-fields`, else `RenderSettings.skybox`; with neither, the flat `_SkyColor` = ambient).
Sky walls (LIMBO_WALLS, mask 0x02) report a miss, so rays that hit them get sky radiance, and visibility rays pass
through them. Sky light is estimated two ways, combined with the balance heuristic:
- `SkyDirect`: one direction from a luminance-weighted alias table over a 64x32 lat-long grid of the sky (cell weight
  = linear luminance x solid angle + a 2% uniform floor), uniform in solid angle within the cell, one visibility ray
  (geometry + fixtures). Weight `p_cos / (p_cos + p_sky)`.
- The cosine bounce ray: when it escapes, it gets `SkyLightRadiance * p_cos / (p_cos + p_sky)` (`SkyMissWeight`).

Together they are unbiased for any table: measured means with and without sky sampling agree within 0.5% (1-1
cerberus 0.641 vs 0.642, user d 0.425 vs 0.424, start 0.535 vs 0.538, linear). The table is built on the CPU from an
async readback of the sky texture (blitted to 512x256, mipped to 64x32) whenever the texture or tint changes. Until
it arrives, a 1-entry uniform-sphere table is bound. A flat sky colour uses that uniform table, and a black one turns
sampling off (0-5, 4-1: no extra cost). The radiance cache's update paths don't take sky samples
(`ULTRART_SKY_NO_NEE`): cells average hundreds of cosine paths, so the ray cost 0.06 ms for no visible gain. Their
cosine misses carry the full sky.

**Sun.** Shadowed directional lights (the game's sun; directional lights from below the horizon stay unshadowed
fills) aim their shadow ray at a uniform point in a cone of `-ultrart-sun-angle` (0.5 deg) around the light direction.
The penumbra is about 1.7 cm per metre of occluder distance.

**Cache energy.** The cache's multi-bounce is energy-correct: 2 bounces plus the 0.8 feedback tail measure the same as
4 explicit bounces without feedback (1-1 cerberus 0.640 vs 0.641, user d 0.421 vs 0.425, torch hall 0.066 vs 0.068,
pt-indirect, linear). Feedback 1.0 adds 1-4%. In the gamma model the cache added 27% (outdoors) to 62% (torch hall)
over one bounce. With linear albedo (~0.15 instead of ~0.45) further bounces carry less, as they should.

### Diagnosis: the "bounce is a tenth of the fill" claim
Measured with the radiance statistics (bench `sky-diag`; mean demodulated lighting by surface orientation, game
units), 1-1 cerberus pose, gamma-space relight (exposure 1.5):

| | up | side | down |
|---|---|---|---|
| sun (direct, no fill) | 1.70 | 0.63 | 0.03 |
| fill at 0.75 (`(3)` + `(4)`) | 0.00 | 0.17 | 0.20 |
| RT sky + bounce (cache) | 0.75 | 0.83 | 0.59 |

On average the RT indirect light was already 2-5x the fill on the surfaces the fill lights (side, down), not a tenth.
The dark cerberus statues came from **exposure**: with the old model, sunlit ground reached 2.5 and auto exposure
pulled the whole frame down by 1.4 EV (user d: 2.0 EV), which pushed shaded bronze to near black. With linear
transport the same poses need -1.0 / -0.7 EV, because shaded surfaces (lit by averaged sky and bounce) are no longer
crushed relative to sunlit ones. Sky light is about 12% of the sun on open ground (up-facing: sun 4.1, sky 0.51,
linear), within the usual 10-20% of a clear day, so the sky scale stays 1.

### Defaults
- **Fill cut 1** (fully ray traced). With linear transport, the 0.25 fill changes outdoor side-facing lighting by about
  4% and down-facing (statue undersides) from 0.74 to 0.86 display. Auto exposure is the same within 0.07 EV, and
  the screenshots are near identical. Fill lights only reach outdoor layers, so rooms are unaffected.
- **Torch rooms: light reference 0.3, not an exposure lift.** Linear transport made torch-lit rooms about 0.6 EV
  darker than the gamma relight (1-1 torch hall mean 0.34 to 0.23, indoor a 0.38 to 0.23). Lifting them with auto
  exposure also lifts every other dim room: at +0.6 EV, 4-1's corridor and 0-1's metal hall came out at 2x and 1.6x the
  game. The cause is the falloff (see "Light reference distance"), so that is where it's fixed. See
  "Auto exposure" / "Flicker" for why the old +0.5 EV attempt measured as flicker.
- Sky scale 1, sun 0.5 deg, sky sampling on.

### Cost and noise (RTX 5070 Ti, 1920x1080, trace scale 0.5)
GPU total vs the gamma relight (bench `sky-validate`): +0.06 to +0.26 ms in 1-1 (sky in view: start +0.11, cerberus
+0.15, user b/c +0.20/+0.22, grass +0.26; indoors +0.09/+0.10), 1-4 +0.06/+0.09, 0-5 and 4-1 +0.00/+0.02 (no sky). All of
it is the sky visibility ray and pow() in PathTrace (`dispatch`); `cache` is unchanged. The sky table is 32 KB.

Temporal noise (Burst, masked sigma, 64 frames; lower is better), gamma relight (fill 0.25) -> new defaults: 1-1 start
0.41 -> 0.42, grass 0.41 -> 0.41, cerberus 0.39 -> 0.36 (repeat: 0.38 -> 0.31), user a 0.90/0.71 -> 0.82/0.68, user b
0.72 -> 0.51, user c 0.49 -> 0.43, user d 1.26 -> 0.79, 1-4 drift 0.36 -> 0.26, 1-4 lamps 0.30 -> 0.33, 4-1 0.17 -> 0.18,
0-5 1.18 -> 1.34. Torch hall and indoor a are dominated by the game's style-meter HUD animating during the burst. The
fill cut is the noisiest part: fill 0.25 instead of 1 lowers cerberus to 0.24 and user a to 0.59 (the fill has no noise).

Other modes are unchanged: everything is gated on relight (`_SkyLight` is zero otherwise). Bench `sky-regress-check`
against a same-session build of the base commit: 0-1 pt-dn mean luma 33.8 vs 33.5-33.9, cerberus hybrid 166.6 vs
166.1-166.6. Switching between relight and another mode clears the radiance cache, because the lighting space changes.

Look: shaded faces outdoors now get coloured sky light and ground bounce instead of the fills' flat orange. Lava and
emissive panels light much further (linear falloff of emitted light), and 1-4's night sky lights the scene. 4-1's
green corridor comes out noticeably brighter and greener than the game (screen mean luma 44 vs the game's 16 and the
gamma relight's 31). It is lit mostly by green point lights, so it gets the light reference boost too (33 with
reference 0.5). That is the most visible departure from the game's look; `-ultrart-relight-ref 0.4` halves the boost
(+0.24 EV instead of +0.49 over 0.5) for every local light, torch rooms included.

### Light reference distance
The linear-transport falloff is `K r^2 / d^2` (r = the light's range), windowed to 2r and capped at 3x the game's
curve, with K chosen so it equals the game's `g(d) = 1 / (1 + 25 d^2 / r^2)` at `d = f r`: `K = g(f)^2.2 f^2`
(`Plugin.SkyGI.cs` `RelightLinearK`, shader `_RelightLinearK`). In display terms, `g(f) (f r / d)^0.91`.

With f = 0.5 (the first version) the curve is far flatter than the game's: 35% dimmer than the game at a quarter of
the range and about half the old gamma relight there (gamma inverse-square was steeper: `1/d^2` in display), and
brighter than the game beyond the range. Torch-lit walls sit close to their torches, so torch rooms lost their glow
(about 0.6 EV darker than the gamma relight), while far-reaching lights (4-1, 0-1) changed little. Matching at 0.3 of
the range scales every local light by 2.1 in linear, 1.4x displayed (+0.49 EV). Sun, sky and emissive surfaces are
unaffected, and outdoor scenes come back to the same image because auto exposure darkens them more. Screen mean luma
(bench `relight-polish`, game / gamma relight / f 0.5 / f 0.3): torch hall 50 / 75 / 54 / 72, indoor a 48 / 84 / 55 / 75,
1-4 lamps 31 / 21 / 23 / 29, 4-1 indoor A 16 / 31 / 33 / 44, 0-1 metal 45 / 40 / 43 / 55; outdoor 1-1 poses within
+-3. f 0.35 gives 67 / 68 / 28 / 42 / 52 for the same five. Lowering the cap's 0.05 floor (it bounds the tail beyond
the range) to 0.01 changed 4-1 by 2%, so the tail isn't what brightens it.

### Radiance statistics
Every RT screenshot in a bench run logs `radiance: all/up/side/down <share> L=<mean lighting> (rgb)` (with linear
transport also `G`, the mean converted to display units), the share of sky and fullbright pixels, and the sky table
(`Plugin.SkyGI.cs`, `RadianceStats`). Combine with mode variants: `pt-direct+relight+plugin._relightFillCut=1` (sun
only), `pt-indirect+relight+plugin._skyScale=0.0001` (bounce without sky),
`pt-indirect+relight+cache.bounces=4+cache.feedback=0` (energy reference). `"ShotSettle": s` in a scenario holds each
mode longer before its screenshot, so the cache converges after a variant. Suites: `sky-diag`, `sky-diag2`,
`sky-noise`, `sky-validate` (before/after with bursts), `sky-shots`.

## Fill lights
Outdoor ULTRAKILL areas are lit by a sun plus one or two directional lights shining *up* from below the horizon, which
fake ground bounce and keep shaded sides readable. In 1-1 those are `Directional Light (3)` (I 1.5, warm) and `(4)`
(I 1.0, orange), and their culling mask (0x03000000 / 0x03002000) only covers the outdoor layers 24/25, so they never
touch the torch-lit rooms. With the fill cut at 1 (the old default) the real bounce from the ground is far weaker than
these fills (roughly a tenth), so 1-1's bronze cerberus statues went dark brown with black rims while pt-dn and hybrid
looked right. The default became 0.25 (three quarters of the fill kept) and is back to 1 since linear transport and
sky light (see "Sky and GI": the statues were dark from exposure, not missing bounce). Indoor exposure measurements
are identical for fill 1, 0.5 and 0.25.

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

**Lamp shades** (2026-11-14 pm, user report: an 8-2 standing lamp that is constant in the game had a fairly smooth
flicker in RT). The lamp (`ApartmentLamp/Point Light`, range 25, inside the shade of a 4 m `StandingLamp`) is part of a
static batch, so it can't carry the fixture mask and is too tall for the fixture test; the shade shadows its own light,
which escapes through the top and bottom openings as cones. With the global soft-shadow radius (0.15 m) the openings
cast wide penumbrae, and their ReSTIR noise boiled slowly under the denoiser: a burst at the user's pose measured masked
sigma 1.88, block sigma 1.68 and drift 2.86 (the same with effect-light flicker off; auto exposure stayed at 0 EV;
volumetrics off: 1.24; radiance cache off: 1.79; a 0.02 m radius everywhere: 0.65). `SceneProxies.StaticEnclosed` now
records point/spot lights inside a static-batched renderer of at most 6 m with a footprint of at most 2 m, and
`GatherLights` gives those that are not lifted a 0.03 m radius: sigma 0.73, block 0.59, drift 0.90
(`bench/results/20261114-185737_lights-pm-check2_alt`). 8-2 has 7 such lights, P-2 17.
