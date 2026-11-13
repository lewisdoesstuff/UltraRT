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
using Debug = UnityEngine.Debug;

namespace UltraRT.Probe
{
    /// <summary>
    /// Feasibility probe for a ray/path tracing mod. Driven by command-line args so it can run unattended:
    ///   -ultrart-scene "Level 1-1"   load this level once the main menu is up
    ///   -ultrart-settle 6            seconds to wait after the level loads before measuring
    ///   -ultrart-bench 10            seconds per benchmark pass
    ///   -ultrart-quit                quit the game when the report is written
    /// Without -ultrart-scene it only logs device capabilities.
    /// Reports land in BepInEx/ultrart/.
    /// </summary>
    [BepInPlugin("ooo.lew.ultrart.probe", "UltraRT Probe", "0.1.0")]
    public class ProbePlugin : BaseUnityPlugin
    {
        private string _scene;
        private float _settle = 6f;
        private float _bench = 10f;
        private bool _quit;
        private string _outDir;
        private readonly StringBuilder _report = new StringBuilder();

        private void Awake()
        {
            ParseArgs();
            _outDir = Path.Combine(Paths.BepInExRootPath, "ultrart");
            Directory.CreateDirectory(_outDir);

            Section("Device");
            Line($"Unity:            {Application.unityVersion}");
            Line($"Graphics API:     {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceVersion})");
            Line($"GPU:              {SystemInfo.graphicsDeviceName} / {SystemInfo.graphicsMemorySize} MB");
            Line($"supportsRayTracing:     {SystemInfo.supportsRayTracing}");
            Line($"supportsComputeShaders: {SystemInfo.supportsComputeShaders}");
            Line($"supportsAsyncCompute:   {SystemInfo.supportsAsyncCompute}");
            Line($"maxComputeBufferInputsCompute: {SystemInfo.maxComputeBufferInputsCompute}");
            Line($"FrameTimingManager enabled: {FrameTimingManager.IsFeatureEnabled()}");
            Line($"Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F1} Hz, mode {Screen.fullScreenMode}");
            Line($"vSyncCount={QualitySettings.vSyncCount} targetFrameRate={Application.targetFrameRate} quality='{QualitySettings.names[QualitySettings.GetQualityLevel()]}'");
            Line($"Args: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
            Logger.LogInfo("\n" + _report);

            SceneManager.activeSceneChanged += (from, to) =>
                Logger.LogInfo($"activeSceneChanged '{from.name}' -> '{to.name}' (SceneHelper.CurrentScene='{SafeCurrentScene()}')");

            // The game can tear down BepInEx's manager object, which kills coroutines started on the plugin.
            // Run on our own hidden, persistent object instead.
            var host = new GameObject("UltraRT.ProbeHost") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            _host = host.AddComponent<CoroutineHost>();

            if (!string.IsNullOrEmpty(_scene))
                _host.StartCoroutine(Run());
            else
                Flush("device");
        }

        private CoroutineHost _host;

        private static string SafeCurrentScene()
        {
            try { return SceneHelper.CurrentScene; } catch { return "?"; }
        }

        private void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : null;
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-scene": _scene = Next(); break;
                    case "-ultrart-settle": float.TryParse(Next(), out _settle); break;
                    case "-ultrart-bench": float.TryParse(Next(), out _bench); break;
                    case "-ultrart-quit": _quit = true; break;
                }
            }
        }

        private IEnumerator Run()
        {
            // Wait for the main menu to finish booting.
            float t0 = Time.realtimeSinceStartup, nextBeat = 0f;
            while (SceneHelper.CurrentScene != "Main Menu" || SceneHelper.PendingScene != null)
            {
                float el = Time.realtimeSinceStartup - t0;
                if (el > nextBeat)
                {
                    Logger.LogInfo($"waiting for main menu: t={el:F0}s CurrentScene='{SafeCurrentScene()}' active='{SceneManager.GetActiveScene().name}'");
                    nextBeat += 10f;
                }
                if (el > 120f) { Logger.LogError("Timed out waiting for main menu"); yield break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(3f);

            Logger.LogInfo($"Loading {_scene}");
            var loadSw = Stopwatch.StartNew();
            SceneHelper.LoadScene(_scene);
            yield return null;
            while (SceneHelper.PendingScene != null) yield return null;
            loadSw.Stop();
            yield return new WaitForSecondsRealtime(_settle);

            Section("Scene");
            Line($"Requested: {_scene}  active: {SceneManager.GetActiveScene().name}  load: {loadSw.Elapsed.TotalSeconds:F1}s");

            // Uncap so we measure what the GPU/CPU can actually do.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return null;

            string api = SystemInfo.graphicsDeviceType.ToString();
            string tag = $"{Sanitize(_scene)}-{api}";
            ScreenCapture.CaptureScreenshot(Path.Combine(_outDir, $"{tag}.png"));

            yield return Bench("Baseline (uncapped, player idle at spawn)", null);

            DumpScene();

            if (SystemInfo.supportsRayTracing)
            {
                RayTracingAccelerationStructure rtas = null;
                try { rtas = BuildRtas(); }
                catch (Exception e) { Line($"RTAS build threw: {e}"); }

                if (rtas != null)
                {
                    yield return Bench("With RTAS.Build() every frame (TLAS refit + skinned BLAS)", () => rtas.Build());
                    rtas.Dispose();
                }
            }
            else
            {
                Section("Ray tracing");
                Line("supportsRayTracing = false, skipping RTAS test.");
            }

            Flush(tag);
            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
            }
        }

        // ---------------------------------------------------------------- benchmark

        private IEnumerator Bench(string label, Action perFrame)
        {
            var dts = new List<float>(4096);
            var cpu = new List<double>(4096);
            var gpu = new List<double>(4096);
            var timings = new FrameTiming[1];
            bool ftm = FrameTimingManager.IsFeatureEnabled();
            var perFrameSw = new Stopwatch();
            double perFrameMs = 0;

            float end = Time.realtimeSinceStartup + _bench;
            yield return null;
            while (Time.realtimeSinceStartup < end)
            {
                if (perFrame != null)
                {
                    perFrameSw.Restart();
                    perFrame();
                    perFrameSw.Stop();
                    perFrameMs += perFrameSw.Elapsed.TotalMilliseconds;
                }
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    cpu.Add(timings[0].cpuFrameTime);
                    gpu.Add(timings[0].gpuFrameTime);
                }
                yield return null;
                dts.Add(Time.unscaledDeltaTime * 1000f);
            }

            Section($"Bench: {label}");
            Line($"frames={dts.Count} over {_bench:F0}s");
            Line(Stats("frame ms", dts.Select(x => (double)x).ToList()));
            if (dts.Count > 0)
            {
                var sorted = dts.OrderBy(x => x).ToList();
                double avgFps = 1000.0 / dts.Average();
                double low1 = 1000.0 / sorted[(int)(sorted.Count * 0.99)];
                Line($"avg fps={avgFps:F0}  1%-low fps={low1:F0}");
            }
            if (ftm && cpu.Count > 0)
            {
                Line(Stats("FrameTiming cpuFrameTime ms", cpu));
                Line(Stats("FrameTiming gpuFrameTime ms", gpu));
            }
            else Line("FrameTimingManager: no data (Frame Timing Stats not enabled in this build)");
            if (perFrame != null && dts.Count > 0)
                Line($"per-frame hook CPU cost: {perFrameMs / dts.Count:F3} ms");
            Logger.LogInfo($"Bench done: {label}");
        }

        private static string Stats(string name, List<double> v)
        {
            if (v.Count == 0) return $"{name}: n/a";
            var s = v.OrderBy(x => x).ToList();
            double P(double p) => s[Math.Min(s.Count - 1, (int)(s.Count * p))];
            return $"{name}: avg {v.Average():F3}  p50 {P(0.5):F3}  p95 {P(0.95):F3}  p99 {P(0.99):F3}  max {s[s.Count - 1]:F3}";
        }

        // ---------------------------------------------------------------- scene dump

        private void DumpScene()
        {
            var all = Resources.FindObjectsOfTypeAll<Renderer>()
                .Where(r => r.gameObject.scene.IsValid()) // skip prefabs/assets
                .ToList();
            var active = all.Where(r => r.enabled && r.gameObject.activeInHierarchy).ToList();

            Section("Renderers");
            Line($"total in scenes={all.Count}  active+enabled={active.Count}");
            foreach (var g in active.GroupBy(r => r.GetType().Name).OrderByDescending(g => g.Count()))
                Line($"  {g.Key,-28} {g.Count(),6}");

            // Geometry
            long staticTris = 0, skinnedTris = 0, staticVerts = 0;
            int nonReadable = 0, staticBatched = 0, missingMesh = 0;
            var uniqueMeshes = new HashSet<Mesh>();
            foreach (var r in active)
            {
                Mesh m = null;
                if (r is MeshRenderer mr)
                {
                    m = mr.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mr.isPartOfStaticBatch) staticBatched++;
                    if (m == null) { missingMesh++; continue; }
                    staticTris += Tris(m);
                    staticVerts += m.vertexCount;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    m = smr.sharedMesh;
                    if (m == null) { missingMesh++; continue; }
                    skinnedTris += Tris(m);
                }
                if (m != null)
                {
                    uniqueMeshes.Add(m);
                    if (!m.isReadable) nonReadable++;
                }
            }
            Section("Geometry (active Mesh/SkinnedMesh renderers)");
            // Static-batched renderers all point at the same combined mesh, so per-renderer sums overcount.
            Line($"unique-mesh tris={uniqueMeshes.Sum(Tris):N0} (what actually needs BLASes)");
            Line($"per-renderer sum: MeshRenderer tris={staticTris:N0} verts={staticVerts:N0} (overcounts static batches)");
            Line($"SkinnedMeshRenderer tris={skinnedTris:N0}");
            Line($"unique meshes={uniqueMeshes.Count}  non-readable instances={nonReadable}  static-batched renderers={staticBatched}  missing mesh={missingMesh}");
            var biggest = uniqueMeshes.OrderByDescending(Tris).Take(8);
            foreach (var m in biggest) Line($"  big mesh: '{m.name}' tris={Tris(m):N0} submeshes={m.subMeshCount} readable={m.isReadable}");

            // Materials / shaders
            var mats = active.SelectMany(r => r.sharedMaterials).Where(m => m != null).ToList();
            var uniqueMats = new HashSet<Material>(mats);
            Section("Shaders (by unique materials / material slots)");
            Line($"unique materials={uniqueMats.Count} slots={mats.Count}");
            var byShader = uniqueMats.GroupBy(m => m.shader).OrderByDescending(g => g.Count()).ToList();
            foreach (var g in byShader)
                Line($"  {g.Count(),5} mats  {mats.Count(m => m.shader == g.Key),6} slots  '{g.Key.name}'  queue={g.Key.renderQueue}");

            Section("Shader properties (top 12 shaders)");
            foreach (var g in byShader.Take(12))
            {
                var sh = g.Key;
                var props = new List<string>();
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                    props.Add($"{sh.GetPropertyName(i)}:{sh.GetPropertyType(i)}");
                var kws = g.SelectMany(m => m.shaderKeywords).Distinct().OrderBy(k => k);
                Line($"'{sh.name}'");
                Line($"   props: {string.Join(", ", props)}");
                Line($"   keywords seen: {string.Join(" ", kws)}");
                var sample = g.First();
                if (sample.HasProperty("_MainTex") && sample.mainTexture is Texture t)
                    Line($"   sample _MainTex: {t.name} {t.width}x{t.height} filter={t.filterMode}");
            }

            var emissive = uniqueMats.Where(m =>
                m.shaderKeywords.Any(k => k.IndexOf("EMISS", StringComparison.OrdinalIgnoreCase) >= 0) ||
                HasAnyProp(m.shader, "emiss", "glow")).ToList();
            Section("Emissive-looking materials");
            Line($"count={emissive.Count}");
            foreach (var m in emissive.Take(15)) Line($"  '{m.name}' shader='{m.shader.name}'");

            // Lights
            var lights = Resources.FindObjectsOfTypeAll<Light>().Where(l => l.gameObject.scene.IsValid()).ToList();
            var activeLights = lights.Where(l => l.enabled && l.gameObject.activeInHierarchy).ToList();
            Section("Lights");
            Line($"total={lights.Count} active={activeLights.Count}");
            foreach (var g in activeLights.GroupBy(l => $"{l.type} bake={l.bakingOutput.lightmapBakeType} shadows={l.shadows} mode={l.renderMode}"))
                Line($"  {g.Count(),5}  {g.Key}  avgRange={g.Average(l => l.range):F1} avgIntensity={g.Average(l => l.intensity):F2}");
            Line($"lightmaps={LightmapSettings.lightmaps.Length} ambientMode={RenderSettings.ambientMode} fog={RenderSettings.fog} ({RenderSettings.fogMode}) skybox={(RenderSettings.skybox ? RenderSettings.skybox.shader.name : "none")}");
            Line($"reflection probes active={FindObjectsOfType<ReflectionProbe>().Length}");

            // Cameras
            Section("Cameras");
            foreach (var cam in Camera.allCameras.OrderBy(c => c.depth))
            {
                Line($"'{cam.name}' depth={cam.depth} path={cam.actualRenderingPath} hdr={cam.allowHDR} msaa={cam.allowMSAA} clear={cam.clearFlags} fov={cam.fieldOfView:F0} near={cam.nearClipPlane} far={cam.farClipPlane} cullMask=0x{cam.cullingMask:X} depthMode={cam.depthTextureMode}");
                Line($"   target={(cam.targetTexture ? $"{cam.targetTexture.name} {cam.targetTexture.width}x{cam.targetTexture.height} {cam.targetTexture.format} filter={cam.targetTexture.filterMode}" : "screen")}");
                Line($"   components: {string.Join(", ", cam.GetComponents<Component>().Select(c => c.GetType().Name))}");
                foreach (CameraEvent ev in Enum.GetValues(typeof(CameraEvent)))
                {
                    var bufs = cam.GetCommandBuffers(ev);
                    if (bufs.Length > 0) Line($"   cmdbuf @{ev}: {string.Join(", ", bufs.Select(b => b.name))}");
                }
            }
            Logger.LogInfo("Scene dump done");
        }

        private static long Tris(Mesh m)
        {
            long n = 0;
            for (int i = 0; i < m.subMeshCount; i++)
                if (m.GetTopology(i) == MeshTopology.Triangles) n += m.GetIndexCount(i) / 3;
            return n;
        }

        private static bool HasAnyProp(Shader s, params string[] needles)
        {
            for (int i = 0; i < s.GetPropertyCount(); i++)
            {
                var n = s.GetPropertyName(i);
                if (needles.Any(x => n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- RTAS

        private RayTracingAccelerationStructure BuildRtas()
        {
            Section("Ray tracing acceleration structure");
            var settings = new RayTracingAccelerationStructure.RASSettings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, ~0);
            var rtas = new RayTracingAccelerationStructure(settings);

            var renderers = FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                .ToList();

            int ok = 0, failed = 0;
            var errors = new Dictionary<string, int>();
            var sw = Stopwatch.StartNew();
            foreach (var r in renderers)
            {
                try
                {
                    int subs = r.sharedMaterials.Length;
                    var flags = Enumerable.Repeat(RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly, Math.Max(1, subs)).ToArray();
                    rtas.AddInstance(r, flags, enableTriangleCulling: false);
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    var k = e.GetType().Name + ": " + e.Message;
                    errors[k] = errors.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            }
            double addMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            rtas.Build();
            double buildMs = sw.Elapsed.TotalMilliseconds;

            Line($"renderers offered={renderers.Count} added={ok} failed={failed}");
            foreach (var kv in errors) Line($"  {kv.Value}x {kv.Key}");
            Line($"instances={rtas.GetInstanceCount()} size={rtas.GetSize() / (1024.0 * 1024.0):F1} MB");
            Line($"AddInstance total CPU {addMs:F1} ms; first Build() CPU {buildMs:F1} ms (GPU work is async, see per-frame bench)");
            Logger.LogInfo($"RTAS: {ok} instances, {rtas.GetSize() / (1024.0 * 1024.0):F1} MB");
            return rtas;
        }

        // ---------------------------------------------------------------- output

        private void Section(string title) => _report.AppendLine().AppendLine($"## {title}");
        private void Line(string s) => _report.AppendLine(s);

        private void Flush(string tag)
        {
            var path = Path.Combine(_outDir, $"report-{tag}.md");
            File.WriteAllText(path, $"# UltraRT probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" + _report);
            Logger.LogInfo($"Report written: {path}");
        }

        private static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    internal sealed class CoroutineHost : MonoBehaviour { }
}
