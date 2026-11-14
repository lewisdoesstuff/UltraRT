using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>Settings of the lit particles (docs/particles.md). Flags in <see cref="ParseArgs"/>.</summary>
    public sealed class ParticleLightSettings
    {
        public bool enabled = true;      // -ultrart-particles on|off (config [Particles] Lit); Ctrl+F7 in game
        public bool hybrid = true;       // also in hybrid (the game's light model, RT shadows and bounce)
        public bool ptDn = true;         // also in pt-dn
        public bool additiveSmoke = true; // relight additive smoke / dust / mist too (not fire, flashes, glows)
        public float strength = 1f;      // relight / pt-dn: 0 = the game's look, 1 = fully ray-traced lighting
        public float hybridStrength = 1f;
        public int gridX = 64, gridY = 36, gridZ = 32;
        public float near = 0.5f, far = 96f;
        public int candidates = 8;       // RIS candidates per froxel
        public float alpha = 0.12f;      // temporal: weight of the new frame
        public float fastAlpha = 0.5f;   // after a light-set change, for fastFrames frames
        public int fastFrames = 3;
        public float spatial = 0.5f;     // weight of the 6 face neighbours in the 3x3x3 filter (edges ^2, corners ^3)
        public float maxSample = 24f;    // firefly clamp (luminance, lighting units)
        public bool indirect = true;     // bounce / sky ray per froxel
        public int debugView;            // 1: draw lit particles with their texture as white (lighting only)

        public void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float def) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-particles": enabled = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-particles-strength": strength = F(strength); break;
                    case "-ultrart-particles-hybrid": hybridStrength = F(hybridStrength); hybrid = hybridStrength > 0f; break;
                    case "-ultrart-particles-additive": additiveSmoke = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-particles-grid":
                    {
                        var p = (Next() ?? "").Split(',');
                        if (p.Length == 3 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) && int.TryParse(p[2], out var z))
                        { gridX = x; gridY = y; gridZ = z; }
                        break;
                    }
                    case "-ultrart-particles-far": far = F(far); break;
                    case "-ultrart-particles-alpha": alpha = F(alpha); break;
                    case "-ultrart-particles-noindirect": indirect = false; break;
                }
            }
        }

        public override string ToString() => FormattableString.Invariant(
            $"{(enabled ? "on" : "off")}, strength {strength} (hybrid {(hybrid ? hybridStrength : 0f)}, pt-dn {(ptDn ? "on" : "off")}), additive smoke {(additiveSmoke ? "on" : "off")}, grid {gridX}x{gridY}x{gridZ} {near}-{far} m, {candidates} candidates, alpha {alpha}, indirect {(indirect ? "on" : "off")}");
    }

    /// <summary>What <see cref="ParticleLighting.Execute"/> needs from the plugin each frame.</summary>
    public struct ParticleLightFrame
    {
        public Camera camera;
        public RayTracingAccelerationStructure rtas;
        public uint frameIndex;
        public float indirectScale;   // hybrid: the GI strength; otherwise 1
        public Color ambient;         // flat ambient in game units (hybrid: the game's 2 x ambient)
        public bool lightsChanged;
        public bool linear;           // relight's linear transport
    }

    /// <summary>
    /// The particle lighting volume (docs/particles.md): a camera-aligned froxel grid of the ray-traced lighting at points in
    /// the air, traced each frame (ParticleLight.raytrace), accumulated over time (ParticleLight.compute) and sampled per
    /// vertex by LitParticle.shader. GPU timer slot "particles".
    /// </summary>
    public sealed class ParticleLighting : IDisposable
    {
        public readonly ParticleLightSettings Settings;
        public RayTracingShader RtShader => _rt;
        public RenderTexture Output => _out;
        public Matrix4x4 ViewProj { get; private set; }
        public Vector4 DepthParams { get; private set; }
        public bool Valid { get; private set; }

        private readonly RayTracingShader _rt;
        private readonly ComputeShader _cs;
        private readonly int _kTemporal;
        private RenderTexture _raw, _out, _history;
        private Matrix4x4 _prevVP;
        private Vector3 _prevCam, _prevFwd;
        private bool _historyValid;
        private int _fast;

        private static readonly int IdScene = Shader.PropertyToID("_Scene"), IdRaw = Shader.PropertyToID("_PLRaw"),
            IdHistory = Shader.PropertyToID("_PLHistory"), IdOut = Shader.PropertyToID("_PLOut"),
            IdInvVP = Shader.PropertyToID("_PLInvViewProj"), IdGrid = Shader.PropertyToID("_PLGrid"),
            IdDepth = Shader.PropertyToID("_PLDepth"), IdCamFwd = Shader.PropertyToID("_PLCamFwd"),
            IdParams = Shader.PropertyToID("_PLParams"), IdAmbient = Shader.PropertyToID("_PLAmbient"),
            IdCamPos = Shader.PropertyToID("_CamPos"), IdFrame = Shader.PropertyToID("_FrameIndex"),
            IdLightScale = Shader.PropertyToID("_LightScale"), IdEmissionScale = Shader.PropertyToID("_EmissionScale"),
            IdIsoMask = Shader.PropertyToID("_LightIsoMask"),
            IdCsCamPos = Shader.PropertyToID("_PLCamPos"), IdPrevVP = Shader.PropertyToID("_PLPrevViewProj"),
            IdPrevCam = Shader.PropertyToID("_PLPrevCamPos"), IdPrevFwd = Shader.PropertyToID("_PLPrevCamFwd"),
            IdTemporal = Shader.PropertyToID("_PLTemporal");

        // Layers effects live on (a light lights particles if its culling mask has any): Default, TransparentFX,
        // Environment, Gib, Limb, Projectile, Explosion, Outdoors, Outdoors Non-solid, GibLit.
        private const uint IsoLayerMask = (1u << 0) | (1u << 1) | (1u << 8) | (1u << 9) | (1u << 10) | (1u << 14) | (1u << 23) | (1u << 24) | (1u << 25) | (1u << 27);

        public ParticleLighting(RayTracingShader rt, ComputeShader cs, ParticleLightSettings settings)
        {
            _rt = rt;
            _cs = cs;
            Settings = settings;
            _kTemporal = cs.FindKernel("Temporal");
            rt.SetShaderPass("UltraRTHit");
        }

        public void Reset() { _historyValid = false; Valid = false; }

        private static RenderTexture Make3D(int x, int y, int z, string name)
        {
            var desc = new RenderTextureDescriptor(x, y, RenderTextureFormat.ARGBHalf, 0)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = z, enableRandomWrite = true, msaaSamples = 1,
            };
            var rt = new RenderTexture(desc) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            return rt;
        }

        private void Ensure(int x, int y, int z)
        {
            if (_raw != null && _raw.width == x && _raw.height == y && _raw.volumeDepth == z) return;
            ReleaseTextures();
            _raw = Make3D(x, y, z, "UltraRT ParticleLight Raw");
            _out = Make3D(x, y, z, "UltraRT ParticleLight A");
            _history = Make3D(x, y, z, "UltraRT ParticleLight B");
            _historyValid = false;
        }

        /// <summary>Trace and filter this frame's volume. Call after PathTrace's DispatchRays (the cache is resolved and the
        /// light tables are bound), with the light list, light tables, emitters and sky already bound on <see cref="RtShader"/>.</summary>
        public void Execute(CommandBuffer cmd, ParticleLightFrame f)
        {
            var s = Settings;
            int gx = Mathf.Clamp(s.gridX, 4, 256), gy = Mathf.Clamp(s.gridY, 4, 256), gz = Mathf.Clamp(s.gridZ, 4, 128);
            Ensure(gx, gy, gz);
            var cam = f.camera;
            var vp = cam.projectionMatrix * cam.worldToCameraMatrix; // GL convention, unjittered, no guard band
            var invVP = vp.inverse;
            Vector3 pos = cam.transform.position, fwd = cam.transform.forward;
            float near = Mathf.Max(0.05f, s.near), far = Mathf.Max(near * 2f, s.far);
            float logRange = Mathf.Log(far / near, 2f);
            var depth = new Vector4(near, logRange, 1f / logRange, f.linear ? 1f : 0f);
            if (f.lightsChanged) _fast = Mathf.Max(_fast, s.fastFrames);

            cmd.SetRayTracingAccelerationStructure(_rt, IdScene, f.rtas);
            cmd.SetRayTracingTextureParam(_rt, IdRaw, _raw);
            cmd.SetRayTracingMatrixParam(_rt, IdInvVP, invVP);
            cmd.SetRayTracingVectorParam(_rt, IdGrid, new Vector4(gx, gy, gz, 0));
            cmd.SetRayTracingVectorParam(_rt, IdDepth, depth);
            cmd.SetRayTracingVectorParam(_rt, IdCamFwd, fwd);
            cmd.SetRayTracingVectorParam(_rt, IdCamPos, pos);
            cmd.SetRayTracingIntParam(_rt, IdFrame, unchecked((int)f.frameIndex));
            cmd.SetRayTracingFloatParam(_rt, IdLightScale, 1f);
            cmd.SetRayTracingFloatParam(_rt, IdEmissionScale, 1f);
            cmd.SetRayTracingIntParam(_rt, IdIsoMask, unchecked((int)IsoLayerMask));
            cmd.SetRayTracingVectorParam(_rt, IdParams, new Vector4(Mathf.Clamp(s.candidates, 1, 32), f.indirectScale, s.maxSample, s.indirect ? 1f : 0f));
            cmd.SetRayTracingVectorParam(_rt, IdAmbient, new Vector4(f.ambient.r, f.ambient.g, f.ambient.b, 0));
            cmd.DispatchRays(_rt, "ParticleLightRayGen", (uint)gx, (uint)gy, (uint)gz);

            // Temporal: _out <- filter(raw) blended with _history (last frame's _out); then swap.
            (_out, _history) = (_history, _out);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdRaw, _raw);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdHistory, _history);
            cmd.SetComputeTextureParam(_cs, _kTemporal, IdOut, _out);
            cmd.SetComputeMatrixParam(_cs, IdInvVP, invVP);
            cmd.SetComputeVectorParam(_cs, IdCsCamPos, pos);
            cmd.SetComputeVectorParam(_cs, IdCamFwd, fwd);
            cmd.SetComputeVectorParam(_cs, IdGrid, new Vector4(gx, gy, gz, 0));
            cmd.SetComputeVectorParam(_cs, IdDepth, depth);
            cmd.SetComputeMatrixParam(_cs, IdPrevVP, _prevVP);
            cmd.SetComputeVectorParam(_cs, IdPrevCam, _prevCam);
            cmd.SetComputeVectorParam(_cs, IdPrevFwd, _prevFwd);
            float a = _fast > 0 ? Mathf.Max(s.alpha, s.fastAlpha) : s.alpha;
            if (_fast > 0) _fast--;
            cmd.SetComputeVectorParam(_cs, IdTemporal, new Vector4(Mathf.Clamp(a, 0.01f, 1f), _historyValid ? 1f : 0f, Mathf.Clamp01(s.spatial), 0));
            cmd.DispatchCompute(_cs, _kTemporal, (gx + 3) / 4, (gy + 3) / 4, (gz + 3) / 4);

            _prevVP = vp;
            _prevCam = pos;
            _prevFwd = fwd;
            _historyValid = true;
            ViewProj = vp;
            DepthParams = depth;
            Valid = true;
        }

        private void ReleaseTextures()
        {
            if (_raw != null) _raw.Release();
            if (_out != null) _out.Release();
            if (_history != null) _history.Release();
            _raw = _out = _history = null;
        }

        public void Dispose()
        {
            ReleaseTextures();
            Valid = false;
        }
    }
}
