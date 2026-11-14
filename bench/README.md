# UltraRT benchmarks

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

## Recording a path
1. Launch normally (`-force-d3d12`), load a level and play or fly around.
2. Press **F10** to start and **F10** again to stop. Recordings are saved to `BepInEx\ultrart\recordings\<level>_<time>.json` in the game folder.
3. Copy the file into `bench/recordings/` and reference it from a suite:
   ```json
   { "Name": "0-5 lava run", "Scene": "Level 0-5",
     "Path": { "Type": "recording", "File": "../recordings/Level_0-5_20261113-180000.json" },
     "Enemies": "active", "Modes": ["off", "pt-dn"], "Screenshots": [5, 15] }
   ```

`"KillEnemies": true` kills enemies as they spawn while the recording plays, as the approach replay does. Arenas then finish, and the doors and rooms they open load on time. With that, a pose's approach route (F11) can be replayed as a real-time run: `bench/suites/perf-8-3.json` walks the user's 8-3 route that way, with one mode per pass, so the cost of rooms loading, clones and spawns is measured as it happens in play. `perf-8-3-route-hybrid.json` is the hybrid pass only, about 2 minutes; `perf-regress.json` covers 1-1 and 0-2.

A recording stores the player's position and look angles at 60 Hz, plus every arena (`ActivateArena`) that triggered and when. On replay the camera follows the path exactly and the same arenas trigger at the same times, so fights happen in the same places.

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

### Saving a pose
Press **F11** in normal play. It appends the pose to `BepInEx\ultrart\poses.jsonl` and saves two files next to the recordings: the route walked since the level loaded (`approach_*.json`) and the area diff (`areas_*.json`). The route also records every checkpoint you activated, so the benchmark knows exactly where to jump. Older routes have no checkpoint list; for those the last checkpoint the route passed through is used.

Copy the approach file into `bench/recordings/` and use it:
```json
{ "Name": "4-1 indoor A", "Scene": "Level 4-1",
  "Path": { "Type": "static", "Approach": "../recordings/4-1-approach-a.json", "Duration": 8 },
  "Modes": ["off", "hybrid", "pt-dn"], "Screenshots": [4] }
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
