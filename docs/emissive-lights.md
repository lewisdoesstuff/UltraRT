# Emissive surfaces as direct light sources

Emissive triangles are sampled by the same RIS direct lighting (ReSTIR too until it was removed on 2026-11-19) as point, spot and directional lights. They cast soft shadows and light rooms, and the cost per pixel stays roughly constant.

Files:
- `src/UltraRT/EmissiveLights.cs`: the triangle list, radiance estimates, per-frame visibility, transforms, the alias table, GPU buffers and `Bind`.
- `src/UltraRT/SceneProxies.Emitters.cs`: read-only hooks into `SceneProxies` and `StaticGroups`. It enumerates the traced submeshes with their proxy material, space, visibility and the CPU copy of the combined mesh. It's a new partial-class file, so `SceneProxies.cs` and `StaticGroups.cs` are unchanged.
- `unity/.../UltraRTLights.hlsl`: emitter evaluation, the mixed candidate stream, and the new reservoir encoding.
- `unity/.../PathTrace.raytrace`: two lines, the bounce-emission weight and the hybrid additive term (gone with hybrid on 2026-11-19).
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

## Hybrid composite (removed 2026-11-19)

History: the hybrid mode, its additive term and `EmissiveHybridAdd` (`hybridAdd`, `-ultrart-emissive-hybrid`) went on 2026-11-19.

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

- **EmissiveLightScale (and EmissiveHybridAdd, removed 2026-11-19) were untested in the game when this was written.** See above. ("Emissive brightness" is in the settings menu since: [settings.md](settings.md).)
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

`enabled` (true), `lightScale` (4, EmissiveLightScale), `share` (0.5), `referenceSamples` (4), `maxTriangles` (65535), `minRadiance` (0.02), `minDistance` (0.25 m), `importance` (true), `importanceRadius` (8 m), `uniformMix` (0.2), `rebuildDistance` (2 m), `bounceHitEmission` (false), `readbacksPerFrame` (2). All of them are read every frame. If `enabled` is switched on after a scene was built with it off, the next `Update` builds the list.

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
   (Historical wiring recipe. Today `Plugin.cs` passes `_lightSampling.ActiveMode` to `_emissive.Bind` (pathtrace under RR overrides the mode); the hybrid argument went with the mode on 2026-11-19.)
7. Optional: arguments in `ParseArgs()`:
   ```csharp
   case "-ultrart-noemissive": _emissiveSettings.enabled = false; break;
   case "-ultrart-emissive-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _emissiveSettings.lightScale); break;
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

Trails, lines and meshes are off by default (`virtualTrailsAndMeshes`). In the surveyed levels they were enemy motion streaks, light shafts and lava overlays, and the beams that matter already have lights (at their start only; since 2026-11-20 combat beams get a chain of lights along their line, see "Combat lights" below). Renderers in a spawned hierarchy that has its own Light never become virtual lights; they only drive flicker.

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

**Hybrid** (removed 2026-11-19; `_Lights[i*4+3].w` is 0 again, `hybridWeight` and `-ultrart-effect-hybrid` are gone). `_Lights[i*4+3].w`, unused before, was the hybrid weight: 0 for game lights and `hybridWeight` (1) for virtual ones. When ReSTIR's chosen visible sample is a weighted light, `g_DirectAdditive` gets its shadowed contribution x weight. PathTrace mode 3 adds that to the bounce channel exactly like `g_DirectEmissive`: the game draws no light for these effects, so hybrid adds it on top of the game's image. Like emitters, virtual lights also count in the shadow ratio.

### Player-made fire (2026-11-21, `t3/stained-glass-and-combat-polish`)

User (playtest 2026-11-20, 4-4's dark corridor): "i made a fire and it didn't light up anything around it?". The
Firestarter's gasoline burns as `BurningVoxel`s: each takes a **`FireSimple`** from `FireObjectPool`
(`GetFire(isSimple: true)`: a `FireSprite` with `FireMaterial` and **no Light**), parents it under the stain's
`VoxelProxy` and scales it x4. Its flames were collected as emitters, but their sprite bounds are 15-17 m (x4 under the
voxel's own scale), and `Measure` dropped anything over 12 m (2 x `MaxRadius`, meant for sky-sized sprites): no candidate
at all. Now:

- **Flames** (`IsFlame`: fire / flame / torch / burn in the name) up to 40 m of bounds extent (`MaxFlameExtent`) keep
  their light; an oversized one is placed low in the flame (1.5 m over its bounds' bottom at most), where it burns on
  the floor, not at the billboard's middle. Radius stays capped at 6 m, so range 12 m.
- **`flameVirtualGain`** (6): a flame *sprite* with no light of its own gets x6 the virtual light of other glows. The
  FireSimple sprite averages ~0.06 (intensity ~1); the same flame's lit variant (`Fire`) carries a light of 5. At 4
  pathtrace barely lit the corridor (luma +0.4, `bench/results/20261121-011415_tmp-sgc-i_alt`), at 8 the corridor went
  near white (36 -> 88); relight at 4 was already warm on the walls (38 -> 50 at 5.5 s). 6 (x2.4 the light of 4) is
  between; not measured on its own.
- Cost: nothing without fire; with a big fire, at most `maxVirtual` (16) lights as before (3-4 were picked in the test).
- Bench kinds `gasoline` (24 of the game's `GasolineProjectile`s dropped on the floor ahead) and `gasignite`
  (`StainVoxelManager.IgniteAll`), bench/README.md. The 5 s effect-light log line now also counts why candidates near
  the camera were not picked (`candidates measured N (covered, own light, weak, kind)`, `near but nothing drawn N: <paths>`)
  and names the covering light's path, intensity, range and distance.

Sheet `4-4_gasoline_fire.jpg` in `C:\Users\death\.t3\projects\ultrart\bench\results\review\stained-glass-and-combat-polish\`.

### Flames with their own light (2026-11-21, `t3/playtest-visual-fixes`)

User, playtest of 2026-11-21, 1-1 pose 1 (`bench/recordings/u1121-1-1-1.json`): behind both wall torches the shadow on
the orange wall was a bright saturated crimson instead of a dark warm shadow, in `pathtrace` and `relight`.

**Cause.** Every 1-1 torch flame (`BigTorch/Fire/LookAtCamera/FireSprite`, `FireMaterial`) was also a virtual light.
The test that leaves a flame to its own game light ("covered", `FindCoveringLight`) goes by distance: the light must sit
within 1 m + a quarter of the sprite's radius of the sprite's bounds centre (1.6 m here). The sprite's centre is 1.7-2.3
m from the `Fire` light in the cup (log line `is on its own light ... m from the effect's centre`), so no torch was ever
covered (`candidates measured 721 (covered 0, ...)`) and each got a virtual light in the sprite's own average colour,
deep red. Since 3538105 (`flameVirtualGain` 6, "Player-made fire") that light is six times stronger. Where the torch's
own light was shadowed by its bowl, the red light, higher up at the sprite's centre, still reached the wall: with
`lc.class.fixture=0` the walls round every torch were plain red. (This also means flame-driven lights, "Flame-driven
lights" below, have not been driving 1-1's torches: they need a covered flame. The recent runs' logs show 0.0 flickering
lights per frame almost everywhere. Not changed here.)

**Fix** (`EffectLights.cs`): an emitter with a Light on itself or one of its 3 nearest parents (`FindOwnLight`: the torch's
`Fire`) gets no virtual light while that light is on (counted as `own light` in the log). Flames without a light (the
gasoline fire's `FireSimple`) keep theirs. `fx.flameOwnLight` (true; bench `fx.flameOwnLight=0` = before).

Measured (alt install, SDR, trace scale 0.5, run `20261121-153104_tmp-pvf-after`, before/after in one launch): the bowl's
shadow on the wall is a dark warm brown in `pathtrace` and `relight`, no red; frame mean 93.5 -> 88.7 (`pathtrace`), the
game 54.6; virtual lights per frame 7 -> 3.4 (the rest are other glows). A patch inside the shadow: (255, 123, 31) ->
(179, 118, 30) in `pathtrace`, (255, 142, 36) -> (205, 133, 32) in `relight`. Sheet:
`bench/results/review/playtest-visual-fixes/1-1_pose_1_torch_shadow.png` (the game, before, after `pathtrace`, after `relight`). Every torch,
brazier or lamp sprite parented under its own light is treated the same way.

## 3. Flame flicker

When a game light sits on a flame sprite or particle system (a torch FireSprite on its Fire light, or a pooled Fire), the flame drives that light. Its packed intensity is multiplied by (the flame's brightness x radius²) / its running mean over about 1 s, clamped to 0.65 to 1.35. At most 16 such flames near the camera are measured, every frame.

### Flame-driven lights (2026-11-18, `t3/emissive-all`, on by default)

No flame is an emitting surface: torches, candles, braziers and lanterns with a flame are a sprite or a particle system
next to one of the game's point lights (986 of the 3,011 fixture lights; docs/lighting-overrides.md, "Handover"). With
`flameDriven` the flame that already drives the flicker also places and shapes that light:

- the light moves to the flame's centre (smoothed over a few frames: particle bounds breathe) and becomes a **sphere
  area light** with radius `flameRadius` 0.45 x the flame's bounds radius, clamped to [0.05, `flameMaxRadius` 0.6] m;
- its colour takes `flameHue` 0.15 of the flame's own hue at the game light's luminance;
- its strength is `flameGain` 0.5 x the game light's (with its class policy): the game puts the light inside the
  torch's cup, which shadows most of the room from it; the flame above the rim does not;
- it flickers with the flame as before, over a wider range (`flameFlickerRange` 0.5 against `flickerRange` 0.35).

Only a plain point light is driven (not a spot, not a light a level file already made an area light), and only while
its flame is one of the `MaxFlickers` (48, was 16) flames tracked near the camera; otherwise the game light stays as it
is. Bench: `fx.flameDriven=0` gives the old lights.

Measured on the torch stops of 1-1, 1-2 and 4-3 (`bench/suites/flame-driven.json`, run `20261118-153442`, 12 views,
medians against the game's frame):

| | hi% (game 3.3) | p95 | chroma | contrast | dark delta |
|---|---|---|---|---|---|
| game lights (`fx.flameDriven=0`) | 7.2 | x1.20 | x1.47 | x0.73 | +5.8 |
| flame-driven | 5.2 | x1.08 | x1.32 | x0.72 | +5.7 |

4-3's brazier room: highlights 7.2 % -> 0.9 % (the game 1.0 %), p95 x1.65 -> x1.14. The first try (gain 1, hue 0.6)
turned the 1-1 and 1-2 torch rooms saturated orange-red; gain 0.35 read at the game's level (p95 x1.09 on that run's
views) and under the punch target. **The wing-shaped shadows of 1-1's torch cups are gone** (the light is above the
rim and as large as the flame; `bench/results/review/emissive-all/flames-1-1-wings.jpg`, first row). The 1-1 cup rooms
are more orange than with the old lights (yellow); the game's own frame is orange-brown there.

Since 2026-11-19 torches are x1.25 by class, not x2.5 (docs/lighting-overrides.md, "Torches x1.25"): with the flame
above the cup, the x2.5 burned 1-1's torch hall and 4-4's boss hall out in trailer v4.

Not covered: a flame farther away than the tracked 48 keeps the game's light; candle piles are handed over to their
texture (the handover rule), not driven.

### Billboards in the emissive table (the literal version, tested 2026-11-18: no; code removed 2026-11-19)

`fx.flameBillboards=1` (one-sided) or `2` (two-sided), off by default: per flame on a game light, a camera-facing
square of the flame's size joins the emissive triangle table with the light's output, and the light is off
(`Plugin.FlameQuads.cs`, removed 2026-11-19 with the `fx.flameBillboards` key; it is in git history before that). Run `20261118-153718_flame-quads` (1-1 stop 2, 4-3 stop 0; sheet `flame-quads.png`):

| | hi% (game 2.7) | p95 | chroma | contrast | dark delta |
|---|---|---|---|---|---|
| flame-driven | 5.5 | x1.37 | x1.65 | x0.86 | +19.5 |
| squares, one-sided | 3.7 | x1.22 | x1.88 | x0.69 | +35.3 |
| squares, two-sided | 7.6 | x1.45 | x1.94 | x0.79 | +35.0 |

It fails, on four counts:

1. **One-sided**: the square lights the camera's side only. The wall behind a 1-1 torch cup, which the flame should
   light most, is unlit (highlights 13 % -> 3.6 % in the cup view), and what is lit changes as the view turns.
2. **Two-sided** fixes the back but the light still turns with the camera: a wall edge-on to the square gets nothing.
3. **No reach**: an emitter has no range, so the flame lights rooms the game's light never reached (4-3: dark end
   +35 against +19.5, the corridor behind the brazier room turns red). The handover has the same property; for lamps
   it was calibrated per rule, for a torch every room would need it.
4. **Table churn**: the table is built once; a flame that starts being tracked later is not in it (1 square per
   build in both levels, with several torches in view) until the next rebuild, and a rebuild per new flame resets the
   light history. The owner matrices themselves are cheap (one 48-byte write per square and frame).

The switch stays for comparison; nothing ships on it.

### Viewmodel effects
Emitters on the viewmodel layer (13) never become virtual lights: a held item's flame (4-3's torch) is drawn by the HUD
camera at the hand, and its own Light already lights the scene from the player (`Torch.Update`). The 4-3 torch flame
used to add a second light (I 1.0) at the hand.

The 5 s log line also names the flames driving flicker (`flicker now: Sprite 'FireSprite'/'Fire' r 0.42 -> light
'Fire'`). The 8-2 standing-lamp "flicker" (user report) was not this: no emitter sits on that light, and the burst is the
same with `fx.flicker=0` (bench variant target `fx`); see docs/relight.md, "Lamp shades".

## Enemy glows (2026-11-18, `t3/emissive-all`)

The user: "glowing parts on enemies would also be great!" What the enemies have, from a survey of eight levels
(`-ultrart-enemy-survey` writes `enemies_<scene>.txt`: renderers, materials, lights per enemy type;
`bench/suites/enemy-survey.json`):

| Enemy | Drawn as | Glowing part | Now |
|---|---|---|---|
| filth, stray, schism, soldier | one skinned mesh, one unlit texture (`ENEMY`, no glow map), no light | none: flesh, bone and blood. A chroma mask finds only the blood | nothing to carry |
| drone | two plain meshes, unlit texture, no light | the magenta eye and visor stripes, painted | **emitter rule** `enemy: drone eye` (glow 2, chroma 0.7): its triangles are in the emissive table |
| Malicious Face | skinned, vertex-lit grey texture, no light | none at rest (its charge and beam are effects with their own lights) | nothing to carry |
| Swordsmachine | skinned body with a glow map, skinned sword, no light | the blade (painted); the "Tundra" body's glow map has no texel above the table's minimum, "Agony" has | **glow light** on the blade's bone (rule `enemy: Swordsmachine sword`, chroma 0.5); the body's by its glow map where it emits |
| cerberus | skinned, vertex-lit; the orb in its hand has one of the game's lights | the orb | the game's light kept (no double) |
| Mindflayer | skinned, unlit; two of the game's lights | hands | the game's lights kept |
| virtue, idol | meshes and skinned wings; one of the game's lights | the whole body | the game's light kept |
| streetcleaner, stalker, sentry, hideous mass | skinned; game lights at the flame / can / eye; sentry, stalker and mass also have glow maps | | the game's lights kept |
| Gabriel (6-2's intro, his fights) | skinned; swords under `_VertexColors` materials the meshes have no colours for (the game draws them near-black) | the swords' gold and blue blades | **emitter rule** `enemy: Gabriel's swords` (glow 5, chroma 0.2; 2026-11-20, docs/ultrakill-rendering.md "Gabriel's swords"); no glow light: Gabriel has the game's lights |

So of the eleven common enemies two had a glow that nothing carried (the drone's eye, the Swordsmachine's blade); four
have none; five already carry a light of the game's at the glowing part.

**How.** A skinned mesh cannot be in the emissive triangle table: its vertices only exist skinned, on the GPU
(`CollectDynamicSubmeshes` skips skinned proxies). `Plugin.EnemyGlow.cs` instead sums what a skinned enemy submesh
emits once per mesh and material in the bind pose (`EmissiveLights.Summarise`: the same extraction as the table's,
so glow maps and emitter rules both count): total output, centre and facing weighted by luminance x area, spread.
Each frame one **sphere light** per part follows the bone whose bind position is nearest to that centre, pushed out of
the mesh along the glow's facing by its radius (spread, clamped to 0.05 to 0.5 m), with the strength that gives the
glowing area's output (the handover's formula backwards, `eg.gain` 1, at most gamma 3). At most `eg.max` 12 lights, the
nearest within `eg.maxDistance` 45 m. An enemy with one of the game's lights enabled on it (not its spawn effect) gets
none (`eg.skipLit`). These are light proxies, not per-triangle sampling: one light per glowing submesh, so a body with
glows at both ends is lit from between them. `-ultrart-noenemyglow` / `eg.enabled=0` turns them off. Log: `Enemy glow:
<type> '<mesh>' / '<material>': output ..., spread ..., bone N` or `nothing emits`, once per mesh and material.

**Chroma masks.** Emitter rules (`_rules.json`, "emitters") gained `"chroma": c` next to `"threshold"`: only texels whose
chroma (max - min of rgb) passes `c` glow. A luminance threshold on the drone picked the highlights of its shell, not
the eye. In the proxy material it is `_EmissiveThreshold` above 1 (1 + c); bench `lc.glow.TEXT=G,1.7` is chroma 0.7.
The table reads masks from a 32 x 32 copy of the texture, so a small eye's output is approximate.

**Bench.** Effects kind `enemy:<EnemyType>` (e.g. `enemy:Drone`) puts a copy of one of the level's own enemies of that
type in front of the camera, facing it, once per level load; further kinds stand beside it. With the scenario's
`Enemies` "blind" (the default) they idle. A tour runs a scenario's `Effects` at its first view.
`bench/suites/enemy-glow.json`: 1-3 (Swordsmachine, drone, cerberus), 2-3 (soldier, stray, schism, filth, drone,
Mindflayer), 5-1 (virtue, drone, Malicious Face, sentry); sheet `bench/results/review/emissive-all/enemy-glow.png`
(the game, pathtrace without the rules and glow lights, pathtrace). Visible change: the drone's eye and its halo on
the shell. The blade's light is not visible in the 1-3 frame.

**Combat cost.** `bench/suites/enemy-glow-perf.json`, the recorded 0-2 combat run (filth, strays, Swordsmachines; so
blade lights only), pathtrace at 1440p, one pair on 2026-11-18 (`-Perf`, exclusive):

| | frame ms | GPU total ms | dispatch ms |
|---|---|---|---|
| `eg.enabled=0` | 8.45 | 5.59 | 2.85 |
| on | 8.58 | 6.06 | 3.26 |

+0.13 ms per frame (1.5 %). The GPU columns differ by more (+0.47 ms) than a few sphere lights should cost; it is one
pair, not a repeated measurement, so read it as "under half a millisecond". A level with drones adds their eye
triangles to the emissive table, not lights (not measured in combat: no recorded run has drones).

Not done: enemies outside the survey's eight levels (their materials with a glow map get a glow light automatically;
painted glows need a rule each); the 6,054 enemy lights of the census are untouched (most are spawn effects, off
except while an enemy spawns).

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
| `-ultrart-effectsurvey` | | off | the survey; it costs milliseconds per frame, so never measure with it on |
| `-ultrart-beam-lights x` | `beamLights` | 1 | combat beam lights, a strength (0 = off); "Combat lights" below |
| (bench `fx.`) | `beamLines`, `lineIntensity`, `lineRange`, `lineMaxLights`, `lineCull`, `lineMedium` | true, 3, 10, 40, 70, 0 | sustained beams as line lights (2026-11-21); "Laser beams" below |
| `-ultrart-parry-light x` | `parryLight` | 1 | the parry burst (0 = off) |
| `-ultrart-lightning-flash x` | `lightningFlash` | 1 | the lightning flash (0 = off) |
| | `gunFlash`, `gunFlashHold`, `gunFlashFade` | on, 0.02, 0.06 | gunshot lights' crisp envelope ("Gunshot flashes") |
| | `lightningInsideScenes`, `lightningInsideGain` | `Level P-2`, 3 | bolts inside the play space: unshadowed, x gain ("Lightning flash") |
| | `flameVirtualGain` | 6 | flame sprites without a light ("Player-made fire") |
| | `flameOwnLight` | true | no virtual light for an emitter whose own Light (on it or up to 3 parents above) is on ("Flames with their own light") |

Flames: `flameDriven` on, `flameRadius` 0.45, `flameMaxRadius` 0.6, `flameHue` 0.15, `flameGain` 0.5, `flameFlickerRange` 0.5. Other fields: `cullDistance` 45, `rangePerSize` 4, `minRange` 3, `maxRange` 12, `minStrength` 0.1, `coverDistance` 1, `flickerRange` 0.35, `virtualTrailsAndMeshes` false.

In the bench, `Scenario.Effects` (`bench/README.md`, `bench/suites/effects.json`) spawns revolver beams, explosions, projectiles or pooled fires in front of the camera, both periodically and right before each screenshot.

## Limits

- **MaxLights is still 256.** Effect lights are packed after the scene lights, so they are the first dropped when the list is full. 8-3 packs about 150 scene lights at the surveyed pose.
- **Light-set churn.** Every effect light that appears or expires shortens ReSTIR history for one frame. Heavy combat trades a little direct-light noise for no lag.
- **`Instantiate<T>(T)` without a position isn't hooked.** Lights in those clones still wait for the 1 s scan, as before.
- **The heuristics go by name and blend mode.** A glowing effect whose name matches the exclusion list, or one that is alpha-blended, gets no virtual light. `virtualTrailsAndMeshes` turns the other renderer kinds on.
- **Virtual light brightness is an estimate.** Every effect at full strength gets the same intensity, scaled by how much of its quad glows. It is calibrated only against the game's own Fire light.

# Combat lights (beams, parry, lightning; 2026-11-20, `t3/combat-storm-lights`)

Files: `src/UltraRT/CombatLights.cs` (the lights and the Harmony hooks, `CombatHooks`), wiring in `Plugin.Effects.cs`
(`AppendEffectLights`, after `EffectLights.Append`), settings in `EffectLightSettings` (bench prefix `fx.`), the hooks
installed from `Patches.Apply`, bench kinds in `Bench/EffectSpawner.cs`, suite `bench/suites/combat-lights.json`.

## What the game does (Assembly-CSharp, read 2026-11-20)

- **`RevolverBeam`** (revolver, coin shots, ricochets, the railcannon: `BeamType.Railgun`, Malicious Face, turrets and
  other enemy beams: `BeamType.Enemy`) is one GameObject with a 2-point world-space `LineRenderer` (start, hit point)
  and a `Light` on the same object (20, range 5 for the revolver), which sits at the beam's *start*. Its line shrinks
  at 1.5 width per second and the light fades at 100 per second (`Update`, `fadeOut`). A ricochet or a pierce clones the
  beam (`Instantiate(gameObject)`) at the hit point; a coin spawns `refBeam` copies (`Coin.SpawnBeam`) and sets their two
  points itself. So every bounce segment is its own beam object, and each one is instantiated.
- **`ContinuousBeam`** (the Mindflayer, beams tied to projectiles) is a sustained line whose width rises to
  `originalWidth` and falls to 0 when it turns off.
- **`TimeController.ParryFlash()`** (every parry: punches, projectiles, coins, Punch.Parry) instantiates `parryLight`
  under the player (a point light, 20 / range 10, colour (1, 1, 0.86)), shows the white screen flash for 0.1 s (if
  Options > parry flash is on) and calls `TrueStop(0.25)`: `Time.timeScale = 0` for 0.25 s of real time. It gets no
  world point.
- **`LightningStrikeDecorative_Animated`** (5-2: three bolts over the sea, `Sea/Lightning/LightningStrike*`):
  `FlashStart` starts a `buildupTime` (0.3 s in 5-2) build-up, then the strike sets its `flash` Light (a point light,
  25 / range 1000, white, about 160 m above the sea, 350 m from the pier) to full and fades it linearly over
  `strikeTime` (1 s), on game time. The bolt sprite's bounds run from y -66 (below the sea) to 255. The game's flash is
  already traced (the active-light set has every enabled light, whatever its intensity).
- **`LightningStrikeExplosive`** (the Ferryman's strikes) spawns an explosion prefab (with its own light, tracked as a
  spawned light) or, over a coin, a reflected `RevolverBeam` from 100 m up; then destroys itself in `Start`.

## 1. Beam lights (`fx.beamLights`)

Every spawned hierarchy (the `EffectSpawns` queue) is searched for `LineRenderer`s whose GameObject has a
`RevolverBeam` or a `ContinuousBeam`; nothing else counts, so LightPillar cones and other decorative lines are never
beams. Scene `ContinuousBeam`s (pre-placed Mindflayers) are collected once per scene. A `RevolverBeam` counts as ready
once its `Start` has run (`cc` set): before that its line still holds the prefab's points.

Each frame each live beam segment gets a chain of point lights: the segment is clipped to `cullDistance` (45 m) around
the camera, a shot from the gun skips its first 1.5 m (the game's own beam light is there), and the rest gets 3 to 6
lights (`beamSpacing` 5 m; 2 under 3 m, 1 under 1 m), evenly spaced, range max(`beamRange` 6, 1.2 x the spacing),
soft-shadow radius 0.12 m (crisp shadows of pillars and enemies). (`beamFade` is **0.1** since 2026-11-21, see "Gunshot
flashes"; the 0.15 s below is the first default.) Colour: the line's start/end colour average x the
material's `_TintColor` / `_Color`, normalised, with 15% white (the revolver: (1, 0.92, 0.58)). Intensity `beamIntensity`
(2) x `beamLights` x a type gain (railcannon x2.5 and range x1.5, Malicious Face x2, enemy x1.2, Mindflayer x1.2,
charged shots x1.4) x the envelope: (1 - age / `beamFade`)^2 (0.15 s of game time) x the line's own width ratio. A
paused game freezes the envelope; a real-time cap (0.6 s) ends it anyway. ContinuousBeams follow their width and have
no age limit. At most `beamMaxLights` (24) per frame, newest beams first; all combat lights go after every scene and
effect light and keep 8 slots free for the enemy glow and portal images appended after them, so they never take an
explosion's or a muzzle flash's slot. Volumetric weight `beamMedium` 0.5.

### Gunshot flashes (2026-11-21, `t3/stained-glass-and-combat-polish`)

User (playtest 2026-11-20): "the light from gunshots lasts far too long to be believable". A revolver shot lit the
frame through three lights: the game's `RevolverBeam` light at the gun (20, range 5, fading linearly at 100 per second:
0.2 s), the beam chain above (0.15 s), and nothing else that mattered (`fx.virtualLights=0` changed nothing beyond the
run-to-run spread). The light model raises a light's gamma colour x intensity to ^2.2, and 20 is four times a torch: the
game's linear fade stayed torch-bright at the gun for ~0.18 s. Now:

- **Gun lights** (`EffectLights`): a tracked light on a `RevolverBeam` or in a `MuzzleFlash` hierarchy (or named
  "muzzle") gets its own envelope on game time: full for `gunFlashHold` (0.02 s), then exp to 5 % over `gunFlashFade`
  (0.06 s), so it is gone by ~0.08 s (light ~0.05^2.2). Such lights are never left to GatherLights' 4 Hz scan (which
  would pack them with the game's fade): `EffectLights.OwnsLight`. A newly tracked light is checked the next frame (it
  waited up to 4 frames before, as an off light).
- **Beam chain**: `beamFade` 0.15 -> 0.1 (the light is 17 % at 33 ms, 5 % at 50 ms).
- `fx.gunFlash=0` and `fx.beamFade=0.15` give the old behaviour.

Measured, 4-1 corridor, revolver (`Weapon` 1), pathtrace, mean luma of the frame `ShotDelay` after the shot (baseline
without a shot 31.5; `bench/results/20261121-002710_tmp-sgc-b`, `20261121-003639_tmp-sgc-c_alt`):

| After | 0.03 s | 0.05 s | 0.08 s | 0.15 s | 0.3 s |
|---|---|---|---|---|---|
| before | 168 (white) | 143 | 74 | 59 | 39 |
| after | 72 | 55 | 30-47 | 27 (baseline) | baseline |

The peak is lower too (the old first frames clipped the whole corridor white). Bench timing jitters by a frame or two
around a spawn, so single values move by ~15. Sheet `4-1_revolver_flash_timing_pathtrace.jpg` in `C:\Users\death\.t3\projects\ultrart\bench\results\review\stained-glass-and-combat-polish\`.

**Relight still lingers** (not fixed): in relight the frame 0.15 s after a shot measured 76-85 against 23 (old envelope
131), lit evenly down the corridor, with auto exposure at x1.00 and held. The denoiser's own anti-lag, forced on
(2 standard errors, histories cut to 2 frames) for 0.3 s around each flash, changed it by under the run-to-run spread
(85 -> 76 at 0.15 s, 102 -> 107 at 0.08 s) and was not kept. The tail comes from another of relight's temporal stages
(radiance cache, upsampler or stabilizer: not isolated). Pathtrace with DLSS RR follows the envelope.

## 2. Parry burst (`fx.parryLight`)

A guarded postfix on `TimeController.ParryFlash` queues a burst ~1.2 m in front of the camera (short of a wall), drained
the same frame (in `OnUpdate`, or in `AppendEffectLights` when the parry came later in the frame). One point light,
`parryIntensity` 4, range 8, colour (1, 0.93, 0.8) (the game's parry light is (1, 1, 0.86)), shadow radius 0.08 m; on
**real** time: full for `parryHold` (0.15 s; the hit-stop lasts 0.25 s), then exp(-t / (`parryFade` / 3)) (0.15 s to
5%), gone after hold + 2 x fade. The game's own parry light (at the player) stays as it is. Volumetric weight
`parryMedium` 0.2.

## 3. Lightning flash (`fx.lightningFlash`)

A postfix on `LightningStrikeDecorative_Animated.FlashStart` arms the bolt; the light starts on the frame its game
`flash` light comes on (the strike, after the build-up). A prefix on `LightningStrikeExplosive.Start` starts one at the
strike point (a 40 m column). Three lights at 35%, 60% and 85% of the bolt's height (the sprite's bounds reach under the
sea), blue-white (0.72, 0.84, 1), shadow radius 0.5 m, total intensity `lightningIntensity` 12, range
`lightningRangePerDistance` (2) x the bolt's distance to the camera (at least 30 m): the camera sits at half the range,
so the light the player sees does not depend on how far the bolt is. Envelope on game time: a strike and two re-strikes
at ~0.06 and ~0.13 s (x0.75, x0.45), each exp(-t / (`lightningFade` / 3)), `lightningFade` 0.18 s; a paused game holds
it, and a real-time cap of 1 s ends it. Volumetric weight `lightningMedium` 0.25. Cleared with every other combat light
when the active scene changes.

**P-2's tunnel of blood** (2026-11-21, `t3/stained-glass-and-combat-polish`; user, P-2 pose 2: "occasional lightning
flashes in p-2's tunnel of blood ... we should give them the same treatment as 5-2"). They are the same
`LightningStrikeDecorative_Animated` bolts (8 in P-2: `Main Section/Inside/6 - Soul Tunnel/6 Nonstuff*/Lightning/
LightningStrike*`, 2 more on the `Shortcut`; the log lists a level's bolts at load, `Combat lights: decorative bolts in
the scene`), and the hook already gave them bolt lights. But the bolts stand in the tunnel behind its walls of blood,
53 m ahead at the user's pose: the shadowed bolt lights reached the walkway at +1.8 luma, and the game's flash (25 /
range 1000) lit nothing there either (it sits in GatherLights' 4 Hz scan only after it is already fading; the walkway
read the same before and during a strike). Scenes in `lightningInsideScenes` (`Level P-2`; the game's scene name,
`SceneHelper.CurrentScene`) get their bolt lights **unshadowed** (`l3.z` 0.75) and x **`lightningInsideGain`** (3). At the
pose, 0.38 s after `FlashStart` (the strike is 0.3 s in): pathtrace mean luma 24.3 -> 42.5, relight -> 50.3; the
walkway and the tunnel walls flash warm. At x16 (`fx.lightningIntensity=200`) the frame went white (92). A level rule
making the game's own flash unshadowed (tried first, `"set": { "shadows": false }`) changed nothing at the strike and
was dropped. `bench/results/20261121-011038_tmp-sgc-h_alt`, sheet `P-2_pose_2_lightning.jpg` in `C:\Users\death\.t3\projects\ultrart\bench\results\review\stained-glass-and-combat-polish\`.

**Plain decorative bolts** (`LightningStrikeDecorative`, no build-up: the strike and its flash at once, a 1.5 s linear
fade) were not hooked; `FlashStart` now gets the same bolt light, in the flash's hue when it is coloured. No level checked
so far uses them (P-2 and 5-2: 0); bench `lightning` / `boltlight` fall back to them.

## Auto exposure

A beam, parry or bolt flash holds auto exposure still (the Adapt pass runs with dt = 0: meter, game match, exposure and
colour balance all keep their state) while it lights the frame and for `exposureHoldTail` (0.15 s) after, at most 1 s at
a time (rapid fire: then it lets go until the flashes stop). Without it a 0.15 s flash moved the meter by ~45% of its
step (4/s, computed, not measured) and could start a darkening at 3 EV/s that crept back at 1.5/s. `fx.flashHoldsExposure=0` turns it off.

## Measured (2026-11-20, alt, 2560x1440, `bench/suites/combat-lights.json`)

A/B against the switch at 0 in the same run, on the raw RT image (luma 0-255, mean / p95 / share at 250+):

| Shot | Mode | Off | On |
|---|---|---|---|
| 4-1 corridor, 3-segment bounce (`beamchain`) | pathtrace | 85 / 170 / 0% | 169 / 244 / 0.01% |
| 4-1 corridor, parry burst (0.03 s) | relight | 17.8 / 34 / 0% | 69.7 / 127 / 0% |
| same | pathtrace | 18.1 / 35 / 0% | 78.1 / 141 / 0% |
| 4-1 parry, 0.7 s later | relight / pathtrace | 18.1 / 17.6 | 18.4 / 18.1 (back to baseline) |
| 5-2 pier, bolt light alone (`boltlight`) | relight | 27 / 56 | 34 / 75 |
| same | pathtrace | 24 / 46 | 31 / 63 |
| 5-2 pier, real strike + game flash (`lightning`, 0.36 s) | pathtrace | 70 / 241 / 2.2% | 78 / 242 / 2.5% |

- **Beams.** The corridor lights up along the shot to its far end; without the chain only the 5 m around each segment's
  start (the game's beam lights) is lit. In relight the game's beam lights alone already take the corridor near the
  camera to luma ~150, so the chain shows mostly down the corridor there. Intensity
  went 6 -> 8 -> 3 -> 2: 8 washed the corridor out, and at 3 pathtrace put 76% of the frame above luma 150 (the game:
  8%); 2 is shipped. The punch dark delta during the flash (+66 pathtrace) is the flash itself, not a steady lift.
- **Parry.** At intensity 40 (first try) the pathtrace frame clipped 67%: a torch's Fire light is 5. At 4 the corridor
  walls light warm with a falloff into the dark, nothing clips, and the gun is not blown out (it picks up little of the
  burst at this strength). The burst is gone 0.7 s later. Two bench traps: an effect spawned with `ShotDelay` 0 is not
  in the capture unless the light is drained in `AppendEffectLights` (fixed), and slow bench frames around a spawn
  stretch a 0.06 s `ShotDelay` to ~0.17 s of real time, past a 0.12 s hold (relight then showed 10% of the burst).
  Relight's temporal filters were not the cause: TAAU off, cache off and `dn.alphaMin=0.5` all gave the same.
- **Lightning.** The game's flash (25 / range 1000) already lights the pier near white during a strike, so the bolt
  light adds a cool, sharper-shadowed spike on top (pathtrace mean +8, clipped share 2.2% -> 2.5%). At intensity 18 it
  was 2.3% -> 4.2% clipped, hence 12. The re-strike flicker is not visible in stills.
- **Cost.** `-Perf`, exclusive lock, 4-1 corridor, a 3-segment bounce and a parry every 0.2 s, modes alternating every
  2 s in one pass: pathtrace 6.29 -> 6.41 ms, GPU 4.32 -> 4.35 ms; relight 6.31 -> 6.29 ms, GPU 4.14 -> 4.15 ms; plugin
  CPU 0.87 / 0.78 ms both ways. Within the noise.

Bench kinds (`bench/README.md`): `beamchain`, `railbeam`, `parry`, `parrygame`, `lightning`, `boltlight`.

### Laser beams: line lights (2026-11-21, `t3/7-4-laser-lights`)

User (7-4, Brain chamber, during the fight): the long hot-pink lasers should light the walls, ceiling, floor and Brain
along their whole length. They didn't: at the user's pose the beam lights added nothing measurable.

**What the lasers are.** Every laser in 7-4's Brain fight is a `ContinuousBeam` (the Mindflayer's component) with a
2-point world-space `LineRenderer`, material `MindflayerBeam 1` (`Sprites/Default`, white tint; the pink is in the
texture `MindflayerBeam3`). The scene has 81: the corridor barriers `Main/Centaur Full/Hide When Outside/BeamsGroup*/
Centaur Brain Beam Obstacle*` (37, 19.5 m long, width 2, static) and the fight's `Main/Interior/InteriorStuff(Clone)/
BrainFight/LaserRing/Cube*/Beams/Centaur Brain Beam*` (24 at once, 27-34 m, width 1, on a ring that turns around the
Brain) plus `BrainFight/VerticalBeamsBeam`. `InteriorStuff` is instantiated at load, so its beams come through the spawn
hook; the rest through the scene scan. Providence's beam attack (`Drone.ShootSecondary` spawns a `Pincer`, whose child
lines turn towards the pincer's axis and shrink their width at the end) and the Mindflayer are the same kind of
sustained line. Why nothing showed before:

- The beam chain had 24 lights per frame in total, 3 to 6 per segment within 45 m: about four of the 49 segments in
  view got light, as separate pools.
- The colour came from the line and the material (both white), so the few lights there were white, not pink.
- `MaxBeams` was 64; 7-4's 81 scene beams filled it, so a Providence spawned later would have been dropped.

**Now** (`CombatLights.PackLines`, `fx.beamLines`, on by default): each segment of a sustained beam (`ContinuousBeam`)
that is on gets **one line light** (a new area-light shape, docs/lighting-overrides.md "Area lights (shader)"): the
light is evaluated at the nearest point of the segment, so the whole beam lights the room evenly, all round, and the
light is re-read every frame, so it follows a turning ring or a sweeping pincer. Intensity `lineIntensity` (3) x
`beamLights` x 1.2 (the Mindflayer gain) x the line's width / its full width (`originalWidth`; the widest width seen
while that is still 0: it is set in the beam's `Awake`); range `lineRange` (10 m) x sqrt(width / 0.35) (the Mindflayer
10 m, the ring 17 m, the barriers 24 m); colour: the line x material colour x the texture's average colour (from the same
16x16 downsample as the effect lights), normalised and raised to the 4th power, because the texture's white core makes
the average pale ((1, 0.72, 0.87) -> (1, 0.27, 0.57)). At most `lineMaxLights` (40) per frame, the segments nearest the
camera first, within `lineCull` (70 m); they are packed ahead of the shot chains. Weight in the air `lineMedium` 0:
volumetrics treats area lights as points at their centre, which would put a glow in the middle of each beam.
RevolverBeams (shots, coins, railcannon) keep their point chains (calibrated with them; a shot lasts 0.1 s). `MaxBeams`
is 256. The log lists the scene's beams (`Combat lights: N ContinuousBeams in the scene`, the first 12 with width, hue and
material) and, every 10 s while lines are lit, how many were packed.

`data/lights/Level 7-4.json` lost its two static area lights on the barrier `BeamsGroup (3)`
(`brain-laser-barrier-front` / `-back`): the barrier's own line lights replace them.

**Measured** (alt, 2560x1440, trace scale 0.5, `bench/results/20261121-155400_tmp-7-4-lasers_alt`; the user's pose,
checkpoint load, the fight's laser ring switched on with the bench's `Activate`, game time frozen so every mode shows
the same frame). Mean RGB of the lower right of the frame (floor and wall under the beams), 0-255:

| Mode | user's pose | second angle |
|---|---|---|
| off (the game) | 29 / 21 / 34 | 43 / 41 / 57 |
| pathtrace, no beam lights | 31 / 33 / 41 | 39 / 74 / 79 |
| pathtrace, before (point chains) | 30 / 31 / 39 | 39 / 73 / 79 |
| pathtrace, line lights | 46 / 36 / 53 | 70 / 78 / 95 |
| relight, before | 32 / 31 / 40 | 41 / 74 / 81 |
| relight, line lights | 49 / 37 / 55 | 72 / 80 / 97 |

Frame mean luma: pathtrace 65.0 -> 71.6, relight 66.9 -> 74.3 (the game 56.4). The floor, the column and the walls
under the ring take on the beams' magenta, all along them. `lineIntensity` 1.6 / range 5 (the first try) was invisible;
4 / 10 was a little stronger than shipped.

**Cost** (`-Perf`, exclusive lock, main, 2560x1440; same pose, 49 segments in reach, 40 packed): GPU total pathtrace
7.21 ms (line lights) / 7.15 (chains) / 7.74 (no beam lights), relight 6.83 / 6.94 / 7.03: within the run-to-run
spread. Plugin CPU `lights` 0.43 / 0.40 / 0.32 ms. In a level without lasers nothing runs on the CPU, and the shader's
line branch sits inside the area-light branch: 1-4 lamps, old build -> new build, GPU relight 3.15 -> 3.14 ms,
pathtrace 4.23 -> 4.23 ms, CPU `lights` 0.07 ms both.

**Not verified in a run:** Providence's pincer and the Mindflayer (no level with one was loaded); they take the same
path (spawned `ContinuousBeam`s). If a pincer line turns out not to carry a `ContinuousBeam`, it gets no light.

Bench switches added for this (bench/README.md, "Poses deep in a level"): `Path.ApproachSpare` (7-4: `["boss", "Wicked"]`; killing the Brain
fight's Wicked flushes the player out of the Earthmover), `Path.Activate`, `Path.FreezeAtPose`; `-ultrart-effectsurvey`
also lists the enabled line renderers near the camera.

### Limits

- **No coin or railcannon shot measured.** Coin shots spawn the same `RevolverBeam` segments as `beamchain`; the alt
  save has no railcannon (`railbeam` fails with "no Railcannon"). Enemy beams and the Mindflayer were not seen in a run.
- **The parry point is a guess.** `ParryFlash` gets no position, so the burst sits 1.2 m ahead of the camera, not at
  the parried projectile or enemy.
- **Point chains, not line lights, for shots.** RevolverBeams still get 3 to 6 points per segment, which can show as
  separate pools on a wall right next to a long beam. Sustained beams use line lights since 2026-11-21 ("Laser beams");
  the shots could too, after a re-calibration.
