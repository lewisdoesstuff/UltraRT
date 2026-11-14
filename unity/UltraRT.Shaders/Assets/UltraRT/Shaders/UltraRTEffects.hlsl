#ifndef ULTRART_EFFECTS_INCLUDED
#define ULTRART_EFFECTS_INCLUDED

// The effects pass (docs/path-tracing.md, "Effects in reflections"): what the game's glowing effects (fire, explosions,
// flashes, beams: EffectGeometry.cs) add along a ray segment. Their billboards sit in the RTAS under the transparent
// mask with a non-opaque hit group (EffectProxy.shader); water and glass under the same mask are opaque, so
// RAY_FLAG_CULL_OPAQUE leaves only the effects. The any-hit shader accumulates into payload.albedo and ignores the hit.
//
// Used after a specular ray found its hit (reflections, mirror-like path vertices). Camera rays don't need it: the game
// draws its effects itself, at full resolution. Diffuse bounces don't either: effects light the scene as (virtual)
// point lights already (EffectLights.cs), and counting their billboards too would light it twice.
//
// Needs RayPayload (UltraRTCommon.hlsl) and _Scene.

int    _UltraRTEffects;      // global: effect instances in the RTAS this frame (0: no ray is traced)
float4 _UltraRTEffectParams; // global: x brightness of effects seen by rays

// Radiance the effects add between origin and origin + dir * tMax, in the game's display units (what its additive
// blend adds to the frame).
float3 EffectsAlong(float3 origin, float3 dir, float tMax)
{
    if (_UltraRTEffects <= 0) return 0;
    RayDesc r;
    r.Origin = origin;
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = tMax;
    RayPayload p = (RayPayload)0;
    p.flags = ULTRART_PAYLOAD_EFFECTS;
    TraceRay(_Scene, RAY_FLAG_CULL_OPAQUE | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, ULTRART_MASK_TRANSPARENT, 0, 1, 0, r, p);
    return max(p.albedo, 0.0) * _UltraRTEffectParams.x; // max: also drops NaN
}

#endif
