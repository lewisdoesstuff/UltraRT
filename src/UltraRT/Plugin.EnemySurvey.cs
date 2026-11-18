using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// -ultrart-enemy-survey (bench): enemies_&lt;scene&gt;.txt in the run's folder, one block per enemy type in the level
    /// (active or not): its renderers with their materials (shader, keywords, emission), and the lights under it.
    /// The groundwork for the enemy glow lights (docs/emissive-lights.md, "Enemy glows").
    /// </summary>
    public partial class Plugin
    {
        private bool _enemySurvey; // -ultrart-enemy-survey

        private void WriteEnemySurvey(string outDir)
        {
            if (!_enemySurvey || string.IsNullOrEmpty(outDir)) return;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var byType = new SortedDictionary<string, List<EnemyIdentifier>>(StringComparer.Ordinal);
                foreach (var e in UnityEngine.Object.FindObjectsByType<EnemyIdentifier>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    string t = e.enemyType.ToString();
                    if (!byType.TryGetValue(t, out var list)) byType[t] = list = new List<EnemyIdentifier>();
                    list.Add(e);
                }
                var sb = new StringBuilder();
                foreach (var kv in byType)
                {
                    var e = kv.Value[0];
                    sb.Append($"== {kv.Key} x{kv.Value.Count} (first: '{e.name}')\n");
                    foreach (var r in e.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                        Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                        sb.Append($"  {r.GetType().Name} '{r.name}' L{r.gameObject.layer} mesh {(mesh != null ? $"'{mesh.name}' {mesh.vertexCount}v readable={mesh.isReadable}" : "-")}" +
                                  $"{(r is SkinnedMeshRenderer s2 ? $" bones {s2.bones.Length}" : "")} size {r.bounds.size.magnitude.ToString("F1", inv)}\n");
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            string em = "";
                            if (m.HasProperty("_EmissiveColor") && m.HasProperty("_EmissiveIntensity"))
                            {
                                var c = m.GetColor("_EmissiveColor");
                                em = $" emis ({c.r.ToString("F2", inv)},{c.g.ToString("F2", inv)},{c.b.ToString("F2", inv)}) x{m.GetFloat("_EmissiveIntensity").ToString("F2", inv)}" +
                                     $" tex {(m.HasProperty("_EmissiveTex") && m.GetTexture("_EmissiveTex") != null ? m.GetTexture("_EmissiveTex").name : "-")}";
                            }
                            string main = m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null ? m.GetTexture("_MainTex").name : "-";
                            sb.Append($"    mat '{m.name}' {m.shader?.name} q{m.renderQueue} main {main}{em} kw [{string.Join(" ", m.shaderKeywords.Where(k => !k.StartsWith("_GLITCH") && !k.StartsWith("_RAINCEIL")))}]\n");
                        }
                    }
                    foreach (var l in e.GetComponentsInChildren<Light>(true))
                    {
                        var c = l.color;
                        sb.Append($"  light '{GameControlPathBelow(l.transform, e.transform)}' {l.type} ({c.r.ToString("F2", inv)},{c.g.ToString("F2", inv)},{c.b.ToString("F2", inv)}) x{l.intensity.ToString("F2", inv)} range {l.range.ToString("F1", inv)}" +
                                  $" enabled={l.enabled} active={l.gameObject.activeSelf}\n");
                    }
                }
                string file = Path.Combine(outDir, $"enemies_{SafeCurrentScene()}.txt");
                File.WriteAllText(file, sb.ToString());
                Logger.LogInfo($"Enemy survey: {byType.Count} enemy types of '{SafeCurrentScene()}' -> {file}");
            }
            catch (Exception ex) { Logger.LogWarning($"Enemy survey failed: {ex.Message}"); }
        }

        private static string GameControlPathBelow(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
