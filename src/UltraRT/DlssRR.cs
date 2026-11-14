using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>
    /// NVIDIA DLSS Ray Reconstruction through UltraRT.Native.dll (Dlss.cpp), replacing the denoiser and the temporal
    /// upsampler in the replace modes. See docs/dlss.md. Owns the RR input/output textures, the jitter sequence and the
    /// RayReconstruction.compute passes; the plugin event itself runs NGX on Unity's render thread.
    /// </summary>
    internal sealed class DlssRR : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameData
        {
            public IntPtr color, diffuse, specular, normals, depth, motion, specHit, output;
            public int renderW, renderH, outW, outH;
            public int reset, preset, quality, flags;
            public float jitterX, jitterY, mvScaleX, mvScaleY;
            public float frameTimeMs, pad0, pad1, pad2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] worldToView;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] viewToClip;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long LongFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate int ConfigureFn(string dllDir, string dataDir);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetFrameFn(int slot, ref FrameData f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TakeLogFn(byte[] buf, int size);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private readonly IntPtr _eventFunc;
        private readonly int _eventBase, _ring;
        private readonly SetFrameFn _setFrame;
        private readonly IntFn _getState;
        private readonly LongFn _evalCount;
        private readonly PtrFn _getStatus;
        private readonly TakeLogFn _takeLog;
        private readonly byte[] _logBuf = new byte[16384];
        private readonly float[] _w2v = new float[16], _v2c = new float[16];

        private ComputeShader _cs;
        private int _kPrepare = -1, _kFinish = -1, _kExposure = -1;
        private RenderTexture _color, _diffuse, _specular, _normals, _depth, _motion, _hitDist, _out, _final, _aeFiltered, _aeAlbedo;
        private IntPtr _pColor, _pDiffuse, _pSpecular, _pNormals, _pDepth, _pMotion, _pHitDist, _pOut;
        private int _slot;
        private uint _jitterIndex;
        private bool _historyValid;
        private Matrix4x4 _prevViewProj;
        private Vector2 _prevJitter;
        private bool _disposed;

        /// <summary>Halton(2,3) phases (DLSS-RR guide: at least 32).</summary>
        public int JitterPhases = 64;
        /// <summary>Sign of the jitter handed to DLSS (our offset is the sample position relative to the texel centre).</summary>
        public float JitterSign = -1f;
        /// <summary>0 = default (recommended), 4 = D, 5 = E, 6 = F (NVSDK_NGX_RayReconstruction_Hint_Render_Preset).</summary>
        public int Preset;
        /// <summary>true = record into our own command list submitted through Unity (ExecuteCommandList); false = Unity's list.</summary>
        public bool OwnCommandList = true;
        public bool SpecularHitDistance = true;
        /// <summary>Linear-space input with relight's linear transport (else the composite's gamma terms).</summary>
        public bool Linearize = true;
        /// <summary>Log Unity's tracked resource states for the first evaluates (needs the obsolete IUnityGraphicsD3D12).</summary>
        public bool LogStates = true;

        public RenderTexture Final => _final;
        public int State => _disposed ? -1 : _getState();
        public long Evaluations => _evalCount();
        public string Status => Marshal.PtrToStringAnsi(_getStatus()) ?? "";
        public Vector2 Jitter { get; private set; }

        private DlssRR(IntPtr module)
        {
            _eventFunc = Get<PtrFn>(module, "GetRenderEventAndDataFunc")();
            _eventBase = Get<IntFn>(module, "UltraRT_Dlss_GetEventBase")();
            _ring = Get<IntFn>(module, "UltraRT_Dlss_GetRingSize")();
            int size = Get<IntFn>(module, "UltraRT_Dlss_GetFrameSize")();
            _setFrame = Get<SetFrameFn>(module, "UltraRT_Dlss_SetFrame");
            _getState = Get<IntFn>(module, "UltraRT_Dlss_GetState");
            _evalCount = Get<LongFn>(module, "UltraRT_Dlss_GetEvalCount");
            _getStatus = Get<PtrFn>(module, "UltraRT_Dlss_GetStatus");
            _takeLog = Get<TakeLogFn>(module, "UltraRT_Dlss_TakeLog");
            if (_ring <= 0) throw new InvalidOperationException(Status);
            if (size != Marshal.SizeOf<FrameData>()) throw new InvalidOperationException($"frame struct size mismatch: native {size}, managed {Marshal.SizeOf<FrameData>()}");
        }

        private static T Get<T>(IntPtr module, string name) where T : Delegate
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"UltraRT.Native.dll has no export '{name}' (old build?)");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        /// <summary>Main thread. Loads UltraRT.Native.dll (shared with the GPU timer), registers it with Unity, points NGX
        /// at nvngx_dlssd.dll next to it. Never throws.</summary>
        public static bool TryCreate(string pluginDir, string dataDir, out DlssRR rr, out string error)
        {
            rr = null;
            error = null;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) { error = $"graphics API is {SystemInfo.graphicsDeviceType}, RR needs Direct3D12"; return false; }
                var dll = Path.Combine(pluginDir, GpuTimer.DllName);
                if (!File.Exists(dll)) { error = $"{dll} not found"; return false; }
                if (!File.Exists(Path.Combine(pluginDir, "nvngx_dlssd.dll"))) { error = $"nvngx_dlssd.dll not found in {pluginDir} (scripts\\fetch-dlss.ps1, deploy)"; return false; }
                IntPtr module = LoadLibraryW(Path.GetFullPath(dll));
                if (module == IntPtr.Zero) { error = $"LoadLibrary({dll}) failed: Win32 error {Marshal.GetLastWin32Error()}"; return false; }
                var init = Get<IntFn>(module, "UltraRT_Init");
                if (init() == 0) { error = "native init failed: " + Marshal.PtrToStringAnsi(Get<PtrFn>(module, "UltraRT_GetStatus")()); return false; }
                Directory.CreateDirectory(dataDir);
                Get<ConfigureFn>(module, "UltraRT_Dlss_Configure")(Path.GetFullPath(pluginDir), Path.GetFullPath(dataDir));
                rr = new DlssRR(module);
                return true;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        public void SetShader(ComputeShader cs)
        {
            _cs = cs;
            _kPrepare = cs != null ? cs.FindKernel("Prepare") : -1;
            _kFinish = cs != null ? cs.FindKernel("Finish") : -1;
            _kExposure = cs != null ? cs.FindKernel("ExposureInput") : -1;
        }

        public bool HasShader => _cs != null;

        /// <summary>Native log lines since the last call (NGX's own log included), or null.</summary>
        public string TakeLog()
        {
            int n = _takeLog(_logBuf, _logBuf.Length);
            return n > 0 ? System.Text.Encoding.ASCII.GetString(_logBuf, 0, n).TrimEnd('\n') : null;
        }

        public void Reset() => _historyValid = false;

        /// <summary>This frame's sample offset from the trace texel centre (trace px): Halton(2,3), JitterPhases long.</summary>
        public Vector2 NextJitter()
        {
            uint k = _jitterIndex++ % (uint)Mathf.Max(1, JitterPhases) + 1;
            Jitter = new Vector2(Halton(k, 2) - 0.5f, Halton(k, 3) - 0.5f);
            return Jitter;
        }

        private static float Halton(uint i, uint b)
        {
            float f = 1f, r = 0f;
            while (i > 0) { f /= b; r += f * (i % b); i /= b; }
            return r;
        }

        public struct Inputs
        {
            public RenderTexture radiance, albedo, emission, specular, normalDepth, instanceId, surface, reflection, litWeight, gameDepth;
            public Camera camera;
            public Matrix4x4 viewProj;   // this frame's (jittered, guard band) trace viewProj
            public Vector2Int guardPx;
            public Vector2 guardScale;
            public Vector4 reflParams;
            public ComputeBuffer exposure;
            public float frameTime;      // s
        }

        /// <summary>Records Prepare, the RR plugin event and Finish. Returns false (nothing recorded) if RR can't run.</summary>
        public bool Execute(CommandBuffer cmd, in Inputs i, GpuTimer gpu = null, int evalSlot = -1)
        {
            if (_disposed || _cs == null || _getState() < 0) return false;
            int tw = i.radiance.width, th = i.radiance.height;
            int rw = tw - 2 * i.guardPx.x, rh = th - 2 * i.guardPx.y;
            int W = i.gameDepth.width, H = i.gameDepth.height;
            EnsureTargets(rw, rh, W, H, tw, th);
            var cs = _cs;
            var cam = i.camera;
            var jitter = Jitter;
            if (!_historyValid) { _prevViewProj = i.viewProj; _prevJitter = jitter; }

            // ---- Prepare
            int k = _kPrepare;
            cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(tw, th, 1f / tw, 1f / th));
            cmd.SetComputeVectorParam(cs, "_CamPos", cam.transform.position);
            cmd.SetComputeVectorParam(cs, "_CamFwd", cam.transform.forward);
            cmd.SetComputeMatrixParam(cs, "_InvViewProj", i.viewProj.inverse);
            cmd.SetComputeMatrixParam(cs, "_PrevViewProj", _prevViewProj);
            cmd.SetComputeVectorParam(cs, "_Jitter", new Vector4(jitter.x, jitter.y, _prevJitter.x, _prevJitter.y));
            cmd.SetComputeIntParams(cs, "_Guard", i.guardPx.x, i.guardPx.y, rw, rh);
            cmd.SetComputeVectorParam(cs, "_ReflParams", i.reflParams);
            cmd.SetComputeVectorParam(cs, "_RRParams", new Vector4(Linearize ? 1f : 0f, cam.farClipPlane, _historyValid ? 1f : 0f, SpecularHitDistance ? 1f : 0f));
            cmd.SetComputeTextureParam(cs, k, "_Radiance", i.radiance);
            cmd.SetComputeTextureParam(cs, k, "_Albedo", i.albedo);
            cmd.SetComputeTextureParam(cs, k, "_Emission", i.emission);
            cmd.SetComputeTextureParam(cs, k, "_Specular", i.specular);
            cmd.SetComputeTextureParam(cs, k, "_NormalDepth", i.normalDepth);
            cmd.SetComputeTextureParam(cs, k, "_InstanceId", i.instanceId);
            cmd.SetComputeTextureParam(cs, k, "_Surface", i.surface);
            cmd.SetComputeTextureParam(cs, k, "_Reflection", i.reflection);
            BindExposure(cmd, k, i);
            cmd.SetComputeTextureParam(cs, k, "_OutColor", _color);
            cmd.SetComputeTextureParam(cs, k, "_OutDiffuse", _diffuse);
            cmd.SetComputeTextureParam(cs, k, "_OutSpecular", _specular);
            cmd.SetComputeTextureParam(cs, k, "_OutNormal", _normals);
            cmd.SetComputeTextureParam(cs, k, "_OutDepth", _depth);
            cmd.SetComputeTextureParam(cs, k, "_OutMotion", _motion);
            cmd.SetComputeTextureParam(cs, k, "_OutHitDist", _hitDist);
            cmd.SetComputeTextureParam(cs, k, "_RROutput", _out);
            cmd.DispatchCompute(cs, k, Mathf.CeilToInt(rw / 8f), Mathf.CeilToInt(rh / 8f), 1);

            // ---- RR (plugin event)
            var f = new FrameData
            {
                color = _pColor, diffuse = _pDiffuse, specular = _pSpecular, normals = _pNormals, depth = _pDepth, motion = _pMotion,
                specHit = _pHitDist, output = _pOut,
                renderW = rw, renderH = rh, outW = W, outH = H,
                reset = _historyValid ? 0 : 1, preset = Preset, quality = QualityFor(rw, W),
                flags = (SpecularHitDistance ? 1 : 0) | (LogStates ? 2 : 0),
                jitterX = JitterSign * jitter.x, jitterY = JitterSign * jitter.y, mvScaleX = 1f, mvScaleY = 1f,
                frameTimeMs = Mathf.Max(0.1f, i.frameTime * 1000f),
            };
            // Matrices: row-major with row vectors = Unity's column-major memory order. View space +z forward.
            var flip = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            var w2v = flip * cam.worldToCameraMatrix;
            var v2c = cam.projectionMatrix * flip;
            f.worldToView = _w2v;
            f.viewToClip = _v2c;
            for (int n = 0; n < 16; n++) { _w2v[n] = w2v[n]; _v2c[n] = v2c[n]; }
            int slot = _slot;
            _slot = (_slot + 1) % _ring;
            _setFrame(slot, ref f);
            if (evalSlot >= 0) gpu?.Begin(cmd, evalSlot);
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + (OwnCommandList ? 1 : 0), (IntPtr)slot);
            if (evalSlot >= 0) gpu?.End(cmd, evalSlot);

            // ---- Finish
            k = _kFinish;
            var proj = cam.projectionMatrix;
            float near = cam.nearClipPlane, far = cam.farClipPlane;
            Vector4 z = SystemInfo.usesReversedZBuffer
                ? new Vector4(-1f + far / near, 1f, (-1f + far / near) / far, 1f / far)
                : new Vector4(1f - far / near, far / near, (1f - far / near) / far, far / near / far);
            cmd.SetComputeVectorParam(cs, "_OutDim", new Vector4(W, H, 1f / W, 1f / H));
            cmd.SetComputeVectorParam(cs, "_TraceMap", new Vector4((float)rw / W, (float)rh / H, i.guardPx.x, i.guardPx.y));
            cmd.SetComputeVectorParam(cs, "_TraceRay", new Vector4(i.guardScale.x / proj.m00, i.guardScale.y / proj.m11, 0f, 0f));
            cmd.SetComputeVectorParam(cs, "_ZParams", z);
            cmd.SetComputeVectorParam(cs, "_Match", new Vector4(0.03f, 0.05f, 0f, 0f));
            cmd.SetComputeTextureParam(cs, k, "_RRColor", _out);
            cmd.SetComputeTextureParam(cs, k, "_GameDepth", i.gameDepth, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeTextureParam(cs, k, "_NormalDepth", i.normalDepth);
            cmd.SetComputeTextureParam(cs, k, "_InstanceId", i.instanceId);
            cmd.SetComputeTextureParam(cs, k, "_Final", _final);
            BindExposure(cmd, k, i);
            cmd.DispatchCompute(cs, k, Mathf.CeilToInt(W / 8f), Mathf.CeilToInt(H / 8f), 1);

            _prevViewProj = i.viewProj;
            _prevJitter = jitter;
            _historyValid = true;
            return true;
        }

        /// <summary>After Execute: the trace-res texture relight's auto exposure meters (albedo 1, see the shader).</summary>
        public void ExposureInput(CommandBuffer cmd, in Inputs i, out RenderTexture filtered, out RenderTexture albedo)
        {
            int k = _kExposure;
            int tw = i.radiance.width, th = i.radiance.height;
            cmd.SetComputeTextureParam(_cs, k, "_RRColor", _out);
            cmd.SetComputeTextureParam(_cs, k, "_Emission", i.emission);
            cmd.SetComputeTextureParam(_cs, k, "_AEFiltered", _aeFiltered);
            cmd.SetComputeTextureParam(_cs, k, "_AEAlbedo", _aeAlbedo);
            BindExposure(cmd, k, i);
            cmd.DispatchCompute(_cs, k, Mathf.CeilToInt(tw / 8f), Mathf.CeilToInt(th / 8f), 1);
            filtered = _aeFiltered;
            albedo = _aeAlbedo;
        }

        private void BindExposure(CommandBuffer cmd, int k, in Inputs i)
        {
            if (i.exposure != null) AutoExposure.BindComposite(cmd, _cs, k, i.exposure, i.litWeight);
            else
            {
                cmd.SetComputeBufferParam(_cs, k, "_UltraRTExposure", DummyBuffer());
                cmd.SetComputeTextureParam(_cs, k, "_UltraRTLitWeight", i.litWeight);
            }
        }

        private ComputeBuffer _dummy;
        private ComputeBuffer DummyBuffer()
        {
            if (_dummy == null) { _dummy = new ComputeBuffer(2, 16); _dummy.SetData(new[] { Vector4.one, Vector4.zero }); }
            return _dummy;
        }

        /// <summary>NVSDK_NGX_PerfQuality_Value nearest to the render/output ratio (RR sizes come from Create, this only picks
        /// the preset slot): 0 MaxPerf (0.5), 1 Balanced (0.58), 2 MaxQuality (0.67), 3 UltraPerformance (0.33), 5 DLAA (1).</summary>
        private static int QualityFor(int renderW, int outW)
        {
            float r = (float)renderW / Mathf.Max(1, outW);
            if (r > 0.9f) return 5;
            if (r > 0.62f) return 2;
            if (r > 0.54f) return 1;
            if (r > 0.42f) return 0;
            return 3;
        }

        private void EnsureTargets(int rw, int rh, int W, int H, int tw, int th)
        {
            bool render = _color == null || _color.width != rw || _color.height != rh || !_color.IsCreated();
            bool screen = _out == null || _out.width != W || _out.height != H || !_out.IsCreated();
            bool trace = _aeFiltered == null || _aeFiltered.width != tw || _aeFiltered.height != th || !_aeFiltered.IsCreated();
            if (render)
            {
                Release(ref _color); Release(ref _diffuse); Release(ref _specular); Release(ref _normals); Release(ref _depth); Release(ref _motion); Release(ref _hitDist);
                _color = Create(rw, rh, RenderTextureFormat.ARGBHalf, "UltraRT RR Color");
                _diffuse = Create(rw, rh, RenderTextureFormat.ARGB32, "UltraRT RR DiffuseAlbedo");
                _specular = Create(rw, rh, RenderTextureFormat.ARGB32, "UltraRT RR SpecularAlbedo");
                _normals = Create(rw, rh, RenderTextureFormat.ARGBHalf, "UltraRT RR NormalRoughness");
                _depth = Create(rw, rh, RenderTextureFormat.RFloat, "UltraRT RR Depth");
                _motion = Create(rw, rh, RenderTextureFormat.RGHalf, "UltraRT RR Motion");
                _hitDist = Create(rw, rh, RenderTextureFormat.RHalf, "UltraRT RR SpecHitDist");
                _pColor = _color.GetNativeTexturePtr(); _pDiffuse = _diffuse.GetNativeTexturePtr(); _pSpecular = _specular.GetNativeTexturePtr();
                _pNormals = _normals.GetNativeTexturePtr(); _pDepth = _depth.GetNativeTexturePtr(); _pMotion = _motion.GetNativeTexturePtr();
                _pHitDist = _hitDist.GetNativeTexturePtr();
                _historyValid = false;
            }
            if (screen)
            {
                Release(ref _out); Release(ref _final);
                _out = Create(W, H, RenderTextureFormat.ARGBHalf, "UltraRT RR Output");
                _final = Create(W, H, RenderTextureFormat.ARGBHalf, "UltraRT RR Final");
                _pOut = _out.GetNativeTexturePtr();
                _historyValid = false;
            }
            if (trace)
            {
                Release(ref _aeFiltered); Release(ref _aeAlbedo);
                _aeFiltered = Create(tw, th, RenderTextureFormat.ARGBHalf, "UltraRT RR AE Lighting");
                _aeAlbedo = Create(tw, th, RenderTextureFormat.ARGB32, "UltraRT RR AE Albedo");
            }
        }

        private static RenderTexture Create(int w, int h, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name, enableRandomWrite = true, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                useMipMap = false, autoGenerateMips = false, hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        /// <summary>Releases the NGX feature (render thread, after in-flight frames) when RR is switched off for a while.</summary>
        public void ReleaseFeature(CommandBuffer cmd)
        {
            if (_disposed) return;
            cmd.IssuePluginEventAndData(_eventFunc, _eventBase + 2, IntPtr.Zero);
            _historyValid = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            var cmd = new CommandBuffer { name = "UltraRT RR release" };
            ReleaseFeature(cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            _disposed = true;
            Release(ref _color); Release(ref _diffuse); Release(ref _specular); Release(ref _normals); Release(ref _depth); Release(ref _motion);
            Release(ref _hitDist); Release(ref _out); Release(ref _final); Release(ref _aeFiltered); Release(ref _aeAlbedo);
            _dummy?.Release();
            _dummy = null;
        }
    }
}
