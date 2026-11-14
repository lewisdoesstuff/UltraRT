// Water and glass on top of the game's own transparent draw (docs/reflections.md, "Water and glass").
// TransparentSurfaces.cs appends this material to a water/glass renderer's material list, so Unity draws the same
// (last) submesh again right after the game's material, at the same point of the transparent queue: particles and
// effects in front of the surface still draw over it. Three passes, all reading screen-space inputs:
//   1-2 RefractAdd / RefractSub (water only): screen-space refraction. The game blended its water colour W over the
//       background B: dst = W a + B (1 - a). Shifting the background by the rippled refraction offset is
//       dst + (1 - a) (B(uv') - B(uv)); a signed term, so it is added and subtracted in two blended passes.
//       B is a copy of the colour target taken after the RT composite, before any transparents (_UltraRTPreAlpha).
//   3   Reflect: dst * (1 - w) + w * R, with R the ray-traced reflection (PathTrace.raytrace, TransparentGen) at trace
//       resolution and w = Fresnel(ripple normal) x weight, faded by fog.
// Everything is skipped unless the plugin traced this camera's frame (_UltraRTTransOn, camera position match).
Shader "UltraRT/TransparentOverlay"
{
    Properties
    {
        _MainTex ("Source albedo (alpha estimate)", 2D) = "white" {}
        _Color ("Source colour", Color) = (1,1,1,1)
        _Opacity ("Source opacity", Float) = 1
        _Kind ("0 glass, 1 water", Float) = 0
        _F0 ("F0", Float) = 0.04
        _Weight ("Reflection weight", Float) = 1
        _CullMode ("Cull", Float) = 2
    }

    SubShader
    {
        // RenderType: no replacement shader ever matches this.
        Tags { "Queue" = "Transparent" "RenderType" = "UltraRTOverlay" "IgnoreProjector" = "True" }
        ZWrite Off
        ZTest LEqual
        Cull [_CullMode]
        Offset -1, -1
        ColorMask RGB

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "UltraRTWater.hlsl"

        sampler2D _MainTex;
        float4 _MainTex_ST;
        float4 _Color;
        float _Opacity;
        float _Kind;
        float _F0;
        float _Weight;

        float _UltraRTTransOn;           // 1 while the plugin traced transparents this frame
        float4 _UltraRTTransCam;         // xyz camera position of that trace, w = refraction inputs bound
        float4x4 _UltraRTTransVP;        // the trace's view-projection (GL convention, not flipped)
        Texture2D _UltraRTTransRefl;     // rgb reflected radiance, a = distance to the transparent hit (< 0 none)
        float4 _UltraRTTransParams;      // x global strength, y max weight, z tone map white point (0 = off)
        StructuredBuffer<float4> _UltraRTExposureGlobal; // relight auto exposure ([0].x), bound globally when in use
        float _UltraRTAutoExposure;      // 1: apply _UltraRTExposureGlobal (as the denoiser's composite does for the lit part)
        float4 _UltraRTFog;              // x mode (0 off, 1 linear, 2 exp, 3 exp2), y start, z end, w density
        Texture2D _UltraRTPreAlpha;      // colour target after the RT composite, before transparents
        Texture2D _UltraRTGameDepth;     // the game's opaque depth (raw), copied by the RT composite
        float4 _UltraRTDepthParams;      // z > 0.5: _UltraRTGameDepth valid
        SamplerState my_point_clamp_sampler;
        SamplerState my_linear_clamp_sampler;

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float3 wpos : TEXCOORD0;
            float3 wn : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float eye : TEXCOORD3;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.wn = UnityObjectToWorldNormal(v.normal);
            o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            o.eye = -UnityObjectToViewPos(v.vertex.xyz).z;
            return o;
        }

        // Shared set-up: view vector, geometric and rippled normals. False: nothing to do for this camera/frame.
        bool Surface(v2f i, out float3 v, out float dist, out float3 ng, out float3 n)
        {
            v = _WorldSpaceCameraPos - i.wpos;
            dist = max(length(v), 1e-4);
            v /= dist;
            ng = normalize(i.wn);
            if (dot(ng, v) < 0.0) ng = -ng;
            n = _Kind > 0.5 ? WaterNormal(ng, i.wpos, dist) : ng;
            if (_UltraRTTransOn < 0.5) return false;
            // Water seen from below: the game draws its own underwater look, and a mirror there would be wrong (that is
            // total internal reflection, not handled). With the camera inside a water renderer's bounds the plugin does
            // not trace at all (_UltraRTTransOn stays 0); this catches surfaces above a camera outside those bounds.
            if (_Kind > 0.5 && abs(ng.y) > 0.7 && _WorldSpaceCameraPos.y < i.wpos.y) return false;
            return distance(_WorldSpaceCameraPos, _UltraRTTransCam.xyz) < 0.01;
        }

        float FogVisibility(float d)
        {
            float m = _UltraRTFog.x;
            if (m < 0.5) return 1.0;
            if (m < 1.5) return saturate((_UltraRTFog.z - d) / max(_UltraRTFog.z - _UltraRTFog.y, 1e-3));
            float x = _UltraRTFog.w * d;
            return m < 2.5 ? exp2(-x * 1.442695) : exp2(-x * x * 1.442695);
        }

        // Relight's shoulder tone map (Denoise.compute, composite), so reflections roll off like the surfaces around.
        float3 Tonemap(float3 c)
        {
            float white = _UltraRTTransParams.z;
            if (white <= 0.0) return saturate(c);
            const float knee = 0.75;
            float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
            if (lum > knee)
            {
                float x = lum - knee, room = 1.0 - knee, range = max(white - knee, 1e-3);
                c *= (knee + room * x * (1.0 + x / (range * range) * room) / (x + room)) / lum;
            }
            return saturate(c);
        }

        // Screen-space refraction (water): (1 - a) (B(uv') - B(uv)), uv' = where the rippled refracted ray meets the
        // background, approximated by shifting the surface point along the refraction difference.
        float3 RefractionDelta(v2f i)
        {
            float3 v, ng, n;
            float dist;
            if (!Surface(i, v, dist, ng, n) || _Kind < 0.5 || _UltraRTTransCam.w < 0.5 || _UltraRTDepthParams.z < 0.5 || _UltraRTWater.w <= 0.0)
                discard;
            float2 suv = i.pos.xy / _ScreenParams.xy;
            float behind = LinearEyeDepth(_UltraRTGameDepth.SampleLevel(my_point_clamp_sampler, suv, 0).r) - i.eye;
            if (behind <= 0.02) discard;
            float3 t1 = refract(-v, n, 0.75);
            float3 t0 = refract(-v, ng, 0.75);
            float3 p1 = i.wpos + (t1 - t0) * (min(behind, 4.0) * _UltraRTWater.w);
            float4 c0 = mul(UNITY_MATRIX_VP, float4(i.wpos, 1.0));
            float4 c1 = mul(UNITY_MATRIX_VP, float4(p1, 1.0));
            float4 s0 = ComputeScreenPos(c0), s1 = ComputeScreenPos(c1);
            float2 uv2 = suv + (s1.xy / s1.w - s0.xy / s0.w);
            if (any(uv2 < 0.0) || any(uv2 > 1.0)) discard;
            // Only background that is itself behind the surface (not an object in front of the water).
            if (LinearEyeDepth(_UltraRTGameDepth.SampleLevel(my_point_clamp_sampler, uv2, 0).r) <= i.eye + 0.02) discard;
            float a = saturate(tex2D(_MainTex, i.uv).a * _Color.a * _Opacity);
            float3 d = (1.0 - a) * (_UltraRTPreAlpha.SampleLevel(my_point_clamp_sampler, uv2, 0).rgb
                                  - _UltraRTPreAlpha.SampleLevel(my_point_clamp_sampler, suv, 0).rgb);
            return d * FogVisibility(dist);
        }
        ENDCG

        Pass
        {
            Name "RefractAdd"
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            float4 frag(v2f i) : SV_Target { return float4(max(RefractionDelta(i), 0.0), 0.0); }
            ENDCG
        }

        Pass
        {
            Name "RefractSub"
            BlendOp RevSub
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            float4 frag(v2f i) : SV_Target { return float4(max(-RefractionDelta(i), 0.0), 0.0); }
            ENDCG
        }

        Pass
        {
            Name "Reflect"
            Blend One SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            float4 frag(v2f i) : SV_Target
            {
                float3 v, ng, n;
                float dist;
                if (!Surface(i, v, dist, ng, n)) discard;

                // The trace texel(s) under this fragment: bilinear over the 2x2 nearest, keeping only texels whose
                // transparent hit is this surface (same distance), so edges against walls and other surfaces stay clean.
                float4 clip = mul(_UltraRTTransVP, float4(i.wpos, 1.0));
                float2 tuv = clip.xy / clip.w * 0.5 + 0.5;
                uint w, h;
                _UltraRTTransRefl.GetDimensions(w, h);
                float2 f = tuv * float2(w, h) - 0.5;
                int2 b = (int2)floor(f);
                float2 fr = f - b;
                float3 sum = 0;
                float ws = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 tx = clamp(b + o, int2(0, 0), int2(w - 1, h - 1));
                    float4 r = _UltraRTTransRefl.Load(int3(tx, 0));
                    float wb = (o.x ? fr.x : 1.0 - fr.x) * (o.y ? fr.y : 1.0 - fr.y);
                    if (r.a > 0.0 && abs(r.a - dist) < dist * 0.03 + 0.1) { sum += r.rgb * wb; ws += wb; }
                }
                if (ws < 1e-4) discard;
                float3 refl = sum / ws;
                if (_UltraRTAutoExposure > 0.5) refl *= _UltraRTExposureGlobal[0].x;
                refl = Tonemap(refl);

                float fres = SchlickFresnel(_F0, dot(n, v));
                float wgt = min(saturate(fres * _Weight * _UltraRTTransParams.x) * FogVisibility(dist), _UltraRTTransParams.y);
                return float4(refl * wgt, 1.0 - wgt);
            }
            ENDCG
        }
    }
}
