# Run RegExp category in chunks and aggregate into single batch result.
param(
    [string]$Test262Root = "",
    [string]$Exe = ".\FenBrowser.Js.Test262\bin\Release\net10.0\FenBrowser.Js.Test262.exe",
    [string]$OutDir = "Results\test262\batched",
    [int]$TimeoutMs = 2000
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Resolve the test262 checkout (env TEST262_ROOT, sibling dir, or local dir).
. "$PSScriptRoot\resolve-paths.ps1"
$Test262Root = Resolve-Test262Root -ExplicitRoot $Test262Root

$chunks = Get-ChildItem "$Test262Root\test\built-ins\RegExp" -Directory | Where-Object { $_.Name -ne "Results" }

$allTests = @()
$totalPass = 0; $totalAll = 0

foreach ($chunk in $chunks) {
    $tag = "RegExp_" + $chunk.Name
    $out = Join-Path $OutDir "b_built-ins_${tag}.json"
    $log = Join-Path $OutDir "b_built-ins_${tag}.log"
    $err = "$log.err"
    $N = (Get-ChildItem $chunk.FullName -Recurse -Filter "*.js" -File).Count
    Write-Host "$($chunk.Name): $N tests"

    & $Exe --runtime-subset --root $Test262Root --test262 $chunk.FullName --max 100000 --timeout-ms $TimeoutMs --out $out > $log 2> $err
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { Write-Host "  WARNING: exit code $LASTEXITCODE" }

    if (Test-Path $out) {
        try {
            $j = Get-Content $out -Raw | ConvertFrom-Json
            $chunkPass = [int]$j.passed; $chunkTotal = [int]$j.total
            Write-Host "  -> $chunkPass/$chunkTotal"
            $totalPass += $chunkPass; $totalAll += $chunkTotal
            $allTests += $j.tests
        } catch { Write-Host "  Failed to parse JSON" }
    } else {
        Write-Host "  No output file"
    }
}

# Write aggregate
$pct = if ($totalAll) { [math]::Round(100.0 * $totalPass / $totalAll, 1) } else { 0 }
$aggregate = @{
    engine = "fenbrowser"; mode = "runtime"; total = $totalAll; passed = $totalPass
    failed = $totalAll - $totalPass; tests = $allTests
    failures = @($allTests | Where-Object { $_.status -ne "pass" -and $_.status -ne "passed" })
}
$aggregate | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutDir "b_built-ins_RegExp.json") -Encoding utf8
Write-Host "RegExp aggregate: $totalPass/$totalAll = $pct%"
& python scripts/test262_report.py
