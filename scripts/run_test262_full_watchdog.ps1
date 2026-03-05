param(
    [string]$Project = "FenBrowser.Test262/FenBrowser.Test262.csproj",
    [string]$Test262Root = "test262",
    [int]$ChunkSize = 1000,
    [double]$MaxProcessMemoryGB = 10.0,
    [int]$ChunkTimeoutMinutes = 45,
    [switch]$SkipBuild,
    [int]$StartChunk = 1,
    [int]$EndChunk = 0,
    [string]$ResultsRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $resultsRoot = Join-Path $repoRoot "Results/test262_full_run"
} else {
    if ([System.IO.Path]::IsPathRooted($ResultsRoot)) {
        $resultsRoot = $ResultsRoot
    } else {
        $resultsRoot = Join-Path $repoRoot $ResultsRoot
    }
}
$chunkDir = Join-Path $resultsRoot "chunks"
$logDir = Join-Path $resultsRoot "logs"
$analysisDir = Join-Path $resultsRoot "analysis"
New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $chunkDir -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
New-Item -ItemType Directory -Path $analysisDir -Force | Out-Null

function Normalize-ErrorPattern([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return "(empty)" }
    $s = $text.Trim()
    $s = [Regex]::Replace($s, "\d+", "#")
    $s = [Regex]::Replace($s, "\s+", " ")
    if ($s.Length -gt 220) { $s = $s.Substring(0, 220) }
    return $s
}

function Write-FailedChunkAnalysis {
    param(
        [int]$Chunk,
        [string]$ChunkJsonPath,
        [string]$OutputPath
    )

    $data = Get-Content $ChunkJsonPath -Raw | ConvertFrom-Json
    $failed = @($data.results | Where-Object { -not $_.passed })

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# Chunk $Chunk Failure Analysis")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- Total: $($data.total)")
    [void]$sb.AppendLine("- Passed: $($data.passed)")
    [void]$sb.AppendLine("- Failed: $($data.failed)")
    [void]$sb.AppendLine("- Pass Rate: $([math]::Round([double]$data.passRate,2))%")
    [void]$sb.AppendLine("- Duration: $($data.durationMs) ms")
    [void]$sb.AppendLine("")

    if ($failed.Count -eq 0) {
        [void]$sb.AppendLine("No failed tests in this chunk.")
        Set-Content -Path $OutputPath -Value $sb.ToString() -Encoding UTF8
        return
    }

    $byPattern = $failed | Group-Object { Normalize-ErrorPattern($_.error) } | Sort-Object Count -Descending
    [void]$sb.AppendLine("## Top Error Patterns")
    [void]$sb.AppendLine("| Count | Pattern |")
    [void]$sb.AppendLine("|---:|---|")
    foreach ($g in ($byPattern | Select-Object -First 20)) {
        $p = ($g.Name -replace "\|", "\\|")
        [void]$sb.AppendLine("| $($g.Count) | $p |")
    }
    [void]$sb.AppendLine("")

    $featureCounts = @{}
    foreach ($f in $failed) {
        if ($null -eq $f.features) { continue }
        foreach ($feat in $f.features) {
            if ([string]::IsNullOrWhiteSpace($feat)) { continue }
            if (-not $featureCounts.ContainsKey($feat)) { $featureCounts[$feat] = 0 }
            $featureCounts[$feat]++
        }
    }

    [void]$sb.AppendLine("## Top Features In Failed Tests")
    if ($featureCounts.Count -eq 0) {
        [void]$sb.AppendLine("No feature tags present.")
    } else {
        [void]$sb.AppendLine("| Count | Feature |")
        [void]$sb.AppendLine("|---:|---|")
        foreach ($k in ($featureCounts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 20)) {
            [void]$sb.AppendLine("| $($k.Value) | $($k.Key) |")
        }
    }
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Detailed Failed Tests (Top 80)")
    [void]$sb.AppendLine("| Test | Error | Expected | Actual | Features | Duration (ms) |")
    [void]$sb.AppendLine("|---|---|---|---|---|---:|")
    foreach ($f in ($failed | Select-Object -First 80)) {
        $err = ([string]$f.error)
        if ([string]::IsNullOrWhiteSpace($err)) { $err = "(none)" }
        $exp = ([string]$f.expected)
        $act = ([string]$f.actual)
        $fts = if ($null -eq $f.features) { "" } else { ($f.features -join ",") }

        foreach ($ref in @("err","exp","act","fts")) {
            Set-Variable -Name $ref -Value ((Get-Variable $ref -ValueOnly) -replace "\|", "\\|" -replace "\r?\n", " ")
        }

        if ($err.Length -gt 180) { $err = $err.Substring(0, 180) + "..." }
        if ($act.Length -gt 140) { $act = $act.Substring(0, 140) + "..." }
        if ($exp.Length -gt 80) { $exp = $exp.Substring(0, 80) + "..." }

        [void]$sb.AppendLine("| $($f.file) | $err | $exp | $act | $fts | $($f.durationMs) |")
    }

    Set-Content -Path $OutputPath -Value $sb.ToString() -Encoding UTF8
}

if (-not $SkipBuild) {
    Write-Host "[build] Building Test262 runner (Release)..."
    dotnet build $Project -c Release | Out-Host
}

Write-Host "[discover] Getting total chunk count..."
$chunkCountOutput = dotnet run --project $Project -c Release --no-build -- get_chunk_count --root $Test262Root
$chunkCountLine = ($chunkCountOutput | Where-Object { $_ -match '^Chunks:\s*(\d+)$' } | Select-Object -First 1)
if (-not $chunkCountLine) {
    throw "Unable to parse chunk count from output.`n$($chunkCountOutput -join "`n")"
}
$chunkCount = [int]([regex]::Match($chunkCountLine, 'Chunks:\s*(\d+)').Groups[1].Value)
Write-Host "[discover] Total chunks: $chunkCount"
if ($StartChunk -lt 1) { throw "StartChunk must be >= 1" }
if ($EndChunk -le 0 -or $EndChunk -gt $chunkCount) { $EndChunk = $chunkCount }
if ($StartChunk -gt $EndChunk) { throw "StartChunk ($StartChunk) cannot be greater than EndChunk ($EndChunk)" }
Write-Host "[discover] Running chunk range: $StartChunk-$EndChunk"

$summary = New-Object System.Collections.Generic.List[object]

for ($chunk = $StartChunk; $chunk -le $EndChunk; $chunk++) {
    $start = ($chunk - 1) * $ChunkSize + 1
    $end = $chunk * $ChunkSize

    $chunkJson = Join-Path $chunkDir ("chunk_{0:D3}.json" -f $chunk)
    $chunkOut = Join-Path $logDir ("chunk_{0:D3}.out.log" -f $chunk)
    $chunkErr = Join-Path $logDir ("chunk_{0:D3}.err.log" -f $chunk)
    $chunkAnalysis = Join-Path $analysisDir ("chunk_{0:D3}_failed.md" -f $chunk)

    Write-Host "`n[chunk $chunk/$chunkCount] Running tests $start-$end"

    $args = @(
        "run", "--project", $Project, "-c", "Release", "--no-build", "--",
        "run_chunk", "$chunk", "--root", $Test262Root,
        "--chunk-size", "$ChunkSize", "--format", "json", "--output", $chunkJson
    )

    $proc = Start-Process -FilePath "dotnet" -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardOutput $chunkOut -RedirectStandardError $chunkErr

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = "completed"
    $killedReason = ""
    $peakWS = 0.0
    $peakPM = 0.0

    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        try {
            $p = Get-Process -Id $proc.Id -ErrorAction Stop
            $wsGb = [math]::Round($p.WorkingSet64 / 1GB, 3)
            $pmGb = [math]::Round($p.PrivateMemorySize64 / 1GB, 3)
            if ($wsGb -gt $peakWS) { $peakWS = $wsGb }
            if ($pmGb -gt $peakPM) { $peakPM = $pmGb }

            if ($wsGb -ge $MaxProcessMemoryGB -or $pmGb -ge $MaxProcessMemoryGB) {
                $status = "killed_memory"
                $killedReason = "RAM threshold exceeded (WS=${wsGb}GB PM=${pmGb}GB >= ${MaxProcessMemoryGB}GB)"
                Write-Warning "[chunk $chunk] $killedReason"
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                break
            }

            if ($sw.Elapsed.TotalMinutes -ge $ChunkTimeoutMinutes) {
                $status = "killed_timeout"
                $killedReason = "Chunk exceeded timeout (${ChunkTimeoutMinutes}m)"
                Write-Warning "[chunk $chunk] $killedReason"
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                break
            }
        }
        catch {
            break
        }
    }

    if (-not $proc.HasExited) {
        try { $proc.WaitForExit(10000) | Out-Null } catch {}
    }

    $exitCode = if ($proc.HasExited) { $proc.ExitCode } else { -1 }

    $total = 0
    $passed = 0
    $failed = 0
    $passRate = 0.0
    $durationMs = [int64]$sw.ElapsedMilliseconds

    if (Test-Path $chunkJson) {
        try {
            $json = Get-Content $chunkJson -Raw | ConvertFrom-Json
            $total = [int]$json.total
            $passed = [int]$json.passed
            $failed = [int]$json.failed
            $passRate = [double]$json.passRate
            $durationMs = [int64]$json.durationMs

            if ($failed -gt 0) {
                Write-FailedChunkAnalysis -Chunk $chunk -ChunkJsonPath $chunkJson -OutputPath $chunkAnalysis
            }
        }
        catch {
            $status = if ($status -eq "completed") { "json_parse_error" } else { $status }
            if ([string]::IsNullOrWhiteSpace($killedReason)) {
                $killedReason = "Failed to parse chunk JSON: $($_.Exception.Message)"
            }
        }
    }
    else {
        if ($status -eq "completed") {
            $status = "no_result_file"
            $killedReason = "Chunk process ended without JSON result file"
        }
    }

    $summary.Add([pscustomobject]@{
        chunk = $chunk
        range = "$start-$end"
        status = $status
        exitCode = $exitCode
        total = $total
        passed = $passed
        failed = $failed
        passRate = [math]::Round($passRate, 2)
        durationMs = $durationMs
        peakWorkingSetGb = [math]::Round($peakWS, 3)
        peakPrivateMemoryGb = [math]::Round($peakPM, 3)
        note = $killedReason
        json = $chunkJson
        outLog = $chunkOut
        errLog = $chunkErr
        failedAnalysis = if (Test-Path $chunkAnalysis) { $chunkAnalysis } else { "" }
    }) | Out-Null

    Write-Host "[chunk $chunk] status=$status pass=$passed fail=$failed rate=$([math]::Round($passRate,2))% peakWS=${peakWS}GB peakPM=${peakPM}GB"
}

$summaryJsonPath = Join-Path $resultsRoot "full_run_summary.json"
$summaryMdPath = Join-Path $resultsRoot "full_run_summary.md"

$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryJsonPath -Encoding UTF8

$totalTests = ($summary | Measure-Object -Property total -Sum).Sum
$totalPassed = ($summary | Measure-Object -Property passed -Sum).Sum
$totalFailed = ($summary | Measure-Object -Property failed -Sum).Sum
$overallRate = if ($totalTests -gt 0) { [math]::Round(($totalPassed * 100.0) / $totalTests, 2) } else { 0 }
$killed = @($summary | Where-Object { $_.status -like "killed_*" })
$failedChunks = @($summary | Where-Object { $_.failed -gt 0 -or $_.status -ne 'completed' })

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Test262 Full Chunked Run Summary")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("- Timestamp: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")")
[void]$sb.AppendLine("- Chunk size: $ChunkSize")
[void]$sb.AppendLine("- Max process RAM threshold: ${MaxProcessMemoryGB} GB")
[void]$sb.AppendLine("- Total chunks executed: $($summary.Count)
- Selected range: $StartChunk-$EndChunk")
[void]$sb.AppendLine("- Total tests executed (reported): $totalTests")
[void]$sb.AppendLine("- Passed: $totalPassed")
[void]$sb.AppendLine("- Failed: $totalFailed")
[void]$sb.AppendLine("- Overall pass rate: $overallRate%")
[void]$sb.AppendLine("- Killed chunks: $($killed.Count)")
[void]$sb.AppendLine("")

[void]$sb.AppendLine("## Chunk Results")
[void]$sb.AppendLine("| Chunk | Range | Status | Passed | Failed | Pass % | Duration (ms) | Peak WS (GB) | Peak PM (GB) | Note |")
[void]$sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---|")
foreach ($row in $summary) {
    $note = ([string]$row.note) -replace "\|", "\\|"
    [void]$sb.AppendLine("| $($row.chunk) | $($row.range) | $($row.status) | $($row.passed) | $($row.failed) | $($row.passRate) | $($row.durationMs) | $($row.peakWorkingSetGb) | $($row.peakPrivateMemoryGb) | $note |")
}

[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Failed Chunk Deep-Dive Files")
if ($failedChunks.Count -eq 0) {
    [void]$sb.AppendLine("No failed chunks.")
} else {
    foreach ($row in $failedChunks) {
        if ([string]::IsNullOrWhiteSpace($row.failedAnalysis)) {
            [void]$sb.AppendLine("- Chunk $($row.chunk): no analysis file (status=$($row.status))")
        } else {
            [void]$sb.AppendLine("- Chunk $($row.chunk): $($row.failedAnalysis)")
        }
    }
}

Set-Content -Path $summaryMdPath -Value $sb.ToString() -Encoding UTF8

Write-Host "`n[done] Summary JSON: $summaryJsonPath"
Write-Host "[done] Summary MD:   $summaryMdPath"
Write-Host "[done] Chunk JSONs:  $chunkDir"
Write-Host "[done] Logs:         $logDir"
Write-Host "[done] Analysis:     $analysisDir"




