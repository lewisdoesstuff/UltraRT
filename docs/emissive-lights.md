# Emissive surfaces as direct light sources

Emissive triangles are sampled by the same RIS / ReSTIR direct lighting as point, spot and directional lights. They cast soft shadows and light rooms, and the cost per pixel stays roughly constant.

Files:
- `src/UltraRT/EmissiveLights.cs`: the triangle list, radiance estimates, per-frame visibility, transforms, the alias table, GPU buffers and `Bind`.
- `src/UltraRT/SceneProxies.Emitters.cs`: read-only hooks into `SceneProxies` and `StaticGroups`. It enumerates the traced submeshes with their proxy material, space, visibility and the CPU copy of the combined mesh. It's a new partial-class file, so `SceneProxies.cs` and `StaticGroups.cs` are unchanged.
- `unity/.../UltraRTLights.hlsl`: emitter evaluation, the mixed candidate stream, and the new reservoir encoding.
- `unity/.../PathTrace.raytrace`: two lines, the bounce-emission weight and the hybrid additive term.
- `src/UltraRT/LightSampling.cs`: the M cap is now 4000 (12-bit M), plus an optional `emissiveSetChanged` argument to `Bind`.

## Why

Before this change, emission only reached other surfaces when the single cosine-weighted bounce ray happened to hit an emitter. Indoor levels such as 4-1 are lit mostly by emissive strips, signs and screens, so the RT lighting barely showed there. Small emitters were also very noisy.

## Triangle list (CPU, at scene build)

`EmissiveLights.Build(proxies)` collects every traced submesh whose proxy material is emissive. That means `_EmissiveIntensity > 0` and `luminance(_EmissiveColor) > 0`, which covers both the ULTRAKILL/Master `EMISSIVE` keyword and Standard `_EMISSION`, since `SceneProxies.Proxy` maps both onto those properties. Three kinds of material are excluded:
- **Sky walls**: they report a miss and never block light.
- **Atlas-mode materials** (StaticSceneOptimizer's Stationary geometry): an average over a whole level atlas would make all level geometry glow. In practice these materials are not emissive.
- **Skinned renderers** (TODO): their vertices only exist skinned, on the GPU. They still appear through bounce hits if `bounceHitEmission` is on.

Geometry sources:

| Source | Vertices | Space (owner) | Visibility |
|---|---|---|---|
| Static batch, merge mode | `StaticGroups`' CPU copy of the combined mesh (no new readback) | `staticBatchRootTransform`, or world | `slot.VisibleSources > 0` (room activation) |
| Static batch, flags mode | GPU readback of the combined mesh, cached | same | same |
| Dynamic MeshRenderer | `mesh.vertices` if readable, else GPU readback (`GetVertexBuffer` / `GetIndexBuffer`), cached per `Mesh` | the proxy's transform | `DynamicProxy.Visible` (in the RTAS) |

The vertex decode handles any stream, stride and offset, and Float32/Float16/UNorm8/SNorm8/UNorm16/SNorm16. 16-bit index buffers are read the same way `StaticGroups.TryReadCombined` reads them.

Each triangle is stored in its **owner's local space**, 48 bytes (3 × uint4):

| | xyz | w |
|---|---|---|
| [0] | p0 | owner index \| flags << 16 (bit 0 two-sided, bit 1 counter-clockwise front faces) |
| [1] | p1 − p0 | half(Le.r) \| half(Le.g) << 16 |
| [2] | p2 − p0 | half(Le.b) |

Owners are a separate buffer, 3 rows of `localToWorld` each. They are re-uploaded only on frames when a matrix changed. The shader transforms the triangle at evaluation time, so **moving dynamic emitters are exact every frame**, in sync with the RTAS (`SceneProxies.SyncDynamic` runs first). The front normal is `cross(e1, e2)`, because Unity winds front faces clockwise in its left-handed space. It is flipped for mirroring transforms (negative determinant) and for `-ultrart-ccw`. Two-sided materials emit from both sides.

**Cap**: 65,535 triangles, because indices are 16 bits in the alias table and the reservoir. Triangles darker than `minRadiance` (0.02) are dropped first, which leaves out the dark parts of emissive textures. If the list is still over the cap, the brightest by luminance × area are kept. The list is **append-only**: emitters registered later (rescans, spawned hierarchies) are appended, and their GPU readbacks are limited to `readbacksPerFrame` (2) per frame. When more than half the list (and over 4,096 triangles) belongs to destroyed objects, it is compacted. That bumps a generation counter, and the shader drops emissive history for one frame (`_EmissiveHistoryValid = 0`).

### Radiance estimate

For each triangle: `Le = _EmissiveColor × _EmissiveIntensity × tint × T`. This is what the proxy hit shader computes, averaged over the triangle:
- the texture is `_MainTex` with `_UseAlbedoAsEmissive` (tint = `_Color`, `_MainTex_ST` applied), otherwise `_EmissiveTex` (white if unset);
- `T` comes from a **32×32 downsample** of that texture: one `Graphics.Blit` into a float render texture plus one `ReadPixels`, cached per texture. Blit picks the matching mip when the texture has mips (a box-filtered average). Otherwise it point-samples a 32×32 grid. This works for non-readable textures, and ReadPixels doesn't care about the texture's format;
- each triangle averages 8 lookups in the 32×32 copy: its 3 vertices, 3 edge midpoints, and the centroid weighted twice. UVs wrap. A triangle whose UVs span the whole texture or more (tiling) gets the texture's average instead.

## Sampling

**Split, not a combined table.** Each RIS candidate is an emitter sample with probability `share` (default 0.5), or a light from the existing light alias table otherwise. If a frame has only one kind, all candidates go to that kind. A single combined table would need the game's light units and emitted radiance on one scale. ULTRAKILL's `1/(1+25d²/r²)` model has no physical relation to radiance, so a fixed split is easier to reason about and to tune.

**Emitter source pdf.** A Vose alias table over the active triangles, with weight `luminance(Le) × area_world × R²/(R² + d_cam²)` (R = `importanceRadius`, 8 m). It is mixed with 20% of a pure `luminance × area` pdf so distant emitters are never starved. The point on the triangle is uniform. The table is rebuilt when emitter visibility changes, and when the camera has moved 2 m (at most every 8 frames). Between rebuilds it stays exact, because the pdfs it stores are the ones used.

**Target and weights.** A candidate's contribution, per unit emitter area, is

`c = Le × EmissiveLightScale × cosθ_surface × cosθ_emitter / (π × max(d², minDistance²))`

and `p̂ = luminance(c)`. The source pdf of an emitter sample in area measure is `share × pdf(tri) / area`, and that of a light is `(1 − share) × pdf(light)`. The RIS domain is the disjoint union of the light indices (counting measure) and the emitter surfaces (area measure), so each sample is simply weighted against its own pdf: `w = p̂ × area / (share × pdf)` for an emitter, `w = p̂ / ((1 − share) × pdf)` for a light. The final estimate is `c(y) × V(y) × W`, as before.

`EmissiveLightScale` goes into `c`, so it also goes into `p̂`. That keeps resampling between lights and emitters in one unit.

**Visibility.** The existing single shadow ray, with geometry mask `0x01` and back-face culling. For emitter samples the ray stops at `0.999·d − 0.02` instead of `d − 0.05`, so the emitter can't shadow itself. The sample point lies exactly on the traced triangle, since the same transform is in the RTAS. Receivers on the emitter's own plane get cos ≈ 0 and are rejected (`cos ≤ 1e-4`).

**Bounce.** `DirectOne` uses the same mixed candidates. When emitters are sampled directly, `_EmissiveBounceWeight` is set to 0, so emission seen by a bounce ray is not counted a second time. There is no MIS: RIS doesn't provide the light pdf of an arbitrary hit point. The cost is that emitters outside the list (skinned, over the cap, failed readback) lose their bounce contribution. `bounceHitEmission = true` restores it, at the price of double counting the listed ones.

## Reservoir layout (still 16 bytes)

The reservoir has to identify the emitter sample well enough to re-evaluate it next frame: the triangle (16 bits), a type flag, and the point. The point is stored as two 10-bit stratum indices. `EmissiveBary` maps them to the stratum centre and folds it into the triangle, so the sample is uniform over the area and reproducible exactly. The bits come from fields that had more precision than they needed:

| Field | Before | Now |
|---|---|---|
| x | light (16) \| half(M) (16) | light code (17: index \| `0x10000` emissive; `0xFFFF` none) \| uint(M) << 20 (12 bits, M ≤ 4000) |
| y | asuint(W) | asuint(W) with the low 10 mantissa bits replaced by bary u (W keeps 13 mantissa bits: relative error < 1.3e-4) |
| z | oct normal 2 × 16 | oct normal 2 × 11 \| bary v << 22 |
| w | plane | plane, unchanged (still against the quantised normal, so the plane test stays exact) |

An 11-bit octahedral normal has about 0.1° steps, far below the 0.9-cosine (about 25°) reuse tolerance. M was a half of at most `historyLimit × candidates` (160 by default), so an integer up to 4095 loses nothing. The history cap is clamped to 4000 in `LightSampling.Bind`.

**History of emitter samples.** Triangle indices are stable between compactions, so no remap is needed. A history sample whose triangle is no longer active is **dropped**, like a light that is gone. That happens when its room is hidden or its object destroyed, and is tracked by a per-triangle bit in `_EmissiveActive` (8 KB at the cap). It is also dropped on a generation change. A merged sample is re-evaluated at the current pixel with the stored point, including the current transform for moving emitters. When the active emitter set changes, `EmissiveLights.SetChanged` goes to `LightSampling.Bind(..., emissiveSetChanged)`, which shortens the history cap for that frame, like a light-set change.

**UAV budget unchanged.** PathTrace still binds exactly 8 UAVs. Everything this change adds is a read-only StructuredBuffer: `_EmissiveTris`, `_EmissiveOwners`, `_EmissiveAlias` and `_EmissiveActive`. Empty buffers bind small dummies.

## EmissiveLightScale (default 4)

At scale 1, `c` is physically consistent with the path tracer's own bounce, where a hit on an emitter contributes `Le` with a cosine pdf (`∫ Le cos dω / π`). ULTRAKILL's lights are much brighter than that convention. A typical point light (intensity 1–2, range 10–20) gives `c ≈ I / (1 + 25d²/r²) ≈ 0.5–1` on a wall 2 m away. A typical light strip (Le ≈ 1–2, 0.25 m × 2 m = 0.5 m²) gives `Le·A/(π d²) ≈ 0.06` at the same distance, more than 10× dimmer. Large emitters (lava in 0-5, big screens) have 10–100× the area, though, and would blow out at ×10. The default of 4 is a compromise: a strip lights a nearby wall at a third to a half of a point light's brightness, and lava stays in range. **This is an estimate, not a measurement.** Calibrate it in 4-1 with `pt-direct` and the build log line, which prints the median and maximum Le luminance and the total emitter area.

## Hybrid composite

The hybrid's shadow ratio is shadowed / unshadowed of our own direct light (`g_DirectUnshadowed`). Emitter samples are in both sums the same way: unshadowed `c·W`, and shadowed only if the ray is clear. Emitters therefore add shadows in hybrid mode, through the same denoised ratio.

The game doesn't light surfaces from emissives, so there is also an **additive term**. PathTrace mode 3 adds `g_DirectEmissive × EmissiveHybridAdd / GIStrength` (the shadowed emissive direct light) to the bounce channel. The composite then multiplies that channel by `GIStrength × albedo × lit`, which gives `EmissiveHybridAdd × albedo × emissive light × lit` on top of the game's colour. That is the same form as the GI term, and it is denoised by the existing bounce denoiser, with no new texture and no new UAV. Default `EmissiveHybridAdd` = 0.25 (modest). It is disabled automatically when `GIStrength` is 0, because the channel is then multiplied by 0.

## Reference mode

Brute force over every emissive triangle is not feasible. In Reference mode, each primary hit takes `referenceSamples` (4) emitter samples from the same alias table, with one shadow ray each. The bounce takes at most 1. This is unbiased but noisy, and fine for `-ultrart-accumulate` ground truth. Set `referenceSamples = 0` to skip emitters there; bounce hits then count emission again.

## Costs (estimated, not measured)

- **CPU, scene build**: one synchronous GPU readback per distinct emissive dynamic mesh (about 0.3–2 ms each). One Blit + ReadPixels per distinct emissive texture (about 0.5–1 ms each). About 1 µs per triangle. For a level with a few dozen emissive materials and meshes, about 50–100 ms on top of the existing build. Logged as `Emissive lights: ... took N ms`.
- **CPU, per frame**: a visibility poll per emitter submesh (a delegate call, about 20 ns), a matrix compare per owner, and a 4–48 KB upload only on frames where something changed. The alias rebuild is O(active triangles), about 0.1 ms for 5k and about 1–1.5 ms at the 65k cap. It runs only on visibility changes or after 2 m of camera movement (at most every 8 frames).
- **GPU memory**: 48 B per triangle (3 MB at the cap), 16 B per active triangle for the alias table, 8 KB of mask, and 48 B per owner. CPU: about 100 B per triangle, 16 KB per texture, and cached readbacks of dynamic meshes (positions and UVs streams plus indices).
- **GPU time**: no extra rays in RIS or ReSTIR. An emitter candidate costs 3 uint4 + 3 float4 reads and about 60 FLOPs, close to a light candidate (up to 4 float4 reads). `share` moves half the candidates to emitters, so point lights get about M/2 candidates when both kinds exist. Raise `candidates` (8 → 12) if point-light noise goes up in mixed scenes. Reference mode adds `referenceSamples` shadow rays per primary hit and 1 per bounce hit.

## Risks and limitations

- **EmissiveLightScale and EmissiveHybridAdd were untested in the game when this was written.** See above. ("Emissive brightness" is in the settings menu since: [settings.md](settings.md).)
- **Statue orbs** are emitters too: orb fixtures get `EmitterMask`, and the orb's light lights its holders at x0.5 ([scene-proxies.md](scene-proxies.md), "Statue orbs"; [lighting-overrides.md](lighting-overrides.md)).
- **Large emitters near receivers** (lava floors, wall-sized screens) are noisy, because uniform area sampling of a big triangle gives high variance close to it. The `minDistance` clamp (0.25 m) removes the worst fireflies at the cost of a little bias. Solid-angle sampling of the triangle would be the real fix. Lava emitters are split into pieces of at most 1 m2 (docs/lava.md).
- **Runtime material changes** (swaps, property blocks) are followed by the hit shader and re-registered here (docs/scene-proxies.md, "Runtime material changes"); emission changes from property blocks at most once a second per proxy (the hit shader follows them every frame).
- **Emission uv**: the game samples `_EmissiveTex` at the main texture's uv (`uv0 * _MainTex_ST`, checked in the Master shader's compiled code), so both the hit shader and the triangle radiance here use `_MainTex_ST`. Before 2026-11-14 they used `_EmissiveTex_ST` (never set, identity): materials sharing one texture with different `_MainTex` offsets (0-2's `FinalFrameL 2/3` light strips) glowed one band off, an extra row of lit strips on the wall while the real ones were lit orange.
- **No light layers**: lights respect culling masks, but emitters light every layer.
- **Bounce emission is dropped for emitters outside the list** (skinned, over the cap, failed readback). See Bounce above.
- **Readbacks during gameplay** for late emitters stall the GPU briefly, with at most 2 per frame.
- **Static-group partial rebuilds are deferred** (`MaxPartialBuildsPerFrame`). For a frame or two, an emitter can be counted visible from `VisibleSources` before its partial mesh is in the RTAS.
- **Reservoir precision changes**: W truncation (< 1.3e-4 relative), 11-bit normals, and integer M. All are far below the tolerances in use.

## Settings (`EmissiveLightSettings`)

`enabled` (true), `lightScale` (4, EmissiveLightScale), `hybridAdd` (0.25, EmissiveHybridAdd), `share` (0.5), `referenceSamples` (4), `maxTriangles` (65535), `minRadiance` (0.02), `minDistance` (0.25 m), `importance` (true), `importanceRadius` (8 m), `uniformMix` (0.2), `rebuildDistance` (2 m), `bounceHitEmission` (false), `readbacksPerFrame` (2). All of them are read every frame. If `enabled` is switched on after a scene was built with it off, the next `Update` builds the list.

## Integration (Plugin.cs)

`EmissiveLights.Bind` must run on **every PathTrace dispatch, in every light sampling mode**, or Unity reports unset properties (the `_Emissive*` buffers and ints). It must run after `LightSampling.Bind`.

1. Fields, next to `_lightSampling`:
   ```csharp
   private EmissiveLights _emissive;
   private readonly EmissiveLightSettings _emissiveSettings = new EmissiveLightSettings();
   ```
2. `Awake()`, right after `_lightSampling = new LightSampling(MaxLights) { Settings = _lightSamplingSettings };`:
   ```csharp
   _emissive = new EmissiveLights(Logger) { Settings = _emissiveSettings };
   ```
3. `ResetScene()`, after `_lightSampling?.Reset();` (this also covers shader reloads):
   ```csharp
   _emissive?.Clear();
   ```
4. `BuildWhenLoaded()`, right after `_proxies.Build();`. `Build` catches and logs its own exceptions, so a failure leaves an empty list and keeps the scene:
   ```csharp
   _emissive.Build(_proxies);
   ```
5. `Trace()`, right after `_proxies.SyncDynamic();`, inside the CPU stopwatch:
   ```csharp
   _emissive.Update(_proxies, _camera.transform.position);
   ```
6. `Trace()`, inside `if (pt) { ... }`. Replace the `_lightSampling.Bind(...)` line with these two lines:
   ```csharp
   _lightSampling.Bind(_cmd, shader, w, h, viewProj, _camera.transform.position, _packedLights, _lightData, _lightCount, _emissive.SetChanged);
   _emissive.Bind(_cmd, shader, _lightSamplingSettings.mode);
   ```
   (Historical wiring recipe. Today `Plugin.cs` passes `_lightSampling.ActiveMode` to `_emissive.Bind`, because hybrid has its own mode, and `kind == Kind.Hybrid` to `_lightSampling.Bind`.)
7. Optional: arguments in `ParseArgs()`:
   ```csharp
   case "-ultrart-noemissive": _emissiveSettings.enabled = false; break;
   case "-ultrart-emissive-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.lightScale); break;
   case "-ultrart-emissive-hybrid": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.hybridAdd); break;
   ```
   Also a hotkey in `OnUpdate()` (F5 was free then; plain F5 now toggles emissive lights, as built here) for A/B tests:
   ```csharp
   if (Input.GetKeyDown(KeyCode.F5)) { _emissiveSettings.enabled = !_emissiveSettings.enabled; _lightSampling?.Reset(); _denoiser?.Reset(); _denoiserDirect?.Reset(); Logger.LogInfo($"Emissive lights: {(_emissiveSettings.enabled ? "on" : "off")}"); }
   ```
   Also add `emissive={_emissive.ActiveCount}/{_emissive.TriangleCount}` to the "First trace OK" log line and the HUD.
8. Teardown, if the plugin ever disposes its buffers: `_emissive.Dispose();`.

`-ultrart-ccw` needs no extra wiring: the triangle flags read `SceneProxies.FrontCounterClockwise` directly.

# Effect lights (spawned lights, virtual lights, flame flicker)

Files:
- `src/UltraRT/EffectLights.cs`: everything below.
- `EffectSpawns.cs`: the Instantiate hook.
- `Plugin.Effects.cs`: wiring and flags. Plugin.cs only calls `UpdateEffects()` from `OnUpdate` and `AppendEffectLights()` at the end of `GatherLights`.
- `EffectSurvey.cs`: the diagnostic survey.
- `Bench/EffectSpawner.cs`: bench effects.
- Shader side: `_Lights[i*4+3].w` and `g_DirectAdditive` in `UltraRTLights.hlsl`, plus one line in `PathTrace.raytrace`.
- `LightSampling.VirtualIds` gives light-less entries a stable id.

## Survey (2026-11-14)

Run with `-ultrart-effectsurvey` over the effects suite: 1-1 torch hall, the 0-2 run with live enemies and revolver fire, 0-5, 4-1 and 8-3.

**Almost every combat effect already ships a Light** (point lights; intensity / range):

| Effect | Intensity | Range | Notes |
|---|---|---|---|
| Explosion, Explosion Big | 60 | 10 | the light grows and fades in `Explosion.Update` |
| Revolver Beam | 20 | 5 | fades at 100 per second (`RevolverBeam.Update`) |
| Enemy muzzle Flash | 20 | 3 | |
| Projectile (enemy orbs) | 5 | 10 | |
| LaserHitParticle | 10 | 10 | |
| Fire (`FireObjectPool`) | 5 | 10 | |
| Player: PunchZone / ChargeEffect / railcannon FullCharge / nailgun barrels | 20 / 20 / 50 / 10 | 10 / 5 / 2 / 5 | |
| CheckpointActivate | 10 | 25 | |

**The problem was timing, not missing lights.** `GatherLights` scans `FindObjectsOfType<Light>` once a second. Of the spawned lights that switched on during the survey:

| Lifetime | Lights |
|---|---|
| under 0.1 s | 175 |
| 0.1 to 0.25 s | 223 |
| 0.25 to 0.5 s | 15 |
| 0.5 to 1 s | 61 |
| 1 to 2 s | 9 |
| 2 s or more | 7 |

About 95% died within a second, so most were gone before a scan came round. Lights that a scan did catch appeared up to a second late. As a result, muzzle flashes, beam lights and most explosions practically never lit anything in RT.

**Fires are pooled.** `FireObjectPool` instantiates 100 `Fire` objects (with a light) and 100 `FireSimple` objects (flame sprite only) at level load. After that it only calls `SetActive` on them, and `Flammable.Burn` enables the Fire's Light. Nothing is instantiated when something catches fire.

**Game lights don't flicker.** During the whole survey, only one light changed intensity: 1-1's FerryLamp (`Flicker`). Torch "Fire" lights are constant in the game. What flickers is the flame sprite.

**Glowing effects without a light are few.** Additive renderers with no light:
- FireSimple flame sprites and decorative torch flames;
- `FlameStreetcleaner` particles (4-1, 8-3) and sparks;
- additive enemy motion trails (`DiveTrail`, material "Additive"): 66 in 1-1, 102 in 0-2;
- LightPillar light-shaft meshes and lava overlays;
- dust and smoke puffs drawn additively (`Smoke` on ULTRAKILL/Master: `DustBig`, `ImpactParticle`, `GasParticle`), and the checkpoint hologram quad.

## 1. Spawned and pooled lights

This is the change that matters most.

`EffectSpawns` adds a postfix to the non-generic `Object.Instantiate` overloads. Every generic overload that takes a position or a parent calls one of them. The single-argument generic overload goes straight to native code and is not seen. The postfix only appends the clone to a list.

`EffectLights.ProcessSpawns` drains that list twice per frame: in `OnUpdate`, and again at the start of `AppendEffectLights`. Clones made by coroutines or late updates are therefore packed in the same frame's trace.

Every Light in a clone is **tracked until it is destroyed**, so pooled fires are found when the pool reactivates them. Each frame, a tracked light that is on but not in the current scan snapshot is packed exactly as `GatherLights` packs point and spot lights:
- Lights that were on last frame are checked every frame. Lights that were off are checked every 4th frame, so a pooled fire is seen within 4 frames of switching on.
- When the 1 s scan replaces the snapshot array, tracked lights in it are flagged and left to `GatherLights`, so nothing is packed twice.
- Intensity, colour, range and position are read every frame, so the lights follow the explosion's growth and fade and the beam's fade.

ReSTIR keeps working across the handover. Its light remap is by `Light.GetInstanceID()`, so a light moving from the appended section into the scanned section keeps its reservoirs. A new light sets `LightSetChanged`, which caps history M at `candidates` for that frame (`shortenHistoryOnLightChange`). A muzzle flash therefore shows on its first frame, not after up to 20 frames of history.

## 2. Virtual lights

**Which emitters.** Scene renderers are collected once per RT scene build, with one `FindObjectsOfType<Renderer>(true)` (9 to 36 ms). Renderers of spawned clones are added as they spawn. An emitter qualifies when:
- it is a **sprite or a particle system**;
- its material blends additively (`_DstBlend == One`, or "Additive" in the shader name) in the transparent queue;
- neither its material nor its texture name contains `smoke dust gas fog cloud mist steam checkpoint water splash glass shockwave hole lava pillar`.

Trails, lines and meshes are off by default (`virtualTrailsAndMeshes`). In the surveyed levels they were enemy motion streaks, light shafts and lava overlays, and the beams that matter already have lights. Renderers in a spawned hierarchy that has its own Light never become virtual lights; they only drive flicker.

**Colour.** The product of:
- 2 x `_TintColor` (or `_Color`);
- the texture average: rgb x alpha, from a 16x16 Blit + ReadPixels, cached per texture;
- the vertex colour: the particle start colour, or the sprite colour;
- the current sprite's own rectangle average, and the AnimatedTexture frame from the property block.

The light's colour is that product, normalised so its brightest channel is 1.

**Intensity.** `intensityScale` (4) x strength. For comparison, the game's Fire light is 5. Strength = clamp(brightest channel x 4, 0, 1) x fill, where fill = 1 for sprites and 1 - e^(-count/4) for particles. Brightness rises at once and decays over a few frames, because particle counts jump.

**Shape.** Range = 4 x the bounds radius, clamped to 3 to 12 m (the game's Fire: 10 m). The light sits at the bounds centre. Its soft-shadow radius is 0.4 x the bounds radius, clamped to 0.05 to 0.5 m.

**Selection**, every 4th frame. Candidates are emitters that:
- are within 45 m;
- have a strength of at least 0.1;
- have no game light sitting on them. A light counts if it is the nearest packed light and within 1 m + 0.25 x the effect's radius; this is re-checked every 60 frames.

Candidates are scored by strength x size / (1 + d²/25), with a 1.5x bonus for last frame's picks. The best 16 are packed after the scene and spawned lights, with `packed[i] = null` and `ids[i] = renderer.GetInstanceID()` (through `LightSampling.VirtualIds`). Inactive emitters are checked only every 16th frame.

**Hybrid.** `_Lights[i*4+3].w`, unused before, is now the hybrid weight: 0 for game lights and `hybridWeight` (1) for virtual ones. When ReSTIR's chosen visible sample is a weighted light, `g_DirectAdditive` gets its shadowed contribution x weight. PathTrace mode 3 adds that to the bounce channel exactly like `g_DirectEmissive`: the game draws no light for these effects, so hybrid adds it on top of the game's image. Like emitters, virtual lights also count in the shadow ratio.

## 3. Flame flicker

When a game light sits on a flame sprite or particle system (a torch FireSprite on its Fire light, or a pooled Fire), the flame drives that light. Its packed intensity is multiplied by (the flame's brightness x radius²) / its running mean over about 1 s, clamped to 0.65 to 1.35. At most 16 such flames near the camera are measured, every frame.

### Viewmodel effects
Emitters on the viewmodel layer (13) never become virtual lights: a held item's flame (4-3's torch) is drawn by the HUD
camera at the hand, and its own Light already lights the scene from the player (`Torch.Update`). The 4-3 torch flame
used to add a second light (I 1.0) at the hand.

The 5 s log line also names the flames driving flicker (`flicker now: Sprite 'FireSprite'/'Fire' r 0.42 -> light
'Fire'`). The 8-2 standing-lamp "flicker" (user report) was not this: no emitter sits on that light, and the burst is the
same with `fx.flicker=0` (bench variant target `fx`); see docs/relight.md, "Lamp shades".

## Costs

Measured on an RTX 5070 Ti at 1920x1080, trace scale 0.5, effects suite.

CPU time is the `Effect lights: CPU` log line, printed every 5 s; it covers spawn processing and `Append`.

| Where | CPU per frame |
|---|---|
| 0-2 run, combat with revolver fire every 0.5 s (0.4 to 3.5 spawned lights appended per frame) | 9 to 14 µs |
| 1-1 torch hall (8 flickering torches) | 13 to 25 µs |
| 4-1 indoor A | 20 µs |

Collecting scene emitters costs 7 to 27 ms once per RT scene build (8-3: 243 emitters, 27 ms), and the first texture averages add a few Blits. No per-frame allocations.

GPU (native timer) and frame times were measured in the effects suite, before (`-ultrart-noeffectlights`) and after, same build. Both are within run-to-run noise:

| Scenario | Mode | GPU total, before → after (ms) | Dispatch, before → after (ms) | Frame, before → after (ms) |
|---|---|---|---|---|
| 0-2 run | hybrid | 2.159 → 2.178 | 1.230 → 1.241 | 4.26 → 4.32 |
| 0-2 run | relight | 1.949 → 1.962 | | 4.16 → 4.26 |
| 8-3 portals | relight | 1.699 → 1.721 | | 9.08 → 8.99 |
| 0-5 lava | relight | 1.886 → 1.876 | | |

The extra lights add no rays: ReSTIR's per-pixel cost doesn't depend on the light count.

Screenshots: `bench/results/20261114-064629_effects` (before) and `20261114-065016_effects` (after). Compare `1-1_explosion_relight_1.5s_relight.png`: before, the room is unlit by the explosion; after, it is lit orange on the frame the explosion spawns. Also `1-1_flame_without_light_1.5s_*` (orange light on the stairs from a FireSimple flame) and `0-2_run_*`. Torch flicker measured over the 1-1 torch hall: x0.77 to x1.29 (8-3: x0.77 to x1.26).

## Flags and settings (`EffectLightSettings`)

| Flag | Setting | Default | Effect |
|---|---|---|---|
| `-ultrart-noeffectlights` | | | turns all of the above off |
| `-ultrart-nospawnedlights` | `spawnedLights` | on | |
| `-ultrart-novirtuallights` | `virtualLights` | on | |
| `-ultrart-noflicker` | `flicker` | on | |
| `-ultrart-effect-scale x` | `intensityScale` | 4 | |
| `-ultrart-effect-max n` | `maxVirtual` | 16 | |
| `-ultrart-effect-hybrid x` | `hybridWeight` | 1 | |
| `-ultrart-effectsurvey` | | off | the survey; it costs milliseconds per frame, so never measure with it on |

Other fields: `cullDistance` 45, `rangePerSize` 4, `minRange` 3, `maxRange` 12, `minStrength` 0.1, `coverDistance` 1, `flickerRange` 0.35, `virtualTrailsAndMeshes` false.

In the bench, `Scenario.Effects` (`bench/README.md`, `bench/suites/effects.json`) spawns revolver beams, explosions, projectiles or pooled fires in front of the camera, both periodically and right before each screenshot.

## Limits

- **MaxLights is still 256.** Effect lights are packed after the scene lights, so they are the first dropped when the list is full. 8-3 packs about 150 scene lights at the surveyed pose.
- **Light-set churn.** Every effect light that appears or expires shortens ReSTIR history for one frame. Heavy combat trades a little direct-light noise for no lag.
- **`Instantiate<T>(T)` without a position isn't hooked.** Lights in those clones still wait for the 1 s scan, as before.
- **The heuristics go by name and blend mode.** A glowing effect whose name matches the exclusion list, or one that is alpha-blended, gets no virtual light. `virtualTrailsAndMeshes` turns the other renderer kinds on.
- **Virtual light brightness is an estimate.** Every effect at full strength gets the same intensity, scaled by how much of its quad glows. It is calibrated only against the game's own Fire light.
