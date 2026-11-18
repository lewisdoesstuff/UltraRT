using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace UltraRT
{
    /// <summary>
    /// The cutout pass's own acceleration structure (Plugin.Cutout.cs, CutoutTrace.raytrace; docs/path-tracing.md,
    /// "Cutout pass"). It holds only the alpha-tested submeshes of the instances in the main RTAS (grass cards, grates,
    /// leaf cards: a handful of instances per level), with the main RTAS's ids, culling and transforms. Two reasons: Unity
    /// builds the shader table of a DispatchRays from every instance of the structure it traces, and with the whole scene
    /// that cost 0.45-0.9 ms of render thread per frame (measured 2026-11-18 on 7-1, 8-3, 1-3); and the instance mask has
    /// no free bit (0x01-0x80 are all taken). The rays stop at the game's depth, so nothing else needs to occlude them.
    /// Each traced frame <see cref="UpdateCutouts"/> syncs it (add, remove, transforms) and tells whether a cutout is in
    /// the camera's view within <see cref="CutoutRange"/>: the pass runs only then.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>Cutout instances inside the camera's frustum within <see cref="CutoutRange"/> this frame (the scan
        /// counts up to one: the pass only needs to know there is one).</summary>
        public int CutoutsInView { get; private set; }
        /// <summary>Instances in <see cref="CutoutRtas"/>.</summary>
        public int CutoutInstances => _cutIn.Count;
        /// <summary>The alpha-tested submeshes of the main RTAS's instances; made on the first traced frame.</summary>
        public RayTracingAccelerationStructure CutoutRtas { get; private set; }
        /// <summary>Cutouts farther than this (m) don't start the pass: a blade there covers no pixel worth a ray.</summary>
        public static float CutoutRange = 150f;

        private sealed class CutEntry
        {
            public Mesh Mesh;   // re-added when the proxy's mesh changes (a group's partial mesh, a displaced copy)
            public object Key;  // the flags / shown-slots array it was added with
            public bool Dynamic; // transform updated every frame
            public bool Seen;
        }

        private readonly Plane[] _cutPlanes = new Plane[6];
        private readonly Dictionary<Renderer, Bounds> _cutBounds = new Dictionary<Renderer, Bounds>();
        private readonly Dictionary<Renderer, CutEntry> _cutIn = new Dictionary<Renderer, CutEntry>();
        private readonly List<Renderer> _cutGone = new List<Renderer>();
        private RayTracingAccelerationStructure _cutOwner; // the main RTAS CutoutRtas mirrors (a scene build makes a new one)
        private int _cutBoundsCursor;

        private static bool HasCutoutSubmesh(RayTracingSubMeshFlags[] flags, Material[] mats)
        {
            if (flags == null || mats == null) return false;
            for (int i = 0; i < flags.Length && i < mats.Length; i++)
                if (flags[i] != RayTracingSubMeshFlags.Disabled && SourceAlphaTested(mats[i])) return true;
            return false;
        }

        private bool CutoutInView(Renderer r, Vector3 cam, bool refresh)
        {
            if (r == null) return false;
            if (refresh || !_cutBounds.TryGetValue(r, out var b)) _cutBounds[r] = b = r.bounds;
            if ((b.ClosestPoint(cam) - cam).sqrMagnitude > CutoutRange * CutoutRange) return false;
            return GeometryUtility.TestPlanesAABB(_cutPlanes, b);
        }

        /// <summary>Puts the alpha-tested submeshes of r into CutoutRtas (or keeps them), marking it seen this frame.</summary>
        private void CutKeep(Renderer r, Mesh mesh, object key, RayTracingSubMeshFlags[] mainFlags, bool culling, bool ccw, uint id, bool dynamic)
        {
            if (r == null || mesh == null) return;
            if (_cutIn.TryGetValue(r, out var e))
            {
                if (ReferenceEquals(e.Mesh, mesh) && ReferenceEquals(e.Key, key))
                {
                    e.Seen = true;
                    if (dynamic) CutoutRtas.UpdateInstanceTransform(r);
                    return;
                }
                CutoutRtas.RemoveInstance(r);
                _cutIn.Remove(r);
            }
            var mats = r.sharedMaterials;
            var flags = new RayTracingSubMeshFlags[mats.Length];
            bool any = false;
            for (int i = 0; i < mats.Length; i++)
            {
                bool on = (mainFlags == null || (i < mainFlags.Length && mainFlags[i] != RayTracingSubMeshFlags.Disabled)) && ProxyAlphaTested(mats[i]);
                flags[i] = on ? RayTracingSubMeshFlags.Enabled : RayTracingSubMeshFlags.Disabled;
                any |= on;
            }
            if (!any) return;
            CutoutRtas.AddInstance(r, flags, enableTriangleCulling: culling, frontTriangleCounterClockwise: ccw, mask: GeometryMask, id: id);
            _cutIn[r] = new CutEntry { Mesh = mesh, Key = key, Dynamic = dynamic, Seen = true };
        }

        /// <summary>Per traced frame, before RR, while the pass is enabled. The caller builds CutoutRtas when it traces.</summary>
        public void UpdateCutouts(Camera camera)
        {
            CutoutsInView = 0;
            if (camera == null || Rtas == null) return;
            if (!ReferenceEquals(_cutOwner, Rtas)) DisposeCutoutRtas();
            _cutOwner = Rtas;
            if (CutoutRtas == null)
                CutoutRtas = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.RASSettings(
                    RayTracingAccelerationStructure.ManagementMode.Manual, RayTracingAccelerationStructure.RayTracingModeMask.Everything, ~0));
            foreach (var e in _cutIn.Values) e.Seen = false;

            GeometryUtility.CalculateFrustumPlanes(camera, _cutPlanes);
            var cam = camera.transform.position;
            // Moving cutouts (a held torch's leaves, debris) get their bounds re-read in turn: 32 per frame.
            int refreshFrom = _cutBoundsCursor, refreshTo = _cutBoundsCursor + 32, idx = 0;
            int n = 0;
            foreach (var d in _active)
            {
                // Skinned proxies stay out: their BLAS would be rebuilt a second time every frame.
                if (!d.Visible || !d.Cutout || d.Mask == TransparentMask || d.SkinnedProxy != null || d.Proxy == null) continue;
                var mf = d.Proxy.GetComponent<MeshFilter>();
                CutKeep(d.Proxy, mf != null ? mf.sharedMesh : null, d.Flags, d.Flags, !d.TwoSided, InstanceCcw(d), d.Id, true);
                bool refresh = idx >= refreshFrom && idx < refreshTo;
                idx++;
                if (n == 0 && CutoutInView(d.Proxy, cam, refresh)) n++;
            }
            _cutBoundsCursor = refreshTo >= idx ? 0 : refreshTo;
            foreach (var g in _groups)
            {
                for (int p = 0; p < PartCount; p++)
                {
                    var gp = g.Parts[p];
                    if (gp == null || !gp.InRtas || gp.Transparent || gp.Proxy == null) continue;
                    var mats = gp.FullMaterials;
                    if (!ReferenceEquals(gp.CutoutChecked, mats))
                    {
                        gp.CutoutChecked = mats;
                        gp.HasCutout = false;
                        if (mats != null) foreach (var m in mats) gp.HasCutout |= ProxyAlphaTested(m);
                    }
                    if (!gp.HasCutout) continue;
                    CutKeep(gp.Proxy, gp.Filter != null ? gp.Filter.sharedMesh : null, gp.Shown, null, (p & 1) == 0, FrontCounterClockwise, g.Combined.Id, false);
                    if (n == 0 && CutoutInView(gp.Proxy, cam, false)) n++;
                }
            }
            _cutGone.Clear();
            foreach (var kv in _cutIn) if (!kv.Value.Seen) _cutGone.Add(kv.Key);
            foreach (var r in _cutGone)
            {
                if (r != null) try { CutoutRtas.RemoveInstance(r); } catch (System.Exception) { /* destroyed with its scene */ }
                _cutIn.Remove(r);
            }
            if (_cutBounds.Count > 50000) _cutBounds.Clear(); // renderers of unloaded scenes
            CutoutsInView = _cutIn.Count > 0 ? n : 0;
        }

        private void DisposeCutoutRtas()
        {
            CutoutRtas?.Dispose();
            CutoutRtas = null;
            _cutIn.Clear();
            _cutBounds.Clear();
            _cutOwner = null;
        }
    }
}
