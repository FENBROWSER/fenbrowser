# FenBrowser - Claude Code Instructions

## Test262 Conformance Testing Protocol

### Running Test262 Suite
Test262 tests MUST be run in **chunks of 1000** using the benchmark tool with strict safety controls:

1. **Chunk Size**: 1000 tests per chunk (53 total chunks)
2. **Completion Threshold**: Each chunk MUST have **900+ tests complete** (pass or fail) before moving to the next chunk. If a chunk completes fewer than 900, investigate and re-run.
3. **Memory Safety**: Check RAM usage before each chunk. **NEVER exceed 70% RAM usage**. If RAM is above 70%, wait or kill processes before continuing.
4. **Results File**: All results go to `test262_results.md` in the project root.
5. **Per-test timeout**: 180 seconds (3 minutes). Anything longer = failure.

### How to Run

```bash
# Build once
dotnet build FenBrowser.Test262Benchmark/FenBrowser.Test262Benchmark.csproj -c Release

# Get chunk count
./FenBrowser.Test262Benchmark/bin/Release/net9.0/FenBrowser.Test262Benchmark.exe get_chunk_count

# Run a specific chunk (1-indexed)
./FenBrowser.Test262Benchmark/bin/Release/net9.0/FenBrowser.Test262Benchmark.exe run_chunk <N>

# Check RAM before each chunk (Windows PowerShell)
powershell -Command "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory"
```

### RAM Monitoring Formula
- Total RAM: ~32 GB (33475132 KB)
- 70% threshold = ~23.4 GB used = ~10 GB free minimum
- If FreePhysicalMemory < 10000000 KB (~10 GB), DO NOT start next chunk

### Chunk Validation Rules
- A chunk "completes" when the process exits normally (not crash/timeout)
- 900+ of 1000 tests must have run (passed + failed >= 900)
- Record: chunk number, range, time, passed, failed, pass%, avg/test
- If chunk crashes or <900 tests complete, log it and retry once before moving on

### Results Format
Results are appended as markdown table rows to `test262_results.md`:
```
| Chunk | Range | Time (ms) | Tests | Passed | Failed | Pass % | Avg/Test (ms) |
```
