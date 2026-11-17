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
shadow and reflection would hang where the player stands), unless "V1's reflection and shadow stay" is on (`TracePlayer`,
default off; bench `"TracePlayer": true` in the `Cinematic` block): for a mirror shot, stand the player in front of the
mirror and put the camera ahead of it, so V1 shows only in the reflection (camera rays never see V1's model itself:
docs/reflections.md, section 13; a bench scenario can ask for it, see "A moving player, third-person shots, lamps").
"Enemies ignore the player" (menu, default on) makes enemies
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
- **Off and live.** Two view modes skip the accumulation. `off` renders the game's own image (ray tracing off) along the
  same path: the "before" of a before / after pair (its AA samples still supersample the game's raster; `Aa` 1 is the
  game as it looks). `live` renders the current view mode **as played**: trace scale, denoiser, DLSS Ray Reconstruction
  or the upsampler and all their histories run exactly as in play, one rendered frame per output frame on the fixed time
  step, and every temporal filter blends as at the output frame rate (`Denoiser.FrameTimeOverride`). It is honest footage
  of what players see and costs a frame per frame (the PNG writer paces it: the render sleeps instead of rendering
  extra frames, which would add temporal history the player never gets). `Samples`, `Aa`, `Paths`, `Bounces` and motion
  blur do not apply to `live`. In a bench scenario `live` uses the scenario's first RT mode (`"Modes": ["pathtrace"]`).
- **Restore.** A `finally` block puts back every setting it changed (view mode, trace scale, denoiser / reflection /
  light-sampling / pathtrace settings, guard band, RR, upsampler, time step, time scale, vsync, frame limit, audio,
  background running), also on an error, a cancel, or a level change. Nothing is written to the game's settings.

### Settings ([Cinematics] in the config, "Cinematics" in the menu; bench overrides in brackets)

| Setting | Default | |
|---|---|---|
| view mode (`Mode`) | pathtrace | pathtrace, relight, the current mode (`current`), `off` (the game's image), `live` (the current mode as played) |
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

### Sample renders and quality / time (2026-11-15, RTX 5070 Ti, 2560x1440)

`bench/suites/cine-demo.json`, run `bench/results/20261115-140450_cine-demo/` (not committed: frames, MP4s, `render.json`):

| Clip | Mode | Frames | Samples | s / frame | ms / sub-frame | Render + encode | Video |
|---|---|---|---|---|---|---|---|
| `1-3-water-glide` 1.0-4.0 s | pathtrace | 181 (3 s at 60 fps) | 16 x 4 AA = 64 spp | 2.49 | 38.9 | 451 s + 22 s | `cine_1-3_glide_pathtrace\1-3-water-glide.mp4` (24 MB) |
| same | relight | 181 | 64 | 0.77 | 12.0 | 139 s + 25 s | `cine_1-3_glide_relight\1-3-water-glide.mp4` |
| `1-1-start-descent` 0.5-3.5 s | pathtrace | 181 | 64 | 2.45 | 38.2 | 443 s + 26 s | `cine_1-1_descent_pathtrace\1-1-start-descent.mp4` (36 MB) |

Live playback of the same paths in pathtrace (DLSS RR): 10.2 ms average, 19.8 ms p99 (1-3); 7.6 / 16.3 ms (1-1). The
pathtrace clips' mean luma moves at most 2.8 (1-3) and 4.4 (1-1) per frame, with no reversals: the camera walking from
the lit room into the corridor and out into daylight, not exposure flicker.

Cost: a sub-frame is one full game frame with a full-resolution trace: about 38-46 ms in pathtrace at 1440p in these
corridors (one path, 2 bounces, water and glass on), 16 ms in 6-1's dark hall, 12-14 ms in relight. Time per output
frame = AA x samples x that. Two paths per frame cost 25.2 ms against 15.8 for one (6-1): about 20% cheaper per sample
than accumulating twice as many frames, so raise `Paths` before `Samples` once the frame count is long. The PNG writer
(350-470 ms per frame at 1440p, on the thread pool) is never the limit in pathtrace; very fast relight renders at a
few samples are paced by it.

Quality (one 1-3 frame at 4 AA, RMS difference in luma against 64 x 4 = 256 spp):

| Samples per AA sample | spp | s / frame | RMS vs 256 spp | Mean luma |
|---|---|---|---|---|
| 4 | 16 | 0.71 | 6.9 | 36.95 |
| 16 | 64 | 2.85 | 3.1 | 38.28 |
| 64 | 256 | 11.75 | - | 38.37 |

- Noise halves for every 4x the time, as it should for an unbiased mean: 64 spp leaves a fine grain on the dark red
  walls (about 2.7 luma), 256 spp is clean. 64 spp is a good draft, 128-256 for final footage.
- Keep `Samples` at 16 or more: each AA sample is converged in HDR and tone-mapped before the AA samples are averaged, so
  very few samples per AA sample average noisy tone-mapped values and come out darker (4% at 4 samples, 0.2% at 16).
- AA 4 is enough for the game's texture-and-edge look; more AA samples cost the same as more samples, so spend the budget
  on samples. Motion blur uses the AA samples as time steps: use 4-8 with it.
- Supersampling (`OutputScale` 0.5: 1280x720 from 1440p) puts 4 rendered pixels into each output pixel: the same render
  time, half the noise. For 1440p output there is no supersampling (the render is the window's size).

### Determinism

`bench/suites/cine-replay.json` (run `20261115-142513_cine-replay`) replays seconds 4-6 of the 0-2 run twice from fresh
loads (relight, 4 spp). The camera path, the player's position, the arena timing and the enemy that spawns are the same
in both takes, frame for frame; the game's random smoke and dust particles are not (they are emitted with Unity's
random numbers, which the level's seed does not pin down). On 16x16-pixel block means the two takes differ by 0.7 luma
(noise) once the smoke has cleared and by up to 6.7 luma while it drifts through the view. A static path in a frozen
world (game speed 0) renders the same image every time, up to sampling noise.

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

### A moving player, third-person shots, lamps

Four more fields of the `Cinematic` block, for shots where V1 itself matters (bench only; `bench/suites/trailer-test3.json`
uses all of them):

| Field | |
|---|---|
| `"Player": "camera"` | The player rides the camera path: the camera is the player's eye, as in play, so V1's shadow and its reflection (puddles, metal) move with the shot. A first-person walk without recording one. |
| `"Player": "<path file>"` | The player walks a second path (same format as a camera path: each key is the player's position and view direction) while the camera flies `File`. V1 is kept in the traced scene, its walk animation follows the route's speed (the parameters `PlayerAnimations` would set from `NewMovement`, which the bench has switched off), and its head and gun arm follow the route's view direction, not the camera's. The walk cycle restarts with every take, so the takes of a pair animate alike. |
| `"ShowPlayer": true` | The camera sees V1's model: a third-person shot. Traced modes: camera rays include V1's instance mask (bit 16 of `_UltraRTPlayerRays`, set only here). `off` takes: the main camera draws layer 30, where the model lives. Default: on when `Player` is a path file. |
| `"ShopScreensOff": true` | Shop terminals stay dark. A terminal's screen switches on when the player enters its zone, and the shop UI then covers the glass, which is the part that reflects. The zone's trigger is off while the camera is on. |
| `"Flickers": [ { "Light": "Metro Light/Spot Light", "From": 3.6, "To": 4.95, "On": true }, ... ]` | Lamp timing. `Light` is the end of a light's hierarchy path. A lamp with the game's `Flicker` component (fully on / off at random intervals) is held in that phase with its own `On()` / `Off()` for the stretch of path time and flickers by itself outside the stretches. A steady light is switched (the `Light` component only; its lamp mesh keeps glowing) and put back afterwards. The log lists the level's nearest flickering lamps and what each entry matched. |

Use `"TimeScale": 1` with a moving player: the walk animation needs game time, and so do flickering lamps. The two
takes of a pair then differ in whatever the game randomises (particles, untimed flicker), which a hard switch hides and
a sweep mostly does. With `Player`, `Start` > 0 plays the route up to `Start` first, as a replay does.

What the model looks like: in `off` takes V1 is fullbright, as in the game's own mirrors; path traced it is lit by the
room (a blue body in an orange room reads grey; the wings keep their yellow glow). The bench's weapon is the default
revolver in default colours; the user's alternate gun colours live in the main install's save.

## Before / after pairs and trailers

Render the same path twice, once with `"Mode": "off"` and once with `"Mode": "live"` (or an accumulating mode), both with
`"TimeScale": 0`: the path is a pure function of time and the world is frozen, so the two takes line up pixel for pixel
and can be wiped or split against each other. In a bench scenario, `"Modes": ["off", "live"]` in the `Cinematic` block
renders both takes back to back into `cine_<scenario>_off` and `cine_<scenario>_live` and keeps the world frozen from the
first take to the last, so particles (falling leaves, smoke) are at the same instant in both; two separate scenarios
line up in camera and geometry only. `scripts/trailer.py` does the wipe and the rest of an edit (title cards,
captions, cross-fades, music) from an edit-list JSON: see docs/trailer.md.

### Paired capture: both images from one take, world running

`"Paired": true` in a bench `Cinematic` block (any mode but `off`) writes two folders from one take:
`cine_<scenario>_off` (the game's own image) and `cine_<scenario>_<mode>` (the traced one). The world is stepped once per
output frame and held while both images of that frame are rendered, so flames, particles, flickering lamps, a walking
V1 and a fired gun are at the same instant in both, with the world alive (`"TimeScale": 1`). Frame numbers are the
same in both folders.

| Mode | Order within an output frame |
|---|---|
| accumulating (`pathtrace`, `relight`, `current`) | the world steps; one unrecorded frame with the view off; the game's image (all `Aa` samples); two unrecorded frames with the view back on; the accumulated samples. The unrecorded frames are there because lit particles and lamp cones follow the view switch in `Update`, a frame late. |
| `live` | the world steps and the traced frame is rendered straight away, so every temporal filter and DLSS RR see the previous output frame as their previous frame; then one held frame with the view off for the game's image. Lit particles' light volume and the effect geometry keep their state across the view-off frames. |

`"OnFrom"` and `"OffUntil"` (path seconds) limit which frames each folder gets: the game's image up to `OffUntil`, the
traced one from `OnFrom`. A comparison that switches at 3 s needs the game's image only until then. Traced frames are
still rendered throughout (one sample per frame outside the window in accumulating modes), so exposure and histories
are settled when the window opens; a frame outside a window is simply not written, and `scripts/trailer.py` addresses
frames by number. The player-driven options above work as before.

`"ShowWeapon": true` (with `"Player": "camera"`, and `"Weapon": <slot>` on the scenario) keeps the gun in view: the HUD
camera draws the viewmodel again, lit by the traced scene as in play, and the HUD's own canvases stay hidden.
`"Shots": [0.75, 2.25, ...]` fires at those path seconds (`"ShotKinds"`, default `["revolver"]`: the held revolver fires as
the player fires it, with its muzzle light, beam and recoil; any kind of the bench's `Effects` works). Add the runtime
variant `plugin._vmFullScreen=1` to the mode: the viewmodel pass otherwise works in a rectangle measured a few frames
earlier, and the recoil swings the gun out of it for a moment.

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
