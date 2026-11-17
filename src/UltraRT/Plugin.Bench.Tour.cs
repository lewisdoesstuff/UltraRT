using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UltraRT.Bench;
using UnityEngine;

namespace UltraRT
{
    /// <summary>
    /// Bench path type "tour" (bench/README.md, "Tour"; docs/lighting-overrides.md, "Scoreboard"): screenshots of every mode
    /// at each entry of the game's Teleport Menu cheat (the first room and every checkpoint), for levels without recorded
    /// routes. Every stop is a fresh level load plus one press of the menu's own button, so each is the state the cheat
    /// gives a player; successive teleports in one load would leave earlier rooms (and their lights, which the game does
    /// not shadow) switched on.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>
        /// View directions at a stop: of eight candidates 45 degrees apart (from the stop's own facing), the ones with the
        /// longest free line of sight, at least 60 degrees apart. Sorted by yaw offset, so a stop's views keep their order.
        /// </summary>
        private static List<float> TourYaws(Vector3 eye, float baseYaw, int views)
        {
            int mask = ~(1 << 2); // everything but Ignore Raycast, as the light classifier
            var cands = new List<(float offset, float dist)>();
            for (int k = 0; k < 8; k++)
            {
                float off = k * 45f;
                var dir = Quaternion.Euler(0f, baseYaw + off, 0f) * Vector3.forward;
                float d = Physics.Raycast(eye, dir, out var hit, 80f, mask, QueryTriggerInteraction.Ignore) ? hit.distance : 80f;
                cands.Add((off, d));
            }
            var picked = new List<float>();
            foreach (var c in cands.OrderByDescending(c => c.dist).ThenBy(c => c.offset))
            {
                if (picked.Count >= Mathf.Max(1, views)) break;
                if (picked.Any(p => Mathf.Abs(Mathf.DeltaAngle(p, c.offset)) < 60f)) continue;
                picked.Add(c.offset);
            }
            picked.Sort();
            return picked.Select(o => baseYaw + o).ToList();
        }

        private IEnumerator RunTour(Scenario sc, ScenarioResult sr, string outDir)
        {
            var spec = sc.Tour ?? new TourSpec();
            if (_benchNoShots) { Logger.LogInfo($"Benchmark: tour '{sc.Name}' skipped (no screenshots in this run)"); yield break; }
            sr.Load = "tour";
            sr.Tour = new List<TourStop>();
            string key = _level != null ? _level.Key : null;

            // The stop list is the game's own Teleport Menu (the cheat): "First Room", then every checkpoint it offers.
            // Read once from the level as loaded (EnsureLevel gave this scenario a fresh one).
            var entries = new List<PoseLoader.TeleportEntry>();
            yield return PoseLoader.OpenTeleportMenu(entries);
            PoseLoader.CloseTeleportMenu();
            // The menu's order changes from load to load (see below), so the stops are numbered by label: "First Room",
            // then the checkpoints in ordinal order. A stop keeps its number, and its file names, from run to run.
            var names = entries.Select(e => e.Name).OrderBy(n => n == "First Room" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList();
            Logger.LogInfo($"Benchmark: tour '{sc.Name}': the teleport menu lists {names.Count} entries: " + string.Join("; ", names.Select((n, i) => $"stop {i} '{n}'")));
            var stops = Enumerable.Range(0, names.Count).Where(i => spec.Start || names[i] != "First Room").ToList();
            if (spec.Only != null && spec.Only.Count > 0) stops = stops.Where(i => spec.Only.Contains(i)).ToList();
            if (spec.Max > 0 && stops.Count > spec.Max)
            {
                // Evenly over the level, always with the first and the last.
                var keep = new List<int>();
                for (int k = 0; k < spec.Max; k++) keep.Add(stops[Mathf.RoundToInt(k * (stops.Count - 1) / (float)Mathf.Max(1, spec.Max - 1))]);
                stops = keep.Distinct().ToList();
            }
            if (stops.Count == 0) Logger.LogWarning($"Benchmark: tour '{sc.Name}': nothing to visit");

            bool fresh = true; // the level EnsureLevel loaded
            foreach (int index in stops)
            {
                float t0 = Time.realtimeSinceStartup;
                if (!fresh)
                {
                    GameControl.ReleaseControl();
                    yield return LoadLevel(sc);
                    _level = new LevelState { Key = key, Spawn = GameControl.CurrentPose() };
                }
                fresh = false;
                ApplyBenchMode("off");

                // Press the entry as the player would: the game moves the player and the camera, switches the checkpoint's
                // rooms on, unlocks its doors, runs its restart event and starts the level.
                // Found by name: the menu's order is not the same on every load (3-1 swaps neighbours; the game sorts by
                // sibling index, and checkpoints clone their rooms in place as they start).
                yield return PoseLoader.OpenTeleportMenu(entries);
                string name = names[index];
                int nth = names.Take(index).Count(n => n == name);
                var entry = entries.Where(e => e.Name == name).Skip(nth).FirstOrDefault();
                if (entry == null || !PoseLoader.Teleport(entry))
                {
                    PoseLoader.CloseTeleportMenu();
                    Logger.LogWarning($"Benchmark: tour stop {index} '{name}' is not in the teleport menu after the reload ({string.Join("; ", entries.Select(e => e.Name))}); skipped");
                    continue;
                }
                yield return null;
                var pose = GameControl.CurrentPose();
                Logger.LogInfo($"Benchmark: tour stop {index}: '{name}' at ({pose.Position.x:F1}, {pose.Position.y:F1}, {pose.Position.z:F1}), facing {pose.Yaw:F0}");
                GameControl.TakeControl(pose);
                GameControl.HideLevelTitle();
                // The rooms a checkpoint switches on run their Start()s next frame (and clone themselves there).
                float until = Time.realtimeSinceStartup + 0.5f + Mathf.Max(0f, spec.Settle);
                while (Time.realtimeSinceStartup < until) { GameControl.SetPose(pose); GameControl.HideCheatPanels(); yield return null; }
                if (spec.Step > 0f)
                {
                    // Walk, not jump: a trigger the step crosses must see the player inside it on a physics step.
                    var from = pose.Position;
                    var dir = Quaternion.Euler(0f, pose.Yaw, 0f) * Vector3.forward;
                    float walked = 0f;
                    while (walked < spec.Step)
                    {
                        walked = Mathf.Min(spec.Step, walked + 8f * Time.unscaledDeltaTime);
                        pose.Position = from + dir * walked;
                        GameControl.SetPose(pose);
                        GameControl.HideCheatPanels();
                        yield return null;
                    }
                    Logger.LogInfo($"Benchmark: tour stop {index}: stepped {spec.Step:0.#} m forward to ({pose.Position.x:F1}, {pose.Position.y:F1}, {pose.Position.z:F1})");
                    until = Time.realtimeSinceStartup + 1.5f;
                    while (Time.realtimeSinceStartup < until) { GameControl.SetPose(pose); GameControl.HideCheatPanels(); yield return null; }
                }

                // Let the RT scene register what the stop switched on, then give RR its feature (as RunScenario does).
                string warm = sc.Modes.FirstOrDefault(m => m != "off");
                if (warm != null && _proxies != null)
                {
                    ApplyBenchMode(warm);
                    float w1 = Time.realtimeSinceStartup + 5f;
                    int frames = 0;
                    while (Time.realtimeSinceStartup < w1 && (frames++ < 30 || _proxies.DeferredCount > 0 || Patches.PendingSpawns.Count > 0))
                    {
                        GameControl.SetPose(pose);
                        GameControl.HideCheatPanels();
                        yield return null;
                    }
                    yield return WaitForRR();
                    ApplyBenchMode("off");
                }

                var eye = GameControl.Cam != null ? GameControl.Cam.transform.position : pose.Position;
                var yaws = TourYaws(eye, pose.Yaw, spec.Views);
                var stop = new TourStop { Index = index, Name = name, Position = new[] { pose.Position.x, pose.Position.y, pose.Position.z } };
                for (int v = 0; v < yaws.Count; v++)
                {
                    var view = new CamPose(pose.Position, yaws[v], spec.Pitch);
                    string prefix = $"{Sanitize(sc.Name)}-s{index:00}-v{v}";
                    GameControl.SetPose(view);
                    GameControl.HideLevelTitle();
                    foreach (var m in sc.Modes)
                    {
                        ApplyBenchMode(m);
                        yield return WaitForRR();
                        for (int f = 0; f < 12; f++) { GameControl.SetPose(view); GameControl.HideCheatPanels(); yield return null; }
                        float settle = Time.realtimeSinceStartup + Mathf.Max(0f, sc.ShotSettle) + (AutoExposureFor(m.Split('+')[0]) ? 0.6f : 0f);
                        while (Time.realtimeSinceStartup < settle) { GameControl.HideCheatPanels(); yield return null; }
                        if (AutoExposureFor(m.Split('+')[0])) Logger.LogInfo($"Benchmark: '{sc.Name}' stop {index} view {v} {m}: {ExposureSummary()}");
                        yield return new WaitForEndOfFrame();
                        string file = $"{prefix}_0.0s_{Sanitize(m)}.png";
                        SaveScreen(Path.Combine(outDir, file), spec.Scale);
                        sr.Screenshots.Add(file);
                        yield return null;
                    }
                    stop.Yaws.Add(yaws[v]);
                    stop.Views.Add(prefix + "_0.0s");
                }
                ApplyBenchMode("off");
                stop.LoadSeconds = Time.realtimeSinceStartup - t0;
                sr.Tour.Add(stop);
            }
            sr.LoadDetail = $"{sr.Tour.Count} stops, {sr.Tour.Sum(s => s.Views.Count)} views";
            WriteLightCensus(outDir); // -ultrart-light-census (Plugin.LightClasses.cs)
            sr.Lights = _lightCount;
            sr.Instances = (int)(_proxies?.Rtas?.GetInstanceCount() ?? 0);
        }

        /// <summary>The screen as it is now (call after WaitForEndOfFrame), optionally scaled down, as PNG.</summary>
        private static void SaveScreen(string path, float scale)
        {
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                int w = Mathf.Max(16, Mathf.RoundToInt(shot.width * Mathf.Clamp(scale, 0.1f, 1f)));
                int h = Mathf.Max(16, Mathf.RoundToInt(shot.height * Mathf.Clamp(scale, 0.1f, 1f)));
                if (w >= shot.width)
                {
                    File.WriteAllBytes(path, shot.EncodeToPNG());
                    return;
                }
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                shot.filterMode = FilterMode.Bilinear; // an exact 2x2 box at scale 0.5
                Graphics.Blit(shot, rt);
                RenderTexture.active = rt;
                var small = new Texture2D(w, h, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                small.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(path, small.EncodeToPNG());
                Destroy(small);
            }
            finally { Destroy(shot); }
        }
    }
}
