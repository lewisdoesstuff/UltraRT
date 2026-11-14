// Exposure and tone mapping for the denoiser's Composite (relight; docs/relight.md). Included by Denoise.compute.
//
// Exposure scales the lit part only: albedo * lighting and the specular term, weighted by the surface's lit weight, so
// fullbright art and emission keep the game's brightness. The exposure itself comes from Exposure.compute (GPU only, one
// frame behind). Both resources are bound on the Composite kernel by the plugin every frame (AutoExposure.BindComposite).

#ifndef ULTRART_EXPOSURE_INCLUDED
#define ULTRART_EXPOSURE_INCLUDED

StructuredBuffer<float4> _UltraRTExposure;  // [0].x = exposure (linear multiplier)
Texture2D<uint4>         _UltraRTLitWeight; // PathTrace's _HybridLight: w & 0xFF = lit weight (0 = fullbright)
float  _UltraRTAutoExposure;                // global: 1 = apply _UltraRTExposure, 0 = off
float  _UltraRTTonemapWhite;                // global, relight: luminance (or max channel) that maps to 1; 0 = no tone map
float4 _UltraRTTonemapParams;               // global: x curve (0 = luminance shoulder, 1 = max-channel shoulder), y knee (0 = 0.75)

// Exposure multiplier for the lit part of trace pixel s.
float UltraRTPixelExposure(uint2 s)
{
    if (_UltraRTAutoExposure <= 0.0) return 1.0;
    float e = _UltraRTExposure[0].x;
    if (!(e > 0.0) || isinf(e)) return 1.0;
    float lit = (_UltraRTLitWeight[s].w & 0xFFu) * (1.0 / 255.0);
    return lerp(1.0, e, lit);
}

// Shoulder-only tone map: values up to the knee (normal surface colours, fullbright/emissive art) are untouched;
// above it they roll off smoothly so that _UltraRTTonemapWhite maps to 1.
//   Curve 0 (luminance): the old curve. Scales by luminance, so a saturated colour can still clip in one channel.
//   Curve 1 (max channel): scales by the largest channel. Hue and saturation are kept and nothing clips below the white
//   point; above it the brightest channel saturates first, which desaturates towards white like film.
float3 UltraRTToneMap(float3 c)
{
    if (_UltraRTTonemapWhite <= 0.0) return c;
    float knee = _UltraRTTonemapParams.y > 0.0 ? _UltraRTTonemapParams.y : 0.75;
    float v = _UltraRTTonemapParams.x > 0.5 ? max(c.r, max(c.g, c.b)) : dot(c, float3(0.2126, 0.7152, 0.0722));
    if (v > knee)
    {
        float x = v - knee, room = 1.0 - knee, range = max(_UltraRTTonemapWhite - knee, 1e-3);
        float mapped = knee + room * x * (1.0 + x / (range * range) * room) / (x + room);
        c *= mapped / v;
    }
    return c;
}

#endif
