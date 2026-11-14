using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Diagnostic (-ultrart-particlesurvey; docs/particles.md "Survey"): which effect renderers the game draws in its
    /// transparent / particle passes while playing, with their shader, keywords, blend state, queue and layer. Every
    /// 0.5 s it walks the visible particle / sprite / line / trail renderers and the visible meshes with a transparent or
    /// particle-like material; every 10 s it logs the totals (frames seen, most at once, an example object). Also logs
    /// the blood and oil stain materials once. Costs a millisecond or more per census: never measure with it on.
    /// </summary>
    internal sealed class ParticleSurvey
    {
        private readonly ManualLogSource _log;
        private float _nextCensus, _nextTotals;
        private bool _bloodLogged;

        private sealed class Agg { public int Frames, Max, Now; public string Example; public float Dist = float.MaxValue; }
        private readonly Dictionary<string, Agg> _totals = new Dictionary<string, Agg>();

        public ParticleSurvey(ManualLogSource log) { _log = log; }

        private static string F(Material m, string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.##") : "-";
        private static string C(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";

        public static string Describe(Material m)
        {
            if (m == null) return "null material";
            var sb = new StringBuilder();
            sb.Append($"'{m.shader?.name}' mat '{m.name}' q {m.renderQueue}");
            sb.Append($" blend {F(m, "_SrcBlend")}/{F(m, "_DstBlend")} bm {F(m, "_BlendMode")} mode {F(m, "_Mode")} zw {F(m, "_ZWrite")}");
            if (m.HasProperty("_Color")) sb.Append($" col {C(m.GetColor("_Color"))}");
            if (m.HasProperty("_TintColor")) sb.Append($" tint {C(m.GetColor("_TintColor"))}");
            if (m.HasProperty("_EmissionColor")) sb.Append($" emis {C(m.GetColor("_EmissionColor"))}");
            if (m.HasProperty("_EmissiveIntensity")) sb.Append($" emI {F(m, "_EmissiveIntensity")}");
            var kw = m.shaderKeywords;
            if (kw.Length > 0) sb.Append($" kw [{string.Join(" ", kw)}]");
            var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (tex != null) sb.Append($" tex '{tex.name}' {tex.width}x{tex.height}");
            return sb.ToString();
        }

        private static string Root(Transform t)
        {
            string n = EffectSurvey.BaseName(t.name);
            var p = t.parent;
            for (int i = 0; i < 2 && p != null; i++, p = p.parent) n = EffectSurvey.BaseName(p.name) + "/" + n;
            return n;
        }

        public void Update(Camera cam)
        {
            if (cam == null) return;
            float now = Time.unscaledTime;
            if (!_bloodLogged) LogBlood();
            if (now >= _nextCensus)
            {
                _nextCensus = now + 0.5f;
                foreach (var a in _totals.Values) a.Now = 0;
                var camPos = cam.transform.position;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !r.isVisible) continue;
                    bool fx = r is ParticleSystemRenderer || r is SpriteRenderer || r is LineRenderer || r is TrailRenderer;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || m.shader == null) continue;
                        string s = m.shader.name;
                        bool transp = m.renderQueue >= 2501 || s.Contains("Particle") || s.Contains("Sprite") || s.Contains("Additive");
                        if (!fx && !transp) continue;
                        string extra = "";
                        if (r is ParticleSystemRenderer pr)
                        {
                            if (i == 1 && pr.trailMaterial == m) extra = " trail";
                            else extra = $" {pr.renderMode}";
                        }
                        string key = $"{r.GetType().Name}{extra} L{r.gameObject.layer} {Describe(m)}";
                        if (!_totals.TryGetValue(key, out var a)) _totals[key] = a = new Agg();
                        a.Now++;
                        float d = Vector3.Distance(camPos, r.bounds.center);
                        if (a.Example == null || d < a.Dist) { a.Example = $"'{Root(r.transform)}' at {d:F1} m"; a.Dist = d; }
                    }
                }
                foreach (var a in _totals.Values)
                {
                    if (a.Now > 0) a.Frames++;
                    a.Max = Math.Max(a.Max, a.Now);
                }
            }
            if (now >= _nextTotals)
            {
                _nextTotals = now + 10f;
                var sb = new StringBuilder();
                sb.Append($"particle survey totals ({_totals.Count} kinds, scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'):");
                foreach (var kv in _totals.OrderByDescending(k => k.Value.Frames * 4 + k.Value.Max).Take(120))
                    sb.Append($"\n  censuses {kv.Value.Frames} max {kv.Value.Max} | {kv.Key} | e.g. {kv.Value.Example}");
                _log.LogInfo(sb.ToString());
            }
        }

        private void LogBlood()
        {
            var bm = MonoSingleton<BloodsplatterManager>.Instance;
            if (bm == null) return;
            _bloodLogged = true;
            _log.LogInfo($"particle survey: blood stains {Describe(bm.stainMat)}; composite {Describe(bm.bloodCompositeMaterial)}; compute {bm.usedComputeShadersAtStart}");
            var sv = MonoSingleton<StainVoxelManager>.Instance;
            if (sv != null) _log.LogInfo($"particle survey: oil stains {Describe(sv.gasStainMat)}; composite {Describe(sv.gasolineCompositeMaterial)}");
        }
    }
}
