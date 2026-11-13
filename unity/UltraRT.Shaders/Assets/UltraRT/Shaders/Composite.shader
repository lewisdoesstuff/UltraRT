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
            Texture2D _UltraRTLighting;     // denoised bounce (demodulated, HDR)
            Texture2D<uint4> _UltraRTHybrid;  // packed halves: unshadowed direct rgb, shadowed direct rgb, lit weight
            Texture2D _UltraRTDirect;         // denoised shadowed direct (second denoiser instance)
            Texture2D _UltraRTAlbedo;
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

            float4 frag(v2f i) : SV_Target
            {
                if (_UltraRTNormalDepth.Sample(my_point_clamp_sampler, i.uv).w < 0.0) discard;

                if (_UltraRTMode > 0.5)
                {
                    // Hybrid: game colour with ray-traced shadows (as a ratio of our own lights, shadowed vs not) plus
                    // a scaled, denoised bounce. Where our lights barely reach a surface (the game lit it some other way,
                    // e.g. baked vertex light data), the ratio tends to 1 and the game's look is kept.
                    float3 game = _UltraRTGameColor.Sample(my_point_clamp_sampler, i.uv).rgb;
                    uint w, h;
                    _UltraRTHybrid.GetDimensions(w, h);
                    uint4 pk = _UltraRTHybrid.Load(int3(min(uint2(i.uv * float2(w, h)), uint2(w - 1, h - 1)), 0));
                    float4 un = float4(f16tof32(pk.x), f16tof32(pk.x >> 16), f16tof32(pk.y), f16tof32(pk.w)); // rgb unshadowed, a lit weight
                    float3 sh = _UltraRTDirect.Sample(my_point_clamp_sampler, i.uv).rgb; // denoised shadowed direct
                    float3 bounce = _UltraRTLighting.Sample(my_point_clamp_sampler, i.uv).rgb;
                    float3 albedo = _UltraRTAlbedo.Sample(my_point_clamp_sampler, i.uv).rgb;
                    float lit = un.a;
                    float3 shadow = (sh + _UltraRTShadowBias) / (un.rgb + _UltraRTShadowBias);
                    float3 result = game * lerp(1.0, shadow, lit) + _UltraRTGIStrength * albedo * bounce * lit;
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
