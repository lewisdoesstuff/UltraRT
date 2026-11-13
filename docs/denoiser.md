# Denoiser

A lightweight version of SVGF for the 1 spp path-traced lighting. It filters demodulated lighting only. Albedo and emission are applied at the end and are never blurred, so point-sampled texel edges stay sharp.

Files: `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/Denoise.compute` (in the `ultrart.shaders` bundle) and `src/UltraRT/Denoiser.cs`.

## Passes

All passes use 8x8 thread groups and run at trace resolution, except Composite, which runs at output resolution.

| # | Kernel | Reads | Writes |
|---|---|---|---|
| 1 | `Temporal` | radiance, normalDepth, instanceId, prevGeo, hist, prevMoments, radiance cache (optional) | t0 (lighting, variance), moments[cur], geo[cur], boost (debug) |
| 2 | `Atrous3` or `Atrous5` ×N | previous iteration's output, geo[cur] | iteration 0 writes **hist** (the history fed back next frame). Later iterations alternate t0 and t1. |
| 3 | `Composite` | last filtered output, albedo, emission, radiance, moments[cur], boost | output |

**Temporal**
- Loads a 16x16 tile (the 8x8 group plus a 4-pixel apron) into groupshared memory: radiance, ray t, world position and normal. The 3x3 statistics and the pre-blur read from it.
- Rebuilds each pixel's world position from the primary ray (`camPos + dir * t`), using the same formula as the ray generation shader.
- Moves that position to where the surface was last frame, `p' = p + motion` (see [Motion vectors](#motion-vectors); 0 for static geometry), projects `p'` with the previous frame's viewProj, then does a 2x2 bilinear fetch. Each tap is validated separately: the valid bit is set (not sky), the low 7 bits of the instance id match, `dot(n, nPrev) ≥ reprojNormalTolerance`, and `|dot(pPrev − p', n)| ≤ reprojPlaneTolerance · t + 0.01`. The weights of the valid taps are renormalised.
- If no tap is valid, a 3x3 search around the reprojected point takes the best texel that passes the same tests (`historySearch`). Only if that also fails does the history length go back to 0. See [Temporal stability](#temporal-stability).
- Blends with an exponential moving average, `alpha = max(1 / len, alphaMin)` where `len = min(histLen + 1, maxHistory)`. Both the lighting and the luminance moments (m1, m2) are accumulated this way.
- Variance is `m2 − m1²`. Below 4 frames of history it is replaced by the luminance variance of the 3x3 neighbourhood. The 3x3 is cached in groupshared memory and only includes pixels whose depth is within 10%.
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

ULTRAKILL's camera moves very fast (slides, dashes). A newly revealed pixel has no history, so plain SVGF shows its 1 spp lighting, filtered only by the à-trous passes, for the first several frames. That shows up as speckle, worst along the leading screen edge while sliding. Four features in the `Temporal` kernel address it. `stability = false` (`-ultrart-dn-legacy`, Ctrl+F3) turns all four off and reproduces the previous output exactly.

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

## Motion vectors

Every temporal consumer reprojects a moving surface point from where it was last frame: this denoiser (both instances), ReSTIR's temporal reuse (`UltraRTLights.hlsl`, `g_PrimaryMotion`) and the reflection history (`Reflections.compute`, surface and virtual point). The disocclusion tests (plane, normal, instance) compare the history against `p + motion`, so an enemy keeps its history while it walks and the wall it uncovers is rejected.

**What is stored.** PathTrace's primary ray asks the hit shader for motion (payload `flags` bit 30 in, packed motion out in the payload's `motion` dword; other rays and DebugView get 0). To make room within the 64-byte payload, emission travels as 3 halves (`emissionHalf`, `PayloadEmission()`), the precision every consumer stores it at anyway; `uvHalf` / `primitive` stay free for reflections' game-cubemap term on hybrid primary rays. The hit shader returns `prevWorld − curWorld` of the hit point, packed into one 32-bit word with a shared exponent (`PackMotion`: 3 × sign + 8-bit magnitude, 5-bit exponent; error ≤ max|d|/255, 0 = static). PathTrace writes it to `_InstanceId.y`: the instance texture is now `RGInt` (x unchanged). No 9th UAV, the payload stays 64 bytes, and readers that declare `Texture2D<int>` still see the instance in x.

**Where the hit shader gets the previous position** (`src/UltraRT/MotionVectors.cs`, owned by `SceneProxies`):
- Each dynamic proxy gets a **motion slot**, put in its RTAS custom id (`layer | slot << 8`, 16-bit slot; static batches are slot 0 and skip everything). Per slot, the global `_UltraRTMotion` holds the world-space motion matrix `W = M_prev · M_cur⁻¹` and a skinned-pose offset. Rigid proxies use their `localToWorldMatrix` (exactly the instance transform; updated only when `hasChanged`, reset to identity the frame after they stop). Skinned proxies use the root bone's position and rotation.
- **Skinned meshes**: after each trace, `MotionCopy.compute` copies every active skinned proxy's object-space positions out of its skinned vertex buffer (`vertexBufferTarget |= Raw`, `GetVertexBuffer()`) into one shared buffer `_UltraRTPrevSkin`. Next frame the hit shader interpolates the previous pose with the same triangle indices and barycentrics: `prevWorld = W · O2W_cur · prevPose`. Regions are recycled per vertex count when a proxy leaves the RTAS.
- Why a copy and not Unity's own previous buffer (`GetPreviousVertexBuffer`) or a second vertex stream: the previous buffer would have to be bound per instance through a property block that changes every frame (it is double-buffered), which hit groups would have to re-read; a stream would need the mesh layout changed. The copy is one tiny dispatch per active skinned mesh (12 bytes per vertex), the binding is one global, and the hit shader only reads it on primary hits of skinned instances.
- A proxy entering the RTAS has no motion for its first frame; a skinned one whose pose wasn't copied yet (first frame, buffer regrow) gets root motion only.

**Cost** (RTX 5070 Ti, see the bench numbers in the commit / below): the hit shader adds 4 float4 loads and a few matrix ops on primary hits of dynamic instances (3 extra float3 loads for skinned), one more 4-byte write per pixel, and one extra 4-byte read per pixel in each Temporal pass. CPU: a matrix inverse per moving instance per frame and one `SetData` over the changed slots.

**Debug view `motion`** (`DenoiserDebugView.Motion`, last in the Page Up/Page Down list): object motion as a flow colour over a dim grey image (hue = screen direction, saturation = speed, full at 8 trace px/frame); camera-only motion is a faint tint. `-ultrart-nomotion` turns motion off (all slots identity, no pose copies) for A/B; `-ultrart-motion-probe` logs which space Unity's skinned vertex buffer is in.

**Limits.** Normals are not rotated back, so a surface turning faster than `acos(reprojNormalTolerance)` (25°) per frame still loses its history. Motion is only for the primary hit; reflections of moving objects reproject with the reflecting surface's motion. Static-batched geometry never moves. Instances that teleport (checkpoint room clones) produce a huge motion that lands off-screen or fails the tests, which is the right result.

## Textures (owned by the denoiser)

| Name | Format | Content |
|---|---|---|
| hist | ARGBHalf | rgb filtered lighting (output of iteration 0), a variance |
| t0, t1 | ARGBHalf | integrated lighting and à-trous ping-pong |
| moments[2] | ARGBHalf | m1, m2, history length, temporal variance (ping-pong) |
| geo[2] | ARGBInt (R32G32B32A32_SInt) | packed world position, normal and instance (ping-pong) |
| boost | ARGB32 | debug channels for `dn-boost`: pre-blur, cache seed, search, firefly cut (written every frame, 3.7 MB) |

All of these are enableRandomWrite and point filtered. At 1280x720 they total about 70 MB. There are also two 8-slot dummy cache buffers, bound when there is no cache. They are reallocated, and the history is reset, whenever the radiance size changes.

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
| debugView | Final | `Final`, `FilteredLighting`, `RawLighting`, `Variance` (heatmap of the relative σ of the temporal estimate; blue is clean, red is σ ≥ 2× the mean), `HistoryLength` (heatmap of len / maxHistory), `Disocclusion` (red where history was rejected this frame, grey elsewhere), `SpatialBoost` (see above), `Motion` (see [Motion vectors](#motion-vectors)) |
| stability | true | Master switch for the four features below. false = the previous output, bit for bit. `-ultrart-dn-legacy`; Ctrl+F3 toggles both instances. |
| boostFrames | 8 | Pre-blur fades out at this history length. 0 = off. `-ultrart-dn-boost N` |
| boostRadius | 4 | Pre-blur radius in trace pixels at history 0. Clamped to 4 (tile apron). `-ultrart-dn-boost-radius R` |
| historySearch | true | 3x3 fallback search. `-ultrart-dn-nosearch` |
| cacheSeed | 1 | Cache seed weight in virtual samples at history 0. 0 = off. `-ultrart-dn-cache W` |
| cacheSeedTolerance | 0.5 | Relative tolerance of the seed's consistency test |
| fireflySigma | 8 | Firefly bound in spreads above the reference. 0 = off. `-ultrart-dn-firefly K` |

Other flags: `-ultrart-dn-noclamp` (clampHistory = false), `-ultrart-dn-iterations N`. Flags apply to both instances (main and hybrid's direct). They are read when a `Denoiser` is constructed; Plugin keeps the main instance's settings across F9 reloads.

## Cost (estimated, not yet measured)

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
- All five inputs have the same size and come from the same frame and the same `invViewProj` / `camPos` passed in `inputs`. The primary rays are **not jittered**. The progressive accumulation mode does jitter, so bypass the denoiser while accumulating.
- `normalDepth.w` is the ray t along the *normalized* primary direction and is negative for sky. The normal faces the camera.
- `radiance` is demodulated, so `albedo * radiance + emission` reproduces the undenoised image. Sky pixels have albedo 1.
- `instanceId` is an `RGInt` texture: x the instance (only the low 7 bits are compared), y the packed world-space motion (`PackMotion`, 0 = static).
- `Execute` is called once per frame, and the recorded commands run exactly once, in order, before the next `Execute`. Ping-pong indices advance at record time.
- The output must be enableRandomWrite and not sRGB. It is written with `saturate()` and no tonemapping.

## Known limitations
- The history clip uses a 1 spp neighbourhood, so it is loose by design. Fast lighting changes, such as muzzle flashes or toggled lights, take a few frames to settle (about `1/alphaMin` frames at worst). An A-SVGF gradient pass would fix this properly.
- Reprojection is bilinear, so the history is resampled slightly every frame while the camera moves. Lighting is smooth enough that this is hard to see, but it does slightly soften shadow edges in motion.
- Screen-edge disocclusions have no history at all. They start from the pre-blur (4 px radius at trace resolution) plus the cache seed, and are visibly softer than converged regions for the first few frames. Raise `boostRadius` only together with the tile apron (`APRON` in Denoise.compute; groupshared cost grows quadratically).
- The cache seed is biased wherever the cache and the input disagree by less than the consistency tolerance: at most about `cacheSeedTolerance` (50%) of the value, only for the first frames, and fading as `boost` does. The channel/offset table above is an approximation of what PathTrace adds; check `dn-boost`'s green against `-ultrart-dn-cache 0` in rooms with strong ambient.
- Instance ids are compared modulo 128, and the normal is quantised to 12-bit octahedral components.
