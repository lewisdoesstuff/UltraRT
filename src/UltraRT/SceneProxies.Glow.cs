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
    /// Plain proxy materials, and atlas slots of a fixture part (the baked level atlas is one material for the whole level,
    /// so such slots get a variant of their own named after the original: SceneProxies.AtlasVariant, 2026-11-21); not
    /// transparents, not the player. Per-renderer variants made before a rule change
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

        private void ApplyEmitterGlow(Material src, Material m, Vector2? glow = null)
        {
            var g = glow ?? (EmitterGlowOf != null ? EmitterGlowOf(src) : Vector2.zero);
            bool had = _glowProxies.Contains(m);
            // A material with emission of its own keeps it: its light is scaled by the rule's "scale" instead.
            if (g.x > 0f && (had || m.GetFloat("_EmissiveIntensity") <= 0f))
            {
                m.SetColor("_EmissiveColor", Color.white);
                m.SetFloat("_EmissiveIntensity", g.x);
                m.SetFloat("_UseAlbedoAsEmissive", 1f);
                m.SetFloat("_EmissiveMask", 0f);
                m.SetFloat(IdEmissiveThreshold, Mathf.Clamp(g.y, 0f, 2f)); // above 1: a chroma mask (Proxy.shader)
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

        /// <summary>Plain proxy materials are shared by every renderer of a source material, so an emitter rule with a glow
        /// and a path (the dome lamps' discs, "Dome/Inner") reaches a renderer through a copy of the proxy material of its
        /// own: one per (proxy material, glow), with the proxy's name (handover parts are found by it). Atlas slots get
        /// theirs in AtlasVariant.</summary>
        private void PathGlowVariants(Renderer source, Material[] srcMats, Material[] proxyMats)
        {
            if (EmitterGlowAt == null || source == null || srcMats == null) return;
            for (int i = 0; i < proxyMats.Length && i < srcMats.Length; i++)
            {
                var src = srcMats[i];
                var pm = proxyMats[i];
                if (src == null || pm == null || pm == _disabledMaterial || _atlasProxies.Contains(pm)) continue;
                Vector2 g;
                try { g = EmitterGlowAt(src, source); } catch { continue; }
                if (g.x <= 0f) continue;
                var key = (pm, g);
                if (!_pathGlowProxies.TryGetValue(key, out var v) || v == null)
                {
                    v = new Material(pm) { name = pm.name };
                    _pathGlowProxies[key] = v;
                    _pathGlowSources[v] = (src, source);
                    if (IsTwoSided(pm)) _twoSidedProxies.Add(v);
                    if (IsSkyWall(pm)) _skyWallProxies.Add(v);
                    _ownMaterials.Add(v); // destroyed with the scene (SceneProxies.Sync.cs)
                    ApplyEmitterGlow(src, v, g);
                    PathGlowProxies++;
                    _log.LogInfo($"Emitter glow by path: '{GameControlPath(source.transform)}' slot {i} '{src.name}' glows x{g.x:0.##} (own proxy material)");
                }
                proxyMats[i] = v;
            }
        }

        private readonly Dictionary<(Material, Vector2), Material> _pathGlowProxies = new Dictionary<(Material, Vector2), Material>();
        private readonly Dictionary<Material, (Material src, Renderer r)> _pathGlowSources = new Dictionary<Material, (Material, Renderer)>();
        public int PathGlowProxies;

        /// <summary>The rules changed: every proxy material again. The caller rebuilds the emissive triangle table.</summary>
        public void SyncEmitterGlow()
        {
            foreach (var kv in _materialCache)
                if (kv.Key != null && kv.Value != null && !_atlasProxies.Contains(kv.Value)) ApplyEmitterGlow(kv.Key, kv.Value);
            // Atlas slots of a fixture part (AtlasVariant): their variant follows the original material's rule.
            foreach (var kv in _atlasPartOriginals)
            {
                if (kv.Key == null || kv.Value == null) continue;
                // A variant made for a glow rule with a path follows that rule (its renderer's path), else the material's.
                Vector2? g = null;
                if (_atlasPartSources.TryGetValue(kv.Key, out var r) && r != null && EmitterGlowAt != null)
                {
                    var pg = EmitterGlowAt(kv.Value, r);
                    if (pg.x > 0f) g = pg;
                }
                ApplyEmitterGlow(kv.Value, kv.Key, g);
            }
            // Plain proxies' copies made for a glow rule with a path (PathGlowVariants): that rule again, at their renderer.
            foreach (var kv in _pathGlowSources)
            {
                if (kv.Key == null || kv.Value.src == null) continue;
                Vector2? g = null;
                if (kv.Value.r != null && EmitterGlowAt != null)
                {
                    var pg = EmitterGlowAt(kv.Value.src, kv.Value.r);
                    if (pg.x > 0f) g = pg;
                }
                ApplyEmitterGlow(kv.Value.src, kv.Key, g);
            }
        }
    }
}
