using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UltraRT
{
    /// <summary>
    /// UltraRT: hardware ray tracing for ULTRAKILL (DX12 only: launch with -force-d3d12).
    ///   F7 toggle view   F8 next mode   F9 reload shader bundle (also automatic when the file changes)   F4 reflections
    /// Unattended testing (see scripts/run-debugview.ps1):
    ///   -ultrart-scene "Level 1-1"  -ultrart-view N  -ultrart-scale 0.5  -ultrart-capture  -ultrart-quit
    /// </summary>
    [BepInPlugin("ooo.lew.ultrart", "UltraRT", "0.2.0")]
    [BepInDependency(PluginConfiguratorGuid, BepInDependency.DependencyFlags.SoftDependency)] // in-game settings menu (SettingsMenu.cs)
    public partial class Plugin : BaseUnityPlugin
    {
        private enum Kind { Debug, PathTrace, Denoised, Hybrid, Reflection }

        // For Denoised, `mode` is a DenoiserDebugView; the trace itself runs PathTrace mode 0.
        private static readonly (string name, Kind kind, int mode)[] Modes =
        {
            ("hybrid", Kind.Hybrid, (int)DenoiserDebugView.Final),
            ("pt-dn", Kind.Denoised, (int)DenoiserDebugView.Final),
            ("relight", Kind.Denoised, (int)DenoiserDebugView.Final), // full RT with physical falloff (docs/relight.md)
            ("pathtrace", Kind.Denoised, (int)DenoiserDebugView.Final), // multi-bounce path tracing, relight's light model (docs/path-tracing.md)
            ("dn-lighting", Kind.Denoised, (int)DenoiserDebugView.FilteredLighting),
            ("dn-variance", Kind.Denoised, (int)DenoiserDebugView.Variance),
            ("dn-history", Kind.Denoised, (int)DenoiserDebugView.HistoryLength),
            ("pt", Kind.PathTrace, 0),
            ("pt-direct", Kind.PathTrace, 1),
            ("pt-indirect", Kind.PathTrace, 2),
            ("lit", Kind.Debug, 0),
            ("albedo", Kind.Debug, 1),
            ("normals", Kind.Debug, 2),
            ("depth", Kind.Debug, 3),
            ("instance", Kind.Debug, 4),
            ("uv", Kind.Debug, 5),
            ("emission", Kind.Debug, 6),
            ("primitive", Kind.Debug, 7),
            // Reflections (docs/reflections.md). Kind.Reflection traces like pt-dn and shows a Reflections.compute debug view.
            ("refl", Kind.Reflection, 2),        // filtered reflected radiance
            ("refl-raw", Kind.Reflection, 1),    // one-ray reflected radiance, unfiltered
            ("refl-spec", Kind.Reflection, 3),   // what gets added (specW x filtered reflection)
            ("refl-history", Kind.Reflection, 4),
            ("materials", Kind.Debug, 8),        // material class colours (striped: REFLECTION materials)
            ("spec-params", Kind.Debug, 9),      // r smoothness, g metalness, b reflection strength / spec weight
            ("cache", Kind.PathTrace, CacheDebugModeBase + (int)RadianceCacheDebug.Radiance),     // Plugin.Cache.cs
            ("cache-cells", Kind.PathTrace, CacheDebugModeBase + (int)RadianceCacheDebug.Cells),
            ("dn-boost", Kind.Denoised, (int)DenoiserDebugView.SpatialBoost), // denoiser stability features (docs/denoiser.md)
            ("motion", Kind.Denoised, (int)DenoiserDebugView.Motion),        // motion vectors (docs/denoiser.md)
        };

        private const int MaxLights = 256;
        private float _primaryAmbient = 0.5f;

        private string _bundlePath;
        private DateTime _bundleStamp;
        private float _nextBundleCheck;
        private AssetBundle _bundle;
        private RayTracingShader _debugShader, _ptShader;
        private Shader _proxyShader;

        private SceneProxies _proxies;
        private RenderTexture _output;
        private ComputeBuffer _lightBuffer;
        private readonly Vector4[] _lightData = new Vector4[MaxLights * 4];
        private readonly uint[] _lightMasks = new uint[MaxLights];
        private ComputeBuffer _lightMaskBuffer;
        private int _lightCount;
        private float _nextLightScan, _nextActiveLights;
        private readonly List<Light> _activeLights = new List<Light>();
        private int _activeLightsVersion; // bumped whenever _activeLights is rebuilt (EffectLights appends what it lacks)
        private SceneProxies _activeLightsOwner;
        private CommandBuffer _cmd;
        private Camera _camera;
        private CoroutineHost _host;

        private bool _viewEnabled = true;
        private int _mode;
        private float _scale = 0.5f;
        private uint _frameIndex;
        private uint _accumFrames;
        private bool _accumulate;
        private Matrix4x4 _lastViewProj;
        private int _lastMode = -1;
        private int _tracedFrames;

        private string _autoScene;
        private bool _capture, _quit;
        private string _outDir;

        private void Awake()
        {
            InitSettings(); // SettingsMenu.cs: register the settings and remember the code defaults before any entry or flag changes them
            // Starting view mode: BepInEx/config/ooo.lew.ultrart.cfg ([View] DefaultMode), overridden by -ultrart-view.
            var defaultMode = Config.Bind("View", "DefaultMode", "relight",
                "View mode at startup: " + string.Join(", ", Modes.Select(m => m.name)) + ". Cycle in game with Page Up / Page Down.").Value;
            int dm = Array.FindIndex(Modes, m => m.name.Equals(defaultMode, StringComparison.OrdinalIgnoreCase));
            if (dm >= 0) _mode = dm;
            ParseArgs();
            ParseCacheArgs();
            ParseExposureArgs();
            ParseDlssArgs(); // DLSS Ray Reconstruction (Plugin.Dlss.cs)
            ParsePathTraceArgs(); // the pathtrace mode (Plugin.PathTrace.cs)
            ParseTransparentArgs();
            ParseSkyArgs();
            ParseVolumetricsArgs();
            ParseMaterialArgs();
            ParseLavaArgs();
            ParseViewmodelArgs();
            ParseVfxArgs(); // screen-space effects (Plugin.Vfx.cs)
            ParseWetArgs(); // wet surfaces (Plugin.Wet.cs)
            _outDir = Path.Combine(Paths.BepInExRootPath, "ultrart");
            Directory.CreateDirectory(_outDir);

            Logger.LogInfo($"API={SystemInfo.graphicsDeviceType} supportsRayTracing={SystemInfo.supportsRayTracing} colorSpace={QualitySettings.activeColorSpace}");
            if (!SystemInfo.supportsRayTracing || Environment.GetCommandLineArgs().Any(a => a.Equals("-ultrart-minimal", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogWarning("Ray tracing unavailable (launch with -force-d3d12 on a DXR GPU). UltraRT is idle.");
                // Benchmarks can still run game-only ("off" modes) for comparisons, e.g. against DX11.
                var idleHost = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(idleHost);
                _host = idleHost.AddComponent<CoroutineHost>();
                _noScene = true;
                SceneManager.sceneUnloaded += s => Logger.LogInfo($"sceneUnloaded '{s.name}'");
                SceneManager.activeSceneChanged += (_, to) => Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                StartBenchIfRequested();
                return;
            }

            _bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", "ultrart.shaders");
            SceneProxies.MaterialOverrideFolders = new[] { Path.GetDirectoryName(Info.Location) ?? "", Paths.ConfigPath };
            if (!LoadShaders()) return;

            if (_useGpuTimer)
            {
                // Opt-in dev tool: the native DLL registers itself with Unity through an internal routine.
                var nativePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", GpuTimer.DllName);
                if (GpuTimer.TryCreate(nativePath, out _gpu, out var gpuError)) Logger.LogInfo($"GPU timer: {_gpu.Status}");
                else Logger.LogWarning($"GPU timer unavailable: {gpuError}");
            }

            if (!_noHook)
            {
                Patches.Apply(Info.Metadata.GUID);
                Logger.LogInfo(BloodWet.Hooked ? "Wet blood: stain hook installed (BloodsplatterManager.CreateBloodstain)" : $"Wet blood: stain hook missing ({BloodWet.HookError}): stains stay dry");
                Logger.LogInfo(Patches.GenericInstantiateHooked ? "Instantiate hooks installed (incl. generic Instantiate<T>)"
                    : $"Instantiate hooks installed; generic Instantiate<T> not hooked ({Patches.GenericInstantiateError}), the periodic rescan covers it");
                Logger.LogInfo(Patches.PortalClonesHooked ? "Portal clone hook installed (PortalAwareRenderer.CreateClone)" : $"Portal clones not hooked: {Patches.PortalClonesError}");
            }
            _cmd = new CommandBuffer { name = "UltraRT" };
            _lightBuffer = new ComputeBuffer(MaxLights * 4, 16);
            _lightMaskBuffer = new ComputeBuffer(MaxLights, 4);
            _lightSampling = new LightSampling(MaxLights) { Settings = _lightSamplingSettings };
            _emissive = new EmissiveLights(Logger) { Settings = _emissiveSettings };
            var go = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
            _host.OnUpdate = OnUpdate;
            _host.OnGui = DrawHud;
            AttachSettings(); // config entries -> fields (flags win), then the PluginConfigurator page

            SceneManager.sceneUnloaded += s => Logger.LogInfo($"sceneUnloaded '{s.name}'");
            SceneManager.activeSceneChanged += (_, to) =>
            {
                Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                ResetScene();
                _host.StartCoroutine(BuildWhenLoaded());
            };

            if (!_noHook) Camera.onPreRender += OnCameraPreRender;
            if (!StartBenchIfRequested() && !string.IsNullOrEmpty(_autoScene)) _host.StartCoroutine(AutoRun());
        }

        private void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-scene": _autoScene = Next(); break;
                    case "-ultrart-view": int.TryParse(Next(), out _mode); break;
                    case "-ultrart-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _scale); break;
                    case "-ultrart-accumulate": _accumulate = true; break;
                    case "-ultrart-capture": _capture = true; break;
                    case "-ultrart-quit": _quit = true; break;
                    case "-ultrart-off": _viewEnabled = false; break;
                    case "-ultrart-dumpverts": SceneProxies.DumpAtlasVertices = true; break;
                    case "-ultrart-skinned-visible": SceneProxies.SkinnedProxiesVisible = true; break;
                    case "-ultrart-skinned-noupdate": SceneProxies.SkinnedTransformUpdates = false; break;
                    case "-ultrart-spawn-test": _spawnTest = true; break;
                    case "-ultrart-gputimer": _useGpuTimer = true; break;
                    case "-ultrart-nostatic": SceneProxies.SkipStaticBatches = true; break;
                    case "-ultrart-nomerge": SceneProxies.NoSubmeshMerge = true; break;
                    case "-ultrart-flipy": _flipY = true; break;
                    case "-ultrart-ccw": SceneProxies.FrontCounterClockwise = true; break;
                    case "-ultrart-noscene": _noScene = true; break;
                    case "-ultrart-relight-falloff": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _relightFalloff); break;
                    case "-ultrart-relight-fill": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _relightFillCut); break;
                    case "-ultrart-relight-ambient": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _relightAmbientCut); break;
                    case "-ultrart-relight-exposure": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _relightExposure); break;
                    case "-ultrart-relight-white": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _relightWhite); break;
                    case "-ultrart-light-radius": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _lightRadius); break;
                    case "-ultrart-nofixtureshadows": _fixtureShadows = false; break;
                    case "-ultrart-nofixturelift": _fixtureLift = false; break;
                    case "-ultrart-logfixtures": _logFixtures = true; break;
                    case "-ultrart-nodepthmatch": _noDepthMatch = true; break;
                    case "-ultrart-noportals": _portals = false; break;
                    case "-ultrart-noportallayer": SceneProxies.TracePortalLayer = false; break;
                    case "-ultrart-portal-hops": int.TryParse(Next(), out _portalHops); break;
                    case "-ultrart-rtmirrors": _rtMirrors = true; break;
                    case "-ultrart-noplayer": SceneProxies.TracePlayerModel = false; break;
                    case "-ultrart-player-rays": int.TryParse(Next(), out _playerRays); break;
                    case "-ultrart-norain": _rainOn = false; break;
                    case "-ultrart-player-noanimate": SceneProxies.PlayerAlwaysAnimate = false; break;
                    case "-ultrart-compdebug": int.TryParse(Next(), out _compositeDebug); break;
                    case "-ultrart-lights":
                        switch ((Next() ?? "").ToLowerInvariant())
                        {
                            case "ref": _lightSamplingSettings.mode = LightSamplingMode.Reference; break;
                            case "ris": _lightSamplingSettings.mode = LightSamplingMode.Ris; break;
                            default: _lightSamplingSettings.mode = LightSamplingMode.ReSTIR; break;
                        }
                        break;
                    case "-ultrart-ris-m": int.TryParse(Next(), out _lightSamplingSettings.candidates); break;
                    case "-ultrart-restir-spatial": int.TryParse(Next(), out _lightSamplingSettings.spatialSamples); break;
                    case "-ultrart-restir-spatial-hybrid": int.TryParse(Next(), out _lightSamplingSettings.spatialSamplesHybrid); break;
                    case "-ultrart-restir-fresh": int.TryParse(Next(), out _lightSamplingSettings.freshCandidates); break; // extra candidates without history
                    case "-ultrart-restir-retest": _lightSamplingSettings.occludedRetest = true; break; // re-test occluded history, full M in umbrae (docs/light-sampling.md, "Occluded history")
                    case "-ultrart-restir-pixel-change": _lightSamplingSettings.perPixelLightChange = true; break; // per-pixel light-change history (docs/light-sampling.md)
                    case "-ultrart-noemissive": _emissiveSettings.enabled = false; break;
                    case "-ultrart-emissive-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.lightScale); break;
                    case "-ultrart-emissive-hybrid": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.hybridAdd); break;
                    case "-ultrart-static-always": SceneProxies.StaticActivation = false; break;
                    case "-ultrart-static-level": { if (int.TryParse(Next(), out var lv)) SceneProxies.ForceGroupLevel = lv; break; }
                    case "-ultrart-static-budget": { if (int.TryParse(Next(), out var bg)) SceneProxies.StaticRecordBudget = bg; break; }
                    case "-ultrart-shadowbias": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _shadowBias); break;
                    case "-ultrart-gi": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _giStrength); break;
                    case "-ultrart-nohook": _noHook = true; break;
                    case "-ultrart-pose": _capturePose = Next(); break;
                    case "-ultrart-inject": if (Enum.TryParse(Next(), true, out CameraEvent ev)) { InjectEvent = ev; _injectForced = true; } break;
                    case "-ultrart-gputimer-flags": Environment.SetEnvironmentVariable("ULTRART_GPUTIMER_FLAGS", Next()); break;
                    case "-ultrart-ambient": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _primaryAmbient); break;
                    case "-ultrart-noreflections": _reflSettings.enabled = false; break;
                    case "-ultrart-refl-strength": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _reflSettings.reflStrength); break;
                    case "-ultrart-spec-weight": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _reflSettings.specWeight); break;
                    case "-ultrart-refl-cache": { var v = Next(); _reflSettings.cacheShading = v == "off" ? 0 : v == "always" ? 2 : 1; break; }
                    case "-ultrart-refl-keepcube": _reflSettings.replaceGameCube = false; break;
                    case "-ultrart-matdump": SceneProxies.DumpMaterialClasses = true; break;
                    case "-ultrart-viewprobe": SceneProxies.ViewProbe = true; break;
                    case "-ultrart-nomatsync": SceneProxies.MaterialSync = false; break;
                    case "-ultrart-lit-unlit": SceneProxies.FullbrightUnlit = false; break;
                    case "-ultrart-albedo-emission-glow": SceneProxies.LitAlbedoEmission = false; break;
                    case "-ultrart-enemy-lit": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SceneProxies.UnlitEnemyLitWeight); break;
                    case "-ultrart-nomotion": MotionVectors.Enabled = false; break;
                    case "-ultrart-guard":
                        float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _guardX);
                        _guardY = _guardX;
                        if (i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gy)) { _guardY = gy; i++; }
                        break;
                    case "-ultrart-motion-probe": SceneProxies.MotionProbe = true; break;
                }
            }
            _scale = Mathf.Clamp(_scale, 0.1f, 1f);
            _mode = Mathf.Clamp(_mode, 0, Modes.Length - 1);
        }

        // ---------------------------------------------------------------- shaders / hot reload

        private bool LoadShaders()
        {
            if (!File.Exists(_bundlePath)) { Logger.LogError($"Shader bundle missing: {_bundlePath}"); return false; }
            // Load from memory so the file isn't locked and the build can overwrite it while the game runs.
            var bundle = AssetBundle.LoadFromMemory(File.ReadAllBytes(_bundlePath));
            if (bundle == null) { Logger.LogError($"Could not load shader bundle at {_bundlePath}"); return false; }

            var rts = bundle.LoadAllAssets<RayTracingShader>();
            var debug = rts.FirstOrDefault(s => s.name == "DebugView");
            var pt = rts.FirstOrDefault(s => s.name == "PathTrace");
            var shaders = bundle.LoadAllAssets<Shader>();
            var proxy = shaders.FirstOrDefault(s => s.name == "UltraRT/Proxy");
            var composite = shaders.FirstOrDefault(s => s.name == "UltraRT/Composite");
            _skyConvertShader = shaders.FirstOrDefault(s => s.name == "Hidden/UltraRT/SkyConvert"); // optional: cubemap skies give no sky light without it
            ReleaseSkyLatLong(); // reconvert with the reloaded shader
            SceneProxies.PortalShader = shaders.FirstOrDefault(s => s.name == "UltraRT/Portal"); // optional: no portals without it
            if (SceneProxies.PortalShader == null) Logger.LogWarning("UltraRT/Portal missing from the bundle: portals are not traced");
            var denoise = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Denoise");
            var reflections = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Reflections");
            Logger.LogInfo($"Bundle loaded: DebugView={(debug ? "ok" : "MISSING")} PathTrace={(pt ? "ok" : "MISSING")} " +
                           $"proxy={(proxy ? (proxy.isSupported ? "ok" : "UNSUPPORTED") : "MISSING")} Denoise={(denoise ? "ok" : "MISSING")} " +
                           $"Composite={(composite ? "ok" : "MISSING")} Reflections={(reflections ? "ok" : "MISSING")}");
            if (debug == null || pt == null || proxy == null || denoise == null || composite == null || reflections == null) { bundle.Unload(true); return false; }

            if (_compositeMat != null) Destroy(_compositeMat);
            _compositeMat = new Material(composite) { name = "UltraRT Composite" };

            var settings = _denoiser?.Settings;
            _denoiser?.Dispose();
            _denoiser = new Denoiser(denoise);
            _denoiserDirect?.Dispose();
            _denoiserDirect = new Denoiser(denoise);
            _unpackHybrid = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "UnpackHybrid");
            if (_unpackHybrid == null) Logger.LogWarning("UnpackHybrid compute shader missing from bundle");
            var upsample = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Upsample");
            var upSettings = _upsampler?.Settings;
            _upsampler?.Dispose();
            _upsampler = upsample != null ? new TemporalUpsampler(upsample) : null;
            if (_upsampler == null) Logger.LogWarning("Upsample compute shader missing from bundle: replace modes use the 2x2 depth-guided composite");
            else if (upSettings != null) _upsampler.Settings = upSettings;
            _motionCopy = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "MotionCopy");
            if (_motionCopy == null) Logger.LogWarning("MotionCopy compute shader missing from bundle: skinned meshes get root motion only");
            if (settings != null) _denoiser.Settings = settings;
            _reflFilter?.Dispose();
            _reflFilter = new ReflectionFilter(reflections);
            LoadCacheShaders(bundle);
            LoadExposureShader(bundle, denoise);
            LoadTransparentShaders(bundle);
            LoadLavaShader(bundle);
            LoadVolumetricsShaders(bundle);
            LoadViewmodelShaders(bundle);
            LoadDlssShader(bundle);
            LoadEffectShaders(shaders);
            LoadVfxShaders(bundle);
            LoadWetShaders(bundle);

            _bundle = bundle;
            _debugShader = debug;
            _ptShader = pt;
            _proxyShader = proxy;
            _bundleStamp = File.GetLastWriteTimeUtc(_bundlePath);
            return true;
        }

        private void ReloadShaders()
        {
            Logger.LogInfo("Reloading shader bundle...");
            ResetScene(); // proxy materials reference the old shader
            if (_bundle != null) _bundle.Unload(true);
            _bundle = null;
            _debugShader = _ptShader = null;
            _proxyShader = null;
            if (LoadShaders()) _host.StartCoroutine(BuildWhenLoaded());
        }

        private void OnUpdate()
        {
            UpdateRecorder();
            UpdateEffects();
            UpdateShaftFakes(); // lamp cones: the game's fake cone meshes hide while ours draw (Plugin.Shafts.cs)
            UpdateParticles(); // lit particles (Plugin.Particles.cs)
            MaterialsUpdate(); // Ctrl+F1, _UltraRTDetail global
            try
            {
                if (Input.GetKeyDown(KeyCode.F7) && !CtrlHeld() && !ShiftHeld()) { _viewEnabled = !_viewEnabled; _denoiser?.Reset(); _denoiserDirect?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset(); Logger.LogInfo($"View {(_viewEnabled ? "on" : "off")}"); }
                ExposureHotkeys(); // Ctrl+F4
                if (Input.GetKeyDown(KeyCode.F4) && !CtrlHeld()) { _reflSettings.enabled = !_reflSettings.enabled; _reflFilter?.Reset(); Logger.LogInfo($"Reflections: {(_reflSettings.enabled ? "on" : "off")}"); Toast($"Reflections {(_reflSettings.enabled ? "on" : "off")} (F4)"); }
                SkyHotkeys(); // Ctrl+F5
                if (Input.GetKeyDown(KeyCode.F5) && !CtrlHeld()) { _emissiveSettings.enabled = !_emissiveSettings.enabled; _lightSampling?.Reset(); _denoiser?.Reset(); _denoiserDirect?.Reset(); Logger.LogInfo($"Emissive lights: {(_emissiveSettings.enabled ? "on" : "off")}"); }
                VolumetricsHotkeys(); // Ctrl+F6
                ViewmodelHotkeys(); // Ctrl+F7
                VfxHotkeys(); // Ctrl+F2, Shift+F2
                if (Input.GetKeyDown(KeyCode.F6) && !CtrlHeld()) { _lightSamplingSettings.mode = (LightSamplingMode)(((int)_lightSamplingSettings.mode + 1) % 3); _lightSampling?.Reset(); Logger.LogInfo($"Light sampling: {_lightSamplingSettings.mode}"); }
                // Page Up / Page Down cycle view modes (F8 opens ULTRAKILL's console).
                if (Input.GetKeyDown(KeyCode.PageDown)) { _mode = (_mode + 1) % Modes.Length; Logger.LogInfo($"Mode: {Modes[_mode].name}"); Toast($"UltraRT mode: {Modes[_mode].name}"); }
                if (Input.GetKeyDown(KeyCode.PageUp)) { _mode = (_mode + Modes.Length - 1) % Modes.Length; Logger.LogInfo($"Mode: {Modes[_mode].name}"); Toast($"UltraRT mode: {Modes[_mode].name}"); }
                if (Input.GetKeyDown(KeyCode.F9)) ReloadShaders();
                if (Input.GetKeyDown(KeyCode.F2) && !CtrlHeld() && !ShiftHeld() && _upsampler != null)
                {
                    _upsampler.Settings.enabled = !_upsampler.Settings.enabled;
                    string on = _upsampler.Settings.enabled ? "on" : "off (2x2 depth-guided)";
                    Logger.LogInfo($"Temporal upsampling: {on}");
                    Toast($"Temporal upsampling {on} (F2)");
                }
                CacheHotkeys();
                DlssUpdate(); // Ctrl+F8, native RR log (Plugin.Dlss.cs)
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
            _menu?.Update(); // the settings menu follows what the hotkeys changed

            if (Time.realtimeSinceStartup >= _nextBundleCheck)
            {
                _nextBundleCheck = Time.realtimeSinceStartup + 0.5f;
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(_bundlePath);
                    // Let the writer finish before reading.
                    if (stamp != _bundleStamp && (DateTime.UtcNow - stamp).TotalSeconds > 0.5) ReloadShaders();
                }
                catch (IOException) { }
            }
        }

        // ---------------------------------------------------------------- scene

        private void ResetScene()
        {
            _effectGeometry?.SceneReset(); // its instances go with the RT scene
            if (_proxies != null)
            {
                var sw = Stopwatch.StartNew();
                Logger.LogInfo("ResetScene: disposing RT scene...");
                _proxies.Dispose();
                Logger.LogInfo($"ResetScene: disposed in {sw.Elapsed.TotalMilliseconds:F1} ms");
            }
            _proxies = null;
            Patches.CollectSpawns = false;
            Patches.PendingSpawns.Clear();
            _spawnCursor = 0;
            _spawnSeen.Clear();
            _accumFrames = 0;
            _denoiser?.Reset();
            _denoiserDirect?.Reset();
            _lightSampling?.Reset();
            _reflFilter?.Reset();
            _emissive?.Clear();
            _radianceCache?.Reset();
            _autoExposure?.Reset();
            _volumetrics?.Reset();
            _lightShafts?.Clear();
            _vfx?.Reset();
            ViewmodelReset();
        }

        private Denoiser _denoiser;
        private ReflectionFilter _reflFilter;
        private readonly ReflectionSettings _reflSettings = new ReflectionSettings();
        private RenderTexture _denoised;
        private RenderTexture _displayed;
        private Kind _lastKind = Kind.Debug;

        private IEnumerator BuildWhenLoaded()
        {
            yield return null;
            while (SceneHelper.PendingScene != null) yield return null;
            yield return new WaitForSecondsRealtime(1f);
            if (_proxies != null || _proxyShader == null) yield break;
            if (_noScene) { Logger.LogInfo("RT scene build skipped (-ultrart-noscene)"); yield break; }
            try
            {
                var sw = Stopwatch.StartNew();
                // The player's camera decides what is visible. Other cameras named "Main Camera" (menus, cutscenes) may
                // render everything, including trigger volumes drawn with the default material (0-1's Gun Room trigger
                // is a 30 m cube on layer 16).
                var playerCam = MonoSingleton<CameraController>.Instance != null ? MonoSingleton<CameraController>.Instance.cam : null;
                var cam = playerCam != null ? playerCam : Camera.allCameras.FirstOrDefault(c => c.name == "Main Camera") ?? Camera.main;
                Logger.LogInfo($"RT layer mask from {(cam != null ? Bench.GameControl.PathOf(cam.transform) : "none")}: 0x{(cam != null ? cam.cullingMask : ~0):X8}");
                _proxies = new SceneProxies(Logger, _proxyShader, cam != null ? cam.cullingMask : ~0, _motionCopy);
                _proxies.Build();
                _emissive.Build(_proxies);
                Patches.PendingSpawns.Clear();
                _spawnCursor = 0;
                _spawnSeen.Clear();
                Patches.CollectSpawns = true;
                _nextRescan = Time.realtimeSinceStartup + RescanInterval; // the build just scanned everything
                Logger.LogInfo($"Scene RT setup took {sw.Elapsed.TotalMilliseconds:F0} ms");
                LogLightingEnvironment();
            }
            catch (Exception e)
            {
                Logger.LogError($"Proxy build failed: {e}");
                ResetScene();
            }
        }

        private void GatherLights()
        {
            // Lights and sky spheres are tracked by SceneProxies (scene build, spawned hierarchies, rescans). Scanning the
            // scene for them here (FindObjectsOfType<Light/Renderer> every second) cost several ms per call in 8-3.
            if (Time.realtimeSinceStartup >= _nextLightScan)
            {
                _nextLightScan = Time.realtimeSinceStartup + 1f;
                _proxies.PruneLights();
                _skyMaterial = FindSkyMaterial(_proxies.SkyRenderers, _camera != null ? _camera.transform.position : (Vector3?)null);
            }
            // Most tracked lights are off (8-3: ~1,300 lights, ~100 on). Re-check the whole list at 4 Hz, and every frame
            // only the lights that were on (a light switching on is seen within 0.25 s; it used to take up to 1 s).
            if (Time.realtimeSinceStartup >= _nextActiveLights || _activeLightsOwner != _proxies)
            {
                _nextActiveLights = Time.realtimeSinceStartup + 0.25f;
                _activeLightsOwner = _proxies;
                _activeLights.Clear();
                _activeLightsVersion++;
                foreach (var l in _proxies.Lights)
                    if (l != null && l.isActiveAndEnabled) _activeLights.Add(l);
            }
            // Layout must match PathTrace.raytrace: 4 x float4 per light (see the comment on _Lights there).
            UpdateFixtureLifts();
            if (_logFixtures && _camera != null && Time.realtimeSinceStartup > _nextLightDump)
            {
                _nextLightDump = Time.realtimeSinceStartup + 3f;
                Logger.LogInfo($"--- lights near camera {_camera.transform.position}");
                LogLightsNear(_camera.transform.position, 25f);
                LogEnclosing(_camera.transform.position);
                if (_proxies != null) foreach (var line in _proxies.DescribeVisibleAround(_camera.transform.position)) Logger.LogInfo($"proxy around camera: {line}");
                if (_proxies != null) foreach (var line in _proxies.DescribeSkinnedNear(_camera.transform.position, 30f)) Logger.LogInfo($"skinned near camera: {line}");
            }
            _lightCount = 0;
            // The main sun: the brightest directional light from above the horizon (LowSideLight below).
            Light mainSun = null;
            float mainSunLum = -1f;
            foreach (var l in _activeLights)
            {
                if (l == null || l.type != LightType.Directional || !l.isActiveAndEnabled || l.intensity <= 0f || -l.transform.forward.y < -0.05f) continue;
                float lum = (0.2126f * l.color.r + 0.7152f * l.color.g + 0.0722f * l.color.b) * l.intensity;
                if (lum > mainSunLum) { mainSunLum = lum; mainSun = l; }
            }
            foreach (var l in _activeLights)
            {
                if (_lightCount >= MaxLights) break;
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                var t = l.transform;
                var fwd = t.forward;
                var c = l.color * l.intensity;
                var pos = t.position;
                float shadowRadius = 0f; // 0: the global _LightRadius
                if (_fixtureLifts.TryGetValue(l, out var lift))
                {
                    pos.y += lift.y;
                    shadowRadius = lift.w;
                }
                else if (_fixtureLift && _proxies.StaticEnclosed.Contains(l)) shadowRadius = EnclosedLightRadius; // lamp shades (SceneProxies.StaticEnclosed)
                Vector4 l0, l3;
                switch (l.type)
                {
                    case LightType.Point:
                        l0 = new Vector4(pos.x, pos.y, pos.z, Mathf.Max(l.range, 1e-4f));
                        // A point light at the eye (the game's player light) shadows nothing the camera sees: evaluate it
                        // with no ray, outside ReSTIR's competition (Plugin.Noise.cs, EyeLight).
                        l3 = new Vector4(-1f, 1f, EyeLight(pos) ? 0.75f : 0f, 0);
                        break;
                    case LightType.Spot:
                    {
                        l0 = new Vector4(pos.x, pos.y, pos.z, Mathf.Max(l.range, 1e-4f));
                        float a = l.spotAngle * Mathf.Deg2Rad;
                        float cosHalf = Mathf.Cos(a * 0.5f), cosQuarter = Mathf.Cos(a * 0.25f);
                        l3 = new Vector4(cosHalf, 1f / Mathf.Max(cosQuarter - cosHalf, 1e-4f), 0, 0);
                        break;
                    }
                    case LightType.Directional:
                        l0 = new Vector4(-fwd.x, -fwd.y, -fwd.z, 0f);
                        // Directional lights arriving from below the horizon are fill lights faking ground bounce (the game
                        // has no shadows). Shadowing them against the floor would remove them, so leave them unshadowed.
                        // _sunsUnshadowed (diagnostics): every directional light unshadowed (0.75: not a fill, so relight's
                        // fill cut leaves it).
                        // Low side lights (LowSideLight) are unshadowed but kept (0.75) as well.
                        l3 = new Vector4(-1f, 1f, -fwd.y < -0.05f ? 1f : _sunsUnshadowed || LowSideLight(l, mainSun) ? 0.75f : 0f, 0);
                        break;
                    default:
                        continue;
                }
                int i = _lightCount * 4;
                _lightData[i] = l0;
                _lightData[i + 1] = new Vector4(c.r, c.g, c.b, 0);
                _lightMasks[_lightCount] = unchecked((uint)l.cullingMask) | BakedLightMask(l); // Plugin.Noise.cs
                _packedLights[_lightCount] = l;
                _lightData[i + 2] = new Vector4(fwd.x, fwd.y, fwd.z, shadowRadius);
                _lightData[i + 3] = l3;
                _lightCount++;
            }
            DimLavaLights(); // Plugin.Lava.cs: point lights faking the lava glow
            AppendEffectLights(); // Plugin.Effects.cs: spawned effect lights and virtual lights for glowing effects
            FlagPlayerLights();   // Plugin.PlayerLights.cs: V1 never shadows the player's own lights (flashlight, held torch)
            _lightBuffer.SetData(_lightData, 0, 0, Math.Max(1, _lightCount) * 4);
            _lightMaskBuffer.SetData(_lightMasks, 0, 0, Math.Max(1, _lightCount));
        }

        // Lights inside open fixtures (torch bowls, braziers) are moved to just above the rim and get a soft-shadow radius
        // the size of the fixture, like a fire filling the bowl: the bowl casts a soft shadow under itself instead of
        // blacking out everything below its rim. Lights with something solid right above their fixture (ceiling lamps)
        // stay put. y: lift (m), w: soft-shadow radius (m). docs/relight.md, "Light fixtures".
        private readonly Dictionary<Light, Vector4> _fixtureLifts = new Dictionary<Light, Vector4>();
        private SceneProxies _fixtureLiftSource;
        private int _fixtureLiftVersion = -1;
        private bool _fixtureLift = true; // -ultrart-nofixturelift
        private bool _logFixtures;        // -ultrart-logfixtures
        private float _nextLightDump;
        private const float EnclosedLightRadius = 0.03f; // soft-shadow radius of lights inside a static lamp housing / shade
        private const float FixtureLiftAbove = 0.15f, FixtureLiftMax = 1.5f, FixtureRadiusMin = 0.08f, FixtureRadiusMax = 0.75f;

        private void UpdateFixtureLifts()
        {
            var px = _fixtureLift ? _proxies : null;
            if (px == null)
            {
                _fixtureLifts.Clear();
                _fixtureLiftSource = null;
                return;
            }
            if (px == _fixtureLiftSource && px.FixtureVersion == _fixtureLiftVersion) return;
            _fixtureLiftSource = px;
            _fixtureLiftVersion = px.FixtureVersion;
            _fixtureLifts.Clear();
            int covered = 0, tooDeep = 0;
            foreach (var kv in px.FixtureBounds)
            {
                var l = kv.Key;
                if (l == null) continue;
                var b = kv.Value;
                var p = l.transform.position;
                float radius = Mathf.Clamp(Mathf.Min(b.extents.x, b.extents.z), FixtureRadiusMin, FixtureRadiusMax);
                float lift = Mathf.Max(0f, b.max.y + FixtureLiftAbove - p.y);
                var top = new Vector3(p.x, b.max.y + 0.02f, p.z);
                var hitAbove = Physics.Raycast(top, Vector3.up, out var hit, 1f, ~(1 << 2), QueryTriggerInteraction.Ignore);
                if (_logFixtures)
                    Logger.LogInfo($"fixture light {Bench.GameControl.PathOf(l.transform)} at {p} bounds {b.min}..{b.max} lift {lift:F2}" +
                                   (hitAbove ? $" covered by {hit.collider.name} at {hit.distance:F2} m" : ""));
                // Only lights near the top of their fixture (a fire in a bowl). A light in the middle of an orb or skull
                // is the object glowing; moving it above would be wrong.
                if (lift > FixtureLiftMax || b.max.y - p.y > 0.4f * b.size.y) { tooDeep++; continue; }
                if (hitAbove) { covered++; continue; }
                _fixtureLifts[l] = new Vector4(0f, lift, 0f, -radius); // negative: upper half-sphere (UltraRTLights.hlsl)
            }
            Logger.LogInfo($"Fixture lights: {_fixtureLifts.Count} lifted above their fixture, {covered} covered (kept), {tooDeep} too deep in it (kept); {px.StaticEnclosed.Count} in a static housing (soft-shadow radius {EnclosedLightRadius} m)");
        }

        private void LogLightsNear(Vector3 c, float radius)
        {
            var renderers = FindObjectsOfType<Renderer>();
            foreach (var l in FindObjectsOfType<Light>())
            {
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                var p = l.transform.position;
                if ((p - c).magnitude > radius) continue;
                // Small props around the light, plus big transparents (fake light cones / shafts hanging from it).
                var near = renderers.Where(r => r != null && !r.name.EndsWith("[UltraRT]") && r.bounds.SqrDistance(p) < 1f &&
                                                (r.bounds.size.magnitude < 12f || (r.sharedMaterial != null && r.sharedMaterial.renderQueue >= 2500 && r.bounds.size.magnitude < 60f)))
                    .OrderBy(r => r.bounds.size.magnitude).Take(12)
                    .Select(r => $"{r.name}{(r.isPartOfStaticBatch ? "[static]" : "")} L{r.gameObject.layer} {r.bounds.min}..{r.bounds.max} " +
                                 string.Join("+", r.sharedMaterials.Where(m => m != null).Select(m => $"{m.name}/{m.shader.name}/q{m.renderQueue}" +
                                     (m.HasProperty("_SurfaceType") ? $"/st{m.GetFloat("_SurfaceType")}" : ""))));
                Logger.LogInfo($"light near cam: {Bench.GameControl.PathOf(l.transform)} {l.type} at {p} range {l.range:F1} int {l.intensity:F2} col {l.color} " +
                               $"{(l.type == LightType.Spot ? $"angle {l.spotAngle:F0} fwd {l.transform.forward:F2} " : "")}mask 0x{l.cullingMask:X8} on {l.isActiveAndEnabled} " +
                               $"comps {string.Join(",", l.GetComponents<MonoBehaviour>().Where(b => b != null).Select(b => b.GetType().Name))} | {string.Join(" ; ", near)}");
            }
        }

        private void LogEnclosing(Vector3 c)
        {
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (r == null || r.name.EndsWith("[UltraRT]") || !r.bounds.Contains(c) || r.bounds.size.magnitude < 8f) continue;
                var m = r.sharedMaterial;
                Logger.LogInfo($"enclosing camera: {Bench.GameControl.PathOf(r.transform)} {r.GetType().Name} layer {r.gameObject.layer} " +
                               $"enabled {r.enabled}/{r.gameObject.activeInHierarchy} static {r.isPartOfStaticBatch} size {r.bounds.size} " +
                               $"mat {(m != null ? m.name + " / " + m.shader.name : "-")} queue {(m != null ? m.renderQueue : 0)}");
            }
        }

        private static float AsFloat(int bits) => BitConverter.Int32BitsToSingle(bits);

        private void EnsureGBuffer(ref RenderTexture rt, int w, int h, RenderTextureFormat format, string name)
        {
            if (rt != null && rt.width == w && rt.height == h) return;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 0, format) { enableRandomWrite = true, filterMode = FilterMode.Point, name = name };
            rt.Create();
        }

        // ---------------------------------------------------------------- rendering

        // The RT image is drawn inside the main camera's render, after opaques + skybox. Transparents, blood decals,
        // post-processing, the viewmodel camera and UI all still draw on top of it.
        private CameraEvent InjectEvent = CameraEvent.AfterSkybox; // -ultrart-inject overrides the choice below
        private bool _injectForced;
        private CameraEvent _attachedEvent;

        /// <summary>
        /// After the skybox when the camera draws one, so the game's sky is in place behind us; otherwise after the
        /// opaques. Unity never fires AfterSkybox for a camera that clears to a colour (0-1 has no skybox: nothing
        /// ray traced ever reached the screen there).
        /// </summary>
        private CameraEvent InjectEventFor(Camera cam)
        {
            if (_injectForced) return InjectEvent;
            bool sky = cam.clearFlags == CameraClearFlags.Skybox &&
                       (RenderSettings.skybox != null || (cam.TryGetComponent<Skybox>(out var sb) && sb.enabled && sb.material != null));
            return sky ? CameraEvent.AfterSkybox : CameraEvent.AfterForwardOpaque;
        }
        private Camera _attachedCam;
        private static readonly int UltraRTImageId = Shader.PropertyToID("_UltraRTImage");
        private static readonly int UltraRTFlipYId = Shader.PropertyToID("_UltraRTFlipY");
        private static readonly int UltraRTNormalDepthId = Shader.PropertyToID("_UltraRTNormalDepth");
        private static readonly int UltraRTInstanceIdId = Shader.PropertyToID("_UltraRTInstanceId");
        private static readonly int UltraRTGameColorId = Shader.PropertyToID("_UltraRTGameColor");
        private static readonly int UltraRTLightingId = Shader.PropertyToID("_UltraRTLighting");
        private static readonly int UltraRTHybridId = Shader.PropertyToID("_UltraRTHybrid");
        private static readonly int UltraRTDirectId = Shader.PropertyToID("_UltraRTDirect");

        private static readonly int UltraRTAlbedoId = Shader.PropertyToID("_UltraRTAlbedo");
        private static readonly int UltraRTSpecularId = Shader.PropertyToID("_UltraRTSpecular");
        private static readonly int UltraRTShadowBiasId = Shader.PropertyToID("_UltraRTShadowBias");
        private static readonly int UltraRTGIStrengthId = Shader.PropertyToID("_UltraRTGIStrength");
        private static readonly int UltraRTModeId = Shader.PropertyToID("_UltraRTMode");
        private LightSampling _lightSampling;
        private EmissiveLights _emissive;
        private readonly EmissiveLightSettings _emissiveSettings = new EmissiveLightSettings();
        private readonly Light[] _packedLights = new Light[MaxLights];
        private readonly LightSamplingSettings _lightSamplingSettings = new LightSamplingSettings();
        private Denoiser _denoiserDirect;          // hybrid: filters ReSTIR's (noisy) shadowed direct light
        private ComputeShader _unpackHybrid;
        private ComputeShader _motionCopy; // MotionCopy.compute: previous skinned poses for motion vectors
        private RenderTexture _gShadowed, _denoisedDirectOut;
        private float _shadowBias = 0.1f; // -ultrart-shadowbias: how much of our light a surface needs before RT shadows apply fully
        private float _giStrength = 0.35f;
        private int _compositeDebug; // -ultrart-compdebug 1..6: shadow ratio / unshadowed direct / shadowed direct / bounce / portal depth / game-albedo overlay
        private bool _portals = true; // -ultrart-noportals (bench variant: plugin._portals=0): rays ignore portals (docs/portals.md)
        private int _portalHops = 3;  // -ultrart-portal-hops N (bench variant: plugin._portalHops=N): teleports per ray
        private bool _rtMirrors;      // -ultrart-rtmirrors (bench: plugin._rtMirrors=1): camera rays reflect off portal mirrors instead of keeping the game's mirror view
        // Which rays see V1's third-person model (SceneProxies.PlayerMask; docs/reflections.md, "The player in reflections"):
        // 1 reflection rays (incl. water/glass), 2 bounce rays, 4 shadow rays, 8 camera rays after a portal hop (8-x
        // mirrors, as the game's portal views). Camera rays never see it directly. -ultrart-player-rays N, bench plugin._playerRays=N.
        private int _playerRays = 15;
        private static readonly int PlayerRaysId = Shader.PropertyToID("_UltraRTPlayerRays");
        private static readonly int PlayerBoxMinId = Shader.PropertyToID("_UltraRTPlayerBoxMin"), PlayerBoxMaxId = Shader.PropertyToID("_UltraRTPlayerBoxMax");
        private bool _flipY;
        private int _recordedFrame = -1;
        private int _camSwitchLogs;
        private Material _compositeMat;
        private TemporalUpsampler _upsampler; // replace modes: jittered trace -> screen-res history (docs/denoiser.md, "Temporal upsampling")
        private int _upsamplerGen;
        private uint _upsampledFrame = uint.MaxValue - 1;
        private Vector2 _jitter;
        private static readonly int UltraRTUpsampledId = Shader.PropertyToID("_UltraRTUpsampled");

        private void OnCameraPreRender(Camera cam)
        {
            if (cam == null || _cmd == null) return;
            Shader.SetGlobalVector(SoftParticlesId, Vector4.zero); // soft particles: only while the main camera draws (set again in Trace)
            if (cam.name != "Main Camera") { ViewmodelPreRender(cam); return; } // the HUD camera: viewmodel pass (Plugin.Viewmodel.cs)
            if (_recordedFrame == Time.frameCount && cam == _attachedCam) return; // rendered twice in a frame: keep the recording
            _recordedFrame = Time.frameCount;
            _camera = cam;
            var ev = InjectEventFor(cam);
            if (_attachedCam != cam || ev != _attachedEvent)
            {
                if (_attachedCam != null) _attachedCam.RemoveCommandBuffer(_attachedEvent, _cmd);
                cam.AddCommandBuffer(ev, _cmd);
                _attachedCam = cam;
                _attachedEvent = ev;
                if (_camSwitchLogs++ < 20)
                    Logger.LogInfo($"RT attached to camera {Bench.GameControl.PathOf(cam.transform)} at {ev} (id {cam.GetInstanceID()}, " +
                                   $"clear {cam.clearFlags}, target {(cam.targetTexture != null ? cam.targetTexture.name : "screen")}, mask {cam.cullingMask:X})");
            }

            _cmd.Clear();
            TransparentFrameReset();
            VolumetricsFrameReset(cam); // the after-transparents buffer (Plugin.Volumetrics.cs)
            VfxFrameReset(cam);         // screen effects, after it (Plugin.Vfx.cs)
            if (_proxies?.Rtas == null || !_viewEnabled || _ptShader == null || _compositeMat == null) return;
            if (_proxies.Rtas.GetInstanceCount() == 0) return; // dispatching against an empty RTAS hangs the GPU

            try { Trace(); }
            catch (Exception e)
            {
                _cmd.Clear();
                Logger.LogError($"Trace failed, disabling view: {e}");
                _viewEnabled = false;
            }
        }

        private void EnsureTargets(int w, int h)
        {
            if (_output != null && _output.width == w && _output.height == h) return;
            if (_output != null) _output.Release();
            _output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT Output" };
            _output.Create();
            _accumFrames = 0;
            EnsureGBuffer(ref _gRadiance, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Radiance");
            EnsureGBuffer(ref _gAlbedo, w, h, RenderTextureFormat.ARGB32, "UltraRT Albedo");
            EnsureGBuffer(ref _gEmission, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Emission");
            EnsureGBuffer(ref _gNormalDepth, w, h, RenderTextureFormat.ARGBHalf, "UltraRT NormalDepth");
            // x instance index, y packed world-space motion (docs/denoiser.md, "Motion vectors").
            EnsureGBuffer(ref _gInstanceId, w, h, RenderTextureFormat.RGInt, "UltraRT InstanceId");
            EnsureGBuffer(ref _gHybrid, w, h, RenderTextureFormat.ARGBInt, "UltraRT HybridLight");
            // PathTrace writes albedo + emission + material packed into _gSurface (Unity's 8-UAV limit), and the reflection
            // into _gReflection. ReflectionFilter's resolve unpacks _gAlbedo / _gEmission and writes _gSpecular.
            EnsureGBuffer(ref _gSurface, w, h, RenderTextureFormat.ARGBInt, "UltraRT Surface");
            EnsureGBuffer(ref _gReflection, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Reflection");
            EnsureGBuffer(ref _gSpecular, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Specular");
            EnsureGBuffer(ref _gReflDebug, w, h, RenderTextureFormat.ARGB32, "UltraRT Reflection Debug");
        }

        private RenderTexture _gRadiance, _gAlbedo, _gEmission, _gNormalDepth, _gInstanceId, _gHybrid, _gameCopy;
        private RenderTexture _gSurface, _gReflection, _gSpecular, _gReflDebug;
        private string _capturePose;
        private float _lightRadius = 0.15f;   // -ultrart-light-radius (m)

        // Relight knobs (docs/relight.md): -ultrart-relight-falloff/-fill/-ambient/-exposure/-white
        private bool _relightActive;
        private float _relightFalloff = 1f, _relightFillCut = 1f, _relightAmbientCut = 1f, _relightExposure = 1.5f, _relightWhite = 3f;
        private Vector4 RelightParams => _relightActive
            ? new Vector4(_relightFalloff, _relightFillCut, _relightExposure, _relightAmbientCut)
            : Vector4.zero;
        private bool _fixtureShadows = true;  // -ultrart-nofixtureshadows
        private RenderTexture _depthCopy;
        private bool _noDepthMatch;
        private static readonly int UltraRTGameDepthId = Shader.PropertyToID("_UltraRTGameDepth");
        private static readonly int SoftParticlesId = Shader.PropertyToID("_UltraRTSoft");
        private static readonly int UltraRTDepthParamsId = Shader.PropertyToID("_UltraRTDepthParams");
        private static readonly int UltraRTUVScaleId = Shader.PropertyToID("_UltraRTUVScale");
        /// <summary>-ultrart-guard X [Y]: guard band per side as a fraction of the trace width / height (0 = off).</summary>
        private float _guardX = 0.04f, _guardY = 0f;
        /// <summary>Ctrl+F3's "previous build" and "legacy" presets turn the guard band off.</summary>
        private bool _guardActive = true;
        /// <summary>Traced size / visible size this frame (1 without a guard band).</summary>
        private Vector2 _guardScale = Vector2.one;

        /// <summary>
        /// Copy of the game's depth at the injection point, so the composite can leave pixels alone where the game drew
        /// something closer than our ray hit (objects the traced scene doesn't contain).
        /// </summary>
        private void BindGameDepth(bool pt)
        {
            var depth = pt && !_noDepthMatch ? MonoSingleton<PostProcessV2_Handler>.Instance?.depthBuffer : null;
            if (depth == null)
            {
                _cmd.SetGlobalVector(UltraRTDepthParamsId, Vector4.zero);
                return;
            }
            if (_depthCopy == null || _depthCopy.width != depth.width || _depthCopy.height != depth.height || _depthCopy.depthStencilFormat != depth.depthStencilFormat)
            {
                if (_depthCopy != null) _depthCopy.Release();
                var desc = depth.descriptor;
                desc.msaaSamples = 1;
                _depthCopy = new RenderTexture(desc) { name = "UltraRT GameDepth", filterMode = FilterMode.Point };
                _depthCopy.Create();
            }
            _cmd.CopyTexture(depth, _depthCopy);
            _cmd.SetGlobalTexture(UltraRTGameDepthId, _depthCopy, RenderTextureSubElement.Depth);
            var proj = _camera.projectionMatrix;
            // The view ray of a trace uv: the trace's NDC is the screen's divided by the guard scale.
            _cmd.SetGlobalVector(UltraRTDepthParamsId, new Vector4(_guardScale.x / proj.m00, _guardScale.y / proj.m11, 1f, 0f));
        }
        private bool _noScene, _noHook;
        private Material _skyMaterial;

        /// <summary>
        /// The sky the player actually sees: ULTRAKILL draws fake skies as sky spheres (e.g. LimboSkybox/SkySphere, 8-2's
        /// DaySkybox), often with RenderSettings.skybox left at an unused default. Prefer an active sky renderer. When the
        /// active ones use different materials, take the one horizontally nearest the camera (0 inside its footprint):
        /// 8-2 has a day dome over its street (DaySkybox) and a night backdrop (MirrorHider) beside the far side of its
        /// portal, neither of which encloses the camera (docs/relight.md "Area colour").
        /// </summary>
        private static Material FindSkyMaterial(List<Renderer> candidates, Vector3? cam = null)
        {
            Material first = null, nearest = null;
            bool several = false;
            float best = float.MaxValue;
            foreach (var r in candidates)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var m = r.sharedMaterial;
                if (!IsSkyMaterial(m)) continue;
                if (first == null) first = m;
                else if (m != first) several = true;
                if (!cam.HasValue) continue;
                var b = r.bounds;
                var c = cam.Value;
                float dx = Mathf.Max(0f, Mathf.Max(b.min.x - c.x, c.x - b.max.x)), dz = Mathf.Max(0f, Mathf.Max(b.min.z - c.z, c.z - b.max.z));
                float d = dx * dx + dz * dz;
                if (d < best) { best = d; nearest = m; }
            }
            if (several && nearest != null) return nearest;
            if (first != null) return first;
            var s = RenderSettings.skybox;
            return IsSkyMaterial(s) ? s : null;
        }

        /// <summary>Unity's / ULTRAKILL's panoramic sky with a lat-long texture, or ULTRAKILL/Skybox/Skybox in cubemap mode.</summary>
        internal static bool IsSkyMaterial(Material m)
        {
            if (m == null || m.shader == null) return false;
            var n = m.shader.name;
            if (n == "Skybox/Panoramic" || n == "ULTRAKILL/Skybox/Panoramic")
                return m.mainTexture != null && m.mainTexture.dimension == TextureDimension.Tex2D;
            if (n == "ULTRAKILL/Skybox/Skybox")
                return m.IsKeywordEnabled("_TEXMODE_CUBEMAP") && m.HasProperty("_Cubemap") && m.GetTexture("_Cubemap") is Cubemap;
            return false;
        }

        // Cubemap skies (ULTRAKILL/Skybox/Skybox) are converted once per cubemap into the lat-long layout _SkyTex uses. One
        // target per cubemap, so the sky table (keyed by texture) rebuilds when 8-2 switches between its day and night sky.
        private Shader _skyConvertShader;
        private Material _skyConvertMat;
        private readonly Dictionary<Texture, RenderTexture> _skyLatLong = new Dictionary<Texture, RenderTexture>();

        private Texture SkyLatLong(Texture cube)
        {
            if (cube == null || _skyConvertShader == null || !_skyConvertShader.isSupported) return null;
            if (_skyLatLong.TryGetValue(cube, out var rt) && rt != null && rt.IsCreated()) return rt;
            if (_skyLatLong.Count >= 4) ReleaseSkyLatLong();
            if (_skyConvertMat == null) _skyConvertMat = new Material(_skyConvertShader) { name = "UltraRT SkyConvert" };
            rt = new RenderTexture(1024, 512, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, name = "UltraRT SkyLatLong " + cube.name };
            rt.Create();
            _skyConvertMat.SetTexture("_Cube", cube);
            Graphics.Blit(null, rt, _skyConvertMat);
            _skyLatLong[cube] = rt;
            Logger.LogInfo($"RT sky: converted cubemap '{cube.name}' ({cube.width}px) to lat-long");
            return rt;
        }

        private void ReleaseSkyLatLong()
        {
            foreach (var rt in _skyLatLong.Values) if (rt != null) rt.Release();
            _skyLatLong.Clear();
        }

        private void BindSky(RayTracingShader shader)
        {
            var m = _skyMaterial;
            Texture tex = null;
            Color tint = Color.white;
            float rot = 0f;
            if (IsSkyMaterial(m))
            {
                if (m.shader.name == "ULTRAKILL/Skybox/Skybox")
                {
                    // Cubemap times _SkyColor (game units, no colour-space double).
                    tex = SkyLatLong(m.GetTexture("_Cubemap"));
                    tint = m.HasProperty("_SkyColor") ? m.GetColor("_SkyColor") : Color.white;
                }
                else
                {
                    tex = m.mainTexture;
                    var t = m.HasProperty("_Tint") ? m.GetColor("_Tint") : Color.gray;
                    float exposure = m.HasProperty("_Exposure") ? m.GetFloat("_Exposure") : 1f;
                    rot = m.HasProperty("_Rotation") ? m.GetFloat("_Rotation") : 0f;
                    // Skybox/Panoramic: tex * _Tint * unity_ColorSpaceDouble (2 in gamma space) * _Exposure.
                    float k = (QualitySettings.activeColorSpace == ColorSpace.Gamma ? 2f : 4.59479f) * exposure;
                    tint = new Color(t.r * k, t.g * k, t.b * k);
                }
            }
            if (tex == null)
            {
                _cmd.SetRayTracingTextureParam(shader, "_SkyTex", Texture2D.blackTexture);
                _cmd.SetRayTracingVectorParam(shader, "_SkyTint", Vector4.one);
                BindSkyLight(shader, Vector4.zero, null, Color.white, RenderSettings.ambientLight); // also sets _SkyParams
                return;
            }
            _cmd.SetRayTracingTextureParam(shader, "_SkyTex", tex);
            _cmd.SetRayTracingVectorParam(shader, "_SkyTint", new Vector4(tint.r, tint.g, tint.b, 1));
            BindSkyLight(shader, new Vector4(1f, rot * Mathf.Deg2Rad, 0, 0), tex, tint, Color.black); // also sets _SkyParams
        }

        private void LogLightingEnvironment()
        {
            var sky = RenderSettings.skybox;
            var rtSky = _proxies != null ? FindSkyMaterial(_proxies.SkyRenderers, _camera != null ? _camera.transform.position : (Vector3?)null) : null;
            Logger.LogInfo($"RT sky: {(rtSky != null ? $"{rtSky.name} tex {rtSky.mainTexture?.name} {rtSky.mainTexture?.dimension}" : "none (flat ambient colour)")}");
            Logger.LogInfo($"Lighting env: ambientMode={RenderSettings.ambientMode} ambient={RenderSettings.ambientLight} sky={(sky ? sky.shader.name : "none")} skyTex={(sky && sky.HasProperty("_MainTex") && sky.mainTexture ? sky.mainTexture.name : "-")} fog={RenderSettings.fog} fogColor={RenderSettings.fogColor}");
            foreach (var l in FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional))
                Logger.LogInfo($"  directional '{l.name}' enabled={l.isActiveAndEnabled} color={l.color} intensity={l.intensity:F2} mask=0x{l.cullingMask:X8} rot={l.transform.eulerAngles}");
        }

        /// <summary>Bench screenshots: the area's lighting state (what triggers change per area) and the strongest lights near
        /// the camera, so a colour difference against the game can be traced to its source (docs/relight.md "Area colour").</summary>
        private void LogAreaLighting(string tag)
        {
            var cam = Bench.GameControl.Cam != null ? Bench.GameControl.Cam.transform.position : Vector3.zero;
            var sky = RenderSettings.skybox;
            Logger.LogInfo(FormattableString.Invariant(
                $"Area lighting '{tag}': ambientMode={RenderSettings.ambientMode} ambient={RenderSettings.ambientLight} sky/eq/gnd={RenderSettings.ambientSkyColor}/{RenderSettings.ambientEquatorColor}/{RenderSettings.ambientGroundColor} ") +
                FormattableString.Invariant($"_AmbientStrength={Shader.GetGlobalFloat("_AmbientStrength"):F2} _HeightFog={Shader.GetGlobalFloat("_HeightFog"):F0} _Gamma={Shader.GetGlobalFloat("_Gamma"):F2} _LUT={(Shader.GetGlobalTexture("_LUT") ? Shader.GetGlobalTexture("_LUT").name : "-")} _PaletteTex={(Shader.GetGlobalTexture("_PaletteTex") ? Shader.GetGlobalTexture("_PaletteTex").name : "-")} fog={RenderSettings.fog} {RenderSettings.fogColor} {RenderSettings.fogStartDistance:F0}-{RenderSettings.fogEndDistance:F0} ") +
                $"skybox={(sky ? sky.name + "/" + sky.shader.name : "none")} rtSky={(_skyMaterial ? _skyMaterial.name + "/" + _skyMaterial.shader.name : "none")}");
            var lights = FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.intensity > 0f).ToList();
            foreach (var l in lights.Where(l => l.type == LightType.Directional))
                Logger.LogInfo(FormattableString.Invariant($"  area sun '{Bench.GameControl.PathOf(l.transform)}' col {l.color} I {l.intensity:F2} fwd {l.transform.forward:F2} mask 0x{l.cullingMask:X8} shadows {l.shadows}"));
            // Point/spot lights by how much they reach the camera's surroundings in the game's model: colour x intensity x g(d).
            float Reach(Light l) { float d = (l.transform.position - cam).magnitude; float r = Mathf.Max(l.range, 1e-3f); return l.intensity * l.color.maxColorComponent / (1f + 25f * d * d / (4f * r * r)); }
            var near = lights.Where(l => l.type != LightType.Directional).OrderByDescending(Reach).Take(10).ToList();
            float total = lights.Where(l => l.type != LightType.Directional).Sum(Reach);
            Logger.LogInfo($"  area lights: {lights.Count} on, reach-weighted top 10 of total {total:F2}:");
            foreach (var l in near)
                Logger.LogInfo(FormattableString.Invariant($"    {Reach(l) / Mathf.Max(total, 1e-4f):P0} '{Bench.GameControl.PathOf(l.transform)}' {l.type} col {l.color} I {l.intensity:F2} range {l.range:F0} dist {(l.transform.position - cam).magnitude:F1} mask 0x{l.cullingMask:X8}"));
            // Game-unlit (fullbright) materials in view: their fog settings decide how the game's fog swallows them.
            var fwd = Bench.GameControl.Cam != null ? Bench.GameControl.Cam.transform.forward : Vector3.forward;
            var seen = new HashSet<Material>();
            foreach (var r in FindObjectsOfType<Renderer>())
            {
                if (seen.Count >= 30) break;
                if (r == null || !r.enabled || r.name.EndsWith("[UltraRT]")) continue;
                var to = r.bounds.center - cam;
                if (to.magnitude > 80f || Vector3.Dot(to.normalized, fwd) < 0.6f) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    bool named = r.name.IndexOf("andle", StringComparison.OrdinalIgnoreCase) >= 0 || r.transform.parent != null && r.transform.parent.name.IndexOf("andle", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool unlitOrGlow = m.shader.name == "ULTRAKILL/Master" && (!m.IsKeywordEnabled("VERTEX_LIGHTING") || m.IsKeywordEnabled("EMISSIVE"));
                    if (!(named || unlitOrGlow) || !seen.Add(m)) continue;
                    float F(string p) => m.HasProperty(p) ? m.GetFloat(p) : float.NaN;
                    Logger.LogInfo(FormattableString.Invariant($"  area unlit '{Bench.GameControl.PathOf(r.transform)}' L{r.gameObject.layer} d {to.magnitude:F0} mat '{m.name}' q{m.renderQueue} col {(m.HasProperty("_Color") ? m.GetColor("_Color") : Color.clear)} ") +
                                   FormattableString.Invariant($"fog {F("_Fog")} customDists {F("_UseCustomFogDists")} emFog {F("_EmissiveFogStart"):F0}-{F("_EmissiveFogEnd"):F0} emI {F("_EmissiveIntensity"):F2} emCol {(m.HasProperty("_EmissiveColor") ? m.GetColor("_EmissiveColor") : Color.clear)} shader {m.shader.name} kw [{string.Join(" ", m.shaderKeywords)}]"));
                }
            }
        }

        private float _nextRescan;
        private const float RescanInterval = 120f;
        private readonly HashSet<GameObject> _spawnSeen = new HashSet<GameObject>();
        private int _spawnCursor;
        private const double SpawnBudgetMs = 0.5;

        /// <summary>
        /// Objects the game instantiated (Patches.InstantiatePatch): register their renderers and lights, oldest first, within
        /// a small time budget per frame. Most are short-lived effects; destroyed ones cost nothing.
        /// </summary>
        private void RegisterSpawns()
        {
            long t0 = Perf.Now;
            var list = Patches.PendingSpawns;
            long budget = (long)(SpawnBudgetMs / Perf.TickMs);
            while (_spawnCursor < list.Count && Perf.Now - t0 < budget)
            {
                var go = list[_spawnCursor++];
                if (go == null || !_spawnSeen.Add(go)) continue;
                _proxies.AddHierarchy(go);
            }
            if (_spawnCursor >= list.Count)
            {
                list.Clear();
                _spawnCursor = 0;
                _spawnSeen.Clear();
            }
            Perf.Add(Perf.Sec.Hierarchies, t0);
        }
        private double _cpuMs;
        private int _cpuFrames;
        private bool _spawnTest;
        private float _lastTraceScale = -1f;

        private void Trace()
        {
            var (_, kind, mode) = Modes[_mode];
            bool denoised = kind == Kind.Denoised || kind == Kind.Hybrid;
            SetPlayerSplit(kind == Kind.Denoised); // Plugin.Noise.cs
            bool pt = kind != Kind.Debug;
            bool resolve = denoised || kind == Kind.Reflection; // modes that read the unpacked surface / filtered reflection
            if (denoised && _lastKind != Kind.Denoised && _lastKind != Kind.Hybrid) { _denoiser.Reset(); _denoiserDirect?.Reset(); } // history is stale after other modes
            if (pt && _lastKind == Kind.Debug) _lightSampling.Reset();
            if (resolve && _lastKind != Kind.Denoised && _lastKind != Kind.Hybrid && _lastKind != Kind.Reflection) _reflFilter.Reset();
            _lastKind = kind;
            if (resolve) _accumulate = false; // the denoiser and the reflection filter need unjittered primary rays
            // Temporal upsampling (pt-dn, relight, pathtrace, dn-*): jitter the trace grid over the screen pixels;
            // Upsample.compute accumulates the result at screen resolution using the game's depth buffer.
            var depthBuf = pt && !_noDepthMatch ? MonoSingleton<PostProcessV2_Handler>.Instance?.depthBuffer : null;
            bool full = PathTraceActive; // the pathtrace mode: RayGenPT, its own trace scale (Plugin.PathTrace.cs)
            bool rr = RayReconstructionFor(kind, depthBuf); // DLSS RR replaces the denoiser and the upsampler (Plugin.Dlss.cs)
            float scale = pt ? TraceScaleFor(full, rr) : 1f;
            if (scale != _lastTraceScale) { _lastTraceScale = scale; _lightSampling.Reset(); } // reservoirs are per trace pixel
            int w = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelWidth * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelHeight * scale));
            bool taau = kind == Kind.Denoised && _upsampler != null && _upsampler.Settings.enabled && depthBuf != null;
            _jitter = taau && _upsampler.Settings.jitter ? _upsampler.NextJitter(depthBuf.width, depthBuf.height, w, h) : Vector2.zero;
            if (rr) _jitter = _rr.NextJitter();
            // Guard band (docs/denoiser.md): trace a slightly wider view than the screen, so surfaces entering the screen
            // while turning already have denoiser/ReSTIR history and warm cache cells. The visible part keeps its pixel grid.
            int gxPx = pt && _guardActive ? Mathf.RoundToInt(w * Mathf.Clamp(_guardX, 0f, 0.25f)) : 0;
            int gyPx = pt && _guardActive ? Mathf.RoundToInt(h * Mathf.Clamp(_guardY, 0f, 0.25f)) : 0;
            _guardScale = new Vector2((float)(w + 2 * gxPx) / w, (float)(h + 2 * gyPx) / h);
            w += 2 * gxPx;
            h += 2 * gyPx;
            if (rr) ExactGridTrace(depthBuf.width, depthBuf.height, gxPx, gyPx, ref w, ref h); // RR at an integer upscale (Plugin.Dlss.cs)
            EnsureTargets(w, h);

            long traceStart = Perf.Now;
            var cpu = Stopwatch.StartNew();
            if (Patches.PendingHierarchies.Count > 0)
            {
                long th = Perf.Now;
                foreach (var go in Patches.PendingHierarchies) _proxies.AddHierarchy(go);
                Patches.PendingHierarchies.Clear();
                Perf.Add(Perf.Sec.Hierarchies, th);
            }
            if (Patches.PendingSpawns.Count > 0) RegisterSpawns();
            if (_proxies.DeferredCount > 0)
            {
                long tq = Perf.Now;
                // Proxies for renderers found after the build. A checkpoint clones whole areas (8-3: ~3,000 renderers),
                // which made one 700 ms frame; a larger queue gets a larger share so it still drains within seconds.
                _proxies.ProcessDeferred(_proxies.DeferredCount > 256 ? 2.0 : 0.75);
                Perf.Add(Perf.Sec.Hierarchies, tq);
            }
            if (Patches.SpawnsOverflowed)
            {
                Patches.SpawnsOverflowed = false;
                _nextRescan = Mathf.Min(_nextRescan, Time.realtimeSinceStartup); // lost some: let the rescan find them
            }
            if (Time.realtimeSinceStartup >= _nextRescan)
            {
                // Safety net for renderers that don't come through a hook (e.g. Cyber Grind's runtime combined mesh).
                // Spawns come through Patches.InstantiatePatch now, so this is rare: the scene-wide find alone costs
                // ~7 ms in 8-3 (27k mesh renderers).
                _nextRescan = Time.realtimeSinceStartup + RescanInterval;
                long tr = Perf.Now;
                var sw = Stopwatch.StartNew();
                _proxies.Rescan();
                Perf.Add(Perf.Sec.Rescan, tr);
                Logger.LogInfo($"Rescan took {sw.Elapsed.TotalMilliseconds:F2} ms");
            }
            _proxies.SyncDynamic();
            _proxies.UpdateLava(); // scroll offsets of the lava proxies (SceneProxies.Lava.cs)
            _proxies.UpdateFrosted(); // frosted (ice) scene proxies on/off (SceneProxies.Transparent.cs)
            SceneProxies.PortalHops = _portalHops;
            SceneProxies.TraceMirrors = _rtMirrors;
            _proxies.SyncPortals(_portals); // portal quads + travel matrices (docs/portals.md); binds _UltraRTPortals
            Shader.SetGlobalInt(PlayerRaysId, SceneProxies.TracePlayerModel ? _playerRays & 15 : 0);
            if (_proxies.PlayerShadowBox(_camera.transform.position, out var playerBox))
            {
                Shader.SetGlobalVector(PlayerBoxMinId, playerBox.min);
                Shader.SetGlobalVector(PlayerBoxMaxId, playerBox.max);
            }
            else
            {
                Shader.SetGlobalVector(PlayerBoxMinId, Vector4.one);
                Shader.SetGlobalVector(PlayerBoxMaxId, Vector4.zero);
            }
            UpdateMovers(); // moving occluders: fast-responding shadows of the player and enemies (Plugin.Noise.cs)
            UpdateEffectGeometry(pt); // glowing effects as billboards for reflection rays (Plugin.Effects.cs)
            long te = Perf.Now;
            _emissive.Update(_proxies, _camera.transform.position);
            UpdateRain(); // the game's rain on surfaces (Plugin.PlayerLights.cs; SceneProxies.Rain.cs)
            UpdateWet();  // rain wetness (Plugin.Wet.cs)
            UpdateDebrisGlow(); // explosion debris cools down (Plugin.Vfx.cs)
            Perf.Add(Perf.Sec.Emissive, te);
            _cpuMs += cpu.Elapsed.TotalMilliseconds;
            _cpuFrames++;
            if (_cpuFrames >= 600)
            {
                var mv = _proxies.Motion;
                Logger.LogInfo($"RT scene sync CPU avg {_cpuMs / _cpuFrames:F3} ms/frame; motion vectors {(MotionVectors.Enabled ? "on" : "off")}: " +
                               $"{mv.SlotCount} slots, {mv.CopiesLastFrame} skinned pose copies/frame, {mv.SkinVertices} pose vertices, " +
                               $"{(double)mv.RigidUpdates / _cpuFrames:F1} rigid updates/frame, {(double)_proxies.SkinnedTransformUpdateCount / _cpuFrames:F1} skinned transform updates/frame, motion CPU {mv.Ticks * 1000.0 / Stopwatch.Frequency / _cpuFrames:F3} ms/frame; " +
                               $"deferred proxies: {_proxies.DeferredMade} made in {_proxies.DeferredMs:F1} ms (queue max {_proxies.DeferredMaxQueue}, now {_proxies.DeferredCount}), " +
                               $"{_proxies.HierarchiesAdded} hierarchies ({Patches.SpawnCount} instantiated), {_proxies.Lights.Count} lights tracked ({_activeLights.Count} on); " +
                               $"emissive: {_emissive.AliasBuilds} alias rebuilds ({_emissive.AliasTicks * 1000.0 / Stopwatch.Frequency:F1} ms), {_emissive.ActiveUpdates} active-set updates, " +
                               $"new {_emissive.PendingTicks * 1000.0 / Stopwatch.Frequency:F1} ms, visibility {_emissive.VisibilityTicks * 1000.0 / Stopwatch.Frequency:F1} ms, transforms {_emissive.TransformTicks * 1000.0 / Stopwatch.Frequency:F1} ms; " +
                               $"{_emissive.EmitterCount} emitters, {_emissive.OwnerCount} owners; dynamic: {_proxies.DirtyEvaluations} dirty evaluations " +
                               $"({_proxies.DirtyTicks * Perf.TickMs:F1} ms), active loop {_proxies.ActiveTicks * Perf.TickMs:F1} ms over {_proxies.ActiveDynamicCount}, poll {_proxies.PollTicks * Perf.TickMs:F1} ms");
                Logger.LogInfo($"Material sync ({Patches.PortalClones} portal clones registered so far): {_proxies.SyncSwaps} swaps, {_proxies.SyncStaticSwaps} static slot swaps, {_proxies.SyncBlockUpdates} property-block updates " +
                               $"({_proxies.SyncHot} proxies followed), {_proxies.SyncPropertyUpdates} material property re-copies; {_proxies.SyncTicks * Perf.TickMs / _cpuFrames:F4} ms/frame " +
                               $"(swap checks {_proxies.SyncSwapTicks * Perf.TickMs / _cpuFrames:F4}, blocks {_proxies.SyncHotTicks * Perf.TickMs / _cpuFrames:F4}, re-copies {_proxies.SyncCopyTicks * Perf.TickMs / _cpuFrames:F4}, static {_proxies.SyncStaticTicks * Perf.TickMs / _cpuFrames:F4})");
                _proxies.SyncSwaps = _proxies.SyncStaticSwaps = _proxies.SyncBlockUpdates = _proxies.SyncPropertyUpdates = 0;
                _proxies.SyncTicks = _proxies.SyncSwapTicks = _proxies.SyncHotTicks = _proxies.SyncCopyTicks = _proxies.SyncStaticTicks = 0;
                if (_proxies.PlayerProxiesTotal > 0) Logger.LogInfo(_proxies.DescribePlayerModel(_camera.transform.position));
                _proxies.DirtyEvaluations = 0; _proxies.DirtyTicks = _proxies.ActiveTicks = _proxies.PollTicks = 0;
                _emissive.AliasBuilds = 0; _emissive.AliasTicks = 0; _emissive.ActiveUpdates = 0; _emissive.PendingTicks = _emissive.VisibilityTicks = _emissive.TransformTicks = 0;
                _proxies.DeferredMade = 0; _proxies.DeferredMs = 0; _proxies.DeferredMaxQueue = 0; _proxies.HierarchiesAdded = 0; Patches.SpawnCount = 0;
                mv.Ticks = 0; mv.RigidUpdates = 0; _proxies.SkinnedTransformUpdateCount = 0;
                _cpuMs = 0; _cpuFrames = 0;
            }

            var viewProj = Matrix4x4.Scale(new Vector3(1f / _guardScale.x, 1f / _guardScale.y, 1f)) * _camera.projectionMatrix * _camera.worldToCameraMatrix;
            // Jitter: pixel p's primary ray goes through trace position p + 0.5 + _jitter. Every temporal consumer reprojects
            // world positions with this (jittered) matrix, so their histories stay consistent.
            if (_jitter != Vector2.zero) viewProj = Matrix4x4.Translate(new Vector3(-2f * _jitter.x / w, -2f * _jitter.y / h, 0f)) * viewProj;
            if (!_accumulate || !Approximately(viewProj, _lastViewProj) || _mode != _lastMode) _accumFrames = 0;
            _lastViewProj = viewProj;
            _lastMode = _mode;
            var invViewProj = viewProj.inverse;

            _cmd.Clear();
            _gpu?.Begin(_cmd, SlotTotal);
            _cmd.BeginSample(SampleBuild);
            _gpu?.Begin(_cmd, SlotRtas);
            _cmd.BuildRayTracingAccelerationStructure(_proxies.Rtas);
            _gpu?.End(_cmd, SlotRtas);
            _cmd.EndSample(SampleBuild);

            var shader = pt ? _ptShader : _debugShader;
            shader.SetShaderPass("UltraRTHit");
            _cmd.SetRayTracingAccelerationStructure(shader, "_Scene", _proxies.Rtas);
            _cmd.SetRayTracingTextureParam(shader, "_Output", _output);
            _cmd.SetRayTracingMatrixParam(shader, "_InvViewProj", invViewProj);
            _cmd.SetRayTracingVectorParam(shader, "_CamPos", _camera.transform.position);
            _cmd.SetRayTracingIntParam(shader, "_Mode", kind == Kind.Hybrid ? 3 : resolve || mode >= CacheDebugModeBase ? 0 : mode);
            _cmd.SetRayTracingFloatParam(shader, "_GIStrength", _giStrength);
            _cmd.SetRayTracingFloatParam(shader, "_LightRadius", _lightRadius);
            _relightActive = RelightLook(Modes[_mode].name) || _relightForce;
            _cmd.SetRayTracingVectorParam(shader, "_Relight", RelightParams);
            _cmd.SetGlobalFloat("_UltraRTTonemapWhite", _relightActive ? _relightWhite : 0f); // BindExposure refines this for denoised modes
            SkyGlobals(); // relight linear transport flag (Plugin.SkyGI.cs)
            _cmd.SetRayTracingFloatParam(shader, "_FixtureShadows", _fixtureShadows ? 1f : 0f);

            if (pt)
            {
                long tl = Perf.Now;
                GatherLights();
                Perf.Add(Perf.Sec.Lights, tl);
                var ambient = RenderSettings.ambientLight;

                _cmd.SetRayTracingBufferParam(shader, "_Lights", _lightBuffer);
                _cmd.SetRayTracingBufferParam(shader, "_LightMasks", _lightMaskBuffer);
                _cmd.SetRayTracingIntParam(shader, "_LightCount", _lightCount);
                long tls = Perf.Now;
                PathTraceLightsBegin(full, rr); // RR may want another light sampling mode (restored below)
                _proxies.PortalViewpoints(_camera.transform.position, _lightSampling.ViewPoints); // portal-aware importance
                _lightSampling.Bind(_cmd, shader, w, h, viewProj, _camera.transform.position, _packedLights, _lightData, _lightCount, _emissive.SetChanged, kind == Kind.Hybrid);
                Perf.Add(Perf.Sec.LightSampling, tls);
                _emissive.Bind(_cmd, shader, _lightSamplingSettings.mode);
                _cmd.SetRayTracingIntParam(shader, "_FrameIndex", (int)_frameIndex);
                _cmd.SetRayTracingIntParam(shader, "_AccumFrames", (int)_accumFrames);
                _cmd.SetRayTracingVectorParam(shader, "_SkyColor", new Vector4(ambient.r, ambient.g, ambient.b, 1));
                BindSky(shader);
                // The game's lit term is 2 * ambient + lights.
                _cmd.SetRayTracingVectorParam(shader, "_Ambient", new Vector4(ambient.r, ambient.g, ambient.b, 1) * 2f);
                _cmd.SetRayTracingFloatParam(shader, "_PrimaryAmbient", _primaryAmbient);
                _cmd.SetRayTracingFloatParam(shader, "_BounceAmbient", 1f);
                _cmd.SetRayTracingFloatParam(shader, "_LightScale", 1f);
                _cmd.SetRayTracingFloatParam(shader, "_EmissionScale", 1f);
                _cmd.SetRayTracingTextureParam(shader, "_Radiance", _gRadiance);
                _cmd.SetRayTracingTextureParam(shader, "_Surface", _gSurface);
                _cmd.SetRayTracingTextureParam(shader, "_Reflection", _gReflection);
                _cmd.SetRayTracingVectorParam(shader, "_ReflParams", _reflSettings.ShaderParams);
                _cmd.SetRayTracingFloatParam(shader, "_ReflMaxRadiance", Mathf.Max(0.01f, _reflSettings.maxRadiance));
                _cmd.SetRayTracingVectorParam(shader, "_ReflCache", _reflSettings.CacheParams);
                _cmd.SetRayTracingTextureParam(shader, "_NormalDepth", _gNormalDepth);
                _cmd.SetRayTracingTextureParam(shader, "_InstanceId", _gInstanceId);
                _cmd.SetRayTracingTextureParam(shader, "_HybridLight", _gHybrid);
                TraceRadianceCache(shader, w, h, invViewProj, ambient);
                BindTransparents(shader, denoised); // water and glass (Plugin.Transparent.cs)
                BindBloodWet(shader); // wet blood: the wetness mask of fresh stains (Plugin.Wet.cs)
                BindPathTrace(shader, full, rr);
                PathTraceLightsEnd();
            }
            else
            {
                _cmd.SetRayTracingFloatParam(shader, "_DepthScale", 150f);
            }

            _cmd.BeginSample(SampleTrace);
            _gpu?.Begin(_cmd, SlotDispatch);
            _cmd.DispatchRays(shader, pt && full ? "RayGenPT" : "RayGen", (uint)w, (uint)h, 1);
            _gpu?.End(_cmd, SlotDispatch);
            _cmd.EndSample(SampleTrace);
            _proxies.RecordMotionCopies(_cmd); // this frame's skinned poses become next frame's previous poses
            if (kind == Kind.PathTrace) TraceCacheDebug(mode);
            if (pt) TraceParticleLight(kind); // lit particles' lighting volume (Plugin.Particles.cs)

            _displayed = _output;
            if (resolve)
            {
                // Reflections: filter, then unpack albedo / emission and resolve the specular term (also with reflections off).
                var reflInputs = new ReflectionInputs
                {
                    surface = _gSurface, reflection = _gReflection, normalDepth = _gNormalDepth, instanceId = _gInstanceId,
                    viewProj = viewProj, invViewProj = invViewProj, camPos = _camera.transform.position,
                    albedo = _gAlbedo, emission = _gEmission, specular = _gSpecular, debug = _gReflDebug,
                };
                _cmd.BeginSample(SampleReflections);
                _reflFilter.Execute(_cmd, reflInputs, _reflSettings, kind == Kind.Hybrid, kind == Kind.Reflection ? mode : 0);
                _cmd.EndSample(SampleReflections);
                if (kind == Kind.Reflection) _displayed = _gReflDebug;
            }
            if (denoised)
            {
                if (_denoised == null || _denoised.width != w || _denoised.height != h)
                {
                    if (_denoised != null) _denoised.Release();
                    _denoised = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                        { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT Denoised" };
                    _denoised.Create();
                }
                _denoiser.Settings.debugView = (DenoiserDebugView)mode;
                var inputs = new DenoiserInputs
                {
                    radiance = _gRadiance, albedo = _gAlbedo, emission = _gEmission,
                    normalDepth = _gNormalDepth, instanceId = _gInstanceId, specular = _gSpecular,
                    moverFlags = _gRadiance, // a = 2: moving-occluder flag (docs/denoiser.md, "Moving shadows")
                    viewProj = viewProj, invViewProj = invViewProj,
                    camPos = _camera.transform.position,
                };
                _cmd.BeginSample(SampleDenoise);
                _gpu?.Begin(_cmd, SlotDenoise);
                _denoiser.FrameTime = Time.unscaledDeltaTime;
                BindExposure();
                VfxBloomSource(kind, w, h, rr, depthBuf); // the composite also writes the HDR bloom source (Plugin.Vfx.cs)
                if (!rr) _denoiser.Execute(_cmd, inputs, _denoised);
                if (!rr) RunExposure(w, h); // relight auto exposure: measured after the denoiser, applied from the next frame
                if (kind == Kind.Hybrid && _unpackHybrid != null)
                {
                    // ReSTIR's shadowed direct light is a 1-shadow-ray estimate: unpack it and filter it separately.
                    EnsureGBuffer(ref _gShadowed, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Shadowed");
                    EnsureGBuffer(ref _denoisedDirectOut, w, h, RenderTextureFormat.ARGB32, "UltraRT DenoisedDirect");
                    int k = _unpackHybrid.FindKernel("Unpack");
                    _cmd.SetComputeTextureParam(_unpackHybrid, k, "_Packed", _gHybrid);
                    _cmd.SetComputeTextureParam(_unpackHybrid, k, "_Shadowed", _gShadowed);
                    _cmd.SetComputeIntParams(_unpackHybrid, "_Dim", w, h);
                    _cmd.DispatchCompute(_unpackHybrid, k, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
                    var directInputs = inputs;
                    directInputs.radiance = _gShadowed;
                    directInputs.specular = null;
                    _denoiserDirect.Settings.debugView = DenoiserDebugView.Final;
                    _denoiserDirect.FrameTime = Time.unscaledDeltaTime;
                    _denoiserDirect.Execute(_cmd, directInputs, _denoisedDirectOut);
                }
                _gpu?.End(_cmd, SlotDenoise);
                _cmd.EndSample(SampleDenoise);
                _displayed = _denoised;
                TraceTransparents(viewProj); // water and glass (Plugin.Transparent.cs)
            }

            BindGameDepth(pt);
            // Soft particles (LitParticle.shader): the depth copy is this camera's, from here to the end of its render.
            _cmd.SetGlobalVector(SoftParticlesId, _depthCopy != null && pt && !_noDepthMatch && _particleSettings.soft
                ? new Vector4(1f / Mathf.Max(0.02f, _particleSettings.softDistance), 1f, 0f, 0f) : Vector4.zero);
            bool upsampled = false;
            if (rr && _depthCopy != null && RunRayReconstruction(w, h, viewProj, new Vector2Int(gxPx, gyPx))) upsampled = true;
            else if (taau && _displayed == _denoised && _depthCopy != null)
            {
                _gpu?.Begin(_cmd, SlotUpsample);
                // Its history goes with the denoiser's, and is stale after frames it didn't run (F2, other modes).
                bool reset = _denoiser.Generation != _upsamplerGen || _upsampledFrame != _frameIndex - 1;
                _upsamplerGen = _denoiser.Generation;
                _upsampledFrame = _frameIndex;
                _upsampler.Execute(_cmd, _camera, _denoised, _gNormalDepth, _gInstanceId, _depthCopy,
                    new Vector2Int(gxPx, gyPx), _guardScale, _jitter, reset);
                _gpu?.End(_cmd, SlotUpsample);
                _cmd.SetGlobalTexture(UltraRTUpsampledId, _upsampler.Output);
                upsampled = true;
            }

            // Composite into the camera's current target without changing render-target bindings.
            _gpu?.Begin(_cmd, SlotBlit);
            _cmd.SetGlobalTexture(UltraRTImageId, _displayed);
            _cmd.SetGlobalVector(UltraRTUVScaleId, new Vector4(1f / _guardScale.x, 1f / _guardScale.y, upsampled ? 1f : 0f, 0f));
            // Hybrid: keep the game's shaded colour and apply the RT/game lighting ratio. Needs a copy of the game's
            // colour target as it is right now (opaques + sky drawn), since the composite draws into it.
            var gameTex = kind == Kind.Hybrid ? MonoSingleton<PostProcessV2_Handler>.Instance?.mainTex : null;
            bool ratio = gameTex != null && _denoiser.FilteredLighting != null;
            if (ratio)
            {
                if (_gameCopy == null || _gameCopy.width != gameTex.width || _gameCopy.height != gameTex.height || _gameCopy.format != gameTex.format)
                {
                    if (_gameCopy != null) _gameCopy.Release();
                    var desc = gameTex.descriptor;
                    desc.depthBufferBits = 0;
                    desc.msaaSamples = 1;
                    desc.enableRandomWrite = false;
                    _gameCopy = new RenderTexture(desc) { name = "UltraRT GameColor", filterMode = FilterMode.Point };
                    _gameCopy.Create();
                }
                _cmd.CopyTexture(gameTex, _gameCopy);
                _cmd.SetGlobalTexture(UltraRTGameColorId, _gameCopy);
                _cmd.SetGlobalTexture(UltraRTLightingId, _denoiser.FilteredLighting);
                _cmd.SetGlobalTexture(UltraRTHybridId, _gHybrid);
                _cmd.SetGlobalTexture(UltraRTDirectId, _denoiserDirect?.FilteredLighting != null ? (Texture)_denoiserDirect.FilteredLighting : _gShadowed);
                _cmd.SetGlobalTexture(UltraRTAlbedoId, _gAlbedo);
                _cmd.SetGlobalTexture(UltraRTSpecularId, _gSpecular);
                _cmd.SetGlobalFloat(UltraRTShadowBiasId, _shadowBias);
                _cmd.SetGlobalFloat(UltraRTGIStrengthId, _giStrength);
            }
            _cmd.SetGlobalFloat(UltraRTModeId, ratio ? (_compositeDebug > 0 ? 1f + _compositeDebug : 1f) : 0f);
            _cmd.SetGlobalTexture(UltraRTNormalDepthId, pt ? (Texture)_gNormalDepth : Texture2D.whiteTexture);
            _cmd.SetGlobalTexture(UltraRTInstanceIdId, _gInstanceId); // portal pixels: distance to the portal (Composite.shader)
            _cmd.SetGlobalFloat(UltraRTFlipYId, _flipY ? 1f : 0f);
            _cmd.DrawProcedural(Matrix4x4.identity, _compositeMat, 0, MeshTopology.Triangles, 3);
            _gpu?.End(_cmd, SlotBlit);
            TraceVolumetrics(kind, upsampled); // sun shafts, light halos, the game's fog on RT pixels (Plugin.Volumetrics.cs)
            CopyPreAlpha(); // refraction background for the water overlays (Plugin.Transparent.cs)
            TraceVfx(kind); // bloom, heat haze, shockwaves, underwater: after the transparents (Plugin.Vfx.cs)
            _gpu?.End(_cmd, SlotTotal);
            if (_gpu != null)
            {
                _gpu.EndFrame(_cmd);
                SampleGpuTimings();
            }

            ViewmodelTraced(kind); // the HUD camera lights the viewmodel against this frame (Plugin.Viewmodel.cs)
            _frameIndex++;
            Perf.Add(Perf.Sec.Trace, traceStart);
            if (pt && _accumulate) _accumFrames++;
            if (_tracedFrames++ == 0) Logger.LogInfo($"First trace OK: {w}x{h}, instances={_proxies.Rtas.GetInstanceCount()}, lights={_lightCount}, cache={(_radianceCache?.Active == true ? $"{_cacheSettings.mode} {_radianceCache.Capacity} slots {_radianceCache.GpuBytes >> 20} MB" : "off")}");
        }

        // Debug markers (visible in PIX/RenderDoc). Unity's Recorder reports 0 GPU time in this release player;
        // real GPU timings come from the native timer plugin.
        private const string SampleBuild = "UltraRT.BuildRTAS", SampleTrace = "UltraRT.DispatchRays", SampleDenoise = "UltraRT.Denoise",
            SampleReflections = "UltraRT.Reflections";

        // ---------------------------------------------------------------- GPU timing (opt-in, -ultrart-gputimer)

        private const int SlotTotal = 0, SlotRtas = 1, SlotDispatch = 2, SlotDenoise = 3, SlotBlit = 4, SlotCache = 5, SlotExposure = 6, SlotPreAlpha = 7, SlotUpsample = 8, SlotVolumetrics = 9, SlotVolLate = 10, SlotCount = 21; // 11-15: viewmodel pass and its stages (Plugin.Viewmodel.cs), 16: SlotParticles (Plugin.Particles.cs), 17-18: DLSS RR (Plugin.Dlss.cs), 19-20: screen effects (Plugin.Vfx.cs)
        private static readonly string[] SlotNames = { "total", "rtas", "dispatch", "denoise", "blit", "cache", "exposure", "prealpha", "upsample", "volumetrics", "vollate", "viewmodel", "vm-gbuffer", "vm-rays", "vm-filter", "vm-composite", "particles", "rr", "rr-eval", "vfx", "vfxlate" };
        private bool _useGpuTimer;
        private GpuTimer _gpu;
        private long _lastResolved;
        private readonly double[] _gpuSum = new double[SlotCount];
        private readonly int[] _gpuN = new int[SlotCount];

        /// <summary>Accumulate each newly resolved frame's per-slot timings (values lag 2-4 frames).</summary>
        private void SampleGpuTimings()
        {
            long resolved = _gpu.ResolvedFrames;
            if (resolved == _lastResolved) return;
            _lastResolved = resolved;
            for (int s = 0; s < SlotCount; s++)
                if (_gpu.TryGetMs(s, out var ms) && ms > 0f) { _gpuSum[s] += ms; _gpuN[s]++; }
        }

        /// <summary>Averages since the last call, then resets. Empty if the timer is off.</summary>
        private string TakeGpuSummary()
        {
            if (_gpu == null) return "";
            var parts = Enumerable.Range(0, SlotCount).Where(s => _gpuN[s] > 0)
                .Select(s => $"{SlotNames[s]}={_gpuSum[s] / _gpuN[s]:F3}").ToArray(); // materialise before clearing
            Array.Clear(_gpuSum, 0, SlotCount);
            Array.Clear(_gpuN, 0, SlotCount);
            return "GPU ms: " + string.Join(" ", parts);
        }

        private static bool Approximately(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }

        // ---------------------------------------------------------------- unattended testing

        private IEnumerator AutoRun()
        {
            float t0 = Time.realtimeSinceStartup;
            bool skippedIntro = false;
            while (SafeCurrentScene() != "Main Menu" || SceneHelper.PendingScene != null)
            {
                // Skip the intro sequence: jump straight to the main menu once the intro scene is up.
                if (!skippedIntro && SafeCurrentScene() == "Intro" && SceneHelper.PendingScene == null) { skippedIntro = true; SceneHelper.LoadScene("Main Menu"); }
                if (Time.realtimeSinceStartup - t0 > 120f) { Logger.LogError("Timed out waiting for main menu"); yield break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3f);
            SceneHelper.LoadScene(_autoScene);
            yield return null;
            while (SceneHelper.PendingScene != null || _proxies == null)
            {
                if (Time.realtimeSinceStartup - t0 > 180f) { Logger.LogError("Timed out waiting for level/RT setup"); break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(4f);

            if (!string.IsNullOrEmpty(_capturePose))
            {
                var v = _capturePose.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Bench.GameControl.TakeControl(new Bench.CamPose(new Vector3(v[0], v[1], v[2]), v[3], v[4]));
                yield return new WaitForSecondsRealtime(1f);
            }
            if (_spawnTest) yield return SpawnTestEnemy();

            if (_capture)
            {
                string tag = new string(_autoScene.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                var report = new StringBuilder($"# UltraRT capture {DateTime.Now:yyyy-MM-dd HH:mm:ss} {_autoScene} scale={_scale}\n");
                bool prevView = _viewEnabled, prevAccum = _accumulate;
                int prevMode = _mode;

                // Uncapped frame-time cost: game alone vs. each tracing mode.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                _viewEnabled = false;
                yield return Measure("game only", report);
                _viewEnabled = true;
                _accumulate = false;
                foreach (string name in new[] { "pt-dn", "pt", "lit" })
                {
                    int m = Array.FindIndex(Modes, x => x.name == name);
                    _mode = m;
                    yield return Measure($"+ {Modes[m].name} @ {(Modes[m].kind != Kind.Debug ? _scale : 1f)}x", report);
                }

                _viewEnabled = false;
                yield return null; yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"dv-{tag}-game.png"));
                yield return null; yield return null;
                _viewEnabled = true;
                _mode = Array.FindIndex(Modes, x => x.name == "pt-dn");
                for (int f = 0; f < 30; f++) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"dv-{tag}-composited-{_attachedEvent}.png"));
                yield return null; yield return null;
                _viewEnabled = false;
                yield return null; yield return null;
                _viewEnabled = true;

                for (int m = 0; m < Modes.Length; m++)
                {
                    _mode = m;
                    _accumulate = false;
                    yield return null; yield return null;
                    if (Modes[m].kind == Kind.Denoised || Modes[m].kind == Kind.Reflection)
                        for (int f = 0; f < 60; f++) yield return null; // let temporal history build up
                    yield return new WaitForEndOfFrame();
                    SaveOutput(Path.Combine(_outDir, $"dv-{tag}-{m:00}-{Modes[m].name}.png"));
                    if (Modes[m].kind == Kind.PathTrace)
                    {
                        // Reference image: freeze game time so the camera holds still, accumulate up to 256 frames.
                        float prevTimeScale = Time.timeScale;
                        Time.timeScale = 0f;
                        _accumulate = true;
                        _accumFrames = 0;
                        float deadline = Time.realtimeSinceStartup + 8f;
                        while (_accumFrames < 256 && Time.realtimeSinceStartup < deadline) yield return null;
                        yield return new WaitForEndOfFrame();
                        SaveOutput(Path.Combine(_outDir, $"dv-{tag}-{m:00}-{Modes[m].name}-accum{_accumFrames}.png"));
                        report.AppendLine($"{Modes[m].name}: accumulated {_accumFrames} frames");
                        _accumulate = false;
                        Time.timeScale = prevTimeScale;
                    }
                }

                File.WriteAllText(Path.Combine(_outDir, $"dv-{tag}-timing.md"), report.ToString());
                Logger.LogInfo(report.ToString());
                _viewEnabled = prevView; _accumulate = prevAccum; _mode = prevMode;
                Logger.LogInfo("Capture done");
            }
            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
            }
        }

        /// <summary>Test hook: wake one of the level's dormant enemies a few metres in front of the camera.</summary>
        private IEnumerator SpawnTestEnemy()
        {
            var cam = Camera.allCameras.FirstOrDefault(c => c.name == "Main Camera") ?? Camera.main;
            var enemy = Resources.FindObjectsOfTypeAll<EnemyIdentifier>()
                .Where(e => e != null && e.gameObject.scene.IsValid() && !e.gameObject.activeInHierarchy)
                .OrderBy(e => e.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0 ? 0 : 1)
                .FirstOrDefault();
            if (enemy == null || cam == null) { Logger.LogWarning("Spawn test: no dormant enemy found"); yield break; }

            var t = enemy.transform;
            t.SetParent(null, true);
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            t.position = cam.transform.position + fwd * 7f + Vector3.down * 1.5f;
            t.rotation = Quaternion.LookRotation(-fwd, Vector3.up);
            enemy.gameObject.SetActive(true);
            Logger.LogInfo($"Spawn test: activated '{enemy.name}' ({enemy.enemyType}) with {enemy.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} skinned renderers");
            yield return new WaitForSecondsRealtime(1.5f);

            // Animation check: two normals frames a moment apart. If skinned proxies animate, the enemy differs.
            int prevMode = _mode;
            _mode = Array.FindIndex(Modes, m => m.name == "normals");
            string tag = new string(_autoScene.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            for (int i = 0; i < 2; i++)
            {
                yield return null; yield return new WaitForEndOfFrame();
                SaveOutput(Path.Combine(_outDir, $"skin-{tag}-{i}.png"));
                yield return new WaitForSecondsRealtime(0.3f);
            }
            _mode = prevMode;
        }

        private IEnumerator Measure(string label, StringBuilder report, float seconds = 4f)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (_gpu != null) TakeGpuSummary(); // discard warm-up samples
            var dts = new List<float>(4096);
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                dts.Add(Time.unscaledDeltaTime * 1000f);
            }
            dts.Sort();
            float avg = dts.Average();
            var line = $"{label,-28} avg {avg:F3} ms ({1000f / avg:F0} fps)  p50 {dts[dts.Count / 2]:F3}  p99 {dts[(int)(dts.Count * 0.99)]:F3}  {TakeGpuSummary()}";
            report.AppendLine(line);
            Logger.LogInfo(line);
            if (_gpu != null)
            {
                var raw = string.Join(" ", Enumerable.Range(0, SlotCount).Select(s => _gpu.TryGetMs(s, out var v) ? $"{SlotNames[s]}={v:F4}" : $"{SlotNames[s]}=n/a"));
                Logger.LogInfo($"GPU timer status: ready={_gpu.IsReady} resolved={_gpu.ResolvedFrames} dropped={_gpu.DroppedFrames} raw[{raw}] '{_gpu.Status}'");
            }
        }

        private void SaveOutput(string path)
        {
            var src = _displayed != null ? _displayed : _output;
            if (src == null) { Logger.LogWarning("No RT output to save"); return; }
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }

        private static string SafeCurrentScene()
        {
            try { return SceneHelper.CurrentScene; } catch { return "?"; }
        }
    }

    internal sealed class CoroutineHost : MonoBehaviour
    {
        public Action OnUpdate;
        public Action OnGui;
        public Action OnFixed;
        private void Update() => OnUpdate?.Invoke();
        private void FixedUpdate() => OnFixed?.Invoke();
        private void OnGUI() => OnGui?.Invoke();
    }
}
