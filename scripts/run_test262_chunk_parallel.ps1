param(
    [int]$ChunkNumber = 1,
    [int]$ChunkSize = 1000,
    [int]$WorkerCount = 20,
    [int]$MicroChunkSize = 0,
    [string]$Project = "FenBrowser.Test262/FenBrowser.Test262.csproj",
    [string]$Test262Root = "test262",
    [int]$TimeoutMs = 10000,
    [int]$MaxMemoryMB = 10000,
    [switch]$SkipBuild,
    [switch]$IsolateProcess,
    [string]$ResultsRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoPath([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ""
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $PathValue))
}

if ($ChunkNumber -lt 1) {
    throw "ChunkNumber must be >= 1."
}

if ($ChunkSize -lt 1) {
    throw "ChunkSize must be >= 1."
}

if ($WorkerCount -lt 1) {
    throw "WorkerCount must be >= 1."
}

if ($MicroChunkSize -lt 0) {
    throw "MicroChunkSize must be >= 0."
}

if ($MicroChunkSize -eq 0) {
    if ($ChunkSize % $WorkerCount -ne 0) {
        throw "ChunkSize must divide evenly by WorkerCount when MicroChunkSize is omitted."
    }

    $MicroChunkSize = [int]($ChunkSize / $WorkerCount)
}

if ($ChunkSize % $MicroChunkSize -ne 0) {
    throw "ChunkSize must divide evenly by MicroChunkSize."
}

$expectedWorkers = [int]($ChunkSize / $MicroChunkSize)
if ($expectedWorkers -ne $WorkerCount) {
    throw "WorkerCount ($WorkerCount) must match ChunkSize / MicroChunkSize ($expectedWorkers)."
}

$sliceStartOffset = ($ChunkNumber - 1) * $ChunkSize
if ($sliceStartOffset % $MicroChunkSize -ne 0) {
    throw "Chunk boundary must align with MicroChunkSize."
}

$startMicroChunk = [int]($sliceStartOffset / $MicroChunkSize) + 1
$endMicroChunk = $startMicroChunk + $WorkerCount - 1

$resolvedProject = Resolve-RepoPath $Project
$resolvedTest262Root = Resolve-RepoPath $Test262Root
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $ResultsRoot = "Results/chunk${ChunkNumber}_${ChunkSize}tests_${WorkerCount}workers_${timestamp}"
}

$resolvedResultsRoot = Resolve-RepoPath $ResultsRoot
$logRoot = Join-Path $resolvedResultsRoot "logs"
New-Item -ItemType Directory -Force -Path $resolvedResultsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if (-not $SkipBuild) {
    & dotnet build $resolvedProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

$procs = New-Object System.Collections.Generic.List[object]
for ($microChunk = $startMicroChunk; $microChunk -le $endMicroChunk; $microChunk++) {
    $relativeIndex = $microChunk - $startMicroChunk + 1
    $jsonPath = Join-Path $resolvedResultsRoot ("chunk_{0:D2}.json" -f $relativeIndex)
    $outLog = Join-Path $logRoot ("worker_{0:D2}.out.log" -f $relativeIndex)
    $errLog = Join-Path $logRoot ("worker_{0:D2}.err.log" -f $relativeIndex)

    $args = @(
        "run", "--project", $resolvedProject, "-c", "Release", "--no-build", "--",
        "run_chunk", "$microChunk",
        "--root", $resolvedTest262Root,
        "--chunk-size", "$MicroChunkSize",
        "--timeout", "$TimeoutMs",
        "--max-memory-mb", "$MaxMemoryMB",
        "--format", "json",
        "--output", $jsonPath
    )

    if ($IsolateProcess) {
        $args += "--isolate-process"
    }

    $proc = Start-Process -FilePath "dotnet" -ArgumentList $args -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru -WindowStyle Hidden
    $procs.Add([pscustomobject]@{
        Process = $proc
        RelativeIndex = $relativeIndex
        GlobalMicroChunk = $microChunk
    }) | Out-Null
}

$procs.Process | Wait-Process

$completed = New-Object System.Collections.Generic.List[object]
$missing = New-Object System.Collections.Generic.List[object]

foreach ($procInfo in $procs) {
    $jsonPath = Join-Path $resolvedResultsRoot ("chunk_{0:D2}.json" -f $procInfo.RelativeIndex)
    if (Test-Path $jsonPath) {
        $payload = Get-Content $jsonPath -Raw | ConvertFrom-Json
        $completed.Add([pscustomobject]@{
            RelativeIndex = $procInfo.RelativeIndex
            GlobalMicroChunk = $procInfo.GlobalMicroChunk
            Total = [int]$payload.total
            Passed = [int]$payload.passed
            Failed = [int]$payload.failed
        }) | Out-Null
    }
    else {
        $missing.Add([pscustomobject]@{
            RelativeIndex = $procInfo.RelativeIndex
            GlobalMicroChunk = $procInfo.GlobalMicroChunk
        }) | Out-Null
    }
}

$summary = [pscustomobject]@{
    chunkNumber = $ChunkNumber
    chunkSize = $ChunkSize
    workerCount = $WorkerCount
    microChunkSize = $MicroChunkSize
    startMicroChunk = $startMicroChunk
    endMicroChunk = $endMicroChunk
    resultsRoot = $resolvedResultsRoot
    microchunksCompleted = $completed.Count
    testsCompleted = @($completed | Measure-Object -Property Total -Sum).Sum
    passed = @($completed | Measure-Object -Property Passed -Sum).Sum
    failed = @($completed | Measure-Object -Property Failed -Sum).Sum
    missingMicrochunks = @($missing | ForEach-Object { $_.RelativeIndex })
    missingGlobalMicrochunks = @($missing | ForEach-Object { $_.GlobalMicroChunk })
}

$summary | ConvertTo-Json | Set-Content (Join-Path $resolvedResultsRoot "summary.json")

$summaryLines = @(
    "# Test262 Parallel Chunk Summary",
    "",
    "- Logical chunk: $($summary.chunkNumber)",
    "- Logical chunk size: $($summary.chunkSize)",
    "- Worker count: $($summary.workerCount)",
    "- Microchunk size: $($summary.microChunkSize)",
    "- Global microchunks: $($summary.startMicroChunk)-$($summary.endMicroChunk)",
    "- Completed microchunks: $($summary.microchunksCompleted)",
    "- Tests completed: $($summary.testsCompleted)",
    "- Passed: $($summary.passed)",
    "- Failed: $($summary.failed)",
    "- Missing worker slots: $(([string]::Join(',', $summary.missingMicrochunks)))",
    "- Missing global microchunks: $(([string]::Join(',', $summary.missingGlobalMicrochunks)))"
)
$summaryLines -join [Environment]::NewLine | Set-Content (Join-Path $resolvedResultsRoot "summary.md")

$summary | ConvertTo-Json -Compress

if ($summary.missingMicrochunks.Count -gt 0 -or $summary.failed -gt 0) {
    exit 1
}

exit 0
