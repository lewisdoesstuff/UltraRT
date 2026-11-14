# Shared helpers for game-lock.ps1, bench.ps1, build.ps1 and deploy.ps1 (dot-source it).
# Two game installs ("slots"): main = the Steam install, alt = a copy for visual/diagnostic runs (bench/README.md).
# ASCII only, PowerShell 5.1.

$script:UrtMainDefault = 'D:\SteamLibrary\steamapps\common\ULTRAKILL'
$script:UrtAltDefault = 'D:\UltraRT-Bench\ULTRAKILL'

function Get-SlotDir {
    param([ValidateSet('main', 'alt')] [string] $Slot)
    if ($Slot -eq 'alt') { if ($env:ULTRAKILL_ALT_DIR) { return $env:ULTRAKILL_ALT_DIR } else { return $script:UrtAltDefault } }
    if ($env:ULTRAKILL_DIR) { return $env:ULTRAKILL_DIR } else { return $script:UrtMainDefault }
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

# Copy the staged build (UltraRT.dll, UltraRT.Probe.dll, UltraRT.Native.dll, ultrart.shaders, ultrart-materials.json)
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
                $same = ((Get-FileHash $d -Algorithm SHA1).Hash -eq (Get-FileHash $f.FullName -Algorithm SHA1).Hash)
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
