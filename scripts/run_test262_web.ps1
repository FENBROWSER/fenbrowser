# Full test262 run with Flask web dashboard.
#
# This launcher handles the complete lifecycle:
#   1. Creates/verifies the Python venv (scripts\.test262_venv)
#   2. Builds the C# test262 runner
#   3. Starts the Flask dashboard server in the background
#   4. Opens the browser to http://localhost:5099
#   5. Starts the test262 batch runner
#   6. When the runner finishes, keeps the server alive so you can browse results
#   7. Click "Stop Server" in the dashboard to shut down Flask
#
# Usage:
#   pwsh scripts/run_test262_web.ps1             # full batched run
#   pwsh scripts/run_test262_web.ps1 -Categories # category sweep
#   pwsh scripts/run_test262_web.ps1 -Chunked    # chunked dirs
#
param(
    [switch]$Full,
    [switch]$Categories,
    [switch]$Chunked,
    [string]$Test262Root = "",
    [string]$BatchDir = "Results\test262\batched",
    [string]$VenvPath = "scripts\.test262_venv",
    [int]$Port = 5099,
    [int]$TimeoutMs = 2000
)

$ErrorActionPreference = "Stop"

# Resolve the test262 checkout (env TEST262_ROOT, sibling dir, or local dir).
. "$PSScriptRoot\resolve-paths.ps1"
$Test262Root = Resolve-Test262Root -ExplicitRoot $Test262Root

$repoRoot = $PSScriptRoot + "\.."
Push-Location $repoRoot

$VenvPython = "$VenvPath\Scripts\python.exe"
$DashApp = "scripts\test262_web\app.py"
$SetupScript = "scripts\setup_test262_venv.ps1"

# ── 1. venv setup ────────────────────────────────────────────────────────
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "  test262 Web Dashboard Launcher" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

if (-not (Test-Path $VenvPython)) {
    Write-Host "Venv not found. Running setup..." -ForegroundColor Yellow
    if (Test-Path $SetupScript) {
        & pwsh $SetupScript
    } else {
        Write-Host "Setup script not found. Creating venv manually..." -ForegroundColor Yellow
        $py = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
        if (-not $py) {
            Write-Host "ERROR: Python not found." -ForegroundColor Red
            exit 1
        }
        & $py.Source -m venv $VenvPath
        & $VenvPython -m pip install flask --quiet
    }
    if (-not (Test-Path $VenvPython)) {
        Write-Host "ERROR: Venv setup failed." -ForegroundColor Red
        exit 1
    }
}
Write-Host "Venv: $VenvPython" -ForegroundColor Green

# ── 2. build runner ──────────────────────────────────────────────────────
Write-Host "Building test262 runner..." -ForegroundColor Cyan
dotnet build FenBrowser.Js.Test262\FenBrowser.Js.Test262.csproj -c Release --no-restore 2>$null
if (-not (Test-Path "FenBrowser.Js.Test262\bin\Release\net10.0\FenBrowser.Js.Test262.dll")) {
    Write-Host "ERROR: Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Runner ready." -ForegroundColor Green

# ── 3. clean stale progress files ────────────────────────────────────────
if (Test-Path $BatchDir) {
    Remove-Item "$BatchDir\b_*_progress.jsonl" -ErrorAction SilentlyContinue
}

# ── 4. start Flask server ────────────────────────────────────────────────
$env:TEST262_BATCH_DIR = $BatchDir
$env:FLASK_APP = $DashApp

Write-Host ""
Write-Host "Starting Flask dashboard server..." -ForegroundColor Cyan

$flaskProc = Start-Process -FilePath $VenvPython `
  -ArgumentList $DashApp, "--port", $Port, "--batch-dir", $BatchDir `
  -PassThru -NoNewWindow -RedirectStandardOutput ".test262_web_out.log" `
  -RedirectStandardError ".test262_web_err.log"

Write-Host "  Dashboard server started (PID: $($flaskProc.Id))" -ForegroundColor Green
Write-Host "  URL: http://localhost:$Port" -ForegroundColor Cyan
Write-Host ""

# Wait for Flask to be ready
$ready = $false
for ($i = 0; $i -lt 15; $i++) {
    try {
        $ping = Invoke-WebRequest -Uri "http://localhost:$Port/api/ping" -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($ping.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {}
    Start-Sleep -Seconds 1
}

if (-not $ready) {
    Write-Host "WARNING: Flask server may not be ready yet. Check logs." -ForegroundColor Yellow
}

# ── 5. open browser ──────────────────────────────────────────────────────
Write-Host "Opening browser..." -ForegroundColor Cyan
Start-Process "http://localhost:$Port"
Start-Sleep -Seconds 2

# ── 6. run tests ─────────────────────────────────────────────────────────
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "  Starting test262 runner..." -ForegroundColor Green
Write-Host "  Dashboard: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "  Stop:      Click 'Stop Server' in the dashboard" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""

$env:TEST262_PROGRESS = "1"

if ($Categories) {
    & pwsh scripts/run-test262-category-resume.ps1 -TimeoutMs $TimeoutMs
} elseif ($Chunked) {
    & bash scripts/run-dir-chunked.sh
} else {
    & bash scripts/run_full_batched.sh
}

# ── 7. tests done — keep server alive ────────────────────────────────────
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "  Tests finished!" -ForegroundColor Green
Write-Host "  Dashboard: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "  Click 'Stop Server' in the dashboard to shut down" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""

try {
    Write-Host "Waiting for Flask server (PID: $($flaskProc.Id))..." -ForegroundColor Dim
    $flaskProc.WaitForExit()
} catch {
    # User stopped server or pressed Ctrl+C
} finally {
    if (-not $flaskProc.HasExited) {
        Write-Host "Stopping Flask server..." -ForegroundColor Yellow
        $flaskProc.Kill($true)
    }
    Write-Host "Flask server stopped." -ForegroundColor Green
}

Pop-Location
