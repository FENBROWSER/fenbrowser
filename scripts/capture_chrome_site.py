#!/usr/bin/env python3
"""Capture a reference browsing run of one site with headless Chrome over CDP.

Emits the same evidence families the FenBrowser diagnostic bundle carries
(navigation, network, console/exceptions, scripts, DOM, layout, paint) so the
two engines can be compared artifact-for-artifact.

Usage:
    python scripts/capture_chrome_site.py <url> <out_dir> [settle_ms]

Paths are relative to the repository root (the process working directory).
"""

import asyncio
import base64
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone

import requests
from websockets.asyncio.client import connect

VIEWPORT_W = int(os.environ.get("CHROME_CAPTURE_W", "1280"))
VIEWPORT_H = int(os.environ.get("CHROME_CAPTURE_H", "800"))
DEFAULT_SETTLE_MS = 15000
NETWORK_IDLE_MS = 2000

CHROME_CANDIDATES = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
]


def find_chrome():
    for path in CHROME_CANDIDATES:
        if os.path.isfile(path):
            return path
    found = shutil.which("chrome") or shutil.which("google-chrome")
    if found:
        return found
    raise SystemExit("chrome.exe not found")


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_for_devtools(port, timeout=30):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            r = requests.get(f"http://127.0.0.1:{port}/json/version", timeout=2)
            if r.ok:
                return r.json()
        except Exception as exc:  # noqa: BLE001 - polling a starting process
            last = exc
        time.sleep(0.2)
    raise SystemExit(f"chrome devtools endpoint never came up: {last}")


class Cdp:
    """Minimal flat-session CDP client."""

    def __init__(self, ws):
        self.ws = ws
        self.next_id = 0
        self.pending = {}
        self.events = []
        self.session_id = None
        self._reader = None

    def start(self):
        self._reader = asyncio.create_task(self._read_loop())

    async def _read_loop(self):
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                if "id" in msg:
                    fut = self.pending.pop(msg["id"], None)
                    if fut and not fut.done():
                        fut.set_result(msg)
                else:
                    msg["_recv_ms"] = time.time() * 1000.0
                    self.events.append(msg)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 - connection teardown races
            pass

    async def send(self, method, params=None, session=True, timeout=30):
        self.next_id += 1
        mid = self.next_id
        payload = {"id": mid, "method": method, "params": params or {}}
        if session and self.session_id:
            payload["sessionId"] = self.session_id
        fut = asyncio.get_running_loop().create_future()
        self.pending[mid] = fut
        await self.ws.send(json.dumps(payload))
        try:
            msg = await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            self.pending.pop(mid, None)
            return {"error": {"message": f"timeout waiting for {method}"}}
        if "error" in msg:
            return {"error": msg["error"]}
        return msg.get("result", {})

    def drain(self, method):
        return [e for e in self.events if e.get("method") == method]


EVAL_PAGE_STATE = r"""
(() => {
  const out = {};
  out.url = location.href;
  out.title = document.title;
  out.readyState = document.readyState;
  out.characterSet = document.characterSet;
  out.compatMode = document.compatMode;
  out.docElementClass = document.documentElement ? document.documentElement.className : null;

  let nodes = 0, elements = 0, texts = 0, comments = 0;
  const tags = {};
  const walk = (root) => {
    const it = document.createNodeIterator(root, NodeFilter.SHOW_ALL);
    let n;
    while ((n = it.nextNode())) {
      nodes++;
      if (n.nodeType === 1) {
        elements++;
        const t = n.tagName.toLowerCase();
        tags[t] = (tags[t] || 0) + 1;
      } else if (n.nodeType === 3) texts++;
      else if (n.nodeType === 8) comments++;
    }
  };
  walk(document);
  out.dom = { nodes, elements, texts, comments, topTags: Object.entries(tags).sort((a,b)=>b[1]-a[1]).slice(0,40) };

  out.scripts = Array.from(document.querySelectorAll('script')).map((s, i) => ({
    index: i,
    src: s.src || null,
    type: s.type || null,
    async: s.async,
    defer: s.defer,
    module: s.type === 'module',
    inlineLength: s.src ? 0 : (s.textContent || '').length,
  }));
  out.stylesheets = Array.from(document.querySelectorAll('link[rel~="stylesheet"]')).map(l => l.href);
  out.iframes = Array.from(document.querySelectorAll('iframe')).map(f => ({ src: f.src || null, name: f.name || null }));
  out.images = document.images.length;
  out.forms = document.forms.length;

  const t = performance.timing || {};
  const nav = performance.getEntriesByType('navigation')[0] || null;
  out.timing = nav ? {
    type: nav.type,
    protocol: nav.nextHopProtocol,
    domInteractive: nav.domInteractive,
    domContentLoaded: nav.domContentLoadedEventEnd,
    loadEventEnd: nav.loadEventEnd,
    responseEnd: nav.responseEnd,
    transferSize: nav.transferSize,
    encodedBodySize: nav.encodedBodySize,
    decodedBodySize: nav.decodedBodySize,
  } : {
    domContentLoaded: t.domContentLoadedEventEnd - t.navigationStart,
    loadEventEnd: t.loadEventEnd - t.navigationStart,
  };
  out.resourceEntries = performance.getEntriesByType('resource').length;

  const paints = {};
  for (const p of performance.getEntriesByType('paint')) paints[p.name] = p.startTime;
  out.paint = paints;

  out.frameworks = {
    react: !!(window.React || document.querySelector('[data-reactroot],#root,#__next')),
    next: !!window.__NEXT_DATA__,
    vue: !!window.Vue || !!document.querySelector('[data-v-app],#app[data-v-app]'),
    angular: !!window.ng || !!document.querySelector('[ng-version]'),
    jquery: !!window.jQuery,
    webpack: !!(window.webpackChunk || window.webpackJsonp),
  };

  out.scroll = {
    width: document.documentElement.scrollWidth,
    height: document.documentElement.scrollHeight,
    clientWidth: document.documentElement.clientWidth,
    clientHeight: document.documentElement.clientHeight,
  };

  const bodyText = (document.body && document.body.innerText) || '';
  out.textLength = bodyText.length;
  out.textSample = bodyText.slice(0, 4000);
  return out;
})()
"""

EVAL_LAYOUT = r"""
(() => {
  const rows = [];
  const seen = new Set();
  const push = (el) => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    rows.push({
      tag: el.tagName.toLowerCase(),
      id: el.id || null,
      cls: (el.className && typeof el.className === 'string') ? el.className.slice(0, 120) : null,
      x: Math.round(r.x * 100) / 100,
      y: Math.round(r.y * 100) / 100,
      w: Math.round(r.width * 100) / 100,
      h: Math.round(r.height * 100) / 100,
      display: cs.display,
      position: cs.position,
      fontSize: cs.fontSize,
      color: cs.color,
      background: cs.backgroundColor,
      visible: r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none',
    });
  };
  // Landmark elements first, then the widest-covering blocks.
  for (const sel of ['html','body','header','nav','main','footer','h1','h2','form','table']) {
    for (const el of Array.from(document.querySelectorAll(sel)).slice(0, 8)) {
      if (!seen.has(el)) { seen.add(el); push(el); }
    }
  }
  const all = Array.from(document.querySelectorAll('*'))
    .map(el => [el, el.getBoundingClientRect()])
    .filter(([, r]) => r.width > 40 && r.height > 20)
    .sort((a, b) => (b[1].width * b[1].height) - (a[1].width * a[1].height))
    .slice(0, 120);
  for (const [el] of all) { if (!seen.has(el)) { seen.add(el); push(el); } }
  let zeroArea = 0;
  for (const el of document.querySelectorAll('body *')) {
    const r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) zeroArea++;
  }
  return { boxes: rows, zeroAreaElements: zeroArea };
})()
"""


async def capture(url, out_dir, settle_ms):
    os.makedirs(out_dir, exist_ok=True)
    chrome = find_chrome()
    port = free_port()
    profile = tempfile.mkdtemp(prefix="fen-chrome-")
    args = [
        chrome,
        "--headless=new",
        f"--remote-debugging-port={port}",
        f"--user-data-dir={profile}",
        f"--window-size={VIEWPORT_W},{VIEWPORT_H}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        "--disable-features=Translate,MediaRouter",
        "--hide-scrollbars",
        "about:blank",
    ]
    proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    run_started = datetime.now(timezone.utc)
    wall_start = time.time()
    try:
        version = wait_for_devtools(port)
        async with connect(version["webSocketDebuggerUrl"], max_size=None) as ws:
            cdp = Cdp(ws)
            cdp.start()

            target = await cdp.send("Target.createTarget", {"url": "about:blank"}, session=False)
            attached = await cdp.send(
                "Target.attachToTarget",
                {"targetId": target["targetId"], "flatten": True},
                session=False,
            )
            cdp.session_id = attached["sessionId"]

            await cdp.send("Page.enable")
            await cdp.send("Network.enable")
            # Some sites (x.com) 403 the "HeadlessChrome" token outright, which
            # would make Chrome a worse reference than the engine under test.
            # Present the equivalent headful UA so the comparison is fair.
            ua = (version.get("User-Agent") or "").replace("HeadlessChrome", "Chrome")
            if ua:
                await cdp.send(
                    "Network.setUserAgentOverride",
                    {"userAgent": ua, "acceptLanguage": "en-US,en"},
                )
            await cdp.send("Runtime.enable")
            await cdp.send("Log.enable")
            await cdp.send("Page.setLifecycleEventsEnabled", {"enabled": True})
            await cdp.send(
                "Emulation.setDeviceMetricsOverride",
                {"width": VIEWPORT_W, "height": VIEWPORT_H, "deviceScaleFactor": 1, "mobile": False},
            )

            nav_started = time.time()
            nav = await cdp.send("Page.navigate", {"url": url}, timeout=60)
            nav_error = nav.get("error") or nav.get("errorText")

            # Settle: wait for load, then for a quiet network window.
            deadline = nav_started + settle_ms / 1000.0
            last_activity = time.time()
            seen = 0
            loaded = False
            while time.time() < deadline:
                await asyncio.sleep(0.25)
                total = len(cdp.events)
                if total != seen:
                    seen = total
                    last_activity = time.time()
                if not loaded and cdp.drain("Page.loadEventFired"):
                    loaded = True
                if loaded and (time.time() - last_activity) * 1000 > NETWORK_IDLE_MS:
                    break

            state = await cdp.send(
                "Runtime.evaluate",
                {"expression": EVAL_PAGE_STATE, "returnByValue": True, "awaitPromise": False},
                timeout=60,
            )
            page_state = (state.get("result") or {}).get("value") if "error" not in state else {"evalError": state["error"]}

            layout = await cdp.send(
                "Runtime.evaluate",
                {"expression": EVAL_LAYOUT, "returnByValue": True},
                timeout=60,
            )
            layout_state = (layout.get("result") or {}).get("value") if "error" not in layout else {"evalError": layout["error"]}

            html = await cdp.send(
                "Runtime.evaluate",
                {"expression": "document.documentElement.outerHTML", "returnByValue": True},
                timeout=60,
            )
            html_text = (html.get("result") or {}).get("value") or ""

            shot = await cdp.send("Page.captureScreenshot", {"format": "png"}, timeout=60)
            if "data" in shot:
                with open(os.path.join(out_dir, "screenshot.png"), "wb") as fh:
                    fh.write(base64.b64decode(shot["data"]))

            full = await cdp.send(
                "Page.captureScreenshot",
                {"format": "png", "captureBeyondViewport": True},
                timeout=60,
            )
            if "data" in full:
                with open(os.path.join(out_dir, "screenshot_full.png"), "wb") as fh:
                    fh.write(base64.b64decode(full["data"]))

            # ---- Network reconstruction -------------------------------------
            requests_by_id = {}
            for e in cdp.drain("Network.requestWillBeSent"):
                p = e["params"]
                requests_by_id[p["requestId"]] = {
                    "requestId": p["requestId"],
                    "url": p["request"]["url"][:2000],
                    "method": p["request"]["method"],
                    "type": p.get("type"),
                    "initiator": (p.get("initiator") or {}).get("type"),
                    "documentURL": p.get("documentURL", "")[:500],
                    "redirectChain": [],
                    "status": None,
                    "mimeType": None,
                    "protocol": None,
                    "fromCache": None,
                    "encodedDataLength": None,
                    "failed": None,
                }
                rp = p.get("redirectResponse")
                if rp:
                    requests_by_id[p["requestId"]]["redirectChain"].append(
                        {"url": rp.get("url", "")[:500], "status": rp.get("status")}
                    )
            for e in cdp.drain("Network.responseReceived"):
                p = e["params"]
                r = requests_by_id.setdefault(p["requestId"], {"requestId": p["requestId"], "url": p["response"]["url"][:2000]})
                resp = p["response"]
                r["status"] = resp.get("status")
                r["statusText"] = resp.get("statusText")
                r["mimeType"] = resp.get("mimeType")
                r["protocol"] = resp.get("protocol")
                r["fromCache"] = resp.get("fromDiskCache") or resp.get("fromServiceWorker")
                r["remoteIP"] = resp.get("remoteIPAddress")
                r["securityState"] = resp.get("securityState")
                r["type"] = p.get("type", r.get("type"))
            for e in cdp.drain("Network.loadingFinished"):
                p = e["params"]
                r = requests_by_id.get(p["requestId"])
                if r:
                    r["encodedDataLength"] = p.get("encodedDataLength")
            for e in cdp.drain("Network.loadingFailed"):
                p = e["params"]
                r = requests_by_id.setdefault(p["requestId"], {"requestId": p["requestId"]})
                r["failed"] = {
                    "errorText": p.get("errorText"),
                    "canceled": p.get("canceled"),
                    "blockedReason": p.get("blockedReason"),
                }
            net = list(requests_by_id.values())
            failures = [r for r in net if r.get("failed")]
            statuses = {}
            for r in net:
                key = str(r.get("status") or ("FAILED" if r.get("failed") else "pending"))
                statuses[key] = statuses.get(key, 0) + 1

            # ---- Console / exceptions ---------------------------------------
            console = []
            for e in cdp.drain("Runtime.consoleAPICalled"):
                p = e["params"]
                parts = []
                for a in p.get("args", []):
                    if "value" in a:
                        parts.append(str(a["value"]))
                    elif a.get("description"):
                        parts.append(a["description"])
                    else:
                        parts.append(a.get("type", "?"))
                console.append({
                    "level": p.get("type"),
                    "text": " ".join(parts)[:2000],
                    "timestamp": p.get("timestamp"),
                    "url": ((p.get("stackTrace") or {}).get("callFrames") or [{}])[0].get("url"),
                })
            for e in cdp.drain("Log.entryAdded"):
                p = e["params"]["entry"]
                console.append({
                    "level": p.get("level"),
                    "source": p.get("source"),
                    "text": (p.get("text") or "")[:2000],
                    "url": p.get("url"),
                    "timestamp": p.get("timestamp"),
                })
            exceptions = []
            for e in cdp.drain("Runtime.exceptionThrown"):
                d = e["params"]["exceptionDetails"]
                exceptions.append({
                    "text": d.get("text"),
                    "description": ((d.get("exception") or {}).get("description") or "")[:2000],
                    "className": (d.get("exception") or {}).get("className"),
                    "url": d.get("url"),
                    "line": d.get("lineNumber"),
                    "column": d.get("columnNumber"),
                })

            lifecycle = [
                {"name": e["params"].get("name"), "timestamp": e["params"].get("timestamp")}
                for e in cdp.drain("Page.lifecycleEvent")
            ]
            frames = [
                {"url": e["params"]["frame"].get("url", "")[:500], "id": e["params"]["frame"].get("id")}
                for e in cdp.drain("Page.frameNavigated")
            ]

            errors_console = [c for c in console if str(c.get("level")) in ("error", "assert")]
            warnings_console = [c for c in console if str(c.get("level")) in ("warning", "warn")]

            meta = {
                "engine": "chrome",
                "chrome": version.get("Browser"),
                "userAgent": ua or version.get("User-Agent"),
                "userAgentOverridden": bool(ua),
                "protocolVersion": version.get("Protocol-Version"),
                "requestedUrl": url,
                "finalUrl": (page_state or {}).get("url"),
                "title": (page_state or {}).get("title"),
                "readyState": (page_state or {}).get("readyState"),
                "navigationError": nav_error,
                "loadEventFired": loaded,
                "settleMs": settle_ms,
                "wallClockMs": round((time.time() - wall_start) * 1000),
                "startedUtc": run_started.isoformat(),
                "viewport": {"width": VIEWPORT_W, "height": VIEWPORT_H},
                "counts": {
                    "requests": len(net),
                    "requestFailures": len(failures),
                    "consoleMessages": len(console),
                    "consoleErrors": len(errors_console),
                    "consoleWarnings": len(warnings_console),
                    "uncaughtExceptions": len(exceptions),
                    "domNodes": ((page_state or {}).get("dom") or {}).get("nodes"),
                    "domElements": ((page_state or {}).get("dom") or {}).get("elements"),
                    "scripts": len((page_state or {}).get("scripts") or []),
                    "stylesheets": len((page_state or {}).get("stylesheets") or []),
                    "iframes": len((page_state or {}).get("iframes") or []),
                    "textLength": (page_state or {}).get("textLength"),
                    "zeroAreaElements": (layout_state or {}).get("zeroAreaElements"),
                },
                "statusHistogram": statuses,
                "frameworks": (page_state or {}).get("frameworks"),
                "timing": (page_state or {}).get("timing"),
                "paint": (page_state or {}).get("paint"),
                "scroll": (page_state or {}).get("scroll"),
            }

            def dump(name, obj):
                with open(os.path.join(out_dir, name), "w", encoding="utf-8") as fh:
                    json.dump(obj, fh, indent=2, ensure_ascii=False)

            dump("meta.json", meta)
            dump("network.json", {"requests": net, "failures": failures, "statusHistogram": statuses})
            dump("console.json", {"messages": console, "exceptions": exceptions})
            dump("page_state.json", page_state)
            dump("layout.json", layout_state)
            dump("lifecycle.json", {"lifecycle": lifecycle, "frames": frames})
            with open(os.path.join(out_dir, "dom.html"), "w", encoding="utf-8") as fh:
                fh.write(html_text)
            with open(os.path.join(out_dir, "text.txt"), "w", encoding="utf-8") as fh:
                fh.write(((page_state or {}).get("textSample")) or "")

            print(json.dumps(meta["counts"], indent=2))
            print(f"final url : {meta['finalUrl']}")
            print(f"title     : {meta['title']}")
            print(f"artifacts : {out_dir}")
            return meta
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=10)
        except Exception:  # noqa: BLE001 - best-effort teardown
            proc.kill()
        shutil.rmtree(profile, ignore_errors=True)


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    url = sys.argv[1]
    out_dir = sys.argv[2]
    settle_ms = int(sys.argv[3]) if len(sys.argv) > 3 else DEFAULT_SETTLE_MS
    asyncio.run(capture(url, out_dir, settle_ms))


if __name__ == "__main__":
    main()
