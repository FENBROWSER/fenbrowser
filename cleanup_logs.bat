@echo off
set TARGET_DIR=C:\Users\udayk\Videos\FENBROWSER

echo Deleting .log files...
del /q "%TARGET_DIR%\logs\*"
del /q "%TARGET_DIR%\*.log"
del /q "%TARGET_DIR%\*.png"
del /q "%TARGET_DIR%\*.csv"
del /q "%TARGET_DIR%\*.js"
del /q "%TARGET_DIR%\*.py"
del /q "%TARGET_DIR%\*.html"
echo Deleting .txt files...
del /q "%TARGET_DIR%\*.txt"

echo Done.
