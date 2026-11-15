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
        private IEnumerator RunCinematicScenario(Scenario sc, ScenarioResult sr, string suiteDir, string outDir)
        {
            var spec = sc.Cinematic;
            string file = Path.IsPathRooted(spec.File) ? spec.File : Path.Combine(suiteDir, spec.File ?? "");
            var path = CinePathFile.Load(file);
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
                    var r = new CineRenderResult();
                    sr.Cinematic = r;
                    string dir = Path.Combine(outDir, $"cine_{Sanitize(sc.Name)}");
                    yield return CineRender(sp, o, dir, _cineName, r, bench: true);
                    if (r.Error != null) sr.Error = "cinematic render: " + r.Error;
                }
            }
            finally
            {
                CineExit();
            }
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
                var c = s.Cinematic;
                string play = p != null ? $"{p.AvgMs:F2} / {p.P99Ms:F2}" : "-";
                if (c == null) { sb.AppendLine($"| {s.Name} | {play} | - | | | | | | | | |"); continue; }
                sb.AppendLine($"| {s.Name} | {play} | {c.Mode} | {c.Written}/{c.Frames}{(c.Cancelled ? " (cancelled)" : "")} | {c.Width}x{c.Height} | {c.SubframesPerFrame} | " +
                              $"{c.SecondsPerFrame:F2} | {c.MsPerSubframe:F1} | {c.PrerollSeconds:F1} | {c.WriterMsPerFrame:F0} | {(c.Video != null ? Path.GetFileName(c.Video) : c.Error ?? "frames only")} |");
            }
        }
    }
}
