#!/usr/bin/env bash
# Measures reCAPTCHA's post-click JS job inside the real GUI, with the
# renderer child's GC and CPU counters sampled while it runs.
#
# The headless harness cannot show this: the same job is 4.5s there, 6s in
# a 1280x800 window and 10s maximized on a 4K display, so whatever costs the
# time lives in the renderer child's rendering, not the interpreter.
#
# Usage: bash scripts/measure_captcha_job.sh <tag> [--window-size WxH | --maximized] [extra Host args]
# Output: logs/measure_<tag>/{console.log,counters.csv,summary.txt}
set -u

TAG="${1:?usage: measure_captcha_job.sh <tag> [--window-size WxH | --maximized]}"
shift
LAYOUT_ARGS=("$@")
URL="${CAPTCHA_URL:-https://www.google.com/recaptcha/api2/demo}"
RUN_DIR="logs/measure_${TAG}"
HOST="./FenBrowser.Host/bin/Release/net10.0/FenBrowser.Host.exe"
# Checkbox in window-client coordinates: the document position is fixed by
# the page (55,373) and the browser chrome above the document is 81px tall.
CLICK_X="${CLICK_X:-55}"
CLICK_Y="${CLICK_Y:-454}"

rm -rf "$RUN_DIR"; mkdir -p "$RUN_DIR"
export FEN_DIAGNOSTICS_DIR="$(pwd)/$RUN_DIR"
rm -f logs/*.jsonl

"$HOST" "${LAYOUT_ARGS[@]}" "$URL" > "$RUN_DIR/console.log" 2>&1 &
HOST_PID=$!

# Wait for the anchor widget to be attached (the checkbox exists from here).
for i in $(seq 1 60); do
    if grep -q "iframe document attached final='https://www.google.com/recaptcha/api2/anchor" "$RUN_DIR/console.log" 2>/dev/null; then break; fi
    sleep 0.5
done
if ! grep -q "recaptcha/api2/anchor" "$RUN_DIR/console.log" 2>/dev/null; then
    echo "[measure] anchor never attached"; kill $HOST_PID 2>/dev/null; exit 1
fi

# The renderer child is the FenBrowser.Host process that is not the broker
# and not the warm spare: the one that logged the anchor load owns the page.
CHILD_PID=$(grep -o '"process_id":[0-9]*' logs/*.jsonl 2>/dev/null | sort | uniq -c | sort -rn | head -1 | grep -o '[0-9]*$')
if [ -z "${CHILD_PID:-}" ]; then
    CHILD_PID=$(tasklist //FI "IMAGENAME eq FenBrowser.Host.exe" //FO CSV //NH | sort -t, -k5 -rn | head -1 | cut -d, -f2 | tr -d '"')
fi
echo "[measure] host=$HOST_PID child=$CHILD_PID layout=${LAYOUT_ARGS[*]:-maximized}"

# Let the widget finish its own setup before clicking, like a person would.
sleep 4
dotnet-counters collect -p "$CHILD_PID" --counters System.Runtime --refresh-interval 1 --format csv -o "$RUN_DIR/counters.csv" > "$RUN_DIR/counters.log" 2>&1 &
COUNTERS_PID=$!
sleep 2

powershell -NoProfile -ExecutionPolicy Bypass -File scripts/click_via_message.ps1 -X "$CLICK_X" -Y "$CLICK_Y"
CLICK_AT=$(date +%s)

# Wait for the post-click job to complete (it is the one job over 2s).
for i in $(seq 1 90); do
    if grep -q "Completed id=[0-9]* kind=InvokeFenJsCallbackSafely elapsedMs=[0-9]\{4,\}" "$RUN_DIR/console.log" logs/*.jsonl 2>/dev/null; then break; fi
    sleep 1
done
sleep 3
kill $COUNTERS_PID 2>/dev/null; sleep 1
kill $HOST_PID 2>/dev/null
taskkill //IM FenBrowser.Host.exe //F > /dev/null 2>&1

{
    echo "layout: ${LAYOUT_ARGS[*]:-maximized}"
    grep -o "renderer received MouseUp[^|]*" "$RUN_DIR/console.log" | head -1
    grep -ho "Completed id=[0-9]* kind=InvokeFenJsCallbackSafely elapsedMs=[0-9]* instructions=[0-9]*" "$RUN_DIR/console.log" logs/*.jsonl | sort -u | head -3
    mv logs/*.jsonl "$RUN_DIR/" 2>/dev/null
    echo "paints during run: $(grep -c 'Renderer pass complete' "$RUN_DIR/console.log")"
    python - "$RUN_DIR/counters.csv" <<'EOF'
import csv,sys,collections
rows=list(csv.DictReader(open(sys.argv[1])))
by=collections.defaultdict(list)
for r in rows: by[r['Counter Name']].append(float(r['Mean/Increment']))
def show(name,fmt="{:.1f}"):
    v=by.get(name)
    if v: print(f"  {name}: max={fmt.format(max(v))} avg={fmt.format(sum(v)/len(v))} samples={len(v)} sumIncr={fmt.format(sum(v))}")
for n in ['% Time in GC since last GC (%)','GC Heap Size (MB)','Gen 0 GC Count','Gen 1 GC Count','Gen 2 GC Count','CPU Usage (%)','Working Set (MB)','Allocation Rate (B / 1 sec)','ThreadPool Thread Count','Number of Active Timers']:
    show(n)
EOF
} | tee "$RUN_DIR/summary.txt"
