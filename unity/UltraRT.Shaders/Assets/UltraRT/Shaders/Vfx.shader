// Screen-space effects (docs/vfx.md), drawn after the game's transparents (CameraEvent.AfterForwardAlpha, after the
// volumetric scatter), from a copy of the camera target. Mode-agnostic: everything works from the composited image, the
// game's depth and (bloom only) an optional HDR source the RT composite writes.
//   0 BloomPrefilter  half res, linear light: what the game's transparents added since the RT composite (fire, flashes,
//                     explosions, beams: late - pre, where the result is bright) + the HDR source (energy the tone curve
//                     compressed, bright emission), written by Denoise.compute's Composite at trace resolution.
//   1 BloomDown       2x2 box with a 4-tap bilinear footprint (13 texels wide), one per mip.
//   2 BloomUp         tent upsample of the next smaller mip, added (Blend One One) to this mip.
//   3 Final           the camera target: scene sampled through the distortion field (heat haze over lava and fire,
//                     explosion shockwaves, underwater wobble), underwater fog / shafts / caustics, then bloom added in
//                     linear light with a soft shoulder.
//   4 FinalHdr        pass 3, plus the HDR display's late gain map (docs/hdr.md, "Effects and bloom"): per pixel, how much
//                     brighter than the SDR result an HDR display shows it. Bright pixels the game's transparents made
//                     (fire, explosions, beams, coins) get HDREffects; glow the SDR shoulder squeezed under white gets
//                     HDRBloom more room. Written through a UAV, so the SDR colour target and pass 3 stay as they were.
//   5 FinalNative     pass 3 for native float HDR (docs/hdr.md, "Native float HDR"): the target is ARGBHalf and nothing
//                     clips at 1, so what the game's transparents added (late - pre) is exact; its top range is expanded by
//                     HDREffects (display units) in the target itself, and bloom adds in linear light (no shoulder).
// Distortion is anchored in the world: the noise is evaluated at the point where the view ray crosses the hot air, the
// amplitude is a world-space length projected to the screen, and a displaced sample is rejected when it lands on
// something in front of the hot air (so foreground edges never smear).
Shader "UltraRT/Vfx"
{
    SubShader
    {
        HLSLINCLUDE
            #include "UnityCG.cginc"
            #include "UltraRTToneCurve.hlsl"
            #include "UltraRTDither.hlsl"

            Texture2D _VfxScene;               // copy of the target after the transparents (screen resolution)
            Texture2D _VfxPre;                 // copy of the target right after the RT composite (before the transparents)
            Texture2D _VfxLate;                // the target after the transparents, before the volumetric haze (or _VfxScene)
            Texture2D _VfxHdr;                 // HDR bloom source, linear light, trace resolution (may have a guard band)
            Texture2D _VfxSrc;                 // bloom chain input
            Texture2D _VfxBloom;               // bloom result (mip 0, half resolution)
            Texture2D _VfxNoise;               // tileable noise, rg independent, repeat + bilinear
            Texture2D _UltraRTGameDepth;       // the game's opaque depth (raw)
            SamplerState my_point_clamp_sampler;
            SamplerState my_linear_clamp_sampler;
            SamplerState my_linear_repeat_sampler;
            float4 _VfxSrcTexel;               // xy 1 / size of _VfxSrc, z upsample weight
            float4 _VfxBloomParams;            // x strength, y late-delta weight, z delta threshold lo, w delta threshold hi
            float4 _VfxBloomParams2;           // x 1 = HDR source valid, y clamp, z soft maximum the bloom adds (display), w how much bright pixels are spared
            float4 _VfxHdrScale;               // xy visible size / traced size of the HDR source (guard band)
            float4 _VfxCamPos;                 // xyz camera position, w time (s)
            float4 _VfxRayX, _VfxRayY, _VfxRayZ; // view ray = Z + X ndc.x + Y ndc.y (X = right / P00, Y = up / P11)
            float4x4 _VfxViewProj;             // world -> clip (GL convention, y up)
            float4 _VfxProj;                   // x P00, y P11, z view-plane size of a pixel at 1 m
            float4 _VfxCounts;                 // x heat boxes, y heat points, z shockwaves, w 1 = any distortion
            float4 _VfxHeatBoxMin[8];          // xyz, w strength
            float4 _VfxHeatBoxMax[8];
            float4 _VfxHeatPoints[16];         // xyz centre of the hot air, w radius
            float4 _VfxHeat;                   // x amplitude (fraction of the screen height), y noise frequency (1/m), z rise speed (m/s), w max offset (uv)
            float4 _VfxShock[8];               // xyz centre, w radius now (m)
            float4 _VfxShockP[8];              // x amplitude (m), y thickness (m)
            float4 _VfxWater;                  // x 1 = camera under water, y surface height, z wobble (uv), w fog density (1/m)
            float4 _VfxWaterColor;             // rgb water colour (display), w fog darkening per metre of depth
            float4 _VfxWaterLight;             // xyz direction to the light above the surface (unit), w shaft strength
            float4 _VfxWaterParams;            // x caustics strength, y shaft distance (m), z 1 / shaft scale (1/m), w unused
            float4 _VfxWaterFog;               // x fog colour level (x the water colour), y share of it left at depth, zw unused
            float  _UltraRTFlipY;
            float  _VfxDebug;                  // 1 bloom only, 2 distortion field
            RWTexture2D<float4> _VfxHdrGain : register(u1); // pass 4: rgb gain (linear, >= 1), a = the SDR colour packed
            float4 _VfxHdrFx;                  // x effects: gain - 1 in linear light, y / z brightest channel where it starts / is full, w bloom room (display units)
            float4 _VfxHdrTop;                 // x display peak over paper white (display units), y largest gain
                                               // (pass 5: _VfxHdrFx.x = HDREffects - 1 in display units; _VfxHdrTop.x the same peak)

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

            float MaxC(float3 c) { return max(c.r, max(c.g, c.b)); }
            float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
            float3 ToLinear(float3 c) { return pow(max(c, 0.0), 2.2); }
            // HDR late map: how much of a pixel's light the game's transparents made (docs/hdr.md "Effects and bloom"). The share of
            // the light they added (luminance, linear). Where the image under the effect was near white the effect clipped,
            // and that share says nothing of its strength: 0-3's light pillars crossing the hot wall band got a third of the
            // gain they got elsewhere and read see-through in HDR (2026-11-20, "Effects over bright surfaces"). There the
            // share of the room left above the pixel that the effect filled counts, in the channel where it filled most (a beam
            // fills most of it; a faint glow adds a step or two and gets little).
            float EffectShare(float3 late, float3 pre)
            {
                float ll = Luma(ToLinear(late));
                float share = saturate((ll - Luma(ToLinear(pre))) / max(ll, 1e-3));
                float3 d = late - pre;
                float3 fc = saturate(d / max(1.0 - pre, 1e-3)) * smoothstep(0.01, 0.04, d) * step(0.02, 1.0 - pre);
                return max(share, max(fc.r, max(fc.g, fc.b)) * smoothstep(0.5, 0.85, MaxC(pre)));
            }
            float EffectShareAt(float2 uv)
            {
                return EffectShare(_VfxLate.SampleLevel(my_point_clamp_sampler, uv, 0).rgb, _VfxPre.SampleLevel(my_point_clamp_sampler, uv, 0).rgb);
            }
            float3 ToDisplay(float3 c) { return pow(max(c, 0.0), 1.0 / 2.2); }

            // ---------------------------------------------------------------- bloom

            float4 fragPrefilter(v2f i) : SV_Target
            {
                // Before the haze: the in-scattering is not a transparent and must not bloom (3-2's lit air bloomed into a veil).
                float3 late = _VfxLate.SampleLevel(my_linear_clamp_sampler, i.uv, 0).rgb;
                float3 pre = _VfxPre.SampleLevel(my_linear_clamp_sampler, i.uv, 0).rgb;
                // What the transparents added, where the result is bright: additive glows saturate, smoke and dust don't.
                // The amount is the luminance they added; the colour is the pixel's own (a channel that was already
                // saturated has no difference left, which would turn the glow of lava green).
                float3 lateLin = ToLinear(late);
                float added = max(Luma(lateLin) - Luma(ToLinear(pre)), 0.0);
                float3 delta = lateLin * (added / max(Luma(lateLin), 1e-3));
                delta *= smoothstep(_VfxBloomParams.z, _VfxBloomParams.w, MaxC(late)) * _VfxBloomParams.y;
                float3 hdr = 0;
                if (_VfxBloomParams2.x > 0.5)
                {
                    float2 tuv = (i.uv - 0.5) * _VfxHdrScale.xy + 0.5;
                    hdr = _VfxHdr.SampleLevel(my_linear_clamp_sampler, tuv, 0).rgb;
                }
                else
                {
                    // No HDR source (DLSS RR): the brightest part of the displayed RT image stands in.
                    hdr = ToLinear(pre) * smoothstep(0.8, 1.0, MaxC(pre)) * 0.5;
                }
                float3 c = delta + hdr;
                if (any(isnan(c)) || any(isinf(c))) c = 0;
                // Karis-style weight: one very hot texel can't dominate the first downsample (no fireflies in the glow).
                float m = MaxC(c);
                c *= min(1.0, _VfxBloomParams2.y / max(m, 1e-4));
                return float4(c, 1.0);
            }

            float4 fragDown(v2f i) : SV_Target
            {
                float2 t = _VfxSrcTexel.xy;
                float3 c = _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2(-1.0, -1.0), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2( 1.0, -1.0), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2(-1.0,  1.0), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2( 1.0,  1.0), 0).rgb;
                return float4(c * 0.25, 1.0);
            }

            float4 fragUp(v2f i) : SV_Target
            {
                float2 t = _VfxSrcTexel.xy;
                float3 c = _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2(-0.5, -0.5), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2( 0.5, -0.5), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2(-0.5,  0.5), 0).rgb
                         + _VfxSrc.SampleLevel(my_linear_clamp_sampler, i.uv + t * float2( 0.5,  0.5), 0).rgb;
                return float4(c * (0.25 * _VfxSrcTexel.z), 1.0); // z: weight of the wider level being added
            }

            // ---------------------------------------------------------------- distortion

            float SceneDistance(float2 uv, out float3 rd)
            {
                float z = LinearEyeDepth(_UltraRTGameDepth.SampleLevel(my_point_clamp_sampler, uv, 0).r);
                float2 ndc = uv * 2.0 - 1.0;
                float3 v = _VfxRayZ.xyz + _VfxRayX.xyz * ndc.x + _VfxRayY.xyz * ndc.y;
                float len = length(v);
                rd = v / len;
                return z * len;
            }

            // World-anchored, rising noise (two octaves), zero mean.
            // lod: mip of the first octave at which one texel covers the pixel's footprint (the pattern averages out
            // towards zero beyond it instead of turning into moire on surfaces seen at a grazing angle).
            float2 HeatNoise(float3 p, float lod)
            {
                float t = _VfxCamPos.w;
                float f = _VfxHeat.y;
                float2 a = _VfxNoise.SampleLevel(my_linear_repeat_sampler, float2(p.x + p.z * 0.71, p.y - t * _VfxHeat.z) * f, lod).rg;
                float2 b = _VfxNoise.SampleLevel(my_linear_repeat_sampler, float2(p.z - p.x * 0.63, p.y - t * _VfxHeat.z * 1.7) * f * 2.3 + 0.37, lod + 1.2).gr;
                return (a - 0.5) * 2.0 + (b - 0.5);
            }

            // xy: uv offset. z: distance along the ray at which the distorting air starts (for the depth rejection).
            float3 Distortion(float2 uv, float3 ro, float3 rd, float dist)
            {
                float2 offset = 0;
                float tNear = 1e9;
                int nb = (int)_VfxCounts.x, np = (int)_VfxCounts.y, ns = (int)_VfxCounts.z;
                float wSum = 0;
                float3 pSum = 0;
                float3 inv = 1.0 / (abs(rd) > 1e-5 ? rd : 1e-5);
                for (int b = 0; b < nb; b++)
                {
                    float3 t0 = (_VfxHeatBoxMin[b].xyz - ro) * inv, t1 = (_VfxHeatBoxMax[b].xyz - ro) * inv;
                    float3 lo = min(t0, t1), hi = max(t0, t1);
                    float ta = max(max(lo.x, lo.y), max(lo.z, 0.0)), tb = min(min(hi.x, hi.y), min(hi.z, dist));
                    if (tb <= ta) continue;
                    float3 pm = ro + rd * (0.5 * (ta + tb));
                    // strongest just above the surface, gone at the top of the column
                    float h = saturate((pm.y - _VfxHeatBoxMin[b].y) / max(_VfxHeatBoxMax[b].y - _VfxHeatBoxMin[b].y, 1e-3));
                    float w = saturate((tb - ta) * 0.2) * (1.0 - h) * _VfxHeatBoxMin[b].w;
                    // fade near the sides so the column has no hard edge
                    float2 e = min(pm.xz - _VfxHeatBoxMin[b].xz, _VfxHeatBoxMax[b].xz - pm.xz);
                    w *= saturate(min(e.x, e.y) * 2.0 + 0.25);
                    wSum += w;
                    pSum += pm * w;
                    if (w > 0.0) tNear = min(tNear, ta);
                }
                for (int k = 0; k < np; k++)
                {
                    float4 s = _VfxHeatPoints[k];
                    float3 oc = s.xyz - ro;
                    float tc = dot(oc, rd);
                    float d2 = dot(oc, oc) - tc * tc;
                    float r2 = s.w * s.w;
                    if (d2 >= r2 || tc <= 0.0) continue;
                    float half = sqrt(r2 - d2);
                    float ta = max(tc - half, 0.0), tb = min(tc + half, dist);
                    if (tb <= ta) continue;
                    float w = saturate((tb - ta) / s.w) * (1.0 - d2 / r2) * 0.75;
                    float3 pm = ro + rd * (0.5 * (ta + tb));
                    wSum += w;
                    pSum += pm * w;
                    tNear = min(tNear, ta);
                }
                if (wSum > 1e-4)
                {
                    float3 pm = pSum / wSum;
                    // Hot air bends light by an angle: the shimmer has the same size on screen at any distance (a little
                    // less far away), while its pattern, anchored in the world, gets finer.
                    float z = max(dot(pm - ro, _VfxRayZ.xyz), 0.5);
                    // footprint of 4 pixels (the mid point moves several times faster than the surface point at grazing angles)
                    float2 n = HeatNoise(pm, log2(max(z * _VfxProj.z * 4.0 * _VfxHeat.y * 128.0, 1.0)));
                    float2 o = n * (min(wSum, 1.5) * _VfxHeat.x / (1.0 + z * 0.02)) * float2(_VfxProj.x / _VfxProj.y, 1.0);
                    float m = length(o);
                    if (m > _VfxHeat.w) o *= _VfxHeat.w / m;
                    offset += o;
                }
                for (int s2 = 0; s2 < ns; s2++)
                {
                    float3 c = _VfxShock[s2].xyz;
                    float r = _VfxShock[s2].w;
                    float tc = clamp(dot(c - ro, rd), 0.0, dist);
                    float3 q = ro + rd * tc;
                    float x = (length(q - c) - r) / max(_VfxShockP[s2].y, 1e-3);
                    if (abs(x) > 3.0) continue;
                    float ring = x * exp(-x * x) * 2.33; // odd profile: pushes out ahead of the front, pulls in behind it
                    // radial direction on screen, in view-plane units
                    float4 cc = mul(_VfxViewProj, float4(c, 1.0));
                    float2 cuv = cc.w > 0.05 ? cc.xy / cc.w * 0.5 + 0.5 : 0.5;
                    float2 dv = (uv - cuv) / _VfxProj.xy;
                    float dl = length(dv);
                    if (dl < 1e-5) continue;
                    float z = max(dot(q - ro, _VfxRayZ.xyz), 1.0);
                    offset += dv / dl * (ring * _VfxShockP[s2].x * 0.5 / z) * _VfxProj.xy;
                    tNear = min(tNear, max(tc - r, 0.0));
                }
                return float3(offset, tNear);
            }

            // ---------------------------------------------------------------- underwater

            // Ridged pattern of two drifting noise layers: bright thin lines, like light focused by a rippled surface.
            float Caustic(float2 xz)
            {
                float t = _VfxCamPos.w;
                float s = _VfxWaterParams.z;
                float a = _VfxNoise.SampleLevel(my_linear_repeat_sampler, xz * s + float2(t * 0.021, t * 0.013), 0).r;
                float b = _VfxNoise.SampleLevel(my_linear_repeat_sampler, xz * s * 1.37 - float2(t * 0.017, -t * 0.019) + 0.5, 0).g;
                // Sparse: thin bright lines on a dark field (about a tenth of the area), so beams marched through it stay
                // separate shafts instead of adding up to a veil.
                float r = saturate(1.0 - abs(a + b - 1.0) * 3.5);
                r *= r;
                return r * r * r;
            }

            float2 SurfacePoint(float3 p)
            {
                float3 l = _VfxWaterLight.xyz;
                return p.xz + l.xz * ((_VfxWater.y - p.y) / max(l.y, 0.2));
            }

            float Ign(float2 px)
            {
                return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
            }

            float3 Underwater(float3 c, float2 uv, float2 px, float3 ro, float3 rd, float dist)
            {
                float depthCam = max(_VfxWater.y - ro.y, 0.0);
                float3 hit = ro + rd * dist;
                float sigma = _VfxWater.w;
                // Caustics on what the camera looks at (below the surface only), fading with depth.
                float below = _VfxWater.y - hit.y;
                if (below > 0.0 && dist < 60.0)
                    // (less on surfaces that are already bright: the lines must not clip to white)
                    c *= 1.0 + _VfxWaterParams.x * (Caustic(SurfacePoint(hit)) * 2.0 - 0.1) * exp(-below * 0.12) * exp(-dist * sigma) * (1.0 - 0.8 * saturate(MaxC(c)));
                // Shafts: the same pattern extruded along the light direction, marched through the water in front of the surface.
                float shaft = 0;
                float range = min(dist, _VfxWaterParams.y);
                if (_VfxWaterLight.w > 0.0 && range > 0.5)
                {
                    const int N = 6;
                    float dt = range / N;
                    float j = Ign(px);
                    for (int s = 0; s < N; s++)
                    {
                        float t = (s + j) * dt;
                        float3 p = ro + rd * t;
                        float d = _VfxWater.y - p.y;
                        if (d <= 0.0) continue;
                        shaft += Caustic(SurfacePoint(p)) * exp(-d * 0.18 - t * sigma * 1.5) * dt;
                    }
                    shaft *= _VfxWaterLight.w / max(_VfxWaterParams.y, 1.0);
                }
                // Fog: distance fades into the water colour, which darkens with the camera's depth down to a floor
                // (_VfxWaterFog.y of it). Until 2026-11-21 the fog went to near black (water colour x 0.02-0.14) and
                // darkened at 0.12 /m: 87 m down in 5-1's flooded cave every wall 30 m away was half black, where the
                // game's own underwater view (and the cave seen through the glass) is a clear cyan (docs/vfx.md, "Underwater").
                float dark = exp(-depthCam * _VfxWaterColor.w);
                float3 fogC = _VfxWaterColor.rgb * _VfxWaterFog.x * lerp(_VfxWaterFog.y, 1.0, dark);
                float vis = exp(-min(dist, 400.0) * sigma);
                c = lerp(fogC, c, vis);
                c += (_VfxWaterColor.rgb * 0.5 + 0.5) * shaft * dark;
                return c;
            }

            float4 fragFinal(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float3 ro = _VfxCamPos.xyz;
                float3 rd = _VfxRayZ.xyz;
                float dist = 0;
                bool water = _VfxWater.x > 0.5;
                bool warp = _VfxCounts.w > 0.5;
                float2 offset = 0;
                if (warp || water)
                {
                    dist = SceneDistance(uv, rd);
                    if (warp)
                    {
                        float3 d = Distortion(uv, ro, rd, dist);
                        offset = d.xy;
                        if (dot(offset, offset) > 0.0)
                        {
                            // Reject a displaced sample that lands on something in front of the distorting air.
                            float3 rd2;
                            float dist2 = SceneDistance(uv + offset, rd2);
                            if (dist2 < d.z - 0.05) offset = 0;
                        }
                    }
                    if (water)
                    {
                        float t = _VfxCamPos.w;
                        float2 wob = float2(sin(uv.y * 31.0 + t * 2.3) + sin(uv.y * 13.0 - t * 1.1), cos(uv.x * 27.0 + t * 1.9) + cos(uv.x * 11.0 - t * 1.3));
                        offset += wob * _VfxWater.z * saturate(dist * 0.25);
                    }
                }
                if (_VfxDebug > 1.5) return float4(0.5 + offset * 100.0, 0.0, 1.0);
                float3 c;
                if (dot(offset, offset) > 0.0)
                {
                    // Point sampled: displaced pixels stay whole pixels (bilinear resampling blurred everything a shock
                    // front or the water's wobble touched).
                    c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb;
                    if (water) dist = SceneDistance(uv + offset, rd);
                }
                else c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv, 0).rgb;
                if (water) c = Underwater(c, uv, i.pos.xy, ro, rd, dist);

                if (_VfxBloomParams.x > 0.0)
                {
                    float3 b = _VfxBloom.SampleLevel(my_linear_clamp_sampler, uv + offset, 0).rgb * _VfxBloomParams.x;
                    if (_VfxDebug > 0.5) return float4(ToDisplay(b), 1.0);
                    // Less of it on pixels that are already bright: the glow goes around the light, the art stays as drawn.
                    b *= 1.0 - _VfxBloomParams2.w * smoothstep(0.35, 0.95, MaxC(c));
                    float3 o = ToDisplay(ToLinear(saturate(c)) + b);
                    // Soft shoulder on what was added (as the volumetric scatter does): the glow saturates smoothly towards
                    // white and never adds more than the soft maximum, so it can't wash out the art around it.
                    float3 delta = max(o - c, 0.0);
                    float3 h = max(1.0 - c, 1e-3);
                    if (_VfxBloomParams2.z > 0.0) h = min(h, _VfxBloomParams2.z);
                    c = c + h * (1.0 - exp(-delta / h));
                }
                return float4(UltraRTOutputDither(c, i.pos.xy, 2.0), 1.0); // a no-op on pixels nothing changed (already 8-bit)
            }

            // Pass 4: a copy of fragFinal (keep the two in step) that also writes the HDR late gain map. fragFinal itself
            // stays as it was, so the SDR image compiles to the same code with HDR off.
            float4 fragFinalHdr(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float3 ro = _VfxCamPos.xyz;
                float3 rd = _VfxRayZ.xyz;
                float dist = 0;
                bool water = _VfxWater.x > 0.5;
                bool warp = _VfxCounts.w > 0.5;
                float2 offset = 0;
                if (warp || water)
                {
                    dist = SceneDistance(uv, rd);
                    if (warp)
                    {
                        float3 d = Distortion(uv, ro, rd, dist);
                        offset = d.xy;
                        if (dot(offset, offset) > 0.0)
                        {
                            // Reject a displaced sample that lands on something in front of the distorting air.
                            float3 rd2;
                            float dist2 = SceneDistance(uv + offset, rd2);
                            if (dist2 < d.z - 0.05) offset = 0;
                        }
                    }
                    if (water)
                    {
                        float t = _VfxCamPos.w;
                        float2 wob = float2(sin(uv.y * 31.0 + t * 2.3) + sin(uv.y * 13.0 - t * 1.1), cos(uv.x * 27.0 + t * 1.9) + cos(uv.x * 11.0 - t * 1.3));
                        offset += wob * _VfxWater.z * saturate(dist * 0.25);
                    }
                }
                if (_VfxDebug > 1.5)
                {
                    _VfxHdrGain[uint2(i.pos.xy)] = float4(1.0, 1.0, 1.0, 0.0);
                    return float4(0.5 + offset * 100.0, 0.0, 1.0);
                }
                float3 c;
                if (dot(offset, offset) > 0.0)
                {
                    // Point sampled: displaced pixels stay whole pixels (bilinear resampling blurred everything a shock
                    // front or the water's wobble touched).
                    c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb;
                    if (water) dist = SceneDistance(uv + offset, rd);
                }
                else c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv, 0).rgb;
                if (water) c = Underwater(c, uv, i.pos.xy, ro, rd, dist);

                float3 gain = 1.0;
                if (_VfxHdrFx.x > 0.0)
                {
                    // Effects: what the game's transparents added since the RT composite, where the result is bright. The SDR
                    // image clips them at white; an HDR display shows them HDREffects brighter (in display gamma). The share
                    // of the pixel's light the transparents added keeps a dim glow over a bright wall from lifting the wall.
                    float3 late = _VfxLate.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb;
                    float3 pre = _VfxPre.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb;
                    float share = EffectShare(late, pre);
                    if (share < 0.6 && MaxC(pre) > 0.85 && MaxC(late) > 0.9)
                    {
                        // Blind pixels: under the effect the image was so near white in the channels the effect adds to that
                        // nothing of it shows (a yellow beam over a white-hot strip changes no channel). With the effect on
                        // both sides of the pixel along some axis, it is inside the effect: it takes the weaker side's share.
                        // A wall pixel beside a beam has it on one side only and keeps its own. 3 radii x 4 axes.
                        float w, h;
                        _VfxLate.GetDimensions(w, h);
                        float best = 0.0;
                        [unroll] for (int ri = 0; ri < 3; ri++)
                        {
                            float r = h * (ri == 0 ? 0.008 : (ri == 1 ? 0.022 : 0.05));
                            [unroll] for (int ai = 0; ai < 4; ai++)
                            {
                                float2 dir = ai == 0 ? float2(1.0, 0.0) : (ai == 1 ? float2(0.0, 1.0) : (ai == 2 ? float2(0.7071, 0.7071) : float2(0.7071, -0.7071)));
                                float2 o = dir * r / float2(w, h);
                                best = max(best, min(EffectShareAt(uv + offset + o), EffectShareAt(uv + offset - o)));
                            }
                        }
                        share = max(share, best);
                    }
                    float t = smoothstep(_VfxHdrFx.y, _VfxHdrFx.z, MaxC(late));
                    gain = 1.0 + _VfxHdrFx.x * share * t * t;
                }

                if (_VfxBloomParams.x > 0.0)
                {
                    float3 b = _VfxBloom.SampleLevel(my_linear_clamp_sampler, uv + offset, 0).rgb * _VfxBloomParams.x;
                    if (_VfxDebug > 0.5)
                    {
                        _VfxHdrGain[uint2(i.pos.xy)] = float4(1.0, 1.0, 1.0, 0.0);
                        return float4(ToDisplay(b), 1.0);
                    }
                    // Less of it on pixels that are already bright: the glow goes around the light, the art stays as drawn.
                    b *= 1.0 - _VfxBloomParams2.w * smoothstep(0.35, 0.95, MaxC(c));
                    float3 o = ToDisplay(ToLinear(saturate(c)) + b);
                    // Soft shoulder on what was added (as the volumetric scatter does): the glow saturates smoothly towards
                    // white and never adds more than the soft maximum, so it can't wash out the art around it.
                    float3 delta = max(o - c, 0.0);
                    float3 h = max(1.0 - c, 1e-3);
                    if (_VfxBloomParams2.z > 0.0) h = min(h, _VfxBloomParams2.z);
                    float3 c0 = c;
                    c = c + h * (1.0 - exp(-delta / h));
                    if (_VfxHdrFx.w > 0.0)
                    {
                        // Bloom: the same shoulder with more room (HDRBloom, up to the peak), so glow the SDR image squeezed
                        // under white goes above it. Only where the SDR result is near white (below that it is the SDR
                        // glow, as the composite's gain is 1 below the knee) and the glow itself is strong: a faint glow over
                        // a brightly lit floor lifted the whole floor (and the HUD panel over it) in the first version.
                        float3 h2 = max(h, min(max(_VfxHdrTop.x - c0, 1e-3), _VfxHdrFx.w));
                        float3 ch = c0 + h2 * (1.0 - exp(-delta / h2));
                        float3 sl = ToLinear(c), hl = ToLinear(ch);
                        float3 gb = float3(sl.r > 1e-4 ? hl.r / sl.r : 1.0, sl.g > 1e-4 ? hl.g / sl.g : 1.0, sl.b > 1e-4 ? hl.b / sl.b : 1.0);
                        gain = max(gain, lerp(1.0, gb, smoothstep(0.75, 1.0, MaxC(c)) * smoothstep(0.03, 0.15, MaxC(delta))));
                    }
                }
                if (any(isnan(gain)) || any(isinf(gain))) gain = 1.0;
                _VfxHdrGain[uint2(i.pos.xy)] = float4(clamp(gain, 1.0, max(_VfxHdrTop.y, 1.0)), UltraRTHdrPack(c));
                return float4(UltraRTOutputDither(c, i.pos.xy, 2.0), 1.0);
            }

            // Pass 5: native float HDR. A copy of fragFinal's distortion and underwater code (keep the three in step); the
            // target holds display-gamma float colour above 1 (docs/hdr.md, "Native float HDR"), so nothing is saturated.
            float4 fragFinalNative(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float3 ro = _VfxCamPos.xyz;
                float3 rd = _VfxRayZ.xyz;
                float dist = 0;
                bool water = _VfxWater.x > 0.5;
                bool warp = _VfxCounts.w > 0.5;
                float2 offset = 0;
                if (warp || water)
                {
                    dist = SceneDistance(uv, rd);
                    if (warp)
                    {
                        float3 d = Distortion(uv, ro, rd, dist);
                        offset = d.xy;
                        if (dot(offset, offset) > 0.0)
                        {
                            float3 rd2;
                            float dist2 = SceneDistance(uv + offset, rd2);
                            if (dist2 < d.z - 0.05) offset = 0;
                        }
                    }
                    if (water)
                    {
                        float t = _VfxCamPos.w;
                        float2 wob = float2(sin(uv.y * 31.0 + t * 2.3) + sin(uv.y * 13.0 - t * 1.1), cos(uv.x * 27.0 + t * 1.9) + cos(uv.x * 11.0 - t * 1.3));
                        offset += wob * _VfxWater.z * saturate(dist * 0.25);
                    }
                }
                if (_VfxDebug > 1.5) return float4(0.5 + offset * 100.0, 0.0, 1.0);
                float3 c;
                if (dot(offset, offset) > 0.0)
                {
                    c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb;
                    if (water) dist = SceneDistance(uv + offset, rd);
                }
                else c = _VfxScene.SampleLevel(my_point_clamp_sampler, uv, 0).rgb;
                c = min(max(c, 0.0), 64.0); // no NaN (min / max drop it on D3D), no infinity
                if (water) c = Underwater(c, uv, i.pos.xy, ro, rd, dist);

                if (_VfxHdrFx.x > 0.0)
                {
                    // Effects (HDREffects): the game's transparents are authored for SDR, where their brightest part is
                    // white. In the float target what they added (late - pre) is exact, whatever lies behind it, so their
                    // top range is expanded where it is (an inverse tone map of the effect, not of the pixel): the part of
                    // what they added whose brightest channel lies above _VfxHdrFx.y (fully from .z, squared) becomes
                    // HDREffects as bright in display gamma (_VfxHdrFx.x = HDREffects - 1). A faint glow, smoke and dust
                    // are left as drawn; nothing is saturated, and the output transform at present rolls it to the peak.
                    float3 late = min(max(_VfxLate.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb, 0.0), 64.0);
                    float3 pre = min(max(_VfxPre.SampleLevel(my_point_clamp_sampler, uv + offset, 0).rgb, 0.0), 64.0);
                    float3 d = max(late - pre, 0.0);
                    float t = smoothstep(_VfxHdrFx.y, _VfxHdrFx.z, MaxC(d));
                    c += d * (_VfxHdrFx.x * t * t);
                }

                if (_VfxBloomParams.x > 0.0)
                {
                    // Bloom: the glow adds in linear light, nothing more. The float target has no white to stay under, and
                    // the display transform at the end of the frame rolls it towards the peak with everything else.
                    // (Less of it on pixels that are already bright, as pass 3: the glow goes around a light.)
                    float3 b = _VfxBloom.SampleLevel(my_linear_clamp_sampler, uv + offset, 0).rgb * _VfxBloomParams.x;
                    if (_VfxDebug > 0.5) return float4(ToDisplay(b), 1.0);
                    b *= 1.0 - _VfxBloomParams2.w * smoothstep(0.35, 0.95, MaxC(c));
                    c = ToDisplay(ToLinear(c) + b);
                }
                return float4(min(max(c, 0.0), 64.0), 1.0);
            }
        ENDHLSL

        Pass // 0 prefilter
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragPrefilter
            #pragma target 4.5
            ENDHLSL
        }
        Pass // 1 downsample
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragDown
            #pragma target 4.5
            ENDHLSL
        }
        Pass // 2 upsample, added
        {
            ZTest Always ZWrite Off Cull Off
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragUp
            #pragma target 4.5
            ENDHLSL
        }
        Pass // 3 final
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragFinal
            #pragma target 4.5
            ENDHLSL
        }
        Pass // 4 final + HDR late gain map (UAV u1)
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragFinalHdr
            #pragma target 5.0
            ENDHLSL
        }
        Pass // 5 final, native float HDR
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragFinalNative
            #pragma target 4.5
            ENDHLSL
        }
    }
}
