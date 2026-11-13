using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Unity picks ray tracing hit shaders from each instance's material, and the game's shaders have none.
    /// So we mirror the scene with hidden proxy renderers that use our hit shader, and put only those in the RTAS.
    ///  - Static-batched renderers share one combined mesh: one proxy per combined mesh, materials mapped per submesh.
    ///  - Everything else: one proxy parented to the source, so it follows transforms and activation.
    /// Skinned meshes are not handled yet.
    /// </summary>
    internal sealed class SceneProxies : IDisposable
    {
        private static readonly PropertyInfo StaticBatchRoot =
            typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly ManualLogSource _log;
        private readonly Shader _proxyShader;
        private readonly Material _disabledMaterial;
        private readonly Dictionary<Material, Material> _materialCache = new Dictionary<Material, Material>();
        private readonly HashSet<string> _loggedLayouts = new HashSet<string>();
        private readonly List<GameObject> _proxyObjects = new List<GameObject>();
        private readonly List<(Renderer source, MeshRenderer proxy, bool visible)> _dynamic = new List<(Renderer, MeshRenderer, bool)>();

        public RayTracingAccelerationStructure Rtas { get; private set; }

        public SceneProxies(ManualLogSource log, Shader proxyShader)
        {
            _log = log;
            _proxyShader = proxyShader;
            _disabledMaterial = new Material(proxyShader) { name = "UltraRT Disabled" };
        }

        public void Build()
        {
            Rtas = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.RASSettings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, ~0));

            var renderers = Object.FindObjectsOfType<MeshRenderer>()
                .Where(r => r.enabled && !(r.sharedMaterials.Length == 0))
                .ToList();

            int batchedSources = 0, dynamicProxies = 0, skipped = 0;
            var batches = new Dictionary<Mesh, List<MeshRenderer>>();
            foreach (var r in renderers)
            {
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) { skipped++; continue; }
                if (r.isPartOfStaticBatch)
                {
                    if (!batches.TryGetValue(mesh, out var list)) batches[mesh] = list = new List<MeshRenderer>();
                    list.Add(r);
                    batchedSources++;
                }
                else
                {
                    var proxy = CreateProxy($"{r.name} [UltraRT]", r.transform, mesh, r.sharedMaterials.Select(Proxy).ToArray());
                    _dynamic.Add((r, proxy, false));
                    dynamicProxies++;
                }
            }

            foreach (var kv in batches) AddStaticBatch(kv.Key, kv.Value);

            SyncDynamic(force: true);
            Rtas.Build();
            _log.LogInfo($"Proxies: {batches.Count} static batches (from {batchedSources} renderers), {dynamicProxies} dynamic, {skipped} skipped; " +
                         $"RTAS instances={Rtas.GetInstanceCount()} size={Rtas.GetSize() / (1024.0 * 1024.0):F1} MB, proxy materials={_materialCache.Count}");
        }

        private void AddStaticBatch(Mesh combined, List<MeshRenderer> sources)
        {
            var mats = new Material[combined.subMeshCount];
            var flags = new RayTracingSubMeshFlags[combined.subMeshCount];
            for (int i = 0; i < mats.Length; i++) { mats[i] = _disabledMaterial; flags[i] = RayTracingSubMeshFlags.Disabled; }

            foreach (var r in sources)
            {
                var src = r.sharedMaterials;
                for (int i = 0; i < src.Length; i++)
                {
                    int sub = r.subMeshStartIndex + i;
                    if (sub < 0 || sub >= mats.Length || src[i] == null) continue;
                    mats[sub] = Proxy(src[i]);
                    flags[sub] = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly;
                }
            }

            if (_loggedLayouts.Add(string.Join(",", combined.GetVertexAttributes().Select(a => a.attribute))))
                _log.LogInfo($"Vertex layout of '{combined.name}': " + string.Join(", ",
                    combined.GetVertexAttributes().Select(a => $"{a.attribute}:{a.format}x{a.dimension}@s{a.stream}")) +
                    $" | sources' shader: {sources[0].sharedMaterial?.shader?.name}");

            if (DumpAtlasVertices && sources[0].sharedMaterial?.shader?.name.Contains("Stationary") == true && !_dumpedAtlasVerts)
            {
                _dumpedAtlasVerts = true;
                DumpVertices(combined, 24);
                var tex = sources[0].sharedMaterial.mainTexture;
                if (tex != null) _log.LogInfo($"Atlas _MainTex '{tex.name}' {tex.width}x{tex.height} filter={tex.filterMode} wrap={tex.wrapMode} type={tex.GetType().Name}");
            }

            // Combined vertices live in the batch root's space, or world space if there is no root.
            var root = StaticBatchRoot?.GetValue(sources[0]) as Transform;
            var proxy = CreateProxy($"{combined.name} [UltraRT]", root, combined, mats);
            Rtas.AddInstance(proxy, flags, enableTriangleCulling: false);
        }

        /// <summary>Set by -ultrart-dumpverts: log a decode of the StaticSceneOptimizer mesh's vertex data.</summary>
        public static bool DumpAtlasVertices;
        private bool _dumpedAtlasVerts;

        /// <summary>Debug: decode the first few vertices of a (possibly non-readable) mesh straight from its GPU vertex buffer.</summary>
        private void DumpVertices(Mesh mesh, int count)
        {
            try
            {
                int stride = mesh.GetVertexBufferStride(0);
                using (var vb = mesh.GetVertexBuffer(0))
                {
                    // Whole-mesh summary: distinct atlas rects and the UV0 range used with each.
                    var all = new uint[mesh.vertexCount * stride / 4];
                    vb.GetData(all, 0, 0, all.Length);
                    var ab = new byte[all.Length * 4];
                    Buffer.BlockCopy(all, 0, ab, 0, ab.Length);
                    int o1 = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord1);
                    int o2 = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord2);
                    int o0 = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                    int oc = mesh.GetVertexAttributeOffset(VertexAttribute.Color);
                    var rects = new Dictionary<string, (int n, Vector2 min, Vector2 max, int blendNonZero)>();
                    int colorNotWhite = 0;
                    for (int v = 0; v < mesh.vertexCount; v++)
                    {
                        int b0 = v * stride;
                        string key = string.Join(",", Enumerable.Range(0, 4).Select(d => Decode(ab, b0 + o1, VertexAttributeFormat.Float16, d).ToString("0.####")));
                        var uv = new Vector2(Decode(ab, b0 + o0, VertexAttributeFormat.Float16, 0), Decode(ab, b0 + o0, VertexAttributeFormat.Float16, 1));
                        bool blend = Enumerable.Range(0, 4).Any(d => Decode(ab, b0 + o2, VertexAttributeFormat.Float16, d) != 0);
                        if (ab[b0 + oc] != 255 || ab[b0 + oc + 1] != 255 || ab[b0 + oc + 2] != 255 || ab[b0 + oc + 3] != 255) colorNotWhite++;
                        rects[key] = rects.TryGetValue(key, out var r)
                            ? (r.n + 1, Vector2.Min(r.min, uv), Vector2.Max(r.max, uv), r.blendNonZero + (blend ? 1 : 0))
                            : (1, uv, uv, blend ? 1 : 0);
                    }
                    _log.LogInfo($"Atlas summary '{mesh.name}': {rects.Count} distinct TexCoord1 rects, {colorNotWhite} verts with non-white color");
                    foreach (var kv in rects.OrderByDescending(k => k.Value.n).Take(25))
                        _log.LogInfo($"  rect({kv.Key}) verts={kv.Value.n} uv0 min={kv.Value.min:F2} max={kv.Value.max:F2} blendRectNonZero={kv.Value.blendNonZero}");

                    count = Math.Min(count, mesh.vertexCount);
                    var words = new uint[count * stride / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var bytes = new byte[words.Length * 4];
                    Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);

                    var attrs = mesh.GetVertexAttributes().Where(a => a.stream == 0).ToArray();
                    _log.LogInfo($"Vertex dump '{mesh.name}' stride={stride} verts={mesh.vertexCount}");
                    for (int v = 0; v < count; v++)
                    {
                        var parts = attrs.Select(a =>
                        {
                            int off = v * stride + mesh.GetVertexAttributeOffset(a.attribute);
                            var vals = Enumerable.Range(0, a.dimension).Select(d => Decode(bytes, off, a.format, d));
                            return $"{a.attribute}=({string.Join(",", vals.Select(x => x.ToString("0.####")))})";
                        });
                        _log.LogInfo($"  v{v}: {string.Join(" ", parts)}");
                    }
                }
            }
            catch (Exception e) { _log.LogWarning($"Vertex dump failed: {e.Message}"); }
        }

        private static float Decode(byte[] b, int off, VertexAttributeFormat f, int i)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, off + i * 4);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, off + i * 2));
                case VertexAttributeFormat.UNorm8: return b[off + i] / 255f;
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, off + i * 2) / 65535f;
                default: return float.NaN;
            }
        }

        private MeshRenderer CreateProxy(string name, Transform parent, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            if (parent != null) go.transform.SetParent(parent, false);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.forceRenderingOff = true; // never rasterised; still eligible for the RTAS
            _proxyObjects.Add(go);
            return mr;
        }

        /// <summary>Per frame: add/remove dynamic proxies whose source toggled, refresh transforms.</summary>
        public void SyncDynamic(bool force = false)
        {
            for (int i = 0; i < _dynamic.Count; i++)
            {
                var (src, proxy, wasVisible) = _dynamic[i];
                bool visible = src != null && proxy != null && src.enabled && src.gameObject.activeInHierarchy;
                if (visible != wasVisible || force)
                {
                    if (proxy != null)
                    {
                        if (visible)
                        {
                            var flags = Enumerable.Repeat(RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly,
                                Math.Max(1, proxy.sharedMaterials.Length)).ToArray();
                            Rtas.AddInstance(proxy, flags, enableTriangleCulling: false);
                        }
                        else if (wasVisible) Rtas.RemoveInstance(proxy);
                    }
                    _dynamic[i] = (src, proxy, visible);
                }
                else if (visible && src.transform.hasChanged)
                {
                    Rtas.UpdateInstanceTransform(proxy);
                }
            }
        }

        private Material Proxy(Material src)
        {
            if (src == null) return _disabledMaterial;
            if (_materialCache.TryGetValue(src, out var m)) return m;

            m = new Material(_proxyShader) { name = src.name + " [UltraRT]" };
            if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") is Texture tex)
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            if (src.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));

            // StaticSceneOptimizer's baked level geometry: _MainTex is a scene atlas, and each vertex carries
            // its atlas rect in TEXCOORD1 (main) / TEXCOORD2 (blend). UV0 tiles within that rect.
            if (src.shader != null && src.shader.name.Contains("Stationary"))
                m.SetFloat("_AtlasMode", 1f);

            // ULTRAKILL/Master emission
            if (src.IsKeywordEnabled("EMISSIVE"))
            {
                if (src.HasProperty("_EmissiveTex") && src.GetTexture("_EmissiveTex") is Texture etex) m.SetTexture("_EmissiveTex", etex);
                if (src.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", src.GetColor("_EmissiveColor"));
                m.SetFloat("_EmissiveIntensity", src.HasProperty("_EmissiveIntensity") ? Mathf.Max(src.GetFloat("_EmissiveIntensity"), 0.0001f) : 1f);
                bool albedoAsEmissive = src.IsKeywordEnabled("_USEALBEDOASEMISSIVE_ON") ||
                                        src.HasProperty("_UseAlbedoAsEmissive") && src.GetFloat("_UseAlbedoAsEmissive") > 0.5f;
                m.SetFloat("_UseAlbedoAsEmissive", albedoAsEmissive ? 1 : 0);
            }
            // Unity Standard emission
            else if (src.IsKeywordEnabled("_EMISSION") && src.HasProperty("_EmissionColor"))
            {
                if (src.GetTexture("_EmissionMap") is Texture etex) m.SetTexture("_EmissiveTex", etex);
                m.SetColor("_EmissiveColor", src.GetColor("_EmissionColor"));
                m.SetFloat("_EmissiveIntensity", 1f);
            }

            _materialCache[src] = m;
            return m;
        }

        public void Dispose()
        {
            Rtas?.Dispose();
            Rtas = null;
            foreach (var go in _proxyObjects) if (go != null) Object.Destroy(go);
            _proxyObjects.Clear();
            _dynamic.Clear();
            foreach (var m in _materialCache.Values) if (m != null) Object.Destroy(m);
            _materialCache.Clear();
            if (_disabledMaterial != null) Object.Destroy(_disabledMaterial);
        }
    }
}
