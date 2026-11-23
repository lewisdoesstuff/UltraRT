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

ULTRAKILL's camera moves very fast (slides, dashes). A newly revealed pixel has no history, so plain SVGF shows its 1 spp lighting, filtered only by the à-trous passes, for the first several frames. That shows up as speckle, worst along the leading screen edge while sliding. Four features in the `Temporal` kernel address it. `stability = false` turned all four off and reproduced plain SVGF exactly (the `Legacy` preset, `-ultrart-dn-legacy`, Ctrl+F3; removed on 2026-11-23, see [Presets and Ctrl+F3](#presets-and-ctrlf3)); each still has its own off value below. The [guard band](#guard-band) removes most of the leading-edge case.

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
  | pt-dn, relight, dn-*, pathtrace without RR | full lighting (`_Mode` 0) | cache `total` + primary ambient + 0.5 · ambient |

  Until 2026-11-19 the hybrid mode had two more rows: its main instance denoised the bounce only (PathTrace `_Mode` 3,
  seeded from the cache's `indirect`), and a second instance denoised its shadowed/unshadowed luminance pair (no seed).
  Both went with the hybrid mode; there is one denoiser instance since.

  "ambient" is PathTrace's `_Ambient · RelightAmbientScale()`. The 0.5 · ambient term stands in for the bounce-hit ambient (`albedo · ambient`), which the cache leaves out, using a typical albedo of 0.5. The seed is off when the cache is off or its mode is `Off`.

**4. Anti-firefly** (`fireflySigma` 8). This applies only to pixels with ≥ 4 frames of history, where the reference is trustworthy.
- `ref = max(history m1, mean of the 8 neighbours)` and `spread = max(history σ, σ of the 8 neighbours)`. The sample's luminance is limited to `max(2 · ref, ref + fireflySigma · spread)`, and the hue is kept.
- Muzzle flashes and lights switching on brighten the neighbours too, so they pass. A lone 50x sample on a converged surface no longer flashes a blotch for about 10 frames.
- Short-history pixels are left alone (at 1 spp any clamp would eat legitimate sparse samples). The pre-blur spreads their outliers instead.
- **Bias**: on a converged surface whose bright samples are rarer than about 1 in 30 frames, those samples are partly clamped, which darkens it slightly. Use `-ultrart-dn-firefly 0` to compare.

**Debug view `dn-boost`** (`DenoiserDebugView.SpatialBoost`, near the end of the Page Up/Page Down list, before `motion`), drawn over a dim grey image: **red** is pre-blur strength, **green** the cache seed weight, **blue** history recovered by the 3x3 search, and **white** a firefly clamped by more than 10%. While sliding, expect a red band along the leading edge that fades within 8 frames, green over most of it (indirect light near the camera), and blue specks along silhouettes. `dn-history` still shows the history length.

## Static noise

*History (2026-11-14/15): the hybrid rows and the ReSTIR features below (hybrid spatial reuse, fresh-pixel candidates,
per-pixel light-change history) went with the hybrid mode and ReSTIR on 2026-11-19; RIS has been the default sampler
since 2026-11-15 ([light-sampling.md](light-sampling.md)). The denoiser features are current.*

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

## Motion noise

The static-noise work above roughly halved the noise of a still image, but in motion (slides, dashes, fast turns) the image was still visibly noisier than Quake II RTX or Minecraft RTX. Measured with the bench's motion-noise tool (bench/README.md, "Motion noise"): the same window of a camera path is replayed several times on a fixed 1/360 s clock with every history reset, and the per-pixel difference between replays at the same frame is the noise the player sees in motion (sigma, screen luma 0–255, over traced surfaces; the viewmodel, the HUD and what the game animates itself are left out). The denoiser classifies each pixel, so the noise is split by cause. Suites: `bench/suites/motion-noise.json`, `motion-noise-r3.json` (variant study), `motion-final.json` (results below). Volumetric haze is measured off (`vol=0`): with it on, its own noise dominated every scene (σ 3.6 instead of 0.9 on the 1-1 sweep), see docs/volumetrics.md.

**Where it comes from** (relight, before this work):

| Scenario | σ | Breakdown (σ / share of pixels / share of the squared error) |
|---|---|---|
| 1-1 spawn, still | 0.48 | interior 0.49 |
| 1-1 sweep 180°/s | 0.90 | **fresh 1.97 / 10% / 48%**, young (8–31 frames) 0.84 / 11% / 10%, screen edge 0.78 / 27% / 20%, edges 1.29 / 2% / 5%, interior 0.51 / 49% / 16% |
| 1-1 sweep 500°/s | 1.14 | **fresh 1.88 / 26% / 70%**, young 0.95 / 15% / 10%, edges 1.56 / 3% / 5%, interior 0.52 / 38% / 8% |
| 0-2 run, 509°/s turn (40.9 s) | 2.31 | **fresh 3.62 / 16% / 41%**, edges 2.78 / 8% / 12%, young 2.22 / 13% / 12% |

- **Disocclusions dominate.** Pixels with under 8 frames of denoiser history are 10–26% of the screen in a fast turn but carry half to two thirds of the error, at about 4x the noise of a converged pixel. A pixel that has history is almost as calm in motion as when still (interior 0.51 vs 0.49): reprojection itself costs little.
- **Silhouettes and thin geometry** are 2–3x noisier than interiors, but only 2–8% of the pixels.
- **Changing lights**: a muzzle-light flash every 0.1 s in the 1-1 corridor adds little (0.48 → 0.56); a large explosion-like light every 0.3 s more (0.83). Lights that move with the camera while sliding (0-2) are the worst case.
- The user's "noisy around the edges while sliding" is the screen-edge and fresh classes: in forward motion the edges carry the fastest flow and the nearest geometry, so they disocclude most.

**What changed** (the `Full` preset; `Static` = the build before). All three act only on pixels without history, so a converged image is unchanged:
1. **Cache seed 1 → 4** (`cacheSeed`). A fresh pixel takes 4 virtual samples of its radiance-cache cell (fading out with the pre-blur by 8 frames), under the same consistency test. The cache is world space and averages dozens of paths per cell, so it is the best prior a pixel without history has. 1-1 sweep: 0.90 → 0.73 alone (8: 0.70; 4 keeps the bias bound of the seed smaller).
2. **More ReSTIR candidates for fresh pixels** (`LightSamplingSettings.freshCandidates` 24, docs/light-sampling.md "Fresh pixels"): a pixel whose temporal reservoir failed resamples 32 lights instead of 8. 0.90 → 0.71 alone, and silhouettes and thin geometry (which lose their reservoir often) improve most: edges 1.29 → 0.96, thin 2.30 → 1.63.
3. **Wider à-trous for young pixels** (`freshSpread` 2): the à-trous steps of a pixel with no history are doubled (radius 14 instead of 7 trace px after three iterations), fading back to normal by 8 frames. 0.90 → 0.80 alone. No extra pass: the same taps, further apart.

Available but **off by default** (no reliable gain in the measurements):
- **Anti-lag** (`antilag`, `-ultrart-dn-antilag 3`; `antilagMinLength` 8): where the 5x5 same-surface mean of this frame's input leaves the reprojected history by more than `antilag` standard errors plus `antilagTolerance` (15%), the lighting really changed and the history length is cut by the square of the excess, never below `antilagMinLength` frames. It follows changing lights sooner, but every cut history is noisier for a while: with a muzzle-light flash every 0.1 s in 4-1 it raised the noise from 0.65 to 0.89 (cut to 0 frames: 1.45), and on the 0-2 windows it was inconclusive.
- **Per-pixel ReSTIR history on light changes** (`LightSamplingSettings.perPixelLightChange`, `-ultrart-restir-pixel-change`; docs/light-sampling.md "Light changes"): a light appearing or disappearing caps the reservoir history only where it matters instead of on every pixel. No measurable difference on the flash and explosion scenes; on the 0-2 windows the whole-screen cap measured lower twice. Its per-pixel evaluation of the change list also cost about 0.3 ms in the 0-2 combat run.

**Results** (`bench/suites/motion-final.json`, `motion-cost.json`; RTX 5070 Ti, 2560x1440, trace scale 0.5; motion σ Static → Full, same run):

| Scenario | relight | hybrid |
|---|---|---|
| 1-1 sweep 180°/s | 0.88 → **0.66** (fresh 1.95 → 1.31, edges 1.24 → 0.89, thin 2.23 → 1.59) | 0.86 → 0.81 |
| 1-1 sweep 500°/s | 1.14 → **0.86** (fresh 1.86 → 1.31) | 1.02 → 0.90 |
| 4-1 indoor A sweep 180°/s | 0.95 → **0.64** (fresh 1.72 → 0.68) | 0.25 → 0.21 |
| 0-2 run, 509°/s turn (40.9 s) | 2.22 → **1.84** (fresh 3.54 → 2.69) | 4.06 → 3.93 |
| 0-2 run, 30 m/s slide (7.6 s) / 40 m/s dash (16.6 s) | 2.15 / 2.31 → 1.96 / 2.45 | 1.35 / 1.77 → 1.46 / 2.00 |
| 1-1 corridor, muzzle flash every 0.1 s | 0.55 → 0.52 | 0.69 → 0.73 |
| 4-1 indoor A, muzzle flash every 0.1 s | 0.62 → 0.65 | 0.25 → 0.24 |
| 1-1 corridor, explosion light every 0.3 s (auto exposure off, 4 passes; `motion-blast.json`) | 1.33 → 1.34 (Static measured first and last: 1.36 / 1.30) | |

(the flash rows and the two 0-2 slide windows are from the run's "Full without anti-lag" ablation, i.e. the current defaults.) Changing lights are not helped by the fresh-pixel fixes, and not hurt: the explosion scene's own spread between runs (Static alone measured 0.80 to 1.36) is larger than any difference between the settings. The 0-2 windows are the least reliable: lighting there also depends on game time (sprite-flicker lights, emissive visibility updates on a real-time schedule), so identical settings measured within ±10–20% between runs; the slide and dash windows have few fresh pixels, so the fixes have little to act on. Strips: the `_f92` .. `_f95` frames of each mode (4 consecutive frames at 360 Hz) in the run folder; at the entering screen edge the Full rows show visibly less mottling on flat walls.

Static images are unchanged (bursts, masked σ / block σ, Static → Full): 1-1 spawn relight 0.26 / 0.17 → 0.26 / 0.17, hybrid 0.43 / 0.30 → 0.43 / 0.30; 4-1 indoor A relight 0.27 / 0.21 → 0.27 / 0.21; 0-3 ghosts relight 0.39 / 0.22 → 0.39 / 0.23, hybrid 0.64 / 0.44 → 0.63 / 0.44.

**Cost** (GPU total ms, Static → Full, modes alternating in the same pass): 0-2 run with enemies and revolver fire: relight 4.36 → 4.49 (+0.13), hybrid 4.19 → 4.27 (+0.08); a sustained 500°/s sweep, the worst case (a quarter of the screen fresh every frame): relight 4.93 → 5.19 (+0.26), of which the extra ReSTIR candidates are about 0.3 (Full without them: 4.87). The cache seed and the wider footprint cost nothing measurable.

Tried and not kept: a longer pre-blur fade (`boostFrames` 16: no change), TAAU weight 8 (0.90 → 0.86, mostly interiors, more smear in motion), a wider guard band (8%: 0.84 → 0.81 on the 500°/s sweep for 8% more rays), a vertical guard band (4%: mixed on the 0-2 windows), shortening ReSTIR history for lights that only change brightness (`lightChangeKept`: noisier around fading lights).

## Moving shadows

V1's third-person model casts shadows (docs/reflections.md, section 13), but while walking its shadow was practically invisible, and when standing still it faded in over several frames (user report, `bench/suites/user-1114-pm.json` "0-2 v1 shadow walk"). Measured with the motion replay (`noise-pm-diag2/3`, `noise-pm-check`): the raw direct light (`pt-direct+relight` strips) has a crisp shadow in every frame, the denoised image almost none.

**Why.** A moving shadow is a large, fast change of the input on a surface whose history is long and valid (the floor doesn't move, so reprojection succeeds). The temporal blend (about 70 ms) and the stabilisation average it out, and the moving edge also raises the temporal variance, which loosens the à-trous luminance weights and blurs the rest away. ReSTIR's visibility reuse adds a trail: an occluded reservoir keeps its M with W = 0, so the floor the shadow just left stays dark for up to 160 samples of M.

Tried first and not kept: a targeted anti-lag (`moverAntilag`, the anti-lag test with its own parameters within 2 px of a pixel whose light sample crossed a moving object's box). The cut pixels went back to the fresh-pixel path (pre-blur, wider à-trous, and the cache seed, which never holds the player's shadow), so the shadow got weaker still (shadow contrast in the strip 0.11 against 1.0 without it). It stays available, off (`dn.moverAntilag`).

**What changed: the player's shadow is kept out of the denoised signal** (relight, pt-dn and the dn-* views; the raw pt modes keep the old path, as hybrid did until 2026-11-19):
1. PathTrace's final direct-light shadow ray ignores V1 (`_UltraRTPlayerSplit`), so the denoised lighting (and, until 2026-11-19, ReSTIR's reuse) never sees V1's shadow: no lag, no trail.
2. Where the sample's segment crosses V1's bounds (`_UltraRTPlayerModelMin/Max`), the light is visible and it is not the player's own (the player-light flag, or the player box for emitters; docs/reflections.md, "Level lights near the head, and the shadow split"), one more ray against V1 alone (mask 0x80, accept-first-hit) tells whether V1 blocks it. The light it blocks becomes a factor, `1 - blocked / lighting`, written to `_Radiance.a` (nothing else read it).
3. Temporal filters that factor separately: a 5x5 same-surface mean (inner 3x3 weight 1, outer ring 0.5), blended 0.5 with its own history on the same reprojection footprint (`_playerF[2]`, RHalf), so it follows within a frame or two. Composite multiplies it into the filtered lighting. Where V1 is not involved the factor is 1 and the image is unchanged.

The extra ray is only traced for pixels whose light sample passes V1's box (the area its shadow can fall on). Bounce rays still see V1 (its occlusion of bounce light is small and stays in the filtered signal). Enemies and other moving objects are not split (they have no mask bit of their own); for them only the ReSTIR part applies: occluded history whose sample crosses a moving object's box is dropped (docs/light-sampling.md, "Moving occluders").

Settings: `plugin._playerSplit` (true; 0 = the old path), `dn.playerShadowBlend` (0.5; 0 = ignore the factor), `plugin._movers` (moving-object boxes; ReSTIR used them until 2026-11-19), `dn.moverAntilag` (0).

**Results** ("0-2 v1 shadow walk", the user's recording of walking left and right at about 12 m/s with a light behind; motion replay, 96 frames at 360 Hz after 120 settle frames, `bench/suites/noise-pm-check.json` and `noise-pm-final.json`). Strip frames `0-2_v1_shadow_walk_motion_2.0s_<mode>_f88..f95.jpg` in the run folders:
- Before: the denoised image shows at most a faint blob where the shadow was; the raw direct light (`pt-direct+relight`) has a crisp shadow in the same frames. A held screenshot (the bench holds the pose) shows the full shadow, which is why the problem never showed in bench screenshots.
- After: the shadow (body, gun arm, wings) is visible in every strip frame while walking, at the same place as in the raw direct light, and as crisp as the held screenshot when standing still.
- Motion noise (sigma, luma): 0.53 → 1.27 (`relight+plugin._playerSplit=0+plugin._movers=0` → `relight`; without V1 at all 0.44). The added error is on the shadow itself (the error map shows V1's head, wing and gun-arm shadow, a few pixels wide each): before, there was no shadow to be noisy. Where V1 blocks one of several lights the factor still varies from frame to frame; a longer factor history (`dn.playerShadowBlend` below 0.5) would calm it at the price of lag.
- Static images are unchanged where V1's shadow isn't involved (the factor is 1 there).

**Cost of the whole evening pass** (player split and movers, eye lights, occluded-history cap, portal importance, the viewmodel filter changes, game-matched exposure, haze balance; `bench/suites/noise-pm-cost.json`, `-Perf`, exclusive slots, modes alternating every 0.5 s against all of it switched off; RTX 5070 Ti, 1920x1080, trace scale 0.5; GPU total / DispatchRays / denoise ms):

| Scenario | all off | defaults |
|---|---|---|
| 0-2 run (71 s, active enemies) | 3.264 / 1.964 / 0.303 | 3.542 / 2.215 / 0.324 |
| 8-2 portal side 1 | 3.384 / 1.879 / 0.316 | 3.433 / 1.909 / 0.332 |
| 3-1 noisy room (checkpoint) | 2.624 / 1.528 / 0.299 | 2.678 / 1.569 / 0.311 |

The 0-2 run pays most (+0.28 ms): the player is in view of lit floors all the time, so many pixels trace the V1-only shadow ray, and the moving enemies' boxes add the per-sample slab tests. Plugin CPU +0.07 ms in the 0-2 run (mover boxes), +0.15 ms in 8-2 (portal viewpoints in the alias table, the game-colour copy for the exposure match).

## Presets and Ctrl+F3

**Removed on 2026-11-23** (before 0.3.0): the `DenoiserPreset` enum, `ApplyPreset`, `ApplyDenoiserPreset`, the Ctrl+F3
cycle, the settings menu's Denoiser row (`[Quality] Denoiser`), `-ultrart-dn-static`, `-ultrart-dn-previous`,
`-ultrart-dn-legacy`, the bench variant `preset=` and the `stability` master switch. `Full` (the current defaults) is
what UltraRT's denoiser always runs: it is still the denoiser of relight and of pathtrace without DLSS RR, and every
feature keeps its own field and flag. The cinematic accumulation render, which used `stability = false`, now zeroes the
same fields one by one (`boostFrames`, `fireflySigma`, `historySearch`, `cacheSeed`, `freshSpread`, `antilag`,
`moverAntilag`, `stabilize`). `-ultrart-dn-mover` went too (`dn.moverAntilag` stays, off). The measurements above that
compare Static / Previous / Legacy with Full are kept as they were. The presets were:

Ctrl+F3 cycled `DenoiserPreset` on the denoiser (two instances until hybrid's went on 2026-11-19; `Plugin.ApplyDenoiserPreset`, also used by the bench's `preset=` variant):
- **Full**: everything above and [Motion noise](#motion-noise).
- **Static**: the build before the motion-noise work: cache seed 1, the normal à-trous footprint for young pixels, no extra ReSTIR candidates for fresh pixels (and the optional anti-lag and per-pixel light-change history off; the ReSTIR parts are moot since ReSTIR went on 2026-11-19, so Static and Full now differ in denoiser fields only).
- **Previous**: the build before the static-noise work. alphaMin 0.1 per frame, 32 frames, no variance floor, no stabilisation, no guard band, no hybrid spatial reuse (gone with hybrid); the four stability features stay on (motion features off, as in Static).
- **Legacy**: Previous without the stability features, i.e. plain SVGF.

`-ultrart-dn-static`, `-ultrart-dn-previous` and `-ultrart-dn-legacy` started in those presets (denoiser fields only). Individual flags, still in place: `-ultrart-dn-alpha A`, `-ultrart-dn-spatialvar K`, `-ultrart-dn-nostabilize`, `-ultrart-dn-stabilize-alpha A`, `-ultrart-dn-stabilize-sigma K`, `-ultrart-guard X [Y]` (`-ultrart-restir-spatial N` and `-ultrart-restir-spatial-hybrid N` went with ReSTIR on 2026-11-19).

**Not the denoiser**: in 1-1's start area, the tree canopies and grass are the game's own animated foliage, drawn over the RT image. The thin wires against the sky used to flicker because of the half-resolution depth-guided upsample; the [temporal upsampler](#temporal-upsampling) fixes that in the replace modes.

## Temporal upsampling

The trace runs at `-ultrart-scale` (0.5) of the screen. The old composite picked, per screen pixel, the one of the 2x2 nearest trace texels whose depth matched the game's. That gave stair-stepped silhouettes, blocky textures and lighting (2x2 screen pixels per texel), and thin geometry (wires, railings) that switched between RT and the game's own shading. In the replace modes (pt-dn, relight, dn-\*) a TAAU pass now rebuilds a screen-resolution image: `Upsample.compute`, `src/UltraRT/TemporalUpsampler.cs`. Hybrid is unchanged: it keeps the game's full-resolution colour and applies only RT ratios.

**Where it applies.** It works on the final RT colour (the denoiser's composite output), not on demodulated lighting. In relight and pt-dn the textures come from the RT hits too: no full-resolution albedo exists to remodulate with (`pathtrace` has remodulation since 2026-11-15: [path-tracing.md](path-tracing.md), "UltraRT's denoiser at 1/3"). `pathtrace` runs through the upsampler as well; with DLSS RR the upsampler is replaced ([dlss.md](dlss.md)). Jittered exact-hit samples rebuild the textures at screen resolution as well.

**Jitter.** Each frame the trace grid is offset by a sub-pixel jitter. The projection is `T(-2j / traceSize) · viewProj`, so pixel p's primary ray goes through trace position p + 0.5 + j. When the screen is exactly twice the visible trace size, j cycles through (±0.25, ±0.25) trace px: every screen pixel centre is traced exactly once per 4 frames. Otherwise j follows an 8-phase Halton(2, 3) sequence. Every temporal consumer (this denoiser, ReSTIR, reflections, the radiance cache) reprojects world positions with the same jittered matrices, so their histories stay consistent with no other change.

**Per screen pixel** (one 8x8 group loads the 11x11 trace samples it needs into groupshared memory):
1. **Geometry from the game.** The copied game depth buffer gives the eye depth and the exact world position. A trace sample belongs to this pixel's surface when its eye depth matches within 3% + 5 cm, the old composite's discard test. Sky, and anything the game draws that we don't trace, matches nothing, and the game's pixel is kept (output a = 0, the composite discards).
2. **New samples.** Of the 3x3 matching samples around the nearest one, each counts with weight exp(−4 d²), where d is its distance to the pixel centre in screen px. An exact hit counts 1, a neighbour one pixel away 0.02, so edges and texels stay pixel sharp. A depth-matched Gaussian of the same samples (σ 0.6 trace px, weight 0.05) is the **edge-aware fallback** where there is no history.
3. **History.** The world position is moved by the matching sample's object motion (`_InstanceId.y`) and projected with last frame's screen viewProj. The 4 bilinear taps of the screen-resolution history are validated against their stored eye depth (2%). With all 4 valid, a 5-tap Catmull-Rom resamples the history, clamped to the 4 taps' range. The history is clipped to the matching samples' min/max (+10%), but only when at least 3 samples match: an edge pixel with 1 or 2 would lose its history every frame. The weight is capped at 3 exact hits (`maxWeight`).
4. **Thin geometry.** If no sample matches this frame but the history weight is ≥ 0.1, the history is kept, decaying ×0.95 per frame. A wire the jittered grid missed this frame stays RT-shaded instead of falling back to the game's pixel. The threshold matters: in 1-1 the game's sky dome sits at nearly the depth of the arch, and without it, tiny bilinear weights let the arch creep 25 px into the sky over a few seconds.

**A/B**: F2 toggles it (off = the old 2x2 path, no jitter). `-ultrart-notaau`, `-ultrart-taau-nojitter`, `-ultrart-taau-weight N`, `-ultrart-taau-sharpness K`. In benchmark modes, `taau=0|1` and `up.<field>=value` (`UpsamplerSettings`). `up.debugView=1` shows r = matching samples, g = history weight, b = held without a sample (debug only: it breaks the history). `up.debugView=2` shows the game's depth; `up.debugView=3` compares the nearest trace sample's depth with the game's (r = traced surface behind, g = in front, full at 5%; grey-blue = within the match tolerance, blue = no hit): the quickest way to see that something we trace is not where the game draws it (docs/scene-proxies.md, "Mirrored skinned meshes").

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

**Trace scale**: 0.67 with the upsampler costs 50–60% more GPU (1-1 start 1.27 → 1.85 ms, 8-3 b 1.67 → 2.55 ms, 0-2 run 2.18 → 3.37 ms). A static view looks the same as 0.5, slightly softer even: the Halton jitter never lands exactly on pixel centres. Noise is no better. **Keep 0.5**, the only scale whose jitter hits every pixel centre exactly (historical for `pathtrace`, whose default scale is 1/3 with an exact 3x3 jitter grid: [path-tracing.md](path-tracing.md)). The upsampler works at any scale.

**Limits.** Thin features and silhouettes inherit more denoiser noise than before. Their trace texels alternate between surfaces as the grid jitters, so the denoiser's history there is short, and the cap of 3 exact hits only partly averages it. A pixel held without samples doesn't follow lighting changes until a sample lands on it again (≤ 4 frames static). The game's depth is assumed to be the main camera's, covering the same view as the composite target.

## Motion vectors

Every temporal consumer reprojects a moving surface point from where it was last frame: this denoiser, ReSTIR's temporal reuse until 2026-11-19 (`UltraRTLights.hlsl`, `g_PrimaryMotion`) and the reflection history (`Reflections.compute`, surface and virtual point). The disocclusion tests (plane, normal, instance) compare the history against `p + motion`, so an enemy keeps its history while it walks and the wall it uncovers is rejected.

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

**Debug view `motion`** (`DenoiserDebugView.Motion`, last in the Page Up/Page Down list): object motion as a flow colour over a dim grey image (hue = screen direction, saturation = speed, full at 8 trace px/frame; slow objects are grey-white); camera-only motion is a faint tint. A spinning object shows a hue wheel; an object whose previous pose is wrong shows a field converging on one point (that was the symptom of a broken pose copy, with `dn-history` dropping to 0 on every enemy). `-ultrart-nomotion` turns motion off (all slots identity, no pose copies) for A/B; `-ultrart-motion-probe` logs which space Unity's skinned vertex buffer is in (`-ultrart-motion-probe-filter <text>`: once for each skinned proxy whose path contains the text, with its placement and both BakeMesh variants).

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
| Classes | R8 | bench only (`ClassifyFrame`): per-pixel motion-noise classes, written by `Classify` (bench/README.md, "Motion noise") |

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
| ~~stability~~ | (removed 2026-11-23) | Was the master switch for the four features below (false = plain SVGF's temporal pass; [Presets and Ctrl+F3](#presets-and-ctrlf3)). Turn each one off by its own field. |
| boostFrames | 8 | Pre-blur fades out at this history length. 0 = off. `-ultrart-dn-boost N` |
| boostRadius | 4 | Pre-blur radius in trace pixels at history 0. Clamped to 4 (tile apron). `-ultrart-dn-boost-radius R` |
| historySearch | true | 3x3 fallback search. `-ultrart-dn-nosearch` |
| cacheSeed | 4 | Cache seed weight in virtual samples at history 0. 0 = off. `-ultrart-dn-cache W`. 1 before the motion work ([Motion noise](#motion-noise)) |
| cacheSeedTolerance | 0.5 | Relative tolerance of the seed's consistency test |
| fireflySigma | 8 | Firefly bound in spreads above the reference. 0 = off. `-ultrart-dn-firefly K` |
| freshSpread / freshSpreadFrames | 2 / 8 | À-trous step multiplier for pixels with no history, fading out at this history length. 1 = off. `-ultrart-dn-fresh-spread K` ([Motion noise](#motion-noise)) |
| antilag / antilagTolerance / antilagMinLength | 0 / 0.15 / 8 | Anti-lag for changing lights: history shortened where the 5x5 input mean leaves the history by more than this many standard errors plus the relative tolerance, never below the minimum length. 0 = off. `-ultrart-dn-antilag K` |

Other flags: `-ultrart-dn-noclamp` (clampHistory = false), `-ultrart-dn-iterations N`. Flags apply to the denoiser (to both instances, main and hybrid's direct, until 2026-11-19). They are read when a `Denoiser` is constructed; Plugin keeps the main instance's settings across F9 reloads.

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
- The history clip uses a 1 spp neighbourhood, so it is loose by design. Fast lighting changes, such as muzzle flashes or toggled lights, take a few frames to settle (about `1/alphaMin` frames at worst; the optional anti-lag cuts that to about 8 at the price of more noise, see [Motion noise](#motion-noise)). An A-SVGF gradient pass would fix this properly.
- Reprojection is bilinear, so the history is resampled slightly every frame while the camera moves. Lighting is smooth enough that this is hard to see, but it does slightly soften shadow edges in motion.
- Screen-edge disocclusions beyond the [guard band](#guard-band) (fast vertical turns, or turns faster than the band covers) have no history at all. They start from the pre-blur (4 px radius at trace resolution), the cache seed (4 virtual samples), 32 ReSTIR candidates and a doubled à-trous footprint ([Motion noise](#motion-noise)), so they are softer than converged regions for the first few frames and still about twice as noisy. Raise `boostRadius` only together with the tile apron (`APRON` in Denoise.compute; groupshared cost grows quadratically).
- The cache seed is biased wherever the cache and the input disagree by less than the consistency tolerance: at most about `cacheSeedTolerance` (50%) of the value, only for the first frames, and fading as `boost` does. The channel/offset table above is an approximation of what PathTrace adds; check `dn-boost`'s green against `-ultrart-dn-cache 0` in rooms with strong ambient.
- Instance ids are compared modulo 128, and the normal is quantised to 12-bit octahedral components.
