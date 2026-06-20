param(
    # Per-test cooperative budget passed to the runner (--timeout-ms). A test that
    # exceeds this self-aborts and the runner moves on.
    [int]$TimeoutMs = 15000,
    # Hard stall watchdog: if a category process produces NO new output for this many
    # seconds (a test that ignores the cooperative timeout and wedges the process),
    # kill it and move to the next category. Must exceed TimeoutMs so a single
    # legitimately-slow test that self-aborts does not trip it.
    [int]$StallTimeoutSec = 35,
    [string]$Test262Root = "C:\Users\udayk\Videos\test262",
    [int]$MaxPerCategory = 100000,
    # A directory holding more than this many .js tests is split into its subdirs so
    # one wedging test only kills its own small bucket (not a 10k-test category).
    [int]$Threshold = 2000,
    [switch]$Fresh
)

$env:TEST262_PROGRESS = "1"
$ErrorActionPreference = "Stop"

$sourceRoot = (Resolve-Path $Test262Root).Path
$testRoot = Join-Path $sourceRoot "test"
$outDir = "Results/test262/categories"
$failReport = Join-Path $outDir "_failures.txt"
$summaryCsv = Join-Path $outDir "_summary.csv"

New-Item -ItemType Directory -Force $outDir | Out-Null

if ($Fresh) {
    Write-Host "Fresh run: clearing $outDir/*.json"
    Remove-Item (Join-Path $outDir "*.json") -Force -ErrorAction SilentlyContinue
}

# Build the runner once (rebuilds FenBrowser.Js too) so every category runs the
# current engine -- avoids the stale-binary trap from editing the lib but running
# a consumer's copied dll.
Write-Host "Building Test262 runner (Release)..."
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release -v quiet --nologo | Out-Null
$dll = "FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.dll"
if (-not (Test-Path $dll)) { throw "Runner dll not found: $dll" }

# Build the category list by adaptive recursion: a directory with <= $Threshold
# tests becomes one category (scoped by its path); a bigger directory is split into
# its subdirs, and any loose .js sitting directly in a split directory becomes a
# single-file category (so nothing is missed and no test is double-counted).
# harness/ is includes, not tests -- skipped.
$script:Threshold = $Threshold

function Expand-Categories {
    param([string]$absDir, [string]$rel)
    $jsCount = @(Get-ChildItem $absDir -Recurse -File -Filter *.js -ErrorAction SilentlyContinue).Count
    if ($jsCount -eq 0) { return @() }
    if ($jsCount -le $script:Threshold) { return @($rel) }
    $subs = @(Get-ChildItem $absDir -Directory | Sort-Object Name)
    if ($subs.Count -eq 0) { return @($rel) }   # leaf bigger than threshold; cannot split further
    $out = New-Object System.Collections.Generic.List[string]
    # Loose tests directly under a split dir become ONE shallow category (files in
    # this dir only, not subdirs) -- avoids an explosion of single-file categories.
    if (@(Get-ChildItem $absDir -File -Filter *.js).Count -gt 0) {
        $out.Add("SHALLOW::$rel")
    }
    foreach ($s in $subs) {
        foreach ($c in (Expand-Categories $s.FullName "$rel/$($s.Name)")) { $out.Add($c) }
    }
    return $out
}

$categories = New-Object System.Collections.Generic.List[string]
foreach ($top in (Get-ChildItem $testRoot -Directory | Sort-Object Name)) {
    if ($top.Name -eq 'harness') { continue }
    foreach ($c in (Expand-Categories $top.FullName $top.Name)) { $categories.Add($c) }
}

Write-Host "Categories: $($categories.Count) (threshold=$Threshold tests/category)"
Write-Host ("=" * 78)

function Get-SafeName([string]$cat) { return ($cat -replace '[\\/]', '__') }

# A "SHALLOW::<dir>" entry means: run only the loose .js sitting directly in <dir>
# (via --test262-shallow), not its subdirs. Resolve an entry to scope/safe/label.
function Resolve-Cat([string]$cat) {
    if ($cat.StartsWith("SHALLOW::")) {
        $scope = $cat.Substring(9)
        return @{ Scope = $scope; Safe = ((Get-SafeName $scope) + "__loose"); Label = "$scope/*(loose)"; Shallow = $true }
    }
    return @{ Scope = $cat; Safe = (Get-SafeName $cat); Label = $cat; Shallow = $false }
}

$i = 0
foreach ($cat in $categories) {
    $i++
    $rc = Resolve-Cat $cat
    $shallow = $rc.Shallow
    $scope = $rc.Scope
    $label = $rc.Label
    $safe = $rc.Safe
    $outFile = Join-Path $outDir "$safe.json"

    if (Test-Path $outFile) {
        try {
            $j = Get-Content $outFile -Raw | ConvertFrom-Json
            if ($null -ne $j.total) {
                Write-Host ("[{0,3}/{1}] SKIP {2,-34} total={3} pass={4} fail={5} crash={6} timeout={7}" -f `
                    $i, $categories.Count, $label, $j.total, $j.passed, $j.failed, $j.crashed, $j.timedOut)
                continue
            }
        }
        catch { Write-Host "  (invalid json for $cat, rerunning)" }
    }

    $t0 = Get-Date
    Write-Host -NoNewline ("[{0,3}/{1}] RUN  {2,-34} ... " -f $i, $categories.Count, $label)

    # Run the category as a tracked process so a wedged test can't hang the sweep.
    # The runner streams a [progress] line every few percent; if the log file stops
    # growing for $StallTimeoutSec the process is stuck on a single test -> kill it
    # and move on (the test that wedged it is recorded in the stub note).
    $logOut = Join-Path $outDir "$safe.log"
    $logErr = Join-Path $outDir "$safe.err"
    $procArgs = @($dll, "--runtime-subset", "--root", $sourceRoot, "--test262", $scope,
        "--max", $MaxPerCategory, "--timeout-ms", $TimeoutMs, "--out", $outFile)
    if ($shallow) { $procArgs += "--test262-shallow" }
    $proc = Start-Process -FilePath "dotnet" -ArgumentList $procArgs -PassThru -NoNewWindow `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr

    $stalled = $false
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 2
        $last = if (Test-Path $logOut) { (Get-Item $logOut).LastWriteTime } else { $t0 }
        $idle = ((Get-Date) - $last).TotalSeconds
        if ($idle -gt $StallTimeoutSec) {
            $stalled = $true
            try { taskkill /PID $proc.Id /F /T 2>&1 | Out-Null } catch {}
            break
        }
    }

    $secs = [int]((Get-Date) - $t0).TotalSeconds

    if ($stalled -or -not (Test-Path $outFile)) {
        # Pull the last test the runner started from the progress log so we know which
        # test wedged the process.
        $lastLine = if (Test-Path $logOut) { (Get-Content $logOut -Tail 1) } else { "" }
        $reason = if ($stalled) { "stalled >${StallTimeoutSec}s on a single test (process killed)" } else { "process crashed (no output json)" }
        Write-Host ("{0} ({1}s) last: {2}" -f ($(if ($stalled) { 'STALL-KILL' } else { 'CRASH' }), $secs, $lastLine))
        [pscustomobject]@{
            total = 0; passed = 0; failed = 0; crashed = $(if ($stalled) { 0 } else { 1 }); timedOut = 0
            stalled = $stalled; note = $reason; lastProgress = "$lastLine"
        } | ConvertTo-Json | Set-Content $outFile -Encoding utf8
        continue
    }

    $j = Get-Content $outFile -Raw | ConvertFrom-Json
    Write-Host ("total={0} pass={1} fail={2} crash={3} timeout={4} ({5}s)" -f `
        $j.total, $j.passed, $j.failed, $j.crashed, $j.timedOut, $secs)
}

Write-Host ("=" * 78)
Write-Host "Per-category summary (sorted by failures):"

$rows = @()
foreach ($cat in $categories) {
    $rc = Resolve-Cat $cat
    $f = Join-Path $outDir "$($rc.Safe).json"
    if (-not (Test-Path $f)) { continue }
    $j = Get-Content $f -Raw | ConvertFrom-Json
    $rows += [pscustomobject]@{
        Category = $rc.Label
        Total    = [int]$j.total
        Passed   = [int]$j.passed
        Failed   = [int]$j.failed
        Crashed  = [int]$j.crashed
        TimedOut = [int]$j.timedOut
        PassPct  = if ([int]$j.total -gt 0) { [math]::Round(100.0 * [int]$j.passed / [int]$j.total, 1) } else { 0 }
        Note     = if ($j.stalled) { "STALL-KILLED" } elseif ([int]$j.total -eq 0) { "no-result" } else { "" }
    }
}

$rows | Sort-Object Failed -Descending | Format-Table -AutoSize
$rows | Sort-Object Category | Export-Csv -NoTypeInformation -Path $summaryCsv -Encoding utf8

Write-Host ""
Write-Host "Aggregate:"
[pscustomobject]@{
    Categories = $rows.Count
    Total      = ($rows | Measure-Object Total -Sum).Sum
    Passed     = ($rows | Measure-Object Passed -Sum).Sum
    Failed     = ($rows | Measure-Object Failed -Sum).Sum
    Crashed    = ($rows | Measure-Object Crashed -Sum).Sum
    TimedOut   = ($rows | Measure-Object TimedOut -Sum).Sum
    PassPct    = if (($rows | Measure-Object Total -Sum).Sum -gt 0) {
        [math]::Round(100.0 * ($rows | Measure-Object Passed -Sum).Sum / ($rows | Measure-Object Total -Sum).Sum, 2)
    } else { 0 }
} | Format-List

# Final output: the exact list of failing tests, grouped by category, with the
# failure classification + message, so the user can see precisely what failed.
Write-Host "Writing failing-test list -> $failReport"
$sw = New-Object System.IO.StreamWriter($failReport, $false, [System.Text.Encoding]::UTF8)
$totalFail = 0
foreach ($cat in ($categories | Sort-Object)) {
    $rc = Resolve-Cat $cat
    $f = Join-Path $outDir "$($rc.Safe).json"
    if (-not (Test-Path $f)) { continue }
    $j = Get-Content $f -Raw | ConvertFrom-Json
    if ($null -eq $j.failures -or $j.failures.Count -eq 0) { continue }
    $sw.WriteLine("### $($rc.Label)  ($($j.failures.Count) failing)")
    foreach ($fail in $j.failures) {
        $totalFail++
        $msg = ($fail.message -replace '\s+', ' ')
        if ($msg.Length -gt 120) { $msg = $msg.Substring(0, 120) }
        $sw.WriteLine("  [$($fail.classification)] $($fail.relativePath) :: $msg")
    }
    $sw.WriteLine("")
}
$sw.Close()
Write-Host "Total failing tests listed: $totalFail"
Write-Host "Reports: $failReport  |  $summaryCsv"
