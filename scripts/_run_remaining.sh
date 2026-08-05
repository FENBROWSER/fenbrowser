#!/usr/bin/env bash
# Run all remaining test262 directories through the chunked runner.
# Each directory is processed in 250-test chunks to avoid the JsValue
# static-pool memory leak that balloons past 25 GB in a single process.
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

# All remaining directories (relative to $ROOT/test)
DIRS=(
  # BIG (>250 tests) — run first since they dominate
  "built-ins/Temporal"
  "built-ins/Object"
  "intl402"
  "built-ins/Array"
  "built-ins/RegExp"
  "staging"
  "built-ins/TypedArray"
  "built-ins/String"
  "annexB"
  "built-ins/TypedArrayConstructors"
  "built-ins/Promise"
  "built-ins/Date"
  "built-ins/Iterator"
  "built-ins/Function"
  "built-ins/Set"
  "built-ins/Number"
  "built-ins/Math"
  "built-ins/Proxy"
  # SMALL (<=250 tests)
  "built-ins/Map"
  "built-ins/JSON"
  "built-ins/Reflect"
  "built-ins/WeakMap"
  "built-ins/SharedArrayBuffer"
  "built-ins/Symbol"
  "built-ins/NativeErrors"
  "built-ins/DisposableStack"
  "built-ins/Error"
  "built-ins/WeakSet"
  "built-ins/Uint8Array"
  "built-ins/ShadowRealm"
  "built-ins/GeneratorPrototype"
  "built-ins/decodeURIComponent"
  "built-ins/decodeURI"
  "built-ins/parseInt"
  "built-ins/parseFloat"
  "built-ins/FinalizationRegistry"
  "built-ins/encodeURIComponent"
  "built-ins/encodeURI"
  "built-ins/WeakRef"
  "built-ins/global"
  "built-ins/GeneratorFunction"
  "built-ins/SuppressedError"
  "built-ins/RegExpStringIteratorPrototype"
  "built-ins/isFinite"
  "built-ins/isNaN"
  "built-ins/ThrowTypeError"
  "built-ins/MapIteratorPrototype"
  "built-ins/SetIteratorPrototype"
  "built-ins/eval"
  "built-ins/undefined"
  "built-ins/StringIteratorPrototype"
  "built-ins/Infinity"
  "built-ins/NaN"
)

TOTAL_DIRS=${#DIRS[@]}
DIR_NUM=0
GLOBAL_PASS=0
GLOBAL_TOTAL=0

echo "=== Running $TOTAL_DIRS remaining directories (chunk=$CHUNK, stall=${STALL}s) ==="
echo "Start: $(date)"
echo

for rel in "${DIRS[@]}"; do
  DIR_NUM=$((DIR_NUM + 1))
  base=$(echo "$rel" | sed 's#/#_#g')

  # Check if already done (all chunks exist with valid JSON)
  existing_chunks=$(ls "$OUTDIR/b_${base}"_c*.json 2>/dev/null | wc -l)
  if [ "$existing_chunks" -gt 0 ]; then
    # Verify they're valid
    valid=0
    for f in "$OUTDIR/b_${base}"_c*.json; do
      python -c "import json; d=json.load(open(r'$f',encoding='utf-8-sig')); assert d.get('total') is not None" 2>/dev/null && valid=$((valid+1))
    done
    if [ "$valid" -eq "$existing_chunks" ]; then
      echo "[$DIR_NUM/$TOTAL_DIRS] $rel: SKIP ($existing_chunks chunks cached)"
      continue
    fi
    # Some chunks corrupted, remove and redo
    rm -f "$OUTDIR/b_${base}"_c*.json "$OUTDIR/b_${base}"_c*.log
  fi

  total=$(find "$ROOT/test/$rel" -name '*.js' -type f 2>/dev/null | wc -l)
  chunks=$(( (total + CHUNK - 1) / CHUNK ))
  HARD_TIMEOUT=$(( CHUNK * 2 + 30 ))

  echo "[$DIR_NUM/$TOTAL_DIRS] $rel: $total tests, $chunks chunks"

  skip=0; c=0; dir_pass=0; dir_total=0
  while [ "$skip" -lt "$total" ]; do
    tag="${base}_c$(printf '%03d' "$c")"
    out="$OUTDIR/b_${tag}.json"; log="$OUTDIR/b_${tag}.log"
    printf '  chunk %d/%d skip=%-5d ' "$((c+1))" "$chunks" "$skip"
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
    if [ "$stalled" = "1" ]; then
      echo "STALL $line"
    else
      echo "$line"
    fi
    p=$(echo "$line" | cut -d' ' -f1); t=$(echo "$line" | cut -d' ' -f2)
    dir_pass=$((dir_pass + p)); dir_total=$((dir_total + t))
    skip=$((skip + CHUNK)); c=$((c + 1))
  done
  GLOBAL_PASS=$((GLOBAL_PASS + dir_pass))
  GLOBAL_TOTAL=$((GLOBAL_TOTAL + dir_total))
  echo "  => $dir_pass/$dir_total ($(python -c "print(round(100*$dir_pass/$dir_total,2))")%)"
done

# Final aggregate
python -c "
import json,glob
P=T=0
for f in glob.glob('$OUTDIR/b_*.json'):
    try:
        d=json.load(open(f,encoding='utf-8-sig')); P+=d['passed']; T+=d['total']
    except: pass
json.dump({'passed':P,'total':T,'pct':round(100*P/T,2) if T else 0}, open('$OUTDIR/_batched_total.json','w'))
print('=== AGGREGATE:', P, '/', T, '=', round(100*P/T,2) if T else 0, '%', '===')
"
echo "End: $(date)"
echo "ALL_DONE" >> "$OUTDIR/_progress.txt"
