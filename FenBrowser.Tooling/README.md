# FenBrowser.Tooling

`FenBrowser.Tooling` is the command-line utility project for verification and automation tasks around FenBrowser.

## Prerequisites

- .NET SDK 8.0+
- Windows PowerShell (for examples below)
- Repo built at least once:

```powershell
dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Debug
```

## Run Pattern

Use `dotnet run` with a tooling command:

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- <command> [options]
```

## Commands

### `verify`

Generate a verification snapshot image from an HTML file.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- verify <html_path>
```

### `acid2`

Run the Acid2 test in headless mode.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- acid2
```

### `acid2-compare`

Capture live/reference Acid2 images and compare.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- acid2-compare
```

### `acid2-layout-html`

Generate an HTML layout snapshot report from Acid2 run artifacts.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- acid2-layout-html
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- acid2-layout-html C:\path\to\report.html
```

### `webdriver`

Start FenBrowser WebDriver server.

Options:
- `--port=<N>` or `--port <N>`
- `--headless`

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- webdriver --port=4444 --headless
```

### `render-perf`

Run render performance benchmark suite.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- render-perf
```

### `capability-ledger`

Generate a machine-readable capability ledger by reconciling:
- `docs/COMPLIANCE_MATRIX.md` capabilities
- `docs/spec_governance_map.json` governed files + required IDs
- live source headers (`SpecRef`, `CapabilityId`, `Determinism`, `FallbackPolicy`)

Outputs:
- timestamped snapshot in `Results/`
- latest alias at `Results/capability_ledger_latest.json` (or custom output path)

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- capability-ledger
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- capability-ledger C:\Users\udayk\Videos\fenbrowser-test\Results\capability_ledger_custom.json
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- capability-ledger --require-live-evidence
```

### `debug-css`

Run CSS parser debug routine and write `css_debug.txt`.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- debug-css
```

### `test`

Run FenEngine logic test entrypoint.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- test
```

### `test262`

Run official Test262 files against FenBrowser runtime via tooling runner.

Required:
- `--root <path>`: path to official Test262 checkout (must contain `test/` and `harness/`).

Main options:
- `--workers <N>`: number of worker processes.
- `--timeout-ms <N>`: per-scenario execution timeout in milliseconds (default: `10000`).
- `--max <N>`: max number of test files (deterministic first N after sort).
- `--filter <text>`: include only paths containing substring.
- `--output <json_path>`: report output path.
- `--event-log <jsonl_path>`: append-only per-scenario event log path. Defaults to the output path with `.events.jsonl`.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- test262 --root C:\Users\udayk\Videos\test262 --workers 20 --max 1000 --output C:\Users\udayk\Videos\fenbrowser-test\Results\test262_fenrunner_1000.json
```

### `wpt`

Run upstream WPT through the external WebDriver harness and write a deterministic result bundle.

Defaults:
- WPT root: `C:\Users\udayk\Videos\wpt`
- Host binary: first existing `FenBrowser.Host\bin\Debug\net8.0\FenBrowser.Host.exe`, then Release.
- WebDriver launcher: `scripts\wpt-webdriver-launcher.cmd`
- Output: timestamped `Results\wpt_*`

Prerequisite:
- Install the FenBrowser WPT product plugin into the Python environment used by the upstream WPT checkout:

```powershell
C:\Users\udayk\Videos\wpt\_venv3\Scripts\python.exe -m pip install -e C:\Users\udayk\Videos\fenbrowser-test\tools\wptrunner-fenbrowser
```

The plugin uses WPT's documented `wptrunner.products` entry point. It does not patch WPT core.

Main options:
- `--root <path>`: upstream WPT checkout.
- `--binary <path>`: FenBrowser Host executable.
- `--webdriver-binary <path>`: launcher that starts `FenBrowser.Tooling webdriver`.
- `--processes <N>`: WPT worker process count.
- `--timeout-seconds <N>`: watchdog for the whole WPT run.
- `--venv <path>`: WPT virtualenv path; defaults to `C:\Users\udayk\Videos\wpt\_venv3` when present.
- `--skip-venv-setup`: use the specified virtualenv as-is.
- `--output-dir <path>`: result bundle directory.
- `--tests <paths>`: comma-separated WPT paths. Trailing positional paths are also accepted.

Outputs:
- `wpt.raw.json`
- `wpt.report.json`
- `wpt.mach.log`
- `wpt.stdout.log`
- `wpt.stderr.log`
- `wpt.summary.json`, including `failurePhase: "wpt_startup"` when the watchdog fires before WPT emits any `test_start` events.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- wpt --tests acid/acid2/reftest.html --processes 1 --timeout-seconds 120
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- wpt --tests html/ --processes 20 --timeout-seconds 1800 --output-dir C:\Users\udayk\Videos\fenbrowser-test\Results\wpt_html_current
```

### Staged Recovery Baseline Bundle

Run staged Stage 0-3 baseline slices and emit a JSON result bundle under `Results/`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_stage_recovery_baseline.ps1
```

Focused run for Stage 0 + Stage 1 only:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_stage_recovery_baseline.ps1 -Slices stage0-governance,stage1-pipeline -SkipBuild
```

Disable strict live-artifact evidence requirement (keeps reporting but does not fail on missing artifacts):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_stage_recovery_baseline.ps1 -RequireLiveArtifactEvidence:$false
```

Use a custom logs directory for live-artifact gates:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_stage_recovery_baseline.ps1 -LogsDir C:\custom\logs
```

#### Test262 Notes

- Discovery includes `test/**/*.js`.
- Excludes `*_FIXTURE.js` and files under `test/harness`.
- Current runner skips tests with flags:
  - `module`
  - `async`
- Output JSON includes:
  - summary (`passed`, `failed`, `skipped`, `timedOut`, `totalScenarios`, `categories`, etc.)
  - per-scenario results (`file`, `scenario`, `outcome`, `category`, `durationMs`, `message`)
- Output JSONL event log includes one per-scenario record as it completes; parent runs merge worker event logs into the configured event path.
- Worker runs stream low-noise `[test262] progress shard=<index>/<count> files=<done>/<total> pass=<n> fail=<n> skip=<n> timeout=<n> total=<n>` lines so long runs show live status before the final summary.
- A timed-out scenario aborts the current shard after recording the timeout result; this avoids reporting later unexecuted files as completed.

## Output Locations

- Most verification artifacts are written under repo `Results/` unless explicitly redirected.
- `test262` default output path:
  - `Results/test262_fenrunner_results.json`

## Troubleshooting

- `test262 root not found`:
  - verify `--root` points to official checkout directory.
- Empty/low test count:
  - check `--filter` and `--max` values.
- Worker failures:
  - rerun with `--workers 1` to isolate a failing case.
- Timeout failures:
  - inspect `outcome: "timeout"` and `category: "timeout"` entries in the JSON report, then rerun that file with a narrower `--filter`.
- Large runs:
  - start with `--max 100` and scale up.

## Quick Start

```powershell
dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Debug
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- test262 --root C:\Users\udayk\Videos\test262 --max 100 --workers 10 --output C:\Users\udayk\Videos\fenbrowser-test\Results\test262_quick.json
```
