using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Per-instance data the proxy hit shader needs to return a primary hit's world-space motion (docs/denoiser.md,
    /// "Motion vectors"). Owned by SceneProxies, so it lives exactly as long as the RT scene.
    ///  - Motion slots: every dynamic proxy gets a slot, passed in its RTAS custom id (layer | slot &lt;&lt; 8; static
    ///    batches use slot 0 = no motion). Per slot, 4 float4 in <c>_UltraRTMotion</c>: rows 0-2 the world-space motion
    ///    matrix W = M_prev * M_cur^-1, row 3 x = first vertex of the previous skinned pose (or -1). Only slots whose
    ///    data changed are uploaded, one SetData per run of nearby changed slots.
    ///  - Previous skinned pose: after each trace, every active skinned proxy's positions are copied from its skinned
    ///    vertex buffer into one shared buffer <c>_UltraRTPrevSkin</c> (MotionCopy.compute, one small dispatch each).
    ///    Regions are recycled per vertex count when a proxy leaves the RTAS.
    /// Both buffers are global shader properties: hit shaders take resources from globals when the material lacks them.
    /// </summary>
    internal sealed class MotionVectors : IDisposable
    {
        /// <summary>-ultrart-nomotion: every slot stays identity and no poses are copied (the old, static reprojection).</summary>
        public static bool Enabled = true;

        public const int MaxSlots = 1 << 16; // custom id is 24 bits: 8 layer + 16 slot

        private static readonly int IdMotion = Shader.PropertyToID("_UltraRTMotion");
        private static readonly int IdPrevSkin = Shader.PropertyToID("_UltraRTPrevSkin");
        private static readonly int IdSkinSrc = Shader.PropertyToID("_SkinSrc");
        private static readonly int IdCopyDst = Shader.PropertyToID("_PrevSkin"); // MotionCopy.compute's UAV
        private static readonly int IdCount = Shader.PropertyToID("_Count");
        private static readonly int IdStride = Shader.PropertyToID("_Stride");
        private static readonly int IdOffset = Shader.PropertyToID("_Offset");
        private static readonly int IdDstBase = Shader.PropertyToID("_DstBase");

        private readonly ComputeShader _copy;
        private readonly int _kCopy = -1;

        private Vector4[] _rows = new Vector4[4 * 256];
        private int _slotCount = 1; // slot 0: static
        private readonly List<int> _dirty = new List<int>();
        private bool[] _isDirty = new bool[256];
        private ComputeBuffer _slotBuffer;

        private ComputeBuffer _skin;
        private int _skinCapacity, _skinUsed;
        private readonly Dictionary<int, Stack<int>> _freeRegions = new Dictionary<int, Stack<int>>();
        private readonly List<GraphicsBuffer> _pending = new List<GraphicsBuffer>(); // vertex buffer handles of the last recording

        /// <summary>Bumped whenever the pose buffer is reallocated (its contents are lost: every pose is invalid for a frame).</summary>
        public int Generation { get; private set; }
        public bool CanCopySkin => _kCopy >= 0 && Enabled;
        public int SlotCount => _slotCount;
        public int SkinVertices => _skinUsed;
        public int CopiesLastFrame { get; private set; }
        /// <summary>Accumulated by SceneProxies (CPU ticks spent on motion bookkeeping, rigid instance updates), read and reset by the plugin's log.</summary>
        public long Ticks;
        public int RigidUpdates;
        private int _copies;

        public MotionVectors(ComputeShader copy)
        {
            _copy = copy;
            if (copy != null && copy.HasKernel("CopyPositions")) _kCopy = copy.FindKernel("CopyPositions");
            SetIdentity(0);
            EnsureSlotBuffer();
            EnsureSkin(1 << 16);
        }

        public int AllocSlot()
        {
            if (_slotCount >= MaxSlots) return 0; // out of slots: treated as static
            int s = _slotCount++;
            if (_rows.Length < _slotCount * 4)
            {
                Array.Resize(ref _rows, Math.Max(_rows.Length * 2, _slotCount * 4));
                Array.Resize(ref _isDirty, _rows.Length / 4);
            }
            SetIdentity(s);
            return s;
        }

        public void SetIdentity(int slot) => SetRows(slot, Matrix4x4.identity, -1);

        /// <summary>W maps this frame's world position to last frame's (M_prev * M_cur^-1); skinBase -1 = rigid.</summary>
        public void SetMotion(int slot, in Matrix4x4 w, int skinBase)
        {
            if (slot <= 0) return;
            if (!Enabled) SetRows(slot, Matrix4x4.identity, -1);
            else SetRows(slot, w, skinBase);
        }

        private void SetRows(int slot, in Matrix4x4 m, int skinBase)
        {
            int i = slot * 4;
            // y = 1: "written" marker (the shader ignores it), so a slot's first write always differs and is uploaded.
            var r3 = new Vector4(skinBase, 1f, 0, 0);
            var r0 = m.GetRow(0); var r1 = m.GetRow(1); var r2 = m.GetRow(2);
            if (_rows[i + 3] == r3 && _rows[i] == r0 && _rows[i + 1] == r1 && _rows[i + 2] == r2) return;
            _rows[i] = r0; _rows[i + 1] = r1; _rows[i + 2] = r2; _rows[i + 3] = r3;
            if (!_isDirty[slot]) { _isDirty[slot] = true; _dirty.Add(slot); }
        }

        /// <summary>A region of the pose buffer for a skinned proxy with this many vertices (recycled by size).</summary>
        public int AllocSkin(int vertices)
        {
            if (vertices <= 0) return -1;
            if (_freeRegions.TryGetValue(vertices, out var st) && st.Count > 0) return st.Pop();
            if ((long)_skinUsed + vertices > 1 << 24) return -1; // float-encoded base must stay exact
            int b = _skinUsed;
            _skinUsed += vertices;
            if (_skinUsed > _skinCapacity) EnsureSkin(Math.Max(_skinCapacity * 2, _skinUsed));
            return b;
        }

        public void FreeSkin(int b, int vertices)
        {
            if (b < 0 || vertices <= 0) return;
            if (!_freeRegions.TryGetValue(vertices, out var st)) _freeRegions[vertices] = st = new Stack<int>();
            st.Push(b);
        }

        /// <summary>Upload the slots changed this frame and (re)bind the globals. Call after the per-frame sync, before tracing.</summary>
        public void Upload()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureSlotBuffer();
            if (_dirty.Count > 0)
            {
                // Moving instances are scattered over thousands of slots: upload runs, bridging gaps of a few slots.
                _dirty.Sort();
                int start = _dirty[0], end = start;
                for (int k = 1; k <= _dirty.Count; k++)
                {
                    if (k < _dirty.Count && _dirty[k] - end <= 4) { end = _dirty[k]; continue; }
                    _slotBuffer.SetData(_rows, start * 4, start * 4, (end - start + 1) * 4);
                    if (k < _dirty.Count) start = end = _dirty[k];
                }
                foreach (int sl in _dirty) _isDirty[sl] = false;
                _dirty.Clear();
            }
            Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            Shader.SetGlobalBuffer(IdMotion, _slotBuffer);
            Shader.SetGlobalBuffer(IdPrevSkin, _skin);
            CopiesLastFrame = _copies;
            _copies = 0;
            // The command buffer that used last frame's vertex buffer handles has executed by now.
            foreach (var gb in _pending) gb.Dispose();
            _pending.Clear();
        }

        /// <summary>
        /// Records the copy of <paramref name="smr"/>'s current skinned positions into its region. Returns false if the
        /// renderer has no skinned buffer yet (then its next frame has no previous pose).
        /// </summary>
        public bool RecordCopy(CommandBuffer cmd, SkinnedMeshRenderer smr, int dstBase, int vertices, out GraphicsBuffer source)
        {
            source = null;
            if (!CanCopySkin || dstBase < 0 || smr == null) return false;
            GraphicsBuffer vb;
            try { vb = smr.GetVertexBuffer(); }
            catch (Exception) { return false; }
            if (vb == null) return false;
            _pending.Add(vb);
            if ((vb.target & GraphicsBuffer.Target.Raw) == 0 || vb.stride <= 0) return false;
            int count = Math.Min(vertices, vb.count);
            if (count <= 0) return false;
            var mesh = smr.sharedMesh;
            int offset = mesh != null ? Math.Max(0, mesh.GetVertexAttributeOffset(VertexAttribute.Position)) : 0;
            cmd.SetComputeBufferParam(_copy, _kCopy, IdSkinSrc, vb);
            cmd.SetComputeBufferParam(_copy, _kCopy, IdCopyDst, _skin);
            cmd.SetComputeIntParam(_copy, IdCount, count);
            cmd.SetComputeIntParam(_copy, IdStride, vb.stride);
            cmd.SetComputeIntParam(_copy, IdOffset, offset);
            cmd.SetComputeIntParam(_copy, IdDstBase, dstBase);
            cmd.DispatchCompute(_copy, _kCopy, (count + 63) / 64, 1, 1);
            _copies++;
            source = vb;
            return true;
        }

        /// <summary>Debug (-ultrart-motion-probe): read back one vertex of the previous-pose buffer, ordered after the copy.</summary>
        public void RequestPoseReadback(CommandBuffer cmd, int vertex, Action<Vector3> done)
        {
            cmd.RequestAsyncReadback(_skin, 12, vertex * 12, r => { if (!r.hasError) done(r.GetData<Vector3>()[0]); });
        }

        private void EnsureSlotBuffer()
        {
            int need = _rows.Length;
            if (_slotBuffer != null && _slotBuffer.count >= need) return;
            _slotBuffer?.Release();
            _slotBuffer = new ComputeBuffer(need, 16, ComputeBufferType.Structured) { name = "UltraRT Motion Slots" };
            _slotBuffer.SetData(_rows);
            foreach (int sl in _dirty) _isDirty[sl] = false;
            _dirty.Clear();
        }

        private void EnsureSkin(int vertices)
        {
            _skin?.Release();
            _skinCapacity = vertices;
            _skin = new ComputeBuffer(vertices, 12, ComputeBufferType.Structured) { name = "UltraRT Previous Skinned Positions" };
            Generation++;
        }

        public void Dispose()
        {
            foreach (var gb in _pending) gb.Dispose();
            _pending.Clear();
            _slotBuffer?.Release(); _slotBuffer = null;
            _skin?.Release(); _skin = null;
        }
    }
}
