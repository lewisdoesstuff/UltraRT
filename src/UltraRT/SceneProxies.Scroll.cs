using System.Collections.Generic;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// ULTRAKILL/Master SCROLLING on traced (opaque) surfaces (docs/scene-proxies.md, "Scrolling textures"). The game
    /// samples its textures at uv * _MainTex_ST + _ScrollOffset + _ScrollSpeed * _Time.y; ScrollingTexture components set
    /// _ScrollOffset per renderer in a property block. Proxy materials used to copy _MainTex_ST only, so in relight and
    /// pathtrace (which replace the game's opaque pixels with RT albedo x lighting) a flowing surface stood still and only
    /// its vertex displacement moved: 5-2's sea read as gentle waves instead of the game's rushing current. Proxy.shader
    /// adds the same offset (clock _UltraRTRainTime = Time.timeSinceLevelLoad); the global _UltraRTTexScroll turns it off.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        private static readonly int IdScrolling = Shader.PropertyToID("_Scrolling");
        private static readonly int IdScrollSpeed = Shader.PropertyToID("_ScrollSpeed");
        private static readonly int IdScrollOffset = Shader.PropertyToID("_ScrollOffset");
        private static readonly int IdScrollVelocity = Shader.PropertyToID("_ScrollVelocity");
        private readonly List<string> _scrollLog = new List<string>();
        public int ScrollingMaterials { get; private set; }

        private void ApplyScrolling(Material src, Material proxy)
        {
            if (src == null || !src.IsKeywordEnabled("SCROLLING") || !src.HasProperty(IdScrollSpeed)) return;
            var speed = src.GetVector(IdScrollSpeed);
            var offset = src.HasProperty(IdScrollOffset) ? src.GetVector(IdScrollOffset) : Vector4.zero;
            proxy.SetFloat(IdScrolling, 1f);
            proxy.SetVector(IdScrollSpeed, speed);
            proxy.SetVector(IdScrollOffset, offset);
            proxy.SetVector(IdScrollVelocity, speed);
            ScrollingMaterials++;
            if (_scrollLog.Count < 16)
                _scrollLog.Add($"'{src.name}' speed ({speed.x:G3}, {speed.y:G3}) offset ({offset.x:G3}, {offset.y:G3}) tex {(src.mainTexture != null ? src.mainTexture.name : "-")} ST {src.mainTextureScale} kw [{string.Join(" ", src.shaderKeywords)}]");
        }

        /// <summary>A scrolling proxy's texture velocity (uv/s): the material's _ScrollSpeed plus the renderer's ScrollingTexture.</summary>
        private static Vector4 ScrollVelocity(SyncState sync, Renderer source, Material shared)
        {
            if (!sync.ScrollLooked) { sync.ScrollLooked = true; sync.ScrollTex = source != null ? source.GetComponent<ScrollingTexture>() : null; }
            var v = shared.GetVector(IdScrollSpeed);
            if (sync.ScrollTex != null) { v.x += sync.ScrollTex.scrollSpeedX; v.y += sync.ScrollTex.scrollSpeedY; }
            return v;
        }

        private static bool ProxyScrolls(Material m) => m != null && m.HasProperty(IdScrolling) && m.GetFloat(IdScrolling) > 0.5f;

        /// <summary>Diagnostics (every 600 frames): each scrolling renderer's ScrollingTexture speed and _ScrollOffset, game vs proxy.</summary>
        public string DescribeScrolling()
        {
            var parts = new List<string>();
            int n = 0;
            var block = new MaterialPropertyBlock();
            foreach (var d in _active)
            {
                if (d.Source == null || d.Proxy == null || !(d.Source is MeshRenderer mr)) continue;
                var st = mr.GetComponent<ScrollingTexture>();
                if (st == null) continue;
                n++;
                if (parts.Count >= 6) continue;
                mr.GetPropertyBlock(block);
                var game = block.isEmpty ? Vector4.zero : block.GetVector(IdScrollOffset);
                var pm = d.Proxy.sharedMaterial;
                var rt = pm != null && pm.HasProperty(IdScrollOffset) ? pm.GetVector(IdScrollOffset) : Vector4.zero;
                var vel = pm != null && pm.HasProperty(IdScrollVelocity) ? pm.GetVector(IdScrollVelocity) : Vector4.zero;
                parts.Add($"'{mr.name}' speed ({st.scrollSpeedX:G3}, {st.scrollSpeedY:G3}) offset game ({game.x:F3}, {game.y:F3}) rt ({rt.x:F3}, {rt.y:F3}) velocity rt ({vel.x:G3}, {vel.y:G3}) proxy scrolls {ProxyScrolls(pm)} hot {d.Sync?.Hot}");
            }
            return n == 0 ? null : $"Scrolling textures: {n} active ScrollingTexture renderers: {string.Join("; ", parts)}";
        }

        private void LogScrolling()
        {
            if (ScrollingMaterials == 0) return;
            _log.LogInfo($"Scrolling textures: {ScrollingMaterials} traced SCROLLING materials: {string.Join("; ", _scrollLog)}");
            _scrollLog.Clear();
        }
    }
}
