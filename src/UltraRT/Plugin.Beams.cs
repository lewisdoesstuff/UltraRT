using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The game's light cones (LightPillar* materials: additive gradient meshes under lamps and in 0-3's pillars) are drawn
    /// by the game over the traced image at their fixed strength. Where the traced room is brighter than the game's, the
    /// cone reads thin (user, 0-3 pose 1: the light pillars "too transparent"). A level can raise them while frames are
    /// traced: "exposure": { "coneGain": F } in its light file (docs/lighting-overrides.md, "Light cones"). Applied to the
    /// shared materials (alpha first, the rest on the colour), the game's values back when tracing stops or the scene changes.
    /// </summary>
    public partial class Plugin
    {
        private readonly Dictionary<Material, Color> _coneOriginal = new Dictionary<Material, Color>();
        private string _coneScene;
        private float _coneApplied = 1f;
        private float _coneNextScan;
        /// <summary>Bench: plugin._coneGainOverride F (> 0) replaces the level's coneGain while traced.</summary>
        private float _coneGainOverride = 0f;

        private void UpdateConeGain()
        {
            string scene = SafeCurrentScene();
            if (scene != _coneScene)
            {
                RestoreCones();
                _coneOriginal.Clear();
                _coneScene = scene;
                _coneNextScan = Time.realtimeSinceStartup + 2f; // after the level's own Start
            }
            bool traced = _viewEnabled && _proxies?.Rtas != null && Time.frameCount - _rtTracedFrame < 30;
            float gain = !traced ? 1f : _coneGainOverride > 0f ? _coneGainOverride : _lightOverrides && _lightOv != null ? _lightOv.LevelConeGain : 1f;
            if (gain != 1f && _coneNextScan > 0f && Time.realtimeSinceStartup >= _coneNextScan)
            {
                _coneNextScan = 0f;
                RestoreCones();
                _coneOriginal.Clear();
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null || m.shader.name != "ULTRAKILL/Master" || m.renderQueue < 3000 || m.renderQueue >= 4000) continue;
                    if (!m.name.StartsWith("LightPillar", System.StringComparison.OrdinalIgnoreCase) || !m.HasProperty("_Color")) continue;
                    if (!m.HasProperty("_BlendMode") || Mathf.RoundToInt(m.GetFloat("_BlendMode")) != 3) continue; // additive cones only
                    _coneOriginal[m] = m.GetColor("_Color");
                }
                Logger.LogInfo($"Light cones ({scene}): {_coneOriginal.Count} materials, gain {gain:F2} while traced");
                _coneApplied = 1f;
            }
            if (gain == _coneApplied) return;
            if (gain == 1f) RestoreCones();
            else
                foreach (var kv in _coneOriginal)
                {
                    if (kv.Key == null) continue;
                    var c = kv.Value;
                    float a = Mathf.Min(1f, c.a * gain);
                    float rest = c.a > 0f ? gain * c.a / Mathf.Max(a, 1e-4f) : gain;
                    kv.Key.SetColor("_Color", new Color(c.r * rest, c.g * rest, c.b * rest, a));
                }
            _coneApplied = gain;
        }

        private void RestoreCones()
        {
            foreach (var kv in _coneOriginal) if (kv.Key != null) kv.Key.SetColor("_Color", kv.Value);
            _coneApplied = 1f;
        }
    }
}
