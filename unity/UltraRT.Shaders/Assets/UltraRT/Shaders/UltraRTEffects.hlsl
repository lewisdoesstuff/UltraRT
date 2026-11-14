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
// global: x brightness of effects seen by rays; y roughness up to which a lobe shows them in full; z their weight in lobes
// of roughness 0.6 and above; w cover gain (EffectCover)
float4 _UltraRTEffectParams;

// How much of the effects this pixel's rays show: 1, less where the game draws an effect over the pixel itself (set by
// the ray generation shader from EffectCover). The billboards face the camera, so whatever is behind a flame mirrors
// it onto the very pixels the flame covers, and flame plus reflection clipped to white.
static float g_EffectVisible = 1.0;

// How much of the effects a specular lobe of this roughness shows. A smooth surface (a screen, glass, polished metal)
// mirrors a flame; on rough metal the same term is a wide glow that sits behind the flame itself (the billboards face
// the camera, so does their reflection in whatever is behind them) and reads as the flame blown out. Rough lobes keep a
// share of it; the effect's point light already lights such surfaces.
float EffectLobeWeight(float roughness)
{
    return lerp(1.0, _UltraRTEffectParams.z, smoothstep(_UltraRTEffectParams.y, 0.6, roughness));
}

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
    return max(p.albedo, 0.0) * (_UltraRTEffectParams.x * g_EffectVisible); // max: also drops NaN
}

// Camera rays (once per pixel, only while effects are in the RTAS): how strongly the game's own effects cover this pixel
// up to the primary hit; fades the effects out of this pixel's reflections accordingly.
void EffectCover(float3 origin, float3 dir, float tMax)
{
    g_EffectVisible = 1.0;
    if (_UltraRTEffects <= 0 || _UltraRTEffectParams.w <= 0.0) return;
    float3 e = EffectsAlong(origin, dir, tMax);
    g_EffectVisible = 1.0 - saturate(dot(e, float3(0.2126, 0.7152, 0.0722)) / max(_UltraRTEffectParams.x, 1e-3) * _UltraRTEffectParams.w);
}

#endif
