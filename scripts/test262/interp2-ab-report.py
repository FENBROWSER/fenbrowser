"""Diff two test262 result files by test, not by count.

Two runs with the same pass total are not the same run: the new loop can fix one
test and break another and still look identical on the scoreboard. This compares
the sets, so a regression is named rather than averaged away.
"""

import json
import sys


def load(path):
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    failures = {}
    for failure in data.get("failures", []):
        name = failure.get("file") or failure.get("path") or failure.get("name") or "?"
        failures[name] = (failure.get("details") or "").strip().splitlines()[:1]
    return data.get("passed", 0), data.get("total", 0), failures


def main():
    if len(sys.argv) < 3:
        print("usage: interp2-ab-report.py <v1.json> <v2.json> [label]", file=sys.stderr)
        return 2

    label = sys.argv[3] if len(sys.argv) > 3 else ""
    old_passed, old_total, old_failures = load(sys.argv[1])
    new_passed, new_total, new_failures = load(sys.argv[2])

    regressed = sorted(set(new_failures) - set(old_failures))
    fixed = sorted(set(old_failures) - set(new_failures))

    print(f"{label}  v1 {old_passed}/{old_total}   v2 {new_passed}/{new_total}   "
          f"delta {new_passed - old_passed:+d}")
    if fixed:
        print(f"  fixed by v2 ({len(fixed)}):")
        for name in fixed[:10]:
            print(f"    + {name}")
    if regressed:
        print(f"  BROKEN by v2 ({len(regressed)}):")
        for name in regressed[:25]:
            detail = new_failures[name][0] if new_failures[name] else ""
            print(f"    - {name}  {detail[:120]}")
    if not regressed and not fixed:
        print("  identical result sets")

    return 1 if regressed else 0


if __name__ == "__main__":
    sys.exit(main())
