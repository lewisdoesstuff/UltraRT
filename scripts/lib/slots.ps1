# Shared helpers for game-lock.ps1, bench.ps1, build.ps1 and deploy.ps1 (dot-source it).
# Two game installs ("slots"): main = the Steam install, alt = a copy for visual/diagnostic runs (bench/README.md).
# ASCII only, PowerShell 5.1.

# Where the installs are (first match wins):
#   main: $env:ULTRAKILL_DIR, the local config's "main", the Steam library that has ULTRAKILL (Steam's registry key and
#         steamapps\libraryfolders.vdf), else the default Steam folder.
#   alt:  $env:ULTRAKILL_ALT_DIR, the local config's "alt"; neither = no alt slot.
# The local config is per user and outside the repo, so every worktree sees it: %LOCALAPPDATA%\UltraRT\installs.json,
# for example {"alt": "E:\\UltraRT-Alt\\ULTRAKILL"} (bench/README.md, "Install locations").
$script:UrtLocalConfig = Join-Path $env:LOCALAPPDATA 'UltraRT\installs.json'
$script:UrtSteamDefault = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\ULTRAKILL'

function Get-UrtLocalConfig {
    if (-not (Test-Path $script:UrtLocalConfig)) { return $null }
    try { return (Get-Content $script:UrtLocalConfig -Raw | ConvertFrom-Json) }
    catch { Write-Warning "Unreadable $($script:UrtLocalConfig): $($_.Exception.Message)"; return $null }
}

# The ULTRAKILL folder of the Steam library that has it, or '' if Steam or the game is not found.
function Find-SteamUltrakill {
    $steam = $null
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $v = Get-ItemProperty $k -ErrorAction SilentlyContinue
        if ($v -and $v.SteamPath) { $steam = [string] $v.SteamPath; break }
        if ($v -and $v.InstallPath) { $steam = [string] $v.InstallPath; break }
    }
    if (-not $steam) { return '' }
    $steam = $steam -replace '/', '\'
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        # "path"  "E:\\Games": VDF doubles every backslash.
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') }
    }
    foreach ($lib in $libs) {
        $d = Join-Path $lib 'steamapps\common\ULTRAKILL'
        if (Test-Path (Join-Path $d 'ULTRAKILL.exe')) { return $d }
    }
    return ''
}

# The alt install's folder, or '' when none is configured.
function Get-AltDir {
    if ($env:ULTRAKILL_ALT_DIR) { return $env:ULTRAKILL_ALT_DIR }
    $c = Get-UrtLocalConfig
    if ($c -and $c.alt) { return [string] $c.alt }
    return ''
}

function Get-SlotDir {
    param([ValidateSet('main', 'alt')] [string] $Slot)
    if ($Slot -eq 'alt') {
        $a = Get-AltDir
        if (-not $a) { throw "No alt install configured: set ULTRAKILL_ALT_DIR or add `"alt`" to $($script:UrtLocalConfig) (bench/README.md, 'Install locations')" }
        return $a
    }
    if ($env:ULTRAKILL_DIR) { return $env:ULTRAKILL_DIR }
    if (-not $script:UrtMainDir) {
        $c = Get-UrtLocalConfig
        $script:UrtMainDir = if ($c -and $c.main) { [string] $c.main } else { Find-SteamUltrakill }
        if (-not $script:UrtMainDir) { $script:UrtMainDir = $script:UrtSteamDefault }
    }
    return $script:UrtMainDir
}

function Get-NormPath([string] $p) {
    if (-not $p) { return '' }
    return ($p.TrimEnd('\', '/') -replace '/', '\').ToLowerInvariant()
}

# Which slot a game folder is: main, alt, or '' (unknown).
function Get-SlotOfDir([string] $dir, [string] $mainDir, [string] $altDir) {
    $d = Get-NormPath $dir
    if ($d -eq (Get-NormPath $mainDir)) { return 'main' }
    if ($altDir -and $d -eq (Get-NormPath $altDir)) { return 'alt' }
    return ''
}

# Running ULTRAKILL.exe processes with their folder and command line. Agents' runs always carry -ultrart-* arguments
# (bench.ps1 does; add -ultrart-agent to a hand launch); a launch without them is the user playing.
function Get-GameProcs {
    $out = @()
    try { $procs = @(Get-CimInstance Win32_Process -Filter "Name='ULTRAKILL.exe'" -ErrorAction Stop) } catch { $procs = @() }
    foreach ($p in $procs) {
        $path = [string] $p.ExecutablePath
        $out += [pscustomobject]@{
            Pid = [int] $p.ProcessId
            Path = $path
            Dir = $(if ($path) { Split-Path $path -Parent } else { '' })
            Cmd = [string] $p.CommandLine
            Agent = ([string] $p.CommandLine) -match '-ultrart-'
        }
    }
    return $out
}

function Read-JsonFile([string] $path) {
    for ($i = 0; $i -lt 3; $i++) {
        try {
            if (-not (Test-Path $path)) { return $null }
            return (Get-Content $path -Raw -ErrorAction Stop | ConvertFrom-Json)
        } catch { Start-Sleep -Milliseconds 50 }
    }
    return $null
}

function Write-JsonFile([string] $path, $obj) {
    $tmp = "$path.tmp$PID"
    [IO.File]::WriteAllText($tmp, ($obj | ConvertTo-Json -Compress), [Text.Encoding]::ASCII)
    Move-Item $tmp $path -Force
}

# Copy the staged build (UltraRT.dll, UltraRT.Native.dll, ultrart.shaders, ultrart-materials.json)
# into <GameDir>\BepInEx\plugins\UltraRT, only the files that differ. A changed shader bundle gets a fresh timestamp,
# so a running game hot-reloads it. Returns the names it copied.
function Sync-Stage {
    param([Parameter(Mandatory)] [string] $From, [Parameter(Mandatory)] [string] $GameDir)
    $dest = Join-Path $GameDir 'BepInEx\plugins\UltraRT'
    New-Item -ItemType Directory -Force $dest | Out-Null
    $copied = @()
    foreach ($f in Get-ChildItem $From -File | Where-Object { $_.Name -ne 'stage.json' }) {
        $d = Join-Path $dest $f.Name
        $same = $false
        if (Test-Path $d) {
            $di = Get-Item $d
            if ($di.Length -eq $f.Length) {
                $same = ((Get-QuickHash $d) -eq (Get-QuickHash $f.FullName))
            }
        }
        if ($same) { continue }
        try {
            Copy-Item $f.FullName $d -Force -ErrorAction Stop
            # Copy-Item keeps the source timestamp; a running game only reloads the bundle when it changes.
            if ($f.Name -eq 'ultrart.shaders') { (Get-Item $d).LastWriteTimeUtc = [DateTime]::UtcNow }
            $copied += $f.Name
        } catch {
            Write-Warning "$($f.Name) not deployed (in use by a running game?): $($_.Exception.Message)"
        }
    }
    # The preloader patcher (stage\patchers -> BepInEx\patchers\UltraRT; docs/hdr.md). It is loaded when the game starts.
    $patchFrom = Join-Path $From 'patchers'
    if (Test-Path $patchFrom) {
        $patchTo = Join-Path $GameDir 'BepInEx\patchers\UltraRT'
        New-Item -ItemType Directory -Force $patchTo | Out-Null
        foreach ($f in Get-ChildItem $patchFrom -File) {
            $d = Join-Path $patchTo $f.Name
            if ((Test-Path $d) -and (Get-Item $d).Length -eq $f.Length -and (Get-QuickHash $d) -eq (Get-QuickHash $f.FullName)) { continue }
            try { Copy-Item $f.FullName $d -Force -ErrorAction Stop; $copied += "patchers\$($f.Name)" }
            catch { Write-Warning "$($f.Name) not deployed (in use by a running game?): $($_.Exception.Message)" }
        }
    }
    # Shipped light overrides: lights\*.json mirrored (a level file dropped from the stage is removed). The user's own edits
    # live in BepInEx\ultrart\lights and are never touched here.
    $lightsFrom = Join-Path $From 'lights'
    $lightsTo = Join-Path $dest 'lights'
    if (Test-Path $lightsFrom) {
        New-Item -ItemType Directory -Force $lightsTo | Out-Null
        # Subfolders are layers (lights\proposed\: docs/lighting-overrides.md, "Review"); paths are kept relative.
        $want = @{}
        $fromRoot = (Resolve-Path $lightsFrom).Path.TrimEnd([char]92)
        foreach ($f in Get-ChildItem $lightsFrom -File -Recurse -Filter *.json) {
            $rel = $f.FullName.Substring($fromRoot.Length + 1)
            $want[$rel] = $true
            $d = Join-Path $lightsTo $rel
            if ((Test-Path $d) -and (Get-Item $d).Length -eq $f.Length -and (Get-QuickHash $d) -eq (Get-QuickHash $f.FullName)) { continue }
            New-Item -ItemType Directory -Force (Split-Path $d -Parent) | Out-Null
            Copy-Item $f.FullName $d -Force
            $copied += "lights\$rel"
        }
        $toRoot = (Resolve-Path $lightsTo).Path.TrimEnd([char]92)
        foreach ($f in Get-ChildItem $lightsTo -File -Recurse -Filter *.json) {
            $rel = $f.FullName.Substring($toRoot.Length + 1)
            if (-not $want.ContainsKey($rel)) { Remove-Item $f.FullName -Force; $copied += "-lights\$rel" }
        }
    } elseif (Test-Path $lightsTo) {
        # A stage without shipped overrides (an older build): none of ours may linger.
        foreach ($f in Get-ChildItem $lightsTo -File -Filter *.json) { Remove-Item $f.FullName -Force; $copied += "-lights\$($f.Name)" }
    }
    return $copied
}

# ---------------------------------------------------------------- resident game (bench.ps1 -KeepOpen)

function Get-ServerDir([string] $GameDir) { Join-Path $GameDir 'BepInEx\ultrart\server' }

# The resident benchmark game of an install (started by bench.ps1 -KeepOpen), or $null.
function Get-Resident([string] $GameDir) {
    $ready = Join-Path (Get-ServerDir $GameDir) 'ready.json'
    $r = Read-JsonFile $ready
    if (-not $r) { return $null }
    $p = Get-Process -Id ([int] $r.pid) -ErrorAction SilentlyContinue
    if (-not $p -or $p.ProcessName -ne 'ULTRAKILL') { Remove-Item $ready -Force -ErrorAction SilentlyContinue; return $null }
    return [pscustomobject]@{ Pid = $p.Id; Process = $p; State = $r.state; Dll = [int64] $r.dll }
}

# Ask the resident game to quit; kill it only if it is ours (it is: bench.ps1 started it) and did not quit in time.
function Stop-Resident([string] $GameDir) {
    $res = Get-Resident $GameDir
    if (-not $res) { return }
    $srv = Get-ServerDir $GameDir
    Write-JsonFile (Join-Path $srv ("cmd-{0:yyyyMMddHHmmssfff}.json" -f (Get-Date))) @{ Quit = $true }
    if (-not $res.Process.WaitForExit(30000)) { Stop-Process -Id $res.Pid -Force }
    Remove-Item (Join-Path $srv 'ready.json') -Force -ErrorAction SilentlyContinue
}

# SHA1 of a file, much faster than Get-FileHash in Windows PowerShell 5.1.
function Get-QuickHash([string] $path) {
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path))) } finally { $sha.Dispose() }
}

# ---------------------------------------------------------------- the user's display settings
# Both installs share Unity's registry prefs (HKCU\Software\Hakita\ULTRAKILL), and Unity writes the window's monitor,
# resolution and fullscreen mode there when a game quits. A bench game that ended up on another monitor (two games at
# once) overwrote the user's 2560x1440 / monitor 0 on 2026-11-14. The user's values are kept in
# <main>\BepInEx\ultrart\display-prefs.json (scripts\display-prefs.ps1 save) and put back after every agent game.
$script:DisplayPrefsKey = 'HKCU:\Software\Hakita\ULTRAKILL'
$script:DisplayPrefsPattern = '^(UnitySelectMonitor|Screenmanager (Resolution Width|Resolution Height|Fullscreen mode|Window Position X|Window Position Y|Resolution Use Native))_h\d+$'

# A registry DWORD (UInt32 or Int32 or Int64 from JSON) as the signed Int32 Set-ItemProperty -Type DWord accepts.
function ConvertTo-DWordInt($v) { [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32] ([int64] $v -band 0xFFFFFFFFL)), 0) }

function Get-DisplayPrefsFile([string] $MainDir) { Join-Path $MainDir 'BepInEx\ultrart\display-prefs.json' }

function Save-DisplayPrefs([string] $MainDir) {
    $p = Get-ItemProperty $script:DisplayPrefsKey -ErrorAction SilentlyContinue
    if (-not $p) { return $false }
    $o = [ordered]@{}
    foreach ($x in $p.PSObject.Properties) { if ($x.Name -match $script:DisplayPrefsPattern) { $o[$x.Name] = ConvertTo-DWordInt $x.Value } }
    Write-JsonFile (Get-DisplayPrefsFile $MainDir) $o
    return $true
}

# Puts the saved user values back where they differ. Returns a short description of what changed ('' if nothing).
function Restore-DisplayPrefs([string] $MainDir) {
    $want = Read-JsonFile (Get-DisplayPrefsFile $MainDir)
    if (-not $want) { return '' }
    $cur = Get-ItemProperty $script:DisplayPrefsKey -ErrorAction SilentlyContinue
    if (-not $cur) { return '' }
    $changed = @()
    foreach ($x in $want.PSObject.Properties) {
        $now = $cur.($x.Name)
        # DWORDs come back as UInt32 (a window on a monitor left of the primary has a 'negative' position).
        if ($null -ne $now -and (ConvertTo-DWordInt $now) -ne (ConvertTo-DWordInt $x.Value)) {
            Set-ItemProperty $script:DisplayPrefsKey $x.Name (ConvertTo-DWordInt $x.Value) -Type DWord
            $changed += "$($x.Name -replace '_h\d+$','') $now -> $($x.Value)"
        }
    }
    return ($changed -join '; ')
}
