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
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i].ToLowerInvariant())
                {
                    case "-ultrart-bench": _benchSuitePath = a[i + 1]; break;
                    case "-ultrart-bench-out": _benchOut = a[i + 1]; break;
                    case "-ultrart-bench-tag": _benchTag = a[i + 1]; break;
                    case "-ultrart-bench-load": _benchLoad = a[i + 1]; break;
                    case "-ultrart-bench-validate-methods": _benchValidateMethods = a[i + 1].Split(','); break;
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
            PoseLoader.Log = Logger;
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
            bool skippedIntro = false;
            while (SafeCurrentScene() != "Main Menu" || SceneHelper.PendingScene != null)
            {
                // Skip the intro sequence: jump straight to the main menu once the intro scene is up.
                if (!skippedIntro && SafeCurrentScene() == "Intro" && SceneHelper.PendingScene == null) { skippedIntro = true; SceneHelper.LoadScene("Main Menu"); }
                if (Time.realtimeSinceStartup - t0 > 120f) { Logger.LogError("Benchmark: timed out waiting for main menu"); yield break; }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2f);

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
                if (failed) { _level = null; GameControl.ReleaseControl(); }
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

            if (_quit)
            {
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit();
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
            _denoiserDirect?.Reset();
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
            yield return new WaitForSecondsRealtime(sc.Settle);

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
                            killed += KillEnemies();
                        }
                        yield return null;
                    }
                }
                finally
                {
                    _host.OnFixed = null;
                    if (gameSpeed > 1f) PoseLoader.SetGameSpeed(1f);
                }
                if (killed > 0) Logger.LogInfo($"Benchmark: approach killed {killed} enemies");
                res.Spawn = route.Sample(route.Duration);
                GameControl.SetPose(res.Spawn);
                yield return new WaitForSecondsRealtime(1.5f); // let area activation settle
            }
            res.Seconds = Time.realtimeSinceStartup - t0;
            Logger.LogInfo($"Benchmark: pose loaded via {method} in {res.Seconds:F1}s{(res.Detail != null ? $" ({res.Detail})" : "")}");
        }

        private static int KillEnemies()
        {
            int killed = 0;
            var tracker = MonoSingleton<EnemyTracker>.Instance;
            if (tracker != null)
                foreach (var e in tracker.GetCurrentEnemies().ToList())
                    if (e != null && !e.dead) { e.InstaKill(); killed++; }
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
            var load = new PoseLoad();
            yield return LoadPose(sc, method, suiteDir, load, _level.Spawn, routeFrom);
            sr.Load = load.Method;
            sr.LoadSeconds = load.Seconds;
            sr.LoadDetail = load.Detail;
            var spawn = load.Spawn;
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
            sb.AppendLine($"{r.TotalSeconds:F0} s, {r.LevelLoads} level loads for {r.Scenarios.Count} scenarios ({(r.Chaining ? "chaining" : "no chaining")})");
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
            sb.AppendLine();
            sb.AppendLine($"How each scenario was reached (level loads {r.Scenarios.Sum(s => s.LevelLoadSeconds):F0} s, pose loads {r.Scenarios.Sum(s => s.LoadSeconds):F0} s in total):");
            sb.AppendLine();
            sb.AppendLine("| # | Scenario | Reached | Level load s | Pose load | Pose s | Scenario s |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var sc in r.Scenarios.Where(s => s.Reached != null).OrderBy(s => s.Order))
                sb.AppendLine($"| {sc.Order} | {sc.Name} | {sc.Reached} | {(sc.LevelLoadSeconds > 0 ? sc.LevelLoadSeconds.ToString("F1") : "-")} | " +
                              $"{sc.Load}{(sc.LoadDetail != null ? $" ({sc.LoadDetail})" : "")} | {sc.LoadSeconds:F1} | {sc.WallSeconds:F1} |");
            return sb.ToString();

            string Get(Dictionary<string, float> d, string k) => d.TryGetValue(k, out var v) ? v.ToString("F3") : "-";
        }

        private static string Sanitize(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
    }
}
