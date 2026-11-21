#ifndef ULTRART_MATERIAL_INCLUDED
#define ULTRART_MATERIAL_INCLUDED

// Surface materials for reflections (docs/reflections.md). Shared by the proxy hit shader, PathTrace, DebugView,
// Reflections.compute and the composites. Values come from MaterialClasses.cs (per proxy material).
//
// Payload (RayPayload, UltraRTCommon.hlsl):
//   material: roughness 8 | metalness 8 << 8 | f0 8 << 16 | specular weight 8 << 24   (all unorm8)
//   flags:    bit 0 geometric normal | reflection mode 2 << 1 | layer 8 << 8 | reflection strength 8 << 16 | class 5 << 24
//
// Surface G-buffer (_Surface, RWTexture2D<uint4>; replaces the old _AlbedoOut + _EmissionOut + _LitWeight, freeing UAVs):
//   x: albedo rgb unorm8 x3 | class 5 << 24 | reflection mode 2 << 29 (bit 31 unused: was the hybrid mode's game cubemap)
//   y: half(emission.r) | half(emission.g) << 16
//   z: half(emission.b) (sign bit: no fog) | reflection strength 8 << 16 | lit weight 8 << 24
//   w: the payload's material word

#define ULTRART_REFL_NONE     0u
#define ULTRART_REFL_SPECULAR 1u // GGX lobe weighted by Fresnel and the specular weight
#define ULTRART_REFL_BLEND    2u // ULTRAKILL REFLECTION, _CubeMode 0: lerp(colour, reflection, strength)
#define ULTRART_REFL_ADD      3u // ULTRAKILL REFLECTION, _CubeMode 1: colour + strength * reflection

#define ULTRART_METAL_F0_MIN 0.5 // minimum luminance of a metal's albedo-tinted f0 (SpecularTerms)

#define ULTRART_CLASS_COUNT 23u // 0-20 ULTRAKILL SurfaceType, 21 other/unknown, 22 mirror (name allow-list)
#define ULTRART_CLASS_FLESH 2u  // SurfaceClass.Flesh (MaterialClasses.cs): subsurface wrap light (UltraRTLights.hlsl, FleshSssBegin)
#define ULTRART_CLASS_WET   13u // SurfaceClass.Wet: water (ripple normals on the transparent ray)

struct SurfaceMaterial
{
    float roughness;
    float metalness;
    float f0;
    float weight;       // per-material specular weight (scaled by the global setting)
    float reflStrength; // REFLECTION materials: _ReflectionStrength
    uint  mode;         // ULTRART_REFL_*
    uint  cls;          // class id for debug views
};

uint Unorm8(float x) { return (uint)round(saturate(x) * 255.0); }
float FromUnorm8(uint x) { return (float)(x & 0xFFu) * (1.0 / 255.0); }

uint PackMaterialWord(float roughness, float metalness, float f0, float weight)
{
    return Unorm8(roughness) | (Unorm8(metalness) << 8) | (Unorm8(f0) << 16) | (Unorm8(weight) << 24);
}

uint PackMaterialFlags(uint mode, float reflStrength, uint cls)
{
    return ((mode & 3u) << 1) | (Unorm8(reflStrength) << 16) | ((cls & 0x1Fu) << 24);
}

SurfaceMaterial UnpackMaterial(uint word, uint mode, float reflStrength, uint cls)
{
    SurfaceMaterial m;
    m.roughness = FromUnorm8(word);
    m.metalness = FromUnorm8(word >> 8);
    m.f0 = FromUnorm8(word >> 16);
    m.weight = FromUnorm8(word >> 24);
    m.reflStrength = reflStrength;
    m.mode = mode & 3u;
    m.cls = cls;
    return m;
}

SurfaceMaterial MaterialFromPayload(uint word, uint flags)
{
    return UnpackMaterial(word, (flags >> 1) & 3u, FromUnorm8(flags >> 16), (flags >> 24) & 0x1Fu);
}

// Payload flags (hit -> ray gen): the game draws this material without its distance fog (_FOG_OFF, the "NoFog"
// materials: 0-1's giant gears). Kept in _Surface as the sign bit of emission.b's half (emission is never negative), so
// the fog pass leaves the pixel unfogged as the game does (SurfaceNoFog; VolumetricsComposite.shader, pass 0).
#define ULTRART_FLAG_NOFOG 0x40u

uint4 PackSurface(float3 albedo, float3 emission, uint materialWord, uint flags, float litWeight = 1.0)
{
    uint mode = (flags >> 1) & 3u;
    uint cls = (flags >> 24) & 0x1Fu;
    uint rs = (flags >> 16) & 0xFFu;
    return uint4(Unorm8(albedo.r) | (Unorm8(albedo.g) << 8) | (Unorm8(albedo.b) << 16) | (cls << 24) | (mode << 29),
                 f32tof16(emission.r) | (f32tof16(emission.g) << 16),
                 (f32tof16(max(emission.b, 0.0)) & 0x7FFFu) | ((flags & ULTRART_FLAG_NOFOG) != 0u ? 0x8000u : 0u) | (rs << 16) | (Unorm8(litWeight) << 24),
                 materialWord);
}

float3 SurfaceAlbedo(uint4 s) { return float3(FromUnorm8(s.x), FromUnorm8(s.x >> 8), FromUnorm8(s.x >> 16)); }
float3 SurfaceEmission(uint4 s) { return float3(f16tof32(s.y), f16tof32(s.y >> 16), f16tof32(s.z & 0x7FFFu)); }
bool SurfaceNoFog(uint4 s) { return (s.z & 0x8000u) != 0u; }
float SurfaceLitWeight(uint4 s) { return FromUnorm8(s.z >> 24); }
SurfaceMaterial SurfaceMat(uint4 s) { return UnpackMaterial(s.w, (s.x >> 29) & 3u, FromUnorm8(s.z >> 16), (s.x >> 24) & 0x1Fu); }

// Specular terms shared by the ray generation shader (non-denoised output) and Reflections.compute (resolve):
//   final = diffuse * keep + specW * Lrefl
// where diffuse is the colour without reflections (albedo * lighting + emission's lit part) and Lrefl the reflected
// radiance. params: x global specular weight, y global reflection strength.
//   SPECULAR: Schlick Fresnel at the shading normal (roughness damps the grazing peak), f0 tinted by albedo for metals.
//     ULTRAKILL's metal textures are dark painted diffuse colours (luma ~0.1-0.2), far below real metal F0, so a metal's
//     f0 keeps the albedo's tint but is raised to at least ULTRART_METAL_F0_MIN luminance.
//     Energy-conserving blend: keep = 1 - w * lerp(luma(F), 1, metalness).
//   BLEND / ADD: the game's REFLECTION model with RT radiance instead of its cubemap, no Fresnel (matches the game:
//     blend lerp(colour, refl, RS); additive colour + RS^2 * refl, since the game scales its cube sample by RS twice).
void SpecularTerms(SurfaceMaterial m, float3 albedo, float NdotV, float2 params, out float3 specW, out float keep)
{
    specW = 0;
    keep = 1.0;
    if (m.mode == ULTRART_REFL_SPECULAR)
    {
        float la = dot(albedo, float3(0.2126, 0.7152, 0.0722));
        float3 metalF0 = saturate(albedo * max(1.0, ULTRART_METAL_F0_MIN / max(la, 1e-3)));
        float3 f0 = lerp(m.f0.xxx, metalF0, m.metalness);
        float fc = pow(1.0 - saturate(NdotV), 5.0);
        float3 F = f0 + (max((1.0 - m.roughness).xxx, f0) - f0) * fc;
        float w = saturate(m.weight * params.x);
        specW = F * w;
        float lumF = dot(F, float3(0.2126, 0.7152, 0.0722));
        keep = 1.0 - w * lerp(lumF, 1.0, m.metalness);
    }
    else if (m.mode == ULTRART_REFL_BLEND)
    {
        float s = saturate(m.reflStrength * params.y);
        specW = s.xxx;
        keep = 1.0 - s;
    }
    else if (m.mode == ULTRART_REFL_ADD)
    {
        specW = (m.reflStrength * m.reflStrength * params.y).xxx;
    }
}

// Debug colours, one per class (ULTRAKILL SurfaceType order).
float3 ClassColor(uint cls)
{
    switch (cls)
    {
        case 0:  return float3(0.45, 0.45, 0.45); // Generic
        case 1:  return float3(0.60, 0.20, 0.25); // Carpet
        case 2:  return float3(0.95, 0.45, 0.55); // Flesh
        case 3:  return float3(0.55, 0.95, 1.00); // Glass
        case 4:  return float3(0.25, 0.75, 0.20); // Grass
        case 5:  return float3(0.55, 0.50, 0.40); // Gravel
        case 6:  return float3(0.30, 0.40, 1.00); // Hologram
        case 7:  return float3(0.95, 0.95, 0.85); // Marble
        case 8:  return float3(0.70, 0.45, 0.20); // MetalStumpy
        case 9:  return float3(0.50, 0.55, 0.65); // MetalGrill
        case 10: return float3(0.75, 0.75, 0.90); // MetalThin
        case 11: return float3(0.40, 0.33, 0.28); // Rubble
        case 12: return float3(0.90, 0.80, 0.50); // Sand
        case 13: return float3(0.10, 0.45, 0.85); // Wet
        case 14: return float3(0.55, 0.33, 0.12); // Wood
        case 15: return float3(0.75, 0.50, 0.25); // WoodThin
        case 16: return float3(1.00, 0.85, 0.20); // MetalHigh
        case 17: return float3(0.80, 0.60, 0.10); // MetalLow
        case 18: return float3(0.35, 0.25, 0.15); // Dirt
        case 19: return float3(0.85, 0.30, 0.10); // Crunchy
        case 20: return float3(1.00, 1.00, 1.00); // Snow
        case 21: return float3(0.15, 0.05, 0.20); // other / unknown
        default: return float3(1.00, 0.00, 1.00); // mirror
    }
}

#endif
