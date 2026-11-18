# Scene proxies and static-batch activation

The game's shaders have no ray tracing hit shader, so `SceneProxies` mirrors the scene with hidden proxy renderers that use ours (see the class comment in `src/UltraRT/SceneProxies.cs`). This page covers static-batched geometry (`src/UltraRT/StaticGroups.cs`): how it follows the game hiding and showing rooms.

## The problem

ULTRAKILL hides and shows whole rooms and areas by deactivating GameObjects: room and area triggers (`ObjectActivator` and roughly 60 other components that use `UltrakillEvent`), doors (`Door.activatedRooms` and `deactivatedRooms`), checkpoints and arenas. In 8-3 this covers entire regions. Dynamic proxies are children of their sources, so they follow these changes. Static-batched geometry instead shares a few combined meshes, and used to be mirrored by instances that never changed. Hidden rooms therefore stayed in the RT image and kept casting shadows.

## Model

- **Slot**: one submesh of a combined mesh. `StaticSceneOptimizer` gives each source renderer exactly one submesh (`SetStaticBatchInfo(r, first, 1)`). Unity's own static batching gives each renderer a contiguous run of submeshes.
- **Sources**: a slot is visible while *any* of its source renderers is `enabled` and `activeInHierarchy`. Several renderers can share a slot because `CheckPoint` clones its rooms. `Instantiate` keeps the static batch info, so the clone draws the same combined submesh. The checkpoint then deactivates the original (and moves it +10000 on x, which static batches ignore), and destroys and re-clones the room on every respawn. Ownership therefore can't be tied to the original objects.
- **Group**: an activation group, a partition of a combined mesh's slots. Each group has up to 8 parts (two-sided × sky wall × transparent, which need their own instance for culling and the instance mask; transparent = water/glass/blood slots under mask 0x10, docs/reflections.md section 12). Each part is one proxy instance whose mesh contains only that part's slots, merged to **one submesh per material**, as before.
  - Whole group visible: the prebuilt "full" mesh, added with `Rtas.AddInstance`.
  - Nothing visible: `Rtas.RemoveInstance`. Its shader-table records disappear too.
  - Partly visible: a mesh of just the visible slots, built on the CPU from a copy of the combined mesh taken at load (no GPU readback at runtime). The last two are cached, so re-entering a room in the same state, or a source that keeps switching on and off, reuses them. At most 4 are built per frame and the rest wait. Replaced meshes are destroyed 4 frames later.
- **Flags mode** (fallback): if a combined mesh can't be read back, or with `-ultrart-nomerge`, it keeps its original layout with one group, and visibility is applied through submesh flags. Correct, but it costs one record per visible submesh.

## Detecting changes (per frame, in `SyncDynamic`)

1. **Checkpoint clones**: each `CheckPoint.newRooms` list is compared by reference. A new clone's toggle targets, checkpoints and static-batched renderers (extra sources of known slots) are registered at once. Its other renderers go to the proxy queue (see [New objects](#new-objects)).
2. **Watchers**: `activeInHierarchy` is polled for every watcher, every frame (about 30 ns each, capped at 1500). A watcher is a source's deepest known *toggle point* below depth 2, or its depth-2 ancestor. When a watcher turns off, all its sources go invisible without being checked one by one; when it turns on, they are re-evaluated. Because a deeper watcher's `activeInHierarchy` includes every ancestor, toggles at or above the watcher are picked up in the same frame.
3. **Slice poll**: 256 sources per frame are checked (`renderer.enabled`, `activeInHierarchy`, destroyed). This catches what the watchers can't: `renderer.enabled` toggles, toggles below the watcher, and destroyed walls. A full cycle takes `sources / 256` frames (about 6 frames for 1-1's 1,441 sources).
4. Groups whose visible set changed are added, removed or rebuilt.

**Toggle points** are collected at load by reflection over every Assembly-CSharp `MonoBehaviour` (field lists cached per type):
- every `UltrakillEvent` (and arrays and lists of them), via `toActivateObjects` and `toDisActivateObjects`;
- GameObject fields, arrays or lists named `toActivate`, `toDisactivate`, `toDeactivate`, `activatedRooms`, `deactivatedRooms`, `rooms`, `defaultRooms`, `newRooms`, `roomsToInherit`, `toAppear` and similar;
- any ancestor that is inactive at build time.

Polling was chosen over event trackers on purpose. A tracker would have to be a child GameObject under each room. `Instantiate` would copy it into room clones, game scripts that walk children (such as `CheckPoint`'s room inheritance) would see it, and it would shift the child indices the bench's `AreaState` paths depend on. A per-frame poll of a few hundred `activeInHierarchy` reads costs microseconds and touches nothing.

## Choosing the grouping

The keys come from each slot's first source, and groups are formed per combined mesh:

| Level | Group key | Notes |
|---|---|---|
| 0 | the watcher (toggle point) | Groups under 96 tris use the depth-2 key instead, because each costs a record |
| 1 | ancestor at depth 2 (`Pre-Space/Rooms/10B - Night Street`) | |
| 2 | ancestor at depth 1 | |
| 3 | scene root object | |
| 4 | none: one group per combined mesh | Old instance layout, but every toggle is a partial rebuild |

The finest level whose total records (all groups visible) fit `StaticRecordBudget` (384) is used. Only groups currently in the RTAS cost anything per frame. Each record costs about 0.4 µs a frame: cutting 1,300 records saved about 0.5 ms. For comparison, 1-1 had 34 static records (169 in total) before this change.

Correctness never depends on the level. Visibility is always tracked per slot. A coarser level only means more partial rebuilds, which cost CPU on the toggle frame and a BLAS rebuild of that group on the GPU.

## Failure modes and limits

- **A toggle below the watcher, or `renderer.enabled`**: the scene is correct after at most one slice cycle (`sources / 256` frames). Examples: a destroyed wall, or an object toggled by a script through a field not in the name list.
- **A level with an unusual hierarchy**, such as rooms at depth 0 or very deep: level 0 still follows the real toggle points. The depth fallback just picks a different granularity.
- **Room clones from something other than `CheckPoint`**: picked up by the instantiate hook (see [New objects](#new-objects)), or by the 120 s `Rescan`. Static-batched renderers whose combined mesh is known become extra sources. Until then, those slots show only if another source is visible.
- **Combined meshes created at runtime** (Cyber Grind): unknown to the build, so they are ignored, as before.
- **Memory**: the CPU copy of every combined mesh (vertices and indices) is kept for rebuilds, and logged as `CPU copy N MB`. 1-1's static geometry is about 90k tris, a few MB.
- **A batch root that moves**: static proxies are parented to `staticBatchRootTransform` (or the scene root), not to the sources, as before.

- **Vertex-shader motion (`VERTEX_DISPLACEMENT`)**: reproduced for dynamic proxies since 2026-11-15 (see [Vertex displacement](#vertex-displacement)). Static-batched and skinned renderers with the keyword still trace their rest pose.

## Vertex displacement

ULTRAKILL's Master shader moves vertices in its vertex shader when `VERTEX_DISPLACEMENT` is on. That covers 3-2's flesh walls, heart and eye, most of 3-1, P-1 and parts of 8-3 (the flesh, mouth and vein materials), water waves, lava, acid, tree leaves and seaweed. The transforms never move. So before 2026-11-15 the RTAS held the rest pose: traced walls, shadows and lighting sat where the walls rest while the game drew them displaced. In 3-2's flesh hallway (pose `3-2 moving walls`, bench/suites/user-1114-late.json) this showed as dark shards and seams on the moving walls.

### The game's function

Read from the compiled Master vertex shader, with UnityPy and `fxc /dumpbin` as for the other decompiled terms. The parameter blob names the constant buffer. Every `VERTEX_DISPLACEMENT` variant shares the function: `VERTEX_LIGHTING`, `VERTEX_WARPING` (screen snapping) and the other keywords only change what comes after it.

Per vertex, in world space: `p` is the world position, `n` the world normal and `t = Time.timeSinceLevelLoad` (the game's `_Time.y`). `±` is `-` when `_ReverseFlow == 1`.

| Step | Formula |
|---|---|
| origin | the object's position if `_LocalOffset != 0`, else 0 |
| mode 0 (136 of the 142 materials) | `dir = normalize(max(_FlowDirection.xyz, 0)) * (_ReverseFlow == 1 ? 3 : -1)`; `s = dot(p - origin, dir) * _VertexNoiseScale * 0.1 + (t / 20) * _VertexNoiseSpeed`; `d = (tex(_VertexNoiseTex, (s, s)).r - 0.5) * _VertexNoiseAmplitude * dir`, times vertex colour g if `_VColStrength == 1` |
| mode 2 | `w = (colour.r * 2 - 1) * sin(±_VertexNoiseSpeed * t) * 0.5 + 0.5`, times colour g if `_VColStrength == 1` |
| mode 1 | `w = abs(frac(frac(uv.y * scale * 0.1) * scale ± speed * t) * 2 - 1)` |
| mode 3 (nebulae, 7-S ocean, 5-4 vortex) | `w = (tex(_VertexNoiseTex, uv * _UVNoiseScale + _UVNoiseSpeed * t).r - 0.5) * amplitude * 0.1` |
| modes 1-3 | `d = n * w * amplitude * 5`; any other mode: `d = 0` |
| result | `p' = p + d`. With `_PreventClipping == 1` (the flesh family): `p' = lerp(p', p, saturate((5 - length(p' - camera)) / 3))`, so walls flatten within 2-5 m of the camera |
| `_RecalculateNormals == 1` | Evaluate the same function at `p + T k` and `p + B k`, where `k = _NormalOffsetScale`, `T = normalize(-0.05 n.y / n.x, 0.05, 0)` and `B = normalize(cross(n, T))`. Their uv is offset by `T.xz k / 10` and `B.xz k / 10`. The normal is `normalize(cross(normalize(p'_T - p'), normalize(p'_B - p')))`. Where `n.x = 0`, the game's `T` is not finite (its guard tests `abs(n.x) < 0`, which is never true); we keep the rest normal there |

Typical values:
- The flesh family uses mode 0, amplitude 1-2, speed 10 (0.5 for the mouth blends) and flow (1, 1, 1); the veins use flow (0, 1, 0). `_VertexNoiseTex` is `BlackAndWhite` 16x16 (the mouths use `VerticalNoise` 1x32). Normals are recalculated and `_PreventClipping` is on.
- Water is mode 0 with amplitude 0.13-1; lava uses 0.25.

### Implementation

The code is in `src/UltraRT/SceneProxies.Displaced.cs` and `Displace.compute`.

- **Copy per renderer.** `AddDynamic` passes every MeshRenderer proxy whose source has the keyword on a submesh that proxy traces to `TryDisplace`. The proxy then gets its own copy of the mesh, with the same vertex layout. Readable meshes are copied with `Instantiate`. Others go through a GPU readback of every stream and of the index buffer. The copy gets `vertexBufferTarget |= Raw` and `rayTracingMode = DynamicGeometry`. The rest pose goes to a structured buffer once: object-space position, normal, colour, uv0 and the material slot. Opaque, water/glass and lava proxies all use this path, and each proxy of a renderer gets its own copy.
- **Per frame, before the RTAS build** (`RecordDisplacement`, GPU slot `displace`). One dispatch per moving copy writes `p'` and the recalculated normal into the copy's vertex buffer, in object space. It uses the proxy's current `localToWorld`, inverted in the kernel. Material values (14 floats per material slot; the renderer's property block wins) are read at the first dispatch and then for 4 proxies per frame, so material swaps show within a few frames. The build that follows rebuilds the copy's BLAS (DynamicGeometry).
- **Motion vectors.** Before overwriting, the kernel copies the old positions into the previous-pose buffer (`MotionVectors.PoseBuffer`, `_UltraRTPrevSkin`), in a region allocated as for a skinned proxy. The motion slot points at that region whenever the copy was displaced the frame before. `HitMotion` therefore returns per-vertex motion exactly as for skinned proxies, rigid motion of the transform included. No copy is needed after the trace.
- **Distance freeze.** Unity 2022.3 rebuilds a DynamicGeometry BLAS every frame and has no refit or build-flag API. A copy whose bounds are more than `_displaceRange` from the camera (40 m, with 5 m hysteresis) switches to Static: it is removed and re-added, because the mode is read at `AddInstance`. It keeps its last pose and is no longer dispatched. At the moving-walls pose, 3-4 of the 43 copies in the RTAS move; in 3-1's noisy room, 1-2 of 42.
- **Switches:**
  - `-ultrart-nodisplace`: no copies are made at the build.
  - `-ultrart-displace-range m`: sets the freeze distance.
  - Bench variant `plugin._displaceRange=-1`: puts the original meshes back on the proxies. Use it as a true "master" baseline within one launch.
  - `plugin._displaceRange=0`: freezes every copy.
  - `plugin._displace=0`: the copies get the rest pose.
  - `plugin._displaceMotion=0`: no per-vertex motion.
- **Not covered:**
  - Static-batched renderers keep the rest pose, because the static groups merge them into shared meshes. Counts: 3-1 21, 8-3 about 50, 5-S 23, 8-1 10. They are logged as `keeps the rest pose`.
  - Skinned renderers keep the rest pose too. The only one is the `Puppet` prefab.
  - The emitter table (emissive lights, lava) keeps the rest-pose triangles.
  - Material keyword changes at run time are not followed.

### Validation

Suites:
- `bench/suites/displace-check.json`: 3-2 moving walls, 3-2 eye and 3-1 noisy room. Modes: the game, the rest pose, displaced, the depth-match view and pathtrace.
- `displace-regress.json`: 0-1 metal and 6-2 start.

The depth-match view is the upsampler's `up.debugView=1`. Its red channel is the number of trace samples that lie at the game's own depth in that pixel, in the same frame.
- With the rest pose, the moving walls show blue (no match) wherever they bulge. Displaced, they match the game except at silhouettes.
- Pixels with no matching sample (red < 32, centre of the screen):

  | Pose | Rest pose | Displaced | Notes |
  |---|---|---|---|
  | 3-2 moving walls | 36.0 % | 10.7 % | the remainder is the far hallway and the viewmodel |
  | 3-2 eye | 7.9 % | 1.8 % | |

- 3-1's noisy room matches everywhere (only edge lines).
- 0-1 has no displaced renderer. 6-2's 8 water copies are out of the RTAS at its start pose.

Motion noise (`3-2 walls motion`: relight, vol=0, static camera while the walls move). The run-to-run spread is about ±0.1 sigma.

| Variant | Run 1 (all copies moving) | Run 2 (40 m range) |
|---|---|---|
| per-vertex motion | 3.00 | 2.86 |
| no per-vertex motion | 2.90 | 2.92 |
| rest pose / original meshes | 2.67 | 2.77 |

At the bench's 1/360 s step the walls move too little per frame for per-vertex motion to matter: there is no measurable difference. Moving, re-lit surfaces cost up to 0.3 sigma over static ones.

### Cost

Measured with `bench/suites/displace-perf.json`: exclusive `-Perf` run, RTX 5070 Ti, 2560x1440, trace scale 0.5, modes interleaved with `plugin._displaceRange=-1` (original meshes) in the same launch. Results are in `20261115-034133_displace-perf` (not committed).

| Pose | Mode | Frame ms (original → displaced) | GPU total | RTAS build | Ray dispatch | `displace` |
|---|---|---|---|---|---|---|
| 3-2 moving walls | relight | 5.81 → 6.14 | 5.09 → 5.45 | 0.16 → 0.33 | 3.27 → 3.39 | 0.025 |
| 3-2 moving walls | pathtrace | 5.97 → 6.16 | 5.33 → 5.51 | 0.16 → 0.33 | 2.75 → 2.74 | 0.026 |
| 3-2 eye | relight | 5.40 → 5.62 | 4.39 → 4.58 | 0.14 → 0.20 | 2.63 → 2.69 | 0.04 |
| 3-2 eye | pathtrace | 5.83 → 5.94 | 4.78 → 4.94 | 0.14 → 0.20 | 2.25 → 2.30 | 0.04 |
| 3-1 noisy room | relight | 5.11 → 5.01 | 4.25 → 4.21 | 0.157 → 0.157 | 2.55 → 2.50 | 0.005 |
| 3-1 noisy room | pathtrace | 4.99 → 5.05 | 4.23 → 4.28 | 0.157 → 0.158 | 1.80 → 1.84 | 0.005 |

- The kernel and its CPU recording are negligible. Record CPU is 0.02-0.05 ms per frame, down from 0.22-0.27 ms in the first version, which read every material every frame.
- The cost is Unity's per-frame DynamicGeometry BLAS rebuild, plus a little ray traversal.
  - With every copy moving (no range), 3-1's noisy room took 5.27 ms per frame against 4.86 ms with the 40 m range, in one launch (`20261115-033028_displace-perf-range`). Across launches, the walls pose cost +0.8 ms GPU. Writing the rest pose into the moving copies cost the same as displacing them. Displacement itself is not the expensive part; DynamicGeometry is.
  - With the 40 m range, a pose pays only for the moving surfaces near the camera.
- Comparisons across launches are unreliable here: the same pose's ray dispatch moved by ±0.2 ms between launches. Use the in-launch `_displaceRange=-1` baseline.
- Runs whose game.log shows `UltraRT.Native.dll ... Win32 error 4551` (Smart App Control blocking a fresh native build) have no GPU timings and no RR. Discard their GPU and pathtrace numbers.

### Inventory

Logs:
- At the scene build: `Vertex displacement: N displaced proxies (...)`, then the first 24 copies with their material values.
- Every 600 frames: `Vertex displacement (on): ...`, with dispatches/frame, vertices/frame, frozen/frame, switches and record CPU.

Seen in game:

| Level | Copies | Notes |
|---|---|---|
| 3-2 | 127 | 126 opaque, 1 acid; 39,677 vertices |
| 3-1 | 294 | 277 opaque, 13 water, 4 lava/acid; 21 static-batched renderers keep the rest pose |
| 6-2 | 8 | water |

Offline (UnityPy over the scene bundles, 2026-11-15), 142 materials carry the keyword. Renderers by level (static / dynamic):

| Level | Static / dynamic | Main materials |
|---|---|---|
| 3-1 | 21 / 297 | flesh family |
| 3-2 | 0 / 123 | flesh family |
| 8-3 | 50 / 52 | flesh family |
| P-1 | 0 / 75 | flesh family |
| 5-S | 23 / 59 | |
| 6-1 | 0 / 52 | `Corpses2Small Heresy` |
| 8-2 | 0 / 46 | `M_TreeLeaves` |
| 5-4 | 0 / 42 | corpses, the vortex |
| 5-1 | 0 / 39 | seaweed, water |
| 2-4 | 0 / 35 | water |
| 2-3 | 0 / 21 | water |
| 7-1, 7-2, 7-3 | about 20 each | water, gibs, leaves |
| 5-3 | 15 in total | |
| 0-5 | 14 in total | lava |
| 8-1 | 10 / 1 | |

0-E, 1-2, 1-3, 1-4, 1-E, 2-1, 4-1 to 4-4, 5-2, 6-2, 7-4, 8-4 and P-2 have 1-8 renderers each.

## Skinned meshes (enemies)

Each enemy `SkinnedMeshRenderer` gets a proxy `SkinnedMeshRenderer`: a hidden child of the source that shares its mesh, `bones` and `rootBone`, with `rayTracingMode = DynamicGeometry`, `updateWhenOffscreen` and `forceRenderingOff` (unless `-ultrart-skinned-visible`). It enters and leaves the RTAS like any dynamic proxy.

Two things place a skinned instance in the RTAS, and they are updated differently:
- **The pose.** Unity skins into the root bone's space (without scale; `-ultrart-motion-probe` checks this). Because the instance is `DynamicGeometry`, every `Build()` re-reads the skinned vertex buffer and rebuilds its BLAS. Animation therefore always shows, even with rendering forced off.
- **The instance transform**, which is the root bone's. The RTAS is in `Manual` mode, so `AddInstance` stores the transform once and only `UpdateInstanceTransform` changes it. `SyncDynamic` calls it for an active skinned proxy whenever the root bone's position or rotation changed since last frame (`MotionMatrix` != `CurM`, which motion vectors compute anyway).

**The bug this fixed: ghosts at spawn spots.** Before 2026-11-14, only rigid proxies got `UpdateInstanceTransform`. A skinned instance stayed at the root-bone transform it had when it entered the RTAS, usually where the enemy was activated, while its pose kept animating. In 0-3 (`bench/suites/ghosts.json`) the Filth walk 7 to 12 m from their spawn points (`root moved ... since added` in the `-ultrart-logfixtures` lines). The RT image showed them, animating, at their spawn spots and cast their shadows there; at the enemies' real positions there was no RT geometry. The composite mostly hid the copies, so in game they read as dark enemy-shaped shadows. Two things made the bug hard to see:
- The proxy's `Renderer.bounds` followed the enemy, because Unity computes those from the bones, not from the RTAS.
- The original check (`-ultrart-spawn-test`) only checked that enemies animate.

The motion vectors of skinned hits were wrong too, because the hit shader's `ObjectToWorld` was the stale matrix.

`-ultrart-skinned-noupdate` turns the update off, to reproduce the bug. The 600-frame scene-sync log line reports `skinned transform updates/frame`: up to about 17 per frame in the 0-2 run, at well under a microsecond each. Measured cost is within run-to-run noise: 0-2 run pt-dn GPU total 1.949 ms without the update against 1.971 and 1.987 ms with it, dispatch 1.262 against 1.277 and 1.289 ms. The short suite is unchanged.

### Mirrored skinned meshes (2026-11-15)

The cerberus statues (`Statue*/CerberusStatic`: `Cerberus_Fused` and `Cerb_Apple`, in 8-1's lobby, 1-3's water hall, 0-1's gun
room, 8-1's courtyard) showed RT lighting only as a bright rim around their outline, with the game's own statue inside it.
The upsampler's depth view (`up.debugView=3`: red = the traced surface lies behind the game's, green = in front, grey-blue =
matched) showed every mirrored statue solid red and the unmirrored ones matched.

- **Half the statues are mirror images.** Their root bone `Root` has scale (-46, 46, 46) (determinant < 0), the others (46, 46, 46).
- **Unity bakes the mirror into the skinned vertices.** `-ultrart-motion-probe-filter Cerb` reads back vertex 0 of the
  proxy's skinned buffer: it equals `TRS(root position, root rotation, 1)^-1 x world` for both kinds (`raw = rootNoScale`), so
  the buffer is in the root bone's space without its scale, and for a mirrored root the vertices themselves are mirrored
  (vertex 0 at x -0.0085 instead of +0.0085). Mirroring reverses the triangles' winding.
- **DXR decides facing in object space.** For a rigid instance the mirror sits in the instance transform and facing still
  comes out right (the mirrored statue pedestal, a rigid MeshRenderer, matched). For a skinned instance the mirror is in the
  object-space vertices, so with back-face culling on (the statue material culls back faces) every outward face was a back face:
  primary rays passed through the near side and hit the inside of the far side. Its depth was behind the game's everywhere
  except right at the silhouette, so the composite kept the game's pixels there and showed ours only as the rim. Shadow rays
  had the same problem, so a mirrored statue also cast its shadows from its far side only.

Fix (`SceneProxies.InstanceCcw`, used by every dynamic `AddInstance`): a skinned instance whose root bone has a negative
determinant is added with the opposite front-face winding. Logged as `Mirrored skinned proxy '<path>' (root ... scale ...):
front winding flipped` (first 8 per scene). `-ultrart-nomirrorfix` turns it off. Shading is unaffected: the hit shader already
flips the geometric normal to face the ray.

The 8-1 lobby's armchairs (`3 Stuff(Clone)/HotelChair`) also showed red in that view, for another reason: see "Instances
added too early" below.

### Instances added too early (2026-11-15)

8-1's lobby armchairs (`3 Stuff/HotelChair`, four props sharing the mesh `HotelChair`, used by nothing else) were missing
from the RT scene: the `instance` view showed the wall behind them and `up.debugView=3` showed them red, so the composite
kept the game's chairs without RT light or shadows. Everything the CPU can see about them was right: the proxy was in the
RTAS (`Trace` and view-probe lines: visible, mask 0x1, one entry, not mirrored, opaque submesh flags, a readable float3
triangle mesh, placed exactly where the chair is, `hasChanged` handled).

What told it apart: removing and re-adding the instances (diagnostic `plugin._readdDynamic=1`) brought them back, and the
bench applies every variant of a scenario right after loading, so suites with that variant showed the chairs even in their
plain modes. Without a re-add they missed on 7 of 7 loads (fresh `approach-fast` loads and checkpoint loads, `chairs-repeat`
/ `chairs-diag` / `statue-rim-chairs`); with one they were hit on 8 of 8. This is the fault the portal quads had (docs/portals.md,
"Portal quads added too early"): an instance added in the frame its proxy renderer was made can stay in the RTAS for the whole
level without ever being hit, until it is removed and added again. Hundreds of other proxies are made and added in the same
frames and are fine, so it seems to need something more (here: the first instance of a mesh nothing else used); that part is a
guess about Unity's internals. Re-sending the transform or uploading the mesh first (`UploadMeshData`) was not what fixed it.

Fix (`SceneProxies.QueueReAdd` / `ProcessReAdds`): the first `AddInstance` of every rigid dynamic instance is followed by a
remove and add 2 frames later (`NewInstanceReAddDelay`). Skinned instances (DynamicGeometry, rebuilt every frame) are left
alone. The 600-frame scene-sync line counts them: `N new rigid instances re-added (M with a mesh new to the RTAS; scene
total)`, M being the ones whose mesh no earlier dynamic instance used (the chairs' kind). `-ultrart-noreadd` turns it off;
`-ultrart-trace-proxies <text>` logs every add and remove of the matching proxies. The cost is one extra remove and add per
new proxy, spread over the frames the proxies are made in.

## New objects

Everything found after the build goes through one path, `AddHierarchy` (`src/UltraRT/SceneProxies.cs`):

- **Sources**: `Patches.InstantiatePatch` hooks every non-generic `Object.Instantiate` overload. A separate patch covers the generic `Instantiate<T>(T)`, which goes straight to native code; Mono shares one body for every reference type, so patching `Instantiate<Object>` covers them all. The game uses that overload at 86 call sites (gibs, fire pools, `BulkInstantiate`). Enemies waking up (`EnemyIdentifier.OnEnable`) and checkpoint clones are hooked as before. Spawns are collected only while an RT scene exists, at most 4,096 per frame, and handled oldest first within 0.5 ms per frame. If the list overflows, a rescan runs.
- **Static-batched renderers** register at once, because they decide whether a slot is visible. **All other renderers** go to a queue, and their proxies are made within a per-frame budget: 0.75 ms, or 2 ms while more than 256 are waiting. 8-3's checkpoint re-clones whole areas: about 3,300 proxies, roughly 90 ms of work. Made in one frame, that was a 700 ms freeze (with `IsLightFixture`'s all-pairs light test, now a 4 m grid); the queue now spreads it over about 45 frames.
- **Lights and sky spheres** are tracked here as well (`Lights`, `SkyRenderers`), from the build, spawned hierarchies and rescans. The plugin used to call `FindObjectsOfType<Light>` and `FindObjectsOfType<Renderer>` (for the sky) every second, about 7 ms each time in 8-3. Now the full light list (about 1,300 in 8-3, about 150 of them on) is re-checked at 4 Hz, and every frame only the lights that are on.
- **`Rescan`** is a safety net for objects that appear any other way (`new GameObject`, `AddComponent`). It runs every 120 s instead of 30 s, and not right after the build. It uses `FindObjectsByType(..., FindObjectsSortMode.None)`, and the proxies we create are already marked known, so it no longer walks them. 8-3 still costs about 7 ms to find 27k mesh renderers, plus 2 ms to scan them.
- **Copies of our proxies**: `Instantiate` copies a source's proxy child, hideFlags included, into the clone. `IsProxyClone` skips these copies by flag, name or shader, so they never become sources.

## Performance in 8-3

8-3's heavy parts are the game's own: the "8-3 b" area runs at about 10 ms per frame with RT off, and the checkpoint clone is a 145 ms game frame. What UltraRT added on top, before this change (RTX 5070 Ti, 1920x1080, trace scale 0.5, `bench/suites/perf-8-3.json`, the user's route replayed in real time):

- a **700 ms freeze** at the checkpoint (registering the clone), plus 16 ms (enemies) and 24 ms (emissive) in the same frame;
- a **7 ms frame every second** (the light and sky scan);
- a **10 to 13 ms frame every 30 s** (the rescan);
- **0.9 ms of plugin CPU per frame**: emissive owner transforms read every frame, alias rebuilds every 8 frames while moving, all dynamic instances checked in full every frame, a partial mesh rebuilt every time a source flickered.

Measured with `scripts/bench.ps1 -Suite perf-8-3`, the base build and this one run back to back:

| Scenario | Mode | Avg ms (before / after) | 1% low fps | Worst frame ms | Plugin CPU avg / worst ms |
|---|---|---|---|---|---|
| 8-3 route (75 s, real time) | off | 4.72 / 5.09 | 84 / 80 | 149 / 143 | - |
| | hybrid | 7.39 / 7.28 | 44 / 60 | 903 / 159 | 0.91 / 754 -> 0.70 / 15 |
| | relight | 7.21 / 6.51 | 45 / 70 | 887 / 179 | 0.88 / 741 -> 0.65 / 23 |
| 8-3 b (static pose) | hybrid | 11.34 / 10.79 | 56 / 71 | 19.2 / 14.2 | 0.83 / 8.1 -> 0.61 / 0.9 |
| 8-3 portals sweep | hybrid | 7.92 / 7.52 | 91 / 101 | 13.6 / 10.7 | 0.89 / 1.3 -> 0.75 / 1.1 |

The remaining worst frame on the route is the game's own clone. RT off shows the same frame, at 143 to 149 ms. Run-to-run noise is about 5 % on averages; compare the RT-off rows. Where the rest of the RT cost goes: 0.7 ms of plugin CPU and about 0.5 ms of Unity's own main-thread work for our command buffer (RTAS build, ray dispatch), all of it main-thread bound. The GPU needs about 2 ms of the 7 ms frame.

## Logs

- `Static activation groups: level L (...), G groups, I part instances, R records (budget B; by level a/b/c/d/e), W watchers (mode), T toggle targets, S sources, F tiny groups folded, ...`, followed by the 8 largest groups.
- `Shader-table hit records (enabled submeshes): N (static x of y in i/j group instances, dynamic ...)`.
- Every 600 frames, if anything happened: `Static activation (level L): instances and records in the RTAS; watcher flips, source flips, add/remove, partial builds (ms), cached partials, deferred, late sources`.
- The first 12 partial builds individually: `Partial group mesh ...`.
- `Room clone 'X': N behaviours scanned for toggle points in ms, +S late static sources and R renderers queued in ms`.
- `Rescan: find ms (counts), scan ms: ...; dynamic proxies, cloned proxies skipped, lights tracked`.
- The plugin's 600-frame line (`RT scene sync CPU avg ...`) also reports the proxy queue (`deferred proxies: made, ms, queue max`), hierarchies and instantiations seen, lights tracked and on, emissive costs (alias rebuilds, visibility, transforms) and the dynamic loops (dirty evaluations, active loop, dormant poll).

## Tunables (static fields, each with a command-line flag)

- `SceneProxies.StaticActivation = false` (`-ultrart-static-always`): static batches are always present (the old behaviour), for A/B tests.
- `SceneProxies.ForceGroupLevel = 0..4` (`-ultrart-static-level N`): force a grouping level.
- `SceneProxies.StaticRecordBudget` (`-ultrart-static-budget N`): the record budget.

## Runtime material changes

Proxy materials copy their source material's properties once (`SceneProxies.Proxy`). Game scripts change materials while
playing, so `src/UltraRT/SceneProxies.Sync.cs` follows them (`-ultrart-nomatsync` turns it off, for A/B):

| Change | Examples | How it is followed |
|---|---|---|
| Material swap on a dynamic renderer (`sharedMaterial =`, `renderer.material(s)` instancing) | `FinalDoor.Open` (the lit door frames: 0-2's start door), enrage/radiance variants, Gabriel's enrage materials | 16 active proxies per frame compare their source's materials (one `sharedMaterial` read for single-material renderers; our transparent overlay is ignored). On a change the proxy gets the new proxy materials; its submesh flags are re-added only if they changed. |
| Material swap on a static-batched renderer | the FinalDoor frame pieces that are Unity static batches | The static slice poll (256 sources per frame) checks non-atlas sources (multi-material ones on every 4th visit). The slot takes the new proxy material and its group part is rebuilt (merged meshes group submeshes by material). Atlas (StaticSceneOptimizer) sources are never checked: their material is the scene atlas. |
| MaterialPropertyBlock | `Door` light colours (locked red/black, unlocked), `PulsateEmissiveMaterial`, `BloodFiller` (`_FillAmount`, `_MeshScale`) | A proxy whose source has a block with `_Color`, `_EmissiveColor`, `_EmissiveIntensity` or `_FillAmount` is "followed": 8 per frame re-read their block, hash the values and, only when they changed, write them into the proxy's own copy of its material for that slot (made on first need, destroyed with the scene). |
| Property edits on a source material | scripts that set colours on an instanced material, scrolling offsets | 4 source materials per frame are re-read (`_Color`, emission, `_MainTex` and its scale/offset) and copied when changed. |

Emitters sampled as lights (docs/emissive-lights.md) follow swaps and emission changes too: the changed submeshes are
re-registered (`TakeMaterialChanges`), at most once a second per proxy for property blocks, and the old entries stop
counting through a per-proxy / per-slot material generation.

**BLOOD_FILLER** (7-3's tree): the game's shader paints the albedo's green and blue out below a noisy fill height. The
proxy hit shader does the same (`BloodFillTint` in `Proxy.shader`, decompiled from the Master shader): mesh TEXCOORD2.xyz
in [-1, 1], height `h = y/2 + 1/2`, noise `_BloodNoiseTex` at (angle, h x `_MeshScale.z` / 4) on sides and (x, h) on caps,
blended by the world normal's y; red where `h - 0.05 n < _FillAmount`. Only meshes with a 3-component TEXCOORD2.

**Fullbright** (`_Unlit`): `ULTRAKILL/Master` without `VERTEX_LIGHTING` is never lit by the game (Gabriel, screens, gibs,
unlit props): their proxies now return lit weight 0 like atlas fullbright geometry, so relight no longer darkens them (6-2's
Gabriel looked dim and red-brown instead of gold). Enemies (`ENEMY`) are also unlit in the game, but fully fullbright
enemies would lose all RT shadowing, so they keep half of it: `SceneProxies.UnlitEnemyLitWeight` = 0.5
(`-ultrart-enemy-lit w`, 0 = like the game, 1 = the old relit look). `-ultrart-lit-unlit` restores the old behaviour.

**Albedo as emissive in a lit material** (2026-11-14 late, user report: 6-1's chandelier candles pure white in relight,
dark red in the game). The Master shader's emission with `_UseAlbedoAsEmissive` is its "albedo", `_MainTex` times the
per-vertex term, and in a `VERTEX_LIGHTING` material that term is the vertex lighting (decompiled vertex shader:
`o1 = lighting * colour`), fogged like the surface. So the emission is the **lit** albedo times
`_EmissiveColor * _EmissiveIntensity` (with saturation), replacing the surface (`_EmissiveReplaces`) or added to it: such
a surface never glows by itself. 6-1's `M_WhiteMarble` (white, intensity 1, replaces) is simply a lit surface in the game;
traced as albedo emission it was white and lit the hall as an emitter. Proxies of lit Master materials with albedo as
emissive now have no emission; the emissive factor (`emissive x intensity` when it replaces, `1 + that` otherwise) scales
`_Color`, clamped to 1 (the game's brightening of a lit area isn't kept: albedo above 1 would add energy to the bounce).
Counted in the scene line (`lit albedo-as-emissive N`: 6-1 2, 7-3 1, 8-2 2, 6-2 1; none in 1-1, 0-1, 0-5, 2-2).
`-ultrart-albedo-emission-glow` restores the old emission. Unlit Master materials with albedo as emissive (their
per-vertex term is only the vertex colour) still glow, as in the game.

**Emissive mask** (`_EmissiveMask`, keyword `_EMISSIVEMASK_ON`): the game multiplies the emission by the main texture's
alpha^2.2 (decompiled pixel shader). The proxy hit shader now does too, and the emitter table scales the material's
emitted power by the mean of alpha^2.2 over the texture. 6-1's furnace coffins (`M_FurnaceCoffin`, orange, intensity
1.5) glowed over their whole front; in the game only the grate glows.

**Cost** (600-frame log line `Material sync: ...`, split into swap checks, blocks, re-copies and static checks): 0.006 to
0.03 ms per frame in 0-2, 1-3, 4-4, 6-2 and 7-3 (visual runs; first version was 0.05 to 0.2 ms with larger slices and
string property lookups). A swap creates a proxy material once per new source material (classification and material maps,
a one-off), and a static swap rebuilds one group mesh (budgeted with the partial rebuilds).

**Limits.** Latency is a slice cycle: about `active proxies / 16` frames for swaps (a few frames to a quarter second).
Keyword changes on a material (e.g. `EMISSIVE` switched on at runtime) and `_EmissiveTex` changes are not followed.
Skinned proxies' emission is not sampled as lights (as before). A static slot swapped to a material that isn't traced keeps
its old material (a merged slot can't be switched off).

**Diagnostics.** `-ultrart-viewprobe` logs, at every bench screenshot (after the first RT mode's effects spawned), the
renderers in a 30 degree cone ahead: materials, keywords, notable properties, property blocks, static slots' original
materials, and the proxy state (`SceneProxies.Probe.cs`). Bench effect kinds `coin`, `coinfront` (the revolver's coin,
hovering) and `opendoors` (the game's `FinalDoor.Open()` on every final door) exist for these tests;
`bench/suites/materials-pm.json` is the suite. Static slots also list their slot (part, transparent kind, proxy material) and each material its
`transp=` / `lava=` classification. The game overwrites `BepInEx/LogOutput.log` at the next launch in that slot (often
another agent's, seconds after your release): copy it before `game-lock.ps1 release`. `bench/suites/materials-late.json`
(the user's late playtest poses for 0-2 blood stains, 6-1 coffins, 3-2 eye and walls, 0-E ice, the museum fountain) and
`materials-late-check.json` (those plus water-glass, 8-2 pool, 0-2 blood river, 0-5 lava) go with docs/reflections.md
"Late playtest fixes".

## Scrolling textures (SCROLLING)

User report 2026-11-15 (5-2, pose 1): "with RT off the water shows a rushing current, with RT on it looks more like
gentle waves". 5-2's sea is not water to us: it is two opaque `Sea/Sea Itself/Filler/Plane` renderers with `Corpses` /
`CorpsesAlt (Instance)` (Master, `SCROLLING VERTEX_DISPLACEMENT VERTEX_LIGHTING RAIN`, texture `styxture3`), traced as
geometry with displaced copies. The current is the texture: a `ScrollingTexture` component on each renderer adds
`speed x Time.deltaTime` (here (1.25, 0.5) uv/s) to an offset every frame and writes it to the renderer's property block as
`_ScrollOffset`; its `Start` instances the materials and enables `SCROLLING` on them (decompiled `ScrollingTexture`). The
Master shader samples at `uv * _MainTex_ST + _ScrollOffset + _ScrollSpeed * _Time.y`. Proxy materials copied `_MainTex_ST`
only, and relight / pathtrace replace the game's opaque pixel with RT albedo x lighting, so the texture stood still and
only the vertex displacement moved.

Now (`src/UltraRT/SceneProxies.Scroll.cs`, `Proxy.shader`):
- **Scroll.** A proxy of a `SCROLLING` material gets `_Scrolling`, `_ScrollSpeed` and `_ScrollOffset`; the hit shader adds
  `_ScrollOffset + _ScrollSpeed * t` (t = `_UltraRTRainTime`, the game's `_Time.y`) to the main texture's uv (non-atlas
  materials). The property-block sync (`SceneProxies.Sync.cs`) follows `_ScrollOffset` like `_Color`: the renderer is
  "hot", its own proxy material copy gets the offset every frame. Logged every 600 frames as
  `Scrolling textures: N active ScrollingTexture renderers: 'Plane' speed (1.25, 0.5) offset game (33.806, 13.522) rt
  (33.797, 13.519) ...` (game vs proxy, at most a frame apart) and at the build as `Scrolling textures: N traced SCROLLING
  materials`.
- **Texture motion.** The temporal filters reproject along geometry motion, which is zero on a scrolling surface, so the
  moving texture smeared (relight burst on the 5-2 pose, masked sigma 2.48 against 0.37 with the texture frozen). The hit
  shader now adds the texture's own motion to `p.motion` (`ScrollMotion`): the world-space tangent frame of the triangle's
  texture uv (dP/du, dP/dv, as for the detail normals) times `_ScrollVelocity x dt` (`ScrollingTexture` speed +
  `_ScrollSpeed`, `_UltraRTFrameDt` = `Time.deltaTime`, the game's own step). TAAU, RR and the denoiser follow the flowing
  texture; lighting on such surfaces is smooth, so following the texture costs nothing visible. Same burst: 0.36.
- **Switches.** `-ultrart-notexscroll` / bench `plugin._texScroll=0` (frozen, as before), `-ultrart-notexscroll-motion` /
  `plugin._texScrollMotion=0` (scroll without texture motion vectors).
- **Seen in game.** 5-2: the two sea planes. 1-1: the fountain's `Limbo Water 2` cylinders scroll (0, 1) / (0, 0.5) but
  are transparent water (overlay, not proxies: unaffected). 0-5: `Lava Opaque` has `SCROLLING` with speed 0 and no
  `ScrollingTexture` (unchanged); the lava pieces with `ScrollingTexture` are LavaProxy proxies, which already scrolled
  (docs/lava.md). Museum fountain, 0-5 lava: same image as before at the regression poses (`mgw-diag4`).
- **Cost** (`bench/suites/mgw-perf.json`, `-Perf`, exclusive slots, 2560x1440, modes alternating every 0.5 s; the sea fills
  half the screen): GPU total relight 3.761 against 3.730 ms with `plugin._texScroll=0` (+0.03), pathtrace 4.344 against
  4.339 ms; frame times within noise (9.09 / 9.19 ms relight).
- **Not done.** The transparent water overlay's procedural ripples (docs/reflections.md, "Water and glass") don't follow a
  `ScrollingTexture` flow; atlas (StaticSceneOptimizer) materials never scroll in the game.

## Cutout audit

`Cutout audit: dynamic N cutout submeshes (A active; lost material X, opaque flag Y), static ...` (after the scene build
and every 600 frames, `SceneProxies.Cutout.cs`): every traced alpha-tested submesh needs an `_AlphaTest` proxy material and
an instance flag without `ClosestHitOnly`, or its any-hit never runs and the card is a solid quad. The first audit of a
scene also logs `Foliage materials:` (grass/bush/leaf/flower material names: shader, queue, RenderType tag, keywords,
alpha/blend/cull properties, whether we treat them as alpha tested). 1-1 (2026-11-15): `GrassSprite1-4 1` (Master
`ALPHA_TEST`, queue 2450, TransparentCutout) on `1 - First Field/1 Nonstuff/Grass` are dynamic proxies, all with material
and flag intact at every audit; `Bush` (Master, queue 2000, Opaque, no alpha test) is opaque in the game too.

**1-1 grass report (2026-11-15, not reproduced).** The user's screenshot (pathtrace, standing in the grass by the fountain)
showed grass cards as solid yellow-green quads with hard triangular shadows. Tried on master 86fbb58 + this branch, all
with correct cutouts and blade shadows: the old `1-1 grass` pose and the user's pose (relight, pathtrace, albedo; vertex
displacement on and `plugin._displaceRange=-1`), a 360 degree sweep inside the grass, a 7-stop walk through the grass
beside the fountain with the camera among the cards (`bench/suites/mgw-grass.json`, `recordings/1-1-grass-wander.json`;
pathtrace at the user's normal strength x2, albedo, `-ultrart-viewprobe`), and the cutout audit at every 600 frames (no
cutout submesh ever lost its material or flag). Ruled out by inventory: vertex displacement (1-1 has no
`VERTEX_DISPLACEMENT` renderer), the static groups (the grass sprites are dynamic proxies), material swaps (5 in the whole
approach, none on grass). The audit shows 112 of 184 cutout submeshes' sources destroyed during the approach (the game
replaces those objects), which is the one state change found. A bisect needs a reproduction: an F11 pose saved where it
happens, with the mode in use, is the next step.

## Blended veils (2026-11-18)

8-1's city outside the hotel windows has cloud layers over it (`Outside/Pipe/CityTest/FraudCity_Clouds/CloudLayer2`, `3`;
material `FraudCity_Clouds`: ULTRAKILL/Master, `TRANSPARENCY`, `_BlendMode` 2, no `_ZWRITE_ON`, queue 2999) and a water
sheen (`FraudCity_Water`, queue 2998). Their queues are in Unity's alpha-test range, so `SourceAlphaTested` made them
cutouts and they were traced with a 0.5 alpha test: solid white streaks over the city, in relight and pathtrace, where the
game has a faint haze (user report, tour stop "Busted Hotel Rooms").

`SceneProxies.BlendedVeil` now takes such a material out of the RT scene: ULTRAKILL/Master, queue 2501 to 2999,
`TRANSPARENCY` with alpha blending (`_BlendMode` 2), no depth write, no alpha-test keyword, not water, glass or lava
(those are classified first). The log lists them per level (`Blended veils left to the game's own pass`, with up to six
of their renderers). The line covers a whole level (the scan includes inactive rooms). Checked in 0-1, 0-5, 1-1, 2-2,
3-1, 4-2, 5-1, 5-2, 6-2, 7-1, 7-2, 8-3 and P-2 (`bench/suites/veil-check.json`): no material matches in any of them, so
water, glass, the 5-2 sea, lava and its haze are untouched; 8-1's two are the only ones found.

**Limit.** In the open the game draws a veil itself over the traced image. 8-1's city is seen through a portal, where
the traced image replaces the game's portal view, so there the veil is missing: the city is clear where the game has
its haze. Showing it needs blended (not additive) layers on the camera and portal rays; not done.

`bench/suites/sky-8-1.json` is the check. `-ultrart-viewprobe` now also works in a tour (once per view, 2 km, 80
renderers), but a static batch's bounds put most of a level at distance 0, so a far backdrop is easier to find in the
`Scrolling textures` and `Blended veils` log lines.

## Statue orbs (2026-11-15, user notes 1 and 2)

The cerberus statues (8-1's lobby, 1-3's water hall, the `StatueFake` cerberuses, 0-1) hold a ball, `Cerb_Apple` (the lit
`Cerberus` material: `ENEMY VERTEX_LIGHTING`, albedo `T_Cerberus`, pale stone), with a point light (range 25, x5) at its
centre and an `ExplosionSphere` glow around it (`Statue/Sphere_8/Point Light`). The game lights the statue per vertex
without shadows, so every side that faces the orb is pale. With RT on (user's 8-1 pose 1) the statue was nearly black:
- **Albedo was right** (the `albedo` view shows the pale stone); it was lighting.
- **The ball blocked its own light.** It encloses the light, so it was a light fixture (`FixtureMask`), and fixtures shadow
  their light past a 0.35 m clearance; the ball's radius is about 0.55 m.
- **The hands and arms shadowed the body.** The statue cups the light in both hands, so even past the ball most of the body
  was in the hands' shadow.

Fix (`SceneProxies.Orbs.cs`, `SceneProxies.IsLightFixture` / `IsGlowing`):
- An *orb fixture* is a ball (roughly as deep as wide) with the light in the middle third of every axis **and** hanging in an
  additive glow shell that wraps it (the light's parent draws `TRANSPARENCY` with `DstBlend One`, swapped or not). It gets
  `EmitterMask` (no shadow ray tests it). Without the shell test, altars, skulls, desk lamps and the player's `WallCheck`
  counted as orbs (40 per level in 8-1).
- The renderers that hold an orb (everything under the lowest common ancestor of the ball and its light, if that is at most
  14 m across) get `FixtureMask`, which ordinary shadow rays still test (`0x05`), and the orb's light is packed with shadow
  flag `l3.z = 0.25`: shadowed, but its rays skip fixtures (`UltraRTLights.hlsl`, `LightSkipsFixtures`). So the statue still
  casts shadows from every other light and stays in the room's shadows, but its own orb lights it like the game.
- Logs: `Orb fixtures (...): N: <paths>`, `Orb holders: N renderers hold M orb lights`, plus the holders' scales.
- Off: `-ultrart-noorbemitters` (the ball is a fixture again), `-ultrart-noorbholders` (holders and housings).

Note 2 (1-3: the orbs dark in the middle) was the **soft particle fade** (docs/vfx.md): the `ExplosionSphere` glow is swapped
unlit for the fade, and in front of the ball the glow is within 0.6 m of the opaque scene, so it faded out there and the
ball showed as a hollow rim. Glow renderers whose bounds hold an orb fixture's centre now get a property block
`_PLNoSoft = 1` (`LitParticles.KeepOrbGlows`; log `N glow spheres around orb fixtures keep their hard edge`).

Measured on the user's poses (`bench/suites/shade-1115g.json`; mean luma in a box on the statue / the orb, 0-255):

| | game | relight before | relight after | pathtrace before | pathtrace after |
|---|---|---|---|---|---|
| 8-1 statue (torso and legs) | 64 | 50 | 111 | 48 | 111 |
| 1-3 orb centre | 59 | 102 | 128 | 100 | 122 |

**Orb light x0.5 (user, 2026-11-15: "i'd make the cerberus's orb light a touch weaker").** The 8-1 statue was 1.8x the
game's luma in that box: the orb light is a physical light right at the stone, the game's vertex lighting spreads it thinly.
The statue orbs' own lights (`SceneProxies.OrbFixtureLights`: the lights of orb fixtures whose holder was found, not the 3-1
eye housings) are packed at half intensity (`Plugin._orbLightScale` 0.5, `-ultrart-orb-light F`, bench
`plugin._orbLightScale`), after any light override. Falloff and shadows are unchanged, and the orb itself and its glow are
not lit by it (the ball's outer surface faces away from the light inside it), so they keep their brightness.
Mean luma (0-255) in a box on the statue's torso and on the room beside it, x1 / x0.5 (`bench/results/20261115-145139_orb-dim`
and `20261115-145614_orb-dim-final_alt`; auto-exposure on, as played):

| | game | relight x1 / x0.5 | pathtrace x1 / x0.5 |
|---|---|---|---|
| 8-1 torso | 62 | 113 / 83 | 114 / 84 |
| 8-1 room (floor and wall to the left) | 31 | 42 / 37 | 42 / 37 |
| 1-3 torso | 91 | 135 / 124 | 134 / 125 |
| 1-3 room (wall to the right) | 78 | 91 / 105 | 91 / 104 |
| 1-3 orb ball (yellow pixels only) | 161 | 213 / 207 | 212 / 209 |
| 0-1 torso | 122 | 151 / 135 | 169 / 143 |
| 0-1 room (gun room wall) | 35 | 70 / 36 | 55 / 37 |

The 1-3 room gets brighter: the orb lit little of it, and auto-exposure lifts the frame when the statue dims. 0-1's gun room is
lit mostly by its four statue orbs and drops to the game's level (it was twice the game's).

The bright rim that followed the silhouette of some statues was mirrored skinning, fixed in "Mirrored skinned meshes" above.
