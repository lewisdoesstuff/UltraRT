# Portals and portal mirrors

Layer 8 (8-1 to 8-4) builds its non-euclidean spaces from portals: a doorway shows, and leads to, a place somewhere else in the level. Mirrors there are portals too. This page covers how the game implements them, what ray tracing did with them before, and how rays now follow them.

Files:
- `src/UltraRT/SceneProxies.Portals.cs`: reads the game's portals, keeps one quad instance per rendered portal side in the RTAS, and uploads the travel matrices.
- `unity/.../Portal.shader`: the portal quads' hit shader.
- `unity/.../UltraRTPortals.hlsl`: `TraceScene`, the portal-following `TraceRay` used by every camera, bounce, reflection and cache ray.
- `unity/.../PathTrace.raytrace`, `CacheUpdate.raytrace`, `DebugView.raytrace`: use `TraceScene`.
- `unity/.../UltraRTLights.hlsl`: ReSTIR reprojects through portals.
- `unity/.../Composite.shader`: the depth test at portal pixels.

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

**Other portal-aware systems**: `PortalAwareRenderer` clones renderers that straddle a portal (`PortalAwareRendererClone`), enemies get "(PortalOffset)" transforms, particles, audio (`VirtualAudioListener`), navigation links and the player's traversal (`PortalTraversalV2`, `CheckPlayerTraversals`) all use the same `NativePortalScene`. The game's "Portal" layer (30) is not used by the render path above.

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

**`TraceScene`** (`UltraRTPortals.hlsl`) replaces `TraceRay` for every `ULTRART_MASK_SCENE` ray. On a portal hit it moves the ray through the side's matrix (origin = travel * hit point, nudged 1 mm along the new direction; direction = travel's rotation * direction; `TMax` shortened by the distance covered) and traces again, up to `ULTRART_PORTAL_MAX_HOPS` = 3 teleports. Past that the ray ends on a black, unlit surface (the game stops at its recursion limit too). It returns the last segment in `ray`, so callers keep computing the hit as `ray.Origin + ray.Direction * p.t`, plus a `PortalPath`: the accumulated rotation, the path length before the last segment, and the distance to the first portal.

Where it is used:
- **Primary rays** (PathTrace, DebugView, the cache's primary rays): shading, lights, shadows, cache lookups and the reflection ray all happen at the real hit on the far side.
- **Bounce rays** (PathTrace, CacheUpdate): bounce light comes through doorways from the right room.
- **Reflection rays**: a mirror sees through portals; the reported hit distance is the full path length.
- **Water and glass** (the transparent pass at the end of RayGen): along the camera ray up to the opaque hit or the first portal, whichever is nearer; water seen through a portal gets no RT reflection overlay (the overlay is drawn by the main camera only).
- **Not shadow rays**: light sampling is in world space, so a surface is lit by the lights of the room it really is in. Lights shining through a portal into another space are not modelled yet (see Limits).

**The G-buffer stays in camera space.** Because the travel matrices are rigid, the far side seen through a portal is a rigid copy of it in front of the camera (the "virtual" scene). Primary hits write the virtual view: `_NormalDepth` = (rotation^-1 * normal, full path length), the motion in `_InstanceId.y` rotated the same way. The denoiser, the reflection filter and ReSTIR rebuild positions as `camPos + dir * t`, so they reproject surfaces behind a portal like any other: static portals need no special case. ReSTIR's reuse (`UltraRTLights.hlsl`, `g_PrimaryVirtual*`) reprojects and stores the reservoir plane with the virtual point and normal and evaluates lights at the real one.

## 4. Compositing

The game's depth buffer at a portal pixel holds whatever the portal draw left there; the RT path through the portal is longer than the distance to the portal plane. Primary rays that crossed a portal store the distance to the first portal surface in the high half of `_InstanceId.x` (half float; the denoiser and reflection filter only use the instance's low 7 bits, and the low 16 bits keep the instance). `Composite.shader`'s depth test and its depth-guided upsampling accept a match with either distance, so RT applies at portal pixels whichever depth the game writes there. Everything else (objects in front of a portal, glow quads) is tested as before.

`-ultrart-compdebug 5` (bench: `hybrid+plugin._compositeDebug=5`) colours through-portal pixels by what the game's depth matched: green = the portal plane, blue = the far-side hit, red = neither (the composite then keeps the game's pixel); other pixels show the game's colour darkened.

## 5. Settings

| Flag / variant | Effect |
|---|---|
| `-ultrart-noportals`, bench `plugin._portals=0` | no portal quads: rays go straight through, as before (A/B) |
| `-ultrart-compdebug 5` | portal depth diagnostic (hybrid) |
| `ULTRART_PORTAL_MAX_HOPS` (UltraRTPortals.hlsl) | teleports per ray (3) |
| `SceneProxies.MaxPortalSides` | 1024 |

Logs: `Portal side N: '<path>' enter|exit|mirror WxH at <pos> fwd <dir>, render <flags>, recursions R, disableRange D` the first time each side appears (first 64), and `Portals: N portal sides in the RTAS (M known)` when the set changes (first 40).

## 6. Results

(See below.)

## 7. Limits

- **Lights through portals**: a light in room A does not light room B through a portal between them, and shadow rays ignore portals. The game fakes this for `PortalAwareLight`s (up to 16 transformed copies, clipped by the far plane). Doing it in RT means virtual lights (a light's image through each nearby portal, sampled with a plane clip and a shadow ray that is teleported back) in the light list.
- **Per-portal overrides** the game applies to a portal view are not applied to rays: `overrideSkyboxEnter/Exit`, fog overrides, `canSeeItself`, `appearsInRecursions`, `maxRecursions` (one global limit of 3 instead).
- **Moving portals and objects seen through a portal**: the motion of objects behind a portal is rotated into camera space, but a portal that itself moves makes the virtual scene move without motion vectors: history is rejected there (correct, but noisier) until it stops.
- **The game's own cubemap reflection** reconstructed for hybrid (`GameCubeTerm`) uses the portal exit point as the eye instead of the virtual camera. The error is small (the direction to each vertex differs slightly).
- **Particles, blood and transparent objects** the game draws in portal views are not traced, as everywhere.
