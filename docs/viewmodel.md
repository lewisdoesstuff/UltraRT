# Viewmodel lighting

The first-person arms and guns (the viewmodel) are on screen all the time. Before this pass, relight (and pathtrace and pt-dn) relit the whole world, but the viewmodel kept the game's own vertex lighting: no RT shadows on it, no coloured bounce, no reflections on its metal, and muzzle flashes and explosions lit it the game's way. `Plugin.Viewmodel.cs` now lights it with the same lights, shadow rays, radiance cache and reflections as the world, in relight, pt-dn and hybrid.

Files:
- `src/UltraRT/Plugin.Viewmodel.cs`: renderers, materials, targets, and the command buffer on the HUD camera.
- `unity/.../ViewmodelGBuffer.shader`: re-draws the viewmodel into a G-buffer.
- `unity/.../Viewmodel.raytrace`: the lighting rays. It is a separate shader because PathTrace is at Unity's 8-UAV limit; this one has 4 UAVs.
- `unity/.../ViewmodelFilter.compute`: temporal accumulation, à-trous, and the bounds measurement.
- `unity/.../ViewmodelComposite.shader`: upsampling and the composite.
- Small hooks:
  - `UltraRTLights.hlsl`: `ULTRART_LIGHT_LAYER_TEST`, an overridable light/layer test. The default is the old test, so other shaders compile to the same code.
  - `RadianceCache.BindLookupOn` and `SceneProxies.ClassifyMaterial` / `SourceAlphaTested` (now internal).
  - `Plugin.cs`: one line per hook, GPU timer slots `viewmodel` and `vm-*`. Ctrl+F7 now toggles the viewmodel pass; plain F7 still toggles the view.
  - `ultrart-materials.json`: metal classes for the weapon and arm materials.
- `bench/suites/viewmodel.json` (validation), `viewmodel-quick.json` and `viewmodel-cost.json`.

## 1. How the game draws the viewmodel

From the decompiled `Assembly-CSharp` (`PostProcessV2_Handler`, `CameraController`, `GunColorGetter`, `ViewModelFlip`) and the plugin's log (`Viewmodel RT attached to ...`, `Viewmodel renderers under Main Camera: ...`):

- **Camera.** `Player/Main Camera/HUD Camera` is a child of Main Camera at local identity, so it has the same position and rotation.
  - Settings: depth 0 (Main Camera −1, so it renders after the world), clear **Depth**, culling mask `0x00002000` (layer 13 "AlwaysOnTop" only), forward path, near 0.1, far 1000.
  - FOV is **90**, whatever the main FOV is (105 here). `CameraController` eases it to 90, or to the zoom target while zooming.
  - On screens narrower than 16:9 its local scale x is `min(aspect / 1.778, 1)`.
- **Targets.**
  - `PostProcessV2_Handler` calls `hudCam.SetTargetBuffers(mainTex.colorBuffer, depthBuffer.depthBuffer)` in every `OnPreRender`: the same ARGB32 colour and depth buffers as the world.
  - The world camera renders MRT (`mainTex`, `Reusable A` RG16 for outlines, `View Normal` RGB565). The HUD camera only writes colour, so the viewmodel writes no normals.
  - Because the HUD camera clears depth, after its opaques the depth buffer holds only viewmodel depth, in the HUD projection.
  - PostProcessV2 (palette, pixelation) runs afterwards on `mainTex`, so whatever we composite there gets the game's post-processing like the world does.
- **Renderers.** Everything on layer 13 under Main Camera: `Guns/<weapon>(Clone)/...` and `Punch/<arm>(Clone)/...`. There are 49 renderers with all weapons unlocked. Most are skinned (`Revolver_Body`, `RightArm`, `Feedbacker`, ...), and only the current weapon and arm are active.
  - Opaque ones use `ULTRAKILL/Master` with `VERTEX_LIGHTING` and `_VERTEXCOLORS_ON`, often `EMISSIVE` (the revolver's charge light, railcannon pips).
  - Transparent parts (queue 3000: battery monitor `psx/unlit/ambient`, charge sprites, `HeatSinkHeat` additive, nailgun barrel heat) are drawn by the game after the opaques and are left to it.
  - The weapon materials have `_SurfaceType` Generic, so the classifier gave them no reflections.
  - The game lights the viewmodel with Unity's per-object vertex lights whose culling masks include layer 13. The sun and the outdoor fill lights are masked to the Outdoors layers (24/25), so the game's gun never gets sunlight.
- **Alternate and custom gun colours.** `GunColorGetter` swaps in `coloredMaterials` (e.g. `Pistol New CustomColor`, keyword `CUSTOM_COLORS`, with an `_IDTex`) and sets `_CustomColor1-3` in the renderer's MaterialPropertyBlock.
  - The Master shader's compiled code (`shaderdump/master_glsl.txt`) gives the albedo as: `id = _IDTex(uv)`, `c = id.a`, then `c = lerp(c, id.a * _CustomColorN.rgb, 0.8 * id[N])` for N = r, g, b.
  - The alpha of each custom colour scales the game's cubemap reflection. We don't use it.
- **Other.** `ViewModelFlip` mirrors the weapon root (local scale x = −1) for left-handed hold. The bench takes the player over, so weapon bob and sway were not exercised in testing (see Limits).

## 2. Design

A command buffer on the HUD camera at **AfterForwardOpaque**: after the game drew its viewmodel, before the viewmodel's transparents (muzzle sprites, beams) and the UI. `ViewmodelPreRender` records it every frame, but only when Main Camera traced this frame in relight, pathtrace, pt-dn or hybrid (`ViewmodelTraced`, at the end of `Trace`).

1. **G-buffer (full resolution).** `cmd.DrawRenderer` for every active opaque submesh of the viewmodel renderers, with a per-source-material copy of `ViewmodelGBuffer.shader`. It draws on the HUD camera, so its projection and pixels are the game's, into five targets with its own depth buffer:

   | Target | Format | Contents |
   |---|---|---|
   | albedo | ARGB32 | rgb albedo, a = lit weight |
   | normal | ARGBHalf | world normal (two-sided, facing the camera), w = material index |
   | position | ARGBHalf | world position − camera, w = frame stamp |
   | emission | RGB111110Float | emission |
   | motion | ARGBHalf | screen-uv and view-depth delta to last frame |

   - **Albedo:** `_MainTex × _Color × vertex colour` (`_VERTEXCOLORS_ON`). Under `CUSTOM_COLORS` it is the `_IDTex` formula above, with the colours from the renderer's property block.
   - **Stamps:** the targets are never cleared (stamp `frame % 2000 + 1`; a clear pass every 1000 frames), so only viewmodel pixels cost bandwidth.
   - **Motion** is each vertex's motion relative to the camera since last frame:
     - the rigid part per renderer, `prevModelView × modelView⁻¹` (root bone for skinned ones): bob, sway, recoil of the whole weapon;
     - for skinned renderers (`Revolver_Body`, `RightArm`, `Feedbacker`, ...) also the bone animation: the vertex shader reads last frame's skinned position of the same vertex (`SV_VertexID` + the submesh's base vertex) from `_VMPrevVerts`, a copy of the renderer's skinned vertex buffer that a small kernel (`ViewmodelFilter.compute`, `CopyVerts`) makes right after the G-buffer draws. `SkinnedMeshRenderer.vertexBufferTarget` gets `Raw` so the buffer can be read. A renderer that was not drawn last frame (weapon switch) falls back to rigid motion for one frame. `plugin._vmSkinnedMotion=0` / `-ultrart-vm-skinned off` turns it off.
   - **Materials:** emission as in `SceneProxies.Proxy`, sampled at the main texture's uv (`uv0 × _MainTex_ST`, as the game's Master shader does; it used `_EmissiveTex_ST` on top until 2026-11-15). `ULTRAKILL/Master` without `VERTEX_LIGHTING` is fullbright (lit weight 0). Reflection parameters come from `MaterialClassifier` and go into a small buffer (`_VMMaterials`, 2 float4 per material).
2. **Rays (half resolution, `Viewmodel.raytrace`).** Each half-res pixel takes the first covered pixel of its 2×2 block and lights the world point where the gun really is: camera position + the stored offset. Lighting is computed like a PathTrace primary hit:
   - **Lights.** The lights that light the viewmodel are those whose culling mask includes layer 13 or the layer under the player. That layer comes from a 6 m physics raycast down; baked 6/7 map to 24/8. So the sun reaches the gun outdoors, and the RT shadow keeps it out indoors. `-ultrart-vm-lightmask HEX` overrides this.
   - **Direct light:** fill lights plus `_VMDirectSamples` (1) RIS samples over the light and emissive tables (`RisCandidates`), each with one shadow ray. The unshadowed sum (`_VMDirectOut`) feeds the filter's luma moments (it was also hybrid's ratio until 2026-11-19).
   - **Bounce:** one cosine ray, its hit lit from the radiance cache. A sky miss counts the full sky (`ULTRART_SKY_NO_NEE`: no separate sky sample).
   - **Reflection:** one ray on materials whose specular weight is worth it, using the same `SpecularTerms` and threshold as the world. By default it goes along the mirror direction (`_vmStableRefl`), with no GGX sample: stable, and the filter's blur stands in for roughness. The hit is shaded like PathTrace's reflections (cache by footprint, emission, sky).
   - **Masks:** the viewmodel is not in the RTAS. It never shadows the world or itself, and no ray sees V1's third-person model (0x80), which sits exactly where the first-person gun is.
   - **Latency.** The pass is latency-bound: few threads, each a chain of dependent rays. Halving the thread count saved only 0.05 of 0.26 ms, and a 1×1 dispatch costs 0.01 ms, so there is no fixed overhead. So with `_vmCheckerboard` (default):
     - glossy pixels alternate (checkerboard, swapping every frame) between the diffuse rays and the reflection ray;
     - secondary hits never trace a shadow ray: the cache, or the unshadowed fill lights on a cache miss.

     This brought the rays from 0.27 to 0.14 ms.
3. **Filter (`ViewmodelFilter.compute`, half resolution, over the viewmodel's rectangle).** The gun moves with the camera, so history lives in screen space:
   - **Temporal.** Reprojects with the G-buffer's rigid motion and validates by view depth and frame stamp. Clamps the history to the 3×3 neighbourhood (mean ± 3σ of this frame's samples) and blends with 1/n: n ≤ 32 for lighting and the shadow ratio, ≤ 12 for reflections.
   - **Fast response.** When the 3×3 mean of this frame's samples leaves the history by more than `_vmFastSigma` (3) standard errors of that mean plus `_vmFastTol` (30%) of the luminance, history is cut to 2 frames. On frames where the light set changed (`LightSampling.LightSetChanged`: a muzzle flash or explosion light appeared) the tolerance drops to `_vmFastTolChange` (10%). Without this, muzzle flashes barely showed on the gun. The first version used a plain ratio test (more than 1.8× or less than 0.55×, and every light-set change); at 1 sample per pixel the 3×3 mean of a dim room crossed those bounds all the time, so the gun's history kept being cut to 2 frames (see "Noise" below). `plugin._vmFastSigma=0` restores it.
   - **Clamp.** The history is clipped to the 3×3 mean ± 3 × max(the 3×3 spread, the history's own temporal spread). The temporal spread is a moving average of luma² carried in the direct-light history's z. With the 3×3 spread alone, a neighbourhood whose 9 samples all missed the bright light had almost no spread and pulled the history down.
   - **History search.** When none of the 4 bilinear taps passes the depth test, the closest-depth texel of the 3×3 around the reprojected point is used (within twice the tolerance), as in the world's denoiser. The gun idles: its bones move a little every frame, which the per-renderer rigid motion doesn't carry, so silhouettes and creases used to lose their history every frame (`plugin._vmHistorySearch=0`).
   - **À-trous.** Three 3×3 iterations (steps 1, 2, 4), edge-stopping on depth and normal. Short histories get the full blur; long ones keep at least half. Reflections are filtered only above roughness 0.1.
   - **Bounds.** A small kernel measures the bounding rectangle of this frame's viewmodel pixels for the next frames (below).
4. **Composite (`ViewmodelComposite.shader`, full resolution, scissored).** A pixel is replaced only where this frame's G-buffer covers it and the game's depth matches ours. The game's depth buffer is read directly; the composite binds only the colour target, so no copy is needed. Anything the game drew in front of the gun keeps the game's colour. Lighting is upsampled from the 4×4 half-res neighbourhood with bilinear × depth × normal weights, using viewmodel samples only (no halo from the world behind). Thin parts with no bilinear sample take the most similar neighbour.
   - **relight / pathtrace / pt-dn** (pass 0): `(albedo × lerp(1, lighting, lit) × keep + specW × refl) × exposure + emission`, then relight's tone curve. These are the same formulas, globals and auto-exposure buffer as the denoiser's Composite and RR's `Prepare`/`Finish`, so the gun sits in the world's exposure. The world's area colour match gain (docs/relight.md, "Area colour") is deliberately not applied: it corrects the tint of the area's surfaces, but the gun is lit by other light. In 7-3's red flashlight room the gain was (0.89, 1.24, 1.12), and applying it turned the flashlit gun cyan.
   - **HDR** (pass 1): pass 0 plus the late gain map write ("HDR displays" below; pass 2 until 2026-11-19).
   - **hybrid** (removed 2026-11-19; was pass 1, `Blend One SrcAlpha`): `game colour × lerp(1, shadow ratio, lit) × keep + GI × albedo × bounce × lit × keep + specW × refl`, as `Composite.shader` does for world pixels. The shadow ratio is a luminance ratio, so the multiplier is a scalar alpha, and no copy of the game's colour is needed. `-ultrart-vm-hybrid off` / `[Viewmodel] Hybrid` leaves hybrid's viewmodel to the game.

**Screen rectangle.** Renderer bounds alone are useless here: skinned arm bounds contain the camera, so the projected box is the whole screen. The CPU rectangle from bounds (corners behind the near plane are pulled onto it) is therefore intersected with the GPU-measured bounds of the last frames (async readback, typically 1–3 frames late), padded by 64 px. When the viewmodel touched the edge of the rectangle it was measured in, the CPU rectangle is used instead, so a punch or weapon switch that leaves the rectangle is caught the next frame. A typical revolver pose is 16 % of the screen.

**Settings.** Plugin fields, so bench variants can set them, e.g. `relight+plugin._vmEnabled=0`:

| Field | Default | Meaning |
|---|---|---|
| `_vmEnabled` | true | the pass; config `[Viewmodel] Enabled`, `-ultrart-viewmodel on\|off`, Ctrl+F7 |
| `_vmDebug` | 0 | 1 coverage (green = replaced, red = depth mismatch), 2 lighting, 3 albedo, 4 reflection, 5 history length; magenta = no lighting sample; `-ultrart-vm-debug N` |
| `_vmSamples` | 1 | direct-light samples (shadow rays) per pixel; `-ultrart-vm-samples` |
| `_vmCheckerboard` | true | split glossy pixels between diffuse and reflection rays; no shadow rays at secondary hits |
| `_vmStableRefl` | true | mirror-direction reflection rays |
| `_vmReflGraze` | 0.5 | below this cos(normal, view) the reflection is blended towards the gun's own lighting (section 7); 0 = the mirror reflection everywhere (before 2026-11-21) |
| `_vmSkinnedMotion` | true | per-vertex motion of skinned renderers (bone animation) in the motion vectors; `-ultrart-vm-skinned on\|off` |
| `_vmNdlPow` | 2.2 | cosine exponent of the player's own lights on the gun under linear transport; above 1 also caps their falloff at the game's curve (section 6); 1 = the physical model |
| `_vmMaxHistory`, `_vmMaxHistoryRefl`, `_vmClampK` | 32, 12, 3 | temporal filter; `-ultrart-vm-history` |
| `_vmFastSigma`, `_vmFastTol`, `_vmFastTolChange` | 3, 0.3, 0.1 | fast response: standard errors, relative tolerance, relative tolerance on light-set changes; sigma 0 = the old ratio test |
| `_vmHistorySearch` | true | 3×3 history search when the bilinear taps fail |
| `_vmCandidates` | 32 | RIS candidates per direct-light sample (0 = the world's 8); light evaluations only, no rays |
| `_vmAtrous` | 3 | à-trous iterations (0–3) |
| `_vmDepthTol` | 0.03 | composite depth-match tolerance (fraction of depth + 5 mm) |
| `_vmFullScreen` | false | dispatch over the whole screen (`-ultrart-vm-fullscreen`) |
| `_vmLightMask` | 0 | 0 = automatic (13 + ground layer); `-ultrart-vm-lightmask HEX` |
| `_vmRayTest` | 0 | measurement only: 1 = a single ray-gen thread |

Log lines: `Viewmodel RT attached to ...` (HUD camera settings), `Viewmodel renderers under Main Camera: ...` (every renderer and material with its class), `Viewmodel draws: ...` (whenever the drawn set changes, flagged `custom colours`), `Viewmodel skinned motion: ...` (once per skinned renderer: vertex count, bytes per vertex, position offset), every 20 s `Viewmodel RT: N draws ..., rect ..., light mask ...`, and every 2 s (10 at most, plus at every bench screenshot with effects) `Viewmodel lights at the camera`: the strongest lights reaching the gun, with distance, range, the game's falloff, spot cone, culling mask (whether the game lights its own gun with it: layer 13) and the player-light flag.

## 3. Results

RTX 5070 Ti, 2560×1440, trace scale 0.5, GPU timer. Results in `bench/results/20261114-160707_viewmodel` (validation suite) and `20261114-160416_viewmodel-quick` (not committed). Side-by-side crops (off / relight with the pass off / relight / hybrid) are in `compare_*.png` in the first folder.

**GPU cost** (slot `viewmodel`; it is not part of `total`, which ends with Main Camera):

| Pose | relight | hybrid | of which rays |
|---|---|---|---|
| 1-1 start (sun, revolver + arm, 16 % of screen) | 0.256 | 0.261 | 0.137 |
| 1-1 revolver / explosion | 0.258–0.259 | 0.268 | 0.139–0.149 |
| 1-1 approach / doors runs (moving) | 0.245–0.257 | 0.245–0.258 | 0.124–0.137 |
| 0-1 metal | 0.237 | 0.238 | 0.115 |
| 1-4 lamps | 0.265 | 0.269 | 0.145 |
| 0-5 lava | 0.235 | 0.244 | 0.114 |
| 1-1 torch hall | 0.232 | 0.236 | 0.112 |

The rest is roughly constant (`20261114-160416_viewmodel-quick`, 1-1 start): G-buffer 0.043, filter 0.048, composite 0.029.

How it got there (1-1 start, relight):

| Version | Total ms |
|---|---|
| first version: full-screen rect, depth and colour copies, all rays at every pixel, secondary shadow rays | 0.47 |
| GPU-measured rect | 0.43 |
| checkerboard split and cache-only secondary hits | 0.31 |
| no copies | 0.27 |

`plugin._vmCheckerboard=0` costs 0.39; `_vmFullScreen=1` costs 0.35. Frame time with and without the pass is within run-to-run noise at ~230 fps.

**What it looks like** (compare images):
- **1-1 start:** the gun is lit by the sun and sky; its underside and the arm facing away are darker, consistent with the fountain rim next to it. Hybrid stays close to the game's look with RT shading.
- **1-1 approach run, 3 s:** next to the red-lit wall at the spawn, the gun and arm pick up red bounce; the game's gun stays neutral.
- **0-5 lava:** strong orange bounce from below on the grip and arm.
- **0-1 metal:** red bounce from the walls, and the metal gun and arm reflect the room.
- **Muzzle flash** (1-1 revolver, torch hall + revolver): the bench's revolver effect lights the gun warm orange in relight, as the game's own flash lights it. The first version with a 12–32 frame history showed almost nothing; the fast-response rule fixed it.
- **Explosion at 6 m:** a slight warm lift on the side facing it, about what the game shows.
- **Coverage** (`_vmDebug=1`): the replaced area matches the game's viewmodel exactly, with no red pixels. Transparent parts (battery monitor, charge sprite) keep the game's drawing.
- **Alternate colours:** with the user's alternate preset (`Pistol New CustomColor`: brown grip, dark body), the RT gun matches the game's colours. Without the `_IDTex` path it came out in the default grey.

**Noise** (1-1 start, 32-frame burst, masked sigma): relight 0.31 with the pass off, 0.43 with it (0.37 with GGX reflections instead of mirror rays), hybrid 0.29. The sigma maps show the bright outline of the gun silhouette in every RT mode, including with the pass off. It comes from the world's upsampler at the depth edge, not from this pass. Inside the gun the residual is low-frequency blotching from the 1-spp direct light that the 3×3 clamp lets through.

## 4. Limits

- **No self-shadowing and no shadow onto the world.** The viewmodel is not in the RTAS, and V1's third-person model (which is) is excluded from these rays because it overlaps the first-person gun. Arm-on-gun occlusion is missing; Quake II RTX has it.
- **Motion noise is sample noise.** Bone animation is in the motion vectors since 2026-11-15 (section 6), and the history survives bob, walking and recoil. What is left in motion is the 1-spp direct light (low-frequency blotches after the à-trous), and while firing, the history cut that lets a muzzle flash show. Only more samples reduced it (`_vmSamples=2`: −18 %, about +0.1 ms).
- **Light-change responsiveness** uses luminance ratios and the global light-set flag. A slowly fading effect light lags by a few frames.
- **Materials.** Gun and arm classes come from shipped name rules (`ultrart-materials.json`: weapons MetalLow r 0.4 w 0.6, arms MetalThin r 0.5 w 0.4, wildcards so the `CustomColor` variants match too). There are no per-texel PBR maps for the viewmodel, and the custom colours' alpha (the game's per-colour shininess) is ignored. The wooden revolver grip is treated as metal.
- **Affine texture warping and vertex snapping.** If the game's options enable them, the game draws the viewmodel warped and our G-buffer doesn't. Coverage is then still matched by depth, but albedo can differ by a texel near large triangles.
- **Bounce sky light** comes from one cosine ray, with no direct sky sample. Outdoors it is a little noisier than world surfaces, which also sample the sky directly.
- **HUD camera only.** Weapon previews in menus and the shop's 3D models are not lit.

## 5. Validation suites

- `bench/suites/viewmodel.json`:
  - 1-1 start with bursts, revolver, explosion;
  - 1-1 approach and doors recordings (moving camera);
  - 0-1 metal, 1-4 lamps, 0-5 lava;
  - 1-1 torch hall with the revolver. This is the one slow load (22 s approach).
- `viewmodel-quick.json`: 1-1 start with debug views, revolver, 1-4 lamps (~1.5 min).
- `viewmodel-look.json` (2026-11-15): the two 7-3 flashlight poses, the revolver shot at 0.03 s and 0.1 s, 1-1 start, each with `_vmEnabled=0` and `_vmNdlPow=1` (the master look).
- `viewmodel-mannequins.json` (2026-11-21): P-2 revolver shots and 7-3 shots with `plugin._vmReflGraze=0` as the before (section 7), and the 7-1 mannequin room.
- `viewmodel-motion.json`: viewmodel motion noise with `MotionViewmodel` and `GunBob`: 1-1 start bob only, bob + revolver shots every 0.25 s (recoil), 1-4 lamps bob + shots, the 1-1 doors walk; each with and without skinned motion.
- `viewmodel-pt.json`: RR warm-up, then 1-1 start, 1-4 lamps and 7-3 in relight, `pathtrace` and `pathtrace` with the pass off.
- `viewmodel-cost2.json` (`-Perf`): cost with the revolver held, while firing, and in `pathtrace`.
- `viewmodel-diag.json`: the filter variants behind "Motion noise" in section 6.
- `viewmodel-cost.json`: cost variants.

## 6. Motion vectors, player lights, emission (2026-11-15)

Results in `bench/results/20261115-023917_viewmodel-{look,motion,pt}_alt`, `20261115-024645_viewmodel-diag_alt`,
`20261115-024925_viewmodel-diag_alt` and `20261115-025255_viewmodel-cost2` (not committed). RTX 5070 Ti, 2560×1440, trace scale
0.5. The alt install holds the user's alternate gun colours (`Revolver Ricochet`, `RocketLauncherCustom`: `custom colours` in
the draw log) and the blue Feedbacker arm.

### Bench additions

- **The revolver fires as the player fires it.** Effect kind `revolver` now calls the held revolver's own `Revolver.Shoot(1)`:
  the beam with the gun barrel as its start point, the muzzle flash sprite at the barrel, the recoil animation and the FOV kick.
  The old spawn (now `revolverbeam`) started the beam at a fixed point right of the camera and played no recoil. The beam's
  muzzle light itself sits at the camera in both cases, because that is where the game puts it: `RevolverBeam` is instantiated
  at the camera (`cc.transform.position`), and only its child sprite moves to the barrel. Logged: `EffectSpawner: revolver shot
  by ...`. If no revolver is out, the beam alone is spawned, starting at the barrel.
- **`Weapon`** (scenario): the slot to hold (`GunControl.SwitchWeapon`; 1 = revolver). The saves in both installs hold the
  rocket launcher, so before this the "revolver" scenarios never showed a revolver.
- **`GunBob`**: the gun moves as `WalkingBob` moves it at full walking speed, which the bench never did because it takes the
  player over.
- **`MotionViewmodel`**: the motion-noise replay also measures the viewmodel's own pixels and counts history hits and misses in
  the filter (bench/README.md, "Motion noise"). Each pass restores the viewmodel animators, the FOV and `Random`, so the gun
  animates identically in every pass.

### Motion: skinned motion vectors

The diagnosis was that the gun crawls in motion because bone animation is missing from its motion vectors. The skinned
motion vectors are in (section 2, "Motion"). Measured with the new replay statistics (relight, `vol=0`, three passes,
history hits per viewmodel pixel and frame):

| Window | rigid only: bilinear hit / rejected | skinned: bilinear hit / rejected | viewmodel sigma rigid → skinned |
|---|---|---|---|
| 1-1 start, gun bob | 99.9 % / 0.04 % | 99.9 % / 0.05 % | 0.91 → 0.94 |
| 1-1 start, bob + shots (recoil) | 94.3 % / 3.93 % | **97.3 % / 2.51 %** | 2.09 → 2.25 |
| 1-4 lamps, bob + shots | 93.7 % / 4.36 % | **97.0 % / 2.72 %** | 2.70 → 2.65 |
| 1-1 doors walk | 99.9 % / 0.01 % | 99.9 % / 0.01 % | 1.03 → 1.02 |

- Recoil is where bone animation matters. With skinned motion, about 40 % fewer pixels lose their history (2.5–2.7 % instead
  of 3.9–4.4 %). The remaining rejections are expected to be disocclusions (parts hidden last frame); not broken down.
- Bob, walking and idling never lost history: rigid motion already covered them (99.9 %).
- The viewmodel's motion sigma did not move beyond run-to-run spread (about ±0.06 on the bob window). So the crawl the user sees
  is not history loss. The bob window was tested with these variants (`viewmodel-diag.json`):

  | Variant | 1-1 bob | 1-4 bob |
  |---|---|---|
  | default | 0.93–1.03 | 0.50 |
  | fast response off (`_vmFastTol=2`) | 1.03 | |
  | history 64 | 1.04 | |
  | clamp k 6 / k 6 + history 64 / no clamp | 0.95 / 0.75 / 0.76 | 0.47 / 0.58 / 0.57 |
  | no checkerboard | 1.04 | |
  | **2 direct samples** | **0.84** | |

  Only more samples helped consistently. The noise is the 1-spp direct light: low-frequency blotches that the à-trous leaves
  (block4 ≈ sigma). While firing, the error image shows the flash-lit part of the gun, where the fast response keeps only 2
  frames of history on purpose. Defaults are unchanged.
- Cost: +0.006 ms (G-buffer 0.043 → 0.049; one copy dispatch per skinned renderer, ~2,700–2,900 vertices × 40 bytes each).

### Player lights on the gun (7-3's flashlight)

User report: under 7-3's white flashlight the RT gun came out near-white while the game's stayed readable. The new light log
(`Viewmodel lights at the camera`) gives the facts. The flashlight is `Player/Main Camera/Flashlight`, a spot light with
colour × intensity 3, range 120 and angle 35, about 10 m behind and above the eye. Its mask includes layer 13, so the game
lights its own gun with it too. The eye light (`Player/Point Light`, 0.5, range 15) is the only other player light.

- **Cause.**
  - With linear transport the composite shows lighting^(1/2.2). The game lights its gun per vertex with a plain gamma Lambert,
    `I × falloff × cos`. Relight's physical cosine therefore turns into cos^0.45 on screen: a face at 0.2 shows 0.48 instead of
    0.2.
  - A light carried behind the view lights every face of the gun that faces the camera, and with intensity 3 they all saturate.
    The gun went flat and near-white, losing the shading the game has.
  - At 10 m the relight falloff is also 1.4× the game's (0.97 against 0.70 in linear), because the near-light cap is 3× the game.
  - Exposure was not the cause: auto exposure was darkening (×0.64). Neither was the mask, which the game shares.
- **Fix** (`Viewmodel.raytrace`, `ULTRART_PLAYER_LIGHT_NDL_POW` in `UltraRTLights.hlsl`; other shaders compile as before).
  At the gun's primary hits, the player's own lights (the player-light flag: flashlight, eye light, muzzle flashes, a held torch)
  use cos^2.2, so after the display transform they shade like the game's gamma Lambert. Their falloff is also capped at the
  game's own curve. Level lights, the sun, and bounce and reflection hits keep the world's model.
- **Rejected alternatives.**
  - Gamma Lambert for every light darkened the sunlit gun at 1-1 start by 10 % below the game.
  - The world's area colour match gain is not applied to the gun (section 2, composite). In 7-3's red flashlight room it turned
    the flashlit gun cyan.

Mean / 90th percentile luma over the gun (game / pass off, i.e. the game's own gun over the RT world / master look
`_vmNdlPow=1` / branch):

| Pose | game | pass off | master | branch |
|---|---|---|---|---|
| 7-3 flashlight shadow (user-1114-late) | 54.9 / 114 | 56.8 / 116 | 79.3 / 144 | **55.2 / 103** |
| 7-3 flashlight (user-1114-pm, red room) | 65.7 / 135 | 67.1 / 136 | 114.8 / 204 | **84.4 / 162** |
| 1-1 start (sun) | 82.9 / 146 | 88.6 / 155 | 79.1 / 155 | 77.5 / 154 |

The red-room pose stays about 28 % above the game. That is RT bounce from the red walls and the flashlit floor, plus the
hallway light (a level light, at relight's +0.49 EV local-light reference), which the game's ambient-free vertex lighting
doesn't have.

### Emission uv

`ViewmodelGBuffer.shader` sampled `_EmissiveTex` at `uv × _EmissiveTex_ST` on top of the main texture's `_MainTex_ST`. It
now samples at `uv0 × _MainTex_ST`, as the game does (docs/scene-proxies.md). Our material copies never set
`_EmissiveTex_ST`, so it was identity and the gun's emission was already in the right place. The fix removes the trap for a
weapon material with its own emission tiling. Checked: the revolver's green charge pips (1-4 lamps, 7-3) sit where the game
draws them. Coins are thrown objects, not viewmodel renderers.

### Muzzle flash

At 0.03 s after the shot, the beam's light (colour × intensity 7.8 by then, at the eye, flagged as a player light) is in the
viewmodel's light list, and the gun's lighting saturates the lighting debug view on 66 % of its pixels. The final gun is
nonetheless darker than the game's: mean luma 124 against 215 in the game and 182 for the game's own gun over the RT world.
1-1's sunny exposure (×0.45) and the tone curve's shoulder hold it below white, whereas the game whites the gun out for a few
frames. That is the world's exposure model, not the viewmodel pass, and is left as is.

### pathtrace

The viewmodel composite already uses RR's formula (`(albedo × L^(1/2.2) × keep + specW × refl) × exposure + emission`, tone
curve; RR's `Prepare`/`Finish`). Its texels are exact at screen resolution, because it draws its own full-resolution
G-buffer after RR. Gun luma, pathtrace against relight with RR on: 1-1 start 93 / 102, 1-4 lamps 69 / 72, 7-3 58 / 60. There
was no seam or exposure jump between the gun and the RR world, so nothing needed changing. Cost is the same as in relight.

### HDR displays (2026-11-18)
With HDR output on and a late gain map this frame (docs/hdr.md, "Effects and bloom"), the composite draws with pass 1 (pass 2 before hybrid's pass went on 2026-11-19):
pass 0 unchanged, plus a UAV write of the gain of the gun's glowing parts (`HDREmissive`, `UltraRTHdrEmissive`) and the
gun's own colour into the late map, so the charge pips go above paper white and the world's gain behind the gun can't
leak onto it. The game's transparents on the gun (muzzle flash, charge sprites) stay at paper white.

### Cost (`viewmodel-cost2`, `-Perf`, exclusive)

| Pose | relight | hybrid | pathtrace | of which rays |
|---|---|---|---|---|
| 1-1 start, revolver held (sun) | 0.311 | 0.323 | | 0.183 |
| 1-4 lamps | 0.305 | | 0.308 | 0.177 |
| 1-1 start, firing every 0.25 s (recoil fills 26 % of the screen) | 0.867 | | | 0.550 |

The idle revolver costs 0.31 ms against the 0.25 ms of section 3. The rays take most of the difference (0.18 against
0.14 ms). This branch adds nothing to the rays beyond a `pow` and a `min` per player light. The likely causes are master's
light-sampling changes since then (RIS 16, unbiased direct light) and the revolver's screen area, but neither was measured
separately. While firing, the cost follows the screen area the recoil animation covers.

## 7. See-through gun (2026-11-21, `t3/viewmodel-and-mannequins`)

User, playtest of 2026-11-20: the revolver looked slightly see-through, in dim rooms while firing and in P-2 next to
Sisyphus without firing. Asked what it looked like: the background showing through the gun's body, on its top and
edges, in `pathtrace`.

**What was ruled out** (by reading the frame order and with A/B captures):
- Nothing of ours draws over the gun after it. The volumetric haze and the screen effects (bloom, heat haze) run on Main
  Camera at `AfterForwardAlpha` (camera depth -1); DLSS RR and the world composite are in Main Camera's own buffer; the
  HUD camera (depth 0) draws the gun afterwards, opaque, and this pass replaces its pixels after its opaques. Native
  float HDR (`HdrScenePreRender`) runs at the Virtual Camera (depth 1), after the gun, and the present pass in `Hdr.cpp`
  chooses between the float frame and the 8-bit frame of the same image (it never blends the gun with what is behind
  it): not involved, and `Hdr.cpp` / `Plugin.Hdr.cs` were not changed. `relight+vfx.enabled=0` and
  `relight+plugin._vmEnabled=0` in P-2 confirmed the gun's pixels are this pass's.
- The user's saved P-2 poses (corridor, red room) did not show it. In P-2's dark rooms the game draws its own gun
  pitch black (no light reaches layer 13 there), and the RT gun is lit and tinted by the room instead.

**Cause.** The gun's reflection. Weapons are classified MetalLow (roughness 0.4, weight 0.6) and metals get an f0 of at
least 0.5 luminance (`SpecularTerms`), so about 30-36 % of a gun pixel is its reflection. The reflection ray goes along the
mirror direction (`_vmStableRefl`), and the viewmodel is not in the RTAS, so nothing of the gun can block it. Where the
gun is seen at a grazing angle (the top of the barrel and frame seen from behind, every silhouette edge, more of it when
the recoil tilts the gun), the mirror direction is close to the view direction: the ray runs on past the gun and returns
the scene right behind it, nearly unblurred (the filter blurs reflections by a few pixels only). With a dark diffuse
(a dim room) or a bright object ahead (a wall lit by the muzzle flash, Sisyphus), that image of the background is what
the edges show, and the gun reads as see-through. The reflection debug view (`_vmDebug=4`) in 7-3 shows the flashlit
ground and grass on the gun.

**Fix** (`ViewmodelComposite.shader`, `_vmReflGraze`, default 0.5): the reflection is blended towards the gun's own
display lighting, the cosine-weighted average of its surroundings (what a fully rough lobe returns), with weight
`1 - smoothstep(0, 0.5, cos(n, v))`. Faces seen head-on keep the mirror reflection; grazing faces lose the background
image but keep a sheen of the room's average light, so the metal does not go dark at its edges. No extra rays; the
composite's cost is unchanged (one lerp).

**Measured** (RTX 5070 Ti, 2560x1440, alt slot, `vm-mq-diag3`, same launch): the change against
`plugin._vmReflGraze=0` lies on the gun's edges and top faces (difference image), mean over the gun's quadrant 4.8 / 5.8
luma levels in 7-3 (flashlight) / 1-1 (sun), 10 % / 15 % of its pixels by more than 8 levels. On the top edge of the
barrel the old reflection showed the flashlit ground (7-3) and the pale stone (1-1); with the fix those edges look like
reflections off, while the rest of the metal keeps its reflections. Sheet:
`bench/results/review/viewmodel-and-mannequins/gun_grazing_reflection_before_after.png` (game / old / fix / reflections
off) in the main checkout. The user's exact scenes (a dim room while firing, Sisyphus) were not reproduced on the bench:
check there.

**Open.** While firing in P-2's red room, two relight captures (old build) showed a hard, vertical light edge across the
arm and gun: one side lit by the muzzle flash, the other not, while the game lights the whole arm
(`gun_firing_light_edge_open.png`). The dark side is not the game's colour, so it is not the composite's screen
rectangle; most likely the flash light's cone or range at the eye. Not reproduced on demand (the shot animation is
picked at random), not investigated further.

`bench/suites/viewmodel-mannequins.json`: the 7-1 mannequin room turns (docs/scene-proxies.md, "Posed mannequins"),
P-2 revolver shots and 7-3 shots with `plugin._vmReflGraze=0` as the before.

## 8. The RayQuery bridge: not for the viewmodel (2026-11-21, `t3/portal-raster-skip-and-hitches`)

Wave 3 moved volumetrics and particle light onto native inline `RayQuery` compute (docs/performance.md, Wave 3 item 1),
which removed Unity's per-`DispatchRays` shader-table rebuild for those passes. The viewmodel pass still pays that
rebuild. Its render-thread time (exclusive `-Perf`, 2026-11-21, `bench/results/20261121-203500_*`) is 0.40-0.67 ms in
the 0-x, 1-4 and 6-2 poses, 0.76-0.97 ms at 8-3 b and 8-2's portal, and 0.92-1.01 ms in 8-3 combat. Of that,
`vm-rays` (the `DispatchRays`) is 0.31-0.90 ms, while its GPU time is 0.27-0.63 ms. It does not move as cleanly:

- Volumetrics and particle light only ask whether a segment is blocked. The viewmodel's reflection and bounce rays
  (`Viewmodel.raytrace`, `TraceReflection` and the cosine bounce) shade the surface they hit with its albedo, emission,
  alpha, normal and layer. Those values come from each scene material's `UltraRTHit` closest-hit shader (Unity's
  shader pass and its per-material properties, bound through the shader table). A native `cs_6_5` shader sees only
  the instance, primitive and barycentrics. It cannot run those hit shaders or read Unity's material bindings.
- Moving only the shadow rays to `RayQuery` leaves the `DispatchRays` for reflection and bounce, so the rebuild stays.
- The bridge would need its own copy of every material's surface data (a per-instance material table plus vertex
  attributes for the UVs and normals). That is a second material system kept in sync with the first, for under 1 ms of
  render-thread time in the heaviest scenes.

Not implemented. A cheaper option, if this time matters later, is to trace the viewmodel's rays inside PathTrace's own
dispatch (one shader-table build per frame instead of two). docs/performance.md, "One dispatch for the auxiliary
passes", explains why that is not simple either.
