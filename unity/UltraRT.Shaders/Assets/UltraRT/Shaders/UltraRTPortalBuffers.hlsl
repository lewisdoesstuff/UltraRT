#ifndef ULTRART_PORTAL_BUFFERS_INCLUDED
#define ULTRART_PORTAL_BUFFERS_INCLUDED

// The portal sides' global buffers (SceneProxies.BindPortalBuffer, set every frame, also with portals off), for
// UltraRTPortals.hlsl (ray teleportation) and UltraRTLights.hlsl (lights imaged through portals), which some
// includers use without the other. See docs/portals.md.

StructuredBuffer<float4> _UltraRTPortals; // per portal side: 3 rows of its 3x4 travel matrix (world -> world)
// Per portal side: 3 rows of world -> its quad's space (the quad is x, y in -0.5..0.5 at z = 0; z in metres along the
// side's forward).
StructuredBuffer<float4> _UltraRTPortalQuads;
// Per portal side: the same 3 rows composed with the inverse of its travel: a point on the exit's side straight into the
// side's quad space (SceneProxies.BindPortalBuffer). For lights imaged through the side (UltraRTLights.hlsl), which
// tested their opening by carrying points back through the travel matrix: three loads instead of six and no matrix,
// which kept that code's registers out of the light-sampling loop (docs/performance.md, "2026-11-20 batch").
StructuredBuffer<float4> _UltraRTPortalImageQuads;

// Did the segment from a to b go through side s's opening? For a moving portal's history: what slides into view at
// the opening's edge was behind the wall beside it a frame ago (docs/portals.md, "Edges of a moving portal"); and for a
// light imaged through a portal, whether it reaches a point (UltraRTLights.hlsl, "Portal images").
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

// PortalSegmentThrough(side, back(a), back(b)) for points a, b on the side's exit (back: the inverse travel), on the
// composed rows. Quad space is affine in the world, so the crossing is found in the world and tested there.
bool PortalImageSegmentThrough(uint side, float3 a, float3 b)
{
    uint s = side * 3u;
    float4 r2 = _UltraRTPortalImageQuads[s + 2u];
    float za = dot(r2, float4(a, 1.0)), zb = dot(r2, float4(b, 1.0));
    float dz = za - zb;
    if (abs(dz) < 1e-6) return false;
    float t = za / dz;
    if (t <= 0.0 || t >= 1.0) return false;
    float4 h = float4(lerp(a, b, t), 1.0);
    if (abs(dot(_UltraRTPortalImageQuads[s], h)) > 0.5) return false;
    return abs(dot(_UltraRTPortalImageQuads[s + 1u], h)) <= 0.5;
}

// Side s's travel matrix as rotation m and offset t: x -> mul(m, x) + t. Rigid for every non-mirror side, so its inverse
// is x -> mul(x - t, m) (PortalBack*).
void PortalTravel(uint s, out float3x3 m, out float3 t)
{
    uint k = s * 3u;
    float4 r0 = _UltraRTPortals[k], r1 = _UltraRTPortals[k + 1u], r2 = _UltraRTPortals[k + 2u];
    m = float3x3(r0.xyz, r1.xyz, r2.xyz);
    t = float3(r0.w, r1.w, r2.w);
}
float3 PortalBackPoint(float3x3 m, float3 t, float3 x) { return mul(x - t, m); }
float3 PortalBackDir(float3x3 m, float3 d) { return mul(d, m); }

#endif
