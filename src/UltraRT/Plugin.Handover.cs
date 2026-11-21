using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// "Emissive over lights" for fixtures (docs/lighting-overrides.md, "Handover"): a light whose rule has a "handover"
    /// gives its output to the glowing part of its fixture. Here: which lights have such a part, and what each part is
    /// to emit (EmissiveLights.PowerOf scales the part's triangles in the emissive triangle table to that; the light's
    /// strength becomes the rule's "keep", BuildResolved).
    ///
    /// The output: a point light under relight's physical curve gives c x K x r^2 / d^2 (c = (colour x intensity)^2.2,
    /// r = range, K = 0.003205: UltraRTLights.hlsl), an emitter Le x area x (lightScale / pi) x cos / d^2 with
    /// lightScale 4. Equal total output: sum(Le x area) = pi x K x r^2 x c, times a spot's share of the sphere. That is
    /// the light's inverse-square curve at every distance, which is more than the game's short falloff shows; the
    /// fixtures tuned by eye sit at LightClasses.HandoverGain of it.
    /// </summary>
    public partial class Plugin
    {
        private sealed class HandedLight
        {
            public LightRule Rule;
            public int Parts;
            public bool Baked;
            /// <summary>Its parts as EmissiveLights.PowerFulfilled names them.</summary>
            public readonly List<object> Keys = new List<object>();
            public readonly List<KeyValuePair<Transform, string>> ObjectParts = new List<KeyValuePair<Transform, string>>();
        }

        private readonly Dictionary<Light, HandedLight> _handed = new Dictionary<Light, HandedLight>();
        private readonly Dictionary<KeyValuePair<Transform, string>, Vector3> _handPower = new Dictionary<KeyValuePair<Transform, string>, Vector3>();
        /// <summary>Per part: the share of its output the volumetric pass scatters (the handed lights' "medium", weighted by
        /// their output). EmissiveLights.PowerOf's w is 1 + this.</summary>
        private readonly Dictionary<KeyValuePair<Transform, string>, float> _handAir = new Dictionary<KeyValuePair<Transform, string>, float>();
        /// <summary>Baked parts per material: bounds, output (xyz) and air share (w).</summary>
        private readonly Dictionary<string, List<KeyValuePair<Bounds, Vector4>>> _handBaked = new Dictionary<string, List<KeyValuePair<Bounds, Vector4>>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Material names of every handed-over part (EmissiveLights.IsPowerPart).</summary>
        private readonly HashSet<string> _handParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private SceneProxies _handScene;
        private bool _handBuilding;
        /// <summary>What the handover was last built for (EnsureHandover). Compared field by field: the check runs for every
        /// emissive material test and every handed light's resolve, so it must not allocate.</summary>
        private string _handKeyScene;
        private int _handKeyOvVersion = -1, _handKeyToggle = -1, _handKeyLights = -1;
        private bool _handKeyOverrides, _handKeyOn;
        private float _handKeyGain = float.NaN, _handKeyTint = float.NaN;
        /// <summary>
        /// The light list as the handover sees it: <see cref="_handLightsVersion"/> changes only when a light whose rule has a
        /// handover appears (HandoverLightsChanged). It used to be the list's length, and combat changes that all the time (every explosion,
        /// beam and projectile brings a light): 8-3 rebuilt the whole handover 20 times in 30 s of fighting, ~100 ms each,
        /// inside EmissiveLights' "new" time (2026-11-21, docs/performance.md "Combat hitches").
        /// </summary>
        private int _handLightsVersion, _handLightsCount = -1;
        private readonly HashSet<Light> _handSeen = new HashSet<Light>();
        private readonly HashSet<Light> _handRuled = new HashSet<Light>();
        /// <summary>Full handover builds since the last periodic log (Plugin.cs's scene sync line).</summary>
        private int _handBuilds;
        /// <summary>Bench variants lc.handover=0 (the lights stay, no part is scaled) and lc.hgain=F (on every gain).</summary>
        private bool _handoverOn = true;
        private float _handoverGain = 1f;
        /// <summary>
        /// A fullbright part (not emissive in the game) whose lights all have one strong colour shows that colour: its proxy
        /// albedo x lerp(white, the lights' chroma, this). P-2's street lamps light the street red from white bulbs (user,
        /// 2026-11-20: "should the lamps appear white if they're giving off red light?"). 0 = the game's white bulb.
        /// Bench plugin._handoverTint=F, -ultrart-handover-tint F. docs/lighting-overrides.md, "Handover", "Part colour".
        /// </summary>
        private float _handoverTint = 0.85f;
        /// <summary>Parts whose lights' chroma (max channel 1) has a smaller channel at most this: "one strong colour".</summary>
        private const float HandoverTintMinChroma = 0.5f;
        /// <summary>The lights of one part material agree when no channel of their chroma differs by more than this.</summary>
        private const float HandoverTintSpread = 0.15f;

        /// <summary>Per part material name: the handed lights' chroma range and the renderers that carry them.</summary>
        private sealed class PartTint
        {
            public Vector3 Min = Vector3.one * 9f, Max = Vector3.one * -9f, Sum;
            public float Weight;
            public readonly HashSet<Material> Sources = new HashSet<Material>();
            public readonly HashSet<Renderer> Renderers = new HashSet<Renderer>();
        }
        private readonly Dictionary<string, PartTint> _handTintParts = new Dictionary<string, PartTint>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Source material -> tint still to apply (its proxy material may not exist yet).</summary>
        private readonly List<KeyValuePair<Material, Color>> _handTintPending = new List<KeyValuePair<Material, Color>>();
        /// <summary>Proxy material -> its colour before the tint (restored when the handover is rebuilt).</summary>
        private readonly Dictionary<Material, Color> _handTinted = new Dictionary<Material, Color>();
        private Dictionary<Material, int> _handMatUsers;
        private SceneProxies _handMatUsersScene;

        private string HandoverVariantKey => (_handoverOn ? "on" : "off") + _handoverGain.ToString(CultureInfo.InvariantCulture) + "t" + _handoverTint.ToString(CultureInfo.InvariantCulture);

        private const float HandoverK = 0.003205f; // relight's physical curve (UltraRTLights.hlsl, _RelightLinearK's default)
        private const float HandoverMaxGamma = 3f; // colour x intensity above this only widens the white patch around the game's light: an emitter would flood the room with it

        /// <summary>The fixture of a light for a handover: renderers under the nearest ancestors (three levels up at most;
        /// more than 24 renderers is a room) whose material the rule names. One entry per renderer and material.</summary>
        private static void HandoverParts(Light l, LightHandover h, List<KeyValuePair<Renderer, string>> parts)
        {
            parts.Clear();
            var t = l.transform.parent;
            for (int up = 0; up < 3 && t != null; up++, t = t.parent)
            {
                var rs = t.GetComponentsInChildren<MeshRenderer>(true);
                if (rs.Length == 0) continue;
                if (rs.Length > 24) return;
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    string last = null;
                    // Baked into the StaticSceneOptimizer atlas: the renderer shows the atlas material; the part is its original.
                    var mats = r.sharedMaterials;
                    if (OriginalMaterials.IsCaptured(r) && OriginalMaterials.TryGet(r, out var orig, out _, out _) && orig != null) mats = orig;
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        string n = CleanMaterialName(m.name);
                        if (n == last || !h.PartMatches(n)) continue;
                        if (!parts.Any(p => ReferenceEquals(p.Key, r) && p.Value == n)) parts.Add(new KeyValuePair<Renderer, string>(r, n));
                        last = n;
                    }
                }
                if (parts.Count > 0) return;
            }
        }

        /// <summary>What the light gives as sum(Le x area) of an emitter, with the class policy it would have had.</summary>
        private Vector3 HandoverPowerOf(Light l, LightResolved rb, LightHandover h)
        {
            float f = rb.Intensity * rb.Toggle;
            var c = l.color * l.intensity;
            var g = new Vector3(c.r * rb.Color.x * f, c.g * rb.Color.y * f, c.b * rb.Color.z * f);
            float mx = Mathf.Max(g.x, Mathf.Max(g.y, g.z));
            if (mx > HandoverMaxGamma) g *= HandoverMaxGamma / mx;
            var lin = new Vector3(Mathf.Pow(Mathf.Max(g.x, 0f), 2.2f), Mathf.Pow(Mathf.Max(g.y, 0f), 2.2f), Mathf.Pow(Mathf.Max(g.z, 0f), 2.2f));
            float r = Mathf.Max(l.range * rb.Range, 0.01f);
            float cone = l.type == LightType.Spot ? 0.5f * (1f - Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad)) : 1f;
            float gain = LightClasses.HandoverGain * (h.Gain > 0f ? h.Gain : 1f) * _handoverGain;
            return lin * (gain * Mathf.PI * HandoverK * r * r * cone);
        }

        /// <summary>Builds the handover state for the scene's lights when the rules, the review switches, the scene or the
        /// light list changed.</summary>
        private void EnsureHandover()
        {
            if (_handBuilding || _lightOv == null) return;
            bool newScene = !ReferenceEquals(_handScene, _proxies); // rebuilt below, which fills the light sets
            int count = _proxies != null ? _proxies.Lights.Count : 0;
            if (count != _handLightsCount)
            {
                _handLightsCount = count;
                if (!newScene && HandoverLightsChanged()) _handLightsVersion++;
            }
            if (ReferenceEquals(_handScene, _proxies) && _handKeyScene == _lightOv.Scene && _handKeyOvVersion == _lightOv.Version &&
                _handKeyToggle == _toggleVersion && _handKeyLights == _handLightsVersion && _handKeyOverrides == _lightOverrides &&
                _handKeyOn == _handoverOn && _handKeyGain == _handoverGain && _handKeyTint == _handoverTint) return;
            _handKeyScene = _lightOv.Scene;
            _handKeyOvVersion = _lightOv.Version;
            _handKeyToggle = _toggleVersion;
            _handKeyLights = _handLightsVersion;
            _handKeyOverrides = _lightOverrides;
            _handKeyOn = _handoverOn;
            _handKeyGain = _handoverGain;
            _handKeyTint = _handoverTint;
            _handScene = _proxies;
            _handBuilds++;
            _handed.Clear();
            _handPower.Clear();
            _handAir.Clear();
            _handBaked.Clear();
            _handParts.Clear();
            RestoreHandoverTints();
            _handSeen.Clear();
            _handRuled.Clear();
            if (_proxies == null || !_lightOverrides || !_handoverOn || !_lightOv.Classes.HasHandovers) return;
            _handBuilding = true;
            try
            {
                var parts = new List<KeyValuePair<Renderer, string>>();
                var perRule = new Dictionary<string, int[]>(); // rule -> lights handed, hybrid, without a part
                foreach (var l in _proxies.Lights)
                {
                    if (l == null) continue;
                    _handSeen.Add(l);
                    if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                    var rb = BuildResolved(l, false);
                    var rule = rb.RuleObject;
                    var h = rule?.Handover;
                    if (h == null) continue;
                    _handRuled.Add(l);
                    string rn = rule.Name ?? "(unnamed)";
                    if (!perRule.TryGetValue(rn, out var cnt)) perRule[rn] = cnt = new int[3];
                    if (rb.Disabled || rb.Intensity <= 0f) continue; // off by policy: nothing to hand over
                    HandoverParts(l, h, parts);
                    if (parts.Count == 0) { cnt[2]++; continue; }
                    var share = HandoverPowerOf(l, rb, h) / parts.Count;
                    // What the light would have scattered in the air: its class's (or entry's) medium. The punch policy
                    // boosts fixtures x2.5 with medium 0.4 so their haze stays where it was; the part must keep that cut.
                    float air = Mathf.Max(0f, rb.Medium);
                    bool baked = false;
                    var hl = new HandedLight { Rule = rule, Parts = parts.Count };
                    var lc = new Vector3(l.color.r, l.color.g, l.color.b);
                    float lmax = Mathf.Max(lc.x, Mathf.Max(lc.y, lc.z));
                    foreach (var p in parts)
                    {
                        _handParts.Add(p.Value);
                        if (lmax > 1e-4f) NoteTintPart(p.Key, p.Value, lc / lmax, share.x * 0.2126f + share.y * 0.7152f + share.z * 0.0722f);
                        if (p.Key.isPartOfStaticBatch || (p.Key is MeshRenderer pm && OriginalMaterials.IsCaptured(pm)))
                        {
                            // Baked into a static batch: no object of its own in the RT scene. Its world bounds
                            // (a little padded) pick its triangles out of the batch.
                            baked = true;
                            if (!_handBaked.TryGetValue(p.Value, out var list)) _handBaked[p.Value] = list = new List<KeyValuePair<Bounds, Vector4>>();
                            var bb = p.Key.bounds;
                            bb.Expand(Mathf.Max(0.04f, bb.size.magnitude * 0.05f));
                            int at = list.FindIndex(x => x.Key.center == bb.center && x.Key.size == bb.size);
                            if (at >= 0)
                            {
                                var o = list[at].Value;
                                var sum = new Vector3(o.x, o.y, o.z) + share;
                                list[at] = new KeyValuePair<Bounds, Vector4>(bb, new Vector4(sum.x, sum.y, sum.z, MixAir(new Vector3(o.x, o.y, o.z), o.w, share, air)));
                            }
                            else { at = list.Count; list.Add(new KeyValuePair<Bounds, Vector4>(bb, new Vector4(share.x, share.y, share.z, air))); }
                            hl.Keys.Add(new KeyValuePair<object, int>(list, at));
                        }
                        else
                        {
                            var k = new KeyValuePair<Transform, string>(p.Key.transform, p.Value);
                            bool had = _handPower.TryGetValue(k, out var b);
                            _handAir.TryGetValue(k, out var ba);
                            _handAir[k] = had ? MixAir(b, ba, share, air) : air;
                            _handPower[k] = b + share;
                            hl.ObjectParts.Add(k);
                        }
                    }
                    hl.Baked = baked;
                    _handed[l] = hl;
                    cnt[h.Keep > 0f ? 1 : 0]++;
                }
                PlanHandoverTints();
                if (perRule.Count > 0)
                    Logger.LogInfo($"Handover ({_lightOv.Scene}): {_handed.Count} lights to {_handPower.Count} parts and {_handBaked.Values.Sum(x => x.Count)} baked parts; " +
                                   string.Join("; ", perRule.Select(kv => $"'{kv.Key}' {kv.Value[0]} handed, {kv.Value[1]} hybrid, {kv.Value[2]} without a part")));
            }
            catch (Exception e) { Logger.LogWarning($"Handover: failed, the lights stay: {e.Message}"); _handed.Clear(); _handPower.Clear(); _handAir.Clear(); _handBaked.Clear(); _handParts.Clear(); _handTintPending.Clear(); }
            finally { _handBuilding = false; _handTintParts.Clear(); }
        }

        /// <summary>The light list changed length: true when a light with a handover rule is new (a fixture light of a room
        /// the level clones or switches in), false for everything else (combat's explosions, beams, projectiles). Only the
        /// lights not seen before are resolved.
        /// A handed light that is destroyed (a lamp shot in a fight: 8-3 lost two in the 39 s combat run) is no reason to
        /// rebuild either. Its part's triangles already carry their output in the emissive table (EmissiveLights.ApplyPower
        /// runs once per extract), so a rebuild changed nothing on screen and cost ~120 ms.</summary>
        private bool HandoverLightsChanged()
        {
            if (_proxies == null || !_lightOverrides || !_handoverOn || !_lightOv.Classes.HasHandovers) return false;
            bool changed = false;
            _handRuled.RemoveWhere(l => l == null);
            _handSeen.RemoveWhere(l => l == null);
            foreach (var l in _proxies.Lights)
            {
                if (l == null || !_handSeen.Add(l)) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                if (BuildResolved(l, false).RuleObject?.Handover == null) continue;
                _handRuled.Add(l);
                changed = true;
            }
            return changed;
        }

        private void NoteTintPart(Renderer r, string name, Vector3 chroma, float weight)
        {
            if (!_handTintParts.TryGetValue(name, out var t)) _handTintParts[name] = t = new PartTint();
            t.Min = Vector3.Min(t.Min, chroma);
            t.Max = Vector3.Max(t.Max, chroma);
            float w = Mathf.Max(weight, 1e-6f);
            t.Sum += chroma * w;
            t.Weight += w;
            t.Renderers.Add(r);
            foreach (var m in r.sharedMaterials)
                if (m != null && CleanMaterialName(m.name) == name) t.Sources.Add(m);
        }

        /// <summary>
        /// Which part materials take their lights' colour (<see cref="_handoverTint"/>): fullbright (not emissive in the game),
        /// every handed light of one strong colour, and no renderer in the scene uses the material that is not such a part
        /// (1-4's statue orbs share the street lamps' <c>Light</c>: a tint per material would colour them too).
        /// </summary>
        private void PlanHandoverTints()
        {
            if (_handoverTint <= 0f || _handTintParts.Count == 0) return;
            if (_handMatUsers == null || !ReferenceEquals(_handMatUsersScene, _proxies))
            {
                _handMatUsersScene = _proxies;
                _handMatUsers = new Dictionary<Material, int>();
                foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (r == null || SceneProxies.IsProxyObject(r.gameObject)) continue;
                    foreach (var m in r.sharedMaterials)
                        if (m != null) { _handMatUsers.TryGetValue(m, out int n); _handMatUsers[m] = n + 1; }
                }
            }
            var notes = new List<string>();
            foreach (var kv in _handTintParts)
            {
                var t = kv.Value;
                var spread = t.Max - t.Min;
                var chroma = t.Sum / Mathf.Max(t.Weight, 1e-6f);
                float mn = Mathf.Min(chroma.x, Mathf.Min(chroma.y, chroma.z));
                string why = null;
                if (Mathf.Max(spread.x, Mathf.Max(spread.y, spread.z)) > HandoverTintSpread) why = "lights of several colours";
                else if (mn > HandoverTintMinChroma) why = $"pale light (min channel {mn:F2})";
                else
                    foreach (var m in t.Sources)
                    {
                        if (m.IsKeywordEnabled("EMISSIVE") || m.IsKeywordEnabled("_EMISSION")) { why = "emissive in the game"; break; }
                        int users = _handMatUsers.TryGetValue(m, out int u) ? u : 0;
                        if (users > t.Renderers.Count) { why = $"{users - t.Renderers.Count} other renderer(s) use '{m.name}'"; break; }
                    }
                if (why != null) { notes.Add($"'{kv.Key}' kept ({why})"); continue; }
                var tint = Vector3.Lerp(Vector3.one, chroma, Mathf.Clamp01(_handoverTint));
                var c = new Color(tint.x, tint.y, tint.z, 1f);
                foreach (var m in t.Sources) _handTintPending.Add(new KeyValuePair<Material, Color>(m, c));
                notes.Add($"'{kv.Key}' x ({tint.x:F2}, {tint.y:F2}, {tint.z:F2}) on {t.Renderers.Count} part(s)");
            }
            if (notes.Count > 0) Logger.LogInfo($"Handover ({_lightOv.Scene}) part colour: " + string.Join("; ", notes));
            ApplyHandoverTints();
        }

        /// <summary>Tints the proxy materials of planned parts once they exist (a few entries, tried while any is left).</summary>
        private void ApplyHandoverTints()
        {
            if (_handTintPending.Count == 0 || _proxies == null) return;
            for (int i = _handTintPending.Count - 1; i >= 0; i--)
            {
                var kv = _handTintPending[i];
                if (kv.Key == null) { _handTintPending.RemoveAt(i); continue; }
                var pm = _proxies.ProxyMaterialOf(kv.Key);
                if (pm == null || !pm.HasProperty("_Color")) continue;
                if (!_handTinted.ContainsKey(pm))
                {
                    var o = pm.GetColor("_Color");
                    _handTinted[pm] = o;
                    pm.SetColor("_Color", new Color(o.r * kv.Value.r, o.g * kv.Value.g, o.b * kv.Value.b, o.a));
                }
                _handTintPending.RemoveAt(i);
            }
        }

        private void RestoreHandoverTints()
        {
            foreach (var kv in _handTinted)
                if (kv.Key != null) kv.Key.SetColor("_Color", kv.Value);
            _handTinted.Clear();
            _handTintPending.Clear();
        }

        /// <summary>The air share of a part that already gives <paramref name="had"/> at <paramref name="hadAir"/> and takes
        /// <paramref name="add"/> at <paramref name="addAir"/> more: the mean weighted by output.</summary>
        private static float MixAir(Vector3 had, float hadAir, Vector3 add, float addAir)
        {
            float a = had.x * 0.2126f + had.y * 0.7152f + had.z * 0.0722f, b = add.x * 0.2126f + add.y * 0.7152f + add.z * 0.0722f;
            return a + b > 1e-12f ? (a * hadAir + b * addAir) / (a + b) : addAir;
        }

        /// <summary>EmissiveLights.PowerOf.</summary>
        private Vector4 HandoverPower(Material proxyMaterial, Transform source)
        {
            if (!_lightOverrides || !_handoverOn || _lightOv == null || proxyMaterial == null || !_lightOv.Classes.HasHandovers) return Vector4.zero;
            if (_lightOv.Scene != SafeCurrentScene()) return Vector4.zero; // the table is rebuilt once the level's rules are in
            EnsureHandover();
            string name = CleanMaterialName(proxyMaterial.name);
            if (source == null) return Vector4.zero;
            var key = new KeyValuePair<Transform, string>(source, name);
            if (!_handPower.TryGetValue(key, out var p)) return Vector4.zero;
            return new Vector4(p.x, p.y, p.z, 1f + (_handAir.TryGetValue(key, out var air) ? air : 1f));
        }

        private bool HandoverFulfilled(HandedLight hl)
        {
            if (_emissive == null) return false;
            var done = _emissive.PowerFulfilled;
            if (done.Count == 0) return false;
            foreach (var k in hl.Keys) if (done.Contains(k)) return true;
            foreach (var k in hl.ObjectParts)
                foreach (var f in done)
                    if (f is KeyValuePair<Transform, string> o && ReferenceEquals(o.Key, k.Key) && CleanMaterialName(o.Value) == k.Value) return true;
            return false;
        }

        /// <summary>EmissiveLights.IsPowerPart.</summary>
        private bool HandoverIsPart(Material proxyMaterial)
        {
            if (!_lightOverrides || !_handoverOn || _lightOv == null || proxyMaterial == null || !_lightOv.Classes.HasHandovers) return false;
            if (_lightOv.Scene != SafeCurrentScene()) return false;
            EnsureHandover();
            return _handParts.Count > 0 && _handParts.Contains(PartNameOf(proxyMaterial));
        }

        /// <summary>The part name a proxy material stands for: its own, or for an atlas slot variant made for a fixture part
        /// (SceneProxies.AtlasVariant) the original material's.</summary>
        private string PartNameOf(Material proxyMaterial) => _proxies?.AtlasPartName(proxyMaterial) ?? CleanMaterialName(proxyMaterial.name);

        /// <summary>SceneProxies.AtlasPartWanted: atlas slots of this original get a variant of their own (a handover part, or
        /// an emitter rule's glow), so the handover and the glow can reach them.</summary>
        private bool AtlasPartWanted(Material original)
        {
            if (!_lightOverrides || _lightOv == null || original == null) return false;
            return EmitterGlow(original).x > 0f || (_lightOv.Classes.HasHandovers && _lightOv.Classes.IsHandoverPart(CleanMaterialName(original.name)));
        }

        /// <summary>EmissiveLights.BakedPowerOf.</summary>
        private List<KeyValuePair<Bounds, Vector4>> HandoverBakedPower(Material proxyMaterial)
        {
            if (!_lightOverrides || !_handoverOn || _lightOv == null || proxyMaterial == null || !_lightOv.Classes.HasHandovers) return null;
            if (_lightOv.Scene != SafeCurrentScene()) return null;
            EnsureHandover();
            return _handBaked.TryGetValue(PartNameOf(proxyMaterial), out var list) ? list : null;
        }

        /// <summary>BuildResolved: the light's own strength once its part carries it. Returns "handed", "hybrid", or
        /// "no part" (the rule matched and the fixture has no such part: the light stays).</summary>
        private string ApplyHandover(Light l, LightResolved r, LightRule rule)
        {
            var h = rule?.Handover;
            if (h == null || !_handoverOn) return null;
            EnsureHandover();
            ApplyHandoverTints();
            if (!_handed.TryGetValue(l, out var hl)) return "no part";
            // Only once the part's triangles are in the emissive table: a part that is not traced (or not yet) must not
            // cost the light.
            if (!HandoverFulfilled(hl)) return "part not traced";
            r.Intensity *= Mathf.Max(0f, h.Keep);
            if (h.Keep > 0f && h.KeepMedium.HasValue) r.Medium = Mathf.Max(0f, h.KeepMedium.Value);
            return h.Keep > 0f ? "hybrid" : "handed";
        }
    }
}
