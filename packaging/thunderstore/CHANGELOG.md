# Changelog

## 0.3.1

- Fixed Cyber Grind shadows disappearing or glitching out when enemies spawn after a wave settles (refresh the ray-tracing acceleration structure whenever `EndlessGrid` rebuilds its combined arena mesh).
- Fixed blood-stained moving objects (Cyber Grind blocks on subsequent waves, doors, elevators, moving platforms) freezing in the ray-traced scene while moving, eliminating shadow jumps when blocks settle.
- Fixed newly spawned Cyber Grind stairs and jump pads popping into ray tracing late during wave transitions.
- Added the animated Cyber Grind neon pulse wave and custom grid texture support to ray-traced views.

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
- Seamless portals: rays follow each portal's own recursion rules, and lights, blood stains, water, glass and effects carry through portals and mirrors (including 8-3's doorways and the endless drop).
- Stability and smoothness: no hitches when enemies spawn, a guard against a ray-tracing crash in Unity's acceleration-structure builds, and a streamlined settings menu.
