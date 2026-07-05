param(
    [string[]]$Categories = @(),
    [int]$TimeoutSeconds = 300,
    [string]$ResultsDir = "Results/wpt_categories",
    [switch]$Fresh = $false,
    [switch]$ListOnly = $false,
    [int]$StallTimeoutSec = 60
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
Set-Location $repoRoot

$toolingExe = "$repoRoot\FenBrowser.Tooling\bin\Release\net10.0\FenBrowser.Tooling.exe"
if (-not (Test-Path $toolingExe)) {
    Write-Host "[wpt-sweep] FenBrowser.Tooling.exe not found at $toolingExe. Build first." -ForegroundColor Red
    exit 1
}

# WPT test categories - directories under WPT root that contain test files.
# Excludes infrastructure dirs and third_party reference data.
$skipDirs = @(
    "tools", "resources", "common", "media", "docs", "conformance-checkers",
    "webdriver", "_venv3", ".git", "__pycache__", "third_party",
    "fonts", "interfaces", "webgpu"  # empty dirs
)

$wptRoot = "C:\Users\udayk\Videos\wpt"
$allCategories = Get-ChildItem -Path $wptRoot -Directory |
    Where-Object { $skipDirs -notcontains $_.Name -and -not $_.Name.StartsWith('.') } |
    Sort-Object Name

if ($ListOnly) {
    foreach ($cat in $allCategories) {
        $count = (Get-ChildItem -Path $cat.FullName -Recurse -File -Include "*.html","*.js","*.xhtml","*.svg","*.xml" -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Host "$($cat.Name): $count"
    }
    exit 0
}

# Determine categories to run
if ($Categories.Count -eq 0) {
    $catsToRun = $allCategories
} else {
    $catsToRun = $allCategories | Where-Object { $Categories -contains $_.Name }
}

Write-Host "[wpt-sweep] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') Starting sweep over $($catsToRun.Count) categories" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Results dir: $repoRoot\$ResultsDir"
Write-Host "[wpt-sweep] Timeout per category: ${TimeoutSeconds}s, stall timeout: ${StallTimeoutSec}s"

$resultsDir = Join-Path $repoRoot $ResultsDir
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

$allResults = @()
$totalPassed = 0
$totalFailed = 0
$totalCrashed = 0
$totalTimeout = 0
$totalTests = 0

foreach ($cat in $catsToRun) {
    $catName = $cat.Name
    $catResultFile = Join-Path $resultsDir "$catName.json"

    if (-not $Fresh -and (Test-Path $catResultFile)) {
        try {
            $existing = Get-Content $catResultFile -Raw | ConvertFrom-Json
            Write-Host "[wpt-sweep] SKIP $catName (already run: $($existing.total) tests)" -ForegroundColor DarkGray
            $allResults += $existing
            continue
        } catch {
            Write-Host "[wpt-sweep] Corrupt result for $catName, re-running" -ForegroundColor Yellow
        }
    }

    $catOutputDir = Join-Path $resultsDir "raw_$catName"
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

    Write-Host "[wpt-sweep] [$timestamp] START $catName" -ForegroundColor Yellow

    $argList = @(
        "wpt",
        "--tests", "$catName/",
        "--timeout-seconds", [string]$TimeoutSeconds,
        "--output-dir", $catOutputDir
    )

    $proc = Start-Process -FilePath $toolingExe -ArgumentList $argList -NoNewWindow -PassThru
    $startTime = Get-Date

    # Stall watchdog: if no output progress for StallTimeoutSec, kill
    $resultJsonPath = Join-Path $catOutputDir "wpt.raw.json"
    $lastStartCount = 0
    $stallSeconds = 0

    do {
        Start-Sleep -Seconds 5

        # Check for progress by counting test_start entries
        if (Test-Path $resultJsonPath) {
            try {
                $currentStartCount = (Select-String -Path $resultJsonPath -Pattern '"action": "test_start"' | Measure-Object).Count
                if ($currentStartCount -ne $lastStartCount) {
                    $lastStartCount = $currentStartCount
                    $stallSeconds = 0
                    Write-Host "[wpt-sweep]   $catName progress: $currentStartCount tests started" -ForegroundColor DarkGray
                } else {
                    $stallSeconds += 5
                }
            } catch {
                $stallSeconds += 5
            }
        } else {
            $stallSeconds += 5
        }

        if ($stallSeconds -ge $StallTimeoutSec) {
            Write-Host "[wpt-sweep]   $catName STALLED (no progress for ${StallTimeoutSec}s), killing..." -ForegroundColor Red
            try { $proc.Kill($true) } catch {}
            Start-Sleep -Seconds 2
            break
        }

        $elapsed = ((Get-Date) - $startTime).TotalSeconds
        if ($elapsed -gt $TimeoutSeconds) {
            Write-Host "[wpt-sweep]   $catName TIMEOUT ($TimeoutSeconds s), killing..." -ForegroundColor Red
            try { $proc.Kill($true) } catch {}
            Start-Sleep -Seconds 2
            break
        }
    } while (-not $proc.HasExited)

    # Clean up any leftover processes
    try {
        Get-Process -Name "FenBrowser.Tooling" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Get-Process -Name "FenBrowser.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Get-Process -Name "python" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    } catch {}

    Start-Sleep -Seconds 3

    # Parse results from raw log
    $catResult = @{
        category = $catName
        passed = 0
        failed = 0
        crashed = 0
        timeout = 0
        skipped = 0
        total = 0
        error = $false
        errorMessage = ""
    }

    if (Test-Path $resultJsonPath) {
        try {
            $rawLines = Get-Content $resultJsonPath
            foreach ($line in $rawLines) {
                if ($line -match '"action": "test_end"') {
                    $catResult.total++
                    if ($line -match '"status": "(OK|PASS)"') {
                        $catResult.passed++
                    } elseif ($line -match '"status": "CRASH"') {
                        $catResult.crashed++
                    } elseif ($line -match '"status": "TIMEOUT"') {
                        $catResult.timeout++
                    } elseif ($line -match '"status": "(ERROR|FAIL)"') {
                        $catResult.failed++
                    } elseif ($line -match '"status": "SKIP"') {
                        $catResult.skipped++
                    } else {
                        $catResult.failed++
                    }
                }
            }
        } catch {
            $catResult.error = $true
            $catResult.errorMessage = "Failed to parse raw log: $_"
        }
    } else {
        $catResult.error = $true
        $catResult.errorMessage = "No raw log produced"
    }

    if ($catResult.total -eq 0 -and -not $catResult.error) {
        $catResult.error = $true
        $catResult.errorMessage = "Zero tests completed"
    }

    $catResultJson = $catResult | ConvertTo-Json -Compress
    $catResultJson | Set-Content -Path $catResultFile

    $totalPassed += $catResult.passed
    $totalFailed += ($catResult.failed + $catResult.crashed + $catResult.timeout)
    $totalCrashed += $catResult.crashed
    $totalTimeout += $catResult.timeout
    $totalTests += $catResult.total

    $rate = if ($catResult.total -gt 0) { [math]::Round(100 * $catResult.passed / $catResult.total, 1) } else { 0 }
    $status = if ($catResult.error) { "ERR" } elseif ($rate -ge 90) { "PASS" } else { "" }
    Write-Host "[wpt-sweep] DONE $catName : $($catResult.passed)/$($catResult.total) ($rate%) pass=$($catResult.passed) crash=$($catResult.crashed) timeout=$($catResult.timeout) fail=$($catResult.failed) $status" -ForegroundColor $(if ($rate -ge 90) { "Green" } elseif ($catResult.error) { "Red" } else { "White" })

    $allResults += $catResult

    # Small pause between categories
    Start-Sleep -Seconds 2
}

# Write aggregate summary
$summary = @{
    generatedAt = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssK")
    totalCategories = $allResults.Count
    totalTests = $totalTests
    totalPassed = $totalPassed
    totalFailed = $totalFailed
    totalCrashed = $totalCrashed
    totalTimeout = $totalTimeout
    passRate = if ($totalTests -gt 0) { [math]::Round(100 * $totalPassed / $totalTests, 2) } else { 0 }
    categories = $allResults | Sort-Object { -$_.total }
}

$summaryPath = Join-Path $resultsDir "_summary.json"
$summary | ConvertTo-Json -Depth 10 | Set-Content -Path $summaryPath

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "[wpt-sweep] SWEEP COMPLETE" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Categories: $($summary.totalCategories)" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Total tests: $totalTests" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Passed: $totalPassed ($($summary.passRate)%)" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Failed: $totalFailed (crash=$totalCrashed timeout=$totalTimeout)" -ForegroundColor Cyan
Write-Host "[wpt-sweep] Summary: $summaryPath" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
