#!/usr/bin/env python3
"""Build a local fixture that isolates CSS cost from JS and network cost.

github.com peaks around 5 GB where wikipedia peaks around 450 MB, and it is the
one page in the set shipping ~2.4 MB of CSS across ~23 sheets. This downloads
those sheets once and builds a page that loads them against a small, fixed DOM
with no script at all, so a memory profile of it measures the cascade and
nothing else.

    python scripts/build_css_stress_fixture.py [--elements 900]

Writes Results/css-stress/ and prints the local URL to serve it from.
"""
import argparse
import json
import os
import urllib.request

OUT = os.path.join("Results", "css-stress")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--elements", type=int, default=900)
    ap.add_argument("--network", default=os.path.join("logs", "github", "chrome", "network.json"))
    args = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    net = json.load(open(args.network, encoding="utf-8"))
    css_urls = [r["url"] for r in net["requests"]
                if (r.get("mimeType") or "").startswith("text/css")]
    print(f"stylesheets referenced by the real page: {len(css_urls)}")

    local = []
    total = 0
    for i, url in enumerate(css_urls):
        name = f"sheet{i:02d}.css"
        path = os.path.join(OUT, name)
        if not os.path.exists(path):
            try:
                req = urllib.request.Request(
                    url, headers={"User-Agent": "Mozilla/5.0", "Accept-Encoding": "identity"})
                data = urllib.request.urlopen(req, timeout=30).read()
            except Exception as exc:  # noqa: BLE001 - a missing sheet just shrinks the fixture
                print(f"  skip {url[:70]}: {exc}")
                continue
            with open(path, "wb") as fh:
                fh.write(data)
        total += os.path.getsize(path)
        local.append(name)

    print(f"downloaded {len(local)} sheets, {total:,} bytes")

    # A small, fixed DOM. The point is many rules against few elements, which is
    # what the real page does: 905 elements against ~17,000 rules.
    body = []
    for i in range(args.elements):
        body.append(
            f'<div id="n{i}" class="Box color-fg-default d-flex flex-items-center px-3 py-2" '
            f'data-i="{i}"><span class="text-bold">item {i}</span></div>')

    links = "\n".join(f'<link rel="stylesheet" href="{n}">' for n in local)
    html = (
        "<!doctype html><html><head><meta charset='utf-8'>"
        "<title>CSS stress</title>\n" + links + "\n</head><body>\n"
        + "\n".join(body) + "\n</body></html>")

    with open(os.path.join(OUT, "index.html"), "w", encoding="utf-8") as fh:
        fh.write(html)

    # A no-CSS control with the identical DOM, to separate cascade cost from
    # parse/layout of the elements themselves.
    control = ("<!doctype html><html><head><meta charset='utf-8'>"
               "<title>CSS stress control</title></head><body>\n"
               + "\n".join(body) + "\n</body></html>")
    with open(os.path.join(OUT, "control.html"), "w", encoding="utf-8") as fh:
        fh.write(control)

    print(f"fixture : {OUT}/index.html  ({args.elements} elements, {len(local)} sheets)")
    print(f"control : {OUT}/control.html (same DOM, no CSS)")
    print("serve   : python -m http.server 8901 --directory Results/css-stress")


if __name__ == "__main__":
    main()
