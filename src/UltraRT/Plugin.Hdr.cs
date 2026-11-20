using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// HDR display output (docs/hdr.md). The work is in UltraRT.Native.dll (Hdr.cpp): a swapchain proxy installed by the
    /// preloader patcher (src/UltraRT.Preload) presents the game as HDR10. This file is the plugin's side:
    ///   - settings: [Display] HDR (read by the patcher: needs a restart), HDRPaperWhite, HDRPeak, HDRHighlights, and
    ///     -ultrart-hdr [on|off|force], -ultrart-hdr-paperwhite N, -ultrart-hdr-peak N, -ultrart-hdr-highlights on|off,
    ///     -ultrart-hdr-emissive X, -ultrart-hdr-lava X, -ultrart-hdr-sky X, -ultrart-hdr-effects X, -ultrart-hdr-bloom X, -ultrart-hdr-white X, -ultrart-hdr-hue sdr|channel|floor, -ultrart-hdr-saturation X, -ultrart-hdr-boost X, -ultrart-hdr-gamma srgb|2.2, -ultrart-hdr-debug,
    ///     -ultrart-hdr-capture [raw], -ultrart-hdr-native on|off
    ///   - native float HDR (the default since 2026-11-20, docs/hdr.md "Native float HDR"): the game's colour target
    ///     (PostProcessV2_Handler.mainTex) is ARGBHalf (HdrGameTarget patches SetupRTs), the composite writes the HDR colour
    ///     itself, the game's transparents add to it unclipped, and right before the game's final pass the float frame is
    ///     handed to the present pass (HdrScenePreRender). The gain maps below are the fallback (-ultrart-hdr-native off).
    ///   - the gain map: the composite (Denoise.compute Composite, RayReconstruction.compute Finish) writes, per pixel, what
    ///     an HDR display should show over what the SDR image shows; the native present pass multiplies it in.
    ///   - the late map: the same from the screen-effects pass (Vfx.shader pass 4) after the game's transparents: effects
    ///     (explosions, fire, beams) and bloom. HdrLateFrame / HdrSubmitLate (Plugin.Vfx.cs TraceVfx).
    /// Hooks: InitHdr (Awake, from ParseExposureArgs), HdrTick (Update), HdrPrepare (Trace, before the denoiser / RR),
    /// HdrSubmitDenoiser / HdrSubmitRR (after the kernel that wrote the map), HdrCapture (bench screenshots).
    /// </summary>
    public partial class Plugin
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct HdrFrame
        {
            public IntPtr gain;
            public int width, height, flags, pad;
            public float uvScaleX, uvScaleY, uvOffsetX, uvOffsetY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HdrParams
        {
            public float paperNits, peakNits, gamma22, tolLo, tolHi, sdrBoost, debug, gainMax, gainStrength, gainFloor;
            // Native float HDR: where the finished SDR frame differs from the float frame saturated by more than nativeTolLo
            // (fully from nativeTolHi; largest channel difference, display units), something was drawn over it (HUD, menus)
            // and it shows SDR. gameGamma: the exponent of the game's final pass (Options > Brightness; 1 = none),
            // colorPrecision: its colour compression (levels per channel; 0 = none).
            public float nativeTolLo, nativeTolHi, gameGamma, colorPrecision, pad1, pad2;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HdrIntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr HdrPtrFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HdrInfoFn(double[] info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HdrSetParamsFn(ref HdrParams p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HdrSetFrameFn(int slot, ref HdrFrame f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate void HdrCaptureFn(string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HdrTakeLogFn(byte[] buf, int size);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private bool _hdrInstalled;          // the native hooks went in at preload (this process may still be SDR)
        private HdrIntFn _hdrGetState;
        private HdrInfoFn _hdrGetInfo;
        private HdrSetParamsFn _hdrSetParams;
        private HdrSetFrameFn _hdrSetFrame;
        private HdrCaptureFn _hdrCapture;
        private HdrTakeLogFn _hdrTakeLog;
        private IntPtr _hdrEventFunc;
        private int _hdrEvent, _hdrRing, _hdrSlot;
        private readonly double[] _hdrInfo = new double[6];
        private readonly byte[] _hdrLogBuf = new byte[8192];
        private float _hdrNextLog;
        private bool _hdrLoggedActive;

        private float _hdrPaperWhite = 0f, _hdrPeak = 0f;    // nits; 0 = Windows' SDR content brightness / the peak the display reports
        private bool _hdrHighlights = true;                    // the gain map (true HDR highlights); off = SDR at paper white
        private float _hdrEmissive = 2f;                       // glowing surfaces in HDR: x their SDR value (display gamma; 1 = as in SDR)
        private float _hdrGameSurfaces = 2f, _hdrSky = 1f;     // the same for bright pixels the game draws itself: lava and acid; the sky (DLSS RR path)
        private float _hdrEffects = 2.5f;                       // bright pixels the game's transparents made (fire, explosions, beams): x their SDR value (display gamma)
        private float _hdrFxLo = 0.6f, _hdrFxHi = 1f;          // ... from this brightest channel (display) on, fully from the second
        private float _hdrBloom = 1f;                          // bloom: extra room above SDR white for the glow (display units; 0 = SDR bloom)
        private RenderTexture _hdrLateTex;                     // the late map the native side was last given a pointer of
        private IntPtr _hdrLatePtr;
        private float _hdrWhite = 0f;                          // composite value that reaches the peak (0 = the SDR white point)
        private float _hdrGainMax = 16f, _hdrSdrBoost = 0f;
        private float _hdrSaturation = 0.3f;                   // highlight colour sdr: share of the saturation the SDR rolloff took that HDR gives back (0 = the SDR colour's, 1 = the HDR curve's)
        private float _hdrGainMode = 0f;                       // highlight colour (UltraRTHdrGain mode): 0 = SDR hue, HDR luminance and saturation; 1 = per channel, gain >= 1 (before 2026-11-20); 2 = per channel
        private bool _hdrGamma22 = true, _hdrDebug;
        private int _hdrCaptureMode;                           // bench: 1 = log statistics of the HDR frame at each screenshot, 2 = + raw frame
        private HdrParams _hdrSent;
        // Native float HDR (docs/hdr.md, "Native float HDR"): [Display] HDRNative, -ultrart-hdr-native on|off, bench plugin._hdrNative=0|1.
        private bool _hdrNative = true;
        private float _hdrNativeTolLo = 0.025f, _hdrNativeTolHi = 0.08f;
        private bool _hdrNativeNow;                            // this frame's composite writes scene values into the float target (HdrPrepare)
        private float _hdrToneMode = 1f;                       // display transform (UltraRTHdrDisplay): 0 = brightest channel, 1 = per channel (default), 2 = luminance (-ultrart-hdr-tone max|channel|lum, bench plugin._hdrToneMode)
        private float _hdrToneWhite, _hdrToneKnee;             // this frame's tone curve white point and knee (BindExposure)
        private Vector4 _hdrSceneTone;                         // the display transform's parameters this frame (HdrPrepare)
        private int _hdrNativeFrame = -1;                      // ... and in which frame
        private int _hdrNativeReinits, _hdrNativeNextReinit;   // game target rebuilds asked for since the last match
        private bool _hdrNativeTargetLogged;
        private int _hdrSceneFrames, _hdrSceneMissed;
        private ComputeShader _hdrSceneCs;                     // Denoise.compute's HdrScene kernel (LoadShaders)
        private int _hdrSceneKernel = -1;
        private RenderTexture _hdrSceneTex;
        private IntPtr _hdrScenePtr;
        private CommandBuffer _hdrSceneCmd;
        private static readonly int HdrNativeId = Shader.PropertyToID("_UltraRTHdrNative");
        private static readonly int HdrSceneSrcId = Shader.PropertyToID("_HdrSceneSrc"), HdrSceneOutId = Shader.PropertyToID("_HdrSceneOut"),
            HdrSceneDimId = Shader.PropertyToID("_HdrSceneDim"), HdrSceneToneId = Shader.PropertyToID("_HdrSceneTone"), GameGammaId = Shader.PropertyToID("_Gamma"), ColorPrecisionId = Shader.PropertyToID("_ColorPrecision");
        private RenderTexture _hdrGainTex, _hdrGameCopy;
        private IntPtr _hdrGainPtr;

        /// <summary>The native proxy is presenting HDR10 (false before its first present, and whenever HDR is off).</summary>
        private bool HdrActive => _hdrInstalled && _hdrGetState() == 2;

        private void InitHdr()
        {
            _hdrRequested = Config.Bind("Display", "HDR", false,
                "HDR10 display output through UltraRT's swapchain proxy (docs/hdr.md). Needs Windows HDR on for the display, and a " +
                "game restart to change. -ultrart-hdr [on|off] overrides it.").Value;
            var emissiveEntry = Config.Bind("Display", "HDREmissive", _hdrEmissive,
                "HDR output: how much brighter glowing surfaces (light fixtures, screens, the gun's lights) are than in SDR, where they " +
                "can only be as bright as white paper. 1 = as in SDR; 2 is about 4.6x the light (display gamma).");
            var lavaEntry = Config.Bind("Display", "HDRLava", _hdrGameSurfaces,
                "HDR output: the same for lava and acid, which the game draws itself (their bright parts only). 1 = as in SDR.");
            _hdrSky = Config.Bind("Display", "HDRSky", 1f,
                "HDR output: the same for the bright parts of the game's sky. 1 = as in SDR.").Value;
            var effectsEntry = Config.Bind("Display", "HDREffects", _hdrEffects,
                "HDR output: how much brighter the bright parts of effects (explosions, fire, beams, coins) are than in SDR, " +
                "where they clip at white. 1 = as in SDR; 2.5 is about 7.5x the light, rolled off towards the display's peak. " +
                "Needs screen effects on.");
            // Once per config (not in bench launches, which never change the user's settings): entries that still hold the
            // first prototype's defaults move to the 2026-11-18 ones (docs/hdr.md, "Defaults"). A value set afterwards sticks.
            if (_configVersion.Value < 2 && !BenchLaunch) // _configVersion: bound in Awake (Plugin.cs), before this
            {
                if (emissiveEntry.Value == 1.5f) emissiveEntry.Value = _hdrEmissive;
                if (lavaEntry.Value == 1.5f) lavaEntry.Value = _hdrGameSurfaces;
                if (effectsEntry.Value == 2f) effectsEntry.Value = _hdrEffects; // written by bench runs of t3/hdr-more before its default moved
                Logger.LogInfo($"Config: HDR defaults of 2026-11-18 (HDREmissive {emissiveEntry.Value}, HDRLava {lavaEntry.Value}, HDREffects {effectsEntry.Value})");
                _configVersion.Value = 2;
            }
            _hdrEmissive = emissiveEntry.Value;
            _hdrGameSurfaces = lavaEntry.Value;
            _hdrEffects = effectsEntry.Value;
            _hdrBloom = Config.Bind("Display", "HDRBloom", _hdrBloom,
                "HDR output: how far above SDR white bloom (the glow around lights and effects) may go, in display units " +
                "(1 = up to twice SDR white in display gamma, about 4.6x the light, or the display's peak). 0 = as in SDR.").Value;
            // (Key HDRPaperWhiteNits, not HDRPaperWhite: a config written by the first prototype build holds that key's old default, 200.)
            _hdrPaperWhite = Config.Bind("Display", "HDRPaperWhiteNits", 0f,
                "HDR output: brightness of SDR white (the HUD, menus, and everything below the highlights), in nits. " +
                "0 = what Windows shows SDR content at on this display (its SDR content brightness), so the game is as bright as with HDR output off.").Value;
            _hdrPeak = Config.Bind("Display", "HDRPeak", 0f,
                "HDR output: brightest highlight in nits. 0 = the peak the display reports.").Value;
            _hdrHighlights = Config.Bind("Display", "HDRHighlights", true,
                "HDR output: ray-traced highlights above SDR white (relight, pathtrace). Off = the SDR image at paper white.").Value;
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length && !a[i + 1].StartsWith("-") ? a[++i] : null;
                bool F(out float v) { v = 0f; var s = Next(); return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); }
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-hdr": { var v = (Next() ?? "on").ToLowerInvariant(); _hdrRequested = v != "off" && v != "0" && v != "false"; break; }
                    case "-ultrart-hdr-paperwhite": if (F(out var pw)) _hdrPaperWhite = pw; break;
                    case "-ultrart-hdr-peak": if (F(out var pk)) _hdrPeak = pk; break;
                    case "-ultrart-hdr-highlights": { var v = (Next() ?? "on").ToLowerInvariant(); _hdrHighlights = v != "off" && v != "0"; break; }
                    case "-ultrart-hdr-white": if (F(out var wh)) _hdrWhite = wh; break;
                    case "-ultrart-hdr-emissive": if (F(out var em)) _hdrEmissive = em; break;
                    case "-ultrart-hdr-lava": if (F(out var la)) _hdrGameSurfaces = la; break;
                    case "-ultrart-hdr-sky": if (F(out var sk)) _hdrSky = sk; break;
                    case "-ultrart-hdr-effects": if (F(out var fx)) _hdrEffects = fx; break;
                    case "-ultrart-hdr-bloom": if (F(out var bl)) _hdrBloom = bl; break;
                    case "-ultrart-hdr-boost": if (F(out var bo)) _hdrSdrBoost = bo; break;
                    case "-ultrart-hdr-gamma": _hdrGamma22 = (Next() ?? "2.2").ToLowerInvariant() != "srgb"; break;
                    case "-ultrart-hdr-debug": _hdrDebug = true; break;
                    case "-ultrart-hdr-saturation": if (F(out var sa)) _hdrSaturation = Mathf.Clamp01(sa); break;
                    case "-ultrart-hdr-hue":
                    {
                        var v = (Next() ?? "sdr").ToLowerInvariant();
                        _hdrGainMode = v == "floor" || v == "legacy" ? 1f : v == "channel" || v == "hdr" ? 2f : 0f;
                        break;
                    }
                    case "-ultrart-hdr-capture": _hdrCaptureMode = (Next() ?? "").ToLowerInvariant() == "raw" ? 2 : 1; break;
                    case "-ultrart-hdr-native": { var v = (Next() ?? "on").ToLowerInvariant(); _hdrNative = v != "off" && v != "0" && v != "false"; break; }
                    case "-ultrart-hdr-tone": { var v = (Next() ?? "channel").ToLowerInvariant(); _hdrToneMode = v.StartsWith("max") ? 0f : v.StartsWith("lum") ? 2f : 1f; break; }
                }
            }
            if (_hdrPaperWhite > 0f) _hdrPaperWhite = Mathf.Clamp(_hdrPaperWhite, 40f, 1000f);
            if (_hdrPeak > 0f) _hdrPeak = Mathf.Clamp(_hdrPeak, 80f, 10000f);
            _hdrEmissive = Mathf.Clamp(_hdrEmissive, 1f, 4f);
            _hdrGameSurfaces = Mathf.Clamp(_hdrGameSurfaces, 1f, 4f);
            _hdrSky = Mathf.Clamp(_hdrSky, 1f, 4f);
            _hdrEffects = Mathf.Clamp(_hdrEffects, 1f, 4f);
            _hdrBloom = Mathf.Clamp(_hdrBloom, 0f, 4f);
            LogHdrDisplay();
            try
            {
                IntPtr module = GetModuleHandleW(GpuTimer.DllName);
                IntPtr p = module != IntPtr.Zero ? GetProcAddress(module, "UltraRT_Hdr_GetState") : IntPtr.Zero;
                if (p == IntPtr.Zero || Marshal.GetDelegateForFunctionPointer<HdrIntFn>(p)() == 0)
                {
                    if (_hdrRequested)
                        Logger.LogWarning("HDR output is on, but the swapchain hooks were not installed at start-up: is " +
                                          "BepInEx\\patchers\\UltraRT\\UltraRT.Preload.dll deployed? (docs/hdr.md) Staying SDR.");
                    return;
                }
                T Get<T>(string name) where T : Delegate
                {
                    IntPtr f = GetProcAddress(module, name);
                    if (f == IntPtr.Zero) throw new EntryPointNotFoundException(name);
                    return Marshal.GetDelegateForFunctionPointer<T>(f);
                }
                _hdrGetState = Get<HdrIntFn>("UltraRT_Hdr_GetState");
                _hdrGetInfo = Get<HdrInfoFn>("UltraRT_Hdr_GetInfo");
                _hdrSetParams = Get<HdrSetParamsFn>("UltraRT_Hdr_SetParams");
                _hdrSetFrame = Get<HdrSetFrameFn>("UltraRT_Hdr_SetFrame");
                _hdrCapture = Get<HdrCaptureFn>("UltraRT_Hdr_RequestCapture");
                _hdrTakeLog = Get<HdrTakeLogFn>("UltraRT_Hdr_TakeLog");
                _hdrEvent = Get<HdrIntFn>("UltraRT_Hdr_GetEvent")();
                _hdrRing = Get<HdrIntFn>("UltraRT_Hdr_GetRingSize")();
                int fs = Get<HdrIntFn>("UltraRT_Hdr_GetFrameSize")(), ps = Get<HdrIntFn>("UltraRT_Hdr_GetParamsSize")();
                if (fs != Marshal.SizeOf<HdrFrame>() || ps != Marshal.SizeOf<HdrParams>())
                    throw new InvalidOperationException($"struct size mismatch (frame {fs}/{Marshal.SizeOf<HdrFrame>()}, params {ps}/{Marshal.SizeOf<HdrParams>()})");
                // Registers the DLL with Unity (shared with the GPU timer and DLSS): the frame event needs it.
                bool events = Get<HdrIntFn>("UltraRT_Init")() != 0;
                _hdrEventFunc = events ? Get<HdrPtrFn>("GetRenderEventAndDataFunc")() : IntPtr.Zero;
                _hdrInstalled = true;
                HdrGameTarget.Float = HdrNativeWanted; // before the game's first SetupRTs (LateUpdate, while mainTex is null)
                HdrPushParams(true);
                HdrPumpLog();
                int state = _hdrGetState();
                Logger.LogInfo($"HDR output: {(state == 2 ? "active (HDR10)" : state == -1 ? "not used (see the lines above)" : "hooks installed, no frame presented yet")}; " +
                               $"paper white {(_hdrPaperWhite > 0f ? _hdrPaperWhite.ToString("F0") + " nits" : "as Windows shows SDR content")}, " +
                               $"peak {(_hdrPeak > 0f ? _hdrPeak.ToString("F0") + " nits" : "from the display")}, " +
                               $"highlights {(_hdrHighlights ? "on" : "off")}, {(_hdrNative ? "native float" : "gain map")}" +
                               $"{(events ? "" : "; frame events unavailable: no gain map")}");
            }
            catch (Exception e)
            {
                _hdrInstalled = false;
                Logger.LogWarning($"HDR output: plugin side failed ({e.GetType().Name}: {e.Message}); the image stays at paper white");
            }
        }

        private float HdrPaperNits()
        {
            if (_hdrPaperWhite > 0f) return _hdrPaperWhite;
            _hdrGetInfo(_hdrInfo);
            return _hdrInfo[5] > 1.0 ? (float)_hdrInfo[5] : 200f;
        }

        private float HdrPeakNits()
        {
            if (_hdrPeak > 0f) return _hdrPeak;
            _hdrGetInfo(_hdrInfo);
            return _hdrInfo[0] > 1.0 ? (float)_hdrInfo[0] : 1000f;
        }

        private void HdrPushParams(bool force = false)
        {
            // The game's final pass (ULTRAKILL/PostProcessV2, docs/ultrakill-rendering.md "Final post-process: dither"): colour
            // compression floor(c P) / P, then c^(2 (1 - _Gamma / 2)). The present pass applies both to the float frame too.
            float g = Shader.GetGlobalFloat(GameGammaId);
            float gameGamma = g > 0f && g < 4f ? Mathf.Max(2f * (1f - g / 2f), 0.01f) : 1f;
            float prec = Mathf.Max(Shader.GetGlobalFloat(ColorPrecisionId), Shader.GetGlobalInt(ColorPrecisionId));
            var p = new HdrParams
            {
                paperNits = _hdrPaperWhite, peakNits = _hdrPeak, gamma22 = _hdrGamma22 ? 1f : 0f, tolLo = 0.06f, tolHi = 0.25f,
                sdrBoost = _hdrSdrBoost, debug = _hdrDebug ? 1f : 0f, gainMax = _hdrGainMax, gainStrength = _hdrHighlights ? 1f : 0f,
                gainFloor = Mathf.Abs(_hdrGainMode - 1f) < 0.5f ? 1f : 0f,
                nativeTolLo = _hdrNativeTolLo, nativeTolHi = Mathf.Max(_hdrNativeTolHi, _hdrNativeTolLo + 1e-3f),
                gameGamma = gameGamma, colorPrecision = prec > 0.5f && prec < 256f ? prec : 0f,
            };
            var s = _hdrSent; // field by field: ValueType.Equals would box every frame
            if (!force && p.paperNits == s.paperNits && p.peakNits == s.peakNits && p.gamma22 == s.gamma22 && p.sdrBoost == s.sdrBoost &&
                p.debug == s.debug && p.gainMax == s.gainMax && p.gainStrength == s.gainStrength && p.gainFloor == s.gainFloor &&
                p.nativeTolLo == s.nativeTolLo && p.nativeTolHi == s.nativeTolHi && p.gameGamma == s.gameGamma && p.colorPrecision == s.colorPrecision) return;
            _hdrSent = p;
            _hdrSetParams(ref p);
        }

        private void HdrPumpLog()
        {
            int n = _hdrTakeLog(_hdrLogBuf, _hdrLogBuf.Length);
            if (n <= 0) return;
            foreach (var line in System.Text.Encoding.ASCII.GetString(_hdrLogBuf, 0, n).Split('\n'))
                if (line.Length > 0) Logger.LogInfo("HDR output: " + line.TrimEnd('\r'));
        }

        /// <summary>Update: settings to the native side, its log lines into ours.</summary>
        private void HdrTick()
        {
            if (!_hdrInstalled) return;
            HdrPushParams();
            HdrNativeTarget();
            if (Time.realtimeSinceStartup < _hdrNextLog) return;
            _hdrNextLog = Time.realtimeSinceStartup + 0.5f;
            HdrPumpLog();
            if (!_hdrLoggedActive && _hdrGetState() == 2)
            {
                _hdrLoggedActive = true;
                _hdrGetInfo(_hdrInfo);
                Logger.LogInfo($"HDR output: active, {_hdrInfo[1]:F0}x{_hdrInfo[2]:F0} HDR10, paper white {HdrPaperNits():F0} nits, peak {HdrPeakNits():F0} nits (display reports {_hdrInfo[0]:F0}, Windows SDR white {_hdrInfo[5]:F0})");
            }
        }

        /// <summary>Trace, before the denoiser's Composite or RR's Finish: hands it the gain map to fill (final-image modes only),
        /// or, with native float HDR, tells it to write the HDR colour itself.</summary>
        private void HdrPrepare(Kind kind, int mode, bool rr, int w, int h, RenderTexture screenDepth)
        {
            if (_denoiser != null) { _denoiser.HdrTarget = null; _denoiser.HdrNative = false; }
            if (_rr != null) { _rr.HdrTarget = null; _rr.HdrNative = false; }
            _hdrNativeNow = false;
            if (!_hdrHighlights || kind != Kind.Denoised || mode != 0 || _hdrEventFunc == IntPtr.Zero || !HdrActive) return;
            if (rr && (screenDepth == null || _rr == null)) return;
            if (HdrNativeLive)
            {
                // Native float HDR (docs/hdr.md): no gain map. The composite writes scene values into the float target
                // (Denoise.compute Composite, RayReconstruction.compute Finish / RemodFinish), the passes after it read
                // _UltraRTHdrNative, and HdrScenePreRender puts the finished frame through the display transform and
                // hands it to the present pass.
                float topN = HdrTopDisplay();
                var pn = new Vector4(2f, topN, _hdrWhite, _hdrGainMax);
                var en = new Vector4(Mathf.Max(0f, _hdrEmissive - 1f), 0.5f, 0.9f, _hdrGainMode);
                _hdrNativeNow = true;
                _hdrNativeFrame = Time.frameCount;
                _cmd.SetGlobalVector(HdrNativeId, new Vector4(1f, topN, 0f, 0f));
                // Display transform: identical below the SDR curve's knee; the scene value that reaches the peak is the SDR
                // curve's white point (what reaches paper white in SDR reaches the peak here), or -ultrart-hdr-white.
                float white = _hdrWhite > 0f ? _hdrWhite : _hdrToneWhite > 0f ? _hdrToneWhite : topN;
                _hdrSceneTone = new Vector4(_hdrToneKnee > 0f ? _hdrToneKnee : 0.75f, topN, Mathf.Max(white, topN), _hdrToneMode);
                if (rr)
                {
                    _rr.HdrNative = true; _rr.HdrParams = pn; _rr.HdrEmissive = en;
                    // Lava, acid and sky: display units here (the factor scales the game's colour in the float target).
                    _rr.HdrGame = new Vector4(_hdrGameSurfaces, _hdrSky, 0f, 0f);
                    _rr.HdrGameColor = _hdrGameSurfaces > 1f || _hdrSky > 1f ? HdrGameCopy(screenDepth.width, screenDepth.height) : null;
                }
                else { _denoiser.HdrNative = true; _denoiser.HdrParams = pn; _denoiser.HdrEmissive = en; }
                return;
            }
            int gw = rr ? screenDepth.width : w, gh = rr ? screenDepth.height : h;
            if (_hdrGainTex == null || _hdrGainTex.width != gw || _hdrGainTex.height != gh || !_hdrGainTex.IsCreated())
            {
                if (_hdrGainTex != null) _hdrGainTex.Release(); // the native side holds its own reference while frames are in flight
                _hdrGainTex = new RenderTexture(gw, gh, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT HDR Gain", hideFlags = HideFlags.HideAndDontSave };
                _hdrGainTex.Create();
                _hdrGainPtr = _hdrGainTex.GetNativeTexturePtr();
            }
            float top = HdrTopDisplay();
            var p = new Vector4(1f, top, _hdrWhite, _hdrGainMax);
            var e = new Vector4(Mathf.Max(0f, _hdrEmissive - 1f), 0.5f, 0.9f, _hdrGainMode);
            if (rr)
            {
                _rr.HdrTarget = _hdrGainTex; _rr.HdrParams = p; _rr.HdrEmissive = e;
                _rr.HdrGame = new Vector4(Mathf.Pow(_hdrGameSurfaces, 2.2f), Mathf.Pow(_hdrSky, 2.2f), 0f, 0f);
                _rr.HdrGameColor = _hdrGameSurfaces > 1f || _hdrSky > 1f ? HdrGameCopy(gw, gh) : null;
            }
            else { _denoiser.HdrTarget = _hdrGainTex; _denoiser.HdrParams = p; _denoiser.HdrEmissive = e; }
        }

        /// <summary>Display peak in the composite's units (display gamma, 1 = SDR white).</summary>
        private float HdrTopDisplay() => Mathf.Pow(Mathf.Max(1f, HdrPeakNits() / HdrPaperNits()), 1f / 2.2f);

        /// <summary>A copy of the game's frame as it is now (opaques, lava and sky drawn, before the composite draws over it): what
        /// the pixels that stay the game's own are expected to look like (HDRLava, HDRSky).</summary>
        private RenderTexture HdrGameCopy(int w, int h)
        {
            var gameTex = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
            if (gameTex == null || gameTex.width != w || gameTex.height != h) return null;
            if (_hdrGameCopy == null || _hdrGameCopy.width != w || _hdrGameCopy.height != h || _hdrGameCopy.format != gameTex.format)
            {
                if (_hdrGameCopy != null) _hdrGameCopy.Release();
                var desc = gameTex.descriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.enableRandomWrite = false;
                _hdrGameCopy = new RenderTexture(desc) { name = "UltraRT HDR GameColor", filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                _hdrGameCopy.Create();
            }
            _cmd.CopyTexture(gameTex, _hdrGameCopy);
            return _hdrGameCopy;
        }

        /// <summary>After the denoiser's Composite: its gain map is at trace resolution, guard band included.</summary>
        private void HdrSubmitDenoiser()
        {
            if (_hdrGainTex == null || _denoiser.HdrTarget != _hdrGainTex || !_denoiser.HdrWritten) return;
            HdrSubmit(1f / _guardScale.x, 1f / _guardScale.y, false); // point sampled: the alpha is a packed colour
        }

        /// <summary>After RR's Finish: its gain map is at screen resolution.</summary>
        private void HdrSubmitRR()
        {
            if (_hdrGainTex == null || _rr == null || _rr.HdrTarget != _hdrGainTex || !_rr.HdrWritten) return;
            HdrSubmit(1f, 1f, false);
        }

        // sx, sy: visible / traced size. The map is stored like every Unity render texture (row 0 = the bottom of the image);
        // the back buffer's uv starts at the top.
        private void HdrSubmit(float sx, float sy, bool linear) => HdrSubmitMap(_cmd, _hdrGainTex, _hdrGainPtr, sx, sy, linear ? 1 : 0);

        // flags: bit 0 linear filter, bit 1 the late map.
        private void HdrSubmitMap(CommandBuffer cmd, RenderTexture tex, IntPtr ptr, float sx, float sy, int flags)
        {
            var f = new HdrFrame
            {
                gain = ptr, width = tex.width, height = tex.height, flags = flags,
                uvScaleX = sx, uvScaleY = -sy, uvOffsetX = 0.5f * (1f - sx), uvOffsetY = 0.5f * (1f + sy),
            };
            int slot = _hdrSlot;
            _hdrSlot = (_hdrSlot + 1) % _hdrRing;
            _hdrSetFrame(slot, ref f);
            cmd.IssuePluginEventAndData(_hdrEventFunc, _hdrEvent, (IntPtr)slot);
        }

        /// <summary>TraceVfx: whether the screen-effects pass writes the late map this frame, and its parameters.</summary>
        private bool HdrLateFrame(out Vector4 fx, out Vector4 top)
        {
            fx = top = Vector4.zero;
            if (!_hdrHighlights || _hdrEventFunc == IntPtr.Zero || !HdrActive || _hdrNativeNow || (_hdrEffects <= 1f && _hdrBloom <= 0f)) return false;
            fx = new Vector4(Mathf.Pow(_hdrEffects, 2.2f) - 1f, _hdrFxLo, Mathf.Max(_hdrFxHi, _hdrFxLo + 1e-3f), _hdrBloom);
            top = new Vector4(Mathf.Pow(Mathf.Max(1f, HdrPeakNits() / HdrPaperNits()), 1f / 2.2f), _hdrGainMax, 0f, 0f);
            return true;
        }

        private RenderTexture _hdrLateFrameTex;  // the late map announced this frame (for the viewmodel composite)
        private int _hdrLateFrameNo = -1;
        private Vector4 _hdrLateTop;
        private static readonly int VmHdrEmId = Shader.PropertyToID("_VMHdrEm"), VmHdrTopId = Shader.PropertyToID("_VMHdrTop");

        /// <summary>Viewmodel composite (HUD camera, after the main camera): the late map to add the gun's glowing parts to,
        /// when the screen-effects pass wrote one this frame; null otherwise.</summary>
        private RenderTexture HdrLateForViewmodel(Material mat)
        {
            if (_hdrNativeNow) return null;
            if (_hdrLateFrameNo != Time.frameCount || _hdrLateFrameTex == null || _hdrEmissive <= 1f) return null;
            mat.SetVector(VmHdrEmId, new Vector4(_hdrEmissive - 1f, 0.5f, 0.9f, 0f));
            mat.SetVector(VmHdrTopId, _hdrLateTop);
            return _hdrLateFrameTex;
        }

        /// <summary>Viewmodel composite with native float HDR this frame: its parameters for pass 2 (the gun's glowing parts
        /// HDREmissive brighter in the float target).</summary>
        private bool HdrNativeForViewmodel(Material mat)
        {
            if (_hdrNativeFrame != Time.frameCount) return false;
            mat.SetVector(VmHdrEmId, new Vector4(Mathf.Max(0f, _hdrEmissive - 1f), 0.5f, 0.9f, 0f));
            mat.SetVector(VmHdrTopId, new Vector4(HdrTopDisplay(), _hdrGainMax, 0f, 0f));
            return true;
        }

        /// <summary>After the screen-effects pass, in its command buffer: the late map of this frame, at the target's size.</summary>
        private void HdrSubmitLate(CommandBuffer cmd, RenderTexture tex, Vector4 top)
        {
            _hdrLateFrameTex = tex;
            _hdrLateFrameNo = Time.frameCount;
            _hdrLateTop = top;
            if (!ReferenceEquals(tex, _hdrLateTex)) { _hdrLateTex = tex; _hdrLatePtr = tex.GetNativeTexturePtr(); }
            HdrSubmitMap(cmd, tex, _hdrLatePtr, 1f, 1f, 2);
        }

        // ---------------------------------------------------------------- native float HDR (docs/hdr.md, "Native float HDR")

        /// <summary>The game's colour target should be ARGBHalf: HDR output can be (or is) active and native float HDR is on.</summary>
        private bool HdrNativeWanted => _hdrInstalled && _hdrNative && _hdrHighlights && _hdrEventFunc != IntPtr.Zero && _hdrGetState() >= 1 &&
                                        HdrGameTarget.Patched > 0 && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);

        /// <summary>Native float HDR this frame: wanted, HDR10 presenting, and the game's target is float.</summary>
        private bool HdrNativeLive
        {
            get
            {
                if (!HdrNativeWanted || !HdrActive) return false;
                var t = MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex;
                return t != null && t.format == RenderTextureFormat.ARGBHalf;
            }
        }

        /// <summary>Update: the game's colour target follows the setting. A change makes the game rebuild its targets (as a
        /// resolution change does: PostProcessV2_Handler.reinitializeTextures), which creates them in the patched format.</summary>
        private void HdrNativeTarget()
        {
            bool want = HdrNativeWanted;
            HdrGameTarget.Float = want;
            var pp = MonoSingleton<PostProcessV2_Handler>.Instance;
            var t = pp != null ? pp.mainTex : null;
            if (t == null) return;
            bool isFloat = t.format == RenderTextureFormat.ARGBHalf;
            if (isFloat == want)
            {
                if (_hdrNativeReinits > 0 || !_hdrNativeTargetLogged)
                    Logger.LogInfo($"HDR output: the game's colour target is {t.format} {t.width}x{t.height} ({(want ? "native float HDR" : "8-bit")})");
                _hdrNativeReinits = 0;
                _hdrNativeTargetLogged = true;
                return;
            }
            if (_hdrNativeReinits >= 3 || PpReinit == null || Time.frameCount < _hdrNativeNextReinit) return;
            _hdrNativeNextReinit = Time.frameCount + 30;
            if (++_hdrNativeReinits == 3)
            {
                Logger.LogWarning($"HDR output: the game's colour target is still {t.format} after 2 rebuilds; giving up (native float HDR stays off)");
                return;
            }
            Logger.LogInfo($"HDR output: rebuilding the game's render targets as {(want ? "ARGBHalf (native float HDR)" : "ARGB32")}");
            PpReinit.SetValue(pp, true);
        }

        /// <summary>Camera.onPreRender of the game's Virtual Camera, which draws the final pass (ULTRAKILL/PostProcessV2) from
        /// mainTex to the screen, so the main camera (outlines included) and the HUD camera are done: sanitises the float frame
        /// (NaN, infinity, negative values from any shader), puts it through the display transform (UltraRTHdrDisplay),
        /// copies it back for the final pass, and hands it to the present pass, which shows it where the finished SDR frame
        /// matches it (not under the HUD, menus or overlays).</summary>
        private void HdrScenePreRender(Camera cam, PostProcessV2_Handler pp)
        {
            if (_hdrNativeFrame != Time.frameCount || !_hdrInstalled || cam == null || pp == null || cam != pp.virtualCam) return;
            var src = pp.mainTex;
            if (src == null || src.format != RenderTextureFormat.ARGBHalf || _hdrSceneCs == null || _hdrSceneKernel < 0) { _hdrSceneMissed++; return; }
            if (_hdrSceneTex == null || _hdrSceneTex.width != src.width || _hdrSceneTex.height != src.height || !_hdrSceneTex.IsCreated())
            {
                if (_hdrSceneTex != null) _hdrSceneTex.Release(); // the native side holds its own reference while frames are in flight
                _hdrSceneTex = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT HDR Scene", hideFlags = HideFlags.HideAndDontSave };
                _hdrSceneTex.Create();
                _hdrScenePtr = _hdrSceneTex.GetNativeTexturePtr();
            }
            if (_hdrSceneCmd == null) _hdrSceneCmd = new CommandBuffer { name = "UltraRT HDR scene" };
            var cmd = _hdrSceneCmd;
            cmd.Clear();
            var cs = _hdrSceneCs;
            int k = _hdrSceneKernel;
            cmd.SetComputeTextureParam(cs, k, HdrSceneSrcId, src);
            cmd.SetComputeTextureParam(cs, k, HdrSceneOutId, _hdrSceneTex);
            cmd.SetComputeVectorParam(cs, HdrSceneDimId, new Vector4(src.width, src.height, 0f, 0f));
            cmd.SetComputeVectorParam(cs, HdrSceneToneId, _hdrSceneTone);
            cmd.DispatchCompute(cs, k, (src.width + 7) / 8, (src.height + 7) / 8, 1);
            cmd.CopyTexture(_hdrSceneTex, src); // the game's final pass reads the sanitised frame too
            // Bound as a UAV again (writes nothing): the present pass expects the state a compute kernel leaves it in.
            cmd.SetComputeVectorParam(cs, HdrSceneDimId, Vector4.zero);
            cmd.DispatchCompute(cs, k, 1, 1, 1);
            HdrSubmitMap(cmd, _hdrSceneTex, _hdrScenePtr, 1f, 1f, 4);
            Graphics.ExecuteCommandBuffer(cmd);
            if (_hdrSceneFrames++ == 0)
                Logger.LogInfo($"HDR output: native float frame {src.width}x{src.height} handed to the present pass (camera depths: main {pp.mainCam?.depth}, " +
                               $"HUD {pp.hudCam?.depth}, virtual {cam.depth})");
        }

        /// <summary>Bench (-ultrart-hdr-capture [raw]): the next present logs statistics of the HDR10 frame it sent to the display;
        /// "raw" also writes it next to the screenshot (png path + .hdr10.bin; tools/hdr/hdr10-view.py reads it).</summary>
        private void HdrCapture(string pngPath)
        {
            if (_hdrCaptureMode == 0 || !HdrActive) return;
            Logger.LogInfo($"HDR output: capture for {Path.GetFileName(pngPath)} ({(_hdrNativeFrame == Time.frameCount ? "native float" : _hdrNative ? "native float wanted, not this frame" : "gain map")}; " +
                           $"{_hdrSceneFrames} native frames so far, {_hdrSceneMissed} missed)");
            _hdrCapture(_hdrCaptureMode == 2 ? pngPath + ".hdr10.bin" : null);
        }
    }

    /// <summary>
    /// Native float HDR (docs/hdr.md): PostProcessV2_Handler.SetupRTs creates the game's colour target (mainTex) and the
    /// scratch target reusableBufferB as ARGB32. This transpiler makes both <see cref="GameColorFormat"/>: ARGBHalf while
    /// <see cref="Float"/> is set. (reusableBufferB too: the game's heat-wave pass copies mainTex into it with CopyTexture,
    /// which needs equal formats; the outline and stain passes only draw into it.) Every other target, size and setting stays
    /// the game's own, so the rebuilds it does by itself (resolution, pixelization, camera changes) keep the format and
    /// release the old targets as before.
    /// </summary>
    internal static class HdrGameTarget
    {
        public static volatile bool Float;
        public static int Patched;

        public static RenderTextureFormat GameColorFormat() => Float ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;

        public static void Patch(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var setup = AccessTools.Method(typeof(PostProcessV2_Handler), "SetupRTs");
                if (setup == null) { log.LogWarning("HDR output: PostProcessV2_Handler.SetupRTs not found: no native float HDR"); return; }
                harmony.Patch(setup, transpiler: new HarmonyMethod(typeof(HdrGameTarget).GetMethod(nameof(Transpiler), BindingFlags.NonPublic | BindingFlags.Static)));
                if (Patched == 0) log.LogWarning("HDR output: no ARGB32 target found in PostProcessV2_Handler.SetupRTs: no native float HDR");
                else log.LogInfo($"HDR output: game target hook installed (PostProcessV2_Handler.SetupRTs, {Patched} targets)");
            }
            catch (Exception e)
            {
                Patched = 0;
                log.LogWarning($"HDR output: patching PostProcessV2_Handler.SetupRTs failed ({e.GetType().Name}: {e.Message}): no native float HDR");
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var ctor = AccessTools.Constructor(typeof(RenderTexture), new[] { typeof(int), typeof(int), typeof(int), typeof(RenderTextureFormat) });
            var fmt = AccessTools.Method(typeof(HdrGameTarget), nameof(GameColorFormat));
            var list = new List<CodeInstruction>(instructions);
            int n = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Newobj || !Equals(list[i].operand, ctor)) continue;
                var prev = list[i - 1];
                bool argb32 = prev.opcode == OpCodes.Ldc_I4_0 ||
                              ((prev.opcode == OpCodes.Ldc_I4 || prev.opcode == OpCodes.Ldc_I4_S) && Convert.ToInt32(prev.operand) == 0);
                if (!argb32) continue; // the depth (Depth), outline mask (RG16) and normal (RGB565) targets stay as they are
                prev.opcode = OpCodes.Call; // in place: keeps any labels on the instruction
                prev.operand = fmt;
                n++;
            }
            Patched = n;
            return list;
        }
    }
}
