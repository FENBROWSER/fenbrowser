@echo off
setlocal EnableExtensions
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
"C:\Users\udayk\Videos\fenbrowser-test\FenBrowser.Tooling\bin\Release\net8.0\FenBrowser.Tooling.exe" webdriver --headless --port "%PORT%"
exit /b %ERRORLEVEL%