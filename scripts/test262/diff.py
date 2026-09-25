"""Diff two test262 result directories (or files) by test, not by count.

    python scripts/test262/diff.py Results/test262/x/before Results/test262/x/after

Each side is a directory of b_<tag>.json batch results (what run.py writes with
--out) or a single result file. Tests are matched by path, so a change that fixes
one test and breaks another is reported as both rather than netting to zero.
Exits 1 when anything regressed.
"""

import glob
import json
import os
import sys


def load(path):
    files = sorted(glob.glob(os.path.join(path, "b_*.json"))) if os.path.isdir(path) else [path]
    passed = total = 0
    failures = {}
    for file in files:
        with open(file, encoding="utf-8") as handle:
            data = json.load(handle)
        passed += data.get("passed", 0)
        total += data.get("total", 0)
        for failure in data.get("failures", []):
            name = failure.get("file") or failure.get("path") or failure.get("name") or "?"
            lines = (failure.get("details") or "").strip().splitlines()
            failures[name] = lines[0] if lines else ""
    return passed, total, failures


def main():
    if len(sys.argv) < 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    old_passed, old_total, old_failures = load(sys.argv[1])
    new_passed, new_total, new_failures = load(sys.argv[2])

    regressed = sorted(set(new_failures) - set(old_failures))
    fixed = sorted(set(old_failures) - set(new_failures))

    print(f"before {old_passed}/{old_total}   after {new_passed}/{new_total}   "
          f"delta {new_passed - old_passed:+d}")
    print(f"fixed {len(fixed)}   regressed {len(regressed)}")
    for name in fixed[:limit]:
        print(f"  + {name}")
    for name in regressed[:limit]:
        print(f"  - {name}  {new_failures[name][:140]}")

    return 1 if regressed else 0


if __name__ == "__main__":
    sys.exit(main())
