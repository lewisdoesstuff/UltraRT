# Runs a benchmark suite unattended and prints the summary; optionally compares against an earlier run.
# Usage:
#   .\scripts\bench.ps1                         # short suite
#   .\scripts\bench.ps1 -Suite full
#   .\scripts\bench.ps1 -Compare bench\results\20261113-170000_short
# Results go to bench\results\<timestamp>_<suite>\ (results.json, summary.md, screenshots). Saves, prefs and Steam
# stats are blocked in-game for the whole run.
param(
    [string] $Suite = 'short',
    [string] $Compare,
    [double] $Scale = 0.5,
    [switch] $NoGpuTimer,
    [string[]] $Extra = @(),
    [string] $GameDir = $(if ($env:ULTRAKILL_DIR) { $env:ULTRAKILL_DIR } else { 'D:\SteamLibrary\steamapps\common\ULTRAKILL' }),
    [int] $TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$suitePath = if (Test-Path $Suite) { (Resolve-Path $Suite).Path } else { Join-Path $root "bench\suites\$Suite.json" }
if (-not (Test-Path $suitePath)) { throw "Suite not found: $suitePath" }
$suiteName = [IO.Path]::GetFileNameWithoutExtension($suitePath)

$commit = (git -C $root rev-parse --short HEAD 2>$null)
if (git -C $root status --porcelain 2>$null) { $commit += '-dirty' }
$outDir = Join-Path $root "bench\results\$(Get-Date -Format 'yyyyMMdd-HHmmss')_$suiteName"
New-Item -ItemType Directory -Force $outDir | Out-Null

$gameArgs = @('-force-d3d12', '-ultrart-bench', "`"$suitePath`"", '-ultrart-bench-out', "`"$outDir`"",
    '-ultrart-bench-tag', $commit, '-ultrart-scale', $Scale.ToString([Globalization.CultureInfo]::InvariantCulture), '-ultrart-quit')
if (-not $NoGpuTimer) { $gameArgs += '-ultrart-gputimer' }
if ($Extra) { $gameArgs += @($Extra | Where-Object { $_ }) }

$exe = Join-Path $GameDir 'ULTRAKILL.exe'
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $exe -ArgumentList $gameArgs -WorkingDirectory $GameDir -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Stop-Process -Id $p.Id -Force
    Write-Warning "Timed out after $TimeoutSec s; killed. Partial results (if any) in $outDir"
}
"Ran $suiteName in $([int]$sw.Elapsed.TotalSeconds)s -> $outDir"

$summary = Join-Path $outDir 'summary.md'
if (Test-Path $summary) { Get-Content $summary -Encoding UTF8 } else {
    Write-Warning 'No summary written. Last plugin log lines:'
    Get-Content (Join-Path $GameDir 'BepInEx\LogOutput.log') | Select-String 'UltraRT\]' | Select-Object -Last 15 | ForEach-Object Line
    exit 1
}

if ($Compare) {
    $baseFile = if (Test-Path (Join-Path $Compare 'results.json')) { Join-Path $Compare 'results.json' } else { Join-Path $root "bench\results\$Compare\results.json" }
    $base = Get-Content $baseFile -Raw | ConvertFrom-Json
    $cur = Get-Content (Join-Path $outDir 'results.json') -Raw | ConvertFrom-Json
    ""
    "## vs $($base.Tag) ($($base.Started))"
    "| Scenario | Mode | Avg ms (base -> now) | delta ms | fps (base -> now) | GPU total (base -> now) |"
    "|---|---|---|---|---|---|"
    foreach ($sc in $cur.Scenarios) {
        $b = $base.Scenarios | Where-Object Name -eq $sc.Name | Select-Object -First 1
        if (-not $b) { continue }
        foreach ($m in $sc.Modes.PSObject.Properties) {
            $bm = $b.Modes.PSObject.Properties[$m.Name]
            if (-not $bm) { continue }
            $n = $m.Value; $o = $bm.Value
            $gpuN = if ($n.GpuMs.total) { '{0:F3}' -f $n.GpuMs.total } else { '-' }
            $gpuO = if ($o.GpuMs.total) { '{0:F3}' -f $o.GpuMs.total } else { '-' }
            '| {0} | {1} | {2:F3} -> {3:F3} | {4:+0.000;-0.000} | {5:F0} -> {6:F0} | {7} -> {8} |' -f $sc.Name, $m.Name, $o.AvgMs, $n.AvgMs, ($n.AvgMs - $o.AvgMs), $o.AvgFps, $n.AvgFps, $gpuO, $gpuN
        }
    }
}
