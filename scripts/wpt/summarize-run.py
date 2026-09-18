#!/usr/bin/env python3
"""Summarise one wpt tool run directory: per-file harness status, subtest pass/fail counts,
and the first failing subtest messages.

    python scripts/wpt/summarize-run.py Results/wpt/categories/dom_lists [--max=N]
    python scripts/wpt/summarize-run.py            # newest run under Results/wpt*
"""
import json, sys, glob, os

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    max_msgs = 4
    for a in sys.argv[1:]:
        if a.startswith("--max="):
            max_msgs = int(a.split("=", 1)[1])
    runs = glob.glob("Results/wpt/categories/*") + glob.glob("Results/wpt_*")
    d = args[0] if args else sorted(runs, key=os.path.getmtime)[-1]
    res = {}
    for line in open(os.path.join(d, "wpt.raw.json"), encoding="utf-8"):
        j = json.loads(line)
        a = j.get("action")
        if a == "test_status":
            r = res.setdefault(j["test"], {"P": 0, "F": 0, "msgs": []})
            if j["status"] == "PASS":
                r["P"] += 1
            else:
                r["F"] += 1
                if len(r["msgs"]) < max_msgs:
                    r["msgs"].append((j.get("subtest"), str(j.get("message"))[:200]))
        elif a == "test_end":
            r = res.setdefault(j["test"], {"P": 0, "F": 0, "msgs": []})
            r["status"] = j["status"]
            r["emsg"] = str(j.get("message"))[:240]
    tp = tf = 0
    for t, r in sorted(res.items()):
        tp += r["P"]; tf += r["F"]
        extra = r.get("emsg", "") if r.get("status") != "OK" else ""
        print(f"{t} {r.get('status')} pass={r['P']} fail={r['F']} {extra}")
        for m in r["msgs"]:
            print("    ", m)
    print(f"TOTAL files={len(res)} subtests pass={tp} fail={tf}")

if __name__ == "__main__":
    main()
