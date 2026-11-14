# Scene proxies and static-batch activation

The game's shaders have no ray tracing hit shader, so `SceneProxies` mirrors the scene with hidden proxy renderers that use ours (see the class comment in `src/UltraRT/SceneProxies.cs`). This page covers static-batched geometry (`src/UltraRT/StaticGroups.cs`): how it follows the game hiding and showing rooms.

## The problem

ULTRAKILL hides and shows whole rooms and areas by deactivating GameObjects: room and area triggers (`ObjectActivator` and roughly 60 other components that use `UltrakillEvent`), doors (`Door.activatedRooms` and `deactivatedRooms`), checkpoints and arenas. In 8-3 this covers entire regions. Dynamic proxies are children of their sources, so they follow these changes. Static-batched geometry instead shares a few combined meshes, and used to be mirrored by instances that never changed. Hidden rooms therefore stayed in the RT image and kept casting shadows.

## Model

- **Slot**: one submesh of a combined mesh. `StaticSceneOptimizer` gives each source renderer exactly one submesh (`SetStaticBatchInfo(r, first, 1)`). Unity's own static batching gives each renderer a contiguous run of submeshes.
- **Sources**: a slot is visible while *any* of its source renderers is `enabled` and `activeInHierarchy`. Several renderers can share a slot because `CheckPoint` clones its rooms. `Instantiate` keeps the static batch info, so the clone draws the same combined submesh. The checkpoint then deactivates the original (and moves it +10000 on x, which static batches ignore), and destroys and re-clones the room on every respawn. Ownership therefore can't be tied to the original objects.
- **Group**: an activation group, a partition of a combined mesh's slots. Each group has up to 4 parts (two-sided × sky wall, which need their own instance for culling and the instance mask). Each part is one proxy instance whose mesh contains only that part's slots, merged to **one submesh per material**, as before.
  - Whole group visible: the prebuilt "full" mesh, added with `Rtas.AddInstance`.
  - Nothing visible: `Rtas.RemoveInstance`. Its shader-table records disappear too.
  - Partly visible: a mesh of just the visible slots, built on the CPU from a copy of the combined mesh taken at load (no GPU readback at runtime). It is cached, so re-entering a room in the same state reuses it. At most 4 are built per frame and the rest wait. Replaced meshes are destroyed 4 frames later.
- **Flags mode** (fallback): if a combined mesh can't be read back, or with `-ultrart-nomerge`, it keeps its original layout with one group, and visibility is applied through submesh flags. Correct, but it costs one record per visible submesh.

## Detecting changes (per frame, in `SyncDynamic`)

1. **Checkpoint clones**: each `CheckPoint.newRooms` list is compared by reference. A new clone is registered at once: its toggle targets, its checkpoints, and `AddHierarchy(root)`. That registers its static renderers as extra sources of known slots, and its dynamic renderers too, which previously waited up to 30 s for `Rescan`.
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
- **Room clones from something other than `CheckPoint`**: picked up by the 30 s `Rescan`, which now also registers static-batched renderers whose combined mesh is known. Until then, those slots show only if another source is visible.
- **Combined meshes created at runtime** (Cyber Grind): unknown to the build, so they are ignored, as before.
- **Memory**: the CPU copy of every combined mesh (vertices and indices) is kept for rebuilds, and logged as `CPU copy N MB`. 1-1's static geometry is about 90k tris, a few MB.
- **A batch root that moves**: static proxies are parented to `staticBatchRootTransform` (or the scene root), not to the sources, as before.

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

## Logs

- `Static activation groups: level L (...), G groups, I part instances, R records (budget B; by level a/b/c/d/e), W watchers (mode), T toggle targets, S sources, F tiny groups folded, ...`, followed by the 8 largest groups.
- `Shader-table hit records (enabled submeshes): N (static x of y in i/j group instances, dynamic ...)`.
- Every 600 frames, if anything happened: `Static activation (level L): instances and records in the RTAS; watcher flips, source flips, add/remove, partial builds (ms), cached partials, deferred, late sources`.
- The first 12 partial builds individually: `Partial group mesh ...`.

## Tunables (static fields; no command-line flags yet)

- `SceneProxies.StaticActivation = false`: static batches are always present (the old behaviour), for A/B tests.
- `SceneProxies.ForceGroupLevel = 0..4`: force a grouping level.
- `SceneProxies.StaticRecordBudget`: the record budget.
