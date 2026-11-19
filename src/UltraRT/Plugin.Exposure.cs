using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Relight auto exposure and tone map wiring (docs/relight.md, "Auto exposure"). Kept in its own file so Plugin.cs only
    /// has one-line hooks: ParseExposureArgs (Awake), LoadExposureShader (LoadShaders), _autoExposure.Reset (ResetScene),
    /// ExposureHotkeys (OnUpdate), BindExposure (Trace, before the denoiser), RunExposure (Trace, after the denoiser).
    ///   -ultrart-relight-autoexposure on|off   (config [Relight] AutoExposure)
    ///   -ultrart-ae-key K, -ultrart-ae-min EV, -ultrart-ae-max EV, -ultrart-ae-speed UP[,DOWN], -ultrart-ae-band LO,HI,
    ///   -ultrart-ae-comp EV, -ultrart-ae-ptdn
    ///   -ultrart-relight-tonemap film[,K]|lum|max   film (default): luminance shoulder + highlight rolloff to white, K = the
    ///                                          share of its room a channel may use untouched (0.8); lum: the shoulder alone
    ///                                          (saturated colours clip per channel); max: shoulder on the largest channel
    ///   -ultrart-relight-knee K                where the shoulder starts (default 0.75)
    ///   -ultrart-relight-toe S[,END]           toe strength (0 = none) and where it joins the identity
    /// Ctrl+F4: toggle auto exposure.
    /// </summary>
    public partial class Plugin
    {
        private readonly AutoExposureSettings _aeSettings = new AutoExposureSettings();
        private AutoExposure _autoExposure;
        private ComputeShader _denoiseShader;
        private int _denoiseCompositeKernel = -1;
        private int _aeLastFrame = -10;
        private float _tonemapCurve = 2f; // 0 = luminance shoulder (the original curve), 1 = max-channel shoulder, 2 = film (default since 2026-11-18)
        private float _tonemapSat = 0.8f; // film: share of the room above luminance a channel may use before the rolloff (UltraRTToneCurve.hlsl)
        private float _tonemapKnee = 0.75f;
        private float _tonemapToe = 0.6f, _tonemapToeEnd = 0.25f; // toe strength (0 = none; 0.6 since the punch defaults, 2026-11-17) and where it ends (UltraRTToneCurve.hlsl)

        private static readonly int
            UltraRTAutoExposureId = Shader.PropertyToID("_UltraRTAutoExposure"),
            UltraRTTonemapParamsId = Shader.PropertyToID("_UltraRTTonemapParams");

        private void ParseExposureArgs()
        {
            _aeSettings.enabled = Config.Bind("Relight", "AutoExposure", true,
                "Relight mode: adapt exposure to the scene so sunlit outdoor areas don't clip (docs/relight.md). " +
                "Overridden by -ultrart-relight-autoexposure on|off; Ctrl+F4 toggles in game.").Value;
            _aeSettings.ParseArgs();
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-relight-tonemap":
                    {
                        var p = (Next() ?? "").ToLowerInvariant().Split(',');
                        _tonemapCurve = p[0] == "max" ? 1f : p[0] == "lum" ? 0f : 2f;
                        if (p.Length > 1 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ks)) _tonemapSat = Mathf.Clamp(ks, 0.05f, 0.98f);
                        break;
                    }
                    case "-ultrart-relight-knee":
                        if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) _tonemapKnee = Mathf.Clamp(k, 0.05f, 0.99f);
                        break;
                    case "-ultrart-relight-toe":
                    {
                        var p = (Next() ?? "").Split(',');
                        if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) _tonemapToe = Mathf.Clamp01(t);
                        if (p.Length > 1 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var te)) _tonemapToeEnd = Mathf.Clamp(te, 0.01f, 0.7f);
                        break;
                    }
                }
            }
            InitHdr(); // HDR display output (Plugin.Hdr.cs, docs/hdr.md)
            Logger.LogInfo($"Relight: {_aeSettings}; tone map {(_tonemapCurve > 1.5f ? $"film (rolloff from {_tonemapSat})" : _tonemapCurve > 0.5f ? "max-channel shoulder" : "luminance shoulder")}, knee {_tonemapKnee}, white {_relightWhite}, toe {_tonemapToe} to {_tonemapToeEnd}");
        }

        private void LoadExposureShader(AssetBundle bundle, ComputeShader denoise)
        {
            _denoiseShader = denoise;
            _denoiseCompositeKernel = denoise.FindKernel("Composite");
            _autoExposure?.Dispose();
            _autoExposure = null;
            var cs = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Exposure");
            if (cs == null) { Logger.LogWarning("Exposure compute shader missing from bundle: relight auto exposure off"); return; }
            _autoExposure = new AutoExposure(cs);
        }

        private void ExposureHotkeys()
        {
            if (!Input.GetKeyDown(KeyCode.F4) || !CtrlHeld()) return;
            _aeSettings.enabled = !_aeSettings.enabled;
            _autoExposure?.Reset();
            Logger.LogInfo($"Relight auto exposure: {(_aeSettings.enabled ? "on" : "off")}");
            Toast($"Auto exposure {(_aeSettings.enabled ? "on" : "off")} (Ctrl+F4)");
        }

        private static bool ShiftHeld()
        {
            try { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }
            catch (InvalidOperationException) { return false; }
        }

        private static bool CtrlHeld()
        {
            try { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Whether auto exposure applies to a view mode name.</summary>
        private bool AutoExposureFor(string mode) =>
            _autoExposure != null && _aeSettings.enabled && (RelightLook(mode) || (_aeSettings.ptDn && mode == "pt-dn"));

        /// <summary>Before the denoiser: bind the exposure on its Composite kernel and set the tone map globals.</summary>
        private void BindExposure()
        {
            string name = Modes[_mode].name;
            bool ae = AutoExposureFor(name);
            if (_denoiseShader != null && _denoiseCompositeKernel >= 0)
            {
                // The kernel declares these in every mode, so they are always bound.
                var buffer = _autoExposure != null ? _autoExposure.ExposureBuffer : null;
                if (buffer != null)
                {
                    AutoExposure.BindComposite(_cmd, _denoiseShader, _denoiseCompositeKernel, buffer, _gLitWeight);
                    _cmd.SetGlobalBuffer("_UltraRTExposureGlobal", buffer); // the water/glass overlay (TransparentOverlay.shader)
                }
                else ae = false;
            }
            _cmd.SetGlobalFloat(UltraRTAutoExposureId, ae ? 1f : 0f);
            bool tonemap = _relightActive || ae;
            _cmd.SetGlobalFloat("_UltraRTTonemapWhite", tonemap ? _relightWhite : 0f);
            _cmd.SetGlobalVector(UltraRTTonemapParamsId, new Vector4(_tonemapCurve > 1.5f ? 2f + Mathf.Clamp(_tonemapSat, 0.05f, 0.98f) : _tonemapCurve, _tonemapKnee, _tonemapToe, Mathf.Min(_tonemapToeEnd, _tonemapKnee)));
        }

        /// <summary>After the denoiser: measure this frame and adapt (used from the next frame on).</summary>
        private void RunExposure(int w, int h)
        {
            if (!AutoExposureFor(Modes[_mode].name) || _denoiser.FilteredLighting == null) return;
            if (Time.frameCount - _aeLastFrame > 2) _autoExposure.Reset(); // first frame after another mode or the view off
            _aeLastFrame = Time.frameCount;
            _gpu?.Begin(_cmd, SlotExposure); // nested inside the denoise slot
            // The bench's bursts capture far below real time and set FrameTimeOverride so the denoiser blends as at 360 Hz;
            // adapt on the same clock, or every slow captured frame takes a large step after the noisy meter.
            float dt = _aeBenchClock && Denoiser.FrameTimeOverride > 0f ? Denoiser.FrameTimeOverride : Time.unscaledDeltaTime;
            // Offline cinematic render: metered once per output frame, on a 1 / fps clock (Plugin.CineRender.cs).
            if (!CineExposureGate(ref dt)) { _aeLastFrame = Time.frameCount; _gpu?.End(_cmd, SlotExposure); return; }
            _aeSettings.hdrActive = HdrActive; // an HDR display needs less darkening (AutoExposureSettings.EffectiveKey)
            BindGameMatch();
            _autoExposure.Execute(_cmd, _aeSettings, _denoiser.FilteredLighting, _gAlbedo, _gSpecular, _gNormalDepth, _gLitWeight,
                w, h, dt);
            _gpu?.End(_cmd, SlotExposure);
        }

        private bool _hdrRequested;
        /// <summary>Bench variant plugin._aeBenchClock=0: adapt on real time during bursts (the old behaviour).</summary>
        private bool _aeBenchClock = true;

        /// <summary>
        /// Logs what Unity says about HDR output. Unity 2022.3 only allows it when the player was built with
        /// PlayerSettings.allowHDRDisplaySupport (ULTRAKILL's globalgamemanagers: allowHDRDisplaySupport = False,
        /// useHDRDisplay = False), so HDROutputSettings.main.available stays false whatever the display supports; UltraRT's
        /// HDR output replaces the swapchain underneath Unity instead (Plugin.Hdr.cs, docs/hdr.md).
        /// </summary>
        private void LogHdrDisplay()
        {
            try
            {
                var hdr = HDROutputSettings.main;
                string info = $"HDR display: available={hdr.available} active={(hdr.available && hdr.active)} " +
                              $"displays={HDROutputSettings.displays?.Length ?? 0} supportFlags={SystemInfo.hdrDisplaySupportFlags} " +
                              $"backbuffer={SystemInfo.GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat.HDR)}/{SystemInfo.GetGraphicsFormat(UnityEngine.Experimental.Rendering.DefaultFormat.LDR)}";
                if (hdr.available)
                    info += $" gamut={hdr.displayColorGamut} format={hdr.graphicsFormat} paperWhite={hdr.paperWhiteNits} " +
                            $"maxLum={hdr.maxToneMapLuminance} maxFullFrame={hdr.maxFullFrameToneMapLuminance} minLum={hdr.minToneMapLuminance}";
                Logger.LogInfo(info); // Unity's own view; UltraRT's HDR output does not go through it (Plugin.Hdr.cs)
            }
            catch (Exception e)
            {
                Logger.LogWarning($"HDR display probe failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>For logs and the bench: the last read-back exposure state.</summary>
        private string ExposureSummary()
        {
            if (_autoExposure == null) return "auto exposure unavailable";
            var e = _autoExposure.Last;
            var m = _autoExposure.LastMatch;
            return FormattableString.Invariant(
                $"exposure x{e.x:F3} ({e.y:+0.00;-0.00} EV, target {e.w:+0.00;-0.00}), mean lum {Mathf.Pow(2f, e.z):F3}, {_autoExposure.LastCount:F0} px; ") +
                FormattableString.Invariant($"game match: game {m.x:+0.00;-0.00} vs RT {m.y:+0.00;-0.00} log2 over {m.w:F0} near px, correction {m.z:+0.00;-0.00} EV; ") +
                FormattableString.Invariant($"colour match gain ({_autoExposure.LastBalance.x:F2}, {_autoExposure.LastBalance.y:F2}, {_autoExposure.LastBalance.z:F2})");
        }

        private RenderTexture _aeGameCopy;

        /// <summary>
        /// Game match (docs/relight.md, "Game match"): a copy of the game's frame as it is now (opaques and sky, before the
        /// RT composite draws over it) for the exposure histogram, plus the trace-to-screen map and the game's fog.
        /// </summary>
        private void BindGameMatch()
        {
            var s = _aeSettings;
            var gameTex = (s.gameMatchUp > 0f || s.gameMatchDown > 0f || s.colorMatch > 0f) ? MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex : null;
            if (gameTex == null) { _autoExposure.GameColor = null; return; }
            if (_aeGameCopy == null || _aeGameCopy.width != gameTex.width || _aeGameCopy.height != gameTex.height || _aeGameCopy.format != gameTex.format)
            {
                if (_aeGameCopy != null) _aeGameCopy.Release();
                var desc = gameTex.descriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.enableRandomWrite = false;
                _aeGameCopy = new RenderTexture(desc) { name = "UltraRT AE GameColor", filterMode = FilterMode.Point };
                _aeGameCopy.Create();
            }
            _cmd.CopyTexture(gameTex, _aeGameCopy);
            _autoExposure.GameColor = _aeGameCopy;
            _autoExposure.GameMap = new Vector4(_guardScale.x, _guardScale.y, _flipY ? 1f : 0f, 1f);
            var mode = RenderSettings.fogMode;
            _autoExposure.GameFog = new Vector4(RenderSettings.fog ? 1f : 0f, mode == FogMode.Linear ? 1f : mode == FogMode.Exponential ? 2f : 3f,
                mode == FogMode.Linear ? RenderSettings.fogStartDistance : RenderSettings.fogDensity, RenderSettings.fogEndDistance);
        }
    }
}
