param(
    [string[]]$Slices = @("stage0-governance", "stage1-pipeline", "stage2-html", "stage3-css-js"),
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [bool]$RequireLiveArtifactEvidence = $true,
    [string]$LogsDir = ""
)

$ErrorActionPreference = "Stop"
$runStartedUtc = (Get-Date).ToUniversalTime()

$normalizedSlices = New-Object System.Collections.Generic.List[string]
foreach ($slice in $Slices)
{
    if ([string]::IsNullOrWhiteSpace($slice))
    {
        continue
    }

    foreach ($part in ($slice -split "[,;]"))
    {
        $trimmed = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($trimmed))
        {
            $normalizedSlices.Add($trimmed)
        }
    }
}

if ($normalizedSlices.Count -eq 0)
{
    $normalizedSlices.AddRange(@("stage0-governance", "stage1-pipeline", "stage2-html", "stage3-css-js"))
}

$Slices = $normalizedSlices

$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDir = Join-Path $repoRoot "Results"
New-Item -Path $resultsDir -ItemType Directory -Force | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$bundlePath = Join-Path $resultsDir "stage_recovery_baseline_${timestamp}.json"
$latestBundlePath = Join-Path $resultsDir "stage_recovery_baseline_latest.json"

$toolingProject = Join-Path $repoRoot "FenBrowser.Tooling\FenBrowser.Tooling.csproj"
$testsProject = Join-Path $repoRoot "FenBrowser.Tests\FenBrowser.Tests.csproj"
$effectiveLogsDir = if ([string]::IsNullOrWhiteSpace($LogsDir)) { Join-Path $repoRoot "logs" } else { [System.IO.Path]::GetFullPath($LogsDir) }

$steps = New-Object System.Collections.Generic.List[hashtable]

$ledgerArgs = @("run")
if ($SkipBuild) { $ledgerArgs += "--no-build" }
$ledgerArgs += @("--project", $toolingProject, "--", "capability-ledger")
if ($RequireLiveArtifactEvidence) { $ledgerArgs += "--require-live-evidence" }
if (-not [string]::IsNullOrWhiteSpace($effectiveLogsDir)) { $ledgerArgs += @("--logs-dir", $effectiveLogsDir) }
$steps.Add(@{
    id = "capability-ledger"
    category = "stage0"
    kind = "dotnet"
    args = $ledgerArgs
})

function Add-LiveArtifactStep {
    param(
        [string]$Id,
        [string]$Category
    )

    $steps.Add(@{
        id = $Id
        category = $Category
        kind = "live-artifact-gate"
    })
}

function Invoke-LiveArtifactGate {
    param(
        [string]$Category,
        [string]$LogsDirectory,
        [bool]$RequireEvidence,
        [datetime]$RunStartUtc
    )

    $requiredArtifacts = @("debug_screenshot.png", "dom_dump.txt", "js_debug.log")
    $artifactRows = @()
    $presentRequired = 0
    $missingFailures = @()
    $recentFailures = @()

    foreach ($artifact in $requiredArtifacts)
    {
        $path = Join-Path $LogsDirectory $artifact
        $exists = Test-Path -LiteralPath $path
        $sizeBytes = 0
        $lastWriteUtc = $null
        $isRecent = $false

        if ($exists)
        {
            $item = Get-Item -LiteralPath $path
            $sizeBytes = [int64]$item.Length
            $lastWriteUtc = $item.LastWriteTimeUtc
            $isRecent = $lastWriteUtc -ge $RunStartUtc
        }

        if ($exists -and $sizeBytes -gt 0)
        {
            $presentRequired++
        }
        else
        {
            $missingFailures += $artifact
        }

        if (-not $isRecent)
        {
            $recentFailures += $artifact
        }

        $artifactRows += [ordered]@{
            name = $artifact
            required = $true
            exists = [bool]$exists
            sizeBytes = $sizeBytes
            lastWriteUtc = if ($lastWriteUtc) { $lastWriteUtc.ToString("o") } else { $null }
            updatedDuringRun = [bool]$isRecent
            path = $path
        }
    }

    $latestFenLog = Get-ChildItem -Path $LogsDirectory -Filter "fenbrowser_*.log" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    $artifactRows += [ordered]@{
        name = "fenbrowser_*.log(latest)"
        required = $false
        exists = [bool]($null -ne $latestFenLog)
        sizeBytes = if ($latestFenLog) { [int64]$latestFenLog.Length } else { 0 }
        lastWriteUtc = if ($latestFenLog) { $latestFenLog.LastWriteTimeUtc.ToString("o") } else { $null }
        updatedDuringRun = if ($latestFenLog) { [bool]($latestFenLog.LastWriteTimeUtc -ge $RunStartUtc) } else { $false }
        path = if ($latestFenLog) { $latestFenLog.FullName } else { (Join-Path $LogsDirectory "fenbrowser_*.log") }
    }

    $hasMinimumEvidence = $presentRequired -eq $requiredArtifacts.Count
    $hasRecentRequiredEvidence = ($recentFailures.Count -eq 0)

    $evidenceId = ""
    if ($hasMinimumEvidence)
    {
        $material = ($artifactRows | Where-Object { $_.required } | ForEach-Object { "$($_.name)|$($_.sizeBytes)|$($_.lastWriteUtc)" }) -join ";"
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try
        {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($material)
            $hashBytes = $sha.ComputeHash($bytes)
            $hash = ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant().Substring(0, 12)
            $latestRequiredWrite = ($artifactRows | Where-Object { $_.required -and $_.lastWriteUtc } |
                ForEach-Object { [datetime]$_.lastWriteUtc } |
                Sort-Object -Descending |
                Select-Object -First 1)
            if (-not $latestRequiredWrite)
            {
                $latestRequiredWrite = (Get-Date).ToUniversalTime()
            }
            $evidenceId = "live-$($latestRequiredWrite.ToString('yyyyMMddHHmmss'))-$hash"
        }
        finally
        {
            $sha.Dispose()
        }
    }

    $exitCode = 0
    if ($RequireEvidence -and -not $hasMinimumEvidence)
    {
        $exitCode = 1
    }

    $summary = [ordered]@{
        total = $requiredArtifacts.Count
        passed = $presentRequired
        failed = ($requiredArtifacts.Count - $presentRequired)
        skipped = 0
    }

    $gate = [ordered]@{
        logsDirectory = $LogsDirectory
        hasMinimumEvidence = [bool]$hasMinimumEvidence
        hasRecentRequiredEvidence = [bool]$hasRecentRequiredEvidence
        evidenceId = $evidenceId
        artifacts = $artifactRows
    }

    $outputLines = @(
        "Live artifact gate category=$Category",
        "logsDirectory=$LogsDirectory",
        "evidenceId=$evidenceId",
        "hasMinimumEvidence=$hasMinimumEvidence",
        "hasRecentRequiredEvidence=$hasRecentRequiredEvidence",
        "requiredArtifacts=$($requiredArtifacts.Count)",
        "presentRequired=$presentRequired"
    )
    if ($missingFailures.Count -gt 0)
    {
        $outputLines += "missingArtifacts=$($missingFailures -join ',')"
    }
    if ($recentFailures.Count -gt 0)
    {
        $outputLines += "staleArtifacts=$($recentFailures -join ',')"
    }

    return [ordered]@{
        exitCode = $exitCode
        summary = $summary
        gate = $gate
        outputText = ($outputLines -join [Environment]::NewLine)
        failures = @($missingFailures | ForEach-Object { [ordered]@{ kind = "artifact-missing"; path = $_; capabilityIds = @() } })
    }
}

function Parse-LedgerSummary {
    param(
        [string]$RepositoryRoot
    )

    $latestLedgerPath = Join-Path $RepositoryRoot "Results\capability_ledger_latest.json"
    if (-not (Test-Path -LiteralPath $latestLedgerPath))
    {
        return $null
    }

    try
    {
        $ledger = Get-Content -LiteralPath $latestLedgerPath -Raw | ConvertFrom-Json
        $summaryNode = if ($ledger.summary) { $ledger.summary } elseif ($ledger.Summary) { $ledger.Summary } else { $null }
        if ($null -eq $summaryNode)
        {
            return $null
        }

        $totalRaw = if ($summaryNode.totalCapabilities) { $summaryNode.totalCapabilities } elseif ($summaryNode.TotalCapabilities) { $summaryNode.TotalCapabilities } else { 0 }
        $violationsRaw = if ($summaryNode.implicitCompleteViolationCount) { $summaryNode.implicitCompleteViolationCount } elseif ($summaryNode.ImplicitCompleteViolationCount) { $summaryNode.ImplicitCompleteViolationCount } else { 0 }
        $total = [int]$totalRaw
        $violations = [int]$violationsRaw
        if ($violations -lt 0) { $violations = 0 }
        if ($violations -gt $total) { $violations = $total }
        return [ordered]@{
            total = $total
            passed = ($total - $violations)
            failed = $violations
            skipped = 0
        }
    }
    catch
    {
        return $null
    }
}

function Parse-FailureGroups {
    param([string]$Text)

    $failures = @()
    if ([string]::IsNullOrWhiteSpace($Text))
    {
        return $failures
    }

    $lines = $Text -split "(`r`n|`n)"
    foreach ($line in $lines)
    {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith("Failed", [System.StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }

        $capabilityMatches = [regex]::Matches($trimmed, "[A-Z0-9]+-[A-Z0-9\-]+-\d+")
        $capabilityIds = @()
        foreach ($match in $capabilityMatches)
        {
            $capabilityIds += $match.Value
        }
        $capabilityIds = $capabilityIds | Select-Object -Unique

        $failures += [ordered]@{
            kind = "test-failure"
            path = $trimmed
            capabilityIds = @($capabilityIds)
        }
    }

    return $failures
}

function Add-TestStep {
    param(
        [string]$Id,
        [string]$Category,
        [string]$Filter
    )

    $args = @(
        "test",
        $testsProject,
        "-c", $Configuration,
        "--nologo",
        "-v", "minimal",
        "--filter", $Filter
    )

    if ($SkipBuild)
    {
        $args += "--no-build"
    }

    $steps.Add(@{
        id = $Id
        category = $Category
        kind = "dotnet"
        args = $args
    })
}

if ($Slices -contains "stage0-governance")
{
    Add-TestStep -Id "spec-governance" -Category "stage0" -Filter "(FullyQualifiedName~SpecGovernanceTests)"
}

if ($Slices -contains "stage1-pipeline")
{
    Add-TestStep -Id "pipeline-invariants" -Category "stage1" -Filter "(FullyQualifiedName~PipelineContextTests|FullyQualifiedName~PipelineInvariantGateTests|FullyQualifiedName~EventLoopTests|FullyQualifiedName~EventLoopPriorityTests|FullyQualifiedName~ExecutionContextSchedulingTests)"
    Add-LiveArtifactStep -Id "stage1-live-artifacts" -Category "stage1"
}

if ($Slices -contains "stage2-html")
{
    Add-TestStep -Id "html-parser-tranche" -Category "stage2" -Filter "(FullyQualifiedName~Html5libTokenizerTests|FullyQualifiedName~Html5libTreeBuilderTests|FullyQualifiedName~CanonicalHtmlParserEntrypointTests|FullyQualifiedName~StreamingHtmlParserTests|FullyQualifiedName~ParserHardeningGuardTests)"
    Add-LiveArtifactStep -Id "stage2-live-artifacts" -Category "stage2"
}

if ($Slices -contains "stage3-css-js")
{
    Add-TestStep -Id "css-js-tranche" -Category "stage3" -Filter "(FullyQualifiedName~SelectorMatcherConformanceTests|FullyQualifiedName~SelectorEngineConformanceTests|FullyQualifiedName~CascadeModernTests|FullyQualifiedName~JsParserLoopReproTests|FullyQualifiedName~ParserFuzzRegressionTests)"
    Add-LiveArtifactStep -Id "stage3-live-artifacts" -Category "stage3"
}

function Parse-TestSummary {
    param([string]$Text)

    $matches = [regex]::Matches($Text, "Total tests:\s*(?<total>\d+)\.?\s*Passed:\s*(?<passed>\d+)\.?\s*Failed:\s*(?<failed>\d+)\.?\s*Skipped:\s*(?<skipped>\d+)\.?")
    if ($matches.Count -eq 0)
    {
        $matches = [regex]::Matches($Text, "Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)")
        if ($matches.Count -eq 0)
        {
            return $null
        }
    }

    $last = $matches[$matches.Count - 1]
    return [ordered]@{
        total = [int]$last.Groups["total"].Value
        passed = [int]$last.Groups["passed"].Value
        failed = [int]$last.Groups["failed"].Value
        skipped = [int]$last.Groups["skipped"].Value
    }
}

$results = @()
$overallExitCode = 0

foreach ($step in $steps)
{
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $outputText = ""
    $summary = $null
    $exitCode = 0
    $commandText = ""
    $failures = @()
    $liveGate = $null

    if ($step.kind -eq "dotnet")
    {
        $commandText = "dotnet " + ($step.args -join " ")
        $previousErrorPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $output = & dotnet @($step.args) 2>&1
        $ErrorActionPreference = $previousErrorPreference
        $exitCode = $LASTEXITCODE
        $outputText = ($output | Out-String)
        $summary = Parse-TestSummary -Text $outputText
        if ($step.id -eq "capability-ledger" -and $null -eq $summary)
        {
            $summary = Parse-LedgerSummary -RepositoryRoot $repoRoot
        }
        $failures = Parse-FailureGroups -Text $outputText
    }
    elseif ($step.kind -eq "live-artifact-gate")
    {
        $commandText = "live-artifact-gate --logs-dir `"$effectiveLogsDir`" --require $RequireLiveArtifactEvidence"
        $gateResult = Invoke-LiveArtifactGate -Category $step.category -LogsDirectory $effectiveLogsDir -RequireEvidence $RequireLiveArtifactEvidence -RunStartUtc $runStartedUtc
        $exitCode = [int]$gateResult.exitCode
        $summary = $gateResult.summary
        $liveGate = $gateResult.gate
        $outputText = $gateResult.outputText
        $failures = $gateResult.failures
    }
    else
    {
        throw "Unknown step kind: $($step.kind)"
    }

    $sw.Stop()

    $logPath = Join-Path $resultsDir ("stage_recovery_{0}_{1}.log" -f $timestamp, $step.id)
    $outputText | Set-Content -Path $logPath -Encoding UTF8

    $results += [ordered]@{
        id = $step.id
        category = $step.category
        kind = $step.kind
        command = $commandText
        exitCode = $exitCode
        durationSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 3)
        summary = $summary
        liveArtifactGate = $liveGate
        failures = @($failures)
        logPath = $logPath
    }

    if ($exitCode -ne 0)
    {
        $overallExitCode = 1
    }
}

$orderedCategories = @("stage0", "stage1", "stage2", "stage3")
$sliceTotals = @()
foreach ($category in $orderedCategories)
{
    $categoryResults = @($results | Where-Object { $_.category -eq $category })
    if ($categoryResults.Count -eq 0)
    {
        continue
    }

    $total = 0
    $passed = 0
    $failed = 0
    $skipped = 0
    foreach ($entry in $categoryResults)
    {
        if ($entry.summary)
        {
            $total += [int]$entry.summary.total
            $passed += [int]$entry.summary.passed
            $failed += [int]$entry.summary.failed
            $skipped += [int]$entry.summary.skipped
        }
        elseif ($entry.exitCode -ne 0)
        {
            $failed++
            $total++
        }
    }

    $sliceTotals += [ordered]@{
        category = $category
        total = $total
        passed = $passed
        failed = $failed
        skipped = $skipped
    }
}

$failureGroups = @()
foreach ($entry in $results)
{
    if (-not $entry.failures -or $entry.failures.Count -eq 0)
    {
        continue
    }

    foreach ($failure in $entry.failures)
    {
        $failureGroups += [ordered]@{
            category = $entry.category
            stepId = $entry.id
            kind = $failure.kind
            path = $failure.path
            capabilityIds = $failure.capabilityIds
        }
    }
}

$bundle = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    runStartedUtc = $runStartedUtc.ToString("o")
    slices = $Slices
    configuration = $Configuration
    skipBuild = [bool]$SkipBuild
    requireLiveArtifactEvidence = [bool]$RequireLiveArtifactEvidence
    logsDirectory = $effectiveLogsDir
    overallExitCode = $overallExitCode
    totalsBySlice = $sliceTotals
    failureGroups = $failureGroups
    results = $results
}

$bundle | ConvertTo-Json -Depth 8 | Set-Content -Path $bundlePath -Encoding UTF8
$bundle | ConvertTo-Json -Depth 8 | Set-Content -Path $latestBundlePath -Encoding UTF8

Write-Host "Stage recovery baseline bundle: $bundlePath"
Write-Host "Latest bundle alias: $latestBundlePath"

if ($overallExitCode -ne 0)
{
    exit 1
}
