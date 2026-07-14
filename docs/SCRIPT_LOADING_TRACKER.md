# FenBrowser Script Loading Tracker

Snapshot date: 2026-07-14.

| Behavior | Status | Evidence | Remaining gap |
| --- | --- | --- | --- |
| Parser-inserted classic scripts | TESTED | Google blocking scripts executed | Add deterministic order fixture to included tests |
| External classic scripts | TESTED | Google fetched and executed external scripts | Record HTTP/MIME/policy decisions per script |
| `async` | INTEGRATED | Google includes one async script | Ordering reduction and DCL/load blocking proof |
| `defer` | INTEGRATED | Google includes one defer script | Ordering reduction against parser completion |
| Dynamic script insertion | INTEGRATED | Executions exceed initial script elements | Normalize discovery and execution counters |
| Module scripts | RESEARCHED | Runtime has module-related code | Google run contains zero modules; graph/linking proof absent |
| Dynamic import | RESEARCHED | JavaScript engine supports syntax/runtime pieces | Browser host resolution and fetch graph not proven |
| Import maps | RESEARCHED | No current real-site trace proof | Selected WPT required |

## Required per-script fields

Stable script ID, URL/source label, HTML source offset/line/column, inline/external, classic/module, async, defer, no-module, parser-inserted, blocking state, request ID, MIME, fetch result, ready timestamp, execution order, realm, exception, and DCL/load blocking interval.

## Google evidence

The 2026-07-14 bundle reports 13 discovered/eligible script elements, six fetch starts/completions, and 17 completed executions with zero script failures. The count difference is not itself a failure; it shows that discovery and execution currently use different populations, especially for dynamically inserted scripts. The tracker remains INTEGRATED, not `DONE`.
