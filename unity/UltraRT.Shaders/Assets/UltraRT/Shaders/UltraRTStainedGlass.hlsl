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
// RelightExposure), Rand(inout uint), _CamPos, UltraRTWater.hlsl (WaterRippleSourceShift, SchlickFresnel, _UltraRTWater),
// _UltraRTWaterPlanes and _UltraRTRipple (PathTrace.raytrace, ripple caustics).

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
SamplerState my_linear_clamp_sampler_UltraRTStainedGlassTex; // under water only (the blur taps)

// Under water (docs/materials.md, "Stained-glass projection", "Under water"): x absorption per metre of the light's path
// in the water (trans.waterAbsorb x sg.waterAbsorbScale; 0 = none), y transmittance floor of deep water
// (trans.waterAbsorbFloor), z longest path (m), w 1 = on (sg.water; 0: under water looks as on a dry floor)
float4 _UltraRTStainedGlassWater;
// x blur radius per metre of in-water path (m / m), y ripple shift scale (1 = the ripple caustics' own), zw unused
float4 _UltraRTStainedGlassWater2;

// The water surface over pos, from the ripple caustics' planes (the same test as RippleCausticAt): depth under it (m,
// 0 = dry) and its height.
float StainedGlassWaterDepth(float3 pos, out float waterY)
{
    waterY = 0.0;
    uint count = (uint)_UltraRTRipple.y;
    for (uint i = 0u; i < count; i++)
    {
        float4 r = _UltraRTWaterPlanes[2u * i];
        if (pos.x < r.x || pos.x > r.z || pos.z < r.y || pos.z > r.w) continue;
        float4 h = _UltraRTWaterPlanes[2u * i + 1u];
        float depth = h.x - pos.y;
        if (depth <= 0.0 || pos.y < h.y) continue;
        waterY = h.x;
        return depth;
    }
    return 0.0;
}

// A baked pane texel (gamma colour, alpha-premultiplied by the bake's coverage, 0 off the glass) to the colour it
// passes: below the floor the leading passes nothing; above it the colour is stretched back to full and saturated.
float3 StainedGlassPass(float4 g, float leadFloor, float sat)
{
    float3 col = saturate((g.rgb - leadFloor * g.a) / max(1.0 - leadFloor, 1e-3));
    float lum = dot(col, float3(0.2126, 0.7152, 0.0722));
    return max(lerp(lum.xxx, col, sat), 0.0);
}

// The light the windows send to a primary hit (lighting space, like DirectLighting). pos: the hit, n: shading normal,
// nv: the geometric normal facing the ray, footprint: the hit's texel on the surface (m; the ripples' band limit).
// Under a water plane the light has crossed the water: dimmed along its path in it, shifted by the ripples (the
// caustics' map) and blurred with depth; dry hits take the plain path below, unchanged. At most one shadow ray: where
// several beams overlap, one is picked by its unshadowed brightness and weighted (exact where only one reaches, which
// is nearly everywhere: the beams are window-sized).
float3 StainedGlassLight(float3 pos, float3 n, float3 nv, float footprint, inout uint rng)
{
    const float3 lw = float3(0.2126, 0.7152, 0.0722);
    float strength = _UltraRTStainedGlassParams.x;
    float leadFloor = saturate(_UltraRTStainedGlassParams.y);
    float sat = _UltraRTStainedGlassParams.z;
    float wSum = 0.0, pickW = 0.0, pickT = 0.0;
    float3 pick = 0, pickL = 0;
    float dbg = _UltraRTStainedGlassParams.w;
    if (dbg > 3.5 && dbg < 4.5) return float3(0, 20, 0); // debug 4: any window bound
    // Under water: the depth, and the ripples' shift of where the light came in (horizontal, world metres). The shift is
    // taken at the receiver as for light from straight above (as the ripple caustics take it), so the mosaic and the
    // caustic lines on the same floor move together.
    float waterY = 0.0, wetDepth = 0.0;
    float3 wetShift = 0;
    [branch] if (_UltraRTStainedGlassWater.w > 0.5 && _UltraRTRipple.y > 0.5)
    {
        wetDepth = StainedGlassWaterDepth(pos, waterY);
        [branch] if (wetDepth > 0.0 && _UltraRTStainedGlassWater2.y > 0.0)
        {
            float fade = saturate(1.0 - length(pos - _CamPos.xyz) / max(_UltraRTWater.z, 1.0)); // the ripples' view fade
            float2 sh = WaterRippleSourceShift(WaterPlaneUV(pos), wetDepth, _UltraRTWater.x, _UltraRTRipple.z, footprint, waterY)
                      * (_UltraRTStainedGlassWater2.y * fade);
            wetShift = float3(sh.x, 0.0, -sh.y); // WaterPlaneUV: u = +x, v = -z
        }
    }
    bool wet = wetDepth > 0.0;
    // debug 7: the water depth over the hit (< 0.5 m red, < 1 yellow, < 1.5 green, < 2 cyan, < 3 blue, deeper magenta)
    if (dbg > 6.5)
        return !wet ? 0 : wetDepth < 0.5 ? float3(20, 0, 0) : wetDepth < 1.0 ? float3(20, 20, 0) : wetDepth < 1.5 ? float3(0, 20, 0)
             : wetDepth < 2.0 ? float3(0, 20, 20) : wetDepth < 3.0 ? float3(0, 0, 20) : float3(20, 0, 20);
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
        // Under water the light that lands here came in where the ripples' shift says (the shadow ray keeps pos).
        float3 src = pos + wetShift;
        float ts = wet ? dot(a.xyz - src, e.xyz) / denom : t;
        float3 x = src + L * ts - a.xyz;
        float2 uv = float2(dot(x, bu.xyz) * bu.w + 0.5, dot(x, bv.xyz) * bv.w + 0.5);
        if (any(uv <= 0.0) || any(uv >= 1.0)) continue;
        if (_UltraRTStainedGlassParams.w > 2.5) return float3(20, 0, 20); // debug 3: magenta wherever a beam reaches
        float3 c;
        [branch] if (!wet)
        {
            // Point sampled at full resolution (no mips: Plugin.StainedGlass.cs, SgBake), like the game's pixel-art panes.
            float4 g = _UltraRTStainedGlassTex.SampleLevel(my_point_clamp_sampler_UltraRTStainedGlassTex, float3(uv, a.w), 0);
            if (_UltraRTStainedGlassParams.w > 1.5) g = float4(1, 1, 1, 1);
            // g: the pane's (gamma) colour, alpha-premultiplied by the bake's coverage (0 off the glass): StainedGlassPass.
            c = ToLight(StainedGlassPass(g, leadFloor, sat) * (strength * RelightExposure())) * ndl;
        }
        else
        {
            // The light's path in the water: refracted at the (mean) surface, n 1.33.
            float cosA = saturate(L.y);
            float cosW = sqrt(saturate(1.0 - (1.0 - cosA * cosA) / (1.33 * 1.33)));
            float path = min(wetDepth / max(cosW, 0.05), _UltraRTStainedGlassWater.z);
            // Blur with the path: ripples finer than the band limit and the light's own spread smear the leading.
            // A Vogel disc of 8 bilinear taps, turned at random per sample (the denoiser / DLSS RR average it).
            float r = _UltraRTStainedGlassWater2.x * path; // radius on the pane (m)
            float3 acc = 0;
            [branch] if (r < 0.5 * e.w)
            {
                float4 g = _UltraRTStainedGlassTex.SampleLevel(my_point_clamp_sampler_UltraRTStainedGlassTex, float3(uv, a.w), 0);
                if (_UltraRTStainedGlassParams.w > 1.5) g = float4(1, 1, 1, 1);
                acc = ToLight(StainedGlassPass(g, leadFloor, sat));
            }
            else
            {
                float2 rad = r * float2(bu.w, bv.w);
                float rot = Rand(rng) * 6.2831853;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float sn, cs;
                    sincos(rot + 2.3999632 * k, sn, cs);
                    float2 o = sqrt((k + 0.5) / 8.0) * float2(cs, sn) * rad;
                    float4 g = _UltraRTStainedGlassTex.SampleLevel(my_linear_clamp_sampler_UltraRTStainedGlassTex, float3(saturate(uv + o), a.w), 0);
                    if (_UltraRTStainedGlassParams.w > 1.5) g = float4(1, 1, 1, 1);
                    acc += ToLight(StainedGlassPass(g, leadFloor, sat));
                }
                acc *= 0.125;
            }
            // Beer-Lambert as the overlay's water absorption (docs/reflections.md, "Water absorption"), grey (the pane
            // gives the colour), and the share the surface reflects away.
            float tr = lerp(_UltraRTStainedGlassWater.y, 1.0, exp(-_UltraRTStainedGlassWater.x * path)) * (1.0 - SchlickFresnel(0.02, cosA));
            c = acc * ToLight((strength * RelightExposure()).xxx) * (ndl * tr); // ToLight is a power: it factors
        }
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
