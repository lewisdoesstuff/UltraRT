using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UltraRT.Bench;
using UltraRT.Cinematics;
using UnityEngine;

namespace UltraRT
{
    /// <summary>What an offline render did (render.json next to the frames, and the bench summary).</summary>
    public sealed class CineRenderResult
    {
        public string Path, Scene, Dir, Video, EncodeCommand, Error, Mode;
        public bool Cancelled;
        public int Frames, Written, Width, Height, SamplesPerFrame, SubframesPerFrame;
        public float Fps, PathSeconds, RenderSeconds, PrerollSeconds, SecondsPerFrame, MsPerSubframe, EncodeSeconds, WriterMsPerFrame;
        public CineSettings Settings;
    }

    /// <summary>
    /// Offline render of a camera path (docs/cinematics.md, "Render"). Per output frame, game time advances exactly
    /// 1 / fps (Time.captureDeltaTime: independent of how long a frame takes to render), then the frame is held
    /// (timeScale 0) while it is rendered AA x samples times:
    ///  - every AA sample is a sub-pixel offset of the camera's projection (the game's raster and our trace together), or with
    ///    motion blur a step of game time and camera time inside the shutter;
    ///  - within an AA sample the camera and the world are still, the denoiser and the reflection filter run as pure running
    ///    means (no spatial filter, no clamps, no history cap below 255, no stabilisation; RIS light sampling, no reservoir
    ///    reuse) and their history starts over at every AA sample, so the image converges to the path tracer's own mean;
    ///  - the last frame of each AA sample is grabbed (FrameSink: async readback, averaged on a worker thread, written as PNG).
    /// Exposure is either locked (as at the start) or metered once per output frame on the converged image with a 1 / fps
    /// clock, so it moves exactly as it would at that frame rate. Everything changed is put back in a finally block.
    /// </summary>
    public partial class Plugin
    {
        private bool _cineCancel, _cineRendering, _cinePreroll;
        private bool _cineMeterFrame;
        private float _cineMeterDt;
        private string _cineProgress;

        /// <summary>RunExposure: whether to measure this frame and with which clock (offline render only).</summary>
        private bool CineExposureGate(ref float dt)
        {
            if (!_cineRendering) return true;
            // Preroll: every frame, on a coarse clock, so exposure has settled (several seconds' worth) before frame 0.
            if (_cinePreroll) { dt = 0.1f; return true; }
            if (!_cineMeterFrame) return false;
            dt = _cineMeterDt;
            return true;
        }

        /// <summary>End (or the menu): render the current path with the menu's settings into BepInEx\ultrart\renders.</summary>
        private void CineStartRender()
        {
            if (_cineState == CineState.Off) CineEnter();
            if (_cineState == CineState.Off || _cineState == CineState.Render) return;
            if (_cineKeys.Count < 2) { Toast("A path needs two keys to render (K adds one)"); return; }
            if (_proxies == null) { Toast("The RT scene is not ready yet"); return; }
            string scene = SafeCurrentScene();
            string dir = Path.Combine(_outDir, "renders", $"{CineSanitize(_cineName ?? scene)}_{DateTime.Now:yyyyMMdd-HHmmss}");
            var result = new CineRenderResult();
            _host.StartCoroutine(CineRender(CineSplineNow(), _cine.Clone(), dir, _cineName, result, false));
        }

        private static Vector2 CineHalton(int i)
        {
            float H(int n, int b) { float f = 1f, r = 0f; while (n > 0) { f /= b; r += f * (n % b); n /= b; } return r; }
            return new Vector2(H(i + 1, 2) - 0.5f, H(i + 1, 3) - 0.5f);
        }

        /// <summary>The offline render (see the class comment). <paramref name="bench"/>: waits for ffmpeg before returning.</summary>
        private IEnumerator CineRender(CineSpline sp, CineSettings o, string dir, string name, CineRenderResult r, bool bench)
        {
            string scene = SafeCurrentScene();
            r.Scene = scene; r.Dir = dir; r.Path = name; r.Settings = o;
            var prevState = _cineState;
            _cineState = CineState.Render;
            _cineCancel = false;

            int modeIndex = o.mode == CineRenderMode.Pathtrace ? Array.FindIndex(Modes, m => m.name == PathTraceModeName)
                : o.mode == CineRenderMode.Relight ? Array.FindIndex(Modes, m => m.name == "relight") : _mode;
            if (modeIndex < 0) modeIndex = _mode;
            r.Mode = Modes[modeIndex].name;
            int J = Mathf.Clamp(o.aa, 1, 64), S = Mathf.Clamp(o.samples, 1, 255);
            float fps = Mathf.Clamp(o.fps, 1f, 240f), fdt = 1f / fps;
            float mbStep = o.motionBlur && J > 1 ? Mathf.Clamp01(o.shutter) * fdt / J : 0f;
            float g = Mathf.Max(0f, o.timeScale);
            float t0 = Mathf.Clamp(o.renderStart, 0f, sp.Duration);
            float t1 = o.renderSeconds > 0f ? Mathf.Min(sp.Duration, t0 + o.renderSeconds) : sp.Duration;
            int frames = Mathf.Max(1, Mathf.FloorToInt((t1 - t0) * fps + 1e-3f) + 1);
            r.Frames = frames; r.Fps = fps; r.PathSeconds = t1 - t0; r.SamplesPerFrame = J; r.SubframesPerFrame = J * S;

            // Everything we change, to put back.
            var snap = SaveVariantState();
            int savedMode = _mode;
            bool savedView = _viewEnabled, savedGuard = _guardActive, savedRR = _rrEnabled;
            float savedScale = _scale;
            float savedCapture = Time.captureDeltaTime;
            int savedVsync = QualitySettings.vSyncCount, savedTarget = Application.targetFrameRate;
            bool savedAudio = AudioListener.pause, savedBg = Application.runInBackground;
            bool savedUp = _upsampler != null && _upsampler.Settings.enabled;
            FrameSink sink = null;
            var total = Stopwatch.StartNew();
            Logger.LogInfo($"Cinematic render: '{name}' in {scene}, {r.Mode}, {frames} frames at {fps:F0} fps ({t0:F2}..{t1:F2}s of {sp.Duration:F2}s), " +
                           $"{J} {(mbStep > 0f ? "motion-blur" : "AA")} x {S} samples, paths {o.paths}, bounces {o.bounces}, output x{o.outputScale:F2}, exposure {o.exposure}, game speed {g:F2} -> {dir}");
            try
            {
                // ---- render settings: full resolution, no RR / upsampler / guard band, accumulating filters
                _mode = modeIndex;
                _viewEnabled = true;
                _scale = 1f;
                _guardActive = false;
                _rrEnabled = false;
                if (_upsampler != null) _upsampler.Settings.enabled = false;
                var pt = _ptSettings;
                pt.denoiser = PathTraceDenoiser.UltraRT;
                pt.scale = 1f;
                pt.remodulate = false;
                pt.paths = pt.pathsOwn = Mathf.Clamp(o.paths, 1, 4);
                pt.bounces = Mathf.Clamp(o.bounces, 1, 4);
                pt.cacheTail = o.cacheTail;
                if (_lightSamplingSettings.mode == LightSamplingMode.ReSTIR) _lightSamplingSettings.mode = LightSamplingMode.Ris;
                foreach (var dn in new[] { _denoiser?.Settings, _denoiserDirect?.Settings })
                {
                    if (dn == null) continue;
                    dn.iterations = 0; dn.alphaMin = 0f; dn.maxHistory = 255; dn.clampHistory = false;
                    dn.stability = false; dn.stabilize = false; dn.antilag = 0f; dn.moverAntilag = 0f;
                    dn.debugView = DenoiserDebugView.Final;
                }
                var rf = _reflSettings;
                rf.alphaMirror = rf.alphaRough = 0f; rf.clampMirror = rf.clampRough = 1e4f; rf.maxHistory = 255; rf.spatial = false; rf.temporal = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Application.runInBackground = true;
                AudioListener.pause = true;
                CineSetSpeed(g); // TimeController's modifier, so a hit stop ending mid-render restores this speed
                _cineRendering = true;

                // ---- preroll: the first pose, world frozen, exposure and radiance cache settle (and RayGenPT compiles)
                var pre = Stopwatch.StartNew();
                _cinePreroll = true;
                Time.captureDeltaTime = fdt;
                Time.timeScale = 0f;
                _cineCur = sp.Sample(t0);
                _cineJitter = Vector2.zero;
                CineCut();
                for (int i = 0; i < Mathf.Max(o.preroll, 2) || (_proxies != null && _proxies.DeferredCount > 0 && i < 600); i++)
                {
                    _cineProgress = $"Render '{name}': warming up {i + 1}/{o.preroll}";
                    Time.timeScale = 0f;
                    yield return null;
                    if (CineRenderAborted(scene)) break;
                }
                _cinePreroll = false;
                r.PrerollSeconds = (float)pre.Elapsed.TotalSeconds;

                // ---- frames
                sink = new FrameSink(dir, Screen.width, Screen.height, o.outputScale, J, o.png16);
                r.Width = sink.OutW; r.Height = sink.OutH;
                var loop = Stopwatch.StartNew();
                long subframes = 0;
                for (int f = 0; f < frames && !CineRenderAborted(scene); f++)
                {
                    // Don't run ahead of the writer (each queued frame holds a full-size float buffer).
                    while (sink.Pending >= FrameSink.MaxPending && sink.LastError == null) { Time.timeScale = 0f; yield return null; }
                    if (sink.LastError != null) throw new IOException("frame writer: " + sink.LastError);
                    sink.BeginFrame(f);
                    for (int j = 0; j < J; j++)
                    {
                        // This frame's game time was stepped by the frame before (see below); pose the camera to match.
                        float camT = t0 + f * fdt + j * mbStep;
                        _cineCur = sp.Sample(Mathf.Min(camT, sp.Duration));
                        _cineJitter = J > 1 ? CineHalton(j) : Vector2.zero;
                        // A new accumulation: the denoiser, the reflection filter and the light sampler start over.
                        _denoiser?.Reset();
                        _denoiserDirect?.Reset();
                        _reflFilter?.Reset();
                        _lightSampling?.Reset();
                        for (int s = 0; s < S; s++)
                        {
                            bool last = s == S - 1;
                            _cineMeterFrame = o.exposure == CineExposure.Auto && last && j == J - 1;
                            _cineMeterDt = fdt;
                            _cineHideGui = last;
                            // The next frame's game step: into the next motion-blur sub-step, or to the next output frame.
                            float next = !last ? 0f : j < J - 1 ? mbStep : fdt - (J - 1) * mbStep;
                            Time.captureDeltaTime = next > 0f ? next : fdt;
                            Time.timeScale = next > 0f ? g : 0f;
                            float elapsed = (float)loop.Elapsed.TotalSeconds;
                            float perFrame = f > 0 ? elapsed / f : 0f;
                            _cineProgress = $"Render '{name}': frame {f + 1}/{frames}, sample {j * S + s + 1}/{J * S}" +
                                            (f > 0 ? $"  |  {perFrame:F2} s/frame, {CineFormat.Time(perFrame * (frames - f))} left" : "") + "  |  End / Esc cancels";
                            if (!last) { yield return null; subframes++; continue; }
                            yield return new WaitForEndOfFrame();
                            sink.CaptureSample();
                            _cineHideGui = false;
                            yield return null;
                            subframes++;
                        }
                    }
                    if (f % 10 == 9 || f == frames - 1)
                        Logger.LogInfo($"Cinematic render: frame {f + 1}/{frames}, {loop.Elapsed.TotalSeconds / (f + 1):F3} s/frame, {loop.Elapsed.TotalMilliseconds / Math.Max(1, subframes):F2} ms/subframe, writer {sink.Written} written, {sink.Pending} pending");
                }
                r.Cancelled = _cineCancel || CineRenderAborted(scene);
                r.RenderSeconds = (float)loop.Elapsed.TotalSeconds;
                r.MsPerSubframe = (float)(loop.Elapsed.TotalMilliseconds / Math.Max(1, subframes));
                // The last readbacks and PNGs.
                Time.timeScale = 0f;
                float drainUntil = Time.realtimeSinceStartup + 120f;
                while (sink.Pending > 0 && sink.LastError == null && Time.realtimeSinceStartup < drainUntil) { _cineProgress = $"Render '{name}': writing {sink.Pending} frame(s)"; yield return null; }
                r.Written = sink.Written;
                r.SecondsPerFrame = r.Written > 0 ? r.RenderSeconds / r.Written : 0f;
                r.WriterMsPerFrame = r.Written > 0 ? (float)sink.EncodeMs / r.Written : 0f;
                if (sink.LastError != null) r.Error = sink.LastError;
            }
            finally
            {
                _cineRendering = false;
                _cinePreroll = false;
                _cineMeterFrame = false;
                _cineHideGui = false;
                _cineJitter = Vector2.zero;
                try { sink?.Dispose(); } catch (Exception e) { Logger.LogWarning($"Cinematic render: writer shutdown: {e.Message}"); }
                Time.captureDeltaTime = savedCapture;
                var tc = MonoSingleton<TimeController>.Instance;
                Time.timeScale = tc != null ? tc.timeScale * tc.timeScaleModifier : 1f;
                QualitySettings.vSyncCount = savedVsync;
                Application.targetFrameRate = savedTarget;
                AudioListener.pause = savedAudio;
                Application.runInBackground = savedBg;
                RestoreVariantState(snap);
                _mode = savedMode; _viewEnabled = savedView; _scale = savedScale; _guardActive = savedGuard; _rrEnabled = savedRR;
                if (_upsampler != null) _upsampler.Settings.enabled = savedUp;
                CineCut();
                if (_cineState == CineState.Render) _cineState = prevState == CineState.Off || prevState == CineState.Render ? CineState.Free : prevState == CineState.Play ? CineState.Free : prevState;
                _cineProgress = null;
                r.Error = r.Error ?? (r.Written == 0 && !r.Cancelled ? "no frames written" : null);
                Logger.LogInfo($"Cinematic render: settings restored ({r.Written} frames written{(r.Cancelled ? ", cancelled" : "")})");
            }

            // ---- encode
            if (r.Written > 0)
            {
                string video = Path.Combine(dir, $"{CineSanitize(name ?? scene)}.mp4");
                string args = FrameSink.EncodeArgs(dir, r.Fps, o.crf, video);
                r.EncodeCommand = "ffmpeg " + args;
                try { File.WriteAllText(Path.Combine(dir, "encode.cmd"), "@echo off\r\nrem UltraRT cinematic render: frames -> H.264\r\n" + r.EncodeCommand + "\r\n"); } catch { }
                string ffmpeg = o.encode ? FrameSink.FindFfmpeg() : null;
                if (o.encode && ffmpeg == null) Logger.LogWarning($"Cinematic render: ffmpeg not found on PATH; encode with: {r.EncodeCommand} (also in encode.cmd)");
                if (ffmpeg != null)
                {
                    var enc = Stopwatch.StartNew();
                    Process p = null;
                    try { p = FrameSink.StartEncode(ffmpeg, args, Path.Combine(dir, "ffmpeg.log")); }
                    catch (Exception e) { Logger.LogWarning($"Cinematic render: ffmpeg did not start ({e.Message}); encode.cmd has the command"); }
                    if (p != null)
                    {
                        if (!bench) Toast($"Render done: {r.Written} frames; encoding...");
                        float until = Time.realtimeSinceStartup + 600f;
                        while (!p.HasExited && Time.realtimeSinceStartup < until) yield return null;
                        r.EncodeSeconds = (float)enc.Elapsed.TotalSeconds;
                        if (p.HasExited && p.ExitCode == 0 && File.Exists(video)) r.Video = video;
                        else Logger.LogWarning($"Cinematic render: ffmpeg {(p.HasExited ? $"exit code {p.ExitCode}" : "timed out")}: see {Path.Combine(dir, "ffmpeg.log")}");
                    }
                }
            }
            try { File.WriteAllText(Path.Combine(dir, "render.json"), JsonConvert.SerializeObject(r, Formatting.Indented)); } catch { }
            string summary = $"Cinematic render {(r.Cancelled ? "cancelled" : "done")}: {r.Written}/{r.Frames} frames {r.Width}x{r.Height} in {r.RenderSeconds:F1}s " +
                             $"({r.SecondsPerFrame:F2} s/frame, {r.MsPerSubframe:F1} ms per sub-frame, {r.SubframesPerFrame} sub-frames per frame; preroll {r.PrerollSeconds:F1}s, " +
                             $"writer {r.WriterMsPerFrame:F0} ms/frame on its thread{(r.Video != null ? $", encoded in {r.EncodeSeconds:F1}s" : "")}) -> {r.Video ?? dir}{(r.Error != null ? $"; error: {r.Error}" : "")}";
            Logger.LogInfo(summary);
            if (!bench) Toast(r.Video != null ? $"Render done: {r.Video}" : $"Render done: {r.Written} frames in {dir}");
        }

        private bool CineRenderAborted(string scene) =>
            _cineCancel || _cineState != CineState.Render || SafeCurrentScene() != scene || !CineInLevel() || _proxies == null;
    }
}
