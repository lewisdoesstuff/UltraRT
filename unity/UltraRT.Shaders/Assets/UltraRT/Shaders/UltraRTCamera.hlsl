#ifndef ULTRART_CAMERA_INCLUDED
#define ULTRART_CAMERA_INCLUDED

// The camera ray through a clip-space point (ndc in -1..1), from an inverse view-projection matrix.
//
// The point is unprojected on the far plane and kept homogeneous: the direction is xyz - camPos * w, not xyz / w - camPos.
// Both are the same direction while w > 0 (w = 1 / far). A camera whose far / near ratio is beyond float precision has
// its far plane at infinity: (f + n) / (f - n) rounds to 1, w comes out 0 and xyz / w is inf / NaN, so every ray of the
// frame was NaN and missed. 7-4 (near 0.1 m, far 5,000,000 m) looked exactly like the game with ray tracing on
// for that reason (docs/scene-proxies.md, "Cameras with a far plane at infinity"). The homogeneous form is the limit at
// w = 0, so it holds for any far plane.
float3 UltraRTCameraRay(float4x4 invViewProj, float2 ndc, float3 camPos)
{
    float4 h = mul(invViewProj, float4(ndc, 1.0, 1.0));
    return normalize(h.xyz - camPos * h.w);
}

#endif
