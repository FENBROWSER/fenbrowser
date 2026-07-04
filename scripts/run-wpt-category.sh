#!/bin/bash
# Run a single WPT category against FenBrowser and save results.
# Usage: bash scripts/run-wpt-category.sh <category> [--timeout-seconds N] [--processes N]
# Example: bash scripts/run-wpt-category.sh dom/lists
#          bash scripts/run-wpt-category.sh accname --timeout-seconds 1200

set -euo pipefail

CATEGORY="${1:-}"
if [ -z "$CATEGORY" ]; then
    echo "Usage: bash scripts/run-wpt-category.sh <category> [--timeout-seconds N] [--processes N]"
    echo "Example: bash scripts/run-wpt-category.sh dom/lists"
    exit 1
fi
shift

TIMEOUT_SECONDS=600
PROCESSES=1
while [ $# -gt 0 ]; do
    case "$1" in
        --timeout-seconds) TIMEOUT_SECONDS="$2"; shift 2 ;;
        --processes) PROCESSES="$2"; shift 2 ;;
        *) echo "Unknown arg: $1"; exit 1 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLING_PROJ="$REPO_ROOT/FenBrowser.Tooling/FenBrowser.Tooling.csproj"
RESULTS_DIR="$REPO_ROOT/Results/wpt/categories"

mkdir -p "$RESULTS_DIR"

# Sanitize category name for filename
SAFE_NAME=$(echo "$CATEGORY" | tr '/' '_' | tr '\\' '_')
OUT_DIR="$RESULTS_DIR/wpt_${SAFE_NAME}_$(date +%Y%m%d_%H%M%S)"

echo "[wpt-category] category=$CATEGORY timeout=${TIMEOUT_SECONDS}s processes=$PROCESSES"
echo "[wpt-category] output=$OUT_DIR"

dotnet run --project "$TOOLING_PROJ" -- \
    wpt \
    --tests "$CATEGORY" \
    --processes "$PROCESSES" \
    --timeout-seconds "$TIMEOUT_SECONDS" \
    --output-dir "$OUT_DIR"

echo "[wpt-category] Done. Results: $OUT_DIR"
