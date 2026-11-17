using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UltraRT.Bench;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Per-light brightness and colour fit to the game (docs/lighting-overrides.md, "Fit"). At a bench pose with
    /// "LightFit": true, in relight:
    ///  1. the RT image's and the game's mean colour at the near pixels are measured in linear units (Exposure.compute,
    ///     _Params6.w): both are pre-exposure, and the RT sum is linear in every light's intensity;
    ///  2. each significant light is switched off in turn (with the histories reset and re-settled), which gives its share
    ///     A of the RT mean (direct light plus its bounce);
    ///  3. per level, over all its fitted poses, bounded least squares finds per-light linear factors m (rgb) so that
    ///     base + sum (m - 1) A matches the game's mean, relative to each pose's brightness, with a pull towards no change
    ///     and towards keeping each light's hue. Factors are clamped to 0.5x-2x in the game's (gamma) units.
    /// Shadows and contrast are kept: only whole lights are scaled. The result (the level's current overrides with the fit
    /// multiplied in) goes to BepInEx\ultrart\lights\fit\&lt;scene&gt;.json with a report.
    /// </summary>
    public partial class Plugin
    {
        private Light _fitSkip; // GatherLights leaves this light out (an ablation)

        private sealed class FitLight
        {
            public string Key, Path, Name, Type;
            public Vector3 Pos;
            public Vector3 A; // linear rgb share of the pose's RT mean
        }

        private sealed class FitPose
        {
            public string Name;
            public Vector3 Base, Game, BaseEnd, BaseStart, DispRt, DispGame, Target;
            public List<FitLight> Lights = new List<FitLight>();
        }

        private readonly Dictionary<string, List<FitPose>> _fitPoses = new Dictionary<string, List<FitPose>>();
        public static int FitSettleFrames = 50, FitMeasureFrames = 16;
        public static float FitMin = 0.5f, FitMax = 2f, FitLambda = 0.03f, FitHue = 0.15f;

        private IEnumerator RunLightFit(Scenario sc, string outDir)
        {
            string mode = sc.Modes.FirstOrDefault(m => m.Split('+')[0] == "relight") ?? "relight";
            ApplyBenchMode(mode);
            for (int f = 0; f < 10; f++) { GameControl.HideCheatPanels(); yield return null; }
            if (_camera == null || _autoExposure == null) { Logger.LogWarning("Light fit: no camera / auto exposure"); yield break; }
            string scene = SafeCurrentScene();
            var pose = new FitPose { Name = sc.Name };

            // Candidates: the packed scene lights (not the player's, not spawned), by the RIS importance weight, whose reach
            // touches the view.
            var cam = _camera.transform.position;
            var planes = GeometryUtility.CalculateFrustumPlanes(_camera);
            var cands = new List<(Light l, float w)>();
            for (int k = 0; k < _lightCount; k++)
            {
                var l = _packedLights[k];
                if (ReferenceEquals(l, null) || l == null || l.type == LightType.Directional) continue;
                if (_proxies != null && !_proxies.IsBuildLight(l)) continue;
                if (_lightData[k * 4 + 1].w > 0.5f) continue; // the player's own
                var l0 = _lightData[k * 4];
                var c = _lightData[k * 4 + 1];
                float R = 2f * l0.w;
                var lp = new Vector3(l0.x, l0.y, l0.z);
                if (!GeometryUtility.TestPlanesAABB(planes, new Bounds(lp, Vector3.one * 2f * R))) continue;
                float d = Mathf.Max(0f, Vector3.Distance(lp, cam) - R);
                float w = (0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z) * R * R / (R * R + d * d);
                cands.Add((l, w));
            }
            var chosen = cands.OrderByDescending(x => x.w).Take(Math.Max(1, sc.LightFitLights)).Select(x => x.l).ToList();
            Logger.LogInfo($"Light fit '{sc.Name}': {cands.Count} lights reach the view, measuring {chosen.Count}");

            var m0 = new Vector3[1];
            yield return FitMeasure(m0);
            pose.Base = m0[0];
            pose.Game = _fitGame;
            pose.DispRt = _fitDispRt;
            pose.DispGame = _fitDispGame;
            var without = new List<(Light l, Vector3 v)>();
            foreach (var l in chosen)
            {
                if (l == null) continue;
                _fitSkip = l;
                yield return FitMeasure(m0);
                _fitSkip = null;
                without.Add((l, m0[0]));
            }
            yield return FitMeasure(m0);
            pose.BaseEnd = m0[0];
            // The base drifts while the radiance cache converges over the run (a few % per measurement, always the same
            // way): each light's share is taken against the base interpolated to its measurement.
            for (int j = 0; j < without.Count; j++)
            {
                var (l, v) = without[j];
                if (l == null) continue;
                var baseJ = Vector3.Lerp(pose.Base, pose.BaseEnd, (j + 1f) / (without.Count + 1f));
                var id = LightIdentity(l);
                var a = baseJ - v;
                pose.Lights.Add(new FitLight
                {
                    Key = LightOverrides.KeyOf(id.path, id.pos), Path = id.path, Name = LightOverrides.NormalizedName(l.name),
                    Type = l.type.ToString().ToLowerInvariant(), Pos = id.pos, A = a,
                });
                Logger.LogInfo(FormattableString.Invariant($"Light fit '{sc.Name}': {id.path} share ({a.x:F4}, {a.y:F4}, {a.z:F4}) of ({baseJ.x:F4}, {baseJ.y:F4}, {baseJ.z:F4})"));
            }
            // The solve uses the mean base (the shares are relative to the interpolated one).
            pose.BaseStart = pose.Base;
            pose.Base = 0.5f * (pose.Base + pose.BaseEnd);
            // The target is the game's look, in display units: the linear base times (game / RT display mean)^2.2 per
            // channel (a uniform linear scale s moves every pixel's display value by s^(1/2.2)). Matching linear means
            // instead would match energy: 6-1's coffins are 1.09x the game in linear and 1.45x on screen. A near-empty
            // channel (under 0.004 in either image) takes the luminance ratio.
            float lumRatio = Lum(pose.DispGame) / Mathf.Max(Lum(pose.DispRt), 1e-4f);
            for (int c = 0; c < 3; c++)
            {
                float r = pose.DispRt[c] > 0.004f && pose.DispGame[c] > 0.004f ? pose.DispGame[c] / pose.DispRt[c] : lumRatio;
                pose.Target[c] = pose.Base[c] * Mathf.Pow(Mathf.Clamp(r, 0.05f, 20f), 2.2f);
            }
            Logger.LogInfo(FormattableString.Invariant($"Light fit '{sc.Name}': base ({pose.Base.x:F4}, {pose.Base.y:F4}, {pose.Base.z:F4}), again ({pose.BaseEnd.x:F4}, {pose.BaseEnd.y:F4}, {pose.BaseEnd.z:F4}), game ({pose.Game.x:F4}, {pose.Game.y:F4}, {pose.Game.z:F4}) (linear); display RT ({V(pose.DispRt)}) game ({V(pose.DispGame)}); target ({V(pose.Target)})"));
            if (!_fitPoses.TryGetValue(scene, out var list)) _fitPoses[scene] = list = new List<FitPose>();
            list.RemoveAll(p => p.Name == pose.Name);
            list.Add(pose);
            try { SolveLightFit(scene, list); }
            catch (Exception e) { Logger.LogError($"Light fit solve failed: {e}"); }
        }

        private Vector3 _fitGame, _fitDispRt, _fitDispGame;

        /// <summary>Resets the histories, lets them settle, then averages the linear means of a few frames.</summary>
        private IEnumerator FitMeasure(Vector3[] result)
        {
            ResetLightHistories();
            _accumFrames = 0;
            for (int f = 0; f < FitSettleFrames; f++) { GameControl.HideCheatPanels(); yield return null; }
            var trace = new List<Vector4[]>();
            _autoExposure.FitTrace = trace;
            for (int f = 0; f < FitMeasureFrames; f++) { GameControl.HideCheatPanels(); yield return null; }
            _autoExposure.FitTrace = null;
            for (int f = 0; f < 4; f++) yield return null; // the last readbacks
            if (trace.Count == 0) { result[0] = Vector3.zero; yield break; }
            var rt = Vector3.zero;
            var game = Vector3.zero;
            Vector3 drt = Vector3.zero, dgame = Vector3.zero;
            foreach (var t in trace) { rt += (Vector3)t[0]; game += (Vector3)t[1]; drt += (Vector3)t[2]; dgame += (Vector3)t[3]; }
            _fitDispRt = drt / trace.Count;
            _fitDispGame = dgame / trace.Count;
            result[0] = rt / trace.Count;
            _fitGame = game / trace.Count;
        }

        /// <summary>Bounded least squares over the level's fitted poses; writes the fit file and its report.</summary>
        private void SolveLightFit(string scene, List<FitPose> poses)
        {
            // Unknowns: per light, linear factors m (rgb). Lights with a negligible share everywhere are left out.
            var keys = new List<string>();
            var info = new Dictionary<string, FitLight>();
            foreach (var p in poses)
                foreach (var l in p.Lights)
                {
                    float rel = Lum(l.A) / Mathf.Max(Lum(p.Base), 1e-5f);
                    if (rel < 0.02f) continue;
                    if (!info.ContainsKey(l.Key)) { info[l.Key] = l; keys.Add(l.Key); }
                }
            int n = keys.Count;
            var idx = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
            var m = new float[n, 3];
            for (int i = 0; i < n; i++) for (int c = 0; c < 3; c++) m[i, c] = 1f;
            float lo = Mathf.Pow(FitMin, 2.2f), hi = Mathf.Pow(FitMax, 2.2f);

            // Per pose: residual r = base + sum (m - 1) A - game, weighted by 1 / lum(game) (relative error).
            float Weight(FitPose p) => 1f / Mathf.Max(Lum(p.Target), 1e-4f);
            float[] Residual(FitPose p)
            {
                var r = new float[3];
                for (int c = 0; c < 3; c++)
                {
                    float v = p.Base[c] - p.Target[c];
                    foreach (var l in p.Lights) if (idx.TryGetValue(l.Key, out int i)) v += (m[i, c] - 1f) * l.A[c];
                    r[c] = v;
                }
                return r;
            }
            // Coordinate descent on the convex quadratic with box constraints.
            for (int it = 0; it < 200; it++)
            {
                float change = 0f;
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        // Objective in x = m[i,c]: sum_p w_p^2 (r_pc + (x - x0) A_pic)^2 + lambda (x - 1)^2 + hue (x - mean_i)^2
                        float a = FitLambda + FitHue * (2f / 3f) * (2f / 3f), b = 0f;
                        float x0 = m[i, c];
                        foreach (var p in poses)
                        {
                            var l = p.Lights.FirstOrDefault(q => q.Key == keys[i]);
                            if (l == null) continue;
                            float w = Weight(p);
                            float A = l.A[c] * w;
                            float r = Residual(p)[c] * w;
                            a += A * A;
                            b += A * (r - x0 * A); // d/dx: A (r + (x - x0) A)
                        }
                        float others = 0f;
                        for (int c2 = 0; c2 < 3; c2++) if (c2 != c) others += m[i, c2];
                        // hue term: (x - (x + others) / 3)^2 = ((2x - others) / 3)^2
                        b += FitHue * (2f / 3f) * (-others / 3f);
                        b += -FitLambda * 1f;
                        float x = Mathf.Clamp(-b / a, lo, hi);
                        change = Mathf.Max(change, Mathf.Abs(x - x0));
                        m[i, c] = x;
                    }
                if (change < 1e-5f) break;
            }

            // Report and file: the level's current overrides with the fit multiplied in (gamma factors = m^(1/2.2)).
            var sb = new StringBuilder();
            sb.AppendLine($"# Light fit: {scene}");
            sb.AppendLine();
            sb.AppendLine($"{poses.Count} poses, {n} lights fitted (bounds x{FitMin}-x{FitMax}, lambda {FitLambda}, hue {FitHue}). Linear mean colour at the near pixels, pre-exposure.");
            sb.AppendLine();
            sb.AppendLine("| Pose | RT / game on screen (display, near px) | predicted | RT linear before | target | predicted | base repeat drift |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var p in poses)
            {
                var pred = p.Base;
                foreach (var l in p.Lights)
                    if (idx.TryGetValue(l.Key, out int i))
                        for (int c = 0; c < 3; c++) pred[c] += (m[i, c] - 1f) * l.A[c];
                float dBefore = Lum(p.DispRt) / Mathf.Max(Lum(p.DispGame), 1e-5f);
                float dPred = dBefore * Mathf.Pow(Mathf.Max(Lum(pred), 1e-6f) / Mathf.Max(Lum(p.Base), 1e-6f), 1f / 2.2f);
                sb.AppendLine(FormattableString.Invariant($"| {p.Name} | {dBefore:F2} | {dPred:F2} | {V(p.Base)} | {V(p.Target)} | {V(pred)} | {(Lum(p.BaseEnd) / Mathf.Max(Lum(p.BaseStart), 1e-5f) - 1f) * 100f:F1} % |"));
            }
            sb.AppendLine();
            sb.AppendLine("| Light | intensity | colour (r, g, b) | share at poses |");
            sb.AppendLine("|---|---|---|---|");
            var file = new LightOverrideFile { Scene = scene, Note = $"Light fit (UltraRT {DateTime.Now:yyyy-MM-dd}; docs/lighting-overrides.md). Current overrides with the fitted scales multiplied in; review before shipping." };
            EnsureLightOverrides();
            var done = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                var fl = info[keys[i]];
                var g = new Vector3(Mathf.Pow(m[i, 0], 1f / 2.2f), Mathf.Pow(m[i, 1], 1f / 2.2f), Mathf.Pow(m[i, 2], 1f / 2.2f));
                float s = (g.x + g.y + g.z) / 3f;
                var col = g / Mathf.Max(s, 1e-4f);
                var cur = _lightOv.Find(fl.Path, fl.Name, fl.Pos);
                var e = cur != null ? cur.Clone() : new LightOverrideEntry { Path = fl.Path, Name = fl.Name, Pos = new[] { LightOverrides.Round(fl.Pos.x), LightOverrides.Round(fl.Pos.y), LightOverrides.Round(fl.Pos.z) }, Type = fl.Type };
                e.Intensity = LightOverrides.Round((e.Intensity ?? 1f) * s);
                var oc = e.Color != null && e.Color.Length >= 3 ? new Vector3(e.Color[0], e.Color[1], e.Color[2]) : Vector3.one;
                var nc = Vector3.Scale(oc, col);
                e.Color = Mathf.Abs(nc.x - 1f) < 0.01f && Mathf.Abs(nc.y - 1f) < 0.01f && Mathf.Abs(nc.z - 1f) < 0.01f ? null : new[] { LightOverrides.Round(nc.x), LightOverrides.Round(nc.y), LightOverrides.Round(nc.z) };
                e.Source = cur != null && cur.Source != null && cur.Source != "fit" ? cur.Source + "+fit" : "fit";
                e.Note = (cur?.Note != null && cur.Source != "fit" ? cur.Note + "; " : "") + FormattableString.Invariant($"fit x{s:F2} ({col.x:F2}, {col.y:F2}, {col.z:F2}) over {poses.Count(p => p.Lights.Any(q => q.Key == keys[i]))} poses");
                file.Lights.Add(e);
                done.Add(e.Key);
                string shares = string.Join(", ", poses.Select(p => (p, l: p.Lights.FirstOrDefault(q => q.Key == keys[i]))).Where(x => x.l != null)
                    .Select(x => FormattableString.Invariant($"{x.p.Name} {Lum(x.l.A) / Mathf.Max(Lum(x.p.Base), 1e-5f) * 100f:F0} %")));
                sb.AppendLine(FormattableString.Invariant($"| `{fl.Path}` | x{s:F2} | ({col.x:F2}, {col.y:F2}, {col.z:F2}) | {shares} |"));
            }
            // Keep the level's other (classifier / user) entries as they are.
            foreach (var e in CurrentEntries()) if (!done.Contains(e.Key)) file.Lights.Add(e);
            file.Lights = file.Lights.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            file.Added = _lightOv.Added.ToList();
            string dir = Path.Combine(Paths.BepInExRootPath, "ultrart", "lights", "fit");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, LightOverrides.FileName(scene)), LightOverrides.Serialize(file));
            File.WriteAllText(Path.Combine(dir, scene + ".fit.md"), sb.ToString());
            Logger.LogInfo($"Light fit '{scene}': {n} lights over {poses.Count} poses -> {Path.Combine(dir, LightOverrides.FileName(scene))}\n{sb}");
        }

        /// <summary>Every effective (non-user-reset) entry of the current level.</summary>
        private IEnumerable<LightOverrideEntry> CurrentEntries() => _lightOv?.AllEffective() ?? Enumerable.Empty<LightOverrideEntry>();

        private static float Lum(Vector3 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
        private static string V(Vector3 v) => FormattableString.Invariant($"{v.x:F4}, {v.y:F4}, {v.z:F4}");
    }
}
