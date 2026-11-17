# Copies a staged build (scripts\build.ps1 -NoDeploy) into a game install: seconds, only changed files.
# Usage (after .\scripts\game-lock.ps1 acquire <owner>):
#   .\scripts\deploy.ps1                    stage -> main install
#   .\scripts\deploy.ps1 -Slot alt          stage -> the alt install (visual/diagnostic runs)
#   .\scripts\deploy.ps1 -Owner me          required when the slot is locked: refuses unless the lock is 'me''s
# A changed shader bundle gets a fresh timestamp, so a running game hot-reloads it. A running game keeps its
# UltraRT.dll / UltraRT.Native.dll loaded: those need a relaunch (a warning says so).
param(
    [ValidateSet('main', 'alt')] [string] $Slot = 'main',
    [string] $GameDir,
    [string] $Stage,
    [string] $Owner,
    [string] $LockDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $GameDir) { $GameDir = Get-SlotDir $Slot }
if (-not $Stage) { $Stage = Join-Path $root 'stage' }
if (-not (Test-Path (Join-Path $Stage 'UltraRT.dll'))) { throw "No staged build in $Stage (run scripts\build.ps1 -NoDeploy first)" }
if (-not (Test-Path $GameDir)) { throw "Game folder not found: $GameDir" }

if (-not $LockDir) { $LockDir = Join-Path (Get-SlotDir 'main') 'BepInEx\ultrart' }
$lockFile = Join-Path $LockDir $(if ($Slot -eq 'alt') { 'game-alt.lock' } else { 'game.lock' })
$held = Read-JsonFile $lockFile
if ($Owner -and (-not $held -or $held.owner -ne $Owner)) { throw "The $Slot slot is not locked by $Owner (lock: $(if ($held) { $held.owner } else { 'none' })); run game-lock.ps1 acquire first" }
if ($held -and -not $Owner) { throw "The $Slot slot is held by $($held.owner): pass -Owner <you> (deploy.ps1 -Slot $Slot -Owner <you>) so a deploy can't land in another agent's slot. Your slot: game-lock.ps1 slot <you>" }
if (-not $held) { Write-Warning "The $Slot slot is not locked: run game-lock.ps1 acquire first (other agents may be using it)" }
else { (Get-Item $lockFile).LastWriteTime = Get-Date }   # activity: keeps the hold from looking stale

# A running game keeps UltraRT.dll mapped, so a changed one cannot be copied: stop our own resident game first (bench.ps1 -KeepOpen).
if (Get-Resident $GameDir) {
    foreach ($n in 'UltraRT.dll', 'UltraRT.Native.dll') {
        $s = Join-Path $Stage $n; $d = Join-Path $GameDir "BepInEx\plugins\UltraRT\$n"
        if ((Test-Path $s) -and (Test-Path $d) -and (Get-QuickHash $s) -ne (Get-QuickHash $d)) { "$n changed: stopping the resident game"; Stop-Resident $GameDir; break }
    }
}
$sw = [Diagnostics.Stopwatch]::StartNew()
$copied = @(Sync-Stage -From $Stage -GameDir $GameDir)
$note = ''
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (Split-Path $_.Path -Parent) -eq $GameDir }) {
    if ($copied -contains 'UltraRT.dll') { $note = ' (a game is running in this slot: it still has the old UltraRT.dll loaded, relaunch it)' }
}
"Deployed to $Slot ($GameDir) in $([int]$sw.ElapsedMilliseconds) ms: $(if ($copied.Count) { $copied -join ', ' } else { 'nothing changed' })$note"
