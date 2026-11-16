# Trailer: shots, paths and assembly

How the mod's trailer footage is made, from a camera path to an MP4 with wipes, captions and music. Everything is
scripted, so a shot can be re-rendered after a rendering fix and the cut rebuilt with one command.

Parts:
- `scripts/trailer-paths.py`: writes camera paths (and scouting suites) from poses given relative to a known pose.
- `bench/trailer/paths.json` -> `bench/cinematics/trailer-*.json`: the trailer's camera paths.
- `bench/suites/trailer-test.json`: renders the shots (docs/cinematics.md, bench "Cinematic" scenarios).
- `scripts/trailer.py` + `bench/trailer/trailer-test.json`: the edit list and the assembly (ffmpeg).

Rendered frames, videos and music are never committed: they live under `bench/results/` (gitignored).

## Quick start (the test cut)

```powershell
# 1. Paths (only after editing bench\trailer\paths.json)
python scripts\trailer-paths.py bench\trailer\paths.json
# 2. Render the shots: about 4 minutes of game time in the alt slot (bench/README.md, "How to run tests")
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -NoDeploy
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\death\.t3\projects\ultrart\scripts\game-lock.ps1 acquire <you> -Kind visual -Slot alt
.\scripts\deploy.ps1 -Slot alt -Owner <you>
.\scripts\bench.ps1 -Suite trailer-test -Slot alt -TimeoutSec 1500
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\death\.t3\projects\ultrart\scripts\game-lock.ps1 release <you>
# 3. Assemble (about a minute; no game needed)
python scripts\trailer.py bench\trailer\trailer-test.json
#    -> bench\results\trailer\trailer-test\trailer-test.mp4, and one MP4 per clip in ...\clips\
```

`trailer.py --only 2,5` rebuilds only those clips (the others are reused) and joins again: the loop for tuning a wipe
or a caption. `--root` and `--out` move the source and output folders.

## Render modes for footage

docs/cinematics.md has the renderer. For a trailer, two of its modes matter:

| Mode | Use | Cost at 1440p |
|---|---|---|
| `live` | the RT image **as played**: path tracing at the play trace scale, DLSS Ray Reconstruction, the real-time filters, one rendered frame per output frame on the fixed time step | about 0.03 s per frame (paced by the PNG writer) |
| `off` | the game's own image along the same path: the "before" | about 0.1 s per frame |
| `pathtrace` (accumulating) | converged stills and hero shots | 2.5 s per frame at 64 spp |

`live` keeps the footage honest (it is what a player's screen shows, including the denoiser's behaviour in motion) and
is fast enough to iterate on camera paths. The DLSS SDK has to be in `lib\dlss` when the native DLL is built
(`scripts\fetch-dlss.ps1`); without it `live` falls back to UltraRT's own denoiser (the log says
`DLSS Ray Reconstruction unavailable`).

**Before / after pairs.** Render the same path twice (`"Mode": "off"` and `"Mode": "live"`), both with
`"TimeScale": 0`. The path is a pure function of time and the world is frozen, so the two takes line up exactly and
can be wiped against each other. With the world running (`TimeScale` 1) the camera still lines up, but particles,
flames and anything else the game randomises differ between the takes.

## Authoring paths without flying them

Flying a path by hand (Home, K, Ctrl+S) is the best way to get exactly the move you want. `trailer-paths.py` is for
doing it blind, from screenshots:

1. Pick a **base pose** the player has stood at: the end of a recorded route in `bench/recordings` (a bench pose). It
   is known to be in a loaded room with free space around it, and there is usually a screenshot of it in a bench run.
2. Describe candidate camera positions **relative to it**: `[forward, right, up, yaw, pitch]` in metres and degrees along
   the base pose's own view direction. "8 m forward, 2 m up" can be read off the screenshot.
3. **Scout**: `python scripts\trailer-paths.py bench\trailer\scout-example.json --scout bench\suites\trailer-scout.json --out bench\results\trailer\scout`
   turns every key into a held "cut" key and writes a suite that renders one quarter-size, 8-sample frame per key
   (about 0.2 s each; a level load per spot). Look at the frames: keys inside a wall or a tree trunk show at once, and
   the good ones are the path's keys.
4. Write the path in `bench/trailer/paths.json` with durations, easing and a field of view (the game's FOV is very wide;
   75-90 reads as a camera rather than a player). `"LookAt": [forward, right, up]` aims every key at one point, which
   makes orbits and push-ins easy to keep framed.
5. Draft it in `live` mode at `"Fps": 30, "OutputScale": 0.25` (seconds), look at a strip of frames, adjust, render.

A spec entry:

```json
{ "Name": "trailer-7-3-tree-orbit", "Scene": "Level 7-3",
  "Base": { "Player": [-129.86, -14.40, 483.61], "Yaw": 271.1, "Pitch": 10 }, "LookAt": [15, 0, 5.2], "Fov": 80,
  "Keys": [ [8.3, -6.7, -0.9, 0, 0, 2.2, "in"], [5.6, -3.4, 0.0, 0, 0, 2.2, "linear"], [5.0, 0.9, 1.2, 0, 0, 2.2, "out"], [6.3, 5.0, 2.6] ] }
```

A key is `[forward, right, up, yaw, pitch, seconds to the next key, ease, fov]`; everything after `up` is optional.
`up` moves the player's feet, as a path's `Player` keys do: the camera is 1.4 m above. For `--scout`, an entry also
needs `"Path"`: the bench scenario `Path` block that loads the level (`Approach`, `Load`, ...).

What worked for the test cut: orbits and push-ins around one subject, 1.5-2.5 m/s, three or four keys, "in" on the
first segment and "out" on the last. Stay near the base pose: every metre away from where the player walked is a
guess until a scout frame shows it.

## The edit list

`bench/trailer/trailer-test.json`. Top level:

| Field | |
|---|---|
| `Name` | output name |
| `Root` | folder the sources and the music are relative to (relative to the edit list); `--root` overrides |
| `Width`, `Height`, `Fps`, `Crf` | the output (1280x720, 60, 18) |
| `Font`, `TitleFont` | font files (default `C:/Windows/Fonts/bahnschrift.ttf`) |
| `Music` | `File`, `Start` (seconds into the track), `Volume`, `FadeIn`, `FadeOut` |
| `Clips` | the clips, in order |

Clips (`Type`):

| Type | Fields |
|---|---|
| `shot` | `Source`, `In`, `Out` (seconds of the source), `Speed` |
| `wipe` | `Before`, `After` (two renders of one path), `In`, `Out`, `Wipe` (`Keys`: `[time, position]` pairs, position 0 = all before, 1 = all after, smoothed between keys; `Slant`: lean of the divider; `Line`: its width), `Labels` (before, after) |
| `title` | `Duration`, `Text`, `Sub`, `Small`, `Size`; optional `Source` (+ `Dim`, `Blur`, `Saturation`) puts it over a dimmed shot instead of black |

Every clip takes `Name`, `Caption` (a string, or `{ "Text", "Sub", "At", "For" }`: bottom left), `Tag` (bottom right, e.g.
the level), `FadeIn` / `FadeOut` (from / to black) and `Transition`: how the clip comes in after the one before it
(`cut`, the default; `{ "Type": "fade", "Duration": 0.4 }` cross-fade; `dip` through black). A `Source` is a render
folder (`frame_00000.png` ...; its frame rate comes from `render.json`) or a video file; wildcards pick the last match
by name, so `"*_trailer-test_alt/cine_7-3_tree_off"` is always the newest run. A wipe's left side is "after"; a hard
A/B toggle is a wipe whose keys jump (`[[0, 0], [1.99, 0], [2.0, 1]]`), a static split screen one that stays at 0.5.

The script renders each clip to `clips/NN_<name>.mp4` (near-lossless), then joins them and adds the music. Text is
drawn with ffmpeg's `drawtext`; the wipe is a per-pixel `blend` expression, which is why a wipe clip takes a few seconds.

## Music

The user OK'd the ULTRAKILL soundtrack for this. There is no Steam soundtrack on this machine
(`D:\SteamLibrary\steamapps\music` does not exist), so the track comes from the game's own files:
`ULTRAKILL_Data\StreamingAssets\aa\StandaloneWindows64\music_assets_layer*.bundle` hold every level's music as
AudioClips (named `0-1`, `0-1 Clean`, `7-2 Intro`, ...; "Clean" is the calm layer, the plain name the battle layer).
With UnityPy in a scratch venv (`pip install UnityPy`), `obj.read().samples` gives the WAV:

```python
env = UnityPy.load(r"...\music_assets_layer0.bundle")
for obj in env.objects:
    if obj.type.name == "AudioClip" and obj.read().m_Name == "0-1":
        for name, data in obj.read().samples.items():
            open(r"bench\results\trailer\music\0-1.wav", "wb").write(data)
```

The test cut uses `0-1` (the battle layer of 0-1's theme, 84 s) from 33.9 s: the track steps up at 36.0 s, which lands
on the first shot. Music and every other game-derived file stay out of git.

## The test cut (2026-11-16)

`bench\results\trailer\trailer-test\trailer-test.mp4`: 24.1 s, 1280x720 at 60 fps, from run `20261116-160807_trailer-test_alt`.

| # | Clip | Source | Seconds | What it shows |
|---|---|---|---|---|
| 1 | title | black | 2.4 | "ULTRART / path tracing for ULTRAKILL" |
| 2 | 7-3 tree orbit, wipe | `cine_7-3_tree_off` / `_live`, frozen world | 5.2 | the red tree as the courtyard's only light: flat red walls against lit stone, leaf shadows and the tree's glow on the walls |
| 3 | 3-1 eye beams | `cine_3-1_eye_beams`, world running | 3.7 | the eye's beam through the ribs (volumetric channel, traced shadows) |
| 4 | P-2 hall | `cine_P-2_hall`, world running | 4.0 | crane down from the stained glass into the hall: bounce light, haze, vent smoke |
| 5 | 1-1 statue push, wipe | `cine_1-1_statue_off` / `_live`, frozen world | 4.6 | daylight: pillar shadows, sky light, the glass under the statue |
| 6 | 4-4 hall | `cine_4-4_hall`, world running | 3.4 | braziers and wall lamps lighting the hall |
| 7 | end card | black | 2.4 | |

Render: 271 s of game time for the 5 scenarios (7 takes, 2,449 frames, 5 level loads): a `live` take of a 6 s path
takes 13-15 s (0.04 s per frame, DLSS RR from 854x480 to 1440p, box-filtered to 720p), an `off` take 45 s (0.12 s per
frame: it waits for the PNG writer with only 3 frames queued). Assembly: 2 min 20 s. Scouting: two runs of about 2
minutes (8 spots, 99 frames) plus one draft run.

How the blind paths turned out: all five were usable on the first or second try. Orbits and push-ins around one
subject work; the scout frames caught the one bad key (inside the 7-3 trunk at "forward 15"). What a hand-flown path
would do better: moves that pass close to geometry (through a doorway, along a wall), anything that follows the level's
flow for more than about 10 m, and timing a move to an animation.

Seen while reviewing (candidates for fixes before the real trailer):
- **1-1 loses its sunrise tint.** The game paints the pillars' sun side warm brown and their caps gold; in pathtrace
  they are neutral grey stone. The shadows and the glass are better, the colour is flatter: in the daylight wipe RT
  does not clearly win.
- **P-2 floor patch.** A yellow-green pixelated patch on the entrance hall floor (bottom right from 2 s of the shot):
  looks like a puddle or decal with the wrong texture or reflection. Not checked against `off`.
- **P-2 exposure.** The last second of the path runs into the vent smoke and the image brightens by a third; the cut
  stops before it.
- **1-3 terminal glare.** In the `live` test of `1-3-water-glide` the first frames show a white-purple bloom blob on the
  shop terminal's sign.
- A before / after pair rendered as two scenarios does not show the same instant (7-3's falling leaves had moved):
  `Cinematic.Modes` renders both takes in one scenario with the world held (docs/cinematics.md).

## Planned shot list for the full trailer

About 75 s, 22 shots. "cine" = scripted camera path through the renderer (`live` pathtrace unless noted); "pair" =
`off` + `live` takes of one path with a frozen world; **LIVE** = needs the user's own play (see below).

| # | Shot | Level | Mode | s | Shows |
|---|---|---|---|---|---|
| 1 | Cold open: down the entry corridor into daylight, RT off; hard switch to path traced on the first hit of the music | 1-1 start (`1-1-start-descent`) | pair, toggle | 4 | the whole idea in one cut |
| 2 | Title sting | - | title | 2 | |
| 3 | Tree orbit, slanted wipe (in the test cut) | 7-3 | pair | 5 | emissive light, leaf shadows |
| 4 | Boss hall, wipe the other way | 4-4 | pair | 4 | fire and lamp light against the game's dim flat gold |
| 5 | Red hall and stairs, static split screen with a moving camera | 0-2 (v1 shadow a pose) | pair | 3.5 | shadows, red bounce |
| 6 | Stained-glass corridor, wipe | 6-2 start | pair | 3.5 | coloured light through glass |
| 7 | Statue between the pillars (in the test cut; after the tint fix) | 1-1 | pair | 4 | sun shadows, sky light, glass |
| 8 | Metal room, camera a hand above the floor towards the door | 0-1 | cine | 3.5 | reflections on metal, statue lights |
| 9 | Water glide | 1-3 (`1-3-water-glide`) | cine | 4 | water reflections, stained glass |
| 10 | Storm sea, lightning | 5-2 | cine, world running | 4 | scrolling sea, rain, lightning as light |
| 11 | Fountain and palms | museum | cine | 3 | water and glass in daylight (the brightest shot) |
| 12 | Mirror room: V1 in the mirror | 8-3 Secret/Mirror | **LIVE** (or a replayed recording) | 3.5 | mirrors, self reflection (the cinematic camera hides V1) |
| 13 | Through a portal doorway | 8-2 / 8-3 | **LIVE** or replay of `8-3-b` | 3.5 | portals traced, light through them |
| 14 | Eye beams through the ribs (in the test cut) | 3-1 | cine | 3.7 | volumetrics |
| 15 | Hall reveal from the stained glass (in the test cut); second angle: up the stairs to the lit door | P-2 | cine | 4 | bounce light, haze, smoke |
| 16 | Lava channel, camera low along the lava | 0-5 | cine | 3.5 | lava as an emitter |
| 17 | Neon street | 2-2 | cine | 3 | coloured emissives, purple bounce |
| 18 | Flashlight in the forest | 7-3 start | **LIVE** | 3 | the player's light and its shadows |
| 19 | Gun and muzzle flash: revolver shots lighting a corridor, viewmodel lit by the room | 0-2 / 1-1 corridor | **LIVE** | 3 | viewmodel RT, effect lights |
| 20 | Combat montage, 4 quick cuts: explosions lighting a room, blood on wet floors, coins and their reflections, Gabriel's wings | 0-2, 1-1 arena, 6-2 | **LIVE** | 9 | it plays like this |
| 21 | Candle hall or night lamps, slow pull back | 6-1 / 1-4 | cine, 64 spp accumulated | 4 | a clean hero frame to end on |
| 22 | End card over the last shot, dimmed | - | title | 4 | name, "work in progress", credits |

**The LIVE shots.** Two ways, both honest footage of the real-time pipeline:
- Record the run with F10 (bench/README.md, "Recording a path") and replay it through the renderer: a cinematic scenario
  whose `Path` is the recording, with `"Mode": "live"` (docs/cinematics.md, "Replaying a recorded run"). The camera, the
  player and the arenas repeat on the fixed time step, so the same run can also be rendered `off` for a before / after
  of gameplay; enemies and gore are not identical between takes. Not tried yet with `live`.
- Or capture the screen while playing (OBS at 60 fps). The game's cheats can hide the HUD and the weapon for clean
  frames; keep the weapon for shots 19 and 20.

For the final cut: render at the window's 1440p (`OutputScale` 1) or supersample to 1080p, 60 fps; keep `live` for
everything that moves and use an accumulated `pathtrace` render only for the closing hero shot.
