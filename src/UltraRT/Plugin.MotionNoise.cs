using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UltraRT.Bench;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRT
{
    /// <summary>
    /// Bench "motion noise" (bench/README.md, "Motion noise"): how much the image in motion depends on the random numbers
    /// rather than on the scene. For every mode the same window of the camera path (MotionSettle + Motion frames) is
    /// replayed MotionPasses times on a fixed 1/360 s clock (camera, game time and the denoiser's blend factors), with every
    /// history reset at the start of a pass. A pass is therefore a statistically identical replay whose only differences are
    /// the path tracer's random numbers (and, with MotionJitterRotate, the TAAU jitter phase). The per-pixel difference
    /// between passes at the same frame is the noise the player sees in motion: disocclusions, screen edges, silhouettes,
    /// changing lights, without any reprojection. "off" (the game alone) runs two passes first; the pixels it changes
    /// between them (the game's own animation, particles) are masked out. A denoiser pass (Denoise.compute, Classify)
    /// classifies each pixel of the first pass, so the noise is broken down by cause.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>Bench variant (plugin._motionJitterRotate=1): start each motion-noise pass at a different TAAU jitter phase.</summary>
        private bool _motionJitterRotate;

        private static readonly string[] MotionClassNames = { "thin", "edge", "fresh", "screen edge", "interior", "none", "young" };

        private IEnumerator RunMotionNoise(Scenario sc, ScenarioResult sr, string outDir, CameraPath path, float t0)
        {
            bool moving = sc.Path.Type != "static" && sc.Path.Type != "spawn";
            var modes = sc.MotionModes != null && sc.MotionModes.Count > 0 ? sc.MotionModes : sc.Modes;
            int stride = Math.Max(1, sc.MotionStride);
            int caps = Math.Max(1, sc.Motion / stride);
            int settle = Math.Max(0, sc.MotionSettle);
            int total = settle + caps * stride;
            int passes = Math.Max(2, sc.MotionPasses);
            int stripN = Math.Max(0, Math.Min(sc.MotionStrip, total - settle));
            float tStart = moving ? Mathf.Max(0f, t0 - settle / 360f) : t0;
            int fxEvery = sc.Effects != null && sc.Effects.Every > 0f ? Math.Max(1, Mathf.RoundToInt(sc.Effects.Every * 360f)) : 0;

            ApplyBenchMode("off");
            var saved = SaveVariantState();
            float prevCapture = Time.captureDeltaTime;
            byte[][] offMask = null; // per capture: 1 = the game changed this pixel between its own passes
            byte[][] refL = new byte[caps][];
            byte[][] cls = new byte[caps][];
            int W = 0, H = 0, tw = 0, th = 0;
            bool classImageSaved = false;
            Denoiser.FrameTimeOverride = 1f / 360f;
            Time.captureDeltaTime = 1f / 360f;
            try
            {
                foreach (var spec in modes)
                {
                    string mode = spec == "off" ? "off" : ApplyBenchVariant(spec);
                    ApplyBenchMode(mode);
                    yield return WaitForRR();
                    bool isOff = mode == "off";
                    int nPasses = isOff ? 2 : passes;
                    var recs = new List<MotionRecord>(caps * passes);
                    double offsetSum = 0;
                    int offsetN = 0;
                    var images = new List<string>();
                    string baseName = $"{Sanitize(sc.Name)}_motion_{t0:F1}s_{Sanitize(spec)}";

                    // Pass -1 (RT modes): warm-up, so the radiance cache has seen this stretch of the path before pass 0.
                    for (int pass = isOff ? 0 : -1; pass < nPasses; pass++)
                    {
                        ResetHistories();
                        _upsampler?.Reset();
                        if (_upsampler != null) _upsampler.JitterIndex = sc.MotionJitterRotate || _motionJitterRotate ? (uint)Math.Max(0, pass) : 0u;
                        for (int f = 0; f < total; f++)
                        {
                            GameControl.SetPose(path.Sample(moving ? Mathf.Min(tStart + f / 360f, path.Duration) : t0));
                            GameControl.HideCheatPanels();
                            QualitySettings.vSyncCount = 0;
                            Application.targetFrameRate = -1;
                            if (fxEvery > 0 && f > 0 && f % fxEvery == 0) EffectSpawner.Spawn(sc.Effects);
                            int ci = f >= settle && (f - settle) % stride == 0 ? (f - settle) / stride : -1;
                            bool cap = pass >= 0 && ci >= 0;
                            bool strip = pass == 0 && stripN > 0 && f >= total - stripN;
                            bool classify = cap && pass == 0 && !isOff && _denoiser != null;
                            if (_denoiser != null) _denoiser.ClassifyFrame = classify;
                            if (!cap && !strip) { yield return null; continue; }

                            yield return new WaitForEndOfFrame();
                            var tex = ScreenCapture.CaptureScreenshotAsTexture();
                            var px = tex.GetPixels32();
                            if (W == 0) { W = tex.width; H = tex.height; }
                            Destroy(tex);
                            if (px.Length != W * H) { Logger.LogWarning("Motion noise: screen size changed, aborted"); yield break; }
                            if (strip)
                            {
                                var st = new Texture2D(W, H, TextureFormat.RGBA32, false);
                                st.SetPixels32(px);
                                st.Apply();
                                string file = $"{baseName}_f{f - settle}.jpg";
                                File.WriteAllBytes(Path.Combine(outDir, file), st.EncodeToJPG(95));
                                Destroy(st);
                                images.Add(file);
                            }
                            if (classify && _denoiser.Classes != null)
                            {
                                var req = AsyncGPUReadback.Request(_denoiser.Classes);
                                req.WaitForCompletion();
                                if (!req.hasError)
                                {
                                    tw = _denoiser.Classes.width; th = _denoiser.Classes.height;
                                    var data = req.GetData<byte>();
                                    if (cls[ci] == null || cls[ci].Length != data.Length) cls[ci] = new byte[data.Length];
                                    data.CopyTo(cls[ci]);
                                }
                            }
                            if (_denoiser != null) _denoiser.ClassifyFrame = false;
                            if (cap)
                            {
                                if (refL[ci] == null || refL[ci].Length != px.Length) refL[ci] = new byte[px.Length];
                                if (isOff)
                                {
                                    if (offMask == null) offMask = new byte[caps][];
                                    if (pass == 0)
                                    {
                                        if (offMask[ci] == null || offMask[ci].Length != px.Length) offMask[ci] = new byte[px.Length];
                                        Luma(px, offMask[ci]);
                                    }
                                    else
                                    {
                                        var m = offMask[ci];
                                        for (int i = 0; i < px.Length; i++)
                                            m[i] = (byte)(Math.Abs(LumaOf(px[i]) - m[i]) > 3 ? 1 : 0);
                                    }
                                }
                                else if (pass == 0) Luma(px, refL[ci]);
                                else
                                {
                                    var mask = offMask != null && offMask[ci] != null && offMask[ci].Length == px.Length ? offMask[ci] : null;
                                    var excl = Exclusion(sc, W, H);
                                    var c = cls[ci];
                                    var r = refL[ci];
                                    // The pass's global offset (auto exposure settles slightly differently each pass) is not noise.
                                    double so = 0; long no = 0;
                                    for (int i = 0; i < px.Length; i += 7)
                                    {
                                        if (mask != null && mask[i] != 0) continue;
                                        so += LumaOf(px[i]) - r[i]; no++;
                                    }
                                    float off = no > 0 ? (float)(so / no) : 0f;
                                    offsetSum += Math.Abs(off); offsetN++;
                                    float[] err = pass == 1 && ci == caps / 2 ? new float[px.Length] : null;
                                    var rec = new MotionRecord(MotionClassNames.Length);
                                    var rsum = rec.Sum;
                                    var rcnt = rec.Cnt;
                                    float ts = _lastTraceScale > 0f ? _lastTraceScale : _scale; int visW = Mathf.RoundToInt(W * ts), visH = Mathf.RoundToInt(H * ts);
                                    int gx = Math.Max(0, (tw - visW) / 2), gy = Math.Max(0, (th - visH) / 2);
                                    for (int by = 0; by + 4 <= H; by += 4)
                                    for (int bx = 0; bx + 4 <= W; bx += 4)
                                    {
                                        float bs = 0; int bn = 0;
                                        for (int yy = 0; yy < 4; yy++)
                                        {
                                            int y = by + yy;
                                            int ty = Math.Min(th - 1, (int)((y + 0.5f) * visH / H) + gy);
                                            for (int xx = 0; xx < 4; xx++)
                                            {
                                                int x = bx + xx, i = y * W + x;
                                                if ((mask != null && mask[i] != 0) || excl[i]) { rec.MaskedOut++; continue; }
                                                float d = LumaOf(px[i]) - r[i] - off;
                                                // Gross differences are the game's (birds, gore), not denoised lighting noise: skipped, counted.
                                                if (Math.Abs(d) > MotionClip) { rec.Clipped++; rec.MaskedOut++; continue; }
                                                int k = 5;
                                                if (c != null && tw > 0)
                                                {
                                                    int tx = Math.Min(tw - 1, (int)((x + 0.5f) * visW / W) + gx);
                                                    k = MotionClass(c[ty * tw + tx], x, y, W, H);
                                                }
                                                rsum[k] += d * d; rcnt[k]++;
                                                rec.Considered++;
                                                if (k == 5) continue; // no traced surface: the game's own foliage, birds, sky
                                                bs += d; bn++;
                                                if (err != null) err[i] = Math.Abs(d) * 0.70710678f;
                                            }
                                        }
                                        if (bn == 16) { float bm = bs / 16f; rec.BlockSum += bm * bm; rec.Blocks++; }
                                    }
                                    recs.Add(rec);
                                    if (err != null)
                                    {
                                        string ef = baseName + "_err.png";
                                        SaveHeat(Path.Combine(outDir, ef), err, W, H, 8f);
                                        images.Add(ef);
                                        if (!classImageSaved && c != null)
                                        {
                                            classImageSaved = true;
                                            string cf = $"{Sanitize(sc.Name)}_motion_{t0:F1}s_classes.png";
                                            SaveClassImage(Path.Combine(outDir, cf), c, tw, th, W, H, visW, visH, gx, gy, r);
                                            images.Add(cf);
                                        }
                                    }
                                    // Consecutive passes are compared (pass p against p - 1): slow game-side changes (a pulsing
                                    // light, an animation on a long cycle) drift less between neighbours than across the run.
                                    Luma(px, r);
                                }
                            }
                            yield return null;
                        }
                    }

                    // Frames where the game itself changed between the two passes (a timed event, a light cycle) show up as a
                    // squared error far above the window's median: drop them (logged).
                    var mses = recs.Select(x => x.SurfaceMse).Where(x => x > 0).OrderBy(x => x).ToList();
                    double med = mses.Count > 0 ? mses[mses.Count / 2] : 0;
                    var kept = recs.Where(x => med <= 0 || x.SurfaceMse <= 4.0 * med).ToList();
                    int dropped = recs.Count - kept.Count;
                    var sum = new double[MotionClassNames.Length];
                    var cnt = new long[MotionClassNames.Length];
                    double blockSum = 0;
                    long blocks = 0, considered = 0, maskedOut = 0, clipped = 0;
                    foreach (var x in kept)
                    {
                        for (int k = 0; k < sum.Length; k++) { sum[k] += x.Sum[k]; cnt[k] += x.Cnt[k]; }
                        blockSum += x.BlockSum; blocks += x.Blocks; considered += x.Considered; maskedOut += x.MaskedOut; clipped += x.Clipped;
                    }
                    if (!isOff && considered > 0)
                    {
                        double all = sum.Sum();
                        long surf = considered - cnt[5];
                        var ms = new MotionStats
                        {
                            Mode = spec, Window = t0, Frames = caps, Passes = nPasses, Width = W, Height = H,
                            // Headline: traced surfaces only ("none" is what the game draws itself: foliage, birds, sky).
                            Sigma = surf > 0 ? (float)Math.Sqrt((all - sum[5]) / surf / 2.0) : 0f,
                            SigmaBlock = blocks > 0 ? (float)Math.Sqrt(blockSum / blocks / 2.0) : 0f,
                            Offset = offsetN > 0 ? (float)(offsetSum / offsetN) : 0f,
                            MaskedShare = (float)((double)maskedOut / Math.Max(1, considered + maskedOut)),
                            Dropped = dropped,
                            Clipped = (float)((double)clipped / Math.Max(1, considered + clipped)),
                            Images = images,
                        };
                        for (int k = 0; k < MotionClassNames.Length; k++)
                        {
                            ms.Classes.Add(MotionClassNames[k]);
                            ms.ClassShare.Add((float)((double)cnt[k] / considered));
                            ms.ClassSigma.Add(cnt[k] > 0 ? (float)Math.Sqrt(sum[k] / cnt[k] / 2.0) : 0f);
                            ms.ClassEnergy.Add(k != 5 && all - sum[5] > 0 ? (float)(sum[k] / (all - sum[5])) : 0f);
                        }
                        sr.MotionNoise.Add(ms);
                        Logger.LogInfo($"Motion noise '{sc.Name}' @ {t0:F1}s {spec}: sigma {ms.Sigma:F2} block4 {ms.SigmaBlock:F2} offset {ms.Offset:F2} masked {ms.MaskedShare:P0} clipped {ms.Clipped:P2} dropped {dropped}/{recs.Count}; " +
                                       string.Join(", ", Enumerable.Range(0, MotionClassNames.Length).Select(k =>
                                           $"{MotionClassNames[k]} {ms.ClassSigma[k]:F2} ({ms.ClassShare[k]:P0} px, {ms.ClassEnergy[k]:P0} energy)")));
                    }
                    RestoreVariantState(saved);
                }
            }
            finally
            {
                if (_denoiser != null) _denoiser.ClassifyFrame = false;
                Time.captureDeltaTime = prevCapture;
                Denoiser.FrameTimeOverride = 0f;
                RestoreVariantState(saved);
                ApplyBenchMode("off");
            }
        }

        private bool[] _exclusion;
        private int _exclusionW, _exclusionH;
        private Scenario _exclusionFor;

        /// <summary>Pixels left out of the statistics (Scenario.MotionExclude), bottom-up like GetPixels32.</summary>
        private bool[] Exclusion(Scenario sc, int w, int h)
        {
            if (_exclusion != null && _exclusionW == w && _exclusionH == h && ReferenceEquals(_exclusionFor, sc)) return _exclusion;
            _exclusion = new bool[w * h];
            _exclusionW = w; _exclusionH = h; _exclusionFor = sc;
            if (sc.MotionExclude != null)
                foreach (var r in sc.MotionExclude)
                {
                    if (r == null || r.Length < 4) continue;
                    int x0 = Mathf.Clamp((int)(r[0] * w), 0, w), x1 = Mathf.Clamp((int)(r[2] * w), 0, w);
                    int y0 = Mathf.Clamp((int)((1f - r[3]) * h), 0, h), y1 = Mathf.Clamp((int)((1f - r[1]) * h), 0, h);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) _exclusion[y * w + x] = true;
                }
            return _exclusion;
        }

        private sealed class MotionRecord
        {
            public readonly double[] Sum;
            public readonly long[] Cnt;
            public double BlockSum;
            public long Blocks, Considered, MaskedOut, Clipped;
            public MotionRecord(int n) { Sum = new double[n]; Cnt = new long[n]; }
            public double SurfaceMse
            {
                get
                {
                    double s = 0; long c = 0;
                    for (int k = 0; k < Sum.Length; k++) if (k != 5) { s += Sum[k]; c += Cnt[k]; }
                    return c > 0 ? s / c : 0;
                }
            }
        }

        /// <summary>Per-pixel differences above this (luma) are left out of the statistics and counted as clipped.</summary>
        private const float MotionClip = 40f;

        private static int LumaOf(Color32 c) => (2126 * c.r + 7152 * c.g + 722 * c.b + 5000) / 10000;

        private static void Luma(Color32[] px, byte[] dst)
        {
            for (int i = 0; i < px.Length; i++) dst[i] = (byte)LumaOf(px[i]);
        }

        /// <summary>Exclusive class of a screen pixel from its trace pixel's class bits (Denoise.compute, Classify).</summary>
        private static int MotionClass(byte b, int x, int y, int w, int h)
        {
            if ((b & 64) == 0) return 5;
            if ((b & 4) != 0) return 0;
            if ((b & 2) != 0) return 1;
            if ((b & 1) != 0) return 2;
            if (x < w / 10 || x >= w - w / 10 || y < h / 10 || y >= h - h / 10) return 3;
            return (b & 8) != 0 ? 6 : 4;
        }

        private static void SaveClassImage(string path, byte[] c, int tw, int th, int w, int h, int visW, int visH, int gx, int gy, byte[] luma)
        {
            var colors = new[]
            {
                new Color32(255, 255, 0, 255), new Color32(255, 0, 255, 255), new Color32(255, 64, 0, 255),
                new Color32(0, 160, 255, 255), new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255), new Color32(0, 255, 64, 255),
            };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int ty = Math.Min(th - 1, (int)((y + 0.5f) * visH / h) + gy);
                for (int x = 0; x < w; x++)
                {
                    int tx = Math.Min(tw - 1, (int)((x + 0.5f) * visW / w) + gx);
                    int k = MotionClass(c[ty * tw + tx], x, y, w, h);
                    byte g = (byte)(luma[y * w + x] / 3);
                    var col = colors[k];
                    px[y * w + x] = k == 4 || k == 5 ? new Color32(g, g, g, 255)
                        : new Color32((byte)Math.Min(255, g + col.r * 2 / 3), (byte)Math.Min(255, g + col.g * 2 / 3), (byte)Math.Min(255, g + col.b * 2 / 3), 255);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }

        private static void SummarizeMotion(StringBuilder sb, SuiteResult r)
        {
            if (!r.Scenarios.Any(s => s.MotionNoise.Count > 0)) return;
            sb.AppendLine();
            sb.AppendLine("Motion noise (screen luma 0-255; sigma = RMS difference between replays of the same path window / sqrt 2, over traced " +
                          "surfaces; block = the same on 4x4 pixel means; per class: sigma / share of pixels / share of the squared error; " +
                          "none = pixels without a traced surface, not in sigma):");
            sb.AppendLine();
            var cols = new[] { 0, 1, 2, 6, 3, 4 }; // thin, edge, fresh, young, screen edge, interior
            sb.AppendLine("| Scenario | Window | Mode | sigma | block4 | " + string.Join(" | ", cols.Select(k => MotionClassNames[k])) + " | none | offset / dropped frames |");
            sb.AppendLine("|---|---|---|---|---|" + string.Concat(cols.Select(_ => "---|")) + "---|---|");
            foreach (var sc in r.Scenarios)
                foreach (var m in sc.MotionNoise)
                    sb.AppendLine($"| {sc.Name} | {m.Window:F1}s | {m.Mode} | {m.Sigma:F2} | {m.SigmaBlock:F2} | " +
                                  string.Join(" | ", cols.Select(k =>
                                      k < m.ClassSigma.Count ? $"{m.ClassSigma[k]:F2} / {m.ClassShare[k] * 100:F0}% / {m.ClassEnergy[k] * 100:F0}%" : "-")) +
                                  $" | {(m.ClassSigma.Count > 5 ? $"{m.ClassSigma[5]:F2} / {m.ClassShare[5] * 100:F0}%" : "-")} | {m.Offset:F2} / {m.Dropped} |");
        }
    }
}
