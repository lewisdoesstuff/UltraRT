using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// One light's override (docs/lighting-overrides.md). Every field is optional: absent = the game's value. Lights are
    /// identified by their hierarchy path with "(Clone)" removed plus their original world position, so checkpoint clones
    /// and rooms switched on later match the same entry.
    /// </summary>
    public sealed class LightOverrideEntry
    {
        [JsonProperty("path")] public string Path;
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name;
        [JsonProperty("pos")] public float[] Pos;
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string Type;   // the game light's type (information)
        /// <summary>Multiplier on the game's colour x intensity (gamma, like Light.intensity).</summary>
        [JsonProperty("intensity", NullValueHandling = NullValueHandling.Ignore)] public float? Intensity;
        /// <summary>Per-channel multiplier on the game's colour.</summary>
        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)] public float[] Color;
        /// <summary>Multiplier on the light's range.</summary>
        [JsonProperty("range", NullValueHandling = NullValueHandling.Ignore)] public float? Range;
        [JsonProperty("shadows", NullValueHandling = NullValueHandling.Ignore)] public bool? Shadows;
        [JsonProperty("disabled", NullValueHandling = NullValueHandling.Ignore)] public bool? Disabled;
        /// <summary>1 = relight's physical falloff, 0 = the game's own (LightPolicy.Reach).</summary>
        [JsonProperty("reach", NullValueHandling = NullValueHandling.Ignore)] public float? Reach;
        /// <summary>false: lights the air only, not surfaces (LightPolicy.Surfaces).</summary>
        [JsonProperty("surfaces", NullValueHandling = NullValueHandling.Ignore)] public bool? Surfaces;
        /// <summary>Multiplier on what the light scatters in the air (LightPolicy.Medium).</summary>
        [JsonProperty("medium", NullValueHandling = NullValueHandling.Ignore)] public float? Medium;
        /// <summary>point (no change), sphere, rect or disc.</summary>
        [JsonProperty("shape", NullValueHandling = NullValueHandling.Ignore)] public string Shape;
        /// <summary>Full size in metres: sphere / disc diameter = size[0]; rect width, height.</summary>
        [JsonProperty("size", NullValueHandling = NullValueHandling.Ignore)] public float[] Size;
        /// <summary>World-space emission direction of a rect / disc (default: a spot's forward, else straight down). On a
        /// spot without a shape: where the spot points (6-2's window spots aimed along the beams, docs/materials.md).</summary>
        [JsonProperty("normal", NullValueHandling = NullValueHandling.Ignore)] public float[] Normal;
        /// <summary>A spot's full cone angle in degrees (default: the game's spotAngle).</summary>
        [JsonProperty("angle", NullValueHandling = NullValueHandling.Ignore)] public float? Angle;
        /// <summary>World-space direction of a rect's width (default: derived from the normal).</summary>
        [JsonProperty("tangent", NullValueHandling = NullValueHandling.Ignore)] public float[] Tangent;
        /// <summary>World-space move in metres.</summary>
        [JsonProperty("offset", NullValueHandling = NullValueHandling.Ignore)] public float[] Offset;
        [JsonProperty("class", NullValueHandling = NullValueHandling.Ignore)] public string Class;
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string Source;
        [JsonProperty("confidence", NullValueHandling = NullValueHandling.Ignore)] public float? Confidence;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;

        [JsonIgnore] public string Origin; // "shipped" or "user"

        [JsonIgnore] public Vector3 Position => Pos != null && Pos.Length >= 3 ? new Vector3(Pos[0], Pos[1], Pos[2]) : Vector3.zero;
        [JsonIgnore] public string Key => LightOverrides.KeyOf(Path, Position);

        /// <summary>Changes nothing (an entry the user reset, or one that only carries a note).</summary>
        [JsonIgnore] public bool IsIdentity =>
            (Intensity ?? 1f) == 1f && (Color == null || Color.All(c => c == 1f)) && (Range ?? 1f) == 1f && Shadows == null && Disabled != true &&
            Reach == null && Surfaces == null && Medium == null && string.IsNullOrEmpty(Class) && Normal == null && Angle == null &&
            (string.IsNullOrEmpty(Shape) || Shape == "point") && !HasOffset;

        [JsonIgnore] public bool HasOffset => Offset != null && Offset.Length >= 3 && (Offset[0] != 0f || Offset[1] != 0f || Offset[2] != 0f);
        /// <summary>The entry decides where the light is (fixture lift and the enclosed-lamp radius don't apply).</summary>
        [JsonIgnore] public bool OwnsGeometry => HasOffset || LightOverrides.ShapeCode(Shape) != 0;

        public LightOverrideEntry Clone() => (LightOverrideEntry)MemberwiseClone();
    }

    /// <summary>A light that the game doesn't have: an area light bound to an emissive surface, a sky portal at a window.</summary>
    public sealed class LightAddEntry
    {
        [JsonProperty("id")] public string Id;
        /// <summary>area (default); spot (a spot light: "normal" is where it points, "angle" its full cone in degrees, default
        /// 30). "skyportal" is reserved for window portals (not implemented: packed as a plain area light).</summary>
        [JsonProperty("kind", NullValueHandling = NullValueHandling.Ignore)] public string Kind;
        [JsonProperty("angle", NullValueHandling = NullValueHandling.Ignore)] public float? Angle;
        [JsonProperty("pos")] public float[] Pos;
        [JsonProperty("shape", NullValueHandling = NullValueHandling.Ignore)] public string Shape;
        [JsonProperty("size", NullValueHandling = NullValueHandling.Ignore)] public float[] Size;
        [JsonProperty("normal", NullValueHandling = NullValueHandling.Ignore)] public float[] Normal;
        [JsonProperty("tangent", NullValueHandling = NullValueHandling.Ignore)] public float[] Tangent;
        /// <summary>Gamma colour (like Light.color).</summary>
        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)] public float[] Color;
        [JsonProperty("intensity", NullValueHandling = NullValueHandling.Ignore)] public float? Intensity;
        [JsonProperty("range", NullValueHandling = NullValueHandling.Ignore)] public float? Range;
        [JsonProperty("shadows", NullValueHandling = NullValueHandling.Ignore)] public bool? Shadows;
        /// <summary>Culling mask as hex ("FFFFFFFF" = every layer, the default).</summary>
        [JsonProperty("mask", NullValueHandling = NullValueHandling.Ignore)] public string Mask;
        /// <summary>Hierarchy path ("(Clone)" removed) of a renderer: the light is on only while that renderer is drawn.</summary>
        [JsonProperty("bind", NullValueHandling = NullValueHandling.Ignore)] public string Bind;
        [JsonProperty("disabled", NullValueHandling = NullValueHandling.Ignore)] public bool? Disabled;
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string Source;
        [JsonProperty("confidence", NullValueHandling = NullValueHandling.Ignore)] public float? Confidence;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;

        [JsonIgnore] public string Origin;
        [JsonIgnore] public Vector3 Position => Pos != null && Pos.Length >= 3 ? new Vector3(Pos[0], Pos[1], Pos[2]) : Vector3.zero;
        public LightAddEntry Clone() => (LightAddEntry)MemberwiseClone();
    }

    /// <summary>Level-wide exposure settings in a light override file ("exposure").</summary>
    public sealed class LevelExposure
    {
        /// <summary>The level's own RT brightness (AutoExposureSettings.rtBrightness, 0-1): the level is shown at least this
        /// bright, whatever the user's setting. For levels whose look was approved under the exposure policy before
        /// 2026-11-17 (P-2).</summary>
        [JsonProperty("rtBrightness", NullValueHandling = NullValueHandling.Ignore)] public float? RtBrightness;
        /// <summary>0-1: the level follows the game's brightness downwards (AutoExposureSettings.levelFollowGame).</summary>
        [JsonProperty("followGame", NullValueHandling = NullValueHandling.Ignore)] public float? FollowGame;
        /// <summary>0-1: the level follows the game's brightness upwards (AutoExposureSettings.levelFollowGameUp).</summary>
        [JsonProperty("followGameUp", NullValueHandling = NullValueHandling.Ignore)] public float? FollowGameUp;
        /// <summary>Strength of the game's light cones (LightPillar* materials, additive) while frames are traced
        /// (Plugin.Beams.cs): the cone is drawn by the game over the traced room, which is often brighter than the game's.</summary>
        [JsonProperty("coneGain", NullValueHandling = NullValueHandling.Ignore)] public float? ConeGain;
        /// <summary>Factor on the traced water's ripples in this level (TransparentSettings.waves; 0 = a flat mirror). For
        /// still water the user wants calm (8-2's pool, 2026-11-18).</summary>
        [JsonProperty("waterWaves", NullValueHandling = NullValueHandling.Ignore)] public float? WaterWaves;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;
    }

    public sealed class LightOverrideFile
    {
        [JsonProperty("version")] public int Version = 1;
        [JsonProperty("scene")] public string Scene;
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)] public string Note;
        [JsonProperty("exposure", NullValueHandling = NullValueHandling.Ignore)] public LevelExposure Exposure;
        /// <summary>This level's class policies (over lights\_rules.json's): "classes": { "fill": { "intensity": 0 } }.</summary>
        [JsonProperty("classes", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, LightPolicy> Classes;
        /// <summary>This level's own rules, tried before the global ones.</summary>
        [JsonProperty("rules", NullValueHandling = NullValueHandling.Ignore)] public List<LightRule> Rules;
        /// <summary>This level's emitter rules (LightEmitterRule), tried before the global ones.</summary>
        [JsonProperty("emitters", NullValueHandling = NullValueHandling.Ignore)] public List<LightEmitterRule> Emitters;
        [JsonProperty("lights")] public List<LightOverrideEntry> Lights = new List<LightOverrideEntry>();
        [JsonProperty("added")] public List<LightAddEntry> Added = new List<LightAddEntry>();
    }

    /// <summary>
    /// The per-level light override store: shipped defaults (plugin folder, lights\&lt;scene&gt;.json, from the repo's
    /// data\lights) under the user's own edits (BepInEx\ultrart\lights\&lt;scene&gt;.json, written by the in-game editor and
    /// never touched by a deploy). A user entry replaces the shipped entry of the same light; an empty user entry resets it
    /// to the game's values. Both files are re-read when they change on disk.
    /// </summary>
    public sealed class LightOverrides
    {
        private readonly ManualLogSource _log;
        public readonly string ShippedDir, UserDir;
        public string Scene { get; private set; }
        /// <summary>Bumped whenever the effective set changes (load, reload, edit): per-light caches drop.</summary>
        public int Version { get; private set; }

        private readonly Dictionary<string, LightOverrideEntry> _shipped = new Dictionary<string, LightOverrideEntry>();
        private readonly Dictionary<string, LightOverrideEntry> _user = new Dictionary<string, LightOverrideEntry>();
        private readonly Dictionary<string, LightAddEntry> _shippedAdded = new Dictionary<string, LightAddEntry>();
        private readonly Dictionary<string, LightAddEntry> _userAdded = new Dictionary<string, LightAddEntry>();
        private readonly Dictionary<string, List<LightOverrideEntry>> _byPath = new Dictionary<string, List<LightOverrideEntry>>();
        private readonly Dictionary<string, List<LightOverrideEntry>> _byName = new Dictionary<string, List<LightOverrideEntry>>();
        public readonly List<LightAddEntry> Added = new List<LightAddEntry>();
        private DateTime _shippedStamp, _userStamp;
        private float _nextCheck;
        public string ShippedNote { get; private set; }
        private LevelExposure _shippedExposure, _userExposure;
        /// <summary>The level's own RT brightness ("exposure": { "rtBrightness": F }; the user's file over the shipped
        /// one), or 0.</summary>
        public float LevelRtBrightness => Mathf.Clamp01((_userExposure?.RtBrightness ?? _shippedExposure?.RtBrightness) ?? 0f);
        public float LevelFollowGame => Mathf.Clamp01((_userExposure?.FollowGame ?? _shippedExposure?.FollowGame) ?? 0f);
        public float LevelFollowGameUp => Mathf.Clamp01((_userExposure?.FollowGameUp ?? _shippedExposure?.FollowGameUp) ?? 0f);
        public float LevelWaterWaves => Mathf.Clamp((_userExposure?.WaterWaves ?? _shippedExposure?.WaterWaves) ?? 1f, 0f, 2f);
        public float LevelConeGain => Mathf.Clamp((_userExposure?.ConeGain ?? _shippedExposure?.ConeGain) ?? 1f, 0f, 4f);

        /// <summary>Classes and rules (lights\_rules.json plus this level's own).</summary>
        public readonly LightClasses Classes;
        private LightOverrideFile _shippedFile, _userFile;
        /// <summary>A named layer over the shipped files (bench variant lc.layer=proposed): lights\&lt;layer&gt;\&lt;file&gt; stands
        /// in for lights\&lt;file&gt; where it exists. For reviewing a proposal against what ships.</summary>
        public string Layer { get; private set; }

        public bool SetLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) layer = null;
            if (layer == Layer) return false;
            Layer = layer;
            if (Scene != null) Load(Scene);
            return true;
        }

        private string Layered(string file)
        {
            if (Layer != null)
            {
                var p = Path.Combine(ShippedDir, Layer, file);
                if (File.Exists(p)) return p;
            }
            return Path.Combine(ShippedDir, file);
        }

        /// <summary>The rules for every level, and the layer's additions to them (null without a layer).</summary>
        public string RulesPath => Path.Combine(ShippedDir, LightClasses.RulesFileName);
        public string LayerRulesPath => Layer != null ? Path.Combine(ShippedDir, Layer, LightClasses.RulesFileName) : null;

        public int ShippedCount => _shipped.Count;
        public int UserCount => _user.Count;
        public int EffectiveCount { get; private set; }

        public LightOverrides(ManualLogSource log, string shippedDir, string userDir)
        {
            _log = log;
            ShippedDir = shippedDir;
            UserDir = userDir;
            Classes = new LightClasses(log);
        }

        public static string FileName(string scene) => scene + ".json";
        public string ShippedPath => Layered(FileName(Scene ?? ""));
        public string UserPath => Path.Combine(UserDir, FileName(Scene ?? ""));

        // ---------------------------------------------------------------- identity

        private static readonly Regex CloneRx = new Regex(@"\s*\(Clone\)", RegexOptions.Compiled);

        /// <summary>Hierarchy path with every "(Clone)" removed (checkpoints clone whole rooms in place).</summary>
        public static string NormalizedPath(Transform t)
        {
            var sb = new StringBuilder(CloneRx.Replace(t.name, ""));
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, CloneRx.Replace(p.name, "") + "/");
            return sb.ToString();
        }

        public static string NormalizedName(string n) => CloneRx.Replace(n ?? "", "");

        public static string KeyOf(string path, Vector3 pos) =>
            string.Format(CultureInfo.InvariantCulture, "{0}@{1:F1},{2:F1},{3:F1}", path, pos.x, pos.y, pos.z);

        /// <summary>Matching tolerances (m): an entry with the same path matches within PathTolerance (the nearest wins);
        /// without one, an entry with the same name within NameTolerance.</summary>
        public const float PathTolerance = 2f, NameTolerance = 0.5f;

        /// <summary>The effective entry for a light at <paramref name="originalPos"/>, or null.</summary>
        public LightOverrideEntry Find(string path, string name, Vector3 originalPos)
        {
            LightOverrideEntry best = null;
            float bestD = float.MaxValue;
            if (_byPath.TryGetValue(path, out var list))
                foreach (var e in list)
                {
                    float d = (e.Position - originalPos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = e; }
                }
            if (best != null && bestD <= PathTolerance * PathTolerance) return best;
            best = null;
            bestD = float.MaxValue;
            if (_byName.TryGetValue(NormalizedName(name), out list))
                foreach (var e in list)
                {
                    float d = (e.Position - originalPos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = e; }
                }
            return best != null && bestD <= NameTolerance * NameTolerance ? best : null;
        }

        /// <summary>Every effective entry (user entries over shipped ones).</summary>
        public IEnumerable<LightOverrideEntry> AllEffective() => _byPath.Values.SelectMany(l => l);

        public LightOverrideEntry UserEntry(string key) => _user.TryGetValue(key, out var e) ? e : null;
        public LightOverrideEntry ShippedEntry(string key) => _shipped.TryGetValue(key, out var e) ? e : null;
        public bool HasUserAdded(string id) => _userAdded.ContainsKey(id);

        // ---------------------------------------------------------------- loading

        /// <summary>Switch to a scene's files (call on every scene change).</summary>
        public void Load(string scene)
        {
            Scene = scene;
            _shipped.Clear(); _user.Clear(); _shippedAdded.Clear(); _userAdded.Clear();
            ShippedNote = null;
            _shippedExposure = _userExposure = null;
            _shippedFile = _userFile = null;
            _shippedStamp = Stamp(ShippedPath);
            _userStamp = Stamp(UserPath);
            ReadInto(ShippedPath, "shipped", _shipped, _shippedAdded, true);
            ReadInto(UserPath, "user", _user, _userAdded, false);
            Classes.Load(RulesPath, LayerRulesPath, scene, new[] { _shippedFile, _userFile });
            Rebuild();
            if (_shipped.Count + _user.Count + _shippedAdded.Count + _userAdded.Count > 0 || LevelRtBrightness > 0f || Classes.ActiveRuleCount > 0)
                _log.LogInfo($"Light overrides '{scene}'{(Layer != null ? $" (layer {Layer})" : "")}: {_shipped.Count} shipped + {_shippedAdded.Count} added ({ShippedPath}), {_user.Count} user + {_userAdded.Count} added ({UserPath}); {EffectiveCount} effective; {Classes.ActiveRuleCount} rules ({RulesPath})" + (LevelRtBrightness > 0f ? $"; level RT brightness {LevelRtBrightness:F2}" : ""));
        }

        /// <summary>Once a second: re-read a file that changed on disk (hand edits, a deploy).</summary>
        public bool CheckReload()
        {
            if (Scene == null || Time.realtimeSinceStartup < _nextCheck) return false;
            _nextCheck = Time.realtimeSinceStartup + 1f;
            var s = Stamp(ShippedPath);
            var u = Stamp(UserPath);
            bool rules = Classes.Changed;
            if (s == _shippedStamp && u == _userStamp && !rules) return false;
            // Let a writer finish.
            var newest = rules ? LightClasses.Stamp(RulesPath) : s > u ? s : u;
            if ((DateTime.UtcNow - newest).TotalSeconds < 0.3) { _nextCheck = Time.realtimeSinceStartup + 0.3f; return false; }
            _log.LogInfo($"Light overrides: {(rules ? RulesPath : s != _shippedStamp ? ShippedPath : UserPath)} changed, reloading");
            Load(Scene);
            return true;
        }

        private static DateTime Stamp(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return DateTime.MinValue; }
        }

        private void ReadInto(string path, string origin, Dictionary<string, LightOverrideEntry> into, Dictionary<string, LightAddEntry> added, bool shipped)
        {
            if (!File.Exists(path)) return;
            try
            {
                var f = JsonConvert.DeserializeObject<LightOverrideFile>(File.ReadAllText(path));
                if (f == null) return;
                if (shipped) { ShippedNote = f.Note; _shippedExposure = f.Exposure; _shippedFile = f; }
                else { _userExposure = f.Exposure; _userFile = f; }
                foreach (var e in f.Lights ?? new List<LightOverrideEntry>())
                {
                    if (string.IsNullOrEmpty(e.Path) || e.Pos == null || e.Pos.Length < 3) continue;
                    e.Origin = origin;
                    into[e.Key] = e;
                }
                foreach (var a in f.Added ?? new List<LightAddEntry>())
                {
                    if (string.IsNullOrEmpty(a.Id) || a.Pos == null || a.Pos.Length < 3) continue;
                    a.Origin = origin;
                    added[a.Id] = a;
                }
            }
            catch (Exception e) { _log.LogError($"Light overrides: can't read {path}: {e.Message}"); }
        }

        private void Rebuild()
        {
            _byPath.Clear();
            _byName.Clear();
            int n = 0;
            foreach (var kv in _shipped.Concat(_user))
            {
                // A user entry replaces the shipped one with the same key.
                if (kv.Value.Origin == "shipped" && _user.ContainsKey(kv.Key)) continue;
                var e = kv.Value;
                if (!e.IsIdentity) n++;
                if (!_byPath.TryGetValue(e.Path, out var l)) _byPath[e.Path] = l = new List<LightOverrideEntry>(1);
                l.Add(e);
                var name = NormalizedName(e.Name ?? e.Path.Substring(e.Path.LastIndexOf('/') + 1));
                if (!_byName.TryGetValue(name, out l)) _byName[name] = l = new List<LightOverrideEntry>(1);
                l.Add(e);
            }
            EffectiveCount = n;
            Added.Clear();
            foreach (var kv in _shippedAdded) if (!_userAdded.ContainsKey(kv.Key)) Added.Add(kv.Value);
            Added.AddRange(_userAdded.Values);
            Version++;
        }

        // ---------------------------------------------------------------- editing (user layer)

        /// <summary>The user entry for a light, created as a copy of the effective one (or blank) on first edit.</summary>
        public LightOverrideEntry EditEntry(string path, string name, Vector3 originalPos, string type)
        {
            var key = KeyOf(path, originalPos);
            if (_user.TryGetValue(key, out var u)) return u;
            var eff = Find(path, name, originalPos);
            u = eff != null ? eff.Clone() : new LightOverrideEntry();
            u.Path = path;
            u.Name = name;
            u.Pos = new[] { Round(originalPos.x), Round(originalPos.y), Round(originalPos.z) };
            u.Type = type;
            u.Origin = "user";
            u.Source = "user";
            // A shipped entry whose key differs (matched by tolerance) is shadowed by giving the user entry its key.
            if (eff != null && eff.Key != u.Key && eff.Origin == "shipped") u.Pos = (float[])eff.Pos.Clone();
            _user[u.Key] = u;
            Rebuild();
            return u;
        }

        /// <summary>Drops the user's entry for the light (back to the shipped entry, if any).</summary>
        public bool ResetUser(string key)
        {
            bool removed = _user.Remove(key);
            if (removed) Rebuild();
            return removed;
        }

        /// <summary>Marks the user's entry for the light as identity (the game's values, ignoring a shipped entry).</summary>
        public void ResetToGame(LightOverrideEntry u)
        {
            u.Intensity = null; u.Color = null; u.Range = null; u.Shadows = null; u.Disabled = null; u.Shape = null; u.Size = null;
            u.Reach = null; u.Surfaces = null; u.Medium = null;
            u.Normal = null; u.Angle = null; u.Tangent = null; u.Offset = null; u.Class = null; u.Confidence = null; u.Note = "reset to the game's values";
            Rebuild();
        }

        public void Touched() => Rebuild();

        public LightAddEntry AddUserLight(LightAddEntry a)
        {
            a.Origin = "user";
            a.Source = a.Source ?? "user";
            _userAdded[a.Id] = a;
            Rebuild();
            return a;
        }

        public LightAddEntry EditAdded(LightAddEntry a)
        {
            if (a.Origin == "user") return a;
            var u = a.Clone();
            u.Origin = "user";
            u.Source = "user";
            _userAdded[u.Id] = u;
            Rebuild();
            return u;
        }

        public bool RemoveUserAdded(string id)
        {
            bool removed = _userAdded.Remove(id);
            if (removed) Rebuild();
            return removed;
        }

        /// <summary>Writes the user layer. Returns the path, or null on failure.</summary>
        public string SaveUser()
        {
            if (Scene == null) return null;
            try
            {
                Directory.CreateDirectory(UserDir);
                var f = new LightOverrideFile
                {
                    Scene = Scene,
                    Note = "UltraRT light overrides edited in game (docs/lighting-overrides.md). Entries here replace the shipped ones for the same light.",
                    Exposure = _userExposure,
                    Classes = _userFile?.Classes,
                    Rules = _userFile?.Rules,
                    Emitters = _userFile?.Emitters,
                    Lights = _user.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList(),
                    Added = _userAdded.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
                };
                File.WriteAllText(UserPath, Serialize(f));
                _userStamp = Stamp(UserPath);
                return UserPath;
            }
            catch (Exception e)
            {
                _log.LogError($"Light overrides: can't write {UserPath}: {e.Message}");
                return null;
            }
        }

        public static string Serialize(LightOverrideFile f) =>
            JsonConvert.SerializeObject(f, Formatting.Indented, new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture });

        public static float Round(float v) => (float)Math.Round(v, 3);

        // ---------------------------------------------------------------- packing (UltraRTLights.hlsl layout)

        public static int ShapeCode(string shape)
        {
            switch ((shape ?? "").ToLowerInvariant())
            {
                case "sphere": return 1;
                case "rect": return 2;
                case "disc": return 3;
                default: return 0;
            }
        }

        public static readonly string[] ShapeNames = { "point", "sphere", "rect", "disc" };

        public static Vector3 Vec(float[] a, Vector3 fallback) => a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : fallback;

        /// <summary>Default rect width axis: horizontal, perpendicular to the normal.</summary>
        public static Vector3 DefaultTangent(Vector3 n)
        {
            var t = Mathf.Abs(n.y) < 0.99f ? Vector3.Cross(Vector3.up, n) : Vector3.right;
            return t.normalized;
        }

        /// <summary>
        /// Packs an area light into _Lights[i*4+2] and [i*4+3] (UltraRTLights.hlsl). halfX / halfY are half sizes (sphere and
        /// disc: the radius in halfX).
        /// </summary>
        public static void PackArea(int shape, Vector3 normal, Vector3 tangent, float halfX, float halfY, float unshadowed, float w3, out Vector4 l2, out Vector4 l3)
        {
            normal = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.down;
            tangent = tangent - normal * Vector3.Dot(tangent, normal);
            tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : DefaultTangent(normal);
            uint code = (uint)shape | (OctEncode10(tangent) << 2);
            l2 = new Vector4(normal.x, normal.y, normal.z, Mathf.Max(halfX, 0.005f));
            l3 = new Vector4(-2f - Mathf.Max(halfY, 0f), code, unshadowed, w3); // w3: unused slot, kept as passed
        }

        /// <summary>The tangent of a packed area light (l3.y's code), as UltraRTLights.hlsl LightAreaTangent decodes it.</summary>
        public static Vector3 AreaTangent(uint code)
        {
            uint oct = code >> 2;
            float ex = ((oct & 1023u) + 0.5f) * (2f / 1024f) - 1f, ey = (((oct >> 10) & 1023u) + 0.5f) * (2f / 1024f) - 1f;
            var v = new Vector3(ex, ey, 1f - Mathf.Abs(ex) - Mathf.Abs(ey));
            if (v.z < 0f) { float ox = (1f - Mathf.Abs(v.y)) * (v.x >= 0f ? 1f : -1f), oy = (1f - Mathf.Abs(v.x)) * (v.y >= 0f ? 1f : -1f); v.x = ox; v.y = oy; }
            return v.normalized;
        }

        private static uint OctEncode10(Vector3 v)
        {
            float s = Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z);
            float x = v.x / s, y = v.y / s;
            if (v.z < 0f)
            {
                float ox = (1f - Mathf.Abs(y)) * (x >= 0f ? 1f : -1f);
                float oy = (1f - Mathf.Abs(x)) * (y >= 0f ? 1f : -1f);
                x = ox; y = oy;
            }
            uint ux = (uint)Mathf.Clamp(Mathf.FloorToInt((x * 0.5f + 0.5f) * 1024f), 0, 1023);
            uint uy = (uint)Mathf.Clamp(Mathf.FloorToInt((y * 0.5f + 0.5f) * 1024f), 0, 1023);
            return ux | (uy << 10);
        }
    }
}
