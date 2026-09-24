#!/usr/bin/env python3
"""WPT front-end: drives upstream wptrunner against FenBrowser through
`FenBrowser.Tooling wpt` (WebDriver-backed, stall watchdog built in).

    python scripts/wpt/run.py category dom/lists [dom/ranges ...]   # one Tooling process per category
    python scripts/wpt/run.py sweep                                  # every top-level WPT dir, resumable
    python scripts/wpt/run.py sweep --categories dom,html,url --fresh
    python scripts/wpt/run.py list                                   # top-level dirs and how many tests each has
    python scripts/wpt/run.py status                                 # summarise Results/wpt/categories

Each category runs as its own short-lived process and lands in
Results/wpt/categories/<tag>/ (wpt.summary.json, wpt.raw.json, wpt.failures.json,
logs). A category with a valid wpt.summary.json is skipped on the next sweep so an
interrupted run continues; --fresh wipes it. scripts/wpt/report.py turns the
category store into docs/wpt_results.md; scripts/wpt/summarize-run.py digs into
one run directory.

Prerequisites (one-off): build FenBrowser.Tooling (Release) and pip-install the
product plugin into the WPT venv — see tools/wptrunner-fenbrowser/README.md.

Environment: WPT_ROOT (checkout, default D:/wpt), FEN_WPT_TOOLING_EXE.
Everything after --tooling-args is passed to `FenBrowser.Tooling wpt` verbatim.
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STORE = os.path.join("Results", "wpt", "categories")
LAUNCHER = os.path.join(REPO, "scripts", "wpt", "webdriver-launcher.cmd")

# Top-level WPT directories that hold no tests: shared resources, fonts, media
# files, IDL, tooling.
SKIP_DIRS = {
    "common", "conformance-checkers", "docs", "fonts", "images", "interfaces", "media",
    "resources", "tools", "_venv3",
}

# Test suites the default sweep cannot run, and why. Say so rather than drop them.
UNSWEPT_SUITES = {
    "webdriver": "wdspec tests need webdriver.client in the WPT venv; run "
                 "`category webdriver --suite webdriver` once it is installed",
}

# Directories too large for one category run; the sweep runs each subdirectory
# (and the directory's own loose files) separately.
SPLIT_DIRS = {"css"}


def find_root(explicit):
    for cand in (explicit, os.environ.get("WPT_ROOT"), "D:/wpt",
                 os.path.join(REPO, "..", "wpt"), os.path.join(REPO, "wpt")):
        if cand and os.path.isfile(os.path.join(cand, "wpt")):
            return os.path.abspath(cand)
    sys.exit("WPT checkout not found: set WPT_ROOT (expected D:/wpt)")


def find_tooling(explicit, build):
    proj = os.path.join(REPO, "FenBrowser.Tooling", "FenBrowser.Tooling.csproj")
    if build:
        subprocess.run(["dotnet", "build", proj, "-c", "Release", "--nologo", "-v", "q"], check=True)
    exe = "FenBrowser.Tooling" + (".exe" if os.name == "nt" else "")
    for cand in (explicit, os.environ.get("FEN_WPT_TOOLING_EXE"),
                 os.path.join(REPO, "FenBrowser.Tooling", "bin", "Release", "net10.0", exe),
                 os.path.join(REPO, "FenBrowser.Tooling", "bin", "Debug", "net10.0", exe)):
        if cand and os.path.isfile(cand):
            return os.path.abspath(cand)
    sys.exit("FenBrowser.Tooling not built: dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release "
             "(or pass --build)")


def tag_for(category):
    return category.strip("/").replace("/", "_")


def load_summary(outdir):
    try:
        with open(os.path.join(outdir, "wpt.summary.json"), encoding="utf-8-sig") as fh:
            d = json.load(fh)
        return d if "TestEnd" in d or "testEnd" in d else None
    except Exception:
        return None


def get(d, key):
    return d.get(key, d.get(key[0].lower() + key[1:]))


def is_complete(d):
    """A category run counts as done only when it started tests, finished every
    one it started, and neither timed out nor stalled. wptrunner exits 1 when
    results were unexpected (without expectation metadata: any failure), so 0
    and 1 are both finished runs; any other exit code is not. Anything else is
    incomplete: shown as such and rerun by the next sweep."""
    if not d:
        return False
    started = get(d, "TestStart") or 0
    ended = get(d, "TestEnd") or 0
    return (started > 0 and ended >= started and not get(d, "TimedOut") and not get(d, "Stalled")
            and (get(d, "ExitCode") or 0) in (0, 1))


def summarize(d):
    if not d:
        return "no summary"
    counts = get(d, "StatusCounts") or {}
    ok = counts.get("OK", 0) + counts.get("PASS", 0)
    ended = get(d, "TestEnd") or 0
    tail = ""
    if get(d, "Stalled"):
        tail = "  STALLED"
    elif get(d, "TimedOut"):
        tail = "  TIMED-OUT"
    elif not is_complete(d):
        tail = f"  INCOMPLETE (exit {get(d, 'ExitCode')}, {get(d, 'TestEnd') or 0}/{get(d, 'TestStart') or 0} finished)"
    subtests = get(d, "UnexpectedSubtestFailures") or 0
    return (f"files {ok}/{ended} harness-OK  {' '.join(f'{k}={v}' for k, v in sorted(counts.items()))}"
            f"  failing-subtests={subtests}{tail}")


def run_category(tooling, root, category, outdir, a):
    os.makedirs(outdir, exist_ok=True)
    for stale in glob.glob(os.path.join(outdir, "wpt.*")):
        os.remove(stale)
    args = [tooling, "wpt", "--root", root, "--tests", category, "--output-dir", outdir,
            "--processes", str(a.processes), "--timeout-seconds", str(a.timeout),
            "--stall-timeout-seconds", str(a.stall), "--max-restarts", str(a.max_restarts),
            "--webdriver-binary", LAUNCHER, "--skip-venv-setup"]
    if a.suite:
        args += ["--suite", a.suite]
    args += a.tooling_args
    # Belt and braces around Tooling's own watchdog: if the whole category
    # exceeds its wall budget (a wedged wptrunner never returns), kill it.
    wall = a.wall if a.wall else None
    with open(os.path.join(outdir, "driver.log"), "w", encoding="utf-8") as log:
        started = time.time()
        try:
            proc = subprocess.run(args, stdout=log, stderr=subprocess.STDOUT, timeout=wall)
            rc = proc.returncode
        except subprocess.TimeoutExpired:
            rc = -1
            kill_engine_processes()
    return rc, time.time() - started


def kill_engine_processes():
    """A wedged category can leave the engine and wptrunner's python alive."""
    if os.name != "nt":
        return
    for name in ("FenBrowser.Host.exe", "FenBrowser.Tooling.exe"):
        subprocess.run(["taskkill", "/IM", name, "/F", "/T"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def top_level_categories(root):
    cats = []
    for name in sorted(os.listdir(root)):
        full = os.path.join(root, name)
        if name.startswith(".") or name in SKIP_DIRS or name in UNSWEPT_SUITES or not os.path.isdir(full):
            continue
        if name in SPLIT_DIRS:
            cats += [f"{name}/{sub}" for sub in sorted(os.listdir(full))
                     if os.path.isdir(os.path.join(full, sub)) and not sub.startswith(".")
                     and sub not in ("support", "resources", "tools", "reference", "common")]
            continue
        cats.append(name)
    return cats


def count_tests(root, category):
    n = 0
    for _, dirs, files in os.walk(os.path.join(root, category)):
        dirs[:] = [d for d in dirs if d not in ("resources", "support", "tools")]
        n += sum(1 for f in files if f.endswith((".html", ".htm", ".xhtml", ".any.js", ".window.js", ".worker.js")))
    return n


# --- subcommands ------------------------------------------------------------

def cmd_category(a, tooling, root):
    rc_all = 0
    for category in a.categories:
        if not os.path.isdir(os.path.join(root, category.strip("/"))):
            sys.exit(f"not a directory under {root}: {category}")
        outdir = os.path.join(a.out, tag_for(category))
        print(f"{category}: {count_tests(root, category)} test files  ->  {outdir}", flush=True)
        rc, secs = run_category(tooling, root, category, outdir, a)
        d = load_summary(outdir)
        print(f"  -> {summarize(d)}  ({secs:.0f}s, exit {rc})")
        if not is_complete(d):
            rc_all = 1
    if not a.no_report and a.out == STORE:
        subprocess.run([sys.executable, os.path.join(REPO, "scripts", "wpt", "report.py")], check=False)
    return rc_all


def cmd_sweep(a, tooling, root):
    cats = [c.strip() for c in a.categories.split(",")] if a.categories else top_level_categories(root)
    os.makedirs(a.out, exist_ok=True)
    print(f"{len(cats)} categories  processes {a.processes}  per-test {a.timeout}s  stall {a.stall}s  ->  {a.out}")
    if not a.categories:
        for suite, why in UNSWEPT_SUITES.items():
            print(f"  not swept: {suite} - {why}")
    failed = []
    for i, category in enumerate(cats, 1):
        outdir = os.path.join(a.out, tag_for(category))
        cached = None if a.fresh else load_summary(outdir)
        if is_complete(cached):
            print(f"[{i:3}/{len(cats)}] {category:<36} SKIP  {summarize(cached)} (cached)")
            continue
        print(f"[{i:3}/{len(cats)}] {category:<36} run ...", flush=True)
        rc, secs = run_category(tooling, root, category, outdir, a)
        d = load_summary(outdir)
        print(f"[{i:3}/{len(cats)}] {category:<36} {summarize(d)}  ({secs:.0f}s)")
        if not is_complete(d):
            failed.append(category)
    if failed:
        print("incomplete (rerun with `category <name>`): " + ", ".join(failed))
    if not a.no_report and a.out == STORE:
        subprocess.run([sys.executable, os.path.join(REPO, "scripts", "wpt", "report.py")], check=False)
    return 0


def cmd_list(a, tooling=None, root=None):
    root = find_root(a.root)
    for c in top_level_categories(root):
        print(f"{count_tests(root, c):6}  {c}")
    return 0


def cmd_status(a, tooling=None, root=None):
    rows = []
    for outdir in sorted(glob.glob(os.path.join(a.out, "*"))):
        d = load_summary(outdir)
        if d:
            rows.append((os.path.basename(outdir), summarize(d)))
    if not rows:
        print(f"no results in {a.out}; run: python scripts/wpt/run.py sweep")
        return 0
    for tag, s in rows:
        print(f"  {tag:<36} {s}")
    incomplete = sum(1 for _, s in rows if "INCOMPLETE" in s or "STALLED" in s or "TIMED-OUT" in s)
    if incomplete:
        print(f"{incomplete} of {len(rows)} categories are incomplete")
    return 0


def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--root", help="WPT checkout (default: WPT_ROOT or D:/wpt)")
    common.add_argument("--out", default=STORE, help="category result store")
    run_opts = argparse.ArgumentParser(add_help=False)
    run_opts.add_argument("--tooling", help="FenBrowser.Tooling binary")
    run_opts.add_argument("--build", action="store_true", help="dotnet build FenBrowser.Tooling first")
    run_opts.add_argument("--processes", type=int, default=2,
                          help="parallel browser instances (keep <= 4: the engine wedges under high parallelism)")
    run_opts.add_argument("--timeout", type=int, default=300, help="wptrunner per-test timeout budget in seconds")
    run_opts.add_argument("--stall", type=int, default=90, help="kill the category when its raw log is idle this long")
    run_opts.add_argument("--wall", type=int, default=0, help="hard wall-clock cap per category in seconds (0 = none)")
    run_opts.add_argument("--max-restarts", type=int, default=2)
    run_opts.add_argument("--suite", choices=["normal", "workers", "webdriver", "all"], default=None)
    run_opts.add_argument("--no-report", action="store_true", help="don't regenerate docs/wpt_results.md")
    run_opts.add_argument("--tooling-args", nargs=argparse.REMAINDER, default=[])
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("category", parents=[common, run_opts], help="run one or more categories (paths under the WPT root)")
    s.add_argument("categories", nargs="+")
    s.set_defaults(fn=cmd_category)
    s = sub.add_parser("sweep", parents=[common, run_opts], help="every top-level directory, resumable")
    s.add_argument("--categories", help="comma-separated subset instead of every top-level dir")
    s.add_argument("--fresh", action="store_true", help="rerun categories that already have results")
    s.set_defaults(fn=cmd_sweep)
    s = sub.add_parser("list", parents=[common], help="top-level directories with test-file counts")
    s.set_defaults(fn=cmd_list)
    s = sub.add_parser("status", parents=[common], help="summarise the category store")
    s.set_defaults(fn=cmd_status)

    a = p.parse_args(argv)
    os.chdir(REPO)
    if a.cmd in ("list", "status"):
        return a.fn(a)
    root = find_root(a.root)
    tooling = find_tooling(a.tooling, a.build)
    return a.fn(a, tooling, root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
