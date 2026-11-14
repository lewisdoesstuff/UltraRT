using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Water and glass (docs/reflections.md, "Water and glass"): transparent renderers get a second, separate proxy with
    /// only their water/glass submeshes, in the RTAS under <see cref="TransparentMask"/>. Every existing ray masks it out
    /// (ULTRART_MASK_SCENE), so they neither block light nor show up in the opaque G-buffer; only the transparent pass
    /// sees them. The source renderer also gets the overlay material (TransparentOverlays) that composites the result.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public const uint TransparentMask = 0x10;

        /// <summary>Set by the plugin before a scene build: the overlay shader (null = no overlays) and the settings.</summary>
        public static Shader OverlayShader;
        public static TransparentSettings Transparents = new TransparentSettings();

        private TransparentOverlays _overlays;
        private readonly Dictionary<Material, Material> _transparentCache = new Dictionary<Material, Material>();
        private int _transparentWater, _transparentGlass, _transparentStatic, _transparentNoOverlay, _transparentBlood;
        private readonly HashSet<string> _bloodMaterials = new HashSet<string>();
        private readonly List<string> _transparentLog = new List<string>();
        private readonly List<Renderer> _waterSources = new List<Renderer>();

        /// <summary>
        /// True when p is inside an active water renderer's bounds: the camera is under water, where the game draws its
        /// own underwater look and mirror-like water reflections would be wrong (total internal reflection is not done).
        /// </summary>
        public bool InsideWater(Vector3 p)
        {
            if (ActiveTransparent <= 0) return false;
            foreach (var r in _waterSources)
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.bounds.Contains(p)) return true;
            return false;
        }

        /// <summary>Transparent instances currently in the RTAS (the transparent pass is skipped when 0).</summary>
        public int ActiveTransparent { get; private set; }

        private static bool TransparentSource(Material m) => TransparentClassifier.Of(m) != TransparentKind.None;

        /// <summary>Registers the renderer's water/glass submeshes, if any. Returns true if a transparent proxy was made.</summary>
        private bool TryAddTransparent(MeshRenderer r, Mesh mesh)
        {
            if (!Transparents.enabled) return false;
            var mats = r.sharedMaterials;
            if (!mats.Any(TransparentSource)) return false;
            // Sky decorations (1-1's LimboSkybox has a water sprite): drawn as part of the sky, not a surface.
            for (var t = r.transform; t != null; t = t.parent)
                if (t.name.IndexOf("Skybox", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (r.isPartOfStaticBatch)
            {
                // Combined meshes would need StaticGroups support; none of the surveyed water or glass is batched.
                _transparentStatic++;
                return false;
            }
            var flags = new RayTracingSubMeshFlags[System.Math.Max(1, mesh.subMeshCount)];
            var proxyMats = new Material[flags.Length];
            TransparentKind kind = TransparentKind.None;
            for (int i = 0; i < flags.Length; i++)
            {
                var m = i < mats.Length ? mats[i] : null;
                var k = TransparentClassifier.Of(m);
                flags[i] = k != TransparentKind.None ? RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly : RayTracingSubMeshFlags.Disabled;
                proxyMats[i] = k != TransparentKind.None ? TransparentProxy(m, k) : _disabledMaterial;
                if (k != TransparentKind.None) kind = k;
            }
            if (kind == TransparentKind.None) return false;

            var proxy = CreateMeshProxy($"{r.name} (transparent) [UltraRT]", r.transform, mesh, proxyMats);
            bool twoSided = mats.Any(m => TransparentSource(m) && SourceTwoSided(m));
            AddDynamic(r, proxy, flags, mask: TransparentMask, twoSided: twoSided);
            if (kind == TransparentKind.Water) { _transparentWater++; _waterSources.Add(r); }
            if (kind == TransparentKind.Blood)
            {
                _transparentBlood++; _waterSources.Add(r);
                foreach (var m in r.sharedMaterials) if (TransparentClassifier.Of(m) == TransparentKind.Blood) _bloodMaterials.Add(m.name);
            }
            else _transparentGlass++;

            if (_overlays == null && OverlayShader != null) _overlays = new TransparentOverlays(OverlayShader, Transparents);
            if (_overlays == null || !_overlays.Attach(r)) _transparentNoOverlay++;
            if (_transparentLog.Count < 40)
                _transparentLog.Add($"  {kind} '{GameControlPath(r.transform)}' mat '{mats.Last(TransparentSource).name}' at {r.bounds.center:F1} size {r.bounds.size:F1}" +
                                    $"{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}");
            return true;
        }

        private Material TransparentProxy(Material src, TransparentKind kind)
        {
            if (_transparentCache.TryGetValue(src, out var m)) return m;
            m = new Material(_proxyShader) { name = src.name + " [UltraRT transparent]" };
            if (src.HasProperty("_MainTex") && Tex2D(src, "_MainTex") is Texture tex)
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            if (src.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));
            MaterialParams.Apply(m, TransparentClassifier.Params(kind, Transparents), null);
            if (SourceTwoSided(src)) _twoSidedProxies.Add(m);
            _transparentCache[src] = m;
            return m;
        }

        private void LogTransparents()
        {
            if (_transparentWater + _transparentGlass + _transparentBlood + _transparentStatic == 0) return;
            _log.LogInfo($"Transparent surfaces: {_transparentWater} water, {_transparentBlood} blood ({string.Join(", ", _bloodMaterials)}) and {_transparentGlass} glass renderers traced (mask 0x10), " +
                         $"overlays {_overlays?.Count ?? 0}{(OverlayShader == null ? " (overlay shader missing)" : "")}, without overlay {_transparentNoOverlay}, " +
                         $"skipped static-batched {_transparentStatic}");
            foreach (var line in _transparentLog) _log.LogInfo(line);
            _transparentLog.Clear();
        }

        private void DisposeTransparents()
        {
            _overlays?.Dispose();
            _overlays = null;
            foreach (var m in _transparentCache.Values) if (m != null) Object.Destroy(m);
            _transparentCache.Clear();
            _waterSources.Clear();
            ActiveTransparent = 0;
        }

        private void CountTransparent(DynamicProxy d, bool added)
        {
            if (d.Mask == TransparentMask) ActiveTransparent += added ? 1 : -1;
        }
    }
}
