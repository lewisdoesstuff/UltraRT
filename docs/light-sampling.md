# Light sampling (RIS + temporal ReSTIR DI)

Direct lighting with a fixed number of shadow rays per pixel, independent of the light count.

Files:
- `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/UltraRTLights.hlsl`: the light model (moved out of `PathTrace.raytrace`), the reference `DirectAll`, RIS, reservoirs and temporal reuse. Included by `PathTrace.raytrace`.
- `src/UltraRT/LightSampling.cs`: settings, the per-frame alias table and fill-light list, the light index remap between frames, and the ping-ponged reservoir buffers.

## Why

`DirectAll` shoots one shadow ray to every light in range of the pixel. Level 8-3 has 100+ active point and spot lights, so `DispatchRays` costs about 2 ms at 1280x720 on an RTX 5070 Ti, against about 0.5 ms elsewhere.

## Modes (`LightSamplingSettings.mode`, shader `_LightSampling`)

| Mode | Primary hit | Bounce hit | Shadow rays per pixel (primary + bounce) |
|---|---|---|---|
| `Reference` (0) | `DirectAll`: every light, one ray each | one uniformly picked light, ×N | lights in range + 1 |
| `Ris` (1) | RIS over M candidates, 1 ray | RIS over `bounceCandidates`, 1 ray | 1 + 1 |
| `ReSTIR` (2, default) | RIS, then merged with the previous frame's reservoir, 1 ray | as `Ris` | 1 + 1 |

`Reference` is the old code path: the same math and the same random sequence. Use it for A/B comparisons and as ground truth with `-ultrart-accumulate`. ReSTIR's temporal reuse is correlated across frames, so accumulating it converges to a slightly different (biased) image.

## Light model

`LightEval(i, p, n, layer)` is the old `LightFrom` without the visibility test: culling mask, `N·L > 0`, attenuation `1 / (1 + d²·25/r²) · spot · saturate(2 − d/r)`, and the same `atten < 1e-3` cut-off. It returns the unshadowed contribution `c`, the shadow direction and the shadow ray length. `LightFrom` is `LightEval` plus `Visible` for shadowed lights, and `DirectAll` sums `LightFrom`, as before.

**Target function** p̂(i) = luminance(c), so the resampling follows the game's own falloff, cone, culling mask and N·L.

**Fill lights** (`_Lights[i*4+3].z > 0.5`, the directional lights from below the horizon) are unshadowed and only a few, so they skip RIS. `FillLighting` evaluates all of them at every hit, with no rays. Shadowed directional lights do go through RIS: their p̂ is their colour times N·L, with no distance falloff.

## Candidate generation (RIS)

Each frame the CPU builds a Vose **alias table** over the shadowed lights. Drawing a candidate costs one 16-byte read: entry `(threshold, light | aliasLight << 16, pdf(light), pdf(aliasLight))`.

The source pdf is `(1 − uniformMix) · w_i / Σw + uniformMix / N`, with:
- `w_i = luminance(colour·intensity) · R² / (R² + max(0, |camPos − lightPos| − R)²)`, where `R = 2·range`, the radius at which ULTRAKILL's fade reaches zero. Directional lights use `w = luminance`.
- With `importance = false`, the source pdf is uniform.
- The uniform mix (default 0.2) keeps every light reachable. This matters for lights far from the camera that still light a distant visible wall, and for bounce hits outside the view.

Each pixel draws `candidates` (M, default 8) lights. Each candidate gets weight `p̂/pdf` and goes through single-sample weighted reservoir sampling. The final weight is `W = wSum / (M · p̂(y))`, and the estimate is `c(y) · V(y) · W`. The bounce hit does the same with `bounceCandidates` (default 4) and no reuse.

**Portals** (`portalImportance`, default on). The camera's distance to a light assumes the camera sees the world around it. Through a portal it sees a place that can be hundreds of metres away in world space: 8-2's fake mirrors (`4 - Hub/MirrorBig/Portal`) join x = 19 and x = 281, so every light of the room seen "in the mirror" was 262 m from the camera and was drawn almost only through the uniform mix (0.2 / 110 lights). `SceneProxies.PortalViewpoints` adds the camera's image through each portal side it can look into (in front of the side, within 120 m, at most 8): travel matrix × camera position. A light's weight is the largest over the camera and these viewpoints. Bench: `ls.portalImportance=0`. Results in docs/portals.md, "Noise behind portals".

## Temporal reuse

**Reservoir buffer.** `ComputeBuffer` of `uint4` per pixel at trace resolution, ping-ponged: `_ReservoirPrev` is read as an SRV and `_ReservoirCur` is written as a UAV.

| Field | Content |
|---|---|
| x | light code (17 bits: light index, or `0x10000` \| emissive triangle; `0xFFFF` = none) \| uint(M) << 20 (12 bits) |
| y | asuint(W) with its 10 low mantissa bits holding the emissive sample's barycentric u; W = 0 if the final shadow ray was blocked (see below) |
| z | octahedral normal, 2 × 11 bits \| barycentric v << 22 (10 bits) |
| w | asuint(dot(n_quantised, worldPos)): the surface plane |

This layout changed with emissive lights (it used to be a 16-bit light index, half(M), and a 2 × 16-bit normal); see `docs/emissive-lights.md`. M is now an integer capped at 4000 (`_RestirParams.x`).

Sky pixels write `(0xFFFF, 0, 0, 0)`, and M = 0 is never reused. At 1280x720 each buffer is 14.7 MB, so 29.5 MB in total. The buffers are only allocated in `ReSTIR` mode. Other modes bind two 1-element dummies, never the same buffer as both SRV and UAV.

**Reprojection.** Uses the same convention as the denoiser:
1. `pos` is the primary hit, `camPos + dir·t`.
2. `clip = _PrevViewProj · (pos + motion, 1)` and `pixel = floor((clip.xy/clip.w · 0.5 + 0.5) · dim)`. `motion` is the hit's world-space motion (0 for static geometry; docs/denoiser.md, "Motion vectors"); the plane test below also uses `pos + motion`.
3. Reservoirs can't be blended, so this is a nearest-pixel fetch, not bilinear.

**Validation.** A history reservoir is used only if:
- M > 0
- `dot(n, n_prev) ≥ normalTolerance` (0.9)
- `|dot(n_prev, pos) − plane_prev| ≤ planeTolerance · t + 0.01` (0.01)

The plane is stored against the quantised normal, so the test is exact for a static point.

**Merge.** The history's M is clamped to `historyLimit × candidates` (20 × 8 = 160). Its light is remapped to this frame's index and re-evaluated at the current pixel, then added to the reservoir with weight `p̂_current(y) · W_prev · M_prev`.

**Bias: biased 1/M combine.** There is no MIS or 1/Z over the history domain, so no extra target evaluations or rays are needed. The bias is limited to pixels where the history's domain differs from the current pixel's: a light whose p̂ is 0 there but not here, which can only happen at validation-passing geometric edges or when a light moves. Validation keeps this small.

**Visibility reuse with the one ray.** If the final shadow ray is blocked, the stored W is 0, but the light and M are kept. Here is why the static case stays unbiased. The history then contributes `V·p̂·W·M`, and the shading applies V again. Since V² = V, the combined estimate is still `∫ p̂·V` / (pixel's total M) per unit of M, the same expectation as fresh RIS with visibility. An occluded sample therefore stops propagating instead of being re-picked every frame, which cuts noise in shadowed areas. The cost is lag when an occluder moves away: at most `historyLimit` frames' worth of M.

**Light identity: remap, not stable slots.** `_Lights` stays a compact per-frame list in GatherLights order, which changes as rooms toggle lights. Each frame `LightSampling` records the `Light.GetInstanceID()` of every packed index and builds `_LightRemap[prevIndex] → currentIndex`, with `0xFFFF` for a light that is gone. Stable sparse slots would have left holes in the list. A remap keeps the alias table, `_LightCount`, `DirectAll` and the reference path exactly as they are, at the cost of one 256-entry upload and a dictionary lookup per light per frame.
- If a reservoir's light is gone, that history is **dropped** (M not counted). Counting its M with zero weight would darken the pixel, since the history also stood for the other lights it did not pick.
- On frames where any light was added or removed (`LightSetChanged`) and `shortenHistoryOnLightChange` is on, the history M is shortened so that a light switched on with its room shows up within a frame or two instead of after up to 20 frames. By default the cap drops to 1× `candidates` over the whole screen for that frame; `perPixelLightChange` does it only where the change matters ([Light changes](#light-changes)).

### Light changes
Effect lights (docs/emissive-lights.md, "Effect lights") make the light set change many times a second in combat: every muzzle flash, beam, projectile and explosion adds a light and removes it a few frames later. With the whole-screen cap, each of those frames threw away the reservoir history of every pixel, including pixels the light can't reach, and ReSTIR needs about 20 frames to build it back. With `perPixelLightChange` (off by default: the motion-noise runs showed no reliable noise gain, docs/denoiser.md "Motion noise"; `-ultrart-restir-pixel-change`):
- `LightSampling` keeps the previous frame's packed light data and builds a **change list** (`_LightChanges`, at most `MaxChanges` = 16 per frame): for each light that appeared or disappeared, its data before and after (an absent side has zero colour).
- At the primary hit, before the temporal merge, `RestirChangeCap` evaluates each change at the pixel with the normal light model (`LightEvalRaw`, no rays): `rho` = sum of |p̂ now − p̂ before| / (the fresh candidates' estimate of the total, wSum / M, plus p̂ before). The history may keep at most a share `h = lightChangeTolerance / rho` (0.1) of the merged reservoir, so the cap is `M_fresh · h / (1 − h)`, clamped to [M_fresh, the normal cap]. Where the changes contribute less than 10% the history is untouched; where they dominate it is cut to 1× `candidates`, as before.
- More than 16 changes in one frame, or a change of the emissive triangle set, fall back to the whole-screen cap.
- `lightChangeKept` (off) also lists lights that stay but change brightness (`lightChangeIntensity`, 10% per frame) or move (`lightChangeMove`, 5% of the range). Measured noisier: a fading muzzle light then shortens the history around it on every frame of its fade, while the stale share it leaves in a full history is small and smooth.
- Cost: on frames with changes, two light evaluations per change per pixel (16 changes at most); nothing otherwise.

### Penumbrae
User report: in 3-1's ribbed cave ("3-1 noisy room", which needs `"Load": "checkpoint"`: its approach ends in the void), the walkway showed a smooth, slowly moving noise pattern while standing still. Burst (64 frames after a 480-frame settle, the walkway region): ReSTIR boils there, every light evaluated (`lights=ref`) is calm. The walkway is lit through the gaps between the ribs, i.e. it is one large penumbra. With soft shadows (a random point on the light, or the sun disk) each frame's shadow ray is one random visibility draw, and a blocked draw was stored with W = 0 and the full history M (up to `historyLimit` x `candidates` = 160): the pixel stayed dark for about 20 frames, then lit for about as long, independently of its neighbours, which the à-trous turned into drifting blobs. `occludedHistory` (2) caps the M an occluded history carries at that many x `candidates`, so a blocked draw is one vote among a few frames instead of twenty. Unshadowed pixels and fully shadowed ones are unaffected in expectation (a fully shadowed pixel still gets W = 0 every frame). Bench: `ls.occludedHistory=0` (no cap).

RESULTS_PENUMBRA

### Moving occluders
A reservoir that was occluded last frame keeps its M with W = 0 (visibility reuse, above), so when an occluder moves away the pixel stays dark for up to `historyLimit` frames of M: a walking player or enemy left a trail behind its shadow. `SceneProxies.CollectMovers` (`SceneProxies.Movers.cs`) uploads every frame up to 8 boxes of dynamic objects that moved in the last 3 frames (V1's model first, then the nearest moving enemies and props within 60 m, at most 16 m in size), each the union of this frame's and last frame's bounds (`_UltraRTMovers`, `_UltraRTMoverCount`; PathTrace only, `ULTRART_MOVERS`). In `RestirMerge`, a history sample whose shadow segment crosses a box is re-weighted to at most `candidates` of M, and dropped when it was occluded. The final sample's crossing is flagged in `_Radiance.a` (+2) for the denoiser (docs/denoiser.md, "Moving shadows"). Bench: `plugin._movers=0` (also `plugin._moverOthers=0`: the player only).

### Fresh pixels
A pixel whose temporal reservoir failed validation (a disocclusion, the screen edge while turning, the first frame) used to show an 8-candidate RIS sample. `freshCandidates` (24) adds that many candidates to it (`RestirReuse` returns whether the temporal reservoir merged; `ReservoirCombine` adds the second RIS stream). Light evaluations only, no rays, and only on those pixels. In the motion-noise measurements this alone cut the 180°/s sweep's noise by a fifth (docs/denoiser.md, "Motion noise"). Flag: `-ultrart-restir-fresh N` (0 = off).

**Spatial reuse (optional, `spatialSamples`, default 0).** A true spatial pass needs a second dispatch after all temporal reservoirs are written. Instead, k extra reservoirs are taken from the **previous** frame inside a disk of `spatialRadius` pixels (default 12) around the reprojected pixel. Each uses the same validation and merge. This fits in the single RayGen with no extra rays. It is biased at shadow boundaries, because a neighbour's W reflects its own visibility, so it is off by default in relight and pt-dn: there it made 1-1's sun shadow edges boil more. Hybrid uses `spatialSamplesHybrid` (2, radius `spatialRadiusHybrid` 6 px), because it only takes ReSTIR's shadowed/unshadowed ratio, where it cuts the slow boiling by about a third (docs/denoiser.md, "Static noise"). Flags: `-ultrart-restir-spatial N`, `-ultrart-restir-spatial-hybrid N`.

## Cost (estimated, not measured)

Per pixel in `ReSTIR` mode:
- 4 rays: primary, 1 shadow, bounce, 1 bounce shadow. The reference uses `2 + lights in range`.
- `candidates + bounceCandidates + 1` light evaluations: 12 × (1 alias read + mask + up to 4 float4 reads).
- 16-byte reservoir read and write, plus `fillCount` × 2 unshadowed evaluations.

8-3 should then cost about the same as other levels, about 0.5–0.7 ms at 1280x720 instead of about 2 ms, and the cost no longer scales with light count. The CPU cost per frame is building the alias table (O(N), N ≤ 256) plus three small `SetData` calls.

**Noise.** Direct lighting is no longer deterministic. `docs/denoiser.md` assumes that it is ("Why 3x3 is the default"). With 8 candidates plus up to 160 history samples, the remaining noise is mostly in penumbrae and in pixels lit by many similar lights. The denoiser's temporal pass and 3x3 à-trous should absorb it. If not, raise `candidates` (cheap, no rays), try `spatialSamples = 2`, or set `Denoiser.Settings.wideKernel`.

## Integration (Plugin.cs)

`LightSampling` binds everything `UltraRTLights.hlsl` declares beyond `_Lights`, `_LightMasks` and `_LightCount`. **It must be bound on every PathTrace dispatch, in every mode**, or Unity reports unset properties.

1. Fields, next to `_lightMaskBuffer`:
   ```csharp
   private LightSampling _lightSampling;
   private readonly Light[] _packedLights = new Light[MaxLights];
   private readonly LightSamplingSettings _lightSamplingSettings = new LightSamplingSettings(); // filled by ParseArgs
   ```
2. `Awake()`, right after `_lightMaskBuffer = new ComputeBuffer(MaxLights, 4);`:
   ```csharp
   _lightSampling = new LightSampling(MaxLights) { Settings = _lightSamplingSettings };
   ```
3. `GatherLights()`, in the packing loop, just before `_lightCount++;`:
   ```csharp
   _packedLights[_lightCount] = l;
   ```
   Also change the layout comment there: the `_Lights` layout is now documented in `UltraRTLights.hlsl`.
4. `Trace()`, inside `if (pt) { ... }`, after `_cmd.SetRayTracingIntParam(shader, "_LightCount", _lightCount);`:
   ```csharp
   _lightSampling.Bind(_cmd, shader, w, h, viewProj, _camera.transform.position, _packedLights, _lightData, _lightCount);
   ```
   `viewProj` is the existing local, `_camera.projectionMatrix * _camera.worldToCameraMatrix`, whose inverse is `_InvViewProj`. `w` and `h` are the dispatch size. `LightSampling` remembers its own previous `viewProj`, so there is nothing else to pass.
5. Resets: call `_lightSampling?.Reset();` wherever `_denoiser?.Reset()` / `_denoiser.Reset()` is called:
   - `ResetScene()`, which also covers shader reload
   - the F7 toggle
   - the `denoised && _lastKind != Kind.Denoised` line. Better: reset whenever the previous traced frame was not a PathTrace kind, e.g. `if (pt && _lastKind == Kind.Debug) _lightSampling.Reset();` before `_lastKind = kind;`.
   - Respawns and teleports. Validation would mostly reject the history anyway.

   Resolution changes and mode switches are handled internally.
6. Optional, for the bench and A/B: arguments in `ParseArgs()`:
   ```csharp
   case "-ultrart-lights": // ref | ris | restir
       switch ((Next() ?? "").ToLowerInvariant())
       {
           case "ref": case "reference": _lightSamplingSettings.mode = LightSamplingMode.Reference; break;
           case "ris": _lightSamplingSettings.mode = LightSamplingMode.Ris; break;
           default: _lightSamplingSettings.mode = LightSamplingMode.ReSTIR; break;
       }
       break;
   case "-ultrart-ris-m": int.TryParse(Next(), out _lightSamplingSettings.candidates); break;
   case "-ultrart-restir-spatial": int.TryParse(Next(), out _lightSamplingSettings.spatialSamples); break;
   ```
   Also a hotkey in `OnUpdate()` (F6 is free):
   ```csharp
   if (Input.GetKeyDown(KeyCode.F6))
   {
       var s = _lightSampling.Settings;
       s.mode = (LightSamplingMode)(((int)s.mode + 1) % 3);
       _accumFrames = 0; _denoiser?.Reset();
       Logger.LogInfo($"Light sampling: {s.mode}");
   }
   ```
   Consider adding `_lightSampling.Settings.mode`, `RisLightCount` and `FillLightCount` to the HUD and to the "First trace OK" log line.
7. Teardown, if the plugin ever disposes its buffers: `_lightSampling.Dispose();`.

### UAV budget
Unity's RayTracingShader appears to allow at most 8 UAVs per `.raytrace`. A 9th makes `DispatchRays` fail. Light sampling adds exactly **one** UAV, `_ReservoirCur` (`RWStructuredBuffer<uint4>`). Everything else it binds is a read-only SRV: `_ReservoirPrev`, `_LightAlias`, `_FillLights` and `_LightRemap`. The ping-pong swaps which `ComputeBuffer` goes into each slot every frame. Keep the PathTrace total at 8 or fewer: with the main agent's 7 RW outputs plus `_ReservoirCur`, it is exactly 8. (Reflections kept it at 8 by packing albedo and emission into `_Surface` to make room for `_Reflection`; see docs/reflections.md.)

### Assumptions
- `Bind` is called once per traced PathTrace frame, after `GatherLights()`, and the recorded commands run exactly once, in order. The reservoir ping-pong and the light-index history advance at record time.
- `_packedLights[i]` is the `Light` whose data is at `_lightData[i*4 .. i*4+3]` for `i < _lightCount`. Entries past `_lightCount` are ignored.
- The primary rays are the same as the denoiser's: through pixel centres via `_InvViewProj` (jittered only while accumulating).
- `MaxLights` ≤ 65535, since light indices are 16 bits in the reservoir and the alias table.

## Known limitations / next steps
- No true spatial pass. Doing one needs RayGen split into two dispatches, with the reservoirs and G-buffer written by the first.
- The bounce uses plain RIS with no reuse. A light-tile or world-space grid of reservoirs would help when dim rooms are mostly bounce-lit.
- The importance heuristic ignores the spot cone and frustum visibility. Culling each light's 2·range sphere against the frustum (for primary hits) would sharpen the source pdf in 8-3.
- The biased 1/M combine. Pairwise MIS would remove the remaining edge bias at the cost of one extra target evaluation per reused reservoir, plus storing the history position.
