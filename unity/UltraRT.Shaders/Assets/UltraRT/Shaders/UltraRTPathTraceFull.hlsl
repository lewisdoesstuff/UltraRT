#ifndef ULTRART_PATHTRACE_FULL_INCLUDED
#define ULTRART_PATHTRACE_FULL_INCLUDED

// The `pathtrace` view mode (docs/path-tracing.md): multi-bounce paths with next-event estimation at every vertex.
// Included by PathTrace.raytrace, whose RayGenPT entry point runs the same primary-hit code as RayGen (relight) and
// calls PtIndirect / PtReflection where relight traces its single cache-terminated bounce and its reflection ray. The
// G-buffer is the same, so both denoiser paths (ours, DLSS RR) and every later pass read it unchanged.
//
// A path vertex:
//   emission       0 for emitters the light sampling covers (_EmissiveBounceWeight), in full after a specular bounce
//   direct light   DirectOne: fill lights + RIS over lights and emissive triangles, one shadow ray
//   sky            SkyDirect: one table sample with a visibility ray, MIS-weighted against the cosine bounce
//   next segment   the specular lobe with probability by its weight (the composite's SpecularTerms), else cosine
// The last vertex of a path takes its lighting from the radiance cache (direct + every further bounce) when the tail
// is on and its cell is valid; that is the only place the cache enters a path.
//
// Needs, from the includer: Rand, CosineHemisphere, ReflectionDirection, TraceSceneCone, DiffuseCone, GlossyCone, DirectOne, SkyDirect,
// SkyMissWeight, CacheQueryTotal, EffectsAlong, StainTint, SpecularTerms, _ReflParams, _ReflMaxRadiance, _Ambient, _BounceAmbient,
// _EmissionScale.

// x paths per pixel (1-4), y bounce rays per path (1-4), z radiance-cache tail at the last vertex (0/1),
// w Russian roulette: paths whose throughput falls below this survive in proportion (0 = off)
float4 _PtParams;
// x firefly clamp on one path's radiance (luminance, lighting units; 0 = off), y specular lobes at path vertices (0/1),
// z sky samples at path vertices (0/1; the primary hit always takes one), w light samples at the primary hit (1-4; only
// without reservoirs, _LightSampling 1)
float4 _PtParams2;
// x water / glass / blood in the path (0/1): path rays see the transparent proxies (mask 0x10) and reflect off them by
// Fresnel or pass through tinted, y how much of the surface colour tints what passes through (0-1)
float4 _PtParams3;

float PtLuma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// Radiance arriving at r.Origin from r.Direction (lighting space), following the path for up to `bounces` segments.
//   specular   the first direction came from a specular lobe: emitters count in full, a sky miss is not MIS-weighted,
//              and V1's model is seen as reflection rays see it
//   nPrev      the normal the first direction was cosine-sampled around (the sky MIS weight of a diffuse miss)
//   firstDist  path length to the first hit (through portals); 1000 for the sky
//   effectW    the weight of glowing effects on the first segment when it is specular (EffectLobeWeight of its lobe)
//   cone       ray cone at r.Origin (width, spread angle) for the hits' texture LOD (docs/materials.md, "Texture LOD")
float3 PtPath(RayDesc r, bool specular, float3 nPrev, float effectW, float2 cone, int bounces, inout uint rng, out float firstDist)
{
    float3 L = 0;
    float3 T = 1;
    firstDist = 1000.0;
    bool cacheTail = _PtParams.z > 0.5;
    bool specLobes = _PtParams2.y > 0.5 && _ReflParams.x > 0.5;
    bool skyNee = _PtParams2.z > 0.5;
    bool skyMis = true; // the vertex this segment leaves took a direct sky sample (the primary hit always does)
    uint surfaces = ULTRART_MASK_SCENE | (_PtParams3.x > 0.5 ? ULTRART_MASK_TRANSPARENT : 0u);
    int crossings = 0; // transparent surfaces met so far (they don't count as bounces; at most 3 per path)
    float3 ambient = ToLight(_Ambient.rgb) * _BounceAmbient * RelightAmbientScale();
    [loop] for (int b = 1; b <= bounces; b++)
    {
        RayPayload h;
        float travelled;
        TraceSceneCone(RAY_FLAG_CULL_BACK_FACING_TRIANGLES,
                       surfaces | PlayerRayMask(specular ? ULTRART_PLAYER_REFLECTION : ULTRART_PLAYER_BOUNCE), cone, r, h, travelled); // r: the last segment after portals
        // Glowing effects in front of what a specular lobe sees (UltraRTEffects.hlsl). Cosine bounces get their light
        // from the effects' point lights instead.
        if (specular) L += T * ToLight(EffectsAlong(r.Origin, r.Direction, h.t < 0.0 ? 1.0e4 : h.t) * effectW);
        if (h.t < 0.0)
        {
            // Sky (sky walls report a miss too). After a cosine bounce the sky was also sampled directly at the previous
            // vertex: balance heuristic. A specular lobe sees the sky as it is drawn.
            if (specular) L += T * ToLight(SkyRadiance(r.Direction));
            else L += T * SkyLightRadiance(r.Direction) * (skyMis ? SkyMissWeight(nPrev, r.Direction) : 1.0);
            break;
        }
        float3 hp = r.Origin + r.Direction * h.t;
        float3 hn = h.normal; // faces the ray; no per-texel detail on path vertices (docs/materials.md)
        cone.x += cone.y * (travelled + h.t);
        uint transparent = (h.flags >> ULTRART_FLAG_TRANSPARENT_SHIFT) & 3u;
        if (transparent != 0u)
        {
            // Water, glass or liquid blood (the game draws it for the camera; here a path meets it): a mirror reflection
            // with the Fresnel probability, else straight through, tinted by its colour. Not a bounce. After 3 of them
            // the rest of the path ignores transparents (a ray skimming a pool could otherwise cross it for ever).
            if (++crossings >= 3) surfaces = ULTRART_MASK_SCENE;
            float f0 = FromUnorm8(h.material >> 16);
            float F = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(hn, -r.Direction)), 5.0);
            if (Rand(rng) < F)
            {
                r.Origin = hp + hn * 0.02;
                r.Direction = reflect(r.Direction, hn);
                specular = true;
                effectW = 1.0;
            }
            else
            {
                r.Origin = hp + r.Direction * 0.02;
                T *= lerp(1.0, ToLight(h.albedo), _PtParams3.y * (transparent == 3u ? 1.0 : 0.6));
            }
            r.TMin = 0.0;
            r.TMax = 1.0e4;
            b--;
            continue;
        }
        if (b == 1) firstDist = travelled + h.t;
        bool last = b == bounces;

        // Emitters the light sampling covers must not count again when a cosine bounce hits them.
        L += T * ToLight(PayloadEmission(h) * (_EmissionScale * (specular ? 1.0 : _EmissiveBounceWeight)));

        // The surface: a diffuse part (albedo x keep) and, on glossy and REFLECTION materials, the specular lobe the
        // composite adds at primary hits. The last vertex is all diffuse: its lobe would need one more segment.
        SurfaceMaterial mat = MaterialFromPayload(h.material, h.flags);
        // Seen along a specular segment (a reflection): the game's blood stains, which only its camera view draws.
        float3 albedo = h.albedo;
        if (specular) albedo *= StainTint(hp, hn); // not ?: (both sides run in HLSL 2018)
        float3 specW = 0;
        float keep = 1.0;
        if (specLobes && !last && mat.mode != ULTRART_REFL_NONE)
            SpecularTerms(mat, albedo, saturate(dot(hn, -r.Direction)), _ReflParams.yz, false, specW, keep);
        float3 diffuse = ToLight(albedo * keep);
        float lit = h.alpha; // 0 = fullbright: shows its albedo whatever the light, and reflects none

        float3 light = 0;
        bool tail = false;
        if (last && cacheTail) tail = CacheQueryTotal(hp, hn, light); // not && (no short-circuit in HLSL 2018)
        if (!tail)
        {
            light = DirectOne(hp, hn, PayloadLayer(h), rng);
            if (skyNee) light += SkyDirect(hp, hn, rng);
        }
        L += T * diffuse * lerp(1.0, light + ambient, lit);
        if (last) break;

        float3 specT = ToLight(specW);
        float3 diffT = diffuse * lit;
        float ws = PtLuma(specT), wd = PtLuma(diffT);
        if (ws + wd < 1e-5) break;
        float pSpec = ws / (ws + wd);
        r.Origin = hp + hn * 0.02;
        r.TMin = 0.0;
        r.TMax = 1.0e4;
        if (Rand(rng) < pSpec)
        {
            T *= specT / pSpec;
            r.Direction = ReflectionDirection(hn, r.Direction, mat.roughness, rng);
            cone = GlossyCone(cone, mat.roughness);
            specular = true;
            effectW = EffectLobeWeight(mat.roughness);
        }
        else
        {
            T *= diffT / (1.0 - pSpec);
            r.Direction = CosineHemisphere(hn, Rand(rng), Rand(rng));
            cone = DiffuseCone(cone);
            specular = false;
            nPrev = hn;
            skyMis = skyNee;
        }

        // Russian roulette: dim paths are thinned, the survivors weighted up.
        if (_PtParams.w > 0.0)
        {
            float q = saturate(max(T.r, max(T.g, T.b)) / _PtParams.w);
            if (q < 1.0)
            {
                if (Rand(rng) >= q) break;
                T /= q;
            }
        }
    }
    return max(L, 0.0); // also drops NaN
}

// More light samples at the primary hit, averaged into `direct` and the globals DirectLighting leaves behind. Only with
// plain RIS (_LightSampling 1): ReSTIR's reservoir holds one sample per pixel. DLSS RR gets its direct light this way:
// uncorrelated from frame to frame, with the noise bought down by samples instead of by reuse (docs/dlss.md).
void PtMoreDirect(inout float3 direct, uint2 id, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    int count = _LightSampling == 1 ? clamp((int)_PtParams2.w, 1, 4) : 1;
    if (count <= 1) return;
    float3 sum = direct, unshadowed = g_DirectUnshadowed, blocked = g_PlayerBlocked;
    [loop] for (int k = 1; k < count; k++)
    {
        g_PlayerBlocked = 0;
        sum += DirectLighting(id, dim, pos, n, t, layer, rng);
        unshadowed += g_DirectUnshadowed;
        blocked += g_PlayerBlocked;
    }
    float inv = 1.0 / (float)count;
    direct = sum * inv;
    g_DirectUnshadowed = unshadowed * inv;
    g_PlayerBlocked = blocked * inv;
}

int PtPaths() { return clamp((int)_PtParams.x, 1, 4); }
int PtBounces() { return clamp((int)_PtParams.y, 1, 4); }

// Indirect light at a primary hit: the mean of N cosine-sampled paths (the cosine pdf cancels Lambert's cos / pi).
// n: shading normal (per-texel detail); nv: vertex normal (ray offset, hemisphere test); cone: the camera pixel's ray cone
// at the hit (width, spread angle).
float3 PtIndirect(float3 pos, float3 n, float3 nv, float2 cone, inout uint rng)
{
    int paths = PtPaths();
    int bounces = PtBounces();
    float3 sum = 0;
    [loop] for (int s = 0; s < paths; s++)
    {
        RayDesc b;
        b.Origin = pos + nv * 0.02;
        // Stratified over the paths in the radial coordinate.
        b.Direction = CosineHemisphere(n, ((float)s + Rand(rng)) / (float)paths, Rand(rng));
        if (dot(b.Direction, nv) < 1e-3) b.Direction = reflect(b.Direction, nv); // detail normal: stay above the surface
        b.TMin = 0.0;
        b.TMax = 1.0e4;
        float dist;
        float3 L = PtPath(b, false, n, 1.0, DiffuseCone(cone), bounces, rng, dist);
        float l = PtLuma(L);
        if (_PtParams2.x > 0.0 && l > _PtParams2.x) L *= _PtParams2.x / l;
        sum += L;
    }
    return sum / (float)paths;
}

// Radiance along the reflection direction(s) of a primary hit, as TraceReflection returns it (display units: every
// sample is converted on its own, the reflection filter works on displayed values). dist: the first path's hit
// distance (1000 = sky). pos, nv, n as PtIndirect; dir: the camera ray's direction at the hit.
float3 PtReflection(float3 pos, float3 nv, float3 n, float3 dir, float roughness, float2 cone, inout uint rng, out float dist)
{
    int paths = PtPaths();
    int bounces = PtBounces();
    bool glossy = roughness >= 0.03;
    if (!glossy) paths = 1; // a mirror has one direction: more paths would only average the lighting noise behind it
    float3 sum = 0;
    dist = 1000.0;
    [loop] for (int s = 0; s < paths; s++)
    {
        RayDesc r;
        r.Origin = pos + nv * 0.02;
        r.Direction = ReflectionDirection(n, dir, roughness, rng);
        if (dot(r.Direction, nv) < 1e-3) r.Direction = reflect(dir, nv); // detail normal tilted the lobe below the surface
        r.TMin = 0.0;
        r.TMax = 1.0e4;
        float d;
        float3 c = LightToDisplay(PtPath(r, true, n, EffectLobeWeight(roughness), GlossyCone(cone, glossy ? roughness : 0.0), bounces, rng, d));
        if (s == 0) dist = d;
        if (glossy)
        {
            float l = PtLuma(c);
            if (l > _ReflMaxRadiance) c *= _ReflMaxRadiance / l;
        }
        sum += c;
    }
    return sum / (float)paths;
}

#endif
