#ifndef ULTRART_LIGHTS_INCLUDED
#define ULTRART_LIGHTS_INCLUDED

// Direct lighting for PathTrace.raytrace: ULTRAKILL's light model plus light selection.
//   _LightSampling 0: reference. Every light, one shadow ray each (DirectAll), uniform pick at the bounce.
//   _LightSampling 1: RIS. M candidates from an alias table, one shadow ray for the winner.
//   _LightSampling 2: RIS + temporal reuse (ReSTIR DI, 1/M combine). One shadow ray per pixel.
// At the primary hit both can weight lights by a per-pixel visibility memory (_VisMemory).
// Unshadowed fill lights are always evaluated exhaustively (they are few and need no rays).
// Emissive triangles (EmissiveLights.cs) are a second kind of RIS candidate: a point on a triangle of an emissive
// surface. In reference mode they get _EmissiveRefSamples extra shadow rays per hit instead.
// See docs/light-sampling.md and docs/emissive-lights.md.
//
// The includer must declare, before this file: RayPayload (UltraRTCommon.hlsl), _Scene, Rand(inout uint)
// and _LightScale.

// Per light, 4 x float4:
//   [0] position (or -forward for directional), range (0 = directional)
//   [1] color * intensity, flags: 1 = the player's own light (attached to the player or the camera: the held torch, the
//       7-3 flashlight, muzzle flashes; Plugin.FlagPlayerLights): V1's model never shadows it. Culling masks live in
//       _LightMasks.
//   [2] forward, soft-shadow radius (m; 0 = _LightRadius; < 0 upper half-sphere). Lights lifted out of their fixture
//       get the fixture's size.
//   [3] cos(spot/2) (-1 for non-spot), 1 / (cos(spot/4) - cos(spot/2)), unshadowed (1 = fill light),
//       hybrid weight: > 0 for lights the game doesn't draw (EffectLights' virtual lights for glowing effects); hybrid
//       adds that much of their shadowed light on top of the game's image, like emissive surfaces
// Area lights (per-level light overrides, LightOverrides.cs; docs/lighting-overrides.md) keep this layout:
//   [2] xyz the emission normal (rect/disc emit to its side only), w half-size x (sphere/disc: radius)
//   [3] x = -2 - half-size y (<= -2 marks an area light; every spot test sees "no cone"), y = shape | octahedral tangent
//       << 2 (shape 1 sphere, 2 rect, 3 disc; tangent 2 x 10 bits; an exact integer in a float)
//   They are evaluated at the point of the shape closest to the receiver (the game's falloff from there, times the
//   emitter cosine for rect/disc), so every sampler treats them as a deterministic light, and their shadow rays aim at a
//   uniform random point on the shape (soft shadows the size of the emitter).
StructuredBuffer<float4> _Lights;
StructuredBuffer<uint>   _LightMasks; // per-light culling mask (separate: masks like ~0 are NaN bit patterns as floats)
int      _LightCount;
// Does a light with culling mask m reach a surface on `layer`? An includer may define its own test before including this
// file (Viewmodel.raytrace lights the first-person gun with the lights of several layers; docs/viewmodel.md).
#ifndef ULTRART_LIGHT_LAYER_TEST
#define ULTRART_LIGHT_LAYER_TEST(m, layer) ((((m) >> (layer)) & 1u) != 0u)
#endif
#ifdef ULTRART_LIGHT_ISOTROPIC
// ParticleLight.raytrace (docs/particles.md): lights are evaluated at points in the air, facing every light (no cosine,
// no layer); a light counts if its culling mask has any of these layers (the ones effects live on).
uint     _LightIsoMask;
#endif

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
// Lights that changed since the previous frame (LightSampling.cs, docs/light-sampling.md "Light changes"): per change,
// 8 x float4 = the light's packed data last frame (zero colour: it is new) and this frame (zero colour: it is gone).
// The history M cap is shortened only where these changes matter (RestirChangeCap).
StructuredBuffer<float4>  _LightChanges;
int       _LightChangeCount;
float     _LightChangeTolerance; // share of a pixel's direct light that may be stale before its history is shortened
int       _RestirFreshCandidates; // extra RIS candidates for a pixel whose temporal history failed (disocclusion, screen edge)
float     _RestirOccludedCap;     // most M an occluded (W = 0) history may carry (docs/light-sampling.md, "Penumbrae")
int       _RestirOccludedRetest;  // 1: an occluded temporal history re-tests its sample's visibility instead of the cap
float     _RestirRetestCap;       // most M a re-tested history that is blocked again may carry, unless it is in an umbra
int       _RestirRetestUmbra;     // 1: blocked again and the light's centre blocked too (an umbra): the history keeps its M
// Visibility reuse (the old ReSTIR history): 1 = an occluded final sample is stored with W = 0 (and its M capped by
// _RestirOccludedCap), so the history carries the pixel's visibility. Biased bright wherever lights compete with shadowed
// ones (docs/light-sampling.md, "Bias"). 0 = the history keeps the sample's unshadowed W and every frame traces its own
// visibility: plain temporal RIS reuse, unbiased.
int       _RestirVisReuse;
// Visibility memory (docs/light-sampling.md, "Visibility memory"): per pixel, up to 3 lights with a running count of their
// shadow-ray outcomes. The RIS target becomes p_hat * Vbar (Vbar = (visible + 1) / (tests + 2)), so a light that is blocked
// at this pixel (8-2's sun on the skylight room's walls) stops taking the samples of the lights that reach it. The
// estimate stays unbiased: Vbar > 0, and the shading traces the real visibility. Stored next to the reservoir
// (_ReservoirStride 2: element 2i is the reservoir, 2i + 1 the memory).
int       _VisMemory;          // 1: use and update the memory at the primary hit
int       _VisMemoryValid;     // the previous frame's memory may be read
int       _ReservoirStride;    // elements per pixel in _ReservoirPrev / _ReservoirCur (1 or 2)

// Moving occluders (docs/denoiser.md, "Moving shadows"): world AABBs of dynamic objects that moved in the last frames (the
// player's third-person model first, then the nearest moving enemies and props), each the union of this frame's and the
// previous frame's bounds, as min, max pairs. A shadow ray that crosses one may be shadowed differently than last frame:
// ReSTIR drops occluded history whose sample crosses one, and PathTrace flags the pixel for the denoiser's targeted
// anti-lag. Only PathTrace defines ULTRART_MOVERS (other includers never see these globals).
#ifdef ULTRART_MOVERS
StructuredBuffer<float4> _UltraRTMovers;
int _UltraRTMoverCount;
static bool g_MoverShadow = false; // the primary hit's light sample (or its dropped history) crossed a moving occluder
// Player shadow split (docs/denoiser.md, "Moving shadows"): with _UltraRTPlayerSplit, the primary hit's final shadow ray
// ignores V1's model, so the denoised lighting (and ReSTIR's visibility reuse) never holds the player's shadow; a second
// ray against V1 alone (only where the sample's segment crosses V1's box) gives the light it blocks, which the denoiser
// applies after filtering as a lightly filtered factor. g_PlayerBlocked is that light (lighting units, x _LightScale).
int _UltraRTPlayerSplit;
float4 _UltraRTPlayerModelMin, _UltraRTPlayerModelMax; // V1's body bounds (min > max = none)
static float3 g_PlayerBlocked = 0;

// Does the segment p + L * [0.02, dist) cross a mover box (slab test)?
bool MoverCrosses(float3 p, float3 L, float dist)
{
    float3 inv = 1.0 / (abs(L) > 1e-8 ? L : (L >= 0.0 ? 1e-8 : -1e-8));
    float tEnd = min(dist, 1.0e4);
    for (int k = 0; k < _UltraRTMoverCount; k++)
    {
        float3 bmin = _UltraRTMovers[2 * k].xyz, bmax = _UltraRTMovers[2 * k + 1].xyz;
        float3 t0 = (bmin - p) * inv, t1 = (bmax - p) * inv;
        float3 lo = min(t0, t1), hi = max(t0, t1);
        float a = max(max(lo.x, lo.y), max(lo.z, 0.02));
        float b = min(min(hi.x, hi.y), min(hi.z, tEnd));
        if (a <= b) return true;
    }
    return false;
}
#endif

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

// Extra instance mask for every shadow ray: ULTRART_MASK_PLAYER where V1's third-person model casts shadows (PathTrace sets
// it from _UltraRTPlayerRays; the radiance cache keeps 0, so its world-space cells never hold the moving player's shadow).
// Set by each ray-generation shader, like g_ShadowRng.
static uint g_ShadowPlayerMask;
// V1's bounds, padded (SceneProxies.PlayerShadowBox; min > max = none). An emitter sample that ends inside them is the
// player's own (glowing effects at the first-person gun, which sits inside the third-person model's arm): the model
// doesn't shadow it (EmissiveVisible). Point and spot lights carry the decision in their player-light flag instead
// (Plugin.FlagPlayerLights: lights on the player or camera, at the eye, and transient lights inside this box), so a
// level light that happens to be near the player's head still gets V1's shadow (0-2's first room).
float4 _UltraRTPlayerBoxMin, _UltraRTPlayerBoxMax;

bool InPlayerBox(float3 e) { return all(e >= _UltraRTPlayerBoxMin.xyz) && all(e <= _UltraRTPlayerBoxMax.xyz); }

bool VisibleMasked(float3 p, float3 n, float3 dir, float dist, uint mask)
{
    mask |= g_ShadowPlayerMask;
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

// ---------------------------------------------------------------- area lights (docs/lighting-overrides.md)

bool LightIsArea(float4 l3) { return l3.x < -1.5; }
uint LightAreaCode(float4 l3) { return (uint)(max(l3.y, 0.0) + 0.5); }

// Octahedral 2 x 10-bit tangent from the packed code (LightOverrides.PackArea).
float3 LightAreaTangent(uint code)
{
    uint oct = code >> 2;
    float2 e = (float2(oct & 1023u, (oct >> 10) & 1023u) + 0.5) * (2.0 / 1024.0) - 1.0;
    float3 v = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}

// The tangent frame of a rect/disc: t, b in the emitter's plane, orthogonal to the normal nrm.
void LightAreaFrame(float3 nrm, uint code, out float3 t, out float3 b)
{
    t = LightAreaTangent(code);
    t = t - nrm * dot(t, nrm);
    float tl = length(t);
    t = tl > 1e-4 ? t / tl : (abs(nrm.y) < 0.99 ? normalize(cross(nrm, float3(0, 1, 0))) : float3(1, 0, 0));
    b = cross(nrm, t);
}

// The point of the light's shape closest to p (p itself inside a sphere). emit: the emitter-side cosine factor.
float3 LightAreaClosest(float4 l0, float4 l2, float4 l3, float3 p)
{
    uint code = LightAreaCode(l3);
    uint shape = code & 3u;
    float3 c = l0.xyz;
    float hx = max(l2.w, 0.0), hy = max(-l3.x - 2.0, 0.0);
    float3 v = p - c;
    if (shape <= 1u)
    {
        float d = length(v);
        return d > hx ? c + v * (hx / d) : c;
    }
    float3 t, b;
    LightAreaFrame(l2.xyz, code, t, b);
    float x = dot(v, t), y = dot(v, b);
    if (shape == 2u) { x = clamp(x, -hx, hx); y = clamp(y, -hy, hy); }
    else
    {
        float r = sqrt(x * x + y * y);
        if (r > hx) { float k = hx / r; x *= k; y *= k; }
    }
    return c + t * x + b * y;
}

// A uniform random point on the light's shape (shadow rays). Spheres: on the surface half that faces p.
float3 LightAreaSample(float4 l0, float4 l2, float4 l3, float3 p, float u1, float u2)
{
    uint code = LightAreaCode(l3);
    uint shape = code & 3u;
    float3 c = l0.xyz;
    float hx = max(l2.w, 0.0), hy = max(-l3.x - 2.0, 0.0);
    if (shape <= 1u)
    {
        float z = u1 * 2.0 - 1.0, phi = u2 * 6.28318530718, rr = sqrt(max(0.0, 1.0 - z * z));
        float3 o = float3(rr * cos(phi), z, rr * sin(phi));
        if (dot(o, p - c) < 0.0) o = -o;
        return c + o * hx;
    }
    float3 t, b;
    LightAreaFrame(l2.xyz, code, t, b);
    if (shape == 2u) return c + t * ((u1 * 2.0 - 1.0) * hx) + b * ((u2 * 2.0 - 1.0) * hy);
    float r = hx * sqrt(u1), phi = u2 * 6.28318530718;
    return c + t * (r * cos(phi)) + b * (r * sin(phi));
}

bool LightVisible(uint i, float3 p, float3 n, float3 L, float dist)
{
    float4 al3 = _Lights[i * 4 + 3];
    if (LightIsArea(al3) && dist < 1.0e3)
    {
        // Area light: a uniform point on the emitter, so the shadow is as soft as the emitter is large.
        float u1 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float u2 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float3 lp = LightAreaSample(_Lights[i * 4], _Lights[i * 4 + 2], al3, p, u1, u2);
        float3 d = lp - p;
        dist = length(d);
        if (dist < 1e-4) return true;
        L = d / dist;
#ifndef ULTRART_LIGHT_ISOTROPIC
        if (dot(L, n) <= 0.0) return false;
#endif
        uint amask = _FixtureShadows > 0.5 ? 0x05u : 0x01u;
        uint aplayer = g_ShadowPlayerMask;
        if (_Lights[i * 4 + 1].w > 0.5) g_ShadowPlayerMask = 0u;
        bool avis = VisibleMasked(p, n, L, dist - min(kLightFixtureClearance, 0.5 * dist), amask);
        g_ShadowPlayerMask = aplayer;
        return avis;
    }
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
#ifndef ULTRART_LIGHT_ISOTROPIC
        if (dot(L, n) <= 0.0) return false;
#endif
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
#ifndef ULTRART_LIGHT_ISOTROPIC
        if (dot(L, n) <= 0.0) return false;
#endif
    }
    uint mask = _FixtureShadows > 0.5 ? 0x05u : 0x01u; // fixtures (0x04) cast shadows unless disabled
    // The player's own lights (a head-mounted flashlight, the held torch, muzzle flashes) sit inside or next to V1's
    // model: it never shadows them (flag set by Plugin.FlagPlayerLights).
    uint playerMask = g_ShadowPlayerMask;
    if (_Lights[i * 4 + 1].w > 0.5) g_ShadowPlayerMask = 0u;
    bool vis = VisibleMasked(p, n, L, dist - min(kLightFixtureClearance, 0.5 * dist), mask);
    g_ShadowPlayerMask = playerMask;
    return vis;
}

// LightVisible without the soft-shadow jitter: the ray to the light's centre (the sun's own direction), to tell an umbra
// from a penumbra (docs/light-sampling.md, "Occluded history"). The caller sets g_ShadowPlayerMask as for LightVisible.
bool LightCentreVisible(uint i, float3 p, float3 n, float3 L, float dist)
{
    uint mask = _FixtureShadows > 0.5 ? 0x05u : 0x01u;
    uint playerMask = g_ShadowPlayerMask;
    if (_Lights[i * 4 + 1].w > 0.5) g_ShadowPlayerMask = 0u;
    bool vis = VisibleMasked(p, n, L, dist - min(kLightFixtureClearance, 0.5 * dist), mask);
    g_ShadowPlayerMask = playerMask;
    return vis;
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

// The light model on one light's packed data (4 x float4, see _Lights); the culling mask is the caller's.
bool LightEvalRaw(float4 l0, float4 l1, float4 l2, float4 l3, float3 p, float3 n, out float3 L, out float dist, out float3 c)
{
    L = 0; dist = 0; c = 0;
    bool local = l0.w != 0.0;
    bool area = local && LightIsArea(l3);
    float3 toL = local ? (area ? LightAreaClosest(l0, l2, l3, p) : l0.xyz) - p : l0.xyz;
    float d2 = max(dot(toL, toL), 1e-6);
    float d = sqrt(d2);
    L = toL / d;
    // Rect and disc emitters light their front side only, with a cosine lobe (sphere: every direction).
    float emit = area && (LightAreaCode(l3) & 3u) >= 2u ? dot(l2.xyz, -L) : 1.0;
    if (area && d2 <= 1e-6) emit = 1.0; // on the emitter itself
    if (emit <= 0.0) return false;
#ifdef ULTRART_LIGHT_ISOTROPIC
    float ndl = emit; // a point in the air (lit particles): no surface orientation
#else
    float ndl = dot(n, L);
    if (ndl <= 0.0) return false;
    ndl *= emit;
#endif

    float atten = 1.0;
    dist = 1.0e4;
    bool lin = LinearLight();
    if (local && lin)
    {
        // Linear transport: the same curves on linear values. The physical curve is inverse-square in linear light,
        // equal to the game's at a fraction f of the range (_RelightLinearK; f 0.5: (1/7.25)^2.2 at r/2), windowed to 2r
        // and capped at 3x the game.
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
#ifdef ULTRART_PLAYER_LIGHT_NDL_POW
            // The includer's player-light shaping also keeps those lights at or under the game's own falloff (the gun:
            // 7-3's flashlight 10 m behind the eye was 1.4x the game there).
            if (l1.w > 0.5 && ULTRART_PLAYER_LIGHT_NDL_POW > 1.0) phys = min(phys, gameL);
#endif
            gameL = lerp(gameL, phys, saturate(_Relight.x));
        }
        atten = gameL * pow(saturate((max(dot(L, -l2.xyz), 0.0) - l3.x) * l3.y), 2.2);
        dist = d;
        if (atten < 2.5e-7) return false; // 1e-3 in gamma
    }
    else if (local)
    {
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
    if (l3.z > 0.9) scale *= 1.0 - saturate(_Relight.y); // unshadowed fill light (0.75: unshadowed, not a fill)
    if (scale <= 0.0) return false;
    // Exposure and fill cut scale the light's (gamma) colour, so a light on its own keeps its brightness either way.
#ifdef ULTRART_PLAYER_LIGHT_NDL_POW
    // An includer may shape the cosine of the player's own lights (flag in l1.w) under linear transport
    // (Viewmodel.raytrace: ndl^2.2, so after the composite's ^(1/2.2) the gun shades like the game's gamma-space Lambert;
    // docs/viewmodel.md, "Player lights on the gun").
    if (lin && l1.w > 0.5) ndl = pow(ndl, ULTRART_PLAYER_LIGHT_NDL_POW);
#endif
    c = ToLight(l1.rgb * scale) * (atten * ndl);
    return true;
}

bool LightEval(uint i, float3 p, float3 n, uint layer, out float3 L, out float dist, out float3 c)
{
    L = 0; dist = 0; c = 0;
#ifdef ULTRART_LIGHT_ISOTROPIC
    if ((_LightMasks[i] & _LightIsoMask) == 0u) return false;
#else
    if (!ULTRART_LIGHT_LAYER_TEST(_LightMasks[i], layer)) return false;
#endif
    return LightEvalRaw(_Lights[i * 4], _Lights[i * 4 + 1], _Lights[i * 4 + 2], _Lights[i * 4 + 3], p, n, L, dist, c);
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
#ifdef ULTRART_LIGHT_ISOTROPIC
    float cosS = 1.0;
#else
    float cosS = dot(n, L);
    if (cosS <= 1e-4) return false;
#endif
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
    uint playerMask = g_ShadowPlayerMask;
    if (InPlayerBox(p + L * dist)) g_ShadowPlayerMask = 0u; // an emitter at the player (see _UltraRTPlayerBoxMin)
    bool vis = Visible(p, n, L, dist * 0.999 - 0.02);
    g_ShadowPlayerMask = playerMask;
    return vis;
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

// Weighted reservoir sampling of one stream element (weight w, representing M samples). pHat: the sample's target.
void ReservoirAdd(inout Reservoir r, uint light, uint bary, float w, float M, float3 c, float3 L, float dist, float u, float pHat)
{
    r.wSum += w;
    r.M += M;
    if (w > 0.0 && u * r.wSum < w)
    {
        r.light = light;
        r.bary = bary;
        r.pHat = pHat;
        r.c = c; r.L = L; r.dist = dist;
    }
}

// ---------------------------------------------------------------- visibility memory

// Slots of this pixel's memory (light index | visible count << 16 | test count << 20; light 0xFFFF = empty), loaded by
// VisLoad from the previous frame and updated by VisObserve. Only the primary hit's light selection uses them
// (g_VisActive): bounce hits, the cache and the other shaders see Vbar = 1.
static uint3 g_VisSlot = uint3(0xFFFFu, 0xFFFFu, 0xFFFFu);
static bool  g_VisActive = false;
static bool  g_VisLoaded = false;
#define ULTRART_VIS_MAX 15u

float VisEstimate(uint s)
{
    float v = (float)((s >> 16) & 0xFu), n = (float)((s >> 20) & 0xFu);
    return (v + 1.0) / (n + 2.0);
}

// Vbar of light code `light` at this pixel (1 when it has no slot). A light whose shadow segment crosses a moving occluder
// may have been blocked by it: its memory is stale, so it gets at least 1/2.
float VisFactor(uint light, float3 p, float3 L, float dist)
{
    if (!g_VisActive) return 1.0;
    float f = 1.0;
    [unroll] for (uint k = 0; k < 3; k++)
        if ((g_VisSlot[k] & 0xFFFFu) == light) f = VisEstimate(g_VisSlot[k]);
#ifdef ULTRART_MOVERS
    if (f < 0.5 && _UltraRTMoverCount > 0 && MoverCrosses(p, L, dist)) f = 0.5;
#endif
    return f;
}

// The outcome of the final shadow ray toward light code `light`: counted in its slot, or (blocked, no slot) given the slot
// that says least (the highest Vbar; an empty slot first). Emitter samples are not remembered (thousands of triangles).
void VisObserve(uint light, bool visible)
{
    if (!g_VisActive || (light & ULTRART_EMISSIVE) != 0u || light >= 0xFFFFu) return;
    int hit = -1, worst = 0;
    float worstV = -1.0;
    [unroll] for (int k = 0; k < 3; k++)
    {
        uint s = g_VisSlot[k];
        if ((s & 0xFFFFu) == light) hit = k;
        float v = (s & 0xFFFFu) == 0xFFFFu ? 2.0 : VisEstimate(s);
        if (v > worstV) { worstV = v; worst = k; }
    }
    if (hit < 0)
    {
        if (!visible) g_VisSlot[worst] = light | (1u << 20);
        return;
    }
    uint s = g_VisSlot[hit];
    uint v = ((s >> 16) & 0xFu) + (visible ? 1u : 0u), n = ((s >> 20) & 0xFu) + 1u;
    if (n > ULTRART_VIS_MAX) { n = (n + 1u) >> 1; v = (v + 1u) >> 1; }
    g_VisSlot[hit] = light | (min(v, n) << 16) | (n << 20);
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
        float w = 0.0, pHat = 0.0;
        uint code, bary = 0;
        if (u < share)
        {
            uint tri;
            float pdf, area;
            SampleEmissiveTable(u / share, tri, pdf);
            bary = EmissiveRandomBary(rng);
            code = ULTRART_EMISSIVE | tri;
            if (pdf > 0.0 && EmissiveEval(tri, bary, p, n, L, dist, c, area)) { pHat = LightLuminance(c); w = pHat * area / (share * pdf); }
        }
        else
        {
            uint li;
            float pdf;
            SampleLightTable(saturate((u - share) / (1.0 - share)), li, pdf);
            code = li;
            if (pdf > 0.0 && LightEval(li, p, n, layer, L, dist, c))
            {
                pHat = LightLuminance(c) * VisFactor(li, p, L, dist);
                w = pHat / ((1.0 - share) * pdf);
            }
        }
        ReservoirAdd(r, code, bary, w, 1.0, c, L, dist, Rand(rng), pHat);
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

uint RestirIndex(uint2 q, uint2 dim) { return (q.y * dim.x + q.x) * (uint)max(_ReservoirStride, 1); }

// W is the sample's unshadowed contribution weight; x bit 17 flags that its final shadow ray was blocked (the history then
// counts as W = 0 unless it is re-tested: docs/light-sampling.md, "Occluded history").
uint4 RestirPack(Reservoir r, float M, float W, bool occluded, float3 n, float3 pos)
{
    uint2 oct = OctEncode11(n);
    float3 nq = OctDecode11(oct); // the plane is stored against the quantised normal, so the test is exact
    uint m = min((uint)round(M), 4095u);
    uint bary = (r.light & ULTRART_EMISSIVE) != 0u ? r.bary : 0u;
    return uint4(
        (r.light & 0x1FFFFu) | (occluded ? (1u << 17) : 0u) | (m << 20),
        (asuint(W) & ~0x3FFu) | (bary & 0x3FFu), // W keeps 13 mantissa bits (relative error < 1.3e-4)
        oct.x | (oct.y << 11) | (((bary >> 10) & 0x3FFu) << 22),
        asuint(dot(nq, pos)));
}

float  RestirM(uint4 h) { return (float)(h.x >> 20); }
uint   RestirLight(uint4 h) { return h.x & 0x1FFFFu; }
bool   RestirOccludedFlag(uint4 h) { return ((h.x >> 17) & 1u) != 0u; }
float  RestirW(uint4 h) { return asfloat(h.y & ~0x3FFu); }
uint   RestirBary(uint4 h) { return (h.y & 0x3FFu) | ((h.z >> 22) << 10); }
float3 RestirNormal(uint4 h) { return OctDecode11(uint2(h.z & 0x7FFu, (h.z >> 11) & 0x7FFu)); }

// Sky / no surface: an entry that is never reused (M = 0), and an empty visibility memory.
void RestirWriteEmpty(uint2 id, uint2 dim)
{
    if (_LightSampling >= 2) _ReservoirCur[RestirIndex(id, dim)] = uint4(ULTRART_NO_LIGHT, 0u, 0u, 0u);
    if (_VisMemory != 0) _ReservoirCur[RestirIndex(id, dim) + 1] = uint4(0xFFFFu, 0xFFFFu, 0xFFFFu, 0u);
}

// Visibility memory record: x, y, z = slots (24 bits each), their top bytes the surface normal (oct, 2 x 8 bits) for
// validation; w = asuint(dot(normal, pos)).
uint4 VisPack(float3 n, float3 pos)
{
    float3 a = n / (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = a.xy;
    if (a.z < 0.0) e = (1.0 - abs(a.yx)) * float2(a.x >= 0.0 ? 1.0 : -1.0, a.y >= 0.0 ? 1.0 : -1.0);
    uint2 q = (uint2)round(saturate(e * 0.5 + 0.5) * 255.0);
    float3 nq = OctDecode11(uint2(round((float2)q / 255.0 * ULTRART_OCT_MAX)));
    return uint4((g_VisSlot.x & 0xFFFFFFu) | (q.x << 24), (g_VisSlot.y & 0xFFFFFFu) | (q.y << 24),
                  g_VisSlot.z & 0xFFFFFFu, asuint(dot(nq, pos)));
}

bool VisMatch(uint4 h, float3 pos, float3 n, float t)
{
    float3 nh = OctDecode11(uint2(round(float2(h.x >> 24, h.y >> 24) / 255.0 * ULTRART_OCT_MAX)));
    if (dot(n, nh) < _RestirParams.y) return false;
    return abs(dot(nh, pos) - asfloat(h.w)) <= _RestirParams.z * t + 0.01;
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
static float g_RestirCap = 1.0;

bool RestirMerge(inout Reservoir r, uint4 h, float3 pos, float3 n, uint layer, inout uint rng, bool temporalHistory)
{
    float M = min(RestirM(h), g_RestirCap);
    float Wh = RestirW(h);
    // Without visibility reuse the stored W is the unshadowed one and the occluded flag is ignored: this frame's shadow
    // ray decides (a W of 0 is a history that found no light, which counts its M as below).
    bool occluded = _RestirVisReuse != 0 && (RestirOccludedFlag(h) || !(Wh > 0.0));
    // Visibility reuse: an occluded history stands for M samples that received nothing. With soft shadows that is one
    // random draw, though: in a penumbra it held the pixel dark for up to the whole history.
    //  - Re-test (temporal history, _RestirOccludedRetest): one shadow ray to the stored sample from here, this frame. Blocked
    //    again: the history keeps its M with W = 0 (an umbra stays dark and keeps the pixel's memory of its visible lights);
    //    visible: it counts as a normal history with its stored (unshadowed) W. A penumbra re-draws visibility every frame.
    //  - Otherwise (spatial neighbours, or re-test off): the history counts at most _RestirOccludedCap samples. In an umbra
    //    that threw the pixel's memory away each time the occluded light won RIS again (8-2's skylight room boiled).
    bool retest = occluded && temporalHistory && _RestirOccludedRetest != 0 && Wh > 0.0;
    if (occluded && !retest) { M = min(M, _RestirOccludedCap); Wh = 0.0; }
    uint prevLight = RestirLight(h);
    uint light = ULTRART_NO_LIGHT;
    uint bary = 0;
    float3 L = 0, c = 0;
    float dist = 0;
    float w = 0, pHat = 0;
    if ((prevLight & ULTRART_EMISSIVE) != 0u)
    {
        // Triangle indices are stable while _EmissiveHistoryValid. A triangle whose surface left the RTAS (room hidden,
        // object destroyed) is dropped like a light that is gone.
        uint tri = prevLight & 0xFFFFu;
        if (_EmissiveHistoryValid == 0 || !EmissiveActive(tri)) return false;
        light = prevLight;
        bary = RestirBary(h);
        float area;
        if (EmissiveEval(tri, bary, pos, n, L, dist, c, area)) { pHat = LightLuminance(c); w = pHat * Wh * M; }
    }
    else if (prevLight != ULTRART_NO_LIGHT)
    {
        if (prevLight >= (uint)_PrevLightCount) return false;
        light = _LightRemap[prevLight];
        // The light is gone (switched off with its room, destroyed): this history describes a light set that
        // no longer exists, so drop it rather than count its M with zero weight.
        if (light == ULTRART_NO_LIGHT) return false;
        if (LightEval(light, pos, n, layer, L, dist, c)) { pHat = LightLuminance(c) * VisFactor(light, pos, L, dist); w = pHat * Wh * M; }
    }
    if (retest && w > 0.0)
    {
        // The same ray the final shading traces (with the player split, V1 is left out as there).
        uint playerMask = g_ShadowPlayerMask;
#ifdef ULTRART_MOVERS
        if (_UltraRTPlayerSplit != 0 && _Mode == 0) g_ShadowPlayerMask = 0u;
#endif
        bool emitter = (light & ULTRART_EMISSIVE) != 0u;
        bool vis = emitter ? EmissiveVisible(pos, n, L, dist) : LightVisible(light, pos, n, L, dist);
        // Blocked twice in a row. An umbra (the light's centre is blocked as well) keeps the full M; a penumbra (or an
        // emitter) is capped, or one more blocked draw would hold it dark for the whole history.
        bool umbra = !vis && !emitter && _RestirRetestUmbra != 0 && !LightCentreVisible(light, pos, n, L, dist);
        g_ShadowPlayerMask = playerMask;
        if (!vis) { w = 0.0; Wh = 0.0; if (!umbra) M = min(M, _RestirRetestCap); }
    }
    else if (retest) Wh = 0.0;
#ifdef ULTRART_MOVERS
    // The history's shadow ray crosses a moving occluder: its visibility (W = 0 when it was occluded) is stale. Occluded
    // history is dropped (the player's shadow moving away would otherwise linger for up to historyLimit frames of M);
    // visible history is kept at the fresh candidates' weight (the final shadow ray re-tests it if it wins).
    if (_UltraRTMoverCount > 0 && light != ULTRART_NO_LIGHT && dist > 0.0 && MoverCrosses(pos, L, dist))
    {
        g_MoverShadow = true;
        if (!(Wh > 0.0)) return false;
        float capM = (float)_RisCandidates;
        if (M > capM) { w *= capM / M; M = capM; }
    }
#endif
    // A history with no light (or W = 0 because its sample was occluded) still counts its M: it is evidence
    // that this pixel receives nothing from those samples. See docs/light-sampling.md.
    ReservoirAdd(r, light, bary, w, M, c, L, dist, Rand(rng), pHat);
    return true;
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

// History M cap at this pixel. Lights that appeared, disappeared, changed brightness or moved since the previous frame
// leave the history describing a different light set. rho = how much of this pixel's direct light changed (the sum of
// |p_now - p_before| over the changes, against the fresh candidates' estimate of the total, wSum / M). The history may
// keep at most a share tol / rho of the merged reservoir, i.e. cap = M_fresh * h / (1 - h), h = tol / rho. Pixels the
// changes don't reach keep their full history (a muzzle flash no longer resets ReSTIR over the whole screen).
float RestirChangeCap(Reservoir fresh, float3 pos, float3 n)
{
    float cap = _RestirParams.x;
    if (_LightChangeCount <= 0) return cap;
    float delta = 0, before = 0;
    for (int k = 0; k < _LightChangeCount; k++)
    {
        float3 L, c0, c1;
        float d;
        uint b = (uint)k * 8u;
        LightEvalRaw(_LightChanges[b], _LightChanges[b + 1], _LightChanges[b + 2], _LightChanges[b + 3], pos, n, L, d, c0);
        LightEvalRaw(_LightChanges[b + 4], _LightChanges[b + 5], _LightChanges[b + 6], _LightChanges[b + 7], pos, n, L, d, c1);
        float l0 = LightLuminance(c0), l1 = LightLuminance(c1);
        delta += abs(l1 - l0);
        before += l0;
    }
    float total = (fresh.M > 0.0 ? fresh.wSum / fresh.M : 0.0) + before;
    float rho = delta / max(total, 1e-6);
    if (rho <= _LightChangeTolerance) return cap;
    float h = saturate(_LightChangeTolerance / rho);
    float m = max(fresh.M, 1.0);
    return clamp(m * h / max(1.0 - h, 1e-3), m, cap);
}

// The primary hit in the previous frame: its pixel (pp, continuous), and the point and normal the stored planes use.
bool RestirReproject(uint2 dim, float3 pos, float3 n, out float2 pp, out float3 prevPos, out float3 vn)
{
    vn = g_PrimaryVirtual ? g_PrimaryVirtualN : n;
    prevPos = (g_PrimaryVirtual ? g_PrimaryVirtualPos : pos) + g_PrimaryMotion;
    pp = 0;
    float4 pc = mul(_PrevViewProj, float4(prevPos, 1.0));
    if (pc.w <= 1e-6) return false;
    pp = (pc.xy / pc.w * 0.5 + 0.5) * (float2)dim;
    return true;
}

// This pixel's visibility memory, from the previous frame (nearest pixel, validated like a reservoir, light indices
// remapped to this frame's). Once per primary hit: further DirectLighting calls (pathtrace's extra samples) keep it.
void VisLoad(uint2 dim, float3 pos, float3 n, float t)
{
    g_VisActive = _VisMemory != 0;
    if (g_VisLoaded) return;
    g_VisLoaded = true;
    g_VisSlot = uint3(0xFFFFu, 0xFFFFu, 0xFFFFu);
    if (!g_VisActive || _VisMemoryValid == 0) return;
    float2 pp;
    float3 prevPos, vn;
    if (!RestirReproject(dim, pos, n, pp, prevPos, vn) || any(pp < 0.0) || any(pp >= (float2)dim)) return;
    uint4 h = _ReservoirPrev[RestirIndex((uint2)pp, dim) + 1];
    if (!VisMatch(h, prevPos, vn, t)) return;
    [unroll] for (uint k = 0; k < 3; k++)
    {
        uint s = h[k] & 0xFFFFFFu;
        uint li = s & 0xFFFFu;
        if (li == 0xFFFFu || li >= (uint)_PrevLightCount) continue;
        uint cur = _LightRemap[li];
        if (cur != ULTRART_NO_LIGHT) g_VisSlot[k] = cur | (s & 0xFF0000u);
    }
}

// Returns whether the temporal (reprojected) reservoir was merged.
bool RestirReuse(inout Reservoir r, uint2 dim, float3 pos, float3 n, float t, uint layer, inout uint rng)
{
    if (_RestirHistoryValid == 0) return false;
    g_RestirCap = RestirChangeCap(r, pos, n);
    float2 pp;
    float3 prevPos, vn;
    if (!RestirReproject(dim, pos, n, pp, prevPos, vn)) return false;

    bool merged = false;
    if (all(pp >= 0.0) && all(pp < (float2)dim))
    {
        uint4 h = _ReservoirPrev[RestirIndex((uint2)pp, dim)];
        if (RestirMatch(h, prevPos, vn, t)) merged = RestirMerge(r, h, pos, n, layer, rng, true);
    }

    for (int k = 0; k < _RestirSpatial; k++)
    {
        float a = Rand(rng) * 6.28318530718;
        float rad = sqrt(Rand(rng)) * _RestirParams.w;
        float2 q = pp + float2(cos(a), sin(a)) * rad;
        if (any(q < 0.0) || any(q >= (float2)dim)) continue;
        uint4 h = _ReservoirPrev[RestirIndex((uint2)q, dim)];
        if (RestirMatch(h, prevPos, vn, t)) RestirMerge(r, h, pos, n, layer, rng, false);
    }
    return merged;
}

// Merge reservoir b (its own wSum and M) into a.
void ReservoirCombine(inout Reservoir a, Reservoir b, float u)
{
    a.wSum += b.wSum;
    a.M += b.M;
    if (b.wSum > 0.0 && u * a.wSum < b.wSum)
    {
        a.light = b.light; a.bary = b.bary; a.pHat = b.pHat;
        a.c = b.c; a.L = b.L; a.dist = b.dist;
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
    VisLoad(dim, pos, n, t);
    Reservoir r = RisCandidates(pos, n, layer, _RisCandidates, rng);
    bool temporal = _LightSampling >= 2;
    // A pixel without temporal history (disocclusion, screen edge in motion, first frame) would otherwise show an
    // 8-candidate RIS sample: give it more candidates (light evaluations only, no extra rays).
    if (temporal && !RestirReuse(r, dim, pos, n, t, layer, rng) && _RestirFreshCandidates > 0)
        ReservoirCombine(r, RisCandidates(pos, n, layer, _RestirFreshCandidates, rng), Rand(rng));

    float W = ReservoirW(r);
    float storedW = W;     // the history keeps the unshadowed W, flagged when occluded (re-tested next frame)
    bool occludedFinal = false;
#ifdef ULTRART_MOVERS
    if (W > 0.0 && _UltraRTMoverCount > 0 && MoverCrosses(pos, r.L, r.dist)) g_MoverShadow = true;
#endif
#ifdef ULTRART_MOVERS
    uint playerMask = g_ShadowPlayerMask;
    bool split = _UltraRTPlayerSplit != 0 && playerMask != 0u && _Mode == 0;
    if (split) g_ShadowPlayerMask = 0u;
#endif
    if (W > 0.0)
    {
        unshadowedSum += r.c * W;
        // With visibility reuse, occluded samples are stored with W = 0 so they stop propagating.
        bool visible = SampleVisible(r, pos, n);
        // Visibility memory: a blocked or clear draw toward this light (not one a moving occluder may have decided).
        bool observe = true;
#ifdef ULTRART_MOVERS
        if (_UltraRTMoverCount > 0 && MoverCrosses(pos, r.L, r.dist)) observe = false;
#endif
        if (observe) VisObserve(r.light, visible);
#ifdef ULTRART_MOVERS
        if (split && visible)
        {
            // V1 alone, along the sample's (unjittered) direction; skipped when the light is the player's own (as
            // SampleVisible decides: the player-light flag for lights, the player box for emitters; the 7-3 flashlight
            // used to get V1's shadow here) or the segment misses V1's bounds.
            bool own = (r.light & ULTRART_EMISSIVE) != 0u ? InPlayerBox(pos + r.L * r.dist) : _Lights[r.light * 4 + 1].w > 0.5;
            float3 inv = 1.0 / (abs(r.L) > 1e-8 ? r.L : (r.L >= 0.0 ? 1e-8 : -1e-8));
            float3 t0 = (_UltraRTPlayerModelMin.xyz - pos) * inv, t1 = (_UltraRTPlayerModelMax.xyz - pos) * inv;
            float a = max(max(min(t0.x, t1.x), min(t0.y, t1.y)), max(min(t0.z, t1.z), 0.02));
            float b = min(min(max(t0.x, t1.x), max(t0.y, t1.y)), min(max(t0.z, t1.z), r.dist));
            if (!own && a <= b)
            {
                RayDesc pr;
                pr.Origin = pos + n * 0.02;
                pr.Direction = r.L;
                pr.TMin = 0.0;
                pr.TMax = max(0.0, r.dist - 0.05);
                RayPayload sp = (RayPayload)0;
                sp.t = 1.0;
                TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, ULTRART_MASK_PLAYER, 0, 1, 0, pr, sp);
                if (sp.t >= 0.0) g_PlayerBlocked = r.c * (W * DetailScale(n, r.L) * _LightScale);
            }
        }
#endif
        if (visible)
        {
            float3 cd = r.c * (W * DetailScale(n, r.L));
            vertexSum += r.c * W;
            sum += cd;
            if ((r.light & ULTRART_EMISSIVE) != 0u) g_DirectEmissive = cd * _LightScale;
            else g_DirectAdditive = cd * (_LightScale * LightHybridWeight(r.light));
        }
        else { W = 0.0; occludedFinal = true; }
    }

#ifdef ULTRART_MOVERS
    if (split) g_ShadowPlayerMask = playerMask;
#endif
    if (temporal) _ReservoirCur[RestirIndex(id, dim)] = RestirPack(r, min(r.M, _RestirParams.x),
        _RestirOccludedRetest != 0 || _RestirVisReuse == 0 ? storedW : W, occludedFinal,
        g_PrimaryVirtual ? g_PrimaryVirtualN : n, g_PrimaryVirtual ? g_PrimaryVirtualPos : pos);
    if (g_VisActive) _ReservoirCur[RestirIndex(id, dim) + 1] =
        VisPack(g_PrimaryVirtual ? g_PrimaryVirtualN : n, g_PrimaryVirtual ? g_PrimaryVirtualPos : pos);
    g_VisActive = false; // bounce hits and reflections: plain targets
    if (_VisMemory == 2)
    {
        // Debug (ls.visibilityDebug): red = lowest remembered Vbar, green = remembered lights / 3.
        float lo = 1.0, used = 0.0;
        [unroll] for (uint k = 0; k < 3; k++)
            if ((g_VisSlot[k] & 0xFFFFu) != 0xFFFFu) { lo = min(lo, VisEstimate(g_VisSlot[k])); used += 1.0; }
        return float3(lo, used / 3.0, 0.0);
    }
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
