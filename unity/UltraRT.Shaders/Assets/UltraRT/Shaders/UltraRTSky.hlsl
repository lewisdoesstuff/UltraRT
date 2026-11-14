#ifndef ULTRART_SKY_INCLUDED
#define ULTRART_SKY_INCLUDED

// Sky radiance and sky light sampling (docs/relight.md, "Sky and GI"). Shared by PathTrace.raytrace and
// CacheUpdate.raytrace; include after UltraRTLights.hlsl (Visible, ToLight, _SkyLight) and after Rand().
//
// The sky is the level's panoramic sky texture (lat-long), or the flat _SkyColor when there is none. Sky light is
// estimated two ways and combined with the balance heuristic (MIS):
//   SkyDirect      one direction drawn from a luminance-weighted table over the sky (_SkyAlias), one visibility ray.
//   SkyMissWeight  the weight of a cosine-sampled bounce ray that escapes to the sky.
// Both together are unbiased; with sampling off (_SkyLight.z = 0) the weight is 1 and only the bounce ray sees the sky.

float4    _SkyColor;      // radiance for rays escaping the level when there is no sky texture
Texture2D _SkyTex;        // the level's panoramic sky (lat-long), e.g. LimboSkybox/SkySphere
SamplerState sampler_linear_repeat_SkyTex;
float4    _SkyTint;       // tint * exposure * colour-space double, rgb
float4    _SkyParams;     // x: has texture, y: rotation (radians), z: table width, w: table height (0 = no table)
// Sky table: one entry per cell of a W x H lat-long grid (row 0 = straight down), as the light alias table (Vose):
// x asuint(threshold), y cell | aliasCell << 16, z asuint(P(cell)), w asuint(P(aliasCell)). Entry k's own cell is k,
// so z is also the probability of cell k for the pdf lookup.
StructuredBuffer<uint4> _SkyAlias;

static const float kSkyPi = 3.14159265;

float3 SkyRotate(float3 d, float sgn)
{
    float s = sin(_SkyParams.y) * sgn, c = cos(_SkyParams.y);
    return float3(c * d.x - s * d.z, d.y, s * d.x + c * d.z);
}

float2 SkyUV(float3 d)
{
    d = SkyRotate(d, 1.0);
    return float2(atan2(d.x, d.z) * (0.5 / kSkyPi) + 0.5, asin(clamp(d.y, -1.0, 1.0)) * (1.0 / kSkyPi) + 0.5);
}

// The sky as the game draws it (gamma game units): primary rays, reflections.
float3 SkyRadiance(float3 d)
{
    if (_SkyParams.x < 0.5) return _SkyColor.rgb;
    return _SkyTex.SampleLevel(sampler_linear_repeat_SkyTex, SkyUV(d), 0).rgb * _SkyTint.rgb;
}

// The sky as a light (lighting space: linear in linear transport), times the relight sky scale.
float3 SkyLightRadiance(float3 d)
{
    return ToLight(SkyRadiance(d)) * (_SkyLight.w > 0.0 ? _SkyLight.w : 1.0);
}

// ULTRART_SKY_NO_NEE (CacheUpdate): no direct sky samples; cosine misses carry the full sky (weight 1). The cache
// averages hundreds of paths per cell, so the extra ray would cost more than the variance it saves.
#ifdef ULTRART_SKY_NO_NEE
bool SkySampling() { return false; }
#else
bool SkySampling() { return _SkyLight.z > 0.5 && _SkyParams.z >= 1.0 && _SkyParams.w >= 1.0; }
#endif

// Solid angle of row y of the table (all cells of a row are equal).
float SkyCellSolidAngle(uint y)
{
    float h = _SkyParams.w;
    float lat0 = ((float)y / h - 0.5) * kSkyPi, lat1 = ((float)(y + 1u) / h - 0.5) * kSkyPi;
    return (2.0 * kSkyPi / _SkyParams.z) * (sin(lat1) - sin(lat0));
}

// pdf (solid angle) of SampleSky drawing direction d.
float SkyPdf(float3 d)
{
    if (!SkySampling()) return 0.0;
    uint w = (uint)_SkyParams.z, h = (uint)_SkyParams.w;
    float2 uv = SkyUV(d);
    uint x = min((uint)(frac(uv.x) * w), w - 1u), y = min((uint)(saturate(uv.y) * h), h - 1u);
    return asfloat(_SkyAlias[y * w + x].z) / max(SkyCellSolidAngle(y), 1e-8);
}

// A direction drawn from the sky table: a cell by its probability, then uniform in solid angle within the cell.
float3 SampleSky(float u0, float u1, float u2, out float pdf)
{
    uint w = (uint)_SkyParams.z, h = (uint)_SkyParams.w;
    uint n = w * h;
    float x = u0 * (float)n;
    uint slot = min((uint)x, n - 1u);
    uint4 e = _SkyAlias[slot];
    bool keep = (x - (float)slot) < asfloat(e.x);
    uint cell = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
    float p = asfloat(keep ? e.z : e.w);
    uint cx = cell % w, cy = cell / w;
    float lat0 = ((float)cy / h - 0.5) * kSkyPi, lat1 = ((float)(cy + 1u) / h - 0.5) * kSkyPi;
    float sy = lerp(sin(lat0), sin(lat1), u2);
    float lon = (((float)cx + u1) / w - 0.5) * 2.0 * kSkyPi;
    float r = sqrt(max(0.0, 1.0 - sy * sy));
    pdf = p / max(SkyCellSolidAngle(cy), 1e-8);
    return SkyRotate(float3(r * sin(lon), sy, r * cos(lon)), -1.0);
}

// Sky light arriving at (p, n), cosine-weighted (same units as a cosine-sampled bounce ray: integral of L cos / pi):
// one table sample and one visibility ray, MIS-weighted against cosine sampling. Sky walls (0x02) don't block it.
float3 SkyDirect(float3 p, float3 n, inout uint rng)
{
    if (!SkySampling()) return 0;
    float pdf;
    float3 L = SampleSky(Rand(rng), Rand(rng), Rand(rng), pdf);
    float pc = dot(n, L) * (1.0 / kSkyPi);
    if (pc <= 0.0 || !(pdf > 0.0)) return 0;
    if (!Visible(p, n, L, 1.0e4)) return 0;
    return SkyLightRadiance(L) * (pc / (pdf + pc));
}

// MIS weight of a cosine-sampled ray from a surface with normal n that escaped to the sky in direction d.
float SkyMissWeight(float3 n, float3 d)
{
    if (!SkySampling()) return 1.0;
    float pc = max(dot(n, d), 0.0) * (1.0 / kSkyPi);
    return pc / max(pc + SkyPdf(d), 1e-8);
}

#endif
