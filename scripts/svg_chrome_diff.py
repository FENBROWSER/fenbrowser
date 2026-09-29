"""Compare the first-party SVG renderer against headless Chrome on a captured corpus.

Usage (from repo root):
    python scripts/svg_chrome_diff.py <capture dir> <output dir> [--chrome PATH] [--limit N]

<capture dir> is the output of `BenchSvg --capture-sites` (manifest.json + corpus/).
Each SVG is rendered by BenchSvg --inspect-svg (our pixels, natural size) and by
headless Chrome at the same size: inline captures (#inline-svg-N) are embedded in an
HTML page as they were on the site, linked files are loaded through <img>. Pixels
are compared premultiplied; a pixel differs when a channel moves by more than
DIFF_THRESHOLD, which absorbs antialiasing noise between Skia builds.

Writes <output dir>/report.json and report.md, plus ours/ and chrome/ PNGs for any
file that is not a match.
"""
import argparse
import html
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

DIFF_THRESHOLD = 48
MATCH_FRACTION = 0.005
CLOSE_FRACTION = 0.03
DEFAULT_CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
BENCH = Path("scripts/BenchSvg/bin/Release/net10.0/BenchSvg.exe")


def premultiplied(image):
    for r, g, b, a in image.get_flattened_data():
        yield (r * a + 127) // 255, (g * a + 127) // 255, (b * a + 127) // 255, a


def compare(ours, chrome):
    width, height = ours.size
    chrome = chrome.crop((0, 0, width, height)) if chrome.size != ours.size else chrome
    differing = 0
    ink_ours = ink_chrome = ink_both = 0
    for pa, pb in zip(premultiplied(ours), premultiplied(chrome)):
        if max(abs(x - y) for x, y in zip(pa, pb)) > DIFF_THRESHOLD:
            differing += 1
        a_on, b_on = pa[3] > 0, pb[3] > 0
        ink_ours += a_on
        ink_chrome += b_on
        ink_both += a_on and b_on
    total = max(1, width * height)
    union = ink_ours + ink_chrome - ink_both
    return {
        "differingFraction": round(differing / total, 5),
        "inkIoU": round(ink_both / union, 4) if union else 1.0,
        "inkOurs": ink_ours,
        "inkChrome": ink_chrome,
    }


def render_ours(svg_path, prefix, inline):
    # Inline captures go through FenBrowser's HTML parser like an inline <svg> on a page.
    mode = "--inspect-inline-svg" if inline else "--inspect-image-svg"
    out = subprocess.run([str(BENCH), mode, str(svg_path), "--output-prefix", str(prefix)],
                         capture_output=True, text=True, timeout=60)
    line = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else "{}"
    result = json.loads(line)
    png = Path(str(prefix) + ".png")
    return result, (Image.open(png).convert("RGBA") if result.get("admissible") and png.exists() else None)


def render_chrome(chrome, profile, svg_path, inline, width, height, out_png):
    page = Path(tempfile.gettempdir()) / f"svgdiff_{os.getpid()}.html"
    if inline:
        body = svg_path.read_text(encoding="utf-8", errors="replace")
    else:
        body = f'<img src="{html.escape(svg_path.resolve().as_uri())}" width="{width}" height="{height}" style="display:block">'
    # The outer inline <svg> is pinned to the size we rendered: its size on the real
    # page comes from page layout, which this comparison deliberately leaves out.
    page.write_text("<!doctype html><html><head><style>html,body{margin:0;background:transparent}"
                    f"body>svg{{display:block;width:{width}px;height:{height}px}}</style></head><body>"
                    + body + "</body></html>", encoding="utf-8")
    subprocess.run([chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                    f"--user-data-dir={profile}", "--force-device-scale-factor=1",
                    "--default-background-color=00000000", f"--window-size={width},{height}",
                    f"--screenshot={out_png}", page.as_uri()],
                   capture_output=True, text=True, timeout=60)
    return Image.open(out_png).convert("RGBA") if out_png.exists() else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture")
    parser.add_argument("output")
    parser.add_argument("--chrome", default=DEFAULT_CHROME)
    parser.add_argument("--limit", type=int, default=0)
    args = parser.parse_args()

    capture, output = Path(args.capture), Path(args.output)
    manifest = json.loads((capture / "manifest.json").read_text(encoding="utf-8-sig"))
    files = manifest["Files"][: args.limit or None]
    output.mkdir(parents=True, exist_ok=True)
    (output / "ours").mkdir(exist_ok=True)
    (output / "chrome").mkdir(exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="svgdiff_"))
    profile = work / "profile"

    rows = []
    try:
        for entry in files:
            rel = entry["Path"]
            svg_path = capture / "corpus" / rel
            inline = "#inline-svg-" in entry["SourceUrl"]
            stem = rel.replace("/", "_").removesuffix(".svg")
            ours_result, ours = render_ours(svg_path, work / f"{stem}.ours", inline)
            row = {"path": rel, "site": entry["SiteId"], "inline": inline,
                   "admissible": bool(ours_result.get("admissible")),
                   "rejection": ours_result.get("rejection")}
            if ours is None:
                row["verdict"] = "refused"
                rows.append(row)
                continue
            chrome = render_chrome(args.chrome, profile, svg_path, inline, ours.width, ours.height,
                                   work / f"{stem}.chrome.png")
            if chrome is None:
                row["verdict"] = "chrome-failed"
                rows.append(row)
                continue
            row.update({"width": ours.width, "height": ours.height}, **compare(ours, chrome))
            fraction = row["differingFraction"]
            row["verdict"] = "match" if fraction <= MATCH_FRACTION else "close" if fraction <= CLOSE_FRACTION else "different"
            if row["verdict"] != "match":
                ours.save(output / "ours" / f"{stem}.png")
                chrome.save(output / "chrome" / f"{stem}.png")
            rows.append(row)
            print(f"{row['verdict']:9} {fraction:8.4f} {rel}", flush=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    counts = {}
    for row in rows:
        counts[row["verdict"]] = counts.get(row["verdict"], 0) + 1
    (output / "report.json").write_text(json.dumps({"counts": counts, "files": rows}, indent=1), encoding="utf-8")
    lines = ["# First-party SVG vs Chrome", "", f"Files: {len(rows)}", ""]
    lines += [f"- {k}: {v}" for k, v in sorted(counts.items())]
    lines += ["", "| file | verdict | differing | ink IoU |", "|---|---|---:|---:|"]
    for row in sorted(rows, key=lambda r: -r.get("differingFraction", 1)):
        lines.append(f"| {row['path']} | {row['verdict']} | {row.get('differingFraction', '-')} | {row.get('inkIoU', '-')} |")
    (output / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(counts))
    return 0


if __name__ == "__main__":
    sys.exit(main())
