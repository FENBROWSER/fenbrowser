# FenBrowser Test262 Runner

This folder contains FenBrowser's local Test262 CLI runner. The vendored upstream suite lives in [`../test262`](../test262), the runner lives in [`../FenBrowser.Test262`](../FenBrowser.Test262), and all output artifacts should go to [`../Results`](../Results).

## Layout

- [`Program.cs`](./Program.cs): command-line entrypoint for suite discovery and execution.
- [`Test262Config.cs`](./Test262Config.cs): runner defaults such as chunk size, timeout, and output format.
- [`ResultsExporter.cs`](./ResultsExporter.cs): JSON, Markdown, and TAP exporters.
- [`../scripts/clean_test262.ps1`](../scripts/clean_test262.ps1): removes stale results, kills stuck Test262 runner processes, and deletes local debug files from the vendored suite.
- [`../scripts/run_test262_chunk_parallel.ps1`](../scripts/run_test262_chunk_parallel.ps1): runs one logical chunk as multiple smaller microchunks in parallel.

## Rules

- Treat [`../test262`](../test262) as a vendored upstream dependency. Do not put local repro files there.
- Put local debug scripts in [`../scratch`](../scratch), repo root temp files, or xUnit coverage under [`../FenBrowser.Tests`](../FenBrowser.Tests).
- Keep run outputs in [`../Results`](../Results). Do not mix JSON and logs into the project folders.
- Use external worker fan-out for parallelism. The in-process runner stays effectively sequential to avoid runtime global-state races.

## Prerequisites

- .NET SDK `8.0.416` or compatible patch from [`../global.json`](../global.json)
- Local Test262 checkout at [`../test262`](../test262)
- PowerShell for the helper scripts

## Clean State

Run this before starting a new chunk or after a crash:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\clean_test262.ps1
```

If you also want to remove `FenBrowser.Test262/bin` and `FenBrowser.Test262/obj`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\clean_test262.ps1 -IncludeBuildOutputs
```

What it does:

- Kills lingering `dotnet` or `FenBrowser.Test262` runner processes tied to this repo
- Empties [`../Results`](../Results)
- Removes workspace-only `tmp-debug-*`, `debug_*`, `custom-test*`, and `test/local-host/*` files from the vendored suite

## Build

```powershell
dotnet build .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release
```

## Supported Commands

Suite summary:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- summary --root test262
```

Chunk count for the current suite:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- get_chunk_count --root test262 --chunk-size 1000
```

Single file:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- run_single built-ins\Array\length.js --root test262
```

Single category:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- run_category built-ins/Array --root test262 --max 100
```

Single logical chunk:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- run_chunk 1 --root test262 --chunk-size 1000 --format json --output .\Results\chunk1.json
```

Crash-safe logical chunk:

```powershell
dotnet run --project .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release -- run_chunk 1 --root test262 --chunk-size 1000 --isolate-process --format json --output .\Results\chunk1_isolated.json
```

## Canonical Parallel Workflow

For the current team workflow, one logical chunk means `1000` tests. To run logical chunk `1` split across `20` workers, use the parallel helper instead of the full-suite watchdog:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_test262_chunk_parallel.ps1 -ChunkNumber 1 -ChunkSize 1000 -WorkerCount 20
```

That helper:

- Splits the logical chunk into `20` global microchunks of `50` tests each
- Launches `20` `dotnet run ... run_chunk` workers in parallel
- Stores one JSON result and two logs per worker under a timestamped [`../Results`](../Results) folder
- Writes an aggregate `summary.json` and `summary.md`

Example with per-test process isolation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_test262_chunk_parallel.ps1 -ChunkNumber 1 -ChunkSize 1000 -WorkerCount 20 -IsolateProcess
```

## Full-Suite Watchdog

Use the watchdog script only when you want parallelism across many logical chunks of the full suite:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run_test262_parallel_watchdog.ps1 -WorkerCount 10 -ChunkSize 1000 -SkipBuild
```

This is not the same thing as "run the first 1000 tests on 20 workers." For a single logical chunk split into many workers, use [`../scripts/run_test262_chunk_parallel.ps1`](../scripts/run_test262_chunk_parallel.ps1).

## Output Conventions

- One-off CLI runs default to `Results/test262_results.json` unless you pass `--output`.
- Parallel chunk runs create timestamped directories like `Results/chunk1_1000tests_20workers_YYYYMMDD_HHMMSS`.
- Worker logs are stored under `logs/worker_XX.out.log` and `logs/worker_XX.err.log`.
- If a worker crashes before writing JSON, inspect the matching `err.log` first.

## Recommended Sequence

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\clean_test262.ps1
dotnet build .\FenBrowser.Test262\FenBrowser.Test262.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\scripts\run_test262_chunk_parallel.ps1 -ChunkNumber 1 -ChunkSize 1000 -WorkerCount 20 -SkipBuild
```
