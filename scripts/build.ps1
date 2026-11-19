# Builds the shader bundle (Unity batch mode) and the BepInEx plugins.
# Usage:
#   .\scripts\build.ps1                       build and deploy into the main game install (needs the game lock: bench/README.md)
#   .\scripts\build.ps1 -NoDeploy             build into <repo>\stage only (no game lock needed), then
#   .\scripts\deploy.ps1 -Slot main|alt       copy the stage into a game install in seconds, after game-lock.ps1 acquire
#   .\scripts\build.ps1 -Slot alt             build and deploy straight into the alt install
# Switches: -SkipShaders -SkipPlugins -SkipNative; -StageDir <dir> picks another stage folder (implies -NoDeploy).
param(
    [switch] $SkipShaders,
    [switch] $SkipPlugins,
    [switch] $SkipNative,
    [switch] $NoDeploy,
    [string] $StageDir,
    [ValidateSet('main', 'alt')] [string] $Slot = 'main',
    [string] $GameDir,
    [string] $UnityExe = $(if ($env:UNITY_2022_3_29) { $env:UNITY_2022_3_29 } else { 'C:\Program Files\Unity\Hub\Editor\2022.3.29f1\Editor\Unity.exe' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $GameDir) { $GameDir = Get-SlotDir $Slot }
$explicitGameDir = ($Slot -ne 'main') -or $PSBoundParameters.ContainsKey('GameDir')
if ($StageDir) { $NoDeploy = $true }
if ($NoDeploy -and -not $StageDir) { $StageDir = Join-Path $root 'stage' }
if ($NoDeploy) {
    if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
    New-Item -ItemType Directory -Force $StageDir | Out-Null
}
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\UltraRT'
$project = Join-Path $root 'unity\UltraRT.Shaders'
$bundle = Join-Path $project 'Build\ultrart.shaders'

if (-not $SkipShaders) {
    $log = Join-Path $project 'Logs\batch-build.log'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $UnityExe -PassThru -Wait -ArgumentList '-batchmode', '-quit', '-projectPath', "`"$project`"",
        '-executeMethod', 'UltraRT.Editor.BuildBundles.Build', '-logFile', "`"$log`""
    $errors = Select-String -Path $log -Pattern 'Shader error|error CS\d+|\[UltraRT\] Bundle build failed' | ForEach-Object Line
    if ($p.ExitCode -ne 0 -or $errors) {
        $errors
        throw "Shader bundle build failed (exit $($p.ExitCode)). Log: $log"
    }
    Select-String -Path $log -Pattern 'Shader warning' | ForEach-Object Line | Select-Object -Unique
    "Shaders built in $([int]$sw.Elapsed.TotalSeconds)s"

    # Deploy right away: a running game hot-reloads the bundle when it changes.
    if (-not $NoDeploy -and (Test-Path $pluginDir)) {
        Copy-Item $bundle $pluginDir -Force
        # Copy-Item keeps the source timestamp; bump it so a running game always sees a change.
        (Get-Item (Join-Path $pluginDir 'ultrart.shaders')).LastWriteTimeUtc = [DateTime]::UtcNow
        "Deployed shader bundle -> $pluginDir"
    }
}

if (-not $SkipPlugins) {
    # Prefer the user-local SDK; the system 'dotnet' on PATH may be a runtime-only install.
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
    if (Test-Path $userDotnet) { $env:DOTNET_ROOT = Split-Path $userDotnet }
    $env:DOTNET_NOLOGO = 1; $env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
    # References come from the game folder (the main one unless told otherwise); -NoDeploy never writes there.
    $props = @()
    if ($explicitGameDir) { $props += "/p:GameDir=$GameDir" }
    if ($NoDeploy) { $props += '/p:Deploy=false' }
    foreach ($proj in 'src\UltraRT.Preload\UltraRT.Preload.csproj', 'src\UltraRT\UltraRT.csproj') {
        & $dotnet build (Join-Path $root $proj) -c Release -v q -nologo @props 2>&1 | Select-String ' error |Deployed' | ForEach-Object Line | Select-Object -Unique
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
    }
}

# Native GPU timer DLL (opt-in at runtime with -ultrart-gputimer).
$nativeDll = Join-Path $root 'native\UltraRT.Native\bin\UltraRT.Native.dll'
if (-not $SkipNative) {
    & (Join-Path $root 'native\UltraRT.Native\build.ps1')
    if (-not $NoDeploy -and (Test-Path $pluginDir)) {
        # A running game keeps the DLL loaded (it is never unloaded), so the copy can fail until the game exits.
        try { Copy-Item $nativeDll $pluginDir -Force; "Deployed UltraRT.Native.dll -> $pluginDir" }
        catch { Write-Warning "UltraRT.Native.dll not deployed (game running?): $($_.Exception.Message)" }
    }
}

if ($NoDeploy) {
    $files = @(
        (Join-Path $root 'src\UltraRT\bin\Release\netstandard2.1\UltraRT.dll'),
        (Join-Path $root 'src\UltraRT\ultrart-materials.json'),
        $bundle, $nativeDll,
        # DLSS Ray Reconstruction runtime (scripts\fetch-dlss.ps1; NVIDIA's file, never committed; docs/dlss.md)
        (Join-Path $root 'lib\dlss\bin\nvngx_dlssd.dll'))
    foreach ($f in $files) { if (Test-Path $f) { Copy-Item $f $StageDir -Force } }
    # The preloader patcher of the HDR output (docs/hdr.md): deployed to BepInEx\patchers\UltraRT, not the plugin folder.
    $preload = Join-Path $root 'src\UltraRT.Preload\bin\Release\netstandard2.1\UltraRT.Preload.dll'
    if (Test-Path $preload) {
        New-Item -ItemType Directory -Force (Join-Path $StageDir 'patchers') | Out-Null
        Copy-Item $preload (Join-Path $StageDir 'patchers') -Force
    }
    # Shipped per-level light overrides (data\lights\<scene>.json -> plugin folder lights\; docs/lighting-overrides.md).
    $lightsSrc = Join-Path $root 'data\lights'
    if (Test-Path $lightsSrc) {
        $lightsDst = Join-Path $StageDir 'lights'
        New-Item -ItemType Directory -Force $lightsDst | Out-Null
        # Mirror, subfolders included (layers such as proposed\): a file removed from data\lights must not linger in the stage.
        Get-ChildItem $lightsDst -File -Recurse -Filter *.json | Remove-Item -Force
        $srcRoot = (Resolve-Path $lightsSrc).Path.TrimEnd([char]92)
        foreach ($f in Get-ChildItem $lightsSrc -File -Recurse -Filter *.json) {
            $d = Join-Path $lightsDst $f.FullName.Substring($srcRoot.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $d -Parent) | Out-Null
            Copy-Item $f.FullName $d -Force
        }
    }
    if (-not (Test-Path (Join-Path $StageDir 'UltraRT.dll'))) { throw "Nothing staged: UltraRT.dll missing (build skipped or failed?)" }
    $head = (git -C $root rev-parse --short HEAD 2>$null)
    @{ commit = $head; dirty = [bool](git -C $root status --porcelain 2>$null); built = (Get-Date).ToString('s') } | ConvertTo-Json -Compress |
        Set-Content (Join-Path $StageDir 'stage.json') -Encoding ascii
    "Staged $((Get-ChildItem $StageDir -File).Count) files in $StageDir (not deployed: scripts\deploy.ps1 -Slot main|alt after game-lock.ps1 acquire)"
}
