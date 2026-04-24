@echo off
setlocal
set "ROOT=%~dp0.."
set "TOOLING_DLL=%ROOT%\FenBrowser.Tooling\bin\Release\net8.0\FenBrowser.Tooling.dll"

if not exist "%TOOLING_DLL%" (
  echo FenBrowser.Tooling.dll not found: %TOOLING_DLL%
  exit /b 1
)

"C:\Program Files\dotnet\dotnet.exe" "%TOOLING_DLL%" webdriver %*
