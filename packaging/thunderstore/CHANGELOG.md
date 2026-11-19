# Changelog

## 0.3.0

First Thunderstore release.

- Ray-traced lighting (relight), multi-bounce path tracing (pathtrace) with NVIDIA DLSS Ray Reconstruction or UltraRT's own denoiser, and pt-dn.
- Ray-traced reflections including the 8-x portal mirrors, volumetric light and fog, lit particles, screen effects (bloom, heat haze, shockwaves, underwater, rain and wet blood), the first-person gun and arms lit by the ray tracer.
- Per-level light overrides and an in-game light editor; free camera and offline renderer.
- Settings page in PluginConfigurator (main page and Advanced settings), with the version and credits.
- Installs anywhere under `BepInEx/plugins` (mod-manager folders such as `<Team>-UltraRT` work), and finds the optional UltraRT_DetailMaps package by itself.
- On DirectX 11, or on a GPU without ray tracing, an in-game message explains what to change and the mod stays off.
- HDR10 display output (prototype, off by default).
