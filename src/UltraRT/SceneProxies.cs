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
    ///  - Static-batched renderers share one combined mesh: proxies per (combined mesh, activation group), merged by
    ///    material, added/removed as the game toggles rooms (StaticGroups.cs, docs/scene-proxies.md).
    ///  - Other MeshRenderers: one proxy parented to the source, so it follows transforms and activation.
    ///  - SkinnedMeshRenderers: a proxy SkinnedMeshRenderer sharing the source's bones.
    /// Each instance's custom id is the source's original layer, used by the shaders for per-light culling masks.
    /// </summary>
    internal sealed partial class SceneProxies : IDisposable
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
        public int AlphaTestedMaterials { get; private set; }

        /// <summary>Winding convention for RT back-face culling (-ultrart-ccw flips it).</summary>
        public static bool FrontCounterClockwise;

        /// <summary>Folders searched for ultrart-materials.json (plugin folder, then BepInEx/config). Set by the plugin.</summary>
        public static string[] MaterialOverrideFolders = new string[0];

        /// <summary>-ultrart-matdump: write the material classification to BepInEx/ultrart/material-classes-SCENE.tsv, and give
        /// atlas geometry a variant for every class (not only reflective ones) so the "materials" view shows all of them.</summary>
        public static bool DumpMaterialClasses;

        private readonly MaterialClassifier _classifier;
        private readonly HashSet<Material> _atlasProxies = new HashSet<Material>();
        private readonly Dictionary<(Material, MaterialParams, MaterialParams?, bool), Material> _atlasVariants = new Dictionary<(Material, MaterialParams, MaterialParams?, bool), Material>();
        private int _atlasSlotsHook, _atlasSlotsFootstep, _atlasSlotsUnknown;

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

            // Motion vectors (MotionVectors.cs). CurM / PrevM: the matrix that drives the instance this frame and last
            // frame (rigid: the proxy's localToWorld; skinned: the root bone's position and rotation).
            public int Slot;
            public Matrix4x4 CurM, PrevM;
            public bool Moved;
            public Transform Root;
            public int SkinBase = -1, SkinVerts, SkinGen;
            public bool SkinPrevValid;
        }

        /// <summary>Diagnostics: visible dynamic proxies whose bounds contain p (something the camera may be inside of).</summary>
        public IEnumerable<string> DescribeVisibleAround(Vector3 p)
        {
            foreach (var d in _dynamic)
            {
                if (!d.Visible || d.Proxy == null || !d.Proxy.bounds.Contains(p)) continue;
                var m = d.Source != null ? d.Source.sharedMaterial : null;
                yield return $"{(d.SourceTransform != null ? Bench.GameControl.PathOf(d.SourceTransform) : "?")} size {d.Proxy.bounds.size} " +
                             $"layer {(d.SourceObject != null ? d.SourceObject.layer : -1)} mask {d.Mask} src enabled {(d.Source != null && d.Source.enabled)} " +
                             $"mat {(m != null ? m.name + " / " + m.shader.name : "-")}";
            }
        }

        public RayTracingAccelerationStructure Rtas { get; private set; }
        public int ActiveDynamicCount => _active.Count;
        public int SkinnedCount { get; private set; }
        public MotionVectors Motion { get; }

        public SceneProxies(ManualLogSource log, Shader proxyShader, int cameraCullingMask, ComputeShader motionCopy = null)
        {
            _log = log;
            Motion = new MotionVectors(motionCopy);
            _proxyShader = proxyShader;
            _cameraMask = cameraCullingMask & ~ExcludedLayers;
            _disabledMaterial = new Material(proxyShader) { name = "UltraRT Disabled" };
            _classifier = new MaterialClassifier(log, MaterialOverrideFolders);
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

            OriginalMaterials.Prune();
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

            if (!SkipStaticBatches && batches.Count > 0) BuildStaticGroups(batches);

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
            int dynamicRecords = _active.Sum(d => d.Flags.Count(f => f != RayTracingSubMeshFlags.Disabled));
            _log.LogInfo($"Shader-table hit records (enabled submeshes): {StaticRecordsActive + dynamicRecords} (static {StaticRecordsActive} of {_staticRecordsTotal} " +
                         $"in {StaticInstancesActive}/{_staticPartsTotal} group instances, dynamic {dynamicRecords} in {_active.Count} instances)");
            _log.LogInfo($"Proxies: {batches.Count} static batches (from {stats.Batched} renderers), {stats.Dynamic} dynamic, {stats.Skinned} skinned; " +
                         $"skipped layer={stats.SkippedLayer} material={stats.SkippedMaterial} mesh={stats.SkippedMesh} backdrop={stats.SkippedBackdrop}; " +
                         $"RTAS instances={Rtas.GetInstanceCount()} size={Rtas.GetSize() / (1024.0 * 1024.0):F1} MB, proxy materials={_materialCache.Count} (alpha-tested {AlphaTestedMaterials}), light fixtures {FixtureCount}");
            LogMaterialClasses();
        }

        private void LogMaterialClasses()
        {
            _log.LogInfo($"Material classes: {_classifier.Summary()}");
            _log.LogInfo($"Baked (atlas) slots: {_atlasSlotsHook + _atlasSlotsFootstep + _atlasSlotsUnknown} (originals from hook {_atlasSlotsHook}, " +
                         $"footstep clones {_atlasSlotsFootstep}, unknown {_atlasSlotsUnknown}; captured renderers {OriginalMaterials.CapturedCount}), " +
                         $"{_atlasVariants.Count} atlas material variants{(DumpMaterialClasses ? " (all classes: -ultrart-matdump)" : "")}");
            if (!DumpMaterialClasses) return;
            try
            {
                var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "ultrart",
                    $"material-classes-{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}.tsv");
                _classifier.Dump(path);
                _log.LogInfo($"Material classification written to {path}");
            }
            catch (Exception e) { _log.LogWarning($"Material dump failed: {e.Message}"); }
        }

        /// <summary>
        /// Baked geometry shares one atlas material, so its reflection parameters come from the source renderer's original
        /// (dominant) material, as a variant of the atlas proxy per distinct parameter set. Classes without reflections
        /// keep the plain atlas proxy (no extra shader-table records) unless -ultrart-matdump asks for all of them.
        /// </summary>
        private Material AtlasVariant(Material atlasProxy, MeshRenderer source)
        {
            Material original = null;
            if (OriginalMaterials.TryGet(source, out var mats, out var counts, out var how))
                original = OriginalMaterials.Dominant(mats, counts, m => Traceable(m) && !(m.shader != null && m.shader.name.Contains("Stationary")));
            if (original == null)
            {
                _atlasSlotsUnknown++;
                _classifier.Count(MaterialParams.None(SurfaceClass.Other, "atlas"));
                return atlasProxy;
            }
            if (how == "hook") _atlasSlotsHook++;
            else _atlasSlotsFootstep++;

            var p = _classifier.Classify(original, out var secondary);
            _classifier.Count(p);
            // Cutout originals (e.g. baked grass cards) keep their alpha test: the atlas material itself isn't alpha-tested.
            bool alphaTest = SourceAlphaTested(original) || SourceAlphaTested(OriginalMaterials.Dominant(mats, counts, SourceAlphaTested));
            if (!DumpMaterialClasses && !alphaTest && !p.HasReflection && !(secondary?.HasReflection ?? false)) return atlasProxy;

            var key = (atlasProxy, p, secondary, alphaTest);
            if (_atlasVariants.TryGetValue(key, out var v)) return v;
            v = new Material(atlasProxy) { name = $"{atlasProxy.name} / {p}{(alphaTest ? " / cutout" : "")}" };
            MaterialParams.Apply(v, p, secondary);
            if (alphaTest)
            {
                var cutSrc = SourceAlphaTested(original) ? original : OriginalMaterials.Dominant(mats, counts, SourceAlphaTested);
                v.SetFloat("_AlphaTest", 1f);
                v.SetFloat("_Cutoff", cutSrc != null && cutSrc.HasProperty("_Cutoff") ? cutSrc.GetFloat("_Cutoff") : 0.5f);
                AlphaTestedMaterials++;
            }
            if (IsTwoSided(atlasProxy)) _twoSidedProxies.Add(v);
            if (IsSkyWall(atlasProxy)) _skyWallProxies.Add(v);
            _atlasVariants[key] = v;
            return v;
        }

        private bool IsAtlasProxy(Material proxy) => proxy != null && _atlasProxies.Contains(proxy);

        /// <summary>Pick up renderers created or loaded since the last scan (Cyber Grind spawns, instantiated props).</summary>
        public int Rescan()
        {
            var stats = new ScanStats();
            ScanRenderers(null, stats);
            int added = stats.Dynamic + stats.Skinned + stats.LateStatic;
            if (added > 0) _log.LogInfo($"Rescan: +{stats.Dynamic} dynamic, +{stats.Skinned} skinned, +{stats.LateStatic} static-batched (late sources)");
            return added;
        }

        /// <summary>Targeted registration for a newly enabled/spawned hierarchy (e.g. an enemy). Cheap; dedupes.</summary>
        public void AddHierarchy(GameObject root)
        {
            if (root == null) return;
            var stats = new ScanStats();
            ScanRenderers(null, stats, root.GetComponentsInChildren<MeshRenderer>(true), root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }

        private sealed class ScanStats { public int Batched, Dynamic, Skinned, LateStatic, SkippedLayer, SkippedMaterial, SkippedMesh, SkippedBackdrop; }

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
                    // Merged batches can't carry the fixture mask, but the light can still be lifted out (Plugin.cs).
                    IsLightFixture(r);
                    if (batches == null)
                    {
                        // Combined meshes only exist at load, but their renderers get cloned (CheckPoint rooms).
                        if (AddLateStaticSource(r, mesh)) stats.LateStatic++;
                        continue;
                    }
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
                flags[i] = Traceable(m) ? SubmeshOn(m) : RayTracingSubMeshFlags.Disabled;
            }
            return flags;
        }

        private void AddDynamic(Renderer source, Renderer proxy, RayTracingSubMeshFlags[] flags)
        {
            var ss = source as SkinnedMeshRenderer;
            int slot = Motion.AllocSlot();
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
                Id = (uint)OriginalLayer(source.gameObject.layer) | ((uint)slot << 8),
                Slot = slot,
                Root = ss != null ? (ss.rootBone != null ? ss.rootBone : ss.transform) : null,
                SkinVerts = ss != null && ss.sharedMesh != null ? ss.sharedMesh.vertexCount : 0,
                TwoSided = source.sharedMaterials.Any(m => Traceable(m) && SourceTwoSided(m)),
                Mask = source.sharedMaterials.Where(Traceable).All(SourceSkyWall) ? SkyWallMask : IsLightFixture(source) ? FixtureMask : GeometryMask,
            };
            _dynamic.Add(entry);
            var tracker = proxy.gameObject.AddComponent<ProxyTracker>();
            tracker.Owner = this;
            tracker.Entry = entry;
            _dirty.Add(entry); // evaluate once, whatever the initial state
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

        /// <summary>Experiment (-ultrart-nomerge): keep static batches' original submesh layout (records per submesh).</summary>
        public static bool NoSubmeshMerge;

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
            // Readable as a ByteAddressBuffer, so MotionCopy.compute can keep the previous pose (motion vectors).
            p.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
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

        /// <summary>
        /// Per frame: add/remove dynamic proxies whose source toggled, refresh transforms, sync blend shapes; then add/remove
        /// static-batch group instances whose rooms toggled (StaticGroups.cs).
        /// </summary>
        public void SyncDynamic(bool force = false)
        {
            long ts = Perf.Now;
            SyncStatic(force);
            Perf.Add(Perf.Sec.Static, ts);
            long td = Perf.Now;

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
                    // Skinned: root motion as a matrix, the pose itself from last frame's copy (RecordMotionCopies).
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var m = MotionMatrix(d);
                    d.PrevM = d.CurM;
                    d.CurM = m;
                    bool pose = d.SkinPrevValid && d.SkinGen == Motion.Generation;
                    Motion.SetMotion(d.Slot, d.PrevM * m.inverse, pose ? d.SkinBase : -1);
                    Motion.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                }
                else if (d.SourceTransform.hasChanged)
                {
                    Rtas.UpdateInstanceTransform(d.Proxy);
                    d.SourceTransform.hasChanged = false; // Unity never clears this itself
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var m = MotionMatrix(d);
                    d.PrevM = d.CurM;
                    d.CurM = m;
                    Motion.SetMotion(d.Slot, d.PrevM * m.inverse, -1);
                    d.Moved = true;
                    Motion.RigidUpdates++;
                    Motion.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                }
                else if (d.Moved)
                {
                    // Stopped: no motion since last frame.
                    d.PrevM = d.CurM;
                    Motion.SetIdentity(d.Slot);
                    d.Moved = false;
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

            Perf.Add(Perf.Sec.Dynamic, td);
            long tm = Perf.Now;
            Motion.Upload(); // motion slots changed above, before this frame's trace
            Perf.Add(Perf.Sec.Motion, tm);
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
                // Newly in the RTAS: no motion history (it appears where it is).
                d.CurM = d.PrevM = MotionMatrix(d);
                d.Moved = false;
                d.SkinPrevValid = false;
                if (d.SkinnedProxy != null && d.SkinBase < 0) d.SkinBase = Motion.AllocSkin(d.SkinVerts);
                Motion.SetIdentity(d.Slot);
            }
            else
            {
                if (d.Proxy != null) Rtas.RemoveInstance(d.Proxy);
                _active.Remove(d);
                Motion.FreeSkin(d.SkinBase, d.SkinVerts);
                d.SkinBase = -1;
                d.SkinPrevValid = false;
            }
            d.Visible = visible;
        }

        /// <summary>
        /// The transform that moves an instance, as a matrix. Rigid proxies: exactly the instance transform. Skinned:
        /// the root bone's position and rotation; Unity's instance matrix for a skinned mesh differs from it at most by
        /// a constant (scale), which cancels in M_prev * M_cur^-1.
        /// </summary>
        private static Matrix4x4 MotionMatrix(DynamicProxy d)
        {
            if (d.SkinnedProxy != null)
                return d.Root != null ? Matrix4x4.TRS(d.Root.position, d.Root.rotation, Vector3.one) : Matrix4x4.identity;
            return d.Proxy != null ? d.Proxy.transform.localToWorldMatrix : Matrix4x4.identity;
        }

        /// <summary>
        /// After the frame's DispatchRays: copy each active skinned proxy's pose (this frame's skin) into its region of the
        /// previous-pose buffer, for next frame's hit shader.
        /// </summary>
        public void RecordMotionCopies(CommandBuffer cmd)
        {
            if (!Motion.CanCopySkin) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { RecordMotionCopiesCore(cmd); }
            finally { Perf.Add(Perf.Sec.Motion, t0); }
        }

        private void RecordMotionCopiesCore(CommandBuffer cmd)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var d in _active)
            {
                if (d.SkinnedProxy == null || d.SkinBase < 0) continue;
                bool ok = Motion.RecordCopy(cmd, d.SkinnedProxy, d.SkinBase, d.SkinVerts, out var vb);
                d.SkinPrevValid = ok;
                d.SkinGen = Motion.Generation;
                if (ok && MotionProbe && _probeCount < 6 && ++_probeFrames % 97 == 0) { _probeCount++; ProbeSkinSpace(cmd, d, vb); }
            }
            Motion.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>-ultrart-motion-probe: log which space Unity's skinned vertex buffer is in (checks MotionMatrix's assumption).</summary>
        public static bool MotionProbe;
        private int _probeFrames, _probeCount;

        private void ProbeSkinSpace(CommandBuffer cmd, DynamicProxy d, GraphicsBuffer vb)
        {
            var smr = d.SkinnedSource;
            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var v0 = baked.vertexCount > 0 ? baked.vertices[0] : Vector3.zero;
            Object.Destroy(baked);
            var world = smr.transform.TransformPoint(v0);
            var root = d.Root;
            string name = smr.name;
            var rootNoScale = Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse.MultiplyPoint3x4(world);
            var rootLocal = root.InverseTransformPoint(world);
            var smrLocal = smr.transform.InverseTransformPoint(world);
            var log = _log;
            int stride = vb.stride;
            int slot = d.Slot;
            var w = d.PrevM * d.CurM.inverse;
            Motion.RequestPoseReadback(cmd, d.SkinBase, v => log.LogInfo($"Motion probe '{name}': copied pose vertex 0 = {v:F4} (base {d.SkinBase})"));
            cmd.RequestAsyncReadback(vb, 12, 0, r =>
            {
                if (r.hasError) { log.LogWarning("Motion probe: readback failed"); return; }
                var raw = r.GetData<Vector3>()[0];
                log.LogInfo($"Motion probe '{name}': raw={raw:F4} slot={slot} W.t={w.GetColumn(3):F4} world={world:F4} rootNoScale={rootNoScale:F4} rootLocal={rootLocal:F4} " +
                            $"smrLocal={smrLocal:F4} rootScale={root.lossyScale:F3} smrScale={smr.transform.lossyScale:F3} root='{root.name}' stride={stride}");
            });
        }

        private Material ProxyOrDisabled(Material src) => Traceable(src) ? Proxy(src) : _disabledMaterial;

        /// <summary>
        /// Cutout (alpha-tested) materials, e.g. grass cards: their hits run the any-hit alpha test, so their submeshes must
        /// not be flagged opaque (ClosestHitOnly). Works on source or proxy materials.
        /// </summary>
        internal static RayTracingSubMeshFlags SubmeshOn(Material m) =>
            RayTracingSubMeshFlags.Enabled | (IsAlphaTested(m) ? 0 : RayTracingSubMeshFlags.ClosestHitOnly);

        private static bool IsAlphaTested(Material m)
        {
            if (m == null) return false;
            if (m.HasProperty("_AlphaTest") && m.shader != null && m.shader.name == "UltraRT/Proxy") return m.GetFloat("_AlphaTest") > 0.5f;
            return SourceAlphaTested(m);
        }

        private static bool SourceAlphaTested(Material m)
        {
            if (m == null) return false;
            if (m.IsKeywordEnabled("ALPHA_TEST") || m.IsKeywordEnabled("_ALPHATEST_ON") || m.IsKeywordEnabled("_ALPHA_TEST")) return true;
            if (m.renderQueue >= 2450 && m.renderQueue < 3000) return true; // Unity's AlphaTest queue range
            return m.HasProperty("_Mode") && m.shader != null && m.shader.name == "Standard" && Mathf.RoundToInt(m.GetFloat("_Mode")) == 1; // Cutout
        }

        private readonly HashSet<Material> _twoSidedProxies = new HashSet<Material>();
        private readonly HashSet<Material> _skyWallProxies = new HashSet<Material>();
        private bool IsSkyWall(Material proxy) => proxy != null && _skyWallProxies.Contains(proxy);

        /// <summary>
        /// Instance masks: shadow rays only test GeometryMask. Sky walls (LIMBO_WALLS, the "TV sky") stop camera and bounce
        /// rays and report sky, so the level behind them stays hidden as in game, but never block light.
        /// </summary>
        public const uint GeometryMask = 0x01, SkyWallMask = 0x02, FixtureMask = 0x04;

        /// <summary>
        /// Light fixtures: small objects whose bounds contain a point/spot light (torch bowls, lamp housings). ULTRAKILL
        /// puts lights inside them; the game has no shadows, but ray traced they'd shadow everything below their rim.
        /// Shadow rays toward point/spot lights skip FixtureMask; everything else still sees them.
        /// </summary>
        private bool IsLightFixture(Renderer r)
        {
            if (_pointLights == null)
                _pointLights = Object.FindObjectsOfType<Light>(true).Where(l => l.type == LightType.Point || l.type == LightType.Spot).ToArray();
            var rb = r.bounds;
            if (rb.size.magnitude > 4f) return false;
            var b = rb;
            b.Expand(0.2f);
            var encloses = false;
            foreach (var l in _pointLights)
            {
                if (l == null) continue;
                var p = l.transform.position;
                // Encloses the light (torch bowl, lamp housing), or holds it from just below (a fire sitting on a wide,
                // flat brazier: 1-1's BigTorch light is 0.25 m above its "Pipe"). Both are recorded for the light lift
                // (Plugin.UpdateFixtureLifts); only enclosing ones get the fixture mask.
                bool inside = b.Contains(p);
                bool under = !inside && p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z
                             && p.y > rb.max.y && p.y - rb.max.y < FixtureSupportGap;
                if (!inside && !under) continue;
                encloses |= inside;
                if (FixtureBounds.TryGetValue(l, out var e)) { e.Encapsulate(rb); FixtureBounds[l] = e; }
                else FixtureBounds[l] = rb;
                FixtureVersion++;
            }
            if (encloses) FixtureCount++;
            return encloses;
        }

        private const float FixtureSupportGap = 0.5f;
        private Light[] _pointLights;

        /// <summary>World bounds of the fixture(s) each light sits in (union when several renderers enclose it).</summary>
        public readonly Dictionary<Light, Bounds> FixtureBounds = new Dictionary<Light, Bounds>();
        /// <summary>Bumped whenever <see cref="FixtureBounds"/> changes.</summary>
        public int FixtureVersion { get; private set; }
        public int FixtureCount { get; private set; }
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

        /// <summary>
        /// The texture in a material slot, only if it is a plain 2D texture. The proxy's hit shader declares Texture2D;
        /// binding anything else (a cubemap, array or 3D texture: some 0-1 materials do) makes DispatchRays fail silently
        /// for the whole frame, so nothing at all is ray traced.
        /// </summary>
        internal Texture Tex2D(Material src, string prop)
        {
            var t = src.GetTexture(prop);
            if (t == null || t.dimension == TextureDimension.Tex2D) return t;
            if (RejectedTextures++ < 20) _log.LogWarning($"Not binding {t.dimension} texture '{t.name}' from {src.name}.{prop} (2D expected)");
            return null;
        }

        public int RejectedTextures { get; private set; }

        private Material Proxy(Material src)
        {
            if (src == null) return _disabledMaterial;
            if (_materialCache.TryGetValue(src, out var m)) return m;

            m = new Material(_proxyShader) { name = src.name + " [UltraRT]" };
            if (src.HasProperty("_MainTex") && Tex2D(src, "_MainTex") is Texture tex)
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
                m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
            }
            if (src.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));
            if (SourceAlphaTested(src))
            {
                m.SetFloat("_AlphaTest", 1f);
                m.SetFloat("_Cutoff", src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);
                AlphaTestedMaterials++;
            }

            // StaticSceneOptimizer's baked level geometry: _MainTex/_BlendTex are scene atlases, and each vertex carries
            // its atlas rects in TEXCOORD1 (main) / TEXCOORD2 (blend). UV0 tiles within those rects.
            if (src.shader != null && src.shader.name.Contains("Stationary"))
            {
                m.SetFloat("_AtlasMode", 1f);
                if (src.HasProperty("_BlendTex") && Tex2D(src, "_BlendTex") is Texture blend) m.SetTexture("_BlendTex", blend);
            }

            // ULTRAKILL/Master emission
            if (src.IsKeywordEnabled("EMISSIVE"))
            {
                if (src.HasProperty("_EmissiveTex") && Tex2D(src, "_EmissiveTex") is Texture etex) m.SetTexture("_EmissiveTex", etex);
                if (src.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", src.GetColor("_EmissiveColor"));
                m.SetFloat("_EmissiveIntensity", src.HasProperty("_EmissiveIntensity") ? Mathf.Max(src.GetFloat("_EmissiveIntensity"), 0.0001f) : 1f);
                bool albedoAsEmissive = src.IsKeywordEnabled("_USEALBEDOASEMISSIVE_ON") ||
                                        src.HasProperty("_UseAlbedoAsEmissive") && src.GetFloat("_UseAlbedoAsEmissive") > 0.5f;
                m.SetFloat("_UseAlbedoAsEmissive", albedoAsEmissive ? 1 : 0);
            }
            // Unity Standard emission
            else if (src.IsKeywordEnabled("_EMISSION") && src.HasProperty("_EmissionColor"))
            {
                if (src.HasProperty("_EmissionMap") && Tex2D(src, "_EmissionMap") is Texture etex) m.SetTexture("_EmissiveTex", etex);
                m.SetColor("_EmissiveColor", src.GetColor("_EmissionColor"));
                m.SetFloat("_EmissiveIntensity", 1f);
            }

            // Reflection parameters (MaterialClasses.cs). Atlas proxies get theirs per slot (AtlasVariant).
            if (src.shader != null && src.shader.name.Contains("Stationary"))
            {
                MaterialParams.Apply(m, MaterialParams.None(SurfaceClass.Other, "atlas"), null);
                _atlasProxies.Add(m);
            }
            else
            {
                var p = _classifier.Classify(src, out var secondary);
                MaterialParams.Apply(m, p, secondary);
                _classifier.Count(p);
                GameReflection.Apply(m, src);
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
            Motion.Dispose();
            _dynamic.Clear();
            _active.Clear();
            _dirty.Clear();
            DisposeStatic();
            foreach (var m in _materialCache.Values) if (m != null) Object.Destroy(m);
            _materialCache.Clear();
            foreach (var m in _atlasVariants.Values) if (m != null) Object.Destroy(m);
            _atlasVariants.Clear();
            _atlasProxies.Clear();
            if (_disabledMaterial != null) Object.Destroy(_disabledMaterial);
        }
    }
}
