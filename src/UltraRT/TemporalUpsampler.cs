using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UltraRT
{
    /// <summary>Tunables of <see cref="TemporalUpsampler"/>, read every Execute (docs/denoiser.md, "Temporal upsampling").</summary>
    public sealed class UpsamplerSettings
    {
        /// <summary>Master switch (F2, -ultrart-notaau). Off = the previous depth-guided 2x2 nearest-texel composite.</summary>
        public bool enabled = true;
        /// <summary>Jitter the trace grid over the screen pixels (off: the upsampler only stabilises the unjittered samples).</summary>
        public bool jitter = true;
        /// <summary>New-sample weight exp(-sharpness d^2), d = distance to the pixel centre in screen px. 4: an exact hit counts
        /// 1, a sample one pixel away 0.02, so textures and edges stay pixel sharp.</summary>
        public float sharpness = 4f;
        /// <summary>History weight cap, in exact hits. Higher = smoother, slower to follow changes the clip box misses.</summary>
        public float maxWeight = 3f;
        /// <summary>Spatial fallback (no history): Gaussian sigma in trace px over the depth-matched samples.</summary>
        public float spatialSigma = 0.6f;
        /// <summary>Weight of the spatial fallback added every frame (keeps a new pixel from showing only its nearest sample).</summary>
        public float spatialWeight = 0.05f;
        /// <summary>A trace sample belongs to a screen pixel's surface when |its eye depth - the game's| &lt;= this x depth + depthToleranceAbs
        /// (the old composite's discard test).</summary>
        public float depthTolerance = 0.03f;
        public float depthToleranceAbs = 0.05f;
        /// <summary>History tap depth tolerance (relative to the expected eye depth).</summary>
        public float reprojTolerance = 0.02f;
        /// <summary>History clip box = the matching samples' min/max, padded by this fraction of its size.</summary>
        public float clipPadding = 0.1f;
        /// <summary>Clip only with at least this many matching samples (an edge pixel with 1-2 would lose its history every frame).</summary>
        public int clipMinSamples = 3;
        /// <summary>Catmull-Rom history resampling where all 4 bilinear taps are valid (sharper in motion).</summary>
        public bool catmullRom = true;
        /// <summary>A pixel no trace sample matched this frame keeps its history (thin geometry the jittered grid missed) if the
        /// history weight is at least this; the held weight is multiplied by holdDecay each frame.</summary>
        public float holdMinWeight = 0.1f;
        public float holdDecay = 0.95f;
        /// <summary>Debug: 1 = r matching samples, g history weight, b history kept without a sample; 2 = game depth.</summary>
        public int debugView;

        public static UpsamplerSettings FromCommandLine()
        {
            var s = new UpsamplerSettings();
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                float F(float fallback) => float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-notaau": s.enabled = false; break;
                    case "-ultrart-taau-nojitter": s.jitter = false; break;
                    case "-ultrart-taau-weight": s.maxWeight = F(s.maxWeight); break;
                    case "-ultrart-taau-sharpness": s.sharpness = F(s.sharpness); break;
                }
            }
            return s;
        }
    }

    /// <summary>
    /// Temporal upsampler for the replace modes: accumulates the denoiser's trace-resolution colour, traced on a jittered
    /// grid, into a screen-resolution history guided by the game's depth buffer (Upsample.compute). Owns the jitter sequence.
    /// </summary>
    internal sealed class TemporalUpsampler : IDisposable
    {
        public UpsamplerSettings Settings = UpsamplerSettings.FromCommandLine();
        /// <summary>The accumulated value is not a displayed colour: keep values above 1 (DLSS RR's demodulation colour, DlssRR.cs).</summary>
        public bool Unclamped;

        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private readonly RenderTexture[] _hist = new RenderTexture[2];
        private readonly RenderTexture[] _depth = new RenderTexture[2];
        private int _cur;
        private bool _historyValid;
        private Matrix4x4 _prevScreenVP;
        private uint _jitterIndex;

        /// <summary>The upsampled colour of the last Execute (screen res; a = 0 where there is no RT surface).</summary>
        public RenderTexture Output { get; private set; }

        private static readonly int
            IdColor = Shader.PropertyToID("_Color"),
            IdNormalDepth = Shader.PropertyToID("_NormalDepth"),
            IdInstanceId = Shader.PropertyToID("_InstanceId"),
            IdGameDepth = Shader.PropertyToID("_GameDepth"),
            IdHistIn = Shader.PropertyToID("_HistIn"),
            IdHistDepthIn = Shader.PropertyToID("_HistDepthIn"),
            IdHistOut = Shader.PropertyToID("_HistOut"),
            IdHistDepthOut = Shader.PropertyToID("_HistDepthOut"),
            IdOutDim = Shader.PropertyToID("_OutDim"),
            IdTraceDim = Shader.PropertyToID("_TraceDim"),
            IdTraceMap = Shader.PropertyToID("_TraceMap"),
            IdJitter = Shader.PropertyToID("_Jitter"),
            IdTraceRay = Shader.PropertyToID("_TraceRay"),
            IdZParams = Shader.PropertyToID("_ZParams"),
            IdInvScreenVP = Shader.PropertyToID("_InvScreenVP"),
            IdPrevScreenVP = Shader.PropertyToID("_PrevScreenVP"),
            IdCamPos = Shader.PropertyToID("_CamPos"),
            IdCamFwd = Shader.PropertyToID("_CamFwd"),
            IdParams = Shader.PropertyToID("_Params"),
            IdParams2 = Shader.PropertyToID("_Params2"),
            IdParams3 = Shader.PropertyToID("_Params3"),
            IdParams4 = Shader.PropertyToID("_Params4");

        public TemporalUpsampler(ComputeShader shader)
        {
            _shader = shader ? shader : throw new ArgumentNullException(nameof(shader));
            _kernel = shader.FindKernel("Taau");
        }

        public void Reset() => _historyValid = false;

        /// <summary>Position in the jitter sequence (bench: motion-noise passes start at a chosen phase).</summary>
        public uint JitterIndex { get => _jitterIndex; set => _jitterIndex = value; }

        /// <summary>
        /// This frame's sample offset from the trace texel centre, in trace px. When the screen is exactly twice the visible
        /// trace size, the 4 offsets (+-0.25, +-0.25) put the samples on the 4 screen pixel centres of each 2x2 block in turn;
        /// otherwise an 8-phase Halton(2, 3) sequence over the texel.
        /// </summary>
        public Vector2 NextJitter(int screenW, int screenH, int traceVisW, int traceVisH)
        {
            uint i = _jitterIndex++;
            if (screenW == 2 * traceVisW && screenH == 2 * traceVisH)
            {
                // Diagonal pairs first, so consecutive frames differ in both axes.
                switch (i & 3)
                {
                    case 0: return new Vector2(-0.25f, -0.25f);
                    case 1: return new Vector2(0.25f, 0.25f);
                    case 2: return new Vector2(0.25f, -0.25f);
                    default: return new Vector2(-0.25f, 0.25f);
                }
            }
            uint k = (i & 7) + 1;
            return new Vector2(Halton(k, 2) - 0.5f, Halton(k, 3) - 0.5f);
        }

        private static float Halton(uint i, uint b)
        {
            float f = 1f, r = 0f;
            while (i > 0) { f /= b; r += f * (i % b); i /= b; }
            return r;
        }

        /// <summary>
        /// Records the upsampler. <paramref name="color"/>, <paramref name="normalDepth"/> and <paramref name="instanceId"/> are
        /// the trace-resolution inputs (guard band included), traced with <paramref name="jitter"/>; <paramref name="gameDepth"/>
        /// is a copy of the game's depth buffer (the output gets its size). <paramref name="guardPx"/>: guard band per side in
        /// trace px; <paramref name="guardScale"/>: traced / visible size.
        /// </summary>
        /// <param name="traceMap">Optional: the screen-to-trace map (trace px per screen px, then the trace position of the screen's
        /// corner) when it is not the integer guard band, e.g. DLSS RR's exact-grid trace (DlssRR.cs).</param>
        public void Execute(CommandBuffer cmd, Camera cam, RenderTexture color, RenderTexture normalDepth, RenderTexture instanceId,
                            RenderTexture gameDepth, Vector2Int guardPx, Vector2 guardScale, Vector2 jitter, bool resetHistory, Vector4? traceMap = null)
        {
            int W = gameDepth.width, H = gameDepth.height;
            EnsureTargets(W, H);
            if (resetHistory) _historyValid = false;
            var s = Settings;
            var cs = _shader;
            int k = _kernel;
            int prev = _cur ^ 1;

            var proj = cam.projectionMatrix;
            var screenVP = proj * cam.worldToCameraMatrix;
            float n = cam.nearClipPlane, f = cam.farClipPlane;
            Vector4 z = SystemInfo.usesReversedZBuffer
                ? new Vector4(-1f + f / n, 1f, (-1f + f / n) / f, 1f / f)
                : new Vector4(1f - f / n, f / n, (1f - f / n) / f, f / n / f);
            int tw = color.width, th = color.height;
            int visW = tw - 2 * guardPx.x, visH = th - 2 * guardPx.y;
            float sharp = Mathf.Max(0f, s.sharpness);
            // Spatial sigma is given in trace px: convert to screen px.
            float sigmaScreen = Mathf.Max(0.05f, s.spatialSigma) * W / Mathf.Max(1, visW);

            cmd.SetComputeVectorParam(cs, IdOutDim, new Vector4(W, H, 1f / W, 1f / H));
            cmd.SetComputeVectorParam(cs, IdTraceDim, new Vector4(tw, th, 1f / tw, 1f / th));
            cmd.SetComputeVectorParam(cs, IdTraceMap, traceMap ?? new Vector4((float)visW / W, (float)visH / H, guardPx.x, guardPx.y));
            cmd.SetComputeVectorParam(cs, IdJitter, jitter);
            cmd.SetComputeVectorParam(cs, IdTraceRay, new Vector4(guardScale.x / proj.m00, guardScale.y / proj.m11, 0f, 0f));
            cmd.SetComputeVectorParam(cs, IdZParams, z);
            cmd.SetComputeMatrixParam(cs, IdInvScreenVP, screenVP.inverse);
            cmd.SetComputeMatrixParam(cs, IdPrevScreenVP, _historyValid ? _prevScreenVP : screenVP);
            cmd.SetComputeVectorParam(cs, IdCamPos, cam.transform.position);
            cmd.SetComputeVectorParam(cs, IdCamFwd, cam.transform.forward);
            cmd.SetComputeVectorParam(cs, IdParams, new Vector4(sharp, Mathf.Max(0.1f, s.maxWeight), 0.5f / (sigmaScreen * sigmaScreen), _historyValid ? 1f : 0f));
            cmd.SetComputeVectorParam(cs, IdParams2, new Vector4(Mathf.Max(0f, s.depthTolerance), Mathf.Max(0f, s.depthToleranceAbs), Mathf.Max(0f, s.clipPadding), Mathf.Max(0, s.clipMinSamples)));
            cmd.SetComputeVectorParam(cs, IdParams3, new Vector4(Mathf.Max(0f, s.reprojTolerance), s.catmullRom ? 1f : 0f, Mathf.Max(1e-3f, s.spatialWeight), s.debugView));
            cmd.SetComputeVectorParam(cs, IdParams4, new Vector4(Mathf.Max(1e-3f, s.holdMinWeight), Mathf.Clamp01(s.holdDecay), Unclamped ? 1f : 0f, 0f));
            cmd.SetComputeTextureParam(cs, k, IdColor, color);
            cmd.SetComputeTextureParam(cs, k, IdNormalDepth, normalDepth);
            cmd.SetComputeTextureParam(cs, k, IdInstanceId, instanceId);
            cmd.SetComputeTextureParam(cs, k, IdGameDepth, gameDepth, 0, RenderTextureSubElement.Depth);
            cmd.SetComputeTextureParam(cs, k, IdHistIn, _hist[prev]);
            cmd.SetComputeTextureParam(cs, k, IdHistDepthIn, _depth[prev]);
            cmd.SetComputeTextureParam(cs, k, IdHistOut, _hist[_cur]);
            cmd.SetComputeTextureParam(cs, k, IdHistDepthOut, _depth[_cur]);
            cmd.DispatchCompute(cs, k, Mathf.CeilToInt(W / 8f), Mathf.CeilToInt(H / 8f), 1);

            Output = _hist[_cur];
            _prevScreenVP = screenVP;
            _historyValid = true;
            _cur = prev;
        }

        private void EnsureTargets(int w, int h)
        {
            if (_hist[0] != null && _hist[0].width == w && _hist[0].height == h && _hist[0].IsCreated()) return;
            ReleaseTargets();
            for (int i = 0; i < 2; i++)
            {
                _hist[i] = Create(w, h, RenderTextureFormat.ARGBHalf, $"UltraRT Upsample History {i}");
                _depth[i] = Create(w, h, RenderTextureFormat.RFloat, $"UltraRT Upsample Depth {i}");
            }
            _cur = 0;
            _historyValid = false;
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

        private void ReleaseTargets()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_hist[i] != null) { _hist[i].Release(); Object.Destroy(_hist[i]); _hist[i] = null; }
                if (_depth[i] != null) { _depth[i].Release(); Object.Destroy(_depth[i]); _depth[i] = null; }
            }
            Output = null;
            _historyValid = false;
        }

        public void Dispose() => ReleaseTargets();
    }
}
