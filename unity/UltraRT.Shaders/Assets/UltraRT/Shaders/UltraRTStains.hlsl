#ifndef ULTRART_STAINS_INCLUDED
#define ULTRART_STAINS_INCLUDED

// Blood stains at reflection hits (docs/path-tracing.md, "Blood stains in reflections"). The game draws its stains as
// screen-space decals multiplied over the frame, so the camera view has them but no ray did. StainGrid.compute puts the
// stains near the camera into a world-space hash grid each frame; StainAt finds the one covering a hit point with the
// game's own box, normal and atlas-shape tests, and StainTint turns it into the factor the game's composite applies.
// The game (Bloodstain_ProceduralInstancing + PostProcess/CompositeBlood pass 1, read from its DXBC): the stamp writes
// t = saturate((far - camera distance) / (far - near)) with the fog distances, the composite multiplies the lit frame by
// (1, 1 - t, 1 - t): a stain keeps red and takes green and blue, fading with distance like the fog.

#include "UltraRTStainGrid.hlsl"

StructuredBuffer<uint> _StainGrid;
StructuredBuffer<StainRecord> _StainRecords;
Texture2D _StainAtlas;           // the game's stain shapes (_SplatterAtlas): 5 cells side by side, alpha = shape
SamplerState my_point_repeat_sampler_StainAtlas;
float4 _StainParams;             // x on (0/1; 2 = debug: stains bright green), y darkening of a wet stain (0-1), z fog near, w fog far (m; the composite's fade)
float4 _StainCamPos;             // xyz camera position (the fade is by distance from it, as the game's)
float4 _StainModes;              // x 1 = at reflection hits (StainTint), y 1 = at camera hits behind portals (StainTintAt)

// Coverage (0/1) of the stain at world point p with surface normal n (either side), and that stain's wetness.
float StainAt(float3 p, float3 n, out float wet)
{
    wet = 0.0;
    if (_StainParams.x < 0.5) return 0.0;
    uint b = StainBucket((int3)floor(p / STAIN_CELL));
    uint base = b * (STAIN_BUCKET + 1u);
    uint count = min(_StainGrid[base], STAIN_BUCKET);
    [loop] for (uint k = 0; k < count; k++)
    {
        StainRecord r = _StainRecords[_StainGrid[base + 1u + k]];
        float3 d = p - r.centre;
        float3 l = float3(dot(r.ax, d), dot(r.ay, d), dot(r.az, d));
        if (abs(l.x) > 2.0 || abs(l.y) > 2.0 || abs(l.z) > r.halfZ) continue;
        bool clipped = r.flags >= 1.0; // flags = clip (0/1) + wetness / 2
        if (clipped && dot(n, r.az) < 0.5) continue;
        float2 uv = l.xy * r.uvScale + 0.5;
        float a = _StainAtlas.SampleLevel(my_point_repeat_sampler_StainAtlas, frac(uv) * float2(0.2, 1.0) + float2(r.cell, 0.0), 0).a;
        if (a < 0.5 || any(uv < 0.0) || any(uv > 1.0)) continue;
        wet = saturate((r.flags - (clipped ? 1.0 : 0.0)) * 2.0);
        return 1.0;
    }
    return 0.0;
}

// The factor the game's blood composite would put on a surface at p (1 = no stain), faded over the fog distances by
// `viewDistance` (the game's composite: the camera's distance; behind a portal, the way the camera ray went).
float3 StainTintAt(float3 p, float3 n, float viewDistance)
{
    float wet;
    float c = StainAt(p, n, wet);
    if (c <= 0.0) return 1.0;
    if (_StainParams.x > 1.5) return float3(0.0, 6.0, 0.0);
    float t = saturate((_StainParams.w - viewDistance) / max(_StainParams.w - _StainParams.z, 1e-3));
    return float3(1.0, 1.0 - t, 1.0 - t) * (1.0 - _StainParams.y * wet);
}

// The part of a stain tint that goes on the surface's emission: all of it (the game's composite multiplies the finished
// pixel, glow included; since 2026-11-22).
float3 StainEmissionTint(float3 tint) { return tint; }

// At reflection hits.
float3 StainTint(float3 p, float3 n)
{
    if (_StainModes.x < 0.5) return 1.0;
    return StainTintAt(p, n, distance(p, _StainCamPos.xyz));
}

#endif
