# Changelog

## 0.3.0

Initial release.

- Three ray-traced view modes: multi-bounce path tracing (`pathtrace`), faster single-bounce ray tracing (`relight`), and a vanilla-style ray-traced mode (`pt-dn`).
- NVIDIA DLSS Ray Reconstruction support on RTX GPUs, plus a built-in denoiser for other DXR GPUs.
- Ray-traced reflections on metal, glass, water, terminals, and mirrors (showing V1, wings, and your held weapon), plus light passing through portals.
- Traced water caustics, depth absorption, interactive ripples, and stained-glass light projection.
- Dynamic light from gunshots, weapon and laser beams, parries, explosions, fires, torches, and lightning.
- Volumetric light shafts and fog, lit smoke and blood spray, bloom, heat haze, rain puddles, glossy wet blood, translucent lampshades, and ray-traced lighting on your gun and arms.
- Native HDR10 output for compatible displays (off by default).
- In-game settings menu with Quality presets (`Ultra`, `High`, `Performance`, `Competitive`), light editor, free camera with offline video rendering, and optional `UltraRT_DetailMaps` support.
