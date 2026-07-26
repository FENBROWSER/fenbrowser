[CmdletBinding()]
param(
    [ValidateSet("normal", "workers", "webdriver", "all")]
    [string]$Suite = "normal",
    [int]$Shards = 5,
    [int]$ProcessesPerShard = 4,
    [int]$MaxRestarts = 2,
    [string[]]$Tests = @("dom/"),
    [string]$SelectionFile = "",
    [string]$ResultsRoot = "Results/wpt/sharded",
    [string]$History = "Results/wpt",
    [int]$TimeoutSeconds = 86400,
    [int]$StallTimeoutSeconds = -1,
    [switch]$Replan
)

$ErrorActionPreference = "Stop"

if ($Shards -lt 1) { throw "Shards must be at least 1." }
if ($ProcessesPerShard -lt 1) { throw "ProcessesPerShard must be at least 1." }
if ($StallTimeoutSeconds -lt 0) {
    $StallTimeoutSeconds = if ($Suite -in @("webdriver", "all")) { 240 } else { 90 }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$toolCandidates = @(
    (Join-Path $repoRoot "FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"),
    (Join-Path $repoRoot "FenBrowser.Tooling/bin/Debug/net10.0/FenBrowser.Tooling.exe")
)
$tool = $toolCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $tool) {
    throw "FenBrowser.Tooling.exe was not found. Build FenBrowser.Tooling first."
}

$resultsBase = [IO.Path]::GetFullPath((Join-Path $repoRoot $ResultsRoot))
$historyPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $History))
$suiteRoot = Join-Path $resultsBase $Suite
$planRoot = Join-Path $suiteRoot "plan"
New-Item -ItemType Directory -Force -Path $planRoot | Out-Null

# PowerShell drops an empty native argument. A comma is preserved and the
# tooling parser intentionally normalizes it to an empty positional selection,
# allowing --include-file to be the sole source of test IDs.
$testValue = if ($Tests.Count -gt 0) { $Tests -join "," } else { "," }
$planPath = Join-Path $planRoot "wpt.shard-plan.json"
if ($Replan -or -not (Test-Path -LiteralPath $planPath)) {
    $planArgs = @(
        "wpt",
        "--suite", $Suite,
        "--plan-shards", $Shards,
        "--history", $historyPath,
        "--output-dir", $planRoot,
        "--tests", $testValue,
        "--timeout-seconds", $TimeoutSeconds,
        "--stall-timeout-seconds", "0"
    )
    if ($SelectionFile) {
        $selectionPath = if ([IO.Path]::IsPathRooted($SelectionFile)) {
            [IO.Path]::GetFullPath($SelectionFile)
        } else {
            [IO.Path]::GetFullPath((Join-Path $repoRoot $SelectionFile))
        }
        if (-not (Test-Path -LiteralPath $selectionPath)) {
            throw "SelectionFile was not found: $selectionPath"
        }
        $planArgs += @("--include-file", $selectionPath)
    }
    & $tool @planArgs
    if ($LASTEXITCODE -ne 0) {
        throw "WPT shard planning failed with exit code $LASTEXITCODE."
    }
} else {
    Write-Host "[wpt-shards] resume: reusing saved plan $planPath"
}

$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if ([string]$plan.suite -ne $Suite -or [int]$plan.shardCount -ne $Shards) {
    throw "Saved plan does not match Suite/Shards. Pass -Replan to replace it."
}
$running = [System.Collections.Generic.List[object]]::new()
$conformanceFailures = 0

foreach ($shard in $plan.shards) {
    $shardName = "shard_{0:000}" -f [int]$shard.shard
    if ([int]$shard.testCount -eq 0) {
        Write-Host "[wpt-shards] skip: $shardName has no selected tests"
        continue
    }
    $includeFile = Join-Path $planRoot $shard.includeFile
    $outputDir = Join-Path $suiteRoot $shardName
    $summaryPath = Join-Path $outputDir "wpt.summary.json"
    $hashPath = Join-Path $outputDir "shard.plan.sha256"
    $planHash = (Get-FileHash -LiteralPath $includeFile -Algorithm SHA256).Hash

    $complete = $false
    if ((Test-Path -LiteralPath $summaryPath) -and (Test-Path -LiteralPath $hashPath)) {
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        $savedHash = (Get-Content -LiteralPath $hashPath -Raw).Trim()
        $complete = (-not $summary.TimedOut) -and (-not $summary.Stalled) -and
            ([int]$summary.TestEnd -eq [int]$shard.testCount) -and
            ($savedHash -eq $planHash)
    }

    if ($complete) {
        Write-Host "[wpt-shards] resume: $shardName already complete"
        if ([int]$summary.ExitCode -ne 0) {
            $conformanceFailures++
        }
        continue
    }

    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    Set-Content -LiteralPath $hashPath -Value $planHash -NoNewline
    $arguments = @(
        "wpt",
        "--suite", $Suite,
        "--include-file", $includeFile,
        "--output-dir", $outputDir,
        "--processes", $ProcessesPerShard,
        "--max-restarts", $MaxRestarts,
        "--timeout-seconds", $TimeoutSeconds,
        "--stall-timeout-seconds", $StallTimeoutSeconds,
        "--tests", $testValue
    )
    $quotedArguments = $arguments | ForEach-Object {
        $value = [string]$_
        if ($value -match '[\s"]') {
            '"' + $value.Replace('"', '\"') + '"'
        } else {
            $value
        }
    }
    $process = Start-Process -FilePath $tool -ArgumentList $quotedArguments `
        -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru
    $running.Add([pscustomobject]@{
        Name = $shardName
        Process = $process
        OutputDir = $outputDir
        ExpectedTests = [int]$shard.testCount
    }) | Out-Null
    Write-Host "[wpt-shards] started $shardName pid=$($process.Id) tests=$($shard.testCount) estimate=$([math]::Round($shard.estimatedSeconds, 1))s"
}

$failed = 0
foreach ($item in $running) {
    $item.Process.WaitForExit()
    $summaryPath = Join-Path $item.OutputDir "wpt.summary.json"
    $summary = if (Test-Path -LiteralPath $summaryPath) {
        Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    } else {
        $null
    }
    $complete = $null -ne $summary -and (-not $summary.TimedOut) -and
        (-not $summary.Stalled) -and
        ([int]$summary.TestEnd -eq [int]$item.ExpectedTests)

    if (-not $complete) {
        $failed++
        Write-Host "[wpt-shards] $($item.Name) infrastructure failure (exit $($item.Process.ExitCode))" -ForegroundColor Red
    } elseif ([int]$summary.ExitCode -ne 0) {
        $conformanceFailures++
        Write-Host "[wpt-shards] $($item.Name) complete with conformance failures"
    } else {
        Write-Host "[wpt-shards] $($item.Name) complete"
    }
    $item.Process.Dispose()
}

if ($failed -gt 0) {
    throw "$failed WPT shard(s) failed. Re-run the same command to resume only incomplete shards."
}

Write-Host "[wpt-shards] all shards complete: $suiteRoot"
if ($conformanceFailures -gt 0) {
    throw "$conformanceFailures completed WPT shard(s) contain conformance failures."
}
