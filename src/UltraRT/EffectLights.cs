using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>Tunables for <see cref="EffectLights"/>, read every frame. See docs/emissive-lights.md, "Effect lights".</summary>
    public sealed class EffectLightSettings
    {
        /// <summary>Spawned and pooled game Lights (muzzle flashes, explosions, fires) join the light list the frame they light up.</summary>
        public bool spawnedLights = true;
        /// <summary>Virtual point lights for glowing effects that have no Light (fire sprites, additive particles and trails).</summary>
        public bool virtualLights = true;
        /// <summary>Torch / fire lights follow the brightness of the flame sprite or particles drawn on them.</summary>
        public bool flicker = true;
        /// <summary>At most this many virtual lights per frame (the most important near the camera win).</summary>
        public int maxVirtual = 16;
        /// <summary>Emitters farther than this from the camera get no light (m).</summary>
        public float cullDistance = 45f;
        /// <summary>Intensity of a virtual light for an effect at full strength (the game's own "Fire" light is 5).</summary>
        public float intensityScale = 4f;
        /// <summary>Range = rangePerSize x the effect's bounds radius, clamped to [minRange, maxRange] (the game's fire: 10).</summary>
        public float rangePerSize = 4f, minRange = 3f, maxRange = 12f;
        /// <summary>Emitters whose strength (peak channel of their average visible colour, x4) is below this are ignored.</summary>
        public float minStrength = 0.1f;
        /// <summary>An emitter within this distance of an active game light (plus its own size) is already lit by the game.</summary>
        public float coverDistance = 1f;
        /// <summary>Hybrid: how much of a virtual light's (shadowed) light is added on top of the game's image.</summary>
        public float hybridWeight = 1f;
        /// <summary>Flicker: the light's intensity follows flame brightness / its running mean, clamped to [1 - x, 1 + x].</summary>
        public float flickerRange = 0.35f;
        /// <summary>
        /// Virtual lights also for additive trails, lines and meshes. Off by default: in the levels surveyed those are enemy
        /// motion streaks, light-shaft decor (LightPillar) and lava overlays (lit by EmissiveLights), and every beam that
        /// matters (revolver, railcannon) has its own Light.
        /// </summary>
        public bool virtualTrailsAndMeshes = false;
    }

    /// <summary>
    /// Lights for the game's dynamic effects (docs/emissive-lights.md, "Effect lights"):
    ///  1. Spawned and pooled Lights. Explosions, revolver beams, muzzle flashes and projectiles are instantiated with their
    ///     own Light; fires come from FireObjectPool. GatherLights' light list is a FindObjectsOfType scan once a second,
    ///     which misses nearly all of them (95% live under a second, a muzzle light ~50 ms). Every instantiated hierarchy
    ///     comes through <see cref="EffectSpawns"/>; its Lights are tracked for good and packed whenever they are on and
    ///     the current scan doesn't have them.
    ///  2. Virtual lights. Glowing effects with no Light (FireSimple flames, additive particles and trails) get a point
    ///     light at their bounds centre, coloured by what they draw (tint x sprite/texture average x vertex colour) and
    ///     sized by their bounds. Their id is the renderer's instance id, so ReSTIR's light remap keeps their reservoirs.
    ///  3. Flicker. A glowing effect sitting on a game light (torch flame sprites on their "Fire" light) drives that light's
    ///     intensity with its own frame-to-frame brightness: in the game the light is constant and only the sprite flickers.
    /// No per-frame allocations and no per-frame FindObjectsOfType: scene emitters are collected once per RT scene build,
    /// spawned ones from the Instantiate hook.
    /// </summary>
    internal sealed class EffectLights
    {
        public EffectLightSettings Settings = new EffectLightSettings();

        public int TrackedLights => _trackedCount;
        public int EmitterCount => _emitters.Count;

        private readonly ManualLogSource _log;

        // ---------------------------------------------------------------- spawned / pooled lights

        private struct Tracked
        {
            public Light Light;
            public bool InSnapshot; // the current GatherLights scan has it: GatherLights packs it
            public bool On;         // was on when last checked (on lights are checked every frame, off ones every 4th)
        }
        private Tracked[] _tracked = new Tracked[256];
        private int _trackedCount;
        private readonly Dictionary<Light, int> _trackedIndex = new Dictionary<Light, int>();
        private Light[] _snapshot;
        private const int MaxTracked = 4096;

        // ---------------------------------------------------------------- emitters

        private enum Kind : byte { Particles, Sprite, Line, Trail, Mesh }

        private sealed class Emitter
        {
            public Renderer R;
            public ParticleSystem PS;
            public SpriteRenderer SR;
            public GameObject Go;
            public Transform T;
            public Kind Kind;
            public int Id;
            public Color Base;        // visible colour without the sprite / fill (premultiplied, gamma)
            public string Name;
            public bool OnLight;      // its spawned hierarchy has a Light: never a virtual light, only flicker
            public int NextCheck;     // inactive emitters are looked at every 16th frame
            public Light CoverLight;  // the game light sitting on it (null: none)
            public int CoverFrame = -100000;
            public float Score;
            public bool Selected;
            public float Smoothed;    // smoothed strength (hysteresis against particle-count noise)
            public float Mean = -1f;  // running mean strength, for flicker
            public Vector3 Pos;
            public float Radius;
            public Color Hue;         // normalised colour (max channel 1)
            public float Strength;    // 0..1
        }

        private readonly List<Emitter> _emitters = new List<Emitter>(256);
        private readonly HashSet<int> _emitterIds = new HashSet<int>();
        private readonly List<Emitter> _selected = new List<Emitter>(32);
        private readonly List<Emitter> _flickers = new List<Emitter>(32);
        private readonly Dictionary<Material, MatInfo> _matCache = new Dictionary<Material, MatInfo>();
        private readonly Dictionary<Texture, Color> _texCache = new Dictionary<Texture, Color>();
        private readonly Dictionary<Sprite, Color> _spriteCache = new Dictionary<Sprite, Color>();
        private Texture2D _readTex;
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private const int TexRes = 16;
        private const int MaxFlickers = 16;

        private struct MatInfo { public bool Glows; public Color Color; }

        // Glowing-looking effects that are not light sources: dust and smoke puffs drawn additively, checkpoint holograms,
        // water. Matched against material and texture names (lower case).
        private static readonly string[] Excluded = { "smoke", "dust", "gas", "fog", "cloud", "mist", "steam", "checkpoint", "water", "splash", "glass", "shockwave", "hole", "lava", "pillar" };
        private const float MaxRadius = 6f;

        private readonly List<Light> _tmpLights = new List<Light>(16);
        private readonly List<Renderer> _tmpRenderers = new List<Renderer>(64);
        private int _frame;
        private object _scene; // the RT scene the scene emitters were collected for
        private readonly Stopwatch _sw = new Stopwatch();
        private double _cpuMs;
        private int _logFrames, _logAppended, _logVirtual, _logMaxVirtual, _logFlicker, _logs;
        private float _flickerMin = float.MaxValue, _flickerMax, _nextLog;

        public EffectLights(ManualLogSource log) { _log = log; }

        public void Clear()
        {
            Array.Clear(_tracked, 0, _trackedCount);
            _trackedCount = 0;
            _trackedIndex.Clear();
            _snapshot = null;
            _emitters.Clear(); _emitterIds.Clear(); _selected.Clear(); _flickers.Clear();
            _matCache.Clear(); _texCache.Clear(); _spriteCache.Clear();
            _scene = null;
        }

        // ---------------------------------------------------------------- spawns

        /// <summary>Every frame (OnUpdate): drain the spawn queue. Cheap when nothing spawned.</summary>
        public void ProcessSpawns(EffectSurvey survey)
        {
            var pending = EffectSpawns.Pending;
            if (pending.Count == 0) return;
            _sw.Restart();
            bool lights = Settings.spawnedLights, virt = Settings.virtualLights || Settings.flicker;
            for (int p = 0; p < pending.Count; p++)
            {
                var go = pending[p];
                if (go == null) continue;
                try
                {
                    survey?.OnSpawned(go);
                    bool hasLight = false;
                    go.GetComponentsInChildren(true, _tmpLights);
                    foreach (var l in _tmpLights)
                    {
                        if (l.type == LightType.Directional) continue;
                        hasLight = true;
                        if (lights) Track(l);
                    }
                    // Effects with a Light of their own are lit by it; only unlit ones can get virtual lights. Spawned
                    // flames on a light (Fire) still drive its flicker through the coverage test.
                    if (virt)
                    {
                        go.GetComponentsInChildren(true, _tmpRenderers);
                        foreach (var r in _tmpRenderers) TryAddEmitter(r, hasLight);
                    }
                }
                catch (Exception e) { _log.LogWarning($"Effect lights: spawn of '{go.name}' failed: {e.Message}"); }
            }
            pending.Clear();
            _sw.Stop();
            _cpuMs += _sw.Elapsed.TotalMilliseconds;
        }

        private void Track(Light l)
        {
            if (_trackedIndex.ContainsKey(l) || _trackedCount >= MaxTracked) return;
            if (_trackedCount == _tracked.Length) Array.Resize(ref _tracked, _tracked.Length * 2);
            _trackedIndex[l] = _trackedCount;
            _tracked[_trackedCount++] = new Tracked { Light = l, InSnapshot = _snapshot != null && Array.IndexOf(_snapshot, l) >= 0 };
        }

        /// <summary>Once per RT scene build: collect the level's own glowing effects (torch flames, ambient embers).</summary>
        private void CollectSceneEmitters()
        {
            var sw = Stopwatch.StartNew();
            int before = _emitters.Count;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                if (r is ParticleSystemRenderer || r is SpriteRenderer || r is LineRenderer || r is TrailRenderer || r is MeshRenderer)
                    TryAddEmitter(r, false);
            var groups = _emitters.Skip(before).GroupBy(e => $"{e.Kind} {e.Name}").OrderByDescending(g => g.Count()).Take(12)
                .Select(g => $"{g.Key} x{g.Count()}");
            _log.LogInfo($"Effect lights: {_emitters.Count - before} glowing scene effects in {sw.Elapsed.TotalMilliseconds:F1} ms: {string.Join(", ", groups)}");
        }

        private void TryAddEmitter(Renderer r, bool onLight)
        {
            if (r == null || r.name.EndsWith("[UltraRT]", StringComparison.Ordinal)) return;
            Kind kind;
            ParticleSystem ps = null;
            if (r is ParticleSystemRenderer) { kind = Kind.Particles; ps = r.GetComponent<ParticleSystem>(); if (ps == null) return; }
            else if (r is SpriteRenderer) kind = Kind.Sprite;
            else if (r is LineRenderer) kind = Kind.Line;
            else if (r is TrailRenderer) kind = Kind.Trail;
            else if (r is MeshRenderer) kind = Kind.Mesh;
            else return;
            if (!Settings.virtualTrailsAndMeshes && kind != Kind.Sprite && kind != Kind.Particles) return; // see the setting
            var m = r.sharedMaterial;
            if (m == null) return;
            var info = Analyse(m);
            if (!info.Glows) return;
            var col = info.Color;
            if (kind == Kind.Particles)
            {
                var sc = ps.main.startColor;
                Color vc = sc.mode == ParticleSystemGradientMode.TwoColors ? Color.Lerp(sc.colorMin, sc.colorMax, 0.5f) : sc.color;
                col *= new Color(vc.r * vc.a, vc.g * vc.a, vc.b * vc.a, 1f);
            }
            else if (kind == Kind.Sprite)
            {
                var c = ((SpriteRenderer)r).color;
                col *= new Color(c.r * c.a, c.g * c.a, c.b * c.a, 1f);
            }
            else if (kind == Kind.Line || kind == Kind.Trail)
            {
                var g = kind == Kind.Line ? ((LineRenderer)r).colorGradient : ((TrailRenderer)r).colorGradient;
                var c = g.Evaluate(0.5f);
                col *= new Color(c.r * c.a, c.g * c.a, c.b * c.a, 1f);
            }
            if (MaxC(col) < 1e-3f) return;
            int id = r.GetInstanceID();
            if (!_emitterIds.Add(id)) return;
            _emitters.Add(new Emitter
            {
                R = r, PS = ps, SR = r as SpriteRenderer, Go = r.gameObject, T = r.transform, Kind = kind, Id = id, Base = col, Name = m.name,
                OnLight = onLight,
            });
        }

        /// <summary>
        /// Does this material glow (additive blending in the transparent queue, and not smoke / dust / holograms), and
        /// what colour does it draw on average? Legacy particle shaders draw 2 x _TintColor x vertex colour x texture;
        /// the colour is premultiplied by alpha (what an additive blend adds), gamma space like Light.color.
        /// </summary>
        private MatInfo Analyse(Material m)
        {
            if (_matCache.TryGetValue(m, out var info)) return info;
            info = new MatInfo();
            try
            {
                string s = m.shader != null ? m.shader.name : "";
                bool additive;
                if (m.HasProperty("_DstBlend")) additive = Mathf.RoundToInt(m.GetFloat("_DstBlend")) == (int)BlendMode.One;
                else additive = s.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0;
                if (additive && m.renderQueue >= 2450 && !IsExcluded(m))
                {
                    Color tint = m.HasProperty("_TintColor") ? m.GetColor("_TintColor") * 2f
                               : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                    var t = m.mainTexture != null ? TexAverage(m.mainTexture) : Color.white;
                    info.Color = new Color(tint.r * tint.a * t.r, tint.g * tint.a * t.g, tint.b * tint.a * t.b, 1f);
                    info.Glows = MaxC(info.Color) > 1e-3f;
                }
            }
            catch (Exception e) { _log.LogWarning($"Effect lights: material '{m.name}': {e.Message}"); }
            _matCache[m] = info;
            return info;
        }

        private static bool IsExcluded(Material m)
        {
            string n = m.name.ToLowerInvariant();
            string t = m.mainTexture != null ? m.mainTexture.name.ToLowerInvariant() : "";
            foreach (var x in Excluded)
                if (n.Contains(x) || t.Contains(x)) return true;
            return false;
        }

        /// <summary>Average of rgb x alpha (what an additive blend adds per pixel of the quad), from a 16x16 Blit + ReadPixels.</summary>
        private Color TexAverage(Texture tex) => _texCache.TryGetValue(tex, out var c) ? c : (_texCache[tex] = Downsample(tex, Vector2.one, Vector2.zero));

        /// <summary>The same for one sprite (its rectangle of a sprite sheet). Cached per sprite: animated flames swap sprites.</summary>
        private Color SpriteAverage(Sprite s)
        {
            if (_spriteCache.TryGetValue(s, out var c)) return c;
            var tex = s.texture;
            if (tex == null) c = Color.white;
            else
            {
                var r = s.textureRect;
                c = Downsample(tex, new Vector2(r.width / tex.width, r.height / tex.height), new Vector2(r.x / tex.width, r.y / tex.height));
            }
            _spriteCache[s] = c;
            return c;
        }

        private Color Downsample(Texture tex, Vector2 scale, Vector2 offset)
        {
            var avg = Color.white;
            if (tex.dimension != TextureDimension.Tex2D) return avg;
            var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;
            var rt = RenderTexture.GetTemporary(TexRes, TexRes, 0, fmt, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt, scale, offset);
                RenderTexture.active = rt;
                if (_readTex == null) _readTex = new Texture2D(TexRes, TexRes, fmt == RenderTextureFormat.ARGBFloat ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true);
                _readTex.ReadPixels(new Rect(0, 0, TexRes, TexRes), 0, 0, false);
                var px = _readTex.GetPixels();
                Color s = Color.clear;
                foreach (var c in px) s += new Color(c.r * c.a, c.g * c.a, c.b * c.a, 0f);
                avg = s * (1f / px.Length);
                avg.a = 1f;
            }
            catch (Exception e) { _log.LogWarning($"Effect lights: texture '{tex.name}': {e.Message}"); }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            return avg;
        }

        private static float MaxC(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));

        // ---------------------------------------------------------------- per frame

        /// <summary>
        /// Append this frame's effect lights to the packed light list (after the scene lights), and apply flame flicker to
        /// the scene lights already packed. Spawned / pooled Lights the current scan lacks are packed as GatherLights packs
        /// them; virtual lights use the renderer's instance id (ids[i], packed[i] = null).
        /// </summary>
        /// <param name="scene">The RT scene (scene emitters are collected once per instance).</param>
        /// <param name="snapshot">GatherLights' current FindObjectsOfType result: lights in it are packed by GatherLights.</param>
        /// <returns>The new light count.</returns>
        public int Append(object scene, Light[] snapshot, Vector4[] data, uint[] masks, Light[] packed, int[] ids, int count, int max, Vector3 camPos)
        {
            _sw.Restart();
            _frame++;
            int appended = 0, virt = 0;
            try
            {
                if (scene != null && !ReferenceEquals(scene, _scene))
                {
                    // A new level. Tracked lights and emitters of the old one are destroyed and drop out by themselves; the
                    // ones spawned while this level loaded (FireObjectPool fills its pool in Awake) must stay.
                    _matCache.Clear(); _texCache.Clear(); _spriteCache.Clear();
                    _scene = scene;
                    if (Settings.virtualLights || Settings.flicker) CollectSceneEmitters();
                }
                if (Settings.spawnedLights)
                {
                    int before = count;
                    count = AppendTracked(snapshot, data, masks, packed, count, max);
                    appended = count - before;
                }
                if (Settings.virtualLights || Settings.flicker)
                {
                    int before = count;
                    count = AppendVirtual(data, masks, packed, ids, count, max, camPos);
                    virt = count - before;
                }
            }
            catch (Exception e) { _log.LogWarning($"Effect lights: {e}"); }
            _sw.Stop();
            _cpuMs += _sw.Elapsed.TotalMilliseconds;
            _logFrames++;
            _logAppended += appended;
            _logVirtual += virt;
            _logMaxVirtual = Math.Max(_logMaxVirtual, virt);
            if (Time.realtimeSinceStartup >= _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + 5f;
                _log.LogInfo($"Effect lights: CPU {_cpuMs / _logFrames * 1000.0:F1} us/frame; tracked lights {_trackedCount}, appended {(double)_logAppended / _logFrames:F2}/frame; " +
                             $"virtual {(double)_logVirtual / _logFrames:F2}/frame (max {_logMaxVirtual}) from {_emitters.Count} emitters; " +
                             $"flickering lights {(double)_logFlicker / _logFrames:F1}/frame (x{(_flickerMax > 0 ? _flickerMin : 1):F2}..{(_flickerMax > 0 ? _flickerMax : 1):F2}); spawn queue drops {EffectSpawns.Dropped}" +
                             (_logs < 40 && _selected.Count > 0 ? $"; selected now: {string.Join(", ", _selected.Take(6).Select(e => $"{e.Kind} '{e.Name}' I {e.Smoothed * Settings.intensityScale:F1}"))}" : ""));
                _logs++;
                _cpuMs = 0; _logFrames = 0; _logAppended = 0; _logVirtual = 0; _logMaxVirtual = 0; _logFlicker = 0;
                _flickerMin = float.MaxValue; _flickerMax = 0f;
            }
            return count;
        }

        private int AppendTracked(Light[] snapshot, Vector4[] data, uint[] masks, Light[] packed, int count, int max)
        {
            if (!ReferenceEquals(snapshot, _snapshot))
            {
                // A new scan: GatherLights packs what it found; mark those.
                _snapshot = snapshot;
                for (int i = 0; i < _trackedCount; i++) _tracked[i].InSnapshot = false;
                if (snapshot != null && _trackedCount > 0)
                    foreach (var l in snapshot)
                        if (!ReferenceEquals(l, null) && _trackedIndex.TryGetValue(l, out int k)) _tracked[k].InSnapshot = true;
            }
            for (int i = 0; i < _trackedCount; i++)
            {
                ref var t = ref _tracked[i];
                if (t.InSnapshot) continue;
                if (!t.On && ((i + _frame) & 3) != 0) continue; // off lights: every 4th frame
                var l = t.Light;
                if (l == null)
                {
                    // Destroyed: swap-remove.
                    _trackedIndex.Remove(l);
                    int last = --_trackedCount;
                    if (i != last)
                    {
                        _tracked[i] = _tracked[last];
                        _trackedIndex[_tracked[i].Light] = i;
                    }
                    _tracked[last] = default;
                    i--;
                    continue;
                }
                t.On = l.isActiveAndEnabled && l.intensity > 0f;
                if (!t.On || count >= max) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                var tr = l.transform;
                var pos = tr.position;
                var fwd = tr.forward;
                var c = l.color * l.intensity;
                Vector4 l3;
                if (l.type == LightType.Spot)
                {
                    float a = l.spotAngle * Mathf.Deg2Rad;
                    float cosHalf = Mathf.Cos(a * 0.5f), cosQuarter = Mathf.Cos(a * 0.25f);
                    l3 = new Vector4(cosHalf, 1f / Mathf.Max(cosQuarter - cosHalf, 1e-4f), 0, 0);
                }
                else l3 = new Vector4(-1f, 1f, 0, 0);
                int j = count * 4;
                data[j] = new Vector4(pos.x, pos.y, pos.z, Mathf.Max(l.range, 1e-4f));
                data[j + 1] = new Vector4(c.r, c.g, c.b, 0);
                data[j + 2] = new Vector4(fwd.x, fwd.y, fwd.z, 0f);
                data[j + 3] = l3;
                masks[count] = unchecked((uint)l.cullingMask);
                packed[count] = l;
                count++;
            }
            return count;
        }

        private int AppendVirtual(Vector4[] data, uint[] masks, Light[] packed, int[] ids, int count, int max, Vector3 camPos)
        {
            var s = Settings;
            float cull2 = s.cullDistance * s.cullDistance;
            if ((_frame & 3) == 0 || _frame < 4)
            {
                // Every 4th frame: score every live emitter near the camera and keep the best maxVirtual uncovered ones
                // (a bonus for last selection keeps the set stable); covered flames near the camera drive flicker.
                foreach (var e in _selected) e.Selected = false;
                _selected.Clear();
                _flickers.Clear();
                for (int i = _emitters.Count - 1; i >= 0; i--)
                {
                    var e = _emitters[i];
                    if (e.R == null)
                    {
                        _emitterIds.Remove(e.Id);
                        _emitters[i] = _emitters[_emitters.Count - 1];
                        _emitters.RemoveAt(_emitters.Count - 1);
                        continue;
                    }
                    bool was = e.Score > 0f;
                    e.Score = 0f;
                    if (e.NextCheck > _frame) continue;
                    if (!e.Go.activeInHierarchy || !e.R.enabled) { e.Smoothed = 0f; e.Mean = -1f; e.NextCheck = _frame + 16; continue; }
                    float d2 = (e.T.position - camPos).sqrMagnitude;
                    if (d2 > cull2 || !Measure(e)) continue;
                    if (_frame - e.CoverFrame > 60) { e.CoverLight = FindCoveringLight(e, data, packed, count); e.CoverFrame = _frame; }
                    if (e.CoverLight != null)
                    {
                        if (s.flicker && _flickers.Count < MaxFlickers && (e.Kind == Kind.Sprite || e.Kind == Kind.Particles)) _flickers.Add(e);
                        continue;
                    }
                    if (!s.virtualLights || e.OnLight || e.Strength < s.minStrength) continue;
                    if (!s.virtualTrailsAndMeshes && e.Kind != Kind.Sprite && e.Kind != Kind.Particles) continue;
                    e.Score = e.Strength * (e.Radius + 0.5f) / (1f + d2 / 25f) * (was ? 1.5f : 1f);
                    Insert(e, Math.Max(0, s.maxVirtual));
                }
                foreach (var e in _selected) e.Selected = true;
            }
            else
            {
                foreach (var e in _selected) if (e.R != null && e.Go.activeInHierarchy && e.R.enabled) Measure(e);
                foreach (var e in _flickers) if (e.R != null && e.Go.activeInHierarchy && e.R.enabled) Measure(e);
            }

            if (s.flicker) ApplyFlicker(data, packed, count);

            foreach (var e in _selected)
            {
                if (count >= max) break;
                if (e.R == null || !e.Go.activeInHierarchy || !e.R.enabled || e.Strength <= 0f) { e.Smoothed = 0f; continue; }
                // Particle counts jump: follow rises at once (flashes start immediately), decay over a few frames.
                e.Smoothed = e.Strength > e.Smoothed ? e.Strength : Mathf.Lerp(e.Smoothed, e.Strength, 0.35f);
                var c = e.Hue * (e.Smoothed * s.intensityScale);
                float range = Mathf.Clamp(e.Radius * s.rangePerSize, s.minRange, s.maxRange);
                int j = count * 4;
                data[j] = new Vector4(e.Pos.x, e.Pos.y, e.Pos.z, range);
                data[j + 1] = new Vector4(c.r, c.g, c.b, 0);
                // Soft shadows from the effect's size; w of [3] > 0 marks it as not in the game's image (hybrid adds it).
                data[j + 2] = new Vector4(0, -1, 0, Mathf.Clamp(e.Radius * 0.4f, 0.05f, 0.5f));
                data[j + 3] = new Vector4(-1f, 1f, 0f, Mathf.Max(1e-4f, s.hybridWeight));
                masks[count] = ~0u;
                packed[count] = null;
                ids[count] = e.Id;
                count++;
            }
            return count;
        }

        private void ApplyFlicker(Vector4[] data, Light[] packed, int count)
        {
            float range = Mathf.Clamp(Settings.flickerRange, 0f, 0.9f);
            foreach (var e in _flickers)
            {
                if (e.CoverLight == null || e.R == null || !e.Go.activeInHierarchy || !e.R.enabled || e.Strength <= 0f) continue;
                // Brightness x size (flames flicker by swapping sprites and by scaling), against its running mean over
                // ~1 s at 60+ fps: the light keeps the game's intensity on average.
                float b = e.Strength * e.Radius * e.Radius;
                e.Mean = e.Mean < 0f ? b : Mathf.Lerp(e.Mean, b, 0.03f);
                float k = Mathf.Clamp(b / Mathf.Max(e.Mean, 1e-6f), 1f - range, 1f + range);
                for (int i = 0; i < count; i++)
                {
                    if (!ReferenceEquals(packed[i], e.CoverLight)) continue;
                    var c = data[i * 4 + 1];
                    data[i * 4 + 1] = new Vector4(c.x * k, c.y * k, c.z * k, c.w);
                    _logFlicker++;
                    _flickerMin = Mathf.Min(_flickerMin, k);
                    _flickerMax = Mathf.Max(_flickerMax, k);
                    break;
                }
            }
        }

        private void Insert(Emitter e, int cap)
        {
            if (cap == 0) return;
            if (_selected.Count < cap) _selected.Add(e);
            else if (_selected[cap - 1].Score < e.Score) _selected[cap - 1] = e;
            else return;
            for (int i = _selected.Count - 1; i > 0 && _selected[i - 1].Score < e.Score; i--) { _selected[i] = _selected[i - 1]; _selected[i - 1] = e; }
        }

        /// <summary>Position, size, colour and strength this frame. False when it draws nothing.</summary>
        private bool Measure(Emitter e)
        {
            float fill = 1f;
            var col = e.Base;
            switch (e.Kind)
            {
                case Kind.Particles:
                {
                    int n = e.PS.particleCount;
                    if (n == 0) { e.Strength = 0f; return false; }
                    fill = 1f - Mathf.Exp(-n * 0.25f);
                    break;
                }
                case Kind.Line:
                case Kind.Trail:
                {
                    float w;
                    int points;
                    if (e.Kind == Kind.Line) { var lr = (LineRenderer)e.R; w = lr.widthMultiplier; points = lr.positionCount; }
                    else { var tr = (TrailRenderer)e.R; w = tr.widthMultiplier; points = tr.positionCount; }
                    if (w <= 0f || points < 2) { e.Strength = 0f; return false; }
                    fill = Mathf.Clamp01(w * 4f);
                    break;
                }
                case Kind.Sprite:
                {
                    var sp = e.SR.sprite;
                    if (sp == null) { e.Strength = 0f; return false; }
                    var a = SpriteAverage(sp);
                    var c = e.SR.color;
                    col = new Color(col.r * a.r * c.r * c.a, col.g * a.g * c.g * c.a, col.b * a.b * c.b * c.a, 1f);
                    // Flames animate with AnimatedTexture, which swaps _MainTex frames through a property block.
                    e.R.GetPropertyBlock(_mpb);
                    if (!_mpb.isEmpty)
                    {
                        var frame = _mpb.GetTexture(MainTexId);
                        if (frame != null)
                        {
                            var f = TexAverage(frame);
                            col = new Color(col.r * f.r, col.g * f.g, col.b * f.b, 1f);
                        }
                    }
                    break;
                }
            }
            var b = e.R.bounds;
            e.Pos = b.center;
            e.Radius = Mathf.Min(b.extents.magnitude, MaxRadius);
            float m = MaxC(col);
            if (m <= 1e-5f || b.extents.magnitude > MaxRadius * 2f) { e.Strength = 0f; return false; }
            e.Hue = col * (1f / m);
            // A quad whose average (premultiplied) colour reaches 0.25 is a full-strength glow; flames cover about that much.
            e.Strength = Mathf.Clamp01(m * 4f) * fill;
            return true;
        }

        /// <summary>The nearest active game light (not virtual) within reach of the emitter, from this frame's packed list.</summary>
        private Light FindCoveringLight(Emitter e, Vector4[] data, Light[] packed, int count)
        {
            // Small reach: the light must sit on the effect (a torch's Fire light inside its flame sprite), not merely nearby
            // (the player's weapon lights next to a flame on the floor).
            float reach = Settings.coverDistance + 0.25f * e.Radius;
            float best = reach * reach;
            Light found = null;
            for (int i = 0; i < count; i++)
            {
                var l0 = data[i * 4];
                if (l0.w == 0f || ReferenceEquals(packed[i], null)) continue; // directional; virtual
                float dx = l0.x - e.Pos.x, dy = l0.y - e.Pos.y, dz = l0.z - e.Pos.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best) { best = d2; found = packed[i]; }
            }
            return found;
        }
    }
}
