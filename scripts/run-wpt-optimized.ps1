# WPT Optimized Sweep — batches categories, runs 6 browser instances in parallel
param(
    [switch]$Fresh = $false,
    [int]$Processes = 6,
    [int]$TimeoutSeconds = 120,
    [int]$StallTimeoutSec = 20,
    [string]$WptRoot = ""
)

$ErrorActionPreference = "Continue"
$repoRoot = $PSScriptRoot | Split-Path -Parent
Set-Location $repoRoot

$toolingExe = "$repoRoot\FenBrowser.Tooling\bin\Release\net10.0\FenBrowser.Tooling.exe"
if (-not (Test-Path $toolingExe)) {
    Write-Host "ERROR: Build FenBrowser.Tooling first" -ForegroundColor Red
    exit 1
}

# Resolve the WPT checkout (env WPT_ROOT, sibling dir, or local dir).
. "$PSScriptRoot\resolve-paths.ps1"
$wptRoot = Resolve-WptRoot -ExplicitRoot $WptRoot

# Skip non-test dirs
$skipDirs = @(
    "tools", "resources", "common", "media", "docs", "conformance-checkers",
    "webdriver", "_venv3", ".git", "__pycache__", "third_party",
    "fonts", "interfaces", "webgpu"
)
$allCategories = Get-ChildItem -Path $wptRoot -Directory |
    Where-Object { $skipDirs -notcontains $_.Name -and -not $_.Name.StartsWith('.') } |
    Sort-Object Name

$resultsDir = Join-Path $repoRoot "Results/wpt_categories"
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

# ── Batch strategy: group small categories together ──
# First, estimate test counts from file counts (rough proxy)
$catSizes = @{}
foreach ($cat in $allCategories) {
    $count = (Get-ChildItem -Path $cat.FullName -Recurse -File -Include "*.html","*.js","*.xhtml" -ErrorAction SilentlyContinue | Measure-Object).Count
    $catSizes[$cat.Name] = $count
}

# Build batches: categories with <50 files get batched; large ones run alone
$batchMaxTests = 100  # max estimated tests per batch
$batches = @()
$currentBatch = @()
$currentBatchSize = 0

foreach ($cat in $allCategories) {
    $catName = $cat.Name
    $size = $catSizes[$catName]

    # Large categories run solo
    if ($size -gt 200) {
        # Flush current batch first
        if ($currentBatch.Count -gt 0) {
            $batches += @{ Categories = $currentBatch; Label = ($currentBatch -join '+') }
            $currentBatch = @()
            $currentBatchSize = 0
        }
        $batches += @{ Categories = @($catName); Label = $catName }
        continue
    }

    # Small/medium categories: batch together
    if ($currentBatchSize + $size -gt $batchMaxTests -and $currentBatch.Count -gt 0) {
        $batches += @{ Categories = $currentBatch; Label = ($currentBatch -join '+') }
        $currentBatch = @()
        $currentBatchSize = 0
    }
    $currentBatch += $catName
    $currentBatchSize += $size
}
# Flush remaining
if ($currentBatch.Count -gt 0) {
    $batches += @{ Categories = $currentBatch; Label = ($currentBatch -join '+') }
}

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "[wpt-opt] OPTIMIZED SWEEP" -ForegroundColor Cyan
Write-Host "[wpt-opt] Categories: $($allCategories.Count) → Batches: $($batches.Count)" -ForegroundColor Cyan
Write-Host "[wpt-opt] Processes per batch: $Processes (parallel browser instances)" -ForegroundColor Cyan
Write-Host "[wpt-opt] Timeout: ${TimeoutSeconds}s  Stall: ${StallTimeoutSec}s" -ForegroundColor Cyan
Write-Host "[wpt-opt] Estimated speedup: 6-10x over serial" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

$totalPassed = 0; $totalFailed = 0; $totalCrashed = 0; $totalTimeout = 0; $totalTests = 0
$batchNum = 0

foreach ($batch in $batches) {
    $batchNum++
    $label = $batch.Label
    $cats = $batch.Categories
    $resultFile = Join-Path $resultsDir "$($cats[0]).json"  # use first category as key

    # Resumability: skip if all categories in batch have results
    $allDone = $true
    foreach ($c in $cats) {
        $rf = Join-Path $resultsDir "$c.json"
        if (-not (Test-Path $rf)) { $allDone = $false; break }
    }
    if ($allDone -and -not $Fresh) {
        Write-Host "[wpt-opt] SKIP batch $batchNum/$($batches.Count): $label (done)" -ForegroundColor DarkGray
        foreach ($c in $cats) {
            $rf = Join-Path $resultsDir "$c.json"
            try {
                $j = Get-Content $rf -Raw | ConvertFrom-Json
                $totalPassed += $j.passed; $totalTests += $j.total
                $totalCrashed += $j.crashed; $totalTimeout += $j.timeout; $totalFailed += $j.failed
            } catch {}
        }
        continue
    }

    $outputDir = Join-Path $resultsDir "raw_$($cats[0])"
    $testPaths = ($cats | ForEach-Object { "$_/" }) -join ","
    $ts = Get-Date -Format "HH:mm:ss"

    $estSize = if ($catSizes.ContainsKey($cats[0])) { $catSizes[$cats[0]] } else { 0 }
    Write-Host "[wpt-opt] [$ts] BATCH $batchNum/$($batches.Count): $label ($($cats.Count) cats, ~$estSize tests)" -ForegroundColor Yellow

    $argList = @(
        "wpt",
        "--tests", $testPaths,
        "--timeout-seconds", [string]$TimeoutSeconds,
        "--processes", [string]$Processes,
        "--output-dir", $outputDir
    )

    $proc = Start-Process -FilePath $toolingExe -ArgumentList $argList -NoNewWindow -PassThru
    $startTime = Get-Date
    $resultJsonPath = Join-Path $outputDir "wpt.raw.json"
    $lastCount = 0; $stallSec = 0

    # Stall watchdog
    do {
        Start-Sleep -Seconds 5
        if (Test-Path $resultJsonPath) {
            try {
                $current = (Select-String -Path $resultJsonPath -Pattern '"action": "test_end"' | Measure-Object).Count
                if ($current -ne $lastCount) {
                    $lastCount = $current; $stallSec = 0
                    Write-Host "[wpt-opt]   $label : $current tests done" -ForegroundColor DarkGray
                } else { $stallSec += 5 }
            } catch { $stallSec += 5 }
        } else { $stallSec += 5 }

        if ($stallSec -ge $StallTimeoutSec) {
            Write-Host "[wpt-opt]   $label STALLED, killing..." -ForegroundColor Red
            try { $proc.Kill($true) } catch {}
            Start-Sleep -Seconds 2
            break
        }
        if (((Get-Date) - $startTime).TotalSeconds -gt $TimeoutSeconds) {
            Write-Host "[wpt-opt]   $label TIMEOUT, killing..." -ForegroundColor Red
            try { $proc.Kill($true) } catch {}
            Start-Sleep -Seconds 2
            break
        }
    } while (-not $proc.HasExited)

    # Cleanup
    try {
        Get-Process -Name "FenBrowser.Tooling" -ErrorAction SilentlyContinue | Stop-Process -Force
        Get-Process -Name "FenBrowser.Host" -ErrorAction SilentlyContinue | Stop-Process -Force
        Get-Process -Name "python" -ErrorAction SilentlyContinue | Stop-Process -Force
    } catch {}
    Start-Sleep -Seconds 3

    # Parse and split results per category
    if (Test-Path $resultJsonPath) {
        try {
            $rawLines = Get-Content $resultJsonPath
            $perCat = @{}  # category → {passed, total, crashed, timeout, failed}
            foreach ($cat in $cats) { $perCat[$cat] = @{ passed=0; total=0; crashed=0; timeout=0; failed=0; error=$false } }

            foreach ($line in $rawLines) {
                if ($line -match '"action": "test_end"') {
                    # Extract test path to determine category
                    $testMatch = [regex]::Match($line, '"test":\s*"/([^/"]+)/')
                    if ($testMatch.Success) {
                        $testCat = $testMatch.Groups[1].Value
                        if ($perCat.ContainsKey($testCat)) {
                            $perCat[$testCat].total++
                            if ($line -match '"status": "OK"') { $perCat[$testCat].passed++ }
                            elseif ($line -match '"status": "CRASH"') { $perCat[$testCat].crashed++ }
                            elseif ($line -match '"status": "TIMEOUT"') { $perCat[$testCat].timeout++ }
                            else { $perCat[$testCat].failed++ }
                        }
                    }
                }
            }

            # Write per-category result files
            foreach ($cat in $cats) {
                $cr = $perCat[$cat]
                $rf = Join-Path $resultsDir "$cat.json"
                $crJson = @{
                    category = $cat
                    passed = $cr.passed
                    total = $cr.total
                    crashed = $cr.crashed
                    timeout = $cr.timeout
                    failed = $cr.failed
                    skipped = 0
                    error = ($cr.total -eq 0)
                    errorMessage = if ($cr.total -eq 0) { "No tests discovered in batch" } else { "" }
                }
                $crJson | ConvertTo-Json -Compress | Set-Content -Path $rf

                $totalPassed += $cr.passed
                $totalFailed += $cr.failed
                $totalCrashed += $cr.crashed
                $totalTimeout += $cr.timeout
                $totalTests += $cr.total

                $rate = if ($cr.total -gt 0) { [math]::Round(100 * $cr.passed / $cr.total, 1) } else { 0 }
                $status = if ($cr.total -eq 0) { "EMPTY" } elseif ($rate -ge 90) { "GOOD" } else { "" }
                Write-Host "[wpt-opt]   $cat : $($cr.passed)/$($cr.total) ($rate%) $status" -ForegroundColor $(if ($rate -ge 90) { "Green" } elseif ($cr.total -eq 0) { "DarkGray" } else { "White" })
            }
        } catch {
            Write-Host "[wpt-opt]   $label : PARSE ERROR: $_" -ForegroundColor Red
            foreach ($cat in $cats) {
                $rf = Join-Path $resultsDir "$cat.json"
                if (-not (Test-Path $rf)) {
                    @{category=$cat; passed=0; total=0; crashed=0; timeout=0; failed=0; error=$true; errorMessage="Parse failure"} | ConvertTo-Json -Compress | Set-Content -Path $rf
                }
            }
        }
    } else {
        Write-Host "[wpt-opt]   $label : NO OUTPUT (crashed before any tests?)" -ForegroundColor Red
    }
}

# ── Aggregate summary ──
$summary = @{
    generatedAt = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssK")
    totalCategories = $allCategories.Count
    totalBatches = $batches.Count
    totalTests = $totalTests
    totalPassed = $totalPassed
    totalFailed = $totalFailed
    totalCrashed = $totalCrashed
    totalTimeout = $totalTimeout
    passRate = if ($totalTests -gt 0) { [math]::Round(100 * $totalPassed / $totalTests, 2) } else { 0 }
    processesPerBatch = $Processes
}

$summaryPath = Join-Path $resultsDir "_summary.json"
$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryPath

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "[wpt-opt] SWEEP COMPLETE" -ForegroundColor Cyan
Write-Host "[wpt-opt] Batches: $($batches.Count)  Categories: $($allCategories.Count)" -ForegroundColor Cyan
Write-Host "[wpt-opt] Total tests: $totalTests  Passed: $totalPassed ($($summary.passRate)%)" -ForegroundColor Cyan
Write-Host "[wpt-opt] Failed: $totalFailed  Crashed: $totalCrashed  Timeout: $totalTimeout" -ForegroundColor Cyan
Write-Host "[wpt-opt] Summary: $summaryPath" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
