# Memory-safe full test262 run (PowerShell twin of run_full_batched.sh).
# One process per directory batch so RAM is released between batches, with a
# stall-kill watchdog so a test wedged in native code can't hang the whole run.
# Aggregates pass/total into Results/test262/batched/_batched_total.json.
#
# Run from repo root:  pwsh scripts/run_full_batched.ps1
param(
    [string]$Test262Root = "C:\Users\udayk\Videos\test262",
    [string]$Exe = ".\FenBrowser.Js.Test262\bin\Release\net10.0\FenBrowser.Js.Test262.exe",
    [string]$OutDir = "Results\test262\batched",
    # Mandatory per-test cooperative budget (--timeout-ms).
    [int]$TimeoutMs = 2000,
    # Hard stall watchdog: if a batch produces no new output for this many seconds
    # it is wedged on one test -> kill the process tree and move on.
    [int]$StallTimeoutSec = 30,
    [int]$MaxPerBatch = 100000
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Exe)) {
    Write-Error "Runner not found: $Exe (build it: dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release)"
    exit 1
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$prog = Join-Path $OutDir "_progress.txt"
Set-Content -Path $prog -Value "" -Encoding utf8

# Build the batch list: split the two oversized language dirs one level deeper,
# everything else runs as a second-level (or top-level) directory.
$batches = New-Object System.Collections.Generic.List[string]
foreach ($sub in @("expressions", "statements")) {
    $p = Join-Path $Test262Root "test\language\$sub"
    if (Test-Path $p) { Get-ChildItem $p -Directory | ForEach-Object { $batches.Add($_.FullName) } }
}
Get-ChildItem (Join-Path $Test262Root "test\language") -Directory |
    Where-Object { $_.Name -notin @("expressions", "statements") } |
    ForEach-Object { $batches.Add($_.FullName) }
Get-ChildItem (Join-Path $Test262Root "test\built-ins") -Directory |
    ForEach-Object { $batches.Add($_.FullName) }
foreach ($leaf in @("intl402", "annexB", "staging")) {
    $batches.Add((Join-Path $Test262Root "test\$leaf"))
}

$i = 0
foreach ($b in $batches) {
    $i++
    $tag = ($b -replace '.*[\\/]test[\\/]', '') -replace '[\\/]', '_'
    $tag = $tag.TrimEnd('_')
    $out = Join-Path $OutDir "b_$tag.json"
    $log = Join-Path $OutDir "b_$tag.log"
    $err = "$log.err"
    Set-Content -Path $log -Value "" -Encoding utf8

    $procArgs = @("--runtime-subset", "--root", $Test262Root, "--test262", $b,
        "--max", $MaxPerBatch, "--timeout-ms", $TimeoutMs, "--out", $out)
    $proc = Start-Process -FilePath $Exe -ArgumentList $procArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError $err

    $stalled = $false
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        $mtime = if (Test-Path $log) { (Get-Item $log).LastWriteTime } else { Get-Date }
        if (((Get-Date) - $mtime).TotalSeconds -gt $StallTimeoutSec) {
            $stalled = $true
            try { taskkill /PID $proc.Id /F /T 2>&1 | Out-Null } catch {}
            break
        }
    }

    $passed = 0; $total = 0
    if (Test-Path $out) {
        try {
            $j = Get-Content $out -Raw | ConvertFrom-Json
            $passed = [int]$j.passed; $total = [int]$j.total
        }
        catch {}
    }
    if ($stalled) {
        $last = if (Test-Path $log) { Get-Content $log -Tail 1 -ErrorAction SilentlyContinue } else { "" }
        "$i $tag $passed $total STALL-KILL last:[$last]" | Add-Content $prog
    }
    else {
        "$i $tag $passed $total" | Add-Content $prog
    }
}

# Aggregate.
$P = 0; $T = 0
Get-ChildItem $OutDir -Filter "b_*.json" | ForEach-Object {
    try {
        $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $P += [int]$j.passed; $T += [int]$j.total
    }
    catch {}
}
$pct = if ($T) { [math]::Round(100.0 * $P / $T, 2) } else { 0 }
@{ passed = $P; total = $T; pct = $pct } | ConvertTo-Json |
    Set-Content (Join-Path $OutDir "_batched_total.json") -Encoding utf8
"DONE $P / $T = $pct %" | Add-Content $prog
"ALL_BATCHES_COMPLETE" | Add-Content $prog
Write-Host "DONE $P / $T = $pct %"
