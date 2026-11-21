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
    ///   Keys, settings and flags: README.md "Controls" and docs/settings.md. Unattended runs: scripts/bench.ps1 (bench/README.md).
    /// </summary>
    [BepInPlugin("ooo.lew.ultrart", "UltraRT", PluginVersion.Number)] // Version.props (repo root)
    [BepInDependency(PluginConfiguratorGuid, BepInDependency.DependencyFlags.SoftDependency)] // in-game settings menu (SettingsMenu.cs)
    public partial class Plugin : BaseUnityPlugin
    {
        private enum Kind { Debug, PathTrace, Denoised, Reflection }

        // For Denoised, `mode` is a DenoiserDebugView; the trace itself runs PathTrace mode 0.
        private static readonly (string name, Kind kind, int mode)[] Modes =
        {
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

        /// <summary>Index of a view mode by name (case-insensitive), -1 if there is none. "hybrid" (removed on 2026-11-19)
        /// falls back to relight with a warning, so an old config, flag or bench suite keeps working.</summary>
        private int ModeIndex(string name)
        {
            if (string.Equals(name, "hybrid", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("View mode 'hybrid' was removed on 2026-11-19 (README.md, \"View modes\"); using relight");
                name = "relight";
            }
            return Array.FindIndex(Modes, m => m.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

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
        private readonly Vector4[] _lightDataGpu = new Vector4[MaxLights * 4];
        private readonly uint[] _lightMasks = new uint[MaxLights];
        private ComputeBuffer _lightMaskBuffer;
        /// <summary>Bench A/B: plugin._preLinearLights=0 uploads gamma-space light colours and lets every shader run pow(rgb, 2.2).</summary>
        private bool _preLinearLights = true;
        private static readonly int UltraRTLinearLightsId = Shader.PropertyToID("_UltraRTLinearLights");
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
        private bool _ptPrewarmed, _ptPrewarmWanted;
        private int _ptPrewarmFrame = -10;
        /// <summary>RayGenImg (1) / RayGenPTImg (2) dispatched once this session (Trace: their pipelines are warmed ahead of the
        /// first portal image); _imgPrewarmFirst until the session's first lit trace.</summary>
        private int _imgPrewarmed;
        private bool _imgPrewarmFirst = true;

        private string _autoScene;
        private bool _capture, _quit;
        private string _outDir;

        /// <summary>[View] ConfigVersion: which one-time default migrations this config file has had (here: the default
        /// mode; Plugin.Hdr.cs: the HDR defaults). Bound once in Awake.</summary>
        private BepInEx.Configuration.ConfigEntry<int> _configVersion;
        /// <summary>Launched by the bench (any -ultrart-bench* flag): migrations never run then, so a bench never changes
        /// the user's settings. Read once (the hotkey and hot-reload checks ask every frame).</summary>
        private static readonly bool BenchLaunch = Environment.GetCommandLineArgs().Any(x => x.StartsWith("-ultrart-bench", StringComparison.OrdinalIgnoreCase));

        /// <summary>A GeForce GTX (10 / 16 series and older): DXR runs, but there are no RT cores and no DLSS. The name has to
        /// say GTX and not RTX; an unknown NVIDIA name counts as RTX.</summary>
        internal static bool IsNonRtxNvidia(string deviceName) =>
            !string.IsNullOrEmpty(deviceName)
            && deviceName.IndexOf("GTX", StringComparison.OrdinalIgnoreCase) >= 0
            && deviceName.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) < 0;

        private void Awake()
        {
            // The default mode is pathtrace where DLSS Ray Reconstruction can run (an NVIDIA GPU and nvngx_dlssd.dll beside the
            // plugin; docs/dlss.md), relight elsewhere (pathtrace without RR is the slower fallback denoiser). Set before
            // InitSettings, which remembers the code defaults (the menu's "reset"). A GeForce GTX (no RT cores, no RR) gets
            // relight: pathtrace there would compile RayGenPT (up to ~30 s) at startup only to run without RR (since 2026-11-20).
            string pluginDir = Path.GetDirectoryName(Info.Location) ?? "";
            Logger.LogInfo($"UltraRT {PluginVersion.Number} in {pluginDir}");
            bool nvidia = SystemInfo.graphicsDeviceVendor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
            bool nonRtx = nvidia && IsNonRtxNvidia(SystemInfo.graphicsDeviceName);
            bool rrPresent = nvidia && !nonRtx
                && File.Exists(Path.Combine(pluginDir, "nvngx_dlssd.dll")) && File.Exists(Path.Combine(pluginDir, GpuTimer.DllName));
            if (nonRtx) Logger.LogInfo($"{SystemInfo.graphicsDeviceName}: no RTX, so no DLSS Ray Reconstruction; the default view mode is relight");
            _mode = Mathf.Max(0, Array.FindIndex(Modes, m => m.name == (rrPresent ? "pathtrace" : "relight")));
            InitSettings(); // SettingsMenu.cs: register the settings and remember the code defaults before any entry or flag changes them
            // Starting view mode: BepInEx/config/ooo.lew.ultrart.cfg ([View] DefaultMode), overridden by -ultrart-view.
            var defaultModeEntry = Config.Bind("View", "DefaultMode", rrPresent ? "pathtrace" : "relight",
                "View mode at startup: " + string.Join(", ", Modes.Select(m => m.name)) + ". Cycle in game with Page Up / Page Down.");
            // BepInEx writes a default into the file, so a config made before 2026-11-17 still says relight although nobody
            // chose it. Once per config: such an entry moves to the new default. Picking relight again afterwards sticks.
            _configVersion = Config.Bind("View", "ConfigVersion", 0, "Internal: which default migrations this file has had. Do not edit.");
            // Not in a bench launch: the user's own start of the game does it (a bench never changes their settings).
            if (_configVersion.Value < 1 && !BenchLaunch)
            {
                if (rrPresent && defaultModeEntry.Value.Equals("relight", StringComparison.OrdinalIgnoreCase))
                {
                    defaultModeEntry.Value = "pathtrace";
                    Logger.LogInfo("Config: [View] DefaultMode was the old default (relight): now pathtrace. Set it back in the settings menu to keep relight.");
                }
                _configVersion.Value = 1;
            }
            string defaultMode = defaultModeEntry.Value;
            int dm = ModeIndex(defaultMode);
            if (dm >= 0) _mode = dm;
            ParseArgs();
            ParseHotkeyArgs(); // -ultrart-debug-modes, -ultrart-dev-hotkeys (Plugin.Hotkeys.cs)
            ParseCacheArgs();
            ParseExposureArgs();
            ParseDlssArgs(); // DLSS Ray Reconstruction (Plugin.Dlss.cs)
            ParsePathTraceArgs(); // the pathtrace mode (Plugin.PathTrace.cs)
            // Pre-compile RayGenPT only when pathtrace is going to be used: the session starts in it, or a benchmark runs (its
            // scenarios switch to it). Otherwise every shader update would freeze relight players' menu for up to ~30 s.
            var cmdArgs = Environment.GetCommandLineArgs();
            _ptPrewarmWanted = Modes[_mode].name == "pathtrace" || cmdArgs.Any(a => a == "-ultrart-bench" || a == "-ultrart-bench-server");
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
            bool minimal = Environment.GetCommandLineArgs().Any(a => a.Equals("-ultrart-minimal", StringComparison.OrdinalIgnoreCase));
            if (!SystemInfo.supportsRayTracing || minimal)
            {
                if (minimal) Logger.LogWarning("-ultrart-minimal: UltraRT is idle.");
                else StayIdle(bundleMissing: false);
                // Benchmarks can still run game-only ("off" modes) for comparisons, e.g. against DX11.
                if (_host == null) IdleHost();
                _noScene = true;
                SceneManager.sceneUnloaded += s => Logger.LogInfo($"sceneUnloaded '{s.name}'");
                SceneManager.activeSceneChanged += (_, to) => Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                StartBenchIfRequested();
                return;
            }

            _bundlePath = Path.Combine(pluginDir, "ultrart.shaders");
            SceneProxies.MaterialOverrideFolders = new[] { pluginDir, Paths.ConfigPath };
            if (!LoadShaders()) { StayIdle(bundleMissing: true); return; }

            if (_useGpuTimer)
            {
                // Opt-in dev tool: the native DLL registers itself with Unity through an internal routine.
                var nativePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", GpuTimer.DllName);
                if (GpuTimer.TryCreate(nativePath, out _gpu, out var gpuError))
                {
                    Logger.LogInfo($"GPU timer: {_gpu.Status}");
                    InstallFrameSlot();
                }
                else Logger.LogWarning($"GPU timer unavailable: {gpuError}");
            }
            {
                // Native D3D12 cs_6_5 inline RayQuery bridge (Volumetrics + ParticleLight): bypasses Unity's DispatchRays SBT rebuild.
                var nativePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", GpuTimer.DllName);
                if (RayQueryBridge.TryCreate(nativePath, out _rayQuery, out var rqError))
                {
                    Logger.LogInfo($"RayQuery bridge: {_rayQuery.Status}");
                    RayQueryBridge.RestoreTargets = RestoreCameraTargets; // the camera's targets back after each dispatch event
                }
                else
                    Logger.LogInfo($"RayQuery bridge unavailable (using .raytrace fallback): {rqError}");
            }

            if (!_noHook)
            {
                Patches.Apply(Info.Metadata.GUID);
                Logger.LogInfo(BloodWet.Hooked ? "Wet blood: stain hook installed (BloodsplatterManager.CreateBloodstain)" : $"Wet blood: stain hook missing ({BloodWet.HookError}): stains stay dry");
                Logger.LogInfo(Patches.GenericInstantiateHooked ? "Instantiate hooks installed (incl. generic Instantiate<T>)"
                    : $"Instantiate hooks installed; generic Instantiate<T> not hooked ({Patches.GenericInstantiateError}), the periodic rescan covers it");
                Logger.LogInfo(Patches.PortalClonesHooked ? "Portal clone hook installed (PortalAwareRenderer.CreateClone)" : $"Portal clones not hooked: {Patches.PortalClonesError}");
                PortalRasterSkip.Patch(new HarmonyLib.Harmony(Info.Metadata.GUID + ".portalskip")); // docs/portals.md, "Raster skip"
                Logger.LogInfo(PortalRasterSkip.Hooked ? "Portal raster skip hook installed (PortalRenderV2.Render)" : $"Portal raster skip not hooked: {PortalRasterSkip.Error}");
                // Native float HDR (Plugin.Hdr.cs): only when the HDR swapchain hooks are in, so an SDR game is never patched.
                if (_hdrInstalled) HdrGameTarget.Patch(new HarmonyLib.Harmony(Info.Metadata.GUID + ".hdr"), Logger);
            }
            _cmd = new CommandBuffer { name = "UltraRT" };
            _lightBuffer = new ComputeBuffer(MaxLights * 4, 16);
            _lightMaskBuffer = new ComputeBuffer(MaxLights, 4);
            _lightSampling = new LightSampling(MaxLights) { Settings = _lightSamplingSettings };
            _lightSampling.SetGridShader(_lightGridCs);
            _emissive = new EmissiveLights(Logger) { Settings = _emissiveSettings, LightScaleOf = EmitterLightScale, PowerOf = HandoverPower, BakedPowerOf = HandoverBakedPower, IsPowerPart = HandoverIsPart, IsAtlasPart = m => _proxies != null && _proxies.AtlasPartName(m) != null };
            var go = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
            _host.OnUpdate = OnUpdate;
            _host.OnGui = DrawHud;
            AttachSettings(); // config entries -> fields (flags win), then the PluginConfigurator page
            ParsePresetArgs(); // -ultrart-preset ultra|high|performance|competitive (Plugin.Presets.cs)

            SceneManager.sceneUnloaded += s => Logger.LogInfo($"sceneUnloaded '{s.name}'");
            SceneManager.activeSceneChanged += (_, to) =>
            {
                Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                CineOnSceneChanged(); // the cinematic camera puts back what it changed (Plugin.Cinematics.cs)
                ResetScene();
                _host.StartCoroutine(BuildWhenLoaded());
            };

            if (!_noHook) Camera.onPreRender += OnCameraPreRender;
            if (!StartBenchIfRequested() && !string.IsNullOrEmpty(_autoScene)) _host.StartCoroutine(AutoRun());
        }

        /// <summary>The host object without the RT update loop (UltraRT idle).</summary>
        private GameObject IdleHost()
        {
            var go = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
            return go;
        }

        /// <summary>UltraRT can't run (no Direct3D 12, no DXR, no shader bundle): say why in the log and, once per launch,
        /// on screen (StartupNotice.cs; not in bench launches).</summary>
        private void StayIdle(bool bundleMissing)
        {
            var (title, body) = StartupNotice.Reason(bundleMissing, _bundlePath);
            if (title == null) { title = "UltraRT is off"; body = ""; }
            Logger.LogWarning($"{title}. UltraRT is idle. {body.Replace("\n\n", " ").Replace('\n', ' ')}");
            if (_host == null) IdleHost();
            // -ultrart-startup-notice: show it in a bench launch too (testing the notice itself, docs/release.md).
            if (!BenchLaunch || Environment.GetCommandLineArgs().Any(a => a.Equals("-ultrart-startup-notice", StringComparison.OrdinalIgnoreCase)))
                StartupNotice.Show(_host.gameObject, title, body);
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
                    case "-ultrart-view": { var v = Next(); if (!int.TryParse(v, out _mode)) { int vi = ModeIndex(v); _mode = vi >= 0 ? vi : _mode; } break; } // index or name
                    case "-ultrart-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _scale); break;
                    case "-ultrart-accumulate": _accumulate = true; break;
                    case "-ultrart-capture": _capture = true; break;
                    case "-ultrart-quit": _quit = true; break;
                    case "-ultrart-off": _viewEnabled = false; break;
                    case "-ultrart-skinned-visible": SceneProxies.SkinnedProxiesVisible = true; break;
                    case "-ultrart-skinned-noupdate": SceneProxies.SkinnedTransformUpdates = false; break;
                    case "-ultrart-nodisplace": SceneProxies.DisplacementBuild = false; break; // VERTEX_DISPLACEMENT geometry keeps its rest pose
                    case "-ultrart-nostaticlift": SceneProxies.LiftStaticDisplaced = false; break; // static-batched displaced water keeps its rest pose (StaticGroups.Lifted.cs)
                    case "-ultrart-nostaticlift-opaque": SceneProxies.LiftStaticOpaque = false; break; // static-batched displaced flesh keeps its rest pose (StaticGroups.Lifted.cs)
                    case "-ultrart-displace-range": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _displaceRange); break; // m; < 0: original meshes (displacement off at run time)
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
                    case "-ultrart-aux-every": if (int.TryParse(Next(), out int every)) _auxEvery = Mathf.Clamp(every, 1, 4); break;
                    case "-ultrart-nofixturelift": _fixtureLift = false; break;
                    case "-ultrart-noorbemitters": SceneProxies.OrbEmitters = false; break;
                    case "-ultrart-beside": _besideV1 = true; break;
                    case "-ultrart-noorbholders": SceneProxies.OrbHolders = false; break;
                    case "-ultrart-noorbshells": SceneProxies.OrbShells = false; break;
                    case "-ultrart-nolightoverrides": _lightOverrides = false; break; // per-level light overrides (docs/lighting-overrides.md)
                    case "-ultrart-classify-lights": _classifyLights = true; break;  // the light classifier, once per level (Plugin.LightClassifier.cs)
                    case "-ultrart-lights-preview": _lightsPreview = true; break;    // overrides from the classifier's output folder
                    case "-ultrart-noenemyglow": _enemyGlowSettings.enabled = false; break; // Plugin.EnemyGlow.cs
                    case "-ultrart-noveils": SceneProxies.Veils.enabled = false; break; // Plugin.Veils.cs
                    case "-ultrart-enemy-survey": _enemySurvey = true; break;        // bench: enemies_<scene>.txt per level (Plugin.EnemySurvey.cs)
                    case "-ultrart-light-census": _lightCensus = true; break;        // bench: lights_<scene>.json per level (Plugin.LightClasses.cs)
                    case "-ultrart-logfixtures": _logFixtures = true; break;
                    case "-ultrart-noeyeclip": _playerLightEyeClip = false; break; // player spot lights: shadow rays to the light itself
                    case "-ultrart-log-light-toggles": _logLightToggles = true; break; // diagnostics: lights switching on/off, per-frame cine exposure
                    case "-ultrart-nodepthmatch": _noDepthMatch = true; break;
                    case "-ultrart-noportals": _portals = false; break;
                    case "-ultrart-noportallayer": SceneProxies.TracePortalLayer = false; break;
                    case "-ultrart-portal-hops": int.TryParse(Next(), out _portalHops); break;
                    case "-ultrart-rtmirrors": _rtMirrors = true; break;
                    case "-ultrart-portal-raster-skip": { var v = (Next() ?? "").ToLowerInvariant(); _portalRasterSkip = v != "off" && v != "0" && v != "false"; break; } // PortalRasterSkip.cs
                    case "-ultrart-portal-lights": { var v = (Next() ?? "").ToLowerInvariant(); _portalLights = v != "off" && v != "0" && v != "false"; break; } // Plugin.PortalLights.cs
                    case "-ultrart-noplayer": SceneProxies.TracePlayerModel = false; break;
                    case "-ultrart-v1-unlit": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SceneProxies.PlayerModelUnlit); break;
                    case "-ultrart-player-rays": int.TryParse(Next(), out _playerRays); break;
                    case "-ultrart-v1-glow": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _v1Glow); break; // V1's wings in reflections (SceneProxies.Player.cs)
                    case "-ultrart-v1-noheld": _v1HeldItem = false; break; // the held item in V1's third-person hand (SceneProxies.HeldItem.cs)
                    case "-ultrart-v1-held-size": if (float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hsz)) _v1HeldSize = Mathf.Max(0f, hsz); break;
                    case "-ultrart-v1-eye-glow": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _v1EyeGlow); break; // V1's camera lens in reflections
                    case "-ultrart-v1-weapon-glow": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _v1WeaponGlow); break; // the held gun's glow in reflections
                    case "-ultrart-handover-tint": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _handoverTint); break; // fullbright handover parts take their lights' colour (Plugin.Handover.cs)
                    case "-ultrart-norain": _rainOn = false; break;
                    case "-ultrart-notexscroll": _texScroll = false; break;
                    case "-ultrart-notexscroll-motion": _texScrollMotion = false; break;
                    case "-ultrart-player-noanimate": SceneProxies.PlayerAlwaysAnimate = false; break;
                    case "-ultrart-lights":
                    {
                        string v = (Next() ?? "").ToLowerInvariant();
                        switch (v)
                        {
                            case "ref": _lightSamplingSettings.mode = LightSamplingMode.Reference; break;
                            case "ris": _lightSamplingSettings.mode = LightSamplingMode.Ris; break;
                            case "restir": Logger.LogWarning("-ultrart-lights restir: ReSTIR was removed on 2026-11-19 (docs/light-sampling.md); using ris"); _lightSamplingSettings.mode = LightSamplingMode.Ris; break;
                            default: Logger.LogWarning($"-ultrart-lights '{v}': expected ref | ris; keeping {_lightSamplingSettings.mode}"); break;
                        }
                        break;
                    }
                    case "-ultrart-ris-m": int.TryParse(Next(), out _lightSamplingSettings.candidates); break;
                    case "-ultrart-noemissive": _emissiveSettings.enabled = false; break;
                    case "-ultrart-emissive-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.lightScale); break;
                    case "-ultrart-static-always": SceneProxies.StaticActivation = false; break;
                    case "-ultrart-static-level": { if (int.TryParse(Next(), out var lv)) SceneProxies.ForceGroupLevel = lv; break; }
                    case "-ultrart-static-budget": { if (int.TryParse(Next(), out var bg)) SceneProxies.StaticRecordBudget = bg; break; }
                    case "-ultrart-nohook": _noHook = true; break;
                    case "-ultrart-pose": _capturePose = Next(); break;
                    case "-ultrart-inject": if (Enum.TryParse(Next(), true, out CameraEvent ev)) { InjectEvent = ev; _injectForced = true; } break;
                    case "-ultrart-gputimer-flags": Environment.SetEnvironmentVariable("ULTRART_GPUTIMER_FLAGS", Next()); break;
                    case "-ultrart-ambient": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _primaryAmbient); break;
                    case "-ultrart-noreflections": _reflSettings.enabled = false; break;
                    case "-ultrart-refl-strength": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _reflSettings.reflStrength); break;
                    case "-ultrart-spec-weight": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _reflSettings.specWeight); break;
                    case "-ultrart-refl-cache": { var v = Next(); _reflSettings.cacheShading = v == "off" ? 0 : v == "always" ? 2 : 1; break; }
                    case "-ultrart-matdump": SceneProxies.DumpMaterialClasses = true; break;
                    case "-ultrart-viewprobe": SceneProxies.ViewProbe = true; break;
                    case "-ultrart-skin-probe": SceneProxies.SkinProbeFilter = Next(); break; // bench screenshots: game vs proxy skinned vertices (SceneProxies.Probe.cs)
                    case "-ultrart-nosettle-readd": SceneProxies.SkinnedSettleReAdd = false; break; // skinned instances are not re-added when a pose change settles (docs/scene-proxies.md, "Posed mannequins")
                    case "-ultrart-glow-smooth": _smoothGlow = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase); break;
                    case "-ultrart-nomatsync": SceneProxies.MaterialSync = false; break;
                    case "-ultrart-albedo-emission-glow": SceneProxies.LitAlbedoEmission = false; break;
                    case "-ultrart-baked-remap": SceneProxies.BakedOwnLayers = false; break; // trace baked layers 6/7 as 24/8 (before 2026-11-21; SceneProxies.OriginalLayer)
                    case "-ultrart-novertexblend": SceneProxies.VertexBlendTextures = false; break; // Master VERTEX_BLENDING: _MainTex only (SceneProxies.cs)
                    case "-ultrart-nomotion": MotionVectors.Enabled = false; break;
                    case "-ultrart-guard":
                        float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _guardX);
                        _guardY = _guardX;
                        if (i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gy)) { _guardY = gy; i++; }
                        break;
                    case "-ultrart-motion-probe": SceneProxies.MotionProbe = true; break;
                    case "-ultrart-motion-probe-filter": SceneProxies.MotionProbe = true; SceneProxies.MotionProbeFilter = Next(); break;
                    case "-ultrart-nomirrorfix": SceneProxies.MirroredSkinFix = false; break; // mirrored skinned meshes keep the default winding (docs/scene-proxies.md)
                    case "-ultrart-noreadd": SceneProxies.NewInstanceReAdd = false; break; // new rigid instances are added once only (docs/scene-proxies.md, "Instances added too early")
                    case "-ultrart-trace-proxies": SceneProxies.TraceFilter = Next(); break;
                    case "-ultrart-game-dither": { var v = (Next() ?? "").ToLowerInvariant(); _gameDither = v != "off" && v != "0" && v != "false"; break; } // Plugin.GameDither.cs
                    case "-ultrart-enemy-outlines": { var v = (Next() ?? "").ToLowerInvariant(); _enemyOutlines = _enemyOutlinesDefault = v != "off" && v != "0" && v != "false"; break; } // Plugin.EnemyOutlines.cs
                    case "-ultrart-output-dither": { var v = (Next() ?? "").ToLowerInvariant(); _outputDither = v != "off" && v != "0" && v != "false"; break; } // Plugin.GameDither.cs
                    case "-ultrart-orb-light": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _orbLightScale); break;
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
            _hdrSceneCs = denoise; // native float HDR's finished frame (Plugin.Hdr.cs)
            _hdrSceneKernel = denoise.HasKernel("HdrScene") ? denoise.FindKernel("HdrScene") : -1;
            var upsample = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Upsample");
            var upSettings = _upsampler?.Settings;
            _upsampler?.Dispose();
            _upsampler = upsample != null ? new TemporalUpsampler(upsample) : null;
            if (_upsampler == null) Logger.LogWarning("Upsample compute shader missing from bundle: replace modes use the 2x2 depth-guided composite");
            else if (upSettings != null) _upsampler.Settings = upSettings;
            _motionCopy = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "MotionCopy");
            if (_motionCopy == null) Logger.LogWarning("MotionCopy compute shader missing from bundle: skinned meshes get root motion only");
            _displaceCs = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Displace");
            if (_displaceCs == null) Logger.LogWarning("Displace compute shader missing from bundle: VERTEX_DISPLACEMENT geometry traces its rest pose");
            _proxies?.SetDisplaceShader(_displaceCs);
            _lightGridCs = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "LightGrid");
            if (_lightGridCs == null) Logger.LogWarning("LightGrid compute shader missing from bundle: RIS uses global alias table only");
            _lightSampling?.SetGridShader(_lightGridCs);
            if (settings != null) _denoiser.Settings = settings;
            _reflFilter?.Dispose();
            _reflFilter = new ReflectionFilter(reflections);
            LoadCacheShaders(bundle);
            LoadExposureShader(bundle, denoise);
            LoadTransparentShaders(bundle);
            LoadLavaShader(bundle);
            LoadVolumetricsShaders(bundle);
            LoadVeilShaders(bundle);
            LoadCutoutShader(bundle);
            LoadViewmodelShaders(bundle);
            LoadDlssShader(bundle);
            LoadEffectShaders(shaders);
            LoadVfxShaders(bundle);
            LoadWetShaders(bundle);
            LoadStainShader(bundle);
            LoadStainedGlassShader(bundle); // stained-glass projection (Plugin.StainedGlass.cs)

            _bundle = bundle;
            _debugShader = debug;
            _ptShader = pt;
            _ptCountShader = rts.FirstOrDefault(s => s.name == "PathTraceCount"); // optional: ray counters (Plugin.RayCounts.cs)
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
            _debugShader = _ptShader = _ptCountShader = null;
            _proxyShader = null;
            if (LoadShaders()) _host.StartCoroutine(BuildWhenLoaded());
        }

        private void OnUpdate()
        {
            UpdateHotkeyGuard(); // _keysLive: no hotkeys while the console or a text field has the keyboard (Plugin.Hotkeys.cs)
            UpdateRecorder();
            CineUpdate(); // cinematic camera: Home, then its keys (Plugin.Cinematics.cs)
            UpdateEffects();
            UpdateShaftFakes(); // lamp cones: the game's fake cone meshes hide while ours draw (Plugin.Shafts.cs)
            UpdateParticles(); // lit particles (Plugin.Particles.cs)
            UpdateConeGain(); // the game's light cones while traced (Plugin.Beams.cs)
            MaterialsUpdate(); // Ctrl+F1, _UltraRTDetail global
            HdrTick(); // HDR display output: settings and log (Plugin.Hdr.cs)
            MsaaProbeUpdate(); // measurement probe, bench plugin._msaaProbe only (Plugin.MsaaProbe.cs)
            try
            {
                if (_keysLive) PlayerAndDevHotkeys();
                LightEditorUpdate(); // Insert + numpad (Plugin.LightOverrides.cs)
                LightClassifierUpdate(); // -ultrart-classify-lights, Ctrl+Insert (Plugin.LightClassifier.cs)
                DlssUpdate(); // Ctrl+F8, native RR log (Plugin.Dlss.cs)
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
            _menu?.Update(); // the settings menu follows what the hotkeys changed

            // Shader hot reload: a 0.5 s poll of the bundle's write time. Bench launches (bench.ps1 -KeepOpen iterations) and
            // [View] DevHotkeys only; players skip the file system call (F9 and the menu's Reload shaders still work).
            if (DevHotkeysActive && Time.realtimeSinceStartup >= _nextBundleCheck)
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

        /// <summary>OnUpdate's hotkeys, while _keysLive. Developer / bench keys go through DevKey (Plugin.Hotkeys.cs).</summary>
        private void PlayerAndDevHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F7) && !CtrlHeld() && !ShiftHeld()) { _viewEnabled = !_viewEnabled; _denoiser?.Reset(); _lightSampling?.Reset(); _reflFilter?.Reset(); Logger.LogInfo($"View {(_viewEnabled ? "on" : "off")}"); }
            ExposureHotkeys(); // Ctrl+F4
            if (Input.GetKeyDown(KeyCode.F4) && !CtrlHeld()) { _reflSettings.enabled = !_reflSettings.enabled; _reflFilter?.Reset(); Logger.LogInfo($"Reflections: {(_reflSettings.enabled ? "on" : "off")}"); Toast($"Reflections {(_reflSettings.enabled ? "on" : "off")} (F4)"); }
            SkyHotkeys(); // Ctrl+F5
            if (Input.GetKeyDown(KeyCode.F5) && !CtrlHeld()) { _emissiveSettings.enabled = !_emissiveSettings.enabled; _lightSampling?.Reset(); _denoiser?.Reset(); Logger.LogInfo($"Emissive lights: {(_emissiveSettings.enabled ? "on" : "off")}"); }
            VolumetricsHotkeys(); // Ctrl+F6
            ViewmodelHotkeys(); // Ctrl+F7
            VfxHotkeys(); // Ctrl+F2, Shift+F2
            if (DevKey(Input.GetKeyDown(KeyCode.F6) && !CtrlHeld(), "F6")) { _lightSamplingSettings.mode = (LightSamplingMode)(((int)_lightSamplingSettings.mode + 1) % 2); _lightSampling?.Reset(); Logger.LogInfo($"Light sampling: {_lightSamplingSettings.mode}"); }
            // Page Up / Page Down cycle view modes (F8 opens ULTRAKILL's console); the debug views only with DebugModes.
            if (Input.GetKeyDown(KeyCode.PageDown)) CycleMode(+1);
            if (Input.GetKeyDown(KeyCode.PageUp)) CycleMode(-1);
            if (DevKey(Input.GetKeyDown(KeyCode.F9), "F9")) ReloadShaders(); // the menu's "Reload shaders" button works regardless
            if (DevKey(Input.GetKeyDown(KeyCode.F2) && !CtrlHeld() && !ShiftHeld(), "F2") && _upsampler != null)
            {
                _upsampler.Settings.enabled = !_upsampler.Settings.enabled;
                string on = _upsampler.Settings.enabled ? "on" : "off (2x2 depth-guided)";
                Logger.LogInfo($"Temporal upsampling: {on}");
                Toast($"Temporal upsampling {on} (F2)");
            }
            CacheHotkeys(); // F3, Shift+F3, Ctrl+F3 (developer keys)
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
            PortalRasterSkip.Active = false; // the game's portal views render again until the next traced frame
            _glSlowLogs = 0; _longFrameLogs = 0;
            PortalRasterSkip.Proxies = null;
            Patches.CollectSpawns = false;
            Patches.PendingSpawns.Clear();
            _spawnCursor = 0;
            _spawnSeen.Clear();
            _accumFrames = 0;
            _denoiser?.Reset();
           
            _lightSampling?.Reset();
            _reflFilter?.Reset();
            _emissive?.Clear();
            _radianceCache?.Reset();
            _autoExposure?.Reset();
            _volumetrics?.Reset();
            _lightShafts?.Clear();
            _vfx?.Reset();
            ViewmodelReset();
            LightOverridesReset(); // Plugin.LightOverrides.cs
            SgClear(); // stained-glass windows (Plugin.StainedGlass.cs)
            _sgBuiltFor = null;
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
                SceneProxies.MergeStaticDynamic = _mergeStaticDynamic;
                SceneProxies.WarmHitGroups = _warmHitGroups;
                SceneProxies.WarmHitShaders.Clear();
                SceneProxies.WarmHitShaders.Add(_proxyShader);
                SceneProxies.WarmHitShaders.Add(_effectProxyShader);
                SceneProxies.WarmHitShaders.Add(SceneProxies.LavaShader);
                SceneProxies.WarmHitShaders.Add(SceneProxies.PortalShader);
                _proxies = new SceneProxies(Logger, _proxyShader, cam != null ? cam.cullingMask : ~0, _motionCopy) { EmitterGlowOf = EmitterGlow, AtlasPartWanted = AtlasPartWanted, CubeToLatLong = CubeLatLong, EmitterGlowAt = EmitterGlowAt };
                _proxies.SetDisplaceShader(_displaceCs);
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

        // GatherLights' steps, for its slow-frame log (docs/performance.md, "Combat stalls"): ticks per step this frame.
        private readonly long[] _glTicks = new long[12];
        private static readonly string[] GlStepNames = { "scan", "fixtures", "sun", "pack", "lava", "added", "effect", "enemyGlow", "portalImages", "medium+upload", "(of effect: spawns", "append)" };
        private int _glSlowLogs;
        private int _glPacked;
        private void GlMark(int step, ref long t) { long n = Perf.Now; _glTicks[step] += n - t; t = n; }

        private void GatherLights()
        {
            System.Array.Clear(_glTicks, 0, _glTicks.Length);
            long gt = Perf.Now, g0 = gt;
            GatherLightsBody(ref gt);
            double ms = (Perf.Now - g0) * Perf.TickMs;
            if (ms > 4.0 && _glSlowLogs < 40)
            {
                _glSlowLogs++;
                var sb = new System.Text.StringBuilder();
                sb.Append($"Lights section slow: {ms:F2} ms at frame {Time.frameCount} ({_activeLights.Count} active of {_proxies.Lights.Count} tracked, {_glPacked} scene lights packed, {_lightCount} in all, enemy glow {EnemyGlowLights}):");
                for (int i = 0; i < _glTicks.Length; i++) sb.Append($" {GlStepNames[i]} {_glTicks[i] * Perf.TickMs:F2}");
                Logger.LogInfo(sb.ToString());
            }
        }

        private void GatherLightsBody(ref long gt)
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
                if (_logLightToggles) LogLightToggles();
            }
            GlMark(0, ref gt);
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
            for (int i = 0; i < _lightMedium.Length; i++) _lightMedium[i] = 1f;
            BeginLightOverrides(); // per-level light overrides (Plugin.LightOverrides.cs; relight look only)
            _proxies.UpdateOrbHolders(); // statue orbs: their lights pass through the statue holding them (SceneProxies.Orbs.cs)
            GlMark(1, ref gt);
            // The main sun: the brightest directional light from above the horizon (LowSideLight below).
            Light mainSun = null;
            float mainSunLum = -1f;
            foreach (var l in _activeLights)
            {
                if (l == null || l.type != LightType.Directional || !l.isActiveAndEnabled || l.intensity <= 0f || -l.transform.forward.y < -0.05f) continue;
                float lum = (0.2126f * l.color.r + 0.7152f * l.color.g + 0.0722f * l.color.b) * l.intensity;
                if (lum > mainSunLum) { mainSunLum = lum; mainSun = l; }
            }
            _mediumOnly.Clear();
            GlMark(2, ref gt);
            foreach (var l in _activeLights)
            {
                if (_lightCount >= MaxLights) break;
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                var rl = ResolveLight(l); // class, policy and the light's own entry (Plugin.LightClasses.cs)
                if ((rl != null && (rl.Disabled || rl.Intensity * rl.Toggle <= 0f)) || SoloHides(l) || ReferenceEquals(l, _fitSkip)) continue;
                // A light that lights the air only goes after every light surfaces see (_surfLightCount).
                if (rl != null && !rl.Surfaces) { if (rl.Medium > 0f) _mediumOnly.Add(l); continue; }
                if (_effectLights != null && _effectLights.OwnsLight(l)) continue; // a gunshot light: its own envelope (EffectLights)
                PackSceneLight(l, rl, mainSun);
            }
            _glPacked = _lightCount;
            GlMark(3, ref gt);
            DimLavaLights(); // Plugin.Lava.cs: point lights faking the lava glow
            GlMark(4, ref gt);
            AppendAddedLights(); // Plugin.LightOverrides.cs: area lights and sky portals the overrides add
            GlMark(5, ref gt);
            AppendEffectLights(); // Plugin.Effects.cs: spawned effect lights and virtual lights for glowing effects
            GlMark(6, ref gt);
            AppendEnemyGlowLights(); // Plugin.EnemyGlow.cs: the glowing parts of skinned enemies
            GlMark(7, ref gt);
            AppendPortalLightImages(); // Plugin.PortalLights.cs: lights imaged through portals (they light the room behind the exit)
            GlMark(8, ref gt);
            // Lights [0, _surfLightCount) light surfaces (every surface pass binds that count); the ones after it only
            // the air (the volumetric pass takes _lightCount). docs/lighting-overrides.md, "Classes and rules".
            _surfLightCount = _lightCount;
            foreach (var l in _mediumOnly)
            {
                if (_lightCount >= MaxLights) break;
                PackSceneLight(l, ResolveLight(l), mainSun);
            }
            FlagPlayerLights();   // Plugin.PlayerLights.cs: V1 never shadows the player's own lights (flashlight, held torch)
            int uploadLights = Math.Max(1, _lightCount);
            if (_preLinearLights && LinearActive)
            {
                Array.Copy(_lightData, _lightDataGpu, uploadLights * 4);
                for (int k = 0; k < _lightCount; k++)
                {
                    var c = _lightDataGpu[k * 4 + 1];
                    _lightDataGpu[k * 4 + 1] = new Vector4(
                        c.x > 0f ? Mathf.Pow(c.x, 2.2f) : 0f,
                        c.y > 0f ? Mathf.Pow(c.y, 2.2f) : 0f,
                        c.z > 0f ? Mathf.Pow(c.z, 2.2f) : 0f,
                        c.w);
                }
                _lightBuffer.SetData(_lightDataGpu, 0, 0, uploadLights * 4);
                Shader.SetGlobalFloat(UltraRTLinearLightsId, 1f);
            }
            else
            {
                _lightBuffer.SetData(_lightData, 0, 0, uploadLights * 4);
                Shader.SetGlobalFloat(UltraRTLinearLightsId, 0f);
            }
            BakedLayerBits(uploadLights); // masks for the baked layers 6/7 (Plugin.Noise.cs)
            _lightMaskBuffer.SetData(_lightMasks, 0, 0, uploadLights);
            GlMark(9, ref gt);
        }

        private int _surfLightCount;
        private readonly List<Light> _mediumOnly = new List<Light>();
        /// <summary>Per packed light: what it scatters in the air, as a factor (LightPolicy.Medium).</summary>
        private readonly float[] _lightMedium = new float[MaxLights];

        /// <summary>Packs one scene light at _lightCount (layout: PathTrace.raytrace, _Lights).</summary>
        private void PackSceneLight(Light l, LightResolved rl, Light mainSun)
        {
            var ov = rl?.Entry; // an entry that places the light (offset or area shape)
            var t = l.transform;
            var fwd = t.forward;
            var c = l.color * l.intensity;
            var pos = t.position;
            float shadowRadius = 0f; // 0: the global _LightRadius
            if (ov != null) pos += LightOverrides.Vec(ov.Offset, Vector3.zero); // the entry places it: no lift
            else if (_fixtureLifts.TryGetValue(l, out var lift))
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
                    // with no ray, outside RIS's competition (Plugin.Noise.cs, EyeLight).
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
                    return;
            }
            int i = _lightCount * 4;
            _lightData[i] = l0;
            _lightData[i + 1] = new Vector4(c.r, c.g, c.b, 0);
            _lightMasks[_lightCount] = unchecked((uint)l.cullingMask) | BakedLightMask(l); // Plugin.Noise.cs
            _packedLights[_lightCount] = l;
            _lightData[i + 2] = new Vector4(fwd.x, fwd.y, fwd.z, shadowRadius);
            _lightData[i + 3] = l3;
            // An orb's light: shadowed, but its rays skip fixtures (the statue holding it; SceneProxies.Orbs.cs).
            if (l3.z == 0f && SceneProxies.OrbHolders && _proxies.OrbLights.Contains(l)) _lightData[i + 3].z = OrbLightShadowFlag;
            if (rl != null && (rl.Changes || rl.Toggle != 1f)) ApplyResolved(rl, l, _lightCount);
            // The statue orbs' lights, a touch weaker than the game's (user, 2026-11-15): the orb sits right at the stone
            // and lights it physically, brighter than the game's thin vertex lighting (docs/scene-proxies.md, "Statue orbs").
            if (_orbLightScale != 1f && SceneProxies.OrbHolders && _proxies.OrbFixtureLights.Contains(l))
            {
                var oc = _lightData[i + 1];
                _lightData[i + 1] = new Vector4(oc.x * _orbLightScale, oc.y * _orbLightScale, oc.z * _orbLightScale, oc.w);
            }
            _lightPlaced[_lightCount] = ov != null;
            _lightCount++;
        }

        // Lights inside open fixtures (torch bowls, braziers) are moved to just above the rim and get a soft-shadow radius
        // the size of the fixture, like a fire filling the bowl: the bowl casts a soft shadow under itself instead of
        // blacking out everything below its rim. Lights with something solid right above their fixture (ceiling lamps)
        // stay put. y: lift (m), w: soft-shadow radius (m). docs/relight.md, "Light fixtures".
        private readonly Dictionary<Light, Vector4> _fixtureLifts = new Dictionary<Light, Vector4>();
        private SceneProxies _fixtureLiftSource;
        private int _fixtureLiftVersion = -1;
        private bool _fixtureLift = true; // -ultrart-nofixturelift
        private const float OrbLightShadowFlag = 0.25f; // l3.z: shadowed, rays skip fixtures (UltraRTLights.hlsl LightShadowSetup)
        /// <summary>Statue orb lights' intensity factor (-ultrart-orb-light, bench plugin._orbLightScale).</summary>
        private float _orbLightScale = 0.5f;
        /// <summary>White statue orbs' glow shell strength while traced (SceneProxies.OrbShells.cs; bench plugin._orbShellGain).</summary>
        private float _orbShellGain = SceneProxies.OrbShellGain;
        private int _rtTracedFrame = -100;
        /// <summary>Limbo water is not faded around the player while traced (bench plugin._flatWaterFade=0: the game's fade
        /// with its dither squares).</summary>
        private bool _flatWaterFade = true;
        /// <summary>Diagnostic (bench plugin._readdDynamic=1): remove and re-add every active dynamic instance once.</summary>
        private bool _readdDynamic;
        private readonly bool[] _lightPlaced = new bool[MaxLights]; // packed light i is placed by a light override (offset / shape)
        private bool _logFixtures;        // -ultrart-logfixtures
        private bool _logLightToggles;    // -ultrart-log-light-toggles
        private readonly HashSet<Light> _loggedOn = new HashSet<Light>();
        private readonly List<Light> _loggedOff = new List<Light>();

        /// <summary>Diagnostics (-ultrart-log-light-toggles): which lights switched on or off since the last rebuild of
        /// _activeLights (4 Hz), with game time and the distance to the camera.</summary>
        private void LogLightToggles()
        {
            var cam = _camera != null ? _camera.transform.position : Vector3.zero;
            _loggedOff.Clear();
            foreach (var l in _loggedOn) if (l == null || !_activeLights.Contains(l)) _loggedOff.Add(l);
            foreach (var l in _loggedOff)
            {
                _loggedOn.Remove(l);
                Logger.LogInfo(l == null ? $"light off (destroyed) at t {Time.time:F2}" : FormattableString.Invariant(
                    $"light off at t {Time.time:F2}: '{Bench.GameControl.PathOf(l.transform)}' {l.type} I {l.intensity:F2} range {l.range:F0} dist {(l.transform.position - cam).magnitude:F1}"));
            }
            foreach (var l in _activeLights)
                if (_loggedOn.Add(l))
                    Logger.LogInfo(FormattableString.Invariant(
                        $"light on at t {Time.time:F2}: '{Bench.GameControl.PathOf(l.transform)}' {l.type} col {l.color} I {l.intensity:F2} range {l.range:F0} dist {(l.transform.position - cam).magnitude:F1} mask 0x{l.cullingMask:X8}"));
        }
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
        private static readonly int UltraRTSurfaceId = Shader.PropertyToID("_UltraRTSurface");
        private static readonly int UltraRTSurfaceOnId = Shader.PropertyToID("_UltraRTSurfaceOn");
        /// <summary>The game's fog on traced pixels leaves "NoFog" (_FOG_OFF) materials alone, as the game does (bench
        /// plugin._noFogSkip=0: fogged like everything else, before 2026-11-18).</summary>
        private bool _noFogSkip = true;

        private static readonly int UltraRTAlbedoId = Shader.PropertyToID("_UltraRTAlbedo");
        private static readonly int UltraRTSpecularId = Shader.PropertyToID("_UltraRTSpecular");
        private LightSampling _lightSampling;
        private EmissiveLights _emissive;
        private readonly EmissiveLightSettings _emissiveSettings = new EmissiveLightSettings();
        private readonly Light[] _packedLights = new Light[MaxLights];
        private readonly LightSamplingSettings _lightSamplingSettings = new LightSamplingSettings();
        private ComputeShader _motionCopy; // MotionCopy.compute: previous skinned poses for motion vectors
        private ComputeShader _displaceCs; // Displace.compute: the game's VERTEX_DISPLACEMENT for the RT scene (SceneProxies.Displaced.cs)
        private ComputeShader _lightGridCs; // LightGrid.compute: camera-centered 3D light grid (ReGIR-lite) for RIS (LightSampling.cs)
        private RayQueryBridge _rayQuery;  // native D3D12 cs_6_5 inline RayQuery bridge (RayQueryBridge.cs)
        private Texture _lastSkyTex;
        private Vector4 _lastSkyTint = Vector4.one, _lastSkyParams = Vector4.zero;
        /// <summary>Runtime switch for vertex displacement in the RT scene (bench variant plugin._displace=0: rest pose, as before).</summary>
        private bool _displace = true;
        private float _displaceRange = 40f; // bench variant plugin._displaceRange: copies beyond this (m) freeze (SceneProxies.DisplacementRange)
        private bool _displaceMotion = true; // bench variant plugin._displaceMotion=0: displaced proxies without per-vertex motion vectors
        private bool _portals = true; // -ultrart-noportals (bench variant: plugin._portals=0): rays ignore portals (docs/portals.md)
        private int _portalHops = 3;  // -ultrart-portal-hops N (bench variant: plugin._portalHops=N): teleports per ray
        private bool _rtMirrors;      // -ultrart-rtmirrors (bench: plugin._rtMirrors=1): camera rays reflect off portal mirrors instead of keeping the game's mirror view
        /// <summary>Trim redundant deep PortalRenderV2 raster recursions on non-mirror portals while RT replaces opaque portal views. Bench plugin._portalRasterTrim=0.</summary>
        private bool _portalRasterTrim = true;
        /// <summary>Skip the game's raster portal views (PortalRenderV2.Render) while RT replaces them; mirrors and the sky behind
        /// portals are then traced (docs/portals.md, "Raster skip"). -ultrart-portal-raster-skip on|off, bench plugin._portalRasterSkip=0.</summary>
        private bool _portalRasterSkip = true;
        /// <summary>One hidden instance per hit-group shader in the RTAS from the scene build on, so a new shader mid-level
        /// (the first effect billboard) doesn't rebuild every RT pipeline (SceneProxies.WarmHitGroups.cs). Bench plugin._warmHitGroups=0.</summary>
        private bool _warmHitGroups = true;
        private static readonly int PortalOwnId = Shader.PropertyToID("_UltraRTPortalOwn");
        // Which rays see V1's third-person model (SceneProxies.PlayerMask; docs/reflections.md, "The player in reflections"):
        // 1 reflection rays (incl. water/glass), 2 bounce rays, 4 shadow rays, 8 camera rays after a portal hop (8-x
        // mirrors, as the game's portal views). Camera rays never see it directly (bit 16 does that; only a third-person cinematic shot sets it: _cineShowPlayer). -ultrart-player-rays N, bench plugin._playerRays=N.
        private int _playerRays = 15;
        // V1's glow in reflections (SceneProxies.Player.cs, docs/reflections.md "V1's glow"): the body's emissive materials (wings)
        // x _v1Glow; the third-person gun mirrors the viewmodel's emission x _v1WeaponGlow (0 = off). Bench plugin._v1Glow=F.
        private float _v1Glow = 2.5f;
        private float _v1WeaponGlow = 1f;
        // V1's camera lens (the yellow eye on its head) glows x _v1EyeGlow in RT (SceneProxies.PlayerEyeGlow; 0 = lit as before
        // 2026-11-21). Bench plugin._v1EyeGlow=F.
        private float _v1EyeGlow = 2.5f;
        // The item V1 holds (a skull, a torch) shown in its third-person left hand (SceneProxies.HeldItem.cs; false = not shown,
        // as before 2026-11-21). Bench plugin._v1HeldItem=0, -ultrart-v1-noheld.
        private bool _v1HeldItem = true;
        // Its largest side in metres (0 = the item's size on the ground). Bench plugin._v1HeldSize=X, -ultrart-v1-held-size X.
        private float _v1HeldSize = 0.35f;
        private static readonly int PlayerRaysId = Shader.PropertyToID("_UltraRTPlayerRays");
        private static readonly int PlayerBoxMinId = Shader.PropertyToID("_UltraRTPlayerBoxMin"), PlayerBoxMaxId = Shader.PropertyToID("_UltraRTPlayerBoxMax");
        private static readonly int PlayerEyeId = Shader.PropertyToID("_UltraRTPlayerEye");
        /// <summary>The player's own spot lights (7-3's flashlight, 10 m behind the eye) end their shadow rays at the eye
        /// plane, so nothing behind the player shadows them (-ultrart-noeyeclip, bench plugin._playerLightEyeClip=0).</summary>
        private bool _playerLightEyeClip = true;
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
            if (cam.name != "Main Camera")
            {
                ViewmodelPreRender(cam); // the HUD camera: viewmodel pass (Plugin.Viewmodel.cs)
                if (_hdrNativeFrame == Time.frameCount) HdrScenePreRender(cam, MonoSingleton<PostProcessV2_Handler>.Instance); // the virtual camera: native float HDR frame (Plugin.Hdr.cs)
                return;
            }
            if (_recordedFrame == Time.frameCount && cam == _attachedCam) return; // rendered twice in a frame: keep the recording
            Shader.SetGlobalVector(HdrNativeId, Vector4.zero); // native float HDR: set again by HdrPrepare when this frame has it
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
                                   $"clear {cam.clearFlags}, target {(cam.targetTexture != null ? cam.targetTexture.name : "screen")}, mask {cam.cullingMask:X}, " +
                                   $"near {cam.nearClipPlane} far {cam.farClipPlane}{(cam.farClipPlane / cam.nearClipPlane > 1e7f ? ": far plane past float precision (UltraRTCamera.hlsl)" : "")})");
            }

            _cmd.Clear();
            TransparentFrameReset();
            VolumetricsFrameReset(cam); // the after-transparents buffer (Plugin.Volumetrics.cs)
            VfxFrameReset(cam);         // screen effects, after it (Plugin.Vfx.cs)
            SceneProxies.PortalRasterTrim = _portalRasterTrim;
            bool rtReplaceActive = _proxies?.Rtas != null && _viewEnabled && _ptShader != null && _compositeMat != null
                && _proxies.Rtas.GetInstanceCount() > 0 && Modes[_mode].kind == Kind.Denoised;
            _proxies?.ApplyPortalRasterTrim(rtReplaceActive);
            // Raster skip: our callback runs before PortalManagerV2's (registered at plugin start, the game's per level), so
            // this frame's PortalRenderV2.Render already sees the new state, also on the frame RT turns off.
            SceneProxies.PortalRasterSkip = _portalRasterSkip;
            bool skip = rtReplaceActive && _portalRasterSkip && _portals && PortalRasterSkip.Hooked && _proxies.PortalSidesInRtas > 0;
            PortalRasterSkip.Proxies = _proxies;
            PortalRasterSkip.Active = skip;
            Shader.SetGlobalInt(PortalOwnId, skip ? 1 : 0);
            if (_proxies?.Rtas == null || !_viewEnabled || _ptShader == null || _compositeMat == null) return;
            if (_proxies.Rtas.GetInstanceCount() == 0) return; // dispatching against an empty RTAS hangs the GPU
            LogLongFrame();

            try { Trace(); }
            catch (Exception e)
            {
                _cmd.Clear();
                Logger.LogError($"Trace failed, disabling view: {e}");
                _viewEnabled = false;
            }
        }

        // Frames over 100 ms while tracing (docs/performance.md, "Combat stalls"): what changed in the RTAS around them.
        private int _longFrameLogs, _lfLastInstances, _lfPrevInstances;
        private void LogLongFrame()
        {
            int inst = (int)_proxies.Rtas.GetInstanceCount();
            float dt = Time.unscaledDeltaTime * 1000f;
            if (dt > 100f && _longFrameLogs < 20)
            {
                _longFrameLogs++;
                Logger.LogInfo($"Long frame: {dt:F1} ms at frame {Time.frameCount}, t {Time.realtimeSinceStartup:F2} s; RTAS instances {inst} (last frames {_lfPrevInstances}, {_lfLastInstances}); " +
                               $"effect proxies {_effectGeometry?.Active ?? -1} (first added at frame {EffectGeometry.FirstAddFrame}); warm hit groups {_proxies.WarmHitGroupCount}; portal views skipped {PortalRasterSkip.Skipped}, run {PortalRasterSkip.RanVanilla}");
            }
            _lfPrevInstances = _lfLastInstances;
            _lfLastInstances = inst;
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
            AutoExposure.PortalInstanceIds = _gInstanceId; // the game match skips portal pixels while the portal views are skipped
            // PathTrace writes albedo + emission + material + lit weight packed into _gSurface (Unity's 8-UAV limit,
            // freeing 2 UAV slots for the folded radiance cache), and the reflection into _gReflection.
            // ReflectionFilter's resolve unpacks _gAlbedo / _gEmission and writes _gSpecular.
            EnsureGBuffer(ref _gSurface, w, h, RenderTextureFormat.ARGBInt, "UltraRT Surface");
            EnsureGBuffer(ref _gReflection, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Reflection");
            EnsureGBuffer(ref _gSpecular, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Specular");
            EnsureGBuffer(ref _gReflDebug, w, h, RenderTextureFormat.ARGB32, "UltraRT Reflection Debug");
        }

        private RenderTexture _gRadiance, _gAlbedo, _gEmission, _gNormalDepth, _gInstanceId;
        private RenderTexture _gSurface, _gReflection, _gSpecular, _gReflDebug;
        /// <summary>Skip ReflectionFilter temporal+spatial passes when DLSS RR is active (RR filters specular). Bench plugin._reflBypassRR=0.</summary>
        private bool _reflBypassRR = true;
        /// <summary>C4: merge stationary non-batched dynamic MeshRenderers into static groups at scene build. Bench plugin._mergeStaticDynamic=0.</summary>
        private bool _mergeStaticDynamic = true;
        private string _capturePose;
        private float _lightRadius = 0.15f;   // -ultrart-light-radius (m)

        // Relight knobs (docs/relight.md): -ultrart-relight-falloff/-fill/-ambient/-exposure/-white
        private bool _relightActive;
        private float _relightFalloff = 1f, _relightFillCut = 1f, _relightAmbientCut = 1f, _relightExposure = 1.5f, _relightWhite = 3f;
        private Vector4 RelightParams => _relightActive
            ? new Vector4(_relightFalloff, _relightFillCut, _relightExposure, _relightAmbientCut)
            : Vector4.zero;
        private bool _fixtureShadows = true;  // -ultrart-nofixtureshadows
        /// <summary>-ultrart-aux-every N: the radiance-cache update runs every N-th frame (1 = every frame).
        /// docs/performance.md, "Pass schedule".</summary>
        public int _auxEvery = 1;
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

        /// <summary>A cubemap surface's colour as a lat-long texture (8-3's sun: SceneProxies.ApplyCubeSun). Kept for the
        /// session (not with the sky's targets, which are released when a fifth sky cubemap comes): a proxy material holds it.</summary>
        private readonly Dictionary<Texture, RenderTexture> _cubeLatLong = new Dictionary<Texture, RenderTexture>();

        private Texture CubeLatLong(Texture cube)
        {
            if (cube == null || _skyConvertShader == null || !_skyConvertShader.isSupported) return null;
            if (_cubeLatLong.TryGetValue(cube, out var rt) && rt != null && rt.IsCreated()) return rt;
            if (_cubeLatLong.Count >= 8) return null;
            if (_skyConvertMat == null) _skyConvertMat = new Material(_skyConvertShader) { name = "UltraRT SkyConvert" };
            rt = new RenderTexture(1024, 512, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, name = "UltraRT CubeLatLong " + cube.name };
            rt.Create();
            _skyConvertMat.SetTexture("_Cube", cube);
            Graphics.Blit(null, rt, _skyConvertMat);
            _cubeLatLong[cube] = rt;
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
            SgNoteSky(tex, tint); // stained glass follows the sky's brightness (Plugin.StainedGlass.cs, SgDayFactor)
            _lastSkyTex = tex;
            _lastSkyTint = tex == null ? Vector4.one : new Vector4(tint.r, tint.g, tint.b, 1f);
            _lastSkyParams = tex == null ? Vector4.zero : new Vector4(1f, rot * Mathf.Deg2Rad, 0f, 0f);
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

        /// <summary>The trace size of a frame: <paramref name="visW"/> x <paramref name="visH"/> visible at <paramref name="scale"/>,
        /// plus the guard band (docs/denoiser.md: a slightly wider view than the screen, so surfaces entering the screen while
        /// turning already have denoiser history and warm cache cells; the visible part keeps its pixel grid), resized
        /// for RR's exact grid. No side effects (Plugin.Dlss.cs sizes RR's feature with it before the frame).</summary>
        private void TraceDims(bool pt, float scale, int grid, RenderTexture depthBuf, out int w, out int h, out int gxPx, out int gyPx,
                               out Vector2 guardScale, out int visW, out int visH)
        {
            w = visW = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelWidth * scale));
            h = visH = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelHeight * scale));
            gxPx = pt && _guardActive ? Mathf.RoundToInt(w * Mathf.Clamp(_guardX, 0f, 0.25f)) : 0;
            gyPx = pt && _guardActive ? Mathf.RoundToInt(h * Mathf.Clamp(_guardY, 0f, 0.25f)) : 0;
            guardScale = new Vector2((float)(w + 2 * gxPx) / w, (float)(h + 2 * gyPx) / h);
            w += 2 * gxPx;
            h += 2 * gyPx;
            if (grid > 2 && depthBuf != null) ExactGridTrace(grid, depthBuf.width, depthBuf.height, gxPx, gyPx, ref w, ref h, ref guardScale); // an integer upscale: RR or the remodulating fallback (Plugin.Dlss.cs)
        }

        private void Trace()
        {
            var (_, kind, mode) = Modes[_mode];
            bool denoised = kind == Kind.Denoised;
            SetPlayerSplit(kind == Kind.Denoised); // Plugin.Noise.cs
            bool pt = kind != Kind.Debug;
            bool resolve = denoised || kind == Kind.Reflection; // modes that read the unpacked surface / filtered reflection
            if (denoised && _lastKind != Kind.Denoised) _denoiser.Reset(); // history is stale after other modes
            if (pt && _lastKind == Kind.Debug) _lightSampling.Reset();
            if (resolve && _lastKind != Kind.Denoised && _lastKind != Kind.Reflection) _reflFilter.Reset();
            _lastKind = kind;
            if (resolve) _accumulate = false; // the denoiser and the reflection filter need unjittered primary rays
            // Temporal upsampling (pt-dn, relight, pathtrace, dn-*): jitter the trace grid over the screen pixels;
            // Upsample.compute accumulates the result at screen resolution using the game's depth buffer.
            var depthBuf = pt && !_noDepthMatch ? MonoSingleton<PostProcessV2_Handler>.Instance?.depthBuffer : null;
            bool full = PathTraceActive; // the pathtrace mode: RayGenPT, its own trace scale (Plugin.PathTrace.cs)
            bool rr = RayReconstructionFor(kind, depthBuf); // DLSS RR replaces the denoiser and the upsampler (Plugin.Dlss.cs)
            float scale = pt ? TraceScaleFor(full, rr) : 1f;
            if (scale != _lastTraceScale) { _lastTraceScale = scale; _lightSampling.Reset(); } // reservoirs are per trace pixel
            bool taau = kind == Kind.Denoised && _upsampler != null && _upsampler.Settings.enabled && depthBuf != null;
            bool remod = RemodFor(full, rr, taau); // the pathtrace fallback remodulates like RR (Plugin.Remod.cs)
            _remodGridNow = remod ? RemodGrid(scale) : 0;
            TraceDims(pt, scale, rr ? _rr.ExactGrid : _remodGridNow, depthBuf, out int w, out int h, out int gxPx, out int gyPx, out _guardScale, out int visW, out int visH);
            _jitter = taau && _upsampler.Settings.jitter ? _upsampler.NextJitter(depthBuf.width, depthBuf.height, visW, visH) : Vector2.zero;
            if (_remodGridNow > 2 && _upsampler.Settings.jitter) _jitter = DlssRR.GridJitter(_remodJitterIndex++, _remodGridNow);
            if (rr) _jitter = _rr.NextJitter();
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
            if (_readdDynamic) { _readdDynamic = false; Logger.LogInfo($"Re-added {_proxies.ReAddDynamic()} dynamic instances (diagnostic)"); }
            _proxies.SyncDynamic();
            _proxies.UpdateLava(); // scroll offsets of the lava proxies (SceneProxies.Lava.cs)
            _proxies.UpdateCaustics(); // caustic volumes and settings (SceneProxies.Caustics.cs)
            // Water planes for ripple caustics (same file), and for stained-glass light under water while windows are bound.
            if (_camera != null) _proxies.UpdateRippleCaustics(_camera.transform.position, SceneProxies.Transparents.waveFade, _sgBound > 0 && _sgSettings.water);
            UpdateWaterImpulses(); // interactive water ripples: splashes, wakes, explosions (Plugin.WaterRipples.cs)
            _proxies.UpdateVeils(); // the same for the veil proxies (SceneProxies.Veils.cs)
            _proxies.UpdateFrosted(); // frosted (ice) scene proxies on/off (SceneProxies.Transparent.cs)
            SceneProxies.PortalHops = _portalHops;
            // The game's mirror views are skipped with the other portal views: camera rays reflect off the mirrors instead.
            SceneProxies.TraceMirrors = _rtMirrors || PortalRasterSkip.Active;
            SceneProxies.MovingPortalMotion = _portalMotion;
            SceneProxies.MovingPortalEdges = _portalEdges;
            Shader.SetGlobalFloat("_UltraRTPortalStart", _portalStart);
            SceneProxies.HideHeldItems = _hideHeld; SceneProxies.SkinnedScaleUpdates = _skinScale;
            DlssRR.GrazingMatch = _grazingMatch;
            DlssRR.MatchDebug = _matchDebug;
            DlssRR.ThinMatch = _thinMatch;
            DlssRR.ThinNear = _thinNear;
            Shader.SetGlobalFloat("_UltraRTWaterBand", _waterBand ? 1f : 0f);
            _proxies.SyncPortals(_portals); // portal quads + travel matrices (docs/portals.md); binds _UltraRTPortals
            PortalCrossingRemap(); // the camera went through a portal: histories follow it (Plugin.PortalTemporal.cs)
            PortalViews(); // the camera's images through portals: light importance, emitter importance, cache cell size
            Shader.SetGlobalInt(PlayerRaysId, SceneProxies.TracePlayerModel ? (_playerRays & 15) | (_cineShowPlayer ? 16 : 0) : 0); // 16: third-person cinematic shots
            SceneProxies.PlayerEmissionGain = _v1Glow;
            SceneProxies.PlayerWeaponGlow = _v1WeaponGlow;
            SceneProxies.PlayerEyeGlow = _v1EyeGlow;
            _proxies.UpdatePlayerGlow(); // the wings' gain, the held gun's glow mirrored from the viewmodel
            SceneProxies.PlayerHeldItem = _v1HeldItem;
            SceneProxies.PlayerHeldSize = _v1HeldSize;
            _proxies.UpdateHeldItem(); // the held skull / torch in V1's third-person hand (reflections)
            var eye = _camera.transform.position; // the player's own spot lights end their shadow rays here (UltraRTLights.hlsl)
            Shader.SetGlobalVector(PlayerEyeId, new Vector4(eye.x, eye.y, eye.z, _playerLightEyeClip ? 1f : 0f));
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
            _emissive.ViewPoints = _portalViews; // portal-aware importance for glowing surfaces too
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
                               $"{(double)mv.RigidUpdates / _cpuFrames:F1} rigid updates/frame, {(double)_proxies.SkinnedTransformUpdateCount / _cpuFrames:F1} skinned transform updates/frame, {_proxies.NewInstancesReAdded} new rigid instances re-added ({_proxies.NewMeshInstancesReAdded} with a mesh new to the RTAS; scene total), {_proxies.SkinnedSettleReAdds} skinned re-added after a pose settled, motion CPU {mv.Ticks * 1000.0 / Stopwatch.Frequency / _cpuFrames:F3} ms/frame; " +
                               $"deferred proxies: {_proxies.DeferredMade} made in {_proxies.DeferredMs:F1} ms (queue max {_proxies.DeferredMaxQueue}, now {_proxies.DeferredCount}), " +
                               $"{_proxies.HierarchiesAdded} hierarchies ({Patches.SpawnCount} instantiated), {_proxies.Lights.Count} lights tracked ({_activeLights.Count} on); " +
                               $"emissive: {_emissive.AliasBuilds} alias rebuilds ({_emissive.AliasTicks * 1000.0 / Stopwatch.Frequency:F1} ms), {_emissive.ActiveUpdates} active-set updates, " +
                               $"new {_emissive.PendingTicks * 1000.0 / Stopwatch.Frequency:F1} ms, visibility {_emissive.VisibilityTicks * 1000.0 / Stopwatch.Frequency:F1} ms, transforms {_emissive.TransformTicks * 1000.0 / Stopwatch.Frequency:F1} ms; " +
                               $"{_emissive.EmitterCount} emitters, {_emissive.OwnerCount} owners, {_handBuilds} handover builds; dynamic: {_proxies.DirtyEvaluations} dirty evaluations " +
                               $"({_proxies.DirtyTicks * Perf.TickMs:F1} ms), active loop {_proxies.ActiveTicks * Perf.TickMs:F1} ms over {_proxies.ActiveDynamicCount}, poll {_proxies.PollTicks * Perf.TickMs:F1} ms");
                Logger.LogInfo($"Material sync ({Patches.PortalClones} portal clones registered so far): {_proxies.SyncSwaps} swaps, {_proxies.SyncStaticSwaps} static slot swaps, {_proxies.SyncBlockUpdates} property-block updates " +
                               $"({_proxies.SyncHot} proxies followed), {_proxies.SyncPropertyUpdates} material property re-copies; {_proxies.SyncTicks * Perf.TickMs / _cpuFrames:F4} ms/frame " +
                               $"(swap checks {_proxies.SyncSwapTicks * Perf.TickMs / _cpuFrames:F4}, blocks {_proxies.SyncHotTicks * Perf.TickMs / _cpuFrames:F4}, re-copies {_proxies.SyncCopyTicks * Perf.TickMs / _cpuFrames:F4}, static {_proxies.SyncStaticTicks * Perf.TickMs / _cpuFrames:F4})");
                _proxies.SyncSwaps = _proxies.SyncStaticSwaps = _proxies.SyncBlockUpdates = _proxies.SyncPropertyUpdates = 0;
                _proxies.SyncTicks = _proxies.SyncSwapTicks = _proxies.SyncHotTicks = _proxies.SyncCopyTicks = _proxies.SyncStaticTicks = 0;
                Logger.LogInfo(_proxies.CutoutAudit());
                var scrollLine = _proxies.DescribeScrolling();
                if (scrollLine != null) Logger.LogInfo(scrollLine);
                if (_proxies.PlayerProxiesTotal > 0) Logger.LogInfo(_proxies.DescribePlayerModel(_camera.transform.position));
                if (_proxies.DisplacedCount > 0)
                    Logger.LogInfo($"Vertex displacement ({(_displace ? "on" : "off")}): {_proxies.DisplacedCount} displaced proxies, {(double)_proxies.DisplaceDispatches / _cpuFrames:F1} dispatches/frame, " +
                                   $"{(double)_proxies.DisplaceVertices / _cpuFrames:F0} vertices/frame, {(double)_proxies.DisplaceFrozen / _cpuFrames:F1} frozen/frame (range {_displaceRange} m, {_proxies.DisplaceSwitches} switches), record CPU {_proxies.DisplaceTicks * Perf.TickMs / _cpuFrames:F4} ms/frame");
                _proxies.DisplaceDispatches = 0; _proxies.DisplaceVertices = 0; _proxies.DisplaceFrozen = 0; _proxies.DisplaceSwitches = 0; _proxies.DisplaceTicks = 0;
                _proxies.DirtyEvaluations = 0; _proxies.DirtyTicks = _proxies.ActiveTicks = _proxies.PollTicks = 0;
                _emissive.AliasBuilds = 0; _emissive.AliasTicks = 0; _emissive.ActiveUpdates = 0; _emissive.PendingTicks = _emissive.VisibilityTicks = _emissive.TransformTicks = 0;
                _handBuilds = 0;
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
            if (_proxies.DisplacedCount > 0)
            {
                // The game's vertex displacement into the displaced proxies' meshes (their BLAS is rebuilt from them below).
                SceneProxies.DisplacementActive = _displace;
                SceneProxies.DisplacementMotion = _displaceMotion;
                SceneProxies.DisplacementRange = _displaceRange;
                _gpu?.Begin(_cmd, SlotDisplace);
                _proxies.RecordDisplacement(_cmd, _camera.transform.position);
                _gpu?.End(_cmd, SlotDisplace);
            }
            _gpu?.Begin(_cmd, SlotRtas);
            _rayQuery?.BeginRtasBuild(_cmd);
            _cmd.BuildRayTracingAccelerationStructure(_proxies.Rtas);
            _rayQuery?.EndRtasBuild(_cmd);
            _gpu?.End(_cmd, SlotRtas);
            _cmd.EndSample(SampleBuild);

            var shader = MainTraceShader(pt); // the counting variant with plugin._rayCounts=1 (Plugin.RayCounts.cs)
            shader.SetShaderPass("UltraRTHit");
            _cmd.SetRayTracingAccelerationStructure(shader, "_Scene", _proxies.Rtas);
            _cmd.SetRayTracingTextureParam(shader, "_Output", _output);
            _cmd.SetRayTracingMatrixParam(shader, "_InvViewProj", invViewProj);
            _cmd.SetRayTracingVectorParam(shader, "_CamPos", _camera.transform.position);
            _cmd.SetRayTracingIntParam(shader, "_Mode", resolve || mode >= CacheDebugModeBase ? 0 : mode);
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
                _cmd.SetRayTracingIntParam(shader, "_LightCount", _surfLightCount);
                long tls = Perf.Now;
                PathTraceLightsBegin(full, rr); // RR may want another light sampling mode (restored below)
                _lightSampling.ViewPoints.Clear();
                _lightSampling.ViewPoints.AddRange(_portalViews); // portal-aware importance
                _lightSampling.Bind(_cmd, shader, _camera.transform.position, _packedLights, _lightData, _surfLightCount, _lightBuffer);
                Perf.Add(Perf.Sec.LightSampling, tls);
                _emissive.Bind(_cmd, shader, _lightSampling.ActiveMode);
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
                long tc = Perf.Now;
                TraceRadianceCache(shader, w, h, invViewProj, ambient);
                Perf.Add(Perf.Sec.Cache, tc);
                BindTransparents(shader, denoised, viewProj, w, h); // water and glass (Plugin.Transparent.cs)
                BindBloodWet(shader, w, h); // wet blood: the wetness mask of fresh stains (Plugin.Wet.cs)
                BindStains(shader, kind == Kind.Denoised || kind == Kind.Reflection); // blood stains at reflection hits (Plugin.Stains.cs)
                BindStainedGlass(shader); // stained-glass windows project their mosaic (Plugin.StainedGlass.cs)
                BindPathTrace(shader, full, rr);
                PathTraceLightsEnd();
            }
            else
            {
                _cmd.SetRayTracingFloatParam(shader, "_DepthScale", 150f);
            }

            if (pt && !full && !_ptPrewarmed && _ptSettings.prewarm && _ptPrewarmWanted)
            {
                // The pathtrace raygen's pipeline is compiled on its first dispatch (16-33 s after a shader change, docs/path-tracing.md,
                // "Warm-up"): do it now, on the session's first trace, at one pixel. RayGen below rewrites that pixel.
                _ptPrewarmed = true;
                _ptPrewarmFrame = Time.frameCount;
                _cmd.DispatchRays(shader, "RayGenPT", 1, 1, 1);
            }
            else if (full) _ptPrewarmed = true;
            // Lights imaged through portals (Plugin.PortalLights.cs) have their own ray generations, RayGenImg / RayGenPTImg:
            // compiled into RayGen / RayGenPT, that code cost ~0.1 ms of dispatch in every scene without any
            // (docs/performance.md, "2026-11-20 batch"). Their pipelines compile on first dispatch too: warmed at one pixel
            // on the session's first trace (the main menu) and on the first frame of a level with portal sides, for the
            // mode in use, so the first image doesn't stall the frame it appears in.
            bool imgEntry = pt && PortalLightImages > 0;
            if (pt && _ptSettings.prewarm && _portalLights)
            {
                int bit = full ? 2 : 1;
                bool want = (_imgPrewarmed & bit) == 0 && (_imgPrewarmFirst || (_proxies != null && _proxies.PortalSideCount > 0));
                if (!full && _imgPrewarmFirst && _ptPrewarmWanted && (_imgPrewarmed & 2) == 0)
                {
                    _imgPrewarmed |= 2;
                    _cmd.DispatchRays(shader, "RayGenPTImg", 1, 1, 1);
                }
                _imgPrewarmFirst = false;
                if (want && !imgEntry)
                {
                    _imgPrewarmed |= bit;
                    _cmd.DispatchRays(shader, full ? "RayGenPTImg" : "RayGenImg", 1, 1, 1);
                }
                else if (imgEntry) _imgPrewarmed |= bit;
            }
            _cmd.BeginSample(SampleTrace);
            _gpu?.Begin(_cmd, SlotDispatch);
            string entry = pt && _lightSampling.ActiveMode == LightSamplingMode.Reference
                ? "RayGenRef"
                : pt && full ? (imgEntry ? "RayGenPTImg" : "RayGenPT") : imgEntry ? "RayGenImg" : "RayGen";
            _cmd.DispatchRays(shader, entry, (uint)w, (uint)h, 1);
            _rtTracedFrame = Time.frameCount; // white orb shells follow it (SceneProxies.OrbShells.cs)
            RequestRayCounts(pt, w, h, (full ? "pathtrace" : kind == Kind.Denoised ? "relight/pt-dn" : kind.ToString()) + (_transRect ? "" : ", transRect off")); // plugin._rayCounts=1 (Plugin.RayCounts.cs)
            _gpu?.End(_cmd, SlotDispatch);
            _cmd.EndSample(SampleTrace);
            if (pt) ResolveRadianceCache();
            if (_diagDispatches > 0 && pt) DiagDispatches(invViewProj); // bench: per-dispatch render-thread cost (docs/performance.md)
            _proxies.RecordMotionCopies(_cmd); // this frame's skinned poses become next frame's previous poses
            if (kind == Kind.PathTrace) TraceCacheDebug(mode, invViewProj);
            long tpl = Perf.Now;
            if (pt) TraceParticleLight(kind); // lit particles' lighting volume (Plugin.Particles.cs)
            Perf.Add(Perf.Sec.Particles, tpl);
            long tdn = Perf.Now;

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
                _reflFilter.Execute(_cmd, reflInputs, _reflSettings, kind == Kind.Reflection ? mode : 0, bypassFilter: rr && _reflBypassRR && kind != Kind.Reflection);
                _cmd.EndSample(SampleReflections);
                if (kind == Kind.Reflection) _displayed = _gReflDebug;
            }
            if (denoised)
            {
                // Native float HDR (Plugin.Hdr.cs): the composite writes colour above 1, so its output is float.
                var denoisedFormat = HdrNativeLive ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                if (_denoised == null || _denoised.width != w || _denoised.height != h || _denoised.format != denoisedFormat)
                {
                    if (_denoised != null) _denoised.Release();
                    _denoised = new RenderTexture(w, h, 0, denoisedFormat, RenderTextureReadWrite.Linear)
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
                if (remod) RemodPrepare(w, h, invViewProj);
                else _denoiser.RemodTarget = _denoiser.RemodDemod = null;
                HdrPrepare(kind, mode, rr, w, h, depthBuf); // HDR display gain map (Plugin.Hdr.cs)
                if (!rr) _denoiser.Execute(_cmd, inputs, _denoised);
                if (!rr) HdrSubmitDenoiser();
                if (!rr) RunExposure(w, h); // relight auto exposure: measured after the denoiser, applied from the next frame
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
                if (remod) _cmd.SetGlobalTexture(UltraRTUpsampledId, RemodUpsample(new Vector2Int(gxPx, gyPx), reset));
                else
                {
                    _upsampler.Unclamped = _hdrNativeNow; // native float HDR: the image goes above 1 (Plugin.Hdr.cs)
                    _upsampler.Execute(_cmd, _camera, _denoised, _gNormalDepth, _gInstanceId, _depthCopy,
                        new Vector2Int(gxPx, gyPx), _guardScale, _jitter, reset);
                    _cmd.SetGlobalTexture(UltraRTUpsampledId, _upsampler.Output);
                }
                _gpu?.End(_cmd, SlotUpsample);
                upsampled = true;
            }

            Perf.Add(Perf.Sec.Denoise, tdn); // reflections filter, denoiser or RR, exposure, upsampler
            // Composite into the camera's current target without changing render-target bindings.
            _gpu?.Begin(_cmd, SlotBlit);
            _cmd.SetGlobalTexture(UltraRTImageId, _displayed);
            _cmd.SetGlobalVector(UltraRTUVScaleId, new Vector4(1f / _guardScale.x, 1f / _guardScale.y, upsampled ? 1f : 0f, 0f));
            _cmd.SetGlobalTexture(UltraRTNormalDepthId, pt ? (Texture)_gNormalDepth : Texture2D.whiteTexture);
            _cmd.SetGlobalTexture(UltraRTInstanceIdId, _gInstanceId); // portal pixels: distance to the portal (Composite.shader)
            // The game's fog on traced pixels skips "NoFog" materials (SurfaceNoFog; VolumetricsComposite.shader, pass 0).
            if (_gSurface != null) _cmd.SetGlobalTexture(UltraRTSurfaceId, _gSurface);
            _cmd.SetGlobalFloat(UltraRTSurfaceOnId, _gSurface != null && _noFogSkip ? 1f : 0f);
            _cmd.SetGlobalFloat(UltraRTFlipYId, _flipY ? 1f : 0f);
            _cmd.DrawProcedural(Matrix4x4.identity, _compositeMat, 0, MeshTopology.Triangles, 3);
            _gpu?.End(_cmd, SlotBlit);
            long tv = Perf.Now;
            TraceVolumetrics(kind, upsampled); // sun shafts, light halos, the game's fog on RT pixels (Plugin.Volumetrics.cs)
            TraceVeils(kind, mode); // behind portals: the rest of the game's fog, blended veils (Plugin.Veils.cs)
            Perf.Add(Perf.Sec.Volumetrics, tv);
            CopyPreAlpha(); // refraction background for the water overlays (Plugin.Transparent.cs)
            long tx = Perf.Now;
            TraceVfx(kind); // bloom, heat haze, shockwaves, underwater: after the transparents (Plugin.Vfx.cs)
            Perf.Add(Perf.Sec.Vfx, tx);
            _gpu?.End(_cmd, SlotTotal); // the frame's queries are resolved at the end of the frame (InstallFrameSlot)

            ViewmodelTraced(kind); // the HUD camera lights the viewmodel against this frame (Plugin.Viewmodel.cs)
            _frameIndex++;
            Perf.Add(Perf.Sec.Trace, traceStart);
            if (pt && _accumulate) _accumFrames++;
            if (_tracedFrames++ == 0) Logger.LogInfo($"First trace OK: {w}x{h}, instances={_proxies.Rtas.GetInstanceCount()}, lights={_lightCount}, cache={(_radianceCache?.Active == true ? $"{_cacheSettings.mode} {_radianceCache.Capacity} slots {_radianceCache.GpuBytes >> 20} MB" : "off")}, rayQuery={(_rayQuery != null ? _rayQuery.Status : "off")}");
        }

        // Bench diagnostics (docs/performance.md, "Render thread"): plugin._diagDispatches=N records N extra 1x1 DispatchRays of
        // the debug view against the scene RTAS; plugin._diagNoHit=1 gives them a hit pass no material has (no hit groups).
        private int _diagDispatches;
        private bool _diagNoHit;
        private RenderTexture _diagTarget;

        private void DiagDispatches(Matrix4x4 invViewProj)
        {
            if (_debugShader == null) return;
            if (_diagTarget == null)
            {
                _diagTarget = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf) { enableRandomWrite = true, name = "UltraRT Diag" };
                _diagTarget.Create();
            }
            _debugShader.SetShaderPass(_diagNoHit ? "UltraRTDiagNoSuchPass" : "UltraRTHit");
            _cmd.SetRayTracingAccelerationStructure(_debugShader, "_Scene", _proxies.Rtas);
            _cmd.SetRayTracingTextureParam(_debugShader, "_Output", _diagTarget);
            _cmd.SetRayTracingMatrixParam(_debugShader, "_InvViewProj", invViewProj);
            _cmd.SetRayTracingVectorParam(_debugShader, "_CamPos", _camera.transform.position);
            _cmd.SetRayTracingIntParam(_debugShader, "_Mode", 1);
            _cmd.SetRayTracingFloatParam(_debugShader, "_DepthScale", 150f);
            for (int k = 0; k < _diagDispatches; k++) _cmd.DispatchRays(_debugShader, "RayGen", 1, 1, 1);
        }

        // Debug markers (visible in PIX/RenderDoc). Unity's Recorder reports 0 GPU time in this release player;
        // real GPU timings come from the native timer plugin.
        private const string SampleBuild = "UltraRT.BuildRTAS", SampleTrace = "UltraRT.DispatchRays", SampleDenoise = "UltraRT.Denoise",
            SampleReflections = "UltraRT.Reflections";

        // ---------------------------------------------------------------- GPU timing (opt-in, -ultrart-gputimer)

        private const int SlotTotal = 0, SlotRtas = 1, SlotDispatch = 2, SlotDenoise = 3, SlotBlit = 4, SlotCache = 5, SlotExposure = 6, SlotPreAlpha = 7, SlotUpsample = 8, SlotVolumetrics = 9, SlotVolLate = 10, SlotDisplace = 21, SlotFrame = 22, SlotVeils = 23, SlotCount = 24; // 21: vertex displacement (before the RTAS build); 11-15: viewmodel pass and its stages (Plugin.Viewmodel.cs), 16: SlotParticles (Plugin.Particles.cs), 17-18: DLSS RR (Plugin.Dlss.cs), 19-20: screen effects (Plugin.Vfx.cs), 23: SlotVeils (Plugin.Veils.cs)
        private static readonly string[] SlotNames = { "total", "rtas", "dispatch", "denoise", "blit", "cache", "exposure", "prealpha", "upsample", "volumetrics", "vollate", "viewmodel", "vm-gbuffer", "vm-rays", "vm-filter", "vm-composite", "particles", "rr", "rr-eval", "vfx", "vfxlate", "displace", "frame", "veils" };
        private bool _useGpuTimer;
        private GpuTimer _gpu;
        private long _lastResolved;
        private readonly double[] _gpuSum = new double[SlotCount];
        private readonly int[] _gpuN = new int[SlotCount];

        private CommandBuffer _frameBeginCmd, _frameEndCmd;

        /// <summary>
        /// The whole-frame slot ("frame"): from right before the frame's rendering starts on the main thread to right after
        /// it ends, before the present, in every mode including off. Its GPU time is the span the GPU took for the frame's
        /// commands (the game's and ours), so a frame is GPU-bound when it is close to the frame time; its render-thread
        /// time is the span from the first to the last of them on that thread. The frame's queries are resolved here, after
        /// every camera, so late passes (viewmodel, late volumetrics) land in the same frame as the trace.
        /// </summary>
        private void InstallFrameSlot()
        {
            Perf.InstallFrameMarkers();
            _frameBeginCmd = new CommandBuffer { name = "UltraRT frame begin" };
            _gpu.Begin(_frameBeginCmd, SlotFrame);
            _frameEndCmd = new CommandBuffer { name = "UltraRT frame end" };
            _gpu.End(_frameEndCmd, SlotFrame);
            _gpu.EndFrame(_frameEndCmd);
            Perf.OnRenderStart = () => { if (_gpu != null) Graphics.ExecuteCommandBuffer(_frameBeginCmd); };
            Perf.OnRenderEnd = () =>
            {
                if (_gpu == null) return;
                Graphics.ExecuteCommandBuffer(_frameEndCmd);
                SampleGpuTimings();
            };
        }

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
