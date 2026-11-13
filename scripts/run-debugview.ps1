# Launches ULTRAKILL on DX12 with UltraRT, loads a level, saves the game frame plus every debug-view mode, quits.
# Usage: .\scripts\run-debugview.ps1 -Scene "Level 1-1"
param(
    [string] $Scene = 'Level 1-1',
    [double] $Scale = 0.5,
    [switch] $GpuTimer,
    [string[]] $Extra = @(),
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [int] $TimeoutSec = 240
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameDir 'ULTRAKILL.exe'
$outDir = Join-Path $GameDir 'BepInEx\ultrart'
$started = Get-Date

$gameArgs = @('-force-d3d12', '-ultrart-scene', "`"$Scene`"", '-ultrart-scale', $Scale.ToString([Globalization.CultureInfo]::InvariantCulture), '-ultrart-capture', '-ultrart-quit')
if ($Extra) { $gameArgs += @($Extra | Where-Object { $_ }) }
if ($GpuTimer) { $gameArgs += '-ultrart-gputimer' }
$p = Start-Process $exe -ArgumentList $gameArgs -WorkingDirectory $GameDir -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Stop-Process -Id $p.Id -Force
    Write-Warning "Timed out after $TimeoutSec s; killed."
}

Get-Content (Join-Path $GameDir 'BepInEx\LogOutput.log') | Select-String '\[(Info|Warning|Error|Fatal)\s*:\s*UltraRT\]' | ForEach-Object Line
Get-ChildItem $outDir -Filter 'dv-*.png' | Where-Object { $_.LastWriteTime -ge $started } | ForEach-Object FullName
