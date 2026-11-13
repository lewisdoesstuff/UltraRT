#ifndef ULTRART_COMMON_INCLUDED
#define ULTRART_COMMON_INCLUDED

// Shared between the ray generation shaders and the proxy material's hit shaders. Every hit/miss shader of a dispatch
// must use the same payload size, so it is kept at 64 bytes (16 dwords). Layout:
//   albedo, alpha, normal, t, emission, instance  as named
//   material   packed roughness / metalness / f0 / specular weight (UltraRTMaterial.hlsl, PackMaterialWord)
//   uvHalf     half(uv.x) | half(uv.y) << 16: debug views only (was float2 uv)
//   primitive  PrimitiveIndex(): debug views only
//   flags      bit 0: vertex normals missing (geometric normal used); bits 1-2: reflection mode; bits 8-15: original
//              layer; bits 16-23: reflection strength (unorm8); bits 24-28: material class (see UltraRTMaterial.hlsl)
struct RayPayload
{
    float3 albedo;
    float  alpha;      // lit weight: 0 = fullbright surface, 1 = fully lit (game: lerp(1, lighting, vertexColor.a))
    float3 normal;     // world space, facing the ray
    float  t;          // hit distance, < 0 on miss
    float3 emission;
    uint   instance;
    uint   material;
    uint   uvHalf;
    uint   primitive;
    uint   flags;
};

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

#endif
