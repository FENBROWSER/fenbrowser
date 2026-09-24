#!/usr/bin/env python3
import argparse
import asyncio
import base64
import hashlib
import json
import os
import platform
import shutil
import socket
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from websockets.asyncio.client import connect

sys.path.insert(0, str(Path(__file__).resolve().parent))
from capture_chrome_site import Cdp, wait_for_devtools

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent
DEFAULT_FIXTURE = SCRIPT_DIR / "browser_benchmark_fixture.html"
DEFAULT_OUTPUT = REPO_ROOT / "Results" / "benchmarks" / "browser-vs-chrome"
RESULT_EXPRESSION = "window.__BROWSER_BENCHMARK__ || null"
VIEWPORT_EXPRESSION = "{ width: window.innerWidth, height: window.innerHeight, clientWidth: document.documentElement.clientWidth, clientHeight: document.documentElement.clientHeight, devicePixelRatio: window.devicePixelRatio || 1 };"
EXPECTED = {
    "cardCount": 900,
    "createdElements": 4500,
    "createdTextNodes": 2700,
    "addedNodes": 100,
    "removedNodes": 100,
    "selectedCards": 900,
    "layoutSampleCount": 900,
    "layoutValidCount": 900,
    "timerTicks": 24,
    "domChecksum": 1358406040,
    "mutationChecksum": 1195171210,
    "jsChecksum": 1081865070,
    "timerChecksum": 11100,
}
METRICS = (
    "wallToResultMs",
    "navigationToResultMs",
    "domBuildMs",
    "domMutationMs",
    "mutationAttributeMs",
    "mutationTextMs",
    "mutationAppendMs",
    "mutationRemoveMs",
    "mutationQueryMs",
    "layoutMs",
    "layoutRectMs",
    "layoutComputedStyleMs",
    "jsCpuMs",
    "eventLoopMs",
    "totalHarnessMs",
)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def json_request(base, method, path, payload=None, timeout=30):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = Request(
        base + path,
        data=data,
        method=method,
        headers={"Content-Type": "application/json", "Accept": "application/json"},
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            body = response.read()
    except HTTPError as error:
        body = error.read().decode("utf-8", "replace")
        raise RuntimeError(f"{method} {path} returned HTTP {error.code}: {body[:1000]}") from error
    except URLError as error:
        raise RuntimeError(f"{method} {path} failed: {error.reason}") from error
    if not body:
        return {}
    try:
        return json.loads(body.decode("utf-8"))
    except json.JSONDecodeError as error:
        raise RuntimeError(f"{method} {path} returned invalid JSON") from error


def terminate_process(process):
    if process is None:
        return
    if process.poll() is None:
        if os.name == "nt":
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=20,
                    check=False,
                )
            except (OSError, subprocess.TimeoutExpired):
                pass
        else:
            process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass


def find_tooling():
    candidates = [
        os.environ.get("FEN_BENCHMARK_TOOLING"),
        str(REPO_ROOT / "FenBrowser.Tooling" / "bin" / "Release" / "net10.0" / "FenBrowser.Tooling.exe"),
        str(REPO_ROOT / "FenBrowser.Tooling" / "bin" / "Debug" / "net10.0" / "FenBrowser.Tooling.exe"),
    ]
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return str(Path(candidate).resolve())
    raise RuntimeError("FenBrowser.Tooling.exe was not found; build FenBrowser.Tooling in Release mode")


def wait_for_webdriver(port, process, timeout=60):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"FenBrowser.Tooling exited during startup with code {process.returncode}")
        try:
            response = json_request(f"http://127.0.0.1:{port}", "GET", "/status", timeout=2)
            if response.get("value", {}).get("ready"):
                return response
            last_error = response
        except Exception as error:
            last_error = error
        time.sleep(0.1)
    raise RuntimeError(f"FenBrowser WebDriver did not become ready: {last_error}")


def wait_for_chrome_devtools(port, timeout):
    try:
        return wait_for_devtools(port, timeout)
    except SystemExit as error:
        raise RuntimeError(str(error)) from error


class FixtureServer:
    def __init__(self, payload):
        self.payload = payload
        self.server = None
        self.thread = None

    def __enter__(self):
        payload = self.payload

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                path = urlsplit(self.path).path
                if path == "/favicon.ico":
                    self.send_response(204)
                    self.end_headers()
                    return
                if path not in ("/", "/benchmark"):
                    self.send_response(404)
                    self.end_headers()
                    return
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(payload)))
                self.send_header("Cache-Control", "no-store, no-cache, must-revalidate")
                self.send_header("Pragma", "no-cache")
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format_string, *args):
                return

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        if self.server is not None:
            self.server.shutdown()
            self.server.server_close()
        if self.thread is not None:
            self.thread.join(timeout=5)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"


class FenSession:
    def __init__(self, executable, startup_timeout):
        self.executable = executable
        self.startup_timeout = startup_timeout
        self.process = None
        self.port = None
        self.base = None
        self.session_id = None

    def start(self):
        self.port = free_port()
        self.process = subprocess.Popen(
            [self.executable, "webdriver", "--headless", "--port", str(self.port)],
            cwd=str(REPO_ROOT),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        self.base = f"http://127.0.0.1:{self.port}"
        wait_for_webdriver(self.port, self.process, self.startup_timeout)
        response = self.request("POST", "/session", {"capabilities": {}})
        try:
            self.session_id = response["value"]["sessionId"]
        except (KeyError, TypeError) as error:
            raise RuntimeError(f"FenBrowser returned an invalid session: {response}") from error
        self.request(
            "POST",
            f"/session/{self.session_id}/timeouts",
            {"script": 120000, "pageLoad": 120000, "implicit": 0},
        )
        return self

    def request(self, method, path, payload=None, timeout=120):
        return json_request(self.base, method, path, payload, timeout)

    def execute(self, script, timeout=120):
        response = self.request(
            "POST",
            f"/session/{self.session_id}/execute/sync",
            {"script": script, "args": []},
            timeout,
        )
        if "error" in response:
            raise RuntimeError(f"FenBrowser script error: {response['error']}")
        return response.get("value")

    def close(self):
        if self.session_id and self.base:
            try:
                self.request("DELETE", f"/session/{self.session_id}", timeout=20)
            except Exception:
                pass
            self.session_id = None
        terminate_process(self.process)
        self.process = None


def wait_for_fen_result(session, timeout):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        try:
            result = session.execute("return " + RESULT_EXPRESSION + ";", timeout=30)
            if isinstance(result, dict) and result.get("status") in ("complete", "failed"):
                return result
        except Exception as error:
            last_error = error
        time.sleep(0.025)
    raise TimeoutError(f"FenBrowser benchmark result did not arrive: {last_error}")


def cdp_value(response):
    if not isinstance(response, dict) or "error" in response:
        return None
    result = response.get("result")
    if not isinstance(result, dict):
        return None
    return result.get("value")


async def wait_for_chrome_result(cdp, timeout):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        response = await cdp.send(
            "Runtime.evaluate",
            {"expression": RESULT_EXPRESSION, "returnByValue": True, "awaitPromise": False},
            timeout=15,
        )
        value = cdp_value(response)
        if isinstance(value, dict) and value.get("status") in ("complete", "failed"):
            return value
        if "error" in response:
            last_error = response["error"]
        await asyncio.sleep(0.025)
    raise TimeoutError(f"Chrome benchmark result did not arrive: {last_error}")


def chrome_errors(cdp):
    errors = []
    for event in cdp.events:
        method = event.get("method")
        params = event.get("params") or {}
        if method == "Runtime.exceptionThrown":
            details = params.get("exceptionDetails") or {}
            errors.append("exception:" + str(details.get("text") or "unknown"))
        elif method == "Runtime.consoleAPICalled" and params.get("type") in ("error", "assert"):
            errors.append("console:" + str(params.get("type")))
        elif method == "Log.entryAdded":
            entry = params.get("entry") or {}
            if str(entry.get("level", "")).lower() in ("error", "assert"):
                errors.append("log:" + str(entry.get("text") or "unknown"))
    return errors


def result_viewport(result):
    viewport = result.get("viewport") if isinstance(result, dict) else None
    return viewport if isinstance(viewport, dict) else {}


def same_viewport(actual, expected):
    if not isinstance(actual, dict) or not isinstance(expected, dict):
        return False
    try:
        return all(abs(float(actual.get(key, -1)) - float(expected.get(key, -2))) < 0.01 for key in ("width", "height", "clientWidth", "clientHeight"))
    except (TypeError, ValueError):
        return False


def validate_result(result, expected_viewport, expected=EXPECTED, strict=True):
    failures = []
    if not isinstance(result, dict):
        return ["benchmark result was not an object"]
    if result.get("status") != "complete":
        failures.append("benchmark status was " + str(result.get("status")))
    if result.get("readyState") != "complete":
        failures.append("document readiness was " + str(result.get("readyState")))
    errors = result.get("errors") or []
    if errors:
        failures.append("page errors: " + "; ".join(str(error) for error in errors))
    for key, expected_value in expected.items():
        if expected_value is None:
            continue
        actual = result.get(key)
        if actual != expected_value:
            failures.append(f"{key}: expected {expected_value}, got {actual}")
    if result.get("layoutSampleCount") != result.get("layoutValidCount"):
        failures.append("not every layout sample had a visible non-zero box")
    if result.get("clockSource") != "performance.now":
        failures.append("performance.now was not available for phase timing")
    if not isinstance(result.get("clockResolutionMs"), (int, float)) or result.get("clockResolutionMs") < 0:
        failures.append("invalid clock resolution measurement")
    if not same_viewport(result_viewport(result), expected_viewport):
        failures.append(
            f"viewport mismatch: expected {expected_viewport}, got {result_viewport(result)}"
        )
    if strict and any(expected.get(key) is None for key in ("domChecksum", "mutationChecksum", "jsChecksum", "timerChecksum")):
        failures.append("checksum contract is incomplete")
    return failures


def numeric_result(result, extra):
    output = dict(result)
    output.update(extra)
    return output


def run_fen(args, url, run_number, run_dir, expected_viewport):
    record = {
        "engine": "fenbrowser",
        "run": run_number,
        "valid": False,
        "failures": [],
        "result": None,
    }
    session = FenSession(find_tooling(), args.startup_timeout)
    wall_start = time.perf_counter()
    startup_start = time.perf_counter()
    try:
        session.start()
        startup_ms = (time.perf_counter() - startup_start) * 1000
        blank_viewport = session.execute("return " + VIEWPORT_EXPRESSION, timeout=30)
        if not same_viewport(blank_viewport, expected_viewport):
            record["failures"].append(
                f"blank viewport mismatch: expected {expected_viewport}, got {blank_viewport}"
            )
        navigation_start = time.perf_counter()
        session.request("POST", f"/session/{session.session_id}/url", {"url": url}, timeout=args.page_timeout)
        navigation_response_ms = (time.perf_counter() - navigation_start) * 1000
        result = wait_for_fen_result(session, args.page_timeout)
        navigation_to_result_ms = (time.perf_counter() - navigation_start) * 1000
        wall_ms = (time.perf_counter() - wall_start) * 1000
        if args.screenshots:
            screenshot_start = time.perf_counter()
            shot = session.request("GET", f"/session/{session.session_id}/screenshot", timeout=60)
            if shot.get("value"):
                (run_dir / "screenshot.png").write_bytes(base64.b64decode(shot["value"]))
            record["screenshotMs"] = (time.perf_counter() - screenshot_start) * 1000
        record["result"] = numeric_result(
            result,
            {
                "startupMs": startup_ms,
                "wallToResultMs": wall_ms,
                "navigationToResultMs": navigation_to_result_ms,
                "navigationResponseMs": navigation_response_ms,
            },
        )
        record["failures"].extend(validate_result(result, expected_viewport, strict=not args.allow_contract_mismatch))
        record["valid"] = not record["failures"]
    except Exception as error:
        record["failures"].append(f"{type(error).__name__}: {error}")
    finally:
        process = session.process
        session.close()
        record["processExitCode"] = process.poll() if process is not None else None
    return record


async def run_chrome(args, url, run_number, run_dir, expected_viewport):
    record = {
        "engine": "chrome",
        "run": run_number,
        "valid": False,
        "failures": [],
        "result": None,
    }
    chrome_path = shutil.which("chrome") or shutil.which("google-chrome")
    if chrome_path is None:
        for candidate in (
            r"C:\Program Files\Google\Chrome\Application\chrome.exe",
            r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
        ):
            if Path(candidate).is_file():
                chrome_path = candidate
                break
    if chrome_path is None:
        record["failures"].append("Chrome executable was not found")
        return record
    profile = tempfile.mkdtemp(prefix="fen-benchmark-chrome-")
    port = free_port()
    process = subprocess.Popen(
        [
            chrome_path,
            "--headless=new",
            f"--remote-debugging-port={port}",
            f"--user-data-dir={profile}",
            f"--window-size={int(expected_viewport['width'])},{int(expected_viewport['height'])}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-networking",
            "--disable-extensions",
            "--disable-features=Translate,MediaRouter",
            "--hide-scrollbars",
            "about:blank",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    wall_start = time.perf_counter()
    startup_start = time.perf_counter()
    try:
        version = await asyncio.to_thread(wait_for_chrome_devtools, port, args.startup_timeout)
        async with connect(version["webSocketDebuggerUrl"], max_size=None) as websocket:
            cdp = Cdp(websocket)
            cdp.start()
            target = await cdp.send("Target.createTarget", {"url": "about:blank"}, session=False)
            if "error" in target:
                raise RuntimeError(f"Chrome target creation failed: {target['error']}")
            attached = await cdp.send(
                "Target.attachToTarget",
                {"targetId": target["targetId"], "flatten": True},
                session=False,
            )
            if "error" in attached:
                raise RuntimeError(f"Chrome target attachment failed: {attached['error']}")
            cdp.session_id = attached["sessionId"]
            await cdp.send("Page.enable")
            await cdp.send("Network.enable")
            await cdp.send("Runtime.enable")
            await cdp.send("Log.enable")
            await cdp.send("Page.setLifecycleEventsEnabled", {"enabled": True})
            await cdp.send(
                "Emulation.setDeviceMetricsOverride",
                {
                    "width": int(expected_viewport["width"]),
                    "height": int(expected_viewport["height"]),
                    "deviceScaleFactor": 1,
                    "mobile": False,
                },
            )
            startup_ms = (time.perf_counter() - startup_start) * 1000
            navigation_start = time.perf_counter()
            navigation = await cdp.send("Page.navigate", {"url": url}, timeout=60)
            if "error" in navigation:
                raise RuntimeError(f"Chrome navigation failed: {navigation['error']}")
            navigation_response_ms = (time.perf_counter() - navigation_start) * 1000
            result = await wait_for_chrome_result(cdp, args.page_timeout)
            navigation_to_result_ms = (time.perf_counter() - navigation_start) * 1000
            wall_ms = (time.perf_counter() - wall_start) * 1000
            if args.screenshots:
                screenshot_start = time.perf_counter()
                shot = await cdp.send("Page.captureScreenshot", {"format": "png"}, timeout=60)
                if "data" in shot:
                    (run_dir / "screenshot.png").write_bytes(base64.b64decode(shot["data"]))
                record["screenshotMs"] = (time.perf_counter() - screenshot_start) * 1000
            browser_errors = chrome_errors(cdp)
            record["result"] = numeric_result(
                result,
                {
                    "startupMs": startup_ms,
                    "wallToResultMs": wall_ms,
                    "navigationToResultMs": navigation_to_result_ms,
                    "navigationResponseMs": navigation_response_ms,
                    "browser": version.get("Browser"),
                    "browserErrors": browser_errors,
                },
            )
            if browser_errors:
                record["failures"].extend("browser: " + error for error in browser_errors)
            record["failures"].extend(validate_result(result, expected_viewport, strict=not args.allow_contract_mismatch))
            record["valid"] = not record["failures"]
    except Exception as error:
        record["failures"].append(f"{type(error).__name__}: {error}")
    finally:
        terminate_process(process)
        shutil.rmtree(profile, ignore_errors=True)
        record["processExitCode"] = process.poll()
    return record


def calibrate_viewport(args):
    session = FenSession(find_tooling(), args.startup_timeout)
    try:
        session.start()
        viewport = session.execute("return " + VIEWPORT_EXPRESSION, timeout=30)
        if not isinstance(viewport, dict):
            raise RuntimeError(f"FenBrowser returned an invalid viewport: {viewport}")
        return viewport
    finally:
        session.close()


def aggregate(samples):
    valid_samples = [sample for sample in samples if sample.get("valid")]
    metrics = {}
    for metric in METRICS:
        values = [float(sample["result"][metric]) for sample in valid_samples if metric in sample.get("result", {})]
        if not values:
            metrics[metric] = {"count": 0}
            continue
        metrics[metric] = {
            "count": len(values),
            "values": values,
            "mean": statistics.mean(values),
            "median": statistics.median(values),
            "stdev": statistics.stdev(values) if len(values) > 1 else 0.0,
            "min": min(values),
            "max": max(values),
        }
    return {
        "sampleCount": len(samples),
        "validSampleCount": len(valid_samples),
        "failedSampleCount": len(samples) - len(valid_samples),
        "metrics": metrics,
    }


def compare_aggregates(fen_aggregate, chrome_aggregate):
    output = {}
    for metric in METRICS:
        fen = fen_aggregate["metrics"].get(metric, {})
        chrome = chrome_aggregate["metrics"].get(metric, {})
        if fen.get("count") and chrome.get("count"):
            output[metric] = {
                "fenbrowserMean": fen["mean"],
                "chromeMean": chrome["mean"],
                "deltaMs": fen["mean"] - chrome["mean"],
                "ratio": fen["mean"] / chrome["mean"] if chrome["mean"] else None,
            }
    return output


def render_markdown(report):
    lines = [
        "# FenBrowser vs Chrome Large Browser Benchmark",
        "",
        f"- Generated: `{report['generatedUtc']}`",
        f"- Runs per engine: `{report['configuration']['runs']}`",
        f"- Fixture SHA-256: `{report['fixture']['sha256']}`",
        f"- Viewport: `{report['configuration']['viewport']}`",
        f"- Accuracy gate: `{'PASS' if report['validation']['passed'] else 'FAIL'}`",
        "",
        "Each sample uses a fresh browser process and profile, the same local fixture, the same content viewport, and a no-store HTTP response. The page validates structural and checksum contracts before timing is accepted.",
        "The in-page phase metrics use `performance.now`; `navigationToResultMs` is the common navigation-start-to-benchmark-marker interval. `wallToResultMs` additionally includes fresh-process startup and automation overhead. `navigationResponseMs` is retained per sample as a diagnostic only because WebDriver and CDP expose different readiness boundaries.",
        "No warm-up run is discarded: five independent cold-process samples are summarized by mean, median, sample standard deviation, minimum, and maximum. The layout checksum is diagnostic because browser geometry rounding is allowed to differ; all functional checksums must match.",
        "",
        "## Averages",
        "",
        "| Metric | FenBrowser mean (ms) | Chrome mean (ms) | Delta (ms) | Fen/Chrome |",
        "|---|---:|---:|---:|---:|",
    ]
    comparison = report["comparison"]
    for metric in METRICS:
        item = comparison.get(metric)
        if item:
            ratio = "-" if item["ratio"] is None else f"{item['ratio']:.3f}"
            lines.append(
                f"| {metric} | {item['fenbrowserMean']:.3f} | {item['chromeMean']:.3f} | "
                f"{item['deltaMs']:.3f} | {ratio} |"
            )
    lines.extend(
        [
            "",
            "## Spread",
            "",
            "| Engine | Metric | n | Mean | Median | Std dev | Min | Max |",
            "|---|---|---:|---:|---:|---:|---:|---:|",
        ]
    )
    for engine in ("fenbrowser", "chrome"):
        aggregate_data = report["aggregates"][engine]
        for metric in METRICS:
            item = aggregate_data["metrics"].get(metric, {})
            if item.get("count"):
                lines.append(
                    f"| {engine} | {metric} | {item['count']} | {item['mean']:.3f} | "
                    f"{item['median']:.3f} | {item['stdev']:.3f} | {item['min']:.3f} | {item['max']:.3f} |"
                )
    lines.extend(["", "## Validation", ""])
    validation = report["validation"]
    lines.append(f"- Valid samples: `{validation['validSamples']}/{validation['totalSamples']}`")
    lines.append(f"- All page checksums identical across engines: `{validation['checksumsIdentical']}`")
    lines.append(f"- All samples used the calibrated viewport: `{validation['viewportsIdentical']}`")
    if validation["failures"]:
        lines.append("- Failures:")
        for failure in validation["failures"]:
            lines.append(f"  - {failure}")
    return "\n".join(lines) + "\n"


def build_report(args, fixture, fixture_hash, viewport, samples, calibration, tooling):
    fen_samples = [sample for sample in samples if sample["engine"] == "fenbrowser"]
    chrome_samples = [sample for sample in samples if sample["engine"] == "chrome"]
    fen_aggregate = aggregate(fen_samples)
    chrome_aggregate = aggregate(chrome_samples)
    checksum_keys = ("domChecksum", "mutationChecksum", "jsChecksum", "timerChecksum")
    checksum_sets = []
    for sample in samples:
        if not sample.get("valid") or not isinstance(sample.get("result"), dict):
            continue
        values = tuple(sample["result"].get(key) for key in checksum_keys)
        if all(value is not None for value in values):
            checksum_sets.append(values)
    checksums_identical = bool(checksum_sets) and len(set(checksum_sets)) == 1
    viewports_identical = all(
        same_viewport(result_viewport(sample.get("result")), viewport)
        for sample in samples
        if sample.get("valid")
    )
    failures = []
    for sample in samples:
        failures.extend(f"{sample['engine']} run {sample['run']}: {failure}" for failure in sample.get("failures", []))
    if len(samples) != args.runs * 2:
        failures.append(f"expected {args.runs * 2} samples, got {len(samples)}")
    if fen_aggregate["validSampleCount"] != args.runs:
        failures.append(f"FenBrowser valid samples: {fen_aggregate['validSampleCount']}/{args.runs}")
    if chrome_aggregate["validSampleCount"] != args.runs:
        failures.append(f"Chrome valid samples: {chrome_aggregate['validSampleCount']}/{args.runs}")
    for engine, aggregate_data in (("FenBrowser", fen_aggregate), ("Chrome", chrome_aggregate)):
        for metric in METRICS:
            if aggregate_data["metrics"].get(metric, {}).get("count", 0) != args.runs:
                failures.append(f"{engine} metric {metric} did not produce {args.runs} values")
    if not checksums_identical:
        failures.append("valid samples did not share one checksum set")
    if not viewports_identical:
        failures.append("valid samples did not share the calibrated viewport")
    return {
        "schemaVersion": 1,
        "generatedUtc": utc_now(),
        "configuration": {
            "runs": args.runs,
            "fixture": str(fixture),
            "strictContract": not args.allow_contract_mismatch,
            "screenshots": args.screenshots,
            "pageTimeoutSeconds": args.page_timeout,
            "startupTimeoutSeconds": args.startup_timeout,
            "viewport": viewport,
            "executionOrder": "alternating by repetition",
        },
        "environment": {
            "python": platform.python_version(),
            "platform": platform.platform(),
            "machine": platform.machine(),
            "processor": platform.processor(),
            "cpuCount": os.cpu_count(),
            "workingDirectory": str(REPO_ROOT),
        },
        "tooling": {
            "path": tooling,
            "sha256": file_sha256(Path(tooling)),
        },
        "contract": EXPECTED,
        "fixture": {
            "path": str(fixture),
            "bytes": fixture.stat().st_size,
            "sha256": fixture_hash,
        },
        "calibration": calibration,
        "aggregates": {
            "fenbrowser": fen_aggregate,
            "chrome": chrome_aggregate,
        },
        "comparison": compare_aggregates(fen_aggregate, chrome_aggregate),
        "validation": {
            "passed": not failures,
            "totalSamples": len(samples),
            "validSamples": sum(1 for sample in samples if sample.get("valid")),
            "checksumsIdentical": checksums_identical,
            "viewportsIdentical": viewports_identical,
            "failures": failures,
        },
        "samples": samples,
    }


def print_summary(report):
    print(f"fixture={report['fixture']['path']}")
    print(f"sha256={report['fixture']['sha256']}")
    print(f"viewport={report['configuration']['viewport']}")
    print(f"validation={'PASS' if report['validation']['passed'] else 'FAIL'}")
    print("metric                 fen_mean   chrome_mean   delta   ratio")
    for metric in METRICS:
        item = report["comparison"].get(metric)
        if item:
            ratio = "-" if item["ratio"] is None else f"{item['ratio']:.3f}"
            print(
                f"{metric:22} {item['fenbrowserMean']:9.3f} {item['chromeMean']:12.3f} "
                f"{item['deltaMs']:8.3f} {ratio:>7}"
            )
    if report["validation"]["failures"]:
        for failure in report["validation"]["failures"]:
            print(f"failure: {failure}")


def parse_args():
    parser = argparse.ArgumentParser(description="Run the five-sample FenBrowser versus Chrome large browser benchmark")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--screenshots", action="store_true")
    parser.add_argument("--allow-contract-mismatch", action="store_true")
    parser.add_argument("--page-timeout", type=float, default=120.0)
    parser.add_argument("--startup-timeout", type=float, default=60.0)
    args = parser.parse_args()
    if args.runs < 1:
        parser.error("--runs must be at least 1")
    if args.page_timeout <= 0 or args.startup_timeout <= 0:
        parser.error("timeouts must be positive")
    return args


def main():
    args = parse_args()
    fixture = args.fixture if args.fixture.is_absolute() else (Path.cwd() / args.fixture)
    fixture = fixture.resolve()
    if not fixture.is_file():
        raise SystemExit(f"fixture not found: {fixture}")
    args.fixture = fixture
    executable = find_tooling()
    chrome_path = shutil.which("chrome") or shutil.which("google-chrome")
    if chrome_path is None:
        for candidate in (
            r"C:\Program Files\Google\Chrome\Application\chrome.exe",
            r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
        ):
            if Path(candidate).is_file():
                chrome_path = candidate
                break
    if chrome_path is None:
        raise SystemExit("Chrome executable was not found")
    run_stamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S_%f")
    output_dir = args.out if args.out.is_absolute() else (Path.cwd() / args.out)
    output_dir = output_dir / run_stamp
    output_dir.mkdir(parents=True, exist_ok=True)
    payload = fixture.read_bytes()
    fixture_hash = file_sha256(fixture)
    with FixtureServer(payload) as server:
        calibration = {"viewport": calibrate_viewport(args)}
        viewport = calibration["viewport"]
        samples = []
        for run_number in range(1, args.runs + 1):
            run_dir = output_dir / f"run-{run_number:02d}"
            run_dir.mkdir(parents=True, exist_ok=True)
            url = f"{server.url}/benchmark?run={run_number}"
            if run_number % 2 == 1:
                order = ("fenbrowser", "chrome")
            else:
                order = ("chrome", "fenbrowser")
            for engine in order:
                print(f"run {run_number}/{args.runs} {engine}", flush=True)
                if engine == "fenbrowser":
                    sample = run_fen(args, url, run_number, run_dir, viewport)
                else:
                    sample = asyncio.run(run_chrome(args, url, run_number, run_dir, viewport))
                samples.append(sample)
                if not sample.get("valid"):
                    for failure in sample.get("failures", []):
                        print(f"  {engine} invalid: {failure}", flush=True)
                else:
                    result = sample["result"]
                    print(
                        f"  {engine} wall={result['wallToResultMs']:.1f}ms "
                        f"dom={result['domBuildMs']:.1f}ms layout={result['layoutMs']:.1f}ms "
                        f"js={result['jsCpuMs']:.1f}ms",
                        flush=True,
                    )
    report = build_report(args, fixture, fixture_hash, viewport, samples, calibration, executable)
    report_path = output_dir / "benchmark.json"
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    markdown_path = output_dir / "benchmark.md"
    markdown_path.write_text(render_markdown(report), encoding="utf-8")
    latest_json = output_dir.parent / "latest.json"
    latest_md = output_dir.parent / "latest.md"
    latest_json.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    latest_md.write_text(render_markdown(report), encoding="utf-8")
    print_summary(report)
    print(f"report={report_path}")
    print(f"markdown={markdown_path}")
    return 0 if report["validation"]["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
