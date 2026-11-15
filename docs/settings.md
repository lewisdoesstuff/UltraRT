# Settings menu

UltraRT has an in-game settings page, so view mode, quality, lighting and the effects can be changed without command-line flags or hotkeys. It is a page in [PluginConfigurator](https://thunderstore.io/c/ultrakill/p/EternalsTeam/PluginConfigurator/), the library most ULTRAKILL mods use for their settings.

## Install and open

PluginConfigurator is optional. Without it UltraRT works as before: flags, hotkeys, and the config file `BepInEx/config/ooo.lew.ultrart.cfg`. Install it from Thunderstore (r2modman or the Thunderstore Mod Manager: search "PluginConfigurator", EternalsTeam), or by hand: unzip the package into `BepInEx/plugins` so that `BepInEx/plugins/PluginConfigurator/PluginConfigurator.dll` exists.

In game: pause, Options, **Plugin Config** (a button PluginConfigurator adds), then **UltraRT**. PluginConfigurator also opens its page list with `]` (rebindable in its own settings).

The log says which case applies (`BepInEx/LogOutput.log`):
- `Settings menu: PluginConfigurator page created (N settings)`
- `Settings menu: PluginConfigurator is not installed, so there is no in-game menu ...`

## Layout

| Section | Settings |
|---|---|
| View | Ray tracing on/off (F7), view mode (Page Up / Down: hybrid, relight, pathtrace, pt-dn, and the debug views) |
| Quality and performance | Trace resolution (Performance 0.5x, Balanced 0.67x, Quality 1x), temporal upsampler (F2), denoiser preset (Ctrl+F3), radiance cache mode (F3), light sampling (F6) |
| Path tracing (view mode pathtrace) | Denoiser (Auto = DLSS RR when available, UltraRT, DLSS RR), render resolution with RR (1/3, 1/2, 0.67x), paths per pixel, bounces per path, light samples per pixel, radiance cache after the last bounce, sky sample at every bounce; DLSS RR in relight / pt-dn (Ctrl+F8) and its exact-texel remodulation (docs/path-tracing.md, docs/dlss.md) |
| Lighting | Light reference, light exposure, fill light cut, sky + linear light (Ctrl+F5), sky sampling, sky light, emissive lighting (F5: glowing surfaces as lights) and its brightness |
| Auto exposure | On/off (Ctrl+F4), target brightness, darkest and brightest exposure (EV) |
| Effects | Volumetric light and fog (Ctrl+F6), fog density, light shaft strength; surface detail maps (Ctrl+F1), detail strength in relight/pt-dn and in hybrid, roughness variation; reflections (F4) and their strength, V1 in reflections, ray-traced mirrors; effect lights (muzzle flashes, explosions, fire); fire and explosions in reflections; lava and acid |
| Screen effects | Screen effects on/off (Ctrl+F2: the whole late pass), bloom (Shift+F2) and its strength, soft particles, heat haze over lava and fire and its strength, explosion shockwaves and their strength, glowing explosion debris, underwater shafts / fog / distortion; rain wetness and puddles, its wetness and puddle amount, wet blood and its drying time ([vfx.md](vfx.md)) |
| Cinematics | Offline render: view mode, frame rate, accumulated frames per AA sample, AA / motion-blur samples, paths and bounces (pathtrace), radiance cache tail, output size, motion blur and shutter, exposure (auto / locked), 16-bit PNG, ffmpeg encode and CRF; game speed during playback / render; free camera speed and sensitivity; "enemies ignore the player"; buttons: camera on / off (Home), play, render, save, load ([cinematics.md](cinematics.md)) |
| Tools | Reload shaders (F9), reset all UltraRT settings |
| Keybinds (sub page) | A read-only list of the hotkeys |

Setting names carry their hotkey in brackets where there is one.

## What applies when

Everything in the menu applies at once, with the same side effects as the hotkey (denoiser history reset, cache notified, and so on). Nothing needs a restart. Two things to know:
- **Trace resolution** reallocates the trace targets on the next frame. Performance (0.5x) is what the temporal upsampler is tuned for.
- **Effect lights** is one switch for the three effect-light features (spawned lights, virtual lights for sprites and particles, flicker). The finer controls stay command-line flags.

Not in the menu (they need a scene rebuild or are developer knobs): the shader and material set, static-geometry options, the benchmark and capture flags, the GPU timer, every other `-ultrart-*` flag.

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
- The page uses PluginConfigurator's `saveToConfig: false` fields and `saveToFile = false`: the BepInEx config is the only store.

To add a setting: one line in `InitSettings` (`m.Bool/Float/Choice(section, key, label, get, set, flags...)`, in the group that is current). A setting that other code reads only at the scene build does not belong in the menu.

## Testing

Checked in game with PluginConfigurator 1.10.3 and without it: every menu field changed through its change events (the same path a click takes) must set the live field and write its config entry; a live field changed behind the menu's back (as a hotkey does) must move the menu field and leave the config alone; reset all; startup from a config with a `-ultrart-scale` flag on the command line.
