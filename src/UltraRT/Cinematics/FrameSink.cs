using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UltraRT.Cinematics
{
    /// <summary>
    /// Offline render output (docs/cinematics.md, "Render"). The game's final frame (the back buffer: RT composite, the
    /// game's own transparents and post-processing) is copied into a render texture at the end of a frame and read back
    /// asynchronously, so the GPU never waits for the CPU. A worker thread averages the anti-aliasing / motion-blur samples
    /// of each output frame (float), box-filters it down to the output size (supersampling), and writes a PNG (8 or 16 bit).
    /// The main thread only waits when more than <see cref="MaxPending"/> frames are queued.
    /// </summary>
    internal sealed class FrameSink : IDisposable
    {
        public readonly string Dir;
        public readonly int SrcW, SrcH, OutW, OutH, SamplesPerFrame;
        public readonly bool Sixteen, FlipY;
        public int Written => _written;
        public int Pending => _pendingFrames;
        public string LastError;
        public double EncodeMs => _encodeTicks * 1000.0 / Stopwatch.Frequency;
        public const int MaxPending = 3;

        private RenderTexture _grab;
        private readonly Thread _worker;
        private readonly BlockingCollection<Job> _jobs = new BlockingCollection<Job>(new ConcurrentQueue<Job>());
        private readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        private int _written, _pendingFrames;
        private long _encodeTicks;
        private Frame _current;
        private readonly object _lock = new object();

        private sealed class Frame
        {
            public int Index, Received, Valid, Expected, Captured;
            public float[] Accum;      // RGB float (only with several samples)
            public byte[] Single;      // the only sample (RGBA32) when Expected == 1
        }

        private struct Job
        {
            public Frame Frame;
            public byte[] Data;  // one RGBA32 sample (pooled)
        }

        public FrameSink(string dir, int srcW, int srcH, float outScale, int samplesPerFrame, bool sixteen)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            SrcW = srcW; SrcH = srcH;
            // Even sizes: yuv420p (H.264) needs them.
            OutW = Math.Max(2, (int)Math.Round(srcW * Mathf.Clamp(outScale, 0.1f, 1f) / 2.0) * 2);
            OutH = Math.Max(2, (int)Math.Round(srcH * Mathf.Clamp(outScale, 0.1f, 1f) / 2.0) * 2);
            SamplesPerFrame = Math.Max(1, samplesPerFrame);
            Sixteen = sixteen;
            // CaptureScreenshotIntoRenderTexture fills the texture upside down where UVs start at the top (D3D).
            FlipY = SystemInfo.graphicsUVStartsAtTop;
            _grab = new RenderTexture(srcW, srcH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "UltraRT Cine Grab" };
            _grab.Create();
            _worker = new Thread(Work) { IsBackground = true, Name = "UltraRT cine writer" };
            _worker.Start();
        }

        /// <summary>Start output frame <paramref name="index"/> (its samples follow with <see cref="CaptureSample"/>).</summary>
        public void BeginFrame(int index)
        {
            _current = new Frame { Index = index, Expected = SamplesPerFrame };
            Interlocked.Increment(ref _pendingFrames);
        }

        /// <summary>Call from WaitForEndOfFrame: grab the back buffer and queue its readback as a sample of the current frame.</summary>
        public void CaptureSample()
        {
            var frame = _current ?? throw new InvalidOperationException("BeginFrame first");
            frame.Captured++;
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_grab);
            AsyncGPUReadback.Request(_grab, 0, TextureFormat.RGBA32, r =>
            {
                if (r.hasError)
                {
                    LastError = "GPU readback failed";
                    try { _jobs.Add(new Job { Frame = frame, Data = null }); } catch (InvalidOperationException) { }
                    return;
                }
                var src = r.GetData<byte>();
                if (!_pool.TryTake(out var buf) || buf.Length != src.Length) buf = new byte[src.Length];
                src.CopyTo(buf);
                try { _jobs.Add(new Job { Frame = frame, Data = buf }); } catch (InvalidOperationException) { } // disposed meanwhile
            });
        }

        /// <summary>A cancelled render: the current frame will never get all its samples; stop counting it as pending.</summary>
        public void AbandonFrame()
        {
            var f = _current;
            _current = null;
            if (f != null && f.Captured < f.Expected) Interlocked.Decrement(ref _pendingFrames);
        }

        private void Work()
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try { Consume(job); }
                catch (Exception e) { LastError = e.Message; Interlocked.Decrement(ref _pendingFrames); }
            }
        }

        private void Consume(Job job)
        {
            var f = job.Frame;
            if (job.Data == null) { } // a failed readback: the frame averages the samples that arrived
            else if (f.Expected == 1) { f.Single = job.Data; f.Valid++; }
            else
            {
                if (f.Accum == null) f.Accum = new float[SrcW * SrcH * 3];
                var a = f.Accum;
                var d = job.Data;
                for (int i = 0, j = 0; i < a.Length; i += 3, j += 4)
                {
                    a[i] += d[j]; a[i + 1] += d[j + 1]; a[i + 2] += d[j + 2];
                }
                _pool.Add(d);
                f.Valid++;
            }
            if (++f.Received < f.Expected) return;
            // Resolve, encode and write on the thread pool: PNG encoding (~0.4 s at 1440p) would otherwise pace the render.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                long t0 = Stopwatch.GetTimestamp();
                try { if (f.Valid > 0) WriteFrame(f); }
                catch (Exception e) { LastError = e.Message; }
                Interlocked.Add(ref _encodeTicks, Stopwatch.GetTimestamp() - t0);
                if (f.Single != null) { _pool.Add(f.Single); f.Single = null; }
                f.Accum = null;
                Interlocked.Increment(ref _written);
                Interlocked.Decrement(ref _pendingFrames);
            });
        }

        /// <summary>Average, box-filter to the output size, flip, encode, write.</summary>
        private void WriteFrame(Frame f)
        {
            int ch = Sixteen ? 4 : 3;
            int bpc = Sixteen ? 2 : 1;
            var outBytes = new byte[OutW * OutH * ch * bpc];
            float inv = 1f / Math.Max(1, f.Valid);
            float sx = (float)SrcW / OutW, sy = (float)SrcH / OutH;
            bool identity = OutW == SrcW && OutH == SrcH;
            var acc = f.Accum;
            var one = f.Single;
            float Src(int x, int y, int c) => acc != null ? acc[(y * SrcW + x) * 3 + c] * inv : one[(y * SrcW + x) * 4 + c];
            for (int oy = 0; oy < OutH; oy++)
            {
                // The readback and the encoder share Unity's texture row order; only the grab itself may be upside down.
                int dstRow = FlipY ? OutH - 1 - oy : oy;
                for (int ox = 0; ox < OutW; ox++)
                {
                    float r, g, b;
                    if (identity) { r = Src(ox, oy, 0); g = Src(ox, oy, 1); b = Src(ox, oy, 2); }
                    else
                    {
                        // Area-weighted box filter over the source footprint of this output pixel.
                        float x0 = ox * sx, x1 = x0 + sx, y0 = oy * sy, y1 = y0 + sy;
                        float rs = 0, gs = 0, bs = 0, ws = 0;
                        for (int y = (int)y0; y < Mathf.Min(SrcH, Mathf.CeilToInt(y1)); y++)
                        {
                            float wy = Mathf.Min(y + 1, y1) - Mathf.Max(y, y0);
                            for (int x = (int)x0; x < Mathf.Min(SrcW, Mathf.CeilToInt(x1)); x++)
                            {
                                float w = wy * (Mathf.Min(x + 1, x1) - Mathf.Max(x, x0));
                                rs += Src(x, y, 0) * w; gs += Src(x, y, 1) * w; bs += Src(x, y, 2) * w; ws += w;
                            }
                        }
                        r = rs / ws; g = gs / ws; b = bs / ws;
                    }
                    int o = (dstRow * OutW + ox) * ch * bpc;
                    if (Sixteen)
                    {
                        Put16(outBytes, o, r); Put16(outBytes, o + 2, g); Put16(outBytes, o + 4, b);
                        outBytes[o + 6] = 0xFF; outBytes[o + 7] = 0xFF;
                    }
                    else
                    {
                        outBytes[o] = (byte)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255);
                        outBytes[o + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255);
                        outBytes[o + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255);
                    }
                }
            }
            var fmt = Sixteen ? GraphicsFormat.R16G16B16A16_UNorm : GraphicsFormat.R8G8B8_SRGB;
            var png = ImageConversion.EncodeArrayToPNG(outBytes, fmt, (uint)OutW, (uint)OutH, (uint)(OutW * ch * bpc));
            File.WriteAllBytes(FramePath(f.Index), png);
        }

        /// <summary>0-255 (averaged) to 16 bit, little-endian as Unity's PNG encoder takes it.</summary>
        private static void Put16(byte[] b, int o, float v8)
        {
            int v = Mathf.Clamp(Mathf.RoundToInt(v8 * (65535f / 255f)), 0, 65535);
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)(v >> 8);
        }

        public string FramePath(int i) => Path.Combine(Dir, $"frame_{i:00000}.png");

        /// <summary>Wait (worker thread) until every queued frame is written, at most <paramref name="timeoutMs"/>.</summary>
        public bool Drain(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (_pendingFrames > 0 && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
            return _pendingFrames == 0;
        }

        public void Dispose()
        {
            _jobs.CompleteAdding();
            _worker.Join(30000);
            Drain(30000); // frames still encoding on the pool
            if (_grab != null) { _grab.Release(); UnityEngine.Object.Destroy(_grab); _grab = null; }
        }

        // ---------------------------------------------------------------- ffmpeg

        /// <summary>ffmpeg on PATH, in WinGet's links folder, or next to the usual installs; null if not found.</summary>
        public static string FindFfmpeg()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            dirs.Add(@"C:\ffmpeg\bin");
            dirs.Add(@"C:\Program Files\ffmpeg\bin");
            dirs.Add(@"C:\ProgramData\chocolatey\bin");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
            foreach (var d in dirs)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try
                {
                    var p = Path.Combine(d.Trim('"'), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>The H.264 command for this folder's frames (also written as encode.cmd).</summary>
        public static string EncodeArgs(string dir, float fps, int crf, string video) =>
            $"-y -framerate {fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{Path.Combine(dir, "frame_%05d.png")}\" " +
            $"-c:v libx264 -preset slow -crf {crf} -pix_fmt yuv420p -movflags +faststart \"{video}\"";

        public static Process StartEncode(string ffmpeg, string args, string logPath)
        {
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var log = new List<string>();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.Add(e.Data); };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.Add(e.Data); };
            p.Exited += (_, __) => { try { lock (log) File.WriteAllLines(logPath, log); } catch { } };
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            return p;
        }
    }
}
