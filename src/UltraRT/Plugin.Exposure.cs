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
    ///   -ultrart-relight-tonemap lum|max       shoulder on luminance (the old curve) or on the largest channel
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
        private float _tonemapCurve = 0f; // 0 = luminance shoulder (default, the original curve), 1 = max-channel shoulder
        private float _tonemapKnee = 0.75f;
        private float _tonemapToe = 0f, _tonemapToeEnd = 0.25f; // toe strength (0 = none) and where it ends (UltraRTToneCurve.hlsl)

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
                    case "-ultrart-relight-tonemap": _tonemapCurve = (Next() ?? "").ToLowerInvariant() == "max" ? 1f : 0f; break;
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
            _hdrRequested = Config.Bind("Display", "HDR", false,
                "Experimental: ask Unity for HDR display output. ULTRAKILL is built without HDR display support, so this " +
                "currently only logs why it is unavailable (docs/relight.md, \"HDR displays\").").Value;
            LogHdrDisplay();
            Logger.LogInfo($"Relight: {_aeSettings}; tone map {(_tonemapCurve > 0.5f ? "max-channel" : "luminance")} shoulder, knee {_tonemapKnee}, white {_relightWhite}, toe {_tonemapToe} to {_tonemapToeEnd}");
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
            _autoExposure != null && _aeSettings.enabled && (mode == "relight" || (_aeSettings.ptDn && mode == "pt-dn"));

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
                    AutoExposure.BindComposite(_cmd, _denoiseShader, _denoiseCompositeKernel, buffer, _gHybrid);
                    _cmd.SetGlobalBuffer("_UltraRTExposureGlobal", buffer); // the water/glass overlay (TransparentOverlay.shader)
                }
                else ae = false;
            }
            _cmd.SetGlobalFloat(UltraRTAutoExposureId, ae ? 1f : 0f);
            bool tonemap = _relightActive || ae;
            _cmd.SetGlobalFloat("_UltraRTTonemapWhite", tonemap ? _relightWhite : 0f);
            _cmd.SetGlobalVector(UltraRTTonemapParamsId, new Vector4(_tonemapCurve, _tonemapKnee, _tonemapToe, Mathf.Min(_tonemapToeEnd, _tonemapKnee)));
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
            _autoExposure.Execute(_cmd, _aeSettings, _denoiser.FilteredLighting, _gAlbedo, _gSpecular, _gNormalDepth, _gHybrid,
                w, h, dt);
            _gpu?.End(_cmd, SlotExposure);
        }

        private bool _hdrRequested;
        /// <summary>Bench variant plugin._aeBenchClock=0: adapt on real time during bursts (the old behaviour).</summary>
        private bool _aeBenchClock = true;

        /// <summary>
        /// HDR display output probe. Unity 2022.3 only allows HDR output when the player was built with
        /// PlayerSettings.allowHDRDisplaySupport (ULTRAKILL's globalgamemanagers: allowHDRDisplaySupport = False,
        /// useHDRDisplay = False), so HDROutputSettings.main.available stays false whatever the display supports.
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
                Logger.LogInfo(info);
                if (!_hdrRequested) return;
                if (!hdr.available)
                {
                    Logger.LogWarning("[Display] HDR = true, but Unity reports HDR output unavailable: ULTRAKILL was built without " +
                                      "HDR display support (PlayerSettings.allowHDRDisplaySupport = false). Staying SDR.");
                    return;
                }
                hdr.RequestHDRModeChange(true);
                Logger.LogInfo("HDR display: mode change requested");
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
            return FormattableString.Invariant(
                $"exposure x{e.x:F3} ({e.y:+0.00;-0.00} EV, target {e.w:+0.00;-0.00}), mean lum {Mathf.Pow(2f, e.z):F3}, {_autoExposure.LastCount:F0} px");
        }
    }
}
