#!/usr/bin/env bash
# Memory-safe full test262 run: one process per directory batch so RAM is
# released between batches. Aggregates pass/total into _batched_total.json.
set -u
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"
ROOT="C:/Users/udayk/Videos/test262"
OUTDIR="Results/test262/batched"
mkdir -p "$OUTDIR"
PROG="$OUTDIR/_progress.txt"
: > "$PROG"

# Build the batch list: split the two oversized language dirs one level deeper,
# everything else runs as a second-level (or top-level) directory.
batches=()
for d in "$ROOT"/test/language/expressions/*/ "$ROOT"/test/language/statements/*/; do
  [ -d "$d" ] && batches+=("$d")
done
for d in "$ROOT"/test/language/*/; do
  case "$d" in */expressions/|*/statements/) continue;; esac
  batches+=("$d")
done
for d in "$ROOT"/test/built-ins/*/; do batches+=("$d"); done
batches+=("$ROOT/test/intl402" "$ROOT/test/annexB" "$ROOT/test/staging")

# Stall watchdog: the per-test --timeout-ms is cooperative and only abandons (does
# not kill) a worker thread, so a test wedged in native code — e.g. catastrophic
# regex backtracking — can hang a whole batch forever. The runner streams a
# [progress] line every ~5s; if a batch's log stops growing for $STALL seconds it is
# wedged on one test, so we kill the process tree and move on. This is what keeps a
# single test from blocking the entire suite.
STALL=${STALL_TIMEOUT_SEC:-30}

i=0
for b in "${batches[@]}"; do
  i=$((i+1))
  tag=$(echo "$b" | sed "s#.*/test/##; s#/#_#g; s#_*$##")
  out="$OUTDIR/b_${tag}.json"
  log="$OUTDIR/b_${tag}.log"
  : > "$log"
  "$EXE" --runtime-subset --root "$ROOT" --test262 "$b" --max 100000 --timeout-ms 2000 --out "$out" >"$log" 2>&1 &
  pid=$!
  winpid=$(cat "/proc/$pid/winpid" 2>/dev/null || echo "")
  stalled=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 2
    mtime=$(stat -c %Y "$log" 2>/dev/null || echo 0)
    idle=$(( $(date +%s) - mtime ))
    if [ "$idle" -gt "$STALL" ]; then
      stalled=1
      [ -n "$winpid" ] && taskkill //PID "$winpid" //F //T >/dev/null 2>&1
      kill -9 "$pid" 2>/dev/null
      break
    fi
  done
  wait "$pid" 2>/dev/null
  line=$(python -c "
import json
try:
    d=json.load(open(r'$out',encoding='utf-8-sig'))
    print(f\"{d['passed']} {d['total']}\")
except Exception as e:
    print('0 0')
")
  if [ "$stalled" = "1" ]; then
    echo "$i ${tag} $line STALL-KILL last:[$(tail -n1 "$log" 2>/dev/null)]" >> "$PROG"
  else
    echo "$i ${tag} $line" >> "$PROG"
  fi
done

python -c "
import json,glob
P=T=0
for f in glob.glob('$OUTDIR/b_*.json'):
    try:
        d=json.load(open(f,encoding='utf-8-sig'))
        P+=d['passed']; T+=d['total']
    except: pass
json.dump({'passed':P,'total':T,'pct':round(100*P/T,2) if T else 0}, open('$OUTDIR/_batched_total.json','w'))
print('DONE', P, '/', T, '=', round(100*P/T,2) if T else 0, '%')
" >> "$PROG"
echo "ALL_BATCHES_COMPLETE" >> "$PROG"
