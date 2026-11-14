#ifndef ULTRART_LIGHTS_INCLUDED
#define ULTRART_LIGHTS_INCLUDED

// Direct lighting for PathTrace.raytrace: ULTRAKILL's light model plus light selection.
//   _LightSampling 0: reference. Every light, one shadow ray each (DirectAll), uniform pick at the bounce.
//   _LightSampling 1: RIS. M candidates from an alias table, one shadow ray for the winner.
//   _LightSampling 2: RIS + temporal reuse (ReSTIR DI, biased 1/M combine). One shadow ray per pixel.
// Unshadowed fill lights are always evaluated exhaustively (they are few and need no rays).
// Emissive triangles (EmissiveLights.cs) are a second kind of RIS candidate: a point on a triangle of an emissive
// surface. In reference mode they get _EmissiveRefSamples extra shadow rays per hit instead.
// See docs/light-sampling.md and docs/emissive-lights.md.
//
// The includer must declare, before this file: RayPayload (UltraRTCommon.hlsl), _Scene, Rand(inout uint)
// and _LightScale.

// Per light, 4 x float4:
//   [0] position (or -forward for directional), range (0 = directional)
//   [1] color * intensity, unused (culling masks live in _LightMasks)
//   [2] forward, soft-shadow radius (m; 0 = _LightRadius; < 0 upper half-sphere). Lights lifted out of their fixture
//       get the fixture's size.
//   [3] cos(spot/2) (-1 for non-spot), 1 / (cos(spot/4) - cos(spot/2)), unshadowed (1 = fill light),
//       hybrid weight: > 0 for lights the game doesn't draw (EffectLights' virtual lights for glowing effects); hybrid
//       adds that much of their shadowed light on top of the game's image, like emissive surfaces
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
// Reservoir per pixel (docs/emissive-lights.md, "Reservoir layout"):
//   x: light code (17 bits: index | emissive flag << 16; 0xFFFF = none) | uint(M) << 20 (12 bits)
//   y: asuint(W) with its 10 low mantissa bits replaced by the emissive sample's barycentric u (10 bits)
//   z: oct11x2 normal (22 bits) | barycentric v << 22 (10 bits)
//   w: asuint(dot(normal, pos)): the surface plane
StructuredBuffer<uint4>   _ReservoirPrev;
RWStructuredBuffer<uint4> _ReservoirCur;
float4x4  _PrevViewProj;    // viewProj of the frame that wrote _ReservoirPrev
int       _RestirHistoryValid;
float4    _RestirParams;    // x max history M (<= 4095), y min normal cosine, z plane tolerance (x ray t), w spatial radius (px)
int       _RestirSpatial;   // extra neighbour reservoirs taken from the previous frame (0 = temporal only)

#define ULTRART_NO_LIGHT 0xFFFFu
#define ULTRART_EMISSIVE 0x10000u // light code flag: the low 16 bits index _EmissiveTris

// Emissive triangles (EmissiveLights.cs). Per triangle, 3 x uint4, in the space of its owner (_EmissiveOwners):
//   [0] asuint(p0.xyz), owner | flags << 16 (bit 0 two-sided, bit 1 flip facing)
//   [1] asuint((p1 - p0).xyz), half(Le.r) | half(Le.g) << 16
//   [2] asuint((p2 - p0).xyz), half(Le.b)
// Le is the triangle's average emitted radiance, before _EmissiveParams.y.
StructuredBuffer<uint4>  _EmissiveTris;
StructuredBuffer<float4> _EmissiveOwners;  // 3 rows of localToWorld per owner
StructuredBuffer<uint4>  _EmissiveAlias;   // as _LightAlias, over the active triangles (values index _EmissiveTris)
StructuredBuffer<uint>   _EmissiveActive;  // one bit per triangle: its surface is in the RTAS this frame
int      _EmissiveCount;        // entries in _EmissiveAlias (0 = no emissive candidates)
int      _EmissiveTriCount;     // triangles in _EmissiveTris
int      _EmissiveRefSamples;   // reference mode: emissive samples (one shadow ray each) per primary hit
int      _EmissiveHistoryValid; // 0 when triangle indices changed since the previous frame: drop emissive history
float4   _EmissiveParams;       // x P(a RIS candidate is emissive), y EmissiveLightScale / pi, z min squared distance
float    _EmissiveBounceWeight; // weight of hit emission on bounce rays (0 while emissive triangles are sampled directly)
float    _EmissiveHybridAdd;    // hybrid: emissive direct light added on top of the game's colour (via the bounce channel)

// ---------------------------------------------------------------- light model

// Sky and GI (relight; docs/relight.md, "Sky and GI"). Unbound (zero) = the game's gamma-space model, hard sun shadows,
// no sky sampling.
//   x: 1 = linear light transport. Game colours (light colours, albedo, emission, sky) are gamma encoded; lighting is
//      computed on their linear values (^2.2) and the composites convert the filtered result back (LightToDisplay).
//   y: sun angular radius (radians) for shadowed directional lights; 0 = hard shadows
//   z: 1 = sample the sky directly (UltraRTSky.hlsl, SkyDirect)
//   w: sky light scale (0 = 1)
float4 _SkyLight;

bool   LinearLight() { return _SkyLight.x > 0.5; }
float3 ToLight(float3 g) { return LinearLight() ? pow(max(g, 0.0), 2.2) : g; }
float3 LightToDisplay(float3 l) { return LinearLight() ? pow(max(l, 0.0), 1.0 / 2.2) : l; }

bool VisibleMasked(float3 p, float3 n, float3 dir, float dist, uint mask)
{
    RayDesc r;
    r.Origin = p + n * 0.02;
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = max(0.0, dist - 0.05);
    RayPayload sp = (RayPayload)0;
    sp.t = 1.0; // stays positive if anything is hit; the miss shader sets it negative
    TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, mask, 0, 1, 0, r, sp);
    return sp.t < 0.0;
}

// Geometry + light fixtures (0x01 | 0x04); sky walls (0x02) never block light.
bool Visible(float3 p, float3 n, float3 dir, float dist) { return VisibleMasked(p, n, dir, dist, 0x05u); }

// ULTRAKILL places many lights inside their fixtures (torch bowls, lamp housings). The game has no shadows, so that's
// invisible there; ray traced, the fixture shadows everything below its rim. Shadow rays toward point/spot lights
// therefore stop this far short of the light (capped at half the distance so nearby contact shadows survive).
static const float kLightFixtureClearance = 0.35;
float _LightRadius;      // soft-shadow light size (m); 0 = point lights
float _FixtureShadows;   // 1: light fixtures (torch bowls etc.) shadow their own lights
static uint g_ShadowRng; // seeded per pixel by each ray-generation shader

bool LightVisible(uint i, float3 p, float3 n, float3 L, float dist)
{
    // Soft shadows: aim at a random point within the light's radius (a point light becomes a small sphere).
    // Per-light radius: > 0 a sphere; < 0 the upper half-sphere only (a flame sitting on its bowl: samples never dip
    // below the rim, where the bowl itself would block them).
    float radius = _Lights[i * 4 + 2].w != 0.0 ? _Lights[i * 4 + 2].w : _LightRadius;
    bool upper = radius < 0.0;
    radius = abs(radius);
    if (dist >= 1.0e3 && _SkyLight.y > 0.0)
    {
        // Directional light (the sun): a random direction within its disk, for penumbrae that widen with distance.
        float u1 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float u2 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float cosT = 1.0 - u1 * (1.0 - cos(_SkyLight.y));
        float sinT = sqrt(max(0.0, 1.0 - cosT * cosT));
        float phi = u2 * 6.28318530718;
        float3 t = abs(L.y) < 0.999 ? normalize(cross(L, float3(0, 1, 0))) : float3(1, 0, 0);
        float3 b = cross(L, t);
        L = normalize(t * (sinT * cos(phi)) + b * (sinT * sin(phi)) + L * cosT);
        if (dot(L, n) <= 0.0) return false;
    }
    else if (radius > 0.0 && dist < 1.0e3)
    {
        float3 lp = p + L * dist;
        float u1 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float u2 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float u3 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float z = u1 * 2.0 - 1.0, phi = u2 * 6.28318530718, rr = sqrt(max(0.0, 1.0 - z * z));
        float3 o = float3(rr * cos(phi), z, rr * sin(phi));
        if (upper) o.y = abs(o.y);
        lp += o * (radius * pow(u3, 1.0 / 3.0));
        float3 d = lp - p;
        dist = length(d);
        L = d / max(dist, 1e-4);
        if (dot(L, n) <= 0.0) return false;
    }
    uint mask = _FixtureShadows > 0.5 ? 0x05u : 0x01u; // fixtures (0x04) cast shadows unless disabled
    return VisibleMasked(p, n, L, dist - min(kLightFixtureClearance, 0.5 * dist), mask);
}

float LightLuminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

bool LightUnshadowed(uint i) { return _Lights[i * 4 + 3].z > 0.5; }

// Unshadowed contribution of light i at p, using ULTRAKILL's falloff (StaticSceneOptimizer / Stationary shader):
// Unity's vertex-light term, the spot cone, and a linear fade from range to 2 * range.
// Returns false (and c = 0) when the light contributes nothing. L and dist are the shadow-ray direction and length.
// Relight (docs/relight.md). Zero = the game's own light model (unbound shaders get that).
//   x: falloff blend, 0 = ULTRAKILL's curve, 1 = physical inverse-square (matched to the game at half the range)
//   y: fill cut, 0 keeps the game's unshadowed fill lights, 1 removes them (bounce light replaces them)
//   z: exposure multiplier for lights (0 = 1)
//   w: ambient cut, 0 keeps the game's flat ambient, 1 removes it
float4 _Relight;
// Linear transport: coefficient K of the physical curve K * r^2 / d^2 (r = the light's range), i.e. g(f)^2.2 * f^2 for
// a curve that equals the game's g(d) = 1 / (1 + 25 d^2 / r^2) at d = f * r. 0 = f 0.5 (0.003205). Plugin.SkyGI.cs.
float _RelightLinearK;

float RelightExposure() { return _Relight.z > 0.0 ? _Relight.z : 1.0; }
float RelightAmbientScale() { return 1.0 - saturate(_Relight.w); }

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
    bool lin = LinearLight();
    if (local && lin)
    {
        // Linear transport: the same curves on linear values. The physical curve is inverse-square in linear light,
        // equal to the game's at a fraction f of the range (_RelightLinearK; f 0.5: (1/7.25)^2.2 at r/2), windowed to 2r
        // and capped at 3x the game.
        float4 l2 = _Lights[i * 4 + 2];
        float4 l3 = _Lights[i * 4 + 3];
        float r = max(l0.w, 1e-6);
        float game = (1.0 / (1.0 + d2 * 25.0 / (r * r))) * saturate(2.0 - d / r);
        float gameL = pow(game, 2.2);
        if (_Relight.x > 0.0)
        {
            float x = d / (2.0 * r);
            float window = saturate(1.0 - x * x * x * x);
            float k = _RelightLinearK > 0.0 ? _RelightLinearK : 0.003205; // (1/7.25)^2.2 * 0.5^2
            float phys = k * (r * r) / max(d2, 0.01) * window * window;
            phys = min(phys, pow(3.0 * game + 0.05, 2.2));
            gameL = lerp(gameL, phys, saturate(_Relight.x));
        }
        atten = gameL * pow(saturate((max(dot(L, -l2.xyz), 0.0) - l3.x) * l3.y), 2.2);
        dist = d;
        if (atten < 2.5e-7) return false; // 1e-3 in gamma
    }
    else if (local)
    {
        float4 l2 = _Lights[i * 4 + 2];
        float4 l3 = _Lights[i * 4 + 3];
        float r = max(l0.w, 1e-6);
        float game = (1.0 / (1.0 + d2 * 25.0 / (r * r))) * saturate(2.0 - d / r);
        if (_Relight.x > 0.0)
        {
            // Physical falloff, equal to the game's at half the range (1/7.25 there), with a smooth window to 2r so
            // lights still end where the level designers expected. Clamped near the light (0.1 m) to avoid hotspots.
            float rRef = 0.5 * r;
            float x = d / (2.0 * r);
            float window = saturate(1.0 - x * x * x * x);
            float phys = (1.0 / 7.25) * (rRef * rRef) / max(d2, 0.01) * window * window;
            // Never more than 3x the game's own brightness: inverse-square near a light (e.g. one carried right next to
            // the camera) would otherwise blow surfaces out to white.
            phys = min(phys, 3.0 * game + 0.05);
            game = lerp(game, phys, saturate(_Relight.x));
        }
        atten = game * saturate((max(dot(L, -l2.xyz), 0.0) - l3.x) * l3.y);
        dist = d;
    }
    if (!lin && atten < 1e-3) return false;
    float scale = RelightExposure();
    if (_Lights[i * 4 + 3].z > 0.5) scale *= 1.0 - saturate(_Relight.y); // unshadowed fill light
    if (scale <= 0.0) return false;
    // Exposure and fill cut scale the light's (gamma) colour, so a light on its own keeps its brightness either way.
    c = ToLight(_Lights[i * 4 + 1].rgb * scale) * (atten * ndl);
    return true;
}

float3 LightFrom(uint i, float3 p, float3 n, uint layer)
{
    float3 L, c;
    float dist;
    if (!LightEval(i, p, n, layer, L, dist, c)) return 0;
    if (!LightUnshadowed(i) && !LightVisible(i, p, n, L, dist)) return 0;
    return c;
}

// ---------------------------------------------------------------- emissive triangles

// The point on a triangle: 10-bit stratum centres folded into the triangle, so it is uniform over the triangle's area
// and exactly reproducible from the 20 bits stored in the reservoir.
float2 EmissiveBary(uint bary)
{
    float2 b = (float2(bary & 0x3FFu, (bary >> 10) & 0x3FFu) + 0.5) * (1.0 / 1024.0);
    return (b.x + b.y > 1.0) ? 1.0 - b : b;
}

uint EmissiveRandomBary(inout uint rng)
{
    uint u = min((uint)(Rand(rng) * 1024.0), 1023u);
    uint v = min((uint)(Rand(rng) * 1024.0), 1023u);
    return u | (v << 10);
}

bool EmissiveActive(uint tri)
{
    return tri < (uint)_EmissiveTriCount && ((_EmissiveActive[tri >> 5] >> (tri & 31u)) & 1u) != 0u;
}

// Unshadowed contribution at (p, n) of the point `bary` on emissive triangle `tri`, per unit of emitter area:
// Le * scale * cos(receiver) * cos(emitter) / (pi * max(d^2, min d^2)). `area` is the triangle's world-space area,
// so the source pdf of a sample in area measure is pdf(tri) / area. L and dist describe the shadow ray.
bool EmissiveEval(uint tri, uint bary, float3 p, float3 n, out float3 L, out float dist, out float3 c, out float area)
{
    L = 0; dist = 0; c = 0; area = 0;
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
    // Unity's front faces wind clockwise in its left-handed space, which makes cross(e1, e2) the front normal.
    // A mirroring transform flips it, and so does -ultrart-ccw (flag bit 1).
    if ((determinant(m) < 0.0) != ((flags & 2u) != 0u)) nl = -nl;

    float2 b = EmissiveBary(bary);
    float3 x = p0 + e1 * b.x + e2 * b.y;
    float3 toL = x - p;
    float d2 = dot(toL, toL);
    if (d2 < 1e-8) return false;
    dist = sqrt(d2);
    L = toL / dist;
    float cosS = dot(n, L);
    if (cosS <= 1e-4) return false;
    float cosL = -dot(nl, L);
    if ((flags & 1u) != 0u) cosL = abs(cosL);
    if (cosL <= 1e-4) return false;

    float3 le = ToLight(float3(f16tof32(t1.w), f16tof32(t1.w >> 16), f16tof32(t2.w)));
    c = le * (_EmissiveParams.y * cosS * cosL / max(d2, _EmissiveParams.z));
    return true;
}

// Visibility of an emitter sample: the ray stops just short of the emitter, so the emitter can't shadow itself.
bool EmissiveVisible(float3 p, float3 n, float3 L, float dist)
{
    return Visible(p, n, L, dist * 0.999 - 0.02);
}

void SampleEmissiveTable(float u, out uint tri, out float pdf)
{
    float x = u * (float)_EmissiveCount;
    uint slot = min((uint)x, (uint)_EmissiveCount - 1u);
    uint4 e = _EmissiveAlias[slot];
    bool keep = (x - (float)slot) < asfloat(e.x);
    tri = keep ? (e.y & 0xFFFFu) : (e.y >> 16);
    pdf = asfloat(keep ? e.z : e.w);
}

// Reference mode: `count` emitter samples from the alias table, one shadow ray each. Unbiased but noisy: brute force
// over every emissive triangle would cost thousands of rays per pixel.
float3 EmissiveDirect(float3 p, float3 n, int count, inout uint rng, out float3 unshadowed)
{
    unshadowed = 0;
    if (_EmissiveCount <= 0 || count <= 0) return 0;
    float3 shadowed = 0;
    for (int k = 0; k < count; k++)
    {
        uint tri;
        float pdf;
        SampleEmissiveTable(Rand(rng), tri, pdf);
        uint bary = EmissiveRandomBary(rng);
        float3 L, c;
        float dist, area;
        if (pdf <= 0.0 || !EmissiveEval(tri, bary, p, n, L, dist, c, area)) continue;
        c *= area / pdf;
        unshadowed += c;
        if (EmissiveVisible(p, n, L, dist)) shadowed += c;
    }
    unshadowed /= (float)count;
    return shadowed / (float)count;
}

// ---------------------------------------------------------------- reference / fill lights

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
    uint   light;  // light code: light index, or ULTRART_EMISSIVE | triangle; ULTRART_NO_LIGHT when nothing was selected
    uint   bary;   // emissive samples: the point on the triangle (EmissiveBary)
    float  wSum;
    float  M;
    float  pHat;   // target (luminance of the unshadowed contribution) of the sample at this pixel
    float3 c;      // unshadowed contribution of the sample at this pixel
    float3 L;
    float  dist;
};

Reservoir ReservoirEmpty()
{
    Reservoir r;
    r.light = ULTRART_NO_LIGHT;
    r.bary = 0;
    r.wSum = 0; r.M = 0; r.pHat = 0;
    r.c = 0; r.L = 0; r.dist = 0;
    return r;
}

// Weighted reservoir sampling of one stream element (weight w, representing M samples).
void ReservoirAdd(inout Reservoir r, uint light, uint bary, float w, float M, float3 c, float3 L, float dist, float u)
{
    r.wSum += w;
    r.M += M;
    if (w > 0.0 && u * r.wSum < w)
    {
        r.light = light;
        r.bary = bary;
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

// RIS: `count` candidates from the source pdf, resampled by the unshadowed contribution. A candidate is an emitter
// sample with probability _EmissiveParams.x (alias table over the emissive triangles, then a uniform point on the
// triangle: pdf in area measure), otherwise a light from the light alias table (pdf over light indices). The domain is
// the disjoint union of both, so mixing measures is fine as long as each sample uses its own pdf.
Reservoir RisCandidates(float3 p, float3 n, uint layer, int count, inout uint rng)
{
    Reservoir r = ReservoirEmpty();
    float share = _EmissiveCount > 0 ? _EmissiveParams.x : 0.0;
    if (_RisLightCount <= 0) share = _EmissiveCount > 0 ? 1.0 : 0.0;
    if (_RisLightCount <= 0 && share <= 0.0) return r;
    for (int k = 0; k < count; k++)
    {
        float u = Rand(rng);
        float3 L = 0, c = 0;
        float dist = 0;
        float w = 0.0;
        uint code, bary = 0;
        if (u < share)
        {
            uint tri;
            float pdf, area;
            SampleEmissiveTable(u / share, tri, pdf);
            bary = EmissiveRandomBary(rng);
            code = ULTRART_EMISSIVE | tri;
            if (pdf > 0.0 && EmissiveEval(tri, bary, p, n, L, dist, c, area)) w = LightLuminance(c) * area / (share * pdf);
        }
        else
        {
            uint li;
            float pdf;
            SampleLightTable(saturate((u - share) / (1.0 - share)), li, pdf);
            code = li;
            if (pdf > 0.0 && LightEval(li, p, n, layer, L, dist, c)) w = LightLuminance(c) / ((1.0 - share) * pdf);
        }
        ReservoirAdd(r, code, bary, w, 1.0, c, L, dist, Rand(rng));
    }
    return r;
}

bool SampleVisible(Reservoir r, float3 p, float3 n)
{
    return (r.light & ULTRART_EMISSIVE) != 0u ? EmissiveVisible(p, n, r.L, r.dist) : LightVisible(r.light, p, n, r.L, r.dist);
}

// ---------------------------------------------------------------- temporal reuse

#define ULTRART_OCT_MAX 2047.0 // 11 bits per component

uint2 OctEncode11(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return (uint2)round(saturate(e * 0.5 + 0.5) * ULTRART_OCT_MAX);
}

float3 OctDecode11(uint2 q)
{
    float2 e = (float2)q / ULTRART_OCT_MAX * 2.0 - 1.0;
    float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

uint RestirIndex(uint2 q, uint2 dim) { return q.y * dim.x + q.x; }

uint4 RestirPack(Reservoir r, float M, float W, float3 n, float3 pos)
{
    uint2 oct = OctEncode11(n);
    float3 nq = OctDecode11(oct); // the plane is stored against the quantised normal, so the test is exact
    uint m = min((uint)round(M), 4095u);
    uint bary = (r.light & ULTRART_EMISSIVE) != 0u ? r.bary : 0u;
    return uint4(
        (r.light & 0x1FFFFu) | (m << 20),
        (asuint(W) & ~0x3FFu) | (bary & 0x3FFu), // W keeps 13 mantissa bits (relative error < 1.3e-4)
        oct.x | (oct.y << 11) | (((bary >> 10) & 0x3FFu) << 22),
        asuint(dot(nq, pos)));
}

float  RestirM(uint4 h) { return (float)(h.x >> 20); }
uint   RestirLight(uint4 h) { return h.x & 0x1FFFFu; }
float  RestirW(uint4 h) { return asfloat(h.y & ~0x3FFu); }
uint   RestirBary(uint4 h) { return (h.y & 0x3FFu) | ((h.z >> 22) << 10); }
float3 RestirNormal(uint4 h) { return OctDecode11(uint2(h.z & 0x7FFu, (h.z >> 11) & 0x7FFu)); }

// Sky / no surface: an entry that is never reused (M = 0).
void RestirWriteEmpty(uint2 id, uint2 dim)
{
    if (_LightSampling >= 2) _ReservoirCur[RestirIndex(id, dim)] = uint4(ULTRART_NO_LIGHT, 0u, 0u, 0u);
}

// Does the previous frame's reservoir h belong to (roughly) the same surface as (pos, n)?
bool RestirMatch(uint4 h, float3 pos, float3 n, float t)
{
    if (!(RestirM(h) > 0.0)) return false;
    float3 nh = RestirNormal(h);
    if (dot(n, nh) < _RestirParams.y) return false;
    return abs(dot(nh, pos) - asfloat(h.w)) <= _RestirParams.z * t + 0.01;
}

// Merge a previous-frame reservoir into r, re-targeting its sample at the current pixel (biased 1/M combine).
void RestirMerge(inout Reservoir r, uint4 h, float3 pos, float3 n, uint layer, inout uint rng)
{
    float M = min(RestirM(h), _RestirParams.x);
    uint prevLight = RestirLight(h);
    uint light = ULTRART_NO_LIGHT;
    uint bary = 0;
    float3 L = 0, c = 0;
    float dist = 0;
    float w = 0;
    if ((prevLight & ULTRART_EMISSIVE) != 0u)
    {
        // Triangle indices are stable while _EmissiveHistoryValid. A triangle whose surface left the RTAS (room hidden,
        // object destroyed) is dropped like a light that is gone.
        uint tri = prevLight & 0xFFFFu;
        if (_EmissiveHistoryValid == 0 || !EmissiveActive(tri)) return;
        light = prevLight;
        bary = RestirBary(h);
        float area;
        if (EmissiveEval(tri, bary, pos, n, L, dist, c, area)) w = LightLuminance(c) * RestirW(h) * M;
    }
    else if (prevLight != ULTRART_NO_LIGHT)
    {
        if (prevLight >= (uint)_PrevLightCount) return;
        light = _LightRemap[prevLight];
        // The light is gone (switched off with its room, destroyed): this history describes a light set that
        // no longer exists, so drop it rather than count its M with zero weight.
        if (light == ULTRART_NO_LIGHT) return;
        if (LightEval(light, pos, n, layer, L, dist, c)) w = LightLuminance(c) * RestirW(h) * M;
    }
    // A history with no light (or W = 0 because its sample was occluded) still counts its M: it is evidence
    // that this pixel receives nothing from those samples. See docs/light-sampling.md.
    ReservoirAdd(r, light, bary, w, M, c, L, dist, Rand(rng));
}

// World-space motion of the primary hit (previous position - current), set by the ray generation shader from the hit's
// packed motion (docs/denoiser.md). Reprojection and the plane test use pos + motion; the merge evaluates at pos.
static float3 g_PrimaryMotion = 0;
// Primary hits seen through a portal (UltraRTPortals.hlsl): reprojection and the stored reservoir plane use the
// camera-side point and normal (where the camera sees the hit; g_PrimaryMotion is in that space too), while lights are
// evaluated at the real hit. Set by PathTrace's ray generation.
static bool   g_PrimaryVirtual = false;
static float3 g_PrimaryVirtualPos = 0;
static float3 g_PrimaryVirtualN = 0;

void RestirReuse(inout Reservoir r, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    if (_RestirHistoryValid == 0) return;
    float3 vn = g_PrimaryVirtual ? g_PrimaryVirtualN : n;
    float3 prevPos = (g_PrimaryVirtual ? g_PrimaryVirtualPos : pos) + g_PrimaryMotion;
    float4 pc = mul(_PrevViewProj, float4(prevPos, 1.0));
    if (pc.w <= 1e-6) return;
    float2 pp = (pc.xy / pc.w * 0.5 + 0.5) * (float2)dim;

    if (all(pp >= 0.0) && all(pp < (float2)dim))
    {
        uint4 h = _ReservoirPrev[RestirIndex((uint2)pp, dim)];
        if (RestirMatch(h, prevPos, vn, t)) RestirMerge(r, h, pos, n, layer, rng);
    }

    for (int k = 0; k < _RestirSpatial; k++)
    {
        float a = Rand(rng) * 6.28318530718;
        float rad = sqrt(Rand(rng)) * _RestirParams.w;
        float2 q = pp + float2(cos(a), sin(a)) * rad;
        if (any(q < 0.0) || any(q >= (float2)dim)) continue;
        uint4 h = _ReservoirPrev[RestirIndex((uint2)q, dim)];
        if (RestirMatch(h, prevPos, vn, t)) RestirMerge(r, h, pos, n, layer, rng);
    }
}

// ---------------------------------------------------------------- entry points

// The unshadowed counterpart of the last DirectLighting() result (same samples, no visibility). The hybrid composite
// uses shadowed / unshadowed as its shadow ratio, so it needs no separate loop over every light.
static float3 g_DirectUnshadowed;
// The emissive-triangle part of the last DirectLighting() result (shadowed, scaled), for the hybrid's additive term.
static float3 g_DirectEmissive;
// The part from lights the game doesn't draw (virtual effect lights, [3].w > 0), shadowed, scaled and weighted.
static float3 g_DirectAdditive;

float LightHybridWeight(uint i) { return _Lights[i * 4 + 3].w; }

// Hybrid with a per-texel detail normal (docs/materials.md): DirectLighting gets the vertex normal (sampling, reservoirs,
// the unshadowed denominator: the game lit the surface with it) and the shadowed result is re-shaded with this normal,
// so the composite's shadowed / unshadowed ratio carries the per-texel shading. Zero = off (every other caller).
static float3 g_DetailN = 0;

// With g_DetailN: the shadowed direct light at the vertex normal (what DirectLighting returns without detail).
static float3 g_DirectVertex;

// c (a light's contribution at normal n from direction L) re-shaded with the detail normal.
float DetailScale(float3 n, float3 L)
{
    if (!any(g_DetailN != 0.0)) return 1.0;
    return saturate(dot(g_DetailN, L)) / max(dot(n, L), 1e-4);
}

// Direct light at the primary hit. Writes this pixel's reservoir when temporal reuse is on.
float3 DirectLighting(uint2 id, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    g_DirectEmissive = 0;
    g_DirectAdditive = 0;
    g_DirectVertex = 0;
    if (_LightSampling <= 0)
    {
        float3 shadowed = 0, unshadowed = 0;
        for (int i = 0; i < _LightCount; i++)
        {
            float3 L, c;
            float dist;
            if (!LightEval((uint)i, pos, n, layer, L, dist, c)) continue;
            unshadowed += c;
            if (LightUnshadowed((uint)i) || LightVisible((uint)i, pos, n, L, dist))
            {
                float3 cd = c * DetailScale(n, L);
                g_DirectVertex += c * _LightScale;
                shadowed += cd;
                g_DirectAdditive += cd * (LightHybridWeight((uint)i) * _LightScale);
            }
        }
        float3 emUnshadowed;
        float3 em = EmissiveDirect(pos, n, _EmissiveRefSamples, rng, emUnshadowed);
        g_DirectEmissive = em * _LightScale;
        g_DirectUnshadowed = (unshadowed + emUnshadowed) * _LightScale;
        g_DirectVertex += em * _LightScale;
        return (shadowed + em) * _LightScale;
    }

    float3 sum = FillLighting(pos, n, layer);
    float3 unshadowedSum = sum;
    float3 vertexSum = sum;
    if (any(g_DetailN != 0.0)) sum = FillLighting(pos, g_DetailN, layer);
    Reservoir r = RisCandidates(pos, n, layer, _RisCandidates, rng);
    bool temporal = _LightSampling >= 2;
    if (temporal) RestirReuse(r, dim, pos, n, t, layer, rng);

    float W = ReservoirW(r);
    if (W > 0.0)
    {
        unshadowedSum += r.c * W;
        // Occluded samples are stored with W = 0 so they stop propagating (visibility reuse with the one ray).
        if (SampleVisible(r, pos, n))
        {
            float3 cd = r.c * (W * DetailScale(n, r.L));
            vertexSum += r.c * W;
            sum += cd;
            if ((r.light & ULTRART_EMISSIVE) != 0u) g_DirectEmissive = cd * _LightScale;
            else g_DirectAdditive = cd * (_LightScale * LightHybridWeight(r.light));
        }
        else W = 0.0;
    }

    if (temporal) _ReservoirCur[RestirIndex(id, dim)] = RestirPack(r, min(r.M, _RestirParams.x), W,
        g_PrimaryVirtual ? g_PrimaryVirtualN : n, g_PrimaryVirtual ? g_PrimaryVirtualPos : pos);
    g_DirectUnshadowed = unshadowedSum * _LightScale;
    g_DirectVertex = vertexSum * _LightScale;
    return sum * _LightScale;
}

// Direct light at the bounce hit: one shadow ray (reference: one light ray plus at most one emitter ray).
float3 DirectOne(float3 p, float3 n, uint layer, inout uint rng)
{
    if (_LightSampling <= 0)
    {
        float3 emUnshadowed;
        float3 em = EmissiveDirect(p, n, min(_EmissiveRefSamples, 1), rng, emUnshadowed);
        if (_LightCount <= 0) return em * _LightScale;
        int i = min((int)(Rand(rng) * _LightCount), _LightCount - 1);
        return (LightFrom((uint)i, p, n, layer) * _LightCount + em) * _LightScale;
    }

    float3 sum = FillLighting(p, n, layer);
    Reservoir r = RisCandidates(p, n, layer, _BounceCandidates, rng);
    float W = ReservoirW(r);
    if (W > 0.0 && SampleVisible(r, p, n)) sum += r.c * W;
    return sum * _LightScale;
}

#endif
