# Portals and portal mirrors

Layer 8 (8-1 to 8-4) builds its non-euclidean spaces from portals: a doorway shows, and leads to, a place somewhere else in the level. Mirrors there are portals too. This page covers how the game implements them, what ray tracing did with them before, and how rays now follow them.

Files:
- `src/UltraRT/SceneProxies.Portals.cs`: reads the game's portals, keeps one quad instance per rendered portal side in the RTAS, and uploads the travel matrices.
- `unity/.../Portal.shader`: the portal quads' hit shader.
- `unity/.../UltraRTPortals.hlsl`: `TraceScene`, the portal-following `TraceRay` used by every camera, bounce, reflection and cache ray.
- `unity/.../PathTrace.raytrace`, `CacheUpdate.raytrace`, `DebugView.raytrace`: use `TraceScene`.
- `unity/.../UltraRTLights.hlsl`: portal-aware light importance (ReSTIR also reprojected through portals until it was removed on 2026-11-19).
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

**Layer 30 ("Portal")**: the main camera never draws it; portal views with `canSeePortalLayer` do. Level geometry that should only exist "through" a portal lives there: in 8-3, 29 to 33 renderers, 3 of them active at the poses tested, among them the theatre chandeliers seen through the Shifting Arena's strip portals.

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
- **Water and glass** (the transparent pass at the end of RayGen): along the camera ray up to the opaque hit or the first portal, whichever is nearer; water seen through a portal gets no RT reflection overlay (the overlay is drawn by the main camera only).
- **Layer 30**: renderers on the "Portal" layer are traced with instance mask `SceneProxies.PortalLayerMask` = 0x40. `TraceScene` drops 0x40 from the first segment and adds it back after a hop through a side whose `canSeePortalLayer` is set (the hit reports it in `p.material` bit 0), which is the game's per-view culling mask. This is what puts the chandeliers into 8-3's Shifting Arena. Static-batched layer-30 renderers are not traced (none seen). `-ultrart-noportallayer` turns it off (at scene build).
- **Not shadow rays**: light sampling is in world space, so a surface is lit by the lights of the room it really is in. Lights shining through a portal into another space are not modelled yet (see Limits).

**The G-buffer stays in camera space.** Because the travel matrices are rigid, the far side seen through a portal is a rigid copy of it in front of the camera (the "virtual" scene). Primary hits write the virtual view: `_NormalDepth` = (rotation^-1 * normal, full path length), the motion in `_InstanceId.y` rotated the same way. The denoiser, the reflection filter and ReSTIR rebuild positions as `camPos + dir * t`, so they reproject surfaces behind a portal like any other: static portals need no special case. ReSTIR's reuse (`UltraRTLights.hlsl`, `g_PrimaryVirtual*`) reprojects and stores the reservoir plane with the virtual point and normal and evaluates lights at the real one.

## 4. Compositing

**Measured**: the game's depth buffer at a portal pixel holds the portal quad's plane (the `Portal Draw` quad writes depth; `-ultrart-compdebug 5` is solid green over every portal and mirror tested). The RT path through the portal is longer than that, so before this change the composite threw RT away at most portal pixels and kept the game's portal view unlit by RT, and wherever the straight-through hit happened to be within the depth tolerance it applied the wrong room. Primary rays that crossed a portal store the distance to the first portal surface in the high half of `_InstanceId.x` (half float; the denoiser and reflection filter only use the instance's low 7 bits, and the low 16 bits keep the instance). `Composite.shader`'s depth test and its depth-guided upsampling accept a match with either distance, so RT applies at portal pixels whichever depth the game writes there. Everything else (objects in front of a portal, glow quads) is tested as before.

**Mirrors keep the game's view** (default, every mode). The game's portal mirrors show V1 lit by the game and at full resolution; a traced reflection is half resolution, denoised, lacks particles and blood, and V1 is often dark under RT lights. So camera rays stop at a portal mirror: `TraceScene` with the request `ULTRART_TRACE_STOP_AT_MIRROR` (set by PathTrace's primary ray unless `_UltraRTMirrors` is 1) returns a miss there with `path.mirrorT`, the mirror side's hit shader reporting itself in `p.material` bit 1 (`_PortalMirror`). The pixel is written like sky, which every composite (ratio, replace, the temporal upsampler) leaves to the game, so the game's mirror view shows unchanged; water and glass are traced only up to the mirror. Bounce, reflection and cache rays and the debug views still follow mirrors. `-ultrart-rtmirrors` (bench `plugin._rtMirrors=1`) traces camera rays through mirrors as before: relight/pt-dn then show the RT reflection (lit like the rest of the frame, V1 included via mask 0x80), hybrid applies its shadow ratio over the game's mirror view. Cost is the same either way (8-3 secret mirror, relight: 5.14 vs 5.17 ms GPU).

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
| `SceneProxies.MaxPortalSides` | 1024 |

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
- **Lights through portals**: a light in room A does not light room B through a portal between them, and shadow rays ignore portals. The game fakes this for `PortalAwareLight`s (up to 16 transformed copies, clipped by the far plane). Doing it in RT means virtual lights (a light's image through each nearby portal, sampled with a plane clip and a shadow ray that is teleported back) in the light list.
- **Per-portal overrides** the game applies to a portal view are not applied to rays: `overrideSkyboxEnter/Exit`, `canSeeItself`, per-portal `maxRecursions` (one global limit of 3 instead), the fog-end distance cull, and `supportInfiniteRecursion`'s fake textures. `appearsInRecursions` and `canSeePortalLayer` are honoured. Fog overrides (and the level's fog behind a portal) are applied by the veil pass since 2026-11-18, for the first portal on a camera ray (docs/scene-proxies.md, "Blended veils").
- **Moving portals and crossing a portal** had no temporal handling until 2026-11-18: see section 8.
- **The game's own cubemap reflection** (`GameCubeTerm`; for pattern cubes since hybrid went on 2026-11-19) uses the portal exit point as the eye instead of the virtual camera. The error is small (the direction to each vertex differs slightly).
- **Particles, blood and transparent objects** the game draws in portal views are not traced, as everywhere.
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
position; moot since hybrid went on 2026-11-19), the player light and muzzle flash (not imaged through portals; built off by default in section 11), the backdrop exclusion by player position
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

