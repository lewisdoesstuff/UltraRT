# Portals and portal mirrors

Layer 8 (8-1 to 8-4) builds its non-euclidean spaces from portals: a doorway shows, and leads to, a place somewhere else in the level. Mirrors there are portals too. This page covers how the game implements them, what ray tracing did with them before, and how rays now follow them.

Files:
- `src/UltraRT/SceneProxies.Portals.cs`: reads the game's portals, keeps one quad instance per rendered portal side in the RTAS, and uploads the travel matrices.
- `unity/.../Portal.shader`: the portal quads' hit shader.
- `unity/.../UltraRTPortals.hlsl`: `TraceScene`, the portal-following `TraceRay` used by every camera, bounce, reflection and cache ray.
- `unity/.../PathTrace.raytrace`, `CacheUpdate.raytrace`, `DebugView.raytrace`: use `TraceScene`.
- `unity/.../UltraRTLights.hlsl`: portal-aware light importance (ReSTIR also reprojected through portals until it was removed on 2026-11-19); lights imaged through portals (section 15).
- `unity/.../UltraRTPortalBuffers.hlsl`: the sides' travel and quad buffers, shared by the portal and light includes.
- `src/UltraRT/Plugin.PortalLights.cs`: scene lights imaged through portals (section 15).
- `unity/.../Composite.shader`: the depth test at portal pixels.
- `unity/.../Upsample.compute`: the temporal upsampler of the replace modes, same depth rule, reprojects through portals.
- `src/UltraRT/SceneProxies.cs`: layer-30 renderers get their own instance mask.

## 1. How the game does it

Decompiled from `Assembly-CSharp` (namespace `ULTRAKILL.Portal`, mostly Burst-compiled jobs).

**Data.** A `Portal` component has two transforms, `entry` and `exit`, and a `shape` (`PlaneShape`: width and height, in the transform's local xy plane; the transforms' scale is ignored). `PortalManagerV2` (a `MonoSingleton`) keeps every `Portal` that called `AddPortal` in its private `portalComponents` list, and rebuilds a `NativePortalScene` from it every `Update` (`PortalScene.Sync`). A portal is in the scene when it is `isActiveAndEnabled`, both `entry` and `exit` are `activeInHierarchy`, and, if `disableRange` > 0, the player is within that distance of either end. Each portal gives two `NativePortal`s, one per side:

| | Enter side | Exit side |
|---|---|---|
| transform | `TRS(entry.position, entry.rotation, 1)` = E | `TRS(exit.position, exit.rotation, 1)` = X |
| travel matrix | `X * scale(-1, 1, -1) * E^-1` | `E * scale(-1, 1, -1) * X^-1` |
| plane | normal = E's forward through E's origin | X's forward |

The travel matrix maps the world near one side to the world near the other, rotated 180 degrees about the up axis so that walking *into* one side means walking *out of* the other. It is rigid (a rotation and a translation), so distances and angles carry over.

**Which side is "in".** `PortalMath.Raycast` only accepts a ray that starts in the side's back half-space (`dot(start - centre, -forward) >= 0`) and moves along its forward axis (`dot(dir, forward) > 0`). From the other side, the portal does not exist: you see the level behind it.

**Rendering** (`PortalRenderV2`): no render textures per portal and no stencil in the main view. In `LateUpdate`, `Setup` walks the visible portals recursively (default depth 4, `maxRecursions` 3 per portal, culling by portal frusta, an optional occlusion pass that reads back a 64-bit visibility mask) and builds one mesh, `totalPortalMesh`, with one quad submesh per portal side. In the main camera's `OnPreRender`, `Render` draws each visible portal view with a second camera (`PortalManagerV2.portalCamera`) placed at `travel * mainCamera` with an oblique near plane on the far side's plane, into the same `PostProcessV2_Handler` buffers, and composites it into `Portal Composite Color` (screen-sized ARGB32) through the quad, using stencil and depth for nested views. The main camera then draws `totalPortalMesh` with `portalMaterial` in a command buffer at `BeforeForwardOpaque` ("Portal Draw"), which pastes the composite into the main image at the portal quads. Sides whose `renderSettings` excludes them are collapsed to zero-size quads.

**Mirrors.** `Portal.mirror == true`. The scene uses the entry transform for both ends (`exit = entry`), so the travel matrix is a 180 degree rotation about the side's up axis; the renderer then flips the projection's x (`mirrorMatrix = scale(-1, 1, 1)`, inverted culling). Together that is a reflection about the plane. Only the enter side is added to the render list.

**Lights through portals.** `PortalAwareLight` registers point and spot lights every `LateUpdate`; `UpdatePortalLights` finds up to 16 (light, portal side) pairs where the light is in front of the side within its range, transforms the light by the side's travel matrix, and uploads `_PortalLightPositions`, `_PortalLightColors`, `_PortalLightAttens`, `_PortalLightDirs` and `_PortalLightPlanes` (the far side's plane, to clip the light to that side) for the `PORTAL_LIGHTS` keyword of the game's shaders. The player's own lights (muzzle flash etc.) are always included.

**Recursion rules** (`BurstRenderData`, `GetOnscreenPortalsBurst`): a view renders the sides whose plane has the camera behind it, within `maxDistance` (the fog end when fog is on), on screen and above `minPortalPixelArea`, never the side it came out of (`ignoreIndex`), and at depth > 0 only sides with `appearsInRecursions`. Each view's culling mask is the main camera's plus layer 30 when the portal it looks through has `canSeePortalLayer`, minus layer 30 otherwise.

**Layer 30 ("Portal")**: the main camera never draws it; portal views with `canSeePortalLayer` do. Level geometry that should only exist "through" a portal lives there: in 8-3, 29 to 33 renderers, 3 of them active at the poses tested, among them the theatre chandeliers seen through the Shifting Arena's strip portals. *2026-11-21: not so in the current game. The log now lists the layer-30 renderers: in 8-3 they are the secret arena's Ferryman parts and V1's model; the theatre chandeliers (`3 Stuff/Breakables/ChandelierFerry`) are ordinary renderers (section 18).*

**Other portal-aware systems**: `PortalAwareRenderer` clones renderers that straddle a portal (`PortalAwareRendererClone`), enemies get "(PortalOffset)" transforms, particles, audio (`VirtualAudioListener`), navigation links and the player's traversal (`PortalTraversalV2`, `CheckPlayerTraversals`) all use the same `NativePortalScene`.

**Portals in 8-3** (from the `Portal side` log lines):
- *False Starts / Door (2) / Portal (2)*: a 10 x 15 m doorway, enter facing +z, exit facing +y (it turns the world 90 degrees).
- *Escher Entrance*: two mirrors, 2 x 2.5 m (`Secret/Mirror`, `CeilingSegment/Mirror`), and a TV (`Decorations/TV/Quad`, 4.5 x 3 m, `renderSettings` = everything) that is a portal to another spot.
- *Shifting Hallway*: three 10 x 20 m doorways in a row (`1 -> 2`, `2 -> 3`, `3 -> 4`) whose exits move along z while you walk: the hallway shifts.
- *Shifting Arena*: two banks of four 60 x 5 m strip portals (`Scrollers`, `Scrollers (1)`) stacked into a wall, scrolling vertically every frame.
All have `maxRecursions` 3, `appearsInRecursions`, `canSeeItself` and `canSeePortalLayer` on, default clipping, no `disableRange`.

## 2. What ray tracing did before

The portal quads are a procedural mesh drawn by a command buffer: no renderer, so no proxy, so nothing in the RTAS. Every ray went straight through the portal plane into whatever is behind it in world space. In 8-3's first portal room (`bench/suites/portals.json`, "8-3 portals"), the doorway leads to a corridor that, in world space, is a small room much further away, so:
- **pt-dn / relight** (replace composites): the doorway showed the wrong room, and the area around the portal showed black (rays escaping into unlit space).
- **hybrid**: the ratio composite applied the wrong room's shadows and bounce to the game's portal view where the depths happened to agree, and nothing elsewhere.
- **Bounce, reflections, cache**: light and reflections "through" a doorway came from the wrong place.

## 3. Ray teleportation

**Portal surfaces in the RTAS.** `SceneProxies.SyncPortals` (every frame, from `Plugin.Trace` after `SyncDynamic`) reads `PortalManagerV2.portalComponents` by reflection (it never creates the singleton) and applies the game's rules: active portal and ends, `disableRange`, `renderSettings` per side, enter side only for mirrors. Each rendered side gets a hidden `MeshRenderer` proxy: a unit quad scaled to the `PlaneShape`, placed at the side's transform (not parented: the game ignores the transform's scale), with its own `UltraRT/Portal` material whose `_PortalIndex` is the side's slot. It is added with:
- instance mask `SceneProxies.PortalMask = 0x20`. Camera, bounce, reflection and cache rays use `ULTRART_MASK_SCENE` (0x6F: everything but water and glass, 0x10, and V1's third-person model, 0x80) and see it; shadow rays use 0x05 and never do, so portals don't block light. The transparent pass (0x10 only) doesn't see portals either.
- triangle culling off and not opaque (`RayTracingSubMeshFlags.Enabled` only), so the any-hit shader runs: it ignores hits from the back (`dot(rayDir, forward) <= 0`), which makes the side one-way exactly like `PortalMath.Raycast`.
- moved with `UpdateInstanceTransform` when the side moves, removed when it leaves the game's set.
- added only once the shared quad mesh and the side's proxy are `SceneProxies.PortalAddDelay` = 2 frames old (see "Portal quads added too early" below).

**Portal quads added too early** (fixed 2026-11-14). All sides share one quad mesh, made together with the first side's proxy in the frame the level's portals first pass the game's rules, and they used to go into the RTAS in that same frame. Added that early, the quads were never hit: every side, for the rest of the level, in every mode (`compdebug 5` empty, `albedo` looking straight through mirrors and doorways, images identical to `_portals=0`), although the log said `Portals: 12 portal sides in the RTAS` and the instance count included them. Re-sending the instance transforms on the next frames did not help. What did fix it was removing every side and adding it again later, which the bench variant `plugin._portals=0` does when it switches off and back on. That fits an acceleration structure built for the shared quad before the mesh was usable, then kept for as long as any instance used it (a guess about Unity's internals, not verified). Waiting 2 frames before the first `AddInstance` fixes it on a fresh level load. The bug was always there; it went unnoticed because `portals.json` runs `_portals=0` before the portals-on modes of every scenario, so the sides had already been re-added when it measured. The suites that came later (`self-reflection-8-3.json`) run `_portals=0` last, and the 13:00 to 13:50 misses were blamed on the screen resolution, which changed in the same hours (2560x1440 vs 1920x1080) but has nothing to do with it. Evidence in `bench/results/20261114-141003_portal-fix` (doorway on a fresh load: `relight_raw` goes straight through; `pt-dn_raw`, after the `_portals=0` toggle, shows the corridor), `20261114-141438_portal-fix-quick` (1920x1080: still missed), `20261114-141735_portal-fix-fresh` (mirror first on a fresh load: missed) and `20261114-142144_portal-fix-fresh` (with the delay: mirror and doorway hit on fresh loads, `compdebug 5` solid green).

The travel matrix of each side (3 rows) is uploaded to the global structured buffer `_UltraRTPortals` (`3 * index`). Mirrors use `E * scale(1, 1, -1) * E^-1`, a true reflection, instead of the game's rotation plus projection flip.

**The hit shader** (`Portal.shader`) only reports: `p.t`, `p.instance = _PortalIndex`, flag `ULTRART_FLAG_PORTAL` (payload flags bit 29, unused by the material flags). The payload stays 64 bytes and no UAV is added: the buffer is an SRV.

**`TraceScene`** (`UltraRTPortals.hlsl`) replaces `TraceRay` for every `ULTRART_MASK_SCENE` ray. On a portal hit it moves the ray through the side's matrix (origin = travel * hit point, nudged 1 mm along the new direction; direction = travel's rotation * direction; `TMax` shortened by the distance covered) and traces again, up to `_UltraRTPortalHops` = 3 teleports (`-ultrart-portal-hops`, bench `plugin._portalHops=N`). Past that the ray goes on straight without portals: the game does not draw a portal deeper than its recursion limit, so the level behind it shows. Rays after a hop carry `ULTRART_PORTAL_IN_RECURSION` (payload flags bit 29 as a request), and sides with `appearsInRecursions` off ignore them in their any-hit shader. It returns the last segment in `ray`, so callers keep computing the hit as `ray.Origin + ray.Direction * p.t`, plus a `PortalPath`: the accumulated rotation, the path length before the last segment, and the distance to the first portal.

Where it is used:
- **Primary rays** (PathTrace, DebugView, the cache's primary rays): shading, lights, shadows, cache lookups and the reflection ray all happen at the real hit on the far side.
- **Bounce rays** (PathTrace, CacheUpdate): bounce light comes through doorways from the right room.
- **Reflection rays**: a mirror sees through portals; the reported hit distance is the full path length.
- **Water and glass** (the transparent pass at the end of RayGen): along the camera ray up to the opaque hit or the first portal, whichever is nearer; water seen through a portal gets no RT reflection overlay (the overlay is drawn by the main camera only). Since 2026-11-20 the camera ray also collects the water and glass on every segment past a portal and lays them over the hit (section 13).
- **Layer 30**: renderers on the "Portal" layer are traced with instance mask `SceneProxies.PortalLayerMask` = 0x40. `TraceScene` drops 0x40 from the first segment and adds it back after a hop through a side whose `canSeePortalLayer` is set (the hit reports it in `p.material` bit 0), which is the game's per-view culling mask. (The chandeliers in 8-3's Shifting Arena turned out not to be layer 30: section 18.) Static-batched layer-30 renderers are not traced (none seen). `-ultrart-noportallayer` turns it off (at scene build).
- **Not shadow rays**: light sampling is in world space, so a surface is lit by the lights of the room it really is in. Lights shining through a portal into another space are their images (section 15, since 2026-11-20): an image's shadow ray runs in two segments, one per side.

**The G-buffer stays in camera space.** Because the travel matrices are rigid, the far side seen through a portal is a rigid copy of it in front of the camera (the "virtual" scene). Primary hits write the virtual view: `_NormalDepth` = (rotation^-1 * normal, full path length), the motion in `_InstanceId.y` rotated the same way. The denoiser, the reflection filter and ReSTIR rebuild positions as `camPos + dir * t`, so they reproject surfaces behind a portal like any other: static portals need no special case. ReSTIR's reuse (`UltraRTLights.hlsl`, `g_PrimaryVirtual*`) reprojects and stores the reservoir plane with the virtual point and normal and evaluates lights at the real one.

## 4. Compositing

**Measured**: the game's depth buffer at a portal pixel holds the portal quad's plane (the `Portal Draw` quad writes depth; `-ultrart-compdebug 5` is solid green over every portal and mirror tested). The RT path through the portal is longer than that, so before this change the composite threw RT away at most portal pixels and kept the game's portal view unlit by RT, and wherever the straight-through hit happened to be within the depth tolerance it applied the wrong room. Primary rays that crossed a portal store the distance to the first portal surface in the high half of `_InstanceId.x` (half float; the denoiser and reflection filter only use the instance's low 7 bits, and the low 16 bits keep the instance). `Composite.shader`'s depth test and its depth-guided upsampling accept a match with either distance, so RT applies at portal pixels whichever depth the game writes there. Everything else (objects in front of a portal, glow quads) is tested as before.

**Mirrors keep the game's view** (default, every mode). The game's portal mirrors show V1 lit by the game and at full resolution; a traced reflection is half resolution, denoised, lacks particles and blood, and V1 is often dark under RT lights. So camera rays stop at a portal mirror: `TraceScene` with the request `ULTRART_TRACE_STOP_AT_MIRROR` (set by PathTrace's primary ray unless `_UltraRTMirrors` is 1) returns a miss there with `path.mirrorT`, the mirror side's hit shader reporting itself in `p.material` bit 1 (`_PortalMirror`). The pixel is written like sky, which every composite (ratio, replace, the temporal upsampler) leaves to the game, so the game's mirror view shows unchanged; water and glass are traced only up to the mirror. Bounce, reflection and cache rays and the debug views still follow mirrors. `-ultrart-rtmirrors` (bench `plugin._rtMirrors=1`) traces camera rays through mirrors as before: relight/pt-dn then show the RT reflection (lit like the rest of the frame, V1 included via mask 0x80), hybrid applies its shadow ratio over the game's mirror view. Cost is the same either way (8-3 secret mirror, relight: 5.14 vs 5.17 ms GPU). **Since 2026-11-21 the raster skip (section 17, on by default) skips the game's mirror views too, so camera rays are traced through mirrors whenever the skip is on**, as with `-ultrart-rtmirrors`. With `-ultrart-portal-raster-skip off` the behaviour above applies again.

**Temporal upsampling** (pt-dn, relight; `Upsample.compute`): it matches trace samples to the game's depth per screen pixel the same way, so a through-portal sample is matched on its portal distance; history is reprojected from the far-side point instead of the game's (portal plane) point, and that depth is stored for next frame's validation, so the view through a portal doesn't smear like a picture painted on the portal.

`-ultrart-compdebug 5` (bench: `hybrid+plugin._compositeDebug=5`) colours through-portal pixels by what the game's depth matched: green = the portal plane, blue = the far-side hit, red = neither (the composite then keeps the game's pixel); other pixels show the game's colour darkened. `-ultrart-compdebug 6` blends the game's colour 50/50 with our albedo wherever RT applies: within one frame, any traced geometry that doesn't line up with what the game draws shows as a double image. That is the only fair check for moving portals (the Shifting Arena scrolls, so screenshots of different modes are never the same moment).

## 5. Settings

| Flag / variant | Effect |
|---|---|
| `-ultrart-noportals`, bench `plugin._portals=0` | no portal quads: rays go straight through, as before (A/B) |
| `-ultrart-portal-hops N`, bench `plugin._portalHops=N` | teleports per ray (3); past it rays ignore portals |
| `-ultrart-noportallayer` | don't trace layer-30 renderers |
| `-ultrart-rtmirrors`, bench `plugin._rtMirrors=1` | camera rays reflect off portal mirrors instead of keeping the game's mirror view (settings menu: "Ray-traced mirrors", [settings.md](settings.md)) |
| `SceneProxies.PortalAddDelay` | 2 frames before a new portal quad goes into the RTAS |
| bench `trans.portalGlass=0`, `trans.portalGlassStrength`, `trans.portalGlassHits` | water and glass behind portals (section 13): off / blend alpha x (1) / surfaces per segment (2, max 2) |
| `SceneProxies.MaxPortalSides` | 1024 |
| `-ultrart-portal-lights on\|off`, bench `plugin._portalLights=0\|1`, settings menu Advanced > Lighting "Lights shine through portals" (`[Lighting] PortalLights`) | scene lights imaged through portals (section 15; on) |
| bench `plugin._portalLightMax=N`, `plugin._portalLightLoopSpan=M`, `plugin._portalLightGuards=0` | images per frame (16), the loop guard's span (100 m), all guards off (diagnostic) |
| `-ultrart-stain-portals on\|off`, bench `wet.stainPortals=0` | blood stains on surfaces seen through a portal (section 18; on) |
| `-ultrart-portal-raster-skip on\|off`, bench `plugin._portalRasterSkip=0` | skip the game's raster portal views while RT replaces them (section 17; on) |
| bench `plugin._portalRasterTrim=0` | disable deep `PortalRenderV2` raster recursion trimming (on by default since 2026-11-21: while RT replaces opaque portal views, non-mirror portals are capped to `maxRecursions = 0, appearsInRecursions = false` so depth-0 raster views, depth, skyboxes, particles and all mirrors stay intact while skipping redundant depth 1..3 raster renders; RT still reads the saved original `appearsInRecursions` for full 3-hop ray-traced portals, and originals are restored immediately when RT turns off or unloads) |

`-ultrart-compdebug 5` (portal depth diagnostic) and `6` (game / RT albedo alignment overlay) were views of the hybrid
mode's ratio composite and went with it on 2026-11-19, as did `portals-align.json` and `perf-8-3-portals.json` (whose
scenarios were hybrid-only). The measurements below that used them stay as history.

Logs: `Portal side N: '<path>' enter|exit|mirror WxH at <pos> fwd <dir>, render <flags>, recursions R, disableRange D` the first time each side appears (first 64), and `Portals: N portal sides in the RTAS (M known)` when the set changes (first 40).

## 6. Results

RTX 5070 Ti, 1920x1080, trace scale 0.5, GPU timer. Suites: `bench/suites/portals.json` (each pose with portals on and `plugin._portals=0`), `portals-align.json` (`-ultrart-compdebug 6` overlays), `perf-8-3-portals.json` (the 8-3 b route, portals on and off). Results in `bench/results/20261114-082510_portals`, `20261114-082315_portals-align`, `20261114-082643_perf-8-3-portals` (not committed).

**What it fixes** (8-3):
- **Doorway into the false-start corridor** (`8-3 portals`): without portals the RT saw open sky and a far-away corridor through the doorway (`hybrid_..._portals_0_raw.png`); with portals it sees the corridor the game shows, with the sliding door slab in the same place in the same frame (`hybrid_raw.png` against `hybrid.png`). `compdebug 5` is solid green over the doorway: every through-portal pixel matches the game's portal-plane depth, so RT now applies there instead of being thrown away.
- **Shifting Arena** (`8-3 b`): the strip portals scroll every frame. With layer 30 traced, the theatre's chandeliers appear; `compdebug 6` overlays are single images (no doubling) with 1, 2 and 3 hops, so the traced view lines up with the game's.
- **Escher Entrance mirror** (`8-3 mirror`, `CeilingSegment/Mirror`): the mirror quad matches (green) and reflects; before, the RT saw through it.

**Cost** (GPU total ms; portals off -> on, same session):

| Pose | hybrid | relight | pt-dn |
|---|---|---|---|
| 8-3 portals (doorway, static) | 2.29 -> 2.55 | 2.41 -> 3.04 | 2.22 -> 2.72 |
| 8-3 portals sweep | 2.31 -> 2.77 | 2.47 -> 2.89 | |
| 8-3 mirror | 2.64 -> 2.68 | 2.68 -> 2.72 | |
| 8-3 b (Shifting Arena) | 2.07 -> 2.45 | 1.82 -> 2.72 | |
| 8-3 b route (99 s, real time) | 2.456 -> 2.449 (frame 7.13 -> 7.10 ms, plugin CPU 0.724 -> 0.722 ms) | | |

Where the camera looks through a portal, the dispatch now traces and lights a real room (often with layer-30 props) instead of escaping into sky, so it costs up to about 0.6 ms more in relight and 0.4 ms in hybrid; that is the price of the correct image, not overhead of the teleport itself (one extra any-hit and one more `TraceRay` per crossing). Away from portals nothing changes: the 8-3 route, which spends most of its time elsewhere, is identical within noise. Plugin CPU for `SyncPortals` is about 0.03 to 0.05 ms per frame with 22 sides (the scrolling and shifting sides move every frame, so their instance transforms and the matrix buffer are updated every frame).

### Noise behind portals (2026-11-14 evening)
User report: in 8-2, the far side of a fake-mirror portal is much noisier than the near side ("8-2 portal side 1" / "side 2" in `bench/suites/user-1114-pm.json`, on either side of `4 - Hub/4 Nonstuff/MirrorBig/Portal`, which joins x = 19 and x = 281). Reprojection was not the problem: the G-buffer stores the camera-side view, so the denoiser, ReSTIR and the upsampler already reproject through-portal pixels like any other. Light selection was. ReSTIR's alias table weights every light by its distance to the camera, and the room seen through the mirror is 262 m from it in world space, so its lights were picked almost only through the 20% uniform mix (over 110 lights). The importance now also counts the camera's image through every portal side it can look into (docs/light-sampling.md, "Portals"; `ls.portalImportance`).

Burst block sigma (64 frames, regions of the frame; `noise-pm-diag3`), "8-2 portal side 1" (the camera's half is on the left, the mirrored half on the right):

| | near wall | wall seen through the portal | floor |
|---|---|---|---|
| importance from the camera only (`ls.portalImportance=0`) | 0.15 | 0.79 | 0.13 |
| with the portal viewpoints | 0.19 | **0.22** | 0.16 |
| same, volumetrics off: camera only → portal viewpoints | 0.15 → 0.19 | 0.66 → **0.22** | 0.16 → 0.17 |

The far side is now as calm as the near side. "Side 2" has a flickering lamp in view (`off` itself changes there), and its runs did not order consistently. CPU: one matrix-point multiply per portal side in front of the camera and one extra importance evaluation per light and viewpoint when the alias table is built (at most 8 viewpoints x 256 lights).

## 7. Limits

- **The player in mirrors**: V1's third-person model (layer 30) now has its own instance mask, 0x80; camera rays add it after a hop through a `canSeePortalLayer` portal, as the game's views do (docs/reflections.md, section 13). The `8-3 mirror` pose is awkward: the CeilingSegment mirror hangs about 10 m above the floor, so V1 is half inside the ceiling there; use `Secret/Mirror` (`bench/suites/self-reflection-8-3.json`).
- **Sliding door**: the false-start doorway's slab slides on wall-clock time, so at some moments it covers the whole doorway and `compdebug 5` has no portal pixels there. That is correct, not a miss; check the raw RT image or another moment.
- **Bounce and reflection rays from a surface seen through a portal** start a fresh path: they don't see layer-30 props.
- **Lights through portals**: built on 2026-11-20 (section 15): images of the scene lights, one hop, clipped to the exit's opening, shadowed on both sides. Bounce light through a doorway was already right (bounce rays follow portals); emitters (glowing surfaces) are not imaged.
- **Per-portal overrides** the game applies to a portal view are not applied to rays: `overrideSkyboxEnter/Exit`, `canSeeItself`, per-portal `maxRecursions` (one global limit of 3 instead), the fog-end distance cull, and `supportInfiniteRecursion`'s fake textures. `appearsInRecursions` and `canSeePortalLayer` are honoured. Fog overrides (and the level's fog behind a portal) are applied by the veil pass since 2026-11-18, for the first portal on a camera ray (docs/scene-proxies.md, "Blended veils").
- **Moving portals and crossing a portal** had no temporal handling until 2026-11-18: see section 8.
- **The game's own cubemap reflection** (`GameCubeTerm`; for pattern cubes since hybrid went on 2026-11-19) uses the portal exit point as the eye instead of the virtual camera. The error is small (the direction to each vertex differs slightly).
- **Particles, blood and transparent objects** the game draws in portal views are not traced, as everywhere. Blood stains are
  the exception since 2026-11-21 (section 18). Water and glass are the exception since 2026-11-20 (section 13), without their reflection.
- **Portal clones**: `PortalAwareRenderer` copies objects whose bounds cross a portal (V1 jumping into 8-2's pool, enemies in doorways) with `new GameObject` + `AddComponent`, which the Instantiate hooks never saw (only the 120 s rescan did). A Harmony postfix on its private `CreateClone` now registers the copy's hierarchy like a spawn (`Patches.PortalClonePostfix`; logged as `Portal clone hook installed` and counted in the `Material sync` line). Copies of V1 keep layer 30, so rays see them only after a portal hop, as the game's views do.
- **8-2's starting pool** is a floor portal (15 x 60 m, `Opening Hallway/1 Nonstuff/Portal`) into a copy of the street 300 m away, not a mirror. The game shows V1 in it only while V1's bounds cross the pool plane (the clones above); standing at the edge or above it, the game's own view (RT off) shows no V1 either (`bench/suites/materials-pm.json`, "8-2 pool"). The clone path is not verified in game (a bench pose can't put V1 through the plane).

## 8. Temporal: moving portals and walking through one (2026-11-18, `t3/portal-temporal`)

User report, from the 8-3 route: the walls of the Shifting Hallway seen through its doorways smear sideways, the
theatre behind the Shifting Arena's scrolling strips smears up and down, and the image is noisy and soft for a moment
after walking through a portal. Two causes, both in what the histories are told about the previous frame.

**A moving portal.** The far side seen through a portal is a rigid copy of it in front of the camera (section 3). When
the portal's travel matrix changes (8-3: the hallway's exits slide along z, the arena's strips scroll every frame),
that copy moves, and nothing said so: the motion vector of a through-portal pixel was only the hit object's own
motion, rotated. The denoiser's plane test rejects a big step, but a scroll of a few centimetres per frame passes, so
it (and DLSS RR, which trusts the vectors) blended each frame with the previous one a little off: a smear along the
portal's motion. Now `SceneProxies` keeps last traced frame's travel rows (`_UltraRTPortalsPrev`), `TraceScene`
composes last frame's version of the path's transform next to this frame's (`PortalPath.prevRot`, `prevOff`, `moved`),
and for a primary hit behind a moved portal `PathTrace.raytrace` writes the motion of the camera-side image: where
last frame's path, inverted, puts the point the hit was at then, minus where it is seen now. A static portal takes the
old code path (`moved` false), so its vectors are bit-identical. `plugin._portalMotion=0` turns it off.

**Walking through a portal.** The camera jumps to the other side. What it sees there is what it saw through the portal
a frame ago, but every history looked the previous frame up with the previous camera, 10 to 800 m away, found
nothing and restarted. The jump is the portal's travel matrix T, so the previous frame's view of this frame's world is
`prevViewProj x T^-1`. `Plugin.PortalTemporal.cs` detects the crossing (the camera moved more than 1.5 m in a frame,
it was at an active non-mirror side, and that side's matrix takes the old position to within 0.9 of the jump, at most
12 m, of the new one) and remaps once: DLSS RR's and the upsamplers' previous view-projection, the reflection
filter's, light sampling's, the volumetrics' and the particle volume's (with their previous camera position), and for
the denoiser a `_HistoryXform` applied to the looked-up position and normal, because its history stores positions.
Light sampling's stored planes are not moved, so reservoirs and the visibility memory still restart at a crossing.
`plugin._portalRemap=0` turns it off; the log has `Portal crossing: through '<side>', <jump> m, residual <m>`.

**The bench could not show a crossing.** A recording's camera path is a spline through 30 Hz samples, so at a portal
the replayed camera glided across the gap, through the walls, for the length of a sample; part of the bad moment in
the 8-3 run was that. `CameraPath` now cuts where two samples are more than 6 m and under 0.1 s apart (the fastest
fall in the recordings is 4.5 m per sample): the camera jumps, as in play. In the Shifting Hallway the exits move, so
a replay lands 2 to 8 m from where the portal leads in that run; the remap uses the portal's matrix alone and leaves
that remainder as camera motion (a real crossing leaves one step). `plugin._portalJumpDiag=1` logs every camera jump
near a portal with what each side makes of it.

**Evidence** (`bench/suites/portal-motion.json`, 8-3-b; sheets in `bench/results/review/portal-temporal/`). The
portals are at another point of their cycle in every pass, so two columns never show the same view:
- `arena-smear-off-on.png`: held shots in the Shifting Arena at real frame rate, pathtrace. Off: everything behind the
  scrolling strips is streaked vertically (curtains, chandeliers, wallpaper). On: sharp. The user confirmed this is
  the smear they saw.
- `hallway-crossing-off-on.png`: frames in motion 10 to 20 frames after the two hallway crossings, pathtrace. Off:
  the walls are grainy and the next doorway's view is streaked. On: clean walls, sharp view.
- The door portal at 12.4 s (static; the crossing is detected with a 0.74 m remainder) shows no gain: the rooms
  behind it only switch on at the crossing, so there is no earlier view of them.
- relight's own denoiser: its history-length view showed no surface keeping its history across the hallway crossings
  with the remap on. Not explained (the replay lands off the exit there; the static door has nothing to keep), so for
  relight the crossing remap is unproven. Its moving-portal vectors work (the history length behind a moving doorway
  goes from half to full).
- Six levels' first rooms (`sky-8-1`) with the final build: no exceptions.

Cost: three more buffer loads and two small matrix products per portal hop, on every ray that crosses a portal; one
compare per traced frame on the CPU. Not measured separately.

## 9. Distance to the camera (2026-11-18, `t3/door-brightness`)

Many things in the plugin ask "how far is this from the camera" in world space: which lights and glowing surfaces get
samples, how large a radiance cache cell is, which effects are worth a light or a proxy. Behind a portal the answer
is wrong: 8-2's hallway seen through its portal is 365 m from the camera, 8-1's city 800 m.

`SceneProxies.PortalViewpoints` gives the camera's images through the portal sides it can look into (in front of
the side, within 120 m): travel x camera. It now follows up to 3 portals (the images of the images: 8-3's Shifting
Hallway shows three doorways in a row), keeps the 16 nearest by the way to the last portal crossed, and skips an
image within 1 m of a known one (back through the same pair). `Plugin.PortalViews()` fills the list once per traced
frame, right after the crossing remap, and everything below reads it. "Distance" then means the distance to the
camera or to its nearest image.

| What | Where | Before |
|---|---|---|
| Light importance | `LightSampling.ViewPoints` (as before, now from the shared list) | one hop, 8 images |
| Emitter importance (glowing surfaces) | `EmissiveLights.BuildAlias`, `em.portalImportance` | camera only: behind a portal an emitter was drawn through the uniform mix alone; since the fixture handover such surfaces are the lights there |
| Radiance cache cell size | `UltraRTCache.hlsl`, `CacheLevelF`; `_UltraRTCacheViews[16]`; bench `plugin._cachePortalViews=0` | 8-2's hallway behind its portal had 16 m cells for a 4 m corridor |
| Flame-driven and virtual effect lights (cull 45 m, score) | `EffectLights.AppendVirtual` | camera only |
| Shadow movers (60 m, 8 boxes) | `SceneProxies.CollectMovers` | camera only |
| Enemy glow parts (45 m), effect geometry (80 m) | `Plugin.EnemyGlow.cs`, `EffectGeometry.Update` | camera only |

The last three rows go through `PortalView.SqrDistance` (`src/UltraRT/PortalView.cs`; bench
`plugin._portalDistances=0` = camera only).

Two fixes from the same audit (lead's read-only pass over every "distance to the player or camera" in the code):
- `Denoise.compute`: a fresh-history pixel is seeded from the radiance cache at its position. Through a portal that
  position is the unfolded point (the camera ray continued straight on), not where the surface is, so the seed was
  another place's light or nothing. Skipped there now.
- `LitParticle.shader`: the lighting volume is the main camera's frustum. The game's own portal and mirror cameras
  draw the same particles and read the light of whatever is at the same screen place in the main view. They get the
  game's look (factor 1) now, by the test `TransparentOverlay.shader` uses (camera position against the volume's).

Left as they are, on purpose: the heat-haze flame list (`CollectFlames`, a screen effect of the main view), the beam
hull pick for volumetrics (the volumetric pass itself does not follow portals), stains (80 m) and displaced geometry
(40 m freeze): low value, see the list below.

**Measured 2026-11-18** (`bench/suites/portal-emitters.json`, pathtrace, 1440p, old = the three switches off, 16-frame
bursts, per-pixel sigma over the whole frame): 8-2 pose 4 (hallway through the portal) 0.62 -> 0.57, 8-2 pose 5
(inside, no portal in view) 0.71 -> 0.92, 8-3 pose 1 7.35 -> 7.14, 8-3 pose 2 13.13 -> 10.86 (both 8-3 poses have
portals in motion, so their numbers mostly measure that). The burst's run-to-run spread is as large as these
differences: **no measurable noise gain** from the distance changes on these poses. They are kept because they are
correct and free (a few distance tests per frame), and because the cases they are for (a glowing surface that is the
only light behind a portal, a cache cell larger than the room) are the audit's, not these poses'. The frames of the
user's sway recording in front of the 8-2 portal look the same old and new; what the user saw there in motion is
not explained yet (open).

**Not portal-aware yet** (from the audit, by value): volumetrics (the froxel march and its composite stop at the
portal plane), the sky (one global sky; a portal into another sky shows the camera's), hybrid's game cube term (eye
position; moot since hybrid went on 2026-11-19), the player light and muzzle flash (imaged through portals since 2026-11-20 with every other light, section 15), the backdrop exclusion by player position
at scene build.

## 10. Edges of a moving portal (2026-11-18, `t3/door-brightness`)

User, after the moving-portal vectors of section 8: the smear is "mostly fixed", but there is "still artifacting
around the moving edges". In the Shifting Hallway (8-3) the view behind a doorway slides sideways. With DLSS RR the
sliding stone wall carried thin stippled lines and bands of the green wall beside the doorway, several of them,
riding along with the view (sheet `bench/results/review/door/8-3_pose1_seams.png`).

Cause: what slides into view at the opening's edge was behind the wall beside the opening a frame ago. Its motion
vector is right, and it points at that wall. RR keeps a little of the wall's colour there, and from then on the
vectors carry it along with the view. The plugin's own denoiser rejects those pixels by its plane test (relight
shows no lines).

Fix (`PathTrace.raytrace`, `UltraRTPortals.hlsl` `PortalSegmentThrough`): for a primary hit behind a moved portal,
test whether the line from the camera to where the point was seen last frame goes through the first portal's
opening (`_UltraRTPortalQuads`: world to quad space per side, set every frame). If not, the motion is replaced by one
that points far off screen, so RR and the denoiser start the pixel fresh. Only on frames in which a portal on the
path moved; a static portal's vectors are untouched. Bench `plugin._portalEdges=0` = off.

Evidence (`bench/suites/portal-edges.json`, the user's 8-3 pose 1, six moments per mode;
`bench/results/review/door/8-3_edges_pathtrace_before_after.png`): before, green bands or lines in 3 of 6 shots;
after, none in 6. relight: none before or after.

**Still there**: a one- to two-pixel pale line on the sliding wall, in about 3 of 8 shots. It is in the `albedo` view
too (no history involved) and not in the game's image (`portal-edges-diag.json`, sheets `8-3_edges_diag_albedo.png`,
`8-3_edges_albedo_zoom.png`): rays slip through between two surfaces of the far side near the exit's plane and see
the lit room behind. Not found yet; the suspects are the 1 mm start offset past the exit's plane against a wall face
lying in that plane, and the proxy quad's size against the game's portal mesh.
*2026-11-19: both suspects ruled out, see section 11.*

Also fixed here: a new portal side counted as "moved" in its first frame (`SyncSide` compared after assigning).

## 11. Player lights, the 8-2 re-check and the 8-3 hairline (2026-11-19, `t3/portal-light`)

**8-2 portal in motion.** The user saw "slight edge artifacts plus noticeably noisy surfaces seen through the portal while
moving" (playtest of 2026-11-18). Re-checked on master (6a46c93) with the user's sway recording, six shots in motion
(`bench/suites/portal-light.json`, sheets `bench/results/review/portal-light/sway.png`, `sway_zoom.png`). Still there, slightly:
stair-stepped edges along the doorway and the light panel inside it, on the shots taken mid-sway. Noise through the
portal is not clearly worse than outside it on single shots; the 16-frame burst on 8-2 pose 4 gives sigma 0.57-0.60
over the whole frame (region 0: 2.08-2.20), the same as section 9's. The walls behind the portal are darker than the
game's (a general colour/brightness difference, not a portal one).

*2026-11-20: superseded by section 15. Every scene light, the player's included, is imaged, on by default, clipped to the
exact opening and shadowed; the cone-only, unshadowed copies below are gone.*

**The player's light through portals** (`Plugin.PortalLights.cs`, `SceneProxies.PortalImages`; **off by default**,
bench `plugin._portalLights=1`). The player's own lights (the eye light, muzzle flashes: `PlayerOwnedLight`, the test of
`FlagPlayerLights`) get a virtual copy behind each portal side they can shine through, by the game's rule (from the
side's back half-space, within 2 x range of the opening; not mirrors; one hop; at most 4 per light, 8 in all): at the
light's image through the side's travel matrix, as a spot whose cone holds the exit's opening (its bounding circle as
seen from the image, the outer quarter soft), unshadowed (`l3.z` 0.75) like the eye light. Packed with the surface
lights before the air-only ones; no air scattering. Why off:
- The game does not do it either. A Unity light lights its own place; the portal camera renders the far side with the
  far side's lights, so in the game the view through a portal is not lit by the player light. On, RT departs from the
  game there.
- 8-2 (Player/Point Light: intensity 0.5, range 15) shows no visible difference on/off (`portal_lights_sheet.png`,
  `bench/results/20261119-002934_portal-light`): the far hallway is lit by its own lamps.
It is for a dark place behind a portal that only the player's light would show. None was found in 8-2 or 8-3.

**8-3 hairline** (section 10, 8-3 pose 1). Two suspects tested, neither is it:
- *Double-door seam.* 8-3's BigDoor portals come in pairs, 2.75 m wide with centres 2.75 m apart, so their quads only
  touch. Traced quads 5 mm larger on every side: the line stays (`hairline_zoom.png`; the change was dropped).
- *The 1 mm start offset past the exit plane.* Now a switch, `plugin._portalStart` (metres; 0 = 1 mm, global
  `_UltraRTPortalStart`). At 0.1 mm and 1 cm the line is the same (`hairline_start.png`,
  `bench/results/20261119-004514_portal-hairline_alt`).
What the shots show: the line moves with the view through the Shifting Hallway's moving portals (10 x 20 m sides at
x -61/-71, -91/-101, -121/-131, exits sliding in z), and it is always about 60 px (at 1440p) inside the slab from the
slab's edge where the next segment's view begins. That fits a portal quad's edge seen through the moving portal (the
next segment's side, seen at hop 2 or 3, slightly off from the geometry around it) more than anything in the slab.
Next step: a hop diagnostic on that pose (which hop each pixel ended at; `-ultrart-compdebug 5`, which did that in hybrid, went on 2026-11-19), and the hop limit (past
`_UltraRTPortalHops` a ray goes on without portals and sees the level behind, a pale room). Open.

**Backdrop exclusion by player position** (scene build, `SceneProxies.IsBackdrop`: enclosures over 1 km that contain the
player). Not changed: no level showed an enclosure around a portal's far side that blocks the sun there. Left as it is.

## 12. Atmosphere behind portals (2026-11-19, `t3/portal-atmosphere`)

**Haze past the portal plane: not built.** The volumetric march ends at the game's depth, the portal's plane, so no
in-scattering is computed past a portal. The levels with portals have almost none to add: the medium in 8-1, 8-2,
8-3 and 8-4 is at or near its floor, 0.002/m (at most 0.0046/m), in every bench log of the last two days. On 8-2 pose 4
the whole volumetric pass (`vol.debugView=1`, in-scattering only) shows a glow around the far light panel and nothing
else. Continuing the march through the portal (a second segment from the exit, lights of the far side) would add a
fraction of that. Not worth a second march per portal pixel; left as a limit.

**The lit box behind 8-2's Reception portal: fixed.** What did show was a lavender, hard-edged box under the far
hallway's ceiling light, seen only through the portal (`bench/results/review/portal-light/box_split.png`,
`veil_split.png`, `cone_fix.png`). Split by switches on 8-2 pose 4 (the far wall inside the box, mean RGB): game
(108, 107, 125); pathtrace (167, 159, 204); without the game fog `vol.gameFog=0` (102, 96, 125), because without it the
veil pass does not run here at all; veil fog only, `veil.gain=0+veil.cover=0`: (110, 96, 126) (8 % of the portal's pink
fog, as the game's portal view has). So the box was the veil pass's layers: the game's additive light cone
(`LightPillar 3C`) behind the portal, drawn without its vertex fade (scene-proxies.md, "Light cones"). Cones now take
their vertex colours: (114, 100, 131), the cone a faint frustum like the game's (`cone_fix.png`, run
`bench/results/20261119-010135_portal-atmosphere_alt`). Cost: none (a material setting at the scene build).

Follow-up, not changed: `Plugin.Viewmodel.cs` and `LitParticles.cs` also test `IsKeywordEnabled("_VERTEXCOLORS_ON")`
on ULTRAKILL/Master materials, which may be false the same way.

**Not portal-aware, unchanged:** the sky (one global sky; no portal level here leads into another sky), hybrid's game
cube term (eye position; gone with hybrid on 2026-11-19), particle lighting for the game's portal cameras (they get the game's look, section 9).

## 13. Water and glass through portals (2026-11-20, `t3/water-portal-glass`)

**The report.** The user, on the v5c trailer's 8-3 Clockwork Crushers shot (`bench/cinematics/final-8-3-crushers.json`):
the glass isn't rendered through the portal. The hall's side walls are glass panes in front of two sideways loop
portals (`R3 Nonstuff/SidewaysLoop 1` and `2`, 20 and 40 m wide, at x 196.5 / 174.5, facing +-x): looking at a side wall
you see the hall again, from the other side, through its other glass wall. The game's portal view draws all of that
glass. Ours did not: the traced pixel replaces the game's portal view, the transparent pass stops at the first portal
(above, "Where it is used") and the overlay covers only the near side's renderers. Only the near pane was left, so
the walls read as nearly clear windows onto the looped hall (`pt-noglass` below).

**Fix.** `TraceScene` takes a request bit, `ULTRART_TRACE_PORTAL_GLASS` (never sent to a hit shader; compiled only
where `ULTRART_PORTAL_GLASS` is defined, i.e. PathTrace's ray generation). With it, every segment after a hop, and the
straight segment past the hop limit, traces the transparent mask (0x10) from its origin to its hit, up to 2 surfaces
per segment, and composites them front to back into `g_PortalGlass` (rgb = sum of transmittance x alpha x albedo,
a = transmittance left). The transparent proxy's closest hit returns the game's blend alpha (texture alpha x colour
alpha x opacity) in `p.motion` to this ray (the only one that sets `ULTRART_PAYLOAD_TRANSPARENT`; it never asks for
motion). The ray generation shader reads the result right after the primary `TraceScene` and blends it into the hit:
`albedo = albedo a + rgb`, `emission *= a`. That is the game's `W a + B (1 - a)` with `W` (the pane, lit) taken as its
albedo lit like the hit behind it; because it goes into albedo and emission, relight, the denoiser, pathtrace and DLSS
RR all carry it like any surface, with no new buffer. A segment that ends at a portal mirror the camera ray stops at
adds nothing (the game's mirror view keeps its own). Plugin: `_UltraRTPortalGlass` = (strength, hits per segment),
`TransparentSettings.portalGlass` / `portalGlassStrength` / `portalGlassHits`; on whenever water/glass instances
are active and the transparent pass is on (F4), in every mode, independent of the near side's screen rectangle.

**Result** (`bench/suites/water-portal-glass.json`, scenario `8-3 crushers`: the trailer's checkpoint pose and
cinematic, 0.5 s live takes at 1280x720, pathtrace with RR at scale 1 as in the trailer; run `bench/results/20261120-132034_water-portal-glass_alt`, frame 14 of each
take, `crop-8-3-left.jpg` = the left wall in off / `pt-noglass` / `pt` / relight, `stack-8-3.jpg` = off / before /
after): the side walls now show the frosted diamond pattern of the far panes over the looped hall, in pathtrace and
relight alike. The opening at the hall's end (`Portals A`) has no glass and is unchanged.

**Still lighter than the game.** The game's side walls are a dense grey frost with the looped hall barely visible. The
side portals have `maxRecursions` 1; rays use the global limit of 3 hops (section 7, "Per-portal overrides"), so
they see the hall repeat further and more clearly than the game's single recursion does. Not changed here.

**Not done:** the far-side panes get no reflection (the overlay's Fresnel term) and are lit like the hit behind them,
not by their own light; the sky behind a pane is the game's pixel anyway (sky pixels are left to the game). Cost: up
to 2 transparent rays per segment past a hop, at portal pixels only; not measured with the GPU timer (the live takes'
wall time is bound by the PNG writer: 61.4 vs 60.7 ms per frame with and without).

## 14. The game's portal views under a cinematic camera (2026-11-20, `t3/trailer-light-fixes`)

The v5c trailer's 8-2 supermarket aisle: with RT off the arch at the end of the aisle was black, traced it showed the
island, lit. The arch is the exit side of `12 - Island/12 Nonstuff/2/12 -> 12B Entrance` (10 x 10 m at (-180, 0, 196),
facing +z; its enter side is on the island at (-607, 1421, 555)). The game renders both sides (`renderSettings` Enter |
Exit; looking through the exit, the view uses the exit side's fog override, which is off). No game rule hides it: the
black came from the cinematic camera. `PortalRenderV2.Setup` picks the sides on screen in `PortalManagerV2.LateUpdate` from
the main camera's pose then, and the cinematic camera is posed afterwards (`onBeforeRender`), so the game picked from the
frozen player's eye at the island checkpoint; the arch side was never rendered and its quad pasted the empty composite.
Fixed by running `Setup` again from the posed camera (docs/cinematics.md, "Portals"). With it the game's image shows the
island through the arch as the trace does (`bench/suites/trailer-light-fixes.json`, run
`20261120-132549_trailer-light-fixes_alt`: arch region mean 0.9 -> 36 in the game's image at the aisle's end, 42 -> 46
traced; 8-3's Clockwork Crushers take unchanged in character). In play the camera is at the player's eye, so play was never
affected; any bench cinematic in a portal level was (the trailer's 8-2 and 8-3 shots).

## 15. Scene lights through portals (2026-11-20, `t3/portal-scene-lights`)

**Before.** Camera, bounce and reflection rays follow portals, but light sampling is in world space and shadow rays
ignore portals. A lamp in room A lit room B through the doorway between them only by bounce light; a bright room did
not spill onto a dark one across a portal doorway. Section 11's player-light copies were off and unshadowed.

**Now** (`Plugin.PortalLights.cs`, `SceneProxies.PortalImages`, `UltraRTLights.hlsl` "Portal images"; on by default).
Every packed point, spot and area light (not the sun, not fill lights, not an image itself; the player's own lights
included) that shines at an active non-mirror portal side gets an **image**: the light carried through the side's
travel matrix. "Shines at": from the side's back half-space (the game's rule), within 2 x range of the opening's
rectangle, the opening's bounding circle inside a spot's cone, a corner of the opening in front of a one-sided area
emitter. The image's spot axis, area normal and area tangent are rotated with it; it keeps the light's colour, range,
masks, shadow flags and the player flag. `l3.w = -(side + 1)` marks it (enemy glow lights already used `l3.w = 1`, so
the mark is negative).

The shader treats an image as two things:
- **The opening clips it** (`LightEval`): the line from a receiver to the image, carried back through the side's
  travel matrix, must cross the side's rectangle (`PortalSegmentThrough`, the moving-portal edge test's function, now in
  `UltraRTPortalBuffers.hlsl`). Light through a rectangular doorway lands as a rectangle, sharp, with no cone.
  Because the clip is in `LightEval`, RIS gives a clipped receiver a zero weight (no wasted candidates), and fill
  lights, the radiance cache, the viewmodel and lit particles all get the same clip.
- **The shadow runs on both sides** (`LightRayVisible`, one `TraceRay` site in a 2-step loop): receiver to 2 cm before
  the exit's plane on the receiving side, then from the matching point of the side's opening (2 cm past its plane) to
  the light on the light's side. A soft-shadow ray jittered outside the opening counts as blocked. Portal quads are
  mask 0x20, never in a shadow mask (0x05), so neither segment hits them. The player's spot-light eye clip is skipped
  for images (its plane would be on the camera's side). Regular lights pay one compare in `LightEval` and one in the
  shadow setup.
- **Cost** (2026-11-20, [performance.md](performance.md), "2026-11-20 batch"): compiled into PathTrace's ray generation,
  this code cost ~0.1 ms of relight dispatch in every scene, also without portals. PathTrace now has `RayGen` /
  `RayGenPT` without it and `RayGenImg` / `RayGenPTImg` with it, dispatched on frames that pack images (pipelines warmed
  ahead). Both tests run on composed rows (`_UltraRTPortalImageQuads`: the side's quad rows after its inverse travel,
  `PortalImageSegmentThrough`), so only the second shadow segment is carried back through the travel matrix.

**Guards** (`plugin._portalLightGuards=0` turns them off, for diagnostics):

| Guard | Rule | What it catches (2026-11-20 logs) |
|---|---|---|
| one hop | images are never imaged again (packed after every source) | feedback through 8-3's loops |
| same place | the image within 1 m of the light | a pair that leads back to itself |
| mirror | the light makes a U-turn (the exit faces the way the entrance does: `dot(T f, f) < -0.5`), or the portal's path contains "Mirror" | 8-2's starting pool (a floor portal into a copy of the street, 300 m away), `MirrorBig` (into a mirrored copy of the hub room), the island's `Mirror`, the storage room's ceiling portal. The copy behind has its own copies of the lights, as behind the game's real mirrors (never imaged) |
| loop | an exit within `_portalLightLoopSpan` (100 m) that opens into the light's own room (the light is on the side of the exit's plane the image lights), or a local portal (exit within 100 m of its entrance) whose exit the light sees straight through the level (`Physics.Raycast` on the game's Environment colliders, cached per pair for a second) | 8-3's Clockwork Crushers: the sideways loops (22 m) by the first rule, Portals A/B at the hall's ends (23 m) by the second; the Escher TV; 8-2's looping entrance hall `Portal Left` (80 m) and Black Lodge corridor (45 m). 8-2's BigDoors (2.75 x 6 m double doors) are as local but open into another room behind a wall: they keep their images |
| contiguous | the exit opening within the light's reach in world space | shadow rays ignore portals, so the real light already reaches what lies behind the exit straight through the world: 8-3's Shifting Hallway (doorways 10-12 m apart along one hall), 8-2's garage (20 m) |

**Budget.** Pairs are scored by luminance x (0.05 + (1 - distance to the opening / reach)^2), divided by
1 + (how far the exit is from the camera or its nearest portal viewpoint, beyond the light's reach, / 30 m)^2; exits more
than 150 m beyond reach are dropped; last frame's pairs get x1.5 so the set doesn't flip at the cap. At most 4 sides per
light and `_portalLightMax` = 16 images per frame, with 8 slots of `MaxLights` (256) kept for the air-only lights
packed after them. Images light no air (`_lightMedium` 0): the volumetric march stops at the portal plane. Logs:
`Portal light: '<light>' (reach R m) through <side>: image|skipped, <guard> ...` once per pair (80 per level),
`Portal light set (N): <light> -> <side> (score); ...` when the set changes (30), and
`Portal light images: N of M candidates (cap 16); guards skipped A mirror, B loop, C contiguous` when the count changes.

**Results** (`bench/suites/portal-scene-lights.json`; frozen-time cinematic takes, `TimeScale` 0, so every take shows
the same instant (8-3's door slab slides on its own, which made plain screenshots useless for an A/B); 1280x720, last
of 30 frames per take; run `bench/results/20261120-184035_portal-scene-lights_alt`, the final build). Mean change per
pixel (0-255) with images on vs `plugin._portalLights=0`, and over the pixels darker than 40 in the "before" frame
(`punch.py`'s dark-pixel question: does anything dark that the images can't reach get lifted?):

| Pose | pathtrace mean / dark | relight mean / dark | guards off (pathtrace) mean / dark / p99 |
|---|---|---|---|
| 8-2 reception (hallway through the Reception portal) | -0.12 / -0.01 | -0.21 / +0.01 | |
| 8-2 arch (supermarket aisle, island arch) | -2.25 / -0.52 | -1.11 / -0.14 | |
| 8-2 pool (starting pool: mirror guard) | +0.01 / +0.01 | -0.02 / 0.00 | +0.16 / +0.08 / +2.7 |
| 8-3 doorway (false start, the Shifting Hallway's door) | -0.22 / -0.13 | -0.08 / +0.05 | +0.87 / +0.68 / +4.3 |
| 8-3 crushers (between the sideways loops) | +0.96 / +0.28 | -0.02 / +0.03 | **+2.14 / +1.30 / +18** |

- **No global lift**: dark pixels move by at most 0.3/255 except at the arch, where the whole frame is 1-3 % darker
  (region ratios 0.97-0.99 everywhere, +1 % on the floor right at the arch): the auto exposure answering the lit arch at
  the frame's centre, not a lighting change. No exceptions in any run (5-1's explosion near water with the water
  ripples and combat lights included: the ripple rings were logged, the frames are clean).
- **The guards matter**: in the Clockwork Crushers, guards off lifts the hall's walls and floor visibly (its own lamps
  imaged through the sideways loops); with them, four weak images remain (Portals A/B, scores 0.06-0.25: the light's
  line to the exit is blocked, by the crushers' colliders presumably, so the line-of-sight rule keeps them), +1/255 in
  pathtrace, nothing in relight. The pool's images (scored highest of the 8-2 hub's, 9.1) would light the hallway and
  the street copy from below.
- **Visible gain is small on these poses**: what the images reach here is already lit by its own lamps (the Reception
  hallway, the island), dark carpet, or (8-3) skipped as contiguous because the real light already arrives straight
  through the world. The case it is for, a dark room beside a bright portal doorway, is in none of the bench's poses.
  An earlier run (`20261120-182135`, before the loop guard's last form) gave the same picture.

**Cost** (RTX 5070 Ti, 1440p, GPU timer, plugin CPU; `-Perf` under an exclusive hold, alternating 1.5 s segments,
off -> on; run `bench/results/20261120-183849_tmp-portal-scene-lights-perf_alt`):

| Pose (images) | pathtrace GPU total / dispatch | relight GPU total / dispatch | plugin CPU "lights" pathtrace / relight |
|---|---|---|---|
| 8-2 reception (9-16) | 5.97 -> 6.10 / 1.96 -> 2.04 | 5.63 -> 5.74 / 2.66 -> 2.69 | 0.26 -> 0.42 / 0.38 -> 0.42 ms |
| 8-2 arch (16) | 5.76 -> 5.78 / 2.24 -> 2.28 | 6.22 -> 6.32 / 3.62 -> 3.68 | 0.32 -> 0.67 / 0.38 -> 0.63 ms |
| 8-3 doorway (10-14) | 6.12 -> 6.01 / 2.20 -> 2.22 | 6.29 -> 6.10 / 3.27 -> 3.26 | 0.47 -> 0.45 / 0.34 -> 0.40 ms |

GPU: up to 0.08 ms of dispatch and 0.13 ms in total; the totals' run-to-run spread is about as large. Images only
add work where they light something (the clip zeroes them in RIS elsewhere), plus one compare per regular light. CPU:
up to 0.35 ms, at the arch (island lights with a 500 m reach pass the side tests for most of the level's sides). The
first version cost 1.2 ms there: per light x side it compared Unity objects to null (native calls) and inverted the
travel matrix; the sides are now cached once per frame, with an exit-to-view cull and scalar first tests. Outside
layer 8 nothing runs (no portal sides). The 10-14 ms "lights" spikes in the table's worst frames occur with images
off as well (periodic, older).

## 16. 8-1's lobby and long hallway: distant light under path tracing (2026-11-21, `t3/8-1-lampshades-banners-fixtures`)

The user's 8-1 playtest of 2026-11-20: pose 3 "distant lighting through portals is still noticeably lower quality", pose
5 "this hallway in 8-1 is probably the worst case scenario for path tracing quality" (the long red-carpet corridor with
its row of wall sconces and the lion statue far down it). Measured, alt slot, 2560x1440, SDR, DLSS RR at 1/3
(`bench/results/20261121-015644_tmp-81_alt`, `-020543_tmp-81_alt`); what was found, and what was not changed:

- **Which portal.** At pose 3 the portals the level has switched on (the 2B hotel hallway's exit at x = 0, the Drop
  Hallway's, the Fountain Loop's) are behind the camera or off to the side: the lobby ahead, to the arch and the flower
  statue 60-70 m away, is real geometry. The user's own route crossed the 2B portal just before the pose. So what the
  user sees there is distance, not a portal.
- **Held-pose noise is low and no sampling budget changes it.** 48-frame bursts, masked sigma / block sigma (0-255):
  pose 3 pathtrace 0.66 / 0.36, pose 5 0.67 / 0.27 (relight 0.66 / 0.30 and 0.64 / 0.23). `ptx.directSamplesRR=2`,
  `ptx.candidatesRR=32` and `ptx.paths=2` all land within 0.01-0.02 of that, the far region of the frame (the hallway's
  vanishing end) included (region sigma 1.02-1.11 in every variant). The light sampling and the path budget are not
  what limits these views at rest.
- **What does differ is the far end itself**: at RR's 1/3 resolution (853x480 traced) the corridor beyond the lion is a
  few traced pixels per sconce and reads soft and murky where the game's vertex lighting keeps every sconce's pool; and
  pathtrace's frame is about 20 % darker in linear light than relight's at pose 5 (0.099 against 0.120), the sconce pools
  dimmer. Both are general to RR and to the handed-over sconces (`hotel wall lights`), not to 8-1; a motion measurement
  (walking down the hallway) was not possible from the saved route, which ends with the user standing still.
- **The emissive triangle table is full in 8-1** (`Emissive lights: 65,535 triangles ... 50,375 over the cap`): 8-1's
  city backdrop (`FraudCity_*`, about 53,000 triangles and 1.3e8 m2 of dim emission, luminance 0.06-0.1) fills it, and
  every emitter registered after it is not sampled. `lc.emit.FraudCity=0` takes the city out (14,522 triangles, nothing
  over the cap) and changed nothing visible at poses 3 and 5 (frame mean 0.107 / 0.099 either way): every handed-over
  part was in the table (a light is only switched off once its part is), so the lamps that light these rooms were sampled. Not shipped: the city may be what lights 8-1's outdoor end, which was not checked. A
  table that ranks emitters (bright and small before vast and dim) when it overflows would be the general fix.
- **What did change at these poses**: the dome lamps (docs/lighting-overrides.md, "Baked fixture parts") and the far
  standing lamp's shade (docs/materials.md section 16; it glows in pose 3's distance as in the game). Frame mean linear
  lighting at poses 3 and 5 is unchanged (0.134 -> 0.135, 0.120 -> 0.120). Review sheets
  `bench/results/review/8-1-fixes/8-1_pose_3_lobby_portal.jpg`, `8-1_pose_5_hallway.jpg` (local).


## 17. Raster skip: the game's portal views are not rendered while RT replaces them (2026-11-21, `t3/portal-raster-skip-and-hitches`)

The game renders every visible portal view with a second camera (`PortalRenderV2.Render`, called from the main camera's
`OnPreRender`): a full forward render of the far side per view, the blood/oil and sky composites, and the composite into
`Portal Composite Color`. In the replace modes (pathtrace, relight) every opaque pixel of those views is overwritten by the
trace, because the camera ray follows the portal (section 3). In 8-3 b that thrown-away work was about 6 ms on the render
thread per frame.

**What is skipped** (`src/UltraRT/PortalRasterSkip.cs`, a Harmony prefix on `PortalRenderV2.Render`). While RT replaces
the view, the prefix runs only the part of `Render` that feeds the main view: `UpdatePortalLights` (the portal-aware light
upload that the game's transparents with `PORTAL_LIGHTS` still use), then clears the per-frame light lists. It skips the
portal-camera renders and the composite.

**What is kept:**
- `PortalManagerV2.LateUpdate` / `PortalRenderV2.Setup`: the portal mesh, the depth prepass and the main camera's `Portal Draw`
  quads, which put the portal plane into the game's depth.
- The scene, traversal and portal tracking (`totalPortalMesh` included).

**When the game's own render runs instead:**
- A frame where a view looks through a portal side that is not in the RTAS yet (`SceneProxies.IsPortalSideTraced`: a new
  portal is added after `PortalAddDelay` frames). `Render` then runs unchanged for that frame.
- The frame RT turns off (F7), the mode leaves a replace mode, or the plugin unloads (`PortalRasterSkip.Active` is set
  before every main-camera `OnPreRender`).

**What the trace has to do itself while the skip is on** (`_UltraRTPortalOwn` = 1):
- **Mirrors are traced.** The game's mirror view (section 4, "Mirrors keep the game's view") is one of the skipped views, so
  `SceneProxies.TraceMirrors` is forced on and camera rays reflect off portal mirrors. V1 shows in them through mask 0x80
  (8-3 secret mirror, both modes).
- **Sky through a portal.** A camera ray that misses after crossing a portal writes the sky (`SkyRadiance`) as a far emitter
  (`PathTrace.raytrace`), with the portal distance in `_InstanceId`. Without the skip the composites left a miss to the game,
  and the game's pixel there was its own portal view; with the skip that pixel holds no view.
- **Matching at the portal plane.** The game's depth at a portal pixel is the plane, but nothing of the game's stands behind
  it any more. RR's `Finish`, `Upsample.compute` and `Composite.shader` therefore also accept a through-portal sample when
  the game's depth is at or behind the portal plane. When no 3x3 sample matches and the centre sample went through a portal,
  that sample is taken (an occluder in the doorway is traced too and would have matched).
- **Exposure.** The auto exposure's game match (`Exposure.compute`) skips pixels behind a portal, because the game's colour
  there is no longer an image of the far side.

**Checks** (alt, SDR, `bench/suites/portal-raster-skip.json`, 2026-11-21, after the RayQuery composite fix below):
- 8-2 portal side 1 and 2 and the 8-2 hallway portal look the same with the skip on and off, in pathtrace and relight.
- 8-3 b's theatre traces with no black regions, and `off` after RT shows the game's own portal views unchanged.
- 8-3 secret mirror: V1 traced and lit.
- The 8-3 doorway's noise-textured strip covers different parts of the opening per shot with the skip on and off: it moves.
- Transparents and doorways: no black regions found.

Earlier screenshots that seemed to show black portals and dark mirrors with the skip were the RayQuery bridge bug
(docs/performance.md, "The RayQuery bridge hid the composite"): the composite was not drawn at all.

**Measured** (exclusive `-Perf`, main, 2560x1440, trace scale 0.5, RTX 5070 Ti, one launch, `bench/results/20261121-203500_*`):

| Scenario | Mode | skip off ms (fps) | skip on ms (fps) | render thread `frame` ms (off -> on) | `off` (vanilla) ms |
|---|---|---|---|---|---|
| 8-2 portal side 1 | pathtrace | 12.15 (82) | **9.87 (101)** | 10.54 -> 8.18 | 5.29 |
| 8-2 portal side 1 | relight | 13.93 (72) | **9.70 (103)** | 12.25 -> 7.12 | |
| 8-3 b | pathtrace | 12.52 (80) | **8.61 (116)** | 10.74 -> 7.03 | 8.67 |
| 8-3 b | relight | 14.57 (69) | **9.19 (109)** | 12.57 -> 6.22 | |
| 8-3 combat | pathtrace | 13.33 (75), 164 frames > 16.7 ms | **11.46 (87)**, 122 frames > 16.7 ms | 10.92 -> 9.20 | 5.88 |

8-3 b pathtrace now runs at vanilla's frame time. The settings row is in section 5.

## 18. The 8-3 playtest: hallway slab, strip portals, Providence, Deathcatcher door, blood (2026-11-21, `t3/portal-visibility-motion-blood`)

The user's 8-3 playtest on 2026-11-21 reported five problems seen through portals. The checks are in
`bench/suites/portal-vis-1121.json` (frozen poses). The runs below were on alt, SDR, 2560x1440, trace scale 0.5, at
`bench/results/20261121-2*_tmp-*` (since deleted suites, same scenarios).

**1. Shifting Hallway: a bright grey slab and green dots at the exits. This is not a regression.** A build of 316e0ca,
before the 2026-11-21 perf wave, shows the same slab, dotted columns and blocky patches (`20261121-215012_tmp-hall_alt`).
Toggling `_portalRasterSkip`, `_portalRasterTrim` or `_portalEdges` doesn't change them.
- The slab is `Pre-Space/Rooms/2 - Shifting Hallway/2 Nonstuff/Segment 2/2 -> 1/Sprite (11)` and `(12)`: a ProBuilder
  box with the `Charcoal` material (ULTRAKILL/Master, `VERTEX_LIGHTING`, `_USEALBEDOASEMISSIVE_ON` but no `EMISSIVE`,
  so it is a lit surface, not an emitter), with no vertex colours. It moves with the 1 -> 2 exit and lies less than 0.3 m
  past the exit plane. With `plugin._portalStart=0.3` or `1` the slab is gone and the sky shows behind it
  (`20261121-221047`).
- The game never shows it, because its portal camera clips at the opening (near plane at the opening's nearest corner,
  plus the per-material `PORTAL_CLIP_PLANE`). The rays start at the portal plane and hit it.
- It is bright because RT lights it per pixel from the `SupermarketLamp`s, while the game's vertex lighting leaves this
  box dark. That is a difference in lighting model, not a portal bug.
- The default stays `_portalStart=0`; a larger offset was only a diagnostic and was not checked on other portals. Not
  fixed.
- The green dotted columns along the moving exit's frame pieces show only while the exit moves: frozen shots are clean.
  `_portalMotion=0` makes them a smear instead. They are a temporal or motion-vector problem on the moving exit that is
  still **open**. The rotating portals in 8-3 b (FreezeAtPose stops them) showed no such artefact.

**2. Chandeliers, chairs and coins through the Shifting Arena strips.** The chandeliers and chairs are traced through
the strips in every state tested: fresh load, checkpoint-cloned and after combat effects (`20261121-220406`).
Earlier sections said the chandeliers were layer 30, which is wrong in the current game. The plugin's
layer-30 log (now listing up to 24 renderers) shows only the secret arena's Ferryman parts and V1's model in 8-3.
A coin tossed through the hallway doorway portal (`cointoss` with `ThroughPortals`, `20261121-221443`) is traced: the
view probe shows `Coin(Clone)/Model` at (-80.3, 2.35, 434.2), traced, mask 0x1, visible. In that shot the coin was
behind the slab from item 1, so it was not visually confirmed.

**3. Providence as a flat white hexagon.** Providence's `Back_Glow` is a 7-vertex hexagon on Unity's `Standard` shader
(white). It was traced as an opaque surface. The rest of the enemy is transparent and drawn by the game: wings, eye,
rainbow, charge sphere and the `GlowStrong` plane. `Traceable()` now skips `Standard` materials named `*Glow*`
(docs/scene-proxies.md, "Glow cards on Unity's Standard shader"). The hexagon is gone in the direct view
(`20261121-222627`, `8-3 providence direct`). Seen through a portal, Providence's transparent parts are still missing,
as all game transparents are (section 7).

**4. Deathcatcher doorway: a blue-grey void (pose 147). Not reproduced.** The portal there is
`Blue Path (Dimensional)/B7 - Dimensional Deathcatcher/B7 Nonstuff/B7-1/PortalSpinner/Portal`: 80 x 120 m, rotating,
with fog overrides. The bench poses (checkpoint and `approach-fast`, `20261121-213730`, `-214203`) land in another room
state, because the dimensional rooms switch on portal-travel events that a replay doesn't run. The void's colour equals
the level's fog colour (0.282, 0.549, 0.898), which is what the game's `DepthPrepass` clears `mainTex` to. That fits a
camera ray that missed the portal quad without a hop: the composite leaves sky pixels to the game, and with the raster
skip no game portal view is drawn there. Next step: a pose saved in that room state, then the view probe on the void.

**5. Blood stains through portals: now traced.** Before, the stain grid only held stains near the camera, so surfaces
seen through a portal had none.
- `StainGrid.compute` now also keeps stains within `wet.stainDistance` of each portal view (up to 8, `_StainViews`).
- `PathTrace.raytrace` tints the primary hit behind a portal (`path.hops > 0`) with `StainTintAt`, which fades by the
  ray's total length instead of the distance from the camera.
- Settings: `wet.stainPortals` / `-ultrart-stain-portals on|off` (default on). Path tracing only: relight's portal hits
  are unchanged.
- Bench: `bloodstainahead` with `"ThroughPortals": true` places stains through the doorway (bench/README.md).

Checked on the 8-2 hallway portal, frozen (`20261121-222828_tmp-portal-vis_alt`): `off` and `pathtrace` both show the
stains; `pathtrace+wet.stainPortals=0` shows none. Stains on vs off changes 17,695 final pixels and 18,506 `_raw` pixels
by more than 30. The mean |final - off| is 13.5 with the fix and 13.4 without, so the image moves no further from the
game. `wet.stainDebug=1` shows the stains in green through the portal.

**Measured cost** (exclusive `-Perf`, main, 2560x1440, trace scale 0.5, RTX 5070 Ti, `20261121-223144_tmp-portal-perf`):

| Scenario | Mode | stains off: avg ms / GPU total / dispatch | stains on: avg ms / GPU total / dispatch |
|---|---|---|---|
| 8-2 hallway portal (stains placed) | pathtrace | 8.86 / 6.07 / 2.55 | 8.87 / 6.11 / 2.59 |
| 8-2 hallway portal | relight | 9.27 / 5.99 / 3.76 | 9.35 / 6.02 / 3.80 |
| 8-3 b (no stains) | pathtrace | 8.63 / 5.74 / 2.58 | 8.69 / 5.77 / 2.61 |

About +0.03 ms GPU dispatch, within run-to-run noise. Relight doesn't tint hits behind portals, so its +0.04 ms is noise
too.

**Bench and diagnostics added:** `ThroughPortals` effects (`PortalPhysicsV2.Raycast`), effect kinds `bloodstainahead`
and `cointoss`, `-ultrart-merge-static-dynamic on|off` (docs/performance.md, C4), mean vertex colour in the view
probe's mesh field, and the layer-30 renderer list in the log.

**Tried and dropped:** re-aiming the game's `AlwaysLookAtCamera` billboards at each portal view before the dynamic sync
(the game does this for its own portal cameras in `PortalManagerV2.OnPreRenderCallback`). It made no visible
difference to Providence or the 8-3 sprites, so it was removed.
