---
name: fen-logs
description: Read and act on FenBrowser logs with minimal tokens. Use when investigating a page that fails, hangs, renders wrong or loads slowly; a script error, missing API, exception or long task; anything under logs/ (fenbrowser_*.jsonl, *_trace.jsonl, logs/real-site debug-site bundles, rendered_text/engine_source/raw_source dumps, missing_apis); or to confirm a fix by comparing runs.
---

# fen-logs

`logs/` holds hundreds of MB. A structured record is ~1 KB of mostly-null
context, `fenbrowser_<stamp>.jsonl` and its trace file are the same events
twice, and bundle files such as `compatibility_events.json` or `trace.jsonl`
run to 5-10 MB. Reading them directly burns the context for nothing.

## Rules

1. **Never Read or cat** `logs/*.jsonl`, bundle `trace.jsonl` / `logs.ndjson` /
   `summary.json` / `compatibility_events.json` / `event_loop.json` /
   `network.json`, or any `*.html` dump. Use `scripts/fenlog.py`; it reads one
   stream and prints bounded, deduplicated lines.
2. Go narrowest-first and keep `--limit` small; widen only when the answer is
   not there yet.
3. For text dumps (`layout_dump.txt`, `style_dump.txt`, `paint_dump.txt`,
   `raw_source.html`, `engine_source_*.html`) use the Grep tool with a specific
   pattern and `head_limit`, never a whole-file Read.
4. Run everything from the repo root.

## Loop

1. **Find evidence.** `python scripts/fenlog.py runs` (engine log runs) or
   `python scripts/fenlog.py bundles [--site HOST]` (debug-site bundles).
   For fresh evidence of a URL, prefer a bundle:
   `FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe debug-site <url> [settle_ms]`
   (writes `logs/real-site/<host>/<runId>/`); `diagnose <url> [settle_ms]` is
   the quicker report. The Host writes JSONL only with `FEN_LOG_PRESET=testrun`.
2. **Triage.** `fenlog.py bundle latest` for a bundle, or
   `fenlog.py summary [RUN]` for a log run. Both fit on one screen.
3. **Localize.**
   - `fenlog.py errors RUN` - warn+ collapsed to templates with count, first
     sequence, C# source and the JS error text.
   - `fenlog.py grep 'REGEX' RUN [--cat JS] [--level warn] [--data]`
   - `fenlog.py show SEQ RUN --context 5` - one record in full.
   - `fenlog.py slow RUN` - long tasks and slow JS jobs.
   - `fenlog.py missing-apis [SITE]`
4. **Map to code.** Use the printed `File.cs:line Member` and Grep that file.
   When no source prints, the record came through a logging shim: Grep the
   message prefix (e.g. `\[FenJsBridge\] Script error`) in the C# instead. A JS
   error's `script:line` points into the page: Grep the bundle's
   `raw_source.html` or the external script, not the whole file.
5. **Fix, rebuild, rerun the same command**, then
   `fenlog.py diff OLD_RUN NEW_RUN` - the template must be GONE and nothing
   NEW should appear. For bundles, compare `fenlog.py bundle` outputs.

RUN is a stamp (`20260913_181127`, or a unique prefix/suffix), a `.jsonl`
file, or a bundle directory; omit it for the newest run.

## Which artifact answers what

| Question | Look at |
|---|---|
| Did it navigate / finish loading? | `bundle` header, `summary` navigation line |
| What broke first? | `bundle` first_blocker + blocker signals |
| Script errors, rejections | `errors`, `bundle` exceptions |
| Unimplemented DOM/JS API | `missing-apis SITE`, `errors` collapsed API list |
| Slow load / frozen page | `slow`, then `grep LongTask` |
| Wrong layout or paint | Grep `layout_dump.txt` / `paint_dump.txt` for the element |
| Wrong text on the page | Grep `rendered_text*.txt` |
| Network failure | `grep 'fail|blocked|CSP|CORB' RUN --cat Network --data` |

## Gotchas

- Log stamps are local time; record timestamps are UTC and print as local.
- Newer trace files start with a UTF-8 BOM; the script handles it, `json.loads` does not.
- A run can have only a trace file (no main log) and the main log rotates into `.001.jsonl`.
- `EngineLogCompat.cs:120/123` is the logging shim, not the code that failed.
