# Lighting overrides (per-level light re-authoring)

ULTRAKILL's lights were placed for per-vertex lighting without shadows: point lights inside lamp housings, invisible fill
lights faking bounce, radii and colours tuned around fog, lamps and windows with no light behind them. Relight and
pathtrace trace those lights with shadows and real bounce, so some of them now leak, self-shadow or double count. The
RTX remasters got their largest visual gains from re-authoring lights. This is UltraRT's version: per-level override
files, an in-game editor to review and tweak them, and two automatic passes that write them (a classifier for
mechanical fixes, a fit to the game's room brightness and colour).

Code: `src/UltraRT/LightOverrides.cs` (data, matching, packing), `Plugin.LightOverrides.cs` (runtime + editor),
`Plugin.LightClassifier.cs`, `Plugin.LightFit.cs`; shader side `UltraRTLights.hlsl` (area lights) and `Exposure.compute`
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
  "lights": [
    {
      "path": "2 - Courtyard/Lamps/Lamp (3)/Point Light",  // hierarchy path, "(Clone)" removed
      "pos": [12.5, 4.2, -30.1],                           // the light's position when first seen (identity)
      "intensity": 0.8,          // x the game's colour x intensity (gamma units, like Light.intensity)
      "color": [1.0, 0.9, 0.8],  // per-channel multiplier
      "range": 1.2,              // x range
      "shadows": false,          // force on / off (absent: as the game / relight decide)
      "disabled": true,
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
| 8 / 2 | previous / next parameter: intensity, red, green, blue, range, shadows, disabled, shape, width, height, move x/y/z |
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

## Not done yet

- Sky portals at windows (the format's `kind` field is there; no runtime sky colour, no sampling change).
- Auto-generated camera poses around rooms (the fit uses saved bench poses only).
- More levels: the fit is clearly better in 7-3, 1-1 and 0-1, neutral in 2-2 / 3-2 / 6-1 on screen.
