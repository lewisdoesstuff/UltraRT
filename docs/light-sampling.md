# Light sampling (RIS; temporal ReSTIR DI until 2026-11-19)

Direct lighting with a fixed number of shadow rays per pixel, independent of the light count.

Files:
- `unity/UltraRT.Shaders/Assets/UltraRT/Shaders/UltraRTLights.hlsl`: the light model (moved out of `PathTrace.raytrace`), the reference `DirectAll` and RIS (the reservoirs and temporal reuse went with ReSTIR on 2026-11-19). Included by `PathTrace.raytrace`.
- `src/UltraRT/LightSampling.cs`: settings, the per-frame alias table and fill-light list, and light-set change detection (until 2026-11-19 also the light index remap between frames and the ping-ponged reservoir buffers).

## Why

`DirectAll` shoots one shadow ray to every light in range of the pixel. Level 8-3 has 100+ active point and spot lights, so `DispatchRays` costs about 2 ms at 1280x720 on an RTX 5070 Ti, against about 0.5 ms elsewhere.

## Modes (`LightSamplingSettings.mode`, shader `_LightSampling`)

| Mode | Primary hit | Bounce hit | Shadow rays per pixel (primary + bounce) |
|---|---|---|---|
| `Reference` (0) | `DirectAll`: every light, one ray each | one uniformly picked light, ×N | lights in range + 1 |
| `Ris` (1, default since 2026-11-15) | RIS over `risCandidates` (16), 1 ray | RIS over `bounceCandidates`, 1 ray | 1 + 1 |
| ~~`ReSTIR` (2)~~ | removed 2026-11-19; was RIS over `candidates` (8), then merged with the previous frame's reservoir, 1 ray | as `Ris` | 1 + 1 |

`mode` is set by F6 (cycles Reference / RIS), the settings menu, `-ultrart-lights ref|ris` and bench `lights=`. Why RIS is the default: [Bias and noise](#bias-and-noise-2026-11-15).
The menu's config key is `Quality.LightSampler` (renamed from `LightSampling`, which pinned ReSTIR in existing configs).
`candidates` (8) is still the count for the radiance cache's update and the other shaders bound through `BindTables`.

### Removed: ReSTIR and the hybrid mode's sampler (2026-11-19)
ReSTIR (`mode` 2) and the hybrid mode, whose own `hybridMode` used ReSTIR with the old visibility reuse and spatial reuse, were removed on 2026-11-19 ([cleanup-audit-2026-11-19.md](cleanup-audit-2026-11-19.md)). RIS had been the default since 2026-11-15 and measured unbiased and quieter (below). What went: the reservoir buffers (`_ReservoirPrev` / `_ReservoirCur`, so PathTrace has a free UAV slot), temporal and spatial reuse, visibility reuse and visibility memory, the light-change list and index remap, the occluded-history cap and re-test, fresh-pixel candidates, and the flags and bench keys for them (`-ultrart-restir-*`, `ls.historyLimit`, `ls.spatialSamples`, `ls.visibilityReuse`, `ls.visibilityMemory`, `ls.occludedHistory`, `ls.occludedRetest`, `ls.freshCandidates`, `ls.perPixelLightChange`, `ls.lightChangeKept`, ...). RIS output is unchanged by the removal (the same random sequence).

Fallbacks: `-ultrart-lights restir` and bench `lights=restir` log a warning and use RIS; a config file holding `LightSampler = ReSTIR` is reset to the default (RIS) by BepInEx, because the default is the first acceptable value. Unknown `ls.<field>` bench keys only log a warning, as before.

The sections from "Temporal reuse" to "Visibility memory" below describe ReSTIR and stay as its history.

`Reference` is the old code path: the same math and the same random sequence. Use it for A/B comparisons and as ground truth with `-ultrart-accumulate`. (ReSTIR's temporal reuse was correlated across frames, so accumulating it converged to a slightly different, biased image.)

## Light model

`LightEval(i, p, n, layer)` is the old `LightFrom` without the visibility test: culling mask, `N·L > 0`, attenuation `1 / (1 + d²·25/r²) · spot · saturate(2 − d/r)`, and the same `atten < 1e-3` cut-off. It returns the unshadowed contribution `c`, the shadow direction and the shadow ray length. `LightFrom` is `LightEval` plus `Visible` for shadowed lights, and `DirectAll` sums `LightFrom`, as before.

**Target function** p̂(i) = luminance(c), so the resampling follows the game's own falloff, cone, culling mask and N·L.

**Fill lights** (`_Lights[i*4+3].z > 0.5`, the directional lights from below the horizon) are unshadowed and only a few, so they skip RIS. `FillLighting` evaluates all of them at every hit, with no rays. Shadowed directional lights do go through RIS: their p̂ is their colour times N·L, with no distance falloff.

## Candidate generation (RIS)

Each frame the CPU builds a Vose **alias table** over the shadowed lights. Drawing a candidate costs one 16-byte read: entry `(threshold, light | aliasLight << 16, pdf(light), pdf(aliasLight))`.

The source pdf is `(1 − uniformMix) · w_i / Σw + uniformMix / N`, with:
- `w_i = luminance(colour·intensity) · R² / (R² + max(0, |camPos − lightPos| − R)²)`, where `R = 2·range`, the radius at which ULTRAKILL's fade reaches zero. Directional lights use `w = luminance`.
- With `importance = false`, the source pdf is uniform.
- The uniform mix (default 0.2) keeps every light reachable. This matters for lights far from the camera that still light a distant visible wall, and for bounce hits outside the view.

Each pixel draws `candidates` (M, default 8) lights. Each candidate gets weight `p̂/pdf` and goes through single-sample weighted reservoir sampling. The final weight is `W = wSum / (M · p̂(y))`, and the estimate is `c(y) · V(y) · W`. The bounce hit does the same with `bounceCandidates` (default 4) and no reuse.

**Portals** (`portalImportance`, default on). The camera's distance to a light assumes the camera sees the world around it. Through a portal it sees a place that can be hundreds of metres away in world space: 8-2's fake mirrors (`4 - Hub/MirrorBig/Portal`) join x = 19 and x = 281, so every light of the room seen "in the mirror" was 262 m from the camera and was drawn almost only through the uniform mix (0.2 / 110 lights). `SceneProxies.PortalViewpoints` adds the camera's image through each portal side it can look into (in front of the side, within 120 m, at most 8): travel matrix × camera position. A light's weight is the largest over the camera and these viewpoints. Bench: `ls.portalImportance=0`. Results in docs/portals.md, "Noise behind portals".

Since 2026-11-18 (`t3/door-brightness`) the images come from one shared list (`Plugin.PortalViews()`): up to 3 portals
deep, the 16 nearest, and the emissive triangles' importance uses them as well (docs/portals.md, section 9).

## Temporal reuse (ReSTIR; removed 2026-11-19, history)

**Reservoir buffer.** `ComputeBuffer` of `uint4` per pixel at trace resolution, ping-ponged: `_ReservoirPrev` is read as an SRV and `_ReservoirCur` is written as a UAV.

| Field | Content |
|---|---|
| x | light code (17 bits: light index, or `0x10000` \| emissive triangle; `0xFFFF` = none) \| uint(M) << 20 (12 bits) |
| y | asuint(W) with its 10 low mantissa bits holding the emissive sample's barycentric u; W = 0 if the final shadow ray was blocked (see below) |
| z | octahedral normal, 2 × 11 bits \| barycentric v << 22 (10 bits) |
| w | asuint(dot(n_quantised, worldPos)): the surface plane |

This layout changed with emissive lights (it used to be a 16-bit light index, half(M), and a 2 × 16-bit normal); see `docs/emissive-lights.md`. M is now an integer capped at 4000 (`_RestirParams.x`).

Sky pixels write `(0xFFFF, 0, 0, 0)`, and M = 0 is never reused. At 1280x720 each buffer is 14.7 MB, so 29.5 MB in total. The buffers are only allocated in `ReSTIR` mode. Other modes bind two 1-element dummies, never the same buffer as both SRV and UAV.

**Reprojection.** Uses the same convention as the denoiser:
1. `pos` is the primary hit, `camPos + dir·t`.
2. `clip = _PrevViewProj · (pos + motion, 1)` and `pixel = floor((clip.xy/clip.w · 0.5 + 0.5) · dim)`. `motion` is the hit's world-space motion (0 for static geometry; docs/denoiser.md, "Motion vectors"); the plane test below also uses `pos + motion`.
3. Reservoirs can't be blended, so this is a nearest-pixel fetch, not bilinear.

**Validation.** A history reservoir is used only if:
- M > 0
- `dot(n, n_prev) ≥ normalTolerance` (0.9)
- `|dot(n_prev, pos) − plane_prev| ≤ planeTolerance · t + 0.01` (0.01)

The plane is stored against the quantised normal, so the test is exact for a static point.

**Merge.** The history's M is clamped to `historyLimit × candidates` (20 × 8 = 160). Its light is remapped to this frame's index and re-evaluated at the current pixel, then added to the reservoir with weight `p̂_current(y) · W_prev · M_prev`.

**The 1/M combine.** There is no MIS or 1/Z over the history domain, so no extra target evaluations or rays are needed. The bias is limited to pixels where the history's domain differs from the current pixel's: a light whose p̂ is 0 there but not here, which can only happen at validation-passing geometric edges or when a light moves. Validation keeps this small.

**Visibility reuse (`visibilityReuse`, off since 2026-11-15; hybrid keeps it).** The history now stores the sample's unshadowed W and every frame traces its own shadow ray, which is plain temporal RIS reuse: unbiased. The old scheme, below, is biased with soft shadows; see [Bias and noise](#bias-and-noise-2026-11-15).

The old scheme: if the final shadow ray is blocked, the stored W is 0, but the light and M are kept. The argument for the static case: The history then contributes `V·p̂·W·M`, and the shading applies V again. Since V² = V, the combined estimate is still `∫ p̂·V` / (pixel's total M) per unit of M, the same expectation as fresh RIS with visibility. That holds only for a deterministic V. With soft shadows (every light has a radius, the sun a disk) V is a fresh random draw each frame, and a history kept because its last draw was clear is shaded with a new draw: its expected contribution is multiplied by the visibility probability again, so penumbrae read dark (2-2's floor -22 %, 1-4's lamps -38 % without the cap below). An occluded sample therefore stops propagating instead of being re-picked every frame, which cuts noise in shadowed areas. The cost is lag when an occluder moves away: at most `historyLimit` frames' worth of M.

**Light identity: remap, not stable slots.** `_Lights` stays a compact per-frame list in GatherLights order, which changes as rooms toggle lights. Each frame `LightSampling` records the `Light.GetInstanceID()` of every packed index and builds `_LightRemap[prevIndex] → currentIndex`, with `0xFFFF` for a light that is gone. Stable sparse slots would have left holes in the list. A remap keeps the alias table, `_LightCount`, `DirectAll` and the reference path exactly as they are, at the cost of one 256-entry upload and a dictionary lookup per light per frame.
- If a reservoir's light is gone, that history is **dropped** (M not counted). Counting its M with zero weight would darken the pixel, since the history also stood for the other lights it did not pick.
- On frames where any light was added or removed (`LightSetChanged`) and `shortenHistoryOnLightChange` is on, the history M is shortened so that a light switched on with its room shows up within a frame or two instead of after up to 20 frames. By default the cap drops to 1× `candidates` over the whole screen for that frame; `perPixelLightChange` does it only where the change matters ([Light changes](#light-changes)).

### Light changes
Effect lights (docs/emissive-lights.md, "Effect lights") make the light set change many times a second in combat: every muzzle flash, beam, projectile and explosion adds a light and removes it a few frames later. With the whole-screen cap, each of those frames threw away the reservoir history of every pixel, including pixels the light can't reach, and ReSTIR needs about 20 frames to build it back. With `perPixelLightChange` (off by default: the motion-noise runs showed no reliable noise gain, docs/denoiser.md "Motion noise"; `-ultrart-restir-pixel-change`):
- `LightSampling` keeps the previous frame's packed light data and builds a **change list** (`_LightChanges`, at most `MaxChanges` = 16 per frame): for each light that appeared or disappeared, its data before and after (an absent side has zero colour).
- At the primary hit, before the temporal merge, `RestirChangeCap` evaluates each change at the pixel with the normal light model (`LightEvalRaw`, no rays): `rho` = sum of |p̂ now − p̂ before| / (the fresh candidates' estimate of the total, wSum / M, plus p̂ before). The history may keep at most a share `h = lightChangeTolerance / rho` (0.1) of the merged reservoir, so the cap is `M_fresh · h / (1 − h)`, clamped to [M_fresh, the normal cap]. Where the changes contribute less than 10% the history is untouched; where they dominate it is cut to 1× `candidates`, as before.
- More than 16 changes in one frame, or a change of the emissive triangle set, fall back to the whole-screen cap.
- `lightChangeKept` (off) also lists lights that stay but change brightness (`lightChangeIntensity`, 10% per frame) or move (`lightChangeMove`, 5% of the range). Measured noisier: a fading muzzle light then shortens the history around it on every frame of its fade, while the stale share it leaves in a full history is small and smooth.
- Cost: on frames with changes, two light evaluations per change per pixel (16 changes at most); nothing otherwise.

### Eye lights
User report: in 3-1's ribbed cave ("3-1 noisy room", which needs `"Load": "checkpoint"`: its approach ends in the void), the walkway showed a smooth, slowly moving noise pattern while standing still. The walkway is lit by two lights: a spot light 72 m away shining through the gaps between the ribs (a large penumbra), and the game's own player light (`Point Light`, range 15, grey, at the eye; docs/volumetrics.md). ReSTIR has one sample per pixel for both, and with visibility reuse the penumbra's random visibility draws kept each pixel on one or the other for many frames; after the temporal filter that is slow, smooth wandering. With every light evaluated each frame (`lights=ref`) the walkway was calm.

A point light within 0.5 m of the camera can't shadow anything the camera sees (every visible point has a clear line to the eye). Such lights are now packed as unshadowed non-fill lights (`_Lights[i*4+3].z` = 0.75): evaluated at every hit with no ray, like fill lights, but not removed by relight's fill cut, and out of ReSTIR's competition (`Plugin.Noise.cs`, `EyeLight`). The spot light then gets all of the samples. At bounce hits the light is also unshadowed, a small leak for surfaces the camera doesn't see. Bench: `plugin._eyeLightsUnshadowed=0`.

Burst (64 frames after 240 settle frames, interleaved A/B/A/B, `bench/suites/noise-pm-final.json`), 3-1 walkway block sigma: **0.04 / 0.04** with the change against 1.87 / 1.91 without (sigma 0.07 against 1.9; `lights=ref` 0.04). The viewmodel in the same frame: 3.5 against 3.85. The walkway is 5% brighter (the player light no longer loses samples to occluded draws).

Tried for 3-1 and not kept: more candidates (16: no change), a shorter history (`historyLimit` 8: 2.35 instead of 3.1), no emissive lights (no change).

### Occluded history (penumbrae)
Only with `visibilityReuse` (hybrid since 2026-11-15; relight no longer stores occluded history, see [Bias and noise](#bias-and-noise-2026-11-15)).

With soft shadows each frame's shadow ray is one random visibility draw, and a blocked draw was stored with W = 0 and the full history M (up to `historyLimit` x `candidates` = 160): a pixel in a penumbra stayed dark for about 20 frames, then lit for as long, independently of its neighbours. `occludedHistory` (2) caps the M an occluded history carries at that many x `candidates`. Interleaved bursts (`noise-pm-final.json`, masked sigma / block sigma / mean luma): 1-4 drift (moonlit bridge) **0.36 / 0.28 / 56.8** with the cap against 1.02 / 0.95 / 54.4 without (twice each, identical), p95 1.2 against 5.3; 3-1 with eye lights unshadowed unchanged (0.07 on the walkway either way). It biases penumbrae slightly bright (+4% mean luma in 1-4 drift): occluded draws count less than lit ones. Before the eye-light change it made 3-1's walkway 25% brighter and noisier (the player light's samples were being blocked there). Bench: `ls.occludedHistory=0` (no cap).

#### Umbrae: re-testing occluded history (opt-in)
Superseded: the bias fix (RIS by default) removed the 8-2 boil without this trade-off ([Bias and noise](#bias-and-noise-2026-11-15)).

User report (2026-11-14, pose "8-2 building boil" in `bench/suites/user-1114-late.json`): the white room with a skylight in
8-2 boiled on every wall in relight, finer at trace scale 1.0; `lights=ref` was calm. The sun shines through the skylight
but is blocked on every wall, and RIS keeps picking it because its unshadowed weight is large. With the `occludedHistory`
cap, each blocked sun pick cut the pixel's history to 16 samples, discarding its memory of the lamps it does see, so the
walls' estimate kept restarting from 8-24 samples. Masked block sigma (`bench/suites/noise-late-diag.json`, vol off):
default 0.67-0.83, `ls.occludedHistory=0` 0.31, `lights=ris` 0.28, `ls.historyLimit=4` 0.35, `lights=ref` 0.13;
`ls.candidates=32` and `cache=off` did not help.

`occludedRetest` (`-ultrart-restir-retest`, bench `ls.occludedRetest=1`): the reservoir keeps the occluded sample's
unshadowed W and flags it (x bit 17) instead of storing W = 0. Next frame the temporal history (spatial neighbours keep
the cap) re-traces that sample's shadow ray from the current pixel. Visible: the history counts with its W. Blocked
again: one more ray to the light's centre (`retestUmbra`); blocked too, it is an umbra and keeps its full M, otherwise
it is capped at `retestCap` (2) x candidates. Off, the code path is the old one (same reservoir contents, same cap).

Measured (`noise-late-fix.json`, `noise-late-tune.json`, relight with vol on, masked block sigma, interleaved, before ->
after), RTX 5070 Ti, 2560x1440, scale 0.5:

| Pose | cap (default) | re-test, cap 2 + umbra |
|---|---|---|
| 8-2 building boil | 0.64 | 0.43-0.46 |
| 2-2 floor noise purple | 0.39-0.40 | 0.36 (mean luma -1 %) |
| 1-4 drift (moonlit penumbrae) | 0.21 | 0.26 |
| 0-1 metal | 0.41-0.42 | 0.49 |
| 6-2 start | 0.34 | 0.40 |
| hybrid (0-1, 6-2) | 0.30 / 0.24 | 0.31 / 0.25 |

Variants tried: a 3-bit "occluded N frames in a row" streak (no gain: the room's pixels alternate between a visible lamp
sample and the blocked sun, so the streak keeps resetting); a centre ray on the final sample only (room 0.40 with vol off,
1-4 drift 0.22 -> 0.32); re-test with no cap (room 0.36-0.38, 1-4 drift 0.45, 0-1 metal 0.65: a blocked re-test with the
full M lingers dark in penumbrae, as before the cap); re-test with caps 2 / 6 (room 0.51 / 0.37-0.42, 1-4 drift
0.21 / 0.26-0.28, 0-1 metal 0.38 / 0.51). Every variant that calms the room makes the penumbra poses boil 15-25 % more,
so it stays off by default. Cost when on: one shadow ray on pixels whose temporal history was occluded, plus one for
those blocked again (not measured: the visual runs shared the GPU with other games). A real fix probably needs a
per-pixel visibility estimate (a filtered shadow ratio) instead of one binary draw per history.

### Moving occluders
A reservoir that was occluded last frame keeps its M with W = 0 (visibility reuse, above), so when an occluder moves away the pixel stays dark for up to `historyLimit` frames of M: a walking player or enemy left a trail behind its shadow. `SceneProxies.CollectMovers` (`SceneProxies.Movers.cs`) uploads every frame up to 8 boxes of dynamic objects that moved in the last 3 frames (V1's model first, then the nearest moving enemies and props within 60 m, at most 16 m in size), each the union of this frame's and last frame's bounds (`_UltraRTMovers`, `_UltraRTMoverCount`; PathTrace only, `ULTRART_MOVERS`). In `RestirMerge`, a history sample whose shadow segment crosses a box is re-weighted to at most `candidates` of M, and dropped when it was occluded. The final sample's crossing is flagged in `_Radiance.a` (+2) for the denoiser (docs/denoiser.md, "Moving shadows"). Bench: `plugin._movers=0` (also `plugin._moverOthers=0`: the player only).

### Fresh pixels
A pixel whose temporal reservoir failed validation (a disocclusion, the screen edge while turning, the first frame) used to show an 8-candidate RIS sample. `freshCandidates` (24) adds that many candidates to it (`RestirReuse` returns whether the temporal reservoir merged; `ReservoirCombine` adds the second RIS stream). Light evaluations only, no rays, and only on those pixels. In the motion-noise measurements this alone cut the 180°/s sweep's noise by a fifth (docs/denoiser.md, "Motion noise"). Flag: `-ultrart-restir-fresh N` (0 = off).

**Spatial reuse (optional, `spatialSamples`, default 0).** A true spatial pass needs a second dispatch after all temporal reservoirs are written. Instead, k extra reservoirs are taken from the **previous** frame inside a disk of `spatialRadius` pixels (default 12) around the reprojected pixel. Each uses the same validation and merge. This fits in the single RayGen with no extra rays. It is biased at shadow boundaries, because a neighbour's W reflects its own visibility, so it is off by default in relight and pt-dn: there it made 1-1's sun shadow edges boil more. Hybrid uses `spatialSamplesHybrid` (2, radius `spatialRadiusHybrid` 6 px), because it only takes ReSTIR's shadowed/unshadowed ratio, where it cuts the slow boiling by about a third (docs/denoiser.md, "Static noise"). Flags: `-ultrart-restir-spatial N`, `-ultrart-restir-spatial-hybrid N`.

## Bias and noise (2026-11-15)

Against the reference sampler (`lights=ref`: every light, one ray each), relight's ReSTIR read 8-16 % bright in some
rooms and 15 % dark in others, and 2-2's floor 47 % too blue (docs/path-tracing.md, "Energy"). Plain RIS matched the
reference. Measured on the raw lighting buffer (the radiance statistics of each screenshot: mean linear lighting before
exposure, game match and the tone curve; `bench/suites/sampling-base.json`, `sampling-fix.json`, `sampling-fix2.json`),
RTX 5070 Ti, 2560x1440, trace scale 0.5:

| Pose | old ReSTIR | old, `occludedHistory=0` | **RIS 16 (new default)** | fixed ReSTIR | reference |
|---|---|---|---|---|---|
| 0-5 lava | 0.934 (+13 %) | 0.808 (-2 %) | 0.815 / 0.812 | 0.815 / 0.814 | 0.826 / 0.812 / 0.816 |
| 2-2 floor (blue) | 0.511 (+9 %; 1.74, +47 %) | 0.363 (-22 %) | 0.465 / 0.478 (1.19) | 0.471 / 0.464 (1.19) | 0.468 / 0.467 / 0.466 (1.18) |
| 0-1 metal | 2.906 (-2 %) | 2.753 (-7 %) | 2.933 | 2.985 | 2.954 / 2.950 |
| 6-2 start | 0.660 (+4 %) | 0.624 | 0.634 | 0.634 | 0.633 / 0.633 |
| 1-4 lamps | 0.498 (-15 %) | 0.364 (-38 %) | 0.594 | 0.587 | 0.587 / 0.588 |
| 1-4 drift | | | 1.114 | 1.139 | 1.115 |
| 8-2 building boil | 2.552 (+9 %) | 2.339 | 2.350 / 2.334 | 2.339 | 2.343 / 2.339 / 2.341 |

(Several values: separate runs. Each is the single-frame mean of a noisy buffer, so about +-1 % run to run.)

**Sources.** Two biases in the old visibility reuse, in opposite directions:
1. *Reused visibility draws* (dark). With soft shadows a shadow ray is a random draw. The history kept the samples
   whose draw was clear (W) and zeroed the others, and the shading then applied a fresh draw on top: the history's
   expected contribution was multiplied by the visibility probability twice. `occludedHistory=0` shows it alone: 2-2
   -22 %, 1-4's lamps -38 %.
2. *The occluded-history cap* (bright). `occludedHistory` (2) counted an occluded history as at most 16 samples while a
   lit one kept up to 160: the zero evidence counted less than the lit evidence. In rooms where bright lights are
   blocked (0-5's lava lights, 8-2's lamps in neighbouring rooms) that over-corrects: 0-5 +13 %, 8-2 +9 %. 2-2's
   colour: the blue lamps are the ones whose samples survive, so they took the weight.

M caps, the target function (luminance of the unshadowed contribution), spatial reuse (off in relight) and the radiance
cache (identical in every variant) were not the cause: without visibility reuse every variant lands on the reference.

**Fix.** `visibilityReuse` off: the history keeps the sample's unshadowed W (as the old `occludedRetest` did) and every
frame traces its own visibility, which is plain temporal RIS reuse, unbiased up to the 1/M combine's edge cases. The
occluded cap, the re-test and the mover drop then have nothing to do (they only touch histories flagged occluded,
which no longer happens). Hybrid keeps the old scheme (`hybridMode`, unchanged look).

**Noise at a held pose** (masked burst sigma, `vol=0`, 64 frames; a range = repeats, interleaved or separate runs):

| Pose | old ReSTIR | fixed ReSTIR | **RIS 16** | RIS 32 | reference |
|---|---|---|---|---|---|
| 8-2 building boil | 0.62-0.63 | 0.38-0.40 | **0.32-0.33** | 0.29 | 0.23 |
| 2-2 floor noise purple | 0.44-0.46 | 0.40-0.65 | **0.28-0.32** | 0.28 | 0.25-0.26 |
| 0-1 metal | 0.57-0.74 | 0.52 | **0.48-0.49** | | 0.48 |
| 6-2 start | 0.52-0.54 | 0.54 | **0.50-0.53** | | 0.45-0.46 |
| 1-4 lamps | 0.37-0.40 | 0.39 | **0.36-0.37** | | 0.33 |
| 1-4 drift (penumbrae) | 0.33 | 0.33 | **0.25** | | 0.22 |
| 0-5 lava | 1.08-1.11 | 1.10 | **0.93-0.94** | 1.01 | 1.01-1.08 |

**In motion** (replay sigma / 4x4 block, `vol=0`, `bench/suites/sampling-motion.json`; spread about 0.2; the RIS
columns ran with the visibility memory on, which measured neutral at rest):

| Path | old ReSTIR | fixed ReSTIR | RIS 16 | RIS 32 |
|---|---|---|---|---|
| 0-2 v1 shadow walk | 1.73 / 1.60 | 1.67 / 1.51 | 1.87 / 1.71 | 1.79 / 1.62 |
| 1-1 spawn sweep (60 deg/s) | 2.01 / 1.57 | 1.83 / 1.40 | 1.84 / 1.49 | 1.72 / 1.35 |
| 4-1 indoor A sweep (180 deg/s) | 1.30 / 1.08 | 1.38 / 1.13 | 1.23 / 0.96 | 1.53 / 1.21 |

**Default: RIS, 16 candidates.** Unbiased, quieter than either ReSTIR at every held pose (at the reference's level on
0-1 metal and 1-4's penumbrae), the same in motion within the metric's spread. ReSTIR's samples persist for up to 20
frames, which relight's temporal filter can't average away (slow boiling: high block sigma); RIS samples are
independent from frame to frame and the filter averages them (docs/archive/pt-plan.md 6.1 found the same for DLSS RR). 32
candidates helped 8-2 a little and nothing else. ReSTIR stayed available (F6, the menu, `lights=restir`) until its removal on 2026-11-19 and might still
win where many lights compete per pixel (8-3's 100+ lights were not re-measured here).

**8-2's umbra boil** ("8-2 building boil", the skylight room) is fixed by the same change, without the old trade-off:
0.62 -> 0.33 masked (block 0.53 -> 0.23), while the soft-shadow poses got quieter too (1-4 drift 0.33 -> 0.25, 0-1
metal 0.57 -> 0.48, 6-2 0.54 -> 0.50). The old `occludedRetest` got the room to 0.43-0.46 at the cost of 15-25 % more
noise on those. The boil came from the occluded cap cutting the history to 16 every time a blocked light won; RIS has
no history to cut.

**GPU cost** (`bench/suites/sampling-perf.json`, exclusive, modes interleaved every 0.75 s; GPU total / `dispatch` ms):

| | old ReSTIR | **RIS 16** | RIS 32 | fixed ReSTIR | RIS 16 + visibility memory |
|---|---|---|---|---|---|
| 0-1 metal | 6.26 / 4.27 | **5.59 / 3.61** | 5.80 / 3.82 | 5.82 / 3.84 | 6.02 / 4.03 |
| 0-5 lava | 6.20 / 4.12 | **5.88 / 3.80** | 6.38 / 4.31 | 6.00 / 3.94 | 5.91 / 3.83 |
| 8-2 building boil | 5.22 / 3.16 | **5.22 / 3.16** | 5.69 / 3.63 | 5.11 / 3.05 | 5.38 / 3.30 |

RIS 16 is 0-0.67 ms cheaper than the old default (no reservoir read and write, no merge, no 24 fresh candidates on
disocclusions); in RIS mode the reservoir buffers (2 x 14.7 MB at 1280x720) are not allocated.

`pathtrace`: under DLSS RR it already used RIS (`ptx.lightsRR`), unchanged. With its UltraRT-denoiser fallback it
follows `mode`, so it gets RIS 16 too (unbiased instead of the old ReSTIR's bias).

### Visibility memory (opt-in, `visibilityMemory`; removed 2026-11-19)
An attempt at a visibility-aware target for umbrae: per pixel, up to 3 lights with counts of their shadow-ray outcomes
(4-bit visible / tested, halved at 16; Vbar = (visible + 1) / (tests + 2)), stored next to the reservoir (element 2i + 1
of the reservoir buffers, still one UAV), reprojected and validated like a reservoir, light indices remapped. RIS and
ReSTIR use p̂ x Vbar as the target at the primary hit; a blocked light without a slot takes the slot with the highest
Vbar; a light whose segment crosses a mover box gets at least 1/2 and isn't counted. Unbiased (Vbar > 0, the shading
traces visibility). `ls.visibilityDebug=1` with `pt-direct` shows it: red = the lowest remembered Vbar, green = slots in
use / 3.

It works as designed (8-2's walls fill all three slots with blocked lights; mean lowest Vbar 0.24), but bought no
noise: 8-2 RIS 0.33 / 0.33 with and without (twice, interleaved), fixed ReSTIR 0.38-0.39 against 0.40; 0-5 lava RIS
0.97-1.05 against 0.94 (worse); 2-2 0.28 against 0.29. After the bias fix the remaining selection noise isn't dominated
by blocked lights. Off by default (in RIS mode it costs a reservoir buffer pair and a 16-byte read and write per pixel).

## Cost (estimated, not measured)

Since 2026-11-19 only RIS and Reference remain: RIS is as below without the reservoir read and write (measured cost of RIS against ReSTIR: [Bias and noise](#bias-and-noise-2026-11-15)). The original estimate, per pixel in `ReSTIR` mode:
- 4 rays: primary, 1 shadow, bounce, 1 bounce shadow. The reference uses `2 + lights in range`.
- `candidates + bounceCandidates + 1` light evaluations: 12 × (1 alias read + mask + up to 4 float4 reads).
- 16-byte reservoir read and write, plus `fillCount` × 2 unshadowed evaluations.

8-3 should then cost about the same as other levels, about 0.5–0.7 ms at 1280x720 instead of about 2 ms, and the cost no longer scales with light count. The CPU cost per frame is building the alias table (O(N), N ≤ 256) plus three small `SetData` calls.

**Noise.** Direct lighting is no longer deterministic. `docs/denoiser.md` assumes that it is ("Why 3x3 is the default"). With 8 candidates plus up to 160 history samples, the remaining noise is mostly in penumbrae and in pixels lit by many similar lights. The denoiser's temporal pass and 3x3 à-trous should absorb it. If not, raise `candidates` (cheap, no rays), try `spatialSamples = 2`, or set `Denoiser.Settings.wideKernel`.

## Integration (Plugin.cs)

`LightSampling` binds everything `UltraRTLights.hlsl` declares beyond `_Lights`, `_LightMasks` and `_LightCount`. **It must be bound on every PathTrace dispatch, in every mode**, or Unity reports unset properties.

1. Fields, next to `_lightMaskBuffer`:
   ```csharp
   private LightSampling _lightSampling;
   private readonly Light[] _packedLights = new Light[MaxLights];
   private readonly LightSamplingSettings _lightSamplingSettings = new LightSamplingSettings(); // filled by ParseArgs
   ```
2. `Awake()`, right after `_lightMaskBuffer = new ComputeBuffer(MaxLights, 4);`:
   ```csharp
   _lightSampling = new LightSampling(MaxLights) { Settings = _lightSamplingSettings };
   ```
3. `GatherLights()`, in the packing loop, just before `_lightCount++;`:
   ```csharp
   _packedLights[_lightCount] = l;
   ```
   Also change the layout comment there: the `_Lights` layout is now documented in `UltraRTLights.hlsl`.
4. `Trace()`, inside `if (pt) { ... }`, after `_cmd.SetRayTracingIntParam(shader, "_LightCount", _lightCount);`:
   ```csharp
   _lightSampling.Bind(_cmd, shader, w, h, viewProj, _camera.transform.position, _packedLights, _lightData, _lightCount);
   ```
   (Historical wiring recipe. Since 2026-11-19 the call is `_lightSampling.Bind(_cmd, shader, camPos, _packedLights, _lightData, _lightCount)`: no reservoirs, so no size, matrices or mode flags.)
   `viewProj` is the existing local, `_camera.projectionMatrix * _camera.worldToCameraMatrix`, whose inverse is `_InvViewProj`. `w` and `h` are the dispatch size. `LightSampling` remembers its own previous `viewProj`, so there is nothing else to pass.
5. Resets: call `_lightSampling?.Reset();` wherever `_denoiser?.Reset()` / `_denoiser.Reset()` is called:
   - `ResetScene()`, which also covers shader reload
   - the F7 toggle
   - the `denoised && _lastKind != Kind.Denoised` line. Better: reset whenever the previous traced frame was not a PathTrace kind, e.g. `if (pt && _lastKind == Kind.Debug) _lightSampling.Reset();` before `_lastKind = kind;`.
   - Respawns and teleports. Validation would mostly reject the history anyway.

   Resolution changes and mode switches are handled internally.
6. Optional, for the bench and A/B: arguments in `ParseArgs()`:
   ```csharp
   case "-ultrart-lights": // ref | ris | restir (since 2026-11-19: ref | ris; restir warns and uses ris)
       switch ((Next() ?? "").ToLowerInvariant())
       {
           case "ref": case "reference": _lightSamplingSettings.mode = LightSamplingMode.Reference; break;
           case "ris": _lightSamplingSettings.mode = LightSamplingMode.Ris; break;
           default: _lightSamplingSettings.mode = LightSamplingMode.ReSTIR; break;
       }
       break;
   case "-ultrart-ris-m": int.TryParse(Next(), out _lightSamplingSettings.candidates); break;
   case "-ultrart-restir-spatial": int.TryParse(Next(), out _lightSamplingSettings.spatialSamples); break;
   ```
   Also a hotkey in `OnUpdate()` (F6 was free then; it now cycles the light sampling mode, as built here):
   ```csharp
   if (Input.GetKeyDown(KeyCode.F6))
   {
       var s = _lightSampling.Settings;
       s.mode = (LightSamplingMode)(((int)s.mode + 1) % 3); // % 2 since ReSTIR went (2026-11-19)
       _accumFrames = 0; _denoiser?.Reset();
       Logger.LogInfo($"Light sampling: {s.mode}");
   }
   ```
   Consider adding `_lightSampling.Settings.mode`, `RisLightCount` and `FillLightCount` to the HUD and to the "First trace OK" log line.
7. Teardown, if the plugin ever disposes its buffers: `_lightSampling.Dispose();`.

### UAV budget
Since 2026-11-19 light sampling adds no UAV (`_ReservoirCur` went with ReSTIR) and PathTrace binds 7. The rest of this subsection is the ReSTIR-era budget.

Unity's RayTracingShader appears to allow at most 8 UAVs per `.raytrace`. A 9th makes `DispatchRays` fail. Light sampling adds exactly **one** UAV, `_ReservoirCur` (`RWStructuredBuffer<uint4>`). Everything else it binds is a read-only SRV: `_ReservoirPrev`, `_LightAlias`, `_FillLights` and `_LightRemap`. The ping-pong swaps which `ComputeBuffer` goes into each slot every frame. Keep the PathTrace total at 8 or fewer: with the main agent's 7 RW outputs plus `_ReservoirCur`, it is exactly 8. (Reflections kept it at 8 by packing albedo and emission into `_Surface` to make room for `_Reflection`; see docs/reflections.md.)

### Assumptions
- `Bind` is called once per traced PathTrace frame, after `GatherLights()`, and the recorded commands run exactly once, in order. (The ReSTIR-era reservoir ping-pong and light-index history advanced at record time.)
- `_packedLights[i]` is the `Light` whose data is at `_lightData[i*4 .. i*4+3]` for `i < _lightCount`. Entries past `_lightCount` are ignored.
- The primary rays are the same as the denoiser's: through pixel centres via `_InvViewProj` (jittered only while accumulating).
- `MaxLights` ≤ 65535, since light indices are 16 bits in the reservoir and the alias table.

## Known limitations / next steps
- The bounce uses plain RIS with no reuse. A light-tile or world-space grid of reservoirs would help when dim rooms are mostly bounce-lit.
- The importance heuristic ignores the spot cone and frustum visibility. Culling each light's 2·range sphere against the frustum (for primary hits) would sharpen the source pdf in 8-3.
- Sample reuse (ReSTIR) was removed on 2026-11-19. If it comes back, it needs a visibility-correct scheme (see "Bias and noise"), pairwise MIS for the 1/M combine's edge bias, and ideally a true spatial pass (RayGen split into two dispatches).
