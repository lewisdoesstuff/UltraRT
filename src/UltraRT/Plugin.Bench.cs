using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using UltraRT.Bench;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltraRT
{
    /// <summary>
    /// Benchmark runner: -ultrart-bench &lt;suite.json&gt; [-ultrart-bench-out dir] [-ultrart-bench-tag text] [-ultrart-quit].
    /// See bench/README.md. Saves, prefs and Steam stats are blocked for the whole run (BenchSafety).
    /// </summary>
    public partial class Plugin
    {
        private string _benchSuitePath, _benchOut, _benchTag;
        private Recorder _recorder;

        private void ParseBenchArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-bench": _benchSuitePath = a[i + 1]; break;
                    case "-ultrart-bench-out": _benchOut = a[i + 1]; break;
                    case "-ultrart-bench-tag": _benchTag = a[i + 1]; break;
                }
            }
        }

        /// <summary>Called from Awake. Returns true if a benchmark run was started.</summary>
        private bool StartBenchIfRequested()
        {
            ParseBenchArgs();
            _recorder = new Recorder(Path.Combine(_outDir, "recordings"), Logger);
            if (string.IsNullOrEmpty(_benchSuitePath)) return false;

            var harmony = new Harmony(Info.Metadata.GUID + ".bench");
            int patched = BenchSafety.Install(harmony, Logger);
            if (patched < 0) { Logger.LogError("Benchmark aborted: could not install save protection."); return false; }
            BenchSafety.Active = true;
            Logger.LogInfo($"Benchmark: save/prefs/stats writes blocked ({patched} hooks)");
            _host.StartCoroutine(RunSuite());
            return true;
        }

        private void UpdateRecorder()
        {
            if (BenchSafety.Active || _recorder == null) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F10))
                {
                    bool was = _recorder.IsRecording;
                    _recorder.Toggle();
                    if (was && !_recorder.IsRecording) Toast($"Recording saved ({_recorder.LastSavedName})");
                }
                if (Input.GetKeyDown(KeyCode.F11)) SavePose();
            }
            catch (InvalidOperationException) { }
            _recorder.Update();
        }

        /// <summary>F11: append the current player position + look to BepInEx/ultrart/poses.jsonl, ready to paste into a suite.</summary>
        private void SavePose()
        {
            if (GameControl.Player == null || GameControl.Cam == null) { Toast("No player in this scene"); return; }
            var p = GameControl.CurrentPose();
            string approach = _recorder?.SaveApproach();
            string areas = _recorder?.SaveAreas();
            var entry = new
            {
                Scene = SceneHelper.CurrentScene,
                Saved = DateTime.Now.ToString("s"),
                Approach = approach,
                Areas = areas,
                Position = new[] { Round(p.Position.x), Round(p.Position.y), Round(p.Position.z) },
                Yaw = Round(p.Yaw),
                Pitch = Round(p.Pitch),
            };
            string file = Path.Combine(_outDir, "poses.jsonl");
            File.AppendAllText(file, JsonConvert.SerializeObject(entry) + Environment.NewLine);
            Logger.LogInfo($"Pose saved: {JsonConvert.SerializeObject(entry)}");
            Toast($"Pose saved: {entry.Scene} ({entry.Position[0]}, {entry.Position[1]}, {entry.Position[2]}){(areas != null ? " + area state" : "")}{(approach != null ? " + approach" : "")}");

            float Round(float v) => (float)Math.Round(v, 2);
        }

        // ---------------------------------------------------------------- on-screen feedback

        private string _toast;
        private float _toastUntil;
        private GUIStyle _hudStyle;

        private void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.realtimeSinceStartup + 3f;
        }

        private void DrawHud()
        {
            if (BenchSafety.Active) return;
            if (_hudStyle == null)
                _hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            if (_recorder != null && _recorder.IsRecording)
            {
                _hudStyle.normal.textColor = Color.red;
                GUI.Label(new Rect(20, 20, 600, 40), $"● REC {_recorder.Elapsed:F1}s  (F10 to stop)", _hudStyle);
            }
            if (_toast != null && Time.realtimeSinceStartup < _toastUntil)
            {
                _hudStyle.normal.textColor = Color.white;
                GUI.Label(new Rect(20, 60, 1200, 40), _toast, _hudStyle);
            }
        }

        // ---------------------------------------------------------------- suite

        private IEnumerator RunSuite()
        {
            Suite suite;
            try { suite = JsonConvert.DeserializeObject<Suite>(File.ReadAllText(_benchSuitePath), new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace }); }
            catch (Exception e) { Logger.LogError($"Benchmark: cannot read suite {_benchSuitePath}: {e.Message}"); yield break; }
            string suiteDir = Path.GetDirectoryName(Path.GetFullPath(_benchSuitePath));

            string outDir = _benchOut ?? Path.Combine(_outDir, "bench", $"{DateTime.Now:yyyyMMdd-HHmmss}_{suite.Name}");
            Directory.CreateDirectory(outDir);

            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            var result = new SuiteResult
            {
                Suite = suite.Name, Tag = _benchTag, Started = DateTime.Now.ToString("s"),
                Gpu = SystemInfo.graphicsDeviceName, Api = SystemInfo.graphicsDeviceVersion,
                TraceScale = _scale, GpuTimer = _gpu != null,
            };

            float t0 = Time.realtimeSinceStartup;
            while (SafeCurrentScene() != "Main Menu" || SceneHelper.PendingScene != null)
            {
                if (Time.realtimeSinceStartup - t0 > 120f) { Logger.LogError("Benchmark: timed out waiting for main menu"); yield break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);

            foreach (var sc in suite.Scenarios)
            {
                var sr = new ScenarioResult { Name = sc.Name, Scene = sc.Scene };
                result.Scenarios.Add(sr);
                Logger.LogInfo($"Benchmark: scenario '{sc.Name}' ({sc.Scene})");
                IEnumerator run = RunScenario(sc, sr, suiteDir, outDir);
                while (true)
                {
                    object current;
                    try
                    {
                        if (!run.MoveNext()) break;
                        current = run.Current;
                    }
                    catch (Exception e)
                    {
                        sr.Error = e.Message;
                        Logger.LogError($"Benchmark: scenario '{sc.Name}' failed: {e}");
                        break;
                    }
                    yield return current;
                }
                GameControl.ReleaseControl();
                File.WriteAllText(Path.Combine(outDir, "results.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            }

            result.Resolution = $"{Screen.width}x{Screen.height}";
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            File.WriteAllText(Path.Combine(outDir, "summary.md"), Summarize(result));
            Logger.LogInfo($"Benchmark done: {outDir}\n{Summarize(result)}");

            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
            }
        }

        // ---------------------------------------------------------------- scenario

        private IEnumerator RunScenario(Scenario sc, ScenarioResult sr, string suiteDir, string outDir)
        {
            // Seed before the level's own Start()s run (sceneLoaded fires first).
            UnityEngine.Random.InitState(sc.Seed);
            void Seed(Scene s, LoadSceneMode m) { UnityEngine.Random.InitState(sc.Seed); SceneManager.sceneLoaded -= Seed; }
            SceneManager.sceneLoaded += Seed;

            ResetScene();
            SceneHelper.LoadScene(sc.Scene);
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (SceneHelper.PendingScene != null || _proxies == null || SafeCurrentScene() != sc.Scene)
            {
                if (Time.realtimeSinceStartup - t0 > 120f) throw new TimeoutException($"loading {sc.Scene}");
                yield return null;
            }
            yield return new WaitForSecondsRealtime(sc.Settle);

            GameControl.EnableCheats(blindEnemies: sc.Enemies == "blind", disableSpawns: sc.Enemies == "none");
            yield return null;

            var spawn = GameControl.CurrentPose();
            if (!string.IsNullOrEmpty(sc.Path.Areas))
            {
                // Re-apply what the area triggers had switched on/off when the pose was saved (no route needed).
                var areaFile = Path.IsPathRooted(sc.Path.Areas) ? sc.Path.Areas : Path.Combine(suiteDir, sc.Path.Areas);
                var (applied, missing) = AreaState.Load(areaFile).Apply();
                Logger.LogInfo($"Benchmark: area state applied ({applied} changes, {missing} not found)");
                yield return new WaitForSecondsRealtime(1f);
            }
            if (!string.IsNullOrEmpty(sc.Path.Approach))
            {
                // Re-walk the recorded route (not timed, RT off) so the game's area triggers fire in order.
                var approachRec = Recording.Load(Path.IsPathRooted(sc.Path.Approach) ? sc.Path.Approach : Path.Combine(suiteDir, sc.Path.Approach));
                var approach = approachRec.ToPath();
                ApplyBenchMode("off");
                GameControl.TakeControl(approach.Start);
                float speed = Mathf.Max(0.1f, sc.Path.ApproachSpeed);
                float at = 0f;
                Logger.LogInfo($"Benchmark: approach {approach.Duration:F1}s at {speed}x");
                while (at < approach.Duration)
                {
                    at += Time.unscaledDeltaTime * speed;
                    GameControl.SetPose(approach.Sample(at));
                    GameControl.HideCheatPanels();
                    yield return null;
                }
                GameControl.SetPose(approach.Sample(approach.Duration));
                spawn = approach.Sample(approach.Duration);
                yield return new WaitForSecondsRealtime(1.5f); // let area activation settle
            }
            var (path, arenas) = BuildPath(sc.Path, spawn, suiteDir);
            sr.Duration = path.Duration;
            GameControl.TakeControl(path.Start);
            yield return new WaitForSecondsRealtime(0.5f);

            var frames = sc.Modes.ToDictionary(m => m, _ => new List<float>(8192));
            var gpuSum = sc.Modes.ToDictionary(m => m, _ => new double[SlotCount]);
            var gpuN = sc.Modes.ToDictionary(m => m, _ => new int[SlotCount]);
            var shots = new Queue<float>(sc.Screenshots.OrderBy(x => x));

            for (int round = 0; round < Math.Max(1, sc.Rounds); round++)
            {
                int nextArena = 0;
                string current = null;
                int sinceSwitch = 0;
                float pathT = 0f;
                long lastResolved = _gpu?.ResolvedFrames ?? 0;

                while (pathT <= path.Duration)
                {
                    // Screenshots: hold the pose and grab every mode.
                    if (round == 0 && shots.Count > 0 && pathT >= shots.Peek())
                    {
                        float at = shots.Dequeue();
                        var activeLights = FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.intensity > 0).ToList();
                        Logger.LogInfo($"Benchmark: '{sc.Name}' @ {at:F1}s ambient={RenderSettings.ambientLight} lights: " +
                                       string.Join(", ", activeLights.GroupBy(l => l.type).Select(g => $"{g.Key}={g.Count()}")) +
                                       string.Concat(activeLights.Where(l => l.type == LightType.Directional)
                                           .Select(l => $" | sun '{l.name}' I={l.intensity:F2} mask=0x{l.cullingMask:X8}")));
                        var camPos = GameControl.Cam != null ? GameControl.Cam.transform.position : Vector3.zero;
                        foreach (var r in FindObjectsOfType<Renderer>())
                        {
                            var m = r.sharedMaterial;
                            if (!r.enabled || m == null || m.shader == null) continue;
                            var texProps = m.GetTexturePropertyNames().Select(n => (n, t: m.GetTexture(n))).Where(x => x.t != null).ToList();
                            bool usesRT = texProps.Any(x => x.t is RenderTexture);
                            bool skyShader = m.shader.name.IndexOf("Sky", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool bigNear = r.bounds.size.magnitude > 150f && r.bounds.SqrDistance(camPos) < 1f; // camera inside its bounds
                            if (!usesRT && !skyShader && !bigNear) continue;
                            Logger.LogInfo($"  sky-ish renderer '{GameControl.PathOf(r.transform)}' size={r.bounds.size.magnitude:F0} layer={r.gameObject.layer} " +
                                           $"shader='{m.shader.name}' mat='{m.name}' queue={m.renderQueue} kw=[{string.Join(" ", m.shaderKeywords)}] " +
                                           $"tex=[{string.Join(", ", texProps.Select(x => $"{x.n}={x.t.name}{(x.t is RenderTexture ? "(RT)" : "")}"))}] " +
                                           $"why={(usesRT ? "RT " : "")}{(skyShader ? "skyshader " : "")}{(bigNear ? "encloses-camera" : "")}");
                        }
                        foreach (var m in sc.Modes)
                        {
                            ApplyBenchMode(m);
                            for (int f = 0; f < 12; f++) { GameControl.HideCheatPanels(); yield return null; }
                            yield return new WaitForEndOfFrame();
                            // What the player sees (RT composited under transparents/post/HUD), plus the raw RT image.
                            string file = $"{Sanitize(sc.Name)}_{at:F1}s_{Sanitize(m)}.png";
                            ScreenCapture.CaptureScreenshot(Path.Combine(outDir, file));
                            sr.Screenshots.Add(file);
                            if (m != "off")
                            {
                                string raw = $"{Sanitize(sc.Name)}_{at:F1}s_{Sanitize(m)}_raw.png";
                                SaveOutput(Path.Combine(outDir, raw));
                                sr.Screenshots.Add(raw);
                            }
                            yield return null; // CaptureScreenshot completes at the end of this frame
                        }
                        current = null; // force a fresh switch below
                    }

                    var mode = sc.Modes[(int)(pathT / Mathf.Max(sc.Segment, 0.05f)) % sc.Modes.Count];
                    if (mode != current) { ApplyBenchMode(mode); current = mode; sinceSwitch = 0; }

                    if (sc.ReplayArenas && round == 0)
                        while (nextArena < arenas.Count && arenas[nextArena].Time <= pathT) TriggerArena(arenas[nextArena++]);

                    // The game re-applies its frame limiter on level load; keep the run uncapped.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    GameControl.SetPose(path.Sample(pathT));
                    GameControl.HideCheatPanels();
                    yield return null;

                    float dt = Time.unscaledDeltaTime;
                    pathT += dt;
                    if (++sinceSwitch > sc.SwitchSkipFrames)
                    {
                        frames[current].Add(dt * 1000f);
                        if (_gpu != null && current != "off" && _gpu.ResolvedFrames != lastResolved)
                        {
                            lastResolved = _gpu.ResolvedFrames;
                            for (int s = 0; s < SlotCount; s++)
                                if (_gpu.TryGetMs(s, out var ms) && ms > 0f) { gpuSum[current][s] += ms; gpuN[current][s]++; }
                        }
                    }
                }
            }

            foreach (var m in sc.Modes)
            {
                var st = Stats(frames[m]);
                for (int s = 0; s < SlotCount; s++)
                    if (gpuN[m][s] > 0) st.GpuMs[SlotNames[s]] = (float)(gpuSum[m][s] / gpuN[m][s]);
                sr.Modes[m] = st;
            }
            sr.Instances = (int)(_proxies?.Rtas?.GetInstanceCount() ?? 0);
            sr.Lights = _lightCount;
            ApplyBenchMode("off");
            GameControl.ReleaseControl();
        }

        private (CameraPath path, List<Recording.ArenaEvent> arenas) BuildPath(PathSpec p, CamPose spawn, string suiteDir)
        {
            var origin = p.Position != null && p.Position.Length == 3
                ? new CamPose(new Vector3(p.Position[0], p.Position[1], p.Position[2]), p.Yaw, p.Pitch)
                : spawn;
            switch ((p.Type ?? "spawn").ToLowerInvariant())
            {
                case "spawn": return (CameraPath.Static(spawn, p.Duration), new List<Recording.ArenaEvent>());
                case "static": return (CameraPath.Static(origin, p.Duration), new List<Recording.ArenaEvent>());
                case "sweep": return (CameraPath.Sweep(origin, p.Duration, p.YawSpeed, p.PitchAmplitude), new List<Recording.ArenaEvent>());
                case "recording":
                {
                    var rec = Recording.Load(Path.IsPathRooted(p.File) ? p.File : Path.Combine(suiteDir, p.File));
                    float end = p.End > 0 ? p.End : float.MaxValue;
                    var arenas = rec.Arenas.Where(a => a.Time >= p.Start && a.Time <= end)
                        .Select(a => new Recording.ArenaEvent { Time = a.Time - p.Start, Path = a.Path, Position = a.Position })
                        .OrderBy(a => a.Time).ToList();
                    return (rec.ToPath(p.Start, end), arenas);
                }
                default: throw new ArgumentException($"unknown path type '{p.Type}'");
            }
        }

        private void TriggerArena(Recording.ArenaEvent e)
        {
            var all = FindObjectsOfType<ActivateArena>(true);
            var arena = all.FirstOrDefault(a => GameControl.PathOf(a.transform) == e.Path);
            if (arena == null && e.Position != null)
            {
                var p = new Vector3(e.Position[0], e.Position[1], e.Position[2]);
                arena = all.OrderBy(a => (a.transform.position - p).sqrMagnitude).FirstOrDefault();
            }
            if (arena == null) { Logger.LogWarning($"Benchmark: arena '{e.Path}' not found"); return; }
            if (!arena.activated) arena.Activate();
        }

        private void ApplyBenchMode(string mode)
        {
            if (mode == "off") { _viewEnabled = false; return; }
            int i = Array.FindIndex(Modes, m => m.name == mode);
            if (i < 0) throw new ArgumentException($"unknown mode '{mode}'");
            _mode = i;
            _viewEnabled = true;
        }

        private static ModeStats Stats(List<float> ms)
        {
            var st = new ModeStats { Frames = ms.Count };
            if (ms.Count == 0) return st;
            var s = ms.OrderBy(x => x).ToList();
            float P(double q) => s[Math.Min(s.Count - 1, (int)(s.Count * q))];
            st.AvgMs = ms.Average();
            st.P50Ms = P(0.5); st.P95Ms = P(0.95); st.P99Ms = P(0.99); st.MaxMs = s[s.Count - 1];
            st.AvgFps = 1000f / st.AvgMs;
            st.Low1Fps = 1000f / s.Skip((int)(s.Count * 0.99)).DefaultIfEmpty(s[s.Count - 1]).Average();
            return st;
        }

        private static string Summarize(SuiteResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# UltraRT benchmark: {r.Suite}{(string.IsNullOrEmpty(r.Tag) ? "" : $" ({r.Tag})")}");
            sb.AppendLine($"{r.Started} | {r.Gpu} | {r.Resolution} | trace scale {r.TraceScale}{(r.GpuTimer ? " | GPU timer" : "")}");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Mode | Avg ms | fps | 1% low | p99 ms | delta vs off | GPU ms (total/dispatch/denoise) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var sc in r.Scenarios)
            {
                if (sc.Error != null) { sb.AppendLine($"| {sc.Name} | **failed** | {sc.Error} |||||| "); continue; }
                sc.Modes.TryGetValue("off", out var off);
                foreach (var kv in sc.Modes)
                {
                    var m = kv.Value;
                    string delta = off != null && kv.Key != "off" ? (m.AvgMs - off.AvgMs).ToString("+0.000;-0.000") : "";
                    string gpu = m.GpuMs.Count == 0 ? "" :
                        $"{Get(m.GpuMs, "total")}/{Get(m.GpuMs, "dispatch")}/{Get(m.GpuMs, "denoise")}";
                    sb.AppendLine($"| {sc.Name} | {kv.Key} | {m.AvgMs:F3} | {m.AvgFps:F0} | {m.Low1Fps:F0} | {m.P99Ms:F3} | {delta} | {gpu} |");
                }
            }
            return sb.ToString();

            string Get(Dictionary<string, float> d, string k) => d.TryGetValue(k, out var v) ? v.ToString("F3") : "-";
        }

        private static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
    }
}
