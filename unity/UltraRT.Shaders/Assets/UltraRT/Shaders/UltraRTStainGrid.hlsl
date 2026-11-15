#ifndef ULTRART_STAIN_GRID_INCLUDED
#define ULTRART_STAIN_GRID_INCLUDED

// Shared by StainGrid.compute (build) and UltraRTStains.hlsl (lookup): blood stains in a world-space hash grid
// (docs/path-tracing.md, "Blood stains in reflections").

#define STAIN_BUCKETS 16384u   // hash buckets
#define STAIN_BUCKET 32u       // slots per bucket (more are dropped)
#define STAIN_CELL 6.0         // cell size (m): larger than a stain's box, so a stain touches at most 2 cells per axis

struct StainRecord
{
    float3 centre; float halfZ;   // world centre; half thickness of the box along the normal (m)
    float3 ax;     float uvScale; // in-plane axis (unit); atlas uv per metre
    float3 ay;     float cell;    // in-plane axis (unit); atlas cell offset (0, 0.2, ... 0.8)
    float3 az;     float flags;   // normal (unit); 1 = clipped to its surface (normal test), + wetness / 2
};

uint StainBucket(int3 c)
{
    uint h = ((uint)c.x * 73856093u) ^ ((uint)c.y * 19349663u) ^ ((uint)c.z * 83492791u);
    return h % STAIN_BUCKETS;
}

#endif
