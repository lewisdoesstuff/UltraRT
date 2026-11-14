# The user's game display settings (monitor, resolution, fullscreen mode) that bench runs put back after every agent
# game (scripts\lib\slots.ps1, Restore-DisplayPrefs). After changing them in game on purpose, save them again:
#   .\scripts\display-prefs.ps1 save      # store the current registry values as the user's
#   .\scripts\display-prefs.ps1 restore   # put the stored values back now (no game may be running)
#   .\scripts\display-prefs.ps1 show
param(
    [Parameter(Mandatory)] [ValidateSet('save', 'restore', 'show')] [string] $Action,
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' })
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
switch ($Action) {
    'save' { if (Save-DisplayPrefs $GameDir) { "saved to $(Get-DisplayPrefsFile $GameDir)" } }
    'restore' {
        if (@(Get-GameProcs).Count -gt 0) { throw 'A game is running; Unity would overwrite the values when it quits. Try again later.' }
        $r = Restore-DisplayPrefs $GameDir
        if ($r) { "restored: $r" } else { 'already as saved' }
    }
    'show' { Get-Content (Get-DisplayPrefsFile $GameDir) }
}
