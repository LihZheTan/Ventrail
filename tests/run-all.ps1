# Runs every Ventrail test suite and prints a summary.
#
#   powershell -ExecutionPolicy Bypass -File tests\run-all.ps1              # all suites
#   powershell -ExecutionPolicy Bypass -File tests\run-all.ps1 -Only 12,13  # just some weeks
#   powershell -ExecutionPolicy Bypass -File tests\run-all.ps1 -Benchmark   # also the classifier speed check
#
# Exit code 0 = everything passed. Nothing here changes the Windows Firewall or needs admin rights:
# network tests use loopback (127.0.0.1) ports 45100-48199 only.
param(
    [int[]]$Only,
    [switch]$Benchmark
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$suites = Get-ChildItem $here -Directory -Filter 'Ventrail.Tests.*' |
    Where-Object { $_.Name -notlike '*Benchmark*' } |
    Sort-Object { $_.Name -notlike '*Week*' }, Name   # weeks in order, then Redesign
if ($Only) {
    $suites = $suites | Where-Object { $Only -contains [int]($_.Name -replace '\D', '') }
}

$results = @()
foreach ($suite in $suites) {
    Write-Host "`n=== $($suite.Name) ===" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet run --project $suite.FullName -c Release -v q --nologo 2>&1 | ForEach-Object { "$_" }
    $clock.Stop()
    $output | Where-Object { $_ -match '^(PASS|FAIL|---)|passed' } | ForEach-Object {
        if ($_ -like 'FAIL*') { Write-Host $_ -ForegroundColor Red } else { Write-Host $_ }
    }

    $summary = $output | Where-Object { $_ -match '(\d+) passed, (\d+) failed' } | Select-Object -Last 1
    if ($summary -and $summary -match '(\d+) passed, (\d+) failed') {
        $passed = [int]$Matches[1]; $failed = [int]$Matches[2]
    } else {
        $passed = 0; $failed = 1   # crashed or didn't build
        $output | Select-Object -Last 15 | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
    }
    $results += [pscustomobject]@{ Suite = $suite.Name; Passed = $passed; Failed = $failed; Seconds = [int]$clock.Elapsed.TotalSeconds }
}

if ($Benchmark) {
    Write-Host "`n=== Classifier speed (Week 8) ===" -ForegroundColor Cyan
    & dotnet run --project (Join-Path $here 'Ventrail.Tests.Benchmark.ClassifierSpeed') -c Release -v q --nologo
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
$totalPassed = ($results | Measure-Object Passed -Sum).Sum
$totalFailed = ($results | Measure-Object Failed -Sum).Sum
if ($totalFailed -eq 0) {
    Write-Host "All $totalPassed checks passed." -ForegroundColor Green
    exit 0
}
Write-Host "$totalFailed check(s) failed, $totalPassed passed." -ForegroundColor Red
exit 1
