#ifndef ULTRART_COMMON_INCLUDED
#define ULTRART_COMMON_INCLUDED

// Shared between the ray generation shaders and the proxy material's hit shaders. Every hit/miss shader of a dispatch
// must use the same payload size, so it is kept at 64 bytes (16 dwords). Layout:
//   albedo, alpha, normal, t, instance  as named
//   emissionHalf  emission rgb as 3 halves (PayloadEmission; every consumer stores it as half anyway)
//   motion     packed world-space motion of the hit (PackMotion) when the caller set ULTRART_PAYLOAD_WANT_MOTION, else 0
//   material   packed roughness / metalness / f0 / specular weight (UltraRTMaterial.hlsl, PackMaterialWord)
//   uvHalf     half(uv.x) | half(uv.y) << 16: debug views only (was float2 uv). Hybrid primary hits with the game's
//              cubemap: its term C (rgb unorm8) | keep factor k << 24 (UltraRTMaterial.hlsl).
//   primitive  PrimitiveIndex(): debug views only. Primary hits (ULTRART_PAYLOAD_WANT_MOTION) with a per-texel detail
//              normal (ULTRART_FLAG_DETAIL): bits 0-23 the interpolated vertex normal (PackOct24); bits 24-31 the game
//              cubemap's lighting contribution (with ULTRART_FLAG_GAMECUBE).
//   flags      bit 0: vertex normals missing (geometric normal used); bits 1-2: reflection mode; bit 3: `normal` is a
//              per-texel detail normal (docs/materials.md); bits 8-15: original layer; bits 16-23: reflection strength
//              (unorm8); bits 24-28: material class (see UltraRTMaterial.hlsl); bits 4-5: transparent surface kind (water / glass
//              / blood proxies under mask 0x10: 1 glass, 2 water, 3 blood; ULTRART_FLAG_TRANSPARENT_*).
//              In (ray gen -> hit): bit 30 = ULTRART_PAYLOAD_WANT_MOTION, bit 31 = ULTRART_FLAG_GAMECUBE (UltraRTMaterial.hlsl)
struct RayPayload
{
    float3 albedo;
    float  alpha;      // lit weight: 0 = fullbright surface, 1 = fully lit (game: lerp(1, lighting, vertexColor.a))
    float3 normal;     // world space, facing the ray
    float  t;          // hit distance, < 0 on miss
    uint2  emissionHalf;
    uint   motion;
    uint   instance;
    uint   material;
    uint   uvHalf;
    uint   primitive;
    uint   flags;
};

#define ULTRART_FLAG_NOFOG 0x40u // hit -> ray gen: the game draws this material without its distance fog (_FOG_OFF: 1-4's windows)
#define ULTRART_FLAG_TRANSPARENT_SHIFT 4u // hit -> ray gen: (flags >> 4) & 3 = the transparent kind (0 = an ordinary surface)
#define ULTRART_PAYLOAD_WANT_MOTION 0x40000000u // PathTrace's primary ray: return the hit's motion in p.motion
#define ULTRART_PAYLOAD_EFFECTS 0x08000000u     // ray gen -> hit: the effects ray (UltraRTEffects.hlsl); effect billboards
                                                // (EffectProxy.shader) ignore every ray without it
// ray gen -> hit: p.albedo.xy carries a ray cone (texture LOD, docs/materials.md "Texture LOD"): x the cone's width at
// the ray's origin, y its spread angle (width grows by y per unit of distance). TraceScene fills it per segment (g_Cone).
#define ULTRART_PAYLOAD_CONE 0x04000000u
// ray gen -> hit: the veil ray (VeilTrace.raytrace, docs/scene-proxies.md "Blended veils"): veil proxies (VeilProxy.shader)
// blend into the payload (albedo: sum of colour x alpha, normal.x: sum of alpha, alpha: transmittance) and ignore every
// other ray.
#define ULTRART_PAYLOAD_VEILS 0x02000000u
// ray gen -> any-hit: a visibility (shadow) ray whose p.albedo is the transmittance so far, starting at 1. Stained glass
// (_Transmit, docs/materials.md "Stained glass") multiplies its colour in and lets the ray through; its dark lead lines
// block. Shadow rays without it (albedo 0) see stained glass as before: opaque, transparent glass not at all.
#define ULTRART_PAYLOAD_SHADOW 0x01000000u
// ray gen -> any-hit: the transparent pass's ray (PathTrace TraceTransparent). Transparent instances that also carry the
// geometry bit (stained glass, for shadow rays) are ignored by every other ray.
#define ULTRART_PAYLOAD_TRANSPARENT 0x00800000u
#define ULTRART_FLAG_DETAIL 0x8u // ray gen -> hit: apply per-texel detail (primary rays only; reflection, bounce, cache and
                                 // volumetric rays skip it). Hit -> ray gen: p.normal carries a per-texel detail normal; on
                                 // primary rays (WANT_MOTION) the vertex normal is in p.primitive bits 0-23 (PackOct24)

// Unit vector in 24 bits (octahedral, 12 bits per axis).
uint PackOct24(float3 n)
{
    float2 o = n.xy / (abs(n.x) + abs(n.y) + abs(n.z));
    if (n.z < 0.0) o = (1.0 - abs(o.yx)) * float2(o.x >= 0.0 ? 1.0 : -1.0, o.y >= 0.0 ? 1.0 : -1.0);
    uint2 q = (uint2)round(saturate(o * 0.5 + 0.5) * 4095.0);
    return q.x | (q.y << 12);
}
float3 UnpackOct24(uint v)
{
    float2 o = float2(v & 0xFFFu, (v >> 12) & 0xFFFu) * (2.0 / 4095.0) - 1.0;
    float3 n = float3(o, 1.0 - abs(o.x) - abs(o.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

// RTAS instance masks (SceneProxies.cs): 0x01 geometry, 0x02 sky walls, 0x04 light fixtures, 0x08 glowing fixtures,
// 0x10 transparent water and glass, 0x20 portal quads, 0x40 layer-30 props, 0x80 V1's third-person model.
// Transparent surfaces are seen only by the transparent pass (PathTrace.raytrace, TransparentGen; docs/reflections.md
// "Water and glass"): every other ray uses ULTRART_MASK_SCENE and passes through them. The player model is not in
// ULTRART_MASK_SCENE either: camera rays never see it (the camera sits in its head); reflection, bounce and shadow rays
// add it per _UltraRTPlayerRays (UltraRTPortals.hlsl, PlayerRayMask; docs/reflections.md "The player in reflections").
#define ULTRART_MASK_SCENE       0x6Fu
#define ULTRART_MASK_TRANSPARENT 0x10u
#define ULTRART_MASK_PLAYER      0x80u

uint2 PackEmissionHalf(float3 e)
{
    e = min(e, 65000.0);
    return uint2(f32tof16(e.r) | (f32tof16(e.g) << 16), f32tof16(e.b));
}
float3 PayloadEmission(RayPayload p)
{
    return float3(f16tof32(p.emissionHalf.x), f16tof32(p.emissionHalf.x >> 16), f16tof32(p.emissionHalf.y));
}

uint PayloadLayer(RayPayload p) { return (p.flags >> 8) & 0xFFu; }
float2 PayloadUV(RayPayload p) { return float2(f16tof32(p.uvHalf), f16tof32(p.uvHalf >> 16)); }

struct AttributeData
{
    float2 barycentrics;
};

uint HashU32(uint x)
{
    x ^= x >> 16; x *= 0x7feb352dU;
    x ^= x >> 15; x *= 0x846ca68bU;
    x ^= x >> 16;
    return x;
}

float3 HashColor(uint x)
{
    uint h = HashU32(x);
    return float3(h & 0xFF, (h >> 8) & 0xFF, (h >> 16) & 0xFF) / 255.0;
}

// ---------------------------------------------------------------- motion (docs/denoiser.md, "Motion vectors")

// World-space motion of a surface point: its previous-frame position minus its current one, in one 32-bit word
// (stored in _InstanceId.y). Shared exponent: 3 x (sign + 8-bit magnitude) | (e + 16) << 27, with |d| <= 2^e.
// Error <= max|d| / 255 per component (0.4 mm at 10 cm/frame); 0 = static (anything under 10 um).
uint PackMotion(float3 d)
{
    float m = max(abs(d.x), max(abs(d.y), abs(d.z)));
    if (!(m >= 1e-5)) return 0u; // also NaN
    int e = clamp((int)ceil(log2(m)), -16, 15);
    float3 q = clamp(round(d * (255.0 * exp2((float)-e))), -255.0, 255.0);
    uint3 c = (uint3)abs(q) | ((uint3)(q < 0.0) << 8);
    return c.x | (c.y << 9) | (c.z << 18) | ((uint)(e + 16) << 27);
}

float3 UnpackMotion(uint v)
{
    if (v == 0u) return 0;
    uint3 c = uint3(v, v >> 9, v >> 18) & 0x1FFu;
    float3 s = 1.0 - 2.0 * (float3)((c >> 8) & 1u);
    return s * (float3)(c & 0xFFu) * (exp2((float)((int)(v >> 27) - 16)) / 255.0);
}

#endif
