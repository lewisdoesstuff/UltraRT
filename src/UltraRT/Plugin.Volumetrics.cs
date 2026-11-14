using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Volumetric lighting wiring (docs/volumetrics.md). Plugin.cs only has one-line hooks: ParseVolumetricsArgs (Awake),
    /// LoadVolumetricsShaders (LoadShaders), _volumetrics.Reset (ResetScene), VolumetricsHotkeys (OnUpdate) and
    /// TraceVolumetrics (Trace, right after the RT composite).
    ///   -ultrart-volumetrics on|off (config [Volumetrics] Enabled), -ultrart-vol-density F, -ultrart-vol-min-density F,
    ///   -ultrart-vol-strength F, -ultrart-vol-steps N, -ultrart-vol-block N, -ultrart-vol-g G, -ultrart-vol-lights F,
    ///   -ultrart-vol-distance M, -ultrart-vol-nofog, -ultrart-vol-hybrid
    /// Ctrl+F6 toggles in game.
    /// </summary>
    public partial class Plugin
    {
        private readonly VolumetricsSettings _volSettings = new VolumetricsSettings();
        private Volumetrics _volumetrics;
        private Material _volCompositeMat;
        private int _volLastFrame = -10;
        private float _volNextLog;

        private void ParseVolumetricsArgs()
        {
            _volSettings.enabled = Config.Bind("Volumetrics", "Enabled", true,
                "Ray-traced volumetric lighting (sun shafts, light halos) and the game's distance fog on ray-traced pixels, " +
                "in relight and pt-dn (docs/volumetrics.md). Overridden by -ultrart-volumetrics on|off; Ctrl+F6 toggles in game.").Value;
            _volSettings.ParseArgs();
            Logger.LogInfo($"Volumetrics: {_volSettings}");
        }

        private void LoadVolumetricsShaders(AssetBundle bundle)
        {
            _volumetrics?.Dispose();
            _volumetrics = null;
            var rt = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault(s => s.name == "Volumetrics");
            var cs = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Volumetrics");
            var sh = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/VolumetricsComposite");
            if (rt == null || cs == null || sh == null)
            {
                Logger.LogWarning($"Volumetrics shaders missing (raytrace={(rt ? "ok" : "MISSING")} compute={(cs ? "ok" : "MISSING")} composite={(sh ? "ok" : "MISSING")}): volumetrics off");
                return;
            }
            if (_volCompositeMat != null) Destroy(_volCompositeMat);
            _volCompositeMat = new Material(sh) { name = "UltraRT Volumetrics Composite" };
            _volumetrics = new Volumetrics(rt, cs, _volSettings);
        }

        private void VolumetricsHotkeys()
        {
            if (!Input.GetKeyDown(KeyCode.F6) || !CtrlHeld()) return;
            _volSettings.enabled = !_volSettings.enabled;
            _volumetrics?.Reset();
            Logger.LogInfo($"Volumetrics: {(_volSettings.enabled ? "on" : "off")}");
            Toast($"Volumetric lighting {(_volSettings.enabled ? "on" : "off")} (Ctrl+F6)");
        }

        private bool VolumetricsFor(Kind kind, string name)
        {
            if (_volumetrics == null || !_volSettings.enabled) return false;
            if (name == "relight") return true;
            if (name == "pt-dn") return _volSettings.ptDn;
            return kind == Kind.Hybrid && _volSettings.hybrid;
        }

        /// <summary>After the RT composite (the camera target holds the RT image), before the pre-alpha copy and the game's
        /// transparents.</summary>
        private void TraceVolumetrics(Kind kind, bool upsampled)
        {
            string name = Modes[_mode].name;
            if (!VolumetricsFor(kind, name) || _depthCopy == null || _proxies?.Rtas == null || _camera == null) return;
            var target = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
            if (target == null || target.width != _depthCopy.width || target.height != _depthCopy.height) return;
            if (Time.frameCount - _volLastFrame > 2) _volumetrics.Reset(); // stale after other modes / view off
            _volLastFrame = Time.frameCount;

            // The sun: the brightest shadowed directional light (fill lights from below the horizon are fakes).
            int sun = -1;
            float best = 0f;
            for (int i = 0; i < _lightCount; i++)
            {
                if (_lightData[i * 4].w != 0f || _lightData[i * 4 + 3].z > 0.5f) continue;
                var c = _lightData[i * 4 + 1];
                float lum = 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
                if (lum > best) { best = lum; sun = i; }
            }
            float lightExposure = _relightActive ? _relightExposure : 1f;
            Vector4 sunDir = Vector4.zero;
            Color sunColor = Color.black;
            if (sun >= 0)
            {
                var d = _lightData[sun * 4];
                sunDir = new Vector4(d.x, d.y, d.z, Mathf.Cos(Mathf.Max(0f, _sunAngle) * Mathf.Deg2Rad));
                var c = _lightData[sun * 4 + 1] * lightExposure;
                sunColor = new Color(Mathf.Pow(Mathf.Max(0f, c.x), 2.2f), Mathf.Pow(Mathf.Max(0f, c.y), 2.2f), Mathf.Pow(Mathf.Max(0f, c.z), 2.2f));
            }

            var frame = new VolumetricsFrame
            {
                camera = _camera,
                gameDepth = _depthCopy,
                target = target,
                rtas = _proxies.Rtas,
                lights = _lightBuffer,
                lightCount = _lightCount,
                sunDir = sunDir,
                sunColor = sunColor,
                falloff = new Vector4(_relightActive ? _relightFalloff : 0f, lightExposure, 1f, 0f),
                localLightCount = _lightCount,
                applyGameFog = upsampled && kind != Kind.Hybrid,
                autoExposure = AutoExposureFor(name),
                exposure = _autoExposure?.ExposureBuffer,
                composite = _volCompositeMat,
                frameIndex = _frameIndex,
            };
            _gpu?.Begin(_cmd, SlotVolumetrics);
            _emissive.Bind(_cmd, _volumetrics.RtShader, _lightSamplingSettings.mode); // emissive triangles for surface glow scattering
            _volumetrics.Execute(_cmd, frame);
            _gpu?.End(_cmd, SlotVolumetrics);

            if (Time.realtimeSinceStartup >= _volNextLog)
            {
                _volNextLog = Time.realtimeSinceStartup + 10f;
                var m = _volumetrics.Medium;
                string sunText = sun >= 0 ? FormattableString.Invariant($"#{sun} ({sunColor.r:F2}, {sunColor.g:F2}, {sunColor.b:F2})") : "none";
                Logger.LogInfo(FormattableString.Invariant($"Volumetrics: medium {m.x:F4}/m (fog {_volumetrics.MediumSource}, colour {RenderSettings.fogColor}), tint ({m.y:F2}, {m.z:F2}, {m.w:F2}), ") +
                               $"sun {sunText}, {_lightCount} lights, game fog {(frame.applyGameFog && _volSettings.gameFog ? "on" : "off")}");
            }
        }

        /// <summary>For bench logs.</summary>
        private string VolumetricsSummary()
        {
            if (_volumetrics == null) return "volumetrics unavailable";
            var m = _volumetrics.Medium;
            return FormattableString.Invariant($"volumetrics {(_volSettings.enabled ? "on" : "off")}: medium {m.x:F4}/m ({_volumetrics.MediumSource}), fog {(RenderSettings.fog ? "on" : "off")} {RenderSettings.fogColor}");
        }
    }
}
