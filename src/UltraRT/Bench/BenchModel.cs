using System.Collections.Generic;

namespace UltraRT.Bench
{
    /// <summary>A suite file: bench/suites/*.json.</summary>
    public sealed class Suite
    {
        public string Name = "suite";
        public List<Scenario> Scenarios = new List<Scenario>();
    }

    public sealed class Scenario
    {
        public string Name;
        public string Scene;
        public int Seed = 1;
        /// <summary>
        /// Force the level's random room picks: picker object name -> entry object name, e.g. { "False Starts": "1-1" }.
        /// Pickers not listed are seeded from Seed and their own name. The log lists each picker and its entries.
        /// </summary>
        public Dictionary<string, string> RandomPicks;
        /// <summary>Seconds to let the level settle after loading (and RT setup) before anything happens.</summary>
        public float Settle = 3f;
        /// <summary>
        /// Always load the level for this scenario, never reuse one an earlier scenario left (see "Chaining" in
        /// bench/README.md). Later scenarios may still chain after it.
        /// </summary>
        public bool FreshLoad;
        public PathSpec Path = new PathSpec();
        /// <summary>blind: enemies idle (still animate); active: they fight an invincible player; none: no spawns.</summary>
        public string Enemies = "blind";
        /// <summary>For recordings: trigger the recorded arenas at their recorded times.</summary>
        public bool ReplayArenas = true;
        /// <summary>Modes compared within each pass, alternating every Segment seconds. "off" = game only.</summary>
        public List<string> Modes = new List<string> { "off", "pt-dn" };
        public float Segment = 0.5f;
        /// <summary>Frames ignored after each mode switch (GPU timer lag, denoiser history warm-up).</summary>
        public int SwitchSkipFrames = 6;
        public int Rounds = 1;
        /// <summary>Path times (s) at which every mode is screenshotted (first round only).</summary>
        public List<float> Screenshots = new List<float>();
        /// <summary>Kill enemies as they spawn during the measurement (recordings of a route: finished arenas open the next rooms).</summary>
        public bool KillEnemies;
    }

    public sealed class PathSpec
    {
        /// <summary>spawn | static | sweep | recording</summary>
        public string Type = "spawn";
        public float Duration = 8f;
        // static
        public float[] Position;
        public float Yaw, Pitch;
        // sweep (around the spawn pose, or Position if given)
        public float YawSpeed = 90f;
        public float PitchAmplitude = 15f;
        // approach: re-walk a recorded route first (untimed) so room/area triggers fire like they did for the player.
        // F11 saves one with every pose. Without Position, the pose defaults to the approach's last sample.
        public string Approach;
        // areas: re-apply the object/light/render-settings changes area triggers had made when the pose was saved (F11).
        public string Areas;
        /// <summary>
        /// How to get the level into the state the pose needs:
        /// approach-fast = fast-forward the whole Approach route (same result as walking it);
        /// checkpoint = jump to the last checkpoint on the route (rooms restored the way the game's teleport cheat
        /// does), then fast-forward the rest; quickest, but the room behind that checkpoint is not loaded;
        /// approach = walk the whole route in real time; areas = re-apply the Areas diff; none = just teleport.
        /// Default (auto): approach-fast when there is an Approach, else areas when there is an Areas file, else none.
        /// </summary>
        public string Load = "auto";
        /// <summary>Game speed while fast-forwarding a route (checkpoint and approach-fast).</summary>
        public float FastTimeScale = 6f;
        public float ApproachSpeed = 1.5f;
        /// <summary>Kill enemies as they spawn during the approach, so arena completions open doors and load areas.</summary>
        public bool ApproachKillEnemies = true;
        // recording
        public string File;
        public float Start;
        public float End = -1f;
    }

    // ---------------------------------------------------------------- results

    public sealed class SuiteResult
    {
        public string Suite;
        public string Tag;
        public string Started;
        public string Gpu;
        public string Api;
        public string Resolution;
        public float TraceScale;
        public bool GpuTimer;
        /// <summary>Levels loaded once and reused by later scenarios (off with -ultrart-bench-nochain).</summary>
        public bool Chaining;
        public int LevelLoads;
        /// <summary>Wall-clock seconds from the plugin starting the suite to the summary.</summary>
        public float TotalSeconds;
        public List<ScenarioResult> Scenarios = new List<ScenarioResult>();
    }

    public sealed class ScenarioResult
    {
        public string Name;
        public string Scene;
        public float Duration;
        public int Instances;
        public int Lights;
        public string Error;
        /// <summary>How the pose was reached (see PathSpec.Load) and how long that took after the level had settled.</summary>
        public string Load;
        public float LoadSeconds;
        /// <summary>"fresh load" or "chained after '&lt;scenario&gt;'" (the level that scenario left was reused).</summary>
        public string Reached;
        public string ChainedAfter;
        /// <summary>Level load including Settle (0 when chained); Load detail; whole scenario wall-clock; run order (1-based).</summary>
        public float LevelLoadSeconds;
        public string LoadDetail;
        public float WallSeconds;
        public int Order;
        public Dictionary<string, ModeStats> Modes = new Dictionary<string, ModeStats>();
        public List<string> Screenshots = new List<string>();
    }

    // ---------------------------------------------------------------- load validation (-ultrart-bench-validate)

    public sealed class LoadCheck
    {
        public string Scenario;
        public string Scene;
        public List<LoadCheckMethod> Methods = new List<LoadCheckMethod>();
    }

    public sealed class LoadCheckMethod
    {
        public string Load;
        public string Detail;
        public string Error;
        public float LoadSeconds;
        /// <summary>Level mesh renderers switched on (enemies, gore and the player excluded); Near = within 120 m of the camera.</summary>
        public int Renderers, NearRenderers, Lights, Instances;
        // Against the first method (the reference):
        public int MissingNear, ExtraNear, Missing, Extra;
        /// <summary>Game-only screenshot vs the reference: mean absolute difference (0-255) and share of pixels off by more than 32.</summary>
        public float ImageDiff, ImageDiffPct;
        public bool Match;
        public string Screenshot;
        public List<string> MissingNearSamples = new List<string>();
        public List<string> ExtraNearSamples = new List<string>();
    }

    public sealed class ModeStats
    {
        public int Frames;
        public float AvgMs, P50Ms, P95Ms, P99Ms, MaxMs;
        public float AvgFps, Low1Fps;
        /// <summary>Average GPU ms per timer slot (empty without -ultrart-gputimer).</summary>
        public Dictionary<string, float> GpuMs = new Dictionary<string, float>();
        /// <summary>Frames slower than 1/60 s and 1/30 s; the 5 slowest frame times.</summary>
        public int Over16, Over33;
        public List<float> WorstMs = new List<float>();
        /// <summary>Main thread per frame (Perf.cs PlayerLoop markers): scripts/physics/animation, rendering (incl. our Trace).</summary>
        public float ScriptsMs, RenderMs;
        /// <summary>Plugin CPU per section (Perf.Sec): average and worst frame, ms.</summary>
        public Dictionary<string, float> CpuMs = new Dictionary<string, float>();
        public Dictionary<string, float> CpuMaxMs = new Dictionary<string, float>();
        /// <summary>Gen-0 collections during the measured frames, and managed heap growth per frame (KB, collections excluded).</summary>
        public int GcCollections;
        public float AllocKbPerFrame;
    }
}
