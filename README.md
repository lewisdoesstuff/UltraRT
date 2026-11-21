<img src="assets/icon.svg" width="64" height="64" alt="">

# ultrart

UltraRT is a [BepInEx](https://github.com/BepInEx/BepInEx) mod that adds hardware ray tracing and path tracing to [ULTRAKILL](https://store.steampowered.com/app/1229490/ULTRAKILL/). It runs the game on DirectX 12, builds a ray-tracing scene from the active level every frame, and traces shadows, bounce light, reflections, and volumetric fog through Unity's DXR API. It then denoises the lighting with NVIDIA DLSS Ray Reconstruction or UltraRT's own denoiser so that pixel-art textures stay sharp.

Created in [T3 Code](https://t3.codes).

## View modes

Press **Page Up** or **Page Down** in game to switch modes, pick one in the settings menu, or set `[View] DefaultMode` in `BepInEx/config/ooo.lew.ultrart.cfg`. On NVIDIA RTX GPUs with `nvngx_dlssd.dll` installed, UltraRT starts in `pathtrace`. On GTX, AMD, Intel, or setups without DLSS Ray Reconstruction, it starts in `relight` (see [docs/release.md](docs/release.md)).

| Mode | What it does |
|---|---|
| `pathtrace` (default on RTX with DLSS RR) | Full multi-bounce path tracing. It uses [DLSS Ray Reconstruction](docs/dlss.md) on NVIDIA RTX GPUs when `nvngx_dlssd.dll` is present, or UltraRT's own denoiser with exact-texel remodulation otherwise. See [docs/path-tracing.md](docs/path-tracing.md). |
| `relight` (default without DLSS RR) | Faster ray tracing with soft shadows from every light, sky and sun lighting, one bounce of global illumination from a radiance cache, reflections, auto exposure, and volumetrics. See [docs/relight.md](docs/relight.md). |
| `pt-dn` | Ray tracing that keeps ULTRAKILL's original light falloff and flat ambient feel, paired with the denoiser. |
| debug views | 24 diagnostic views (`dn-lighting`, `normals`, `albedo`, `depth`, `refl`, `cache`, `motion`, and more). They are available in the settings menu and `-ultrart-view`, and they join the Page Up / Page Down cycle when you enable **Advanced > Debug views** (`[View] DebugModes`). |

The old `hybrid` mode and ReSTIR light sampling were removed on 2026-11-19 ([docs/cleanup-audit-2026-11-19.md](docs/cleanup-audit-2026-11-19.md)). Any older config or flag that still asks for `hybrid` falls back to `relight`, and `restir` falls back to RIS.

## Features

- **Lighting and shadows:** soft shadows with RIS light sampling ([docs/light-sampling.md](docs/light-sampling.md)), emissive surfaces that emit area light ([docs/emissive-lights.md](docs/emissive-lights.md)), glowing lava and acid ([docs/lava.md](docs/lava.md)), a world-space radiance cache for bounce light ([docs/radiance-cache.md](docs/radiance-cache.md)), lights that shine through Layer 8 portals ([docs/portals.md](docs/portals.md)), stained glass that casts soft colored light onto floors and under water, translucent lampshades, subtle red glow inside flesh, and auto exposure.
- **Combat and weather lights:** dynamic light from muzzle flashes, revolver and railcannon beams, parries, explosions, player-made gasoline fires, flickering torches, and lightning strikes in 5-2 and P-2.
- **Reflections and water:** ray-traced reflections on metal, glass, water, terminals, and Layer 8 mirrors, with V1, V1's glowing wings, and your held weapon (including live glow and custom colors) visible in them ([docs/reflections.md](docs/reflections.md)). Water also has traced caustics, depth absorption, and interactive ripples from splashes, movement, and explosions ([docs/caustics.md](docs/caustics.md)).
- **Scene coverage:** static-batched geometry, skinned enemies and posing mannequins, vertex displacement on flesh and water, scrolling textures, alpha cutouts, curved-mesh normals, infinite-far-plane camera support for 7-4 Earthmover, and ray-traced lighting on your first-person gun and arms ([docs/scene-proxies.md](docs/scene-proxies.md), [docs/viewmodel.md](docs/viewmodel.md)).
- **Detail maps:** optional normal and roughness maps for ULTRAKILL's pixel-art textures, with ray-cone texture LOD and Toksvig roughness ([docs/materials.md](docs/materials.md)).
- **Atmosphere and effects:** volumetric light shafts and fog, lamp cones, and 3-1's eye beams ([docs/volumetrics.md](docs/volumetrics.md)); lit smoke, dust, and blood spray ([docs/particles.md](docs/particles.md)); bloom, heat haze, shockwaves, underwater grading, rain puddles and ripples, and glossy wet blood on floors ([docs/vfx.md](docs/vfx.md)).
- **HDR10 output (experimental, off by default):** native floating-point HDR10 output with an asymptotic `film` highlight curve and HUD separation at paper white ([docs/hdr.md](docs/hdr.md)).
- **Level lighting and tools:** shipped light overrides across 28 levels plus global class rules, an in-game light editor (**Insert**) and classifier ([docs/lighting-overrides.md](docs/lighting-overrides.md)), a two-page settings menu in PluginConfigurator ([docs/settings.md](docs/settings.md)), a free camera with offline path-traced rendering ([docs/cinematics.md](docs/cinematics.md)), trailer assembly tools ([docs/trailer.md](docs/trailer.md)), and an automated benchmark runner ([bench/README.md](bench/README.md)).

## Performance

Measured on an RTX 5070 Ti at 2560x1440, trace scale 0.5, DirectX 12 ([docs/performance.md](docs/performance.md)):

- Most levels run at 4.5–7 ms per frame in both `relight` and `pathtrace` (144–220 fps).
- The portal levels in Layer 8 (`8-2` and `8-3`) are limited by the CPU render thread and run around 12.5–16.5 ms per frame, compared with 5.1–9.7 ms for the game alone.

## Requirements

- Windows and the Steam build of ULTRAKILL (Unity 2022.3.29f1, Mono, built-in render pipeline).
- A GPU and driver that support DirectX 12 ray tracing (DXR). UltraRT was developed and tested on an RTX 5070 Ti. DLSS Ray Reconstruction requires an NVIDIA RTX GPU; other GPUs use UltraRT's own denoiser.
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) in the game folder (tested with 5.4.23.x).
- DirectX 12 launch options: `-force-d3d12 -force-gfx-jobs legacy`. Without `-force-d3d12`, ray tracing is unavailable and UltraRT stays idle. `-force-gfx-jobs legacy` avoids a Unity DirectX 12 deadlock when 8-3 unloads.
- **To build from source:** Unity 2022.3.29f1 (for batch-building the shader bundle), a .NET SDK, and the Visual Studio C++ build tools for `native/UltraRT.Native`. Optional: [PluginConfigurator](https://thunderstore.io/c/ultrakill/p/EternalsTeam/PluginConfigurator/) for the settings menu, `ffmpeg` for video output, and Python for the detail-map and trailer scripts.

## Build, install, and deploy

Players can install the Thunderstore package via r2modman or Thunderstore Mod Manager. See [docs/release.md](docs/release.md) for how `scripts\package.ps1` builds both `UltraRT` and `UltraRT_DetailMaps`. The mod version lives in `Version.props`.

```powershell
# Build the shader bundle, plugin, preloader, and native DLL, then deploy to the main install:
.\scripts\build.ps1

# Build into <repo>\stage without touching a game install, then deploy in seconds:
.\scripts\build.ps1 -NoDeploy
.\scripts\deploy.ps1 -Slot main        # use -Slot alt for the second install, -Owner <name> while locked

.\scripts\fetch-dlss.ps1               # one-time: fetch NVIDIA's DLSS SDK parts into lib\dlss (gitignored)
.\scripts\fetch-pluginconfigurator.ps1 # optional: compile the PluginConfigurator settings menu in
```

If `lib\dlss` is missing, the native DLL builds as a stub and DLSS Ray Reconstruction is unavailable. A running game keeps `UltraRT.dll` and `UltraRT.Native.dll` loaded, so plugin or native changes require a relaunch, while a rebuilt shader bundle hot-reloads when Developer hotkeys are enabled. Detail maps are generated offline from your own game files with `tools/materials` and are never committed ([tools/materials/README.md](tools/materials/README.md)). Before deploying on a shared machine, check [AGENTS.md](AGENTS.md) and the lock rules in [bench/README.md](bench/README.md).

## Controls

| Key | Action |
|---|---|
| Page Up / Page Down | Cycle `pathtrace`, `relight`, and `pt-dn` (debug views join when **Advanced > Debug views** is on) |
| F7 | Toggle ray tracing on or off |
| Ctrl+F2 / Shift+F2 | Toggle screen effects / toggle bloom |
| F4 / Ctrl+F4 | Toggle reflections / toggle auto exposure |
| F5 / Ctrl+F5 | Toggle emissive lights / toggle sky light and linear light transport |
| Ctrl+F6 | Toggle volumetrics |
| Ctrl+F1 | Toggle surface detail maps |
| Ctrl+F7 / Shift+F7 | Toggle ray-traced gun lighting / toggle lit particles |
| Ctrl+F8 | Toggle DLSS Ray Reconstruction in `relight` and `pt-dn` |
| Insert, then numpad | Light editor (`5` select, `4`/`6` cycle, `8`/`2` parameter, `+`/`-` change, `Enter` save); `Ctrl+Insert` opens the light classifier ([docs/lighting-overrides.md](docs/lighting-overrides.md)) |
| Home | Toggle the free camera (`K` add key, `P` play, `Ctrl+S` save, `End` render offline; [docs/cinematics.md](docs/cinematics.md)) |

Developer keys stay off unless you enable **Advanced > Developer hotkeys** (`[View] DevHotkeys`, `-ultrart-dev-hotkeys`) or run a benchmark launch:

| Key | Action |
|---|---|
| F2 | Toggle temporal upsampler |
| F3 / Shift+F3 / Ctrl+F3 | Radiance cache mode / cache debug view / denoiser preset |
| F6 | Switch light sampling (RIS vs. reference) |
| F9 | Reload shaders (the **Reload shaders** button in the settings menu always works) |
| F10 / F11 | Record a camera path / save the current pose ([bench/README.md](bench/README.md)) |

All hotkeys pause while ULTRAKILL's console (`F8`) or a text field is open. Hotkeys change settings for the current session only. Use **Options > Plugin Config > UltraRT** to save settings to `BepInEx/config/ooo.lew.ultrart.cfg`, or pass `-ultrart-*` command-line flags to override the config. See [docs/settings.md](docs/settings.md).

## Benchmarking

```powershell
.\scripts\bench.ps1                   # short suite, about a minute
.\scripts\bench.ps1 -Suite full -Perf # frame times only
```

Suites in `bench/suites` run scripted poses, sweeps, and recorded routes across modes and write `results.json`, `summary.md`, and screenshots to `bench/results` (gitignored). During a benchmark run, the plugin blocks all save, prefs, and Steam-stat writes. Use `scripts\game-lock.ps1` to queue runs across the `main` and `alt` installs. See [bench/README.md](bench/README.md) and [docs/gpu-timer.md](docs/gpu-timer.md).

## Cinematics and trailer

Press **Home** to detach the free camera, **K** to place keys, **P** to preview the spline, **Ctrl+S** to save the path, and **End** to render an offline path-traced video (16-bit PNGs and an `ffmpeg` encode, with optional motion blur). Benchmark runs can also capture offline or live takes for `scripts/trailer.py`. See [docs/cinematics.md](docs/cinematics.md) and [docs/trailer.md](docs/trailer.md).

## Documentation

| Doc | What it covers |
|---|---|
| [docs/settings.md](docs/settings.md) | Settings menu layout, saving, flags vs. config, hotkeys |
| [docs/release.md](docs/release.md) | Thunderstore packaging (`scripts/package.ps1`), install layout, optional detail-map pack, DX11 notice, licensing |
| [docs/relight.md](docs/relight.md) | `relight` mode: physical light model, tone curve, auto exposure, sky and sun |
| [docs/hdr.md](docs/hdr.md) | Experimental native floating-point HDR10 output, `film` highlight curve, swapchain proxy, and settings |
| [docs/path-tracing.md](docs/path-tracing.md) | `pathtrace` mode: paths, bounces, settings, cost, and noise |
| [docs/dlss.md](docs/dlss.md) | NVIDIA DLSS Ray Reconstruction setup, emission outside RR, and license |
| [docs/denoiser.md](docs/denoiser.md) | UltraRT's SVGF-style denoiser and temporal upsampler |
| [docs/light-sampling.md](docs/light-sampling.md) | RIS direct lighting, bias, and noise |
| [docs/radiance-cache.md](docs/radiance-cache.md) | World-space hash-grid radiance cache for bounce light |
| [docs/emissive-lights.md](docs/emissive-lights.md) | Emissive triangles, combat lights, and effect lights |
| [docs/lighting-overrides.md](docs/lighting-overrides.md) | Per-level light overrides, area and directional lights, fixture handovers, and light editor |
| [docs/reflections.md](docs/reflections.md) | Ray-traced reflections, water, glass, and V1 in reflections |
| [docs/materials.md](docs/materials.md) | Detail maps, ray-cone LOD, lit weights, stained glass, flesh subsurface, translucent lampshades, and curved normals |
| [docs/portals.md](docs/portals.md) | Layer 8 portals, mirrors, and portal scene lights |
| [docs/scene-proxies.md](docs/scene-proxies.md) | Scene mirroring into the RTAS: static batches, skinned meshes, mannequins, displacement, scrolling textures, 7-4 far plane |
| [docs/volumetrics.md](docs/volumetrics.md) | Light shafts, fog, lamp cones, and 3-1 eye beams |
| [docs/particles.md](docs/particles.md) | Lit smoke, dust, debris, and blood spray |
| [docs/vfx.md](docs/vfx.md) | Bloom, heat haze, shockwaves, underwater, rain, and wet blood |
| [docs/lava.md](docs/lava.md) | Lava and acid as ray-traced light sources |
| [docs/caustics.md](docs/caustics.md) | Traced water caustics, refraction, absorption, and interactive ripples |
| [docs/viewmodel.md](docs/viewmodel.md) | Ray-traced lighting on the first-person gun and arms |
| [docs/aliasing.md](docs/aliasing.md) | Aliasing measurements across modes |
| [docs/cinematics.md](docs/cinematics.md) | Free camera, spline paths, and offline renderer |
| [docs/trailer.md](docs/trailer.md) | Trailer shots, paths, assembly, and graphics |
| [docs/performance.md](docs/performance.md) | 1440p frame-time breakdown and optimization history |
| [docs/gpu-timer.md](docs/gpu-timer.md) | Native D3D12 per-pass GPU timer |
| [docs/ultrakill-rendering.md](docs/ultrakill-rendering.md) | Notes on ULTRAKILL's rendering pipeline and dither |
| [docs/cleanup-audit-2026-11-19.md](docs/cleanup-audit-2026-11-19.md) | Static audit for dead code, duplication, and stale suites |
| [docs/archive/](docs/archive) | Older plans, audits, and handoffs kept for history |
| [bench/README.md](bench/README.md) | Benchmark runner, game lock, suites, and metrics |
| [tools/materials/README.md](tools/materials/README.md) | Offline detail-map pipeline |
| [AGENTS.md](AGENTS.md) | Rules for coding agents in this repo |
