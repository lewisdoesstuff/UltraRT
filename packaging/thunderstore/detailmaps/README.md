# UltraRT Detail Maps

This optional pack adds normal and roughness detail for {{COUNT}} textures in [UltraRT](https://thunderstore.io/c/ultrakill/p/{{TEAM}}/UltraRT/). Bricks, stone, and metal panels catch ray-traced light and reflections along their pixel-art edges. The effect is subtle, and UltraRT works fine without it.

Version {{VERSION}}.

## How to install

Install this pack alongside UltraRT using r2modman or Thunderstore Mod Manager. You do not need to configure anything: UltraRT detects the pack automatically when a level loads. You can confirm that it loaded by checking `BepInEx/LogOutput.log` for `Material maps (ai): package UltraRT_DetailMaps`. Press **Ctrl+F1** in game to toggle the maps on or off, or adjust their strength under **Advanced settings > Effects**.

If you generated your own detail maps with UltraRT's `tools/materials` tool (saved in `BepInEx/ultrart/materials/ai`), UltraRT uses those instead of this pack.

## What is inside

The pack contains `plugins/ultrart-detailmaps.json` (which lets UltraRT locate it) and `plugins/ai/`, which holds `manifest.json` and one `<texture>@<w>x<h>_d.png` file for each texture (normal data in red and green, roughness in blue). These maps were generated from ULTRAKILL's textures with PBRify Remix, and they do not contain any original game textures.

## Credits

- **PBRify Remix** by Kim2091 (`1x-PBRify_NormalV3`, `1x-PBRify_RoughnessV2`, CC0).
- **ULTRAKILL** by Arsi "Hakita" Patala, published by New Blood Interactive. This is an unofficial mod and is not affiliated with or endorsed by them.
