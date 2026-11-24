using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Read-only view of the traced geometry for <see cref="EmissiveLights"/>: which submeshes are in the RTAS, with which
    /// proxy material, in which space, and (for static batches) the CPU copy of the combined mesh. Nothing here changes
    /// the proxies.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>One traced submesh of one instance.</summary>
        public sealed class TracedSubmesh
        {
            /// <summary>The proxy material (UltraRT/Proxy), i.e. what the hit shader shades with.</summary>
            public Material Material;
            /// <summary>The mesh the instance traces: the combined mesh for static batches.</summary>
            public Mesh Mesh;
            public int SubMesh;
            /// <summary>Vertices are in this transform's local space; null = world space.</summary>
            public Transform Space;
            /// <summary>The game object the submesh was made from (emitter rules with "path"); null for static batches.</summary>
            public Transform Source;
            /// <summary>Identity of the instance (combined mesh or dynamic proxy). Submeshes with the same owner share Space.</summary>
            public object Owner;
            public bool Static, TwoSided, SkyWall;
            /// <summary>Is this submesh in the RTAS right now (room active, renderer enabled)?</summary>
            public Func<bool> Visible;
            /// <summary>Bumped by <see cref="SceneProxies.SyncDynamic"/> whenever a rigid dynamic proxy's transform changes
            /// (since SyncDynamic consumes <c>ProxyTransform.hasChanged</c>); null for static/other owners.</summary>
            public Func<int> TransformGen;
            /// <summary>Static batches in merge mode: the CPU copy of the combined mesh (raw streams, raw indices). Else null.</summary>
            public byte[][] Streams;
            public int[] Strides;
            public uint[] Indices;
        }

        /// <summary>Grows when proxies are added (rescans, spawned hierarchies); 0 after Dispose.</summary>
        public int DynamicProxyCount => _dynamic.Count;

        /// <summary>Every static-batch submesh whose proxy material passes <paramref name="filter"/>.</summary>
        public void CollectStaticSubmeshes(Func<Material, bool> filter, List<TracedSubmesh> output)
        {
            foreach (var c in _combined.Values)
            {
                if (c.Slots == null) continue;
                foreach (var slot in c.Slots)
                {
                    if (slot == null || (slot.Part & TransparentPart) != 0 || !filter(slot.Material)) continue;
                    var s = slot;
                    int gen = s.MatGen;
                    output.Add(new TracedSubmesh
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

        /// <summary>
        /// Submeshes of skinned proxies <c>[from, DynamicProxyCount)</c> whose proxy material passes
        /// <paramref name="filter"/>, in the mesh's bind pose (Space is null: the caller places them by the bones). For the
        /// enemy glow lights (Plugin.EnemyGlow.cs); they are not in the emissive triangle table. Returns the new cursor.
        /// </summary>
        public int CollectSkinnedSubmeshes(Func<Material, bool> filter, int from, List<TracedSubmesh> output, List<SkinnedMeshRenderer> sources)
        {
            for (int k = Math.Max(0, from); k < _dynamic.Count; k++)
            {
                var d = _dynamic[k];
                if (d.SkinnedProxy == null || d.SkinnedSource == null || d.Mask == TransparentMask || d.Mask == PlayerMask) continue;
                var mesh = d.SkinnedSource.sharedMesh;
                if (mesh == null) continue;
                var mats = d.SkinnedProxy.sharedMaterials;
                for (int i = 0; i < mats.Length && i < d.Flags.Length && i < mesh.subMeshCount; i++)
                {
                    if (d.Flags[i] == RayTracingSubMeshFlags.Disabled || !filter(mats[i])) continue;
                    var e = d;
                    output.Add(new TracedSubmesh
                    {
                        Material = mats[i],
                        Mesh = mesh,
                        SubMesh = i,
                        Space = null,
                        Source = d.SourceTransform,
                        Owner = d,
                        Static = false,
                        TwoSided = IsTwoSided(mats[i]),
                        Visible = () => e.Visible && e.Proxy != null,
                    });
                    sources.Add(d.SkinnedSource);
                }
            }
            return _dynamic.Count;
        }

        /// <summary>
        /// Submeshes of dynamic MeshRenderer proxies <c>[from, DynamicProxyCount)</c> whose proxy material passes
        /// <paramref name="filter"/>. Skinned proxies are skipped (their vertices only exist on the GPU, skinned).
        /// Returns the new cursor (pass it as <paramref name="from"/> next time to get only proxies added since).
        /// </summary>
        public int CollectDynamicSubmeshes(Func<Material, bool> filter, int from, List<TracedSubmesh> output)
        {
            for (int k = Math.Max(0, from); k < _dynamic.Count; k++)
            {
                var d = _dynamic[k];
                if (d.SkinnedProxy != null || d.Mask == TransparentMask || d.Mask == PlayerMask || !(d.Proxy is MeshRenderer mr) || mr == null) continue;
                var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var mats = mr.sharedMaterials;
                for (int i = 0; i < mats.Length && i < d.Flags.Length; i++)
                {
                    if (d.Flags[i] == RayTracingSubMeshFlags.Disabled || !filter(mats[i])) continue;
                    var e = d;
                    int gen = d.MatGen;
                    output.Add(new TracedSubmesh
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
                        TransformGen = () => e.TransformGen,
                    });
                }
            }
            return _dynamic.Count;
        }
    }
}
