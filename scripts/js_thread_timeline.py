"""Timeline of the FenJS worker thread for one debug-site bundle or log run.

Reads the `[FenJsWorker] Started/Completed id=N kind=K ... elapsedMs=M` pairs and
prints the busiest stretch: every task over a threshold, with when it started
relative to the first task, plus totals per kind and how much of the load window
the thread was busy. A page "frozen for 20 s" is this thread never idle.

Usage: python scripts/js_thread_timeline.py <bundle-dir|trace.jsonl> [min_ms=100]
"""
import collections
import datetime
import json
import os
import re
import sys

path = sys.argv[1]
min_ms = float(sys.argv[2]) if len(sys.argv) > 2 else 100.0
if os.path.isdir(path):
    path = os.path.join(path, "trace.jsonl")

started = {}
done = []
pat = re.compile(r"\[FenJsWorker\] (Started|Completed) id=(\d+) kind=(\S+).*?(?:queuedMs=(\d+))?.*?(?:elapsedMs=(\d+))?")
for line in open(path, encoding="utf-8-sig"):
    if "[FenJsWorker]" not in line:
        continue
    try:
        rec = json.loads(line)
    except json.JSONDecodeError:
        continue
    msg = rec.get("message") or rec.get("msg") or ""
    m = re.search(r"\[FenJsWorker\] (Started|Completed) id=(\d+) kind=(\S+)", msg)
    if not m:
        continue
    ts = rec.get("timestamp") or rec.get("ts") or rec.get("time")
    t = datetime.datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp() if isinstance(ts, str) else float(ts)
    if m.group(1) == "Started":
        q = re.search(r"queuedMs=(\d+)", msg)
        started[m.group(2)] = (t, m.group(3), int(q.group(1)) if q else 0)
    else:
        e = re.search(r"elapsedMs=(\d+)", msg)
        s = started.pop(m.group(2), None)
        if s and e:
            done.append((s[0], int(e.group(1)), m.group(3), s[2], m.group(2)))

if not done:
    sys.exit("no FenJsWorker task pairs found")
done.sort()
t0 = done[0][0]
end = max(s + ms / 1000 for s, ms, *_ in done)
busy = sum(ms for _, ms, *_ in done)
by_kind = collections.Counter()
for _, ms, kind, *_ in done:
    by_kind[kind] += ms
print(f"tasks={len(done)} window={end - t0:.1f}s busy={busy / 1000:.1f}s ({100 * busy / 1000 / max(end - t0, 1e-9):.0f}%)")
print("busy by kind:", ", ".join(f"{k}={v / 1000:.1f}s" for k, v in by_kind.most_common(8)))
print(f"\ntasks >= {min_ms:.0f} ms (start offset, ms, kind, queued ms, id):")
for s, ms, kind, q, tid in done:
    if ms >= min_ms:
        print(f"  +{s - t0:7.2f}s {ms:6d}ms {kind:32s} queued={q}ms id={tid}")

# Longest stretch with no idle gap over 50 ms - what an input event would wait behind.
best = (0, 0, 0)
cur_start, cur_end = done[0][0], done[0][0] + done[0][1] / 1000
for s, ms, *_ in done[1:]:
    if s - cur_end > 0.05:
        if cur_end - cur_start > best[0]:
            best = (cur_end - cur_start, cur_start, cur_end)
        cur_start = s
    cur_end = max(cur_end, s + ms / 1000)
if cur_end - cur_start > best[0]:
    best = (cur_end - cur_start, cur_start, cur_end)
print(f"\nlongest run without a 50 ms idle gap: {best[0]:.1f}s (+{best[1] - t0:.1f}s .. +{best[2] - t0:.1f}s)")
