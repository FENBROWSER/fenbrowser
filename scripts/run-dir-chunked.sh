#!/usr/bin/env bash
# RAM-safe runner for large/flat test262 directories. The runner leaks live
# memory across a single process (a per-test JsHeap pinned by abandoned timeout
# worker threads, not reclaimable by GC), so a 1900-test flat dir balloons past
# 25 GB. The only reliable bound is process exit, so we run the directory as a
# series of --skip/--max windows, each its own short-lived process that releases
# all its RAM when it exits. Per-chunk JSONs (b_<tag>_cNNN.json) are summed by the
# existing aggregator with no double-count (no parent b_<tag>.json is written).
#
# Usage: bash scripts/run-dir-chunked.sh <reldir> [<reldir> ...]
#   env: CHUNK (default 250), STALL_TIMEOUT_SEC (default 60)
set -u
export TEST262_PROGRESS=1
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"

# Resolve the test262 checkout: TEST262_ROOT env var wins, then a sibling
# directory next to this repo, then a local `test262` directory.
if [ -n "${TEST262_ROOT:-}" ]; then
  ROOT="$TEST262_ROOT"
elif [ -d "../test262" ]; then
  ROOT="$(cd .. && pwd)/test262"
elif [ -d "./test262" ]; then
  ROOT="$(pwd)/test262"
else
  echo "test262 checkout not found. Set TEST262_ROOT or place it as ../test262 (sibling of this repo)." >&2
  exit 2
fi

OUTDIR="Results/test262/batched"
CHUNK=${CHUNK:-250}
STALL=${STALL_TIMEOUT_SEC:-60}
mkdir -p "$OUTDIR"

for rel in "$@"; do
  base=$(echo "$rel" | sed 's#/#_#g')
  # Count scoped test files (recursive .js under the dir).
  total=$(find "$ROOT/test/$rel" -name '*.js' -type f 2>/dev/null | wc -l)
  # Hard timeout: chunk_size × per-test-timeout + 30s buffer
  HARD_TIMEOUT=$(( CHUNK * 2 + 30 ))
  echo "=== $rel : $total tests, chunk=$CHUNK, hard_timeout=${HARD_TIMEOUT}s ==="
  skip=0; c=0
  while [ "$skip" -lt "$total" ]; do
    tag="${base}_c$(printf '%03d' "$c")"
    out="$OUTDIR/b_${tag}.json"; log="$OUTDIR/b_${tag}.log"
    printf '  chunk %-3d skip=%-5d ' "$c" "$skip"
    : > "$log"
    timeout "$HARD_TIMEOUT" "$EXE" --runtime-subset --root "$ROOT" --test262 "$ROOT/test/$rel" \
        --skip "$skip" --max "$CHUNK" --timeout-ms 2000 --out "$out" >"$log" 2>&1 &
    pid=$!; winpid=$(cat "/proc/$pid/winpid" 2>/dev/null || echo ""); stalled=0
    while kill -0 "$pid" 2>/dev/null; do
      sleep 2
      mtime=$(stat -c %Y "$log" 2>/dev/null || echo 0)
      if [ $(( $(date +%s) - mtime )) -gt "$STALL" ]; then
        stalled=1
        [ -n "$winpid" ] && taskkill //PID "$winpid" //F //T >/dev/null 2>&1
        kill -9 "$pid" 2>/dev/null; break
      fi
    done
    wait "$pid" 2>/dev/null
    line=$(python -c "
import json
try:
    d=json.load(open(r'$out',encoding='utf-8-sig')); print(d['passed'], d['total'])
except Exception: print('0 0')")
    [ "$stalled" = "1" ] && echo "STALL $line" || echo "$line"
    skip=$((skip + CHUNK)); c=$((c + 1))
  done
done

python -c "
import json,glob
P=T=0
for f in glob.glob('$OUTDIR/b_*.json'):
    try:
        d=json.load(open(f,encoding='utf-8-sig')); P+=d['passed']; T+=d['total']
    except: pass
json.dump({'passed':P,'total':T,'pct':round(100*P/T,2) if T else 0}, open('$OUTDIR/_batched_total.json','w'))
print('AGGREGATE', P, '/', T, '=', round(100*P/T,2) if T else 0, '%')"
