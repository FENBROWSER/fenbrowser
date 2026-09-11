# Load a URL in the GUI browser N times, screenshotting each run after a fixed settle,
# so an intermittent layout complaint can be seen across runs instead of guessed at.
#
#   powershell -ExecutionPolicy Bypass -File scripts/capture_site_runs.ps1 -Url https://www.google.com -Runs 3 -SettleSec 25 -OutDir logs/runs

param([string]$Url = "https://www.google.com", [int]$Runs = 3, [int]$SettleSec = 25, [string]$OutDir = "logs/runs", [string]$Size = "1920x1000")

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }
$env:FEN_LOG_PRESET = 'testrun'
for ($i = 1; $i -le $Runs; $i++) {
    Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Start-Process -FilePath "FenBrowser.Host\bin\Release\net10.0\FenBrowser.Host.exe" -ArgumentList "--windowed --window-size $Size $Url" | Out-Null
    Start-Sleep -Seconds $SettleSec
    $out = Join-Path $OutDir ("run{0}.png" -f $i)
    powershell -ExecutionPolicy Bypass -File scripts/capture_window.ps1 -Out $out
    $p = Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    $p | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 6
    Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
