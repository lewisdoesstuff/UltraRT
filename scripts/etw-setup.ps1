<#
.SYNOPSIS
One-time setup for CPU profiling of the game with Windows Performance Recorder (docs/performance.md, "ETW").

.DESCRIPTION
A kernel CPU trace needs administrator rights. This script asks for them ONCE and registers three scheduled tasks
that run wpr.exe elevated with fixed arguments:

  UltraRT ETW start    wpr -start CPU -filemode
  UltraRT ETW stop     wpr -stop <OutDir>\ultrart.etl
  UltraRT ETW cancel   wpr -cancel

scripts\etw.ps1 then starts and stops traces through them, with no further prompt. The tasks run nothing but wpr.exe
from System32 with these arguments (no script, nothing a user process could swap), so all they hand out is the right
to start and stop a system-wide CPU trace. The trace file stays on this machine.

  .\scripts\etw-setup.ps1            register (one UAC prompt)
  .\scripts\etw-setup.ps1 -Remove    delete the tasks again (one UAC prompt)
#>
param(
    [switch]$Remove,
    [string]$OutDir = 'D:\UltraRT-Bench\etw',
    [string]$User = ''
)
$ErrorActionPreference = 'Stop'
$names = @('UltraRT ETW start', 'UltraRT ETW stop', 'UltraRT ETW cancel')

$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    # Relaunch elevated, carrying the account the tasks are for (an elevation with other credentials changes identity).
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-OutDir', "`"$OutDir`"", '-User', "`"$($me.Name)`"")
    if ($Remove) { $argList += '-Remove' }
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden
    Write-Host "etw-setup: elevated run exited with $($p.ExitCode)"
    exit $p.ExitCode
}

try {
    if ($Remove) {
        foreach ($n in $names) {
            if (Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $n -Confirm:$false }
        }
        exit 0
    }
    if (-not $User) { $User = $me.Name }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $wpr = Join-Path $env:WINDIR 'System32\wpr.exe'
    if (-not (Test-Path $wpr)) { throw "wpr.exe not found at $wpr" }
    # S4U: runs without a window and without a stored password; Highest: elevated.
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType S4U -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew
    $actions = @{
        'UltraRT ETW start'  = '-start CPU -filemode'
        'UltraRT ETW stop'   = "-stop `"$(Join-Path $OutDir 'ultrart.etl')`""
        'UltraRT ETW cancel' = '-cancel'
    }
    foreach ($n in $names) {
        $action = New-ScheduledTaskAction -Execute $wpr -Argument $actions[$n]
        Register-ScheduledTask -TaskName $n -Action $action -Principal $principal -Settings $settings -Force `
            -Description 'UltraRT: CPU profiling of the game (scripts\etw-setup.ps1 -Remove deletes this task).' | Out-Null
    }
    exit 0
}
catch {
    $_ | Out-String | Set-Content -Path (Join-Path $env:TEMP 'ultrart-etw-setup-error.txt') -Encoding utf8
    exit 1
}
