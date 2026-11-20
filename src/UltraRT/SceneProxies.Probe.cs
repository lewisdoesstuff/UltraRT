using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// -ultrart-viewprobe: at every bench screenshot, log the renderers in front of the camera with their materials,
    /// the property blocks game scripts set on them, and what the RT scene made of them (docs/scene-proxies.md,
    /// "Runtime material changes"). For finding why a surface looks different with RT on.
    /// </summary>
    internal sealed partial class SceneProxies
    {
        public static bool ViewProbe;
        private static readonly MaterialPropertyBlock ProbeBlock = new MaterialPropertyBlock();

        public IEnumerable<string> DescribeView(Camera cam, float coneDeg = 30f, float maxDist = 40f, int max = 60)
        {
            if (cam == null) yield break;
            var bySource = new Dictionary<Renderer, DynamicProxy>();
            foreach (var d in _dynamic) if (d.Source != null && !bySource.ContainsKey(d.Source)) bySource[d.Source] = d;
            var pos = cam.transform.position;
            var fwd = cam.transform.forward;
            float cosCone = Mathf.Cos(coneDeg * Mathf.Deg2Rad);
            var hits = new List<(float d, Renderer r)>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                // Particles, sprites, lines and trails too (the game's glow sprites; 2026-11-19): listed with no proxy.
                if (!r.enabled || IsProxyObject(r.gameObject)) continue;
                var b = r.bounds;
                var c = b.ClosestPoint(pos);
                float dist = (c - pos).magnitude;
                // Far renderers only where the camera's centre ray passes through them (0-1's cog at 60 m, 2026-11-18).
                if (dist > maxDist && (dist > 4f * maxDist || !b.IntersectRay(new Ray(pos, fwd)))) continue;
                // In the cone: the bounds centre or the closest point, or the camera's forward ray passes through it.
                var toC = b.center - pos;
                bool inCone = dist < 0.01f || Vector3.Dot(toC.normalized, fwd) > cosCone || (dist > 0.01f && Vector3.Dot((c - pos) / dist, fwd) > cosCone)
                              || b.IntersectRay(new Ray(pos, fwd));
                if (inCone) hits.Add((dist, r));
            }
            foreach (var (dist, r) in hits.OrderBy(h => h.d).Take(max)) yield return DescribeRenderer(r, dist, bySource);
        }

        /// <summary>
        /// Bench cinematic "Probes" (bench/README.md): every renderer whose path contains <paramref name="filter"/>, enabled or
        /// not, with what decides whether the game draws it (enabled, active, isVisible = drawn by a camera last frame,
        /// forceRenderingOff, layer in the main camera's mask, in its frustum, bounds) and its materials and proxy.
        /// </summary>
        public IEnumerable<string> DescribeMatching(string filter, Camera cam, int max = 80)
        {
            var bySource = new Dictionary<Renderer, DynamicProxy>();
            foreach (var d in _dynamic) if (d.Source != null && !bySource.ContainsKey(d.Source)) bySource[d.Source] = d;
            var pos = cam != null ? cam.transform.position : Vector3.zero;
            var planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            int n = 0, inactive = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r == null || IsProxyObject(r.gameObject)) continue;
                if (GameControlPath(r.transform).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!r.gameObject.activeInHierarchy) { inactive++; continue; } // inactive ones only counted
                if (n++ >= max) break;
                var b = r.bounds;
                string head = $"enabled={r.enabled} active={r.gameObject.activeInHierarchy} isVisible={r.isVisible} forceOff={r.forceRenderingOff} " +
                              $"inCamMask={(cam != null && ((cam.cullingMask >> r.gameObject.layer) & 1) != 0)} inFrustum={(planes != null && GeometryUtility.TestPlanesAABB(planes, b))} " +
                              $"bounds=({b.center:F2} size {b.size:F2}) ";
                // The Master shader's per-material fog distances (its material cbuffer holds unity_FogStart / unity_FogEnd / _HeightFog).
                r.GetPropertyBlock(ProbeBlock);
                var m0 = r.sharedMaterial;
                string FogVal(string n) => (ProbeBlock.HasFloat(n) ? $"mpb {ProbeBlock.GetFloat(n):G4}" : m0 != null && m0.HasFloat(n) ? $"mat {m0.GetFloat(n):G4}" : "-") + $"/global {Shader.GetGlobalFloat(n):G4}";
                head += $"fog[start {FogVal("unity_FogStart")} end {FogVal("unity_FogEnd")} height {FogVal("_HeightFog")}; RenderSettings {RenderSettings.fog} {RenderSettings.fogMode} {RenderSettings.fogStartDistance:G4}..{RenderSettings.fogEndDistance:G4} {RenderSettings.fogColor}] ";
                if (r is SkinnedMeshRenderer s0)
                    head += $"localBounds=({s0.localBounds.center:F2} size {s0.localBounds.size:F2}) root='{(s0.rootBone != null ? s0.rootBone.name : "-")}' rootPos={(s0.rootBone != null ? s0.rootBone.position : r.transform.position):F2} rootScale={(s0.rootBone != null ? s0.rootBone.lossyScale : r.transform.lossyScale):F3} ";
                yield return head + DescribeRenderer(r, (b.ClosestPoint(pos) - pos).magnitude, bySource);
            }
            yield return $"{n} active renderer(s) listed{(n > max ? " (more not listed)" : "")}, {inactive} inactive ones matching '{filter}' not listed";
        }

        /// <summary>
        /// Bench cinematic "Probes": the skinned vertices the game drew this frame (the source renderer's GPU skin buffer)
        /// against our proxy's, as world-space boxes (the buffer is in root-bone space without scale), for every active
        /// skinned source matching <paramref name="filter"/>. Logged when the readbacks arrive.
        /// </summary>
        public void ProbeSkinnedExtents(string filter, System.Action<string> log)
        {
            foreach (var d in _dynamic)
            {
                if (d.SkinnedSource == null || d.SkinnedProxy == null || !d.SkinnedSource.gameObject.activeInHierarchy) continue;
                string path = GameControlPath(d.SkinnedSource.transform);
                if (path.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                var root = d.Root != null ? d.Root : d.SkinnedSource.transform;
                var rootM = Matrix4x4.TRS(root.position, root.rotation, Vector3.one);
                var mesh = d.SkinnedSource.sharedMesh;
                int posOffset = mesh != null ? Mathf.Max(0, mesh.GetVertexAttributeOffset(VertexAttribute.Position)) : 0;
                bool sameBones = d.SkinnedProxy.bones.Length == d.SkinnedSource.bones.Length && d.SkinnedProxy.bones.Zip(d.SkinnedSource.bones, (a, b) => a == b).All(x => x);
                string head = $"skin '{path}' enabled={d.SkinnedSource.enabled} mesh={mesh?.name}/{d.SkinnedProxy.sharedMesh?.name} sameBones={sameBones} quality={d.SkinnedSource.quality}/{d.SkinnedProxy.quality} " +
                              $"motionVectors={d.SkinnedSource.skinnedMotionVectors} root='{root.name}'";
                foreach (var (which, smr) in new[] { ("game", d.SkinnedSource), ("proxy", d.SkinnedProxy) })
                {
                    GraphicsBuffer vb = null;
                    try { vb = smr.GetVertexBuffer(); } catch (System.Exception e) { log($"{head} {which}: GetVertexBuffer: {e.Message}"); }
                    if (vb == null) { log($"{head} {which}: no skinned buffer this frame"); continue; }
                    int stride = vb.stride, count = vb.count;
                    var buf = vb;
                    UnityEngine.Rendering.AsyncGPUReadback.Request(vb, r =>
                    {
                        try
                        {
                            if (r.hasError) { log($"{head} {which}: readback failed"); return; }
                            var bytes = r.GetData<byte>();
                            int n = bytes.Length / stride;
                            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                            var max = -min;
                            var wmin = min; var wmax = max;
                            for (int i = 0; i < n; i++)
                            {
                                int o = i * stride + posOffset;
                                var p = new Vector3(System.BitConverter.ToSingle(new[] { bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3] }, 0),
                                                    System.BitConverter.ToSingle(new[] { bytes[o + 4], bytes[o + 5], bytes[o + 6], bytes[o + 7] }, 0),
                                                    System.BitConverter.ToSingle(new[] { bytes[o + 8], bytes[o + 9], bytes[o + 10], bytes[o + 11] }, 0));
                                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                                var w = rootM.MultiplyPoint3x4(p);
                                wmin = Vector3.Min(wmin, w); wmax = Vector3.Max(wmax, w);
                            }
                            log($"{head} {which}: {n} verts (stride {stride}), root-space box {min:F3}..{max:F3} (size {(max - min).magnitude:F3}), world box {wmin:F2}..{wmax:F2}");
                        }
                        finally { buf.Dispose(); }
                    });
                }
            }
        }

        private string DescribeRenderer(Renderer r, float dist, Dictionary<Renderer, DynamicProxy> bySource)
        {
            {
                var sb = new StringBuilder();
                sb.Append($"'{GameControlPath(r.transform)}' {r.GetType().Name} L{r.gameObject.layer} d={dist:F1} size={r.bounds.size.magnitude:F1} static={r.isPartOfStaticBatch} " +
                          $"known={_known.Contains(r)}");
                if (bySource.TryGetValue(r, out var dp))
                    sb.Append($" proxy[visible={dp.Visible} mask=0x{dp.Mask:X} twoSided={dp.TwoSided} det={r.transform.localToWorldMatrix.determinant:F2} scale={r.transform.lossyScale:F2} " +
                              $"pos={r.transform.position:F2} proxyPos={(dp.Proxy != null ? dp.Proxy.transform.position : Vector3.zero):F2} " +
                              $"mesh={(dp.Proxy != null && dp.Proxy.GetComponent<MeshFilter>() is MeshFilter pmf && pmf.sharedMesh != null ? pmf.sharedMesh.name + " readable=" + pmf.sharedMesh.isReadable : "-")} " +
                              $"mats={string.Join(",", dp.Proxy != null ? dp.Proxy.sharedMaterials.Select(m => m != null ? m.name : "-") : new string[0])} " +
                              $"{DescribeProxyState(dp)}]");
                else if (r.isPartOfStaticBatch)
                {
                    sb.Append(" (static slot)");
                    var ss = _staticSources.FirstOrDefault(x => x.Renderer == r);
                    if (ss?.SlotByMat != null)
                        for (int i = 0; i < ss.SlotByMat.Length; i++)
                            if (ss.SlotByMat[i] is Slot sl)
                                sb.Append($"\n      slot[{i}] sub={sl.Sub} part={sl.Part} kind={sl.Kind} visible={sl.VisibleSources} proxy='{sl.Material?.name}' shader='{sl.Material?.shader?.name}'");
                    if (r is MeshRenderer smr0 && OriginalMaterials.TryGet(smr0, out var orig, out var counts, out var how))
                        for (int i = 0; i < orig.Length; i++)
                            if (orig[i] != null)
                                sb.Append($"\n      orig[{i}] ({how}, {(counts != null && i < counts.Length ? counts[i] : -1)} idx) '{orig[i].name}' shader='{orig[i].shader?.name}' " +
                                          $"kw=[{string.Join(" ", orig[i].shaderKeywords)}] props{{{DescribeProps(orig[i], null)}}}");
                }
                else sb.Append(" NO PROXY");
                if (r is ParticleSystemRenderer psr && r.GetComponent<ParticleSystem>() is ParticleSystem ps)
                    sb.Append($" particles[mode={psr.renderMode} align={psr.alignment} count={ps.particleCount} size={ps.main.startSize.constant:F2}..{ps.main.startSize.constantMax:F2} pos={r.transform.position:F2}]");
                else if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) sb.Append($" pos={r.transform.position:F2}");
                if (r is SkinnedMeshRenderer smr)
                {
                    int nullBones = smr.bones.Count(x => x == null);
                    var anim = r.GetComponentInParent<Animator>();
                    sb.Append($" skinned[mesh={smr.sharedMesh?.name} root={smr.rootBone?.name} bones={smr.bones.Length} null={nullBones} offscreen={smr.updateWhenOffscreen} " +
                              $"anim={(anim != null ? $"{anim.name} enabled={anim.enabled} cull={anim.cullingMode} ctrl={anim.runtimeAnimatorController?.name}" : "-")}]");
                }
                sb.Append($" hasMPB={r.HasPropertyBlock()}");
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) { sb.Append($"\n      [{i}] null"); continue; }
                    sb.Append($"\n      [{i}] '{m.name}' shader='{m.shader?.name}' q={m.renderQueue} traceable={Traceable(m)} transp={TransparentClassifier.Of(m)} lava={LavaClassifier.IsLava(m)} kw=[{string.Join(" ", m.shaderKeywords)}]");
                    sb.Append(" props{" + DescribeProps(m, null) + "}");
                    r.GetPropertyBlock(ProbeBlock, i);
                    if (!ProbeBlock.isEmpty) sb.Append(" mpb[" + i + "]{" + DescribeProps(m, ProbeBlock) + "}");
                }
                r.GetPropertyBlock(ProbeBlock);
                if (!ProbeBlock.isEmpty && mats.Length > 0 && mats[0] != null) sb.Append("\n      mpb{" + DescribeProps(mats[0], ProbeBlock) + "}");
                return sb.ToString();
            }
        }

        /// <summary>The proxy's own state and its mesh's layout (what the RTAS needs to accept the instance).</summary>
        private string DescribeProxyState(DynamicProxy dp)
        {
            var p = dp.Proxy;
            if (p == null) return "proxy=null";
            int entries = _dynamic.Count(x => x.Source == dp.Source);
            var sb = new StringBuilder($"entries={entries} active={p.gameObject.activeInHierarchy} enabled={p.enabled} rt={p.rayTracingMode} layer={p.gameObject.layer} " +
                                       $"placedAt={(Vector3)dp.CurM.GetColumn(3):F2} hasChanged={dp.SourceTransform.hasChanged} " +
                                       $"parentIsSource={p.transform.parent == dp.Source.transform} flags=[{string.Join(",", dp.Flags.Select(f => ((int)f).ToString()))}]");
            var mesh = p is SkinnedMeshRenderer ps ? ps.sharedMesh : p.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh != null)
            {
                sb.Append($" verts={mesh.vertexCount} idx={mesh.indexFormat} sub={mesh.subMeshCount} pos={(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Position) ? mesh.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Position) + "x" + mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Position) : "none")} " +
                          $"streams={mesh.vertexBufferCount} topo=[{string.Join(",", Enumerable.Range(0, mesh.subMeshCount).Select(s => mesh.GetTopology(s) + ":" + mesh.GetSubMesh(s).indexCount))}]");
            }
            return sb.ToString();
        }

        /// <summary>Non-default-looking values of a material's shader properties, or the ones a property block overrides.</summary>
        private static string DescribeProps(Material m, MaterialPropertyBlock block)
        {
            var sh = m.shader;
            if (sh == null) return "";
            var parts = new List<string>();
            int n = sh.GetPropertyCount();
            for (int p = 0; p < n; p++)
            {
                string name = sh.GetPropertyName(p);
                var t = sh.GetPropertyType(p);
                switch (t)
                {
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector:
                        if (block != null) { if (block.HasVector(name) || block.HasColor(name)) parts.Add($"{name}={block.GetVector(name):F3}"); }
                        else if (Interesting(name)) parts.Add($"{name}={m.GetVector(name):F3}");
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (block != null) { if (block.HasFloat(name)) parts.Add($"{name}={block.GetFloat(name):F3}"); }
                        else if (Interesting(name)) parts.Add($"{name}={m.GetFloat(name):F3}");
                        break;
                    case ShaderPropertyType.Texture:
                        if (block != null) { if (block.HasTexture(name)) parts.Add($"{name}={block.GetTexture(name)?.name}"); }
                        else { var tex = m.GetTexture(name); if (tex != null) parts.Add($"{name}={tex.name}({tex.dimension} {tex.width}x{tex.height} {tex.filterMode})"); }
                        break;
                }
            }
            return string.Join(" ", parts);
        }

        private static bool Interesting(string name) =>
            name.IndexOf("Color", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Emissi", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Opac", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Fill", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Blend", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Alpha", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Surface", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            name == "_Cutoff" || name == "_CullMode" || name.IndexOf("Clip", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Intensity", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
