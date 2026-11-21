# Changelog

## 0.3.0

First Thunderstore release.

- Added three ray-traced view modes: full multi-bounce path tracing (`pathtrace`), faster ray-traced lighting with single-bounce global illumination (`relight`), and a vanilla-style ray-traced mode (`pt-dn`).
- Included NVIDIA DLSS Ray Reconstruction for RTX GPUs, along with UltraRT's own denoiser and remodulation so that pixel-art textures stay sharp. GeForce GTX cards start in `relight` automatically.
- Added ray-traced reflections for metal, glass, water, terminals, and Layer 8 mirrors. V1, V1's glowing wings, and your held weapon (with its live glow and custom colors) now appear in reflections, and lights shine through Layer 8 portals.
- Added traced water caustics, deeper underwater color absorption, and interactive water ripples from splashes, movement, and explosions.
- Added soft colored light through stained glass (which dims at night and works under water), translucent lampshades that glow from their bulb, and subtle red glow inside flesh.
- Added dynamic combat and weather light for muzzle flashes, revolver and railcannon beams, parries, explosions, player-made gasoline fires, and lightning strikes in 5-2 and P-2.
- Added volumetric light shafts and fog, lit smoke and blood spray, bloom, heat haze over fire and lava, rain puddles and ripples, glossy wet blood on floors, and ray-traced lighting on your first-person gun and arms.
- Shipped hand-tuned lighting across 28 levels (including full support for 7-4 Earthmover), an in-game light editor, and a free camera with offline rendering.
- Added experimental native HDR10 output in floating point (off by default, with a smooth highlight curve that follows Windows SDR brightness).
- Added a two-page settings menu in PluginConfigurator, hotkeys that turn off while the console is open, automatic detection of the optional `UltraRT_DetailMaps` pack, and an in-game notice if DirectX 12 launch options are missing.
