#ifndef ULTRART_CACHE_INCLUDED
#define ULTRART_CACHE_INCLUDED

// World-space radiance cache (SHaRC-style spatial hash grid). See docs/radiance-cache.md.
//
//   CacheUpdate.raytrace (#define ULTRART_CACHE_WRITE): sparse multi-bounce paths insert cells and add samples.
//   CacheResolve.compute: blends each cell's new samples into its history, evicts stale cells.
//   PathTrace.raytrace: read-only lookups (no UAVs).
//
// A cell is keyed by its quantised position (cell size grows with the distance to the camera), its level, and the
// dominant axis of the surface normal (6 bins). Each table slot holds a 32-bit checksum of the key (0 = empty). The slot
// is chosen by an independent 32-bit hash; probing stays inside an aligned bucket of ULTRART_CACHE_BUCKET slots.
//
// What a cell stores (demodulated, game units like PathTrace's `lighting`):
//   total    = direct + indirect lighting arriving at the surface (what a bounce hit multiplies by its albedo)
//   indirect = the indirect part only (for the primary-hit query mode, where direct light comes from ReSTIR)
//
// The includer must declare, before this file: _CamPos (float4) and HashU32 (UltraRTCommon.hlsl).

#define ULTRART_CACHE_BUCKET 8u
#define ULTRART_CACHE_MAX_LEVEL 15

#ifdef ULTRART_CACHE_WRITE
RWStructuredBuffer<uint> _CacheKeys;     // checksum per slot, 0 = empty
RWStructuredBuffer<uint> _CacheAccum;    // 8 uints per slot: total rgb, count, indirect rgb, last frame (fixed point)
#else
StructuredBuffer<uint>   _CacheKeys;
#endif
// Per slot: x half(total.r) | half(total.g) << 16, y half(total.b) | half(indirect.r) << 16,
//           z half(indirect.g) | half(indirect.b) << 16, w half(M samples) | half(avg samples per frame) << 16
StructuredBuffer<uint4>  _CacheResolved;

int      _CacheMode;      // 0 off, 1 query at bounce hits, 2 query indirect at the primary hit (no bounce ray)
uint     _CacheCapacity;  // slots, a power of two (>= ULTRART_CACHE_BUCKET)
float4   _CacheGrid;      // x base cell size (m), y 1 / LOD distance (m), z normal offset (fraction of a cell), w level fallback (0/1)
float4   _CacheQuery;     // x min samples for a valid cell, y primary-mode jitter (fraction of a cell), z, w unused

// ---------------------------------------------------------------- keys

uint CacheNormalBin(float3 n)
{
    float3 a = abs(n);
    if (a.x >= a.y && a.x >= a.z) return n.x >= 0.0 ? 0u : 1u;
    if (a.y >= a.z) return n.y >= 0.0 ? 2u : 3u;
    return n.z >= 0.0 ? 4u : 5u;
}

// Continuous level: 0 inside the LOD distance, +1 per doubling of the distance beyond it.
float CacheLevelF(float3 p)
{
    float d = length(p - _CamPos.xyz);
    return log2(max(d * _CacheGrid.y, 1.0));
}

float CacheCellSize(int level) { return _CacheGrid.x * exp2((float)level); }

// A second, independent mix for the checksum (the slot hash uses HashU32).
uint CacheHash2(uint x)
{
    x = x * 747796405u + 2891336453u;
    x = ((x >> ((x >> 28u) + 4u)) ^ x) * 277803737u;
    return (x >> 22u) ^ x;
}

// Slot of the key's bucket and its checksum (never 0).
void CacheKey(float3 p, float3 n, int level, out uint bucket, out uint check)
{
    float cell = CacheCellSize(level);
    // Pushing the point half a cell along its normal centres axis-aligned surfaces that lie on grid planes (ULTRAKILL's
    // level geometry sits on round coordinates), so they don't flicker between two cells. It also keeps the samples of a
    // floor next to a wall on the floor's side.
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

    bucket = h & (_CacheCapacity - 1u) & ~(ULTRART_CACHE_BUCKET - 1u);
    check = max(c, 1u);
}

int CacheLevel(float3 p) { return min((int)CacheLevelF(p), ULTRART_CACHE_MAX_LEVEL); }

bool CacheFind(uint bucket, uint check, out uint slot)
{
    slot = 0;
    [unroll]
    for (uint k = 0; k < ULTRART_CACHE_BUCKET; k++)
    {
        if (_CacheKeys[bucket + k] == check) { slot = bucket + k; return true; }
    }
    return false;
}

void CacheDecode(uint4 r, out float3 total, out float3 indirect, out float M)
{
    total = float3(f16tof32(r.x), f16tof32(r.x >> 16), f16tof32(r.y));
    indirect = float3(f16tof32(r.y >> 16), f16tof32(r.z), f16tof32(r.z >> 16));
    M = f16tof32(r.w);
}

// Cached lighting at (p, n) with enough samples; also tries the neighbouring level, since cells change level as the
// camera moves and the new level starts cold.
bool CacheLookup(float3 p, float3 n, out float3 total, out float3 indirect)
{
    total = 0; indirect = 0;
    if (_CacheMode <= 0) return false;
    float lf = CacheLevelF(p);
    int level = min((int)lf, ULTRART_CACHE_MAX_LEVEL);
    uint bucket, check, slot;
    float M;
    CacheKey(p, n, level, bucket, check);
    if (CacheFind(bucket, check, slot))
    {
        CacheDecode(_CacheResolved[slot], total, indirect, M);
        if (M >= _CacheQuery.x) return true;
    }
    if (_CacheGrid.w > 0.5)
    {
        int other = frac(lf) < 0.5 ? level - 1 : level + 1;
        if (other >= 0 && other <= ULTRART_CACHE_MAX_LEVEL)
        {
            CacheKey(p, n, other, bucket, check);
            if (CacheFind(bucket, check, slot))
            {
                CacheDecode(_CacheResolved[slot], total, indirect, M);
                if (M >= _CacheQuery.x) return true;
            }
        }
    }
    total = 0; indirect = 0;
    return false;
}

// Lighting arriving at a bounce hit (direct + all further bounces). False on a miss: use the 1-sample estimate.
bool CacheQueryTotal(float3 p, float3 n, out float3 total)
{
    float3 indirect;
    return CacheLookup(p, n, total, indirect);
}

// Indirect lighting at a primary hit, from a point jittered within its cell's tangent plane, so the cell grid turns into
// noise the denoiser can filter instead of visible blocks.
bool CacheQueryIndirect(float3 p, float3 n, float u1, float u2, out float3 indirect)
{
    float cell = CacheCellSize(CacheLevel(p));
    float3 t = abs(n.y) < 0.999 ? normalize(cross(n, float3(0, 1, 0))) : float3(1, 0, 0);
    float3 b = cross(n, t);
    float3 pj = p + (t * (u1 - 0.5) + b * (u2 - 0.5)) * (cell * _CacheQuery.y);
    float3 total;
    return CacheLookup(pj, n, total, indirect);
}

// ---------------------------------------------------------------- update (CacheUpdate.raytrace only)

#ifdef ULTRART_CACHE_WRITE

#define ULTRART_CACHE_NONE 0xFFFFFFFFu
#define ULTRART_CACHE_SCALE 2048.0 // fixed point: 1/2048 of a lighting unit

// Find or insert the cell of (p, n). Returns ULTRART_CACHE_NONE when its bucket is full.
uint CacheInsert(float3 p, float3 n)
{
    uint bucket, check;
    CacheKey(p, n, CacheLevel(p), bucket, check);
    // Look for the key first: an evicted slot earlier in the bucket must not get a duplicate.
    uint firstEmpty = ULTRART_CACHE_BUCKET;
    [unroll]
    for (uint k = 0; k < ULTRART_CACHE_BUCKET; k++)
    {
        uint v = _CacheKeys[bucket + k];
        if (v == check) return bucket + k;
        if (v == 0u && firstEmpty == ULTRART_CACHE_BUCKET) firstEmpty = k;
    }
    for (uint j = firstEmpty; j < ULTRART_CACHE_BUCKET; j++)
    {
        uint prev;
        InterlockedCompareExchange(_CacheKeys[bucket + j], 0u, check, prev);
        if (prev == 0u || prev == check) return bucket + j;
    }
    return ULTRART_CACHE_NONE;
}

void CacheAccumulate(uint slot, float3 total, float3 indirect, float maxValue)
{
    if (slot == ULTRART_CACHE_NONE) return;
    // Rounded fixed point; NaN/negative guarded by the clamp (max(NaN, 0) is 0 on GPUs).
    uint3 t = (uint3)(clamp(total, 0.0, maxValue) * ULTRART_CACHE_SCALE + 0.5);
    uint3 i = (uint3)(clamp(indirect, 0.0, maxValue) * ULTRART_CACHE_SCALE + 0.5);
    uint base = slot * 8u;
    InterlockedAdd(_CacheAccum[base + 0u], t.x);
    InterlockedAdd(_CacheAccum[base + 1u], t.y);
    InterlockedAdd(_CacheAccum[base + 2u], t.z);
    InterlockedAdd(_CacheAccum[base + 3u], 1u);
    InterlockedAdd(_CacheAccum[base + 4u], i.x);
    InterlockedAdd(_CacheAccum[base + 5u], i.y);
    InterlockedAdd(_CacheAccum[base + 6u], i.z);
}

#endif // ULTRART_CACHE_WRITE

#endif
