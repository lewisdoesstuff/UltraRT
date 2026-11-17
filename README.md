<img src="assets/icon.svg" width="64" height="64" alt="">

# ultrart

UltraRT is a [BepInEx](https://github.com/BepInEx/BepInEx) mod for [ULTRAKILL](https://store.steampowered.com/app/1229490/ULTRAKILL/) that replaces the game's lighting with hardware ray tracing and path tracing. It runs the game on DirectX 12, builds a ray-tracing acceleration structure of the level every frame, and traces shadows, global illumination, reflections, volumetric light and more through Unity's DXR support, then denoises the result (DLSS Ray Reconstruction or UltraRT's own denoiser) so the game's pixel-art look survives: texels stay sharp, nothing is blurred or recoloured.

Created in [T3 Code](https://t3.codes).

## View modes

Cycle them in game with **Page Up / Page Down**, pick one in the settings menu, or set the startup mode (`[View] DefaultMode` in `BepInEx/config/ooo.lew.ultrart.cfg`). The default is `pathtrace` where DLSS Ray Reconstruction can run (an NVIDIA GPU and `nvngx_dlssd.dll` beside the plugin), `relight` elsewhere. A config written before 2026-11-17 that still holds the old default (`relight`) is moved to `pathtrace` once; choosing `relight` again afterwards sticks.

| Mode | What it is |
|---|---|
| `relight` (default without DLSS RR) | Full ray tracing with a physical light model: inverse-square falloff, soft shadows from every light, sky and sun, one bounce of global illumination from a radiance cache, ray-traced reflections, auto exposure, volumetrics. Calibrated against the game's brightness and colour; see [docs/relight.md](docs/relight.md). |
| `pathtrace` (default with DLSS RR) | Real multi-bounce path tracing with the same look as relight. [DLSS Ray Reconstruction](docs/dlss.md) denoises and upscales it when it is available (NVIDIA RTX GPU, the DLSS SDK runtime fetched by `scripts/fetch-dlss.ps1`); otherwise UltraRT's own denoiser runs at a lower trace resolution with exact-texel remodulation. See [docs/path-tracing.md](docs/path-tracing.md). |
| `hybrid` | The game draws its own image; ray-traced lighting ratios are applied on top of it. |
| `pt-dn` | Ray traced with the game's own light model (its falloff and flat ambient) and the denoiser. |
| debug views | `dn-lighting`, `dn-variance`, `normals`, `albedo`, `depth`, `refl`, `cache`, `motion`, and others at the end of the Page Up / Page Down cycle. |

## Features

- **Ray-traced lighting**: shadow rays per pixel with RIS / ReSTIR light sampling ([docs/light-sampling.md](docs/light-sampling.md)), emissive surfaces as lights ([docs/emissive-lights.md](docs/emissive-lights.md)), lava and acid as emitters ([docs/lava.md](docs/lava.md)), a world-space radiance cache for bounce light ([docs/radiance-cache.md](docs/radiance-cache.md)), and auto exposure.
- **Reflections**: glossy and metal surfaces, water, glass and the layer-8 portal mirrors, with V1 visible in them; fire, explosions and blood stains appear in reflections too ([docs/reflections.md](docs/reflections.md), [docs/portals.md](docs/portals.md)).
- **Scene coverage**: static-batched geometry, skinned enemies (including mirrored statues), the game's vertex displacement (flesh levels, water), scrolling textures with texture motion vectors, alpha-cutout materials, portal-aware levels (8-x), and the first-person gun and arms lit by the ray tracer ([docs/scene-proxies.md](docs/scene-proxies.md), [docs/viewmodel.md](docs/viewmodel.md)).
- **Per-texel materials**: normal and roughness detail for the game's pixel-art textures, generated offline from your own game files, with ray-cone texture LOD (normal-map mips, Toksvig roughness) ([docs/materials.md](docs/materials.md)).
- **Atmosphere**: volumetric light shafts and fog, lamp cones, the 3-1 eye beams as a volumetric channel ([docs/volumetrics.md](docs/volumetrics.md)); lit smoke, dust and blood spray ([docs/particles.md](docs/particles.md)); bloom, heat haze, shockwaves, soft particles, underwater, rain and wet blood ([docs/vfx.md](docs/vfx.md)).
- **Light authoring**: per-level light overrides, area lights, and an in-game light editor (**Insert**) for re-authoring lights that were placed for the game's vertex lighting; statue orbs light their holders ([docs/lighting-overrides.md](docs/lighting-overrides.md)).
- **Settings menu**: view mode, quality, lighting, effects and cinematics in the pause menu through the optional PluginConfigurator mod ([docs/settings.md](docs/settings.md)).
- **Cinematics**: a free camera, saved camera paths with spline playback, and an offline renderer that writes accumulated, anti-aliased path-traced frames at a fixed frame rate and encodes them with ffmpeg ([docs/cinematics.md](docs/cinematics.md)). The trailer tooling builds on it ([docs/trailer.md](docs/trailer.md)).
- **Benchmarks**: a scripted suite runner with saved poses, recorded runs, noise and motion metrics, GPU timings per pass, and a lock that shares the game between agents ([bench/README.md](bench/README.md)).

## Performance

All figures are from the repo's own docs. Measured on an RTX 5070 Ti at 2560x1440, trace scale 0.5, DX12 (2026-11-15, [docs/performance.md](docs/performance.md)): relight averaged 4.5-6.2 ms per frame (161-221 fps) over five static poses (0-1, 0-3, 0-5, 1-4, 6-2), and pathtrace (DLSS RR) 4.9-6.9 ms (144-205 fps). In the 8-2 and 8-3 portal levels the render thread, not the GPU, bounds the frame: relight 12.9 and 16.5 ms there, against 5.1-5.3 and 9.7 ms for the game alone. Numbers move with the scene and the build; the docs say which run each came from.

## Requirements

- Windows, and ULTRAKILL on Steam (the mod is built and tested against its Unity 2022.3.29f1 build, Mono, built-in render pipeline).
- A GPU and driver with DirectX 12 ray tracing (DXR). It was developed on an RTX 5070 Ti. DLSS Ray Reconstruction additionally needs an NVIDIA GPU that supports it; other GPUs use UltraRT's own denoiser.
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) in the game folder (5.4.23.6 was used).
- The game must run on DX12. Add these Steam launch options: `-force-d3d12 -force-gfx-jobs legacy`. Without `-force-d3d12` ray tracing is unavailable and the mod stays idle; the `legacy` graphics-jobs setting avoids a Unity D3D12 deadlock when a large level (8-3) unloads.
- To build: the Unity 2022.3.29f1 editor (the shader bundle is built in batch mode), a .NET SDK, and the Visual Studio C++ build tools for the native DLL (`native/UltraRT.Native`). Optional: [PluginConfigurator](https://thunderstore.io/c/ultrakill/p/EternalsTeam/PluginConfigurator/) for the settings menu, ffmpeg for video output, Python for the material-map and trailer tools.

## Build, install and deploy

```powershell
# The shader bundle (Unity batch mode), the plugin and the native DLL. Without -NoDeploy this also copies
# them into the game folder (ULTRAKILL_DIR sets which one; the default is in scripts\lib\slots.ps1).
.\scripts\build.ps1

# Build into <repo>\stage only (no game folder touched), then copy the stage into an install, in seconds:
.\scripts\build.ps1 -NoDeploy
.\scripts\deploy.ps1 -Slot main        # -Slot alt for a second install; -Owner <name> when a slot is locked

.\scripts\fetch-dlss.ps1               # once: NVIDIA's DLSS SDK parts into lib\dlss (gitignored; accepts NVIDIA's license)
.\scripts\fetch-pluginconfigurator.ps1 # optional: compiles the settings menu in
```

Without `lib\dlss` the native DLL builds as a stub and DLSS Ray Reconstruction is unavailable. A running game keeps `UltraRT.dll` loaded, so a rebuilt plugin needs a relaunch (a rebuilt shader bundle hot-reloads). The per-texel material maps are generated by `tools/materials` from your own game files and are never committed ([tools/materials/README.md](tools/materials/README.md)). Agents and anyone sharing a machine with a running game should read [AGENTS.md](AGENTS.md) and the lock rules in [bench/README.md](bench/README.md) before using `deploy.ps1`.

## Controls

| Key | Action |
|---|---|
| Page Up / Page Down | previous / next view mode |
| F7 | ray tracing on / off |
| F2, Ctrl+F2, Shift+F2 | temporal upsampler; screen effects; bloom |
| F3, Shift+F3, Ctrl+F3 | radiance cache mode; its debug view; denoiser preset |
| F4, Ctrl+F4 | reflections; auto exposure |
| F5, Ctrl+F5 | emissive surfaces as lights; sky light and linear light transport |
| F6, Ctrl+F6 | light sampling (reference, RIS, ReSTIR); volumetrics |
| Ctrl+F1, Ctrl+F7, Shift+F7 | surface detail maps; viewmodel pass; lit particles |
| Ctrl+F8 | DLSS Ray Reconstruction in relight and pt-dn (pathtrace chooses its own) |
| F9 | reload shaders |
| Insert, then numpad | light editor: 5 aim-select, 4 / 6 cycle, 8 / 2 parameter, + / - change, Enter save ([docs/lighting-overrides.md](docs/lighting-overrides.md)) |
| Home | cinematic free camera on / off; then K add key, P play, Ctrl+S save, End render ([docs/cinematics.md](docs/cinematics.md)) |
| F10, F11 | record a camera path; save the current pose (for benchmarks, [bench/README.md](bench/README.md)) |

Hotkeys last for the session. Everything else is in the pause menu (**Options > Plugin Config > UltraRT**, needs PluginConfigurator), saved to `BepInEx/config/ooo.lew.ultrart.cfg`, or set with `-ultrart-*` command-line flags, which win over the config. The menu's **Keybinds** page lists the keys in game. See [docs/settings.md](docs/settings.md).

## Benchmarking

```powershell
.\scripts\bench.ps1                   # the short suite, about a minute
.\scripts\bench.ps1 -Suite full -Perf # frame times only
```

Suites in `bench/suites` play scripted scenarios (static poses, sweeps, recorded runs, cinematic paths) with the modes alternating inside one pass, and write `results.json`, `summary.md` and screenshots to `bench/results` (gitignored). The plugin blocks every save, prefs and Steam-stat write for the whole run. `scripts\game-lock.ps1` queues runs in front of the game installs. All of it, including how poses are loaded and how noise and motion are measured, is in [bench/README.md](bench/README.md); [docs/gpu-timer.md](docs/gpu-timer.md) is the per-pass GPU timer.

## Cinematics and the trailer

**Home** detaches a free camera; **K** adds camera keys, **P** plays the spline, **Ctrl+S** saves the path per level, and **End** renders it offline: path-traced, accumulated, anti-aliased, optionally with motion blur, 16-bit PNGs and an ffmpeg encode. Bench scenarios can render the same paths unattended, with DLSS RR at full resolution, or capture the game's own image and the ray-traced one from the same take. [docs/cinematics.md](docs/cinematics.md) has the keys, settings and sample render times; [docs/trailer.md](docs/trailer.md) covers the trailer pipeline (camera paths, shot plan, `scripts/trailer.py` assembly with wipes, captions in the game's HUD style, and music). Rendered frames, videos and music are never committed.

## Documentation

| Doc | What it covers |
|---|---|
| [docs/settings.md](docs/settings.md) | The in-game settings page: layout, saving, flags versus config, hotkeys |
| [docs/relight.md](docs/relight.md) | The default mode: physical light model, tone curve, auto exposure, sky and sun, colour match |
| [docs/path-tracing.md](docs/path-tracing.md) | The `pathtrace` mode: paths, settings, effects in reflections, cost and noise |
| [docs/pt-plan.md](docs/pt-plan.md) | How the path tracing design was arrived at: experiments and alternatives |
| [docs/dlss.md](docs/dlss.md) | DLSS Ray Reconstruction through NGX: setup, evaluation, license |
| [docs/denoiser.md](docs/denoiser.md) | UltraRT's SVGF-style denoiser, static and motion noise work, temporal upsampling |
| [docs/light-sampling.md](docs/light-sampling.md) | RIS and temporal ReSTIR direct lighting, bias and noise |
| [docs/radiance-cache.md](docs/radiance-cache.md) | The world-space hash-grid cache for multi-bounce diffuse light |
| [docs/emissive-lights.md](docs/emissive-lights.md) | Emissive triangles and effect lights as direct light sources |
| [docs/lighting-overrides.md](docs/lighting-overrides.md) | Per-level light overrides, area lights, the light editor and classifier |
| [docs/reflections.md](docs/reflections.md) | Materials, ray-traced reflections, water and glass, V1 in reflections |
| [docs/materials.md](docs/materials.md) | Per-texel normal and roughness maps, ray-cone LOD, Toksvig roughness |
| [docs/portals.md](docs/portals.md) | Layer 8 portals and mirrors and how rays follow them |
| [docs/scene-proxies.md](docs/scene-proxies.md) | How the scene is mirrored into the RT scene: static batches, skinned meshes, displacement, scrolling textures, statue orbs |
| [docs/volumetrics.md](docs/volumetrics.md) | Light shafts, fog, lamp cones and the 3-1 eye beams |
| [docs/particles.md](docs/particles.md) | Lit smoke, dust, debris and blood spray |
| [docs/vfx.md](docs/vfx.md) | Bloom, heat haze, shockwaves, soft particles, underwater, rain and wet blood |
| [docs/lava.md](docs/lava.md) | Lava and acid as ray-traced emitters |
| [docs/viewmodel.md](docs/viewmodel.md) | The first-person gun and arms, lit by the ray tracer |
| [docs/cinematics.md](docs/cinematics.md) | Free camera, camera paths, offline renderer, bench cinematic scenarios |
| [docs/trailer.md](docs/trailer.md) | Trailer shots, paths, edit lists, graphics and assembly |
| [docs/performance.md](docs/performance.md) | Where the 1440p frame goes and what was done about it |
| [docs/gpu-timer.md](docs/gpu-timer.md) | The native D3D12 GPU timer (`UltraRT.Native`) |
| [docs/ultrakill-rendering.md](docs/ultrakill-rendering.md) | Notes on how ULTRAKILL itself renders |
| [docs/audit-2026-11-15.md](docs/audit-2026-11-15.md) | A visual audit of 33 poses against the game |
| [docs/plan-look-and-perf.md](docs/plan-look-and-perf.md) | Plan of 2026-11-17 (not implemented): light classes and brightness, material maps, trailer flaws, the 120 / 240 Hz targets |
| [bench/README.md](bench/README.md) | Running benchmarks: the game lock, suites, scenario JSON, poses, noise and motion metrics |
| [tools/materials/README.md](tools/materials/README.md) | The offline material-map pipeline |
| [AGENTS.md](AGENTS.md) | Rules for coding agents working in this repo |
