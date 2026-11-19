using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Light classes at run time (docs/lighting-overrides.md, "Classes and rules"): every scene light is resolved once into
    /// its class, the rule that set it, and what RT does with it (class policy, rule, the light's own entry, in that
    /// order of precedence from weak to strong). On top of that sit the review switches: whole classes or single lights
    /// off, dimmed or soloed (the light editor, and bench variants lc.class.NAME=F, lc.solo=NAME, lc.light.TEXT=F), and a
    /// proposal layer of files (lc.layer=NAME). A census of every light with its class and policy goes into a bench
    /// run's folder with -ultrart-light-census.
    /// </summary>
    public partial class Plugin
    {
        private sealed class LightResolved
        {
            public LightOverrideEntry Entry; // the light's own entry (null: none)
            public string Class = LightClasses.Other, Rule = "";
            public float Intensity = 1f, Range = 1f, Reach = 1f, Medium = 1f;
            /// <summary>The review switches' factor (class / light toggles, solo); 0 = hidden.</summary>
            public float Toggle = 1f;
            public Vector3 Color = Vector3.one;
            public bool Surfaces = true, Disabled;
            public bool? Shadows;
            /// <summary>Anything but the toggle differs from the game's light.</summary>
            public bool Changes;
            /// <summary>The rule that classed it (null: code, an entry, or none).</summary>
            public LightRule RuleObject;
            /// <summary>Handover (Plugin.Handover.cs): "handed", "hybrid", "no part", or null.</summary>
            public string Handed;
            /// <summary>The intensity before a handover cut it (-1: none). The lamp keeps it for its glints, the
            /// highlight on water and wet floors, which emissive parts don't make (Plugin.Glints.cs).</summary>
            public float GlintIntensity = -1f;

            public string PolicyText()
            {
                var p = new LightPolicy
                {
                    Intensity = Intensity != 1f ? Intensity : (float?)null, Range = Range != 1f ? Range : (float?)null,
                    Reach = Reach != 1f ? Reach : (float?)null, Medium = Medium != 1f ? Medium : (float?)null,
                    Color = Color != Vector3.one ? new[] { Color.x, Color.y, Color.z } : null,
                    Surfaces = Surfaces ? (bool?)null : false, Shadows = Shadows, Disabled = Disabled ? true : (bool?)null,
                };
                return p.ToString();
            }
        }

        private readonly Dictionary<Light, LightResolved> _resolved = new Dictionary<Light, LightResolved>();
        private SceneProxies _resolvedScene;
        private int _resolvedFixtureVersion = -1, _resolvedToggleVersion = -1, _resolvedPowerVersion = -1;

        // Review switches.
        private readonly List<KeyValuePair<string, float>> _classToggles = new List<KeyValuePair<string, float>>();
        private readonly List<KeyValuePair<string, float>> _lightToggles = new List<KeyValuePair<string, float>>();
        private string _classSolo;
        private int _toggleVersion;
        /// <summary>Class policies set by bench variants (lc.set.CLASS.FIELD=VALUE), over the files' policies.</summary>
        private readonly Dictionary<string, LightPolicy> _variantPolicies = new Dictionary<string, LightPolicy>(StringComparer.OrdinalIgnoreCase);
        private bool _lightCensus; // -ultrart-light-census
        private readonly HashSet<string> _censusDone = new HashSet<string>();

        // ---------------------------------------------------------------- emitters (glowing surfaces as light sources)

        /// <summary>Emitter scales set by bench variants (lc.emit.TEXT=F: materials whose name contains TEXT).</summary>
        private readonly List<KeyValuePair<string, float>> _variantEmitters = new List<KeyValuePair<string, float>>();
        private readonly List<KeyValuePair<string, Vector2>> _variantGlow = new List<KeyValuePair<string, Vector2>>();
        private string _emitterKey = "";

        private static string CleanMaterialName(string n) => (n ?? "").Replace(" (Instance)", "").Replace(" [UltraRT]", "").Trim();

        /// <summary>lc.emit.TEXT / lc.glow.TEXT: TEXT is part of the material's name, or with "exact." in front the
        /// whole name (lc.glow.exact.Light: 'Light', not 'Streetlight').</summary>
        private static bool VariantNameMatches(string name, string key) =>
            key.StartsWith("exact.", StringComparison.OrdinalIgnoreCase) ? string.Equals(name, key.Substring(6), StringComparison.OrdinalIgnoreCase)
                                                                         : name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>EmissiveLights.LightScaleOf: the scale on the light a surface gives ("emitters" rules, bench variants).</summary>
        private float EmitterLightScale(Material m, Texture emission, Transform source)
        {
            if (!_lightOverrides || _lightOv == null || m == null) return 1f;
            string name = CleanMaterialName(m.name);
            foreach (var kv in _variantEmitters)
                if (VariantNameMatches(name, kv.Key)) return kv.Value;
            return _lightOv.Classes.EmitterScale(name, emission != null ? emission.name : "", source != null ? LightOverrides.NormalizedPath(source) : "", out _);
        }

        /// <summary>SceneProxies.EmitterGlowOf: the emitter mask of a source material ("emitters" rules with "glow",
        /// bench variants lc.glow.TEXT=G or G,T).</summary>
        private Vector2 EmitterGlow(Material src)
        {
            if (!_lightOverrides || _lightOv == null || src == null) return Vector2.zero;
            string name = CleanMaterialName(src.name);
            foreach (var kv in _variantGlow)
                if (VariantNameMatches(name, kv.Key)) return kv.Value;
            var main = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            return _lightOv.Classes.EmitterGlow(name, main != null ? main.name : "");
        }

        /// <summary>EnsureLightOverrides: the emissive triangle table is built with the emitter scales; rebuild it when they change.</summary>
        private void SyncEmitterScales()
        {
            string key = !_lightOverrides ? "off" : _lightOv.Classes.EmitterKey + (_lightOv.Classes.HasHandovers ? "@" + _lightOv.Scene + HandoverVariantKey : "") + "#" + string.Join(";", _variantEmitters.Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)))
                + "#" + string.Join(";", _variantGlow.Select(kv => kv.Key + "=" + kv.Value.x.ToString(CultureInfo.InvariantCulture) + "," + kv.Value.y.ToString(CultureInfo.InvariantCulture)));
            if (key == _emitterKey) return;
            bool first = _emitterKey == "";
            _emitterKey = key;
            if (first && _lightOv.Classes.EmitterKey == "" && _variantEmitters.Count == 0 && _variantGlow.Count == 0) return; // nothing to apply
            _proxies?.SyncEmitterGlow(); // masks first: the table reads the proxy materials
            _emissive?.Clear(); // rebuilt on the next traced frame (EmissiveLights.Update)
            ResetLightHistories();
            Logger.LogInfo($"Emitter scales changed ({key}): emissive lights rebuild");
        }

        private bool LightTogglesActive => !_handoverOn || _handoverGain != 1f || _classToggles.Count > 0 || _lightToggles.Count > 0 || _classSolo != null || _variantPolicies.Count > 0 || _variantEmitters.Count > 0 || _variantGlow.Count > 0;

        private void ClearLightToggles()
        {
            if (!LightTogglesActive) return;
            _variantEmitters.Clear();
            _variantGlow.Clear();
            _classToggles.Clear();
            _lightToggles.Clear();
            _variantPolicies.Clear();
            _classSolo = null;
            _handoverOn = true;
            _handoverGain = 1f;
            _toggleVersion++;
        }

        /// <summary>"CLASS.FIELD" = value: intensity, range, reach, medium (numbers), surfaces, shadows, disabled (0 / 1).</summary>
        private void SetVariantPolicy(string spec, string value)
        {
            int dot = spec.LastIndexOf('.');
            if (dot <= 0) { Logger.LogWarning($"Burst: lc.set needs CLASS.FIELD, got '{spec}'"); return; }
            string cls = spec.Substring(0, dot).Replace('.', ':'), field = spec.Substring(dot + 1).ToLowerInvariant();
            if (!_variantPolicies.TryGetValue(cls, out var p)) _variantPolicies[cls] = p = new LightPolicy();
            bool on = value != "0" && value != "off" && value != "false";
            float f = ParseScale(value);
            switch (field)
            {
                case "intensity": p.Intensity = f; break;
                case "range": p.Range = f; break;
                case "reach": p.Reach = f; break;
                case "medium": p.Medium = f; break;
                case "surfaces": p.Surfaces = on; break;
                case "shadows": p.Shadows = on; break;
                case "disabled": p.Disabled = on; break;
                default: Logger.LogWarning($"Burst: lc.set: unknown field '{field}'"); break;
            }
            _toggleVersion++;
        }

        private LightPolicy VariantPolicy(string cls)
        {
            if (_variantPolicies.Count == 0 || string.IsNullOrEmpty(cls)) return null;
            if (_variantPolicies.TryGetValue(cls, out var p)) return p;
            int colon = cls.IndexOf(':');
            return colon > 0 && _variantPolicies.TryGetValue(cls.Substring(0, colon), out p) ? p : null;
        }

        private void SetClassToggle(string cls, float scale)
        {
            _classToggles.RemoveAll(kv => string.Equals(kv.Key, cls, StringComparison.OrdinalIgnoreCase));
            if (scale != 1f) _classToggles.Add(new KeyValuePair<string, float>(cls, Mathf.Max(0f, scale)));
            _toggleVersion++;
        }

        private static float ParseScale(string v)
        {
            switch ((v ?? "").ToLowerInvariant())
            {
                case "off": case "false": return 0f;
                case "on": case "true": case "": return 1f;
                default: return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? Mathf.Max(0f, f) : 1f;
            }
        }

        /// <summary>Bench variants "lc.KEY=VALUE": class.NAME=off|F, solo=NAME, light.TEXT=off|F (TEXT: part of the
        /// hierarchy path), emit.TEXT=F (the light of emissive materials named TEXT), glow.TEXT=G or G,T (an emitter
        /// mask on materials named TEXT: glow, threshold), set.CLASS.FIELD=VALUE (a class policy for this mode), layer=NAME (lights\NAME\ over the
        /// shipped files), handover=0 (handover rules do nothing), hgain=F (on every handover's gain). A kind is written with a dot (prefab.firstroom = prefab:firstroom: ':' cannot be in a file
        /// name). Undone by RestoreVariantState.</summary>
        private void ApplyLightVariant(string key, string value)
        {
            if (key.StartsWith("class.", StringComparison.OrdinalIgnoreCase)) SetClassToggle(key.Substring(6).Replace('.', ':'), ParseScale(value));
            else if (key.StartsWith("set.", StringComparison.OrdinalIgnoreCase)) SetVariantPolicy(key.Substring(4), value);
            else if (key.StartsWith("emit.", StringComparison.OrdinalIgnoreCase)) { _variantEmitters.Add(new KeyValuePair<string, float>(key.Substring(5), ParseScale(value))); _toggleVersion++; }
            else if (key.StartsWith("glow.", StringComparison.OrdinalIgnoreCase))
            {
                var parts = (value ?? "").Split(',');
                _variantGlow.Add(new KeyValuePair<string, Vector2>(key.Substring(5), new Vector2(ParseScale(parts[0]), parts.Length > 1 ? ParseScale(parts[1]) : 0f)));
                _toggleVersion++;
            }
            else if (key.StartsWith("light.", StringComparison.OrdinalIgnoreCase))
            {
                _lightToggles.Add(new KeyValuePair<string, float>(key.Substring(6), ParseScale(value)));
                _toggleVersion++;
            }
            else if (key.Equals("solo", StringComparison.OrdinalIgnoreCase)) { _classSolo = string.IsNullOrEmpty(value) || value == "0" || value == "off" ? null : value.Replace('.', ':'); _toggleVersion++; }
            else if (key.Equals("handover", StringComparison.OrdinalIgnoreCase)) { _handoverOn = ParseScale(value) > 0f; _toggleVersion++; }
            else if (key.Equals("hgain", StringComparison.OrdinalIgnoreCase)) { _handoverGain = ParseScale(value); _toggleVersion++; }
            else if (key.Equals("layer", StringComparison.OrdinalIgnoreCase))
            {
                EnsureLightOverrides();
                _lightOv.SetLayer(value == "0" || value == "off" ? null : value);
            }
            else Logger.LogWarning($"Burst: unknown light variant 'lc.{key}'");
        }

        private void RestoreLightVariants()
        {
            ClearLightToggles();
            _lightOv?.SetLayer(null);
        }

        private static readonly string[] ScriptWords = { "flicker", "light", "blink", "pulse", "fade", "toggle", "switch", "day", "night", "color", "colour", "intensity", "anim", "lerp" };

        /// <summary>The light's class and the rule that set it. Lights that move or belong to another system get their
        /// class from code; level lights from the rules (LightClasses), else "other".</summary>
        private string ClassifyLight(Light l, string path, out string rule)
        {
            rule = "code";
            var t = l.transform;
            if (l.type == LightType.Directional) return -t.forward.y < -0.05f ? LightClasses.SkyFill : LightClasses.Sun;
            if (l.type != LightType.Point && l.type != LightType.Spot) return LightClasses.Other;
            var player = MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.transform : null;
            var camT = _camera != null ? _camera.transform : null;
            if ((player != null && t.IsChildOf(player)) || (camT != null && t.IsChildOf(camT))) return LightClasses.Player;
            if (l.GetComponentInParent<EnemyIdentifier>(true) != null) return LightClasses.Enemy;
            if (l.GetComponentInParent<ItemIdentifier>(true) != null) return LightClasses.Item;
            if (_proxies == null || !_proxies.IsBuildLight(l)) return LightClasses.Effect;
            if (_lavaLights.Contains(l)) return LightClasses.Lava;
            var f = new LightFacts
            {
                Path = path, Type = l.type == LightType.Spot ? "spot" : "point", Range = l.range, Intensity = l.intensity,
                Saturation = LightFacts.SaturationOf(l.color),
                Fixture = _proxies.FixtureBounds.ContainsKey(l), Lifted = _fixtureLifts.ContainsKey(l), Enclosed = _proxies.StaticEnclosed.Contains(l),
                Orb = _proxies.OrbLights.Contains(l) || _proxies.OrbFixtureLights.Contains(l),
            };
            foreach (var b in l.GetComponents<MonoBehaviour>())
                if (b != null && HasWord(b.GetType().Name, ScriptWords)) { f.Scripted = true; break; }
            f.Animated = l.GetComponentInParent<Animator>() != null || l.GetComponentInParent<Animation>() != null;
            var r = _lightOv.Classes.Match(f);
            if (r == null) { rule = ""; return LightClasses.Other; }
            rule = r.Name ?? "(unnamed)";
            _matchedRule = r;
            return r.Class;
        }

        private LightRule _matchedRule; // out of ClassifyLight (the rule object, for its "set")

        private static void Merge(LightResolved r, LightPolicy p)
        {
            if (p == null) return;
            if (p.Intensity.HasValue) r.Intensity = Mathf.Max(0f, p.Intensity.Value);
            if (p.Color != null && p.Color.Length >= 3) r.Color = new Vector3(p.Color[0], p.Color[1], p.Color[2]);
            if (p.Range.HasValue) r.Range = Mathf.Max(0.01f, p.Range.Value);
            if (p.Reach.HasValue) r.Reach = Mathf.Clamp01(p.Reach.Value);
            if (p.Surfaces.HasValue) r.Surfaces = p.Surfaces.Value;
            if (p.Medium.HasValue) r.Medium = Mathf.Max(0f, p.Medium.Value);
            if (p.Shadows.HasValue) r.Shadows = p.Shadows;
            if (p.Disabled.HasValue) r.Disabled = p.Disabled.Value;
        }

        /// <summary>Classes whose lights move or belong to another system: policies apply (a class can be dimmed), but
        /// "reach" and "surfaces" stay out of the question for the player's own lights.</summary>
        private LightResolved BuildResolved(Light l, bool handover = true)
        {
            var id = LightIdentity(l);
            var r = new LightResolved();
            var e = _lightOv.Find(id.path, l.name, id.pos);
            _matchedRule = null;
            r.Class = ClassifyLight(l, id.path, out r.Rule);
            var rule = _matchedRule;
            // An entry may name the class itself (a hand correction, the editor). The classes in files written by the
            // 2026-11-15 classifier and fit are the older, coarser ones: the rules know better.
            bool oldTool = e != null && e.Source != null && (e.Source.StartsWith("classifier", StringComparison.Ordinal) || e.Source == "fit");
            if (e != null && !oldTool && !string.IsNullOrEmpty(e.Class) && e.Class != "point")
            {
                string c = e.Class;
                int slash = c.IndexOf('/');
                if (slash > 0) c = c.Substring(0, slash); // "fixture:lamp/shade"
                if (!string.Equals(c, r.Class, StringComparison.OrdinalIgnoreCase)) { r.Class = c; r.Rule = "entry"; rule = null; }
            }
            // An identity entry of the user's is "the game's values": nothing applies, not even the class's policy.
            bool reset = e != null && e.Origin == "user" && (e.Intensity ?? 1f) == 1f && e.Color == null && e.Range == null && e.Shadows == null &&
                         e.Disabled != true && e.Reach == null && e.Surfaces == null && e.Medium == null && LightOverrides.ShapeCode(e.Shape) == 0 && !e.HasOffset &&
                         e.Note == "reset to the game's values";
            if (!reset)
            {
                Merge(r, _lightOv.Classes.GlobalPolicy(r.Class));
                Merge(r, _lightOv.Classes.LevelPolicy(r.Class));
                Merge(r, VariantPolicy(r.Class));
                Merge(r, rule?.Set);
                r.RuleObject = rule;
                if (e != null)
                {
                    Merge(r, new LightPolicy { Intensity = e.Intensity, Color = e.Color, Range = e.Range, Reach = e.Reach, Surfaces = e.Surfaces, Medium = e.Medium, Shadows = e.Shadows, Disabled = e.Disabled });
                    if (e.OwnsGeometry) r.Entry = e;
                }
                // After the light's own entry: the entry's strength is what is handed over (Plugin.Handover.cs).
                if (handover) r.Handed = ApplyHandover(l, r, rule);
            }
            if (r.Class == LightClasses.Player) { r.Reach = 1f; r.Surfaces = true; }
            r.Changes = r.Entry != null || r.Intensity != 1f || r.Color != Vector3.one || r.Range != 1f || r.Reach != 1f || !r.Surfaces || r.Medium != 1f || r.Shadows.HasValue || r.Disabled;
            // Review switches.
            float tg = ClassToggleFor(r.Class);
            foreach (var kv in _lightToggles)
                if (id.path.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) tg *= kv.Value;
            if (_classSolo != null && l.type != LightType.Directional && !LightClasses.ClassIs(r.Class, _classSolo)) tg = 0f;
            r.Toggle = tg;
            return r;
        }

        private float ClassToggleFor(string cls)
        {
            float s = 1f;
            foreach (var kv in _classToggles) if (LightClasses.ClassIs(cls, kv.Key)) s *= kv.Value;
            return s;
        }

        /// <summary>What RT does with a scene light this frame, or null (overrides inactive: the game's light).</summary>
        private LightResolved ResolveLight(Light l)
        {
            if (!_ovActiveThisFrame) return null;
            return ResolvedFor(l);
        }

        /// <summary>The same for the editor and the census: also while overrides are not applied.</summary>
        private LightResolved ResolvedFor(Light l)
        {
            if (!_resolved.TryGetValue(l, out var r) || CarriedChanged(l, r)) _resolved[l] = r = BuildResolved(l);
            return r;
        }

        /// <summary>
        /// A carried item's light (0-S's skull, 4-3's torch) is "item" on the ground and "player" in the hand
        /// (ClassifyLight). The resolved set is cached, so the class used to be whichever the light had when the cache
        /// was last cleared: picked up after a clear, the skull kept "item" (x2.5, reach 0) until the next one, then
        /// turned "player" (x1, full reach) mid-play (2026-11-18, `t3/dark-0-S`). Re-resolved when that changes.
        /// </summary>
        private bool CarriedChanged(Light l, LightResolved r)
        {
            if (r.Class != LightClasses.Item && r.Class != LightClasses.Player) return false;
            if (l == null) return false;
            var t = l.transform;
            var player = MonoSingleton<NewMovement>.Instance != null ? MonoSingleton<NewMovement>.Instance.transform : null;
            var camT = _camera != null ? _camera.transform : null;
            bool carried = (player != null && t.IsChildOf(player)) || (camT != null && t.IsChildOf(camT));
            return carried != (r.Class == LightClasses.Player);
        }

        /// <summary>BeginLightOverrides: the resolved set follows the files, the fixture data and the review switches.</summary>
        private void SyncResolvedLights()
        {
            int fv = _proxies != null ? _proxies.FixtureVersion : -1;
            // Handover: a light is handed over once its part is in the emissive table (Plugin.Handover.cs).
            int pv = _emissive != null ? _emissive.PowerVersion : 0;
            if (pv != _resolvedPowerVersion) { _resolvedPowerVersion = pv; if (_lightOv.Classes.HasHandovers) _resolved.Clear(); }
            if (_ovCacheVersion == _lightOv.Version && ReferenceEquals(_resolvedScene, _proxies) && _resolvedFixtureVersion == fv && _resolvedToggleVersion == _toggleVersion) return;
            bool switched = _resolvedToggleVersion != _toggleVersion; // a review switch: a cut, not a drift
            _ovCacheVersion = _lightOv.Version;
            _resolvedScene = _proxies;
            _resolvedFixtureVersion = fv;
            _resolvedToggleVersion = _toggleVersion;
            _resolved.Clear();
            if (switched) ResetLightHistories();
        }

        // ---------------------------------------------------------------- census

        private sealed class CensusRow
        {
            [JsonProperty("path")] public string Path;
            [JsonProperty("pos")] public float[] Pos;
            [JsonProperty("type")] public string Type;
            [JsonProperty("dir", NullValueHandling = NullValueHandling.Ignore)] public float[] Dir; // spot and directional: where it points
            [JsonProperty("angle", NullValueHandling = NullValueHandling.Ignore)] public float? Angle; // spot: full cone angle (deg)
            [JsonProperty("range")] public float Range;
            [JsonProperty("intensity")] public float Intensity;
            [JsonProperty("color")] public float[] Color;
            [JsonProperty("active")] public bool Active;
            [JsonProperty("class")] public string Class;
            [JsonProperty("rule")] public string Rule;
            [JsonProperty("policy")] public string Policy;
            [JsonProperty("entry", NullValueHandling = NullValueHandling.Ignore)] public string Entry;
            /// <summary>The fixture's materials, "name:emissive|unlit|lit" (FixtureMaterials).</summary>
            [JsonProperty("fixture", NullValueHandling = NullValueHandling.Ignore)] public string[] Fixture;
            /// <summary>Handover: "handed", "hybrid" or "no part" (the rule matched, the fixture has no such part).</summary>
            [JsonProperty("handover", NullValueHandling = NullValueHandling.Ignore)] public string Handover;
        }

        /// <summary>Census: what the light's fixture is made of, so that tools/lights/fixtures.py can tell which fake
        /// lights have a glowing part to hand their job to (docs/lighting-overrides.md, "Emissive fixtures"). The
        /// fixture is the nearest ancestor (two levels up at most) with renderers below it; more than 24 renderers is
        /// a room, not a fixture. Per material: emissive (the game's EMISSIVE / _EMISSION keyword: it is in the
        /// emissive light list when bright enough), unlit (fullbright Master: it shows its texture, and lights nothing
        /// unless it is also emissive), or lit.</summary>
        private static string[] FixtureMaterials(Light l)
        {
            var t = l.transform.parent;
            for (int up = 0; up < 2 && t != null; up++, t = t.parent)
            {
                var rs = t.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) continue;
                if (rs.Length > 24) return null;
                var names = new List<string>();
                foreach (var r in rs)
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string kind = m.IsKeywordEnabled("EMISSIVE") || m.IsKeywordEnabled("_EMISSION") ? "emissive"
                            : m.shader != null && m.shader.name == "ULTRAKILL/Master" && !m.IsKeywordEnabled("VERTEX_LIGHTING") ? "unlit" : "lit";
                        string n = CleanMaterialName(m.name) + ":" + kind;
                        if (!names.Contains(n) && names.Count < 8) names.Add(n);
                    }
                }
                return names.Count > 0 ? names.ToArray() : null;
            }
            return null;
        }

        /// <summary>Bench, at the end of a scenario (-ultrart-light-census): every light the RT scene tracks, on or off,
        /// with its class, the rule and the policy, as lights_&lt;scene&gt;.json in the run's folder (once per scene), and
        /// lights_&lt;scene&gt;.&lt;layer&gt;.json for every layer folder under the shipped files (lights\proposed\).
        /// tools/lights/rulediff.py turns them into the level's review table.</summary>
        private void WriteLightCensus(string outDir)
        {
            if (!_lightCensus || _proxies == null) return;
            string scene = SafeCurrentScene();
            if (!_censusDone.Add(outDir + "|" + scene)) return;
            try
            {
                EnsureLightOverrides();
                string was = _lightOv.Layer;
                var layers = new List<string> { null };
                if (Directory.Exists(_lightOv.ShippedDir)) layers.AddRange(Directory.GetDirectories(_lightOv.ShippedDir).Select(Path.GetFileName));
                foreach (var layer in layers)
                {
                    _lightOv.SetLayer(layer);
                    var rows = new List<CensusRow>();
                    foreach (var l in _proxies.Lights)
                    {
                        if (l == null) continue;
                        var id = LightIdentity(l);
                        var r = BuildResolved(l);
                        var e = _lightOv.Find(id.path, l.name, id.pos);
                        rows.Add(new CensusRow
                        {
                            Path = id.path, Pos = new[] { LightOverrides.Round(id.pos.x), LightOverrides.Round(id.pos.y), LightOverrides.Round(id.pos.z) },
                            Type = l.type.ToString().ToLowerInvariant(), Range = l.range, Intensity = l.intensity,
                            Dir = l.type == LightType.Point ? null : new[] { LightOverrides.Round(l.transform.forward.x), LightOverrides.Round(l.transform.forward.y), LightOverrides.Round(l.transform.forward.z) },
                            Angle = l.type == LightType.Spot ? l.spotAngle : (float?)null,
                            Color = new[] { LightOverrides.Round(l.color.r), LightOverrides.Round(l.color.g), LightOverrides.Round(l.color.b) },
                            Active = l.isActiveAndEnabled, Class = r.Class, Rule = r.Rule, Policy = r.PolicyText(), Entry = e?.Origin,
                            Fixture = layer == null ? FixtureMaterials(l) : null, Handover = r.Handed,
                        });
                    }
                    var emitters = _emissive?.Describe().Select(g =>
                    {
                        float sc = _lightOv.Classes.EmitterScale(CleanMaterialName(g.Material), g.Texture, "", out string er);
                        return new { material = CleanMaterialName(g.Material), texture = g.Texture, triangles = g.Triangles, visible = g.Visible, area = LightOverrides.Round(g.Area), lum = LightOverrides.Round(g.Lum), scale = sc, rule = er };
                    }).ToList();
                    var doc = new { scene, layer, emitters, lights = rows.OrderBy(x => x.Class, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal).ToList() };
                    string file = Path.Combine(outDir, "lights_" + string.Join("_", scene.Split(Path.GetInvalidFileNameChars())) + (layer != null ? "." + layer : "") + ".json");
                    File.WriteAllText(file, JsonConvert.SerializeObject(doc, Formatting.Indented, new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture }));
                    Logger.LogInfo($"Light census: {rows.Count} lights of '{scene}'{(layer != null ? $" (layer {layer})" : "")} -> {file}");
                }
                _lightOv.SetLayer(was);
                _resolved.Clear();
            }
            catch (Exception e) { Logger.LogWarning($"Light census failed: {e.Message}"); }
        }
    }
}
