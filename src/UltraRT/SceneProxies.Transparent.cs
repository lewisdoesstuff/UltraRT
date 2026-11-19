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
        /// <summary>Limbo water is not faded around the player while traced (TransparentOverlays.SetFlatFade).</summary>
        public void SetFlatWaterFade(bool on) => _overlays?.SetFlatFade(on);
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
            _dynamic[_dynamic.Count - 1].ShadowMask = GlassShadowBit(flags, proxyMats); // stained glass (SceneProxies.Glass.cs)
            if (kind == TransparentKind.Water) { _transparentWater++; _waterSources.Add(r); }
            if (kind == TransparentKind.Blood)
            {
                _transparentBlood++; _waterSources.Add(r);
                foreach (var m in r.sharedMaterials) if (TransparentClassifier.Of(m) == TransparentKind.Blood) _bloodMaterials.Add(m.name);
            }
            else _transparentGlass++;

            if (mats.Any(TransparentClassifier.Frosted)) AddFrostedScene(r, mesh, mats);

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

        /// <summary>
        /// Frosted transparents (ice, <see cref="TransparentClassifier.Frosted"/>): a second proxy in the scene under
        /// EmitterMask (0x08: camera, bounce, reflection and cache rays, no shadow rays), whose any-hit lets camera rays
        /// through (_CameraSkip), since the game draws the surface itself. Without it the ice was missing from every RT
        /// reflection, the glass doors next to it included.
        /// </summary>
        private void AddFrostedScene(MeshRenderer r, Mesh mesh, Material[] mats)
        {
            var flags = new RayTracingSubMeshFlags[System.Math.Max(1, mesh.subMeshCount)];
            var proxyMats = new Material[flags.Length];
            for (int i = 0; i < flags.Length; i++)
            {
                var m = i < mats.Length ? mats[i] : null;
                bool on = m != null && TransparentClassifier.Frosted(m);
                flags[i] = on ? RayTracingSubMeshFlags.Enabled : RayTracingSubMeshFlags.Disabled; // not ClosestHitOnly: the any-hit must run
                proxyMats[i] = on ? FrostedProxy(m) : _disabledMaterial;
            }
            var proxy = CreateMeshProxy($"{r.name} (frosted) [UltraRT]", r.transform, mesh, proxyMats);
            AddDynamic(r, proxy, flags, mask: EmitterMask, twoSided: mats.Any(m => TransparentClassifier.Frosted(m) && SourceTwoSided(m)));
            _frostedProxies.Add(_dynamic[_dynamic.Count - 1]);
            _transparentFrosted++;
        }

        private readonly HashSet<DynamicProxy> _frostedProxies = new HashSet<DynamicProxy>();
        private bool _frostedOn = true;

        private bool FrostedHidden(DynamicProxy d) => !Transparents.frosted && _frostedProxies.Contains(d);

        /// <summary>Per traced frame, after SyncDynamic: notices the frosted switch (re-evaluated in the next SyncDynamic).</summary>
        public void UpdateFrosted()
        {
            if (_frostedProxies.Count == 0 || Transparents.frosted == _frostedOn) return;
            _frostedOn = Transparents.frosted;
            foreach (var d in _frostedProxies) MarkDirty(d);
        }

        private int _transparentFrosted;

        private Material FrostedProxy(Material src)
        {
            if (_frostedCache.TryGetValue(src, out var m)) return m;
            m = new Material(TransparentProxy(src, TransparentKind.Glass)) { name = src.name + " [UltraRT frosted]" };
            m.SetFloat("_CameraSkip", 1f);
            m.SetFloat("_Transparent", 0f); // in the scene it is a frosted, opaque surface
            _frostedCache[src] = m;
            return m;
        }

        private readonly Dictionary<Material, Material> _frostedCache = new Dictionary<Material, Material>();

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
            float opacity = src.HasProperty("_Opacity") ? src.GetFloat("_Opacity") : src.HasProperty("_OpacScale") ? src.GetFloat("_OpacScale") : 1f;
            m.SetFloat("_TransOpacity", opacity);
            if (kind == TransparentKind.Water && _transparentCache.Count < 64)
                _log.LogInfo(System.FormattableString.Invariant($"Transparent proxy: water '{src.name}' colour {(src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white)}, opacity {opacity:F2}, texture '{(m.mainTexture != null ? m.mainTexture.name : "-")}', {(src.IsKeywordEnabled("VERTEX_LIGHTING") ? "vertex lit" : "fullbright")}, keywords [{string.Join(" ", src.shaderKeywords)}]"));
            MaterialParams.Apply(m, TransparentClassifier.Params(kind, Transparents), null);
            m.SetFloat("_Transparent", (float)(int)kind); // pathtrace: paths reflect off it or pass through, tinted
            ApplyTransmit(src, m); // stained glass tints shadow rays (SceneProxies.Glass.cs)
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
                         $"skipped static-batched {_transparentStatic}, frosted (ice) also in the scene {_transparentFrosted}");
            foreach (var line in _transparentLog) _log.LogInfo(line);
            _transparentLog.Clear();
        }

        private void DisposeTransparents()
        {
            _overlays?.Dispose();
            _overlays = null;
            foreach (var m in _transparentCache.Values) if (m != null) Object.Destroy(m);
            _transparentCache.Clear();
            foreach (var m in _frostedCache.Values) if (m != null) Object.Destroy(m);
            _frostedCache.Clear();
            _waterSources.Clear();
            ActiveTransparent = 0;
        }

        private void CountTransparent(bool added) => ActiveTransparent += added ? 1 : -1;

        private readonly HashSet<DynamicProxy> _veilProxySet = new HashSet<DynamicProxy>();

        /// <summary>
        /// The rectangle (in the NDC of <paramref name="viewProj"/>, the trace's projection) that holds every active water,
        /// glass and blood instance in the RTAS (veil proxies, which a camera ray never hits, are left out). False: none
        /// can be on screen, and PathTrace skips its transparent ray at every pixel (docs/performance.md, "Ray counts":
        /// that ray ran at every pixel whenever anything with the transparent mask was active, light cones included).
        /// An instance reaching behind the camera's near plane gives the whole screen.
        /// </summary>
        public bool TransparentScreenRect(Matrix4x4 viewProj, out Vector4 rect)
        {
            rect = new Vector4(1f, 1f, -1f, -1f);
            float bestArea = 0f;
            TransparentRectWidestName = null; _widestArea = 0f;
            foreach (var d in _active)
            {
                if (d.Mask != TransparentMask || _veilProxySet.Contains(d) || d.Source == null) continue;
                AddRect(d.Source.bounds, viewProj, ref rect, d.Source.name, ref bestArea);
            }
            // Static groups: per slot, the bounds of its own vertices (a group's combined mesh spans a level: 8-2's and 8-3's
            // glass parts covered every view). Without them (no CPU copy of the mesh) the part's mesh bounds.
            foreach (var g in _groups)
                for (int pi = 0; pi < PartCount; pi++)
                {
                    var gp = g.Parts[pi];
                    if (gp == null || !gp.InRtas || !gp.Transparent || gp.Filter == null || gp.Filter.sharedMesh == null) continue;
                    var toWorld = gp.Proxy.transform.localToWorldMatrix;
                    var slots = g.Slots[pi];
                    bool whole = slots == null || slots.Count == 0;
                    if (!whole)
                        foreach (var sl in slots)
                        {
                            if (!sl.HasBounds) { whole = true; break; }
                            if (sl.VisibleSources <= 0) continue;
                            AddRect(TransformBounds(sl.LocalBounds, toWorld), viewProj, ref rect, sl.Source != null ? sl.Source.name : gp.Proxy.name, ref bestArea);
                        }
                    if (whole) AddRect(TransformBounds(gp.Filter.sharedMesh.bounds, toWorld), viewProj, ref rect, gp.Proxy.name, ref bestArea);
                }
            // Nothing, or nothing overlapping the trace's view.
            return rect.x <= 1f && rect.y <= 1f && rect.z >= -1f && rect.w >= -1f && rect.x <= rect.z;
        }

        /// <summary>The instance with the widest rectangle in the last <see cref="TransparentScreenRect"/> (diagnostics).</summary>
        public string TransparentRectWidest => TransparentRectWidestName == null ? "-" : $"'{TransparentRectWidestName}' {_widestArea / 4f:P0} of the view";
        public string TransparentRectWidestName { get; private set; }
        private float _widestArea;

        private void AddRect(Bounds b, Matrix4x4 viewProj, ref Vector4 rect, string name, ref float bestArea)
        {
            var r = new Vector4(1f, 1f, -1f, -1f);
            if (!NdcRect(b, viewProj, ref r)) return;
            float area = Mathf.Max(0f, Mathf.Min(r.z, 1f) - Mathf.Max(r.x, -1f)) * Mathf.Max(0f, Mathf.Min(r.w, 1f) - Mathf.Max(r.y, -1f));
            if (area > bestArea) { bestArea = area; _widestArea = area; TransparentRectWidestName = name; }
            rect = new Vector4(Mathf.Min(rect.x, r.x), Mathf.Min(rect.y, r.y), Mathf.Max(rect.z, r.z), Mathf.Max(rect.w, r.w));
        }

        private static readonly Vector4[] _boxH = new Vector4[8];

        /// <summary>The NDC rectangle of the part of a box in front of the camera (its edges clipped at w = 0.05). False:
        /// nothing of it is in front.</summary>
        private static bool NdcRect(Bounds b, Matrix4x4 viewProj, ref Vector4 rect)
        {
            var mn = b.min; var mx = b.max;
            for (int k = 0; k < 8; k++)
                _boxH[k] = viewProj * new Vector4((k & 1) != 0 ? mx.x : mn.x, (k & 2) != 0 ? mx.y : mn.y, (k & 4) != 0 ? mx.z : mn.z, 1f);
            const float near = 0.05f;
            bool any = false;
            for (int k = 0; k < 8; k++)
            {
                var h = _boxH[k];
                if (h.w > near) { Grow(ref rect, h); any = true; }
                // Edges to the corners that differ in one axis bit: where they cross the near plane.
                for (int bit = 1; bit <= 4; bit <<= 1)
                {
                    int j = k ^ bit;
                    if (j < k) continue;
                    var o = _boxH[j];
                    if ((h.w > near) == (o.w > near)) continue;
                    float t = (near - h.w) / (o.w - h.w);
                    Grow(ref rect, h + (o - h) * t);
                    any = true;
                }
            }
            return any;
        }

        private static void Grow(ref Vector4 rect, Vector4 h)
        {
            float x = h.x / h.w, y = h.y / h.w;
            rect.x = Mathf.Min(rect.x, x); rect.y = Mathf.Min(rect.y, y);
            rect.z = Mathf.Max(rect.z, x); rect.w = Mathf.Max(rect.w, y);
        }

        private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var x = m.MultiplyVector(new Vector3(e.x, 0f, 0f)); var y = m.MultiplyVector(new Vector3(0f, e.y, 0f)); var z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            var ext = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(c, 2f * ext);
        }


        private void CountTransparent(DynamicProxy d, bool added)
        {
            if (d.Mask == TransparentMask) ActiveTransparent += added ? 1 : -1;
        }
    }
}
