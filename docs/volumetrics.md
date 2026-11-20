# Volumetric lighting

Ray-traced single scattering for relight, pathtrace (and pt-dn): sun shafts through windows, arches and trees, and halos and
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
  uses its density. Times `densityScale`, clamped to `[minDensity, maxDensity]` = [0.002, 0.005] per metre (0.008
  before 2026-11-15, see "Dark rooms"). Where the fog is off or thin (4-1, 8-3, any area that turns it off),
  `minDensity` gives faint clear-air shafts and light halos with no haze colour; where it is off the scattering is
  also scaled by `clearAir` (0.25). The cap only bites in thick-fog levels: at 1-4's 0.0185 the lamps lit the fog into
  a grey veil that lifted the whole night scene (mean luma 43 -> 71); capped, it stays a halo.
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
  sit inside their fixtures); a light that skips fixtures (statue orbs, 3-1's eyes in their housing) tests geometry
  only, as its surface rays do (since 2026-11-20: "The Stairways too bright" below). Falloff and spot cone are relight's (physical inverse square windowed to 2x range, in
  linear light). Directional and fill lights are skipped, and so are **lights attached to the player or the camera**
  (`playerLights` false; those within 2.5 m of the eye, `attachedReach`, 1.5 m before 2026-11-18; also any light within 0.5 m of the eye): the game's player light (range 15, grey, at the eye)
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
`VolumetricsComposite.shader`, three fullscreen passes into the camera target (fog, scatter, beams):

1. **Fog** (pass 0), right after the RT composite, before the game's transparents: on pixels the temporal upsampler
   marks as ray traced (`_UltraRTUpsampled.a > 0`), the game's fog, exactly its formula and colour (display space, eye
   depth), as a blend (`Blend One SrcAlpha`: fog x (1 - vis) + dst x vis), no copy. Sky and the game's own pixels (glow
   quads, decals) already have it.
2. **Scatter** (pass 1), in a second command buffer at `CameraEvent.AfterForwardAlpha`, after the game's transparents,
   from a copy of the target: `out = (in^2.2 + exposure x strength x S)^(1/2.2)`, then a soft shoulder on what was added
   (`c + h (1 - exp(-delta / h))`, h the room above c), so bright shafts over a bright sky saturate smoothly. The
   exposure is relight's auto exposure (what the lit RT image gets), read from its GPU buffer; 1 when auto exposure is
   off (pt-dn by default).
3. **Beams** (pass 2), in the same late buffer before the scatter pass's copy: the game's beam meshes times their traced
   visibility, added in display space (see "Eye beams", "Beam channel").

The scatter used to be drawn before the transparents too. Alpha-tested grates and fences are transparent-queue objects
in ULTRAKILL (not in the depth buffer): drawn over the glowing air they stood out black (6-2's grates, reported by the
user). After the transparents, grates, water, glass and the game's own light-shaft meshes get the glow of the air
behind them. The price of that order: an alpha-blended effect close to the camera (smoke, a sprite) gets the glow of
the whole ray behind it rather than of the air in front of it (additive effects are unaffected: addition commutes).
The game's fog still applies to transparents as the game draws them. Stained glass tints the in-scatter of light that
crosses it (`g_VolTint`, docs/materials.md "Stained glass").

## Settings

| Flag | Field (`vol.<field>` bench variant) | Default | |
|---|---|---|---|
| `-ultrart-volumetrics on\|off`, config `[Volumetrics] Enabled`, **Ctrl+F6** | `enabled` (`vol=0\|1`) | on | relight, pathtrace and pt-dn |
| | `shadowPass` | true | the hit pass traces with `Proxy.shader`'s slim `UltraRTShadow` pass (docs/performance.md) |
| | `ptDn` | true | also in pt-dn (`-ultrart-vol-hybrid` and the `hybrid` field went with the hybrid mode on 2026-11-19) |
| `-ultrart-vol-block N` | `block` | 4 | screen px per low-res px |
| `-ultrart-vol-steps N` | `steps` | 8 | sun shadow rays per low-res px |
| `-ultrart-vol-distance M` | `maxDistance` | 100 | |
| `-ultrart-vol-density F` | `densityScale` | 1 | x the fog-derived density |
| `-ultrart-vol-min-density F` | `minDensity` / `maxDensity` | 0.002 / 0.005 per m | max 0.008 before 2026-11-15 |
| | `clearAir` | 0.25 | scattering albedo where the game's fog is off (1 = as before 2026-11-15) |
| | `toe` | 0.002 | black-level toe on the in-scattering (linear display units): `s * L / (L + toe)`; 0 = off |
| `-ultrart-vol-strength F` | `strength` | 0.65 | 1 before the evening balance pass |
| | `maxAdd` | 0.25 | the most the haze adds to a pixel (soft, display units); 0 = up to white |
| | `darkFog` | 0.25 | fog colour brightness for full scattering; darker fog scatters less, black none; 0 = off |
| `-ultrart-vol-g G` | `anisotropy` | 0.4 | |
| | `tint` | 0.5 | |
| `-ultrart-vol-lights F` | `lights`, `lightCandidates`, `lightSamples` | 1, 8, 1 | 0 = no point/spot lights; samples (shadow rays) per low-res px |
| | `hazeRange`, `core` | 20 m, 0 | light scattering x 1 / (1 + (range / hazeRange)^3) (0 = off); soft core (x range) |
| | `playerLights` | false | let lights attached to the player / camera light the air |
| | `attachedReach` | 2.5 m | how far from the eye an attached light still counts as carried (`vol.attachedReach`; 1.5 m before 2026-11-18) |
| | `sun` | 1 | sun scattering multiplier (diagnostics: `vol.sun=0`) |
| `-ultrart-vol-emissive F` | `emissive`, `emissiveSamples` | 0.5, 1 | 0 = no emissive surfaces |
| `-ultrart-vol-emitter-medium F` | `emitterMedium` | 1 | 1: an emitter triangle's air share applies (a fixture handed over to its glowing part keeps its class's `medium` in the air, below); 0 = the full emitter output in the air, as before 2026-11-19 (bench `vol.emitterMedium=0`) |
| | `maxSample` | 2 | firefly clamp |
| `-ultrart-vol-nofog` | `gameFog` | true | the game's fog on RT pixels (not on "NoFog" materials, below the table) |
| | `alphaMin`, `maxHistory`, `clipSigma`, `spatialSigma`, `depthTolerance`, `spatialDepthSigma` | 0.03, 64, 3, 1.5, 0.1, 0.05 | temporal filter |
| | `atrous`, `atrousLumSigma`, `atrousDepthSigma`, `prefilterSamples`, `momentsAlpha` | 3, 4, 0.1, 8, 0.1 | a-trous filter (0 passes = off) |
| | `meterHaze` | 0 | > 0: auto exposure meters the haze with the image (last frame's in-scattering, as the composite adds it) |
| | `debugView` | 0 | 1 = the exposed in-scattering alone, 2 = the same without the temporal filter |

**The game's fog skips "NoFog" materials (2026-11-18, `t3/playtest-materials`).** The fog pass (VolumetricsComposite
pass 0) fogged every traced pixel, but the game draws `_FOG_OFF` materials (named "... NoFog": 0-1's 445 m gears in the
Curved Hallway, `Metal Decoration * NoFog`, 79 m away) without its distance fog. User, playtest of 2026-11-18 (0-1 pose
2): the cogs "don't look properly lit": flat orange with every light off. The orange was the fog colour (0.52, 0.24,
0.11) added after the trace: raw frame (8, 3, 1) in the cog's region, final (113, 51, 25), run
`20261118-234907_playtest-materials_alt`. Now a proxy material carries `_NoFog` from its source's `_FOG_OFF`, the hit
sets payload flag `ULTRART_FLAG_NOFOG` (0x40), PathTrace keeps it in `_Surface` as the sign bit of emission.b's half
(emission is never negative; `SurfaceEmission` masks it, `SurfaceNoFog` reads it), and the fog pass leaves such pixels
alone (`_UltraRTSurface`, at the depth-matched trace texel of the 2x2 through the guard band, as Composite.shader picks
it; 2026-11-19, merged with `t3/stained-glass`, which had the same idea). Atlas slots get it through their variant
(`AtlasVariant`): 1-4's stained-glass windows (docs/materials.md, "Stained glass"). Bench `plugin._noFogSkip=0` = before. **Not verified in game**: in the two verification
loads the gears were not in the traced image at that pose (raw frame black there; the level's state differs between
loads), so the before / after could not be shown.

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

### Balance (2026-11-14 evening)
User report: "overall still a touch too bright", probably the fog volumetrics in many levels. Measured on the user's poses (`bench/suites/exposure-pm.json`, mean screen luma, game / relight / relight with `vol=0`): the haze added 10-20% in lit outdoor scenes (1-1), 40-50% at night in 1-4 (drift 34 / 54 / 38), and turned dark levels into walls of glow: 7-3 "tree a" 10 / 97 / 14 (a white spot light lighting the air), 4-4 "v2 chair" from black to an orange screen (the sun lighting 100 m of air in front of an unlit area). The split by term (`bench/suites/vol-balance.json`, `vol.sun=0` / `vol.lights=0` / `vol.emissive=0`): local lights in 7-3, 6-2 and 1-4, the sun in 4-4; emissive surfaces negligible.

Changes: `strength` 1 → **0.65**, and a cap on what the haze may add to a pixel: the composite's soft shoulder now saturates at `maxAdd` (0.25, display units) instead of at white, so shafts and halos keep their shape but can't fill the screen. **Dark fog is an absorbing medium** (`darkFog` 0.25): the medium's scattering colour used to fall back to white when the level's fog colour was black, so 7-3's garden, whose game view fades into pitch black, got a bright grey sky from a large white spot light (reported with "7-3 tree a"). The scattering albedo now follows the fog colour's brightest channel, full from 0.25 up: black fog (7-3, 2-1, 2-2, 4-1, 2-3) scatters nothing, 1-4's night blue (0.10) 40%, coloured fogs (1-1, 0-5, 3-1, 6-x, 8-2) as before. Old behaviour: `vol.darkFog=0`. Mean screen luma, game / before (`vol.darkFog=0`) / after, same run (`bench/suites/noise-pm-look.json`): 7-3 tree a 7.1 / 47.2 / **11.5**, 7-3 tree b 16.2 / 44.7 / 31.9, 1-4 drift 34.0 / 48.3 / 43.8, 1-4 lamps 23.4 / 30.9 / 27.9.

The menu entry was renamed (`[Volumetrics] ShaftStrength`): config files keep the default they were first written with, so the old `Strength` key would have pinned 1. Old look: `vol.strength=1+vol.maxAdd=0`.

### Dark rooms: 3-2 and 6-1 (2026-11-15, branch `t3/relight-haze-match`)
Report (lighting-overrides review sheet `20261115-032006_lights-verify`): mean screen luma (HUD and viewmodel masked)
against the game, relight on master ee52438: **3-2 eye x4.40** (an orange haze filled the flesh cave, the eye's
underside clipped to flat yellow), **3-2 wall edges x2.25** (a flat haze where the game is black, slab edges standing
out), **6-1 coffins x1.42**. The user's earlier playtest shot of the eye was far less hazy.

**Bisect.** Earlier bench screenshots of the same pose: 45-49 luma on every run up to 23:28 (11-14; ab44f1e,
b08dbb4), 80-101 from 03:01 (11-15; de95ed3, 33c2399, 6343ee7). 6-1 coffins (~25) and 3-2 wall edges (~10.5) did
not change over the night, and merge-regress's 6-2 start stayed at 24.5-26.2 across every merge. Scratch builds of
the two sides of the suspect merge, same pose and run (`haze-bisect`, `20261115-035146` / `-035215`):

| Build | game | relight | relight, lit particles off |
|---|---|---|---|
| 08c3cd9 (master before the vfx merge) | 22.8 | 47.2 | 44.1 |
| 35ca87d (vfx merge) | 23.7 | **101.6** | 45.5 |

**The vfx merge (35ca87d) made 3-2 twice as bright, through the lit-particle swap, not bloom.** The merge's
`LitParticles.Swap` re-applies the `_PL*` configuration after the shader change (values set while a material had the
game's shader were not always kept). After it, 3-2's `HeatWaves (2)` - one Master additive `Smoke` emitter (a 50 m
rectangle) that fills the whole eye cave - is drawn as intended: additive smoke multiplied by the RT lighting (up to
0.6 of its art, docs/particles.md). The game lights that material with its flat ambient only, which is black in 3-2,
so the game barely shows it; dozens of overlapping relit sprites around the eye's bright light became the orange veil.
The block pattern in it is the particle lighting volume's froxels.

**Split by term** (`haze-diag`, `haze-diag2`, `haze-test`; master's defaults):

| Pose (game) | relight | lit particles off | vol=0 | particles off + vol=0 + bloom off |
|---|---|---|---|---|
| 3-2 eye (23.8) | 96.8 | 39.8 | 66.8 (bloom off 66.1) | 28.8 |
| 3-2 wall edges (4.9) | 9.5 | 9.7 | 5.2 | - |
| 6-1 coffins (18.3) | 25.4 | 25.7 | 20.5 | - |

With the particles on, bloom and haze were each worth about the same in 3-2, and switching off either removed both:
**bloom was blooming the haze**. Its "what the transparents added" source (docs/vfx.md) is the target after the
transparents minus the copy before them, and the target it read already had the in-scattering added (the scatter pass
runs first in the same `AfterForwardAlpha` slot), so lit air near a bright light counted as a bright transparent.

In 3-2 wall edges and 6-1 the haze was the rest. 3-2 has the game's fog **off**: the medium falls back to
`minDensity` with full white scattering, so the cave's room lights (range 20-60 m) lit 0.002/m of air. In linear light
that is little (the exposed in-scattering alone averages about luma 7), but added to pixels at 5/255 it doubles their
display value (gamma): the flat veil over black. 6-1 has dense red fog (0.0087/m, capped to 0.008) and red room
lights of range 20-30; per term `vol.lights=0` takes 25.7 to 22.7, emissive surfaces nothing, `vol=0` 20.6 (which also
drops the game's own fog from RT pixels, so it is not the target).

**Fixes.**
1. *Atmosphere particles keep the game's look* (docs/particles.md, "Atmosphere"): particle systems whose emitter and
   particles both span 30 m or more are not relit. The volumetric pass already lights the air; a room-filling emitter
   relit as smoke counted it twice, in art the game draws at ambient. 3-2: `HeatWaves (2)` and a 44 m cone of dust
   `Dot`s; 8-2 `Rain/Mist`; P-2's street and idol arena `Mist`s; `1 - Opener/Mist`. Vent gas, debris trails and gore
   mist stay lit.
2. *Bloom from before the haze*: the bloom prefilter reads the volumetric pass's pre-haze copy (the target after the
   transparents, `Volumetrics.PreHazeCopy`) for its transparents term. No extra copy; the haze itself never blooms.
3. *Clear air where the game's fog is off* (`clearAir` 0.25): as with black fog ("Balance"), the game's frame has no
   glowing air there; a quarter of the scattering keeps faint halos and shafts (`clearAir=0` took 3-2 wall edges to the
   game exactly, 5.1, `0.3` to 7.6).
4. *Toe* (`toe` 0.002, composite): `s *= L / (L + toe)` on the exposed in-scattering, so a faint even veil (luma
   under ~10) fades out while halos and shafts well above it keep their level.
5. *Max density 0.005* (was 0.008): dense-fog levels (6-1 0.0087, 0-5 0.0079, 1-4 0.0185, 0-1 0.0055) scatter less
   from their room lights; the game's own fog colour, applied exactly by pass 0, is unchanged.

Candidates (`haze-cand`, `20261115-042341_haze-cand_alt`): A = 3 + 4, B = A + 5. B kept every regression pose within
1-2 luma (0-1 metal 60.6 -> 59.8, game 63.9; 1-1 start 189.9 -> 189.6, game 187.7; 6-2 start 23.9 -> 22.8, game 13.8;
0-5 lava 73.1 -> 70.9, game 62.0, the lava glow keeps its look; 8-2 intro day 64.4 -> 64.9, game 69.8) and is the
default.

**Results** (`haze-verify`, `bench/results/20261115-043444_haze-verify_alt/sheet.png`; "master-like" = this build
with `pl.atmosphereSize=0+vol.clearAir=1+vol.toe=0+vol.maxDensity=0.008`, the bloom fix can't be switched off; masked
mean luma, x the game; pathtrace with DLSS RR):

| Pose | game | relight master-like -> branch | pathtrace master-like -> branch |
|---|---|---|---|
| 3-2 eye | 20.2 | 95.9 (x4.75) -> **29.8 (x1.48)** | 65.1 (x3.23) -> **32.1 (x1.59)** |
| 3-2 wall edges | 4.6 | 9.5 (x2.05) -> **6.2 (x1.34)** | 9.7 (x2.10) -> **6.1 (x1.32)** |
| 6-1 coffins | 18.3 | 25.4 (x1.39) -> **24.1 (x1.32)** | 25.5 (x1.39) -> **23.6 (x1.29)** |
| 6-1 candles | 10.0 | 15.6 (x1.56) -> 15.3 (x1.54) | 14.6 (x1.46) -> 14.7 (x1.47) |
| 1-4 lamps | 21.2 | 25.4 (x1.20) -> 20.7 (x0.97) | 24.9 (x1.17) -> 20.4 (x0.96) |
| 8-2 b (lamp cones, rain) | 28.2 | 51.4 -> 51.4 | 51.1 -> 51.0 |
| P-2 vent smoke | 9.5 | 16.2 (x1.70) -> 15.0 (x1.57) | 18.1 (x1.91) -> 16.8 (x1.76) |

Colour: 3-2 eye's tint error (sum of |normalised rgb - game's| x 100) 29.7 -> 10.5, wall edges 15.9 -> 8.3. The eye's
texture and its lit underside's gradient show again; P-2's vent gas is still lit red, 8-2's streetlight cones and rain
are unchanged.

**What is left.** 3-2 eye x1.48: `vol=0` alone gives 26.7, so the remaining haze is the cave's lights at a quarter
strength; the rest is relight's lighting, the eye's top lit where the game leaves it dark (light overrides, not
haze). 6-1 coffins x1.32: relight's lighting is x1.12 without any haze (docs/lighting-overrides.md); the red room
lights' haze adds the rest (`vol.hazeRange=10` 23.7, `vol.lights=0` 22.7). 1-4's lamp halo is softer than before (the
toe and the density cap; the frame now matches the game's brightness, it was x1.20). 6-1 candles and P-2 are relight's
lighting (x1.5), not haze.

Cost (exclusive perf run `bench/results/20261115-044813_haze-perf`, 2560x1440, branch vs master-like in the same
pass): GPU totals equal within noise (3-2 eye relight 3.976 vs 3.969 ms, 6-1 coffins 6.085 vs 6.085, 8-2 b 6.412 vs
6.405, pathtrace likewise; 0-5 lava +0.08 relight / -0.18 pathtrace, run noise in opposite directions). The toe is a
few ALU ops in the composite, the bloom reads an existing copy, and the atmosphere check runs with the particle scan
every 5 s (one `FindObjectsOfType<ParticleSystemRenderer>`; renderers are decided once).

### Development notes (for whoever tunes this next)
- The first version (uniform 4-candidate light picks, area-uniform emissive points, 3x3 clip box, alpha 0.06) boiled
  in 1-4 (block sigma 1.8-2.6): most candidates missed every light, small emitters gave rare huge samples, and the clip
  box from 9 one-sample neighbours threw the history away. Enumerating lights, clipping the equiangular range to each
  light's reach, the 5x5 filter, alpha 0.03 and the emissive clamp each mattered.
- `debugView=2` (raw samples) showed 1-4's blotches came from sparse emissive samples (most pixels 0, a few very
  bright): fixed by the exact back-face rejection and the emissive clamp. NaN/Inf guards were added while checking and
  stay (cheap); whether NaNs ever occurred was not established.
- Units: a first build forgot the factor pi (lighting = irradiance / pi), making every term pi times too dark.

### Lamp cones (2026-11-14 pm, branch `lights-pm`)
User report: in 8-2 the streetlights' light cones looked the same with RT on and off. They are the game's fakes:
additive `ULTRAKILL/Master` meshes (`LightShaft`, material `LightPillarStreetlight`, colour (0.72, 0.58, 0.40, 0.15),
static-batched), one per streetlight, hanging from a **point** light (range 30, intensity 2, warm). The volumetric pass
lit the air around that point light as a round halo, and the haze range weight left 23% of it (range 30), so relight
showed a faint glow plus the fake cone drawn on top.

`src/UltraRT/LightShafts.cs` + `Plugin.Shafts.cs`. Once per RT scene, the first time the volumetric pass runs, every
additive Master mesh named like a shaft (`lightshaft`, `lightpillar`, `lightray`, `godray`, `shaft`; not lava) is read
back (its submesh of the static batch, one GPU readback per combined mesh) and matched to the point/spot light at its
apex: a vertex within 1 m (or 10% of the mesh length) of the light. The cone is fitted to the far half of the vertices:
axis = their mean direction from the light, half angle = the widest of them; it must be 3-60 degrees and no longer than
3x the light's range (4-3's 190 m light-pillar boxes fit a cone from their base otherwise). 8-2: 18 lights, 32 degrees,
tilted 13 degrees toward the street, 21.7 m long (59 shaft meshes; 38 have no light at their apex: window shafts).

Per light the volumetric pass gets two more float4 (`_VolLightCone`, `Volumetrics.raytrace` `ScatterCone` /
`ScatterWeight`): the fitted cone replaces the light's own shape for the medium only (`ClipSpot` clips the ray segment to
it, the in-scattering gets a soft edge over the outer 25% of the half angle), and the light's scattering weight becomes
`shaft.strength` (3) in place of the haze range weight. Surfaces keep the game's point light. While the volumetric pass
runs (relight; pt-dn when volumetrics are on there), the fake meshes are disabled (`renderer.enabled`), and
re-enabled whenever it doesn't (any other mode, F7, Ctrl+F6, scene change). A light that moves away from its mesh keeps
its fake.

| Flag | Field (`shaft.`) | Default | |
|---|---|---|---|
| `-ultrart-shafts on\|off` | `enabled` | on | |
| `-ultrart-shaft-strength F` | `strength` | 3 | scattering relative to a light with haze weight 1 |
| | `edge` | 0.75 | inner fraction of the half angle at full strength |
| `-ultrart-shaft-keepfakes` | `keepFakes` | off | draw the game's meshes on top as well |

Result (`bench/results/20261114-185737_lights-pm-check2_alt/8-2_b_*`): each streetlight now has a real cone of light in
the rain, the same shape and direction as the game's, falling off with distance and shadowed by the lamp head and post.
`shaft.strength=10` looks the same as 3 (the per-pixel sample clamp, `maxSample`, caps the brightest cones); global
density and exposure are unchanged. Cost: the match takes 42 ms once per 8-2 scene build (17 ms in 4-3); per frame a
256-entry buffer upload and two extra buffer reads per light sample in the trace.

### Thrown items (2026-11-14 pm)
User report: in 4-3, holding the torch and using the whiplash, the game clearly shows the torch lighting the walls as it
flies; with RT on it is very subtle. Bench: `bench/suites/lights-pm-throw.json` (the user's route with the torch held via
the bench's `holdtorch` and the whiplash pressed `ShotDelay` before each capture; `bench/results/20261114-192647_lights-pm-throw`,
`20261114-193339_lights-pm-throw`). Findings:
- With the torch held (its light pinned to the player by `Torch.Update`) relight and the game light the room alike.
- Thrown, the torch lands on the door ~10 m away. The game's room goes black: ULTRAKILL lights per vertex, and the big
  wall quads have no vertex near the torch. Relight lights per pixel, so the torch still lights the room from the door
  (mean lighting L 1.23 linear, the torch's orange; without the torch the room is L 0). The radiance cache adds about 4%
  of that (cache off: L 1.18), and auto exposure (off: same image) and ReSTIR's per-pixel moved-light history cut
  (`ls.perPixelLightChange=1 + ls.lightChangeKept=1`: same) don't change it: it is the torch's present light, not lag.
- What was missing: the medium skipped every light in the player's or camera's hierarchy, and a thrown item stays
  parented to the hand. Lights in those hierarchies are now skipped only within 1.5 m of the eye
  (`AttachedLightReach`, `Plugin.Volumetrics.cs`), so a thrown torch (or skull) carries its halo through the air; held,
  nothing changes.
- **2026-11-18 (`t3/dark-0-S`)**: "held, nothing changes" was true of the torch (`Torch.Update` pins its light to the
  eye: 0.2 m) but not of a carried skull, whose light hangs 2.1-2.9 m from the eye in world space (the HUD camera draws
  the item nearer). Its halo fogged 0-S's dark corridors teal: far end of the corridor 11.9 luma against the game's 1.7.
  The reach is a setting now, `attachedReach` 2.5 m (`vol.attachedReach`): 5.7. The first metres of a throw lose their
  halo; past 2.5 m it is there as before. The log prints each carried light's distance once
  (`Volumetrics: carried light '...' packed N m from the eye`). Run `20261118-231755_dark-0-S-diag_alt`, sheet
  `bench/results/review/dark0s/corridor_before_after.png`.
- Not done: matching the game's per-vertex look (the room going dark as soon as the torch leaves) would need a steeper
  falloff for moving lights; relight keeps the physical one.

### Handed-over lamps in the air (2026-11-19, `t3/trailer-v4-light-halo-fix`)

User report after trailer v4: a halo round the street lamps in several levels (P-2, 1-4) "looks like a sprite". What
draws it, proven with variants at the lamps up close (`bench/suites/light-halo-diag3.json`, `LookAt` `mat:Light` at
5-6 m; sheets `bench/results/review/halo-fix/`, `c3_door.png`, `c3_p2.png`):

| Switch | The glow round the lamp head |
|---|---|
| the game's own frame (`off`) | none: the lamp prefab has no glow sprite or flare (wrong: it has one, `Point Light/Square`, faint; its 16x16 point-filtered texture is the blocky cells round the lamp, docs/vfx.md "Glow sprites") |
| `vfx.enabled=0` (bloom and the other screen effects off) | stays |
| `vol.lights=0` | stays |
| `vol.emissive=0` | gone |
| `vol.strength=0` | gone |
| `lc.handover=0` (the lamp's game light instead of its glass) | a halo of about the same size, from the light (`vol.lights`) |

So it is this pass's in-scattering from the lamp glass: since `t3/emissive-all` the street lamps' lights are handed
over to their glass (docs/lighting-overrides.md, "Handover"), and the glass, an emitter in the emissive triangle table,
lights the level's fog round the head. A lamp in fog has a halo; this one was too strong. The handed-over part emits
its light's output **with the class policy**: fixtures x2.5 (the "punch", docs/lighting-overrides.md). The policy also
has `medium` 0.4, so that a boosted light's haze stays where it was. The emitter dropped that cut, and scattered the
full x2.5 (and the rule's handover gain: P-2 2, 1-4's lamps more) in the air. That is the "bloom": a round soft ball
around a bright rectangle, the same from every side.

The fix: every emitter triangle carries an **air share** (the high half of its third record's `w`, which held only
the blue of its radiance), 1 for an ordinary emitter (lava, screens, panels: unchanged) and the handed light's resolved
`medium` for a handover part (several lights on one part: their mean, weighted by output). `Volumetrics.raytrace`
multiplies the emitter sample by it (`SampleEmissivePoint`). The surface lighting does not change.

Measured 2026-11-19 (`20261119-140528_light-halo-fix_alt`, 1-4's bridge lamp at 6 m, mean luma of the sky beside the
head, 1-4's moonlit fog): the game 26.1; before (`vol.emitterMedium=0`) 43.9; now 38.8; without any emitter haze
(`vol.emissive=0`) 32.1. The emitters' part of the halo went from +11.8 to +6.7, 1 m out from +8.5 to +4.0: about
half. P-2's lamps flicker at random, so its frames do not compare by number; by eye the head's glow is softer and the
red haze the street lamps put over the far arch is lower.

The blocky cells in the halo the user saw after this fix are not this pass: they are the game's own glow sprite
(16x16, point filtered), in the game's frame too; filtered bilinear since 2026-11-19 (docs/vfx.md, "Glow sprites").

Options if the user wants the halo gone as well, not halved (not done):
- `vol.emissive` lower for every emitter (0.5 now; 0.2 halves lava's rising glow too);
- the handover gain out of the air as well as the punch (the gain calibrates the part's light on surfaces, about x3 in
  1-4: air share = medium / gain would leave a tenth of today's halo in 1-4);
- a per-rule air share in `_rules.json` (a `handover.medium`), for the lamps that should glow in the fog and those
  that should not.

### Not done / limits
- The game's fake light-shaft meshes that hang from a light are replaced by that light's volumetric cone (see "Lamp
  cones"); shafts with no light at their apex (sun shafts through windows, `Godrays`) and mist particles are left as
  they are, on top.
- Transparents get the glow of the air behind them, not of the air in front of them: right for far grates, water and
  glass, too much for alpha-blended smoke right in front of the camera.
- Moving lights (7-2's searchlights, flashes) still lag by the temporal history; the haze range takes the brightest of
  them out of the air.
- Homogeneous medium: no height fog, no noise texture.
- One light and one emissive sample per pixel per frame by default (`lightSamples`, `emissiveSamples`); very large light
  counts (> 128) fall back to RIS.
- The haze range is a heuristic on the light's range: a big-range lamp that really is a lamp loses its halo, and a
  short-range light the level uses as a fill keeps it.

### Eye beams (2026-11-15, user note 4)

3-1 (user pose 1, the staircase room): "the light beams coming from the eye don't seem to cast shadows". Each eye
(`EyeWithSocket/Eyeball`) holds a Spot Light (range 80, x10, 60-90 degrees) and, parented under it, four nested ProBuilder
frusta `Cube`..`Cube (3)` with `LightPillar 1F NoFog` (additive orange `(1, 0.5, 0)`, alpha 0.25, two-sided). Those
meshes are the beam: drawn on top of everything, unshadowed. The camera in this room stands inside the outermost frustum, so
they also give the room its orange cast. The same prefab sits in 3-1's circular and bridge arenas.

What blocked a shadowed version:
- **Matching.** LightShafts fitted cones only to meshes whose tip is at a light; these frusta start 2-10 m out. Beam meshes
  parented under a spot light are now matched to it (`LightShaftSettings.parentSpots`): axis from the far half of the vertices
  (within 20 degrees of the spot's forward), half angle the widest, nested meshes widen one cone, and the cone follows the light
  if it moves (axis kept in the light's space). A cone whose light or mesh is destroyed (checkpoint respawns re-clone rooms)
  makes LightShafts fit again, at most once a second.
- **The eye blocked its own light.** The spot sits inside `BigEye` (39 m across) and the flesh socket `EyeWithSocket` (38 m),
  ordinary geometry, so in RT the eye lit nothing: the floor under the beam was dark where the game's is beige. The renderers
  around a beam light whose bounds hold it become its *housing* (`SceneProxies.AddHousing`, at most 64 m, beam lights only):
  fixtures, and the light skips fixtures (as statue orbs, docs/scene-proxies.md). Now the eye lights the ribs and the floor,
  and the ribs and V1 cast shadows from it (`LightShaftSettings.housings`, log `Light housing: ...`).
- **Look (first try, replaced).** A constant-density glow of the beam colour inside the fitted cone, added to the haze: the
  composite adds haze in linear light under a soft cap (`vol.maxAdd`), and it read as an olive haze over the whole room, not
  the game's cone (`shade-1115g`).

#### Beam channel (2026-11-15, branch `t3/eye-beams-channel`)

The beams are now drawn by a separate channel of the volumetric pass, on by default: the game's look where the beam is
unoccluded, cut by traced shadows where it isn't. Code: `LightShafts.cs` (`BeamHull`, `FitHull`), `Plugin.Shafts.cs`
(`VolumetricsBeamBuffer`, `DrawBeamMeshes`), `Volumetrics.cs` (`Filter`, beam textures), `Volumetrics.raytrace`
(`BeamVisibility`), `VolumetricsComposite.shader` (pass 2, `fragBeams`).

What the game draws (material `LightPillar 1F` / `1F NoFog`, read with UnityPy): `ULTRAKILL/Master` with `TRANSPARENCY`,
`SrcBlend SrcAlpha`, `DstBlend One` (additive, not alpha-blended), `_Cull 0` (both sides), `_Color` (1, 0.5, 0, 0.25) in the
staircase room and (1, 0.37, 0, 0.25) in the circular and bridge arenas, a white texture, and **vertex colours**: each
frustum is white at the eye end and black at the far end (4-vertex pyramids in the circular arena, 8-vertex frusta
elsewhere), so the beam fades along its length. A ray through the beam adds the colour once per face it crosses in front of
the depth, so nested layers stack and from inside the widest frustum only its far side shows (the room's orange cast).

1. **Hulls** (`FitHull`, once per scene with the cone fit): each beam mesh's convex hull from its vertices (planes through
   every vertex triple with all vertices on one side; at most 8 planes, 64 vertices; every vertex must lie on the hull, else
   the game keeps drawing that mesh), in its light's space so it follows the eye. Per face, a linear fit of the vertex
   brightness over the mesh triangles lying on it (0 where the mesh has no face). Log: `Light shafts: beam hull ...`.
2. **Visibility** (`BeamVisibility`, in the volumetric trace at 1/4 resolution): per hull the ray crosses, its weight
   `w` (the hull's colour x the faces it draws in front of the depth) and `beamSamples` (1) uniform points on the ray's part
   inside the hull, each with a shadow ray to the eye's light (geometry only, stopped where the mesh begins: the eyeball is
   the light's housing). Output `(sum w x lit, sum w)` into a second UAV, filtered by the same temporal + a-trous kernels as
   the haze with its own history (`beamAtrous` 2 passes). The composite upsamples both sums with depth weights and divides:
   the lit fraction of the beam along each pixel's ray, smooth across the hulls' hard edges.
3. **Colour** (`beamMeshes` 1): the game's own beam meshes, drawn with `DrawMesh` (their mesh, material, property block and
   pass: `ForwardPass` takes a ForwardBase / Always pass and else pass 0, which is the beam shader's only pass, LightMode `Vertex`) into a screen-size buffer against the game's depth, in the late command buffer after the
   transparents; the camera's targets are bound again afterwards. Their renderers stay disabled (`UpdateFakes`, only for
   hulls drawn in the last 2 frames; the rest keep the game's meshes). Pass 2 adds `colour x visibility` to the target
   (`Blend One One`, display space, as the game adds the meshes), before the haze's copy, so the bloom sees them as it sees
   the game's transparents.

   The analytic alternative (`beamMeshes=0`: each hull's colour x per-face vertex brightness, per screen pixel) matches the
   meshes' shape, nesting and gradient, but the Master shader dims distant faces in a way the material alone doesn't
   explain (`DISTANCE_FADE` is on; the far side of the outer frusta came out 1.3-3x too bright). Drawing the game's meshes
   removes that guess; the analytic hulls remain for the visibility weights and as the fallback when the camera depth is
   multisampled.

Measured (2560x1440, RTX 5070 Ti, trace scale 0.5; `bench/suites/beams-3-1*.json`):

| | |
|---|---|
| Model check, unshadowed channel (`shaft.beamShadows=0`) vs the game's meshes over the same relight image | mean abs diff 1.1-1.3 of 255 (relight noise level) at the user pose, under the eye and at the back of the room; analytic colour 3.3-3.5 |
| Look | the beam above the rib cage as in the game; below it, dark where the spine and ribs shadow it, with shafts through the gaps (clearest from the walkway inside the beam, `3-1 beams floor`); the eye's housing does not block it |
| Static noise (user pose, burst of 64, masked) | relight sigma 0.22 / block 0.17 (game meshes 0.21 / 0.16); pathtrace 0.32 / 0.24 |
| Sweep (15 deg/s) | block sigma 1.11 vs 1.04 with the game's meshes (camera motion dominates); no crawling shafts in the frames |
| GPU (`beams-3-1-perf`, `-Perf`) | `volumetrics` 0.21 -> 0.33 ms, `vollate` 0.07 -> 0.20 ms: about +0.25 ms in relight and pathtrace; plugin CPU +0.02-0.03 ms |

| Field (`shaft.`) | Default | |
|---|---|---|
| `beams` (`-ultrart-beams on\|off`) | on | the channel; off = the game's meshes, unshadowed |
| `beamStrength` | 1 | brightness relative to the game's meshes |
| `beamShadows` | 1 | 1 = traced visibility, 0 = none (A/B against the game), in between = a mix |
| `beamMeshes` | 1 | colour from the game's meshes (1) or the analytic hulls (0) |
| `beamSamples`, `beamAtrous` | 1, 2 | shadow rays per hull and low-res pixel; a-trous passes over the visibility |
| `maxBeamHulls` | 32 | nearest first, within 300 m; the rest keep the game's meshes |
| `beamFaces` | 0 | analytic diagnostics: 1 entering faces only, 2 leaving faces only |

Limits and taste calls:
- The channel shadows what the game's meshes draw, nothing more: rays that end on the floor inside the widest frustum add
  nothing (the game draws no face there), so the floor under the beam gets its light from the eye's spot (relight, traced),
  not from the channel.
- Below the rib cage the beam is mostly dark at the user's pose: that is what the spine and ribs do to light from the eye.
  `shaft.beamShadows=0.7` keeps some of the game's glow there if that reads better.
- The visibility is the lit fraction along the whole ray inside a hull, at 1/4 resolution: thin shafts narrower than about
  4 screen pixels blur, and a moving camera shows the temporal filter's lag like the haze.
- Only meshes under a spot light are drawn this way (`parentSpots`); lamp cones (8-2) keep the cone haze.

#### The Stairways too bright: the eye's light and its haze (2026-11-20, branch `t3/3-1-staircase-brightness`)

User: "the 3-1 staircase room is far far too bright ... in sdr, too. i think it's partially related to the much less
dense haze over stock". The room is this one (the tour's "6 - Stairways", the beams suites' poses). Before the change
RT surfaces metered 2.65 EV over the game's there (`game match: game -4.10 vs RT -1.45 log2`). Auto exposure held 0 EV:
the mean, 0.35, is under the key, and the game match's down side is off by default.

**Attribution** (`beams-3-1` user pose, pathtrace, mean luma: game 47.6, shipped 74.2). These changed nothing: haze off
(`vol=0`) 77.4, screen effects off 72.5, beam channel off 77.5, lit particles off 74.3, auto exposure off 74.3. Every
level light off: 35.5. The light census (`-ultrart-light-census`) shows two lights in the room: the eye's `Spot Light`
(range 80, x10, 90 degrees, 62 m above the walkway) and its child `Spot Light/Point Light` (range 30, x10, pointing
up; class `fill`, x1). The eye's spot was class **`fixture:orb`** (rule `statue orb`): the eyeball is an orb fixture
around its light, so the punch policy's x2.5 applied to the key light of a 60 m room. With it off: 40.6; at x1 (=
`lc.light.Eyeball=0.4`): 51.5.

**Fix, part 1: the eye at the game's strength.** A rule ahead of `statue orb` in `data/lights/_rules.json`
(`eye beam lights`: `(EyeWithSocket|EyeballParent)*/Eyeball/Spot Light`, 3-1's three eyes and 3-2's opening eye) gives
class `fixture:eye`: x1, `reach` 0, `medium` 1 (docs/lighting-overrides.md, "3-1's eye rooms"). 3-2's eye has its own
fitted entry (x0.53) and is unchanged by it.

**Darkening was tried and dropped.** `followGame` 1 in `Level 3-1.json` (the 7-1 / 7-3 / 0-S policy) brought the
room to the game's mean: user pose x0.91, under the eye x0.90, the game's dark pixels +59 -> +6 (pathtrace). The user,
on the frames: "the eye, and the light beams *look* bright, but the environment is dark. would it not be more sensible
to fix the issues here with a denser haze? it's fine if the lit areas are brighter than stock". The exposure scales the
lit surfaces only, and the beam meshes, the haze and the eye's glow keep their brightness, so a -1.6 to -2.2 EV pull
left bright beams over a dim room. Not shipped.

**Why the room had no haze.** The game's fog is off in the eye rooms (`RenderSettings.fog` false, `NoFog` materials).
Stock's orange veil is the eye's `LightPillar 1F NoFog` frusta around the camera. Our medium there falls back to clear
air (0.002/m, albedo 0.25, white), and two things kept the eye out of it:

1. **Haze shadow rays hit the eye's own housing (bug, all housed lights).** `LightSample` in `Volumetrics.raytrace`
   traced its shadow rays against geometry and fixtures (0x05) for every light. A light that skips fixtures
   (`_Lights[i*4+3].z` in (0.1, 0.5): statue orbs, the eyes in their 39 m eyeball housing) lights surfaces past its
   housing, but every one of its haze samples was blocked by it, so its haze was exactly 0. The eye's `medium` at 20
   or 60 with a 0.005/m medium changed the user pose by 0.1-0.5 luma (`20261120-214159_tmp-31b-haze_alt`). Now such
   a light tests geometry only (0x01), as its surface shadow rays do. This also gives statue orbs their halo back.
2. **The haze range weight.** `hazeRange` 20 m weights a range-80 light's air by 1 / (1 + 4^3) = 1/65.

**Fix, part 2: the eye fills its room with haze.** `Level 3-1.json` sets `"classes": { "fixture:eye": { "medium": 650 } }`.
That is x65 to undo the range weight, times 10 for clear air's scattering (0.002/m x 0.25 against 0.005/m x 1 in 3-1's
foggy rooms): the eye scatters as in a 0.005/m medium, with traced shadows through the spine and ribs. A level-wide
clear-air override (density 0.005, albedo 1 where the fog is off; prototyped, not shipped) gave the same eye haze but
also lit every other light's air: the Bridge Arena's first tour view went x1.90 -> x3.25 with a brown veil where the
game is black. The eye's own weight leaves that view at x1.90, as before.

Measured (alt, 2560x1440, SDR, pathtrace, mean luma ratio to the game with HUD and viewmodel masked, `tools/lights/score.py`;
"dark" = the game's dark pixels, game -> RT; master `20261120-213112_tmp-31b-final_alt`, after
`20261120-215814_tmp-31b-ship_alt`):

| Pose | game | master | after (pathtrace) | after (relight) | dark, master -> after |
|---|---|---|---|---|---|
| user pose (`beams-3-1` "user") | 53.2 | x1.59 | x1.38 | x1.39 | +58.8 -> +31.8 |
| under the eye | 50.9 | x1.38 | x1.32 | x1.33 | +54.2 -> +28.3 |
| back of the room | 16.1 | x1.47 | x1.57 | x1.58 | +6.3 -> +11.5 |
| walkway (`floor`) | 46.5 | x1.42 | x1.30 | x1.30 | |
| 3-1 tour, 18 views (checkpoint views: the Stairways' eye is not lit there) | | x1.36 median, dark +2.9 | x1.35, dark +2.6 | | every view within 0.15 of master |

The look moved from the image's light to the air's: the walkway is no longer near white, the near bones stand dark
against an orange haze lit through the cone with shafts between the ribs, and the eye keeps its glow. The back view's
mean rises a little (the lit air under the eye). Lit areas stay brighter than stock, as the user allowed. Review stills:
`bench/results/review/3-1-brightness/` (main checkout). The haze costs nothing extra: the eye was already sampled,
and only its weight and a shadow-ray mask changed.

Seen on the way:
- **3-2's eye ring** (user: "it's got a halo around it"): a thin red octagon around the opening eye, the same on
  master. Not haze, flesh SSS, rim, screen effects or vertex displacement (`vol=0`, `mat.fleshSss=0`, `mat.fleshRim=0`,
  `vfx.enabled=0`, `plugin._displaceRange=-1` all keep it). `relight+up.debugView=3` shows the eye red (traced
  surface behind the game's): the trace goes through the eye's dark octagonal shell (`EyeballParent/Breakable`, flesh
  `Flesh5` with vertex displacement) and only its rim is left, the "statue rim" pattern of docs/scene-proxies.md. Open.
- An emitter mask on the eye's material (`lc.glow.M_BigEye=1,0.2`) brought its bright core back under the
  `followGame` pull (max 160 -> 245, the game's 245); without the pull it is not needed. Not shipped.

#### Window beams (2026-11-18, branch `t3/window-beams`; **off by default**, `shaft.windowBeams`)

Plan A5: the beam meshes that have no light (a shaft of daylight through a window: 1-1's hall and corridors, 0-1's
Grand Hall, 6-1, 6-2, 4-4) stay raster and unshadowed. First step, behind a switch: they go through the beam channel
with a **fitted source** instead of a light.

- **Fit** (`LightShafts.MatchFree`, per mesh no lamp cone or parent spot took): the axis is the direction from the
  bright vertices to the dark ones (the game fades its beams away from the source; every mesh fitted so far had this
  gradient), else the long axis from the narrow end to the wide end, else downwards; no gradient and not elongated, or
  no way to tell the ends apart: the game keeps the mesh. The hull is the beam channel's (`FitHull`: convex, at most 8
  planes and 64 vertices), in the mesh's own transform when it is not static.
- **Source.** The shadow rays go to a point far behind the source end (4 beam lengths, at least 60 m: nearly parallel
  to the beam) and stop at the source end's plane. The mesh usually starts in or behind the window's wall, so that end
  is moved to the room side of whatever level collider lies in the beam's first 40 % (physics rays down the centre
  and from every vertex of the end; log `source end moved N m past the window`). No shader change: the hull buffer
  carries the virtual point as its light.
- **Colour**: the game's own mesh, as for the eye beams.

Fitted (`20261118-015523_window-beams_alt`): 6-1 61 of 127 shaft meshes (30 more are lamp cones), 6-2 33 of 39, 4-4
15 of 32, 1-1 12 of 27, 0-1 10 of 26. Left to the game: hulls of 9-12 planes, meshes of over 64 vertices (4-4's
`LightRays`), flat sprites, 1.8 m stubs.

**Why it is off.** In the frames (`20261118-020616_wb-quick_alt`, 1-1's hall and torch corridor, 6-2) the traced
version does not read as shafts cut by pillars: it takes the beams' glow out of large parts of the room (mean luma
-3.1 and -4.9 of 255 in the two 1-1 views; nothing visible in 6-2's views). The game's beam meshes are wide slabs
that fill much of a room and reach through seating and walls; light that really came through the window would not be
there, so the trace removes it, and the room loses a glow the level was drawn with. What it needs before it can be
a default:

1. the pool of light and the air glow from a real source at the window (the plan's rect light with a narrow lobe),
   so what the trace removes from the fake is given back where light would be;
2. the stop surface along the wall instead of a plane square to the beam (the source end moved 8.3 m in 0-1's Grand
   Hall to clear a tilted wall: the first 8 m of those beams are then unshadowed);
3. a pose per level that looks along a beam with something standing in it (the tours do not), to judge the shafts.

`-ultrart-window-beams on`, bench `shaft.windowBeams=1`, suite `bench/suites/window-beams.json`. A switch of the
setting fits the scene again. Cost not measured (off).

