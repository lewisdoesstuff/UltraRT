// Native D3D12 cs_6_5 inline RayQuery particle lighting volume (Wave 3).
// Eliminates ParticleLight.raytrace's Unity DispatchRays shader-table rebuild (~0.82 ms RT-CPU in 8-x).
// Direct: exact RIS over scene lights + emissive triangles with inline RayQuery shadow ray.
// Indirect: inline RayQuery bounce ray -> miss: exact SkyLightRadiance; hit: SHaRC radiance cache lookup * representative albedo.

RaytracingAccelerationStructure _Scene : register(t0);

cbuffer PlCB : register(b0)
{
    column_major float4x4 _PLInvViewProj;
    float4 _CamPos;
    float4 _PLGrid;          // x, y, z, frameIndex (as float? use _PLInt0.x)
    float4 _PLDepth;         // x near, y log2(far/near), z 1/log2, w linear (0/1)
    float4 _PLCamFwd;        // xyz forward, w _LightRadius
    float4 _PLParams;        // x candidates, y indirectScale, z maxSample, w indirectOn
    float4 _PLAmbient;       // rgb flat ambient (game units), w _FixtureShadows
    float4 _Relight;         // x falloff blend, y exposure, z linearK, w preLinearLights
    float4 _SkyLight;        // x linear, y sunAngle, z skySampling, w skyScale
    float4 _SkyColor;        // rgb flat sky
    float4 _SkyTint;         // rgb tint
    float4 _SkyParams;       // x hasTex, y rotation, z, w
    float4 _EmissiveParams;  // x share, y lightScale/pi, z minDist2, w bounceWeight
    float4 _CacheGrid;       // x cellSize, y 1/lodDist, z normalOffset, w levelFallback
    float4 _CacheQuery;      // x minSamples, y, z, w
    uint4  _PLInt0;          // x frameIndex, y lightCount, z risLightCount, w fillCount
    uint4  _PLInt1;          // x isoMask, y emissiveCount, z emissiveTriCount, w cacheMode
    uint4  _PLInt2;          // x cacheCapacity, y cacheViewCount, z portalImagesOn, w unused
    float4 _CacheViews[16];  // portal cache viewpoints
};

StructuredBuffer<float4> _Lights           : register(t1);
StructuredBuffer<uint>   _LightMasks       : register(t2);
StructuredBuffer<uint4>  _LightAlias       : register(t3);
StructuredBuffer<uint>   _FillLights       : register(t4);
StructuredBuffer<uint4>  _EmissiveTris     : register(t5);
StructuredBuffer<float4> _EmissiveOwners   : register(t6);
StructuredBuffer<uint4>  _EmissiveAlias    : register(t7);
StructuredBuffer<uint>   _EmissiveActive   : register(t8);
StructuredBuffer<uint>   _CacheKeys        : register(t9);
StructuredBuffer<uint4>  _CacheResolved    : register(t10);
Texture2D<float4>        _SkyTex           : register(t11);
StructuredBuffer<float4> _PortalImageQuads : register(t12);
StructuredBuffer<float4> _PortalTravels    : register(t13);

RWTexture3D<float4> _PLRaw : register(u0);

SamplerState s_LinearRepeat : register(s0);

static const float kPi = 3.14159265;
#define ULTRART_NO_LIGHT 0xFFFFu
#define ULTRART_EMISSIVE 0x10000u
#define ULTRART_CACHE_BUCKET 8u
#define ULTRART_CACHE_MAX_LEVEL 15
#define ULTRART_MASK_SCENE 0x6Fu

uint HashU32(uint x)
{
    x ^= x >> 16; x *= 0x7feb352dU;
    x ^= x >> 15; x *= 0x846ca68bU;
    x ^= x >> 16;
    return x;
}

uint Pcg(inout uint s)
{
    s = s * 747796405u + 2891336453u;
    uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}
float Rand(inout uint s) { return Pcg(s) * (1.0 / 4294967296.0); }

float3 UltraRTCameraRay(float4x4 invViewProj, float2 ndc, float3 camPos)
{
    float4 h = mul(invViewProj, float4(ndc, 1.0, 1.0));
    return normalize(h.xyz - camPos * h.w);
}

bool LinearLight() { return _SkyLight.x > 0.5; }
float3 ToLight(float3 g) { return LinearLight() ? pow(max(g, 0.0), 2.2) : g; }
float LightLuminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// ---- Sky
float3 SkyRotate(float3 d, float sgn)
{
    float s = sin(_SkyParams.y) * sgn, c = cos(_SkyParams.y);
    return float3(c * d.x - s * d.z, d.y, s * d.x + c * d.z);
}

float2 SkyUV(float3 d)
{
    d = SkyRotate(d, 1.0);
    return float2(atan2(d.x, d.z) * (0.5 / kPi) + 0.5, asin(clamp(d.y, -1.0, 1.0)) * (1.0 / kPi) + 0.5);
}

float3 SkyLightRadiance(float3 d)
{
    float3 sr = _SkyParams.x < 0.5 ? _SkyColor.rgb : _SkyTex.SampleLevel(s_LinearRepeat, SkyUV(d), 0).rgb * _SkyTint.rgb;
    return ToLight(sr) * (_SkyLight.w > 0.0 ? _SkyLight.w : 1.0);
}

// ---- SHaRC Cache lookup
uint CacheNormalBin(float3 n)
{
    float3 a = abs(n);
    if (a.x >= a.y && a.x >= a.z) return n.x >= 0.0 ? 0u : 1u;
    if (a.y >= a.z) return n.y >= 0.0 ? 2u : 3u;
    return n.z >= 0.0 ? 4u : 5u;
}

float CacheLevelF(float3 p)
{
    float3 dc = p - _CamPos.xyz;
    float d2 = dot(dc, dc);
    uint vc = _PLInt2.y;
    for (uint i = 0u; i < vc; i++)
    {
        float3 dv = p - _CacheViews[i].xyz;
        d2 = min(d2, dot(dv, dv));
    }
    return log2(max(sqrt(d2) * _CacheGrid.y, 1.0));
}

float CacheCellSize(int level) { return _CacheGrid.x * exp2((float)level); }

uint CacheHash2(uint x)
{
    x = x * 747796405u + 2891336453u;
    x = ((x >> ((x >> 28u) + 4u)) ^ x) * 277803737u;
    return (x >> 22u) ^ x;
}

void CacheKey(float3 p, float3 n, int level, out uint bucket, out uint check)
{
    float cell = CacheCellSize(level);
    int3 q = (int3)floor((p + n * (_CacheGrid.z * cell)) / cell);
    uint meta = (uint)level | (CacheNormalBin(n) << 4);
    uint h = HashU32(asuint(q.x) + 0x9E3779B9u);
    h = HashU32(h ^ asuint(q.y));
    h = HashU32(h ^ asuint(q.z));
    h = HashU32(h ^ meta);
    uint c = CacheHash2(asuint(q.x) ^ 0x85EBCA6Bu);
    c = CacheHash2(c + asuint(q.y));
    c = CacheHash2(c + asuint(q.z));
    c = CacheHash2(c + meta * 0xC2B2AE35u);
    bucket = h & (_PLInt2.x - 1u) & ~(ULTRART_CACHE_BUCKET - 1u);
    check = max(c, 1u);
}

bool CacheFind(uint bucket, uint check, out uint slot)
{
    slot = 0;
    [unroll] for (uint k = 0; k < ULTRART_CACHE_BUCKET; k++)
    {
        if (_CacheKeys[bucket + k] == check) { slot = bucket + k; return true; }
    }
    return false;
}

bool CacheQueryTotal(float3 p, float3 n, out float3 total)
{
    total = 0;
    if (_PLInt1.w == 0u || _PLInt2.x == 0u) return false;
    float lf = CacheLevelF(p);
    int level = min((int)lf, ULTRART_CACHE_MAX_LEVEL);
    uint bucket, check, slot;
    CacheKey(p, n, level, bucket, check);
    if (CacheFind(bucket, check, slot))
    {
        uint4 r = _CacheResolved[slot];
        if (f16tof32(r.w) >= _CacheQuery.x)
        {
            total = float3(f16tof32(r.x), f16tof32(r.x >> 16), f16tof32(r.y));
            return true;
        }
    }
    if (_CacheGrid.w > 0.5)
    {
        int other = frac(lf) < 0.5 ? level - 1 : level + 1;
        if (other >= 0 && other <= ULTRART_CACHE_MAX_LEVEL)
        {
            CacheKey(p, n, other, bucket, check);
            if (CacheFind(bucket, check, slot))
            {
                uint4 r = _CacheResolved[slot];
                if (f16tof32(r.w) >= _CacheQuery.x)
                {
                    total = float3(f16tof32(r.x), f16tof32(r.x >> 16), f16tof32(r.y));
                    return true;
                }
            }
        }
    }
    return false;
}

// ---- Visibility via inline RayQuery
bool VisibleRaw(float3 p, float3 dir, float dist, uint mask)
{
    RayDesc r;
    r.Origin = p;
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = max(0.0, dist - 0.05);
    RayQuery<RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_CULL_NON_OPAQUE> q;
    q.TraceRayInline(_Scene, 0u, mask, r);
    q.Proceed();
    return q.CommittedStatus() == COMMITTED_NOTHING;
}

bool PortalImageSegmentThrough(uint side, float3 p, float3 v)
{
    uint k = side * 3u;
    float4 q2 = _PortalImageQuads[k + 2u];
    float za = dot(q2, float4(p, 1.0));
    float zb = dot(q2, float4(v, 1.0));
    if (za <= 0.0 || zb >= 0.0) return false;
    float s = za / (za - zb);
    float4 h = float4(lerp(p, v, s), 1.0);
    return abs(dot(_PortalImageQuads[k], h)) <= 0.5 && abs(dot(_PortalImageQuads[k + 1u], h)) <= 0.5;
}

bool LightRayVisible(float lw, float3 p, float3 L, float dist, uint mask)
{
    float len = dist, tP = 0.0;
    bool second = false;
    if (_PLInt2.z != 0u && lw < -0.5)
    {
        uint k = (uint)(-lw - 0.5) * 3u;
        float4 q2 = _PortalImageQuads[k + 2u];
        float za = dot(q2, float4(p, 1.0));
        float dz = dot(q2.xyz, L);
        if (za <= 0.0 || dz >= -1e-6) return false;
        tP = za / -dz;
        float4 h = float4(p + L * tP, 1.0);
        if (abs(dot(_PortalImageQuads[k], h)) > 0.5) return false;
        if (abs(dot(_PortalImageQuads[k + 1u], h)) > 0.5) return false;
        if (tP < dist) { len = tP + 0.03; second = dist - tP > 0.07; }
    }
    float3 o = p, d = L;
    bool vis = true;
    [loop] for (uint seg = 0u; seg < 2u; seg++)
    {
        vis = VisibleRaw(o, d, len, mask);
        if (!vis || !second) break;
        second = false;
        uint s = (uint)(-lw - 0.5) * 3u;
        float4 r0 = _PortalTravels[s], r1 = _PortalTravels[s + 1u], r2 = _PortalTravels[s + 2u];
        float3x3 m = float3x3(r0.xyz, r1.xyz, r2.xyz);
        float3 t = float3(r0.w, r1.w, r2.w);
        d = mul(d, m);
        o = mul(p - t, m) + d * tP;
        len = dist - tP;
    }
    return vis;
}

// ---- Isotropic Light Evaluation (matches UltraRTLights.hlsl ULTRART_LIGHT_ISOTROPIC)
bool LightEvalIso(uint i, float3 p, out float3 L, out float dist, out float3 c)
{
    L = 0; dist = 0; c = 0;
    if ((_LightMasks[i] & _PLInt1.x) == 0u) return false;
    float4 l0 = _Lights[i * 4];
    float4 l1 = _Lights[i * 4 + 1];
    float4 l2 = _Lights[i * 4 + 2];
    float4 l3 = _Lights[i * 4 + 3];
    float atten = 1.0;
    if (l0.w == 0.0)
    {
        L = l0.xyz;
        dist = 1.0e4;
    }
    else
    {
        float3 lp = l0.xyz;
        if (_PLInt2.z != 0u && l3.w < -0.5)
        {
            if (!PortalImageSegmentThrough((uint)(-l3.w - 0.5), p, lp)) return false;
        }
        float3 toL = lp - p;
        float d2 = dot(toL, toL);
        float r = max(l0.w, 1e-6);
        float reach = 2.0 * r;
        if (d2 >= reach * reach) return false;
        dist = sqrt(max(d2, 1e-8));
        L = toL / dist;
        float spot = 1.0;
        if (l3.x > -0.5)
        {
            spot = saturate((max(dot(L, -l2.xyz), 0.0) - l3.x) * l3.y);
            if (spot <= 0.0) return false;
        }
        float game = (1.0 / (1.0 + d2 * 25.0 / (r * r))) * saturate(2.0 - dist / r);
        float xr = dist / reach;
        float window = saturate(1.0 - xr * xr * xr * xr);
        float wReach = 1.0 - 0.25 * floor(l1.w * 0.5);
        if (LinearLight())
        {
            float gameL = pow(game, 2.2);
            float phys = _Relight.z * (r * r) / max(d2, 0.01) * window * window;
            phys = min(phys, pow(3.0 * game + 0.05, 2.2));
            phys = lerp(gameL, phys, saturate(_Relight.x));
            atten = lerp(min(phys, gameL), phys, wReach) * pow(spot, 2.2);
        }
        else
        {
            float rRef = 0.5 * r;
            float phys = (1.0 / 7.25) * (rRef * rRef) / max(d2, 0.01) * window * window;
            phys = min(phys, 3.0 * game + 0.05);
            phys = lerp(game, phys, saturate(_Relight.x));
            atten = lerp(min(phys, game), phys, wReach) * spot;
        }
    }
    if (atten <= 0.0) return false;
    float3 lc;
    if (LinearLight() && _Relight.w > 0.5)
    {
        float sLin = _Relight.y != 1.0 ? pow(max(_Relight.y, 0.0), 2.2) : 1.0;
        lc = l1.rgb * sLin;
    }
    else
    {
        lc = ToLight(l1.rgb * _Relight.y);
    }
    c = lc * atten;
    return true;
}

bool EmissiveEvalIso(uint tri, uint bary, float3 p, out float3 L, out float dist, out float3 c, out float area)
{
    L = 0; dist = 0; c = 0; area = 0;
    if (tri >= _PLInt1.z) return false;
    if (((_EmissiveActive[tri >> 5] >> (tri & 31u)) & 1u) == 0u) return false;
    uint4 t0 = _EmissiveTris[tri * 3];
    uint4 t1 = _EmissiveTris[tri * 3 + 1];
    uint4 t2 = _EmissiveTris[tri * 3 + 2];
    uint owner = t0.w & 0xFFFFu;
    uint flags = t0.w >> 16;
    float4 r0 = _EmissiveOwners[owner * 3];
    float4 r1 = _EmissiveOwners[owner * 3 + 1];
    float4 r2 = _EmissiveOwners[owner * 3 + 2];
    float3x3 m = float3x3(r0.xyz, r1.xyz, r2.xyz);
    float3 p0 = mul(m, asfloat(t0.xyz)) + float3(r0.w, r1.w, r2.w);
    float3 e1 = mul(m, asfloat(t1.xyz));
    float3 e2 = mul(m, asfloat(t2.xyz));
    float3 nc = cross(e1, e2);
    float len = length(nc);
    if (len < 1e-10) return false;
    area = 0.5 * len;
    float3 nl = nc / len;
    if ((determinant(m) < 0.0) != ((flags & 2u) != 0u)) nl = -nl;
    float b1 = (bary & 1023u) * (1.0 / 1024.0);
    float b2 = ((bary >> 10) & 1023u) * (1.0 / 1024.0);
    if (b1 + b2 > 1.0) { b1 = 1.0 - b1; b2 = 1.0 - b2; }
    float3 x = p0 + e1 * b1 + e2 * b2;
    float3 toL = x - p;
    float d2 = dot(toL, toL);
    if (d2 < 1e-8) return false;
    dist = sqrt(d2);
    L = toL / dist;
    float cosL = -dot(nl, L);
    if ((flags & 1u) != 0u) cosL = abs(cosL);
    if (cosL <= 1e-4) return false;
    float3 le = ToLight(float3(f16tof32(t1.w), f16tof32(t1.w >> 16), f16tof32(t2.w)));
    c = le * (_EmissiveParams.y * cosL / max(d2, _EmissiveParams.z));
    return true;
}

float3 UniformSphere(float u1, float u2)
{
    float z = 1.0 - 2.0 * u1;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = 6.28318530718 * u2;
    return float3(r * cos(phi), z, r * sin(phi));
}

float3 FroxelToWorld(float3 g)
{
    float2 ndc = g.xy / _PLGrid.xy * 2.0 - 1.0;
    float3 dir = UltraRTCameraRay(_PLInvViewProj, ndc, _CamPos.xyz);
    float depth = _PLDepth.x * exp2(g.z / _PLGrid.z * _PLDepth.y);
    return _CamPos.xyz + dir * (depth / max(dot(dir, _PLCamFwd.xyz), 1e-3));
}

[numthreads(4, 4, 4)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint3 dim = (uint3)_PLGrid.xyz;
    if (id.x >= dim.x || id.y >= dim.y || id.z >= dim.z) return;

    uint rng = HashU32(id.x * 73856093u ^ id.y * 19349663u ^ id.z * 83492791u ^ (_PLInt0.x * 2654435761u)) | 1u;
    float3 p = FroxelToWorld((float3)id + float3(Rand(rng), Rand(rng), Rand(rng)));

    // ---- Direct: RIS over scene lights + emissive triangles
    uint risN = _PLInt0.z;
    uint emN = _PLInt1.y;
    float share = emN > 0u ? _EmissiveParams.x : 0.0;
    if (risN == 0u) share = emN > 0u ? 1.0 : 0.0;

    uint pickLight = ULTRART_NO_LIGHT;
    float wSum = 0.0, pickM = 0.0, pickPHat = 0.0, pickDist = 0.0;
    float3 pickC = 0, pickL = 0;

    if (risN > 0u || share > 0.0)
    {
        int cands = max((int)_PLParams.x, 1);
        for (int k = 0; k < cands; k++)
        {
            float u = Rand(rng);
            float3 L = 0, c = 0;
            float dist = 0, w = 0.0, pHat = 0.0;
            uint code = ULTRART_NO_LIGHT;
            if (u < share)
            {
                float x = (u / share) * (float)emN;
                uint slot = min((uint)x, emN - 1u);
                uint4 e = _EmissiveAlias[slot];
                bool keep = (x - (float)slot) < asfloat(e.x);
                uint tri = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
                float pdf = asfloat(keep ? e.z : e.w);
                uint bary = min((uint)(Rand(rng) * 1024.0), 1023u) | (min((uint)(Rand(rng) * 1024.0), 1023u) << 10);
                code = ULTRART_EMISSIVE | tri;
                float area;
                if (pdf > 0.0 && EmissiveEvalIso(tri, bary, p, L, dist, c, area))
                {
                    pHat = LightLuminance(c);
                    w = pHat * area / (share * pdf);
                }
            }
            else
            {
                float x = saturate((u - share) / max(1.0 - share, 1e-6)) * (float)risN;
                uint slot = min((uint)x, risN - 1u);
                uint4 e = _LightAlias[slot];
                bool keep = (x - (float)slot) < asfloat(e.x);
                uint li = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
                float pdf = asfloat(keep ? e.z : e.w);
                code = li;
                if (pdf > 0.0 && LightEvalIso(li, p, L, dist, c))
                {
                    pHat = LightLuminance(c);
                    w = pHat / ((1.0 - share) * pdf);
                }
            }
            wSum += w;
            pickM += 1.0;
            if (w > 0.0 && Rand(rng) * wSum < w)
            {
                pickLight = code;
                pickPHat = pHat;
                pickC = c;
                pickL = L;
                pickDist = dist;
            }
        }
    }

    float3 direct = 0;
    float W = (pickPHat > 0.0 && pickM > 0.0) ? wSum / (pickM * pickPHat) : 0.0;
    if (W > 0.0)
    {
        uint mask = 0x05u;
        float dist = pickDist;
        float lw = 0.0;
        if ((pickLight & ULTRART_EMISSIVE) != 0u)
        {
            dist = dist * 0.999 - 0.02;
        }
        else
        {
            float4 l0 = _Lights[pickLight * 4];
            float4 l3 = _Lights[pickLight * 4 + 3];
            lw = l3.w;
            if (l0.w > 0.0)
            {
                dist -= min(0.35, 0.5 * dist);
                if ((l3.z > 0.1 && l3.z < 0.5) || _PLAmbient.w < 0.5) mask = 0x01u;
            }
        }
        if (LightRayVisible(lw, p, pickL, dist, mask)) direct = pickC * W;
    }

    // ---- Indirect: uniform sphere RayQuery -> sky on miss, SHaRC radiance cache on hit
    float3 indirect = 0;
    if (_PLParams.w > 0.5)
    {
        RayDesc ray;
        ray.Origin = p;
        ray.Direction = UniformSphere(Rand(rng), Rand(rng));
        ray.TMin = 0.0;
        ray.TMax = 1.0e4;
        RayQuery<RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_FORCE_OPAQUE> q;
        q.TraceRayInline(_Scene, 0u, ULTRART_MASK_SCENE, ray);
        q.Proceed();
        if (q.CommittedStatus() == COMMITTED_NOTHING)
        {
            indirect = SkyLightRadiance(ray.Direction);
        }
        else
        {
            float3 hp = ray.Origin + ray.Direction * q.CommittedRayT();
            float3 total;
            if (CacheQueryTotal(hp, -ray.Direction, total))
                indirect = total * 0.45;
        }
        indirect *= _PLParams.y;
    }

    float3 sum = direct + indirect + ToLight(_PLAmbient.rgb);
    float lum = LightLuminance(sum);
    if (lum > _PLParams.z && _PLParams.z > 0.0) sum *= _PLParams.z / lum;
    if (any(!(sum >= 0.0)) || any(isinf(sum))) sum = 0;
    _PLRaw[id] = float4(sum, 1.0);
}
