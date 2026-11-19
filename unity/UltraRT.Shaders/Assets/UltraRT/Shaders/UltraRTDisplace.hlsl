// The game's VERTEX_DISPLACEMENT function (ULTRAKILL/Master's vertex shader), shared by Displace.compute (the traced copy
// of a displaced mesh) and TransparentOverlay.shader (the overlay must sit on the game's displaced water surface, or its
// depth test and distance match fail in patches; docs/reflections.md "Displaced water and the temporal filter").
// The caller declares the noise texture and passes the game's _Time and camera position. Formula: Displace.compute.
#ifndef ULTRART_DISPLACE_HLSL
#define ULTRART_DISPLACE_HLSL

struct DispMat
{
    float3 flow;
    float  mode, reverse, noiseScale, vcolStrength;
    float2 uvNoiseScale, uvNoiseSpeed;
    float  noiseSpeed, amplitude, localOffset, recalc, normalOffset, preventClip;
};

// One displaced point, world space. noiseTex / noiseSampler: the material's _VertexNoiseTex.
float3 DisplacePoint(DispMat m, float3 p, float3 n, float4 col, float2 uv, float3 origin, float4 gameTime, float3 cameraPos,
                     Texture2D<float4> noiseTex, SamplerState noiseSampler)
{
    bool rev = m.reverse == 1.0;
    float sgn = rev ? -1.0 : 1.0;
    float3 d;
    if (m.mode == 0.0)
    {
        float3 dir = normalize(max(m.flow, 0.0)) * (rev ? 3.0 : -1.0);
        float s = dot(p - origin, dir) * m.noiseScale * 0.1 + gameTime.x * m.noiseSpeed;
        float v = noiseTex.SampleLevel(noiseSampler, float2(s, s), 0).r;
        d = (v - 0.5) * m.amplitude * dir;
        if (m.vcolStrength == 1.0) d *= col.g;
    }
    else
    {
        float w;
        if (m.mode == 2.0)
        {
            w = (col.r * 2.0 - 1.0) * sin(sgn * m.noiseSpeed * gameTime.y) * 0.5 + 0.5;
            if (m.vcolStrength == 1.0) w *= col.g;
        }
        else if (m.mode == 1.0)
        {
            float f = frac(frac(uv.y * m.noiseScale * 0.1) * m.noiseScale + sgn * m.noiseSpeed * gameTime.y);
            w = abs(f * 2.0 - 1.0);
        }
        else if (m.mode == 3.0)
        {
            float v = noiseTex.SampleLevel(noiseSampler, uv * m.uvNoiseScale + m.uvNoiseSpeed * gameTime.y, 0).r;
            w = (v - 0.5) * m.amplitude * 0.1;
        }
        else w = 0.0;
        d = n * w * m.amplitude * 5.0;
    }
    float3 q = p + d;
    if (m.preventClip == 1.0)
    {
        float k = saturate((length(q - cameraPos) - 5.0) * -0.333333);
        q = lerp(q, p, k);
    }
    return q;
}

// The displaced point q of p and, with _RecalculateNormals, the game's recomputed normal (else n). World space.
void DisplaceVertex(DispMat m, float3 p, float3 n, float4 col, float2 uv, float3 origin, float4 gameTime, float3 cameraPos,
                    Texture2D<float4> noiseTex, SamplerState noiseSampler, out float3 q, out float3 nOut)
{
    q = DisplacePoint(m, p, n, col, uv, origin, gameTime, cameraPos, noiseTex, noiseSampler);
    nOut = n;
    if (m.recalc == 1.0)
    {
        float k = m.normalOffset;
        float3 t = normalize(float3(-0.05 * n.y / n.x, 0.05, 0.0));
        float3 b = normalize(cross(n, t));
        float3 qt = DisplacePoint(m, p + t * k, n, col, uv + t.xz * k * 0.1, origin, gameTime, cameraPos, noiseTex, noiseSampler);
        float3 qb = DisplacePoint(m, p + b * k, n, col, uv + b.xz * k * 0.1, origin, gameTime, cameraPos, noiseTex, noiseSampler);
        float3 nw = normalize(cross(normalize(qt - q), normalize(qb - q)));
        if (all(isfinite(nw))) nOut = nw; // where n.x is 0 the game's T is not finite: keep the rest normal
    }
}

#endif
