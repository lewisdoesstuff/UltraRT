using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// The materials StaticSceneOptimizer's baked level geometry had before it was baked. SSO.SetupMeshes (in Start) swaps
    /// every source renderer to one shared atlas material ('Hidden/ULTRAKILL/ULTRAKILL-Stationary') that has no surface
    /// type or reflection settings, so classification needs the originals:
    ///  1. A Harmony prefix on StaticSceneOptimizer.SetupMeshes (Patches.cs) records staticMRends[i].sharedMaterials and
    ///     the per-submesh index counts of the original mesh, before the swap.
    ///  2. Fallback (no hook, or a renderer the prefix didn't see): the footstep physics clone that SceneHelper makes in
    ///     sceneLoaded (before Start) keeps the original materials; the source's PhysicsSceneStateEnforcer points to it.
    /// SSO collapses a source's submeshes into one slot, so one material has to represent it: the one with the most
    /// indices in the original mesh (the "dominant" material).
    /// </summary>
    internal static class OriginalMaterials
    {
        private struct Entry
        {
            public Material[] Materials;
            public int[] IndexCounts; // per original submesh; null if unknown
        }

        private static readonly Dictionary<MeshRenderer, Entry> Captured = new Dictionary<MeshRenderer, Entry>();
        private static readonly FieldInfo MatchingObject =
            typeof(PhysicsSceneStateEnforcer).GetField("matchingObject", BindingFlags.Instance | BindingFlags.NonPublic);

        public static int CapturedCount => Captured.Count;

        public static bool IsCaptured(MeshRenderer r) => r != null && Captured.ContainsKey(r);

        /// <summary>Harmony prefix body: called with SSO's renderer list before it swaps meshes and materials.</summary>
        public static void Capture(List<MeshRenderer> renderers)
        {
            if (renderers == null) return;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                Captured[r] = new Entry { Materials = r.sharedMaterials, IndexCounts = IndexCounts(mf != null ? mf.sharedMesh : null) };
            }
        }

        /// <summary>Drop entries of destroyed renderers (previous scenes).</summary>
        public static void Prune()
        {
            var dead = new List<MeshRenderer>();
            foreach (var k in Captured.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) Captured.Remove(k);
        }

        private static int[] IndexCounts(Mesh mesh)
        {
            if (mesh == null) return null;
            try
            {
                var c = new int[mesh.subMeshCount];
                for (int i = 0; i < c.Length; i++) c[i] = (int)mesh.GetIndexCount(i);
                return c;
            }
            catch { return null; }
        }

        /// <summary>
        /// The original materials of a baked renderer and, when known, each one's index count. source: "hook" or "footstep".
        /// </summary>
        public static bool TryGet(MeshRenderer r, out Material[] materials, out int[] indexCounts, out string source)
        {
            materials = null;
            indexCounts = null;
            source = null;
            if (r == null) return false;
            if (Captured.TryGetValue(r, out var e))
            {
                materials = e.Materials;
                indexCounts = e.IndexCounts;
                source = "hook";
                return materials != null && materials.Length > 0;
            }

            // Fallback: the footstep physics clone (made before SSO ran) carries the original materials and mesh.
            try
            {
                var enforcer = r.GetComponent<PhysicsSceneStateEnforcer>();
                var clone = enforcer != null && MatchingObject != null ? MatchingObject.GetValue(enforcer) as GameObject : null;
                var cr = clone != null ? clone.GetComponent<MeshRenderer>() : null;
                if (cr == null) return false;
                materials = cr.sharedMaterials;
                Mesh mesh = null;
                if (clone.TryGetComponent<MeshCollider>(out var mc)) mesh = mc.sharedMesh;
                if (mesh == null && clone.TryGetComponent<MeshFilter>(out var mf)) mesh = mf.sharedMesh;
                indexCounts = IndexCounts(mesh);
                source = "footstep";
                return materials != null && materials.Length > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The material covering the most of the original mesh among those passing <paramref name="usable"/>.</summary>
        public static Material Dominant(Material[] materials, int[] indexCounts, Func<Material, bool> usable)
        {
            Material best = null;
            long bestCount = -1;
            for (int i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                if (m == null || !usable(m)) continue;
                long n = indexCounts != null && i < indexCounts.Length ? indexCounts[i] : 0;
                if (n > bestCount) { best = m; bestCount = n; }
            }
            return best;
        }
    }
}
