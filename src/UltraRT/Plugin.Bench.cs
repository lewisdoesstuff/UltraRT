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
    /// -ultrart-bench / -ultrart-bench-out may repeat: the suites run one after the other in this launch.
    /// -ultrart-bench-noshots / -ultrart-bench-noburst skip screenshots / noise bursts (perf runs),
    /// -ultrart-bench-settle S overrides every scenario's Settle, -ultrart-bench-server &lt;dir&gt; keeps the game open at the
    /// main menu and runs the suites that bench.ps1 -KeepOpen drops there as cmd-*.json files (see "ServeBenchCommands").
    /// See bench/README.md. Saves, prefs and Steam stats are blocked for the whole run (BenchSafety).
    /// </summary>
    public partial class Plugin
    {
        /// <summary>One suite of a launch (or of a server command).</summary>
        private sealed class BenchRun { public string Suite, Out; }
        private readonly List<BenchRun> _benchRuns = new List<BenchRun>();
        /// <summary>The suite being run now.</summary>
        private string _benchSuitePath, _benchOut;
        private string _benchTag;
        /// <summary>-ultrart-bench-noshots / -noburst: skip screenshots (and what is taken with them) / temporal noise bursts.</summary>
        private bool _benchNoShots, _benchNoBursts;
        /// <summary>-ultrart-bench-settle: seconds to settle after every level load, instead of each scenario's Settle (negative = not set).</summary>
        private float _benchSettle = -1f;
        /// <summary>-ultrart-bench-server: command folder of a resident game; -ultrart-bench-idle: minutes without a command before it quits.</summary>
        private string _benchServerDir;
        private float _benchIdleMin = 20f;
        /// <summary>-ultrart-bench-load: force one PathSpec.Load for every scenario.</summary>
        private string _benchLoad;
        /// <summary>-ultrart-bench-validate: compare the pose-load methods instead of measuring.</summary>
        private bool _benchValidate;
        private string[] _benchValidateMethods;
        /// <summary>-ultrart-bench-nochain: load the level for every scenario (the behaviour before chaining).</summary>
        private bool _benchNoChain;
        /// <summary>The loaded level as far as chaining knows it; null = the next scenario loads its level.</summary>
        private LevelState _level;
        private int _levelLoads;
        private Recorder _recorder;

        private void ParseBenchArgs()
        {
            var a = Environment.GetCommandLineArgs();
            _benchValidate = a.Any(x => x.Equals("-ultrart-bench-validate", StringComparison.OrdinalIgnoreCase));
            _benchNoChain = a.Any(x => x.Equals("-ultrart-bench-nochain", StringComparison.OrdinalIgnoreCase));
            _benchNoShots = a.Any(x => x.Equals("-ultrart-bench-noshots", StringComparison.OrdinalIgnoreCase));
            _benchNoBursts = a.Any(x => x.Equals("-ultrart-bench-noburst", StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-bench": _benchRuns.Add(new BenchRun { Suite = a[i + 1] }); break;
                    case "-ultrart-bench-out": if (_benchRuns.Count > 0) _benchRuns[_benchRuns.Count - 1].Out = a[i + 1]; break;
                    case "-ultrart-bench-tag": _benchTag = a[i + 1]; break;
                    case "-ultrart-bench-load": _benchLoad = a[i + 1]; break;
                    case "-ultrart-bench-validate-methods": _benchValidateMethods = a[i + 1].Split(','); break;
                    case "-ultrart-bench-settle": float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _benchSettle); break;
                    case "-ultrart-bench-server": _benchServerDir = a[i + 1]; break;
                    case "-ultrart-bench-idle": float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _benchIdleMin); break;
                }
            }
        }

        /// <summary>Called from Awake. Returns true if a benchmark run was started.</summary>
        private bool StartBenchIfRequested()
        {
            ParseBenchArgs();
            _recorder = new Recorder(Path.Combine(_outDir, "recordings"), Logger);
            if (_benchRuns.Count == 0 && string.IsNullOrEmpty(_benchServerDir)) return false;

            var harmony = new Harmony(Info.Metadata.GUID + ".bench");
            int patched = BenchSafety.Install(harmony, Logger);
            if (patched < 0) { Logger.LogError("Benchmark aborted: could not install save protection."); return false; }
            BenchSafety.Active = true;
            // Two games can run at once (alt install): one that loses focus while it boots must not freeze at the loading screen.
            Application.runInBackground = true;
            Perf.InstallFrameMarkers();
            PoseLoader.Log = Logger;
            EffectSpawner.Log = Logger;
            PoseLoader.InstallDeterminism(harmony);
            Logger.LogInfo($"Benchmark: save/prefs/stats writes blocked ({patched} hooks)");
            _host.StartCoroutine(RunSuite());
            return true;
        }

        private void UpdateRecorder()
        {
            if (BenchSafety.Active || _recorder == null) return;
            try
            {
                // Developer keys ([View] DevHotkeys; Plugin.Hotkeys.cs). F10 always stops a recording that is running.
                if (_keysLive && Input.GetKeyDown(KeyCode.F10) && (_recorder.IsRecording || DevKey(true, "F10")))
                {
                    bool was = _recorder.IsRecording;
                    _recorder.Toggle();
                    if (was && !_recorder.IsRecording) Toast($"Recording saved ({_recorder.LastSavedName})");
                }
                if (_keysLive && DevKey(Input.GetKeyDown(KeyCode.F11), "F11")) SavePose();
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
            // The approach file carries the checkpoints passed; this copy is for the reader.
            var cp = _recorder?.LastCheckpoint;
            var entry = new
            {
                Scene = SceneHelper.CurrentScene,
                Saved = DateTime.Now.ToString("s"),
                Approach = approach,
                Areas = areas,
                Checkpoint = cp?.Path,
                CheckpointPosition = cp?.Position,
                Position = new[] { Round(p.Position.x), Round(p.Position.y), Round(p.Position.z) },
                Yaw = Round(p.Yaw),
                Pitch = Round(p.Pitch),
            };
            string file = Path.Combine(_outDir, "poses.jsonl");
            File.AppendAllText(file, JsonConvert.SerializeObject(entry) + Environment.NewLine);
            Logger.LogInfo($"Pose saved: {JsonConvert.SerializeObject(entry)}");
            Toast($"Pose saved: {entry.Scene} ({entry.Position[0]}, {entry.Position[1]}, {entry.Position[2]}){(areas != null ? " + area state" : "")}{(approach != null ? " + approach" : "")}{(cp != null ? " + checkpoint" : "")}");

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
            if (_cineHideGui) return; // a frame the cinematic renderer captures (Plugin.CineRender.cs)
            if (BenchSafety.Active) { if (_editorBenchShow) DrawLightEditor(); return; } // the editor only when a bench variant asks (its screenshot)
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
            DrawLightEditor(); // Plugin.LightOverrides.cs
            DrawCine(); // Plugin.Cinematics.cs
        }

        // ---------------------------------------------------------------- suite

        /// <summary>Boot to the main menu, run this launch's suites in order, then quit (-ultrart-quit) or serve commands (-ultrart-bench-server).</summary>
        private IEnumerator RunSuite()
        {
            float t0 = Time.realtimeSinceStartup;
            bool skippedIntro = false;
            while (SafeCurrentScene() != "Main Menu" || SceneHelper.PendingScene != null)
            {
                // Skip the intro sequence: jump straight to the main menu once the intro scene is up.
                if (!skippedIntro && SafeCurrentScene() == "Intro" && SceneHelper.PendingScene == null) { skippedIntro = true; SceneHelper.LoadScene("Main Menu"); }
                if (Time.realtimeSinceStartup - t0 > 120f) { Logger.LogError("Benchmark: timed out waiting for main menu"); yield break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);

            foreach (var run in _benchRuns.ToArray())
            {
                yield return RunOneSuite(run, t0);
                t0 = Time.realtimeSinceStartup;
            }
            if (!string.IsNullOrEmpty(_benchServerDir)) { yield return ServeBenchCommands(); yield break; }
            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
            }
        }

        /// <summary>A command for the resident game: bench.ps1 writes it as cmd-*.json into the server folder.</summary>
        private sealed class ServerCommand
        {
            public string Id;
            public bool Quit;
            public List<BenchRun> Runs = new List<BenchRun>();
            public string Tag, Load;
            public bool NoChain, Validate, NoShots, NoBursts;
            public string ValidateMethods;
            public float Settle = -1f;
        }

        /// <summary>
        /// -ultrart-bench-server &lt;dir&gt;: stay at the main menu and run what bench.ps1 -KeepOpen drops into the folder
        /// (cmd-*.json, oldest first; each run's options come from the command, none carry over). ready.json says the game is
        /// up; done-&lt;id&gt;.json says a command finished. Quits on {"Quit":true}, or after -ultrart-bench-idle minutes
        /// (default 20) without a command. Shader bundles hot-reload while it waits; a changed UltraRT.dll needs a relaunch.
        /// </summary>
        private IEnumerator ServeBenchCommands()
        {
            string dir = _benchServerDir;
            Directory.CreateDirectory(dir);
            void State(string state) => File.WriteAllText(Path.Combine(dir, "ready.json"), JsonConvert.SerializeObject(new
            {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id, state, updated = DateTime.Now.ToString("s"),
                dll = File.GetLastWriteTimeUtc(Info.Location).Ticks,
            }));
            State("idle");
            Logger.LogInfo($"Benchmark server: waiting for commands in {dir}");
            float lastCommand = Time.realtimeSinceStartup;
            while (true)
            {
                string file = Directory.GetFiles(dir, "cmd-*.json").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                if (file != null)
                {
                    ServerCommand cmd = null;
                    for (int tries = 0; cmd == null && tries < 5; tries++)
                    {
                        cmd = TryReadCommand(file);   // the writer may still hold the file
                        if (cmd == null) yield return new WaitForSecondsRealtime(0.1f);
                    }
                    try { File.Delete(file); } catch { }
                    if (cmd == null) { Logger.LogError($"Benchmark server: unreadable command {file}, dropped"); continue; }
                    if (cmd.Quit) { State("quit"); Logger.LogInfo("Benchmark server: quit requested"); yield return new WaitForSecondsRealtime(0.5f); Application.Quit(); yield break; }

                    State("busy");
                    _benchTag = cmd.Tag; _benchLoad = string.IsNullOrEmpty(cmd.Load) ? null : cmd.Load;
                    _benchNoChain = cmd.NoChain; _benchValidate = cmd.Validate;
                    _benchValidateMethods = string.IsNullOrEmpty(cmd.ValidateMethods) ? null : cmd.ValidateMethods.Split(',');
                    _benchNoShots = cmd.NoShots; _benchNoBursts = cmd.NoBursts; _benchSettle = cmd.Settle;
                    Logger.LogInfo($"Benchmark server: command {cmd.Id}: {string.Join(", ", cmd.Runs.Select(r => Path.GetFileNameWithoutExtension(r.Suite)))}");
                    foreach (var run in cmd.Runs) yield return RunOneSuite(run, Time.realtimeSinceStartup);
                    File.WriteAllText(Path.Combine(dir, $"done-{cmd.Id}.json"), JsonConvert.SerializeObject(new { cmd.Id, finished = DateTime.Now.ToString("s") }));
                    // Idle again: do not spin the GPU at an uncapped frame rate while waiting.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = 30;
                    State("idle");
                    lastCommand = Time.realtimeSinceStartup;
                    continue;
                }
                if (Time.realtimeSinceStartup - lastCommand > _benchIdleMin * 60f)
                {
                    Logger.LogInfo($"Benchmark server: idle for {_benchIdleMin:F0} min, quitting");
                    State("quit");
                    Application.Quit();
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }

        private static ServerCommand TryReadCommand(string file)
        {
            try { return JsonConvert.DeserializeObject<ServerCommand>(File.ReadAllText(file)); }
            catch (Exception) { return null; }
        }

        private IEnumerator RunOneSuite(BenchRun run, float t0)
        {
            _benchSuitePath = run.Suite; _benchOut = run.Out;
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
                Options = string.Join(", ", new[] { _benchNoShots ? "no screenshots" : null, _benchNoBursts ? "no bursts" : null, _benchSettle >= 0f ? $"settle {_benchSettle:0.##}s" : null }.Where(x => x != null)),
            };

            var checks = new List<LoadCheck>();
            var jobs = new List<(string name, Func<IEnumerator> run, Action<string> fail)>();
            _level = null;
            _levelLoads = 0;
            result.Chaining = !_benchNoChain && !_benchValidate;
            if (_benchValidate)
            {
                // One check per distinct pose: a "pose" and a "sweep" scenario often share one.
                var validated = new HashSet<string>();
                var poses = new List<(Scenario sc, LoadCheck check)>();
                foreach (var sc in suite.Scenarios)
                {
                    if (string.IsNullOrEmpty(sc.Path.Approach)) continue;
                    string key = sc.Scene + "|" + sc.Path.Approach + "|" + (sc.Path.Position != null ? string.Join(",", sc.Path.Position) : "") + "|" + sc.Path.Yaw + "|" + sc.Path.Pitch;
                    if (!validated.Add(key)) continue;
                    var check = new LoadCheck { Scenario = sc.Name, Scene = sc.Scene };
                    checks.Add(check);
                    poses.Add((sc, check));
                    jobs.Add((sc.Name, () => ValidateScenario(sc, check, suiteDir, outDir), e => FailCheck(check, e)));
                }
                // "chain": every pose again, reached by chaining (see ChainPlan) and compared with the same reference.
                var methods = _benchValidateMethods ?? ValidateMethods;
                if (methods.Any(m => m.Trim().Equals("chain", StringComparison.OrdinalIgnoreCase)))
                {
                    var plan = ChainPlan.Build(poses.Select(x => x.sc).ToList(), p => ResolveLoad(p, "approach-fast"), suiteDir, true, Logger);
                    Logger.LogInfo("Benchmark: chain validation plan:\n  " + string.Join("\n  ", ChainPlan.Describe(plan)));
                    foreach (var it in plan)
                    {
                        var check = poses[it.Index].check;
                        jobs.Add((it.Sc.Name + " (chain)", () => ValidateChained(it, check, suiteDir, outDir), e => FailCheck(check, e)));
                    }
                }
            }
            else
            {
                var srs = suite.Scenarios.Select(sc => new ScenarioResult { Name = sc.Name, Scene = sc.Scene }).ToList();
                result.Scenarios.AddRange(srs);
                var plan = ChainPlan.Build(suite.Scenarios, p => ResolveLoad(p), suiteDir, result.Chaining, Logger);
                Logger.LogInfo($"Benchmark: plan ({(result.Chaining ? "chaining" : "no chaining")}):\n  " + string.Join("\n  ", ChainPlan.Describe(plan)));
                int order = 0;
                foreach (var it in plan)
                {
                    var sr = srs[it.Index];
                    sr.Order = ++order;
                    jobs.Add((it.Sc.Name, () => RunStep(it, sr, suiteDir, outDir, result.Chaining), e => sr.Error = e));
                }
            }

            var variantDefaults = SaveVariantState(); // restored when a scenario fails mid-variant
            foreach (var job in jobs)
            {
                Logger.LogInfo($"Benchmark: {(_benchValidate ? "validating" : "scenario")} '{job.name}'");
                // Run nested enumerators ourselves, so an exception anywhere fails just this scenario.
                var stack = new Stack<IEnumerator>();
                stack.Push(job.run());
                bool failed = false;
                while (stack.Count > 0)
                {
                    object current;
                    try
                    {
                        var top = stack.Peek();
                        if (!top.MoveNext()) { stack.Pop(); continue; }
                        current = top.Current;
                    }
                    catch (Exception e)
                    {
                        job.fail(e.Message);
                        Logger.LogError($"Benchmark: scenario '{job.name}' failed: {e}");
                        foreach (var en in stack) { try { (en as IDisposable)?.Dispose(); } catch { } }
                        failed = true;
                        break;
                    }
                    if (current is IEnumerator nested && !(current is CustomYieldInstruction)) { stack.Push(nested); continue; }
                    yield return current;
                }
                _host.OnFixed = null;
                PoseLoader.SetGameSpeed(1f);
                // The player stays taken over between chained scenarios; it is released before the next level load.
                if (failed)
                {
                    _level = null; GameControl.ReleaseControl();
                    // A failure inside a burst or a variant mode would otherwise leave its settings on for the rest of the run.
                    _benchModeVariant = null;
                    CineExit(); // a cinematic scenario that failed: camera, HUD, time scale back (its render restored its own settings)
                    RestoreVariantState(variantDefaults);
                    Denoiser.FrameTimeOverride = 0f;
                    if (_autoExposure != null) _autoExposure.Trace = null;
                }
                if (_benchValidate) File.WriteAllText(Path.Combine(outDir, "validation.json"), JsonConvert.SerializeObject(checks, Formatting.Indented));
                else File.WriteAllText(Path.Combine(outDir, "results.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            GameControl.ReleaseControl();
            _level = null;

            result.LevelLoads = _levelLoads;
            result.TotalSeconds = Time.realtimeSinceStartup - t0;
            result.Resolution = $"{Screen.width}x{Screen.height}";
            string summary = _benchValidate ? SummarizeValidation(checks, suite.Name, _benchTag, result.TotalSeconds, _levelLoads) : Summarize(result);
            if (!_benchValidate) File.WriteAllText(Path.Combine(outDir, "results.json"), JsonConvert.SerializeObject(result, Formatting.Indented));
            File.WriteAllText(Path.Combine(outDir, "summary.md"), summary);
            Logger.LogInfo($"Benchmark done: {outDir}\n{summary}");

            // Another suite may follow in this game: leave no variant, mode or camera takeover behind.
            if (_proxies != null && (_benchRuns.Count > 1 || !string.IsNullOrEmpty(_benchServerDir)))
            {
                try { ApplyBenchMode("off"); RestoreVariantState(variantDefaults); Denoiser.FrameTimeOverride = 0f; }
                catch (Exception e) { Logger.LogWarning($"Benchmark: could not reset the view after the suite: {e.Message}"); }
            }
        }

        private static void FailCheck(LoadCheck check, string error)
        {
            if (check.Methods.Count == 0) check.Methods.Add(new LoadCheckMethod { Load = "?" });
            check.Methods[check.Methods.Count - 1].Error = error;
        }

        // ---------------------------------------------------------------- chaining

        private sealed class Reach
        {
            /// <summary>"fresh load" or "chained after '...'".</summary>
            public string How;
            public string After;
            public float LevelSeconds;
            /// <summary>Route time the replay resumes from (0 = from the level start).</summary>
            public float RouteFrom;
        }

        /// <summary>
        /// Reuse the loaded level when the scenario can start from the state the previous one left (ChainPlan.CanReuse),
        /// else load it fresh. On reuse the player stays taken over and the replay later resumes where the walked route
        /// ended; the RT history is cleared so the measurement starts the way it does after a fresh load.
        /// </summary>
        private IEnumerator EnsureLevel(PlanItem it, bool chain, Reach reach)
        {
            if (chain && ChainPlan.CanReuse(_level, it))
            {
                reach.After = _level.Last;
                reach.How = $"chained after '{_level.Last}'";
                reach.RouteFrom = it.Need == Need.Route && !_level.Fresh ? _level.RouteTime : 0f;
                ResetRtHistory();
                Logger.LogInfo($"Benchmark: '{it.Sc.Name}' reuses the level left by '{_level.Last}'" +
                               (reach.RouteFrom > 0 ? $"; route {it.Route.Name} resumes at {reach.RouteFrom:F1}s of {it.Route.Duration:F1}s" : ""));
                yield break;
            }
            if (chain && it.PlannedAfter != null)
                Logger.LogWarning($"Benchmark: '{it.Sc.Name}' was planned to chain after '{it.PlannedAfter}' but the level state does not allow it; reloading");
            GameControl.ReleaseControl();
            _level = null;
            float t0 = Time.realtimeSinceStartup;
            yield return LoadLevel(it.Sc);
            _level = new LevelState { Key = it.Key, Spawn = GameControl.CurrentPose() };
            reach.How = "fresh load";
            reach.LevelSeconds = Time.realtimeSinceStartup - t0;
        }

        /// <summary>Temporal RT state (accumulation, denoiser and cache history) as after a level load.</summary>
        private void ResetRtHistory()
        {
            _accumFrames = 0;
            _denoiser?.Reset();
            _lightSampling?.Reset();
            _reflFilter?.Reset();
            _radianceCache?.Reset();
        }

        private IEnumerator RunStep(PlanItem it, ScenarioResult sr, string suiteDir, string outDir, bool chain)
        {
            float t0 = Time.realtimeSinceStartup;
            var reach = new Reach();
            yield return EnsureLevel(it, chain, reach);
            sr.Reached = reach.How;
            sr.ChainedAfter = reach.After;
            sr.LevelLoadSeconds = reach.LevelSeconds;
            yield return RunScenario(it.Sc, sr, suiteDir, outDir, it.Method ?? ResolveLoad(it.Sc.Path), reach.RouteFrom);
            _level = _level?.After(it);
            sr.WallSeconds = Time.realtimeSinceStartup - t0;
        }

        // ---------------------------------------------------------------- scenario

        /// <summary>Load the scenario's level with its seed, let it settle and switch the bench cheats on.</summary>
        private IEnumerator LoadLevel(Scenario sc)
        {
            _levelLoads++;
            PoseLoader.Seed = sc.Seed;
            PoseLoader.Pins = sc.RandomPicks;
            // Seed before the level's own Start()s run (sceneLoaded fires first).
            UnityEngine.Random.InitState(sc.Seed);
            void Seed(Scene s, LoadSceneMode m) { UnityEngine.Random.InitState(sc.Seed); SceneManager.sceneLoaded -= Seed; }
            SceneManager.sceneLoaded += Seed;

            ResetScene();
            SceneHelper.LoadScene(sc.Scene);
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (SceneHelper.PendingScene != null || (_proxies == null && !_noScene) || SafeCurrentScene() != sc.Scene)
            {
                if (Time.realtimeSinceStartup - t0 > 120f) throw new TimeoutException($"loading {sc.Scene}");
                yield return null;
            }
            yield return new WaitForSecondsRealtime(_benchSettle >= 0f ? _benchSettle : sc.Settle);

            GameControl.EnableCheats(blindEnemies: sc.Enemies == "blind", disableSpawns: sc.Enemies == "none");
            yield return null;
        }

        private sealed class PoseLoad
        {
            public CamPose Spawn;
            public string Method, Detail;
            public float Seconds;
        }

        private string ResolveLoad(PathSpec p, string forced = null)
        {
            string l = (forced ?? _benchLoad ?? p.Load ?? "auto").ToLowerInvariant();
            bool hasApproach = !string.IsNullOrEmpty(p.Approach), hasAreas = !string.IsNullOrEmpty(p.Areas);
            if (l == "auto") return hasApproach ? "approach-fast" : hasAreas ? "areas" : "none";
            if (l != "checkpoint" && l != "approach" && l != "approach-fast" && l != "areas" && l != "none")
                throw new ArgumentException($"unknown Load '{l}'");
            if ((l == "checkpoint" || l == "approach" || l == "approach-fast") && !hasApproach) return hasAreas ? "areas" : "none";
            if (l == "areas" && !hasAreas) return "none";
            return l;
        }

        /// <summary>
        /// Bring the level into the state the scenario's pose needs (see PathSpec.Load). Untimed, RT off.
        /// spawn = the level's spawn pose. routeFrom &gt; 0: the level already walked the route up to there (chaining),
        /// so the replay resumes from that point (approach-fast only).
        /// </summary>
        private IEnumerator LoadPose(Scenario sc, string method, string suiteDir, PoseLoad res, CamPose spawn, float routeFrom = 0f)
        {
            float t0 = Time.realtimeSinceStartup;
            res.Method = method;
            res.Spawn = spawn;
            if (routeFrom > 0f && method != "approach-fast") throw new InvalidOperationException($"cannot resume a route with Load '{method}'");
            var p = sc.Path;
            string Resolve(string f) => Path.IsPathRooted(f) ? f : Path.Combine(suiteDir, f);

            if (!string.IsNullOrEmpty(p.Areas) && (method == "areas" || method == "approach"))
            {
                // Re-apply what the area triggers had switched on/off when the pose was saved (no route needed).
                var (applied, missing) = AreaState.Load(Resolve(p.Areas)).Apply();
                res.Detail = $"area state: {applied} changes, {missing} not found";
                Logger.LogInfo($"Benchmark: area state applied ({applied} changes, {missing} not found)");
                yield return new WaitForSecondsRealtime(1f);
            }
            if (method == "checkpoint" || method == "approach" || method == "approach-fast")
            {
                var rec = Recording.Load(Resolve(p.Approach));
                var route = rec.ToPath();
                float speed = Mathf.Max(0.1f, p.ApproachSpeed);
                float gameSpeed = method == "approach" ? 1f : Mathf.Max(1f, p.FastTimeScale);
                float from = Mathf.Clamp(routeFrom, 0f, route.Duration);
                ApplyBenchMode("off");
                if (from > 0f) res.Detail = $"resumed at {from:F1}s: {route.Duration - from:F1}s of the {route.Duration:F1}s route";

                if (method == "checkpoint")
                {
                    var pick = PoseLoader.PickCheckpoint(rec);
                    foreach (var line in PoseLoader.DescribeAll(rec)) Logger.LogInfo("Benchmark:   " + line);
                    if (pick == null)
                    {
                        res.Detail = "no checkpoint on the route; whole route fast-forwarded";
                        Logger.LogInfo($"Benchmark: {res.Detail}");
                    }
                    else
                    {
                        // The rooms the checkpoint switches on run their Start()s next frame (checkpoints clone their
                        // rooms there); give them a moment before the player moves on.
                        Logger.LogInfo($"Benchmark: jumping to checkpoint {PoseLoader.Describe(pick.Checkpoint)} ({pick.How}, route time {pick.Time:F1}s of {route.Duration:F1}s)");
                        string rooms = pick.Checkpoint.toActivate.name;
                        GameControl.TakeControl(PoseLoader.Jump(pick.Checkpoint));
                        float until = Time.realtimeSinceStartup + 0.5f;
                        while (Time.realtimeSinceStartup < until) { GameControl.HideCheatPanels(); yield return null; }
                        from = Mathf.Clamp(pick.Time, 0f, route.Duration);
                        res.Detail = $"checkpoint '{rooms}' ({pick.How}), then {route.Duration - from:F1}s of the {route.Duration:F1}s route";
                    }
                }

                GameControl.TakeControl(route.Sample(from));
                Logger.LogInfo($"Benchmark: approach {route.Duration - from:F1}s of route at {speed}x, game speed {gameSpeed}x");
                int killed = 0;
                float at = from;
                float deadline = Time.realtimeSinceStartup + (route.Duration - from) / (speed * gameSpeed) * 4f + 30f;
                try
                {
                    if (gameSpeed > 1f)
                    {
                        // Fast-forward: the whole game runs faster and the player moves once per physics step, so
                        // triggers see the same step size as in the real-time replay.
                        _host.OnFixed = () =>
                        {
                            at += Time.fixedDeltaTime * speed;
                            GameControl.SetPoseNow(route.Sample(Mathf.Min(at, route.Duration)));
                        };
                    }
                    float nextKill = 0f;
                    while (at < route.Duration)
                    {
                        if (Time.realtimeSinceStartup > deadline) throw new TimeoutException($"approach replay stalled at {at:F1}s of {route.Duration:F1}s");
                        if (gameSpeed > 1f) PoseLoader.SetGameSpeed(gameSpeed);
                        else
                        {
                            at += Time.unscaledDeltaTime * speed;
                            GameControl.SetPose(route.Sample(at));
                        }
                        GameControl.HideCheatPanels();
                        // Arena completion (all enemies dead) is what opens doors and activates the next areas.
                        if (p.ApproachKillEnemies && Time.time >= nextKill)
                        {
                            nextKill = Time.time + 0.25f;
                            killed += KillEnemies(p.ApproachSpare);
                        }
                        yield return null;
                    }
                }
                finally
                {
                    _host.OnFixed = null;
                    if (gameSpeed > 1f) PoseLoader.SetGameSpeed(1f);
                }
                if (killed > 0 || KilledTypes.Count > 0) Logger.LogInfo($"Benchmark: approach killed {killed} enemies ({string.Join(", ", KilledTypes)})");
                KilledTypes.Clear();
                res.Spawn = route.Sample(route.Duration);
                GameControl.SetPose(res.Spawn);
                yield return new WaitForSecondsRealtime(1.5f); // let area activation settle
            }
            res.Seconds = Time.realtimeSinceStartup - t0;
            Logger.LogInfo($"Benchmark: pose loaded via {method} in {res.Seconds:F1}s{(res.Detail != null ? $" ({res.Detail})" : "")}");
        }

        /// <summary>Path.Activate: every object whose path matches a glob goes on, with its parents and its children.</summary>
        private void ActivateObjects(List<string> globs)
        {
            var rxs = globs.Select(g => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(g).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            int roots = 0, switched = 0;
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                string path = GameControl.PathOf(t);
                if (!rxs.Any(rx => rx.IsMatch(path))) continue;
                roots++;
                for (var x = t; x != null; x = x.parent) if (!x.gameObject.activeSelf) { x.gameObject.SetActive(true); switched++; }
                foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (!c.gameObject.activeSelf) { c.gameObject.SetActive(true); switched++; }
                if (roots <= 6) Logger.LogInfo($"Benchmark: Activate '{path}' at {t.position}");
            }
            Logger.LogInfo($"Benchmark: Activate matched {roots} objects, switched on {switched}");
        }

        private static bool Spared(EnemyIdentifier e, List<string> spare)
        {
            if (spare == null) return false;
            foreach (var s in spare)
            {
                if (string.Equals(s, "boss", StringComparison.OrdinalIgnoreCase) && (e.isBoss || e.GetComponent<BossHealthBar>() != null)) return true;
                if (string.Equals(s, e.enemyType.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Enemy types the approach killed (and bosses it spared), for the log.</summary>
        private static readonly HashSet<string> KilledTypes = new HashSet<string>();

        private static int KillEnemies(List<string> spare = null)
        {
            int killed = 0;
            var tracker = MonoSingleton<EnemyTracker>.Instance;
            if (tracker != null)
                foreach (var e in tracker.GetCurrentEnemies().ToList())
                {
                    if (e == null || e.dead) continue;
                    if (Spared(e, spare)) { KilledTypes.Add("spared " + e.enemyType); continue; }
                    KilledTypes.Add(e.enemyType.ToString());
                    e.InstaKill(); killed++;
                }
            return killed;
        }

        // ---------------------------------------------------------------- load validation

        private static readonly string[] ValidateMethods = { "approach", "checkpoint", "approach-fast", "areas", "none", "chain" };

        /// <summary>
        /// -ultrart-bench-validate: reach the scenario's pose with every load method in turn (a fresh level load each)
        /// and compare what is loaded there against the real-time approach replay.
        /// </summary>
        private IEnumerator ValidateScenario(Scenario sc, LoadCheck check, string suiteDir, string outDir)
        {
            float fastScale = sc.Path.FastTimeScale;
            try
            {
                foreach (var spec in _benchValidateMethods ?? ValidateMethods)
                {
                    // "approach-fast@3" = that method at game speed 3.
                    var parts = spec.Split('@');
                    string method = parts[0].Trim().ToLowerInvariant();
                    if (method == "chain") continue; // a separate pass after every pose (ValidateChained)
                    sc.Path.FastTimeScale = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : fastScale;
                    if (ResolveLoad(sc.Path, method) != method) continue; // needs a file this scenario doesn't have
                    var m = new LoadCheckMethod { Load = spec.Trim() };
                    check.Methods.Add(m);

                    GameControl.ReleaseControl();
                    _level = null;
                    yield return LoadLevel(sc);
                    var load = new PoseLoad();
                    yield return LoadPose(sc, method, suiteDir, load, GameControl.CurrentPose());
                    m.LoadSeconds = load.Seconds;
                    m.Detail = load.Detail;
                    yield return CheckPose(sc, check, m, load, suiteDir, outDir);
                    GameControl.ReleaseControl();
                }
            }
            finally { sc.Path.FastTimeScale = fastScale; }
        }

        /// <summary>
        /// Validation "chain" method: reach the pose the way a chained benchmark does (the level and route the previous
        /// pose left, when the plan allows) and compare it with the same reference as the fresh-load methods.
        /// </summary>
        private IEnumerator ValidateChained(PlanItem it, LoadCheck check, string suiteDir, string outDir)
        {
            var m = new LoadCheckMethod { Load = "chain" };
            check.Methods.Add(m);
            var reach = new Reach();
            yield return EnsureLevel(it, true, reach);
            var load = new PoseLoad();
            yield return LoadPose(it.Sc, it.Method, suiteDir, load, _level.Spawn, reach.RouteFrom);
            m.LoadSeconds = load.Seconds;
            m.Detail = reach.How + (load.Detail != null ? "; " + load.Detail : "");
            yield return CheckPose(it.Sc, check, m, load, suiteDir, outDir);
            // CheckPose held the pose for 3 s like a measurement would; the next pose may resume from here.
            _level = _level?.After(it);
        }

        private readonly Dictionary<LoadCheck, (SceneFingerprint fp, Color32[] image)> _validateRefs = new Dictionary<LoadCheck, (SceneFingerprint, Color32[])>();

        /// <summary>Hold the pose with no enemies alive, fingerprint what is loaded and compare it with the check's first method.</summary>
        private IEnumerator CheckPose(Scenario sc, LoadCheck check, LoadCheckMethod m, PoseLoad load, string suiteDir, string outDir)
        {
            var (path, _) = BuildPath(sc.Path, load.Spawn, suiteDir);
            var pose = path.Start;
            ApplyBenchMode("off");
            GameControl.TakeControl(pose);
            // Same enemy state for every method: none alive. Then let doors, gore and effects come to rest.
            float until = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < until)
            {
                KillEnemies();
                GameControl.SetPose(pose);
                GameControl.HideCheatPanels();
                yield return null;
            }

            var fp = SceneFingerprint.Take(GameControl.Cam != null ? GameControl.Cam.transform.position : pose.Position);
            m.Renderers = fp.Count;
            m.NearRenderers = fp.NearCount;
            m.Lights = fp.Lights;
            m.Instances = (int)(_proxies?.Rtas?.GetInstanceCount() ?? 0);

            yield return new WaitForEndOfFrame();
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            m.Screenshot = $"{Sanitize(sc.Name)}_{check.Methods.Count}_{Sanitize(m.Load)}.jpg";
            File.WriteAllBytes(Path.Combine(outDir, m.Screenshot), shot.EncodeToJPG(85));
            var image = Shrink(shot, 160, 90);
            Destroy(shot);

            if (!_validateRefs.TryGetValue(check, out var reference))
            {
                _validateRefs[check] = (fp, image);
                m.Match = true;
            }
            else
            {
                var missingNear = SceneFingerprint.Missing(reference.fp.Near, fp.Renderers);
                var extraNear = SceneFingerprint.Missing(fp.Near, reference.fp.Renderers);
                m.MissingNear = missingNear.Count;
                m.ExtraNear = extraNear.Count;
                m.Missing = SceneFingerprint.Missing(reference.fp.Renderers, fp.Renderers).Count;
                m.Extra = SceneFingerprint.Missing(fp.Renderers, reference.fp.Renderers).Count;
                m.MissingNearSamples = missingNear.Take(12).ToList();
                m.ExtraNearSamples = extraNear.Take(12).ToList();
                long sum = 0;
                int off = 0;
                for (int i = 0; i < image.Length; i++)
                {
                    int d = Math.Max(Math.Abs(image[i].r - reference.image[i].r), Math.Max(Math.Abs(image[i].g - reference.image[i].g), Math.Abs(image[i].b - reference.image[i].b)));
                    sum += d;
                    if (d > 32) off++;
                }
                m.ImageDiff = (float)sum / image.Length;
                m.ImageDiffPct = 100f * off / image.Length;
                m.Match = m.MissingNear <= ValidateMissing && m.ImageDiffPct < ValidateImagePct;
            }
            Logger.LogInfo($"Benchmark: validate '{sc.Name}' {m.Load}: {m.LoadSeconds:F1}s, renderers {m.Renderers} (near {m.NearRenderers}), " +
                           $"missing near {m.MissingNear}, extra near {m.ExtraNear}, image diff {m.ImageDiff:F1} / {m.ImageDiffPct:F1}% -> {(m.Match ? "match" : "DIFFERENT")}");
        }

        /// <summary>Near renderers allowed to be missing: flickering lamps and broken props differ between two identical runs.</summary>
        private const int ValidateMissing = 3;
        /// <summary>Share of pixels (at 160x90) that may differ from the reference screenshot. Loose on purpose: gore and moving
        /// geometry (8-3's shifting hallway) change up to 10% between good loads; an unloaded area changes 60% or more.</summary>
        private const float ValidateImagePct = 25f;

        /// <summary>Box-filter a screenshot down to w x h so small animated details don't dominate the comparison.</summary>
        private static Color32[] Shrink(Texture2D src, int w, int h)
        {
            var px = src.GetPixels32();
            int sw = src.width, sh = src.height;
            var result = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = x * sw / w, x1 = Math.Max(x0 + 1, (x + 1) * sw / w), y0 = y * sh / h, y1 = Math.Max(y0 + 1, (y + 1) * sh / h);
                long r = 0, g = 0, b = 0;
                int n = 0;
                // Every 4th pixel is plenty for an average.
                for (int yy = y0; yy < y1; yy += 4)
                for (int xx = x0; xx < x1; xx += 4)
                {
                    var c = px[yy * sw + xx];
                    r += c.r; g += c.g; b += c.b; n++;
                }
                result[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
            }
            return result;
        }

        private static string SummarizeValidation(List<LoadCheck> checks, string suite, string tag, float totalSeconds, int levelLoads)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# UltraRT pose-load validation: {suite}{(string.IsNullOrEmpty(tag) ? "" : $" ({tag})")}");
            sb.AppendLine($"{totalSeconds:F0} s, {levelLoads} level loads");
            sb.AppendLine();
            sb.AppendLine("Each pose is reached with every load method on a fresh level load and compared with the first row. " +
                          "\"chain\" reaches the poses the way a chained benchmark does (the level and route the previous pose left, see Detail). " +
                          "Renderers = the level's mesh renderers that are on (enemies, gore, player excluded); near = within 120 m of the camera. " +
                          "Missing/extra near = near renderers the reference has and this load lacks, and the reverse. " +
                          $"Image = game-only screenshot at 160x90: mean difference (0-255) and share of pixels off by more than 32. Match = at most {ValidateMissing} missing near (lamps flicker, props break) and under {ValidateImagePct}% of pixels off (gore and moving geometry such as 8-3's shifting hallway change up to 10% between good loads).");
            sb.AppendLine();
            sb.AppendLine("| Pose | Load | Load s | Renderers (near) | RT instances | Lights | Missing near | Extra near | Image diff | Match | Detail |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var c in checks)
                foreach (var m in c.Methods)
                {
                    bool first = m == c.Methods[0];
                    sb.AppendLine($"| {c.Scenario} | {m.Load} | {m.LoadSeconds:F1} | {m.Renderers} ({m.NearRenderers}) | {m.Instances} | {m.Lights} | " +
                                  (m.Error != null ? $"**failed**: {m.Error} |||||" :
                                   first ? "- | - | - | reference | |" :
                                   $"{m.MissingNear} | {m.ExtraNear} | {m.ImageDiff:F1} / {m.ImageDiffPct:F1}% | {(m.Match ? "yes" : "**no**")} | {m.Detail} |"));
                }
            return sb.ToString();
        }

        /// <summary>Measure one scenario in the loaded level (EnsureLevel). routeFrom: see LoadPose.</summary>
        private IEnumerator RunScenario(Scenario sc, ScenarioResult sr, string suiteDir, string outDir, string method, float routeFrom)
        {
            // The game's enemy outlines for this scenario (both images; Plugin.EnemyOutlines.cs): its own field, else the command line's.
            _enemyOutlines = sc.EnemyOutlines ?? _enemyOutlinesDefault;
            if ((sc.Path.Type ?? "").ToLowerInvariant() == "tour") { yield return RunTour(sc, sr, outDir); yield break; } // Plugin.Bench.Tour.cs
            var load = new PoseLoad();
            yield return LoadPose(sc, method, suiteDir, load, _level.Spawn, routeFrom);
            if (sc.Path.Activate != null && sc.Path.Activate.Count > 0)
            {
                ActivateObjects(sc.Path.Activate);
                yield return new WaitForSecondsRealtime(0.5f); // their Start and first physics steps (beams aim in FixedUpdate)
            }
            if (sc.Path.FreezeAtPose) { PoseLoader.SetGameSpeed(0f); Logger.LogInfo("Benchmark: game time frozen at the pose (FreezeAtPose)"); }
            sr.Load = load.Method;
            sr.LoadSeconds = load.Seconds;
            sr.LoadDetail = load.Detail;
            var spawn = load.Spawn;
            var (path, arenas) = BuildPath(sc.Path, spawn, suiteDir);
            if (!string.IsNullOrEmpty(sc.Path.LookAt)) path = LookAtPath(sc.Path, path) ?? path; // Plugin.Bench.LookAt.cs
            sr.Duration = path.Duration;
            GameControl.TakeControl(path.Start);
            GameControl.GunBob = sc.GunBob;
            GameControl.BobTime = 0f;
            if (sc.Weapon > 0)
            {
                var gc = MonoSingleton<GunControl>.Instance;
                if (gc != null && gc.slots.Count >= sc.Weapon && gc.slots[sc.Weapon - 1].Count > 0)
                {
                    int variant = -1;
                    if (!string.IsNullOrEmpty(sc.WeaponVariant))
                    {
                        var list = gc.slots[sc.Weapon - 1];
                        for (int vi = 0; vi < list.Count; vi++)
                            if (list[vi] != null && list[vi].name.StartsWith(sc.WeaponVariant, StringComparison.OrdinalIgnoreCase)) { variant = vi; break; }
                        if (variant < 0) Logger.LogWarning($"Benchmark: no '{sc.WeaponVariant}' in weapon slot {sc.Weapon}");
                    }
                    if (variant >= 0) { if (gc.currentWeapon != gc.slots[sc.Weapon - 1][variant]) gc.SwitchWeapon(sc.Weapon, variant); }
                    else if (gc.currentSlotIndex != sc.Weapon) gc.SwitchWeapon(sc.Weapon);
                    Logger.LogInfo($"Benchmark: holding weapon slot {sc.Weapon}: {(gc.currentWeapon != null ? gc.currentWeapon.name : "none")}");
                    yield return new WaitForSecondsRealtime(1.0f); // the draw animation
                }
                else Logger.LogWarning($"Benchmark: no weapon in slot {sc.Weapon}");
            }
            yield return new WaitForSecondsRealtime(0.5f);
            // The route was fast-forwarded with RT off, so the RT scene hasn't seen what it switched on yet (8-3's
            // checkpoint clones ~3,000 renderers). Trace until that is registered, so it doesn't land in the measurement.
            string warm = sc.Modes.FirstOrDefault(m => m != "off");
            if (warm != null && _proxies != null)
            {
                ApplyBenchMode(warm);
                float until = Time.realtimeSinceStartup + 5f;
                int warmFrames = 0;
                while (Time.realtimeSinceStartup < until && (warmFrames++ < 30 || _proxies.DeferredCount > 0 || Patches.PendingSpawns.Count > 0))
                {
                    GameControl.SetPose(path.Start);
                    GameControl.HideCheatPanels();
                    yield return null;
                }
                // DLSS RR: each mode that uses it gets its feature now (NGX's start-up and the feature's creation run in the
                // background; their frames run UltraRT's denoiser and would otherwise be measured as the mode's, or, before
                // the warm-up, a stalled first frame could swallow the whole scenario).
                foreach (var m in sc.Modes)
                {
                    if (m == "off" || !(m.StartsWith(PathTraceModeName) || m.Contains("_rrEnabled=1"))) continue;
                    ApplyBenchMode(m);
                    GameControl.SetPose(path.Start);
                    yield return WaitForRR();
                }
                ApplyBenchMode("off");
            }

            if (sc.Cinematic != null) { yield return RunCinematicScenario(sc, sr, suiteDir, outDir, path, arenas); yield break; } // Plugin.Cinematics.Bench.cs

            var frames = sc.Modes.ToDictionary(m => m, _ => new List<float>(8192));
            var gpuSum = sc.Modes.ToDictionary(m => m, _ => new double[SlotCount]);
            var gpuN = sc.Modes.ToDictionary(m => m, _ => new int[SlotCount]);
            var rtSum = sc.Modes.ToDictionary(m => m, _ => new double[SlotCount]); // render-thread CPU per slot
            var rtN = sc.Modes.ToDictionary(m => m, _ => new int[SlotCount]);
            // Screenshot times also trigger the burst. -noshots + -noburst (perf runs): nothing is captured, the pose just plays.
            bool wantBurst = sc.Burst > 0 && !_benchNoBursts;
            var shots = new Queue<float>(_benchNoShots && !wantBurst ? new List<float>() : sc.Screenshots.OrderBy(x => x).ToList());
            // Per-frame CPU breakdown (Perf.cs): plugin sections, main-thread scripts/render, GC.
            int nSec = (int)Perf.Sec.Count;
            var secMs = new float[nSec];
            var cpuSum = sc.Modes.ToDictionary(m => m, _ => new double[nSec]);
            var cpuMax = sc.Modes.ToDictionary(m => m, _ => new float[nSec]);
            var mainSum = sc.Modes.ToDictionary(m => m, _ => new double[2]);
            var gcCount = sc.Modes.ToDictionary(m => m, _ => 0);
            var allocSum = sc.Modes.ToDictionary(m => m, _ => 0.0);
            // Wall-clock spans of the counted frames per mode (unix seconds), for cutting an ETW trace (bench.ps1 -Etw).
            var windows = sc.Modes.ToDictionary(m => m, _ => new List<double[]>());
            bool windowOpen = false;
            var csv = new StringBuilder("t,mode,dt_ms,scripts_ms,render_ms,gc0,alloc_kb," + string.Join(",", Perf.Names.Select(n => n + "_ms")) + ",instances,static_records,dyn_active\n");
            int gc0 = GC.CollectionCount(0);
            long heap = GC.GetTotalMemory(false);
            float nextKill = 0f;
            Perf.Take(null);

            for (int round = 0; round < Math.Max(1, sc.Rounds); round++)
            {
                int nextArena = 0;
                string current = null;
                int sinceSwitch = 0;
                float pathT = 0f;
                float nextEffect = sc.Effects != null && sc.Effects.Every > 0f ? 0.5f : float.MaxValue;
                long lastResolved = _gpu?.ResolvedFrames ?? 0;

                while (pathT <= path.Duration)
                {
                    // Screenshots: hold the pose and grab every mode.
                    if (round == 0 && shots.Count > 0 && pathT >= shots.Peek())
                    {
                        float at = shots.Dequeue();
                        if (!_benchNoShots)
                        {
                        var activeLights = FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.intensity > 0).ToList();
                        Logger.LogInfo($"Benchmark: '{sc.Name}' @ {at:F1}s ambient={RenderSettings.ambientLight} lights: " +
                                       string.Join(", ", activeLights.GroupBy(l => l.type).Select(g => $"{g.Key}={g.Count()}")) +
                                       string.Concat(activeLights.Where(l => l.type == LightType.Directional)
                                           .Select(l => $" | sun '{l.name}' I={l.intensity:F2} mask=0x{l.cullingMask:X8}")));
                        LogAreaLighting(sc.Name);
                        var camPos = GameControl.Cam != null ? GameControl.Cam.transform.position : Vector3.zero;
                        foreach (var r in FindObjectsOfType<Renderer>())
                        {
                            var m = r.sharedMaterial;
                            if (!r.enabled || m == null || m.shader == null) continue;
                            if (m.shader.name.StartsWith("UltraRT/", StringComparison.Ordinal)) continue; // our own proxies (a static veil has its batch's bounds)
                            var texProps = m.GetTexturePropertyNames().Select(n => (n, t: m.GetTexture(n))).Where(x => x.t != null).ToList();
                            bool usesRT = texProps.Any(x => x.t is RenderTexture);
                            bool skyShader = m.shader.name.IndexOf("Sky", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool bigNear = r.bounds.size.magnitude > 150f && r.bounds.SqrDistance(camPos) < 1f; // camera inside its bounds
                            // Large geometry above the camera (8-2's upside-down city: does it shadow the sun?)
                            bool overhead = r.bounds.min.y > camPos.y + 8f && Mathf.Max(r.bounds.size.x, r.bounds.size.z) > 60f;
                            if (overhead)
                                Logger.LogInfo($"  overhead renderer '{GameControl.PathOf(r.transform)}' bounds {r.bounds.min:F0}..{r.bounds.max:F0} layer={r.gameObject.layer} " +
                                               $"shader='{m.shader.name}' mat='{m.name}' static={r.isPartOfStaticBatch}");
                            if (!usesRT && !skyShader && !bigNear) continue;
                            Logger.LogInfo($"  sky-ish renderer '{GameControl.PathOf(r.transform)}' size={r.bounds.size.magnitude:F0} layer={r.gameObject.layer} " +
                                           $"shader='{m.shader.name}' mat='{m.name}' queue={m.renderQueue} kw=[{string.Join(" ", m.shaderKeywords)}] " +
                                           $"tex=[{string.Join(", ", texProps.Select(x => $"{x.n}={x.t.name}{(x.t is RenderTexture ? "(RT)" : "")}"))}] " +
                                           $"why={(usesRT ? "RT " : "")}{(skyShader ? "skyshader " : "")}{(bigNear ? "encloses-camera" : "")}");
                        }
                        bool probed = false, skinProbed = false;
                        foreach (var m in sc.Modes)
                        {
                            ApplyBenchMode(m);
                            yield return WaitForRR();
                            for (int f = 0; f < 12; f++) { GameControl.HideCheatPanels(); yield return null; }
                            if (sc.ShotSettle > 0f)
                            {
                                float settleUntil = Time.realtimeSinceStartup + sc.ShotSettle;
                                while (Time.realtimeSinceStartup < settleUntil) { GameControl.HideCheatPanels(); yield return null; }
                            }
                            if (AutoExposureFor(m.Split('+')[0]))
                            {
                                // Auto exposure snaps on the first frame of the mode; give the async readback time to log it.
                                float until = Time.realtimeSinceStartup + 0.6f;
                                while (Time.realtimeSinceStartup < until) { GameControl.HideCheatPanels(); yield return null; }
                                Logger.LogInfo($"Benchmark: '{sc.Name}' @ {at:F1}s {m}: {ExposureSummary()}");
                            }
                            if (m != "off") Logger.LogInfo($"Benchmark: '{sc.Name}' @ {at:F1}s {m}: {RadianceStats()}");
                            if (sc.Effects != null)
                            {
                                // The same effect at the same age in every mode.
                                EffectSpawner.Spawn(sc.Effects);
                                float until = Time.realtimeSinceStartup + Mathf.Max(0f, sc.Effects.ShotDelay);
                                while (Time.realtimeSinceStartup < until) { GameControl.HideCheatPanels(); yield return null; }
                            }
                            if (SceneProxies.ViewProbe && _proxies != null && m != "off" && !probed)
                            {
                                // After the mode's effects spawned (and their proxies were made): what is in front of the camera.
                                probed = true;
                                foreach (var line in _proxies.DescribeView(GameControl.Cam != null ? GameControl.Cam.cam : null)) Logger.LogInfo("  view probe: " + line);
                                foreach (var line in _proxies.DescribeRtasExtremes()) Logger.LogInfo("  rtas probe: " + line);
                            }
                            if (SceneProxies.SkinProbeFilter != null && _proxies != null && m != "off" && !skinProbed)
                            {
                                // -ultrart-skin-probe: the matching skinned renderers, and the game's skinned vertices against the proxy's.
                                skinProbed = true;
                                var pcam = GameControl.Cam != null ? GameControl.Cam.cam : null;
                                foreach (var line in _proxies.DescribeMatching(SceneProxies.SkinProbeFilter, pcam, 12)) Logger.LogInfo("  skin probe: " + line);
                                _proxies.ProbeSkinnedExtents(SceneProxies.SkinProbeFilter, line => Logger.LogInfo("  skin probe: " + line), 12);
                            }
                            if (m != "off" && _vmCam != null && sc.Effects != null) LogViewmodelLights(_vmCam); // the lights on the gun at the capture
                            yield return new WaitForEndOfFrame();
                            // What the player sees (RT composited under transparents/post/HUD), plus the raw RT image.
                            string file = $"{Sanitize(sc.Name)}_{at:F1}s_{Sanitize(m)}.png";
                            ScreenCapture.CaptureScreenshot(Path.Combine(outDir, file));
                            HdrCapture(Path.Combine(outDir, file)); // -ultrart-hdr-capture: what the display was sent (Plugin.Hdr.cs)
                            sr.Screenshots.Add(file);
                            if (m != "off")
                            {
                                string raw = $"{Sanitize(sc.Name)}_{at:F1}s_{Sanitize(m)}_raw.png";
                                SaveOutput(Path.Combine(outDir, raw));
                                sr.Screenshots.Add(raw);
                            }
                            yield return null; // CaptureScreenshot completes at the end of this frame
                        }
                        }
                        if (wantBurst && sr.Bursts.Count == 0) yield return RunBurst(sc, sr, outDir, path, pathT);
                        if (sc.LightFit && !_benchNoShots) yield return RunLightFit(sc); // Plugin.LightFit.cs
                        if (sc.Motion > 0) yield return RunMotionNoise(sc, sr, outDir, path, at); // every screenshot time
                        current = null; // force a fresh switch below
                    }

                    var mode = sc.Modes[(int)(pathT / Mathf.Max(sc.Segment, 0.05f)) % sc.Modes.Count];
                    if (mode != current) { ApplyBenchMode(mode); current = mode; sinceSwitch = 0; windowOpen = false; }

                    if (sc.ReplayArenas && round == 0)
                        while (nextArena < arenas.Count && arenas[nextArena].Time <= pathT) TriggerArena(arenas[nextArena++]);

                    // The game re-applies its frame limiter on level load; keep the run uncapped.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    GameControl.SetPose(path.Sample(pathT));
                    GameControl.BobTime = pathT;
                    GameControl.HideCheatPanels();
                    if (sc.KillEnemies && Time.time >= nextKill)
                    {
                        nextKill = Time.time + 0.25f;
                        KillEnemies();
                    }
                    if (pathT >= nextEffect) { EffectSpawner.Spawn(sc.Effects, periodic: true); nextEffect = pathT + sc.Effects.Every; }
                    yield return null;

                    float dt = Time.unscaledDeltaTime;
                    pathT += dt;
                    // The previous frame's plugin sections and main-thread markers (dt is that frame's duration too).
                    Perf.Take(secMs);
                    Perf.LastFrame(out float scriptsMs, out float renderMs);
                    int gcNow = GC.CollectionCount(0);
                    long heapNow = GC.GetTotalMemory(false);
                    int gcd = gcNow - gc0;
                    float allocKb = gcd == 0 ? Math.Max(0, heapNow - heap) / 1024f : 0f;
                    gc0 = gcNow;
                    heap = heapNow;
                    // Not counted either: frames while DLSS RR warms up (NGX's start-up or a feature being created in the
                    // background: those frames run UltraRT's denoiser instead), so they can't fill a mode's measurement.
                    bool counted = ++sinceSwitch > sc.SwitchSkipFrames && !RRWarmingNow;
                    csv.Append(pathT.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(current).Append(counted ? "" : "*").Append(',')
                        .Append(F(dt * 1000f)).Append(',').Append(F(scriptsMs)).Append(',').Append(F(renderMs)).Append(',').Append(gcd).Append(',').Append(F(allocKb));
                    for (int k = 0; k < nSec; k++) csv.Append(',').Append(F(secMs[k]));
                    csv.Append(',').Append(_proxies?.Rtas?.GetInstanceCount() ?? 0).Append(',').Append(_proxies?.StaticRecordsActive ?? 0)
                        .Append(',').Append(_proxies?.ActiveDynamicCount ?? 0).Append('\n');
                    if (!counted) windowOpen = false;
                    if (counted)
                    {
                        double unixNow = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        var spans = windows[current];
                        if (!windowOpen) { spans.Add(new[] { unixNow - dt, unixNow }); windowOpen = true; }
                        else spans[spans.Count - 1][1] = unixNow;
                        for (int k = 0; k < nSec; k++) { cpuSum[current][k] += secMs[k]; cpuMax[current][k] = Math.Max(cpuMax[current][k], secMs[k]); }
                        mainSum[current][0] += scriptsMs;
                        mainSum[current][1] += renderMs;
                        gcCount[current] += gcd;
                        allocSum[current] += allocKb;
                        frames[current].Add(dt * 1000f);
                        // "off" too: the whole-frame slot is timed in every mode.
                        if (_gpu != null && _gpu.ResolvedFrames != lastResolved)
                        {
                            lastResolved = _gpu.ResolvedFrames;
                            for (int s = 0; s < SlotCount; s++)
                            {
                                if (_gpu.TryGetMs(s, out var ms) && ms > 0f) { gpuSum[current][s] += ms; gpuN[current][s]++; }
                                if (_gpu.TryGetCpuMs(s, out var cms) && cms >= 0f) { rtSum[current][s] += cms; rtN[current][s]++; }
                            }
                        }
                    }
                }
            }

            foreach (var m in sc.Modes)
            {
                var st = Stats(frames[m]);
                for (int s = 0; s < SlotCount; s++)
                {
                    if (gpuN[m][s] > 0) st.GpuMs[SlotNames[s]] = (float)(gpuSum[m][s] / gpuN[m][s]);
                    if (rtN[m][s] > 0) st.RenderThreadMs[SlotNames[s]] = (float)(rtSum[m][s] / rtN[m][s]);
                }
                int n = Math.Max(1, st.Frames);
                for (int k = 0; k < nSec; k++)
                {
                    st.CpuMs[Perf.Names[k]] = (float)(cpuSum[m][k] / n);
                    st.CpuMaxMs[Perf.Names[k]] = cpuMax[m][k];
                }
                st.ScriptsMs = (float)(mainSum[m][0] / n);
                st.RenderMs = (float)(mainSum[m][1] / n);
                st.GcCollections = gcCount[m];
                st.AllocKbPerFrame = (float)(allocSum[m] / n);
                st.WindowsUtc = windows[m];
                sr.Modes[m] = st;
            }
            WriteLightCensus(outDir); // -ultrart-light-census (Plugin.LightClasses.cs)
            WriteEnemySurvey(outDir); // -ultrart-enemy-survey (Plugin.EnemySurvey.cs)
            try { File.WriteAllText(Path.Combine(outDir, $"frames_{Sanitize(sc.Name)}.csv"), csv.ToString()); }
            catch (Exception e) { Logger.LogWarning($"Benchmark: frame log not written: {e.Message}"); }
            sr.Instances = (int)(_proxies?.Rtas?.GetInstanceCount() ?? 0);
            sr.Lights = _lightCount;
            ApplyBenchMode("off"); // the player stays taken over: a chained scenario may continue from here
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
            // Modes may carry runtime variants ("relight+preset=previous", Plugin.Burst.cs); undo the previous ones first.
            if (_benchModeVariant != null) { RestoreVariantState(_benchModeVariant); _benchModeVariant = null; Denoiser.FrameTimeOverride = 0f; }
            if (mode.Contains("+"))
            {
                _benchModeVariant = SaveVariantState();
                mode = ApplyBenchVariant(mode);
            }
            if (mode == "off") { _viewEnabled = false; return; }
            int i = ModeIndex(mode); // "hybrid" (removed) falls back to relight with a warning
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
            st.Over16 = s.Count(x => x > 1000f / 60f);
            st.Over33 = s.Count(x => x > 1000f / 30f);
            st.WorstMs = s.Skip(Math.Max(0, s.Count - 5)).Reverse().ToList();
            return st;
        }

        private static string F(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        private static string Summarize(SuiteResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# UltraRT benchmark: {r.Suite}{(string.IsNullOrEmpty(r.Tag) ? "" : $" ({r.Tag})")}");
            sb.AppendLine($"{r.Started} | {r.Gpu} | {r.Resolution} | trace scale {r.TraceScale}{(r.GpuTimer ? " | GPU timer" : "")}");
            sb.AppendLine($"{r.TotalSeconds:F0} s, {r.LevelLoads} level loads for {r.Scenarios.Count} scenarios ({(r.Chaining ? "chaining" : "no chaining")})");
            if (!string.IsNullOrEmpty(r.Options)) sb.AppendLine($"Options: {r.Options}");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Mode | Avg ms | fps | 1% low | p99 ms | worst ms | >16.7 ms | delta vs off | GPU ms (total/dispatch/denoise) | main ms (scripts/render) | plugin ms (avg/worst) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
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
                    sb.AppendLine($"| {sc.Name} | {kv.Key} | {m.AvgMs:F3} | {m.AvgFps:F0} | {m.Low1Fps:F0} | {m.P99Ms:F3} | {m.MaxMs:F1} | {m.Over16} | {delta} | {gpu} | " +
                                  $"{m.ScriptsMs:F2}/{m.RenderMs:F2} | {Get(m.CpuMs, "trace")}/{Get(m.CpuMaxMs, "trace")} |");
                }
            }
            sb.AppendLine();
            sb.AppendLine("Plugin CPU per section, ms per frame, average / worst frame (Perf.cs). GC = gen-0 collections, alloc = managed heap growth per frame:");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Mode | " + string.Join(" | ", Perf.Names) + " | GC | alloc KB/frame |");
            sb.AppendLine("|---|---|" + string.Concat(Perf.Names.Select(_ => "---|")) + "---|---|");
            foreach (var sc in r.Scenarios)
            {
                if (sc.Error != null) continue;
                foreach (var kv in sc.Modes)
                {
                    if (kv.Key == "off") continue;
                    var m = kv.Value;
                    sb.AppendLine($"| {sc.Name} | {kv.Key} | " + string.Join(" | ", Perf.Names.Select(n => $"{Get(m.CpuMs, n)}/{Get(m.CpuMaxMs, n)}")) +
                                  $" | {m.GcCollections} | {m.AllocKbPerFrame:F1} |");
                }
            }
            if (r.Scenarios.Any(s => s.Error == null && s.Modes.Values.Any(m => m.RenderThreadMs.Count > 0)))
            {
                // Passes in the order of their cost; "frame" is the whole frame (the game included), in every mode.
                var slots = SlotNames.Where(n => r.Scenarios.Any(s => s.Error == null && s.Modes.Values.Any(m => m.RenderThreadMs.ContainsKey(n) || m.GpuMs.ContainsKey(n)))).ToList();
                sb.AppendLine();
                sb.AppendLine("GPU ms / render-thread ms per pass (native timer: the render thread's own time between a pass's begin and end events). frame = the whole frame's rendering, the game included: its GPU time against the frame time says whether the GPU is the limit:");
                sb.AppendLine();
                sb.AppendLine("| Scenario | Mode | frame ms | " + string.Join(" | ", slots) + " |");
                sb.AppendLine("|---|---|---|" + string.Concat(slots.Select(_ => "---|")));
                foreach (var sc in r.Scenarios)
                {
                    if (sc.Error != null) continue;
                    foreach (var kv in sc.Modes)
                        sb.AppendLine($"| {sc.Name} | {kv.Key} | {kv.Value.AvgMs:F2} | " + string.Join(" | ", slots.Select(n => $"{Get2(kv.Value.GpuMs, n)}/{Get2(kv.Value.RenderThreadMs, n)}")) + " |");
                }
            }
            sb.AppendLine();
            sb.AppendLine($"How each scenario was reached (level loads {r.Scenarios.Sum(s => s.LevelLoadSeconds):F0} s, pose loads {r.Scenarios.Sum(s => s.LoadSeconds):F0} s in total):");
            sb.AppendLine();
            sb.AppendLine("| # | Scenario | Reached | Level load s | Pose load | Pose s | Scenario s |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var sc in r.Scenarios.Where(s => s.Reached != null).OrderBy(s => s.Order))
                sb.AppendLine($"| {sc.Order} | {sc.Name} | {sc.Reached} | {(sc.LevelLoadSeconds > 0 ? sc.LevelLoadSeconds.ToString("F1") : "-")} | " +
                              $"{sc.Load}{(sc.LoadDetail != null ? $" ({sc.LoadDetail})" : "")} | {sc.LoadSeconds:F1} | {sc.WallSeconds:F1} |");
            SummarizeBursts(sb, r);
            SummarizeMotion(sb, r);
            SummarizeCinematics(sb, r); // Plugin.Cinematics.Bench.cs
            return sb.ToString();

            string Get(Dictionary<string, float> d, string k) => d.TryGetValue(k, out var v) ? v.ToString("F3") : "-";
            string Get2(Dictionary<string, float> d, string k) => d.TryGetValue(k, out var v) ? v.ToString("F2") : "-";
        }

        private static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
    }
}
