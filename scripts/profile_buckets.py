"""Splits one thread's CPU samples into engine subsystems.

Each sample goes to the first bucket whose marker appears anywhere on its stack,
checked in priority order (GC and layout win over the interpreter frames that
called into them), so the buckets add up to the thread total.

Usage: python scripts/profile_buckets.py <speedscope.json> [thread-substr] [under-frame]
"""
import collections
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
want = sys.argv[2] if len(sys.argv) > 2 else None
under = sys.argv[3] if len(sys.argv) > 3 else None
names = [f["name"] for f in data["shared"]["frames"]]

BUCKETS = [
    ("gc", ("JsHeap.MinorCollect", "JsHeap.MajorCollect", "JsHeap.Collect", "JsHeap.RunAutomaticCollection")),
    ("compile", ("BytecodeCompiler.", "JsParser.", "JsLexer.")),
    ("cascade", ("CascadeEngine", "CssLoader", "ParallelCascadeScheduler", "Rendering.Css.")),
    ("layout", ("FenEngine.Layout.", "LayoutEngine", "SkiaDomRenderer.EnsureLayout")),
    ("selectors", ("Dom.V2.Selectors",)),
    ("dom-core", ("FenBrowser.Core.Dom",)),
    ("host-bindings", ("FenEngine.Scripting.",)),
    ("builtins", ("FenBrowser.Js.Builtins", "FenBrowser.Js.Regex", "FenBrowser.Js.Intl")),
    ("interpreter", ("FenBrowser.Js.",)),
]

threads = collections.Counter()
per = collections.defaultdict(collections.Counter)
for profile in data["profiles"]:
    thread = profile.get("name", "?")
    stack, previous = [], profile["startValue"]
    for event in profile["events"]:
        duration = event["at"] - previous
        if stack and duration > 0 and names[stack[-1]] == "CPU_TIME":
            frames = [names[i] for i in stack]
            if under is None or any(under in f for f in frames):
                threads[thread] += duration
                bucket = "other"
                # GC, compile, cascade and layout own everything beneath them;
                # otherwise the deepest classifiable frame says what the sample is
                # doing (a DOM call made by a binding made by the interpreter is DOM).
                for label, markers in BUCKETS[:4]:
                    if any(m in f for f in frames for m in markers):
                        bucket = label
                        break
                else:
                    for f in reversed(frames):
                        hit = next((label for label, markers in BUCKETS[4:] if any(m in f for m in markers)), None)
                        if hit:
                            bucket = hit
                            break
                per[thread][bucket] += duration
        if event["type"] == "O":
            stack.append(event["frame"])
        else:
            stack.pop()
        previous = event["at"]

candidates = [t for t, _ in threads.most_common() if want is None or want in t]
for t in candidates[:1]:
    total = threads[t]
    print(f"{t}: {total:.0f} ms")
    for label, v in per[t].most_common():
        print(f"  {label:14s} {v:8.0f} ms {100 * v / total:5.1f}%")
