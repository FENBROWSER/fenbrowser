#!/usr/bin/env bash
# Re-run ONE test262 category into the batched result store, then refresh
# docs/test262_results.md. Use this after fixing a category instead of
# re-running the full suite.
#
#   bash scripts/rerun-test262-category.sh built-ins/Object
#   bash scripts/rerun-test262-category.sh language/statements/for-of
#
# Arg is a path relative to <test262>/test/. Includes the stall-kill watchdog.
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
STALL=${STALL_TIMEOUT_SEC:-30}

[ $# -ge 1 ] || { echo "usage: $0 <category-path under test/>  e.g. built-ins/Object" >&2; exit 2; }
[ -f "$EXE" ] || { echo "Runner not found: $EXE (build FenBrowser.Js.Test262 -c Release)" >&2; exit 1; }

sub="${1#test/}"; sub="${sub%/}"
scope="$ROOT/test/$sub"
[ -d "$scope" ] || { echo "Not a directory: $scope" >&2; exit 1; }
tag=$(echo "$sub" | sed 's#/#_#g')
out="$OUTDIR/b_${tag}.json"; log="$OUTDIR/b_${tag}.log"
mkdir -p "$OUTDIR"; : > "$log"

# Count tests and compute hard timeout: N×2s + 30s buffer
N=$(find "$scope" -name '*.js' -type f 2>/dev/null | wc -l)
HARD=$(( N * 2 + 30 ))
echo "rerun: $sub ($N tests, hard_timeout=${HARD}s)  ->  $out"
timeout "$HARD" "$EXE" --runtime-subset --root "$ROOT" --test262 "$scope" --max 100000 --timeout-ms 2000 --out "$out" >"$log" 2>&1 &
pid=$!; winpid=$(cat "/proc/$pid/winpid" 2>/dev/null || echo "")
while kill -0 "$pid" 2>/dev/null; do
  sleep 2
  mt=$(stat -c %Y "$log" 2>/dev/null || echo 0)
  if [ $(( $(date +%s) - mt )) -gt "$STALL" ]; then
    echo "STALL-KILL: wedged on a test (see $log)"; [ -n "$winpid" ] && taskkill //PID "$winpid" //F //T >/dev/null 2>&1; kill -9 "$pid" 2>/dev/null; break
  fi
done
wait "$pid" 2>/dev/null
python -c "import json;d=json.load(open(r'$out',encoding='utf-8-sig'));print('  ->',d['passed'],'/',d['total'],'=',round(100*d['passed']/d['total'],1) if d['total'] else 0,'%')" 2>/dev/null || echo "  (no result json — check $log)"
python scripts/test262_report.py
