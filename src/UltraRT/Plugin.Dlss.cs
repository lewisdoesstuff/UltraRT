using System;
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
        private int _rrGen = -1;
        private float _rrLastUsed;
        private string _rrStatusLogged;
        private uint _rrFrame = uint.MaxValue - 1;
        private ComputeShader _rrShader;
        private const int SlotRR = 17, SlotRREval = 18;

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
            if (_rrShader == null) Logger.LogWarning("RayReconstruction compute shader missing from bundle: DLSS RR unavailable");
            _rr?.SetShader(_rrShader);
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
            var log = _rr?.TakeLog();
            if (log != null) foreach (var line in log.Split('\n')) Logger.LogInfo($"[RR] {line}");
            if (_rr != null && Time.frameCount % 60 == 0)
            {
                string st = _rr.Status;
                if (st != _rrStatusLogged) { _rrStatusLogged = st; Logger.LogInfo($"[RR] status: {st} (state {_rr.State}, {_rr.Evaluations} evaluations)"); }
            }
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
            _rr.SetShader(_rrShader);
            Logger.LogInfo($"DLSS Ray Reconstruction: native side loaded ({_rr.Status}); NGX initialises on the first frame");
            return true;
        }

        /// <summary>Trace(): whether RR replaces the denoiser and upsampler this frame.</summary>
        private bool RayReconstructionFor(Kind kind, RenderTexture depthBuf)
        {
            bool want = _rrEnabled && kind == Kind.Denoised && depthBuf != null && _rrShader != null;
            if (want)
            {
                string name = Modes[_mode].name;
                want = (name == "relight" || name == "pt-dn") && EnsureRR() && _rr.HasShader;
            }
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
            }
            return want;
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
                _autoExposure.Execute(_cmd, _aeSettings, filtered, albedo, null, _gNormalDepth, _gHybrid, w, h, dt);
                _gpu?.End(_cmd, SlotExposure);
            }
            return true;
        }
    }
}
