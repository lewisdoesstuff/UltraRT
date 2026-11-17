# Lighting overrides (per-level light re-authoring)

ULTRAKILL's lights were placed for per-vertex lighting without shadows: point lights inside lamp housings, invisible fill
lights faking bounce, radii and colours tuned around fog, lamps and windows with no light behind them. Relight and
pathtrace trace those lights with shadows and real bounce, so some of them now leak, self-shadow or double count. The
RTX remasters got their largest visual gains from re-authoring lights. This is UltraRT's version: per-level override
files, an in-game editor to review and tweak them, and two automatic passes that write them (a classifier for
mechanical fixes, a fit to the game's room brightness and colour).

Code: `src/UltraRT/LightOverrides.cs` (data, matching, packing), `Plugin.LightOverrides.cs` (runtime + editor),
`LightClasses.cs` and `Plugin.LightClasses.cs` (classes, rules, review switches), `Plugin.LightClassifier.cs`, `Plugin.LightFit.cs`; shader side `UltraRTLights.hlsl` (area lights) and `Exposure.compute`
(fit statistics). Shipped data: `data/lights/<scene>.json`. Review sheets: `tools/lights/sheet.py`.

## Where overrides apply

- **Relight look only**: `relight`, `pathtrace`, or a mode with relight forced (`+relight=1`). Hybrid and pt-dn composite
  against the game's own lighting (the game draws its lights itself), so changing the traced lights there would only
  break their ratio; they keep the game's lights.
- Overrides change the **packed light data only** (`_Lights`). The `Light` components are never touched, so the game's
  flicker scripts, area triggers and 8-2's day/night keep working: an entry multiplies whatever the light is this frame,
  and a light the game switches off stays off. Every consumer of `_Lights` sees the result: direct light (RIS, ReSTIR,
  reference), pathtrace's NEE, the radiance cache, volumetrics, lit particles, the viewmodel pass.
- **Statue orb lights** get one more factor after the override: `Plugin._orbLightScale` 0.5 (`-ultrart-orb-light F`), applied to
  the lights of orb fixtures whose holder was found ([scene-proxies.md](scene-proxies.md), "Statue orbs"). It stacks on any
  override of those lights, and the classifier leaves `fixture:orb` lights alone.
- Off switches: `-ultrart-nolightoverrides`; settings menu "Light editor" > "Per-level light overrides" (config
  `[Lights] Overrides`); Keypad `*` while the editor is open (session); bench variant `plugin._lightOverrides=0`.

## Files and layers

| Layer | Path | Written by |
|---|---|---|
| shipped | `BepInEx\plugins\UltraRT\lights\<scene>.json` (from the repo's `data\lights\`, staged by `build.ps1`, mirrored by `deploy.ps1` / `Sync-Stage`) | the classifier and the fit, reviewed, committed |
| user | `BepInEx\ultrart\lights\<scene>.json` | the in-game editor (never touched by a deploy) |
| preview | `-ultrart-lights-preview`: `BepInEx\ultrart\lights\classified\` replaces the shipped layer | `-ultrart-classify-lights` |

`<scene>` is ULTRAKILL's scene name (`Level 7-3.json`). A user entry replaces the shipped entry of the same light; an
empty user entry (Shift+Keypad 0) resets that light to the game's values. Both files are re-read within a second when
they change on disk, so hand edits show up live.

### Format

```json
{
  "version": 1,
  "scene": "Level 1-1",
  "exposure": { "rtBrightness": 1 },   // optional, level-wide: see "Level exposure" below
  "classes": { "fill": { "intensity": 0 } },   // optional: this level's class policies ("Classes and rules")
  "rules": [ { "name": "entry fog", "path": "1 - Entryway/Lighting/", "class": "fogfill" } ],   // optional, before the global rules
  "lights": [
    {
      "path": "2 - Courtyard/Lamps/Lamp (3)/Point Light",  // hierarchy path, "(Clone)" removed
      "pos": [12.5, 4.2, -30.1],                           // the light's position when first seen (identity)
      "intensity": 0.8,          // x the game's colour x intensity (gamma units, like Light.intensity)
      "color": [1.0, 0.9, 0.8],  // per-channel multiplier
      "range": 1.2,              // x range
      "shadows": false,          // force on / off (absent: as the game / relight decide)
      "disabled": true,
      "reach": 0, "surfaces": false, "medium": 2,  // "Classes and rules": falloff cap, air only, strength in the air
      "shape": "rect",           // point (no change) | sphere | rect | disc
      "size": [1.2, 0.3],        // m: rect width, height; sphere / disc: diameter in size[0]
      "normal": [0, -1, 0],      // emission side of a rect / disc (default: a spot's forward, else down)
      "tangent": [1, 0, 0],      // a rect's width axis
      "offset": [0, -0.4, 0],    // world-space move (m); an entry with an offset or a shape owns the position (no fixture lift)
      "class": "fixture:lamp", "source": "classifier", "confidence": 0.7, "note": "..."
    }
  ],
  "added": [
    { "id": "user-031522-123", "pos": [..], "shape": "rect", "size": [0.5, 0.5], "normal": [..], "color": [1, 1, 1],
      "intensity": 1, "range": 8, "shadows": true, "mask": "FFFFFFFF", "bind": "Room/Screen (Clone)" }
  ]
}
```

**Matching** (`LightOverrides.Find`): an entry with the same normalised path wins if its `pos` is within 2 m (the
nearest one if several: ULTRAKILL reuses names like `Point Light`); otherwise an entry with the same name within 0.5 m.
Checkpoints clone whole rooms in place and rooms are switched on later, so path + original position survives both. A
light first seen while moving (a platform) keys on where it was then.

**Added lights** are appended after the scene lights (and before the effect lights) as area lights. `bind` names a
renderer by normalised path: the light is on only while that renderer is drawn (an emissive screen, a room that toggles).

**Level exposure** (`"exposure": { "rtBrightness": F, "note": ".." }`, 2026-11-17): the level's own RT brightness
(docs/relight.md, "Follow the game"). The level is shown at least that bright, whatever the user's "RT brightness"
setting is; the user's file wins over the shipped one, and switching overrides off switches it off too. It is for a
level whose look was approved under the exposure policy before 2026-11-17. No shipped file has one now: `Level
P-2.json` had 1 and no light entries until the user dropped the exception the same day ("may as well drop the P-2
exception too"), and the file went with it. Bench variant `ae.levelOverride=0` shows the level without it. The editor
keeps a user file's block when it saves; the fit's output file does not carry one.

## Classes and rules (2026-11-17)

Every level light has a **class**, and a class can carry a **policy**: what RT does with all its lights. This is how
the 13,157 lights of the 43 levels are handled without an entry each (docs/plan-look-and-perf.md, A4). Code:
`LightClasses.cs` (rules, policies), `Plugin.LightClasses.cs` (resolution per light, review switches, census).

**Where a class comes from**, first match wins:

1. code, for lights that move or that another system owns: `sun`, `skyfill` (a directional light from below),
   `player`, `enemy` (under an `EnemyIdentifier`), `item`, `effect` (not part of the level at build), `lava`;
2. the level file's `"rules"` (the user's file, then the shipped one);
3. `lights\_rules.json` (`data/lights/_rules.json`), for every level;
4. none matched: `other`.

An entry with `"class"` written by hand or by the editor overrides the rule for that light. The classes in the files
of 2026-11-15 (`source` `classifier` or `fit`) are the older, coarser ones and are ignored here.

A rule:

```json
{ "name": "start room", "class": "prefab:firstroom",
  "path": "(^|/)[^/]*FirstRoom[^/]*/Room/((Pit|Hallway)[^/]*/)?Point Light[^/]*$",   // regex on the hierarchy path, no case
  "notPath": "...", "scene": "Level 6-2",                    // regex the path must not match; regex on the scene name
  "type": "point",                                            // point | spot
  "rangeMin": 12, "rangeMax": 40, "intensityMin": 0, "intensityMax": 8, "saturationMin": 0.5, "saturationMax": 1,
  "signals": ["fixture", "!scripted"],                        // what only the running game knows, "!" = not
  "set": { "intensity": 0.5 } }                               // optional: a policy for the lights of this rule
```

Signals: `fixture` (inside or at a small renderer), `lifted` (an open-fire fixture: docs/relight.md, "Light
fixtures"), `enclosed` (in a static lamp shade), `orb` (a statue orb's), `scripted` (a script on the light drives it),
`animated`. Names in paths carry numbers (`Pit (3)`, `Point Light (1)`): write `[^/]*` after a name.

Classes of the shipped rules: `prefab:firstroom|shop|finaldoor|finalroom|checkpoint|door|pit` (the same objects in
every level), `gameplay` (altars, grapple points, switches, coloured arena lights), `lightning`,
`fixture:torch|orb|window|screen|lamp`, `scripted`, `accent` (a spot with no fixture), `fill` (a point light with no
fixture and a range of 12 m or more), `glow` (the same, smaller). `fixture` names all its kinds wherever a class is
given.

**Policy** (`LightPolicy`), on a class or on one light's entry; every field optional:

| Field | Meaning |
|---|---|
| `intensity`, `color`, `range`, `shadows`, `disabled` | as on an entry ("Format"); `intensity` 0 drops the light |
| `reach` | 1 (default): relight's falloff. 0: never more than the game's own falloff at any distance (the dimmer of the two curves), so what the game's short falloff left dark far from the light stays dark. Steps of 0.25. Not for the player's own lights |
| `surfaces` | `false`: the light does not light surfaces and stays in the volumetric pass ("air only": a fog fill) |
| `medium` | factor on what the light scatters in the air (0: none) |

Where policies are written, weak to strong: `"classes"` in `_rules.json`, `"classes"` in the level's file, a rule's
`"set"`, the light's own entry. Field by field: a level can set `"classes": { "fill": { "intensity": 0 } }` and one
entry can give a single fill its intensity back. An empty entry of the user's (Shift+Keypad 0) still means "the
game's values": no policy applies to that light.

Shipped (2026-11-17, after the user's review of the sheets): the start and final room (their wall strips as the
light, `prefab:firstroom` and `prefab:finalroom` off) in `_rules.json`, and the level policies of 1-4, 3-1, 6-2 and
0-1 ("Levels"). Every other level has the rules only, so nothing looks different there until its policies are
reviewed and shipped (below). Mechanics: reach is packed into `_Lights[i*4+1].w` above the player-light flag (no
buffer changed); air-only lights are packed after every other light, and the surface passes are given the count
without them, so they cost surfaces nothing.

The offline census, `tools/lights/census.py`, applies the path and number conditions of the rules to every light of
all levels from the game's bundles, for writing rules (it cannot know the signals: the game's own census, below, is
the authority). With the rules of 2026-11-17, by name alone: 3,990 enemy, 2,112 fill, 1,412 lamps, 1,349 glow, 1,158
gameplay, 816 torches, 784 accent, 1,119 prefab lights, 211 directional.

## Review: compare before anything ships (2026-11-17)

No policy ships blind. The tools, in the order of use:

| Tool | What it shows |
|---|---|
| Light editor (Insert) | Every light's label carries its class and what RT does with it (red: hidden); the panel names the rule. **Ctrl+Keypad /**: only the selected light's class. **Ctrl+Keypad 0**: that class off / on. **Ctrl+Keypad .**: clear the switches. Parameters `Reach`, `Air only`, `Air` edit one light |
| Bench variants | `lc.class.NAME=0` (or a factor), `lc.solo=NAME`, `lc.light.TEXT=0` (TEXT: part of the path), `lc.emit.TEXT=F` and `lc.glow.TEXT=G[,T]` (emitters, below), `lc.set.CLASS.FIELD=VALUE` (a class policy for that mode: `lc.set.fill.reach=0`), `lc.layer=NAME`. A kind is written with a dot: `prefab.firstroom`. Any number per mode, joined with `+` |
| Proposal layer | `data/lights/proposed/`: `_rules.json` (its rules come first, its class policies replace those of the same name) and level files (in place of the shipped file of that level). Inactive unless a mode says `lc.layer=proposed` |
| A/B sheet | A suite with the modes `off`, `pathtrace`, `pathtrace+lc.layer=proposed` and the two at fixed exposure (`+ae.enabled=0`), then `tools/lights/score.py <run> --sheet`: the game, what ships and the proposal side by side with the scoreboard's numbers. `bench/suites/light-review.json` is the template |
| Census and rule diff | `bench.ps1 ... -Extra "-ultrart-light-census"` writes `lights_<scene>.json` (every light, on or off: class, rule, policy) and one per layer; `tools/lights/rulediff.py <run>` writes `lights_<scene>.md`: the classes of the level with what ships and what is proposed, and one row per group of lights the proposal changes |
| Fixtures | `tools/lights/fixtures.py bench/results`: per level, each fixture's lights and whether it has a glowing part ("Emissive fixtures") |
| Board | `tools/lights/board.py bench/results --since <stamp>`: every level's scored tour in one table, worst first ("Levels") |
| Attribution | Which lights make a pose too bright: the class switches as modes (`bench/suites/light-classes.json`), then `lc.light.TEXT=0` for single lights; the scoreboard's dark delta per mode is the share |

A level's change is: write it in `data/lights/proposed/`, run its tour with the review modes, read the sheet and the
rule diff, and move the files up once the sheet has been seen (the taste-sensitive levels by the user).

First use, the start room of every level (`FirstRoom`: four floating orange point lights, range 12-25 m, intensity
2-8). 1-4's first room at fixed exposure, the game / current / with the lights at half
(`bench/results/20261117-150631_light-classes_alt`, sheet `bench/results/light-classes/firstroom-v2.png`):

| View | current | lights x0.5 | reach 0 | lights off |
|---|---|---|---|---|
| 0 | x1.12, dark +1.5 | x1.07, +0.9 | x1.08, +0.9 | x1.07, +0.5 |
| 1 | x1.38, dark +3.2 | x1.01, +1.9 | x1.16, +2.2 | x0.71, +0.6 |
| 2 | x1.49, dark +16.1 | x0.95, +5.3 | x1.16, +9.9 | x0.59, -1.7 |

Half strength lands on the game (the user, 2026-11-17: "about right"; "emissive should carry where possible").

### Emitters: a glowing surface as the light (2026-11-17, `t3/light-levels`)

"Emissive before lights": where a room has glowing surfaces, they should light it, and the floating lights the game
used instead go. An emissive surface gives little light at its texture's own brightness (the start room's strips:
x0.6-0.7 of the game with the lights off), so an **emitter rule** scales the light one surface gives:

```json
"emitters": [ { "name": "start room wall strips", "material": "^FinalFrame", "texture": "glow", "scene": "Level", "scale": 4.5 } ]
```

in `_rules.json`, a layer's `_rules.json` or a level's file (the level's first; first match wins). `material` and
`texture` are regexes on the surface's material name and its emission texture's name; the census lists both for
every emissive surface of a level (`"emitters"` in `lights_<scene>.json`: triangles, area, mean radiance). Only the
table of emissive triangles the light sampling uses is scaled (`EmissiveLights.LightScaleOf`): the surface looks the
same to the camera and in reflections. The table is rebuilt when the rules change. Bench variant:
`lc.emit.TEXT=F` (materials whose name contains TEXT). Emitter rules follow the overrides switch in every mode.

The start room with its strips as the light and its floating lights off, 1-4's first room at fixed exposure
(`bench/results/20261117-151822_light-probe_alt`, sheet `bench/results/light-classes/firstroom-emissive.png`: the
game, lights x0.5, strips x4):

| View | current | lights x0.5 | strips x4, lights off | strips x8, lights off |
|---|---|---|---|---|
| 0 | x1.12, tint 8.0, dark +1.5 | x1.08, 4.2, +0.9 | x1.08, 4.5, +1.2 | x1.17, 10.6, +3.0 |
| 1 | x1.37, tint 23.2, dark +3.2 | x0.97, 6.6, +1.5 | x0.89, 5.0, +1.5 | x1.27, 8.9, +4.0 |
| 2 | x1.49, tint 23.6, dark +16.2 | x0.95, 12.7, +5.4 | x0.86, 1.9, +3.9 | x1.35, 6.1, +14.5 |

The strips alone match the game's brightness and match its colour better than the orange lights do. Proposal layer
(`data/lights/proposed/_rules.json`): strips x4.5, `prefab:firstroom` and `prefab:finalroom` (the same walls) off.
With strips x5 over six views of 1-4 and 3-1 (`bench/results/20261117-151956_light-review_alt`, sheet
`bench/results/light-classes/review-firstroom-emissive.png`): pathtrace median x1.17 -> x1.05 at shipped settings,
x1.47 -> x1.07 at fixed exposure, dark delta +10.7 -> +7.8. Open: the final room has no scored pose yet, and 3-1's
first view (x1.4, a view out of the room) is other lights.

### Emissive fixtures: the lamp itself as the light (2026-11-17, `t3/emissive-masks`)

The same idea for single fixtures: a lamp's glowing part lights the room and the point or spot light the game put
inside it is switched off. Three pieces.

**Which fixtures can.** The census (`-ultrart-light-census`) now lists, per light, the materials of its fixture (the
nearest ancestor with renderers, at most two levels up, at most 24 renderers) as `name:emissive|unlit|lit`, and
`tools/lights/fixtures.py bench/results [--since <stamp>] [--level "Level 8-2"]` groups a level's fixture lights by
fixture with a verdict: `emissive` (the game marks the part as emissive: an emitter rule's `scale` is enough),
`unlit` (a fullbright part that shows its texture and is no emitter: it needs a mask), `lit` (no glowing part: a
mask on a lit texture, or the light stays), `none` (no fixture). `bench/suites/fixture-census-a.json` and `-b` load
sixteen levels for the census alone (one minute each). Over those levels and 8-2, 2026-11-17: 375 lights in
emissive fixtures, 386 in unlit ones, 616 lit, 1,731 none. What they are:

| Fixture | Levels seen | Glowing part | Verdict |
|---|---|---|---|
| `StreetlightFraud` | 8-2 | `Streetlight` (emissive texture) | scale |
| `OfficeLight`, `SupermarketLamp` | 8-2 | `Ceiling_Light`, `Metal Light 2C` (emissive) | scale |
| `Streetlight` | 1-4, 5-1, P-2 | `Light` (unlit) | mask |
| `ApartmentLamp` (standing lamp) | 8-2 | `Light` (unlit) | mask |
| `Dome` lamps | 5-3, 8-2 | `Basic Color - White NoFog` (unlit) where it is not baked | mask, partly |
| `Glowing Mushrooms`, `OfficeComputer` screens | 5-1, 8-2 | `GlowingShroom`, `ComputerScreen_*` (unlit) | mask |
| `Light` pillars (0-3, 5-1) | 0-3, 5-1 | `LightPillar *` (unlit beam meshes) | beams: `t3/window-beams` |
| `BigTorch`, `Candle`, `Lantern`, `Fire` | 4-3, 5-3, 1-4 | `FireMaterial` flame sprites | the light stays |

Torches and candles have no surface to carry them: their lights stay (the user, 2026-11-17: "not opposed to some
lights where needed"). Parts baked into the level atlas (`Batch Material Environment`) cannot be named by material.
The census also counts 100 pooled `BloodsplatterManager` lights per level as `fixture:torch`: they are not fixtures.

**The mask.** An emitter rule with `glow` makes a surface that is not emissive in the game an emitter
(`SceneProxies.Glow.cs`): its albedo times `glow` is the emission, in the texels whose luminance (linear, 0-1) is
above `threshold` (a soft step of 0.1 either side; 0 = the whole surface, for a bulb or a shade that has its own
material). `texture` is then the main texture's name.

```json
"rules":    [ { "name": "street lamps", "path": "Streetlight[^/]*/[^/]*$", "class": "fixture:lamp", "set": { "intensity": 0 } } ],
"emitters": [ { "name": "lamp bulbs", "material": "^Light$", "glow": 6 } ]
```

The proxy material gets `_EmissiveColor` white, `_EmissiveIntensity` = glow, `_UseAlbedoAsEmissive` and
`_EmissiveThreshold` (`Proxy.shader`), so camera rays, reflections and bounces see the glow, and the emissive
triangle table samples it like any emissive surface (with the threshold applied to its 32x32 copy of the texture:
approximate for masks of single texels). A material with emission of its own keeps it. Plain proxy materials only:
not the level atlas, transparents or the player. On an unlit surface the glow adds to the texture the surface
already shows. Bench variants: `lc.glow.TEXT=G` or `G,T`; `exact.` before TEXT (also for `lc.emit`) takes the whole
material name (`lc.glow.exact.Light=6`: `Light`, not `Streetlight`).

**Proved on three fixtures** (alt install, pathtrace, shipped exposure; ratio to the game, p10 / p50 / p90 regions,
tint; sheets in `bench/results/review/`):

| Fixture, pose | Today (fake lights) | Lights off, nothing else | Emissive | Proposal |
|---|---|---|---|---|
| 8-2 street lamps, pool (`20261117-203720_emissive-8-2_alt`) | x0.93, x0.68 / x0.79 / x1.20, tint 3.0: a hard-edged spot cone the game does not have | x0.74 | heads x4: x0.76, x0.79 / x0.73 / x0.81, tint 1.6; x8: x0.87, tint 4.8; x12: x0.88, tint 5.8 | heads x4 |
| 8-2 standing lamp (same run) | x0.93, x0.79 / x1.24 / x0.80, tint 7.5 | x0.72 | bulb glow 6: x0.81; 20: x1.04, x0.89 / x1.30 / x1.00, tint 5.8; with threshold 0.6 the same | glow 20 |
| 1-4 street lamps (`20261117-204053_emissive-1-4_alt`) | x0.83, tint 6.6, dark +1.6: yellow patches on the arch | x0.78 | bulb glow 6: x1.05, tint 7.5, dark +4.6; 20: x1.42; 60: x1.67 | glow 6 |

The street lamps' pool on 8-2 is even and keeps the yard's pink at x4 (the remaining x0.76 is the yard's ambient, the
same with the lamps off); more than that washes the ground yellow. The standing lamp's bulb sits inside its shade,
so the light leaves through the top and the bottom as it does with the fake light. On 1-4 the lamp's pool on the
grass is the game's, and the yellow patches the fake lights left on the arch are gone. The same material name needs
a different glow per level (6 on 1-4, 20 on 8-2: the bulbs differ in size), so the rules are per level.

Proposals (`data/lights/proposed/Level 8-2.json`, `Level 1-4.json`; off unless `lc.layer=proposed`): review R9. As files
(`bench/suites/emissive-review.json`, `20261117-204345_emissive-review_alt`): 8-2 pool x1.02 -> x0.76 (tint 3.5 -> 1.6),
standing lamp x0.93 -> x1.04, 1-4 lamps x0.82 -> x1.06; the rules take 33 + 8 lights on 8-2 and 18 on 1-4.
Open: `^Light$` on 1-4 also names six orbs' spheres (not checked in view); the per-level application for the other
levels comes with their light proposals; a first rule match was missed once because numbered copies
(`StreetlightFraud (1)`) need `[^/]*` after the name: check `lights_<scene>.proposed.json` for the rule's count.

## Levels (2026-11-17, `t3/light-levels`)

**The board.** `tools/lights/board.py bench/results --since <stamp>` reads the scored checkpoint tours of every level
(`bench/suites/tour/tour-<level>.json`, one launch each with `-Extra "-ultrart-light-census"`) and prints one row per
level, worst first: median and worst ratio to the game, median dark delta and failing views, at shipped settings and
at fixed exposure. First full board, 43 levels, 644 views, 2a44606 (kept locally as
`bench/results/light-classes/board-20261117.md`). The top and the user's levels:

| Level | views | ships: median | worst | fixed exposure: median | worst | dark delta (fixed) |
|---|---|---|---|---|---|---|
| 4-3 | 12 | x1.11 | x1.84 | x2.22 | x20.8 | +3.8 |
| 6-2 | 6 | x1.26 | x1.38 | x2.03 | x2.55 | +9.5 |
| 5-3 | 21 | x1.21 | x1.33 | x1.91 | x3.12 | +18.2 |
| 1-4 | 9 | x1.15 | x1.35 | x1.89 | x5.24 | +28.4 |
| 3-1 | 18 | x1.05 | x1.41 | x1.80 | x4.02 | +5.6 |
| 7-3 | 15 | x1.10 | x1.43 | x1.80 | x7.29 | +50.9 |
| 0-3 | 15 | x1.25 | x7.21 | x1.73 | x7.57 | +18.7 |
| 8-2 | 27 | x1.06 | x1.49 | x1.67 | x4.21 | +32.1 |
| 5-1 | 21 | x1.14 | x1.29 | x1.65 | x2.71 | +15.1 |
| P-2 | 18 | x1.57 | x2.02 | x1.54 | x2.35 | +2.7 |
| 0-1 | 18 | x1.15 | x1.27 | x1.24 | x1.47 | +6.4 |

Auto exposure hides most of the excess (medians x1.0-1.26 at shipped settings), which is why the fixed-exposure
column is the one to rank by: it is the light the level's lights add. 37 of 43 levels are over x1.2 there. Not
measured by a tour: 7-4, 4-S and 2-S (the captures are the same image in every mode: something of the game's covers
the view at their stops), and 7-1 reads x0.46 (dim, not bright: its own case). P-2 is bright at shipped settings by
its exposure exception, which is the user's call to keep (plan, section 8).

The in-game census of the same runs: 21,982 lights in 43 levels (with what levels instantiate), every one with a
class, none `other`. 6,054 enemy, 5,395 torches, 1,972 fill, 1,485 lamps, 1,188 gameplay, 1,129 glow, 863 effect, 798
accent, 736 final room, 303 start room. At a level's start 414 fills are on, more than any other class.

**Which lights.** `bench/suites/attr/attr-<level>.json` switches each class off in turn at fixed exposure, and
`tools/lights/attribute.py <run>` pivots the scores. In all four levels the excess is the floating room lights:
`fill` (a point light with no fixture, range 12 m or more), and in rooms that switch their lights on, the same lights
as `scripted`. Fixtures, glows, accents and gameplay lights change nothing measurable. Examples at fixed exposure:
6-2 Organ Hall x2.42 -> x0.54 with fills off; 3-1 Bridge Arena x3.4 -> x1.0 with scripted off; 0-1 Glass Intro x1.28
-> x0.60 with fills off (there the fills are the room's lighting, not an extra).

**Shipped 2026-11-17** (the user on the four sheets and the start room: "look great"; they were proposals in
`data/lights/proposed/` until then, and the runs below compare them as `lc.layer=proposed` against what shipped
before; sheets under `bench/results/light-classes/`). The proposal layer is empty again, for the next levels:

| Level | Policy | Tour at shipped settings | Tour at fixed exposure | Run |
|---|---|---|---|---|
| every level | start and final room: wall strips x4.5 as the light, floating lights off ("Emitters") | x1.17 -> x1.05 (six views) | x1.47 -> x1.07 | `20261117-151956_light-review_alt` |
| 1-4 | fill `reach` 0, x0.4; fixtures `reach` 0; street lamps x0.5 | x1.18 -> x0.98, dark +5.9 -> +3.1 | x1.89 -> x0.93, dark +28.3 -> +9.5 | `20261117-153226_review-1-4_alt` |
| 3-1 | fill `reach` 0, x0.5; scripted `reach` 0, x0.35; the opening's fill `reach` 0 only; Uphill Battle and Structure unchanged | x1.05 -> x0.99 (bright views 10 -> 5 of 21) | x1.85 -> x1.19, dark +4.6 -> +2.5 | `20261117-182747_review-3-1_alt` |
| 6-2 | fill and scripted `reach` 0, x0.4 | x1.26 -> x1.00, dark +3.7 -> +1.6 | x2.06 -> x1.01, dark +9.6 -> +1.7 | `20261117-183139_review-6-2_alt` |
| 0-1 | fill and scripted `reach` 0, x0.9 (on top of the fitted entries of 2026-11-15) | x1.15 -> x0.98 at x0.8; x0.9: the two brightest rooms x1.04-1.16 -> x0.91-0.97 | x1.24 -> x1.02 at x0.8 | `20261117-183254_review-0-1_alt`, `20261117-183733_tune2_alt` |

How the strengths were found (`20261117-182041_tune-levels_alt`: `reach` 0 alone, with x0.5, with x0.25, per view):
`reach` 0 alone brings the start rooms and 0-1 to the game; the big halls need the factor as well. 1-4's street lamps
stay at x0.5: at x0.65 and x0.8 the lamp pose is no brighter (x0.83-0.84, the cap on their reach decides it) and the
far wall the game leaves dark comes back (bridge pose reveal 0.2 % -> 2.3 % -> 8.6 %).

Open after these runs:

- **1-4**: the two poses of the user's (lamps, bridge) pass the dark test but read a little dim (x0.84, x0.87);
  rectangular hot spots on the walls behind the street lamps where the game has a soft pool; two Arena views still
  x2.1-2.4 at fixed exposure.
- **3-1 Big Arena** is dim before any change (x0.74-0.82 at fixed exposure; x0.61-0.68 proposed): not the fills.
- **3-1 Stairways.** At the checkpoint the room is unlit in both renderers: its lights come on when the player walks
  in (a tour stop can now do that: `"Step": 20`, bench/README.md). Lit, the room is the eye's beam in thick orange
  haze, and none of the classes above change it (`bench/results/light-classes/3-1-stairs-lit.png`). The game fills
  the whole room with orange haze (its `LightPillar 1F NoFog` meshes; `RenderSettings.fog` is off) and hides the
  far wall; RT has a brighter pool under the beam (x1.66 at fixed exposure), almost no haze 40 m in (x0.47-0.65) and
  shows the far wall. That is the beam channel's balance (docs/volumetrics.md), work for `t3/window-beams`.
- **6-2 Organ Hall**: with the fills down, an orange patch on the wall by the terminal that the game does not have
  (tint error 2-6 -> 9-17 at shipped settings).
- **0-1 First Room** (the short starting room, not the prefab): one view x0.61-0.65 in every variant: not lights.
- The final room has no scored pose.

### Levels, second batch: 4-3, 5-3, 7-3, 0-3, 8-2, 5-1 (2026-11-17, `t3/light-levels-2`, proposals)

The next six of the board. The fill policy of the first four does not carry over: with fills and scripted lights
off, most of their bright rooms stay bright (`20261117-204658_tune-a_alt`, `20261117-205324_tune-b_alt`). Class
switches on those rooms at fixed exposure (`20261117-210340_attr2-a_alt`, `20261117-210942_attr2-b_alt`,
`20261117-211546_attr3_alt`; `lc.solo=none` = every level light off):

| Level | Room (tour stop) | ships | the class that carries it, off |
|---|---|---|---|
| 4-3 | Generator Room Hallway | x4.5 | fixture (torches): x0.36 |
| 4-3 | Stairs Hallway | x3.0 | fixture: x1.5 |
| 4-3 | First Room | x1.5 | item (the altar's torch): x0.79 |
| 4-3 | L Hallway | x1.6 | none: x1.64 with every light off (not a light) |
| 5-3 | Dual Wield Arena, Traitor Hallway | x1.9 | fixture: x0.79, x0.34 |
| 5-3 | Hologram Room, Residence Corridor | x2.6, x2.2 | glow: x2.1, x0.64 |
| 5-3 | First Room, Lounge Bar, Staircase | x1.4-2.3 | fill and scripted: x0.9, x1.0, x0.43 |
| 7-3 | Central Plaza Upper, Circular Garden, Forest Path | x5.2, x6.6, x1.8 | **player** (the flashlight): x0.25, x0.20, x0.28 |
| 7-3 | Grand Hall | x1.8 | fill and scripted: x1.3 |
| 0-3 | two rooms | x1.8-2.0 | fill and scripted: x0.84-0.94 |
| 0-3 | two rooms | x1.6 | fixture: x0.90-0.96 |
| 8-2 | Elevator to Windtunnel, Shaped Hallway | x1.5-3.0 | fill and scripted: x0.7-1.5 |
| 8-2 | Storage Room, Supermarket Shutters | x1.7, x1.9 | fixture (office and supermarket lamps): x1.2 |
| 8-2 | Mirror Reaper Arena | x2.4 | effect: x0.72 |
| 5-1 | First Encounter | x2.0 | fixture: x0.84 |
| 5-1 | Tunnel B, Tunnel C | x1.8 | accent: x1.0 |

7-3 is its own case: the game's flashlight lights a spot on the far wall; path traced, its bounce fills the white
halls. Nothing but its strength helps (a class policy on `player`, which also dims the small light at the player).
The bench variant for the sun is `lc.class.sun=0` (there is no class `directional`).

Strengths (`20261117-211947_tune-a_alt`, `20261117-212553_tune-b_alt`: every level light's `reach` 0 with the
fixture, accent, glow, effect and item classes at x1, x0.5, x0.25 and fills at x0.35), then the proposals as files,
tour medians (`bench/suites/review/review-<level>.json`; sheets `bench/results/review/review-<level>.png`):

| Level | Proposal (`reach` 0 on all of them) | shipped exposure | fixed exposure | dark delta (fixed) | Run |
|---|---|---|---|---|---|
| 4-3 | fill, scripted x0.35; fixture, accent, glow, effect, item x0.5 | x1.08 -> x0.95 (bright views 5 -> 0 of 12) | x1.97 -> x1.45 | +5.5 -> +1.7 | `20261117-213515_review-4-3_alt` |
| 5-3 | x0.35; x0.7 | x1.24 -> x0.93 (bright 18 -> 0 of 21, dim 0 -> 8) | x2.05 -> x0.96 | +16.0 -> +7.2 | `20261117-213515_review-5-3_alt` |
| 7-3 | x0.5; x0.7; player x0.6 | x1.02 -> x0.90 (bright 7 -> 3 of 15) | x1.72 -> x1.57 | +26.5 -> +19.3 | `20261117-215227_review-7-3_alt` |
| 0-3 | x0.35; x1 | x1.22 -> x1.01 (bright 13 -> 6 of 15) | x1.76 -> x1.08 | +18.5 -> +8.9 | `20261117-214151_review-0-3_alt` |
| 8-2 | x0.35; x0.5; street and standing lamps by their glowing parts ("Emissive fixtures") | x1.08 -> x1.05 (bright 11 -> 9 of 27) | x1.66 -> x1.14 | +31.6 -> +17.3 | `20261117-214151_review-8-2_alt` |
| 5-1 | x0.5; x1 | x1.15 -> x0.90 (bright 11 -> 3 of 21, dim 3 -> 10) | x1.60 -> x1.18 | +14.3 -> +8.8 | `20261117-215227_review-5-1_alt` |

A first, stronger pass for 7-3 (player x0.35, others x0.5) and 5-1 (others x0.7) matched the game at fixed exposure
(x1.47, x1.00) but read dim at shipped exposure (x0.84, x0.80: `20261117-213515_review-7-3_alt`,
`20261117-214151_review-5-1_alt`), so both were softened. Open: 5-3 and 5-1 trade bright views for dim ones (8 and
10); 7-3 stays far over the game at fixed exposure (the flashlight's bounce) and auto exposure carries it; 4-3's L
Hallway and Stairs Hallway (x1.5-1.6 with the lights off) are not lights; 8-2's Elevator stays x1.6.
All six are proposals (`data/lights/proposed/`, review R10); nothing ships.

## Area lights (shader)

Area lights keep the 4 x float4 per-light layout, so no buffer, UAV or payload changed (still 8 UAVs in PathTrace, 64-byte
payload untouched):

| Slot | Point / spot | Area |
|---|---|---|
| `[2].xyz` | forward | emission normal |
| `[2].w` | soft-shadow radius | half-size x (sphere / disc radius) |
| `[3].x` | cos(spot / 2), -1 for points | `-2 - half-size y` (<= -2 marks an area light) |
| `[3].y` | spot sharpness | shape (1 sphere, 2 rect, 3 disc) \| octahedral tangent (2 x 10 bits) << 2, an exact integer |

Every spot test in the code (`l3.x > -0.5`, the cone `saturate((cos - l3.x) * l3.y)`) reads "no cone" for these values, so
consumers that don't know area lights (volumetrics' scattering, the hybrid ratio) treat them as point lights at the
centre.

- **Evaluation** (`LightEvalRaw`): the light is evaluated at the point of its shape closest to the receiver, with the
  game's falloff (or relight's physical one) from there, times the emitter cosine for rect and disc (one-sided; a
  sphere emits everywhere). It is deterministic, so RIS, ReSTIR (reservoirs store the light index, as before), the
  reference sampler, pathtrace's NEE and the radiance cache use it unchanged. A long rect lights a corridor evenly along
  its length, like a fluorescent tube; a downward panel leaves the ceiling to the bounce.
- **Shadows** (`LightVisible`): the shadow ray aims at a uniform random point on the shape (spheres: the half facing the
  receiver), so penumbrae are as wide as the emitter. Fixture clearance (0.35 m) and the player-light flag apply as for
  points.
- **Brightness**: straight in front of the emitter an area light is as bright as the point light was at the same
  distance; its total output grows with its size. Re-balancing is the fit's job (below).
- **Cost**: a few ALU per evaluation (closest point, one decode); no extra rays, but shadow rays to random points on a
  large emitter are less coherent. The alias table weights area lights like points (luminance x range). Measured
  (`bench/suites/lights-perf.json`, exclusive, RTX 5070 Ti, 2560x1440, scale 0.5, modes interleaved every 0.75 s; GPU
  total / dispatch ms, with / without overrides):

  | Pose | with | without |
  |---|---|---|
  | 0-1 metal, area test file (4 spheres 0.5 m, a 2 m rect, an added 1 m panel) | 6.82 / 4.72 | 6.12 / 4.05 |
  | 7-3 tree room, shipped (intensity / colour only), relight | 6.92 / 4.67 | 6.91 / 4.67 |
  | 7-3 tree room, shipped, pathtrace (RR) | 6.36 / 3.51 | 6.35 / 3.50 |

  So intensity / colour / disable entries are free on the GPU; area lights cost about 0.1 ms each in view at these
  sizes (mostly the incoherent soft-shadow rays). CPU (`lights` section): 0.084 against 0.060 ms per frame, plus a
  one-off ~6 ms frame the first time a level's overrides are resolved (JIT and the per-light lookup cache).

  These run-time on/off comparisons use the same compiled shaders both ways, so they could not see that the area-light
  code itself (compiled into every light evaluation and shadow ray) cost ~1.1 ms of relight dispatch in every level;
  fixed on branch t3/perf-1440p (docs/performance.md, "The light-overrides merge regression").

## In-game editor

**Insert** toggles it (free key; Ctrl+Insert runs the classifier). Off during benchmarks. Numpad while it is open:

| Key | Action |
|---|---|
| 5 | select the light under the crosshair (within 12 degrees, nearest wins) |
| 4 / 6 | previous / next light, by distance (active lights within 80 m, directional lights last, then added lights) |
| 8 / 2 | previous / next parameter: intensity, red, green, blue, range, shadows, disabled, shape, width, height, move x/y/z, reach, air only, air |
| Ctrl + / , Ctrl + 0, Ctrl + . | class switches: only the selected light's class; that class off / on; clear ("Review") |
| + / - | change it (hold to repeat; Shift: a fifth of the step). Intensity and range x1.1 per step, colour 0.05, sizes and moves 5 cm |
| 7 | rotate a rect 15 degrees about its normal (Shift: back) |
| 9 | point the emission normal along the view direction |
| 1 | add a 0.5 m rect light on the surface under the crosshair, facing out |
| 3 | delete the selected added light (a shipped one is disabled in your file) |
| 0 | drop your edit of the light (the shipped entry applies again); Shift+0: back to the game's values |
| / | solo: only the selected light (and the directional lights) |
| * | all overrides on / off (A/B) |
| . | labels of the other lights on / off |
| Enter | save now (edits also save 2 s after the last change) |

Switching a light to an area shape sizes it like its fixture (`SceneProxies.FixtureBounds`): a rect or disc on the
housing's bottom face, facing down. The gizmo shows the selected light's range circle (the game's light fades out at 2x),
spot cone, area outline and emission direction, a line from the game's position if it was moved, and crosses with names for
the lights around (orange: has an override, cyan: added). The panel lists the game's values, the entry's origin, class,
confidence and note.

Settings menu, group "Light editor": the overrides switch, the editor switch, and the selected light's intensity,
colour, range, shadows, disabled, shape and size (session only; the values live in the JSON), plus buttons for
"select the light at the crosshair", "next light" and "save".

## Classifier (stage b)

(2026-11-17: the class of every light now comes from the rules, "Classes and rules". This tool remains for what
needs geometry: area shapes on glowing parts, the emissive dedupe, lights in walls. Its class names are the older
ones.)

`-ultrart-classify-lights` (once per level, 4 s after load) or Ctrl+Insert. It looks at every light of the level the RT
scene tracks (active or not: rooms are off at load), the emissive triangles (`EmissiveLights.WorldTriangles`), the
fixture data and physics colliders, and writes `BepInEx\ultrart\lights\classified\<scene>.json` (the fixes) and
`<scene>.report.md` (counts per class, every change with its confidence, notes on what was left). It never changes
anything by itself; a reviewed copy is shipped as `data\lights\<scene>.json`.

| Class | Rule | Fix |
|---|---|---|
| player/effect, enemy/item | not part of the level at build, under the player or camera, under an `EnemyIdentifier` / `ItemIdentifier`, spawn effects | none |
| lava | over active lava of the same colour (`DimLavaLights`) | none (the lava system dims them) |
| fixture:torch | in an open fire fixture (torch, brazier, candle...) | none: the fixture lift puts it above the bowl (docs/relight.md) |
| fixture:lamp / window / screen | inside a small renderer (fixture data or a static lamp shade) or next to renderers named like one | glowing part found (emissive triangles in the fixture): an area light the shape of that part (flat: rect or disc on it, facing out; a closed shell: a sphere), moved onto it. Housing named as a lamp, light in its upper part, no glow: a disc / rect on its bottom face, facing down (confidence 0.45). Lamp shades (static batches): none |
| fixture:orb | inside a ball (statue orbs) | none |
| emissive | next to an emissive surface but no fixture | dedupe: when the surface's own emissive light is at least 0.75x the light's, intensity x(1 - 0.5 ratio), 0.5-0.85 |
| fill | no fixture or glow within reach, range >= 12 m, intensity <= 1.2, >= 1 m from walls | intensity x0.5 (confidence >= 0.7) or x0.7: relight and pathtrace both have bounce (radiance cache, real paths), which the fill faked |
| (any) wall | inside level geometry or within 0.15 m of a wall, open room on the other side | moved 0.1-0.4 m into the room |

Tags in the report: `off-at-load` (switched on by a trigger), `scripted:<component>` (a script that drives the light:
flicker, fades, switches), `animated`, `taste` (secret rooms, boss arenas, 7-3, P-2: fill lights there are not dimmed and
the report flags them for the user).

Comparing emitter and light (dedupe): the light's radiant intensity in relight is about `K r^2 (colour x intensity x
1.5)^2.2` (linear transport, light reference 0.3: K = 0.00673); a glowing surface's is `area x Le^2.2 x lightScale / pi`.

## Fit to the game (stage c)

A bench scenario with `"LightFit": true` (`LightFitLights`, default 12) measures, at its first screenshot time, in relight:

1. **Statistics.** `Exposure.compute` (with `_Params6.w`) sums, over the same near pixels as the game match, the RT
   colour in linear units (`albedo^2.2 x keep x lighting + reflection^2.2`, pre-exposure) and the game's colour decoded
   to linear. The RT sum is linear in every light's intensity, unlike the colour match's display-unit sums.
   `AutoExposure.FitTrace` reads both back every frame (`_Exposure[4]`, `[5]`).
2. **Shares.** The lights whose reach (2 x range) touches the view are ranked by the RIS weight; the top N are switched
   off one at a time (`_fitSkip`), with the denoiser, light sampling and radiance cache reset and 50 frames to settle,
   then 16 frames averaged. A light's share A = base - without (its direct light and its bounce). The base is measured
   again at the end; the difference is the measurement's noise and drift.
3. **Solve** (per level, over all its fitted poses so far): per light, linear rgb factors m minimise
   sum over poses ((base + sum (m - 1) A - game) / lum(game))^2 + 0.03 sum (m - 1)^2 + 0.15 sum (m - mean m)^2,
   with m clamped to [0.5^2.2, 2^2.2] (0.5x-2x in the game's gamma units). Coordinate descent on the convex quadratic.
   Relative errors weigh dark and bright poses alike; the hue term keeps lights from changing colour unless the
   room's colour needs it. Lights with less than 2 % of a pose's light are left out.
4. **Output**: `BepInEx\ultrart\lights\fit\<scene>.json`, the level's current entries with the fit multiplied in
   (intensity = mean of the gamma factors, colour = the factors over it), and `<scene>.fit.md` (per pose: game, RT
   before, RT predicted, the drift; per light: the factor and its share at each pose).

Only room averages are fitted and only whole lights are scaled, so shadows, falloff and contrast stay as traced.
Auto exposure: between the dark key and the key, relight's exposure is 0 EV, so a room's brightness is its lights'
(the game match corrects only part of a difference); brighter scenes are pulled to the key anyway, where the fit only
moves colour.

## Per-level status (2026-11-15)

Shipped: `data/lights/Level {0-1,1-1,2-2,3-2,6-1,7-3}.json` = the classifier's fixes plus the fit of the poses in
`bench/suites/lights-fit-a.json` / `lights-fit-b.json` (`tools/lights/merge.py`), and `Level 5-2.json` (hand-written, below).
Other levels have no file (no change).

### Classifier (lights tracked; fixes)

| Level | Lights | point | fixture (torch / lamp / orb / named) | fill (dimmed) | emissive (dedupe) | enemy/item, player/effect | Fixes |
|---|---|---|---|---|---|---|---|
| 0-1 | 503 | 267 | 0 / 3 / 8 / - | 31 (19) | 1 (0) | 188 / 3 | 19 |
| 1-1 | 473 | 227 | 44 / 3 / 8 / - | 5 (4) | 1 (1) | 171 / 9 | 5 |
| 2-2 | 401 | 218 | 14 / 40 / 2 / - | 10 (10) | 1 (1) | 102 / 9 | 11 |
| 3-2 | 250 | 204 | 0 / 2 / 0 / - | 25 (25) | 2 (2) | 6 / 9 | 27 |
| 6-1 | 524 | 320 | 2 / 6 / 0 / - | 40 (21 after the named-fixture rule) | 4 (1) | 139 / 9 | 22 |
| 7-3 | 310 | 173 | 7 / 2 / 0 / - | 5 (0: taste) | 1 (1) | 101 / 11 | 1 |

(Run of 2026-11-15 03:1x, before the "named fixtures are never fills" rule; `merge.py` applies that rule to the shipped
files: 6-1's chandeliers lost a wrong x0.5.) No area conversions survive in these six levels: the glass of street lamps,
statue orbs and lanterns is not an emissive surface in the RT scene (fullbright or lit art), so the "glowing part" rule
finds nothing, and the "housing" rule is limited to housings named as lamps. `enemy/item` is large because pickable
torches, skulls and enemies' lights carry `ItemIdentifier` / `EnemyIdentifier`; they are left alone either way.

### Fit and verification

Screen mean luma over the image minus HUD and viewmodel, relative to the game (`tools/lights/sheet.py`; runs
`20261115-032006_lights-verify(-pt)`, relight / pathtrace with DLSS RR, same build, modes interleaved per scenario):

| Pose | before (no overrides) | after | tint error before / after | fit prediction (near px, display) |
|---|---|---|---|---|
| 7-3 tree room tint | x1.87 | **x1.48** | 12.1 / 11.0 | 1.92 -> 1.40 |
| 7-3 tree room tint (pathtrace) | x1.90 | **x1.55** | 17.7 / 16.3 | |
| 7-3 tree a | x1.55 | **x1.28** | 7.6 / 9.0 | 1.84 -> 1.33 |
| 1-1 indoor a (torches) | x1.60 | **x1.16** | 2.5 / 3.5 | 1.84 -> 0.99 |
| 1-1 indoor a (pathtrace) | x1.53 | **x1.24** | 4.2 / 2.1 | |
| 0-1 metal | x1.18 | **x0.96** | 23.9 / 20.5 | 1.52 -> 1.00 |
| 2-2 floor noise purple | x1.19 | x1.15 | 3.2 / **1.8** | 1.41 -> 1.25 |
| 3-2 eye | x4.40 | x3.96 | 29.8 / 28.6 | 1.43 -> 1.01 |
| 3-2 wall edges | x2.25 | x2.12 | 5.6 / 6.1 | 1.72 -> 1.01 |
| 6-1 coffins | x1.42 | x1.38 | 4.0 / 4.4 | 1.16 -> 1.23 |

What moved:
- **7-3**: the two white `FightLights` spots carry 60 % of the tree room's light; both go to the 0.5x bound, the tree's
  red light stays (x0.86, slightly redder). The walls read red instead of white. Still 1.5x: the bound stops it (the fit
  wanted less), and some of the rest is haze.
- **1-1 torch rooms**: `BigTorch/Fire` x0.6 and a little less saturated (linear transport saturates coloured lights:
  the game's torch rooms are less orange than relight's).
- **0-1**: the statue-orb lights x0.6-0.9 (fitted before the orb light x0.5 above existed: the effective factor on the orbs is now that times 0.5).
- **5-2** (hand-written, 2026-11-15): the Ferryman's cabin fill light unshadowed (docs/relight.md, "V1 beside a light").
- **2-2**: the blue street light is pushed purple (1.27, 0.53, 1.20): the frame-wide colour match does less work.
- **3-2 and 6-1**: at the near pixels the fit lands on the game (3-2: 1.43 / 1.72 -> 1.01), but the screen barely moves:
  their excess is volumetric haze and far (fogged) pixels, not the lights. Next lever: volumetrics balance there.
  6-1's lit surfaces are already within 1.16x of the game at the near pixels.

Game match and colour match do less work (exposure log lines, same runs): 7-3 tree room correction -0.32 -> -0.08 EV,
colour gain (1.42, 0.76, 0.46) -> (1.22, 0.89, 0.54); 2-2 (1.16, 0.61, 1.09) -> (1.08, 0.76, 1.04); 3-2 wall edges
-0.41 -> -0.16 EV; 1-1 indoor a -0.19 -> 0.00 EV (its gain went from 1 to (1.02, 1.02, 0.82): the fit's de-saturated
torches are corrected back a little); 0-1 metal -0.07 -> 0.00 EV.

Measurement quality: the base measured again at the end of a pose differed by 0-14 % (7-3 tree room 13.9 %: the
radiance cache converging after the resets); shares are taken against the base interpolated to their measurement.
The fit poses are the verification poses (no hold-out set yet).

### Area lights

No shipped level uses them yet. Tested with a hand-made 0-1 file (`lights-area-test.json`: four statue orbs as 0.5 m
spheres, one as a 2 m x 0.3 m downward rect, an added 1 m blue panel): relight 62.4 (RIS), 61.6 (ReSTIR), 61.8
(reference) mean luma, pathtrace 57.3; soft shadows, no artifacts.

### Taste calls (flagged, not "fixed")

- 7-3: the forest is meant to be dark; the fit only touched the tree room's lights (the user's report), fill lights
  in 7-3 and P-2 are never dimmed. A lower bound than 0.5x on the white spots would get the tree room closer.
- Secret rooms and boss arenas (`taste` tag): fills not dimmed.
- 1-1's torch de-saturation: matches the game's mean colour but makes fire slightly less orange than relight had it.
- 3-2's eye room and 6-1: the remaining brightness is haze, not lights.

## Scoreboard (2026-11-17)

How brightness is compared with the game from now on (docs/plan-look-and-perf.md, workstream A). The earlier numbers
on this page are screen means; a mean cannot see the most common complaint, light on what the game hides: 1-4's lamp
pose has the game's mean (x0.96) while its far wall, door and trees, black in the game, are plainly visible.

`tools/lights/score.py <run> [--sheet] [--board bench/results/scoreboard.csv]` scores every RT screenshot of a bench
run against the game's frame (`off`) of the same pose, over the screen minus HUD and viewmodel:

| Column | Meaning | Limit |
|---|---|---|
| ratio | mean luma, RT / game | 0.9-1.1 |
| tint | sum of abs(rgb share - the game's) x 100 | (reported) |
| dark % | share of the frame the game leaves at luma 8 or less | |
| dark game -> RT (delta) | mean luma of exactly those pixels in both frames | delta 2.0 |
| reveal % | share of the frame that is dark in the game and at least 6 levels brighter in RT, on 4x4 block means | 0.5 |
| p10 / p50 / p90 | luma percentiles, RT / game | (reported) |

`score.md` and `score.csv` go into the run's folder, `--sheet` adds `score.png` (game and every mode side by side with
the numbers, failures in red), `--board` accumulates rows over runs for trends. The limits were set on the user's
examples of 2026-11-17: 1-4 lamps, the 1-4 bridge and the 6-2 hall fail, 3-1's beam room passes.

Two conditions, always both. **Fixed exposure** (the mode variant `ae.enabled=0`) judges the lights: with auto
exposure following the game (docs/relight.md, "Game match"), a room whose lights are too strong is hidden by the
exposure, and its dark corners are then *darker* than the game's. **Shipped settings** judge the result.

Poses:
- `bench/suites/bright-core.json`: nine poses reached through approach routes (the shared start room, 0-1 metal,
  1-4 lamps and bridge, 3-1's beam room, 6-2 start and hall, P-2 vent smoke and street), in pathtrace, the
  pre-2026-11-17 exposure policy, relight, and fixed exposure;
- **tours**: `bench/suites/tour/tour-<level>.json` for all 43 campaign levels (written by
  `tools/lights/tour_suites.py`): every entry of the game's Teleport Menu cheat (the first room and each checkpoint),
  three views each, no route needed (bench/README.md, "Tour");
- any other suite with `off` among its modes.

First boards (2026-11-17, after the exposure change of docs/relight.md "Follow the game"; median over the views,
pathtrace, shipped settings / fixed exposure):

| Run | views | ratio shipped | ratio fixed exposure | dark delta shipped / fixed | passes shipped |
|---|---|---|---|---|---|
| `bright-core` | 9 | x1.14 | x1.63 | +5.1 / +7.8 | 1 |
| tour 1-4 (first room, Arena, Main Hall) | 9 | x1.15 | x1.92 | +6.2 / +28.2 | 1 |
| tour 6-2 (first room, Organ Hall) | 6 | x1.27 | x2.03 | +4.0 / +9.4 | 0 |
| tour 3-1 (first room and five checkpoints) | 18 | x1.03 | x1.57 | +1.6 / +5.6 | 6 |

The fixed-exposure column is the work list for the lights: 1-4's Arena reads x2.1 to x5.2 of the game there (its
walls and floor, black in the game, are fully lit; 37-70 % of the frame "revealed"), the Main Hall's statue wall is
+47 on the game's dark pixels even with auto exposure. The exposure hides the mean, not the distribution. 3-1's
Bridge Arena and Stairways, nearly black in the game (luma 4-10), read x2.3 to x4 at fixed exposure. Two runs of the
3-1 tour gave the same medians (x1.03 / x1.56 and x1.03 / x1.57); two of 6-2 agree at shipped settings (x1.26,
x1.27) and less at fixed exposure (x1.84, x2.03). A tour hides the level title the game types out after a teleport;
a level's own subtitles (6-2's) stay in the frame, in every mode alike.

## Not done yet

- Policies per level (`t3/light-levels`): nothing but the rules ships yet; the start room's is proposed.
- Emissive first beyond the start room: emitter rules per level; lamp glass painted into lit textures needs emissive masks (materials work).
- The fit with a lower bound of 0, class priors and the dark-region term (plan A4): the fit is unchanged.
- Sky portals at windows (the format's `kind` field is there; no runtime sky colour, no sampling change).
- Auto-generated camera poses around rooms (the fit uses saved bench poses only).
- More levels: the fit is clearly better in 7-3, 1-1 and 0-1, neutral in 2-2 / 3-2 / 6-1 on screen.
