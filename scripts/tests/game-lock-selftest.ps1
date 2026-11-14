# Self-test of game-lock.ps1 in a scratch folder (fake installs, never the real lock). Takes about a minute.
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\game-lock-selftest.ps1
$ErrorActionPreference = 'Stop'
$lock = Join-Path (Split-Path $PSScriptRoot -Parent) 'game-lock.ps1'
$work = Join-Path $env:TEMP "urt-lock-selftest-$PID"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
$main = Join-Path $work 'main'; $alt = Join-Path $work 'alt'
New-Item -ItemType Directory -Force (Join-Path $main 'BepInEx'), $alt | Out-Null
$fails = 0
function RunLock { param([string[]] $A) $ErrorActionPreference = "Continue"; & powershell -NoProfile -ExecutionPolicy Bypass -File $lock @A -GameDir $main -AltDir $alt -NoRestore -PollSec 1 2>&1 | ForEach-Object { "$_" } }
function BgLock { param([string[]] $A) $o = Join-Path $work ("bg-{0}.txt" -f [guid]::NewGuid().ToString('N').Substring(0, 6))
    Start-Process powershell -WindowStyle Hidden -RedirectStandardOutput $o -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$lock`"") + $A + @('-GameDir', "`"$main`"", '-AltDir', "`"$alt`"", '-NoRestore', '-PollSec', '1')) | Out-Null
    return $o }
function Check($name, $cond) { if ($cond) { "ok   $name" } else { "FAIL $name"; $script:fails++ } }
function WaitFor($cond, $sec = 15) { $t = Get-Date; while (-not (& $cond) -and ((Get-Date) - $t).TotalSeconds -lt $sec) { Start-Sleep -Milliseconds 400 }; & $cond }

Check 'status free' ((RunLock @('status'))[0] -eq 'free')
Check 'acquire a' ((RunLock @('acquire', 'a'))[0] -eq 'acquired by a')
Check 'status locked' ((RunLock @('status'))[0] -like 'locked by a since *')
Check 'slot a' ((RunLock @('slot', 'a')) -eq 'main')
$o1 = BgLock @('acquire', 'b', '-TimeoutMin', '2'); Start-Sleep -Milliseconds 1500
$o2 = BgLock @('acquire', 'b', '-TimeoutMin', '2'); Start-Sleep -Milliseconds 1500
$o3 = BgLock @('acquire', 'c', '-Kind', 'visual', '-Slot', 'alt', '-TimeoutMin', '2'); Start-Sleep -Milliseconds 1500
$o4 = BgLock @('acquire', 'd', '-Kind', 'visual', '-Slot', 'main', '-TimeoutMin', '2'); Start-Sleep -Seconds 3
$st = RunLock @('status')
Check 'queue lists b c d once each' (($st | Where-Object { $_ -match '^\s+\d\. ' }).Count -eq 3)
Check 'one ticket for b' (@($st | Where-Object { $_ -match '^\s+\d\. b ' }).Count -eq 1)
Check 'c waits behind perf b' ((Get-Content $o3 -Raw) -match 'behind b')
RunLock @('release', 'a') | Out-Null
Check 'b gets it first' (WaitFor { (Get-Content $o1 -Raw) -match 'acquired by b' })
Check 'second acquire of b returns' (WaitFor { (Get-Content $o2 -Raw) -match 'acquired by b' })
Check 'c not yet' (-not ((Get-Content $o3 -Raw) -match 'acquired by c'))
RunLock @('release', 'b') | Out-Null
Check 'c gets alt' (WaitFor { (Get-Content $o3 -Raw) -match 'acquired by c' })
Check 'd gets main meanwhile' (WaitFor { (Get-Content $o4 -Raw) -match 'acquired by d' })
Check 'slot c is alt' ((RunLock @('slot', 'c')) -eq 'alt')
$o5 = BgLock @('acquire', 'e', '-TimeoutMin', '2'); Start-Sleep -Seconds 3
Check 'perf e waits for both' (-not ((Get-Content $o5 -Raw) -match 'acquired by e'))
RunLock @('release', 'c') | Out-Null
Start-Sleep -Seconds 2
Check 'perf e still waits for d' (-not ((Get-Content $o5 -Raw) -match 'acquired by e'))
RunLock @('release', 'd') | Out-Null
Check 'perf e acquires both' (WaitFor { (Get-Content $o5 -Raw) -match 'acquired by e' })
Check 'status shows alt held' ((RunLock @('status')) -match 'slot alt: held by e')
Check 'release by a stranger refused' ((RunLock @('release', 'zed')) -match 'Lock is held by e, not zed')
RunLock @('release', 'e') | Out-Null
Check 'free again' ((RunLock @('status'))[0] -eq 'free')
# A waiter that died loses its ticket.
RunLock @('acquire', 'x') | Out-Null
$o6 = BgLock @('acquire', 'y', '-TimeoutMin', '2'); Start-Sleep -Seconds 3
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'acquire y' -and $_.CommandLine -match [regex]::Escape($work) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Check 'dead waiter loses its ticket' (WaitFor { -not ((RunLock @('status')) -match '^\s+\d\. y ') } 30)
RunLock @('release', 'x') | Out-Null
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($work) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if ($fails) { "$fails FAILED"; exit 1 } else { 'all passed' }
