# Runs benchmark suites unattended and prints the summaries; optionally compares against an earlier run.
# Usage (hold a game slot first: scripts\game-lock.ps1 acquire <owner>; see bench/README.md "How to run tests"):
#   .\scripts\bench.ps1                         # short suite
#   .\scripts\bench.ps1 -Suite full
#   .\scripts\bench.ps1 -Suite a,b,c            # several suites in ONE game launch (results in one folder per suite)
#   .\scripts\bench.ps1 -Suite poses -Perf      # frame times only: no screenshots, no noise bursts
#   .\scripts\bench.ps1 -Suite x -Slot alt      # run in the alt install (visual/diagnostic runs)
#   .\scripts\bench.ps1 -Suite x -KeepOpen      # leave the game running at the main menu; the next call reuses it
#   .\scripts\bench.ps1 -Stop                   # quit that resident game
#   .\scripts\bench.ps1 -Suite x -Perf -Etw     # also record a CPU trace (trace.etl; scripts\etw-setup.ps1 once)
#   .\scripts\bench.ps1 -Compare bench\results\20261113-170000_short
# Results go to bench\results\<timestamp>_<suite>\ (results.json, summary.md, screenshots). Saves, prefs and Steam
# stats are blocked in-game for the whole run.
param(
    [string[]] $Suite = 'short',
    [ValidateSet('d3d12', 'd3d11')] [string] $Api = 'd3d12',
    # One earlier results folder (or name under bench\results) per suite, in order; a single one is compared against every suite.
    [string[]] $Compare,
    [double] $Scale = 0.5,
    [switch] $NoGpuTimer,
    # Unity 2022.3's native D3D12 graphics jobs deadlock when unloading big levels (e.g. leaving 8-3). Legacy jobs don't; same speed.
    [switch] $NativeGfxJobs,
    # Force one pose-load method for every scenario: checkpoint, approach-fast, approach, areas, none.
    [string] $Load,
    # Load the level for every scenario instead of reusing it along shared routes (the behaviour before chaining).
    [switch] $NoChain,
    # Instead of measuring: reach every pose with each load method and compare the loaded scene (see bench\README.md).
    [switch] $ValidateLoad,
    # With -ValidateLoad: comma-separated methods to compare, first one is the reference. "approach-fast@3" = at game speed 3;
    # "chain" = reached the way a chained benchmark reaches it.
    [string] $ValidateMethods,
    [string[]] $Extra = @(),
    # Default: the folder of -Slot.
    [string] $GameDir,
    # Per run; with several suites the default is this times the number of suites.
    [int] $TimeoutSec = 900,
    # main = the Steam install, alt = the copy for visual/diagnostic runs (see game-lock.ps1 for who may use which).
    [ValidateSet('main', 'alt')] [string] $Slot = 'main',
    # Skip the per-mode screenshots / the temporal noise bursts of every scenario. -Perf = both (frame-time runs).
    [switch] $NoScreenshots,
    [switch] $NoBursts,
    [switch] $Perf,
    # Seconds to settle after each level load instead of each scenario's own Settle (3 s unless a suite says otherwise).
    [double] $Settle = -1,
    # Keep one game running at the main menu between calls (shader-only and config-only iterations skip the launch).
    # A changed UltraRT.dll or different launch switches (-Scale, -Api, -Extra ...) restart it. Quits itself after 20 idle minutes.
    [switch] $KeepOpen,
    # Quit the resident game of this slot and exit.
    [switch] $Stop,
    # Record a system-wide CPU trace (Windows Performance Recorder) over the launch: trace.etl in the first suite's
    # results folder, analysed by tools\perf\etw_report.py. Needs scripts\etw-setup.ps1 once; one launch only (no
    # -KeepOpen). Keep the suite short: a trace grows by tens of MB per second. docs/performance.md, "ETW".
    [switch] $Etw,
    # The CPU floor: -Perf at trace scale 0.2, where the GPU work of the RT passes is small and every pass still runs,
    # so the frame time is what the main and render threads cost (docs/performance.md, "Profiling").
    [switch] $Floor
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $GameDir) { $GameDir = Get-SlotDir $Slot }
if ($Floor) { $Perf = $true; $Scale = 0.2 }
if ($Perf) { $NoScreenshots = $true; $NoBursts = $true }

if ($Stop) {
    if (Get-Resident $GameDir) { Stop-Resident $GameDir; "Quit the resident game of $GameDir" } else { "No resident game in $GameDir" }
    $restored = Restore-DisplayPrefs (Get-SlotDir 'main'); if ($restored) { "Restored the user's display settings ($restored)" }
    return
}

# ---------------------------------------------------------------- suites and output folders
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runs = @()
foreach ($s in @($Suite | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $path = if (Test-Path $s) { (Resolve-Path $s).Path } else { Join-Path $root "bench\suites\$s.json" }
    if (-not (Test-Path $path)) { throw "Suite not found: $path" }
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    # Created at once and atomically: two runs started in the same second (other slot, other agent) never share a folder.
    $base = "bench\results\${stamp}_$name" + $(if ($Slot -eq 'alt') { '_alt' } else { '' })
    $out = Join-Path $root $base
    for ($n = 2; ; $n++) {
        try { New-Item -ItemType Directory -Path $out -ErrorAction Stop | Out-Null; break }
        catch { if ($n -gt 50) { throw }; $out = Join-Path $root "${base}_$n" }
    }
    $runs += [pscustomobject]@{ Name = $name; Path = $path; Out = $out; Summary = (Join-Path $out 'summary.md') }
}
if (-not $runs) { throw 'No suite given' }

$commit = (git -C $root rev-parse --short HEAD 2>$null)
if (git -C $root status --porcelain 2>$null) { $commit += '-dirty' }

# ---------------------------------------------------------------- who else is on the machine
$procs = Get-GameProcs
$resident = Get-Resident $GameDir
$mainNorm = Get-NormPath (Get-SlotDir 'main')
$user = @($procs | Where-Object { -not $_.Agent -and ((-not $_.Dir) -or (Get-NormPath $_.Dir) -eq $mainNorm) })
if ($user.Count -gt 0) { throw "The user is playing ULTRAKILL (pid $($user[0].Pid)); not starting a game. Wait for it to end, never kill it." }
$here = @($procs | Where-Object { (Get-NormPath $_.Dir) -eq (Get-NormPath $GameDir) -and (-not $resident -or $_.Pid -ne $resident.Pid) })
if ($here.Count -gt 0) { throw "ULTRAKILL is already running in $GameDir (pid $($here[0].Pid)), and it is not a resident game of bench.ps1 -KeepOpen. Not starting a second one." }
$others = @($procs | Where-Object { (Get-NormPath $_.Dir) -ne (Get-NormPath $GameDir) })
if ($others.Count -gt 0) {
    $msg = "Another ULTRAKILL is running ($(@($others | ForEach-Object { $_.Dir }) -join ', ')): frame times of this run are not exclusive."
    if ($Perf) { Write-Warning $msg } elseif ($NoScreenshots -and $NoBursts) { Write-Warning $msg }
}
# Activity on the lock keeps the hold from looking stale (game-lock.ps1 -HoldMaxMin).
$lockDir = Join-Path (Get-SlotDir 'main') 'BepInEx\ultrart'
$lockFile = Join-Path $lockDir $(if ($Slot -eq 'alt') { 'game-alt.lock' } else { 'game.lock' })
if (Test-Path $lockFile) { (Get-Item $lockFile).LastWriteTime = Get-Date }
else { Write-Warning "The $Slot slot is not locked: game-lock.ps1 acquire <owner> first, other agents may be using it." }

# ---------------------------------------------------------------- arguments
$fixed = @("-force-$Api", '-ultrart-scale', $Scale.ToString([Globalization.CultureInfo]::InvariantCulture))
if (-not $NoGpuTimer) { $fixed += '-ultrart-gputimer' }
if (-not $NativeGfxJobs) { $fixed += @('-force-gfx-jobs', 'legacy') }
# The alt install writes its own Unity log: Player.log under LocalLow is shared with the main install.
if ($Slot -eq 'alt') { $fixed += @('-logFile', "`"$(Join-Path $GameDir 'Player.log')`"") }
if ($Extra) { $fixed += @($Extra | Where-Object { $_ }) }

$runArgs = @('-ultrart-bench-tag', $commit)
if ($Load) { $runArgs += @('-ultrart-bench-load', $Load) }
if ($NoChain) { $runArgs += '-ultrart-bench-nochain' }
if ($ValidateLoad) { $runArgs += '-ultrart-bench-validate' }
if ($ValidateMethods) { $runArgs += @('-ultrart-bench-validate-methods', $ValidateMethods) }
if ($NoScreenshots) { $runArgs += '-ultrart-bench-noshots' }
if ($NoBursts) { $runArgs += '-ultrart-bench-noburst' }
if ($Settle -ge 0) { $runArgs += @('-ultrart-bench-settle', $Settle.ToString([Globalization.CultureInfo]::InvariantCulture)) }

$exe = Join-Path $GameDir 'ULTRAKILL.exe'
$limit = if ($PSBoundParameters.ContainsKey('TimeoutSec')) { $TimeoutSec } else { $TimeoutSec * $runs.Count }
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = $null
$id = $null
$srv = Get-ServerDir $GameDir
# BepInEx rewrites LogOutput.log at every launch, so each run keeps its own copy (game.log in the results folder).
$gameLog = Join-Path $GameDir 'BepInEx\LogOutput.log'
$logStart = 0
function Save-GameLog([string] $dest, [long] $from) {
    try {
        $fs = [IO.File]::Open($gameLog, 'Open', 'Read', 'ReadWrite')
        try {
            if ($from -gt $fs.Length) { $from = 0 }   # the game restarted and the log began again
            $null = $fs.Seek($from, 'Begin')
            $out = [IO.File]::Create($dest); try { $fs.CopyTo($out) } finally { $out.Close() }
        } finally { $fs.Close() }
    } catch { Write-Warning "Could not copy the game log to ${dest}: $($_.Exception.Message)" }
}

if ($Etw -and ($KeepOpen -or $resident)) { throw '-Etw records one launch: not with -KeepOpen or a resident game (bench.ps1 -Stop first).' }
if ($KeepOpen -or $resident) {
    # ---- resident game: launch once, hand it commands
    $sig = ($fixed -join ' ')
    $dllFile = Join-Path $GameDir 'BepInEx\plugins\UltraRT\UltraRT.dll'
    if ($resident) {
        $l = Read-JsonFile (Join-Path $srv 'launch.json')
        $dllTicks = if (Test-Path $dllFile) { (Get-Item $dllFile).LastWriteTimeUtc.Ticks } else { 0 }
        if (-not $l -or $l.sig -ne $sig -or $resident.Dll -ne $dllTicks) {
            Write-Host 'The resident game has another build or other launch switches: restarting it.'
            Stop-Resident $GameDir
            $resident = $null
        }
    }
    if (-not $resident) {
        New-Item -ItemType Directory -Force $srv | Out-Null
        Get-ChildItem $srv -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-JsonFile (Join-Path $srv 'launch.json') @{ sig = $sig; started = (Get-Date).ToString('s') }
        $p = Start-Process $exe -ArgumentList (@($fixed) + @('-ultrart-bench-server', "`"$srv`"", '-ultrart-bench-idle', '20')) -WorkingDirectory $GameDir -PassThru
        $boot = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $resident = Get-Resident $GameDir
            if ($resident -and $resident.State -eq 'idle') { break }
            if ($p.HasExited) { throw "The game exited while starting (exit code $($p.ExitCode)); see $(Join-Path $GameDir 'BepInEx\LogOutput.log')" }
            if ($boot.Elapsed.TotalSeconds -gt 180) { Stop-Process -Id $p.Id -Force; throw 'The game did not reach the main menu in 180 s; killed it.' }
            Start-Sleep -Milliseconds 300
        }
        "Game up in $([int]$boot.Elapsed.TotalSeconds)s (resident, pid $($resident.Pid))"
        $sw.Restart()
    }
    $p = $resident.Process
    # The resident game keeps appending to one log: remember where this run's part starts.
    # A game launched just now: keep its start-up lines too.
    $logStart = if ($boot) { 0 } else { try { (Get-Item $gameLog -ErrorAction Stop).Length } catch { 0 } }
    $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $cmd = @{ Id = $id; Runs = @($runs | ForEach-Object { @{ Suite = $_.Path; Out = $_.Out } }); Tag = $commit
              NoChain = [bool] $NoChain; Validate = [bool] $ValidateLoad; NoShots = [bool] $NoScreenshots; NoBursts = [bool] $NoBursts; Settle = $Settle }
    if ($Load) { $cmd.Load = $Load }
    if ($ValidateMethods) { $cmd.ValidateMethods = $ValidateMethods }
    Write-JsonFile (Join-Path $srv ("cmd-{0:yyyyMMddHHmmssfff}.json" -f (Get-Date))) $cmd
    $doneFile = Join-Path $srv "done-$id.json"
    $deadline = (Get-Date).AddSeconds($limit)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline -and -not (Test-Path $doneFile)) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $doneFile)) {
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Warning "Timed out after $limit s; killed the resident game. Partial results (if any) under bench\results." }
        else { Write-Warning 'The resident game exited during the run.' }
        Remove-Item (Join-Path $srv 'ready.json') -Force -ErrorAction SilentlyContinue
    } elseif (-not $KeepOpen) { Stop-Resident $GameDir }
} else {
    # ---- one launch, quits by itself
    $gameArgs = @("-force-$Api")
    foreach ($r in $runs) { $gameArgs += @('-ultrart-bench', "`"$($r.Path)`"", '-ultrart-bench-out', "`"$($r.Out)`"") }
    $gameArgs += $runArgs
    $gameArgs += @($fixed | Select-Object -Skip 1)
    $gameArgs += '-ultrart-quit'
    $runs | ForEach-Object { New-Item -ItemType Directory -Force $_.Out | Out-Null }
    $tracing = $false
    if ($Etw) { & (Join-Path $PSScriptRoot 'etw.ps1') start; $tracing = $true }
    try {
        $p = Start-Process $exe -ArgumentList $gameArgs -WorkingDirectory $GameDir -PassThru
        $deadline = (Get-Date).AddSeconds($limit)
        $last = $runs[-1].Summary
        while (-not $p.HasExited -and (Get-Date) -lt $deadline -and -not (Test-Path $last)) { Start-Sleep -Milliseconds 500 }
        # The trace ends with the measurements: the game's exit is not part of it.
        if ($tracing) { $tracing = $false; & (Join-Path $PSScriptRoot 'etw.ps1') stop -Out (Join-Path $runs[0].Out 'trace.etl') }
        # The summary is the last thing written. Give the game a moment to quit by itself; it sometimes hangs on exit.
        if (-not $p.HasExited -and -not $p.WaitForExit(20000)) {
            Stop-Process -Id $p.Id -Force
            if (Test-Path $last) { Write-Warning 'Game did not quit after finishing; killed it (results are complete).' }
            else { Write-Warning "Timed out after $limit s; killed. Partial results (if any) in $($runs[-1].Out)" }
        }
    }
    finally {
        # Never leave a trace recording.
        if ($tracing) { try { & (Join-Path $PSScriptRoot 'etw.ps1') cancel } catch { Write-Warning "ETW cancel failed: $($_.Exception.Message)" } }
    }
}
if (Test-Path $lockFile) { (Get-Item $lockFile).LastWriteTime = Get-Date }
foreach ($r in $runs) { New-Item -ItemType Directory -Force $r.Out | Out-Null; Save-GameLog (Join-Path $r.Out 'game.log') $logStart }
"Game log saved as game.log in the results folder$(if ($runs.Count -gt 1) { 's' })"
# Unity saved this game's window monitor/resolution into the registry the user's game reads: put the user's back.
$restored = Restore-DisplayPrefs (Get-SlotDir 'main')
if ($restored) { "Restored the user's display settings ($restored)" }
if ($runs.Count -eq 1) { "Ran $($runs[0].Name) in $([int]$sw.Elapsed.TotalSeconds)s -> $($runs[0].Out)" }
else {
    foreach ($r in $runs) { "Ran $($r.Name) -> $($r.Out)" }
    "Ran $($runs.Count) suites in one launch in $([int]$sw.Elapsed.TotalSeconds)s"
}

# ---------------------------------------------------------------- summaries
$failed = $false
$i = 0
foreach ($r in $runs) {
    $outDir = $r.Out
    if ($runs.Count -gt 1) { ""; "# $($r.Name)" }
    if (Test-Path $r.Summary) { Get-Content $r.Summary -Encoding UTF8 } else {
        Write-Warning "No summary written for $($r.Name). Last plugin log lines:"
        Get-Content (Join-Path $outDir 'game.log') -ErrorAction SilentlyContinue | Select-String 'UltraRT\]' | Select-Object -Last 15 | ForEach-Object Line
        $failed = $true
        $i++
        continue
    }

    $cmp = if ($Compare) { $Compare[[math]::Min($i, $Compare.Count - 1)] } else { $null }
    $i++
    if ($cmp) {
        $baseFile = if (Test-Path (Join-Path $cmp 'results.json')) { Join-Path $cmp 'results.json' } else { Join-Path $root "bench\results\$cmp\results.json" }
        $base = Get-Content $baseFile -Raw | ConvertFrom-Json
        $cur = Get-Content (Join-Path $outDir 'results.json') -Raw | ConvertFrom-Json
        ""
        "## vs $($base.Tag) ($($base.Started))"
        "| Scenario | Mode | Avg ms (base -> now) | delta ms | fps (base -> now) | GPU total (base -> now) |"
        "|---|---|---|---|---|---|"
        foreach ($sc in $cur.Scenarios) {
            $b = $base.Scenarios | Where-Object Name -eq $sc.Name | Select-Object -First 1
            if (-not $b) { continue }
            foreach ($m in $sc.Modes.PSObject.Properties) {
                $bm = $b.Modes.PSObject.Properties[$m.Name]
                if (-not $bm) { continue }
                $n = $m.Value; $o = $bm.Value
                $gpuN = if ($n.GpuMs.total) { '{0:F3}' -f $n.GpuMs.total } else { '-' }
                $gpuO = if ($o.GpuMs.total) { '{0:F3}' -f $o.GpuMs.total } else { '-' }
                '| {0} | {1} | {2:F3} -> {3:F3} | {4:+0.000;-0.000} | {5:F0} -> {6:F0} | {7} -> {8} |' -f $sc.Name, $m.Name, $o.AvgMs, $n.AvgMs, ($n.AvgMs - $o.AvgMs), $o.AvgFps, $n.AvgFps, $gpuO, $gpuN
            }
        }
    }
}
if ($failed) { exit 1 }
