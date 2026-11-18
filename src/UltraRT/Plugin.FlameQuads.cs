using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The literal "flames as emitting surfaces" test (fx.flameBillboards=1|2, off by default; docs/emissive-lights.md,
    /// "Billboards in the emissive table"): per flame that sits on a game light, a camera-facing square in the emissive
    /// triangle table with the light's output, and the light off. Kept as an A/B against the flame-driven lights.
    /// </summary>
    public partial class Plugin
    {
        private readonly Dictionary<Light, Transform> _flameQuadSpaces = new Dictionary<Light, Transform>();
        private readonly List<KeyValuePair<Light, Vector4>> _flameScratch = new List<KeyValuePair<Light, Vector4>>();
        private int _flameQuadMode;

        /// <summary>EmissiveLights.FlameQuads.</summary>
        private List<EmissiveLights.FlameQuad> FlameQuadList()
        {
            if (_effectSettings.flameBillboards <= 0 || _effectLights == null) return null;
            var list = new List<EmissiveLights.FlameQuad>();
            _flameScratch.Clear();
            _effectLights.Flames(_flameScratch);
            var plain = new LightHandover();
            foreach (var kv in _flameScratch)
            {
                var l = kv.Key;
                if (l == null || l.type != LightType.Point) continue;
                if (!_flameQuadSpaces.TryGetValue(l, out var sp) || sp == null)
                {
                    var go = new GameObject("UltraRT flame quad") { hideFlags = HideFlags.HideAndDontSave };
                    sp = go.transform;
                    _flameQuadSpaces[l] = sp;
                }
                sp.position = new Vector3(kv.Value.x, kv.Value.y, kv.Value.z);
                float half = Mathf.Clamp(kv.Value.w * _effectSettings.flameRadius, 0.05f, Mathf.Max(0.05f, _effectSettings.flameMaxRadius));
                var power = HandoverPowerOf(l, BuildResolved(l, false), plain) * (Mathf.Max(0f, _effectSettings.flameGain) / LightClasses.HandoverGain);
                list.Add(new EmissiveLights.FlameQuad { Space = sp, Half = half, Power = power, TwoSided = _effectSettings.flameBillboards >= 2 });
            }
            Logger.LogInfo($"Flame billboards: {list.Count} quads in the emissive table (mode {_effectSettings.flameBillboards})");
            return list;
        }

        /// <summary>Every frame: the squares follow their flames and face the camera; a change of the switch rebuilds the table.</summary>
        private void UpdateFlameQuads(Camera cam)
        {
            int mode = _effectSettings.flameBillboards;
            if (mode != _flameQuadMode)
            {
                _flameQuadMode = mode;
                _emissive?.Clear();
                ResetLightHistories();
            }
            if (mode <= 0 || cam == null || _effectLights == null || _flameQuadSpaces.Count == 0) return;
            _flameScratch.Clear();
            _effectLights.Flames(_flameScratch);
            var eye = cam.transform.position;
            foreach (var kv in _flameScratch)
            {
                if (kv.Key == null || !_flameQuadSpaces.TryGetValue(kv.Key, out var sp) || sp == null) continue;
                var p = new Vector3(kv.Value.x, kv.Value.y, kv.Value.z);
                var d = eye - p;
                if (d.sqrMagnitude < 1e-6f) continue;
                sp.SetPositionAndRotation(p, Quaternion.LookRotation(d.normalized, Vector3.up));
            }
        }
    }
}
