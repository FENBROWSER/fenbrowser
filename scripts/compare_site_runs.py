#!/usr/bin/env python3
"""Compare the FenBrowser and Chrome captures for each site.

Reads logs/<site>/fenbrowser/meta.json and logs/<site>/chrome/meta.json, writes
a per-site logs/<site>/compare.json, and renders one aggregate markdown report.

Usage:
    python scripts/compare_site_runs.py <site>[,<site>...] [--report <path>]
"""

import glob
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from collect_fen_site_logs import DomStats  # noqa: E402 - local helper, path set above

LOGS = "logs"
DEFAULT_REPORT = "Results/site-comparison/report.md"

# Fields present in both engines' meta, so they can be diffed directly.
SHARED = [
    ("domElements", "DOM elements"),
    ("scripts", "Scripts in DOM"),
    ("stylesheets", "Stylesheets"),
    ("iframes", "Iframes"),
    ("textLength", "Rendered text length"),
]


def parse_dom(path):
    """Measure a serialized document with the parser used on both engines.

    meta.json's textLength is not symmetric across engines - FenBrowser counts
    serialized text nodes while Chrome reports innerText, which omits hidden
    content. Running one parser over both saved documents removes that skew.
    """
    if not path or not os.path.isfile(path):
        return None
    try:
        raw = open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        return None
    parser = DomStats()
    try:
        parser.feed(raw)
    except Exception:  # noqa: BLE001 - malformed markup must not lose the run
        pass
    return {"bytes": len(raw), "elements": parser.elements,
            "textNodes": parser.texts, "textLength": parser.text_len,
            "title": parser.title}


def load(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, json.JSONDecodeError):
        return None


def ratio(fen, chrome):
    if not isinstance(fen, (int, float)) or not isinstance(chrome, (int, float)):
        return None
    if chrome == 0:
        return None if fen else 1.0
    return round(fen / chrome, 3)


def missing_api_records(site_dir):
    names = []
    for fn in os.listdir(site_dir):
        if fn.startswith("missing_apis") and fn.endswith(".json"):
            doc = load(os.path.join(site_dir, fn))
            for r in (doc or {}).get("records", []):
                name = r.get("apiName")
                if name:
                    names.append(name)
    return sorted(set(names))


def compare_site(site):
    fen_dir = os.path.join(LOGS, site, "fenbrowser")
    chrome_dir = os.path.join(LOGS, site, "chrome")
    fen = load(os.path.join(fen_dir, "meta.json"))
    chrome = load(os.path.join(chrome_dir, "meta.json"))
    if not fen or not chrome:
        return {"site": site, "error": "missing meta.json on one or both sides",
                "fenbrowser": bool(fen), "chrome": bool(chrome)}

    fc, cc = fen.get("counts", {}), chrome.get("counts", {})
    metrics = {}
    for key, label in SHARED:
        f, c = fc.get(key), cc.get(key)
        metrics[key] = {"label": label, "fenbrowser": f, "chrome": c,
                        "delta": (f - c) if isinstance(f, (int, float)) and isinstance(c, (int, float)) else None,
                        "ratio": ratio(f, c)}

    # Symmetric measurement: same parser over each engine's serialized document.
    fen_src = max(glob.glob(os.path.join(fen_dir, "engine_source_*.html")),
                  key=os.path.getsize, default=None)
    sym_fen = parse_dom(fen_src)
    sym_chrome = parse_dom(os.path.join(chrome_dir, "dom.html"))
    symmetric = {}
    if sym_fen and sym_chrome:
        for key in ("elements", "textNodes", "textLength", "bytes"):
            f, c = sym_fen.get(key), sym_chrome.get(key)
            symmetric[key] = {"fenbrowser": f, "chrome": c,
                              "delta": f - c, "ratio": ratio(f, c)}

    fen_err = load(os.path.join(fen_dir, "errors.json")) or {}
    apis = missing_api_records(fen_dir)

    same_url = (fen.get("finalUrl") or "").rstrip("/") == (chrome.get("finalUrl") or "").rstrip("/")
    same_title = (fen.get("title") or "").strip() == (chrome.get("title") or "").strip()

    dom_ratio = (symmetric.get("elements") or metrics["domElements"])["ratio"]
    text_ratio = (symmetric.get("textLength") or metrics["textLength"])["ratio"]
    if not same_url:
        verdict = "NAVIGATION MISMATCH"
    elif fc.get("crashSignals") or fc.get("outOfMemory"):
        verdict = "RENDERED BUT UNSTABLE"
    elif dom_ratio is not None and dom_ratio < 0.5:
        verdict = "UNDER-BUILT DOM"
    elif fc.get("errors") or fc.get("jsErrors"):
        verdict = "RENDERED WITH JS ERRORS"
    else:
        verdict = "CLEAN MATCH"

    return {
        "site": site,
        "requestedUrl": fen.get("requestedUrl"),
        "verdict": verdict,
        "navigation": {
            "fenFinalUrl": fen.get("finalUrl"),
            "chromeFinalUrl": chrome.get("finalUrl"),
            "sameFinalUrl": same_url,
            "fenTitle": fen.get("title"),
            "chromeTitle": chrome.get("title"),
            "sameTitle": same_title,
        },
        "metrics": metrics,
        "symmetricDom": symmetric,
        "symmetricSources": {"fenbrowser": fen_src,
                             "chrome": os.path.join(chrome_dir, "dom.html")},
        "domFidelity": dom_ratio,
        "textFidelity": text_ratio,
        "fenbrowserOnly": {
            "errors": fc.get("errors"),
            "warnings": fc.get("warnings"),
            "jsErrors": fc.get("jsErrors"),
            "outOfMemory": fc.get("outOfMemory"),
            "crashSignals": fc.get("crashSignals"),
            "blockedResources": fc.get("blockedResources"),
            "logRecords": fc.get("logRecords"),
            "resourceEvents": fc.get("resourceEvents"),
            "missingApis": apis,
            "topJsErrors": [e.get("message", "")[:300] for e in (fen_err.get("jsErrors") or [])[:5]],
            "topErrors": [e.get("message", "")[:300] for e in (fen_err.get("errors") or [])[:5]],
        },
        "chromeOnly": {
            "requests": cc.get("requests"),
            "requestFailures": cc.get("requestFailures"),
            "consoleErrors": cc.get("consoleErrors"),
            "uncaughtExceptions": cc.get("uncaughtExceptions"),
            "zeroAreaElements": cc.get("zeroAreaElements"),
            "userAgentOverridden": chrome.get("userAgentOverridden"),
            "timing": chrome.get("timing"),
            "statusHistogram": chrome.get("statusHistogram"),
        },
    }


def render(results):
    lines = ["# FenBrowser vs Chrome - live site comparison", ""]
    lines.append("One live FenBrowser session per site (window closed by hand), against a")
    lines.append("headless Chrome CDP capture of the same URL at 1280x800.")
    lines.append("")
    lines.append("DOM fidelity and text fidelity are measured by running one parser over")
    lines.append("each engine's own serialized document (FenBrowser `engine_source_*.html`,")
    lines.append("Chrome `dom.html`), so neither side's reporting convention skews the count.")
    lines.append("")
    lines.append("## Summary")
    lines.append("")
    lines.append("| Site | Verdict | DOM el. (Fen/Chrome) | DOM fidelity | Text chars (Fen/Chrome) | Text fidelity | Fen errors | JS errors | Crash |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for r in results:
        if r.get("error"):
            lines.append(f"| {r['site']} | {r['error']} | - | - | - | - | - | - | - |")
            continue
        m, f = r["metrics"], r["fenbrowserOnly"]
        sym = r.get("symmetricDom") or {}
        el = sym.get("elements") or m["domElements"]
        tx = sym.get("textLength") or m["textLength"]
        dr, tr = r["domFidelity"], r["textFidelity"]
        lines.append(
            f"| {r['site']} | {r['verdict']} | "
            f"{el['fenbrowser']} / {el['chrome']} | "
            f"{'-' if dr is None else f'{dr*100:.0f}%'} | "
            f"{tx['fenbrowser']} / {tx['chrome']} | "
            f"{'-' if tr is None else f'{tr*100:.0f}%'} | "
            f"{f['errors']} | {f['jsErrors']} | {f['crashSignals']} |"
        )
    lines.append("")

    for r in results:
        if r.get("error"):
            continue
        lines.append(f"## {r['site']} - {r['requestedUrl']}")
        lines.append("")
        nav = r["navigation"]
        lines.append(f"- Final URL: `{nav['fenFinalUrl']}` vs `{nav['chromeFinalUrl']}` "
                     f"({'match' if nav['sameFinalUrl'] else 'MISMATCH'})")
        lines.append(f"- Title: {'match' if nav['sameTitle'] else 'MISMATCH'} - `{nav['fenTitle']}`")
        lines.append("")
        lines.append("| Metric | FenBrowser | Chrome | Delta | Ratio |")
        lines.append("|---|---|---|---|---|")
        for key, _label in SHARED:
            m = r["metrics"][key]
            lines.append(f"| {m['label']} | {m['fenbrowser']} | {m['chrome']} | "
                         f"{m['delta'] if m['delta'] is not None else '-'} | "
                         f"{m['ratio'] if m['ratio'] is not None else '-'} |")
        for key, label in (("elements", "Elements (same parser)"),
                           ("textNodes", "Text nodes (same parser)"),
                           ("textLength", "Text chars (same parser)"),
                           ("bytes", "Serialized bytes")):
            m = (r.get("symmetricDom") or {}).get(key)
            if m:
                lines.append(f"| {label} | {m['fenbrowser']} | {m['chrome']} | "
                             f"{m['delta']} | {m['ratio']} |")
        lines.append("")
        f, c = r["fenbrowserOnly"], r["chromeOnly"]
        lines.append(f"- FenBrowser: {f['errors']} errors, {f['warnings']} warnings, {f['jsErrors']} JS errors, "
                     f"{f['outOfMemory']} OOM, {f['crashSignals']} crash signals, "
                     f"{f['logRecords']} log records")
        lines.append(f"- Chrome: {c['requests']} requests ({c['requestFailures']} failed), "
                     f"{c['consoleErrors']} console errors, {c['uncaughtExceptions']} uncaught exceptions")
        if f["missingApis"]:
            lines.append(f"- Missing APIs observed: {', '.join('`' + a + '`' for a in f['missingApis'][:20])}")
        for e in f["topJsErrors"]:
            lines.append(f"  - JS: `{e.splitlines()[0][:220]}`")
        for e in f["topErrors"]:
            lines.append(f"  - ERR: `{e.splitlines()[0][:220]}`")
        lines.append("")
    return "\n".join(lines)


def main():
    argv = list(sys.argv[1:])
    report = DEFAULT_REPORT
    if "--report" in argv:
        i = argv.index("--report")
        report = argv[i + 1]
        del argv[i:i + 2]
    if not argv:
        raise SystemExit(__doc__)
    sites = [s for chunk in argv for s in chunk.split(",") if s]

    results = []
    for site in sites:
        res = compare_site(site)
        results.append(res)
        if not res.get("error"):
            with open(os.path.join(LOGS, site, "compare.json"), "w", encoding="utf-8") as fh:
                json.dump(res, fh, indent=2, ensure_ascii=False)

    os.makedirs(os.path.dirname(report), exist_ok=True)
    text = render(results)
    with open(report, "w", encoding="utf-8") as fh:
        fh.write(text)

    enc = sys.stdout.encoding or "utf-8"
    print(text.encode(enc, "replace").decode(enc, "replace"))
    print(f"\nreport: {report}")


if __name__ == "__main__":
    main()
