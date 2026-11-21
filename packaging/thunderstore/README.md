# UltraRT

UltraRT adds hardware ray tracing and path tracing to ULTRAKILL. It replaces the game's original lighting with soft shadows, bounce light, reflections, volumetric fog, and dynamic combat lights, while keeping the pixel-art textures sharp.

Version {{VERSION}}.

## What it adds

- **Path tracing and ray tracing:** soft shadows from every light, multi-bounce global illumination, glowing surfaces and lava that emit real light, and sky lighting.
- **Reflections and portals:** ray-traced reflections on metal, glass, water, terminal screens, and the mirrors in Layer 8. You can see V1, V1's glowing wings, and your held weapon (including its live glow and custom colors) in mirrors and glass. Light also shines through portals in Layer 8.
- **Water and stained glass:** traced water caustics, deeper color in deep pools, and ripples that spread out when you move, splash, or trigger explosions. Stained glass casts soft colored light onto floors and under water, and it dims naturally in night levels.
- **Combat, fire, and weather:** dynamic light from muzzle flashes, revolver and railcannon beams, parries, explosions, gasoline fires, and lightning strikes in 5-2 and P-2.
- **Materials and atmosphere:** volumetric light shafts and fog, lit smoke and blood spray, bloom, heat haze over fire and lava, rain puddles and ripples, glossy wet blood on floors, translucent lampshades that glow from their bulb, and subtle red glow inside flesh. Your first-person gun and arms are lit by the ray tracer too.
- **Hand-tuned levels and tools:** custom lighting across 28 levels (including full support for 7-4), an in-game editor for lights, and a free camera that can render offline videos.
- **HDR10 output (experimental, off by default):** native HDR10 output in floating point for HDR monitors, with a smooth highlight curve that follows your Windows SDR brightness.

## Requirements

- **A GPU that supports DirectX 12 ray tracing (DXR)** with a current driver. UltraRT was built and tested on an NVIDIA RTX 5070 Ti. It should also work on AMD RX 6000+, Intel Arc, and older DXR cards, though those have not been tested yet. If your GPU does not support DXR, UltraRT shows a notice and leaves the game untouched.
- **NVIDIA DLSS Ray Reconstruction** gives the cleanest image in path tracing and requires an NVIDIA RTX GPU.{{DLSS_REQUIREMENT}} Other GPUs use UltraRT's own denoiser.
- **DirectX 12 launch options.** ULTRAKILL defaults to DirectX 11, which does not support ray tracing. You must add these launch options:

  ```
  -force-d3d12 -force-gfx-jobs legacy
  ```

  Without `-force-d3d12`, UltraRT shows a notice in game and stays off. The `-force-gfx-jobs legacy` flag avoids a Unity DirectX 12 freeze when a large level such as 8-3 unloads.
- **BepInEx** and **PluginConfigurator** (for the in-game settings menu). Mod managers install both automatically.

## How to install

### Using r2modman or Thunderstore Mod Manager

1. Search for **UltraRT** and install it. Its dependencies will install with it.
2. Open **Settings > Debugging > Set launch parameters**, enter `-force-d3d12 -force-gfx-jobs legacy`, and click **Update launch parameters**. Leave your Steam launch options for ULTRAKILL empty so the two do not conflict.
3. Launch the game with **Start modded**.

### Manual install (with BepInEx 5 already set up)

1. Copy the `plugins` folder from this zip into `BepInEx/plugins/UltraRT/`, and copy the `patchers` folder into `BepInEx/patchers/UltraRT/`.
2. In Steam, right-click **ULTRAKILL > Properties > General > Launch Options** and enter `-force-d3d12 -force-gfx-jobs legacy`.
3. Optional: install **PluginConfigurator** if you want the in-game settings menu.

To confirm that everything loaded, check `BepInEx/LogOutput.log` for `UltraRT {{VERSION}}` and `API=Direct3D12 supportsRayTracing=True`.

## Settings and view modes

Pause the game and open **Options > Plugin Config > UltraRT**. The main page covers the view mode, resolution, denoiser, brightness, HDR output, and major effects. Everything else is under **Advanced settings**. Changes apply right away and are saved to `BepInEx/config/ooo.lew.ultrart.cfg`.

You can choose between three view modes:

- **pathtrace** (default on RTX GPUs with DLSS Ray Reconstruction): full multi-bounce path tracing.
- **relight** (default on GTX, AMD, Intel, and setups without DLSS Ray Reconstruction): faster ray tracing with one bounce of global illumination from a radiance cache.
- **pt-dn**: ray tracing that keeps the game's original lighting feel and falloff.

## Hotkeys

| Key | Action |
|---|---|
| Page Up / Page Down | Switch between pathtrace, relight, and pt-dn |
| F7 | Toggle ray tracing on or off to compare with vanilla |
| F4 / Ctrl+F4 | Toggle reflections / toggle auto exposure |
| F5 / Ctrl+F5 | Toggle glowing surfaces as lights / toggle sky light |
| Ctrl+F6 | Toggle volumetric light and fog |
| Ctrl+F2 / Shift+F2 | Toggle screen effects / toggle bloom |
| Ctrl+F7 / Shift+F7 | Toggle ray tracing on your gun / toggle lit particles |
| Ctrl+F1 | Toggle detail maps (requires the optional detail-map pack) |
| Ctrl+F8 | Toggle DLSS Ray Reconstruction in relight and pt-dn |
| Insert, then numpad | Open the light editor (`5` select, `4`/`6` cycle, `8`/`2` pick value, `+`/`-` adjust, `Enter` save) |
| Home | Toggle the free camera (`K` add key, `P` play path, `Ctrl+S` save, `End` render offline) |

Hotkeys only last for your current session; use the settings menu to save changes. All hotkeys turn off automatically while the console (`F8`) or a text box is open. You can see the full list under **Keybinds** in the settings menu. Debug views and developer keys (`F2`, `F3`, `F6`, `F9`, `F10`, `F11`) stay off unless you enable them under **Advanced settings > View**.

## Optional detail maps

If you want extra surface detail on bricks, stone, and metal, install the companion package **UltraRT_DetailMaps**. It adds subtle normal and roughness maps for the game's pixel-art textures. UltraRT finds the pack automatically once it is installed, and you can toggle it in game with **Ctrl+F1**. Without it, everything still works normally using smooth material presets.

## Known issues

- **Performance:** Ray tracing is heavy. At 2560x1440 on an RTX 5070 Ti, most levels run at 4.5–7 ms per frame in both `relight` and `pathtrace`. The portal levels in Layer 8 are CPU-bound on the render thread and run around 12.5–16.5 ms. If your frame rate is low, lower **Trace resolution** in the settings menu.
- **First-time shader freeze:** The first time that you switch to `pathtrace`, the game can freeze for up to 30 seconds while your GPU driver compiles the path-tracing shader. This only happens once after a driver update or a new UltraRT version. GeForce GTX cards start in `relight` automatically to avoid this pause.
- **HDR10 output:** Experimental and off by default. Turn on Windows HDR for your monitor, enable HDR in the UltraRT settings menu, and restart the game. In-game screenshots still capture in SDR, and full-screen flashes or vignettes drop to SDR brightness while they cover the screen.
- **4-S:** This secret level uses a separate third-person camera that UltraRT does not attach to yet, so it shows the original game visuals.
- **Gunshot flash tail in `relight`:** In `relight`, the flash from a gunshot can linger for about 0.15 seconds. `pathtrace` stays crisp.
- **DirectX 11:** Without `-force-d3d12`, UltraRT stays off and shows a notice explaining what to add.

## Where files go

UltraRT keeps its code, shaders, material rules, and custom lights for each level inside its own plugin folder.{{DLSS_FILES}} The HDR preloader goes into the `patchers` folder. Any light edits that you save, along with logs and offline renders, go into `BepInEx/ultrart/` (inside your mod-manager profile if you use r2modman).

## Credits and licences

- **UltraRT:** {{LICENSE_LINE}}
- **ULTRAKILL** by Arsi "Hakita" Patala, published by New Blood Interactive. UltraRT is an unofficial mod and is not affiliated with or endorsed by them.
- **BepInEx** (LGPL-2.1) and **HarmonyX** (MIT), the mod loader.
- **PluginConfigurator** by EternalsTeam, for the in-game settings page.
- **NVIDIA DLSS Ray Reconstruction**, integrated through the NVIDIA DLSS SDK. NVIDIA and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and other countries. UltraRT is not sponsored or endorsed by NVIDIA.{{DLSS_CREDITS}}
- **PBRify Remix** by Kim2091 (CC0), which was used to create the optional detail maps.
