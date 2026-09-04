#!/usr/bin/env python3
"""Ask Chrome for the used geometry and computed style of specific selectors.

`capture_chrome_site.py` gives a whole-page reference; this is the follow-up
question: for the three or four elements a defect actually turns on, what does
a real browser compute? Reading `position`, the four insets and the used rect
side by side is what separates "our height is wrong" from "our inset resolution
is wrong".

    python scripts/probe_chrome_selectors.py <url> <selector> [selector ...]
                                             [--settle-ms N] [--props a,b,c]

Selectors are passed to querySelector. Prefix one with `all:` to report every
match instead of the first. Paths are relative to the repository root.
"""

import argparse
import asyncio
import json
import os
import socket
import subprocess
import sys
import tempfile
import time

import requests
from websockets.asyncio.client import connect

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from capture_chrome_site import CHROME_CANDIDATES  # noqa: E402

VIEWPORT_W = 1280
VIEWPORT_H = 800

# The properties a layout defect is usually decided by. Anything else the
# caller can ask for with --props.
DEFAULT_PROPS = [
    "display", "position", "top", "right", "bottom", "left",
    "width", "height", "min-height", "max-height", "box-sizing",
    "margin", "padding", "flex-direction", "align-items", "justify-content",
    "flex", "flex-grow", "flex-shrink", "flex-basis", "overflow",
    "white-space", "text-overflow", "background-color", "z-index",
]

PROBE_TEMPLATE = r"""
(() => {
  const selectors = %SELECTORS%;
  const props = %PROPS%;
  const describe = (el) => {
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    const style = {};
    for (const p of props) style[p] = cs.getPropertyValue(p);
    return {
      tag: el.tagName.toLowerCase(),
      id: el.id || null,
      cls: typeof el.className === 'string' ? el.className : null,
      rect: {
        x: +r.x.toFixed(1), y: +r.y.toFixed(1),
        w: +r.width.toFixed(1), h: +r.height.toFixed(1),
      },
      scroll: {w: el.scrollWidth, h: el.scrollHeight},
      style,
    };
  };
  const out = {};
  for (const sel of selectors) {
    const all = sel.startsWith('all:');
    const q = all ? sel.slice(4) : sel;
    try {
      if (all) {
        out[sel] = [...document.querySelectorAll(q)].map(describe);
      } else {
        const el = document.querySelector(q);
        out[sel] = el ? describe(el) : null;
      }
    } catch (e) {
      out[sel] = {error: String(e)};
    }
  }
  return out;
})()
"""


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


async def run(ws_url, url, probe, settle_ms):
    async with connect(ws_url, max_size=64 * 1024 * 1024) as ws:
        mid = [0]

        async def send(method, params=None):
            mid[0] += 1
            await ws.send(json.dumps({"id": mid[0], "method": method,
                                      "params": params or {}}))
            while True:
                msg = json.loads(await ws.recv())
                if msg.get("id") == mid[0]:
                    return msg

        await send("Page.enable")
        await send("Runtime.enable")
        await send("Emulation.setDeviceMetricsOverride",
                   {"width": VIEWPORT_W, "height": VIEWPORT_H,
                    "deviceScaleFactor": 1, "mobile": False})
        await send("Page.navigate", {"url": url})
        await asyncio.sleep(settle_ms / 1000.0)
        r = await send("Runtime.evaluate",
                       {"expression": probe, "returnByValue": True,
                        "awaitPromise": False})
        if "result" not in r:
            raise RuntimeError(json.dumps(r)[:1200])
        inner = r["result"]
        if "exceptionDetails" in inner:
            raise RuntimeError(json.dumps(inner["exceptionDetails"])[:1200])
        return inner["result"]["value"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("url")
    ap.add_argument("selectors", nargs="+")
    ap.add_argument("--settle-ms", type=int, default=8000)
    ap.add_argument("--props", default=None,
                    help="comma-separated CSS property list (default: layout set)")
    args = ap.parse_args()

    props = args.props.split(",") if args.props else DEFAULT_PROPS
    probe = (PROBE_TEMPLATE
             .replace("%SELECTORS%", json.dumps(args.selectors))
             .replace("%PROPS%", json.dumps(props)))

    chrome = next((c for c in CHROME_CANDIDATES if os.path.isfile(c)), None)
    if not chrome:
        print("chrome not found")
        return 1
    port = free_port()
    profile = tempfile.mkdtemp(prefix="selprobe_")
    proc = subprocess.Popen(
        [chrome, "--headless=new", f"--remote-debugging-port={port}",
         f"--user-data-dir={profile}", "--no-first-run",
         f"--window-size={VIEWPORT_W},{VIEWPORT_H}"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        ws_url = None
        for _ in range(100):
            try:
                tabs = requests.get(f"http://127.0.0.1:{port}/json/list",
                                    timeout=1).json()
                page = next((t for t in tabs if t.get("type") == "page"), None)
                if page:
                    ws_url = page["webSocketDebuggerUrl"]
                    break
            except Exception:
                pass
            time.sleep(0.2)
        if not ws_url:
            print("chrome did not expose CDP")
            return 1
        print(json.dumps(asyncio.run(run(ws_url, args.url, probe,
                                         args.settle_ms)), indent=2))
    finally:
        proc.terminate()
    return 0


if __name__ == "__main__":
    sys.exit(main())
