# Builds the Thunderstore packages of UltraRT (docs/release.md). Outputs go to <repo>\dist (gitignored).
# Usage:
#   .\scripts\package.ps1                         build from a clean commit, then dist\UltraRT-<version>.zip
#   .\scripts\package.ps1 -SkipIconCheck          no icon yet: warn and leave icon.png out (Thunderstore rejects such a zip)
#   .\scripts\package.ps1 -IncludeDlss:$false     without NVIDIA's nvngx_dlssd.dll (RR then needs the player to add it)
#   .\scripts\package.ps1 -SkipBuild              reuse dist\stage (it must come from this commit)
#   .\scripts\package.ps1 -TexturePack            only the optional UltraRT_DetailMaps package, from a local maps cache
# Switches: -AllowDirty (build a working tree with uncommitted changes; the zip is marked as such), -Team <Thunderstore team>
# (only used for the detail-map package's dependency string and the install layout printed at the end), -WebsiteUrl,
# -Icon <png>, -MapsDir <cache folder>, -OutDir <folder>.
# Never touches a game install: the build stages into dist\stage (build.ps1 -StageDir), references are read from the main
# install as for any build. ASCII only, PowerShell 5.1.
param(
    [switch] $AllowDirty,
    [switch] $SkipBuild,
    [switch] $IncludeDlss = $true,
    [switch] $SkipIconCheck,
    [string] $Icon,
    [string] $Team = 'lemmonsltd',
    [string] $WebsiteUrl = 'https://lewisdoesstuff.github.io/UltraRT/',
    [switch] $TexturePack,
    [string] $MapsDir,
    [string] $MapsSource = 'ai',
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
if (-not $Icon) { $Icon = Join-Path $root 'packaging\thunderstore\icon.png' }
$tsDir = Join-Path $root 'packaging\thunderstore'

# ---------------------------------------------------------------- package facts (the one place to change them)
# Dependency strings as the ULTRAKILL community on Thunderstore has them (checked 2026-11-19 against
# https://thunderstore.io/c/ultrakill/api/v1/package/: latest BepInExPack and PluginConfigurator; docs/release.md).
$BepInExPack = 'BepInEx-BepInExPack-5.4.2305'
$PluginConfigurator = 'EternalsTeam-PluginConfigurator-1.10.3'
$Name = 'UltraRT'
$Description = 'Hardware ray tracing and path tracing for ULTRAKILL: shadows, bounce light, reflections, volumetrics, NVIDIA DLSS Ray Reconstruction. Needs a DXR GPU and the launch options -force-d3d12 -force-gfx-jobs legacy.'
$PackName = 'UltraRT_DetailMaps'
$PackDescription = 'Optional surface detail maps for UltraRT: per-texel normal and roughness for the game''s textures, made with PBRify Remix (CC0). UltraRT finds them by itself.'
$LicenseLine = 'MIT (see LICENSE in the package), except the NVIDIA DLSS runtime and NGX helper code, which stay under NVIDIA''s license.'
$PackMarker = 'ultrart-detailmaps.json'   # MaterialMaps.PackMarker
$PackFormat = 1                            # MaterialMaps.PackFormat

# ---------------------------------------------------------------- helpers

function Get-Version {
    [xml] $props = Get-Content (Join-Path $root 'Version.props') -Raw
    $v = [string] ($props.Project.PropertyGroup | ForEach-Object { $_.UltraRTVersion } | Where-Object { $_ } | Select-Object -First 1)
    if ($v -notmatch '^\d+\.\d+\.\d+$') { throw "Version.props: UltraRTVersion '$v' is not Major.Minor.Patch (Thunderstore needs that)" }
    return $v
}

function Test-ManifestFields([string] $name, [string] $description) {
    if ($name -notmatch '^[A-Za-z0-9_]{1,128}$') { throw "Package name '$name': only A-Z a-z 0-9 _ (Thunderstore)" }
    if ($description.Length -gt 250) { throw "Description of $name is $($description.Length) characters; Thunderstore allows 250" }
}

function Write-Utf8NoBom([string] $path, [string] $text) {
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllText($path, ($text -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
}

function Write-Manifest([string] $path, [string] $name, [string] $version, [string] $description, [string[]] $deps) {
    Test-ManifestFields $name $description
    $esc = { param($s) ($s -replace '\\', '\\' -replace '"', '\"') }
    $depJson = ($deps | ForEach-Object { '    "' + (& $esc $_) + '"' }) -join ",`n"
    $json = "{`n  `"name`": `"$name`",`n  `"version_number`": `"$version`",`n  `"website_url`": `"$(& $esc $WebsiteUrl)`",`n" +
            "  `"description`": `"$(& $esc $description)`",`n  `"dependencies`": [`n$depJson`n  ]`n}`n"
    $null = $json | ConvertFrom-Json   # must parse
    Write-Utf8NoBom $path $json
}

# icon.png: 256x256 PNG at the zip root (Thunderstore). Returns $true when it goes into the package.
function Add-Icon([string] $pkgDir) {
    if (-not (Test-Path $Icon)) {
        if ($SkipIconCheck) { Write-Warning "No icon at $Icon (-SkipIconCheck): the zip has no icon.png, and Thunderstore rejects an upload without one"; return $false }
        throw "No icon at $Icon. Add a 256x256 PNG there, or pass -SkipIconCheck for a test package (docs/release.md)."
    }
    Add-Type -AssemblyName System.Drawing
    $img = [Drawing.Image]::FromFile((Resolve-Path $Icon).Path)
    try {
        $ok = $img.Width -eq 256 -and $img.Height -eq 256 -and $img.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Png.Guid
        $size = "$($img.Width)x$($img.Height)"
    } finally { $img.Dispose() }
    if (-not $ok) {
        if ($SkipIconCheck) { Write-Warning "$Icon is $size or not a PNG (needs a 256x256 PNG): included anyway (-SkipIconCheck)" }
        else { throw "$Icon is $size or not a PNG: Thunderstore needs a 256x256 PNG (or pass -SkipIconCheck)" }
    }
    Copy-Item $Icon (Join-Path $pkgDir 'icon.png') -Force
    return $true
}

# A zip with forward-slash entry names (Windows PowerShell's Compress-Archive can write backslashes, which mod managers
# do not unpack as folders). Every file under $dir, relative to it.
function New-PackageZip([string] $dir, [string] $zipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $base = (Resolve-Path $dir).Path.TrimEnd([char]92)
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($f in Get-ChildItem $dir -File -Recurse | Sort-Object FullName) {
            $entry = $f.FullName.Substring($base.Length + 1) -replace '\\', '/'
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, 'Optimal')
        }
    } finally { $zip.Dispose() }
    # Check what was written: the root files, no backslashes.
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        if ($names | Where-Object { $_ -match '\\' }) { throw "zip entry with a backslash in $zipPath" }
        foreach ($need in 'manifest.json', 'README.md') { if ($names -notcontains $need) { throw "$need missing at the root of $zipPath" } }
    } finally { $zip.Dispose() }
    return $names
}

function Get-Commit {
    $head = (git -C $root rev-parse --short HEAD).Trim()
    $dirty = [bool] (git -C $root status --porcelain)
    if ($dirty -and -not $AllowDirty) {
        git -C $root status --short | Select-Object -First 20
        throw 'The working tree has uncommitted changes: commit them, or pass -AllowDirty for a test package (never upload one).'
    }
    if ($dirty) { Write-Warning "Packaging a dirty working tree ($head + changes): for testing only" }
    return [pscustomobject]@{ Head = $head; Dirty = $dirty }
}

$version = Get-Version
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---------------------------------------------------------------- the detail-map package
if ($TexturePack) {
    if (-not $MapsDir) { $MapsDir = Join-Path (Get-SlotDir 'main') "BepInEx\ultrart\materials\$MapsSource" }
    $mapsManifest = Join-Path $MapsDir 'manifest.json'
    if (-not (Test-Path $mapsManifest)) { throw "No maps cache at $MapsDir (manifest.json missing; tools/materials makes it)" }
    $commit = Get-Commit
    $pkgDir = Join-Path $OutDir "pkg\$PackName-$version"
    if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
    $plug = Join-Path $pkgDir "plugins\$MapsSource"
    New-Item -ItemType Directory -Force $plug | Out-Null
    # Only what the plugin reads: manifest.json and the combined detail textures (*_d.png). The cache's _n / _r files are
    # the generator's intermediates.
    Copy-Item $mapsManifest $plug
    $maps = @(Get-ChildItem $MapsDir -File -Filter '*_d.png')
    if ($maps.Count -eq 0) { throw "No *_d.png in $MapsDir" }
    foreach ($f in $maps) { Copy-Item $f.FullName $plug }
    $marker = [ordered]@{ format = $PackFormat; name = $PackName; version = $version; sources = @($MapsSource);
        generator = 'tools/materials (PBRify Remix, CC0)'; made = (Get-Date).ToString('yyyy-MM-dd'); commit = $commit.Head }
    Write-Utf8NoBom (Join-Path $pkgDir "plugins\$PackMarker") (($marker | ConvertTo-Json) + "`n")
    Write-Manifest (Join-Path $pkgDir 'manifest.json') $PackName $version $PackDescription @("$Team-$Name-$version")
    $readme = (Get-Content (Join-Path $tsDir 'detailmaps\README.md') -Raw) -replace '\{\{VERSION\}\}', $version -replace '\{\{COUNT\}\}', $maps.Count -replace '\{\{TEAM\}\}', $Team
    Write-Utf8NoBom (Join-Path $pkgDir 'README.md') $readme
    $hasIcon = Add-Icon $pkgDir
    $zipPath = Join-Path $OutDir "$PackName-$version.zip"
    $names = New-PackageZip $pkgDir $zipPath
    $mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    "Detail-map package: $zipPath ($mb MB, $($names.Count) files, $($maps.Count) maps from $MapsDir$(if (-not $hasIcon) { ', NO icon' }))"
    if ($Team -eq 'Author') { Write-Warning "Dependency '$Team-$Name-$version' uses the placeholder team: pass -Team <your Thunderstore team> for an upload" }
    "r2modman installs it to BepInEx\plugins\$Team-$PackName\ ($PackMarker, $MapsSource\manifest.json, $MapsSource\*_d.png)"
    return
}

# ---------------------------------------------------------------- the UltraRT package
$commit = Get-Commit
$stage = Join-Path $OutDir 'stage'
$dlssDir = Join-Path $root 'lib\dlss'
# The native DLL needs NVIDIA's SDK at build time for Ray Reconstruction (a stub without it), whether or not the runtime
# DLL ships; the settings page needs PluginConfigurator.dll at build time.
if (-not (Test-Path (Join-Path $dlssDir 'lib\nvsdk_ngx_s.lib'))) { throw "lib\dlss missing: run scripts\fetch-dlss.ps1 (or copy lib\dlss from the main checkout). A release without it has no DLSS RR." }
$dlssKind = if (Test-Path (Join-Path $dlssDir 'version.txt')) { (Get-Content (Join-Path $dlssDir 'version.txt') -Raw).Trim() } else { '' }
if ($IncludeDlss -and $dlssKind -notmatch '\brel\b') { throw "lib\dlss\version.txt says '$dlssKind': only NVIDIA's 'rel' nvngx_dlssd.dll may ship, never 'dev' (scripts\fetch-dlss.ps1 without -Dev; docs/dlss.md, License)" }

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -StageDir $stage
}
$stageInfo = Read-JsonFile (Join-Path $stage 'stage.json')
if (-not $stageInfo) { throw "No build in $stage (stage.json missing): run without -SkipBuild" }
if ($stageInfo.commit -ne $commit.Head -or ([bool] $stageInfo.dirty -and -not $AllowDirty)) {
    throw "$stage was built from $($stageInfo.commit)$(if ($stageInfo.dirty) { ' (dirty)' }), HEAD is $($commit.Head): rebuild (no -SkipBuild)"
}

$need = 'UltraRT.dll', 'UltraRT.Native.dll', 'ultrart.shaders', 'ultrart-materials.json', 'patchers\UltraRT.Preload.dll'
foreach ($n in $need) { if (-not (Test-Path (Join-Path $stage $n))) { throw "$n missing from $stage" } }
if (-not (Test-Path (Join-Path $stage 'lights'))) { throw "lights\ (shipped light overrides) missing from $stage" }
# The settings page is compiled in only when PluginConfigurator.dll was found at build time (SettingsMenuUi).
$pluginBytes = [IO.File]::ReadAllBytes((Join-Path $stage 'UltraRT.dll'))
if ([Text.Encoding]::ASCII.GetString($pluginBytes).IndexOf('SettingsMenuUi') -lt 0) {
    throw 'UltraRT.dll was built without the settings page (PluginConfigurator.dll not found at build time: scripts\fetch-pluginconfigurator.ps1)'
}
# The native DLL with Ray Reconstruction links NVIDIA's NGX loader; the stub doesn't.
$nativeText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $stage 'UltraRT.Native.dll')))
if ($nativeText.IndexOf('nvngx', [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'UltraRT.Native.dll is the stub without DLSS RR (lib\dlss missing at build time?)' }

$pkgDir = Join-Path $OutDir "pkg\$Name-$version"
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
$plug = Join-Path $pkgDir 'plugins'
$patch = Join-Path $pkgDir 'patchers'
New-Item -ItemType Directory -Force $plug, $patch | Out-Null
foreach ($n in 'UltraRT.dll', 'UltraRT.Native.dll', 'ultrart.shaders', 'ultrart-materials.json') { Copy-Item (Join-Path $stage $n) $plug }
# Ship top-level light overrides only; subfolders like lights\proposed\ are dev review layers (docs/lighting-overrides.md).
$lightsDst = Join-Path $plug 'lights'
New-Item -ItemType Directory -Force $lightsDst | Out-Null
Get-ChildItem (Join-Path $stage 'lights') -File -Filter *.json | ForEach-Object { Copy-Item $_.FullName $lightsDst }
Copy-Item (Join-Path $stage 'patchers\UltraRT.Preload.dll') $patch
if ($IncludeDlss) {
    $dll = Join-Path $stage 'nvngx_dlssd.dll'
    if (-not (Test-Path $dll)) { throw "nvngx_dlssd.dll missing from $stage (lib\dlss\bin)" }
    $sig = Get-AuthenticodeSignature $dll
    if ($sig.Status -ne 'Valid') { throw "nvngx_dlssd.dll's signature is $($sig.Status): ship NVIDIA's file unmodified and signed" }
    Copy-Item $dll $plug
    # NVIDIA's license and the runtime's third-party notices go beside the DLL (docs/release.md, licensing checklist).
    Copy-Item (Join-Path $dlssDir 'LICENSE.txt') (Join-Path $plug 'NVIDIA-RTX-SDKs-LICENSE.txt')
    Copy-Item (Join-Path $tsDir 'NVIDIA-DLSS-THIRD-PARTY-NOTICES.txt') $plug
}

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $pkgDir 'LICENSE')   # MIT + the 'not covered' list
Write-Manifest (Join-Path $pkgDir 'manifest.json') $Name $version $Description @($BepInExPack, $PluginConfigurator)
$readme = Get-Content (Join-Path $tsDir 'README.md') -Raw
$tokens = @{
    VERSION = $version
    LICENSE_LINE = $LicenseLine
    DLSS_REQUIREMENT = $(if ($IncludeDlss) { " NVIDIA's runtime for it (``nvngx_dlssd.dll``) is included." } else { " This package does not include NVIDIA's runtime (``nvngx_dlssd.dll``); put it in UltraRT's plugin folder to use it." })
    DLSS_FILES = $(if ($IncludeDlss) { " ``nvngx_dlssd.dll`` is NVIDIA's DLSS Ray Reconstruction runtime; its licence and notices are the two ``NVIDIA-*.txt`` files beside it." } else { '' })
    DLSS_CREDITS = $(if ($IncludeDlss) { " The runtime ``nvngx_dlssd.dll`` is (c) NVIDIA Corporation and is distributed under the NVIDIA RTX SDKs license (``NVIDIA-RTX-SDKs-LICENSE.txt``); its third-party notices are in ``NVIDIA-DLSS-THIRD-PARTY-NOTICES.txt``." } else { '' })
}
foreach ($k in $tokens.Keys) { $readme = $readme.Replace("{{$k}}", $tokens[$k]) }
if ($readme -match '\{\{[A-Z_]+\}\}') { throw "README.md template: unknown token $($Matches[0])" }
Write-Utf8NoBom (Join-Path $pkgDir 'README.md') $readme
$changelog = Get-Content (Join-Path $tsDir 'CHANGELOG.md') -Raw
if ($changelog -notmatch "(?m)^## $([regex]::Escape($version))\s*$") { Write-Warning "CHANGELOG.md has no '## $version' section" }
Write-Utf8NoBom (Join-Path $pkgDir 'CHANGELOG.md') $changelog
$hasIcon = Add-Icon $pkgDir

$zipName = "$Name-$version$(if ($commit.Dirty) { '-dirty' }).zip"
$zipPath = Join-Path $OutDir $zipName
$names = New-PackageZip $pkgDir $zipPath
$mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
@{ version = $version; commit = $commit.Head; dirty = $commit.Dirty; dlss = [bool] $IncludeDlss; icon = $hasIcon; zip = $zipName; files = $names } |
    ConvertTo-Json | Set-Content (Join-Path $OutDir "$Name-$version.json") -Encoding ascii
"Package: $zipPath ($mb MB, $($names.Count) files; commit $($commit.Head)$(if ($commit.Dirty) { ' + changes' }); DLSS runtime $(if ($IncludeDlss) { 'included' } else { 'not included' }); icon $(if ($hasIcon) { 'yes' } else { 'NO' }))"
"r2modman installs plugins\ to BepInEx\plugins\$Team-$Name\ and patchers\ to BepInEx\patchers\$Team-$Name\ (manifest.json, README.md, CHANGELOG.md, icon.png also land in the plugins folder)"
