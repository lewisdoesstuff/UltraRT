<#
.SYNOPSIS
Starts or stops a system-wide CPU trace (Windows Performance Recorder) through the scheduled tasks that
scripts\etw-setup.ps1 registered once. No elevation prompt. docs/performance.md, "ETW".

  .\scripts\etw.ps1 start
  .\scripts\etw.ps1 stop -Out bench\results\<run>\trace.etl     (writes and moves the trace; can take a minute)
  .\scripts\etw.ps1 cancel                                       (drops a running trace)

A trace records every process on the machine while it runs: keep it to the seconds that are measured, and keep the
file out of the repo (bench\results is gitignored).
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('start', 'stop', 'cancel')][string]$Action,
    [string]$Out = '',
    # Where the stop task writes ultrart.etl. Default: read from the registered 'UltraRT ETW stop' task (etw-setup.ps1 -OutDir).
    [string]$EtwDir = '',
    [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
$task = "UltraRT ETW $Action"
if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
    throw "Scheduled task '$task' is missing: run scripts\etw-setup.ps1 once (it asks for elevation)."
}
if ($EtwDir) { $etl = Join-Path $EtwDir 'ultrart.etl' }
else {
    $stopArgs = ''
    $stopTask = Get-ScheduledTask -TaskName 'UltraRT ETW stop' -ErrorAction SilentlyContinue
    if ($stopTask) { $stopArgs = [string] @($stopTask.Actions)[0].Arguments }
    $m = [regex]::Match($stopArgs, '-stop\s+"([^"]+)"')
    if (-not $m.Success) { throw "Cannot read the trace path from the 'UltraRT ETW stop' task ($stopArgs): run scripts\etw-setup.ps1 again or pass -EtwDir" }
    $etl = $m.Groups[1].Value
}
if ($Action -eq 'stop' -and (Test-Path $etl)) { Remove-Item $etl -Force }

$before = (Get-ScheduledTaskInfo -TaskName $task).LastRunTime
Start-ScheduledTask -TaskName $task
$deadline = (Get-Date).AddSeconds($TimeoutSec)
do {
    Start-Sleep -Milliseconds 300
    $state = (Get-ScheduledTask -TaskName $task).State
    $info = Get-ScheduledTaskInfo -TaskName $task
} while (($state -eq 'Running' -or $info.LastRunTime -eq $before) -and (Get-Date) -lt $deadline)
if ($state -eq 'Running') { throw "'$task' still running after $TimeoutSec s" }
$code = $info.LastTaskResult
if ($code -ne 0) {
    # wpr's own codes: 0xc5583001 = already recording (start), 0xc5583000 = not recording (stop / cancel).
    throw ("'{0}' failed: 0x{1:X8}" -f $task, $code)
}
if ($Action -eq 'stop') {
    if (-not (Test-Path $etl)) { throw "wpr reported success but $etl is missing" }
    if ($Out) {
        $dir = Split-Path -Parent $Out
        if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
        Move-Item $etl $Out -Force
        $etl = $Out
    }
    Write-Host ("ETW: trace written, {0:N0} MB: {1}" -f ((Get-Item $etl).Length / 1MB), $etl)
}
else { Write-Host "ETW: $Action ok" }
