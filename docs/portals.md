# Portals and portal mirrors

Layer 8 (8-1 to 8-4) builds its non-euclidean spaces from portals: a doorway shows, and leads to, a place somewhere else in the level. Mirrors there are portals too. This page covers how the game implements them, what ray tracing did with them before, and how rays now follow them.

Files:
- `src/UltraRT/SceneProxies.Portals.cs`: reads the game's portals, keeps one quad instance per rendered portal side in the RTAS, and uploads the travel matrices.
- `unity/.../Portal.shader`: the portal quads' hit shader.
- `unity/.../UltraRTPortals.hlsl`: `TraceScene`, the portal-following `TraceRay` used by every camera, bounce, reflection and cache ray.
- `unity/.../PathTrace.raytrace`, `CacheUpdate.raytrace`, `DebugView.raytrace`: use `TraceScene`.
- `unity/.../UltraRTLights.hlsl`: ReSTIR reprojects through portals.
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

**Other portal-aware systems**: `PortalAwareRenderer` clones renderers that straddle a portal (`PortalAwareRendererClone`), enemies get "(PortalOffset)" transforms, particles, audio (`VirtualAudioListener`), navigation links and the player's traversal (`PortalTraversalV2`, `CheckPlayerTraversals`) all use the same `NativePortalScene`. The game's "Portal" layer (30) is not used by the render path above.

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
- instance mask `SceneProxies.PortalMask = 0x20`. Camera, bounce, reflection and cache rays use `ULTRART_MASK_SCENE` (0xEF: everything but water and glass, 0x10) and see it; shadow rays use 0x05 and never do, so portals don't block light. The transparent pass (0x10 only) doesn't see portals either.
- triangle culling off and not opaque (`RayTracingSubMeshFlags.Enabled` only), so the any-hit shader runs: it ignores hits from the back (`dot(rayDir, forward) <= 0`), which makes the side one-way exactly like `PortalMath.Raycast`.
- moved with `UpdateInstanceTransform` when the side moves, removed when it leaves the game's set.

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

**Temporal upsampling** (pt-dn, relight; `Upsample.compute`): it matches trace samples to the game's depth per screen pixel the same way, so a through-portal sample is matched on its portal distance; history is reprojected from the far-side point instead of the game's (portal plane) point, and that depth is stored for next frame's validation, so the view through a portal doesn't smear like a picture painted on the portal.

`-ultrart-compdebug 5` (bench: `hybrid+plugin._compositeDebug=5`) colours through-portal pixels by what the game's depth matched: green = the portal plane, blue = the far-side hit, red = neither (the composite then keeps the game's pixel); other pixels show the game's colour darkened. `-ultrart-compdebug 6` blends the game's colour 50/50 with our albedo wherever RT applies: within one frame, any traced geometry that doesn't line up with what the game draws shows as a double image. That is the only fair check for moving portals (the Shifting Arena scrolls, so screenshots of different modes are never the same moment).

## 5. Settings

| Flag / variant | Effect |
|---|---|
| `-ultrart-noportals`, bench `plugin._portals=0` | no portal quads: rays go straight through, as before (A/B) |
| `-ultrart-compdebug 5` | portal depth diagnostic (hybrid) |
| `-ultrart-portal-hops N`, bench `plugin._portalHops=N` | teleports per ray (3); past it rays ignore portals |
| `-ultrart-noportallayer` | don't trace layer-30 renderers |
| `-ultrart-compdebug 6` | game / RT albedo alignment overlay (hybrid) |
| `SceneProxies.MaxPortalSides` | 1024 |

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

## 7. Limits

- **The player in mirrors**: the game shows V1 in mirror views through layer 30; layer-30 skinned renderers are traced now, but whether the player model is among them (and lines up) was not checked; the mirror pose tested is awkward (a sideways Escher room with geometry the game hides covering half the view, in both A/B variants).
- **Bounce and reflection rays from a surface seen through a portal** start a fresh path: they don't see layer-30 props.
- **Lights through portals**: a light in room A does not light room B through a portal between them, and shadow rays ignore portals. The game fakes this for `PortalAwareLight`s (up to 16 transformed copies, clipped by the far plane). Doing it in RT means virtual lights (a light's image through each nearby portal, sampled with a plane clip and a shadow ray that is teleported back) in the light list.
- **Per-portal overrides** the game applies to a portal view are not applied to rays: `overrideSkyboxEnter/Exit`, fog overrides, `canSeeItself`, per-portal `maxRecursions` (one global limit of 3 instead), the fog-end distance cull, and `supportInfiniteRecursion`'s fake textures. `appearsInRecursions` and `canSeePortalLayer` are honoured.
- **Moving portals and objects seen through a portal**: the motion of objects behind a portal is rotated into camera space, but a portal that itself moves makes the virtual scene move without motion vectors: history is rejected there (correct, but noisier) until it stops.
- **The game's own cubemap reflection** reconstructed for hybrid (`GameCubeTerm`) uses the portal exit point as the eye instead of the virtual camera. The error is small (the direction to each vertex differs slightly).
- **Particles, blood and transparent objects** the game draws in portal views are not traced, as everywhere.
