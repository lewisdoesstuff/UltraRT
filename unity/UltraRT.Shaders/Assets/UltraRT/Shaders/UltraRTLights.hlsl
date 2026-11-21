#ifndef ULTRART_LIGHTS_INCLUDED
#define ULTRART_LIGHTS_INCLUDED

// Direct lighting for PathTrace.raytrace: ULTRAKILL's light model plus light selection.
//   _LightSampling 0: reference. Every light, one shadow ray each (DirectAll), uniform pick at the bounce.
//   _LightSampling 1: RIS. M candidates from an alias table, one shadow ray for the winner. No history (temporal
//   reuse, ReSTIR DI, was removed on 2026-11-19 with the hybrid mode that was its last user; docs/light-sampling.md).
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
//   [3] cos(spot/2) (-1 for non-spot), 1 / (cos(spot/4) - cos(spot/2)), unshadowed (1 = fill light), w: <= -1 the
//       light is an image through portal side -w - 1 (below, "Portal images"; Plugin.PortalLights.cs), else unused
// Area lights (per-level light overrides, LightOverrides.cs; docs/lighting-overrides.md) keep this layout:
//   [2] xyz the emission normal (rect/disc emit to its side only), w half-size x (sphere/disc: radius)
//   [3] x = -2 - half-size y (<= -2 marks an area light; every spot test sees "no cone"), y = shape | octahedral tangent
//       << 2 (shape 1 sphere, 2 rect, 3 disc; tangent 2 x 10 bits; an exact integer in a float)
//   They are evaluated at the point of the shape closest to the receiver (the game's falloff from there, times the
//   emitter cosine for rect/disc), so every sampler treats them as a deterministic light, and their shadow rays aim at a
//   uniform random point on the shape (soft shadows the size of the emitter).
#include "UltraRTPortalBuffers.hlsl" // portal images: the sides' travel matrices and openings
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

int      _LightSampling;     // 0 reference, 1 RIS
int      _RisCandidates;     // candidates per pixel at the primary hit
int      _BounceCandidates;  // candidates at the bounce hit
int      _RisLightCount;     // entries in _LightAlias (every shadowed light, once)
// Alias table over the shadowed lights (Vose). Entry: x asuint(threshold), y light | aliasLight << 16,
// z asuint(pdf(light)), w asuint(pdf(aliasLight)). pdf is the probability of picking that light index.
StructuredBuffer<uint4>  _LightAlias;
int      _FillCount;
StructuredBuffer<uint>   _FillLights;   // indices of unshadowed fill lights

// Moving occluders (docs/denoiser.md, "Moving shadows"): world AABBs of dynamic objects that moved in the last frames (the
// player's third-person model first, then the nearest moving enemies and props), each the union of this frame's and the
// previous frame's bounds, as min, max pairs. A shadow ray that crosses one may be shadowed differently than last frame:
// PathTrace flags the pixel for the denoiser's targeted anti-lag. Only PathTrace defines ULTRART_MOVERS (other includers never see these globals).
#ifdef ULTRART_MOVERS
StructuredBuffer<float4> _UltraRTMovers;
int _UltraRTMoverCount;
static bool g_MoverShadow = false; // the primary hit's light sample crossed a moving occluder
// Player shadow split (docs/denoiser.md, "Moving shadows"): with _UltraRTPlayerSplit, the primary hit's final shadow ray
// ignores V1's model, so the denoised lighting never holds the player's shadow; a second
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
float4   _EmissiveParams;       // x P(a RIS candidate is emissive), y EmissiveLightScale / pi, z min squared distance
float    _EmissiveBounceWeight; // weight of hit emission on bounce rays (0 while emissive triangles are sampled directly)

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

// ---------------------------------------------------------------- flesh subsurface (docs/materials.md, "Flesh subsurface")

// Light that enters thick tissue comes out again a little further on, reddened (blood absorbs green and blue): a wrap
// term around the terminator, tinted crimson, from each light's own (shadowed) sample. No constant term, no extra ray:
// where no light arrives, nothing is added, and a light behind a wall stays blocked by its shadow ray.
// Global (Plugin.Materials.cs): x strength (0 = off), y wrap (how far past the terminator, in cosine), z rim strength
// (view-dependent backlit scatter at grazing light), w unused.
float4 _UltraRTFleshSss;
// Set by the ray generation shader around the primary hit's direct light only (FleshSssBegin / FleshSssEnd): bounce,
// reflection, cache and particle evaluations keep the plain Lambert term.
// Only the wrap and the view are per-hit state; the tint and rim are read from the uniform where they are used, so the
// light-sampling loops keep four values fewer live (docs/performance.md, "2026-11-20 batch").
static float  g_SssWrap = 0.0;  // > 0: the surface being lit is flesh
static float3 g_SssView = 0;    // towards the eye

// Crimson scatter colour in lighting space, x 2 x strength. Deep blood red (display space), converted like a light colour
// so linear transport shows the same hue at the terminator, where the scatter is most of the light. Strength 1 = twice the
// plain wrap (the plain wrap on red alone was invisible next to the frame's noise).
float3 FleshSssTint() { return ToLight(float3(1.0, 0.14, 0.06)) * (2.0 * _UltraRTFleshSss.x); }

// The scatter added to Lambert's max(nl, 0) for a light along L (n: shading normal, nl = dot(n, L)): wrapped diffuse
// minus Lambert, which is 0 where the surface faces the light, w / (1 + w) at the terminator and 0 again at nl = -w;
// plus a backlit rim where the eye looks past the surface towards the light. Per channel it never takes the total over
// 1 (what the light gives a surface facing it): a strong scatter flattens the red shading, but no grazing face near a
// light comes out brighter than the face turned to it (strength 8 without the cap lit a pillar edge flat red).
float3 FleshScatter(float3 n, float3 L, float nl)
{
    float w = g_SssWrap;
    float lambert = saturate(nl);
    float s = max(0.0, saturate((nl + w) / (1.0 + w)) - lambert);
    float t = saturate(dot(g_SssView, -L + n * 0.4));
    s += max(_UltraRTFleshSss.z, 0.0) * (t * t * t) * (1.0 - lambert);
    return min(FleshSssTint() * s, 1.0 - lambert);
}

// ---------------------------------------------------------------- thin translucent surfaces (docs/materials.md, "Translucent shades")

// A thin translucent sheet (the game's TRANSMISSION keyword: 8-1's fabric lamp shades) passes part of the light that
// reaches its back side through to its front: a light behind the sheet lights it with g_Trans x |n.L| (diffuse
// transmission; the albedo the lighting is multiplied by tints it). Its shadow ray starts on the back side (VisibleRaw), so
// a lamp inside a shade lights the shade's outside and goes dark with the lamp. Only the primary hit's direct light
// (TranslucentBegin / TranslucentEnd around it, like the flesh scatter); unshadowed fill lights keep Lambert (they would
// reach through walls).
// Global (Plugin.Materials.cs): x the share of the back light that comes through (0 = off), yzw unused.
float4 _UltraRTTranslucency;
static float g_Trans = 0.0; // > 0: the surface being lit is translucent

// The lowest cosine a light may have and still light the surface: 0 (Lambert), -wrap on flesh, any on a translucent sheet.
float SssNdlMin() { return g_Trans > 0.0 ? -1.0 : -g_SssWrap; }
// The cosine term: Lambert in front, the transmitted share behind (translucent sheets only; elsewhere nl > 0 here).
float TransNdl(float nl) { return nl > 0.0 ? nl : g_Trans * -nl; }

#ifdef ULTRART_MATERIAL_INCLUDED
// cls: the hit's class (payload flags); v: towards the eye.
void FleshSssBegin(uint cls, float3 v)
{
    g_SssWrap = 0.0;
    [branch] if (cls == ULTRART_CLASS_FLESH && _UltraRTFleshSss.x > 0.0)
    {
        g_SssWrap = clamp(_UltraRTFleshSss.y, 0.01, 1.0);
        g_SssView = v;
    }
}
void FleshSssEnd() { g_SssWrap = 0.0; }
#endif
// flags: the hit's payload flags.
void TranslucentBegin(uint flags) { g_Trans = (flags & ULTRART_FLAG_TRANSLUCENT) != 0u ? max(_UltraRTTranslucency.x, 0.0) : 0.0; }
void TranslucentEnd() { g_Trans = 0.0; }

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
// The camera (xyz) and w = 1 while the player's own spot lights clip their shadow rays at the eye (LightShadowSetup;
// Plugin.cs, -ultrart-noeyeclip / bench plugin._playerLightEyeClip=0).
float4 _UltraRTPlayerEye;

// The transmittance of the last visibility ray that got through (VisibleRaw): 1, or the colour of the stained glass it
// crossed (Proxy.shader any-hit, docs/materials.md "Stained glass"). Read it right after a test that returned true.
static float3 g_ShadowTint;

// The one shadow-ray TraceRay. Every visibility test computes its ray first and traces through here once: each extra
// TraceRay site in a function that is inlined into the ray generation shaders costs (docs/performance.md, "One trace site").
bool VisibleRaw(float3 p, float3 n, float3 dir, float dist, uint mask)
{
    RayDesc r;
    // A light behind a translucent sheet (TranslucentBegin): the ray starts on the sheet's back side.
    r.Origin = p + n * (g_Trans > 0.0 && dot(dir, n) < 0.0 ? -0.02 : 0.02);
    r.Direction = dir;
    r.TMin = 0.0;
    r.TMax = max(0.0, dist - 0.05);
    RayPayload sp = (RayPayload)0;
    sp.t = 1.0; // stays positive if anything is hit; the miss shader sets it negative
    sp.albedo = 1.0; // transmittance: stained glass multiplies its colour in (ULTRART_PAYLOAD_SHADOW)
    sp.flags = ULTRART_PAYLOAD_SHADOW;
    ULTRART_COUNT_RAY(ULTRART_RAY_SHADOW);
    TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, mask, 0, 1, 0, r, sp);
    g_ShadowTint = sp.albedo;
    return sp.t < 0.0;
}

bool VisibleMasked(float3 p, float3 n, float3 dir, float dist, uint mask) { return VisibleRaw(p, n, dir, dist, mask | g_ShadowPlayerMask); }

// Geometry + light fixtures (0x01 | 0x04); sky walls (0x02) never block light.
bool Visible(float3 p, float3 n, float3 dir, float dist) { return VisibleMasked(p, n, dir, dist, 0x05u); }

// ---------------------------------------------------------------- portal images (docs/portals.md, "Scene lights through portals")
// A light in front of a portal side (Plugin.PortalLights.cs) is packed a second time as its image through the side: the
// light carried by the side's travel matrix (position, spot axis, area frame) and l3.w = -(side + 1). It lights what lies
// behind the side's exit, and only through the exit's opening: the line from a receiver to the image, carried back
// through the travel matrix, must cross the side's rectangle (LightEval), and its shadow ray runs in two segments,
// receiver to the opening on the receiving side, then from the matching point of the side's opening to the light on
// the light's side (LightRayVisible). Regular lights pay one compare.
bool LightIsPortalImage(float4 l3) { return l3.w < -0.5; }
// False in PathTrace's ray generations for frames without portal images (RayGen, RayGenPT; Plugin.cs picks RayGenImg /
// RayGenPTImg while any are packed): the code below then drops out at compile time. Code that never runs still cost
// ~0.1 ms of relight dispatch in every scene (docs/performance.md, "2026-11-20 batch"). Other shaders keep it on.
static bool g_PortalImages = true;
uint LightPortalSide(float4 l3) { return (uint)(-l3.w - 0.5); }

// Does the image (virtual position v) of a light through side s reach p through the opening? On the side's composed
// quad rows (PortalImageSegmentThrough): this sits in every light evaluation's code, inside the light-sampling loops,
// so it is kept small even though regular lights never run it (docs/performance.md, "2026-11-20 batch").
bool PortalImageReaches(uint s, float3 p, float3 v) { return PortalImageSegmentThrough(s, p, v); }

// Visibility along (L, dist) from p, for a light whose l3.w is lw: VisibleRaw, or for a portal image both segments (one
// TraceRay site in a loop). The ray must pass through the opening (a jittered soft-shadow ray may miss it: blocked).
// The portal quads themselves are never in a shadow mask (0x20 vs 0x05), so they don't block either segment.
// Kept lean for regular lights, which share its code (2026-11-20, docs/performance.md "2026-11-20 batch"): the opening
// test runs on the composed quad rows in the receiver's world, and the light's-side segment is only set up after the
// first segment got through, so little extra state stays live across the trace.
bool LightRayVisible(float lw, float3 p, float3 n, float3 L, float dist, uint mask)
{
    float len = dist, tP = 0.0;
    bool second = false;
    [branch] if (g_PortalImages && lw < -0.5)
    {
        uint k = (uint)(-lw - 0.5) * 3u;
        float4 q2 = _UltraRTPortalImageQuads[k + 2u];
        float za = dot(q2, float4(p, 1.0)); // the receiver, carried back: beyond the side's plane (quad z > 0)
        float dz = dot(q2.xyz, L);          // quad z per metre along the ray: < 0 toward the light
        if (za <= 0.0 || dz >= -1e-6) return false;
        tP = za / -dz;
        float4 h = float4(p + L * tP, 1.0); // where the ray crosses the exit's opening (the side's, carried back)
        if (abs(dot(_UltraRTPortalImageQuads[k], h)) > 0.5) return false;
        if (abs(dot(_UltraRTPortalImageQuads[k + 1u], h)) > 0.5) return false;
        // The receiving side's segment ends 2 cm before the exit's plane (whatever the exit is set in stays out of it);
        // the light's side starts 2 cm past the side's plane (VisibleRaw's offset along n = the carried direction).
        if (tP < dist) { len = tP + 0.03; second = dist - tP > 0.07; }
    }
    float3 o = p, on = n, d = L;
    float3 tint = 1.0;
    bool vis = true;
    [loop] for (uint seg = 0u; seg < 2u; seg++)
    {
        vis = VisibleRaw(o, on, d, len, mask);
        tint *= g_ShadowTint;
        if (!vis || !second) break;
        // The light's side: from the matching point of the side's opening, carried back through the travel.
        second = false;
        float3x3 m;
        float3 t;
        PortalTravel((uint)(-lw - 0.5), m, t);
        d = PortalBackDir(m, L);
        o = PortalBackPoint(m, t, p) + d * tP;
        on = d;
        len = dist - tP;
    }
    g_ShadowTint = tint;
    return vis;
}

// ULTRAKILL places many lights inside their fixtures (torch bowls, lamp housings). The game has no shadows, so that's
// invisible there; ray traced, the fixture shadows everything below its rim. Shadow rays toward point/spot lights
// therefore stop this far short of the light (capped at half the distance so nearby contact shadows survive).
static const float kLightFixtureClearance = 0.35;
float _LightRadius;      // soft-shadow light size (m); 0 = point lights
float _FixtureShadows;   // 1: light fixtures (torch bowls etc.) shadow their own lights
static uint g_ShadowRng; // seeded per pixel by each ray-generation shader

// V1's model casts no shadow from light i: flag 1 in _Lights[i*4+1].w = the player's own light; 0 < w <= 0.5 = a level
// light right beside V1 (a room's fill light at chest height: 5-2's cabin), skipped with probability 2w so the shadow
// fades out as V1 walks up to the light instead of popping (Plugin.FlagPlayerLights, docs/relight.md "V1 beside a light").
bool LightSkipsFixtures(float4 l3) { return l3.z > 0.1 && l3.z < 0.5; }

// _Lights[i*4+1].w also carries the light's reach above that flag: w = flag (0-1) + 2 x code, code 0-4 in quarters
// (LightPolicy.Reach = 1 - code / 4; Plugin.ApplyResolved, docs/lighting-overrides.md "Classes and rules"). Reach 1 is
// relight's falloff; reach 0 never exceeds the game's own falloff (the smaller of the two curves at every distance),
// so what the game's short falloff left dark stays dark.
float LightPlayerFlag(float w) { return w - 2.0 * floor(w * 0.5); }
float LightReach(float w) { return 1.0 - 0.25 * floor(w * 0.5); }

bool LightSkipsPlayer(uint i)
{
    float w = LightPlayerFlag(_Lights[i * 4 + 1].w);
    if (w > 0.75) return true;
    if (w <= 0.0) return false;
    return (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0 < 2.0 * w;
}

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

// The shadow ray toward light i (L, dist: towards its centre): the jittered direction and length and the instance mask.
// 0 = blocked without a ray (the jittered direction is below the surface), 1 = visible without a ray, 2 = trace it.
uint LightShadowSetup(uint i, float3 p, float3 n, inout float3 L, inout float dist, out uint mask)
{
    float4 al3 = _Lights[i * 4 + 3];
    // Fixtures (0x04) cast shadows unless disabled, or unless the light is a statue orb's (shadow flag 0.25: the statue
    // holding it is a fixture; SceneProxies.Orbs.cs).
    mask = _FixtureShadows > 0.5 && !LightSkipsFixtures(al3) ? 0x05u : 0x01u;
    [branch] if (LightIsArea(al3) && dist < 1.0e3)
    {
        // Area light: a uniform point on the emitter, so the shadow is as soft as the emitter is large.
        float u1 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float u2 = (HashU32(g_ShadowRng += 0x9E3779B9u) & 0xFFFFFFu) / 16777216.0;
        float3 lp = LightAreaSample(_Lights[i * 4], _Lights[i * 4 + 2], al3, p, u1, u2);
        float3 d = lp - p;
        dist = length(d);
        if (dist < 1e-4) return 1u;
        L = d / dist;
#ifndef ULTRART_LIGHT_ISOTROPIC
        if (dot(L, n) <= SssNdlMin()) return 0u; // flesh: past the terminator (FleshScatter)
#endif
    }
    else
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
#ifndef ULTRART_LIGHT_ISOTROPIC
            if (dot(L, n) <= SssNdlMin()) return 0u; // flesh: past the terminator (FleshScatter)
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
            if (dot(L, n) <= SssNdlMin()) return 0u; // flesh: past the terminator (FleshScatter)
#endif
        }
    }
    // The player's own spot lights end their shadow rays at the plane through the eye, square to the beam. 7-3's
    // head-mounted flashlight (Player/Main Camera/Flashlight) hangs about 10 m behind and 1.5 m above the eye so its cone
    // is wide where the player looks; a ray all the way to it was blocked by whatever stood behind the player (a wall,
    // the tree's canopy) and the whole pool switched on and off as the camera moved (docs/relight.md, "Player lights end
    // at the eye"). What stands in front of the player still shadows it.
    // Not for a portal image: its eye plane would be the camera's, on the other side of the portal.
    [branch] if (_UltraRTPlayerEye.w > 0.5 && al3.x > -0.999 && al3.w > -0.5 && dist < 1.0e3 && LightPlayerFlag(_Lights[i * 4 + 1].w) > 0.75)
    {
        float3 f = _Lights[i * 4 + 2].xyz; // the spot's forward
        float den = dot(L, f);             // < 0: the ray runs back up the beam
        if (den < -0.05)
        {
            float t = dot(_UltraRTPlayerEye.xyz - p, f) / den;
            if (t > 0.0 && t < dist) dist = t;
        }
    }
    dist -= min(kLightFixtureClearance, 0.5 * dist);
    // The player's own lights (a head-mounted flashlight, the held torch, muzzle flashes) sit inside or next to V1's
    // model: it never shadows them; nor level lights right beside it (flag set by Plugin.FlagPlayerLights).
    if (!LightSkipsPlayer(i)) mask |= g_ShadowPlayerMask;
    return 2u;
}

bool LightVisible(uint i, float3 p, float3 n, float3 L, float dist)
{
    uint mask;
    uint s = LightShadowSetup(i, p, n, L, dist, mask);
    if (s != 2u) return s == 1u;
    return LightRayVisible(_Lights[i * 4 + 3].w, p, n, L, dist, mask);
}

// LightVisible without the soft-shadow jitter: the ray to the light's centre (the sun's own direction), to tell an umbra
// from a penumbra (docs/light-sampling.md, "Occluded history"). The caller sets g_ShadowPlayerMask as for LightVisible.
bool LightCentreVisible(uint i, float3 p, float3 n, float3 L, float dist)
{
    uint mask = _FixtureShadows > 0.5 && !LightSkipsFixtures(_Lights[i * 4 + 3]) ? 0x05u : 0x01u;
    uint playerMask = g_ShadowPlayerMask;
    if (LightSkipsPlayer(i)) g_ShadowPlayerMask = 0u;
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
    // Unity compiles HLSL 2018: ?: and && evaluate both sides, so anything costly sits behind an if (docs/performance.md,
    // "Short-circuit"). As a ternary, LightAreaClosest ran for every light evaluation (+0.2-0.3 ms everywhere).
    float3 toL = local ? l0.xyz - p : l0.xyz;
    [branch] if (area) toL = LightAreaClosest(l0, l2, l3, p) - p;
    float d2 = max(dot(toL, toL), 1e-6);
    float d = sqrt(d2);
    L = toL / d;
    // Rect and disc emitters light their front side only, with a cosine lobe (sphere: every direction).
    float emit = 1.0;
    [branch] if (area)
    {
        if ((LightAreaCode(l3) & 3u) >= 2u) emit = dot(l2.xyz, -L);
        if (d2 <= 1e-6) emit = 1.0; // on the emitter itself
        if (emit <= 0.0) return false;
    }
#ifdef ULTRART_LIGHT_ISOTROPIC
    float ndl = emit; // a point in the air (lit particles): no surface orientation
#else
    float nl = dot(n, L);
    // Flesh (FleshSssBegin): lights a little behind the terminator still scatter in, but only lights with a shadow ray
    // (an unshadowed fill light there would reach through the wall it sits behind).
    if (nl <= (l3.z > 0.5 ? 0.0 : SssNdlMin())) return false;
    float ndl = TransNdl(nl) * emit; // max(nl, 0) except behind a translucent sheet
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
            if (LightPlayerFlag(l1.w) > 0.75 && ULTRART_PLAYER_LIGHT_NDL_POW > 1.0) phys = min(phys, gameL);
#endif
            phys = lerp(gameL, phys, saturate(_Relight.x));
            gameL = lerp(min(phys, gameL), phys, LightReach(l1.w));
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
            phys = lerp(game, phys, saturate(_Relight.x));
            game = lerp(min(phys, game), phys, LightReach(l1.w));
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
    if (lin && LightPlayerFlag(l1.w) > 0.75) ndl = pow(ndl, ULTRART_PLAYER_LIGHT_NDL_POW);
#endif
    float3 shape = ndl;
#ifndef ULTRART_LIGHT_ISOTROPIC
    [branch] if (g_SssWrap > 0.0) shape += FleshScatter(n, L, nl) * emit;
#endif
    c = ToLight(l1.rgb * scale) * (atten * shape);
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
    float4 l0 = _Lights[i * 4], l3 = _Lights[i * 4 + 3];
    bool lit = LightEvalRaw(l0, _Lights[i * 4 + 1], _Lights[i * 4 + 2], l3, p, n, L, dist, c);
    // A portal image lights only what sees it through the exit's opening (crisp: the opening is the image's shape).
    [branch] if (g_PortalImages && lit && LightIsPortalImage(l3))
    {
        if (!PortalImageReaches(LightPortalSide(l3), p, l0.xyz)) { c = 0; lit = false; }
    }
    return lit;
}

float3 LightFrom(uint i, float3 p, float3 n, uint layer)
{
    float3 L, c;
    float dist;
    if (!LightEval(i, p, n, layer, L, dist, c)) return 0;
    float3 tint = 1.0;
    if (!LightUnshadowed(i)) { if (!LightVisible(i, p, n, L, dist)) return 0; tint = g_ShadowTint; } // nested: && does not short-circuit
    return c * tint;
}

// ---------------------------------------------------------------- emissive triangles

// The point on a triangle: 10-bit stratum centres folded into the triangle, so it is uniform over the triangle's area
// and exactly reproducible from 20 bits.
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
    float nlS = dot(n, L);
    if (nlS <= (g_Trans > 0.0 ? -1.0 : g_SssWrap > 0.0 ? -g_SssWrap : 1e-4)) return false; // flesh: a little past the terminator (FleshScatter); translucent: behind too
    float3 cosS = TransNdl(nlS);
    [branch] if (g_SssWrap > 0.0) cosS += FleshScatter(n, L, nlS);
#endif
    float cosL = -dot(nl, L);
    if ((flags & 1u) != 0u) cosL = abs(cosL);
    if (cosL <= 1e-4) return false;

    float3 le = ToLight(float3(f16tof32(t1.w), f16tof32(t1.w >> 16), f16tof32(t2.w)));
    c = le * (_EmissiveParams.y * cosS * cosL / max(d2, _EmissiveParams.z));
    return true;
}

// Visibility of an emitter sample: the ray stops just short of the emitter, so the emitter can't shadow itself.
void EmissiveShadowSetup(float3 p, inout float dist, float3 L, out uint mask)
{
    mask = 0x05u;
    if (!InPlayerBox(p + L * dist)) mask |= g_ShadowPlayerMask; // an emitter at the player (see _UltraRTPlayerBoxMin)
    dist = dist * 0.999 - 0.02;
}

bool EmissiveVisible(float3 p, float3 n, float3 L, float dist)
{
    uint mask;
    EmissiveShadowSetup(p, dist, L, mask);
    return VisibleRaw(p, n, L, dist, mask);
}

// Visibility of light code `code` (a light index, or ULTRART_EMISSIVE | triangle) along (L, dist): one TraceRay site for
// both kinds.
bool VisibleTo(uint code, float3 p, float3 n, float3 L, float dist)
{
    uint mask, s = 2u;
    float lw = 0.0; // a light's l3.w: a portal image traces two segments (LightRayVisible)
    [branch] if ((code & ULTRART_EMISSIVE) != 0u) EmissiveShadowSetup(p, dist, L, mask);
    else { s = LightShadowSetup(code, p, n, L, dist, mask); lw = _Lights[code * 4 + 3].w; }
    if (s != 2u) return s == 1u;
    return LightRayVisible(lw, p, n, L, dist, mask);
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
        if (EmissiveVisible(p, n, L, dist)) shadowed += c * g_ShadowTint;
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
                pHat = LightLuminance(c);
                w = pHat / ((1.0 - share) * pdf);
            }
        }
        ReservoirAdd(r, code, bary, w, 1.0, c, L, dist, Rand(rng), pHat);
    }
    return r;
}

bool SampleVisible(Reservoir r, float3 p, float3 n)
{
    // Not ?: (HLSL 2018 evaluates both sides: that traced an emitter ray and a light ray for every sample), and one
    // TraceRay site for both kinds (VisibleTo).
    return VisibleTo(r.light, p, n, r.L, r.dist);
}

// ---------------------------------------------------------------- entry points

// Direct light at the primary hit.
float3 DirectLighting(float3 pos, float3 n, uint layer, inout uint rng)
{
    if (_LightSampling <= 0)
    {
        float3 shadowed = 0;
        for (int i = 0; i < _LightCount; i++)
        {
            float3 L, c;
            float dist;
            if (!LightEval((uint)i, pos, n, layer, L, dist, c)) continue;
            bool lit = LightUnshadowed((uint)i);
            float3 tint = 1.0;
            if (!lit) { lit = LightVisible((uint)i, pos, n, L, dist); tint = g_ShadowTint; } // not ||: it does not short-circuit
            if (lit) shadowed += c * tint;
        }
        float3 emUnshadowed;
        float3 em = EmissiveDirect(pos, n, _EmissiveRefSamples, rng, emUnshadowed);
        return (shadowed + em) * _LightScale;
    }

    float3 sum = FillLighting(pos, n, layer);
    Reservoir r = RisCandidates(pos, n, layer, _RisCandidates, rng);
    float W = ReservoirW(r);
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
        bool visible = SampleVisible(r, pos, n);
        float3 tint = g_ShadowTint;
#ifdef ULTRART_MOVERS
        if (split && visible)
        {
            // V1 alone, along the sample's (unjittered) direction; skipped when the light is the player's own (as
            // SampleVisible decides: the player-light flag for lights, the player box for emitters; the 7-3 flashlight
            // used to get V1's shadow here) or the segment misses V1's bounds.
            bool own = (r.light & ULTRART_EMISSIVE) != 0u ? InPlayerBox(pos + r.L * r.dist) : LightSkipsPlayer(r.light);
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
                ULTRART_COUNT_RAY(ULTRART_RAY_PLAYER_SHADOW);
                TraceRay(_Scene, RAY_FLAG_CULL_BACK_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER, ULTRART_MASK_PLAYER, 0, 1, 0, pr, sp);
                if (sp.t >= 0.0) g_PlayerBlocked = r.c * (W * _LightScale);
            }
        }
#endif
        if (visible) sum += r.c * tint * W;
    }

#ifdef ULTRART_MOVERS
    if (split) g_ShadowPlayerMask = playerMask;
#endif
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
    [branch] if (W > 0.0) { if (SampleVisible(r, p, n)) sum += r.c * g_ShadowTint * W; } // nested: no shadow ray without a sample
    return sum * _LightScale;
}

#endif
