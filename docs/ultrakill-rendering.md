# ULTRAKILL rendering notes

Observed in the Steam build running Unity 2022.3.29f1 (Mono, built-in render pipeline), using the probe plugin and decompiled `Assembly-CSharp`.

## Pipeline
- Forward rendering, no HDR, no MSAA, no depth texture. Everything is point-filtered.
- Cameras: `Main Camera` (world), `HUD Camera` (viewmodel/HUD, layer 13), `Virtual Camera`.
- Command buffers on `Main Camera`: `Reset Clip Plane` @BeforeDepthTexture, `Blood and Oil` @BeforeForwardAlpha, `Outlines` @AfterEverything.
- Final composite: `ULTRAKILL/PostProcessV2` reads a `Main` render texture at screen resolution.
- Lighting: a handful of realtime point lights (toggled per room), no shadows, no lightmaps, vertex lighting.

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
