# UltraRT

Ray tracing and path tracing for ULTRAKILL. UltraRT replaces the game's lighting with hardware ray tracing: soft shadows from every light, bounce light, ray-traced reflections (water, metal, glass, the 8-x mirrors), volumetric light and lit smoke, and a full path-traced mode. The result is denoised so the game's pixel-art textures stay sharp.

Version {{VERSION}}.

## Requirements

- **A GPU with DirectX 12 ray tracing (DXR)** and a current driver: NVIDIA RTX 20 series or newer, AMD RX 6000 or newer, Intel Arc. UltraRT was developed on an RTX 5070 Ti. On a GPU without DXR the mod stays off and the game runs as normal.
- **DLSS Ray Reconstruction** (the best-looking denoiser for path tracing) needs an NVIDIA RTX GPU.{{DLSS_REQUIREMENT}} Other GPUs use UltraRT's own denoiser.
- **The game must run on DirectX 12.** Add these launch options:

  ```
  -force-d3d12 -force-gfx-jobs legacy
  ```

  Without `-force-d3d12` ULTRAKILL runs on DirectX 11, where ray tracing doesn't exist; UltraRT then shows a message in game and stays off. `-force-gfx-jobs legacy` avoids a Unity DirectX 12 freeze when a large level (8-3) unloads.
- BepInEx and PluginConfigurator (the settings page). The mod manager installs both with UltraRT.

## Install

**r2modman or Thunderstore Mod Manager**

1. Search for UltraRT and install it. The dependencies come with it.
2. Set the launch options: **Settings > Debugging > Set launch parameters**, enter `-force-d3d12 -force-gfx-jobs legacy`, and click **Update launch parameters**. The mod manager starts the game through Steam with them. Leave the launch options in Steam's game properties empty, or put the same options in only one of the two places.
3. Start the game with **Start modded**.

**By hand** (BepInEx 5 already installed in the game folder)

1. Copy the package's `plugins` folder to `BepInEx/plugins/UltraRT/` and its `patchers` folder to `BepInEx/patchers/UltraRT/`.
2. Steam: right-click ULTRAKILL > Properties > General > Launch Options: `-force-d3d12 -force-gfx-jobs legacy`.
3. Optional: install PluginConfigurator for the settings page.

To check that it works, look in `BepInEx/LogOutput.log` for `UltraRT {{VERSION}}` and `API=Direct3D12 supportsRayTracing=True`.

## Settings

Pause, **Options > Plugin Config > UltraRT**. The main page has the view mode, trace resolution, the path-tracing denoiser, brightness, HDR output and the main effect switches; **Advanced settings** has everything else. Changes apply at once and are saved to `BepInEx/config/ooo.lew.ultrart.cfg`.

View modes:
- **pathtrace** (the default with DLSS Ray Reconstruction): multi-bounce path tracing.
- **relight** (the default without it): full ray tracing with one bounce of light from a cache. Faster.
- **pt-dn**: ray traced with the game's own light model.

## Hotkeys

| Key | Action |
|---|---|
| Page Up / Page Down | previous / next view mode |
| F7 | ray tracing on / off (compare with the game's own image) |
| F4, Ctrl+F4 | reflections; auto exposure |
| Ctrl+F6 | volumetric light and fog |
| Ctrl+F1 | surface detail maps (needs the optional detail-map package) |
| Ctrl+F8 | DLSS Ray Reconstruction in relight and pt-dn |
| Insert, then numpad | light editor (5 select the light at the crosshair, 4 / 6 cycle, 8 / 2 parameter, + / - change, Enter save) |
| Home | free camera; then K add a key, P play the path, Ctrl+S save, End render it offline |

Hotkeys change things for the session only; the settings page saves them. They are ignored while the console (F8) is open. The settings page's **Keybinds** page lists all keys. The debug views and the developer keys (F2, F3, F6, F9, F10, F11) are off unless turned on under **Advanced settings > View**.

## Optional: surface detail maps

Per-texel normal and roughness detail for the game's textures comes in a separate package, **UltraRT_DetailMaps**, if it is available. Without it, surfaces are lit from their flat geometry. UltraRT finds the package by itself; the log says which maps it used (`Material maps (ai): package ...`).

## Known issues

- **Performance.** Ray tracing is expensive. At 2560x1440 on an RTX 5070 Ti, most rooms run at 4.5-7 ms per frame (relight and pathtrace). The 8-x portal levels are limited by the CPU (render thread), around 13-17 ms per frame. Lower **Trace resolution** on slower GPUs.
- **The first switch to pathtrace can freeze the game for up to about 30 s** while the GPU driver compiles the path-tracing shaders. It happens once per driver or UltraRT update.
- **HDR output** is a prototype, off by default. It needs Windows HDR on and a game restart after switching it on.
- **On DirectX 11** (missing launch options) UltraRT does nothing except show a message.
- The light editor writes your edits to `BepInEx/ultrart/lights/`. In r2modman that folder is in the profile's BepInEx folder.

## Files

UltraRT keeps its own files in its plugin folder: the plugin, `UltraRT.Native.dll` (GPU timer, DLSS, HDR output), the shader bundle `ultrart.shaders`, material rules, and per-level light overrides in `lights/`.{{DLSS_FILES}} The HDR preloader is in the patchers folder. Your data (light edits, caches, logs) goes to `BepInEx/ultrart/`.

## Credits and licences

- **UltraRT**: {{LICENSE_LINE}}
- **ULTRAKILL** by Arsi "Hakita" Patala, published by New Blood Interactive. UltraRT is an unofficial mod and is not affiliated with or endorsed by them.
- **BepInEx** (LGPL-2.1) and **HarmonyX** (MIT): the mod loader.
- **PluginConfigurator** by EternalsTeam: the settings page.
- **NVIDIA DLSS Ray Reconstruction**: denoising and upscaling in path tracing, through the NVIDIA DLSS SDK. NVIDIA and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and other countries. UltraRT is not sponsored or endorsed by NVIDIA.{{DLSS_CREDITS}}
- **PBRify Remix** by Kim2091 (CC0): the networks behind the optional detail maps.
