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
    # Rebuild even if the inputs are unchanged.
    [switch] $Force,
    # Record the current inputs as matching the DLL already in bin\ (adopt a known-good binary) without building.
    [switch] $Adopt,
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

$cflags = "/nologo /c /O2 /MT /EHsc /W4 /permissive- /std:c++17 /DUNICODE /D_UNICODE /DNDEBUG /Zi /Brepro /I`"$PluginApi`" /Fo`"$obj\\`" /Fd`"$obj\\vc.pdb`""
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
# Rebuild only when the inputs change, and keep the DLL's bytes otherwise: Windows Smart App Control judges every new
# unsigned DLL by its hash, and has blocked some rebuilds of unchanged source (LoadLibrary error 4551 = no RR, no GPU
# timer). /Brepro + a fixed PDB name also make a rebuild of the same source produce the same bytes.
$stamp = Join-Path $OutDir 'inputs.sha256'
$inputFiles = @(Get-ChildItem $src -File -Include *.cpp, *.h, build.ps1 -Recurse | Where-Object { $_.FullName -notlike "$obj*" -and $_.FullName -notlike "$OutDir*" } | Sort-Object FullName)
$inputText = ($inputFiles | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash + ' ' + $_.Name }) -join "`n"
$inputText += "`nsdk=$(Test-Path $ngxLib)"
$inputHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($inputText))).Replace('-', '')
if ($Adopt) {
    if (-not (Test-Path $dll)) { throw "Nothing to adopt: $dll is missing" }
    Set-Content -Encoding ascii $stamp $inputHash; "Adopted $dll for the current inputs"; return
}
if (-not $Force -and -not $Test -and (Test-Path $dll) -and (Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $inputHash) {
    "UltraRT.Native.dll is up to date (inputs unchanged): kept $dll"; return
}
$cmds = @(
    "cl $cflags $dlssFlags `"$src\GpuTimer.cpp`" `"$src\UnityLocator.cpp`" `"$src\Hdr.cpp`" $dlssSrc",
    "link /nologo /DLL /DEBUG /Brepro /PDBALTPATH:UltraRT.Native.pdb /OPT:REF /OPT:ICF /MACHINE:X64 /OUT:`"$dll`" /PDB:`"$OutDir\UltraRT.Native.pdb`" `"$obj\GpuTimer.obj`" `"$obj\UnityLocator.obj`" `"$obj\Hdr.obj`" $dlssObj kernel32.lib user32.lib dxgi.lib d3d12.lib d3dcompiler.lib"
)
if ($Test) {
    $exe = Join-Path $obj 'LocatorTest.exe'
    $cmds += "cl $cflags `"$src\LocatorTest.cpp`""
    $cmds += "link /nologo /OUT:`"$exe`" `"$obj\LocatorTest.obj`" `"$obj\UnityLocator.obj`" kernel32.lib"
    # HdrTest.exe: the HDR swapchain proxy without the game (docs/hdr.md). Built here, run by hand:
    #   obj\HdrTest.exe bin\UltraRT.Native.dll <output folder>     (opens a small window for about a second)
    $cmds += "cl $cflags `"$src\HdrTest.cpp`""
    $cmds += "link /nologo /OUT:`"$obj\HdrTest.exe`" `"$obj\HdrTest.obj`" kernel32.lib user32.lib dxgi.lib d3d12.lib"
}

$bat = Join-Path $obj 'build.cmd'
# vcvars64 can return a non-zero errorlevel for harmless warnings (e.g. vswhere not on PATH): check for cl instead.
Set-Content -Encoding ascii $bat (@('@echo off', "call `"$vcvars`" >nul 2>&1", 'where cl >nul 2>&1 || (echo vcvars64 did not put cl.exe on PATH & exit /b 1)') + ($cmds | ForEach-Object { "$_ || exit /b 1" }))
& cmd.exe /c "`"$bat`"" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Native build failed (exit $LASTEXITCODE)" }
Set-Content -Encoding ascii $stamp $inputHash
"Built $dll"

if ($Test) {
    if (-not (Test-Path $UnityPlayer)) { throw "UnityPlayer.dll not found at $UnityPlayer" }
    & $exe $UnityPlayer
    if ($LASTEXITCODE -ne 0) { throw "Locator test failed against $UnityPlayer" }
}

