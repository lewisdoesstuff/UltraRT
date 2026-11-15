# Screen effects and wet surfaces

Effects that sit on top of the ray-traced image: bloom, heat haze, shockwave distortion, soft particles, glowing
explosion debris, the underwater look, and two kinds of wetness (rain, fresh blood) that change a surface's material
before it is traced. They add punch and readability; none of them blurs or recolours the pixel art. Everything works in
every lit mode (`pathtrace` with DLSS RR, `relight`, `pt-dn`, `hybrid`), because it reads only what all of them share:
the composited image, the game's depth, the G-buffer and (bloom) the image before the tone curve.

| File | Role |
|---|---|
| `src/UltraRT/Vfx.cs`, `Plugin.Vfx.cs`, `unity/.../Vfx.shader` | The late pass: bloom, heat haze, shockwaves, underwater; glowing debris wiring |
| `Denoise.compute` (Composite), `RayReconstruction.compute` (Finish) | Write the HDR bloom source |
| `LitParticle.shader`, `LitParticles.cs` | Soft particles: the depth fade, and the fire / explosion / smoke materials swapped for it |
| `Proxy.shader` (`RainWetness`), `Plugin.Wet.cs` | Rain wetness, puddles, ripples |
| `BloodWet.cs`, `unity/.../BloodWet.shader`, `PathTrace.raytrace` | Wet blood |
| `bench/suites/vfx-*.json` | `vfx-report` (the strips below), `vfx-perf`, `vfx-perf2`, `vfx-soft`, `vfx-wet`, `vfx-blood`, `vfx-c`, `vfx-a`, `vfx-softdiag`, `vfx-survey` |

## Where each effect sits in the frame

1. **Before the trace** (main camera, at the RT injection point): the wet-blood mask is drawn from the game's depth and
   stain buffers (only while a stain is fresh). Rain wetness needs nothing here: it is in the hit shader.
2. **The trace**: the hit shader returns wet materials (rain); the ray generation makes the surface under fresh blood
   glossy. Reflection rays, the radiance cache and the denoisers then treat them like any other glossy surface.
3. **Denoise and tone map**: UltraRT's denoiser tone-maps in `Denoise.compute`'s Composite at trace resolution; RR's
   `Finish` does it at screen resolution, after the remodulation (RR denoises lighting; texels are multiplied back in
   `Finish`). Both write the **HDR bloom source** next to their tone-mapped output: the energy the tone curve compressed
   or clipped plus bright emission, in linear light. So bloom sees the same pre-tone-curve image in both paths, and
   under RR it sees it after remodulation, at screen resolution.
4. **RT composite**, the game's fog pass, then a copy of the camera target (`_VfxPre`, bloom only).
5. **The game's transparents**: particles, fire, explosions, water, blood stains. The swapped effect materials fade out
   against the game's opaque depth here (soft particles). That depth is the game's own screen-resolution buffer, copied
   at the injection point, in every mode: nothing reads trace-resolution depth or motion vectors.
6. **After the transparents** (`CameraEvent.AfterForwardAlpha`, after the volumetric scatter): the screen-effects pass.
   One copy of the target, the bloom chain, then one fullscreen pass that samples the scene through the distortion
   field, applies the underwater look and adds the bloom. The viewmodel and the HUD are drawn later by the game's HUD
   camera and are never distorted or bloomed.

The game strips every `AfterForwardAlpha` command buffer when it recreates its render targets (resolution or
pixelization change); `VfxFrameReset` notices the new target and adds the volumetric and the screen-effects buffers
again, in order.

## A. Screen-space effects

### Soft particles
Hard lines where a sprite or an explosion sphere cuts into the floor are an old-engine tell. The lit-particle shader
(`UltraRT/Particles/Lit`, docs/particles.md) now fades every blended kind over `softDistance` (0.6 m) in front of the
opaque scene: `saturate((sceneZ - particleZ) / distance)`, smoothstepped, from the game's depth at the pixel.
Depth-writing kinds (blood drops) are left alone, and the fade is only on while the main camera draws
(`_UltraRTSoft`, set in the RT command buffer and cleared at every camera's pre-render: portal and HUD cameras have
other depth).

Dust, smoke, splashes and debris trails were already swapped to that shader for lighting. Fire, explosions and their
smoke were not (they are light sources or multiply the image), so three kinds are now swapped **unlit**, only for the
fade (`_PLLit` 0: no lighting volume, no tone curve). Each reproduces the game's shader from its compiled GLSL
(extracted from `shaders.bundle`):

| Kind | Game shader | Materials | Formula |
|---|---|---|---|
| 5 | `ULTRAKILL/Additive`, legacy `Particles/Additive` | `FireMaterial`, `FlameStreetcleaner` (fire sprites) | `2 x vertex colour x _TintColor x tex`, `SrcAlpha One` |
| 4 + `_PLScroll` | `ULTRAKILL/Master` TRANSPARENCY additive | `Explosion`, `Explosion 1/2`, `ExplosionSphere` | `tex x _Color x vertex colour`, alpha `x _Opacity`, uv `+ _ScrollOffset + _ScrollSpeed x time`, fog to black |
| 6 | legacy `Particles/Multiply (Double)` | `Smoke2` (explosion and fire smoke) | `lerp(0.5, 2 x tex x vertex colour, alpha)`, `DstColor SrcColor` |

Selection is by name (`fire`, `flame`, `explosion`; the multiply smoke by `smoke`) and only for these shaders: an
additive quad that lies flat on a wall (checkpoints, screens, light pillars) must not fade.

Two traps found on the way:
- **Draw order.** An explosion's multiply smoke sits at the centre of its additive spheres, at the same sort
  distance. With the game's shader it happens to be drawn after them (the dark core every explosion has); with ours
  it was drawn first and vanished under the spheres. The swapped smoke is one render-queue step later.
- **Effect lights and effect geometry read these materials** (blend state, tint). `LitParticles.EffectTint` /
  `EffectSrcOne` answer for swapped and unswapped materials alike; the swapped shader declares `_TintColor`.

`pl.soft=0` restores the game's shaders for the three kinds (the materials are reclassified); `pl.softKinds` (1
explosion spheres, 2 fire, 4 smoke) is the diagnostic switch.

### Heat haze
Over lava and fire. Sources: the active lava surfaces (`SceneProxies.ActiveLava`, nearest 8 within 80 m: a box from the
surface up `heatHeight` 8 m) and the flames the effect lights already follow (`EffectLights.CollectFlames`, up to 16
within 30 m: a ball of hot air above each). Per pixel the pass intersects the view ray, cut at the scene depth, with
every source, and displaces the sample by a rising noise:

- **World-anchored.** The noise is evaluated at the world point where the ray crosses the hot air, so the pattern
  stays put when the camera turns; it rises at `heatRise` 1.2 m/s.
- **Angular amplitude.** Hot air bends light by an angle, so the displacement is a fraction of the screen
  (`heatAmplitude` 0.004 of the height, at most `heatMaxOffset` 0.006) and falls off slowly with distance; a
  world-space amplitude was invisible from 15 m above 0-5's lava.
- **No moire.** The noise texture has mips and the level follows the pixel's footprint: where the pattern would be
  finer than a pixel it averages out to no displacement.
- **No foreground smear.** A displaced sample that lands on something closer than the hot air is rejected.
- **Point sampled.** Displaced pixels stay whole pixels.

The game has heat shimmer of its own in a few places (`ULTRAKILL/HeatWave` particles, 0-5); it is untouched.

### Shockwaves
Every spawned hierarchy with an `Explosion` component (rockets, Maurice's beam, mines, the bench's explosions; through
the effect lights' spawn queue) starts a shock front: a sphere that grows to `shockRadius` 1.7 x the explosion's
`maxSize` in `shockLife` 0.6 s, easing out. Where the view ray (cut at the scene depth) passes the front, the sample is
pushed outwards ahead of it and pulled in behind it (`x exp(-x^2)` over a thickness of 0.25 m + 3% of the radius),
radially on screen, by `shockAmplitude` 0.3 m x sqrt(size / 5) projected to the screen and fading with age. Surfaces
the front crosses ripple; a front behind a wall does nothing. Up to 8 at once.

### Underwater
While `UnderwaterController.inWater` (the camera is in a `Water` volume; the surface is the top of its collider):
- **Wobble**: a slow screen-space wave (`waterWobble` 0.0012 of the screen), point sampled, less on near surfaces.
- **Fog that darkens with depth**: distance fades into the water's own colour (`Water.clr`), dark, and darker the
  deeper the camera is (`waterFog` 0.025 /m, `waterDarken` 0.12 /m).
- **Light shafts from the surface**: a sparse caustic pattern on the surface plane, extruded along the direction to
  the level's sun (straight up without one), marched through the water in front of the surface (6 dithered steps up to
  24 m).
- **Caustics** on what is under water, fading with depth; less on surfaces that are already bright.

The game's own underwater tint (`_UnderwaterOverlay`) still applies.

## C. Bloom (and glowing debris)

### Bloom
From linear light, before the tone curve, output SDR. Three sources, added in the prefilter (half resolution):
1. **Compressed energy**: `hdr^2.2 - shown^2.2`, what the tone curve's shoulder and the clip at white took away
   (`bloomExcess` 0.35): lit hotspots, explosion light on walls.
2. **Bright emission**: the G-buffer's emission where its brightest channel is over `bloomEmissionLo` 0.55
   (`bloomEmission` 0.35): lava, neon, screens, glowing enemies and projectiles. From the G-buffer, so a wall that is
   merely white never glows.
3. **What the game's transparents added**: the luminance difference between the target after the transparents and
   the copy taken before them, with the pixel's own colour, where the result is bright (`bloomEffects` 0.6 between
   `bloomEffectsLo` 0.6 and `Hi` 0.95): fire, muzzle flashes, explosions, beams, the lava overlay. The colour is the
   pixel's, not the difference's: a channel that was already saturated has no difference left, which turned the glow
   of lava green.

Without an HDR source (hybrid) the brightest part of the displayed RT image stands in for 1 and 2.

Six mips, 4-tap downsample, tent upsample added back with each wider level weighted `bloomSpread` 0.55 of the one
before; composited as `(scene^2.2 + strength x bloom)^(1/2.2)` with `bloomStrength` 0.5 and a soft shoulder that
never adds more than `bloomMaxAdd` 0.25. Pixels that are already bright get up to `bloomProtect` 80% less of it: the
glow goes around a light, and the art that draws the light keeps its texels (lava keeps its crust).

With every level weighted the same (the first version) large emitters veiled the room: 6-2's start, lit by its red
windows, came out 8% brighter with 15-18% of its pixels changed by more than 16 levels. With the spread at 0.55 the
glow stays next to its source: +1% and 2-3% of the pixels (numbers below).

### Glowing debris
The chunks an explosion throws out (`ExplosionChunk`, 24-30 meshes, alive 1-3 s) emit a glow that cools: 3x display
white at the blast, a third of that every 0.7 s, from yellow-orange to dark red (`debrisIntensity`,
`debrisCoolTime`). The chunks use ordinary level materials (`Wall`), so each chunk's proxy gets its own material copy
flagged `_HotDebris` (`SceneProxies.MarkHotDebris`; freed when the chunk is gone) and the hit shader adds the global
`_UltraRTDebrisGlow`. Being emission, it shows in reflections, lights the bounce, and blooms. All live chunks share one
age (the latest explosion's). The chunks are not registered as sampled light sources: the explosion's own light
dwarfs them.

## B. Wet surfaces

### Rain
The game rains by switching the `RAIN` keyword on outdoor materials (docs/materials.md, "Rain on surfaces"), and
`SceneProxies.Rain.cs` mirrors that per proxy material, so the hit shader already knows exactly which surfaces the
game rains on, and under roofs it stops by itself. `RainWetness` adds, at every hit of such a surface:
- **Wet**: albedo x (1 - 0.18 wet), roughness down to 0.3, a dielectric film (f0 0.04, specular weight 0.5 wet).
  Walls 60% as wet as floors; ceilings dry when the game keeps its drops off them.
- **Puddles** on up-facing surfaces: a two-octave world-space noise (`puddleSize` 5 m) thresholded so that `puddles`
  0.45 of the range holds water: roughness 0.03, full weight, darker, and the shading normal flattened (the surface
  detail drowns).
- **Ripples**: the game's own raindrop rings (`_RainDrops`, the same clock) tilt the normal: the ring function is
  evaluated at three taps and its slope added, primary rays only.

Metals keep their own specular weight and most of their roughness: raising it turned P-2's metal floor black under
its dark sky (a metal's reflection replaces its diffuse colour). REFLECTION materials keep the game's model. Rain is
wherever the game sets it: P-2 (1,245 of 1,427 rain-capable materials at the vent pose), 8-2's streets, 7-3's
outdoor parts (the recorded 7-3 poses are indoors: 2 of 366 materials raining, so there is no 7-3 screenshot).

### Wet blood
The game's blood stains are not traced (docs/particles.md): it stamps them from a GPU ring buffer of up to 100,000
stains (`BloodsplatterManager.instanceBuffer`) into a screen buffer and multiplies that into the frame after our
composite. It stores no age. So:
- A Harmony postfix on `BloodsplatterManager.CreateBloodstain` records the birth time of each slot (`BloodWet.cs`);
  `StainsCleared` and a checkpoint reload forget them.
- While any stain is younger than `bloodDryTime` (40 s), the newest stains (they are the last slots written; at most
  `bloodMaxStains` 4,096) are drawn into a screen-sized R8 mask of wetness by `Hidden/UltraRT/BloodWet`: a port of the
  game's stamp shader (same mesh, per-slot rotation and atlas cell, box and normal tests), so the wet area is exactly
  the stain the game draws. Wetness is 1 for `bloodWetTime` 6 s, then falls to 0 at the drying time; overlapping
  stains keep the wettest (`BlendOp Max`).
- `PathTrace`'s ray generation reads the mask at the primary hit: albedo x (1 - 0.3 wet), roughness down to 0.12,
  f0 0.12, full specular weight (metals keep theirs). The game then multiplies its red over the result, so the
  reflections in fresh blood come out dark red.

The mask draw changes the render target inside the RT command buffer, so it puts the camera's three colour targets
and depth back afterwards. Pixels seen through a portal are skipped (the mask is the main view's). No cost while no
blood is fresh.

Bench: the effect kind `bloodstain` writes stains onto the floor and walls ahead (as a splatter's drops do when they
land), since the bench's `blood` splatter rarely lands in view.

## Settings

Settings menu: section "Screen effects" (and the wet entries after it). Config sections `[ScreenEffects]`, `[Wet]`.
Bench variants: `vfx.<field>`, `wet.<field>`, `pl.soft`, `pl.softDistance`.

| Setting | Flag | Default | Hotkey |
|---|---|---|---|
| Screen effects (the whole late pass) | `-ultrart-vfx on\|off` | on | **Ctrl+F2** |
| Bloom / strength | `-ultrart-bloom on\|off`, `-ultrart-bloom-strength F` | on / 0.5 | **Shift+F2** |
| Soft particles | `-ultrart-particles-soft on\|off`, `-ultrart-particles-soft-distance M` | on / 0.6 m | |
| Heat haze / strength | `-ultrart-heathaze on\|off`, `-ultrart-heathaze-strength F` | on / 1 | |
| Explosion shockwaves / strength | `-ultrart-shockwaves on\|off`, `-ultrart-shockwave-strength F` | on / 1 | |
| Glowing explosion debris | `-ultrart-debris-glow on\|off` | on | |
| Underwater | `-ultrart-underwater on\|off` | on | |
| Rain wetness / wetness / puddles | `-ultrart-wet-rain on\|off`, `-ultrart-wet-rain-strength F`, `-ultrart-wet-puddles F` | on / 0.8 / 0.45 | |
| Wet blood / drying time | `-ultrart-wet-blood on\|off`, `-ultrart-wet-blood-dry S` | on / 40 s | |

`vfx.debugView=1` shows the bloom alone, `2` the distortion field (offset x100 around grey).

## Results (RTX 5070 Ti, 2560x1440)

Strips (game | relight with the new effects off, i.e. master's look | relight | pathtrace + RR), in
`bench/results/20261115-010940_vfx-report_alt/strips/`: `0-5_lava_3.0.jpg` (bloom, heat haze),
`0-1_metal_blast_3.0.jpg` (soft explosion, shock front, bloom), `0-1_metal_blood_3.0.jpg` (wet blood),
`P-2_vent_smoke_3.0.jpg` (rain), `7-1_underwater_3.0.jpg`, `6-2_start_3.0.jpg` and `1-1_start_3.0.jpg` (unchanged
looks). Detail crops of each effect came from `vfx-soft`, `vfx-wet`, `vfx-blood` and `vfx-c`.

GPU cost, exclusive perf runs (`bench/results/20261115-010346_vfx-perf`, `vfx-perf2` right after it; native timer
slots `vfx` = the pre-transparents copy, `vfxlate` = the late pass):

| | `vfx` + `vfxlate` (ms) |
|---|---|
| Bloom only (1-1 start, 6-2 start, 0-1 metal; pathtrace and relight alike) | 0.011-0.014 + 0.096-0.109 |
| + heat haze over lava (0-5) | 0.013 + 0.145 (heat haze alone, bloom off: 0.069) |
| + shock fronts and soft explosions (0-1, an explosion every 0.7 s) | 0.014 + 0.129 |
| + underwater (7-1) | 0.010 + 0.178 |

The whole pass is 0.11-0.19 ms at 1440p. The rest:
- **Soft particles**: inside the game's own particle draws (one depth read per fragment); frame times with explosions
  every 0.7 s are equal within noise (8.22 -> 8.40 ms with everything on).
- **Wet blood**: pathtrace GPU total 6.298 -> 6.338 ms (+0.04) with fresh stains in view, relight within noise
  (5.991 -> 5.965); nothing while no stain is fresh.
- **Rain wetness** (P-2 vent pose): relight dispatch 5.067 -> 5.244 ms (+0.18: reflection rays on a street that had
  none), pathtrace 3.887 -> 3.870 (no measurable change at its 1/3 trace scale).
- **Glowing debris**: one emission add in the hit shader; 24-30 small materials per explosion, freed with the chunks.

Regression looks, the pass on against off in the same run (`bench/results/20261115-011453_vfx-regress`; mean
screen colour without the HUD and viewmodel areas, and the share of pixels that differ by more than 16 of 255):

| Pose | pathtrace off -> on | changed | relight off -> on | changed |
|---|---|---|---|---|
| 1-1 start | (150.8, 53.0, 29.3) -> (151.5, 53.3, 29.5) | 0.8% | (159.2, 54.8, 30.1) -> (160.2, 55.2, 30.3) | 0.2% |
| 6-2 start | (95.1, 4.0, 2.8) -> (96.2, 4.0, 2.9) | 2.9% | (96.4, 4.0, 2.8) -> (96.1, 4.1, 3.0) | 2.3% |
| 0-5 lava | (119.9, 62.1, 2.7) -> (120.4, 62.7, 2.7) | 11.9% | (133.3, 68.5, 2.9) -> (134.1, 69.1, 2.9) | 11.6% |
| 0-1 metal | not comparable (enemies spawning in the first capture) | | (140.2, 52.6, 21.9) -> (143.4, 53.9, 22.1) | 22.6% |

1-1 and 6-2 keep their look (under 1.2% in the mean). 0-5's changed pixels are the lava itself (its texture scrolls
between the two captures, and the heat haze moves it); its mean moves by 0.5%. 0-1's lights flicker and its enemies
move between captures, which is most of its difference; the mean is 2% up.

## Decided against, and not done

- **Parallax / height (part E).** The material maps hold a per-texel normal and a roughness offset and no height
  (docs/materials.md: PBRify's height model was left out; the procedural method uses luminance as height only to get
  slopes). A height could be integrated from the normals offline, but parallax is the wrong tool here: (1) the textures
  are 32-64 px, point sampled, 3-6 cm per texel, and a parallax shift moves texel edges by fractions of a texel
  against the geometry's own edges, which reads as swimming, not depth; (2) `pathtrace` re-applies texels after RR from
  exact texel hits at screen resolution (remodulation) and every temporal consumer reprojects with geometric motion: a
  view-dependent texture lookup breaks both; (3) the game draws decals, outlines and blood on the flat surface.
  Not implemented.
- **The three brightness leftovers** (7-3's tree room at ~1.7x the game with whitish walls, 6-1 at ~1.45x, 3-2's eye
  clipping to pink) are not addressed. They are light-model questions (docs/relight.md "Area colour": white lights
  under the physical falloff), and a regional colour match touches the exposure path both denoisers share; it deserves
  its own measured pass against 1-1, 0-1, 6-2 and 0-5.
- **Depth of field, motion blur, film grain, chromatic aberration, lens flares**: out of scope by request.

## Limits

- Heat boxes are the lava renderers' bounds: an L-shaped or tilted lava mesh shimmers over its whole box. Opaque lava
  (`Lava Opaque`) is not a source.
- One shared debris age; enviro gibs (broken props) do not glow.
- Underwater was tuned on 7-1's pool from a teleport pose (unloaded surroundings, white water colour); 5-1 needs a
  recorded approach to check. The surface height is the collider's top: sloped or moving water is approximated.
- Wet blood: primary hits only (a mirror does not show stains glossy); stains loaded from a checkpoint are dry; the
  mesh-based stain fallback (compute shaders off) is not handled. Pre-placed `Bloodstain_Fallback` meshes stay matte.
- Rain puddles follow a noise, not the geometry: no height data exists to find real low spots.
- The swapped fire and explosion materials do not write the game's outline and view-normal targets (the game's
  shaders mark them there); no visible difference was found.
- Bloom's third source reads the frame before and after the transparents: a bright alpha-blended sprite over a dark
  background counts as glow.
