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
    ///   F7 toggle view   F8 next mode   F9 reload shader bundle (also automatic when the file changes)
    /// Unattended testing (see scripts/run-debugview.ps1):
    ///   -ultrart-scene "Level 1-1"  -ultrart-view N  -ultrart-scale 0.5  -ultrart-capture  -ultrart-quit
    /// </summary>
    [BepInPlugin("ooo.lew.ultrart", "UltraRT", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        private enum Kind { Debug, PathTrace, Denoised }

        // For Denoised, `mode` is a DenoiserDebugView; the trace itself runs PathTrace mode 0.
        private static readonly (string name, Kind kind, int mode)[] Modes =
        {
            ("pt-dn", Kind.Denoised, (int)DenoiserDebugView.Final),
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
        private RenderTexture _output, _accum;
        private ComputeBuffer _lightBuffer;
        private readonly Vector4[] _lightData = new Vector4[MaxLights * 4];
        private int _lightCount;
        private Light[] _sceneLights = Array.Empty<Light>();
        private float _nextLightScan;
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
            ParseArgs();
            _outDir = Path.Combine(Paths.BepInExRootPath, "ultrart");
            Directory.CreateDirectory(_outDir);

            Logger.LogInfo($"API={SystemInfo.graphicsDeviceType} supportsRayTracing={SystemInfo.supportsRayTracing} colorSpace={QualitySettings.activeColorSpace}");
            if (!SystemInfo.supportsRayTracing)
            {
                Logger.LogWarning("Ray tracing unavailable (launch with -force-d3d12 on a DXR GPU). UltraRT is idle.");
                return;
            }

            _bundlePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", "ultrart.shaders");
            if (!LoadShaders()) return;

            if (_useGpuTimer)
            {
                // Opt-in dev tool: the native DLL registers itself with Unity through an internal routine.
                var nativePath = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", GpuTimer.DllName);
                if (GpuTimer.TryCreate(nativePath, out _gpu, out var gpuError)) Logger.LogInfo($"GPU timer: {_gpu.Status}");
                else Logger.LogWarning($"GPU timer unavailable: {gpuError}");
                _cmdPost = new CommandBuffer { name = "UltraRT Post" };
            }

            Patches.Apply(Info.Metadata.GUID);
            _cmd = new CommandBuffer { name = "UltraRT" };
            _lightBuffer = new ComputeBuffer(MaxLights * 4, 16);
            var go = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
            _host.OnUpdate = OnUpdate;

            SceneManager.activeSceneChanged += (_, to) =>
            {
                Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                ResetScene();
                _host.StartCoroutine(BuildWhenLoaded());
            };

            _host.StartCoroutine(RenderLoop());
            if (!string.IsNullOrEmpty(_autoScene)) _host.StartCoroutine(AutoRun());
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
                    case "-ultrart-spawn-test": _spawnTest = true; break;
                    case "-ultrart-gputimer": _useGpuTimer = true; break;
                    case "-ultrart-nostatic": SceneProxies.SkipStaticBatches = true; break;
                    case "-ultrart-nomerge": SceneProxies.NoSubmeshMerge = true; break;
                    case "-ultrart-gputimer-flags": Environment.SetEnvironmentVariable("ULTRART_GPUTIMER_FLAGS", Next()); break;
                    case "-ultrart-ambient": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _primaryAmbient); break;
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
            var proxy = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/Proxy");
            var denoise = bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(s => s.name == "Denoise");
            Logger.LogInfo($"Bundle loaded: DebugView={(debug ? "ok" : "MISSING")} PathTrace={(pt ? "ok" : "MISSING")} " +
                           $"proxy={(proxy ? (proxy.isSupported ? "ok" : "UNSUPPORTED") : "MISSING")} Denoise={(denoise ? "ok" : "MISSING")}");
            if (debug == null || pt == null || proxy == null || denoise == null) { bundle.Unload(true); return false; }

            var settings = _denoiser?.Settings;
            _denoiser?.Dispose();
            _denoiser = new Denoiser(denoise);
            if (settings != null) _denoiser.Settings = settings;

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
            try
            {
                if (Input.GetKeyDown(KeyCode.F7)) { _viewEnabled = !_viewEnabled; _denoiser?.Reset(); Logger.LogInfo($"View {(_viewEnabled ? "on" : "off")}"); }
                if (Input.GetKeyDown(KeyCode.F8)) { _mode = (_mode + 1) % Modes.Length; Logger.LogInfo($"Mode: {Modes[_mode].name}"); }
                if (Input.GetKeyDown(KeyCode.F9)) ReloadShaders();
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }

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
            _proxies?.Dispose();
            _proxies = null;
            _accumFrames = 0;
            _denoiser?.Reset();
        }

        private Denoiser _denoiser;
        private RenderTexture _denoised;
        private RenderTexture _displayed;
        private Kind _lastKind = Kind.Debug;

        private IEnumerator BuildWhenLoaded()
        {
            yield return null;
            while (SceneHelper.PendingScene != null) yield return null;
            yield return new WaitForSecondsRealtime(1f);
            if (_proxies != null || _proxyShader == null) yield break;
            try
            {
                var sw = Stopwatch.StartNew();
                var cam = Camera.allCameras.FirstOrDefault(c => c.name == "Main Camera") ?? Camera.main;
                _proxies = new SceneProxies(Logger, _proxyShader, cam != null ? cam.cullingMask : ~0);
                _proxies.Build();
                Logger.LogInfo($"Scene RT setup took {sw.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception e)
            {
                Logger.LogError($"Proxy build failed: {e}");
                ResetScene();
            }
        }

        private void GatherLights()
        {
            if (Time.realtimeSinceStartup >= _nextLightScan)
            {
                _nextLightScan = Time.realtimeSinceStartup + 1f;
                _sceneLights = FindObjectsOfType<Light>();
            }
            // Layout must match PathTrace.raytrace: 4 x float4 per light (see the comment on _Lights there).
            _lightCount = 0;
            foreach (var l in _sceneLights)
            {
                if (_lightCount >= MaxLights) break;
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                var t = l.transform;
                var fwd = t.forward;
                var c = l.color * l.intensity;
                Vector4 l0, l3;
                switch (l.type)
                {
                    case LightType.Point:
                        l0 = new Vector4(t.position.x, t.position.y, t.position.z, Mathf.Max(l.range, 1e-4f));
                        l3 = new Vector4(-1f, 1f, 0, 0);
                        break;
                    case LightType.Spot:
                    {
                        l0 = new Vector4(t.position.x, t.position.y, t.position.z, Mathf.Max(l.range, 1e-4f));
                        float a = l.spotAngle * Mathf.Deg2Rad;
                        float cosHalf = Mathf.Cos(a * 0.5f), cosQuarter = Mathf.Cos(a * 0.25f);
                        l3 = new Vector4(cosHalf, 1f / Mathf.Max(cosQuarter - cosHalf, 1e-4f), 0, 0);
                        break;
                    }
                    case LightType.Directional:
                        l0 = new Vector4(-fwd.x, -fwd.y, -fwd.z, 0f);
                        l3 = new Vector4(-1f, 1f, 0, 0);
                        break;
                    default:
                        continue;
                }
                int i = _lightCount * 4;
                _lightData[i] = l0;
                _lightData[i + 1] = new Vector4(c.r, c.g, c.b, AsFloat(l.cullingMask));
                _lightData[i + 2] = new Vector4(fwd.x, fwd.y, fwd.z, 0);
                _lightData[i + 3] = l3;
                _lightCount++;
            }
            _lightBuffer.SetData(_lightData, 0, 0, Math.Max(1, _lightCount) * 4);
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

        private IEnumerator RenderLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (_proxies?.Rtas == null || !_viewEnabled || _ptShader == null) continue;
                if (_proxies.Rtas.GetInstanceCount() == 0) continue; // dispatching against an empty RTAS hangs the GPU
                if (_camera == null || !_camera.isActiveAndEnabled)
                    _camera = Camera.allCameras.FirstOrDefault(c => c.name == "Main Camera") ?? Camera.main;
                if (_camera == null) continue;

                try { Trace(); }
                catch (Exception e)
                {
                    Logger.LogError($"Trace failed, disabling view: {e}");
                    _viewEnabled = false;
                }
            }
        }

        private void EnsureTargets(int w, int h)
        {
            if (_output != null && _output.width == w && _output.height == h) return;
            if (_output != null) _output.Release();
            if (_accum != null) _accum.Release();
            _output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT Output" };
            _output.Create();
            _accum = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { enableRandomWrite = true, filterMode = FilterMode.Point, name = "UltraRT Accum" };
            _accum.Create();
            _accumFrames = 0;
            EnsureGBuffer(ref _gRadiance, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Radiance");
            EnsureGBuffer(ref _gAlbedo, w, h, RenderTextureFormat.ARGB32, "UltraRT Albedo");
            EnsureGBuffer(ref _gEmission, w, h, RenderTextureFormat.ARGBHalf, "UltraRT Emission");
            EnsureGBuffer(ref _gNormalDepth, w, h, RenderTextureFormat.ARGBHalf, "UltraRT NormalDepth");
            EnsureGBuffer(ref _gInstanceId, w, h, RenderTextureFormat.RInt, "UltraRT InstanceId");
        }

        private RenderTexture _gRadiance, _gAlbedo, _gEmission, _gNormalDepth, _gInstanceId;
        private float _nextRescan;
        private double _cpuMs;
        private int _cpuFrames;
        private bool _spawnTest;

        private void Trace()
        {
            var (_, kind, mode) = Modes[_mode];
            bool denoised = kind == Kind.Denoised;
            bool pt = kind != Kind.Debug;
            if (denoised && _lastKind != Kind.Denoised) _denoiser.Reset(); // history is stale after other modes
            _lastKind = kind;
            if (denoised) _accumulate = false; // the denoiser needs unjittered primary rays
            int w = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelWidth * (pt ? _scale : 1f)));
            int h = Mathf.Max(1, Mathf.RoundToInt(_camera.pixelHeight * (pt ? _scale : 1f)));
            EnsureTargets(w, h);

            var cpu = Stopwatch.StartNew();
            if (Patches.PendingHierarchies.Count > 0)
            {
                foreach (var go in Patches.PendingHierarchies) _proxies.AddHierarchy(go);
                Patches.PendingHierarchies.Clear();
            }
            if (Time.realtimeSinceStartup >= _nextRescan)
            {
                // Safety net for renderers that don't come through a hook (e.g. Cyber Grind's runtime combined mesh).
                _nextRescan = Time.realtimeSinceStartup + 30f;
                var sw = Stopwatch.StartNew();
                _proxies.Rescan();
                Logger.LogInfo($"Rescan took {sw.Elapsed.TotalMilliseconds:F2} ms");
            }
            _proxies.SyncDynamic();
            _cpuMs += cpu.Elapsed.TotalMilliseconds;
            _cpuFrames++;
            if (_cpuFrames >= 600)
            {
                Logger.LogInfo($"RT scene sync CPU avg {_cpuMs / _cpuFrames:F3} ms/frame");
                _cpuMs = 0; _cpuFrames = 0;
            }

            var viewProj = _camera.projectionMatrix * _camera.worldToCameraMatrix;
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
            _cmd.SetRayTracingIntParam(shader, "_Mode", denoised ? 0 : mode);

            if (pt)
            {
                GatherLights();
                var ambient = RenderSettings.ambientLight;
                _cmd.SetRayTracingTextureParam(shader, "_Accum", _accum);
                _cmd.SetRayTracingBufferParam(shader, "_Lights", _lightBuffer);
                _cmd.SetRayTracingIntParam(shader, "_LightCount", _lightCount);
                _cmd.SetRayTracingIntParam(shader, "_FrameIndex", (int)_frameIndex);
                _cmd.SetRayTracingIntParam(shader, "_AccumFrames", (int)_accumFrames);
                _cmd.SetRayTracingVectorParam(shader, "_SkyColor", new Vector4(ambient.r, ambient.g, ambient.b, 1));
                // The game's lit term is 2 * ambient + lights.
                _cmd.SetRayTracingVectorParam(shader, "_Ambient", new Vector4(ambient.r, ambient.g, ambient.b, 1) * 2f);
                _cmd.SetRayTracingFloatParam(shader, "_PrimaryAmbient", _primaryAmbient);
                _cmd.SetRayTracingFloatParam(shader, "_BounceAmbient", 1f);
                _cmd.SetRayTracingFloatParam(shader, "_LightScale", 1f);
                _cmd.SetRayTracingFloatParam(shader, "_EmissionScale", 1f);
                _cmd.SetRayTracingTextureParam(shader, "_Radiance", _gRadiance);
                _cmd.SetRayTracingTextureParam(shader, "_AlbedoOut", _gAlbedo);
                _cmd.SetRayTracingTextureParam(shader, "_EmissionOut", _gEmission);
                _cmd.SetRayTracingTextureParam(shader, "_NormalDepth", _gNormalDepth);
                _cmd.SetRayTracingTextureParam(shader, "_InstanceId", _gInstanceId);
            }
            else
            {
                _cmd.SetRayTracingFloatParam(shader, "_DepthScale", 150f);
            }

            _cmd.BeginSample(SampleTrace);
            _gpu?.Begin(_cmd, SlotDispatch);
            _cmd.DispatchRays(shader, "RayGen", (uint)w, (uint)h, 1);
            _gpu?.End(_cmd, SlotDispatch);
            _cmd.EndSample(SampleTrace);

            _displayed = _output;
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
                    normalDepth = _gNormalDepth, instanceId = _gInstanceId,
                    viewProj = viewProj, invViewProj = invViewProj,
                    camPos = _camera.transform.position,
                };
                _cmd.BeginSample(SampleDenoise);
                _gpu?.Begin(_cmd, SlotDenoise);
                _denoiser.Execute(_cmd, inputs, _denoised);
                _gpu?.End(_cmd, SlotDenoise);
                _cmd.EndSample(SampleDenoise);
                _displayed = _denoised;
            }

            _gpu?.Begin(_cmd, SlotBlit);
            Graphics.ExecuteCommandBuffer(_cmd);
            Graphics.Blit(_displayed, (RenderTexture)null);
            if (_gpu != null)
            {
                // Blit isn't part of _cmd, so the bracket closes in a second buffer queued after it.
                _cmdPost.Clear();
                _gpu.End(_cmdPost, SlotBlit);
                _gpu.End(_cmdPost, SlotTotal);
                _gpu.EndFrame(_cmdPost);
                Graphics.ExecuteCommandBuffer(_cmdPost);
                SampleGpuTimings();
            }

            _frameIndex++;
            if (pt && _accumulate) _accumFrames++;
            if (_tracedFrames++ == 0) Logger.LogInfo($"First trace OK: {w}x{h}, instances={_proxies.Rtas.GetInstanceCount()}, lights={_lightCount}");
        }

        // Debug markers (visible in PIX/RenderDoc). Unity's Recorder reports 0 GPU time in this release player;
        // real GPU timings come from the native timer plugin.
        private const string SampleBuild = "UltraRT.BuildRTAS", SampleTrace = "UltraRT.DispatchRays", SampleDenoise = "UltraRT.Denoise";

        // ---------------------------------------------------------------- GPU timing (opt-in, -ultrart-gputimer)

        private const int SlotTotal = 0, SlotRtas = 1, SlotDispatch = 2, SlotDenoise = 3, SlotBlit = 4, SlotCount = 5;
        private static readonly string[] SlotNames = { "total", "rtas", "dispatch", "denoise", "blit" };
        private bool _useGpuTimer;
        private GpuTimer _gpu;
        private CommandBuffer _cmdPost;
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
            while (SafeCurrentScene() != "Main Menu" || SceneHelper.PendingScene != null)
            {
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

                for (int m = 0; m < Modes.Length; m++)
                {
                    _mode = m;
                    _accumulate = false;
                    yield return null; yield return null;
                    if (Modes[m].kind == Kind.Denoised)
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
        private void Update() => OnUpdate?.Invoke();
    }
}
