# Trailer: shots, paths and assembly

How the mod's trailer footage is made, from a camera path to an MP4 with wipes, captions and music. Everything is
scripted, so a shot can be re-rendered after a rendering fix and the cut rebuilt with one command.

Parts:
- `scripts/trailer-paths.py`: writes camera paths (and scouting suites) from poses given relative to a known pose.
- `bench/trailer/paths.json` -> `bench/cinematics/trailer-*.json`: the trailer's camera paths.
- `bench/suites/trailer-test.json`, `trailer-test2.json`, `trailer-test3.json`: render the shots (docs/cinematics.md, bench "Cinematic" scenarios).
- `scripts/trailer.py` + `bench/trailer/trailer-test.json`, `trailer-test2.json`, `trailer-test3.json`: the edit lists and the assembly (ffmpeg).
  `trailer-test`, `-test2` and `-test3` are the test cuts (`trailer-test3` also needs the `trailer-test2` run: three of its
  shots are that cut's). The current cut is the final trailer ("The final trailer" below).
- `scripts/trailer-plan.py` + `bench/trailer/final-plan.json`: the final trailer's plan, which writes the `trailer-final-*` suites and
  edit lists; `scripts/trailer_gfx.py` and `scripts/trailer-font.py`: the motion graphics in the game's HUD style ("Graphics" below).

Rendered frames, videos and music are never committed: they live under `bench/results/` (gitignored).

## Quick start (the test cuts: `trailer-test`, `trailer-test2`, `trailer-test3`)

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

**V1 in a shot.** A cinematic scenario can move the player during the take, show V1 to the camera, keep a shop
terminal's screen dark and time the level's flickering lamps (`Player`, `ShowPlayer`, `ShopScreensOff`, `Flickers`:
docs/cinematics.md, "A moving player, third-person shots, lamps"). The third cut's P-2 and terminal shots use them.

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
needs `"Path"`: the bench scenario `Path` block that loads the level (`Approach`, `Load`, ...). `"Scenario"` and
`"Cinematic"` objects are merged into the scout scenario and its `Cinematic` block, e.g.
`"Cinematic": { "Modes": ["off", "pathtrace"], "TracePlayer": true }` to scout both takes of a mirror shot.

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
| `Graphics` | `"motion"`: cards, captions, badges and level titles are animated overlays in the game's HUD style (`scripts/trailer_gfx.py`; "Graphics" under the final trailer) instead of `drawtext` |
| `Labels` | the comparison's two state names (`["RT OFF", "PATH TRACED"]`) |
| `Exports`, `ClipCrf` | further encodes of the finished trailer (`Suffix`, `Height`, `Crf`); the CRF of the per-clip files |
| `Music` | `File`, `Start` (seconds into the track), `Volume`, `FadeIn`, `FadeOut`; or `Tracks`: several pieces on the trailer's timeline, each `File`, `Start`, `At` (when it comes in), `Until`, `FadeIn`, `FadeOut`, `Volume` (files loop) |
| `Clips` | the clips, in order |

Clips (`Type`):

| Type | Fields |
|---|---|
| `shot` | `Source`, `In`, `Out` (seconds of the source), `Speed` |
| `wipe` | `Before`, `After` (two renders of one path), `In`, `Out`, `Wipe` (`Keys`: `[time, position]` pairs, position 0 = all before, 1 = all after, smoothed between keys; `Slant`: lean of the divider, 0 = vertical; `Flip`: "after" on the right, so the wipe comes in from the right; `Line`: the divider's width), `Labels` (before, after) |
| `title` | `Duration`, `Text`, `Sub`, `Small`, `Size`; optional `Source` (+ `Dim`, `Blur`, `Saturation`) puts it over a dimmed shot instead of black |

Every clip takes `Name`, `Caption` (a string, or `{ "Text", "Sub", "At", "For" }`: bottom left), `Tag` (bottom right, e.g.
the level), `FadeIn` / `FadeOut` (from / to black) and `Transition`: how the clip comes in after the one before it
(`cut`, the default; `{ "Type": "fade", "Duration": 0.4 }` cross-fade; `dip` through black). A `Source` is a render
folder (`frame_00000.png` ...; its frame rate comes from `render.json`) or a video file; wildcards pick the last match
by name, so `"*_trailer-test_alt/cine_7-3_tree_off"` is always the newest run. A wipe's left side is "after" (its right
side with `Flip`). The same clip type makes the other comparisons: a hard switch is a wipe whose keys jump
(`[[0, 0], [1.499, 0], [1.5, 1]]`), a split screen one whose keys stay between 0 and 1, a sliding split one that moves
between such values with `"Slant": 0`.

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
on the first shot. Music and every other game-derived file stay out of git. The second cut puts every cut on a bar
line: see "Beat grid" under it.

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

## The second test cut (2026-11-16)

`bench\results\trailer\trailer-test2\trailer-test2.mp4`: 28.9 s, every shot an off / pathtrace pair, cuts on the music's
bars. Edit list `bench/trailer/trailer-test2.json`, shots `bench/suites/trailer-test2.json` (run
`20261116-171559_trailer-test2_alt`; the 1-4 pair from `20261116-172211_trailer-test2-mirror_alt`).

| # | Clip | Treatment | Seconds | What it shows |
|---|---|---|---|---|
| 1 | title | black | 2.34 | |
| 2 | 7-3 tree orbit | slanted wipe, held half way | 4.5 | the tree as the courtyard's light |
| 3 | 0-1 statue room, low glide to the door | hard switch on the bar | 3.0 | lamps that only light what they face; the game's flat, brighter room against it |
| 4 | P-2 hall, crane down from the stained glass | vertical split that slides left, right, then off | 4.5 | bounce light and haze against a hall that is nearly black |
| 5 | 1-1 hallway to the red skull, glide between the torches | slanted wipe from the right | 4.5 | torch light and torch shadows |
| 6 | 1-4 vanity mirror, V1 in front of it | hard switch on the phrase's first beat | 3.0 | the mirror goes from a flat grey texture to a reflection with V1's wings in it |
| 7 | 4-4 boss hall, low arc | fast slanted sweep, then held | 4.5 | fire and lamp light, coloured bounce |
| 8 | end card | black | 2.6 | |

Changed from the first cut: 3-1's eye beams and the daylight 1-1 statue are out; 0-1, the 1-1 torch hallway and the
1-4 mirror are in; P-2 and 4-4 became pairs. Render: 213 s of game time for the six pairs (3,876 frames; an `off` take
now takes as long as a `live` one, 0.04 s per frame) plus 28 s for the mirror re-render; assembly 4 min 50 s (six
wipe clips: the per-pixel blend is the slow part). Scouting: two runs (99 s and 31 s).

Notes from making it:
- **P-2's patch is the puddle.** `Intro/1 - Opening Street/Decorations/Puddle` at (10, -20, 48), an octagon on the floor.
  The `off` take has it in the same place: brightened 14x it is a slightly darker octagon with a faint mottled pattern,
  red like the floor around it. In pathtrace the same octagon shows its mottled texture in yellow-green with more
  contrast. So the object is right; its colour is not the game's (not tinted by the red light, or showing a sky
  reflection).
- **1-1 hallway.** `bench/recordings/1-1-red-skull-hall.json` is the first 13.6 s of `1-1-user-d.json` (3 s to load):
  the hallway is `2 - Indoors`, floor y -4.62, along z 330 from x 30 to 75, then south at x 81 through a doorway to the
  red skull's altar at (81, -2.2, 275). The torches' cups cast large wing-shaped shadows on the walls in pathtrace.
- **Mirrors.** Outside layer 8 the game has one mirror: 1-4's bedroom vanity (`Mirror` material, cracked, behind a shard
  mesh). The player has to stand at x 14.5 or further back: at x 13.6 and 12.8 both `off` and pathtrace render black
  (the game switches the room off or blacks the screen there; not investigated). In the dark bedroom only V1's glowing
  wing feathers read in the reflection. Layer 8's portal mirrors are left out for now (user): seen from a free camera
  with `plugin._rtMirrors=1`, 8-2's floor-2 bathroom mirror showed V1 well in an accumulated render, but the game's own
  mirror view (the `off` take, and the default in every mode) is drawn for the player's eye and showed a small picture
  in a black frame from any other camera position.
- **Beat grid.** `0-1` is 160 bpm: a bar is 1.5 s, a four-bar phrase 6.0 s, with phrase downbeats at 36.34 s + 6 n
  (measured from the onset envelope: the strongest onsets sit at 36.34, 37.83, 43.84, 48.33, 49.84, 54.33, 55.84,
  60.33 s). With `Start` 34.0 the bars fall at 2.34 + 1.5 n seconds of the trailer, and every clip is 2 or 3 bars.
- ffmpeg 8 crashed (segmentation fault, twice, the same command) on a two-input graph with `colorlevels`; nothing in
  `trailer.py` uses that filter.

## The third test cut (2026-11-16)

`bench\results\trailer\trailer-test3\trailer-test3.mp4`: 33.4 s, every shot an off / pathtrace pair shown either as a
hard switch on a bar or as one sweep of the divider (user, after cut 2: no splits that hold half way or move back and
forth). Edit list `bench/trailer/trailer-test3.json`. The P-2, 1-1 hallway and terminal shots are
`bench/suites/trailer-test3.json` (runs `20261116-214344_trailer-test3_alt` and, for the final P-2 take,
`20261116-215032_trailer-test3-p2_alt`); 0-1, 7-3 and 4-4 are the takes of the second cut.

| # | Clip | Treatment | Seconds | What it shows |
|---|---|---|---|---|
| 1 | title | black | 2.34 | |
| 2 | 0-1 statue room (the opener) | hard switch on the bar | 3.0 | lamps and their shadows against the game's flat room |
| 3 | 7-3 tree orbit | one slanted sweep | 4.5 | the tree as the courtyard's light |
| 4 | P-2 street, a first-person run to just before the tunnel | hard switch on the bar | 6.0 | the puddle's reflections, the tunnel's lamp lighting the street as it flickers, V1's long shadow when the lamps cut out |
| 5 | 1-1 hallway to the red skull, round the corner to the doorway | one slanted sweep from the right | 6.0 | torch light and shadows, daylight through the doorway with the pavilion beyond |
| 6 | 1-1 first room: V1 walks up to the shop terminal | hard switch on the bar | 4.5 | V1 lit by the room, its shadow, its reflection in the terminal's glass (a flat dark panel in the game) |
| 7 | 4-4 boss hall, low arc | one slanted sweep | 4.5 | fire and lamp light, coloured bounce |
| 8 | end card | black | 2.6 | |

Render: 142 s of game time for the suite's three shots as first rendered together (P-2 pair, hallway pair, terminal
pair, plus a relight take of P-2: 2,626 frames at 0.04 s each), 56 s for the final P-2 pair alone. Scouting and tests: 10
more runs of 30 to 175 s (most of them on the P-2 shadow, below). Assembly 4 min 50 s.

Notes from making it:
- **P-2's long shadow.** The spot is (4.6, 103.3) on the street, 25 m before the tunnel at z 128, looking down the
  street (fitted to the user's screenshot by the floor's joints: every 5 units, at x = 5 k and z = 88 + 5 k). The shadow
  is V1's own, thrown down the street by the street lamp behind at (10.25, 73). It shows only when two other lamps are
  not lighting that floor: the tunnel's lamp (`2 - Alleyway/Decorations/Metro Light/Spot Light`, a `Flicker`, on / off
  every 0.5 +- 0.5 s) and the lamp post beside the spot (`Streetlight (2)/Point Light` at (10.25, 108), 9 m away, a
  steady light with no `Flicker`). With the lamp post lit, as it is in every bench load, it gives about 37 % of the
  light there against 9 % from the lamp behind, and the shadow is a faint soft stripe in pathtrace and in relight
  alike. With its light off the shadow is crisp in both (tested with a temporary light override). Why it was out in
  the user's screenshot is not known. The cut times both lamps (`Flickers`): the tunnel lamp flickers by itself until
  3.6 s, is on until the camera stops at 4.95 s, then both go off for 0.6 s, on for 0.3 s and off for 0.6 s.
- **The camera height is the game's.** The path's player heights are the recorded ones (y -21.75 on the street, floor
  -23.25) and the eye is the game's 1.4 above that: 2.9 units over the floor. With a 90 degree FOV and a 10 to 14
  degree down pitch it read as low; the path now uses the game's 105 and pitches 5 to 10 degrees.
- **The puddle's "dimples" are rain ripples**, different in every frame; they are no landmark.
- **Terminal.** 1-1's first room is the well-lit one: orange wall lights, the terminal at (6.78, -2, 267) facing -z,
  screen centre about (6.78, 0.9, 266.5). The screen switches on when the player comes within about 4 m, and is on
  for the first frames after a level load, so the shot uses `ShopScreensOff`. V1 stops a little right of the screen's
  centre and the camera is behind on its left, so the mirror image falls on the glass.
- **1-1 corner.** The hallway runs east along z 330 and turns south at x 78.5; the doorway is at z 308, the red skull's
  pavilion outdoors beyond it. Floor y -5 (the player stands at -3.5).
- **8-2's vending machines** (user: their glass panel reflects well) are a candidate for a second V1 reflection shot.
  The nearest to the start is `2 - Reception/.../OfficeVendingMachine (2)` at (7.5, -10, 423.5); a teleport there with
  `"Load": "none"` lands in a void (the room is not loaded), so the shot needs a recorded route into the room.

## The final trailer, third render (2026-11-18, `trailer-final-v3`)

The same cut again (`bench/trailer/trailer-final-final.json`; one plan change: the 5-2 shot's `TierIn` for the final
tier, 4.85 -> 5.2), rendered on `t3/trailer-v3` = `t3/emissive-all` (fixtures x2.5, 7-3 brighter, the handover,
flame-driven torches, the drone's eye; not merged at the time of the render). Output:
`bench\results\trailer\trailer-final-v3\` (`trailer-final-final.mp4`, 2560x1440, 60 fps, 114.84 s, 43 Mbit/s,
622 MB; `trailer-final-final-1080p.mp4`, 233 MB; `clips\`; `weak-shots-v1-v2-v3.png`). Four runs in the alt slot,
11.5 minutes, 0 exceptions; assembly 26 minutes; the frames were deleted afterwards.

**The lightning.** There is no 4-2 shot in the cut: the "lightning flash" is the 5-2 sea shot (#9). The first master
has a strike 1.5 s into the clip; the second render has none in its 4.5 s (its In point was the first master's, and
the take's strikes fall elsewhere). In the new 15 s take the strikes are at 6.4 to 7.3 s and 10.1 to 11.0 s; with In
5.2 the first one is 1.25 s into the clip (mean luma 30 -> 120 -> 30 over a second). A longer take was asked for
(`Render` 30) and did not come: the take's length is the camera path's, 15 s. The strike times moved between the three
renders, so check the In point again at the next render (the scan is a mean-luma spike over the take's frames).

One frame late in each clip (the traced side; mean and 95th percentile of luma, chroma = mean channel spread):

| Shot | v1 -> v2 -> v3 | Against v1 | Against v2 |
|---|---|---|---|
| 0-1 statue room | mean 39 -> 36 -> 43, p95 97 -> 109 -> 132 | brighter highlights | brighter (fixtures x2.5) |
| 7-3 tree | mean 28 -> 18 -> 34, chroma 65 -> 39 -> 69 | a little brighter, the red is back on the walls | **recovered** (7-3 brighter: the blood trees' lights at full reach) |
| P-2 hall | mean 17 -> 12 -> 9, p95 42 -> 34 -> 21 | darker | darker; the level's lamps flicker, so one frame does not decide it |
| 1-1 hallway | p95 180 -> 200 -> 200 | brighter torches | the same level; the torches are flame-driven sphere lights now |
| 8-2 street | mean 70 -> 72 -> 74 | the same | the same (v2's second take, with the pools) |
| 1-3 water | mean 70 -> 76 -> 78 | a little brighter | the same |
| 4-4 hall | p95 145 -> 183 -> 203 | punchier | a little punchier |
| 5-2 sea | mean 32 -> 33 -> 32 at the late frame | the strike 0.25 s earlier in the clip | **the strike is in the clip** |
| P-2 street | mean 14 -> 15 -> 27, chroma 37 -> 37 -> 75 | brighter and far more colour | **brighter, more colour** (its street lamp bulbs are handed over at gain 2) |
| 1-1 terminal | mean 56 -> 37 -> 37, chroma 84 -> 36 -> 36 | **still less red** behind the terminal | unchanged |
| 2-2 street | p95 94 -> 73 -> 77 | a little darker | the same |
| 7-3 gun | mean 22 -> 8 -> 18, p95 97 -> 22 -> 90 | close to v1 | **recovered** (flashlight x4, exposure 0.75 towards the game) |
| 0-5 lava | mean 77 -> 80 -> 81 | the same | the same |
| 8-2 lamp | chroma 37 -> 26 -> 26 | the lamp lit by its bulb | unchanged |
| museum fountain | 93 -> 93 -> 93 | the same | the same |
| 6-2 hall | mean 18 -> 11 -> 11, chroma 80 -> 47 -> 47 | **still a darker floor** | unchanged |
| 1-4 lamps | mean 44 -> 36 -> 44, p95 127 -> 84 -> 111 | the same mean, softer highlights | **brighter** (1-4's lamps handed over at three times the global gain) |

The weak shots of v2, checked again (`weak-shots-v1-v2-v3.png` has the three renders side by side):

- **7-3 tree, 7-3 gun**: fixed by the 7-3 look change. The gun, the path and the tree's red on the walls read as in v1.
- **1-1 terminal**: not fixed. The start room's floating orange lights are off (`prefab:firstroom` x0, replaced by the
  wall strips), so the red walls behind the terminal are darker than in v1, and darker than the game's own image of
  the room. Bringing some of that light back is a decision for every level's start room, not a trailer setting.
- **6-2 hall**: not changed, and it matches the game: the game's own image of the hall has the same dark floor with
  the red at the sides. v1's lit floor was the look before the reach cap.

## The final trailer, second render (2026-11-18, `trailer-final-v2`)

The same cut (`bench/trailer/trailer-final-final.json`, suites `trailer-final-final-1..4`, nothing in the plan
changed) rendered again on the build with the punch lighting on every level, normal strength 1 and normal smoothing
(branch `t3/trailer-v2`, on `t3/window-beams`). Output: `bench\results\trailer\trailer-final-v2\`
(`trailer-final-final.mp4`, 2560x1440, 60 fps, 114.84 s as before, 42 Mbit/s, 599 MB; `trailer-final-final-1080p.mp4`,
225 MB; `clips\`). Four runs in the alt slot, 12 minutes in all, 35 GB of frames (deleted afterwards), 0 exceptions;
assembly 28 minutes. A draft-tier render first (6 minutes for all four suites in one launch) for the framing.

Framing: every shot still frames what it did; no path or timing was changed. Against the first master, one frame
late in each clip (the traced side; mean and 95th percentile of luma, mean channel spread):

| Shot | old -> new | |
|---|---|---|
| 0-1 statue room | mean 39 -> 36, p95 97 -> 109 | as before, a little more contrast |
| 7-3 tree | mean 28 -> 18, chroma 65 -> 39 | **darker.** 7-3's lights have been at x0.5 / x0.7 with the reach cap since 2026-11-17 and its exposure follows the game: less of the tree's red reaches the walls |
| P-2 hall | mean 17 -> 12 (draft: 23) | the level's flicker; no judgement from one frame |
| 1-1 hallway | p95 180 -> 200 | brighter torches, same framing |
| 8-2 street | mean 70 -> 59, p95 136 -> 108 | **worse for its caption** ("Soft beams in the air, pools of light on the ground"): the street lamps light by their glowing heads since 2026-11-17 and their lights are off, so the cones in the air and the pools on the ground are gone |
| 1-3 water, 4-4 hall, 0-5 lava, museum fountain, 5-2 sea | within a few levels; 4-4 p95 145 -> 183 | as before or a little punchier |
| P-2 street | p95 29 -> 49 | the arch at the end of the street is lit |
| 1-1 terminal | mean 56 -> 37, chroma 84 -> 36 | **less colour.** The start room's floating orange lights are off since 2026-11-17 (its wall strips light it): the walls behind the terminal are no longer red |
| 2-2 street | p95 94 -> 73 | a little darker, the colours hold |
| 7-3 gun | mean 22 -> 8, p95 97 -> 22 | **darker.** The forest path and the gun are hard to read between shots; the muzzle light still shows. 7-3 follows the game's exposure and the player's light is x0.6 |
| 8-2 lamp | chroma 37 -> 26 | same shot, the lamp lit by its bulb |
| 6-2 hall | mean 18 -> 11, chroma 80 -> 47 | **darker floor**: the hall's lights no longer reach it (reach cap), as in the game |
| 1-4 lamps | p95 127 -> 84 | brighter lamp heads, a darker path |

Tried for 7-3: `followGame` 0.5 instead of 1 (draft redo of both shots): the tree did not change (22.8 -> 22.5), the
gun shot rose from 8 to 14, and the tour's dark-pixel delta went from +9.1 to +16.4. Not taken; 7-3 ships as it was.
(Later that day 7-3 was made brighter for both shots: docs/lighting-overrides.md, "7-3: brighter". At draft tier
the tree reads mean 36, chroma 68 and the gun shot mean 21, p95 107, against the first master's 28, 64 and 22, 97.)

**8-2 street was rendered again** after its lamps were fixed (`t3/lamp-cones`: the street lamps' lights back on at x1.3 next to their glowing heads; docs/lighting-overrides.md, "8-2's street lamps"): clip 6 and the masters in `trailer-final-v2` carry the new take, with the pools on the ground; the cones in the air are faint.

What would bring the weaker shots back is a look decision per level, not a trailer setting: lamp cones and pools
for 8-2's street lamps next to their glowing heads (done, above), the start room's red, 7-3's exposure. Until then the
first master (`trailer-final-final`) is the better cut for the four shots left and this one for the rest.

## The final trailer (2026-11-16)

Rendered 2026-11-17: `bench\results\trailer\trailer-final-final\trailer-final-final.mp4` (2560x1440, 60 fps, H.264
CRF 13, 47 Mbit/s, 642 MB), `trailer-final-final-1080p.mp4` beside it (scaled from the master, CRF 15, 237 MB) and one
file per clip in `clips\` (CRF 8). Four bench runs in the alt slot, 12 minutes of game time in all (4.1 + 3.4 + 3.4 +
1.3; 0.05 to 0.10 s per output frame, most of it writing PNGs), 37 GB of frames; assembly 27 minutes. No run
failed. The draft tier's output is `bench\results\trailer\trailer-final-draft\`.

`bench/trailer/final-plan.json` is the whole trailer: the shots in order, each a whole number of bars, with its path,
treatment, caption and level title, plus the quality tiers and the music. `scripts/trailer-plan.py` turns it into the
bench suites and the edit lists, so the draft and the final render are the same cut at different quality.

```powershell
python scripts\trailer-font.py "<ULTRAKILL folder>"                       # once: the game's font for the graphics
python scripts\trailer-paths.py bench\trailer\paths.json                  # after editing paths
python scripts\trailer-plan.py bench\trailer\final-plan.json              # suites + edit lists for every tier
#   --tier draft            only that tier
#   --only "8-2 street,7-3 gun"   also writes trailer-final-<tier>-redo.json with just those shots
.\scripts\bench.ps1 -Suite trailer-final-draft-1 -Slot alt                # ... -2, -3, -4: 5 shots a run
python scripts\trailer.py bench\trailer\trailer-final-draft.json          # -> bench\results\trailer\trailer-final-draft\
```

The edit list's sources are wildcards over every run of the tier (`*_trailer-final-<tier>-*_alt/cine_<shot>`), and the
newest match wins, so re-rendering one shot with the `-redo` suite replaces just that shot.

### Plan fields

| Field | |
|---|---|
| `Bar`, `Margin` | seconds per bar (1.5 at 160 bpm); extra seconds rendered either side of a comparison's switch |
| `Tiers` | per tier: `Mode` (the cinematic's render mode), `Modes` (the scenario's RT mode and runtime variants), `OutputScale`, `Fps`, `Preroll`, `Width`, `Height`, `Crf`, `ClipCrf`, `Exports` |
| `Scenario` | fields for every bench scenario (`"Enemies": "blind"`) |
| `Labels`, `Graphics`, `Music` | passed to the edit list |

Shots: `Kind` is `title` / `end` (a card: `Card` holds the title clip's fields), `beauty` (one traced take), `switch`
(the game's image, then a hard switch to the traced one after `Off` seconds) or `sweep` (the divider crosses once,
starting after `Off` seconds and taking `Sweep`; `Slant`, `Flip` as in a wipe). `Bars` (+ `Lead` / `Seconds`) is the
length; `File` the path in bench/cinematics; `Path` and `Scenario` go to the bench scenario, `Cinematic` to its
cinematic block, `TierCinematic` the same for one tier; `ModeAdd` appends runtime variants to the tier's `Modes` for one
shot; `In` starts the clip that far into the take and `Render` renders a longer take than the clip (a shot whose best
moment is picked afterwards; `TierIn` gives `In` per tier; 5-2's lightning is now called on cue instead, `Calls` in docs/cinematics.md); `TimeScale` 0 freezes the world for one shot; `Caption` and `Tag`
as in the edit list.

### How the footage is made

- **Real-time path tracing with DLSS Ray Reconstruction, not offline accumulation.** The first draft was accumulated
  (2 samples a frame, then 64): without a denoiser 64 samples a pixel are still visibly grainy in these levels, and the
  user asked why it did not look denoised. `"Mode": "live"` renders what a player sees. In play RR runs at a third of
  the output resolution (854x480 for 1440p); the final tier adds the runtime variant `ptx.scaleRR=1`, which runs it at
  the full 2560x1440 (DLAA; the log says "feature 2560x1440 -> 2560x1440"). `bench\results\trailer\rr-compare.jpg` has
  the three side by side: full-resolution RR is clean and sharp, a third is clean but soft, 64 accumulated samples are
  sharp but grainy. 2 paths a pixel and 3 bounces were tried on top: no visible gain for 1.5 to 3 times the time.
- **Paired capture** (docs/cinematics.md): every comparison is one take that writes the game's image and the traced
  image of each frame with the world held between them, so the world runs (flames, smoke, rain, flicker, V1 walking,
  gunfire) and the two images still match. `OnFrom` / `OffUntil` come from the shot's `Off` and `Sweep`, so each image
  is only written where the cut shows it.
- 0-1's statue room is the one frozen shot (`"TimeScale": 0`): with the world running, the bench's enemy handling
  leaves spawn and death smoke in that arena room.
- Nothing is staged. P-2's street is a plain first-person jog (`"Player": "camera"`); its lamps flicker as the level
  has them. The gun shot is the game's own revolver fired by the bench at fixed path times (`ShowWeapon`, `Shots`).
- Tiers: `draft` is 1280x720 at 30 fps with RR at a third (about 2 minutes of game time per run of five shots, 3.5
  minutes to assemble); `final` is 2560x1440 at 60 fps with RR at full resolution, clips at CRF 8, master at CRF 13 and a
  1080p export (CRF 15) scaled from the master.

### Graphics: the game's own HUD style

`"Graphics": "motion"` in an edit list replaces the `drawtext` captions with animated overlays from
`scripts/trailer_gfx.py` (Pillow; PNG sequences that the assembly overlays). They copy the game's UI, measured from its
assets (`LevelNamePopup`, `HudMessageReceiver`, `HudOpenEffect`, the fonts bundle):

| Element | In the game | In the trailer |
|---|---|---|
| Font | VCR OSD Mono, white, a hard dark underlay offset down and right | the same font file, extracted by `scripts/trailer-font.py` to `bench\results\trailer\fonts` (not in git); `TRAILER_FONT` overrides; Consolas stands in without it |
| Level title | canvas 720 units high; the layer line (`PRELUDE /// FIRST`) 26 units, centred 245 above the middle; the name (`INTO THE FIRE`) 46 units, 210 above; typed at 100 letters a second, half a second between the lines, 3 s hold, 1 s fade | `"Tag": ["PRELUDE /// FIRST", "INTO THE FIRE"]`: the same layout and timing at 0.85 of the size, with the level's real strings (read from each level's MapInfo). Shown the first time a level appears. |
| Feature caption | HUD message: a rounded black box at 59 % opacity, low in the middle, that snaps open from 5 % (width first, then height), text typed with a block cursor | `Caption`: the same box and motion; heading in capitals, an optional sub line under it |
| State badge | - | top left, the same box: `RT OFF` grey with a hollow marker, `PATH TRACED` white with an accent marker; it snaps open again and flashes when the picture under it changes (at the switch, or when the divider passes it) |
| Cards | - | on black, laid out as a level title: sub line typed first, then `ULTRART` under it in large type ("RT" in the accent colour), further lines typed, small print faded in. `Beats`: when the sub line, the title and the first further line start. |

No logo and no art of the game is used; the cards say "a path tracing mod for ULTRAKILL" and "unofficial", and the end
card's small print says how the footage was captured and that the mod is not affiliated with the game's developers.

### Music

0-1's theme, both layers from the game's files, 160 bpm, bars at 0.34 + 1.5 n seconds. The calm layer (`0-1 Clean`)
plays from its start under the title and the first eight shots (40 bars, to 60.34 s); the battle layer (`0-1`) comes in
there from its step up at 36.34 s of the song and runs 32 bars to its loop point (108.34 s of the trailer); the calm
layer returns under the end card and fades over 4.5 s. Every cut, switch and gunshot (half bars) is on the grid.

### Shot list

The cut as of the fourth render (v4, 2026-11-19): 2-4's tunnel and 2-1's water (the user's request of 2026-11-18) replace 8-2's standing lamp and 6-2's hall.

All comparisons are paired live takes (the game's image / real-time path tracing with RR); "beauty" is one traced take.

| # | Start | s | Shot | Level | Treatment | Caption |
|---|---|---|---|---|---|---|
| 1 | 0:00.0 | 4.84 | Title card | - | card | |
| 2 | 0:04.8 | 7.5 | Statue room glide (world frozen) | 0-1 | switch at 3.0 s | Ray-traced shadows |
| 3 | 0:12.3 | 7.5 | Tree orbit | 7-3 | sweep | Emissive lighting |
| 4 | 0:19.8 | 7.5 | Hall crane from the stained glass | P-2 | sweep from the right | Global illumination |
| 5 | 0:27.3 | 9.0 | Torch hallway, round the corner to the doorway | 1-1 | sweep | |
| 6 | 0:36.3 | 7.5 | Street, lamp posts and the pool | 8-2 | sweep from the right | Lamp light |
| 7 | 0:43.8 | 6.0 | Glide down to the water | 1-3 | sweep | Water reflections |
| 8 | 0:49.8 | 6.0 | Boss hall, low arc | 4-4 | sweep from the right | Coloured bounce light |
| 9 | 0:55.8 | 4.5 | Storm sea, a lightning strike on the second bar (`Calls`) | 5-2 | beauty | |
| 10 | 1:00.3 | 6.0 | First-person jog down the street (`Player` camera) | P-2 | switch at 2.25 s | Wet surfaces |
| 11 | 1:06.3 | 6.0 | V1 walks up to the shop terminal (`Player` path, `ShopScreensOff`) | 1-1 | switch at 2.25 s | Ray-traced reflections |
| 12 | 1:12.3 | 4.5 | Neon street pan | 2-2 | sweep | |
| 13 | 1:16.8 | 6.0 | First-person walk, revolver fired on the half bars (`ShowWeapon`, `Shots`) | 7-3 | switch at 1.5 s | Gunfire is a light source |
| 14 | 1:22.8 | 4.5 | Low along the lava | 0-5 | switch at 1.5 s | |
| 15 | 1:27.3 | 4.5 | Push down the tunnel towards the red-lit arches (the user's pose 3) | 2-4 | switch at 1.5 s | |
| 16 | 1:31.8 | 4.5 | Fountain in daylight | museum | sweep | |
| 17 | 1:36.3 | 6.0 | Glide down onto the water channel towards the door (the user's pose) | 2-1 | sweep from the right | Reflective water |
| 18 | 1:42.3 | 6.0 | Night lamps, pull back | 1-4 | beauty, fade out | |
| 19 | 1:48.3 | 6.5 | End card | - | card | |

8-2's street was first captioned "Volumetric light": the game draws its lamp cones as bold polygons and the traced
cones are soft and faint, so the comparison did not carry that caption. What the traced image adds there is the lamps'
light on the ground and the pool's reflections.

Total 1:54.8. Tried and left out: 6-1's candle hall and 8-2's vending machine (the bench's area loads land in a void
there), a replay of the recorded 0-2 run (no gunfire, and it leaves the level after 20 s), 1-1's fountain (the water is
too small in frame to read), 7-1's pool (the pose loaded without its level state: grey and unlit), layer 8's mirrors and portals, 3-1.
Out since v4: 8-2's standing lamp and 6-2's first hall (both kept in `paths.json`: `final-8-2-lamp`, `final-6-2-nave`).
Scouted for v4 and left out: 6-2's Grand Hall at the user's pose 1 after `t3/stained-glass` (the windows show their
lead pattern, but no beam on the floor read from any of 14 views, and the hall is the same all-red look as the 6-2 shot
that went out), 0-S's skull-lit corridor (a held item needs the skull in a cinematic take; the 2-4 tunnel showed the
same week's work better).

Known problems in the footage, left as they are (renderer bugs are not fixed on this branch):
- V1's body reads grey in 1-1's orange room when path traced (blue in the game's image and in its own reflection).
- P-2's puddles have a yellow-green tint (v1-v3; in v4's P-2 hall take, on the stained-glass build, the puddle reads red).
- The bench's install has no custom gun colours, so the revolver and V1's gun are in the default colours.
- P-2's long shadow from the street lamp is faint: the steady lamp post beside it lights the same ground.
- 5-2's lightning is random. Up to v3 the clip's `In` was set to a strike of the take in use (`TierIn`, a 15 s take);
  v4's 15 s take had no strike at all. Since v4 the strike is called on cue (`Calls`: the nearest
  `LightningStrikeDecorative_Animated`'s `FlashStart` at 2.45 s of the path; the flash comes 0.3 s later, 1.55 s into
  the clip, mean luma 33 -> 116, back to 33 after a second), and the take is the clip's length.
- The game's image of a gunshot shows the gun one animation frame later than the traced image of the same frame.
