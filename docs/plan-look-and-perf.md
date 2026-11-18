# Plan: look and performance (2026-11-17)

The plan of phase 1 of the owner's brief (written with no code changes, on master `8ceda9b`), approved by the user on
2026-11-17 and merged at `b028469`. Implementation is under way: one agent, one branch at a time, in the order of
section 7; the [status](#status) table says where each step is. Measured on the RTX 5070 Ti at 2560x1440.

Decisions the user made while this was written, already folded in:
- **No frame generation** of any kind (DLSS-G, Smooth Motion, interpolation): 120 / 240 Hz mean rendered frames.
- **`pathtrace` with DLSS RR is the default mode** to plan for (today the default is `relight`). Look and perf work
  target pathtrace first, relight second.
- **Window light beams** (6-2 and other levels) are still the game's raster meshes and should become physical.
- Raw recording playback does not load areas reliably: evidence below uses poses reached through an approach route,
  and every frame was looked at. Frames from unloaded areas are excluded and named.

Principles the user added with the approval (section 8 has the answers to the questions):
- **Emissive over lights.** "If something can be emissive instead of using a light, it should." Fixtures, lamps,
  screens and glowing props light the scene through their emissive surfaces (with emissive masks where the art needs
  them), and their fake point / spot lights go. A light stays only where nothing emissive can stand in for it; what
  remains is classified as fills.
- **Compare, don't sweep.** The roughly 6,400 floating lights are not deleted in one pass. Every rule is reviewable:
  A/B sheets per level or room (game / current / proposed) driven by the scoreboard, classes and single lights that
  can be toggled in game and in bench variants, and a diff of what each rule changes per level. Levels roll out one
  by one, the taste-sensitive ones after the user has seen their sheets.

Contents: [Status](#status) · [1 Findings](#1-findings) · [2 Lights and brightness](#2-workstream-a-lights-and-brightness) ·
[3 Materials](#3-workstream-b-generated-material-maps) · [4 Trailer flaws](#4-workstream-c-flaws-from-the-trailer) ·
[5 Performance](#5-workstream-d-performance) · [6 Refactoring](#6-workstream-e-refactoring) ·
[7 Order and branches](#7-order-branches-parallel-work) · [8 Decisions](#8-decisions-for-the-user) · [9 Effort](#9-effort)

## Status

Updated as branches land. "ready" = reported to the lead for merge-regress and merging.

| Step | Branch | Status |
|---|---|---|
| A1 scoreboard (`tools/lights/score.py`: mean, dark-region delta, revealed share, sheets) | `t3/bright-metrics` | ready 2026-11-17 (docs/lighting-overrides.md, "Scoreboard") |
| A2 pose coverage: tour of the game's Teleport Menu entries (`Path.Type` "tour"), tour suites for 43 levels | `t3/bright-metrics` | ready 2026-11-17; run on 1-4, 6-2, 3-1 so far (bench/README.md, "Tour") |
| A2 per-light attribution report | `t3/light-classes` | ready 2026-11-17 as class and light switches scored per mode (docs/lighting-overrides.md, "Review") |
| A3 exposure policy and the "RT brightness" setting | `t3/bright-metrics` | ready 2026-11-17: pathtrace median x1.53 -> x1.14 of the game over nine poses; P-2 kept as it was by a level setting, for the user to confirm (docs/relight.md, "Follow the game") |
| 5.1 profiling pass (frame slot, render-thread and main-thread ms per pass, ETW traces, hitch attribution, `-Floor`); E3 default mode pathtrace | `t3/perf-profile` | ready 2026-11-17 (docs/performance.md, "Profiling") |
| 5.1 step 5 ray counters; E1 pass scheduler | `t3/perf-dispatch` | planned (P1 was judged on dispatch ms with `footprint` 0 / `bounces=1` as its bounds, so the counters wait for R2) |
| P1, P2 footprint-terminated paths; R1 ray generation per mode | `t3/perf-paths` | ready 2026-11-17: P1 + P2 in (one rule, default 1 cell: 0-1 metal 145 -> 176 fps; docs/path-tracing.md, "Footprint"); R1 measured at 0.02-0.04 ms and dropped (docs/performance.md, "Tried: a ray generation per mode") |
| A4 light classes, flags, prefab layer, class and light toggles, rule diffs | `t3/light-classes` | ready 2026-11-17: a class for every light from rules (`data/lights/_rules.json`), policies per class and level, `reach` / air-only / air strength per light, class switches in the editor and as bench variants, proposal layer, census and rule diff, offline census of all levels. No policy ships yet; the start room's (lights x0.5) is proposed with its sheet. Moved to `t3/light-levels`: the fit v2, per-surface emission scale (emissive first), policies per level (docs/lighting-overrides.md, "Classes and rules", "Review") |
| A6 emitter rules (emissive first), levels | `t3/light-levels` | in review 2026-11-17: emitter rules in (a glowing surface's light scaled per material); proposals for the start and final room, 1-4, 3-1, 6-2 and 0-1 in `data/lights/proposed/` with review sheets (docs/lighting-overrides.md, "Levels"); nothing ships until the user has seen the sheets. First board of all 43 levels (`tools/lights/board.py`) |
| A6b emissive fixtures: fixture census, emitter masks (`glow` rules), first proposals | `t3/emissive-masks` | ready 2026-11-17: the census names each fixture's glowing part (`tools/lights/fixtures.py`); emitter rules with `glow` / `threshold` turn an unlit or lit surface into an emitter (`SceneProxies.Glow.cs`); proved on 8-2's street lamps (scale), 8-2's standing lamp and 1-4's street lamps (mask). Proposals for 8-2 and 1-4 in `data/lights/proposed/`, review R9; per-level application rides with each level's light proposal (docs/lighting-overrides.md, "Emissive fixtures") |
| A6c light proposals, second batch (4-3, 5-3, 7-3, 0-3, 8-2, 5-1) | `t3/light-levels-2` | ready 2026-11-17 as proposals (review R10): attribution per class, strengths, review sheets; nothing ships. Next batch: P-1, P-2, 2-4 and down the board |
| Ship R8-R10; tour stops in the void | `t3/ship-r9-r10` | ready 2026-11-17: emissive fixtures (8-2, 1-4), the six second-batch levels and soft puddles on by default; emitter rules can name objects (`path`: 1-4's statue orbs); a tour stop with no level under it replays the level's own start (0-1) |
| A6d light proposals, third batch (P-1, P-2, 2-4, 8-4, 1-3, 7-2, 4-4, 3-2); B2 bake-off; R11 second try | `t3/light-levels-3` | ready 2026-11-17 as proposals (review R13); bake-off done: DeepBump gives no visible gain over PBRify with smoothing 1, no switch (R12, docs/materials.md section 10); R11 did not reproduce at the trailer's pose either. Next: 0-S's prefab light, the player's light as one global question, 8-3 and the rest of the board |
| A7 punch: the reworked target (highlights, colour, contrast) | `t3/light-punch` | ready 2026-11-18: `tools/lights/punch.py`; shipped on 0-3, 0-4, 2-2 (fills at full with reach capped, fixtures x1.8 with haze held) with the exposure (0.4 beyond 0.35 EV) and the shadow toe (0.6) as defaults. 0-3's lava-pillar void haze listed (older than this) |
| A8 punch on every level | `t3/light-punch-all` | ready 2026-11-18: the policy is the global class policy; 43 levels toured (sheets and index in `bench/results/review/punch-all/`); exceptions 0-3 (fixture haze), 4-3 and 5-3 (x1.2), 7-1 and 7-3 (level `followGame` exposure), 8-2 (strengths of 11-17 kept), 7-2, P-2. Open: 0-E far above the game in the dark (older), 7-4 / 4-S tour views show no ray tracing |
| Trailer, second render | `t3/trailer-v2` | rendered 2026-11-18 on the punch build: `bench/results/trailer/trailer-final-v2/` (same cut, 114.84 s). Five shots weaker than the first master, with reasons (review R17) |
| 8-2 street lamps: lights back on beside the emissive heads | `t3/lamp-cones` | ready 2026-11-18: lights x1.3, air x0.5, heads x2: the pool and the cone are back (trailer shot "8-2 street" re-rendered into `trailer-final-v2`). Tour dark delta +15.1 -> +20.4 |
| Fixtures x2.5 | `t3/punch-25` | ready 2026-11-18: global fixture group x2.5, `medium` 0.4; 42 levels toured x1.8 against x2.5 (sheets and index in `bench/results/review/punch-25/`). 5-1 stays at x1.8 (white corridors blow out); 4-3, 5-3, 7-1, 8-2 keep their strengths; 0-3 `medium` 0.11 |
| 7-3 brighter | `t3/dark-7-3` | ready 2026-11-18: player light x4 (its pool was a third of the game's), `followGame` 0.75, the blood trees' lights at full reach; trailer poses at or above the first master (tree mean 36 against 28, gun p95 107 against 97); tour dark +10.1 -> +16.1. P-2 and 7-2 player lights back at x1 |
| Emissive over lights, fixtures | `t3/emissive-all` | fixtures part built 2026-11-18: 16 global handover rules; of 3,011 fixture lights 853 handed to their glowing part, 58 hybrid (8-3's hooded street lamps), 8 by 8-2's older rule, 986 flames (next: flame-driven lights), 1,106 kept (657 no part, 243 beam only, 166 with a part but scripted or under a level rule, 40 in the air). 43 levels toured; 8-1 and 1-4 move, the rest hold (docs/lighting-overrides.md, "Handover"). Flame-driven lights on by default (sphere light at the flame, gain 0.5; torch stops p95 x1.20 -> x1.08, the 1-1 wing shadows gone); the literal billboard version tested and rejected (docs/emissive-lights.md). Enemy glows: drone eye (emitter rule with a chroma mask, in the triangle table) and Swordsmachine blade (a bone-following sphere light, `Plugin.EnemyGlow.cs`); filth, stray, schism, soldier and Malicious Face have no glowing part; cerberus, Mindflayer, virtue, idol keep the game's light; 0-2 combat +0.13 ms per frame (docs/emissive-lights.md, "Enemy glows"). **Ready 2026-11-18** |
| B1 in-engine matrix, B2 generator bake-off | offline + `t3/materials-v2`, `t3/light-levels-3` | B2 done 2026-11-17 (DeepBump: no switch; PBRFusion not run, gated). B1 done 2026-11-17: the fragments are the maps' texel-to-texel disagreement, not RR's resolution; a 3x3 low-pass of the normals at load removes them (`-ultrart-materials-smooth`, default 1 since the user's pick on 2026-11-17; docs/materials.md, section 9). B5's footprint fade is not the fix. B2 not started: DeepBump can run offline, PBRFusion's page needs a sign-in |
| C1-C5, R2, R3 dispatch and CPU floor | `t3/perf-dispatch` | ready 2026-11-17 with a negative result: C1 (cache update every second frame, `-ultrart-aux-every 2`) gains 0.1 ms and stays off; with the particle volume scheduled too it gained 0.3-0.5 ms but smoke got noisier, so that part was removed. C2 / C3 do not fit Unity's eight writable targets per ray tracing shader (the main one has 7). Left: C4 (fewer hit-group records), C5, R2, R3 (docs/performance.md, "Pass schedule", "One dispatch") |
| A6 level roll-out (per-level files and review sheets) | `t3/light-levels`, then `t3/perf-dispatch` | start/final room, 1-4, 3-1, 6-2, 0-1 **shipped 2026-11-17** (user: the sheets "look great"); 5 of 43 proposed 2026-11-17; the rest in board order once the first proposals are accepted. Found on the way: 3-1 Stairways' haze is far weaker than the game's (for `t3/window-beams`) |
| A5 window beams | `t3/window-beams` | first step ready 2026-11-18, **off by default** (`shaft.windowBeams`): beam meshes without a light get a fitted source and traced shadows through the beam channel (10-61 per level fit). Off because the first frames lose the beams' glow more than they gain shafts (docs/volumetrics.md, "Window beams"): needs the window's own light, a stop surface along the wall, and poses that look along a beam |
| B3-B5 new maps (AO, emissive masks, metal), review workflow, RR interplay | `t3/materials-v2` | planned |
| C trailer flaws; hitches; 8-x floor of 60 fps | `t3/trailer-fixes` | planned |

## 1. Findings

Runs of this phase (in this worktree's `bench/results/`, gitignored; suites `bench/suites/plan-*.json` are committed):

| Run | Kind | What |
|---|---|---|
| `20261117-130451_plan-perf` | exclusive, `-Perf` | off / relight / pathtrace interleaved: two static poses, two runs, 8-3 |
| `20261117-131506_plan-perf-floor` | exclusive, `-Perf -Scale 0.2` | relight with almost no GPU work (the CPU floor), one pass switched off at a time; pathtrace cost knobs |
| `20261117-131649_plan-perf-notimer` | exclusive, `-Perf -Scale 0.2 -NoGpuTimer` | the same floor without the GPU timer |
| `20261117-130849_plan-bright_alt` | visual, alt | game against pathtrace and relight at the places named in the brief, with one-setting variants |
| `20261117-130849_plan-materials_alt` | visual, alt | per-texel maps on / off |
| `plan-look-and-perf/` | derived | side-by-side sheets (`v_*.jpg`, `c_*.jpg`, `m_*.jpg`), `bright-sheet.md`, the light census (`census.json`, `census.py`), the dark-region script (`reveal.py`), two trailer frames |

Also used: the lead's merge-regress of today (`20261117-122416_merge-regress`, main checkout) and the final trailer's
clips.

### 1.1 Brightness has three separate causes

Screen mean luma against the game (HUD and viewmodel masked, `tools/lights/sheet.py`), and the mean luma of the pixels
the game leaves dark (game luma <= 8 of 255; `reveal.py`). "strict" is pathtrace with
`ae.gameMatchDown=1 + ae.gameMatchBand=0.05 + ae.upStrength=0`: auto exposure follows the game's brightness fully and
never lifts a dark scene.

| Pose | game mean | pathtrace | relight | strict | dark pixels: game → pathtrace (other) |
|---|---|---|---|---|---|
| Start room of every level (`FirstRoom`, seen from 3-1's spawn) | 41.0 | x1.56 | x1.69 | x1.26 | 5.9 → 26.9 |
| 0-1 first room after the fall | 23.7 | x1.51 | x1.70 | x1.02 | 4.4 → 15.5 (strict 10.5) |
| 0-1 metal room (fitted on 2026-11-15) | 51.1 | x1.02 | x0.91 | x1.02 | |
| 6-2 hall ("6-2 a") | 10.4 | x1.77 | x1.82 | x1.26 | 3.4 → 9.1 (strict 5.8, haze off 8.5) |
| 1-4 lamps | 21.3 | x0.96 | x0.96 | x0.91 | 3.5 → 6.8 (haze off 4.1, sky off 6.9) |
| 1-4 bridge ("drift a") | 30.8 | x1.22 | | x1.05 | 3.1 → 9.3 (haze off 3.4) |
| 3-1 eye beam room | 12.9 | x1.03 | x1.02 | x0.98 | 0.0 → 1.4 |
| P-2 opening street | 26.8 | x0.84 | x0.80 | x0.68 | 6.7 → 13.5 |

Sheets: `plan-look-and-perf/v_14.jpg` (1-4 lamps: game, pathtrace, pathtrace without haze), `v_14d.jpg`, `v_62.jpg`,
`v_31.jpg`, `v_p2.jpg`. Excluded: the 0-1 route frames at 5.0, 10.5 and 12.5 s (an unloaded shaft).

What the numbers say:

1. **Exposure policy** is most of the *mean* excess. Auto exposure lifts dark scenes on purpose (`upStrength` 0.5
   below the dark key) and only removes 40 % of a brightness excess over the game, beyond a 0.35 EV dead band
   (`gameMatchDown`, `gameMatchBand`; docs/relight.md "Game match": "lit rooms may stay brighter than the game").
   With the strict variant the 0-1 room goes from x1.51 to x1.02, the 6-2 hall from x1.77 to x1.26, the start room
   from x1.56 to x1.26. That is a setting, not a light problem, and it cannot fix what follows.
2. **Distribution, not mean.** 1-4 lamps has the *same* mean as the game (x0.96) and still shows the far wall, the
   door, the trees and the bushes that the game leaves as black silhouettes (`v_14.jpg`). The mean ratio that every
   earlier audit used cannot see the user's complaint. In the dark pixels two things add up: our haze is brighter
   than the game's night-blue fog (dark-pixel mean 6.8, 4.1 with the haze off, game 3.5; on the bridge 9.3 → 3.4), and
   with the haze off the far surfaces are plainly lit. The game's falloff is down to 4 % at a light's range; relight's
   inverse-square curve, from the formulas in docs/relight.md "Light reference distance", is about 2.7 times the
   game's there in display terms (capped at 3x, windowed to twice the range). Which lights do it at this pose is not
   attributed yet (A2). The sky is not the cause (`_skyScale=0` changes nothing).
3. **Lights nobody has looked at.** An offline census of every `Light` in the 43 campaign level bundles (UnityPy,
   `census.py`): 13,157 lights; 6,399 point or spot lights have no lamp, torch, screen, enemy or pickup word anywhere
   in their hierarchy path ("floating"), 3,823 of those with a range of 12 m or more, and none of them casts shadows
   in the game. The game's convention is visible in the paths: a `Lighting` or `Lights` group under a room's
   `Nonstuff` object holds hand-placed lights with no fixture (`1 - Entryway/Lighting/Point Light`, range 35,
   intensity 30, pure red, is 6-2's fog fill; `3 - Gun Room/Spot Light`, range 20, intensity 10, white, is 0-1's gun
   accent; P-2's `6 - Soul Tunnel/.../Lighting/Point Light` x6, range 80, intensity 15). Seven levels have an override
   file (`data/lights`), and in those the classifier's `fill` rule (range >= 12, intensity <= 1.2, 1 m from walls,
   not scripted) only *dims* to x0.5 or x0.7 and leaves 200-270 lights per level in the unexamined `point` class.
   Prefab lights repeat across every level: `FirstRoom/Room/Point Light` (range 25, intensity 8), and by name count
   about 150 checkpoint lights, 103 at final doors, 92 at shops, 66 door lights. The start room is x1.56 at 3-1's
   spawn and is the same prefab in every level; it has no rule, because overrides are per scene.

The 2026-11-15 audit (docs/audit-2026-11-15.md) agrees on the levels: 0-2 x1.4-1.7 in every pose, 4-4 x1.68, 7-3
x1.6-2.4, 8-2 night side x1.79, 6-2 x1.61.

### 1.2 Material maps

- Native-resolution crops, game / pathtrace without detail / pathtrace with detail: `c_11wall.jpg` (1-1 torch-lit
  hall), `c_82wall.jpg` (8-2 facade), `m_11a.jpg`, `m_first.jpg` (the start room).
- In the 1-1 hall the detail normals put irregular light and dark fragments *inside* bricks, not aligned to the texel
  grid. Two things combine: PBRify treats every texel as a feature with a photo prior (each texel gets its own tilt),
  and pathtrace traces at 1/3 scale (853x480), so a texel covers one to three traced pixels and RR reconstructs soft
  blotches from per-texel facets it cannot resolve. NormalStrength 1 halves it, it does not remove it.
- Without detail, pathtrace's texel edges are already softer than the game's in that crop. Worth a separate check
  (remodulation at grazing angles), noted in 4.
- The 8-2 facade at 20 m shows almost no difference on / off (the ray-cone LOD does its job).
- I could not identify "the 8-2 intro room floor" and "the 0-1 starting room's exit door" with certainty. My reading
  is that both are in the shared start room (`FirstRoom`), which is in `m_first.jpg`; question 8.4 asks.
- Generators (research by two Sonnet agents; licence facts read on the projects' own pages, the rest second-hand and
  marked so in 3):

  | Tool | Outputs | Licence | Verdict |
  |---|---|---|---|
  | PBRify Remix (current) | normal, roughness, height (author disables height) | CC0 | baseline |
  | PBRFusion 4 (NightRaven109, the RTX Remix community's model) | normal, depth (NVIDIA's write-up of v3: also roughness, height) | Apache-2.0 | test |
  | DeepBump | normal from colour, height from normal, curvature | GPL-3.0 (tool, run offline) | test |
  | Ubisoft CHORD | base colour, normal, roughness, metalness, height by integration | research-only, copyleft | **excluded** by licence |
  | Laigter, Materialize, Material Maker | filter-based normal / parallax / occlusion | GPL / MIT | only as a filter reference |

  Nothing found that is made for tiling pixel-art textures. NVIDIA's Minecraft RTX texturing guide says the opposite
  of what we do: for pixelated 16x16 sets use a *height* map instead of a normal map, so every texel is a small block.

### 1.3 Trailer flaws

- V1 grey in 1-1's terminal room: `plan-look-and-perf/trailer-v1-grey.jpg` (pathtrace: body light grey, wings yellow,
  the terminal glass shows the wings). Not diagnosed yet; V1 has been lit by the scene since 2026-11-14
  (docs/reflections.md, "V1 lit like the scene in reflections").
- P-2: pathtrace is darker and less saturated than the game on the street (x0.84, mean colour (66, 12, 3) against
  (89, 11, 4)), the floor is dull rather than reflective (`trailer-p2-wet.jpg`, `v_p2.jpg`). The yellow-green patch is
  the game's `Puddle` object (docs/trailer.md); I did not get it in frame this time.
- Rain puddles are a thresholded world-space noise with a 0.12-wide edge (`Proxy.shader` `RainWetness`), and a wet
  surface gets roughness 0.3 with half specular weight: that is the "obvious edges, not very reflective" report.

### 1.4 Performance

Frame ms (fps). Modes interleaved in one pass; `floor` = relight at trace scale 0.2, where our GPU work is under 2 ms,
so the frame is bound by the CPU and render thread.

| Scene | off | relight | pathtrace | relight floor |
|---|---|---|---|---|
| 0-1 metal (static) | 1.74 | 5.70 (175) | 7.91 (126) | 3.86 |
| 0-3 ghosts (static, enemies active) | 2.24 | 6.67 (150) | 7.84 (128) | <= 5.5 |
| 1-1 doors run | 2.07 | 5.98 (167) | 6.23 (161) | |
| **0-2 run with enemies and gunfire** | 3.27 (2.64 in the floor run) | **9.05 (110)**, p99 22.6 | **10.61 (94)**, p99 22.7 | **6.25**, p99 20.1 |
| 8-3 b | 8.56 (117) | 16.96 (59) | 15.90 (63) | <= 14.5 |

(`<=`: measured with an in-pass scale variant that reallocates targets at each switch; the 0-1 number that way was
4.47 against 3.86 from a `-Scale 0.2` launch, so treat those two as upper bounds.)

GPU per pass (ms, native timer):

| | relight 0-1 | pathtrace 0-1 | pathtrace 0-2 combat |
|---|---|---|---|
| total | 4.87 | 7.08 | 8.62 |
| dispatch (the main ray generation) | 2.85 | 4.39 | 5.80 |
| DLSS RR | | 1.63 | 1.64 |
| denoise + upsample | 0.82 | | |
| volumetrics | 0.37 | 0.37 | 0.43 |
| viewmodel | 0.27 | 0.28 | 0.59 |
| cache, rtas, particles, vfx, exposure | 0.70 | 0.62 | 0.70 |

Pathtrace cost knobs, 0-1 metal (dispatch ms / frame ms; baseline 4.04 / 7.47 in that pass):

| Variant | dispatch | frame | Reading |
|---|---|---|---|
| `ptx.bounces=1` (first path vertex reads the radiance cache) | 1.80 | 5.26 | the second segment and its light sample cost 2.2 ms here, 2.9 ms in combat (5.24 → 2.30) |
| `refl=0` | 2.29 | 5.64 | specular paths are 1.75 ms in the metal room |
| `ptx.cacheTail=0` | 6.60 | 10.05 | the cache tail already saves 2.6 ms |
| `ptx.roulette=0.7` | 3.80 | 7.20 | small |
| `ptx.candidatesRR=4` | 5.04 | 8.45 | slower with fewer candidates: unexplained, to profile |

CPU floor, one pass off at a time (frame ms at `-Scale 0.2`, 0-1 metal): relight 3.86; without volumetrics 3.54;
without the viewmodel pass 3.44; without particle lighting 3.59; without all three and the cache update **2.83**; off
1.70. In combat: relight 6.25, without the four passes 4.74, off 2.64. Main thread (scripts + render) is 3.29 ms of
the 3.86: the rest is the main thread waiting for the render thread. The GPU timer costs nothing (3.91 without it).

What follows:

1. **Pathtrace at 120 Hz** holds in calm scenes with no headroom (126-128 fps) and fails in combat (94 fps), where it
   is GPU-bound (8.6 ms of ours). The largest single cost is the second path segment.
2. **Relight at 240 Hz is blocked by the CPU side before the GPU.** (Refined 2026-11-17, docs/performance.md
   "Profiling": at trace scale 0.5 a calm relight frame is GPU-bound, 5.63 ms of GPU in a 5.81 ms frame; the CPU
   floor below is what remains once the GPU work is cut, and it is the main thread, not the render thread, in 0-x.) With no GPU work at all the frame is 3.9 ms in a
   calm room and 6.3 ms in combat, against a 4.17 ms budget. The four auxiliary ray dispatches (cache update,
   volumetrics, particle light, viewmodel) cost 1.0 ms of frame in 0-1 and 1.5 ms in combat on the render thread,
   more than their GPU time. This matches the per-`DispatchRays` shader-table cost found on 2026-11-15
   (docs/performance.md), now measured in an ordinary level.
3. **In combat the game alone takes 2.6-3.3 ms**, so 240 Hz leaves 0.9-1.5 ms for everything of ours. I do not think
   240 Hz in combat is reachable; 240 in calm scenes and about 170-190 in combat is (5.5).
4. **Hitches**: p99 is 20-23 ms in combat in every RT mode (off: 4.7-7.3). Attributed 2026-11-17
   (docs/performance.md, "Hitches"): mostly the game's own script phase when an explosion spawns, present with RT off.
5. 8-3 is render-thread bound at 59-63 fps and the game alone runs at 117 fps there: 120 Hz is out of reach in 8-3
   whatever we do; the aim there is to cut the 7-8 ms we add.
6. Unity 2022.3.29 has no inline ray tracing (`UnityRayQuery.cginc` is absent from the editor's includes; it arrived
   in 2023.x), so visibility rays cannot move into compute shaders. RR's cost (1.63 ms) is fixed: the SDK offers
   presets D, E and the default F, and docs/dlss.md found D and E never better than F.

## 2. Workstream A: lights and brightness

### Approach

Measure first, then fix in the order of how many places each fix touches: exposure policy (every level), prefab
lights (every level), light classes (rule-driven, every level), then per-level fitting and taste.

**A1. A scoreboard that sees the complaint.** `tools/lights/score.py`, from any bench run's screenshots, per pose
and mode against the game's frame:
- mean ratio and tint (as `sheet.py`);
- **dark-region lift**: mean luma of the pixels the game leaves dark, RT over game;
- **edge reveal**: gradient energy inside those pixels, RT over game (geometry that became visible);
- p10 / p50 / p90 ratios.

Written as CSV per run plus a per-level trend file, so every later change shows as a row. Thresholds are calibrated
on the user's own examples (1-4 lamps and 3-1's door walls must fail today).

Two measuring conditions, always both: **fixed exposure** (auto exposure off) to judge the lights themselves, and the
shipped settings to judge the result. With a strict exposure match on, a bright room is hidden by exposure and the
light work would be invisible in the numbers.

**A2. Pose coverage for all levels.**
- The 60 or so approach poses that exist (20 levels).
- A new bench path type `tour`: jump to each checkpoint of a level the way the game's own checkpoint restore does
  (the `checkpoint` load already does this for one), hold four yaw angles at each, screenshot off and RT. Automatic,
  no authoring, respawn-state rooms.
- F11 poses from the user for the places they point at (question 8.3).
- Per-light attribution at any pose: the fit's leave-one-out pass (`LightFit`) as a plain report, with each light's
  share of the *dark-region* light, to name the lights that reveal hidden geometry.

**A3. Exposure policy.** Default to the game's brightness: `gameMatchDown` 0.4 → about 0.9, band 0.35 → 0.1, and the
dark lift only where the game's own frame is brighter than ours. One user setting, "RT brightness", from "as the
game" to "today's look". Cheap, every level, first.

**A4. Light classes and what RT does with each.** The classifier gets the hierarchy signals from the census (a
`Lighting` / `Lights` group, `Nonstuff`, prefab identity), room-relative range, spot aim, and fog state, and writes a
class for *every* light. An offline pre-pass (`tools/lights/census.py`, from the script used here) covers all levels
without the game; the in-game classifier confirms with geometry and emissive data.

| Class | How it is recognised | RT policy |
|---|---|---|
| sun, moon | directional from above | keep |
| sky fill | directional from below | drop (done: fill cut) |
| fixture: torch, lamp, orb, screen, window | inside or at a small renderer; emissive surface near | **emissive first**: the glowing surface is the light (an emissive mask where the glow is painted into a lit texture, B3) and the fake point / spot light is dropped. Only where nothing can emit (an open flame drawn by particles, a fixture with no glowing part): keep the light, as an area light the size of the source |
| emissive duplicate | next to an emissive surface that already carries the light | drop (today: x0.5-0.85) |
| bounce fill | floating, room-sized range, room-coloured, no aim | **drop**; GI replaces it. Restore at a fitted factor only where the room falls under about x0.85 of the game |
| fog fill | floating in a fogged area, colour near the fog colour, large range and intensity (6-2 entryway, P-2 tunnel) | **volumetric only**: lights the medium, not surfaces, at a calibrated strength |
| accent | floating spot aimed at a prop or a spot on the floor (0-1 gun) | keep as a shadowed spot inside its cone; taste list |
| gameplay / scripted | door, checkpoint, alarm, arena colour phases, flicker scripts | keep, with capped reach |
| prefab | `FirstRoom`, large doors, shops, checkpoints, final doors | one global rule each, all levels |
| enemy, item, effect, player | as today | unchanged |

Review tooling, so that no rule is applied blind (the user's "compare, don't sweep"):
- **class and light toggles**: every class can be switched off, dimmed or soloed at run time, in the light editor
  (Insert) and as bench variants (`lights.class.<name>=off|0.5|solo`), and single lights by path; the editor's labels
  show each light's class and the rule that set it;
- **A/B sheets**: per level, game / current / proposed side by side for every scored pose, with the scoreboard's
  numbers, from one bench run (the proposal is a preview layer, so nothing ships to see it);
- **rule diff**: per level, a table of every light a rule touches (class, rule, before, after, its share of the
  light at the scored poses), written next to the level's override file and kept in review until the user has seen
  the sheets of the taste-sensitive levels.

Mechanisms this needs:
- per-light flags in the packed light data: lights surfaces, lights the medium, reach cap. "Volumetric only" is then
  leaving the light out of the surface samplers' tables while the volumetric pass keeps it;
- a **prefab layer** of overrides keyed by path pattern (`data/lights/_prefabs.json`), under the per-level files;
- **reach**: the 3x cap on the game's curve beyond a light's range becomes a per-class value (about 1x for fills and
  gameplay lights). This is the lever for geometry the game hid by short falloff;
- the fit with a lower bound of 0 and class priors (fills free to reach 0, fixtures anchored near 1), solved on the
  mean *and* the dark-region term, so it prefers dropping a fill over dimming a torch;
- haze in dark fog: our fog and haze must not exceed the game's fog colour where the game's frame is dark (1-4).

**A5. Window beams (user's addition).** The game's `LightShaft` / `Godrays` meshes with no light at their apex stay
raster today (6-2 has 20, 6-1 68, 8-3 38, 8-2 18; docs/volumetrics.md). The beam channel that already replaces 3-1's
eye beams (mesh hull x traced visibility) is generalised: fit the hull's axis and source face (the window end), hide
the game's mesh, and give the window a matching one-sided rect light with a narrow lobe along the axis. The same
source then makes the beam in the air (scattering in the level's medium, cut by pillars, enemies and V1) and the pool
of light on the floor. Brightness is calibrated to the game's beam at a reference pose; where the fit fails the game's
mesh stays.

**A6. Level order.** (1) Prefabs, the start room first: every level opens x1.5 too bright. (2) The user's list: 1-4,
3-1, 6-2, 0-1, P-2 (P-2 only its fog fills: the user likes its look, and its street is already darker than the game).
(3) The audit's worst: 0-2, 4-4, 7-3, 8-2 night side. (4) The rest, act by act, rule-driven, with the scoreboard
deciding where a hand pass is needed.

### Steps

1. `score.py`, the `tour` path type, the attribution report; baseline scoreboard for the levels with poses.
2. Exposure policy and its setting; scoreboard again.
3. Light flags, reach cap, prefab layer; `FirstRoom` and the other prefabs.
4. Census tool and classifier v2 (a class for every light); fit v2.
5. Levels in the order of A6: classify, fit, review sheet, ship the file.
6. Window beams.
7. Docs: lighting-overrides.md (classes, flags, prefab layer, status table per level), relight.md (exposure policy),
   volumetrics.md (beams).

### Outcome, risks, success

- **Outcome**: rooms at the game's brightness by default; distant and unlit geometry dark again; fewer lights per
  frame (a small GPU gain: fewer RIS candidates carry weight); beams that enemies and pillars cut.
- **Risks**: dropping fills exposes places where the radiance cache or two bounces do not carry enough light (dark
  corners, long corridors lit only by a fill): the fit's restore step is the answer. Rule-driven classes will be
  wrong somewhere: every level ships only after its review sheet. A strict exposure match can pump when the game's
  own frame changes fast (lightning, flashes): keep the smoothing and test 5-2. The override files grow: keep them
  generated plus a small hand layer.
- **Success**: per pose, mean x0.9-1.1 of the game and dark-region lift under x1.3 in pathtrace at shipped settings,
  for at least 90 % of scored poses per level, with the misses listed; the user's four examples pass by eye.

## 3. Workstream B: generated material maps

### What is wrong (ranked, to be confirmed by B1)

1. Per-texel normals against RR's 1/3-scale trace: facets smaller than what RR can reconstruct turn into blotches.
2. PBRify's relief follows a photo prior, not the texel grid: texels inside one brick tilt differently.
3. The class table is coarse: one strength for all "stone, brick, concrete, baked level geometry".
4. Strength: already halved to 1; not the root cause.

### Steps

**B1. In-engine matrix (one visual run, a few hours).** At the 1-1 hall, the start room and one metal pose: detail
off / PBRify / PBRify with normals low-passed inside each texel neighbourhood / normals derived from a per-texel
height with a bevel profile / roughness and AO only. Each under RR at 1/3, RR at full scale and UltraRT's denoiser.
This separates "the maps" from "RR's resolution" before any generator work.

**B2. Generator bake-off (offline).** A fixed set of about 24 textures (the named ones, plus brick, stone, metal
panel, wood, flesh, marble, printed art) through PBRify, PBRFusion 4, DeepBump and a height-first route. Contact
sheets as `tools/materials/compare.py` makes, plus the same in-engine poses. The height-first route: height from
whichever model gives the cleanest one, quantised per texel, normals from height differences on the texel grid, so
every map is constant per texel and grid-aligned (the Minecraft RTX advice).

**B3. More map types**, by what they would change on screen in this renderer:

| Map | Visible effect here | Cost | Verdict |
|---|---|---|---|
| AO / cavity per texel (from height) | mortar lines, panel seams and grates darken in bounce and sky light; grid-aligned, RR-friendly | a multiply on indirect; fits the free alpha range of the detail texture | **do** |
| Emissive mask | painted lamp glass, windows and screens on lit materials become real emitters; lets A4 turn fixtures into area lights and drop their fake point lights | texture read exists for emissive materials; a mask for selected textures | **do**, for a reviewed list |
| Metal mask | rivets, bare edges and rust within a "metal" texture stop sharing one metalness; breaks up mirror-flat panels | one more channel; heuristics per class, no model outputs it under a usable licence | try on the 0-x and 7-x sets |
| Height | no parallax or displacement in a ray-traced flat quad without new geometry; used only as the *source* of normals and AO | offline only | source, not shipped |
| Specular / F0 | below what the class table already gives | | no |

**B4. Per-texture review, ranked by screen coverage.** A level uses 100-160 textures and the game reuses a small set
heavily. Log which textures cover the most pixels along the bench routes, and review the top 100 on a sheet with
per-texture settings (`ultrart-materials.json` already takes rules: strength, flat, low-pass, mask). No generator will
be right for 2,000 textures; this puts the hand work where the pixels are.

**B5. RR interplay.** Fade the detail normal by footprint under RR (a texel under about two traced pixels gets the
flat normal and Toksvig roughness instead; the ray-cone LOD already has the footprint), and test RR's normal guide
with the flat normal.

### Outcome, risks, success

- **Outcome**: relief that reads as part of the pixel art (each texel a block or flat), contact darkening from AO,
  lamps that emit. If B1 shows the maps are fine at full-scale RR, the fix is B5 alone and no generator changes.
- **Switch rule**: change generator only if it wins the blind sheet on at least two thirds of the set *and* in the
  three in-engine poses; otherwise keep PBRify with the height-derived normals and per-texture rules.
- **Risks**: PBRFusion is a diffusion pipeline tuned for upscaled inputs (its behaviour at 64 px is unknown); GPL
  tools are run offline and their outputs are not GPL, but nothing of theirs is bundled; maps are still generated
  from the user's own game files and never committed.
- **Success**: the user picks the new maps on the three named surfaces from a blind sheet; pathtrace burst and
  motion noise at 1-1 indoor a and 8-2 a no worse than today.

## 4. Workstream C: flaws from the trailer

| Item | State | Plan | Priority |
|---|---|---|---|
| V1 grey in 1-1's orange room | **done 2026-11-17**: it is physics (blue albedo under orange light); V1 keeps 0.2 of its own colour unlit (docs/reflections.md, "V1's own blue") | One third-person capture at the terminal pose with `albedo`, `dn-lighting`, pathtrace, and `PlayerModelLit` off. Candidates: (a) the lit copy's albedo is not what the game shows (the body material is `_USEALBEDOASEMISSIVE_ON`: the game shows it unlit); (b) the body's reflection class makes it mirror the room; (c) correct physics: a blue-grey body under orange light. If (c), it becomes a taste call (8.7) | high |
| P-2 `Puddle` yellow-green | **open 2026-11-17: not reproduced** in today's build at the pose (docs/reflections.md, "Water tint"); a tint-by-alpha switch exists, off. Earlier note: cause known in outline: the game draws a dark, nearly transparent octagon; we shade its texture as an opaque lit surface | dump the material; class it as standing water: no albedo of its own, mirror-like, darkening as the game's blend does | high |
| Rain puddle edges, dull wet floor | **built 2026-11-17 behind `-ultrart-wet-soft`, review R8** (docs/vfx.md, "Soft puddles"). Was: `RainWetness`: noise threshold 0.12 wide; wet roughness 0.3, weight 0.5 | wide soft edge with a wet margin around each puddle; break the noise with the surface's own height (B2) so water sits in joints and low texels; wet floor roughness about 0.15 with full Fresnel; check P-2's colour against the game (x0.84, less saturated) | high |
| 5-2 lightning blows out | noted | leave; test that the stricter exposure match (A3) does not pump on it | none |
| 1-4 vanity mirror black when closer than x 14, in the game's image too | game-side | read the bedroom's triggers in `Assembly-CSharp`; if the game switches the room off, document it | low |
| White speck in the path-traced mirror | not reproduced here | capture at the mirror pose with the firefly clamp views; likely one unclamped specular sample | low |
| Wing-shaped shadows from 1-1's torch cups | **done 2026-11-18** (`t3/emissive-all`): flame-driven lights, a sphere light the size of the flame at the flame (docs/emissive-lights.md, "Flame-driven lights"). Was: the fixture lift puts the light above the bowl, whose rim then shadows the wall | area light the size of the flame for torches (A4's fixture rule), or exclude the cup from its own light's shadow rays as orbs do | medium |
| 1-3 shop terminal bloom blob | bloom source includes an emissive sign at full strength in the first frames | clamp the bloom source per pixel and check the sign's emission scale | low |
| Paired capture: gun one animation frame off | capture order within a frame | take the game's image after the viewmodel's animation update of the same frame | low |
| 8-1 city: cloud haze missing through the hotel windows (user, 2026-11-18) | **half done 2026-11-18** (73a77f6, in master 96f34bf): the cloud layers were traced as cutouts (white streaks); blended veils are now left out of the RT scene (docs/scene-proxies.md, "Blended veils"). Open: the city is behind a portal, so the game's own veil draw never reaches the traced image | blended (not additive) layers on the camera and portal rays, for `FraudCity_Clouds` and `FraudCity_Water`; after the current queue. Also: the veil rule is global, so check more levels with blended layers (water, glass, fog sheets, the 5-2 sea, 0-5 lava haze) and confirm from the `Blended veils` log line that only veils match | medium |
| Texel edges softer than the game's without detail (new, 1.2) | seen in one crop | compare remodulated albedo against the game's at grazing angles | medium |

Success: each item either fixed with a before / after pair, or closed with the reason written in its doc.

## 5. Workstream D: performance

### 5.1 Profiling pass (first, about one session)

What is missing today is *where the render thread and the main thread spend their time*, and whether a frame is
bound by GPU, main thread or render thread.

1. **Render-thread time per pass, from the timer we have.** The GPU timer's begin / end plugin events run on the
   render thread: take a CPU timestamp in each callback and report CPU ms per slot next to GPU ms.
2. **Whole-frame GPU time**, the game included (timestamps at frame start and before present), so "GPU-bound" is
   read, not inferred.
3. **CPU sampling of the player** (ETW) with Unity's public symbols for `UnityPlayer.dll`, on 0-1, the 0-2 combat run
   and 8-3. Step 0 is checking that the symbols resolve for this build.
4. **Bench**: a `floor` switch (what `-Scale 0.2` did here), the 0-2 combat run as a standard scenario, and hitch
   attribution: for each frame over 12 ms, which section or event (new BLAS, light list rebuild, GC).
5. Ray counts per pixel by type in the two ray generations (a debug counter view), to turn 5.2's estimates into
   numbers; and the `candidatesRR=4` anomaly.

### 5.2 Candidates

Gains are frame ms. "Measured bound" = the most the idea can give, from a run above; "estimate" = my expectation.

**Pathtrace, target 8.33 ms; today 7.5-7.9 calm, 9.8-10.6 combat.**

| # | Idea | Gain |
|---|---|---|
| P1 | **Footprint-terminated paths.** After a diffuse bounce the ray cone is wide; when its footprint at the first vertex exceeds the radiance cache's cell, read the cache there (as `bounces=1` does everywhere) and trace the second segment only for near hits, where contact shadows and colour bleeding are. Relight's reflections already use this test | measured bound 2.2 calm, 2.9 combat (dispatch); estimate 1.2-2.0 with the look kept |
| P2 | The same for specular paths: rough lobes end in the cache at their first hit, mirrors keep two segments | part of P1's bound (reflections are 1.75 in the metal room); estimate 0.4-0.8 |
| P3 | Auxiliary passes on a schedule (C1 below) | estimate 0.3-0.6, more in combat |
| P4 | Viewmodel pass in combat (0.59 GPU): full-screen rectangle only while the gun leaves its box | estimate 0.2 |
| P5 | Dynamic trace resolution as the guarantee: RR input between 1/3 and about 0.28 when the frame exceeds the budget | holds the target by definition; a visible softening, so last |

P1 + P3 should put combat near 8.0-8.5 ms; P5 covers the rest. RR's 1.63 ms is fixed.

**Relight, target 4.17 ms; today 5.7-6.7 calm, 9.0 combat; CPU floor 3.9 calm, 6.3 combat.**

| # | Idea | Gain |
|---|---|---|
| C1 | **Pass scheduler.** Cache update, volumetrics and particle lighting on alternating frames (each still runs over 100 times a second at 240 Hz); their temporal filters take the frame time | measured bound 1.0 calm / 1.5 combat for all four passes; estimate 0.4-0.7 |
| C2 | **One dispatch for the auxiliary ray generations** (one shader-table build instead of three or four): extra rows of one dispatch select the job. Unity's 8-UAV limit is the difficulty; spike first | estimate 0.5-0.8 calm, more in 8-x (2026-11-15: about 0.5 ms per dispatch there) |
| C3 | Viewmodel on the slim hit pass, or its rays as rows of the main dispatch | measured bound 0.42; estimate 0.3 |
| C4 | Fewer shader-table records: never-moving dynamic proxies merged into static groups | small in 0-x, estimate 1-2 in 8-3 |
| C5 | Plugin CPU (0.6 calm, 0.9-1.2 combat): change-driven sync instead of per-frame polling for static, emissive and light lists | estimate 0.2-0.4 |
| R1 | Compile-time ray generations per mode (relight without hybrid, reference and debug branches; fewer trace sites) | estimate 0.2-0.5 GPU |
| R2 | **Half-rate secondary rays at high frame rates**: bounce, sky and reflection rays on a checkerboard that alternates per frame; the denoiser's history fills in. Samples per second stay what they are at 120 Hz | estimate 0.6-1.0 GPU; 5.1 step 5 gives the bound |
| R3 | Denoiser passes by history length, TAAU folded into the composite | estimate 0.2-0.4 GPU |
| R4 | Dynamic trace scale as the guarantee | as P5 |

Calm scenes: floor 3.9 → about 3.0 (C1-C3, C5), GPU 4.9 → about 3.2 (R1-R3, C1): 240 Hz is plausible. Combat: floor
6.3 → about 4.8: **about 170-190 fps, not 240**. The game alone is 2.6-3.3 ms there.

**Both modes.** Hitches (p99 20 ms in combat): attribute first; likely fixes are budgeted BLAS creation for spawned
enemies and the one-off 6-7 ms `lights` frame. 8-x: C2 and C4 are its fixes; expect 60 → 75-90 fps.

### Risks and success

- P1 changes the look where the cache is coarse (0.5 m cells): judged on A/B sheets at contact-heavy poses before it
  becomes the default; the footprint factor is the dial.
- R2 raises noise in motion (history is short exactly there): measured with the motion-noise suites; it may end up
  enabled only above a frame-rate threshold.
- C2 may not fit Unity's binding limits; C1 and C3 do not depend on it.
- Everything is measured in one launch against master (docs/performance.md's method), GPU slots and frame times, on:
  0-1 metal, 0-3 ghosts, 6-2 start, 0-5 lava, the 0-2 combat run, 8-3 b.
- **Success**: pathtrace average >= 120 fps on all of those but 8-3, including the combat run, with p99 under 12 ms;
  relight >= 240 on the four calm poses and the best reachable in combat, stated with its number; no look change
  outside the A/B-approved ones; noise metrics within their run-to-run spread.

## 6. Workstream E: refactoring

Only what the work above needs or what already hurts:

1. **Frame pipeline.** `Plugin.Trace` builds the whole frame in one method of about 450 lines. Passes become objects
   (record, GPU slot, CPU section, cadence, enable), run by a small scheduler. C1 and R2 need exactly this.
2. **Ray generation per mode.** `RayGenBody(full)` already specialises pathtrace at compile time; do the same for
   relight, and move hybrid, pt-dn, reference and debug code out of the two hot ray generations (question 8.6).
3. **Default mode.** `[View] DefaultMode` becomes `pathtrace` where RR is available, `relight` otherwise, with a
   config migration (BepInEx keeps a written default: an entry still equal to the old default is rewritten once).
   README, settings.md and the bench defaults follow.
4. **Light overrides v2.** Class, flags and the prefab layer in the schema; classification and the fit's maths as
   plain functions without Unity types, with unit tests (the repo has none; these are the pieces where a silent
   error ships wrong lights to every level).
5. **Bench suites.** About 230 files, most of them one-off diagnostics: move those to `bench/suites/archive/`, keep a
   named core (quick, perf, combat, bright, materials, noise).
6. Settings: one registry for flag, config entry, menu item and bench variant, replacing the per-file parsers, when
   the files are touched anyway.

## 7. Order, branches, parallel work

Conflict hotspots: `PathTrace.raytrace`, `UltraRTLights.hlsl`, `Proxy.shader`, `Plugin.cs`. The split keeps each of
them in one branch at a time.

| Stage | Branch A | Branch B | Offline (no game, no conflicts) |
|---|---|---|---|
| 1 | `perf-profile`: 5.1, the pass scheduler (E1), default mode (E3) | `bright-metrics`: A1, A2, A3 | B2 generator bake-off; census tool |
| 2 | `perf-paths`: P1, P2, R1 (`PathTrace.raytrace`, `UltraRTPathTraceFull.hlsl`) | `light-classes`: A4 mechanisms and prefabs (`LightSampling.cs`, classifier, a flag in `UltraRTLights.hlsl`) | B1 matrix (one visual run), then B3-B5 design |
| 3 | `perf-dispatch`: C1-C5, R2, R3 | `light-levels`: A6 level files (data only), then `window-beams` (A5) | `materials-v2` implementation after stage 2's `Proxy.shader` work lands |
| 4 | `trailer-fixes` (C), hitches, 8-x | level roll-out continues | |

(As approved: one agent does all of it, one branch at a time, so the two columns run one after the other: stage 1
is `bright-metrics`, then `perf-profile`; then stage 2 left to right, and so on. Each branch is reported as ready and
the next starts from master, or stacked on the previous one while that waits for its merge.)

Dependencies: A3 before any light fitting (it changes what "matches" means); A1 before everything in A; E1 before
C1 / R2; B's emissive masks before the fixture-to-area-light part of A4 (not before the rest); the puddle shading
in C wants B's height maps but can start without. Two implementation agents at a time fits this table; a third only
for the offline column. Merges go through the lead's merge-regress as now.

## 8. Decisions (answered by the user, 2026-11-17)

The user agreed with every recommendation; where the answer adds something it is noted.

| # | Question | Decision |
|---|---|---|
| 1 | Brightness target | Pathtrace sits at the game's brightness by default, with an "RT brightness" setting for the brighter look |
| 2 | Fills in 7-3, secret rooms, boss arenas | May be dropped or dimmed, each with a review sheet before it ships. P-2: only fog fills become volumetric-only |
| 3 | F11 poses for the over-lit places | Not provided yet: proceed with the tour poses. The user may save poses later |
| 4 | Which surfaces are "the 8-2 intro room floor" and "the 0-1 exit door" | Not answered: proceed with the best guess, the shared start room |
| 5 | Accent lights (0-1 gun spot) | Keep as real shadowed spotlights |
| 6 | Old modes | `hybrid` and `pt-dn` frozen: kept working, no new features, out of the hot ray generations; removal later if nobody misses them |
| 7 | V1's colour, if it is physics | Lit by the room, with a small unlit share so the blue reads |
| 8 | Perf targets | 120 fps average in the combat run with p99 under 12 ms for pathtrace. Relight: 240 in calm scenes, best effort in combat ("240 is maybe optimistic"). **8-x: they perform much worse because of the portals; fine as long as they never drop below 60 fps** |
| 9 | Quality for speed | Pathtrace may read the radiance cache after the first bounce when that hit is far (after the A/B has been seen), and dynamic resolution may act as the last resort |
| 10 | Material tools | PBRFusion and DeepBump may be tested (DeepBump offline only); CHORD stays excluded |
| 11 | Window beams | Replace the game's beam meshes wherever the fit works, keep the game's elsewhere |
| 12 | P-2 street | **Leave it as it looks now.** It may depend on which lights are active: if a light change alters it, flag it with a before / after instead of pulling it toward the game's red |
| 13 | Agents | **One agent does all of it, sequentially**, no time limit; research subagents (Sonnet 5.5, low) are fine. The lead runs merge-regress and merges each branch when it is reported ready |
| 14 | Start room (first light proposal, 2026-11-17) | Its floating lights at half strength are "about right". Some lights are fine where they are needed, but **emissive surfaces should carry a room where possible** |
| 15 | First light sheets (2026-11-17) | 1-4, 3-1, 6-2, 0-1 and the shared start / final room "look great": on by default. The same approach for the remaining levels, their sheets reviewed in a batch. P-2's exposure exception: dropped ("may as well drop the p2 exception too"), P-2 follows the general policy; normal smoothing: "looks reasonably good too", on by default (smooth 1), the setting stays |
| 16 | Second review (2026-11-17) | R8 (soft puddles), R9 (lamps lit by their own glowing part) and R10 (4-3, 5-3, 7-3, 0-3, 8-2, 5-1): "they look great! big fan of all of those": on by default. The policy is approved on two batches: keep producing sheets, more levels per review. Tour views must not be of the void (0-1's first stop) |
| 17 | The target changes (2026-11-17, after the second batch shipped) | "A little too dark ... the colours had a lot more pop ... large lights should be very bright, brighter than the base game ... bright, harsh lighting." Not the game's mean brightness: contrast. Dark areas stay dark (fills' reach), key lights and fixtures at full or boosted, exposure may sit above the game, colour scored. Level roll-outs hold until the new policy has been seen (0-3 first; the user also named 0-4 and 2-2) |
| 18 | Punch verdict and the night's mandate (2026-11-18) | "This looks great! much much better!" Fixtures x1.8 ("x1.6, maybe a touch brighter"); game match down 0.4 beyond 0.35 EV and the shadow toe 0.6 are defaults; fills at full with the reach cap. "Assume I accept the rest of the batches": the policy ships on every level without a review gate, sheets in one folder for a look afterwards, a level that clearly regresses is fixed or left off and flagged. Then window beams, then the trailer re-rendered (`trailer-final-v2`) |
| 19 | Fixtures x2.5 (2026-11-18) | "I think we probably could go up to the 2.5x on the lights": fixture, accent, glow, effect and item lights x2.5 globally with `medium` 0.4; level exceptions kept unless they read dim next to the rest. Same morning: 7-3 is far too dark (player light back up); emissive over lights on every level, flames as flame-driven area lights, enemies' glowing parts as emitters; trailer v3 with the lightning strike |

## 9. Effort

In sessions (one agent, a few hours including builds and game runs):

| Workstream | Sessions | Notes |
|---|---|---|
| A1-A3 scoreboard, tour poses, exposure policy | 2 | |
| A4 classes, flags, prefab layer, classifier and fit v2 | 3-4 | |
| A6 level roll-out, 43 levels | 5-8 | rule-driven; hand passes where the scoreboard says |
| A5 window beams | 2 | |
| B1-B2 matrix and bake-off | 1-2 | mostly offline |
| B3-B5 new maps, review workflow, RR interplay | 3-4 | depends on B1's outcome |
| C trailer flaws | 2-3 | V1 and puddles first |
| D 5.1 profiling pass | 1 | |
| D pathtrace (P1-P5) | 2-3 | |
| D relight and CPU floor (C1-C5, R1-R4) | 4-6 | C2 is the uncertain one |
| D hitches, 8-x | 2 | |
| E refactoring not already counted (overrides v2 tests, suites, settings) | 2 | E1-E3 are inside D |

About 30-40 sessions in all; with two agents in parallel, stages 1 and 2 are about four sessions each side.

## 10. Review list (for the user, one sitting)

Everything that needs the user's taste waits here, **off by default** until answered (lead, 2026-11-17). Sheets are
local, one folder: `bench/results/review/` (rendered frames: not in the repo). Each row: what to look at, the
question, and what happens on yes.

| # | Since | Sheet(s) in `bench/results/review/` | Question | On yes | How to see it in game |
|---|---|---|---|---|---|
| ~~R1~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-firstroom-emissive.png` | Start and final room lit by their wall strips (x4.5) with the floating lights off: right? | move the rule from `data/lights/proposed/_rules.json` to `data/lights/_rules.json` | bench variant `lc.layer=proposed` |
| ~~R2~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-1-4.png` | 1-4: room lights x0.4, street lamps x0.5, none reaching further than in the game. The lamps and bridge poses read a little dim (x0.84, x0.87): acceptable? | ship `proposed/Level 1-4.json` | `lc.layer=proposed` |
| ~~R3~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-3-1.png` | 3-1: room lights x0.5, the Bridge Arena's x0.35 | ship `proposed/Level 3-1.json` | `lc.layer=proposed` |
| ~~R4~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-6-2.png` | 6-2: room lights x0.4. (Known: an orange patch by the Organ Hall terminal.) | ship `proposed/Level 6-2.json` | `lc.layer=proposed` |
| ~~R5~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-0-1.png` | 0-1: room lights x0.9 with their reach capped | ship `proposed/Level 0-1.json` | `lc.layer=proposed` |
| ~~R6~~ **answered 2026-11-17: exception dropped** | 2026-11-17 | `board-20261117.md` | P-2: keep its brighter exposure exception (x1.57 at shipped settings), or bring the street down? | no change / remove the exception | |
| ~~R7~~ **approved 2026-11-17: smooth 1 is the default** | 2026-11-17 | `materials-smooth-1-1-wall.png` | Detail normals smoothed over neighbouring texels ("smooth 1"), or normals off on walls like this (top row, third cell)? | `[Materials] NormalSmoothing` default 1 | `-ultrart-materials-smooth 1` |
| ~~R8~~ **approved 2026-11-17: soft puddles on by default** | 2026-11-17 | `soft-puddles-p-2.png` (game / today / soft) | Soft puddle edges with a soaked margin and a glossier wet floor? | `wet.soft` on by default | `-ultrart-wet-soft on` |
| ~~R9~~ **approved and shipped 2026-11-17 (1-4 with its statue orbs handled separately)** | 2026-11-17 | `emissive-8-2-street-lamps.png`, `emissive-8-2-standing-lamp.png`, `emissive-1-4-street-lamps.png` (game / today / two strengths) | Lamps lit by their own glowing part, their fake lights off: the look, and which strength? (Proposed: 8-2 heads x4 and standing-lamp bulbs glow 20, 1-4 bulbs glow 6; `bench/suites/emissive-review.json` shows the files as they are.) | proposals shipped for 8-2 and 1-4, then the same per level | `lc.layer=proposed` (docs/lighting-overrides.md, "Emissive fixtures") |
| ~~R10~~ **approved and shipped 2026-11-17** | 2026-11-17 | `review-4-3.png`, `review-5-3.png`, `review-7-3.png`, `review-0-3.png`, `review-8-2.png`, `review-5-1.png` (per view: game / ships / proposal, shipped and fixed exposure) | Six more levels brought to the game's brightness: level lights capped to the game's reach, room lights x0.35-0.5, torches and lamps x0.5-1. 7-3 also dims the flashlight to x0.6 (its bounce floods the white halls). 5-3 and 5-1 trade bright views for some dim ones: too far? | ship the six `proposed/Level *.json` (8-2's includes R9's lamps) | `lc.layer=proposed` (docs/lighting-overrides.md, "Levels, second batch") |
| R11 | 2026-11-17 | `water-tint-p-2-hall.png` (game / tint as before / tint by the game's alpha, at the trailer's hall-crane end pose) | P-2's yellow-green puddle: at the pose it was first seen from, the puddle is red in every mode in today's build (docs/reflections.md, "Water tint and P-2's puddle"). Tinting water by the game's alpha makes it slightly redder. Turn that on anyway, or leave it? | `trans.alphaTint` on by default | bench `trans.alphaTint=1` |
| R12 | 2026-11-17 | `materials-deepbump-maps.png` (albedo / PBRify / DeepBump), `materials-deepbump-engine.png` (game / maps off / PBRify / DeepBump in the frame) | For information: the bake-off found no visible gain from DeepBump over PBRify with smoothing 1, so nothing changes (docs/materials.md, section 10). Say so if the sheet reads differently to you | none | alt only: `-ultrart-materials-source db` |
| ~~R13~~ **shipped 2026-11-18 under the punch policy** ("assume I accept the rest of the batches"; sheets `punch-all/`) | 2026-11-17 | `review-P-1.png`, `review-P-2.png`, `review-2-4.png`, `review-8-4.png`, `review-1-3.png`, `review-7-2.png`, `review-4-4.png`, `review-3-2.png` | Eight more levels to the game's brightness (docs/lighting-overrides.md, "Levels, third batch"). P-2 and 7-2 also dim the player's own light (x0.6, x0.85). 7-2 trades bright views for dim ones (5 -> 10 of 18): too far? Should the player's light be dimmed once for every level instead? | ship the eight `proposed/Level *.json` | `lc.layer=proposed` |
| ~~R14~~ **approved and shipped 2026-11-18** (fixtures x1.8; exposure and toe as defaults) | 2026-11-17 | `punch-0-3.png`, `punch-0-4.png`, `punch-2-2.png` (per view: game / old bright build / ships / new with fixtures x1.6 / new with fixtures x2.5) | The new target, "bright, harsh key lights and colour, dark areas kept": is this the look, and which fixture strength? Known: 0-3's pillar room fogs its void again; colour is back most of the way, not to the old build's level | apply the policy to the shipped levels (start room, 1-4, 3-1, 6-2, 0-1, 4-3, 5-3, 7-3, 8-2, 5-1), sheets again, then defaults | `lc.layer=proposed` on 0-3, 0-4, 2-2 with `-ultrart-ae-match 0.75,0.4 -ultrart-ae-match-band 0.35,0.35 -ultrart-relight-toe 0.6` (docs/lighting-overrides.md, "Punch") |
| R15 (a look afterwards, shipped) | 2026-11-18 | `punch-all/index.md` and `punch-all/punch-<level>.png` (per view: game / before / punch) | Every level under the punch policy. Worth a look first: 7-1 and 7-3 (kept dark by following the game), 8-2 (kept as it was), 4-3 and 5-3 (x1.2), 0-3's pillar rooms, 0-E | none: tell me which levels read wrong | on by default |
| R16 (information; off) | 2026-11-18 | none yet (the tours do not look along the beams) | Window beams, first step: traced shadows on the game's window beams take glow out of the room (1-1 hall -3 to -5 luma) without clear shafts. Left off | say if you want it pursued (window light + poses) before other work | `-ultrart-window-beams on` |
| R17 | 2026-11-18 | `bench/results/trailer/trailer-final-v2/` (master, 1080p, clips) against `trailer-final-final/` | The trailer on the punch build. Better or equal in twelve shots; weaker in five (7-3 tree, 7-3 gun, 8-2 street, 1-1 terminal, 6-2 hall: docs/trailer.md, "second render") | which of the five to fix in the look (8-2 lamp cones and pools, the start room's red, 7-3's exposure) before a third render | none: the look as shipped |
| R18 (shipped; a look afterwards) | 2026-11-18 | `bench/results/review/punch-25/` (`index.md`, `punch25-<level>.png`, `flagged-1.jpg`, `flagged-2.jpg`) | Fixtures x2.5 on every level. 5-1 held at x1.8 | is 5-1 right at x1.8, and should 4-1's green hall come down | `lc.set.fixture.intensity=1.8` (and accent, glow, effect, item) for the old strength |
| R19 (shipped; a look afterwards) | 2026-11-18 | `bench/results/review/dark-7-3/` (`trailer-poses.jpg`, `tour-7-3.png`) | 7-3 brighter: flashlight x4, exposure a little above the game, red tree light on the walls | are the halls still dark enough (tour dark +16), and should P-2's flashlight go to x2 | `lc.set.player.intensity=1`; `ae.levelOverride=0` |
| R20 | 2026-11-18 | `bench/results/review/emissive-all/` (`index.md`): `handover-<level>.png`, the game, the fixture lights and the handover per view. Look at 8-1 (darker in the dark, lamps light their surroundings), 1-4 (lamp garden), P-1 and P-2 (street lamp bulbs) | Do the handed-over fixtures stay punchy enough, 8-1 above all? |

Not a question, for information: `3-1-stairs-lit.png`; `v1-unlit-share.png` (V1 with 0 and with 0.2 of its own blue showing, by decision 7; `-ultrart-v1-unlit F` to change) (the Stairways' haze is far weaker than the game's; work for
`t3/window-beams`).
