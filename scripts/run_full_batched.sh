#!/usr/bin/env bash
# Memory-safe full test262 run: one process per directory batch so RAM is
# released between batches. Aggregates pass/total into _batched_total.json.
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
mkdir -p "$OUTDIR"
PROG="$OUTDIR/_progress.txt"
: > "$PROG"

if [ ! -x "$EXE" ] && [ ! -f "$EXE" ]; then
  echo "Runner not found: $EXE (build: dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release)" >&2
  exit 1
fi

# Resume: by default a batch whose result JSON already exists is skipped, so an
# interrupted run continues where it stopped. FRESH=1 wipes prior batch results
# and reruns everything. (Stall-killed batches write no JSON, so they always rerun.)
FRESH=${FRESH:-0}
if [ "$FRESH" = "1" ]; then
  rm -f "$OUTDIR"/b_*.json "$OUTDIR"/b_*.log "$OUTDIR"/b_*.log.err "$OUTDIR"/_batched_total.json 2>/dev/null
fi

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

N=${#batches[@]}
echo "Running $N batches  (per-test 2000ms, stall ${STALL}s)  ->  $OUTDIR"
i=0
for b in "${batches[@]}"; do
  i=$((i+1))
  tag=$(echo "$b" | sed 's#.*/test/##; s#/#_#g; s#_*$##')
  out="$OUTDIR/b_${tag}.json"
  log="$OUTDIR/b_${tag}.log"

  # Resume: skip a batch that already has a valid result JSON.
  if [ -f "$out" ]; then
    done=$(python -c "
import json,sys
try:
    d=json.load(open(r'$out',encoding='utf-8-sig'))
    if d.get('total') is None: sys.exit(1)
    print(d.get('passed',0), d['total'])
except Exception:
    sys.exit(1)
" 2>/dev/null) && { printf '[%3d/%d] %-44s SKIP   %s (cached)\n' "$i" "$N" "$tag" "$done"; echo "$i ${tag} $done SKIP" >> "$PROG"; continue; }
  fi

  printf '[%3d/%d] %-44s run ...
' "$i" "$N" "$tag"
  # Count tests and compute hard timeout: N×2s + 30s buffer
  n_tests=$(find "$b" -name '*.js' -type f 2>/dev/null | wc -l)
  hard=$(( n_tests * 2 + 30 ))
  : > "$log"
  timeout "$hard" "$EXE" --runtime-subset --root "$ROOT" --test262 "$b" --max 100000 --timeout-ms 2000 --out "$out" >"$log" 2>&1 &
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
    printf '[%3d/%d] %-44s STALL  %s\n' "$i" "$N" "$tag" "$line"
    echo "$i ${tag} $line STALL-KILL last:[$(tail -n1 "$log" 2>/dev/null)]" >> "$PROG"
  else
    printf '[%3d/%d] %-44s %s\n' "$i" "$N" "$tag" "$line"
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
