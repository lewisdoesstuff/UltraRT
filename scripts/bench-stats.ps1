# Who used the game and for how long: lock holds (lock-history.csv) and bench runs (results folders in the main repo
# and every worktree) since a given time.
#   .\scripts\bench-stats.ps1                 # last 2 hours
#   .\scripts\bench-stats.ps1 -Since 18:20    # since a time today (or a full date)
param(
    [string] $Since,
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [string] $MainRepo = 'C:\Users\death\.t3\projects\ultrart',
    [string] $Worktrees = 'C:\Users\death\.t3\worktrees\ultrart'
)
$t0 = if ($Since) { [datetime] $Since } else { (Get-Date).AddHours(-2) }
"Since $($t0.ToString('yyyy-MM-dd HH:mm'))"

$hist = Join-Path $GameDir 'BepInEx\ultrart\lock-history.csv'
if (Test-Path $hist) {
    $h = @(Import-Csv $hist | Where-Object { [datetime] $_.end -gt $t0 })
    ''
    '== Lock holds per owner'
    $h | Group-Object owner | Sort-Object { -($_.Group | Measure-Object hold_s -Sum).Sum } | ForEach-Object {
        $s = ($_.Group | Measure-Object hold_s -Sum).Sum; $w = ($_.Group | Measure-Object waited_s -Sum).Sum
        '{0,-18} holds {1,3}   held {2,6:N1} min   waited {3,6:N1} min' -f $_.Name, $_.Count, ($s / 60), ($w / 60)
    }
}

''
'== Bench runs (start, suite, minutes, scenarios, screenshots, where)'
$dirs = @(Get-ChildItem (Join-Path $MainRepo 'bench\results\*') -Directory -ErrorAction SilentlyContinue) +
        @(Get-ChildItem (Join-Path $Worktrees '*\bench\results\*') -Directory -ErrorAction SilentlyContinue)
$dirs | Where-Object { $_.CreationTime -gt $t0 -and $_.Name -match '^\d{8}-\d{6}_' } | ForEach-Object {
    $files = @(Get-ChildItem $_.FullName -File)
    $end = if ($files) { ($files | Measure-Object LastWriteTime -Maximum).Maximum } else { $_.CreationTime }
    $sum = Join-Path $_.FullName 'summary.md'
    $n = if (Test-Path $sum) { @(Select-String -Path $sum -Pattern '^\| \d+ \|').Count } else { 0 }
    $tree = $_.Parent.Parent.Name
    [pscustomobject]@{
        start = $_.CreationTime.ToString('HH:mm:ss'); suite = ($_.Name -replace '^\d{8}-\d{6}_', '')
        min = [math]::Round(($end - $_.CreationTime).TotalMinutes, 1); scen = $n
        shots = @($files | Where-Object Extension -eq '.png').Count; tree = $tree
    }
} | Sort-Object start | Format-Table -AutoSize | Out-String -Width 220
