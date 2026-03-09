#!/bin/bash
EXE="/c/Users/udayk/Videos/fenbrowser-test/FenBrowser.WPT/bin/Release/net8.0/FenBrowser.WPT.exe"
START=${1:-5}
END=${2:-20}
WORKERS=${3:-10}

for chunk in $(seq $START $END); do
  # RAM check
  FREE_KB=$(powershell -Command "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory" 2>/dev/null | tr -d '\r')
  if [ -n "$FREE_KB" ] && [ "$FREE_KB" -lt 10000000 ]; then
    echo "[RAM WARNING] Only ${FREE_KB}KB free. Stopping before chunk $chunk."
    break
  fi
  echo "=== Starting chunk $chunk | Workers: ${WORKERS} | Free RAM: ${FREE_KB}KB ==="
  $EXE run_chunk $chunk --workers $WORKERS 2>&1 | tail -2
  echo "=== Chunk $chunk done ==="
done
