"""Watch Chrome load the reCAPTCHA demo, so our own run has something to be
compared against rather than reasoned about.

Chrome is the reference for what the widget actually needs: which frames get
created and when, what postMessage traffic flows between them, and - the thing
our run never reaches - the moment the checkbox drops
`recaptcha-checkbox-loading` and becomes live.

Usage:
    python scripts/chrome_captcha_probe.py [url] [observe_seconds]

Writes a timeline to stdout. Requires the `websockets` package and Chrome.
"""

import asyncio
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

import websockets

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
PORT = 9333

# Injected into every frame before its own scripts run. Records the two things
# the comparison needs - message traffic and the checkbox's class - against a
# clock shared by every frame, so the anchor's transition can be placed relative
# to the messages that caused it.
PROBE = r"""
(() => {
  if (window.__fenProbe) return;
  const t0 = Date.now();
  const log = [];
  window.__fenProbe = log;
  const at = () => Date.now() - t0;

  const origPost = window.postMessage.bind(window);
  window.postMessage = function (msg, ...rest) {
    log.push({ t: at(), kind: "post-self", data: String(msg).slice(0, 40) });
    return origPost(msg, ...rest);
  };

  window.addEventListener("message", (e) => {
    log.push({
      t: at(),
      kind: "recv",
      data: String(e.data).slice(0, 40),
      ports: e.ports ? e.ports.length : 0,
      origin: e.origin,
    });
  }, true);

  // The checkbox is created by the anchor frame's own script, so poll for it
  // and then report only when its class actually changes.
  let lastClass = null;
  setInterval(() => {
    const el = document.getElementById("recaptcha-anchor");
    if (!el) return;
    if (el.className !== lastClass) {
      lastClass = el.className;
      log.push({ t: at(), kind: "checkbox", data: el.className });
    }
  }, 50);
})();
"""


async def cdp(url, seconds):
    profile = tempfile.mkdtemp(prefix="fen-chrome-")
    chrome = subprocess.Popen(
        [
            CHROME,
            "--headless=new",
            "--remote-debugging-port=%d" % PORT,
            "--user-data-dir=" + profile,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-gpu",
            "about:blank",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    try:
        target = None
        for _ in range(60):
            try:
                raw = urllib.request.urlopen("http://127.0.0.1:%d/json/list" % PORT, timeout=1).read()
                for entry in json.loads(raw):
                    if entry.get("type") == "page":
                        target = entry["webSocketDebuggerUrl"]
                        break
                if target:
                    break
            except Exception:
                pass
            time.sleep(0.5)

        if not target:
            print("could not reach Chrome's debugger")
            return

        async with websockets.connect(target, max_size=64 * 1024 * 1024) as ws:
            counter = [0]
            contexts = {}

            async def send(method, params=None):
                counter[0] += 1
                mid = counter[0]
                await ws.send(json.dumps({"id": mid, "method": method, "params": params or {}}))
                return mid

            async def wait_for(mid, deadline=25.0):
                end = time.time() + deadline
                while time.time() < end:
                    msg = json.loads(await asyncio.wait_for(ws.recv(), timeout=end - time.time()))
                    if msg.get("id") == mid:
                        return msg
                    note(msg)
                return None

            def note(msg):
                if msg.get("method") == "Runtime.executionContextCreated":
                    ctx = msg["params"]["context"]
                    contexts[ctx["id"]] = ctx.get("origin", "") + " " + str(
                        ctx.get("auxData", {}).get("frameId", ""))[:12]

            await wait_for(await send("Page.enable"))
            await wait_for(await send("Runtime.enable"))
            await wait_for(await send("Page.addScriptToEvaluateOnNewDocument", {"source": PROBE}))

            started = time.time()
            await wait_for(await send("Page.navigate", {"url": url}))

            # Drain events while the page runs so execution contexts for the
            # iframes are seen as they appear.
            while time.time() - started < seconds:
                try:
                    note(json.loads(await asyncio.wait_for(ws.recv(), timeout=1.0)))
                except asyncio.TimeoutError:
                    pass

            print("[chrome] frames/contexts seen: %d" % len(contexts))
            for cid, label in contexts.items():
                mid = await send(
                    "Runtime.evaluate",
                    {
                        "expression": "JSON.stringify(window.__fenProbe || [])",
                        "contextId": cid,
                        "returnByValue": True,
                    },
                )
                reply = await wait_for(mid, deadline=10.0)
                if not reply or "result" not in reply:
                    continue
                value = reply["result"].get("result", {}).get("value")
                if not value:
                    continue
                events = json.loads(value)
                if not events:
                    continue
                print("\n=== context %s (%s) — %d events ===" % (cid, label, len(events)))
                for e in events[:40]:
                    print("  %6dms %-9s %s" % (
                        e["t"], e["kind"],
                        (e.get("data") or "")[:200]
                        + (" ports=%d" % e["ports"] if e.get("ports") else "")))
    finally:
        chrome.terminate()
        try:
            chrome.wait(timeout=10)
        except Exception:
            chrome.kill()
        shutil.rmtree(profile, ignore_errors=True)


if __name__ == "__main__":
    target_url = sys.argv[1] if len(sys.argv) > 1 else "https://www.google.com/recaptcha/api2/demo"
    observe = float(sys.argv[2]) if len(sys.argv) > 2 else 12.0
    if not os.path.exists(CHROME):
        print("Chrome not found at " + CHROME)
        sys.exit(2)
    asyncio.run(cdp(target_url, observe))
