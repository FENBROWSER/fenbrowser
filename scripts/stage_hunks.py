#!/usr/bin/env python3
"""Stage only selected hunks of a file, leaving the rest of the working tree
uncommitted.

`git add <file>` stages the whole file. When a file already carries someone
else's in-progress work, that silently sweeps their changes into an unrelated
commit. This selects hunks by the marker text they contain, builds a patch from
just those, and applies it to the index.

Usage:
    python scripts/stage_hunks.py <file> --contains "<text>" [--contains ...]
    python scripts/stage_hunks.py <file> --list
"""

import subprocess
import sys


def run(args):
    p = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0 and p.stderr.strip():
        print(p.stderr.strip(), file=sys.stderr)
    return p.stdout


def split_hunks(diff_text):
    lines = diff_text.splitlines(keepends=True)
    header, hunks, current = [], [], None
    for line in lines:
        if line.startswith("@@"):
            if current is not None:
                hunks.append(current)
            current = [line]
        elif current is None:
            header.append(line)
        else:
            current.append(line)
    if current is not None:
        hunks.append(current)
    return "".join(header), hunks


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    path = sys.argv[1]
    argv = sys.argv[2:]

    diff = run(["git", "diff", "-U3", "--", path])
    if not diff.strip():
        print(f"no unstaged changes in {path}")
        return
    header, hunks = split_hunks(diff)

    if "--list" in argv:
        for i, h in enumerate(hunks):
            added = [l.rstrip() for l in h if l.startswith("+") and not l.startswith("+++")]
            print(f"[{i}] {h[0].strip()}")
            for a in added[:3]:
                print(f"      {a[:100]}")
            if len(added) > 3:
                print(f"      ... {len(added) - 3} more added lines")
        return

    wanted = [argv[i + 1] for i, a in enumerate(argv) if a == "--contains"]
    if not wanted:
        raise SystemExit("give at least one --contains <text>")

    keep = [h for h in hunks if any(w in "".join(h) for w in wanted)]
    if not keep:
        raise SystemExit("no hunk matched")

    patch = header + "".join("".join(h) for h in keep)
    tmp = ".git/stage_hunks.patch"
    with open(tmp, "w", encoding="utf-8", newline="") as fh:
        fh.write(patch)

    p = subprocess.run(["git", "apply", "--cached", "--unidiff-zero", tmp],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        print(p.stderr.strip(), file=sys.stderr)
        raise SystemExit("failed to apply patch to index")
    print(f"staged {len(keep)} of {len(hunks)} hunk(s) from {path}")


if __name__ == "__main__":
    main()
