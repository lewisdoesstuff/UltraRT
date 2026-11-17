using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Emitter masks (docs/lighting-overrides.md, "Emissive fixtures"): a surface the game does not mark as emissive
    /// (a fullbright lamp shade, a screen, a lit texture with a painted bulb) becomes an emitter by an "emitters" rule
    /// with "glow". Its albedo is the emission, times the glow, in the texels whose luminance passes the rule's
    /// threshold (the mask); the proxy then lights the scene like any emissive surface (bounces and the emissive
    /// triangle table), and the fake light the game put next to it can go.
    ///
    /// Only plain proxy materials: not the baked level atlas (its materials are whole-level atlases, a name cannot
    /// pick a fixture out of them), not transparents, not the player. Per-renderer variants made before a rule change
    /// keep their old mask until they are rebuilt (bench variants only: rules load before a level's proxies).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Source material -> (glow, threshold); glow 0 = no rule. Set by the plugin.</summary>
        public Func<Material, Vector2> EmitterGlowOf;

        private static readonly int IdEmissiveThreshold = Shader.PropertyToID("_EmissiveThreshold");
        private readonly HashSet<Material> _glowProxies = new HashSet<Material>();

        /// <summary>Proxy materials that glow by a rule.</summary>
        public int GlowMaterials => _glowProxies.Count;

        private void ApplyEmitterGlow(Material src, Material m)
        {
            var g = EmitterGlowOf != null ? EmitterGlowOf(src) : Vector2.zero;
            bool had = _glowProxies.Contains(m);
            // A material with emission of its own keeps it: its light is scaled by the rule's "scale" instead.
            if (g.x > 0f && (had || m.GetFloat("_EmissiveIntensity") <= 0f))
            {
                m.SetColor("_EmissiveColor", Color.white);
                m.SetFloat("_EmissiveIntensity", g.x);
                m.SetFloat("_UseAlbedoAsEmissive", 1f);
                m.SetFloat("_EmissiveMask", 0f);
                m.SetFloat(IdEmissiveThreshold, Mathf.Clamp01(g.y));
                _glowProxies.Add(m);
            }
            else if (had)
            {
                m.SetFloat("_EmissiveIntensity", 0f);
                m.SetFloat("_UseAlbedoAsEmissive", 0f);
                m.SetFloat(IdEmissiveThreshold, 0f);
                _glowProxies.Remove(m);
            }
        }

        /// <summary>The rules changed: every proxy material again. The caller rebuilds the emissive triangle table.</summary>
        public void SyncEmitterGlow()
        {
            foreach (var kv in _materialCache)
                if (kv.Key != null && kv.Value != null && !_atlasProxies.Contains(kv.Value)) ApplyEmitterGlow(kv.Key, kv.Value);
        }
    }
}
