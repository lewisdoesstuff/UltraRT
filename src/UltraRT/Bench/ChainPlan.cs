using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UltraRT.Bench
{
    /// <summary>An approach route (Recording) loaded once per suite.</summary>
    internal sealed class Route
    {
        public string File;
        public Recording Rec;
        public float Duration => Rec.Samples.Count == 0 ? 0f : Rec.Samples[Rec.Samples.Count - 1][0];
        public string Name => Path.GetFileNameWithoutExtension(File);
    }

    /// <summary>What a scenario needs from the loaded level before it starts.</summary>
    internal enum Need
    {
        /// <summary>A level nothing has changed yet (spawn, or a pose that only teleports).</summary>
        Fresh,
        /// <summary>A fresh level, or one whose walked route is a prefix of this scenario's route.</summary>
        Route,
        /// <summary>Its own level load: recordings, FreshLoad, and the checkpoint / approach / areas methods.</summary>
        NewLoad,
    }

    /// <summary>The level state a scenario leaves behind.</summary>
    internal enum Leaves
    {
        /// <summary>Still as loaded: the player only stood at the spawn pose.</summary>
        Fresh,
        /// <summary>Its whole route has been walked and the pose was measured at the route's end.</summary>
        Route,
        /// <summary>Anything else; the next scenario reloads.</summary>
        Dirty,
    }

    internal sealed class PlanItem
    {
        /// <summary>Position in the suite file (results are reported in this order).</summary>
        public int Index;
        public Scenario Sc;
        /// <summary>Scenarios with the same key can share a level load.</summary>
        public string Key;
        public string Method;
        public Route Route;
        /// <summary>Route replay settings; chaining needs the walked part to have been replayed the same way.</summary>
        public string RouteSig;
        public Need Need;
        public Leaves Leaves;
        /// <summary>Planned predecessor in the same level load (null = fresh load). Checked again at run time.</summary>
        public string PlannedAfter;
    }

    /// <summary>The loaded level, as far as chaining is concerned.</summary>
    internal sealed class LevelState
    {
        public string Key;
        /// <summary>Nothing has moved the level on since it loaded.</summary>
        public bool Fresh = true;
        /// <summary>The route walked so far (when not Fresh), up to RouteTime.</summary>
        public Route Route;
        public float RouteTime;
        public string RouteSig;
        public CamPose Spawn;
        /// <summary>The last scenario that ran in this level load.</summary>
        public string Last;

        /// <summary>Update after a scenario ran. Returns null when the level must be reloaded for whatever comes next.</summary>
        public LevelState After(PlanItem it)
        {
            Last = it.Sc.Name;
            switch (it.Leaves)
            {
                case Leaves.Fresh: return this;
                case Leaves.Route:
                    Fresh = false;
                    Route = it.Route;
                    RouteTime = it.Route.Duration;
                    RouteSig = it.RouteSig;
                    return this;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Chaining: load each level once and reach the poses along one route in a single replay. Poses saved during one
    /// play session (F11 pressed several times) have routes that are prefixes of one another, so the longest route is
    /// replayed once and stopped at each shorter route's end to measure that pose.
    /// </summary>
    internal static class ChainPlan
    {
        /// <summary>How far (m) a shorter route may stray from the longer one and still count as its prefix.</summary>
        public const float PositionTolerance = 0.5f;
        /// <summary>The last sample of a route is the pose itself, written at F11 time between two trace samples.</summary>
        public const float EndTolerance = 3f;
        /// <summary>A measured pose this close (m) to its route's end leaves the level as the route left it.</summary>
        public const float PoseAtEnd = 1f;
        public const float EventTimeTolerance = 0.25f;

        public static string LevelKey(Scenario sc)
        {
            string picks = sc.RandomPicks == null ? "" : string.Join(",", sc.RandomPicks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            return $"{sc.Scene}|seed {sc.Seed}|enemies {sc.Enemies}|picks {picks}";
        }

        /// <summary>
        /// Classify every scenario and, with chaining on, order them so each level loads once: scenarios of one level
        /// run together (levels in order of first appearance), spawn poses first, then each chain of routes from
        /// shortest to longest, then everything that needs its own load. Without chaining the suite order is kept.
        /// </summary>
        public static List<PlanItem> Build(IList<Scenario> scenarios, Func<PathSpec, string> resolveLoad, string suiteDir, bool chain,
                                           BepInEx.Logging.ManualLogSource log)
        {
            var routes = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
            var items = new List<PlanItem>();
            for (int i = 0; i < scenarios.Count; i++)
            {
                var sc = scenarios[i];
                var it = new PlanItem { Index = i, Sc = sc, Key = LevelKey(sc), Need = Need.NewLoad, Leaves = Leaves.Dirty };
                items.Add(it);
                try { Classify(it, resolveLoad, suiteDir, routes); }
                catch (Exception e)
                {
                    // The scenario itself will report the problem when it runs.
                    log?.LogWarning($"Benchmark: '{sc.Name}' not chained: {e.Message}");
                    it.Need = Need.NewLoad;
                    it.Leaves = Leaves.Dirty;
                }
            }
            if (!chain) { Simulate(items, false); return items; }

            var ordered = new List<PlanItem>();
            foreach (var group in items.GroupBy(x => x.Key)) // GroupBy keeps first-appearance order
            {
                var list = group.ToList();
                ordered.AddRange(list.Where(x => x.Need == Need.Fresh && x.Leaves == Leaves.Fresh));

                // Chains, built from shortest to longest route. FreshLoad scenarios start a chain of their own.
                var chainable = list.Where(x => x.Route != null && x.Method == "approach-fast" && (x.Need == Need.Route || x.Sc.FreshLoad))
                    .OrderBy(x => x.Route.Duration).ThenBy(x => x.Index).ToList();
                var chains = new List<List<PlanItem>>();
                foreach (var it in chainable)
                {
                    List<PlanItem> best = null;
                    if (!it.Sc.FreshLoad)
                        foreach (var c in chains)
                        {
                            var tail = c[c.Count - 1];
                            if (tail.Leaves != Leaves.Route || tail.RouteSig != it.RouteSig) continue;
                            if (!IsPrefix(tail.Route, tail.Route.Duration, it.Route, out _)) continue;
                            if (best == null || tail.Route.Duration > best[best.Count - 1].Route.Duration) best = c;
                        }
                    if (best != null) best.Add(it); else chains.Add(new List<PlanItem> { it });
                }
                foreach (var c in chains.OrderBy(c => c.Min(x => x.Index))) ordered.AddRange(c);

                ordered.AddRange(list.Where(x => !ordered.Contains(x)));
            }
            Simulate(ordered, true);
            return ordered;
        }

        private static void Classify(PlanItem it, Func<PathSpec, string> resolveLoad, string suiteDir, Dictionary<string, Route> routes)
        {
            var p = it.Sc.Path;
            string type = (p.Type ?? "spawn").ToLowerInvariant();
            if (type == "recording") return; // replays from the level start and triggers arenas: own load
            if (type == "tour") return;      // reloads the level for every stop (Plugin.Bench.Tour.cs): own load, leaves it dirty
            it.Method = resolveLoad(p);
            if (it.Method == "approach-fast")
            {
                string file = Path.GetFullPath(Path.IsPathRooted(p.Approach) ? p.Approach : Path.Combine(suiteDir, p.Approach));
                if (!routes.TryGetValue(file, out var route))
                    routes[file] = route = new Route { File = file, Rec = Recording.Load(file) };
                if (route.Rec.Samples.Count == 0) throw new InvalidDataException($"route {file} has no samples");
                it.Route = route;
                it.RouteSig = $"speed {p.ApproachSpeed} x{p.FastTimeScale} kill {p.ApproachKillEnemies}{(p.ApproachSpare != null && p.ApproachSpare.Count > 0 ? " spare " + string.Join("+", p.ApproachSpare) : "")}";
                it.Need = it.Sc.FreshLoad ? Need.NewLoad : Need.Route;
                // A pose measured away from the route's end may have fired triggers there.
                var end = route.Rec.Samples[route.Rec.Samples.Count - 1];
                bool atEnd = type == "spawn" || p.Position == null || p.Position.Length != 3 ||
                             Dist(p.Position[0], p.Position[1], p.Position[2], end[1], end[2], end[3]) <= PoseAtEnd;
                it.Leaves = atEnd ? Leaves.Route : Leaves.Dirty;
            }
            else if (it.Method == "none")
            {
                it.Need = it.Sc.FreshLoad ? Need.NewLoad : Need.Fresh;
                bool atSpawn = type == "spawn" || p.Position == null || p.Position.Length != 3;
                it.Leaves = atSpawn ? Leaves.Fresh : Leaves.Dirty;
            }
            // checkpoint, approach (real time) and areas reproduce one specific load method: always a fresh load.
        }

        /// <summary>Can the scenario start in this level state without reloading?</summary>
        public static bool CanReuse(LevelState s, PlanItem it)
        {
            if (s == null || s.Key != it.Key) return false;
            switch (it.Need)
            {
                case Need.Fresh: return s.Fresh;
                case Need.Route:
                    if (s.Fresh) return true;
                    return s.Route != null && s.RouteSig == it.RouteSig && IsPrefix(s.Route, s.RouteTime, it.Route, out _);
                default: return false;
            }
        }

        /// <summary>Fill in PlannedAfter by walking the plan with the same rules as the run.</summary>
        private static void Simulate(List<PlanItem> items, bool chain)
        {
            LevelState s = null;
            foreach (var it in items)
            {
                if (chain && CanReuse(s, it)) it.PlannedAfter = s.Last;
                else s = new LevelState { Key = it.Key };
                s = s.After(it);
            }
        }

        private static readonly Dictionary<string, (bool ok, string why)> PrefixCache = new Dictionary<string, (bool, string)>();

        /// <summary>
        /// Was route a, walked up to time upTo, the start of route b? Positions are compared at a's sample times
        /// (b interpolated) within PositionTolerance; the arenas and checkpoints both recorded before upTo must agree.
        /// Routes saved from one play session share their timeline, so their samples line up exactly.
        /// </summary>
        public static bool IsPrefix(Route a, float upTo, Route b, out string why)
        {
            string key = a.File + "|" + upTo.ToString("R") + "|" + b.File;
            if (!PrefixCache.TryGetValue(key, out var r)) PrefixCache[key] = r = (CheckPrefix(a, upTo, b, out var w), w);
            why = r.why;
            return r.ok;
        }

        private static bool CheckPrefix(Route a, float upTo, Route b, out string why)
        {
            why = null;
            if (a.File == b.File && upTo <= b.Duration + 1e-3f) return true;
            if (a.Rec.Scene != null && b.Rec.Scene != null && a.Rec.Scene != b.Rec.Scene) { why = "different scene"; return false; }
            if (b.Duration + 0.05f < upTo) { why = $"{b.Name} is shorter ({b.Duration:F1}s < {upTo:F1}s)"; return false; }

            var sa = a.Rec.Samples;
            var sb = b.Rec.Samples;
            int j = 0, compared = 0;
            for (int i = 0; i < sa.Count; i++)
            {
                var s = sa[i];
                float t = s[0];
                if (t > upTo + 1e-4f) break;
                bool last = i == sa.Count - 1;
                var q = At(sb, t, ref j);
                float d = Dist(s[1], s[2], s[3], q[0], q[1], q[2]);
                if (d > (last ? EndTolerance : PositionTolerance)) { why = $"diverges at {t:F1}s ({d:F1} m apart)"; return false; }
                compared++;
            }
            if (compared == 0) { why = "nothing to compare"; return false; }

            float cutoff = upTo - 0.1f;
            if (!SameEvents(a.Rec.Arenas, b.Rec.Arenas, cutoff, "arenas", out why)) return false;
            // Old recordings have no checkpoint list; then positions are all there is.
            if (a.Rec.Checkpoints != null && b.Rec.Checkpoints != null && !SameEvents(a.Rec.Checkpoints, b.Rec.Checkpoints, cutoff, "checkpoints", out why)) return false;
            return true;
        }

        private static bool SameEvents(List<Recording.ArenaEvent> a, List<Recording.ArenaEvent> b, float cutoff, string what, out string why)
        {
            why = null;
            var ea = (a ?? new List<Recording.ArenaEvent>()).Where(e => e.Time <= cutoff).OrderBy(e => e.Time).ToList();
            var eb = (b ?? new List<Recording.ArenaEvent>()).Where(e => e.Time <= cutoff).OrderBy(e => e.Time).ToList();
            if (ea.Count != eb.Count) { why = $"{what} differ ({ea.Count} vs {eb.Count} before {cutoff:F1}s)"; return false; }
            for (int i = 0; i < ea.Count; i++)
                if (ea[i].Path != eb[i].Path || Math.Abs(ea[i].Time - eb[i].Time) > EventTimeTolerance)
                {
                    why = $"{what} differ at {ea[i].Time:F1}s ('{ea[i].Path}' vs '{eb[i].Path}' at {eb[i].Time:F1}s)";
                    return false;
                }
            return true;
        }

        /// <summary>Position on samples s at time t (linear), advancing the hint j.</summary>
        private static float[] At(List<float[]> s, float t, ref int j)
        {
            if (s.Count == 1 || t <= s[0][0]) return new[] { s[0][1], s[0][2], s[0][3] };
            var lastS = s[s.Count - 1];
            if (t >= lastS[0]) return new[] { lastS[1], lastS[2], lastS[3] };
            if (j >= s.Count - 1 || s[j][0] > t) j = 0;
            while (j < s.Count - 2 && s[j + 1][0] <= t) j++;
            var k1 = s[j];
            var k2 = s[j + 1];
            float u = (t - k1[0]) / Math.Max(k2[0] - k1[0], 1e-6f);
            return new[] { k1[1] + (k2[1] - k1[1]) * u, k1[2] + (k2[2] - k1[2]) * u, k1[3] + (k2[3] - k1[3]) * u };
        }

        private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx, dy = ay - by, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>One line per scenario, for the log.</summary>
        public static IEnumerable<string> Describe(List<PlanItem> items) =>
            items.Select((it, n) => $"{n + 1}. '{it.Sc.Name}' ({it.Sc.Scene}): " +
                                    (it.PlannedAfter != null ? $"chained after '{it.PlannedAfter}'" : "fresh load") +
                                    (it.Route != null ? $", route {it.Route.Name} {it.Route.Duration:F1}s" : "") +
                                    (it.Sc.FreshLoad ? ", FreshLoad" : ""));
    }
}
