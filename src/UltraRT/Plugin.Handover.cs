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
        private string _handKey;
        private SceneProxies _handScene;
        private bool _handBuilding;
        /// <summary>Bench variants lc.handover=0 (the lights stay, no part is scaled) and lc.hgain=F (on every gain).</summary>
        private bool _handoverOn = true;
        private float _handoverGain = 1f;

        private const float HandoverK = 0.003205f; // relight's physical curve (UltraRTLights.hlsl, _RelightLinearK's default)
        private const float HandoverMaxGamma = 3f; // colour x intensity above this only widens the white patch around the game's light: an emitter would flood the room with it

        private string HandoverVariantKey => (_handoverOn ? "on" : "off") + _handoverGain.ToString(CultureInfo.InvariantCulture);

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
                    foreach (var m in r.sharedMaterials)
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
            int lights = _proxies != null ? _proxies.Lights.Count : 0;
            string key = _lightOv.Scene + "|" + _lightOv.Version + "|" + _toggleVersion + "|" + lights + "|" + (_lightOverrides ? 1 : 0) + HandoverVariantKey;
            if (key == _handKey && ReferenceEquals(_handScene, _proxies)) return;
            _handKey = key;
            _handScene = _proxies;
            _handed.Clear();
            _handPower.Clear();
            _handAir.Clear();
            _handBaked.Clear();
            _handParts.Clear();
            if (_proxies == null || !_lightOverrides || !_handoverOn || !_lightOv.Classes.HasHandovers) return;
            _handBuilding = true;
            try
            {
                var parts = new List<KeyValuePair<Renderer, string>>();
                var perRule = new Dictionary<string, int[]>(); // rule -> lights handed, hybrid, without a part
                foreach (var l in _proxies.Lights)
                {
                    if (l == null || (l.type != LightType.Point && l.type != LightType.Spot)) continue;
                    var rb = BuildResolved(l, false);
                    var rule = rb.RuleObject;
                    var h = rule?.Handover;
                    if (h == null) continue;
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
                    foreach (var p in parts)
                    {
                        _handParts.Add(p.Value);
                        if (p.Key.isPartOfStaticBatch)
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
                if (perRule.Count > 0)
                    Logger.LogInfo($"Handover ({_lightOv.Scene}): {_handed.Count} lights to {_handPower.Count} parts and {_handBaked.Values.Sum(x => x.Count)} baked parts; " +
                                   string.Join("; ", perRule.Select(kv => $"'{kv.Key}' {kv.Value[0]} handed, {kv.Value[1]} hybrid, {kv.Value[2]} without a part")));
            }
            catch (Exception e) { Logger.LogWarning($"Handover: failed, the lights stay: {e.Message}"); _handed.Clear(); _handPower.Clear(); _handAir.Clear(); _handBaked.Clear(); _handParts.Clear(); }
            finally { _handBuilding = false; }
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
            return _handParts.Count > 0 && _handParts.Contains(CleanMaterialName(proxyMaterial.name));
        }

        /// <summary>EmissiveLights.BakedPowerOf.</summary>
        private List<KeyValuePair<Bounds, Vector4>> HandoverBakedPower(Material proxyMaterial)
        {
            if (!_lightOverrides || !_handoverOn || _lightOv == null || proxyMaterial == null || !_lightOv.Classes.HasHandovers) return null;
            if (_lightOv.Scene != SafeCurrentScene()) return null;
            EnsureHandover();
            return _handBaked.TryGetValue(CleanMaterialName(proxyMaterial.name), out var list) ? list : null;
        }

        /// <summary>BuildResolved: the light's own strength once its part carries it. Returns "handed", "hybrid", or
        /// "no part" (the rule matched and the fixture has no such part: the light stays).</summary>
        private string ApplyHandover(Light l, LightResolved r, LightRule rule)
        {
            var h = rule?.Handover;
            if (h == null || !_handoverOn) return null;
            EnsureHandover();
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
