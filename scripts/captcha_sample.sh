#!/usr/bin/env bash
# Samples the engine while the captcha page's own blocking job is running.
#
# Tracing the whole run buries the job in start-up and idle time, and the
# window that matters is short, so the collector attaches to an already-running
# process and stops on its own.
#
# Usage: bash scripts/captcha_sample.sh <tag> [delay_s] [duration_s]
set -u

TAG="${1:?usage: captcha_sample.sh <tag> [delay_s] [duration_s]}"
DELAY="${2:-9}"
DURATION="${3:-20}"
URL="${CAPTCHA_URL:-https://www.google.com/recaptcha/api2/demo}"
OUT="${OUT_DIR:-logs/sample_${TAG}}"
TOOLING="./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"

rm -rf "$OUT"; mkdir -p "$OUT"
"$TOOLING" captcha "$URL" 35000 25000 > "$OUT/console.log" 2>&1 &
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
grep -E "^\[FenJsEngine\]" "$OUT/console.log" | sed 's/natives=.*//' 
