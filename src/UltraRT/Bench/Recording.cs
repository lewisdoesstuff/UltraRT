using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace UltraRT.Bench
{
    /// <summary>On-disk recording: the player's path and look, plus arena triggers, sampled while someone plays.</summary>
    public sealed class Recording
    {
        public int Version = 1;
        public string Scene;
        public string Created;
        public float SampleRate = 60f;
        /// <summary>Per sample: t, x, y, z, yaw, pitch.</summary>
        public List<float[]> Samples = new List<float[]>();
        public List<ArenaEvent> Arenas = new List<ArenaEvent>();

        public sealed class ArenaEvent
        {
            public float Time;
            public string Path;
            public float[] Position;
        }

        public CameraPath ToPath(float start = 0f, float end = float.MaxValue)
        {
            var keys = Samples.Where(s => s[0] >= start && s[0] <= end)
                .Select(s => new PathKey { Time = s[0] - start, Pose = new CamPose(new Vector3(s[1], s[2], s[3]), s[4], s[5]) })
                .ToList();
            return new CameraPath(keys);
        }

        public static Recording Load(string path) => JsonConvert.DeserializeObject<Recording>(File.ReadAllText(path));

        public void Save(string path) => File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.None));
    }

    /// <summary>
    /// F10 starts/stops recording while you play normally (no cheats, saves untouched). Samples the player's pose at
    /// SampleRate and logs every ActivateArena.Activate with its hierarchy path, so replays can trigger the same fights.
    /// </summary>
    internal sealed class Recorder
    {
        public static Recorder Current { get; private set; }

        private readonly string _dir;
        private readonly BepInEx.Logging.ManualLogSource _log;
        private Recording _rec;
        private float _t0, _nextSample;

        public bool IsRecording => _rec != null;
        public string LastSavedName { get; private set; }
        public float Elapsed => _rec == null ? 0f : Time.realtimeSinceStartup - _t0;

        public Recorder(string dir, BepInEx.Logging.ManualLogSource log)
        {
            _dir = dir;
            _log = log;
            Directory.CreateDirectory(dir);
            Current = this;
        }

        public void Toggle()
        {
            if (_rec == null) Start(); else Stop();
        }

        private void Start()
        {
            if (GameControl.Player == null || GameControl.Cam == null) { _log.LogWarning("Recorder: no player in this scene"); return; }
            _rec = new Recording { Scene = SceneHelper.CurrentScene, Created = DateTime.Now.ToString("s") };
            _t0 = Time.realtimeSinceStartup;
            _nextSample = 0f;
            _log.LogInfo($"Recording started in {_rec.Scene}");
        }

        public void Stop()
        {
            if (_rec == null) return;
            string name = $"{Sanitize(_rec.Scene)}_{DateTime.Now:yyyyMMdd-HHmmss}.json";
            string path = Path.Combine(_dir, name);
            _rec.Save(path);
            LastSavedName = name;
            _log.LogInfo($"Recording saved: {path} ({_rec.Samples.Count} samples, {Elapsed:F1}s, {_rec.Arenas.Count} arena events)");
            _rec = null;
        }

        // ---------------------------------------------------------------- background approach trace

        // Always-on, low-rate trace since the level loaded. F11 saves it as the "approach" to a pose, so benchmarks
        // can re-walk the route and fire the same room/area triggers a teleport would skip.
        private Recording _trace;
        private string _traceScene;
        private float _traceT0, _traceNext;
        private const float TraceRate = 30f;

        private void UpdateTrace()
        {
            var scene = SceneHelper.CurrentScene;
            if (GameControl.Player == null || GameControl.Cam == null) { _trace = null; return; }
            if (_trace == null || _traceScene != scene)
            {
                _trace = new Recording { Scene = scene, Created = DateTime.Now.ToString("s"), SampleRate = TraceRate };
                _traceScene = scene;
                _traceT0 = Time.realtimeSinceStartup;
                _traceNext = 0f;
                _areaBaseline = AreaState.Snapshot.Take(); // before the player has passed any area trigger
            }
            float t = Time.realtimeSinceStartup - _traceT0;
            if (t < _traceNext) return;
            _traceNext = t + 1f / TraceRate;
            var p = GameControl.CurrentPose();
            _trace.Samples.Add(new[] { t, p.Position.x, p.Position.y, p.Position.z, p.Yaw, p.Pitch });
        }

        private AreaState.Snapshot _areaBaseline;

        /// <summary>Save what area triggers changed since the level loaded (objects/lights toggled, ambient, fog). Returns the file name.</summary>
        public string SaveAreas()
        {
            if (_areaBaseline == null || _trace == null) return null;
            var diff = AreaState.Diff(_areaBaseline, AreaState.Snapshot.Take(), _trace.Scene);
            string name = $"areas_{Sanitize(_trace.Scene)}_{DateTime.Now:yyyyMMdd-HHmmss}.json";
            diff.Save(Path.Combine(_dir, name));
            _log.LogInfo($"Area state saved: {name} ({diff.Objects.Count} objects, {diff.Lights.Count} lights changed)");
            return name;
        }
        /// <summary>Save the route walked since the level loaded (ending at the current pose). Returns the file name.</summary>
        public string SaveApproach()
        {
            if (_trace == null || _trace.Samples.Count == 0) return null;
            var p = GameControl.CurrentPose();
            float t = Time.realtimeSinceStartup - _traceT0;
            _trace.Samples.Add(new[] { t, p.Position.x, p.Position.y, p.Position.z, p.Yaw, p.Pitch });
            string name = $"approach_{Sanitize(_trace.Scene)}_{DateTime.Now:yyyyMMdd-HHmmss}.json";
            _trace.Save(Path.Combine(_dir, name));
            _log.LogInfo($"Approach saved: {name} ({_trace.Samples.Count} samples, {t:F1}s, {_trace.Arenas.Count} arena events)");
            return name;
        }

        public void Update()
        {
            UpdateTrace();
            if (_rec == null) return;
            if (GameControl.Player == null) { Stop(); return; } // level ended / changed
            float t = Elapsed;
            if (t < _nextSample) return;
            _nextSample = t + 1f / _rec.SampleRate;
            var p = GameControl.CurrentPose();
            _rec.Samples.Add(new[] { t, p.Position.x, p.Position.y, p.Position.z, p.Yaw, p.Pitch });
        }

        public void OnArenaActivated(ActivateArena arena)
        {
            if (arena == null) return;
            var pos = arena.transform.position;
            var path = GameControl.PathOf(arena.transform);
            if (_rec != null) _rec.Arenas.Add(new Recording.ArenaEvent { Time = Elapsed, Path = path, Position = new[] { pos.x, pos.y, pos.z } });
            if (_trace != null) _trace.Arenas.Add(new Recording.ArenaEvent { Time = Time.realtimeSinceStartup - _traceT0, Path = path, Position = new[] { pos.x, pos.y, pos.z } });
        }

        private static string Sanitize(string s) => new string((s ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        [HarmonyPatch(typeof(ActivateArena), nameof(ActivateArena.Activate))]
        private static class ArenaPatch
        {
            private static void Prefix(ActivateArena __instance, out bool __state) => __state = __instance.activated;

            private static void Postfix(ActivateArena __instance, bool __state)
            {
                if (!__state && __instance.activated) Current?.OnArenaActivated(__instance);
            }
        }
    }
}
