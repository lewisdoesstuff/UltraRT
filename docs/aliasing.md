# Aliasing per view mode

How much stair-stepping, crawling edges and specular sparkle each view mode has, measured against an anti-aliased
reference of the same frozen instant, and which anti-aliasing approach would help each kind. Measurement only: no
rendering default changed. Suite `bench/suites/aliasing.json` (written by `tools/aliasing/make_suite.py`), analysis
`tools/aliasing/analyze.py`, MSAA probe `src/UltraRT/Plugin.MsaaProbe.cs` (bench variant `plugin._msaaProbe=4`).

## Summary (2026-11-19, RTX 5070 Ti, 2560x1440, alt install, `bench/results/20261119-150542_aliasing_alt`)

- **At rest, the traced modes are not more stair-stepped than the game; they are softer.** Their still frames sit
  0.56-0.91 of the way from pixel-centre edges to the anti-aliased reference (game raster 0, hybrid 0.11-0.33): the
  TAAU (relight) and DLSS RR (pathtrace) blur edges at least as much as a box-filtered pixel does.
- **In motion they crawl like the game or worse.** Frame-to-frame edge error in a slow pan: game 5.9-8.5 levels, RT
  modes 8.0-10.7 on ordinary geometry (0-1, 1-4, 8-2), and 32-41 against 6.6 on 7-1's grass (sub-pixel blades over a
  bright background), which also flickers in relight with the camera still (17 against 1.6-2.4 elsewhere).
- **Thin geometry under RR is lost, not aliased**: at trace scale 1/3 the grass blades smear away (edge error 47
  against a mode aliasing of 41); a higher RR trace scale helps there more than any AA.
- **Specular aliasing is small and Toksvig does not move it**: highlight error 4-13 levels, highlight crawl 3.7-5.8
  in a slow pan (game texture crawl at the same pixels 0.6-3.7); `mat.toksvig` 0, 1 (default) and 3 differ by at most
  0.3 levels on every metric, within run noise.
- **Ghosting is a minor share** of the traced modes' motion error: the previous frame explains 9-16% of the
  error at 0.5 px/frame (lag 0.19-0.23) and 2-7% at 10 px/frame (lag 0.01-0.06); on 7-1's grass 15-24%.
- **MSAA on the game's raster works for the game's image** (4x: 0-1 edge error 9.8 -> 4.7, partial-coverage pixels
  18.7 -> 8.2, textures untouched) but helps neither relight nor pathtrace (their colour is traced), not alpha-tested
  foliage (7-1: 6.7 -> 5.7), and breaks hybrid until our depth copy and the game's depth readers get a resolve.

Recommendation (non-temporal, as the user wants; details and costs in "What would help"):

| Category | Best non-temporal option | Expected cost (1440p) |
|---|---|---|
| Geometric edges, `off` / `hybrid` | 4x MSAA on the game's targets, with a depth resolve for our passes and the game's decals | frame time +0.00 / +0.12 ms on `off` (CPU-bound, GPU cost not separated); resolve passes ~0.05 ms (estimate) |
| Geometric edges, `relight` / `pathtrace` | Edge-adaptive supersampling in our trace: extra primary samples (visibility + texel) only on geometric-edge pixels (3-7% of the screen), lighting reused | ~0.2-0.6 ms (estimate); full lighting per sample 1-2.7 ms |
| Stair-steps after any mode, cheapest | SMAA 1x with edges from depth / instance (geometry-predicated, so texels stay crisp) | ~0.3-0.5 ms (literature, not measured) |
| Thin geometry (grass, wires) | Coverage: alpha-to-coverage under MSAA for the game raster; in our trace, edge supersampling above; under RR, a higher RR trace scale | see rows above; RR at scale 1/2 ~2.25x the trace pixels |
| Specular | Nothing urgent: geometric specular AA (Tokuyoshi-Kaplanyan normal-variance roughness at primary hits) only if a sparkle case shows up | ~0.02 ms (ALU only) |

## How each mode makes its pixels (what can alias)

| Mode | Where a pixel's colour comes from | Temporal parts |
|---|---|---|
| `off` | The game's forward raster: one sample per pixel, point-filtered textures (mostly without mips), no MSAA, no AA pass | none |
| `hybrid` | The game's raster, multiplied by a ray-traced lighting ratio (trace at 0.5, depth-matched 2x2 upsample) | denoiser on the ratio |
| `relight` | Our own trace: primary hits at trace scale 0.5 on a 2x2 jitter cycle, rebuilt at screen resolution by the TAAU pass ([denoiser.md](denoiser.md), "Temporal upsampling"); textures from the exact hits | denoiser, TAAU (exact pixel-centre jitter, so it reconstructs pixel centres: no supersampling) |
| `pathtrace` | Our own trace at 1/3 on the exact 3x3 jitter grid, denoised and upscaled by DLSS Ray Reconstruction; texels remodulated from exact hits ([dlss.md](dlss.md), "Exact jitter grid") | DLSS RR (its own history) |

The game's raster is only the final image in `off` and `hybrid`. In `relight` and `pathtrace` the colour is traced, so
whatever anti-aliases the game's raster (MSAA, a post AA run before our composite) never reaches those pixels: AA for
them has to happen in our trace or after our composite.

## Method

**Same instant, many takes.** One bench cinematic scenario per pose (bench/README.md, "Cinematics"), with the world
frozen (`TimeScale` 0), renders every take below along one camera path, so every take shows the same world state and
lines up pixel for pixel. `Cinematic.Takes` (new, `src/UltraRT/Bench/BenchModel.cs` `CineTake`) gives each take its
own view mode (bench mode string, variants allowed), render mode, AA, samples and time window.

**The path** (`bench/cinematics/alias-*.json`): 8 frames still at the pose, 12 frames of a slow pan (3 deg/s yaw and a
0.3 m/s strafe: about 0.5 px per frame at 1440p), 12 frames of a fast one (60 deg/s, 6 m/s: about 10 px per frame), at
60 fps. The fast segment is still slower than a lot of play; it is where reprojection and history show.

**Takes per pose:**
- *Live*: `off`, `relight`, `hybrid`, `pathtrace` exactly as played (`Mode: live`: trace scale, denoiser, TAAU, DLSS RR
  and their histories, after a 90-frame warm-up at the first pose), the whole path.
- *AA reference*: each mode accumulated at full resolution (the offline renderer, [cinematics.md](cinematics.md)), 8
  Halton sub-pixel jitter positions x 16 frames = 128 samples per pixel (a box-filtered pixel); the game's own image
  with 16 jitter positions. From the still pose to the end (25 frames). 16 frames per jitter position keeps clear of
  the LDR AA-average trap (samples are tone-mapped before the AA average: fewer than 16 per position come out darker).
- *Un-jittered reference* (`-ref1`): the same 128 samples, all at the pixel centre, for the still frame: the converged
  image with exactly the live frame's sampling of geometry. `ref1` vs `ref` is the mode's own aliasing, free of noise
  and denoiser error.
- *Masks*: the `instance`, `normals`, `depth` and `spec-params` debug views at full resolution, un-jittered, every frame.

**Pixel classes** (geometry from the debug views, not image content): *edge* = the instance changes, the normal turns
more than 30 degrees or the depth jumps more than 4% between two neighbours (both pixels of the pair; sky counts as an
instance); *texture* = not an edge but the AA reference has a luma gradient above 12 levels (texel edges); *flat* = the
rest; *spec* = not an edge and a specular surface (spec-params weight or reflection strength above 0.05); *hilite* =
the brightest 10% of the spec pixels in the reference (highlights, reflections of lamps). Edge shares in these poses:
see the results.

**Metrics** (8-bit display levels, RGB RMS; the live frame is first scaled by its least-squares gain on flat pixels,
which absorbs auto-exposure differences between live play and the offline render; the gain is reported):
- *edge / texture / flat / hilite RMSE*: live still frame vs AA reference per class.
- *alias edge*: `ref1` vs `ref` on edge pixels: how much the mode's own converged image aliases.
- *AA position* on edges (and highlights): where the live frame sits between the un-jittered (0) and the AA reference
  (1), `<L - R1, R - R1> / |R - R1|^2` over pixels where they differ by more than 6 levels. Noise that does not
  correlate with the AA difference averages out. 0 = pixel-centre (stair-stepped) edges, 1 = anti-aliased.
- *stair*: live vs AA reference over the partial-coverage pixels (edge pixels where `ref1` and `ref` differ by > 6).
- *shimmer*: frame-to-frame change of the error against the reference of the same instant, `RMS(e_t - e_t-1) / sqrt 2`,
  per class, separately for the still hold (the reference does not change: denoiser noise and jitter flicker only),
  the slow pan and the fast pan. Crawling stair-steps and flickering highlights raise it in motion above the still
  baseline; noise alone does not.
- *lag*: `<L_t - R_t, R_t-1 - R_t> / |R_t-1 - R_t|^2` over pixels whose reference changes by more than 8 levels between
  frames: the share of the previous frame's image still visible in this one (ghosting / smear). 0 = none.

Crops of the worst 96x96 tiles of the still frame per mode and class (live | AA reference | un-jittered reference |
error x4) go to `<run>/aliasing/crops/` (not committed: rendered frames).

Run it:

```powershell
py -3 tools/aliasing/make_suite.py                 # writes the suite and paths (already committed)
.\scripts\bench.ps1 -Suite aliasing -Slot alt -TimeoutSec 3600
py -3 tools/aliasing/analyze.py bench/results/<run>_aliasing_alt
py -3 tools/aliasing/lag_share.py bench/results/<run>_aliasing_alt   # the lag share column
```

## Results (2026-11-19, `bench/results/20261119-150542_aliasing_alt`, analysis in its `aliasing/`)

Poses: **7-1 graves** (u1118-7-1-2: fence rails, crosses, a tree and cutout grass against a bright sky), **1-4 hall**
(u1118-1-4-4, checkpoint load: a long dark hall, distant lamp posts, window frames), **0-1 metal** (the statue room's
striped metal walls and floor), **8-2 street** (wet street and pool, lamp posts). Volumetrics off in every traced
take (see "Traps"). Live modes as played: relight at trace scale 0.5 + TAAU, pathtrace at 1/3 + DLSS RR.

Pixel shares of the still frame: geometric edges 2.8-7.1% (0-1 4.0, 1-4 2.8, 7-1 7.1, 8-2 4.2), of which
partial-coverage pixels (the AA reference differs from the pixel-centre image by more than 6 levels) 1.5-5.8%;
texture edges 2.0-4.1%; specular 6.6-76.8%; highlights 0.7-20.4%.

### Still frame against the AA reference (levels, 0-255)

`alias` = the mode's own un-jittered converged image against its AA reference (for the game's raster, live is the
un-jittered image, so the two columns match). `AA pos` = where live sits between pixel-centre (0) and anti-aliased (1).

| Pose | Mode | edge RMSE | alias (edge) | AA pos (edge) | stair | flat RMSE | hilite RMSE | alias (hilite) | AA pos (hilite) |
|---|---|---|---|---|---|---|---|---|---|
| 0-1 metal | off | 9.85 | 9.85 | 0 | 18.3 | 1.15 | 3.98 | 3.98 | 0 |
| 0-1 metal | hybrid | 9.26 | 8.13 | 0.11 | 15.1 | 1.94 | 3.44 | 3.28 | 0.21 |
| 0-1 metal | relight | 12.37 | 11.24 | 0.65 | 18.1 | 3.90 | 6.47 | 6.15 | 0.77 |
| 0-1 metal | pathtrace | 13.02 | 11.36 | 0.84 | 18.3 | 3.84 | 6.90 | 6.24 | 0.78 |
| 1-4 hall | off | 6.96 | 6.96 | 0 | 20.8 | 1.02 | 1.42 | 1.42 | 0 |
| 1-4 hall | hybrid | 8.42 | 7.84 | 0.16 | 19.1 | 5.45 | (20.6) | (14.7) | (-0.32) |
| 1-4 hall | relight | 15.18 | 14.20 | 0.56 | 18.0 | 7.28 | 4.34 | 5.31 | 0.43 |
| 1-4 hall | pathtrace | 17.50 | 14.26 | 0.64 | 20.5 | 7.37 | 4.67 | 5.59 | 0.58 |
| 7-1 graves | off | 7.09 | 7.09 | 0 | 18.7 | 1.27 | 0.04 | 0.04 | - |
| 7-1 graves | hybrid | 19.83 | 17.81 | 0.33 | 26.3 | 4.46 | 2.63 | 2.61 | 0.15 |
| 7-1 graves | relight | 51.63 | 40.98 | 0.78 | 55.6 | 19.05 | 12.78 | 11.67 | 0.75 |
| 7-1 graves | pathtrace | 46.89 | 40.69 | 0.91 | 50.0 | 18.07 | 10.64 | 12.16 | 0.61 |
| 8-2 street | off | 9.60 | 9.60 | 0 | 16.2 | 1.70 | 3.59 | 3.59 | 0 |
| 8-2 street | hybrid | 11.09 | 10.68 | 0.18 | 15.3 | 5.69 | 7.88 | 7.27 | 0.14 |
| 8-2 street | relight | 10.09 | 10.94 | 0.61 | 13.3 | 5.57 | 8.34 | 8.72 | 0.65 |
| 8-2 street | pathtrace | 13.89 | 10.93 | 0.80 | 16.4 | 6.26 | 8.30 | 8.70 | 0.89 |

(1-4's highlights are 0.7% of the frame, a few lamp glints: its hybrid highlight row is noise.) Live gains against the
reference were 0.95-1.09 (auto exposure as played against the offline renderer's metering).

Reading it: the traced modes' own aliasing (`alias`) is larger than the game's (11-14 against 7-10 on edges, 41 in
7-1), because the traced image has more contrast at silhouettes (shadowed backgrounds, lit edges, the bright 7-1 sky
behind the grass) than the game's flat-lit, fogged raster. Live frames sit well on the anti-aliased side (AA position
0.56-0.91): the TAAU and RR average jittered samples and the denoiser blurs, so a still frame has no hard stair-steps,
and the remaining edge error is softness and noise (flat RMSE 3.8-7.4 against 1.0-1.7 for the game). Hybrid keeps the
game's raster and its stair-steps (AA position 0.11-0.33). In 7-1 the grass is the error: sub-pixel blades in front
of a bright background, a 41-level aliasing problem even for the converged image.

### Motion: frame-to-frame change of the error, and lag

| Pose | Mode | edge: still / slow / fast | hilite: still / slow / fast | flat: still / slow / fast | lag slow / fast | lag share slow / fast |
|---|---|---|---|---|---|---|
| 0-1 metal | off | 0.00 / 8.52 / 7.86 | 0.00 / 3.06 / 4.40 | 0.00 / 1.43 / 1.93 | 0 / 0 | 0 / 0 |
| 0-1 metal | hybrid | 0.92 / 7.24 / 7.68 | 0.63 / 2.47 / 3.67 | 0.59 / 1.61 / 2.46 | 0 / 0 | 0 / 0 |
| 0-1 metal | relight | 1.70 / 8.03 / 8.74 | 0.75 / 3.95 / 6.09 | 0.65 / 2.80 / 4.12 | 0.20 / 0.02 | 13.5% / 3.7% |
| 0-1 metal | pathtrace | 1.60 / 8.04 / 8.88 | 0.86 / 4.25 / 6.33 | 0.75 / 2.92 / 4.05 | 0.19 / 0.01 | 11.8% / 2.4% |
| 1-4 hall | off | 0.00 / 5.94 / 5.35 | 0.00 / 2.02 / 2.45 | 0.01 / 1.24 / 1.72 | 0 / 0 | 0 / 0 |
| 1-4 hall | hybrid | 0.63 / 6.29 / 6.66 | 0.50 / 3.24 / 6.47 | 0.48 / 2.05 / 3.24 | 0.02 / 0.01 | 0 / 0 |
| 1-4 hall | relight | 1.67 / 9.60 / 11.80 | 0.54 / 3.73 / 5.22 | 1.00 / 5.02 / 5.96 | 0.23 / 0.06 | 16.0% / 6.6% |
| 1-4 hall | pathtrace | 2.35 / 10.65 / 12.92 | 0.61 / 4.69 / 5.57 | 1.77 / 5.34 / 6.17 | 0.20 / 0.04 | 9.1% / 2.8% |
| 7-1 graves | off | 0.14 / 6.61 / 6.00 | 0.00 / 0.61 / 0.98 | 0.04 / 1.50 / 1.81 | 0 / 0 | 0 / 0 |
| 7-1 graves | hybrid | 2.58 / 17.18 / 27.84 | 0.23 / 2.21 / 10.14 | 0.39 / 3.24 / 11.55 | 0.17 / 0.06 | 2.8% / 0.4% |
| 7-1 graves | relight | 16.96 / 41.41 / 47.06 | 0.85 / 8.35 / 11.04 | 4.34 / 13.62 / 19.81 | 0.38 / 0.27 | 14.9% / 15.0% |
| 7-1 graves | pathtrace | 3.78 / 32.05 / 40.00 | 0.93 / 8.63 / 9.46 | 0.90 / 12.14 / 18.20 | 0.44 / 0.31 | 23.6% / 21.1% |
| 8-2 street | off | 0.13 / 8.41 / 8.40 | 0.02 / 3.70 / 4.52 | 0.03 / 2.10 / 3.10 | 0 / 0 | 0 / 0 |
| 8-2 street | hybrid | 0.98 / 9.26 / 10.16 | 0.50 / 4.35 / 6.32 | 0.54 / 3.49 / 5.87 | 0.07 / 0.01 | 0.7% / 0.1% |
| 8-2 street | relight | 2.00 / 8.11 / 8.98 | 1.40 / 4.86 / 6.69 | 0.88 / 4.77 / 5.75 | 0.20 / 0.05 | 12.7% / 4.1% |
| 8-2 street | pathtrace | 1.85 / 9.70 / 10.06 | 1.56 / 5.75 / 7.04 | 0.93 / 5.11 / 5.94 | 0.20 / 0.05 | 10.0% / 3.3% |

`still` is the held camera (noise and jitter flicker only: the reference does not change), `slow` 0.5 px/frame,
`fast` 10 px/frame. Negative lags (the game's raster, -0.05 to 0) are shown as 0. The lag share is the part of the
error energy on moving pixels that the previous frame's reference explains (`max(lag, 0)^2 |R_t-1 - R_t|^2 / |e|^2`,
summed over the segment; `tools/aliasing/lag_share.py <run>`).

Reading it: the game's own crawl (`off`, no noise at all) is 5.9-8.5 levels per frame on edges in the slow pan. The
traced modes add their still flicker (1.6-2.4; 17 for relight's grass) and otherwise crawl about as much (0-1, 8-2) or
up to 1.8x more (1-4: the distant lamp posts and window frames). Motion is where the traced modes lose their at-rest
softness: the TAAU and RR histories are cut or lag, and pixel-centre sampling shows through. Lag is real but small on
ordinary geometry: 0.2 at 0.5 px/frame (9-16% of the error), 0.01-0.06 at 10 px/frame (2-7%). On the grass it is
larger (lag 0.3-0.44; 15% of relight's error, 21-24% of pathtrace's). **So most of RR's motion error is not
ghosting**: it is aliasing and the detail a 1/3-scale trace misses, except on sub-pixel foliage, where a fifth is lag.

### Live variants: Toksvig (0-1 metal, 8-2 street)

`mat.toksvig` (the share of the normal-map slope variance a mip filters away that becomes roughness;
[materials.md](materials.md), section 8) at 0, 1 (default) and 3: every static and motion number moved by at most 0.3
levels (0-1 pathtrace highlight RMSE 7.22 / 6.90 / 6.59, highlight slow crawl 4.31 / 4.25 / 4.21; 8-2 relight
highlight RMSE 8.38 / 8.34 / 8.40), the size of run noise. At these distances most primary hits read mip 0 of the
detail maps, where there is no filtered-away variance to convert; the highlight error that remains is the game's
point-sampled texels and the stripes' own geometry, not normal-map sparkle.

### Worst tiles

`aliasing/crops/<pose>_<mode>_<edge|hilite><n>_<x>x<y>.png` (live | AA reference | un-jittered reference | error x4;
not committed: rendered frames). What they show:
- 7-1 relight and pathtrace: the cutout grass. Relight keeps the blades, but they flicker between blade and bright sky;
  pathtrace (RR at 1/3) smears them into a soft brown mass and loses them.
- 0-1 pathtrace highlights: the metal wall stripes. The un-jittered reference has hard stair-steps on every stripe;
  live is softer than even the AA reference.
- 1-4 relight / pathtrace: lamp-post and window-frame silhouettes against the bright stained glass; the RT edges are
  smooth but blurred, and much of the error is reference noise on the bright glass.
- Not aliasing, but found on the way: 8-2 relight loses a thin shadow line on the street that both references show,
  and 8-2 pathtrace draws a bright rim along a pipe's silhouette.

### MSAA probe (`bench/results/20261119-153011_aliasing-msaa_alt`)

`plugin._msaaProbe=4` replaces the game's four world-camera targets with 4x multisampled ones of the same formats
(next section). Same pose and camera as the main run, still frame, scored with the main run's masks against the
game's 16-position AA reference:

| Pose | edge RMSE, no MSAA -> 4x | partial-coverage pixels | texture edges | flat |
|---|---|---|---|---|
| 0-1 metal | 9.84 -> 4.66 | 18.7 -> 8.2 | 10.93 -> 10.73 | 1.15 -> 1.16 |
| 7-1 graves | 6.67 -> 5.74 | 19.8 -> 16.7 | 9.42 -> 9.35 | 1.23 -> 1.26 |

Frame time (`off`, modes alternating every 2 s, CPU-bound at 400+ fps): 7-1 2.53 -> 2.52 ms, 0-1 2.15 -> 2.27 ms; GPU
time was not separable here. The game's image with 4x MSAA renders correctly (the final pass and the outline blits
resolve the colour by themselves), and the texels are untouched. Hybrid with the MSAA targets is broken:
`Graphics.CopyTexture could not find source D3D12 texture object` (282 times in the alt `Player.log`) when our
`BindGameDepth` copies the multisampled depth, so the lighting ratio is computed against no depth and the frame blows
out.

## What would help (non-temporal first)

The user wants no temporal AA: ULTRAKILL is fast, and smear is worse than stair-steps. The traced modes are already
temporal reconstructions (TAAU in relight, RR in pathtrace); that is where their at-rest softness comes from, and the
motion numbers above are what is left when history fails. The options below add anti-aliasing that holds in the first
frame of a motion.

### MSAA on the game's raster (helps `off` and `hybrid` only)

Feasibility, from the decompiled `PostProcessV2_Handler.SetupRTs` and the probe:
- The game renders its world camera into an MRT it creates itself: `mainTex` (ARGB32), `reusableBufferA` (RG16, the
  outline mask), `viewNormal` (RGB565) and a 32-bit `depthBuffer`, all with `antiAliasing = 1`, bound with
  `SetTargetBuffers`; the HUD camera shares `mainTex` and the depth. `QualitySettings.antiAliasing` is 0 and
  `allowMSAA` is off on both cameras, but neither matters: the targets decide. They are recreated only on a size or
  pref change (`reinitializeTextures`), so replacing them once holds.
- What resolves by itself: colour targets sampled as textures (the final `PostProcessV2` pass reads `mainTex`; the
  outline blits read `reusableBufferA`).
- What breaks: everything that reads the **depth** as a texture or copies a target. The game: blood and oil stains
  (`bloodOilCB` at BeforeForwardAlpha samples `_DepthBuffer` and `_WorldNormal`), heat waves
  (`CopyTexture(mainTex, reusableBufferB)` and the stencil). UltraRT: `BindGameDepth`'s `CopyTexture` (composite,
  TAAU, volumetrics, soft particles), the viewmodel pass, wet blood, the transparents' refraction copy and auto
  exposure's game-match copy. Each needs a resolve (depth: a `Texture2DMS` load of the nearest sample into a
  single-sample copy, ~0.05 ms at 1440p, estimate), and the game's `_DepthBuffer` global pointed at the resolved copy
  after `bloodOilCB` sets it (a Harmony postfix on its setup).
- **The fringe.** Hybrid multiplies the resolved colour (a coverage blend of two surfaces) by one lighting ratio from
  the pixel-centre depth. Where the two surfaces are lit differently (a lit edge over a shadowed wall) the partial
  pixel gets the wrong ratio for its minority surface: a 1-pixel halo or dark rim on 1.5-5.8% of the pixels. Fix: two
  ratios per partial pixel (from the nearest and farthest depth sample, weighted by their coverage in the MSAA depth);
  hybrid's depth-matched 2x2 ratio pick already has both candidates at hand.
- Not for relight / pathtrace: their colour comes from our trace, not this raster. Not for cutouts: the game's
  alpha-tested grass and foliage (`clip`) are not anti-aliased by MSAA without alpha-to-coverage, which means editing
  or replacing the game's cutout shaders (7-1: 6.7 -> 5.7 only).
- Cost: within the noise on `off` (CPU-bound). Memory: 4x the colour, outline, normal and depth targets (~90 MB more at
  2560x1440).

### Edge-adaptive supersampling in our trace (relight, pathtrace)

Find geometric-edge pixels (instance, normal or depth discontinuity in the trace's G-buffer at screen resolution,
which the TAAU and remodulation already reconstruct) and trace extra primary samples there, box-filtered before the
composite. Share and cost at 1440p (3.69 M pixels):
- Edge pixels 2.8-7.1% = 103 k-262 k; partial-coverage pixels 1.5-5.8% = 55 k-214 k.
- Visibility + texel only (a primary ray and the hit's albedo / emission per sample; the lighting taken from the
  pixel's reconstructed lighting of the same surface, or the neighbour's across the edge for the other surface): 4
  samples per edge pixel = 0.4-1.05 M coherent primary rays. At the relight dispatch's ~0.5 ns per ray (2.4-2.9 ms for
  ~5.2 rays per trace pixel at 1382x720, [performance.md](performance.md) "Ray counts") that is ~0.2-0.55 ms.
- Full lighting per sample (primary + shadow + bounce + reflection, ~5 rays): 2-5 M rays, ~1-2.7 ms. Not worth it:
  the silhouette error is visibility and texel, not lighting.
- It is the only option here that addresses the 7-1 case (sub-pixel grass) in relight without history. Under RR it
  does not plug in directly (RR wants one sample per trace pixel on its jitter grid): for pathtrace either pre-filter
  the edge pixels' colour before RR, so RR sees anti-aliased input, or raise the RR trace scale where thin geometry
  matters (scale 1/2: 2.25x the trace pixels; the trailer ran at 1).

### Post AA after our composite (any mode)

SMAA 1x or FXAA after the composite (after RR in pathtrace). Non-temporal and cheap (SMAA 1x ~0.3-0.5 ms, FXAA
~0.1-0.15 ms at 1440p: literature figures, not measured here). Two limits:
- **It softens the pixel art** if it finds edges in luma: texel edges are 2.0-4.1% of the pixels against 2.8-7.1% for
  geometric edges, so a luma-edge AA smooths about as many texel edges as silhouettes. Predicate the edge search on
  geometry instead (SMAA's depth / predicated edge detection, fed with the game's depth or our instance buffer), and
  the texels stay crisp.
- It cannot reconstruct sub-pixel geometry: it smooths stair-steps on still and slow frames but not the crawl of thin
  features (wires, grass); that needs coverage (MSAA, supersampling).

### Specular AA (no temporal)

Specular aliasing is not the problem at these poses: highlight error 4-13 levels, crawl 3.7-5.8 per frame in a slow
pan, and Toksvig (already in, for the PBRify detail maps at their ray-cone mip) at 0 / 1 / 3 changes nothing
measurable. What is left at highlights is the game's point-sampled texels and the geometry of striped metal panels.
If a sparkle case shows up (distant curved metal, grazing wet floors), the cheap fix is geometric specular AA at the
primary hit (Kaplanyan / Tokuyoshi: roughness widened by the screen-space variance of the shading normal,
`alpha^2 += k * (|dN/dx|^2 + |dN/dy|^2)`, from the trace's normal G-buffer): a few ALU, ~0.02 ms, non-temporal.

### DLSS RR (pathtrace)

RR is temporal by construction; with the exact 3x3 jitter grid it reconstructs pixel centres, not a supersampled
pixel. Measured: lag is 9-12% of its motion error at 0.5 px/frame and 2-3% at 10 px/frame on ordinary geometry, 21-24%
on 7-1's grass; the rest is aliasing and the detail missing from the 1/3 trace. Non-temporal additions that still
apply under RR: geometry-predicated post AA after RR, and edge supersampling as a pre-filter of RR's input. The lever
with the most effect on thin geometry is RR's trace scale, which is a cost question, not an AA one.

## Traps

- **The offline renderer drops the game's fog in relight / pathtrace accumulations**: `TraceVolumetrics` applies it
  only on the upsampled path (`applyGameFog = upsampled && kind != Kind.Hybrid`, `Plugin.Volumetrics.cs`), and an
  accumulating render turns the upsampler off. Live takes have the fog. Every traced take of the aliasing suite runs
  with `vol.enabled=0` so that live and reference match. Cinematic renders of foggy levels (7-1) in `pathtrace` /
  `relight` accumulation lack the fog today; `live` renders (what the trailer uses) are fine.
- A scenario with `Takes` also renders the Cinematic block's own take unless `"Render": false`.
- The `depth` debug view is 8-bit (`1 - t / scale`): near the camera a one-level step is a large relative jump. The
  edge mask only counts steps of 4+ levels that are also 15%+ of the distance; instance and normals carry most of it.
- The debug views' miss colour (magenta gradient) is treated as sky; otherwise it reads as a specular surface.
- 128-sample references still carry noise in bright areas (1-4's stained glass, 7-1's sky): it adds to every
  live-vs-reference number and sets the floor of the flat RMSE.
- A run takes ~25 minutes for four poses on the alt install (pathtrace references ~2.4-3.5 s per frame at 128
  samples); `analyze.py` takes ~15 minutes (numpy on 1440p PNGs).
