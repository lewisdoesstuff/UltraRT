# Settings menu

UltraRT has an in-game settings page, so view mode, quality, lighting and the effects can be changed without command-line flags or hotkeys. It is a page in [PluginConfigurator](https://thunderstore.io/c/ultrakill/p/EternalsTeam/PluginConfigurator/), the library most ULTRAKILL mods use for their settings.

## Install and open

The page's first line is the version (`UltraRT 0.3.0`, from `Version.props`; since 2026-11-19), and its end has a
**Credits** block (NVIDIA DLSS Ray Reconstruction, PBRify Remix; [release.md](release.md), licensing checklist).

PluginConfigurator is optional. Without it UltraRT works as before: flags, hotkeys, and the config file `BepInEx/config/ooo.lew.ultrart.cfg`. Install it from Thunderstore (r2modman or the Thunderstore Mod Manager: search "PluginConfigurator", EternalsTeam), or by hand: unzip the package into `BepInEx/plugins` so that `BepInEx/plugins/PluginConfigurator/PluginConfigurator.dll` exists.

In game: pause, Options, **Plugin Config** (a button PluginConfigurator adds), then **UltraRT**. PluginConfigurator also opens its page list with `]` (rebindable in its own settings).

The log says which case applies (`BepInEx/LogOutput.log`):
- `Settings menu: PluginConfigurator page created (N settings)`
- `Settings menu: PluginConfigurator is not installed, so there is no in-game menu ...`

## Layout

The page has two levels (since 2026-11-19; before, every setting was on one long page): a short **main page** with the settings most players touch, and an **Advanced settings** sub page (a button on the main page, under the main settings) with everything else. Both pages keep the same group headers, in the same order; a group appears on a page only where it has settings there. Tools and the Keybinds sub page are on the main page as before. Where a setting lives makes no difference to how it is saved: every value stays in the BepInEx config, under the same section and key.

**Main page**

| Group | Settings |
|---|---|
| View | Ray tracing on/off (F7), view mode (Page Up / Down: relight, pathtrace, pt-dn, and the debug views; hybrid was removed on 2026-11-19), the game's screen dither while ray traced (off by default: the game's Options > Graphics > Dithering draws a fine checkerboard over the frame; [ultrakill-rendering.md](ultrakill-rendering.md), "Final post-process: dither") |
| Quality and performance | Trace resolution (Performance 0.5x, Balanced 0.67x, Quality 1x; relight and pt-dn) |
| Path tracing (view mode pathtrace) | Denoiser (Auto = DLSS RR when available, UltraRT, DLSS RR), render resolution with RR (1/3, 1/2, 0.67x), render resolution with UltraRT's denoiser; DLSS RR in relight / pt-dn (Ctrl+F8) ([path-tracing.md](path-tracing.md), [dlss.md](dlss.md)) |
| Auto exposure | RT brightness (0 = the default: lit rooms may sit above the game, dark places are not lifted; 1 = also lifts dark scenes, the look from before 2026-11-17; [relight.md](relight.md) "Follow the game") |
| Effects | Volumetric light and fog (Ctrl+F6), reflections (F4), V1 in reflections, lit particles (Shift+F7: smoke, dust, blood spray and debris lit by the ray-traced light; [particles.md](particles.md)), ray-traced lighting on the gun and arms (Ctrl+F7, the viewmodel pass; [viewmodel.md](viewmodel.md)) |
| HDR display | HDR output (off by default; read when the game starts, so it applies after a restart), paper white in nits (0 = as bright as Windows shows SDR), peak brightness in nits (0 = the display's peak) ([hdr.md](hdr.md)) |
| Tools | Reload shaders (F9), the light editor's and the cinematic camera's buttons, reset all UltraRT settings |
| Keybinds (sub page) | A read-only list of the hotkeys |

**Advanced settings (sub page)**

| Group | Settings |
|---|---|
| Quality and performance | Temporal upsampler (F2), denoiser preset (Ctrl+F3), radiance cache mode (F3), light sampling (F6) |
| Path tracing (view mode pathtrace) | UltraRT's denoiser: exact texels (remodulation), paths per pixel, bounces per path, light samples per pixel (RR), radiance cache after the last bounce, end wide paths in the cache (footprint, in cache cells), sky sample at every bounce; DLSS RR's exact-texel remodulation |
| Lighting | Light reference, light exposure, fill light cut, sky + linear light (Ctrl+F5), sky sampling, sky light, emissive lighting (F5: glowing surfaces as lights) and its brightness |
| Auto exposure | On/off (Ctrl+F4), target brightness, darkest and brightest exposure (EV) |
| Effects | Fog density, light shaft strength; surface detail maps (Ctrl+F1), detail strength in relight/pt-dn, roughness variation; reflection strength, ray-traced mirrors; effect lights (muzzle flashes, explosions, fire); fire and explosions in reflections; lava and acid; water caustics strength and their shadow ray ([caustics.md](caustics.md), since 2026-11-19); light glints on water and wet floors (strength, [reflections.md](reflections.md) "Glints", since 2026-11-19) |
| Screen effects | Screen effects on/off (Ctrl+F2: the whole late pass), bloom (Shift+F2) and its strength, soft particles, heat haze over lava and fire and its strength, explosion shockwaves and their strength, glowing explosion debris, underwater shafts / fog / distortion; rain wetness and puddles, its wetness and puddle amount, wet blood and its drying time, blood stains in reflections ([vfx.md](vfx.md)) |
| HDR display | How far glowing surfaces, lava and acid, the sky and effects go above white, bloom above white, ray-traced highlights above white |
| Light editor (per-level light overrides) | Per-level light overrides on/off (`[Lights] Overrides`), the light editor (Insert), the selected light's intensity, colour, range, shadows, disabled, shape and size ([lighting-overrides.md](lighting-overrides.md)). The selected-light values are session only (they live in the override JSON); the editor's buttons are under Tools |
| Cinematics | Offline render: view mode (pathtrace, relight, the current mode, `off` = the game's image, `live` = as played), frame rate, accumulated frames per AA sample, AA / motion-blur samples, paths and bounces (pathtrace), radiance cache tail, output size, motion blur and shutter, exposure (auto / locked), 16-bit PNG, ffmpeg encode and CRF; game speed during playback / render; free camera speed and sensitivity; "enemies ignore the player", "V1's reflection and shadow stay while the camera is free" ([cinematics.md](cinematics.md)). The camera buttons (on / off (Home), play, render, save, load) are under Tools |

The rule for the split: the main page has the mode, resolution, the denoiser / Ray Reconstruction choice, overall brightness, HDR on/off with paper white and peak, the big feature switches and the game's dither. Tuning values, sampler and denoiser internals, per-effect gains, debug views' companions and A/B or comparison switches go to Advanced. A judgement call, so noted: screen effects, surface detail maps, emissive lighting and lava are feature switches too, but stay on Advanced (they are on by default and are part of the look; the main page lists what players change for speed or taste). Auto exposure on/off is Advanced: off is a diagnostic ([relight.md](relight.md)).

Setting names carry their hotkey in brackets where there is one.

## What applies when

Everything in the menu applies at once, with the same side effects as the hotkey (denoiser history reset, cache notified, and so on). Nothing needs a restart. Two things to know:
- **Trace resolution** reallocates the trace targets on the next frame. Performance (0.5x) is what the temporal upsampler is tuned for.
- **Effect lights** is one switch for the three effect-light features (spawned lights, virtual lights for sprites and particles, flicker). The finer controls stay command-line flags.

Lit particles (`[Particles] Lit`) and the viewmodel pass (`[Viewmodel] Enabled`) were hotkeys, flags and config entries only until 2026-11-19; they are on the main page now. Not in the menu (they need a scene rebuild or are developer knobs): texture LOD (`[Materials] TextureLod`, `LodBias`; [materials.md](materials.md)), the shader and material set, static-geometry options, the benchmark and capture flags, the GPU timer, every other `-ultrart-*` flag.

## Saving, hotkeys, flags

- **A change made in the menu is saved** to `BepInEx/config/ooo.lew.ultrart.cfg` (about a second after the last change, so dragging a slider is one write) and applied at the next start. The new entries are written to the file the first time the plugin starts, with their defaults, so the file can also be edited by hand or with a config manager. The older entries keep their sections: `[View] DefaultMode`, `[Relight] AutoExposure`, `[Materials]`, `[Volumetrics] Enabled`. The view mode entry is the startup mode.
- **Hotkeys are for the session.** Pressing one changes the setting, and the menu entry follows within a frame, but nothing is written. So is anything else that sets the fields at run time, such as a benchmark variant. To keep a hotkey's choice, set it in the menu.
- **Command-line flags win.** For a setting whose flag is on the command line, the config entry is not applied at startup (the log says `follows the command line`), so benchmarks behave as before. The menu shows the value in effect. Changing it in the menu still works and saves.
- `Ray tracing on/off` is session only: it has no config entry, so UltraRT never starts switched off because of a saved choice (use `-ultrart-off` for that).
- **Reset all UltraRT settings** puts every menu setting back to its code default and saves that.

## Code

`src/UltraRT/SettingsMenu.cs`, plus three hooks in `Plugin.cs` (`InitSettings` first in `Awake`, `AttachSettings` once the shaders are loaded, `_menu.Update()` in `OnUpdate`) and `ApplyDenoiserPreset` in `Plugin.Cache.cs`, which the Ctrl+F3 hotkey and the menu share.

- `InitSettings` registers every setting as a getter/setter pair over the plugin's fields (the same ones the hotkeys and flags use), and remembers the current value as its code default. It runs before any flag or config entry changes a field.
- `AttachSettings` runs after the `Parse*` methods and `LoadShaders`: it binds the config entries (an existing entry is reused as it is: `.Legacy()`), applies those whose flags are absent, saves the file once, and creates the page.
- `SettingsMenu.Update` runs every frame: it compares each live value with what the menu shows and pushes changes into the page, and saves the config after the last menu change.
- `SettingsMenuUi` is the only code that touches `PluginConfig.API`. It is compiled when `PluginConfigurator.dll` is found at build time (`lib/`, `BepInEx/plugins/PluginConfigurator/`, or `/p:PluginConfiguratorDll=...`; `scripts/fetch-pluginconfigurator.ps1` downloads it into `lib/`, which git ignores, and does not touch the game), and only called when the plugin is loaded, behind a `[BepInDependency(..., SoftDependency)]` and a `NoInlining` call, so UltraRT loads without it. A build without the library has no menu code and warns at build time.
- `SettingsMenuUi.Create` builds the main page from the settings marked `.Basic()` and the "Advanced settings" `ConfigPanel` from the rest, each with `AddPage` (group headers where the group changes). At startup the log lists both pages, with or without PluginConfigurator: `Settings menu: main page (N): View.Enabled, ...` and `Settings menu: advanced page (N): ...`.
- The page uses PluginConfigurator's `saveToConfig: false` fields and `saveToFile = false`: the BepInEx config is the only store.

To add a setting: one line in `InitSettings` (`m.Bool/Float/Choice(section, key, label, get, set, flags...)`, in the group that is current). It goes to the Advanced page unless the line ends in `.Basic()`; keep the main page short (see "Layout" for the rule). A setting that other code reads only at the scene build does not belong in the menu.

## Testing

Checked in game with PluginConfigurator 1.10.3 and without it: every menu field changed through its change events (the same path a click takes) must set the live field and write its config entry; a live field changed behind the menu's back (as a hotkey does) must move the menu field and leave the config alone; reset all; startup from a config with a `-ultrart-scale` flag on the command line.

The two-page split (2026-11-19) was not clicked through in game: the alt install has no PluginConfigurator, and a session on the main install, which has it, changes the user's own `ooo.lew.ultrart.cfg` as soon as a value is set in the menu. It was checked by a build with `PluginConfigurator.dll` (the page code compiled in) and by an alt run that logged the split (`Settings menu: main page (17): ...`, `advanced page (87): ...`); in the same run, bench variants that switch lit particles and the viewmodel pass moved their new menu entries (`Particles.Lit`, `Viewmodel.Enabled` "changed outside the menu"). Trap found on the way: `ParseParticleArgs` used to run first in the first update, after the menu had bound `[Particles] Lit`, so the menu's short label became the entry's description in the config file; `AttachSettings` now parses the particle arguments first.
