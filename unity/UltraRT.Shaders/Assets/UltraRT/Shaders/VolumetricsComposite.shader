// Volumetric lighting composite (docs/volumetrics.md). Drawn into the main camera's target right after the RT composite
// (before the game's transparents), fullscreen, reading a copy of the target:
//   1. the game's own distance fog on ray-traced pixels (the RT image replaces the game's fogged opaque shading, so the
//      replace modes lost it): exactly the game's formula, lerp(colour, fog colour, fog amount) in display space, only
//      where the temporal upsampler says the pixel is ours (_UltraRTUpsampled.a > 0). The game already fogged its sky.
//   2. the in-scattered light: low-res history upsampled with depth weights, exposed like the lit RT image, added in
//      linear light: out = (in^2.2 + exposure * S)^(1/2.2), with a soft shoulder instead of a hard clip.
Shader "UltraRT/VolumetricsComposite"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture2D _VolSceneColor;          // copy of the target (screen resolution)
            Texture2D _UltraRTGameDepth;       // the game's depth (raw), set by the plugin for the RT composite
            Texture2D _UltraRTUpsampled;       // temporal upsampler output: a > 0 where the RT image replaced the game's pixel
            Texture2D<float4> _VolHist;        // low-res accumulated in-scattering (linear, unexposed)
            Texture2D<float>  _VolDepthTex;    // low-res ray lengths
            StructuredBuffer<float4> _VolExposure; // [0].x auto exposure (AutoExposure's buffer)
            SamplerState my_point_clamp_sampler;
            float4 _VolScreen;                 // screen width, height, block size, unused
            float4 _VolLowDim;                 // low-res width, height, 1 / width, 1 / height
            float4 _VolViewRay;                // x 1 / P00, y 1 / P11, z ray length used for the sky, w eye depth from which a pixel is sky
            float4 _VolFog;                    // x mode (0 off, 1 linear, 2 exp, 3 exp2), y start, z end, w density
            float4 _VolFogColor;               // rgb the game's fog colour, w 1 = apply fog to RT pixels
            float4 _VolComposite;              // x in-scatter strength, y 1 = auto exposure, z fixed exposure, w debug (1 = S only)
            float  _UltraRTFlipY;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 p = float2((id << 1) & 2, id & 2);
                o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
                o.uv = p;
                if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
                if (_UltraRTFlipY > 0.5) o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float FogVisibility(float z)
            {
                float m = _VolFog.x;
                if (m < 0.5) return 1.0;
                if (m < 1.5) return saturate((_VolFog.z - z) / max(_VolFog.z - _VolFog.y, 1e-3));
                float x = _VolFog.w * z;
                return m < 2.5 ? saturate(exp2(-x * 1.442695)) : saturate(exp2(-x * x * 1.442695));
            }

            // Upsample the low-res in-scattering at a screen pixel of radial distance d. Low-res pixel i was traced through
            // screen pixel i * block + block / 2. Each of the 4 nearest low-res values is scaled to this pixel's ray length
            // (in-scattering grows about linearly with distance) and weighted by bilinear x depth similarity.
            float3 Upsample(float2 screenPx, float d)
            {
                float blk = _VolScreen.z;
                float2 pos = (screenPx - 0.5 - floor(blk * 0.5)) / blk;
                int2 b = (int2)floor(pos);
                float2 f = pos - b;
                int2 dim = (int2)_VolLowDim.xy;
                float3 sum = 0;
                float wSum = 0;
                float3 best = 0;
                float bestErr = 1e30;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 q = clamp(b + o, 0, dim - 1);
                    float dq = _VolDepthTex.Load(int3(q, 0));
                    float3 v = _VolHist.Load(int3(q, 0)).rgb * saturate(d / max(dq, 1e-3));
                    float e = (dq - d) / (0.05 * d + 0.1);
                    float bw = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
                    float w = bw * exp(-e * e);
                    sum += v * w;
                    wSum += w;
                    float err = abs(e);
                    if (err < bestErr) { bestErr = err; best = v; }
                }
                return wSum > 1e-3 ? sum / wSum : best;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 c = _VolSceneColor.Sample(my_point_clamp_sampler, i.uv).rgb;
                float raw = _UltraRTGameDepth.Sample(my_point_clamp_sampler, i.uv).r;
                float z = LinearEyeDepth(raw);
                float2 ndc = i.uv * 2.0 - 1.0;
                float d = z * length(float3(ndc.x * _VolViewRay.x, ndc.y * _VolViewRay.y, 1.0));
                float2 screenPx = i.uv * _VolScreen.xy;

                // 1. the game's fog on our pixels
                if (_VolFogColor.w > 0.5 && _UltraRTUpsampled.Sample(my_point_clamp_sampler, i.uv).a > 0.0)
                    c = lerp(_VolFogColor.rgb, c, FogVisibility(z));

                // 2. in-scattering (at the far plane: the sky ray length the low-res pass used)
                float3 s = Upsample(screenPx, z >= _VolViewRay.w ? _VolViewRay.z : d);
                float e = _VolComposite.z;
                if (_VolComposite.y > 0.5)
                {
                    float ae = _VolExposure[0].x;
                    if (ae > 0.0 && !isinf(ae)) e *= ae;
                }
                s *= _VolComposite.x * e;
                if (_VolComposite.w > 0.5) return float4(pow(saturate(s), 1.0 / 2.2), 1.0);
                float3 lin = pow(saturate(c), 2.2) + s;
                float3 o = pow(lin, 1.0 / 2.2);
                // Soft shoulder on what was added: c + h (1 - exp(-delta / h)), h = room left above c.
                float3 delta = max(o - c, 0.0);
                float3 h = max(1.0 - c, 1e-3);
                o = c + h * (1.0 - exp(-delta / h));
                return float4(o, 1.0);
            }
            ENDHLSL
        }
    }
}
