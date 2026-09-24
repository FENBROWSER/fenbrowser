#!/usr/bin/env python3
"""test262 front-end for FenBrowser.Js.Test262 — the one way to run the suite.

    python scripts/test262/run.py full      [--fresh] [--split-on-stall]
    python scripts/test262/run.py category  <path>... [--no-report] [--out DIR]
    python scripts/test262/run.py file      <test.js>
    python scripts/test262/run.py slice     <name> --label <label>
    python scripts/test262/run.py status

Every invocation passes --timeout-ms 2000 (mandatory per-test budget) and runs
under a stall watchdog: the runner prints a progress line at least every 5s, so
a log that stops growing for --stall seconds means a test is wedged in native
code (the cooperative timeout cannot interrupt it) and the process tree is
killed. Each directory batch is its own short-lived process so RAM is released
between batches.

Paths are relative to the repo root (run from there). Result JSONs land in the
batched store Results/test262/batched/b_<tag>.json, which is what
scripts/test262/report.py turns into docs/test262_results.md.

Environment: TEST262_ROOT (checkout), TEST262_EXE (runner binary),
STALL_TIMEOUT_SEC, FEN_JS_INTERPRETER etc. pass through to the runner.
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STORE = os.path.join("Results", "test262", "batched")
TIMEOUT_MS = 2000  # mandatory; never raise this
HARD_BUFFER_SEC = 30

# Directory slices small enough to run before and after a change (see `slice`).
SLICES = {
    # Everything a change to numeric value tagging can move.
    "numeric": [
        "language/expressions/addition", "language/expressions/subtraction",
        "language/expressions/multiplication", "language/expressions/division",
        "language/expressions/modulus", "language/expressions/exponentiation",
        "language/expressions/bitwise-and", "language/expressions/bitwise-or",
        "language/expressions/bitwise-xor", "language/expressions/bitwise-not",
        "language/expressions/left-shift", "language/expressions/right-shift",
        "language/expressions/unsigned-right-shift",
        "language/expressions/postfix-increment", "language/expressions/postfix-decrement",
        "language/expressions/prefix-increment", "language/expressions/prefix-decrement",
        "language/expressions/unary-minus", "language/expressions/unary-plus",
        "language/expressions/equals", "language/expressions/does-not-equals",
        "language/expressions/strict-equals", "language/expressions/strict-does-not-equals",
        "language/expressions/less-than", "language/expressions/greater-than",
        "language/expressions/less-than-or-equal", "language/expressions/greater-than-or-equal",
        "language/expressions/compound-assignment", "language/types/number",
        "built-ins/Number", "built-ins/Math", "built-ins/parseInt", "built-ins/parseFloat",
        "built-ins/JSON", "built-ins/Array", "built-ins/TypedArray", "built-ins/DataView",
    ],
    # The call/return/closure machinery an interpreter-loop change exercises.
    "calls": [
        "language/expressions/call", "language/expressions/new", "language/expressions/super",
        "language/expressions/arrow-function", "language/expressions/function",
        "language/statements/function", "language/statements/return",
        "built-ins/Function", "language/arguments-object",
    ],
}


# --- resolution -------------------------------------------------------------

def find_root(explicit):
    for cand in (explicit, os.environ.get("TEST262_ROOT"), "D:/test262",
                 os.path.join(REPO, "..", "test262"), os.path.join(REPO, "test262")):
        if cand and os.path.isdir(os.path.join(cand, "test")):
            return os.path.abspath(cand)
    sys.exit("test262 checkout not found: set TEST262_ROOT (expected D:/test262)")


def find_exe(explicit, build):
    proj = os.path.join(REPO, "FenBrowser.Js.Test262", "FenBrowser.Js.Test262.csproj")
    if build:
        subprocess.run(["dotnet", "build", proj, "-c", "Release", "--nologo", "-v", "q"], check=True)
    exe_name = "FenBrowser.Js.Test262" + (".exe" if os.name == "nt" else "")
    for cand in (explicit, os.environ.get("TEST262_EXE"),
                 os.path.join(REPO, "FenBrowser.Js.Test262", "bin", "Release", "net10.0", exe_name)):
        if cand and os.path.isfile(cand):
            return os.path.abspath(cand)
    sys.exit("runner not built: dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release "
             "(or pass --build)")


def tag_for(root, scope):
    rel = os.path.relpath(scope, os.path.join(root, "test")).replace("\\", "/").strip("/")
    return rel.replace("/", "_")


def count_tests(scope, shallow=False):
    if shallow:
        return sum(1 for f in os.listdir(scope) if f.endswith(".js"))
    return sum(len([f for f in fs if f.endswith(".js")]) for _, _, fs in os.walk(scope))


def load_result(path):
    try:
        with open(path, encoding="utf-8-sig") as fh:
            d = json.load(fh)
        if d.get("total") is None:
            return None
        return d
    except Exception:
        return None


# --- process control --------------------------------------------------------

def kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/F", "/T"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        import signal
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        pass


def run_batch(exe, root, scope, out_json, log_path, stall_sec, shallow=False, extra_args=()):
    """Run one directory as its own process. Returns (state, result_dict_or_None)
    where state is 'ok' | 'stall' | 'timeout' | 'error'."""
    n = count_tests(scope, shallow)
    hard = n * (TIMEOUT_MS // 1000) + HARD_BUFFER_SEC
    args = [exe, "--runtime-subset", "--root", root, "--test262", scope,
            "--max", "100000", "--timeout-ms", str(TIMEOUT_MS), "--out", out_json]
    if shallow:
        args.append("--test262-shallow")
    args.extend(extra_args)
    env = dict(os.environ, TEST262_PROGRESS="1")
    os.makedirs(os.path.dirname(out_json) or ".", exist_ok=True)
    if os.path.exists(out_json):
        os.remove(out_json)
    with open(log_path, "w", encoding="utf-8") as log:
        popen_kw = {"start_new_session": True} if os.name != "nt" else {}
        proc = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env=env, **popen_kw)
        started = time.time()
        state = "ok"
        while proc.poll() is None:
            time.sleep(1)
            now = time.time()
            try:
                idle = now - os.path.getmtime(log_path)
            except OSError:
                idle = 0
            if idle > stall_sec:
                state = "stall"
            elif now - started > hard:
                state = "timeout"
            if state != "ok":
                kill_tree(proc)
                break
    if state == "ok" and proc.returncode not in (0, 1):
        state = "error"
    return state, load_result(out_json)


def last_log_line(log_path):
    try:
        with open(log_path, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().strip().splitlines()
        return lines[-1] if lines else ""
    except OSError:
        return ""


def fmt(result):
    if not result or not result["total"]:
        return "0/0"
    text = f"{result['passed']}/{result['total']} = {100.0 * result['passed'] / result['total']:.1f}%"
    # Tests that never ran or timed out are not failures of the engine's logic,
    # but they are not passes either; say so instead of hiding them in the rate.
    extra = []
    if result.get("notRun"):
        extra.append(f"{result['notRun']} not run")
    if result.get("timedOut"):
        extra.append(f"{result['timedOut']} timed out")
    if result.get("abandonedWorkers"):
        extra.append(f"{result['abandonedWorkers']} stuck workers")
    return text + (f"  ({', '.join(extra)})" if extra else "")


def engine_commit():
    """HEAD of this checkout, with -dirty for uncommitted changes: the value the
    runner stamps into every result as fenbrowserCommit."""
    try:
        head = subprocess.run(["git", "-C", REPO, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        dirty = subprocess.run(["git", "-C", REPO, "status", "--porcelain", "--untracked-files=no"],
                               capture_output=True, text=True, check=True).stdout.strip()
        return head + ("-dirty" if dirty else "")
    except Exception:
        return "unknown"


def write_total(outdir):
    passed = total = 0
    for f in glob.glob(os.path.join(outdir, "b_*.json")):
        d = load_result(f)
        if d:
            passed += d["passed"]
            total += d["total"]
    pct = round(100.0 * passed / total, 2) if total else 0
    with open(os.path.join(outdir, "_batched_total.json"), "w", encoding="utf-8") as fh:
        json.dump({"passed": passed, "total": total, "pct": pct}, fh)
    return passed, total, pct


def refresh_report():
    subprocess.run([sys.executable, os.path.join(REPO, "scripts", "test262", "report.py")], check=False)


# --- batch plan for the full suite ------------------------------------------

def full_batches(root):
    """One batch per built-ins/* and language/* directory; the two oversized
    language dirs (expressions, statements) are split one level deeper."""
    t = os.path.join(root, "test")
    batches = []
    for big in ("expressions", "statements"):
        batches += sorted(glob.glob(os.path.join(t, "language", big, "*", "")))
    for d in sorted(glob.glob(os.path.join(t, "language", "*", ""))):
        if os.path.basename(d.rstrip("/\\")) not in ("expressions", "statements"):
            batches.append(d)
    batches += sorted(glob.glob(os.path.join(t, "built-ins", "*", "")))
    batches += [os.path.join(t, "intl402"), os.path.join(t, "annexB"), os.path.join(t, "staging"),
                os.path.join(t, "harness")]
    return [os.path.normpath(b) for b in batches if os.path.isdir(b)]


def split_batch(exe, root, scope, outdir, stall_sec, extra_args):
    """Recover a stall-killed batch: one shallow run for its loose top-level
    files plus one recursive run per immediate subdirectory, so the wedged test
    only takes its own bucket down. The parent wrote no JSON, so nothing is
    double-counted by the aggregator."""
    base = tag_for(root, scope)
    parts = [(scope, f"{base}_TOP", True)]
    parts += [(os.path.join(scope, s), f"{base}_{s}", False)
              for s in sorted(os.listdir(scope)) if os.path.isdir(os.path.join(scope, s))]
    for sub_scope, tag, shallow in parts:
        out = os.path.join(outdir, f"b_{tag}.json")
        log = os.path.join(outdir, f"b_{tag}.log")
        state, res = run_batch(exe, root, sub_scope, out, log, stall_sec, shallow, extra_args)
        marker = "" if state == "ok" else f"  {state.upper()} last:[{last_log_line(log)}]"
        print(f"    split {tag:<44} {fmt(res)}{marker}")


# --- subcommands ------------------------------------------------------------

def cmd_full(a, exe, root):
    outdir = a.out
    os.makedirs(outdir, exist_ok=True)
    if a.fresh:
        for f in glob.glob(os.path.join(outdir, "b_*.json")) + glob.glob(os.path.join(outdir, "b_*.log")) \
                + glob.glob(os.path.join(outdir, "_batched_total.json")):
            os.remove(f)
    batches = full_batches(root)
    commit = engine_commit()
    print(f"{len(batches)} batches  per-test {TIMEOUT_MS}ms  stall {a.stall}s  engine {commit}  ->  {outdir}")
    stalled = []
    for i, scope in enumerate(batches, 1):
        tag = tag_for(root, scope)
        out = os.path.join(outdir, f"b_{tag}.json")
        log = os.path.join(outdir, f"b_{tag}.log")
        cached = load_result(out)
        # Resume: a batch is done when it has a result from this same engine
        # revision. One from another revision is stale and runs again, so a
        # store never mixes revisions silently.
        if cached and cached.get("fenbrowserCommit") == commit:
            print(f"[{i:3}/{len(batches)}] {tag:<44} SKIP  {fmt(cached)} (cached)")
            continue
        print(f"[{i:3}/{len(batches)}] {tag:<44} run ...", flush=True)
        state, res = run_batch(exe, root, scope, out, log, a.stall, extra_args=a.runner_args)
        if state == "ok":
            print(f"[{i:3}/{len(batches)}] {tag:<44} {fmt(res)}")
            continue
        print(f"[{i:3}/{len(batches)}] {tag:<44} {state.upper()}  last:[{last_log_line(log)}]")
        stalled.append(scope)
        if a.split_on_stall:
            split_batch(exe, root, scope, outdir, a.stall, a.runner_args)
    p, t, pct = write_total(outdir)
    print(f"DONE {p}/{t} = {pct}%")
    if stalled:
        print("stall-killed batches (rerun with `category <path> --split-on-stall`):")
        for s in stalled:
            print("  " + tag_for(root, s).replace("_", "/", 1))
    if not a.no_report and outdir == STORE:
        refresh_report()
    return 0


def resolve_scope(root, path):
    rel = path.replace("\\", "/").strip("/")
    if rel.startswith("test/"):
        rel = rel[5:]
    scope = os.path.join(root, "test", rel)
    if not os.path.isdir(scope):
        # A missing scope must not fall through to a whole-tree run.
        sys.exit(f"not a directory under {root}/test: {rel}")
    return os.path.normpath(scope)


def cmd_category(a, exe, root):
    os.makedirs(a.out, exist_ok=True)
    rc = 0
    for path in a.paths:
        scope = resolve_scope(root, path)
        tag = tag_for(root, scope)
        out = os.path.join(a.out, f"b_{tag}.json")
        log = os.path.join(a.out, f"b_{tag}.log")
        n = count_tests(scope)
        print(f"{tag}: {n} tests, hard timeout {n * 2 + HARD_BUFFER_SEC}s  ->  {out}", flush=True)
        state, res = run_batch(exe, root, scope, out, log, a.stall, extra_args=a.runner_args)
        if state == "ok":
            print(f"  -> {fmt(res)}")
        else:
            rc = 1
            print(f"  -> {state.upper()} (see {log}) last:[{last_log_line(log)}]")
            if a.split_on_stall:
                split_batch(exe, root, scope, a.out, a.stall, a.runner_args)
    if a.out == STORE:
        write_total(a.out)
        if not a.no_report:
            refresh_report()
    return rc


def cmd_file(a, exe, root):
    path = os.path.abspath(a.path)
    if not os.path.isfile(path):
        sys.exit(f"no such file: {path}")
    out = os.path.join("Results", "test262", "single.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    args = [exe, "--runtime-subset", "--root", root, "--test262-file", path,
            "--timeout-ms", str(TIMEOUT_MS), "--out", out] + a.runner_args
    try:
        subprocess.run(args, timeout=TIMEOUT_MS / 1000 + HARD_BUFFER_SEC, check=False)
    except subprocess.TimeoutExpired:
        print("TIMEOUT: runner did not finish (wedged test)")
        return 2
    d = load_result(out)
    if not d:
        print(f"no result written (see runner output above)")
        return 2
    print(f"{fmt(d)}")
    for f in d.get("failures", []):
        print(f"  [{f.get('classification')}] {f.get('relativePath')}")
        detail = (f.get("details") or f.get("message") or "").strip()
        if detail:
            print("    " + detail.replace("\n", "\n    "))
    return 0 if d["passed"] == d["total"] else 1


def cmd_slice(a, exe, root):
    if a.name not in SLICES:
        sys.exit(f"unknown slice {a.name!r}; known: {', '.join(SLICES)}")
    outdir = os.path.join("Results", "test262", "slices", a.name, a.label)
    os.makedirs(outdir, exist_ok=True)
    passed = total = 0
    for rel in SLICES[a.name]:
        scope = resolve_scope(root, rel)
        tag = tag_for(root, scope)
        state, res = run_batch(exe, root, scope, os.path.join(outdir, f"b_{tag}.json"),
                               os.path.join(outdir, f"b_{tag}.log"), a.stall, extra_args=a.runner_args)
        if res:
            passed += res["passed"]
            total += res["total"]
        print(f"  {tag:<48} {fmt(res)}{'' if state == 'ok' else '  ' + state.upper()}")
    pct = round(100.0 * passed / total, 2) if total else 0
    with open(os.path.join(outdir, "_total.json"), "w", encoding="utf-8") as fh:
        json.dump({"passed": passed, "total": total, "pct": pct}, fh)
    print(f"{a.name}/{a.label}: {passed}/{total} = {pct}%  ->  {outdir}")
    return 0


def cmd_status(a, exe=None, root=None):
    return subprocess.call([sys.executable, os.path.join(REPO, "scripts", "test262", "status.py")])


# --- CLI --------------------------------------------------------------------

def main(argv):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--root", help="test262 checkout (default: TEST262_ROOT or D:/test262)")
    common.add_argument("--exe", help="runner binary (default: Release build of FenBrowser.Js.Test262)")
    common.add_argument("--build", action="store_true", help="dotnet build the runner first")
    common.add_argument("--stall", type=int, default=int(os.environ.get("STALL_TIMEOUT_SEC", "30")),
                        help="kill a batch whose log is idle this many seconds (default 30)")
    common.add_argument("--runner-args", nargs=argparse.REMAINDER, default=[],
                        help="everything after this goes to the runner verbatim (e.g. --engine X)")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("full", parents=[common], help="whole suite, one process per directory batch, resumable")
    s.add_argument("--out", default=STORE)
    s.add_argument("--fresh", action="store_true", help="discard prior batch results first")
    s.add_argument("--split-on-stall", action="store_true",
                   help="rerun a stall-killed batch as top-level files + one run per subdirectory")
    s.add_argument("--no-report", action="store_true", help="don't regenerate docs/test262_results.md")
    s.set_defaults(fn=cmd_full)

    s = sub.add_parser("category", parents=[common], help="rerun one or more directories into the batched store")
    s.add_argument("paths", nargs="+", help="paths under <root>/test, e.g. built-ins/Object")
    s.add_argument("--out", default=STORE, help="result dir (default: the batched store)")
    s.add_argument("--split-on-stall", action="store_true")
    s.add_argument("--no-report", action="store_true", help="don't regenerate docs/test262_results.md")
    s.set_defaults(fn=cmd_category)

    s = sub.add_parser("file", parents=[common], help="run a single test file and print its failure details")
    s.add_argument("path")
    s.set_defaults(fn=cmd_file)

    s = sub.add_parser("slice", parents=[common], help="run a named directory slice for a before/after comparison")
    s.add_argument("name", help=", ".join(SLICES))
    s.add_argument("--label", required=True, help="e.g. before / after")
    s.set_defaults(fn=cmd_slice)

    s = sub.add_parser("status", help="per-category summary of the batched store")
    s.set_defaults(fn=cmd_status)

    a = p.parse_args(argv)
    if any(arg == "--timeout-ms" or arg.startswith("--timeout-ms=") for arg in getattr(a, "runner_args", [])):
        sys.exit(f"--timeout-ms is fixed at {TIMEOUT_MS} by the test262 protocol and cannot be forwarded")
    os.chdir(REPO)
    if a.cmd == "status":
        return cmd_status(a)
    root = find_root(a.root)
    exe = find_exe(a.exe, a.build)
    return a.fn(a, exe, root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
