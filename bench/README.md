# UltraRT benchmarks

```powershell
.\scripts\bench.ps1                       # short suite (~1 min)
.\scripts\bench.ps1 -Suite full           # everything
.\scripts\bench.ps1 -Compare 20261113-171622_short   # diff against an earlier run in bench\results
```

Each run launches the game on DX12 and plays every scenario in `bench/suites/<suite>.json`, then quits. Output goes to `bench/results/<timestamp>_<suite>/`: `results.json`, `summary.md` and screenshots. The run is tagged with the git commit (`-dirty` if the tree has uncommitted changes).

**Saves are safe.** For the whole run the plugin skips every save-file, prefs and Steam-stat writer (`BenchSafety`). Cheats (invincibility, plus blind enemies or no spawns) are enabled for the session only, with `saveState:false`.

## How a scenario is measured
- The level loads with a fixed `Seed` (`Random.InitState`, applied before the level's own `Start`s run), then waits `Settle` seconds.
- The player is taken over the way noclip does it, and the camera follows `Path`.
- `Modes` alternate every `Segment` seconds *within* the same pass (off / pt-dn / off / ...). This keeps the comparison fair even when the scene isn't deterministic, such as enemies or physics. The first `SwitchSkipFrames` frames after each switch aren't counted.
- Frame times are uncapped (vsync off, no frame limit). GPU per-pass times come from the native timer (`-NoGpuTimer` turns it off).

## Path types
| Type | Fields | Use |
|---|---|---|
| `spawn` | `Duration` | hold the level's spawn pose |
| `static` | `Position` [x,y,z], `Yaw`, `Pitch`, `Duration` | hold a given pose |
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

A recording stores the player's position and look angles at 60 Hz, plus every arena (`ActivateArena`) that triggered and when. On replay the camera follows the path exactly and the same arenas trigger at the same times, so fights happen in the same places.

Replays are *statistically* repeatable, not identical. Enemy AI, physics and gore still vary between runs, even with a fixed seed. Raw keyboard/mouse replay would drift after a few seconds for the same reason, so we record the resulting path instead.

Recording itself doesn't enable cheats or block saves. It's just normal play.
