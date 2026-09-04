#!/usr/bin/env python3
"""Ask Chrome what HN's header row actually computes to.

Two reported defects need ground truth, not guesswork: the nav links render
centered instead of packed left after the logo, and the "login" link sits flush
against the right edge. Both are text-align / padding questions, so read the
real values off Chrome rather than reasoning from the UA stylesheet.

    python scripts/probe_hn_header.py
"""

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

PROBE = r"""
(() => {
  const out = {};
  const center = document.querySelector('center');
  const main = document.querySelector('#hnmain');
  const pick = (el) => {
    if (!el) return null;
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    return {
      tag: el.tagName.toLowerCase(),
      id: el.id || null,
      cls: el.className || null,
      textAlign: cs.textAlign,
      padding: cs.padding,
      paddingRight: cs.paddingRight,
      paddingLeft: cs.paddingLeft,
      display: cs.display,
      width: cs.width,
      marginLeft: cs.marginLeft,
      marginRight: cs.marginRight,
      rect: {x: Math.round(r.x), y: Math.round(r.y),
             w: Math.round(r.width), h: Math.round(r.height)},
      inlineStyle: el.getAttribute('style') || null,
      attrs: Object.fromEntries([...el.attributes].map(a => [a.name, a.value])),
    };
  };

  out.center = pick(center);
  out.hnmain = pick(main);

  // The header is the first row of #hnmain: logo cell, nav cell, login cell.
  const headerTable = main && main.querySelector('table');
  out.headerTable = pick(headerTable);
  const cells = headerTable ? [...headerTable.querySelectorAll('td')] : [];
  out.headerCells = cells.map(pick);

  const hnname = document.querySelector('.hnname');
  out.hnname = pick(hnname);
  const login = [...document.querySelectorAll('a')].find(a => a.textContent.trim() === 'login');
  out.login = pick(login);
  out.loginParentTd = login ? pick(login.closest('td')) : null;

  // First story title link, for the cursor/link geometry question.
  const story = document.querySelector('.titleline a');
  out.firstStory = pick(story);
  return out;
})()
"""


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


async def run(ws_url):
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
                   {"width": 1280, "height": 800, "deviceScaleFactor": 1,
                    "mobile": False})
        await send("Page.navigate", {"url": "https://news.ycombinator.com/"})
        await asyncio.sleep(6)
        r = await send("Runtime.evaluate",
                       {"expression": PROBE, "returnByValue": True,
                        "awaitPromise": False})
        if "result" not in r:
            raise RuntimeError(json.dumps(r)[:1200])
        inner = r["result"]
        if "exceptionDetails" in inner:
            raise RuntimeError(json.dumps(inner["exceptionDetails"])[:1200])
        return inner["result"]["value"]


def main():
    chrome = next((c for c in CHROME_CANDIDATES if os.path.isfile(c)), None)
    if not chrome:
        print("chrome not found")
        return 1
    port = free_port()
    profile = tempfile.mkdtemp(prefix="hnprobe_")
    proc = subprocess.Popen(
        [chrome, "--headless=new", f"--remote-debugging-port={port}",
         f"--user-data-dir={profile}", "--no-first-run", "--window-size=1280,800"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        # Runtime.evaluate lives on a page target, not the browser endpoint.
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
        data = asyncio.run(run(ws_url))
        print(json.dumps(data, indent=2))
    finally:
        proc.terminate()
    return 0


if __name__ == "__main__":
    sys.exit(main())
