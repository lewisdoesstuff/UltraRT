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

        /// <summary>Static group part bit for transparent slots (bit 0 two-sided, bit 1 sky wall): own instances, mask 0x10.</summary>
        private const int TransparentPart = 4;

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

        /// <summary>True if the renderer has water/glass/blood materials that should be traced (not a sky decoration).</summary>
        private bool TransparentCandidate(Renderer r, Material[] mats)
        {
            if (!Transparents.enabled || !mats.Any(TransparentSource)) return false;
            // Sky decorations (1-1's LimboSkybox has a water sprite): drawn as part of the sky, not a surface.
            for (var t = r.transform; t != null; t = t.parent)
                if (t.name.IndexOf("Skybox", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        /// <summary>Registers the renderer's water/glass submeshes, if any. Returns true if a transparent proxy was made.
        /// Static-batched renderers are not handled here: their submeshes become transparent slots of the static groups
        /// (StaticGroups.cs, part bit <see cref="TransparentPart"/>), see <see cref="NoteStaticTransparent"/>.</summary>
        private bool TryAddTransparent(MeshRenderer r, Mesh mesh)
        {
            var mats = r.sharedMaterials;
            if (r.isPartOfStaticBatch || !TransparentCandidate(r, mats)) return false;
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

        /// <summary>
        /// A static-batched source with transparent slots (at the build, or a late checkpoint clone): the overlay, the
        /// under-water test and the log. Its slots are already in the static groups, as part <see cref="TransparentPart"/>.
        /// </summary>
        private void NoteStaticTransparent(MeshRenderer r, IEnumerable<TransparentKind> kinds, bool late)
        {
            var kindList = kinds.Distinct().ToList();
            if (kindList.Count == 0) return;
            var kind = kindList.Contains(TransparentKind.Water) ? TransparentKind.Water : kindList.Contains(TransparentKind.Blood) ? TransparentKind.Blood : TransparentKind.Glass;
            if (kind == TransparentKind.Water) _staticWater++;
            else if (kind == TransparentKind.Blood) _staticBlood++;
            else _staticGlass++;
            if (kind != TransparentKind.Glass) _waterSources.Add(r);
            if (kind == TransparentKind.Blood)
                foreach (var m in r.sharedMaterials) if (TransparentClassifier.Of(m) == TransparentKind.Blood) _bloodMaterials.Add(m.name);
            if (_overlays == null && OverlayShader != null) _overlays = new TransparentOverlays(OverlayShader, Transparents);
            bool overlay = _overlays != null && _overlays.Attach(r);
            if (!overlay) _transparentNoOverlay++;
            if (!late && (kind != TransparentKind.Glass || _transparentLog.Count < 40))
            {
                var src = r.sharedMaterials.LastOrDefault(TransparentSource);
                _transparentLog.Add($"  {kind} (static batch, submesh {r.subMeshStartIndex}) '{GameControlPath(r.transform)}' mat '{src?.name}' at {r.bounds.center:F1} size {r.bounds.size:F1}" +
                                    $"{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}{(overlay ? "" : " NO OVERLAY")}");
            }
        }

        private int _staticWater, _staticGlass, _staticBlood;

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
            if (_transparentWater + _transparentGlass + _transparentBlood + _staticWater + _staticGlass + _staticBlood + _transparentStatic == 0) return;
            _log.LogInfo($"Transparent surfaces: {_transparentWater} water, {_transparentBlood} blood ({string.Join(", ", _bloodMaterials)}) and {_transparentGlass} glass renderers traced (mask 0x10), " +
                         $"static-batched {_staticWater} water, {_staticBlood} blood, {_staticGlass} glass; " +
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

        private void CountTransparent(bool added) => ActiveTransparent += added ? 1 : -1;

        private void CountTransparent(DynamicProxy d, bool added)
        {
            if (d.Mask == TransparentMask) ActiveTransparent += added ? 1 : -1;
        }
    }
}
