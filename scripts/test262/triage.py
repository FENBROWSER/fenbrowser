#!/usr/bin/env python3
"""Cluster the failures of one test262 result JSON to find the shared root cause.

    python scripts/test262/triage.py built-ins/Object            # category tag or b_*.json path
    python scripts/test262/triage.py built-ins/Temporal --by 1   # group by the Nth path segment under the category
    python scripts/test262/triage.py built-ins/Temporal --grep "RangeError" --limit 40
    python scripts/test262/triage.py Results/test262/single.json --files

Reads failures[].details (the real thrown message — not tests[].message),
normalises numbers/quotes out of it, and prints the biggest clusters first.
"""
import argparse
import collections
import glob
import json
import os
import re
import sys

STORE = os.path.join("Results", "test262", "batched")


def locate(arg):
    if os.path.isfile(arg):
        return arg
    tag = arg.replace("\\", "/").strip("/").removeprefix("test/").replace("/", "_")
    cand = os.path.join(STORE, f"b_{tag}.json")
    if os.path.isfile(cand):
        return cand
    hits = glob.glob(os.path.join(STORE, f"b_{tag}*.json"))
    if hits:
        return hits[0]
    sys.exit(f"no result for {arg!r} (looked for {cand}); run: python scripts/test262/run.py category {arg}")


_NORMALISE = [
    (re.compile(r"'[^']*'"), "'_'"),
    (re.compile(r'"[^"]*"'), '"_"'),
    (re.compile(r"\b\d+(\.\d+)?\b"), "N"),
    (re.compile(r"\s+"), " "),
]


def normalise(msg):
    msg = (msg or "").strip()
    for rx, rep in _NORMALISE:
        msg = rx.sub(rep, msg)
    return msg[:160]


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("target", help="category path (built-ins/Object) or a result JSON")
    p.add_argument("--by", type=int, default=0,
                   help="also group by path segment N under the category (1 = first subdir)")
    p.add_argument("--grep", help="only failures whose details contain this text")
    p.add_argument("--limit", type=int, default=25, help="clusters / files to print")
    p.add_argument("--files", action="store_true", help="list the failing files instead of clusters")
    p.add_argument("--raw", action="store_true", help="don't normalise messages before clustering")
    a = p.parse_args(argv)

    path = locate(a.target)
    with open(path, encoding="utf-8-sig") as fh:
        d = json.load(fh)
    fails = d.get("failures", [])
    if a.grep:
        fails = [f for f in fails if a.grep in (f.get("details") or f.get("message") or "")]
    print(f"{path}: total={d.get('total')} passed={d.get('passed')} failures={len(fails)}"
          + (f" (matching {a.grep!r})" if a.grep else ""))

    def detail(f):
        return (f.get("details") or f.get("message") or "").strip().replace("\n", " ")

    if a.files:
        for f in fails[:a.limit]:
            print(f"  {f.get('relativePath')} :: {detail(f)[:140]}")
        return 0

    by_class = collections.Counter(f.get("classification", "?") for f in fails)
    print("--- by classification ---")
    for k, v in by_class.most_common():
        print(f"  {v:5}  {k}")

    if a.by:
        seg = collections.Counter()
        for f in fails:
            parts = (f.get("relativePath") or "").split("/")
            # relativePath is test/<top>/<category>/<...>; segment 1 is the first subdir under the category
            idx = 3 + a.by - 1
            seg[parts[idx] if len(parts) > idx + 1 else "(top-level)"] += 1
        print(f"--- by path segment {a.by} ---")
        for k, v in seg.most_common(a.limit):
            print(f"  {v:5}  {k}")

    clusters = collections.defaultdict(list)
    for f in fails:
        key = detail(f) if a.raw else normalise(detail(f))
        clusters[key].append(f.get("relativePath"))
    print(f"--- top {min(a.limit, len(clusters))} of {len(clusters)} message clusters ---")
    for key, files in sorted(clusters.items(), key=lambda kv: -len(kv[1]))[:a.limit]:
        print(f"  {len(files):5}  {key or '(no message)'}")
        print(f"         e.g. {files[0]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
