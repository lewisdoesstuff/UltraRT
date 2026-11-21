# UltraRT

UltraRT adds hardware ray tracing and path tracing to ULTRAKILL. It traces shadows, bounce light, reflections, and volumetric fog in real time while keeping the pixel-art textures sharp. NVIDIA DLSS Ray Reconstruction is used for denoising if supported, or UltraRT's own denoiser if not. HDR10 output is supported on compatible displays.

Version {{VERSION}}.

## Features

- **Path tracing and ray tracing:** soft shadows, multi-bounce global illumination, glowing surfaces and lava that cast real light, sky lighting, and auto exposure.
- **Reflections, glass, and water:** ray-traced reflections on metal, glass, water, and mirrors (showing V1, wings, and your held weapon), stained-glass colored light, water caustics, and interactive ripples.
- **Dynamic combat and weather:** real-time light from gunshots, weapon beams, parries, explosions, fires, torches, and lightning.
- **Atmosphere and effects:** volumetric light shafts and fog, lit smoke and blood spray, bloom, heat haze, rain puddles, glossy wet blood on floors, and ray-traced lighting on your gun and arms.
- **HDR10 output:** native HDR10 output for compatible monitors (off by default).
- **In-game tools:** a settings menu, a light editor, and a free camera with offline video rendering.

## Requirements

- **A DirectX 12 ray-tracing (DXR) GPU** with an up-to-date driver. UltraRT was built and tested on an NVIDIA RTX 5070 Ti; other DXR cards should work using UltraRT's own denoiser, though they have not been tested yet.
- **NVIDIA DLSS Ray Reconstruction** gives the cleanest image in path tracing and requires an NVIDIA RTX GPU.{{DLSS_REQUIREMENT}} Other GPUs use UltraRT's built-in denoiser.
- **DirectX 12 launch options.** ULTRAKILL defaults to DirectX 11, which does not support ray tracing. Add these launch options:

  ```
  -force-d3d12 -force-gfx-jobs legacy
  ```

  Without `-force-d3d12`, UltraRT shows a notice and stays off. `-force-gfx-jobs legacy` avoids a Unity DirectX 12 freeze when large levels unload.
- **BepInEx** and **PluginConfigurator** (installed automatically by mod managers).

## How to install

### r2modman or Thunderstore Mod Manager

1. Search for **UltraRT** and install it.
2. Open **Settings > Debugging > Set launch parameters**, enter `-force-d3d12 -force-gfx-jobs legacy`, and click **Update launch parameters**. Leave Steam's own launch options for ULTRAKILL empty.
3. Click **Start modded**.

### Manual install (with BepInEx 5)

1. Copy `plugins` into `BepInEx/plugins/UltraRT/` and `patchers` into `BepInEx/patchers/UltraRT/`.
2. In Steam, open **ULTRAKILL > Properties > General > Launch Options** and enter `-force-d3d12 -force-gfx-jobs legacy`.
3. Install **PluginConfigurator** if you want the in-game settings menu.

## View modes and controls

Pause and open **Options > Plugin Config > UltraRT** to adjust and save settings.

- **`pathtrace`** (default on NVIDIA RTX GPUs with DLSS Ray Reconstruction): full multi-bounce path tracing.
- **`relight`** (default on other GPUs): faster ray tracing with one bounce of global illumination.
- **`pt-dn`**: ray tracing that keeps ULTRAKILL's original light falloff and flat ambient feel.

| Key | Action |
|---|---|
| Page Up / Page Down | Switch view mode (`pathtrace`, `relight`, `pt-dn`) |
| F7 | Toggle ray tracing on or off |
| Insert | Open the light editor (numpad to adjust, `Enter` to save) |
| Home | Toggle the free camera (`K` add key, `P` play, `Ctrl+S` save, `End` render offline) |

Individual effects also have hotkeys (`F4` reflections, `F5` glowing surfaces, `Ctrl+F6` volumetrics, `Ctrl+F1` detail maps, and more; see **Keybinds** in the settings menu). Hotkeys only apply to the current session and pause while the console (`F8`) or a text box is open.

## Performance

Measured on an RTX 5070 Ti at 2560x1440 on DirectX 12 with default settings:

| Scene | Stock (RT off) | `relight` | `pathtrace` |
|---|---|---|---|
| Typical levels | 280–460+ fps (2.2–3.6 ms) | 133–187 fps (5.3–7.5 ms) | 136–190 fps (5.3–7.4 ms) |
| Portal-heavy levels | 115–189 fps (5.3–8.7 ms) | 103–109 fps (9.2–9.7 ms) | 101–116 fps (8.6–9.9 ms) |
| Heavy combat | 170–255 fps (3.9–5.9 ms) | 81–102 fps (9.8–12.4 ms) | 87–108 fps (9.2–11.5 ms) |

If your frame rate is low, switch to a faster **Quality preset** (`Ultra`, `High`, `Performance`, or `Competitive`) or lower **Trace resolution** in the settings menu.

## Optional detail maps

Install **UltraRT_DetailMaps** if you want subtle normal and roughness detail on bricks, stone, and metal. UltraRT detects it automatically, and you can toggle it with **Ctrl+F1**.

## Known issues

- **First-time shader pause:** Switching to `pathtrace` for the first time can freeze the game for up to 30 seconds while your GPU driver compiles the shader. This happens once per driver or mod update.
- **HDR10 output:** Off by default. Enable Windows HDR, turn on HDR in UltraRT's settings menu, and restart the game. In-game screenshots capture in SDR.
- **Secret level 4-S:** Uses a separate third-person camera that UltraRT does not attach to yet, so it displays vanilla visuals.

## Where files go

UltraRT keeps its code, shaders, material rules, and custom level lights inside its plugin folder, and its HDR preloader in `patchers`.{{DLSS_FILES}} Saved light edits, logs, and offline renders go into `BepInEx/ultrart/`.

## Credits and licences

- **UltraRT:** {{LICENSE_LINE}}
- **ULTRAKILL** by Arsi "Hakita" Patala, published by New Blood Interactive. UltraRT is an unofficial mod and is not affiliated with or endorsed by them.
- **BepInEx** (LGPL-2.1) and **HarmonyX** (MIT).
- **PluginConfigurator** by EternalsTeam, for the in-game settings menu.
- **NVIDIA DLSS Ray Reconstruction**, integrated through the NVIDIA DLSS SDK. NVIDIA and DLSS are trademarks and/or registered trademarks of NVIDIA Corporation in the U.S. and other countries. UltraRT is not sponsored or endorsed by NVIDIA.{{DLSS_CREDITS}}
- **PBRify Remix** by Kim2091 (CC0), used to create the optional detail maps.

## AI Disclosure

UltraRT's code was written almost entirely by agents, and would not have been possible without them.

The optional detail maps were generated by PBRify Remix, an image generation model trained on public domain textures.
