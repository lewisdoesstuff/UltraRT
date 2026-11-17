// Exposure and tone mapping for the denoiser's Composite (relight; docs/relight.md). Included by Denoise.compute.
//
// Exposure scales the lit part only: albedo * lighting and the specular term, weighted by the surface's lit weight, so
// fullbright art and emission keep the game's brightness. The exposure itself comes from Exposure.compute (GPU only, one
// frame behind). Both resources are bound on the Composite kernel by the plugin every frame (AutoExposure.BindComposite).

#ifndef ULTRART_EXPOSURE_INCLUDED
#define ULTRART_EXPOSURE_INCLUDED

#include "UltraRTToneCurve.hlsl"

StructuredBuffer<float4> _UltraRTExposure;  // [0].x = exposure (linear multiplier), [3] = colour match gain (w 1 = valid)
Texture2D<uint4>         _UltraRTLitWeight; // PathTrace's _HybridLight: w & 0xFF = lit weight (0 = fullbright)
float  _UltraRTAutoExposure;                // global: 1 = apply _UltraRTExposure, 0 = off
float  _UltraRTTonemapWhite;                // global, relight: luminance (or max channel) that maps to 1; 0 = no tone map
float4 _UltraRTTonemapParams;               // global: x curve (0 = luminance, 1 = max channel), y knee, z toe strength, w toe end (UltraRTToneCurve.hlsl)
float  _UltraRTLinear;                      // global, relight: 1 = lighting is linear (linear light transport, docs/relight.md)

// Filtered lighting -> the game's gamma lighting units (albedo * lighting is then the displayed colour). After the
// denoiser, so the noisy samples are averaged in linear space.
float3 UltraRTDisplayLighting(float3 l)
{
    return _UltraRTLinear > 0.5 ? pow(max(l, 0.0), 1.0 / 2.2) : l;
}

// Exposure multiplier for the lit part of trace pixel s.
float UltraRTPixelExposure(uint2 s)
{
    if (_UltraRTAutoExposure <= 0.0) return 1.0;
    float e = _UltraRTExposure[0].x;
    if (!(e > 0.0) || isinf(e)) return 1.0;
    float lit = (_UltraRTLitWeight[s].w & 0xFFu) * (1.0 / 255.0);
    return lerp(1.0, e, lit);
}

// Colour match gain for the lit part of trace pixel s (Exposure.compute; docs/relight.md "Area colour"): 1 when auto
// exposure is off or the gain isn't valid yet, weighted by the lit weight like the exposure.
float3 UltraRTPixelBalance(uint2 s)
{
    if (_UltraRTAutoExposure <= 0.0) return 1.0;
    float4 g = _UltraRTExposure[3];
    if (g.w < 0.5 || any(isnan(g)) || any(isinf(g)) || any(g.rgb <= 0.0)) return 1.0;
    float lit = (_UltraRTLitWeight[s].w & 0xFFu) * (1.0 / 255.0);
    return lerp(1.0, g.rgb, lit);
}

// Relight's tone curve (UltraRTToneCurve.hlsl, shared with TransparentOverlay.shader).
float3 UltraRTToneMap(float3 c)
{
    return UltraRTToneCurve(c, _UltraRTTonemapWhite, _UltraRTTonemapParams);
}

#endif
