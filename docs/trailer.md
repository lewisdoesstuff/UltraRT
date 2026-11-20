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
or a caption. `--root` and `--out` move the source and output folders; `--no-exports` skips the edit list's `Exports` (the 1080p copy).

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
| `wipe` | `Before`, `After` (two renders of one path), `In`, `Out`, `Wipe` (`Keys`: `[time, position]` pairs, position 0 = all before, 1 = all after, smoothed between keys; `Slant`: lean of the divider, 0 = vertical; `Flip`: "after" on the right, so the wipe comes in from the right; `Line`: the divider's width; `Border`, `Shade`: the dark edge either side of it, 1.5 px at 720p and the picture at 0.3 of its brightness by default, `"Border": 0` for none), `Labels` (before, after) |
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

## Trailer v6 draft (2026-11-20, branch `t3/trailer-v6-paths`)

New camera paths, constant-speed easing, a combat footage slot at the battle drop and hard switches in the battle half,
on master aa7ad54 + 1f8c96c (Gabriel's glowing swords, enemy outlines off, `HighlightHue` 0.5, water absorption and
ripple caustics, P-2's rain rings, and the v6 graphics). Draft tier only (1280x720, 30 fps, RR at a third): every shot is
a new take. Output: `bench\results\trailer\trailer-v6-draft\` in the branch worktree (`trailer-final-draft.mp4`, 117.84 s,
44 MB; `clips\`; `contact-v5d-v6draft.jpg`: per shot v5d final | v6 draft at 15 % and 85 % of the clip; `timing.txt`:
the frame check), copied to the main checkout's `bench\results\trailer\trailer-v6-draft\`. Takes:
`20261120-161454_trailer-final-draft-1..4_alt` (one alt launch, 0 failures, RR `854x480 -> 2562x1440`).

Paths (`bench/trailer/paths.json`; scout `bench/trailer/scout-v6.json`, suite `trailer-v6-scout`, game | path traced per
key, run `20261120-161145_trailer-v6-scout_alt`):
- **4-4 Window Hallway**: no longer a centred push down the stairs. A low diagonal glide along the hall's west half
  (z 418 -> 436, x 114 -> 117, feet 649), turned 48 -> 32 degrees towards the slatted window, so the striped light on the
  floor and the window wall sweeps across the frame; the west wall's torches pass on the left. At x 112 and less the
  ledge by the west wall comes into the bottom left, so the glide starts at x 114.
- **Museum gallery**: an angled diagonal glide instead of the centred push: from left of the centre path (z 803) across to
  its right (z 808.5) while moving 13 m down the gallery, turned 24 -> 14 degrees towards the -z wall, so the
  portraits and the checkerboard slide sideways. The pedestal rows begin about 7 m either side of z 805, so the glide
  stays over the centre path.
- **8-3 Clockwork Crushers**: v5d's wipe changed room mid-sweep because v5d looked at the opening at the hall's end
  (`Portals A`): through it, and through the side glass's sideways loop portals, the game's portal views and the trace
  show different rooms (the game an orange crusher room, the trace a blue office), so the RT-off half showed one room
  and the traced half another. The scout showed that looking back at the entrance door (yaw 180), both images show the
  same hall, door and frosted glass. v6 is a slow pull-back from the door (z 674 -> 683, 1.8 m/s, a small drift and a
  turn of 8 -> -5 degrees off the axis) with the world frozen (`TimeScale` 0: the crushers moving in the side rooms at
  the end of v5d's take are gone). The opening stays behind the camera. It is a quieter shot than v5d's: the traced side
  adds the pink glow of the lit panel above the door on the ceiling and floor and the far panes in the side glass.
- **Constant speed on the short shots**: `final-0-3-beams`, `final-4-4-window-hall`, `final-8-2-market-aisle`,
  `final-8-3-crushers`, `final-museum-gallery`, `final-1-4-hall`, `final-8-2-street`, `final-1-1-terminal-cam` and
  `final-5-2-sea` go from `"smooth"` (a stop at each end) to `"linear"`. Every one of those paths is longer than its
  clip, so the camera is moving on the first frame and still moving at the cut. The multi-key arcs (1-1 hallway, 7-3,
  P-2 hall, 0-1, 2-1, 6-2 organ) keep their `in` / `linear` / `out` keys. 2-4's tunnel was already linear.

Plan (`bench/trailer/final-plan.json`):
- **Out: 6-2's entry** (4 bars). **In: a 4-bar combat footage slot** (`"Kind": "title"`, "COMBAT FOOTAGE" /
  "PLACEHOLDER - 4 BARS (6.0 S)") right at the battle layer's step up (63.34 s), before P-2's street. The battle layer is
  still exactly 32 bars (4 + 5 + 4 + 3 + 3 + 3 + 3 + 3 + 4), ending on its loop point at 111.34 s. To swap in the
  recording, replace that line with a `video` shot of the same bars (above, "Plan fields").
- **Hard switches in the battle half**: 4-4 and 8-2's market are `switch` at 1.5 s (the bar), like 2-4's tunnel. The
  sweeps left in the battle half alternate: 8-3 from the right, the museum from the left, 1-4 from the right (it was
  from the left: the museum's direction changed).
- **End card**: ULTRART, then "FREE MOD FOR ULTRAKILL  ///  THUNDERSTORE", and as small print "CAPTURED LIVE WITH
  REAL-TIME PATH TRACING AND NVIDIA DLSS RAY RECONSTRUCTION" / "UNOFFICIAL MOD - NOT AFFILIATED WITH NEW BLOOD
  INTERACTIVE OR ARSI "HAKITA" PATALA". **The game's font has no em dash**: "—" draws as an empty box, so the cards use
  "-".

| # | Start | s | Shot | Treatment |
|---|---|---|---|---|
| 1-10 | 0:00.0 | 63.34 | title, 0-3, 6-2 organ, 1-1 hallway, 0-1, 7-3, P-2 hall, 8-2 street, 2-1, 5-2 (as in v5d) | calm layer, 42 bars |
| 11 | 1:03.3 | 6.0 | combat footage slot (placeholder card) | card |
| 12 | 1:09.3 | 7.5 | P-2 street, through the arch into the tunnel | beauty |
| 13 | 1:16.8 | 6.0 | 4-4 Window Hallway, diagonal glide | switch at 1.5 s |
| 14 | 1:22.8 | 4.5 | 1-1 terminal | beauty |
| 15 | 1:27.3 | 4.5 | 8-2 supermarket aisle | switch at 1.5 s |
| 16 | 1:31.8 | 4.5 | 8-3 Clockwork Crushers, pull-back from the door | sweep from the right |
| 17 | 1:36.3 | 4.5 | 2-4 tunnel | switch at 1.5 s |
| 18 | 1:40.8 | 4.5 | museum gallery, diagonal glide | sweep |
| 19 | 1:45.3 | 6.0 | 1-4 hall | sweep from the right, fade out |
| 20 | 1:51.3 | 6.5 | end card with the tagline and small print | card |

Total 1:57.8, as v5d. Staged events, re-measured on the new takes: 0-3's light pillars come on at frame 92 (3.067 s, the
same as v5d), so `In` stays 1.567. The organ hall turns red at frame 328 of the draft take (10.933 s; mean 14 -> 64),
0.25 s later than in v5d's final take (frame 641 at 60 fps, 10.68 s), so the draft tier's `In` is 6.433
(`"TierIn": { "draft": 6.433 }`). The final tier keeps 6.183 until its own take is measured. In the cut the pillars
land on clip frame 45 (1.500 s) and the red flip on clip frame 135 (4.500 s), both on the bar.

Frame check (`timing.txt`): every clip has its planned length, and the master is the 20 clips joined (3535 frames). Each
clip frame is matched against its take frames (+-4) on a central crop. 9 of 17 shots match frame for frame. In 7 more,
1 to 9 frames match the neighbouring take frame about as well as the expected one (0-3's dark room before the
pillars, the organ hall before the flip, 0-1's frozen room, single frames in the 1-1 hallway, 8-2 street, 2-1 and 2-4).
1-4's flags are its 0.6 s fade into the end card, where the fade decides the match, not the take. No hold or jump.

Assembly 3 min 20 s. Seen in the draft: Gabriel's sword glows with no outline (6-2), 2-1's water reflects the green
ring and the walls in its ripples under the blue absorption tint, and the v6 level titles sit above the subjects.


The user's notes on v5c, on branch `t3/trailer-v5d` from master 30522b5 (with `t3/water-portal-glass` and
`t3/trailer-light-fixes` merged). Output: `bench\results\trailer\trailer-final-v5d\` (`trailer-final-final.mp4`, 2560x1440,
60 fps, 117.84 s, 545 MB; no 1080p copy; `clips\`; `contact-v5c-v5d.jpg`: one frame per shot, v5c | v5d; `timing.txt` and
`timing-strict.txt`: the frame check), in the branch worktree and copied to the main checkout's
`bench\results\trailer\trailer-final-v5d\`. Every shot is a new take on this build, so the merged fixes show: 2-1's water
(static-batched displaced water traced displaced), 8-3's glass seen through the loop portals, the RT-off halves of 8-2's
market and 8-3 (the game's portal views now rendered from the cinematic camera: the island through 8-2's arch), 7-3's
tree with the flashlight held off (`Flickers`, already in the plan for v5c's light fixes).

Changes against v5c:
- **0-3's opening room is 3 bars** (was 5): it lit up on the second bar and then held a nearly still room for 4.5 s.
  Now 1.5 s of the dark room, the pillars on the bar, 3 s lit. The push is quicker too (4.5 m in 7 s, was 3 m in 10 s),
  so the camera is visibly moving while the room is lit; `Render` 8 s (was 10).
- **P-2's street is 5 bars** (was 4) and no longer slows to a stop at the arch: the first-person jog picks up from 6 to
  8 m/s and runs on through the red arch into the lit tunnel (the mouth at z ~122, reached at about 6.3 s; the clip ends
  1 s inside). Scouted with `bench/trailer/scout-v5d.json` (suite `trailer-v5d-scout`: game | path traced, z 102-140);
  a first draft at 6 m/s ended just short of the mouth. The tunnel lamp is held on to 9 s (was 8).
- **1-1's terminal is 3 bars** (was 4): V1 stood still at the terminal for its last 3 s. The battle layer is exactly 32
  bars ("Music" below), so P-2's extra bar had to come from another battle-layer shot.
- Total 1:57.8 (was 2:00.8): the calm layer is 42 bars (to 63.34 s), the battle layer still 32 (to 111.34 s).

The staged events, re-measured on the new takes (`events5c.py`-style: the largest frame-to-frame jump in mean
brightness, and in the red ratio for the organ): 0-3's light pillars start at frame 184 (3.067 s; v5c 182), so `In` is
1.567 (v5c 1.533); the organ hall turns red at frame 641 (10.68 s; same as v5c), `In` stays 6.183.

Render: suites 1-4 in one alt launch (12.5 minutes of game time: 272 + 203 + 175 + 93 s, 0 failures, 36.5 GB of frames,
deleted after the check), assembly 38 minutes in one run. Frame check as for v5c (`timing.txt`, `timing-strict.txt`):
every clip frame maps to its expected take frame, and the master is the 20 clips joined (7070 frames). The flags are
events in the takes: the pillars on clip frame 90 (1.50 s, the bar), the red flip on frame 270 (4.50 s; its brightness
step follows 2 frames later, 643 against 641 in the take), 5-2's lightning at 1.55 s, 2-4's hard switch, P-2 hall's lamps,
the cards' typing. New: 8-3's take ends with the crushers moving in the side rooms (the last 4 frames, from 4.45 s), and
2-1's take has two frames with twice the usual motion (4.95 and 5.0 s), which the strict map reads as off by one; both
are in the takes. The battle layer's step up is on the cut into P-2's street (audio level 2700 -> 8100 at 63.3 s).

Look at: 7-3's tree is much darker without the flashlight (frame mean 21 at 80 % of the clip, v5c 44): the tree's own
red light and the dappled light through its canopy are all there is.

Merge regression before the takes (`merge-regress`, alt, exclusive hold, the lead's request): master 30522b5 against
pre-merge master d0ce90d, both on the alt install back to back (`20261120-133931_merge-regress_alt` against
`20261120-133751_merge-regress_alt`, both copied to the main checkout's `bench\results`; the d0ce90d build came from a
temporary worktree).
No regression: GPU totals within +-0.1 ms, frame times within the run-to-run spread (RT-off rows moved by up to
+0.19 ms too), screenshots the same (mean absolute difference under 1 of 255 except the noisy `pt-direct` and one
darker relight frame in the baseline's 0-1, an outlier against the eight earlier runs). Against the main checkout's last
run (`20261119-234437_merge-regress`, main install) 0-3's relight looked 20 % darker: the main install runs with
`HDR = true`, the alt install without, so main and alt runs cannot be compared image for image.

**Re-taking one shot** (the 6-2 organ take is waiting for the `t3/gabriel-swords` fix: phantom green blades on Gabriel
in the traced image, take frames ~700-900): the plan writes a one-shot suite, the edit list's wildcard picks the newest
take, and only the changed clips are rebuilt:

```powershell
python scripts\trailer-plan.py bench\trailer\final-plan.json --tier final --only "6-2 organ"   # -> bench\suites\trailer-final-final-redo.json
.\scripts\bench.ps1 -Suite trailer-final-final-redo -Slot alt -TimeoutSec 1500                   # inside a visual lock hold
#   re-measure the red flip in the new take (cine_6-2_organ_live, frames 580-720: the jump in R/(G+B));
#   if it moved from frame 641, set the shot's In to (flip frame - 270) / 60 and run trailer-plan.py again
python scripts\trailer.py bench\trailer\trailer-final-final.json --out bench\results\trailer\trailer-final-v5e --only 3
```

`--only 3` rebuilds clip 3 (the organ) and reuses the others from the `--out` folder, so copy `trailer-final-v5d`'s
`clips\` there first (or point `--out` at the v5d folder to replace its master in place).

2026-11-20 (`t3/trailer-v5e-6-2-organ`): the user asked for Gabriel's swords to glow, and then to hold the trailer for
now. The emitter rule is in and the see-through blades are fixed (docs/ultrakill-rendering.md, "Gabriel's swords";
docs/dlss.md, "Emitters in front of the game's depth"); the 6-2 re-take is not done. One take on 6e25dd2 before the rule (outlines off,
confirmed by the log) put the red flip at frame 638, not 641: re-measure it in whatever take is used.

## v5c light fixes (2026-11-20, branch `t3/trailer-light-fixes`)

The user's two lighting questions on v5c. Suite `bench/suites/trailer-light-fixes.json` (paired off / traced takes of the
whole paths at 1280x720, 30 fps; `-Extra -ultrart-log-light-toggles` logs lights switching on and off and the exposure of
every cinematic frame) and `bench/suites/trailer-light-fixes-diag.json` (the 8-2 arch push with debug views, 7-3 with and
without the fix). Sheets in the main checkout's `bench\results\review\trailer-light-fixes\`.

- **7-3 tree: a light switches on half way.** Not a game event and not exposure: the game's own image stays flat
  (game-match -3.12 log2 throughout) while the trace jumped at 5.3 s. It was the game's head-mounted flashlight, which hangs
  10 m behind the camera: shadow rays to it were blocked by the walls and canopy behind the camera until the orbit moved it
  clear. Fixed in the renderer (docs/relight.md, "Player lights end at the eye"); `7-3-tree-eye-clip.jpg` (game / before /
  after at 3.0, 5.0, 5.5, 6.7 s). Then, the user's decision: **no flashlight in this shot**. The plan holds it off for the
  whole take (`"Cinematic": { "Flickers": [ { "Light": "Main Camera/Flashlight", "On": false } ] }`: a steady light named in
  `Flickers` is switched off for the stretch, in both images, and put back after; docs/cinematics.md), so the tree and its
  red light are the only light; `7-3-tree-no-flashlight.jpg` (run `20261120-132839_tmp-tlf73_alt`: frame mean game 19-21,
  traced 19-24, no jump). The trunk stays red without the flashlight.
- **8-2 market aisle: the arch is black with RT off, light with RT on.** The arch is a portal to the island
  (`12 - Island/12 Nonstuff/2/12 -> 12B Entrance`). The game does render it; the black was a bench artifact: the game picked
  its portal views from the frozen player's eye (at the Island checkpoint), not from the cinematic camera, so the arch's
  view was never rendered in the RT-off images. Fixed by re-running the game's portal setup from the posed camera
  (docs/portals.md, section 14). Now both halves show the island through the arch (`8-2-market-arch.jpg`, run
  `20261120-132549_trailer-light-fixes_alt`; the traced view is warmer and its sky darker than the game's). So the RT-off
  half of the v5c 8-2 and 8-3 shots was not the game as played; a re-render is needed for both halves to match. Nothing was
  made darker: the game shows the island there in play too.
- `LookAt "12B - Supermarket Entrance/12B Nonstuff/Entrance"` in the 8-2 market scenarios has never matched
  (`0 matches` in every log since the v5c scout), so `LookActivate` switched nothing on; the supermarket is on at the Island
  checkpoint by itself. Left in the plan (harmless); the claim above that it switches the supermarket on is wrong.

## The final trailer, v5c (2026-11-20, `trailer-final-v5c`)

**User notes on v5c (2026-11-20), fixed on `t3/water-portal-glass`, not re-rendered:** 2-1's water showed 6-2's old
triangle patchwork (static-batched displaced water traced at the rest pose: docs/reflections.md, "Static-batched
displaced water"), and 8-3's Clockwork Crushers lost the glass seen through its sideways loop portals (docs/portals.md,
section 13). Both shots need a re-render to show the fixes (done in v5d).

The user's notes on v5b, on branch `t3/trailer-v5c` with master's glints removal, 6-2 blood flicker fix and 1-4 arena
lamps merged in (ed78e9e). Output: `bench\results\trailer\trailer-final-v5c\` (`trailer-final-final.mp4`, 2560x1440,
60 fps, 120.84 s, 555 MB; no 1080p copy; `clips\`; `contact-v5b-v5c.jpg`: one frame per shot, v5b | v5c; `timing.txt`:
the frame check), in the branch worktree and copied to the main checkout's `bench
the frame check), in the branch worktree and copied to the main checkout's `bench\results\trailer\trailer-final-v5c\`. Every
shot is a new take on this build.

Changes against v5b:
- **No captions** at all. The level titles stay.
- **Out**: 7-3's gunfire shot, 2-2's street, 0-5's lava (10 bars of the battle layer).
- **In**, the same 10 bars: **4-4's Window Hallway** (`final-4-4-window-hall`, 4 bars, after P-2's street: down the
  stairs from the boss gate into the hall, the slats' striped light on the floor and walls), **8-2's supermarket aisle**
  (`final-8-2-market-aisle`, 3 bars, after 6-2's entry: down an aisle of `12B - Supermarket Entrance` towards the arch
  under the fluorescent tubes; traced, the tubes light the shelves and throw their colour across the floor, where the
  game's floor is flat yellow) and **8-3's Clockwork Crushers** (`final-8-3-crushers`, 3 bars, before 2-4's tunnel: a push
  down the glass-walled hall; traced, the glass walls reflect the hall's lights and the sky-blue end of the hall).
  The wipe directions still alternate.
- Glints are gone (master); 6-2's organ shot has the blood fix, 1-4's hall the arena street lamps.

Candidates for the two new shots, besides the 4-4 / 8-4 ones below: `bench/trailer/scout-v5c-b.json` (suite
`trailer-v5c-scout-b`) and `bench/trailer/cand-v5c-b.json` (suite `trailer-v5c-cand-b`): 8-2's supermarket (aisle,
front with the fridges, the Shutters corridor's mannequins) and 8-3 (Clockwork Crushers' glass hall, the Shifting
Hallway's portal, the Split Color Door with the eye mural, Space Streets). Stills in the main checkout's
`bench\results\trailer\v5c-candidates\` (`00-overview-8-2-8-3.jpg`). Both levels need a synthetic checkpoint route
(`bench/recordings/v5c-8-2-island.json`: the Island checkpoint, then `LookActivate` on
`12B - Supermarket Entrance/12B Nonstuff/Entrance` switches the supermarket on; `v5c-8-3-crushers.json`: the Clockwork
Crushers checkpoint, on after the jump).

The staged events moved again: 0-3's light pillars start at frame 182 (3.03 s; v5b 185) and the organ hall turns red at
frame 641 (10.68 s; v5b 642), so `In` is 1.533 and 6.183 (v5b 1.583 and 6.2). The pillars' first frame is also brighter
than in v5b (frame mean 109 against 53) and settles to v5b's level within about 5 frames: the room lighting up in the
take, not an assembly fault.

Render: suites 1-4 in one alt launch (13 minutes, 0 failures, 38 GB of frames in
`bench\results\20261120-013527_trailer-final-final-*_alt`; delete them once the cut is accepted), assembly 32 minutes in one run, no stalls.
Frame check (`timing.txt` as for v5b, and `timing-strict.txt`): every clip frame matched against both of its takes
within +-6 frames (the better-fitting take wins; sweep frames that mix both are only counted) maps to its expected take
frame, and the master equals the 20 clips joined frame for frame (7250 frames). The motion flags are all events in the
takes: the pillars and the red flip, P-2 hall's flickering lamps, 5-2's lightning, 2-4's hard switch, the cards' typing.
Two are new and also in the takes: 8-3's crushers take is almost still for its first 3 frames (50 ms, right after
the cut) before the push starts, and 2-4's take has a one-frame jump at its second frame.

## v5c scouting (2026-11-19, branch `t3/trailer-v5c`)

The user's decisions on v5b: no captions (the level titles stay), the 7-3 gunfire shot out, and candidates for two new
shots: 8-4's hallway and 4-4's hallway before the arena (the slatted window and the water). The final takes wait for
the glints removal and the 6-2 blood / 1-4 arena fixes to merge.

- Scout: `bench/trailer/scout-v5c.json` (suite `trailer-v5c-scout`, 16 keys, game | accumulated path tracing).
  Candidate moves: `bench/trailer/cand-v5c.json`, suite `trailer-v5c-cand`, paired live takes at 1440p with
  full-resolution RR, 6 s at 30 fps. Stills (game | path traced at 15, 50 and 90 % of each move, plus an overview) are
  in the main checkout's `bench\results\trailer\v5c-candidates\`.
- **4-4's "5 - Window Hallway"** (x 107.5-127.5, z 355-475; floor y 648.5; stairs at z 395 up to the boss gate's
  landing at y 662.5). The game paints the window's light as striped beam meshes. Path traced, the slats also throw
  real striped light and shadow across the floor and both walls, and the water channels along the walls reflect it.
  Three moves: down the hall from the north end, down the stairs into the hall, sideways past the window.
- **8-4's twisting hallway** (`3 - Upper Intro/TwistingHallway`, x -28.5, z 67-228, at the Elevator checkpoint): a
  striking corridor, but the game and the traced image look almost the same there, so it could only be a traced-only
  shot. Two moves: in from the elevator, and back towards it.
- **8-4's Hotel Hallway** (`1 - Lower Intro/Hotel Hallway`, x -89.5 to -56.5, z 358-390, floor y 29, on at level
  start: `Load: none`): towards the hall's doors (traced: warm light on the wood and the carpet), and a pan across the
  red skull altar (the game draws the altar as a black silhouette; traced, the skull's light picks it out and throws its
  shadow across the carpet).
- **Loading a room behind a checkpoint without a recorded route**: a two-sample synthetic route whose `Checkpoints`
  entry names the checkpoint (`bench/recordings/v5c-4-4-boss-entrance.json`, `v5c-8-4-elevator.json`) makes
  `Load: checkpoint` jump there. 4-4's Window Hallway lies before the Boss Entrance checkpoint and is off after the
  jump; `"LookAt": "5 - Window Hallway/Floor", "LookActivate": true` switches it on with its lights.
- Traps: the Hotel Hallway's east wall is thicker than its objects' bounds suggest: a camera at x -58 renders black
  in both images (x -59 and -60.5 are inside the room).

## The final trailer, v5b (2026-11-19, `trailer-final-v5b`)

The user's notes on v5, on branch `t3/trailer-v5b` from master 1e9adda (glints on water and wet surfaces, caustics
strength 2). Output: `bench\results\trailer\trailer-final-v5b\` (`trailer-final-final.mp4`, 2560x1440, 60 fps,
120.84 s, 565 MB; no 1080p copy; `clips\`; `contact-v5-v5b.jpg`: one frame per shot, v5 | v5b; `timing.txt`: the
frame check below), in the branch worktree and copied to the main checkout's `bench\results\trailer\trailer-final-v5b\`.
Every shot is a new take on this master.

Changes against v5:
- **Captions**: the big headings are back and the user's lines are their sub lines: 7-3's tree "Emissive lighting" /
  "Glowing objects cast real light", P-2's hall "Global illumination" / "Light bounces realistically, carrying color".
  The others are unchanged (0-1 "Real-time path tracing", 8-2 "Water reflections", P-2 street "Reflective wet
  surfaces", terminal "Ray-traced reflections", gun "Gunfire is a light source").
- **Gabriel's intro is the second shot** (the user: it "should absolutely be closer to the start"), straight after 0-3's
  room and still on the calm layer; his level title comes with it (6-2's entry later has none). No fade out: it cuts to
  1-1's hallway on the bar after the hall turns red. The shots between move one slot down, 6-2's entry moves to the
  battle layer (after the terminal), 2-2 and 0-5 swap their wipe directions so the directions still alternate.
- **1-4's hall closes the trailer** and fades into the end card. It is re-framed (below).
- **The museum shot is a picture gallery** (`final-museum-gallery`, the user: "one of the halls with the paintings looks
  good"): a slow push down the dev-space gallery at z 805 west of the fountain, the yellow-framed portraits on both
  walls, the skylit end wall ahead. The museum has four such galleries (x +-48 to 125, z 600-650 and 790-830); final
  takes of three of them were compared, and the user's pick, the west side, had the better light: frame mean 61-63
  (west z 805) against 56-60 (west z 628, a book on a pedestal passes close) and 52-54 (east z 628, the first pick).
  v5's water hall is out: its floor texture showed through the water surface.
- **End card**: ULTRART only, no small print.
- **The stutters** (6-2's entry, P-2's street, the terminal, 2-2) and **2-4's wrong level title** were `trailer.py`
  bugs, not the takes: see the traps below.

**1-4's stained glass is not a renderer bug.** The glass (`StainedGlassBig` / `StainedGlassBlue`, Master shader with
`_FOG_OFF`, atlas slots) is fullbright in the game: it shows its texture at full brightness whatever lights it. The
traced image does the same (lit weight 0 for unlit Master, docs/materials.md section 11; section 12 has these windows,
whose game colour the traced image matches): crops of the windows in the v5 take's game and traced images have the
same brightness. A lit weight cannot be tried on them with `unlit.extra` (atlas slots), and a `"lit"` rule would grey
the windows against the game's look. Against a hall that tracing lights only
dimly it reads as glowing, and v5's push went straight at it. v5b frames the hall from beside the pool in a slow arc
(`final-1-4-hall`; scouted with `bench/trailer/scout-v5b.json`): the pool's reflections in front, the windows smaller.
The arc's first take had a planter cut into the top-right corner throughout; it now turns 10 degrees further left.

The staged events moved a little in the new takes: 0-3's light pillars start at frame 185 (3.08 s; v5 3.17 s) and the
organ hall turns red at frame 642 (10.70 s; v5 10.80 s), so `In` is 1.583 and 6.2 (v5 1.667 and 6.3).

Render: suites 1-4 in one alt launch (about 12 minutes, 0 failures), the gun shot in the main install (about 1.5
minutes), then two one-shot redo runs (1-4's arc, the museum gallery) and one comparison run of the two west galleries,
about 1.5 minutes each. Assembly: 31 minutes in one run, no stalls. The frames are in the branch worktree's
`bench\results\20261119-21*_trailer-final-final-*` and `20261119-220202_trailer-final-final-redo_alt`; delete them once
the cut is accepted.

Frame check (`timing.txt`): every clip's frame-to-frame difference (holds and jumps against the neighbouring frames), and
each clip mapped frame by frame back to its takes. No frame is dropped or repeated; every flag is an event in the take
(the pillars, the red flip, lightning, gunfire, the hard switches, the P-2 hall's flickering lamps) or the cards' typing.

Where the assembly time goes (2026-11-19, P-2 hall, a 7.5 s sweep, 12-thread CPU, RTX 5070 Ti, ffmpeg 9.0.2):

| Stage | Time |
|---|---|
| Pass 1, picture to FFV1 | 102.9 s, of which PNG decoding is 1.4 s and the format conversions 1.0 s: the rest is the divider's per-pixel `blend` expression over the 1.5 s sweep (about 1.1 s a frame) |
| Pass 2, overlays: filters alone | 5.0 s |
| Pass 2 with libx264 `fast` CRF 8 (now) | 18.5 s, 114 MB, PSNR 51.4 dB / SSIM 0.99497 against lossless |
| Pass 2 with `h264_nvenc` p5 constqp 10 | 5.5 s, 97 MB, 52.1 dB / 0.99499 |
| Pass 2 with `hevc_nvenc` p5 constqp 12 | 5.3 s, 58 MB, 51.1 dB / 0.99396 |
| Master, 30 s of it: libx264 `slow` CRF 13 (now) | 78.5 s, 168 MB, 49.6 dB / 0.99367 |
| Master, 30 s: `h264_nvenc` p7 constqp 14 | 12.9 s, 211 MB, 49.9 dB / 0.99328 |
| Master, 30 s: `hevc_nvenc` p7 constqp 16 | 18.6 s, 129 MB, 48.4 dB / 0.99103 |

NVENC matches x264's quality here at 3 to 6 times the speed, but encoding is the smaller part: on v5b's 31 minutes it would
save about 8 (about 4.5 on the master, 3 to 4 on the clips). The divider is the big one: the sweeps' pass 1 is about 20
of the 31 minutes. A precomputed mask (the divider is a straight line moving on a known curve) and `maskedmerge` would
replace the expression. Not changed yet. Scripts: the session's scratch `encbench.py` / `encq.py` (quality paired by
frame index; scoring by timestamp misaligned the mp4 against the mkv and read about 35 dB for everything).

Traps found:
- **The overlay filter drops frames when an overlay ends.** In ffmpeg 9.0.2 `overlay` with `eof_action=pass` skipped
  about 6 frames of the main picture at the moment an overlay input (a caption's or level title's PNG sequence) ran
  out: a hold, then a jump, a tenth of a second of footage lost. That was every stutter in v5 (at 6-2's level title, at
  the captions' ends on P-2's street, the terminal and 2-2). `trailer.py` now pads every overlay with transparent
  frames to the whole clip (`tpad` before and after, then `trim`), so no overlay ends before the picture. Checked by
  mapping every output frame back to its take frame (`bench\results\trailer\trailer-final-v5b\timing.txt`).
- **Stale graphics frames.** Each overlay's PNG sequence is written to `work\gfxNNN\`, numbered by the order clips are
  built; the folders were not emptied, so a shorter sequence left the rest of an earlier, longer one behind and ffmpeg
  read them too. That put 5-2's level title after 2-4's in v5's tunnel clip. `trailer.py` now empties the folder first.

## The final trailer, fifth render (2026-11-19, `trailer-final-v5`)

The user's shot-by-shot notes on v4, rendered on master 3692116 (torches x1.25, smooth glow sprites, the game's dither
off while traced) on branch `t3/trailer-v5-shot-revisions`. Output: `bench\results\trailer\trailer-final-v5\`
(`trailer-final-final.mp4`, 2560x1440, 60 fps, 120.84 s, 40 Mbit/s, 599 MB; no 1080p copy this time, at the user's request: `trailer.py
--no-exports`; `clips\`; `contact-v4-v5.jpg`: one frame per shot, v4 | v5), in the branch worktree and copied to the main checkout's
`bench\results\trailer\trailer-final-v5\`. The shot list is under "The final trailer" below.

Changes against v4:
- **Cards**: the title is "A RAY TRACING MOD FOR ULTRAKILL" / ULTRART (no "unofficial /// work in progress" line); the
  end card is ULTRART and the small print only.
- **Captions** (v5b put the headings back, with these lines as their sub lines): 0-1 "Real-time path tracing" (it
  shows the room filling with lamp light more than shadows, and "Global illumination" was P-2's); 7-3 tree "Glowing objects cast real light"; P-2 hall "Light bounces realistically,
  carrying color"; 8-2 street "Water reflections"; P-2 street "Reflective wet surfaces"; terminal "Ray-traced
  reflections"; gun "Gunfire is a light source". No sub lines.
- **0-3's opening room opens the trailer** (the user's idea, 2026-11-19): path traced throughout, from the room's back
  wall facing the door. The route (`v5-0-3-before-beams.json`, the user's pose 1 route cut at 7.3 s in the hallway) stops
  before the door; during the take V1 walks in (`Player` path `final-0-3-beams-walk`, `ShowPlayer`), the game switches
  the room on and its light pillars grow from thin lines to full columns while the room fills with light. The pillars
  start 3.17 s into the take; `In` 1.667 puts that on the second bar. (Rendered as a paired capture; the game's image
  was the "before" until the user asked for path tracing the whole time.)
- **0-1 statue room** is a sweep (was a hard switch) and moves to third, after 1-1's hallway.
- **P-2 street and the 1-1 terminal are path traced only** (no comparison). P-2's red arch is lit by the tunnel's
  `Metro Light/Spot Light` (a `Flicker`, red, intensity 25): v4's take lost it to the random flicker, so v5 holds it on
  from 1 s (`Flickers`); it flickers by itself before.
- **7-3 gun: the pump shotgun** (`Shotgun Grenade`), fired on the half bars with the new bench kind `shotgun`
  (`Shotgun.Shoot`; bench/README.md, "Combat effects"). The blast and its pellets light the path and the trees orange
  (late-frame mean 22 against the game's 11). The alt install's slot 2 holds the impact hammer (`Hammer Pump`, a
  different component), hence `WeaponVariant`. The shot is rendered in the main install (`"Slot": "main"`), whose save
  has the user's gun colour presets (`gunColorPreset.N` in `Preferences\Prefs.json`; the alt install has no
  `Prefs.json`).
- **0-5 lava** is a sweep (was a hard switch).
- **New: 6-2 entry** (4 bars, sweep): down the entryway from the start between the windows to the lit arch (path
  `final-6-2-entry`; the scout showed the same red room, the traced one brighter with the arch lit and a sheen on the floor).
- **New: the museum's main hall** (`final-museum-hall`, replaces the fountain): low over the hall's water towards the
  statue's door, the traced water reflecting the arches. The museum's rooms are always on: the fountain's areas load
  and a free camera reach every room (scout `scout-v5.json`: lobby, hall, upper floor, wings, theater, aquarium, garden).
- **New: 1-4's hall inside the building** (`final-1-4-hall`, replaces the night lamps): the user's pose 4 (checkpoint
  load), a push towards the stained glass. The game's hall is nearly black (frame mean 21-24); traced, the lamps light
  the stone, the pool and the planters (46-56).
- **New: Gabriel's intro, the last shot** (`final-6-2-organ`; the user's pose 4, checkpoint load): his own light (the
  rule from 2026-11-18, docs/lighting-overrides.md) lights the organ teal, which the game leaves dark blue; 10.8 s into
  the take the hall turns red as the fight starts. The take is 16 s (`Render`, both images throughout), `In` 6.3 puts
  the red flip on the fourth bar; the camera tilts up with him as he rises. Fades out into the end card.
- **Out**: 1-3's water (2-1's water takes its slot, uncaptioned), 4-4's hall, the museum fountain, 1-4's night lamps.
- **Music**: one more four-bar phrase of the calm layer (44 bars), so the battle layer enters at 66.34 s on P-2's
  street; total 2:00.8 (v4 1:54.8).

Render: scouts (one launch, about 3 minutes: museum 19 keys, 1-4 hall 9, 6-2 entry 11, organ hall time-lapse 25 s, a
shotgun test and the P-2 lamp test; then a 0-3 test), a draft-tier check of the six new shots (one launch), then the
final tier: suites 1-4 in one alt launch (about 13 minutes, 0 failures), the gun shot in the main install (about
1.5 minutes). The frames (43 GB) are in the branch worktree's `bench\results\20261119-19*_trailer-final-final-*`; delete them
once the cut is accepted. Assembly: about 30 minutes of encoding, spread over several runs because of the ffmpeg stalls below.

Traps found:
- **0-3's room is off until the player comes in.** A camera inside it sees black (the room's objects are inactive) until
  the player crosses into it; whether the approach route's end already did that varied between loads when the route
  ended inside the room, so the route now ends in the hallway and the walk does it during the take.
- **Gabriel's red flip** comes 10.8 s after the take starts (the world is held during the preroll), the same in the scout
  and the final run, about 11 s after the checkpoint load's route end.
- A walk path's `Player` positions are applied at path time 0 before the preroll, so V1 stands at its first key while
  the preroll runs.
- **ffmpeg (now 9.0.2, from WinGet) deadlocked** on the 7-3 gun clip (a hard switch) every time, and once on 2-2's
  sweep: 0 % CPU, every thread waiting, the output stuck at a few MB, also with `-filter_complex_threads 1` and with a
  null output. The two renders alone (concat, no overlays) encode in seconds; with the graphics' PNG inputs in the same
  graph it stalls. A wipe clip is now made in two passes: the picture (both renders and the divider) losslessly (FFV1)
  to the work folder, then the overlays on it; and each render is split only into the parts that use it. As a guard,
  `trailer.py` also gives a clip encode `max(600 s, 60 x its length)`, kills a hung one and tries once more. A clip
  file left by a killed run is partial: delete it (or pass its number to `--only`) before running again, since
  existing clips are reused. The same ffmpeg once *finished* a clip early instead (2-2's street: 3.03 s of 4.5, exit
  code 0), so every clip `trailer.py` builds is now checked with ffprobe against its planned length (it stops with the
  clip's name); in the overlay pass a picture a frame or two short holds its last frame (the museum take was 2 frames
  short).

## The final trailer, fourth render (2026-11-19, `trailer-final-v4`)

The v3 cut on master with two shots replaced and the 5-2 strike on cue (branch `t3/trailer-v4-render`). Output:
`bench\results\trailer\trailer-final-v4\` (`trailer-final-final.mp4`, 2560x1440, 60 fps, 114.84 s, 45 Mbit/s, 648 MB;
`trailer-final-final-1080p.mp4`, 241 MB; `clips\`; `weak-shots-v3-v4.png`: one late frame per shot, v3 | v4). The
frames are kept in the branch worktree's `bench\results\2026111*_trailer-final-final-*_alt` (37 GB), to delete once
the cut is accepted.

Builds: run 1 (0-1 statue room, 7-3 tree, 1-1 hallway, 8-2 street) on master bb80589 (film tone curve, fixtures and
door hot lights, HDR-aware exposure, cutout pass, held-skull and grazing-floor fixes, 0-S, Gabriel's light, 8-2's still
pool, portals, level look 3); every other shot, P-2's hall again among them, on master 1c2ec96 (+ `t3/stained-glass`,
which came in while run 1 was rendering).

Changes against v3:
- **2-4 tunnel** (new, 3 bars, hard switch on the bar) replaces 8-2's standing lamp: a 20 m push down the tunnel of
  the user's 2-4 pose 3 towards the arches that RT lights red and the game leaves purple and dark ("looks phenomenally
  good", user 2026-11-18). Path `final-2-4-tunnel` (`Load: checkpoint`).
- **2-1 water** (new, 4 bars, sweep from the right, caption "Reflective water") replaces 6-2's hall: the user's 2-1
  reflection sweep. From 0.8 m above the user's pose (the channel is 3.5 m to its right, the water about 2 m under its
  feet) the camera glides 10 m down the channel and drops to just over the water, turning from the left wall to the
  door, so the walls and the door's green ring end up mirrored in the rippling water; the game's water is a flat
  transparent blue. Path `final-2-1-water`; scouts `bench/trailer/scout-v4.json`, `scout-v4b.json`.
- **5-2 strike on cue**: the 15 s take of run 2 had no strike at all, so v4 calls one (`Calls`, docs/cinematics.md):
  `LightningStrikeDecorative_Animated.FlashStart` on the second-nearest of the sea's three bolts (`Index` 1, the one
  ahead and to the right, in frame; the nearest is out of frame to the left) at path 2.45 s. The flash is 0.3 s after
  the call, 1.53 s into the clip (bar at 1.5 s); mean luma 34 -> 120, back after a second. Take = the clip, 5.7 s.
- **7-3 gun** (the user's v3 note: not too dark): left as rendered, no trailer-only lift. Late frame mean 18 -> 22,
  p95 91 -> 111; at the switch the traced frame is mean 24 against the game's own 13, with the trees along the path
  visible. 7-3 is a dark level, and RT is already brighter than the game there.
- 1-1 terminal: some of the start room's red is back (chroma 36 -> 47; the shaft light, `prefab:firstroomshaft`).
- P-2 hall: the stained-glass take; its puddle reads red, not yellow-green.

One late frame per clip (the traced side; mean and 95th percentile of luma, chroma = mean channel spread; frames
include the captions; the replaced slots compare v3's old shot with the new one):

| Shot | v3 -> v4 | |
|---|---|---|
| 0-1 statue room | mean 45 -> 38, p95 140 -> 113, chroma 59 -> 51 | a little darker highlights (film curve) |
| 7-3 tree | mean 39 -> 44, p95 105 -> 154 | brighter trunk, the red holds |
| P-2 hall | mean 11 -> 15, p95 27 -> 36 | a little brighter |
| 1-1 hallway | mean 98 -> 116, p95 197 -> 233 | brighter torch light, warmer white |
| 8-2 street | mean 77 -> 66, p95 138 -> 121 | the night street a little darker (8-2 `followGame` 0.75), pools and the still pool kept |
| 1-3 water | mean 83 -> 80 | the same |
| 4-4 hall | mean 68 -> 86, p95 199 -> 227, chroma 107 -> 124 | brighter, more colour |
| 5-2 sea (t 1.7 s) | mean 79 -> 111 | in the strike in both; v4's bolt is the same one, called on cue |
| P-2 street | mean 33 -> 22, p95 88 -> 40 | **darker** at this frame: the arch is lit less (the street's lamps flicker at random; the hard switch is unchanged) |
| 1-1 terminal | mean 38 -> 41, chroma 36 -> 47 | more red behind the terminal |
| 2-2 street | mean 39 -> 44 | a little brighter |
| 7-3 gun | mean 18 -> 22, p95 91 -> 111 | brighter (above) |
| 0-5 lava, museum fountain, 1-4 lamps | within 1 | the same |
| 8-2 lamp -> 2-4 tunnel | - | new shot, mean 30, chroma 60 |
| 6-2 hall -> 2-1 water | - | new shot, mean 39 |

Render (alt slot, the lock released between runs): scouts 82 s, about 80 s and about 60 s; a test of the two new
shots 86 s; final runs 263 s (run 1), about 200 s (run 2), 185 s (run 3), 155 s (run 4 + the P-2 hall / 5-2 redo),
then three 5-2 takes of about 45 s each to get the strike (`DistantThunder` and `LightningStrikeDecorative`: none in
5-2; then the nearest bolt, out of frame; then `Index` 1). Under 20 minutes of game time in all, 0 exceptions.
Assembly 29.9 minutes, the 5-2 clip and the join again 9.2 minutes (`trailer.py --only 9`).

**The user on v4** (2026-11-19): the 1-1 torch hall and 4-4 hall are "wildly bright on the lights"; the street lamps
(P-2, 1-4) have a halo that "looks like a sprite". Both were the normal render (the shots are `live` RR takes), fixed
there on `t3/trailer-v4-light-halo-fix`: torches x1.25 instead of x2.5 (docs/lighting-overrides.md, "Torches x1.25":
1-1 hall clip 14 % -> 9 %, 4-4 hall 9 % -> 4 %), and the handed-over lamp glass keeps its class's air share in the
haze (docs/volumetrics.md, "Handed-over lamps in the air": the halo about half). Not re-rendered yet.

Scouted and left out: 6-2's Grand Hall at the user's pose 1 with `t3/stained-glass` (`scout-v4c.json`): the windows
show their lead pattern, but no window beam read on the floor in 14 views, and the hall is the same all-red look as the
6-2 shot that went out. 0-S's skull corridor was not tried: the held skull needs a cinematic take with a held item
(`Shots` with `holditem` and `ShowWeapon` might do it), and the 2-4 tunnel showed this week's work better.

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
| `Scenario` | fields for every bench scenario (`"Enemies": "blind", "EnemyOutlines": false`: since 2026-11-20 every shot has the game's enemy outlines off, in the game's image and the traced one; docs/ultrakill-rendering.md, "Enemy outlines") |
| `Labels`, `Graphics`, `Music` | passed to the edit list |

Shots: `Kind` is `title` / `end` (a card: `Card` holds the title clip's fields; a `title` can also stand in the middle of the
trailer as a placeholder), `video` (a video file, e.g. a gameplay recording: `Source` (a path, absolute or under bench/results),
`In`, optional `Speed`, and `Caption`, `Tag`, `TagAt`, `Labels`, `FadeIn`, `FadeOut`, `Transition` passed to the edit list's
`shot` clip; no bench scenario is written; a `shot` with a `Source` and no `File` is the same; `"Labels": null` drops the
state badge), `beauty` (one traced take), `switch`
(the game's image, then a hard switch to the traced one after `Off` seconds) or `sweep` (the divider crosses once,
starting after `Off` seconds and taking `Sweep`; `Slant`, `Flip` as in a wipe). `Bars` (+ `Lead` / `Seconds`) is the
length; `File` the path in bench/cinematics; `Path` and `Scenario` go to the bench scenario, `Cinematic` to its
cinematic block, `TierCinematic` the same for one tier; `ModeAdd` appends runtime variants to the tier's `Modes` for one
shot; `In` starts the clip that far into the take and `Render` renders a longer take than the clip (a shot whose best
moment is picked afterwards; `TierIn` gives `In` per tier; 5-2's lightning is now called on cue instead, `Calls` in docs/cinematics.md); `TimeScale` 0 freezes the world for one shot; `Caption` and `Tag`
as in the edit list. With `Render`, a comparison shot can set `"Cinematic": { "OnFrom": 0, "OffUntil": <Render> }` so both
images cover the whole take and `In` can still move after the render (v5's 0-3 and organ shots). `"Slot": "main"` renders
the shot in the main install (the user's save, so the user's gun colours) in a run of its own: suite
`trailer-final-<tier>-main`, sources `*_trailer-final-<tier>-main*/...` (a main-slot results folder has no `_alt` suffix).

A placeholder for footage that comes later and its swap are one line each in the plan, at the same bars:
`{ "Kind": "title", "Name": "combat", "Bars": 4, "Card": { "Text": "COMBAT FOOTAGE SLOT", "AccentFrom": 99 } }` becomes
`{ "Kind": "video", "Name": "combat", "Bars": 4, "Source": "D:/recordings/combat.mp4", "In": 12.0 }`.

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
| Level title | canvas 720 units high; the layer line (`PRELUDE /// FIRST`) 26 units, centred 245 above the middle; the name (`INTO THE FIRE`) 46 units, 210 above; typed at 100 letters a second, half a second between the lines, 3 s hold, 1 s fade | `"Tag": ["PRELUDE /// FIRST", "INTO THE FIRE"]`: the same timing and the game's 35-unit line gap at 0.85 of the size, with the level's real strings (read from each level's MapInfo). Shown the first time a level appears. Since v6 (2026-11-20) higher than the game has it: the layer line at 0.06 of the height, level with the state badge's middle, the name at 0.11 (v1-v5d: 0.16 and 0.21, the game's place, which put the name over the subject of a centred shot: Gabriel's wings in 6-2, the tree's trunk in 7-3, 0-3's doorway). On a soft dark edge (`legible`: a tight feathered outline plus a wide faint shade that follows the words, no box) and a darker drop shadow (0.9, was 0.59), so the white type keeps an edge on 0-3's sunlit ceiling, 4-4's slats and 8-3's white ceiling. |
| Feature caption | HUD message: a rounded black box at 59 % opacity, low in the middle, that snaps open from 5 % (width first, then height), text typed with a block cursor | `Caption`: the same box and motion; heading in capitals, an optional sub line under it |
| State badge | - | top left, the same box: `RT OFF` grey with a hollow marker, `PATH TRACED` white with an accent marker. Since v6 (2026-11-20) it is open from a clip's first frame to its last, so a cut swaps it in place; it snaps open (HudOpenEffect) and flashes only when the state changes mid-clip (at the switch, or when the divider passes it). v1-v5d snapped every clip's first badge open from an empty box (0.16 s) and faded every badge out over the last 0.15 s before the cut. A switch or wipe whose switch comes in the first 0.3 s shows the after badge for the whole clip. |
| Cards | - | on black, laid out as a level title: sub line typed first, then `ULTRART` under it in large type ("RT" in the accent colour), further lines (`Lines`, e.g. a tagline) typed, small print (`Small`, a list of lines at 0.85 of the height and down) faded in. `Beats`: when the sub line, the title and the first further line start. Since v6 the first further line has a gap of 2.5 % of the height under the title (it touched the title's baseline), and a title wider than 90 % of the picture is set smaller to fit (a placeholder such as `COMBAT FOOTAGE SLOT`). |
| Wipe divider | - | the white line (`Line`, 2 px at 720p) with a dark edge either side since v6 (`Border` 1.5 px at 720p, the picture at `Shade` 0.3 of its brightness), so it stays visible over 4-4's sunlit hall and the museum's white walls. Pixels away from the line cost what they did before (the expression tests the distance first): 2026-11-20, 1440p at 30 fps with overlays, a 1.1 s museum sweep took 51 s (53 s without the border); clip times vary by about 10 s from run to run. |

No logo and no art of the game is used; the title card says "a ray tracing mod for ULTRAKILL" (v1-v4: "a path tracing
mod" and "unofficial /// work in progress"), and the end card's small print says how the footage was captured and that
the mod is unofficial and not affiliated with the game's developers.

### Music

0-1's theme, both layers from the game's files, 160 bpm, bars at 0.34 + 1.5 n seconds. The calm layer (`0-1 Clean`)
plays from its start under the title and the first nine shots (42 bars, to 63.34 s; v5-v5c: 44 bars, to 66.34 s; v4: 40
bars and eight shots, to 60.34 s); the battle layer (`0-1`) comes in there from its step up at 36.34 s of the song and
runs 32 bars to its loop point (111.34 s of the trailer; v5c 114.34); the calm layer returns under the end card and fades
over 4.5 s. The battle layer is always exactly 32 bars (step up on the cut into P-2's street, since the v6 draft into the combat footage slot before it; loop point on the end
card), so a battle-layer shot can only grow by taking bars from another one; the calm layer can be any length (the
plan's `Music.Tracks` `At` / `Until` move with it). Every cut, switch and
gunshot (half bars) is on the grid, and so are v5's two staged events: 0-3's light pillars come on and 6-2's organ hall
turns red on a bar. Since v5b both are in the first 20 seconds, on the calm layer (the organ shot ends on the bar after
the red flip); 1-4's hall closes the battle layer.

### Shot list

The cut as of v5d (2026-11-20; "The final trailer, v5d" above has what changed). The v6 draft changes the battle half: "Trailer v6 draft" above.

All comparisons are paired live takes (the game's image / real-time path tracing with RR); "beauty" is one traced take.
No captions since v5c; level titles stay.

| # | Start | s | Shot | Level | Treatment |
|---|---|---|---|---|---|
| 1 | 0:00.0 | 4.84 | Title card: "A RAY TRACING MOD FOR ULTRAKILL" / ULTRART | - | card |
| 2 | 0:04.8 | 4.5 | Opening room from its back wall, a slow push; the room and its light pillars come on (on the second bar) | 0-3 | beauty |
| 3 | 0:09.3 | 6.0 | Gabriel's intro: his light on the organ, then the hall turns red on the fourth bar | 6-2 | sweep |
| 4 | 0:15.3 | 9.0 | Torch hallway, round the corner to the doorway | 1-1 | sweep from the right |
| 5 | 0:24.3 | 6.0 | Statue room glide (world frozen) | 0-1 | sweep |
| 6 | 0:30.3 | 7.5 | Tree orbit (the game's flashlight held off) | 7-3 | sweep from the right |
| 7 | 0:37.8 | 7.5 | Hall crane from the stained glass | P-2 | sweep |
| 8 | 0:45.3 | 7.5 | Street, lamp posts and the pool | 8-2 | sweep from the right |
| 9 | 0:52.8 | 6.0 | Glide down onto the water channel towards the door (the user's pose) | 2-1 | sweep |
| 10 | 0:58.8 | 4.5 | Storm sea, a lightning strike on the second bar (`Calls`) | 5-2 | beauty |
| 11 | 1:03.3 | 7.5 | First-person jog down the street, through the red arch into the lit tunnel (`Player` camera; the tunnel lamp held on from 1 s) | P-2 | beauty |
| 12 | 1:10.8 | 6.0 | Down the stairs into the Window Hallway, the slats' striped light | 4-4 | sweep |
| 13 | 1:16.8 | 4.5 | V1 walks up to the shop terminal (`Player` path, `ShopScreensOff`) | 1-1 | beauty |
| 14 | 1:21.3 | 6.0 | Down the entryway between the windows to the lit arch | 6-2 | sweep from the right |
| 15 | 1:27.3 | 4.5 | Down a supermarket aisle towards the arch (the island through it) | 8-2 | sweep |
| 16 | 1:31.8 | 4.5 | Push down the Clockwork Crushers' glass hall | 8-3 | sweep from the right |
| 17 | 1:36.3 | 4.5 | Push down the tunnel towards the red-lit arches (the user's pose 3) | 2-4 | switch at 1.5 s |
| 18 | 1:40.8 | 4.5 | Push down a picture gallery | museum | sweep from the right |
| 19 | 1:45.3 | 6.0 | Slow arc beside the pool of the lamp-lit hall inside the building (the user's pose 4) | 1-4 | sweep, fade out |
| 20 | 1:51.3 | 6.5 | End card: ULTRART | - | card |

Total 1:57.8 (v5-v5c 2:00.8). Where gameplay footage (the user's own capture) could go: at the battle layer's entry (1:03.3, before or
instead of P-2's street) or in the battle layer's middle (after the terminal, 1:21.3), in place of battle-layer bars (the
layer is 32 bars, "Music" above); a clip of whole bars keeps the grid.

8-2's street was first captioned "Volumetric light": the game draws its lamp cones as bold polygons and the traced
cones are soft and faint, so the comparison did not carry that caption. What the traced image adds there is the lamps'
light on the ground and the pool's reflections.

Tried and left out: 6-1's candle hall and 8-2's vending machine (the bench's area loads land in a void
there), a replay of the recorded 0-2 run (no gunfire, and it leaves the level after 20 s), 1-1's fountain (the water is
too small in frame to read), 7-1's pool (the pose loaded without its level state: grey and unlit), layer 8's mirrors and portals, 3-1.
Out since v4: 8-2's standing lamp and 6-2's first hall (both kept in `paths.json`: `final-8-2-lamp`, `final-6-2-nave`).
Out since v5: 1-3's water, 4-4's hall, the museum fountain, 1-4's night lamps (kept in `paths.json`). Out since v5b:
the museum's main hall (its floor texture showed through the water; path removed).
Scouted for v4 and left out: 6-2's Grand Hall at the user's pose 1 after `t3/stained-glass` (the windows show their
lead pattern, but no beam on the floor read from any of 14 views, and the hall is the same all-red look as the 6-2 shot
that went out), 0-S's skull-lit corridor (a held item needs the skull in a cinematic take; the 2-4 tunnel showed the
same week's work better).

Known problems in the footage, left as they are (renderer bugs are not fixed on this branch):
- V1's body reads grey in 1-1's orange room when path traced (blue in the game's image and in its own reflection).
- P-2's puddles have a yellow-green tint (v1-v3; in v4's P-2 hall take, on the stained-glass build, the puddle reads red).
- The alt install has no custom gun colours (no `Prefs.json`), so V1's gun in the terminal shot is in the default colours;
  v5's gun shot is rendered in the main install for the user's.
- P-2's long shadow from the street lamp is faint: the steady lamp post beside it lights the same ground.
- 5-2's lightning is random. Up to v3 the clip's `In` was set to a strike of the take in use (`TierIn`, a 15 s take);
  v4's 15 s take had no strike at all. Since v4 the strike is called on cue (`Calls`: `FlashStart` of the
  second-nearest `LightningStrikeDecorative_Animated`, the bolt in frame, at 2.45 s of the path; the flash comes 0.3 s
  later, 1.53 s into the clip, mean luma 34 -> 120, back after a second), and the take is the clip's length.
- The game's image of a gunshot shows the gun one animation frame later than the traced image of the same frame.
