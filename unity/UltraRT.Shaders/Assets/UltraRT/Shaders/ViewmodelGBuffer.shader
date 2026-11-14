// Viewmodel G-buffer (docs/viewmodel.md). Plugin.Viewmodel.cs draws every opaque first-person renderer (layer 13 under
// the main camera) with a copy of this material in a command buffer on the HUD camera, right after the game drew them,
// so the projection is the HUD camera's own (FOV 90) and the pixels line up with the game's viewmodel. Properties use the
// proxy material's names (SceneProxies.Proxy copies them the same way). Five targets:
//   0 ARGB32   albedo rgb, a lit weight (0 = fullbright)
//   1 ARGBHalf world normal xyz (facing the camera), w material index (_VMMaterials, Viewmodel.raytrace)
//   2 ARGBHalf world position - camera position xyz, w frame stamp (_VMStamp): the targets are never cleared, a pixel
//              belongs to this frame only if its stamp matches
//   3 ARGBHalf emission rgb
//   4 ARGBHalf motion: xy previous - current screen uv, z previous - current view depth (rigid motion of the renderer
//              relative to the camera: weapon bob and sway; not bone animation)
// Pass 1 "Clear" writes stamp 0 everywhere (Plugin.Viewmodel.cs runs it every 1000 frames so stamps never repeat).
Shader "UltraRT/ViewmodelGBuffer"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _EmissiveTex ("Emissive", 2D) = "white" {}
        _EmissiveColor ("Emissive Color", Color) = (0,0,0,0)
        _EmissiveIntensity ("Emissive Intensity", Float) = 0
        _UseAlbedoAsEmissive ("Use Albedo As Emissive", Float) = 0
        _Cutoff ("Alpha cutoff", Float) = 0
        _AlphaTest ("Alpha test", Float) = 0
        _VMLit ("Lit weight (0 = fullbright)", Float) = 1
        _VMMatIndex ("Material index", Float) = 0
        _VMVertexColors ("Multiply by vertex colour (_VERTEXCOLORS_ON)", Float) = 0
        _IDTex ("Custom colour masks (rgb) and grey albedo (a)", 2D) = "white" {}
        _VMCustom ("CUSTOM_COLORS: albedo from _IDTex and _CustomColor1-3", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Name "GBuffer"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _EmissiveTex;
            float4 _EmissiveTex_ST;
            float4 _Color;
            float4 _EmissiveColor;
            float _EmissiveIntensity;
            float _UseAlbedoAsEmissive;
            float _Cutoff;
            float _AlphaTest;
            float _VMLit;
            float _VMMatIndex;
            float _VMVertexColors;
            float _VMCustom;
            sampler2D _IDTex;
            // Not material properties: the renderer's MaterialPropertyBlock (GunColorGetter) or the globals Plugin.Viewmodel.cs sets per draw.
            float4 _CustomColor1, _CustomColor2, _CustomColor3;
            float _VMStamp;            // global: this frame's stamp
            float4x4 _VMPrevDelta;     // global, set before each draw: view space now -> view space last frame (rigid)

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 wn : TEXCOORD1;
                float3 rel : TEXCOORD2;  // world position - camera
                float4 cur : TEXCOORD3;  // clip position (for the motion)
                float4 prev : TEXCOORD4; // last frame's clip position
                float2 depth : TEXCOORD5; // view depth now, last frame
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 view = UnityObjectToViewPos(v.vertex.xyz);
                float3 prevView = mul(_VMPrevDelta, float4(view, 1.0)).xyz;
                o.pos = mul(UNITY_MATRIX_P, float4(view, 1.0));
                o.cur = o.pos;
                o.prev = mul(UNITY_MATRIX_P, float4(prevView, 1.0));
                o.depth = float2(-view.z, -prevView.z);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.rel = mul(unity_ObjectToWorld, v.vertex).xyz - _WorldSpaceCameraPos;
                return o;
            }

            struct GBufferOut
            {
                float4 albedo : SV_Target0;
                float4 normal : SV_Target1;
                float4 rel : SV_Target2;
                float4 emission : SV_Target3;
                float4 motion : SV_Target4;
            };

            GBufferOut frag(v2f i)
            {
                float4 albedo = tex2D(_MainTex, i.uv) * _Color;
                if (_VMCustom > 0.5)
                {
                    // ULTRAKILL/Master CUSTOM_COLORS (alternate and custom gun colours), from its compiled code: _IDTex.a is a
                    // grey albedo, rgb masks blend it towards each custom colour (at most 80 %).
                    float4 id = tex2D(_IDTex, i.uv);
                    float3 t = id.rgb * 0.8;
                    float3 c = id.aaa;
                    c = lerp(c, id.a * _CustomColor1.rgb, t.x);
                    c = lerp(c, id.a * _CustomColor2.rgb, t.y);
                    c = lerp(c, id.a * _CustomColor3.rgb, t.z);
                    albedo.rgb = c;
                }
                if (_VMVertexColors > 0.5) albedo.rgb *= i.color.rgb;
                if (_AlphaTest > 0.5) clip(albedo.a - _Cutoff);
                float3 n = normalize(i.wn);
                if (dot(n, i.rel) > 0.0) n = -n; // two-sided: face the camera
                float3 e = 0;
                if (_EmissiveIntensity > 0.0)
                {
                    float3 et = _UseAlbedoAsEmissive > 0.5 ? albedo.rgb : tex2D(_EmissiveTex, i.uv * _EmissiveTex_ST.xy + _EmissiveTex_ST.zw).rgb;
                    e = et * _EmissiveColor.rgb * _EmissiveIntensity;
                }
                // Screen uv in the same convention as SV_Position / _ScreenParams (ComputeScreenPos handles the D3D flip).
                float4 sc = ComputeScreenPos(i.cur), sp = ComputeScreenPos(i.prev);
                float2 mv = i.prev.w > 1e-5 ? sp.xy / sp.w - sc.xy / sc.w : 0.0;
                GBufferOut o;
                o.albedo = float4(saturate(albedo.rgb), saturate(_VMLit));
                o.normal = float4(n, _VMMatIndex);
                o.rel = float4(i.rel, _VMStamp);
                o.emission = float4(min(e, 60000.0), 0.0);
                o.motion = float4(mv, i.depth.y - i.depth.x, 0.0);
                return o;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Clear"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            float4 vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 p = float2((id << 1) & 2, id & 2);
                return float4(p * 2.0 - 1.0, 0.0, 1.0);
            }
            struct ClearOut
            {
                float4 a : SV_Target0;
                float4 b : SV_Target1;
                float4 c : SV_Target2;
                float4 d : SV_Target3;
                float4 e : SV_Target4;
            };
            ClearOut frag()
            {
                ClearOut o;
                o.a = 0; o.b = 0; o.c = 0; o.d = 0; o.e = 0;
                return o;
            }
            ENDHLSL
        }
    }
}
