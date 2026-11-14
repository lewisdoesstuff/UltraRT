# Volumetric lighting

Ray-traced single scattering for relight (and pt-dn): sun shafts through windows, arches and trees, and halos and
shafts around torches, lamps and explosions, in the level's own atmosphere. The same pass also gives the ray-traced
image back the game's distance fog, which the replace modes had lost.

Code: `unity/.../Shaders/Volumetrics.raytrace` (the march), `Volumetrics.compute` (ray lengths, temporal filter),
`VolumetricsComposite.shader` (upsample, fog, composite), `src/UltraRT/Volumetrics.cs` (settings, resources, dispatch),
`src/UltraRT/Plugin.Volumetrics.cs` (wiring). Bench suites `volumetrics-quick` (looks) and `volumetrics` (bursts).

## Survey: the game's own atmosphere

Offline (UnityPy over every `campaign_scenes_*.bundle`, the `RenderSettings` object and GameObject names) plus
`Assembly-CSharp`:

- **Every level has Unity distance fog configured, always linear** (`m_FogMode` 1). Start/end and colour per level,
  for example (fog on/off as saved in the scene):

  | Level | Fog | Colour | Start-end (m) |
  |---|---|---|---|
  | 0-1 | on | dark orange (0.52, 0.23, 0.11) | 0-250 |
  | 0-5 | on | orange (0.65, 0.25, 0) | 0-175 |
  | 1-1 | on | pale yellow (1, 0.94, 0.51) | 0-400 |
  | 1-2 | on | ochre (0.73, 0.53, 0.29) | 0-300 |
  | 1-3 | on | magenta (0.69, 0.20, 0.55) | 0-250 |
  | 1-4 | on | night blue (0, 0.004, 0.10) | 0-75 |
  | 2-1, 2-2 | on | black | 0-500 |
  | 3-1 | on | blood red (0.52, 0.06, 0) | 0-125 |
  | 4-1, 2-3 | on | black | 500-900 (effectively none) |
  | 4-2 | on | amber (1, 0.69, 0) | 50-900 |
  | 5-1 | on | cyan (0, 0.75, 1) | 20-500 |
  | 5-3 | on | brown (0.42, 0.30, 0.14) | 20-150 |
  | 6-1 | on | red (1, 0.01, 0) | 10-150 |
  | 7-1, 7-2, 8-3, 0-2/3/4, 4-3/4 | off in the scene (area triggers turn it on) | | |
  | 8-2 | on | mauve (0.71, 0.38, 0.55) | 0-500 |
  | 8-4 | on | sky blue (0.28, 0.55, 0.90) | 0-1500 |
- **The game toggles and retunes it at runtime.** `OnLevelStart` hides the fog until the level starts (so a scene that
  has just loaded logs `fog=False`); `FogEnabler`/`FogDisabler`/`FogTrigger`/`FogFadeController`/`FogSetterBounds`
  objects change it per area (1-e, 0-e, 3-1, 8-3's "Fog Re-enabler"/"RealFog", P-2's "FogChanger"). So the medium is
  read from `RenderSettings` every frame, never cached per level.
- **The Master shader applies it per vertex** (keyword `_FOG_ON`), to opaques, transparents and the sky sphere
  (`LimboSkySky` has `_FOG_ON`): `lerp(fogColour, colour, saturate((end - z) / (end - start)))` in gamma space, z the
  eye depth. Our ray-traced image (`albedo * lighting + emission`) replaced the fogged opaque pixels, so relight and
  pt-dn showed distant geometry unfogged against a fogged sky.
- **Fake shafts and haze are art meshes**: `LightShaft` objects (0-1: 6, 0-3: 9, 0-e: 10, 6-1: 68, 6-2: 20, 8-2: 18,
  8-3: 38), `Godrays` (1-1), `Lightray` (1-3, 1-e), `Shaft`, `LightRays` (4-4), plus `Mist`/`Dust`/`DustBig`/`FakeFog`
  particle systems. They are additive DISTANCE_FADE transparents (docs/reflections.md, section 12: "waterfalls and light
  shafts"), drawn by the game after our composite, so they stay as they are on top of the ray-traced shafts.
- **Suns**: most levels have one or two shadowed (from above) directional lights and fill lights from below (relight
  cuts the fills, docs/relight.md "Fill lights"). The outdoor ones are switched off by the game indoors.

## Design

### Medium
Homogeneous, read from the level's current fog every frame (`Volumetrics.MediumFromFog`):

- density (extinction per metre): linear fog -> `ln 2 / ((start + end) / 2)`, the exponential medium whose
  transmittance is 50% where the game's fog is half way (1-1 0.0035/m, 0-5 0.0079/m, 1-4 0.0185/m). Exponential fog
  uses its density. Times `densityScale`, clamped to `[minDensity, maxDensity]` = [0.002, 0.008] per metre. Where the
  fog is off or thin (4-1, 8-3, any area that turns it off), `minDensity` gives clear-air shafts and light halos with
  no haze colour. The cap only bites in thick-fog levels: at 1-4's 0.0185 the lamps lit the fog into a grey veil that
  lifted the whole night scene (mean luma 43 -> 71); capped, it stays a halo.
- scattering colour: the fog colour's hue (normalised to its largest channel), half way to white (`tint` 0.5): 1-1's
  shafts are warm, 1-4's bluish, 0-5's orange. Black or no fog -> white. Albedo 1: sigma_s = sigma_t x tint.
- phase: Henyey-Greenstein g = 0.4 (forward scattering: shafts brighten looking toward the sun or a lamp).

The ambient (sky-lit) part of the haze is the game's own fog colour and formula, applied in the composite, so far
geometry fades into the level's fog exactly as in the game, and into its fogged sky.

### Light
Per low-res pixel: one ray from the camera to the game's depth, at most `maxDistance` (100 m; the sky counts as 10 km
and is cut there too). In-scattering is computed in linear light, in the plugin's lighting units times pi (a surface's
lighting is irradiance / pi, since displayed = albedo x lighting; the medium's radiance is sigma_s x phase x irradiance).

- **Sun**: the brightest shadowed directional light (fill lights excluded), `(colour x intensity x relight
  exposure)^2.2`, as the relit surfaces see it. `steps` (8) stratified points along the ray, one blue-noise offset per
  pixel and frame (interleaved gradient noise shifted by the golden ratio per frame), each with one shadow ray toward a
  point on the sun disk (`-ultrart-sun-angle`, like PathTrace's soft sun shadows). Transmittance is integrated
  analytically per segment. Shadow rays: geometry and fixtures (mask 0x05; sky walls never block), no back-face
  culling (thin one-sided roofs and leaf cards block light from either side), first hit ends the search.
- **Point and spot lights**: `lightSamples` (1) per pixel from the plugin's `_Lights` buffer (the one PathTrace uses:
  torches, lamps, fixture lifts, effect lights such as muzzle flashes and explosions). Each light's importance is its
  power times the integral of 1/d^2 over the part of the ray inside its reach (2 x range, where its falloff window
  ends) and, for a spot, inside its cone (ray-cone intersection: samples are not spent on air the spot never lights).
  With up to 128 lights all are enumerated and the samples are drawn in proportion in one walk over the CDF (stratified
  blue-noise numbers); above that, RIS over `lightCandidates` (8) uniform candidates per sample. The distance along the
  ray is drawn by equiangular sampling (Kulla & Fajardo 2012) over the same clipped segment, which is exactly
  proportional to 1/d^2. One shadow ray per sample, stopping 0.35 m short of the light like surface shadow rays (lights
  sit inside their fixtures). Falloff and spot cone are relight's (physical inverse square windowed to 2x range, in
  linear light). Directional and fill lights are skipped, and so are **lights attached to the player or the camera**
  (`playerLights` false; also any light within 0.5 m of the eye): the game's player light (range 15, grey, at the eye)
  starts every view ray inside its core. It took 45-72% of the light picks in every pose tested.
- **Haze range** (`hazeRange` 20 m): a light's scattering is weighted by `1 / (1 + (range / hazeRange)^3)`. Torches,
  fires and lamps (range 15-25) keep 70-34% of their halo; a light that reaches across a whole room is the level's mood
  or fill lighting, and in a dense medium it lit the air like a wall of glow (6-2's red room lights, range 25-35 and
  colour x intensity 20-30; 7-2's searchlights, range 160-240): 23% at 30 m, 6% at 50 m, nothing at 160 m. `core` (0)
  optionally softens a light's inverse square to `1 / (d^2 + (core x range)^2)`; at 0.1 it barely changed the look
  (the glow is broad, not concentrated in the halo cores).
- **Emissive surfaces** (lava, glowing panels, screens): `emissiveSamples` (1) per pixel, each RIS over 6 points from the emissive light table
  (`EmissiveLights.cs`, the alias table PathTrace samples), target = emitted luminance x cosine x the integral of 1/d^2
  along the ray. One-sided emitters facing away from both ends of the ray are rejected exactly (the sign of
  `dot(n, x - p)` is linear along the ray). Equiangular distance, one shadow ray. Multiplier `emissive` 0.5.
- **Firefly clamp**: the one-sample light + emissive estimate of a pixel is clamped to luminance `maxSample` (2,
  linear), the emissive part alone to a quarter of that: small hot emitters (1-4's lamp bulbs inside their housings)
  otherwise give rare, very bright samples that the temporal filter turns into slowly drifting blotches.

### Resolution, temporal filter, upsample
- **1/4 resolution** (`block` 4: 480x270 at 1080p). `Volumetrics.compute` `Depth` reads the game's depth copy at each
  block's centre pixel, and that pixel's ray is the one traced.
- **Temporal** (`Temporal`): a depth-aware 5x5 Gaussian (sigma 1.5 low-res px; depth weight
  `exp(-(dd / (0.05 d + 0.05))^2)`) of this frame's samples; the history reprojected by the ray's end point with last
  frame's matrices, validated by its stored ray length (10%), clipped to the 5x5 mean +- 3 sigma (a 3x3 clip box from
  one-sample estimates threw the history away every frame), blended with alpha = max(1 / length, 0.03), history capped
  at 64 frames. NaN/Inf samples are zeroed in the trace and in the filter. Alongside, the temporal moments of the raw
  samples' luminance (mean, mean of squares; alpha max(1 / length, `momentsAlpha` 0.1), RGFloat), reprojected with the
  history.
- **A-trous** (`Atrous`, `atrous` 3 passes, output only: the history keeps the unfiltered accumulation): SVGF-style
  wavelet passes, 5x5 B-spline taps at step 1, 2, 4 (a 17x17 low-res footprint, 68 screen px), weighted by depth
  (`0.1 d + 0.1`, neighbours scaled to this pixel's ray length like the upsample) and by luminance in units of the
  estimate's standard deviation (`atrousLumSigma` 4). The estimate's variance is the raw samples' temporal variance over
  the samples the history holds (its length x `prefilterSamples` 8 for the 5x5 prefilter), blurred 3x3, and is carried
  through the passes (sum w^2 var / (sum w)^2). Where the history is short or the samples sparse (a moving camera, rare
  light hits) the filter is wide; once converged it stops at shaft edges with more than a few percent of contrast.
- **Upsample** (in the composite): the 4 nearest low-res samples, each scaled to the pixel's own ray length
  (`min(d / d_i, 1)`: in-scattering grows about linearly with distance, so a thin foreground object in front of a far
  sample gets the right, smaller amount) and weighted bilinear x `exp(-((d_i - d) / (0.05 d + 0.1))^2)`. With no depth
  match, the closest-depth sample (scaled) is used.

### Composite
`VolumetricsComposite.shader`, two fullscreen passes into the camera target:

1. **Fog** (pass 0), right after the RT composite, before the game's transparents: on pixels the temporal upsampler
   marks as ray traced (`_UltraRTUpsampled.a > 0`), the game's fog, exactly its formula and colour (display space, eye
   depth), as a blend (`Blend One SrcAlpha`: fog x (1 - vis) + dst x vis), no copy. Sky, the game's own pixels (glow
   quads, decals) and hybrid already have it.
2. **Scatter** (pass 1), in a second command buffer at `CameraEvent.AfterForwardAlpha`, after the game's transparents,
   from a copy of the target: `out = (in^2.2 + exposure x strength x S)^(1/2.2)`, then a soft shoulder on what was added
   (`c + h (1 - exp(-delta / h))`, h the room above c), so bright shafts over a bright sky saturate smoothly. The
   exposure is relight's auto exposure (what the lit RT image gets), read from its GPU buffer; 1 when auto exposure is
   off (pt-dn by default).

The scatter used to be drawn before the transparents too. Alpha-tested grates and fences are transparent-queue objects
in ULTRAKILL (not in the depth buffer): drawn over the glowing air they stood out black (6-2's grates, reported by the
user). After the transparents, grates, water, glass and the game's own light-shaft meshes get the glow of the air
behind them. The price of that order: an alpha-blended effect close to the camera (smoke, a sprite) gets the glow of
the whole ray behind it rather than of the air in front of it (additive effects are unaffected: addition commutes).
The game's fog still applies to transparents as the game draws them.

## Settings

| Flag | Field (`vol.<field>` bench variant) | Default | |
|---|---|---|---|
| `-ultrart-volumetrics on\|off`, config `[Volumetrics] Enabled`, **Ctrl+F6** | `enabled` (`vol=0\|1`) | on | relight and pt-dn |
| `-ultrart-vol-hybrid` | `ptDn`, `hybrid` | true, false | hybrid gets the in-scattering only (its colour already has the game's fog) |
| `-ultrart-vol-block N` | `block` | 4 | screen px per low-res px |
| `-ultrart-vol-steps N` | `steps` | 8 | sun shadow rays per low-res px |
| `-ultrart-vol-distance M` | `maxDistance` | 100 | |
| `-ultrart-vol-density F` | `densityScale` | 1 | x the fog-derived density |
| `-ultrart-vol-min-density F` | `minDensity` / `maxDensity` | 0.002 / 0.008 per m | |
| `-ultrart-vol-strength F` | `strength` | 1 | |
| `-ultrart-vol-g G` | `anisotropy` | 0.4 | |
| | `tint` | 0.5 | |
| `-ultrart-vol-lights F` | `lights`, `lightCandidates`, `lightSamples` | 1, 8, 1 | 0 = no point/spot lights; samples (shadow rays) per low-res px |
| | `hazeRange`, `core` | 20 m, 0 | light scattering x 1 / (1 + (range / hazeRange)^3) (0 = off); soft core (x range) |
| | `playerLights` | false | let lights attached to the player / camera light the air |
| | `sun` | 1 | sun scattering multiplier (diagnostics: `vol.sun=0`) |
| `-ultrart-vol-emissive F` | `emissive`, `emissiveSamples` | 0.5, 1 | 0 = no emissive surfaces |
| | `maxSample` | 2 | firefly clamp |
| `-ultrart-vol-nofog` | `gameFog` | true | the game's fog on RT pixels |
| | `alphaMin`, `maxHistory`, `clipSigma`, `spatialSigma`, `depthTolerance`, `spatialDepthSigma` | 0.03, 64, 3, 1.5, 0.1, 0.05 | temporal filter |
| | `atrous`, `atrousLumSigma`, `atrousDepthSigma`, `prefilterSamples`, `momentsAlpha` | 3, 4, 0.1, 8, 0.1 | a-trous filter (0 passes = off) |
| | `meterHaze` | 0 | > 0: auto exposure meters the haze with the image (last frame's in-scattering, as the composite adds it) |
| | `debugView` | 0 | 1 = the exposed in-scattering alone, 2 = the same without the temporal filter |

GPU timer slots `volumetrics` (after `upsample`: depth, trace, temporal, a-trous, the fog pass) and `vollate` (after the
transparents: the target copy and the scatter pass; not inside `total`). The plugin logs the medium every 10 s
(`Volumetrics: medium 0.0035/m (fog linear 0-400 m, ...), tint (...), sun ..., N lights`) and, at most once a second
when the camera has moved (else every 10 s), the lights that light the most air along the view direction:
`Volumetrics lights at (x, y, z) (N lights, M reach the view ray): [61% 'Point Light' point range 15.0 col (...) dist 0.0] ...`
(share of the importance before the haze range weight, name, type, range, colour x intensity, distance).

## Results
RTX 5070 Ti, 1920x1080, trace scale 0.5, relight, defaults. Suites `volumetrics` (bursts, `bench/results/20261114-085803_volumetrics`)
and `volumetrics-quick` (screenshots: `off`, `relight+vol=0`, `relight`, `relight+vol.debugView=1`;
`bench/results/20261114-090450_volumetrics-quick`).

| Pose | GPU total vol off -> on (ms) | `volumetrics` slot (ms) | burst block sigma vol off -> on | medium |
|---|---|---|---|---|
| 1-1 start | 1.550 -> 1.754 | 0.215 | 0.30 -> 0.26 | 0.0035/m, sun |
| 1-1 cerberus | 1.985 -> 2.264 | 0.273 | 0.32 -> 0.33 | 0.0035/m, sun |
| 1-1 user d (sunlit courtyard) | 2.198 -> 2.456 | 0.281 | 1.41 -> 1.04 | 0.0035/m, sun |
| 1-1 torch hall | 1.970 -> 2.206 | 0.229 | 0.39 -> 0.93 (0.49 -> 0.30 in the run before; the style-meter HUD animates) | 0.0035/m |
| 1-4 lamps (night) | 1.369 -> 1.550 | 0.179 | 0.30 -> 0.52 | 0.008/m (fog 0.0185, capped) |
| 1-4 drift (night) | 1.143 -> 1.320 | 0.177 | 0.27 -> 0.77 | 0.008/m |
| 4-1 indoor A | 1.736 -> 1.963 | 0.223 | 0.14 -> 0.16 | 0.002/m (fog 500-900 m) |
| 0-5 lava | 2.335 -> 2.490 | 0.159 | 0.88 -> 0.56 | 0.0079/m, no sun |
| 8-3 b | 1.765 -> 1.954 | 0.164 | 1.33 -> 1.32 | 0.002/m (fog off), 139 lights |

Block sigma is luma (0-255) noise that survives 8-frame averaging, masked to pixels the game keeps still; lower is
better. 0.15-0.28 ms, under the 0.3 ms target; the sun march is the larger part (cerberus and user d, the poses with the
most sunlit air, are the most expensive). Regression (`merge-regress` against master 1666d51): hybrid, albedo and
pt-direct GPU totals unchanged (0-1 hybrid 2.504 -> 2.507, cerberus hybrid 1.864 -> 1.867 ms); relight and pt-dn grow by
the `volumetrics` slot.

Look (screenshots above):
- **1-1 daytime**: subtle. Distant geometry fades into the level's warm fog again (relight had lost it) and sunlit air
  adds a light warm haze that is a little stronger toward the sun; the shadow wedges behind the start gate and arches
  are visible in the debug view but faint in the final image at 1-1's thin fog (0.0035/m). `-ultrart-vol-density 2-3`
  gives clearly visible shafts.
- **1-1 torch hall**: soft halos around the torches.
- **1-4 lamps**: the clearest result: shafts radiating from the lamp, cut by its own housing, over the night sky.
- **1-4 drift**: the lamps at the end of the bridge glow in the fog and the bridge end is fogged like the game's own
  frame. The lamp-lit fog still lifts the night scene (mean luma 43 -> 67) and shows some dark, quarter-resolution
  streaks behind the lamp posts (shadows of thin geometry undersampled at 1/4 resolution). This is the weakest pose.
- **4-1 indoor A**: almost unchanged: the fog is effectively off (500-900 m) and the emissive panels scatter faintly at
  the minimum density.
- **0-5 lava**: a hot orange glow rising off the lava (emissive scattering); noise is lower than without (the haze
  hides some of the lighting noise).
- **8-3**: little change (fog off, minimum density).

### Noise and glow in dark foggy levels (2026-11-14, branch `vol-noise`)
The user, playing relight: noise in 0-1's dark areas, "very obvious" in 6-2, "an almost comical amount" on 7-2's door to
the outside; denoiser settings changed nothing. Measured (`noise-1114b`): volumetrics doubled both the burst noise and
the brightness of these poses (0-1 dark sigma 0.70 vs 0.36 with `vol=0`, mean luma 55 vs 29; 6-2 start 1.55 vs 0.72).

**Diagnosis** (`vol-noise-diag`: per-term bursts on the exposed in-scattering alone, `vol.debugView=1` with
`vol.sun=0` / `vol.lights=0` / `vol.emissive=0`). Point and spot lights were all of it: without them the in-scattering
of 0-1 dark fell from luma 49 to 5 and its sigma from 0.49 to 0.02 (6-2 b 36 -> 3, 7-2 door 14 -> 6); the sun and the
emissive term changed nothing in these poses, and metering the haze in auto exposure (`vol.meterHaze=1`) did not either
(the scenes sit in auto exposure's dead band between the two keys). The light survey log then showed why:

1. **The player's own light.** Every pose has a `Point Light` (range 15, grey 0.5) at distance 0.0: the game's light on
   the player. Every view ray starts inside its core, so it took 45-72% of the light picks in every pose (starving the
   real lamps of samples) and lit the air at the eye. Skipped now (attached to the player or camera, or within 0.5 m of
   the eye).
2. **Room-sized lights.** 6-2's rooms are lit by red point and spot lights of colour x intensity 20-30 and range 25-35
   (after relight's exposure and linear transport about 1,800x in linear light), 0-1's by orange lights of range 15-35,
   7-2's exterior by searchlight spots of range 160-240. In a medium of 0.0055-0.008/m the air near them glowed like a
   wall (6-2 b in-scattering alone: luma 36), and their sparse, view-dependent samples boiled. The haze range weight
   (20 m) keeps torch, fire and lamp halos and fades these.
3. **Spot cones were ignored when sampling**: a searchlight's importance and equiangular segment covered its whole
   reach sphere, so most samples landed outside the beam (zero) and a few inside (bright). Clipped to the cone now.
4. **Too little filtering for what was left**: one-sample light estimates averaged by a 5x5 Gaussian and a 64-frame
   history leave low-frequency blotches that drift (block sigma close to sigma: slow boiling). The variance-guided
   a-trous passes remove them; with them a second light sample per pixel no longer measured as better (0.24 vs 0.28
   in 0-1 dark with the player light still in; after the skip 1 sample is as good as 2), so the default is 1.

**Grates** (user report): 6-2's grates stood black in front of the glow. They are transparent-queue objects drawn after
the composite; the scatter pass now runs after the transparents (see "Composite").

Results (`vol-noise-r4`, `bench/results/20261114-143809_vol-noise-r4`; RTX 5070 Ti at **2560x1440**, trace scale 0.5;
48-frame bursts, masked sigma, luma 0-255; "old" = this branch with the previous behaviour: 1 sample, no a-trous,
player light in, no haze range):

| Pose | sigma vol=0 / old / new | p95 vol=0 / old / new | burst mean luma vol=0 / old / new |
|---|---|---|---|
| 0-1 dark | 0.31 / 0.32 / 0.32 | 0.77 / 0.68 / 0.82 | 29.8 / 55.3 / 48.6 |
| 6-2 start | 1.10 / 0.82 / 0.35 | 3.23 / 2.92 / 2.06 | 23.8 / 34.6 / 26.7 |
| 6-2 b | 0.18 / 0.60 / 0.16 | 0.56 / 2.38 / 0.53 | 19.6 / 37.6 / 28.9 |
| 6-2 c | 0.17 / 0.48 / 0.13 | 0.48 / 1.29 / 0.38 | 20.6 / 32.3 / 20.5 |
| 7-2 door | 0.25 / 0.75 / 0.24 | 0.74 / 4.01 / 0.74 | 25.4 / 28.2 / 25.5 |
| 7-2 outside | 0.57 / 0.47 / 0.38 | 1.42 / 1.05 / 1.07 | 15.6 / 27.1 / 17.5 |

(On master before this branch, `noise-1114b`: 0-1 dark 0.70, 6-2 start 1.55, 6-2 b 0.64, 6-2 c 0.42, 7-2 door 0.80 with
p99 4.26. "Old" differs from master by the spot-cone clipping and the scatter pass's place after the transparents:
together they took 0-1 dark's "old" from 0.67 (`vol-noise-diag`) to 0.32; the two were not measured separately.) Noise is now
at or below `vol=0` everywhere; 6-2 start and 7-2 outside are below it because the haze covers part of relight's own
lighting noise. Screenshot luma (`2.0s` shots, HUD included) for the poses that should keep their look, vol=0 / old /
new: 1-4 lamps 28.6 / 43.0 / 35.6 (the lamp halo and the shafts cut by its housing stay, softer), 1-4 drift 48.3 / 67.0 /
57.4, 0-5 lava 94.7 / 111.8 / 101.0 (the lava glow is emissive and unchanged), 8-3 b 78.0 / 89.1 / 86.4, 4-1 indoor A
40.3 / 41.8 / 40.3, 1-1 start 146.8 / 164.6 / 164.4 (sun: unchanged). 0-1 dark stays the brightest (screenshot 32.8 /
60.5 / 53.4, the game 34.2): a warm glow above the fan with the blades' shafts through it rather than a uniform wash;
`vol.hazeRange=15` takes it to 50, at the price of most of 1-4's lamp halo (33 instead of 36). `vol.maxDensity=0.006`
changed nothing once the room lights were weighted down. Note that relight without haze is itself 2.3x brighter than
the game in 6-2 (vol=0 27 vs off 12 screenshot luma): that is relight's light model, not this pass.

GPU (1440p; the low-res pass is 640x360, 1.78x the pixels of 1080p): `volumetrics` + `vollate` 0.395 + 0.071 ms in 7-2
door and 0.381 + 0.071 ms at 1-1 start (sun), against 0.328 + 0.071 / 0.330 + 0.071 for "old"; `atrous=2` saves
0.02 ms at the same noise. `merge-regress` (`bench/results/20261114-145043_merge-regress` vs master b6ea2ba's
`20261114-142912_merge-regress`): hybrid, pt-direct and albedo GPU totals unchanged or lower (hybrid 0-1 metal 4.10 vs
4.44, pt-direct 1.69 vs 1.78, albedo 0.76 vs 0.78; master has other work merged since this branch's base), relight's
volumetric cost 0.37-0.60 ms (vol + vollate) against 0.24-0.42 ms: about 0.21-0.34 ms at 1080p.

### Development notes (for whoever tunes this next)
- The first version (uniform 4-candidate light picks, area-uniform emissive points, 3x3 clip box, alpha 0.06) boiled
  in 1-4 (block sigma 1.8-2.6): most candidates missed every light, small emitters gave rare huge samples, and the clip
  box from 9 one-sample neighbours threw the history away. Enumerating lights, clipping the equiangular range to each
  light's reach, the 5x5 filter, alpha 0.03 and the emissive clamp each mattered.
- `debugView=2` (raw samples) showed 1-4's blotches came from sparse emissive samples (most pixels 0, a few very
  bright): fixed by the exact back-face rejection and the emissive clamp. NaN/Inf guards were added while checking and
  stay (cheap); whether NaNs ever occurred was not established.
- Units: a first build forgot the factor pi (lighting = irradiance / pi), making every term pi times too dark.

### Not done / limits
- Hybrid is off by default (`-ultrart-vol-hybrid`): its colour already has the game's fog; only the in-scattering
  would be added.
- The game's fake light-shaft meshes (`LightShaft`, `Godrays`) and mist particles are left as they are, on top.
- Transparents get the glow of the air behind them, not of the air in front of them: right for far grates, water and
  glass, too much for alpha-blended smoke right in front of the camera.
- Moving lights (7-2's searchlights, flashes) still lag by the temporal history; the haze range takes the brightest of
  them out of the air.
- Homogeneous medium: no height fog, no noise texture.
- One light and one emissive sample per pixel per frame by default (`lightSamples`, `emissiveSamples`); very large light
  counts (> 128) fall back to RIS.
- The haze range is a heuristic on the light's range: a big-range lamp that really is a lamp loses its halo, and a
  short-range light the level uses as a fill keeps it.
