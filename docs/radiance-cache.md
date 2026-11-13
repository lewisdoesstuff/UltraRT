# Radiance cache (multi-bounce diffuse GI)

A world-space hash grid of cached lighting, modelled on NVIDIA's SHaRC (Spatial Hash Radiance Cache). Sparse update paths write multi-bounce lighting into cells every frame. PathTrace's bounce hit reads its lighting from the cell it lands in, instead of taking one RIS light sample.

Files:
- `unity/.../Shaders/UltraRTCache.hlsl`: keys, hashing, lookups (all shaders), and insert/accumulate (update only, `#define ULTRART_CACHE_WRITE`).
- `unity/.../Shaders/CacheUpdate.raytrace`: `UpdateRayGen` (the update paths) and `DebugRayGen` (the debug views). It is its own `.raytrace` file, so it has its own UAV budget.
- `unity/.../Shaders/CacheResolve.compute`: `Resolve` (temporal blend and eviction) and `Clear`.
- `unity/.../Shaders/PathTrace.raytrace`: one include plus the bounce block. Read-only: **adds no UAVs**.
- `src/UltraRT/RadianceCache.cs`: settings, buffers, update and resolve dispatch, lookup bindings for PathTrace, and the debug dispatch.
- `src/UltraRT/Plugin.Cache.cs`: Plugin wiring (arguments, shader loading, hotkeys, shared bindings for the update shader). Plugin.cs only has one-line hooks.
- `src/UltraRT/LightSampling.cs`: new `BindTables(cmd, shader)`. It binds this frame's alias and fill tables to another shader without advancing the ReSTIR ping-pong.

## Why

In 4-1 (about 30 point lights, ambient 0), the bounce was black speckle. Each pixel traced 1 bounce ray, and the bounce hit took 1 RIS light sample from 4 candidates. That sample was mostly zero, so the denoiser flattened the result to almost nothing. A bounce hit now reads an average of hundreds of path samples per cell, and that average includes further bounces. The bounce shadow ray goes away too.

## Hash grid

**Cell key** = (quantised position, LOD level, normal bin).
- **LOD**: `level = floor(log2(max(d / lodDistance, 1)))`, where `d` is the distance to the camera. `cell = cellSize · 2^level`. Cells are `cellSize` (0.5 m) within `lodDistance` (10 m), and beyond that they grow linearly with distance. At 1280x720 and the game's FOV, a cell then covers about 35–45 px wherever it is.
- **Normal bin**: the dominant axis of the shading normal, with its sign, giving 6 bins. ULTRAKILL is mostly axis-aligned, and the two faces of a thin wall always land in different cells. The normal faces the incoming ray (as in the hit shader), so each side of a two-sided surface has its own cells.
- **Normal offset**: the point is moved `normalOffset · cell` (half a cell) along its normal before it is quantised. Level geometry sits on round coordinates, so a floor at y = 0 would otherwise split between two cells, and the floor's samples would also bleed into cells on the far side of the plane.
- **Hashing**: the slot comes from a 4-round `HashU32` chain over (qx, qy, qz, level | bin << 4). A **32-bit checksum** from an independent PCG chain is stored in the slot (0 = empty). There are no 64-bit atomics (they need SM 6.6, which Unity's ray tracing compile doesn't target). A false match needs both hashes to collide, which is negligible.
- **Open addressing in aligned buckets of 8 slots** (32 bytes of keys, one cache line). Lookups scan the whole bucket. Inserts look for the key first, then `InterlockedCompareExchange(0 → checksum)` into the first empty slot. Because lookups scan the whole bucket, eviction can leave holes, and scanning for the key before inserting avoids duplicates after an eviction. A full bucket means the sample is dropped. With 2^20 slots and tens of thousands of live cells, that doesn't happen in practice.

**Memory** at `capacityLog2` = 20 (1,048,576 slots), 52 B per slot, **52 MB** in total:

| Buffer | Per slot | Content | Writers / readers |
|---|---|---|---|
| `_CacheKeys` | `uint` | checksum, 0 = empty | update (UAV), resolve (UAV: eviction); PathTrace (SRV) |
| `_CacheAccum` | 8 × `uint` | this frame's sums in fixed point (1/2048): total rgb, count, indirect rgb, last sampled frame | update (atomic adds), resolve (reads and clears) |
| `_CacheResolved` | `uint4` | half total rgb, half indirect rgb, half M (history samples), half average samples per frame | resolve (UAV); update and PathTrace (SRV) |

## What a cell stores

Lighting is demodulated, in the same game units as PathTrace's `lighting`:
- **total**: direct plus indirect light arriving at the surface. A bounce hit uses `albedo × lerp(1, total, litWeight) + emission`.
- **indirect**: the indirect part only, used by the primary-hit query mode, where ReSTIR already provides direct light.

Ambient (`_Ambient · _BounceAmbient`) is not in the cache. PathTrace still adds it at the bounce hit exactly as before, so a cache hit and a cache miss differ only in the light term (no seams between them).

## Update pass (`CacheUpdate.raytrace`, `UpdateRayGen`)

There is one path per `updateStride × updateStride` block of trace pixels (4 → 320 × 180 = 57.6k paths at 1280x720), starting from a random pixel in the block (jittered every frame), with its own primary ray. With `bounces` = 2:

```
x0 (primary hit)  : insert cell, direct0 = CacheDirect(x0), cosine bounce
x1 (bounce hit)   : insert cell, direct1 = CacheDirect(x1), cosine bounce
x2 (tail)         : total2 = feedback × cache(x2)          (cache hit)
                          = CacheDirect(x2)                (miss: cold start)
back-propagate, for i = 1, 0:
  incoming_i = emission_{i+1} + albedo_{i+1} × lerp(1, total_{i+1}, litWeight_{i+1})   (sky radiance on a miss)
  total_i    = direct_i + incoming_i
  cell_i    += (total_i, incoming_i)
```

- `CacheDirect` is `DirectOne` with `candidates` (12) RIS candidates over lights **and emissive triangles**, plus fill lights and 1 shadow ray. These are the same tables and target function as the primary hit, bound through `LightSampling.BindTables` and `EmissiveLights.Bind`. Hit emission is weighted by `_EmissiveBounceWeight`, as in PathTrace, so sampled emitters are not counted twice.
- **Infinite bounce with damping**: the tail reads the cache from previous frames, so every frame adds one more bounce of propagation. The cached tail is multiplied by `feedback` (0.8), and update paths clamp albedo to `albedoMax` (0.95). That keeps the loop contracting even in a closed room with white (albedo 1) walls. ULTRAKILL's light model isn't energy-conserving, so the damping is a safety margin, not a physical term.
- Samples are clamped to `maxSample` (64) per channel before the fixed-point add, which handles both fireflies and the fixed-point range: 32k max-valued samples per cell per frame before a uint overflows.
- The tail vertex doesn't write its own cell, because it has no fresh estimate.

## Resolve (`CacheResolve.compute`, `Resolve`, 1 thread per slot)

- Empty slot: early out after one 4-byte read.
- Sampled this frame (`count > 0`): `new = sum / count`, then a sample-weighted blend with the history, `M' = min(M, historyFrames × avgRate)` and `value = (hist·M' + new·count) / (M' + count)`. `avgRate` is a moving average of the cell's samples per frame. The history is therefore capped at **`historyFrames` (32) frames' worth of samples at that cell's own rate**: a cell 2 m away getting 1,000 samples per frame and a distant cell getting 2 converge equally fast in frames. One sparse frame doesn't wipe out a well-sampled history. The accumulators are then cleared and the frame is stamped.
- Not sampled for `staleFrames` (120) frames: evicted (key = 0, resolved = 0).
- **Light changes**: when `LightSampling.LightSetChanged` or `EmissiveLights.SetChanged` is set (a room toggling its lights, emitters appearing or disappearing), the cap drops to `changeHistoryFrames` (4) for the next `changeFrames` (4) frames, so a room that goes dark or lights up settles within about 4–8 frames.

Order within a frame (one command buffer): update `DispatchRays`, then `Resolve`, then PathTrace `DispatchRays`. PathTrace sees this frame's samples, and the update tail sees last frame's resolve.

## Query (PathTrace)

`_CacheMode` (`RadianceCacheSettings.mode`):

| Mode | PathTrace bounce |
|---|---|
| `Off` (0) | as before: `DirectOne` (1 RIS sample, 1 shadow ray) at the bounce hit |
| `Bounce` (1, default) | `hitLight = cache.total(hit)`, with `DirectOne` as the fallback when the cell is missing or has fewer than `minSamples` (2) samples. Saves the bounce shadow ray on a hit. |
| `Primary` (2) | no bounce ray: `indirect = cache.indirect(primary hit)`, read at a point jittered by ±½ cell (`queryJitter`) in the tangent plane, so the grid becomes noise for the denoiser instead of blocks. On a miss it falls back to the normal bounce. It is cheaper, but blurrier (cell resolution) and leaks more at contact edges. |

**Level fallback** (`levelFallback`): on a miss, the lookup also tries the neighbouring LOD level, the coarser one if `frac(level) ≥ 0.5`, otherwise the finer one. As the camera moves, cells change level and the new level starts cold, while the neighbour is usually warm.

## Debug views

There are two new view modes at the end of the F8 list (existing indices are unchanged). They are written by `DebugRayGen` over PathTrace's output, using the same primary rays as PathTrace.

| Name | Shows |
|---|---|
| `cache` | `albedo × lerp(1, cached total, litWeight) + emission` at the primary hit: what a bounce ray would see. Magenta checker = no valid cell. |
| `cache-cells` | a hash colour per cell, darker on odd LOD levels (LOD rings); grey = not in the table |

`-ultrart-cache-debug N` or **Shift+F4** forces the view used by both modes: 1 radiance, 2 total lighting, 3 indirect lighting, 4 cells, 5 sample count (log heat map, blue ≈ 1 to red ≈ 1000). **F4** cycles the cache mode: Bounce → Primary → Off.

## Settings (`RadianceCacheSettings`) and flags

| Field | Default | Flag |
|---|---|---|
| `enabled` | true | `-ultrart-nocache` |
| `mode` | Bounce | `-ultrart-cache-mode bounce\|primary\|off` |
| `cellSize` (m) | 0.5 | `-ultrart-cache-cell N` |
| `lodDistance` (m) | 10 | `-ultrart-cache-lod N` |
| `updateStride` | 4 (1 path / 16 px) | `-ultrart-cache-stride N` |
| `bounces` | 2 (1–4) | `-ultrart-cache-bounces N` |
| `historyFrames` | 32 | `-ultrart-cache-history N` |
| `feedback` | 0.8 | `-ultrart-cache-feedback F` |
| `capacityLog2` | 20 (52 MB; clamped to 12–22) | `-ultrart-cache-size N` |
| `debugView` | None | `-ultrart-cache-debug N` |
| `candidates` 12, `changeHistoryFrames` 4, `changeFrames` 4, `staleFrames` 120, `albedoMax` 0.95, `maxSample` 64, `minSamples` 2, `queryJitter` 1, `levelFallback` true, `normalOffset` 0.5 | | |

Changing `cellSize`, `lodDistance`, `normalOffset` or `capacityLog2` clears the cache, because every key changes. With `enabled = false` the buffers are released, and PathTrace binds 8-slot dummies.

## UAV budget (verified with DXC's resource-binding dump)

| Shader | UAVs |
|---|---|
| `PathTrace.raytrace` | **8, unchanged**: `_Output _Radiance _AlbedoOut _EmissionOut _NormalDepth _InstanceId _HybridLight _ReservoirCur`. The cache adds 2 SRVs (`_CacheKeys`, `_CacheResolved`), for 14 SRVs in total including the RTAS and sky. |
| `CacheUpdate.raytrace` | **3**: `_CacheKeys _CacheAccum _CacheDebugOut`. `_ReservoirCur`, declared by UltraRTLights.hlsl, is stripped as unused, and so are `_LightRemap`, `_ReservoirPrev` and `_EmissiveActive`. Binding them anyway (BindTables, EmissiveLights.Bind) is harmless. |
| `CacheResolve.compute` | 3 (compute, no RT limit) |

## Integration (already wired)

Plugin.cs has one-line hooks only. Everything else is in `Plugin.Cache.cs`:
1. `Modes`: `cache` and `cache-cells` appended (`mode` = 100 + `RadianceCacheDebug`). `_Mode` gets 0 for them.
2. `Awake`: `ParseCacheArgs();` after `ParseArgs();`.
3. `LoadShaders`: `LoadCacheShaders(bundle);`. Missing cache shaders only log a warning and leave the cache off; the bundle still loads.
4. `ResetScene`: `_radianceCache?.Reset();` (scene change and shader reload).
5. `OnUpdate`: `CacheHotkeys();` (F4, Shift+F4).
6. `Trace`, at the end of the `if (pt)` bind block: `TraceRadianceCache(shader, w, h, invViewProj, ambient);`. It binds the scene, lights, light tables, emitters and sky on the update shader, records update and resolve, and binds the lookups on PathTrace. It must come **after** `_emissive.Bind(_cmd, shader, ...)` for PathTrace, because `EmissiveLights.Bind` consumes its history-valid flag on the first call of the frame. It must run on every PathTrace dispatch, since PathTrace declares the lookup inputs.
7. `Trace`, after PathTrace's `DispatchRays`: `if (kind == Kind.PathTrace) TraceCacheDebug(mode);`.
8. GPU timer: new slot `cache` (5), which wraps update + resolve. It appears in the bench JSON `GpuMs` and in `TakeGpuSummary`. The PathTrace query cost is inside `dispatch`.
9. The "First trace OK" log line reports the cache mode, slots and MB.

## Cost (estimated, not measured)

At 1280x720 trace resolution, stride 4, 2 bounces:
- **Update**: 57.6k paths, each 1 primary + 2 bounce + 2 to 3 shadow rays, about 300k rays. That is under 10% of PathTrace's about 3.7M rays (which take about 0.5 ms), so **about 0.05–0.1 ms**. Plus about 14 atomics per path. Atomics on hot cells near the camera (a few hundred samples per cell per frame) serialise in L2, but that adds only microseconds.
- **Resolve**: 1M threads, mostly empty slots (one 4-byte read each), plus 48 bytes per live cell. **About 0.02–0.04 ms.**
- **Query**: per bounce hit, 2 hash chains, one 32-byte key line and a 16-byte read. This replaces `DirectOne` (4 candidates plus a shadow ray), so in `Bounce` mode PathTrace should get slightly *cheaper* once the cache is warm. `Primary` mode removes the bounce ray as well.
- **Total**: the target was ≤ 0.5 ms; about 0.1–0.15 ms is expected. Measure with `-ultrart-gputimer` (slots `cache` and `dispatch`).
- **Memory**: 52 MB at 2^20 slots (26 MB at `-ultrart-cache-size 19`, which is probably enough for any single level).

## Risks and limitations

- **Light leaking through thin walls**: opposite faces of a wall never share a cell (normal bins). The remaining leak is between **same-facing surfaces on both sides of a wall inside one cell**, typically the floor strip along the base of a wall separating a lit room from a dark one. It is ≤ ½ cell wide near the camera, and wider with distance (cells are 2 m at 40 m). Lower `cellSize` (0.25) reduces it, at the cost of more noise per cell. A cell is also shared by any same-facing surfaces inside its volume, such as a ledge and the floor just below it.
- **Stale cells on room toggles**: handled by the short history after `LightSetChanged` / `SetChanged` (about 4–8 frames). Changes that don't add or remove a light, such as intensity animation or a light moving, converge at the normal rate: about 32 frames (0.5 s at 60 fps) to 1/e. Lower `historyFrames` trades lag for noise. Cells out of view aren't updated; they keep their old value until evicted (120 frames) or refreshed.
- **Moving lights / muzzle flashes**: the primary hit's direct light is unaffected (ReSTIR). Only the bounce lags.
- **Cold cells**: when the view changes (teleport, fast turn, LOD change), the bounce falls back to `DirectOne` for a frame or two (level fallback softens LOD changes). In debug view `cache`, cold cells show as the magenta checker.
- **Brightness changes**: the bounce is now multi-bounce and much stronger than the old 1-sample estimate, which was also biased dark in practice. Hybrid's `GIStrength` (0.35) and `EmissiveLightScale` were tuned against the old bounce and may need retuning. `-ultrart-cache-feedback 0` turns off the infinite-bounce feedback: each path then carries exactly `bounces` bounces (the tail contributes only its emission and fullbright term), which is useful for A/B.
- **Primary mode** is cell-resolution GI. Contact shadows and corners are washed out, and leaking is larger, because the query happens at the visible surface itself.
- **Cache keys use the dominant axis only**: surfaces at exactly 45° can alternate between two bins. This is harmless (it only splits samples).
- **SRV count in PathTrace** rises from 12 to 14. No SRV limit is known for Unity ray tracing shaders. If `DispatchRays` ever fails with the cache on and works with `-ultrart-nocache`, check this first.
- **Unity resource barriers**: the update writes `_CacheKeys` and `_CacheAccum` as UAVs, the resolve then uses them as UAVs, and PathTrace reads `_CacheKeys` as an SRV in the same command buffer. This relies on Unity inserting the D3D12 UAV and state barriers between dispatches, as it does for the ReSTIR reservoirs and the denoiser.
- **Untested in the game**: this was compiled and checked statically only.

## Test steps

1. Build and deploy: `scripts\build.ps1`, or build the shaders and plugin and copy `ultrart.shaders` and `UltraRT.dll` into `BepInEx\plugins\UltraRT`. The log line `Bundle loaded: ...` should have no cache warning. `First trace OK: ... cache=Bounce 1048576 slots 52 MB`.
2. **4-1 indoor poses**: `-ultrart-scene "Level 4-1" -ultrart-pose ... -ultrart-gputimer`
   - `pt-indirect` with and without `-ultrart-nocache` (or F4 to cycle): the bounce should go from black speckle to a warm, smooth fill.
   - `cache` view: rooms should be fully covered within a few frames, with no magenta. `cache-cells`: about 0.5 m cells nearby and LOD rings further away. Shift+F4 → 5 (samples): red/yellow near, blue far.
   - `hybrid`, and `pt-dn` with A/B via F4: check `GIStrength` and the brightness.
   - Leak check: stand next to a wall between a lit and a dark room. In the `cache` view, check the floor strip on the dark side.
   - Room toggle: walk through a door that switches rooms. The bounce should settle within about 8 frames.
3. **1-1** via the bench harness: add `"cache"` to a scenario's modes if wanted. Compare `GpuMs.cache` and `GpuMs.dispatch` with and without the cache (`-ultrart-nocache` as the bench tag run). The expected `cache` slot is ≤ 0.15 ms at a trace scale giving 1280x720.
4. Mode A/B: `-ultrart-cache-mode primary` (cheaper, blurrier); `-ultrart-cache-cell 0.25` (less leak, more noise); `-ultrart-cache-bounces 3`; `-ultrart-cache-history 16` (less lag).
