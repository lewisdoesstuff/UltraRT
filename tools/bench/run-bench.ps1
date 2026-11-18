# One bench run inside one lock hold: acquire -> deploy <repo>\stage to the slot -> bench.ps1 -> release (always).
# Build first (scripts\build.ps1 -NoDeploy). Run it in the background and wait for it; it prints LOCK / DEPLOY / BENCH /
# RELEASE lines. -Kind visual takes any free slot (alt first), -Kind perf both (exclusive; add -Perf for frame times).
# -Extra takes the game's extra arguments comma separated in ONE string: -Extra "-ultrart-viewprobe,-ultrart-light-census"
# (they are re-quoted one by one for bench.ps1; a plain array does not survive the nested powershell call).
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\bench\run-bench.ps1 -Owner me -Suites "a,b" -Kind visual -TimeoutSec 1500
param([string] $Owner = 'plan-owner', [string] $Suites = 'perf-1440p-short,plan-perf', [string] $Kind = 'perf', [switch] $Perf, [int] $TimeoutSec = 900, [string] $Extra = "")
$ErrorActionPreference = 'Continue'
# The worktree this script is in, and the main checkout's lock script (bench/README.md: everyone runs the same lock).
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lock = $(if ($env:ULTRART_LOCK) { $env:ULTRART_LOCK } else { 'C:\Users\death\.t3\projects\ultrart\scripts\game-lock.ps1' })
Set-Location $repo
$acq = & powershell -NoProfile -ExecutionPolicy Bypass -File $lock acquire $Owner -Kind $Kind -TimeoutMin 30
$acq | ForEach-Object { "LOCK: $_" }
if ($LASTEXITCODE -ne 0) { "acquire failed ($LASTEXITCODE)"; exit 1 }
$slot = 'main'
foreach ($l in $acq) { if ($l -match '^slot:\s*(main|alt)') { $slot = $Matches[1] } }
"SLOT: $slot"
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\deploy.ps1 -Slot $slot -Owner $Owner | ForEach-Object { "DEPLOY: $_" }
    if ($LASTEXITCODE -ne 0) { "deploy failed ($LASTEXITCODE)"; return }
    if ($Extra) {
        $cmd = "& .\scripts\bench.ps1 -Suite '$Suites' -Slot $slot -TimeoutSec $TimeoutSec " + $(if ($Perf) { '-Perf ' } else { '' }) + "-Extra " + ((($Extra -split ',') | ForEach-Object { "'$_'" }) -join ',')
        & powershell -NoProfile -ExecutionPolicy Bypass -Command $cmd | ForEach-Object { "BENCH: $_" }
    }
    else {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '.\scripts\bench.ps1', '-Suite', $Suites, '-Slot', $slot, '-TimeoutSec', $TimeoutSec)
        if ($Perf) { $a += "-Perf" }
        & powershell @a | ForEach-Object { "BENCH: $_" }
    }
    "bench exit $LASTEXITCODE"
}
finally {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $lock release $Owner | ForEach-Object { "RELEASE: $_" }
}
