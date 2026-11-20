# Water caustics and refraction (research and implementation, 2026-11-19; ripple caustics 2026-11-20)

**Since 2026-11-19 (branch `t3/caustics-volumes-water-check`) the game's caustics are traced**: option (a) below, with
its shadow ray, in relight and pathtrace; see section 5. Sections 1-4 are the research as it was written (branch
`t3/water-caustics-research`), so section 2's "lost" describes the build before that. The user sees water caustics
in 5-1, most of all in the tall shaft near the start with the blue grapple point, and asked how the game makes them, how
they look in our modes, how we could render them properly, and whether we "do diffraction" (they meant **refraction**).

Short answer: the game's caustics are **not lighting**. They are an unlit pattern that the `ULTRAKILL/Master` shader
adds on top of the lit, fogged colour of certain wall materials, inside designer-placed spheres (`CausticVolume`). Our
traced modes take those materials' albedo from `_MainTex` and never evaluate that term, so the caustics are **lost** in
relight and pathtrace (both denoisers). We refract nothing physically: water gets a screen-space ripple wobble, glass
nothing, and path-traced rays pass through both in a straight line.

## 1. How the game renders them

### The shader (`ULTRAKILL/Master`, keyword `CAUSTICS`)

Read from the GLCore variant of `ULTRAKILL/Master` in `shaders.bundle` (UnityPy; the GLCore programs are plain GLSL,
the vertex program holds both stages). Variant `CAUSTICS VERTEX_LIGHTING _FOG_ON`, pass `LIGHTMODE=Vertex`. The C# side
was decompiled from `Assembly-CSharp.dll` (`CausticVolume`, `CausticVolumeManager`, `ObjectBoundsToShader`).

Properties: `_Cells` (texture `Caustics_Cells`), `_Perlin` (`Caustics_Perlin`), `_CausticColor`, `_CausticMode` (0, 1,
2), `_CausticsDirection` (default up), `_Scale`, `_Speed`, `_Falloff`, per renderer `_BoundsCenter_VolumeCount` (set
by script), global buffer `_CausticVolumeData`. Both textures are 256x256 RGB, **point filtered**, repeat, no mips. The
cells texture is a near-white Voronoi pattern with darker cell borders, slightly offset per colour channel, so the
result has faint coloured fringes.

```hlsl
// P world position, N normal, t = _Time.y * _Speed
float3 Pattern(float2 uv /* = projected P * _Scale * 0.02 */) {
    float3 a = Cells(uv + t * float2( 0.1, 0.1))              - Perlin(uv + float2( 0.36, -0.19) + t * float2(-0.1, 0  )).r;
    float3 b = Cells(uv + float2(0.62, 0.62) + t * float2(-0.1, 0.1)) - Perlin(uv + float2(-0.61, 0.62) + t * float2( 0,   0.1)).r;
    return pow(1 - max(a, b), 10);            // sharp bright network where both layers are dark
}
// Mode 0/1: P projected on the plane normal to _CausticsDirection (world xz for "up").
//   weight = mode 1 ? 1 : saturate(dot(N, _CausticsDirection) * _Falloff); caustic = weight * Pattern
// Mode 2: for each CausticVolume v (position, nearRadius, colour * intensity, farRadius):
//   projection axis = normalize(rendererBoundsCentre - v.pos)
//   weight = saturate(dot(N, v.pos - P)) * saturate(1 - (|v.pos - P| - near) / (far - near))   // unnormalised dot: a facing test
//   caustic += weight * Pattern * v.colour
colour = Fog(albedo * vertexLighting) + caustic * _CausticColor;   // after lighting and fog
```

So the caustic is **added after lighting and fog, like emission**. It ignores every light, every shadow and the fog,
and it does not follow any water surface. **Animation** is two layers scrolling at `0.1 * _Speed` texture repeats per
second in different directions, not a flipbook. One texel is `0.2 m / _Scale` (0.2 m at scale 1, 0.6 m at 0.33, 2 m at
0.1: the "Big" rock materials have very coarse blocky cells). The pattern crosses a 256-texel repeat in `10 / _Speed` s
(20-40 s).

`ObjectBoundsToShader` (on a renderer) writes its bounds centre and the volume count into the renderer's property block.
`CausticVolumeManager` packs every enabled `CausticVolume` into the global `_CausticVolumeData` (32 bytes each:
position + nearRadius, colour x intensity + farRadius) when anything changes. A mode-2 material on a renderer without
`ObjectBoundsToShader` gets a volume count of 0 and shows **nothing**.

### Where the game uses it (offline survey of every level bundle, materials resolved through `materials.bundle`)

| Level | Renderers with CAUSTICS | Volumes | Notes |
|---|---|---|---|
| **5-1** | 91 material slots (all mode 2): `StoneRough3Moss` 20, `Standalone4 Caustics` 11, `CaveRock* MossDesaturated*` 49, `Dirt/Sand/Metal6/StoneMossy/StoneRough3 Caustics` | 15 | 66 renderers carry `ObjectBoundsToShader`. Volumes: Intro A (2), **Intro C** (1), Main Cave (6), arenas 2A/2B/2C (6; 2C's is orange) |
| 5-3 | 1 (mode 2) | 1 | |
| 8-1 | (materials outside `materials.bundle`) | 3 | 3 bounds objects |
| 7-S | `M_WaterUps_Landscape_Caustics` (mode 1) | 0 | |
| 0-E, 4-2, 4-4, 4-S | `Ice*`, `Sand*` (mode 0, colour 0.1 / 0.11) | 0 | a faint shimmer, not water |
| 8-3 | `TilesSquare 2` (mode 0) 5, `Sand Caustics` (mode 2, no volume: invisible) 1 | 0 | |
| 8-4 | `LavaMagenta` (mode 0) 3 | 0 | |
| 2-1, 8-2 | one mode-2 material each (`Standalone4 Caustics`, `Sand Caustics`) | 0 | **invisible** in the game: no volume |
| 1-3, 5-2, others | none | 0 | |

So in the game, caustics are a 5-1 feature (plus a few accents). 2-1, the 8-2 pool, 1-3 and the 5-2 sea have none.

### The 5-1 shaft (the user's spot)

`IntroParent/Intro/Intro C - Second Cave`: the blue `GrapplePoint Slingshot` at (-86.1, 38.5, 251.5), its
`CausticVolume` 36 m below it at (-86.25, 2.2, 251.5) (colour (0.33, 0.73, 1.0), intensity 0.35, near 1 m, far 30 m),
two blue point lights at y 6 and 26 (range 25 / 20), the water `Plane (2)` (`Water 3 Calm`, y 1.5) and cave walls in
`CaveRock1/3 MossDesaturated` (mode 2, scale 1 / 0.33, speed 0.5). Every wall that faces the volume within about 20 m
gets a blue-white cell network that drifts upwards and sideways. The room is switched off when the level loads.

## 2. How it looked in our modes before the implementation

Suite `bench/suites/caustics-5-1.json`: `LookAt` the room's `Hint` trigger (the shaft floor centre) with `LookActivate`
(that switches on `Intro C` and `IntroParent/Intro`, which are off at load), `LookDistance` 0.5, so the camera stands at
(-85.5, 4.8, 251.5). Views E, W, N, S (pitch -10), up at the grapple point (pitch 70), the water (yaw 218, pitch -25),
and two sequences (`off` 8 frames, `pathtrace` 6 frames). A plain teleport (`Position`, first attempt) lands in an
unloaded, black room.

Run `bench/results/20261119-190948_caustics-5-1` (main slot, visual, 2560x1440, current main build 3692116). Sheets
(gitignored) in `bench/results/review/caustics/`: `sheet_{E,W,N,S,up,water}.png` (off / relight / pathtrace /
pathtrace with UltraRT's denoiser), `crop_E_wall_modes.png` (right wall, 1:1), `seq_off_diff01_x8.png` and
`seq_off_ratio01.png` (what changes between two `off` frames), `seq_pt_diff01_x8.png` and `seq_pt_ratio01.png` (the
same for pathtrace).

| Mode | Caustics |
|---|---|
| `off` (the game) | Present: soft, blocky, lighter blue patches drifting over both walls (coarse point-sampled cells). Between two frames 0.25-0.3 s apart the walls change by 21-28 (p99, 0-255) and the exposure-normalised ratio by 8-11 % per pixel; the floor and the water don't change |
| `relight` | **Lost.** Walls show their albedo lit by the blue point lights (mean luma 107-115 against the game's 72), plus V1's shadow; no moving pattern |
| `pathtrace` (DLSS RR) | **Lost.** Normalised for the exposure step between frames, the walls change by 2-5 % per pixel and only at low frequency (auto exposure and lighting settling); what moves is V1's shadow and a drip particle |
| `pathtrace` (UltraRT denoiser) | **Lost**, as with RR |

Nothing is "kept but flattened": there is no trace of the pattern in any traced mode, because no traced shader
evaluates it. The sequence timing: `off` frames are 0.25-0.3 s of game time apart (8 frames, about 2 s);
`pathtrace` frames 1.3-1.7 s apart (each capture waits for RR and the exposure).

## 3. Options for rendering caustics

Costs at 2560x1440 on the RTX 5070 Ti, estimated from measured numbers: pathtrace traces at 1/3 scale (854x480,
0.41 Mpx) for RR and spends about 4.1 ms on ~5 rays per pixel in 0-1 (docs/path-tracing.md, "Cost"), so one more
incoherent ray per pixel with its shading is ~0.5-0.8 ms there and a bare shadow ray ~0.2-0.3 ms; relight and the
UltraRT denoiser trace at 0.5 (0.92 Mpx), about twice that. The water pass (a transparent hit plus one reflection ray)
measured +1.37 ms with 8-2's pool filling the screen at relight's 0.5 (docs/reflections.md). Each extra `DispatchRays`
also costs ~0.5 ms of render thread in 8-x (docs/performance.md).

What water we have to work with: transparent water (`Water*`, `Limbo Water*`, `Sea water`, `psx/water`) is traced by
the transparent pass with mask 0x10, a **procedural ripple normal** (`UltraRTWater.hlsl`: four directional sines,
wavelengths 0.5-2.3 m, slopes up to ~0.15, deep-water dispersion slowed by half, faded out by distance and by the
ray's footprint) and the game's vertex displacement (not on static-batched water). The game's water has no normal map.
8-2's pool has its ripples scaled to 0.25 by its light file (`waterWaves`). The 5-2 sea is opaque scrolling geometry
(`Corpses`), not water. Water surfaces are almost all horizontal planes or boxes.

Which surfaces would get caustics physically: floors and walls **under** shallow water (1-3's limbo water over floors
and grass, the 8-2 pool floor, 2-1 and 5-1 pools, 7-1's pool), and walls **above** water by reflection (the 5-1 shaft,
pool rims). Note that a physical reflection caustic is weak (water reflects ~2-12 % of the light), far weaker than the
game's 5-1 art.

### (a) The game's own pattern, evaluated in RT

Evaluate the exact formula above at traced hits on `CAUSTICS` materials: `_CausticMode`, `_Scale`, `_Speed`,
`_Falloff`, `_CausticColor`, `_CausticsDirection` into our material table, the renderer's
`_BoundsCenter_VolumeCount` from its property block (we already read MPBs for scrolling), the 15-or-fewer volumes from
`CausticVolumeManager.causticVolumes` into a small buffer each frame, `Caustics_Cells` / `Caustics_Perlin` bound.

- **Composite** like the game: emission-like radiance at the hit, scaled into our exposure (calibrated against `off`
  at this pose). In pathtrace it goes into the emitted term, so RR sees a clean, deterministic signal; reflections and
  bounce can see it too if it is added in the hit shader.
- **Light dependence (variant a2)**: multiply by one shadow ray from the hit to the volume centre (the volume acts as a
  patterned light): V1, enemies and rocks then shadow the caustics. +1 shadow ray only on caustic pixels.
- **Cost**: 4 point fetches and a loop over the volumes in range: <0.1 ms; a2 +0.1-0.3 ms when the walls fill the view.
- **Fit**: noise-free, stable in fast motion (world-space, like the game), RR and our denoiser keep it if it is added as
  emission after denoising (or as emission into RR). It is exactly the game's art direction and covers every place the
  game has caustics. It is not physical: the pattern ignores our ripples and real light levels.

### (b) Procedural caustics from our ripple field (a caustic factor on real light)

Our ripples are an analytic height field, so the focusing is analytic too. For light refracted through a gently
rippled surface and received at depth d, the intensity is `1 / |det(I - d (1 - 1/n) H)|` (H the height field's
Hessian, n 1.33), with a clamp at the folds; for reflection it is `1 / |det(I - 2 d H)|`. Two ways to use it:

- per pixel: for a receiver within some metres of a water plane (a small buffer of the active water planes, or one ray
  with mask 0x10 along the light direction), find the surface point along the light direction, evaluate the factor and
  multiply the **direct light** of lights on the other side of the water (the shadow ray is already traced);
- or per frame: a compute pass writes a tileable caustic texture per depth slice (the four waves are periodic) and the
  shading looks it up, so the folds can be resolved by splatting a grid instead of the 1/det singularity.

- **Cost**: the plane test + ~30 ALU per pixel near water; 0.05-0.1 ms for a tile pass; +0.2-0.5 ms if a mask-0x10 ray
  is used instead of plane buffers.
- **Fit**: depends on real lights and shadows, deterministic (no extra noise), animated with our water. RR takes it
  inside the lighting signal; UltraRT's denoiser would blur the lines (its spatial filter is several trace texels wide),
  so there the factor should multiply the denoised lighting at full resolution afterwards (approximate: the share of
  light that came through the water must be estimated). Caveat: with today's ripples (curvature ~0.2-0.5 /m) the
  refractive focal depth is several metres, so a 0.5-2 m deep pool shows only 10-30 % contrast; the caustic term would
  want its own, steeper wave set. Reflection caustics on walls 5-20 m away (the 5-1 shaft) do form with today's waves.

### (c) Light-space caustic maps

Per relevant light, trace a fixed grid of photons at the water surface (e.g. 256x256 over the water's rectangle), refract
or reflect at the rippled normal, trace to the receiver and splat into a light-space map with depth (like a shadow
map, a cube map for point lights); shading looks it up with a depth test.

- **Cost**: ~0.13 M rays per light (two per photon): 0.1-0.2 ms per light plus the splat, a new dispatch (+~0.5 ms
  render thread in heavy levels), and map memory per light. The 5-1 shaft needs 2-3 lights: ~0.5-1 ms.
- **Fit**: real geometry, waves, occlusion and light colour; a fixed grid is stable from frame to frame. But choosing
  lights, sizing maps and resolving thin features takes engineering, and the photon density limits sharpness.

### (d) Photon mapping / light tracing in the path tracer

Emit photons from lights through water (light-specular-diffuse paths) and deposit them in a fine world-space hash grid
(a "caustic cache" at 5-10 cm, separate from the 0.5 m radiance cache), gathered at primary hits. Today a camera path
can never find these paths: NEE shadow rays ignore water, and a delta-specular surface can't be hit by a light sample.

- **Cost**: 100-250 k photons a frame, 0.3-0.8 ms, plus a gather; a new dispatch.
- **Fit**: the most general (glass and metal caustics too), but the water moves every frame, so accumulation must be
  short and the result is blotchy low-frequency noise, the kind RR and our denoiser handle worst. Not worth it for
  this game's flat water.

### (e) Manifold next-event estimation (MNEE)

At each diffuse hit near water, solve for the point on the water surface that connects the hit to the light sample
through one refraction (or reflection) with a few Newton steps on the analytic ripple field, then trace the two
segments and weight by the manifold's generalised geometry term (Hanika et al. 2015; Zeltner et al. 2020, specular
manifold sampling). With a single deterministic seed (the straight path through the flat plane) it is noise-free and
finds the main root; it misses the extra roots of strongly focusing water, so it suits calm water.

- **Cost**: 3-6 Newton steps of ALU + one more shadow segment per pixel that has water between it and the light:
  ~0.2-0.3 ms at 1/3 scale with water filling the view, nothing elsewhere. Fits into the existing RIS light sample;
  ReSTIR-style reuse would not be needed.
- **Fit**: physically correct with real lights, shadows and our waves, in relight's NEE and in every path vertex. It
  needs shadow rays that see water (mask 0x10 on the light segment), the same machinery as traced refraction (below).

### Recommendation

1. **Now, for the look the user saw**: (a) with the a2 shadow ray, in every traced mode. It restores 5-1's caustics
   exactly where the designers put them, at <0.3 ms, with no noise and no denoiser trouble. Calibrate the strength
   against `off` with this suite (`off` vs `pathtrace`, mean luma and the frame-difference images).
2. **Later, for physical water caustics**: (e) with a single seed, or its cheaper cousin (b), as a factor on the light
   that reaches a surface through or off a water plane, starting with underwater floors (1-3, 8-2, 7-1, 2-1). Give the
   caustic term its own steeper wave set. (Done for (b) on 2026-11-20, as a factor on the albedo with a gain on the
   reflection's own waves: section 6.) Do (e) together with traced refraction, since both need light/shadow rays that
   refract at the water surface.
3. Skip (c) and (d): more dispatches, memory and noise than this game's flat, calm water needs.

## 4. Refraction ("diffraction") today

| Where | Water | Glass |
|---|---|---|
| Camera ray (relight, pathtrace) | Passes straight through: mask 0x10 is outside `ULTRART_MASK_SCENE`. The traced image under the water is what lies straight behind the surface, as if there were no water | same |
| What is drawn on top | The game's alpha-blended surface, then our overlay (`TransparentOverlay.shader`): traced Fresnel mirror reflection (f0 0.12) and **screen-space refraction** (`RefractAdd`/`RefractSub`, `-ultrart-water-refraction`, default 1): the background is shifted by the *difference* between refraction at the rippled and at the flat normal, times the depth below the surface (at most 4 m) | game surface + Fresnel reflection (f0 0.08); no refraction |
| Path rays in pathtrace (`ptx.transparents`) | Reflect like a mirror with the Fresnel probability, or pass **straight** through, tinted (0.6 x 0.8 of the surface colour) | same |
| Shadow / NEE rays | Ignore water: light passes unbent and unattenuated, so no caustics and no light loss | ignore clear glass; stained glass transmits a tint |
| Camera under water (`UnderwaterController.inWater`) | No transparent trace; the game's underwater tint plus our screen effects (docs/vfx.md "Underwater"): wobble, fog, and a **procedural caustic** (`Vfx.shader` `Caustic`: two drifting noise layers projected from the water plane along the sun direction) on surfaces below the surface and as shafts. It ignores light and shadows and shows only while the camera is in the water | |

So: the ripples wobble the background, but the bulk refraction is missing (no apparent-depth shift at a pool's edge:
floors look as deep as they are), glass is a reflection over a straight view, there is no Snell's window or total
internal reflection from below, and no light ray refracts anywhere. For thin window panes, straight-through is correct
(the offset of a parallel slab is millimetres), so glass gains almost nothing from refraction. Ice and thick blocks
would.

**Traced refraction for water** would replace the camera's view below a water pixel by a refracted ray from the
rippled surface (Snell, n 1.33, total internal reflection from below), shaded as a full path in pathtrace. Cost:
about the transparent pass again where water is on screen, +0.5-0.8 ms at 1/3 scale (pathtrace) and +1-1.5 ms at 0.5
(relight) with water filling the view, nothing elsewhere. The hard part is the composite, not the ray: the game draws
its water colour over our image, so we would draw the refracted image into the region below the surface (as the
screen-space refraction does now, but from a trace), and RR needs guide buffers for the refracted surface (the
"primary surface replacement" approach: motion vectors and depth of the virtual refracted hit), or it smears.
Gains: correct pool depth and edges, refracted views into pools from above, Snell's window and a real surface from below
(today the overlay leaves underwater views to the game), and the refracting light segments that physical caustics
(3e/3b) need.

## 5. Implementation: the game's pattern, traced and shadowed (2026-11-19)

Option (a) with the a2 shadow ray, in every traced mode. Files: `src/UltraRT/SceneProxies.Caustics.cs` (materials,
per-renderer variants, volumes, settings), `Proxy.shader` (`CausticTerm`: the pattern at every hit),
`PathTrace.raytrace` (`CausticShadowed`: the shadow ray at camera hits), `UltraRTCommon.hlsl` (`ULTRART_FLAG_CAUSTIC`).

### What is evaluated

- **Materials.** A `ULTRAKILL/Master` source with `CAUSTICS` (keyword list checked as well as `IsKeywordEnabled`, which
  misses some Master keywords) gives its proxy `_Caustics` = `_CausticMode` + 1, `_CausticColor`, `_CausticsDirection`,
  `_Scale` / `_Speed` / `_Falloff` and the material's own `_Cells` / `_Perlin` textures, sampled with the proxy's point
  repeat sampler (the game's point filtering and texel grid are kept). 5-1: 19 materials; 1-4 and 8-1: `Mercury` (mode
  0, the altars' mercury pools, scale 2.5).
- **Formula.** Exactly the decompiled GLCore program (section 1): the same projection (the rotation that takes "up" to
  the axis, `P.xz` when the axis is up), the same two scrolling layers, `(1 - max(a, b))^10`, the mode 0 facing fade, the
  mode 2 loop over the volumes with the unnormalised facing dot and the near/far ramp, times `_CausticColor`. The clock
  is `Time.timeSinceLevelLoad`, the game's `_Time.y`. The normal is the unflipped vertex normal (no detail map), as the
  game's. Measured over the game's own textures, the pattern averages 0.29 and peaks at about 4, so it is not clamped.
- **Mode 2's bounds centre.** The projection axis is (renderer bounds centre - volume), per renderer. Proxies share a
  material per source material, and static groups merge each material's slots into one submesh, so every renderer with
  `ObjectBoundsToShader` gets its own copy of the proxy material (`CausticVariant`; 65 in 5-1, so up to 65 more
  shader-table records, about 26 us at 0.4 us each). The copy reads the game's own `_BoundsCenter_VolumeCount` from the
  renderer's property block every 30 frames: the game writes it only while the renderer is active, and without it the
  game draws no caustic, so neither do we (`w` 0). Renderers without the script keep the shared proxy and show nothing
  in mode 2, as in the game (2-1, 8-2, 5-3).
- **Volumes.** `CausticVolumeManager.causticVolumes` (active ones), packed like the game's `_CausticVolumeData`
  ((position, near), (colour x intensity, far)) into the global `_UltraRTCausticVolumes` every frame, at most 64. The
  buffer is always bound (a dummy without volumes) and never released.

### Lit: the shadow ray

Ray tracing shaders here run with recursion depth 1, so the hit shader cannot trace. On camera hits
(`ULTRART_PAYLOAD_WANT_MOTION`) the hit returns the caustic apart from the emission: rgb as halves in `uvHalf` and the
high half of `emissionHalf.y`, the volume that contributes most (weight x colour luminance) in the top byte of
`primitive`, flag `ULTRART_FLAG_CAUSTIC` (bit 31). The ray generation shader traces one visibility ray from the hit
toward that volume's centre through the one shadow-ray site (`VisibleMasked`, mask geometry + fixtures + V1 where V1
casts shadows), stopping at the volume's near radius so a centre inside a rock or under the water's floor does not black
everything out, and adds the caustic only if it gets through. V1, enemies and rocks between a wall and the volume
shadow the pattern; water does not (mask 0x10). Modes 0 and 1 have no volume: unshadowed, as the game draws them.
Reflection, bounce and transparent-pass hits get the caustic unshadowed, in their emission.

### Radiance: fed as emission

The caustic is added to the primary hit's emission (`_Surface`), so every consumer treats it like the game treats it, an
emissive term after lighting: relight's composite (`albedo x lighting + emission`), UltraRT's denoiser (emission is not
filtered), and DLSS RR, whose colour input and diffuse-albedo guide both include emission (RayReconstruction.compute),
so remodulation leaves it alone instead of dividing it into the lighting. Reflections see it in full; bounce rays drop
it with the other emission (`_EmissiveBounceWeight` 0 while emitters are sampled directly), so caustic walls don't
light the room, as in the game. Side effect: a caustic pixel counts as an emitter for the cutout-ray mask
(`CutoutPrep` skips pixels with emission), irrelevant in 5-1's caves.

### Settings

| Setting | Default | Where |
|---|---|---|
| Strength (`[Effects] Caustics`, `-ultrart-caustics X`, bench `caustics.strength=X`) | 2 since 2026-11-19 (1 = the game's amount; it read weak against our brighter walls), 0 = off | Advanced page, Effects |
| Shadow ray (`[Effects] CausticShadows`, `-ultrart-caustics-noshadow`, bench `caustics.shadows=0`) | on | Advanced page, Effects |

The log: `Caustics: N traced CAUSTICS material(s), M mode-2 renderer variant(s): ...` at the scene build, and
`Caustics: V active volume(s), ... (K with the game's bounds centre)` when the volumes change.

### Validation (alt and main, 2560x1440, trace scale 0.5)

Suites `bench/suites/caustics-rt.json` (shaft views, A/B without caustics and without the shadow, sequences, the 1-4
bridge) and `caustics-iso.json` (auto exposure off, strength 4, game sequence). Runs `20261119-193810_caustics-rt_alt`,
`20261119-194530_caustics-iso`; sheets (gitignored) in `bench/results/review/caustics-rt/`: `sheet_E.png`,
`crop_E_wall.png`, `iso_wall.png`, `motion_game_vs_pt4.png` (what moves in 0.3 s: the game at x6, pathtrace at strength
4 x2).

- **Present and moving** in relight, pathtrace (RR) and pathtrace with UltraRT's denoiser. Of 65 mode-2 renderers, the
  6 in the active room (Intro C) had the game's bounds centre; the others are in rooms that are switched off. Caustics on against off at
  the same pose: the walls change by 6-8 % per pixel (median, exposure-normalised; p95 22-25 %). Between frames 0.3 s
  apart the traced walls change by 7.9 % (relight) / 6.1 % (pathtrace, strength 4), the game's by 6.5 % (8-11 % in the
  research run's 0.25 s frames).
- **Same shape**: the motion images show the same stretched, blocky cells along the same diagonals on both walls in the
  game and in pathtrace. RR softens the moving cells a little (it accumulates the emission like the rest of the colour).
- **Looks weaker than the game's** relative to the wall: the pattern is the game's absolute amount (about +0.1 on a
  0-1 scale at the shaft's volume, intensity 0.35), but our walls are lit brighter than the game's (mean luma 100-107
  against 72 at this pose), so the same addition is a smaller share. A strength above 1 would give the game's relative contrast (not tuned:
  the default stays at the game's absolute value; the menu slider goes to 3).
- **Shadow ray**: traced (its cost is measured below) and blocks where the volume is hidden; at the shaft pose the
  volume sits under the camera and V1's shadow falls upward, and the moving pattern makes still A/B shots noisy, so no
  capture shows a clean V1 shadow on the pattern yet. Worth a look in play: stand between a wall and the shaft's water.
- **Not checked on a second level with a volume**: 8-1's window view had no active volume (its three `CausticsVolume`
  objects under `Outside/Pipe` were off there), and 5-3's one volume lights a renderer without `ObjectBoundsToShader`
  (invisible in the game too). 1-4's and 8-1's `Mercury` (mode 0) is traced but was off screen.

### Cost (perf hold, `bench/suites/caustics-perf.json`, run `20261119-194454_caustics-perf`, the shaft E view: caustic walls fill the screen)

| Mode | GPU total ms (off -> on) | dispatch ms | render thread |
|---|---|---|---|
| relight (trace 0.5) | 4.05 -> 4.26 (+0.22) | 2.19 -> 2.38 (+0.19) | unchanged (0.33) |
| pathtrace, RR (trace 1/3) | 4.32 -> 4.48 (+0.16) | 1.54 -> 1.67 (+0.13): pattern +0.06, shadow ray +0.07 | unchanged |

Nothing is spent on pixels without a CAUSTICS material (a branch on the proxy's `_Caustics`), and no new dispatch or
binding (the payload is unchanged at 64 bytes).

## 6. Ripple caustics under water (2026-11-20, `t3/water-absorption-caustics`)

Option (b) of section 3, in its per-pixel form, next to the game's pattern of section 5 (both stay on; they meet only
where a CAUSTICS wall also lies under a water plane, the 5-1 shaft's pool floor). Files: `UltraRTWater.hlsl`
(`WaterRippleField`, `WaterRippleCaustic`, the wave table `kWaterWaves` now shared with the reflection normal),
`PathTrace.raytrace` (`RippleCausticAt`, applied in `RayGenBody`), `SceneProxies.Caustics.cs` (`UpdateRippleCaustics`,
the water planes), `SceneProxies.Transparent.cs` (registration).

### What is computed

- **Where**: at camera hits under an active water surface. Each frame the plugin packs the nearest 16 active water
  renderers (dynamic, static-batched and lifted static ones; within the ripples' fade distance + 5 m) into the global
  `_UltraRTWaterPlanes` as horizontal planes from their renderer bounds: the top is the surface; a thin plane (bounds
  under 1 m tall) lights receivers down to `rippleDepth` (6 m), a water box only inside itself (bottom - 0.25 m). Red
  "water" (colour r > 2 x max(g, b): 6-2's blood pools, `Water 5`) is left out; 1-3's pinkish `Water 2` (1, 0.73, 0.73)
  is kept. The planes are also bound with ripple caustics off while stained-glass windows are (their light under water,
  docs/materials.md section 14; `_UltraRTRipple.x` 0 then skips the caustics). Nothing while the camera is in water
  (`InsideWater`): the underwater screen effect draws its own caustics there (docs/vfx.md, "Underwater"). The loop reads one float4 per plane and the second only inside its rectangle.
- **The focusing**: light from straight above, refracted (n 1.33) at the ripple height field of the four waves the
  reflection uses (`kWaterWaves`, same dispersion and clock `_UltraRTWater.y`, scaled by the same ripple strength
  `_UltraRTWater.x`, so a level's `waterWaves` calms both). A surface point x lands at X = x + q grad h(x), q =
  depth x (1 - 1/1.33); the irradiance is 1 / |det(I + q H)|. The source point of a receiver is found by two fixed-point
  steps (single root), and the fold singularity is softened: `sqrt(1 + eps^2) / sqrt(det^2 + eps^2)`, eps = 0.22 +
  0.04 x depth (a light of finite size blurs the lines with depth) + 4 x the texel's footprint (m). The numerator makes
  flat water exactly 1 at any eps, so a wide footprint or a steep wall fades the pattern to 1, not darker. Waves shorter
  than 6 footprints drop out first, as in the reflection's band limit.
- **Gain**: our ripples are tuned for the reflection (slopes up to ~0.15) and alone would focus only 7 m down (section
  3). `rippleGain` 4 multiplies the focusing; depth x gain x ripple strength is soft-limited at 8
  (`8 tanh(z / 8)`), because beyond it the single root shows blobs instead of lines (offline prototype, scratch). The
  pattern then keeps its line network at any depth and only blurs.
- **Normalised**: caustics move light, they don't add it. Measured offline (300 m x 300 m of the field, gains 2-6) the
  factor averages 1.00 shallow and 0.93 at 6 m; it is divided by `1 - 0.0115 x depth`. In game the mean brightness of
  1-3's water-hall floor changed by 0.8 % with and without (pathtrace 120.5 / 119.6, 0-255).
- **Weight**: strength x fade in from the surface over 0.15 m x fade out over the last 40 % of `rippleDepth` x the
  ripples' view fade (60 m) x `saturate(0.35 + n.y)` (floors in full, walls at a third since the projection is vertical,
  nothing on faces looking down) x the hit's lit weight (fullbright surfaces get none).

### How it is applied: a factor on the albedo

`albedo *= lerp(1, factor, weight)` at the primary hit, before the portal-glass layers and the pack. Like a texture,
it multiplies whatever light reaches the hit (direct with its shadows, sky, bounce), so a shadowed floor shows the
pattern only as bright as its light. Both denoisers and DLSS RR demodulate by albedo, so the lines stay sharp and move
without lag; a factor on the lighting would have been blurred by the UltraRT denoiser's spatial filter and smeared by
RR's history as it moves (section 3's caveat). No ray, no noise. Approximations: the light is taken as coming straight
down for every light (oblique lamps would shift the pattern), and ambient and bounce light are focused as well (real
caustics from a diffuse sky are weaker). The albedo is 8-bit, so a line on an albedo above about 0.5 saturates at 1.
Only camera hits get it: reflections and bounce rays see the floor without it.

### Settings

| Setting | Default | Where |
|---|---|---|
| Strength (`[Effects] RippleCaustics`, `-ultrart-ripple-caustics X`, bench `caustics.ripple=X`) | 1, 0 = off | Advanced page, Effects |
| Gain, deepest receiver, planes per frame (bench `caustics.rippleGain`, `caustics.rippleDepth`, `caustics.ripplePlanes`) | 4, 6 m, 16 | bench only |

Log: `Ripple caustics: N water plane(s) of M, nearest surface y Y over x .. z .., receivers down to y ..` when the
count changes.

### Validation (alt, 2560x1440, 2026-11-20)

Suites `bench/suites/water-absorb-caustics.json` (8 scenarios: 1-3 start water, 2-1 channel, 7-1 pool, 8-2 pool, 1-4
bridge strips, 5-1 shaft; off / both features off / on, in relight and pathtrace, plus each feature alone off) and
`water-absorb-caustics2.json` (second pass after the fixes below; 7-1 from above; a 6-frame pathtrace sequence on the
5-1 pool). Runs `bench/results/20261120-151924_water-absorb-caustics_alt` and `20261120-152741_water-absorb-caustics2_alt`;
sheets (gitignored) in each run's `sheets/`: `<scenario>.jpg` (every mode), `crop_5-1_water.jpg`, `crop_2-1*.jpg`,
`crop_7-1.jpg`, `crop_1-3.jpg`, `seq_5-1_floor.jpg`, `diff_ripple_*.png` (pathtrace on minus ripple off, x8).

- **5-1 shaft pool** (the clearest case, floor ~1 m under `Water 3 Calm`): a soft network of light lines on the pool
  floor in relight and pathtrace, none before. Over six pathtrace frames the lines drift with the ripples and stay
  whole: no sparkle or speckle (`seq_5-1_floor.jpg`). Relight shows them a little stronger than pathtrace (RR softens
  the moving lines slightly). The game's own wall pattern (section 5) is unchanged (`5-1_shaft_E.jpg`).
- **2-1 channel**: soft streaks along the channel floor, stretched by the grazing view (`diff_ripple_2-1_water_2.5s.png`).
  The first build darkened a rim along the shore: steep faces had a wide footprint, so a wide eps, and the old
  `1 / sqrt(det^2 + eps^2)` fell below 1 even on flat water. Fixed by the `sqrt(1 + eps^2)` numerator (second run: no
  rim, `crop_2-1_shore.jpg`).
- **1-3 water hall**: the first build left 1-3 out (its `Water 2` is tinted (1, 0.73, 0.73) and the red filter was
  r > 1.2 x max(g, b)); with r > 2 x it has caustics, subtle at this pose (the floor is shallow and seen at a distance).
- **8-2 pool**: the pool's floor is the portal to the upside-down street copy, so there is nothing to receive caustics.
- **7-1 pool**: 11 m deep box; receivers below 6 m get none (fade).

### Cost (perf hold, main, `bench/suites/water-absorb-caustics-perf.json`, run `20261120-153247_water-absorb-caustics-perf`)

GPU total ms, both features off -> on (alternating 1.5 s segments; includes the water absorption pass of
docs/reflections.md "Water absorption"):

| Pose | pathtrace (RR) | relight |
|---|---|---|
| 5-1 shaft pool (caustic floor fills the lower half) | 4.684 -> 4.694 (+0.01) | 4.424 -> 4.461 (+0.04) |
| 2-1 channel | 5.857 -> 5.912 (+0.06) | 5.475 -> 5.530 (+0.06) |
| 7-1 pool from above | 5.121 -> 5.167 (+0.05) | 4.451 -> 4.481 (+0.03) |

So under 0.06 ms with water filling the view, within the 0.2 ms target. No new dispatch, binding beyond one global
buffer, or UAV.

### Interactive rings (2026-11-20, `t3/water-interactive-ripples`)

Splash, wake and explosion rings (docs/reflections.md, "Interactive ripples") focus light too: `WaterRippleCaustic`
adds every ring of the receiver's plane (its surface height is passed in; rings more than 2.5 m away in height are
another pool's) to the map. X = x + q grad h_waves(x) + sum_i q_i grad h_i(x), and the irradiance is 1 / |det(I + q H_waves +
sum_i q_i H_i)| with the same two fixed-point steps, eps and renormalisation. A ring's slopes are real slopes, so its q_i is
the physical depth x (1 - 1/1.33) x the same `rippleGain`, soft-limited per ring to 0.9 / (slope x k): even a fresh
ring at its steepest stays short of its first focus (no single-root blobs), and a deep floor only blurs it. Rings ignore
the reflection's ripple strength (`slope`): a still pool (8-2's `waterWaves` 0.25, or `trans.waves=0`) still throws ring
caustics. With no ring the old code runs unchanged (a branch on the count). Offline, the test ring (slope 0.25,
wavelength 0.7 m, age 0.65 s) 1 m above a floor at gain 4 gives, along a radius, 1.71 at the bright crest and 0.71 in the
trough, and exactly 1 outside the packet. In game it is a bright ring on the 5-1 pool floor, offset from the surface's
reflected ring by the floor's parallax (docs/reflections.md, "Interactive ripples", `crop_5-1_ring.jpg`). The ring
band-limits at 6 footprints, like the waves.

## Files and tools

- Suites: `bench/suites/caustics-5-1.json` (8 scenarios, about 1.5 min with one level load), `caustics-rt.json`,
  `caustics-iso.json`, `caustics-perf.json` (section 5); `water-absorb-caustics.json`, `water-absorb-caustics2.json`,
  `water-absorb-caustics-perf.json` (section 6); `water-rings.json`, `water-rings-perf.json` (interactive rings).
- Game data was read offline with UnityPy (a scratch venv): materials from `materials.bundle`, scene objects from each
  `campaign_scenes_level*.bundle` (MonoScript names from `monoscript_monoscripts.bundle`), the Master shader's GLCore
  program for keyword set `CAUSTICS VERTEX_LIGHTING _FOG_ON` (platform 15 in the shader's `platforms`; the vertex
  subprogram of type 6 holds both stages; blob table in segment 0, 12-byte entries offset/length/segment). C# with
  `ilspycmd -t <Class>` (`DOTNET_ROLL_FORWARD=Major`). No game files, textures or frames are committed.
