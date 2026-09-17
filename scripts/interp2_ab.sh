#!/usr/bin/env bash
# Run one test262 slice on both execution loops and report what the new one
# changes.
#
# The whole point of building the register-window loop beside the old one rather
# than in place of it is that this comparison exists at every commit. A slice
# takes seconds, so it runs per feature, per commit - the 53k suite stays for
# milestones.
#
#   bash scripts/interp2_ab.sh language/expressions/call
#   bash scripts/interp2_ab.sh built-ins/Function 4000
#
# Exit status is 0 when the new loop passes at least as many tests as the old
# one and fails nothing the old one passed.
set -u

SLICE="${1:-}"
MAX="${2:-100000}"
if [ -z "$SLICE" ]; then
    echo "usage: bash scripts/interp2_ab.sh <path under test262/test> [max]" >&2
    exit 2
fi

ROOT="${TEST262_ROOT:-D:/test262}"
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"
OUT="Results/test262/interp2"
TAG="$(echo "$SLICE" | tr '/' '_')"

if [ ! -x "$EXE" ]; then
    echo "runner not built: dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release" >&2
    exit 2
fi

# A slice path that does not exist is not an empty run: the runner falls back to
# walking the whole tree in one process, which reached 23GB resident before it
# was noticed. Refuse the typo instead.
if [ ! -d "$ROOT/test/$SLICE" ]; then
    echo "no such slice: $ROOT/test/$SLICE" >&2
    exit 2
fi

mkdir -p "$OUT"

run() {
    # $1 = engine label, $2 = output file. FEN_JS_INTERPRETER selects the loop;
    # --timeout-ms 2000 is mandatory on every test262 invocation in this repo.
    FEN_JS_INTERPRETER="$1" "$EXE" \
        --runtime-subset --root "$ROOT" --test262 "$ROOT/test/$SLICE" \
        --max "$MAX" --timeout-ms 2000 --engine "FenJS-$1" --out "$2" >/dev/null 2>&1
}

run v1 "$OUT/${TAG}_v1.json"
run v2 "$OUT/${TAG}_v2.json"

python scripts/interp2_ab_report.py "$OUT/${TAG}_v1.json" "$OUT/${TAG}_v2.json" "$SLICE"
