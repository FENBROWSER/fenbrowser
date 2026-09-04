# Sample a debug-site run's memory while it loads a page.
#
# The GitHub out-of-memory happens inside the CSS render path, and nothing in
# the diagnostic bundle records memory, so there was no way to tell a genuine
# unbounded allocation from a one-off spike. This runs the load and samples the
# process's private bytes and managed heap alongside it.
#
#   pwsh scripts/profile_site_memory.ps1 -Url https://github.com [-SettleMs 20000]

param(
    [string]$Url = "https://github.com",
    [int]$SettleMs = 20000,
    [int]$IntervalMs = 500,
    [string]$OutCsv = "Results/memory/site-memory.csv"
)

$exe = "FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"
if (-not (Test-Path $exe)) { throw "build FenBrowser.Tooling first: $exe not found" }

$dir = Split-Path -Parent $OutCsv
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

Write-Output "url=$Url settle=${SettleMs}ms interval=${IntervalMs}ms"

$proc = Start-Process -FilePath $exe `
    -ArgumentList @("debug-site", $Url, "$SettleMs") `
    -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\fen-mem-stdout.txt" `
    -RedirectStandardError "$env:TEMP\fen-mem-stderr.txt"

$samples = New-Object System.Collections.Generic.List[object]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$peakPrivate = 0L
$peakWorking = 0L

while (-not $proc.HasExited) {
    Start-Sleep -Milliseconds $IntervalMs
    try {
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $private = $proc.PrivateMemorySize64
        $working = $proc.WorkingSet64
        if ($private -gt $peakPrivate) { $peakPrivate = $private }
        if ($working -gt $peakWorking) { $peakWorking = $working }
        $samples.Add([pscustomobject]@{
            ElapsedMs   = [int]$sw.ElapsedMilliseconds
            PrivateMB   = [math]::Round($private / 1MB, 1)
            WorkingSetMB= [math]::Round($working / 1MB, 1)
            Threads     = $proc.Threads.Count
        })
    } catch {
        break
    }
}

$proc.WaitForExit()
$samples | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding utf8

Write-Output ""
Write-Output ("exit code    : {0}" -f $proc.ExitCode)
Write-Output ("samples      : {0}" -f $samples.Count)
Write-Output ("peak private : {0} MB" -f [math]::Round($peakPrivate / 1MB, 1))
Write-Output ("peak working : {0} MB" -f [math]::Round($peakWorking / 1MB, 1))
Write-Output ("csv          : {0}" -f $OutCsv)

if ($samples.Count -gt 0) {
    Write-Output ""
    Write-Output "elapsed_ms  private_mb  working_mb  threads"
    $step = [math]::Max(1, [int]($samples.Count / 24))
    for ($i = 0; $i -lt $samples.Count; $i += $step) {
        $s = $samples[$i]
        Write-Output ("{0,9}  {1,10}  {2,10}  {3,7}" -f $s.ElapsedMs, $s.PrivateMB, $s.WorkingSetMB, $s.Threads)
    }
    $last = $samples[$samples.Count - 1]
    Write-Output ("{0,9}  {1,10}  {2,10}  {3,7}" -f $last.ElapsedMs, $last.PrivateMB, $last.WorkingSetMB, $last.Threads)
}
