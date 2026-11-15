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
| camera (PathTrace primary) | `ULTRART_MASK_SCENE` | replace modes: yes, at `cameraGlow` (0.8) x its emission. Hybrid: no, the any-hit shader calls `IgnoreHit()` (see below). Camera rays are recognised by `ULTRART_PAYLOAD_WANT_MOTION`, which only PathTrace's primary ray sets |
| reflection (opaque surfaces, water/glass pass) | `ULTRART_MASK_SCENE` | yes: the lava's emission (texture x tint, scrolling) |
| bounce, radiance cache | `ULTRART_MASK_SCENE` | yes, as a black surface: emission x `_EmissiveBounceWeight` = 0, because the lava is sampled directly |
| shadow, volumetric shadow | 0x05 / 0x01 | no: the lava is the light source |
| transparent pass | 0x10 | no |
| emissive light sampling, volumetric emissive sample | triangle list | yes |

Lava submeshes are added without `ClosestHitOnly` so that the any-hit shader runs. Lava is one-sided even when the game's material is NoCull: lava lights upwards, and never the channel floor below it.

### Primary rays: the game's lava stays on top

The game draws its additive lava after our composite, so the final pixel is the game's lava plus whatever the composite put underneath. In the raster game that is the channel floor, dimly lit. Replacing the game's lava would mean suppressing its draw and reproducing its vertex waves, depth writes and fog, so the game's lava stays. What changes is the layer underneath, through the global `_UltraRTLavaCamera` (set every frame in `Plugin.Lava.cs`):

- **Replace modes (relight, pt-dn, pt): camera rays stop at the lava.** The pixel under the game's lava is the RT lava's own emission x `cameraGlow` (0.8), not the floor. In the first version camera rays passed through. RT then lit the floor under the lava far brighter than the game does (0-5 lava pose, mean sRGB of a lava patch: game (251, 214, 0), relight (252, 246, 9)), and the additive lava on top washed out to flat yellow with its dark crust gone. Lava over its own glow reads as lava: (252, 225, 10) at the same spot, with the crust visible.
- **Hybrid: camera rays pass through.** Hybrid keeps the game's own colour for the floor, and its lava already matched the raster game.

`-ultrart-lava-camera 0` makes camera rays pass through in every mode.

### Emission

The proxy material (`UltraRT/LavaProxy`, one per renderer so that it can follow that renderer's scroll) emits what the game adds:
- `_MainTex x _Color.rgb x _Color.a` for unlit lava (`Lava`, `LavaMagenta`);
- plus `_EmissiveTex x _EmissiveColor x _EmissiveIntensity x alpha` with EMISSIVE (acid). Acid's main colour is lit by the game's lights (VERTEX_LIGHTING), so only its EMISSIVE part glows.

`UpdateLava` copies `_ScrollOffset` from each source renderer's property block every frame, plus `Time.timeSinceLevelLoad` for `_ScrollSpeed`. Reflections of lava therefore flow like the lava itself.

For direct lighting, `EmissiveLights` reads the same material: Le = the texture average over each triangle x the base colour x `LavaSettings.lightScale` (1.5), then `EmissiveLightScale` (4) as for every emitter. See Calibration below.

**Splitting.** Lava surfaces are a few triangles of tens to hundreds of m², and the shader samples a uniform point on a triangle. A wall next to the lava would draw most of its candidates from metres away. `ExtractLava` splits lava triangles (and the game's opaque lava) at the longest edge until each piece is at most `maxTriangleArea` (1 m²), with at most `maxTrianglesPerSurface` (6,000) pieces per submesh. The alias table's camera-distance importance then puts most samples on the lava near the camera. Faces pointing down (a lava cube's bottom, inside the floor) are dropped.

**Not done: scrolling in the lighting.** The pieces (1 m², about 1.4 m across) are larger than one texture tile (UVs of about 1 per metre), so every piece already has the texture's average radiance, and scrolling would not change it. A moving light pattern would need pieces a quarter of a tile across, which is 16 times as many triangles. The pattern shows where the eye can see it: in reflections and on the lava itself.

### The game's lava point lights

ULTRAKILL fakes the lava glow with orange point lights along the lava. In 0-5 these are 6 lights of intensity 15 and range 30, 4.5 m over the lava. In 6-1 they are lights of intensity 10 and range 25 under `Lighting/Lava`. With the lava now emitting, keeping them would light the walls twice and drown out the lava's own falloff: at lava light x1 with these lights at full strength, 0-5's mean linear relight lighting only went from 5.27 to 5.30. `DimLavaLights` scales packed point lights that sit over an active lava surface (its bounds widened by 2 m, from 1 m below to 6 m above) and whose colour matches the lava's (rgb cosine >= 0.9) by `pointLightScale` (0.15). The game's own rendering is unchanged. Hybrid uses the same scale: its shadow ratio divides the dimmed lights out again.

### Calibration

All numbers are mean sRGB of the right wall at the 0-5 lava pose, 2560x1440. Auto exposure is on, so compare within one run only.
- Game: (129, 52, 1). Relight before: (211, 94, 11), blown out.
- Run 2: lights x0.4, lava light x1: (121, 50, 11). Lights x0.15, lava light x2.5: (120, 52, 11). Same mean, but now brightest at the bottom next to the lava, and darker higher up.
- Run 3, lights x0.15: lava light x2.5 (173, 79, 11), x1.5 (146, 65, 11), x1.0 (128, 56, 11). In 6-1 (right wall; game 35): 69, 56 and 48. In 1-3 hybrid (top half, green channel; game 80): 147, 118 and, with `EmissiveHybridAdd` 0.1, 105.

x1.5 is the default. Walls next to lava read as lit by it, and are 10-15 % brighter than the game where the game also has lava lights. In 1-3 (an outdoor lake with no lava lights) the black structure over the lake becomes orange, as in the game's art. Hybrid in 1-3 is yellower than the game (the 0.25 emissive hybrid add on top of the game's own fill); `-ultrart-emissive-hybrid 0.1` brings it closer.

## Settings

| Flag | Field (`LavaSettings`) | Default |
|---|---|---|
| `-ultrart-nolava` | `enabled` (scene build) | on |
| bench variant `lava.active=0` | `active` (runtime: the proxies leave the RTAS and the emitter list, lights are not dimmed) | on |
| `-ultrart-lava-light X` | `lightScale` (scene build) | 1.5 |
| `-ultrart-lava-glow X` | `glow` (emission seen by reflection, bounce and cache rays) | 1 |
| `-ultrart-lava-pointlights X` | `pointLightScale` | 0.15 |
| `-ultrart-lava-camera X` | `cameraGlow` (replace modes; 0 = camera rays pass through) | 0.8 |
| `-ultrart-lava-loglights` | log every light within 12 m of a lava surface, and why it is or isn't dimmed | off |
| | `maxTriangleArea`, `maxTrianglesPerSurface` | 1 m², 6,000 |

The build log prints `Lava surfaces: N renderers traced as emitters ...` with one line per renderer (path, material, layer, bounds, whether it scrolls), the number of `lava pieces` in the `Emissive lights:` line, and `Lava lights: N point lights ... dimmed` with their names.

## Results

Bench: `bench/suites/lava.json` (0-5's lava, b, c and d poses, plus poses at 1-3's lava lake, 6-1's lava rim and 1-2's lava room, with 0-5 and 1-3 noise bursts). It A/Bs in one run with the variant `lava.active=0`. `bench/suites/lava-calib.json` holds the calibration variants. The poses outside 0-5 use `Load: areas` with hand-made area files (`bench/recordings/lava-*-areas.json`, generated offline from the level bundles), which switch on the lava's room. They show the lava without the level's fog or progress state. Poses generated the same way for 4-1 (pit under a floor) and 3-1 (acid) missed the lava and were dropped.

**Look** (contact sheets in `bench/results/20261114-145318_lava/contact_*.png` and `20261114-150256_lava-calib/contact_final.png`):
- 0-5: relight no longer blows out (walls, see Calibration). The lava keeps its texture and dark crust, and the walls are brightest next to the lava. pt-dn is the same. Hybrid is close to the game, with a little extra glow near the lava.
- 1-3 lava lake: before, the structure over the lake was black in relight (no light reached it). Now it is lit orange from below.
- 6-1 lava rim: the pillar and walls next to the lava pick up its glow.
- Reflections: metal and water reflect the scrolling lava texture (reflection rays see the proxy's emission).
- Volumetrics: the lava is in the emissive table, so the haze over it glows (docs/volumetrics.md).

**Cost** (RTX 5070 Ti, 2560x1440, trace scale 0.5, GPU timer dispatch ms, lava off -> on in the same run):

| Pose | relight | pt-dn | hybrid |
|---|---|---|---|
| 0-5 lava | 2.915 -> 2.892 | 2.940 -> 2.889 | 2.611 -> 2.795 |
| 1-3 lava | 2.402 -> 2.336 | | |
| 6-1 lava | 2.649 -> 2.592 | | |
| 1-2 lava | 1.942 -> 1.999 | | |
| 0-5 c (run 2) | 2.375 -> 2.411 | | |

Within about +-0.06 ms, except hybrid 0-5 at +0.18 ms. There, camera rays run the any-hit shader for every lava triangle they cross, which is the one non-opaque instance on screen. The emitter list grows by 3,000 to 14,000 lava pieces per level (0-5: 11,708). That costs about 10-30 ms more at the scene build, and the alias table rebuild takes about 0.1-0.3 ms. Plugin CPU per frame is unchanged within noise (0-5: 0.33 -> 0.35 ms). `merge-regress` against master (b6ea2ba, same day) is not slower anywhere: 0-1 metal relight 3.068 -> 3.029, torch 2.356 -> 2.306.

**Noise** (64-frame bursts, luma 0-255, all pixels, sigma / block sigma, lava off -> on):

| Pose | relight | pt-dn | hybrid |
|---|---|---|---|
| 0-5 lava | 4.20 / 3.15 -> 4.42 / 3.13 | 3.82 / 2.61 -> 5.73 / 4.09 | 5.08 / 3.38 -> 5.50 / 3.78 |
| 1-3 lava | 10.29 / 6.57 -> 11.67 / 7.88 | | |

Relight and hybrid stay within about 10 %. pt-dn 0-5 is +50 % in absolute luma. The sigma maps put it on the walls and the upper structure, which are now brighter (lit by the lava) and noisy at their texture edges, as before. One lava sample per pixel competes with many lights. More candidates (`ls.candidates`) or solid-angle sampling of emitter triangles would be the next steps. These bursts ran at lava light x2.5; x1.5 is the default.

## Limits

- **Static-batched lava** (8-3: two lava cubes and the acid pool) is skipped, like static-batched water and glass. It would need StaticGroups support.
- `Lava Blue` (5-2) uses a particle shader and is not handled.
- VERTEX_DISPLACEMENT waves are reproduced on the traced lava surface since 2026-11-15 (docs/scene-proxies.md, "Vertex displacement"), but the emitter table keeps the rest-pose triangles (amplitude 0.25: lights sit up to about 0.1 m off the surface). Static-batched lava and acid (8-3) keep the rest pose.
- Acid's lit main colour is not an emitter: in the game it is lit by the level's lights. Acid was not checked in game (3-1's generated pose missed it).
- `LavaMagenta` in 8-4 is Geryon's projectile charge spheres (moving), not a pool. They are traced like any lava renderer.
- No lighting pattern from the scrolling texture (see Emission). The lava's light is steady.
- `lava.lightScale` is read at the scene build. Bench variants for the level use `em.lightScale` (all emitters) instead.
- The light-dimming heuristic goes by position and colour. A non-lava light of the same colour over a lava pool is dimmed too.
