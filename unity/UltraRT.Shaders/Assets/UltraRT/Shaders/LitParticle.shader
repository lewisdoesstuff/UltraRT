// Lit particles (docs/particles.md): stands in for the game's own shader on effect materials that are drawn as unlit
// art (blood spray, dust, debris trails, splashes, drips, smoke), while a ray-traced mode is on. LitParticles.cs swaps the
// material's shader to this one and back; the material keeps all its own properties (_MainTex, _Color, _Cutoff, ...),
// and _PL* properties say which game shader is being reproduced:
//   _PLKind 0  Particles/Standard Unlit (Unity's, and ULTRAKILL's copy): tex x _Color, colour mode with the vertex colour
//          1  psx/unlit/alphatest/nocull: tex x vertex colour x _Color, alpha test on tex.a x vertex alpha (anti-aliased
//             like the game's), PS1 vertex snapping (_VertexWarping)
//          2  Sprites (ULTRAKILL/Sprites/Default, SplashSprite*): premultiplied tex x vertex colour x _Color x _RendererColor
//          3  Mobile/Particles/Alpha Blended: tex x vertex colour
//          4  ULTRAKILL/Master transparent: tex x _Color x vertex colour
// The colour is then multiplied by the ray-traced lighting at the vertex, from the particle lighting volume
// (ParticleLight.raytrace / .compute): lerp(1, lighting x exposure, _PLLit), tone mapped like relight's composite.
// Without a volume this frame (_UltraRTPLCam.w = 0: other modes, other cameras' frames before the first trace) the lighting
// is 1 and the result is the game's own look.
Shader "UltraRT/Particles/Lit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Cutoff", Float) = 0.5
        _ColorAddSubDiff ("ColorAddSubDiff", Vector) = (0,0,0,0)
        _CameraFadeParams ("CameraFadeParams", Vector) = (0,0,0,0)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        _PLKind ("Kind", Float) = 0
        _PLColorMode ("Colour mode (0 multiply, 1 overlay, 2 color, 3 add-sub-diff)", Float) = 0
        _PLAlphaTest ("Alpha test", Float) = 0
        _PLPremultiply ("Premultiply rgb by alpha", Float) = 0
        _PLFade ("Camera fading", Float) = 0
        _PLFog ("Fog (0 none, 1 fog colour, 2 to black)", Float) = 1
        _PLVertexColor ("Use vertex colour", Float) = 1
        _PLLit ("Lit amount", Float) = 1
        _PLSrcBlend ("Src blend", Float) = 5
        _PLDstBlend ("Dst blend", Float) = 10
        _PLZWrite ("ZWrite", Float) = 0
        _PLCull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Blend [_PLSrcBlend] [_PLDstBlend]
        ZWrite [_PLZWrite]
        Cull [_PLCull]
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "UltraRTToneCurve.hlsl"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Cutoff;
            float4 _ColorAddSubDiff;
            float4 _CameraFadeParams;
            float4 _RendererColor;
            float4 _Flip;
            float _PLKind, _PLColorMode, _PLAlphaTest, _PLPremultiply, _PLFade, _PLFog, _PLVertexColor, _PLLit;
            float _VertexWarping, _VertexWarpScale; // psx vertex snapping (the game's own values, global or per material)

            // The particle lighting volume (Plugin.Particles.cs, globals).
            Texture3D _UltraRTPL;
            SamplerState my_linear_clamp_sampler;
            float4x4 _UltraRTPLViewProj;   // the volume's view-projection (GL clip space)
            float4 _UltraRTPLCam;          // xyz camera position, w 1 = volume valid
            float4 _UltraRTPLFwd;          // xyz camera forward, w lit strength (global multiplier on _PLLit)
            float4 _UltraRTPLDepth;        // x near, y log2(far / near), z 1 / log2(far / near), w 1 = lighting is linear
            float4 _UltraRTPLFogColor;     // rgb the level's fog colour (display), w 1 = fog on
            float4 _UltraRTPLFogParams;    // x start, y end (linear fog, as the game's shaders use)
            StructuredBuffer<float4> _UltraRTExposureGlobal; // relight auto exposure ([0].x), bound globally when in use
            float _UltraRTAutoExposure;
            float _UltraRTTonemapWhite;
            float4 _UltraRTTonemapParams;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 light : TEXCOORD1;  // lighting multiplier (display, exposed); 1 = the game's look
                float2 fogFade : TEXCOORD2; // x fog factor (1 = no fog), y camera fade
            };

            float3 VolumeLighting(float3 wpos)
            {
                if (_UltraRTPLCam.w < 0.5) return 1.0;
                float4 clip = mul(_UltraRTPLViewProj, float4(wpos, 1.0));
                float d = dot(wpos - _UltraRTPLCam.xyz, _UltraRTPLFwd.xyz);
                if (clip.w <= 1e-4 || d <= 0.0) return 1.0;
                float3 uvw;
                uvw.xy = clip.xy / clip.w * 0.5 + 0.5;
                uvw.z = log2(max(d, _UltraRTPLDepth.x) / _UltraRTPLDepth.x) * _UltraRTPLDepth.z;
                float3 l = _UltraRTPL.SampleLevel(my_linear_clamp_sampler, saturate(uvw), 0).rgb;
                if (_UltraRTPLDepth.w > 0.5) l = pow(max(l, 0.0), 1.0 / 2.2); // linear transport -> the game's units
                if (_UltraRTAutoExposure > 0.5)
                {
                    float e = _UltraRTExposureGlobal[0].x;
                    if (e > 0.0 && !isinf(e)) l *= e;
                }
                return lerp(1.0, l, saturate(_PLLit * _UltraRTPLFwd.w));
            }

            v2f vert(appdata v)
            {
                v2f o;
                float4 vertex = v.vertex;
                if (_PLKind > 1.5 && _PLKind < 2.5) vertex.xy *= _Flip.xy; // sprites
                float3 wpos = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0)).xyz;
                o.pos = UnityWorldToClipPos(wpos);
                if (_PLKind > 0.5 && _PLKind < 1.5 && _VertexWarping != 0.0)
                {
                    // psx: snap the projected vertex to a coarse grid (the game's formula)
                    float2 res = _ScreenParams.xy / max(_ScreenParams.x, _ScreenParams.y);
                    float dist = distance(wpos, _WorldSpaceCameraPos);
                    float snap = (exp2(log2(max(dist - 200.0, 0.0)) * 0.2) * 0.1 + _VertexWarpScale) * _VertexWarping;
                    float2 grid = res * snap;
                    float3 ndc = o.pos.xyz / o.pos.w;
                    ndc.xy = (floor(grid * ndc.xy) + 0.5) / grid;
                    o.pos.xyz = ndc * o.pos.w;
                }
                o.uv = TRANSFORM_TEX(v.uv.xy, _MainTex);
                float4 c = _PLVertexColor > 0.5 ? v.color : 1.0;
                if (_PLKind > 1.5 && _PLKind < 2.5) c *= _Color * _RendererColor;
                else if (_PLKind > 3.5) c *= _Color;
                else if (_PLKind > 0.5 && _PLKind < 1.5) c.rgb *= _Color.rgb;
                o.color = c;
                o.light = VolumeLighting(wpos);
                float dist = distance(wpos, _WorldSpaceCameraPos);
                float fog = 1.0;
                if (_PLFog > 0.5 && _UltraRTPLFogColor.w > 0.5)
                    fog = saturate((_UltraRTPLFogParams.y - dist) / max(_UltraRTPLFogParams.y - _UltraRTPLFogParams.x, 1e-3));
                float eye = -UnityObjectToViewPos(vertex.xyz).z;
                float fade = _PLFade > 0.5 ? saturate((eye - _CameraFadeParams.x) * _CameraFadeParams.y) : 1.0;
                o.fogFade = float2(fog, fade);
                return o;
            }

            float3 RgbToHsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }
            float3 HsvToRgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 tex = tex2D(_MainTex, i.uv);
                float4 c;
                if (_PLKind < 0.5)
                {
                    // Unity's Particles/Standard Unlit: albedo = tex * _Color, then the colour mode with the vertex colour.
                    c = tex * _Color;
                    if (_PLColorMode > 2.5)
                    {
                        c.rgb = c.rgb + i.color.rgb * _ColorAddSubDiff.x;
                        c.rgb = lerp(c.rgb, abs(c.rgb), _ColorAddSubDiff.y);
                        c.a *= i.color.a;
                    }
                    else if (_PLColorMode > 1.5)
                    {
                        float3 a = RgbToHsv(c.rgb), b = RgbToHsv(i.color.rgb);
                        c = float4(HsvToRgb(float3(b.x, b.y, a.z)), c.a * i.color.a);
                    }
                    else if (_PLColorMode > 0.5)
                    {
                        c.rgb = lerp(1.0 - 2.0 * (1.0 - c.rgb) * (1.0 - i.color.rgb), 2.0 * c.rgb * i.color.rgb, step(c.rgb, 0.5));
                        c.a *= i.color.a;
                    }
                    else c *= i.color;
                }
                else c = tex * i.color;

                if (_PLAlphaTest > 0.5)
                {
                    if (_PLKind > 0.5 && _PLKind < 1.5)
                    {
                        // psx: anti-aliased cutoff (the game's: (a - cutoff) / fwidth(uv) + 0.5 < 0 discards)
                        float2 fw = max(abs(ddx(i.uv)) + abs(ddy(i.uv)), 1e-4);
                        float2 t = (c.a - min(_Cutoff, 0.999)) / fw + 0.5;
                        if (t.x < 0.0 || t.y < 0.0) discard;
                    }
                    else clip(c.a - _Cutoff);
                }
                c.a *= i.fogFade.y;

                float3 lit = c.rgb * i.light;
                if (_UltraRTPLCam.w > 0.5) lit = UltraRTToneCurve(lit, _UltraRTTonemapWhite, _UltraRTTonemapParams);
                if (_PLFog > 1.5) lit *= i.fogFade.x;                                          // additive: fade out
                else if (_PLFog > 0.5) lit = lerp(_UltraRTPLFogColor.rgb, lit, i.fogFade.x);    // blended: fog colour
                if (_PLPremultiply > 0.5) lit *= c.a;
                return float4(lit, c.a);
            }
            ENDCG
        }
    }
}
