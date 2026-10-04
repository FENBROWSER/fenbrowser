#!/usr/bin/env python3
"""Stage only selected hunks of a file, leaving the rest of the working tree
uncommitted.

`git add <file>` stages the whole file. When a file already carries someone
else's in-progress work, that silently sweeps their changes into an unrelated
commit. This selects hunks by the marker text they contain, builds a patch from
just those, and applies it to the index.

Usage:
    python scripts/stage_hunks.py <file> --contains "<text>" [--contains ...]
    python scripts/stage_hunks.py <file> --not-contains "<text>"
    python scripts/stage_hunks.py <file> --list
"""

import os
import subprocess
import sys
import tempfile


# The diff is handled as bytes end to end: decoding it in text mode turned the
# CRLF line endings of most files in this repo into LF, and git then refused
# the rebuilt patch ("patch does not apply").
def run(args):
    p = subprocess.run(args, capture_output=True)
    if p.returncode != 0 and p.stderr.strip():
        print(p.stderr.decode("utf-8", "replace").strip(), file=sys.stderr)
    return p.stdout


def split_hunks(diff_text):
    lines = diff_text.splitlines(keepends=True)
    header, hunks, current = [], [], None
    for line in lines:
        if line.startswith(b"@@"):
            if current is not None:
                hunks.append(current)
            current = [line]
        elif current is None:
            header.append(line)
        else:
            current.append(line)
    if current is not None:
        hunks.append(current)
    return b"".join(header), hunks


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
            added = [l.decode("utf-8", "replace").rstrip() for l in h if l.startswith(b"+") and not l.startswith(b"+++")]
            print(f"[{i}] {h[0].decode('utf-8', 'replace').strip()}")
            for a in added[:3]:
                print(f"      {a[:100]}")
            if len(added) > 3:
                print(f"      ... {len(added) - 3} more added lines")
        return

    wanted = [argv[i + 1].encode("utf-8") for i, a in enumerate(argv) if a == "--contains"]
    excluded = [argv[i + 1].encode("utf-8") for i, a in enumerate(argv) if a == "--not-contains"]
    if not wanted and not excluded:
        raise SystemExit("give at least one --contains or --not-contains <text>")

    def matches(h):
        text = b"".join(h)
        if excluded and any(e in text for e in excluded):
            return False
        return not wanted or any(w in text for w in wanted)

    keep = [h for h in hunks if matches(h)]
    if not keep:
        raise SystemExit("no hunk matched")

    patch = header + b"".join(b"".join(h) for h in keep)
    # Not .git/: inside a worktree that is a file, not a directory.
    fd, tmp = tempfile.mkstemp(prefix="stage_hunks_", suffix=".patch")
    with os.fdopen(fd, "wb") as fh:
        fh.write(patch)

    p = subprocess.run(["git", "apply", "--cached", "--unidiff-zero", tmp], capture_output=True)
    if p.returncode != 0:
        print(p.stderr.decode("utf-8", "replace").strip(), file=sys.stderr)
        raise SystemExit("failed to apply patch to index")
    os.unlink(tmp)
    print(f"staged {len(keep)} of {len(hunks)} hunk(s) from {path}")


if __name__ == "__main__":
    main()
