using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Which of the game's effect materials take their lighting from the ray-traced solution (docs/particles.md), and the
    /// swap itself: while a lit mode is on, those materials (the shared assets, so every renderer and every later spawn
    /// that uses them follows) get UltraRT/Particles/Lit as their shader, configured to reproduce the game's shader; when
    /// it is off, the original shader, render queue and keywords come back.
    ///
    /// Selected: unlit, alpha-blended or alpha-tested art of things that should be lit (blood, smoke, dust, debris,
    /// splashes, drips; by material / texture name), in the shaders the survey found them in. Never: additive glows (fire,
    /// flashes, charges, explosions, beams), multiplicative effects (Multiply (Double) smoke, blood and oil stains: they
    /// already multiply the ray-traced image), shared default materials (Sprites-Default), water and glass surfaces.
    /// </summary>
    internal sealed class LitParticles
    {
        private sealed class Saved
        {
            public Shader Shader;
            public int Queue;
            public string[] Keywords;
            public string Kind;
            public (string name, float value)[] Props; // the _PL* configuration
        }

        private readonly ManualLogSource _log;
        private readonly Dictionary<Material, Saved> _lit = new Dictionary<Material, Saved>();
        private readonly HashSet<int> _decided = new HashSet<int>();
        private readonly Dictionary<string, Saved> _byName = new Dictionary<string, Saved>(); // instances made from a swapped material
        private Shader _shader;
        public bool Active { get; private set; }
        public int Count => _lit.Count;
        public ParticleLightSettings Settings;

        public LitParticles(ManualLogSource log) { _log = log; }

        public void SetShader(Shader s)
        {
            if (Active) Deactivate();
            _shader = s;
        }

        private static readonly string[] Wanted =
            { "blood", "smoke", "dust", "dirt", "debris", "splash", "drip", "wet", "mud", "gib", "gore", "chunk", "shard", "rubble", "ash", "spray", "mist", "feather", "paper", "leaf" };
        private static readonly string[] Unwanted =
            { "fire", "flame", "flash", "muzzle", "spark", "glow", "light", "beam", "laser", "charge", "explosion", "electric", "energy",
              "plasma", "magic", "halo", "lightning", "heat", "skull", "ghost", "holo", "shock", "trail glow", "projectile" };
        private static readonly string[] SmokeNames = { "smoke", "dust", "mist", "steam", "fog" };

        private static bool Any(string s, string[] keys)
        {
            foreach (var k in keys) if (s.Contains(k)) return true;
            return false;
        }

        private static float Get(Material m, string p, float def) => m.HasProperty(p) ? m.GetFloat(p) : def;

        /// <summary>The tint an effect material draws with (2 x _TintColor for the additive particle shaders, else _Color),
        /// whether or not it is currently swapped to the lit shader (which declares both properties).</summary>
        public static Color EffectTint(Material m)
        {
            if (m.HasProperty("_PLKind"))
                return Mathf.RoundToInt(m.GetFloat("_PLKind")) == 5 ? m.GetColor("_TintColor") * 2f : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            return m.HasProperty("_TintColor") ? m.GetColor("_TintColor") * 2f : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
        }

        /// <summary>Whether the material blends One x (not SrcAlpha x), swapped or not.</summary>
        public static bool EffectSrcOne(Material m) =>
            m.HasProperty("_PLSrcBlend") ? Mathf.RoundToInt(m.GetFloat("_PLSrcBlend")) == (int)BlendMode.One
                                         : m.HasProperty("_SrcBlend") && Mathf.RoundToInt(m.GetFloat("_SrcBlend")) == (int)BlendMode.One;

        /// <summary>The configuration that reproduces <paramref name="m"/>'s shader, or null to leave it alone.</summary>
        private (string kind, float k, float src, float dst, float zw, float cull, float premul, float alphaTest, float colorMode, float fog, float vcol, float fade)?
            Classify(Material m)
        {
            _classLit = 1f;
            _classScroll = 0f;
            if (m == null || m.shader == null) return null;
            string s = m.shader.name;
            string n = m.name.ToLowerInvariant();
            string t = m.mainTexture != null ? m.mainTexture.name.ToLowerInvariant() : "";
            string names = n + " " + t;
            var glow = ClassifySoftGlow(m, s, n);
            if (glow != null) return glow;
            if (Any(names, Unwanted)) return null;
            bool wanted = Any(names, Wanted) || n == "alpha blended" || n.StartsWith("alpha blended ", StringComparison.Ordinal);
            bool smoke = Any(names, SmokeNames);
            if (!wanted) return null;
            switch (s)
            {
                case "psx/unlit/alphatest/nocull":
                    // Blood drops and their trails: alpha-tested, depth-writing, drawn in the opaque pass (queue 2450).
                    return ("psx", 1, (float)BlendMode.One, (float)BlendMode.Zero, 1, 0, 0, 1, 0, 1, 1, 0);
                case "Particles/Standard Unlit":
                case "ULTRAKILL/Particles/Standard Unlit":
                {
                    if (m.IsKeywordEnabled("_ALPHAMODULATE_ON")) return null; // multiplies the background: already lit
                    if (m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor") && m.GetColor("_EmissionColor").maxColorComponent > 0.01f) return null;
                    float src = Get(m, "_SrcBlend", 5), dst = Get(m, "_DstBlend", 10);
                    bool additive = Mathf.RoundToInt(dst) == (int)BlendMode.One;
                    if (additive && !(Settings.additiveSmoke && smoke)) return null;
                    float mode = m.IsKeywordEnabled("_COLOROVERLAY_ON") ? 1 : m.IsKeywordEnabled("_COLORCOLOR_ON") ? 2 : m.IsKeywordEnabled("_COLORADDSUBDIFF_ON") ? 3 : 0;
                    return ("standard", 0, src, dst, Get(m, "_ZWrite", 0), Get(m, "_Cull", 0), m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ? 1 : 0,
                            m.IsKeywordEnabled("_ALPHATEST_ON") ? 1 : 0, mode, additive ? 2 : 1, 1, m.IsKeywordEnabled("_FADING_ON") ? 1 : 0);
                }
                case "ULTRAKILL/Legacy Shaders/Particles/Multiply (Double)":
                case "Legacy Shaders/Particles/Multiply (Double)":
                    // Explosion and fire smoke: multiplies the image (already lit by it); swapped only for the soft fade.
                    if (!Settings.soft || (Settings.softKinds & 4) == 0) return null;
                    _classLit = 0f;
                    return ("multiply", 6, (float)BlendMode.DstColor, (float)BlendMode.SrcColor, 0, 0, 0, 0, 0, 1, 1, 0);
                case "Mobile/Particles/Alpha Blended":
                case "Legacy Shaders/Particles/Alpha Blended":
                    return ("mobile", 3, (float)BlendMode.SrcAlpha, (float)BlendMode.OneMinusSrcAlpha, 0, 0, 0, 0, 0, 1, 1, 0);
                case "ULTRAKILL/Sprites/Default":
                case "ULTRAKILL/SplashSprite-Default":
                case "ULTRAKILL/SplashSprite-VertexLit":
                case "Sprites/Default":
                    if (n.StartsWith("sprites-default", StringComparison.Ordinal)) return null; // shared by every plain sprite (muzzle flashes too)
                    return ("sprite", 2, (float)BlendMode.One, (float)BlendMode.OneMinusSrcAlpha, 0, 0, 1, 0, 0, 0, 1, 0);
                case "ULTRAKILL/Master":
                {
                    // Additive smoke and dust only (alpha-blended Master transparents are signs, water and glass).
                    if (!Settings.additiveSmoke || !smoke || !m.IsKeywordEnabled("TRANSPARENCY")) return null;
                    foreach (var k in new[] { "EMISSIVE", "VERTEX_DISPLACEMENT", "SCROLLING", "FRESNEL", "REFLECTION", "DISTANCE_FADE", "LIMBO_WALLS", "CYBER_GRIND" })
                        if (m.IsKeywordEnabled(k)) return null;
                    float src = Get(m, "_SrcBlend", 5), dst = Get(m, "_DstBlend", 1);
                    if (Mathf.RoundToInt(dst) != (int)BlendMode.One) return null;
                    return ("master", 4, src, dst, 0, Get(m, "_CullMode", 0), 0, 0, 0, 2, m.IsKeywordEnabled("_VERTEXCOLORS_ON") ? 1 : 0, 0);
                }
            }
            return null;
        }

        private float _classLit = 1f, _classScroll;
        private static readonly string[] SoftGlowNames = { "fire", "flame", "explosion" };

        /// <summary>
        /// Glows that are volumes in the game's fiction (fire sprites, explosion spheres) but flat or hard-edged art: they
        /// keep the game's look exactly (unlit, no tone curve) and only gain the soft fade where they cut into the scene
        /// (docs/vfx.md, "Soft particles"). By name, and only in the shaders reproduced exactly: glow quads that lie flat on
        /// a wall (checkpoints, screens, light pillars) must not fade.
        /// </summary>
        private (string kind, float k, float src, float dst, float zw, float cull, float premul, float alphaTest, float colorMode, float fog, float vcol, float fade)?
            ClassifySoftGlow(Material m, string shader, string name)
        {
            if (!Settings.soft || !Any(name, SoftGlowNames)) return null;
            switch (shader)
            {
                case "ULTRAKILL/Additive":
                    if ((Settings.softKinds & 2) == 0) return null;
                    _classLit = 0f;
                    return ("additive", 5, (float)BlendMode.SrcAlpha, (float)BlendMode.One, 0, (float)CullMode.Back, 0, 0, 0, 0, 1, 0);
                case "Legacy Shaders/Particles/Additive":
                case "ULTRAKILL/Legacy Shaders/Particles/Additive":
                    if ((Settings.softKinds & 2) == 0) return null;
                    _classLit = 0f;
                    return ("additive", 5, (float)BlendMode.SrcAlpha, (float)BlendMode.One, 0, 0, 0, 0, 0, 2, 1, 0);
                case "ULTRAKILL/Master":
                {
                    if (!m.IsKeywordEnabled("TRANSPARENCY") || (Settings.softKinds & 1) == 0) return null;
                    foreach (var k in new[] { "EMISSIVE", "VERTEX_DISPLACEMENT", "VERTEX_LIGHTING", "FRESNEL", "REFLECTION", "DISTANCE_FADE", "LIMBO_WALLS", "CYBER_GRIND", "CAUSTICS", "VERTEX_BLENDING" })
                        if (m.IsKeywordEnabled(k)) return null;
                    float src = Get(m, "_SrcBlend", 5), dst = Get(m, "_DstBlend", 1);
                    if (Mathf.RoundToInt(dst) != (int)BlendMode.One || Get(m, "_ZWrite", 0) > 0.5f) return null;
                    _classLit = 0f;
                    _classScroll = m.IsKeywordEnabled("SCROLLING") ? 1f : 0f;
                    return ("master glow", 4, src, dst, 0, Get(m, "_CullMode", 2), 0, 0, 0, 2, Get(m, "_VertexColors", 1) > 0.5f ? 1 : 0, 0);
                }
            }
            return null;
        }

        /// <summary>Look at every loaded material not seen before (assets of the level and of the shared bundles, used or
        /// not yet), and swap the selected ones when active.</summary>
        public void Scan()
        {
            if (_shader == null) return;
            int added = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || !_decided.Add(m.GetInstanceID())) continue;
                if (m.shader == _shader) { Adopt(m); continue; }
                var c = Classify(m);
                if (c == null) continue;
                var v = c.Value;
                var saved = new Saved { Shader = m.shader, Queue = m.renderQueue, Keywords = m.shaderKeywords, Kind = v.kind };
                _lit[m] = saved;
                if (!_byName.ContainsKey(m.name)) _byName[m.name] = saved;
                saved.Props = new[]
                {
                    ("_PLKind", v.k), ("_PLSrcBlend", v.src), ("_PLSrcOne", Mathf.RoundToInt(v.src) == (int)BlendMode.One ? 1f : 0f),
                    ("_PLDstBlend", v.dst), ("_PLZWrite", v.zw), ("_PLCull", v.cull), ("_PLPremultiply", v.premul),
                    ("_PLAlphaTest", v.alphaTest), ("_PLColorMode", v.colorMode), ("_PLFog", v.fog), ("_PLVertexColor", v.vcol),
                    ("_PLFade", v.fade), ("_PLLit", _classLit), ("_PLScroll", _classScroll),
                };
                ApplyProps(m, saved);
                if (v.kind == "master")
                    _log.LogInfo($"Lit particles: '{m.name}' custom fog {(m.IsKeywordEnabled("_USECUSTOMFOGDISTS_ON") ? "on" : "off")} " +
                                 $"emissive fog {Get(m, "_EmissiveFogStart", -1):F1}..{Get(m, "_EmissiveFogEnd", -1):F1}, opacity {Get(m, "_Opacity", -1):F2}, " +
                                 $"emissive intensity {Get(m, "_EmissiveIntensity", -1):F2}, colour {(m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "-")}");
                if (Active) Swap(m, saved);
                added++;
            }
            if (added > 0) _log.LogInfo($"Lit particles: {added} new materials ({_lit.Count} in all): " +
                                        string.Join(", ", _lit.Take(40).Select(kv => $"{kv.Key.name} ({kv.Value.Shader.name}, {kv.Value.Kind})")));
        }

        /// <summary>A material that already has our shader: an instance (renderer.material) made from a swapped material.
        /// Restore it like its source.</summary>
        private void Adopt(Material m)
        {
            string baseName = m.name.Replace(" (Instance)", "");
            if (_byName.TryGetValue(baseName, out var src) || _byName.TryGetValue(m.name, out src)) _lit[m] = src;
        }

        private static void ApplyProps(Material m, Saved s)
        {
            if (s.Props == null) return;
            foreach (var (name, value) in s.Props) m.SetFloat(name, value);
        }

        private void Swap(Material m, Saved s)
        {
            m.shader = _shader;
            // The multiply smoke of an explosion sits at the centre of its additive spheres (same sort distance): with the
            // game's shader it happens to be drawn after them, which is what makes the dark core. One queue step later
            // keeps that order for certain.
            m.renderQueue = s.Kind == "multiply" ? s.Queue + 1 : s.Queue;
            // Again with the shader in place: a value set while the material had the game's shader is not always kept
            // (the multiply smoke came out alpha-blended and lit: the shader's defaults).
            ApplyProps(m, s);
        }

        public void Activate()
        {
            if (Active || _shader == null) return;
            Active = true;
            foreach (var kv in _lit) if (kv.Key != null) Swap(kv.Key, kv.Value);
        }

        public void Deactivate()
        {
            if (!Active) return;
            Active = false;
            // Instances made while active (renderer.material copies the shader): find them by shader and restore them too.
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader == _shader && !_lit.ContainsKey(m)) Adopt(m);
            int missing = 0;
            foreach (var kv in _lit)
            {
                var m = kv.Key;
                if (m == null) continue;
                m.shader = kv.Value.Shader;
                m.renderQueue = kv.Value.Queue;
                m.shaderKeywords = kv.Value.Keywords;
            }
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader == _shader) missing++;
            if (missing > 0) _log.LogWarning($"Lit particles: {missing} materials still use the lit shader after restoring");
        }

        /// <summary>Forget every decision (after restoring the materials): a setting that changes which materials are
        /// swapped was toggled (soft particles). The next Scan classifies everything again.</summary>
        public void Reclassify()
        {
            bool was = Active;
            if (was) Deactivate();
            _lit.Clear();
            _decided.Clear();
            _byName.Clear();
            Scan();
            if (was) Activate();
        }

        /// <summary>Scene change: the level's materials are gone; shared bundle materials stay (and stay decided).</summary>
        public void Prune()
        {
            var dead = _lit.Keys.Where(k => k == null).ToList();
            foreach (var k in dead) _lit.Remove(k);
        }
    }
}
