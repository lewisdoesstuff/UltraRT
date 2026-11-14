#ifndef ULTRART_WATER_INCLUDED
#define ULTRART_WATER_INCLUDED

// Water and glass (docs/reflections.md, "Water and glass"). Shared by PathTrace.raytrace (TransparentGen: the
// reflection ray) and TransparentOverlay.shader (Fresnel, refraction): both must see the same ripples.
//
// ULTRAKILL's water has no normal map, so ripples are procedural: a height field of four directional sines in the
// surface's tangent plane (world metres), animated with deep-water dispersion. Strength is a slope scale: 1 gives slopes
// up to about 0.15. Ripples fade out with view distance, where they would only alias at the trace resolution.

#define ULTRART_TRANSPARENT_GLASS 0.0
#define ULTRART_TRANSPARENT_WATER 1.0

// x ripple strength (0 = flat), y time (s, wrapped), z fade-out distance (m), w refraction strength (overlay only)
float4 _UltraRTWater;

float3 WaterNormal(float3 n, float3 p, float viewDist)
{
    float strength = _UltraRTWater.x * saturate(1.0 - viewDist / max(_UltraRTWater.z, 1.0));
    if (strength <= 0.0) return n;
    float3 t = abs(n.y) < 0.999 ? normalize(cross(n, float3(0, 1, 0))) : float3(1, 0, 0);
    float3 b = cross(n, t);
    float2 uv = float2(dot(p, t), dot(p, b));
    // direction xy, wavelength (m), slope amplitude
    static const float4 waves[4] =
    {
        float4( 0.80,  0.60, 2.30, 1.00),
        float4(-0.50,  0.87, 1.30, 0.70),
        float4( 0.20, -0.98, 0.80, 0.50),
        float4(-0.93, -0.37, 0.50, 0.35),
    };
    float2 g = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float k = 6.2831853 / waves[i].z;
        float omega = sqrt(9.81 * k) * 0.5; // slowed down: full speed looks frantic at this scale
        g += waves[i].xy * (waves[i].w * cos(k * dot(waves[i].xy, uv) - omega * _UltraRTWater.y));
    }
    g *= 0.06 * strength;
    return normalize(n - g.x * t - g.y * b);
}

float SchlickFresnel(float f0, float NdotV)
{
    float x = 1.0 - saturate(NdotV);
    float x2 = x * x;
    return f0 + (1.0 - f0) * x2 * x2 * x;
}

#endif
