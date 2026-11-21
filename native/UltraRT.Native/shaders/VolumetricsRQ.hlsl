// Native D3D12 cs_6_5 inline RayQuery volumetric scattering pass (Wave 3).
// 1:1 port of Volumetrics.raytrace (VolumeRayGen) using inline RayQuery against Unity's captured scene TLAS,
// eliminating Unity's per-DispatchRays shader-table rebuild (~0.45-0.50 ms RT-CPU in 8-x).
// Used when the scene has no stained glass (stained-glass scenes keep Volumetrics.raytrace for per-pane tint).

RaytracingAccelerationStructure _Scene : register(t0);

cbuffer VolCB : register(b0)
{
    column_major float4x4 _VolInvViewProj;
    float4 _VolCamPos;
    float4 _VolDim;          // w, h, 1/w, 1/h
    float4 _VolMedium;       // rgb sigma_s, w sigma_t
    float4 _VolParams;       // x steps, y maxDist, z HG g, w frame
    float4 _VolSunDir;       // xyz toward sun, w cos(angular radius)
    float4 _VolSunColor;     // rgb linear irradiance, w active (0/1)
    float4 _VolLightParams;  // x localLightCount, y candidates, z scale, w minDist2
    float4 _VolFalloff;      // x relight falloff blend, y light exposure, z linear transport (0/1), w preLinearLights (0/1)
    float4 _VolLightShape;   // x hazeRange, y soft core, z emitterMedium, w unused
    float4 _VolSamples;      // x lightSamples, y emissiveSamples, z beamCount, w beamRays
    float4 _EmissiveParams;  // x share, y EmissiveLightScale/pi, z minDist2, w unused
    float4 _VolMisc;         // x _VolEmissive, y _VolClamp, z _EmissiveCount, w _EmissiveTriCount
};

Texture2D<float>         _VolDepth       : register(t1);
StructuredBuffer<float4> _Lights         : register(t2);
StructuredBuffer<uint>   _VolSkip        : register(t3);
StructuredBuffer<float4> _VolLightCone   : register(t4);
StructuredBuffer<float4> _VolBeams       : register(t5);
StructuredBuffer<uint4>  _EmissiveTris   : register(t6);
StructuredBuffer<float4> _EmissiveOwners : register(t7);
StructuredBuffer<uint4>  _EmissiveAlias  : register(t8);
StructuredBuffer<uint>   _EmissiveActive : register(t9);

RWTexture2D<float4> _VolOut     : register(u0);
RWTexture2D<float4> _VolBeamOut : register(u1);

static const float kPi = 3.14159265;
#define BEAM_F4 21

float3 UltraRTCameraRay(float4x4 invViewProj, float2 ndc, float3 camPos)
{
    float4 h = mul(invViewProj, float4(ndc, 1.0, 1.0));
    return normalize(h.xyz - camPos * h.w);
}

uint Pcg(inout uint s)
{
    s = s * 747796405u + 2891336453u;
    uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return (w >> 22u) ^ w;
}
float Rand(inout uint s) { return Pcg(s) * (1.0 / 4294967296.0); }

float BlueOffset(uint2 id, uint frame)
{
    float2 p = (float2)id + 5.588238 * (float)(frame & 63u);
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

float PhaseHG(float cosT, float g)
{
    float d = 1.0 + g * g - 2.0 * g * cosT;
    return (1.0 - g * g) / (4.0 * kPi * d * sqrt(max(d, 1e-6)));
}

bool Unoccluded(float3 p, float3 dir, float dist, uint mask = 0x05u)
{
    RayDesc r;
    r.Origin = p;
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = max(0.0, dist);
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_CULL_NON_OPAQUE> q;
    q.TraceRayInline(_Scene, 0u, mask, r);
    q.Proceed();
    return q.CommittedStatus() == COMMITTED_NOTHING;
}

void ScatterCone(uint i, out float3 axis, out float cosHalf, out float sharp)
{
    float4 o = _VolLightCone[i * 2];
    if (o.w > -0.5) { axis = o.xyz; cosHalf = o.w; sharp = _VolLightCone[i * 2 + 1].y; return; }
    axis = _Lights[i * 4 + 2].xyz;
    cosHalf = _Lights[i * 4 + 3].x;
    sharp = _Lights[i * 4 + 3].y;
}
float ScatterWeight(uint i) { return _VolLightCone[i * 2 + 1].x; }

float3 ToLightV(float3 g) { return _VolFalloff.z > 0.5 ? pow(max(g, 0.0), 2.2) : g; }

float HazeWeight(float range)
{
    if (_VolLightShape.x <= 0.0) return 1.0;
    float x = range / _VolLightShape.x;
    return 1.0 / (1.0 + x * x * x);
}

float CoreR2(float range) { float r = _VolLightShape.y * range; return r * r; }

float3 LocalLight(uint i, float3 x, out float3 L, out float dist)
{
    float4 l0 = _Lights[i * 4];
    float4 l1 = _Lights[i * 4 + 1];
    float3 toL = l0.xyz - x;
    float rawD2 = dot(toL, toL);
    float d2 = max(rawD2 + CoreR2(l0.w), _VolLightParams.w);
    float d = sqrt(d2);
    dist = sqrt(rawD2);
    L = toL / max(dist, 1e-4);
    float r = max(l0.w, 1e-6);
    float game = (1.0 / (1.0 + d2 * 25.0 / (r * r))) * saturate(2.0 - d / r);
    float rRef = 0.5 * r;
    float xr = d / (2.0 * r);
    float window = saturate(1.0 - xr * xr * xr * xr);
    float3 coneAxis;
    float coneCos, coneSharp;
    ScatterCone(i, coneAxis, coneCos, coneSharp);
    float spot = saturate((max(dot(L, -coneAxis), 0.0) - coneCos) * coneSharp);
    float atten;
    if (_VolFalloff.z > 0.5)
    {
        float gameL = pow(game, 2.2);
        float phys = 0.012820 * (rRef * rRef) / max(d2, 0.01) * window * window;
        phys = min(phys, pow(3.0 * game + 0.05, 2.2));
        phys = lerp(gameL, phys, saturate(_VolFalloff.x));
        atten = lerp(min(phys, gameL), phys, 1.0 - 0.25 * floor(l1.w * 0.5)) * pow(spot, 2.2);
    }
    else
    {
        float phys = (1.0 / 7.25) * (rRef * rRef) / max(d2, 0.01) * window * window;
        phys = min(phys, 3.0 * game + 0.05);
        phys = lerp(game, phys, saturate(_VolFalloff.x));
        atten = lerp(min(phys, game), phys, 1.0 - 0.25 * floor(l1.w * 0.5)) * spot;
    }
    float3 lc;
    if (_VolFalloff.z > 0.5 && _VolFalloff.w > 0.5)
    {
        float sLin = 1.0;
        if (_VolFalloff.y != 1.0) sLin = pow(max(_VolFalloff.y, 0.0), 2.2);
        lc = l1.rgb * sLin;
    }
    else
    {
        lc = ToLightV(l1.rgb * _VolFalloff.y);
    }
    return lc * atten * HazeWeight(r) * ScatterWeight(i);
}

bool ClipCone(float3 apex, float3 a, float c, float3 o, float3 dir, inout float tA, inout float tB)
{
    float3 v = o - apex;
    float da = dot(dir, a), va = dot(v, a), vd = dot(v, dir), vv = dot(v, v);
    float A = da * da - c * c, B = va * da - c * c * vd, C = va * va - c * c * vv;
    float r0 = tA, r1 = tA;
    if (abs(A) > 1e-6)
    {
        float disc = B * B - A * C;
        if (disc > 0.0)
        {
            float sq = sqrt(disc);
            float x0 = (-B - sq) / A, x1 = (-B + sq) / A;
            r0 = min(x0, x1); r1 = max(x0, x1);
        }
    }
    else if (abs(B) > 1e-9) { r0 = -C / (2.0 * B); r1 = r0; }
    r0 = clamp(r0, tA, tB);
    r1 = clamp(r1, tA, tB);
    float s[4] = { tA, r0, r1, tB };
    float lo = 1e30, hi = -1e30;
    [unroll] for (int k = 0; k < 3; k++)
    {
        if (s[k + 1] <= s[k]) continue;
        float3 x = v + dir * (0.5 * (s[k] + s[k + 1]));
        if (dot(x, a) >= c * length(x)) { lo = min(lo, s[k]); hi = max(hi, s[k + 1]); }
    }
    if (!(hi > lo)) return false;
    tA = lo; tB = hi;
    return true;
}

bool ClipSpot(uint i, float3 o, float3 dir, inout float tA, inout float tB)
{
    float3 a;
    float c, sharpUnused;
    ScatterCone(i, a, c, sharpUnused);
    if (c <= -0.5) return true;
    return ClipCone(_Lights[i * 4].xyz, a, c, o, dir, tA, tB);
}

bool LightSegment(uint i, float3 o, float3 dir, float tMax, out float tA, out float tB)
{
    float4 l0 = _Lights[i * 4];
    float reach = 2.0 * l0.w;
    float delta = dot(l0.xyz - o, dir);
    float3 perp = l0.xyz - (o + dir * delta);
    float h2 = dot(perp, perp);
    float k2 = reach * reach - h2;
    tA = 0; tB = 0;
    if (k2 <= 0.0) return false;
    float k = sqrt(k2);
    tA = max(delta - k, 0.0);
    tB = min(delta + k, tMax);
    if (!(tB > tA)) return false;
    return ClipSpot(i, o, dir, tA, tB);
}

float LightTarget(uint i, float3 o, float3 dir, float tMax)
{
    float4 l0 = _Lights[i * 4];
    if (l0.w == 0.0 || _Lights[i * 4 + 3].z > 0.5) return 0.0;
    if ((_VolSkip[i >> 5] >> (i & 31u)) & 1u) return 0.0;
    float3 c = _Lights[i * 4 + 1].rgb;
    float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
    if (lum <= 0.0) return 0.0;
    float tA, tB;
    if (!LightSegment(i, o, dir, tMax, tA, tB)) return 0.0;
    float delta = dot(l0.xyz - o, dir);
    float3 perp = l0.xyz - (o + dir * delta);
    float h = max(sqrt(dot(perp, perp) + CoreR2(l0.w)), 0.05);
    float angle = atan((tB - delta) / h) - atan((tA - delta) / h);
    float rRef = 0.5 * l0.w;
    return lum * rRef * rRef * angle / h * HazeWeight(l0.w) * ScatterWeight(i);
}

bool SampleEmissivePoint(inout uint rng, out float3 x, out float3 nl, out bool twoSided, out float3 le, out float pdfA)
{
    x = 0; nl = 0; twoSided = false; le = 0; pdfA = 0;
    int emCount = (int)_VolMisc.z;
    int emTriCount = (int)_VolMisc.w;
    if (emCount <= 0) return false;
    float u = Rand(rng) * (float)emCount;
    uint slot = min((uint)u, (uint)emCount - 1u);
    uint4 e = _EmissiveAlias[slot];
    bool keep = (u - (float)slot) < asfloat(e.x);
    uint tri = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
    float pdf = asfloat(keep ? e.z : e.w);
    if (tri >= (uint)emTriCount) return false;
    if (((_EmissiveActive[tri >> 5] >> (tri & 31u)) & 1u) == 0u || !(pdf > 0.0)) return false;
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
    nl = nc / len;
    if ((determinant(m) < 0.0) != ((flags & 2u) != 0u)) nl = -nl;
    twoSided = (flags & 1u) != 0u;
    float b1 = Rand(rng), b2 = Rand(rng);
    if (b1 + b2 > 1.0) { b1 = 1.0 - b1; b2 = 1.0 - b2; }
    x = p0 + e1 * b1 + e2 * b2;
    le = ToLightV(float3(f16tof32(t1.w), f16tof32(t1.w >> 16), f16tof32(t2.w))) * _EmissiveParams.y;
    le *= lerp(1.0, f16tof32(t2.w >> 16), saturate(_VolLightShape.z));
    pdfA = pdf / (0.5 * len);
    return true;
}

float Equiangular(float3 o, float3 dir, float tStart, float tEnd, float3 lp, float u, out float pdf, float core2 = 0.0)
{
    float delta = dot(lp - o, dir);
    float3 perp = lp - (o + dir * delta);
    float h = max(sqrt(dot(perp, perp) + core2), 0.05);
    float thA = atan((tStart - delta) / h), thB = atan((tEnd - delta) / h);
    float t = clamp(delta + h * tan(lerp(thA, thB, u)), tStart, tEnd);
    pdf = h / (max(thB - thA, 1e-6) * (h * h + (t - delta) * (t - delta)));
    return t;
}

float3 LightSample(uint i, float w, float uT, float3 o, float3 dir, float tEnd, float3 sigmaS, float sigmaT)
{
    float pdf, tA, tB;
    LightSegment(i, o, dir, tEnd, tA, tB);
    float t = Equiangular(o, dir, tA, tB, _Lights[i * 4].xyz, uT, pdf, CoreR2(_Lights[i * 4].w));
    float3 x = o + dir * t;
    float3 L;
    float dist;
    float3 Li = LocalLight(i, x, L, dist);
    if (!any(Li > 0.0) || !(pdf > 0.0)) return 0;
    float clear = min(0.35, 0.5 * dist);
    float l3z = _Lights[i * 4 + 3].z;
    if (!Unoccluded(x, L, dist - clear, (l3z > 0.1 && l3z < 0.5) ? 0x01u : 0x05u)) return 0;
    float trans = exp(-sigmaT * (t + dist));
    return Li * sigmaS * (trans * PhaseHG(dot(dir, L), _VolParams.z) / pdf) * w;
}

float3 EmissiveSample(inout uint rng, float uT, float3 o, float3 dir, float tEnd, float3 sigmaS, float sigmaT)
{
    float3 bx = 0, bn = 0, ble = 0;
    bool b2 = false;
    float wSum = 0.0, bTarget = 0.0;
    const int M = 6;
    for (int c = 0; c < M; c++)
    {
        float3 xe, nl, le;
        bool twoSided;
        float pdfA;
        if (!SampleEmissivePoint(rng, xe, nl, twoSided, le, pdfA)) continue;
        float delta = dot(xe - o, dir);
        float3 closest = o + dir * clamp(delta, 0.0, tEnd);
        float h = max(length(xe - (o + dir * delta)), 0.05);
        float angle = atan((tEnd - delta) / h) - atan((0.0 - delta) / h);
        float s0 = dot(nl, o - xe), s1 = dot(nl, o + dir * tEnd - xe);
        if (!twoSided && s0 <= 0.0 && s1 <= 0.0) continue;
        float cosL = abs(dot(nl, normalize(closest - xe + 1e-5)));
        float target = dot(le, float3(0.2126, 0.7152, 0.0722)) * max(cosL, 0.05) * angle / h;
        if (!(target > 0.0)) continue;
        float w = target / pdfA;
        wSum += w;
        if (Rand(rng) * wSum < w) { bx = xe; bn = nl; ble = le; b2 = twoSided; bTarget = target; }
    }
    if (!(bTarget > 0.0)) return 0;
    float W = wSum / (M * bTarget);
    float pdfT;
    float t = Equiangular(o, dir, 0.0, tEnd, bx, uT, pdfT);
    float3 x = o + dir * t;
    float3 toE = bx - x;
    float d2 = dot(toE, toE);
    float dist = sqrt(d2);
    float3 L = toE / max(dist, 1e-4);
    float cosL = -dot(bn, L);
    if (b2) cosL = abs(cosL);
    if (!(cosL > 1e-4) || !(pdfT > 0.0)) return 0;
    if (!Unoccluded(x, L, dist * 0.999 - 0.02)) return 0;
    float3 li = ble * (cosL / max(d2, _EmissiveParams.z));
    float trans = exp(-sigmaT * (t + dist));
    float3 em = li * sigmaS * (trans * PhaseHG(dot(dir, L), _VolParams.z) / pdfT) * W * _VolMisc.x;
    float volClamp = _VolMisc.y;
    float lumE = dot(em, float3(0.2126, 0.7152, 0.0722)) * kPi;
    if (volClamp > 0.0 && lumE > 0.25 * volClamp) em *= 0.25 * volClamp / lumE;
    return em;
}

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

float2 BeamVisibility(float3 o, float3 dir, float tEnd, float u)
{
    float2 acc = 0;
    int nb = clamp((int)_VolSamples.z, 0, 32);
    int ns = clamp((int)_VolSamples.w, 1, 4);
    for (int h = 0; h < nb; h++)
    {
        float t0, t1;
        uint p0, p1;
        if (!BeamHullInterval(h, o, dir, t0, t1, p0, p1)) continue;
        float4 b0 = _VolBeams[h * BEAM_F4];
        uint flags = (uint)b0.w;
        float4 g0 = _VolBeams[h * BEAM_F4 + 13 + p0], g1 = _VolBeams[h * BEAM_F4 + 13 + p1];
        float faces = 0.0;
        if ((flags & 1u) != 0u && t0 > 0.0 && t0 < tEnd) faces += saturate(g0.w + dot(g0.xyz, o + dir * t0));
        if ((flags & 2u) != 0u && t1 < tEnd) faces += saturate(g1.w + dot(g1.xyz, o + dir * t1));
        float w = dot(b0.rgb, float3(0.2126, 0.7152, 0.0722)) * faces;
        if (!(w > 0.0)) continue;
        float a = max(t0, 0.0), b = min(t1, tEnd);
        if (!(b > a)) continue;
        float4 lp = _VolBeams[h * BEAM_F4 + 2];
        float4 ax = _VolBeams[h * BEAM_F4 + 3];
        float lit = 0.0;
        for (int k = 0; k < ns; k++)
        {
            float t = lerp(a, b, frac(u + 0.381966 * h + (float)k / ns));
            float3 x = o + dir * t;
            float3 toL = lp.xyz - x;
            float dist = length(toL);
            float3 L = toL / max(dist, 1e-4);
            float along = dot(-toL, ax.xyz);
            float reach = dist * saturate(1.0 - ax.w / max(along, 1e-3));
            if (Unoccluded(x, L, reach, 0x01u)) lit += 1.0;
        }
        acc += float2(w * lit / ns, w);
    }
    return acc;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    uint2 dim = (uint2)_VolDim.xy;
    if (tid.x >= dim.x || tid.y >= dim.y) return;
    uint2 id = tid.xy;
    uint frame = (uint)_VolParams.w;
    float tEnd = _VolDepth[id];
    if (tEnd <= 0.0) { _VolOut[id] = 0; _VolBeamOut[id] = 0; return; }
    float tBeam = tEnd;
    tEnd = min(tEnd, _VolParams.y);

    float2 ndc = ((float2)id + 0.5) * _VolDim.zw * 2.0 - 1.0;
    float3 o = _VolCamPos.xyz;
    float3 dir = UltraRTCameraRay(_VolInvViewProj, ndc, o);

    uint rng = (id.x * 1973u + id.y * 9277u + frame * 26699u) | 1u;
    float3 sigmaS = _VolMedium.rgb;
    float sigmaT = _VolMedium.w;
    float3 sum = 0;

    if (_VolSunColor.w > 0.5)
    {
        int steps = max((int)_VolParams.x, 1);
        float u = BlueOffset(id, frame);
        float phase = PhaseHG(dot(dir, _VolSunDir.xyz), _VolParams.z);
        float dt = tEnd / steps;
        float segLen = sigmaT * dt < 1e-3 ? dt * (1.0 - 0.5 * sigmaT * dt) : (1.0 - exp(-sigmaT * dt)) / sigmaT;
        float3 sunT = _VolSunDir.xyz;
        float3 tt = abs(sunT.y) < 0.999 ? normalize(cross(sunT, float3(0, 1, 0))) : float3(1, 0, 0);
        float3 bb = cross(sunT, tt);
        float lit = 0.0;
        for (int s = 0; s < steps; s++)
        {
            float t0 = s * dt;
            float t = t0 + u * dt;
            float3 x = o + dir * t;
            float cosT = 1.0 - Rand(rng) * (1.0 - _VolSunDir.w);
            float sinT = sqrt(max(0.0, 1.0 - cosT * cosT));
            float phi = Rand(rng) * 6.28318530718;
            float3 L = normalize(tt * (sinT * cos(phi)) + bb * (sinT * sin(phi)) + sunT * cosT);
            float w = exp(-sigmaT * t0) * segLen;
            if (Unoccluded(x, L, 1.0e4)) lit += w;
        }
        sum += _VolSunColor.rgb * (phase * lit) * sigmaS;
    }

    float3 local = 0;
    int count = (int)_VolLightParams.x;
    int nL = clamp((int)_VolSamples.x, 1, 8);
    if (count > 0 && _VolLightParams.z > 0.0)
    {
        float uT0 = BlueOffset(id.yx, frame + 17u);
        if (count <= 128)
        {
            if (nL == 1)
            {
                float total = 0.0;
                uint pick = 0xFFFFFFFFu;
                float pickT = 0.0;
                for (int i = 0; i < count; i++)
                {
                    float ti = LightTarget(i, o, dir, tEnd);
                    if (!(ti > 0.0)) continue;
                    total += ti;
                    if (Rand(rng) * total < ti) { pick = (uint)i; pickT = ti; }
                }
                if (pick != 0xFFFFFFFFu)
                    local += LightSample(pick, total / pickT, uT0, o, dir, tEnd, sigmaS, sigmaT);
            }
            else
            {
                float total = 0.0;
                uint picks[8];
                float pickTs[8];
                [unroll] for (int m = 0; m < 8; m++) { picks[m] = 0xFFFFFFFFu; pickTs[m] = 0.0; }
                for (int i = 0; i < count; i++)
                {
                    float ti = LightTarget(i, o, dir, tEnd);
                    if (!(ti > 0.0)) continue;
                    total += ti;
                    for (int k = 0; k < nL; k++)
                    {
                        if (Rand(rng) * total < ti) { picks[k] = (uint)i; pickTs[k] = ti; }
                    }
                }
                for (int k = 0; k < nL; k++)
                {
                    if (picks[k] != 0xFFFFFFFFu)
                        local += LightSample(picks[k], total / pickTs[k], frac(uT0 + 0.618034 * k), o, dir, tEnd, sigmaS, sigmaT);
                }
            }
        }
        else
        {
            int cands = max((int)_VolLightParams.y, 1);
            for (int k = 0; k < nL; k++)
            {
                uint pick = 0xFFFFFFFFu;
                float wSum = 0.0, pickTarget = 0.0;
                for (int c = 0; c < cands; c++)
                {
                    uint i = min((uint)(Rand(rng) * count), (uint)count - 1u);
                    float target = LightTarget(i, o, dir, tEnd);
                    if (target <= 0.0) continue;
                    wSum += target;
                    if (Rand(rng) * wSum < target) { pick = i; pickTarget = target; }
                }
                if (pick != 0xFFFFFFFFu)
                    local += LightSample(pick, count * wSum / (cands * pickTarget), frac(uT0 + 0.618034 * k), o, dir, tEnd, sigmaS, sigmaT);
            }
        }
        local *= _VolLightParams.z / (float)nL;
    }

    if (_VolMisc.x > 0.0 && (int)_VolMisc.z > 0)
    {
        int nE = clamp((int)_VolSamples.y, 1, 8);
        float uE0 = BlueOffset(id.yx, frame + 41u);
        float3 emSum = 0;
        for (int k = 0; k < nE; k++)
            emSum += EmissiveSample(rng, frac(uE0 + 0.618034 * k), o, dir, tEnd, sigmaS, sigmaT);
        local += emSum / (float)nE;
    }
    float volClamp = _VolMisc.y;
    float lumLocal = dot(local, float3(0.2126, 0.7152, 0.0722)) * kPi;
    if (lumLocal > volClamp && volClamp > 0.0) local *= volClamp / lumLocal;
    sum += local;

    sum *= kPi;
    float2 beam = _VolSamples.z > 0.5 ? BeamVisibility(o, dir, tBeam, BlueOffset(id, frame + 53u)) : float2(0, 0);
    if (any(!(beam >= 0.0)) || any(isinf(beam))) beam = 0;
    _VolBeamOut[id] = float4(beam, 0, 1);
    if (any(!(sum >= 0.0)) || any(isinf(sum))) sum = 0;
    _VolOut[id] = float4(min(sum, 64.0), 1.0);
}
