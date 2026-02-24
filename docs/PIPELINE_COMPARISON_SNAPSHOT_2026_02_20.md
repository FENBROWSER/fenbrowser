# Pipeline Comparison Snapshot (2026-02-20)

Source: user-provided screenshot artifact (saved as documentation record).  
Comparison: Fen vs Chrome/Firefox/Ladybird baseline.

Scale:
- 80+: built fully
- 55-79: partial/half-built
- 35-54: basic
- <35: missing/critical

| Layer | Chrome/Firefox/Ladybird baseline | Fen score | Status |
|---|---|---:|---|
| Process model & isolation | Strict multi-process + sandbox + site isolation | 52 | Basic |
| Navigation/load pipeline | Streaming navigation + staged commit lifecycle | 58 | Basic/Partial |
| Networking/security | Full HTTP cache, CORS/CSP integration, hardened policies | 63 | Partial |
| HTML parsing | Incremental parser + preload scanner + robust recovery | 58 | Basic/Partial |
| CSS/cascade/selectors | Broad selectors/cascade coverage, strong invalidation | 55 | Partial (lower edge) |
| Layout engine | Mature block/inline/flex/grid/table + edge cases | 64 | Partial |
| Paint/compositing | Retained lists + GPU compositor + frame scheduler | 62 | Partial |
| JS engine | Near-full ECMAScript conformance | 35 | Basic (critical gap) |
| Web APIs/workers/SW | Broad API surface with strong conformance | 45 | Basic |
| Storage/cookies | Full semantics/partitioning/attributes | 57 | Partial |
| Event loop/runtime invariants | Tight task/microtask/render ordering | 67 | Partial |
| Verification (WPT/Test262 truth) | Large conformance suites, CI gates | 42 | Basic |

Overall pipeline maturity in the source snapshot:
- **55/100** (partial engine, not production-parity yet)
