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

// The ripples: direction xy (in the surface's tangent plane), wavelength (m), slope amplitude. Shared by the reflection
// normal (WaterNormalBand) and the caustics under the water (WaterRippleCaustic): both move together.
static const float4 kWaterWaves[4] =
{
    float4( 0.80,  0.60, 2.30, 1.00),
    float4(-0.50,  0.87, 1.30, 0.70),
    float4( 0.20, -0.98, 0.80, 0.50),
    float4(-0.93, -0.37, 0.50, 0.35),
};

// footprint: the size on the surface (m) of what one sample stands for (the reflection ray at pathtrace's trace scale: a
// third of the screen, and ten times wider along the view at a grazing angle). A ripple of a few footprints tilts each
// sample its own way, and the mirror image of a wall 30 m away lands metres apart from one texel to the next: a
// mottled, boiling reflection (8-2's pool, user 2026-11-18). A ripple fades out between eight and four footprints
// per wavelength, so far water is a calm mirror and near water keeps its ripples. 0 = every ripple (the overlay).
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
        float k = 6.2831853 / kWaterWaves[i].z;
        float omega = sqrt(9.81 * k) * 0.5; // slowed down: full speed looks frantic at this scale
        float band = footprint > 0.0 ? saturate(kWaterWaves[i].z / max(4.0 * footprint, 1e-6) - 1.0) : 1.0;
        g += kWaterWaves[i].xy * (kWaterWaves[i].w * band * cos(k * dot(kWaterWaves[i].xy, uv) - omega * _UltraRTWater.y));
    }
    g *= 0.06 * strength;
    return normalize(n - g.x * t - g.y * b);
}

float3 WaterNormal(float3 n, float3 p, float viewDist) { return WaterNormalBand(n, p, viewDist, 0.0); }

// ---------------------------------------------------------------- ripple caustics (docs/caustics.md, section 6)
//
// Light refracted through the rippled surface of a horizontal water plane and received depth metres below. For light
// from straight above, a surface point x lands at X = x + q grad h(x), q = depth (1 - 1/1.33); the irradiance there is
// 1 / |det(I + q H)| (H the height field's Hessian), summed over the points that land at X. Evaluated with a single
// root (two fixed-point steps from x = X): exact while the light has not crossed its first focus, and the gain below
// keeps q x curvature short of the second. The fold singularity is softened with eps (a light of finite size blurs
// the lines with depth; a wide footprint blurs them too, against aliasing). The four waves are those of the
// reflection normal, so the lines move with the visible ripples.

// uv on a horizontal surface, as WaterNormalBand projects it (t = +x, b = -z).
float2 WaterPlaneUV(float3 p) { return float2(p.x, -p.z); }

// Gradient (slope) and Hessian (xx, xy, yy) of the ripple height field, per unit slope scale, at uv; waves shorter than
// about 6 footprints fade out.
void WaterRippleField(float2 uv, float footprint, out float2 g, out float3 hess)
{
    g = 0;
    hess = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float4 w = kWaterWaves[i];
        float k = 6.2831853 / w.z;
        float omega = sqrt(9.81 * k) * 0.5;
        float a = w.w * saturate(w.z / max(6.0 * footprint, 1e-6) - 1.0);
        float s, c;
        sincos(k * dot(w.xy, uv) - omega * _UltraRTWater.y, s, c);
        g += w.xy * (a * c);
        hess += float3(w.x * w.x, w.x * w.y, w.y * w.y) * (-a * k * s);
    }
}

// The caustic factor at a receiver depth metres under the surface point uv (mean about 1: caustics move light around,
// they do not add it). slope: the reflection's ripple strength (_UltraRTWater.x, a level's waterWaves included); gain:
// how much more the light focuses than those slopes alone would (they are tuned for the reflection and focus only at
// 7 m and deeper); footprint: the receiver's texel size (m).
float WaterRippleCaustic(float2 uv, float depth, float slope, float gain, float footprint)
{
    // q x slope scale, the depth-gain product soft-limited at 8 (beyond it the single root shows blobs, not lines).
    float z = depth * gain * slope;
    float zc = 8.0 * tanh(z * 0.125);
    float q = 0.248 * 0.06 * zc; // (1 - 1/1.33) x the ripples' slope scale (WaterNormalBand: 0.06)
    float2 g;
    float3 hess;
    float2 x = uv;
    [unroll] for (int it = 0; it < 2; it++)
    {
        WaterRippleField(x, footprint, g, hess);
        x = uv - q * g;
    }
    WaterRippleField(x, footprint, g, hess);
    float det = (1.0 + q * hess.x) * (1.0 + q * hess.z) - q * q * hess.y * hess.y;
    float d6 = min(depth, 6.0);
    float eps = 0.22 + 0.04 * d6 + 4.0 * footprint;
    // sqrt(1 + eps^2) / sqrt(det^2 + eps^2): exactly 1 over flat water whatever eps (a wide footprint or a steep wall
    // fades to 1, not darker). Averaged over the waves it is 1.0 shallow and 0.93 at 6 m (measured offline over
    // 300 m x 300 m, gains 2-6): renormalised.
    return sqrt(1.0 + eps * eps) * rsqrt(det * det + eps * eps) / (1.0 - 0.0115 * d6);
}

float SchlickFresnel(float f0, float NdotV)
{
    float x = 1.0 - saturate(NdotV);
    float x2 = x * x;
    return f0 + (1.0 - f0) * x2 * x2 * x;
}

#endif
