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
- `--max <N>`: max number of test files (deterministic first N after sort).
- `--filter <text>`: include only paths containing substring.
- `--output <json_path>`: report output path.

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- test262 --root C:\Users\udayk\Videos\test262 --workers 20 --max 1000 --output C:\Users\udayk\Videos\fenbrowser-test\Results\test262_fenrunner_1000.json
```

#### Test262 Notes

- Discovery includes `test/**/*.js`.
- Excludes `*_FIXTURE.js` and files under `test/harness`.
- Current runner skips tests with flags:
  - `module`
  - `async`
  - `generated`
- Output JSON includes:
  - summary (`passed`, `failed`, `skipped`, `totalScenarios`, etc.)
  - per-scenario results (`file`, `scenario`, `outcome`, `message`)

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
- Large runs:
  - start with `--max 100` and scale up.

## Quick Start

```powershell
dotnet build FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Debug
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -- test262 --root C:\Users\udayk\Videos\test262 --max 100 --workers 10 --output C:\Users\udayk\Videos\fenbrowser-test\Results\test262_quick.json
```
