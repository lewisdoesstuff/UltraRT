using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Runtime material changes (docs/scene-proxies.md, "Runtime material changes"). Proxy materials copy their source's
    /// properties once, but game scripts change materials while playing:
    ///  - material swaps: <c>renderer.sharedMaterial = x</c> (FinalDoor's lit frames), <c>renderer.material(s)</c> (an
    ///    instance replaces the shared material), enrage/radiance variants. Dynamic proxies re-pick their materials; static
    ///    batch slots get the new material and their group mesh is rebuilt;
    ///  - MaterialPropertyBlocks: Door light colours, PulsateEmissiveMaterial, BloodFiller's _FillAmount. A proxy whose
    ///    source has a relevant block gets its own copy of the proxy material per slot, updated while the block changes;
    ///  - property changes on (instanced) source materials: re-copied in turn.
    /// All of it is polled in small slices per frame (no events exist for any of these), so the cost stays flat.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>-ultrart-nomatsync: no runtime material sync (the old behaviour, for A/B).</summary>
        public static bool MaterialSync = true;
        /// <summary>Lit Master materials with _UseAlbedoAsEmissive are lit surfaces, not emitters, as in game
        /// (SceneProxies.cs material setup; -ultrart-albedo-emission-glow: the old albedo emission).</summary>
        public static bool LitAlbedoEmission = true;

        private const int SyncEntriesPerFrame = 16;   // dynamic proxies checked for swaps / new property blocks
        private const int SyncMaterialsPerFrame = 4;  // source materials re-copied
        private const int HotPerFrame = 8;            // proxies with property blocks re-read
        private const int MaxHot = 256;
        private const float EmitterRefreshSeconds = 1f;

        private static readonly int IdColor = Shader.PropertyToID("_Color");
        private static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int IdEmissiveColor = Shader.PropertyToID("_EmissiveColor");
        private static readonly int IdEmissiveIntensity = Shader.PropertyToID("_EmissiveIntensity");
        private static readonly int IdFillAmount = Shader.PropertyToID("_FillAmount");
        private static readonly int IdMeshScale = Shader.PropertyToID("_MeshScale");
        private static readonly int IdBloodFill = Shader.PropertyToID("_BloodFill");
        private static readonly int IdBloodScale = Shader.PropertyToID("_BloodScale");

        private readonly List<Material> _syncMats = new List<Material>(8);
        private readonly MaterialPropertyBlock _syncBlock = new MaterialPropertyBlock();
        private readonly List<DynamicProxy> _hot = new List<DynamicProxy>();
        private readonly List<SyncedMaterial> _syncList = new List<SyncedMaterial>();
        private readonly List<Material> _ownMaterials = new List<Material>();
        private readonly List<TracedSubmesh> _materialChanges = new List<TracedSubmesh>();
        private int _syncCursor, _syncMatCursor, _hotCursor;

        /// <summary>Counters since the last scene-sync log line (ticks: Perf ticks per part).</summary>
        public int SyncSwaps, SyncStaticSwaps, SyncBlockUpdates, SyncPropertyUpdates, SyncHot;
        public long SyncTicks, SyncSwapTicks, SyncHotTicks, SyncCopyTicks;

        /// <summary>A source material and what its proxy was last given.</summary>
        private sealed class SyncedMaterial
        {
            public Material Src, Proxy;
            public bool HasColor, HasEmissive, HasMain;
            public Color Color, Emissive;
            public float Intensity;
            public Texture Main;
            public Vector2 Scale, Offset;
        }

        /// <summary>Per dynamic proxy: what the sync knows about its source.</summary>
        private sealed class SyncState
        {
            public Material[] SrcMats;      // the source's materials the proxy was made from (overlays excluded)
            public Material[] Shared;       // per slot: the shared proxy material (null: not traced)
            public bool[] BloodFill;        // per slot: BLOOD_FILLER source on a mesh with TEXCOORD2.xyz
            public Material[] Own;          // per slot: this proxy's own copy (property block values), or null
            public bool Hot;                // has a relevant property block: followed in turn
            public bool PerSlotBlocks;      // the source uses per-material blocks (else one renderer block)
            public int BlockCheckFrame = -1000;
            public bool HasUv2;             // mesh has a 3+ component TEXCOORD2 (BLOOD_FILLER)
            public float LastEmitterRefresh = -100f;
            public int Signature;           // hash of the block values last applied
            public ScrollingTexture ScrollTex; // the source's ScrollingTexture (SceneProxies.Scroll.cs), looked up once
            public bool ScrollLooked;
        }

        private void NoteProxyMaterial(Material src, Material proxy)
        {
            if (src.shader != null && src.shader.name.Contains("Stationary")) return; // atlas: never changes
            var e = new SyncedMaterial
            {
                Src = src, Proxy = proxy, HasColor = src.HasProperty(IdColor),
                HasEmissive = src.HasProperty(IdEmissiveColor) && src.HasProperty(IdEmissiveIntensity), HasMain = src.HasProperty(IdMainTex),
            };
            ReadMaterial(e, out e.Color, out e.Emissive, out e.Intensity, out e.Main, out e.Scale, out e.Offset);
            _syncList.Add(e);
        }

        private static void ReadMaterial(SyncedMaterial e, out Color color, out Color emissive, out float intensity, out Texture main, out Vector2 scale, out Vector2 offset)
        {
            var m = e.Src;
            color = e.HasColor ? m.GetColor(IdColor) : Color.white;
            emissive = e.HasEmissive ? m.GetColor(IdEmissiveColor) : Color.black;
            intensity = e.HasEmissive ? m.GetFloat(IdEmissiveIntensity) : 0f;
            main = e.HasMain ? m.GetTexture(IdMainTex) : null;
            scale = e.HasMain ? m.GetTextureScale(IdMainTex) : Vector2.one;
            offset = e.HasMain ? m.GetTextureOffset(IdMainTex) : Vector2.zero;
        }

        private static Color ColorOf(Material m, int id) => m.HasProperty(id) ? m.GetColor(id) : Color.white;

        /// <summary>Registers a normal (opaque) proxy for syncing: <paramref name="mats"/> are the source materials it was made from.</summary>
        private void TrackSync(DynamicProxy d, Material[] mats, Mesh mesh)
        {
            d.Sync = new SyncState
            {
                HasUv2 = mesh != null && mesh.HasVertexAttribute(VertexAttribute.TexCoord2) && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord2) >= 3,
            };
            SetSyncMaterials(d.Sync, mats);
        }

        private void SetSyncMaterials(SyncState sync, Material[] mats)
        {
            sync.SrcMats = mats;
            sync.Shared = new Material[mats.Length];
            sync.BloodFill = new bool[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                if (!Traceable(mats[i])) continue;
                sync.Shared[i] = Proxy(mats[i]);
                sync.BloodFill[i] = sync.HasUv2 && mats[i].IsKeywordEnabled("BLOOD_FILLER");
            }
        }

        /// <summary>
        /// Submeshes whose proxy material changed since the last call (swaps, emission set by property blocks), for
        /// EmissiveLights to (re)register. The entries it had before stop counting through the material generation.
        /// </summary>
        public void TakeMaterialChanges(List<TracedSubmesh> output)
        {
            output.AddRange(_materialChanges);
            _materialChanges.Clear();
        }

        /// <summary>Per frame from SyncDynamic, after the active loop.</summary>
        private void SyncMaterials()
        {
            if (!MaterialSync) return;
            long t0 = Perf.Now;

            // 1) A slice of the active proxies: material swaps, and whether a relevant property block appeared.
            int n = _active.Count;
            for (int k = 0, count = Math.Min(n, SyncEntriesPerFrame); k < count; k++)
            {
                _syncCursor = (_syncCursor + 1) % n;
                var d = _active[_syncCursor];
                if (d.Sync == null || d.Source == null || d.Proxy == null) continue;
                CheckSwap(d);
                if (!d.Sync.Hot && Time.frameCount - d.Sync.BlockCheckFrame > 120 && d.Source.HasPropertyBlock()) CheckBlockRelevant(d);
            }
            long t1 = Perf.Now;
            SyncSwapTicks += t1 - t0;

            // 2) Proxies with a relevant property block, in turn (all of them every frame while there are few).
            for (int k = 0, count = Math.Min(_hot.Count, HotPerFrame); k < count && _hot.Count > 0; k++)
            {
                _hotCursor = (_hotCursor + 1) % _hot.Count;
                var d = _hot[_hotCursor];
                if (d.Source == null || d.Proxy == null)
                {
                    d.Sync.Hot = false;
                    _hot[_hotCursor] = _hot[_hot.Count - 1];
                    _hot.RemoveAt(_hot.Count - 1);
                    continue;
                }
                if (d.Visible) ApplyBlocks(d);
            }
            SyncHot = _hot.Count;
            long t2 = Perf.Now;
            SyncHotTicks += t2 - t1;

            // 3) Source material properties, in turn (scripts changing colours on instanced materials).
            for (int k = 0, count = Math.Min(_syncList.Count, SyncMaterialsPerFrame); k < count && _syncList.Count > 0; k++)
            {
                _syncMatCursor = (_syncMatCursor + 1) % _syncList.Count;
                var e = _syncList[_syncMatCursor];
                if (e.Src == null || e.Proxy == null)
                {
                    _syncList[_syncMatCursor] = _syncList[_syncList.Count - 1];
                    _syncList.RemoveAt(_syncList.Count - 1);
                    continue;
                }
                ReadMaterial(e, out var color, out var emissive, out var intensity, out var main, out var scale, out var offset);
                if (color == e.Color && emissive == e.Emissive && intensity == e.Intensity && main == e.Main && scale == e.Scale && offset == e.Offset) continue;
                if (color != e.Color) e.Proxy.SetColor(IdColor, color);
                if (e.Proxy.GetFloat(IdEmissiveIntensity) > 0f && (emissive != e.Emissive || intensity != e.Intensity))
                {
                    e.Proxy.SetColor(IdEmissiveColor, emissive);
                    e.Proxy.SetFloat(IdEmissiveIntensity, Mathf.Max(intensity, 0.0001f));
                }
                if (main != e.Main && main != null && main.dimension == TextureDimension.Tex2D) e.Proxy.SetTexture(IdMainTex, main);
                if (scale != e.Scale) e.Proxy.SetTextureScale(IdMainTex, scale);
                if (offset != e.Offset) e.Proxy.SetTextureOffset(IdMainTex, offset);
                e.Color = color; e.Emissive = emissive; e.Intensity = intensity; e.Main = main; e.Scale = scale; e.Offset = offset;
                SyncPropertyUpdates++;
            }
            long t3 = Perf.Now;
            SyncCopyTicks += t3 - t2;
            SyncTicks += t3 - t0;
        }

        private static bool IsOverlayMat(Material m) => m != null && OverlayShader != null && m.shader == OverlayShader;

        /// <summary>The source's materials changed (not counting our transparent overlay): re-pick the proxy's.</summary>
        private void CheckSwap(DynamicProxy d)
        {
            var src = d.Sync.SrcMats;
            // Most renderers have one material: sharedMaterial needs no list (an appended overlay is slot 1).
            if (src.Length == 1 && ReferenceEquals(d.Source.sharedMaterial, src[0])) return;
            d.Source.GetSharedMaterials(_syncMats);
            int count = _syncMats.Count;
            while (count > 0 && IsOverlayMat(_syncMats[count - 1])) count--;
            bool same = count == src.Length;
            for (int i = 0; same && i < count; i++) same = ReferenceEquals(_syncMats[i], src[i]);
            if (same) return;

            var mats = new Material[count];
            for (int i = 0; i < count; i++) mats[i] = _syncMats[i];
            NoteEnemyMaterials(d.Source, mats); // enrage / radiance variants keep their enemy type's lit weight
            int subCount = d.Flags.Length;
            var flags = SubmeshFlags(mats, subCount);
            var proxyMats = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) proxyMats[i] = d.Mask == PlayerMask ? PlayerProxyOrDisabled(mats[i]) : ProxyOrDisabled(mats[i]);
            ReleaseOwn(d);
            d.Proxy.sharedMaterials = proxyMats;
            SetSyncMaterials(d.Sync, mats);
            d.Sync.Signature = 0;           // a followed block is re-applied to the new materials
            d.Sync.BlockCheckFrame = -1000; // look at its property block again
            bool flagsChanged = false;
            for (int i = 0; i < subCount; i++) flagsChanged |= flags[i] != d.Flags[i];
            d.Flags = flags;
            d.Cutout = HasCutoutSubmesh(flags, mats);
            if (flagsChanged && d.Visible)
            {
                // Submesh flags are fixed at AddInstance; material changes alone are picked up with the shader table.
                Rtas.RemoveInstance(d.Proxy);
                Rtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: InstanceCcw(d), mask: d.Mask | d.ShadowMask, id: d.Id);
            }
            d.MatGen++;
            NoteDynamicChange(d);
            SyncSwaps++;
        }

        private static bool BlockRelevant(MaterialPropertyBlock b) =>
            !b.isEmpty && (b.HasColor(IdColor) || b.HasVector(IdColor) || b.HasColor(IdEmissiveColor) || b.HasVector(IdEmissiveColor)
                           || b.HasFloat(IdEmissiveIntensity) || b.HasFloat(IdFillAmount) || b.HasVector(IdScrollOffset));

        private void CheckBlockRelevant(DynamicProxy d)
        {
            var sync = d.Sync;
            sync.BlockCheckFrame = Time.frameCount;
            d.Source.GetPropertyBlock(_syncBlock);
            bool relevant = BlockRelevant(_syncBlock);
            for (int i = 0; i < sync.SrcMats.Length; i++)
            {
                d.Source.GetPropertyBlock(_syncBlock, i);
                if (BlockRelevant(_syncBlock)) { relevant = true; sync.PerSlotBlocks = true; break; }
            }
            if (!relevant || _hot.Count >= MaxHot) return;
            sync.Hot = true;
            _hot.Add(d);
            ApplyBlocks(d);
        }

        /// <summary>Copies the relevant property-block values of each slot into the proxy's own material for that slot.</summary>
        private void ApplyBlocks(DynamicProxy d)
        {
            var sync = d.Sync;
            var src = sync.SrcMats;
            // Cheap check first: the block values (renderer block, or each slot's) hashed; nothing to do if unchanged.
            int sig = 17;
            if (!sync.PerSlotBlocks)
            {
                d.Source.GetPropertyBlock(_syncBlock);
                sig = BlockSignature(_syncBlock, sig);
            }
            else
                for (int i = 0; i < src.Length; i++)
                {
                    if (sync.Shared[i] == null) continue;
                    d.Source.GetPropertyBlock(_syncBlock, i);
                    sig = BlockSignature(_syncBlock, sig);
                }
            if (sig == sync.Signature) return;
            sync.Signature = sig;

            bool emissionChanged = false;
            Material[] proxyMats = null;
            if (!sync.PerSlotBlocks) d.Source.GetPropertyBlock(_syncBlock);
            for (int i = 0; i < src.Length; i++)
            {
                var shared = sync.Shared[i];
                if (shared == null) continue;
                if (sync.PerSlotBlocks) d.Source.GetPropertyBlock(_syncBlock, i);
                var b = _syncBlock;
                var sm = src[i];
                var cur = sync.Own != null && sync.Own[i] != null ? sync.Own[i] : shared;

                Color color = b.HasColor(IdColor) ? b.GetColor(IdColor) : b.HasVector(IdColor) ? (Color)b.GetVector(IdColor) : ColorOf(sm, IdColor);
                bool emissive = shared.GetFloat(IdEmissiveIntensity) > 0f;
                Color eColor = emissive ? (b.HasColor(IdEmissiveColor) ? b.GetColor(IdEmissiveColor) : b.HasVector(IdEmissiveColor) ? (Color)b.GetVector(IdEmissiveColor) : shared.GetColor(IdEmissiveColor)) : Color.black;
                float eIntensity = emissive ? (b.HasFloat(IdEmissiveIntensity) ? Mathf.Max(b.GetFloat(IdEmissiveIntensity), 0.0001f) : shared.GetFloat(IdEmissiveIntensity)) : 0f;
                float fill = sync.BloodFill[i] && b.HasFloat(IdFillAmount) ? b.GetFloat(IdFillAmount) : 0f;
                float scale = b.HasVector(IdMeshScale) ? b.GetVector(IdMeshScale).z : 1f;
                // ScrollingTexture's per-renderer offset (SceneProxies.Scroll.cs), only on proxies that scroll.
                bool scrolls = ProxyScrolls(shared);
                Vector4 scroll = scrolls ? (b.HasVector(IdScrollOffset) ? b.GetVector(IdScrollOffset) : shared.GetVector(IdScrollOffset)) : Vector4.zero;

                bool emissionDiffers = emissive && (cur.GetColor(IdEmissiveColor) != eColor || cur.GetFloat(IdEmissiveIntensity) != eIntensity);
                bool differs = emissionDiffers || cur.GetColor(IdColor) != color || cur.GetFloat(IdBloodFill) != fill || (fill > 0f && cur.GetFloat(IdBloodScale) != scale)
                               || (scrolls && cur.GetVector(IdScrollOffset) != scroll);
                if (!differs) continue;
                if (cur == shared)
                {
                    cur = new Material(shared) { name = shared.name + " (block)" };
                    _ownMaterials.Add(cur);
                    if (sync.Own == null) sync.Own = new Material[src.Length];
                    sync.Own[i] = cur;
                    if (proxyMats == null) proxyMats = d.Proxy.sharedMaterials;
                    if (i < proxyMats.Length) proxyMats[i] = cur;
                    if (IsTwoSided(shared)) _twoSidedProxies.Add(cur);
                }
                emissionChanged |= emissionDiffers;
                cur.SetColor(IdColor, color);
                if (emissive) { cur.SetColor(IdEmissiveColor, eColor); cur.SetFloat(IdEmissiveIntensity, eIntensity); }
                cur.SetFloat(IdBloodFill, fill);
                cur.SetFloat(IdBloodScale, scale);
                if (scrolls)
                {
                    cur.SetVector(IdScrollOffset, scroll);
                    cur.SetVector(IdScrollVelocity, ScrollVelocity(sync, d.Source, shared));
                }
            }
            if (proxyMats != null) d.Proxy.sharedMaterials = proxyMats;
            SyncBlockUpdates++;
            // Emitters sampled as lights: refresh their radiance, at most once a second (PulsateEmissiveMaterial changes
            // every frame; the hit shader follows it anyway).
            if ((emissionChanged || proxyMats != null) && Time.realtimeSinceStartup - sync.LastEmitterRefresh >= EmitterRefreshSeconds)
            {
                sync.LastEmitterRefresh = Time.realtimeSinceStartup;
                d.MatGen++;
                NoteDynamicChange(d);
            }
        }

        private static int BlockSignature(MaterialPropertyBlock b, int h)
        {
            unchecked
            {
                if (b.isEmpty) return h * 31;
                if (b.HasColor(IdColor) || b.HasVector(IdColor)) h = h * 31 + b.GetVector(IdColor).GetHashCode();
                if (b.HasColor(IdEmissiveColor) || b.HasVector(IdEmissiveColor)) h = h * 31 + b.GetVector(IdEmissiveColor).GetHashCode();
                if (b.HasFloat(IdEmissiveIntensity)) h = h * 31 + b.GetFloat(IdEmissiveIntensity).GetHashCode();
                if (b.HasFloat(IdFillAmount)) h = h * 31 + b.GetFloat(IdFillAmount).GetHashCode();
                if (b.HasVector(IdMeshScale)) h = h * 31 + b.GetVector(IdMeshScale).GetHashCode();
                if (b.HasVector(IdScrollOffset)) h = h * 31 + b.GetVector(IdScrollOffset).GetHashCode();
                return h == 0 ? 1 : h;
            }
        }

        // Explosion debris (docs/vfx.md, "Glowing debris"): proxies with their own materials flagged _HotDebris.
        private readonly List<DynamicProxy> _hotDebris = new List<DynamicProxy>();
        private static readonly int IdHotDebris = Shader.PropertyToID("_HotDebris");

        /// <summary>
        /// Gives the proxy of an explosion chunk its own copies of its materials, flagged to emit the debris glow
        /// (Proxy.shader, _UltraRTDebrisGlow). Own copies, because the chunks use ordinary level materials ('Wall') whose
        /// shared proxy also covers the level's walls. False while the renderer has no proxy yet (they are made a few
        /// frames after the spawn).
        /// </summary>
        public bool MarkHotDebris(Renderer src)
        {
            DynamicProxy d = null;
            for (int i = _dynamic.Count - 1, n = 0; i >= 0 && n < 1024; i--, n++) // new proxies are at the end
                if (ReferenceEquals(_dynamic[i].Source, src)) { d = _dynamic[i]; break; }
            if (d == null || d.Sync == null || d.Proxy == null || d.Sync.Shared == null) return false;
            var sync = d.Sync;
            var mats = d.Proxy.sharedMaterials;
            if (sync.Own == null) sync.Own = new Material[sync.Shared.Length];
            for (int i = 0; i < sync.Shared.Length; i++)
            {
                var shared = sync.Shared[i];
                if (shared == null) continue;
                var cur = sync.Own[i];
                if (cur == null)
                {
                    cur = new Material(shared) { name = shared.name + " (debris)" };
                    _ownMaterials.Add(cur);
                    sync.Own[i] = cur;
                    if (i < mats.Length) mats[i] = cur;
                    if (IsTwoSided(shared)) _twoSidedProxies.Add(cur);
                }
                cur.SetFloat(IdHotDebris, 1f);
            }
            d.Proxy.sharedMaterials = mats;
            _hotDebris.Add(d);
            return true;
        }

        /// <summary>Frees the materials of debris that is gone (chunks live 1-3 s).</summary>
        public void PruneHotDebris()
        {
            for (int i = _hotDebris.Count - 1; i >= 0; i--)
            {
                if (_hotDebris[i].Source != null) continue;
                ReleaseOwn(_hotDebris[i]);
                _hotDebris.RemoveAt(i);
            }
        }

        private void ReleaseOwn(DynamicProxy d)
        {
            var own = d.Sync?.Own;
            if (own == null) return;
            foreach (var m in own)
                if (m != null) { _ownMaterials.Remove(m); _retiredMaterials.Add((m, Time.frameCount)); }
            d.Sync.Own = null;
        }

        // Materials a proxy stopped using: destroyed a few frames later (the GPU may still use this frame's shader table).
        private readonly List<(Material m, int frame)> _retiredMaterials = new List<(Material, int)>();

        private void RetireMaterials(bool all)
        {
            for (int i = _retiredMaterials.Count - 1; i >= 0; i--)
                if (all || Time.frameCount - _retiredMaterials[i].frame > RetireFrames)
                {
                    if (_retiredMaterials[i].m != null) Object.Destroy(_retiredMaterials[i].m);
                    _retiredMaterials.RemoveAt(i);
                }
        }

        /// <summary>Queues the proxy's traced submeshes for EmissiveLights (MeshRenderer proxies only, as in the build).</summary>
        private void NoteDynamicChange(DynamicProxy d)
        {
            if (d.SkinnedProxy != null || d.Mask == TransparentMask || d.Mask == PlayerMask || !(d.Proxy is MeshRenderer mr)) return;
            var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) return;
            var mats = mr.sharedMaterials;
            int gen = d.MatGen;
            for (int i = 0; i < mats.Length && i < d.Flags.Length; i++)
            {
                if (d.Flags[i] == RayTracingSubMeshFlags.Disabled || mats[i] == null) continue;
                var e = d;
                _materialChanges.Add(new TracedSubmesh
                {
                    Material = mats[i],
                    Mesh = mesh,
                    SubMesh = i,
                    Space = mr.transform,
                    Source = d.SourceTransform,
                    Owner = d,
                    Static = false,
                    TwoSided = IsTwoSided(mats[i]),
                    SkyWall = IsSkyWall(mats[i]) || d.Mask == SkyWallMask,
                    Visible = () => e.Visible && e.Proxy != null && e.MatGen == gen,
                });
            }
        }

        private void DisposeSync()
        {
            foreach (var m in _ownMaterials) if (m != null) Object.Destroy(m);
            _ownMaterials.Clear();
            RetireMaterials(true);
            _hot.Clear();
            _syncList.Clear();
            _materialChanges.Clear();
        }

        // ---------------------------------------------------------------- static batches

        /// <summary>
        /// A static-batched source swapped a material (FinalDoor sets its frames' lit materials on the same renderers):
        /// its slots take the new proxy material and their group parts are rebuilt. Atlas (StaticSceneOptimizer) sources
        /// never get here: their material is the scene atlas.
        /// </summary>
        private void CheckStaticSwap(StaticSource s)
        {
            var r = s.Renderer;
            if (s.Mats == null || r == null) return;
            long t0 = Perf.Now;
            try { CheckStaticSwapCore(s, r); }
            finally { SyncStaticTicks += Perf.Now - t0; }
        }

        public long SyncStaticTicks;

        private void CheckStaticSwapCore(StaticSource s, MeshRenderer r)
        {
            // One material (most sources): no list. Multi-material sources only every 4th frame.
            if (s.Mats.Length == 1) { if (ReferenceEquals(r.sharedMaterial, s.Mats[0])) return; }
            else if ((s.SwapPhase++ & 3) != 0) return; // every 4th visit of this source
            r.GetSharedMaterials(_syncMats);
            int count = Math.Min(_syncMats.Count, s.Mats.Length);
            bool changed = false;
            for (int i = 0; i < count; i++) changed |= !ReferenceEquals(_syncMats[i], s.Mats[i]);
            if (!changed) return;
            for (int i = 0; i < count; i++)
            {
                var nm = _syncMats[i];
                if (ReferenceEquals(nm, s.Mats[i])) continue;
                s.Mats[i] = nm;
                var slot = s.SlotByMat[i];
                if (slot == null || !Traceable(nm)) continue; // a merged slot can't be switched off; keep the old material
                if ((slot.Part & TransparentPart) != 0) continue; // water/glass slots keep their transparent proxy
                var pm = Proxy(nm);
                if (IsAtlasProxy(pm) || slot.Material == pm) continue;
                slot.Material = pm;
                slot.BaseMaterial = pm;
                slot.MatGen++;
                var c = slot.Group?.Combined;
                if (c != null) c.SubMaterials[slot.Sub] = pm;
                var g = slot.Group;
                var gp = g?.Parts[slot.Part];
                if (gp != null)
                {
                    gp.FullStale = true;
                    if (gp.Partial != null) { _retired.Add((gp.Partial, Time.frameCount)); gp.Partial = null; gp.PartialSet = null; }
                    if (gp.Partial2 != null) { _retired.Add((gp.Partial2, Time.frameCount)); gp.Partial2 = null; gp.PartialSet2 = null; }
                    gp.Shown = null; // forces UpdatePart to rebuild and re-add
                    MarkGroupDirty(g);
                }
                if (c != null) NoteStaticChange(c, slot);
                SyncStaticSwaps++;
            }
        }

        private void NoteStaticChange(Combined c, Slot slot)
        {
            int gen = slot.MatGen;
            var s = slot;
            _materialChanges.Add(new TracedSubmesh
            {
                Material = s.Material,
                Mesh = c.Mesh,
                SubMesh = s.Sub,
                Space = c.Root,
                Owner = c,
                Static = true,
                TwoSided = (s.Part & 1) != 0,
                SkyWall = (s.Part & 2) != 0,
                Visible = () => s.VisibleSources > 0 && s.MatGen == gen,
                Streams = c.FlagsMode ? null : c.Streams,
                Strides = c.FlagsMode ? null : c.Strides,
                Indices = c.FlagsMode ? null : c.Indices,
            });
        }
    }
}
