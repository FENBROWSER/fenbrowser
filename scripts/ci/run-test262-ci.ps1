<#
.SYNOPSIS
    Runs a stable subset of Test262 tests for CI regression detection.
    Compares pass count against baseline and fails if regression exceeds tolerance.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$BaselinePath = Join-Path $PSScriptRoot '..\..\docs\test262_ci_baseline.json'
$RunnerExe = Join-Path $PSScriptRoot '..\..\FenBrowser.Test262\bin\Release\net8.0\FenBrowser.Test262.exe'
$ResultsPath = Join-Path $PSScriptRoot '..\..\test262-ci-results.json'
$CategoryResultsDir = Join-Path $PSScriptRoot '..\..\Results\ci-regression'
$DefaultTest262Root = Join-Path $PSScriptRoot '..\..\test262'

if (-not (Test-Path $BaselinePath)) {
    Fail "Test262 CI baseline not found at $BaselinePath. Run locally to generate."
}
if (-not (Test-Path $RunnerExe)) {
    Fail "Test262 runner executable not found at $RunnerExe. Ensure the Release runner build step completed."
}

$Test262Root = $env:TEST262_ROOT
if ([string]::IsNullOrWhiteSpace($Test262Root)) {
    $Test262Root = $DefaultTest262Root
}

if (-not (Test-Path (Join-Path $Test262Root 'harness')) -or -not (Test-Path (Join-Path $Test262Root 'test'))) {
    Fail "Test262 root not found at $Test262Root. Set TEST262_ROOT or provision the suite at repo-root test262/."
}

New-Item -ItemType Directory -Force -Path $CategoryResultsDir | Out-Null

$baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
Write-Host "Baseline: $($baseline.passing)/$($baseline.total) passing (subset: $($baseline.subset))"
Write-Host "Test262 root: $Test262Root"

$categories = @(
    @{ Name = 'language/expressions'; Max = 100 },
    @{ Name = 'language/statements'; Max = 100 },
    @{ Name = 'built-ins/Math'; Max = 80 },
    @{ Name = 'built-ins/String'; Max = 80 },
    @{ Name = 'built-ins/Array'; Max = 80 },
    @{ Name = 'built-ins/Number'; Max = 60 }
)

$totalPassed = 0
$totalFailed = 0
$totalTests = 0

foreach ($cat in $categories) {
    Write-Host "`n--- Running category: $($cat.Name) (max $($cat.Max)) ---"

    $categorySlug = $cat.Name -replace '[^A-Za-z0-9._-]', '_'
    $categoryResultsPath = Join-Path $CategoryResultsDir "$categorySlug.json"
    Remove-Item -LiteralPath $categoryResultsPath -ErrorAction SilentlyContinue

    $output = & $RunnerExe run_category $cat.Name --root $Test262Root --max $cat.Max --format json --output $categoryResultsPath 2>&1
    $outputStr = $output -join "`n"
    $runnerExitCode = $LASTEXITCODE

    if (-not (Test-Path $categoryResultsPath)) {
        Fail "Category $($cat.Name) did not produce a JSON result artifact at $categoryResultsPath.`n$outputStr"
    }

    $categoryResults = Get-Content $categoryResultsPath -Raw | ConvertFrom-Json
    if ($null -eq $categoryResults.total -or $null -eq $categoryResults.passed -or $null -eq $categoryResults.failed) {
        Fail "Category $($cat.Name) produced malformed JSON results at $categoryResultsPath.`n$outputStr"
    }

    $passed = [int]$categoryResults.passed
    $failed = [int]$categoryResults.failed
    $categoryTotal = [int]$categoryResults.total

    if (($passed + $failed) -ne $categoryTotal) {
        Fail "Category $($cat.Name) arithmetic mismatch: passed + failed != total in $categoryResultsPath"
    }
    if ($categoryTotal -le 0) {
        Fail "Category $($cat.Name) returned zero executed tests, which indicates a runner/configuration failure.`n$outputStr"
    }

    $totalPassed += $passed
    $totalFailed += $failed
    $totalTests += $categoryTotal
    Write-Host "  Result: $passed passed, $failed failed (runner exit code: $runnerExitCode)"
}

$results = @{
    subset = $baseline.subset
    total = $totalTests
    passing = $totalPassed
    failing = $totalFailed
    timestamp = (Get-Date).ToUniversalTime().ToString('o')
    baseline_passing = $baseline.passing
}
$results | ConvertTo-Json -Depth 3 | Out-File -FilePath $ResultsPath -Encoding utf8
Write-Host "`nResults written to $ResultsPath"

$tolerance = 5
$regression = $baseline.passing - $totalPassed

Write-Host "`n=== Test262 CI Regression Check ==="
Write-Host "Baseline passing: $($baseline.passing)"
Write-Host "Current passing:  $totalPassed"
Write-Host "Regression:       $regression (tolerance: $tolerance)"

if ($env:GITHUB_STEP_SUMMARY) {
    @"
## Test262 CI Regression Check
| Metric | Value |
|--------|-------|
| Baseline passing | $($baseline.passing) |
| Current passing | $totalPassed |
| Total tests | $totalTests |
| Regression | $regression |
| Tolerance | $tolerance |
| Status | $(if ($regression -gt $tolerance) { 'FAIL' } else { 'PASS' }) |
"@ | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

if ($regression -gt $tolerance) {
    Fail "Test262 REGRESSION: $regression tests regressed (tolerance: $tolerance). Baseline: $($baseline.passing), Current: $totalPassed"
}

Write-Host "`nTest262 regression check PASSED."
