# Per-texel materials (normal and roughness detail)

ULTRAKILL's surfaces are flat pixel art: one vertex normal per face and one roughness per material class
([reflections.md](reflections.md)). This adds a per-texel normal and roughness to every textured surface the hit
shader sees, so ray-traced light, shadows and reflections pick out bricks, bevels, louvres and grates, texel by texel,
without changing the art. The maps are generated offline from the game's own textures, at their native resolution,
and kept outside the repo.

Code: `tools/materials/` (offline pipeline, [README](../tools/materials/README.md)), `src/UltraRT/MaterialMaps.cs`
(cache, detail textures, atlas composition), `src/UltraRT/Plugin.Materials.cs` (settings, Ctrl+F1), the
`AddAtlasCells` part of `StaticGroups.cs`, `Proxy.shader` (`DetailSample`, `DetailNormal`), `PathTrace.raytrace` and
`UltraRTLights.hlsl` (detail normal). Until 2026-11-19 also the hybrid ratio and `Composite.shader`'s albedo-guided
pick, which went with the hybrid mode. Bench suites
`materials` (looks and GPU cost, detail on/off within one run), `materials-tune` (strengths) and
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

**Where the plugin looks** (since 2026-11-19, `MaterialMaps.ResolveDir`), once per source and session:
1. the local cache, `BepInEx/ultrart/materials/<source>/manifest.json` (as above; maps made on this PC win);
2. a detail-map package under `BepInEx/plugins` (searched 3 folders deep): a folder with the marker
   `ultrart-detailmaps.json` (`{"format": 1, "name", "version", "sources": [...]}`; a higher format is skipped with a
   warning) and `<source>/manifest.json` + `<source>/*_d.png` beside it. r2modman installs the `UltraRT_DetailMaps`
   package (`scripts/package.ps1 -TexturePack`, [release.md](release.md)) to `BepInEx/plugins/<Team>-UltraRT_DetailMaps/`.
   Several packages: the newest marker wins, with a warning.

The log says which one in one line: `Material maps (ai): local cache <dir>`, `Material maps (ai): package
UltraRT_DetailMaps 0.3.0 in <dir>`, or with neither `Material maps (ai): none installed (optional: ...): per-texel detail
off` (info, no warning: the maps are optional). Tested in an r2modman layout on the alt install (2026-11-19,
`bench/results/20261119-175409_release-install_alt`: 1955 textures from the package; `-175550_release-dx11_alt`: the
local cache wins over an installed package).

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
  `_HybridLight` anyway), the lighting contribution stays in `primitive` bits 24-31. (Since 2026-11-19 only pattern
  cubes use C; hybrid's cube removal went with the mode.)

**Ray generation** (`PathTrace.raytrace`):
- Relight, pt-dn, pt: light is evaluated at the detail normal (direct, sky, bounce lobe), so the per-texel shading is
  physical. Ray origins are offset along the vertex normal, and a bounce or reflection direction that the detail
  normal tilted below the surface is mirrored back above it. The radiance-cache lookup uses the vertex normal.
- G-buffer: `_NormalDepth` gets the detail normal, so the denoiser's normal edge-stopping keeps texels apart instead of
  averaging their shading away.
- **Hybrid** (removed 2026-11-19; kept as history): the game's colour is already lit with the vertex normal, and the composite scales it by RT shadowed /
  unshadowed light. So `DirectLighting` samples and stores reservoirs with the vertex normal, the unshadowed
  denominator stays at the vertex normal (what the game used), and only the shadowed numerator is re-shaded with the
  detail normal (`c x max(0, n_d.L) / n_v.L`). That ratio would also scale the game's ambient, which doesn't depend on
  the normal, and at grazing light it swings from 0 to 2: tilted texel rows went black on 0-5's lava-lit walls. So the
  detail is applied as `direct_vertex x clamp((lum(direct_detail) + ambient) / (lum(direct_vertex) + ambient), 0.3,
  1.5)`. The clamp is for night scenes: next to a lamp, a texel tilted towards it saw several times the vertex
  normal's light and glowed (1-4).
- (Removed 2026-11-19 with hybrid.) Hybrid's composite picked one of the 2x2 nearest trace texels per screen pixel by depth. Per-texel shading made that
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
| `-ultrart-materials-roughness F`, `[Materials] Roughness` | 1 | global multiplier on the roughness variation (menu: Enabled, both strengths and this one, [settings.md](settings.md); the LOD entries are config / flags only) |
| `-ultrart-materials-lod on\|off`, `[Materials] TextureLod` | on | ray-cone texture LOD for the detail maps (section 8), shader-side |
| `-ultrart-materials-lod-bias F`, `[Materials] LodBias` | 0 | mips added to the cone's LOD (0 = a traced pixel's footprint) |
| `-ultrart-materials-toksvig F` | 1 | share of the filtered-away slope variance that becomes roughness (0 = off) |
| `-ultrart-materials-albedo-lod on\|off` | off | albedo/emission at the cone's mip on secondary hits (the game's textures mostly have no mips) |
| bench variants (`mat.hybridNormalStrength`, `mat.guidedPick`, `-ultrart-materials-hybrid-normal`, `-ultrart-materials-guided` and `[Materials] HybridNormalStrength` went with hybrid on 2026-11-19) `mat.enabled=0`, `mat.normalStrength=2`, `mat.roughness=0`, `mat.lod=0`, `mat.lodBias=-1.58`, `mat.lodAniso=1`, `mat.toksvig=0`, `mat.albedoLod=1`, `mat.lodDiffuse=0.3` | | e.g. `"relight+mat.enabled=0"` |

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

Final build at the defaults of then (detail on primary rays only; relight normal strength 2, hybrid 1; the relight
default is 1 since 2026-11-17), GPU total ms, detail
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
Primary, bounce and reflection rays all see it (hybrid, until its removal on 2026-11-19, kept the game's own colour, so only its bounce albedo changed).
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

## 10. Bake-off: DeepBump against PBRify (2026-11-17, `t3/light-levels-3`; plan B2)

`tools/materials/deepbump.py --cache <work> --deepbump <checkout>` writes a second map set, `<work>/db`, with
DeepBump's colour-to-normals network (GPL-3.0; cloned next to the work folder and run offline with onnxruntime on
the CPU: nothing of it is in the repository, and nothing it writes ships). The same recipe as PBRify's set: each
texel upscaled to a 4x4 block, the result box-filtered back to one normal per texel; a small texture is wrapped into
one 256 px tile so the network sees its own repeat. DeepBump has no roughness: the set carries PBRify's. 1,955
textures in 16 minutes on four threads (half a second each; a GPU is not needed).

**The maps** (sheet `bench/results/review/materials-deepbump-maps.png`: albedo, PBRify, DeepBump for six textures):

| | PBRify (`ai`) | DeepBump (`db`) |
|---|---|---|
| median tilt (`nstd`) | 0.215 | 0.140 |
| texel-to-texel disagreement (mean distance of a normal from its 3x3 mean, 200 textures) | 0.051 | 0.021 |
| noisy textures (grass) | per-texel noise | nearly flat |
| structured textures (panels, glyphs, a hatch) | sharper edges, stronger relief | the same structure, softer, wider bevels |

DeepBump's maps are what section 9 asked for: the relief without the disagreement between neighbours.

**In the engine** (`bench/suites/mat-matrix.json` and `mat-matrix-src.json` with `-ultrart-materials-source db`;
`20261117-225024_mat-matrix_alt`, `20261117-225210_mat-matrix-src_alt`; sheet
`bench/results/review/materials-deepbump-engine.png`), with today's default smoothing of 1 on both sets:

| Pose | PBRify vs DeepBump | PBRify vs maps off | DeepBump vs maps off |
|---|---|---|---|
| 1-1 indoor wall | 3.1 | 3.2 | 2.6 |
| 0-1 metal | 4.5 | 4.6 | 5.4 |
| start room | 0.8 | 1.6 | 1.5 |

(mean difference of the frames, of 255, noise included.) The two sets are as far from each other as each is from no
maps at all, and at native size the crops are hard to tell apart: the wall's bricks and the metal panels read the
same. The load-time smoothing already removed what DeepBump would have fixed.

**Conclusion.** No switch. PBRify with smoothing 1 stays: it is CC0, has its own roughness, and in the frame
DeepBump is not visibly better. DeepBump stays as a tool for a texture that PBRify gets wrong (its grass is the
example). PBRFusion 4 was not run: its model page is gated and its licence and files could not be confirmed
(2026-11-17), and a diffusion pipeline per texture is a different order of cost. Review list: R12, for information.

## 11. Lit weight: fullbright vs relit (2026-11-18, `t3/unlit-lit`)

The game draws `ULTRAKILL/Master` without `VERTEX_LIGHTING` (and the `psx/unlit/*` shaders) with no lighting at all.
In RT every proxy has a **lit weight**: 0 = fullbright (the albedo is the radiance, as in the game), 1 = fully relit,
in between a mix (`SceneProxies.Unlit.cs`). Dynamic and static-batched proxies carry it as `_Unlit = 1 - weight`;
StaticSceneOptimizer atlas slots carry the game's own weight in the vertex alpha, and an atlas variant whose original
material has a rule lifts the fullbright part: `lerp(_LitOverride, 1, alpha)` (`Proxy.shader`, `SampleAlbedo`).

Where the weight comes from (first match wins):

| Source | Weight | Notes |
|---|---|---|
| `unlit.extra` (bench variant) | `glob:w;glob:w` on material names | tuning in one launch: `pathtrace+unlit.extra=Gibs:0.5`. Not atlas slots |
| `"lit"` in `ultrart-materials.json` | 0-1 | by material, texture or shader glob, last matching rule wins; a rule with only `lit` leaves the reflection alone |
| unlit `ENEMY` material | `[Materials] EnemyTypeLitWeights` for its `EnemyType`, else `[Materials] EnemyLitWeight` (**0.5**) | the type comes from the renderer's `EnemyIdentifier` (first renderer seen per material; enrage swaps keep it) |
| `unlit.keepTypes` (`Drone,Swordsmachine`) | `unlit.keepWeight` 0.5 | painted glows (the drone's eye, the Swordsmachine's blade) keep 0.5 whatever `EnemyLitWeight` is, unless `EnemyTypeLitWeights` names them |
| unlit Master | 0 | screens, Gabriel, props |
| anything else | 1 | |

Switches: `-ultrart-lit-unlit` (relight everything, the old look), `-ultrart-enemy-lit w`,
`-ultrart-enemy-types "Filth=0.65, Stray=0.65"`, `-ultrart-lit-rules off`, `-ultrart-lit-probe GLOB` (logs every proxy
and atlas slot of matching materials with its weight: `Lit probe: ...`). Bench variants `unlit.enemy=0.8`,
`unlit.enemyTypes=Filth:0.65;Stray:0.8` (':' and ';', since '+' and '=' separate variants), `unlit.extra=...`,
`unlit.fullbright=0`: every proxy material made so far is re-weighted the next frame (`RefreshUnlitIfChanged`), so one
launch compares several weights. The scene log has `Lit weights (...): N proxy materials fullbright, N partial, N set
by a "lit" rule, N atlas variants with a rule; enemy types seen: ...`.

Shipped rules and decisions (2026-11-18; runs `bench/results/20261118-21*_unlit-*`, `20261118-220500_enemy-lit_alt`):

| Material | Weight | Evidence |
|---|---|---|
| `Gibs` | 0.6 | 0-2 4B hallway: fullbright gibs glow red in the dark; 0.5-0.75 sit in the room's light and stay readable |
| `LionStatue*`, `StatueBaseUnlit*`, `Statue3` | 0.7 | **not checked in a screenshot**: the 1-1 statue was switched off (or its skinned bounds 74 m wide) at every pose tried |
| `BoneWallRedUnlit` | 0.5 | **not checked**: the 6-1 soul tubes and P-2 arches were switched off at the poses tried |
| portraits `Hakita`, `Dawg`, `Imp`, `Francis`, `Jericho`, `CabalCrow`, `Florp`, `Presentation` | 0.6 | **not checked**: none came into frame (6-2, 8-2, 8-4) |
| `BloodPool` (0-1 `BloodWindow` flow, 0-2) | 0 (no rule) | 0-1 user pose 3: any lit weight makes the window lighter and less saturated than the game (the room's red light is stronger than its fullbright colour; screen luma 52.9 at 0, 58.1 at 0.5, 60.5 at 0.8, game 40.5) |
| `Mercury`, `Salt`, `Sulfur` (1-1 altar elements) | 0 (no rule) | altar liquids and stones that glow in the game; 0.6 changed nothing visible |
| `Basic Color - Orange/Red/Yellow Unlit` | 0 (no rule) | indicator lamps (8-2 `AlertLight/Yellow`, 0-E fan staircase, 0-5): emitters, left to the fixture handover |
| `GothicGateGhostsSolid` | 0 (no rule) | 6-1's painted ghost faces in the gates: a painted glow; 0.6 looked the same |
| `CityFromAbove` (2-3 `Unlit/Texture`, 4-1 `psx/unlit/notexwarp`) | no rule yet | the game draws them fullbright, we relight them; no shot showed the backdrop, so no rule ships (`unlit.extra=CityFromAbove*:0` to try) |
| grass (`GrassSprite1-4`, `GrassSprite* 1`) | 1 (lit) | lit Master (`VERTEX_LIGHTING`, `ALPHA_TEST`) in 7-1, 7-3, 1-1 to 1-4, 8-2, 8-3: relit, not fullbright (`-ultrart-lit-probe GrassSprite*`). 7-1's white sparkle is the game's own blade pixels: docs/path-tracing.md |

Enemy lit weight stays **0.5**. Comparison sheet (game / 0.5 / 0.65 / 0.8 on the 2-3 neon street and corridor, a 7-1
Mannequin, 1-1 outdoors): `bench/results/20261118-220500_enemy-lit_alt/enemy-lit-sheet.png`. Higher weights take on the
room's tint and shading (2-3's blue); 0.5 is closest to the game's flat colours.

Cost: none measurable (a property per proxy material, resolved when the proxy material is made; atlas variants only
for slots whose original has a rule; per frame only `RefreshUnlitIfChanged`'s signature of the settings). A merge-regress
rise after the merge (0-3 relight 6.20 -> 7.05 ms) did not reproduce in a direct `-Perf` A/B, f5e8053 against 8198414
(2026-11-18, `bench/suites/regress-unlit.json`, `bench/results/20261118-225844_regress-unlit` and `-230000_`): 0-3
ghosts relight 7.46 -> 6.57 ms, pathtrace 7.21 -> 6.25; 6-2 start relight 6.44 -> 5.87, pathtrace 6.50 -> 5.66; 0-5
lava relight 6.51 -> 6.43, pathtrace 6.02 -> 6.20. Equal or faster: run-to-run noise.

## 12. Stained glass (2026-11-19, `t3/stained-glass`)

The user's playtest of 2026-11-18 asked for real stained glass (light through the panes tinted, the leading casting
shadow) and named 1-4 pose 4 and 6-2 poses 1 and 2 (`bench/suites/user-1118.json`). What those poses turned out to
be, and what ships:

| Pose | What it was | Change |
|---|---|---|
| 1-4 pose 4 (V2 arena windows, `StainedGlassBig`/`Blue`, atlas slots) | not light at all: the windows are `_FOG_OFF` in the game, and the fog pass fogged them blue. Window mean colour game (145, 114, 94), before (75, 64, 71) | **No-fog surfaces** (below): now (140, 113, 100) in pathtrace and relight |
| 6-2 pose 1 (Grand Hall) | the windows are an opaque unlit wall texture (`GothicWindow`), not glass; the beams are additive meshes (`LightPillar HeresyWindows Big`); the light under each window is a red spot aimed 35 deg down with a 30 m range, lighting the floor by the window wall, 36 m short of where the beam lands | the four spots re-aimed along the beams: `data/lights/Level 6-2.json`, docs/lighting-overrides.md "Window beams in 6-2" |
| 6-2 pose 2 (Corner Room), "a floating light aimed at the window" | the fill `1 - Entryway/Lighting/Point Light (3)`, 8.5 m up: its traced shadow edge drew a lit wedge above the left window that reads as a second beam (it stays with `shaft.beams=0`) | unshadowed, as the game draws it: the wedge is gone |

**Transmission** (all `StainedGlass*` materials, a `"transmit": 1` rule in `ultrart-materials.json`): shadow rays pass
the glass, tinted; the leading (dark texels) blocks.

- **Path**: the rule gives the proxy material (or the atlas variant) `_Transmit`, and its submeshes run the any-hit.
  Shadow rays set `ULTRART_PAYLOAD_SHADOW` and start with a transmittance of 1 in the payload's albedo
  (`VisibleRaw`, UltraRTLights.hlsl; `Unoccluded`, Volumetrics.raytrace). The any-hit (Proxy.shader, both passes)
  multiplies in `lerp(1, saturate(colour^2.2 x transmit x gain), opacity)` (opacity: the texture alpha for transparent
  glass, 1 for opaque) and ignores the hit, unless nothing is left: then it blocks. The lighting multiplies by the
  result (`g_ShadowTint`: direct light in relight and pathtrace, emitters, sky, the radiance cache, lit particles, the
  viewmodel; `g_VolTint`: the air's in-scatter, so sun shafts through a window take its colours).
- **Transparent stained glass** (`StainedGlass1A 10-19`, mask 0x10) was never in a shadow ray's mask, so it cast no
  shadow at all. Its instances now also carry the geometry bit (`DynamicProxy.ShadowMask`, static transparent parts in
  StaticGroups) with every enabled submesh running the any-hit, which lets every ray through except shadow rays (tint)
  and the transparent pass's own (`ULTRART_PAYLOAD_TRANSPARENT`, PathTrace `TraceTransparent`).
- **Switches**: `[Materials] StainedGlass` (default on), `-ultrart-glass on|off`, `-ultrart-glass-gain F` (default
  1.5), bench `glass.gain=2`, `glass.enabled=0` (the gain goes to 0: opaque glass blocks again, transparent glass casts
  nothing; the instance masks stay until the next level load). `-ultrart-glass-extra GLOB` (diagnostic): every
  matching material transmits. The scene log has `Stained glass (...): N proxy materials and N atlas variants
  transmit, N transparent instances (re)added with the geometry bit`; `-ultrart-lit-probe StainedGlass*` lists them.
- **Check**: with `-ultrart-glass-extra *` on 1-1's sunlit start (`bench/suites/glass-check.json`,
  `bench/results/20261119-000128_glass-check_alt`) the ground under the trees brightens in the leaves' green and the
  trunks' brown (4 % of pixels change; dark textures pass little light after the ^2.2). With the real rule no pose
  checked so far shows a coloured patch: the windows found (1-3's Water Hall `StainedGlassBig`, 1-4's arena, 8-3 pose
  1's `StainedGlass1A`, P-2's hall) sit on solid walls or face away from the sun, so nothing comes through
  (`bench/suites/glass-13.json`, `glass-ab.json`, `glass-p2.json`; image differences on vs off are noise).
- **Cost** (`-Perf`, exclusive lock, 1440p, `bench/suites/glass-perf.json`; `-ultrart-glass off` -> default,
  `bench/results/20261119-001108_glass-perf` -> `-001220_`): GPU total 1-3 hall relight 4.33 -> 4.56, pathtrace
  4.80 -> 4.97; 1-4 pose 4 relight 4.06 -> 4.20, pathtrace 4.45 -> 4.51; 8-3 pose 1 relight 6.33 -> 6.25, pathtrace
  5.97 -> 5.91 (noise). About 0.1-0.2 ms where stained glass is in view; frame times in that run are noisy (the first
  run was colder).
- **Scenes without stained glass** (2026-11-19): merge-regress after the merge (1c2ec96) showed GPU +0.2-0.3 ms in
  0-1 metal's dispatch. In one exclusive session (`bench/suites/glass-cost.json`, `-Perf`, GPU total / dispatch ms):
  bb80589 0-1 metal relight 4.98 / 2.89 (repeat 4.97 / 2.90), pathtrace 5.62 / 2.58 (5.60 / 2.59), 6-2 start relight
  4.66 / 2.59 (4.68 / 2.61), pathtrace 4.83 / 1.88 (4.82 / 1.88); 1c2ec96 5.00 / 2.92, 5.67 / 2.64, 4.70 / 2.62,
  4.86 / 1.91. So +0.03-0.06 ms, not +0.2-0.3: the merge-regress run shared the GPU with other agents. Since then
  the any-hit runs the old alpha-only test unless the scene has stained glass (`_UltraRTGlass.y`, `SceneProxies.HasGlass`):
  the glass path samples the full colour, which the plain alpha test never needed. Measured after: 5.04 / 2.95,
  5.70 / 2.67, 4.76 / 2.66, 4.88 / 1.92 (`-ultrart-glass off`: 5.02 / 2.93, 5.66 / 2.64, 4.72 / 2.63, 4.88 / 1.91),
  the same within the session's drift.

**No-fog surfaces.** ULTRAKILL/Master materials with `_FOG_OFF` are drawn without the distance fog; the fog pass
(VolumetricsComposite.shader pass 0) fogged every traced pixel. One implementation since the merge with
`t3/playtest-materials` (0-1's gears, docs/volumetrics.md): the proxy material carries `_NoFog`, and so does the atlas
variant of a slot whose original has `_FOG_OFF` (`AtlasVariant`; 1-4's windows are atlas slots), the hit sets
`ULTRART_FLAG_NOFOG`, `PackSurface` keeps it as the sign bit of emission.b's half (`SurfaceNoFog`), and the fog pass
reads the depth-matched trace texel of the 2x2 and leaves those pixels alone. Bench `plugin._noFogSkip=0` fogs them
again. Run (first implementation): `bench/results/20261118-234255_glass-fog_alt`.

## 13. Shipping without the maps (2026-11-19, `t3/detail-maps-comparison-dlss`)

The maps are made from the game's textures, so the release will not bundle them: they become a separate texture-pack
download. What does a user without the pack see? Suite `bench/suites/detail-pack.json` (6 poses: 0-1 metal, 1-1
start, 1-1 indoor a, 8-2 a, 6-2 pose 1, 2-1 pose 1; `off`, pathtrace and relight, each with the `ai` maps and with
`mat.enabled=0`) and `detail-pack-src.json` (pathtrace, relight) run with `-ultrart-materials-source proc` and with a
source folder that does not exist (the real no-pack path: `Material maps: no cache at ...: per-texel detail off`;
since the package search the line is `Material maps (<source>): none installed ...`).
Defaults of the day: strength 1, smoothing 1, LOD on. Alt slot, 2560x1440, RR at 1/3. Runs
`bench/results/20261119-171951_detail-pack_alt` (ai, off, maps off), `-172244_detail-pack-src_alt` (proc),
`-172457_detail-pack-src_alt` (no cache); sheets (local, game-derived) `bench/results/detail-pack-sheets/<pose>_sheet.png`
(columns game / no maps / proc / ai, rows pathtrace / relight) and `<pose>_zoom.png` (2x crops of a textured wall).
The proc set was installed in the alt install for this (`BepInEx\ultrart\materials\proc`).

- **How visible.** Small. At full frame the three RT columns are hard to tell apart in every pose; in the 2x crops
  the `ai` maps add a little bevel shading to 1-1's torch-lit bricks and 0-1's louvres, and almost nothing on 8-2's
  facades, 6-2's skull wall, 2-1's carved walls or 1-1's shaded gate. Mean frame difference `ai` vs maps off is
  2-4 of 255, and the same settings in two launches (no cache vs `mat.enabled=0`) already differ by 2-7 (gun sway,
  flames, haze): most poses are at the noise floor. Only the 1-1 brick wall crop is clearly above it (3.0 vs 1.1 in
  pathtrace). Local contrast (mean |pixel - 3x3 mean| in the crops) is the same with and without maps within 0.05,
  so the maps change the shading of texels, not the frame's texel-scale contrast. Since strength 1 and smoothing 1
  (section 8, 9) the relief is deliberately gentle; section 5's "relight is where it pays off" was at strength 2.
- **No maps does not look broken anywhere seen.** The detail roughness is an offset around the class roughness
  (b = 0.5 + (r - mean) / 2), so without a detail texture (`_DetailParams.w` 0) the hit shader keeps the class
  roughness, the value the maps vary around; Toksvig widening only applies with detail. Signed mean brightness
  change with maps vs without is within +-1 of 255 in every pose and mode; the metal room's reflections look the same
  (a little smoother on the slats). `mat.enabled=0` and the no-cache path give the same picture (the no-cache frames
  differ from `mat.enabled=0` only by the launch-to-launch noise above).
- **proc vs ai.** As close to each other as each is to no maps (1.4-4 of 255). In the crops proc is a shade flatter
  than ai on bricks; nothing looks wrong with it at these poses. A reasonable no-pack default if it can be made on
  the user's machine (below); "maps off" is an equally defensible default, and costs nothing.
- **Generating proc at runtime.** The generator (`generate.py`, `procedural`) needs only the albedo texels: sRGB
  luminance, a wrapped 3x3 Sobel (slope scale 2), and roughness from the mean luminance and the 3x3 standard
  deviation, plus the `fill_transparent` mean colour for cutouts and the texture's mean roughness (`rmean`). That is
  a few dozen operations per texel: 1-1 binds 136 textures, 8-2 323, nearly all 32-256 px, so the arithmetic is a few
  ms per level in C# (or one compute dispatch). The cost is getting the texels: game textures are not CPU-readable,
  so each would be blitted into an sRGB RGBA32 render target (point, same size: decodes DXT/crunch) and read back,
  ideally in one batched AsyncGPUReadback per level, then fed to `MakeDetailTexture` (which already builds the mips
  and variance from `Color32[]`) and to the atlas composition (which also wants the CPU pixels). Estimate: tens to
  ~150 ms per level load, in the range of today's 65-140 ms PNG decode, and a disk cache keyed by `<name>@<w>x<h>`
  (the same keys) would make it a one-time cost. Nothing game-derived would ship. Not implemented.

## 14. Stained-glass projection (2026-11-20, `t3/stained-glass-projection`)

Section 12's transmission only shows where a light stands behind the glass, and ULTRAKILL's stained glass sits on solid
walls with nothing behind it. The user asked for the panes' colours on the floor and walls, the dark leading drawing
crisp lines in them, without a sun. So every stained-glass window now sends its own collimated beam into the room.

Code: `src/UltraRT/Plugin.StainedGlass.cs` (discovery, bake, per-frame upload), `StainedGlassBake.shader` (the bake),
`UltraRTStainedGlass.hlsl` (`StainedGlassLight`, called once in `PathTrace.raytrace` after `DirectLighting`, so relight
and pathtrace both get it at primary hits). Suites `bench/suites/stained-glass.json` (look) and
`stained-glass-perf.json` (`-Perf`); under water `stained-glass-water.json` and `stained-glass-water-perf.json` (below).

**How it works**

- **Discovery** (first traced frame of an RT scene, and again when a window renderer is destroyed, e.g. a checkpoint
  restore): every `MeshRenderer` (inactive ones too; UltraRT's own proxies skipped) with an opaque material matching
  `sg.materials` (default `StainedGlass*,GothicWindow*`; StaticSceneOptimizer's atlas slots by their original material,
  sliced by the original submesh's index count). Transparent stained glass (`StainedGlass1A`, render queue >= 2450) is
  left out: it stands free in rooms (1-4's railings and arches) and keeps section 12's shadow tint only. The slot's
  triangles (GPU readback) are grouped into flat connected pieces (coplanar within 5 cm, gaps under 0.3 m); a piece over
  1.2 m both ways is a window (the 0.5-1 m edges of glass slabs are not). Frame: U horizontal, V up the pane.
- **Plain textures are not windows**: a texture whose 32 x 32 copy is nearly grey (mean chroma under `sg.minChroma`,
  0.08) and flat (luma spread under 0.08) is skipped before it takes a slice; atlas slots are judged on their baked
  slice. 6-2's and P-2's `GothicWindow` is the grey `smoothgrid` texture (chroma 0.00, spread 0.02) on 10 x 20 m wall
  faces, UV 0-1, `_Color` white: their arched windows are red because the rooms' lights are. Nothing to project there
  (18 of 18 slices in 6-2, all 313 renderers in P-2 dropped). P-2 has no `StainedGlass*` material at all.
- **Bake**: each window's own triangles (a compact copy of the source vertices, every attribute) are drawn flat into
  one slice of a 256 x 256 `Texture2DArray` (RGBA8, point): the vertex shader places a vertex by its world position in
  the window frame; the pixel shader samples the texture with the material's tiling and `_Color`, or the atlas rect
  (TEXCOORD1, `frac(uv0)`) x vertex colour, as Proxy.shader does. Off-plane triangles are discarded. Identical windows
  (the inactive copies checkpoints keep of a room) share a slice; at most 96 slices, the biggest windows first. No mips:
  `GenerateMips` on a 2D-array render texture crashed D3D12 here ("could not create a compute pipeline state object").
  1-4: 40 slices, upright panes with their leading, tiled where the game tiles them (`sg.dump=1` or
  `-ultrart-stained-glass-dump 1` writes them to `BepInEx/ultrart/stained-glass/<scene>_<slice>.png`; game-derived,
  never commit).
- **Room side**, decided the first frame a window is live (at the scene build its room's colliders often do not exist
  yet: 1-4's arena raycasts all missed and half the windows faced the void): not the side with a wall within 1 m, and
  the side where 6 rays fanned around the beam hit the level within its reach (a room) rather than escape; ties go to
  the side the faces show. A one-sided face whose front looks away from the room (the outer face of 1-4's 0.5 m glass
  slabs) and a pane facing up are dropped. The log lists each decision (`Stained-glass windows live:`).
- **Beam**: the inward normal tilted down to `sg.tilt` (40 deg; 6-2's game beams fall 27 deg, but at 30 deg the high
  windows of 1-4 and the P-2 hall land beyond the floor), reach `sg.reach` 80 m. Per frame the 16 live windows whose
  beam segments pass nearest the camera (or a portal view) go to `_UltraRTStainedGlass` (5 float4 each).
- **Shader**: per primary hit, each window: the ray back along the beam meets the pane at (u, v); outside [0, 1], behind
  the pane or facing away: nothing. Else the slice is point sampled; gamma colour below `sg.leadFloor` (0.12) passes
  nothing (the leading), above it is stretched back to 1 and saturated (`sg.saturation` 1.3); light =
  `ToLight(colour x strength x relight exposure) x cos`. Where beams overlap, one is picked by brightness (exact where
  one reaches, which is nearly everywhere); one shadow ray (`VisibleMasked`, mask 0x05 plus V1) stops 0.15 m short of
  the pane: pillars, the planters, enemies and V1 cut the mosaic, and the stone around a pane that sits behind the wall
  surface blocks the rest (1-4's panes are 10 x 20 m faces of which about 5 x 9 m show through the opening).

**Switches**: `-ultrart-stained-glass F` (0 or off = off), `-ultrart-stained-glass-tilt F`, config
`[Effects] StainedGlassProjection` (Advanced > Effects), bench `sg.strength=0`, `sg.tilt`, `sg.reach`, `sg.leadFloor`,
`sg.saturation`, `sg.maxWindows`, `sg.minChroma`, `sg.water`, `sg.waterBlur`, `sg.waterShift`, `sg.waterAbsorbScale`
("Under water" below) (the build-time fields `tilt`, `reach`, `resolution`, `materials`
rebuild the windows when changed; `minChroma` applies at the next build). Diagnostics: `sg.debug=1` no shadow ray,
`2` white wherever a beam reaches, `3` bright magenta there (no cosine, no texture), `4`-`6` staged tests of the
shader's checks (any window bound / faces a beam / within reach), with a CPU probe of the same math at the crosshair in
the log (`SG probe`); `7` the water depth over each hit (under 0.5 m red, 1 yellow, 1.5 green, 2 cyan, 3 blue, deeper
magenta); `sg.noShadow`, `sg.bake=0`.

**Look** (alt, 2560 x 1440, `bench/results/20261120-175513_stained-glass_alt`; strength 3): 1-4's V2 arena, looking at
the pool in front of the west window: the dove and flame panes land on the landing beside the pillar (warm) and on the
pool floor (violet and blue, the leading as dark lines), seen through the water; the pillar and planter shadows cut
them. 7 % of the frame brighter by more than 8 of 255 in both modes; pathtrace (DLSS RR) a little crisper than relight
(the UltraRT denoiser), both keep the leading lines. The user's pose 4 view barely sees the lit floor (the beams land
behind the pool). 6-2 pose 1 and P-2's hall: no window projects (see above); their A/B differences are noise (P-2's
lights shift quickly: pathtrace mean +9.9 of 255 with no window bound). Strength 1 (the first default) was too faint in
linear transport (dark pane colours ^2.2); 3 reads as coloured light without lifting the room.

**Dark pixels** (`tools/lights/punch.py` on the same run; mean luma of the pixels the game leaves dark, RT minus game):
1-4 west +18.5 -> +18.6 (relight), +17.9 -> +18.0 (pathtrace); 1-4 pose 4 +15.5 -> +15.5, +15.0 -> +15.0. Unchanged:
only the beams' footprints get light.

**Cost** (exclusive `-Perf`, main install, 1440p, `bench/results/20261120-175809_stained-glass-perf`, GPU total /
dispatch ms, `sg.strength=0` -> default): 1-4 arena west relight 4.32 / 2.31 -> 4.41 / 2.40, pathtrace 4.63 / 1.66 ->
4.70 / 1.72; 1-4 pose 4 relight 4.08 / 2.11 -> 4.13 / 2.17, pathtrace 4.45 / 1.53 -> 4.48 / 1.56. About +0.05-0.1 ms
where windows are bound; nothing where none is (a count of 0 skips the call). CPU: the build is one frame of 50-150 ms
per level (1-4 54 ms, P-2 148 ms with 313 renderers read back); the per-frame upload is negligible. VRAM: 256 KB per
slice, 10 MB in 1-4, 25 MB at the 96-slice cap.

**Under water** (2026-11-20, `t3/stained-glass-water`). The user liked the mosaic on the landing before the steps but not
on 1-4's pool floor under the water: crisp and bright, as if pasted on top of the water (the overlay's view absorption
alone left it at full contrast). The light that lands under a water plane has now crossed the water:

- **Where**: a hit under one of the ripple caustics' water planes (`_UltraRTWaterPlanes`: inside its rectangle, below
  its surface, above its lowest receiver; docs/caustics.md section 6), the same test as `RippleCausticAt`. The planes
  are now also bound when ripple caustics are off (`caustics.ripple=0`) while windows are bound; `_UltraRTRipple.x` stays
  0 then, which skips the caustics. None while the camera is in water (the planes aren't bound then: as dry). Dry hits
  take the old code unchanged (no extra work, no random number).
- **Dimmed** along the light's path in the water: refracted at the mean surface (n 1.33), path = depth / cos (capped at
  `trans.waterAbsorbMaxPath`), T = lerp(`trans.waterAbsorbFloor`, 1, exp(-sigma path)) x (1 - Fresnel), sigma =
  `trans.waterAbsorb` x `sg.waterAbsorbScale` (2.5). Grey: the pane gives the colour, and the overlay's view absorption
  still adds the water's hue on top. The scale is there because 1-4's pool is shallow (0.5-1 m, `sg.debug=7`): at the
  overlay's 0.35 / m the light lost under 20 % and still read as pasted on.
- **Shifted by the ripples**: the lookup point moves by the ripple caustics' own map run backwards
  (`WaterRippleSourceShift` in `UltraRTWater.hlsl`: the same q, two fixed-point steps, interactive rings, footprint band
  limit and 60 m view fade) x `sg.waterShift` (3). It is taken at the receiver as for light from straight above, as the
  caustics take it, so the mosaic wobbles with the caustic lines the albedo factor draws on the same floor and moves
  with the ripples. The shadow ray keeps the unshifted point.
- **Blurred with depth**: radius on the pane = `sg.waterBlur` (0.25 m per metre of in-water path), 8 bilinear taps on
  a Vogel disc turned by one random number per sample (the denoiser and DLSS RR average it); under half a texel it
  stays one point sample. The leading is applied per tap, so the lines soften rather than grey out.
- Not done: no refraction shift of the whole beam (the floor image would move toward the window by about half the
  depth, and the straight shadow ray would no longer match it); pool walls get the same treatment as the floor.

Result (alt, 2560 x 1440, `bench/results/20261120-182650_stained-glass-water_alt`, review sheets in the main
checkout's `bench/results/review/stained-glass-water/`): the pool floor's mosaic is softer, wavier and dimmer, still
recognisable, in both modes; pool region mean (0-255) 64.9 -> 62.6 relight, 64.6 -> 62.1 pathtrace, 21-24 % of its
pixels change by more than 8. The dry landing before the steps: mean |difference| 1.6 relight / 2.3 pathtrace against a
frame-to-frame noise of the same mode of 2.1 / 2.6 (`1-4-west_dry_landing_unchanged.jpg`): unchanged. Pose 4 sees the
pool far off: 1.5-2.8 % of the frame changes. Motion (static camera, six stills 0.1 s apart): the mosaic drifts with the
ripples, no sparkle in either mode (`1-4-west_motion_seq_*.jpg`). Tuning (`20261120-182504`): blur 0.06 / shift 1 /
absorption 1 (physical) barely changed this shallow pool; 0.15 / 2 softened it; 0.25 / 3 / 2.5 chosen.

Cost (same-session `-Perf` on alt with main idle, `bench/results/20261120-182801_stained-glass-water-perf_alt`, GPU total
/ dispatch ms, `sg.water=0` -> default): 1-4 arena west relight 4.469 / 2.440 -> 4.461 / 2.438, pathtrace 4.669 / 1.730
-> 4.675 / 1.736; pose 4 relight 4.181 / 2.201 -> 4.180 / 2.205, pathtrace 4.433 / 1.567 -> 4.444 / 1.575. Within
noise (+0.01 ms at most); no ray added.

**Not done**: 1-1 to 1-3 use the same `StainedGlassBig/Blue/Red` materials and should work unchanged (not checked). No
volumetric beam (the haze stays the game's) and no bounce light from the coloured pools (primary hits only).
