# UltraRT Detail Maps

Optional surface detail for [UltraRT](https://thunderstore.io/c/ultrakill/p/{{TEAM}}/UltraRT/) (ray tracing for ULTRAKILL): per-texel normal and roughness maps for {{COUNT}} of the game's textures, so bricks, panels and metal catch the ray-traced light and reflections along their pixel-art detail.

Version {{VERSION}}.

## Install

Install it next to UltraRT (r2modman or Thunderstore Mod Manager do both). Nothing to set up: UltraRT finds the maps by itself when a level loads. `BepInEx/LogOutput.log` says `Material maps (ai): package UltraRT_DetailMaps ...` when they are used. **Ctrl+F1** switches them off and on in game; the strength is under Advanced settings > Effects in UltraRT's settings page.

Maps you generated yourself with UltraRT's `tools/materials` (in `BepInEx/ultrart/materials/ai`) take precedence over this package.

## What is in it

`plugins/ultrart-detailmaps.json` (the marker UltraRT looks for) and `plugins/ai/`: `manifest.json` and one `<texture>@<w>x<h>_d.png` per texture (normal in red and green, roughness in blue). They are derived from ULTRAKILL's textures by the PBRify Remix networks; they contain no game textures.

## Credits

- **PBRify Remix** by Kim2091 (`1x-PBRify_NormalV3`, `1x-PBRify_RoughnessV2`, CC0).
- **ULTRAKILL** by Arsi "Hakita" Patala, published by New Blood Interactive. This is an unofficial mod, not affiliated with or endorsed by them.
