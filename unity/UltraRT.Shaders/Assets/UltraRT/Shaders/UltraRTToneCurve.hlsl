// Relight's tone curve (docs/relight.md), shared by the denoiser's Composite (UltraRTExposure.hlsl) and the water/glass
// reflection overlay (TransparentOverlay.shader) so both always use the same curve. Plain functions, no resources.
//
//   white   luminance (or max channel) that maps to 1; <= 0 = no tone map (values are only saturated by the caller)
//   params  x curve (0 = luminance shoulder, 1 = max-channel shoulder), y knee (0 = 0.75),
//           z toe strength (0 = no toe), w toe end (where the toe joins the identity; 0 = 0.25)

#ifndef ULTRART_TONECURVE_INCLUDED
#define ULTRART_TONECURVE_INCLUDED

// Shoulder: values up to the knee (normal surface colours, fullbright/emissive art) are untouched; above it they roll off
// smoothly so that the white point maps to 1.
//   Curve 0 (luminance): scales by luminance, so a saturated colour can still clip in one channel.
//   Curve 1 (max channel): scales by the largest channel. Hue and saturation are kept and nothing clips below the white
//   point; above it the brightest channel saturates first, which desaturates towards white like film.
// Toe: below the toe end, luminance v becomes lerp(v, v^2/t * (2 - v/t), strength): same value and slope at t, slope
// 1 - strength at black. Deepens shadows a little (contrast) without crushing them; hue is kept.
float3 UltraRTToneCurve(float3 c, float white, float4 params)
{
    if (white <= 0.0) return c;
    float knee = params.y > 0.0 ? params.y : 0.75;
    float v = params.x > 0.5 ? max(c.r, max(c.g, c.b)) : dot(c, float3(0.2126, 0.7152, 0.0722));
    if (v > knee)
    {
        float x = v - knee, room = 1.0 - knee, range = max(white - knee, 1e-3);
        float mapped = knee + room * x * (1.0 + x / (range * range) * room) / (x + room);
        c *= mapped / v;
    }
    else if (params.z > 0.0)
    {
        float t = params.w > 0.0 ? params.w : 0.25;
        float l = dot(c, float3(0.2126, 0.7152, 0.0722));
        if (l > 1e-6 && l < t)
        {
            float toe = l * l / t * (2.0 - l / t);
            c *= lerp(l, toe, saturate(params.z)) / l;
        }
    }
    return c;
}

#endif
