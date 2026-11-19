# Cleanup audit, 2026-11-19

A static audit of the repo for legacy and unused code, duplication and structural problems, as phase 1 of a cleanup:
**report only, no code changed**. The lead decides what gets implemented. Base: `main` at `ba65188` (after the
aliasing-measurement merge).

**Method.** No game runs. The plugin was compiled from a scratch copy with the .NET code-style analyzers raised to
warnings (`IDE0051` unused private member, `IDE0052` unread member, `IDE0060` unused parameter, `CS0649` never
assigned); every hit was checked by hand, because Unity messages, Harmony patches, JSON-deserialised classes and the
bench's reflection variants (`plugin._field=value`, `Plugin.Burst.cs:131-139`) all look unused to the analyzer.
Everything else is grep evidence: references from C# to shader kernels, raygens, properties and defines; from scripts,
docs and code to scripts, tools and bench suites; and the docs against the code defaults. Line counts are measured where
a whole file or method goes, and estimates (`~`) elsewhere.

**Headline.** The code is in better shape than the rapid iteration suggests: almost every "old" path named in the brief
(ReSTIR, the TAAU upsampler, the non-flame-driven torch lights, the denoiser presets, the debug views) is still
reachable on purpose, and most diagnostics are documented and gated. The real weight is elsewhere:

| Where | What | Lines removable |
|---|---|---|
| `bench/suites` | 107 one-off suites that nothing references (diag/debug/A-B runs, 2026-11-13..19) | ~5,900 (of 33,600) |
| `src/UltraRT.Probe` + launchers | The feasibility probe plugin, still built and deployed into both installs; three superseded launch scripts | ~540 |
| C# dead code | Compiler-confirmed dead members, the `fx.flameBillboards` test, `-ultrart-dumpverts`, `_readdDynamic` | ~260 |
| Docs | Four dated plan/handoff/audit docs indexed as reference; stale status sections | ~1,400 moved to an archive, ~60 edited |
| Duplication (C# + HLSL) | RT (re)allocation, bundle loading, dispatch boilerplate, vertex readback, RNG/sampling/oct/luma helpers in shaders | ~700-900 net |
| Superseded options (owner decisions) | ReSTIR experiment knobs, old denoiser presets, old A/B flags | ~150-300 + shader |
| Config plumbing | 103 menu settings + 30 loose `Config.Bind` + 285 flag cases in 23 hand-written parsers + reflection bench keys; one-line defects (P1-P5) | ~450-500 via a table-driven `Setting<T>` |
| Structure | `Trace()` (400 lines), `Plugin` partial with ~457 fields | refactor, not removal |

Total safely removable without a behaviour change: **about 6,700 lines** (6,400 of them suites, scripts and the probe).
With the duplication, the settings registry and the owner-decision items: **about 8,000-8,500**.

## Decisions (user, 2026-11-19)

- **Dead code goes:** everything in section 1, B1, the P-items.
- **Legacy options mostly go**, and **the MSAA probe goes** (O8). The constraint: users without an NVIDIA GPU must
  keep a full-quality path, so UltraRT's own denoiser (`Denoiser`, Full preset), the temporal upsampler and the
  remodulation path for pathtrace without RR all stay; only their old A/B variants go. Proposed split:

  | Goes | Stays |
  |---|---|
  | ReSTIR experiment knobs (O1); denoiser presets `Static`, `Previous`, `Legacy` and the Ctrl+F3 cycle (O3; `Full` becomes the only preset, its individual fields stay as bench keys); the TAAU-off 2x2 composite, F2 and `-ultrart-notaau` (O4); `fx.flameDriven=0` (O5); the old-look and early-days flags (O6, O7, O9); `Plugin.MsaaProbe.cs` (O8); `Plugin.EnemySurvey.cs` if EnemyGlow does not read its output | `Reference` light sampling (ground truth for `-ultrart-accumulate`); RIS; the debug views; the documented diagnostics of section 8 |

- **Hybrid mode: dropped, with ReSTIR** (decided later the same day; RIS and Reference stay). What it alone kept alive: ReSTIR as a sampler (`hybridMode` defaults to ReSTIR and its
  composite uses the reuse passes, `LightSampling.cs:27, 104, 119`), the second denoiser instance (`_denoiserDirect`,
  `Plugin.cs:841`), `UnpackHybrid.compute`, the `_gHybrid` G-buffer (allocated in every mode, `Plugin.cs:921`), the
  game-colour copy and ratio composite (`Plugin.cs:1548-1567`, `Composite.shader`), `PackHybridW` and `_Mode == 3`
  branches in `PathTrace.raytrace`, and per-feature hybrid weights (`-ultrart-emissive-hybrid`, normal strength
  hybrid, viewmodel / particle / reflection hybrid paths). "hybrid" appears 136 times over ~25 C# files and in 15
  shader files. Dropping it would take ReSTIR with it entirely (RIS + Reference remain), roughly 54 ReSTIR mentions in
  `LightSampling.cs` and 53 in `UltraRTLights.hlsl`. Estimated ~600-900 lines with the ReSTIR removal; needs one
  visual run of relight and pathtrace to confirm nothing shared changed. It does not affect non-NVIDIA users: relight
  and pathtrace already run with UltraRT's own denoiser.

## Implementation status (2026-11-19, branch `t3/codebase-cleanup-audit`)

- **PR 1 (dead code and one-line defects): done**, commit `8a3145e`, build-only check. Section 1 as listed, except:
  **C3 kept** (`_readdDynamic` is used by the `chairs-diag` / `chairs-repeat` suites; the "used by no suite" below was
  wrong) and **C9 kept** (`_cutoutFlip` and `_portalJumpDiag` are documented diagnostics, docs/path-tracing.md and
  docs/portals.md). `UltraRT.Probe` is out of the build and deploy scripts, but the installed
  `BepInEx\plugins\UltraRT\UltraRT.Probe.dll` is still in **both game installs** (main, Steam; alt,
  `D:\UltraRT-Bench\ULTRAKILL`). Deleting it is left to the lead; nothing builds or deploys it any more.
- **PR 2 (bench suites, docs): done**, commits `e8365a4` (102 unreferenced suites deleted, the five added on 11-19 kept)
  and `70a5e1f` (dated docs to `docs/archive/`, handoff traps into bench/README.md, stale docs fixed).
- **Hybrid mode and ReSTIR: removed**, a separate commit after PR 2. One denoiser instance; no `UnpackHybrid.compute`,
  ratio composite, game-colour copy, reservoir buffers, `_Mode` 3, game-cube removal or per-feature hybrid weights.
  `_HybridLight` is now `_LitWeight` (its lit weight in `w` is read by auto exposure and RR in every mode), so
  PathTrace binds 7 UAVs. Fallbacks: `hybrid` as a view mode (config, `-ultrart-view`, bench) gives relight, and
  `-ultrart-lights restir` / bench `lights=restir` give RIS, each with a warning; a config `LightSampler = ReSTIR` is
  reset to the default. Bench suites lost their hybrid and ReSTIR-key modes (three hybrid-only suites deleted:
  `perf-8-3-portals`, `perf-8-3-route-hybrid`, `portals-align`). Docs got dated removal notes; measured history
  stays. Checked with an A/B on alt (relight / pathtrace / pathtrace with UltraRT's denoiser at 4 poses, before vs
  after build) plus `poses-quick`; the numbers are in the commit message.
- **Not started:** PR 3 (duplication), PR 4 (legacy options, including the MSAA probe O8), PR 5+.
- **Follow-ups found on the way:** `_LitWeight` could shrink to one channel (only `w` is used now); the proxies still
  get `_GameRefl` / `_CubeTex` for every REFLECTION material although only pattern cubes read them now;
  `ViewmodelComposite`'s `_VMDirect` stays (the filter's luma moments use it).

## Proposed PRs

Ordered so that each one is reviewable on its own and the risky work comes last. Sizes are lines removed (net).

| PR | Contents (item ids below) | Verification | Size |
|---|---|---|---|
| **1. Dead code and one-line defects** | C1-C9, S1-S2, N1, X1-X4, P1-P5 (P1 changes only what a mistyped flag does) | `build.ps1 -NoDeploy` only (the shader items need the bundle build, which it does). No game run: every removed path is off by default and reached only by a flag or bench key nobody uses | ~800 |
| **2. Bench suites and docs** | B1-B4, D1-D9 | none (no code); the lead skims the suite list in B1 against their own notes first | ~5,900 suites + docs moves |
| **3. Shared helpers (C# and HLSL)** | H1-H9 | build + one short visual A/B on alt (`-Kind visual`): `short` or `merge-regress`-style poses in relight and pathtrace, before/after screenshots must match bit-for-bit or within noise | ~700-900 |
| **4. Retire superseded options** | O1-O9, each a separate commit | owner decision per item; one run of the feature's own suite | ~150-300 + shader |
| **5+. Structure** | R1-R7, one PR each (R5 = section 7) | build + `-quick` suite per PR | refactor; R5 saves ~450-500 |

PR 1 and PR 2 can go in parallel today. PR 3 should land before PR 5 (the helpers make the extractions smaller).

---

## 1. Dead code (PR 1)

All verified by the analyzer build plus grep; "refs" counts are over `src/`.

| Id | Location | Evidence | Risk | Size |
|---|---|---|---|---|
| C1 | `Plugin.FlameQuads.cs` (whole file), `Plugin.cs:1295`, `EffectLights.cs:58-60, 612, 647`, `EmissiveLights.cs:97-101, 138-168, 395` | `fx.flameBillboards` (default 0) is "the literal flames-as-emitting-surfaces test", recorded as "tested 2026-11-18: no" in `docs/emissive-lights.md:309`; still called every frame from `Trace()` (`UpdateFlameQuads(_camera); // fx.flameBillboards (test)`). Only reachable by the bench key | safe (off by default); update emissive-lights.md:309, 445 | ~120 |
| C2 | `SceneProxies.cs:30-31, 286-300, 1434-1494`, `StaticGroups.cs:309-312`, `Plugin.cs:232` | `-ultrart-dumpverts` / `DumpVertices` / `Decode`: a one-off vertex-layout investigation of the StaticSceneOptimizer mesh; not in any doc or suite | safe | ~80 |
| C3 | `Plugin.cs:709-710, 1262`, `SceneProxies.cs:898-910` | `_readdDynamic` / `ReAddDynamic()`: "Diagnostic (bench plugin._readdDynamic=1)"; used by no suite, no doc | safe | ~20 |
| C4 | `Plugin.Stains.cs:181-185` | `ReleaseStains()` has 0 callers (IDE0051). Either call it from the plugin's `OnDestroy` (`Plugin.GameDither.cs:63`, the only teardown) or delete it; the plugin is never destroyed in practice | safe | 5 |
| C5 | `Plugin.Particles.cs:220-221`, `Plugin.Volumetrics.cs:266-272` | `ParticlesSummary()`, `VolumetricsSummary()` "For bench logs": 0 callers (IDE0051); the bench lost its call sites | safe (or wire back into the bench log if wanted) | 10 |
| C6 | `Plugin.cs:786` | `AsFloat(int)`: 0 callers | safe | 1 |
| C7 | `SceneProxies.Veils.cs:105, 110`; `Plugin.PortalLights.cs:23, 58` (`_portalLightImages`); `Plugin.Viewmodel.cs:84` (`_vmLastRect`), `:124` (`VmGB4Id`); `Cinematics/FrameSink.cs:39` (`_lock`) | written, never read (IDE0052) | safe | ~8 |
| C8 | `Plugin.LightFit.cs:49` (`outDir`), `Plugin.Viewmodel.cs:368` (`hud`) | unused parameters (IDE0060) | safe | 2 |
| C9 | `Plugin.Cutout.cs:22` + `DlssRR` `CutoutFlip`; `Plugin.PortalTemporal.cs:26` + `SceneProxies.Portals.cs:379-…` (`_portalJumpDiag`) | bench-only diagnostics set by reflection, but used by no suite (`_cutoutFlip`: "flip the cutout rays' y", a one-off sign check); every other `CS0649` field is a live bench variant (see "Kept") | safe | ~30 |
| S1 | `UltraRTMaterial.hlsl:105-106` | `HybridLitWeight`, `HybridGameCube`: the decoders of `PackHybridW` (written at `PathTrace.raytrace:527`), called nowhere; `UnpackHybrid.compute` reads only the light halves | safe, shader build | 2 |
| S2 | `UltraRTMaterial.hlsl:30` `ULTRART_CLASS_COUNT`; `UltraRTWater.hlsl:11-12` `ULTRART_TRANSPARENT_GLASS/_WATER` | `#define`s with no other use | safe, shader build | 3 |
| S3 | `PathTraceCount.raytrace` | checked: **not** dead (the ray-counter variant behind `plugin._rayCounts=1`, docs/performance.md) | keep | 0 |
| N1 | `native/UltraRT.Native/Hdr.cpp:1199` | `UltraRT_Hdr_GetStatus` export: no caller in C#, the preloader or `HdrTest.cpp` | safe, native build | ~5 |
| X1 | `src/UltraRT.Probe/` (416 + 37 lines), `scripts/build.ps1:66, 86`, `scripts/lib/slots.ps1:61`, `scripts/run-probe.ps1` (26) | The day-1 feasibility probe (`ooo.lew.ultrart.probe`). Inactive without `-ultrart-probe`, which only `run-probe.ps1` passes; not in any doc. Still compiled, staged and **deployed into both installs, where BepInEx loads it on every launch**. `run-probe.ps1` has no lock or user-playing check and no `-force-gfx-jobs legacy` | safe for the code; the stale `BepInEx\plugins\UltraRT\UltraRT.Probe.dll` in main and alt must be deleted by hand under a lock (`Sync-Stage` copies, it does not prune) | ~480 |
| X2 | `scripts/run-debugview.ps1` (28), `Plugin.cs:16-20` header | Superseded by `bench.ps1` captures; no lock check. The `Plugin.cs` header still documents it and says "F8 next mode" (Page Up / Page Down since; F8 is the game's console) | safe; AGENTS.md line on the old launchers changes | ~32 |
| X3 | `tools/bench/run-bench.ps1` (35) | Re-implements acquire / deploy / bench / release; hard-codes the main checkout path; only documented in `docs/archive/handoff-2026-11-18.md` | safe (or document it in bench/README if the lead uses it) | 35 |
| X4 | `bench/suites/lights-classify.json:3` | **Broken JSON**: the `Note` contains `BepInEx\ultrart\...` (`\u` + non-hex), which Newtonsoft and Python reject, so the suite cannot load. Fix the escape (`\\`) or delete with B1 | safe | 1 |

## 2. Bench suites (PR 2)

`bench/suites` holds 441 JSON files (379 top-level, 62 in `tour/`, `review/`, `attr/`), 33,600 lines. A word-boundary
search for each suite name over `docs/`, `README.md`, `AGENTS.md`, `bench/README.md`, `scripts/`, `tools/` and `src/`
finds 279 referenced (the evidence behind a documented number, a generator, or a standing suite) and 117 that nothing
references. The git dates are first-commit dates (the LF conversion of 2026-11-17 touched every file, so last-change
dates are meaningless).

**B1. Delete 107 unreferenced one-off suites (5,876 lines).** Git keeps them; a doc that later needs one can restore it.
`audit-1115-c audit-1115-d audit-1115-e beams-3-1-diag beams-3-1-quick blood-river-24 blood-river bugs-1114
chairs-verify debug-1-1-new debug-4-1 debug-4-1b-parts debug-4-1b debug-8-3 debug-approach debug-cost debug-fixes
debug-fixtures-torch debug-fixtures debug-gi debug-grass-down debug-grass-native debug-hybrid debug-new debug-outdoor
debug-radius debug-ratio debug-relight debug-round3 debug-skinned debug-strip debug-strip2 debug-teardown-off
debug-teardown debug-terminal displace-perf-range emissive-1-4-hall exposure-pm4 exposure-quick fixture-census-b
fixture-census-c fixture-census-d ghost-floor ghost-move glass-14 glass-62 glass-fog glass-look glass-probe grass-lit
haze-perf infra-tiny lights-a lights-classify lights-verify-pt lights-verify mgw-diag mgw-diag2 mgw-diag3 motion-visual
nofog-0-1 noise-late-pre noise-pm-31 noise-pm-31b noise-pm-diag orb-dim plan-bright plan-materials plan-perf-floor
plan-perf-notimer pool-probe portal-fix-fresh portal-fix-quick portal-fix portal-hairline portals-hops poses-checkpoint
pt-energy pt-final pt-look pt-motion pt-perf2 pt2-a pt2-b pt2-c pt2-d pt2-e pt2-f pt2-h pt2-l pt2-perf res-test
sampling-visdbg shade-1115 trans-rect unlit-a unlit-b unlit-c unlit-d unlit-e unlit-verify user-0-1 v1-late-check
v1-late-diag vfx-regress vm-lava volumetrics-noise`.
Risk: safe for the code. Two caveats for the lead: agents' memory notes may name some of these (memory is outside the
repo; restore from git if one is wanted), and the five added on 2026-11-19 (`glass-62`, `glass-fog`, `nofog-0-1`,
`portal-hairline`, `trans-rect`) may belong to work still in flight, so hold those back if a branch is open on them.

**B2. Keep, but note.** Not deleted by B1: `tour/` (one per level, written by `tools/lights/tour_suites.py`),
`review/` and `attr/` (documented by pattern in docs/lighting-overrides.md), the `trailer-final-*` / `trailer-v4-scout*`
suites (written by `scripts/trailer-plan.py`, documented in docs/trailer.md), `tour-void` (generated alongside). The
suites cited only by the historical docs of D1 (`audit-1115-a/b/diag`, `fx2`, `look3`, `ghost-*`) stay with those docs.

**B3. Encoding.** `merge-regress.json` and `user-1114-late.json` start with a UTF-8 BOM (Newtonsoft accepts it, strict
parsers do not); strip it when touching them.

**B4. Optional follow-up.** Many referenced suites are cited once, as the run behind a dated measurement (for example 16
`noise-*`, 9 `pt2-*`/`pt-*`, 7 `haze-*`). They are history, not tools. A later pass could move all such suites to
`bench/suites/archive/` and leave only the standing ones (`short`, `full`, `perf-*`, `*-quick`, `merge-regress`,
feature suites named in bench/README) at the top level, so `ls bench/suites` reads as a menu again. Not proposed for
now: every move breaks a doc link.

## 3. Docs (PR 2)

Every doc is indexed and every flag, script, path and default checked (about 60 settings against their `Config.Bind`
defaults, every `-ultrart-*` flag mentioned in a doc against the code) matches, except as listed.

| Id | Location | Finding | Action | Size |
|---|---|---|---|---|
| D1 | `docs/archive/plan-look-and-perf.md` (615) | Indexed as "Plan of 2026-11-17 (not implemented)" (`README.md:127`) but mostly implemented (its own status table); says the default mode is `relight` (line 9); names `data/lights/_prefabs.json` (line 317), which never shipped (prefabs are `prefab:*` classes in `_rules.json`) | move to `docs/archive/`, relabel the README row "historical plan", fix ~10 links (`lighting-overrides.md:104, 1455`, `relight.md:168`, `reflections.md:554`, `performance.md:202`, `SceneProxies.Player.cs:38`, `tools/lights/score.py:21`) | move |
| D2 | `docs/archive/handoff-2026-11-18.md` | Its open branches (`t3/materials-fx-2`, `t3/perf-dispatch` ray counters, `t3/stained-glass`, `t3/cutout-pass`) are merged; scratch paths in another worktree. Section 4 (lines 78-112: `-Extra` quoting, the proposal layer, the "variant cannot set a field the frame overwrites" trap, the HDR capture check) is documented nowhere else | fold section 4 (~35 lines) into bench/README.md, archive the rest | ~35 moved |
| D3 | `docs/archive/audit-2026-11-15.md` (150) | Scores 33 poses at `7420f2c`, before the exposure policy, punch and film curve; its verdicts no longer describe the build. Cited only by `tools/lights/overview.py` | archive; README row "snapshot, superseded" | move |
| D4 | `docs/archive/pt-plan.md` | Sections 1-5 are the pre-implementation plan (line 13: "`relight` (default)", ReSTIR DI); section 6 (results, "not done and why") is cited by path-tracing.md, dlss.md, light-sampling.md | fold section 6 into path-tracing.md and archive, or relabel the README row "history and experiments" | ~80 moved |
| D5 | `docs/lighting-overrides.md:1376-1380` "Per-level status (2026-11-15)", `:1506-1513` "Not done yet" | Both describe the state before light classes and the punch policy ("Other levels have no file", "nothing but the rules ships yet") | delete or retitle as dated history | ~20 |
| D6 | `docs/lighting-overrides.md` (1,513 lines) | Mostly dated per-branch sections (lines ~101-1243) around a reference core | later: split into a reference doc (format, classes, rules, editor, classifier) and `archive/lighting-history.md` | ~700 moved |
| D7 | `README.md:100` | `relight.md` is "The default mode"; the default is `pathtrace` where RR runs | "The relight mode (default without DLSS RR)" | 1 |
| D8 | `docs/materials.md:220` | "Final build at the defaults (... relight normal strength 2 ...)" without the note that the default is 1 since 2026-11-17 (`MaterialMaps.cs:23`) | add the note (AGENTS.md rule) | 1 |
| D9 | `SettingsMenu.cs:214-221` vs `README.md:125` | The in-game Keybinds page omits Ctrl+F7, Shift+F7, Shift+F3, F10, F11; README says it "lists the keys" | add the rows (code) or say "most keys" | ~5 |
| D10 | `docs/light-sampling.md:309` | the `-ultrart-lights` parser sketch maps `default:` to ReSTIR; RIS is the default | check against `Plugin.cs` and fix | 1 |
| D11 | `docs/relight.md:433-458` "HDR displays" | restates hdr.md | trim to a pointer | ~20 |

## 4. Duplication (PR 3)

None of these changes behaviour by intent, but all touch hot paths or shader maths, so each needs a build plus a short
visual A/B (same poses before and after; the RNG and luma moves must be bit-identical).

| Id | What | Where | Proposal | Size |
|---|---|---|---|---|
| H1 | Render-texture (re)allocation on resize | 54 `new RenderTexture`; 19 inline `width != w` checks in 10 Plugin partials (`Plugin.cs:788 EnsureGBuffer, 970, 1469, 1554`, `Plugin.Exposure.cs:203`, `Plugin.Hdr.cs:268, 295`, `Plugin.Remod.cs:104`, `Plugin.Transparent.cs:149`, `Plugin.Viewmodel.cs:467, 473`); per-class `Create` clones in `Denoiser.cs:606`, `DlssRR.cs:646`, `ReflectionFilter.cs:192`, `TemporalUpsampler.cs:231`, `Volumetrics.cs:561`, `Plugin.MsaaProbe.cs:45` | one `RtUtil.Ensure(ref rt, desc, name)` + `EnsureCopyOf(ref rt, src)` (the four "copy of a game texture" blocks) | ~120-150 |
| H2 | Shader loading from the bundle | 31 `LoadAllAssets` calls in 14 partials (`Plugin.cs` 8, Particles 3, Veils 3, Viewmodel 3, Volumetrics 3, Stains, Lava, Wet, Cutout ...) | one `Bundle.Find<T>(name)` over a list loaded once per (re)load, warning on a miss; also faster F9 / hot reload | ~60 |
| H3 | Compute dispatch boilerplate | 203 `Set*TextureParam` calls; `Plugin.Viewmodel.cs:653-703` repeats `SetComputeTextureParam(cs, k, ...)` 25 times; `Mathf.CeilToInt(w / 8f)` everywhere | a small `ComputePass` (`Tex(name, rt)`, `Dispatch(w, h)`) | ~60-80 |
| H4 | GPU timer slots | 45 `_gpu?.Begin/End` lines over 9 partials; slot numbers 0-22 hand-tracked in a comment at `Plugin.cs:1631` | `using (_gpu.Scope(cmd, Slot.X))` with an enum | ~40 |
| H5 | Vertex-buffer readback | "GetVertexBuffer, copy, decode attribute at stride/offset" in `SceneProxies.Displaced.cs:211, 310`, `StaticGroups.cs:436, 467`, `EmissiveLights.cs:1185-1257`, `LightShafts.cs:796, 893`, plus C2's dump; `VertexAttributeReader` (`MaterialMaps.cs:489`) exists but only the atlas uses it. The two `Decode` copies (`SceneProxies.cs:1483`, `EmissiveLights.cs:1242`) differ in SNorm support and the miss value (NaN vs 0): merge deliberately | extend `VertexAttributeReader` into the one mesh reader | ~150 |
| H6 | Material/shader-name tests | `"ULTRAKILL/Master"` compared at 13 sites, `shader.name.Contains("Stationary")` at 7 (`SceneProxies.cs:358, 1350, 1408`, `SceneProxies.Cutout.cs:90`, `SceneProxies.Sync.cs:86`, `SceneProxies.Unlit.cs:182`, `StaticGroups.cs:351`), the full `Traceable(m) && !…Stationary` expression 3 times; water regexes twice with different lists (`MaterialClasses.cs:360`, `TransparentSurfaces.cs:59`); glob-to-regex twice (`SceneProxies.Unlit.cs:60`, `MaterialClasses.cs:221`) | a static `Mats` helper (`IsMaster`, `IsAtlas`, `Glob`) | ~60 |
| H7 | Luminance in C# | `0.2126/0.7152/0.0722` inline at ~20 sites; two private `Lum` helpers (`EmissiveLights.cs:1258`, `Plugin.LightFit.cs:281`). **Not** `Plugin.LightClassifier.cs:227`, `Plugin.SkyGI.cs:187` (gamma-decoded first) | `Luma()` extensions on `Color`/`Vector3` | ~35 |
| H8 | HLSL sampling helpers | `Pcg`+`Rand` defined 5 times identically (`CacheUpdate.raytrace:28`, `ParticleLight.raytrace:30`, `PathTrace.raytrace:48`, `Viewmodel.raytrace:57`, `Volumetrics.raytrace:75`); `CosineHemisphere` 3 times; `SampleGgxVndf` + `ReflectionDirection` twice (`PathTrace.raytrace:67, 86`, `Viewmodel.raytrace:74, 90`) | move into `UltraRTCommon.hlsl` (or a new `UltraRTSampling.hlsl`) | ~85 |
| H9 | HLSL screen-space helpers | `OctWrap/OctEncode/OctDecode` twice (`Denoise.compute:37-46`, `Reflections.compute:35-42`) plus `OctEncode11` in `UltraRTLights.hlsl:850`; `GeoPos/GeoNormal/PrimaryDir` twice (`Denoise.compute:64, 65, 135`, `Reflections.compute:58, 59, 26`; `PrimaryDir` takes `int2` vs `uint2`); `Luma` defined in 6 files and `dot(c, 0.2126...)` inline 35 times; `FogVisibility` 3 times with the same maths (`TransparentOverlay.shader:123`, `VeilTrace.raytrace:30`, `VolumetricsComposite.shader:72`) | shared include(s) | ~85 |
| H10 | Scene scans | `FindObjectsOfType` 11 times in `SceneProxies.cs` alone, plus `EffectLights.cs:245`, `LitParticles.cs:235, 298`, `LightShafts.cs:200`, `StaticGroups.cs:184` | a per-rebuild scene census; saves level-load time more than lines. `FindObjectsOfType` and `FindObjectsByType(None)` order differently: check ordering assumptions | runtime gain, needs a level-load timing |
| H11 | Script paths | the `D:\SteamLibrary\...` default copied into `bench-stats.ps1:7`, `display-prefs.ps1:8`, `game-lock.ps1:24` (and the two launchers of X1/X2) though `scripts/lib/slots.ps1:5` has it; `C:\Users\death\.t3\projects\ultrart` hard-coded in `bench-stats.ps1:8`, `game-lock.ps1:26`, `run-bench.ps1:11` | use `Get-SlotDir` / one constant | ~10 |
| H12 | Python helpers | `load`, `box`, `fmt`, `mask_for`, `verdict` re-written across `tools/aliasing/*.py`, `tools/lights/score.py`, `sheet.py`, `handover.py` (not identical) | optional `tools/common.py` | ~60, low value |

## 5. Superseded options that are still reachable (PR 4: owner decisions)

These are deliberate A/B paths, not dead code. Each is a behaviour change only for whoever selects the old path. Listed
with what a removal would take; none is recommended without the owner's yes.

| Id | Option | Default / reachable from | Evidence it is superseded | Removal size |
|---|---|---|---|---|
| O1 | ReSTIR experiment knobs: `visibilityMemory`, `occludedRetest` + `retestCap` + `retestUmbra`, `perPixelLightChange`, `visibilityDebug` (`LightSampling.cs:38-103`) | all off; bench keys `ls.*` (106 uses in suites, all historical) | docs/light-sampling.md: visibility memory "no noise gain with RIS, 5 % with ReSTIR" and costs a 16-byte read/write per pixel + a buffer pair; the retest "30 % in one room, 17-24 % more boil elsewhere" | ~70 C# + the shader branches in `UltraRTLights.hlsl` (not sized) |
| O2 | ReSTIR itself as a main-mode sampler | `mode` = RIS; ReSTIR via F6, menu, `-ultrart-lights`, `lights=restir` | still the **hybrid** default (`LightSampling.cs:27`) and needed by its composite: keep. Only the F6 / menu choice in relight/pathtrace could go | small; probably keep |
| O3 | Denoiser presets `Previous` and `Legacy` (`Denoiser.cs:42, 148, 179-180`, `Plugin.Cache.cs:98-113`, `SettingsMenu.cs:40, 95`) | default `Full`; Ctrl+F3, menu, flags | A/B baselines for the static/motion-noise docs; decide whether those baselines are still wanted | ~40 + shader stability branches |
| O4 | TAAU off (the 2x2 depth-guided composite) | upsampler on; F2, `-ultrart-notaau`, menu | A/B only (docs/denoiser.md:248). The upsampler itself is **not** superseded: DLSS RR uses it for remodulation (`DlssRR.cs:212`) | not sized (shader path in Composite) |
| O5 | `fx.flameDriven=0` (game torch lights left in the cup) | `flameDriven = true` (`EffectLights.cs:47`), only branch `:611` | docs/emissive-lights.md:287 keeps a comparison table against "the old lights" | small |
| O6 | `-ultrart-albedo-emission-glow` (`Plugin.cs:314`, `SceneProxies.Sync.cs:27`, `SceneProxies.cs:1367-1384`) | new behaviour default; flag restores "the old albedo emission" | docs/scene-proxies.md:371 | ~10 |
| O7 | `plugin._noFogSkip=0` (`Plugin.cs:824-825, 1579`) | true; bench only | "before 2026-11-18" behaviour | ~3 |
| O8 | Diagnostics whose question is answered: `Plugin.MsaaProbe.cs` (81, polled every `OnUpdate` at `Plugin.cs:429`, returns at once), `Plugin.EnemySurvey.cs` (80, `-ultrart-enemy-survey`; the enemy-glow work it fed has shipped) | off | MSAA probe: keep until the AA decision (docs/aliasing.md) is made, then remove. Enemy survey: remove if `Plugin.EnemyGlow.cs` no longer needs its output | ~160 |
| O9 | Undocumented early-days flags and old-look switches (list in section 7) | off; flag only | in no script, suite or doc (the first group); one doc each (the second) | ~5 each |

## 6. Structure (PR 5+)

| Id | Finding | Proposal | Effort |
|---|---|---|---|
| R1 | `Trace()` is one ~400-line method (`Plugin.cs:1199-1598`) mixing CPU scene sync (~20 calls: `RegisterSpawns` ... `UpdateDebrisGlow`), ~40 global `SetRayTracing*Param` binds, dispatch, denoise and post | split into named stages (`SyncScene`, `BindGlobals`, `TraceMain`, `Denoise`, `Upsample`, `Post`) in place: no behaviour change, makes the order explicit | ~0.5 day |
| R2 | The frame order is a contract held in comments: each feature has 3-5 call sites in `Awake`, `LoadShaders`, `OnUpdate`, `ResetScene` (14 hand-listed resets, `Plugin.cs:511`) and `Trace`; late features read state another stage filled (`_depthCopy` from `BindGameDepth`, `Plugin.cs:970`, read at `Plugin.Volumetrics.cs:126`, `Plugin.Vfx.cs:285`) | a `FramePass` interface (`Load`, `Reset`, `Hotkeys`, `Bind`, `Record`) for Exposure, Vfx, Volumetrics, Stains, Wet, Veils, Transparent, Cutout, Particles; Plugin loops a list. One feature per PR | ~1 week total |
| R3 | `Plugin` is a god object: ~457 instance fields over ~45 partials (`Plugin.cs` 104, Viewmodel 47, Cinematics 34, Dlss 24, Hdr 23) | extract, in this order: **`ViewmodelPass`** (Plugin.Viewmodel.cs, 939 lines, all `_vm*` fields, self-contained; ~2 days), **`CinematicsController`** (Cinematics + CineRender + Cinematics.Bench, ~1,700 lines; ~2-3 days), **light authoring** (editor, classifier, fit) apart from the runtime rules (LightOverrides + Handover; ~2 days), **`BenchDriver`** (Bench*, Burst, MotionNoise, ~3,000 lines) last, after R5 | see each |
| R4 | Bench variants write Plugin fields by reflection (`Plugin.Burst.cs:131-139`, `SetField`): a renamed `_field` only logs a warning and the suite silently measures the wrong thing; the analyzer reports 13 such fields as "never assigned" | a `[BenchVariant]` attribute (or a whitelist) checked at load, and fail the scenario on an unknown key | ~0.5 day |
| R5 | Config plumbing over four channels | see section 7 | |
| R6 | Oversized files (behaviour-neutral splits, build only): `SceneProxies.cs` (1,527: material rules 190-260 + 1111-1140, proxy materials 1314-1430, dynamic lifecycle 740-1000, fixtures/glow 1151-1292), `StaticGroups.cs` (1,194: combined mesh 256-490, grouping 569-770, toggles/sync 772-1170), `EmissiveLights.cs` (1,275: mesh source 1144-1257, alias table 841-937, settings 14-66), `LightShafts.cs` (981: matching 226-523, hull fit 524-920) | split into partial files along those lines | ~0.5 day each |
| R7 | Hard-coded level literal: `Plugin.LightClassifier.cs:169` `scene.Contains("7-3") \|\| scene.Contains("P-2")` tags "taste" lights | a field in `data/lights/Level 7-3.json` / `Level P-2.json` or a rule in `_rules.json`. The only per-level branch left in C#: per-level behaviour is otherwise data-driven | trivial |

## 7. Config plumbing

**Inventory.** A setting reaches the plugin through up to five channels:

| Channel | Count | Where |
|---|---|---|
| Settings menu entries (`m.Bool/Float/Choice`) | 103 (72 in `SettingsMenu.cs`, 19 in `Plugin.Cinematics.cs:143-164`, 12 in `Plugin.LightOverrides.cs:47-59`); 17 of them `.Legacy()` (bound by older parse code), 1 `.SessionOnly()` | `Setting<T>` in `SettingsMenu.cs` already binds config, applies "a flag beats the config" and syncs per frame |
| Literal `Config.Bind` calls outside that registry | 30 (Materials 11, Hdr 10, Plugin.cs 2, Dlss, Exposure, Cutout, Particles, Volumetrics, Viewmodel 2) | the legacy path |
| `-ultrart-*` flags | 295 distinct literals, 285 `case` arms, in 23 hand-written `Parse*Args` functions (31 `GetCommandLineArgs` calls; `NumberStyles.Float` typed 48 times) | each parser re-declares its own `Next()` / `F()` / `On()` |
| Bench scenario fields | ~110 over 9 classes in `Bench/BenchModel.cs` | Newtonsoft, unknown keys silently dropped |
| Bench variant keys (`mode+key=value`) | 245 distinct keys in suites, none declared: reflection over 22 settings objects plus any `Plugin` field (`Plugin.Burst.cs:55-77, 131-139`) | see R4 |

A new-style setting costs 4-5 lines (field, flag `case`, one menu line; the variant is free) but names its flag twice
with nothing tying the two together. A legacy one costs 7-10 lines and names the setting in three places and its range
in two. Nothing is dead: every `Config.Bind` value and every flag's field is read.

**Defects found on the way (PR 1, one line each):**

| Id | Location | Finding |
|---|---|---|
| P1 | `Plugin.cs:280-283` | `-ultrart-lights` maps any value other than `ref` / `ris` to ReSTIR (`default:`), so a typo silently selects ReSTIR; RIS is the default. Reject unknown values (and fix the same sketch in docs/light-sampling.md:309, D10) |
| P2 | `bench/suites/ledge-dashes.json:17, 36` | variant `plugin._edgeMatch=0`: no such field exists any more; the bench only logs "no Plugin field" and the A/B measures nothing. Drop the key (or the suite) |
| P3 | `Plugin.Hdr.cs:155-157` vs `SettingsMenu.cs:203-205` | HDR emissive / lava / sky are clamped to 1..4 by the parser but ranged 1..3 in the menu: one setting, two validations |
| P4 | `Plugin.cs:118`, `Plugin.Hdr.cs:106` | `[View] ConfigVersion` is bound twice with a migration counter in each place; `benchLaunch` is derived at `Plugin.cs:120`, `Plugin.Hdr.cs:107` and `Plugin.cs:141`, and the bench flag is parsed again at `Plugin.Bench.cs:61`. One `BenchLaunch` property and one migration list |
| P5 | `AutoExposure.cs:134`, `Plugin.Wet.cs:43` vs `Plugin.Materials.cs:50`; `Plugin.Hdr.cs` `Next()` | flag parsers disagree: `On()` accepts `off/0/false` in two parsers and only `off` in a third; the HDR `Next()` refuses a value starting with `-`, the others accept it (`-ultrart-hdr-sky -1` vs `-ultrart-ae-key -1`) |

**Undocumented early-days flags** (owner decision, O9): in `src` only, in no script, suite or doc:
`-ultrart-ambient`, `-ultrart-gi` (`Plugin.cs:299-304`, the primary ambient 0.5 / GI strength 0.35 of the first
hybrid), `-ultrart-shadowbias`, `-ultrart-light-radius` (`Plugin.cs:249, 298`), `-ultrart-inject` (`:302`),
`-ultrart-flipy`, `-ultrart-nodepthmatch` (`:241, 265`), `-ultrart-noscene`, `-ultrart-nostatic`, `-ultrart-minimal`,
`-ultrart-dumpverts` (C2), `-ultrart-dn-mover` (`Denoiser.cs:788`). Old-look switches with no suite use, one doc each:
`-ultrart-lit-unlit`, `-ultrart-nomotion`, `-ultrart-nomatsync`, `-ultrart-noorbemitters`,
`-ultrart-albedo-emission-glow` (O6), `-ultrart-rr-nowarm`; bench-only `plugin._aeBenchClock` ("the old behaviour",
no doc). Each is a few lines; document or drop.

**Bench model fields no suite sets** (read in code, so defaults nobody overrides, not dead): `Scenario.Seed`, `Settle`,
`LightFitLights`, `MotionStride`, `MotionSettle`, `MotionJitterRotate`, `MotionExclude`; `TourSpec.Settle`,
`TourStop.Yaws`, `TourStop.LoadSeconds`; `CinematicSpec.ShowPlayer`, `ShotKinds`; `CineTake.Bounces`;
`PathSpec.FastTimeScale`. Keep; optionally set `MissingMemberHandling.Error` (or warn) on suite load so a typo in a
suite key stops being silent.

**R5, table-driven settings (design).** Extend the existing `Setting<T>` so one declaration feeds the config bind, the
menu, the flag and the Keybinds page:

```csharp
S.Float("Display", "HDRSky", "Sky (x its SDR brightness)", 1f, 3f, 2, () => _hdrSky, v => _hdrSky = v)
 .Flag("hdr-sky").Key(null);
```

One loop over the command line applies every setting whose flag matches, with one parser per type, which replaces the
23 `Parse*Args` frameworks; ranges and clamps live in one place; `.Key("F2")` generates the Keybinds rows (D9). Bench
variants keep their reflection (with R4's whitelist). About 20 of the 285 flags take non-scalar values
(`-ultrart-ae-match a,b`, `-ultrart-dn-*` presets, `-ultrart-lights`) and keep small custom parsers; renamed legacy keys
need a `.Migration()` hook. Estimate: **~450-500 lines saved** (~280 parser scaffolding, ~120 `Config.Bind` blocks and
migrations, ~40 for the `.Legacy()` path, ~30 Keybinds), and a new setting becomes one line. Behaviour-neutral by
intent, but it touches every setting's load path, so verify with a config written by an older build, a run with flags
(`-Extra`), and the menu. ~2-3 days; do it after PR 3 and before extracting `BenchDriver` (R3).

## 8. Looked at and kept

So nobody re-audits these:

- **Live diagnostics** (documented, gated, cheap when off): `Plugin.RayCounts.cs` + `PathTraceCount.raytrace`
  (docs/performance.md), `Plugin.MotionNoise.cs` (bench/README "Motion"), `Plugin.LightFit.cs` (produces `data/lights`),
  `Plugin.LightClassifier.cs`, `ParticleSurvey.cs` / `EffectSurvey.cs` (docs/particles.md, emissive-lights.md),
  `SceneProxies.Probe.cs` (`-ultrart-viewprobe`), and the reflection-set bench fields the analyzer flags as "never
  assigned": `_cutoutDebug`, `_rrGuard`, `_editorBenchShow`, `_motionJitterRotate`, `_msaaProbe`, `_sunsUnshadowed`,
  `_particlesOff`, `_matchDebug`, `_rayCounts`, `_diagDispatches`, `_diagNoHit` (each used by a suite and named in a doc).
- **Not duplicates:** `LightClasses` (policy by class, `_rules.json`) vs `LightOverrides` (per-light data and area
  lights); `ultrart-materials.json` (rules for `MaterialClassifier`, step 5); EffectLights / EmissiveLights / LitParticles
  / ParticleLighting (four different jobs sharing one particle-tint analysis); the tone curve (one HLSL implementation).
- **Live, despite the name:** `Plugin.Handover.cs` (the "emissive over lights" runtime feature), `TemporalUpsampler`
  (the default upsampler outside RR and RR's remodulation), `Upsample.compute`, `Reference` light sampling (ground truth
  for `-ultrart-accumulate`), `UltraRT.Preload` (the HDR preloader), the debug views.
- **Shaders:** every `#pragma kernel`, raygen and shader file is loaded from C#; there is no `multi_compile` /
  `shader_feature` (variants are `#define`s per file); no cbuffer field is dead. The C#-set names no shader reads
  (`_DitherStrength`, `_VertexMode`, `_SrcBlend`, `_DstBlend`, `_ZWrite`) are game-material properties.
- **Native:** every other `UltraRT_*` export has a caller; `HdrTest.cpp` / `LocatorTest.cpp` build only with
  `build.ps1 -Test` and are documented (docs/hdr.md, docs/gpu-timer.md).
- **data/lights:** every key in every level file is parsed by `LightOverrides.cs` / `LightClasses.cs`; `proposed/_rules.json`
  is the documented empty proposal layer (`lc.layer=proposed`).
- **Scripts/tools:** the trailer scripts, etw scripts, `tools/perf/*` (EtwProfile is used by `etw_report.py`),
  `tools/lights/tour_suites.py`, `score.py`, `board.py` are referenced and used. Judgement calls, not proposed:
  `scripts/bench-stats.ps1` and `scripts/tests/game-lock-selftest.ps1` (referenced nowhere, but useful: document them),
  `tools/lights/overview.py` + `attribute.py` (~130 lines, one-off analyses cited by docs).
- **Analyzer false positives:** Unity messages (`Awake`, `OnDestroy`, `Update`, `OnEnable`, `OnGUI` on `Plugin`,
  `CoroutineHost`, `ProxyTracker`, `BenchWhiplash`, `BenchLightFade`), Harmony methods (`Patches.*.Prefix/Postfix/
  TargetMethods`), JSON-deserialised `ServerCommand` fields. One real warning outside the audit's scope:
  `EffectGeometry.cs:264` uses the obsolete `ParticleSystemRenderer.BakeMesh(Mesh, Camera, bool)` (CS0618).
