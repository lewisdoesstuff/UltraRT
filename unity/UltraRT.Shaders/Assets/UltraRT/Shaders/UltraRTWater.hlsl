#ifndef ULTRART_WATER_INCLUDED
#define ULTRART_WATER_INCLUDED

// Water and glass (docs/reflections.md, "Water and glass"). Shared by PathTrace.raytrace (TransparentGen: the
// reflection ray) and TransparentOverlay.shader (Fresnel, refraction): both must see the same ripples.
//
// ULTRAKILL's water has no normal map, so ripples are procedural: a height field of four directional sines in the
// surface's tangent plane (world metres), animated with deep-water dispersion. Strength is a slope scale: 1 gives slopes
// up to about 0.15. Ripples fade out with view distance, where they would only alias at the trace resolution.

// x ripple strength (0 = flat), y time (s, wrapped), z fade-out distance (m), w refraction strength (overlay only)
float4 _UltraRTWater;

// 1 = the reflection ray's ripples are band-limited to its texel's footprint (global; bench plugin._waterBand=0).
float _UltraRTWaterBand;

// footprint: the size on the surface (m) of what one sample stands for (the reflection ray at pathtrace's trace scale: a
// third of the screen, and ten times wider along the view at a grazing angle). A ripple of a few footprints tilts each
// sample its own way, and the mirror image of a wall 30 m away lands metres apart from one texel to the next: a
// mottled, boiling reflection (8-2's pool, user 2026-11-18). A ripple fades out between eight and four footprints
// per wavelength, so far water is a calm mirror and near water keeps its ripples. 0 = every ripple (the overlay).
// direction xy, wavelength (m), slope amplitude (x 0.06 x strength)
static const float4 kWaterWaves[4] =
{
    float4( 0.80,  0.60, 2.30, 1.00),
    float4(-0.50,  0.87, 1.30, 0.70),
    float4( 0.20, -0.98, 0.80, 0.50),
    float4(-0.93, -0.37, 0.50, 0.35),
};

// How much of wave i the normal keeps: 0 below four footprints per wavelength, 1 from eight (0 footprint = every wave).
float WaterWaveBand(float wavelength, float footprint)
{
    return footprint > 0.0 ? saturate(wavelength / max(4.0 * footprint, 1e-6) - 1.0) : 1.0;
}

float3 WaterNormalBand(float3 n, float3 p, float viewDist, float footprint)
{
    float strength = _UltraRTWater.x * saturate(1.0 - viewDist / max(_UltraRTWater.z, 1.0));
    if (strength <= 0.0) return n;
    float3 t = abs(n.y) < 0.999 ? normalize(cross(n, float3(0, 1, 0))) : float3(1, 0, 0);
    float3 b = cross(n, t);
    float2 uv = float2(dot(p, t), dot(p, b));
    float2 g = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float4 wv = kWaterWaves[i];
        float k = 6.2831853 / wv.z;
        float omega = sqrt(9.81 * k) * 0.5; // slowed down: full speed looks frantic at this scale
        g += wv.xy * (wv.w * WaterWaveBand(wv.z, footprint) * cos(k * dot(wv.xy, uv) - omega * _UltraRTWater.y));
    }
    g *= 0.06 * strength;
    return normalize(n - g.x * t - g.y * b);
}

float3 WaterNormal(float3 n, float3 p, float viewDist) { return WaterNormalBand(n, p, viewDist, 0.0); }

// The slope variance (squared GGX alpha) of the ripples WaterNormalBand leaves out at this distance and footprint: the
// waves faded by distance or too short for the sample. A glint (PathTrace.raytrace, GlintLight) widens its lobe by it
// (alpha^2 += this), so far water that is a calm mirror for the reflection ray still spreads a light into a long
// glittering path, as sub-pixel waves do (the LEAN / Toksvig idea, as the detail maps' roughness does). A sine of slope
// amplitude A has a slope variance of A^2 / 2, half of it per axis; GGX alpha^2 is about twice the per-axis variance.
float WaterUnresolvedVariance(float viewDist, float footprint)
{
    float full = max(_UltraRTWater.x, 0.0);
    float kept = full * saturate(1.0 - viewDist / max(_UltraRTWater.z, 1.0));
    float v = 0.0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float a = kWaterWaves[i].w * 0.06;
        float b = kept * WaterWaveBand(kWaterWaves[i].z, footprint);
        v += a * a * max(full * full - b * b, 0.0) * 0.5;
    }
    return v;
}

float SchlickFresnel(float f0, float NdotV)
{
    float x = 1.0 - saturate(NdotV);
    float x2 = x * x;
    return f0 + (1.0 - f0) * x2 * x2 * x;
}

#endif
