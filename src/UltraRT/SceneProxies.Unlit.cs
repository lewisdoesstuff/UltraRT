using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Fullbright vs relit (docs/materials.md, "Lit weight"). The game draws ULTRAKILL/Master without VERTEX_LIGHTING (and
    /// the psx/unlit shaders) without any lighting; in RT their proxies get a lit weight (0 = fullbright, 1 = fully relit):
    /// <c>litWeight = 1 - _Unlit</c> on dynamic and static-batched proxies, <c>lerp(_LitOverride, 1, vertex alpha)</c> on
    /// StaticSceneOptimizer atlas slots (whose vertex alpha is the game's own lit weight). The weight comes from, in order:
    ///  1. a "lit" rule in ultrart-materials.json (by material, texture or shader name),
    ///  2. for unlit ENEMY materials, the enemy type's weight (<see cref="UnlitSettings.enemyTypes"/>) or the global
    ///     <see cref="UnlitSettings.enemy"/> (0.5),
    ///  3. unlit Master: 0 (fullbright); everything else: 1.
    /// <see cref="UnlitSettings.fullbright"/> off relights everything (the old behaviour, -ultrart-lit-unlit).
    /// The settings can change at runtime (bench variants "unlit.enemy=0.8", the settings menu): every proxy material made
    /// so far is re-weighted the next frame.
    /// </summary>
    internal sealed class UnlitSettings
    {
        /// <summary>Unlit materials stay fullbright as in the game (false: everything is relit, -ultrart-lit-unlit).</summary>
        public bool fullbright = true;
        /// <summary>Apply the "lit" rules of ultrart-materials.json.</summary>
        public bool rules = true;
        /// <summary>Lit weight of unlit ENEMY materials (the game draws enemies fullbright): 0 = like the game, 1 = fully
        /// relit. In between keeps RT shadows on enemies without darkening them ([Materials] EnemyLitWeight,
        /// -ultrart-enemy-lit w).</summary>
        public float enemy = 0.5f;
        /// <summary>Per enemy type ("Filth=0.65, Stray=0.8": EnemyType names, case-insensitive), over <see cref="enemy"/>
        /// ([Materials] EnemyTypeLitWeights, -ultrart-enemy-types). Bench variants write ':' for '=' and ';' for ','
        /// ("unlit.enemyTypes=Filth:0.65;Stray:0.8"), since '+' and '=' separate variants.</summary>
        public string enemyTypes = "";
        /// <summary>Enemy types whose glow is painted into their texture (the drone's eye, the Swordsmachine's blade): they
        /// keep <see cref="keepWeight"/> whatever <see cref="enemy"/> is, unless <see cref="enemyTypes"/> names them. More
        /// light on them would turn the painted glow into a lit, shaded colour.</summary>
        public string keepTypes = "Drone,Swordsmachine";
        public float keepWeight = 0.5f;
        /// <summary>Extra material-name rules over everything else, "Gibs:0.6;Lion*:0.7" (globs on the material name; for
        /// tuning in one launch: bench variant "unlit.extra=..."). Dynamic and static-batched proxies only, not atlas slots.</summary>
        public string extra = "";

        private string _parsedFrom, _keepFrom, _extraFrom;
        private readonly List<(System.Text.RegularExpressions.Regex rx, float w)> _extra = new List<(System.Text.RegularExpressions.Regex, float)>();

        /// <summary>The weight an <see cref="extra"/> rule gives a material name (last match wins), or null.</summary>
        public float? Extra(string materialName)
        {
            if (!ReferenceEquals(_extraFrom, extra))
            {
                _extraFrom = extra;
                _extra.Clear();
                foreach (var part in (extra ?? "").Split(';', ','))
                {
                    int colon = part.LastIndexOf(':');
                    if (colon <= 0) continue;
                    if (!float.TryParse(part.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) continue;
                    var glob = part.Substring(0, colon).Trim();
                    _extra.Add((new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase), Mathf.Clamp01(w)));
                }
            }
            float? r = null;
            foreach (var e in _extra) if (e.rx.IsMatch(materialName)) r = e.w;
            return r;
        }
        private readonly Dictionary<string, float> _types = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The weight for an enemy type, or <see cref="enemy"/> when the type is unknown or not listed.</summary>
        public float EnemyWeight(string type)
        {
            if (!ReferenceEquals(_parsedFrom, enemyTypes) || !ReferenceEquals(_keepFrom, keepTypes)) Parse();
            if (type != null && _types.TryGetValue(type, out var w)) return w;
            if (type != null && _keep.Contains(type)) return Mathf.Clamp01(keepWeight);
            return Mathf.Clamp01(enemy);
        }

        private readonly HashSet<string> _keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Parse()
        {
            _parsedFrom = enemyTypes;
            _keepFrom = keepTypes;
            _keep.Clear();
            foreach (var k in (keepTypes ?? "").Split(',', ';')) if (k.Trim().Length > 0) _keep.Add(k.Trim());
            _types.Clear();
            if (string.IsNullOrEmpty(enemyTypes)) return;
            foreach (var part in enemyTypes.Split(',', ';'))
            {
                var kv = part.Split('=', ':');
                if (kv.Length != 2) continue;
                if (float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                    _types[kv[0].Trim()] = Mathf.Clamp01(w);
            }
        }

        /// <summary>Changes whenever a setting that affects a weight changes.</summary>
        public int Signature()
        {
            unchecked
            {
                int h = fullbright ? 1 : 0;
                h = h * 31 + (rules ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(Mathf.Clamp01(enemy) * 1000f);
                h = h * 31 + (enemyTypes ?? "").GetHashCode();
                h = h * 31 + (keepTypes ?? "").GetHashCode() + Mathf.RoundToInt(Mathf.Clamp01(keepWeight) * 1000f);
                return h * 31 + (extra ?? "").GetHashCode();
            }
        }

        public override string ToString() =>
            $"fullbright {(fullbright ? "on" : "off")}, rules {(rules ? "on" : "off")}, enemy {enemy:0.##}" +
            (string.IsNullOrEmpty(enemyTypes) ? "" : $", per type '{enemyTypes}'") + (string.IsNullOrEmpty(extra) ? "" : $", extra '{extra}'");
    }

    internal sealed partial class SceneProxies
    {
        /// <summary>Lit weights (see <see cref="UnlitSettings"/>); bench variant prefix "unlit".</summary>
        public static readonly UnlitSettings Unlit = new UnlitSettings();

        private static readonly int IdUnlit = Shader.PropertyToID("_Unlit");
        private static readonly int IdLitOverride = Shader.PropertyToID("_LitOverride");
        private static readonly int IdNoFog = Shader.PropertyToID("_NoFog");

        /// <summary>The enemy type of each ENEMY source material, from the first renderer that used it (null: none found).</summary>
        private readonly Dictionary<Material, string> _enemyTypeOf = new Dictionary<Material, string>();
        /// <summary>Atlas variants with a "lit" rule: the rule's weight (re-applied when <see cref="UnlitSettings.rules"/> changes).</summary>
        private readonly Dictionary<Material, float> _atlasLit = new Dictionary<Material, float>();
        private int _unlitSignature = int.MinValue;

        /// <summary>Counts for the scene log: proxies made fullbright, partial, by rule.</summary>
        private int _unlitFull, _unlitPartial, _unlitByRule;

        /// <summary>Remembers the enemy type behind a renderer's ENEMY materials before their proxies are made.</summary>
        private void NoteEnemyMaterials(Renderer r, Material[] mats)
        {
            bool need = false;
            foreach (var m in mats)
                if (m != null && !_enemyTypeOf.ContainsKey(m) && m.IsKeywordEnabled("ENEMY")) { need = true; break; }
            if (!need) return;
            string type = null;
            try
            {
                var e = r.GetComponentInParent<EnemyIdentifier>(true);
                if (e != null) type = e.enemyType.ToString();
            }
            catch (Exception) { /* game type changed: global weight */ }
            foreach (var m in mats)
                if (m != null && !_enemyTypeOf.ContainsKey(m) && m.IsKeywordEnabled("ENEMY")) _enemyTypeOf[m] = type;
        }

        /// <summary>The lit weight of a source material's proxy (non-atlas). See <see cref="UnlitSettings"/>.</summary>
        private float LitWeightOf(Material src, out bool byRule)
        {
            byRule = false;
            if (src == null || !Unlit.fullbright) return 1f;
            if (!string.IsNullOrEmpty(Unlit.extra) && Unlit.Extra(MaterialClassifier.CleanName(src)) is float x) { byRule = true; return x; }
            if (Unlit.rules && _classifier.LitOf(src) is float r) { byRule = true; return r; }
            if (src.shader == null || src.shader.name != "ULTRAKILL/Master" || src.IsKeywordEnabled("VERTEX_LIGHTING")) return 1f;
            if (!src.IsKeywordEnabled("ENEMY")) return 0f;
            _enemyTypeOf.TryGetValue(src, out var type);
            return Unlit.EnemyWeight(type);
        }

        /// <summary>-ultrart-lit-probe GLOB: logs the lit weight of every proxy and atlas slot whose material matches.</summary>
        public static System.Text.RegularExpressions.Regex LitProbe;
        private int _litProbeLogged;

        private void ProbeLit(string what, Material src, string detail)
        {
            if (LitProbe == null || src == null || _litProbeLogged > 200 || !LitProbe.IsMatch(MaterialClassifier.CleanName(src))) return;
            _litProbeLogged++;
            var kws = string.Join(" ", src.shaderKeywords);
            _log.LogInfo($"Lit probe: {what} '{MaterialClassifier.CleanName(src)}' ({(src.shader != null ? src.shader.name : "-")}; {kws}) q{src.renderQueue}: {detail}");
        }

        /// <summary>Sets _Unlit on a new proxy material (Proxy()).</summary>
        private void ApplyLitWeight(Material src, Material proxy)
        {
            float w = LitWeightOf(src, out bool byRule);
            ProbeLit("proxy", src, $"lit weight {w:0.##}{(byRule ? " (rule)" : "")}{(src.shader != null && src.shader.name.Contains("Stationary") ? " (atlas: vertex alpha)" : "")}");
            proxy.SetFloat(IdUnlit, 1f - w);
            if (w <= 0f) _unlitFull++;
            else if (w < 1f) _unlitPartial++;
            if (byRule) _unlitByRule++;
        }

        /// <summary>The "lit" rule of an atlas slot's original material, or -1 (the vertex alpha alone).</summary>
        private float AtlasLitOf(Material original) =>
            Unlit.fullbright && Unlit.rules && original != null && _classifier.LitOf(original) is float r ? r : -1f;

        /// <summary>Per frame: re-weight every proxy material when the settings changed (bench variants, menu).</summary>
        private void RefreshUnlitIfChanged()
        {
            int sig = Unlit.Signature();
            if (sig == _unlitSignature) return;
            bool first = _unlitSignature == int.MinValue;
            _unlitSignature = sig;
            if (first) return; // the proxies made so far were made with these settings
            foreach (var kv in _materialCache)
            {
                if (kv.Key == null || kv.Value == null || IsAtlasProxy(kv.Value)) continue;
                kv.Value.SetFloat(IdUnlit, 1f - LitWeightOf(kv.Key, out _));
            }
            foreach (var kv in _playerMaterialCache)
            {
                if (kv.Key == null || kv.Value == null || !_materialCache.TryGetValue(kv.Key, out var shared) || shared == null) continue;
                kv.Value.SetFloat(IdUnlit, Mathf.Clamp01(PlayerModelUnlit) * shared.GetFloat(IdUnlit));
            }
            foreach (var kv in _atlasLit)
                if (kv.Key != null) kv.Key.SetFloat(IdLitOverride, Unlit.fullbright && Unlit.rules ? kv.Value : -1f);
            // Per-renderer copies (property blocks, hot debris) follow their shared proxy.
            foreach (var d in _dynamic)
            {
                var own = d.Sync?.Own;
                if (own == null || d.Sync.Shared == null) continue;
                for (int i = 0; i < own.Length && i < d.Sync.Shared.Length; i++)
                    if (own[i] != null && d.Sync.Shared[i] != null) own[i].SetFloat(IdUnlit, d.Sync.Shared[i].GetFloat(IdUnlit));
            }
            _log.LogInfo($"Lit weights re-applied ({Unlit})");
        }

        private string UnlitSummary() =>
            $"Lit weights ({Unlit}): {_unlitFull} proxy materials fullbright, {_unlitPartial} partial, {_unlitByRule} set by a \"lit\" rule, " +
            $"{_atlasLit.Count} atlas variants with a rule; enemy types seen: " +
            string.Join(", ", new SortedSet<string>(EnemyTypesSeen()));

        private IEnumerable<string> EnemyTypesSeen()
        {
            foreach (var kv in _enemyTypeOf)
                if (kv.Key != null) yield return $"{kv.Value ?? "?"}/{MaterialClassifier.CleanName(kv.Key)} {Unlit.EnemyWeight(kv.Value):0.##}";
        }
    }
}
