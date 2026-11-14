using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UltraRT.Bench;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Bench "burst": temporal noise of a held pose (bench/README.md, "Temporal noise bursts"). For each burst mode the
    /// pose is held for BurstSettle frames, then BurstFrames consecutive frames of the final screen are captured and
    /// reduced per pixel (luma, 0-255): sigma over single frames, sigma of BurstBlock-frame means (what survives the eye's
    /// own averaging at high refresh rates: correlated noise and slow drift), and the first-half vs second-half drift.
    /// A burst mode is a view mode optionally followed by runtime variants: "relight", "relight+lights=ref+cache=off".
    /// </summary>
    public partial class Plugin
    {
        /// <summary>Bench variant: use the relight light model in every mode (to break relight down with pt-direct etc.).</summary>
        private bool _relightForce;
        /// <summary>State to restore when the bench leaves a timing mode that carried variants.</summary>
        private Dictionary<string, Dictionary<FieldInfo, object>> _benchModeVariant;

        /// <summary>Snapshot of every settings object a burst variant may change (their public fields).</summary>
        private Dictionary<string, Dictionary<FieldInfo, object>> SaveVariantState()
        {
            var d = new Dictionary<string, Dictionary<FieldInfo, object>>();
            foreach (var kv in VariantTargets())
                d[kv.Key] = kv.Value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(f => f, f => f.GetValue(kv.Value));
            d["relight"] = new Dictionary<FieldInfo, object> { { typeof(Plugin).GetField(nameof(_relightForce), BindingFlags.NonPublic | BindingFlags.Instance), _relightForce } };
            return d;
        }

        private void RestoreVariantState(Dictionary<string, Dictionary<FieldInfo, object>> s)
        {
            foreach (var kv in VariantTargets())
                if (s.TryGetValue(kv.Key, out var fields))
                    foreach (var f in fields) f.Key.SetValue(kv.Value, f.Value);
            if (_denoiserDirect != null && s.TryGetValue("dn", out var dn))
                foreach (var f in dn) f.Key.SetValue(_denoiserDirect.Settings, f.Value);
            _relightForce = (bool)s["relight"].Values.First();
            foreach (var kv in _variantPluginRestore) kv.Key.SetValue(this, kv.Value);
            _variantPluginRestore.Clear();
            ResetHistories();
        }

        private IEnumerable<KeyValuePair<string, object>> VariantTargets()
        {
            yield return new KeyValuePair<string, object>("ls", _lightSamplingSettings);
            yield return new KeyValuePair<string, object>("cache", _cacheSettings);
            yield return new KeyValuePair<string, object>("refl", _reflSettings);
            yield return new KeyValuePair<string, object>("em", _emissiveSettings);
            yield return new KeyValuePair<string, object>("ae", _aeSettings);
            if (_denoiser != null) yield return new KeyValuePair<string, object>("dn", _denoiser.Settings);
            if (_upsampler != null) yield return new KeyValuePair<string, object>("up", _upsampler.Settings);
        }

        /// <summary>Original values of Plugin fields changed by "plugin.&lt;field&gt;=value" variants.</summary>
        private readonly Dictionary<FieldInfo, object> _variantPluginRestore = new Dictionary<FieldInfo, object>();

        private void ResetHistories()
        {
            _denoiser?.Reset();
            _denoiserDirect?.Reset();
            _lightSampling?.Reset();
            _reflFilter?.Reset();
            _autoExposure?.Reset(); // snap to the variant's exposure (same base mode: no automatic snap)
        }

        /// <summary>"mode+key=value+..." -> applies the variants, returns the view mode. Keys: lights=ref|ris|restir,
        /// cache=off|bounce|primary, refl=0|1, emissive=0|1, relight=0|1 (force the relight model), and
        /// &lt;object&gt;.&lt;field&gt;=value for any public field of ls (LightSamplingSettings), cache (RadianceCacheSettings),
        /// refl (ReflectionSettings), em (EmissiveLightSettings), dn (DenoiserSettings, both instances) or up (UpsamplerSettings;
        /// taau=0|1 is up.enabled), e.g.
        /// dn.stability=0, dn.alphaMin=0.05, cache.historyFrames=64, ls.spatialSamples=2.</summary>
        private string ApplyBenchVariant(string spec)
        {
            var parts = spec.Split('+');
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split('=');
                string k = kv[0].Trim(), v = kv.Length > 1 ? kv[1].Trim() : "1";
                bool on = v != "0" && v != "off" && v != "false";
                switch (k.ToLowerInvariant())
                {
                    case "lights":
                        _lightSamplingSettings.mode = v == "ref" ? LightSamplingMode.Reference : v == "ris" ? LightSamplingMode.Ris : LightSamplingMode.ReSTIR;
                        break;
                    case "cache":
                        if (v == "off") _cacheSettings.mode = RadianceCacheMode.Off;
                        else _cacheSettings.mode = v == "primary" ? RadianceCacheMode.Primary : RadianceCacheMode.Bounce;
                        break;
                    case "refl": _reflSettings.enabled = on; break;
                    case "emissive": _emissiveSettings.enabled = on; break;
                    case "relight": _relightForce = on; break;
                    case "taau": if (_upsampler != null) _upsampler.Settings.enabled = on; break;
                    case "preset":
                    {
                        // Ctrl+F3's presets: full | previous | legacy (also switches the guard band like the hotkey).
                        var preset = (DenoiserPreset)Enum.Parse(typeof(DenoiserPreset), v, true);
                        _denoiser?.Settings.ApplyPreset(preset);
                        _denoiserDirect?.Settings.ApplyPreset(preset);
                        var g = typeof(Plugin).GetField(nameof(_guardActive), BindingFlags.NonPublic | BindingFlags.Instance);
                        if (!_variantPluginRestore.ContainsKey(g)) _variantPluginRestore[g] = _guardActive;
                        _guardActive = preset == DenoiserPreset.Full;
                        _lightSamplingSettings.spatialSamplesHybrid = preset == DenoiserPreset.Full ? new LightSamplingSettings().spatialSamplesHybrid : 0;
                        break;
                    }
                    default:
                    {
                        int dot = k.IndexOf('.');
                        if (dot > 0 && k.Substring(0, dot) == "plugin")
                        {
                            // Any Plugin field, e.g. plugin._lightRadius=0
                            var f = typeof(Plugin).GetField(k.Substring(dot + 1), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (f == null) { Logger.LogWarning($"Burst: no Plugin field '{k}'"); break; }
                            if (!_variantPluginRestore.ContainsKey(f)) _variantPluginRestore[f] = f.GetValue(this);
                            SetField(this, f.Name, v);
                            break;
                        }
                        var target = dot > 0 ? VariantTargets().FirstOrDefault(t => t.Key == k.Substring(0, dot)).Value : null;
                        if (target == null) { Logger.LogWarning($"Burst: unknown variant '{p}'"); break; }
                        SetField(target, k.Substring(dot + 1), v);
                        if (target == _denoiser?.Settings && _denoiserDirect != null) SetField(_denoiserDirect.Settings, k.Substring(dot + 1), v);
                        break;
                    }
                }
            }
            ResetHistories();
            return parts[0];
        }

        private void SetField(object o, string name, string value)
        {
            var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (f == null) { Logger.LogWarning($"Burst: no field '{name}' on {o.GetType().Name}"); return; }
            object val;
            if (f.FieldType == typeof(bool)) val = value != "0" && value != "false" && value != "off";
            else if (f.FieldType == typeof(int)) val = int.Parse(value, CultureInfo.InvariantCulture);
            else if (f.FieldType == typeof(float)) val = float.Parse(value, CultureInfo.InvariantCulture);
            else if (f.FieldType.IsEnum) val = Enum.Parse(f.FieldType, value, true);
            else { Logger.LogWarning($"Burst: field '{name}' has unsupported type {f.FieldType.Name}"); return; }
            f.SetValue(o, val);
        }

        /// <summary>Hold the pose and measure the temporal noise of every burst mode (see the class comment).</summary>
        private IEnumerator RunBurst(Scenario sc, ScenarioResult sr, string outDir, CameraPath path, float t0)
        {
            // Moving paths advance a fixed 1/360 s per frame (the user's refresh rate), however long a captured frame takes,
            // so the per-frame camera motion (and thus history loss) is the same in every run.
            bool moving = sc.Path.Type != "static" && sc.Path.Type != "spawn";
            var modes = sc.BurstModes != null && sc.BurstModes.Count > 0 ? sc.BurstModes : sc.Modes;
            int n = Math.Max(4, sc.Burst), block = Math.Max(1, Math.Min(sc.BurstBlock, n / 2));
            float[] mask = null; // per-pixel sigma of "off": pixels the game itself animates are excluded from the masked stats
            ApplyBenchMode("off"); // undo a timing mode's variants before snapshotting
            var saved = SaveVariantState();
            Denoiser.FrameTimeOverride = 1f / 360f; // captures run far below real time; blend factors as at 360 Hz
            foreach (var spec in modes)
            {
                string mode = spec == "off" ? "off" : ApplyBenchVariant(spec);
                ApplyBenchMode(mode);
                int frame = 0;
                CamPose Pose() => path.Sample(moving ? Mathf.Min(t0 + frame++ / 360f, path.Duration) : t0);
                for (int f = 0; f < sc.BurstSettle; f++) { GameControl.SetPose(Pose()); GameControl.HideCheatPanels(); yield return null; }
                double hfL = 0, hfC = 0, hfR = 0;

                int w = 0, h = 0, nb = 0;
                float[] sum = null, sq = null, bsum = null, bm = null, bsq = null, half = null;
                Color32[] last = null;
                for (int f = 0; f < n; f++)
                {
                    GameControl.SetPose(Pose());
                    GameControl.HideCheatPanels();
                    yield return new WaitForEndOfFrame();
                    var tex = ScreenCapture.CaptureScreenshotAsTexture();
                    var px = tex.GetPixels32();
                    if (sum == null)
                    {
                        w = tex.width; h = tex.height;
                        sum = new float[w * h]; sq = new float[w * h]; bsum = new float[w * h];
                        bm = new float[w * h]; bsq = new float[w * h]; half = new float[w * h];
                    }
                    Destroy(tex);
                    if (px.Length != sum.Length) { Logger.LogWarning("Burst: screen size changed, burst aborted"); break; }
                    bool blockEnd = (f + 1) % block == 0;
                    bool firstHalf = f < n / 2;
                    for (int i = 0; i < px.Length; i++)
                    {
                        var c = px[i];
                        float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        sum[i] += l; sq[i] += l * l; bsum[i] += l;
                        if (firstHalf) half[i] += l;
                        if (blockEnd)
                        {
                            float m = bsum[i] / block;
                            bm[i] += m; bsq[i] += m * m; bsum[i] = 0f;
                        }
                    }
                    if (blockEnd) nb++;
                    // High-frequency energy (mean |4-neighbour Laplacian| of luma) in the left 15%, centre 40% and right 15%:
                    // in motion, where the history is short (screen edges), noise shows up here.
                    var (hl, hc, hr) = Laplacian(px, w, h);
                    hfL += hl; hfC += hc; hfR += hr;
                    last = px;
                    yield return null;
                }
                if (sum == null || nb < 2) continue;

                // Reduce.
                int count = w * h, h1 = n / 2, h2 = n - h1;
                var sigma = new float[count];
                var sigmaB = new float[count];
                var drift = new float[count];
                var mean = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float m = sum[i] / n;
                    mean[i] = m;
                    sigma[i] = Mathf.Sqrt(Mathf.Max(0f, sq[i] / n - m * m));
                    float mb = bm[i] / nb;
                    sigmaB[i] = Mathf.Sqrt(Mathf.Max(0f, bsq[i] / nb - mb * mb));
                    drift[i] = Mathf.Abs(half[i] / h1 - (sum[i] - half[i]) / h2);
                }
                if (mode == "off" && mask == null) mask = sigma;

                var st = new BurstStats { Mode = spec, Frames = n, Block = block, Width = w, Height = h,
                                          HfLeft = (float)(hfL / n), HfCenter = (float)(hfC / n), HfRight = (float)(hfR / n) };
                FillBurst(st, sigma, sigmaB, drift, mean, null);
                if (mask != null && mode != "off") FillBurst(st, sigma, sigmaB, drift, mean, mask);
                sr.Bursts.Add(st);
                Logger.LogInfo($"Burst '{sc.Name}' {spec}: sigma {st.Sigma:F2} (masked {st.SigmaMasked:F2}, p95 {st.SigmaP95Masked:F2}) " +
                               $"block{block} {st.SigmaBlock:F2} (masked {st.SigmaBlockMasked:F2}, p95 {st.SigmaBlockP95Masked:F2}) drift {st.DriftMasked:F2} hf L/C/R {st.HfLeft:F2}/{st.HfCenter:F2}/{st.HfRight:F2}");

                // Images: sigma of block means (x16, heat), and the mean frame.
                string baseName = $"{Sanitize(sc.Name)}_burst_{Sanitize(spec)}";
                SaveHeat(Path.Combine(outDir, baseName + "_sigma.png"), sigmaB, w, h, 16f);
                var lastTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                lastTex.SetPixels32(last);
                lastTex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, baseName + "_frame.png"), lastTex.EncodeToPNG());
                Destroy(lastTex);
                st.Images = new List<string> { baseName + "_sigma.png", baseName + "_frame.png" };
                RestoreVariantState(saved);
            }
            RestoreVariantState(saved);
            Denoiser.FrameTimeOverride = 0f;
            ApplyBenchMode("off");
        }

        private static (double left, double centre, double right) Laplacian(Color32[] px, int w, int h)
        {
            double sl = 0, sc = 0, sr = 0;
            long nl = 0, nc = 0, nr = 0;
            int x0 = (int)(w * 0.15f), x1 = (int)(w * 0.3f), x2 = (int)(w * 0.7f), x3 = (int)(w * 0.85f);
            float L(Color32 c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            for (int y = 1; y < h - 1; y += 2)
            for (int x = 1; x < w - 1; x++)
            {
                bool left = x < x0, centre = x >= x1 && x < x2, right = x >= x3;
                if (!left && !centre && !right) continue;
                int i = y * w + x;
                float v = Math.Abs(4f * L(px[i]) - L(px[i - 1]) - L(px[i + 1]) - L(px[i - w]) - L(px[i + w]));
                if (left) { sl += v; nl++; } else if (centre) { sc += v; nc++; } else { sr += v; nr++; }
            }
            return (sl / Math.Max(1, nl), sc / Math.Max(1, nc), sr / Math.Max(1, nr));
        }

        private static void FillBurst(BurstStats st, float[] sigma, float[] sigmaB, float[] drift, float[] mean, float[] mask)
        {
            var s = new List<float>(sigma.Length);
            var sb = new List<float>(sigma.Length);
            double sumS = 0, sumB = 0, sumD = 0, sumM = 0;
            for (int i = 0; i < sigma.Length; i++)
            {
                if (mask != null && mask[i] > 1f) continue;
                s.Add(sigma[i]); sb.Add(sigmaB[i]);
                sumS += sigma[i]; sumB += sigmaB[i]; sumD += drift[i]; sumM += mean[i];
            }
            if (s.Count == 0) return;
            s.Sort(); sb.Sort();
            float P(List<float> l, double q) => l[Math.Min(l.Count - 1, (int)(l.Count * q))];
            if (mask == null)
            {
                st.Sigma = (float)(sumS / s.Count); st.SigmaP95 = P(s, 0.95);
                st.SigmaBlock = (float)(sumB / s.Count); st.SigmaBlockP95 = P(sb, 0.95);
                st.Drift = (float)(sumD / s.Count); st.MeanLuma = (float)(sumM / s.Count);
            }
            else
            {
                st.MaskedShare = (float)s.Count / sigma.Length;
                st.SigmaMasked = (float)(sumS / s.Count); st.SigmaP95Masked = P(s, 0.95);
                st.SigmaBlockMasked = (float)(sumB / s.Count); st.SigmaBlockP95Masked = P(sb, 0.95);
                st.DriftMasked = (float)(sumD / s.Count); st.MeanLumaMasked = (float)(sumM / s.Count);
            }
        }

        private static void SaveHeat(string path, float[] v, int w, int h, float scale)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                float x = Mathf.Clamp01(v[i] * scale / 255f);
                px[i] = new Color32((byte)(255 * Mathf.Clamp01(1.5f - Mathf.Abs(4f * x - 3f))),
                                    (byte)(255 * Mathf.Clamp01(1.5f - Mathf.Abs(4f * x - 2f))),
                                    (byte)(255 * Mathf.Clamp01(1.5f - Mathf.Abs(4f * x - 1f))), 255);
                if (v[i] <= 0f) px[i] = new Color32(0, 0, 0, 255);
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }

        private static void SummarizeBursts(StringBuilder sb, SuiteResult r)
        {
            if (!r.Scenarios.Any(s => s.Bursts.Count > 0)) return;
            sb.AppendLine();
            sb.AppendLine("Temporal noise bursts (screen luma 0-255; sigma = per-pixel std-dev over the burst, block = std-dev of " +
                          "N-frame means, drift = |first half mean - second half mean|; masked = pixels the game itself keeps still in 'off'):");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Mode | Frames | sigma | sigma p95 | block sigma | block p95 | drift | sigma (all px) | block (all px) | mean luma | HF left/centre/right |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var sc in r.Scenarios)
                foreach (var b in sc.Bursts)
                    sb.AppendLine($"| {sc.Name} | {b.Mode} | {b.Frames}/{b.Block} | {b.SigmaMasked:F2} | {b.SigmaP95Masked:F2} | {b.SigmaBlockMasked:F2} | " +
                                  $"{b.SigmaBlockP95Masked:F2} | {b.DriftMasked:F2} | {b.Sigma:F2} | {b.SigmaBlock:F2} | {b.MeanLumaMasked:F1} | {b.HfLeft:F2}/{b.HfCenter:F2}/{b.HfRight:F2} |");
        }
    }
}
