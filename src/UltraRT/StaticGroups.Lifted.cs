using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Static-batched water, blood and glass whose material moves its vertices (VERTEX_DISPLACEMENT; docs/scene-proxies.md,
    /// "Vertex displacement", and docs/reflections.md, "Displaced water and the temporal filter"). The game's vertex
    /// shader displaces in world space, so it moves a batched surface like any other; as a slot of a static group our
    /// traced copy stayed at the rest pose while the overlay (displaced since 2026-11-19) followed the game. The overlay's
    /// reflection then matched the trace only where the two surfaces happened to agree: 2-1's channel
    /// (`Water 3`, amplitude 1) showed the same patchwork of triangles as 6-2's pools did before their fix (trailer v5c,
    /// user 2026-11-20). Such slots are lifted out of the static groups: each becomes a small mesh of its own triangles
    /// (from the CPU copy of the combined mesh), traced as a dynamic transparent proxy of its source renderer, which
    /// gets the displaced copy every dynamic proxy with the keyword gets (SceneProxies.Displaced.cs).
    ///
    /// Opaque slots too since 2026-11-21 (<see cref="LiftStaticOpaque"/>): 3-1's mouth doors (`DoorMouth*/Frame`,
    /// `MouthBlend`, `Flesh3NoFogMouthSettings`) are static-batched flesh that the game swells over the teeth quad behind
    /// them; at the rest pose the traced frame left the quad's corners uncovered, beige triangles beside and above the
    /// teeth that the game never shows (user, playtest of 2026-11-21). Such a slot becomes an opaque dynamic proxy with
    /// its slot's proxy material, displaced like any other.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Lift displaced static-batched transparents out of the static groups. -ultrart-nostaticlift (at the scene
        /// build; bench -Extra): they keep the rest pose, as before 2026-11-20.</summary>
        public static bool LiftStaticDisplaced = true;
        /// <summary>Lift displaced static-batched opaque slots as well (flesh). -ultrart-nostaticlift-opaque (at the scene
        /// build; bench -Extra): they keep the rest pose, as before 2026-11-21.</summary>
        public static bool LiftStaticOpaque = true;

        private sealed class LiftedSlot
        {
            public Mesh Mesh;               // the slot's triangles, in the combined mesh's space (readable)
            public TransparentKind Kind;
            public bool Opaque;             // an opaque slot: traced with Material, not a transparent proxy
            public Material Material;       // opaque: the slot's proxy material (atlas / caustic variant included)
            public string Source;           // for the log
        }

        private readonly Dictionary<(Mesh combined, int sub), LiftedSlot> _lifted = new Dictionary<(Mesh, int), LiftedSlot>();
        private readonly HashSet<Renderer> _notedTransparent = new HashSet<Renderer>();
        public int LiftedSlots => _lifted.Count;
        private int _liftedProxies, _liftedFailed, _liftedOpaque;

        /// <summary>True if this static-batched renderer's material will be lifted (so it does not keep the rest pose).</summary>
        private bool WillLift(Renderer r, Material m) =>
            LiftStaticDisplaced && DisplacementBuild && HasDisplacement(m)
            && (TransparentClassifier.Of(m) != TransparentKind.None && TransparentCandidate(r, r.sharedMaterials) || LiftStaticOpaque && Traceable(m));

        /// <summary>After <see cref="TryReadCombined"/>: the displaced transparent slots leave the combined mesh's slots.</summary>
        private void LiftDisplacedTransparents(Combined c)
        {
            if (!LiftStaticDisplaced || !DisplacementBuild || c.Streams == null || c.Indices == null) return;
            for (int sub = 0; sub < c.Slots.Length; sub++)
            {
                var slot = c.Slots[sub];
                if (slot == null || slot.Source == null) continue;
                bool opaque = (slot.Part & TransparentPart) == 0;
                if (opaque && !LiftStaticOpaque) continue;
                var mats = slot.Source.sharedMaterials;
                int i = sub - slot.Source.subMeshStartIndex;
                var m = i >= 0 && i < mats.Length ? mats[i] : null;
                if (!HasDisplacement(m)) continue;
                Mesh mesh;
                try { mesh = ExtractSlotMesh(c, slot, $"{c.Name} sub {sub}"); }
                catch (Exception e)
                {
                    _liftedFailed++;
                    _log.LogWarning($"Static displaced transparent: '{GameControlPath(slot.Source.transform)}' submesh {sub} of '{c.Name}' not lifted (rest pose): {e.Message}");
                    continue;
                }
                c.Slots[sub] = null;
                c.SubMaterials[sub] = _disabledMaterial;
                _lifted[(c.Mesh, sub)] = new LiftedSlot { Mesh = mesh, Kind = slot.Kind, Opaque = opaque, Material = slot.Material, Source = GameControlPath(slot.Source.transform) };
            }
        }

        /// <summary>One slot's triangles as a mesh of their own: the vertex range VMin..VMax of every stream, indices rebased.</summary>
        private static Mesh ExtractSlotMesh(Combined c, Slot slot, string name)
        {
            int n = slot.VMax - slot.VMin + 1;
            if (n <= 0 || slot.IndexCount <= 0) throw new Exception("empty slot");
            var idx = new int[slot.IndexCount];
            int offset = slot.BaseVertex - slot.VMin;
            for (int k = 0; k < idx.Length; k++) idx[k] = (int)c.Indices[slot.IndexStart + k] + offset;
            var mesh = new Mesh { name = name + " [UltraRT lifted]", indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                mesh.SetVertexBufferParams(n, c.Attributes);
                for (int s = 0; s < c.Streams.Length; s++)
                {
                    int stride = c.Strides[s];
                    if (stride == 0) continue;
                    var bytes = new byte[n * stride];
                    Buffer.BlockCopy(c.Streams[s], slot.VMin * stride, bytes, 0, bytes.Length);
                    mesh.SetVertexBufferData(bytes, 0, 0, bytes.Length, s, quiet);
                }
                mesh.SetIndices(idx, MeshTopology.Triangles, 0, false);
                mesh.RecalculateBounds();
            }
            catch
            {
                Object.Destroy(mesh);
                throw;
            }
            return mesh; // stays readable: the displaced copy is made from it on the CPU (BuildDisplaced)
        }

        /// <summary>A dynamic transparent proxy per lifted slot of this source (at the build, or a checkpoint clone later).
        /// Returns the kinds added (empty: none).</summary>
        private List<TransparentKind> AddLiftedTransparents(MeshRenderer r, Combined c)
        {
            var kinds = new List<TransparentKind>();
            if (_lifted.Count == 0 || r == null || c == null) return kinds;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (TransparentOverlays.IsOverlay(mats[i])) continue;
                if (!_lifted.TryGetValue((c.Mesh, r.subMeshStartIndex + i), out var lf) || mats[i] == null) continue;
                if (lf.Opaque)
                {
                    // Flesh and other opaque surfaces: the slot's own proxy material, the source's flags and mask.
                    var opaqueProxy = CreateMeshProxy($"{r.name} (static, displaced) [UltraRT]", c.Root, lf.Mesh, new[] { lf.Material });
                    AddDynamic(r, opaqueProxy, new[] { SubmeshOn(mats[i]) }, new[] { mats[i] });
                    var oe = _dynamic[_dynamic.Count - 1];
                    _liftedProxies++;
                    _liftedOpaque++;
                    if (_liftedProxies <= 12)
                        _log.LogInfo($"Static displaced opaque: '{GameControlPath(r.transform)}' '{mats[i].name}' submesh {r.subMeshStartIndex + i} of '{c.Name}' " +
                                     $"lifted, {lf.Mesh.vertexCount} verts, {(oe.Disp != null ? "displaced" : "NOT displaced (rest pose)")}");
                    continue;
                }
                var kind = TransparentClassifier.Of(mats[i]);
                if (kind == TransparentKind.None) kind = lf.Kind;
                var flags = new[] { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly };
                var proxyMats = new[] { TransparentProxy(mats[i], kind) };
                var proxy = CreateMeshProxy($"{r.name} (static, displaced {kind}) [UltraRT]", c.Root, lf.Mesh, proxyMats);
                AddDynamic(r, proxy, flags, new[] { mats[i] }, TransparentMask, SourceTwoSided(mats[i]));
                var entry = _dynamic[_dynamic.Count - 1];
                entry.ShadowMask = GlassShadowBit(flags, proxyMats);
                _liftedProxies++;
                if (_liftedProxies <= 12)
                    _log.LogInfo($"Static displaced transparent: '{GameControlPath(r.transform)}' {kind} '{mats[i].name}' submesh {r.subMeshStartIndex + i} of '{c.Name}' " +
                                 $"lifted, {lf.Mesh.vertexCount} verts, {(entry.Disp != null ? "displaced" : "NOT displaced (rest pose)")}");
                kinds.Add(kind);
            }
            return kinds;
        }

        private void DisposeLifted()
        {
            foreach (var lf in _lifted.Values) if (lf.Mesh != null) Object.Destroy(lf.Mesh);
            _lifted.Clear();
            _notedTransparent.Clear();
            _liftedProxies = _liftedFailed = _liftedOpaque = 0;
        }
    }
}
