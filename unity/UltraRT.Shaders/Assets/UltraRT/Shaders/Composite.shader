// Draws the ray-traced image into the main camera's current target (injected after opaques + skybox), so the
// game's transparents, blood, post-processing, viewmodel camera and UI still draw on top. Fullscreen triangle,
// no render-target changes, no depth writes.
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
            #include "UnityCG.cginc"

            Texture2D _UltraRTImage;
            SamplerState my_point_clamp_sampler;
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
                // Rendering into a render texture on D3D flips clip space relative to the texture.
                if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
                if (_UltraRTFlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(_UltraRTImage.Sample(my_point_clamp_sampler, i.uv).rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
