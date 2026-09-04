#!/usr/bin/env python3
"""Harvest one live FenBrowser session's artifacts into a per-site log folder.

Run this after the live window has been closed. Every `logs/` artifact written
or touched during the session is copied out, and the structured streams are
reduced to a `meta.json` shaped like the Chrome capture's `meta.json` so the
two engines line up field-for-field.

Three record shapes are merged into one normalized stream:
  * `fenbrowser_<stamp>.jsonl`       - EngineLog records, severity in `severity`
  * `fenbrowser_trace_<stamp>.jsonl` - trace records, severity in `level`
  * host console stdout             - `HH:MM:SS.mmm [Subsystem][Severity] msg`

The console stream is not written under `logs/`, so it is passed in with
--stdout and copied in as `console_stdout.log`; on some runs it is the only
place a late crash is recorded.

Usage:
    python scripts/collect_fen_site_logs.py <out_dir> <marker_epoch> <url> [site] [--stdout <path>]
"""

import collections
import html.parser
import json
import os
import re
import shutil
import sys
from datetime import datetime, timezone

LOGS = "logs"
SLACK_SEC = 3.0  # filesystem timestamp granularity guard

# Artifacts owned by a different tool's run, not by the live host session.
EXCLUDE_NAMES = {"debug_site_screenshot.png"}

ERROR_LEVELS = {"ERROR", "FATAL", "CRITICAL"}
WARN_LEVELS = {"WARN", "WARNING"}


class DomStats(html.parser.HTMLParser):
    """Counts the serialized engine DOM.

    Only the first <title> is kept: inline SVG logos carry their own <title>
    children for accessibility, and concatenating them corrupts the page title.
    """

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.elements = 0
        self.texts = 0
        self.comments = 0
        self.tags = collections.Counter()
        self.text_len = 0
        self.title = None
        self._title_done = False
        self._in_title = False
        self._in_skip = 0
        self.scripts = []
        self.stylesheets = []
        self.iframes = []
        self._cur_script = None

    def handle_starttag(self, tag, attrs):
        self.elements += 1
        self.tags[tag] += 1
        a = dict(attrs)
        if tag == "title" and not self._title_done:
            self._in_title = True
        elif tag in ("script", "style"):
            self._in_skip += 1
            if tag == "script":
                self._cur_script = {
                    "src": a.get("src"),
                    "type": a.get("type"),
                    "async": "async" in a,
                    "defer": "defer" in a,
                    "inlineLength": 0,
                }
                self.scripts.append(self._cur_script)
        elif tag == "link" and "stylesheet" in (a.get("rel") or ""):
            self.stylesheets.append(a.get("href"))
        elif tag == "iframe":
            self.iframes.append({"src": a.get("src"), "name": a.get("name")})

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag in ("script", "style"):
            self._in_skip = max(0, self._in_skip - 1)

    def handle_endtag(self, tag):
        if tag == "title":
            if self._in_title:
                self._title_done = True
            self._in_title = False
        elif tag in ("script", "style"):
            self._in_skip = max(0, self._in_skip - 1)
            if tag == "script":
                self._cur_script = None

    def handle_data(self, data):
        if self._in_title:
            self.title = ((self.title or "") + data).strip()
            return
        if self._in_skip:
            if self._cur_script is not None and not self._cur_script["src"]:
                self._cur_script["inlineLength"] += len(data)
            return
        stripped = data.strip()
        if stripped:
            self.texts += 1
            self.text_len += len(stripped)

    def handle_comment(self, data):
        self.comments += 1


def newer_files(root, marker, recurse_dirs=()):
    out = []
    for name in sorted(os.listdir(root)):
        path = os.path.join(root, name)
        if os.path.isfile(path) and name not in EXCLUDE_NAMES:
            try:
                if os.path.getmtime(path) >= marker - SLACK_SEC:
                    out.append(path)
            except OSError:
                pass
    for sub in recurse_dirs:
        subdir = os.path.join(root, sub)
        if not os.path.isdir(subdir):
            continue
        for dirpath, _dirnames, filenames in os.walk(subdir):
            for fn in filenames:
                path = os.path.join(dirpath, fn)
                try:
                    if os.path.getmtime(path) >= marker - SLACK_SEC:
                        out.append(path)
                except OSError:
                    pass
    return out


def copy_artifacts(out_dir, marker, stdout_path=None):
    os.makedirs(out_dir, exist_ok=True)
    copied = []
    for src in newer_files(LOGS, marker, recurse_dirs=("missing_apis", "real-site")):
        rel = os.path.relpath(src, LOGS)
        dst = os.path.join(out_dir, rel.replace(os.sep, "__"))
        try:
            # Logging may still hold the write handle; copy through a shared read.
            with open(src, "rb") as fh_in, open(dst, "wb") as fh_out:
                shutil.copyfileobj(fh_in, fh_out)
            copied.append((rel, os.path.getsize(dst)))
        except OSError as exc:
            copied.append((rel, f"COPY_FAILED: {exc}"))
    if stdout_path and os.path.isfile(stdout_path):
        dst = os.path.join(out_dir, "console_stdout.log")
        try:
            with open(stdout_path, "rb") as fh_in, open(dst, "wb") as fh_out:
                shutil.copyfileobj(fh_in, fh_out)
            copied.append(("console_stdout.log", os.path.getsize(dst)))
        except OSError as exc:
            copied.append(("console_stdout.log", f"COPY_FAILED: {exc}"))
    return copied


def read_jsonl(path):
    rows = []
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    except OSError:
        pass
    return rows


CONSOLE_RE = re.compile(
    r"^(?P<ts>\d\d:\d\d:\d\d\.\d+)\s+\[(?P<subsystem>[^\]]+)\]\[(?P<level>[^\]]+)\]\s+(?P<msg>.*)$"
)
URL_RE = re.compile(r"https?://[^\s\"'<>)\]]+")


def normalize_json(row, stream):
    """Flatten either JSONL shape into one record."""
    data = row.get("data") or {}
    level = row.get("severity") or row.get("level") or ""
    return {
        "stream": stream,
        "ts": row.get("timestampUtc") or row.get("ts"),
        "level": str(level).upper(),
        "subsystem": row.get("subsystem") or data.get("subsystem"),
        "category": row.get("category"),
        "message": row.get("message") or row.get("event") or "",
        "data": data,
    }


def read_console(path):
    rows = []
    if not path or not os.path.isfile(path):
        return rows
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                m = CONSOLE_RE.match(line.rstrip("\n"))
                if not m:
                    continue
                msg = m.group("msg")
                source = None
                if " | source=" in msg:
                    msg, source = msg.rsplit(" | source=", 1)
                rows.append({
                    "stream": "console",
                    "ts": m.group("ts"),
                    "level": m.group("level").upper(),
                    "subsystem": m.group("subsystem"),
                    "category": None,
                    "message": msg,
                    "data": {"source": source} if source else {},
                })
    except OSError:
        pass
    return rows


def summarize(out_dir, url, site, marker):
    files = sorted(os.listdir(out_dir))
    main_logs = [f for f in files if re.match(r"fenbrowser_\d+_\d+\.jsonl$", f)]
    trace_logs = [f for f in files if f.endswith("_trace.jsonl") or f.startswith("fenbrowser_trace_")]
    sources = [f for f in files if f.startswith("engine_source_") and f.endswith(".html")]
    raw_sources = [f for f in files if f.startswith("raw_source_") and f.endswith(".html")]
    rendered_text = [f for f in files if f.startswith("rendered_text_")]
    screenshots = [f for f in files if f.endswith(".png")]
    console_log = "console_stdout.log" if "console_stdout.log" in files else None

    records = []
    for f in main_logs:
        records += [normalize_json(r, "engine") for r in read_jsonl(os.path.join(out_dir, f))]
    for f in trace_logs:
        records += [normalize_json(r, "trace") for r in read_jsonl(os.path.join(out_dir, f))]
    console_records = read_console(os.path.join(out_dir, console_log)) if console_log else []
    records += console_records

    levels = collections.Counter(r["level"] for r in records)
    subs = collections.Counter(r["subsystem"] for r in records)
    cats = collections.Counter(r["category"] for r in records if r["category"])

    def render(r, limit=1500):
        detail = ""
        d = r.get("data") or {}
        for key in ("error", "error_type", "exception", "message", "detail", "reason"):
            if key in d and d[key] not in (None, ""):
                detail += f" | {key}={d[key]}"
        return (r["message"] + detail)[:limit]

    errors, warnings = [], []
    for r in records:
        row = {"ts": r["ts"], "stream": r["stream"], "subsystem": r["subsystem"],
               "category": r["category"], "message": render(r)}
        if r["level"] in ERROR_LEVELS:
            errors.append(row)
        elif r["level"] in WARN_LEVELS:
            warnings.append(row)

    text_all = "\n".join(render(r, 4000) for r in records)

    js_records = [r for r in records if r["subsystem"] in ("Js", "JS") or r["category"] in ("JS", "ScriptLoader")]
    js_exceptions = [
        {"ts": r["ts"], "level": r["level"], "message": render(r)}
        for r in js_records
        if r["level"] in ERROR_LEVELS
        or re.search(r"exception|uncaught|TypeError|ReferenceError|SyntaxError|is not a function", render(r), re.I)
    ]
    oom = [{"ts": r["ts"], "message": render(r)} for r in records if "OutOfMemory" in render(r)]
    crashes = [
        {"ts": r["ts"], "message": render(r)}
        for r in records
        if re.search(r"\bCRASH\b|exited unexpectedly|Pipe is broken|dead active process|failure bundle", render(r))
    ]
    blocked = [
        {"ts": r["ts"], "message": render(r)}
        for r in records
        if re.search(r"\[CORB\]|Blocked|CSP|refused to", render(r), re.I)
    ]
    missing_apis = sorted({m[:300] for m in re.findall(r"[Mm]issing[ -]?API[^\n]*", text_all)})

    net_records = [r for r in records if r["subsystem"] in ("Net", "Network", "Fetch")
                   or r["category"] in ("Network", "ResourceLoader")]
    # The engine logger redacts resource URLs to origin + path length, so the
    # structured fields carry more than the rendered message ever will.
    net_urls = collections.Counter()
    origins = collections.Counter()
    resource_events = collections.Counter()
    discovery_types = collections.Counter()
    resources = []
    for r in net_records:
        d = r.get("data") or {}
        ru = d.get("resourceUrl") or d.get("resource_url")
        if ru:
            origins[str(ru)[:300]] += 1
            resources.append({
                "ts": r["ts"],
                "event": d.get("event") or r["message"],
                "origin": str(ru)[:300],
                "pathLength": d.get("resourcePathLength"),
                "discoveryType": d.get("discoveryType"),
            })
        ev = d.get("event") or r["message"]
        if ev:
            resource_events[str(ev)[:80]] += 1
        if d.get("discoveryType"):
            discovery_types[str(d["discoveryType"])] += 1
        for u in URL_RE.findall(render(r, 4000)):
            net_urls[u[:300]] += 1
    http_status = collections.Counter(re.findall(r"\b(?:status|Status|HTTP)[ =:/]+(\d{3})\b", text_all))

    nav_events = [
        {"ts": r["ts"], "message": render(r, 500)}
        for r in records
        if r["subsystem"] == "Nav" or r["category"] == "Navigation"
    ]

    # DOM evidence from the engine's own serialized document (largest wins).
    dom = None
    best = None
    for f in sources:
        p = os.path.join(out_dir, f)
        try:
            size = os.path.getsize(p)
        except OSError:
            continue
        if best is None or size > best[0]:
            best = (size, f)
    if best:
        source_used = best[1]
        raw = open(os.path.join(out_dir, source_used), encoding="utf-8", errors="replace").read()
        header = re.match(r"<!--\s*URL:\s*(.*?)\s*-->", raw)
        parser = DomStats()
        try:
            parser.feed(raw)
        except Exception:  # noqa: BLE001 - malformed markup must not lose the run
            pass
        dom = {
            "sourceFile": source_used,
            "documentUrl": header.group(1) if header else None,
            "bytes": len(raw),
            "elements": parser.elements,
            "textNodes": parser.texts,
            "comments": parser.comments,
            "textLength": parser.text_len,
            "title": parser.title,
            "topTags": parser.tags.most_common(40),
            "scripts": parser.scripts,
            "stylesheets": parser.stylesheets,
            "iframes": parser.iframes,
        }

    # Longest rendered-text dump is the best "what did the user actually see".
    rendered_best, rendered_len = None, 0
    for f in rendered_text:
        p = os.path.join(out_dir, f)
        try:
            size = os.path.getsize(p)
        except OSError:
            continue
        if size > rendered_len:
            rendered_len, rendered_best = size, f

    stamps = [r["ts"] for r in records if r["ts"] and r["stream"] != "console"]
    meta = {
        "engine": "fenbrowser",
        "site": site,
        "requestedUrl": url,
        "finalUrl": (dom or {}).get("documentUrl"),
        "title": (dom or {}).get("title"),
        "sessionStartedUtc": min(stamps) if stamps else None,
        "sessionEndedUtc": max(stamps) if stamps else None,
        "harvestedUtc": datetime.now(timezone.utc).isoformat(),
        "markerEpoch": marker,
        "counts": {
            "logRecords": len(records),
            "consoleRecords": len(console_records),
            "errors": len(errors),
            "warnings": len(warnings),
            "jsErrors": len(js_exceptions),
            "outOfMemory": len(oom),
            "crashSignals": len(crashes),
            "blockedResources": len(blocked),
            "missingApiLines": len(missing_apis),
            "distinctNetworkUrls": len(net_urls),
            "resourceOrigins": len(origins),
            "resourceEvents": sum(resource_events.values()),
            "domElements": (dom or {}).get("elements"),
            "domTextNodes": (dom or {}).get("textNodes"),
            "textLength": (dom or {}).get("textLength"),
            "renderedTextBytes": rendered_len,
            "scripts": len((dom or {}).get("scripts") or []),
            "stylesheets": len((dom or {}).get("stylesheets") or []),
            "iframes": len((dom or {}).get("iframes") or []),
        },
        "levelHistogram": dict(levels),
        "subsystemHistogram": dict(subs),
        "traceCategoryHistogram": dict(cats),
        "httpStatusMentions": dict(http_status),
        "resourceEventHistogram": dict(resource_events.most_common(40)),
        "discoveryTypeHistogram": dict(discovery_types),
        "resourceOriginHistogram": dict(origins.most_common(60)),
        "artifacts": {
            "mainLogs": main_logs,
            "traceLogs": trace_logs,
            "consoleLog": console_log,
            "engineSources": sources,
            "rawSources": raw_sources,
            "renderedText": rendered_text,
            "renderedTextBest": rendered_best,
            "screenshots": screenshots,
            "allFiles": files,
        },
        "dom": dom,
    }

    def dump(name, obj):
        with open(os.path.join(out_dir, name), "w", encoding="utf-8") as fh:
            json.dump(obj, fh, indent=2, ensure_ascii=False)

    dump("meta.json", meta)
    dump("errors.json", {
        "errors": errors,
        "warnings": warnings,
        "jsErrors": js_exceptions,
        "outOfMemory": oom,
        "crashSignals": crashes,
        "blockedResources": blocked,
        "missingApis": missing_apis,
    })
    dump("network.json", {
        "note": "Engine logs redact resource URLs to origin + path length; "
                "full request URLs are not recoverable from this stream.",
        "origins": [{"origin": o, "count": c} for o, c in origins.most_common(500)],
        "resourceEvents": dict(resource_events.most_common(60)),
        "discoveryTypes": dict(discovery_types),
        "resources": resources[:2000],
        "urlsInMessages": [{"url": u, "mentions": c} for u, c in net_urls.most_common(200)],
        "logLines": [{"ts": r["ts"], "message": render(r, 1000)} for r in net_records][:1000],
    })
    dump("navigation.json", {"events": nav_events[:500]})
    return meta


def out(text):
    """Console-safe print: artifact text may hold non-cp1252 characters."""
    enc = sys.stdout.encoding or "utf-8"
    print(str(text).encode(enc, "replace").decode(enc, "replace"))


def main():
    argv = list(sys.argv[1:])
    stdout_path = None
    if "--stdout" in argv:
        i = argv.index("--stdout")
        stdout_path = argv[i + 1]
        del argv[i:i + 2]
    if len(argv) < 3:
        raise SystemExit(__doc__)
    out_dir, marker, url = argv[0], float(argv[1]), argv[2]
    site = argv[3] if len(argv) > 3 else os.path.basename(os.path.dirname(out_dir))

    copied = copy_artifacts(out_dir, marker, stdout_path)
    meta = summarize(out_dir, url, site, marker)
    out(f"copied {len(copied)} artifact(s) to {out_dir}")
    for rel, size in copied:
        out(f"  {rel}  {size}")
    out(json.dumps(meta["counts"], indent=2))
    out(f"final url : {meta['finalUrl']}")
    out(f"title     : {meta['title']}")


if __name__ == "__main__":
    main()
