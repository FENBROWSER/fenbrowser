@echo off
setlocal EnableExtensions
rem Chrome WebDriver launcher for WPT
set "SCRIPT_DIR=%~dp0"
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

rem Use chromedriver from the WPT venv
set "CHROMEDRIVER=C:\Users\udayk\Videos\wpt\_venv3\Lib\site-packages\chromedriver_autoinstaller\152\chromedriver.exe"
if not exist "%CHROMEDRIVER%" (
  echo [wpt-chrome-launcher] chromedriver not found at %CHROMEDRIVER% 1>&2
  exit /b 3
)

rem Start chromedriver on the specified port
"%CHROMEDRIVER%" --port=%PORT% --headless --no-sandbox --disable-dev-shm-usage --disable-gpu
set "CHROMEDRIVER_EXIT=%ERRORLEVEL%"
echo [wpt-chrome-launcher] chromedriver exited code %CHROMEDRIVER_EXIT% on port %PORT% 1>&2
exit /b %CHROMEDRIVER_EXIT%