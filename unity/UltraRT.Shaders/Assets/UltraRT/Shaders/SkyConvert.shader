// Converts a cubemap sky (ULTRAKILL/Skybox/Skybox with _TEXMODE_CUBEMAP, e.g. 8-2's FraudCity_SkyMat) into the lat-long
// layout the ray tracing shaders sample as _SkyTex (UltraRTSky.hlsl SkyUV with rotation 0): u = atan2(x, z) / 2pi + 0.5,
// v = asin(y) / pi + 0.5. Plugin.SkyGI.cs blits it once per sky change (docs/relight.md "Area colour").
Shader "Hidden/UltraRT/SkyConvert"
{
    Properties
    {
        _Cube ("Cubemap", Cube) = "" {}
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Cube;

            float4 frag(v2f_img i) : SV_Target
            {
                float phi = (i.uv.x - 0.5) * 6.28318531;
                float lat = (i.uv.y - 0.5) * 3.14159265;
                float3 d = float3(sin(phi) * cos(lat), sin(lat), cos(phi) * cos(lat));
                return float4(texCUBElod(_Cube, float4(d, 0)).rgb, 1);
            }
            ENDCG
        }
    }
}
