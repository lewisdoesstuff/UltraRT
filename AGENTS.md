# Contributor and agent notes

## Where to start
UltraRT is a BepInEx ray / path-tracing mod for ULTRAKILL (Unity 2022.3, DX12 DXR). [README.md](README.md) says what it is and has an index of [docs/](docs) with a one-line description each: read the doc for the area you touch before changing it. [bench/README.md](bench/README.md) is the reference for running the game (locks, suites, scenario JSON). Code: `src/UltraRT` (plugin), `src/UltraRT.Preload` (preloader patcher for HDR output), `unity/UltraRT.Shaders` (shaders, built into `ultrart.shaders`), `native/UltraRT.Native` (GPU timer, DLSS RR), `scripts` (build, deploy, lock, bench, trailer), `data/lights` (shipped light overrides).

## Build and deploy
- Game installs ("slots"): **main** is the Steam install, the one the user plays. **alt** is an optional copy for visual and diagnostic runs. Scripts find main through Steam (`ULTRAKILL_DIR` overrides it) and alt through `ULTRAKILL_ALT_DIR` or `%LOCALAPPDATA%\UltraRT\installs.json` (bench/README.md, "Install locations"). Both are only touched by the scripts below.
- Build first, outside the lock, without touching a game: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -NoDeploy`. It builds the shader bundle (Unity batch mode), the plugin and the native DLL into `<repo>\stage`. A plain `build.ps1` (or `-Slot alt`) deploys straight into an install: don't, outside your own lock hold.
- Deploy only while you hold a slot: `.\scripts\deploy.ps1 -Slot main|alt -Owner <name>` (seconds; it refuses a slot someone else holds). A running game hot-reloads a changed shader bundle but keeps `UltraRT.dll` / `UltraRT.Native.dll` loaded: those need a relaunch.
- A fresh checkout or worktree has no `lib\` (it is gitignored). Copy `lib\dlss` from another checkout, or run `scripts\fetch-dlss.ps1` (it downloads NVIDIA's public DLSS SDK parts). Without it the native DLL builds as a stub, `build.ps1` warns `building without Ray Reconstruction`, and DLSS RR (and so `pathtrace`'s default denoiser) is unavailable. `lib\PluginConfigurator.dll` (`scripts\fetch-pluginconfigurator.ps1`) is the same for the in-game settings menu, unless the game's own `BepInEx\plugins\PluginConfigurator` has it.
- Compile references come from the main install (read only; `src\GameDir.props`). Docs-only work needs no build.

## Game lock
- `scripts\game-lock.ps1 acquire <owner> [-Kind perf|visual] [-Slot main|alt|any]`, `release <owner>`, `status [-Watch]`, `slot <owner>`, `cancel <owner>`. When several checkouts share the installs, run it from the main checkout's `scripts` folder, so everyone runs the same version. Slots are served FIFO; `acquire` prints your position and the slot you got, one ticket per owner.
- Hold a slot only while a game runs. Build before `acquire`; analyse screenshots and edit code after `release`; queue again for the next run. Always `release`, also after a failure.
- Prefer the alt install: `acquire <owner> -Kind visual` takes whichever slot is free, alt first, and is enough for screenshots and look work. `-Kind perf` (the default) is **exclusive**: it needs both slots and no game of the user's, because frame times are meaningless when two games share the GPU. Use it only for frame-time questions.
- While the user is playing (`status` shows `user game: playing`), nothing is granted and nothing starts. Wait.

## Benchmarks
- `.\scripts\bench.ps1 -Suite a,b -Slot main|alt [-Perf] [-Compare <older run>]`, suites in `bench/suites/<name>.json`, results (`results.json`, `summary.md`, `game.log`, screenshots) in `bench/results/<time>_<suite>/` (gitignored). `-Perf` = frame times only; `-KeepOpen` / `-Stop` for resident-game iterations. Details, scenario fields and poses: bench/README.md.
- Keep a run short: 3-8 scenarios, not a whole suite of 24. Run the feature's own suite plus a `-quick` suite.
- Skip the slow 1-1 poses (torch hall, cerberus, "user d") in iterative runs; the 1-1 start pose is quick.
- `merge-regress` is run once per merge batch by whoever merges, not per feature.
- Several suites can share one launch (`-Suite a,b`). Pose loading and A/B traps (`approach-fast`, checkpoints, chaining) are in bench/README.md; read them before writing a new pose scenario.

## Safety rules (must follow)
- Never kill, restart or touch an ULTRAKILL process you did not start. An `ULTRAKILL.exe` started without any `-ultrart-*` argument is the user playing. A hand launch needs `-ultrart-agent` (any `-ultrart-*` switch counts) so it is recognisable as yours.
- Launch the game only through `scripts\bench.ps1`, never by hand: it adds `-force-d3d12 -force-gfx-jobs legacy` (DX12 is required for ray tracing; native D3D12 graphics jobs deadlock when 8-3 unloads), refuses to start while the user is playing and warns when you hold no slot.
- Benchmarks must never write the game's save, prefs or Steam stats: the plugin's `BenchSafety` blocks every writer for a run. Do not weaken it. It is active only during bench runs, so a hand launch is not protected.
- Never change the user's display settings or prefs. The game's prefs live in the registry (`HKCU\Software\Hakita\ULTRAKILL`), shared by both installs; `scripts\display-prefs.ps1` stores and restores the user's display values around runs (`save` only when the user changed them on purpose).
- Do not edit the user's BepInEx config files (`BepInEx\config\ooo.lew.ultrart.cfg` and others): their settings are deliberate. A bench reads them; set things per run with `-Extra` flags or scenario variants instead.
- Never commit NVIDIA or game-derived files: DLSS binaries and headers (`lib\dlss`), game binaries, textures, fonts, music, rendered frames or videos (`bench/results`, `lib` are gitignored; trailer outputs live under `bench/results`). Do not copy them into docs either. Keep personal paths (user folders, drive letters of one machine) out of tracked files: use `<ULTRAKILL_DIR>`-style placeholders.
- If the game or the PC crashes or hangs during a run, stop and report with the log paths. Do not retry in a loop.

## Repo conventions
- Line endings are LF everywhere (`.gitattributes`: `* text=auto eol=lf`). Write LF; do not introduce CRLF. `git ls-files --eol <file>` shows what a file uses.
- Merge branches with `git merge --no-ff -X ignore-space-at-eol <branch>`. A branch cut before the LF conversion still has CRLF files: add `-X renormalize` as well (`git merge --no-ff -X renormalize -X ignore-space-at-eol <branch>`), or the whole file conflicts.
- A change is not done until its doc is: update the doc that covers the area (defaults, flags, keys, measured results with their date), keep measured history, and add a note where a historical number no longer matches the current default.
