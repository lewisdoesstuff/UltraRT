using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UltraRT
{
    /// <summary>
    /// C4: merge stationary non-batched dynamic <see cref="MeshRenderer"/>s into watcher-grouped static meshes at scene
    /// build, reducing Unity's per-DispatchRays shader-table hit record rebuilds (especially in 8-2 / 8-3, where hundreds
    /// of non-batched architectural/prop MeshRenderers sit idle in the RTAS).
    ///
    /// Safety:
    ///  - Only opaque, non-emissive, non-fixture, non-displaced, non-scrolling, non-cutout, non-transparent renderers with
    ///    no dynamic/animated components in their ancestor chain are eligible.
    ///  - Runtime safety net: if any merged renderer's <see cref="Transform.hasChanged"/> flips true after build (e.g. an
    ///    unrecognised scripted animation or moving room), it is immediately hidden from its merged group and split out into
    ///    a live <see cref="DynamicProxy"/> with motion vectors.
    ///  - Bench A/B switch: <c>plugin._mergeStaticDynamic=0</c> (applies on next scene load).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static bool MergeStaticDynamic = true;

        private const int SdMaxMeshVerts = 4096;
        private const int SdPollPerFrame = 32;
        private const int SdMaxPartialBuildsPerFrame = 4;

        private struct SdCandidate
        {
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public Material[] SourceMats;
            public Material[] ProxyMats;
            public bool TwoSided;
            public bool SkyWall;
            public uint LayerId;
        }

        private sealed class SdMeshData
        {
            public Vector3[] Pos;
            public Vector3[] Norm;
            public Vector2[] Uv0;
            public Color32[] Col;
            public int[][] SubIndices;
        }

        private sealed class SdSlot
        {
            public int Index;
            public Vector3[] Pos;
            public Vector3[] Norm;
            public Vector2[] Uv0;
            public Color32[] Col;
            public Material[] Mats;
            public int[][] Indices;
            public bool Visible;
            public bool SplitOut;
            public SdGroup Group;
        }

        private sealed class SdSource
        {
            public MeshRenderer Renderer;
            public Transform Transform;
            public GameObject Object;
            public Mesh Mesh;
            public Material[] SourceMats;
            public SdSlot Slot;
            public GameObject Watch;
            public bool Visible, Dead, SplitOut;
        }

        private sealed class SdWatcher
        {
            public GameObject Object;
            public bool Active;
            public readonly List<SdSource> Sources = new List<SdSource>();
        }

        private sealed class SdGroup
        {
            public string Name;
            public uint LayerId;
            public bool TwoSided, SkyWall;
            public readonly List<SdSlot> Slots = new List<SdSlot>();
            public MeshRenderer Proxy;
            public MeshFilter Filter;
            public Mesh Full, Partial;
            public Material[] FullMats, PartialMats;
            public bool[] PartialSet, Shown;
            public bool InRtas, Dirty;
            public int Records, FullRecords;
        }

        private readonly List<SdCandidate> _sdCandidates = new List<SdCandidate>();
        private readonly List<SdGroup> _sdGroups = new List<SdGroup>();
        private readonly List<SdSource> _sdSources = new List<SdSource>();
        private readonly List<SdWatcher> _sdWatchers = new List<SdWatcher>();
        private readonly Dictionary<GameObject, SdWatcher> _sdWatcherByGo = new Dictionary<GameObject, SdWatcher>();
        private readonly List<SdGroup> _sdDirtyGroups = new List<SdGroup>();
        private int _sdCursor, _sdSplitOuts, _sdRecordsActive, _sdInstancesActive;

        private static readonly string[] SdDynamicComponentSubstrings =
        {
            "Move", "Moving", "Rotate", "Spin", "Swing", "Pendulum", "Elevator", "Platform", "Piston", "Crush",
            "Destroy", "Destruct", "Break", "Door", "Gate", "Portal", "Bridge", "Fan", "Gear", "Wheel", "Saw",
            "Turret", "Mine", "Barrel", "Lever", "Button", "Switch", "Skull", "Torch", "Hook", "Flicker", "Fade",
            "Color", "Scroll", "Animate", "Track", "Follow", "Orbit", "Float", "Bob", "Shake", "Vibrate", "Sway",
            "Fall", "Drop", "Slide", "Train", "Tram", "Vehicle", "Car", "Boat", "Ship", "Boss", "Enemy", "Limb",
            "Gib", "Chunk", "Debris", "Altar", "Pedestal", "Terminal", "Shop", "Machine", "Trigger", "Activate",
            "Video", "Screen", "Hologram", "Pendul", "Conveyor", "Crane", "Lift", "Chain", "Rope", "Cloth", "Ragdoll",
            "Escher", "RoomMove", "Shuffle", "Shift", "Morph", "Teleport", "Warp"
        };

        private static readonly Dictionary<Type, bool> SdTypeDynamicCache = new Dictionary<Type, bool>();

        private static bool IsDynamicComponentType(Type t)
        {
            if (t == null) return false;
            if (SdTypeDynamicCache.TryGetValue(t, out bool dyn)) return dyn;
            string n = t.Name;
            dyn = false;
            for (int i = 0; i < SdDynamicComponentSubstrings.Length; i++)
            {
                if (n.IndexOf(SdDynamicComponentSubstrings[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dyn = true;
                    break;
                }
            }
            SdTypeDynamicCache[t] = dyn;
            return dyn;
        }

        private bool TryQueueStaticDynamic(MeshRenderer r, Mesh mesh, Material[] mats, bool player, bool portalLayer)
        {
            if (!MergeStaticDynamic || player || portalLayer) return false;
            if (mesh.vertexCount <= 0 || mesh.vertexCount > SdMaxMeshVerts || mesh.subMeshCount <= 0) return false;
            if (r.HasPropertyBlock()) return false;
            // Fixture check must run before skipping so FixtureBounds / OrbFixtures are still recorded identically.
            if (IsLightFixture(r) || IsGlowing(r)) return false;

            // Check ancestor chain for any dynamic/animated/interactive components.
            for (Transform t = r.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<Rigidbody>() != null || t.GetComponent<Animator>() != null || t.GetComponent<Animation>() != null)
                    return false;
                if (t.GetComponent<EnemyIdentifier>() != null || t.GetComponent<Door>() != null || t.GetComponent<Breakable>() != null ||
                    t.GetComponent<Glass>() != null || t.GetComponent<ItemIdentifier>() != null || t.GetComponent<CheckPoint>() != null)
                    return false;
                var mbs = t.GetComponents<MonoBehaviour>();
                for (int i = 0; i < mbs.Length; i++)
                {
                    var mb = mbs[i];
                    if (mb != null && IsDynamicComponentType(mb.GetType())) return false;
                }
            }

            // Check materials: only plain opaque non-emissive non-displaced non-scrolling non-cutout non-caustic2 materials.
            var proxyMats = new Material[mats.Length];
            bool anySub = false, twoSided = false, skyWall = false;
            int subCount = Math.Min(mats.Length, mesh.subMeshCount);
            for (int i = 0; i < subCount; i++)
            {
                var src = mats[i];
                if (!Traceable(src)) continue;
                if (SourceAlphaTested(src)) return false;
                if (src.IsKeywordEnabled("EMISSIVE") || src.IsKeywordEnabled("_EMISSION")) return false;
                if (src.IsKeywordEnabled("VERTEX_DISPLACEMENT") || src.IsKeywordEnabled("SCROLLING") ||
                    src.IsKeywordEnabled("BLOOD_FILLER") || src.IsKeywordEnabled("CUSTOM_COLORS") ||
                    src.IsKeywordEnabled("TRANSPARENCY")) return false;
                if (AtlasPartWanted != null)
                {
                    try { if (AtlasPartWanted(src)) return false; } catch { return false; }
                }
                if (mesh.GetTopology(i) != MeshTopology.Triangles) return false;
                var pm = Proxy(src);
                if (pm == null) continue;
                if (pm.HasProperty("_EmissiveIntensity") && pm.GetFloat("_EmissiveIntensity") > 0f) return false;
                if (pm.HasProperty("_Transmit") && pm.GetFloat("_Transmit") > 0f) return false;
                if (pm.HasProperty("_CausticMode") && pm.GetFloat("_CausticMode") > 1.5f) return false;
                if (pm.HasProperty("_HotDebris") && pm.GetFloat("_HotDebris") > 0.5f) return false;
                if (pm.HasProperty("_Scrolling") && pm.GetFloat("_Scrolling") > 0.5f) return false;
                if (pm.HasProperty("_CameraSkip") && pm.GetFloat("_CameraSkip") > 0.5f) return false;
                proxyMats[i] = pm;
                anySub = true;
                if (IsTwoSided(pm)) twoSided = true;
                if (IsSkyWall(pm)) skyWall = true;
            }
            if (!anySub) return false;

            _sdCandidates.Add(new SdCandidate
            {
                Renderer = r,
                Mesh = mesh,
                SourceMats = mats,
                ProxyMats = proxyMats,
                TwoSided = twoSided,
                SkyWall = skyWall,
                LayerId = (uint)OriginalLayer(r.gameObject.layer),
            });
            return true;
        }

        private void AddFallbackDynamic(in SdCandidate cand)
        {
            var r = cand.Renderer;
            if (r == null) return;
            var mats = cand.SourceMats;
            var mesh = cand.Mesh;
            var proxyMats = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) proxyMats[i] = ProxyOrDisabled(mats[i]);
            CausticVariants(r, proxyMats);
            var proxy = CreateMeshProxy(r.name + " [UltraRT]", r.transform, mesh, proxyMats);
            AddDynamic(r, proxy, SubmeshFlags(mats, mesh.subMeshCount), mats, null);
            TrackSync(_dynamic[_dynamic.Count - 1], mats, mesh);
        }

        private void BuildStaticDynamicGroups()
        {
            if (_sdCandidates.Count == 0) return;
            var sw = Stopwatch.StartNew();
            if (_toggles.Count == 0) CollectToggleTargets(Object.FindObjectsOfType<MonoBehaviour>(true));

            // Partition candidates by (watcher, layerId, twoSided, skyWall).
            var buckets = new Dictionary<(GameObject watch, uint layer, bool twoSided, bool skyWall), List<SdCandidate>>();
            for (int i = 0; i < _sdCandidates.Count; i++)
            {
                var c = _sdCandidates[i];
                if (c.Renderer == null) continue;
                var w = FindSdWatcher(c.Renderer.transform);
                var key = (w, c.LayerId, c.TwoSided, c.SkyWall);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<SdCandidate>(4);
                list.Add(c);
            }

            var meshCache = new Dictionary<Mesh, SdMeshData>();
            int mergedRenderers = 0, fallbackRenderers = 0, totalOrigRecords = 0, totalNewRecords = 0;

            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count < 2)
                {
                    foreach (var c in list) { AddFallbackDynamic(c); fallbackRenderers++; }
                    continue;
                }

                // Build slots for each candidate whose mesh can be read.
                var valid = new List<(SdCandidate cand, SdSlot slot)>(list.Count);
                foreach (var c in list)
                {
                    var md = GetOrReadSdMesh(c.Mesh, meshCache);
                    if (md == null) { AddFallbackDynamic(c); fallbackRenderers++; continue; }
                    var slot = BakeSdSlot(c, md);
                    if (slot == null) { AddFallbackDynamic(c); fallbackRenderers++; continue; }
                    valid.Add((c, slot));
                }

                if (valid.Count < 2)
                {
                    foreach (var v in valid) { AddFallbackDynamic(v.cand); fallbackRenderers++; }
                    continue;
                }

                var g = new SdGroup
                {
                    Name = kv.Key.watch != null ? kv.Key.watch.name : "world",
                    LayerId = kv.Key.layer,
                    TwoSided = kv.Key.twoSided,
                    SkyWall = kv.Key.skyWall,
                };
                for (int i = 0; i < valid.Count; i++)
                {
                    var (c, slot) = valid[i];
                    slot.Index = i;
                    slot.Group = g;
                    g.Slots.Add(slot);
                    totalOrigRecords += slot.Mats.Length;
                }

                g.Full = BuildSdMesh(g.Slots, null, $"SD {g.Name}", out g.FullMats);
                if (g.Full == null)
                {
                    foreach (var v in valid) { AddFallbackDynamic(v.cand); fallbackRenderers++; }
                    continue;
                }
                g.FullRecords = g.FullMats.Length;
                g.Proxy = CreateMeshProxy($"SD {g.Name} [UltraRT]", null, g.Full, g.FullMats);
                g.Filter = g.Proxy.GetComponent<MeshFilter>();
                _sdGroups.Add(g);
                totalNewRecords += g.FullRecords;

                for (int i = 0; i < valid.Count; i++)
                {
                    var (c, slot) = valid[i];
                    var src = new SdSource
                    {
                        Renderer = c.Renderer,
                        Transform = c.Renderer.transform,
                        Object = c.Renderer.gameObject,
                        Mesh = c.Mesh,
                        SourceMats = c.SourceMats,
                        Slot = slot,
                        Watch = kv.Key.watch,
                    };
                    src.Transform.hasChanged = false;
                    _sdSources.Add(src);
                    if (src.Watch != null)
                    {
                        if (!_sdWatcherByGo.TryGetValue(src.Watch, out var swa))
                        {
                            swa = new SdWatcher { Object = src.Watch, Active = src.Watch.activeInHierarchy };
                            _sdWatcherByGo[src.Watch] = swa;
                            _sdWatchers.Add(swa);
                        }
                        swa.Sources.Add(src);
                    }
                    EvaluateSdSource(src);
                    mergedRenderers++;
                }
                MarkSdGroupDirty(g);
            }

            _sdCandidates.Clear();
            SyncStaticDynamic(force: true);
            _log.LogInfo($"Static-dynamic merge (C4): merged {mergedRenderers} stationary renderers into {_sdGroups.Count} groups " +
                         $"({_sdInstancesActive} active instances, {_sdRecordsActive}/{totalNewRecords} records vs {totalOrigRecords} unmerged; " +
                         $"{fallbackRenderers} kept dynamic, {meshCache.Count} meshes read) in {sw.Elapsed.TotalMilliseconds:F0} ms");
        }

        private GameObject FindSdWatcher(Transform tr)
        {
            _chain.Clear();
            for (var t = tr; t != null; t = t.parent) _chain.Add(t);
            int n = _chain.Count;
            if (n == 0) return null;
            var d2 = _chain[Math.Max(0, n - 1 - WatchDepth)].gameObject;
            for (int i = 0; i < n - 1 - WatchDepth; i++)
            {
                var go = _chain[i].gameObject;
                if (_toggles.Contains(go) || !go.activeSelf) return go;
            }
            return d2;
        }

        private SdMeshData GetOrReadSdMesh(Mesh mesh, Dictionary<Mesh, SdMeshData> cache)
        {
            if (cache.TryGetValue(mesh, out var md)) return md;
            try { md = mesh.isReadable ? ReadSdReadable(mesh) : ReadSdGpu(mesh); }
            catch { md = null; }
            cache[mesh] = md;
            return md;
        }

        private static SdMeshData ReadSdReadable(Mesh mesh)
        {
            int vc = mesh.vertexCount;
            var pos = mesh.vertices;
            if (pos == null || pos.Length != vc) return null;
            var norm = mesh.normals;
            if (norm == null || norm.Length != vc) { norm = new Vector3[vc]; for (int i = 0; i < vc; i++) norm[i] = Vector3.up; }
            var uv0 = mesh.uv;
            if (uv0 == null || uv0.Length != vc) uv0 = new Vector2[vc];
            var col = mesh.colors32;
            if (col == null || col.Length != vc) { col = new Color32[vc]; var w = new Color32(255, 255, 255, 255); for (int i = 0; i < vc; i++) col[i] = w; }
            int sc = mesh.subMeshCount;
            var subs = new int[sc][];
            for (int s = 0; s < sc; s++) subs[s] = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : null;
            return new SdMeshData { Pos = pos, Norm = norm, Uv0 = uv0, Col = col, SubIndices = subs };
        }

        private static SdMeshData ReadSdGpu(Mesh mesh)
        {
            if (!mesh.HasVertexAttribute(VertexAttribute.Position)) return null;
            int vc = mesh.vertexCount;
            int vbCount = mesh.vertexBufferCount;
            int ps = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int po = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            var pf = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            int pd = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
            if (!SdDecodable(pf)) return null;

            bool hasN = mesh.HasVertexAttribute(VertexAttribute.Normal) && SdDecodable(mesh.GetVertexAttributeFormat(VertexAttribute.Normal));
            int ns = hasN ? mesh.GetVertexAttributeStream(VertexAttribute.Normal) : -1;
            int no = hasN ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : 0;
            var nf = hasN ? mesh.GetVertexAttributeFormat(VertexAttribute.Normal) : VertexAttributeFormat.Float32;
            int nd = hasN ? mesh.GetVertexAttributeDimension(VertexAttribute.Normal) : 0;

            bool hasU = mesh.HasVertexAttribute(VertexAttribute.TexCoord0) && SdDecodable(mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0));
            int us = hasU ? mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0) : -1;
            int uo = hasU ? mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0) : 0;
            var uf = hasU ? mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0) : VertexAttributeFormat.Float32;
            int ud = hasU ? mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) : 0;

            bool hasC = mesh.HasVertexAttribute(VertexAttribute.Color) && SdDecodable(mesh.GetVertexAttributeFormat(VertexAttribute.Color));
            int cs = hasC ? mesh.GetVertexAttributeStream(VertexAttribute.Color) : -1;
            int co = hasC ? mesh.GetVertexAttributeOffset(VertexAttribute.Color) : 0;
            var cf = hasC ? mesh.GetVertexAttributeFormat(VertexAttribute.Color) : VertexAttributeFormat.UNorm8;
            int cd = hasC ? mesh.GetVertexAttributeDimension(VertexAttribute.Color) : 0;

            var streams = new byte[vbCount][];
            var strides = new int[vbCount];
            foreach (int s in new[] { ps, ns, us, cs })
            {
                if (s < 0 || s >= vbCount || streams[s] != null) continue;
                int st = mesh.GetVertexBufferStride(s);
                strides[s] = st;
                if (st <= 0) return null;
                using (var vb = mesh.GetVertexBuffer(s))
                {
                    var words = new uint[(vc * st + 3) / 4];
                    vb.GetData(words, 0, 0, words.Length);
                    var b = new byte[vc * st];
                    Buffer.BlockCopy(words, 0, b, 0, b.Length);
                    streams[s] = b;
                }
            }

            uint[] idx;
            using (var ib = mesh.GetIndexBuffer())
            {
                if (mesh.indexFormat == IndexFormat.UInt32) { idx = new uint[ib.count]; ib.GetData(idx); }
                else
                {
                    var packed = new uint[(ib.count + 1) / 2];
                    ib.GetData(packed, 0, 0, packed.Length);
                    idx = new uint[ib.count];
                    for (int k = 0; k < ib.count; k++) idx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                }
            }

            var pos = new Vector3[vc];
            var norm = new Vector3[vc];
            var uv0 = new Vector2[vc];
            var col = new Color32[vc];
            var white = new Color32(255, 255, 255, 255);
            for (int v = 0; v < vc; v++)
            {
                int o = v * strides[ps] + po;
                pos[v] = new Vector3(SdDecode(streams[ps], o, pf, 0), pd > 1 ? SdDecode(streams[ps], o, pf, 1) : 0f, pd > 2 ? SdDecode(streams[ps], o, pf, 2) : 0f);
                if (hasN)
                {
                    int on = v * strides[ns] + no;
                    norm[v] = new Vector3(SdDecode(streams[ns], on, nf, 0), nd > 1 ? SdDecode(streams[ns], on, nf, 1) : 0f, nd > 2 ? SdDecode(streams[ns], on, nf, 2) : 0f);
                }
                else norm[v] = Vector3.up;
                if (hasU)
                {
                    int ou = v * strides[us] + uo;
                    uv0[v] = new Vector2(SdDecode(streams[us], ou, uf, 0), ud > 1 ? SdDecode(streams[us], ou, uf, 1) : 0f);
                }
                if (hasC)
                {
                    int oc = v * strides[cs] + co;
                    col[v] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(SdDecode(streams[cs], oc, cf, 0) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((cd > 1 ? SdDecode(streams[cs], oc, cf, 1) : 1f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((cd > 2 ? SdDecode(streams[cs], oc, cf, 2) : 1f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((cd > 3 ? SdDecode(streams[cs], oc, cf, 3) : 1f) * 255f), 0, 255));
                }
                else col[v] = white;
            }

            int sc = mesh.subMeshCount;
            var subs = new int[sc][];
            for (int s = 0; s < sc; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var d = mesh.GetSubMesh(s);
                int cnt = d.indexCount - d.indexCount % 3;
                if (cnt <= 0) continue;
                var arr = new int[cnt];
                for (int k = 0; k < cnt; k++)
                {
                    int vi = (int)idx[d.indexStart + k] + d.baseVertex;
                    if ((uint)vi >= (uint)vc) return null;
                    arr[k] = vi;
                }
                subs[s] = arr;
            }
            return new SdMeshData { Pos = pos, Norm = norm, Uv0 = uv0, Col = col, SubIndices = subs };
        }

        private static bool SdDecodable(VertexAttributeFormat f) =>
            f == VertexAttributeFormat.Float32 || f == VertexAttributeFormat.Float16 || f == VertexAttributeFormat.UNorm8 ||
            f == VertexAttributeFormat.SNorm8 || f == VertexAttributeFormat.UNorm16 || f == VertexAttributeFormat.SNorm16;

        private static float SdDecode(byte[] b, int off, VertexAttributeFormat f, int i)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, off + i * 4);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, off + i * 2));
                case VertexAttributeFormat.UNorm8: return b[off + i] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)b[off + i] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, off + i * 2) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(b, off + i * 2) / 32767f, -1f);
                default: return 0f;
            }
        }

        private static SdSlot BakeSdSlot(in SdCandidate c, SdMeshData md)
        {
            var m = c.Renderer.transform.localToWorldMatrix;
            float det = m.determinant;
            if (Mathf.Abs(det) < 1e-10f) return null;
            bool flip = det < 0f;
            var nMat = m.inverse.transpose;

            var mats = new List<Material>(2);
            var idxs = new List<int[]>(2);
            int subCount = Math.Min(c.ProxyMats.Length, md.SubIndices.Length);
            for (int s = 0; s < subCount; s++)
            {
                var pm = c.ProxyMats[s];
                var si = md.SubIndices[s];
                if (pm == null || si == null || si.Length < 3) continue;
                int[] copy = new int[si.Length];
                if (flip)
                {
                    for (int k = 0; k + 2 < si.Length; k += 3) { copy[k] = si[k]; copy[k + 1] = si[k + 2]; copy[k + 2] = si[k + 1]; }
                }
                else Array.Copy(si, copy, si.Length);
                mats.Add(pm);
                idxs.Add(copy);
            }
            if (mats.Count == 0) return null;

            int vc = md.Pos.Length;
            var pos = new Vector3[vc];
            var norm = new Vector3[vc];
            for (int v = 0; v < vc; v++)
            {
                pos[v] = m.MultiplyPoint3x4(md.Pos[v]);
                var n = nMat.MultiplyVector(md.Norm[v]);
                norm[v] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            }
            return new SdSlot
            {
                Pos = pos,
                Norm = norm,
                Uv0 = md.Uv0,
                Col = md.Col,
                Mats = mats.ToArray(),
                Indices = idxs.ToArray(),
            };
        }

        private static Mesh BuildSdMesh(List<SdSlot> slots, bool[] include, string name, out Material[] materials)
        {
            int totalVerts = 0;
            var order = new List<Material>(4);
            var matIdxCounts = new List<int>(4);
            for (int i = 0; i < slots.Count; i++)
            {
                if (include != null && !include[i]) continue;
                var s = slots[i];
                totalVerts += s.Pos.Length;
                for (int k = 0; k < s.Mats.Length; k++)
                {
                    int mi = order.IndexOf(s.Mats[k]);
                    if (mi < 0) { mi = order.Count; order.Add(s.Mats[k]); matIdxCounts.Add(0); }
                    matIdxCounts[mi] += s.Indices[k].Length;
                }
            }
            if (totalVerts == 0 || order.Count == 0) { materials = null; return null; }

            var pos = new Vector3[totalVerts];
            var norm = new Vector3[totalVerts];
            var uv0 = new Vector2[totalVerts];
            var col = new Color32[totalVerts];
            var subArrs = new int[order.Count][];
            var subCurs = new int[order.Count];
            for (int m = 0; m < order.Count; m++) subArrs[m] = new int[matIdxCounts[m]];

            int vOff = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (include != null && !include[i]) continue;
                var s = slots[i];
                int n = s.Pos.Length;
                Array.Copy(s.Pos, 0, pos, vOff, n);
                Array.Copy(s.Norm, 0, norm, vOff, n);
                Array.Copy(s.Uv0, 0, uv0, vOff, n);
                Array.Copy(s.Col, 0, col, vOff, n);
                for (int k = 0; k < s.Mats.Length; k++)
                {
                    int mi = order.IndexOf(s.Mats[k]);
                    var srcIdx = s.Indices[k];
                    var dstIdx = subArrs[mi];
                    int cur = subCurs[mi];
                    for (int j = 0; j < srcIdx.Length; j++) dstIdx[cur + j] = srcIdx[j] + vOff;
                    subCurs[mi] = cur + srcIdx.Length;
                }
                vOff += n;
            }

            var mesh = new Mesh { name = name + " [UltraRT SD]", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = pos;
            mesh.normals = norm;
            mesh.uv = uv0;
            mesh.colors32 = col;
            mesh.subMeshCount = order.Count;
            for (int m = 0; m < order.Count; m++) mesh.SetTriangles(subArrs[m], m, calculateBounds: false);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            materials = order.ToArray();
            return mesh;
        }

        private void EvaluateSdSource(SdSource src, bool? known = null)
        {
            if (src.Dead || src.SplitOut) return;
            bool visible;
            if (known.HasValue) visible = known.Value;
            else
            {
                var r = src.Renderer;
                if (r == null || src.Object == null)
                {
                    src.Dead = true;
                    visible = false;
                }
                else
                {
                    if (src.Transform.hasChanged)
                    {
                        SplitOutSdSource(src);
                        return;
                    }
                    visible = r.enabled && src.Object.activeInHierarchy;
                }
            }
            if (visible == src.Visible) return;
            src.Visible = visible;
            src.Slot.Visible = visible;
            MarkSdGroupDirty(src.Slot.Group);
        }

        private void SplitOutSdSource(SdSource src)
        {
            src.SplitOut = true;
            src.Slot.SplitOut = true;
            if (src.Visible)
            {
                src.Visible = false;
                src.Slot.Visible = false;
                MarkSdGroupDirty(src.Slot.Group);
            }
            if (src.Renderer != null && src.Mesh != null)
            {
                var mats = src.SourceMats;
                var proxyMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++) proxyMats[i] = ProxyOrDisabled(mats[i]);
                CausticVariants(src.Renderer, proxyMats);
                var proxy = CreateMeshProxy(src.Renderer.name + " [UltraRT]", src.Renderer.transform, src.Mesh, proxyMats);
                AddDynamic(src.Renderer, proxy, SubmeshFlags(mats, src.Mesh.subMeshCount), mats, null);
                TrackSync(_dynamic[_dynamic.Count - 1], mats, src.Mesh);
                if (_sdSplitOuts++ < 12)
                    _log.LogInfo($"Static-dynamic split-out (moved): '{GameControlPath(src.Transform)}' promoted to live DynamicProxy");
            }
        }

        private void MarkSdGroupDirty(SdGroup g)
        {
            if (g == null || g.Dirty) return;
            g.Dirty = true;
            _sdDirtyGroups.Add(g);
        }

        private void SyncStaticDynamic(bool force)
        {
            if (_sdGroups.Count == 0) return;

            for (int i = _sdWatchers.Count - 1; i >= 0; i--)
            {
                var w = _sdWatchers[i];
                var go = w.Object;
                if (go == null)
                {
                    foreach (var s in w.Sources) EvaluateSdSource(s);
                    _sdWatcherByGo.Remove(go);
                    _sdWatchers[i] = _sdWatchers[_sdWatchers.Count - 1];
                    _sdWatchers.RemoveAt(_sdWatchers.Count - 1);
                    continue;
                }
                bool active = go.activeInHierarchy;
                if (active == w.Active) continue;
                w.Active = active;
                foreach (var s in w.Sources) EvaluateSdSource(s, active ? (bool?)null : false);
            }

            int n = _sdSources.Count;
            if (n > 0)
            {
                for (int k = 0, count = Math.Min(n, force ? n : SdPollPerFrame); k < count; k++)
                {
                    _sdCursor = (_sdCursor + 1) % n;
                    EvaluateSdSource(_sdSources[_sdCursor]);
                }
            }

            if (_sdDirtyGroups.Count > 0)
            {
                int budget = force ? int.MaxValue : SdMaxPartialBuildsPerFrame;
                int keep = 0;
                for (int i = 0; i < _sdDirtyGroups.Count; i++)
                {
                    var g = _sdDirtyGroups[i];
                    if (UpdateSdGroup(g, ref budget)) g.Dirty = false;
                    else _sdDirtyGroups[keep++] = g;
                }
                _sdDirtyGroups.RemoveRange(keep, _sdDirtyGroups.Count - keep);
            }
        }

        private bool UpdateSdGroup(SdGroup g, ref int budget)
        {
            int n = g.Slots.Count, visible = 0;
            bool same = g.InRtas && g.Shown != null;
            for (int i = 0; i < n; i++)
            {
                bool v = g.Slots[i].Visible && !g.Slots[i].SplitOut;
                if (v) visible++;
                if (same && g.Shown[i] != v) same = false;
            }
            if (visible == 0)
            {
                if (g.InRtas) RemoveSdGroup(g);
                return true;
            }
            if (same) return true;

            var want = new bool[n];
            for (int i = 0; i < n; i++) want[i] = g.Slots[i].Visible && !g.Slots[i].SplitOut;

            Mesh mesh;
            Material[] mats;
            if (visible == n)
            {
                mesh = g.Full;
                mats = g.FullMats;
            }
            else if (g.Partial != null && SameSet(g.PartialSet, want))
            {
                mesh = g.Partial;
                mats = g.PartialMats;
            }
            else
            {
                if (budget <= 0) return false;
                budget--;
                var built = BuildSdMesh(g.Slots, want, $"SD {g.Name} partial", out var builtMats);
                if (built == null) { mesh = g.Full; mats = g.FullMats; }
                else
                {
                    if (g.Partial != null) _retired.Add((g.Partial, Time.frameCount));
                    g.Partial = mesh = built;
                    g.PartialMats = mats = builtMats;
                    g.PartialSet = want;
                }
            }

            if (g.InRtas) RemoveSdGroup(g);
            g.Filter.sharedMesh = mesh;
            g.Proxy.sharedMaterials = mats;
            var flags = AllOn(mats);
            Rtas.AddInstance(g.Proxy, flags, enableTriangleCulling: !g.TwoSided, frontTriangleCounterClockwise: FrontCounterClockwise,
                mask: g.SkyWall ? SkyWallMask : GeometryMask, id: g.LayerId);
            g.InRtas = true;
            g.Shown = want;
            g.Records = flags.Length;
            _sdRecordsActive += g.Records;
            _sdInstancesActive++;
            StaticRecordsActive += g.Records;
            StaticInstancesActive++;
            return true;
        }

        private void RemoveSdGroup(SdGroup g)
        {
            if (g.Proxy != null) Rtas.RemoveInstance(g.Proxy);
            g.InRtas = false;
            _sdRecordsActive -= g.Records;
            _sdInstancesActive--;
            StaticRecordsActive -= g.Records;
            StaticInstancesActive--;
            g.Records = 0;
        }

        private void DisposeStaticDynamic()
        {
            foreach (var g in _sdGroups)
            {
                if (g.Full != null) Object.Destroy(g.Full);
                if (g.Partial != null) Object.Destroy(g.Partial);
            }
            _sdCandidates.Clear();
            _sdGroups.Clear();
            _sdSources.Clear();
            _sdWatchers.Clear();
            _sdWatcherByGo.Clear();
            _sdDirtyGroups.Clear();
            _sdCursor = _sdSplitOuts = _sdRecordsActive = _sdInstancesActive = 0;
        }
    }
}
