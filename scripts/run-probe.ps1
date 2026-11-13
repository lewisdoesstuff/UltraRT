# Launches ULTRAKILL unattended with the UltraRT probe: loads a level, benchmarks, dumps the scene, quits.
# Usage: .\scripts\run-probe.ps1 -Api d3d12 -Scene "Level 1-1"
param(
    [ValidateSet('d3d11', 'd3d12')] [string] $Api = 'd3d12',
    [string] $Scene = 'Level 1-1',
    [int] $Settle = 6,
    [int] $Bench = 10,
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [int] $TimeoutSec = 240
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameDir 'ULTRAKILL.exe'
$outDir = Join-Path $GameDir 'BepInEx\ultrart'
$started = Get-Date

$gameArgs = @("-force-$Api", '-ultrart-probe', '-ultrart-off', '-ultrart-scene', "`"$Scene`"", '-ultrart-settle', $Settle, '-ultrart-bench', $Bench, '-ultrart-quit')
$p = Start-Process $exe -ArgumentList $gameArgs -WorkingDirectory $GameDir -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Stop-Process -Id $p.Id -Force
    Write-Warning "Timed out after $TimeoutSec s; killed. See $GameDir\BepInEx\LogOutput.log"
}

$report = Get-ChildItem $outDir -Filter 'report-*.md' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime | Select-Object -Last 1
if ($report) { $report.FullName } else { Write-Warning 'No report written.'; exit 1 }
