"""Inclusive CPU time per frame on the busiest thread(s) of a speedscope profile.

Leaf self-time lies for FenJS (see profile_engine_self.py); inclusive time over
the sound part of the stack does not. Each frame is charged once per sample.

Usage: python scripts/profile_inclusive.py <speedscope.json> [top] [filter] [thread-substr] [under-frame]
  filter: only count frames whose name contains this (default "FenBrowser")
"""
import collections
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 40
flt = sys.argv[3] if len(sys.argv) > 3 else "FenBrowser"
want_thread = sys.argv[4] if len(sys.argv) > 4 else None
under = sys.argv[5] if len(sys.argv) > 5 else None  # only samples whose stack has a frame containing this
names = [f["name"] for f in data["shared"]["frames"]]


def short(name):
    return name.split("(")[0].split("!")[-1].replace("FenBrowser.", "")[:110]


per_thread = collections.Counter()
incl = collections.defaultdict(collections.Counter)
for profile in data["profiles"]:
    thread = profile.get("name", "?")
    stack, previous = [], profile["startValue"]
    for event in profile["events"]:
        duration = event["at"] - previous
        if stack and duration > 0 and names[stack[-1]] == "CPU_TIME" and (
                under is None or any(under in names[i] for i in stack)):
            per_thread[thread] += duration
            seen = set()
            for i in stack:
                n = names[i]
                if "!" in n and flt in n and n not in seen:
                    seen.add(n)
                    incl[thread][short(n)] += duration
        if event["type"] == "O":
            stack.append(event["frame"])
        else:
            stack.pop()
        previous = event["at"]

threads = [t for t, _ in per_thread.most_common()]
if want_thread:
    threads = [t for t in threads if want_thread in t]
print("busiest threads:", [(t, round(v)) for t, v in per_thread.most_common(5)])
for t in threads[:int(__import__("os").environ.get("NTHREADS","1"))]:
    total = per_thread[t]
    print(f"\n== {t}: {total:.0f} ms CPU")
    for name, v in incl[t].most_common(top):
        print(f"{v:9.0f} {100 * v / total:5.1f}%  {name}")
