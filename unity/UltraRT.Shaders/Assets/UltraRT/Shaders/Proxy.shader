// Material shader for the mod's hidden proxy renderers. It never rasterises; it only provides
// the ray tracing hit group ("UltraRTHit") that Unity looks up from each instance's material.
Shader "UltraRT/Proxy"
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
        _AtlasMode ("Atlas mode (StaticSceneOptimizer)", Float) = 0
    }

    SubShader
    {
        // Raster pass that draws nothing, in case a proxy ever reaches a camera.
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

            Texture2D _MainTex;
            float4 _MainTex_ST;
            Texture2D _EmissiveTex;
            float4 _EmissiveTex_ST;
            SamplerState my_point_repeat_sampler;

            float4 _Color;
            float4 _EmissiveColor;
            float _EmissiveIntensity;
            float _UseAlbedoAsEmissive;
            float _Cutoff;
            float _AtlasMode;

            float2 FetchUV(uint3 tri, float3 b)
            {
                float2 u0 = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0);
                float2 u1 = UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0);
                float2 u2 = UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
                return u0 * b.x + u1 * b.y + u2 * b.z;
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);

                float3 p0 = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributePosition);
                float3 p1 = UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributePosition);
                float3 p2 = UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributePosition);
                float3 nObj = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributeNormal) * b.x
                            + UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributeNormal) * b.y
                            + UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributeNormal) * b.z;
                uint flags = 0;
                if (dot(nObj, nObj) < 1e-8)
                {
                    nObj = cross(p1 - p0, p2 - p0);
                    flags |= 1;
                }

                float3 n = normalize(mul(nObj, (float3x3)WorldToObject3x4()));
                if (dot(n, WorldRayDirection()) > 0.0) n = -n; // treat everything as two-sided for now

                float2 uv = FetchUV(tri, b);
                float2 texUV = uv * _MainTex_ST.xy + _MainTex_ST.zw;
                if (_AtlasMode > 0.5)
                {
                    // Atlas rect (xMin, yMin, xMax, yMax) is constant per face, so the first vertex's value is enough.
                    float4 rect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord1);
                    texUV = lerp(rect.xy, rect.zw, frac(uv));
                }
                float4 albedo = _MainTex.SampleLevel(my_point_repeat_sampler, texUV, 0) * _Color;

                float3 emission = 0;
                if (_EmissiveIntensity > 0.0)
                {
                    float3 e = _UseAlbedoAsEmissive > 0.5
                        ? albedo.rgb
                        : _EmissiveTex.SampleLevel(my_point_repeat_sampler, uv * _EmissiveTex_ST.xy + _EmissiveTex_ST.zw, 0).rgb;
                    emission = e * _EmissiveColor.rgb * _EmissiveIntensity;
                }

                p.albedo = albedo.rgb;
                p.alpha = albedo.a;
                p.normal = n;
                p.t = RayTCurrent();
                p.emission = emission;
                p.instance = InstanceIndex();
                p.uv = uv;
                p.primitive = PrimitiveIndex();
                p.flags = flags;
            }
            ENDHLSL
        }
    }
}
