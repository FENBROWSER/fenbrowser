#!/usr/bin/env bash
# Time a cold browser start to its first committed frame.
#
# "New tab takes five seconds" needed a number attached to each stage before it
# could be argued about. This launches the host, waits for the first frame, and
# prints the startup timeline from the host's own log.
#
#   bash scripts/measure_startup.sh [url] [runs]
#
# Environment passed through, so a configuration can be compared against the
# default, e.g.:
#   FEN_RENDERER_POOL_PREWARM=1 FEN_RENDERER_POOL_WARM_TARGET=1 \
#     bash scripts/measure_startup.sh fen://newtab 3

set -u
URL="${1:-fen://newtab}"
RUNS="${2:-3}"
EXE="./FenBrowser.Host/bin/Release/net10.0/FenBrowser.Host.exe"
OUT="${TMPDIR:-/tmp}/fen_startup"
mkdir -p "$OUT"

[ -x "$EXE" ] || { echo "build FenBrowser.Host first: $EXE not found" >&2; exit 1; }

for i in $(seq 1 "$RUNS"); do
  LOG="$OUT/run$i.log"
  rm -f "$LOG"
  "$EXE" "$URL" --windowed > "$LOG" 2>&1 &
  PID=$!
  for _ in $(seq 1 30); do
    grep -q "RemoteCommit" "$LOG" 2>/dev/null && break
    sleep 1
  done
  sleep 1
  kill "$PID" 2>/dev/null
  sleep 1
  taskkill //F //IM FenBrowser.Host.exe >/dev/null 2>&1
  sleep 1
done

python - "$OUT" "$RUNS" <<'PY'
import re, sys, os
from datetime import datetime

out, runs = sys.argv[1], int(sys.argv[2])
MARKS = [
    ("window/GL/Skia",      "Window loaded"),
    ("network child",       "Network process reported ready"),
    ("gpu child",           "GpuProcess] Target process reported ready"),
    ("utility child",       "UtilityProcess] Target process reported ready"),
    ("bootstrap frame",     "Bootstrap frame presented"),
    ("initial tab",         "Creating initial tab"),
    ("renderer child",      "Renderer child ready"),
    ("navigation starts",   "NavigationRequested"),
    ("frame buffer",        "FrameSharedMemory] Writer created"),
    ("FIRST FRAME",         "RemoteCommit"),
]

totals = {name: [] for name, _ in MARKS}
first_frames = []
for i in range(1, runs + 1):
    path = os.path.join(out, f"run{i}.log")
    if not os.path.exists(path):
        continue
    rows = []
    for line in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"^(\d\d:\d\d:\d\d\.\d+) \[\w+\]\[\w+\] (.*)$", line.rstrip())
        if m:
            rows.append((datetime.strptime(m.group(1), "%H:%M:%S.%f"), m.group(2)))
    if not rows:
        continue
    t0 = rows[0][0]
    for name, pat in MARKS:
        for t, msg in rows:
            if pat in msg:
                totals[name].append((t - t0).total_seconds() * 1000)
                break

print(f"{'stage':<20} {'median ms':>10} {'runs':>5}")
print("-" * 38)
for name, _ in MARKS:
    v = sorted(totals[name])
    if not v:
        print(f"{name:<20} {'--':>10} {0:>5}")
        continue
    print(f"{name:<20} {v[len(v)//2]:>10.0f} {len(v):>5}")
PY
