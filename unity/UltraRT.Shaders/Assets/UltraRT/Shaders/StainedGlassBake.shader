// Stained-glass projection (Plugin.StainedGlass.cs, docs/materials.md "Stained-glass projection"): draws a window's own
// mesh (the game's, with its UVs, atlas rects and vertex colours) flat into one slice of the window texture array. The
// vertex shader places each vertex by its world position in the window's frame (_SGOrigin, _SGU, _SGV: one texel row
// per raw D3D texture row, as UltraRTStainedGlass.hlsl samples it), so the texture lands where the light would cross the
// pane. Triangles of the same submesh that lie off the window's plane (frames, other windows) are discarded.
// Output: the pane's gamma colour (texture x _Color, atlas slots x vertex colour) with alpha 1; uncovered texels stay 0.
Shader "Hidden/UltraRT/StainedGlassBake"
{
    Properties
    {
        _SGTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D _SGTex;
            float4 _SGTex_ST;     // the material's tiling and offset (ignored for atlas slots)
            float4 _SGColor;
            float4 _SGOrigin;     // xyz the window's corner (u = v = 0), w 1 = atlas slot (StaticSceneOptimizer)
            float4 _SGU;          // xyz U / width (texture u per metre)
            float4 _SGV;          // xyz V / height
            float4 _SGN;          // xyz the window's normal, w the plane tolerance (m)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                nointerpolation float4 rect : TEXCOORD1;
                float4 color : TEXCOORD2;
                float plane : TEXCOORD3;
            };

            v2f Vert(appdata v)
            {
                v2f o;
                float3 w = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz - _SGOrigin.xyz;
                float u = dot(w, _SGU.xyz), t = dot(w, _SGV.xyz);
                // Raw clip space (no Unity projection: the matrices are identity). y = +1 is texture row 0, which is v = 0
                // for SampleLevel in the ray tracing shader: the same raw convention on both sides.
                o.pos = float4(u * 2.0 - 1.0, 1.0 - t * 2.0, 0.5, 1.0);
                o.uv0 = v.uv0;
                o.rect = v.uv1;
                o.color = v.color;
                o.plane = dot(w, _SGN.xyz);
                return o;
            }

            float4 Frag(v2f i) : SV_Target
            {
                if (abs(i.plane) > _SGN.w) discard;
                float4 c;
                if (_SGOrigin.w > 0.5)
                {
                    // Atlas slot (Proxy.shader, _AtlasMode): UV0 tiles within the vertex's atlas rect.
                    c = tex2Dlod(_SGTex, float4(lerp(i.rect.xy, i.rect.zw, frac(i.uv0)), 0, 0));
                    c.rgb *= i.color.rgb;
                }
                else c = tex2Dlod(_SGTex, float4(i.uv0 * _SGTex_ST.xy + _SGTex_ST.zw, 0, 0));
                c *= _SGColor;
                return float4(saturate(c.rgb), 1.0);
            }
            ENDCG
        }
    }
}
