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
