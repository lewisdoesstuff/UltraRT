// Hit shader of the blended veils (src/UltraRT/SceneProxies.Veils.cs, docs/scene-proxies.md "Blended veils"): soft
// alpha-blended layers the game draws without depth (8-1's cloud sheets over the city), added to the RTAS under the
// transparent mask (0x10). Never a closest hit: the any-hit shader blends the layer into the veil ray's payload
// (VeilTrace.raytrace) and ignores the hit, so one ray collects every layer along a segment. Every other ray passes.
Shader "UltraRT/VeilProxy"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // What multiplies texture x vertex colour: the game's _Color, alpha times _Opacity.
        _Tint ("Tint", Color) = (1,1,1,1)
        _VertexColors ("Vertex colours", Float) = 1
        // uv * _MainTex_ST + _ScrollOffset.xy + _ScrollSpeed.xy * time (copied every frame, as the lava proxies').
        _ScrollOffset ("Scroll offset", Vector) = (0,0,0,0)
        _ScrollSpeed ("Scroll speed", Vector) = (0,0,0,0)
        _ScrollTime ("Scroll time", Float) = 0
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
            SamplerState my_linear_repeat_sampler;
            float4 _Tint;
            float _VertexColors;
            float4 _ScrollOffset;
            float4 _ScrollSpeed;
            float _ScrollTime;

            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                if ((p.flags & ULTRART_PAYLOAD_VEILS) != 0u)
                {
                    uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                    float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                    float2 uv = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0) * b.x
                              + UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0) * b.y
                              + UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0) * b.z;
                    float4 c = _MainTex.SampleLevel(my_linear_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw + _ScrollOffset.xy + _ScrollSpeed.xy * _ScrollTime, 0) * _Tint;
                    if (_VertexColors > 0.5)
                        c *= UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeColor) * b.x
                           + UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeColor) * b.y
                           + UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeColor) * b.z;
                    float a = saturate(c.a);
                    if (a > 0.0 && all(c.rgb == c.rgb)) // also drops NaN
                    {
                        // Order-independent: the layers of one veil are alike, so their order barely matters.
                        p.albedo += max(c.rgb, 0.0) * a;
                        p.normal.x += a;
                        p.alpha *= 1.0 - a;
                    }
                }
                IgnoreHit();
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                // Never reached: every hit is ignored.
            }
            ENDHLSL
        }
    }
}
