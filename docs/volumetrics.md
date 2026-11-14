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
  transmittance is 50% where the game's fog is half way. 1-1 0.0035/m, 0-5 0.0079/m, 1-4 0.018/m. Exponential fog
  uses its density. Times `densityScale`, clamped to `[minDensity, maxDensity]` (0.002 and 0.03 per metre). Where the
  fog is off or thinner (4-1, 8-3, any area that turns it off), `minDensity` gives clear-air shafts and light halos
  without any haze colour.
- scattering colour: the fog colour's hue (normalised to its largest channel), half way to white (`tint` 0.5): 1-1's
  shafts are warm, 1-4's bluish, 3-1's reddish. Black or no fog -> white. Albedo 1 otherwise: sigma_s = sigma_t x tint.
- phase: Henyey-Greenstein g = 0.4 (forward scattering; shafts brighten looking toward the sun).

The haze itself (the ambient, sky-lit part of the in-scattering) is the game's own fog colour and formula, applied in
the composite, so far geometry fades into the level's fog exactly as in the game, and into its fogged sky.

### Light
Per low-res pixel (one ray from the camera to the game's depth, or `maxDistance` 150 m; the sky counts as 10 km):

- **Sun**: the brightest shadowed directional light (fills excluded), in linear light: `(colour x intensity x relight
  exposure)^2.2`, as the relit surfaces see it. `steps` (8) stratified points along the ray, one blue-noise offset per
  pixel and frame (interleaved gradient noise, shifted by the golden ratio per frame), each with one shadow ray toward
  a point on the sun disk (`-ultrart-sun-angle`, as PathTrace's soft sun shadows). The transmittance is integrated
  analytically per segment. Shadow rays use geometry and fixtures (mask 0x05), no back-face culling (thin one-sided
  roofs and leaf cards block light from either side), first hit ends the search.
- **Point and spot lights**: one per pixel, picked by RIS over `lightCandidates` (4) uniform candidates from the
  plugin's light list (the same `_Lights` buffer PathTrace uses, so torches, lamps, fixture lifts and effect lights such
  as muzzle flashes and explosions are all there). The RIS target is the light's power times the integral of 1/d^2
  along the ray (the angle the ray subtends at the light), zero for lights whose reach (2 x range) misses the ray. The
  point on the ray is drawn by equiangular sampling (Kulla & Fajardo 2012), which is exactly proportional to 1/d^2,
  so a light right next to the ray costs no extra noise. One shadow ray, stopping 0.35 m short of the light like
  surface shadow rays (lights sit inside their fixtures). Falloff and spot cone are relight's (physical inverse-square
  windowed to 2x range, in linear light).

Emissive surfaces (lava, 4-1's glowing panels, screens) do not scatter yet: they would need the emissive triangle
table and its alias sampling in this shader.

### Resolution, temporal filter, upsample
- **1/4 resolution** (`block` 4: 480x270 at 1080p). `Volumetrics.compute` `Depth` reads the game's depth copy at each
  block's centre pixel, and the ray goes exactly through that pixel.
- **Temporal** (`Temporal`): a depth-aware 3x3 filter of this frame's samples (Gaussian on relative depth difference),
  the history reprojected by the ray's end point with last frame's matrices and validated by its stored ray length
  (10%), clipped to the 3x3 mean +- 2 sigma, blended with alpha = max(1 / length, 0.06), history capped at 32 frames.
- **Upsample** (in the composite): the 4 nearest low-res samples, each scaled to the pixel's own ray length
  (`min(d / d_i, 1)`: in-scattering grows about linearly with distance, so a thin foreground object in front of a
  far sample gets the right, smaller amount) and weighted bilinear x `exp(-((d_i - d) / (0.05 d + 0.1))^2)`. With no
  depth match, the closest-depth sample (scaled) is used.

### Composite
`VolumetricsComposite.shader`, a fullscreen draw into the camera target right after the RT composite (and before the
water/glass refraction copy and the game's transparents), reading a copy of the target:

1. On pixels the temporal upsampler marks as ray traced (`_UltraRTUpsampled.a > 0`): the game's fog, exactly its
   formula and colour (display space). Sky, the game's own pixels (glow quads, decals) and hybrid already have it.
2. Everywhere: `out = (in^2.2 + exposure x strength x S)^(1/2.2)`, then a soft shoulder on what was added
   (`c + h (1 - exp(-delta / h))`, h the room above c), so bright shafts over a bright sky saturate smoothly. The
   exposure is relight's auto exposure (the lit RT image gets the same), read from its GPU buffer.

Transparent effects drawn after us (particles, the game's light-shaft meshes, water) get no ray-traced scattering in
front of them; their own (game) fog still applies.

## Settings

| Flag | Field (`vol.<field>` bench variant) | Default | |
|---|---|---|---|
| `-ultrart-volumetrics on\|off`, config `[Volumetrics] Enabled`, **Ctrl+F6** | `enabled` (`vol=0\|1`) | on | |
| | `ptDn`, `hybrid` | true, false (`-ultrart-vol-hybrid`) | modes besides relight. Hybrid gets the in-scattering only |
| `-ultrart-vol-block N` | `block` | 4 | screen px per low-res px |
| `-ultrart-vol-steps N` | `steps` | 8 | sun shadow rays per low-res px |
| `-ultrart-vol-distance M` | `maxDistance` | 150 | |
| `-ultrart-vol-density F` | `densityScale` | 1 | x the fog-derived density |
| `-ultrart-vol-min-density F` | `minDensity` / `maxDensity` | 0.002 / 0.03 per m | |
| `-ultrart-vol-strength F` | `strength` | 1 | |
| `-ultrart-vol-g G` | `anisotropy` | 0.4 | |
| | `tint` | 0.5 | |
| `-ultrart-vol-lights F` | `lights`, `lightCandidates` | 1, 4 | 0 = sun only |
| `-ultrart-vol-nofog` | `gameFog` | true | the game's fog on RT pixels |
| | `alphaMin`, `maxHistory`, `clipSigma`, `depthTolerance`, `spatialDepthSigma` | 0.06, 32, 2, 0.1, 0.05 | temporal filter |
| | `debugView` | 0 | 1 = the exposed in-scattering alone |

GPU timer slot `volumetrics` (after `upsample`): depth, trace, temporal, the target copy and the composite.

## Results
