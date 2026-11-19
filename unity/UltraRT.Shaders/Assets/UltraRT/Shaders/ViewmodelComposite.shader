// Viewmodel composite (docs/viewmodel.md): draws the ray-traced viewmodel over the game's own, on the HUD camera right
// after its opaques (the game's transparents on the gun, muzzle flashes and UI still draw on top). Fullscreen triangle
// under a scissor rect; the render target is the game's colour buffer, no depth test or write.
//
// A pixel is replaced only where this frame's G-buffer covers it (stamp) and the game's depth there matches ours, so
// anything the game drew in front of the gun (or a viewmodel part we don't draw) keeps the game's colour.
// Lighting is upsampled from half resolution with depth and normal weights (no halo across the gun's silhouette: only
// gun samples are used).
//   relight / pt-dn: (albedo * lighting * keep + specW * reflection) * exposure + emission, then relight's tone curve,
//                    the same as the denoiser's composite for world pixels.
//   pass 1:          pass 0, plus the HDR display's late gain map (docs/hdr.md, "Viewmodel"): the gun's glowing parts get
//                    HDREmissive, as glowing surfaces in the world do. Through a UAV, so pass 0 is unchanged.
Shader "UltraRT/ViewmodelComposite"
{
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off
        ColorMask RGB

        HLSLINCLUDE
            #include "UnityCG.cginc"
            #include "UltraRTMaterial.hlsl"
            #include "UltraRTToneCurve.hlsl"

            Texture2D<float4> _VMGB0, _VMGB1, _VMGB2, _VMGB3; // albedo/lit, normal/material, position-camera/stamp, emission
            Texture2D<float4> _VMLight, _VMRefl;              // filtered, half resolution
            Texture2D<float4> _VMGeo;                         // raw geo (Viewmodel.raytrace): rel xyz, w = 1 + pick
            Texture2D<float>  _VMGameDepth;                   // the game's depth buffer itself (raw; bound as a texture, the composite has no depth target)
            StructuredBuffer<float4> _VMMaterials;
            StructuredBuffer<float4> _UltraRTExposureGlobal;  // relight auto exposure, [0].x (Plugin.Exposure.cs)
            float _UltraRTAutoExposure, _UltraRTTonemapWhite, _UltraRTLinear;
            float4 _UltraRTTonemapParams;

            float  _VMStamp;
            int    _VMDebug;      // 0 off; 1 coverage, 2 lighting, 3 albedo, 4 reflection, 5 history length
            float4 _VMCamFwd;     // xyz camera forward
            float4 _VMParams;     // z depth tolerance (fraction), w blend (0..1); xy unused
            float4 _VMRefl2;      // xy SpecularTerms params (global spec weight, refl strength), z reflections on

            RWTexture2D<float4> _VMHdrGain : register(u1); // pass 1: the late gain map (rgb gain, a = the colour packed)
            float4 _VMHdrEm;      // x HDREmissive - 1, y / z where the emission's brightest channel starts / fully counts
            float4 _VMHdrTop;     // x display peak over paper white (display units), y largest gain

            static const uint2 kOrder[4] = { uint2(0, 0), uint2(1, 1), uint2(1, 0), uint2(0, 1) };

            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
                return o;
            }

            float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

            float4 frag(v2f i) : SV_Target
            {
                uint2 q = (uint2)i.pos.xy;
                float4 g2 = _VMGB2[q];
                if (g2.w != _VMStamp) discard;
                float d = dot(g2.xyz, _VMCamFwd.xyz);
                float gameD = LinearEyeDepth(_VMGameDepth[q]);
                bool match = abs(gameD - d) <= _VMParams.z * d + 0.005;
                if (_VMDebug == 1) return match ? float4(0, 1, 0, 0) : float4(1, 0, 0, 0);
                if (!match) discard;

                float4 g0 = _VMGB0[q];
                float4 g1 = _VMGB1[q];
                float3 albedo = g0.rgb;
                float lit = g0.a;
                float3 n = normalize(g1.xyz);

                // Half-res lighting: bilinear taps weighted by depth and normal similarity (gun samples only).
                uint hw, hh;
                _VMLight.GetDimensions(hw, hh);
                float2 f = ((float2)q + 0.5) * 0.5 - 0.5;
                int2 b = (int2)floor(f);
                float2 fr = f - (float2)b;
                float4 sL = 0, sR = 0;
                float ws = 0, wsR = 0, best = 0;
                int2 bestP = int2(-1, -1);
                [unroll] for (int y = -1; y <= 2; y++)
                [unroll] for (int x = -1; x <= 2; x++)
                {
                    int2 p = b + int2(x, y);
                    if (any(p < 0) || any(p >= int2(hw, hh))) continue;
                    float4 g = _VMGeo[p];
                    if (g.w < 0.5) continue;
                    float4 gn = _VMGB1[(uint2)p * 2u + kOrder[min((uint)g.w - 1u, 3u)]];
                    float wd = exp(-abs(dot(g.xyz, _VMCamFwd.xyz) - d) / (0.005 + 0.01 * d));
                    float wn = pow(saturate(dot(normalize(gn.xyz), n)), 8.0);
                    float geoW = wd * wn;
                    if (geoW > best) { best = geoW; bestP = p; }
                    bool inner = x >= 0 && x <= 1 && y >= 0 && y <= 1;
                    if (!inner) continue;
                    float bw = (x == 1 ? fr.x : 1.0 - fr.x) * (y == 1 ? fr.y : 1.0 - fr.y);
                    float wt = bw * geoW;
                    sL += _VMLight[p] * wt; ws += wt;
                    float4 r = _VMRefl[p];
                    if (r.a > 0.0) { sR += r * wt; wsR += wt; }
                }
                if (ws < 1e-4)
                {
                    // Thin parts: the most similar sample within 2 half-res pixels, or leave the game's pixel.
                    if (bestP.x < 0 || best < 1e-3) { if (_VMDebug > 1) return float4(1, 0, 1, 0); discard; } // debug: magenta = no lighting sample
                    sL = _VMLight[bestP]; ws = 1.0;
                    float4 r = _VMRefl[bestP];
                    if (r.a > 0.0) { sR = r; wsR = 1.0; }
                }
                float3 lighting = sL.rgb / ws;
                float3 refl = wsR > 0.0 ? sR.rgb / wsR : 0.0;
                bool hasRefl = wsR > 0.0 && _VMRefl2.z > 0.5;

                uint mi = (uint)round(g1.w);
                float4 m0 = _VMMaterials[mi * 2u], m1 = _VMMaterials[mi * 2u + 1u];
                SurfaceMaterial mat;
                mat.roughness = m0.x; mat.metalness = m0.y; mat.f0 = m0.z; mat.weight = m0.w;
                mat.mode = (uint)m1.x; mat.reflStrength = m1.y; mat.cls = (uint)m1.z;
                float3 v = -normalize(g2.xyz);
                float3 specW = 0;
                float keep = 1.0;
                if (hasRefl) SpecularTerms(mat, albedo, saturate(dot(n, v)), _VMRefl2.xy, specW, keep);

                if (_VMDebug == 2) return float4((_UltraRTLinear > 0.5 ? pow(max(lighting, 0.0), 1.0 / 2.2) : lighting) * 0.5, 0.0);
                if (_VMDebug == 3) return float4(albedo, 0.0);
                if (_VMDebug == 4) return float4(refl * (hasRefl ? 1.0 : 0.0), 0.0);
                if (_VMDebug == 5) return float4(saturate(sL.a / ws / 32.0).xxx, 0.0);

                float3 emission = _VMGB3[q].rgb;
                float3 l = _UltraRTLinear > 0.5 ? pow(max(lighting, 0.0), 1.0 / 2.2) : lighting; // UltraRTDisplayLighting
                l = lerp(1.0, l, lit);
                // Exposure only: the world's area colour match gain (_UltraRTExposure[3]) is left out on purpose. It
                // corrects the tint of the area's surfaces, and the gun is lit by other light (7-3's red room gave
                // (0.89, 1.24, 1.12), which turned the flashlit gun cyan; docs/viewmodel.md).
                float e = 1.0;
                if (_UltraRTAutoExposure > 0.0)
                {
                    float x = _UltraRTExposureGlobal[0].x;
                    if (x > 0.0 && !isinf(x)) e = lerp(1.0, x, lit);
                }
                float3 c = (albedo * l * keep + specW * refl) * e + emission;
                c = UltraRTToneCurve(c, _UltraRTTonemapWhite, _UltraRTTonemapParams);
                return float4(saturate(c), 0.0);
            }

            // Pass 1: frag (unchanged, so pass 0 compiles as before), then the gain of the gun's glowing parts.
            float4 fragHdr(v2f i) : SV_Target
            {
                float4 r = frag(i);
                uint2 q = (uint2)i.pos.xy;
                float3 shown = r.rgb;
                float s = UltraRTHdrEmissive(_VMGB3[q].rgb, shown, _VMHdrEm);
                float3 hc = min(shown * s, max(_VMHdrTop.x, 1.0));
                float3 sl = pow(max(shown, 0.0), 2.2), hl = pow(hc, 2.2);
                float3 g = float3(sl.r > 1e-4 ? hl.r / sl.r : 1.0, sl.g > 1e-4 ? hl.g / sl.g : 1.0, sl.b > 1e-4 ? hl.b / sl.b : 1.0);
                _VMHdrGain[q] = float4(clamp(g, 1.0, max(_VMHdrTop.y, 1.0)), UltraRTHdrPack(shown));
                return r;
            }
        ENDHLSL

        Pass // 0: replace (relight, pt-dn)
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            ENDHLSL
        }
        Pass // 1: replace + HDR late gain map (UAV u1)
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragHdr
            #pragma target 5.0
            ENDHLSL
        }
    }
}
