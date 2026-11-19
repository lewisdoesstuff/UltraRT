// Volumetric lighting composite (docs/volumetrics.md). Two fullscreen passes into the main camera's target:
//   0 Fog      right after the RT composite (before the game's transparents), blended, no copy: the game's own distance
//              fog on ray-traced pixels (the RT image replaces the game's fogged opaque shading, so the replace modes lost
//              it): exactly the game's formula, lerp(fog colour, colour, visibility) in display space, only where the
//              temporal upsampler says the pixel is ours (_UltraRTUpsampled.a > 0). The game already fogged its sky.
//   1 Scatter  after the game's transparents (CameraEvent.AfterForwardAlpha), from a copy of the target: the in-scattered
//              light, low-res history upsampled with depth weights (opaque depth), exposed like the lit RT image, added in
//              linear light: out = (in^2.2 + exposure * S)^(1/2.2), with a soft shoulder instead of a hard clip. After the
//              transparents so that grates, fences, water and glass in front of the air get its glow too (drawn before
//              them, a dark grate stood black in front of glowing air).
//   2 Beams    the beam channel (docs/volumetrics.md "Eye beams"), before pass 1, Blend One One: the game's beam meshes as
//              the game adds them (drawn with their own material into _VolBeamColor; or analytically: each beam hull's colour
//              once per face the game would draw in front of the depth), times the traced visibility (low-res lit fraction,
//              filtered).
Shader "UltraRT/VolumetricsComposite"
{
    SubShader
    {
        HLSLINCLUDE
            #include "UnityCG.cginc"

            Texture2D _VolSceneColor;          // copy of the target (screen resolution)
            Texture2D _UltraRTGameDepth;       // the game's depth (raw), set by the plugin for the RT composite
            Texture2D _UltraRTUpsampled;       // temporal upsampler output: a > 0 where the RT image replaced the game's pixel
            Texture2D<uint4> _UltraRTSurface;  // PathTrace's packed surface (trace resolution): SurfaceNoFog = the game draws it unfogged
            float _UltraRTSurfaceOn;
            Texture2D<float4> _VolHist;        // low-res accumulated in-scattering (linear, unexposed)
            Texture2D<float>  _VolDepthTex;    // low-res ray lengths
            StructuredBuffer<float4> _VolExposure; // [0].x auto exposure (AutoExposure's buffer)
            SamplerState my_point_clamp_sampler;
            float4 _VolScreen;                 // screen width, height, block size, unused
            float4 _VolLowDim;                 // low-res width, height, 1 / width, 1 / height
            float4 _VolViewRay;                // x 1 / P00, y 1 / P11, z ray length used for the sky, w eye depth from which a pixel is sky
            float4 _VolFog;                    // x mode (0 off, 1 linear, 2 exp, 3 exp2), y start, z end, w density
            float4 _VolFogColor;               // rgb the game's fog colour, w 1 = apply fog to RT pixels
            float4 _VolComposite;              // x in-scatter strength, y 1 = auto exposure, z fixed exposure, w debug (1 = S only)
            float  _VolMaxAdd;                 // the most the haze may add to a pixel (display units; soft); 0 = up to white
            float  _VolToe;                    // black-level toe (linear display units): s -> s * L / (L + toe); 0 = off
            float  _UltraRTFlipY;
            // Beam channel (pass 2): hulls as in Volumetrics.raytrace (21 float4 each), the filtered visibility (r: sum w lit,
            // g: sum w), x hull count, y shadowing (0 = none, 1 = traced), z 1 = colour from _VolBeamColor (the game's meshes), w its brightness
            StructuredBuffer<float4> _VolBeams;
            Texture2D<float4> _VolBeamHist;
            float4 _VolBeamParams;
            float4x4 _VolScreenInvViewProj;   // screen viewProj (unjittered), inverted: the raytrace's ray of a pixel
            float4 _VolCamPosC;
            Texture2D _VolBeamColor;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
                o.uv = p;
                if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
                if (_UltraRTFlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float FogVisibility(float z)
            {
                float m = _VolFog.x;
                if (m < 0.5) return 1.0;
                if (m < 1.5) return saturate((_VolFog.z - z) / max(_VolFog.z - _VolFog.y, 1e-3));
                float x = _VolFog.w * z;
                return m < 2.5 ? saturate(exp2(-x * 1.442695)) : saturate(exp2(-x * x * 1.442695));
            }

            // Upsample the low-res in-scattering at a screen pixel of radial distance d. Low-res pixel i was traced through
            // screen pixel i * block + block / 2. Each of the 4 nearest low-res values is scaled to this pixel's ray length
            // (in-scattering grows about linearly with distance) and weighted by bilinear x depth similarity.
            float3 Upsample(float2 screenPx, float d)
            {
                float blk = _VolScreen.z;
                float2 pos = (screenPx - 0.5 - floor(blk * 0.5)) / blk;
                int2 b = (int2)floor(pos);
                float2 f = pos - b;
                int2 dim = (int2)_VolLowDim.xy;
                float3 sum = 0;
                float wSum = 0;
                float3 best = 0;
                float bestErr = 1e30;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 q = clamp(b + o, 0, dim - 1);
                    float dq = _VolDepthTex.Load(int3(q, 0));
                    float3 v = _VolHist.Load(int3(q, 0)).rgb * saturate(d / max(dq, 1e-3));
                    float e = (dq - d) / (0.05 * d + 0.1);
                    float bw = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
                    float w = bw * exp(-e * e);
                    sum += v * w;
                    wSum += w;
                    float err = abs(e);
                    if (err < bestErr) { bestErr = err; best = v; }
                }
                return wSum > 1e-3 ? sum / wSum : best;
            }

            float3 PixelRay(float2 uv, out float z, out float d)
            {
                float raw = _UltraRTGameDepth.Sample(my_point_clamp_sampler, uv).r;
                z = LinearEyeDepth(raw);
                float2 ndc = uv * 2.0 - 1.0;
                d = z * length(float3(ndc.x * _VolViewRay.x, ndc.y * _VolViewRay.y, 1.0));
                return 0;
            }

            // Pass 0: Blend One SrcAlpha -> dst' = fog (1 - vis) + dst vis on our pixels, dst elsewhere.
            float4 fragFog(v2f i) : SV_Target
            {
                if (_VolFogColor.w < 0.5 || _UltraRTUpsampled.Sample(my_point_clamp_sampler, i.uv).a <= 0.0) return float4(0, 0, 0, 1);
                // "NoFog" materials (_FOG_OFF): the game leaves them unfogged (0-1's gears, 2026-11-18).
                if (_UltraRTSurfaceOn > 0.5)
                {
                    uint sw, sh;
                    _UltraRTSurface.GetDimensions(sw, sh);
                    uint4 sf = _UltraRTSurface.Load(int3(min((uint2)(i.uv * float2(sw, sh)), uint2(sw - 1, sh - 1)), 0));
                    if ((sf.z & 0x8000u) != 0u) return float4(0, 0, 0, 1);
                }
                float z, d;
                PixelRay(i.uv, z, d);
                float vis = FogVisibility(z);
                return float4(_VolFogColor.rgb * (1.0 - vis), vis);
            }

            float4 fragScatter(v2f i) : SV_Target
            {
                float3 c = _VolSceneColor.Sample(my_point_clamp_sampler, i.uv).rgb;
                float z, d;
                PixelRay(i.uv, z, d);
                float2 screenPx = i.uv * _VolScreen.xy;

                // in-scattering (at the far plane: the sky ray length the low-res pass used)
                float3 s = Upsample(screenPx, z >= _VolViewRay.w ? _VolViewRay.z : d);
                float e = _VolComposite.z;
                if (_VolComposite.y > 0.5)
                {
                    float ae = _VolExposure[0].x;
                    if (ae > 0.0 && !isinf(ae)) e *= ae;
                }
                s *= _VolComposite.x * e;
                // Toe (docs/volumetrics.md, "Dark rooms"): a faint, even veil adds little light but, over near-black pixels,
                // a lot of display brightness (gamma): fade it out; halos and shafts well above the toe keep their level.
                if (_VolToe > 0.0)
                {
                    float L = dot(s, float3(0.2126, 0.7152, 0.0722));
                    s *= L / (L + _VolToe);
                }
                if (_VolComposite.w > 0.5) return float4(pow(saturate(s), 1.0 / 2.2), 1.0);
                float3 lin = pow(saturate(c), 2.2) + s;
                float3 o = pow(lin, 1.0 / 2.2);
                // Soft shoulder on what was added: c + h (1 - exp(-delta / h)), h = room left above c, at most _VolMaxAdd
                // (docs/volumetrics.md, "Balance": in dark levels big lights and the sun lit 100 m of air into a wall of glow).
                float3 delta = max(o - c, 0.0);
                float3 h = max(1.0 - c, 1e-3);
                if (_VolMaxAdd > 0.0) h = min(h, _VolMaxAdd);
                o = c + h * (1.0 - exp(-delta / h));
                return float4(o, 1.0);
            }

            // ---- beam channel
            #define BEAM_F4 21
            bool BeamHullInterval(uint h, float3 o, float3 dir, out float t0, out float t1, out uint p0, out uint p1)
            {
                t0 = -1e30; t1 = 1e30; p0 = 0; p1 = 0;
                float4 sph = _VolBeams[h * BEAM_F4 + 1];
                float3 oc = o - sph.xyz;
                float bq = dot(oc, dir), cq = dot(oc, oc) - sph.w * sph.w;
                if (cq > 0.0 && (bq > 0.0 || bq * bq < cq)) return false;
                [loop] for (uint p = 0; p < 8; p++)
                {
                    float4 pl = _VolBeams[h * BEAM_F4 + 5 + p];
                    if (pl.w == -1.0 && dot(pl.xyz, pl.xyz) == 0.0) break;
                    float dn = dot(pl.xyz, dir);
                    float dist = dot(pl.xyz, o) - pl.w;
                    if (abs(dn) < 1e-8) { if (dist > 0.0) return false; continue; }
                    float t = -dist / dn;
                    if (dn < 0.0) { if (t > t0) { t0 = t; p0 = p; } } else if (t < t1) { t1 = t; p1 = p; }
                }
                return t1 > max(t0, 0.0);
            }

            // The game's additive weight of one face at radial distance t (eye depth t x zPerT): its distance fade
            // (DISTANCE_FADE: none within fade near, full from fade far) and, for fogged materials, the fog (additive
            // transparents fade to black).
            float BeamFace(float t, float zPerT, float fadeNear, float fadeFar, bool fog)
            {
                float z = t * zPerT;
                float w = fadeFar > fadeNear ? saturate((z - fadeNear) / (fadeFar - fadeNear)) : 1.0;
                if (fog) w *= FogVisibility(z);
                return w;
            }

            // Visibility from the low-res channel: the 4 nearest samples, bilinear x depth similarity, ratio of the sums.
            float BeamVisibilityAt(float2 screenPx, float d)
            {
                float blk = _VolScreen.z;
                float2 pos = (screenPx - 0.5 - floor(blk * 0.5)) / blk;
                int2 b = (int2)floor(pos);
                float2 f = pos - b;
                int2 dim = (int2)_VolLowDim.xy;
                float2 acc = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 q = clamp(b + o, 0, dim - 1);
                    float dq = _VolDepthTex.Load(int3(q, 0));
                    float2 v = _VolBeamHist.Load(int3(q, 0)).rg;
                    float e = (dq - d) / (0.05 * d + 0.1);
                    float bw = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
                    float w = max(bw, 1e-3) * exp(-e * e) + 1e-4 * bw; // no depth match: plain bilinear, faintly
                    acc += v * w;
                }
                return acc.y > 1e-6 ? saturate(acc.x / acc.y) : 1.0;
            }

            float4 fragBeams(v2f i) : SV_Target
            {
                int nb = clamp((int)_VolBeamParams.x, 0, 32);
                float z, d;
                PixelRay(i.uv, z, d);
                bool sky = z >= _VolViewRay.w;
                float tEnd = sky ? 1e9 : d;
                float zPerT = z / max(d, 1e-4);
                float4 farPt = mul(_VolScreenInvViewProj, float4(i.uv * 2.0 - 1.0, 1.0, 1.0));
                float3 o = _VolCamPosC.xyz;
                float3 dir = normalize(farPt.xyz / farPt.w - o);
                float3 sum = 0;
                if (_VolBeamParams.z > 0.5) { sum = _VolBeamColor.Sample(my_point_clamp_sampler, i.uv).rgb * _VolBeamParams.w; nb = 0; }
                [loop] for (int h = 0; h < nb; h++)
                {
                    float t0, t1;
                    uint p0, p1;
                    if (!BeamHullInterval(h, o, dir, t0, t1, p0, p1)) continue;
                    float4 b0 = _VolBeams[h * BEAM_F4];
                    uint flags = (uint)b0.w;
                    float fadeNear = _VolBeams[h * BEAM_F4 + 2].w, fadeFar = _VolBeams[h * BEAM_F4 + 4].x;
                    bool fog = (flags & 4u) != 0u;
                    // the faces' vertex colour brightness where the ray crosses them (0: the mesh has no face there)
                    float4 g0 = _VolBeams[h * BEAM_F4 + 13 + p0], g1 = _VolBeams[h * BEAM_F4 + 13 + p1];
                    float faces = 0.0;
                    if ((flags & 1u) != 0u && t0 > 0.0 && t0 < tEnd)
                        faces += BeamFace(t0, zPerT, fadeNear, fadeFar, fog) * saturate(g0.w + dot(g0.xyz, o + dir * t0));
                    if ((flags & 2u) != 0u && t1 < tEnd)
                        faces += BeamFace(t1, zPerT, fadeNear, fadeFar, fog) * saturate(g1.w + dot(g1.xyz, o + dir * t1));
                    sum += b0.rgb * faces;
                }
                if (!any(sum > 0.0)) return 0;
                float vis = BeamVisibilityAt(i.uv * _VolScreen.xy, sky ? _VolViewRay.z : d);
                vis = lerp(1.0, vis, saturate(_VolBeamParams.y));
                return float4(sum * vis, 0.0);
            }
        ENDHLSL

        // 0: the game's fog on ray-traced pixels (before the transparents)
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One SrcAlpha
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragFog
            #pragma target 4.5
            ENDHLSL
        }

        // 1: the in-scattered light (after the transparents, from a copy of the target)
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragScatter
            #pragma target 4.5
            ENDHLSL
        }

        // 2: the beam channel (after the transparents, before pass 1's copy): added like the game's additive beam meshes
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One One
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragBeams
            #pragma target 4.5
            ENDHLSL
        }
    }
}
