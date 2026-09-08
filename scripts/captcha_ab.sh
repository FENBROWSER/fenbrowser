#!/usr/bin/env bash
# Repeats the captcha probe so a configuration can be compared against another
# without run-to-run noise deciding the answer. The page's own timings move by
# several seconds between identical runs, so a single run cannot tell a real
# change from weather.
#
# Usage: bash scripts/captcha_ab.sh <tag> <repeats>
# Env:   any FEN_* switch is passed through to every repeat.
set -u

TAG="${1:?usage: captcha_ab.sh <tag> <repeats>}"
N="${2:-3}"
URL="${CAPTCHA_URL:-https://www.google.com/recaptcha/api2/demo}"
TOOLING="./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"

echo "[ab] $TAG x$N"
for i in $(seq 1 "$N"); do
    RUN_DIR="logs/ab_${TAG}_$i"
    rm -rf "$RUN_DIR" 2>/dev/null
    mkdir -p "$RUN_DIR"
    FEN_DIAGNOSTICS_DIR="$(pwd)/$RUN_DIR" \
        "$TOOLING" captcha "$URL" 35000 25000 > "$RUN_DIR/console.log" 2>&1

    # The line the widget's own watchdog races: when the challenge frame exists.
    BFRAME=$(grep -E "^\[captcha\] \+.*bframes=[1-9]" "$RUN_DIR/console.log" |
        head -1 | grep -oE "\+[0-9.]+s" | head -1)
    RESULT=$(grep -oE "RESULT [a-z]+" "$RUN_DIR/console.log" | head -1)
    # The widget's own watchdog giving up is the failure being chased.
    TIMEOUTS=$(grep -rah "reCAPTCHA Timeout" "$RUN_DIR" 2>/dev/null | wc -l)
    LONGEST=$(grep -rahoE "elapsedMs=[0-9]+" "$RUN_DIR" 2>/dev/null |
        sort -t= -k2 -n | tail -1)
    echo "  run $i: bframe=${BFRAME:-none} ${RESULT:-none} rcTimeouts=${TIMEOUTS:-0} maxJob=${LONGEST:-none}"
done
