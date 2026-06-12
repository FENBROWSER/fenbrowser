@echo off
setlocal EnableExtensions
rem WPT drives the browser through WebDriver, whose execute/async bridge polls a
rem JS global for the callback completion flag. The default dual-engine runtime
rem (FenJS-preview with silent legacy fallback) splits that write/read across two
rem separate window globals, so the flag is never observed and every script hits
rem its timeout. Pin the WebDriver session to the legacy engine, which also has
rem the mature DOM/WebAPI bridge the WPT testharness depends on.
set "FEN_BROWSER_SCRIPT_ENGINE=legacy"
set "PORT="
if /I "%~1"=="--port" (
  set "PORT=%~2"
  shift
  shift
) else (
  for /f "tokens=1,2 delims==" %%A in ("%~1") do (
    if /I "%%~A"=="--port" set "PORT=%%~B"
  )
  if /I "%~1"=="--port=" set "PORT=%~2"
)
if not defined PORT (
  echo [wpt-webdriver-launcher] Missing --port argument 1>&2
  exit /b 2
)
set "REPO_ROOT=%~dp0.."
set "TOOLING_EXE=%REPO_ROOT%\FenBrowser.Tooling\bin\Release\net10.0\FenBrowser.Tooling.exe"
if defined FEN_WPT_TOOLING_EXE set "TOOLING_EXE=%FEN_WPT_TOOLING_EXE%"
if not exist "%TOOLING_EXE%" set "TOOLING_EXE=%REPO_ROOT%\FenBrowser.Tooling\bin\Debug\net10.0\FenBrowser.Tooling.exe"
if not exist "%TOOLING_EXE%" (
  echo [wpt-webdriver-launcher] FenBrowser.Tooling.exe not found. Build FenBrowser.Tooling first or set FEN_WPT_TOOLING_EXE. 1>&2
  exit /b 3
)
"%TOOLING_EXE%" webdriver --headless --port "%PORT%"
exit /b %ERRORLEVEL%
