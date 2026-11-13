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
    ///  - Other MeshRenderers: one proxy parented to the source, so it follows transforms and activation.
    ///  - SkinnedMeshRenderers: a proxy SkinnedMeshRenderer sharing the source's bones.
    /// Each instance's custom id is the source's original layer, used by the shaders for per-light culling masks.
    /// </summary>
    internal sealed class SceneProxies : IDisposable
    {
        private static readonly PropertyInfo StaticBatchRoot =
            typeof(Renderer).GetProperty("staticBatchRootTransform", BindingFlags.Instance | BindingFlags.NonPublic);

        // Layers that are never ray traced: 13 = AlwaysOnTop (viewmodel/HUD, rasterised by the HUD camera), 30 = Portal.
        private const int ExcludedLayers = (1 << 13) | (1 << 30);

        /// <summary>Set by -ultrart-dumpverts: log a decode of the StaticSceneOptimizer mesh's vertex data.</summary>
        public static bool DumpAtlasVertices;

        /// <summary>Set by -ultrart-skinned-visible: let skinned proxies "render" (via the no-op pass) so Unity keeps skinning them.</summary>
        public static bool SkinnedProxiesVisible;

        /// <summary>Experiment (-ultrart-nostatic): leave static batches out of the RTAS.</summary>
        public static bool SkipStaticBatches;

        /// <summary>Winding convention for RT back-face culling (-ultrart-ccw flips it).</summary>
        public static bool FrontCounterClockwise;
        private int _staticRecords, _staticSlots;

        private readonly ManualLogSource _log;
        private readonly Shader _proxyShader;
        private readonly int _cameraMask;
        private readonly Material _disabledMaterial;
        private readonly Dictionary<Material, Material> _materialCache = new Dictionary<Material, Material>();
        private readonly HashSet<string> _loggedLayouts = new HashSet<string>();
        private readonly List<GameObject> _proxyObjects = new List<GameObject>();
        private readonly List<DynamicProxy> _dynamic = new List<DynamicProxy>();
        private bool _dumpedAtlasVerts;

        /// <summary>
        /// Lives on each dynamic proxy (a child of its source), so activation changes of the source hierarchy arrive as
        /// events instead of being polled across thousands of mostly-dormant objects every frame.
        /// </summary>
        private sealed class ProxyTracker : MonoBehaviour
        {
            public SceneProxies Owner;
            public DynamicProxy Entry;
            private void OnEnable() => Owner?.MarkDirty(Entry);
            private void OnDisable() => Owner?.MarkDirty(Entry);
        }

        private readonly HashSet<DynamicProxy> _dirty = new HashSet<DynamicProxy>();
        private readonly List<DynamicProxy> _active = new List<DynamicProxy>();
        private void MarkDirty(DynamicProxy d) { if (d != null) _dirty.Add(d); }

        private sealed class DynamicProxy
        {
            public Renderer Source;
            public Transform SourceTransform;
            public GameObject SourceObject;
            public Renderer Proxy;
            public SkinnedMeshRenderer SkinnedSource, SkinnedProxy;
            public int BlendShapes;
            public RayTracingSubMeshFlags[] Flags;
            public uint Id;
            public bool TwoSided;
            public uint Mask = GeometryMask;
            public bool Visible;
        }

        public RayTracingAccelerationStructure Rtas { get; private set; }
        public int SkinnedCount { get; private set; }

        public SceneProxies(ManualLogSource log, Shader proxyShader, int cameraCullingMask)
        {
            _log = log;
            _proxyShader = proxyShader;
            _cameraMask = cameraCullingMask & ~ExcludedLayers;
            _disabledMaterial = new Material(proxyShader) { name = "UltraRT Disabled" };
        }

        /// <summary>
        /// StaticSceneOptimizer moves baked geometry to "baked" layers (24 -> 6, 8 -> 7). Lights' culling masks refer to
        /// the original layers, so map back.
        /// </summary>
        private static int OriginalLayer(int layer) => layer == 6 ? 24 : layer == 7 ? 8 : layer;

        private bool LayerIncluded(int layer) => ((_cameraMask >> layer) & 1) != 0 || layer == 6 || layer == 7;

        /// <summary>Transparent, particle, additive and sky materials stay rasterised.</summary>
        private static bool Traceable(Material m)
        {
            if (m == null || m.shader == null) return false;
            if (m.renderQueue >= 3000) return false;
            string s = m.shader.name;
            // LIMBO_WALLS: the "TV sky" walls (ScreenSky/ScreenField) draw the panoramic sky by view direction through a
            // scanline mask. For RT they are sky, not surfaces: rays pass through to the sky lookup and the sun isn't blocked.
            // (They ARE traced, as "sky walls": see SkyWallMask.)
            return !(s.Contains("Particles") || s.Contains("Additive") || s.Contains("Skybox") || s.Contains("Sprite") || s.Contains("Stencil") || s.StartsWith("Hidden/ProBuilder"));
        }

        public void Build()
        {
            Rtas = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.RASSettings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, ~0));

            var player = MonoSingleton<NewMovement>.Instance;
            _playerPos = player != null ? player.transform.position : (Vector3?)null;
            var batches = new Dictionary<Mesh, List<MeshRenderer>>();
            var stats = new ScanStats();
            // Include inactive objects: ULTRAKILL keeps most enemies and room contents disabled until triggered.
            ScanRenderers(batches, stats);
            // Debug (-ultrart-dumpverts): the largest renderers (sky boxes/domes show up here).
            if (DumpAtlasVertices) foreach (var r in Object.FindObjectsOfType<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                         .OrderByDescending(r => r.bounds.size.magnitude).Take(8))
            {
                var m = r.sharedMaterial;
                _log.LogInfo($"  big renderer '{GameControlPath(r.transform)}' size={r.bounds.size.magnitude:F0} layer={r.gameObject.layer} active={r.gameObject.activeInHierarchy} " +
                             $"static={r.isPartOfStaticBatch} shader='{m?.shader?.name}' mat='{m?.name}' queue={m?.renderQueue} kw=[{(m != null ? string.Join(" ", m.shaderKeywords) : "")}]");
            }

            if (!SkipStaticBatches) foreach (var kv in batches) AddStaticBatch(kv.Key, kv.Value);

            if (DumpAtlasVertices)
            {
                // Debug: every traced source material, to spot odd ones (sky screens, masks).
                var lines = _materialCache.Keys.Where(m => m != null).Select(m =>
                    $"{m.shader?.name}\t{m.name}\tqueue={m.renderQueue}\ttex={(m.mainTexture ? m.mainTexture.name : "-")}\tkw={string.Join(" ", m.shaderKeywords)}")
                    .OrderBy(s => s).ToArray();
                System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ultrart", $"materials-{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}.tsv"), lines);
            }

            SyncDynamic(force: true);
            Rtas.Build();
            int records = _staticRecords + _active.Sum(d => d.Flags.Count(f => f != RayTracingSubMeshFlags.Disabled));
            _log.LogInfo($"Shader-table hit records (enabled submeshes): {records} (static batches {_staticRecords}, total submesh slots {_staticSlots + _active.Sum(d => d.Flags.Length)})");
            _log.LogInfo($"Proxies: {batches.Count} static batches (from {stats.Batched} renderers), {stats.Dynamic} dynamic, {stats.Skinned} skinned; " +
                         $"skipped layer={stats.SkippedLayer} material={stats.SkippedMaterial} mesh={stats.SkippedMesh} backdrop={stats.SkippedBackdrop}; " +
                         $"RTAS instances={Rtas.GetInstanceCount()} size={Rtas.GetSize() / (1024.0 * 1024.0):F1} MB, proxy materials={_materialCache.Count}");
        }

        /// <summary>Pick up renderers created or loaded since the last scan (Cyber Grind spawns, instantiated props).</summary>
        public int Rescan()
        {
            var stats = new ScanStats();
            ScanRenderers(null, stats);
            int added = stats.Dynamic + stats.Skinned;
            if (added > 0) _log.LogInfo($"Rescan: +{stats.Dynamic} dynamic, +{stats.Skinned} skinned");
            return added;
        }

        /// <summary>Targeted registration for a newly enabled/spawned hierarchy (e.g. an enemy). Cheap; dedupes.</summary>
        public void AddHierarchy(GameObject root)
        {
            if (root == null) return;
            var stats = new ScanStats();
            ScanRenderers(null, stats, root.GetComponentsInChildren<MeshRenderer>(true), root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }

        private sealed class ScanStats { public int Batched, Dynamic, Skinned, SkippedLayer, SkippedMaterial, SkippedMesh, SkippedBackdrop; }

        private readonly HashSet<Renderer> _known = new HashSet<Renderer>();

        private void ScanRenderers(Dictionary<Mesh, List<MeshRenderer>> batches, ScanStats stats,
            MeshRenderer[] meshRenderers = null, SkinnedMeshRenderer[] skinnedRenderers = null)
        {
            foreach (var r in meshRenderers ?? Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!_known.Add(r)) continue;
                if (IsProxyObject(r.gameObject)) continue; // our own proxies
                if (!LayerIncluded(r.gameObject.layer)) { stats.SkippedLayer++; continue; }
                if (IsBackdrop(r)) { stats.SkippedBackdrop++; continue; }
                var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) { stats.SkippedMesh++; continue; }
                if (!r.sharedMaterials.Any(Traceable)) { stats.SkippedMaterial++; continue; }

                if (r.isPartOfStaticBatch)
                {
                    if (batches == null) continue; // static batches only exist at load
                    if (!batches.TryGetValue(mesh, out var list)) batches[mesh] = list = new List<MeshRenderer>();
                    list.Add(r);
                    stats.Batched++;
                }
                else
                {
                    var mats = r.sharedMaterials;
                    var proxy = CreateMeshProxy($"{r.name} [UltraRT]", r.transform, mesh, mats.Select(ProxyOrDisabled).ToArray());
                    AddDynamic(r, proxy, SubmeshFlags(mats, mesh.subMeshCount));
                    stats.Dynamic++;
                }
            }

            foreach (var r in skinnedRenderers ?? Object.FindObjectsOfType<SkinnedMeshRenderer>(true))
            {
                if (!_known.Add(r)) continue;
                if (IsProxyObject(r.gameObject)) continue;
                if (r.sharedMesh == null) { stats.SkippedMesh++; continue; }
                if (!LayerIncluded(r.gameObject.layer)) { stats.SkippedLayer++; continue; }
                if (IsBackdrop(r)) { stats.SkippedBackdrop++; continue; }
                if (!r.sharedMaterials.Any(Traceable)) { stats.SkippedMaterial++; continue; }
                var proxy = CreateSkinnedProxy(r);
                AddDynamic(r, proxy, SubmeshFlags(r.sharedMaterials, r.sharedMesh.subMeshCount));
                stats.Skinned++;
            }
            SkinnedCount += stats.Skinned;
        }

        private static RayTracingSubMeshFlags[] SubmeshFlags(Material[] mats, int subMeshCount)
        {
            var flags = new RayTracingSubMeshFlags[Math.Max(1, subMeshCount)];
            for (int i = 0; i < flags.Length; i++)
            {
                var m = i < mats.Length ? mats[i] : null;
                flags[i] = Traceable(m) ? RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly : RayTracingSubMeshFlags.Disabled;
            }
            return flags;
        }

        private void AddDynamic(Renderer source, Renderer proxy, RayTracingSubMeshFlags[] flags)
        {
            var ss = source as SkinnedMeshRenderer;
            var entry = new DynamicProxy
            {
                Source = source,
                SourceTransform = source.transform,
                SourceObject = source.gameObject,
                Proxy = proxy,
                SkinnedSource = ss,
                SkinnedProxy = proxy as SkinnedMeshRenderer,
                BlendShapes = ss != null && ss.sharedMesh != null ? ss.sharedMesh.blendShapeCount : 0,
                Flags = flags,
                Id = (uint)OriginalLayer(source.gameObject.layer),
                TwoSided = source.sharedMaterials.Any(m => Traceable(m) && SourceTwoSided(m)),
                Mask = source.sharedMaterials.Where(Traceable).All(SourceSkyWall) ? SkyWallMask : GeometryMask,
            };
            _dynamic.Add(entry);
            var tracker = proxy.gameObject.AddComponent<ProxyTracker>();
            tracker.Owner = this;
            tracker.Entry = entry;
            _dirty.Add(entry); // evaluate once, whatever the initial state
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
                    if (sub < 0 || sub >= mats.Length || !Traceable(src[i])) continue;
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

            // Unity rebuilds the shader table every dispatch, one record per enabled submesh. Combined meshes have
            // hundreds of submeshes sharing a handful of materials, so merge them by material (~0.5 ms/frame saved).
            var mesh = combined;
            if (!NoSubmeshMerge && TryMergeByMaterial(combined, mats, flags, out var merged, out var mergedMats))
            {
                mesh = merged;
                mats = mergedMats;
                flags = Enumerable.Repeat(RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly, mats.Length).ToArray();
                _ownedMeshes.Add(merged);
            }

            // Combined vertices live in the batch root's space, or world space if there is no root.
            // All sources of one combined mesh share a layer in practice (StaticSceneOptimizer: one per material).
            // Back-face culling is per instance, so two-sided submeshes go in a second instance of the same mesh.
            var root = StaticBatchRoot?.GetValue(sources[0]) as Transform;
            uint id = (uint)OriginalLayer(sources[0].gameObject.layer);
            // Sky walls also need their own instance, for the instance mask.
            foreach (bool twoSided in new[] { false, true })
            foreach (bool skyWall in new[] { false, true })
            {
                var part = flags.Select((f, i) => f != RayTracingSubMeshFlags.Disabled && IsTwoSided(mats[i]) == twoSided && IsSkyWall(mats[i]) == skyWall
                    ? f : RayTracingSubMeshFlags.Disabled).ToArray();
                if (part.All(f => f == RayTracingSubMeshFlags.Disabled)) continue;
                var proxy = CreateMeshProxy($"{combined.name}{(twoSided ? " two-sided" : "")}{(skyWall ? " sky-wall" : "")} [UltraRT]", root, mesh, mats);
                Rtas.AddInstance(proxy, part, enableTriangleCulling: !twoSided, frontTriangleCounterClockwise: FrontCounterClockwise,
                    mask: skyWall ? SkyWallMask : GeometryMask, id: id);
                _staticRecords += part.Count(f => f != RayTracingSubMeshFlags.Disabled);
                _staticSlots += part.Length;
            }
        }

        /// <summary>
        /// Enclosures the raster game never shows: e.g. 1-1's 2.8 km inward-facing OutOfBounds cube, hidden in game behind
        /// the sky sphere (drawn later, transparent queue). As RT geometry they block the sun for the whole level.
        /// Rule: named OutOfBounds, or bounds over 1 km that contain the player. Every exclusion is logged.
        /// </summary>
        private bool IsBackdrop(Renderer r)
        {
            var b = r.bounds;
            bool named = r.name.IndexOf("OutOfBounds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (r.transform.parent != null && r.transform.parent.name.IndexOf("OutOfBounds", StringComparison.OrdinalIgnoreCase) >= 0);
            bool enclosure = b.size.magnitude > 1000f && _playerPos.HasValue && b.Contains(_playerPos.Value);
            if (!named && !enclosure) return false;
            _log.LogInfo($"Excluded backdrop '{GameControlPath(r.transform)}' size={b.size.magnitude:F0} ({(named ? "named" : "encloses player")})");
            return true;
        }

        private Vector3? _playerPos;
        private static string GameControlPath(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        /// <summary>Experiment (-ultrart-nomerge): keep static batches' original submesh layout.</summary>
        public static bool NoSubmeshMerge;
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();

        /// <summary>
        /// Copies a (non-readable) combined mesh via GPU readback into a new GPU-only mesh with one submesh per
        /// material, dropping disabled submeshes. Vertex data is copied verbatim, so the hit shader sees the same layout.
        /// </summary>
        private bool TryMergeByMaterial(Mesh src, Material[] mats, RayTracingSubMeshFlags[] flags, out Mesh merged, out Material[] mergedMats)
        {
            merged = null;
            mergedMats = null;
            try
            {
                var groups = new Dictionary<Material, List<int>>();
                var order = new List<Material>();
                for (int i = 0; i < src.subMeshCount; i++)
                {
                    if (flags[i] == RayTracingSubMeshFlags.Disabled || src.GetTopology(i) != MeshTopology.Triangles) continue;
                    if (!groups.TryGetValue(mats[i], out var list)) { groups[mats[i]] = list = new List<int>(); order.Add(mats[i]); }
                    list.Add(i);
                }
                if (order.Count == 0 || order.Count == src.subMeshCount) return false;

                // Indices (absolute: baseVertex folded in).
                bool wide = src.indexFormat == IndexFormat.UInt32;
                int totalIndices = 0;
                for (int i = 0; i < src.subMeshCount; i++) totalIndices += (int)src.GetIndexCount(i);
                var srcIdx = new uint[0];
                using (var ib = src.GetIndexBuffer())
                {
                    if (wide) { srcIdx = new uint[ib.count]; ib.GetData(srcIdx); }
                    else
                    {
                        var packed = new uint[(ib.count + 1) / 2];
                        ib.GetData(packed, 0, 0, packed.Length); // 16-bit buffer read as 32-bit words
                        srcIdx = new uint[ib.count];
                        for (int k = 0; k < ib.count; k++) srcIdx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                    }
                }

                var newIdx = new List<int>(totalIndices);
                var descs = new List<SubMeshDescriptor>(order.Count);
                foreach (var m in order)
                {
                    int start = newIdx.Count;
                    foreach (int sub in groups[m])
                    {
                        var d = src.GetSubMesh(sub);
                        for (int k = 0; k < d.indexCount; k++) newIdx.Add((int)srcIdx[d.indexStart + k] + d.baseVertex);
                    }
                    descs.Add(new SubMeshDescriptor(start, newIdx.Count - start, MeshTopology.Triangles));
                }

                var dst = new Mesh { name = src.name + " [UltraRT merged]", indexFormat = IndexFormat.UInt32 };
                dst.SetVertexBufferParams(src.vertexCount, src.GetVertexAttributes());
                for (int s = 0; s < src.vertexBufferCount; s++)
                {
                    int stride = src.GetVertexBufferStride(s);
                    using (var vb = src.GetVertexBuffer(s))
                    {
                        var words = new uint[src.vertexCount * stride / 4];
                        vb.GetData(words, 0, 0, words.Length);
                        var bytes = new byte[words.Length * 4];
                        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                        dst.SetVertexBufferData(bytes, 0, 0, bytes.Length, s, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    }
                }
                const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                dst.SetIndexBufferParams(newIdx.Count, IndexFormat.UInt32);
                dst.SetIndexBufferData(newIdx, 0, 0, newIdx.Count, quiet);
                dst.subMeshCount = descs.Count;
                for (int i = 0; i < descs.Count; i++) dst.SetSubMesh(i, descs[i], quiet);
                dst.bounds = src.bounds;
                dst.UploadMeshData(true); // GPU only, like the source

                merged = dst;
                mergedMats = order.ToArray();
                _log.LogInfo($"Merged '{src.name}': {src.subMeshCount} submeshes -> {descs.Count} ({newIdx.Count / 3:N0} tris)");
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Submesh merge failed for '{src.name}', using it as-is: {e.Message}");
                if (merged != null) Object.Destroy(merged);
                merged = null;
                return false;
            }
        }

        public static bool IsProxyObject(GameObject go) => (go.hideFlags & HideFlags.HideInHierarchy) != 0 && go.name.EndsWith("[UltraRT]", StringComparison.Ordinal);

        private MeshRenderer CreateMeshProxy(string name, Transform parent, Mesh mesh, Material[] materials)
        {
            var go = NewProxyObject(name, parent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.forceRenderingOff = true; // never rasterised; still eligible for the RTAS
            return mr;
        }

        private SkinnedMeshRenderer CreateSkinnedProxy(SkinnedMeshRenderer src)
        {
            var go = NewProxyObject($"{src.name} [UltraRT]", src.transform);
            var p = go.AddComponent<SkinnedMeshRenderer>();
            p.sharedMesh = src.sharedMesh;
            p.bones = src.bones;
            p.rootBone = src.rootBone;
            p.localBounds = src.localBounds;
            p.updateWhenOffscreen = true;
            p.sharedMaterials = src.sharedMaterials.Select(ProxyOrDisabled).ToArray();
            p.shadowCastingMode = ShadowCastingMode.Off;
            p.receiveShadows = false;
            p.rayTracingMode = RayTracingMode.DynamicGeometry;
            // If Unity stops skinning never-rendered renderers, the NoDraw pass keeps them "visible" at the cost of an empty draw.
            p.forceRenderingOff = !SkinnedProxiesVisible;
            return p;
        }

        private GameObject NewProxyObject(string name, Transform parent)
        {
            // HideInHierarchy only: DontSave (part of HideAndDontSave) marks objects "not destroyed on scene load", which
            // contradicts being parented under scene objects and hung the game when unloading 8-3.
            var go = new GameObject(name) { hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable };
            if (parent != null) go.transform.SetParent(parent, false);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            _proxyObjects.Add(go);
            return go;
        }

        /// <summary>Per frame: add/remove dynamic proxies whose source toggled, refresh transforms, sync blend shapes.</summary>
        public void SyncDynamic(bool force = false)
        {
            // 1) Hierarchy (de)activations arrive as events via ProxyTracker.
            if (force) foreach (var d in _dynamic) _dirty.Add(d);
            if (_dirty.Count > 0)
            {
                foreach (var d in _dirty) Evaluate(d);
                _dirty.Clear();
            }

            // 2) Per frame, only for instances currently in the RTAS: renderer.enabled toggles, transforms, blend shapes.
            for (int k = _active.Count - 1; k >= 0; k--)
            {
                var d = _active[k];
                if (d.Source == null || d.Proxy == null || !d.Source.enabled) { Evaluate(d); continue; }
                if (d.SkinnedProxy != null)
                {
                    // DynamicGeometry instances are re-read on Build(); only blend shapes need copying.
                    for (int i = 0; i < d.BlendShapes; i++) d.SkinnedProxy.SetBlendShapeWeight(i, d.SkinnedSource.GetBlendShapeWeight(i));
                }
                else if (d.SourceTransform.hasChanged)
                {
                    Rtas.UpdateInstanceTransform(d.Proxy);
                    d.SourceTransform.hasChanged = false; // Unity never clears this itself
                }
            }

            // 3) Renderers that were disabled (but whose GameObject stayed active) can come back without an event.
            //    Check a slice of the dormant set each frame.
            int n = _dynamic.Count;
            if (n > 0)
            {
                for (int s = 0; s < 64; s++)
                {
                    _pollCursor = (_pollCursor + 1) % n;
                    var d = _dynamic[_pollCursor];
                    if (!d.Visible && d.Source != null && d.Source.enabled && d.SourceObject.activeInHierarchy) Evaluate(d);
                }
            }
        }

        private int _pollCursor;

        private void Evaluate(DynamicProxy d)
        {
            bool visible = d.Source != null && d.Proxy != null && d.SourceObject.activeInHierarchy && d.Source.enabled;
            if (visible == d.Visible) return;
            if (visible)
            {
                Rtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: FrontCounterClockwise, mask: d.Mask, id: d.Id);
                d.SourceTransform.hasChanged = false;
                _active.Add(d);
            }
            else
            {
                if (d.Proxy != null) Rtas.RemoveInstance(d.Proxy);
                _active.Remove(d);
            }
            d.Visible = visible;
        }

        private Material ProxyOrDisabled(Material src) => Traceable(src) ? Proxy(src) : _disabledMaterial;

        private readonly HashSet<Material> _twoSidedProxies = new HashSet<Material>();
        private readonly HashSet<Material> _skyWallProxies = new HashSet<Material>();
        private bool IsSkyWall(Material proxy) => proxy != null && _skyWallProxies.Contains(proxy);

        /// <summary>
        /// Instance masks: shadow rays only test GeometryMask. Sky walls (LIMBO_WALLS, the "TV sky") stop camera and bounce
        /// rays and report sky, so the level behind them stays hidden as in game, but never block light.
        /// </summary>
        public const uint GeometryMask = 0x01, SkyWallMask = 0x02;
        private static bool SourceSkyWall(Material m) => m != null && m.IsKeywordEnabled("LIMBO_WALLS");
        private bool IsTwoSided(Material proxy) => proxy != null && _twoSidedProxies.Contains(proxy);

        /// <summary>
        /// Matches the raster pipeline's face culling: rays cull back faces unless the material renders both sides.
        /// (Without this, e.g. 1-1's huge inward-facing OutOfBounds cube, invisible in game, blocked the sun.)
        /// ULTRAKILL/Master uses _CullMode, most Unity shaders _Cull; CullMode.Off == 0. Default is back-face culling.
        /// </summary>
        private static bool SourceTwoSided(Material m)
        {
            if (m == null) return false;
            if (m.HasProperty("_CullMode")) return Mathf.RoundToInt(m.GetFloat("_CullMode")) == (int)CullMode.Off;
            if (m.HasProperty("_Cull")) return Mathf.RoundToInt(m.GetFloat("_Cull")) == (int)CullMode.Off;
            return false;
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

            // StaticSceneOptimizer's baked level geometry: _MainTex/_BlendTex are scene atlases, and each vertex carries
            // its atlas rects in TEXCOORD1 (main) / TEXCOORD2 (blend). UV0 tiles within those rects.
            if (src.shader != null && src.shader.name.Contains("Stationary"))
            {
                m.SetFloat("_AtlasMode", 1f);
                if (src.HasProperty("_BlendTex") && src.GetTexture("_BlendTex") is Texture blend) m.SetTexture("_BlendTex", blend);
            }

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
            if (SourceTwoSided(src)) _twoSidedProxies.Add(m);
            if (SourceSkyWall(src)) { _skyWallProxies.Add(m); m.SetFloat("_SkyWall", 1f); }
            return m;
        }

        // ---------------------------------------------------------------- debug: vertex dump

        /// <summary>Debug: decode a (possibly non-readable) mesh straight from its GPU vertex buffer.</summary>
        private void DumpVertices(Mesh mesh, int count)
        {
            try
            {
                int stride = mesh.GetVertexBufferStride(0);
                using (var vb = mesh.GetVertexBuffer(0))
                {
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

                    var attrs = mesh.GetVertexAttributes().Where(a => a.stream == 0).ToArray();
                    for (int v = 0; v < Math.Min(count, mesh.vertexCount); v++)
                    {
                        var parts = attrs.Select(a =>
                        {
                            int off = v * stride + mesh.GetVertexAttributeOffset(a.attribute);
                            var vals = Enumerable.Range(0, a.dimension).Select(d => Decode(ab, off, a.format, d));
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

        public void Dispose()
        {
            Rtas?.Dispose();
            Rtas = null;
            foreach (var go in _proxyObjects) if (go != null) Object.Destroy(go);
            _proxyObjects.Clear();
            _dynamic.Clear();
            _active.Clear();
            _dirty.Clear();
            foreach (var mesh in _ownedMeshes) if (mesh != null) Object.Destroy(mesh);
            _ownedMeshes.Clear();
            foreach (var m in _materialCache.Values) if (m != null) Object.Destroy(m);
            _materialCache.Clear();
            if (_disabledMaterial != null) Object.Destroy(_disabledMaterial);
        }
    }
}
