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
`materials` (looks and GPU cost, detail on/off within one run) and `materials-tune` (strengths, guided pick).

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
skipped (they were covered by other slots). The composition takes about 7 ms.

**Hit shader** (`Proxy.shader`): the ray generation shader requests detail with payload flag bit 3
(`ULTRART_FLAG_DETAIL`) on primary and reflection rays only; bounce, radiance-cache and volumetric rays skip it (their
hits are blurred by the cache and denoiser anyway). With detail:
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
  detail is applied as `direct_vertex x (lum(direct_detail) + ambient) / (lum(direct_vertex) + ambient)`.
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
| Marble, MetalThin, MetalLow | 0.7 | 0.6 / 0.8 |
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
| `-ultrart-materials-normal F`, `[Materials] NormalStrength` | 1 | global multiplier on the class normal strength |
| `-ultrart-materials-roughness F`, `[Materials] Roughness` | 1 | global multiplier on the roughness variation |
| `-ultrart-materials-guided on\|off` | on | hybrid composite: albedo-guided trace texel pick |
| bench variants `mat.enabled=0`, `mat.normalStrength=2`, `mat.roughness=0`, `mat.guidedPick=0` | | e.g. `"relight+mat.enabled=0"` |

Log lines: `Material maps: N textures in ...`, per atlas `Material maps: atlas '' WxH: cells with detail ...`,
`Material maps: atlas slots mapped per original submesh ..., by dominant material ..., skipped ...`, and after the
build `Material maps (settings): N proxy materials bound, M without maps, K textures loaded in T ms`.

## 5. Results

RESULTS_PLACEHOLDER

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
- **Bounce and cache hits ignore detail** by design (cost); the indirect light is smooth anyway.
