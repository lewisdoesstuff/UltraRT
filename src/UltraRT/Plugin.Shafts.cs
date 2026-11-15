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
        private ComputeBuffer _volBeamBuffer;
        private int _housingVersion = -1;
        private object _housingScene;
        private const int MaxBeams = 8;
        private readonly Vector4[] _volBeams = new Vector4[MaxBeams * 3];
        public int VolBeamCount { get; private set; }

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

        /// <summary>
        /// The game's beam meshes as the volumetric pass draws them (LightShafts.beamStrength; Volumetrics.raytrace
        /// BeamSamples): 3 float4 per beam: [0] apex (the light now) + cos(half angle), [1] axis + far end (m along it),
        /// [2] linear colour x density (per metre) + near end (where the meshes begin). The density is set so that a ray
        /// through the beam's middle gathers what the game's layers add there: alpha_total = 1 - (1 - a)^(2 n).
        /// </summary>
        private ComputeBuffer VolumetricsBeamBuffer(out int count)
        {
            if (_volBeamBuffer == null) _volBeamBuffer = new ComputeBuffer(_volBeams.Length, 16) { name = "UltraRT Volumetrics Beams" };
            count = 0;
            float k = _shaftSettings.beamStrength;
            if (_shaftSettings.enabled && k > 0f && _lightShafts != null)
            {
                var cam = _camera != null ? _camera.transform.position : Vector3.zero;
                foreach (var c in _lightShafts.Cones)
                {
                    if (count >= MaxBeams) break;
                    if (!c.Parented || !c.InPlace || c.Light == null || !c.Light.isActiveAndEnabled || c.BeamAlpha <= 0f) continue;
                    var apex = c.Light.transform.position;
                    if ((apex - cam).sqrMagnitude > (c.Length + 150f) * (c.Length + 150f)) continue; // out of reach
                    var axis = c.WorldAxis;
                    float tanHalf = Mathf.Tan(Mathf.Acos(Mathf.Clamp(c.CosHalf, -1f, 1f)));
                    float diameter = Mathf.Max(0.5f, tanHalf * (c.Start + c.Length));
                    float alphaTotal = 1f - Mathf.Pow(1f - c.BeamAlpha, 2f * Mathf.Max(1, c.Fakes.Count));
                    var lin = c.BeamColor.linear;
                    float dens = k * alphaTotal / diameter;
                    _volBeams[count * 3] = new Vector4(apex.x, apex.y, apex.z, c.CosHalf);
                    _volBeams[count * 3 + 1] = new Vector4(axis.x, axis.y, axis.z, c.Length);
                    _volBeams[count * 3 + 2] = new Vector4(lin.r * dens, lin.g * dens, lin.b * dens, Mathf.Min(c.Start, c.Length));
                    count++;
                }
            }
            VolBeamCount = count;
            _cmd.SetBufferData(_volBeamBuffer, _volBeams, 0, 0, Math.Max(1, count) * 3);
            return _volBeamBuffer;
        }

        /// <summary>The volumetric pass's per-light cone and weight buffer for this frame (TraceVolumetrics).</summary>
        private ComputeBuffer VolumetricsConeBuffer()
        {
            if (!_shaftArgsParsed) ParseShaftArgs();
            if (_lightShafts == null) _lightShafts = new LightShafts(Logger) { Settings = _shaftSettings };
            if (_volConeBuffer == null) _volConeBuffer = new ComputeBuffer(_volCone.Length, 16) { name = "UltraRT Volumetrics Cones" };
            if (_shaftSettings.enabled && _proxies != null)
            {
                try
                {
                    _lightShafts.EnsureBuilt(_proxies, _proxies.Lights);
                    if (_lightShafts.Version != _housingVersion || !ReferenceEquals(_housingScene, _proxies))
                    {
                        _housingVersion = _lightShafts.Version;
                        _housingScene = _proxies;
                        if (_shaftSettings.housings)
                            foreach (var c in _lightShafts.Cones)
                                if (c.Parented && c.Light != null) _proxies.AddHousing(c.Light, c.Fakes);
                    }
                }
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
                if (!c.InPlace) continue; // moved away from its mesh (parent-spot cones move with their light)
                if (c.Parented && _shaftSettings.beamStrength > 0f) continue; // drawn as a beam (VolumetricsBeamBuffer); the spot keeps its own haze
                // The cone gets `strength` in place of the haze range weight (Volumetrics.raytrace HazeWeight): the art says
                // this lamp's light is meant to be seen in the air, whatever its range.
                float r = _lightData[i * 4].w, haze = 1f;
                if (hazeRange > 0f) { float x = r / hazeRange; haze = 1f / (1f + x * x * x); }
                var axis = c.WorldAxis;
                _volCone[i * 2] = new Vector4(axis.x, axis.y, axis.z, c.CosHalf);
                _volCone[i * 2 + 1] = new Vector4(Mathf.Max(0f, _shaftSettings.strength) / Mathf.Max(haze, 1e-3f), c.Sharp, 0, 0);
            }
            _cmd.SetBufferData(_volConeBuffer, _volCone, 0, 0, Math.Max(1, n) * 2);
            return _volConeBuffer;
        }
    }
}
