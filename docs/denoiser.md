# Denoiser

A lightweight version of SVGF for the 1 spp path-traced lighting. It filters demodulated lighting only. Albedo and emission are applied at the end and are never blurred, so point-sampled texel edges stay sharp.

Files: `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/Denoise.compute` (in the `ultrart.shaders` bundle) and `src/UltraRT/Denoiser.cs`.

## Passes

All passes use 8x8 thread groups and run at trace resolution, except Composite, which runs at output resolution.

| # | Kernel | Reads | Writes |
|---|---|---|---|
| 1 | `Temporal` | radiance, normalDepth, instanceId, prevGeo, hist, prevMoments | t0 (lighting, variance), moments[cur], geo[cur] |
| 2 | `Atrous3` or `Atrous5` ×N | previous iteration's output, geo[cur] | iteration 0 writes **hist** (the history fed back next frame). Later iterations alternate t0 and t1. |
| 3 | `Composite` | last filtered output, albedo, emission, radiance, moments[cur] | output |

**Temporal**
- Rebuilds each pixel's world position from the primary ray (`camPos + dir * t`), using the same formula as the ray generation shader.
- Projects that position with the previous frame's viewProj, then does a 2x2 bilinear fetch. Each tap is validated separately: the valid bit is set (not sky), the low 7 bits of the instance id match, `dot(n, nPrev) ≥ reprojNormalTolerance`, and `|dot(pPrev − p, n)| ≤ reprojPlaneTolerance · t + 0.01`. The weights of the valid taps are renormalised. If no tap is valid, the history length goes back to 0.
- Blends with an exponential moving average, `alpha = max(1 / len, alphaMin)` where `len = min(histLen + 1, maxHistory)`. Both the lighting and the luminance moments (m1, m2) are accumulated this way.
- Variance is `m2 − m1²`. Below 4 frames of history it is replaced by the luminance variance of the 3x3 neighbourhood. The 3x3 is cached in groupshared memory and only includes pixels whose depth is within 10%.
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

## Textures (owned by the denoiser)

| Name | Format | Content |
|---|---|---|
| hist | ARGBHalf | rgb filtered lighting (output of iteration 0), a variance |
| t0, t1 | ARGBHalf | integrated lighting and à-trous ping-pong |
| moments[2] | ARGBHalf | m1, m2, history length, temporal variance (ping-pong) |
| geo[2] | ARGBInt (R32G32B32A32_SInt) | packed world position, normal and instance (ping-pong) |

All of these are enableRandomWrite and point filtered. At 1280x720 they total about 66 MB. They are reallocated, and the history is reset, whenever the radiance size changes.

## Settings (`Denoiser.Settings`)

| Field | Default | Notes |
|---|---|---|
| iterations | 3 | 0–5. 0 means temporal accumulation only; the integrated lighting is then copied into hist. |
| wideKernel | false | 5x5 B3 instead of 3x3 |
| alphaMin | 0.1 | Raise it (0.2) for less lag; lower it for more stability. Note that at 360 fps, 0.1 is only about 30 ms of history. |
| maxHistory | 32 | Clamped to 1–255 |
| phiNormal / phiDepth / phiLuminance | 128 / 1 / 4 | Edge-stopping parameters, as in the SVGF paper |
| reprojNormalTolerance | 0.9 | Minimum cosine between current and history normal |
| reprojPlaneTolerance | 0.01 | Maximum plane distance, relative to ray t |
| clampHistory / clampSigma | true / 2 | Turn the clip off to compare ghosting against noise |
| debugView | Final | `Final`, `FilteredLighting`, `RawLighting`, `Variance` (heatmap of the relative σ of the temporal estimate; blue is clean, red is σ ≥ 2× the mean), `HistoryLength` (heatmap of len / maxHistory), `Disocclusion` (red where history was rejected this frame, grey elsewhere) |

## Cost (estimated, not yet measured)

Per pixel at 1280x720:
- Temporal: about 27 texture fetches, 3x3 from groupshared memory.
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
    radiance = _radiance, albedo = _albedo, emission = _emission,
    normalDepth = _normalDepth, instanceId = _instanceId,
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

Call `Reset()` on each of these:
- a scene change (`activeSceneChanged` and `ResetScene()`)
- a shader bundle reload
- a checkpoint restart or respawn, or any teleport or camera cut
- after the view has been off for a while (F7), or after switching back from a non-path-trace mode. The history is still geometrically valid, but its lighting is stale.

There is no need to call it for camera motion or resolution changes. The denoiser handles those itself.

### Assumptions the integrator must satisfy
- All five inputs have the same size and come from the same frame and the same `invViewProj` / `camPos` passed in `inputs`. The primary rays are **not jittered**. The progressive accumulation mode does jitter, so bypass the denoiser while accumulating.
- `normalDepth.w` is the ray t along the *normalized* primary direction and is negative for sky. The normal faces the camera.
- `radiance` is demodulated, so `albedo * radiance + emission` reproduces the undenoised image. Sky pixels have albedo 1.
- `instanceId` is an `RInt` texture. Only the low 7 bits are compared.
- `Execute` is called once per frame, and the recorded commands run exactly once, in order, before the next `Execute`. Ping-pong indices advance at record time.
- The output must be enableRandomWrite and not sRGB. It is written with `saturate()` and no tonemapping.

## Known limitations
- There are no motion vectors. Moving objects reproject using their current world position. When an object moves along its own surface plane its lighting can trail slightly. Motion along the normal is rejected by the plane test, and moving onto a different instance is rejected by the instance test.
- The history clip uses a 1 spp neighbourhood, so it is loose by design. Fast lighting changes, such as muzzle flashes or toggled lights, take a few frames to settle (about `1/alphaMin` frames at worst). An A-SVGF gradient pass would fix this properly.
- Reprojection is bilinear, so the history is resampled slightly every frame while the camera moves. Lighting is smooth enough that this is hard to see, but it does slightly soften shadow edges in motion.
- Reprojection has no 3x3 fallback search when all 4 taps fail. Disocclusions restart from the spatial variance estimate, and the à-trous filter blurs them strongly for the first frames.
- Instance ids are compared modulo 128, and the normal is quantised to 12-bit octahedral components.
