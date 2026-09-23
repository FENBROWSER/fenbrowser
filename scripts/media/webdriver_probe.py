"""Drive a running FenBrowser WebDriver server through one page and report what happened.

Usage: python scripts/media/webdriver_probe.py <port> <url> [seconds] [script]

Opens a session, navigates to <url>, waits, optionally runs <script> (a function body
that returns a value), and prints the title, the window handles and the result. Useful
to reproduce a WPT failure without wptrunner in the loop.
"""
import json
import sys
import time
import urllib.request


def call(port, method, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read() or b"{}")


def main():
    port, url = int(sys.argv[1]), sys.argv[2]
    seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 3
    script = sys.argv[4] if len(sys.argv) > 4 else None
    session = call(port, "POST", "/session", {"capabilities": {}})["value"]["sessionId"]
    base = f"/session/{session}"
    call(port, "POST", base + "/url", {"url": url})
    time.sleep(seconds)
    print("title:", call(port, "GET", base + "/title")["value"])
    print("handles:", call(port, "GET", base + "/window/handles")["value"])
    if script:
        print("script:", call(port, "POST", base + "/execute/sync", {"script": script, "args": []})["value"])
    call(port, "DELETE", base)


if __name__ == "__main__":
    main()
