using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The game's rain on surfaces (docs/materials.md, "Rain"). Outdoor materials get the `RAIN` keyword at runtime
    /// (StaticSceneOptimizer.UpdateRain on the outdoor batch material, StencilValuesByLayer on reusable Master materials),
    /// and the Master / Stationary shaders then add animated raindrop ripples on up-facing surfaces and running streaks on
    /// walls to the albedo, and darken it by _RainDarkening. Relight replaces opaque pixels with albedo x ray-traced
    /// lighting from our proxy materials, so the ripples were gone. Each proxy material (and its atlas variants) mirrors its
    /// source's rain state and properties here; Proxy.shader's RainAlbedo reproduces the term (decoded from the game's
    /// pixel shaders) with the game's clock (_UltraRTRainTime = Time.timeSinceLevelLoad, the shaders' _Time.y).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        private sealed class RainTarget
        {
            public Material Source;
            public readonly List<Material> Proxies = new List<Material>(2);
            public bool On;
        }

        private readonly Dictionary<Material, RainTarget> _rainBySource = new Dictionary<Material, RainTarget>();
        private readonly Dictionary<Material, RainTarget> _rainByProxy = new Dictionary<Material, RainTarget>();
        private static readonly int RainId = Shader.PropertyToID("_Rain");
        private static readonly string[] RainFloats = { "_RainSpeed", "_RaindropsIntensity", "_RainScale", "_WarpDetail", "_LocalOffsetRain", "_RainCeilingToggle" };
        private static readonly string[] RainColors = { "_RainDarkening", "_RainColor" };
        private static readonly string[] RainTextures = { "_RainDrops", "_RainTrails" };

        /// <summary>Rain on proxy materials (-ultrart-norain turns it off).</summary>
        public static bool RainEnabled = true;
        public int RainyMaterials { get; private set; }

        /// <summary>Proxy(): remember sources that can rain (the property exists), set the current state.</summary>
        private void RegisterRain(Material src, Material proxy)
        {
            if (src == null || !src.HasProperty("_RainDrops")) return;
            var t = new RainTarget { Source = src };
            t.Proxies.Add(proxy);
            _rainBySource[src] = t;
            _rainByProxy[proxy] = t;
            Apply(t, src.IsKeywordEnabled("RAIN") && RainEnabled);
        }

        /// <summary>AtlasVariant(): a copy of an atlas proxy follows the same source.</summary>
        private void RegisterRainVariant(Material atlasProxy, Material variant)
        {
            if (!_rainByProxy.TryGetValue(atlasProxy, out var t)) return;
            t.Proxies.Add(variant);
            _rainByProxy[variant] = t;
            Apply(t, t.On, force: true);
        }

        /// <summary>A few times a second: the game turns rain on and off per area (rooms with a roof).</summary>
        public void SyncRain()
        {
            int n = 0;
            foreach (var t in _rainBySource.Values)
            {
                if (t.Source == null) continue;
                bool on = RainEnabled && t.Source.IsKeywordEnabled("RAIN");
                if (on != t.On) Apply(t, on);
                if (t.On) n++;
            }
            if (n != RainyMaterials)
                _log.LogInfo($"Rain: {n} of {_rainBySource.Count} rain-capable proxy sources raining now");
            RainyMaterials = n;
        }

        private static void Apply(RainTarget t, bool on, bool force = false)
        {
            if (on == t.On && !force && t.Proxies.Count > 0 && t.Proxies[0].GetFloat(RainId) == (on ? 1f : 0f)) return;
            t.On = on;
            var s = t.Source;
            foreach (var m in t.Proxies)
            {
                if (m == null) continue;
                m.SetFloat(RainId, on ? 1f : 0f);
                if (!on) continue;
                foreach (var p in RainFloats) if (s.HasProperty(p)) m.SetFloat(p, s.GetFloat(p));
                foreach (var p in RainColors) if (s.HasProperty(p)) m.SetColor(p, s.GetColor(p));
                foreach (var p in RainTextures) if (s.HasProperty(p) && s.GetTexture(p) is Texture tex) m.SetTexture(p, tex);
            }
        }
    }
}
