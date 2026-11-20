// Draws the ray-traced result into the main camera's current target (injected after opaques + skybox), so the
// game's transparents, blood, post-processing, viewmodel camera and UI still draw on top. Fullscreen triangle,
// no render-target changes, no depth writes.
// It replaces the game's pixel with the RT image (the ratio mode of the hybrid view went with it on 2026-11-19).
Shader "UltraRT/Composite"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "UltraRTDither.hlsl"

            Texture2D _UltraRTImage;
            Texture2D _UltraRTNormalDepth;  // w < 0: the ray hit nothing (sky) -> keep the game's own sky
            Texture2D _UltraRTGameDepth;    // copy of the game's depth buffer (raw device depth)
            Texture2D<int2> _UltraRTInstanceId; // x: instance in the low 16 bits; seen through a portal, half(distance to the portal) << 16
            float4 _UltraRTDepthParams;     // x: guard x / P00, y: guard y / P11 (view ray from a trace uv), z: enabled
            float4 _UltraRTUVScale;         // xy: visible size / traced size (guard band; 0 = no guard band), z: _UltraRTUpsampled is set
            Texture2D _UltraRTUpsampled;    // temporal upsampler output (Upsample.compute): screen res, a = 0 where there is no RT surface
            SamplerState my_point_clamp_sampler;
            float _UltraRTFlipY;
            float4 _UltraRTHdrNative;       // x 1 = native float HDR (UltraRTToneCurve.hlsl): an upsampled a of -1 is a game pixel with HDR gain

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
                o.uv = p;
                // Rendering into a render texture on D3D flips clip space relative to the texture.
                if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
                if (_UltraRTFlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float RtViewZ(float2 uv, float t)
            {
                float2 ndc = uv * 2.0 - 1.0;
                return t / length(float3(ndc.x * _UltraRTDepthParams.x, ndc.y * _UltraRTDepthParams.y, 1.0));
            }

            // Distance to the first portal surface on this trace texel's path, or -1 (docs/portals.md). The game draws a
            // portal's view as a quad, so its depth buffer holds the portal plane there, while our t is the distance along
            // the path through the portal to the far-side hit. The depth tests accept either.
            float PortalT(float2 tuv)
            {
                uint tw, th;
                _UltraRTInstanceId.GetDimensions(tw, th);
                uint x = (uint)_UltraRTInstanceId.Load(int3(min(uint2(tuv * float2(tw, th)), uint2(tw - 1, th - 1)), 0)).x;
                return x > 0xFFFFu && x < 0x80000000u ? f16tof32(x >> 16) : -1.0; // sky: x = -1
            }

            float DepthErr(float2 tuv, float t, float gameViewZ)
            {
                float err = abs(RtViewZ(tuv, t) - gameViewZ);
                float tp = PortalT(tuv);
                return tp > 0.0 ? min(err, abs(RtViewZ(tuv, tp) - gameViewZ)) : err;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 screenUV = i.uv;
                if (_UltraRTUVScale.z > 0.5)
                {
                    // Replace modes with the temporal upsampler: it already matched the game's depth per screen pixel.
                    float4 up = _UltraRTUpsampled.Sample(my_point_clamp_sampler, screenUV);
                    // a < 0 (native float HDR only, RayReconstruction.compute Finish): the game's own pixel (lava, sky) with
                    // its bright part scaled for the HDR display.
                    if (up.a <= 0.0 && (up.a > -0.5 || _UltraRTHdrNative.x < 0.5)) discard;
                    return float4(UltraRTOutputDither(up.rgb, i.pos.xy, 0.0), 1.0);
                }
                // RT buffers may cover a slightly wider view than the screen (guard band): screen uv -> trace uv.
                float2 uvScale = _UltraRTUVScale.x > 0.0 ? _UltraRTUVScale.xy : float2(1.0, 1.0);
                float2 traceUV = (screenUV - 0.5) * uvScale + 0.5;
                float gameViewZ = 0.0;
                if (_UltraRTDepthParams.z > 0.5)
                {
                    // Depth-guided upsampling: the RT buffers are lower resolution than the screen. At silhouettes the
                    // nearest trace texel may belong to the background (or vice versa), giving dark/bright rims. Pick the
                    // one of the 2x2 nearest trace texels whose hit depth best matches the game's depth at this pixel,
                    // and read every RT buffer from it.
                    gameViewZ = LinearEyeDepth(_UltraRTGameDepth.Sample(my_point_clamp_sampler, i.uv).r);
                    uint tw, th;
                    _UltraRTNormalDepth.GetDimensions(tw, th);
                    float2 dim = float2(tw, th);
                    float2 base = floor(traceUV * dim - 0.5);
                    float2 bestUV = traceUV;
                    float bestScore = 1e30;
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        float2 texel = clamp(base + float2(k & 1, k >> 1), 0.0, dim - 1.0);
                        float2 tuv = (texel + 0.5) / dim;
                        float tt = _UltraRTNormalDepth.SampleLevel(my_point_clamp_sampler, tuv, 0).w;
                        float err = tt < 0.0 ? 1e20 : DepthErr(tuv, tt, gameViewZ);
                        if (err < bestScore) { bestScore = err; bestUV = tuv; }
                    }
                    i.uv = bestUV;
                }
                else i.uv = traceUV;

                float rtT = _UltraRTNormalDepth.Sample(my_point_clamp_sampler, i.uv).w;
                if (rtT < 0.0) discard;
                // Keep the game's pixel untouched where its depth disagrees with our hit:
                //  - game closer: an object the traced scene doesn't have (glow quads, decals, effects);
                //  - ours closer: an object we trace but the game doesn't draw (hidden/unused renderers).
                if (_UltraRTDepthParams.z > 0.5)
                {
                    float rtViewZ = RtViewZ(traceUV, rtT);
                    float tp = PortalT(i.uv);
                    bool far = abs(gameViewZ - rtViewZ) <= rtViewZ * 0.03 + 0.05;
                    float pz = tp > 0.0 ? RtViewZ(traceUV, tp) : -1e9;
                    bool plane = abs(gameViewZ - pz) <= pz * 0.03 + 0.05;
                    if (!far && !plane) discard;
                }

                return float4(UltraRTOutputDither(_UltraRTImage.Sample(my_point_clamp_sampler, i.uv).rgb, i.pos.xy, 0.0), 1.0);
            }
            ENDHLSL
        }
    }
}
