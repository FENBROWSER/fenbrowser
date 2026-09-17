#!/usr/bin/env python3
"""Mirror a captured debug-site bundle into a self-contained local tree.

Layout/CSS perf work needs the same page on every run, without network
variance and without the 30s CSS-fetch budget skewing the numbers. This takes
a bundle's raw_source.html, pulls its stylesheets down next to it, rewrites the
hrefs to local paths, and (optionally) strips scripts so a run measures
parse + cascade + layout only.

    python scripts/mirror_site.py logs/real-site/en.wikipedia.org/<runId> \
        --out scratch/mirror/wikipedia [--keep-scripts]
    python -m http.server 8099 --directory scratch/mirror/wikipedia
"""
from __future__ import annotations

import argparse
import html
import pathlib
import re
import sys
import urllib.parse
import urllib.request

LINK_RE = re.compile(r'<link\b[^>]*\brel="stylesheet"[^>]*>', re.I)
HREF_RE = re.compile(r'\bhref="([^"]+)"', re.I)
SCRIPT_RE = re.compile(r"<script\b.*?</script>", re.I | re.S)

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36")


def bundle_url(bundle: pathlib.Path) -> str:
    """Recover the page URL so relative stylesheet hrefs can be resolved."""
    summary = bundle / "summary.json"
    if summary.exists():
        import json
        data = json.loads(summary.read_text(encoding="utf-8-sig"))
        for key in ("FinalUrl", "Url", "RequestedUrl", "finalUrl", "url"):
            if data.get(key):
                return str(data[key])
    raise SystemExit(f"could not determine page url from {summary}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("bundle", type=pathlib.Path)
    ap.add_argument("--out", type=pathlib.Path, required=True)
    ap.add_argument("--keep-scripts", action="store_true")
    args = ap.parse_args()

    source = args.bundle / "raw_source.html"
    if not source.exists():
        raise SystemExit(f"missing {source}")

    page_url = bundle_url(args.bundle)
    doc = source.read_text(encoding="utf-8", errors="replace")
    out = args.out
    css_dir = out / "css"
    css_dir.mkdir(parents=True, exist_ok=True)

    seen: dict[str, str] = {}

    def fetch_css(tag: str) -> str:
        m = HREF_RE.search(tag)
        if not m:
            return tag
        href = html.unescape(m.group(1))
        if href in seen:
            return tag.replace(m.group(1), seen[href])
        absolute = urllib.parse.urljoin(page_url, href)
        name = f"sheet{len(seen)}.css"
        try:
            req = urllib.request.Request(absolute, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=60) as resp:
                body = resp.read()
        except Exception as exc:  # noqa: BLE001 - report and keep going
            print(f"  ! {absolute[:90]}: {exc}", file=sys.stderr)
            return tag
        (css_dir / name).write_bytes(body)
        local = f"css/{name}"
        seen[href] = local
        print(f"  + {local} <- {absolute[:90]} ({len(body)} bytes)")
        return tag.replace(m.group(1), local)

    doc = LINK_RE.sub(lambda m: fetch_css(m.group(0)), doc)
    if not args.keep_scripts:
        doc = SCRIPT_RE.sub("", doc)

    (out / "index.html").write_text(doc, encoding="utf-8")
    print(f"mirrored {page_url} -> {out / 'index.html'} "
          f"({len(doc)} bytes, {len(seen)} stylesheets)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
