@echo off
set TARGET_DIR=C:\Users\udayk\Videos\FENBROWSER

echo Deleting .log files...
del /q "%TARGET_DIR%\logs\*.log"
del /q "%TARGET_DIR%\logs\*.html"
del /q "%TARGET_DIR%\*.log"
del /q "%TARGET_DIR%\debug_screenshot.png"

echo Deleting .txt files...
del /q "%TARGET_DIR%\*.txt"

echo Done.
