#ifndef ULTRART_STAINED_GLASS_INCLUDED
#define ULTRART_STAINED_GLASS_INCLUDED

// Stained-glass projection (docs/materials.md, "Stained-glass projection"; Plugin.StainedGlass.cs). Most of ULTRAKILL's
// stained glass has no sun behind it, so the panes never light anything. Each window found at the scene build sends a
// collimated beam into the room (along _UltraRTStainedGlass's direction, tilted down from the window's inward normal):
// a surface point looks back along the beam to the window plane, reads the pane's colour there (the window's texture,
// baked into one slice of _UltraRTStainedGlassTex at the window's own size) and traces one shadow ray to the window, so
// pillars, arches, V1 and the enemies cut the mosaic, and the dark leading (zero light) draws crisp lines in it.
// Primary hits only (relight and pathtrace), as direct light: it goes through the denoiser / DLSS RR like any light.
//
// The includer must declare, before this file: UltraRTLights.hlsl (VisibleMasked, g_ShadowTint, ToLight,
// RelightExposure), Rand(inout uint).

// Per window, 5 x float4 (Plugin.StainedGlass.cs, StainedGlassWindow.Pack):
//   [0] xyz window centre, w texture-array slice
//   [1] xyz U (unit, along the width), w 1 / width
//   [2] xyz V (unit, along the height), w 1 / height
//   [3] xyz beam direction (unit, into the room), w reach (m)
//   [4] xyz inward normal (unit), w one texel of the slice in metres
StructuredBuffer<float4> _UltraRTStainedGlass;
int _UltraRTStainedGlassCount;
// x strength (0 = off), y leading floor (gamma colour below it passes nothing), z saturation, w debug: 1 no shadow ray,
// 2 also white wherever a beam reaches (the texture ignored), 3 bright magenta there (no cosine, no texture)
float4 _UltraRTStainedGlassParams;
Texture2DArray<float4> _UltraRTStainedGlassTex;
SamplerState my_point_clamp_sampler_UltraRTStainedGlassTex;

// The light the windows send to a primary hit (lighting space, like DirectLighting). pos: the hit, n: shading normal,
// nv: the geometric normal facing the ray. At most one shadow ray: where
// several beams overlap, one is picked by its unshadowed brightness and weighted (exact where only one reaches, which
// is nearly everywhere: the beams are window-sized).
float3 StainedGlassLight(float3 pos, float3 n, float3 nv, inout uint rng)
{
    const float3 lw = float3(0.2126, 0.7152, 0.0722);
    float strength = _UltraRTStainedGlassParams.x;
    float leadFloor = saturate(_UltraRTStainedGlassParams.y);
    float sat = _UltraRTStainedGlassParams.z;
    float wSum = 0.0, pickW = 0.0, pickT = 0.0;
    float3 pick = 0, pickL = 0;
    float dbg = _UltraRTStainedGlassParams.w;
    if (dbg > 3.5 && dbg < 4.5) return float3(0, 20, 0); // debug 4: any window bound
    for (int i = 0; i < _UltraRTStainedGlassCount; i++)
    {
        float4 a = _UltraRTStainedGlass[i * 5];
        float4 d = _UltraRTStainedGlass[i * 5 + 3];
        float4 e = _UltraRTStainedGlass[i * 5 + 4];
        float3 L = -d.xyz; // towards the window
        float ndl = dot(n, L);
        float denom = dot(L, e.xyz); // < 0: the beam enters the room through the inward side
        if (ndl <= 0.01 || dot(nv, L) <= 0.0 || denom > -1e-3) continue;
        if (dbg > 4.5 && dbg < 5.5) return float3(0, 20, 0); // debug 5: faces a beam
        float t = dot(a.xyz - pos, e.xyz) / denom;
        if (t <= 0.05 || t > d.w) continue; // behind the window, or beyond the beam's reach
        if (dbg > 5.5) return float3(0, 20, 0); // debug 6: within a beam's reach
        float4 bu = _UltraRTStainedGlass[i * 5 + 1];
        float4 bv = _UltraRTStainedGlass[i * 5 + 2];
        float3 x = pos + L * t - a.xyz;
        float2 uv = float2(dot(x, bu.xyz) * bu.w + 0.5, dot(x, bv.xyz) * bv.w + 0.5);
        if (any(uv <= 0.0) || any(uv >= 1.0)) continue;
        if (_UltraRTStainedGlassParams.w > 2.5) return float3(20, 0, 20); // debug 3: magenta wherever a beam reaches
        // Point sampled at full resolution (no mips: Plugin.StainedGlass.cs, SgBake), like the game's pixel-art panes.
        float4 g = _UltraRTStainedGlassTex.SampleLevel(my_point_clamp_sampler_UltraRTStainedGlassTex, float3(uv, a.w), 0);
        if (_UltraRTStainedGlassParams.w > 1.5) g = float4(1, 1, 1, 1);
        // g: the pane's (gamma) colour, alpha-premultiplied by the bake's coverage (0 off the glass). Below the floor the
        // leading passes nothing; above it the colour is stretched back to full and saturated a little.
        float3 col = saturate((g.rgb - leadFloor * g.a) / max(1.0 - leadFloor, 1e-3));
        float lum = dot(col, lw);
        col = max(lerp(lum.xxx, col, sat), 0.0);
        float3 c = ToLight(col * (strength * RelightExposure())) * ndl;
        float w = dot(c, lw);
        if (w <= 1e-6) continue;
        wSum += w;
        // The first contributing window is taken without a random number, so pixels with one beam stay deterministic.
        bool take = pickW <= 0.0;
        if (!take) take = Rand(rng) * wSum < w;
        if (take) { pick = c; pickL = L; pickT = t; pickW = w; }
    }
    if (pickW <= 0.0) return 0;
    float3 lit = pick * (wSum / pickW);
    [branch] if (_UltraRTStainedGlassParams.w > 0.5) return lit; // debug: unshadowed
    // Stop short of the window (and the wall around it); stained glass on the way tints (g_ShadowTint).
    if (!VisibleMasked(pos, nv, pickL, max(0.0, pickT - 0.15), 0x05u)) return 0;
    return lit * g_ShadowTint;
}

#endif
