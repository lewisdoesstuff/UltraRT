using System;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Lamp cones (LightShafts.cs, docs/volumetrics.md "Lamp cones"): wiring. The volumetric pass gets one float4 pair per
    /// light in <c>_VolLightCone</c>: [0] cone axis + cos(half angle) (w &lt; -0.5: the light's own shape), [1] x weight of the
    /// light's scattering (multiplies the haze range weight), y cone edge sharpness.
    ///   -ultrart-shafts on|off, -ultrart-shaft-strength F, -ultrart-shaft-keepfakes; bench variant shaft.&lt;field&gt;.
    /// </summary>
    public partial class Plugin
    {
        private LightShafts _lightShafts;
        private readonly LightShaftSettings _shaftSettings = new LightShaftSettings();
        private ComputeBuffer _volConeBuffer;
        private readonly Vector4[] _volCone = new Vector4[MaxLights * 2];
        private bool _shaftArgsParsed;
        private int _shaftsFrame = -100;

        private void ParseShaftArgs()
        {
            _shaftArgsParsed = true;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-shafts": _shaftSettings.enabled = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-shaft-strength":
                        if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _shaftSettings.strength = v;
                        break;
                    case "-ultrart-shaft-keepfakes": _shaftSettings.keepFakes = true; break;
                }
            }
            Logger.LogInfo($"Lamp cones: {(_shaftSettings.enabled ? "on" : "off")}, strength {_shaftSettings.strength}{(_shaftSettings.keepFakes ? ", fakes kept" : "")}");
        }

        /// <summary>Every frame (OnUpdate): the game's fake cones are hidden only while the volumetric pass draws ours.</summary>
        private void UpdateShaftFakes()
        {
            if (_lightShafts == null) return;
            bool active = Time.frameCount - _shaftsFrame <= 2 && _viewEnabled;
            try { _lightShafts.UpdateFakes(active); }
            catch (Exception e) { Logger.LogWarning($"Lamp cones: {e.Message}"); }
        }

        /// <summary>The volumetric pass's per-light cone and weight buffer for this frame (TraceVolumetrics).</summary>
        private ComputeBuffer VolumetricsConeBuffer()
        {
            if (!_shaftArgsParsed) ParseShaftArgs();
            if (_lightShafts == null) _lightShafts = new LightShafts(Logger) { Settings = _shaftSettings };
            if (_volConeBuffer == null) _volConeBuffer = new ComputeBuffer(_volCone.Length, 16) { name = "UltraRT Volumetrics Cones" };
            if (_shaftSettings.enabled && _proxies != null)
            {
                try { _lightShafts.EnsureBuilt(_proxies, _proxies.Lights); }
                catch (Exception e) { Logger.LogWarning($"Lamp cones: build failed: {e.Message}"); }
            }
            _shaftsFrame = Time.frameCount;
            float hazeRange = Mathf.Max(0f, _volSettings.hazeRange);
            int n = Math.Min(_lightCount, MaxLights);
            for (int i = 0; i < n; i++)
            {
                _volCone[i * 2] = new Vector4(0, 0, 0, -1f);
                _volCone[i * 2 + 1] = new Vector4(1f, 0, 0, 0);
                var l = _packedLights[i];
                if (!_shaftSettings.enabled || ReferenceEquals(l, null) || l == null || !_lightShafts.TryGet(l, out var c)) continue;
                if ((l.transform.position - c.Apex).sqrMagnitude > 0.25f) continue; // moved away from its mesh
                // The cone gets `strength` in place of the haze range weight (Volumetrics.raytrace HazeWeight): the art says
                // this lamp's light is meant to be seen in the air, whatever its range.
                float r = _lightData[i * 4].w, haze = 1f;
                if (hazeRange > 0f) { float x = r / hazeRange; haze = 1f / (1f + x * x * x); }
                _volCone[i * 2] = new Vector4(c.Axis.x, c.Axis.y, c.Axis.z, c.CosHalf);
                _volCone[i * 2 + 1] = new Vector4(Mathf.Max(0f, _shaftSettings.strength) / Mathf.Max(haze, 1e-3f), c.Sharp, 0, 0);
            }
            _cmd.SetBufferData(_volConeBuffer, _volCone, 0, 0, Math.Max(1, n) * 2);
            return _volConeBuffer;
        }
    }
}
