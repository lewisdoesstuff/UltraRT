using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UltraRT.Bench;
using UltraRT.Cinematics;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Bench scenarios with a "Cinematic" block (bench/README.md, "Cinematics"): the level is reached like a pose (Approach,
    /// Areas, Load), then the saved camera path plays in real time and/or renders offline into the run's results folder.
    /// Saves, prefs and Steam stats stay blocked by the bench (BenchSafety) for the whole run.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>Cinematic "Probes": the matching renderers now, and the outline mask of the next frames.</summary>
        private void CineProbe(string filter, float t, string outDir)
        {
            var cam = MonoSingleton<PostProcessV2_Handler>.Instance?.mainCam;
            Logger.LogInfo($"Benchmark: probe at {t:F2}s (frame {Time.frameCount}, traced view {_viewEnabled}), renderers matching '{filter}':");
            foreach (var line in _proxies.DescribeMatching(filter, cam)) Logger.LogInfo("  probe: " + line);
            _proxies.ProbeSkinnedExtents(filter, line => Logger.LogInfo("  probe: " + line));
            // Which cameras draw the matching skinned renderers in the next frames (OnWillRenderObject), and occlusion culling.
            if (cam != null) Logger.LogInfo($"  probe: main camera '{cam.name}' useOcclusionCulling={cam.useOcclusionCulling} cullingMask=0x{cam.cullingMask:X} near={cam.nearClipPlane} far={cam.farClipPlane}");
            foreach (var smr in FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (smr == null || SceneProxies.IsProxyObject(smr.gameObject) || GameControl.PathOf(smr.transform).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Logger.LogInfo($"  probe: '{GameControl.PathOf(smr.transform)}' allowOcclusionWhenDynamic={smr.allowOcclusionWhenDynamic} enabled={smr.enabled}");
                var w = smr.GetComponent<ProbeWillRender>() ?? smr.gameObject.AddComponent<ProbeWillRender>();
                w.Frames = 4;
                w.Log = Logger;
                if (cam == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                // What lies between the camera and this renderer (bounds on the ray to its centre and its box corners).
                var b = smr.bounds;
                var targets = new[] { b.center, b.min, b.max, new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.min.y, b.max.z) };
                var hits = new Dictionary<Renderer, float>();
                foreach (var tp in targets)
                {
                    var ray = new Ray(cam.transform.position, (tp - cam.transform.position).normalized);
                    float far = (tp - cam.transform.position).magnitude;
                    foreach (var r in FindObjectsOfType<Renderer>())
                    {
                        if (r == null || r == smr || !r.enabled || SceneProxies.IsProxyObject(r.gameObject) || ((cam.cullingMask >> r.gameObject.layer) & 1) == 0) continue;
                        if (r.bounds.IntersectRay(ray, out float d) && d < far && (!hits.TryGetValue(r, out var old) || d < old)) hits[r] = d;
                    }
                }
                foreach (var kv in hits.OrderBy(h => h.Value))
                {
                    var r = kv.Key;
                    Logger.LogInfo($"    in front of '{smr.name}': '{GameControl.PathOf(r.transform)}' {r.GetType().Name} L{r.gameObject.layer} d={kv.Value:F1} isVisible={r.isVisible} " +
                                   string.Join(" ", r.sharedMaterials.Where(m => m != null).Select(m => $"['{m.name}' {m.shader?.name} q={m.renderQueue} kw={string.Join(",", m.shaderKeywords)}]")));
                }
            }
            StartCoroutine(CineProbeMasks(t, outDir));
        }

        /// <summary>Probe helper: logs the cameras that are about to render this object, for a few frames.</summary>
        internal sealed class ProbeWillRender : MonoBehaviour
        {
            public int Frames;
            public BepInEx.Logging.ManualLogSource Log;
            private int _frame = -1;
            private readonly List<string> _cams = new List<string>();
            private void OnWillRenderObject()
            {
                if (Frames <= 0 || Log == null) return;
                if (_frame != Time.frameCount) { Flush(); _frame = Time.frameCount; }
                _cams.Add((Camera.current != null ? Camera.current.name : "null") + $" clip {Shader.GetGlobalVector("_PortalClipPlane"):G4}");
            }
            private void LateUpdate() { if (Frames > 0 && _frame >= 0 && _frame != Time.frameCount) Flush(); }
            private void Flush()
            {
                if (_frame < 0 || _cams.Count == 0) return;
                Log.LogInfo($"  probe: will render '{name}' frame {_frame}: cameras [{string.Join(",", _cams)}]");
                _cams.Clear();
                Frames--;
            }
        }

        private IEnumerator CineProbeMasks(float t, string outDir)
        {
            for (int i = 0; i < 4; i++)
            {
                yield return new WaitForEndOfFrame();
                var rt = MonoSingleton<PostProcessV2_Handler>.Instance?.reusableBufferA;
                if (rt == null) yield break;
                var prev = RenderTexture.active;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                    var px = tex.GetPixels32();
                    int outlined = 0;
                    for (int k = 0; k < px.Length; k++)
                    {
                        // ULTRAKILL/OutlinePx Composite1Px: a masked pixel has r > 0.999 or r + g > 1.
                        if (px[k].r >= 255 || px[k].r + px[k].g > 255) outlined++;
                        px[k].b = 0; px[k].a = 255;
                    }
                    tex.SetPixels32(px);
                    string file = $"probe_{t:F2}s_f{Time.frameCount}_{(_viewEnabled ? "traced" : "off")}.png";
                    File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                    var cam = MonoSingleton<PostProcessV2_Handler>.Instance?.mainCam;
                    string cbs = cam != null ? string.Join(",", cam.GetCommandBuffers(UnityEngine.Rendering.CameraEvent.AfterEverything).Select(c => c.name)) : "-";
                    Logger.LogInfo($"Benchmark: probe outline mask {file}: {rt.width}x{rt.height} {rt.format}, {outlined} masked pixels; " +
                                   $"global _OutlineTex '{Shader.GetGlobalTexture("_OutlineTex")?.name}' _OutlineTex_TexelSize {Shader.GetGlobalVector("_OutlineTex_TexelSize"):G4}, " +
                                   $"AfterEverything [{cbs}]");
                }
                finally { RenderTexture.active = prev; Destroy(tex); }
            }
        }

        private IEnumerator RunCinematicScenario(Scenario sc, ScenarioResult sr, string suiteDir, string outDir, CameraPath route, List<Recording.ArenaEvent> arenas)
        {
            var spec = sc.Cinematic;
            bool replay = string.IsNullOrEmpty(spec.File);
            if (replay && (sc.Path.Type ?? "").ToLowerInvariant() != "recording") throw new ArgumentException("Cinematic needs a File, or a Path of type recording to replay");
            string file = replay ? Path.Combine(suiteDir, sc.Path.File) : Path.IsPathRooted(spec.File) ? spec.File : Path.Combine(suiteDir, spec.File);
            // Recording replay: keys at 30 Hz along the recorded route (player feet + look angles; the camera rides at the eye).
            var path = replay ? RouteAsPath(route, Path.GetFileNameWithoutExtension(file)) : CinePathFile.Load(file);
            var o = _cine.Clone();
            if (!string.IsNullOrEmpty(spec.Mode)) o.mode = (CineRenderMode)Enum.Parse(typeof(CineRenderMode), spec.Mode, true);
            if (spec.Fps.HasValue) o.fps = spec.Fps.Value;
            if (spec.Samples.HasValue) o.samples = spec.Samples.Value;
            if (spec.Aa.HasValue) o.aa = spec.Aa.Value;
            if (spec.Paths.HasValue) o.paths = spec.Paths.Value;
            if (spec.Bounces.HasValue) o.bounces = spec.Bounces.Value;
            if (spec.CacheTail.HasValue) o.cacheTail = spec.CacheTail.Value;
            if (spec.OutputScale.HasValue) o.outputScale = spec.OutputScale.Value;
            if (spec.MotionBlur.HasValue) o.motionBlur = spec.MotionBlur.Value;
            if (spec.Shutter.HasValue) o.shutter = spec.Shutter.Value;
            if (!string.IsNullOrEmpty(spec.Exposure)) o.exposure = (CineExposure)Enum.Parse(typeof(CineExposure), spec.Exposure, true);
            if (spec.TimeScale.HasValue) o.timeScale = spec.TimeScale.Value;
            if (spec.Png16.HasValue) o.png16 = spec.Png16.Value;
            if (spec.Encode.HasValue) o.encode = spec.Encode.Value;
            if (spec.Crf.HasValue) o.crf = spec.Crf.Value;
            if (spec.Seconds.HasValue) o.renderSeconds = spec.Seconds.Value;
            if (spec.Start.HasValue) o.renderStart = spec.Start.Value;
            if (spec.Preroll.HasValue) o.preroll = spec.Preroll.Value;

            string live = sc.Modes.FirstOrDefault(m => m != "off") ?? "relight";
            ApplyBenchMode(live);
            CineEnter(bench: true);
            if (_cineState == CineState.Off) throw new InvalidOperationException("cinematic camera could not start (no player?)");
            if (spec.TracePlayer == true) SceneProxies.TracePlayerModel = _cineSavedPlayerModel; // V1 stands at the scenario's pose
            if (spec.ShowPlayer == true) CineShowPlayer(true);
            if (spec.ShopScreensOff == true) CineShopScreensOff(true);
            bool ride = !replay && string.Equals(spec.Player, "camera", StringComparison.OrdinalIgnoreCase);
            CineSpline walk = null;
            if (!replay && !ride && !string.IsNullOrEmpty(spec.Player))
            {
                // The player's own route: a path file whose keys are the player's position and view direction.
                var pf = CinePathFile.Load(Path.IsPathRooted(spec.Player) ? spec.Player : Path.Combine(suiteDir, spec.Player));
                foreach (var k in pf.Keys) k.Resolve(_cineEye);
                walk = new CineSpline(pf.Keys, CineDefaultFov());
                if (walk.Count < 2) throw new InvalidOperationException($"player path {spec.Player} has {walk.Count} key(s)");
            }
            int nextArena = 0;
            float nextKill = 0f;
            if (replay)
            {
                // Game time and the route stay in step: the player goes where it was at that time, recorded arenas start.
                _cineOnTime = t =>
                {
                    GameControl.SetPose(route.Sample(t));
                    if (sc.ReplayArenas)
                        while (nextArena < arenas.Count && arenas[nextArena].Time <= t) TriggerArena(arenas[nextArena++]);
                    if (sc.KillEnemies && Time.time >= nextKill) { nextKill = Time.time + 0.25f; KillEnemies(); }
                };
                SceneProxies.TracePlayerModel = _cineSavedPlayerModel; // V1 is where the camera is again: keep its shadow
            }
            var flickers = new List<(Flicker lamp, FlickerHold hold)>();
            var steady = new List<(Light light, FlickerHold hold, bool was)>();
            try
            {
                foreach (var k in path.Keys) k.Resolve(_cineEye);
                _cineKeys.Clear();
                _cineKeys.AddRange(path.Keys);
                _cineName = string.IsNullOrEmpty(path.Name) ? Path.GetFileNameWithoutExtension(file) : path.Name;
                _cineMeta = path;
                _cineSplineDirty = true;
                var sp = CineSplineNow();
                if (sp.Count < 2) throw new InvalidOperationException($"path {file} has {sp.Count} key(s)");
                Logger.LogInfo($"Benchmark: cinematic '{_cineName}' ({sp.Count} keys, {sp.Duration:F2}s, eye offset {_cineEye}) from {file}");
                if (ride || walk != null)
                {
                    // The player follows a route in step with the path time: the camera's own (a first-person walk), or
                    // its own while the camera watches (third person). V1 stays in the traced scene and walks.
                    var playerRoute = ride ? sp : walk;
                    _cineOnTime = t => CineDrivePlayer(playerRoute, t);
                    SceneProxies.TracePlayerModel = _cineSavedPlayerModel;
                    if (spec.ShowPlayer ?? !ride) CineShowPlayer(true);
                    CineDrivePlayer(playerRoute, 0f);
                    Logger.LogInfo($"Benchmark: the player {(ride ? "rides the camera path" : $"walks {spec.Player} ({walk.Duration:F2}s)")}, shown to the camera: {_cineShowPlayer}");
                }

                if (spec.ShowWeapon == true) CineShowWeapon();
                if (spec.Shots != null && spec.Shots.Count > 0)
                {
                    var shots = spec.Shots.OrderBy(x => x).ToList();
                    var fx = new EffectSpec { Kinds = spec.ShotKinds != null && spec.ShotKinds.Count > 0 ? spec.ShotKinds : new List<string> { "revolver" }, Every = 0f };
                    var before = _cineOnTime;
                    int next = 0;
                    float last = float.MinValue;
                    _cineOnTime = t =>
                    {
                        before?.Invoke(t);
                        if (t < last - 0.5f) next = 0; // a new take
                        last = t;
                        while (next < shots.Count && shots[next] <= t)
                        {
                            if (t - shots[next] < 0.25f) EffectSpawner.Spawn(fx); // not the ones a late start has passed
                            next++;
                        }
                    };
                    Logger.LogInfo($"Benchmark: cinematic shots ({string.Join(", ", fx.Kinds)}) at {string.Join(", ", shots.Select(x => x.ToString("F2")))} s");
                }

                if (spec.Flickers != null && spec.Flickers.Count > 0)
                {
                    // Flicker timing: a lamp is put into (and kept in) the wanted phase with the game's own On() / Off()
                    // while a hold covers the path time, and flickers by itself again after it.
                    var all = FindObjectsOfType<Flicker>();
                    foreach (var f in all.OrderBy(f => (f.transform.position - GameControl.Player.transform.position).sqrMagnitude).Take(12))
                        Logger.LogInfo($"Benchmark: flickering lamp '{GameControl.PathOf(f.transform)}' at {f.transform.position:F1}, delay {f.delay:F2} +- {f.timeRandomizer:F2}s, quick {f.quickFlicker}");
                    foreach (var h in spec.Flickers)
                    {
                        var lamps = all.Where(f => GameControl.PathOf(f.transform).EndsWith(h.Light ?? "?", StringComparison.OrdinalIgnoreCase)).ToList();
                        foreach (var f in lamps) flickers.Add((f, h));
                        int n = 0;
                        if (lamps.Count == 0) // not a flickering lamp: a steady light, switched for the stretch
                            foreach (var li in FindObjectsOfType<Light>().Where(l => GameControl.PathOf(l.transform).EndsWith(h.Light ?? "?", StringComparison.OrdinalIgnoreCase)))
                            {
                                steady.Add((li, h, li.enabled));
                                n++;
                            }
                        Logger.LogInfo($"Benchmark: lamp hold '{h.Light}' {(h.On ? "on" : "off")} {h.From:F2}..{h.To:F2}s: {lamps.Count} flickering lamp(s), {n} steady light(s)");
                    }
                    var before = _cineOnTime;
                    var held = new HashSet<Flicker>();
                    _cineOnTime = t =>
                    {
                        before?.Invoke(t);
                        foreach (var (light, hold, was) in steady)
                            if (light != null) light.enabled = t >= hold.From && t < hold.To ? hold.On : was;
                        var now = new Dictionary<Flicker, bool>();
                        foreach (var (lamp, hold) in flickers)
                            if (lamp != null && t >= hold.From && t < hold.To) now[lamp] = hold.On;
                        foreach (var kv in now)
                        {
                            var li = kv.Key.GetComponent<Light>();
                            bool on = li != null && li.intensity != 0f;
                            if (held.Add(kv.Key) || on != kv.Value)
                            {
                                kv.Key.CancelInvoke();
                                if (kv.Value) kv.Key.On(); else kv.Key.Off();
                            }
                        }
                        foreach (var lamp in held.Where(l => !now.ContainsKey(l)).ToList())
                        {
                            held.Remove(lamp);
                            if (lamp != null && lamp.isActiveAndEnabled) lamp.Invoke("Flickering", 0.05f); // on its own again
                        }
                    };
                }

                if (spec.Calls != null && spec.Calls.Count > 0)
                {
                    // Game events on cue (5-2's lightning): a component's own method at a path second, once per take.
                    var calls = spec.Calls.OrderBy(c => c.At).ToList();
                    foreach (var c in calls)
                    {
                        var found = FindObjectsOfType<MonoBehaviour>().Where(b => b != null && b.GetType().Name == c.Type).ToList();
                        Logger.LogInfo($"Benchmark: call {c.Type}.{c.Method} at {c.At:F2}s (index {c.Index}): {found.Count} active component(s)" +
                                       string.Concat(found.Take(8).Select(b => $"; '{GameControl.PathOf(b.transform)}' at {b.transform.position:F0}")));
                        if (found.Count == 0) // what the level has instead (lightning, thunder, flash, storm components)
                            Logger.LogInfo("Benchmark: call: components like it here: " + string.Join(", ", FindObjectsOfType<MonoBehaviour>()
                                .Where(b => b != null && System.Text.RegularExpressions.Regex.IsMatch(b.GetType().Name, "ightning|hunder|Flash|Storm|Weather"))
                                .Select(b => b.GetType().Name).Distinct()));
                    }
                    var before = _cineOnTime;
                    int next = 0;
                    float last = float.MinValue;
                    _cineOnTime = t =>
                    {
                        before?.Invoke(t);
                        if (t < last - 0.5f) next = 0; // a new take
                        last = t;
                        while (next < calls.Count && calls[next].At <= t)
                        {
                            if (t - calls[next].At < 0.25f) CineCallNow(calls[next]); // not the ones a late start has passed
                            next++;
                        }
                    };
                }

                if (spec.Probes != null && spec.Probes.Count > 0 && !string.IsNullOrEmpty(spec.ProbeFilter) && _proxies != null)
                {
                    // Diagnostics (renderers matching ProbeFilter and the outline mask) at path seconds, once per take.
                    var probes = spec.Probes.OrderBy(x => x).ToList();
                    var before = _cineOnTime;
                    int next = 0;
                    float last = float.MinValue;
                    _cineOnTime = t =>
                    {
                        before?.Invoke(t);
                        if (t < last - 0.5f) next = 0;
                        last = t;
                        while (next < probes.Count && probes[next] <= t)
                        {
                            if (t - probes[next] < 0.25f) CineProbe(spec.ProbeFilter, t, outDir);
                            next++;
                        }
                    };
                }

                if (spec.Play)
                {
                    // Real-time playback in the scenario's RT mode, as the player previews it: frame times, a screenshot halfway.
                    _cineCur = sp.Sample(0f);
                    _cineState = CineState.Play;
                    CineCut();
                    for (int f = 0; f < 30; f++) yield return null; // histories, exposure
                    var ms = new List<float>(4096);
                    float t = 0f;
                    bool shot = _benchNoShots;
                    CineSetSpeed(o.timeScale);
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = -1;
                    while (t <= sp.Duration)
                    {
                        _cineCur = sp.Sample(t);
                        _cineOnTime?.Invoke(t);
                        if (!shot && t >= sp.Duration * 0.5f)
                        {
                            shot = true;
                            yield return new WaitForEndOfFrame();
                            string png = $"{Sanitize(sc.Name)}_play_{t:F1}s_{Sanitize(live)}.png";
                            ScreenCapture.CaptureScreenshot(Path.Combine(outDir, png));
                            sr.Screenshots.Add(png);
                        }
                        yield return null;
                        float dt = Time.unscaledDeltaTime;
                        ms.Add(dt * 1000f);
                        t += dt;
                    }
                    CineSetSpeed(-1f);
                    sr.CinematicPlay = Stats(ms);
                    sr.Duration = sp.Duration;
                    Logger.LogInfo($"Benchmark: cinematic playback in {live}: {ms.Count} frames, avg {sr.CinematicPlay.AvgMs:F2} ms, p99 {sr.CinematicPlay.P99Ms:F2} ms, worst {sr.CinematicPlay.MaxMs:F1} ms");
                    _cineState = CineState.Free;
                }

                if (spec.Render)
                {
                    // One take, or one per entry of Modes. The game speed set by the first take (CineSetSpeed) stays until the
                    // camera is left, so with TimeScale 0 nothing in the world moves between the takes either.
                    var takes = spec.Modes != null && spec.Modes.Count > 0 ? spec.Modes : new List<string> { null };
                    foreach (var take in takes)
                    {
                        var ot = o.Clone();
                        if (take != null) ot.mode = (CineRenderMode)Enum.Parse(typeof(CineRenderMode), take, true);
                        var r = new CineRenderResult();
                        sr.Cinematic = r;
                        if (takes.Count > 1) (sr.CinematicTakes ?? (sr.CinematicTakes = new List<CineRenderResult>())).Add(r);
                        string dir = Path.Combine(outDir, $"cine_{Sanitize(sc.Name)}" + (take != null ? "_" + Sanitize(take.ToLowerInvariant()) : ""));
                        if (spec.Paired && ot.mode != CineRenderMode.Off)
                        {
                            if (take == null) dir += "_" + ot.mode.ToString().ToLowerInvariant();
                            ot.pairOffDir = Path.Combine(outDir, $"cine_{Sanitize(sc.Name)}_off");
                            if (spec.OnFrom.HasValue) ot.pairOnFrom = spec.OnFrom.Value;
                            if (spec.OffUntil.HasValue) ot.pairOffUntil = spec.OffUntil.Value;
                        }
                        yield return CineRender(sp, ot, dir, _cineName, r, bench: true);
                        if (r.Error != null) { sr.Error = "cinematic render: " + r.Error; break; }
                        _cineState = CineState.Free;
                    }
                }

                if (spec.Takes != null && spec.Takes.Count > 0 && sr.Error == null)
                {
                    // Takes with their own view mode and sample counts (aliasing measurement: live frames, references and
                    // debug views of one frozen instant). The view mode is the bench's, variants included.
                    foreach (var take in spec.Takes)
                    {
                        var ot = o.Clone();
                        if (!string.IsNullOrEmpty(take.Mode)) ot.mode = (CineRenderMode)Enum.Parse(typeof(CineRenderMode), take.Mode, true);
                        if (take.Aa.HasValue) ot.aa = take.Aa.Value;
                        if (take.Samples.HasValue) ot.samples = take.Samples.Value;
                        if (take.Bounces.HasValue) ot.bounces = take.Bounces.Value;
                        if (take.Start.HasValue) ot.renderStart = take.Start.Value;
                        if (take.Seconds.HasValue) ot.renderSeconds = take.Seconds.Value;
                        ot.encode = false;
                        ApplyBenchMode(string.IsNullOrEmpty(take.View) ? live : take.View);
                        var r = new CineRenderResult();
                        sr.Cinematic = r;
                        (sr.CinematicTakes ?? (sr.CinematicTakes = new List<CineRenderResult>())).Add(r);
                        string name = string.IsNullOrEmpty(take.Name) ? (take.View ?? live) + "_" + ot.mode : take.Name;
                        string dir = Path.Combine(outDir, $"cine_{Sanitize(sc.Name)}_{Sanitize(name)}");
                        Logger.LogInfo($"Benchmark: cinematic take '{name}': {ot.mode} of {take.View ?? live}, AA {ot.aa}, samples {ot.samples}, start {ot.renderStart:F3}s, {(ot.renderSeconds > 0f ? ot.renderSeconds.ToString("F3") + "s" : "to the end")}");
                        yield return CineRender(sp, ot, dir, _cineName, r, bench: true);
                        _cineState = CineState.Free;
                        if (r.Error != null) { sr.Error = $"cinematic take '{name}': " + r.Error; break; }
                    }
                    ApplyBenchMode(live);
                }
            }
            finally
            {
                _cineOnTime = null;
                // Lamps a hold stopped flicker by themselves again; steady lights go back to what they were.
                foreach (var (light, _, was) in steady) if (light != null) light.enabled = was;
                foreach (var lamp in flickers.Select(f => f.lamp).Distinct())
                    if (lamp != null && lamp.isActiveAndEnabled && !lamp.IsInvoking("Flickering")) lamp.Invoke("Flickering", 0.05f);
                CineExit();
            }
        }

        /// <summary>One <see cref="CineCall"/>: the method on the active components of that type (all, or the Index-th
        /// nearest the camera).</summary>
        private void CineCallNow(CineCall c)
        {
            var cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var found = FindObjectsOfType<MonoBehaviour>().Where(b => b != null && b.GetType().Name == c.Type)
                .OrderBy(b => (b.transform.position - cam).sqrMagnitude).ToList();
            if (c.Index >= 0) found = found.Skip(c.Index).Take(1).ToList();
            int n = 0;
            foreach (var b in found)
            {
                var m = b.GetType().GetMethod(c.Method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                                        System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null) { Logger.LogWarning($"Benchmark: call: {c.Type} has no parameterless {c.Method}"); return; }
                try { m.Invoke(b, null); n++; }
                catch (Exception e) { Logger.LogWarning($"Benchmark: call {c.Type}.{c.Method} on '{b.name}': {e.InnerException?.Message ?? e.Message}"); }
            }
            Logger.LogInfo($"Benchmark: called {c.Type}.{c.Method} on {n} component(s) at {c.At:F2}s");
        }

        private static CinePathFile RouteAsPath(CameraPath route, string name)
        {
            var f = new CinePathFile { Name = name, Scene = SafeCurrentScene() };
            const float step = 1f / 30f;
            int n = Mathf.Max(2, Mathf.CeilToInt(route.Duration / step) + 1);
            for (int i = 0; i < n; i++)
            {
                float t = Mathf.Min(route.Duration, i * step);
                var p = route.Sample(t);
                float next = Mathf.Min(route.Duration, (i + 1) * step);
                f.Keys.Add(new CineKey
                {
                    Player = new[] { p.Position.x, p.Position.y, p.Position.z }, Yaw = p.Yaw, Pitch = p.Pitch,
                    Duration = Mathf.Max(next - t, 1e-3f), Ease = "linear",
                });
            }
            return f;
        }

        private static void SummarizeCinematics(StringBuilder sb, SuiteResult r)
        {
            var list = r.Scenarios.Where(s => s.Cinematic != null || s.CinematicPlay != null).ToList();
            if (list.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine("Cinematics (docs/cinematics.md): live playback frame times, and the offline render (s/frame = wall time per output frame, " +
                          "sub-frames = AA samples x accumulated frames):");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Playback avg / p99 ms | Render mode | Frames | Size | Sub-frames | s/frame | ms/sub-frame | Preroll s | Writer ms/frame | Video |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var s in list)
            {
                var p = s.CinematicPlay;
                string play = p != null ? $"{p.AvgMs:F2} / {p.P99Ms:F2}" : "-";
                if (s.Cinematic == null) { sb.AppendLine($"| {s.Name} | {play} | - | | | | | | | | |"); continue; }
                foreach (var c in s.CinematicTakes ?? new List<CineRenderResult> { s.Cinematic })
                    sb.AppendLine($"| {s.Name} | {play} | {c.Mode} | {c.Written}/{c.Frames}{(c.Cancelled ? " (cancelled)" : "")} | {c.Width}x{c.Height} | {c.SubframesPerFrame} | " +
                                  $"{c.SecondsPerFrame:F2} | {c.MsPerSubframe:F1} | {c.PrerollSeconds:F1} | {c.WriterMsPerFrame:F0} | {(c.Video != null ? Path.GetFileName(c.Video) : c.Error ?? "frames only")} |");
            }
        }
    }
}
