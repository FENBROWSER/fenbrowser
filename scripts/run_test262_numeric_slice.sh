#!/usr/bin/env bash
# Runs the test262 directories that a change to numeric value tagging can move:
# every arithmetic, bitwise, shift, increment and comparison operator, plus the
# built-ins whose indices and lengths are numbers. Small enough to run twice for
# a before/after, unlike the full suite.
#
#   bash scripts/run_test262_numeric_slice.sh <label>
#
# Writes Results/test262/numeric-slice/<label>/<tag>.json and prints a total.
set -u

LABEL="${1:-run}"
ROOT="C:/Users/udayk/Videos/test262"
EXE="./FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe"
OUT="Results/test262/numeric-slice/$LABEL"
mkdir -p "$OUT"

DIRS=(
  "language/expressions/addition"
  "language/expressions/subtraction"
  "language/expressions/multiplication"
  "language/expressions/division"
  "language/expressions/modulus"
  "language/expressions/exponentiation"
  "language/expressions/bitwise-and"
  "language/expressions/bitwise-or"
  "language/expressions/bitwise-xor"
  "language/expressions/bitwise-not"
  "language/expressions/left-shift"
  "language/expressions/right-shift"
  "language/expressions/unsigned-right-shift"
  "language/expressions/postfix-increment"
  "language/expressions/postfix-decrement"
  "language/expressions/prefix-increment"
  "language/expressions/prefix-decrement"
  "language/expressions/unary-minus"
  "language/expressions/unary-plus"
  "language/expressions/equals"
  "language/expressions/does-not-equals"
  "language/expressions/strict-equals"
  "language/expressions/strict-does-not-equals"
  "language/expressions/less-than"
  "language/expressions/greater-than"
  "language/expressions/less-than-or-equal"
  "language/expressions/greater-than-or-equal"
  "language/expressions/compound-assignment"
  "language/types/number"
  "built-ins/Number"
  "built-ins/Math"
  "built-ins/parseInt"
  "built-ins/parseFloat"
  "built-ins/JSON"
  "built-ins/Array"
  "built-ins/TypedArray"
  "built-ins/DataView"
)

for dir in "${DIRS[@]}"; do
  tag="$(echo "$dir" | tr '/' '_')"
  "$EXE" --runtime-subset --root "$ROOT" --test262 "$ROOT/test/$dir" \
         --max 100000 --timeout-ms 2000 --out "$OUT/$tag.json" > "$OUT/$tag.log" 2>&1
  printf '.'
done
printf '\n'

python - "$OUT" "$LABEL" <<'PY'
import json, glob, os, sys
out, label = sys.argv[1], sys.argv[2]
passed = total = 0
for f in sorted(glob.glob(os.path.join(out, "*.json"))):
    try:
        d = json.load(open(f, encoding="utf-8-sig"))
    except Exception:
        continue
    p, t = d.get("passed", 0), d.get("total", 0)
    passed += p; total += t
    print(f"  {os.path.basename(f)[:-5]:<52} {p:>6}/{t:<6}")
pct = (100.0 * passed / total) if total else 0.0
print(f"{label}: {passed}/{total} = {pct:.3f}%")
PY
