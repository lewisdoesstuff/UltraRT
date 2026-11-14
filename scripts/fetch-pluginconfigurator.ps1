# Downloads PluginConfigurator (the ULTRAKILL options-menu config library UltraRT's settings menu uses) from Thunderstore
# into lib\ so the plugin can be compiled with the menu. This does NOT touch the game folder: to use the menu in game,
# install PluginConfigurator with r2modman / Thunderstore Mod Manager (docs/settings.md).
# Usage: .\scripts\fetch-pluginconfigurator.ps1 [-Version 1.10.3]
param(
    [string] $Version = '1.10.3'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root 'lib'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pluginconfigurator-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $lib, $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'pc.zip'
    Invoke-WebRequest -UseBasicParsing "https://thunderstore.io/package/download/EternalsTeam/PluginConfigurator/$Version/" -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $tmp 'x')
    Copy-Item (Join-Path $tmp 'x\plugins\PluginConfigurator\PluginConfigurator.dll') $lib -Force
    Write-Host "PluginConfigurator $Version -> $lib\PluginConfigurator.dll"
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
