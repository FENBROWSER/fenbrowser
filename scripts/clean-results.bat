@echo off
setlocal

if not exist "AGENTS.md" (
    echo Error: run this script from the FenBrowser repository root. 1>&2
    exit /b 1
)

if exist "Results" (
    rmdir /s /q "Results"
    if exist "Results" (
        echo Error: could not completely remove Results. Check for locked files. 1>&2
        exit /b 1
    )
)

mkdir "Results"
if errorlevel 1 (
    echo Error: could not recreate Results. 1>&2
    exit /b 1
)

echo Cleared Results successfully.
exit /b 0
