# Lit particles

In relight (and pt-dn and hybrid) the opaque world is ray traced and relit, but the game draws its effects afterwards
with its own lighting, or none: blood spray, dust, debris trails, splashes, smoke. Most of that art is unlit, so in a
dark relit room it stayed as bright as in daylight, and torch light, sunlight and shadow never touched it. Lit particles
light those effects from the ray-traced solution, the way Quake II RTX lights its sprites with its own lighting.

Code: `src/UltraRT/LitParticles.cs` (which materials, the shader swap), `src/UltraRT/ParticleLighting.cs` (the lighting
volume: settings, resources, dispatch), `src/UltraRT/Plugin.Particles.cs` (wiring, flags, globals),
`src/UltraRT/ParticleSurvey.cs` (the survey); shaders `ParticleLight.raytrace` (trace), `ParticleLight.compute`
(temporal filter), `LitParticle.shader` (the replacement material shader), and an `ULTRART_LIGHT_ISOTROPIC` switch in
`UltraRTLights.hlsl`. Bench suites `particles-survey` and `particles`; bench effect kinds `blood` and `dust`.

## Survey

`-ultrart-particlesurvey` logs every visible particle, sprite, line and trail renderer and every visible mesh with a
transparent or particle material (shader, keywords, blend, queue, layer), twice a second, with totals every 10 s
(`particle survey totals`). Run over `bench/suites/particles-survey.json`: the 0-2 combat recording with live enemies and
revolver fire, 1-1 start and 4-1 indoor A with explosions, 0-5. Shader code was read from the game's
`shaders.bundle` (UnityPy; the GLCore programs are plain text).

| Effect | Renderer / shader | Blend | How the game lights it | Here |
|---|---|---|---|---|
| Blood spray drops and their trails (`BloodDrop`, `BloodDrop_Trail`: every gore splatter, hit and kill) | particles, `psx/unlit/alphatest/nocull`, queue 2450 | alpha test, depth write | unlit: tex x vertex colour x `_Color`, fog | **lit** |
| Blood drops (`BloodDropMult`, gore zones, crushers) | particles, `Mobile/Particles/Alpha Blended` | alpha | unlit | **lit** |
| Environment gib dust (`Smoke4`, `EnviroGibDust`) | particles, `Particles/Standard Unlit` (overlay colour mode) | alpha | unlit | **lit** |
| Debris trails (`Alpha Blended` on `EnvironmentChunk`, grey, up to 30 at once) | trails, `Particles/Standard Unlit` (add-sub colour mode) | alpha | unlit | **lit** |
| Water splashes and drips (`WaterSplash 1`, `WaterSplashContinuous`, `WaterDrip` from wet enemies) | sprites / particles, `ULTRAKILL/SplashSprite-*`, `ULTRAKILL/Sprites/Default` | premultiplied alpha | unlit (`SplashSprite-VertexLit`: the game's 8 vertex lights) | **lit** |
| Glass shards (`Glass Shard 1`) | particles, `Sprites/Default` | premultiplied alpha | unlit | **lit** |
| Gas and dust clouds (`Smoke` on vents and pipes, `GasParticle`), explosion debris smoke trails (`Smoke` on `ExplosionChunk`, up to 51 at once), mist (`Smoke 2 FogOn`) | particles / trails, `ULTRAKILL/Master` TRANSPARENCY | **additive** | unlit | **lit** (additive smoke, see below) |
| Explosion and gore smoke (`Smoke2`) | particles, `ULTRAKILL/Legacy Shaders/Particles/Multiply (Double)` | `DstColor SrcColor` | multiplies the image behind it | unchanged: already follows the ray-traced image |
| Blood and oil stains on surfaces | `Blood and Oil` camera command buffer: `Bloodstain_ProceduralInstancing` into a screen buffer, then `PostProcess/CompositeBlood` | `DstColor Zero` | multiplies the image behind it | unchanged: already follows (they are drawn at `BeforeForwardAlpha`, after the RT composite) |
| Blood stains on meshes (`Bloodstain_Fallback`, e.g. 0-2's pre-placed `Bloodstain(Clone)` meshes) | mesh, queue 2999 | `DstColor Zero` | multiplies | unchanged; **not traced** (`SceneProxies.Traceable` skips `ULTRAKILL/Bloodstain*`): queue 2999 let them into the opaque scene, where the white (= no blood) part of the multiplicative texture became an opaque white quad over the wall |
| Fire, explosions (`Explosion`, `ExplosionSphere`, `ExplosionShockwave`), muzzle flashes, charge / spawn effects, beams, bite and wind trails, checkpoint, light pillars | various additive (`Legacy Particles/Additive`, Master additive, `Sprites/Default` glows) | additive | emissive | unchanged: light sources (EffectLights lights the scene from them) |
| Signs, cracks, blood river (`Mulch`) | meshes, `ULTRAKILL/Master` TRANSPARENCY + VERTEX_LIGHTING | alpha | the game's vertex lights | unchanged (not done, see Limits) |
| Water and glass | Master TRANSPARENCY | alpha | vertex lights | the reflection overlay (docs/reflections.md section 12) |

**Gibs** (body parts, `EnvironmentChunk` debris) are opaque `ULTRAKILL/Master` meshes on layers 9 / 10 / 27: they are
picked up by the scene proxies like any other prop and are already ray traced and relit. Only their trails are effects.

**Blood decals** need nothing: the game draws them as a multiplicative screen-space composite after our RT composite
(`PostProcessV2_Handler`'s `Blood and Oil` buffer at `BeforeForwardAlpha`), so a stain darkens and tints whatever the RT
image shows there: dark in a dark room, warm under a torch.

## Design

Two parts: a volume that holds the ray-traced lighting at points in the air, and a material swap that makes the
selected effects read it.

### The lighting volume
A camera-aligned froxel grid, 64 x 36 x 32 by default (about 40 px tiles at 1440p), with exponential depth slices from
0.5 m to 96 m. Every frame `ParticleLight.raytrace` takes one sample per froxel at a random point inside it:

- **Direct**: RIS over the scene's lights and emissive triangles (8 candidates; the same alias tables, relight light model,
  fixture lifts, soft shadows and effect lights as the surfaces), one shadow ray. The light code is compiled with
  `ULTRART_LIGHT_ISOTROPIC`: a point in the air has no normal, so there is no cosine (the particle faces every light, as a
  billboard mostly does), and a light counts if its culling mask has any of the layers effects live on (outdoor sun lights
  are masked to the outdoor layers in the game, but they light the air everywhere their shadow rays reach). Fill lights
  (the game's fake bounce) are left out, as relight leaves them out.
- **Indirect**: one ray in a uniform direction. A hit is shaded from the radiance cache (`albedo x cached lighting +
  emission`, what a bounce ray sees); a miss sees the sky. This is the average radiance around the point: bounce light,
  sky light, lava and screen glow. A cache cell that doesn't exist yet counts as unlit.
- In hybrid the light model is the game's (as hybrid's own trace), the indirect part is scaled by the GI strength, and the
  game's flat ambient (2 x ambient) is added. In relight the ambient is cut, as for the surfaces.

`ParticleLight.compute` filters it: a 3x3x3 Gaussian of this frame's samples, blended with the previous volume reprojected
through the previous frame's grid (alpha 0.12, 0.5 for 3 frames after a light-set change: muzzle flashes and explosions
show at once). The result is linear (in relight's linear transport) lighting in the surfaces' units.

### The material swap
The selected materials are the game's shared material assets, so swapping their shader changes every renderer that uses
them and every effect spawned later. While a lit mode is on, `LitParticles` sets each one's shader to
`UltraRT/Particles/Lit`, keeping all of its properties (texture, colours, cutoff) and its render queue, plus a few `_PL*`
properties saying which game shader to reproduce (the colour formula, blend, depth write, cull, alpha test, premultiply,
fog). When the mode goes off (or F7, Shift+F7, a scene change), the original shader, queue and keywords are restored.
Material instances made from a swapped material while it was swapped (`renderer.material`) are found by shader and
restored too.

`LitParticle.shader` reproduces the five game shaders (psx alpha test with its anti-aliased cutoff and vertex snapping,
Unity's Standard Unlit colour modes, sprites, Mobile alpha blended, Master additive), then multiplies the colour by the
volume's lighting at the vertex (one trilinear 3D read per vertex): back to the game's gamma units, times relight's auto
exposure, through relight's tone curve (`UltraRTToneCurve.hlsl`, as the composite), then the game's fog. Lit amount
`lerp(1, lighting, strength)`; strength 1 is fully ray-traced.

Selection: the shader must be one of those five, the material or texture name must say what it is (blood, smoke, dust,
dirt, debris, splash, drip, wet, mud, gib, gore, chunk, shard, ...; or the `Alpha Blended` debris trail material) and
must not name a glow (fire, flame, flash, muzzle, spark, glow, light, beam, laser, charge, explosion, ...).
Multiplicative materials, materials with emission, and `Sprites-Default` (shared by every plain sprite, muzzle flashes
included) are never swapped.

**Additive smoke.** The game draws its vent gas, explosion debris smoke trails and mist additively: in a dark relit room
they glowed. They are not light sources (EffectLights excludes them by name for the same reason), so they are lit too:
the additive contribution is multiplied by the lighting, as in-scattered light is. `-ultrart-particles-additive off`
keeps them as the game draws them. Fire, explosions, flashes and every other additive glow stay as they are.

**Additive smoke brightness** (2026-11-14 pm, user report: P-2's floor-vent gas "almost looks like it's on fire"). The
game's Master "Smoke" (TRANSPARENCY, additive, no VERTEX_LIGHTING, `_EmissiveFogStart/End` 99999, `_Opacity` 1) is lit
by the game's flat ambient only, so its art at lighting 1 is far brighter than anything the game shows: in P-2 (black
ambient) the gas is invisible in the game and with the game's own shader in relight
(`relight+plugin._particlesOff=1`), while our shader at strength 0 drew a bright white column and the RT lighting next to
the flickering red streetlight (intensity up to 13, range 35) multiplied it by up to 8 and the tone curve turned it
orange. For Master additive smoke (`_PLKind` 4) the lighting multiplier is now `s m / (1 + s m / max)` of the brightest
channel `m`, applied to all three channels (hue kept: red light keeps the gas red): scale `additiveScale` 0.5, soft
maximum `additiveMax` 0.6 (`-ultrart-particles-additive-scale`, `-ultrart-particles-additive-max`; 0 = the raw lighting).
At lighting 1 that is 0.27 of the art; next to the red light 0.52 of it, a dim red haze. Unlit additive materials (Unity
Standard Unlit additive dust) keep the raw multiplier: the game draws their art at full brightness.

Blood drops are drawn in the opaque pass (queue 2450), before the RT composite of that frame, so they read the previous
frame's volume (its own grid matrices travel with it as globals). The composite keeps them, since their depth is in front
of the traced surface.

**Soft particles** (2026-11-15, [vfx.md](vfx.md)). Every blended kind fades out where it meets the opaque scene
(`pl.soft`, `pl.softDistance` 0.6 m), and three more kinds are swapped unlit for that fade alone: fire sprites
(`ULTRAKILL/Additive`), explosion spheres (Master additive with SCROLLING) and the multiply smoke of explosions and
fires. The swap now re-applies its `_PL*` values after the shader change and can be redone at runtime
(`LitParticles.Reclassify`).

**Atmosphere** (2026-11-15, docs/volumetrics.md "Dark rooms"). Re-applying the `_PL*` values made every Master
additive `Smoke` really draw as relit smoke, including emitters that fill a whole room: 3-2's `HeatWaves (2)` (a 50 m
rectangle around the eye) turned the cave into an orange veil (screen luma 23 in the game, 97 relit). The game lights
that material with its flat ambient only (black there), and the volumetric pass already lights the air, so relighting
room-sized clouds of it counts the lit air twice. Particle systems whose emitter shape spans `atmosphereSize` (30 m)
or more and whose particles do too (renderer bounds; the bonus orbs' blood drops come from a 64 m sphere shell but
gather in 12 m) keep the game's shader: their renderer gets a private copy of each swapped material, restored to the
original shader, queue and keywords (`LitParticles.ScanAtmosphere`, every scan; the shared material stays lit for
vents and debris trails). The log names each one (`Lit particles: '<path>' keeps the game's look (atmosphere: ...)`).
Seen so far: 3-2 `HeatWaves (2)` and a 44 m cone of dust `Dot`s, 8-2 `Rain/Mist`, P-2 street / idol arena `Mist`s,
`1 - Opener/Mist`. `pl.atmosphereSize=0` (or `-ultrart-particles-atmosphere 0`) relights them as before.

## Settings

| Flag | Field | Default | |
|---|---|---|---|
| `-ultrart-particles on\|off`, config `[Particles] Lit`, **Shift+F7** | `enabled` | on | relight, pt-dn and hybrid |
| `-ultrart-particles-strength F` | `strength` | 1 | relight and pt-dn: 0 = the game's look |
| `-ultrart-particles-hybrid F` | `hybridStrength` | 1 | 0 turns hybrid off |
| `-ultrart-particles-additive on\|off` | `additiveSmoke` | on | additive smoke, dust and mist |
| `-ultrart-particles-additive-scale F` / `-ultrart-particles-additive-max F` | `additiveScale` / `additiveMax` | 0.5 / 0.6 | Master additive smoke: lighting multiplier `s m / (1 + s m / max)` (see "Additive smoke brightness") |
| `-ultrart-particles-atmosphere M` | `atmosphereSize` | 30 m | emitters (and their particles) this large keep the game's look (see "Atmosphere"); 0 = off |
| `-ultrart-particles-grid X,Y,Z` | `gridX/Y/Z` | 64,36,32 | |
| `-ultrart-particles-far M` | `far` | 96 | beyond: the last slice |
| `-ultrart-particles-alpha F` | `alpha` | 0.12 | temporal weight of a new frame |
| `-ultrart-particles-noindirect` | `indirect` | on | no bounce / sky ray |
| `-ultrart-particlesurvey` | | off | the survey (costs milliseconds; never measure with it) |
| bench `plugin._particlesOff=1` | | | A/B in one run |

Other fields: `candidates` 8, `near` 0.5 m, `fastAlpha` 0.5, `fastFrames` 3, `spatial` 0.5, `maxSample` 24. GPU timer slot
`particles` (trace + filter).

## Results
RTX 5070 Ti, 2560x1440, trace scale 0.5, bench suite `particles` (`bench/results/20261114-161615_particles`, build
ea3444e). Every scenario runs `off`, `relight` and `hybrid`, each with and without lit particles
(`+plugin._particlesOff=1`), with the bench's `blood` and `dust` effects (and an explosion in 0-5) spawned just before
each screenshot.

| Scenario | `particles` GPU slot (ms) relight / hybrid | GPU total relight without -> with (ms) | frame relight without -> with (ms) |
|---|---|---|---|
| 1-1 start (sunlit) | 0.114 / 0.115 | 3.028 -> 3.137 | 4.54 -> 4.47 |
| 4-1 indoor A (dark green corridor) | 0.094 / 0.093 | 3.513 -> 3.604 | 4.90 -> 4.97 |
| 0-1 dark | 0.096 / 0.096 | 5.316 -> 5.413 | 5.95 -> 6.20 |
| 1-4 lamps (night) | 0.125 / 0.125 | 2.734 -> 2.856 | 4.15 -> 4.37 |
| 0-5 lava + explosion | 0.105 / 0.106 | 4.778 -> 4.856 | 5.81 -> 5.92 |
| 0-2 combat recording | 0.092 / 0.091 | 4.695 -> 4.747 | 7.15 -> 7.40 |

The volume costs 0.09-0.13 ms GPU at 1440p in every scene (it does not depend on how many effects are on screen: one
sample per froxel). The swapped materials draw at about the game's own cost (one 3D read per vertex). Frame times move
within run-to-run noise (+-0.3 ms; 1-1 start is faster with it). CPU: a material scan every 5 s while a lit mode is on,
0.3-0.6 ms (2.4 ms the first time in a level); nothing per frame beyond a few globals. The `merge-regress` run on the
same build (`bench/results/20261114-161957_merge-regress`) completed every scenario with the slot at 0.10-0.13 ms.

Look (5-way strips, game / relight without / relight with / hybrid without / hybrid with, in the result folder's
screenshots):
- **4-1 indoor A**: the landing dust is a pale grey sheet in the game and in relight without lit particles; with them it
  is a dim green haze in the green-lit corridor, as dark as the walls around it.
- **0-1 dark**: the gore mist (additive `Smoke` tinted red) glowed pink in the dark room; lit, it is a dull dark red that
  sits in the room's orange light. Hybrid shows the same change at hybrid's darker level.
- **0-5 lava**: smoke over the lava takes the lava's yellow-orange instead of staying pale grey.
- **1-1 start** (sunlit): dust stays bright; in hybrid it is a little greyer than the game's flat white.
- No broken materials, wrong blending or popping between modes in any screenshot; the 22 swapped materials are restored on
  every mode switch (logged).

## Limits
- **Blood spray could not be checked in the bench**: the bench's `blood` effect (a pooled gore splatter restarted in front
  of the camera) didn't show up in the screenshots, so the blood materials (BloodDrop, BloodDrop_Trail, BloodDropMult,
  Bloodtrail) are validated only by the shared code path (dust and smoke use the same shader) and the 0-1 gore mist. Check
  them in play.
- **0-2 combat screenshots** show the replay's empty field at the capture times, so combat effects in relight weren't
  compared visually; its timings are valid.
- **Selection is by name.** An effect whose material or texture name says nothing (or names a glow) keeps the game's look;
  extend `Wanted` / `Unwanted` in `LitParticles.cs`.
- **Game vertex-lit transparents** (signs, cracks, the 0-2 blood river `Mulch`) still use the game's vertex lights.
  Reproducing those Master variants (scrolling, vertex blending) wasn't attempted.
- **Point-sampled lighting**: one value per vertex from a 40 px x ~19%-depth froxel grid. Particles get soft lighting,
  not shadow edges across a single puff. Froxel samples converge over about 8 frames (faster after a light change).
- **Blood drops lag one frame** (drawn before this frame's volume).
- **Isotropic lighting** (no cosine): a particle is as bright as a wall facing the light. Particles in front of a light
  aren't darker on their far side.
- Other cameras (portal views) sample the main camera's volume, clamped at its edges.
