// Draws the ray-traced result into the main camera's current target (injected after opaques + skybox), so the
// game's transparents, blood, post-processing, viewmodel camera and UI still draw on top. Fullscreen triangle,
// no render-target changes, no depth writes.
//
// Modes:
//   0 replace: output the RT image (albedo * RT lighting + emission).
//   1 ratio (hybrid): keep the game's own shaded colour and apply only what ray tracing changes:
//        game colour * clamp(RT lighting / game-style unshadowed lighting)
//     so the game's art direction (vertex lighting, fill lights, palette) survives, with shadows/GI on top.
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

            Texture2D _UltraRTImage;
            Texture2D _UltraRTNormalDepth;  // w < 0: the ray hit nothing (sky) -> keep the game's own sky
            Texture2D _UltraRTGameDepth;    // copy of the game's depth buffer (raw device depth)
            float4 _UltraRTDepthParams;     // x: 1/P00, y: 1/P11 (view ray from uv), z: enabled
            Texture2D _UltraRTLighting;     // denoised bounce (demodulated, HDR)
            Texture2D<uint4> _UltraRTHybrid;  // packed halves: unshadowed direct rgb, shadowed direct rgb; w: lit weight unorm8 | game cubemap term rgb unorm8 << 8
            Texture2D _UltraRTDirect;         // denoised shadowed direct (second denoiser instance)
            Texture2D _UltraRTAlbedo;
            Texture2D _UltraRTSpecular;     // reflections (Reflections.compute Resolve): rgb added, a keep factor
            Texture2D _UltraRTGameColor;    // copy of the game's colour target at this point (full res)
            SamplerState my_point_clamp_sampler;
            float _UltraRTFlipY;
            float _UltraRTMode;
            float _UltraRTShadowBias;
            float _UltraRTGIStrength;

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

            float4 frag(v2f i) : SV_Target
            {
                float2 screenUV = i.uv;
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
                    float2 base = floor(i.uv * dim - 0.5);
                    float bestErr = 1e30;
                    float2 bestUV = i.uv;
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        float2 texel = clamp(base + float2(k & 1, k >> 1), 0.0, dim - 1.0);
                        float2 tuv = (texel + 0.5) / dim;
                        float tt = _UltraRTNormalDepth.SampleLevel(my_point_clamp_sampler, tuv, 0).w;
                        float err = tt < 0.0 ? 1e20 : abs(RtViewZ(tuv, tt) - gameViewZ);
                        if (err < bestErr) { bestErr = err; bestUV = tuv; }
                    }
                    i.uv = bestUV;
                }

                float rtT = _UltraRTNormalDepth.Sample(my_point_clamp_sampler, i.uv).w;
                if (rtT < 0.0) discard;
                // Keep the game's pixel untouched where its depth disagrees with our hit:
                //  - game closer: an object the traced scene doesn't have (glow quads, decals, effects);
                //  - ours closer: an object we trace but the game doesn't draw (hidden/unused renderers).
                if (_UltraRTDepthParams.z > 0.5)
                {
                    float rtViewZ = RtViewZ(screenUV, rtT);
                    if (abs(gameViewZ - rtViewZ) > rtViewZ * 0.03 + 0.05) discard;
                }

                if (_UltraRTMode > 0.5)
                {
                    // Hybrid: game colour with ray-traced shadows (as a ratio of our own lights, shadowed vs not) plus
                    // a scaled, denoised bounce. Where our lights barely reach a surface (the game lit it some other way,
                    // e.g. baked vertex light data), the ratio tends to 1 and the game's look is kept.
                    float3 game = _UltraRTGameColor.Sample(my_point_clamp_sampler, screenUV).rgb;
                    uint w, h;
                    _UltraRTHybrid.GetDimensions(w, h);
                    uint4 pk = _UltraRTHybrid.Load(int3(min(uint2(i.uv * float2(w, h)), uint2(w - 1, h - 1)), 0)); // i.uv: chosen trace texel centre
                    float4 un = float4(f16tof32(pk.x), f16tof32(pk.x >> 16), f16tof32(pk.y), (pk.w & 0xFFu) * (1.0 / 255.0)); // rgb unshadowed, a lit weight
                    // The game's own cubemap reflection where ours replaces it (0 elsewhere; UltraRTMaterial.hlsl): removed
                    // before the shadow ratio, which only applies to the lit part. Reconstructed at trace resolution.
                    float3 gameCube = float3((pk.w >> 8) & 0xFFu, (pk.w >> 16) & 0xFFu, pk.w >> 24) * (1.0 / 255.0);
                    float2 shun = _UltraRTDirect.Sample(my_point_clamp_sampler, i.uv).rg; // denoised luminance: shadowed, unshadowed
                    float3 bounce = _UltraRTLighting.Sample(my_point_clamp_sampler, i.uv).rgb;
                    float3 albedo = _UltraRTAlbedo.Sample(my_point_clamp_sampler, i.uv).rgb;
                    float lit = un.a;
                    float3 sh = un.rgb * (shun.x / max(shun.y, 1e-4)); // for the debug view
                    float3 shadow = (shun.x + _UltraRTShadowBias) / (shun.y + _UltraRTShadowBias);
                    float3 result = max(game - gameCube, 0.0) * lerp(1.0, shadow, lit) + _UltraRTGIStrength * albedo * bounce * lit;
                    // Reflections: REFLECTION blend -> lerp(result, refl, RS); additive -> result + RS^2 * refl; other
                    // specular classes -> result + F * w * refl (metals: partly replaced). Where the game's cubemap was
                    // removed, spec.a also undoes its (1 - RS) on the lit colour (docs/reflections.md).
                    float4 spec = _UltraRTSpecular.Sample(my_point_clamp_sampler, i.uv);
                    result = result * spec.a + spec.rgb;
                    if (_UltraRTMode > 1.5)
                    {
                        // Debug: 2 = shadow ratio, 3 = unshadowed direct x0.25, 4 = shadowed direct x0.25, 5 = bounce x0.5
                        if (_UltraRTMode < 2.5) return float4(shadow, 1.0);
                        if (_UltraRTMode < 3.5) return float4(un.rgb * 0.25, 1.0);
                        if (_UltraRTMode < 4.5) return float4(sh * 0.25, 1.0);
                        return float4(bounce * 0.5, 1.0);
                    }
                    return float4(result, 1.0);
                }
                return float4(_UltraRTImage.Sample(my_point_clamp_sampler, i.uv).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
