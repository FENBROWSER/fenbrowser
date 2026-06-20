#!/usr/bin/env bash
# Full test262 batched run WITH live TUI dashboard.
#
# Usage:  bash scripts/run_test262_tui.sh
#
# This is the recommended way to run the full ~53k-test suite. The runner and
# TUI run in separate terminals so you can see live progress.
#
# Prerequisites:
#   pip install rich
#   dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release
set -u

RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

BATCH_DIR="Results/test262/batched"
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"
RUNNER_SCRIPT="scripts/run_full_batched.sh"

# ── build ──────────────────────────────────────────────────────────────────
echo -e "${CYAN}Building runner...${NC}"
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release \
  --no-restore 2>/dev/null || true
if [ ! -f "$EXE" ]; then
  echo -e "${RED}Runner not found after build: $EXE${NC}"
  exit 1
fi
echo -e "${GREEN}Runner ready.${NC}"

# ── clean stale progress files ─────────────────────────────────────────────
echo -e "${CYAN}Cleaning stale progress files...${NC}"
rm -f "$BATCH_DIR"/b_*_progress.jsonl 2>/dev/null || true

# ── launch ─────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${GREEN}  Starting test262 full run + TUI dashboard${NC}"
echo -e "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo "  Open a SECOND terminal and run:"
echo ""
echo -e "    ${CYAN}python scripts/test262_tui.py${NC}"
echo ""
echo "  Then, in THIS terminal, start the run:"
echo ""
echo -e "    ${CYAN}bash $RUNNER_SCRIPT${NC}"
echo ""
echo "  The TUI will auto-discover batches and show live progress."
echo ""
