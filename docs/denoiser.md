# Denoiser

A lightweight version of SVGF for the 1 spp path-traced lighting. It filters demodulated lighting only. Albedo and emission are applied at the end and are never blurred, so point-sampled texel edges stay sharp.

Files: `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/Denoise.compute` (in the `ultrart.shaders` bundle) and `src/UltraRT/Denoiser.cs`.

## Passes

All passes use 8x8 thread groups and run at trace resolution, except Composite, which runs at output resolution. In the replace modes (pt-dn, relight, dn-*) the output then goes through the [temporal upsampler](#temporal-upsampling) to screen resolution.

| # | Kernel | Reads | Writes |
|---|---|---|---|
| 1 | `Temporal` | radiance, normalDepth, instanceId, prevGeo, hist, prevMoments, radiance cache (optional) | t0 (lighting, variance), moments[cur], geo[cur], boost (debug) |
| 2 | `Atrous3` or `Atrous5` ×N | previous iteration's output, geo[cur] | iteration 0 writes **hist** (the history fed back next frame). Later iterations alternate t0 and t1. |
| 3 | `Stabilize` (`stabilize`) | last filtered output, moments[cur], reproj, stable[prev] | stable[cur] (not fed back into hist). See [Static noise](#static-noise). |
| 4 | `Composite` | stabilised (or last filtered) output, albedo, emission, radiance, moments[cur], boost | output |

**Temporal**
- Loads a 16x16 tile (the 8x8 group plus a 4-pixel apron) into groupshared memory: radiance, ray t, world position and normal. The 3x3 statistics and the pre-blur read from it.
- Rebuilds each pixel's world position from the primary ray (`camPos + dir * t`), using the same formula as the ray generation shader.
- Moves that position to where the surface was last frame, `p' = p + motion` (see [Motion vectors](#motion-vectors); 0 for static geometry), projects `p'` with the previous frame's viewProj, then does a 2x2 bilinear fetch. Each tap is validated separately: the valid bit is set (not sky), the low 7 bits of the instance id match, `dot(n, nPrev) ≥ reprojNormalTolerance`, and `|dot(pPrev − p', n)| ≤ reprojPlaneTolerance · t + 0.01`. The weights of the valid taps are renormalised.
- If no tap is valid, a 3x3 search around the reprojected point takes the best texel that passes the same tests (`historySearch`). Only if that also fails does the history length go back to 0. See [Temporal stability](#temporal-stability).
- Blends with an exponential moving average, `alpha = max(1 / len, alphaMin)` where `len = min(histLen + 1, maxHistory)`. Both the lighting and the luminance moments (m1, m2) are accumulated this way. alphaMin is rescaled to the frame time (`referenceHz`, see [Static noise](#static-noise)).
- Variance is `m2 − m1²`. Below 4 frames of history it is replaced by the luminance variance of the 3x3 neighbourhood. The 3x3 is cached in groupshared memory and only includes pixels whose depth is within 10%. It never drops below `spatialVarianceFloor` × that 3x3 variance.
- Writes the history footprint it used (`reproj`: base texel and the 4 validated bilinear weights, or the 3x3 search's texel) for `Stabilize`.
- Long-history pixels get a firefly clamp, and short-history pixels accumulate a pre-blurred (and optionally cache-seeded) input instead of their own sample. See [Temporal stability](#temporal-stability).
- Optional history clip (`clampHistory`): clamps the history to the mean of the noisy 3x3 neighbourhood ± `clampSigma · max(spatial σ, history σ)`. Including the history's own temporal σ in that floor means a sparse bright bounce, where all 9 neighbours miss it in a frame, does not get clamped away and flicker. A light switching off still pulls the history down within a few frames.
- Writes this frame's packed geometry for the next frame: `int4(asint(worldPos), valid | inst&0x7F | oct12 normal)`. Storing world position directly means reprojection needs no inverse of the previous matrix, and plane tests are exact in float32.

**À-trous**
- Steps are 1, 2, 4, …
- Weights:
  - B-spline kernel weight
  - normal: `pow(max(0, dot), phiNormal)`
  - plane distance: `exp(−|dot(pq − p, n)| / (phiDepth · pixelFootprint(t) · |offset| + 0.001 t))`. The 0.001 t term absorbs half-precision depth noise.
  - luminance: `exp(−|Δl| / (phiLuminance · sqrt(g(var)) + 1e-4))`. g is a 3x3 Gaussian prefilter of the variance, applied on iteration 0 only. Later iterations already receive smoothed variance.
- Variance is filtered with squared weights, as in SVGF.
- The output of iteration 0 is the next frame's history, also as in SVGF.

**Why 3x3 is the default.** Direct lighting is deterministic: `DirectAll` shoots a shadow ray to every light, so it has no noise. Only the single diffuse bounce is noisy, and temporal accumulation removes most of that. A 3x3 kernel costs 9 taps per iteration against 25 for 5x5, which fits the budget. It also keeps lighting slightly blocky, which suits the art style better than a wide blur. Set `wideKernel = true` for the 5x5 B3 kernel when bounce light is very noisy, for example in dim rooms lit mainly by emissives.

## Temporal stability

ULTRAKILL's camera moves very fast (slides, dashes). A newly revealed pixel has no history, so plain SVGF shows its 1 spp lighting, filtered only by the à-trous passes, for the first several frames. That shows up as speckle, worst along the leading screen edge while sliding. Four features in the `Temporal` kernel address it. `stability = false` turns all four off and reproduces plain SVGF exactly (the `Legacy` preset, `-ultrart-dn-legacy`, Ctrl+F3). The [guard band](#guard-band) removes most of the leading-edge case.

**1. History-aware pre-blur** (`boostFrames` 8, `boostRadius` 4). This is the "pre-pass" of ReBLUR and A-SVGF.
- For a pixel with `histLen < boostFrames`, the value accumulated this frame is a geometry-aware Gaussian of the noisy input around it, instead of its own sample: `color = lerp(history, preBlur, 1 / len)`.
- Strength is `boost = 1 − histLen / boostFrames`. The radius is `boostRadius · boost` pixels at trace resolution (σ = R / 2), so it is 4 px on the first frame and fades out linearly by frame 8.
- The weights are the à-trous geometry weights: plane distance (`phiDepth`, pixel footprint) and normal (`phiNormal`). There is no luminance weight, which carries no information at 1 spp. Lighting therefore never bleeds across edges, creases or depth discontinuities, and texel-sharp albedo is untouched as always.
- Above radius 2 the window is sampled on a 2-pixel lattice whose phase alternates per pixel. That gives at most 26 taps; each 2x2 quad covers every texel, and the first à-trous step merges them.
- The result feeds the history (iteration 0 → `hist`), so the next frame continues from a smooth estimate. The effective sample count grows by about 25–50 per frame instead of 1.
- Outliers are not clamped here. A lone bright sample is spread at its correct energy into a soft, low blob instead of being thrown away.
- Moments still accumulate the raw per-sample luminance, so the variance (and the à-trous luminance stopping) keeps describing the input noise. Below 4 frames it is the raw 3x3 spatial variance, which also loosens the à-trous luminance weights where the history is short.

**2. 3x3 history search** (`historySearch`). When all four bilinear taps fail (silhouettes, thin geometry, sub-pixel slivers between objects), the best matching texel of the 3x3 around the reprojected point is used. It must pass the same instance, normal and plane tests. The score is `planeDistance / tolerance + 0.5 · texelDistance²`, and the texel keeps its full history length. Screen-edge disocclusions get nothing from this (there is no history), so the pre-blur and the cache seed handle those.

**3. Radiance cache seed** (`cacheSeed` 1, `cacheSeedTolerance` 0.5). Needs the radiance cache (docs/radiance-cache.md).
- The cache is world space, so it has no disocclusions, and a cell near the camera averages dozens of paths per frame. For short-history pixels, Temporal looks up the cell under the pixel (`UltraRTCache.hlsl`, read-only SRVs on the compute shader; PathTrace's UAV budget is untouched). It blends the cell in as `cacheSeed · boost` virtual samples: `in = (preBlur + w · cache) / (1 + w)`.
- The query point is jittered within the cell's tangent plane, as in PathTrace's primary mode, so cell edges become noise rather than blocks.
- **Consistency test**: the seed is used only where the cell agrees with the pre-blurred estimate, `z = |L(preBlur) − L(cache)| / (σ(preBlur mean) + cacheSeedTolerance · L(cache))`. Trust is 1 up to z = 1 and 0 at z ≥ 3. This rejects fullbright surfaces (the input is 1, while the cache holds lighting), terms the cache doesn't have, and cell-sized leaks, so the worst case is the pre-blur alone.
- **Channel** (set per frame by `Plugin.Cache.cs`, `ConfigureDenoiserCacheSeed`):

  | Mode | Denoiser input | Seed |
  |---|---|---|
  | hybrid, main | bounce only (PathTrace `_Mode` 3) | cache `indirect` + 0.5 · ambient |
  | pt-dn, relight, dn-* | full lighting (`_Mode` 0) | cache `total` + primary ambient + 0.5 · ambient |
  | hybrid, direct | shadowed/unshadowed luminance pair | none |

  "ambient" is PathTrace's `_Ambient · RelightAmbientScale()`. The 0.5 · ambient term stands in for the bounce-hit ambient (`albedo · ambient`), which the cache leaves out, using a typical albedo of 0.5. The seed is off when the cache is off or its mode is `Off`.

**4. Anti-firefly** (`fireflySigma` 8). This applies only to pixels with ≥ 4 frames of history, where the reference is trustworthy.
- `ref = max(history m1, mean of the 8 neighbours)` and `spread = max(history σ, σ of the 8 neighbours)`. The sample's luminance is limited to `max(2 · ref, ref + fireflySigma · spread)`, and the hue is kept.
- Muzzle flashes and lights switching on brighten the neighbours too, so they pass. A lone 50x sample on a converged surface no longer flashes a blotch for about 10 frames.
- Short-history pixels are left alone (at 1 spp any clamp would eat legitimate sparse samples). The pre-blur spreads their outliers instead.
- **Bias**: on a converged surface whose bright samples are rarer than about 1 in 30 frames, those samples are partly clamped, which darkens it slightly. Use `-ultrart-dn-firefly 0` to compare.

**Debug view `dn-boost`** (`DenoiserDebugView.SpatialBoost`, at the end of the Page Up/Page Down list), drawn over a dim grey image: **red** is pre-blur strength, **green** the cache seed weight, **blue** history recovered by the 3x3 search, and **white** a firefly clamped by more than 10%. While sliding, expect a red band along the leading edge that fades within 8 frames, green over most of it (indirect light near the camera), and blue specks along silhouettes. `dn-history` still shows the history length.

## Static noise

A still image in relight (and less so in hybrid) used to boil: the lighting kept changing on surfaces that should have converged. Measured with bench noise bursts (bench/README.md, "Temporal noise bursts"): the per-pixel σ of screen luma (0–255) over 64 frames, over the pixels the game itself keeps still, plus the σ of 8-frame means ("block"), which is what still shows at 360 Hz.

**Where it came from** (1-4 drift pose, relight, the build before this work; `bench/suites/noise-diag.json`):

| Variant | σ | block σ |
|---|---|---|
| relight | 0.66 | 0.60 |
| ReSTIR off (`lights=ref`: every light, one ray each) | **0.16** | 0.12 |
| radiance cache off / cache history 128 | 0.65 / 0.64 | 0.59 / 0.59 |
| reflections off / emissive lights off / soft-shadow radius 0 | 0.65 / 0.63 / 0.66 | ≈ 0.59 |
| stability features off | 0.65 | 0.59 |
| denoiser alphaMin 0.03, 100 frames | 0.35 | 0.31 |
| raw `pt-direct` / `pt-indirect` (no denoiser) | 8.2 / 2.5 | **7.0** / 0.85 |

The bounce (radiance cache) and reflections contribute very little; nearly all of it is ReSTIR's direct light. A pixel keeps its reservoir sample for up to `historyLimit` × `candidates` (160) samples of M, so its error stays almost constant for dozens of frames (raw direct: block σ 7.0 of 8.2), while neighbouring pixels pick independently. That has two consequences. First, the temporal accumulation (about 10 frames at alphaMin 0.1, 28 ms at 360 Hz) can't average it out. Second, the temporal moments barely see it, so the à-trous luminance weights treat it as detail and keep it. Shortening the reservoir history (`historyLimit` 4) made it worse: there is more noise per frame. 1-1's sunlit start area shows the same pattern (0.73; 0.58 with ReSTIR off: there the sun's shadow edges and the cache add a little).

**What changed** (the `Full` preset, i.e. the defaults):

1. **Longer, frame-rate-independent history**: `alphaMin` 0.1 → 0.04 at `referenceHz` 360, rescaled to the actual frame time as `1 − (1 − α)^(360·dt)` (0.22 at 60 fps: the same 70 ms time constant). `maxHistory` 32 → 64. The history clip and the firefly clamp still catch real changes.
2. **Spatial variance floor** (`spatialVarianceFloor` 2): Temporal's variance is at least 2 × the raw input's 3x3 spatial variance, so the à-trous passes blur ReSTIR's pixel-independent noise instead of preserving it.
3. **Output stabilisation** (`Stabilize`, `stabilize`): a second, light temporal filter on the *filtered* lighting. It is not fed back into the SVGF history.
   - It reuses Temporal's reprojection footprint, so there is no second validation.
   - It keeps a moving average of luma² next to the colour, and clamps its history to the current filtered value ± `stabilizeSigma` (2) × that history's own temporal σ + `stabilizeTolerance` (2%) × luma.
   - Noise-sized wobbles average out over about 1/`stabilizeAlphaMin` (20) frames. A real lighting change lies far outside the converged spread and pulls the history along within a frame or two.
   - While the history is younger than 4 frames, the à-trous variance (an upper bound) sets the box.
4. **ReSTIR spatial reuse in hybrid** (`LightSamplingSettings.spatialSamplesHybrid` 2, radius 6 px): two reservoirs from the previous frame near the reprojected pixel (docs/light-sampling.md). It cuts hybrid's remaining direct-light boiling by about a third. In relight it made 1-1's sun shadow edges boil more (0.38 → 0.46), so relight and pt-dn keep `spatialSamples` 0.
5. **[Guard band](#guard-band)** for the screen edge in motion.

**Results** (`bench/suites/noise-final.json`, masked σ / block σ, Previous preset → Full; RTX 5070 Ti, 1920x1080, trace scale 0.5):

| Pose | relight σ | relight block σ | hybrid σ | hybrid block σ |
|---|---|---|---|---|
| 0-1 metal | 1.01 → 0.93 | 0.68 → 0.60 | 0.50 → 0.47 | 0.33 → 0.31 |
| 0-3 ghosts | 0.94 → 0.69 | 0.72 → 0.48 | 0.60 → 0.53 | 0.42 → 0.36 |
| 1-4 lamps | 0.45 → 0.31 | 0.40 → 0.27 | 0.34 → 0.28 | 0.29 → 0.23 |
| 1-4 drift | 0.66 → 0.32 | 0.60 → 0.28 | 0.32 → 0.18 | 0.26 → 0.15 |
| 1-1 user a | 1.22 → 0.79 | 0.77 → 0.43 | 0.96 → 0.70 | 0.53 → 0.33 |
| 1-1 user b | 1.00 → 0.54 | 0.72 → 0.36 | 0.66 → 0.46 | 0.42 → 0.27 |
| 1-1 user c | 0.88 → 0.44 | 0.66 → 0.32 | 0.54 → 0.37 | 0.32 → 0.21 |
| 1-1 user d | 0.79 → 0.35 | 0.66 → 0.28 | 0.45 → 0.20 | 0.34 → 0.12 |
| 8-3 b | 1.03 → 0.38 | 0.90 → 0.34 | 0.64 → 0.30 | 0.55 → 0.27 |
| 1-1 start | 0.74 → 0.45 | 0.49 → 0.25 | 0.46 → 0.32 | 0.24 → 0.15 |
| 1-1 torch hall | 1.15 → 0.45 | 1.00 → 0.40 | 0.29 → 0.10 | 0.22 → 0.08 |
| 4-1 indoor A | 0.43 → 0.17 | 0.35 → 0.15 | 0.11 → 0.04 | 0.07 → 0.03 |

The block σ (what survives the eye's own averaging at 360 Hz) drops by 33–62% in relight and by 14–65% in hybrid (half or more on most poses). 0-1 metal barely moves: the game animates that scene itself (σ 4.4 over the whole frame in `off`, flickering lights), and the RT lighting follows it.

**Motion**: on the 1-1 sweep (180°/s), the guard band removes the speckled band along the leading edge (`dn-lighting` frames). The 0-2 run with active enemies shows no new ghosting.

**Cost** (0-2 run, GPU total / dispatch / denoise in ms, Previous → Full): relight 1.98 / 1.28 / 0.20 → 2.11 / 1.34 / 0.22 (+0.12), hybrid 2.14 / 1.23 / 0.38 → 2.25 / 1.24 / 0.43 (+0.11). Most of it is the guard band (8% more rays and denoised pixels); the denoiser itself grows by 0.02 ms per instance (Stabilize plus the extra pixels).

## Guard band

Plugin.cs traces `_guardX` (4%) more than the screen on each side (`-ultrart-guard X [Y]`; Y is 0 by default), at the same pixel density.
- The RT resolution grows by 2 × 4%, and the projection is scaled by the traced/visible ratio.
- `Composite.shader` maps screen uv to trace uv (`_UltraRTUVScale`, with `_UltraRTDepthParams` scaled to match).
- Every other consumer of the trace (denoiser, ReSTIR, reflections, the radiance cache update) works in trace space and needs no change.

Surfaces that turn into view therefore already have denoiser and ReSTIR history and warm cache cells. At 180°/s (0.5°/frame at 360 Hz, about 4 trace px/frame) the 38 px band holds about 9 frames of history. That removes the speckled band along the leading edge while turning (compare the `1-1_sweep_burst_dn-lighting_*_frame.png` images). It costs about 8% more rays and denoised pixels. Vertical turns are rarer, so Y is off. The Previous and Legacy presets turn the band off.

## Presets and Ctrl+F3

Ctrl+F3 cycles `DenoiserPreset` on both denoiser instances:
- **Full**: everything above.
- **Previous**: the build before this work. alphaMin 0.1 per frame, 32 frames, no variance floor, no stabilisation, no guard band, no hybrid spatial reuse; the four stability features stay on.
- **Legacy**: Previous without the stability features, i.e. plain SVGF.

`-ultrart-dn-previous` and `-ultrart-dn-legacy` start in those presets. Individual flags: `-ultrart-dn-alpha A`, `-ultrart-dn-spatialvar K`, `-ultrart-dn-nostabilize`, `-ultrart-dn-stabilize-alpha A`, `-ultrart-dn-stabilize-sigma K`, `-ultrart-guard X [Y]`, `-ultrart-restir-spatial N`, `-ultrart-restir-spatial-hybrid N`.

**Not the denoiser**: in 1-1's start area, the tree canopies and grass are the game's own animated foliage, drawn over the RT image. The thin wires against the sky used to flicker because of the half-resolution depth-guided upsample; the [temporal upsampler](#temporal-upsampling) fixes that in the replace modes.

## Temporal upsampling

The trace runs at `-ultrart-scale` (0.5) of the screen. The old composite picked, per screen pixel, the one of the 2x2 nearest trace texels whose depth matched the game's. That gave stair-stepped silhouettes, blocky textures and lighting (2x2 screen pixels per texel), and thin geometry (wires, railings) that switched between RT and the game's own shading. In the replace modes (pt-dn, relight, dn-\*) a TAAU pass now rebuilds a screen-resolution image: `Upsample.compute`, `src/UltraRT/TemporalUpsampler.cs`. Hybrid is unchanged: it keeps the game's full-resolution colour and applies only RT ratios.

**Where it applies.** It works on the final RT colour (the denoiser's composite output), not on demodulated lighting. In relight and pt-dn the textures come from the RT hits too: no full-resolution albedo exists to remodulate with. Jittered exact-hit samples rebuild the textures at screen resolution as well.

**Jitter.** Each frame the trace grid is offset by a sub-pixel jitter. The projection is `T(-2j / traceSize) · viewProj`, so pixel p's primary ray goes through trace position p + 0.5 + j. When the screen is exactly twice the visible trace size, j cycles through (±0.25, ±0.25) trace px: every screen pixel centre is traced exactly once per 4 frames. Otherwise j follows an 8-phase Halton(2, 3) sequence. Every temporal consumer (this denoiser, ReSTIR, reflections, the radiance cache) reprojects world positions with the same jittered matrices, so their histories stay consistent with no other change.

**Per screen pixel** (one 8x8 group loads the 11x11 trace samples it needs into groupshared memory):
1. **Geometry from the game.** The copied game depth buffer gives the eye depth and the exact world position. A trace sample belongs to this pixel's surface when its eye depth matches within 3% + 5 cm, the old composite's discard test. Sky, and anything the game draws that we don't trace, matches nothing, and the game's pixel is kept (output a = 0, the composite discards).
2. **New samples.** Of the 3x3 matching samples around the nearest one, each counts with weight exp(−4 d²), where d is its distance to the pixel centre in screen px. An exact hit counts 1, a neighbour one pixel away 0.02, so edges and texels stay pixel sharp. A depth-matched Gaussian of the same samples (σ 0.6 trace px, weight 0.05) is the **edge-aware fallback** where there is no history.
3. **History.** The world position is moved by the matching sample's object motion (`_InstanceId.y`) and projected with last frame's screen viewProj. The 4 bilinear taps of the screen-resolution history are validated against their stored eye depth (2%). With all 4 valid, a 5-tap Catmull-Rom resamples the history, clamped to the 4 taps' range. The history is clipped to the matching samples' min/max (+10%), but only when at least 3 samples match: an edge pixel with 1 or 2 would lose its history every frame. The weight is capped at 3 exact hits (`maxWeight`).
4. **Thin geometry.** If no sample matches this frame but the history weight is ≥ 0.1, the history is kept, decaying ×0.95 per frame. A wire the jittered grid missed this frame stays RT-shaded instead of falling back to the game's pixel. The threshold matters: in 1-1 the game's sky dome sits at nearly the depth of the arch, and without it, tiny bilinear weights let the arch creep 25 px into the sky over a few seconds.

**A/B**: F2 toggles it (off = the old 2x2 path, no jitter). `-ultrart-notaau`, `-ultrart-taau-nojitter`, `-ultrart-taau-weight N`, `-ultrart-taau-sharpness K`. In benchmark modes, `taau=0|1` and `up.<field>=value` (`UpsamplerSettings`). `up.debugView=1` shows r = matching samples, g = history weight, b = held without a sample (debug only: it breaks the history).

**Results** (`bench/suites/upsample.json`; RTX 5070 Ti, 1920x1080, scale 0.5; masked σ / block σ, `taau=0` → on):

| Pose | relight | pt-dn |
|---|---|---|
| 1-1 spawn (terminal) | 0.49 / 0.34 → 0.28 / 0.20 | 0.49 / 0.38 → 0.25 / 0.17 |
| 1-1 start | 0.51 / 0.27 → 0.38 / 0.20 | 0.46 / 0.26 → 0.54 / 0.25 |
| 1-1 user a | 0.70 / 0.39 → 0.71 / 0.39 | 0.87 / 0.45 → 0.72 / 0.39 |
| cerberus | 0.29 / 0.17 → 0.28 / 0.18 | 0.40 / 0.26 → 0.39 / 0.23 |
| 8-3 b | 0.36 / 0.33 → 0.37 / 0.33 | 1.25 / 1.22 → 0.48 / 0.44 |
| 1-1 sweep (180°/s) | 0.09 / 0.04 → 0.10 / 0.05 | |

Identical modes vary by about ±0.1 σ between bursts (butterflies, birds), so equal rows are equal. Visually: the shop terminal's STORE sign, keyboard and side panel resolve like the game's own frame; silhouettes follow the game's depth per pixel, with neither the old comb-like steps nor dark rims (cerberus, statues in pt-dn); wires against the sky are continuous RT. In motion the textures soften toward the old half-resolution look and sharpen again within about 4 frames of slowing down; no ghosting was seen on the sweep or the 0-2 run.

**Cost**: the pass is 0.09–0.12 ms at 1080p, and the composite gets about 0.012 ms cheaper. Memory: RGBA16F colour + R32F depth at screen resolution, ×2 ping-pong (50 MB at 1080p). GPU total vs master in merge-regress: relight +0.09 to +0.13 ms, hybrid unchanged.

**Trace scale**: 0.67 with the upsampler costs 50–60% more GPU (1-1 start 1.27 → 1.85 ms, 8-3 b 1.67 → 2.55 ms, 0-2 run 2.18 → 3.37 ms). A static view looks the same as 0.5, slightly softer even: the Halton jitter never lands exactly on pixel centres. Noise is no better. **Keep 0.5**, the only scale whose jitter hits every pixel centre exactly. The upsampler works at any scale.

**Limits.** Thin features and silhouettes inherit more denoiser noise than before. Their trace texels alternate between surfaces as the grid jitters, so the denoiser's history there is short, and the cap of 3 exact hits only partly averages it. A pixel held without samples doesn't follow lighting changes until a sample lands on it again (≤ 4 frames static). The game's depth is assumed to be the main camera's, covering the same view as the composite target.

## Motion vectors

Every temporal consumer reprojects a moving surface point from where it was last frame: this denoiser (both instances), ReSTIR's temporal reuse (`UltraRTLights.hlsl`, `g_PrimaryMotion`) and the reflection history (`Reflections.compute`, surface and virtual point). The disocclusion tests (plane, normal, instance) compare the history against `p + motion`, so an enemy keeps its history while it walks and the wall it uncovers is rejected.

**What is stored.** PathTrace's primary ray asks the hit shader for motion (payload `flags` bit 30 in, packed motion out in the payload's `motion` dword; other rays and DebugView get 0). To make room within the 64-byte payload, emission travels as 3 halves (`emissionHalf`, `PayloadEmission()`), the precision every consumer stores it at anyway; `uvHalf` / `primitive` stay free for reflections' game-cubemap term on hybrid primary rays. The hit shader returns `prevWorld − curWorld` of the hit point, packed into one 32-bit word with a shared exponent (`PackMotion`: 3 × sign + 8-bit magnitude, 5-bit exponent; error ≤ max|d|/255, 0 = static). PathTrace writes it to `_InstanceId.y`: the instance texture is now `RGInt` (x unchanged). No 9th UAV, the payload stays 64 bytes, and readers that declare `Texture2D<int>` still see the instance in x.

**Where the hit shader gets the previous position** (`src/UltraRT/MotionVectors.cs`, owned by `SceneProxies`):
- Each dynamic proxy gets a **motion slot**, put in its RTAS custom id (`layer | slot << 8`, 16-bit slot; static batches are slot 0 and skip everything). Per slot, the global `_UltraRTMotion` holds the world-space motion matrix `W = M_prev · M_cur⁻¹` and a skinned-pose offset. Rigid proxies use their `localToWorldMatrix` (exactly the instance transform; updated only when `hasChanged`, reset to identity the frame after they stop). Skinned proxies use the root bone's position and rotation, which is also their RTAS instance transform (kept current with `UpdateInstanceTransform`, see docs/scene-proxies.md, "Skinned meshes").
- **Skinned meshes**: after each trace, `MotionCopy.compute` copies every active skinned proxy's object-space positions out of its skinned vertex buffer (`vertexBufferTarget |= Raw`, `GetVertexBuffer()`) into one shared buffer `_UltraRTPrevSkin`. Next frame the hit shader interpolates the previous pose with the same triangle indices and barycentrics: `prevWorld = W · O2W_cur · prevPose`. The current position is always the BLAS hit point (`WorldRayOrigin + t · dir`), never the vertex fetch, which does not necessarily return the skinned pose. `-ultrart-motion-probe` confirmed Unity's skinned buffer is in root-bone space without scale (the instance matrix), so W of the root's position and rotation is exact. Regions are recycled per vertex count when a proxy leaves the RTAS.
- Why a copy and not Unity's own previous buffer (`GetPreviousVertexBuffer`) or a second vertex stream: the previous buffer would have to be bound per instance through a property block that changes every frame (it is double-buffered), which hit groups would have to re-read; a stream would need the mesh layout changed. The copy is one tiny dispatch per active skinned mesh (12 bytes per vertex), the binding is one global, and the hit shader only reads it on primary hits of skinned instances.
- A proxy entering the RTAS has no motion for its first frame; a skinned one whose pose wasn't copied yet (first frame, buffer regrow) gets root motion only.

**Cost** (RTX 5070 Ti, 1920x1080, trace scale 0.5, `bench/suites/motion.json` = the 0-2 run with active enemies, vs master c5e4d7c):

| | master | motion vectors |
|---|---|---|
| pt-dn frame / GPU total / dispatch / denoise (ms) | 4.090 / 1.882 / 1.230 / 0.193 | 4.197 / 1.977 / 1.281 / 0.194 |
| hybrid frame / GPU total / dispatch / denoise (ms) | 4.112 / 2.065 / 1.189 / 0.386 | 4.234 / 2.170 / 1.249 / 0.388 |
| 1-1 static pose, pt-dn dispatch (ms) | 0.160 | 0.165 |

About +0.05 ms in DispatchRays (motion on primary hits of dynamic instances, the 8-byte instance texture), about +0.04 ms for the pose copies (25-35 skinned meshes, 36k vertices), and about 0.1 ms of CPU (`motion CPU` in the 600-frame scene-sync log line; mostly the per-skinned-mesh `GetVertexBuffer` and copy dispatch). Static geometry (slot 0) skips the motion code entirely.

**Debug view `motion`** (`DenoiserDebugView.Motion`, last in the Page Up/Page Down list): object motion as a flow colour over a dim grey image (hue = screen direction, saturation = speed, full at 8 trace px/frame; slow objects are grey-white); camera-only motion is a faint tint. A spinning object shows a hue wheel; an object whose previous pose is wrong shows a field converging on one point (that was the symptom of a broken pose copy, with `dn-history` dropping to 0 on every enemy). `-ultrart-nomotion` turns motion off (all slots identity, no pose copies) for A/B; `-ultrart-motion-probe` logs which space Unity's skinned vertex buffer is in.

**Limits.** Normals are not rotated back, so a surface turning faster than `acos(reprojNormalTolerance)` (25°) per frame still loses its history. Motion is only for the primary hit; reflections of moving objects reproject with the reflecting surface's motion. Static-batched geometry never moves. Instances that teleport (checkpoint room clones) produce a huge motion that lands off-screen or fails the tests, which is the right result.

## Textures (owned by the denoiser)

| Name | Format | Content |
|---|---|---|
| hist | ARGBHalf | rgb filtered lighting (output of iteration 0), a variance |
| t0, t1 | ARGBHalf | integrated lighting and à-trous ping-pong |
| moments[2] | ARGBHalf | m1, m2, history length, temporal variance (ping-pong) |
| geo[2] | ARGBInt (R32G32B32A32_SInt) | packed world position, normal and instance (ping-pong) |
| boost | ARGB32 | debug channels for `dn-boost`: pre-blur, cache seed, search, firefly cut (written every frame, 3.7 MB) |
| reproj | R32G32_UInt | Temporal's history footprint: base texel + 1, 4 × unorm8 tap weights (read by Stabilize) |
| stable[2] | ARGBHalf | stabilised lighting, a = moving average of luma² (ping-pong) |

All of these are enableRandomWrite and point filtered. At 1280x720 they total about 70 MB. There are also two 8-slot dummy cache buffers, bound when there is no cache. They are reallocated, and the history is reset, whenever the radiance size changes.

## Settings (`Denoiser.Settings`)

| Field | Default | Notes |
|---|---|---|
| iterations | 3 | 0–5. 0 means temporal accumulation only; the integrated lighting is then copied into hist. |
| wideKernel | false | 5x5 B3 instead of 3x3 |
| alphaMin | 0.04 | At `referenceHz`, rescaled to the frame time. Raise it for less lag; lower it for more stability. `-ultrart-dn-alpha` |
| maxHistory | 64 | Clamped to 1–255 |
| referenceHz | 360 | Frame rate alphaMin and stabilizeAlphaMin are meant for (same time constant at any frame rate). 0 = per frame. |
| spatialVarianceFloor | 2 | Variance ≥ this × the input's 3x3 spatial variance. 0 = plain SVGF. `-ultrart-dn-spatialvar` |
| stabilize | true | Output stabilisation pass ([Static noise](#static-noise)). `-ultrart-dn-nostabilize` |
| stabilizeSigma / stabilizeAlphaMin / stabilizeTolerance / stabilizeMaxLength | 2 / 0.05 / 0.02 / 32 | Its clamp (temporal σ of the filtered lighting, plus a relative slack), blend floor and history cap |
| phiNormal / phiDepth / phiLuminance | 128 / 1 / 4 | Edge-stopping parameters, as in the SVGF paper |
| reprojNormalTolerance | 0.9 | Minimum cosine between current and history normal |
| reprojPlaneTolerance | 0.01 | Maximum plane distance, relative to ray t |
| clampHistory / clampSigma | true / 2 | Turn the clip off to compare ghosting against noise |
| debugView | Final | `Final`, `FilteredLighting`, `RawLighting`, `Variance` (heatmap of the relative σ of the temporal estimate; blue is clean, red is σ ≥ 2× the mean), `HistoryLength` (heatmap of len / maxHistory), `Disocclusion` (red where history was rejected this frame, grey elsewhere), `SpatialBoost` (see above), `Motion` (see [Motion vectors](#motion-vectors)) |
| stability | true | Master switch for the four features below. false = plain SVGF's temporal pass. Ctrl+F3 and `-ultrart-dn-legacy` (presets, [Presets and Ctrl+F3](#presets-and-ctrlf3)). |
| boostFrames | 8 | Pre-blur fades out at this history length. 0 = off. `-ultrart-dn-boost N` |
| boostRadius | 4 | Pre-blur radius in trace pixels at history 0. Clamped to 4 (tile apron). `-ultrart-dn-boost-radius R` |
| historySearch | true | 3x3 fallback search. `-ultrart-dn-nosearch` |
| cacheSeed | 1 | Cache seed weight in virtual samples at history 0. 0 = off. `-ultrart-dn-cache W` |
| cacheSeedTolerance | 0.5 | Relative tolerance of the seed's consistency test |
| fireflySigma | 8 | Firefly bound in spreads above the reference. 0 = off. `-ultrart-dn-firefly K` |

Other flags: `-ultrart-dn-noclamp` (clampHistory = false), `-ultrart-dn-iterations N`. Flags apply to both instances (main and hybrid's direct). They are read when a `Denoiser` is constructed; Plugin keeps the main instance's settings across F9 reloads.

## Cost (estimated, before the static-noise work; measured totals are in [Static noise](#static-noise))

Per pixel at 1280x720:
- Temporal: about 31 texture fetches; the 3x3 and the pre-blur read groupshared memory (10 KB per group). The 16x16 tile costs about 4 extra fetches and 4 ray reconstructions per pixel compared with the old 10x10 tile, about +0.01–0.02 ms with nothing boosted. That overhead remains with `stability = false`, so for timing A/B compare against the previous build, not just the flag.
  - Pre-blur, boosted pixels only: at most 26 groupshared taps of about 40 ALU each. Sliding typically boosts 10–30% of the screen for a few frames, about +0.01–0.03 ms. A full reset (all pixels at history 0) is about 0.05 ms for one frame.
  - Cache seed, boosted pixels only: 2 hash chains, a 32-byte key line and a 16-byte read per lookup, plus a second level on a miss. About +0.01–0.03 ms while sliding.
  - 3x3 search: 9 geometry fetches, only for pixels where all 4 bilinear taps failed.
  - The expected total is +0.02–0.06 ms per instance in fast motion, and at most about +0.1 ms on a camera cut, within the +0.15 ms budget.
- Each 3x3 à-trous iteration: 18 fetches, plus 9 for the variance prefilter on iteration 0.
- Composite: 5 fetches.

The whole working set (about 66 MB plus inputs) fits in the RTX 5070 Ti's L2. The expected total is about 0.1–0.2 ms with the defaults, and about 0.3 ms with `wideKernel`. Measure with PIX or by wrapping the dispatches in `cmd.BeginSample("UltraRT Denoise")`.

## Integration (main plugin)

```csharp
// load (with the other bundle assets; rebuild after a hot reload)
var cs = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Denoise");
_denoiser?.Dispose();
_denoiser = new Denoiser(cs);

// every traced frame, after DispatchRays (same command buffer, or one executed earlier)
var inputs = new DenoiserInputs
{
    radiance = _radiance, albedo = _albedo, emission = _emission, // albedo/emission: unpacked by the reflection resolve
    normalDepth = _normalDepth, instanceId = _instanceId,
    specular = _specular, // optional (docs/reflections.md): output = albedo * lighting * spec.a + emission + spec.rgb
    viewProj = viewProj, invViewProj = invViewProj,
    camPos = _camera.transform.position,
    // prevViewProj / prevCamPos are optional: the denoiser tracks its own
};
_denoiser.Execute(_cmd, inputs, _output); // _output: ARGB32, enableRandomWrite, any size
Graphics.ExecuteCommandBuffer(_cmd);
Graphics.Blit(_output, (RenderTexture)null);

// on teardown
_denoiser.Dispose();
```

Cache seed: before `Execute`, set `CacheSource` (the `RadianceCache`, after its update and resolve were recorded this frame), `CacheChannel` and `CacheOffset`. The plugin does this in `Plugin.Cache.cs` (`ConfigureDenoiserCacheSeed`, called from `TraceRadianceCache`). `RadianceCache.BindComputeLookup` binds the lookups on the Temporal kernel. Without a source, the denoiser binds its own dummy buffers.

Call `Reset()` on each of these:
- a scene change (`activeSceneChanged` and `ResetScene()`)
- a shader bundle reload
- a checkpoint restart or respawn, or any teleport or camera cut
- after the view has been off for a while (F7), or after switching back from a non-path-trace mode. The history is still geometrically valid, but its lighting is stale.

There is no need to call it for camera motion or resolution changes. The denoiser handles those itself.

### Assumptions the integrator must satisfy
- All five inputs have the same size and come from the same frame and the same `invViewProj` / `camPos` passed in `inputs`. The primary rays may be jittered only through these matrices (the [temporal upsampler](#temporal-upsampling) does this: `viewProj` includes the jitter, so reprojection stays exact). The progressive accumulation mode jitters inside the ray generation shader, so bypass the denoiser while accumulating.
- `normalDepth.w` is the ray t along the *normalized* primary direction and is negative for sky. The normal faces the camera.
- `radiance` is demodulated, so `albedo * radiance + emission` reproduces the undenoised image. Sky pixels have albedo 1.
- `instanceId` is an `RGInt` texture: x the instance (only the low 7 bits are compared), y the packed world-space motion (`PackMotion`, 0 = static).
- `Execute` is called once per frame, and the recorded commands run exactly once, in order, before the next `Execute`. Ping-pong indices advance at record time.
- The output must be enableRandomWrite and not sRGB. It is written with `saturate()` and no tonemapping.

## Known limitations
- The history clip uses a 1 spp neighbourhood, so it is loose by design. Fast lighting changes, such as muzzle flashes or toggled lights, take a few frames to settle (about `1/alphaMin` frames at worst). An A-SVGF gradient pass would fix this properly.
- Reprojection is bilinear, so the history is resampled slightly every frame while the camera moves. Lighting is smooth enough that this is hard to see, but it does slightly soften shadow edges in motion.
- Screen-edge disocclusions beyond the [guard band](#guard-band) (fast vertical turns, or turns faster than the band covers) have no history at all. They start from the pre-blur (4 px radius at trace resolution) plus the cache seed, and are visibly softer than converged regions for the first few frames. Raise `boostRadius` only together with the tile apron (`APRON` in Denoise.compute; groupshared cost grows quadratically).
- The cache seed is biased wherever the cache and the input disagree by less than the consistency tolerance: at most about `cacheSeedTolerance` (50%) of the value, only for the first frames, and fading as `boost` does. The channel/offset table above is an approximation of what PathTrace adds; check `dn-boost`'s green against `-ultrart-dn-cache 0` in rooms with strong ambient.
- Instance ids are compared modulo 128, and the normal is quantised to 12-bit octahedral components.
