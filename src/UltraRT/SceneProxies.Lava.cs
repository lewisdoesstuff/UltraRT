using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Ray-traced lava and acid (docs/lava.md). Read every frame unless noted.</summary>
    internal sealed class LavaSettings
    {
        /// <summary>-ultrart-nolava: no lava proxies (read at the scene build).</summary>
        public bool enabled = true;
        /// <summary>Runtime switch (bench variant lava.active=0): lava proxies leave the RTAS and the emitter list, and the
        /// game's point lights are no longer dimmed. Unlike <see cref="enabled"/> this needs no scene rebuild.</summary>
        public bool active = true;
        /// <summary>-ultrart-lava-light: multiplies lava emission for direct lighting (on top of EmissiveLightScale). Read at the scene build.</summary>
        public float lightScale = 2.5f;
        /// <summary>-ultrart-lava-glow: multiplies the emission reflection, bounce and cache rays see on the lava surface.</summary>
        public float glow = 1f;
        /// <summary>-ultrart-lava-pointlights: brightness of the game's point lights that stand in for the lava glow (lights
        /// over a traced lava surface), now that the lava itself emits. 1 = unchanged.</summary>
        public float pointLightScale = 0.15f;
        /// <summary>Emissive triangles of lava are split until no larger than this (m^2), for less noise next to big surfaces.</summary>
        public float maxTriangleArea = 1f;
        /// <summary>Per lava submesh, at most this many emissive triangles (the split area grows to fit).</summary>
        public int maxTrianglesPerSurface = 6000;
        /// <summary>-ultrart-lava-camera: in the replace modes (relight, pt-dn, pt) camera rays stop at the lava and see this
        /// fraction of its emission instead of the RT-lit floor under it; the game's additive lava is drawn on top. 0 = they
        /// pass through (always so in hybrid, where the game's floor colour is kept).</summary>
        public float cameraGlow = 0.8f;
    }

    /// <summary>
    /// Lava, acid and other glowing liquids: ULTRAKILL/Master materials drawn additively (TRANSPARENCY, _BlendMode 3) in the
    /// transparent queue. Additive glows that are not surfaces (light shafts, explosions, heat haze) are excluded by name
    /// and DISTANCE_FADE. Opaque lava ("Lava Opaque", EMISSIVE) is ordinary traced geometry already.
    /// </summary>
    internal static class LavaClassifier
    {
        private static readonly Regex Name = new Regex(@"Lava|Acid|Magma|Molten", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Dictionary<Material, bool> Cache = new Dictionary<Material, bool>();

        public static bool IsLava(Material m)
        {
            if (m == null) return false;
            if (Cache.TryGetValue(m, out bool k)) return k;
            if (Cache.Count > 20000) Cache.Clear();
            return Cache[m] = Classify(m);
        }

        private static bool Classify(Material m)
        {
            if (m.shader == null || m.shader.name != "ULTRAKILL/Master") return false;
            if (!m.IsKeywordEnabled("TRANSPARENCY") || m.IsKeywordEnabled("DISTANCE_FADE")) return false;
            if (!m.HasProperty("_BlendMode") || Mathf.RoundToInt(m.GetFloat("_BlendMode")) != 3) return false;
            return Name.IsMatch(MaterialClassifier.CleanName(m));
        }

        /// <summary>Lava-like emitters (our lava proxies and the game's opaque lava): their emissive triangles get split.</summary>
        public static bool IsLavaEmitter(Material proxy) =>
            proxy != null && (SceneProxies.IsLavaProxy(proxy) || Name.IsMatch(proxy.name));
    }

    /// <summary>
    /// Lava proxies (docs/lava.md): each lava renderer gets a proxy with only its lava submeshes, UltraRT/LavaProxy
    /// materials (black, fullbright, emissive) and instance mask <see cref="LavaMask"/> = EmitterMask (0x08), which shadow
    /// rays (0x05) skip and every other ray (ULTRART_MASK_SCENE) sees; camera rays skip it in the any-hit shader. The
    /// proxy materials are per renderer so they can follow its ScrollingTexture offset. EmissiveLights picks the proxies up
    /// as emitters (SceneProxies.Emitters.cs) like any emissive dynamic proxy.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>No free instance mask bit is left (0x80 is the player model), and glowing fixtures already have the
        /// right visibility: seen by camera/bounce/reflection/cache rays, never by shadow rays.</summary>
        public const uint LavaMask = EmitterMask;

        /// <summary>Set by the plugin before a scene build (null = no lava proxies).</summary>
        public static Shader LavaShader;
        public static LavaSettings Lava = new LavaSettings();

        private sealed class LavaEntry
        {
            public Renderer Source;
            public DynamicProxy Proxy;
            public Material[] Materials; // our per-renderer lava materials (null slots: not lava)
        }

        private readonly List<LavaEntry> _lava = new List<LavaEntry>();
        private readonly HashSet<DynamicProxy> _lavaProxies = new HashSet<DynamicProxy>();
        private bool _lavaActive = true;

        /// <summary>Evaluate: lava proxies stay out of the RTAS while LavaSettings.active is off.</summary>
        private bool LavaHidden(DynamicProxy d) => !Lava.active && _lavaProxies.Contains(d);
        private readonly List<string> _lavaLog = new List<string>();
        private int _lavaStatic;
        private static readonly HashSet<Material> LavaProxies = new HashSet<Material>();
        private static MaterialPropertyBlock _lavaBlock;
        private static readonly int ScrollOffsetId = Shader.PropertyToID("_ScrollOffset");
        private static readonly int ScrollTimeId = Shader.PropertyToID("_ScrollTime");
        private static readonly int EmissiveIntensityId = Shader.PropertyToID("_EmissiveIntensity");

        public static bool IsLavaProxy(Material m) => m != null && LavaProxies.Contains(m);

        /// <summary>Lava renderers with a proxy (active or not).</summary>
        public int LavaCount => _lava.Count;

        /// <summary>Bumped when a lava proxy enters or leaves the RTAS (the plugin re-picks the lights it dims).</summary>
        public int LavaVersion { get; private set; }
        private int _lavaVisibleMask;

        /// <summary>World bounds and emission colour (chromaticity) of the lava surfaces currently in the RTAS.</summary>
        public void ActiveLava(List<(Bounds bounds, Color color)> output)
        {
            output.Clear();
            foreach (var e in _lava)
            {
                if (!e.Proxy.Visible || e.Source == null) continue;
                var m = e.Materials.FirstOrDefault(x => x != null);
                var c = m != null ? m.GetColor("_Color") + m.GetColor("_GameEmissive") : Color.white;
                output.Add((e.Source.bounds, c));
            }
        }

        /// <summary>Registers the renderer's lava submeshes, if any. Returns true if a lava proxy was made.</summary>
        private bool TryAddLava(MeshRenderer r, Mesh mesh)
        {
            if (!Lava.enabled || LavaShader == null) return false;
            var mats = r.sharedMaterials;
            if (!mats.Any(LavaClassifier.IsLava)) return false;
            if (r.isPartOfStaticBatch)
            {
                // Combined meshes would need StaticGroups support (8-3 has two batched lava cubes and one acid pool).
                _lavaStatic++;
                if (_lavaLog.Count < 40) _lavaLog.Add($"  skipped static-batched '{GameControlPath(r.transform)}' mat '{mats.First(LavaClassifier.IsLava).name}'");
                return false;
            }
            var flags = new RayTracingSubMeshFlags[Math.Max(1, mesh.subMeshCount)];
            var proxyMats = new Material[flags.Length];
            var ours = new Material[flags.Length];
            bool any = false;
            for (int i = 0; i < flags.Length; i++)
            {
                var m = i < mats.Length ? mats[i] : null;
                bool lava = LavaClassifier.IsLava(m);
                // No ClosestHitOnly: the any-hit shader lets camera rays through.
                flags[i] = lava ? RayTracingSubMeshFlags.Enabled : RayTracingSubMeshFlags.Disabled;
                if (lava) { ours[i] = LavaProxyMaterial(m); any = true; }
                proxyMats[i] = lava ? ours[i] : _disabledMaterial;
            }
            if (!any) return false;
            var proxy = CreateMeshProxy($"{r.name} (lava) [UltraRT]", r.transform, mesh, proxyMats);
            // One-sided even for the game's NoCull lava: lava lights upwards, never the channel floor below it.
            AddDynamic(r, proxy, flags, mask: LavaMask, twoSided: false);
            _lava.Add(new LavaEntry { Source = r, Proxy = _dynamic[_dynamic.Count - 1], Materials = ours });
            _lavaProxies.Add(_dynamic[_dynamic.Count - 1]);
            if (_lavaLog.Count < 40)
            {
                var src = mats.First(LavaClassifier.IsLava);
                _lavaLog.Add($"  '{GameControlPath(r.transform)}' mat '{src.name}' layer {r.gameObject.layer} at {r.bounds.center:F1} size {r.bounds.size:F1}" +
                             $"{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}{(r.GetComponent("ScrollingTexture") != null ? " scrolling" : "")}");
            }
            return true;
        }

        /// <summary>
        /// The game draws lava additively (SrcAlpha, One): colour = _MainTex * _Color * alpha, unlit (VERTEX_LIGHTING off),
        /// plus _EmissiveTex * _EmissiveColor * _EmissiveIntensity with EMISSIVE. Lit materials (acid: VERTEX_LIGHTING)
        /// emit only their EMISSIVE part; the rest is the game's lighting, not a glow.
        /// </summary>
        private Material LavaProxyMaterial(Material src)
        {
            var m = new Material(LavaShader) { name = src.name + " [UltraRT lava]" };
            float alpha = src.HasProperty("_Color") ? src.GetColor("_Color").a : 1f;
            if (src.HasProperty("_Opacity")) alpha *= src.GetFloat("_Opacity");
            if (src.HasProperty("_MainTex") && Tex2D(src, "_MainTex") is Texture tex)
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            bool lit = src.IsKeywordEnabled("VERTEX_LIGHTING");
            var c = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
            m.SetColor("_Color", lit ? Color.black : new Color(c.r * alpha, c.g * alpha, c.b * alpha, 1f));
            if (src.IsKeywordEnabled("EMISSIVE") && src.HasProperty("_EmissiveColor"))
            {
                if (src.HasProperty("_EmissiveTex") && Tex2D(src, "_EmissiveTex") is Texture etex)
                {
                    m.SetTexture("_EmissiveTex", etex);
                    m.SetTextureScale("_EmissiveTex", src.GetTextureScale("_EmissiveTex"));
                    m.SetTextureOffset("_EmissiveTex", src.GetTextureOffset("_EmissiveTex"));
                }
                var e = src.GetColor("_EmissiveColor") * (src.HasProperty("_EmissiveIntensity") ? src.GetFloat("_EmissiveIntensity") : 1f) * alpha;
                m.SetColor("_GameEmissive", new Color(e.r, e.g, e.b, 1f));
            }
            m.SetColor("_EmissiveColor", Color.white);
            m.SetFloat("_EmissiveIntensity", Mathf.Max(1e-4f, Lava.glow));
            m.SetFloat("_UseAlbedoAsEmissive", 1f);
            if (src.HasProperty("_ScrollSpeed")) m.SetVector("_ScrollSpeed", src.GetVector("_ScrollSpeed"));
            m.SetFloat("_MatClass", (float)SurfaceClass.Other);
            LavaProxies.Add(m);
            return m;
        }

        /// <summary>
        /// Per traced frame, before the dispatch: ScrollingTexture offsets (the game sets _ScrollOffset in each renderer's
        /// property block), the scroll clock and the settings. Also notices lava entering or leaving the RTAS.
        /// </summary>
        public void UpdateLava()
        {
            if (_lava.Count == 0) return;
            if (Lava.active != _lavaActive)
            {
                // Re-evaluated in the next SyncDynamic (before the next trace).
                _lavaActive = Lava.active;
                foreach (var e in _lava) MarkDirty(e.Proxy);
            }
            if (_lavaBlock == null) _lavaBlock = new MaterialPropertyBlock();
            float time = Time.timeSinceLevelLoad; // the game's _Time.y
            float glow = Mathf.Max(1e-4f, Lava.glow);
            int visible = 0;
            for (int k = 0; k < _lava.Count; k++)
            {
                var e = _lava[k];
                if (!e.Proxy.Visible || e.Source == null) continue;
                visible = visible * 31 + k + 1;
                Vector4 offset = Vector4.zero;
                if (e.Source.HasPropertyBlock())
                {
                    e.Source.GetPropertyBlock(_lavaBlock);
                    offset = _lavaBlock.GetVector(ScrollOffsetId);
                }
                foreach (var m in e.Materials)
                {
                    if (m == null) continue;
                    m.SetVector(ScrollOffsetId, offset);
                    m.SetFloat(ScrollTimeId, time);
                    m.SetFloat(EmissiveIntensityId, glow);
                }
            }
            if (visible != _lavaVisibleMask) { _lavaVisibleMask = visible; LavaVersion++; }
        }

        private void LogLava()
        {
            if (_lava.Count + _lavaStatic == 0) return;
            _log.LogInfo($"Lava surfaces: {_lava.Count} renderers traced as emitters (mask 0x{LavaMask:X2}), " +
                         $"skipped static-batched {_lavaStatic}{(LavaShader == null ? " (lava shader missing)" : "")}");
            foreach (var line in _lavaLog) _log.LogInfo(line);
            _lavaLog.Clear();
        }

        private void DisposeLava()
        {
            foreach (var e in _lava)
                foreach (var m in e.Materials)
                    if (m != null) { LavaProxies.Remove(m); Object.Destroy(m); }
            _lava.Clear();
            _lavaProxies.Clear();
            _lavaStatic = 0;
            _lavaVisibleMask = 0;
            LavaVersion++;
        }
    }
}
