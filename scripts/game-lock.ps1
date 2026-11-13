# Serialises use of the single ULTRAKILL install between agents (and the user).
#   .\scripts\game-lock.ps1 acquire <owner>   waits until the game is free, then claims it
#   .\scripts\game-lock.ps1 release <owner>   restores main's build into the game and frees it
# Rules: never deploy into or launch the game without holding the lock; never kill an ULTRAKILL process you
# didn't start (the user may be playing); always release, even on failure.
param(
    [Parameter(Mandatory)] [ValidateSet('acquire', 'release', 'status')] [string] $Action,
    [string] $Owner = $env:USERNAME,
    [int] $TimeoutMin = 90,
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [string] $MainRepo = 'C:\Users\death\.t3\projects\ultrart'
)

$ErrorActionPreference = 'Stop'
$lock = Join-Path $GameDir 'BepInEx\ultrart\game.lock'
New-Item -ItemType Directory -Force (Split-Path $lock) | Out-Null

function Get-LockInfo {
    if (-not (Test-Path $lock)) { return $null }
    try { return Get-Content $lock -Raw | ConvertFrom-Json } catch { return $null }
}

switch ($Action) {
    'status' {
        $i = Get-LockInfo
        if ($i) { "locked by $($i.owner) since $($i.since)" } else { 'free' }
        if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { 'ULTRAKILL is running' }
    }
    'acquire' {
        $deadline = (Get-Date).AddMinutes($TimeoutMin)
        while ($true) {
            $i = Get-LockInfo
            $stale = $i -and ((Get-Date) - [datetime]$i.since).TotalMinutes -gt 60
            $running = [bool](Get-Process ULTRAKILL -ErrorAction SilentlyContinue)
            if ((-not $i -or $stale) -and -not $running) {
                try {
                    if ($stale) { Remove-Item $lock -Force }
                    $fs = [IO.File]::Open($lock, 'CreateNew', 'Write')
                    $b = [Text.Encoding]::ASCII.GetBytes((@{ owner = $Owner; since = (Get-Date).ToString('s') } | ConvertTo-Json -Compress))
                    $fs.Write($b, 0, $b.Length); $fs.Close()
                    "acquired by $Owner"
                    return
                } catch { }
            }
            if ((Get-Date) -gt $deadline) { throw "Timed out waiting for the game (lock: $(if ($i) { $i.owner } else { 'none' }); running: $running)" }
            Start-Sleep -Seconds 30
        }
    }
    'release' {
        $i = Get-LockInfo
        if ($i -and $i.owner -ne $Owner) { throw "Lock is held by $($i.owner), not $Owner" }
        if (-not (Get-Process ULTRAKILL -ErrorAction SilentlyContinue)) {
            & (Join-Path $MainRepo 'scripts\build.ps1') -SkipNative | Out-Null
            'restored main build'
        } else { Write-Warning 'ULTRAKILL is running; main build not restored' }
        Remove-Item $lock -Force -ErrorAction SilentlyContinue
        "released by $Owner"
    }
}
