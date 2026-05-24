# Test262 Conformance Results

Canonical snapshot consumed by CI verification guards. Per-category reports live in `docs/test262_0_8_report.md`.

**Engine:** FenJS (interpreter) | **Test262:** b1f9a0a | **Date:** 2026-05-24

## Parser Subset (500 tests)

| Status | Count |
|--------|-------|
| Passed | 492 (98.4%) |
| Invalid Config | 8 |

## Runtime Subset (2,000 tests)

| Status | Count |
|--------|-------|
| Passed | 548 (27.4%) |
| Failed | 1,189 |
| Harness Unsupported | 251 |
| Timed Out | 4 |
| Crashed | 0 |

## Category Breakdown (Runtime)

| Category | Count | Description |
|----------|-------|-------------|
| runtimeMissing | 1,186 | Feature not implemented |
| runtimeSemanticBug | 1,186 | Spec deviation |
| hostNotApplicable | 259 | Browser/Node-specific |
| parserBug | 3 | Parser regression |
| timeout | 4 | > 15s |

## Baselines

- `FenBrowser.Js.Test262/Baselines/latest-results.json` — runtime subset
- `FenBrowser.Js.Test262/Baselines/latest-parser.json` — parser subset
