# Record an ETW CPU + .NET trace of one debug-site load, for ETW MCP analysis.
# Must run elevated (WPR requirement). Run from repo root:
#   powershell -ExecutionPolicy Bypass -File scripts/capture_layout_etl.ps1 -Url <url>
param(
    [string]$Url = "http://127.0.0.1:8099/index.html",
    [int]$SettleMs = 5000,
    [string]$Out = "logs/etw/layout.etl"
)
# Native tools report through exit codes; under "Stop", Windows PowerShell 5.1
# turns any stderr line (e.g. wpr -cancel with nothing running) into a fatal error.
$ErrorActionPreference = "Continue"
# An elevated process ignores -WorkingDirectory and starts in System32, so
# anchor every relative path to the repo root.
Set-Location (Split-Path -Parent $PSScriptRoot)
$wpr = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpr.exe"
$log = [IO.Path]::ChangeExtension($Out, ".capture.log")
Start-Transcript -Path $log -Force | Out-Null
try {
    & $wpr -cancel *> $null
    & $wpr -start CPU -start DotNET -filemode
    if ($LASTEXITCODE -ne 0) { throw "wpr -start failed ($LASTEXITCODE)" }
    & "FenBrowser.Tooling\bin\Release\net10.0\FenBrowser.Tooling.exe" debug-site $Url $SettleMs | Select-Object -Last 8
    & $wpr -stop $Out
    if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed ($LASTEXITCODE)" }
    "CAPTURE-OK $Out"
}
finally {
    Stop-Transcript | Out-Null
}
