@echo off
set TARGET_DIR=C:\Users\udayk\Videos\FENBROWSER

echo Deleting .log files...
del /q "%TARGET_DIR%\*.log"

echo Deleting .txt files...
del /q "%TARGET_DIR%\*.txt"

echo Done.
