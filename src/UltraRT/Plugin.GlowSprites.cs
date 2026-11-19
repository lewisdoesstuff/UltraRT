using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Smooth glow sprites (docs/vfx.md, "Glow sprites"). The game hangs a large soft glow sprite on some of its lights
    /// (1-4's street lamps: 'Streetlight/Point Light/Square', a SpriteRenderer about 20 m across with the texture 'glow').
    /// Like every texture in ULTRAKILL it is point filtered, so its low-resolution radial gradient shows as a grid of flat
    /// cells round the lamp, which turn with the sprite as the camera moves. The game's own frame has them too; the
    /// in-scattering of the volumetric pass behind them made them stand out. While frames are traced, the textures of such
    /// sprites are switched to bilinear filtering and restored afterwards (the game's own look in "off").
    /// -ultrart-glow-smooth on|off; bench A/B: plugin._smoothGlow=0.
    /// </summary>
    public partial class Plugin
    {
        private bool _smoothGlow = true;
        private readonly Dictionary<Texture, FilterMode> _glowTexOriginal = new Dictionary<Texture, FilterMode>();
        private readonly HashSet<Texture> _glowTextures = new HashSet<Texture>();
        private float _glowNextScan;
        private int _glowSpriteScene = -1;
        private string _glowSpriteLogged;

        /// <summary>A glow sprite: its texture's name has "glow" in it (any case). Other sprites (flames, muzzle flashes, pixel
        /// art) keep the game's point filtering.</summary>
        private static bool IsGlowSprite(Texture tex) =>
            tex.name.IndexOf("glow", System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Every frame, from UpdateParticles (OnUpdate): rtTraced = frames are being traced.</summary>
        private void UpdateGlowSprites(bool rtTraced)
        {
            bool want = rtTraced && _smoothGlow;
            if (!want)
            {
                if (_glowTexOriginal.Count > 0) RestoreGlowSprites();
                return;
            }
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != _glowSpriteScene || Time.realtimeSinceStartup >= _glowNextScan)
            {
                _glowSpriteScene = scene;
                _glowNextScan = Time.realtimeSinceStartup + 5f;
                ScanGlowSprites();
            }
            foreach (var tex in _glowTextures)
            {
                if (tex == null || tex.filterMode == FilterMode.Bilinear) continue;
                if (!_glowTexOriginal.ContainsKey(tex)) _glowTexOriginal[tex] = tex.filterMode;
                tex.filterMode = FilterMode.Bilinear;
            }
        }

        private void ScanGlowSprites()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var census = new Dictionary<Texture, (int count, bool glow, string example)>();
            foreach (var sr in Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var tex = sr.sprite != null ? sr.sprite.texture : null;
                if (tex == null) continue;
                bool glow = IsGlowSprite(tex);
                if (glow) _glowTextures.Add(tex);
                census.TryGetValue(tex, out var c);
                census[tex] = (c.count + 1, glow, c.example ?? GameControlPathOf(sr.transform));
            }
            _glowTextures.RemoveWhere(t => t == null);
            // Log the sprite textures once per distinct set (which ones count as glows, how many renderers use each).
            string line = string.Join(", ", census.OrderByDescending(kv => kv.Value.count).Take(24).Select(kv =>
                $"{kv.Key.name} {kv.Key.width}x{kv.Key.height} {(_glowTexOriginal.TryGetValue(kv.Key, out var f) ? f : kv.Key.filterMode)}{(kv.Value.glow ? " GLOW" : "")} x{kv.Value.count} ('{kv.Value.example}')"));
            if (line != _glowSpriteLogged)
            {
                _glowSpriteLogged = line;
                Logger.LogInfo($"Glow sprites ({sw.Elapsed.TotalMilliseconds:F2} ms scan): {_glowTextures.Count} texture(s) smoothed; sprite textures in the scene: {(line.Length > 0 ? line : "none")}");
            }
        }

        private void RestoreGlowSprites()
        {
            foreach (var kv in _glowTexOriginal)
                if (kv.Key != null) kv.Key.filterMode = kv.Value;
            _glowTexOriginal.Clear();
        }

        private static string GameControlPathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
