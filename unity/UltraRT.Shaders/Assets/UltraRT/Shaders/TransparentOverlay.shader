// Water and glass on top of the game's own transparent draw (docs/reflections.md, "Water and glass").
// TransparentSurfaces.cs appends this material to a water/glass renderer's material list, so Unity draws the same
// (last) submesh again right after the game's material, at the same point of the transparent queue: particles and
// effects in front of the surface still draw over it. Four passes, all reading screen-space inputs:
//   1-2 RefractAdd / RefractSub (water only): screen-space refraction. The game blended its water colour W over the
//       background B: dst = W a + B (1 - a). Shifting the background by the rippled refraction offset is
//       dst + (1 - a) (B(uv') - B(uv)); a signed term, so it is added and subtracted in two blended passes.
//       B is a copy of the colour target taken after the RT composite, before any transparents (_UltraRTPreAlpha).
//   3   Absorb: dst * T, multiplicative, so it is safe on 8-bit targets.
//       Blood: T = 1 - (1 - a) s (1 - tint): what shows through the game's thin alpha-blended blood (the floor, relit and
//       much brighter than the game's own) is absorbed toward the blood's colour, so a river of blood reads as thick liquid.
//       Water: Beer-Lambert over the path through the water to the game's opaque depth (docs/reflections.md, "Water
//       absorption"): T = lerp(deep, 1, exp(-sigma x path)) on the share (1 - a) the game lets through, deep = the
//       water's average colour (hue only) x a floor, so shallow edges stay clear and deep water darkens and tints.
//   4   Reflect: dst * (1 - w) + w * R, with R the ray-traced reflection (PathTrace.raytrace, TransparentGen) at trace
//       resolution and w = Fresnel(ripple normal) x weight, faded by fog.
// Everything is skipped unless the plugin traced this camera's frame (_UltraRTTransOn, camera position match).
Shader "UltraRT/TransparentOverlay"
{
    Properties
    {
        _MainTex ("Source albedo (alpha estimate)", 2D) = "white" {}
        _Color ("Source colour", Color) = (1,1,1,1)
        _Opacity ("Source opacity", Float) = 1
        _Kind ("0 glass, 1 water, 2 blood", Float) = 0
        _F0 ("F0", Float) = 0.04
        _Weight ("Reflection weight", Float) = 1
        _CullMode ("Cull", Float) = 2
        _Absorb ("Blood: absorption of what shows through", Float) = 0
        // LIMBO_WATER_FADE (1-3's floor water): the game fades the water out within _NearClip.._FarClip of the player and
        // every enemy (LimboWater.cs sets the global _FadePositions); no water there means no reflection or refraction.
        _LimboFade ("Limbo water fade", Float) = 0
        _NearClip ("Fade start", Float) = 0
        _FarClip ("Fade end", Float) = 1
        _FadeDither ("Fade dither", 2D) = "gray" {}
        // The game's VERTEX_DISPLACEMENT values, copied from the water material under the game's names (so a renderer's
        // property block reaches them too). _UltraRTDisplace 1: displace like the game (UltraRTDisplace.hlsl).
        _UltraRTDisplace ("Displace like the game", Float) = 0
        _VertexNoiseTex ("Vertex noise", 2D) = "gray" {}
        _FlowDirection ("Flow direction", Vector) = (1, 1, 1, 1)
        _VertexMode ("Vertex mode", Float) = 0
        _ReverseFlow ("Reverse flow", Float) = 0
        _VertexNoiseScale ("Noise scale", Float) = 1
        _VColStrength ("Vertex colour strength", Float) = 0
        _UVNoiseScale ("UV noise scale", Vector) = (1, 1, 0, 0)
        _UVNoiseSpeed ("UV noise speed", Vector) = (1, 1, 0, 0)
        _VertexNoiseSpeed ("Noise speed", Float) = 0
        _VertexNoiseAmplitude ("Amplitude", Float) = 0
        _LocalOffset ("Local offset", Float) = 0
        _RecalculateNormals ("Recalculate normals", Float) = 0
        _NormalOffsetScale ("Normal offset", Float) = 1
        _PreventClipping ("Prevent clipping", Float) = 0
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
        #include "UltraRTToneCurve.hlsl"
        #include "UltraRTDisplace.hlsl"

        sampler2D _MainTex;
        float4 _MainTex_ST;
        float4 _Color;
        float _Opacity;
        float _Kind;
        float _F0;
        float _Weight;
        float _Absorb;
        float _LimboFade, _NearClip, _FarClip;
        float4 _UltraRTWaterAbsorb;      // global: x sigma (1/m, 0 = off), y transmittance floor of deep water, z tint saturation, w longest path (m)
        sampler2D _FadeDither;
        StructuredBuffer<float3> _FadePositions; // global, LimboWater.cs: [0] the player, then enemies
        int _FadePositionsCount;

        float _UltraRTTransOn;           // 1 while the plugin traced transparents this frame
        float4 _UltraRTTransCam;         // xyz camera position of that trace, w = refraction inputs bound
        float4x4 _UltraRTTransVP;        // the trace's view-projection (GL convention, not flipped)
        Texture2D _UltraRTTransRefl;     // rgb reflected radiance, a = distance to the transparent hit (< 0 none)
        float4 _UltraRTTransParams;      // x global strength, y max weight, z tone map white point (0 = off)
        StructuredBuffer<float4> _UltraRTExposureGlobal; // relight auto exposure ([0].x), bound globally when in use
        float _UltraRTAutoExposure;      // 1: apply _UltraRTExposureGlobal (as the denoiser's composite does for the lit part)
        float4 _UltraRTTonemapParams;    // global: relight tone curve parameters (UltraRTToneCurve.hlsl)
        float4 _UltraRTFog;              // x mode (0 off, 1 linear, 2 exp, 3 exp2), y start, z end, w density
        Texture2D _UltraRTPreAlpha;      // colour target after the RT composite, before transparents
        Texture2D _UltraRTGameDepth;     // the game's opaque depth (raw), copied by the RT composite
        float4 _UltraRTDepthParams;      // z > 0.5: _UltraRTGameDepth valid
        SamplerState my_point_clamp_sampler;
        SamplerState my_linear_clamp_sampler;

        float _UltraRTDisplace;          // material: the water material has VERTEX_DISPLACEMENT
        float _UltraRTOverlayDisplace;   // global (Plugin.Transparent.cs): 1 = follow it (bench trans.overlayDisplace)
        Texture2D<float4> _VertexNoiseTex;
        SamplerState sampler_VertexNoiseTex;
        float4 _FlowDirection, _UVNoiseScale, _UVNoiseSpeed;
        float _VertexMode, _ReverseFlow, _VertexNoiseScale, _VColStrength, _VertexNoiseSpeed, _VertexNoiseAmplitude;
        float _LocalOffset, _RecalculateNormals, _NormalOffsetScale, _PreventClipping;

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
            float3 wpos : TEXCOORD0;
            float3 wn : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float eye : TEXCOORD3;
        };

        v2f vert(appdata v)
        {
            v2f o;
            float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 wn = UnityObjectToWorldNormal(v.normal);
            // The game moves this surface's vertices (VERTEX_DISPLACEMENT waves): so must the overlay, or it sits at the
            // rest pose, fails the depth test where a wave rises above it and its distance no longer matches the traced
            // (displaced) copy: whole triangles of 6-2's blood pools flickered in and out of the reflection.
            [branch] if (_UltraRTDisplace > 0.5 && _UltraRTOverlayDisplace > 0.5)
            {
                DispMat m;
                m.flow = _FlowDirection.xyz; m.mode = _VertexMode; m.reverse = _ReverseFlow; m.noiseScale = _VertexNoiseScale;
                m.vcolStrength = _VColStrength; m.uvNoiseScale = _UVNoiseScale.xy; m.uvNoiseSpeed = _UVNoiseSpeed.xy;
                m.noiseSpeed = _VertexNoiseSpeed; m.amplitude = _VertexNoiseAmplitude; m.localOffset = _LocalOffset;
                m.recalc = _RecalculateNormals; m.normalOffset = _NormalOffsetScale; m.preventClip = _PreventClipping;
                float3 origin = _LocalOffset != 0.0 ? unity_ObjectToWorld._m03_m13_m23 : 0.0;
                float3 q, nq;
                DisplaceVertex(m, wpos, normalize(wn), v.color, v.uv, origin, _Time, _WorldSpaceCameraPos, _VertexNoiseTex, sampler_VertexNoiseTex, q, nq);
                wpos = q;
                wn = nq;
            }
            o.pos = mul(UNITY_MATRIX_VP, float4(wpos, 1.0));
            o.wpos = wpos;
            o.wn = wn;
            o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            o.eye = -mul(UNITY_MATRIX_V, float4(wpos, 1.0)).z; // of the displaced vertex (the game's depth has its waves)
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
            n = (_Kind > 0.5 && _Kind < 1.5) ? WaterNormal(ng, i.wpos, dist) : ng; // blood (2): thick, no ripples
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

        // The game's LIMBO_WATER_FADE alpha factor (decompiled ULTRAKILL/Master): saturate(n + (d - near) / (far - near)),
        // d = distance to the nearest fade position, n = length(rg) of the material's _DitherTexture at uv x 4. 1
        // when the material has no fade or the game set no positions.
        float LimboCoverage(v2f i)
        {
            if (_LimboFade < 0.5 || _FadePositionsCount <= 0) return 1.0;
            float dmin = 999999.0;
            int count = min(_FadePositionsCount, 256);
            for (int k = 0; k < count; k++)
            {
                float3 dv = _FadePositions[k] - i.wpos;
                dmin = min(dmin, dot(dv, dv));
            }
            float n = length(tex2Dlod(_FadeDither, float4(i.uv * 4.0, 0, 0)).rg); // the material's _DitherTexture at uv x 4
            return saturate(n + (sqrt(dmin) - _NearClip) / max(_FarClip - _NearClip, 1e-3));
        }

        // Relight's tone curve, the same function as the denoiser's composite (UltraRTToneCurve.hlsl), so reflections
        // roll off like the surfaces around them.
        float3 Tonemap(float3 c)
        {
            return saturate(UltraRTToneCurve(c, _UltraRTTransParams.z, _UltraRTTonemapParams));
        }

        // Screen-space refraction (water): (1 - a) (B(uv') - B(uv)), uv' = where the rippled refracted ray meets the
        // background, approximated by shifting the surface point along the refraction difference.
        float3 RefractionDelta(v2f i)
        {
            float3 v, ng, n;
            float dist;
            if (!Surface(i, v, dist, ng, n) || _Kind < 0.5 || _Kind > 1.5 || _UltraRTTransCam.w < 0.5 || _UltraRTDepthParams.z < 0.5 || _UltraRTWater.w <= 0.0)
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
            return d * (FogVisibility(dist) * LimboCoverage(i));
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
            Name "Absorb"
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            float4 frag(v2f i) : SV_Target
            {
                float3 v, ng, n;
                float dist;
                [branch] if (_Kind > 0.5 && _Kind < 1.5)
                {
                    // Water: Beer-Lambert absorption over the path to the opaque surface behind.
                    if (_UltraRTWaterAbsorb.x <= 0.0 || _UltraRTDepthParams.z < 0.5 || !Surface(i, v, dist, ng, n)) discard;
                    float behind = LinearEyeDepth(_UltraRTGameDepth.SampleLevel(my_point_clamp_sampler, i.pos.xy / _ScreenParams.xy, 0).r) - i.eye;
                    if (behind <= 0.0) discard;
                    // Eye depth to path length along the view ray (a grazing look crosses more water).
                    float path = min(behind * dist / max(i.eye, 1e-3), _UltraRTWaterAbsorb.w);
                    // The water's colour: its texture's average (a coarse mip) x _Color. Red "water" is 6-2's blood pools
                    // (Water 5): left alone, as are grey/white surfaces past the saturation knob.
                    float3 col = tex2Dlod(_MainTex, float4(i.uv, 0, 8)).rgb * _Color.rgb;
                    float mx = max(col.r, max(col.g, col.b));
                    if (mx <= 1e-3 || col.r > 1.5 * max(col.g, col.b)) discard;
                    float3 hue = lerp(1.0, col / mx, _UltraRTWaterAbsorb.z);
                    float a = saturate(tex2D(_MainTex, i.uv).a * _Color.a * _Opacity);
                    float3 tr = lerp(_UltraRTWaterAbsorb.y * hue, 1.0, exp(-_UltraRTWaterAbsorb.x * path));
                    float k = (1.0 - a) * FogVisibility(dist) * LimboCoverage(i);
                    return float4(1.0 - k * (1.0 - tr), 1.0);
                }
                if (_Kind < 1.5 || _Absorb <= 0.0 || !Surface(i, v, dist, ng, n)) discard;
                float4 tex = tex2D(_MainTex, i.uv) * _Color;
                float a = saturate(tex.a * _Opacity);
                // Thickness: how far the game's opaque surface is behind (thin films over a floor absorb less).
                float thick = 1.0;
                if (_UltraRTDepthParams.z > 0.5)
                {
                    float behind = LinearEyeDepth(_UltraRTGameDepth.SampleLevel(my_point_clamp_sampler, i.pos.xy / _ScreenParams.xy, 0).r) - i.eye;
                    thick = saturate(behind / 0.1);
                }
                float3 tint = tex.rgb / max(max(tex.r, max(tex.g, tex.b)), 1e-3);
                float k = (1.0 - a) * _Absorb * thick * FogVisibility(dist);
                // Toward the blood's own hue, and darker (thick blood lets little light back out).
                return float4(1.0 - k * (1.0 - tint * 0.6), 1.0);
            }
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
                // The trace already returns display (gamma) units: TraceReflection converts each sample's lighting. Until
                // 2026-11-19 this pass converted it a second time under linear transport (^(1/2.2) again), which lifted
                // a dark reflection (a night sky at 0.05 showed at 0.26). Exposure, as the denoiser's composite does
                // (UltraRTExposure.hlsl).
                if (_UltraRTAutoExposure > 0.5) refl *= _UltraRTExposureGlobal[0].x;
                refl = Tonemap(refl);

                float fres = SchlickFresnel(_F0, dot(n, v));
                float wgt = min(saturate(fres * _Weight * _UltraRTTransParams.x) * FogVisibility(dist), _UltraRTTransParams.y) * LimboCoverage(i);
                return float4(refl * wgt, 1.0 - wgt);
            }
            ENDCG
        }
    }
}
