// Hit shader of the effect billboards (src/UltraRT/EffectGeometry.cs, docs/path-tracing.md "Effects in reflections"):
// the game's glowing particle systems, sprites, trails and lines, baked into meshes and added to the RTAS under the
// transparent mask (0x10). They are never a closest hit: the any-hit shader adds what the game's blend would add to
// the payload and ignores the hit, so one ray collects every billboard along a segment (EffectsAlong,
// UltraRTEffects.hlsl). Rays that didn't ask for effects (the water/glass pass shares the mask) pass through.
Shader "UltraRT/EffectProxy"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // rgb: what multiplies texture x vertex colour (legacy particle shaders: 2 x _TintColor); a: the same for alpha.
        _Tint ("Tint", Color) = (1,1,1,1)
        // 0: SrcAlpha One (rgb x alpha is added), 1: One One (rgb is added).
        _Mode ("Blend", Float) = 0
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
            SamplerState my_point_repeat_sampler;
            float4 _Tint;
            float _Mode;

            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                if ((p.flags & ULTRART_PAYLOAD_EFFECTS) != 0u)
                {
                    uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                    float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                    float2 uv = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0) * b.x
                              + UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0) * b.y
                              + UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0) * b.z;
                    float4 vc = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeColor) * b.x
                              + UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeColor) * b.y
                              + UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeColor) * b.z;
                    float4 c = _MainTex.SampleLevel(my_point_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw, 0) * vc * _Tint;
                    float3 add = _Mode > 0.5 ? c.rgb : c.rgb * saturate(c.a);
                    p.albedo += max(add, 0.0); // the effects ray's payload: albedo = radiance added along the segment
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
