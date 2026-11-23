# Who used the game and for how long: lock holds (lock-history.csv) and bench runs (results folders in the main repo
# and every worktree of it, from git worktree list) since a given time.
#   .\scripts\bench-stats.ps1                 # last 2 hours
#   .\scripts\bench-stats.ps1 -Since 18:20    # since a time today (or a full date)
param(
    [string] $Since,
    [string] $GameDir,
    [string] $MainRepo = (Split-Path $PSScriptRoot -Parent),
    # Extra folder whose subfolders are checkouts to scan (default $env:ULTRART_WORKTREES); git's worktrees are always scanned.
    [string] $Worktrees = $env:ULTRART_WORKTREES
)
. (Join-Path $PSScriptRoot 'lib\slots.ps1')
if (-not $GameDir) { $GameDir = Get-SlotDir 'main' }
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
$trees = @($MainRepo)
try { $trees += @(& git -C $MainRepo worktree list --porcelain 2>$null | Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring(9) -replace '/', '\' }) } catch { }
if ($Worktrees) { $trees += @(Get-ChildItem $Worktrees -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) }
$seen = @{}
$dirs = @()
foreach ($t in $trees) {
    $k = Get-NormPath $t
    if ($seen.ContainsKey($k)) { continue }
    $seen[$k] = $true
    $dirs += @(Get-ChildItem (Join-Path $t 'bench\results\*') -Directory -ErrorAction SilentlyContinue)
}
$dirs | Where-Object { $_.CreationTime -gt $t0 -and $_.Name -match '^\d{8}-\d{6}_' } | ForEach-Object {
    $files = @(Get-ChildItem $_.FullName -File)
    $end = if ($files) { ($files | Measure-Object LastWriteTime -Maximum).Maximum } else { $_.CreationTime }
    $sum = Join-Path $_.FullName 'summary.md'
    $n = if (Test-Path $sum) { @(Select-String -Path $sum -Pattern '^\| \d+ \|').Count } else { 0 }
    $tree = $_.Parent.Parent.Parent.Name
    [pscustomobject]@{
        start = $_.CreationTime.ToString('HH:mm:ss'); suite = ($_.Name -replace '^\d{8}-\d{6}_', '')
        min = [math]::Round(($end - $_.CreationTime).TotalMinutes, 1); scen = $n
        shots = @($files | Where-Object Extension -eq '.png').Count; tree = $tree
    }
} | Sort-Object start | Format-Table -AutoSize | Out-String -Width 220
