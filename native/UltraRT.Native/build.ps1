# Builds UltraRT.Native.dll (x64) with MSVC, without CMake/vcpkg.
# Usage: .\native\UltraRT.Native\build.ps1 [-Test] [-OutDir <dir>]
#   -Test  also builds LocatorTest.exe and runs it read-only against the game's UnityPlayer.dll
#
# CRT: /MT (static). The DLL is loaded into a game process we don't control; /MD would make it depend on the
# VC++ 14.5x redistributable being installed (an older vcruntime140.dll already in the process or on the
# system makes LoadLibrary fail with "dependency could not be loaded"). Nothing CRT-owned crosses the DLL
# boundary (only POD out-params and a static string), so the private CRT heap is harmless. Cost: ~100 KB.
param(
    [switch] $Test,
    [string] $OutDir,
    [string] $VsDir = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools',
    [string] $PluginApi = 'C:\Program Files\Unity\Hub\Editor\2022.3.29f1\Editor\Data\PluginAPI',
    [string] $UnityPlayer = $(Join-Path $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }) 'UnityPlayer.dll')
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $src 'bin' }
$obj = Join-Path $src 'obj'
New-Item -ItemType Directory -Force $OutDir, $obj | Out-Null

$vcvars = Join-Path $VsDir 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($install) { $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat' }
    }
}
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found (pass -VsDir)" }
if (-not (Test-Path (Join-Path $PluginApi 'IUnityGraphicsD3D12.h'))) { throw "Unity PluginAPI headers not found at $PluginApi (pass -PluginApi)" }

$cflags = "/nologo /c /O2 /MT /EHsc /W4 /permissive- /std:c++17 /DUNICODE /D_UNICODE /DNDEBUG /Zi /I`"$PluginApi`" /Fo`"$obj\\`" /Fd`"$obj\\vc.pdb`""
$dll = Join-Path $OutDir 'UltraRT.Native.dll'
# DLSS Ray Reconstruction (docs/dlss.md): needs NVIDIA's SDK in lib\dlss (scripts\fetch-dlss.ps1, never committed).
# Without it the stub is built and RR reports itself unavailable.
$dlssSdk = Join-Path (Split-Path (Split-Path $src -Parent) -Parent) 'lib\dlss'
$ngxLib = Join-Path $dlssSdk 'lib\nvsdk_ngx_s.lib'
if (Test-Path $ngxLib) {
    $dlssSrc = "`"$src\Dlss.cpp`""
    $dlssObj = "`"$obj\Dlss.obj`" `"$ngxLib`" advapi32.lib version.lib user32.lib shell32.lib ole32.lib setupapi.lib"
    $dlssFlags = "/I`"$dlssSdk\include`""
} else {
    Write-Warning "DLSS SDK not found in $dlssSdk (scripts\fetch-dlss.ps1): building without Ray Reconstruction"
    $dlssSrc = "`"$src\DlssStub.cpp`""
    $dlssObj = "`"$obj\DlssStub.obj`""
    $dlssFlags = ''
}
$cmds = @(
    "cl $cflags $dlssFlags `"$src\GpuTimer.cpp`" `"$src\UnityLocator.cpp`" $dlssSrc",
    "link /nologo /DLL /DEBUG /OPT:REF /OPT:ICF /MACHINE:X64 /OUT:`"$dll`" /PDB:`"$OutDir\UltraRT.Native.pdb`" `"$obj\GpuTimer.obj`" `"$obj\UnityLocator.obj`" $dlssObj kernel32.lib"
)
if ($Test) {
    $exe = Join-Path $obj 'LocatorTest.exe'
    $cmds += "cl $cflags `"$src\LocatorTest.cpp`""
    $cmds += "link /nologo /OUT:`"$exe`" `"$obj\LocatorTest.obj`" `"$obj\UnityLocator.obj`" kernel32.lib"
}

$bat = Join-Path $obj 'build.cmd'
# vcvars64 can return a non-zero errorlevel for harmless warnings (e.g. vswhere not on PATH): check for cl instead.
Set-Content -Encoding ascii $bat (@('@echo off', "call `"$vcvars`" >nul 2>&1", 'where cl >nul 2>&1 || (echo vcvars64 did not put cl.exe on PATH & exit /b 1)') + ($cmds | ForEach-Object { "$_ || exit /b 1" }))
& cmd.exe /c "`"$bat`"" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native build failed (exit $LASTEXITCODE)" }
"Built $dll"

if ($Test) {
    if (-not (Test-Path $UnityPlayer)) { throw "UnityPlayer.dll not found at $UnityPlayer" }
    & $exe $UnityPlayer
    if ($LASTEXITCODE -ne 0) { throw "Locator test failed against $UnityPlayer" }
}

