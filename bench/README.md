# UltraRT benchmarks

## How to run tests (agents)

Two game installs exist: **main** (the Steam install, also what the user plays) and **alt** (`D:\UltraRT-Bench\ULTRAKILL`, a copy with BepInEx and our plugin, for visual and diagnostic runs). `scripts\game-lock.ps1` queues everybody (FIFO) in front of them. The game is only ever started while you hold a slot, so **build first, queue second, release as soon as the runs are done**.

```powershell
# 1. Build outside the lock (no game needed). Stages the build in <repo>\stage.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -NoDeploy
# 2. Queue once (shows position and estimate; no polling loop needed, one ticket per owner). Perf is the default kind.
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\death\.t3\projects\ultrart\scripts\game-lock.ps1 acquire <you>
#    visual run:   ... acquire <you> -Kind visual            (alt install if it is free, else main; prints "slot: alt|main")
# 3. Copy the staged build into the slot (seconds).
.\scripts\deploy.ps1 -Owner <you> [-Slot alt]    (-Owner is required: it refuses a slot someone else holds)
# 4. Run. Several suites in one launch; -Perf = no screenshots, no bursts.
.\scripts\bench.ps1 -Suite a,b,c -Perf [-Slot alt] [-Compare <older run>]
#    Each results folder gets game.log: this run's part of BepInEx\LogOutput.log (the game rewrites that file at every launch).
# 5. Release right away (stops your resident game, restores main's build from a cached copy: about a second).
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\death\.t3\projects\ultrart\scripts\game-lock.ps1 release <you>
```

Which kind of run for which question:

| You want | Acquire | bench.ps1 | Notes |
|---|---|---|---|
| Frame times, GPU ms, regressions | `acquire <you>` (kind perf, the default) | `-Perf` | Exclusive: needs **both** slots and no game of the user's. Nothing else runs on the machine meanwhile, so numbers are comparable. |
| Screenshots, bursts, noise, look-dev, debugging | `acquire <you> -Kind visual` | `-Slot <the one printed>` | One slot is enough; usually the alt install, so a visual run does not block a perf run's slot hunt. Frame times from a run that shares the GPU with another game are not reliable. |
| Both in one go | perf hold, default `bench.ps1` | (no `-Perf`) | Takes both slots; fine for a final check. Screenshots and bursts cost about a third of game time, so use them only for runs that need them. |
| `merge-regress` | **don't** | | The lead runs `merge-regress` once per merge batch. Agents use their feature suite plus a `-quick` suite and leave regression to the lead. |

Rules: hold a slot only while a game runs (analyse screenshots and edit code *after* release; queue again for the next run). Never launch or deploy without a slot. Never kill an ULTRAKILL process you did not start: a launch without `-ultrart-*` arguments is the user playing, and nobody starts anything until it ends (`status` shows `user game: playing`). Hand launches need `-ultrart-agent` (any `-ultrart-*` switch counts). Always release, also after failures.

Shader-only or config-only iterations (the bundle hot-reloads): `bench.ps1 -Suite x -KeepOpen` leaves the game running at the main menu; rebuild with `build.ps1 -NoDeploy`, `deploy.ps1`, and run `bench.ps1 -Suite x -KeepOpen` again to skip the launch. A changed `UltraRT.dll` or different `-Scale`/`-Api`/`-Extra` restarts it by itself. `release` and `bench.ps1 -Stop` quit it; it also quits after 20 idle minutes.


```powershell
.\scripts\bench.ps1                       # short suite (~1 min)
.\scripts\bench.ps1 -Suite full           # everything
.\scripts\bench.ps1 -Compare 20261113-171622_short   # diff against an earlier run in bench\results
```

Each run launches the game on DX12 and plays every scenario in `bench/suites/<suite>.json`, then quits. Output goes to `bench/results/<timestamp>_<suite>/`: `results.json`, `summary.md` and screenshots. The run is tagged with the git commit (`-dirty` if the tree has uncommitted changes).

**Saves are safe.** For the whole run the plugin skips every save-file, prefs and Steam-stat writer (`BenchSafety`). Cheats (invincibility, plus blind enemies or no spawns) are enabled for the session only, with `saveState:false`.

## How a scenario is measured
- The level loads with a fixed `Seed` (or is reused from the previous scenario, see "Chaining") (`Random.InitState`, applied before the level's own `Start`s run), then waits `Settle` seconds.
- The player is taken over the way noclip does it, and the camera follows `Path`.
- `Modes` alternate every `Segment` seconds *within* the same pass (off / pt-dn / off / ...). This keeps the comparison fair even when the scene isn't deterministic, such as enemies or physics. The first `SwitchSkipFrames` frames after each switch aren't counted.
- Frame times are uncapped (vsync off, no frame limit). GPU per-pass times come from the native timer (`-NoGpuTimer` turns it off).
- Before measuring, the first RT mode runs until the RT scene has registered everything the pose load switched on, for at least 30 frames and at most 5 s. Pose loads run with RT off, and 8-3's checkpoint clones about 3,000 renderers.

### What is reported
- `summary.md`, per mode: average, fps, 1% low, p99, the worst frame, and frames over 16.7 ms. Also GPU ms, the main thread split into scripts (Update, LateUpdate, physics, animation) and rendering (culling, our `Trace`, command submission), and plugin CPU (average and worst frame). These come from PlayerLoop markers and Stopwatch sections in `src/UltraRT/Perf.cs`; Unity's Profiler isn't available in the player.
- A second table splits plugin CPU per section (`hier` new objects, `rescan`, `static` groups, `dynamic` proxies, `motion`, `emissive`, `lights`, `lightsamp`, `trace` total), average / worst frame. It also lists gen-0 GC collections and managed heap growth per frame.
- `frames_<scenario>.csv`: one row per frame, with the same columns plus RTAS instances and static records. Frames skipped after a mode switch are marked `mode*`. Use it to tie a spike to its section.

## Path types
| Type | Fields | Use |
|---|---|---|
| `spawn` | `Duration` | hold the level's spawn pose |
| `static` | `Position` [x,y,z], `Yaw`, `Pitch`, `Duration`, `Approach` | hold a given pose (see "Poses deep in a level") |
| `sweep` | `YawSpeed` (deg/s), `PitchAmplitude`, `Duration`, optional `Position` | spin in place; stresses reprojection |
| `recording` | `File` (relative to the suite), optional `Start`/`End` seconds | replay a recorded flythrough or run |
| `tour` | scenario field `Tour` (below) | screenshots at the level's start and at every checkpoint, no route needed, no frame times |

### Tour
`"Path": { "Type": "tour" }` covers a level that has no recorded route (`src/UltraRT/Plugin.Bench.Tour.cs`). The stops
are the entries of the **game's own Teleport Menu cheat**: "First Room" (the shared start room), then every checkpoint
the game offers there, numbered by label (ordinal order), because the menu's own order changes from load to load. The bench opens the menu (`CheatsController.ShowTeleportPanel`) and presses the
entry's button (`PoseLoader.OpenTeleportMenu` / `Teleport`), so the game itself moves the player, switches the
checkpoint's rooms on, unlocks its doors, runs its restart event and starts the level; nothing of that is
re-implemented. The panel's canvas is hidden in a bench run, so its list is built by calling the panel's own
`GenerateList` when its `Start` has not run. A stop is found again by its label after each reload (3-1 lists its
checkpoints in a different order on each load). Every stop gets its **own fresh
level load** first: teleporting from stop to stop in one load would
leave earlier rooms switched on, and the game's lights shine through walls. At a stop the camera looks
along the most open directions (eight candidates 45 degrees apart from the checkpoint's facing, the longest free lines
of sight, at least 60 degrees between them), and every mode is captured as for a normal screenshot (12 frames, DLSS RR
ready, `ShotSettle`, the auto exposure wait).

```json
{ "Name": "1-4 tour", "Scene": "Level 1-4", "Path": { "Type": "tour" },
  "Tour": { "Views": 3, "Max": 0, "Only": null, "Start": true, "Settle": 1, "Step": 0, "Scale": 0.5, "Pitch": 0 },
  "Modes": [ "off", "pathtrace", "pathtrace+ae.enabled=0" ], "ShotSettle": 0.8 }
```

`Views` per stop; `Max` stops spread evenly over the level (0 = all); `Only` picks stops by their number
(0 = "First Room"; the log lists `stop N '<label>'`, and `results.json` has the labels); `Start: false` leaves "First Room" out; a stop with no level under it (0-1's "First Room" is the top of the intro's fall) is replaced by a fresh load without the teleport, and the views are taken where the game lands the player (`no ground under ...` in the log; skipped with a warning if nothing is under the player after 40 s); `Step` walks that many metres forward from the stop before the captures, for rooms whose lights wait for the player to step in (3-1's Stairways: `bench/suites/review/review-3-1.json`); `Scale` 0.5 saves a
1440p frame as 1280x720. Files are `<scenario>-s<stop>-v<view>_0.0s_<mode>.png`, and `results.json` lists the stops
(`Tour`: index, the menu's label, position, yaws). The level is in the state the cheat leaves: rooms behind a
checkpoint are not loaded, as with `Load: checkpoint`.
A tour reloads the level per stop, about 20 s each with three modes: one to three minutes per level, so run two or
three levels per launch. `tools/lights/tour_suites.py` writes one suite per level into `bench/suites/tour/`
(`.\scripts\bench.ps1 -Suite bench\suites\tour\tour-1-4.json -Slot alt`), and `tools/lights/score.py` scores a run
against the game's frames (docs/lighting-overrides.md, "Scoreboard").

## Combat effects
`"Effects": { "Kinds": ["revolver"], "Every": 0.5, "Distance": 5, "ShotDelay": 0.08 }` spawns the game's own effects in front of the camera, every `Every` seconds (0 = only at screenshots). At each screenshot they are also spawned once per mode, `ShotDelay` seconds before the capture, so every mode shows the effect at the same age.

| Kind | What spawns |
|---|---|
| `revolver` | the held revolver fires as the player fires it (`Revolver.Shoot`): beam from the camera with the barrel as its start point, muzzle flash at the barrel, the beam's muzzle light, hit sparks, recoil animation and FOV kick. Without the revolver out: the beam alone, starting at the revolver's barrel |
| `revolverbeam` | only the beam (as before 2026-11-15, but starting at the barrel instead of a fixed point right of the camera): no recoil, no FOV kick |
| `shotgun` | the held shotgun fires as the player fires it (`Shotgun.Shoot`): pellets, the muzzle flashes at its shoot points with their lights, recoil. Needs a `Shotgun` out: `"Weapon": 2` with `"WeaponVariant": "Shotgun Grenade"` (the pump shotgun; the impact hammer, `Hammer Pump`, is a different component) |
| `explosion`, `superexplosion` | `DefaultReferenceManager`'s explosion, made harmless |
| `enemy:<EnemyType>` (`enemy:Drone`, `enemy:Swordsmachine`, ...) | a copy of one of the level's own enemies of that type, standing `Distance` in front of the camera and facing it; once per level load, further kinds beside it. Idle with `Enemies` "blind". The level must contain the type (`-Extra "-ultrart-enemy-survey"` writes `enemies_<scene>.txt` with the types per level). A `tour` scenario runs its `Effects` at each view |
| `projectile` | the enemy orb, flying away from the camera |
| `fire`, `firesimple` | a pooled flame on the floor ahead, with its light switched on as `Flammable` does / the variant without a light |
| `bloodstain` | writes 9 blood stains straight into the game's stain buffer, on the floor and walls ahead (as a splatter's drops do when they land): fresh stains for the wet-blood look (docs/vfx.md) |
| `holditem` | puts the nearest active item that is not a torch (a skull) in the player's hand, the same way; no-op while something is held |
| `holdtorch` | puts the level's nearest `Torch` in the player's hand (`Punch.ForceHold`, as picking it up does); no-op while one is held. Recordings keep the camera path only, not items |
| `whiplash` | presses the whiplash (R) for `EffectSpawner.WhiplashHold` (0.25 s) of game time through the game's `InputActionState`: with a held item (the torch) it flies out on the hook and back |
| `lightbrazier` | burns the nearest `Flammable` within 6 m (not the held item), once, as the held torch does when it hits it (`Flammable.Burn(4)`): 4-3's brazier opens the door ahead |

`"Weapon": N` (scenario) holds weapon slot N (1 = revolver) from the take-over on (`GunControl.SwitchWeapon`, then 1 s for the draw animation); `"WeaponVariant"` picks the variant in that slot by the start of its object's name (`"Shotgun Grenade"`, `"Hammer Pump"`, `"Revolver Twirl"`, ...; the log lists the `Guns/` objects), otherwise the save's last variant is held (the alt install's slot 2 is the impact hammer). Both installs' saves hold the rocket launcher, so `revolver` effects need `"Weapon": 1` to fire the revolver itself. `"GunBob": 1` swings the gun as walking does (see "Motion noise").

`"PeriodicKinds"` (optional) are spawned on the `Every` timer instead of `Kinds`; `Kinds` still spawn at the screenshots. `bench/suites/lights-pm-throw.json` holds the torch along the user's 4-3 route and throws it on the whiplash only `ShotDelay` before each capture (one mode per scenario, so every capture gets a fresh throw). `melee` and `lightbrazier` were meant to light the brazier that opens the door ahead; neither lit it from the recorded pose, so the hook still lands on the closed door ~10 m away. Effects stay in the level: a pooled fire keeps burning in later scenarios, so put long-lived effects last. `bench/suites/effects.json` uses them (docs/emissive-lights.md, "Effect lights").

## Cinematics
A scenario with a `"Cinematic"` block plays and/or renders a saved camera path instead of measuring (docs/cinematics.md,
"Bench: cinematic scenarios"). The level is loaded as for a pose (`Path.Approach` / `Areas` / `Load`); then `"Play": true`
plays the path in real time in the scenario's first RT mode (frame times, a screenshot halfway) and `"Render": true`
(the default) renders it offline into `<results>\cine_<scenario>\` (PNG frames, `render.json`, `encode.cmd`, and an MP4
when ffmpeg is found). Every `[Cinematics]` setting can be overridden per scenario: `Mode` (`pathtrace`, `relight`,
`current`, `off` = the game's image, `live` = the scenario's first RT mode as played), `Fps`, `Samples`, `Aa`,
`Paths`, `Bounces`, `CacheTail`, `OutputScale`, `MotionBlur`, `Shutter`, `Exposure`, `TimeScale`, `Png16`, `Encode`,
`Crf`, plus `Start` / `Seconds` (part of the path) and `Preroll`. The summary gets a "Cinematics" table. Offline renders
take seconds per frame: give long ones `-TimeoutSec`. Suites: `cine-test.json` (smoke test, ~1 min), `cine-test2.json`,
`cine-demo.json` (the sample renders, ~25 min). `Takes` (a list of `{ "Name", "Mode", "View", "Aa", "Samples", "Bounces", "Start", "Seconds" }`) renders several takes of one path with their own bench view mode (variants allowed), render mode and sample counts into `cine_<scenario>_<Name>`; with `TimeScale` 0 they all show the same instant (`bench/suites/aliasing.json`, docs/aliasing.md; set `"Render": false` so the block's own take is skipped). More `Cinematic` fields (`Modes`, `Paired` capture, `OnFrom` / `OffUntil`, a moving `Player`, `TracePlayer`, `ShowWeapon`, `Shots` / `ShotKinds`, `Flickers`, `Calls`) are in docs/cinematics.md ("Bench: cinematic scenarios", "Before / after pairs and trailers") and docs/trailer.md.

```json
{ "Name": "1-3 glide pathtrace", "Scene": "Level 1-3",
  "Path": { "Type": "static", "Approach": "../recordings/1-3-start-water.json", "Duration": 1 },
  "Modes": [ "pathtrace" ],
  "Cinematic": { "File": "../cinematics/1-3-water-glide.json", "Play": true, "Start": 1, "Seconds": 3, "Samples": 16, "Aa": 4 } }
```

## Recording a path
1. Launch normally (`-force-d3d12`), load a level and play or fly around. F10 and F11 are developer keys since 2026-11-20: turn on **Developer hotkeys** first (settings menu, Advanced settings > View, saved as `[View] DevHotkeys`; or launch with `-ultrart-dev-hotkeys`). Without it the first press shows a toast and does nothing ([docs/settings.md](../docs/settings.md), "Hotkeys for players and developers").
2. Press **F10** to start and **F10** again to stop. Recordings are saved to `BepInEx\ultrart\recordings\<level>_<time>.json` in the game folder.
3. Copy the file into `bench/recordings/` and reference it from a suite:
   ```json
   { "Name": "0-5 lava run", "Scene": "Level 0-5",
     "Path": { "Type": "recording", "File": "../recordings/Level_0-5_20261113-180000.json" },
     "Enemies": "active", "Modes": ["off", "pt-dn"], "Screenshots": [5, 15] }
   ```

`"KillEnemies": true` kills enemies as they spawn while the recording plays, as the approach replay does. Arenas then finish, and the doors and rooms they open load on time. With that, a pose's approach route (F11) can be replayed as a real-time run: `bench/suites/perf-8-3.json` walks the user's 8-3 route that way, with one mode per pass, so the cost of rooms loading, clones and spawns is measured as it happens in play. (`perf-8-3-route-hybrid.json`, its hybrid pass only, went with the hybrid mode on 2026-11-19); `perf-regress.json` covers 1-1 and 0-2.

A recording stores the player's position and look angles at 60 Hz, plus every arena (`ActivateArena`) that triggered and when. On replay the camera follows the path exactly and the same arenas trigger at the same times, so fights happen in the same places.

The replayed path is a spline through the samples. Where two samples are more than 6 m and under 0.1 s apart (a portal crossing, a teleport) the path cuts instead: the camera jumps, as it did in play (2026-11-18; it used to glide across the gap). A portal that moves (8-3's Shifting Hallway) is elsewhere in every run, so after such a cut the replayed camera is a few metres from where the portal leads.

Replays are *statistically* repeatable, not identical. Enemy AI, physics and gore still vary between runs, even with a fixed seed. Raw keyboard/mouse replay would drift after a few seconds for the same reason, so we record the resulting path instead.

Recording itself doesn't enable cheats or block saves. It's just normal play.

## Poses deep in a level
ULTRAKILL keeps most of a level switched off and turns rooms on as doors open and arenas finish. Teleporting straight to a pose therefore lands in an unloaded area (black silhouettes, missing geometry). `Path.Load` says how a `static` or `sweep` scenario gets the level ready:

| Load | What it does | Needs |
|---|---|---|
| `approach-fast` | Fast-forwards the whole route: the game runs at `FastTimeScale` (default 6) and the player moves once per physics step, so triggers fire as in real time. Loads the same renderers as `approach` on every pose checked. | `Approach` |
| `checkpoint` | Jumps to the last checkpoint on the route and restores its rooms the way the game's own teleport cheat does (the checkpoint's rooms, its doors, its restart event). Then fast-forwards the rest of the route. Quickest for poses late in a level, but the level is in respawn state: the room you came from, behind the checkpoint, is not loaded (4-1 indoor B loses 21 renderers of the previous hallway). If the route never reached a checkpoint, the whole route is fast-forwarded. | `Approach` |
| `approach` | Walks the whole route in real time at `ApproachSpeed`. Slow; the reference the others are checked against. | `Approach` |
| `areas` | Re-applies the object on/off diff saved with the pose. Not enough in levels that clone their rooms (8-3). | `Areas` |
| `none` | Teleports only. | |

The default, `auto`, is `approach-fast` when the scenario has an `Approach`. Set `"Load": "checkpoint"` on a scenario after checking it with `-ValidateLoad`. `-Load <name>` on `bench.ps1` forces one method for a whole run. Enemies are killed as they spawn on the way (`ApproachKillEnemies`), because finished arenas are what open doors.

The summary lists how each pose was loaded and how long it took.

### Chaining: one level load for several poses
Poses saved in one play session (F11 pressed several times) have routes that are prefixes of one another. The benchmark loads such a level once, fast-forwards the longest route once, and stops at the end of each shorter route to measure that pose (same Modes, Segment, Duration and Screenshots as on its own). Then it carries on to the next pose. A route counts as a prefix of another when its samples lie within 0.5 m of the other route at the same times, and the arenas and checkpoints both recorded so far agree.

To make this work, scenarios of the same level (same `Scene`, `Seed`, `Enemies` and `RandomPicks`) run together, in this order: spawn poses, then each chain of routes from shortest to longest, then everything else. Levels keep the order in which they first appear in the suite. Results are still reported per scenario and in suite order. The summary lists each scenario's run order and how it was reached: `fresh load` or `chained after 'X'`, with the level-load and pose-load times. `results.json` has the same fields (`Reached`, `ChainedAfter`, `LevelLoadSeconds`, `LoadSeconds`, `WallSeconds`, `Order`).

Only `approach-fast` poses chain, and only when `ApproachSpeed`, `FastTimeScale` and `ApproachKillEnemies` match. A level is also reused for spawn poses and poses without a route, as long as nothing has moved the level on. A scenario always gets its own fresh load when it is a recording, uses `checkpoint`, `approach` or `areas`, or sets `"FreshLoad": true`. Nothing chains after a recording, or after a pose held at an explicit `Position` away from its route's end (it may have set off triggers there). A FreshLoad scenario may still be the start of a chain. `-NoChain` loads every scenario's level in suite order, the old behaviour, so results can be compared:
```powershell
.\scripts\bench.ps1 -Suite poses -NoChain
.\scripts\bench.ps1 -Suite poses -ValidateLoad -ValidateMethods 'approach-fast,chain'   # chained vs fresh, per pose
```

### Aiming at an object (`LookAt`)
For objects no saved pose shows (`src/UltraRT/Plugin.Bench.LookAt.cs`): a `static` path with `"LookAt"` places the
camera at the object after the pose load. `"LookAt": "LionStatue"` (GameObject name glob), `"Room/Statue*"` (path glob)
or `"mat:Gibs"` (renderers with that material); `LookIndex` picks the Nth match (active ones first, nearest the pose
first); `LookDistance` (0 = from the size; give it explicitly for skinned meshes, whose bounds can be huge), `LookYaw`
(default: the most open of 16 sides, preferring the object's front) and `Pitch` place the camera; `LookActivate: true`
switches on the target's inactive parents (a whole room, without its triggers: its lights may be off). The log lists the
matches, also switched-off ones (`Benchmark: LookAt ...`). Many props sit in rooms the level switches off behind the
player: approach with a route that ends next to the object (a truncated copy works, e.g. `recordings/unlit-6-1-ghost.json`).

### Saving a pose
Press **F11** in normal play (with Developer hotkeys on, as for F10 above). It appends the pose to `BepInEx\ultrart\poses.jsonl` and saves two files next to the recordings: the route walked since the level loaded (`approach_*.json`) and the area diff (`areas_*.json`). The route also records every checkpoint you activated, so the benchmark knows exactly where to jump. Older routes have no checkpoint list; for those the last checkpoint the route passed through is used.

Copy the approach file into `bench/recordings/` and use it:
```json
{ "Name": "4-1 indoor A", "Scene": "Level 4-1",
  "Path": { "Type": "static", "Approach": "../recordings/4-1-approach-a.json", "Duration": 8 },
  "Modes": ["off", "relight", "pathtrace"], "Screenshots": [4] }
```
Without `Position`, the pose is the route's last sample.

### Random rooms
Some levels pick rooms at random: 8-3 shows one of four "false start" rooms. In benchmarks every such picker is seeded from the scenario `Seed` and its own name, so the pick repeats from run to run. To force a pick, name the picker and the entry:
```json
"RandomPicks": { "False Starts": "1-1" }
```
The plugin log lists each picker and its entries (`random pick ... entries: ...`).

### Checking a load method
```powershell
.\scripts\bench.ps1 -Suite poses -ValidateLoad -TimeoutSec 3600
.\scripts\bench.ps1 -Suite poses-8-3 -ValidateLoad -ValidateMethods 'approach,checkpoint,approach-fast@12'
```
Instead of measuring, this reaches each distinct pose with every method (a fresh level load each time) and compares against the first method. The `chain` method (included by default) runs last: it reaches every pose the way a chained benchmark does and compares it with the same reference. Chaining is otherwise off while validating. `validation.json` and `summary.md` report, per pose and method: load time, how many of the level's mesh renderers are on (all, and within 120 m of the camera), which near renderers are missing or extra compared with the reference, and how much a game-only screenshot differs. A load counts as matching when at most 3 near renderers are missing and under 25% of the screenshot differs. Screenshots of every method are saved for a visual check.

The screenshot limit is loose on purpose. Moving geometry is at a different point of its cycle when a load takes a different amount of time (8-3's shifting hallway), and gore differs between any two runs; together they change up to 10%. An unloaded area changes 60% or more.

`bench/suites/poses.json` holds every saved pose; `poses-quick.json` is one per level.

## Temporal noise bursts
A scenario with `"Burst": N` measures how much a held image changes from frame to frame, at its first screenshot time (`src/UltraRT/Plugin.Burst.cs`). For each entry of `BurstModes` (default `Modes`) the pose is held for `BurstSettle` frames (120), then N consecutive frames of the final screen are captured and reduced per pixel on luma (0-255):

| Column | Meaning |
|---|---|
| sigma | per-pixel standard deviation over the N frames |
| block sigma | standard deviation of `BurstBlock`-frame means (8): noise that survives averaging over 8 frames, i.e. what still shows at 360 Hz. Close to sigma = the noise is correlated over many frames (slow boiling). |
| drift | mean difference between the first and the second half of the burst |
| masked | over the pixels that `off` (put it first) keeps still: excludes the game's own animation (sky, viewmodel, particles) |
| HF left/centre/right | mean absolute Laplacian of luma in the left 15%, centre 40% and right 15% of the screen; with a moving path, noise where history is short |

`BurstRegions` (default: the viewmodel's usual area, `[0.5, 0.5, 0.84, 1]`) lists screen rectangles `[x0, y0, x1, y1]` (fractions from the top left) whose own sigma / block sigma / mean luma are reported unmasked (`regions` column, and `| region N: ...` in the log line): the gun is masked out of the main statistics because the game animates it. `BurstStride: N` captures every Nth frame (the frames between run at full speed), so a burst of 96 captures with stride 8 spans 768 frames: slow boiling (radiance cache, a drifting history) that a plain burst is too short to see.

Moving paths (`sweep`, `recording`) advance a fixed 1/360 s per frame during a burst, and the denoiser blends as at 360 Hz (`Denoiser.FrameTimeOverride`), so every run sees the same per-frame motion however slow the capture is. Each mode saves `<scenario>_burst_<mode>_sigma.png` (block sigma x16 as a heat map) and `_frame.png` (the last frame).

A burst mode may add runtime variants after the view mode, separated by `+`: `relight+lights=ref+cache=off`. Keys: `lights=ref|ris` (`restir`, removed on 2026-11-19, runs ris with a warning), `cache=off|bounce|primary`, `refl=0|1`, `emissive=0|1`, `relight=1` (the relight light model in any mode, e.g. `pt-direct+relight`), `preset=full|static|previous|legacy` (Ctrl+F3's denoiser presets), `taau=0|1` (the temporal upsampler, F2), and `<object>.<field>=value` for any public field of `ls` (LightSamplingSettings), `cache` (RadianceCacheSettings), `refl` (ReflectionSettings), `em` (EmissiveLightSettings), `ae` (AutoExposureSettings), `dn` (DenoiserSettings), `up` (UpsamplerSettings) or `plugin` (any Plugin field, e.g. `plugin._guardX=0`), `ptx` (PathTraceSettings, e.g. `ptx.bounces=3`; docs/path-tracing.md); also `vol`, `mat`, `lava`, `fx` (EffectLightSettings, e.g. `fx.flicker=0`), `pl` (ParticleLightSettings, e.g. `pl.additiveMax=0`) and `shaft` (LightShaftSettings, e.g. `shaft.keepFakes=1`) and `trans` (TransparentSettings, e.g. `trans.frosted=0`), `vfx` (VfxSettings: the screen effects, e.g. `vfx.enabled=0`, `vfx.bloom=0`, `vfx.debugView=1`) and `wet` (WetSettings, e.g. `wet.rain=0`, `wet.blood=0`) (`glint` and `trans.legacyGamma` were removed with the glints on 2026-11-19, docs/reflections.md "Glints"; the `glints*` suites went with them); `pl.soft=0` turns soft particles off (docs/vfx.md). Light classes have their own keys: `lc.class.NAME=0|F`, `lc.solo=NAME`, `lc.light.TEXT=0|F`, `lc.emit.TEXT=F`, `lc.glow.TEXT=G[,T]`, `lc.set.CLASS.FIELD=VALUE`, `lc.layer=NAME` (docs/lighting-overrides.md, "Review" and "Emissive fixtures"). Histories (including auto exposure, which snaps to the variant and settles) are reset and everything is restored after each mode, and after a scenario that fails mid-variant. Keep variant strings short: screenshot names include them, and paths over 260 characters fail the scenario. For relight burst modes the log also gets a per-frame auto exposure trace (`Burst AE ...`: EV, target, band mean, meter; docs/relight.md, "Flicker"); auto exposure adapts on the burst's 1/360 s clock like the denoiser. Every RT screenshot also logs radiance statistics (mean lighting by surface orientation; docs/relight.md, "Radiance statistics"), and `"ShotSettle": seconds` holds each mode longer before its screenshot. `bench/suites/noise-diag.json` and `noise-r2.json` are the suites behind docs/denoiser.md's "Static noise"; `relight-polish.json` is behind docs/relight.md's "Light reference distance" and "Flicker" (game / gamma relight via Ctrl+F5's switches / the 1666d51 settings / current, plus bursts); `upsample.json` is behind "Temporal upsampling" (it also times trace scale 0.67 via `plugin._scale=0.67`).

## bench.ps1 options
| Switch | What it does |
|---|---|
| `-Suite a,b,c` | Several suites in **one** game launch (one results folder per suite, in order). Saves the boot, intro skip and quit of every extra launch (about 10 s each). Each suite still loads its own levels. `-Compare` takes one folder per suite, or one for all. |
| `-Perf` | `-NoScreenshots -NoBursts`: frame times only. Screenshots (12 frames + auto-exposure wait per mode, 2 PNGs per RT mode) and noise bursts (120 settle frames + capture per mode) were 35% of all game time. The suites are not edited: `results.json`/`summary.md` say `Options: no screenshots, no bursts`. |
| `-NoScreenshots` / `-NoBursts` | One of the two. A burst runs at the first screenshot time, so `-NoScreenshots` alone still takes the burst, only without the screenshots. |
| `-Settle <s>` | Seconds to settle after every level load, instead of each scenario's `Settle` (3 s in the suite files). See "Level settle" below. |
| `-Slot main\|alt` | Which install to run in (`-GameDir` still works). A run in alt writes its Unity log to `<alt>\Player.log` (`-logFile`), because `LocalLow\Hakita\ULTRAKILL\Player.log` belongs to the main install. |
| `-KeepOpen` / `-Stop` | Resident game (see "How to run tests"). |
| `-Extra "-ultrart-light-census"` | Writes `lights_<scene>.json` per level into the run's folder (every light with its class, rule and policy), for `tools/lights/rulediff.py`. docs/lighting-overrides.md, "Review". |
| `-Floor` | `-Perf` at trace scale 0.2: the CPU floor (every pass runs, with little GPU work). |
| `-Etw` | Records a CPU trace of the launch (`trace.etl` in the first suite's folder; `scripts\etw-setup.ps1` once, it asks for elevation). Analyse with `tools/perf/etw_report.py <run>`. Tracing slows the game: take frame times from a run without it. docs/performance.md, "ETW". |
| `-Load`, `-NoChain`, `-ValidateLoad`, `-ValidateMethods`, `-Scale`, `-Api`, `-NoGpuTimer`, `-NativeGfxJobs`, `-Extra`, `-TimeoutSec`, `-Compare` | As before. |

`bench.ps1` refuses to start a game while the user is playing, warns when no slot lock is held and, for `-Perf`, when another game instance is running (frame times are then not exclusive).

## Game slots and the lock (`scripts\game-lock.ps1`)
- `acquire <owner> [-Kind perf|visual] [-Slot main|alt|any] [-TimeoutMin N]`: takes a ticket in `BepInEx\ultrart\lock-queue\` (one per owner; a second acquire of the same owner keeps the place and returns at once if it already holds the slot). It polls every 2 s, prints `queued: position 2 of 3 (...); est. wait ~N min` when the position changes and at least once a minute, and prints `acquired by <owner>` when it is yours (then `slot: main|alt ...` as an extra line). A waiter that is gone (its process ended and 30 s passed) loses its ticket; a timed-out acquire keeps the place for 30 s, so retry loops do not lose it. The estimate is the median of the last hold times in `lock-history.csv` times the queue position.
- Order: strict FIFO among tickets that need the same slot. A perf ticket needs both slots, a visual `-Slot any` ticket one of them, so tickets for different slots do not block each other.
- `release <owner>`: quits your resident game, copies the cached main build into your slot(s), frees them, logs the hold. The cache is `BepInEx\ultrart\main-cache\<head>\`, built from the main checkout when its HEAD changed; `acquire` starts that build in the background while you queue (`game-lock.ps1 cache` does it by hand), so `release` is a file copy of changed files only.
- `status [-Watch]`: line 1 `free` or `locked by <owner> since <time>` (main slot, as before), then a table of both slots (holder, kind, how long held, the game running there: agent or USER, pid, uptime), a note if the user is playing, the queue with waits and estimates, and the last three releases. `-Watch` redraws it every 2 s. `slot <owner>` prints the slot you hold; `cancel <owner>` leaves the queue.
- A hold nobody touched (lock file, `bench.ps1` runs and `deploy.ps1` touch it) for 60 minutes with no game running in the slot is taken over.
- The user: an ULTRAKILL.exe from the Steam folder started without any `-ultrart-*` argument. While one runs, no acquire is granted (waiting agents keep their place) and `bench.ps1` refuses to launch.

## Level settle
`Scenario.Settle` (seconds to wait after a level load, before the player is taken over) was 3 and is now 1; `bench.ps1 -Settle <s>` overrides it for a whole run (`-Settle 3` gives the old behaviour; a suite that sets `Settle` itself keeps its value unless `-Settle` is given). Measured on 0-1, 1-4, 0-3, 6-2, 0-5 (`infra-settle.json`, `-Perf`, two runs each at 3 s and 0.5 s, one at 1 s): avg frame times match within the run-to-run noise (relight 5.27/5.48 ms at 3 s against 5.08/5.39 at 0.5 s in 6-2; every other row the same way), and the level load drops from 5.3-6.6 s to 2.6-3.9 s (88 s -> 76 s for the suite). The RT warm-up before measuring (at least 30 frames) and the 0.5 s take-over already cover what the 3 s was for. Visual runs that need a fully settled scene (auto exposure, radiance cache) have `ShotSettle` and `BurstSettle`.

## The alt install (`D:\UltraRT-Bench\ULTRAKILL`)
A plain copy of the Steam install (3.7 GB: game, BepInEx, our plugin, a snapshot of `Saves` and `Preferences`, without `BepInEx\ultrart` and the logs), made with `robocopy ... /E`. It is a normal Steam game folder moved out of Steam's tree, with a `steam_appid.txt` (`1229490`) next to the exe so the Steamworks init does not try to restart the game through Steam; **Steam has to be running**, as for the main install. Use it only through `game-lock.ps1` / `bench.ps1 -Slot alt` / `deploy.ps1 -Slot alt`.
- *Saves and prefs.* The alt folder has its own `Saves` and `Preferences`, and `BenchSafety` blocks every save, prefs and Steam-stat writer there as well, so nothing a run does can reach the user's files.
- *Shared state, checked.* `Player.log` (`%USERPROFILE%\AppData\LocalLow\Hakita\ULTRAKILL`) is the main install's: `bench.ps1 -Slot alt` passes `-logFile <alt>\Player.log`, and the alt runs left the LocalLow log untouched. The registry key `HKCU\Software\Hakita\ULTRAKILL` is shared; across alt-only runs and runs of both installs at once the only values that changed were Unity's analytics counters (`unity.player_session_count`, `unity.player_sessionid`), which every launch changes anyway. The window mode, resolution and position values (`Screenmanager ...`) stayed as they were. Both installs run at the same resolution, so screenshots from either are comparable.
- *Two games at once.* Both windows are borderless fullscreen and cover each other, and the one that is not focused is not rendered by Unity unless `Application.runInBackground` is on. The plugin now sets it when a bench starts (it used to set it only when the suite began, so an instance that lost focus while booting froze at the loading screen until someone clicked it). With that, both instances boot, load and run without any focus; screenshots from the covered window are correct renders.
- *Frame times when both run.* `infra-par.json` (1-1 doors run, four rounds, `-Perf`): alone about 2.4 / 4.5 / 4.7 ms (off / hybrid / pt-dn) in either install, identical; with both running at the same time about 4.4 / 8-9 / 8.7-9 ms, roughly 1.9x. Two games on one GPU split it, so that is why perf runs need both slots and nothing else. For visual runs the cost does not matter: the images are the same (compared `short`'s doors run in both installs). Two `short` runs in parallel took 36 s each, as one does alone.
- *Material maps.* The per-texel detail cache (`BepInEx\ultrart\materials\ai`, docs/materials.md) is not in the
  robocopy (it sits under the excluded `BepInEx\ultrart`); it was copied in on 2026-11-15. Before that, detail was off in
  every alt run. Check the log for `Material maps: N textures in ...` (not `no cache at ...`).
- *Keeping it current.* `release` copies the cached main build into both installs. After a change that is not in the plugin folder (BepInEx, game update), redo the robocopy: `robocopy D:\SteamLibrary\steamapps\common\ULTRAKILL D:\UltraRT-Bench\ULTRAKILL /E /XD ...\BepInEx\ultrart /XF LogOutput.log LogOutput.log.1`, (the copy's `steam_appid.txt` is not in the source, so robocopy keeps it).

## Measured (2026-11-14, this machine)
| | Before | After |
|---|---|---|
| `release` (restore main's build) | 15 s (Unity shader build + `dotnet build`, under the lock) | 1.3 s (copy of changed files from the cache) |
| Build + deploy under the lock | build.ps1 under the lock | `build.ps1 -NoDeploy` before queueing (no lock); `deploy.ps1` 0.1-0.3 s |
| `infra-burst` (1 scenario, one burst) | 55 s | `-Perf`: 23 s; `-NoBursts` only: 24 s |
| `short` (3 scenarios, 1 screenshot time each, no burst) | 38 s | `-Perf`: 36 s (screenshots alone cost about 0.5 s per screenshot time) |
| Two suites | 2 launches: 48 s | `-Suite a,a` in one launch: 35 s (one boot, one quit) |
| Next run while the game is resident | 23-24 s per call | 12-13 s (`-KeepOpen`, no boot, no quit) |
| Level settle | 3 s per level load | 1 s default, 0.5 s tested equal (about 2.5 s per load) |
| Lock handoff | agents polling every 3-30 s, no order | FIFO tickets, polled every 2 s |

Pitfall seen while testing: `short`'s "1-1 pose" is a plain teleport (`Load: none`), so most of the level around it is not loaded and the screenshot shows an empty sky and floor. Use a recording or an `Approach` for anything that has to look like the game (see "Poses deep in a level").

## Motion noise
A burst measures a held pose. In motion a pixel's history keeps moving and being cut, so `"Motion": N` measures noise along the path instead (`src/UltraRT/Plugin.MotionNoise.cs`), at every screenshot time `t`:
- **Replays.** For each entry of `MotionModes` (default `Modes`) the window `t - MotionSettle/360` .. `t + N/360` of the path (`MotionSettle` 120 frames) is played `MotionPasses` (3) times. Every frame advances exactly 1/360 s: the camera, the denoiser's blend factors (as in bursts) and the game itself (`Time.captureDeltaTime`), so animations and spawned effects replay identically. Every history (denoiser, ReSTIR, reflections, upsampler, auto exposure) is reset at the start of a pass, and an unmeasured warm-up pass comes first so the radiance cache has seen the stretch. Effects (`Effects.Every`) spawn on the same frames in every pass.
- **Metric.** Every `MotionStride` (4)th frame of the window is captured. Two passes differ only by the path tracer's random numbers, so the per-pixel luma difference between consecutive passes at the same frame is the noise the player sees in motion: disocclusions, the screen edge while turning, silhouettes, changing lights. No reprojection is involved. `sigma` = RMS difference / sqrt 2 (one pass's noise), over traced surfaces; `block4` is the same on 4x4 pixel means (blotches rather than grain). Each pass's global offset (auto exposure settling slightly differently) is subtracted. Frames whose error is over 4x the window's median are dropped (`dropped`): the game itself changed between the passes (a timed event, a pulsing light).
- **Masks.** `off` (put it first) is played twice; the pixels it changes between its own passes (the game's animation, birds) are left out. `MotionExclude` rectangles (default: the viewmodel and the HUD panel, which the game draws over the RT image) are left out too.
- **Causes.** On the first pass the denoiser classifies every trace pixel (`Denoise.compute`, `Classify`). The classes, exclusive in this order: `thin` (both neighbours on one axis belong to another surface: wires, railings), `edge` (a depth or instance discontinuity in the 3x3: silhouettes), `fresh` (denoiser history under 8 frames: disocclusions), `screen edge` (the outer 10%), `young` (8 to 31 frames), `interior`, and `none` (no traced surface: sky, the game's foliage; not in `sigma`). Each class reports its sigma, its share of pixels and its share of the squared error.
- **Output.** `summary.md` has a table per scenario, window and mode; the log has a `Motion noise` line each. Images: `<scenario>_motion_<t>_<mode>_err.png` (the error of the middle frame, x8 heat), `_classes.png` (yellow thin, magenta edge, orange fresh, blue screen edge, green young), and with `MotionStrip` (4) the last frames of the first pass as JPEG (`_f92.jpg` .. `_f95.jpg`: 4 consecutive frames at 360 Hz).

**Viewmodel** (`"MotionViewmodel": true`, docs/viewmodel.md "Motion"): the same measure over the pixels the viewmodel pass covers this frame (read back from its G-buffer), which `MotionExclude` keeps out of the main statistics: `viewmodel sigma ... block4 ...` in the log line and a "Viewmodel motion noise" table in `summary.md`. Each pass then starts the gun from the same state: the animators under the main camera go back to their state at the start of the first pass (state, time, parameters), the FOV kick is undone and `Random` is reseeded (a revolver shot picks its animation at random). `"GunBob": 1` moves the gun as `WalkingBob` does when walking at full speed (the bench takes the player over, so the game never bobs it), as a function of path time; with `revolver` effects every 0.25 s the window also has recoil (bone animation). `bench/suites/viewmodel-motion.json`.

`plugin._motionJitterRotate=1` starts each pass at a different TAAU jitter phase, so jitter-dependent flicker (thin geometry alternating between surfaces) counts too. Effect kinds `flash` (a revolver-like muzzle light, 20 / range 5, fading over 0.2 s) and `blast` (an explosion-like light, 60 / range 10) spawn a light only, so the game's random particles don't differ between passes. `bench/suites/motion-noise.json`, `motion-noise-r3.json`, `motion-final.json`, `motion-cost.json` and `motion-blast.json` are behind docs/denoiser.md's "Motion noise". Volumetric haze adds its own noise (docs/volumetrics.md): those suites measure with `vol=0`.

## Methods and traps

Collected from the look-and-perf handoff of 2026-11-18 (`docs/archive/handoff-2026-11-18.md`).

- **`-Extra`**: one comma-separated string (`-Extra "-ultrart-viewprobe,-ultrart-light-census"`); the wrapper
  re-quotes each part. An array does not survive the nested PowerShell call.
- **Sheets**: `python tools/bench/sheet.py <run> <scenario prefix> <out.png> [columns]` labels each mode and prints
  the frame's mean luma. A prefix that is the start of another scenario's name ("0-S spawn" / "0-S spawn skull")
  matches both.
- **Light work goes through the proposal layer**: put the level file or rules in `data/lights/proposed/`, compare
  `pathtrace` against `pathtrace+lc.layer=proposed` in one launch, then move the file and empty the layer. The
  build copies `data/lights` into `stage`, so rebuild after editing them.
- **What lights what**: `lc.solo=none` (every level light off: what is left is sky, emitters and unlit materials),
  `lc.class.NAME=F`, `lc.set.CLASS.FIELD=V` (a kind with a dot: `prefab.shop`), `lc.light.TEXT=0`, `vol.strength=0`,
  `vol.emissive=0`, `vfx.enabled=0`, `ae.levelOverride=0`. `-ultrart-light-census` writes `lights_<scene>.json` per
  layer into the run folder: path, class, rule, policy of every light.
- **A variant can only set what the plugin does not overwrite every frame**: `ae.levelFollowGame` and `plugin._skyScale`
  do nothing as variants (set from the level file; taken back by auto exposure). Use the proposal layer. A variant
  naming a field that does not exist only logs `Burst: no Plugin field` / `no field`: check the log.
- **Modes run one after the other on the same loaded pose**, so time passes: a cutscene pose (6-2 pose 4) drifts
  into the fight by the third mode. Short duration, early shot, few modes.
- **Random level state**: 7-1's dummies take a new pose per load; do not compare shapes across launches.
- **Game-drawn transparents** (queue 3000: grates, light cones, water) are drawn by the game after the traced
  composite with the game's own lighting. They are absent from `_raw.png` and cannot be relit without a proxy or a
  material swap. `_raw.png` against the final shot tells haze and screen effects from the traced image.
- **All water in the game is `VERTEX_LIGHTING`** (the proxy log prints it): "water too bright" is the room being
  too dark, not the water.
- **HDR capture**: a bench with `-Extra "-ultrart-hdr,on,-ultrart-hdr-capture,raw"` logs
  `HDR display: available=False active=False` when the slot's display is not in HDR; check that line before reading
  anything into an "HDR" run.
- **Unity's log**: `Debug.Log` from the plugin does not reach `game.log`; use the BepInEx logger.
