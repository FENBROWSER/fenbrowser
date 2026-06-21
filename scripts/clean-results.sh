#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f "AGENTS.md" ]]; then
  echo "Error: run this script from the FenBrowser repository root." >&2
  exit 1
fi

if [[ -e "Results" ]]; then
  rm -rf -- "Results"
fi

mkdir -- "Results"
echo "Cleared Results successfully."
