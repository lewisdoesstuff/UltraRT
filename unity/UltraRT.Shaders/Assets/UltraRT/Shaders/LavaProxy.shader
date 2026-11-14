// Hit group for ray-traced lava and acid (docs/lava.md). The game draws them as additive transparents
// (ULTRAKILL/Master, TRANSPARENCY, _BlendMode 3), which the opaque scene skips. SceneProxies.Lava.cs gives each lava
// renderer a proxy with this material under EmitterMask (0x08): shadow rays (0x05) pass through it, while bounce,
// reflection and cache rays (ULTRART_MASK_SCENE) see a black, fullbright surface whose emission is the lava texture.
// Camera rays (PathTrace's primary ray, flagged ULTRART_PAYLOAD_WANT_MOTION) are steered by the global
// _UltraRTLavaCamera (Plugin.Lava.cs): 0 (hybrid) = they pass through in the any-hit shader, so the game's own additive
// lava is drawn over the RT floor as in the raster game; > 0 (replace modes) = they stop at the lava, which shows that
// fraction of its emission instead of the floor below it, and the game's additive lava is drawn on top.
Shader "UltraRT/LavaProxy"
{
    Properties
    {
        // EmissiveLights.cs reads these the same way as on UltraRT/Proxy (_UseAlbedoAsEmissive: _MainTex * _Color).
        _MainTex ("Lava texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _EmissiveTex ("Emissive", 2D) = "white" {}
        _EmissiveColor ("Emissive Color", Color) = (1,1,1,1)
        _EmissiveIntensity ("Emissive Intensity", Float) = 1
        _UseAlbedoAsEmissive ("Use Albedo As Emissive", Float) = 1
        // The game's EMISSIVE term on top (acid): _EmissiveTex * _GameEmissive (rgb already x intensity).
        _GameEmissive ("Game emissive (rgb)", Color) = (0,0,0,0)
        // ScrollingTexture: uv * _MainTex_ST + _ScrollOffset.xy + _ScrollSpeed.xy * time (copied every frame).
        _ScrollOffset ("Scroll offset", Vector) = (0,0,0,0)
        _ScrollSpeed ("Scroll speed", Vector) = (0,0,0,0)
        _ScrollTime ("Scroll time", Float) = 0
        _MatClass ("Material class", Float) = 21
    }

    SubShader
    {
        Pass
        {
            Name "NoDraw"
            ColorMask 0
            ZWrite Off
            ZTest Never
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert() : SV_POSITION { return float4(0, 0, -2, 1); }
            float4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "UltraRTHit"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            #pragma raytracing UltraRTHit

            #include "UnityRayTracingMeshUtils.cginc"
            #include "UltraRTCommon.hlsl"
            #include "UltraRTMaterial.hlsl"

            Texture2D _MainTex;
            float4 _MainTex_ST;
            Texture2D _EmissiveTex;
            float4 _EmissiveTex_ST;
            SamplerState my_point_repeat_sampler;
            float4 _Color;
            float4 _EmissiveColor;
            float _EmissiveIntensity;
            float4 _GameEmissive;
            float4 _ScrollOffset;
            float4 _ScrollSpeed;
            float _ScrollTime;
            float _UltraRTLavaCamera; // global, not a material property: 0 camera rays pass, > 0 emission they see
            float _MatClass;

            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                if (!(_UltraRTLavaCamera > 0.0) && (p.flags & ULTRART_PAYLOAD_WANT_MOTION) != 0u) IgnoreHit();
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                float3 p0 = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributePosition);
                float3 p1 = UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributePosition);
                float3 p2 = UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributePosition);
                float3 n = normalize(mul(cross(p1 - p0, p2 - p0), (float3x3)WorldToObject3x4()));
                if (dot(n, WorldRayDirection()) > 0.0) n = -n;

                float2 u0 = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0);
                float2 u1 = UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0);
                float2 u2 = UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
                float2 uv = u0 * b.x + u1 * b.y + u2 * b.z;
                float2 scroll = _ScrollOffset.xy + _ScrollSpeed.xy * _ScrollTime;
                float4 tex = _MainTex.SampleLevel(my_point_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw + scroll, 0);
                float3 e = tex.rgb * _Color.rgb * _EmissiveColor.rgb * _EmissiveIntensity;
                if (any(_GameEmissive.rgb > 0.0))
                    e += _EmissiveTex.SampleLevel(my_point_repeat_sampler, uv * _EmissiveTex_ST.xy + _EmissiveTex_ST.zw + scroll, 0).rgb
                       * _GameEmissive.rgb * _EmissiveIntensity;

                if ((p.flags & ULTRART_PAYLOAD_WANT_MOTION) != 0u) e *= _UltraRTLavaCamera; // camera ray, replace modes

                p.albedo = 0;
                p.alpha = 0.0; // fullbright: lighting never applies, only the emission
                p.normal = n;
                p.t = RayTCurrent();
                p.emissionHalf = PackEmissionHalf(e);
                p.instance = InstanceIndex();
                p.uvHalf = f32tof16(uv.x) | (f32tof16(uv.y) << 16);
                p.primitive = PrimitiveIndex();
                p.motion = 0u;
                p.material = PackMaterialWord(1.0, 0.0, 0.02, 0.0);
                p.flags = 1u | ((InstanceID() & 0xFFu) << 8) | PackMaterialFlags(ULTRART_REFL_NONE, 0.0, (uint)_MatClass);
            }
            ENDHLSL
        }
    }
}
