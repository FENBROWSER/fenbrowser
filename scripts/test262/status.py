#!/usr/bin/env python
"""test262 status — compact per-category summary of the batched store.

    python scripts/test262/status.py      (or: python scripts/test262/run.py status)
"""
import json, glob, os, sys

os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

BATCHED = "Results/test262/batched"


def main():
    cats = []
    for f in sorted(glob.glob(f"{BATCHED}/b_*.json")):
        try:
            with open(f, encoding="utf-8-sig") as fp:
                d = json.load(fp)
            t = d.get("total", 0)
            if t == 0:
                continue
            p = d["passed"]
            tag = os.path.basename(f).replace("b_", "").replace(".json", "")
            cats.append((tag, p, t))
        except Exception:
            pass

    cats.sort(key=lambda x: x[2] - x[1], reverse=True)  # by failures descending

    total_p = sum(c[1] for c in cats)
    total_t = sum(c[2] for c in cats)
    total_f = total_t - total_p
    if total_t == 0:
        print(f"no results in {BATCHED}; run: python scripts/test262/run.py full")
        return

    # Compact header
    print(f"test262: {total_p}/{total_t} = {total_p/total_t*100:.2f}%  ({total_f} fail)")
    print()

    # Show only categories with failures (worst first)
    width = 55 if sys.stdout.isatty() else 45
    for tag, p, t in cats:
        f = t - p
        if f == 0:
            continue
        pct = p / t * 100
        bar = "#" * max(1, f // 10) if f > 0 else ""
        print(f"  {tag:<{width}} {pct:5.1f}%  [{f:4d} fail]  {bar}")

    # Summary footer
    perfect = sum(1 for _, p, t in cats if t - p == 0)
    above95 = sum(1 for _, p, t in cats if t > 0 and (p/t*100) >= 95)
    print(f"\n  {perfect} categories at 100%  |  {above95} at >=95%  |  {len(cats)} total")

if __name__ == "__main__":
    main()
