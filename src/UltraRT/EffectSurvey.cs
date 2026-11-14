using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Diagnostic (-ultrart-effectsurvey): which glowing effects exist in play, which come with a Light, and how their
    /// lights behave. Logs:
    ///  - "effect prefab": the first spawned instance of every prefab (Lights, renderers with shader / blend / colours,
    ///    particle systems), from the Instantiate hook;
    ///  - "effect census" every second: active Lights, live particle systems, line / trail / sprite renderers and
    ///    transparent meshes, split by whether their hierarchy has an active Light;
    ///  - "effect totals" every 10 s: the most common effect renderers, lights whose intensity varies, and how long spawned
    ///    lights live (the light list is rescanned once a second).
    /// Costs milliseconds per census (FindObjectsOfType); never enable it for measurements.
    /// </summary>
    internal sealed class EffectSurvey
    {
        private readonly ManualLogSource _log;
        private readonly HashSet<string> _described = new HashSet<string>();
        private float _nextCensus, _nextTotals;

        private sealed class Agg { public int Seen, Max; public string Example; }
        private readonly Dictionary<string, Agg> _totals = new Dictionary<string, Agg>();
        private readonly Dictionary<Light, Vector2> _intensityRange = new Dictionary<Light, Vector2>();

        private struct Tracked { public Light Light; public float Spawned; public bool WasOn; public float OnSince; }
        private readonly List<Tracked> _spawnedLights = new List<Tracked>();
        private readonly int[] _lifeHist = new int[7]; // <0.1 <0.25 <0.5 <1 <2 >=2 never-on
        private readonly Dictionary<string, List<float>> _lifeByName = new Dictionary<string, List<float>>();

        public EffectSurvey(ManualLogSource log) { _log = log; }

        public static string BaseName(string n)
        {
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        /// <summary>A freshly instantiated root (from <see cref="EffectSpawns"/>).</summary>
        public void OnSpawned(GameObject go)
        {
            if (go == null) return;
            foreach (var l in go.GetComponentsInChildren<Light>(true))
                _spawnedLights.Add(new Tracked { Light = l, Spawned = Time.time });
            string name = BaseName(go.name);
            if (_described.Count > 600 || !_described.Add(name)) return;
            var lights = go.GetComponentsInChildren<Light>(true);
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (lights.Length == 0 && !rends.Any(r => !(r is MeshRenderer || r is SkinnedMeshRenderer) || !Opaque(r.sharedMaterial))) return; // plain props / gore meshes
            var sb = new StringBuilder();
            sb.Append($"effect prefab '{name}' at {go.transform.position}: {lights.Length} lights");
            foreach (var l in lights)
                sb.Append($" [{l.name} {l.type} col {Col(l.color)} I {l.intensity:F2} r {l.range:F1} on {l.isActiveAndEnabled} comps {string.Join("+", l.GetComponents<Component>().Select(c => c.GetType().Name).Where(n => n != "Transform" && n != "Light"))}]");
            foreach (var r in rends.Take(12))
            {
                sb.Append($" | {r.GetType().Name} '{r.name}' on {r.enabled && r.gameObject.activeInHierarchy}");
                var m = r.sharedMaterial;
                if (m != null) sb.Append($" {Describe(m)}");
                if (r is ParticleSystemRenderer)
                {
                    var ps = r.GetComponent<ParticleSystem>();
                    if (ps != null)
                    {
                        var main = ps.main;
                        sb.Append($" ps start {Col(main.startColor.color)}/{main.startColor.mode} size {main.startSize.constant:F2}/{main.startSize.constantMax:F2} life {main.startLifetime.constant:F2}/{main.startLifetime.constantMax:F2} max {main.maxParticles} loop {main.loop} dur {main.duration:F1} col-over-life {ps.colorOverLifetime.enabled}");
                    }
                }
            }
            if (rends.Length > 12) sb.Append($" | ... {rends.Length - 12} more renderers");
            _log.LogInfo(sb.ToString());
        }

        public void Update(Camera cam)
        {
            float now = Time.time;
            for (int i = _spawnedLights.Count - 1; i >= 0; i--)
            {
                var t = _spawnedLights[i];
                bool alive = t.Light != null;
                bool on = alive && t.Light.isActiveAndEnabled && t.Light.intensity > 0f;
                if (on && !t.WasOn) { t.WasOn = true; t.OnSince = now; _spawnedLights[i] = t; }
                if (on) continue;
                if (alive && !t.WasOn && now - t.Spawned < 5f) continue; // may still switch on
                float life = t.WasOn ? now - t.OnSince : -1f;
                int b = !t.WasOn ? 6 : life < 0.1f ? 0 : life < 0.25f ? 1 : life < 0.5f ? 2 : life < 1f ? 3 : life < 2f ? 4 : 5;
                _lifeHist[b]++;
                string key = alive ? BaseName(t.Light.transform.root.name) + "/" + t.Light.name : "(destroyed)";
                if (!_lifeByName.TryGetValue(key, out var lst)) _lifeByName[key] = lst = new List<float>();
                if (lst.Count < 64) lst.Add(life);
                _spawnedLights.RemoveAt(i);
            }

            if (now < _nextCensus) return;
            _nextCensus = now + 1f;
            Census(cam);
            if (now >= _nextTotals)
            {
                _nextTotals = now + 10f;
                LogTotals();
            }
        }

        private void Census(Camera cam)
        {
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            var lightsByRoot = new Dictionary<Transform, int>();
            int lights = 0, near = 0, varying = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (!l.isActiveAndEnabled || l.intensity <= 0f || l.type == LightType.Directional) continue;
                lights++;
                if ((l.transform.position - camPos).sqrMagnitude < 900f) near++;
                var root = l.transform.root;
                lightsByRoot[root] = lightsByRoot.TryGetValue(root, out var n) ? n + 1 : 1;
                var range = _intensityRange.TryGetValue(l, out var rr) ? new Vector2(Mathf.Min(rr.x, l.intensity), Mathf.Max(rr.y, l.intensity)) : new Vector2(l.intensity, l.intensity);
                _intensityRange[l] = range;
                if (range.y > range.x * 1.05f + 1e-3f) varying++;
            }

            var counts = new Dictionary<string, int>();
            int particles = 0, particlesLit = 0, lines = 0, trails = 0, sprites = 0, meshes = 0, unlitNear = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || r.name.EndsWith("[UltraRT]")) continue;
                string cat;
                if (r is ParticleSystemRenderer)
                {
                    var ps = r.GetComponent<ParticleSystem>();
                    if (ps == null || ps.particleCount == 0) continue;
                    cat = "particles";
                    particles++;
                }
                else if (r is LineRenderer) { cat = "line"; lines++; }
                else if (r is TrailRenderer) { cat = "trail"; trails++; }
                else if (r is SpriteRenderer) { cat = "sprite"; sprites++; }
                else if ((r is MeshRenderer || r is SkinnedMeshRenderer) && !Opaque(r.sharedMaterial)) { cat = "transparent-mesh"; meshes++; }
                else continue;
                var m = r.sharedMaterial;
                string blend = m != null ? BlendOf(m) : "-";
                bool lit = HasLight(r.transform, lightsByRoot);
                if (cat == "particles" && lit) particlesLit++;
                bool isNear = r.bounds.SqrDistance(camPos) < 900f;
                if (!lit && isNear && blend == "add") unlitNear++;
                string key = $"{cat} {(lit ? "LIT" : "unlit")} {blend} '{BaseName(r.transform.root.name)}/{BaseName(r.name)}' {(m != null ? m.shader.name + " / " + m.name : "-")}";
                counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                if (!_totals.TryGetValue(key, out var a)) _totals[key] = a = new Agg { Example = $"at {r.bounds.center} size {r.bounds.size.magnitude:F1} {(m != null ? Describe(m) : "")}" };
            }
            foreach (var kv in counts)
            {
                var a = _totals[kv.Key];
                a.Seen++;
                a.Max = Math.Max(a.Max, kv.Value);
            }
            _log.LogInfo($"effect census t={Time.time:F1}: lights {lights} ({near} within 30 m, {varying} with varying intensity, {lightsByRoot.Count} roots); " +
                         $"live particle systems {particles} ({particlesLit} with a light in their hierarchy), lines {lines}, trails {trails}, sprites {sprites}, transparent meshes {meshes}; " +
                         $"unlit additive within 30 m: {unlitNear}; spawned lights tracked {_spawnedLights.Count}, spawns dropped {EffectSpawns.Dropped}");
        }

        private static bool HasLight(Transform t, Dictionary<Transform, int> lightsByRoot) => lightsByRoot.ContainsKey(t.root);

        private void LogTotals()
        {
            _log.LogInfo($"effect totals: spawned light lifetimes <0.1s {_lifeHist[0]}, <0.25s {_lifeHist[1]}, <0.5s {_lifeHist[2]}, <1s {_lifeHist[3]}, <2s {_lifeHist[4]}, >=2s {_lifeHist[5]}, never on {_lifeHist[6]}");
            foreach (var kv in _lifeByName.OrderByDescending(k => k.Value.Count).Take(20))
                _log.LogInfo($"  spawned light '{kv.Key}': {kv.Value.Count} lives, median {Median(kv.Value):F2} s");
            foreach (var kv in _totals.OrderByDescending(k => k.Value.Seen * 100 + k.Value.Max).Take(45))
                _log.LogInfo($"  effect renderer x{kv.Value.Max} (in {kv.Value.Seen} censuses): {kv.Key} | {kv.Value.Example}");
            foreach (var kv in _intensityRange.Where(k => k.Key != null && k.Value.y > k.Value.x * 1.05f + 1e-3f).Take(25))
                _log.LogInfo($"  varying light '{Bench.GameControl.PathOf(kv.Key.transform)}' I {kv.Value.x:F2}..{kv.Value.y:F2} comps {string.Join("+", kv.Key.GetComponents<Component>().Select(c => c.GetType().Name).Where(n => n != "Transform" && n != "Light"))}");
        }

        private static float Median(List<float> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }

        public static bool Opaque(Material m)
        {
            if (m == null || m.shader == null) return true;
            if (m.renderQueue >= 3000) return false;
            string s = m.shader.name;
            return !(s.Contains("Particles") || s.Contains("Additive") || s.Contains("Sprite"));
        }

        public static string BlendOf(Material m)
        {
            if (m.HasProperty("_DstBlend"))
            {
                int dst = (int)m.GetFloat("_DstBlend");
                if (dst == 1) return "add";
                if (dst == 10) return "alpha";
                if (dst == 0) return "opaque";
                return "dst" + dst;
            }
            string s = m.shader.name;
            if (s.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0) return "add";
            if (s.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Blend", StringComparison.OrdinalIgnoreCase) >= 0) return "alpha";
            return "?";
        }

        private static string Describe(Material m)
        {
            var sb = new StringBuilder();
            sb.Append($"mat '{m.name}' shader '{m.shader.name}' q{m.renderQueue} blend {BlendOf(m)}");
            foreach (var p in new[] { "_TintColor", "_Color", "_EmissiveColor", "_EmissionColor" })
                if (m.HasProperty(p)) sb.Append($" {p} {Col(m.GetColor(p))}");
            foreach (var p in new[] { "_EmissiveIntensity", "_Intensity", "_SrcBlend", "_DstBlend" })
                if (m.HasProperty(p)) sb.Append($" {p} {m.GetFloat(p):F2}");
            if (m.mainTexture != null) sb.Append($" tex '{m.mainTexture.name}'");
            sb.Append($" kw [{string.Join(" ", m.shaderKeywords)}]");
            return sb.ToString();
        }

        private static string Col(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";
    }
}
