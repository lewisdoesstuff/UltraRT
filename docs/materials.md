# Per-texel materials (normal and roughness detail)

ULTRAKILL's surfaces are flat pixel art: one vertex normal per face and one roughness per material class
([reflections.md](reflections.md)). This adds a per-texel normal and roughness to every textured surface the hit
shader sees, so ray-traced light, shadows and reflections pick out bricks, bevels, louvres and grates, texel by texel,
without changing the art. The maps are generated offline from the game's own textures, at their native resolution,
and kept outside the repo.

Code: `tools/materials/` (offline pipeline, [README](../tools/materials/README.md)), `src/UltraRT/MaterialMaps.cs`
(cache, detail textures, atlas composition), `src/UltraRT/Plugin.Materials.cs` (settings, Ctrl+F1), the
`AddAtlasCells` part of `StaticGroups.cs`, `Proxy.shader` (`DetailSample`, `DetailNormal`), `PathTrace.raytrace` and
`UltraRTLights.hlsl` (vertex vs detail normal, hybrid ratio), `Composite.shader` (albedo-guided pick). Bench suites
`materials` (looks and GPU cost, detail on/off within one run), `materials-tune` (strengths, guided pick) and
`materials-cost` (what the cost is made of).

## 1. Offline pipeline

| Step | Tool | Output (outside the repo) | Time |
|---|---|---|---|
| Extract | `extract.py`: UnityPy over every bundle except sounds, music and shaders, plus `ULTRAKILL_Data/*.assets`. Materials' `_MainTex`/`_BlendTex` references are resolved across bundles (PPtr file id -> CAB name), and the StaticSceneOptimizer `StaticSceneData` atlases are found through their MonoBehaviour fields. | `src/<name>@<w>x<h>.png`, `textures.json` | 90 s |
| Generate | `generate.py`, per texture at native resolution, with wrap padding so tiling edges stay seamless | `ai/` and `proc/`: `<key>_d.png` (what the plugin loads), `<key>_n.png`, `<key>_r.png`, `manifest.json` | ai 150 s on an RTX 5070 Ti, proc 26 s |
| Compare | `compare.py`: contact sheet of albedo, normal, lit preview and roughness per method | PNG | |

2,036 textures referenced by materials (142 more share a name and size with another and are merged; 80 of the 2,036
are atlases, kept for reference). Sizes: 730 are 32 px, 373 64 px, 351 128 px, 205 256 px; only 65 are larger than
512. Maps exist for the 1,955 non-atlas textures.

**Install**: copy `manifest.json` and the `*_d.png` files of `<work>/ai` and `<work>/proc` into
`<game>/BepInEx/ultrart/materials/ai|proc/` (130 MB per method; the separate `_n`/`_r` PNGs are for looking at).
`_d.png` is RGBA: normal xy, 0.5 + (roughness - the texture's mean) / 2, alpha 1. Without the cache the feature is off
and logged as such.

### PBRify Remix through spandrel

`1x-PBRify_NormalV3` and `1x-PBRify_RoughnessV2` (Kim2091, CC0, trained on ambientCG/PolyHaven) are SPAN networks.
spandrel loads them directly (`ModelLoader().load_from_file`, `weights_only` pickle loading), with no ComfyUI or
chaiNNer. They are 1x (same size out as in), fully convolutional, and have no minimum size or multiple-of constraint
(`size_requirements` minimum 0, multiple of 1), so 16-256 px textures run as they are, in fp16 on CUDA. Output: an
RGB normal map in DirectX convention (green flipped to OpenGL here) and a grey roughness map.

**Native resolution beats upscaling.** The upscale-then-reduce route (nearest-neighbour by N so each texel is an N x N
block, run, box-filter back) was tried at 1x, 2x, 4x and 8x on nine stone, wood and metal textures:

| Pre-upscale | rms tilt of the normals (tile-light / metal-side / wood_boards) | correlation with the luminance gradient |
|---|---|---|
| 1x (native) | 0.23 / 0.38 / 0.12 | 0.8-0.95 |
| 2x | 0.10 / 0.16 / 0.05 | 0.6-0.9 |
| 4x | 0.05 / 0.13 / 0.04 | 0.3-0.9 |
| 8x | nearly flat | |

At 4x and 8x the network sees large flat blocks with hard steps, which look like nothing in its training data; it
returns almost flat normals with a thin ridge at each block edge. At 1x every texel is a feature, and the network
behaves like a "smart" height-from-albedo with shape priors: bevels, mortar lines, plank edges and relief come out
with their real orientation. So `--ai-scale` defaults to 1.

### Procedural generator

Height = sRGB luminance (dark = low), central differences with a Sobel kernel on the wrapped texture, slope scale 2;
roughness = 0.55 + 0.6 (mean luminance - luminance) + 1.5 x local 3x3 standard deviation. It needs nothing but numpy
and runs in 26 s for every texture.

### What PBRify gets right and wrong on pixel art

Sheets (local, not committed): `%LOCALAPPDATA%\UltraRT\crops\sheet-a.png`, `sheet-b.png`, `sheet-c.png`,
`s1-s2-proc.png` (scales), `scales-normal.png`.

- **Right**, and clearly better than the procedural maps: bricks (mortar recessed, brick faces flat with bevelled
  edges: `brick-grey`), wooden crates and planks (`crate1`, `wood_boards_big`: boards raised, diagonal brace on top),
  gothic grates and grilles (`gothicgrate`, `T_Cent_GrateFloor`: bars raised, holes deep), machined panels and bevels
  (`metal-c1`, `metal2`, `panel-dark-middle`, `Wall_Border_1_Grey`), carved relief (`T_SunStatue`). The procedural
  map follows every albedo speck instead, so its relief is noise plus "dark = low", which inverts painted highlights.
- **Wrong**: anything printed or painted on a flat surface. Signs, posters and warning labels (`SignWarning`,
  `CityoftheDeadSunPoster`) get their letters and pictures embossed; stained glass (`stainedglassblue`) gets noisy
  relief and noisy roughness along the lead lines; CRT screens (`screen3B`) get scanline ribs. Marble veins
  (`Marble_64_good`, `Excellent_Marble_1`) become engraved grooves, although polished marble is flat. Sand dunes and
  flesh are reasonable but faint. Roughness from PBRify is plausible on stone and metal (grime rougher, raised edges
  smoother) but on printed art it follows the picture.
- Mitigation: the class table (section 3) turns detail off for Glass, Hologram and mirrors and lowers it for Marble;
  unlit materials (most signs and screens are unlit Master materials) get none; and `ultrart-materials.json` rules can
  set `"detail": 0` for any material or texture glob.

## 2. Runtime

**Detail texture**: one RGBA8 texture per game texture, point sampled at the same uv as the albedo: rg = tangent-space
normal xy (OpenGL: x = +u, y = +v), b = 0.5 + (roughness - the texture's mean) / 2, a = 1 where there is detail.
Loaded per scene on first use from `<key>_d.png` with one `LoadImage`, looked up by the texture's Unity name and size
(the name alone when it is unique).

**Proxy materials**: `MaterialMaps.Bind` (from `SceneProxies.Proxy`) sets `_DetailTex` and `_DetailParams` = (normal
strength, roughness amount, 0, 1) from the material's class (section 3) or an override.

**Baked (StaticSceneOptimizer) geometry** samples a per-scene atlas with no texture-to-rect table anywhere
(`StaticSceneData` holds only the atlas textures, meshes and light indices; checked in `Assembly-CSharp`). The atlas
packs the source textures at their native sizes (1-1: 32-128 px cells in 512x512). So the detail atlas is composed at
load from the baked meshes themselves (`StaticGroups.AddAtlasCells`, reading the CPU copy the static groups already
keep):
1. Every triangle's TEXCOORD1 holds its atlas rect. Each baked slot knows its source renderer, and
   `OriginalMaterials` knows that renderer's original materials and per-submesh index counts.
2. SSO concatenates a renderer's submeshes into one slot. When the original index counts add up to the slot's, the
   slot is split into those ranges in order, and the split is accepted only if each range uses exactly one rect (one
   material = one texture = one rect). Otherwise a single-material source maps all its rects to its material, and a
   multi-material slot with several rects is skipped.
3. Each rect is filled from the original material's detail texture (nearest resampling if the cell size differs), with
   that material's class strengths baked into the texels: every atlas surface shares one proxy material (plus the
   reflection-class variants), so the strengths can't be material parameters. Rects nobody maps keep a = 0.
4. The atlas proxy and all its variants get the composed texture with `_DetailParams` = (1, 1, 0, 1).

1-1: 74 of 74 atlas cells mapped, no conflicts; 257 slots split per original submesh, 875 by their only material, 238
skipped (their cells were covered by other slots). 0-1: 76/76 cells; 4-1: 62/62. Composition takes 3-10 ms per atlas.

**Hit shader** (`Proxy.shader`): the ray generation shader requests detail with payload flag bit 3
(`ULTRART_FLAG_DETAIL`) on primary rays only. Reflection, bounce, radiance-cache and volumetric hits skip it: at
reflection hits it cost about 0.2 ms in metal rooms (section 5) and is invisible after the reflection filter. With
detail:
- **Normal**: the tangent frame comes from the triangle's positions and UV0 (`dP/du`, `dP/dv`, object to world,
  Gram-Schmidt against the shading normal; handedness from `dP/dv`), because many meshes have no tangents and the baked
  ones have none. Atlas UVs map into their rect with the same orientation, so UV0 works there too; `_MainTex_ST`'s
  sign flips the axes for mirrored tiling. The perturbed normal is `normalize(n + s (x T + y B))` (strength s scales
  the slope linearly), then pushed back towards the viewer if it would face away. Point sampled: one normal per
  texel, which keeps the pixel-art look (each texel is a small flat facet).
- **Roughness**: for `specular`-mode classes only, `roughness = saturate(class roughness + amount x (texel - texture
  mean))`, written into the payload's existing roughness byte. REFLECTION (blend/additive) materials keep the game's
  strength-derived roughness. The material word was already per hit, so the payload needs no new field.
- **Payload**: still 64 bytes. `p.normal` carries the detail normal and flag bit 3 says so. On primary rays the
  interpolated vertex normal is also returned, octahedral in 24 bits of `primitive` (debug-only before). That needed
  the game-cubemap reconstruction to move: C and k are now 4 x unorm8 in `uvHalf` (C was stored as unorm8 in
  `_HybridLight` anyway), the lighting contribution stays in `primitive` bits 24-31.

**Ray generation** (`PathTrace.raytrace`):
- Relight, pt-dn, pt: light is evaluated at the detail normal (direct, sky, bounce lobe), so the per-texel shading is
  physical. Ray origins are offset along the vertex normal, and a bounce or reflection direction that the detail
  normal tilted below the surface is mirrored back above it. The radiance-cache lookup uses the vertex normal.
- G-buffer: `_NormalDepth` gets the detail normal, so the denoiser's normal edge-stopping keeps texels apart instead of
  averaging their shading away.
- **Hybrid**: the game's colour is already lit with the vertex normal, and the composite scales it by RT shadowed /
  unshadowed light. So `DirectLighting` samples and stores reservoirs with the vertex normal, the unshadowed
  denominator stays at the vertex normal (what the game used), and only the shadowed numerator is re-shaded with the
  detail normal (`c x max(0, n_d.L) / n_v.L`). That ratio would also scale the game's ambient, which doesn't depend on
  the normal, and at grazing light it swings from 0 to 2: tilted texel rows went black on 0-5's lava-lit walls. So the
  detail is applied as `direct_vertex x clamp((lum(direct_detail) + ambient) / (lum(direct_vertex) + ambient), 0.3,
  1.5)`. The clamp is for night scenes: next to a lamp, a texel tilted towards it saw several times the vertex
  normal's light and glowed (1-4).
- Hybrid's composite picks one of the 2x2 nearest trace texels per screen pixel by depth. Per-texel shading made that
  visible as staircase edges along texel boundaries (the trace runs at half resolution). With detail on, the pick also
  compares albedo chromaticity with the game's pixel and prefers the trace texel of the same texture texel
  (`-ultrart-materials-guided`, Composite.shader). Relight goes through the temporal upsampler and doesn't need it.

## 3. Strengths

Per class (`MaterialMaps.ClassNormal` / `ClassRoughness`), multiplied by the global settings:

| Class | Normal | Roughness variation |
|---|---|---|
| Generic, Gravel, Rubble, Dirt, Crunchy, Other (stone, brick, concrete, baked level geometry) | 1.0 | 0.6 |
| Wood, WoodThin, MetalStumpy, MetalGrill, Sand, Grass | 0.8 | 0.6 (metals 0.8) |
| MetalThin, MetalLow | 0.7 | 0.8 |
| Marble (PBRify carves the veins into grooves) | 0.4 | 0.6 |
| Flesh, MetalHigh | 0.6 | 0.6 / 0.8 |
| Snow, Carpet | 0.5 | 0.6 |
| Wet | 0.3 | 0.3 |
| Glass, Hologram, Mirror; unlit Master materials; sky walls | 0 | 0 |

Overrides in `ultrart-materials.json` (plugin folder, then `BepInEx/config`): `"detail": F` (normal strength) and
`"detailRoughness": F`. A rule that sets only these leaves the reflection parameters and the VERTEX_BLENDING
secondary set alone.

## 4. Settings

| Flag / config / key | Default | |
|---|---|---|
| `-ultrart-materials on\|off`, `[Materials] Enabled`, **Ctrl+F1** | on | shader-side switch, instant |
| `-ultrart-materials-source ai\|proc`, `[Materials] Source` | ai | which cache folder; read at scene build |
| `-ultrart-materials-smooth F`, `[Materials] NormalSmoothing` | 1 | 0-1: each texel's normal pulled to its 3x3 neighbourhood average at load (section 9); read at scene build. 0 = the maps as generated. Default 1 since 2026-11-17 (the user on the sheet: "looks reasonably good too"); the config key was `NormalSmooth` for the few hours it defaulted to 0 and was renamed with the default, so a config that already held the old 0 does not keep it; screenshots and measurements dated before that were taken at 0 |
| `-ultrart-materials-normal F`, `[Materials] NormalStrength` | 1 | global multiplier on the class normal strength (relight, pt-dn, pt). Was 2 until 2026-11-17 (section 8: the bumpiness under RR is mostly the strength); measurements below dated before that were taken at 2. An existing config keeps the value written into it |
| `-ultrart-materials-hybrid-normal F`, `[Materials] HybridNormalStrength` | 1 | the same in hybrid |
| `-ultrart-materials-roughness F`, `[Materials] Roughness` | 1 | global multiplier on the roughness variation (menu: Enabled, both strengths and this one, [settings.md](settings.md); the LOD entries are config / flags only) |
| `-ultrart-materials-guided on\|off` | on | hybrid composite: albedo-guided trace texel pick |
| `-ultrart-materials-lod on\|off`, `[Materials] TextureLod` | on | ray-cone texture LOD for the detail maps (section 8), shader-side |
| `-ultrart-materials-lod-bias F`, `[Materials] LodBias` | 0 | mips added to the cone's LOD (0 = a traced pixel's footprint) |
| `-ultrart-materials-toksvig F` | 1 | share of the filtered-away slope variance that becomes roughness (0 = off) |
| `-ultrart-materials-albedo-lod on\|off` | off | albedo/emission at the cone's mip on secondary hits (the game's textures mostly have no mips) |
| bench variants `mat.enabled=0`, `mat.normalStrength=2`, `mat.hybridNormalStrength=1`, `mat.roughness=0`, `mat.guidedPick=0`, `mat.lod=0`, `mat.lodBias=-1.58`, `mat.lodAniso=1`, `mat.toksvig=0`, `mat.albedoLod=1`, `mat.lodDiffuse=0.3` | | e.g. `"relight+mat.enabled=0"` |

Log lines: `Material maps: N textures in ...`, per atlas `Material maps: atlas '' WxH: cells with detail ...`,
`Material maps: atlas slots mapped per original submesh ..., by dominant material ..., skipped ...`, and after the
build `Material maps (settings): N proxy materials bound, M without maps, K textures loaded in T ms; game main textures
with mips A, without B; decode / readback / mips / upload ms (cumulative)`. The atlas line also gives the game atlas's
mip count and how many cells are aligned for mip 1/2/3/5.

## 5. Results

Bench suites: `materials` (10 poses: 1-1 start, indoor a/b/c, terminal room, torch hall; 0-1 metal; 4-1 indoor A;
0-5 lava; 1-4 lamps; modes `off`, `hybrid+mat.enabled=0`, `hybrid`, `relight+mat.enabled=0`, `relight`, alternating
every 1.25 s so on/off are timed in the same pass), `materials-tune` (strength 1/2/3, guided pick on/off),
`materials-cost` (what the cost is made of). RTX 5070 Ti, 2560x1440, trace scale 0.5, GPU timer. Run with
`-ultrart-materials-source proc` for the procedural set. Crops are local (derived from the game's art, not committed):
`%LOCALAPPDATA%\UltraRT\crops\` (`tune\`, `aiproc\`, `final\`, `final2\`), made from the bench screenshots.

### Looks

- **Relight is where it pays off.** At the old strength 1 the detail was there but subtle; `materials-tune` settled the
  default at 2: torch-lit brick walls in 1-1 (indoor a, torch hall) get per-brick relief and light falloff across each
  brick; 0-1's louvres and window frames get lit slats and shadowed gaps, and its metal highlights break up along the
  panel seams; 0-5's carved walls read as carved; the 1-4 column shows its stone blocks under the lamp; 4-1's green
  panels show their bevels. Strength 3 turns noisy and blotchy (4-1, 1-1 indoor a), so 2 is the default.
- **Pixel art survives**: point-sampled per-texel normals give each texel one flat facet, so the relief is blocky in
  the same grid as the albedo; nothing is smoothed or upscaled.
- **Reflections**: per-texel normals and roughness break up the glossy floors and metal walls (1-1 terminal room: the
  ceiling-light streaks become broken, rougher smears instead of clean stripes).
- **Hybrid** is subtler and harder: its shadow ratio is at half resolution and multiplies the game's whole lit colour.
  With the vertex-normal denominator, the ambient term, the clamp and the guided pick it looks right on 1-1's sunlit
  walls and 1-4 at hybrid strength 1; at 2 it gets dirty (dark texel rows, stair-steps at texel edges). Single-hue
  textures still show some stair-stepping where the guided pick can't tell texels apart.
- **AI vs procedural in game** (`aiproc\`): with the same strength, PBRify's maps give bricks, panels and bevels a
  consistent shape; the procedural maps are fainter and follow albedo specks, so walls look grainy rather than built.
  The terminal room's reflections break up along real panel lines with PBRify, randomly with the procedural maps.

### GPU cost

Final build at the defaults (detail on primary rays only; relight normal strength 2, hybrid 1), GPU total ms, detail
off -> on in the same pass (`bench/results/20261114-134928_materials`):

| Pose | hybrid | relight |
|---|---|---|
| 1-1 start | 2.332 -> 2.371 (+0.04) | 2.859 -> 2.983 (+0.12) |
| 1-1 indoor a | 3.385 -> 3.637 (+0.25; this pose swings by about 0.2 between runs) | 4.026 -> 4.173 (+0.15) |
| 1-1 indoor b | 3.589 -> 3.645 (+0.06) | 4.118 -> 4.313 (+0.19) |
| 1-1 indoor c | 3.197 -> 3.242 (+0.04) | 3.714 -> 3.862 (+0.15) |
| 1-1 terminal | 3.974 -> 3.968 (-0.01) | 4.441 -> 4.618 (+0.18) |
| 1-1 torch hall | 3.241 -> 3.298 (+0.06) | 3.838 -> 4.053 (+0.21) |
| 0-1 metal | 4.322 -> 4.488 (+0.17) | 4.453 -> 4.752 (+0.30) |
| 4-1 indoor A | 3.243 -> 3.298 (+0.06) | 3.301 -> 3.439 (+0.14) |
| 0-5 lava | 4.247 -> 4.365 (+0.12) | 4.311 -> 4.688 (+0.38) |
| 1-4 lamps | 2.240 -> 2.282 (+0.04) | 2.600 -> 2.684 (+0.08) |

So hybrid (strength 1) costs about +0.05 ms, up to +0.17 ms in metal rooms, and relight at strength 2 about
+0.1-0.2 ms, up to +0.3-0.4 ms in 0-1's metal room and 0-5. The same build at relight strength 1 measured +0.04-0.10
ms (0-5 +0.19): the texture work is small, and most of the cost grows with the tilt, because shadow, bounce and
reflection rays from bumpy surfaces go in more divergent directions. Average frame times (4-6 ms here) swing by up
to 0.6 ms either way between 1.25 s segments, so the GPU timer is the number to trust: relight frames were mostly
slower with detail (up to +0.9 ms in the terminal room), hybrid ones not measurably.

What it is made of (`materials-cost`, relight dispatch ms over detail off, all at strength 1):

| Pose | detail on, at reflection hits too (first build) | ... per-texel roughness off | ... normal strength 0 | reflections off | detail on, primary rays only |
|---|---|---|---|---|---|
| 1-1 terminal | +0.38 | +0.36 | +0.19 | +0.06 | +0.10 |
| 0-1 metal | +0.40 | +0.46 | +0.24 | +0.11 | +0.16 |
| 0-5 lava | +0.49 | +0.48 | +0.17 | +0.12 | +0.17 |

Per-texel roughness costs nothing. Most of the first build's cost was in the reflections: the detail work at every
reflection hit (about 0.2 ms even with flat normals), plus reflection rays that bumpy surfaces scatter into more
divergent directions (the look itself). Dropping detail at reflection hits removed the first part; the reflected
surfaces are filtered and blurred anyway, so nothing visible was lost. The remaining +0.1-0.2 ms in metal rooms is
mostly the divergence.

Load, per level build: 65-140 ms for the 100-160 detail textures a level uses (one `LoadImage` of `<key>_d.png`
each; the first version decoded two PNGs and made three textures per entry, 1.1 s for 1-1) plus 3-10 ms per atlas
composition. 1-1, 0-1 and 4-1 atlases: 74/74, 76/76 and 62/62 cells mapped (2 conflicting votes in 4-1, first one
kept).

### Recommended defaults

- **On, PBRify (`ai`) maps**, strength 1 in relight / pt-dn / pt (2 until 2026-11-17: too bumpy under DLSS RR,
  section 8), 1 in hybrid, per-class table as in section 3, guided pick on. Detail matters most in relight, which shades with the normals directly. If the +0.3-0.4 ms in
  metal-heavy rooms matters more than the relief, 1.5 is a reasonable middle.
- Procedural maps only as a fallback where no AI maps exist: they are cheap to generate but mostly add albedo noise.
- Keep `"detail": 0` overrides in mind for printed art the class table misses (signs and posters on lit materials).

## 6. Limits and next steps

- **Printed art gets embossed** (signs, posters, stained glass, screens), see section 1; covered by the class table
  and unlit rule where the game's tags allow, otherwise per-material `"detail": 0` rules.
- **Atlas cells of multi-material renderers** whose index ranges don't line up stay without detail if no other
  renderer shows the same texture.
- **Blend atlas** (VERTEX_BLENDING's secondary texture on baked geometry, TEXCOORD2) has no detail: the main texture's
  detail applies everywhere on the face.
- **Hybrid** detail is limited by the half-resolution ratio; the guided pick helps where neighbouring texels differ in
  colour, not on single-hue textures.
- **No parallax or height**: PBRify's height model was not used (its author disables it by default because baked
  lighting in the albedo misleads it). Per-texel normals on flat quads also can't self-shadow.
- **Reflection, bounce and cache hits ignore detail** by design (cost); reflections are filtered and the indirect light
  is smooth anyway.
- **Secondary-hit albedo LOD** needs mipped copies of the game's textures (section 8). The cone path exists but is off by default (`-ultrart-materials-albedo-lod`): the game's textures have no mips.

## 7. Rain on surfaces (2026-11-14 pm, branch `lights-pm`)
(Since 2026-11-15 rained-on surfaces are also wet: darker, glossier, with puddles and ripple normals from the same
raindrop rings. See [vfx.md](vfx.md), "Rain".)

User report: in P-2 the raindrops on the floor vanish with RT on. They are not particles: the game turns on the `RAIN`
keyword on outdoor materials at runtime (`StaticSceneOptimizer.UpdateRain` on the outdoor batch material, shader
`ULTRAKILL-Stationary`; `StencilValuesByLayer` on reusable `ULTRAKILL/Master` materials), and both shaders then change the
albedo before lighting. Relight replaces opaque pixels with albedo x ray-traced lighting from our proxy materials, so
the rain went with the game's albedo.

The term, decoded from the two shaders' DXBC (identical in both; they differ only in where the base albedo comes from):
- Dominant-axis planar projection of the world position (xz on floors and ceilings, xy / zy on walls), tile
  16 / `_RainScale` m (`_LocalOffsetRain` subtracts the object's position).
- Floors (|N.y| largest): `_RainDrops` (R phase, G amplitude), `y = R * 31.45 + t * _RainSpeed * 1.5`, an expanding ring
  `G (1 - frac y) sin(1.5 clamp(9 (G + frac y - 1), 0, 3))` on every other cycle, x 0.5 x `_RaindropsIntensity`;
  `_RainCeilingToggle` 1 removes it where N.y <= 0.001.
- Walls: running streaks from `_RainTrails` (b warps the uv in steps, r + t scrolls a sawtooth, g gates it, a scales it).
- `albedo = albedo * _RainDarkening + amount * _RainColor`; t is the shaders' `_Time.y` (time since level load).

`SceneProxies.Rain.cs` mirrors each source's rain state and properties (`_Rain`, the textures, colours and floats) onto
its proxy material and atlas variants, re-checked at 4 Hz (rooms with a roof switch it off). `Proxy.shader` `RainAlbedo`
applies the term at every hit with the game's unflipped vertex normal and `_UltraRTRainTime` (Time.timeSinceLevelLoad).
Primary, bounce and reflection rays all see it; hybrid keeps the game's own colour, so only its bounce albedo changes.
P-2 at the vent pose: 1,245 of 1,427 rain-capable proxy sources raining. `-ultrart-norain` (bench `plugin._rainOn=0`)
turns it off. Screenshots `bench/results/20261114-185737_lights-pm-check2_alt/P-2_rain_floor_*` (relight with and
without: the darkened floor and the animated drop texels, as in hybrid's game colour). Cost: two point texture reads per
rainy hit; nothing on the CPU beyond the 4 Hz keyword check.

## 8. Texture LOD (2026-11-15, branch `t3/normals-lod`)

User report: "with RR the textures seem a bit too noisy", most in 8-2's starting area, "the brick textures seem far too
bumpy, having bump details smaller than the pixels". The user set `NormalStrength` 0.4 to hide it.

**Cause (confirmed, but the smaller part).** Every texture in the hit shader was read with `SampleLevel(point, uv, 0)`
and the detail textures had no mips: there was no texture LOD at all. Beyond about 10 m at RR's 1/3 scale (a trace
pixel's cone is about 3 mm per metre) a 3 cm brick texel is smaller than a traced pixel, so each trace sample picks one
texel's normal and the jitter turns that into flicker. That is fixed below. The measurements say it was not most of
what the user sees, though: the "too bumpy" look is the strength itself, at texel scale, where texels are resolved.

**What it does.**
- *Mips with variance.* `MaterialMaps.MakeDetailTexture` gives every detail texture (and the composed atlas) a full mip
  chain at load: rg = mean slope, b = mean roughness offset, a = 255 - 85 sd, where sd is the standard deviation of
  the level-0 slopes under the mip texel (a valid texel is >= 127; 0 still means "no detail"). Each level is built
  from the one above with the second moment carried along. One buffer, one `LoadRawTextureData`; atlas levels of 64k
  texels or more in parallel rows.
- *Ray cones* (Akenine-Moller et al. 2021). `PathTrace.raytrace` computes one trace pixel's angle from the
  neighbouring pixel's ray (it already did, for the reflection cache's footprint) and sends it with the primary ray:
  with `ULTRART_PAYLOAD_CONE` the payload's `albedo.xy` carries (width at the ray origin, spread) into the hit, so the
  payload stays 64 bytes; `TraceScene` adds the path length before each portal hop. The hit shader (`Proxy.shader`,
  `ConeLodUv`) takes `0.5 log2(uv area / world area)` of the hit triangle, `log2(cone width)`, `-0.5 log2|cos|` (the
  footprint's area; `mat.lodAniso=1` = its long axis, which blurs floors at grazing angles) and the bias; `TexMip`
  adds `0.5 log2(W H)` and clamps to the chain. Detail is point sampled within the two nearest mips and blended
  between them: close up it is the same per-texel facet as before, only sub-pixel detail is averaged.
- *Atlas cells.* The atlas mips are box filters of the whole atlas, so a cell's mip k is its own only when its rect is
  aligned to 2^k texels: `AtlasMaxLod` caps the LOD per cell from the rect (`firstbitlow` of offset and size). 1-1:
  73/74 cells aligned for mip 5; 8-2 (2048^2): 132/143 for mip 3, 86 for mip 5.
- *Toksvig / LEAN.* For glossy (`specular`) classes, `alpha^2 += toksvig x strength^2 x sd^2` (alpha = roughness^2):
  bumps averaged away become a slightly wider lobe instead of sparkle. Diffuse shading uses the mean slope.
- *RR guides.* RR's normal guide is `_NormalDepth` (the shading normal) and its roughness guide the material word, so
  both now carry the filtered normal and the Toksvig roughness, consistent with the shading ([dlss.md](dlss.md)).
- *Secondary hits.* Reflection rays (`TraceReflection`: the pixel's cone widened by the lobe, `2 r^2`) and bounce rays
  (`mat.lodDiffuse`, 0.3 rad per diffuse vertex; path tracing's `PtPath` grows the cone per vertex) carry a cone too,
  for the game's albedo and emission. It is off (`mat.albedoLod=0`): almost none of the game's textures have mips
  (8-2: 12 of 370 bound proxy materials, 1-1: 1 of 121; the StaticSceneOptimizer atlases have one level), so it
  changed nothing and would only cost the cone math at every secondary hit. Mipped copies of the game's textures would
  be the next step if bounce/reflection texture noise ever matters.

**Results** (`bench/suites/normals-final.json`, `normals-motion.json`, `normals-bias.json`, `normals-lod.json`;
strengths set per mode since the user's config has 0.4; 2560x1440, pathtrace = RR at 1/3; alt slot). The 8-2 pose is
`8-2 a`, the brick street right after the start corridor (`spawn` poses land in the generic start corridor):

| 8-2 a | mip 0 (before) | LOD | strength 0.4, mip 0 |
|---|---|---|---|
| Motion noise, slow pan, relight (sigma, two runs) | 1.61 / 1.57 | 1.47 / 1.45 (-8 %) | |
| Motion noise, slow pan, pathtrace (two runs) | 1.20 / 1.20 | 1.17 / 1.18 (-2 %) | 1.03 / 1.04 |
| Held-pose burst, pathtrace (masked sigma, 3 runs) | 0.86, 0.92, 0.80 / 0.85 | 0.77, 0.80, 0.93 / 0.82 | 0.86, 0.80 |
| Far facades region, pathtrace burst sigma | 1.26 / 1.33 | 1.42 / 1.20 | 1.17 |

The held-pose burst varies by about +-0.07 between runs, so at a held pose LOD and strength are both within noise in
pathtrace (RR's history averages the jittered texel picks). The motion metric (cross-replay with the same jitter: it
sees sampling noise, not deterministic aliasing) improves a little. Detail survives close up (the 8-2 near wall and the
mid-distance facades look the same with and without LOD); `mat.lodBias=3` visibly flattens the far facades, and the
difference from mip 0 grows with the bias (2.6 / 3.2 / 4.7 luma for bias -1.58 / 0 / 3 in the far region), so the LOD
is doing what it should. 0-1 metal with Toksvig on/off: burst sigma 1.06 / 1.07 pathtrace, 0.96 / 0.93 relight, within
noise.

The bumpiness at the 8-2 street is texel-scale relief (mortar lines and brick bevels 3-9 screen pixels wide), which the
LOD rightly keeps: at strength 2 it reads as dark, dirty mortar; at 1 the courses still read with half the depth; 0.4
is nearly flat (`normals-final` crops `8-2_a_2.0s_*`, `1-1_start_2.0s_*`). That is a taste call; **1** looks like the
best balance under RR. The default stays 2 (and the user's config at 0.4) until the user decides.
**Decided 2026-11-17: the default is 1.** The trailer's first render (alt install, strength 2 written in its config)
still showed noisy 8-2 bricks with the LOD on. Configs that already hold a value keep it (BepInEx writes the default
on first run), so an install still at 2 needs its entry changed by hand.

**Cost.** GPU (`normals-perf`, `-Perf`, exclusive hold, LOD off/on alternating in one pass): total +0.00 / +0.01 ms in
8-2 a and 1-1 start (relight and pathtrace), +0.03 / +0.07 ms in 0-1 metal. Load: the mip build adds about 0.7 ms per
texture plus the atlas (8-2: 323 textures and a 2048^2 atlas in 470 ms, of which 280 ms is the PNG decode that was
already there; 1-1: 136 textures in 140 ms). The first version (float sums per level, a second texture per entry) took
2.4 s in 8-2.

**Bench note.** The alt install had no material-maps cache (`BepInEx\ultrart\materials` was left out of its copy), so
per-texel detail was silently off in every alt-slot run before 2026-11-15 14:15. The `ai` cache was copied there then.

## 9. Matrix: what makes the relief look wrong (2026-11-17, `t3/materials-v2`)

The complaint: generated relief looks out of place, as irregular light and dark fragments inside bricks. The matrix
separates the maps from the denoiser (plan B1). Poses: 1-1 indoor a (torch-lit brick at a grazing angle), 0-1 metal;
native-size screenshots, `bench/suites/mat-matrix.json` for the shipped set and `mat-matrix-src.json` with
`-Extra "-ultrart-materials-source <name>"` for each other set. Sets, made from the PBRify maps by
`tools/materials/variants.py` (offline; installed next to `ai` in the alt install):

| Set | What it is |
|---|---|
| `ai` | PBRify as shipped |
| `ai-lp` | each texel's slope averaged with its 3x3 neighbourhood (1-2-1, wrapped) |
| `ai-hgt` | height first: the slopes projected onto the nearest field that is the gradient of one height (Frankot-Chellappa, exact on a tiling texture), normals from that height's central differences |
| `ai-blk` | the same height quantised to four levels first ("every texel a block") |

Each under DLSS RR at 1/3 scale, RR at 0.67 (`ptx.scale=0.67`) and UltraRT's denoiser, plus the shipped set with the
maps off and with roughness only (`mat.normalStrength=0`). Runs `bench/results/20261117-184557_mat-matrix_alt`,
`-184738` (lp), `-184857` (hgt), `-185016` (blk); sheets (local) `bench/results/materials-v2/m-11wall-all.png`,
`m-01metal.png`, made by `tools/materials/matrix.py`.

What the 1-1 wall shows:

- **It is the maps, not RR's resolution.** The fragments are as strong at 0.67 scale as at 1/3 (a little crisper),
  and UltraRT's denoiser shows them too. So fading the normals by footprint under RR (plan B5) would not fix it.
- **It is the normals.** With roughness only the wall is as clean as with the maps off.
- **The cause is texel-to-texel disagreement.** `ai-lp` keeps the relief (mortar recessed, brick faces catching the
  torch) and loses the fragments, under all three denoisers. `ai-hgt` is between the two: making the field
  integrable removes part of the noise, not the single odd texels. `ai-blk` is no better than `ai-hgt`.
- On the metal pose the sets differ little: slightly less grain on flat panels with `ai-lp`.

So the low-pass is in the plugin, applied when a map is loaded, and needs no regenerated maps:
`-ultrart-materials-smooth F` / `[Materials] NormalSmoothing` (0-1, `MaterialMaps.SmoothNormals`; the baked atlases are
composed from the smoothed texels). At 1 the frame matches the offline `ai-lp` set (mean difference 1.5 of 255 on
the wall crop, against 4.9 for the shipped maps; `20261117-185318_mat-matrix-src_alt`, sheet `m-11wall-smooth.png`).
**Default 1 since 2026-11-17** (the user's pick from the sheet); 0 turns it off.

Not answered by this matrix: the start room (the tour stop of 1-4 looks out of the room), printed and painted
surfaces (section 1: per-texture rules, plan B4), and whether another generator does better than PBRify + low-pass
(plan B2: DeepBump is a small ONNX model with a command line and can be run offline; PBRFusion's model page needs a
sign-in, so its licence and files are unconfirmed).
