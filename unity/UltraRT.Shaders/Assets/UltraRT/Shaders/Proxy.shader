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
        // ULTRAKILL/Master _EmissiveMask: the emission is multiplied by the main texture's alpha^2.2 (decompiled).
        _EmissiveMask ("Emissive Mask (albedo alpha)", Float) = 0
        _Cutoff ("Alpha cutoff", Float) = 0
        _AtlasMode ("Atlas mode (StaticSceneOptimizer)", Float) = 0
        _BlendTex ("Blend atlas", 2D) = "white" {}
        _SkyWall ("Sky wall (LIMBO_WALLS): report a miss", Float) = 0
        _AlphaTest ("Alpha test in any-hit", Float) = 0
        // ULTRAKILL/Master SCROLLING: uv * _MainTex_ST + _ScrollOffset + _ScrollSpeed * _Time.y (5-2's sea current, conveyor
        // belts, opaque lava). _ScrollOffset is ScrollingTexture's per-renderer property block value (SceneProxies.Sync.cs).
        _Scrolling ("Texture scrolls (SCROLLING)", Float) = 0
        _ScrollSpeed ("Scroll speed (uv/s)", Vector) = (0,0,0,0)
        _ScrollOffset ("Scroll offset (uv)", Vector) = (0,0,0,0)
        // The texture's velocity (uv/s: ScrollingTexture's speed + _ScrollSpeed), for its motion vectors.
        _ScrollVelocity ("Scroll velocity (uv/s)", Vector) = (0,0,0,0)
        // Frosted transparents (ice) also traced as scene geometry (SceneProxies.Transparent.cs): camera rays pass through
        // (the game draws the surface), every other ray sees it. Needs a non-opaque submesh so the any-hit runs.
        _CameraSkip ("Camera rays pass through", Float) = 0
        // Water / glass / blood proxies (mask 0x10, SceneProxies.Transparent.cs): the kind, reported in the payload flags so
        // the path tracing mode can reflect or pass through them (UltraRTPathTraceFull.hlsl). 0 = an ordinary surface.
        _Transparent ("Transparent kind (1 glass, 2 water, 3 blood)", Float) = 0
        // ULTRAKILL/Master without VERTEX_LIGHTING is fullbright in game: the lit weight is 1 - _Unlit (enemies: a partial
        // value, SceneProxies.UnlitEnemyLitWeight). Non-atlas only; atlas geometry carries it in the vertex colour alpha.
        _Unlit ("Unlit (0 lit, 1 fullbright)", Float) = 0
        // BLOOD_FILLER (7-3's tree): the game paints the albedo's green and blue out below a fill height (mesh TEXCOORD2.y in
        // [-1, 1]) with a noisy edge. _BloodFill: the renderer's _FillAmount (0 = off), _BloodScale its _MeshScale.z
        // (SceneProxies.Sync.cs sets both from the MaterialPropertyBlock; only for meshes with a 3-component TEXCOORD2).
        _BloodFill ("Blood fill amount", Float) = 0
        _BloodScale ("Blood noise scale (_MeshScale.z)", Float) = 1
        _BloodNoiseTex ("Blood noise", 2D) = "gray" {}

        // Reflections (MaterialClasses.cs, docs/reflections.md). Mode: 0 none, 1 specular, 2 blend, 3 additive.
        _Roughness ("Roughness", Float) = 1
        _Metalness ("Metalness", Float) = 0
        _F0 ("F0", Float) = 0.02
        _SpecWeight ("Specular weight", Float) = 0
        _ReflMode ("Reflection mode", Float) = 0
        _ReflStrength ("Reflection strength", Float) = 0
        _MatClass ("Material class", Float) = 21
        // VERTEX_BLENDING: the secondary set applies where the blend weight is < 0.5 (vertex colour r; atlas |normal.w| - 1).
        _Secondary ("Has secondary", Float) = 0
        _Roughness2 ("Roughness 2", Float) = 1
        _Metalness2 ("Metalness 2", Float) = 0
        _F02 ("F0 2", Float) = 0.02
        _SpecWeight2 ("Specular weight 2", Float) = 0
        _ReflMode2 ("Reflection mode 2", Float) = 0
        _MatClass2 ("Material class 2", Float) = 21
        // The game's own REFLECTION cubemap (ULTRAKILL/Master; GameReflection in MaterialClasses.cs), reconstructed for the
        // hybrid composite so it can be replaced. x: 0 none, 1 blend, 2 additive; y _ReflectionStrength;
        // z _LightingContribution; w _ColorContribution. Atlas proxies keep 0 (the baked shader has no cubemap).
        _GameRefl ("Game reflection", Vector) = (0,0,0,0)
        _GameCube ("Game cubemap", Cube) = "" {}
        _GameReflMask ("Game reflection mask", 2D) = "white" {}
        // 1: the game's cube is a pattern (DiamondCubemap on 3-2's big eye, Gabriel's SwordCubemap), not an environment
        // capture: no RT reflection replaces it; every ray but hybrid primaries (whose game colour already has it) adds the
        // game's own cube term, lit by RT lighting with _LightingContribution and as emission otherwise (MaterialClasses.cs).
        _GameCubeLit ("Keep the game's pattern cube", Float) = 0
        // Per-texel material detail (MaterialMaps.cs, docs/materials.md): rg tangent-space normal xy (0.5 + 0.5 x; x = +u,
        // y = +v), b roughness minus the texture's mean (0.5 + 0.5 d), a 1 = texel has detail. Same layout and uv as
        // _MainTex (the scene atlas for atlas proxies). x: normal strength, y: roughness amount (atlas: both baked into
        // the texture per cell, so 1), w: 1 = _DetailTex is bound.
        _DetailTex ("Detail (normal xy, roughness, valid)", 2D) = "black" {}
        _DetailParams ("Detail params", Vector) = (0,0,0,0)
        // The game's RAIN keyword (SceneProxies.Rain.cs, docs/materials.md "Rain"): mirrored from the source material.
        // Explosion debris (docs/vfx.md, "Glowing debris"): the proxy material of the game's ExplosionChunk; it emits the
        // global _UltraRTDebrisGlow, which cools after each explosion (Plugin.Vfx.cs).
        _HotDebris ("Hot debris", Float) = 0
        _Rain ("Rain", Float) = 0
        _RainDrops ("Rain drops", 2D) = "black" {}
        _RainTrails ("Rain trails", 2D) = "black" {}
        _RainDarkening ("Rain darkening", Color) = (1,1,1,1)
        _RainColor ("Rain colour", Color) = (0.2,0.2,0.3,1)
        _RainSpeed ("Rain speed", Float) = 2
        _RaindropsIntensity ("Raindrops intensity", Float) = 1
        _RainScale ("Rain scale", Float) = 1
        _WarpDetail ("Warp detail", Float) = 1
        _LocalOffsetRain ("Local offset rain", Float) = 0
        _RainCeilingToggle ("Rain ceiling toggle", Float) = 1
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
            #include "UltraRTMaterial.hlsl"

            Texture2D _MainTex;
            float4 _MainTex_ST;
            Texture2D _EmissiveTex;
            Texture2D _BlendTex;
            SamplerState my_point_repeat_sampler;

            float4 _Color;
            float4 _EmissiveColor;
            float _EmissiveIntensity;
            float _UseAlbedoAsEmissive;
            float _EmissiveMask;
            float _Cutoff;
            float _AtlasMode;
            float _SkyWall;
            float _AlphaTest;
            float _CameraSkip;
            float _Transparent;
            float _Unlit;
            float _BloodFill, _BloodScale;
            Texture2D _BloodNoiseTex;
            SamplerState my_linear_repeat_sampler;
            float _Roughness, _Metalness, _F0, _SpecWeight, _ReflMode, _ReflStrength, _MatClass;
            float _Secondary, _Roughness2, _Metalness2, _F02, _SpecWeight2, _ReflMode2, _MatClass2;
            float4 _GameRefl;
            TextureCube _GameCube;
            Texture2D _GameReflMask;
            float _GameCubeLit;
            SamplerState my_linear_clamp_sampler;
            Texture2D _DetailTex;
            float4 _DetailParams;
            // Global (Plugin, MaterialMaps): x enabled, y normal strength multiplier, z roughness amount multiplier.
            float4 _UltraRTDetail;
            // Texture LOD (globals, Plugin.Materials.cs; docs/materials.md "Texture LOD"): x 1 = detail at the ray cone's mip,
            // y LOD bias (mips), z grazing-angle stretch exponent, w 1 = albedo and emission at the cone's mip on secondary
            // hits. _UltraRTTexLod2.x: share of the filtered-away slope variance that becomes roughness.
            float4 _UltraRTTexLod;
            float4 _UltraRTTexLod2;

            Texture2D _RainDrops, _RainTrails;
            float4 _RainDarkening, _RainColor;
            float _HotDebris;
            float4 _UltraRTDebrisGlow; // global: rgb the glow of fresh explosion debris right now
            float _Rain, _RainSpeed, _RaindropsIntensity, _RainScale, _WarpDetail, _LocalOffsetRain, _RainCeilingToggle;
            float _UltraRTRainTime; // global: Time.timeSinceLevelLoad, the game shaders' _Time.y
            float _Scrolling;
            float4 _ScrollSpeed, _ScrollOffset, _ScrollVelocity;
            float _UltraRTTexScroll; // global: 1 = SCROLLING textures move (bench plugin._texScroll=0: frozen, as before)
            float _UltraRTScrollMotion; // global: 1 = scrolling surfaces report the texture's motion (bench plugin._texScrollMotion=0)
            float _UltraRTFrameDt;      // global: Time.deltaTime of this frame (the game's scroll step)

            // The game's RAIN term (ULTRAKILL/Master and ULTRAKILL-Stationary pixel shaders, decoded from their DXBC): an
            // albedo change before lighting. Dominant-axis planar projection of the world position (tile 16 / _RainScale
            // m); up- or down-facing surfaces get expanding raindrop rings (none on ceilings with _RainCeilingToggle 1),
            // walls get running streaks. albedo * _RainDarkening + amount * _RainColor.
            // Rain wetness (docs/vfx.md, "Rain"): global, x wetness of a rained-on surface (0 = off), y puddle amount,
            // z puddle noise frequency (1/m), w ripple strength.
            float4 _UltraRTWet;

            float WetHash(float2 p)
            {
                p = frac(p * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            // Smooth value noise in [0, 1], two octaves: where water collects on a floor.
            float WetNoise(float2 p)
            {
                float s = 0.0, a = 0.65;
                for (int o = 0; o < 2; o++)
                {
                    float2 i = floor(p), f = frac(p);
                    f = f * f * (3.0 - 2.0 * f);
                    s += a * lerp(lerp(WetHash(i), WetHash(i + float2(1, 0)), f.x), lerp(WetHash(i + float2(0, 1)), WetHash(i + float2(1, 1)), f.x), f.y);
                    p = p * 2.7 + 17.3;
                    a = 0.35;
                }
                return s;
            }

            // The game's raindrop ring at a _RainDrops sample (RainAlbedo below), signed: the ring's height profile.
            float RainRing(float4 d)
            {
                float y = d.r * 31.45 + _UltraRTRainTime * _RainSpeed * 1.5;
                float f = frac(y);
                float alt = floor(frac(y * 0.5) * 2.0);
                return alt * d.g * (1.0 - f) * sin(1.5 * clamp(9.0 * (d.g + f - 1.0), 0.0, 3.0));
            }

            // Wetness of a rained-on hit: wet 0..1 (darker, glossier), puddle 0..1 (standing water on floors: mirror-like,
            // flat), slope = the puddle surface's tilt from the raindrop rings (world x, z), primary rays only.
            void RainWetness(float3 P, float3 N, bool ripples, out float wet, out float puddle, out float2 slope)
            {
                wet = 0.0; puddle = 0.0; slope = 0.0;
                if (_UltraRTWet.x <= 0.0) return;
                // Ceilings and undersides stay dry when the game keeps its drops off them; walls are wet from the streaks.
                float up = saturate(N.y);
                float under = N.y < -0.2 && abs(_RainCeilingToggle - 1.0) < 1e-3 ? 0.0 : 1.0;
                wet = _UltraRTWet.x * lerp(0.6, 1.0, up) * under;
                if (up < 0.9 || _UltraRTWet.y <= 0.0) return;
                float n = WetNoise(P.xz * _UltraRTWet.z);
                float lo = 1.0 - _UltraRTWet.y * 0.75;
                puddle = smoothstep(lo, lo + 0.12, n) * saturate((up - 0.9) * 20.0);
                if (!ripples || _UltraRTWet.w <= 0.0) return;
                float3 p = P;
                if (_LocalOffsetRain != 0.0) { float3x4 o = ObjectToWorld3x4(); p -= float3(o[0][3], o[1][3], o[2][3]); }
                float2 uv = p.xz * (_RainScale * 0.0625);
                uint tw, th;
                _RainDrops.GetDimensions(tw, th);
                float2 e = 1.0 / float2(max(tw, 1u), max(th, 1u));
                float h0 = RainRing(_RainDrops.SampleLevel(my_linear_repeat_sampler, uv, 0));
                float hx = RainRing(_RainDrops.SampleLevel(my_linear_repeat_sampler, uv + float2(e.x, 0.0), 0));
                float hz = RainRing(_RainDrops.SampleLevel(my_linear_repeat_sampler, uv + float2(0.0, e.y), 0));
                slope = float2(hx - h0, hz - h0) * (_UltraRTWet.w * 1.5) * lerp(0.35, 1.0, puddle);
            }

            float3 RainAlbedo(float3 albedo, float3 P, float3 N)
            {
                float3 p = P;
                if (_LocalOffsetRain != 0.0) { float3x4 o = ObjectToWorld3x4(); p -= float3(o[0][3], o[1][3], o[2][3]); }
                float3 a = abs(N);
                bool yDom = a.y >= max(a.x, a.z);
                float2 uv = yDom ? p.xz : (a.z >= max(a.x, a.y) ? p.xy : p.zy);
                uv *= _RainScale * 0.0625;
                float amount;
                if (yDom)
                {
                    float4 d = _RainDrops.SampleLevel(my_point_repeat_sampler, uv, 0);
                    float y = d.r * 31.45 + _UltraRTRainTime * _RainSpeed * 1.5;
                    float f = frac(y);
                    float z = d.g * (1.0 - f) * sin(1.5 * clamp(9.0 * (d.g + f - 1.0), 0.0, 3.0));
                    float alt = floor(frac(y * 0.5) * 2.0); // every other cycle
                    float ceilingCut = abs(_RainCeilingToggle - 1.0) < 1e-3 ? saturate(ceil(N.y - 0.001)) : 1.0;
                    amount = saturate(alt * z) * 0.5 * ceilingCut * _RaindropsIntensity;
                }
                else
                {
                    float warpDetail = max(_WarpDetail, 1e-3);
                    float wb = _RainTrails.SampleLevel(my_point_repeat_sampler, uv, 0).b;
                    float warp = floor(wb * warpDetail * 8.0) / (warpDetail * 128.0);
                    float4 tr = _RainTrails.SampleLevel(my_point_repeat_sampler, uv + warp, 0);
                    float x = (tr.r + _UltraRTRainTime) * _RainSpeed * 0.5 + tr.g;
                    float m = saturate((1.0 - 2.0 * frac(abs(x))) * ceil(tr.g) - 0.1);
                    amount = tr.a * m * m * 0.5;
                }
                return albedo * _RainDarkening.rgb + amount * _RainColor.rgb;
            }

            float3 WorldNormalAt(uint v)
            {
                return normalize(mul(UnityRayTracingFetchVertexAttribute3(v, kVertexAttributeNormal), (float3x3)WorldToObject3x4()));
            }

            // The game's cubemap term C at this hit (UltraRTMaterial.hlsl: game = lit * k + C). The game computes the
            // reflection vector per vertex (reflect(-V, N) from the camera) and interpolates it, so do the same with the
            // triangle's vertices; the camera is the primary ray's origin. Its 3-tap filtered cube fetch is approximated
            // by one bilinear fetch.
            float3 GameCubeTerm(uint3 tri, float3 b, float3 p0, float3 p1, float3 p2, bool geoNormal, float3 nGeo, float m, out float k)
            {
                float3 cam = WorldRayOrigin();
                float3x4 o2w = ObjectToWorld3x4();
                float3 w0 = mul(o2w, float4(p0, 1.0)), w1 = mul(o2w, float4(p1, 1.0)), w2 = mul(o2w, float4(p2, 1.0));
                float3 n0 = nGeo, n1 = nGeo, n2 = nGeo;
                if (!geoNormal) { n0 = WorldNormalAt(tri.x); n1 = WorldNormalAt(tri.y); n2 = WorldNormalAt(tri.z); }
                float3 r = reflect(normalize(w0 - cam), n0) * b.x + reflect(normalize(w1 - cam), n1) * b.y
                         + reflect(normalize(w2 - cam), n2) * b.z;
                float3 cube = _GameCube.SampleLevel(my_linear_clamp_sampler, r, 0).rgb;
                float rs = _GameRefl.y;
                float3 tint = lerp(1.0, _Color.rgb, _GameRefl.w);
                bool add = _GameRefl.x > 1.5;
                k = add ? 1.0 : saturate(1.0 - m * rs);
                return cube * tint * (add ? m * rs * rs : m * m * rs);
            }

            // Motion vectors (globals, src/UltraRT/MotionVectors.cs; docs/denoiser.md). The instance's custom id carries a
            // motion slot in bits 8-23 (0 = static). Per slot, 4 float4: rows 0-2 the world-space motion matrix
            // W = M_prev * M_cur^-1 of the transform that drives the instance; row 3 x = first vertex of the instance's
            // previous-frame skinned positions in _UltraRTPrevSkin, or -1 (rigid).
            StructuredBuffer<float4> _UltraRTMotion;
            StructuredBuffer<float3> _UltraRTPrevSkin; // object space: skinned poses copied after each trace, displaced poses (Displace.compute) before the build

            // Previous-frame world position of this hit minus the current one, packed (PackMotion). The current position is
            // the BLAS hit point itself: for skinned instances the vertex fetch does not necessarily read the skinned pose
            // the BLAS was built from, so only the previous pose (same space as the BLAS) is interpolated from vertices.
            uint HitMotion(uint3 tri, float3 b)
            {
                uint slot = InstanceID() >> 8;
                if (slot == 0u) return 0u;
                float4 r0 = _UltraRTMotion[slot * 4u];
                float4 r1 = _UltraRTMotion[slot * 4u + 1u];
                float4 r2 = _UltraRTMotion[slot * 4u + 2u];
                float skin = _UltraRTMotion[slot * 4u + 3u].x;
                float3 wCur = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
                float4 w = float4(wCur, 1.0);
                if (skin >= 0.0)
                {
                    uint s = (uint)skin;
                    float3 objPrev = _UltraRTPrevSkin[s + tri.x] * b.x + _UltraRTPrevSkin[s + tri.y] * b.y + _UltraRTPrevSkin[s + tri.z] * b.z;
                    w.xyz = mul(ObjectToWorld3x4(), float4(objPrev, 1.0)); // previous pose under the current transform
                }
                float3 wPrev = float3(dot(r0, w), dot(r1, w), dot(r2, w));
                return PackMotion(wPrev - wCur);
            }

            // SCROLLING (SceneProxies.Scroll.cs): where the texture feature now under this hit was last frame, relative to
            // the hit. The texture moves by -dP/dtexUV x (velocity x dt) over the surface (sampling uv + offset). The
            // temporal filters (TAAU, RR's remodulation, the denoiser) then follow the flowing texture instead of
            // smearing it; lighting on such surfaces (water, lava, belts) is smooth, so following the texture costs nothing.
            float3 ScrollMotion(uint3 tri)
            {
                float2 u0 = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0);
                float2 u1 = UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0);
                float2 u2 = UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
                float3 p0 = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributePosition);
                float3 p1 = UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributePosition);
                float3 p2 = UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributePosition);
                float2 d1 = (u1 - u0) * _MainTex_ST.xy, d2 = (u2 - u0) * _MainTex_ST.xy;
                float det = d1.x * d2.y - d1.y * d2.x;
                if (abs(det) < 1e-12) return 0;
                float3 e1 = p1 - p0, e2 = p2 - p0;
                float3 tu = (e1 * d2.y - e2 * d1.y) / det; // dP / d(texture u), object space
                float3 tv = (e2 * d1.x - e1 * d2.x) / det;
                float2 duv = _ScrollVelocity.xy * _UltraRTFrameDt;
                return mul((float3x3)ObjectToWorld3x4(), tu * duv.x + tv * duv.y); // previous position of the feature - current
            }

            // ---------------------------------------------------------------- texture LOD (ray cones)

            // log2 of the ray cone's footprint in texture uv units (1 = the whole texture) at this hit (Akenine-Moller et al.
            // 2021): half the log2 ratio of the triangle's uv area to its world area, plus log2 of the cone's width here,
            // stretched by the angle to the surface (-z log2 |cos|: 0.5 = the footprint's area, 1 = its long axis). A texture
            // of W x H texels is then at mip lodUv + 0.5 log2(W H) (TexMip). st: texture uv per mesh uv. -64 = no cone.
            float ConeLodUv(float2 u0, float2 u1, float2 u2, float3 p0, float3 p1, float3 p2, float2 st, float coneW)
            {
                float2 d1 = (u1 - u0) * st, d2 = (u2 - u0) * st;
                float uvArea = abs(d1.x * d2.y - d1.y * d2.x);
                float3x3 o2w = (float3x3)ObjectToWorld3x4();
                float3 g = cross(mul(o2w, p1 - p0), mul(o2w, p2 - p0));
                float wArea = length(g);
                if (!(uvArea > 1e-20 && wArea > 1e-20 && coneW > 1e-9)) return -64.0;
                float cosT = abs(dot(g, WorldRayDirection())) / wArea;
                return 0.5 * log2(uvArea / wArea) + log2(coneW) - _UltraRTTexLod.z * log2(max(cosT, 0.02)) + _UltraRTTexLod.y;
            }

            // A texture's mip for a cone LOD, within its chain and under cap (atlas cells: AtlasMaxLod).
            float TexMip(Texture2D t, float lodUv, float cap)
            {
                uint w, h, levels;
                t.GetDimensions(0, w, h, levels);
                return clamp(lodUv + 0.5 * log2((float)max(w * h, 1u)), 0.0, min((float)levels - 1.0, cap));
            }

            // The deepest mip at which an atlas cell's texels stay inside the cell: its rect's offset and size in texels must
            // be multiples of 2^k (the atlas mips are box filters of the whole atlas).
            float AtlasMaxLod(float4 rect, Texture2D atlas)
            {
                uint w, h, levels;
                atlas.GetDimensions(0, w, h, levels);
                uint4 r = (uint4)round(saturate(rect) * float4(w, h, w, h));
                uint v = r.x | r.y | (r.z - r.x) | (r.w - r.y);
                return v == 0u ? 64.0 : (float)firstbitlow(v);
            }

            float2 FetchUV(uint3 tri, float3 b)
            {
                float2 u0 = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0);
                float2 u1 = UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0);
                float2 u2 = UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
                return u0 * b.x + u1 * b.y + u2 * b.z;
            }

            // The game's BLOOD_FILLER fragment (decompiled from ULTRAKILL/Master): p = mesh TEXCOORD2.xyz in [-1, 1]; noise
            // from _BloodNoiseTex around the trunk (u = angle, v = height x _MeshScale.z / 4) on sides, from (x, y) on caps;
            // below the noisy fill height the colour is multiplied by (1, 0, 0).
            float3 BloodFillTint(uint3 tri, float3 b)
            {
                float3 q = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributeTexCoord2) * b.x
                         + UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributeTexCoord2) * b.y
                         + UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributeTexCoord2) * b.z;
                float h = q.y * 0.5 + 0.5;
                float u = atan2(q.z * 0.5, q.x * 0.5) * 0.159155 + 0.5;
                float nSide = _BloodNoiseTex.SampleLevel(my_linear_repeat_sampler, float2(u, h * _BloodScale * 0.25), 0).x;
                float nCap = _BloodNoiseTex.SampleLevel(my_linear_repeat_sampler, float2(q.x * 0.5 + 0.5, h), 0).x;
                float3 nObj = UnityRayTracingFetchVertexAttribute3(tri.x, kVertexAttributeNormal) * b.x
                            + UnityRayTracingFetchVertexAttribute3(tri.y, kVertexAttributeNormal) * b.y
                            + UnityRayTracingFetchVertexAttribute3(tri.z, kVertexAttributeNormal) * b.z;
                float3 nw = normalize(mul(nObj, (float3x3)WorldToObject3x4()) + 1e-6);
                float capW = saturate(abs(nw.y) * 2.0 - 0.5);
                float n = saturate(lerp(nSide, nCap, capW)) * 2.0 - 1.0;
                return (h - n * 0.05 < _BloodFill) ? float3(1.0, 0.0, 0.0) : float3(1.0, 1.0, 1.0);
            }

            // Surface colour (alpha = coverage) at a hit; litWeight 0 = fullbright.
            // blendWeight: < 0.5 selects the secondary material (VERTEX_BLENDING); 1 when the surface doesn't blend.
            // texUV: where _MainTex (and _DetailTex) was sampled. lodUv, cap: the ray cone's LOD (ConeLodUv; -64 = mip 0).
            float4 SampleAlbedo(uint3 tri, float3 b, float2 uv, float lodUv, float cap, out float litWeight, out float blendWeight, out float2 texUV)
            {
                float4 albedo;
                litWeight = 1.0; // 0 = fullbright (game: lerp(1, lighting, vertexColor.a))
                blendWeight = 1.0;
                if (_AtlasMode > 0.5)
                {
                    // StaticSceneOptimizer geometry. Atlas rects (xMin, yMin, xMax, yMax) are constant per face.
                    float2 f = frac(uv);
                    float4 rect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord1);
                    texUV = lerp(rect.xy, rect.zw, f);
                    float4 mainTex = _MainTex.SampleLevel(my_point_repeat_sampler, texUV, TexMip(_MainTex, lodUv, cap));

                    // Vertex blending: texture = lerp(blendAtlas, mainAtlas, |normal.w| - 1).
                    float nw = abs(UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeNormal).w) * b.x
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeNormal).w) * b.y
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeNormal).w) * b.z;
                    float blendW = saturate(nw - 1.0);
                    float4 blendRect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord2);
                    float4 tex = mainTex;
                    if (any(blendRect != 0) && blendW < 0.999)
                    {
                        float4 blendTex = _BlendTex.SampleLevel(my_point_repeat_sampler, lerp(blendRect.xy, blendRect.zw, f), TexMip(_BlendTex, lodUv, cap));
                        tex = lerp(blendTex, mainTex, blendW);
                        blendWeight = blendW;
                    }

                    float4 vc = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeColor) * b.x
                              + UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeColor) * b.y
                              + UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeColor) * b.z;
                    albedo = float4(tex.rgb * vc.rgb, tex.a) * _Color;
                    litWeight = vc.a;
                }
                else
                {
                    texUV = uv * _MainTex_ST.xy + _MainTex_ST.zw;
                    if (_Scrolling * _UltraRTTexScroll > 0.5) texUV += _ScrollOffset.xy + _ScrollSpeed.xy * _UltraRTRainTime;
                    albedo = _MainTex.SampleLevel(my_point_repeat_sampler, texUV, TexMip(_MainTex, lodUv, cap)) * _Color;
                    litWeight = 1.0 - _Unlit;
                    if (_BloodFill > 0.0) albedo.rgb *= BloodFillTint(tri, b);
                    // ULTRAKILL VERTEX_BLENDING: vertex colour r < 0.5 is the secondary surface. Missing colours read as white.
                    if (_Secondary > 0.5)
                        blendWeight = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeColor).r * b.x
                                    + UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeColor).r * b.y
                                    + UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeColor).r * b.z;
                }
                return albedo;
            }

            // ---------------------------------------------------------------- per-texel detail (docs/materials.md)

            // The texel's detail (point sampled, like the albedo), or false where there is none. With a ray cone (lodUv >
            // -64) at the mip whose texels match the footprint: point sampled within each of the two nearest mips and blended
            // between them, so nothing smaller than a traced pixel aliases and close-up texels stay the game's grid.
            // var: the slope variance the mip averaged away (MaterialMaps.MakeDetailTexture: a = 1 - sd / 3), at strength 1.
            bool DetailSample(float2 texUV, float lodUv, float cap, out float4 d, out float var)
            {
                d = 0;
                var = 0.0;
                if (_DetailParams.w < 0.5 || _UltraRTDetail.x < 0.5) return false;
                float m = TexMip(_DetailTex, lodUv, cap);
                float m0 = floor(m);
                d = _DetailTex.SampleLevel(my_point_repeat_sampler, texUV, m0);
                if (m > m0)
                {
                    float4 d1 = _DetailTex.SampleLevel(my_point_repeat_sampler, texUV, m0 + 1.0);
                    if (d.a > 0.25 && d1.a > 0.25) d = lerp(d, d1, m - m0);
                }
                float sd = (1.0 - d.a) * 3.0;
                var = sd * sd;
                return d.a > 0.25;
            }

            // Shading normal n tilted by the tangent-space slope xy. The tangent frame comes from the triangle's positions
            // and UV0 (meshes may have no tangents): T = dp/du, B = dp/dv in world space, made orthogonal to n. Atlas UVs
            // map into their rect with the same orientation, so UV0 works for both; _MainTex_ST's sign flips the axes.
            float3 DetailNormal(float2 u0, float2 u1, float2 u2, float3 p0, float3 p1, float3 p2, float3 n, float2 xy)
            {
                float2 st = _AtlasMode > 0.5 ? float2(1.0, 1.0) : _MainTex_ST.xy;
                float2 d1 = (u1 - u0) * st, d2 = (u2 - u0) * st;
                float det = d1.x * d2.y - d1.y * d2.x;
                if (abs(det) < 1e-12) return n;
                float3 e1 = p1 - p0, e2 = p2 - p0;
                float3x3 o2w = (float3x3)ObjectToWorld3x4();
                float3 T = mul(o2w, (e1 * d2.y - e2 * d1.y) / det);
                float3 B = mul(o2w, (e2 * d1.x - e1 * d2.x) / det);
                T -= n * dot(n, T);
                float tl = dot(T, T);
                if (!(tl > 1e-20)) return n;
                T *= rsqrt(tl);
                float3 Bn = cross(n, T);
                if (dot(Bn, B) < 0.0) Bn = -Bn;
                float3 nd = normalize(n + xy.x * T + xy.y * Bn);
                // Never tilt past the view direction (a back-facing shading normal would go black).
                float3 V = -WorldRayDirection();
                float vn = dot(nd, V);
                if (vn < 0.02) nd = normalize(nd + V * (0.02 - vn));
                return nd;
            }

            // Alpha-tested (cutout) materials such as grass cards: ignore the hit where the texture is transparent.
            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                if (_CameraSkip > 0.5 && (p.flags & ULTRART_PAYLOAD_WANT_MOTION) != 0u) { IgnoreHit(); return; }
                if (_AlphaTest < 0.5) return;
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                float litWeight, blendWeight;
                float2 texUV;
                if (SampleAlbedo(tri, b, FetchUV(tri, b), -64.0, 64.0, litWeight, blendWeight, texUV).a < _Cutoff) IgnoreHit();
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                bool wantGameCube = (p.flags & ULTRART_FLAG_GAMECUBE) != 0u; // request from the ray gen (hybrid primary rays)
                bool wantDetail = (p.flags & ULTRART_FLAG_DETAIL) != 0u;     // primary rays only
                bool wantCone = (p.flags & ULTRART_PAYLOAD_CONE) != 0u;      // p.albedo.xy: the ray cone (texture LOD)
                float2 cone = p.albedo.xy;
                // The game draws these as the sky (by view direction): the ray "escapes" here.
                if (_SkyWall > 0.5) { p.t = -1.0; return; }
                bool wantMotion = (p.flags & ULTRART_PAYLOAD_WANT_MOTION) != 0u;

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
                float3 nGame = n; // the game's (unflipped) vertex normal: rain projection and ceiling test
                if (dot(n, WorldRayDirection()) > 0.0) n = -n; // treat everything as two-sided for now

                float2 u0 = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0);
                float2 u1 = UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0);
                float2 u2 = UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0);
                float2 uv = u0 * b.x + u1 * b.y + u2 * b.z;
                // Texture LOD (docs/materials.md, "Texture LOD"): the cone's footprint here in texture uv. Primary rays use it
                // for the detail maps only (their albedo stays the game's point-sampled mip 0); secondary rays for albedo and
                // emission where the game's textures have mips.
                float lodUv = -64.0, lodCap = 64.0;
                if (wantCone && _UltraRTTexLod.x + _UltraRTTexLod.w > 0.5)
                {
                    float2 st = _MainTex_ST.xy;
                    if (_AtlasMode > 0.5)
                    {
                        float4 rect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord1);
                        st = rect.zw - rect.xy;
                        lodCap = AtlasMaxLod(rect, _MainTex);
                    }
                    lodUv = ConeLodUv(u0, u1, u2, p0, p1, p2, st, cone.x + cone.y * RayTCurrent());
                }
                float lodAlbedo = -64.0;
                if (!wantDetail && _UltraRTTexLod.w > 0.5) lodAlbedo = lodUv;
                float litWeight, blendWeight;
                float2 texUV;
                float4 albedo = SampleAlbedo(tri, b, uv, lodAlbedo, lodCap, litWeight, blendWeight, texUV);
                float wet = 0.0, puddle = 0.0;
                float2 wetSlope = 0.0;
                if (_Rain > 0.5)
                {
                    float3 hitP = WorldRayOrigin() + WorldRayDirection() * RayTCurrent();
                    albedo.rgb = RainAlbedo(albedo.rgb, hitP, nGame);
                    if (litWeight > 0.5)
                    {
                        RainWetness(hitP, nGame, wantDetail, wet, puddle, wetSlope);
                        // A wet surface is darker (water fills the pores); standing water darker still.
                        albedo.rgb *= 1.0 - 0.18 * wet - 0.15 * puddle;
                    }
                }

                // Per-texel detail: perturbed shading normal and roughness. Primary rays also return the vertex normal
                // (packed in `primitive`) for the hybrid shadow ratio's denominator (the game lights with it).
                float4 detail;
                detail = 0;
                float detailVar = 0.0;
                bool hasDetail = false;
                float lodDetail = -64.0;
                if (_UltraRTTexLod.x > 0.5) lodDetail = lodUv;
                if (wantDetail) hasDetail = DetailSample(texUV, lodDetail, lodCap, detail, detailVar); // not &&: HLSL 2018 doesn't short-circuit (a texture read per hit)
                float3 nVertex = n;
                float detailStrength = _DetailParams.x * _UltraRTDetail.y;
                if (hasDetail)
                {
                    float2 xy = (detail.rg * 2.0 - 1.0) * detailStrength;
                    n = DetailNormal(u0, u1, u2, p0, p1, p2, n, xy);
                    flags |= ULTRART_FLAG_DETAIL;
                }
                if (puddle > 0.0 || any(wetSlope != 0.0))
                {
                    // Standing water is flat (the surface detail drowns), and tilts with the raindrop rings.
                    float3 nw = normalize(nVertex + float3(-wetSlope.x, 0.0, -wetSlope.y));
                    n = normalize(lerp(n, nw, saturate(puddle + (any(wetSlope != 0.0) ? 0.5 : 0.0))));
                }

                float3 emission = 0;
                if (_EmissiveIntensity > 0.0)
                {
                    // ULTRAKILL/Master (and Unity's Standard) sample the emission map at the main texture's uv
                    // (uv0 * _MainTex_ST), not with _EmissiveTex_ST: materials that share one texture and pick a band of it
                    // by _MainTex offset (0-2's FinalFrameL 2/3 strips) otherwise glow one band off.
                    float3 e = albedo.rgb;
                    if (_UseAlbedoAsEmissive < 0.5) e = _EmissiveTex.SampleLevel(my_point_repeat_sampler, texUV, TexMip(_EmissiveTex, lodAlbedo, lodCap)).rgb; // not ?: (both sides run)
                    emission = e * _EmissiveColor.rgb * _EmissiveIntensity;
                    if (_EmissiveMask > 0.5) emission *= pow(saturate(albedo.a), 2.2);
                }
                // Fresh explosion debris glows and cools; the texture's light and dark texels stay readable.
                if (_HotDebris > 0.5) emission += _UltraRTDebrisGlow.rgb * (0.4 + 1.2 * dot(albedo.rgb, float3(0.2126, 0.7152, 0.0722)));
                // Pattern cubes (_GameCubeLit): the game's cube term C on top of the surface, game = lit k + C with C lit by
                // lerp(1, lighting, _LightingContribution) -> albedo k + C lc (lit by RT) and C (1 - lc) as emission.
                if (_GameCubeLit > 0.5 && _GameRefl.x > 0.5 && !wantGameCube)
                {
                    float cm = _GameReflMask.SampleLevel(my_point_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw, 0).r;
                    float ck;
                    float3 c = GameCubeTerm(tri, b, p0, p1, p2, (flags & 1u) != 0u, nVertex, cm, ck);
                    albedo.rgb = albedo.rgb * ck + c * _GameRefl.z;
                    emission += c * (1.0 - _GameRefl.z);
                }

                p.albedo = albedo.rgb;
                p.alpha = litWeight;
                p.normal = n;
                p.t = RayTCurrent();
                p.emissionHalf = PackEmissionHalf(emission);
                p.instance = InstanceIndex();
                p.uvHalf = f32tof16(uv.x) | (f32tof16(uv.y) << 16);
                p.primitive = hasDetail && wantMotion ? PackOct24(nVertex) : PrimitiveIndex();
                p.motion = 0u;
                if (wantMotion) p.motion = HitMotion(tri, b); // not ?: (both sides run in HLSL 2018: motion buffers read on every hit)
                if (wantMotion && _Scrolling * _UltraRTTexScroll * _UltraRTScrollMotion > 0.5)
                    p.motion = PackMotion(UnpackMotion(p.motion) + ScrollMotion(tri));

                // Reflection material: constant per proxy material, or the secondary set on VERTEX_BLENDING surfaces.
                bool second = _Secondary > 0.5 && blendWeight < 0.5;
                uint mode = (uint)(second ? _ReflMode2 : _ReflMode);
                uint cls = (uint)(second ? _MatClass2 : _MatClass);
                // The game's _ReflectionMask (r, sampled at the main texture's uv) limits where its cubemap shows, e.g. the
                // glass of the shop terminal: ours follows it (strength for blend/additive, specular weight otherwise).
                float mask = 1.0;
                if (_GameRefl.x > 0.5)
                    mask = _GameReflMask.SampleLevel(my_point_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw, 0).r;
                float specWeight = second ? _SpecWeight2 : _SpecWeight * mask;
                float rough = second ? _Roughness2 : _Roughness;
                // Per-texel roughness around the class value (glossy classes only; REFLECTION keeps the game's strength).
                if (hasDetail && mode == ULTRART_REFL_SPECULAR)
                {
                    rough = saturate(rough + (detail.b * 2.0 - 1.0) * (_DetailParams.y * _UltraRTDetail.z));
                    // The slope variance the detail mip averaged away widens the lobe instead (LEAN / Toksvig: GGX
                    // alpha^2 += slope variance, alpha = roughness^2), so sub-pixel bumps read as a rougher surface.
                    float sv = detailVar * detailStrength * detailStrength * _UltraRTTexLod2.x;
                    if (sv > 0.0) { float a = rough * rough; rough = saturate(sqrt(sqrt(a * a + sv))); }
                }
                float metal = second ? _Metalness2 : _Metalness;
                float f0 = second ? _F02 : _F0;
                if (wet > 0.0 && mode != ULTRART_REFL_BLEND && mode != ULTRART_REFL_ADD)
                {
                    // A film of water: a smooth dielectric coat over whatever is underneath. Matte classes become
                    // (weakly) reflective, glossy ones get smoother; a puddle is a near mirror at grazing angles.
                    if (mode == ULTRART_REFL_NONE)
                    {
                        mode = ULTRART_REFL_SPECULAR;
                        specWeight = 0.0;
                        rough = 1.0;
                        metal = 0.0;
                    }
                    // Metals keep their own weight (their reflection already replaces the diffuse colour: a higher weight
                    // turned a wet metal floor black under a dark sky) and most of their roughness.
                    float nonMetal = 1.0 - metal;
                    float film = saturate(wet + puddle) * lerp(0.35, 1.0, nonMetal);
                    rough = lerp(rough, min(rough, lerp(0.3, 0.03, puddle)), film);
                    specWeight = max(specWeight, lerp(0.5 * wet, 1.0, puddle) * nonMetal);
                    f0 = max(f0, 0.04);
                }
                p.material = PackMaterialWord(rough, metal, f0, specWeight);
                // InstanceID() is the custom id we pass to AddInstance: the source object's original layer | motion slot << 8.
                p.flags = flags | ((InstanceID() & 0xFFu) << 8) | PackMaterialFlags(mode, _ReflStrength * mask, cls)
                        | (((uint)_Transparent & 3u) << ULTRART_FLAG_TRANSPARENT_SHIFT);

                // Hybrid primary hits: the game's cubemap term for the composite to remove. uvHalf / primitive are debug-only
                // otherwise; PathTrace's primary rays never read them.
                if (wantGameCube && _GameRefl.x > 0.5)
                {
                    float k;
                    float3 c = GameCubeTerm(tri, b, p0, p1, p2, (flags & 1u) != 0u, nVertex, mask, k);
                    p.uvHalf = Unorm8(c.r) | (Unorm8(c.g) << 8) | (Unorm8(c.b) << 16) | (Unorm8(k) << 24);
                    p.primitive = (p.primitive & 0xFFFFFFu) | (Unorm8(_GameRefl.z) << 24);
                    p.flags |= ULTRART_FLAG_GAMECUBE;
                }
            }
            ENDHLSL
        }

        // Visibility-only dispatches (Volumetrics.raytrace: shadow rays with SKIP_CLOSEST_HIT): the alpha test alone, as the
        // UltraRTHit pass's any-hit does it (SampleAlbedo's alpha). Unity rebuilds the shader table at every DispatchRays and
        // binds each record's material resources; a pass that uses three of them makes that much cheaper on the render
        // thread (docs/performance.md, "Render thread").
        Pass
        {
            Name "UltraRTShadow"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            #pragma raytracing UltraRTShadow

            #include "UnityRayTracingMeshUtils.cginc"
            #include "UltraRTCommon.hlsl"

            Texture2D _MainTex;
            float4 _MainTex_ST;
            Texture2D _BlendTex;
            SamplerState my_point_repeat_sampler;
            float4 _Color;
            float _Cutoff;
            float _AtlasMode;
            float _AlphaTest;

            [shader("anyhit")]
            void AnyHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                if (_AlphaTest < 0.5) return;
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                float2 uv = UnityRayTracingFetchVertexAttribute2(tri.x, kVertexAttributeTexCoord0) * b.x
                          + UnityRayTracingFetchVertexAttribute2(tri.y, kVertexAttributeTexCoord0) * b.y
                          + UnityRayTracingFetchVertexAttribute2(tri.z, kVertexAttributeTexCoord0) * b.z;
                float a;
                if (_AtlasMode > 0.5)
                {
                    float2 f = frac(uv);
                    float4 rect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord1);
                    a = _MainTex.SampleLevel(my_point_repeat_sampler, lerp(rect.xy, rect.zw, f), 0).a;
                    float nw = abs(UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeNormal).w) * b.x
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeNormal).w) * b.y
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeNormal).w) * b.z;
                    float blendW = saturate(nw - 1.0);
                    float4 blendRect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord2);
                    if (any(blendRect != 0) && blendW < 0.999)
                        a = lerp(_BlendTex.SampleLevel(my_point_repeat_sampler, lerp(blendRect.xy, blendRect.zw, f), 0).a, a, blendW);
                }
                else a = _MainTex.SampleLevel(my_point_repeat_sampler, uv * _MainTex_ST.xy + _MainTex_ST.zw, 0).a;
                if (a * _Color.a < _Cutoff) IgnoreHit();
            }
            ENDHLSL
        }
    }
}
