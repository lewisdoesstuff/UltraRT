using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// DLSS Ray Reconstruction in relight / pt-dn (docs/dlss.md): replaces the denoiser and the temporal upsampler. Hooks in
    /// Plugin.cs: ParseDlssArgs (Awake), LoadDlssShader (LoadShaders), DlssUpdate (OnUpdate), RayReconstructionFor and
    /// RunRayReconstruction (Trace).
    ///   config [DLSS] RayReconstruction (default off), -ultrart-rr / -ultrart-norr, Ctrl+F8 toggles in game
    ///   -ultrart-rr-preset N (0 default, 4 D, 5 E, 6 F), -ultrart-rr-unitylist (record into Unity's command list),
    ///   -ultrart-rr-jitter-sign S, -ultrart-rr-nohitdist, -ultrart-rr-gamma (feed gamma terms, no linearisation)
    /// Bench variants: plugin._rrEnabled=1, plugin._rrPreset=5, plugin._rrOwnList=0, plugin._rrJitterSign=1, ...
    /// </summary>
    public partial class Plugin
    {
        private DlssRR _rr;
        private bool _rrEnabled, _rrInitTried, _rrWasActive;
        private int _rrPreset;
        private bool _rrOwnList = true, _rrSpecHit = true, _rrLinear = true;
        private float _rrJitterSign = -1f;
        /// <summary>Bench cost probe (docs/dlss.md): RR output at this x the screen, e.g. 1.3333 with plugin._scale=0.6667 = 720p -> 1440p on a 1080p screen.</summary>
        private float _rrOutScale = 1f;
        private int _rrJitterPhases = 64; // Halton, as NVIDIA recommends; 4 = the TAAU pattern (no consistent difference measured, docs/dlss.md)
        private int _rrGen = -1;
        private float _rrLastUsed;
        private string _rrStatusLogged;
        private uint _rrFrame = uint.MaxValue - 1;
        private ComputeShader _rrShader, _rrUpsampleShader;
        /// <summary>RR reconstructs lighting; textures come back from exact texel hits at screen resolution (docs/dlss.md,
        /// "Remodulation"). -ultrart-rr-noremod, bench plugin._rrRemodulate=0.</summary>
        private bool _rrRemodulate = true;
        private float _rrDemodSharpness = 30f, _rrDemodMaxWeight = 4f;
        /// <summary>At an integer upscale (trace scale 1/2, 1/3, 1/4) jitter on the exact screen-pixel pattern instead of Halton
        /// (DlssRR.ExactGrid). Bench plugin._rrExactJitter=0.</summary>
        private bool _rrExactJitter = true;
        /// <summary>RR also sees the guard band (exact grid only; DlssRR.RenderGuard). Bench plugin._rrGuard=1.</summary>
        private bool _rrGuard;
        private const int SlotRR = 17, SlotRREval = 18;
        /// <summary>NGX init at plugin start and the feature's creation on a native worker thread, so the first RR frame doesn't
        /// stall the game for seconds (docs/dlss.md, "Warm-up"). -ultrart-rr-nowarm, bench plugin._rrWarm=0: the old lazy
        /// init and creation on the render thread.</summary>
        private bool _rrWarm = true;
        private bool _rrWarmLogged;
        /// <summary>This frame uses UltraRT's denoiser only because RR's feature is being created (Plugin.Remod.cs).</summary>
        private bool _rrWarming;
        private float _rrWarmSince = -1f;

        private void ParseDlssArgs()
        {
            _rrEnabled = Config.Bind("DLSS", "RayReconstruction", false,
                "Experimental: NVIDIA DLSS Ray Reconstruction replaces UltraRT's denoiser and upsampler in relight / pt-dn. " +
                "Needs nvngx_dlssd.dll next to UltraRT.Native.dll (docs/dlss.md). Ctrl+F8 toggles in game.").Value;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-rr": _rrEnabled = true; break;
                    case "-ultrart-norr": _rrEnabled = false; break;
                    case "-ultrart-rr-preset": int.TryParse(Next(), out _rrPreset); break;
                    case "-ultrart-rr-unitylist": _rrOwnList = false; break;
                    case "-ultrart-rr-nohitdist": _rrSpecHit = false; break;
                    case "-ultrart-rr-gamma": _rrLinear = false; break;
                    case "-ultrart-rr-noremod": _rrRemodulate = false; break;
                    case "-ultrart-rr-nowarm": _rrWarm = false; break;
                    case "-ultrart-rr-jitter-sign":
                        float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _rrJitterSign);
                        break;
                }
            }
            Logger.LogInfo($"DLSS Ray Reconstruction: {(_rrEnabled ? "on" : "off")} (preset {_rrPreset}, {(_rrOwnList ? "own" : "Unity's")} command list)");
        }

        private void LoadDlssShader(AssetBundle bundle)
        {
            _rrShader = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "RayReconstruction");
            _rrUpsampleShader = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Upsample"); // remodulation
            if (_rrShader == null) Logger.LogWarning("RayReconstruction compute shader missing from bundle: DLSS RR unavailable");
            _rr?.SetShader(_rrShader, _rrUpsampleShader);
        }

        private void DlssUpdate()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8) && CtrlHeld())
                {
                    _rrEnabled = !_rrEnabled;
                    string state = _rrEnabled ? (EnsureRR() ? "on" : "unavailable: " + (_rr?.Status ?? "see log")) : "off (UltraRT denoiser)";
                    Logger.LogInfo($"DLSS Ray Reconstruction: {state}");
                    Toast($"DLSS Ray Reconstruction {state} (Ctrl+F8)");
                }
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
            // Warm-up: start NGX's initialisation in the background as soon as the game runs, if anything may use RR.
            if (_rrWarm && !_rrInitTried && Time.frameCount > 2 && (_rrEnabled || _ptSettings.denoiser != PathTraceDenoiser.UltraRT) &&
                SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 && EnsureRR())
                _rr.WarmInit();
            var log = _rr?.TakeLog();
            if (log != null) foreach (var line in log.Split('\n')) Logger.LogInfo($"[RR] {line}");
            if (_rr != null && Time.frameCount % 60 == 0)
            {
                string st = _rr.Status;
                if (st != _rrStatusLogged) { _rrStatusLogged = st; Logger.LogInfo($"[RR] status: {st} (state {_rr.State}, {_rr.Evaluations} evaluations)"); }
            }
        }

        /// <summary>Bench: after a mode switch, wait until RR has its feature if the mode uses it (the warm-up creates it in the
        /// background and the frames meanwhile use UltraRT's denoiser), so screenshots, bursts and motion passes see RR.</summary>
        private IEnumerator WaitForRR()
        {
            for (int f = 0; f < 2; f++) yield return null;
            float until = Time.realtimeSinceStartup + 20f;
            while (_rr != null && (_rr.Busy || _rrWarmSince >= 0f) && Time.realtimeSinceStartup < until) { Bench.GameControl.HideCheatPanels(); yield return null; }
        }

        /// <summary>Main thread, lazily on first use: load the native side and point NGX at nvngx_dlssd.dll.</summary>
        private bool EnsureRR()
        {
            if (_rr != null) return _rr.State >= 0;
            if (_rrInitTried) return false;
            _rrInitTried = true;
            string dir = Path.GetDirectoryName(Info.Location) ?? "";
            string data = Path.Combine(Paths.BepInExRootPath, "ultrart", "ngx");
            if (!DlssRR.TryCreate(dir, data, out _rr, out var err))
            {
                Logger.LogWarning($"DLSS Ray Reconstruction unavailable: {err}");
                _rr = null;
                return false;
            }
            _rr.SetShader(_rrShader, _rrUpsampleShader);
            Logger.LogInfo($"DLSS Ray Reconstruction: native side loaded ({_rr.Status}); NGX initialises on the first frame");
            return true;
        }

        /// <summary>Trace(): whether RR replaces the denoiser and upsampler this frame.</summary>
        private bool RayReconstructionFor(Kind kind, RenderTexture depthBuf)
        {
            // relight / pt-dn: the [DLSS] switch (Ctrl+F8). pathtrace: its own denoiser choice (RR when available by default).
            string name = Modes[_mode].name;
            bool asked = name == PathTraceModeName ? PathTraceWantsRR : _rrEnabled && (name == "relight" || name == "pt-dn");
            bool want = asked && kind == Kind.Denoised && depthBuf != null && _rrShader != null;
            if (want) want = EnsureRR() && _rr.HasShader;
            bool warming = false;
            // Unused for a few seconds (mode switch, Ctrl+F8): release the feature, ~200 MB of RR history at 1440p. Not at once,
            // so the bench's alternating modes don't recreate it every segment.
            if (want) { _rrWasActive = true; _rrLastUsed = Time.realtimeSinceStartup; }
            else if (_rrWasActive && _rr != null && Time.realtimeSinceStartup - _rrLastUsed > 5f) {
                // Its own buffer: Trace() clears _cmd after this point.
                var cb = new UnityEngine.Rendering.CommandBuffer { name = "UltraRT RR release" };
                _rr.ReleaseFeature(cb);
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                _rrWasActive = false;
            }
            if (want)
            {
                _rr.Preset = _rrPreset;
                _rr.OwnCommandList = _rrOwnList;
                _rr.JitterSign = _rrJitterSign;
                _rr.SpecularHitDistance = _rrSpecHit;
                _rr.Linearize = _rrLinear;
                _rr.OutScale = _rrOutScale;
                _rr.Remodulate = _rrRemodulate;
                float up = 1f / Mathf.Max(0.05f, name == PathTraceModeName ? _ptSettings.scaleRR : _scale);
                int grid = Mathf.RoundToInt(up);
                _rr.ExactGrid = _rrExactJitter && _rrRemodulate && grid >= 2 && grid <= 5 && Mathf.Abs(up - grid) < 0.02f ? grid : 0;
                _rr.RenderGuard = _rrGuard;
                _rr.DemodSharpness = _rrDemodSharpness;
                _rr.DemodMaxWeight = _rrDemodMaxWeight;
                // NVIDIA's rule: 8 x (output / render)^2 phases (72 at 3x); never fewer than the configured count.
                _rr.JitterPhases = name == PathTraceModeName ? Mathf.Max(_rrJitterPhases, Mathf.CeilToInt(8f / Mathf.Max(0.04f, _ptSettings.scaleRR * _ptSettings.scaleRR))) : _rrJitterPhases;
                if (_rrWarm)
                {
                    // The feature for this frame's sizes must exist: if not, it is created on the native worker thread and this
                    // frame (and the next few) use UltraRT's denoiser instead of the render thread stalling.
                    bool full = name == PathTraceModeName;
                    TraceDims(true, TraceScaleFor(full, true), _rr.ExactGrid, depthBuf, out int tw, out int th, out int gx, out int gy, out var gs, out _, out _);
                    warming = !_rr.EnsureFeatureAsync(tw, th, new Vector2Int(gx, gy), gs, depthBuf.width, depthBuf.height);
                }
            }
            _rrWarming = warming;
            if (warming)
            {
                if (_rrWarmSince < 0f) _rrWarmSince = Time.realtimeSinceStartup;
                if (!_rrWarmLogged) { _rrWarmLogged = true; Logger.LogInfo("DLSS Ray Reconstruction: creating the feature in the background; UltraRT's denoiser until it is ready"); }
                _rrLastUsed = Time.realtimeSinceStartup; // not released meanwhile
                return false;
            }
            if (want && _rrWarmSince >= 0f)
            {
                Logger.LogInfo($"DLSS Ray Reconstruction: ready after {(Time.realtimeSinceStartup - _rrWarmSince) * 1000f:F0} ms of warm-up (frame {Time.frameCount})");
                _rrWarmSince = -1f;
                _rrWarmLogged = false;
            }
            return want;
        }

        /// <summary>
        /// RR at an integer upscale N (DlssRR.ExactGrid): size the trace so that one trace pixel is exactly N screen pixels
        /// and the jitter pattern lands on screen pixel centres. The screen is rarely a multiple of N (2560 / 3), so the
        /// visible part of the trace becomes a whole number of trace pixels that overscans the screen by a fraction of one on
        /// each side, on top of the guard band; _guardScale carries the exact ratio to everything downstream.
        /// </summary>
        private void ExactGridTrace(int n, int screenW, int screenH, int gx, int gy, ref int w, ref int h, ref Vector2 guardScale)
        {
            if (n <= 2) return;
            int vw = Mathf.CeilToInt((float)screenW / n), vh = Mathf.CeilToInt((float)screenH / n);
            // The overscan must be an even number of screen pixels, so the screen sits centred on the grid.
            if ((vw * n - screenW) % 2 != 0) vw++;
            if ((vh * n - screenH) % 2 != 0) vh++;
            if ((vw * n - screenW) % 2 != 0 || (vh * n - screenH) % 2 != 0) return; // even N on an odd screen size: keep the plain trace
            w = vw + 2 * gx;
            h = vh + 2 * gy;
            guardScale = new Vector2((float)w * n / screenW, (float)h * n / screenH);
        }

        /// <summary>Trace(), after BindGameDepth: RR, then the composite reads _UltraRTUpsampled. False = nothing recorded.</summary>
        private bool RunRayReconstruction(int w, int h, Matrix4x4 viewProj, Vector2Int guardPx)
        {
            // History goes with the denoiser's generation (scene change, bench resets) and is stale after skipped frames.
            if (_denoiser.Generation != _rrGen || _rrFrame != _frameIndex - 1) _rr.Reset();
            _rrGen = _denoiser.Generation;
            _rrFrame = _frameIndex;
            var inputs = new DlssRR.Inputs
            {
                radiance = _gRadiance, albedo = _gAlbedo, emission = _gEmission, specular = _gSpecular, normalDepth = _gNormalDepth,
                instanceId = _gInstanceId, surface = _gSurface, reflection = _gReflection, litWeight = _gHybrid, gameDepth = _depthCopy,
                camera = _camera, viewProj = viewProj, guardPx = guardPx, guardScale = _guardScale, reflParams = _reflSettings.ShaderParams,
                exposure = _autoExposure?.ExposureBuffer,
                frameTime = Denoiser.FrameTimeOverride > 0f ? Denoiser.FrameTimeOverride : Time.unscaledDeltaTime,
            };
            _gpu?.Begin(_cmd, SlotRR);
            bool ok = _rr.Execute(_cmd, inputs, _gpu, SlotRREval);
            _gpu?.End(_cmd, SlotRR);
            if (!ok || !_rr.BloomWritten) _vfxHdr = null; // screen effects: no HDR bloom source this frame (Plugin.Vfx.cs)
            if (!ok) return false;
            _cmd.SetGlobalTexture(UltraRTUpsampledId, _rr.Final);
            _displayed = _rr.Final;

            // Relight auto exposure: meter the RR result (the denoiser's FilteredLighting doesn't exist this frame).
            if (AutoExposureFor(Modes[_mode].name))
            {
                if (Time.frameCount - _aeLastFrame > 2) _autoExposure.Reset();
                _aeLastFrame = Time.frameCount;
                _gpu?.Begin(_cmd, SlotExposure);
                _rr.ExposureInput(_cmd, inputs, out var filtered, out var albedo);
                float dt = _aeBenchClock && Denoiser.FrameTimeOverride > 0f ? Denoiser.FrameTimeOverride : Time.unscaledDeltaTime;
                BindGameMatch(); // the game's frame before our composite: game match and area colour match, as without RR
                _autoExposure.Execute(_cmd, _aeSettings, filtered, albedo, null, _gNormalDepth, _gHybrid, w, h, dt);
                _gpu?.End(_cmd, SlotExposure);
            }
            return true;
        }
    }
}
