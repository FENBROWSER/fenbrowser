"""Where engine time goes, ignoring the runtime frame a sample happens to land on.

The sampled profile's leaf is not trustworthy here: .NET inlines Thread.PollGC
at nearly every safepoint, so unwalkable addresses resolve into PollGCWorker and
a naive self-time reading blames it for half the run. The stacks below that leaf
are sound, so each sample is charged to the deepest FenBrowser frame on it.

Usage: python scripts/profile_engine_self.py <speedscope.json> [top]
"""
import collections
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
top = int(sys.argv[2]) if len(sys.argv) > 2 else 22
names = [f["name"] for f in data["shared"]["frames"]]
SYNTH = ("CPU_TIME", "UNMANAGED_CODE_TIME")

def short(name):
    return name.split("(")[0].split("!")[-1].replace("FenBrowser.Js.", "")

own = collections.Counter()
per_thread = collections.Counter()
for profile in data["profiles"]:
    thread = profile.get("name", "?")
    stack, previous = [], profile["startValue"]
    for event in profile["events"]:
        duration = event["at"] - previous
        if stack and duration > 0 and names[stack[-1]] == "CPU_TIME":
            real = [names[i] for i in stack if names[i] not in SYNTH]
            if real:
                per_thread[thread] += duration
                deepest = next((n for n in reversed(real) if "FenBrowser" in n), None)
                own[short(deepest) if deepest else "(runtime only)"] += duration
        if event["type"] == "O":
            stack.append(event["frame"])
        else:
            stack.pop()
        previous = event["at"]

total = sum(own.values())
print(f"threads: {[(t, round(v)) for t, v in per_thread.most_common(4)]}")
print(f"attributed {total:.0f} ms")
for name, value in own.most_common(top):
    print(f"{value:9.1f} {100 * value / total:5.1f}%  {name[:95]}")
