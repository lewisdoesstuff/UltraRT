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
        /// <summary>Temporal noise burst at the first screenshot time (Plugin.Burst.cs): frames captured per mode (0 = off).</summary>
        public int Burst;
        /// <summary>Burst: frames per block for the block-mean sigma (noise that survives averaging over this many frames).</summary>
        public int BurstBlock = 8;
        /// <summary>Burst: frames to hold the pose in each mode before capturing (history convergence).</summary>
        public int BurstSettle = 120;
        /// <summary>Extra seconds to hold each mode before its screenshot (lets the radiance cache converge after a variant).</summary>
        public float ShotSettle;
        /// <summary>Burst modes ("off" first gives the mask); default Modes. "mode+key=value" applies runtime variants.</summary>
        public List<string> BurstModes;
        /// <summary>Optional: spawn combat effects in front of the camera (EffectSpawner.cs), for effect-light tests.</summary>
        public EffectSpec Effects;
        /// <summary>
        /// Motion noise (Plugin.MotionNoise.cs, bench/README.md "Motion noise"): at every screenshot time, replay the same
        /// MotionSettle + Motion frames of the path MotionPasses times per mode on a fixed 1/360 s clock, histories reset at
        /// the start of each pass, and compare the passes frame by frame. Frames captured per pass = Motion / MotionStride.
        /// 0 = off.
        /// </summary>
        public int Motion;
        public int MotionPasses = 3;
        public int MotionStride = 4;
        public int MotionSettle = 120;
        /// <summary>Modes ("off" first: its two passes give the mask of what the game itself animates); default Modes.</summary>
        public List<string> MotionModes;
        /// <summary>Start each pass at a different TAAU jitter phase (pass index), so jitter-dependent flicker counts too.</summary>
        public bool MotionJitterRotate = true;
        /// <summary>Consecutive frames of the first pass saved as PNG at the end of the window (frame strips).</summary>
        public int MotionStrip = 4;
    }

    /// <summary>
    /// Effects spawned in front of the camera during a scenario: "revolver" (a revolver beam from the camera, with its
    /// muzzle light and hit sparks), "explosion" / "superexplosion" (harmless), "projectile" (an enemy orb flying away),
    /// "fire" / "firesimple" (a pooled flame on the floor ahead, with / without its light).
    /// At each screenshot they are also spawned once per mode, ShotDelay seconds before the capture, so every mode shows
    /// the effect at the same age.
    /// </summary>
    public sealed class EffectSpec
    {
        public List<string> Kinds = new List<string> { "revolver" };
        /// <summary>Seconds between spawns (all kinds at once); 0 = only at screenshots.</summary>
        public float Every = 0.5f;
        /// <summary>Distance in front of the camera (m), shortened to stay in front of walls.</summary>
        public float Distance = 5f;
        public float ShotDelay = 0.08f;
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
        public List<BurstStats> Bursts = new List<BurstStats>();
        public List<MotionStats> MotionNoise = new List<MotionStats>();
    }

    /// <summary>
    /// Motion noise of one mode over one path window (screen luma 0-255): sigma = RMS difference between passes / sqrt 2
    /// (the per-frame noise of a single pass), over the pixels the game keeps identical between its own passes.
    /// Classes (exclusive, in this priority): thin, edge, fresh (denoiser history &lt; 8 frames), screen edge (outer 10%),
    /// interior; "none" = no traced surface (sky, game-only pixels).
    /// </summary>
    public sealed class MotionStats
    {
        public string Mode;
        public float Window;
        public int Frames, Passes, Width, Height;
        public float Sigma, SigmaBlock, Offset, MaskedShare;
        public List<string> Classes = new List<string>();
        public List<float> ClassShare = new List<float>(), ClassSigma = new List<float>(), ClassEnergy = new List<float>();
        public List<string> Images;
    }

    /// <summary>Temporal noise of a held pose in one mode (screen luma, 0-255). Masked = pixels still in "off".</summary>
    public sealed class BurstStats
    {
        public string Mode;
        public int Frames, Block, Width, Height;
        public float Sigma, SigmaP95, SigmaBlock, SigmaBlockP95, Drift, MeanLuma;
        /// <summary>Mean |Laplacian| of luma per frame in the left 15%, centre 40%, right 15% of the screen (noise in motion).</summary>
        public float HfLeft, HfCenter, HfRight;
        public float MaskedShare, SigmaMasked, SigmaP95Masked, SigmaBlockMasked, SigmaBlockP95Masked, DriftMasked, MeanLumaMasked;
        public List<string> Images;
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
