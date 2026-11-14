# Serialises use of the ULTRAKILL installs between agents (and the user): a FIFO queue in front of two slots.
#   .\scripts\game-lock.ps1 acquire <owner> [-Kind perf|visual] [-Slot main|alt|any] [-TimeoutMin N]
#   .\scripts\game-lock.ps1 release <owner>    stops your resident game, restores main's build (cached copy) and frees the slot(s)
#   .\scripts\game-lock.ps1 status [-Watch]    first line: 'free' or 'locked by <owner> since <time>' (main slot), then a
#                                              slot table (holder, kind, how long, game running there), the queue, recent releases
#   .\scripts\game-lock.ps1 slot <owner>       prints the slot you hold (main or alt)
#   .\scripts\game-lock.ps1 cancel <owner>     leaves the queue
#   .\scripts\game-lock.ps1 cache              rebuilds the cached main build if master's HEAD changed (acquire starts this for you)
#
# Slots: main = the Steam install, alt = a copy (D:\UltraRT-Bench\ULTRAKILL). Kinds:
#   perf   (default) frame-time runs: exclusive, needs BOTH slots and no game of the user's. Slot = where it runs (main).
#   visual screenshots/diagnostics: needs one slot (any = whichever is free, alt first) and no game of the user's.
# Fair: acquire takes a ticket in <lock dir>\lock-queue (one per owner, re-used by a second acquire of the same owner);
# you are served in order, except that tickets that need different slots don't block each other. A waiter that is gone
# (its process ended and 30 s passed) loses its ticket. Poll interval 2 s.
# Rules: never deploy into or launch a game without holding a slot; never kill an ULTRAKILL process you didn't start
# (the user may be playing: a launch without -ultrart-* arguments is the user's); always release, even on failure.
param(
    [Parameter(Mandatory)] [ValidateSet('acquire', 'release', 'status', 'cancel', 'slot', 'cache')] [string] $Action,
    [string] $Owner = $env:USERNAME,
    [int] $TimeoutMin = 90,
    [ValidateSet('perf', 'visual')] [string] $Kind = 'perf',
    [ValidateSet('main', 'alt', 'any')] [string] $Slot,
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [string] $AltDir = $(if ($env:ULTRAKILL_ALT_DIR) { $env:ULTRAKILL_ALT_DIR } else { 'D:\UltraRT-Bench\ULTRAKILL' }),
    [string] $MainRepo = 'C:\Users\death\.t3\projects\ultrart',
    # Where the lock files, the queue and the main-build cache live. Default: <GameDir>\BepInEx\ultrart. Tests use a scratch dir.
    [string] $LockDir,
    [int] $PollSec = 2,
    # A lock whose file and 'since' are both older than this, with no game running in its slot, is taken over.
    [int] $HoldMaxMin = 60,
    [switch] $NoRestore,
    # status only: redraw every PollSec seconds until Ctrl+C.
    [switch] $Watch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $LockDir) { $LockDir = Join-Path $GameDir 'BepInEx\ultrart' }
New-Item -ItemType Directory -Force $LockDir | Out-Null
$qdir = Join-Path $LockDir 'lock-queue'
$cacheDir = Join-Path $LockDir 'main-cache'
$history = Join-Path $LockDir 'lock-history.csv'
$dirs = @{ main = $GameDir; alt = $AltDir }
$altInstalled = Test-Path $AltDir
$slotNames = if ($altInstalled) { @('main', 'alt') } else { @('main') }
if (-not $Slot) { $Slot = if ($Kind -eq 'visual') { 'any' } else { 'main' } }
if ($Kind -eq 'perf' -and $Slot -eq 'any') { $Slot = 'main' }
if ($Slot -eq 'alt' -and -not $altInstalled) { throw "The alt install is not there: $AltDir" }

function LockPath([string] $s) { Join-Path $LockDir $(if ($s -eq 'alt') { 'game-alt.lock' } else { 'game.lock' }) }

function Get-SlotProcs($procs, [string] $s) {
    $d = Get-NormPath $dirs[$s]
    @($procs | Where-Object { (Get-NormPath $_.Dir) -eq $d -or (-not $_.Dir -and $s -eq 'main') })
}

# The user playing: a game of the main install that was not started with -ultrart-* arguments.
function Get-UserGame($procs) {
    @($procs | Where-Object { -not $_.Agent -and ((-not $_.Dir) -or (Get-NormPath $_.Dir) -eq (Get-NormPath $GameDir)) })
}

# The holder of a slot, or $null. A stale one (nobody touched it for HoldMaxMin and no game runs there) is dropped.
function Get-Hold([string] $s, $procs) {
    $p = LockPath $s
    if (-not (Test-Path $p)) { return $null }
    $i = Read-JsonFile $p
    if (-not $i) { $i = [pscustomobject]@{ owner = '?'; since = (Get-Date).ToString('s'); kind = 'perf'; slot = $s } }
    try { $since = [datetime] $i.since } catch { $since = Get-Date }
    $touched = (Get-Item $p -ErrorAction SilentlyContinue).LastWriteTime
    $age = ((Get-Date) - $(if ($touched -and $touched -gt $since) { $touched } else { $since })).TotalMinutes
    if ($age -gt $HoldMaxMin -and (Get-SlotProcs $procs $s).Count -eq 0) {
        Remove-Item $p -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $i
}

# ---------------------------------------------------------------- queue

function Get-Tickets {
    New-Item -ItemType Directory -Force $qdir | Out-Null
    $list = @()
    foreach ($f in @(Get-ChildItem $qdir -Filter '*.json' | Sort-Object Name)) {
        $t = Read-JsonFile $f.FullName
        $age = ((Get-Date) - $f.LastWriteTime).TotalSeconds
        if (-not $t) { if ($age -gt 10) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }; continue }
        $alive = [bool] (Get-Process -Id ([int] $t.pid) -ErrorAction SilentlyContinue)
        $grace = if ($t.grace) { [int] $t.grace } else { 15 }
        if ((-not $alive -and $age -gt $grace) -or $age -gt 300) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue; continue }
        $t | Add-Member -NotePropertyName file -NotePropertyValue $f.FullName -Force
        $list += $t
    }
    return $list
}

function Get-Demand($t) {
    if ($t.kind -eq 'perf' -or $t.slot -eq 'any') { return $slotNames }
    return @($t.slot)
}

function Join-Queue {
    $safe = $Owner -replace '[^A-Za-z0-9._-]', '_'
    $mine = @(Get-Tickets | Where-Object { $_.owner -eq $Owner })
    $obj = @{ owner = $Owner; kind = $Kind; slot = $Slot; since = (Get-Date).ToString('s'); pid = $PID }
    if ($mine.Count -gt 0) {
        # A second acquire by the same owner keeps the first one's place.
        $obj.since = $mine[0].since
        Write-JsonFile $mine[0].file $obj
        return
    }
    $file = Join-Path $qdir ("{0:yyyyMMddHHmmssfff}_{1}.json" -f (Get-Date), $safe)
    Write-JsonFile $file $obj
    # Two acquires of one owner racing: the earlier ticket stays.
    $all = @(Get-Tickets | Where-Object { $_.owner -eq $Owner })
    if ($all.Count -gt 1) { foreach ($x in ($all | Select-Object -Skip 1)) { Remove-Item $x.file -Force -ErrorAction SilentlyContinue } }
}

function Remove-MyTicket { foreach ($t in @(Get-Tickets | Where-Object { $_.owner -eq $Owner })) { Remove-Item $t.file -Force -ErrorAction SilentlyContinue } }

function Get-MedianHoldSec {
    if (-not (Test-Path $history)) { return 600 }
    $v = @(Get-Content $history -Tail 15 | Where-Object { $_ -match '^\d' } | ForEach-Object { [double] ($_ -split ',')[5] } | Sort-Object)
    if ($v.Count -eq 0) { return 600 }
    return [math]::Max(60, $v[[int] ($v.Count / 2)])
}

function Add-History($hold) {
    if (-not (Test-Path $history)) { 'end,owner,kind,slot,waited_s,hold_s' | Set-Content $history -Encoding ascii }
    $since = try { [datetime] $hold.since } catch { Get-Date }
    $line = '{0},{1},{2},{3},{4},{5}' -f (Get-Date).ToString('s'), $hold.owner, $hold.kind, $hold.slot, [int] $hold.waited, [int] ((Get-Date) - $since).TotalSeconds
    Add-Content $history $line -Encoding ascii
}

# ---------------------------------------------------------------- main build cache

function Get-MainKey {
    try { $k = (& git -C $MainRepo rev-parse HEAD 2>$null); if ($LASTEXITCODE -eq 0 -and $k) { return $k.Substring(0, 12) } } catch { }
    return 'nogit'
}

function Get-CacheCurrent {
    $f = Join-Path $cacheDir 'current.txt'
    if (-not (Test-Path $f)) { return $null }
    $k = (Get-Content $f -Raw).Trim()
    $d = Join-Path $cacheDir $k
    if ($k -and (Test-Path (Join-Path $d 'UltraRT.dll'))) { return [pscustomobject]@{ Key = $k; Dir = $d } }
    return $null
}

# Builds main's HEAD into <cache>\<key> (shader bundle + plugins, nothing deployed). One builder at a time.
function Build-MainCache([switch] $Wait) {
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $mutex = Join-Path $cacheDir 'build.lock'
    $deadline = (Get-Date).AddMinutes(10)
    while ($true) {
        if (Test-Path $mutex) {
            if (((Get-Date) - (Get-Item $mutex).LastWriteTime).TotalMinutes -gt 15) { Remove-Item $mutex -Force -ErrorAction SilentlyContinue }
        }
        try { $fs = [IO.File]::Open($mutex, 'CreateNew', 'Write'); $fs.Close(); break } catch { }
        if (-not $Wait -or (Get-Date) -gt $deadline) { return $false }
        Start-Sleep -Seconds 2
    }
    try {
        $key = Get-MainKey
        $cur = Get-CacheCurrent
        if ($cur -and $cur.Key -eq $key) { return $true }
        $tmp = Join-Path $cacheDir "$key.tmp"
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        & (Join-Path $MainRepo 'scripts\build.ps1') -NoDeploy -StageDir $tmp -SkipNative | Out-Host
        if (-not (Test-Path (Join-Path $tmp 'UltraRT.dll'))) { throw "main build produced no UltraRT.dll in $tmp" }
        $final = Join-Path $cacheDir $key
        if (Test-Path $final) { Remove-Item $final -Recurse -Force }
        Move-Item $tmp $final
        Set-Content (Join-Path $cacheDir 'current.txt') $key -Encoding ascii
        foreach ($old in @(Get-ChildItem $cacheDir -Directory | Where-Object { $_.Name -ne $key } | Sort-Object LastWriteTime -Descending | Select-Object -Skip 1)) {
            Remove-Item $old.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
        return $true
    } finally { Remove-Item $mutex -Force -ErrorAction SilentlyContinue }
}

function Start-CacheRefresh {
    $cur = Get-CacheCurrent
    if ($cur -and $cur.Key -eq (Get-MainKey)) { return }
    $log = Join-Path $LockDir 'main-cache-build.log'
    try {
        Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", 'cache',
            '-GameDir', "`"$GameDir`"", '-AltDir', "`"$AltDir`"", '-MainRepo', "`"$MainRepo`"", '-LockDir', "`"$LockDir`"") `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err" | Out-Null
    } catch { Write-Warning "Could not start the main-build refresh: $($_.Exception.Message)" }
}

# Copy the cached main build into the given slots, unless a game runs in one. Returns $true if every slot got it.
function Restore-Main($procs, $slots) {
    $cur = Get-CacheCurrent
    if (-not $cur -or $cur.Key -ne (Get-MainKey)) {
        # Normally acquire refreshed it while queueing. Wait for a running refresh, else build now.
        if (-not (Build-MainCache -Wait)) { Write-Warning 'main build cache is not current; using the last one' }
        $cur = Get-CacheCurrent
    }
    if (-not $cur) { throw 'No cached main build and building one failed' }
    $ok = $true
    foreach ($s in $slots) {
        if ((Get-SlotProcs $procs $s).Count -gt 0) { $ok = $false; continue }
        Sync-Stage -From $cur.Dir -GameDir $dirs[$s] | Out-Null
    }
    return $ok
}

# ---------------------------------------------------------------- actions

switch ($Action) {
    'status' {
        do {
            $procs = Get-GameProcs
            $now = Get-Date
            function Ago($t) { try { $m = ($now - [datetime] $t).TotalMinutes } catch { return '?' }; if ($m -lt 1) { '<1m' } elseif ($m -lt 60) { '{0}m' -f [int] [math]::Floor($m) } else { '{0}h{1:00}m' -f [int] [math]::Floor($m / 60), [int] ($m % 60) } }
            function Clock($t) { try { ([datetime] $t).ToString('HH:mm') } catch { '?' } }
            $out = @()
            # Line 1 keeps its old contract ('free' / 'locked by <owner> since <time>'); scripts and the self-test read it.
            $mh = Get-Hold 'main' $procs
            $out += if ($mh) { "locked by $($mh.owner) since $($mh.since)" } else { 'free' }
            $out += ''
            $out += '{0,-5} {1,-16} {2,-7} {3,-18} {4}' -f 'SLOT', 'HELD BY', 'KIND', 'FOR', 'GAME'
            foreach ($s in @('main', 'alt')) {
                if ($s -eq 'alt' -and -not $altInstalled) { $out += '{0,-5} {1}' -f 'alt', '(not installed)'; continue }
                $h = Get-Hold $s $procs
                $sp = @(Get-SlotProcs $procs $s)
                $game = if ($sp.Count -eq 0) { '-' } else {
                    ($sp | ForEach-Object {
                        $up = try { Ago (Get-Process -Id $_.Pid -ErrorAction Stop).StartTime } catch { '?' }
                        '{0} pid {1}, up {2}' -f $(if ($_.Agent) { 'agent' } else { 'USER' }), $_.Pid, $up }) -join '; ' }
                if ($h) {
                    $who = if ($h.via) { "$($h.owner) (reserved)" } else { $h.owner }
                    $out += '{0,-5} {1,-16} {2,-7} {3,-18} {4}' -f $s, $who, $h.kind, ('{0} (since {1})' -f (Ago $h.since), (Clock $h.since)), $game
                } else { $out += '{0,-5} {1,-16} {2,-7} {3,-18} {4}' -f $s, 'free', '', '', $game }
            }
            $user = Get-UserGame $procs
            if ($user.Count -gt 0) { $out += ''; $out += "The user is playing (pid $($user[0].Pid)): nothing new starts until that game closes." }
            $tickets = @(Get-Tickets)
            $out += ''
            if ($tickets.Count -eq 0) { $out += 'queue: empty' }
            else {
                $med = Get-MedianHoldSec
                $out += "queue: $($tickets.Count) waiting (typical hold ~$([int] [math]::Ceiling($med / 60)) min)"
                $n = 0
                foreach ($t in $tickets) {
                    $n++
                    $out += '  {0}. {1,-16} {2,-6} {3,-4} waiting {4,-6} est. start ~{5} min' -f $n, $t.owner, $t.kind, $t.slot, (Ago $t.since), [int] [math]::Ceiling($n * $med / 60)
                }
            }
            if (Test-Path $history) {
                $recent = @(Import-Csv $history | Select-Object -Last 3)
                if ($recent.Count -gt 0) {
                    $out += ''
                    $out += 'recent releases:'
                    foreach ($r in $recent) { $out += '  {0}  {1,-16} {2,-6} {3,-4} held {4,4:N1} min, waited {5,4:N1} min' -f (Clock $r.end), $r.owner, $r.kind, $r.slot, ([double] $r.hold_s / 60), ([double] $r.waited_s / 60) }
                }
            }
            if ($Watch) { Clear-Host; "game lock - $($now.ToString('HH:mm:ss')) (refreshes every ${PollSec}s, Ctrl+C to stop)"; '' }
            $out
            if ($Watch) { Start-Sleep -Seconds $PollSec }
        } while ($Watch)
    }
    'slot' {
        $procs = @()
        foreach ($s in $slotNames) {
            $h = Get-Hold $s $procs
            if ($h -and $h.owner -eq $Owner -and -not $h.via) { $s; return }
        }
        exit 1
    }
    'cancel' { Remove-MyTicket; "cancelled $Owner" }
    'cache' {
        if (Build-MainCache) { "main build cache: $((Get-CacheCurrent).Key)" } else { 'main build cache: another build is running' }
    }
    'acquire' {
        $deadline = (Get-Date).AddMinutes($TimeoutMin)
        $started = Get-Date
        $lastNote = [datetime]::MinValue; $lastPos = -1
        $refreshed = $false
        while ($true) {
            $procs = Get-GameProcs
            $holds = @{}; foreach ($s in $slotNames) { $holds[$s] = Get-Hold $s $procs }
            # Already holding what was asked for (a repeated acquire, or a second acquire job of this owner): done.
            $own = @($slotNames | Where-Object { $holds[$_] -and $holds[$_].owner -eq $Owner })
            $have = if ($Kind -eq 'perf') { $own.Count -eq $slotNames.Count -and $holds['main'].kind -ne 'visual' }
                    elseif ($Slot -eq 'any') { $own.Count -gt 0 } else { $own -contains $Slot }
            if ($have) {
                Remove-MyTicket
                "acquired by $Owner"
                Write-Host "slot: $($own[0]) (already held)"
                return
            }
            $user = Get-UserGame $procs
            $tickets = @(Get-Tickets)
            $me = @($tickets | Where-Object { $_.owner -eq $Owner })
            if ($me.Count -eq 0) { Join-Queue; $tickets = @(Get-Tickets); $me = @($tickets | Where-Object { $_.owner -eq $Owner }) }
            else { (Get-Item $me[0].file).LastWriteTime = Get-Date }   # heartbeat
            $pos = [array]::IndexOf(@($tickets | ForEach-Object { $_.owner }), $Owner) + 1
            $demand = Get-Demand $me[0]
            $blockedBy = $null
            foreach ($t in ($tickets | Select-Object -First ($pos - 1))) {
                if (@(Get-Demand $t | Where-Object { $demand -contains $_ }).Count -gt 0) { $blockedBy = $t.owner; break }
            }
            # Slots I can use right now: nobody else holds them and no game runs in them.
            $isFree = @{}
            foreach ($s in $slotNames) {
                $isFree[$s] = ($holds[$s] -and $holds[$s].owner -eq $Owner) -or ((-not $holds[$s]) -and (Get-SlotProcs $procs $s).Count -eq 0)
            }

            $take = $null
            if (-not $blockedBy -and $user.Count -eq 0) {
                if ($Kind -eq 'perf') { if (@($slotNames | Where-Object { -not $isFree[$_] }).Count -eq 0) { $take = $slotNames } }
                elseif ($Slot -eq 'any') { $take = @(@('alt', 'main') | Where-Object { $slotNames -contains $_ -and $isFree[$_] } | Select-Object -First 1) }
                elseif ($isFree[$Slot]) { $take = @($Slot) }
                if ($take -and $take.Count -eq 0) { $take = $null }
            }

            if ($take) {
                $primary = if ($Kind -eq 'perf') { $Slot } else { $take[0] }
                $got = @()
                $failed = $false
                foreach ($s in $take) {
                    $p = LockPath $s
                    $content = @{ owner = $Owner; since = (Get-Date).ToString('s'); kind = $Kind; slot = $s; waited = [int] ((Get-Date) - $started).TotalSeconds }
                    if ($s -ne $primary) { $content.via = $primary }
                    if ($holds[$s] -and $holds[$s].owner -eq $Owner) { $content.since = $holds[$s].since; Write-JsonFile $p $content; continue }
                    try {
                        $fs = [IO.File]::Open($p, 'CreateNew', 'Write')
                        $b = [Text.Encoding]::ASCII.GetBytes(($content | ConvertTo-Json -Compress))
                        $fs.Write($b, 0, $b.Length); $fs.Close()
                        $got += $s
                    } catch { $failed = $true; break }
                }
                if ($failed) { foreach ($s in $got) { Remove-Item (LockPath $s) -Force -ErrorAction SilentlyContinue } }
                else {
                    Remove-MyTicket
                    "acquired by $Owner"
                    Write-Host "slot: $primary ($($dirs[$primary])); kind: $Kind$(if ($take.Count -gt 1) { '; exclusive: alt is reserved too' })"
                    return
                }
            }

            if ((Get-Date) -gt $deadline) {
                # Keep the place for a retry loop for a moment (the ticket expires 30 s after this process is gone).
                foreach ($t in $me) { $o = @{ owner = $t.owner; kind = $t.kind; slot = $t.slot; since = $t.since; pid = $t.pid; grace = 30 }; Write-JsonFile $t.file $o }
                $h = $holds['main']; if (-not $h -and $holds['alt']) { $h = $holds['alt'] }
                throw "Timed out waiting for the game (lock: $(if ($h) { $h.owner } else { 'none' }); running: $([bool] ($procs.Count -gt 0)); queue position: $pos)"
            }

            # While queueing, build main's HEAD for release (once), and say where we are.
            if (-not $refreshed) { $refreshed = $true; if (-not $NoRestore) { Start-CacheRefresh } }
            if ($pos -ne $lastPos -or ((Get-Date) - $lastNote).TotalSeconds -gt 60) {
                $lastPos = $pos; $lastNote = Get-Date
                $why = if ($user.Count -gt 0) { 'the user is playing' } elseif ($blockedBy) { "behind $blockedBy" } else {
                    $busy = @($slotNames | Where-Object { -not $isFree[$_] } | ForEach-Object { if ($holds[$_]) { "$_ held by $($holds[$_].owner)" } else { "$_ has a game running" } })
                    $busy -join ', ' }
                $med = Get-MedianHoldSec
                Write-Host ("queued: position {0} of {1} ({2}); est. wait ~{3} min" -f $pos, $tickets.Count, $why, [int] [math]::Ceiling($pos * $med / 60))
            }
            Start-Sleep -Seconds $PollSec
        }
    }
    'release' {
        $procs = Get-GameProcs
        $mine = @()
        $others = @()
        foreach ($s in $slotNames) {
            $h = Get-Hold $s $procs
            if ($h -and $h.owner -eq $Owner) { $mine += [pscustomobject]@{ Slot = $s; Hold = $h } }
            elseif ($h) { $others += $h }
        }
        # Releasing a lock somebody else holds is refused; with no lock of anyone's it is a plain "restore main".
        if ($mine.Count -eq 0 -and @($others | Where-Object { $_.slot -eq 'main' }).Count -gt 0) { throw "Lock is held by $(@($others | Where-Object { $_.slot -eq 'main' })[0].owner), not $Owner" }
        Remove-MyTicket
        foreach ($m in $mine) { Stop-Resident $dirs[$m.Slot] }
        # Only once no agent game is left running (Unity writes these prefs when a game quits).
        if (@(Get-GameProcs | Where-Object { $_.Agent }).Count -eq 0) {
            $restored = Restore-DisplayPrefs $GameDir
            if ($restored) { "restored the user's display settings ($restored)" }
        }
        if ($mine.Count -gt 0) { Start-Sleep -Milliseconds 300; $procs = Get-GameProcs }
        $restored = $true
        if ($NoRestore) { 'main build restore skipped' }
        else {
            # Our own slots; with no lock of ours, the slots nobody holds (what the old release did for main).
            $targets = @($mine | ForEach-Object { $_.Slot })
            if ($targets.Count -eq 0) { $targets = @($slotNames | Where-Object { -not (Get-Hold $_ $procs) }) }
            try { $restored = Restore-Main $procs $targets }
            catch { Write-Warning "main build not restored: $($_.Exception.Message)" }
            if ($restored) { 'restored main build' } else { Write-Warning 'ULTRAKILL is running; main build not restored' }
        }
        foreach ($m in $mine) {
            if (-not $m.Hold.via) { Add-History $m.Hold }
            Remove-Item (LockPath $m.Slot) -Force -ErrorAction SilentlyContinue
        }
        "released by $Owner"
    }
}
