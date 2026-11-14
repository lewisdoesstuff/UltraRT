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

## Tunables (static fields; no command-line flags yet)

- `SceneProxies.StaticActivation = false`: static batches are always present (the old behaviour), for A/B tests.
- `SceneProxies.ForceGroupLevel = 0..4`: force a grouping level.
- `SceneProxies.StaticRecordBudget`: the record budget.

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
`bench/suites/materials-pm.json` is the suite.
