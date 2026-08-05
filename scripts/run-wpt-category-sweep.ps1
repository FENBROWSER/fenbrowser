#!/usr/bin/env pwsh
<#
.SYNOPSIS
Resumable per-category WPT sweep for FenBrowser. Runs each top-level WPT directory
(or a user-supplied list) as a separate process and writes per-category result bundles.
#>
param(
    [string]$WptRoot = "",
    [string]$ResultsRoot = "",
    [int]$Processes = 1,
    [int]$TimeoutSeconds = 600,
    [int]$StallTimeoutSec = 35,
    [switch]$Fresh,
    [string[]]$Categories = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

# Resolve the WPT checkout (env WPT_ROOT, sibling dir, or local dir).
. "$PSScriptRoot\resolve-paths.ps1"
$wptRoot = Resolve-WptRoot -ExplicitRoot $WptRoot
if (-not $ResultsRoot) { $ResultsRoot = Join-Path $repoRoot "Results\wpt\categories" }

# Build once
Write-Host "[wpt-sweep] Building FenBrowser.Tooling (Release) ..."
dotnet build "$repoRoot\FenBrowser.Tooling\FenBrowser.Tooling.csproj" -c Release | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Determine categories
if ($Categories.Count -eq 0) {
    # Default: key WPT categories most relevant to a browser engine
    $Categories = @(
        "dom", "html/semantics", "html/dom", "html/rendering",
        "css/selectors", "css/css-flexbox", "css/css-grid",
        "accname", "accessibility", "acid"
    )
}

Write-Host "[wpt-sweep] Categories to run: $($Categories -join ', ')"
Write-Host "[wpt-sweep] Processes=$Processes Timeout=${TimeoutSeconds}s StallTimeout=${StallTimeoutSec}s Fresh=$Fresh"

$totalCategories = $Categories.Count
$completed = 0
$failed = @()
$summaryPath = Join-Path $ResultsRoot "_sweep_summary.json"

foreach ($cat in $Categories) {
    $safeName = $cat -replace '[/\\]', '_'
    $outDir = Join-Path $ResultsRoot "wpt_$safeName"
    $summaryFile = Join-Path $outDir "wpt.summary.json"

    # Skip if not Fresh and a valid result exists
    if (-not $Fresh -and (Test-Path $summaryFile)) {
        try {
            $existing = Get-Content $summaryFile -Raw | ConvertFrom-Json
            if ($existing.TestStart -gt 0) {
                Write-Host "[wpt-sweep] SKIP $cat (already has $($existing.TestStart) test_start events)"
                $completed++
                continue
            }
        } catch {}
    }

    Write-Host "[wpt-sweep] RUN $cat ($($completed + 1)/$totalCategories) ..."
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project $repoRoot\FenBrowser.Tooling\FenBrowser.Tooling.csproj -- wpt --root $WptRoot --tests $cat --processes $Processes --timeout-seconds $TimeoutSeconds --output-dir $outDir" `
        -NoNewWindow -PassThru -Wait

    $sw.Stop()

    if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 1) {
        # exit=0 all expected, exit=1 unexpected results (no metadata) — both OK
        Write-Host "[wpt-sweep] $cat DONE in $([math]::Round($sw.Elapsed.TotalSeconds))s (exit=$($proc.ExitCode))"
        $completed++
    } else {
        Write-Host "[wpt-sweep] $cat FAILED (exit=$($proc.ExitCode), elapsed=$([math]::Round($sw.Elapsed.TotalSeconds))s)"
        $failed += $cat
    }
}

Write-Host "[wpt-sweep] Complete: $completed/$totalCategories categories"
if ($failed.Count -gt 0) {
    Write-Host "[wpt-sweep] Failed: $($failed -join ', ')"
}

# Write sweep summary
$sweepSummary = @{
    StartedAt = (Get-Date -Format "o")
    WptRoot = $WptRoot
    CategoriesTotal = $totalCategories
    CategoriesCompleted = $completed
    FailedCategories = $failed
    ResultsRoot = $ResultsRoot
} | ConvertTo-Json -Depth 3
Set-Content -Path $summaryPath -Value $sweepSummary -Encoding UTF8
Write-Host "[wpt-sweep] Summary: $summaryPath"
