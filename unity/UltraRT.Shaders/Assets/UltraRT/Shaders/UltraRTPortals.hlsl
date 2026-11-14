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

#ifndef ULTRART_PORTALS_DEFINES_ONLY

#define ULTRART_PORTAL_MAX_HOPS 3

StructuredBuffer<float4> _UltraRTPortals; // per portal side: 3 rows of its 3x4 travel matrix (world -> world)

struct PortalPath
{
    float3x3 rot;     // camera side -> hit side: a direction d seen by the camera is mul(rot, d) at the hit (incl. mirror flips)
    float travelled;  // path length before the last segment (0 without a portal)
    float firstT;     // distance to the first portal surface; -1 without a portal
    uint hops;
};

// The camera-side ("virtual") version of a direction or normal at the hit: rot is orthonormal.
float3 PortalToVirtual(PortalPath path, float3 d) { return mul(d, path.rot); }

// TraceRay that follows portals. On return `ray` is the last segment, so the hit point is ray.Origin + ray.Direction * p.t
// as before, and the full path length is path.travelled + p.t. inFlags: request bits for the hit shader (re-sent each hop).
void TraceScene(uint rayFlags, uint mask, uint inFlags, inout RayDesc ray, inout RayPayload p, out PortalPath path)
{
    path.rot = float3x3(1, 0, 0, 0, 1, 0, 0, 0, 1);
    path.travelled = 0.0;
    path.firstT = -1.0;
    path.hops = 0u;
    [loop] for (uint i = 0u; ; i++)
    {
        p = (RayPayload)0;
        p.t = -1.0;
        p.flags = inFlags;
        TraceRay(_Scene, rayFlags, mask, 0, 1, 0, ray, p);
        if (p.t < 0.0 || (p.flags & ULTRART_FLAG_PORTAL) == 0u) return;
        if (path.firstT < 0.0) path.firstT = p.t;
        if (i >= ULTRART_PORTAL_MAX_HOPS)
        {
            // Deeper than the recursion limit: a black, unlit surface (the game draws nothing past its own limit either).
            float t = p.t;
            uint inst = p.instance;
            p = (RayPayload)0;
            p.t = t;
            p.normal = -ray.Direction;
            p.instance = inst;
            return;
        }
        uint s = p.instance * 3u;
        float4 r0 = _UltraRTPortals[s], r1 = _UltraRTPortals[s + 1u], r2 = _UltraRTPortals[s + 2u];
        float4 h = float4(ray.Origin + ray.Direction * p.t, 1.0);
        float3x3 m = float3x3(r0.xyz, r1.xyz, r2.xyz);
        float3 d = normalize(mul(m, ray.Direction));
        path.travelled += p.t;
        path.rot = mul(m, path.rot);
        path.hops++;
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

#endif
#endif
