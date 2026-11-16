# Trailer: shots, paths and assembly

How the mod's trailer footage is made, from a camera path to an MP4 with wipes, captions and music. Everything is
scripted, so a shot can be re-rendered after a rendering fix and the cut rebuilt with one command.

Parts:
- `scripts/trailer-paths.py`: writes camera paths (and scouting suites) from poses given relative to a known pose.
- `bench/trailer/paths.json` -> `bench/cinematics/trailer-*.json`: the trailer's camera paths.
- `bench/suites/trailer-test.json`, `trailer-test2.json`, `trailer-test3.json`: render the shots (docs/cinematics.md, bench "Cinematic" scenarios).
- `scripts/trailer.py` + `bench/trailer/trailer-test.json`, `trailer-test2.json`, `trailer-test3.json`: the edit lists and the assembly (ffmpeg).
  `trailer-test3` is the current cut (it also needs the `trailer-test2` run: three of its shots are that cut's);
  `trailer-test` is the first one.

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
| `Music` | `File`, `Start` (seconds into the track), `Volume`, `FadeIn`, `FadeOut` |
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

## Planned shot list for the full trailer

About 80 s, 21 shots, leaning on comparisons. The user's notes so far: more on / off pairs; only two treatments, a
hard switch on a bar or one sweep of the divider (no splits that hold or move back); open on 0-1; P-2 is a favourite
(the street, its puddles and the long shadow); 1-1 is the torch-lit hallway and its doorway, not the daylight statue;
V1 and its reflection should be seen (terminal, vending machine); 3-1's eye beams are out; layer-8 mirrors wait for
their own handling. "pair" = `off` + `live` pathtrace takes of one path in one scenario; "cine" = one `live` pathtrace
take; **LIVE** = needs the user's own play (see below).

| # | Shot | Level | Mode | s | Treatment / shows |
|---|---|---|---|---|---|
| 1 | Title sting | - | title | 1.5 | |
| 2 | Statue room, low glide to the door (in cuts 2 and 3) | 0-1 | pair | 3 | hard switch: the whole idea in one cut |
| 3 | Tree orbit (in every test cut) | 7-3 | pair | 4.5 | sweep: emissive light, leaf shadows |
| 4 | Street run to the tunnel (in cut 3) | P-2 | pair, `Player` camera, `Flickers` | 6 | hard switch: puddle reflections, the flickering lamp, V1's long shadow |
| 5 | Hall reveal from the stained glass (in cuts 1 and 2) | P-2 | pair | 4.5 | sweep: bounce light, haze |
| 6 | Hallway to the red skull, round the corner to the doorway (in cut 3) | 1-1 | pair | 6 | sweep from the right: torch light, daylight through the door |
| 7 | Through that doorway to the red skull's pavilion | 1-1 | pair | 3 | hard switch: sunlight and shade outdoors |
| 8 | V1 walks up to the shop terminal (in cut 3) | 1-1 first room | pair, `Player` path, `ShopScreensOff` | 4.5 | hard switch: V1 lit, its reflection in the glass |
| 9 | V1 at a vending machine's glass | 8-2 reception (needs a recorded route) | pair, `Player` path | 3 | sweep: a second reflection, cooler light |
| 10 | Boss hall, low arc (in every test cut) | 4-4 | pair | 4.5 | sweep: fire, lamps, bounce |
| 11 | Red hall and stairs | 0-2 (v1 shadow a pose) | pair | 3 | hard switch: shadows, red bounce |
| 12 | Stained-glass corridor | 6-2 start | pair | 3 | sweep: coloured light through glass |
| 13 | Water glide | 1-3 (`1-3-water-glide`) | pair | 4.5 | sweep: reflections |
| 14 | Storm sea, lightning | 5-2 | cine, world running | 3 | scrolling sea, rain, lightning as light |
| 15 | Lava channel, camera low along the lava | 0-5 | pair | 3 | sweep: lava as an emitter |
| 16 | Neon street | 2-2 | pair | 3 | hard switch: coloured emissives, purple bounce |
| 17 | Flashlight in the forest | 7-3 start | **LIVE** | 3 | the player's light and its shadows |
| 18 | Revolver shots lighting a corridor, the gun lit by the room | 0-2 / 1-1 corridor | **LIVE** | 3 | viewmodel RT, effect lights |
| 19 | Combat montage, 4 quick cuts on the beat: explosions lighting a room, blood on wet floors, coins and their reflections, Gabriel's wings | 0-2, 1-1 arena, 6-2 | **LIVE** | 9 | it plays like this |
| 20 | Candle hall or night lamps, slow pull back | 6-1 / 1-4 | cine, 64 spp accumulated | 4.5 | a clean hero frame to end on |
| 21 | End card over the last shot, dimmed | - | title | 4.5 | name, "work in progress", credits |

Lengths are whole bars of the music (1.5 s at 160 bpm) so cuts and hard switches land on downbeats. Not in the list
until they have their own handling: layer 8's portal mirrors and portal doorways (8-2, 8-3). Dropped: 1-4's vanity
mirror (dark; the terminal shows V1's reflection better).

**The LIVE shots.** Two ways, both honest footage of the real-time pipeline:
- Record the run with F10 (bench/README.md, "Recording a path") and replay it through the renderer: a cinematic scenario
  whose `Path` is the recording, with `"Mode": "live"` (docs/cinematics.md, "Replaying a recorded run"). The camera, the
  player and the arenas repeat on the fixed time step, so the same run can also be rendered `off` for a before / after
  of gameplay; enemies and gore are not identical between takes. Not tried yet with `live`.
- Or capture the screen while playing (OBS at 60 fps). The game's cheats can hide the HUD and the weapon for clean
  frames; keep the weapon for shots 18 and 19.

For the final cut: render at the window's 1440p (`OutputScale` 1) or supersample to 1080p, 60 fps; keep `live` for
everything that moves and use an accumulated `pathtrace` render only for the closing hero shot.
