#!/usr/bin/env python3
"""Read FenBrowser's own computed style for selected nodes over its CDP server.

Comparing a rendering defect against Chrome only tells you the target value; this
reads what our engine actually computed, so the two can be put side by side.
Requires the browser to be running with FEN_REMOTE_DEBUG=1.

    python scripts/probe_fen_css.py "#hnmain td" "span.pagetop"
"""

import json
import sys
import time

import requests
import websockets.sync.client as wsc

PORT = 9222
WANT = ("padding-right", "padding-left", "padding", "text-align",
        "width", "display", "margin-right")


def main():
    selectors = sys.argv[1:] or ["#hnmain"]
    try:
        tabs = requests.get(f"http://127.0.0.1:{PORT}/json/list", timeout=3).json()
    except Exception as exc:
        print(f"cannot reach FenBrowser CDP on {PORT}: {exc}")
        return 1
    page = next((t for t in tabs if t.get("type") == "page"), tabs[0] if tabs else None)
    if not page:
        print("no page target")
        return 1

    with wsc.connect(page["webSocketDebuggerUrl"], max_size=64 * 1024 * 1024) as ws:
        mid = [0]

        def call(method, params=None):
            mid[0] += 1
            ws.send(json.dumps({"id": mid[0], "method": method, "params": params or {}}))
            while True:
                msg = json.loads(ws.recv())
                if msg.get("id") == mid[0]:
                    return msg

        call("DOM.enable")
        call("CSS.enable")
        doc = call("DOM.getDocument", {"depth": -1})
        root = doc.get("result", {}).get("root", {}).get("nodeId")
        if root is None:
            print("no document root:", json.dumps(doc)[:400])
            return 1

        for sel in selectors:
            r = call("DOM.querySelector", {"nodeId": root, "selector": sel})
            nid = r.get("result", {}).get("nodeId")
            if not nid:
                print(f"{sel}: no match ({json.dumps(r)[:200]})")
                continue
            cs = call("CSS.getComputedStyleForNode", {"nodeId": nid})
            props = cs.get("result", {}).get("computedStyle") or []
            got = {p["name"]: p["value"] for p in props if p.get("name") in WANT}
            print(f"{sel}: " + ", ".join(f"{k}={v}" for k, v in sorted(got.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
