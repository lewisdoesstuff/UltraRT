#ifndef ULTRART_LIGHTS_INCLUDED
#define ULTRART_LIGHTS_INCLUDED

// Direct lighting for PathTrace.raytrace: ULTRAKILL's light model plus light selection.
//   _LightSampling 0: reference. Every light, one shadow ray each (DirectAll), uniform pick at the bounce.
//   _LightSampling 1: RIS. M candidates from an alias table, one shadow ray for the winner.
//   _LightSampling 2: RIS + temporal reuse (ReSTIR DI, biased 1/M combine). One shadow ray per pixel.
// Unshadowed fill lights are always evaluated exhaustively (they are few and need no rays).
// See docs/light-sampling.md.
//
// The includer must declare, before this file: RayPayload (UltraRTCommon.hlsl), _Scene, Rand(inout uint)
// and _LightScale.

// Per light, 4 x float4:
//   [0] position (or -forward for directional), range (0 = directional)
//   [1] color * intensity, unused (culling masks live in _LightMasks)
//   [2] forward, unused
//   [3] cos(spot/2) (-1 for non-spot), 1 / (cos(spot/4) - cos(spot/2)), unshadowed (1 = fill light), unused
StructuredBuffer<float4> _Lights;
StructuredBuffer<uint>   _LightMasks; // per-light culling mask (separate: masks like ~0 are NaN bit patterns as floats)
int      _LightCount;

int      _LightSampling;     // 0 reference, 1 RIS, 2 RIS + temporal reuse
int      _RisCandidates;     // candidates per pixel at the primary hit
int      _BounceCandidates;  // candidates at the bounce hit
int      _RisLightCount;     // entries in _LightAlias (every shadowed light, once)
// Alias table over the shadowed lights (Vose). Entry: x asuint(threshold), y light | aliasLight << 16,
// z asuint(pdf(light)), w asuint(pdf(aliasLight)). pdf is the probability of picking that light index.
StructuredBuffer<uint4>  _LightAlias;
int      _FillCount;
StructuredBuffer<uint>   _FillLights;   // indices of unshadowed fill lights

// Temporal reuse (only touched when _LightSampling == 2).
StructuredBuffer<uint>    _LightRemap;     // previous frame's light index -> this frame's index (0xFFFF = gone)
int       _PrevLightCount;
// Reservoir per pixel: x light | half(M) << 16, y asuint(W), z oct16x2 normal, w asuint(dot(normal, pos)).
StructuredBuffer<uint4>   _ReservoirPrev;
RWStructuredBuffer<uint4> _ReservoirCur;
float4x4  _PrevViewProj;    // viewProj of the frame that wrote _ReservoirPrev
int       _RestirHistoryValid;
float4    _RestirParams;    // x max history M, y min normal cosine, z plane tolerance (x ray t), w spatial radius (px)
int       _RestirSpatial;   // extra neighbour reservoirs taken from the previous frame (0 = temporal only)

#define ULTRART_NO_LIGHT 0xFFFFu

// ---------------------------------------------------------------- light model

bool Visible(float3 p, float3 n, float3 dir, float dist)
{
    RayDesc r;
    r.Origin = p + n * 0.02;
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = max(0.0, dist - 0.05);
    RayPayload sp = (RayPayload)0;
    sp.t = 1.0; // stays positive if anything is hit; the miss shader sets it negative
    TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, 0x01 /* geometry only: sky walls never block light */, 0, 1, 0, r, sp);
    return sp.t < 0.0;
}

float LightLuminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

bool LightUnshadowed(uint i) { return _Lights[i * 4 + 3].z > 0.5; }

// Unshadowed contribution of light i at p, using ULTRAKILL's falloff (StaticSceneOptimizer / Stationary shader):
// Unity's vertex-light term, the spot cone, and a linear fade from range to 2 * range.
// Returns false (and c = 0) when the light contributes nothing. L and dist are the shadow-ray direction and length.
bool LightEval(uint i, float3 p, float3 n, uint layer, out float3 L, out float dist, out float3 c)
{
    L = 0; dist = 0; c = 0;
    if (((_LightMasks[i] >> layer) & 1u) == 0u) return false;

    float4 l0 = _Lights[i * 4];
    bool local = l0.w != 0.0;
    float3 toL = local ? l0.xyz - p : l0.xyz;
    float d2 = max(dot(toL, toL), 1e-6);
    float d = sqrt(d2);
    L = toL / d;
    float ndl = dot(n, L);
    if (ndl <= 0.0) return false;

    float atten = 1.0;
    dist = 1.0e4;
    if (local)
    {
        float4 l2 = _Lights[i * 4 + 2];
        float4 l3 = _Lights[i * 4 + 3];
        float r = max(l0.w, 1e-6);
        atten = 1.0 / (1.0 + d2 * 25.0 / (r * r));
        atten *= saturate((max(dot(L, -l2.xyz), 0.0) - l3.x) * l3.y);
        atten *= saturate(2.0 - d / r);
        dist = d;
    }
    if (atten < 1e-3) return false;
    c = _Lights[i * 4 + 1].rgb * (atten * ndl);
    return true;
}

float3 LightFrom(uint i, float3 p, float3 n, uint layer)
{
    float3 L, c;
    float dist;
    if (!LightEval(i, p, n, layer, L, dist, c)) return 0;
    if (!LightUnshadowed(i) && !Visible(p, n, L, dist)) return 0;
    return c;
}

// Reference: every light, one shadow ray each.
float3 DirectAll(float3 p, float3 n, uint layer)
{
    float3 sum = 0;
    for (int i = 0; i < _LightCount; i++) sum += LightFrom((uint)i, p, n, layer);
    return sum * _LightScale;
}

float3 FillLighting(float3 p, float3 n, uint layer)
{
    float3 sum = 0;
    for (int k = 0; k < _FillCount; k++)
    {
        float3 L, c;
        float dist;
        if (LightEval(_FillLights[k], p, n, layer, L, dist, c)) sum += c;
    }
    return sum;
}

// ---------------------------------------------------------------- reservoirs

struct Reservoir
{
    uint   light;  // ULTRART_NO_LIGHT when nothing was selected
    float  wSum;
    float  M;
    float  pHat;   // target (luminance of the unshadowed contribution) of `light` at this pixel
    float3 c;      // unshadowed contribution of `light` at this pixel
    float3 L;
    float  dist;
};

Reservoir ReservoirEmpty()
{
    Reservoir r;
    r.light = ULTRART_NO_LIGHT;
    r.wSum = 0; r.M = 0; r.pHat = 0;
    r.c = 0; r.L = 0; r.dist = 0;
    return r;
}

// Weighted reservoir sampling of one stream element (weight w, representing M samples).
void ReservoirAdd(inout Reservoir r, uint light, float w, float M, float3 c, float3 L, float dist, float u)
{
    r.wSum += w;
    r.M += M;
    if (w > 0.0 && u * r.wSum < w)
    {
        r.light = light;
        r.pHat = LightLuminance(c);
        r.c = c; r.L = L; r.dist = dist;
    }
}

// Unbiased contribution weight W = wSum / (M * pHat(y)); the estimate is f(y) V(y) W.
float ReservoirW(Reservoir r)
{
    return (r.pHat > 0.0 && r.M > 0.0) ? r.wSum / (r.M * r.pHat) : 0.0;
}

void SampleLightTable(float u, out uint light, out float pdf)
{
    float x = u * (float)_RisLightCount;
    uint slot = min((uint)x, (uint)_RisLightCount - 1u);
    uint4 e = _LightAlias[slot];
    bool keep = (x - (float)slot) < asfloat(e.x);
    light = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
    pdf = asfloat(keep ? e.z : e.w);
}

// RIS: `count` candidates from the source pdf (alias table), resampled by the unshadowed contribution.
Reservoir RisCandidates(float3 p, float3 n, uint layer, int count, inout uint rng)
{
    Reservoir r = ReservoirEmpty();
    if (_RisLightCount <= 0) return r;
    for (int k = 0; k < count; k++)
    {
        uint li;
        float pdf;
        SampleLightTable(Rand(rng), li, pdf);
        float3 L, c;
        float dist;
        float w = (LightEval(li, p, n, layer, L, dist, c) && pdf > 0.0) ? LightLuminance(c) / pdf : 0.0;
        ReservoirAdd(r, li, w, 1.0, c, L, dist, Rand(rng));
    }
    return r;
}

// ---------------------------------------------------------------- temporal reuse

uint2 OctEncode16(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return (uint2)round(saturate(e * 0.5 + 0.5) * 65535.0);
}

float3 OctDecode16(uint2 q)
{
    float2 e = (float2)q / 65535.0 * 2.0 - 1.0;
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

uint RestirIndex(uint2 q, uint2 dim) { return q.y * dim.x + q.x; }

// Sky / no surface: an entry that is never reused.
void RestirWriteEmpty(uint2 id, uint2 dim)
{
    if (_LightSampling >= 2) _ReservoirCur[RestirIndex(id, dim)] = uint4(ULTRART_NO_LIGHT, 0u, 0u, 0u);
}

// Does the previous frame's reservoir h belong to (roughly) the same surface as (pos, n)?
bool RestirMatch(uint4 h, float3 pos, float3 n, float t)
{
    if (!(f16tof32(h.x >> 16) > 0.0)) return false;
    float3 nh = OctDecode16(uint2(h.z & 0xFFFFu, h.z >> 16));
    if (dot(n, nh) < _RestirParams.y) return false;
    return abs(dot(nh, pos) - asfloat(h.w)) <= _RestirParams.z * t + 0.01;
}

// Merge a previous-frame reservoir into r, re-targeting its light at the current pixel (biased 1/M combine).
void RestirMerge(inout Reservoir r, uint4 h, float3 pos, float3 n, uint layer, inout uint rng)
{
    float M = min(f16tof32(h.x >> 16), _RestirParams.x);
    uint prevLight = h.x & 0xFFFFu;
    uint light = ULTRART_NO_LIGHT;
    float3 L = 0, c = 0;
    float dist = 0;
    float w = 0;
    if (prevLight != ULTRART_NO_LIGHT)
    {
        if (prevLight >= (uint)_PrevLightCount) return;
        light = _LightRemap[prevLight];
        // The light is gone (switched off with its room, destroyed): this history describes a light set that
        // no longer exists, so drop it rather than count its M with zero weight.
        if (light == ULTRART_NO_LIGHT) return;
        if (LightEval(light, pos, n, layer, L, dist, c)) w = LightLuminance(c) * asfloat(h.y) * M;
    }
    // A history with no light (or W = 0 because its sample was occluded) still counts its M: it is evidence
    // that this pixel receives nothing from those samples. See docs/light-sampling.md.
    ReservoirAdd(r, light, w, M, c, L, dist, Rand(rng));
}

void RestirReuse(inout Reservoir r, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    if (_RestirHistoryValid == 0) return;
    float4 pc = mul(_PrevViewProj, float4(pos, 1.0));
    if (pc.w <= 1e-6) return;
    float2 pp = (pc.xy / pc.w * 0.5 + 0.5) * (float2)dim;

    if (all(pp >= 0.0) && all(pp < (float2)dim))
    {
        uint4 h = _ReservoirPrev[RestirIndex((uint2)pp, dim)];
        if (RestirMatch(h, pos, n, t)) RestirMerge(r, h, pos, n, layer, rng);
    }

    for (int k = 0; k < _RestirSpatial; k++)
    {
        float a = Rand(rng) * 6.28318530718;
        float rad = sqrt(Rand(rng)) * _RestirParams.w;
        float2 q = pp + float2(cos(a), sin(a)) * rad;
        if (any(q < 0.0) || any(q >= (float2)dim)) continue;
        uint4 h = _ReservoirPrev[RestirIndex((uint2)q, dim)];
        if (RestirMatch(h, pos, n, t)) RestirMerge(r, h, pos, n, layer, rng);
    }
}

// ---------------------------------------------------------------- entry points

// The unshadowed counterpart of the last DirectLighting() result (same samples, no visibility). The hybrid composite
// uses shadowed / unshadowed as its shadow ratio, so it needs no separate loop over every light.
static float3 g_DirectUnshadowed;

// Direct light at the primary hit. Writes this pixel's reservoir when temporal reuse is on.
float3 DirectLighting(uint2 id, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    if (_LightSampling <= 0)
    {
        float3 shadowed = 0, unshadowed = 0;
        for (int i = 0; i < _LightCount; i++)
        {
            float3 L, c;
            float dist;
            if (!LightEval((uint)i, pos, n, layer, L, dist, c)) continue;
            unshadowed += c;
            if (LightUnshadowed((uint)i) || Visible(pos, n, L, dist)) shadowed += c;
        }
        g_DirectUnshadowed = unshadowed * _LightScale;
        return shadowed * _LightScale;
    }

    float3 sum = FillLighting(pos, n, layer);
    float3 unshadowedSum = sum;
    Reservoir r = RisCandidates(pos, n, layer, _RisCandidates, rng);
    bool temporal = _LightSampling >= 2;
    if (temporal) RestirReuse(r, dim, pos, n, t, layer, rng);

    float W = ReservoirW(r);
    if (W > 0.0)
    {
        unshadowedSum += r.c * W;
        // Occluded samples are stored with W = 0 so they stop propagating (visibility reuse with the one ray).
        if (Visible(pos, n, r.L, r.dist)) sum += r.c * W;
        else W = 0.0;
    }

    if (temporal)
    {
        uint2 oct = OctEncode16(n);
        float3 nq = OctDecode16(oct); // the plane is stored against the quantised normal, so the test is exact
        float M = min(r.M, _RestirParams.x);
        _ReservoirCur[RestirIndex(id, dim)] = uint4(
            (r.light & 0xFFFFu) | (f32tof16(M) << 16),
            asuint(W),
            oct.x | (oct.y << 16),
            asuint(dot(nq, pos)));
    }
    g_DirectUnshadowed = unshadowedSum * _LightScale;
    return sum * _LightScale;
}

// Direct light at the bounce hit: one shadow ray.
float3 DirectOne(float3 p, float3 n, uint layer, inout uint rng)
{
    if (_LightSampling <= 0)
    {
        if (_LightCount <= 0) return 0;
        int i = min((int)(Rand(rng) * _LightCount), _LightCount - 1);
        return LightFrom((uint)i, p, n, layer) * (_LightCount * _LightScale);
    }

    float3 sum = FillLighting(p, n, layer);
    Reservoir r = RisCandidates(p, n, layer, _BounceCandidates, rng);
    float W = ReservoirW(r);
    if (W > 0.0 && Visible(p, n, r.L, r.dist)) sum += r.c * W;
    return sum * _LightScale;
}

#endif
