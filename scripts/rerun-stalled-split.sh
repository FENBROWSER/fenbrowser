#!/usr/bin/env bash
# Recover a stall-killed batch from run_full_batched.sh by splitting it finer:
# one shallow run for the parent's loose top-level .js files, plus one recursive
# run per immediate subdirectory. Each runs as its own process with the same
# stall watchdog, so one wedged test only loses its own bucket. Output JSONs land
# in the batched store (b_<tag>[_TOP|_<sub>].json) and are summed by the existing
# aggregator with no overlap/double-count (the stall-killed parent wrote no JSON).
#
# Usage: bash scripts/rerun-stalled-split.sh <reldir> [<reldir> ...]
#   e.g. bash scripts/rerun-stalled-split.sh language/expressions/class built-ins/Promise
set -u
export TEST262_PROGRESS=1
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"
ROOT="C:/Users/udayk/Videos/test262"
OUTDIR="Results/test262/batched"
STALL=${STALL_TIMEOUT_SEC:-45}
mkdir -p "$OUTDIR"

run_one() { # $1 = scope dir (abs), $2 = out tag, $3 = "shallow" | "deep"
  local scope="$1" tag="$2" mode="$3"
  local out="$OUTDIR/b_${tag}.json" log="$OUTDIR/b_${tag}.log"
  local shallow=""; [ "$mode" = "shallow" ] && shallow="--test262-shallow"
  printf '  %-52s run ...\n' "$tag"
  : > "$log"
  "$EXE" --runtime-subset --root "$ROOT" --test262 "$scope" $shallow \
      --max 100000 --timeout-ms 2000 --out "$out" >"$log" 2>&1 &
  local pid=$! winpid; winpid=$(cat "/proc/$pid/winpid" 2>/dev/null || echo "")
  local stalled=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 2
    local mtime; mtime=$(stat -c %Y "$log" 2>/dev/null || echo 0)
    if [ $(( $(date +%s) - mtime )) -gt "$STALL" ]; then
      stalled=1
      [ -n "$winpid" ] && taskkill //PID "$winpid" //F //T >/dev/null 2>&1
      kill -9 "$pid" 2>/dev/null; break
    fi
  done
  wait "$pid" 2>/dev/null
  local line; line=$(python -c "
import json
try:
    d=json.load(open(r'$out',encoding='utf-8-sig')); print(d['passed'], d['total'])
except Exception: print('0 0')")
  [ "$stalled" = "1" ] && printf '  %-52s STALL %s  (last: %s)\n' "$tag" "$line" "$(tail -n1 "$log")" \
                       || printf '  %-52s %s\n' "$tag" "$line"
}

for rel in "$@"; do
  base=$(echo "$rel" | sed 's#/#_#g')
  echo "=== $rel ==="
  run_one "$ROOT/test/$rel" "${base}_TOP" shallow
  for sub in "$ROOT/test/$rel"/*/; do
    [ -d "$sub" ] || continue
    subtag=$(basename "$sub")
    run_one "$sub" "${base}_${subtag}" deep
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
