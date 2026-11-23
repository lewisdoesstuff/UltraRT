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

// The ripples: direction xy (in the surface's tangent plane), wavelength (m), slope amplitude. Shared by the reflection
// normal (WaterNormalBand) and the caustics under the water (WaterRippleCaustic): both move together.
static const float4 kWaterWaves[4] =
{
    float4( 0.80,  0.60, 2.30, 1.00),
    float4(-0.50,  0.87, 1.30, 0.70),
    float4( 0.20, -0.98, 0.80, 0.50),
    float4(-0.93, -0.37, 0.50, 0.35),
};

// ---------------------------------------------------------------- interactive ripples (docs/reflections.md, "Interactive ripples")
//
// Expanding rings from splashes, wakes and explosions at the water (Plugin.WaterRipples.cs), on top of the four waves:
// in the reflection normal (WaterNormalBand) and in the ripple caustics (WaterRippleCaustic), as an analytic height
// field only. Vertex displacement, the traced mesh and the overlay's depth are untouched, so a ring is the same in the
// trace and in the overlay (and so in the reflection's temporal filter).

#define ULTRART_WATER_IMPULSES 16
float4 _UltraRTWaterImpulse;                          // global: x active rings (0 = none: nothing below runs)
float4 _UltraRTWaterImpulses[ULTRART_WATER_IMPULSES];  // global: centre x, centre z, age (s), peak slope (x strength)
float4 _UltraRTWaterImpulses2[ULTRART_WATER_IMPULSES]; // global: surface y, ring speed (m/s), wavelength (m), decay time (s)

// One ring at d = surface point - centre, in any orthonormal frame of the horizontal plane (the ring is radial):
// h(r) = (a / k) E(r) sin(k (r - 1.5 R)), R = speed x age. The packet E travels at the ring speed, its crests at 1.5 x
// that (deep water: 2 x), so crests rise at the back of the packet and fade at its front.
// E = exp(-age / decay) x exp(-((r - R) / sigma)^2) x (1 + r / 2)^-1/2 (circular spreading) x r^2 / (r^2 + eps^2), with
// sigma = 0.6 wavelengths + 0.1 R (the packet widens as it travels) and eps = 0.35 wavelengths. The last factor is zero
// at the centre with h ~ r^2 there: the slope is 0 and the curvature finite, no spike and no NaN. a is the peak slope.
// Adds q x the slope to g and, if hessian, q x the Hessian (xx, xy, yy) to hess; q > 0 (caustics, see
// WaterRippleCaustic) is soft-limited per ring to 0.9 / (a k), short of the focus of its steepest curvature; q = 0: 1.
// bandFootprint: N footprints (m): a ring fades out as its wavelength nears N footprints, as the waves do.
void WaterImpulseRing(float2 d, float4 i1, float4 i2, float bandFootprint, float q, bool hessian, inout float2 g, inout float3 hess)
{
    float lambda = i2.z;
    float band = saturate(lambda / max(bandFootprint, 1e-6) - 1.0);
    float R = i2.y * i1.z;
    float sigma = 0.6 * lambda + 0.1 * R;
    float r = length(d);
    float u = r - R;
    [branch] if (band <= 0.0 || abs(u) > 3.0 * sigma) return;
    float k = 6.2831853 / lambda;
    float a = i1.w * band * exp(-i1.z / max(i2.w, 1e-3)) * saturate(i1.z * 12.5); // in over the first 80 ms
    if (q > 0.0)
    {
        float qmax = 0.9 / max(i1.w * k, 1e-4);
        a *= qmax * tanh(q / qmax);
    }
    float inv = 1.0 / (sigma * sigma);
    float b1 = 1.0 + 0.5 * r;
    float F = exp(-u * u * inv) * rsqrt(b1); // packet x spreading
    float lF = -2.0 * u * inv - 0.25 / b1;   // F' / F
    float dF = F * lF;
    float e2 = 0.1225 * lambda * lambda;
    float s = r * r + e2;
    float C = r * r / s;
    float dC = 2.0 * e2 * r / (s * s);
    float P = a / k * F * C;                  // height amplitude (m)
    float dP = a / k * (dF * C + F * dC);
    float sn, cs;
    sincos(k * (r - 1.5 * R), sn, cs);
    float h1 = dP * sn + P * k * cs;          // dh/dr
    float rs = max(r, 1e-4);
    float2 nr = d / rs;
    g += nr * h1;
    if (hessian)
    {
        float ddF = F * (lF * lF - 2.0 * inv + 0.125 / (b1 * b1));
        float ddC = 2.0 * e2 * (e2 - 3.0 * r * r) / (s * s * s);
        float ddP = a / k * (ddF * C + 2.0 * dF * dC + F * ddC);
        float h2 = ddP * sn + 2.0 * dP * k * cs - P * k * k * sn; // d2h/dr2
        float ht = h1 / rs;                                       // h' / r: finite at the centre (h ~ r^2)
        hess += float3(h2 * nr.x * nr.x + ht * (1.0 - nr.x * nr.x), (h2 - ht) * nr.x * nr.y, h2 * nr.y * nr.y + ht * (1.0 - nr.y * nr.y));
    }
}

// Every active ring of the surface at height y (rings more than 2.5 m above or below belong to another pool) at uv,
// the surface point in the frame (t, b) of the horizontal plane; ring centres are projected the same way.
void WaterImpulseField(float2 uv, float3 t, float3 b, float y, float bandFootprint, float q, bool hessian, inout float2 g, inout float3 hess)
{
    uint count = min((uint)_UltraRTWaterImpulse.x, (uint)ULTRART_WATER_IMPULSES);
    [loop] for (uint i = 0u; i < count; i++)
    {
        float4 i2 = _UltraRTWaterImpulses2[i];
        [branch] if (abs(i2.x - y) < 2.5)
        {
            float4 i1 = _UltraRTWaterImpulses[i];
            float3 c = float3(i1.x, i2.x, i1.y);
            WaterImpulseRing(uv - float2(dot(c, t), dot(c, b)), i1, i2, bandFootprint, q, hessian, g, hess);
        }
    }
}

// footprint: the size on the surface (m) of what one sample stands for (the reflection ray at pathtrace's trace scale: a
// third of the screen, and ten times wider along the view at a grazing angle). A ripple of a few footprints tilts each
// sample its own way, and the mirror image of a wall 30 m away lands metres apart from one texel to the next: a
// mottled, boiling reflection (8-2's pool, user 2026-11-18). A ripple fades out between eight and four footprints
// per wavelength, so far water is a calm mirror and near water keeps its ripples. 0 = every ripple (the overlay).
// The interactive rings are added on horizontal surfaces (|n.y| > 0.9), with the same view fade and band limit; they
// don't follow the level's ripple strength (8-2's still pool still rings when something lands in it).
float3 WaterNormalBand(float3 n, float3 p, float viewDist, float footprint)
{
    float fade = saturate(1.0 - viewDist / max(_UltraRTWater.z, 1.0));
    float strength = _UltraRTWater.x * fade;
    float horiz = saturate((abs(n.y) - 0.9) * 10.0) * fade;
    bool rings = _UltraRTWaterImpulse.x > 0.5 && horiz > 0.0;
    if (strength <= 0.0 && !rings) return n;
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
    g *= 0.06 * max(strength, 0.0);
    [branch] if (rings)
    {
        float2 gi = 0;
        float3 unused = 0;
        WaterImpulseField(uv, t, b, p.y, 4.0 * footprint, 0.0, false, gi, unused);
        g += gi * horiz;
    }
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
// 7 m and deeper); footprint: the receiver's texel size (m); waterY: the surface's height (which interactive rings).
float WaterRippleCaustic(float2 uv, float depth, float slope, float gain, float footprint, float waterY)
{
    // q x slope scale, the depth-gain product soft-limited at 8 (beyond it the single root shows blobs, not lines).
    float z = depth * gain * slope;
    float zc = 8.0 * tanh(z * 0.125);
    float q = 0.248 * 0.06 * zc; // (1 - 1/1.33) x the ripples' slope scale (WaterNormalBand: 0.06)
    float2 g;
    float3 hess;
    float2 x = uv;
    float det;
    [branch] if (_UltraRTWaterImpulse.x < 0.5)
    {
        [unroll] for (int it = 0; it < 2; it++)
        {
            WaterRippleField(x, footprint, g, hess);
            x = uv - q * g;
        }
        WaterRippleField(x, footprint, g, hess);
        det = (1.0 + q * hess.x) * (1.0 + q * hess.z) - q * q * hess.y * hess.y;
    }
    else
    {
        // With interactive rings: their slopes are real slopes (not per unit of the slope scale), so each ring's q is
        // the physical depth x (1 - 1/1.33) x the same gain, soft-limited per ring (WaterImpulseRing). The frame is
        // WaterPlaneUV's (t = +x, b = -z).
        float qi = 0.248 * depth * gain;
        float3 t = float3(1, 0, 0), b = float3(0, 0, -1);
        float3 none = 0;
        [unroll] for (int it = 0; it < 2; it++)
        {
            WaterRippleField(x, footprint, g, hess);
            float2 gs = 0;
            WaterImpulseField(x, t, b, waterY, 6.0 * footprint, qi, false, gs, none);
            x = uv - q * g - gs;
        }
        WaterRippleField(x, footprint, g, hess);
        float2 gi = 0;
        float3 hi = 0;
        WaterImpulseField(x, t, b, waterY, 6.0 * footprint, qi, true, gi, hi);
        float3 hq = q * hess + hi;
        det = (1.0 + hq.x) * (1.0 + hq.z) - hq.y * hq.y;
    }
    float d6 = min(depth, 6.0);
    float eps = 0.22 + 0.04 * d6 + 4.0 * footprint;
    // sqrt(1 + eps^2) / sqrt(det^2 + eps^2): exactly 1 over flat water whatever eps (a wide footprint or a steep wall
    // fades to 1, not darker). Averaged over the waves it is 1.0 shallow and 0.93 at 6 m (measured offline over
    // 300 m x 300 m, gains 2-6): renormalised.
    return sqrt(1.0 + eps * eps) * rsqrt(det * det + eps * eps) / (1.0 - 0.0115 * d6);
}

// WaterRippleCaustic's map run backwards: the shift (in WaterPlaneUV's frame) from a receiver depth metres under the
// surface point uv to the surface point whose refracted light lands there, x - uv. The same q, the same two fixed-point
// steps and the same interactive rings, so a pattern looked up through it (the stained-glass projection under water,
// UltraRTStainedGlass.hlsl) wobbles with the caustic lines the albedo factor draws on the same floor.
float2 WaterRippleSourceShift(float2 uv, float depth, float slope, float gain, float footprint, float waterY)
{
    float q = 0.248 * 0.06 * 8.0 * tanh(depth * gain * slope * 0.125);
    float qi = 0.248 * depth * gain;
    bool rings = _UltraRTWaterImpulse.x > 0.5;
    float3 t = float3(1, 0, 0), b = float3(0, 0, -1), none = 0;
    float2 x = uv, g;
    float3 hess;
    [unroll] for (int it = 0; it < 2; it++)
    {
        WaterRippleField(x, footprint, g, hess);
        float2 gs = 0;
        [branch] if (rings) WaterImpulseField(x, t, b, waterY, 6.0 * footprint, qi, false, gs, none);
        x = uv - q * g - gs;
    }
    return x - uv;
}

float SchlickFresnel(float f0, float NdotV)
{
    float x = 1.0 - saturate(NdotV);
    float x2 = x * x;
    return f0 + (1.0 - f0) * x2 * x2 * x;
}

#endif
