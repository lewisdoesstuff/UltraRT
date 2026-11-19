#ifndef ULTRART_DITHER_INCLUDED
#define ULTRART_DITHER_INCLUDED
// Output dither (docs/ultrakill-rendering.md, "Final post-process: dither"; Plugin.GameDither.cs). The game's frame is
// 8-bit all the way (PostProcessV2_Handler.mainTex is ARGB32), and its own screen dither, which also hid 1-step contours
// in dark gradients, is off while frames are traced. So the passes that turn float colour into those 8 bits add
// +-0.5 of a step of interleaved gradient noise (Jimenez 2014: blue-noise-like, no texture), a new phase every frame.
// Below half a step, it never lifts black or dims white.
// _UltraRTOutDither: x amplitude in 8-bit steps (0 = off), y frame phase (0-63). Each pass gives its own stage so
// stacked passes do not add the same pattern twice.
float4 _UltraRTOutDither;

float3 UltraRTOutputDither(float3 c, float2 px, float stage)
{
    if (_UltraRTOutDither.x <= 0.0) return c;
    float2 p = floor(px) + (_UltraRTOutDither.y + stage * 17.0) * 5.588238;
    float n = frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
    return c + (n - 0.5) * (_UltraRTOutDither.x / 255.0);
}
#endif
