#!/usr/bin/env python3
"""Acid3 progress probe via FenBrowser WebDriver.

Usage:
  python scripts/acid3_probe.py [--port 7400] [--url http://web-platform.test:8000/acid/acid3/test.html]
                                [--wait 30] [--js "return ..."] [--shot out.png]

Drives an already-running WebDriver server (`FenBrowser.Tooling webdriver --headless --port N`)
to load test.html, waits for the harness to finish (or stall), then prints the score,
the index the harness stopped at, and the failure log (the page's `log` global).
"""
import argparse, base64, json, time, urllib.request

def rq(base, method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode())

STATE = "return JSON.stringify({index: typeof index=='undefined'?null:index, score: typeof score=='undefined'?null:score, log: typeof log=='undefined'?null:log, wait: document.documentElement.className});"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=7400)
    ap.add_argument("--url", default="http://web-platform.test:8000/acid/acid3/test.html")
    ap.add_argument("--wait", type=float, default=30)
    ap.add_argument("--js", default=None)
    ap.add_argument("--shot", default=None)
    args = ap.parse_args()
    base = f"http://127.0.0.1:{args.port}"
    sid = rq(base, "POST", "/session", {"capabilities": {}})["value"]["sessionId"]
    s = f"{base}/session/{sid}"
    try:
        rq(s, "POST", "/url", {"url": args.url})
        last = None; same = 0; t0 = time.time()
        while time.time() - t0 < args.wait:
            st = json.loads(rq(s, "POST", "/execute/sync", {"script": STATE, "args": []})["value"])
            if "reftest-wait" not in (st["wait"] or ""):
                break
            if st["index"] == last:
                same += 1
                if same >= 6: break
            else:
                same = 0; last = st["index"]
            time.sleep(0.5)
        st = json.loads(rq(s, "POST", "/execute/sync", {"script": STATE, "args": []})["value"])
        print(f"index={st['index']} score={st['score']} class='{st['wait']}'")
        print(st["log"] or "(empty log)")
        if args.js:
            out = rq(s, "POST", "/execute/sync", {"script": args.js, "args": []})
            print(json.dumps(out.get("value"), indent=2))
        if args.shot:
            png = rq(s, "GET", "/screenshot")["value"]
            open(args.shot, "wb").write(base64.b64decode(png))
            print("wrote", args.shot)
    finally:
        try: rq(base, "DELETE", "/session/" + sid)
        except Exception: pass

if __name__ == "__main__":
    main()
