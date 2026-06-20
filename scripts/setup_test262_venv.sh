#!/usr/bin/env bash
# Creates a Python venv at scripts/.test262_venv and installs Flask.
# Run from repo root:  bash scripts/setup_test262_venv.sh
set -u

VENV="scripts/.test262_venv"

# ── find Python ──────────────────────────────────────────────────────────
PYTHON=""
for p in python3 python; do
  if command -v "$p" &>/dev/null; then
    PYTHON="$p"
    break
  fi
done

if [ -z "$PYTHON" ]; then
  echo "ERROR: Python not found in PATH." >&2
  echo "Install Python from https://python.org and try again." >&2
  exit 1
fi

echo "Python: $(command -v "$PYTHON")"

# ── create venv ──────────────────────────────────────────────────────────
if [ ! -d "$VENV" ]; then
  echo "Creating venv at $VENV ..."
  "$PYTHON" -m venv "$VENV" || { echo "ERROR: Failed to create venv." >&2; exit 1; }
  echo "Venv created."
else
  echo "Venv already exists at $VENV"
fi

# ── install dependencies ─────────────────────────────────────────────────
PIP="$VENV/bin/python"
if [ ! -f "$PIP" ]; then
  PIP="$VENV/Scripts/python.exe"  # Windows venv layout
fi

echo "Installing Flask..."
"$PIP" -m pip install flask --quiet || { echo "ERROR: pip install failed." >&2; exit 1; }

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  Venv ready at: $VENV"
echo "  Python:        $PIP"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Start the dashboard:"
echo "  $PIP scripts/test262_web/app.py"
