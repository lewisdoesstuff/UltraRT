# Downloads the parts of NVIDIA's public DLSS SDK (github.com/NVIDIA/DLSS) that UltraRT.Native needs for DLSS Ray
# Reconstruction into lib\dlss\ (gitignored: NVIDIA's files are never committed; see docs/dlss.md, "License").
#   lib\dlss\include\*.h                  NGX headers (D3D12 + DLSS-D helpers)
#   lib\dlss\lib\nvsdk_ngx_s.lib          static NGX loader (/MT, matches UltraRT.Native's CRT)
#   lib\dlss\bin\nvngx_dlssd.dll          the Ray Reconstruction runtime (rel); deployed next to UltraRT.Native.dll
#   lib\dlss\LICENSE.txt                  NVIDIA RTX SDKs license
# Usage: .\scripts\fetch-dlss.ps1 [-Version v310.9.16] [-Dev]    (-Dev: the development DLL, with the on-screen debug overlay)
# Does NOT touch the game folder. By running it you accept NVIDIA's license (lib\dlss\LICENSE.txt).
param(
    [string] $Version = 'v310.9.16',
    [switch] $Dev,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'lib\dlss'
$base = "https://raw.githubusercontent.com/NVIDIA/DLSS/$Version"
$headers = @('nvsdk_ngx.h', 'nvsdk_ngx_defs.h', 'nvsdk_ngx_params.h', 'nvsdk_ngx_helpers.h', 'nvsdk_ngx_helpers_d3d.h',
    'nvsdk_ngx_helpers_cuda.h', 'nvsdk_ngx_helpers_vk.h', 'nvsdk_ngx_vk.h', 'nvsdk_ngx_defs_vk.h', 'nvsdk_ngx_loader.h',
    'nvsdk_ngx_standalone_common.h', 'nvsdk_ngx_standalone_cuda.h',
    'nvsdk_ngx_defs_dlssd.h', 'nvsdk_ngx_params_dlssd.h', 'nvsdk_ngx_helpers_dlssd.h', 'nvsdk_ngx_helpers_dlssd_d3d.h',
    'nvsdk_ngx_helpers_dlssd_vk.h', 'nvsdk_ngx_helpers_dlssd_cuda.h')
$dllKind = if ($Dev) { 'dev' } else { 'rel' }
$files = @(
    @{ src = 'LICENSE.txt'; dst = 'LICENSE.txt' },
    @{ src = 'lib/Windows_x86_64/x64/nvsdk_ngx_s.lib'; dst = 'lib\nvsdk_ngx_s.lib' },
    @{ src = "lib/Windows_x86_64/$dllKind/nvngx_dlssd.dll"; dst = 'bin\nvngx_dlssd.dll' }
) + ($headers | ForEach-Object { @{ src = "include/$_"; dst = "include\$_" } })

$stamp = Join-Path $dest 'version.txt'
$want = "$Version $dllKind h2"
if (-not $Force -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $want)) { "DLSS SDK $want already in $dest"; return }
foreach ($f in $files) {
    $out = Join-Path $dest $f.dst
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    Invoke-WebRequest -UseBasicParsing "$base/$($f.src)" -OutFile $out
}
Set-Content -Encoding ascii $stamp $want
"DLSS SDK $want -> $dest ($((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB -as [int]) MB)"
