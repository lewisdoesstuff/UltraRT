# Lava and acid as ray-traced emitters

ULTRAKILL draws lava, acid and the magenta lava of 8-4 as additive transparents. Before this change they were not in the RTAS at all (`Traceable` rejects queue >= 3000). The raw RT image showed the channel floor under the lava. The orange glow in rooms came only from the point lights the level designers placed along the lava, and nothing reflected the lava. Now every lava surface is an emissive triangle light. Walls and ceilings are lit by the lava itself, brightest next to it. Metal, water and glass reflect it, and it feeds the volumetric haze.

Files:
- `src/UltraRT/SceneProxies.Lava.cs`: `LavaSettings`, `LavaClassifier`, the lava proxies, scrolling, and the runtime switch.
- `src/UltraRT/EmissiveLights.Lava.cs`: splits lava emitters into small triangles and computes their radiance.
- `src/UltraRT/Plugin.Lava.cs`: flags, shader loading, and dimming the game's lava point lights.
- `unity/.../LavaProxy.shader`: the hit group (`UltraRTHit`), with an any-hit shader that camera rays pass through.
- One-line hooks: `SceneProxies.cs` (`TryAddLava`, `LogLava`, `DisposeLava`, `LavaHidden` in `Evaluate`), `EmissiveLights.cs` (`partial`, the `ExtractLava` branch, the log), `Plugin.cs` (`ParseLavaArgs`, `LoadLavaShader`, `UpdateLava`, `DimLavaLights`), `Plugin.Burst.cs` (the `lava` variant target), `BuildBundles.cs`.

## Survey (offline, UnityPy over `materials.bundle` and the level bundles)

| Material | Shader / keywords | Look | Levels (renderers) |
|---|---|---|---|
| `Lava` | Master, TRANSPARENCY, VERTEX_DISPLACEMENT, `_BlendMode` 3 (SrcAlpha, One), ZWrite on, CullMode back | `LavaSingle` (128², mean rgb 0.98/0.62/0.30) x `_Color` (1, 0.75, 0), unlit | 0-5 (5), 0-E (4), 1-2 (1), 1-3 (7), 1-E (5), 4-1 (3), 6-1 (4), 8-3 (2, static-batched) |
| `Lava NoCull` | same, CullMode off | same | 0-E (3) |
| `LavaMagenta` | same + FRESNEL, CAUSTICS | `_Color` (1, 0, 0.45) | 8-4 (3 spheres, scale 5) |
| `Acid`, `AcidNoFog` | Master, TRANSPARENCY, EMISSIVE, SCROLLING, VERTEX_LIGHTING, VERTEX_DISPLACEMENT, `_BlendMode` 3, two-sided | no `_MainTex`; `_EmissiveTex` (grey) x (1, 0.73, 0) x 0.25, alpha 0.75; the rest is lit by the game's lights | 3-1 (4), 3-2 (1), 8-3 (1, static-batched) |
| `Lava Opaque`, `Lava Opaque NoCull` | Master, opaque, EMISSIVE, SCROLLING | already traced as opaque emissive geometry | 0-5 (9), 1-E (2), 6-1 (2) |
| `Lava Blue` | Particles/Standard Unlit on a MeshRenderer | not handled (not Master) | 5-2 (1) |

Most lava renderers carry a `ScrollingTexture` component (0.1 to 1 UV/s). It sets `_ScrollOffset` in the renderer's property block every frame. The Master shader samples `uv * _MainTex_ST + _ScrollOffset + _ScrollSpeed * _Time.y`. The lava meshes are mostly ProBuilder cubes or planes: a few large triangles, with the top face being the lava.

## Design

### Instance mask: 0x08, shared with glowing fixtures

No mask bit is free: 0x01 geometry, 0x02 sky walls, 0x04 fixtures, 0x08 emitters, 0x10 water/glass, 0x20 portals, 0x40 layer-30 props, and 0x80 is taken by the player model (branch `self-reflection`). EmitterMask (0x08) already has the visibility lava needs:

| Ray | Mask | Sees lava? |
|---|---|---|
| camera (PathTrace primary) | `ULTRART_MASK_SCENE` | **no**: the any-hit shader calls `IgnoreHit()` when the payload has `ULTRART_PAYLOAD_WANT_MOTION`, which only PathTrace's primary ray sets |
| reflection (opaque surfaces, water/glass pass) | `ULTRART_MASK_SCENE` | yes: the lava's emission (texture x tint, scrolling) |
| bounce, radiance cache | `ULTRART_MASK_SCENE` | yes, as a black surface: emission x `_EmissiveBounceWeight` = 0, because the lava is sampled directly |
| shadow, volumetric shadow | 0x05 / 0x01 | no: the lava is the light source |
| transparent pass | 0x10 | no |
| emissive light sampling, volumetric emissive sample | triangle list | yes |

Lava submeshes are added without `ClosestHitOnly` so that the any-hit shader runs. Lava is one-sided even when the game's material is NoCull: lava lights upwards, and never the channel floor below it.

### Primary rays: the game's lava stays on top

The game draws its additive lava after our composite. If camera rays also saw an RT lava surface, the glow would be counted twice. Replacing the game's lava would mean suppressing its draw and reproducing its vertex waves, depth writes and fog. So camera rays pass through, the RT pixel under the lava is the channel floor (lit by RT), and the game's lava is added on top, exactly as in the raster game. `-ultrart-lava-visible` turns the any-hit skip off, for debugging.

### Emission

The proxy material (`UltraRT/LavaProxy`, one per renderer so that it can follow that renderer's scroll) emits what the game adds:
- `_MainTex x _Color.rgb x _Color.a` for unlit lava (`Lava`, `LavaMagenta`);
- plus `_EmissiveTex x _EmissiveColor x _EmissiveIntensity x alpha` with EMISSIVE (acid). Acid's main colour is lit by the game's lights (VERTEX_LIGHTING), so only its EMISSIVE part glows.

`UpdateLava` copies `_ScrollOffset` from each source renderer's property block every frame, plus `Time.timeSinceLevelLoad` for `_ScrollSpeed`. Reflections of lava therefore flow like the lava itself.

For direct lighting, `EmissiveLights` reads the same material: Le = the texture average over each triangle x the base colour x `LavaSettings.lightScale`, then `EmissiveLightScale` as for every emitter.

**Splitting.** Lava surfaces are a few triangles of tens to hundreds of m², and the shader samples a uniform point on a triangle. A wall next to the lava would draw most of its candidates from metres away. `ExtractLava` splits lava triangles (and the game's opaque lava) at the longest edge until each piece is at most `maxTriangleArea` (1 m²), with at most `maxTrianglesPerSurface` (6,000) pieces per submesh. The alias table's camera-distance importance then puts most samples on the lava near the camera. Faces pointing down (a lava cube's bottom, inside the floor) are dropped.

**Not done: scrolling in the lighting.** The pieces (1 m², about 1.4 m across) are larger than one texture tile (UVs of about 1 per metre), so every piece already has the texture's average radiance, and scrolling would not change it. A moving light pattern would need pieces a quarter of a tile across, which is 16 times as many triangles. The pattern shows where the eye can see it: in reflections and on the lava itself.

### The game's lava point lights

ULTRAKILL fakes the lava glow with orange point lights along the lava. With the lava now emitting, keeping them would light the walls twice. It would also flood the channel floor, which shows through the additive lava, so the lava washed out to flat yellow in relight. `DimLavaLights` scales packed point lights that sit over an active lava surface (its bounds widened by 2 m, up to 4 m above) and whose colour matches the lava's (rgb cosine >= 0.9). The default scale is `pointLightScale` = 0.4. The game's own rendering is unchanged.

## Settings

| Flag | Field (`LavaSettings`) | Default |
|---|---|---|
| `-ultrart-nolava` | `enabled` (scene build) | on |
| bench variant `lava.active=0` | `active` (runtime: the proxies leave the RTAS and the emitter list, lights are not dimmed) | on |
| `-ultrart-lava-light X` | `lightScale` (scene build) | 1 |
| `-ultrart-lava-glow X` | `glow` (emission seen by reflections) | 1 |
| `-ultrart-lava-pointlights X` | `pointLightScale` | 0.4 |
| `-ultrart-lava-visible` | `cameraVisible` | off |
| | `maxTriangleArea`, `maxTrianglesPerSurface` | 1 m², 6,000 |

The build log prints `Lava surfaces: N renderers traced as emitters ...` with one line per renderer (path, material, layer, bounds, whether it scrolls), the number of `lava pieces` in the `Emissive lights:` line, and `Lava lights: N point lights ... dimmed` with their names.

## Results

RESULTS

## Limits

- **Static-batched lava** (8-3: two lava cubes and the acid pool) is skipped, like static-batched water and glass. It would need StaticGroups support.
- `Lava Blue` (5-2) uses a particle shader and is not handled.
- VERTEX_DISPLACEMENT waves are not reproduced: the RT lava is the flat mesh.
- Acid's lit main colour is not an emitter: in the game it is lit by the level's lights.
- The light-dimming heuristic goes by position and colour. A non-lava light of the same colour over a lava pool is dimmed too.
