# ULTRAKILL rendering notes

Observed in the Steam build running Unity 2022.3.29f1 (Mono, built-in render pipeline), using the probe plugin and decompiled `Assembly-CSharp`.

See also: [portals.md](portals.md) (layer 8's portals and mirrors), [relight.md](relight.md) (the physical light model and HDR output), [vfx.md](vfx.md) (the screen effects UltraRT adds on top of the ray-traced image).

## Pipeline
- Forward rendering, no HDR, no MSAA, no depth texture. Everything is point-filtered. MSAA is off because
  `PostProcessV2_Handler.SetupRTs` creates the world camera's MRT (`mainTex` ARGB32, `reusableBufferA` RG16,
  `viewNormal` RGB565, `depthBuffer` 32-bit) with `antiAliasing = 1`; swapping in 4x targets works for the game's image
  but breaks every depth reader ([aliasing.md](aliasing.md), "MSAA on the game's raster").
- Cameras: `Main Camera` (world), `HUD Camera` (viewmodel/HUD, layer 13, FOV 90, clears depth, same colour/depth buffers; see [viewmodel.md](viewmodel.md)), `Virtual Camera`.
- Command buffers on `Main Camera`: `Reset Clip Plane` @BeforeDepthTexture, `Blood and Oil` @BeforeForwardAlpha, `Outlines` @AfterEverything.
- Final composite: `ULTRAKILL/PostProcessV2` reads a `Main` render texture at screen resolution (and dithers it: see "Final post-process: dither").
- Lighting: a handful of realtime point lights (toggled per room), no shadows, no lightmaps, vertex lighting.

## Final post-process: dither
Found 2026-11-19 (decompiled `PostProcessV2_Handler` and `SettingsMenu.Components.Pages.GraphicsSettings`, and
`ULTRAKILL/PostProcessV2`'s pixel shader read with UnityPy + `fxc /dumpbin`). The game's last full-screen pass draws a
fine ordered dither over the whole frame, PSX look or not. It looks like a 1-pixel checkerboard and is identical with
UltraRT off and on, but over a ray-traced image it reads as a texture (2026-11-19 trailer frames, 8-2 street).

What the pass does per pixel, in order (`col` = `mainTex`, display-encoded 8-bit):
1. `col = lerp(col, colour, a)`: a flash overlay.
2. **Dither**: `d` = `_Dither` (`PostProcessV2_Handler.ditherTexture`, a 16x16 Bayer texture, sampled at screen pixel / 16),
   `col = lerp(col, (1 - 2d) col^2 + 2 d col, saturate(_DitherStrength))`, a soft-light blend. That is
   `col + s col (1 - col) (2d - 1)`: up to +-s/4 at mid grey (+-13 of 255 at s = 0.2), nothing at black or white.
   Measured on screenshots: the 16x16-phase residual is 4.2-4.3 levels in 8-2 and 1-1, 1.9 in dark 1-4.
3. **Colour compression**: `floor(col * _ColorPrecision) / _ColorPrecision`.
4. Gamma: `col^(max(2 (1 - _Gamma / 2), 0.01))`.

The game's options that drive it (stored in `<game>\Preferences\Prefs.json`, not the registry):
- **Options > Graphics > Dithering** (pref `dithering`, float, default 0.2): `Shader.SetGlobalFloat("_DitherStrength", v)`.
  This is the dither's only switch: 0 turns it off. Levels can override it on a trigger (`ColorSchemeSetter`,
  `replaceDitherUserSetting` + `ditheringAmount`). Both installs here run 0.2.
- **Color Compression** (pref `colorCompression`, 0-5 = 2048, 64, 32, 16, 8, 3 levels per channel, default 2 = 32):
  `_ColorPrecision`. A custom colour palette forces 2048. Independent of the dither, which does not scale with it. The
  user runs 0 (2048, a no-op on 8 bits), so in their game the dither is purely a look.

**UltraRT turns it off while frames are traced** (`Plugin.GameDither.cs`, since 2026-11-19): in relight,
pathtrace and the debug views, `_DitherStrength` holds a marker value (1e-20, effectively 0); when tracing stops (F7,
`-ultrart-off`, a failed trace, ~30 frames later) or the plugin is destroyed, the game's value is put back. Only the
shader global changes, never the prefs. A value the game writes while the marker is in place (the slider, a level's
`ColorSchemeSetter`) is taken as the new game value and the marker goes back on, so the restore is always the game's
latest value. Log lines: `Game dither: off while traced (the game's strength 0.2)` / `Game dither: restored (0.2)`.
Setting: `[View] GameDither` (menu "The game's screen dither while ray traced", default off), `-ultrart-game-dither on|off`,
bench `plugin._gameDither=1`.

**Our own dither instead** (`UltraRTDither.hlsl`). The game dither also hid 1-step contours in dark gradients: everything
from the RT composite to the game's post-process is 8-bit (`mainTex` is ARGB32). So while the game's dither is held off,
the passes that write float colour into those 8 bits add +-0.5 of a step of interleaved gradient noise (blue-noise-like,
a new phase each frame, never lifts black or dims white): the RT composite, the volumetric in-scatter pass, the
screen-effects final pass (a no-op where nothing changed the pixel) and the UltraRT denoiser's ARGB32 output. The HDR
display output keeps it too: below paper white it shows the same 8-bit image ([hdr.md](hdr.md)). `-ultrart-output-dither
on|off`, bench `plugin._outputDither=0`.

Measured 2026-11-19 (`bench/suites/game-dither.json`, alt, 1440p, 8-2 street / 1-4 lamps / 1-1 grass). Banding index =
share of 16x16 tiles (per channel) that are smooth gradients whose level steps are clean contours (steps needed by the
smoothed image vs steps taken < 1.6):

| | 16x16 Bayer residual (levels) | banded, SDR | banded, HDR10 capture |
|---|---|---|---|
| off (the game) | 1.9-4.3 | 0.04-1.1 % | 0.03-0.40 % |
| traced, game dither on (`_gameDither=1`, the old look) | 2.1-4.2 | 0.05-0.18 % | 0.02-0.04 % |
| traced, no dither at all (`_outputDither=0`) | 0.03-0.05 | 1.5-8.7 % | 0.4-4.7 % |
| traced, default (ours) | 0.03-0.04 | 0.67-0.87 % | 0.24-0.37 % |

The banded tiles without any dither are near-black sky and shadow (1-step contours, 5-45 of 255); at 1-2x they don't
show, the index finds them. Ours adds 0.1 level of fine noise (per-pixel high-pass 1.07 -> 1.19 in 1-4). Cost: none
measurable (composite 0.04 ms, volumetrics and late screen-effects timers identical with and without; GPU frame within
+-0.06 ms run-to-run noise); on the CPU one global read and one write per frame.

## Enemy outlines
Found 2026-11-20 (decompiled `PostProcessV2_Handler.SetupOutlines` and `EnemySimplifier`, `ULTRAKILL/OutlinePx` read with
UnityPy + `fxc /dumpbin`; scratch tool `dumpshader.py <bundle> <path id> <out>`, shader path id 2724905604968942524 in
`shaders.bundle`).
- The world camera draws into three targets (MRT): colour, `Reusable A` (RG16, the **outline mask**) and view normals.
  The game's shaders write the mask from their `_Outline` / `_ForceOutline` values, which `EnemySimplifier` sets per
  renderer in a property block every time its state changes (`_Outline` = 1 with the Enemy Simplifier on and the enemy
  beyond `simplifiedDistance`, else 0; `_ForceOutline` 0.5; both 0 for `neverOutlineAndRemoveSimplifier`).
- `PostProcessV2_Handler.OnPreRenderCallback` clears the mask before every main-camera frame; the command buffer
  `Outlines` at `AfterEverything` then draws it. With the pref `outlineThickness` 1 (the default) that is one blit of
  pass 3 (`Composite1Px`, blend `DstColor Zero`, i.e. multiply): a pixel is **masked** when `mask.r > 0.999` or
  `mask.r + mask.g > 1`, and a pixel that is not masked but has a masked 4-neighbour is multiplied by 0. So a black
  1-pixel ring is drawn just outside every masked region. With the Simplifier on and a thickness above 1, passes 0-2 grow
  it with a jump flood instead. `SetupOutlines` runs at start, on camera re-inits and when either pref changes.
- It draws after our composite, onto whatever is in the colour target, so the traced image gets the game's outlines
  exactly where the game's image does, given the same mask.

**Off for a run** (`src/UltraRT/Plugin.EnemyOutlines.cs`): `-ultrart-enemy-outlines off`, bench scenario field
`"EnemyOutlines": false` (the trailer plan's default for every shot, 2026-11-20), bench variant `plugin._enemyOutlines=0`.
The `Outlines` buffer is taken off the main camera every frame while off (the game re-adds it on re-inits), and the
game's own `SetupOutlines()` puts it back when it is on again. No pref is written: BenchSafety stays as it is.

**Why the outline showed only in the traced image** (6-2 organ shot, trailer v5, 2026-11-20): it didn't. The mask and
the `Outlines` buffer are the same in both images (bench probe: identical mask, `_OutlineTex_TexelSize` set, buffer on
the camera in traced and off frames), and the game's own image has the same ring at full resolution, visible where it
crosses Gabriel's red aura. Elsewhere it lies on the hall's near-black background; the traced hall around Gabriel is
brighter, so the same black ring shows. Not an UltraRT bug. The mask is empty until `Gabriel 2nd` (the boss) takes
over from the intro rig at about 11 s; from then his renderers carry `_Outline` = 1 in their property blocks
(EnemySimplifier: Enemy Simplifier option on and the enemy beyond its distance from the player, who stays at the
scenario's pose while the camera flies).

## Gabriel's swords (6-2 intro)
Found 2026-11-20 (bench probes, `bench/suites/gabriel-swords.json`). In the organ shot the traced image shows Gabriel
holding two thin blades (green-tinted in the red hall) that the game's image never shows. They are real, enabled
renderers: `GabrielIntro.SwordPull` (an animation event) enables the intro rig's `GabeSword_*002` skinned meshes, and
`Gabriel 2nd`'s rig has its own. The main camera renders them every frame (`OnWillRenderObject`), their skinned
vertices are identical to our proxy's (GPU readback of both buffers), and a test colour on the game's material showed
them exactly where we trace them. The game draws them **near-black**: their material (`GabrielSwordSplendor` /
`GabrielSwordJustice`, ULTRAKILL/Master with `_VertexColors` = 1, additive `SwordCubemap` reflection whose texels are at
most 33/255) multiplies the blade texture by the mesh's vertex colours, and the `GabeSword_*` meshes (swords and
sheaths) have no colour channel. V1's model is also a skinned mesh without colours under a `_VertexColors` material and
comes out normal, so what the game reads for the missing channel is not defined. At the blade pixels the game's image is
(44, 1, 0) against (38, 1, 0) beside it: in the dark red hall the swords vanish.

Decision (user, 2026-11-20): treat it as a game bug and keep the swords visible in the traced image. UltraRT's hit
shader reads missing vertex colours as white, and on non-atlas proxies it does not apply `_VertexColors` at all (the
game multiplies texture x vertex colour x `_Color` there; a version that did changed 0-2% of pixels at 6-2 and 7-3
poses and was left out to keep this change small). A "black when the mesh has no colours" rule (for every skinned
mesh, or by material rule) worked for the swords but blackened V1's third-person model and guns, and is not in.

**Glowing in the traced image (2026-11-20, `t3/trailer-v5e-6-2-organ`; user: visible and emissive).** Traced without a
rule they read as dull dark-teal sticks: thin blades, unlit-ish (enemy lit weight 0.5) and dark in the red hall. The
emitter rule `enemy: Gabriel's swords` in `data/lights/_rules.json` (material `^GabrielSword(Splendor|Justice)$`, glow 5,
chroma 0.2; docs/emissive-lights.md, "Enemy glows") makes them glow with their own texture colour. The textures
(`T_GabeSword_Gold` / `T_GabeSword_Blue`, 64x256, from `models.bundle`) hold blade and guard in saturated gold and blue
and the grip in near-black: the chroma mask lights blade and guard, not the grip. The sheaths (`GabrielSheath`, dark grey
with a gold trim, and `GabrielSheathText`) have no rule: they stay lit surfaces. Gabriel has lights of the game's, so no
enemy glow light is added for the swords (`eg.skipLit`); their glow shows in the image (and bounces in the path
tracer), not as a light on the hall. In the organ shot they now read as bright white-blue lines instead of dark slivers
(`bench/results/review/gabriel-sword-glow/swords-before-after*.jpg`, not committed: game | before | glow 5 at take frames
400 / 700 / 850; the glow-5 column is a main-install run, which has `HDR = true`, the others alt). Glow 2.5 / 5 / 10
looked alike at draft size (RR at a third smears lines this thin); 5 was judged at full resolution
(`bench/suites/gabriel-sword-glow.json`, `gabriel-sword-diag.json`).

**Open: the blades look see-through (user, 2026-11-20).** In the traced image the organ behind shows through them, with
the rule and without. Narrowed down so far (`gabriel-sword-diag.json` and its variants, full resolution):
- The geometry and material are right: the `albedo` debug view (primary rays, no denoiser, no history) shows both blades
  solid and opaque (the Justice blade saturated blue, the Splendor one a thin gold edge).
- The composite uses the traced blades: `plugin._matchDebug=1` paints no kept pixel on them.
- Not mirrored skinning (docs/scene-proxies.md, "Mirrored skinned meshes"): no bone's skin matrix (bone x bindpose)
  of the four `GabeSword_*` meshes has a negative determinant (root `Root`, det 24.8, 51 bones).
- The previous-pose copy is in the expected space (`-ultrart-motion-probe-filter GabeSword`: raw = rootNoScale).
- DLSS RR and UltraRT's own denoiser (`ptx.denoiser=1`) both blend them with the background, so the cause is in what
  both share after the primary hit: the motion vectors or history of these thin, fast-moving skinned blades. The
  `motion` view did not settle it (the runs' timing differs, so the blades are not at the same place).
Next: freeze a pose with the swords out and compare motion on and off (`-ultrart-nomotion`), and read the motion of a
blade pixel against Gabriel's hand next to it.

Ruled out on the way, for the next look at "renders in RT, not in the game": culling and occlusion culling, the
`_PortalClipPlane` clip distance (0 at every draw), stencil (`_StencilComp` Always), depth occluders (none between the
camera and the swords), fog (linear 10-150 m, the same for every Gabriel renderer), shader discard (none in the
variant). Tools: the cinematic `Probes` (docs/cinematics.md) and the scratch `dumpshader.py` / `matdump.py` /
`meshch.py` UnityPy scripts (shader passes, material values, mesh channels).

## Shaders that matter
- `ULTRAKILL/Master` covers dynamic and most static props. Emission is explicit: the `EMISSIVE` keyword plus `_EmissiveColor`, `_EmissiveTex`, `_EmissiveIntensity` and `_UseAlbedoAsEmissive`.
- `Hidden/ULTRAKILL/ULTRAKILL-Stationary` is used for level geometry baked by `StaticSceneOptimizer` (see below).

## StaticSceneOptimizer (baked level geometry)
At `Start()` it swaps static level meshes for baked combined meshes and assigns one of two materials: `batchMaterialEnvironment`, or `batchMaterialOutdoors` for layer 24. Both use the Stationary shader, with `_MainTex` and `_BlendTex` set to scene-wide **texture atlases**.

Vertex layout of the baked mesh, stride 48:

| Attribute | Format | Meaning |
|---|---|---|
| Position | Float32x3 | |
| Normal | Float16x4 | w is a flag (seen = 2) |
| Color | UNorm8x4 | mostly white; blend weight? |
| TexCoord0 | Float16x2 | tiling UV, ranges well beyond 0–1 (±60) |
| TexCoord1 | Float16x4 | main atlas rect **(xMin, yMin, xMax, yMax)** |
| TexCoord2 | Float16x4 | blend atlas rect (mostly zero) |
| TexCoord3 | Float32x1 | `lightData` |

Sampling: `uv = lerp(rect.xy, rect.zw, frac(uv0))`. The atlas is 512×512 with mostly 32 px cells.

Not yet understood: how `Color` and the blend rect combine (vertex blending), and what `lightData` encodes. With compute shaders enabled, the game also uploads `_GlobalLightData` and `_GlobalLightIndices` buffers every `LateUpdate`.
