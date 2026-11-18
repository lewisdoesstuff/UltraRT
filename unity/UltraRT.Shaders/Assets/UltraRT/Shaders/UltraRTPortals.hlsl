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

#ifndef ULTRART_PORTALS_DEFINES_ONLY

StructuredBuffer<float4> _UltraRTPortals; // per portal side: 3 rows of its 3x4 travel matrix (world -> world)
StructuredBuffer<float4> _UltraRTPortalsPrev; // the same, last traced frame's (a moving portal: docs/portals.md, "Temporal")
// Per portal side: 3 rows of world -> its quad's space (the quad is x, y in -0.5..0.5 at z = 0), and whether the test
// below is on. Global, set every frame (SceneProxies.BindPortalBuffer).
StructuredBuffer<float4> _UltraRTPortalQuads;
int _UltraRTPortalEdge;

// Did the segment from a to b go through side s's opening? For a moving portal's history: what slides into view at
// the opening's edge was behind the wall beside it a frame ago (docs/portals.md, "Edges of a moving portal").
bool PortalSegmentThrough(uint side, float3 a, float3 b)
{
    uint s = side * 3u;
    float4 r0 = _UltraRTPortalQuads[s], r1 = _UltraRTPortalQuads[s + 1u], r2 = _UltraRTPortalQuads[s + 2u];
    float3 la = float3(dot(r0, float4(a, 1.0)), dot(r1, float4(a, 1.0)), dot(r2, float4(a, 1.0)));
    float3 lb = float3(dot(r0, float4(b, 1.0)), dot(r1, float4(b, 1.0)), dot(r2, float4(b, 1.0)));
    float dz = la.z - lb.z;
    if (abs(dz) < 1e-6) return false;
    float t = la.z / dz;
    if (t <= 0.0 || t >= 1.0) return false;
    float2 q = lerp(la.xy, lb.xy, t);
    return abs(q.x) <= 0.5 && abs(q.y) <= 0.5;
}

int _UltraRTPortalHops; // teleports per ray (SceneProxies.PortalHops, default 3); global, set every frame
// 1: camera rays reflect off portal mirrors like any portal (-ultrart-rtmirrors); 0 (default): they stop there and the
// game's mirror view is kept. Global, set every frame.
int _UltraRTMirrors;

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
    float mirrorT;    // ULTRART_TRACE_STOP_AT_MIRROR: the ray stopped at a portal mirror this far along the path (p.t = -1); else -1
};

// The camera-side ("virtual") version of a direction or normal at the hit: rot is orthonormal.
float3 PortalToVirtual(PortalPath path, float3 d) { return mul(d, path.rot); }

// Ray cone of the next TraceScene with ULTRART_PAYLOAD_CONE in inFlags (docs/materials.md, "Texture LOD"): x its width at
// ray.Origin, y its spread angle. Each segment's hit gets the width at that segment's origin.
static float2 g_Cone = 0;

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
    path.mirrorT = -1.0;
    bool stopAtMirror = (inFlags & ULTRART_TRACE_STOP_AT_MIRROR) != 0u;
    inFlags &= ~ULTRART_TRACE_STOP_AT_MIRROR;
    uint baseMask = mask & ~(ULTRART_MASK_PORTAL_LAYER | ULTRART_MASK_PLAYER);
    // Past a hop, layer 30 (and V1, which lives on it) shows only through portals that can see that layer.
    uint portalLayerMask = ULTRART_MASK_PORTAL_LAYER | (((mask & ULTRART_MASK_PLAYER) != 0u) ? ULTRART_MASK_PLAYER : PlayerRayMask(ULTRART_PLAYER_PORTAL));
    mask &= ~ULTRART_MASK_PORTAL_LAYER;
    [loop] for (uint i = 0u; ; i++)
    {
        p = (RayPayload)0;
        p.t = -1.0;
        p.flags = inFlags | (i > 0u ? ULTRART_PORTAL_IN_RECURSION : 0u);
        if ((inFlags & ULTRART_PAYLOAD_CONE) != 0u) p.albedo = float3(g_Cone.x + g_Cone.y * path.travelled, g_Cone.y, 0.0);
        TraceRay(_Scene, rayFlags, mask, 0, 1, 0, ray, p);
        if (p.t < 0.0) p.albedo = 0; // a miss leaves the cone input there
        if (p.t < 0.0 || (p.flags & ULTRART_FLAG_PORTAL) == 0u) return;
        if (path.firstT < 0.0) { path.firstT = p.t; path.first = p.instance; } // first segment: travelled is 0
        if (stopAtMirror && (p.material & 2u) != 0u)
        {
            path.mirrorT = path.travelled + p.t;
            p.t = -1.0;
            return;
        }
        if (i >= (uint)_UltraRTPortalHops)
        {
            // Past the recursion limit the game doesn't draw the portal at all, so the level behind it shows: go on
            // straight, without portals (also bounds the loop).
            p = (RayPayload)0;
            p.t = -1.0;
            p.flags = inFlags | ULTRART_PORTAL_IN_RECURSION;
            mask &= ~ULTRART_MASK_PORTAL;
            if ((inFlags & ULTRART_PAYLOAD_CONE) != 0u) p.albedo = float3(g_Cone.x + g_Cone.y * path.travelled, g_Cone.y, 0.0);
            TraceRay(_Scene, rayFlags, mask, 0, 1, 0, ray, p);
            if (p.t < 0.0) p.albedo = 0;
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
        ray.Origin = float3(dot(r0, h), dot(r1, h), dot(r2, h)) + d * 1e-3;
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
