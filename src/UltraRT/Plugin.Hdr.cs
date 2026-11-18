using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// HDR display output (docs/hdr.md). The work is in UltraRT.Native.dll (Hdr.cpp): a swapchain proxy installed by the
    /// preloader patcher (src/UltraRT.Preload) presents the game as HDR10. This file is the plugin's side:
    ///   - settings: [Display] HDR (read by the patcher: needs a restart), HDRPaperWhite, HDRPeak, HDRHighlights, and
    ///     -ultrart-hdr [on|off|force], -ultrart-hdr-paperwhite N, -ultrart-hdr-peak N, -ultrart-hdr-highlights on|off,
    ///     -ultrart-hdr-emissive X, -ultrart-hdr-lava X, -ultrart-hdr-sky X, -ultrart-hdr-white X, -ultrart-hdr-boost X, -ultrart-hdr-gamma srgb|2.2, -ultrart-hdr-debug,
    ///     -ultrart-hdr-capture [raw]
    ///   - the gain map: the composite (Denoise.compute Composite, RayReconstruction.compute Finish) writes, per pixel, what
    ///     an HDR display should show over what the SDR image shows; the native present pass multiplies it in.
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
            public float paperNits, peakNits, gamma22, tolLo, tolHi, sdrBoost, debug, gainMax, gainStrength, pad0, pad1, pad2;
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
        private float _hdrEmissive = 1.5f;                     // glowing surfaces in HDR: x their SDR value (display gamma; 1 = as in SDR)
        private float _hdrGameSurfaces = 1.5f, _hdrSky = 1f;   // the same for bright pixels the game draws itself: lava and acid; the sky (DLSS RR path)
        private float _hdrWhite = 0f;                          // composite value that reaches the peak (0 = the SDR white point)
        private float _hdrGainMax = 16f, _hdrSdrBoost = 0f;
        private bool _hdrGamma22 = true, _hdrDebug;
        private int _hdrCaptureMode;                           // bench: 1 = log statistics of the HDR frame at each screenshot, 2 = + raw frame
        private HdrParams _hdrSent;
        private RenderTexture _hdrGainTex, _hdrGameCopy;
        private IntPtr _hdrGainPtr;

        /// <summary>The native proxy is presenting HDR10 (false before its first present, and whenever HDR is off).</summary>
        private bool HdrActive => _hdrInstalled && _hdrGetState() == 2;

        private void InitHdr()
        {
            _hdrRequested = Config.Bind("Display", "HDR", false,
                "HDR10 display output through UltraRT's swapchain proxy (docs/hdr.md). Needs Windows HDR on for the display, and a " +
                "game restart to change. -ultrart-hdr [on|off] overrides it.").Value;
            _hdrEmissive = Config.Bind("Display", "HDREmissive", 1.5f,
                "HDR output: how much brighter glowing surfaces (light fixtures, screens, lava) are than in SDR, where they can only " +
                "be as bright as white paper. 1 = as in SDR; 1.5 is about 2.4x the light.").Value;
            _hdrGameSurfaces = Config.Bind("Display", "HDRLava", 1.5f,
                "HDR output: the same for lava and acid, which the game draws itself (their bright parts only). 1 = as in SDR.").Value;
            _hdrSky = Config.Bind("Display", "HDRSky", 1f,
                "HDR output: the same for the bright parts of the game's sky. 1 = as in SDR.").Value;
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
                    case "-ultrart-hdr-boost": if (F(out var bo)) _hdrSdrBoost = bo; break;
                    case "-ultrart-hdr-gamma": _hdrGamma22 = (Next() ?? "2.2").ToLowerInvariant() != "srgb"; break;
                    case "-ultrart-hdr-debug": _hdrDebug = true; break;
                    case "-ultrart-hdr-capture": _hdrCaptureMode = (Next() ?? "").ToLowerInvariant() == "raw" ? 2 : 1; break;
                }
            }
            if (_hdrPaperWhite > 0f) _hdrPaperWhite = Mathf.Clamp(_hdrPaperWhite, 40f, 1000f);
            if (_hdrPeak > 0f) _hdrPeak = Mathf.Clamp(_hdrPeak, 80f, 10000f);
            _hdrEmissive = Mathf.Clamp(_hdrEmissive, 1f, 4f);
            _hdrGameSurfaces = Mathf.Clamp(_hdrGameSurfaces, 1f, 4f);
            _hdrSky = Mathf.Clamp(_hdrSky, 1f, 4f);
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
                HdrPushParams(true);
                HdrPumpLog();
                int state = _hdrGetState();
                Logger.LogInfo($"HDR output: {(state == 2 ? "active (HDR10)" : state == -1 ? "not used (see the lines above)" : "hooks installed, no frame presented yet")}; " +
                               $"paper white {(_hdrPaperWhite > 0f ? _hdrPaperWhite.ToString("F0") + " nits" : "as Windows shows SDR content")}, " +
                               $"peak {(_hdrPeak > 0f ? _hdrPeak.ToString("F0") + " nits" : "from the display")}, " +
                               $"highlights {(_hdrHighlights ? "on" : "off")}{(events ? "" : "; frame events unavailable: no gain map")}");
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
            var p = new HdrParams
            {
                paperNits = _hdrPaperWhite, peakNits = _hdrPeak, gamma22 = _hdrGamma22 ? 1f : 0f, tolLo = 0.06f, tolHi = 0.25f,
                sdrBoost = _hdrSdrBoost, debug = _hdrDebug ? 1f : 0f, gainMax = _hdrGainMax, gainStrength = _hdrHighlights ? 1f : 0f,
            };
            var s = _hdrSent; // field by field: ValueType.Equals would box every frame
            if (!force && p.paperNits == s.paperNits && p.peakNits == s.peakNits && p.gamma22 == s.gamma22 && p.sdrBoost == s.sdrBoost &&
                p.debug == s.debug && p.gainMax == s.gainMax && p.gainStrength == s.gainStrength) return;
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

        /// <summary>Trace, before the denoiser's Composite or RR's Finish: hands it the gain map to fill (final-image modes only).</summary>
        private void HdrPrepare(Kind kind, int mode, bool rr, int w, int h, RenderTexture screenDepth)
        {
            if (_denoiser != null) _denoiser.HdrTarget = null;
            if (_rr != null) _rr.HdrTarget = null;
            if (!_hdrHighlights || kind != Kind.Denoised || mode != 0 || _hdrEventFunc == IntPtr.Zero || !HdrActive) return;
            if (rr && (screenDepth == null || _rr == null)) return;
            int gw = rr ? screenDepth.width : w, gh = rr ? screenDepth.height : h;
            if (_hdrGainTex == null || _hdrGainTex.width != gw || _hdrGainTex.height != gh || !_hdrGainTex.IsCreated())
            {
                if (_hdrGainTex != null) _hdrGainTex.Release(); // the native side holds its own reference while frames are in flight
                _hdrGainTex = new RenderTexture(gw, gh, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT HDR Gain", hideFlags = HideFlags.HideAndDontSave };
                _hdrGainTex.Create();
                _hdrGainPtr = _hdrGainTex.GetNativeTexturePtr();
            }
            // Display peak in the composite's units (display gamma, 1 = SDR white).
            float top = Mathf.Pow(Mathf.Max(1f, HdrPeakNits() / HdrPaperNits()), 1f / 2.2f);
            var p = new Vector4(1f, top, _hdrWhite, _hdrGainMax);
            var e = new Vector4(Mathf.Max(0f, _hdrEmissive - 1f), 0.5f, 0.9f, 0f);
            if (rr)
            {
                _rr.HdrTarget = _hdrGainTex; _rr.HdrParams = p; _rr.HdrEmissive = e;
                _rr.HdrGame = new Vector4(Mathf.Pow(_hdrGameSurfaces, 2.2f), Mathf.Pow(_hdrSky, 2.2f), 0f, 0f);
                _rr.HdrGameColor = _hdrGameSurfaces > 1f || _hdrSky > 1f ? HdrGameCopy(gw, gh) : null;
            }
            else { _denoiser.HdrTarget = _hdrGainTex; _denoiser.HdrParams = p; _denoiser.HdrEmissive = e; }
        }

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
        private void HdrSubmit(float sx, float sy, bool linear)
        {
            var f = new HdrFrame
            {
                gain = _hdrGainPtr, width = _hdrGainTex.width, height = _hdrGainTex.height, flags = linear ? 1 : 0,
                uvScaleX = sx, uvScaleY = -sy, uvOffsetX = 0.5f * (1f - sx), uvOffsetY = 0.5f * (1f + sy),
            };
            int slot = _hdrSlot;
            _hdrSlot = (_hdrSlot + 1) % _hdrRing;
            _hdrSetFrame(slot, ref f);
            _cmd.IssuePluginEventAndData(_hdrEventFunc, _hdrEvent, (IntPtr)slot);
        }

        /// <summary>Bench (-ultrart-hdr-capture [raw]): the next present logs statistics of the HDR10 frame it sent to the display;
        /// "raw" also writes it next to the screenshot (png path + .hdr10.bin; tools/hdr/hdr10-view.py reads it).</summary>
        private void HdrCapture(string pngPath)
        {
            if (_hdrCaptureMode == 0 || !HdrActive) return;
            Logger.LogInfo($"HDR output: capture for {Path.GetFileName(pngPath)}");
            _hdrCapture(_hdrCaptureMode == 2 ? pngPath + ".hdr10.bin" : null);
        }
    }
}
