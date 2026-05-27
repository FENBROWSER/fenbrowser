# Test262 Conformance Results

Canonical snapshot consumed by CI verification guards. Per-category reports live in `docs/test262_0_8_report.md`. Live gap catalogue at `docs/fenjs_gap_audit_2026-05-28.md`.

**Engine:** FenJS (interpreter + tier-4 #24 IL-JIT) | **Test262:** b1f9a0a | **Date:** 2026-05-28 | **Source commit:** `0ec6b59f`

## Parser Subset (500 tests)

| Status | Count |
|--------|-------|
| Passed | 492 (98.4%) |
| Invalid Config | 8 |

## Runtime Subset (2,000 tests, first-alphabetical)

| Status | Count |
|--------|-------|
| Passed | 824 (41.2%) |
| Failed | 1,152 |
| Crashed | 5 |
| Timed Out | 2 |
| Harness Unsupported | 9 |
| Invalid Config | 8 |

Up from 27.4% (2026-05-24) via the tier-6 #29 conformance batch. **5 crashes remain** — see `docs/fenjs_gap_audit_2026-05-28.md` §1 for paths and proposed fixes.

## Runtime Failure Distribution (top dirs)

| Dir | Failed |
|----|----|
| `annexB/language/eval-code` | 343 |
| `built-ins/ArrayBuffer/prototype` | 138 |
| `annexB/language/global-code` | 112 |
| `annexB/language/function-code` | 107 |
| `built-ins/Array/prototype` | 104 |
| `annexB/built-ins/String` | 86 |
| `annexB/built-ins/RegExp` | 54 |
| Other | 408 |

The subset is alphabetical and so over-represents `annexB/*` and `built-ins/A*`. A full ~50K-test sweep is queued (audit doc §7.4) to get a true distribution.

## Reproduce

```bash
dotnet build FenBrowser.Js.Test262/FenBrowser.Js.Test262.csproj -c Release

FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe \
  --parser-subset --root external/test262 \
  --out Results/test262/audit-parser.json --max 500 --timeout-ms 5000

FenBrowser.Js.Test262/bin/Release/net10.0/FenBrowser.Js.Test262.exe \
  --runtime-subset --root external/test262 \
  --out Results/test262/audit-runtime.json --max 2000 --timeout-ms 15000
```

## Baselines

- `FenBrowser.Js.Test262/Baselines/latest-results.json` — runtime subset
- `FenBrowser.Js.Test262/Baselines/latest-parser.json` — parser subset

Refresh in lockstep with this doc when the numbers move.
