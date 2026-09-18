@echo off
setlocal EnableExtensions
rem Chrome WebDriver launcher for WPT — the reference-browser side of a
rem FenBrowser-vs-Chrome comparison. Same --port contract as webdriver-launcher.cmd.
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
  echo [wpt-chrome-launcher] Missing --port argument 1>&2
  exit /b 2
)

rem chromedriver from the WPT venv (WPT_ROOT, default D:\wpt); override with WPT_CHROMEDRIVER.
if not defined WPT_ROOT set "WPT_ROOT=D:\wpt"
set "CHROMEDRIVER="
if defined WPT_CHROMEDRIVER set "CHROMEDRIVER=%WPT_CHROMEDRIVER%"
if not defined CHROMEDRIVER for /d %%D in ("%WPT_ROOT%\_venv3\Lib\site-packages\chromedriver_autoinstaller\*") do if exist "%%~D\chromedriver.exe" set "CHROMEDRIVER=%%~D\chromedriver.exe"
if not defined CHROMEDRIVER (
  echo [wpt-chrome-launcher] chromedriver not found under %WPT_ROOT%\_venv3; set WPT_CHROMEDRIVER 1>&2
  exit /b 3
)

"%CHROMEDRIVER%" --port=%PORT% --headless --no-sandbox --disable-dev-shm-usage --disable-gpu
set "CHROMEDRIVER_EXIT=%ERRORLEVEL%"
echo [wpt-chrome-launcher] chromedriver exited code %CHROMEDRIVER_EXIT% on port %PORT% 1>&2
exit /b %CHROMEDRIVER_EXIT%
