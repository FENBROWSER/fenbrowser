<#
.SYNOPSIS
Run a set of `dotnet test --filter` suites truthfully: fail the job when any
filter matches ZERO compiled tests (vacuous green) or when any matched test fails.

.DESCRIPTION
CI truth gate. A `dotnet test --filter X` that matches nothing exits 0,
which silently turns quality gates into theater when test files get excluded from
compilation. This script runs each filter, parses the emitted TRX counters, and
fails unless every filter matched >= 1 test and all matched tests passed.

.EXAMPLE
./scripts/ci/Invoke-TestFilters.ps1 -Project FenBrowser.Tests/FenBrowser.Tests.csproj -NoBuild `
  -Filters @("FullyQualifiedName~FenBrowser.Tests.Core.Network.CspPolicyTests")
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string[]]$Filters,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Project)) {
    Write-Error "Project not found: $Project"
    exit 2
}

$resultsRoot = Join-Path 'TestResults' ('filters-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

$failedFilters = @()

foreach ($filter in $Filters) {
    $safeName = ($filter -replace '[^A-Za-z0-9_\-]', '_').Trim('_')
    if ($safeName.Length -gt 100) { $safeName = $safeName.Substring(0, 100) }
    $resultsDir = Join-Path $resultsRoot $safeName
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    Write-Host "==> dotnet test --filter $filter"
    $dotnetArgs = @('test', $Project, '-c', $Configuration)
    if ($NoBuild) { $dotnetArgs += '--no-build' }
    $dotnetArgs += @(
        '--filter', $filter,
        '--logger', "trx;LogFileName=$safeName.trx",
        '--results-directory', $resultsDir,
        '--verbosity', 'minimal'
    )

    & dotnet @dotnetArgs
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Host "::error::dotnet test FAILED for filter '$filter' (exit $exitCode)"
        $failedFilters += $filter
        continue
    }

    $trxFile = Get-ChildItem -Path $resultsDir -Filter '*.trx' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $trxFile) {
        Write-Host "::error::No TRX produced for filter '$filter'; cannot prove any test ran."
        $failedFilters += $filter
        continue
    }

    [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $total = 0
    if ($null -ne $counters -and -not [int]::TryParse([string]$counters.total, [ref]$total)) {
        $total = -1
    }

    if ($total -le 0) {
        Write-Host "::error::Filter '$filter' matched ZERO compiled tests - this gate was vacuously green. Fix the filter or re-include the test file in compilation."
        $failedFilters += $filter
    }
    else {
        Write-Host ("    matched {0} test(s), passed {1}, failed {2}" -f $total, $counters.passed, $counters.failed)
    }
}

if ($failedFilters.Count -gt 0) {
    Write-Host ("Zero-match or failing filters: {0}" -f ($failedFilters -join ', '))
    exit 1
}

Write-Host 'All filters matched at least one test and all matched tests passed.'
exit 0
