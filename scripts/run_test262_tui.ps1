# Full test262 batched run with live TUI dashboard.
#
# This launches the runner in one window and the TUI in another.
# The TUI auto-discovers batch progress files and shows live status.
#
# Usage:
#   pwsh scripts/run_test262_tui.ps1          # full batched run + TUI
#   pwsh scripts/run_test262_tui.ps1 -Categories  # category sweep + TUI
#   pwsh scripts/run_test262_tui.ps1 -Chunked # chunked dirs + TUI
param(
    [switch]$Full,
    [switch]$Categories,
    [switch]$Chunked,
    [string]$BatchDir = "Results\test262\batched",
    [string]$Test262Root = "",
    [int]$TimeoutMs = 2000
)

$ErrorActionPreference = "Stop"

# Resolve the test262 checkout (env TEST262_ROOT, sibling dir, or local dir).
. "$PSScriptRoot\resolve-paths.ps1"
$Test262Root = Resolve-Test262Root -ExplicitRoot $Test262Root

# ── build ──────────────────────────────────────────────────────────────────
Write-Host "Building runner..." -ForegroundColor Cyan
dotnet build FenBrowser.Js.Test262\FenBrowser.Js.Test262.csproj -c Release --no-restore 2>$null
if (-not (Test-Path "FenBrowser.Js.Test262\bin\Release\net10.0\FenBrowser.Js.Test262.dll")) {
    Write-Host "ERROR: Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Runner ready." -ForegroundColor Green

# ── clean stale progress files ─────────────────────────────────────────────
Write-Host "Cleaning stale progress files..." -ForegroundColor Cyan
if (Test-Path $BatchDir) {
    Remove-Item "$BatchDir\b_*_progress.jsonl" -ErrorAction SilentlyContinue
}

# ── launch ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "  test262 Live Runner + TUI Dashboard" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""
Write-Host "  The runner and TUI run in separate windows."
Write-Host ""

# Determine which runner script to use
if ($Categories) {
    $runnerCmd = "pwsh -NoExit -Command `"scripts/run-test262-category-resume.ps1 -TimeoutMs $TimeoutMs`""
} elseif ($Chunked) {
    $runnerCmd = "pwsh -NoExit -Command `"bash scripts/run-dir-chunked.sh`""
} else {
    $runnerCmd = "pwsh -NoExit -Command `"bash scripts/run_full_batched.sh`""
}

# Launch the TUI in the current window, runner in a new one
$tuiTitle = "test262 TUI Dashboard"
$runnerTitle = "test262 Runner"

Write-Host "  Opening runner in a new window (title: '$runnerTitle')" -ForegroundColor Cyan
Write-Host "  TUI will start in this window shortly..." -ForegroundColor Cyan
Write-Host ""

# Start the runner in a new PowerShell window
$runnerArgs = "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle='$runnerTitle'; $runnerCmd"
Start-Process pwsh -ArgumentList $runnerArgs

# Give the runner a moment to start producing progress files
Start-Sleep -Seconds 3

# Launch the TUI in this window
Write-Host "Starting TUI dashboard..." -ForegroundColor Cyan
try {
    python scripts/test262_tui.py --batch-dir $BatchDir
} catch {
    Write-Host "TUI exited. Press Ctrl+C to stop the runner, or it will continue in its own window." -ForegroundColor Yellow
}
