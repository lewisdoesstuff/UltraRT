using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UltraRT
{
    /// <summary>
    /// Static-batched geometry that follows the game's room/area activation. See docs/scene-proxies.md.
    ///
    /// Model:
    ///  - A <see cref="Slot"/> is one submesh of a combined (static batch) mesh. StaticSceneOptimizer gives every source
    ///    renderer exactly one submesh; Unity's own static batching gives each renderer a contiguous run of them.
    ///  - A slot is visible while ANY of its source renderers is enabled and active in the hierarchy. Several renderers
    ///    can share a slot: CheckPoint clones its rooms (Instantiate keeps the static batch info), deactivates the
    ///    originals, and re-clones them on every respawn.
    ///  - Slots are partitioned into activation <see cref="Group"/>s. Each group/part (two-sided, sky-wall) owns one
    ///    proxy instance whose mesh holds only its slots, merged to one submesh per material (shader-table records).
    ///    A whole group switching on/off is an RTAS add/remove; a partly visible group gets a rebuilt (cached) mesh.
    ///  - Change detection, cheapest first: "watchers" (the GameObjects the game is known to toggle, or the room-level
    ///    ancestor) are polled every frame; CheckPoint.newRooms is polled for fresh clones; and a small slice of all
    ///    source renderers is polled per frame for everything else (renderer.enabled, deeper toggles, destruction).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>
        /// Upper bound on shader-table records from static groups if every group were visible; grouping coarsens to fit.
        /// Each record costs ~0.4 us/frame (1,300 fewer saved ~0.5 ms). 1-1 had 34 static records before grouping.
        /// </summary>
        public static int StaticRecordBudget = 384;

        /// <summary>-1: choose the finest grouping level that fits the budget; 0..4 forces one (4 = one group per combined mesh).</summary>
        public static int ForceGroupLevel = -1;

        /// <summary>false: static batches are always fully present (the old behaviour), for A/B tests.</summary>
        public static bool StaticActivation = true;

        /// <summary>Depth (0 = scene root object) of the default watcher, e.g. "Pre-Space/Rooms/10B - Night Street".</summary>
        public const int WatchDepth = 2;

        /// <summary>Level-0 groups smaller than this are folded into their depth-2 group (they'd cost a record each).</summary>
        private const int MinGroupTris = 96;

        /// <summary>Watchers are polled every frame (~30 ns each); past this, use coarser toggle points.</summary>
        private const int MaxWatchers = 1500;

        private const int StaticPollPerFrame = 256;
        private const int MaxPartialBuildsPerFrame = 4;
        private const int RetireFrames = 4;
        private const int SummaryFrames = 600;

        private static readonly string[] LevelNames = { "toggle point", "depth 2", "depth 1", "scene root", "combined mesh" };
        private const int Levels = 5;

        private sealed class Combined
        {
            public Mesh Mesh;
            public string Name;
            public Transform Root;
            public uint Id;
            public Slot[] Slots;            // per submesh; null = not traced
            public Material[] SubMaterials; // per submesh (flags mode)
            public bool FlagsMode;          // CPU copy unavailable: one instance, submesh flags follow visibility
            public Bounds Bounds;
            // CPU copy of the GPU-only mesh, for building group meshes
            public VertexAttributeDescriptor[] Attributes;
            public byte[][] Streams;
            public int[] Strides;
            public uint[] Indices;          // raw (add the slot's BaseVertex)
            public long CpuBytes;
        }

        private sealed class Slot
        {
            public int Sub, IndexStart, IndexCount, BaseVertex, VMin, VMax;
            public Material Material;
            public Material BaseMaterial;   // before the per-slot atlas variant (reflection class), for the record log
            public MeshRenderer Source;     // the source that decided the material (captured originals first)
            public int Part;                // bit 0 two-sided, bit 1 sky wall, bit 2 transparent (TransparentPart)
            public TransparentKind Kind;    // transparent slots: water, glass or blood
            public Group Group;
            public StaticSource Owner;      // first source seen; its ancestors define the grouping keys
            public int VisibleSources;
            public int MatGen;              // bumped when a source swaps this slot's material (SceneProxies.Sync.cs)
        }

        private sealed class StaticSource
        {
            public MeshRenderer Renderer;
            public GameObject Object;
            public Slot[] Slots;
            public GameObject Watch, WatchDeep, WatchShallow;
            public GameObject[] Keys;       // per grouping level (level 4 = null)
            public bool Visible, Dead;
            public Material[] Mats;         // source materials last seen (null: atlas source, never swapped)
            public Slot[] SlotByMat;        // per source material index: its slot, or null
            public int SwapPhase;           // multi-material sources: swap check on every 4th slice visit
        }

        private sealed class Watcher
        {
            public GameObject Object;
            public bool Active;
            public readonly List<StaticSource> Sources = new List<StaticSource>();
        }

        private sealed class Group
        {
            public Combined Combined;
            public string Name;
            public readonly List<Slot>[] Slots = new List<Slot>[PartCount];
            public readonly GroupPart[] Parts = new GroupPart[PartCount];
            public bool Dirty;
        }

        /// <summary>Part variants per group: two-sided x sky wall x transparent.</summary>
        private const int PartCount = 8;

        private sealed class GroupPart
        {
            public bool Transparent;        // mask TransparentMask (water/glass/blood), counted in ActiveTransparent
            public MeshRenderer Proxy;
            public MeshFilter Filter;
            public Mesh Full;
            public Material[] FullMaterials;
            public Mesh Partial;
            public Material[] PartialMaterials;
            public bool[] PartialSet;
            // The partial before that: a source that keeps switching (flicker, a door) alternates between two sets.
            public Mesh Partial2;
            public Material[] PartialMaterials2;
            public bool[] PartialSet2;
            public bool[] Shown;            // slots in the instance currently in the RTAS
            public bool InRtas;
            public int Records, FullRecords;
            public bool FullStale;          // a slot's material changed: rebuild Full before using it
            public Material[] CutoutChecked; // the materials HasCutout was computed for (SceneProxies.CutoutPass.cs)
            public bool HasCutout;
        }

        private sealed class CheckpointWatch
        {
            public CheckPoint Checkpoint;
            public GameObject[] Last;
        }

        private readonly Dictionary<Mesh, Combined> _combined = new Dictionary<Mesh, Combined>();
        private readonly List<Group> _groups = new List<Group>();
        private readonly List<StaticSource> _staticSources = new List<StaticSource>();
        private readonly List<Watcher> _watchers = new List<Watcher>();
        private readonly Dictionary<GameObject, Watcher> _watcherByObject = new Dictionary<GameObject, Watcher>();
        private readonly List<Group> _dirtyGroups = new List<Group>();
        private readonly HashSet<GameObject> _toggles = new HashSet<GameObject>();
        private readonly List<CheckpointWatch> _checkpoints = new List<CheckpointWatch>();
        private readonly List<GameObject> _pendingRoots = new List<GameObject>();
        private readonly List<(Mesh mesh, int frame)> _retired = new List<(Mesh, int)>();
        private readonly List<Transform> _chain = new List<Transform>(16);
        private int _staticCursor, _deadSources, _staticFrames, _groupLevel, _watchMode; // 0 deepest, 1 shallowest, 2 depth 2
        private static readonly string[] WatchModeNames = { "deepest toggle point", "shallowest toggle point", "depth 2 only" };

        // Counters: shader-table records and instances of static groups currently in the RTAS / in total.
        public int StaticRecordsActive { get; private set; }
        public int StaticInstancesActive { get; private set; }
        private int _staticRecordsTotal, _staticPartsTotal;

        // Periodic summary counters.
        private int _sumWatcherFlips, _sumSourceFlips, _sumPartial, _sumReused, _sumAddRemove, _sumLate, _sumDeferred;
        private double _sumPartialMs, _maxPartialMs;
        private int _loggedPartial;

        // ---------------------------------------------------------------- build

        private void BuildStaticGroups(Dictionary<Mesh, List<MeshRenderer>> batches)
        {
            var sw = Stopwatch.StartNew();
            _toggles.Clear();
            CollectToggleTargets(Object.FindObjectsOfType<MonoBehaviour>(true));
            foreach (var cp in Object.FindObjectsOfType<CheckPoint>(true)) TrackCheckpoint(cp);
            double toggleMs = sw.Elapsed.TotalMilliseconds;

            var sources = new List<StaticSource>();
            foreach (var kv in batches)
            {
                var c = CreateCombined(kv.Key, kv.Value);
                if (c == null) continue;
                _combined[kv.Key] = c;
                foreach (var r in kv.Value)
                {
                    var s = CreateSource(c, r);
                    if (s != null) sources.Add(s);
                }
            }
            // Batched water/glass that got no transparent slot (a combined mesh without triangles, or not readable).
            foreach (var kv in batches)
                foreach (var r in kv.Value)
                    if (TransparentCandidate(r, r.sharedMaterials) && !(_combined.TryGetValue(kv.Key, out var cc) && HasTransparentSlot(cc, r)))
                        _transparentStatic++;
            Maps.BuildAtlases(); // per-texel detail atlases, from the cells AddAtlasCells recorded
            if (Maps.Available)
                _log.LogInfo($"Material maps: atlas slots mapped per original submesh {_atlasSpansRanged}, by dominant material {_atlasSpansWhole}, " +
                             $"skipped (several materials, ranges unknown) {_atlasSpansSkipped}");
            double readMs = sw.Elapsed.TotalMilliseconds - toggleMs;

            // Watchers: the deepest toggle point per source, unless that makes too many to poll every frame.
            _watchMode = 0;
            while (_watchMode < 2 && sources.Select(s => WatchFor(s, _watchMode)).Distinct().Count() > MaxWatchers) _watchMode++;
            foreach (var s in sources) s.Keys[0] = s.Watch = WatchFor(s, _watchMode);

            // Grouping: fold tiny level-0 groups into their depth-2 group, then pick the finest level within budget.
            var all = _combined.Values.ToList();
            int coalesced = CoalesceTinyGroups(all);
            var records = new int[Levels];
            for (int l = 0; l < Levels; l++) records[l] = CountRecords(all, l);
            var baseRecords = new int[Levels]; // without the reflection-class atlas variants
            for (int l = 0; l < Levels; l++) baseRecords[l] = CountRecords(all, l, useBase: true);
            int level = Levels - 1;
            if (ForceGroupLevel >= 0 && ForceGroupLevel < Levels) level = ForceGroupLevel;
            else for (int l = 0; l < Levels; l++) if (records[l] <= StaticRecordBudget) { level = l; break; }
            _groupLevel = level;
            CreateGroups(all, level);

            if (StaticActivation)
            {
                foreach (var s in sources) RegisterSource(s);
            }
            else
            {
                // Old behaviour: everything present, never re-evaluated.
                foreach (var c in all) foreach (var slot in c.Slots) if (slot != null) slot.VisibleSources = 1;
            }
            foreach (var g in _groups) MarkGroupDirty(g);
            foreach (var s in sources) NoteStaticTransparent(s, late: false);

            long cpuBytes = all.Sum(c => c.CpuBytes);
            int flagsMode = all.Count(c => c.FlagsMode);
            _log.LogInfo($"Static activation groups: level {level} ({LevelNames[level]}), {_groups.Count} groups, {_staticPartsTotal} part instances, " +
                         $"{_staticRecordsTotal} records (budget {StaticRecordBudget}; by level {string.Join("/", records)}), " +
                         $"{_watchers.Count} watchers ({WatchModeNames[_watchMode]}), {_toggles.Count} toggle targets, {sources.Count} sources, {coalesced} tiny groups folded, " +
                         $"{flagsMode} combined meshes in flags mode, CPU copy {cpuBytes / (1024.0 * 1024.0):F1} MB, " +
                         $"took {sw.Elapsed.TotalMilliseconds:F0} ms (toggle scan {toggleMs:F0} ms, readback {readMs:F0} ms){(StaticActivation ? "" : " [activation OFF]")}");
            if (_atlasVariants.Count > 0)
                _log.LogInfo($"Reflection material variants: {_atlasVariants.Count} atlas variants; static records at level {level}: {records[level]} " +
                             $"(without variants {baseRecords[level]}; by level {string.Join("/", baseRecords)})");
            foreach (var g in _groups.OrderByDescending(g => g.Slots.Where(l => l != null).Sum(l => l.Sum(s => s.IndexCount))).Take(DumpAtlasVertices ? 40 : 8))
                _log.LogInfo($"  group '{g.Combined.Name}' / '{g.Name}': {g.Slots.Where(l => l != null).Sum(l => l.Count)} submeshes, " +
                             $"{g.Slots.Where(l => l != null).Sum(l => l.Sum(s => s.IndexCount)) / 3:N0} tris, {g.Parts.Where(p => p != null).Sum(p => p.FullRecords)} records");
        }

        private Combined CreateCombined(Mesh combined, List<MeshRenderer> sources)
        {
            int subCount = combined.subMeshCount;
            var c = new Combined
            {
                Mesh = combined,
                Name = combined.name,
                // Combined vertices live in the batch root's space, or world space if there is no root.
                Root = StaticBatchRoot?.GetValue(sources[0]) as Transform,
                // All sources of one combined mesh share a layer in practice (StaticSceneOptimizer: one per material).
                Id = (uint)OriginalLayer(sources[0].gameObject.layer),
                Slots = new Slot[subCount],
                SubMaterials = Enumerable.Repeat(_disabledMaterial, subCount).ToArray(),
                Bounds = combined.bounds,
            };
            bool any = false;
            // Renderers whose original materials were captured first: a slot's material is decided by the first source
            // seen, and checkpoint room clones (made after StaticSceneOptimizer ran) have no record of their originals.
            foreach (var r in sources.OrderBy(r => OriginalMaterials.IsCaptured(r) ? 0 : 1))
            {
                var src = r.sharedMaterials;
                bool transparentOk = TransparentCandidate(r, src);
                for (int i = 0; i < src.Length; i++)
                {
                    int sub = r.subMeshStartIndex + i;
                    if (sub < 0 || sub >= subCount || c.Slots[sub] != null) continue;
                    if (combined.GetTopology(sub) != MeshTopology.Triangles) continue;
                    var tk = transparentOk ? TransparentClassifier.Of(src[i]) : TransparentKind.None;
                    if (tk != TransparentKind.None)
                    {
                        // Water/glass/blood: its own part (mask 0x10, only the transparent pass sees it), the transparent proxy material.
                        var tm = TransparentProxy(src[i], tk);
                        c.Slots[sub] = new Slot { Sub = sub, Material = tm, BaseMaterial = tm, Source = r, Kind = tk, Part = TransparentPart | (IsTwoSided(tm) ? 1 : 0) };
                        c.SubMaterials[sub] = tm;
                        any = true;
                        continue;
                    }
                    if (!Traceable(src[i])) continue;
                    var pm = Proxy(src[i]);
                    // Baked geometry: the atlas material says nothing about the surface; use the source's original material.
                    var mat = IsAtlasProxy(pm) ? AtlasVariant(pm, r) : pm;
                    c.Slots[sub] = new Slot { Sub = sub, Material = mat, BaseMaterial = pm, Source = r, Part = (IsTwoSided(pm) ? 1 : 0) | (IsSkyWall(pm) ? 2 : 0) };
                    c.SubMaterials[sub] = mat;
                    any = true;
                }
            }
            if (!any) return null;

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
            // hundreds of submeshes sharing a handful of materials, so group meshes are merged by material
            // (~0.5 ms/frame saved). That needs the vertex/index data on the CPU; without it, fall back to flags.
            bool read = TryReadCombined(c);
            c.FlagsMode = NoSubmeshMerge || !read;
            if (read) AddAtlasCells(c);
            return c;
        }

        /// <summary>
        /// Per-texel detail (MaterialMaps, docs/materials.md): which original texture each atlas rect of this combined mesh
        /// shows. SSO collapses a source's submeshes into one slot; when the original submesh index counts add up to the
        /// slot's, the slot's triangles are taken to be in the original submesh order (checked: each span must use one
        /// rect), so every original material gets its own rects. Otherwise the slot's dominant material gets them all,
        /// and only single-rect slots are trusted.
        /// </summary>
        private void AddAtlasCells(Combined c)
        {
            if (!Maps.Available || c.Indices == null) return;
            VertexAttributeReader uv1 = null;
            foreach (var a in c.Attributes)
            {
                if (a.attribute != VertexAttribute.TexCoord1) continue;
                if (a.format != VertexAttributeFormat.Float16 && a.format != VertexAttributeFormat.Float32) return;
                uv1 = new VertexAttributeReader(c.Streams[a.stream], c.Strides[a.stream], c.Mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord1),
                                                a.dimension, a.format == VertexAttributeFormat.Float16);
            }
            if (uv1 == null) return;
            var spans = new List<MaterialMaps.AtlasSpan>();
            foreach (var slot in c.Slots)
            {
                if (slot == null || slot.Source == null || !IsAtlasProxy(slot.BaseMaterial)) continue;
                var atlas = slot.BaseMaterial.GetTexture("_MainTex");
                if (!OriginalMaterials.TryGet(slot.Source, out var mats, out var counts, out _)) continue;
                bool Usable(Material m) => Traceable(m) && !(m.shader != null && m.shader.name.Contains("Stationary"));
                spans.Clear();
                if (counts != null && counts.Length == mats.Length && counts.Sum(n => (long)(n - n % 3)) == slot.IndexCount && mats.Length > 1)
                {
                    int start = slot.IndexStart;
                    bool ok = true;
                    for (int i = 0; i < mats.Length && ok; i++)
                    {
                        int n = counts[i] - counts[i] % 3;
                        if (n > 0 && Usable(mats[i]))
                        {
                            ok = DistinctRects(c, uv1, start, n, slot.BaseVertex) == 1;
                            spans.Add(new MaterialMaps.AtlasSpan { Original = mats[i], Params = _classifier.Classify(mats[i], out _), IndexStart = start, IndexCount = n, BaseVertex = slot.BaseVertex });
                        }
                        start += n;
                    }
                    if (ok) { _atlasSpansRanged++; Maps.AddAtlasSlot(atlas, uv1, c.Indices, spans); continue; }
                    spans.Clear();
                }
                var dom = OriginalMaterials.Dominant(mats, counts, Usable);
                if (dom == null) continue;
                if (mats.Count(Usable) > 1 && DistinctRects(c, uv1, slot.IndexStart, slot.IndexCount, slot.BaseVertex) != 1) { _atlasSpansSkipped++; continue; }
                spans.Add(new MaterialMaps.AtlasSpan { Original = dom, Params = _classifier.Classify(dom, out _), IndexStart = slot.IndexStart, IndexCount = slot.IndexCount, BaseVertex = slot.BaseVertex });
                _atlasSpansWhole++;
                Maps.AddAtlasSlot(atlas, uv1, c.Indices, spans);
            }
        }

        private int _atlasSpansRanged, _atlasSpansWhole, _atlasSpansSkipped;

        private static int DistinctRects(Combined c, VertexAttributeReader uv1, int start, int count, int baseVertex)
        {
            var set = new HashSet<Vector4>();
            for (int k = 0; k + 2 < count; k += 3) set.Add(uv1.Read4((int)c.Indices[start + k] + baseVertex));
            return set.Count;
        }

        /// <summary>
        /// GPU readback of a (non-readable) combined mesh: raw vertex streams and indices, plus each slot's index range
        /// and the vertex range it touches. Kept for the scene's lifetime so groups can be rebuilt without readbacks.
        /// </summary>
        private bool TryReadCombined(Combined c)
        {
            var src = c.Mesh;
            try
            {
                uint[] idx;
                using (var ib = src.GetIndexBuffer())
                {
                    if (src.indexFormat == IndexFormat.UInt32) { idx = new uint[ib.count]; ib.GetData(idx); }
                    else
                    {
                        var packed = new uint[(ib.count + 1) / 2];
                        ib.GetData(packed, 0, 0, packed.Length); // 16-bit buffer read as 32-bit words
                        idx = new uint[ib.count];
                        for (int k = 0; k < ib.count; k++) idx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                    }
                }

                int vertexCount = src.vertexCount;
                foreach (var slot in c.Slots)
                {
                    if (slot == null) continue;
                    var d = src.GetSubMesh(slot.Sub);
                    slot.IndexStart = d.indexStart;
                    slot.IndexCount = d.indexCount - d.indexCount % 3;
                    slot.BaseVertex = d.baseVertex;
                    int min = int.MaxValue, max = -1;
                    for (int k = 0; k < slot.IndexCount; k++)
                    {
                        int v = (int)idx[slot.IndexStart + k] + slot.BaseVertex;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (slot.IndexCount == 0 || min < 0 || max >= vertexCount) throw new Exception($"submesh {slot.Sub} indices out of range");
                    slot.VMin = min;
                    slot.VMax = max;
                }

                c.Attributes = src.GetVertexAttributes();
                c.Streams = new byte[src.vertexBufferCount][];
                c.Strides = new int[src.vertexBufferCount];
                long bytes = idx.Length * 4L;
                for (int s = 0; s < src.vertexBufferCount; s++)
                {
                    int stride = src.GetVertexBufferStride(s);
                    c.Strides[s] = stride;
                    if (stride == 0) { c.Streams[s] = new byte[0]; continue; }
                    using (var vb = src.GetVertexBuffer(s))
                    {
                        var words = new uint[(vertexCount * stride + 3) / 4];
                        vb.GetData(words, 0, 0, words.Length);
                        var b = new byte[vertexCount * stride];
                        Buffer.BlockCopy(words, 0, b, 0, b.Length);
                        c.Streams[s] = b;
                        bytes += b.Length;
                    }
                }
                c.Indices = idx;
                c.CpuBytes = bytes;
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Readback failed for '{src.name}', using submesh flags (more shader-table records): {e.Message}");
                c.Streams = null;
                c.Indices = null;
                return false;
            }
        }

        private StaticSource CreateSource(Combined c, MeshRenderer r)
        {
            var mats = r.sharedMaterials;
            var slots = new List<Slot>(mats.Length);
            var byMat = new Slot[mats.Length];
            bool swappable = false;
            for (int i = 0; i < mats.Length; i++)
            {
                int sub = r.subMeshStartIndex + i;
                // Our overlay, appended to a transparent source (and copied into checkpoint clones of it): not a submesh of
                // this source; its index would land on the next source's slot.
                if (TransparentOverlays.IsOverlay(mats[i])) continue;
                if (sub >= 0 && sub < c.Slots.Length && c.Slots[sub] != null)
                {
                    slots.Add(c.Slots[sub]);
                    byMat[i] = c.Slots[sub];
                    swappable |= !IsAtlasProxy(c.Slots[sub].BaseMaterial) && (c.Slots[sub].Part & TransparentPart) == 0;
                }
            }
            if (slots.Count == 0) return null;

            // Ancestors, deepest first: _chain[0] = the renderer, _chain[n-1] = its scene root object.
            _chain.Clear();
            for (var t = r.transform; t != null; t = t.parent) _chain.Add(t);
            int n = _chain.Count;
            GameObject AtDepth(int depth) => _chain[Math.Max(0, n - 1 - depth)].gameObject;
            var d2 = AtDepth(2);
            // Watcher: the deepest known toggle point (referenced by an activator, or inactive right now), but never
            // shallower than depth 2: a deeper watcher's activeInHierarchy still reflects every ancestor.
            GameObject deep = null, shallow = null;
            for (int i = 0; i < n - 1 - WatchDepth; i++)
            {
                var go = _chain[i].gameObject;
                if (!_toggles.Contains(go) && go.activeSelf) continue;
                if (deep == null) deep = go;
                shallow = go;
            }

            var s = new StaticSource
            {
                Renderer = r,
                Object = r.gameObject,
                Slots = slots.ToArray(),
                WatchDeep = deep ?? d2,
                WatchShallow = shallow ?? d2,
                Keys = new[] { null, d2, AtDepth(1), AtDepth(0), null },
                Mats = swappable ? mats : null,
                SlotByMat = byMat,
            };
            s.Keys[0] = s.Watch = WatchFor(s, _watchMode);
            foreach (var slot in slots) if (slot.Owner == null) slot.Owner = s;
            return s;
        }

        private static bool HasTransparentSlot(Combined c, MeshRenderer r)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                int sub = r.subMeshStartIndex + i;
                if (sub >= 0 && sub < c.Slots.Length && c.Slots[sub] != null && (c.Slots[sub].Part & TransparentPart) != 0) return true;
            }
            return false;
        }

        /// <summary>A static source with transparent slots: overlay, under-water test, log (SceneProxies.Transparent.cs).</summary>
        private void NoteStaticTransparent(StaticSource s, bool late)
        {
            if (s.Renderer == null) return;
            var kinds = s.Slots.Where(x => (x.Part & TransparentPart) != 0).Select(x => x.Kind).ToList();
            if (kinds.Count > 0) NoteStaticTransparent(s.Renderer, kinds, late);
        }

        private static GameObject WatchFor(StaticSource s, int mode) => mode == 0 ? s.WatchDeep : mode == 1 ? s.WatchShallow : s.Keys[1];

        private static GameObject KeyAt(Slot slot, int level) => slot.Owner.Keys[level];

        /// <summary>Level-0 groups below MinGroupTris use the depth-2 key instead. Returns how many were folded.</summary>
        private int CoalesceTinyGroups(List<Combined> all)
        {
            var tris = new Dictionary<(Combined, GameObject), int>();
            foreach (var c in all)
                foreach (var slot in c.Slots)
                {
                    if (slot == null || c.FlagsMode) continue;
                    var key = (c, KeyAt(slot, 0));
                    tris.TryGetValue(key, out int t);
                    tris[key] = t + slot.IndexCount / 3;
                }
            var folded = new HashSet<(Combined, GameObject)>();
            foreach (var c in all)
                foreach (var slot in c.Slots)
                {
                    if (slot == null || c.FlagsMode) continue;
                    var key = (c, KeyAt(slot, 0));
                    if (tris[key] >= MinGroupTris || key.Item2 == slot.Owner.Keys[1]) continue;
                    folded.Add(key);
                    // Owner keys are per source; fold just this slot's view of level 0 via a per-slot override.
                    _foldedSlots.Add(slot);
                }
            return folded.Count;
        }

        private readonly HashSet<Slot> _foldedSlots = new HashSet<Slot>();

        private GameObject GroupKey(Combined c, Slot slot, int level)
        {
            if (c.FlagsMode || level >= Levels - 1) return null;
            if (level == 0 && _foldedSlots.Contains(slot)) return KeyAt(slot, 1);
            return KeyAt(slot, level);
        }

        /// <summary>Records if every group were visible: distinct materials per (group, part); flags mode: every slot.</summary>
        private int CountRecords(List<Combined> all, int level, bool useBase = false)
        {
            var set = new HashSet<(Combined, GameObject, int, Material)>();
            int flags = 0;
            foreach (var c in all)
                foreach (var slot in c.Slots)
                {
                    if (slot == null) continue;
                    if (c.FlagsMode) flags++;
                    else set.Add((c, GroupKey(c, slot, level), slot.Part, useBase ? slot.BaseMaterial : slot.Material));
                }
            return set.Count + flags;
        }

        private void CreateGroups(List<Combined> all, int level)
        {
            var byKey = new Dictionary<(Combined, GameObject), Group>();
            foreach (var c in all)
                foreach (var slot in c.Slots)
                {
                    if (slot == null) continue;
                    var key = (c, GroupKey(c, slot, level));
                    if (!byKey.TryGetValue(key, out var g))
                    {
                        g = new Group { Combined = c, Name = key.Item2 != null ? key.Item2.name : "all" };
                        byKey[key] = g;
                        _groups.Add(g);
                    }
                    (g.Slots[slot.Part] ?? (g.Slots[slot.Part] = new List<Slot>())).Add(slot);
                    slot.Group = g;
                }

            int index = 0;
            foreach (var g in _groups)
            {
                var c = g.Combined;
                for (int p = 0; p < PartCount; p++)
                {
                    var list = g.Slots[p];
                    if (list == null) continue;
                    var gp = new GroupPart { Transparent = (p & TransparentPart) != 0 };
                    if (c.FlagsMode)
                    {
                        gp.Full = c.Mesh;
                        gp.FullMaterials = c.SubMaterials;
                        gp.FullRecords = list.Count;
                    }
                    else
                    {
                        gp.Full = BuildGroupMesh(c, list, null, $"{c.Name} #{index} {g.Name}", out gp.FullMaterials);
                        gp.FullRecords = gp.FullMaterials.Length;
                    }
                    gp.Proxy = CreateMeshProxy($"{c.Name} #{index} {g.Name}{PartName(p)} [UltraRT]",
                        c.Root, gp.Full, gp.FullMaterials);
                    gp.Filter = gp.Proxy.GetComponent<MeshFilter>();
                    g.Parts[p] = gp;
                    _staticRecordsTotal += gp.FullRecords;
                    _staticPartsTotal++;
                }
                index++;
            }
        }

        /// <summary>
        /// One mesh with the chosen slots of a combined mesh: one submesh per material, vertices compacted to the
        /// ranges those slots touch (copied verbatim, so the hit shader sees the same layout).
        /// </summary>
        private Mesh BuildGroupMesh(Combined c, List<Slot> slots, bool[] include, string name, out Material[] materials)
        {
            var order = new List<Material>(4);
            var byMat = new List<List<Slot>>(4);
            var spans = new List<Vector2Int>(slots.Count);
            int indexTotal = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (include != null && !include[i]) continue;
                var s = slots[i];
                int m = order.IndexOf(s.Material);
                if (m < 0) { m = order.Count; order.Add(s.Material); byMat.Add(new List<Slot>()); }
                byMat[m].Add(s);
                indexTotal += s.IndexCount;
                spans.Add(new Vector2Int(s.VMin, s.VMax));
            }

            // Merge the slots' vertex ranges (they may overlap: submeshes of one source share its vertices).
            spans.Sort((a, b) => a.x.CompareTo(b.x));
            var starts = new List<int>();
            var ends = new List<int>();
            var newStarts = new List<int>();
            int vertexCount = 0;
            foreach (var sp in spans)
            {
                int last = ends.Count - 1;
                if (last >= 0 && sp.x <= ends[last] + 1)
                {
                    if (sp.y > ends[last]) { vertexCount += sp.y - ends[last]; ends[last] = sp.y; }
                }
                else
                {
                    starts.Add(sp.x);
                    ends.Add(sp.y);
                    newStarts.Add(vertexCount);
                    vertexCount += sp.y - sp.x + 1;
                }
            }

            var idx = new int[indexTotal];
            var descs = new SubMeshDescriptor[order.Count];
            int n = 0;
            for (int m = 0; m < order.Count; m++)
            {
                int start = n;
                foreach (var s in byMat[m])
                {
                    int span = starts.BinarySearch(s.VMin);
                    if (span < 0) span = ~span - 1; // last range starting at or before VMin
                    int offset = s.BaseVertex + newStarts[span] - starts[span];
                    for (int k = 0; k < s.IndexCount; k++) idx[n++] = (int)c.Indices[s.IndexStart + k] + offset;
                }
                descs[m] = new SubMeshDescriptor(start, n - start, MeshTopology.Triangles)
                {
                    firstVertex = 0,
                    vertexCount = vertexCount,
                    bounds = c.Bounds,
                };
            }

            var dst = new Mesh { name = name + " [UltraRT merged]", indexFormat = IndexFormat.UInt32 };
            try
            {
                const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                dst.SetVertexBufferParams(vertexCount, c.Attributes);
                for (int s = 0; s < c.Streams.Length; s++)
                {
                    int stride = c.Strides[s];
                    if (stride == 0) continue;
                    var bytes = new byte[vertexCount * stride];
                    for (int i = 0; i < starts.Count; i++)
                        Buffer.BlockCopy(c.Streams[s], starts[i] * stride, bytes, newStarts[i] * stride, (ends[i] - starts[i] + 1) * stride);
                    dst.SetVertexBufferData(bytes, 0, 0, bytes.Length, s, quiet);
                }
                dst.SetIndexBufferParams(idx.Length, IndexFormat.UInt32);
                dst.SetIndexBufferData(idx, 0, 0, idx.Length, quiet);
                dst.subMeshCount = descs.Length;
                for (int i = 0; i < descs.Length; i++) dst.SetSubMesh(i, descs[i], quiet);
                dst.bounds = c.Bounds;
                dst.UploadMeshData(true); // GPU only, like the source
            }
            catch
            {
                Object.Destroy(dst);
                throw;
            }
            materials = order.ToArray();
            return dst;
        }

        // ---------------------------------------------------------------- toggle points

        private static readonly Dictionary<Type, FieldInfo[]> ToggleFieldCache = new Dictionary<Type, FieldInfo[]>();

        /// <summary>GameObject fields (besides UltrakillEvent lists) that the game toggles, by name.</summary>
        private static readonly HashSet<string> ToggleFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "toActivate", "toDisactivate", "toDisActivate", "toDeactivate", "toDeActivate", "toActivateObjects", "toDisActivateObjects",
            "activatedRooms", "deactivatedRooms", "rooms", "defaultRooms", "newRooms", "roomsToInherit", "toAppear", "toDisappear",
        };

        private static FieldInfo[] ToggleFields(Type type)
        {
            if (ToggleFieldCache.TryGetValue(type, out var fields)) return fields;
            var list = new List<FieldInfo>();
            if (type.Assembly == typeof(UltrakillEvent).Assembly)
            {
                for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var ft = f.FieldType;
                        if (ft == typeof(UltrakillEvent) || ft == typeof(UltrakillEvent[]) || ft == typeof(List<UltrakillEvent>)) list.Add(f);
                        else if ((ft == typeof(GameObject) || ft == typeof(GameObject[]) || ft == typeof(List<GameObject>)) && ToggleFieldNames.Contains(f.Name)) list.Add(f);
                    }
            }
            return ToggleFieldCache[type] = list.ToArray();
        }

        /// <summary>Everything the level's scripts can SetActive: UltrakillEvent targets (ObjectActivator and ~60 others), rooms of doors and checkpoints.</summary>
        private void CollectToggleTargets(MonoBehaviour[] behaviours)
        {
            foreach (var b in behaviours)
            {
                if (b == null) continue;
                foreach (var f in ToggleFields(b.GetType()))
                {
                    object value;
                    try { value = f.GetValue(b); }
                    catch { continue; }
                    AddToggleValue(value);
                }
            }
        }

        private void AddToggleValue(object value)
        {
            switch (value)
            {
                case GameObject go: if (go != null) _toggles.Add(go); break;
                case GameObject[] arr: foreach (var go in arr) if (go != null) _toggles.Add(go); break;
                case List<GameObject> list: foreach (var go in list) if (go != null) _toggles.Add(go); break;
                case UltrakillEvent e: AddToggleValue(e.toActivateObjects); AddToggleValue(e.toDisActivateObjects); break;
                case UltrakillEvent[] es: foreach (var e in es) AddToggleValue(e); break;
                case List<UltrakillEvent> el: foreach (var e in el) AddToggleValue(e); break;
            }
        }

        // ---------------------------------------------------------------- registration

        private void RegisterSource(StaticSource s)
        {
            _staticSources.Add(s);
            if (!_watcherByObject.TryGetValue(s.Watch, out var w))
            {
                w = new Watcher { Object = s.Watch, Active = s.Watch.activeInHierarchy };
                _watcherByObject[s.Watch] = w;
                _watchers.Add(w);
            }
            w.Sources.Add(s);
            EvaluateStatic(s);
        }

        /// <summary>A static-batched renderer found after the build (checkpoint room clones): one more source for its slots.</summary>
        private bool AddLateStaticSource(MeshRenderer r, Mesh mesh)
        {
            if (!StaticActivation || !_combined.TryGetValue(mesh, out var c)) return false;
            var s = CreateSource(c, r);
            if (s == null) return false;
            RegisterSource(s);
            NoteStaticTransparent(s, late: true);
            _sumLate++;
            return true;
        }

        private void TrackCheckpoint(CheckPoint cp)
        {
            if (cp == null || _checkpoints.Any(w => w.Checkpoint == cp)) return;
            _checkpoints.Add(new CheckpointWatch { Checkpoint = cp, Last = cp.newRooms?.ToArray() ?? new GameObject[0] });
        }

        /// <summary>A room clone appeared: learn its toggle points and checkpoints, then register its renderers.</summary>
        private void RegisterRoot(GameObject root)
        {
            long t0 = Perf.Now;
            int lateBefore = _sumLate, queuedBefore = DeferredCount;
            _toggles.Add(root);
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            CollectToggleTargets(behaviours);
            foreach (var cp in root.GetComponentsInChildren<CheckPoint>(true)) TrackCheckpoint(cp);
            long t1 = Perf.Now;
            AddHierarchy(root);
            _log.LogInfo($"Room clone '{root.name}': {behaviours.Length} behaviours scanned for toggle points in {(t1 - t0) * Perf.TickMs:F1} ms, " +
                         $"+{_sumLate - lateBefore} late static sources and {DeferredCount - queuedBefore} renderers queued in {(Perf.Now - t1) * Perf.TickMs:F1} ms");
        }

        // ---------------------------------------------------------------- per frame

        private void SyncStatic(bool force)
        {
            if (_groups.Count == 0) return;
            _staticFrames++;

            // 0) Meshes replaced a few frames ago: the GPU is done with them.
            for (int i = _retired.Count - 1; i >= 0; i--)
                if (Time.frameCount - _retired[i].frame > RetireFrames || force)
                {
                    if (_retired[i].mesh != null) Object.Destroy(_retired[i].mesh);
                    _retired.RemoveAt(i);
                }

            if (StaticActivation)
            {
                // 1) CheckPoint clones its rooms on activation and on every respawn: new sources for known slots.
                for (int i = _checkpoints.Count - 1; i >= 0; i--)
                {
                    var w = _checkpoints[i];
                    if (w.Checkpoint == null) { _checkpoints.RemoveAt(i); continue; }
                    var rooms = w.Checkpoint.newRooms;
                    if (rooms == null || SameRefs(rooms, w.Last)) continue;
                    foreach (var go in rooms) if (go != null && Array.IndexOf(w.Last, go) < 0) _pendingRoots.Add(go);
                    w.Last = rooms.ToArray();
                }
                if (_pendingRoots.Count > 0)
                {
                    var roots = _pendingRoots.ToArray();
                    _pendingRoots.Clear();
                    foreach (var go in roots) if (go != null) RegisterRoot(go);
                }

                // 2) Watchers, every frame: rooms/areas switching on or off.
                for (int i = _watchers.Count - 1; i >= 0; i--)
                {
                    var w = _watchers[i];
                    var go = w.Object;
                    if (go == null)
                    {
                        foreach (var s in w.Sources) EvaluateStatic(s);
                        _watcherByObject.Remove(go);
                        _watchers[i] = _watchers[_watchers.Count - 1];
                        _watchers.RemoveAt(_watchers.Count - 1);
                        _sumWatcherFlips++;
                        continue;
                    }
                    bool active = go.activeInHierarchy;
                    if (active == w.Active) continue;
                    w.Active = active;
                    _sumWatcherFlips++;
                    // The watcher is an ancestor (or self) of all its sources: if it is off, so are they.
                    foreach (var s in w.Sources) EvaluateStatic(s, active ? (bool?)null : false);
                }

                // 3) A slice of all sources: renderer.enabled, toggles below the watcher, destroyed renderers.
                int n = _staticSources.Count;
                for (int k = 0, count = Math.Min(n, force ? n : StaticPollPerFrame); k < count; k++)
                {
                    _staticCursor = (_staticCursor + 1) % n;
                    var src = _staticSources[_staticCursor];
                    EvaluateStatic(src);
                    if (MaterialSync && !src.Dead && src.Mats != null) CheckStaticSwap(src);
                }
                if (_deadSources > 1024 && _deadSources * 2 > _staticSources.Count) CompactSources();
            }

            // 4) Groups whose visible set changed.
            if (_dirtyGroups.Count > 0)
            {
                int budget = force ? int.MaxValue : MaxPartialBuildsPerFrame;
                int keep = 0;
                for (int i = 0; i < _dirtyGroups.Count; i++)
                {
                    var g = _dirtyGroups[i];
                    if (UpdateGroup(g, ref budget)) g.Dirty = false;
                    else _dirtyGroups[keep++] = g;
                }
                _sumDeferred += keep;
                _dirtyGroups.RemoveRange(keep, _dirtyGroups.Count - keep);
            }

            if (_staticFrames >= SummaryFrames)
            {
                if (_sumWatcherFlips + _sumSourceFlips + _sumLate > 0)
                    _log.LogInfo($"Static activation (level {_groupLevel}): {StaticInstancesActive}/{_staticPartsTotal} instances, {StaticRecordsActive}/{_staticRecordsTotal} records in RTAS; " +
                                 $"last {_staticFrames} frames: {_sumWatcherFlips} watcher flips, {_sumSourceFlips} source flips, {_sumAddRemove} add/remove, " +
                                 $"{_sumPartial} partial builds ({_sumPartialMs:F2} ms, max {_maxPartialMs:F2}), {_sumReused} cached partials, " +
                                 $"{_sumDeferred} deferred, +{_sumLate} late sources; {_staticSources.Count} sources, {_watchers.Count} watchers");
                _staticFrames = 0;
                _sumWatcherFlips = _sumSourceFlips = _sumPartial = _sumReused = _sumAddRemove = _sumLate = _sumDeferred = 0;
                _sumPartialMs = _maxPartialMs = 0;
            }
        }

        private static bool SameRefs(List<GameObject> a, GameObject[] b)
        {
            if (a.Count != b.Length) return false;
            for (int i = 0; i < b.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        private void EvaluateStatic(StaticSource s, bool? known = null)
        {
            if (s.Dead) return;
            bool visible;
            if (known.HasValue) visible = known.Value;
            else
            {
                var r = s.Renderer;
                if (r == null || s.Object == null)
                {
                    s.Dead = true;
                    _deadSources++;
                    visible = false;
                }
                else visible = r.enabled && s.Object.activeInHierarchy;
            }
            if (visible == s.Visible) return;
            s.Visible = visible;
            _sumSourceFlips++;
            foreach (var slot in s.Slots)
            {
                slot.VisibleSources += visible ? 1 : -1;
                MarkGroupDirty(slot.Group);
            }
        }

        private void MarkGroupDirty(Group g)
        {
            if (g == null || g.Dirty) return;
            g.Dirty = true;
            _dirtyGroups.Add(g);
        }

        private void CompactSources()
        {
            _staticSources.RemoveAll(s => s.Dead);
            foreach (var w in _watchers) w.Sources.RemoveAll(s => s.Dead);
            _deadSources = 0;
            _staticCursor = 0;
        }

        /// <summary>Brings every part of the group in line with its slots. False if a rebuild had to wait (budget).</summary>
        private bool UpdateGroup(Group g, ref int budget)
        {
            bool done = true;
            for (int p = 0; p < PartCount; p++)
                if (g.Parts[p] != null && !UpdatePart(g, p, ref budget)) done = false;
            return done;
        }

        private bool UpdatePart(Group g, int p, ref int budget)
        {
            var list = g.Slots[p];
            var gp = g.Parts[p];
            var c = g.Combined;
            int n = list.Count, visible = 0;
            bool same = gp.InRtas && gp.Shown != null;
            for (int i = 0; i < n; i++)
            {
                bool v = list[i].VisibleSources > 0;
                if (v) visible++;
                if (same && gp.Shown[i] != v) same = false;
            }
            if (visible == 0)
            {
                if (gp.InRtas) { RemovePart(gp); _sumAddRemove++; }
                return true;
            }
            if (same) return true;

            var want = new bool[n];
            for (int i = 0; i < n; i++) want[i] = list[i].VisibleSources > 0;
            Mesh mesh;
            Material[] mats;
            RayTracingSubMeshFlags[] flags;

            if (c.FlagsMode)
            {
                mesh = c.Mesh;
                mats = c.SubMaterials;
                flags = new RayTracingSubMeshFlags[c.Mesh.subMeshCount]; // Disabled
                for (int i = 0; i < n; i++) if (want[i]) flags[list[i].Sub] = SubmeshOn(mats[list[i].Sub]);
            }
            else if (visible == n)
            {
                if (gp.FullStale)
                {
                    // A slot's material was swapped: the merged mesh groups submeshes by material.
                    if (budget <= 0) return false;
                    budget--;
                    try
                    {
                        var full = BuildGroupMesh(c, list, null, $"{c.Name} {g.Name}", out var fullMats);
                        if (gp.Full != null && gp.Full != c.Mesh) _retired.Add((gp.Full, Time.frameCount));
                        gp.Full = full;
                        gp.FullMaterials = fullMats;
                        gp.FullRecords = fullMats.Length;
                    }
                    catch (Exception e) { _log.LogWarning($"Group rebuild after a material swap failed for '{c.Name}' / '{g.Name}': {e.Message}"); }
                    gp.FullStale = false;
                }
                mesh = gp.Full;
                mats = gp.FullMaterials;
                flags = AllOn(mats);
            }
            else if (gp.Partial != null && SameSet(gp.PartialSet, want))
            {
                mesh = gp.Partial;
                mats = gp.PartialMaterials;
                flags = AllOn(mats);
                _sumReused++;
            }
            else if (gp.Partial2 != null && SameSet(gp.PartialSet2, want))
            {
                // Swap, so the most recent one stays first.
                (gp.Partial, gp.Partial2) = (gp.Partial2, gp.Partial);
                (gp.PartialMaterials, gp.PartialMaterials2) = (gp.PartialMaterials2, gp.PartialMaterials);
                (gp.PartialSet, gp.PartialSet2) = (gp.PartialSet2, gp.PartialSet);
                mesh = gp.Partial;
                mats = gp.PartialMaterials;
                flags = AllOn(mats);
                _sumReused++;
            }
            else
            {
                if (budget <= 0) return false;
                budget--;
                var sw = Stopwatch.StartNew();
                Mesh built = null;
                Material[] builtMats = null;
                try { built = BuildGroupMesh(c, list, want, $"{c.Name} {g.Name} partial", out builtMats); }
                catch (Exception e) { _log.LogWarning($"Partial rebuild of '{c.Name}' / '{g.Name}' failed, showing the whole group: {e.Message}"); }
                if (built == null)
                {
                    // Over-inclusive but safe: show the whole group rather than retrying every frame.
                    mesh = gp.Full;
                    mats = gp.FullMaterials;
                }
                else
                {
                    if (gp.Partial2 != null) _retired.Add((gp.Partial2, Time.frameCount));
                    gp.Partial2 = gp.Partial;
                    gp.PartialMaterials2 = gp.PartialMaterials;
                    gp.PartialSet2 = gp.PartialSet;
                    gp.Partial = mesh = built;
                    gp.PartialMaterials = mats = builtMats;
                    gp.PartialSet = want;
                    double ms = sw.Elapsed.TotalMilliseconds;
                    _sumPartial++;
                    _sumPartialMs += ms;
                    _maxPartialMs = Math.Max(_maxPartialMs, ms);
                    if (_loggedPartial++ < 12)
                        _log.LogInfo($"Partial group mesh '{c.Name}' / '{g.Name}'{PartName(p)}: {visible}/{n} submeshes, {mesh.vertexCount:N0} verts, {ms:F2} ms");
                }
                flags = AllOn(mats);
            }

            if (gp.InRtas) RemovePart(gp);
            else _sumAddRemove++;
            gp.Filter.sharedMesh = mesh;
            gp.Proxy.sharedMaterials = mats;
            Rtas.AddInstance(gp.Proxy, flags, enableTriangleCulling: (p & 1) == 0, frontTriangleCounterClockwise: FrontCounterClockwise,
                mask: gp.Transparent ? TransparentMask : (p & 2) != 0 ? SkyWallMask : GeometryMask, id: c.Id);
            gp.InRtas = true;
            if (gp.Transparent) CountTransparent(true);
            gp.Shown = want;
            gp.Records = flags.Count(f => f != RayTracingSubMeshFlags.Disabled);
            StaticRecordsActive += gp.Records;
            StaticInstancesActive++;
            return true;
        }

        private static bool SameSet(bool[] a, bool[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string PartName(int p) => ((p & 1) != 0 ? " two-sided" : "") + ((p & 2) != 0 ? " sky-wall" : "") + ((p & TransparentPart) != 0 ? " transparent" : "");

        private static RayTracingSubMeshFlags[] AllOn(Material[] mats)
        {
            var f = new RayTracingSubMeshFlags[mats.Length];
            for (int i = 0; i < mats.Length; i++) f[i] = SubmeshOn(mats[i]);
            return f;
        }

        private void RemovePart(GroupPart gp)
        {
            if (gp.Proxy != null) Rtas.RemoveInstance(gp.Proxy);
            if (gp.InRtas && gp.Transparent) CountTransparent(false);
            gp.InRtas = false;
            StaticRecordsActive -= gp.Records;
            StaticInstancesActive--;
            gp.Records = 0;
        }

        private void DisposeStatic()
        {
            foreach (var g in _groups)
                foreach (var gp in g.Parts)
                {
                    if (gp == null) continue;
                    if (gp.Full != null && !g.Combined.FlagsMode) Object.Destroy(gp.Full);
                    if (gp.Partial != null) Object.Destroy(gp.Partial);
                    if (gp.Partial2 != null) Object.Destroy(gp.Partial2);
                }
            foreach (var (mesh, _) in _retired) if (mesh != null) Object.Destroy(mesh);
            _retired.Clear();
            _groups.Clear();
            _combined.Clear();
            _staticSources.Clear();
            _watchers.Clear();
            _watcherByObject.Clear();
            _dirtyGroups.Clear();
            _toggles.Clear();
            _checkpoints.Clear();
            _pendingRoots.Clear();
            _foldedSlots.Clear();
            StaticRecordsActive = StaticInstancesActive = _staticRecordsTotal = _staticPartsTotal = 0;
        }
    }
}
