// Relight's tone curve (docs/relight.md), shared by the denoiser's Composite (UltraRTExposure.hlsl) and the water/glass
// reflection overlay (TransparentOverlay.shader) so both always use the same curve. Plain functions, no resources (one
// global float4, _UltraRTTonemapHue, below).
//
//   white   luminance (or max channel) that maps to 1; <= 0 = no tone map (values are only saturated by the caller)
//   params  x curve (0 = luminance shoulder, 1 = max-channel shoulder, 2 + k = film: the luminance shoulder plus the
//           highlight rolloff below, k = the share of the room above luminance a channel may use untouched),
//           y knee (0 = 0.75), z toe strength (0 = no toe), w toe end (where the toe joins the identity; 0 = 0.25)

#ifndef ULTRART_TONECURVE_INCLUDED
#define ULTRART_TONECURVE_INCLUDED

// Shoulder: values up to the knee (normal surface colours, fullbright/emissive art) are untouched; above it they roll off
// smoothly so that the white point maps to 1.
//   Curve 0 (luminance): scales by luminance, so a saturated colour can still clip in one channel.
//   Curve 1 (max channel): scales by the largest channel. Hue and saturation are kept and nothing clips below the white
//   point; above it the brightest channel saturates first, which desaturates towards white like film.
// Toe: below the toe end, luminance v becomes lerp(v, v^2/t * (2 - v/t), strength): same value and slope at t, slope
// 1 - strength at black. Deepens shadows a little (contrast) without crushing them; hue is kept.
//   Curve 2 (film, the default since 2026-11-18): curve 0, then the highlight rolloff. Curve 0 looks at luminance only,
//   and a saturated colour has little: pure red at 1 has luminance 0.21, so a red-lit wall went to 1.0 and far beyond
//   in the red channel while the shoulder never saw it, and the caller's saturate cut it flat (the user's "solid
//   orange/red with no gradation", 0-1 and 0-3). Film does not clip a channel, it loses saturation: the rolloff does
//   that. A channel may stand above the pixel's luminance L by up to k x (top - L) untouched; beyond that the excess
//   is rolled off towards top - L and all three channels are pulled towards L by the same factor. Luminance and hue
//   are kept, no channel passes top, and the hotter the pixel the whiter it gets, so a gradient in the light stays a
//   gradient in the image. Neutral colours and anything below k of its room are bit-identical to curve 0.
//   Highlight hue (opt-in, docs/relight.md "Highlight hue"): see UltraRTFilmWarm.

// Highlight hue strength, x 0..1 (0 = off: the rolloff is bit-identical to the curve without it). A global float4, set by
// Plugin.Exposure.cs next to _UltraRTTonemapParams; the only one this file reads.
float4 _UltraRTTonemapHue;

// Highlight hue: the rolloff pulls a red highlight straight towards its luminance, and red plus white reads as pink or
// salmon. Hot red light on film and on the eye goes red -> orange -> white instead. Here a red-leading colour (hue -40 to
// 40 degrees, full from -10 to 10) that the rolloff desaturated by 1 - s turns its hue up to `strength` x 20 degrees
// towards orange (full once s <= 0.5). The leading red channel and the luminance are kept exactly: green rises and blue
// falls, so the colour stays as saturated as the rolloff left it, or a little more (never washed out towards white), and
// a gradient stays monotonic in every channel. Nothing changes where the rolloff did not act (s = 1).
float3 UltraRTFilmWarm(float3 c, float l, float s, float strength)
{
    if (c.r < max(c.g, c.b)) return c;
    float q = (c.g - c.b) / max(c.r - min(c.g, c.b), 1e-6); // signed hue / 60 degrees in the red sector
    float w = saturate((60.0 * q + 40.0) / 30.0) * saturate((40.0 - 60.0 * q) / 30.0);
    float dq = strength * (20.0 / 60.0) * w * saturate((1.0 - s) * 2.0);
    if (dq <= 0.0) return c;
    float qt = q + dq, lp = l - 0.2126 * c.r, g, b;
    if (qt >= 0.0)
    {
        b = max((lp - 0.7152 * qt * c.r) / (0.7152 * (1.0 - qt) + 0.0722), 0.0);
        g = b > 0.0 ? b + qt * (c.r - b) : lp / 0.7152;
    }
    else
    {
        float a = -qt;
        g = max((lp - 0.0722 * a * c.r) / (0.7152 + 0.0722 * (1.0 - a)), 0.0);
        b = g > 0.0 ? g + a * (c.r - g) : lp / 0.0722;
    }
    return float3(c.r, clamp(g, 0.0, c.r), clamp(b, 0.0, c.r));
}

float3 UltraRTFilm(float3 c, float top, float white, float4 params, bool toe)
{
    float knee = params.y > 0.0 ? params.y : 0.75;
    float k = clamp(params.x - 2.0, 0.05, 0.98);
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    if (l > knee)
    {
        float x = l - knee, room = top - knee, range = max(white - knee, 1e-3);
        float mapped = min(knee + room * x * (1.0 + x / (range * range) * room) / (x + room), top);
        c *= mapped / l;
        l = mapped;
    }
    else if (toe && params.z > 0.0)
    {
        float t = params.w > 0.0 ? params.w : 0.25;
        if (l > 1e-6 && l < t)
        {
            float tl = lerp(l, l * l / t * (2.0 - l / t), saturate(params.z));
            c *= tl / l;
            l = tl;
        }
    }
    float e = max(c.r, max(c.g, c.b)) - l, h = max(top - l, 0.0);
    if (e > k * h)
    {
        float rest = (1.0 - k) * h, y = e - k * h;
        float e2 = k * h + (rest > 1e-6 ? rest * y / (y + rest) : 0.0);
        c = l + (c - l) * (e2 / e);
        if (_UltraRTTonemapHue.x > 0.0) c = UltraRTFilmWarm(c, l, e2 / e, _UltraRTTonemapHue.x);
    }
    return c;
}

// Whether the film curve changes this colour above its toe (shoulder or rolloff): the HDR gain uses the SDR colour where
// it does not.
bool UltraRTFilmActs(float3 c, float4 params)
{
    float knee = params.y > 0.0 ? params.y : 0.75;
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    return l > knee || max(c.r, max(c.g, c.b)) - l > clamp(params.x - 2.0, 0.05, 0.98) * (1.0 - l);
}

float3 UltraRTToneCurve(float3 c, float white, float4 params)
{
    if (white <= 0.0) return c;
    if (params.x > 1.5) return UltraRTFilm(c, 1.0, white, params, true);
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
// Returns rgb = the per-channel gain in linear light (>= 1; per channel, so a highlight that went to white in SDR gets
// its colour back: the channel that leads is raised, the others stay at the little the SDR rolloff gave them, which
// in linear light is next to nothing beside it), a = the SDR colour packed (UltraRTHdrPack), by which the present pass recognises that the pixel is
// still the composite's.
float4 UltraRTHdrGain(float3 hdr, float3 shown, float scale, float white, float4 params, float4 hp)
{
    float3 h = max(hdr, 0.0) * max(scale, 1.0);
    float top = max(hp.y, 1.0);
    float3 hc;
    if (white <= 0.0) hc = h; // no tone curve in this mode: SDR clips at 1, HDR at the peak
    else if (params.x > 1.5)
    {
        // Film: the same shoulder and rolloff with the display peak as the top, so a saturated highlight keeps its
        // colour for as long as the display has room and whitens only above that.
        if (UltraRTFilmActs(h, params)) hc = UltraRTFilm(h, top, max(hp.z > 0.0 ? hp.z : white, top), params, true);
        else hc = shown * max(scale, 1.0);
    }
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
