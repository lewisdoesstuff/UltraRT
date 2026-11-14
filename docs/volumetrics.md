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
- **Point and spot lights**: one per pixel from the plugin's `_Lights` buffer (the one PathTrace uses: torches, lamps,
  fixture lifts, effect lights such as muzzle flashes and explosions). Each light's importance is its power times the
  integral of 1/d^2 over the part of the ray inside its reach (2 x range, where its falloff window ends). With up to 128
  lights all are enumerated and one is picked in proportion (blue-noise random number); above that, RIS over
  `lightCandidates` (8) uniform candidates. The distance along the ray is drawn by equiangular sampling (Kulla & Fajardo
  2012) over the same clipped segment, which is exactly proportional to 1/d^2. One shadow ray, stopping 0.35 m short of
  the light like surface shadow rays (lights sit inside their fixtures). Falloff and spot cone are relight's (physical
  inverse square windowed to 2x range, in linear light). Directional and fill lights are skipped.
- **Emissive surfaces** (lava, glowing panels, screens): RIS over 6 points from the emissive light table
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
  at 64 frames. NaN/Inf samples are zeroed in the trace and in the filter.
- **Upsample** (in the composite): the 4 nearest low-res samples, each scaled to the pixel's own ray length
  (`min(d / d_i, 1)`: in-scattering grows about linearly with distance, so a thin foreground object in front of a far
  sample gets the right, smaller amount) and weighted bilinear x `exp(-((d_i - d) / (0.05 d + 0.1))^2)`. With no depth
  match, the closest-depth sample (scaled) is used.

### Composite
`VolumetricsComposite.shader`, a fullscreen draw into the camera target right after the RT composite (before the
water/glass refraction copy and the game's transparents), reading a copy of the target:

1. On pixels the temporal upsampler marks as ray traced (`_UltraRTUpsampled.a > 0`): the game's fog, exactly its
   formula and colour (display space, eye depth). Sky, the game's own pixels (glow quads, decals) and hybrid already
   have it.
2. Everywhere: `out = (in^2.2 + exposure x strength x S)^(1/2.2)`, then a soft shoulder on what was added
   (`c + h (1 - exp(-delta / h))`, h the room above c), so bright shafts over a bright sky saturate smoothly. The
   exposure is relight's auto exposure (what the lit RT image gets), read from its GPU buffer; 1 when auto exposure is
   off (pt-dn by default).

Transparent effects drawn after us (particles, the game's light-shaft meshes, water surfaces) get no ray-traced
scattering in front of them; their own (game) fog still applies.

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
| `-ultrart-vol-lights F` | `lights`, `lightCandidates` | 1, 8 | 0 = no point/spot lights |
| `-ultrart-vol-emissive F` | `emissive` | 0.5 | 0 = no emissive surfaces |
| | `maxSample` | 2 | firefly clamp |
| `-ultrart-vol-nofog` | `gameFog` | true | the game's fog on RT pixels |
| | `alphaMin`, `maxHistory`, `clipSigma`, `spatialSigma`, `depthTolerance`, `spatialDepthSigma` | 0.03, 64, 3, 1.5, 0.1, 0.05 | temporal filter |
| | `debugView` | 0 | 1 = the exposed in-scattering alone, 2 = the same without the temporal filter |

GPU timer slot `volumetrics` (after `upsample`): depth, trace, temporal, the target copy and the composite. The plugin
logs the medium every 10 s (`Volumetrics: medium 0.0035/m (fog linear 0-400 m, ...), tint (...), sun ..., N lights`).

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
- No ray-traced scattering in front of transparents (particles, water) drawn after the composite.
- Homogeneous medium: no height fog, no noise texture.
- One light and one emissive sample per pixel per frame; very large light counts (> 128) fall back to RIS.
