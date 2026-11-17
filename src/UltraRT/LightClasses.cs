using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// What RT does with a light or a class of lights (docs/lighting-overrides.md, "Classes and rules"). Every field is
    /// optional; absent = unchanged. The same fields exist on a single light's entry (LightOverrideEntry).
    /// </summary>
    public sealed class LightPolicy
    {
        /// <summary>Multiplier on the game's colour x intensity (gamma, like Light.intensity). 0 = the light is dropped.</summary>
        [JsonProperty("intensity", NullValueHandling = NullValueHandling.Ignore)] public float? Intensity;
        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)] public float[] Color;
        [JsonProperty("range", NullValueHandling = NullValueHandling.Ignore)] public float? Range;
        /// <summary>How far the light reaches: 1 = relight's falloff (inverse square, brighter than the game's towards
        /// the range), 0 = never more than the game's own falloff at any distance, so what the game left dark far from
        /// the light stays dark. Steps of 0.25.</summary>
        [JsonProperty("reach", NullValueHandling = NullValueHandling.Ignore)] public float? Reach;
        /// <summary>false: the light does not light surfaces (it stays in the volumetric pass: "volumetric only").</summary>
        [JsonProperty("surfaces", NullValueHandling = NullValueHandling.Ignore)] public bool? Surfaces;
        /// <summary>Multiplier on what the light scatters in the air (volumetrics). 0 = none.</summary>
        [JsonProperty("medium", NullValueHandling = NullValueHandling.Ignore)] public float? Medium;
        [JsonProperty("shadows", NullValueHandling = NullValueHandling.Ignore)] public bool? Shadows;
        [JsonProperty("disabled", NullValueHandling = NullValueHandling.Ignore)] public bool? Disabled;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;

        [JsonIgnore] public bool IsEmpty => Intensity == null && Color == null && Range == null && Reach == null && Surfaces == null && Medium == null && Shadows == null && Disabled == null;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Disabled == true) parts.Add("off");
            if (Intensity.HasValue) parts.Add("x" + Intensity.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (Color != null && Color.Length >= 3) parts.Add(string.Format(CultureInfo.InvariantCulture, "rgb {0:0.##} {1:0.##} {2:0.##}", Color[0], Color[1], Color[2]));
            if (Range.HasValue) parts.Add("range x" + Range.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (Reach.HasValue) parts.Add("reach " + Reach.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (Surfaces == false) parts.Add("air only");
            if (Medium.HasValue) parts.Add("air x" + Medium.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (Shadows.HasValue) parts.Add(Shadows.Value ? "shadows" : "no shadows");
            return parts.Count > 0 ? string.Join(", ", parts) : "unchanged";
        }
    }

    /// <summary>
    /// One rule: the first rule whose conditions all hold gives a light its class (and, optionally, a policy of its own
    /// on top of the class's). Conditions that are absent always hold.
    /// </summary>
    public sealed class LightRule
    {
        [JsonProperty("name")] public string Name;
        /// <summary>Regex on the scene name ("Level 6-2"); case-insensitive.</summary>
        [JsonProperty("scene", NullValueHandling = NullValueHandling.Ignore)] public string Scene;
        /// <summary>Regex on the normalised hierarchy path ("(Clone)" removed); case-insensitive.</summary>
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)] public string Path;
        /// <summary>Regex the path must not match.</summary>
        [JsonProperty("notPath", NullValueHandling = NullValueHandling.Ignore)] public string NotPath;
        /// <summary>point | spot.</summary>
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string Type;
        [JsonProperty("rangeMin", NullValueHandling = NullValueHandling.Ignore)] public float? RangeMin;
        [JsonProperty("rangeMax", NullValueHandling = NullValueHandling.Ignore)] public float? RangeMax;
        /// <summary>Light.intensity (the game's units).</summary>
        [JsonProperty("intensityMin", NullValueHandling = NullValueHandling.Ignore)] public float? IntensityMin;
        [JsonProperty("intensityMax", NullValueHandling = NullValueHandling.Ignore)] public float? IntensityMax;
        /// <summary>Saturation of the light's colour, 0 (white) to 1.</summary>
        [JsonProperty("saturationMin", NullValueHandling = NullValueHandling.Ignore)] public float? SaturationMin;
        [JsonProperty("saturationMax", NullValueHandling = NullValueHandling.Ignore)] public float? SaturationMax;
        /// <summary>Signals that must hold; "!name" = must not. fixture (inside or at a small renderer), lifted (an open
        /// fire fixture), enclosed (inside a static lamp shade), orb (a statue orb's), scripted (a script drives it),
        /// animated. The offline census (tools/lights/census.py) knows none of them and reports such rules as "in game
        /// only".</summary>
        [JsonProperty("signals", NullValueHandling = NullValueHandling.Ignore)] public string[] Signals;
        [JsonProperty("class")] public string Class;
        [JsonProperty("set", NullValueHandling = NullValueHandling.Ignore)] public LightPolicy Set;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;

        [JsonIgnore] private Regex _scene, _path, _notPath;
        [JsonIgnore] private bool _compiled;

        private static Regex Rx(string s) => string.IsNullOrEmpty(s) ? null : new Regex(s, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public bool SceneMatches(string scene)
        {
            Compile();
            return _scene == null || _scene.IsMatch(scene ?? "");
        }

        private void Compile()
        {
            if (_compiled) return;
            _compiled = true;
            _scene = Rx(Scene); _path = Rx(Path); _notPath = Rx(NotPath);
        }

        public bool Matches(in LightFacts f)
        {
            Compile();
            if (Type != null && !string.Equals(Type, f.Type, StringComparison.OrdinalIgnoreCase)) return false;
            if (RangeMin.HasValue && f.Range < RangeMin.Value) return false;
            if (RangeMax.HasValue && f.Range > RangeMax.Value) return false;
            if (IntensityMin.HasValue && f.Intensity < IntensityMin.Value) return false;
            if (IntensityMax.HasValue && f.Intensity > IntensityMax.Value) return false;
            if (SaturationMin.HasValue && f.Saturation < SaturationMin.Value) return false;
            if (SaturationMax.HasValue && f.Saturation > SaturationMax.Value) return false;
            if (Signals != null)
                foreach (var s in Signals)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    bool not = s[0] == '!';
                    if (f.Has(not ? s.Substring(1) : s) == not) return false;
                }
            if (_path != null && !_path.IsMatch(f.Path)) return false;
            if (_notPath != null && _notPath.IsMatch(f.Path)) return false;
            return true;
        }
    }

    /// <summary>What a rule can test about a light.</summary>
    public struct LightFacts
    {
        public string Path, Type;
        public float Range, Intensity, Saturation;
        public bool Fixture, Lifted, Enclosed, Orb, Scripted, Animated;

        public bool Has(string signal)
        {
            switch (signal.ToLowerInvariant())
            {
                case "fixture": return Fixture;
                case "lifted": return Lifted;
                case "enclosed": return Enclosed;
                case "orb": return Orb;
                case "scripted": return Scripted;
                case "animated": return Animated;
                default: return false;
            }
        }

        public static float SaturationOf(Color c)
        {
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return mx > 1e-5f ? (mx - mn) / mx : 0f;
        }
    }

    /// <summary>
    /// A glowing surface as a light source: scales the light an emissive surface gives (its look to the camera and in
    /// reflections is not changed). The first matching rule wins. "Emissive before lights": where a room has glowing
    /// surfaces, they carry it and the floating lights the game used instead are dimmed or dropped.
    /// </summary>
    public sealed class LightEmitterRule
    {
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name;
        [JsonProperty("scene", NullValueHandling = NullValueHandling.Ignore)] public string Scene;
        /// <summary>Regex on the surface's material name; case-insensitive.</summary>
        [JsonProperty("material", NullValueHandling = NullValueHandling.Ignore)] public string Material;
        /// <summary>Regex on the name of its emission texture (the albedo, for albedo-as-emission materials).</summary>
        [JsonProperty("texture", NullValueHandling = NullValueHandling.Ignore)] public string Texture;
        [JsonProperty("scale")] public float Scale = 1f;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;

        [JsonIgnore] private Regex _scene, _material, _texture;
        [JsonIgnore] private bool _compiled;

        private void Compile()
        {
            if (_compiled) return;
            _compiled = true;
            Regex Rx(string s) => string.IsNullOrEmpty(s) ? null : new Regex(s, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            _scene = Rx(Scene); _material = Rx(Material); _texture = Rx(Texture);
        }

        public bool SceneMatches(string scene) { Compile(); return _scene == null || _scene.IsMatch(scene ?? ""); }

        public bool Matches(string material, string texture)
        {
            Compile();
            return (_material == null || _material.IsMatch(material ?? "")) && (_texture == null || _texture.IsMatch(texture ?? ""));
        }
    }

    /// <summary>lights\_rules.json: the classes' policies and the rules, for every level.</summary>
    public sealed class LightRulesFile
    {
        [JsonProperty("version")] public int Version = 1;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;
        [JsonProperty("emitters", NullValueHandling = NullValueHandling.Ignore)] public List<LightEmitterRule> Emitters;
        [JsonProperty("classes", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, LightPolicy> Classes;
        [JsonProperty("rules", NullValueHandling = NullValueHandling.Ignore)] public List<LightRule> Rules;
    }

    /// <summary>
    /// Light classes (docs/lighting-overrides.md, "Classes and rules"): every level light gets a class from the first
    /// matching rule (the level file's rules, then lights\_rules.json), and a class may carry a policy (globally in
    /// _rules.json, per level in the level file's "classes"). The result is a default for the light's own entry: an
    /// entry's fields win, field by field.
    /// </summary>
    public sealed class LightClasses
    {
        public const string RulesFileName = "_rules.json";

        /// <summary>Classes the code assigns before any rule (lights that move, or that another system owns).</summary>
        public const string Sun = "sun", SkyFill = "skyfill", Player = "player", Effect = "effect", Enemy = "enemy", Item = "item", Lava = "lava", Other = "other";

        private readonly ManualLogSource _log;
        private readonly List<LightRule> _global = new List<LightRule>();
        private readonly Dictionary<string, LightPolicy> _globalClasses = new Dictionary<string, LightPolicy>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LightRule> _active = new List<LightRule>();
        private readonly List<LightEmitterRule> _globalEmitters = new List<LightEmitterRule>();
        private readonly List<LightEmitterRule> _emitters = new List<LightEmitterRule>();
        /// <summary>Changes when the emitter rules in force do (the emissive triangle table is built with them).</summary>
        public string EmitterKey { get; private set; } = "";

        /// <summary>The scale on the light a surface gives (first matching emitter rule), 1 without one.</summary>
        public float EmitterScale(string material, string texture, out string rule)
        {
            foreach (var r in _emitters)
            {
                try { if (r.Matches(material, texture)) { rule = r.Name ?? "(unnamed)"; return Mathf.Max(0f, r.Scale); } }
                catch (ArgumentException e) { _log.LogError($"Emitter rule '{r.Name}': bad pattern: {e.Message}"); }
            }
            rule = null;
            return 1f;
        }
        private readonly Dictionary<string, LightPolicy> _levelClasses = new Dictionary<string, LightPolicy>(StringComparer.OrdinalIgnoreCase);
        private DateTime _stamp, _layerStamp;
        private string _path, _layerPath;
        public int GlobalRuleCount => _global.Count;
        public int ActiveRuleCount => _active.Count;

        public LightClasses(ManualLogSource log) { _log = log; }

        public static DateTime Stamp(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return DateTime.MinValue; }
        }

        public bool Changed => _path != null && (Stamp(_path) != _stamp || (_layerPath != null && Stamp(_layerPath) != _layerStamp));

        private void ReadRules(string path, bool first)
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                var f = JsonConvert.DeserializeObject<LightRulesFile>(File.ReadAllText(path));
                if (f?.Rules != null)
                {
                    var rules = f.Rules.Where(r => r != null && !string.IsNullOrEmpty(r.Class)).ToList();
                    if (first) _global.InsertRange(0, rules); else _global.AddRange(rules);
                }
                if (f?.Classes != null) foreach (var kv in f.Classes) if (kv.Value != null) _globalClasses[kv.Key] = kv.Value;
                if (f?.Emitters != null)
                {
                    var em = f.Emitters.Where(r => r != null).ToList();
                    if (first) _globalEmitters.InsertRange(0, em); else _globalEmitters.AddRange(em);
                }
            }
            catch (Exception e) { _log.LogError($"Light rules: can't read {path}: {e.Message}"); }
        }

        /// <summary>Reads _rules.json (and a layer's _rules.json over it: its rules come first, its class policies replace
        /// those of the same name) and sets the level's own rules and class policies (from its override files: the
        /// shipped one, then the user's, whose rules come first and whose policies win).</summary>
        public void Load(string rulesPath, string layerRulesPath, string scene, IEnumerable<LightOverrideFile> levelFiles)
        {
            _path = rulesPath;
            _stamp = Stamp(rulesPath);
            _layerPath = layerRulesPath;
            _layerStamp = layerRulesPath != null ? Stamp(layerRulesPath) : DateTime.MinValue;
            _global.Clear();
            _globalClasses.Clear();
            _globalEmitters.Clear();
            ReadRules(rulesPath, false);
            ReadRules(layerRulesPath, true);
            _active.Clear();
            _levelClasses.Clear();
            var level = new List<LightRule>();
            var levelEmitters = new List<LightEmitterRule>();
            foreach (var lf in levelFiles)
            {
                if (lf == null) continue;
                if (lf.Emitters != null) levelEmitters.InsertRange(0, lf.Emitters.Where(r => r != null));
                if (lf.Rules != null) level.InsertRange(0, lf.Rules.Where(r => r != null && !string.IsNullOrEmpty(r.Class)));
                if (lf.Classes != null) foreach (var kv in lf.Classes) if (kv.Value != null) _levelClasses[kv.Key] = kv.Value;
            }
            try
            {
                _active.AddRange(level.Where(r => r.SceneMatches(scene)));
                _active.AddRange(_global.Where(r => r.SceneMatches(scene)));
            }
            catch (ArgumentException e) { _log.LogError($"Light rules: bad pattern: {e.Message}"); }
            _emitters.Clear();
            try
            {
                _emitters.AddRange(levelEmitters.Where(r => r.SceneMatches(scene)));
                _emitters.AddRange(_globalEmitters.Where(r => r.SceneMatches(scene)));
            }
            catch (ArgumentException e) { _log.LogError($"Emitter rules: bad pattern: {e.Message}"); }
            EmitterKey = string.Join(";", _emitters.Select(r => $"{r.Material}|{r.Texture}|{r.Scale.ToString(CultureInfo.InvariantCulture)}"));
        }

        /// <summary>The first matching rule, or null.</summary>
        public LightRule Match(in LightFacts f)
        {
            foreach (var r in _active)
            {
                try { if (r.Matches(f)) return r; }
                catch (ArgumentException e) { _log.LogError($"Light rule '{r.Name}': bad pattern: {e.Message}"); }
            }
            return null;
        }

        /// <summary>A class's policy: "fixture:torch" takes the entry of that name, else the one of "fixture".</summary>
        private static LightPolicy Lookup(Dictionary<string, LightPolicy> d, string cls)
        {
            if (string.IsNullOrEmpty(cls)) return null;
            if (d.TryGetValue(cls, out var p)) return p;
            int colon = cls.IndexOf(':');
            return colon > 0 && d.TryGetValue(cls.Substring(0, colon), out p) ? p : null;
        }

        public LightPolicy GlobalPolicy(string cls) => Lookup(_globalClasses, cls);
        public LightPolicy LevelPolicy(string cls) => Lookup(_levelClasses, cls);

        /// <summary>true when <paramref name="cls"/> is <paramref name="name"/> or one of its kinds ("fixture" covers "fixture:lamp").</summary>
        public static bool ClassIs(string cls, string name) =>
            cls != null && name != null && (string.Equals(cls, name, StringComparison.OrdinalIgnoreCase) ||
                                            (cls.Length > name.Length && cls[name.Length] == ':' && cls.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
    }
}
