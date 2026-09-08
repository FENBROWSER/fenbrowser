#!/usr/bin/env bash
# One captcha investigation run, self-contained and repeatable.
#
# Builds the whole solution, puts this run's logs in their own subfolder under
# logs/ (the folder itself is often held open by a previous process, so runs are
# separated by subfolder rather than by deleting it), drives the reCAPTCHA demo
# end to end, and leaves both the console stream and the engine trace behind.
#
# Usage: bash scripts/captcha_probe.sh <tag> [ready_ms] [observe_ms]
# Env:   any FEN_* switch is passed through to the run.
set -u

TAG="${1:?usage: captcha_probe.sh <tag> [ready_ms] [observe_ms]}"
READY_MS="${2:-40000}"
OBSERVE_MS="${3:-40000}"
URL="${CAPTCHA_URL:-https://www.google.com/recaptcha/api2/demo}"

RUN_DIR="logs/run_${TAG}"
TOOLING="./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe"

echo "[probe] building solution"
if ! dotnet build FenBrowser.sln -c Release -v q --nologo > "/tmp/build_${TAG}.log" 2>&1; then
    echo "[probe] BUILD FAILED"
    grep -E "error" "/tmp/build_${TAG}.log" | head -20
    exit 1
fi
echo "[probe] build ok"

# A run's logs go somewhere nothing is holding open, and the engine is pointed
# at that folder so its trace lands beside the console stream.
rm -rf "$RUN_DIR" 2>/dev/null
mkdir -p "$RUN_DIR"
export FEN_DIAGNOSTICS_DIR="$(pwd)/$RUN_DIR"

echo "[probe] running captcha (ready=${READY_MS}ms observe=${OBSERVE_MS}ms) -> $RUN_DIR"
"$TOOLING" captcha "$URL" "$READY_MS" "$OBSERVE_MS" > "$RUN_DIR/console.log" 2>&1
STATUS=$?

# Engine logs are written to the configured LogPath, which is the repo logs/
# folder; move this run's files into the run folder so the next run starts clean.
find logs -maxdepth 1 -type f -newer "$RUN_DIR" -print0 2>/dev/null |
    xargs -0 -I{} mv {} "$RUN_DIR/" 2>/dev/null

echo "[probe] exit=$STATUS"
grep -E "^\[captcha\] (RESULT|\+[0-9])" "$RUN_DIR/console.log" | tail -8
echo "[probe] logs in $RUN_DIR"
