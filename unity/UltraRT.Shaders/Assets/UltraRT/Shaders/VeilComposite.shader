// Blended veils behind portals (docs/scene-proxies.md, "Blended veils"): one fullscreen pass into the main camera's
// target right after the RT composite, before the game's transparents. _VeilTex (VeilTrace.raytrace): rgb the veil
// layers' colour times their cover, a what is left of the image behind them. Blended in display space, as the game
// blends the layers over its own image: out = image x a + rgb.
Shader "UltraRT/VeilComposite"
{
    SubShader
    {
        Pass
        {
            Name "Veils"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One SrcAlpha
            ColorMask RGB

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            Texture2D _VeilTex;
            SamplerState my_linear_clamp_sampler;
            float _UltraRTFlipY;

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
                if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
                if (_UltraRTFlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 v = _VeilTex.SampleLevel(my_linear_clamp_sampler, i.uv, 0);
                if (v.a >= 0.9995) discard;
                return v;
            }
            ENDHLSL
        }
    }
}
