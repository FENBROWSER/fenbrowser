#!/usr/bin/env python3
"""Token-lean triage of FenBrowser logs and debug-site bundles.

The structured logs are large and repetitive: every record carries ~1 KB of
mostly-null context, and the main log and the trace log hold the same events
twice. This reads one stream, collapses repeats into templates, and prints
bounded, one-line-per-finding output, so a failure can be found without
opening multi-megabyte JSONL, HTML or dump files.

Usage (from the repo root):
    python scripts/fenlog.py runs [-n 10]
    python scripts/fenlog.py summary [RUN]
    python scripts/fenlog.py errors [RUN] [--limit 25]
    python scripts/fenlog.py grep PATTERN [RUN] [--cat C] [--level L] [--data] [--limit 40]
    python scripts/fenlog.py show SEQ [RUN] [--context 5]
    python scripts/fenlog.py slow [RUN] [--limit 15]
    python scripts/fenlog.py diff OLD_RUN NEW_RUN
    python scripts/fenlog.py bundles [-n 10] [--site HOST]
    python scripts/fenlog.py bundle [DIR|latest] [--site HOST]
    python scripts/fenlog.py missing-apis [SITE] [--limit 20]

RUN is a log stamp such as 20260913_181127 (a unique prefix or suffix is
enough), a .jsonl/.ndjson file, a debug-site bundle directory, or omitted for
the newest run.
"""

import argparse
import collections
import json
import os
import re
import sys
from datetime import datetime

LOG_NAME = re.compile(
    r"^fenbrowser_(?P<t1>trace_)?(?P<stamp>\d{8}_\d{6})(?P<t2>_trace)?(?:\.(?P<rot>\d{3}))?\.jsonl$")
BUNDLE_ID = re.compile(r"^\d{8}T\d{6}Z$")
ARTIFACT_NAME = re.compile(r"^(rendered_text|engine_source|raw_source)_(\d{8}_\d{6})\.(txt|html)$")

LEVEL_RANK = {"TRACE": 0, "DEBUG": 1, "INFO": 2, "WARN": 3, "WARNING": 3, "ERROR": 4, "FATAL": 5, "CRITICAL": 5}
# Keys every record repeats; they say nothing about this particular event.
BOILERPLATE = {"marker", "sequence", "subsystem", "source_file", "source_line", "source_member",
               "traceCategory", "eventName", "event"}
# Logging shims whose file:line is the same for every message they forward.
GENERIC_SOURCES = {"EngineLogCompat.cs", "EngineLog.cs", "FenLogger.cs"}
MASKS = [
    (re.compile(r"\b[a-z][a-z0-9+.-]*://[^\s'\")\]]+", re.I), "<url>"),
    (re.compile(r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", re.I), "<guid>"),
    (re.compile(r"\b[0-9a-f]{12,}\b", re.I), "<hex>"),
    (re.compile(r"\d+(?:\.\d+)?"), "<n>"),
]
ERROR_DETAIL_KEYS = ("error", "exceptionMessage", "ExceptionMessage", "errorMessage", "detail", "reason")


# --------------------------------------------------------------------------- io

def out(text=""):
    print(text)


def clip(text, width):
    text = str(text)
    indent = text[:len(text) - len(text.lstrip(" "))]
    text = indent + " ".join(text.split())
    return text if len(text) <= width else text[:width - 3] + "..."


def json_lines(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.lstrip("\ufeff").strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except ValueError:
                continue


def load_json(path):
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def human_size(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f}{unit}" if unit == "B" else f"{n:.1f}{unit}"
        n /= 1024


def parse_ts(ts):
    m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(\.\d+)?(Z|[+-]\d\d:\d\d)?$", ts or "")
    if not m:
        return None
    tz = m[3] or "+00:00"
    tz = "+00:00" if tz == "Z" else tz
    try:
        return datetime.fromisoformat(m[1] + (m[2] or ".0")[:7] + tz).astimezone()
    except ValueError:
        return None


# ------------------------------------------------------------------ records

def normalize(row):
    """One shape for trace rows (ts/level/category/data) and EngineLog rows."""
    if "level" in row or "category" in row:
        data = row.get("data") or {}
        return {
            "seq": data.get("sequence"), "ts": row.get("ts") or "",
            "level": str(row.get("level") or "").upper(),
            "cat": row.get("category") or data.get("subsystem") or "",
            "event": row.get("event") or "", "msg": row.get("message") or "",
            "data": data, "file": data.get("source_file"), "line": data.get("source_line"),
            "member": data.get("source_member"), "marker": data.get("marker"),
        }
    fields = row.get("fields") or {}
    return {
        "seq": row.get("sequence"), "ts": row.get("timestampUtc") or "",
        "level": str(row.get("severity") or "").upper(),
        "cat": fields.get("traceCategory") or row.get("subsystem") or "",
        "event": fields.get("event") or "", "msg": row.get("message") or "",
        "data": fields, "file": row.get("sourceFile"), "line": row.get("sourceLine"),
        "member": row.get("sourceMember"), "marker": row.get("marker"),
    }


def rank(level):
    return LEVEL_RANK.get(level, 2)


def template(text):
    for rx, rep in MASKS:
        text = rx.sub(rep, text)
    return clip(text, 160)


def location(rec):
    if not rec["file"] or rec["file"] in GENERIC_SOURCES:
        return ""
    member = f" {rec['member']}" if rec.get("member") else ""
    return f"{rec['file']}:{rec['line']}{member}"


def compact(rec, width, with_data=False):
    ts = parse_ts(rec["ts"])
    clock = ts.strftime("%H:%M:%S.%f")[:-3] if ts else "--:--:--.---"
    text = rec["msg"]
    if rec["event"] and rec["event"] not in text:
        text = f"{rec['event']} | {text}"
    loc = location(rec)
    if loc:
        text += f"  ({loc})"
    if with_data:
        extra = {k: v for k, v in rec["data"].items() if k not in BOILERPLATE and v not in (None, "", [], {})}
        if extra:
            text += "  " + ", ".join(f"{k}={clip(v, 60)}" for k, v in extra.items())
    seq = rec["seq"] if rec["seq"] is not None else "-"
    return clip(f"{seq:>6} {clock} {rec['level'][:5]:<5} {rec['cat'][:12]:<12} {text}", width)


def error_detail(rec):
    for key in ERROR_DETAIL_KEYS:
        value = rec["data"].get(key)
        if value:
            lines = [l.strip() for l in str(value).splitlines() if l.strip()]
            # A JS error repeats its first line; keep distinct lines only.
            return " / ".join(dict.fromkeys(lines[:4]))
    return ""


def script_origin(rec):
    d = rec["data"]
    label = d.get("scriptResolvedUrl") or d.get("scriptSrc") or d.get("scriptSourceLabel") or d.get("resource_url")
    if not label:
        return ""
    line = d.get("scriptSourceLine")
    return f"{label}:{line}" if line not in (None, "") else str(label)


# --------------------------------------------------------------------- runs

class Run:
    def __init__(self, name, paths, trace=None):
        self.name = name
        self.main = paths
        self.trace = trace

    @property
    def files(self):
        return ([self.trace] if self.trace else []) + self.main

    def records(self):
        # The trace and the main log carry the same events; read only one.
        for path in ([self.trace] if self.trace else self.main):
            for row in json_lines(path):
                yield normalize(row)


def discover_runs(logs):
    runs = {}
    try:
        names = os.listdir(logs)
    except OSError:
        sys.exit(f"logs directory not found: {logs}")
    for name in names:
        m = LOG_NAME.match(name)
        if not m:
            continue
        run = runs.setdefault(m["stamp"], Run(m["stamp"], []))
        path = os.path.join(logs, name)
        if m["t1"] or m["t2"]:
            # Two writers can each leave a trace for one stamp: the engine's
            # full trace, and one holding little more than a session header.
            if not run.trace or os.path.getsize(path) > os.path.getsize(run.trace):
                run.trace = path
        else:
            run.main.append((int(m["rot"] or 0), path))
    for run in runs.values():
        run.main = [p for _, p in sorted(run.main)]
    return sorted(runs.values(), key=lambda r: r.name, reverse=True)


def resolve_run(arg, logs):
    if arg and os.path.isdir(arg):
        for candidate in ("trace.jsonl", "logs.ndjson"):
            path = os.path.join(arg, candidate)
            if os.path.isfile(path):
                return Run(os.path.normpath(arg), [path])
        sys.exit(f"no trace.jsonl or logs.ndjson in {arg}")
    if arg and os.path.isfile(arg):
        return Run(arg, [arg])
    runs = discover_runs(logs)
    if not runs:
        sys.exit(f"no fenbrowser_*.jsonl runs in {logs}")
    if not arg:
        return runs[0]
    matches = [r for r in runs if r.name == arg] or [r for r in runs if r.name.startswith(arg) or r.name.endswith(arg)]
    if len(matches) == 1:
        return matches[0]
    if not matches:
        sys.exit(f"no run matches '{arg}' (try: fenlog.py runs)")
    sys.exit(f"'{arg}' is ambiguous: " + ", ".join(r.name for r in matches[:8]))


def run_artifacts(run, logs, start, end):
    """rendered_text/engine_source/raw_source files stamped inside the run's window."""
    if not start or not end or not os.path.isdir(logs):
        return []
    lo = start.replace(tzinfo=None, microsecond=0)
    hi = end.replace(tzinfo=None)
    found = []
    for name in os.listdir(logs):
        m = ARTIFACT_NAME.match(name)
        if not m:
            continue
        stamp = datetime.strptime(m[2], "%Y%m%d_%H%M%S")
        if lo <= stamp <= hi.replace(microsecond=0) or abs((stamp - hi).total_seconds()) <= 5:
            found.append(os.path.join(logs, name))
    return sorted(found)


# ------------------------------------------------------------------ analyses

def collect(run):
    recs = list(run.records())
    if not recs:
        sys.exit(f"{run.name}: no readable records")
    return recs


def group_problems(recs, min_rank):
    groups = collections.OrderedDict()
    unimplemented = collections.Counter()
    for rec in recs:
        if rank(rec["level"]) < min_rank:
            continue
        if rec.get("marker") == "Unimplemented" or rec["msg"].startswith("[UNSUPPORTED]"):
            api = rec["data"].get("api") or rec["msg"].split("]", 1)[-1].split("|")[0].strip()
            unimplemented[api] += 1
            continue
        key = (rec["level"], rec["cat"], template(f"{rec['event']} {rec['msg']}" if rec["event"] not in rec["msg"] else rec["msg"]))
        group = groups.get(key)
        if group is None:
            groups[key] = group = {"count": 0, "first": rec, "details": collections.Counter()}
        group["count"] += 1
        detail = error_detail(rec)
        if detail:
            group["details"][clip(detail, 200)] += 1
    return groups, unimplemented


def print_problems(recs, limit, width, min_rank=3):
    groups, unimplemented = group_problems(recs, min_rank)
    total = sum(g["count"] for g in groups.values())
    ordered = sorted(groups.items(), key=lambda kv: (-rank(kv[0][0]), -kv[1]["count"]))
    out(f"{total} warn+ records in {len(groups)} templates"
        + (f"; {sum(unimplemented.values())} unimplemented-API warnings collapsed" if unimplemented else ""))
    for (level, cat, tmpl), g in ordered[:limit]:
        first = g["first"]
        seq = first["seq"] if first["seq"] is not None else "-"
        out(clip(f"  {g['count']:>4}x {level[:5]:<5} {cat[:12]:<12} {tmpl}", width))
        where = ", ".join(x for x in (location(first), script_origin(first)) if x)
        out(clip(f"        first seq {seq}" + (f" | {where}" if where else ""), width))
        for detail, n in g["details"].most_common(2):
            out(clip(f"        -> {detail}" + (f" ({n}x)" if n > 1 else ""), width))
    if len(ordered) > limit:
        out(f"  ... {len(ordered) - limit} more templates (raise --limit)")
    if unimplemented:
        names = ", ".join(f"{k}" + (f" x{v}" if v > 1 else "") for k, v in unimplemented.most_common(10))
        out(clip(f"  unimplemented APIs: {names}", width))


SLOW_PATTERNS = [
    (re.compile(r"\[LongTask\] '([^']+)' took (\d+(?:\.\d+)?) ?ms"), lambda m: (m[1], float(m[2]))),
    (re.compile(r"kind=(\S+)(?: phase=(\S+))?.*?elapsedMs=(\d+)"),
     lambda m: (f"JS job {m[1]}" + (f" {m[2]}" if m[2] else ""), float(m[3]))),
]


def slow_items(recs):
    groups = collections.defaultdict(lambda: {"max": 0.0, "count": 0, "total": 0.0, "seq": None})
    for rec in recs:
        hits = []
        for rx, pick in SLOW_PATTERNS:
            m = rx.search(rec["msg"])
            if m:
                hits.append(pick(m))
                break
        if not hits:
            for key, value in rec["data"].items():
                if key.endswith("Ms") and key not in ("delayMs",) and isinstance(value, (int, float)) and value >= 50:
                    hits.append((f"{rec['cat']} {rec['event'] or rec['msg'][:40]} {key}", float(value)))
        for name, ms in hits:
            g = groups[name]
            g["count"] += 1
            g["total"] += ms
            if ms >= g["max"]:
                g["max"], g["seq"] = ms, rec["seq"]
    return sorted(groups.items(), key=lambda kv: -kv[1]["max"])


def missing_api_names(recs):
    names = collections.Counter()
    for rec in recs:
        api = rec["data"].get("apiName") or (rec["data"].get("api") if rec.get("marker") == "Unimplemented" else None)
        if api:
            names[api] += 1
    return names


# ------------------------------------------------------------------ commands

def cmd_runs(args):
    runs = discover_runs(args.logs)
    total = 0
    for root, _, files in os.walk(args.logs):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    out(f"{len(runs)} runs in {args.logs} ({human_size(total)} total incl. bundles/dumps); newest first")
    url_rx = re.compile(rb'"(?:url|requestedUrl|Url)":"(https?://[^"]+)"')
    for run in runs[:args.n]:
        path = run.trace or run.main[0]
        with open(path, "rb") as f:
            blob = f.read()
        warn = blob.count(b'"level":"WARN"') + blob.count(b'"severity":"Warn"')
        err = sum(blob.count(t) for t in (b'"level":"ERROR"', b'"severity":"Error"', b'"level":"FATAL"', b'"severity":"Fatal"'))
        m = url_rx.search(blob)
        streams = ("T" if run.trace else "") + (f"M{len(run.main)}" if run.main else "")
        size = sum(os.path.getsize(p) for p in run.files)
        out(clip(f"  {run.name}  {human_size(size):>7}  {streams:<4} warn={warn:<4} err={err:<3} {m[1].decode() if m else ''}", args.width))


def cmd_summary(args):
    run = resolve_run(args.run, args.logs)
    recs = collect(run)
    stamps = [t for t in (parse_ts(r["ts"]) for r in (recs[0], recs[-1])) if t]
    start, end = (stamps[0], stamps[-1]) if len(stamps) == 2 else (None, None)
    source = os.path.basename(run.trace) if run.trace else ", ".join(os.path.basename(p) for p in run.main)
    span = f"{start:%Y-%m-%d %H:%M:%S} -> {end:%H:%M:%S} ({(end - start).total_seconds():.1f}s)" if start else "?"
    out(f"run {run.name}: {len(recs)} records from {source}; {span}")
    levels = collections.Counter(r["level"] for r in recs)
    out("levels: " + ", ".join(f"{k}={v}" for k, v in sorted(levels.items(), key=lambda kv: -rank(kv[0]))))
    cats = collections.Counter(r["cat"] for r in recs)
    out(clip("categories: " + ", ".join(f"{k}={v}" for k, v in cats.most_common(12)), args.width))
    for rec in recs:
        if "run summary" in rec["msg"]:
            extra = {k: v for k, v in rec["data"].items() if k not in BOILERPLATE}
            out(clip("run summary: " + ", ".join(f"{k}={v}" for k, v in extra.items()), args.width))
    urls = collections.Counter(r["data"].get("url") for r in recs if r["data"].get("url"))
    if urls:
        out(clip("urls: " + ", ".join(f"{u} ({n})" for u, n in urls.most_common(3)), args.width))
    navs = [r for r in recs if r["data"].get("phase") and r["cat"] == "Navigation"]
    if navs:
        last = navs[-1]["data"]
        out(clip(f"navigation: last phase {last.get('phase')} status={last.get('responseStatus')} "
                 f"url={last.get('effectiveUrl') or last.get('requestedUrl')}", args.width))
    out()
    print_problems(recs, args.limit, args.width)
    slow = slow_items(recs)[:5]
    if slow:
        out()
        out("slowest: " + "; ".join(f"{name} max {g['max']:.0f}ms x{g['count']}" for name, g in slow))
    apis = missing_api_names(recs)
    if apis:
        out(clip(f"missing APIs ({len(apis)}): " + ", ".join(k for k, _ in apis.most_common(10)), args.width))
    artifacts = run_artifacts(run, args.logs, start, end) if not os.path.isdir(args.run or "") else []
    if artifacts:
        out("artifacts in this run's window (grep these, do not read whole):")
        for path in artifacts[:9]:
            out(f"  {path}  {human_size(os.path.getsize(path))}")
    out()
    out(f"next: fenlog.py errors {run.name} | grep PATTERN {run.name} --data | show SEQ {run.name}")


def cmd_errors(args):
    run = resolve_run(args.run, args.logs)
    out(f"run {run.name}")
    print_problems(collect(run), args.limit, args.width, min_rank=rank(args.level.upper()))


def cmd_grep(args):
    run = resolve_run(args.run, args.logs)
    rx = re.compile(args.pattern, re.I)
    min_rank = rank(args.level.upper()) if args.level else -1
    shown = matched = 0
    for rec in run.records():
        if rank(rec["level"]) < min_rank:
            continue
        if args.cat and not rec["cat"].lower().startswith(args.cat.lower()):
            continue
        haystack = f"{rec['event']} {rec['msg']}"
        if args.in_data:
            haystack += " " + json.dumps(rec["data"], ensure_ascii=False)
        if not rx.search(haystack):
            continue
        matched += 1
        if shown < args.limit:
            out(compact(rec, args.width, args.data))
            shown += 1
    out(f"-- {matched} matches" + (f", {matched - shown} not shown (raise --limit or narrow)" if matched > shown else ""))


def cmd_show(args):
    run = resolve_run(args.run, args.logs)
    recs = collect(run)
    hits = [i for i, r in enumerate(recs) if str(r["seq"]) == str(args.seq)]
    if not hits:
        sys.exit(f"no record with sequence {args.seq} in {run.name}")
    i = hits[0]
    for rec in recs[max(0, i - args.context):i]:
        out(compact(rec, args.width))
    rec = recs[i]
    out(">> " + compact(rec, args.width))
    out(f"   source: {rec['file']}:{rec['line']} {rec['member'] or ''}  marker={rec.get('marker')}")
    flags_on, flags_off = [], []
    for key, value in rec["data"].items():
        if key in BOILERPLATE or value in (None, "", [], {}):
            continue
        # Booleans take a line each and rarely matter; fold them into one.
        if isinstance(value, bool) or value in ("True", "False"):
            (flags_on if value in (True, "True") else flags_off).append(key)
            continue
        text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
        lines = text.splitlines() or [""]
        out(clip(f"   {key}: {lines[0]}", args.width))
        for extra in lines[1:12]:
            out(clip(f"   {'':{len(key)}}  {extra}", args.width))
    if flags_on or flags_off:
        out(clip(f"   true: {', '.join(flags_on) or '-'} | false: {', '.join(flags_off) or '-'}", args.width))
    for rec in recs[i + 1:i + 1 + args.context]:
        out(compact(rec, args.width))
    if len(hits) > 1:
        out(f"-- {len(hits)} records share sequence {args.seq} (multiple processes); showed the first")


def cmd_slow(args):
    run = resolve_run(args.run, args.logs)
    items = slow_items(collect(run))
    out(f"run {run.name}: {len(items)} timed operations, by worst case")
    for name, g in items[:args.limit]:
        out(clip(f"  max {g['max']:>8.0f}ms  x{g['count']:<4} total {g['total']:>9.0f}ms  seq {g['seq']}  {name}", args.width))


def cmd_diff(args):
    old, new = resolve_run(args.old, args.logs), resolve_run(args.new, args.logs)
    old_recs, new_recs = collect(old), collect(new)
    out(f"{old.name} -> {new.name}")
    lo, ln = collections.Counter(r["level"] for r in old_recs), collections.Counter(r["level"] for r in new_recs)
    out("levels: " + ", ".join(f"{k} {lo.get(k, 0)}->{ln.get(k, 0)}"
                               for k in sorted(set(lo) | set(ln), key=lambda k: -rank(k)) if rank(k) >= 2))
    go, _ = group_problems(old_recs, 3)
    gn, _ = group_problems(new_recs, 3)
    new_keys = [k for k in gn if k not in go]
    gone_keys = [k for k in go if k not in gn]
    changed = [k for k in gn if k in go and abs(gn[k]["count"] - go[k]["count"]) >= max(2, go[k]["count"] // 2)]
    for title, keys, source in (("NEW", new_keys, gn), ("GONE", gone_keys, go)):
        out(f"{title} ({len(keys)}):")
        for key in sorted(keys, key=lambda k: -source[k]["count"])[:args.limit]:
            detail = next(iter(source[key]["details"]), "")
            out(clip(f"  {source[key]['count']:>4}x {key[0][:5]:<5} {key[1][:12]:<12} {key[2]}" + (f" -> {detail}" if detail else ""), args.width))
    if changed:
        out(f"CHANGED ({len(changed)}):")
        for key in changed[:args.limit]:
            out(clip(f"  {go[key]['count']}->{gn[key]['count']} {key[0][:5]:<5} {key[1][:12]:<12} {key[2]}", args.width))
    so, sn = slow_items(old_recs)[:5], dict(slow_items(new_recs))
    if so:
        out("slowest (old max -> new max): " + "; ".join(
            f"{name} {g['max']:.0f}->{sn[name]['max']:.0f}ms" if name in sn else f"{name} {g['max']:.0f}ms->absent"
            for name, g in so))


def bundle_dirs(logs, site=None):
    root = os.path.join(logs, "real-site")
    found = []
    if not os.path.isdir(root):
        return found
    for host in os.listdir(root):
        if site and site.lower() not in host.lower():
            continue
        host_dir = os.path.join(root, host)
        if not os.path.isdir(host_dir):
            continue
        for run_id in os.listdir(host_dir):
            if BUNDLE_ID.match(run_id):
                found.append((run_id, os.path.join(host_dir, run_id)))
    return [path for _, path in sorted(found, reverse=True)]


def summary_md(path):
    values, blockers, section = {}, [], None
    try:
        with open(os.path.join(path, "summary.md"), encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                line = line.rstrip()
                if line.startswith("## "):
                    section = line[3:]
                    continue
                if ": " in line and not line.startswith("- "):
                    key, value = line.split(": ", 1)
                    if section == "First Blocker Signals":
                        blockers.append((key, value))
                    else:
                        values[key] = value
    except OSError:
        pass
    return values, blockers


def cmd_bundles(args):
    dirs = bundle_dirs(args.logs, args.site)
    out(f"{len(dirs)} debug-site bundles; newest first")
    for path in dirs[:args.n]:
        v, _ = summary_md(path)
        out(clip(f"  {path}  {v.get('URL', '?')}  phase={v.get('Navigation lifecycle phase', '?')} "
                 f"scriptsFailed={v.get('Scripts failed', '?')} exceptions={v.get('Exceptions total', '?')} "
                 f"netFailed={v.get('Failed network requests', '?')} elapsed={v.get('Elapsed', '?')}", args.width))


BUNDLE_METRICS = ["DOM nodes", "Rendered text length", "Layout boxes", "Zero-area boxes", "Paint nodes",
                  "Scripts discovered", "Scripts executed", "Scripts failed", "Script fetch failures",
                  "Network requests", "Failed network requests", "Exceptions total", "Console messages",
                  "Callback failures", "Navigation failures", "Unstyled elements"]


def cmd_bundle(args):
    if args.path and args.path != "latest":
        path = args.path
    else:
        dirs = bundle_dirs(args.logs, args.site)
        if not dirs:
            sys.exit("no debug-site bundles under logs/real-site")
        path = dirs[0]
    v, blockers = summary_md(path)
    out(f"bundle {path}")
    out(clip(f"url {v.get('URL', '?')} -> {v.get('Final URL', '?')}  elapsed {v.get('Elapsed', '?')}  "
             f"phase {v.get('Navigation lifecycle phase', '?')}  readyState {v.get('Document readyState probe', '?')}", args.width))
    metrics = [f"{k}={v[k]}" for k in BUNDLE_METRICS if k in v]
    for i in range(0, len(metrics), 6):
        out(clip(("metrics: " if i == 0 else "         ") + ", ".join(metrics[i:i + 6]), args.width))
    signals = [(k, val) for k, val in blockers if val not in ("(none captured)", "True", "completed")]
    if signals:
        out(clip("blocker signals: " + "; ".join(f"{k}: {val}" for k, val in signals), args.width))

    fb = load_json(os.path.join(path, "first_blocker.json"))
    if isinstance(fb, dict):
        out(clip(f"first_blocker: {fb.get('Result')} bucket={fb.get('FailureBucket')} owner={fb.get('SubsystemOwner')} "
                 f"milestone={fb.get('MilestoneBlocked')} confidence={fb.get('Confidence')}", args.width))
        if fb.get("Explanation"):
            out(clip(f"  {fb['Explanation']}", args.width))
        for key in ("AlternativeCandidates", "NonFatalRemainingFailures"):
            for item in (fb.get(key) or [])[:4]:
                out(clip(f"  {key[:-1] if key.endswith('s') else key}: {item}", args.width))

    ex = load_json(os.path.join(path, "exceptions.json"))
    if isinstance(ex, dict) and ex.get("TotalCount"):
        groups = collections.Counter()
        where = {}
        for key, items in ex.items():
            if not isinstance(items, list):
                continue
            for item in items:
                if not isinstance(item, dict):
                    continue
                message = (item.get("ExceptionMessage") or item.get("Message") or "").splitlines()
                sig = f"{item.get('ExceptionType', '?')}: {message[0].strip() if message else ''}"
                groups[sig] += 1
                src = item.get("ScriptUrl") or item.get("ScriptSourceLabel")
                where.setdefault(sig, f"{item.get('CallbackCategory', key)}" + (f" @ {src}:{item.get('SourceLine')}" if src else ""))
        out(f"exceptions: {ex.get('TotalCount')}")
        for sig, n in groups.most_common(args.limit):
            out(clip(f"  {n}x {sig}  [{where[sig]}]", args.width))

    ma = load_json(os.path.join(path, "missing_apis.json"))
    if isinstance(ma, dict) and ma.get("records"):
        names = collections.Counter(r.get("apiName") for r in ma["records"] if isinstance(r, dict))
        out(clip(f"missing APIs ({len(names)}): " + ", ".join(k for k, _ in names.most_common(12)), args.width))

    manifest = load_json(os.path.join(path, "artifact_manifest.json"))
    if isinstance(manifest, list):
        missing = [m.get("name") for m in manifest if isinstance(m, dict) and not m.get("exists")]
        if missing:
            out(clip("missing artifacts: " + ", ".join(missing), args.width))
    dumps = [f for f in ("trace.jsonl", "dom_dump.txt", "layout_dump.txt", "style_dump.txt", "paint_dump.txt",
                         "display_list.txt", "raw_source.html", "rendered_text.txt", "network.json",
                         "script_loading.json")
             if os.path.isfile(os.path.join(path, f))]
    out(clip("drill: fenlog.py summary|errors " + path + "  |  Grep (narrow pattern, head_limit) in: "
             + ", ".join(f"{f} {human_size(os.path.getsize(os.path.join(path, f)))}" for f in dumps), args.width * 2))


def cmd_missing_apis(args):
    root = os.path.join(args.logs, "missing_apis")
    if not os.path.isdir(root):
        sys.exit(f"no {root}")
    if not args.site:
        out("sites (fenlog.py missing-apis SITE for detail):")
        for host in sorted(os.listdir(root)):
            data = load_json(os.path.join(root, host, "missing_apis.json"))
            if isinstance(data, dict):
                out(f"  {host:<24} {len(data.get('records') or []):>4} records  generated {str(data.get('generatedAtUtc', ''))[:19]}")
        return
    hosts = [h for h in os.listdir(root) if args.site.lower() in h.lower()]
    if len(hosts) != 1:
        sys.exit(f"site '{args.site}' matches {hosts or 'nothing'}")
    data = load_json(os.path.join(root, hosts[0], "missing_apis.json")) or {}
    records = [r for r in data.get("records") or [] if isinstance(r, dict)]
    grouped = collections.OrderedDict()
    for r in records:
        grouped.setdefault(r.get("apiName"), []).append(r)
    out(f"{hosts[0]}: {len(records)} records, {len(grouped)} APIs ({data.get('siteUrl', '')})")
    for name, rs in sorted(grouped.items(), key=lambda kv: -len(kv[1]))[:args.limit]:
        first = rs[0]
        script = os.path.basename(str(first.get("scriptUrl") or "")) or "-"
        line = f":{first['line']}" if first.get("line") not in (None, "") else ""
        out(clip(f"  {len(rs):>3}x {name}  [{script}{line}]", args.width))


# ----------------------------------------------------------------------- main

def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except AttributeError:
        pass
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--logs", default="logs", help="logs directory (default: logs, relative to the repo root)")
    common.add_argument("--width", type=int, default=200, help="max characters per output line")
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("runs", parents=[common], help="list log runs, newest first")
    p.add_argument("-n", type=int, default=10)
    p.set_defaults(func=cmd_runs)

    p = sub.add_parser("summary", parents=[common], help="one-screen triage of a run")
    p.add_argument("run", nargs="?")
    p.add_argument("--limit", type=int, default=8)
    p.set_defaults(func=cmd_summary)

    p = sub.add_parser("errors", parents=[common], help="warn+ records collapsed into templates")
    p.add_argument("run", nargs="?")
    p.add_argument("--limit", type=int, default=25)
    p.add_argument("--level", default="warn", help="minimum level (warn, error)")
    p.set_defaults(func=cmd_errors)

    p = sub.add_parser("grep", parents=[common], help="regex over event+message, compact lines")
    p.add_argument("pattern")
    p.add_argument("run", nargs="?")
    p.add_argument("--cat", help="category prefix, e.g. JS, Network, EventLoop")
    p.add_argument("--level", help="minimum level")
    p.add_argument("--data", action="store_true", help="append the record's non-boilerplate fields")
    p.add_argument("--in-data", action="store_true", help="also match inside the fields")
    p.add_argument("--limit", type=int, default=40)
    p.set_defaults(func=cmd_grep)

    p = sub.add_parser("show", parents=[common], help="one record in full with neighbours")
    p.add_argument("seq")
    p.add_argument("run", nargs="?")
    p.add_argument("--context", type=int, default=3)
    p.set_defaults(func=cmd_show)

    p = sub.add_parser("slow", parents=[common], help="long tasks and slow JS jobs by worst case")
    p.add_argument("run", nargs="?")
    p.add_argument("--limit", type=int, default=15)
    p.set_defaults(func=cmd_slow)

    p = sub.add_parser("diff", parents=[common], help="what changed between two runs (verify a fix)")
    p.add_argument("old")
    p.add_argument("new")
    p.add_argument("--limit", type=int, default=15)
    p.set_defaults(func=cmd_diff)

    p = sub.add_parser("bundles", parents=[common], help="list debug-site bundles, newest first")
    p.add_argument("-n", type=int, default=10)
    p.add_argument("--site")
    p.set_defaults(func=cmd_bundles)

    p = sub.add_parser("bundle", parents=[common], help="condensed view of one debug-site bundle")
    p.add_argument("path", nargs="?", default="latest")
    p.add_argument("--site")
    p.add_argument("--limit", type=int, default=10)
    p.set_defaults(func=cmd_bundle)

    p = sub.add_parser("missing-apis", parents=[common], help="missing API report per site")
    p.add_argument("site", nargs="?")
    p.add_argument("--limit", type=int, default=20)
    p.set_defaults(func=cmd_missing_apis)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
