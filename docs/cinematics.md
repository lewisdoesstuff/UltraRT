# Cinematics: camera paths and offline renders

Demo footage of the mod: a free camera that leaves the player frozen, keyframed camera paths saved per level, smooth
playback in any view mode, and an offline renderer that writes clean, path-traced, anti-aliased frames at a fixed frame
rate (however slow each frame is to render) and encodes them with ffmpeg.

Code: `src/UltraRT/Plugin.Cinematics.cs` (free camera, keys, playback, overlay, save/load, menu),
`Plugin.CineRender.cs` (offline render), `Plugin.Cinematics.Bench.cs` (bench scenarios),
`Cinematics/CinePath.cs` (file format, spline), `Cinematics/FrameSink.cs` (grab, readback, averaging, PNG, ffmpeg).
Hooks elsewhere: `CineUpdate` in `OnUpdate`, `CineOnSceneChanged` on a scene change, `DrawCine` in the HUD, a gate in
`RunExposure`, `CineSettingsMenu` in the settings menu, the `Cinematic` branch in the bench's `RunScenario`.

## Quick start

1. Play to where the shot starts (the level in the state you want: doors open, rooms loaded, enemies where you want them).
2. **Home**: the camera detaches; the player, HUD and gun are frozen / hidden. Fly with WASD + mouse.
3. **K** at each key pose. **P** plays the path from the selected key (**Shift+P** from the start). Fix keys with
   Left / Right (select and jump there), **U** (update from the camera), Up / Down (segment duration), **T** (easing).
4. **Ctrl+S** saves the path (and the level state, see below).
5. **End** renders it. Progress is at the bottom of the screen; End (or pausing) cancels. Frames and the
   video land in `BepInEx\ultrart\renders\<path>_<time>\`.
6. **Home** again: everything goes back (camera, player, HUD, gun, game speed).

## Keys

Home toggles the cinematic camera (only in a level, not while paused; never during a benchmark). While it is on, the
game's own player and camera input is locked (as the bench's camera takeover does), so the letters below don't reach
the game. Esc still pauses: the pause menu (and PluginConfigurator's page) show while paused.

| Key | Action |
|---|---|
| W A S D, Q / E, mouse | fly (Q down, E up, world vertical); Shift x4, Alt x0.25; mouse wheel: speed |
| Z / C, X | roll left / right (hold), level the horizon |
| R / F, V | zoom in / out (hold; Shift faster), back to the game's FOV |
| K | add a key at the camera, after the selected key (or at the end) |
| U | update the selected key from the camera |
| Delete / Backspace | delete the selected key |
| Left / Right | select the previous / next key and jump the camera there |
| Up / Down | the selected key's segment (to the next key): +/- 0.25 s (Shift: 1 s) |
| T | the selected segment's easing: linear, smooth, in, out, cut |
| G | even speed: every segment's duration in proportion to its length, the total kept |
| P / Shift+P | play from the selected key / from the start; P stops (the camera stays there) |
| Ctrl+S | save the path |
| L | load the next saved path of this level |
| N, N | new, empty path (press twice) |
| H / Shift+H | hide the overlay / the help lines |
| End | render the path offline; End cancels, and so does pausing (Esc): frames written so far are kept and encoded |

Chosen not to clash: F-keys are UltraRT's and the bench's (F10 record, F11 pose), Insert + numpad the light editor
(which keeps working with the free camera: fly to a light, Keypad 5 selects it), `]` opens PluginConfigurator, F8 the
game's console. Home and End are unused by the game. The settings menu has a "Cinematics" group with the render settings
and buttons for on/off, play, render, save and load; the Keybinds page lists these keys.

## Keys and segments

A key is a camera position, rotation (quaternion: roll included) and vertical FOV (`Camera.fieldOfView`; 0 = the game's).
Each key has a **duration** (seconds to the next key) and an **easing** for that segment:

| Easing | Segment |
|---|---|
| linear | constant speed along the curve (arc length), the default |
| smooth | starts and stops gently (smoothstep) |
| in / out | starts slowly / stops gently (use on the first / last segment) |
| cut | holds the key for the duration, then cuts to the next key (several shots in one path); the spline restarts after a cut |

The curve (`CineSpline`): centripetal Catmull-Rom through the positions (no overshoot or loops between close keys),
re-parameterised by arc length so linear segments move at constant speed; SQUAD between the rotations (smooth across
keys, never the long way round); Catmull-Rom on the FOV. It is a pure function of time, so playback has no frame-rate
dependence or jitter, and the offline renderer samples exactly the same curve. Speed changes where two segments of
different speed meet: G evens them out.

## Saved paths and level state

Ctrl+S writes `BepInEx\ultrart\cinematics\<level>\<name>.json` (`<name>` = `<level>_<time>` the first time, then
overwritten). Like F11 with a pose, it also saves the route walked since the level loaded (`approach_*.json`) and the
area-trigger state (`areas_*.json`) into `BepInEx\ultrart\recordings\`, and names them in the path file
(`Approach`, `Areas`, `Checkpoint`). Rendering in the session you made the path in needs none of that: the level is
already in its state. They are for rendering later from a fresh load through the bench (below). Entering the camera
loads the newest saved path of the level if none is loaded; L cycles through them.

```json
{ "Name": "1-3-water-glide", "Scene": "Level 1-3", "Approach": "approach_Level_1-3_....json", "Areas": "...",
  "Keys": [ { "Position": [0, 1.9, 257], "Rotation": [0, -0.05, 0, 0.99], "Fov": 105, "Duration": 2, "Ease": "in" }, ... ] }
```

Hand-written files may give `Player` (feet position, as a pose's `Position`) instead of `Position`, and `Yaw` / `Pitch` /
`Roll` (degrees, as poses) instead of `Rotation`: the camera then goes to the player's eye (`EyeOffset`, measured when
the camera starts: 1.4 m up). `bench/cinematics/*.json` are written that way from the bench recordings' routes.

## Playback

P plays the path in the current view mode at real time: what you see is what the player would see if the camera moved
like that (RR, the denoiser and the upsampler behave as in play). The camera is posed in `Application.onBeforeRender`,
after every LateUpdate, so nothing in the game moves it later in the frame, and every temporal pass (denoiser,
upsampler, RR, reflections, ReSTIR) reprojects with that frame's camera matrix as usual. Object motion vectors are
world space and don't depend on the camera. A jump (cut segment, a key selected, more than 3 m or 25 degrees in a frame)
resets the temporal histories instead of smearing.

While the camera is free: the player is frozen where it stood (kinematic, input locked, as the bench takes it over);
the HUD canvases and the gun (HUD camera, and the viewmodel RT pass) are hidden; V1 is kept out of the traced scene (its
shadow and reflection would hang where the player stands). "Enemies ignore the player" (menu, default on) makes enemies
blind and the player invincible by swapping only the cheats' static state (not the cheat menu: nothing is saved and
the level is not marked as cheated). "Game speed during playback / render" (default 1) slows or freezes the world (0 =
frozen: animations, particles, enemies and effects stop, the camera still moves); it applies while the camera is on.

## Offline render

**End** (or the menu's "render" button, or a bench scenario) renders the whole path, or `Seconds` of it from `Start`.

How a frame is made (`Plugin.CineRender.cs`):
- **Fixed time step.** `Time.captureDeltaTime` = 1 / fps: game time advances exactly one frame per output frame, however
  long the frame takes to render. Between those steps the world is held (`Time.timeScale` 0) while the frame is rendered
  AA x samples times. Audio is paused for the render.
- **Accumulation.** Within an AA sample the camera and world are still; the denoiser and the reflection filter run as
  pure running means (no a-trous, no history clamps, no firefly clamp or pre-blur, no stabilisation, history up to 255
  frames), light sampling is RIS (no reservoir reuse), and their histories restart at every AA sample. So every frame
  adds one independent path-traced sample per pixel (x paths per pixel) and the result converges to the path tracer's own
  mean (its firefly clamp, roulette and radiance-cache tail are part of that mean; switch the tail off for a reference).
  Tracing is at full resolution (scale 1, no guard band, no RR, no temporal upsampler).
- **Anti-aliasing.** Each AA sample offsets the camera's projection by a Halton sub-pixel jitter: the game's raster and
  our trace (which reads that matrix) move together. The AA samples are averaged (float) on a worker thread.
- **Motion blur** (option): the AA samples are spread over the shutter instead: game time and camera time step by
  shutter / fps / AA between them (each still accumulated to convergence), the rest of the frame time comes after.
- **Exposure.** Auto: metered once per output frame on the converged image, with a 1 / fps clock, so it adapts exactly as
  at that frame rate; the warm-up meters every frame on a coarse clock so frame 0 starts settled. Locked: as when the
  render starts (preview the path first).
- **Warm-up.** `preroll` frames (60) at the first pose with the world held: the radiance cache fills, exposure settles,
  the pathtrace pipeline compiles if it never ran this session, and spawned renderers get their RT proxies.
- **Output.** The final frame (the game's post-processing and transparents included, our overlay not) is copied from the
  back buffer into a render texture at the end of the frame and read back with `AsyncGPUReadback`: the GPU never waits.
  A worker thread averages the AA samples; the thread pool box-filters to the output size and writes
  `frame_00000.png` ... (8-bit RGB, or 16-bit). The render waits only when 3 frames are queued.
- **Encode.** If ffmpeg is found (PATH, WinGet's Links folder, `C:\ffmpeg\bin`, scoop, chocolatey), the frames are encoded
  to `<path>.mp4` (H.264, CRF from the menu, yuv420p, faststart) next to them; `encode.cmd` always has the command.
  `render.json` records the settings and timings.
- **Restore.** A `finally` block puts back every setting it changed (view mode, trace scale, denoiser / reflection /
  light-sampling / pathtrace settings, guard band, RR, upsampler, time step, time scale, vsync, frame limit, audio,
  background running), also on an error, a cancel, or a level change. Nothing is written to the game's settings.

### Settings ([Cinematics] in the config, "Cinematics" in the menu; bench overrides in brackets)

| Setting | Default | |
|---|---|---|
| view mode (`Mode`) | pathtrace | pathtrace, relight, or the current mode |
| frames per second (`Fps`) | 60 | |
| accumulated frames per AA sample (`Samples`) | 32 | samples per pixel = AA x this x paths; 16 for quick takes, not below (see below) |
| AA / motion-blur samples (`Aa`) | 4 | sub-pixel jitter positions, or time steps with motion blur |
| paths per pixel per frame (`Paths`) | 1 | pathtrace; 2 is about 20% cheaper per sample than 2 frames |
| bounces (`Bounces`), radiance cache tail (`CacheTail`) | 2, on | pathtrace |
| output size (`OutputScale`) | 1 | below 1: rendered at screen size and box-filtered down (supersampling) |
| motion blur (`MotionBlur`), shutter (`Shutter`) | off, 0.5 | |
| exposure (`Exposure`) | auto | auto / locked |
| game speed (`TimeScale`) | 1 | world time per path second; 0 = frozen world |
| 16-bit PNG (`Png16`), encode (`Encode`), CRF (`Crf`) | off, on, 16 | |
| bench only: `Start`, `Seconds`, `Preroll`, `Play` | 0, all, 60, off | |

The render size is the game window's (2560x1440 here). It is never changed: for a smaller video use the output size.

### Sample renders and quality / time

See "Sample renders" at the end (filled in from `bench/suites/cine-demo.json`).

## Bench: cinematic scenarios

A scenario with a `Cinematic` block loads its level like a pose (`Path.Approach`, `Areas`, `Load`), then plays the path
in real time (`"Play": true`: frame times and a screenshot halfway, in the scenario's first RT mode) and/or renders it
offline into `bench/results/<run>/cine_<scenario>/`. Saves, prefs and Steam stats stay blocked (BenchSafety), the
player is taken over by the bench, and every override applies to that scenario only. The summary gets a "Cinematics"
table (playback frame times; frames, size, sub-frames per frame, s/frame, ms per sub-frame, preroll, writer time, video).

```json
{ "Name": "1-3 glide pathtrace", "Scene": "Level 1-3",
  "Path": { "Type": "static", "Approach": "../recordings/1-3-start-water.json", "Duration": 1 },
  "Modes": [ "pathtrace" ],
  "Cinematic": { "File": "../cinematics/1-3-water-glide.json", "Play": true, "Mode": "pathtrace",
                 "Start": 1, "Seconds": 3, "Samples": 16, "Aa": 4 } }
```

To render a path saved in game from a fresh load: copy it and its `approach_*.json` (and `areas_*.json`) from
`BepInEx\ultrart\cinematics\` / `recordings\` into `bench/cinematics/` / `bench/recordings/`, point `Path.Approach` at the
approach file, and run the suite. Long renders need `bench.ps1 -TimeoutSec` (default 900 s).

Suites: `cine-test.json` (smoke test: live playback, short pathtrace and relight renders), `cine-test2.json`
(1 vs 2 paths per frame, motion blur, supersampled 16-bit output), `cine-demo.json` (the sample renders, ~25 min).

### Replaying a recorded run

A cinematic scenario without `File` whose `Path` is a `recording` (F10, bench/README.md "Recording a path") replays the
run through the offline renderer: the camera follows the recorded eye (keys every 1/30 s along the route), the player is
moved along with it (`GameControl`, as a recording benchmark does), the recorded arenas start at their times
(`ReplayArenas`), and `KillEnemies` works as in a benchmark. `Start` > 0 first plays the route from its beginning up to
`Start` on the fixed time step (one plain frame per step), so the run is in its state there instead of teleported. V1 is
traced in replays (the camera is where the player is). `bench/suites/cine-replay.json` replays 2 s of the 0-2 run twice
from fresh loads; see "Determinism" below.

## Limitations

- The render size is the window size; supersampling only goes down from it (Unity's resolution would be a display
  setting, which UltraRT never changes).
- The camera flies, the player stays: rooms and enemies the game activates by player position (area triggers, arenas)
  don't react to the camera. Get the level into its state first (play there, or a bench approach / checkpoint load).
- Enemies, physics and gore are not replayed: a render is one take of the level from the time Render is pressed. With a
  frozen world (game speed 0) every take is identical.
- The offline image is the path tracer's converged mean at the window's resolution: textures stay as sharp as the game
  draws them, there is no depth of field, and screen effects (bloom, haze) are the real-time ones.
- Pixels the game draws itself over our image (its sprites, particles, UI-like glows) are averaged across the AA samples
  but are not path traced.
