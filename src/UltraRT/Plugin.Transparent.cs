using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Water and glass wiring (docs/reflections.md, "Water and glass"). Plugin.cs only has one-line hooks:
    /// ParseTransparentArgs (Awake), LoadTransparentShaders (LoadShaders), TransparentFrameReset (OnCameraPreRender),
    /// TraceTransparents (Trace, after the denoisers) and CopyPreAlpha (Trace, after the composite).
    ///   -ultrart-notransparent            no transparent proxies, no overlays, no pass
    ///   -ultrart-transparent-strength X   Fresnel weight multiplier (default 1)
    ///   -ultrart-water-waves X            ripple strength (default 1, 0 = flat mirror water)
    ///   -ultrart-water-refraction X       screen-space refraction strength (default 1, 0 = off)
    ///   -ultrart-caustics X               the game's water caustics in the traced modes (default 2, 0 = off; docs/caustics.md)
    ///   -ultrart-caustics-noshadow        caustics without their shadow ray (as the game draws them)
    ///   -ultrart-ripple-caustics X        caustics of our water ripples on what lies under water (default 1, 0 = off)
    ///   -ultrart-water-absorb X           water absorption, sigma per metre of path (default 0.35, 0 = off)
    /// F4 (reflections) switches the pass too.
    /// </summary>
    public partial class Plugin
    {
        private static readonly int TransOnId = Shader.PropertyToID("_UltraRTTransOn");
        private static readonly int TransCamId = Shader.PropertyToID("_UltraRTTransCam");
        private static readonly int TransVPId = Shader.PropertyToID("_UltraRTTransVP");
        private static readonly int TransReflId = Shader.PropertyToID("_UltraRTTransRefl");
        private static readonly int TransParamsId = Shader.PropertyToID("_UltraRTTransParams");
        private static readonly int TransDisplaceId = Shader.PropertyToID("_UltraRTOverlayDisplace");
        private static readonly int TransFogId = Shader.PropertyToID("_UltraRTFog");
        private static readonly int TransWaterId = Shader.PropertyToID("_UltraRTWater");
        private static readonly int PreAlphaId = Shader.PropertyToID("_UltraRTPreAlpha");
        private static readonly int WaterAbsorbId = Shader.PropertyToID("_UltraRTWaterAbsorb");

        private TransparentSettings TransparentCfg => SceneProxies.Transparents;
        private RenderTexture _preAlpha;
        private bool _transTraced; // this frame: the pass ran, CopyPreAlpha may follow
        private int _lastActiveTransparent = -1, _transLogs;
        private bool _transActive;   // this frame: RayGen traces transparents into _Output
        private Vector4 _transWater; // ripple parameters shared by RayGen and the overlays

        private void ParseTransparentArgs()
        {
            var a = Environment.GetCommandLineArgs();
            var s = TransparentCfg;
            for (int i = 0; i < a.Length; i++)
            {
                float F(float fallback) => i + 1 < a.Length && float.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-notransparent": s.enabled = false; break;
                    case "-ultrart-nofrosted": s.frosted = false; break;
                    case "-ultrart-transparent-strength": s.strength = F(s.strength); break;
                    case "-ultrart-water-waves": s.waves = F(s.waves); break;
                    case "-ultrart-water-refraction": s.refraction = F(s.refraction); break;
                    case "-ultrart-caustics": SceneProxies.Caustics.strength = F(SceneProxies.Caustics.strength); break;
                    case "-ultrart-caustics-noshadow": SceneProxies.Caustics.shadows = false; break;
                    case "-ultrart-ripple-caustics": SceneProxies.Caustics.ripple = F(SceneProxies.Caustics.ripple); break;
                    case "-ultrart-water-absorb": s.waterAbsorb = F(s.waterAbsorb); break;
                }
            }
        }

        private void LoadTransparentShaders(AssetBundle bundle)
        {
            var overlay = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/TransparentOverlay");
            if (overlay == null) Logger.LogWarning("TransparentOverlay shader missing from bundle: water and glass are not ray traced");
            else if (!overlay.isSupported) Logger.LogWarning("TransparentOverlay shader unsupported: water and glass are not ray traced");
            SceneProxies.OverlayShader = overlay != null && overlay.isSupported ? overlay : null;
        }

        /// <summary>Every main-camera frame, before anything is recorded: overlays stay inert unless the pass runs.</summary>
        private void TransparentFrameReset()
        {
            Shader.SetGlobalFloat(TransOnId, 0f);
            _transTraced = false;
            _transActive = false;
        }

        /// <summary>
        /// Before the main DispatchRays (PathTrace bind block): whether RayGen also traces water and glass into _Output
        /// (denoised modes only, and only while a transparent instance is active).
        /// </summary>
        /// <summary>Bench plugin._transRect=0: the transparent ray at every pixel again (no screen rectangle).</summary>
        private bool _transRect = true;
        private int _transRectLogs;
        private bool _transRectLastInView = true;
        private string _transRectWidestLogged = "";

        private void BindTransparents(RayTracingShader shader, bool denoised, Matrix4x4 viewProj, int w, int h)
        {
            var s = TransparentCfg;
            if (_proxies != null && _proxies.ActiveTransparent != _lastActiveTransparent && _transLogs < 100)
            {
                _transLogs++;
                Logger.LogInfo($"Transparent instances active: {_proxies.ActiveTransparent}");
                _lastActiveTransparent = _proxies.ActiveTransparent;
            }
            _transActive = denoised && s.enabled && _reflSettings.enabled && SceneProxies.OverlayShader != null && _proxies != null && _proxies.ActiveTransparent > 0
                           // Under water the overlays draw nothing for water (see TransparentOverlay.shader): don't trace
                           // reflections that would be discarded (every pixel hits the volume: +0.4 ms in 7-1).
                           && !_proxies.InsideWater(_camera.transform.position);
            // A level's own ripple factor ("exposure": { "waterWaves": F }: 8-2's still pool, docs/reflections.md).
            float levelWaves = _lightOverrides && _aeSettings.levelOverride && _lightOv != null ? _lightOv.LevelWaterWaves : 1f; // bench ae.levelOverride=0: without
            _transWater = new Vector4(s.waves * levelWaves, Time.timeSinceLevelLoad % 3600f, s.waveFade, s.refraction);
            // Only where water, glass or blood can be on screen (docs/performance.md, "Ray counts": the transparent ray ran at
            // every pixel whenever anything with the transparent mask was active, light cones included). Padded by 4 trace
            // pixels; nothing in view skips the ray and the overlays this frame.
            var rect = new Vector4(-2f, -2f, 2f, 2f);
            if (_transActive && _transRect)
            {
                bool inView = _proxies.TransparentScreenRect(viewProj, out rect);
                float px = 8f / Mathf.Max(w, 1), py = 8f / Mathf.Max(h, 1);
                rect = new Vector4(rect.x - px, rect.y - py, rect.z + px, rect.w + py);
                string widest = inView ? _proxies.TransparentRectWidestName : null;
                if ((inView != _transRectLastInView || widest != _transRectWidestLogged) && _transRectLogs < 40)
                {
                    _transRectLogs++;
                    Logger.LogInfo(inView ? $"Transparent ray: water/glass in view, NDC rect ({rect.x:F2}, {rect.y:F2})-({rect.z:F2}, {rect.w:F2}), widest {_proxies.TransparentRectWidest}" : "Transparent ray: no water/glass in view, skipped");
                }
                _transRectLastInView = inView;
                _transRectWidestLogged = widest;
                if (!inView) _transActive = false;
            }
            _cmd.SetRayTracingVectorParam(shader, "_TransRect", rect);
            // Water / glass behind portals (docs/portals.md, "Water and glass through portals"): a layer over the far-side
            // hit, collected by the camera ray on every segment past a portal, in every mode (not tied to the near side's
            // screen rectangle).
            bool portalGlass = s.enabled && s.portalGlass && _reflSettings.enabled && _proxies != null && _proxies.ActiveTransparent > 0;
            _cmd.SetRayTracingVectorParam(shader, "_TransParams", new Vector4(s.cacheSpread, _transActive ? 1f : 0f, 0, 0));
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTPortalGlass", new Vector4(portalGlass ? s.portalGlassStrength : 0f, Mathf.Clamp(s.portalGlassHits, 1, 2), 0, 0));
            _cmd.SetRayTracingVectorParam(shader, "_UltraRTWater", _transWater);
        }

        /// <summary>After the denoisers: point the overlays at this frame's result (written by RayGen into _Output).</summary>
        private void TraceTransparents(Matrix4x4 viewProj)
        {
            if (!_transActive) return;
            var s = TransparentCfg;
            var cam = _camera.transform.position;
            bool refract = s.refraction > 0f && MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex != null;
            _cmd.SetGlobalFloat(TransOnId, 1f);
            _cmd.SetGlobalVector(TransCamId, new Vector4(cam.x, cam.y, cam.z, refract ? 1f : 0f));
            _cmd.SetGlobalMatrix(TransVPId, viewProj);
            // The reflection after its temporal filter (ReflectionFilter.ExecuteTransparent; raw with trans.temporal=0).
            _cmd.SetGlobalTexture(TransReflId, _reflFilter != null ? _reflFilter.ExecuteTransparent(_cmd, _output, viewProj, cam, s) : _output);
            _cmd.SetGlobalVector(TransWaterId, _transWater);
            _cmd.SetGlobalFloat(TransDisplaceId, s.overlayDisplace ? 1f : 0f); // overlays on displaced water follow the waves
            _cmd.SetGlobalVector(TransParamsId, new Vector4(s.strength, s.maxWeight, _relightActive ? _relightWhite : 0f, 0f));
            // Water absorption (overlay pass Absorb; docs/reflections.md, "Water absorption").
            _cmd.SetGlobalVector(WaterAbsorbId, new Vector4(Mathf.Max(s.waterAbsorb, 0f), Mathf.Clamp01(s.waterAbsorbFloor), Mathf.Clamp01(s.waterAbsorbTint), Mathf.Max(s.waterAbsorbMaxPath, 0.1f)));
            float mode = !RenderSettings.fog ? 0f : RenderSettings.fogMode == FogMode.Linear ? 1f : RenderSettings.fogMode == FogMode.Exponential ? 2f : 3f;
            _cmd.SetGlobalVector(TransFogId, new Vector4(mode, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, RenderSettings.fogDensity));
            _transTraced = refract;
        }

        /// <summary>
        /// After the RT composite, before the game's transparents: the background the water is blended over, for the
        /// overlay's screen-space refraction.
        /// </summary>
        private void CopyPreAlpha()
        {
            if (!_transTraced) return;
            var target = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
            if (target == null) return;
            if (_preAlpha == null || _preAlpha.width != target.width || _preAlpha.height != target.height || _preAlpha.format != target.format)
            {
                if (_preAlpha != null) _preAlpha.Release();
                var desc = target.descriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.enableRandomWrite = false;
                _preAlpha = new RenderTexture(desc) { name = "UltraRT PreAlpha", filterMode = FilterMode.Point };
                _preAlpha.Create();
            }
            _gpu?.Begin(_cmd, SlotPreAlpha);
            _cmd.CopyTexture(target, _preAlpha);
            _gpu?.End(_cmd, SlotPreAlpha);
            _cmd.SetGlobalTexture(PreAlphaId, _preAlpha);
        }
    }
}
