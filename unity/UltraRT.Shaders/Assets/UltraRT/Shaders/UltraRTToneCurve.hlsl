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

// The colour a pixel is expected to have in the finished frame, packed into one half-float channel (an integer up to
// 2047, exact): 6 bits of luma and 5 bits of r - b. The present pass (Hdr.cpp) compares the frame with it, to leave
// alone what was drawn over the pixel afterwards (HUD, the gun, smoke).
float UltraRTHdrPack(float3 c)
{
    c = saturate(c);
    return round(dot(c, float3(0.2126, 0.7152, 0.0722)) * 63.0) * 32.0 + round(((c.r - c.b) * 0.5 + 0.5) * 31.0);
}

// HDR display gain (docs/hdr.md): what an HDR display should show for this pixel, over what the SDR image shows.
//   hdr    the composite before the tone curve (display gamma, 1 = SDR white)
//   shown  the SDR colour written to the image (the tone curve, saturated)
//   scale  >= 1: HDR-only brightening of this pixel before the curve (glowing surfaces, see UltraRTHdrEmissive)
//   hp     x on, y top = the display peak in the same units ((peak / paper white)^(1/2.2)), z the value that reaches the
//          peak (0 = the SDR white point), w largest gain
// The HDR curve is the same shoulder with more room: identical to the SDR curve up to the knee (gain exactly 1, so the
// look of everything that isn't a highlight is unchanged), then it rolls off to the display peak instead of to SDR white.
// Returns rgb = the per-channel gain in linear light (>= 1; per channel, so a highlight that clipped to white in SDR gets
// its colour back), a = the SDR colour packed (UltraRTHdrPack), by which the present pass recognises that the pixel is
// still the composite's.
float4 UltraRTHdrGain(float3 hdr, float3 shown, float scale, float white, float4 params, float4 hp)
{
    float3 h = max(hdr, 0.0) * max(scale, 1.0);
    float top = max(hp.y, 1.0);
    float3 hc;
    if (white <= 0.0) hc = h; // no tone curve in this mode: SDR clips at 1, HDR at the peak
    else
    {
        float knee = params.y > 0.0 ? params.y : 0.75;
        float v = params.x > 0.5 ? max(h.r, max(h.g, h.b)) : dot(h, float3(0.2126, 0.7152, 0.0722));
        if (v > knee)
        {
            float x = v - knee, room = top - knee, range = max(max(hp.z > 0.0 ? hp.z : white, top) - knee, 1e-3);
            float mapped = knee + room * x * (1.0 + x / (range * range) * room) / (x + room);
            hc = h * (min(mapped, top) / v);
        }
        else hc = shown * max(scale, 1.0); // below the knee (toe included): the SDR colour
    }
    hc = min(hc, top);
    float3 sl = pow(saturate(shown), 2.2), hl = pow(hc, 2.2);
    float3 g = float3(sl.r > 1e-4 ? hl.r / sl.r : 1.0, sl.g > 1e-4 ? hl.g / sl.g : 1.0, sl.b > 1e-4 ? hl.b / sl.b : 1.0);
    if (any(isnan(g)) || any(isinf(g))) g = 1.0;
    return float4(clamp(g, 1.0, max(hp.w, 1.0)), UltraRTHdrPack(shown));
}

// Glowing surfaces in HDR. The game's emissive art is authored for SDR: a light fixture, a screen or lava is "white" at 1,
// the same as a sheet of paper, so the curve above has nothing to expand. This is the HDR-only scale for such a pixel:
//   em   the emission of the trace sample behind the pixel (display gamma)
//   hdr  the pixel's composite before the tone curve
//   ep   x boost - 1 (display gamma: 0.5 = x1.5, about x2.4 in light), y / z where the brightest channel starts / fully counts
// Both the sample's emission and the pixel itself must be bright, so dark texels of a glowing surface stay as they are.
float UltraRTHdrEmissive(float3 em, float3 hdr, float4 ep)
{
    float e = max(em.r, max(em.g, em.b)), v = max(hdr.r, max(hdr.g, hdr.b));
    return 1.0 + max(ep.x, 0.0) * smoothstep(ep.y, ep.z, e) * smoothstep(ep.y, ep.z, v);
}

#endif
