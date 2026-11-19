# Releasing UltraRT on Thunderstore

How UltraRT is packaged for the ULTRAKILL community on Thunderstore (installed by r2modman or the Thunderstore Mod
Manager), what goes where, and what is still to decide before the first upload. Set up on 2026-11-19
(`t3/thunderstore-release-package`).

```powershell
.\scripts\package.ps1 -SkipIconCheck        # dist\UltraRT-<version>.zip, from a clean commit (no icon yet)
.\scripts\package.ps1 -TexturePack -SkipIconCheck -Team <team>   # dist\UltraRT_DetailMaps-<version>.zip (optional pack)
```

## Version

`Version.props` (repo root) holds the one version, `UltraRTVersion` (0.3.0 since 2026-11-19; the plugin said 0.2.0
before). Both projects import it, so the assembly versions follow; `UltraRT.csproj` also generates
`PluginVersion.Number` (`obj\...\UltraRTVersion.g.cs`), a compile-time constant that `[BepInPlugin]` uses. From there:
- the log's first plugin lines: `Loading [UltraRT 0.3.0]` (BepInEx) and `UltraRT 0.3.0 in <plugin folder>`;
- the settings page's first line, `UltraRT 0.3.0` ([settings.md](settings.md));
- the startup notice's footer;
- `scripts\package.ps1`: the manifest's `version_number` and the zip name. Thunderstore needs `Major.Minor.Patch`;
  the script refuses anything else.

To release a new version: bump `Version.props`, add a `## <version>` section to
`packaging/thunderstore/CHANGELOG.md` (the script warns when it is missing), commit, package.

## `scripts\package.ps1`

1. **Clean commit.** Refuses a working tree with uncommitted or untracked changes unless `-AllowDirty` (the zip is then
   named `UltraRT-<version>-dirty.zip`; never upload one).
2. **Build** with `build.ps1 -StageDir dist\stage`: shader bundle (Unity batch mode), plugin, preloader, native DLL. No
   game install is written; references come from the main install as for every build. `-SkipBuild` reuses
   `dist\stage` if its `stage.json` names the same commit.
3. **Checks**: `lib\dlss` present (without it the native DLL is the stub without Ray Reconstruction); the native DLL
   links NGX (the stub doesn't); `UltraRT.dll` contains the settings page (PluginConfigurator.dll was found at build
   time); every shipped file is staged. With `-IncludeDlss` (the default): `lib\dlss\version.txt` says `rel` (the `dev`
   DLL must never ship) and `nvngx_dlssd.dll`'s Authenticode signature is valid (NVIDIA's file, unmodified).
4. **Assemble** `dist\pkg\UltraRT-<version>\` and zip it to `dist\UltraRT-<version>.zip` with forward-slash entry
   names (Windows PowerShell's `Compress-Archive` can write backslashes, which mod managers do not unpack as folders;
   the script checks the written zip). `dist\UltraRT-<version>.json` lists the files, commit and switches.

Switches: `-IncludeDlss:$false` leaves NVIDIA's runtime and its two notice files out (the README then says to add the
DLL by hand); `-SkipIconCheck` builds without `icon.png` (warning) or with a wrong-size one; `-Icon <png>`;
`-WebsiteUrl <url>` (default empty, which Thunderstore accepts); `-Team <name>` (the Thunderstore team: only used for
the detail-map package's dependency and the printed install path; `Author` until a team exists); `-OutDir`.

Package facts kept at the top of the script: name `UltraRT`, the description (checked against Thunderstore's 250
characters; 209 now), and the dependencies.

### Dependencies

As the ULTRAKILL community on Thunderstore has them on 2026-11-19 (latest versions, from the community's package list
`https://thunderstore.io/c/ultrakill/api/v1/package/` and
`https://thunderstore.io/api/experimental/package/EternalsTeam/PluginConfigurator/`):

| Dependency | String | Notes |
|---|---|---|
| BepInEx pack | `BepInEx-BepInExPack-5.4.2305` | latest (2026-03-17). 618 ULTRAKILL packages still pin `-5.4.2100`, 190 use `-5.4.2305`, 35 `-5.4.2304`; PluginConfigurator 1.10.3 and AngryLevelLoader 4.1.1 depend on `-5.4.2305` |
| PluginConfigurator | `EternalsTeam-PluginConfigurator-1.10.3` | latest (2026-10-18); AngryLevelLoader 4.1.1 and others use this string |

PluginConfigurator is a soft dependency in code (UltraRT runs without it, [settings.md](settings.md)), but a hard one in
the package so mod-manager users get the settings page. Check both for newer versions before each upload.

### Package layout and where r2modman puts it

r2modman's install rules for ULTRAKILL (`ecosystem.json`, `InstallRulePluginInstaller.ts` in
github.com/ebkr/r2modmanPlus): a `plugins/` or `patchers/` folder goes to `BepInEx/<route>/<Team>-<Name>/` with its
subfolders kept; root files with no rule (`manifest.json`, `README.md`, `CHANGELOG.md`, `icon.png`) land in
`BepInEx/plugins/<Team>-<Name>/` too; a `config/` folder would go straight into `BepInEx/config` (we ship none).

| In the zip | Installed to (r2modman profile) | What |
|---|---|---|
| `manifest.json`, `README.md`, `CHANGELOG.md`, `icon.png` | `BepInEx/plugins/<Team>-UltraRT/` | Thunderstore metadata; `README.md` is the player-facing page (`packaging/thunderstore/README.md`, tokens filled in) |
| `plugins/UltraRT.dll` | same folder | the plugin |
| `plugins/UltraRT.Native.dll` | same folder | GPU timer, DLSS RR glue, HDR swapchain proxy |
| `plugins/ultrart.shaders` | same folder | shader bundle |
| `plugins/ultrart-materials.json` | same folder | shipped material rules |
| `plugins/lights/*.json`, `plugins/lights/proposed/` | same folder, `lights/` | shipped per-level light overrides (`data/lights`) |
| `plugins/nvngx_dlssd.dll` (`-IncludeDlss`) | same folder | NVIDIA's DLSS RR runtime, `rel`, v310.9.16, 48.3 MB |
| `plugins/NVIDIA-RTX-SDKs-LICENSE.txt`, `plugins/NVIDIA-DLSS-THIRD-PARTY-NOTICES.txt` | same folder | NVIDIA's license (copied from `lib\dlss\LICENSE.txt` at package time) and the runtime's third-party notices (`packaging/thunderstore/`) |
| `patchers/UltraRT.Preload.dll` | `BepInEx/patchers/<Team>-UltraRT/` | the HDR preloader ([hdr.md](hdr.md)) |

0.3.0: 39 files, 22.2 MB zipped. By hand: `plugins/` to `BepInEx/plugins/UltraRT/`, `patchers/` to
`BepInEx/patchers/UltraRT/` (the repo's own layout, which `deploy.ps1` and the dev scripts keep using).

## Install-location audit (2026-11-19)

Everything UltraRT ships is read from the folder of `UltraRT.dll` (`Info.Location`), so any folder under
`BepInEx/plugins` works; everything it writes goes under `BepInEx/ultrart` (in r2modman: the profile's BepInEx).

| File | Found by | Notes |
|---|---|---|
| `ultrart.shaders` | `Plugin.Awake`: plugin folder | |
| `UltraRT.Native.dll` | plugin: plugin folder (`GpuTimer`, `DlssRR.TryCreate`, the RR default check); **preloader**: search under `BepInEx/plugins` | the preloader used `plugins/UltraRT/` only until 2026-11-19 (a mod-manager install had no HDR); now it searches 4 folders deep, prefers the folder with `UltraRT.dll`, newest wins when there are several (warning) ([hdr.md](hdr.md)) |
| `nvngx_dlssd.dll` | next to `UltraRT.Native.dll`; NGX is pointed at the plugin folder | |
| `ultrart-materials.json` | plugin folder, then `BepInEx/config` (personal rules win) | |
| shipped `lights/*.json` | plugin folder `lights/` | user edits, classifier and fit output: `BepInEx/ultrart/lights/` |
| detail maps | `BepInEx/ultrart/materials/<source>` (local cache), else a package under `BepInEx/plugins` | [materials.md](materials.md), "Where the plugin looks" |
| NGX logs and data, `hdr.log`, recordings, bench output, cinematics | `BepInEx/ultrart/...` | user data, never in the plugin folder |
| config | `BepInEx/config/ooo.lew.ultrart.cfg` (`Paths.ConfigPath`; the preloader reads it too) | |

Dev-only paths that still name `plugins\UltraRT` on purpose: `build.ps1` (direct deploy), `deploy.ps1` /
`Sync-Stage`, `bench.ps1 -KeepOpen`'s DLL check, the csprojs' `DeployToGame` targets. A player with both a manual
`plugins/UltraRT` and a mod-manager copy in the same BepInEx gets two copies of the plugin; BepInEx loads one, and the
preloader warns.

## Optional detail-map package (`-TexturePack`)

`package.ps1 -TexturePack` builds `dist\UltraRT_DetailMaps-<version>.zip` from a local maps cache (default: the main
install's `BepInEx\ultrart\materials\ai`, read only; `-MapsDir`, `-MapsSource`). Only what the plugin reads goes in:
`manifest.json` and the 1955 `*_d.png` (the cache's `_n` / `_r` PNGs are the generator's intermediates): 125.8 MB
zipped. Layout:

```
manifest.json            name UltraRT_DetailMaps, dependency <Team>-UltraRT-<version>
README.md                packaging/thunderstore/detailmaps/README.md
plugins/ultrart-detailmaps.json   the marker: {"format": 1, "name", "version", "sources": ["ai"], "generator", "made", "commit"}
plugins/ai/manifest.json, plugins/ai/*_d.png
```

r2modman installs it to `BepInEx/plugins/<Team>-UltraRT_DetailMaps/`; the plugin finds the marker there
([materials.md](materials.md)). A local cache made by `tools/materials` still wins. With neither, the maps are off and
the log has one info line. The zip and its staging stay in `dist/` (gitignored): the maps are derived from the game's
textures and are never committed.

## Startup notice

When UltraRT can't run, `StartupNotice.cs` draws one message box per launch (IMGUI, top centre, scaled to the screen
height; it appears once the intro is over, stays 30 s, a click closes it) and logs the same text as a warning:
- **Not Direct3D 12** (the launch options are missing): `UltraRT is off: the game is running on DirectX 11, ray tracing
  needs DirectX 12`, with `-force-d3d12 -force-gfx-jobs legacy` and where to set them (r2modman: Settings > Debugging >
  Set launch parameters; Steam: Properties > General > Launch Options).
- **D3D12 without DXR**: the GPU's name, which GPUs work, that the game runs as normal.
- **Shader bundle missing or unreadable**: reinstall.

UltraRT then stays idle (the old behaviour; before 2026-11-19 it only logged `Ray tracing unavailable ...`, and a
missing bundle logged an error and left no host at all). Bench launches don't show it (their DX11 runs are
comparisons); `-ultrart-startup-notice` shows it anyway, for testing. `-ultrart-minimal` stays silent.

## Testing a package

Done for 0.3.0 on 2026-11-19 on the **alt** install, under a visual lock (`release-pkg`), main untouched:
1. Back up alt's `BepInEx\plugins\UltraRT`, `BepInEx\patchers\UltraRT` and `config\ooo.lew.ultrart.cfg`, and snapshot
   (SHA-1) every file under `plugins`, `patchers` and `config` plus the `ultrart\materials` folder names.
2. Remove the two UltraRT folders; unpack both zips the way r2modman does (`Author-UltraRT`,
   `Author-UltraRT_DetailMaps`); hide the local `ai` cache (rename) so the package has to be found.
3. `bench.ps1 -Suite release-install -Slot alt -NoBursts -Extra '-ultrart-hdr','on'` (warm-up + 0-1 metal: off,
   relight, pathtrace, pathtrace without maps): `bench/results/20261119-175409_release-install_alt`.
4. `bench.ps1 -Suite release-dx11 -Slot alt -Api d3d11 -Extra '-ultrart-startup-notice'`: `-175519_release-dx11_alt`.
5. Local cache back, PluginConfigurator copied in from main (read only) as `EternalsTeam-PluginConfigurator`,
   `release-dx11` on D3D12: `-175550_release-dx11_alt`.
6. Restore: remove the package folders, the PluginConfigurator copy and the `config\PluginConfigurator` folder it
   wrote; copy the backup back; the snapshot compare matched (42 entries). Then `release`.

What the logs showed:

| Check | Log line (game.log) | Result |
|---|---|---|
| version | `Loading [UltraRT 0.3.0]`, `UltraRT 0.3.0 in ...\plugins\Author-UltraRT` | ok |
| HDR preloader | `HDR output: using ...\plugins\Author-UltraRT\UltraRT.Native.dll`, `swapchain hooks installed`, later `HDR output: active, 2560x1440 HDR10` | ok |
| shaders | `Bundle loaded: DebugView=ok PathTrace=ok proxy=ok Denoise=ok Composite=ok Reflections=ok` | ok |
| native DLL + DLSS RR | `NGXInitContext ... called from module UltraRT.Native.dll at ...\Author-UltraRT`, `Ray Reconstruction feature 854x480 -> 2562x1440 ... (state 2, 7 evaluations)` | ok, pathtrace frame 6.2 ms (shared GPU: not a perf number) |
| detail maps from the package | `Material maps (ai): package UltraRT_DetailMaps 0.3.0 in ...\Author-UltraRT_DetailMaps\ai`, `1955 textures` | ok; the pathtrace shot with maps differs visibly from `mat.enabled=0` on the 0-1 walls |
| local cache wins | `Material maps (ai): local cache ...\BepInEx\ultrart\materials\ai` (package still installed) | ok |
| settings page | `Settings menu: PluginConfigurator page created (104 settings: 17 main, 87 advanced)` with PluginConfigurator 1.10.3 | created; the header and credits were not looked at in the menu (a bench can't open it) |
| DX11 notice | warning line `UltraRT is off: the game is running on DirectX 11 ...`; the screenshot shows the box | ok |

Not tested: a real r2modman install (the layout was reproduced from r2modman's source), a GPU without DXR, a missing
bundle (the code path is the same notice), Steam's own launch options combined with r2modman's.

## Licensing checklist

- [x] **No game files** in either package: the file list is the plugin's own files, shipped JSON, and NVIDIA's DLL
  with its license and notices. The detail maps are derived from the game's textures (see the open question below).
- [x] **NVIDIA DLSS runtime** ([dlss.md](dlss.md), "License"): only the `rel` DLL, unmodified and signed (both checked by
  the script); `NVIDIA-RTX-SDKs-LICENSE.txt` and the third-party notices (Programming Guide 9.6) beside it;
  attribution as text in the package README's credits and the settings page's Credits block ("NVIDIA DLSS Ray
  Reconstruction (NVIDIA DLSS SDK)", the trademark line, and "not sponsored or endorsed by NVIDIA", license 4c).
- [ ] **NVIDIA marks** (logos) are not used: the license (exhibit 7.1(b)) asks for "the NVIDIA Marks" in splash
  screens, about box and credits, but each use of a mark needs NVIDIA's prior written approval (7.2(c)). Text
  attribution only until NVIDIA says otherwise.
- [ ] **NVIDIA notification / permission** (decision): notification is required before a *commercial* release (a free
  mod arguably isn't one). The RR Integration Guide's section 4.2 says the DLLs in the SDK must not be distributed
  publicly and to contact NVIDIA for a distributable one, while the same guide's 5.1 and the SR guide name the public
  `rel` DLL as the one to ship ([dlss.md](dlss.md)). Options: email nvidia-rtx-license-questions@nvidia.com (and the
  notification form) before uploading with the DLL, or upload with `-IncludeDlss:$false` and tell RTX users where to
  get `nvngx_dlssd.dll`. License 2(c) also asks that the DLL be distributed under terms at least as protective as
  NVIDIA's: shipping NVIDIA's license beside it is the minimum.
- [ ] **UltraRT's own licence** (decision): none chosen; the package README says "copyright its author, all rights
  reserved (no licence chosen yet)" (`$LicenseLine` in `package.ps1`). An open-source licence for UltraRT must not
  cover the NVIDIA files (license restriction e): say so in the licence text, or keep the DLL out of the package.
- [ ] **Detail maps** (decision): they contain no game textures but are derived from every one of them (normal and
  roughness by PBRify Remix). Decide whether to publish them, perhaps after asking New Blood / Hakita; the generator
  path (users make the maps themselves with `tools/materials`) needs no permission.
- [x] **PBRify Remix** (CC0): no obligation; credited in both READMEs and the settings page.
- [x] **BepInEx, PluginConfigurator**: dependencies, not redistributed; credited.
- [x] `Dlss.cpp` carries the NGX helper headers' notice; it is source only and not in the package.

## Manual steps left before the first upload

1. **Icon**: a 256x256 PNG at `packaging/thunderstore/icon.png` (`assets/icon.svg` is the README's logo and could be the
   base). Thunderstore rejects a package without `icon.png`; then drop `-SkipIconCheck`.
2. **Thunderstore team**: create it on thunderstore.io, then build the detail-map package with `-Team <team>` (its
   dependency string names the team).
3. **Website URL** (optional): `-WebsiteUrl`, e.g. the repository if it becomes public.
4. **Decisions above**: NVIDIA email / DLL or no DLL, UltraRT's licence, whether the detail maps ship.
5. **Upload**: thunderstore.io > Upload, community ULTRAKILL, the zip from `dist\`; categories by hand. Check the
   dependency versions first (table above).
6. Optional: the native DLL initialises NGX with the engine version string `UltraRT-0.1` (`Dlss.cpp`); harmless
   metadata, could follow `Version.props`.
