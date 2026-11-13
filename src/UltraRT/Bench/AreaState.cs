using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltraRT.Bench
{
    /// <summary>
    /// What the level's area/room triggers changed since it loaded: GameObjects switched on/off, lights toggled, and
    /// the render settings they tweak (ambient, fog, skybox). Captured with F11 next to a pose and re-applied by the
    /// benchmark, so a pose deep in a level (8-3) loads correctly without re-walking the route.
    /// </summary>
    public sealed class AreaState
    {
        public int Version = 1;
        public string Scene;
        public List<Entry> Objects = new List<Entry>();
        public List<Entry> Lights = new List<Entry>();
        public float[] Ambient;
        public bool Fog;
        public float[] FogColor;
        public float FogStart, FogEnd, FogDensity;

        public sealed class Entry
        {
            public string Path;
            public bool On;
        }

        // ---------------------------------------------------------------- snapshots

        /// <summary>activeSelf of every GameObject and enabled of every Light in the active scene, keyed by a stable path.</summary>
        public sealed class Snapshot
        {
            public readonly Dictionary<string, bool> Objects = new Dictionary<string, bool>();
            public readonly Dictionary<string, bool> Lights = new Dictionary<string, bool>();

            public static Snapshot Take()
            {
                var s = new Snapshot();
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                    Walk(root.transform, root.name, s);
                return s;
            }

            private static void Walk(Transform t, string path, Snapshot s)
            {
                if ((t.gameObject.hideFlags & HideFlags.HideAndDontSave) != 0) return; // our own proxies
                s.Objects[path] = t.gameObject.activeSelf;
                var light = t.GetComponent<Light>();
                if (light != null) s.Lights[path] = light.enabled;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    Walk(c, path + "/" + c.name + "#" + i, s);
                }
            }
        }

        public static AreaState Diff(Snapshot baseline, Snapshot now, string scene)
        {
            var a = new AreaState { Scene = scene };
            foreach (var kv in now.Objects)
                if (baseline.Objects.TryGetValue(kv.Key, out var was) && was != kv.Value)
                    a.Objects.Add(new Entry { Path = kv.Key, On = kv.Value });
            foreach (var kv in now.Lights)
                if (baseline.Lights.TryGetValue(kv.Key, out var was) && was != kv.Value)
                    a.Lights.Add(new Entry { Path = kv.Key, On = kv.Value });
            var amb = RenderSettings.ambientLight;
            a.Ambient = new[] { amb.r, amb.g, amb.b, amb.a };
            a.Fog = RenderSettings.fog;
            var fc = RenderSettings.fogColor;
            a.FogColor = new[] { fc.r, fc.g, fc.b, fc.a };
            a.FogStart = RenderSettings.fogStartDistance;
            a.FogEnd = RenderSettings.fogEndDistance;
            a.FogDensity = RenderSettings.fogDensity;
            return a;
        }

        /// <summary>Re-apply: parents before children, so activating a nested area works. Returns (applied, missing).</summary>
        public (int applied, int missing) Apply()
        {
            var index = new Dictionary<string, Transform>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Index(root.transform, root.name, index);

            int applied = 0, missing = 0;
            foreach (var e in Objects.OrderBy(e => e.Path.Count(c => c == '/')))
            {
                if (index.TryGetValue(e.Path, out var t)) { t.gameObject.SetActive(e.On); applied++; }
                else missing++;
            }
            foreach (var e in Lights)
            {
                var l = index.TryGetValue(e.Path, out var t) ? t.GetComponent<Light>() : null;
                if (l != null) { l.enabled = e.On; applied++; }
                else missing++;
            }
            if (Ambient != null) RenderSettings.ambientLight = new Color(Ambient[0], Ambient[1], Ambient[2], Ambient[3]);
            RenderSettings.fog = Fog;
            if (FogColor != null) RenderSettings.fogColor = new Color(FogColor[0], FogColor[1], FogColor[2], FogColor[3]);
            RenderSettings.fogStartDistance = FogStart;
            RenderSettings.fogEndDistance = FogEnd;
            RenderSettings.fogDensity = FogDensity;
            return (applied, missing);
        }

        private static void Index(Transform t, string path, Dictionary<string, Transform> index)
        {
            index[path] = t;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Index(c, path + "/" + c.name + "#" + i, index);
            }
        }

        public void Save(string path) => File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        public static AreaState Load(string path) => JsonConvert.DeserializeObject<AreaState>(File.ReadAllText(path));
    }
}
