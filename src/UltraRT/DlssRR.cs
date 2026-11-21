using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
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
        /// <summary>Finish: a screen pixel whose game depth matches no trace sample still counts as traced when the
        /// samples around it lie on one plane (1 / depth bends by at most this fraction) that passes through that depth (a floor at a grazing angle;
        /// docs/path-tracing.md, "The game's pixels in the traced image"). 0 = off. Bench plugin._grazingMatch=0.</summary>
        public static float GrazingMatch = 0.02f;
        /// <summary>Finish paints the pixels the game keeps by the relative depth of the 5x5's nearest trace sample: red =
        /// behind the game's surface, blue = in front, full at 10 %; green 0.25 = no sample (bench plugin._matchDebug=1).</summary>
        public static bool MatchDebug;
        /// <summary>Diagnostics (bench plugin._rrFinishDebug): 1 = Finish shows the screen-resolution demodulation colour D alone
        /// (tone mapped), 2 = RR's lighting X alone (x 0.5: X = 1 is mid grey). Remodulation only.</summary>
        public static int FinishDebug;
        /// <summary>Finish: a pixel no trace sample of this frame matches is still traced when the screen-resolution
        /// demodulation history has its surface (geometry thinner than a trace texel). Bench plugin._thinMatch=0.</summary>
        public static bool ThinMatch = true;
        /// <summary>With ThinMatch: the depth tolerance grows by what this many trace texels change the depth of a
        /// surface at its angle to the view (edge-on quads: grass blades).</summary>
        public static float SlopeTexels = 1f;
        /// <summary>Finish, after the thin match: a pixel still unmatched takes the trace sample of the 5x5 around it whose
        /// depth is nearest the game's, if within this fraction of the depth, and is drawn as traced with that sample's
        /// colour and RR's lighting at the pixel. The game's own pixel of a grass blade no ray hit is lit by the game (7-1:
        /// white in its fog) and sparkled against the traced blades (docs/path-tracing.md, "Thin geometry no ray hit").
        /// Off by default: depth alone cannot tell such a blade from what the game draws over a traced surface (1-3's
        /// lava turned yellow with it on, 2026-11-18). Bench plugin._thinNear=0.1.</summary>
        public static float ThinNear = 0f;
        /// <summary>The cutout pass (CutoutTrace.raytrace, Plugin.Cutout.cs): set per frame by the plugin. Null shader or
        /// RTAS, or CutoutOn false: no dispatch, and Finish ignores _Cutout.</summary>
        public RayTracingShader CutoutShader;
        public RayTracingAccelerationStructure CutoutRtas;
        public bool CutoutOn;
        /// <summary>Finish paints the game-kept pixels by what the cutout ray found (bench plugin._cutoutDebug=1).</summary>
        public bool CutoutDebug;
        public bool CutoutFlip;
        /// <summary>Frames the cutout pass ran (diagnostics).</summary>
        public int CutoutFrames;
        private RenderTexture _cutout, _cutoutPrep;
        private int _kCutoutPrep = -1;
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SizesFn(int renderW, int renderH, int outW, int outH, int preset, int quality);

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
        private readonly SizesFn _warm, _featureReady;
        private readonly IntFn _isBusy;
        private readonly byte[] _logBuf = new byte[16384];
        private readonly float[] _w2v = new float[16], _v2c = new float[16];

        private ComputeShader _cs;
        private int _kPrepare = -1, _kFinish = -1, _kExposure = -1, _kDemod = -1;
        private RenderTexture _color, _diffuse, _specular, _normals, _depth, _motion, _hitDist, _out, _final, _aeFiltered, _aeAlbedo, _demod;
        // Remodulation (RayReconstruction.compute): the demodulation colour accumulated to screen resolution.
        private TemporalUpsampler _demodUp;
        private IntPtr _pColor, _pDiffuse, _pSpecular, _pNormals, _pDepth, _pMotion, _pHitDist, _pOut;
        private int _slot;
        private bool _remodWas;
        private uint _jitterIndex;
        private bool _historyValid;
        private Matrix4x4 _prevViewProj;
        private Vector2 _prevJitter;
        private bool _disposed;

        /// <summary>Halton(2,3) phases (DLSS-RR guide: at least 32). 4 = the temporal upsampler's pattern, (+-0.25, +-0.25): exact
        /// output pixel centres at 2x.</summary>
        public int JitterPhases = 64;
        /// <summary>N > 1: instead of Halton, the N x N offsets that put the trace samples on the screen pixel centres in turn
        /// (screen = N x the visible trace size, e.g. 3 at trace scale 1/3: every screen pixel is hit exactly once per 9
        /// frames). With <see cref="Remodulate"/> the texels then converge to the exact grid within N x N frames.</summary>
        public int ExactGrid;
        private static readonly int[] GridOrder3 = { 4, 2, 6, 0, 8, 1, 7, 3, 5 }; // centre, then far apart
        /// <summary>Sign of the jitter handed to DLSS. Our J is the sample position relative to the texel centre (texel index space);
        /// DLSS takes the projection offset, which moves the image by +offset, i.e. -J (docs/dlss.md, "Jitter").</summary>
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
        /// <summary>Bench cost probe: RR output = this x the screen (Finish point-samples it back). 1 = normal.</summary>
        public float OutScale = 1f;
        /// <summary>RR reconstructs lighting only; textures are re-applied from exact texel hits accumulated at screen
        /// resolution (docs/dlss.md, "Remodulation"). Needs the Upsample compute shader; not with <see cref="OutScale"/>.</summary>
        public bool Remodulate = true;
        /// <summary>Accumulation of the demodulation colour: the best (nearest) sample of each screen pixel wins.</summary>
        public float DemodSharpness = 30f, DemodMaxWeight = 4f;
        /// <summary>V1's shadow factor gets a 3x3 same-surface filter before RR (RayReconstruction.compute, PlayerShadowFactor).</summary>
        public bool PlayerFilter = true;
        /// <summary>Finish: a glowing surface the trace finds in front of the game's depth is drawn from its own sample (lighting
        /// and colour), where the game's depth buffer lacks it. Gabriel's swords: drawn by the game but missing from its depth,
        /// their screen-resolution colour was the organ behind them, which showed through (docs/dlss.md, "Emitters in front of
        /// the game's depth"). Bench plugin._rrFrontEmitters=0, -ultrart-rr-nofrontemitters: off.</summary>
        public bool FrontEmitters = true;
        /// <summary>Emission stays out of RR's signal and is added back at screen resolution (docs/dlss.md, "Emission outside
        /// RR"; _RRParams2.w). Bench plugin._rrEmitOutside=0, -ultrart-rr-emit-inside: the old way (X = c / D with E in both).</summary>
        public bool EmissionOutside = true;

        public RenderTexture Final => _final;
        /// <summary>Set before Execute: Finish also writes the HDR bloom source into it (must be the output size; null = not
        /// wanted). BloomWritten says whether the last Execute did. Params as Denoiser.BloomParams (docs/vfx.md).</summary>
        public RenderTexture BloomTarget;
        public Vector4 BloomParams;
        public bool BloomWritten { get; private set; }
        /// <summary>Set before Execute: Finish also writes the HDR display gain map into it (the output size; null = not
        /// wanted). HdrWritten says whether the last Execute did (docs/hdr.md).</summary>
        public RenderTexture HdrTarget;
        public Vector4 HdrParams, HdrEmissive, HdrGame;
        /// <summary>The game's frame before the composite (screen size), or null: the expected colour of the pixels the game draws itself.</summary>
        public RenderTexture HdrGameColor;
        public bool HdrWritten { get; private set; }
        /// <summary>Native float HDR (docs/hdr.md): Finish writes the HDR colour itself (HdrParams.x = 2) and the game's own
        /// bright pixels scaled by HdrGame (display units), and no gain map.</summary>
        public bool HdrNative;
        private RenderTexture _bloomDummy;
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
            _warm = Get<SizesFn>(module, "UltraRT_Dlss_Warm");
            _featureReady = Get<SizesFn>(module, "UltraRT_Dlss_FeatureReady");
            _isBusy = Get<IntFn>(module, "UltraRT_Dlss_IsBusy");
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

        public void SetShader(ComputeShader cs, ComputeShader upsample = null)
        {
            _cs = cs;
            _kPrepare = cs != null ? cs.FindKernel("Prepare") : -1;
            _kFinish = cs != null ? cs.FindKernel("Finish") : -1;
            _kCutoutPrep = cs != null && cs.HasKernel("CutoutPrep") ? cs.FindKernel("CutoutPrep") : -1;
            _kExposure = cs != null ? cs.FindKernel("ExposureInput") : -1;
            _kDemod = cs != null && cs.HasKernel("Demod") ? cs.FindKernel("Demod") : -1;
            _demodUp?.Dispose();
            _demodUp = upsample != null ? new TemporalUpsampler(upsample) { Unclamped = true, ExactReplace = 0.08f, Settings = new UpsamplerSettings() } : null;
            _historyValid = false;
        }

        public bool HasShader => _cs != null;

        /// <summary>Native log lines since the last call (NGX's own log included), or null.</summary>
        public string TakeLog()
        {
            int n = _takeLog(_logBuf, _logBuf.Length);
            return n > 0 ? System.Text.Encoding.ASCII.GetString(_logBuf, 0, n).TrimEnd('\n') : null;
        }

        public void Reset() => _historyValid = false;

        /// <summary>The camera went through a portal: last frame's view of this frame's world (docs/portals.md, "Temporal").</summary>
        public void RemapHistory(Matrix4x4 nowToOld) { _prevViewProj = _prevViewProj * nowToOld; _demodUp?.RemapHistory(nowToOld); }

        /// <summary>A warm-up (NGX init and/or feature creation) runs on the native worker thread: don't evaluate.</summary>
        public bool Busy => !_disposed && _isBusy() != 0;

        /// <summary>Starts NGX's initialisation on a worker thread (docs/dlss.md, "Warm-up"). Main thread; returns at once. With a
        /// small throw-away feature that the worker evaluates once: NGX loads its kernels on the first evaluate of the process
        /// (measured 130-230 ms, and it stalled the render thread even when it ran on the worker mid-game), so that happens at
        /// start-up. The first real feature replaces it.</summary>
        public void WarmInit() { if (!_disposed) _warm(320, 180, 960, 540, Preset, 3); }

        /// <summary>The feature Execute would use for this trace: render size (guard band cropped), output size, exact-grid
        /// output offset and the screen-to-trace map. Mirrors what Execute does.</summary>
        public void FeatureSizes(int tw, int th, Vector2Int guardPx, Vector2 guardScale, int Ws, int Hs, out int rw, out int rh, out int W, out int H,
                                 out bool exact, out Vector2Int outOffset, out Vector4 traceMap)
            => FeatureSizes(tw, th, guardPx, guardScale, Ws, Hs, out rw, out rh, out W, out H, out exact, out outOffset, out traceMap, out _);

        /// <param name="renderOffset">Where RR's input starts in the trace: the guard band, or 0 with <see cref="RenderGuard"/>.</param>
        public void FeatureSizes(int tw, int th, Vector2Int guardPx, Vector2 guardScale, int Ws, int Hs, out int rw, out int rh, out int W, out int H,
                                 out bool exact, out Vector2Int outOffset, out Vector4 traceMap, out Vector2Int renderOffset)
        {
            rw = tw - 2 * guardPx.x; rh = th - 2 * guardPx.y;
            renderOffset = guardPx;
            float os = Mathf.Clamp(OutScale, 0.25f, 4f);
            W = Mathf.RoundToInt(Ws * os); H = Mathf.RoundToInt(Hs * os);
            // Exact grid (Plugin.ExactGridTrace sized the trace for it): one trace pixel is exactly N screen pixels, so RR's
            // output is N x its render size, a little larger than the screen and centred on it.
            int grid = ExactGrid;
            exact = grid > 2 && Mathf.Approximately(os, 1f) && rw * grid >= Ws && rh * grid >= Hs && (rw * grid - Ws) % 2 == 0 && (rh * grid - Hs) % 2 == 0
                    && Mathf.Abs(guardScale.x - (float)tw * grid / Ws) < 1e-4f && Mathf.Abs(guardScale.y - (float)th * grid / Hs) < 1e-4f;
            outOffset = Vector2Int.zero;
            traceMap = new Vector4((float)rw / Ws, (float)rh / Hs, guardPx.x, guardPx.y);
            if (exact)
            {
                if (RenderGuard)
                {
                    // RR sees the whole trace, guard band included (history for what enters the screen while turning), and its
                    // output overscans the screen by the guard band too. The screen-to-trace map is the same.
                    rw = tw; rh = th;
                    renderOffset = Vector2Int.zero;
                }
                W = rw * grid; H = rh * grid;
                outOffset = new Vector2Int((W - Ws) / 2, (H - Hs) / 2);
                traceMap = new Vector4(1f / grid, 1f / grid, renderOffset.x + (float)outOffset.x / grid, renderOffset.y + (float)outOffset.y / grid);
            }
        }

        /// <summary>Exact grid only: RR's input is the whole trace including the guard band (bench plugin._rrGuard=1).</summary>
        public bool RenderGuard;

        /// <summary>Whether the feature for this trace exists. If not, starts creating it on the native worker thread (with NGX's
        /// init if that hasn't run) and returns false: the caller uses its own denoiser until it is ready, instead of the
        /// render thread stalling for the creation (docs/dlss.md, "Warm-up").</summary>
        public bool EnsureFeatureAsync(int tw, int th, Vector2Int guardPx, Vector2 guardScale, int Ws, int Hs)
        {
            if (_disposed) return false;
            FeatureSizes(tw, th, guardPx, guardScale, Ws, Hs, out int rw, out int rh, out int W, out int H, out _, out _, out _);
            int q = QualityFor(rw, W);
            if (_featureReady(rw, rh, W, H, Preset, q) != 0) return true;
            WaitReason = $"{rw}x{rh} -> {W}x{H} q{q}: ";
            if (Busy) { WaitReason += "worker busy"; return false; }
            // Another feature exists (the start-up probe, other sizes): release it on the render thread first. The worker
            // releasing a feature it had created on its (since destroyed) queue froze the game for 16 s inside NGX's
            // ReleaseFeature (bench/results/20261115-020657_pt2-e_alt); render-thread releases never stalled.
            if (_getState() == 2) { ReleaseNow(); WaitReason += "releasing the old feature"; return false; }
            WaitReason += $"warm-up requested (state {_getState()}, returned {_warm(rw, rh, W, H, Preset, q)})";
            return false;
        }

        /// <summary>Why the last EnsureFeatureAsync returned false (log).</summary>
        public string WaitReason { get; private set; } = "";

        /// <summary>Whether a feature exists (any sizes).</summary>
        public bool HasFeature => !_disposed && _getState() == 2;

        /// <summary>Releases the feature through the render-thread event, now (main thread).</summary>
        public void ReleaseNow()
        {
            var cb = new CommandBuffer { name = "UltraRT RR release" };
            ReleaseFeature(cb);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        /// <summary>This frame's sample offset from the trace texel centre (trace px): Halton(2,3), JitterPhases long.</summary>
        public Vector2 NextJitter()
        {
            uint i = _jitterIndex++;
            if (ExactGrid > 2)
            {
                Jitter = GridJitter(i, ExactGrid);
                return Jitter;
            }
            if (JitterPhases == 4 || ExactGrid == 2)
            {
                switch (i & 3)
                {
                    case 0: Jitter = new Vector2(-0.25f, -0.25f); break;
                    case 1: Jitter = new Vector2(0.25f, 0.25f); break;
                    case 2: Jitter = new Vector2(0.25f, -0.25f); break;
                    default: Jitter = new Vector2(-0.25f, 0.25f); break;
                }
                return Jitter;
            }
            uint k = i % (uint)Mathf.Max(1, JitterPhases) + 1;
            Jitter = new Vector2(Halton(k, 2) - 0.5f, Halton(k, 3) - 0.5f);
            return Jitter;
        }

        /// <summary>Phase i of the exact N x N grid: the sample offset (trace px) that lands on screen pixel centre i of each trace
        /// pixel's N x N block, in a spread-out order.</summary>
        public static Vector2 GridJitter(uint i, int n)
        {
            int cell = (int)(i % (uint)(n * n));
            if (n == 3) cell = GridOrder3[cell];
            else cell = (int)((cell * 7L + 3) % (n * n)); // 7 is coprime to n x n for n = 4, 5: a spread-out order
            return new Vector2((cell % n + 0.5f) / n - 0.5f, (cell / n + 0.5f) / n - 0.5f);
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
            HdrWritten = false;
            if (_disposed || _cs == null || _getState() < 0) return false;
            int tw = i.radiance.width, th = i.radiance.height;
            int Ws = i.gameDepth.width, Hs = i.gameDepth.height;
            float os = Mathf.Clamp(OutScale, 0.25f, 4f);
            FeatureSizes(tw, th, i.guardPx, i.guardScale, Ws, Hs, out int rw, out int rh, out int W, out int H, out bool exact, out var outOffset, out var traceMap, out var renderOffset);
            EnsureTargets(rw, rh, W, H, tw, th, Ws, Hs);
            var cs = _cs;
            var cam = i.camera;
            var jitter = Jitter;
            bool remod = Remodulate && _demodUp != null && _kDemod >= 0 && Mathf.Approximately(os, 1f);
            if (remod != _remodWas) { _remodWas = remod; _historyValid = false; } // the RR history holds the other signal
            if (!_historyValid) { _prevViewProj = i.viewProj; _prevJitter = jitter; }

            int k;
            var invViewProj = i.viewProj.inverse;
            cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(tw, th, 1f / tw, 1f / th));
            cmd.SetComputeVectorParam(cs, "_RRParams2", new Vector4(remod ? 1f : 0f, PlayerFilter ? 1f : 0f, FrontEmitters ? 1f : 0f, EmissionOutside ? 1f : 0f));
            cmd.SetComputeVectorParam(cs, "_CamPos", cam.transform.position);
            cmd.SetComputeMatrixParam(cs, "_InvViewProj", invViewProj);
            cmd.SetComputeVectorParam(cs, "_ReflParams", i.reflParams);
            if (remod)
            {
                // ---- Demod (trace res), then its accumulation at screen resolution with this frame's jitter
                k = _kDemod;
                cmd.SetComputeTextureParam(cs, k, "_Albedo", i.albedo);
                cmd.SetComputeTextureParam(cs, k, "_Emission", i.emission);
                cmd.SetComputeTextureParam(cs, k, "_Specular", i.specular);
                cmd.SetComputeTextureParam(cs, k, "_NormalDepth", i.normalDepth);
                cmd.SetComputeTextureParam(cs, k, "_Surface", i.surface);
                cmd.SetComputeTextureParam(cs, k, "_DemodOut", _demod);
                cmd.DispatchCompute(cs, k, Mathf.CeilToInt(tw / 8f), Mathf.CeilToInt(th / 8f), 1);
                var us = _demodUp.Settings;
                us.sharpness = DemodSharpness;
                us.maxWeight = DemodMaxWeight;
                us.spatialWeight = 1e-3f; // only until a pixel has a sample of its own
                _demodUp.Execute(cmd, cam, _demod, i.normalDepth, i.instanceId, i.gameDepth, i.guardPx, i.guardScale, jitter, !_historyValid, traceMap);
            }

            // ---- Prepare
            k = _kPrepare;
            cmd.SetComputeTextureParam(cs, k, "_DemodTex", _demod);
            cmd.SetComputeVectorParam(cs, "_Dim", new Vector4(tw, th, 1f / tw, 1f / th));
            cmd.SetComputeVectorParam(cs, "_CamPos", cam.transform.position);
            cmd.SetComputeVectorParam(cs, "_CamFwd", cam.transform.forward);
            cmd.SetComputeMatrixParam(cs, "_InvViewProj", invViewProj);
            cmd.SetComputeMatrixParam(cs, "_PrevViewProj", _prevViewProj);
            cmd.SetComputeVectorParam(cs, "_Jitter", new Vector4(jitter.x, jitter.y, _prevJitter.x, _prevJitter.y));
            cmd.SetComputeIntParams(cs, "_Guard", renderOffset.x, renderOffset.y, rw, rh);
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
            cmd.SetComputeVectorParam(cs, "_OutDim", new Vector4(Ws, Hs, 1f / Ws, 1f / Hs));
            cmd.SetComputeVectorParam(cs, "_RRMap", exact ? new Vector4(1f, 1f, W, H) : new Vector4((float)W / Ws, (float)H / Hs, W, H));
            cmd.SetComputeIntParams(cs, "_RROffset", outOffset.x, outOffset.y, 0, 0);
            cmd.SetComputeVectorParam(cs, "_TraceMap", traceMap);
            cmd.SetComputeVectorParam(cs, "_TraceRay", new Vector4(i.guardScale.x / proj.m00, i.guardScale.y / proj.m11, 0f, 0f));
            cmd.SetComputeVectorParam(cs, "_ZParams", z);
            cmd.SetComputeVectorParam(cs, "_Match", new Vector4(0.03f, 0.05f, GrazingMatch, ThinMatch ? SlopeTexels : 0f));
            bool cutout = CutoutOn && CutoutShader != null && CutoutRtas != null && remod && _kCutoutPrep >= 0;
            if (cutout)
            {
                // One camera ray per screen pixel the trace samples around it don't already match (CutoutTrace.raytrace).
                if (_cutout == null || _cutout.width != Ws || _cutout.height != Hs || !_cutout.IsCreated())
                {
                    Release(ref _cutout);
                    _cutout = Create(Ws, Hs, RenderTextureFormat.ARGBHalf, "UltraRT Cutout");
                }
                if (_cutoutPrep == null || _cutoutPrep.width != Ws || _cutoutPrep.height != Hs || !_cutoutPrep.IsCreated())
                {
                    Release(ref _cutoutPrep);
                    _cutoutPrep = Create(Ws, Hs, RenderTextureFormat.RFloat, "UltraRT Cutout Prep");
                }
                // The plain depth match first (compute: ray tracing shaders can't read the depth buffer); the rays run
                // only where it fails. Uses the Finish parameters set above.
                int kp = _kCutoutPrep;
                cmd.SetComputeVectorParam(cs, "_Match", new Vector4(0.03f, 0.05f, GrazingMatch, ThinMatch ? SlopeTexels : 0f));
                cmd.SetComputeVectorParam(cs, "_OutDim", new Vector4(Ws, Hs, 1f / Ws, 1f / Hs));
                cmd.SetComputeVectorParam(cs, "_TraceMap", traceMap);
                cmd.SetComputeVectorParam(cs, "_TraceRay", new Vector4(i.guardScale.x / proj.m00, i.guardScale.y / proj.m11, 0f, 0f));
                cmd.SetComputeVectorParam(cs, "_ZParams", z);
                cmd.SetComputeTextureParam(cs, kp, "_GameDepth", i.gameDepth, 0, RenderTextureSubElement.Depth);
                cmd.SetComputeTextureParam(cs, kp, "_NormalDepth", i.normalDepth);
                cmd.SetComputeTextureParam(cs, kp, "_Emission", i.emission);
                cmd.SetComputeTextureParam(cs, kp, "_CutoutPrepOut", _cutoutPrep);
                cmd.DispatchCompute(cs, kp, Mathf.CeilToInt(Ws / 8f), Mathf.CeilToInt(Hs / 8f), 1);
                var rs = CutoutShader;
                rs.SetShaderPass("UltraRTHit");
                cmd.SetRayTracingAccelerationStructure(rs, "_Scene", CutoutRtas);
                cmd.SetRayTracingTextureParam(rs, "_CutoutOut", _cutout);
                cmd.SetRayTracingTextureParam(rs, "_CutoutPrep", _cutoutPrep);
                cmd.SetRayTracingMatrixParam(rs, "_CutInvViewProj", invViewProj); // the trace's: rays where Finish maps the pixel
                cmd.SetRayTracingVectorParam(rs, "_CutTraceMap", traceMap);
                cmd.SetRayTracingVectorParam(rs, "_CutJitter", new Vector4(jitter.x, jitter.y, 0f, 0f));
                cmd.SetRayTracingVectorParam(rs, "_CutDim", new Vector4(tw, th, 1f / tw, 1f / th));
                // yz: Finish's plain depth tolerance (_Match.xy): the ray runs that far past the game's surface.
                cmd.SetRayTracingVectorParam(rs, "_CutFlags", new Vector4(CutoutFlip ? 1f : 0f, 0.03f, 0.05f, 0f));
                cmd.SetRayTracingVectorParam(rs, "_CutCamPos", cam.transform.position);
                var fwd = cam.transform.forward;
                cmd.SetRayTracingVectorParam(rs, "_CutCamFwd", new Vector4(fwd.x, fwd.y, fwd.z, 0f));
                cmd.DispatchRays(rs, "RayGen", (uint)Ws, (uint)Hs, 1);
                CutoutFrames++;
            }
            cmd.SetComputeVectorParam(cs, "_Match2", new Vector4(ThinMatch ? ThinNear : 0f, cutout ? 1f : 0f, MatchDebug ? 2f : CutoutDebug ? 1f : 0f, FinishDebug));
            cmd.SetComputeTextureParam(cs, k, "_Cutout", cutout ? (Texture)_cutout : Texture2D.blackTexture);
            cmd.SetComputeTextureParam(cs, k, "_RRColor", _out);
            cmd.SetComputeTextureParam(cs, k, "_GameDepth", i.gameDepth, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeTextureParam(cs, k, "_NormalDepth", i.normalDepth);
            cmd.SetComputeTextureParam(cs, k, "_InstanceId", i.instanceId);
            cmd.SetComputeTextureParam(cs, k, "_Final", _final);
            cmd.SetComputeTextureParam(cs, k, "_DemodTex", _demod);
            cmd.SetComputeTextureParam(cs, k, "_DemodHi", remod ? _demodUp.Output : _demod);
            // HDR bloom source (docs/vfx.md): screen resolution here. A 1x1 dummy when nobody wants it.
            if (_bloomDummy == null)
            {
                _bloomDummy = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { enableRandomWrite = true, name = "UltraRT RR Bloom Dummy", hideFlags = HideFlags.HideAndDontSave };
                _bloomDummy.Create();
            }
            bool bloom = BloomTarget != null && BloomTarget.width == Ws && BloomTarget.height == Hs;
            cmd.SetComputeTextureParam(cs, k, "_BloomOut", bloom ? BloomTarget : _bloomDummy);
            cmd.SetComputeTextureParam(cs, k, "_Emission", i.emission);
            cmd.SetComputeVectorParam(cs, "_BloomParams", BloomParams);
            cmd.SetComputeFloatParam(cs, "_BloomOn", bloom ? 1f : 0f);
            BloomWritten = bloom;
            // HDR display gain map (docs/hdr.md; Plugin.Hdr.cs), screen resolution.
            bool hdr = !HdrNative && HdrTarget != null && HdrTarget.width == Ws && HdrTarget.height == Hs;
            bool native = HdrNative;
            cmd.SetComputeTextureParam(cs, k, "_HdrOut", hdr ? HdrTarget : _bloomDummy);
            cmd.SetComputeVectorParam(cs, "_HdrParams", hdr || native ? HdrParams : Vector4.zero);
            cmd.SetComputeVectorParam(cs, "_HdrEmissive", HdrEmissive);
            cmd.SetComputeVectorParam(cs, "_HdrGame", HdrGame);
            bool gameColor = (hdr || native) && HdrGameColor != null && HdrGameColor.width == Ws && HdrGameColor.height == Hs;
            cmd.SetComputeTextureParam(cs, k, "_HdrGameColor", gameColor ? (Texture)HdrGameColor : Texture2D.blackTexture);
            HdrWritten = hdr;
            BindExposure(cmd, k, i);
            cmd.DispatchCompute(cs, k, Mathf.CeilToInt(Ws / 8f), Mathf.CeilToInt(Hs / 8f), 1);

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
            cmd.SetComputeTextureParam(_cs, k, "_DemodTex", _demod);
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

        private void EnsureTargets(int rw, int rh, int W, int H, int tw, int th, int Ws, int Hs)
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
                Release(ref _out);
                _out = Create(W, H, RenderTextureFormat.ARGBHalf, "UltraRT RR Output");
                _pOut = _out.GetNativeTexturePtr();
                _historyValid = false;
            }
            if (_final == null || _final.width != Ws || _final.height != Hs || !_final.IsCreated())
            {
                Release(ref _final);
                _final = Create(Ws, Hs, RenderTextureFormat.ARGBHalf, "UltraRT RR Final");
            }
            if (trace)
            {
                Release(ref _demod);
                _demod = Create(tw, th, RenderTextureFormat.ARGBHalf, "UltraRT RR Demod");
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
            Release(ref _hitDist); Release(ref _out); Release(ref _final); Release(ref _aeFiltered); Release(ref _aeAlbedo); Release(ref _demod);
            Release(ref _cutout); Release(ref _cutoutPrep);
            _demodUp?.Dispose();
            _demodUp = null;
            _dummy?.Release();
            _dummy = null;
        }
    }
}
