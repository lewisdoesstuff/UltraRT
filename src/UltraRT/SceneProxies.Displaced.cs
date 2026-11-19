using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// Vertex displacement (docs/scene-proxies.md, "Vertex displacement"). ULTRAKILL's Master shader moves vertices in its
    /// vertex shader when VERTEX_DISPLACEMENT is on: 3-2's flesh walls and heart, water waves, lava, acid. A proxy of such a
    /// renderer traces its own copy of the mesh (DynamicGeometry, so every RTAS build rebuilds its BLAS from it), and
    /// Displace.compute writes the game's displaced pose into that copy each frame, before the build. The kernel first
    /// copies the old positions into MotionVectors' previous-pose buffer, so the hit shader returns per-vertex motion like
    /// for a skinned proxy. Static-batched and skinned renderers with the keyword keep the rest pose (logged).
    /// </summary>
    internal sealed partial class SceneProxies
    {
        /// <summary>-ultrart-nodisplace: no displaced copies at the scene build (proxies trace the rest pose, as before).</summary>
        public static bool DisplacementBuild = true;
        /// <summary>Runtime switch (bench variant plugin._displace=0): the copies get the rest pose. Set by the plugin every frame.</summary>
        public static bool DisplacementActive = true;
        /// <summary>Per-vertex motion of displaced proxies (bench variant plugin._displaceMotion=0: rigid motion only, for A/B).</summary>
        public static bool DisplacementMotion = true;
        /// <summary>
        /// Displaced copies whose bounds are farther than this from the camera (m) stop moving: the copy switches to a Static
        /// BLAS, frozen in its last pose, and is no longer dispatched. Unity rebuilds a DynamicGeometry BLAS every frame and
        /// traces it slower (no build-flag or refit API in 2022.3), which is most of the cost; see docs/scene-proxies.md.
        /// Set by the plugin every frame (plugin._displaceRange; 0 = all frozen, a large value = all moving).
        /// </summary>
        public static float DisplacementRange = 40f;
        private const float DisplaceRangeHysteresis = 5f;

        private static readonly int IdRest = Shader.PropertyToID("_Rest"), IdParams = Shader.PropertyToID("_Params"),
            IdDst = Shader.PropertyToID("_Dst"), IdPrevPose = Shader.PropertyToID("_PrevPose"),
            IdNoiseTex = Shader.PropertyToID("_VertexNoiseTex"), IdO2W = Shader.PropertyToID("_ObjectToWorld"),
            IdGameTime = Shader.PropertyToID("_GameTime"), IdCameraPos = Shader.PropertyToID("_CameraPos"),
            IdMeta = Shader.PropertyToID("_Meta"), IdMeta2 = Shader.PropertyToID("_Meta2"), IdDEnabled = Shader.PropertyToID("_Enabled");

        /// <summary>Displaced proxies whose material values are re-read per frame (swaps, property blocks); the rest use the cached ones.</summary>
        private const int DisplaceRefreshPerFrame = 4;
        private int _dispRefreshCursor;

        // The game's material properties (names from the vertex shader's constant buffer).
        private static readonly int PFlow = Shader.PropertyToID("_FlowDirection"), PMode = Shader.PropertyToID("_VertexMode"),
            PReverse = Shader.PropertyToID("_ReverseFlow"), PNoiseScale = Shader.PropertyToID("_VertexNoiseScale"),
            PUvScale = Shader.PropertyToID("_UVNoiseScale"), PUvSpeed = Shader.PropertyToID("_UVNoiseSpeed"),
            PNoiseSpeed = Shader.PropertyToID("_VertexNoiseSpeed"), PAmplitude = Shader.PropertyToID("_VertexNoiseAmplitude"),
            PLocalOffset = Shader.PropertyToID("_LocalOffset"), PRecalc = Shader.PropertyToID("_RecalculateNormals"),
            PNormalOffset = Shader.PropertyToID("_NormalOffsetScale"), PPreventClip = Shader.PropertyToID("_PreventClipping"),
            PVColStrength = Shader.PropertyToID("_VColStrength"), PNoiseTex = Shader.PropertyToID("_VertexNoiseTex");

        private const int ParamsPerSlot = 5;

        private sealed class Displaced
        {
            public DynamicProxy Entry;
            public string Path;
            public Mesh Dst;
            public GraphicsBuffer DstVB;      // the copy's vertex buffer holding the position (Raw)
            public ComputeBuffer Rest;        // RestVertex per vertex
            public ComputeBuffer Params;      // ParamsPerSlot float4 per material slot
            public Vector4[] ParamData, ParamUploaded;
            public int[] SlotSubmesh;         // material slot -> submesh index (its material is re-read every frame)
            public int Vertices, Stride, PosOffset, NrmOffset;
            public int LastFrame = -10;       // frame of the last dispatch (the copy holds that pose); < 0: never, and the first one always runs
            public bool Fresh = true;         // material values not read yet
            public Texture Tex;               // _VertexNoiseTex of the first displacing slot
            public int[] Meta = new int[4], Meta2 = new int[4];
            public DispState State = DispState.Moving;
            public Mesh Original;             // the source mesh (DispState.Original puts it back on the proxy)
        }

        private enum DispState { Moving, Frozen, Original }

        private void SetDisplacedState(Displaced disp, DispState want)
        {
            var d = disp.Entry;
            var mf = d.Proxy.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = want == DispState.Original ? disp.Original : disp.Dst;
            d.Proxy.rayTracingMode = want == DispState.Moving ? RayTracingMode.DynamicGeometry : RayTracingMode.Static;
            if (want != DispState.Moving) disp.LastFrame = Math.Min(disp.LastFrame, 0); // no previous pose next time it moves
            disp.State = want;
            // Mesh and mode are read when the instance is added.
            if (d.Visible)
            {
                Rtas.RemoveInstance(d.Proxy);
                Rtas.AddInstance(d.Proxy, d.Flags, enableTriangleCulling: !d.TwoSided, frontTriangleCounterClockwise: FrontCounterClockwise, mask: d.Mask | d.ShadowMask, id: d.Id);
            }
            DisplaceSwitches++;
        }

        private readonly List<Displaced> _displaced = new List<Displaced>();
        private readonly List<Material> _dispMats = new List<Material>();
        private MaterialPropertyBlock _dispBlock;
        private ComputeShader _displaceCs;
        private int _kDisplace = -1;

        /// <summary>Displace.compute from the bundle (Plugin.LoadShaders). Without it, displaced renderers trace the rest pose.</summary>
        public void SetDisplaceShader(ComputeShader cs)
        {
            _displaceCs = cs;
            _kDisplace = cs != null && cs.HasKernel("Displace") ? cs.FindKernel("Displace") : -1;
        }

        // Diagnostics (inventory per scene, 600-frame log).
        public int DisplacedCount => _displaced.Count;
        public int DisplacedStaticSkipped, DisplacedSkinnedSkipped, DisplacedFailed;
        public long DisplaceTicks;
        public int DisplaceDispatches, DisplaceVertices, DisplaceFrozen, DisplaceSwitches;

        public static bool HasDisplacement(Material m) =>
            m != null && m.shader != null && m.shader.name == "ULTRAKILL/Master" && m.IsKeywordEnabled("VERTEX_DISPLACEMENT");

        private static bool AnyDisplacement(Material[] mats)
        {
            if (mats == null) return false;
            foreach (var m in mats) if (HasDisplacement(m)) return true;
            return false;
        }

        /// <summary>Static-batched / skinned renderers with the keyword: counted and logged, they keep the rest pose.</summary>
        private void NoteUndisplaced(Renderer r, Material[] mats, bool staticBatch)
        {
            if (!AnyDisplacement(mats)) return;
            if (staticBatch) DisplacedStaticSkipped++; else DisplacedSkinnedSkipped++;
            if (DisplacedStaticSkipped + DisplacedSkinnedSkipped <= 12)
                _log.LogInfo($"Vertex displacement: '{GameControlPath(r.transform)}' ({(staticBatch ? "static batch" : "skinned")}) keeps the rest pose; " +
                             $"materials {string.Join(", ", mats.Where(HasDisplacement).Select(m => m.name))}");
        }

        /// <summary>AddDynamic: give a MeshRenderer proxy whose source draws with VERTEX_DISPLACEMENT a displaced copy of its mesh.</summary>
        private void TryDisplace(DynamicProxy d, Material[] mats)
        {
            if (!DisplacementBuild || _kDisplace < 0) return;
            if (!(d.Source is MeshRenderer) || !(d.Proxy is MeshRenderer pm)) return;
            if (!AnyDisplacement(mats)) return;
            var mf = pm.GetComponent<MeshFilter>();
            var src = mf != null ? mf.sharedMesh : null;
            if (src == null) return;
            Displaced disp = null;
            try { disp = BuildDisplaced(d, src, mats); }
            catch (Exception e) { _log.LogWarning($"Vertex displacement: '{GameControlPath(d.SourceTransform)}' mesh '{src.name}' failed: {e.Message}"); }
            if (disp == null) { DisplacedFailed++; return; }
            mf.sharedMesh = disp.Dst;
            pm.rayTracingMode = RayTracingMode.DynamicGeometry;
            d.Disp = disp;
            d.SkinVerts = disp.Vertices; // previous-pose region size (Evaluate allocates it)
            _displaced.Add(disp);
            if (_displaced.Count <= 24)
            {
                var desc = string.Join("; ", disp.SlotSubmesh.Select(sm => DescribeDisplacement(sm < mats.Length ? mats[sm] : null)));
                _log.LogInfo($"Vertex displacement: '{disp.Path}' mesh '{src.name}' {disp.Vertices} verts, {src.subMeshCount} submeshes, " +
                             $"readable {src.isReadable}, normals {(disp.NrmOffset >= 0 ? "written" : "not written")}, mask {d.Mask}: {desc}");
            }
        }

        private static string DescribeDisplacement(Material m)
        {
            if (m == null) return "-";
            float F(int id) => m.HasProperty(id) ? m.GetFloat(id) : float.NaN;
            Vector4 V(int id) => m.HasProperty(id) ? m.GetVector(id) : Vector4.zero;
            var tex = m.HasProperty(PNoiseTex) ? m.GetTexture(PNoiseTex) : null;
            return $"{m.name} mode {F(PMode)} amp {F(PAmplitude)} speed {F(PNoiseSpeed)} scale {F(PNoiseScale)} flow {V(PFlow)} reverse {F(PReverse)} " +
                   $"uv {V(PUvScale).x},{V(PUvScale).y} / {V(PUvSpeed).x},{V(PUvSpeed).y} local {F(PLocalOffset)} recalc {F(PRecalc)} normalOffset {F(PNormalOffset)} " +
                   $"preventClip {F(PPreventClip)} vcol {F(PVColStrength)} tex {(tex != null ? $"{tex.name} {tex.width}x{tex.height} {tex.wrapMode}" : "none")} kw [{string.Join(" ", m.shaderKeywords)}]";
        }

        private struct RestVertex
        {
            public Vector3 Pos, Nrm;
            public Vector4 Col;
            public Vector2 Uv;
            public uint Slot;
        }

        private Displaced BuildDisplaced(DynamicProxy d, Mesh src, Material[] mats)
        {
            int vc = src.vertexCount;
            if (vc == 0) return null;
            if (!src.HasVertexAttribute(VertexAttribute.Position) || src.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                src.GetVertexAttributeDimension(VertexAttribute.Position) < 3)
                throw new Exception("position is not float3");
            for (int sm = 0; sm < src.subMeshCount; sm++)
                if (src.GetTopology(sm) != MeshTopology.Triangles) throw new Exception($"submesh {sm} is not triangles");

            var rest = new RestVertex[vc];
            Mesh dst;
            int[][] subIdx = new int[src.subMeshCount][];
            if (src.isReadable)
            {
                var pos = src.vertices; var nrm = src.normals; var col = src.colors; var uv = src.uv;
                for (int i = 0; i < vc; i++)
                {
                    rest[i].Pos = pos[i];
                    rest[i].Nrm = nrm.Length == vc ? nrm[i] : Vector3.up;
                    rest[i].Col = col.Length == vc ? (Vector4)col[i] : Vector4.one;
                    rest[i].Uv = uv.Length == vc ? uv[i] : Vector2.zero;
                }
                for (int sm = 0; sm < src.subMeshCount; sm++) subIdx[sm] = src.GetIndices(sm, true);
                dst = Object.Instantiate(src);
            }
            else
            {
                // GPU readback (once per renderer): every stream and the index buffer, then a copy with the same layout.
                var streams = new byte[src.vertexBufferCount][];
                for (int s = 0; s < streams.Length; s++)
                {
                    int stride = src.GetVertexBufferStride(s);
                    if (stride <= 0) continue;
                    using (var vb = src.GetVertexBuffer(s))
                    {
                        var words = new uint[(vc * stride + 3) / 4];
                        vb.GetData(words, 0, 0, words.Length);
                        streams[s] = new byte[vc * stride];
                        Buffer.BlockCopy(words, 0, streams[s], 0, streams[s].Length);
                    }
                }
                uint[] idx;
                using (var ib = src.GetIndexBuffer())
                {
                    if (src.indexFormat == IndexFormat.UInt32) { idx = new uint[ib.count]; ib.GetData(idx); }
                    else
                    {
                        var packed = new uint[(ib.count + 1) / 2];
                        ib.GetData(packed, 0, 0, packed.Length);
                        idx = new uint[ib.count];
                        for (int k = 0; k < ib.count; k++) idx[k] = (packed[k >> 1] >> ((k & 1) * 16)) & 0xFFFF;
                    }
                }
                for (int i = 0; i < vc; i++)
                {
                    rest[i].Pos = Read(src, streams, VertexAttribute.Position, i, Vector4.zero);
                    rest[i].Nrm = Read(src, streams, VertexAttribute.Normal, i, new Vector4(0, 1, 0, 0));
                    rest[i].Col = Read(src, streams, VertexAttribute.Color, i, Vector4.one);
                    rest[i].Uv = Read(src, streams, VertexAttribute.TexCoord0, i, Vector4.zero);
                }
                for (int sm = 0; sm < src.subMeshCount; sm++)
                {
                    var desc = src.GetSubMesh(sm);
                    var list = new int[desc.indexCount];
                    for (int k = 0; k < list.Length; k++) list[k] = (int)idx[desc.indexStart + k] + desc.baseVertex;
                    subIdx[sm] = list;
                }
                dst = new Mesh { name = src.name, indexFormat = src.indexFormat };
                dst.SetVertexBufferParams(vc, src.GetVertexAttributes());
                const MeshUpdateFlags quiet = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                for (int s = 0; s < streams.Length; s++)
                    if (streams[s] != null) dst.SetVertexBufferData(streams[s], 0, 0, streams[s].Length, s, quiet);
                dst.SetIndexBufferParams(idx.Length, src.indexFormat);
                if (src.indexFormat == IndexFormat.UInt32) dst.SetIndexBufferData(idx, 0, 0, idx.Length, quiet);
                else dst.SetIndexBufferData(idx.Select(x => (ushort)x).ToArray(), 0, 0, idx.Length, quiet);
                dst.subMeshCount = src.subMeshCount;
                for (int sm = 0; sm < src.subMeshCount; sm++) dst.SetSubMesh(sm, src.GetSubMesh(sm), quiet);
            }
            dst.name = src.name + " (displaced) [UltraRT]";
            dst.hideFlags = HideFlags.HideAndDontSave;
            dst.bounds = src.bounds; // RT doesn't cull by it; the game's bounds don't include the displacement either

            // Material slots: one per submesh whose material displaces (the game runs the vertex shader per submesh).
            var slotOf = new List<int>();
            for (int i = 0; i < vc; i++) rest[i].Slot = 0xffffffffu;
            for (int sm = 0; sm < subIdx.Length; sm++)
            {
                var m = sm < mats.Length ? mats[sm] : null;
                // Only submeshes this proxy traces (a renderer's water and its opaque parts are separate proxies).
                if (!HasDisplacement(m) || (sm < d.Flags.Length && d.Flags[sm] == RayTracingSubMeshFlags.Disabled)) continue;
                uint slot = (uint)slotOf.Count;
                slotOf.Add(sm);
                foreach (int v in subIdx[sm]) if ((uint)v < (uint)vc) rest[v].Slot = slot;
            }
            if (slotOf.Count == 0) { Object.Destroy(dst); return null; }

            // Position (and normal, if float3 in the same stream) are written in place.
            dst.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            int ps = dst.GetVertexAttributeStream(VertexAttribute.Position);
            int nrmOffset = -1;
            if (dst.HasVertexAttribute(VertexAttribute.Normal) && dst.GetVertexAttributeStream(VertexAttribute.Normal) == ps &&
                dst.GetVertexAttributeFormat(VertexAttribute.Normal) == VertexAttributeFormat.Float32 && dst.GetVertexAttributeDimension(VertexAttribute.Normal) >= 3)
                nrmOffset = dst.GetVertexAttributeOffset(VertexAttribute.Normal);
            var disp = new Displaced
            {
                Entry = d,
                Path = GameControlPath(d.SourceTransform),
                Dst = dst,
                Original = src,
                DstVB = dst.GetVertexBuffer(ps),
                Vertices = vc,
                Stride = dst.GetVertexBufferStride(ps),
                PosOffset = dst.GetVertexAttributeOffset(VertexAttribute.Position),
                NrmOffset = nrmOffset,
                SlotSubmesh = slotOf.ToArray(),
                ParamData = new Vector4[slotOf.Count * ParamsPerSlot],
                ParamUploaded = new Vector4[slotOf.Count * ParamsPerSlot],
            };
            if ((disp.DstVB.target & GraphicsBuffer.Target.Raw) == 0) { disp.DstVB.Dispose(); Object.Destroy(dst); throw new Exception("vertex buffer is not raw"); }
            disp.Rest = new ComputeBuffer(vc, 52, ComputeBufferType.Structured) { name = "UltraRT Displaced Rest" };
            disp.Rest.SetData(rest);
            disp.Params = new ComputeBuffer(disp.ParamData.Length, 16, ComputeBufferType.Structured) { name = "UltraRT Displaced Params" };
            for (int i = 0; i < disp.ParamUploaded.Length; i++) disp.ParamUploaded[i] = new Vector4(float.NaN, 0, 0, 0); // first upload always
            return disp;
        }

        /// <summary>One attribute of vertex i from raw streams (formats Unity meshes use), as a Vector4.</summary>
        private static Vector4 Read(Mesh mesh, byte[][] streams, VertexAttribute a, int i, Vector4 fallback)
        {
            if (!mesh.HasVertexAttribute(a)) return fallback;
            int s = mesh.GetVertexAttributeStream(a);
            var b = streams[s];
            if (b == null) return fallback;
            int at = i * mesh.GetVertexBufferStride(s) + mesh.GetVertexAttributeOffset(a);
            int dim = mesh.GetVertexAttributeDimension(a);
            var f = mesh.GetVertexAttributeFormat(a);
            var r = fallback;
            for (int c = 0; c < dim && c < 4; c++)
            {
                float v;
                switch (f)
                {
                    case VertexAttributeFormat.Float32: v = BitConverter.ToSingle(b, at + c * 4); break;
                    case VertexAttributeFormat.Float16: v = Mathf.HalfToFloat(BitConverter.ToUInt16(b, at + c * 2)); break;
                    case VertexAttributeFormat.UNorm8: v = b[at + c] / 255f; break;
                    case VertexAttributeFormat.SNorm8: v = Mathf.Max(-1f, (sbyte)b[at + c] / 127f); break;
                    case VertexAttributeFormat.UNorm16: v = BitConverter.ToUInt16(b, at + c * 2) / 65535f; break;
                    case VertexAttributeFormat.SNorm16: v = Mathf.Max(-1f, BitConverter.ToInt16(b, at + c * 2) / 32767f); break;
                    default: return fallback;
                }
                r[c] = v;
            }
            return r;
        }

        /// <summary>The game's per-material values for a slot (the renderer's property block wins, as in the game).</summary>
        private void FillParams(Displaced disp, int slot, Material m, MaterialPropertyBlock block)
        {
            int o = slot * ParamsPerSlot;
            if (!HasDisplacement(m))
            {
                // Swapped to a material without the keyword: amplitude 0 moves nothing in any mode.
                for (int k = 0; k < ParamsPerSlot; k++) disp.ParamData[o + k] = Vector4.zero;
                disp.ParamData[o + 1] = new Vector4(-1, 0, 0, 0);
                return;
            }
            bool hasBlock = block != null && !block.isEmpty;
            float F(int id) => hasBlock && block.HasFloat(id) ? block.GetFloat(id) : m.GetFloat(id);
            Vector4 V(int id) => hasBlock && block.HasVector(id) ? block.GetVector(id) : m.GetVector(id);
            var uvScale = V(PUvScale); var uvSpeed = V(PUvSpeed);
            disp.ParamData[o] = V(PFlow);
            disp.ParamData[o + 1] = new Vector4(F(PMode), F(PReverse), F(PNoiseScale), F(PVColStrength));
            disp.ParamData[o + 2] = new Vector4(uvScale.x, uvScale.y, uvSpeed.x, uvSpeed.y);
            disp.ParamData[o + 3] = new Vector4(F(PNoiseSpeed), F(PAmplitude), F(PLocalOffset), F(PRecalc));
            disp.ParamData[o + 4] = new Vector4(F(PNormalOffset), F(PPreventClip), 0, 0);
        }

        /// <summary>SyncDynamic, per active displaced proxy: its motion slot (rigid motion and the previous pose).</summary>
        private void DisplacedMotion(DynamicProxy d)
        {
            // The copy holds last frame's pose only if it was displaced last frame; this frame's kernel then copies it out.
            bool prev = DisplacementMotion && d.SkinBase >= 0 && d.Disp.LastFrame == Time.frameCount - 1;
            Motion.SetMotion(d.Slot, d.PrevM == d.CurM ? Matrix4x4.identity : d.PrevM * d.CurM.inverse, prev ? d.SkinBase : -1);
        }

        /// <summary>Before the RTAS build: write this frame's displaced pose of every displaced proxy in the RTAS.</summary>
        public void RecordDisplacement(CommandBuffer cmd, Vector3 cameraPos)
        {
            if (_displaced.Count == 0 || _kDisplace < 0) return;
            long t0 = Perf.Now;
            float t = Time.timeSinceLevelLoad; // the game's _Time.y
            cmd.SetComputeVectorParam(_displaceCs, IdGameTime, new Vector4(t / 20f, t, t * 2f, t * 3f));
            cmd.SetComputeVectorParam(_displaceCs, IdCameraPos, cameraPos);
            cmd.SetComputeIntParam(_displaceCs, IdDEnabled, DisplacementActive ? 1 : 0);
            cmd.SetComputeBufferParam(_displaceCs, _kDisplace, IdPrevPose, Motion.PoseBuffer);
            if (_dispBlock == null) _dispBlock = new MaterialPropertyBlock();
            int n = _displaced.Count;
            _dispRefreshCursor = (_dispRefreshCursor + DisplaceRefreshPerFrame) % n;
            for (int i = _displaced.Count - 1; i >= 0; i--)
            {
                var disp = _displaced[i];
                var d = disp.Entry;
                if (d.Source == null || d.Proxy == null)
                {
                    // Source destroyed (checkpoint re-clones): free the copy.
                    ReleaseDisplaced(disp);
                    _displaced.RemoveAt(i);
                    continue;
                }
                if (!d.Visible) continue;
                // State: moving (DynamicGeometry copy), frozen beyond DisplacementRange (Static copy, last pose) or, with a
                // negative range, the original mesh (exactly the proxy without displacement: a runtime off switch).
                DispState want;
                if (DisplacementRange < 0f) want = DispState.Original;
                else
                {
                    float dist = Mathf.Sqrt(d.Proxy.bounds.SqrDistance(cameraPos));
                    want = dist > DisplacementRange + (disp.State == DispState.Moving ? DisplaceRangeHysteresis : 0f) && disp.LastFrame >= 0
                        ? DispState.Frozen : DispState.Moving;
                }
                if (want != disp.State) SetDisplacedState(disp, want);
                if (disp.State != DispState.Moving) { DisplaceFrozen++; continue; }
                // Material values (swaps, property blocks): read at the first dispatch, then a few proxies per frame.
                if (disp.Fresh || ((i - _dispRefreshCursor) % n + n) % n < DisplaceRefreshPerFrame)
                    RefreshDisplacedParams(disp, cmd);
                disp.Meta[0] = disp.Vertices; disp.Meta[1] = disp.Stride; disp.Meta[2] = disp.PosOffset; disp.Meta[3] = disp.NrmOffset;
                disp.Meta2[0] = d.SkinBase;
                cmd.SetComputeMatrixParam(_displaceCs, IdO2W, d.CurM); // the proxy's localToWorld, kept current by SyncDynamic
                cmd.SetComputeIntParams(_displaceCs, IdMeta, disp.Meta);
                cmd.SetComputeIntParams(_displaceCs, IdMeta2, disp.Meta2);
                cmd.SetComputeBufferParam(_displaceCs, _kDisplace, IdRest, disp.Rest);
                cmd.SetComputeBufferParam(_displaceCs, _kDisplace, IdParams, disp.Params);
                cmd.SetComputeBufferParam(_displaceCs, _kDisplace, IdDst, disp.DstVB);
                cmd.SetComputeTextureParam(_displaceCs, _kDisplace, IdNoiseTex, disp.Tex != null ? disp.Tex : Texture2D.grayTexture);
                cmd.DispatchCompute(_displaceCs, _kDisplace, (disp.Vertices + 63) / 64, 1, 1);
                disp.LastFrame = Time.frameCount;
                DisplaceDispatches++;
                DisplaceVertices += disp.Vertices;
            }
            DisplaceTicks += Perf.Now - t0;
        }

        private void LogDisplacementInventory()
        {
            if (_displaced.Count + DisplacedStaticSkipped + DisplacedSkinnedSkipped + DisplacedFailed == 0) return;
            _log.LogInfo($"Vertex displacement: {_displaced.Count} displaced proxies ({_displaced.Sum(x => x.Vertices)} vertices; " +
                         $"{_displaced.Count(x => x.Entry.Mask == GeometryMask)} geometry, {_displaced.Count(x => x.Entry.Mask == TransparentMask)} water/glass, " +
                         $"{_displaced.Count(x => x.Entry.Mask == LavaMask)} lava, {_displaced.Count(x => x.Entry.Mask != GeometryMask && x.Entry.Mask != TransparentMask && x.Entry.Mask != LavaMask)} other), " +
                         $"{DisplacedStaticSkipped} static-batched and {DisplacedSkinnedSkipped} skinned renderers keep the rest pose, {DisplacedFailed} failed" +
                         $"{(DisplacementBuild ? "" : " (off: -ultrart-nodisplace)")}{(_kDisplace < 0 ? " (Displace.compute missing)" : "")}");
        }

        private void RefreshDisplacedParams(Displaced disp, CommandBuffer cmd)
        {
            var src = disp.Entry.Source;
            src.GetSharedMaterials(_dispMats);
            bool hasBlock = src.HasPropertyBlock();
            if (hasBlock) src.GetPropertyBlock(_dispBlock);
            Texture tex = null;
            for (int s = 0; s < disp.SlotSubmesh.Length; s++)
            {
                int sm = disp.SlotSubmesh[s];
                var m = sm < _dispMats.Count ? _dispMats[sm] : null;
                FillParams(disp, s, m, hasBlock ? _dispBlock : null);
                if (tex == null && HasDisplacement(m)) tex = hasBlock && _dispBlock.HasTexture(PNoiseTex) ? _dispBlock.GetTexture(PNoiseTex) : m.GetTexture(PNoiseTex);
            }
            disp.Tex = tex;
            disp.Fresh = false;
            bool changed = false;
            for (int k = 0; k < disp.ParamData.Length && !changed; k++) changed = disp.ParamData[k] != disp.ParamUploaded[k];
            if (!changed) return;
            Array.Copy(disp.ParamData, disp.ParamUploaded, disp.ParamData.Length);
            cmd.SetBufferData(disp.Params, disp.ParamData);
        }

        private static void ReleaseDisplaced(Displaced disp)
        {
            disp.DstVB?.Dispose(); disp.DstVB = null;
            disp.Rest?.Release(); disp.Rest = null;
            disp.Params?.Release(); disp.Params = null;
            if (disp.Dst != null) Object.Destroy(disp.Dst);
            disp.Dst = null;
        }

        private void DisposeDisplaced()
        {
            foreach (var disp in _displaced) ReleaseDisplaced(disp);
            _displaced.Clear();
        }
    }
}
