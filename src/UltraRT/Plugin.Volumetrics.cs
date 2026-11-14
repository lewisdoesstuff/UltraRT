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
        private float _volNextLog, _volNextSurvey;
        private CommandBuffer _volLateCmd;       // after the game's transparents: the in-scattering (VolumetricsComposite pass 1)
        private Camera _volLateCam;
        private ComputeBuffer _volSkipBuffer;    // one bit per light: lights attached to the player / camera
        private readonly uint[] _volSkip = new uint[(MaxLights + 31) / 32];
        private Vector3 _volSurveyPos = new Vector3(1e9f, 0, 0);
        private const float AttachedLightReach = 1.5f; // m from the eye: player-attached lights the medium skips (the gun, a held torch)

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

        /// <summary>Every frame from OnCameraPreRender, before Trace: keeps the late buffer on the camera, empty unless the
        /// pass runs this frame.</summary>
        private void VolumetricsFrameReset(Camera cam)
        {
            if (_volLateCmd == null) _volLateCmd = new CommandBuffer { name = "UltraRT Volumetrics (after transparents)" };
            _volLateCmd.Clear();
            if (_volLateCam == cam) return;
            if (_volLateCam != null) _volLateCam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _volLateCmd);
            if (cam != null) cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _volLateCmd);
            _volLateCam = cam;
        }

        /// <summary>Lights the medium skips: attached to the player or the camera (the player's light sits at the eye).</summary>
        private ComputeBuffer VolumetricsSkipMask()
        {
            if (_volSkipBuffer == null) _volSkipBuffer = new ComputeBuffer(_volSkip.Length, 4) { name = "UltraRT Volumetrics Skip" };
            Array.Clear(_volSkip, 0, _volSkip.Length);
            var player = MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.transform : null;
            var camT = _camera != null ? _camera.transform : null;
            var camPos = camT != null ? camT.position : Vector3.zero;
            for (int i = 0; i < _lightCount; i++)
            {
                var l0 = _lightData[i * 4];
                if (l0.w == 0f) continue;
                var lt = _packedLights[i];
                // Attached lights only while they are at the player: a held item thrown on the whiplash (4-3's torch) stays
                // in the camera's hierarchy while it flies, and its halo is what shows it moving through the air.
                float eye2 = (new Vector3(l0.x, l0.y, l0.z) - camPos).sqrMagnitude;
                bool attached = eye2 < AttachedLightReach * AttachedLightReach && lt != null &&
                                ((player != null && lt.transform.IsChildOf(player)) || (camT != null && lt.transform.IsChildOf(camT)));
                bool atEye = eye2 < 0.25f;
                if ((attached || atEye) && !_volSettings.playerLights) _volSkip[i >> 5] |= 1u << (i & 31);
            }
            _cmd.SetBufferData(_volSkipBuffer, _volSkip);
            return _volSkipBuffer;
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
            if (RelightLook(name)) return true;
            if (name == "pt-dn") return _volSettings.ptDn;
            return kind == Kind.Hybrid && _volSettings.hybrid;
        }

        /// <summary>After the RT composite (the camera target holds the RT image), before the pre-alpha copy and the game's
        /// transparents.</summary>
        private void TraceVolumetrics(Kind kind, bool upsampled)
        {
            string name = Modes[_mode].name;
            if (_autoExposure != null) _autoExposure.HazeParams = Vector4.zero; // set again below when the pass runs
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
                skip = VolumetricsSkipMask(),
                cones = VolumetricsConeBuffer(), // lamp cones (Plugin.Shafts.cs)
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
            bool late = _volLateCmd != null && _volLateCam == _camera;
            if (late) _gpu?.Begin(_volLateCmd, SlotVolLate);
            _volumetrics.Execute(_cmd, late ? _volLateCmd : null, frame);
            if (late) _gpu?.End(_volLateCmd, SlotVolLate);
            _gpu?.End(_cmd, SlotVolumetrics);
            if (_autoExposure != null && _volSettings.meterHaze > 0f)
            {
                _autoExposure.Haze = _volumetrics.Result;
                _autoExposure.HazeParams = new Vector4(_volumetrics.ResultSize.x, _volumetrics.ResultSize.y,
                    Mathf.Max(0f, _volSettings.strength) * _volSettings.meterHaze, 1f);
            }

            // Light survey for tuning (which lights fill the air): at most once a second, when the camera has moved or every 10 s.
            if (Time.realtimeSinceStartup >= _volNextSurvey &&
                ((_camera.transform.position - _volSurveyPos).sqrMagnitude > 4f || Time.realtimeSinceStartup >= _volNextLog))
            {
                _volNextSurvey = Time.realtimeSinceStartup + 1f;
                _volSurveyPos = _camera.transform.position;
                LogVolumetricLights();
            }
            if (Time.realtimeSinceStartup >= _volNextLog)
            {
                _volNextLog = Time.realtimeSinceStartup + 10f;
                var m = _volumetrics.Medium;
                string sunText = sun >= 0 ? FormattableString.Invariant($"#{sun} ({sunColor.r:F2}, {sunColor.g:F2}, {sunColor.b:F2})") : "none";
                Logger.LogInfo(FormattableString.Invariant($"Volumetrics: medium {m.x:F4}/m (fog {_volumetrics.MediumSource}, colour {RenderSettings.fogColor}), tint ({m.y:F2}, {m.z:F2}, {m.w:F2}), ") +
                               $"sun {sunText}, {_lightCount} lights, game fog {(frame.applyGameFog && _volSettings.gameFog ? "on" : "off")}");
            }
        }

        /// <summary>The point/spot lights that light the most air along the view direction (the shader's LightTarget, for a
        /// 100 m ray through the screen centre, before the haze range weight), with their names and ranges.</summary>
        private void LogVolumetricLights()
        {
            var o = _camera.transform.position;
            var dir = _camera.transform.forward;
            const float tMax = 100f;
            var list = new System.Collections.Generic.List<(float imp, int i)>();
            for (int i = 0; i < _lightCount; i++)
            {
                var l0 = _lightData[i * 4];
                if (l0.w == 0f) continue;
                var c = _lightData[i * 4 + 1];
                float lum = 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
                var p = new Vector3(l0.x, l0.y, l0.z);
                float reach = 2f * l0.w, delta = Vector3.Dot(p - o, dir);
                float h2 = (p - (o + dir * delta)).sqrMagnitude;
                float k2 = reach * reach - h2;
                if (k2 <= 0f || lum <= 0f) continue;
                float k = Mathf.Sqrt(k2), tA = Mathf.Max(delta - k, 0f), tB = Mathf.Min(delta + k, tMax);
                if (tB <= tA) continue;
                float h = Mathf.Max(Mathf.Sqrt(h2), 0.05f);
                float imp = lum * 0.25f * l0.w * l0.w * (Mathf.Atan((tB - delta) / h) - Mathf.Atan((tA - delta) / h)) / h;
                list.Add((imp, i));
            }
            list.Sort((a, b) => b.imp.CompareTo(a.imp));
            float total = 0f;
            foreach (var e in list) total += e.imp;
            var sb = new System.Text.StringBuilder();
            sb.Append(FormattableString.Invariant($"Volumetrics lights at {o} ({_lightCount} lights, {list.Count} reach the view ray):"));
            for (int n = 0; n < Mathf.Min(6, list.Count); n++)
            {
                int i = list[n].i;
                var l0 = _lightData[i * 4];
                var c = _lightData[i * 4 + 1];
                var lt = _packedLights[i];
                string nm = lt != null ? lt.name : "effect";
                string type = _lightData[i * 4 + 3].x > -0.5f ? "spot" : "point";
                float dist = Vector3.Distance(o, new Vector3(l0.x, l0.y, l0.z));
                sb.Append(FormattableString.Invariant($" [{100f * list[n].imp / Mathf.Max(total, 1e-9f):F0}% '{nm}' {type} range {l0.w:F1} col ({c.x:F2},{c.y:F2},{c.z:F2}) dist {dist:F1}]"));
            }
            Logger.LogInfo(sb.ToString());
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
