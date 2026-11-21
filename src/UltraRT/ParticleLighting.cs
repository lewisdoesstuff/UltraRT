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
        public bool ptDn = true;         // also in pt-dn
        public bool additiveSmoke = true; // relight additive smoke / dust / mist too (not fire, flashes, glows)
        public float strength = 1f;      // relight / pt-dn: 0 = the game's look, 1 = fully ray-traced lighting
        // Additive smoke / gas / mist (ULTRAKILL/Master "Smoke", in-scattering). The game lights these with its flat ambient
        // only (no vertex lights), so the art at lighting 1 is far brighter than anything the game draws: P-2's vent gas,
        // invisible in the game (black ambient), became a glowing column next to a flickering red streetlight (intensity
        // up to 13). The multiplier is m s / (1 + m s / max) of the brightest lighting channel m (hue kept: a red light
        // keeps the gas red instead of pushing it through the tone curve to fiery orange). 0 = the raw lighting.
        public float additiveScale = 0.5f, additiveMax = 0.6f;
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
        // Soft particles (docs/vfx.md): the swapped effect materials fade out where they meet the opaque scene.
        public bool soft = true;         // -ultrart-particles-soft on|off (config [ScreenEffects] SoftParticles)
        public int softKinds = 7;         // which glow / smoke materials are swapped for the fade: 1 explosion spheres, 2 fire sprites, 4 multiply smoke
        public float softDistance = 0.6f; // m in front of the surface over which a particle fades in (-ultrart-particles-soft-distance)
        // Room-filling particle systems (3-2's HeatWaves: one 'Smoke' emitter over the whole eye cave) are atmosphere, not
        // smoke: the volumetric pass lights the air, and relit as additive smoke their dozens of overlapping sprites became an
        // orange veil the game never shows (black ambient). Emitters whose shape spans this many metres keep the game's
        // shader (a private copy of the original material); 0 = relight them like any smoke (docs/particles.md, "Atmosphere").
        public float atmosphereSize = 30f; // -ultrart-particles-atmosphere M
        /// <summary>Wave 3: use native D3D12 cs_6_5 inline RayQuery bridge instead of ParticleLight.raytrace DispatchRays
        /// (eliminates ~0.82 ms RT-CPU in 8-x). Bench pl.rayQuery=0.</summary>
        public bool rayQuery = true;

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
                    case "-ultrart-particles-additive": additiveSmoke = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-particles-grid":
                    {
                        var p = (Next() ?? "").Split(',');
                        if (p.Length == 3 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) && int.TryParse(p[2], out var z))
                        { gridX = x; gridY = y; gridZ = z; }
                        break;
                    }
                    case "-ultrart-particles-far": far = F(far); break;
                    case "-ultrart-particles-additive-max": additiveMax = F(additiveMax); break;
                    case "-ultrart-particles-additive-scale": additiveScale = F(additiveScale); break;
                    case "-ultrart-particles-alpha": alpha = F(alpha); break;
                    case "-ultrart-particles-noindirect": indirect = false; break;
                    case "-ultrart-particles-soft": soft = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-particles-soft-distance": softDistance = F(softDistance); break;
                    case "-ultrart-particles-atmosphere": atmosphereSize = F(atmosphereSize); break;
                }
            }
        }

        public override string ToString() => FormattableString.Invariant(
            $"{(enabled ? "on" : "off")}, strength {strength} (pt-dn {(ptDn ? "on" : "off")}), additive smoke {(additiveSmoke ? "on" : "off")} (x{additiveScale}, max {additiveMax}), grid {gridX}x{gridY}x{gridZ} {near}-{far} m, {candidates} candidates, alpha {alpha}, indirect {(indirect ? "on" : "off")}, atmosphere from {atmosphereSize} m");
    }

    /// <summary>What <see cref="ParticleLighting.Execute"/> needs from the plugin each frame.</summary>
    internal struct ParticleLightFrame
    {
        public Camera camera;
        public RayTracingAccelerationStructure rtas;
        public uint frameIndex;
        public float indirectScale;   // 1 (was the hybrid mode's GI strength)
        public Color ambient;         // flat ambient in game units
        public bool lightsChanged;
        public bool linear;           // relight's linear transport
        // Native D3D12 RayQuery bridge (Wave 3)
        public RayQueryBridge rayQuery;
        public ComputeBuffer lights, lightMasks;
        public int lightCount;
        public LightSampling lightSampling;
        public EmissiveLights emissive;
        public RadianceCache cache;
        public Texture skyTex;
        public Vector4 skyColor, skyTint, skyParams, skyLight;
        public Vector4 relight; // x falloff, y exposure, z linearK, w preLinearLights
        public float lightRadius, fixtureShadows;
        public ComputeBuffer portalQuads, portalTravels;
        public bool portalImagesOn;
        public System.Collections.Generic.List<Vector3> cacheViews;
    }

    /// <summary>
    /// The particle lighting volume (docs/particles.md): a camera-aligned froxel grid of the ray-traced lighting at points in
    /// the air, traced each frame (ParticleLight.raytrace), accumulated over time (ParticleLight.compute) and sampled per
    /// vertex by LitParticle.shader. GPU timer slot "particles".
    /// </summary>
    internal sealed class ParticleLighting : IDisposable
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

        /// <summary>The camera went through a portal: last frame's view of this frame's world (docs/portals.md, "Temporal").</summary>
        public void RemapHistory(Matrix4x4 nowToOld, Matrix4x4 oldToNow)
        {
            _prevVP = _prevVP * nowToOld;
            _prevCam = oldToNow.MultiplyPoint3x4(_prevCam);
            _prevFwd = oldToNow.MultiplyVector(_prevFwd);
        }

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

            bool useRq = s.rayQuery && f.rayQuery != null && f.rayQuery.Ready && f.rayQuery.HasTlas;
            if (useRq)
            {
                var ls = f.lightSampling;
                var em = f.emissive;
                var rc = f.cache;
                var rqFrame = new RayQueryBridge.PlRqFrame
                {
                    lightsBuf = f.lights != null ? f.lights.GetNativeBufferPtr() : IntPtr.Zero,
                    lightMasksBuf = f.lightMasks != null ? f.lightMasks.GetNativeBufferPtr() : IntPtr.Zero,
                    lightAliasBuf = ls != null ? ls.AliasBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    fillLightsBuf = ls != null ? ls.FillBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    emTrisBuf = em != null ? em.TrisBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    emOwnersBuf = em != null ? em.OwnersBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    emAliasBuf = em != null ? em.AliasBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    emActiveBuf = em != null ? em.ActiveBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    cacheKeysBuf = rc != null ? rc.KeysBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    cacheResolvedBuf = rc != null ? rc.ResolvedBuffer.GetNativeBufferPtr() : IntPtr.Zero,
                    skyTex = f.skyTex != null ? f.skyTex.GetNativeTexturePtr() : IntPtr.Zero,
                    portalQuadsBuf = f.portalQuads != null ? f.portalQuads.GetNativeBufferPtr() : IntPtr.Zero,
                    portalTravelsBuf = f.portalTravels != null ? f.portalTravels.GetNativeBufferPtr() : IntPtr.Zero,
                    outTex = _raw.GetNativeTexturePtr(),
                    gridX = gx,
                    gridY = gy,
                    gridZ = gz,
                    invViewProj = invVP,
                    camPos = new Vector4(pos.x, pos.y, pos.z, 0f),
                    grid = new Vector4(gx, gy, gz, 0f),
                    depth = depth,
                    camFwd = new Vector4(fwd.x, fwd.y, fwd.z, f.lightRadius),
                    plParams = new Vector4(Mathf.Clamp(s.candidates, 1, 32), f.indirectScale, s.maxSample, s.indirect ? 1f : 0f),
                    ambient = new Vector4(f.ambient.r, f.ambient.g, f.ambient.b, f.fixtureShadows),
                    relight = f.relight,
                    skyLight = f.skyLight,
                    skyColor = f.skyColor,
                    skyTint = f.skyTint,
                    skyParams = f.skyParams,
                    emissiveParams = em != null ? em.BoundParams : Vector4.zero,
                    cacheGrid = rc != null ? rc.GridParams : Vector4.zero,
                    cacheQuery = rc != null ? rc.QueryParams : Vector4.zero,
                    int0 = new RayQueryBridge.UInt4(f.frameIndex, (uint)Mathf.Max(0, f.lightCount), (uint)(ls != null ? ls.RisLightCount : 0), (uint)(ls != null ? ls.FillLightCount : 0)),
                    int1 = new RayQueryBridge.UInt4(IsoLayerMask, (uint)(em != null ? em.BoundCount : 0), (uint)(em != null ? em.BoundTriCount : 0), (uint)(rc != null ? rc.ActiveModeValue : 0)),
                    int2 = new RayQueryBridge.UInt4((uint)(rc != null ? rc.Capacity : 0), (uint)(f.cacheViews != null ? Mathf.Min(f.cacheViews.Count, 16) : 0), f.portalImagesOn ? 1u : 0u, 0u),
                };
                if (f.cacheViews != null)
                {
                    int ncv = Mathf.Min(f.cacheViews.Count, 16);
                    for (int k = 0; k < ncv; k++)
                    {
                        var v = f.cacheViews[k];
                        rqFrame.SetCacheView(k, new Vector4(v.x, v.y, v.z, 0f));
                    }
                }
                f.rayQuery.DispatchParticleLight(cmd, ref rqFrame);
            }
            else
            {
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
            }

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
