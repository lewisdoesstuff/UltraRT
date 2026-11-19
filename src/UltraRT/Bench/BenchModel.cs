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
        /// <summary>Seconds to let the level settle after loading (and RT setup) before anything happens. Was 3; frame times are the same at 0.5 (bench/README.md "Level settle").</summary>
        public float Settle = 1f;
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
        /// <summary>Burst: capture every Nth frame (the frames between run at full speed): slow boiling over thousands of
        /// frames (the radiance cache, a slowly drifting history) without capturing them all. Block = BurstBlock captures.</summary>
        public int BurstStride = 1;
        /// <summary>Extra seconds to hold each mode before its screenshot (lets the radiance cache converge after a variant).</summary>
        public float ShotSettle;
        /// <summary>Light fit at the first screenshot time (Plugin.LightFit.cs, docs/lighting-overrides.md "Fit"): measure each
        /// significant light's share of the relight image against the game's, and re-solve the level's per-light scales.</summary>
        public bool LightFit;
        /// <summary>Light fit: most lights measured at this pose (the rest are left as they are).</summary>
        public int LightFitLights = 12;
        /// <summary>Burst modes ("off" first gives the mask); default Modes. "mode+key=value" applies runtime variants.</summary>
        public List<string> BurstModes;
        /// <summary>Burst: screen rectangles [x0, y0, x1, y1] (fractions from the top left) also reported on their own, unmasked
        /// (sigma / block sigma / mean luma). Default: the viewmodel's usual area (the gun and arm, which "off" masks out).</summary>
        public List<float[]> BurstRegions = new List<float[]> { new[] { 0.5f, 0.5f, 0.84f, 1f } };
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
        public bool MotionJitterRotate;
        /// <summary>Consecutive frames of the first pass saved as PNG at the end of the window (frame strips).</summary>
        public int MotionStrip = 4;
        /// <summary>Screen rectangles left out of the motion-noise statistics, [x0, y0, x1, y1] as fractions of the screen from
        /// the top left: by default the viewmodel and the HUD panel (drawn by the game over the RT image).</summary>
        public List<float[]> MotionExclude = new List<float[]> { new[] { 0.5f, 0.5f, 0.84f, 1f }, new[] { 0f, 0.55f, 0.32f, 1f } };
        /// <summary>Motion noise of the viewmodel (docs/viewmodel.md, "Motion"): also report the noise over the pixels the
        /// viewmodel pass covers (its G-buffer this frame), which MotionExclude leaves out of the main statistics. Each pass
        /// then starts from the same game state for the gun: the viewmodel's animators are put back to their state at the
        /// start of the first pass, the camera FOV is restored and Random is reseeded (the revolver's shot picks its
        /// animation at random).</summary>
        public bool MotionViewmodel;
        /// <summary>Weapon bob while the bench drives the camera (GameControl): the gun swings as WalkingBob moves it while
        /// walking at full speed (+-8 cm sideways, 2.5 cm down, ~0.68 s per cycle), times this. 0 = off (the player stands).</summary>
        public float GunBob;
        /// <summary>Weapon slot to hold (GunControl.SwitchWeapon: 1 revolver, 2 shotgun, 3 nailgun, 4 railcannon, 5 rocket
        /// launcher), switched to when the bench takes the player over; 0 = whatever the save holds.</summary>
        public int Weapon;
        /// <summary>With Weapon: the variant to hold, by the start of its object's name in that slot (e.g. "Shotgun Grenade",
        /// the blue pump shotgun; "Hammer Pump" is the impact hammer); empty = whichever variant the save last held.</summary>
        public string WeaponVariant;
        /// <summary>Cinematic camera path instead of the measurement (docs/cinematics.md, bench/README.md "Cinematics"): the
        /// level is loaded as for a pose (Path.Approach / Areas / Load), then the path plays and/or renders offline.</summary>
        public CinematicSpec Cinematic;
        /// <summary>Path.Type "tour": options of the checkpoint tour (null = the defaults). bench/README.md, "Tour".</summary>
        public TourSpec Tour;
    }

    /// <summary>
    /// Path.Type "tour": screenshots of every mode at each entry of the game's Teleport Menu cheat ("First Room", then
    /// every checkpoint it offers, numbered by label), without a recorded route. Each stop gets its own fresh level load and
    /// one press of the menu's own button (PoseLoader.Teleport), so the level is in the state the cheat gives a player.
    /// Per stop, the most open view directions are captured. No frame times.
    /// </summary>
    public sealed class TourSpec
    {
        /// <summary>Views per stop: the directions with the longest free line of sight (eight candidates 45 degrees apart,
        /// at least 60 degrees between the picked ones).</summary>
        public int Views = 3;
        /// <summary>Also the menu's "First Room" entry (the shared start room).</summary>
        public bool Start = true;
        /// <summary>Stops to run, by their index in the teleport menu as logged (0 = "First Room"); null = all.</summary>
        public List<int> Only;
        /// <summary>At most this many stops (0 = all), spread evenly over the level.</summary>
        public int Max;
        /// <summary>Seconds to let a stop's rooms settle before its first capture.</summary>
        public float Settle = 1f;
        /// <summary>Metres to walk forward from the stop (the way the checkpoint faces) before the captures, at walking
        /// speed, for rooms whose lights or doors wait for the player to step in (3-1's Stairways). 0 = stay.</summary>
        public float Step;
        /// <summary>Screenshot size relative to the screen (0.5 = half width and height: a 1440p frame as 1280x720).</summary>
        public float Scale = 0.5f;
        /// <summary>Camera pitch of every view (degrees, negative = down).</summary>
        public float Pitch = 0f;
    }

    /// <summary>One stop of a tour as it ran (results.json).</summary>
    public sealed class TourStop
    {
        public int Index;
        /// <summary>The entry's label in the game's teleport menu ("First Room", "Main Hall", ...).</summary>
        public string Name;
        public float[] Position;
        /// <summary>Yaw of each captured view (degrees), in file order.</summary>
        public List<float> Yaws = new List<float>();
        /// <summary>The screenshots' common prefix per view: &lt;scenario&gt;-s&lt;index&gt;-v&lt;view&gt;.</summary>
        public List<string> Views = new List<string>();
        public float LoadSeconds;
    }

    /// <summary>A scenario's cinematic: the path file and overrides of the [Cinematics] settings (null = the config's).</summary>
    public sealed class CinematicSpec
    {
        /// <summary>Saved path (BepInEx\ultrart\cinematics\...\*.json), relative to the suite. Without it, a "recording"
        /// Path is replayed: the camera follows the recorded eye, the player moves along it and its arenas trigger on time.</summary>
        public string File;
        /// <summary>Play the path once in real time first, in the scenario's first RT mode (frame times and a mid-path screenshot).</summary>
        public bool Play;
        public bool Render = true;
        /// <summary>pathtrace | relight | current | off (the game's image) | live (the scenario's first RT mode as played)</summary>
        public string Mode;
        /// <summary>Several takes of the path in one scenario, one per mode (e.g. ["off", "live"]), each into
        /// cine_&lt;scenario&gt;_&lt;mode&gt;. With TimeScale 0 the world stays frozen from the first take to the last, so the takes
        /// show the same instant (particles included) and line up exactly: before / after pairs.</summary>
        public List<string> Modes;
        /// <summary>Paired capture (accumulating modes and live): one take that writes two folders, cine_&lt;scenario&gt;_&lt;mode&gt;
        /// and cine_&lt;scenario&gt;_off. Per output frame the world is stepped once and held while the game's own image and
        /// the traced image are rendered, so both show the same world state in every frame with the world running
        /// (TimeScale 1): a before / after pair that needs no frozen world.</summary>
        public bool Paired;
        /// <summary>Paired capture windows (path seconds): the traced image is only rendered from OnFrom, the game's
        /// image only until OffUntil (default: both for the whole path). Frame numbers stay the path's.</summary>
        public float? OnFrom, OffUntil;
        public float? Fps, OutputScale, Shutter, TimeScale, Seconds, Start;
        public int? Samples, Aa, Paths, Bounces, Crf, Preroll;
        public bool? CacheTail, MotionBlur, Png16, Encode;
        /// <summary>Keep V1's model in the traced scene (reflections, shadow) at the scenario's pose while the camera flies.</summary>
        public bool? TracePlayer;
        /// <summary>The player moves during the take (V1's shadow and reflections with it): "camera" = it rides the camera
        /// path (a first-person walk: the camera is the player's eye), or a second path file, relative to the suite, that
        /// the player walks while the camera flies <see cref="File"/> (its keys are the player's position and view
        /// direction). V1 is kept in the traced scene, walks at the route's speed and looks where the route looks. Use
        /// TimeScale 1: the walk animation needs game time.</summary>
        public string Player;
        /// <summary>The camera sees V1's model (a third-person shot), in the traced image and in the game's ("off" takes).
        /// Default: on when <see cref="Player"/> is a path file, off otherwise (the camera would be inside the model).</summary>
        public bool? ShowPlayer;
        /// <summary>Shop terminals keep their screens off during the take: the shop UI that switches on when the player
        /// comes near covers the glass, which is what reflects.</summary>
        public bool? ShopScreensOff;
        /// <summary>A first-person shot with the gun in view (with <see cref="Player"/> "camera"): the HUD camera keeps
        /// drawing the viewmodel (lit by the traced scene as in play), the HUD itself stays hidden. Scenario.Weapon picks
        /// the gun.</summary>
        public bool? ShowWeapon;
        /// <summary>Path seconds at which <see cref="ShotKinds"/> are spawned (EffectSpec kinds; default "revolver": the
        /// held revolver fires as the player fires it).</summary>
        public List<float> Shots;
        public List<string> ShotKinds;
        /// <summary>Timing for the level's lamps. A flickering lamp (the game's Flicker component switches a light fully on
        /// and off at random intervals) is held in its on or its off phase for a stretch of the path, so a shot can land
        /// on the phase it needs; outside the stretches it flickers by itself. A steady light named here is switched
        /// on or off for the stretch (the Light component only: its lamp mesh keeps glowing) and put back after it.</summary>
        public List<FlickerHold> Flickers;
        /// <summary>Game events on cue: at each entry's path second, a parameterless method of the level's components
        /// of one type is called (e.g. 5-2's lightning: <c>{ "At": 1.6, "Type": "DistantThunder", "Method": "ForceThunder" }</c>),
        /// so a random event lands where the cut needs it.</summary>
        public List<CineCall> Calls;
        /// <summary>auto | locked</summary>
        public string Exposure;
        /// <summary>Takes with their own view mode and sample counts, rendered one after another in this scenario (after
        /// <see cref="Modes"/>' takes, if any), each into cine_&lt;scenario&gt;_&lt;Name&gt;. With TimeScale 0 the world is the same
        /// instant in all of them: live frames, accumulated references and debug views of one pose line up pixel for pixel
        /// (bench/suites/aliasing.json, docs/aliasing.md).</summary>
        public List<CineTake> Takes;
    }

    /// <summary>One take of <see cref="CinematicSpec.Takes"/>: unset fields keep the scenario's Cinematic settings.</summary>
    public sealed class CineTake
    {
        /// <summary>Folder suffix: cine_&lt;scenario&gt;_&lt;Name&gt;.</summary>
        public string Name;
        /// <summary>The render mode: pathtrace | relight | current | off | live (as <see cref="CinematicSpec.Mode"/>).</summary>
        public string Mode;
        /// <summary>The bench view mode (with runtime variants, "relight+refl=0") set before the take: what "live" plays
        /// and what "current" accumulates. Default: the scenario's first RT mode.</summary>
        public string View;
        public int? Aa, Samples, Bounces;
        public float? Start, Seconds;
    }

    /// <summary>One game event on cue (see <see cref="CinematicSpec.Calls"/>).</summary>
    public sealed class CineCall
    {
        /// <summary>Path seconds.</summary>
        public float At;
        /// <summary>The component's class name (no namespace), e.g. "DistantThunder".</summary>
        public string Type;
        /// <summary>A parameterless method of it (public or not), e.g. "ForceThunder".</summary>
        public string Method;
        /// <summary>Which of the active components, nearest the camera first (0 = the nearest); -1 (default): all of them.</summary>
        public int Index = -1;
    }

    /// <summary>One lamp held in a flicker phase (see <see cref="CinematicSpec.Flickers"/>).</summary>
    public sealed class FlickerHold
    {
        /// <summary>The end of the light's hierarchy path, e.g. "Metro Light/Spot Light" (case-insensitive).</summary>
        public string Light;
        /// <summary>Path seconds.</summary>
        public float From, To = 1e9f;
        /// <summary>true: held on; false (default): held off.</summary>
        public bool On;
    }

    /// <summary>
    /// Effects spawned in front of the camera during a scenario: "revolver" (the held revolver fires as the game fires it:
    /// beam from its barrel, muzzle light, recoil animation; a beam from the camera with the barrel as its start point when
    /// no revolver is held), "revolverbeam" (only the beam, never the gun's own shot), "explosion" / "superexplosion" (harmless), "projectile" (an enemy orb flying away),
    /// "fire" / "firesimple" (a pooled flame on the floor ahead, with / without its light).
    /// At each screenshot they are also spawned once per mode, ShotDelay seconds before the capture, so every mode shows
    /// the effect at the same age.
    /// </summary>
    public sealed class EffectSpec
    {
        public List<string> Kinds = new List<string> { "revolver" };
        /// <summary>Kinds spawned on the Every timer instead of <see cref="Kinds"/> (null = Kinds); Kinds still spawn at
        /// screenshots. E.g. hold the torch and light the brazier on the way, whiplash only at the screenshots.</summary>
        public List<string> PeriodicKinds;
        /// <summary>Seconds between spawns (all kinds at once); 0 = only at screenshots.</summary>
        public float Every = 0.5f;
        /// <summary>Distance in front of the camera (m), shortened to stay in front of walls.</summary>
        public float Distance = 5f;
        public float ShotDelay = 0.08f;
    }

    public sealed class PathSpec
    {
        /// <summary>spawn | static | sweep | recording | tour (Scenario.Tour: screenshots at every checkpoint)</summary>
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
        /// <summary>
        /// static: aim at an object instead of Position/Yaw (Plugin.Bench.LookAt.cs). A GameObject name glob, a path
        /// ("Room/Statue*"), or "mat:&lt;material glob&gt;" for renderers with that material. LookIndex picks the Nth match
        /// (nearest the pose first); LookDistance (0 = from its size) and LookYaw (null = the most open side, preferring
        /// the object's front) place the camera; Pitch tilts it. LookActivate switches on the target's inactive parents.
        /// </summary>
        public string LookAt;
        public int LookIndex;
        public float LookDistance;
        public float? LookYaw;
        public bool LookActivate;
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
        /// <summary>Run options that change what is measured or captured (no screenshots, no bursts, settle override); empty = defaults.</summary>
        public string Options;
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
        /// <summary>Scenario.Cinematic: the live playback's frame times and the offline render's result.</summary>
        public ModeStats CinematicPlay;
        public CineRenderResult Cinematic;
        /// <summary>Cinematic.Modes: every take (Cinematic is the last one).</summary>
        public List<CineRenderResult> CinematicTakes;
        /// <summary>Path.Type "tour": the stops that ran.</summary>
        public List<TourStop> Tour;
    }

    /// <summary>
    /// Motion noise of one mode over one path window (screen luma 0-255): sigma = RMS difference between passes / sqrt 2
    /// (the per-frame noise of a single pass), over the pixels the game keeps identical between its own passes.
    /// Classes (exclusive, in this priority): thin, edge, fresh (denoiser history &lt; 8 frames), screen edge (outer 10%),
    /// young (8 to 31 frames, not on the screen edge), interior; "none" = no traced surface (sky, game-only pixels).
    /// </summary>
    public sealed class MotionStats
    {
        public string Mode;
        public float Window;
        public int Frames, Passes, Width, Height;
        /// <summary>Captured frames left out because the game itself changed between the two passes (squared error over 4x the median).</summary>
        public int Dropped;
        /// <summary>Share of pixels whose difference exceeded 40 luma (the game's own birds, gore): left out.</summary>
        public float Clipped;
        public float Sigma, SigmaBlock, Offset, MaskedShare;
        /// <summary>Scenario.MotionViewmodel: the same sigma / block4 over the viewmodel's pixels, and their share of the screen.</summary>
        public float VmSigma, VmBlock, VmShare;
        /// <summary>Scenario.MotionViewmodel: share of the viewmodel filter's pixels whose history the bilinear taps found, and
        /// whose history was rejected (no tap, no 3x3 search hit): how well the motion vectors follow the gun.</summary>
        public float VmHistHit, VmHistMiss;
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
        /// <summary>Per BurstRegions rectangle (unmasked): sigma, block sigma, mean luma.</summary>
        public List<float> RegionSigma = new List<float>(), RegionBlock = new List<float>(), RegionLuma = new List<float>();
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
        /// <summary>Average render-thread ms per timer slot: that thread's own time between the slot's begin and end
        /// events (GpuTimer.TryGetCpuMs). "frame" is the whole frame's rendering, in every mode.</summary>
        public Dictionary<string, float> RenderThreadMs = new Dictionary<string, float>();
        /// <summary>Wall-clock spans (unix seconds, UTC) of this mode's counted frames, for cutting an ETW trace
        /// (bench.ps1 -Etw, tools/perf/etw_report.py).</summary>
        public List<double[]> WindowsUtc = new List<double[]>();
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
