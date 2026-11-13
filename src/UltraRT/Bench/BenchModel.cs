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
        /// <summary>Seconds to let the level settle after loading (and RT setup) before anything happens.</summary>
        public float Settle = 3f;
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
        public Dictionary<string, ModeStats> Modes = new Dictionary<string, ModeStats>();
        public List<string> Screenshots = new List<string>();
    }

    public sealed class ModeStats
    {
        public int Frames;
        public float AvgMs, P50Ms, P95Ms, P99Ms, MaxMs;
        public float AvgFps, Low1Fps;
        /// <summary>Average GPU ms per timer slot (empty without -ultrart-gputimer).</summary>
        public Dictionary<string, float> GpuMs = new Dictionary<string, float>();
    }
}
