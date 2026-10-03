#!/usr/bin/env bash
# Samples the engine while debug-site loads a page, for load-time profiling.
#
# Usage: bash scripts/site_sample.sh <tag> <url> [delay_s] [duration_s] [settle_ms]
# Then:  python scripts/profile_inclusive.py logs/sample_<tag>/<tag>.speedscope.json
set -u

TAG="${1:?usage: site_sample.sh <tag> <url> [delay_s] [duration_s] [settle_ms]}"
URL="${2:?url}"
DELAY="${3:-2}"
DURATION="${4:-30}"
SETTLE="${5:-10000}"
OUT="${OUT_DIR:-logs/sample_${TAG}}"
TOOLING="./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"

rm -rf "$OUT"; mkdir -p "$OUT"
"$TOOLING" debug-site "$URL" "$SETTLE" > "$OUT/console.log" 2>&1 &
TOOL_PID=$!

sleep "$DELAY"
PID=$(tasklist //FI "IMAGENAME eq FenBrowser.Tooling.exe" //FO CSV //NH 2>/dev/null |
    head -1 | cut -d, -f2 | tr -d '"')
if [ -z "${PID:-}" ]; then
    echo "[sample] no engine process to attach to"
    wait "$TOOL_PID"; exit 1
fi

echo "[sample] attaching to pid=$PID for ${DURATION}s"
dotnet-trace collect -p "$PID" --profile dotnet-sampled-thread-time \
    --format speedscope --duration "00:00:00:${DURATION}" \
    -o "$OUT/${TAG}.nettrace" > "$OUT/trace.log" 2>&1

wait "$TOOL_PID"
echo "[sample] done -> $OUT"
grep -E "Elapsed|parse\+compile" "$OUT/console.log" | head -5
