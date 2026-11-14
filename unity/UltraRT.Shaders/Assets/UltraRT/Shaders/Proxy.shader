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
        _BlendTex ("Blend atlas", 2D) = "white" {}
        _SkyWall ("Sky wall (LIMBO_WALLS): report a miss", Float) = 0
        _AlphaTest ("Alpha test in any-hit", Float) = 0
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
        // Per-texel material detail (MaterialMaps.cs, docs/materials.md): rg tangent-space normal xy (0.5 + 0.5 x; x = +u,
        // y = +v), b roughness minus the texture's mean (0.5 + 0.5 d), a 1 = texel has detail. Same layout and uv as
        // _MainTex (the scene atlas for atlas proxies). x: normal strength, y: roughness amount (atlas: both baked into
        // the texture per cell, so 1), w: 1 = _DetailTex is bound.
        _DetailTex ("Detail (normal xy, roughness, valid)", 2D) = "black" {}
        _DetailParams ("Detail params", Vector) = (0,0,0,0)
        // The game's RAIN keyword (SceneProxies.Rain.cs, docs/materials.md "Rain"): mirrored from the source material.
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
            float _Cutoff;
            float _AtlasMode;
            float _SkyWall;
            float _AlphaTest;
            float _Unlit;
            float _BloodFill, _BloodScale;
            Texture2D _BloodNoiseTex;
            SamplerState my_linear_repeat_sampler;
            float _Roughness, _Metalness, _F0, _SpecWeight, _ReflMode, _ReflStrength, _MatClass;
            float _Secondary, _Roughness2, _Metalness2, _F02, _SpecWeight2, _ReflMode2, _MatClass2;
            float4 _GameRefl;
            TextureCube _GameCube;
            Texture2D _GameReflMask;
            SamplerState my_linear_clamp_sampler;
            Texture2D _DetailTex;
            float4 _DetailParams;
            // Global (Plugin, MaterialMaps): x enabled, y normal strength multiplier, z roughness amount multiplier.
            float4 _UltraRTDetail;

            Texture2D _RainDrops, _RainTrails;
            float4 _RainDarkening, _RainColor;
            float _Rain, _RainSpeed, _RaindropsIntensity, _RainScale, _WarpDetail, _LocalOffsetRain, _RainCeilingToggle;
            float _UltraRTRainTime; // global: Time.timeSinceLevelLoad, the game shaders' _Time.y

            // The game's RAIN term (ULTRAKILL/Master and ULTRAKILL-Stationary pixel shaders, decoded from their DXBC): an
            // albedo change before lighting. Dominant-axis planar projection of the world position (tile 16 / _RainScale
            // m); up- or down-facing surfaces get expanding raindrop rings (none on ceilings with _RainCeilingToggle 1),
            // walls get running streaks. albedo * _RainDarkening + amount * _RainColor.
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
            StructuredBuffer<float3> _UltraRTPrevSkin; // object space, copied from the skinned vertex buffer after each trace

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
            // texUV: where _MainTex (and _DetailTex) was sampled.
            float4 SampleAlbedo(uint3 tri, float3 b, float2 uv, out float litWeight, out float blendWeight, out float2 texUV)
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
                    float4 mainTex = _MainTex.SampleLevel(my_point_repeat_sampler, texUV, 0);

                    // Vertex blending: texture = lerp(blendAtlas, mainAtlas, |normal.w| - 1).
                    float nw = abs(UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeNormal).w) * b.x
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.y, kVertexAttributeNormal).w) * b.y
                             + abs(UnityRayTracingFetchVertexAttribute4(tri.z, kVertexAttributeNormal).w) * b.z;
                    float blendW = saturate(nw - 1.0);
                    float4 blendRect = UnityRayTracingFetchVertexAttribute4(tri.x, kVertexAttributeTexCoord2);
                    float4 tex = mainTex;
                    if (any(blendRect != 0) && blendW < 0.999)
                    {
                        float4 blendTex = _BlendTex.SampleLevel(my_point_repeat_sampler, lerp(blendRect.xy, blendRect.zw, f), 0);
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
                    albedo = _MainTex.SampleLevel(my_point_repeat_sampler, texUV, 0) * _Color;
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

            // The texel's detail (point sampled, like the albedo), or false where there is none.
            bool DetailSample(float2 texUV, out float4 d)
            {
                d = 0;
                if (_DetailParams.w < 0.5 || _UltraRTDetail.x < 0.5) return false;
                d = _DetailTex.SampleLevel(my_point_repeat_sampler, texUV, 0);
                return d.a > 0.5;
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
                if (_AlphaTest < 0.5) return;
                uint3 tri = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 b = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                float litWeight, blendWeight;
                float2 texUV;
                if (SampleAlbedo(tri, b, FetchUV(tri, b), litWeight, blendWeight, texUV).a < _Cutoff) IgnoreHit();
            }

            [shader("closesthit")]
            void ClosestHit(inout RayPayload p : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                bool wantGameCube = (p.flags & ULTRART_FLAG_GAMECUBE) != 0u; // request from the ray gen (hybrid primary rays)
                bool wantDetail = (p.flags & ULTRART_FLAG_DETAIL) != 0u;     // primary rays only
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
                float litWeight, blendWeight;
                float2 texUV;
                float4 albedo = SampleAlbedo(tri, b, uv, litWeight, blendWeight, texUV);
                if (_Rain > 0.5) albedo.rgb = RainAlbedo(albedo.rgb, WorldRayOrigin() + WorldRayDirection() * RayTCurrent(), nGame);

                // Per-texel detail: perturbed shading normal and roughness. Primary rays also return the vertex normal
                // (packed in `primitive`) for the hybrid shadow ratio's denominator (the game lights with it).
                float4 detail;
                bool hasDetail = wantDetail && DetailSample(texUV, detail);
                float3 nVertex = n;
                if (hasDetail)
                {
                    float2 xy = (detail.rg * 2.0 - 1.0) * (_DetailParams.x * _UltraRTDetail.y);
                    n = DetailNormal(u0, u1, u2, p0, p1, p2, n, xy);
                    flags |= ULTRART_FLAG_DETAIL;
                }

                float3 emission = 0;
                if (_EmissiveIntensity > 0.0)
                {
                    // ULTRAKILL/Master (and Unity's Standard) sample the emission map at the main texture's uv
                    // (uv0 * _MainTex_ST), not with _EmissiveTex_ST: materials that share one texture and pick a band of it
                    // by _MainTex offset (0-2's FinalFrameL 2/3 strips) otherwise glow one band off.
                    float3 e = _UseAlbedoAsEmissive > 0.5
                        ? albedo.rgb
                        : _EmissiveTex.SampleLevel(my_point_repeat_sampler, texUV, 0).rgb;
                    emission = e * _EmissiveColor.rgb * _EmissiveIntensity;
                }

                p.albedo = albedo.rgb;
                p.alpha = litWeight;
                p.normal = n;
                p.t = RayTCurrent();
                p.emissionHalf = PackEmissionHalf(emission);
                p.instance = InstanceIndex();
                p.uvHalf = f32tof16(uv.x) | (f32tof16(uv.y) << 16);
                p.primitive = hasDetail && wantMotion ? PackOct24(nVertex) : PrimitiveIndex();
                p.motion = wantMotion ? HitMotion(tri, b) : 0u;

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
                    rough = saturate(rough + (detail.b * 2.0 - 1.0) * (_DetailParams.y * _UltraRTDetail.z));
                p.material = second ? PackMaterialWord(rough, _Metalness2, _F02, specWeight)
                                    : PackMaterialWord(rough, _Metalness, _F0, specWeight);
                // InstanceID() is the custom id we pass to AddInstance: the source object's original layer | motion slot << 8.
                p.flags = flags | ((InstanceID() & 0xFFu) << 8) | PackMaterialFlags(mode, _ReflStrength * mask, cls);

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
    }
}
