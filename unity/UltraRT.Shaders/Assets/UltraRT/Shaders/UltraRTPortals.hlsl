#ifndef ULTRART_PORTALS_INCLUDED
#define ULTRART_PORTALS_INCLUDED

// Ray teleportation through ULTRAKILL's portals (layer 8) and portal mirrors. See docs/portals.md.
//
// Every rendered portal side is a quad in the RTAS (instance mask SceneProxies.PortalMask = 0x20, shader UltraRT/Portal).
// Its hit shader reports the hit with ULTRART_FLAG_PORTAL and the side's index in p.instance; the ray generation shader
// moves the ray through the side's travel matrix (exit * scale(-1, 1, -1) * inverse(entry), or a reflection about the
// mirror plane) and traces on, at most ULTRART_PORTAL_MAX_HOPS times. Shadow rays (mask 0x05) never see portals.
// Only the front of a side teleports (rays moving along its forward axis, as PortalMath.Raycast); the back is skipped
// in the any-hit shader, so the level behind a portal stays visible from behind, as in game.

#define ULTRART_FLAG_PORTAL 0x20000000u // payload flags (portal hit -> ray gen); bit 29 is free in the material flags
// The same bit as a request (ray gen -> hit): this ray already went through a portal. Sides with appearsInRecursions off
// ignore such rays, as the game skips them in portal views (PortalRenderV2.GetOnscreenPortalsBurst, depth > 0).
#define ULTRART_PORTAL_IN_RECURSION 0x20000000u
// Layer-30 renderers (SceneProxies.PortalLayerMask): only portal views with canSeePortalLayer show them, so rays see them
// only after crossing such a portal; the portal hit reports the flag in p.material bit 0.
#define ULTRART_MASK_PORTAL_LAYER 0x40u
#define ULTRART_MASK_PORTAL 0x20u // SceneProxies.PortalMask
// TraceScene request (inFlags, never sent to a hit shader): stop at a portal mirror and report a miss there with
// path.mirrorT set. Camera rays use it so the composites keep the game's own mirror view (docs/portals.md, "Mirrors").
#define ULTRART_TRACE_STOP_AT_MIRROR 0x10000000u
// TraceScene request (never sent to a hit shader): collect the water and glass on every segment after a hop into
// g_PortalGlass (docs/portals.md, "Water and glass through portals"). The game's portal views draw them; the pixel
// that replaces such a view has to as well. Camera rays of PathTrace only (ULTRART_PORTAL_GLASS defined).
#define ULTRART_TRACE_PORTAL_GLASS 0x00400000u
// The portal hit's p.material (Portal.shader): bit 0 canSeePortalLayer, bit 1 mirror, bit 2 canSeeItself off, bit 3
// supportInfiniteRecursion, bits 8-15 maxRecursions (the game's own, before the raster trim), bits 16-23 the sky slot
// behind the side (overrideSkyboxEnter/Exit; 0 = none). docs/portals.md, "Recursion rules and per-portal skies".
#define ULTRART_PORTAL_NO_SELF 4u
#define ULTRART_PORTAL_INFINITE 8u
uint PortalMaxRecursions(uint word) { return (word >> 8) & 0xFFu; }
uint PortalSkySlot(uint word) { return (word >> 16) & 0xFFu; }

#ifndef ULTRART_PORTALS_DEFINES_ONLY

#include "UltraRTPortalBuffers.hlsl" // _UltraRTPortals, _UltraRTPortalQuads, PortalSegmentThrough
StructuredBuffer<float4> _UltraRTPortalsPrev; // the same, last traced frame's (a moving portal: docs/portals.md, "Temporal")

float _UltraRTPortalStart; // metres past the exit plane where a continued ray starts (0: 1 mm; bench plugin._portalStart)
int _UltraRTPortalHops; // teleports per ray (SceneProxies.PortalHops, default 3); global, set every frame
// Rays follow the game's per-portal recursion limits (maxRecursions of the portal last crossed, and
// supportInfiniteRecursion's deeper loops with _UltraRTPortalInfHops) and canSeeItself (docs/portals.md, "Recursion rules").
int _UltraRTPortalInfHops; // teleports through supportInfiniteRecursion portals (SceneProxies.PortalInfiniteHops)
// 1: camera rays reflect off portal mirrors like any portal (-ultrart-rtmirrors); 0 (default): they stop there and the
// game's mirror view is kept. Global, set every frame.
int _UltraRTMirrors;
// 1 while the game's raster portal views are skipped (PortalRasterSkip.cs; docs/portals.md, "Raster skip"): the trace then
// owns every pixel seen through a portal, the sky behind one included (PathTrace writes it as a far emitter), and the
// composites accept a through-portal sample wherever nothing stands in front of the portal plane. Global, every frame.
int _UltraRTPortalOwn;

// V1's third-person model (instance mask ULTRART_MASK_PLAYER, SceneProxies.Player.cs). Which rays see it (Plugin's
// _playerRays, global, set every frame; 0 = none): 1 reflection rays, 2 bounce rays, 4 shadow rays, 8 camera rays after
// a hop through a portal whose canSeePortalLayer is set (the game draws V1, layer 30, in those views: 8-x mirrors),
// 16 camera rays before a hop (only a third-person cinematic shot sets it: docs/cinematics.md; in play the camera sits
// in V1's head).
int _UltraRTPlayerRays;
#define ULTRART_PLAYER_REFLECTION 1
#define ULTRART_PLAYER_BOUNCE     2
#define ULTRART_PLAYER_SHADOW     4
#define ULTRART_PLAYER_PORTAL     8
#define ULTRART_PLAYER_CAMERA     16
uint PlayerRayMask(int kind) { return (_UltraRTPlayerRays & kind) != 0 ? ULTRART_MASK_PLAYER : 0u; }

struct PortalPath
{
    float3x3 rot;     // camera side -> hit side: a direction d seen by the camera is mul(rot, d) at the hit (incl. mirror flips)
    float travelled;  // path length before the last segment (0 without a portal)
    float firstT;     // distance to the first portal surface; -1 without a portal
    uint first;       // that portal side's index (valid with firstT >= 0)
    // Last frame's version of the whole path's transform (camera side -> hit side): x -> mul(prevRot, x) + prevOff.
    // moved: one of the path's portals has another travel matrix than last frame.
    float3x3 prevRot;
    float3 prevOff;
    bool moved;
    uint hops;
    bool capped;      // the ray met a supportInfiniteRecursion side at the hop limit: the payload is that side, unlit black
    uint sky;         // sky slot of the last side crossed that overrides the sky (0 = the level's sky)
    float mirrorT;    // ULTRART_TRACE_STOP_AT_MIRROR: the ray stopped at a portal mirror this far along the path (p.t = -1); else -1
};

// The camera-side ("virtual") version of a direction or normal at the hit: rot is orthonormal.
float3 PortalToVirtual(PortalPath path, float3 d) { return mul(d, path.rot); }

// Ray cone of the next TraceScene with ULTRART_PAYLOAD_CONE in inFlags (docs/materials.md, "Texture LOD"): x its width at
// ray.Origin, y its spread angle. Each segment's hit gets the width at that segment's origin.
static float2 g_Cone = 0;

// The water / glass layers behind portals (ULTRART_TRACE_PORTAL_GLASS), front to back: rgb = sum of transmittance so far
// x blend alpha x albedo (texture x colour), a = the transmittance left for the hit behind them. The game blends each
// surface as W a + B (1 - a); W (the surface lit) is taken as albedo x the hit's own lighting, so the caller blends
// albedo -> albedo a_total + rgb and scales emission by a. Reset by a TraceScene that asks for it: read it right after.
static float4 g_PortalGlass = float4(0, 0, 0, 1);
#ifdef ULTRART_PORTAL_GLASS
float4 _UltraRTPortalGlass; // x strength (x the blend alpha; 0 = off), y water/glass hits per segment (max 2)
// One portal-side segment, origin to tEnd: its nearest one or two transparent surfaces into g_PortalGlass. The hit
// returns its blend alpha in p.motion for this ray (Proxy.shader; UltraRTCommon.hlsl, ULTRART_PAYLOAD_TRANSPARENT).
void PortalGlassSegment(RayDesc seg, float tEnd)
{
    // From the segment's own TMin: 0 after a hop, past a skipped side (canSeeItself off) when the segment goes on.
    seg.TMax = max(tEnd, 0.0);
    uint n = min((uint)_UltraRTPortalGlass.y, 2u);
    [loop] for (uint k = 0u; k < n && g_PortalGlass.a > 0.02; k++)
    {
        RayPayload h = (RayPayload)0;
        h.t = -1.0;
        h.flags = ULTRART_PAYLOAD_TRANSPARENT;
        ULTRART_COUNT_RAY(ULTRART_RAY_TRANSPARENT);
        TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES, ULTRART_MASK_TRANSPARENT, 0, 1, 0, seg, h);
        if (h.t < 0.0) return;
        float a = saturate(asfloat(h.motion) * _UltraRTPortalGlass.x);
        g_PortalGlass.rgb += g_PortalGlass.a * a * h.albedo;
        g_PortalGlass.a *= 1.0 - a;
        seg.TMin = h.t + 0.01; // the next surface (the hall's other wall)
    }
}

// Glowing effects behind portals (docs/portals.md, "Effects and Providence through portals"): fire, explosions, flashes,
// projectiles, trails and glowing enemy parts (EffectGeometry.cs) that the game's portal views drew. The traced pixel
// replaces those views (raster skip, or the replace composites at opaque pixels), so every camera-ray segment past a hop
// collects them with the effects ray (EffectsAlongRange: one any-hit ray over the billboards, mask 0x10, opaque water and
// glass culled) into g_PortalFx, in display units, dimmed by the water and glass of earlier segments. The ray generation
// shader adds it to the hit's emission. Requested with g_PortalFxWanted (only while effects are in the RTAS: no cost
// otherwise).
#include "UltraRTEffects.hlsl"
int _UltraRTPortalEffects; // 1: collect effects behind portals (EffectGeometrySettings.portalEffects; -ultrart-portal-effects)
static bool g_PortalFxWanted = false;
static float3 g_PortalFx = 0;
static float g_PortalFxT = 1.0; // the share of the hit (or sky) that shows through alpha-blended glow meshes

// One segment past a hop, seg.TMin to tEnd: its effects (before this segment's water and glass: an approximation, they
// are not sorted against each other) and its water and glass.
void PortalLayersSegment(RayDesc seg, float tEnd, bool glass)
{
    [branch] if (g_PortalFxWanted)
    {
        float4 e = EffectsAlongT(seg.Origin, seg.Direction, seg.TMin, max(tEnd, 0.0));
        g_PortalFx += g_PortalGlass.a * g_PortalFxT * e.rgb;
        g_PortalFxT *= e.a;
    }
    [branch] if (glass) PortalGlassSegment(seg, tEnd);
}
#endif

// TraceRay that follows portals. On return `ray` is the last segment, so the hit point is ray.Origin + ray.Direction * p.t
// as before, and the full path length is path.travelled + p.t. inFlags: request bits for the hit shader (re-sent each hop).
void TraceScene(uint rayFlags, uint mask, uint inFlags, inout RayDesc ray, inout RayPayload p, out PortalPath path)
{
    path.rot = float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1);
    path.travelled = 0.0;
    path.firstT = -1.0;
    path.first = 0u;
    path.prevRot = path.rot;
    path.prevOff = 0.0;
    path.moved = false;
    path.hops = 0u;
    path.capped = false;
    path.sky = 0u;
    path.mirrorT = -1.0;
    uint lastSide = 0xFFFFFFFFu, lastWord = 0u;
    bool stopAtMirror = (inFlags & ULTRART_TRACE_STOP_AT_MIRROR) != 0u;
#ifdef ULTRART_PORTAL_GLASS
    bool portalGlass = (inFlags & ULTRART_TRACE_PORTAL_GLASS) != 0u;
    if (portalGlass || g_PortalFxWanted) g_PortalGlass = float4(0, 0, 0, 1);
    g_PortalFx = 0;
    g_PortalFxT = 1.0;
    bool portalLayers = portalGlass || g_PortalFxWanted;
#endif
    inFlags &= ~(ULTRART_TRACE_STOP_AT_MIRROR | ULTRART_TRACE_PORTAL_GLASS);
    uint baseMask = mask & ~(ULTRART_MASK_PORTAL_LAYER | ULTRART_MASK_PLAYER);
    // Past a hop, layer 30 (and V1, which lives on it) shows only through portals that can see that layer.
    uint portalLayerMask = ULTRART_MASK_PORTAL_LAYER | (((mask & ULTRART_MASK_PLAYER) != 0u) ? ULTRART_MASK_PLAYER : PlayerRayMask(ULTRART_PLAYER_PORTAL));
    mask &= ~ULTRART_MASK_PORTAL_LAYER;
    uint i = 0u; // teleports so far
    [loop] for (uint it = 0u; ; it++)
    {
        p = (RayPayload)0;
        p.t = -1.0;
        p.flags = inFlags | (i > 0u ? ULTRART_PORTAL_IN_RECURSION : 0u);
        if ((inFlags & ULTRART_PAYLOAD_CONE) != 0u) p.albedo = float3(g_Cone.x + g_Cone.y * path.travelled, g_Cone.y, 0.0);
        ULTRART_COUNT_TRACE(i);
        TraceRay(_Scene, rayFlags, mask, 0, 1, 0, ray, p);
        if (p.t < 0.0) p.albedo = 0; // a miss leaves the cone input there
#ifdef ULTRART_PORTAL_GLASS
        // Segments past a hop (on the near side the game draws its own, with the overlay): water, glass and glowing
        // effects (PortalLayersSegment). Not on a segment that ends at a portal mirror the camera ray stops at: the
        // game's mirror view keeps its own.
        [branch] if (portalLayers && i > 0u && !(stopAtMirror && (p.flags & ULTRART_FLAG_PORTAL) != 0u && (p.material & 2u) != 0u))
            PortalLayersSegment(ray, p.t >= 0.0 ? p.t : ray.TMax, portalGlass);
#endif
        if (p.t < 0.0 || (p.flags & ULTRART_FLAG_PORTAL) == 0u) return;
        if (path.firstT < 0.0) { path.firstT = p.t; path.first = p.instance; } // first segment: travelled is 0
        if (stopAtMirror && (p.material & 2u) != 0u)
        {
            path.mirrorT = path.travelled + p.t;
            p.t = -1.0;
            return;
        }
        // canSeeItself off: the game's view through a side does not draw that side (PortalRenderV2.Render, "!canSeeItself &&
        // i == handleIndex"), so the level behind it shows there. Not a hop: trace on past the quad.
        if (i > 0u && p.instance == lastSide && (lastWord & ULTRART_PORTAL_NO_SELF) != 0u && it < 24u)
        {
            ray.TMin = p.t + 1e-3;
            continue;
        }
        // Hop limit. The game renders the view behind the i-th crossing only while i <= maxRecursions of the portal
        // crossed last (PortalRenderV2.BurstRenderData: depth > maxDepth), and never deeper than its root's 4; we keep
        // the global _UltraRTPortalHops (3) as a floor of that depth so ordinary portals cost what they did. A loop of
        // supportInfiniteRecursion portals goes on to _UltraRTPortalInfHops: the game pastes last frame's view onto the
        // deepest side there (fake textures), so its shaft never ends.
        bool allowed;
        if (i == 0u) allowed = true;
        else if ((lastWord & ULTRART_PORTAL_INFINITE) != 0u) allowed = i < (uint)_UltraRTPortalInfHops;
        else
        {
            uint maxRec = PortalMaxRecursions(lastWord);
            allowed = i <= maxRec && i < max((uint)_UltraRTPortalHops, min(maxRec + 1u, (uint)_UltraRTPortalInfHops));
        }
        if (!allowed && (p.material & ULTRART_PORTAL_INFINITE) != 0u)
        {
            // The end of an infinite loop: report the side as an unlit black surface (albedo 0, lit weight 0: a bounce
            // ray gets no light from it, not the sky), and let the camera ray's caller fill it (PathTrace: last
            // frame's image of the same side, as the game's fake recursion textures do).
            path.capped = true;
            p.flags &= ~ULTRART_FLAG_PORTAL;
            return;
        }
        if (!allowed)
        {
            // Past the recursion limit the game doesn't draw the portal at all, so the level behind it shows: go on
            // straight, without portals (also bounds the loop).
            p = (RayPayload)0;
            p.t = -1.0;
            p.flags = inFlags | ULTRART_PORTAL_IN_RECURSION;
            mask &= ~ULTRART_MASK_PORTAL;
            if ((inFlags & ULTRART_PAYLOAD_CONE) != 0u) p.albedo = float3(g_Cone.x + g_Cone.y * path.travelled, g_Cone.y, 0.0);
            ULTRART_COUNT_TRACE(1u);
            TraceRay(_Scene, rayFlags, mask, 0, 1, 0, ray, p);
            if (p.t < 0.0) p.albedo = 0;
#ifdef ULTRART_PORTAL_GLASS
            [branch] if (portalLayers) PortalLayersSegment(ray, p.t >= 0.0 ? p.t : ray.TMax, portalGlass);
#endif
            return;
        }
        uint s = p.instance * 3u;
        float4 r0 = _UltraRTPortals[s], r1 = _UltraRTPortals[s + 1u], r2 = _UltraRTPortals[s + 2u];
        float4 h = float4(ray.Origin + ray.Direction * p.t, 1.0);
        float3x3 m = float3x3(r0.xyz, r1.xyz, r2.xyz);
        {
            float4 q0 = _UltraRTPortalsPrev[s], q1 = _UltraRTPortalsPrev[s + 1u], q2 = _UltraRTPortalsPrev[s + 2u];
            if (any(q0 != r0) || any(q1 != r1) || any(q2 != r2)) path.moved = true;
            float3x3 pm = float3x3(q0.xyz, q1.xyz, q2.xyz);
            path.prevOff = mul(pm, path.prevOff) + float3(q0.w, q1.w, q2.w);
            path.prevRot = mul(pm, path.prevRot);
        }
        float3 d = normalize(mul(m, ray.Direction));
        path.travelled += p.t;
        path.rot = mul(m, path.rot);
        path.hops++;
        mask = (p.material & 1u) != 0u ? baseMask | portalLayerMask : baseMask;
        if (PortalSkySlot(p.material) != 0u) path.sky = PortalSkySlot(p.material);
        lastSide = p.instance;
        lastWord = p.material;
        i++;
        ray.Origin = float3(dot(r0, h), dot(r1, h), dot(r2, h)) + d * (_UltraRTPortalStart > 0.0 ? _UltraRTPortalStart : 1e-3);
        ray.Direction = d;
        ray.TMin = 0.0;
        ray.TMax = max(ray.TMax - p.t, 0.0);
    }
}

// Same, for callers that only need the hit (bounce, reflection, cache rays).
void TraceScene(uint rayFlags, uint mask, inout RayDesc ray, inout RayPayload p, out float travelled)
{
    PortalPath path;
    TraceScene(rayFlags, mask, 0u, ray, p, path);
    travelled = path.travelled;
}

// Same with a ray cone (width at ray.Origin, spread angle) for the hit's texture LOD; (0, 0) = none (mip 0).
void TraceSceneCone(uint rayFlags, uint mask, float2 cone, inout RayDesc ray, inout RayPayload p, out float travelled)
{
    PortalPath path;
    g_Cone = cone;
    TraceScene(rayFlags, mask, any(cone > 0.0) ? ULTRART_PAYLOAD_CONE : 0u, ray, p, path);
    travelled = path.travelled;
}

#endif
#endif
