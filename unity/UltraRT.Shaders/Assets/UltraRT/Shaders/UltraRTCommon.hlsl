#ifndef ULTRART_COMMON_INCLUDED
#define ULTRART_COMMON_INCLUDED

// Shared between the ray generation shader and the proxy material's hit shaders.
struct RayPayload
{
    float3 albedo;
    float  alpha;
    float3 normal;     // world space, facing the ray
    float  t;          // hit distance, < 0 on miss
    float3 emission;
    uint   instance;
    float2 uv;
    uint   primitive;
    uint   flags;      // bit 0: vertex normals missing (geometric normal used)
};

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
