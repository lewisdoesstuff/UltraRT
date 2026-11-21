# Changelog

## 0.3.0

Initial release.

- Three ray-traced view modes: multi-bounce path tracing (`pathtrace`), faster single-bounce ray tracing (`relight`), and a vanilla-style ray-traced mode (`pt-dn`).
- NVIDIA DLSS Ray Reconstruction support on RTX GPUs, plus a built-in denoiser for other DXR GPUs.
- Ray-traced reflections on metal, glass, water, terminals, and mirrors (showing V1, wings, and your held weapon), plus light passing through portals.
- Traced water caustics, depth absorption, interactive ripples, and stained-glass light projection.
- Dynamic light from gunshots, weapon beams, parries, explosions, fires, torches, and lightning.
- Volumetric light shafts and fog, lit smoke and blood spray, bloom, heat haze, rain puddles, glossy wet blood, translucent lampshades, and ray-traced lighting on your gun and arms.
- Experimental native HDR10 output (off by default).
- In-game settings menu in PluginConfigurator, light editor, free camera with offline video rendering, and optional `UltraRT_DetailMaps` support.
