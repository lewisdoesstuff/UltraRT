using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Blended veils behind portals (docs/scene-proxies.md, "Blended veils"). Read every frame unless noted.</summary>
    internal sealed class VeilSettings
    {
        /// <summary>-ultrart-noveils: no veil proxies (read at the scene build) and no veil pass.</summary>
        public bool enabled = true;
        /// <summary>Multiplies the layers' colour.</summary>
        public float gain = 1f;
        /// <summary>Multiplies how much of the image the layers cover.</summary>
        public float cover = 0.6f;
        /// <summary>The game's distance fog for the path behind a portal (the fog pass stops at the portal's plane):
        /// 0 = none, 1 = the game's. Needs the volumetrics' game fog (vol.gameFog).</summary>
        public float fog = 1f;
        /// <summary>The fog alone (no veil in view) runs the pass only if the level's fog ends within this distance
        /// (m; linear fog) or is exponential: a mirror in a level with thin fog is not worth a dispatch.</summary>
        public float fogOnlyWithin = 20000f;
        /// <summary>Veil pass resolution against the screen's.</summary>
        public float scale = 0.5f;
        /// <summary>Debug: 1 = also on pixels not seen through a portal (there the game draws the layers itself: doubled).</summary>
        public int everywhere = 0;
        /// <summary>Materials whose name contains one of these (comma separated) are not veils for the pass.</summary>
        public string skip = "";
        /// <summary>Also the game's fake light cones (LightPillar materials) behind portals (SceneProxies.BeamVeil).
        /// Read at the scene build.</summary>
        public bool beams = true;
        /// <summary>Light cones take their vertex colours (the fade from lamp to floor) by the material's _VertexColors
        /// toggle too, not only its keyword. Read at the scene build. 2026-11-19.</summary>
        public bool beamVertexColours = true;
    }

    /// <summary>
    /// Veil proxies: each renderer with a <see cref="BlendedVeil"/> material gets a proxy with only those submeshes,
    /// UltraRT/VeilProxy materials (any-hit only) and the transparent mask (0x10). Scene rays never see them; the veil
    /// ray of VeilTrace.raytrace collects them. Static-batched veils are skipped (none known).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Set by the plugin before a scene build (null = no veil proxies).</summary>
        public static Shader VeilShader;
        public static VeilSettings Veils = new VeilSettings();

        private sealed class VeilEntry
        {
            public Renderer Source;
            public DynamicProxy Proxy;
            public Material[] Materials; // our per-renderer veil materials (null slots: not a veil)
        }

        private readonly List<VeilEntry> _veils = new List<VeilEntry>();
        private readonly List<string> _veilLog = new List<string>();
        private static MaterialPropertyBlock _veilBlock;

        /// <summary>Veil renderers with a proxy.</summary>
        public int VeilCount => _veils.Count;
        /// <summary>Veil proxies in the RTAS this frame (UpdateVeils).</summary>
        public int VeilsVisible { get; private set; }
        /// <summary>Mini acceleration structure for VeilTrace.raytrace: only the active veil proxies and portal side quads
        /// (<= ~40 instances instead of the full scene RTAS, eliminating ~0.55-0.92 ms of render-thread SBT rebuild).</summary>
        public RayTracingAccelerationStructure VeilRtas { get; private set; }
        public int VeilRtasInstances => _veilIn.Count;

        private readonly Dictionary<Renderer, bool> _veilIn = new Dictionary<Renderer, bool>();
        private readonly List<Renderer> _veilGone = new List<Renderer>();
        private RayTracingAccelerationStructure _veilRtasOwner;
        private static readonly RayTracingSubMeshFlags[] VeilPortalFlags = { RayTracingSubMeshFlags.Enabled };

        /// <summary>Syncs <see cref="VeilRtas"/> with active veil proxies and portal side quads. Call before building it.</summary>
        public void UpdateVeilRtas()
        {
            if (Rtas == null) return;
            if (!ReferenceEquals(_veilRtasOwner, Rtas)) DisposeVeilRtas();
            _veilRtasOwner = Rtas;
            if (VeilRtas == null)
                VeilRtas = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.RASSettings(
                    RayTracingAccelerationStructure.ManagementMode.Manual, RayTracingAccelerationStructure.RayTracingModeMask.Everything, ~0));
            _veilGone.Clear();
            foreach (var r in _veilIn.Keys) _veilGone.Add(r);
            foreach (var r in _veilGone) _veilIn[r] = false;

            foreach (var e in _veils)
            {
                var d = e.Proxy;
                if (!d.Visible || d.Proxy == null) continue;
                if (_veilIn.ContainsKey(d.Proxy))
                {
                    _veilIn[d.Proxy] = true;
                    if (d.Moved) VeilRtas.UpdateInstanceTransform(d.Proxy);
                }
                else
                {
                    TraceSkin("veil add", d);
                    VeilRtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: InstanceCcw(d), mask: TransparentMask, id: d.Id);
                    _veilIn[d.Proxy] = true;
                }
            }
            foreach (var s in _portalSideList)
            {
                if (!s.InRtas || s.Proxy == null) continue;
                if (_veilIn.ContainsKey(s.Proxy))
                {
                    _veilIn[s.Proxy] = true;
                    VeilRtas.UpdateInstanceTransform(s.Proxy);
                }
                else
                {
                    VeilRtas.AddInstance(s.Proxy, VeilPortalFlags, enableTriangleCulling: false, frontTriangleCounterClockwise: false, mask: PortalMask, id: 0);
                    _veilIn[s.Proxy] = true;
                }
            }
            _veilGone.Clear();
            foreach (var kv in _veilIn) if (!kv.Value) _veilGone.Add(kv.Key);
            foreach (var r in _veilGone)
            {
                if (r != null) try { VeilRtas.RemoveInstance(r); } catch (Exception) { }
                _veilIn.Remove(r);
            }
        }

        private void DisposeVeilRtas()
        {
            VeilRtas?.Dispose();
            VeilRtas = null;
            _veilIn.Clear();
            _veilRtasOwner = null;
        }

        private static bool VeilSkipped(Material m)
        {
            if (string.IsNullOrEmpty(Veils.skip)) return false;
            foreach (var part in Veils.skip.Split(','))
                if (part.Length > 0 && m.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// A static-batched veil (8-2's light cones are): the renderer's mesh is the batch's combined mesh, in the batch
        /// root's space, and the renderer owns submeshes subMeshStartIndex.. of it. The proxy uses that mesh as it is,
        /// under the batch root, with every submesh but the renderer's veils disabled.
        /// </summary>
        private bool TryAddStaticVeil(MeshRenderer r, Mesh mesh, Material[] mats)
        {
            int n = mesh.subMeshCount, start = r.subMeshStartIndex;
            if (n == 0 || n > 4096) return false;
            var flags = new RayTracingSubMeshFlags[n];
            var proxyMats = new Material[n];
            var ours = new Material[n];
            for (int k = 0; k < n; k++) { flags[k] = RayTracingSubMeshFlags.Disabled; proxyMats[k] = _disabledMaterial; }
            bool any = false;
            Material first = null;
            for (int i = 0; i < mats.Length && start + i < n; i++)
            {
                var m = mats[i];
                if (!PortalVeil(m) || VeilSkipped(m)) continue;
                flags[start + i] = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.UniqueAnyHitCalls;
                proxyMats[start + i] = ours[start + i] = VeilProxyMaterial(m);
                first = first ?? m;
                any = true;
            }
            if (!any) return false;
            var proxy = CreateMeshProxy($"{r.name} (static veil) [UltraRT]", StaticBatchRoot?.GetValue(r) as Transform, mesh, proxyMats);
            AddDynamic(r, proxy, flags, mask: TransparentMask, twoSided: true);
            _veils.Add(new VeilEntry { Source = r, Proxy = _dynamic[_dynamic.Count - 1], Materials = ours });
            _veilProxySet.Add(_dynamic[_dynamic.Count - 1]);
            if (_veilLog.Count < 24)
                _veilLog.Add($"  static-batched '{GameControlPath(r.transform)}' mat '{first.name}' submesh {start} of {n} in '{mesh.name}' size {r.bounds.size:F0}");
            return true;
        }

        /// <summary>Registers the renderer's veil submeshes, if any. Returns true if a veil proxy was made.</summary>
        private bool TryAddVeil(MeshRenderer r, Mesh mesh)
        {
            var mats = r.sharedMaterials;
            if (!Veils.enabled || VeilShader == null) return false;
            if (!mats.Any(PortalVeil)) return false;
            if (r.isPartOfStaticBatch) return TryAddStaticVeil(r, mesh, mats);
            var flags = new RayTracingSubMeshFlags[Math.Max(1, mesh.subMeshCount)];
            var proxyMats = new Material[flags.Length];
            var ours = new Material[flags.Length];
            bool any = false;
            for (int i = 0; i < flags.Length; i++)
            {
                var m = i < mats.Length ? mats[i] : null;
                bool veil = PortalVeil(m) && !VeilSkipped(m);
                // Any-hit only, once per triangle: a second call would blend the layer twice.
                flags[i] = veil ? RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.UniqueAnyHitCalls : RayTracingSubMeshFlags.Disabled;
                if (veil) { ours[i] = VeilProxyMaterial(m); any = true; }
                proxyMats[i] = veil ? ours[i] : _disabledMaterial;
            }
            if (!any) return false;
            var proxy = CreateMeshProxy($"{r.name} (veil) [UltraRT]", r.transform, mesh, proxyMats);
            AddDynamic(r, proxy, flags, mask: TransparentMask, twoSided: true);
            _veils.Add(new VeilEntry { Source = r, Proxy = _dynamic[_dynamic.Count - 1], Materials = ours });
            _veilProxySet.Add(_dynamic[_dynamic.Count - 1]);
            if (_veilLog.Count < 24)
            {
                var src = mats.First(PortalVeil);
                _veilLog.Add($"  '{GameControlPath(r.transform)}' mat '{src.name}' layer {r.gameObject.layer} size {r.bounds.size:F0}" +
                             $"{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}{(r.GetComponent<ScrollingTexture>() != null ? " scrolling" : "")}");
            }
            return true;
        }

        /// <summary>
        /// The game draws a veil with SrcAlpha, OneMinusSrcAlpha: colour = _MainTex x _Color x vertex colour (unlit), alpha
        /// the same product's (times _Opacity). Lit veils (VERTEX_LIGHTING) get the same colour: their lighting is not known here.
        /// </summary>
        private Material VeilProxyMaterial(Material src)
        {
            var m = new Material(VeilShader) { name = src.name + " [UltraRT veil]" };
            if (src.HasProperty("_MainTex") && Tex2D(src, "_MainTex") is Texture tex)
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            var c = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
            if (src.HasProperty("_Opacity")) c.a *= src.GetFloat("_Opacity");
            // The pass's cover (0.6) was set by eye for 8-1's cloud layers; a light cone is drawn as the game draws it.
            bool additive = BeamVeil(src) && Mathf.RoundToInt(src.GetFloat("_BlendMode")) == 3;
            if (BeamVeil(src) && !additive) c.a = Mathf.Min(1f, c.a / Mathf.Max(0.05f, Veils.cover));
            m.SetFloat("_Additive", additive ? 1f : 0f);
            m.SetColor("_Tint", c);
            // A light cone fades by its vertex colours (bright at the lamp, gone at the floor; LightShafts reads the same
            // gradient). Its keyword list has _VERTEXCOLORS_ON, but IsKeywordEnabled returns false for it (Unity 2022
            // checks the shader's declared keywords; ULTRAKILL/Master branches on the _VertexColors property). Without the
            // fade, the cone behind 8-2's Reception portal was an even box several times the game's brightness
            // (2026-11-19, docs/portals.md section 12). Cones take the property as well; other veils keep the keyword test.
            bool vertexColours = src.IsKeywordEnabled("_VERTEXCOLORS_ON") || src.IsKeywordEnabled("VERTEX_COLORS")
                                 || (BeamVeil(src) && Veils.beamVertexColours && src.HasProperty("_VertexColors") && src.GetFloat("_VertexColors") > 0.5f);
            m.SetFloat("_VertexColors", vertexColours ? 1f : 0f);
            if (src.IsKeywordEnabled("SCROLLING") && src.HasProperty(IdScrollSpeed)) m.SetVector(IdScrollSpeed, src.GetVector(IdScrollSpeed));
            if (_veilLog.Count < 24) _veilLog.Add($"  material {DescribeVeilMaterial(src)} [keywords: {string.Join(" ", src.shaderKeywords)}; vertex colours {(vertexColours ? "on" : "off")}]");
            return m;
        }

        /// <summary>The source material's colours and non-zero numbers, for matching the game's look.</summary>
        private static string DescribeVeilMaterial(Material src)
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            var sh = src.shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                string n = sh.GetPropertyName(i);
                switch (sh.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector:
                        var v = src.GetVector(n);
                        if (v != Vector4.zero && v != Vector4.one) parts.Add($"{n}=({v.x.ToString("G3", inv)},{v.y.ToString("G3", inv)},{v.z.ToString("G3", inv)},{v.w.ToString("G3", inv)})");
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        float f = src.GetFloat(n);
                        if (f != 0f && parts.Count < 60) parts.Add($"{n}={f.ToString("G3", inv)}");
                        break;
                    case ShaderPropertyType.Texture:
                        var t = src.GetTexture(n);
                        if (t != null) parts.Add($"{n}='{t.name}' {t.width}x{t.height} ST ({src.GetTextureScale(n).x.ToString("G3", inv)},{src.GetTextureScale(n).y.ToString("G3", inv)})");
                        break;
                }
            }
            return $"'{src.name}': {string.Join(" ", parts)}";
        }

        /// <summary>Per traced frame, before the dispatch: scroll offsets and clock; counts the veils in the RTAS.</summary>
        public void UpdateVeils()
        {
            VeilsVisible = 0;
            if (_veils.Count == 0) return;
            if (_veilBlock == null) _veilBlock = new MaterialPropertyBlock();
            float time = Time.timeSinceLevelLoad; // the game's _Time.y
            foreach (var e in _veils)
            {
                if (!e.Proxy.Visible || e.Source == null) continue;
                VeilsVisible++;
                Vector4 offset = Vector4.zero;
                if (e.Source.HasPropertyBlock())
                {
                    e.Source.GetPropertyBlock(_veilBlock);
                    offset = _veilBlock.GetVector(ScrollOffsetId);
                }
                foreach (var m in e.Materials)
                {
                    if (m == null) continue;
                    m.SetVector(ScrollOffsetId, offset);
                    m.SetFloat(ScrollTimeId, time);
                }
            }
        }

        private void LogVeilProxies()
        {
            if (_veils.Count == 0 && _veilLog.Count == 0) return;
            _log.LogInfo($"Veil proxies: {_veils.Count} renderers (mask 0x{TransparentMask:X2}, collected behind portals by the veil pass)" +
                         $"{(VeilShader == null ? " (veil shader missing)" : "")}");
            foreach (var line in _veilLog) _log.LogInfo(line);
            _veilLog.Clear();
        }

        private void DisposeVeils()
        {
            DisposeVeilRtas();
            foreach (var e in _veils)
                foreach (var m in e.Materials)
                    if (m != null) Object.Destroy(m);
            _veils.Clear();
            _veilProxySet.Clear();
            VeilsVisible = 0;
        }
    }
}
