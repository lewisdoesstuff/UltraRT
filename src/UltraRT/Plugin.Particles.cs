using System;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Lit particles wiring (docs/particles.md). Plugin.cs only calls <see cref="UpdateParticles"/> from OnUpdate and
    /// <see cref="TraceParticleLight"/> in Trace, after PathTrace's DispatchRays.
    ///   -ultrart-particles on|off (config [Particles] Lit), -ultrart-particles-strength F, -ultrart-particles-hybrid F,
    ///   -ultrart-particles-additive on|off, -ultrart-particles-grid X,Y,Z, -ultrart-particles-far M,
    ///   -ultrart-particles-alpha F, -ultrart-particles-noindirect, -ultrart-particlesurvey (diagnostic; costs ms)
    /// Shift+F7 toggles in game.
    /// </summary>
    public partial class Plugin
    {
        private readonly ParticleLightSettings _particleSettings = new ParticleLightSettings();
        private ParticleLighting _particleLight;
        private LitParticles _litParticles;
        private ParticleSurvey _particleSurvey;
        private AssetBundle _particleBundle;
        private bool _particleArgsParsed, _particleSurveyEnabled;
        private float _particleNextScan, _particleNextLog;
        private int _particleTracedFrame = -10;
        private double _particleScanMs;
        private const int SlotParticles = 16;
        private bool _particlesOff; // bench A/B: relight+plugin._particlesOff=1
        private int _particleSoftKinds = 7;
        private bool _particleSoftScanned = true; // the soft setting the materials were last classified with
        private float _particleAtmosScanned = -1f; // the atmosphere size the emitters were last decided with

        private static readonly int PLTexId = Shader.PropertyToID("_UltraRTPL"), PLViewProjId = Shader.PropertyToID("_UltraRTPLViewProj"),
            PLCamId = Shader.PropertyToID("_UltraRTPLCam"), PLFwdId = Shader.PropertyToID("_UltraRTPLFwd"),
            PLDepthId = Shader.PropertyToID("_UltraRTPLDepth"), PLFogColorId = Shader.PropertyToID("_UltraRTPLFogColor"),
            PLFogParamsId = Shader.PropertyToID("_UltraRTPLFogParams"), PLAdditiveMaxId = Shader.PropertyToID("_UltraRTPLAdditiveMax");

        private void ParseParticleArgs()
        {
            _particleArgsParsed = true;
            _particleSettings.enabled = Config.Bind("Particles", "Lit", true,
                "Lit particles: smoke, dust, blood spray, debris and splashes take their lighting from the ray-traced solution in " +
                "relight, pt-dn and hybrid (docs/particles.md). Overridden by -ultrart-particles on|off; Shift+F7 toggles in game.").Value;
            _particleSettings.ParseArgs();
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-ultrart-particlesurvey", StringComparison.OrdinalIgnoreCase)) _particleSurveyEnabled = true;
            Logger.LogInfo($"Lit particles: {_particleSettings}");
        }

        /// <summary>Loads (and reloads, after F9) the shaders from the plugin's bundle.</summary>
        private void LoadParticleShaders()
        {
            _particleBundle = _bundle;
            _litParticles?.Deactivate();
            _particleLight?.Dispose();
            _particleLight = null;
            if (_bundle == null) return;
            var rt = _bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "ParticleLight");
            var cs = _bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "ParticleLight");
            var sh = _bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/Particles/Lit");
            if (rt == null || cs == null || sh == null || !sh.isSupported)
            {
                Logger.LogWarning($"Lit particle shaders missing (raytrace={(rt ? "ok" : "MISSING")} compute={(cs ? "ok" : "MISSING")} shader={(sh ? (sh.isSupported ? "ok" : "unsupported") : "MISSING")}): lit particles off");
                return;
            }
            _particleLight = new ParticleLighting(rt, cs, _particleSettings);
            if (_litParticles == null) _litParticles = new LitParticles(Logger) { Settings = _particleSettings };
            _litParticles.SetShader(sh);
            _particleNextScan = 0f;
        }

        /// <summary>Whether the current mode lights particles from the volume.</summary>
        private bool ParticlesFor(string name, Kind kind)
        {
            var s = _particleSettings;
            if (!s.enabled || _particlesOff || _particleLight == null) return false;
            if (RelightLook(name)) return true;
            if (name == "pt-dn") return s.ptDn;
            return kind == Kind.Hybrid && s.hybrid;
        }

        /// <summary>Every frame, from OnUpdate: swaps the effect materials in and out as the mode changes.</summary>
        private void UpdateParticles()
        {
            if (!_particleArgsParsed) ParseParticleArgs();
            if (_particleBundle != _bundle) LoadParticleShaders();
            if (_particleSurveyEnabled)
            {
                if (_particleSurvey == null) _particleSurvey = new ParticleSurvey(Logger);
                try { _particleSurvey.Update(_camera != null ? _camera : Camera.main); }
                catch (Exception e) { Logger.LogWarning($"particle survey: {e.Message}"); }
            }
            if (Input.GetKeyDown(KeyCode.F7) && ShiftHeld() && !CtrlHeld()) // Shift+F7 (Ctrl+F7 is the viewmodel pass)
            {
                _particleSettings.enabled = !_particleSettings.enabled;
                _particleLight?.Reset();
                Logger.LogInfo($"Lit particles: {(_particleSettings.enabled ? "on" : "off")}");
                Toast($"Lit particles {(_particleSettings.enabled ? "on" : "off")} (Ctrl+F7)");
            }
            // White statue orbs: a glow shell while frames are traced, the game's material otherwise (SceneProxies.OrbShells.cs).
            SceneProxies.OrbShellGain = _orbShellGain;
            bool rtTraced = _viewEnabled && _proxies?.Rtas != null && Time.frameCount - _rtTracedFrame < 30;
            _proxies?.SetOrbShells(rtTraced);
            _proxies?.SetFlatWaterFade(rtTraced && _flatWaterFade); // 1-1's fountain checkerboard (TransparentSurfaces.cs)
            UpdateGlowSprites(rtTraced); // 1-4's lamp glow sprites, bilinear (Plugin.GlowSprites.cs)
            UpdateGameDither(rtTraced); // the game's screen dither off while traced (Plugin.GameDither.cs)
            if (_litParticles == null) return;

            var (name, kind, _) = Modes[_mode];
            // On while ray tracing in a lit mode (the trace ran within the last few frames: not F7, not a failed trace).
            bool want = ParticlesFor(name, kind) && _viewEnabled && _proxies?.Rtas != null && Time.frameCount - _particleTracedFrame < 30;
            if (want && !_litParticles.Active) Shader.SetGlobalVector(PLCamId, Vector4.zero); // until the first volume
            try
            {
                // Soft particles also decide which glow materials are swapped (fire, explosions, their smoke).
                if (_particleSoftScanned != _particleSettings.soft || _particleSoftKinds != _particleSettings.softKinds)
                {
                    _particleSoftScanned = _particleSettings.soft;
                    _particleSoftKinds = _particleSettings.softKinds;
                    _litParticles.Reclassify();
                }
                // Room-filling emitters keep the game's shader; a changed size (bench variant pl.atmosphereSize) re-decides them.
                if (_particleAtmosScanned != _particleSettings.atmosphereSize)
                {
                    _particleAtmosScanned = _particleSettings.atmosphereSize;
                    _litParticles.RestoreAtmosphere();
                    _particleNextScan = 0f;
                }
                if (want && Time.realtimeSinceStartup >= _particleNextScan)
                {
                    _particleNextScan = Time.realtimeSinceStartup + 5f;
                    var sw = Stopwatch.StartNew();
                    _litParticles.Prune();
                    _litParticles.Scan();
                    if (_proxies != null) _litParticles.KeepOrbGlows(_proxies.OrbFixtures, _proxies.OrbVersion);
                    _particleScanMs = sw.Elapsed.TotalMilliseconds;
                }
                if (want) _litParticles.Activate();
                else if (_litParticles.Active)
                {
                    _litParticles.Deactivate();
                    if (!_cinePairedLive) _particleLight?.Reset();
                    Shader.SetGlobalVector(PLCamId, Vector4.zero);
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Lit particles: {e}");
                _particleSettings.enabled = false;
            }
        }

        /// <summary>
        /// In Trace, right after PathTrace's DispatchRays (the radiance cache is resolved, the light list and tables are
        /// built): trace and filter this frame's particle lighting volume and publish it to the lit particle shader.
        /// </summary>
        private void TraceParticleLight(Kind kind)
        {
            string name = Modes[_mode].name;
            // Before the first activation the materials are not swapped yet; run anyway so the volume is warm.
            if (!ParticlesFor(name, kind) || !(kind == Kind.Denoised || kind == Kind.Hybrid) || _proxies?.Rtas == null || _camera == null) return;
            // Not on the pass schedule (docs/performance.md, "Pass schedule"): with the volume a frame old, smoke read
            // 2-4 times noisier between replays (P-2 vent smoke, 2026-11-17).
            if (Time.frameCount - _particleTracedFrame > 2) _particleLight.Reset(); // stale after other modes / view off
            _particleTracedFrame = Time.frameCount;

            var u = _particleLight.RtShader;
            var ambient = RenderSettings.ambientLight;
            _cmd.SetRayTracingBufferParam(u, "_Lights", _lightBuffer);
            _cmd.SetRayTracingBufferParam(u, "_LightMasks", _lightMaskBuffer);
            _cmd.SetRayTracingIntParam(u, "_LightCount", _surfLightCount);
            _cmd.SetRayTracingFloatParam(u, "_LightRadius", _lightRadius);
            _cmd.SetRayTracingVectorParam(u, "_Relight", RelightParams);
            _cmd.SetRayTracingFloatParam(u, "_FixtureShadows", _fixtureShadows ? 1f : 0f);
            _lightSampling.BindTables(_cmd, u);
            _emissive.Bind(_cmd, u, _lightSamplingSettings.mode);
            _cmd.SetRayTracingVectorParam(u, "_SkyColor", new Vector4(ambient.r, ambient.g, ambient.b, 1));
            BindSky(u);
            _radianceCache?.BindRayTracingLookup(_cmd, u);

            bool hybrid = kind == Kind.Hybrid;
            float ambientScale = 2f * (_relightActive ? 1f - Mathf.Clamp01(RelightParams.w) : 1f); // the game's lit term is 2 x ambient + lights
            var frame = new ParticleLightFrame
            {
                camera = _camera,
                rtas = _proxies.Rtas,
                frameIndex = _frameIndex,
                indirectScale = hybrid ? _giStrength : 1f,
                ambient = ambient * ambientScale,
                lightsChanged = _lightSampling.LightSetChanged || _emissive.SetChanged,
                linear = LinearActive,
            };
            _gpu?.Begin(_cmd, SlotParticles);
            _particleLight.Execute(_cmd, frame);
            _gpu?.End(_cmd, SlotParticles);

            var cam = _camera.transform;
            float strength = hybrid ? _particleSettings.hybridStrength : _particleSettings.strength;
            _cmd.SetGlobalTexture(PLTexId, _particleLight.Output);
            _cmd.SetGlobalMatrix(PLViewProjId, _particleLight.ViewProj);
            _cmd.SetGlobalVector(PLCamId, new Vector4(cam.position.x, cam.position.y, cam.position.z, 1f));
            _cmd.SetGlobalVector(PLFwdId, new Vector4(cam.forward.x, cam.forward.y, cam.forward.z, Mathf.Max(0f, strength)));
            _cmd.SetGlobalVector(PLDepthId, _particleLight.DepthParams);
            var fc = RenderSettings.fogColor;
            _cmd.SetGlobalVector(PLFogColorId, new Vector4(fc.r, fc.g, fc.b, RenderSettings.fog ? 1f : 0f));
            _cmd.SetGlobalVector(PLFogParamsId, new Vector4(RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, 0, 0));
            _cmd.SetGlobalVector(PLAdditiveMaxId, new Vector4(Mathf.Max(0f, _particleSettings.additiveScale), Mathf.Max(0f, _particleSettings.additiveMax), 0, 0));

            if (Time.realtimeSinceStartup >= _particleNextLog)
            {
                _particleNextLog = Time.realtimeSinceStartup + 10f;
                Logger.LogInfo($"Lit particles: {(_litParticles?.Active == true ? "active" : "inactive")}, {_litParticles?.Count ?? 0} materials, " +
                               $"last scan {_particleScanMs:F2} ms, mode {name}, strength {strength}");
            }
        }

        /// <summary>For bench logs.</summary>
        private string ParticlesSummary() => _litParticles == null ? "lit particles unavailable"
            : $"lit particles {(_particleSettings.enabled ? "on" : "off")}, {(_litParticles.Active ? "active" : "inactive")}, {_litParticles.Count} materials";
    }
}
