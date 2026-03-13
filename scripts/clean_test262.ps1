param(
    [string]$ResultsRoot = "Results",
    [switch]$IncludeBuildOutputs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoPath([string]$PathValue) {
    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return [System.IO.Path]::GetFullPath($PathValue)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $PathValue))
}

function Stop-Test262Processes {
    $killed = New-Object System.Collections.Generic.List[int]
    $repoRoot = (Get-Location).Path
    $candidates = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @("dotnet.exe", "FenBrowser.Test262.exe") -and
        $_.CommandLine -like "*FenBrowser.Test262*" -and
        $_.CommandLine -like "*$repoRoot*"
    }

    foreach ($candidate in $candidates) {
        try {
            Stop-Process -Id $candidate.ProcessId -Force -ErrorAction Stop
            $killed.Add([int]$candidate.ProcessId) | Out-Null
        }
        catch {
        }
    }

    return @($killed | Sort-Object -Unique)
}

$resolvedResultsRoot = Resolve-RepoPath $ResultsRoot
$killedProcessIds = Stop-Test262Processes

if (Test-Path $resolvedResultsRoot) {
    Get-ChildItem $resolvedResultsRoot -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $resolvedResultsRoot | Out-Null
}

if ($IncludeBuildOutputs) {
    foreach ($buildPath in @("FenBrowser.Test262/bin", "FenBrowser.Test262/obj")) {
        $resolvedBuildPath = Resolve-RepoPath $buildPath
        if (Test-Path $resolvedBuildPath) {
            Remove-Item $resolvedBuildPath -Recurse -Force
        }
    }
}

$vendorDebugFiles = Get-ChildItem (Join-Path (Get-Location).Path "test262/test") -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like "tmp-debug-*" -or
        $_.Name -like "debug_*" -or
        $_.Name -like "custom-test*" -or
        $_.FullName -like "*\test\local-host\*"
    }

$removedVendorDebugFiles = 0
foreach ($file in $vendorDebugFiles) {
    Remove-Item $file.FullName -Force
    $removedVendorDebugFiles++
}

[pscustomobject]@{
    ResultsRoot = $resolvedResultsRoot
    KilledProcesses = @($killedProcessIds).Count
    RemovedVendorDebugFiles = $removedVendorDebugFiles
    BuildOutputsRemoved = [bool]$IncludeBuildOutputs
} | ConvertTo-Json
