using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UltraRT
{
    /// <summary>
    /// UltraRT: hardware ray tracing for ULTRAKILL (DX12 only: launch with -force-d3d12).
    /// Milestone 1 is a primary-ray debug view drawn over the game.
    ///   F7  toggle the debug view        F8  cycle the debug mode
    /// Unattended testing (see scripts/run-debugview.ps1):
    ///   -ultrart-scene "Level 1-1"  -ultrart-view N  -ultrart-capture  -ultrart-quit
    /// </summary>
    [BepInPlugin("ooo.lew.ultrart", "UltraRT", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static readonly string[] ModeNames = { "lit", "albedo", "normals", "depth", "instance", "uv", "emission", "primitive" };

        private RayTracingShader _debugShader;
        private Shader _proxyShader;
        private SceneProxies _proxies;
        private RenderTexture _output;
        private CommandBuffer _cmd;
        private Camera _camera;
        private CoroutineHost _host;

        private bool _viewEnabled = true;
        private int _mode;
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
            if (!LoadShaders()) return;

            _cmd = new CommandBuffer { name = "UltraRT" };
            var go = new GameObject("UltraRT.Host") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _host = go.AddComponent<CoroutineHost>();
            _host.OnUpdate = HandleInput;

            SceneManager.activeSceneChanged += (_, to) =>
            {
                Logger.LogInfo($"Scene -> {to.name} ({SafeCurrentScene()})");
                _proxies?.Dispose();
                _proxies = null;
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
                    case "-ultrart-capture": _capture = true; break;
                    case "-ultrart-quit": _quit = true; break;
                    case "-ultrart-off": _viewEnabled = false; break;
                    case "-ultrart-dumpverts": SceneProxies.DumpAtlasVertices = true; break;
                }
            }
        }

        private bool LoadShaders()
        {
            var path = Path.Combine(Path.GetDirectoryName(Info.Location) ?? "", "ultrart.shaders");
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) { Logger.LogError($"Could not load shader bundle at {path}"); return false; }
            _debugShader = bundle.LoadAllAssets<RayTracingShader>().FirstOrDefault();
            _proxyShader = bundle.LoadAllAssets<Shader>().FirstOrDefault(s => s.name == "UltraRT/Proxy");
            Logger.LogInfo($"Bundle loaded: raytrace={(_debugShader ? _debugShader.name : "MISSING")} proxy={(_proxyShader ? (_proxyShader.isSupported ? "ok" : "UNSUPPORTED") : "MISSING")}");
            return _debugShader != null && _proxyShader != null;
        }

        private IEnumerator BuildWhenLoaded()
        {
            yield return null;
            while (SceneHelper.PendingScene != null) yield return null;
            yield return new WaitForSecondsRealtime(1f);
            if (_proxies != null) yield break;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _proxies = new SceneProxies(Logger, _proxyShader);
                _proxies.Build();
                Logger.LogInfo($"Scene RT setup took {sw.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception e)
            {
                Logger.LogError($"Proxy build failed: {e}");
                _proxies?.Dispose();
                _proxies = null;
            }
        }

        private IEnumerator RenderLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (_proxies?.Rtas == null || !_viewEnabled || _proxies.Rtas.GetInstanceCount() == 0) continue;
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

        private void Trace()
        {
            int w = _camera.pixelWidth, h = _camera.pixelHeight;
            if (_output == null || _output.width != w || _output.height != h)
            {
                if (_output != null) _output.Release();
                _output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = true, name = "UltraRT Output" };
                _output.Create();
            }

            _proxies.SyncDynamic();

            var view = _camera.worldToCameraMatrix;
            var proj = _camera.projectionMatrix;
            var invViewProj = (proj * view).inverse;

            _cmd.Clear();
            _cmd.BuildRayTracingAccelerationStructure(_proxies.Rtas);
            _debugShader.SetShaderPass("UltraRTHit");
            _cmd.SetRayTracingAccelerationStructure(_debugShader, "_Scene", _proxies.Rtas);
            _cmd.SetRayTracingTextureParam(_debugShader, "_Output", _output);
            _cmd.SetRayTracingMatrixParam(_debugShader, "_InvViewProj", invViewProj);
            _cmd.SetRayTracingVectorParam(_debugShader, "_CamPos", _camera.transform.position);
            _cmd.SetRayTracingIntParam(_debugShader, "_Mode", _mode);
            _cmd.SetRayTracingFloatParam(_debugShader, "_DepthScale", 150f);
            _cmd.DispatchRays(_debugShader, "RayGen", (uint)w, (uint)h, 1);

            bool log = _tracedFrames < 3;
            if (log) Logger.LogInfo($"trace frame {_tracedFrames}: {w}x{h} instances={_proxies.Rtas.GetInstanceCount()} cam='{_camera.name}' executing");
            Graphics.ExecuteCommandBuffer(_cmd);
            if (log) Logger.LogInfo($"trace frame {_tracedFrames}: executed, blitting");
            Graphics.Blit(_output, (RenderTexture)null);
            if (log) Logger.LogInfo($"trace frame {_tracedFrames}: done");
            _tracedFrames++;
        }

        private int _tracedFrames;

        private void HandleInput()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F7)) { _viewEnabled = !_viewEnabled; Logger.LogInfo($"Debug view {(_viewEnabled ? "on" : "off")}"); }
                if (Input.GetKeyDown(KeyCode.F8)) { _mode = (_mode + 1) % ModeNames.Length; Logger.LogInfo($"Debug mode: {ModeNames[_mode]}"); }
            }
            catch (InvalidOperationException) { /* legacy input disabled */ }
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

            if (_capture)
            {
                string tag = new string(_autoScene.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                bool prev = _viewEnabled;
                _viewEnabled = false;
                yield return null; yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"dv-{tag}-game.png"));
                yield return null; yield return null;
                _viewEnabled = true;
                for (int m = 0; m < ModeNames.Length; m++)
                {
                    _mode = m;
                    yield return null; yield return null; yield return new WaitForEndOfFrame();
                    SaveOutput(Path.Combine(_outDir, $"dv-{tag}-{m}-{ModeNames[m]}.png"));
                }
                _viewEnabled = prev;
                Logger.LogInfo("Capture done");
            }
            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
            }
        }

        private void SaveOutput(string path)
        {
            if (_output == null) { Logger.LogWarning("No RT output to save"); return; }
            var prev = RenderTexture.active;
            RenderTexture.active = _output;
            var tex = new Texture2D(_output.width, _output.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, _output.width, _output.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            Logger.LogInfo($"Saved {path}");
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
