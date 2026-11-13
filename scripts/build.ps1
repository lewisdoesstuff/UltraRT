# Builds the shader bundle (Unity batch mode) and the BepInEx plugins, deploying both into the game.
# Usage: .\scripts\build.ps1 [-SkipShaders] [-SkipPlugins]
param(
    [switch] $SkipShaders,
    [switch] $SkipPlugins,
    [string] $UnityExe = $(if ($env:UNITY_2022_3_29) { $env:UNITY_2022_3_29 } else { 'C:\Program Files\Unity\Hub\Editor\2022.3.29f1\Editor\Unity.exe' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $SkipShaders) {
    $project = Join-Path $root 'unity\UltraRT.Shaders'
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
}

if (-not $SkipPlugins) {
    # Prefer the user-local SDK; the system 'dotnet' on PATH may be a runtime-only install.
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }
    if (Test-Path $userDotnet) { $env:DOTNET_ROOT = Split-Path $userDotnet }
    $env:DOTNET_NOLOGO = 1; $env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
    foreach ($proj in 'src\UltraRT.Probe\UltraRT.Probe.csproj', 'src\UltraRT\UltraRT.csproj') {
        & $dotnet build (Join-Path $root $proj) -c Release -v q -nologo 2>&1 | Select-String ' error |Deployed' | ForEach-Object Line | Select-Object -Unique
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
    }
}
