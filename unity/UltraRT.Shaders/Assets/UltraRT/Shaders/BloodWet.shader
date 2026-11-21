// Wet blood (docs/vfx.md, "Wet blood"): the wetness of fresh blood stains, per screen pixel, for PathTrace's primary
// hits. The game draws its stains from a compute buffer (BloodsplatterManager.instanceBuffer: position, parent, normal,
// clip state per stain; parentBuffer: the transform of what the stain sticks to) as instanced quads that stamp a
// distance-fog value into a screen buffer, which PostProcess/CompositeBlood then multiplies into the image. This is that
// stamp shader, ported from the game's GLCore program (ULTRAKILL/Bloodstain_ProceduralInstancing), drawing only the
// stains that are still wet and writing their wetness (from the birth time we record per stain, BloodWet.cs) instead.
// Same mesh, same per-stain rotation and atlas cell (both from the stain's slot), same box and normal tests, so the wet
// area is exactly the stain the game draws.
//   - Our own view-projection (the camera's GPU projection for a render texture) and its inverse rebuild the world
//     position from the game's depth, so nothing depends on the globals the game sets later in the frame.
//   - Blend Max: where stains overlap, the wettest wins.
Shader "Hidden/UltraRT/BloodWet"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            struct Stain { float3 pos; int parent; float3 norm; int clip; };
            struct Parent { float4 c0, c1, c2, c3; }; // a Matrix4x4, column by column
            StructuredBuffer<Stain> instanceBuffer;
            StructuredBuffer<Parent> parentBuffer;
            StructuredBuffer<float> _BloodBirth;   // per slot: the time the stain was made (s, the game's clock)

            Texture2D _SplatterAtlas;              // the game's stain shapes: 5 cells side by side, alpha = shape
            Texture2D _BloodDepth;                 // the game's depth buffer (raw)
            Texture2D _BloodNormal;                // the game's view-normal target (xyz * 0.5 + 0.5)
            SamplerState my_point_clamp_sampler;
            SamplerState my_point_repeat_sampler;
            float4x4 _BloodVP, _BloodInvVP;
            float4 _BloodParams;                   // x first slot, y capacity, z now (s), w 1 / screen width... see below
            float4 _BloodParams2;                  // x seconds a stain stays fully wet, y seconds until it is dry, z _StainWarping, w _TextureWarping
            float4 _BloodScreen;                   // xy 1 / size of the target

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uvw : TEXCOORD0;    // xy uv * warp, z warp, w atlas cell offset
                float4 centre : TEXCOORD1; // xyz stain centre (world), w clip state
                float3 ax : TEXCOORD2;     // the stain's axes (world, unit)
                float3 ay : TEXCOORD3;
                float3 az : TEXCOORD4;
                float4 nw : TEXCOORD5;     // xyz the quad's normal (world, scaled as the game's), w wetness
            };

            v2f vert(float3 vertex : POSITION, float4 normal : NORMAL, float2 uv : TEXCOORD0, uint inst : SV_InstanceID)
            {
                v2f o;
                uint cap = max((uint)_BloodParams.y, 1u);
                uint id = ((uint)_BloodParams.x + inst) % cap;
                Stain s = instanceBuffer[id];
                Parent pm = parentBuffer[s.parent];
                float angle = (float)(id % 359u);
                float cell = (float)(id % 5u);
                float ca = cos(angle), sa = sin(angle);
                float3 N = s.norm;
                float3 X, Y, Z;
                float3 t = float3(-N.y, N.x, 0.0);
                float l2 = dot(t.xy, t.xy);
                if (l2 >= 1e-7)
                {
                    t *= rsqrt(l2);
                    float3 b = cross(N, t);
                    X = (t * ca + b * sa) * 8.0;
                    Y = (b * ca - t * sa) * 8.0;
                    Z = N * 8.0;
                }
                else
                {
                    float sg = sign(N.z);
                    X = float3(ca * 8.0, sa * 8.0, 0.0);
                    Y = float3(-sa * 8.0, sg * ca * 8.0, 0.0);
                    Z = float3(0.0, 0.0, 8.0);
                }
                // rows of the parent matrix
                float4 r0 = float4(pm.c0.x, pm.c1.x, pm.c2.x, pm.c3.x);
                float4 r1 = float4(pm.c0.y, pm.c1.y, pm.c2.y, pm.c3.y);
                float4 r2 = float4(pm.c0.z, pm.c1.z, pm.c2.z, pm.c3.z);
                float4 p1 = float4(s.pos, 1.0);
                float3 mx = float3(dot(r0.xyz, X), dot(r1.xyz, X), dot(r2.xyz, X)); // world-space columns of parent * [X Y Z]
                float3 my = float3(dot(r0.xyz, Y), dot(r1.xyz, Y), dot(r2.xyz, Y));
                float3 mz = float3(dot(r0.xyz, Z), dot(r1.xyz, Z), dot(r2.xyz, Z));
                float3 c = float3(dot(r0, p1), dot(r1, p1), dot(r2, p1));
                float3 wp = mx * vertex.x + my * vertex.y + mz * vertex.z + c;
                float4 clip = mul(_BloodVP, float4(wp, 1.0));

                float age = _BloodParams.z - _BloodBirth[id];
                float wet = 1.0 - smoothstep(_BloodParams2.x, max(_BloodParams2.y, _BloodParams2.x + 0.01), age);
                // cleared stains (zero normal), dry ones and ones from the future (a checkpoint reload) draw nothing
                if (all(N == 0.0) || wet <= 0.0 || age < -0.5) clip = float4(0.0, 0.0, 0.0, 0.0);
                o.pos = clip;
                float warp = _BloodParams2.w * (max(clip.w, 0.02) - 0.5) + 0.5;
                o.uvw = float4(uv * warp, warp, cell * 0.2);
                o.centre = float4(c, (float)s.clip);
                o.ax = normalize(mx);
                o.ay = normalize(my);
                o.az = normalize(mz);
                o.nw = float4(mx * normal.x + my * normal.y + mz * normal.z, wet);
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                float2 uv = i.uvw.xy / i.uvw.z;
                float a = _SplatterAtlas.SampleLevel(my_point_repeat_sampler, uv * float2(0.2, 1.0) + float2(i.uvw.w, 0.0), 0).a;
                if (a != 1.0) discard;
                int2 px = (int2)(i.pos.xy * _BloodScreen.zw);
                float z = _BloodDepth.Load(int3(px, 0)).r;
                float2 ndc = float2(i.pos.x * _BloodScreen.x * 2.0 - 1.0, 1.0 - i.pos.y * _BloodScreen.y * 2.0);
                float4 wh = mul(_BloodInvVP, float4(ndc, z, 1.0));
                float3 d = wh.xyz / wh.w - i.centre.xyz;
                float3 local = float3(dot(i.ax, d), dot(i.ay, d), dot(i.az, d));
                float halfZ = (_BloodParams2.z + 1.0) * 0.26 * ((1.0 - i.centre.w) * 5.0 + 1.0);
                if (abs(local.x) > 2.0 || abs(local.y) > 2.0 || abs(local.z) > halfZ) discard;
                if (i.centre.w > 0.5)
                {
                    float3 n = normalize(_BloodNormal.Load(int3(px, 0)).xyz * 2.0 - 1.0);
                    if (dot(i.nw.xyz, n) < 0.5) discard;
                }
                return i.nw.w;
            }
            ENDHLSL
        }
    }
}
