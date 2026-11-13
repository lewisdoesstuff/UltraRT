# Relight mode

`relight` is full RT (`pt-dn`) with the game's light model swapped for a physical one. Nothing is tuned per light;
everything is derived from each light's colour, intensity and range, plus a few global knobs.

| Knob | Flag | Default | Effect |
|---|---|---|---|
| Falloff | `-ultrart-relight-falloff` | 1 | 0 = ULTRAKILL's curve, 1 = inverse-square, matched to the game's brightness at half the light's range, windowed smoothly to 2x range (where the game's lights end), clamped within 0.1 m |
| Fill cut | `-ultrart-relight-fill` | 1 | removes the game's unshadowed fill lights (upward-facing directional "ground bounce" fakes); bounce light replaces them |
| Ambient cut | `-ultrart-relight-ambient` | 1 | removes the game's flat ambient term |
| Exposure | `-ultrart-relight-exposure` | 1.5 | multiplier on every point/spot/directional light (emissive triangles keep their own scale) |
| White point | `-ultrart-relight-white` | 3 | Reinhard tone mapping on luminance in the denoiser's composite, so hotspots roll off instead of clipping |

The radiance cache uses the same light model (it shares `UltraRTLights.hlsl`), so the bounce is consistent with
the relit direct light. Shader side: `_Relight` (float4, zero = the game's model) in `UltraRTLights.hlsl`,
`_UltraRTTonemapWhite` (global) in `Denoise.compute`.

Not done yet: per-level/per-light JSON overrides, emissive exposure, a hybrid variant.

## Light fixtures
Many ULTRAKILL lights sit *inside* their fixture (torch bowls, braziers). The game has no shadows, so it never
mattered; ray traced, the bowl shadows everything below its rim, which in relight (no fill or ambient left) turns the
lower half of a torch-lit room black. When the scene is built, `SceneProxies` records the bounds of every small
renderer (< 4 m) that encloses a point/spot light. `UpdateFixtureLifts` (Plugin.cs) then moves each such light to
0.15 m above its fixture's top and gives it a soft-shadow radius of the fixture's horizontal half-size (0.08-0.5 m),
like a fire filling the bowl: the bowl still casts a soft shadow under itself, but walls and floor around it are lit.
Lights with something solid within 1 m above the fixture (ceiling lamps; a physics raycast) or more than 1.5 m below
its top stay where they are. The per-light radius goes in `_Lights[i*4+2].w` (0 = the global `_LightRadius`).
This applies in every mode; `-ultrart-nofixturelift` turns it off, `-ultrart-nofixtureshadows` removes fixture
shadows altogether.
