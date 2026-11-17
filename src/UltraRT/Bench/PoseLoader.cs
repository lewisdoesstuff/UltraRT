using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UltraRT.Bench
{
    /// <summary>
    /// Getting to a pose deep in a level without re-walking the whole route.
    ///
    /// ULTRAKILL keeps most of a level inactive and switches rooms on as doors open and arenas finish, so a plain
    /// teleport lands in an unloaded area. Every CheckPoint knows the state the level needs for a respawn there:
    /// <c>toActivate</c> (the rooms), <c>doorsToUnlock</c> and <c>onRestart</c>. The game's own Teleport cheat
    /// (TeleportCheat.GenerateList) applies exactly those, plus the first room's door rooms; Jump does the same.
    /// From the checkpoint only the rest of the recorded route is replayed, in fast-forward.
    /// </summary>
    internal static class PoseLoader
    {
        public sealed class Pick
        {
            public CheckPoint Checkpoint;
            /// <summary>Route time at which the player was at the checkpoint; the replay resumes from here.</summary>
            public float Time;
            public string How;
        }

        /// <summary>The checkpoint the recorded route passed last, or null if it never reached one.</summary>
        public static Pick PickCheckpoint(Recording route)
        {
            var all = UnityEngine.Object.FindObjectsOfType<CheckPoint>(true).Where(c => c.toActivate != null).ToList();
            if (all.Count == 0 || route == null || route.Samples.Count == 0) return null;

            // Recordings made after checkpoint tracking was added say which checkpoint was current.
            if (route.Checkpoints != null && route.Checkpoints.Count > 0)
            {
                var e = route.Checkpoints[route.Checkpoints.Count - 1];
                var cp = Nearest(all, new Vector3(e.Position[0], e.Position[1], e.Position[2]), 3f);
                if (cp != null) return new Pick { Checkpoint = cp, Time = e.Time, How = "recorded" };
            }

            // Older recordings: the last checkpoint whose trigger the route went through.
            for (int i = route.Samples.Count - 1; i >= 0; i--)
            {
                var s = route.Samples[i];
                var p = new Vector3(s[1], s[2], s[3]);
                foreach (var cp in all)
                    if (Touches(cp, p)) return new Pick { Checkpoint = cp, Time = s[0], How = "route" };
            }
            return null;
        }

        public static CheckPoint Nearest(IEnumerable<CheckPoint> all, Vector3 p, float maxDistance)
        {
            CheckPoint best = null;
            float bestD = maxDistance * maxDistance;
            foreach (var cp in all)
            {
                float d = (cp.transform.position - p).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = cp; }
            }
            return best;
        }

        public static CheckPoint Nearest(Vector3 p, float maxDistance) =>
            Nearest(UnityEngine.Object.FindObjectsOfType<CheckPoint>(true).Where(c => c.toActivate != null), p, maxDistance);

        // The player's capsule reaches about this far from NewMovement's position.
        private const float PlayerReach = 1.75f;

        /// <summary>Would a player standing at p be inside the checkpoint's trigger? Works for inactive checkpoints too.</summary>
        private static bool Touches(CheckPoint cp, Vector3 p)
        {
            if ((cp.transform.position - p).sqrMagnitude > 60f * 60f) return false;
            bool any = false;
            foreach (var col in cp.GetComponentsInChildren<Collider>(true))
            {
                if (!col.isTrigger) continue;
                any = true;
                var local = col.transform.InverseTransformPoint(p);
                var scale = col.transform.lossyScale;
                var reach = new Vector3(PlayerReach / Mathf.Max(Mathf.Abs(scale.x), 1e-3f), PlayerReach / Mathf.Max(Mathf.Abs(scale.y), 1e-3f), PlayerReach / Mathf.Max(Mathf.Abs(scale.z), 1e-3f));
                if (col is BoxCollider box)
                {
                    var d = local - box.center;
                    var h = box.size * 0.5f + reach;
                    if (Mathf.Abs(d.x) <= h.x && Mathf.Abs(d.y) <= h.y && Mathf.Abs(d.z) <= h.z) return true;
                }
                else if (col is SphereCollider sphere)
                {
                    float r = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) + PlayerReach;
                    if ((col.transform.TransformPoint(sphere.center) - p).sqrMagnitude <= r * r) return true;
                }
                else if ((cp.transform.position - p).sqrMagnitude <= 9f) return true;
            }
            return !any && (cp.transform.position - p).sqrMagnitude <= 9f;
        }

        /// <summary>
        /// What the game's Teleport cheat does for a checkpoint: activate its rooms, unlock its doors, run its
        /// restart event, and start the level (music, timer, first-room door rooms). Returns where the player stands.
        /// </summary>
        public static CamPose Jump(CheckPoint cp)
        {
            var first = UnityEngine.Object.FindObjectOfType<FirstRoomPrefab>();

            var t = cp.transform;
            var pose = new CamPose(t.position + t.up * 1.25f, t.rotation.eulerAngles.y + 0.01f, 0f);

            MonoSingleton<PlayerTracker>.Instance.LevelStart();

            cp.toActivate.SetActive(true);
            if (cp.doorsToUnlock != null)
                foreach (var door in cp.doorsToUnlock)
                {
                    if (door == null) continue;
                    if (door.locked) door.Unlock();
                    if (door.startOpen) door.Open();
                }
            cp.onRestart?.Invoke();

            if (first != null && first.mainDoor != null && first.mainDoor.activatedRooms != null)
                foreach (var room in first.mainDoor.activatedRooms)
                    if (room != null) room.SetActive(true);
            return pose;
        }

        // ---------------------------------------------------------------- the game's Teleport Menu cheat

        /// <summary>One entry of the game's Teleport Menu (the cheat): its label and the button that performs it.</summary>
        public sealed class TeleportEntry
        {
            public string Name;
            public Component Button;
        }

        private static readonly FieldInfo TeleportTemplate = AccessTools.Field(typeof(TeleportCheat), "buttonTemplate");
        private static readonly MethodInfo TeleportGenerate = AccessTools.Method(typeof(TeleportCheat), "GenerateList");

        /// <summary>
        /// Opens the game's Teleport Menu (CheatsController.ShowTeleportPanel, as the cheat does) and lists its entries in
        /// menu order: "First Room", then every checkpoint the game lets the player teleport to, sorted as the game sorts
        /// them. The panel builds its buttons in its Start, so this takes a frame or two. Leaves the panel open (the game
        /// frozen): follow with <see cref="Teleport"/> or <see cref="CloseTeleportMenu"/>.
        /// UI types are reached by name: the plugin only references UnityEngine.UI when PluginConfigurator is present.
        /// </summary>
        public static System.Collections.IEnumerator OpenTeleportMenu(List<TeleportEntry> into)
        {
            into.Clear();
            var cc = MonoSingleton<CheatsController>.Instance;
            if (cc == null) yield break;
            cc.ShowTeleportPanel();
            TeleportCheat menu = null;
            GameObject template = null;
            for (int f = 0; f < 10; f++)
            {
                if (menu == null) menu = cc.cheatsTeleportPanel != null ? cc.cheatsTeleportPanel.GetComponentInChildren<TeleportCheat>(true) : null;
                if (menu == null) menu = UnityEngine.Object.FindObjectOfType<TeleportCheat>(true);
                if (menu != null && template == null) template = TeleportTemplate?.GetValue(menu) as GameObject;
                if (template != null && !template.activeSelf) break; // GenerateList ran: it switches the template off last
                yield return null;
            }
            if (menu != null && template != null && template.activeSelf)
            {
                // The panel's Start has not run (its canvas is hidden in a bench run): build the list as Start would.
                Log?.LogInfo($"Benchmark: teleport menu built directly (panel active {menu.gameObject.activeSelf}, in hierarchy {menu.gameObject.activeInHierarchy})");
                try { TeleportGenerate?.Invoke(menu, null); }
                catch (Exception ex) { Log?.LogWarning("Benchmark: the teleport menu's GenerateList failed: " + (ex.InnerException ?? ex).Message); }
            }
            if (menu == null || template == null || template.activeSelf)
            {
                Log?.LogWarning($"Benchmark: the game's teleport menu did not build its list (menu {(menu != null ? "found" : "missing")}, template {(template != null ? "found" : "missing")})");
                yield break;
            }
            foreach (Transform child in template.transform.parent)
            {
                if (child.gameObject == template || !child.gameObject.activeSelf) continue;
                var button = child.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "Button");
                if (button == null) continue;
                var text = child.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().GetProperty("text") != null && c.GetType().Name.StartsWith("TextMeshPro"));
                string name = text != null ? text.GetType().GetProperty("text").GetValue(text) as string : child.name;
                into.Add(new TeleportEntry { Name = System.Text.RegularExpressions.Regex.Replace(name ?? child.name, "<.*?>", "").Trim(), Button = button });
            }
        }

        /// <summary>Presses a Teleport Menu entry: the game moves the player, restores the checkpoint's rooms, doors and
        /// restart event, starts the level and closes the menu, exactly as when the player clicks it.</summary>
        public static bool Teleport(TeleportEntry e)
        {
            var onClick = e?.Button != null ? e.Button.GetType().GetProperty("onClick")?.GetValue(e.Button) as UnityEngine.Events.UnityEventBase : null;
            var invoke = onClick?.GetType().GetMethod("Invoke", Type.EmptyTypes);
            if (invoke == null) return false;
            invoke.Invoke(onClick, null);
            return true;
        }

        /// <summary>Closes the Teleport Menu without teleporting (what its own Pause handler does).</summary>
        public static void CloseTeleportMenu()
        {
            var menu = UnityEngine.Object.FindObjectOfType<TeleportCheat>(true);
            if (menu != null && menu.gameObject.activeSelf)
            {
                menu.gameObject.SetActive(false);
                MonoSingleton<OptionsManager>.Instance?.UnFreeze();
            }
        }

        public static string Describe(CheckPoint cp)
        {
            var p = cp.transform.position;
            return $"'{GameControl.PathOf(cp.transform)}' at ({p.x:F1}, {p.y:F1}, {p.z:F1}) rooms '{(cp.toActivate != null ? cp.toActivate.name : "-")}'";
        }

        /// <summary>Every checkpoint in the level and how close the route came to it (for the log).</summary>
        public static IEnumerable<string> DescribeAll(Recording route)
        {
            foreach (var cp in UnityEngine.Object.FindObjectsOfType<CheckPoint>(true))
            {
                float best = float.MaxValue, bestT = 0f;
                foreach (var s in route.Samples)
                {
                    float d = (new Vector3(s[1], s[2], s[3]) - cp.transform.position).sqrMagnitude;
                    if (d < best) { best = d; bestT = s[0]; }
                }
                string cols = string.Join(",", cp.GetComponentsInChildren<Collider>(true).Where(c => c.isTrigger).Select(c => c is BoxCollider b ? $"box{b.size}*{c.transform.lossyScale}" : c.GetType().Name));
                yield return $"checkpoint {Describe(cp)} active={cp.gameObject.activeInHierarchy} colliders=[{cols}] route came within {Mathf.Sqrt(best):F1} m at {bestT:F1}s";
            }
        }

        // ---------------------------------------------------------------- deterministic level randomness

        /// <summary>Seed for the level's own random picks (Scenario.Seed).</summary>
        public static int Seed = 1;
        /// <summary>Scenario.RandomPicks: picker object name -> entry object name to force.</summary>
        public static Dictionary<string, string> Pins;
        public static BepInEx.Logging.ManualLogSource Log;

        /// <summary>
        /// Levels pick some rooms at random (8-3's "false start" is one of several). When that happens depends on
        /// frame timing, so a global seed alone does not repeat it. In benchmarks each picker is seeded from its own
        /// name instead, or forced to a given entry.
        /// </summary>
        public static void InstallDeterminism(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(PoseLoader).GetMethod(nameof(BeforeRandomize), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var type in new[] { typeof(Randomness.RandomSetActive), typeof(Randomness.RandomInstantiate) })
            {
                var m = AccessTools.DeclaredMethod(type, "RandomizeWithCount");
                if (m != null) harmony.Patch(m, prefix: prefix);
            }
        }

        private static bool BeforeRandomize(MonoBehaviour __instance)
        {
            if (!BenchSafety.Active) return true;
            string path = GameControl.PathOf(__instance.transform).Replace("(Clone)", "");
            if (Pins != null && __instance is Randomness.RandomSetActive picker && picker.entries != null &&
                Pins.TryGetValue(__instance.name.Replace("(Clone)", ""), out var want))
            {
                var entry = picker.entries.FirstOrDefault(e => e.targetObject != null && e.targetObject.name.Replace("(Clone)", "") == want);
                if (entry != null)
                {
                    if (picker.resetStatesOnRandomize)
                        foreach (var e in picker.entries) if (e.targetObject != null) e.targetObject.SetActive(false);
                    entry.targetObject.SetActive(true);
                    Log?.LogInfo($"Benchmark: random pick '{path}' forced to '{want}'");
                    return false;
                }
                Log?.LogWarning($"Benchmark: random pick '{path}' has no entry '{want}' (entries: {string.Join(", ", picker.entries.Where(e => e.targetObject != null).Select(e => e.targetObject.name))})");
            }
            // FNV-1a of the path: stable across runs, unlike string.GetHashCode.
            uint h = 2166136261;
            foreach (char c in path) h = (h ^ c) * 16777619;
            UnityEngine.Random.InitState(Seed ^ (int)h);
            string entries = __instance is Randomness.RandomSetActive p && p.entries != null ? string.Join(", ", p.entries.Where(e => e.targetObject != null).Select(e => e.targetObject.name)) : "";
            Log?.LogInfo($"Benchmark: random pick '{path}' seeded{(entries.Length > 0 ? $" (entries: {entries}; pin one with RandomPicks)" : "")}");
            return true;
        }

        // ---------------------------------------------------------------- game speed

        private static readonly FieldInfo SlowDown = AccessTools.Field(typeof(TimeController), "slowDown");

        /// <summary>Fast-forward the whole game (physics, door animations, spawn timers). 1 = normal.</summary>
        public static void SetGameSpeed(float scale)
        {
            var tc = MonoSingleton<TimeController>.Instance;
            if (tc != null)
            {
                tc.timeScaleModifier = scale;
                SlowDown?.SetValue(tc, tc.timeScale * scale); // TimeController eases Time.timeScale towards its target otherwise
                Time.timeScale = tc.timeScale * scale;
            }
            else Time.timeScale = scale;
        }
    }

    // ---------------------------------------------------------------- validation fingerprint

    /// <summary>What is loaded around a pose: the level's own mesh renderers that are switched on. Used to compare load methods.</summary>
    internal sealed class SceneFingerprint
    {
        public readonly Dictionary<string, int> Renderers = new Dictionary<string, int>();
        public readonly Dictionary<string, int> Near = new Dictionary<string, int>();
        public int Count, NearCount, Lights;
        public const float NearDistance = 120f;

        public static SceneFingerprint Take(Vector3 camera)
        {
            var f = new SceneFingerprint();
            var player = GameControl.Player != null ? GameControl.Player.transform : null;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.enabled || SceneProxies.IsProxyObject(r.gameObject)) continue;
                if (player != null && r.transform.IsChildOf(player)) continue;
                if (r.GetComponentInParent<EnemyIdentifier>() != null) continue;
                string path = GameControl.PathOf(r.transform);
                if (path.Contains("Gore Zone") || path.Contains("Gib Zone") || path.Contains("Deleted Limb Zone")) continue;
                // Rooms are re-instantiated by checkpoints, so names gain "(Clone)". No position in the key: props and
                // platforms move. Same-named siblings are told apart by count.
                string key = path.Replace("(Clone)", "");
                Add(f.Renderers, key);
                f.Count++;
                if (r.bounds.SqrDistance(camera) <= NearDistance * NearDistance) { Add(f.Near, key); f.NearCount++; }
            }
            f.Lights = UnityEngine.Object.FindObjectsOfType<Light>().Count(l => l.isActiveAndEnabled && l.intensity > 0);
            return f;

            void Add(Dictionary<string, int> d, string k) { d.TryGetValue(k, out int n); d[k] = n + 1; }
        }

        /// <summary>Entries of a that b lacks (with multiplicity).</summary>
        public static List<string> Missing(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var list = new List<string>();
            foreach (var kv in a)
            {
                b.TryGetValue(kv.Key, out int n);
                for (int i = n; i < kv.Value; i++) list.Add(kv.Key);
            }
            return list;
        }
    }
}
