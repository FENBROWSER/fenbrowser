# Test262 Conformance Results

Canonical snapshot consumed by CI verification guards. Per-category reports live in `docs/test262_0_8_report.md`. Live gap catalogue at `docs/fenjs_gap_audit_2026-05-28.md`.

**Engine:** FenJS (interpreter + tier-4 #24 IL-JIT) | **Test262:** b1f9a0a | **Date:** 2026-05-28 | **Source commit:** `0b8afeee`

## Parser Subset (500 tests)

| Status | Count |
|--------|-------|
| Passed | 492 (98.4%) |
| Invalid Config | 8 |

## Runtime Subset (2,000 tests, first-alphabetical)

| Status | Count |
|--------|-------|
| Passed | **858 (42.9%)** |
| Failed | 1,123 |
| Crashed | **0** |
| Timed Out | 2 |
| Harness Unsupported | 9 |
| Invalid Config | 8 |

Trajectory: 27.4% (2026-05-24, pre-tier-6 #29) → 41.2% (2026-05-28 audit) → 41.5% (post §1 partial) → **42.9% (post §1 full + accessors + escape/unescape)**.

**Audit §1 closed:** all 5 known crashes fixed. ArrayBuffer huge-length now spec RangeErrors (`a61fbea7`); iterator-buffered values traced + pinned across drain (`edaaa403`); object-literal accessors install as real getters/setters + GC sees interpreter-frame registers (`0b8afeee`). See `docs/fenjs_gap_audit_2026-05-28.md` §1.

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
