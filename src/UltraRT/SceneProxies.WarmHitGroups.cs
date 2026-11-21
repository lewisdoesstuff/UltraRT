using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// One hidden instance per hit-group shader in the RTAS from the scene build on (docs/performance.md, "Combat stalls").
    ///
    /// Unity builds each ray tracing shader's pipeline state object (PathTrace, Viewmodel, the particle light, the cutout
    /// pass) from the hit groups of the shaders in the RTAS. When an instance with a shader the RTAS has not had yet comes in
    /// (UltraRT/EffectProxy: the first combat sprite or fire billboard; UltraRT/LavaProxy: lava switched on later), those
    /// pipelines are rebuilt on the render thread before their next DispatchRays: a 250 ms frame two frames after 8-3's first
    /// effect proxy, once per process and ray tracing shader. With an instance of each from the start the set never changes.
    ///
    /// The instances are a 1 mm quad at the origin with instance mask 0, so no ray ever tests them; each adds one hit record
    /// to the shader table. Bench plugin._warmHitGroups=0 = off.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static bool WarmHitGroups = true;
        /// <summary>Hit-group shaders to warm (set by the plugin: Proxy, EffectProxy, LavaProxy, Portal).</summary>
        public static readonly List<Shader> WarmHitShaders = new List<Shader>();
        private Mesh _warmQuad;
        private readonly List<Material> _warmMaterials = new List<Material>();
        public int WarmHitGroupCount { get; private set; }

        private void AddWarmHitGroups()
        {
            if (!WarmHitGroups || Rtas == null) return;
            var names = new List<string>();
            foreach (var sh in WarmHitShaders)
            {
                if (sh == null || names.Contains(sh.name)) continue;
                if (_warmQuad == null)
                {
                    _warmQuad = new Mesh { name = "UltraRT warm hit group quad" };
                    _warmQuad.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1e-3f, 0f, 0f), new Vector3(1e-3f, 1e-3f, 0f), new Vector3(0f, 1e-3f, 0f) };
                    _warmQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    _warmQuad.RecalculateNormals();
                    _warmQuad.RecalculateBounds();
                    _warmQuad.UploadMeshData(false);
                }
                var mat = new Material(sh) { name = $"UltraRT warm {sh.name}" };
                _warmMaterials.Add(mat);
                var mr = CreateMeshProxy($"UltraRT warm {sh.name} [UltraRT]", null, _warmQuad, new[] { mat });
                Rtas.AddInstance(mr, new[] { RayTracingSubMeshFlags.Enabled }, enableTriangleCulling: true,
                    frontTriangleCounterClockwise: false, mask: 0u, id: 0);
                names.Add(sh.name);
            }
            WarmHitGroupCount = names.Count;
            _log.LogInfo($"Warm hit groups: {names.Count} hidden instances (mask 0) so the RT pipelines never change mid-level: {string.Join(", ", names)}");
        }

        private void DisposeWarmHitGroups()
        {
            foreach (var m in _warmMaterials) if (m != null) Object.Destroy(m);
            _warmMaterials.Clear();
            if (_warmQuad != null) Object.Destroy(_warmQuad);
            _warmQuad = null;
            WarmHitGroupCount = 0;
        }
    }
}
