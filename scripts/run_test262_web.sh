#!/usr/bin/env bash
# Full test262 run with Flask web dashboard (bash version).
#
# Usage:  bash scripts/run_test262_web.sh
set -u

RED='\033[0;31m' GREEN='\033[0;32m' CYAN='\033[0;36m' YELLOW='\033[0;33m' NC='\033[0m'

VENV="scripts/.test262_venv"
BATCH_DIR="Results/test262/batched"
PORT="${TEST262_WEB_PORT:-5099}"
DASH_APP="scripts/test262_web/app.py"

# ── 1. venv ──────────────────────────────────────────────────────────────
VENV_PY=""
if [ -f "$VENV/Scripts/python.exe" ]; then
  VENV_PY="$VENV/Scripts/python.exe"   # Windows layout
elif [ -f "$VENV/bin/python" ]; then
  VENV_PY="$VENV/bin/python"           # Unix layout
fi

if [ -z "$VENV_PY" ]; then
  echo -e "${YELLOW}Venv not found. Run:  bash scripts/setup_test262_venv.sh${NC}"
  exit 1
fi

echo -e "${CYAN}Venv: $VENV_PY${NC}"

# ── 2. build ─────────────────────────────────────────────────────────────
echo -e "${CYAN}Building runner...${NC}"
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release --no-restore 2>/dev/null
if [ ! -f "FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.dll" ]; then
  echo -e "${RED}ERROR: Build failed.${NC}"
  exit 1
fi
echo -e "${GREEN}Runner ready.${NC}"

# ── 3. clean stale progress ──────────────────────────────────────────────
rm -f "$BATCH_DIR"/b_*_progress.jsonl 2>/dev/null || true

# ── 4. start Flask server ────────────────────────────────────────────────
export TEST262_BATCH_DIR="$BATCH_DIR"
echo -e "${CYAN}Starting Flask dashboard on port $PORT...${NC}"

"$VENV_PY" "$DASH_APP" --port "$PORT" --batch-dir "$BATCH_DIR" &
FLASK_PID=$!
echo -e "${GREEN}Flask server started (PID: $FLASK_PID)${NC}"

# Wait for Flask to be ready
for i in {1..15}; do
  if curl -s "http://localhost:$PORT/api/ping" >/dev/null 2>&1; then
    echo -e "${GREEN}Server ready.${NC}"
    break
  fi
  sleep 1
done

# ── 5. open browser ──────────────────────────────────────────────────────
if command -v start &>/dev/null; then
  start "http://localhost:$PORT" 2>/dev/null || true  # Windows
elif command -v open &>/dev/null; then
  open "http://localhost:$PORT" 2>/dev/null || true    # macOS
elif command -v xdg-open &>/dev/null; then
  xdg-open "http://localhost:$PORT" 2>/dev/null || true # Linux
fi

# ── 6. run tests ─────────────────────────────────────────────────────────
echo -e "${GREEN}Starting test262 runner...${NC}"
echo -e "${CYAN}Dashboard: http://localhost:$PORT${NC}"
echo ""

export TEST262_PROGRESS=1
bash scripts/run_full_batched.sh

# ── 7. done ──────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}Tests finished!${NC}"
echo -e "${CYAN}Dashboard at http://localhost:$PORT${NC}"
echo -e "${YELLOW}Click 'Stop Server' in the dashboard, or press Ctrl+C to stop Flask${NC}"

# Wait for user
wait $FLASK_PID 2>/dev/null || true
echo -e "${GREEN}Flask server stopped.${NC}"
