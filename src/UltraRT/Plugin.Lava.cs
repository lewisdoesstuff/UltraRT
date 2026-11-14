using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Lava wiring (docs/lava.md). Plugin.cs only has one-line hooks: ParseLavaArgs (Awake), LoadLavaShader (LoadShaders),
    /// SceneProxies.UpdateLava (Trace, after SyncDynamic) and DimLavaLights (GatherLights, before the effect lights).
    ///   -ultrart-nolava                 no lava proxies (the game's lava stays a raster-only overlay)
    ///   -ultrart-lava-light X           lava emission for direct lighting (default 1, on top of EmissiveLightScale)
    ///   -ultrart-lava-glow X            lava emission seen by reflection rays (default 1)
    ///   -ultrart-lava-pointlights X     brightness of the game's point lights over lava (default 0.4; 1 = unchanged)
    ///   -ultrart-lava-camera X          replace modes: camera rays see this much of the lava's emission (default 0.8, 0 = pass through)
    ///   -ultrart-lava-loglights         log every light near a lava surface with why it is or isn't dimmed
    /// </summary>
    public partial class Plugin
    {
        private LavaSettings LavaCfg => SceneProxies.Lava;
        private readonly HashSet<Light> _lavaLights = new HashSet<Light>();
        private readonly List<(Bounds bounds, Color color)> _lavaZones = new List<(Bounds, Color)>();
        private SceneProxies _lavaLightsOwner;
        private int _lavaLightsVersion = -1, _lavaLightsCount = -1, _lavaLightLogs;
        private bool _lavaLogLights;
        private static readonly int LavaCameraId = Shader.PropertyToID("_UltraRTLavaCamera");

        private void ParseLavaArgs()
        {
            var a = Environment.GetCommandLineArgs();
            var s = LavaCfg;
            for (int i = 0; i < a.Length; i++)
            {
                float F(float fallback) => i + 1 < a.Length && float.TryParse(a[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-nolava": s.enabled = false; break;
                    case "-ultrart-lava-light": s.lightScale = F(s.lightScale); break;
                    case "-ultrart-lava-glow": s.glow = F(s.glow); break;
                    case "-ultrart-lava-pointlights": s.pointLightScale = F(s.pointLightScale); break;
                    case "-ultrart-lava-camera": s.cameraGlow = F(s.cameraGlow); break;
                    case "-ultrart-lava-loglights": _lavaLogLights = true; break;
                }
            }
        }

        private void LoadLavaShader(AssetBundle bundle)
        {
            var lava = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/LavaProxy");
            if (lava == null) Logger.LogWarning("LavaProxy shader missing from bundle: lava is not ray traced");
            else if (!lava.isSupported) Logger.LogWarning("LavaProxy shader unsupported: lava is not ray traced");
            SceneProxies.LavaShader = lava != null && lava.isSupported ? lava : null;
        }

        /// <summary>
        /// ULTRAKILL fakes the glow of lava with orange point lights along it. Now that the lava surface itself is an emitter,
        /// those lights would count the glow twice and flood the lava channel's floor (seen through the additive lava) with
        /// flat light, so they are dimmed to LavaSettings.pointLightScale. A light counts when it sits over an active lava
        /// surface (its bounds widened by 2 m, up to 4 m above) and its colour matches the lava's. Packed lights only: the
        /// game's own rendering is unchanged.
        /// </summary>
        private void DimLavaLights()
        {
            // What camera rays see at lava (LavaProxy.shader): its emission in the replace modes, the game's floor in hybrid.
            bool replace = Modes[_mode].kind != Kind.Hybrid;
            Shader.SetGlobalFloat(LavaCameraId, replace ? Mathf.Max(0f, LavaCfg.cameraGlow) : 0f);
            var px = _proxies;
            float scale = LavaCfg.pointLightScale;
            if (px == null || px.LavaCount == 0 || Mathf.Approximately(scale, 1f)) return;
            if (px != _lavaLightsOwner || px.LavaVersion != _lavaLightsVersion || px.Lights.Count != _lavaLightsCount)
            {
                _lavaLightsOwner = px;
                _lavaLightsVersion = px.LavaVersion;
                _lavaLightsCount = px.Lights.Count;
                _lavaLights.Clear();
                px.ActiveLava(_lavaZones);
                foreach (var l in px.Lights)
                {
                    if (l == null || l.type == LightType.Directional) continue;
                    var p = l.transform.position;
                    var lc = l.color;
                    foreach (var (b, c) in _lavaZones)
                    {
                        bool near = b.SqrDistance(p) < 144f;
                        bool over = !(p.x < b.min.x - 2f || p.x > b.max.x + 2f || p.z < b.min.z - 2f || p.z > b.max.z + 2f)
                                    && p.y >= b.min.y - 1f && p.y <= b.max.y + 6f;
                        float chroma = Chroma(lc, c);
                        if (_lavaLogLights && near)
                            Logger.LogInfo($"Lava light check: {Bench.GameControl.PathOf(l.transform)} at {p:F1} colour {lc} I {l.intensity:F2} r {l.range:F1} " +
                                           $"on {l.isActiveAndEnabled}; lava {b.min:F1}..{b.max:F1} colour {c}: over {over} chroma {chroma:F2}");
                        if (!over || chroma < 0.9f) continue;
                        _lavaLights.Add(l);
                        break;
                    }
                }
                if (_lavaLightLogs++ < 20)
                    Logger.LogInfo($"Lava lights: {_lavaLights.Count} point lights over {_lavaZones.Count} active lava surfaces dimmed to x{scale:F2}" +
                                   (_lavaLights.Count > 0 ? ": " + string.Join(", ", _lavaLights.Take(12).Select(l => $"{l.name} ({l.intensity:F1}, r {l.range:F0})")) : ""));
            }
            if (_lavaLights.Count == 0) return;
            for (int k = 0; k < _lightCount; k++)
            {
                var l = _packedLights[k];
                if (l == null || !_lavaLights.Contains(l)) continue;
                _lightData[k * 4 + 1] *= scale;
            }
        }

        /// <summary>Cosine between two colours as rgb vectors (1 = same hue and saturation).</summary>
        private static float Chroma(Color a, Color b)
        {
            var va = new Vector3(a.r, a.g, a.b);
            var vb = new Vector3(b.r, b.g, b.b);
            float d = va.magnitude * vb.magnitude;
            return d > 1e-6f ? Vector3.Dot(va, vb) / d : 0f;
        }
    }
}
