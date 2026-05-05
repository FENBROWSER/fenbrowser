# FenBrowser Engine Status

Last updated: 2026-05-05

This file is a living status ledger. It is intentionally conservative and should only move forward when tests and artifacts support it.

## Rendering pipeline

| Area | Status | Evidence | Known limitations |
|---|---|---|---|
| HTML parser | Partial | `FenBrowser.Tests/Core/Parsing/*`, hostile parser regression script | Broad html5lib/WPT parity is incomplete |
| DOM tree | Partial | `FenBrowser.Tests/DOM/*`, targeted Host/Engine DOM tests | Edge-case behavior gaps still exist |
| CSS parser | Partial | `FenBrowser.Tests/StyleLayoutContract/*`, CSS inventory tooling | Long-tail property/value coverage is incomplete |
| Cascade | Partial | `FenBrowser.Tests/Engine/Cascade*` | Full conformance and edge specificity cases pending |
| Computed style | Partial | `FenBrowser.Tests/Engine/*Style*` | Incomplete long-tail value normalization |
| Layout tree | Partial | `FenBrowser.Tests/Layout/*` | Some complex tree/fragmentation cases are not closed |
| Block layout | Partial | `FenBrowser.Tests/Layout/*Block*` | Complex writing-mode/fragmentation gaps remain |
| Inline layout | Experimental | Targeted engine/layout tests | Not conformance-backed end to end |
| Flexbox | Experimental subset | Targeted flex tests in `FenBrowser.Tests/Layout` | Advanced cases still incomplete |
| Tables | Partial | Targeted table/layout tests | Full CSS table behavior not complete |
| Paint | Partial | Paint/layout fidelity and rendering tests | Visual parity on complex sites is incomplete |
| Hit testing | Partial | Host/input + engine interaction tests | Complex overlap/transforms still need closure |
| DOM events | Partial | DOM/Host event tests | Full ordering coverage across all paths is incomplete |
| JavaScript runtime | Experimental | JS runtime slices + focused integration tests | Broad `test262` parity is still in progress |

## Known hacks / quirks

Authoritative active entries live in [QUIRKS.md](./QUIRKS.md).

## Things not supported yet

- Full broad-spectrum standards conformance across HTML/CSS/JS
- Complete WPT pass coverage
- Complete `test262` pass coverage
