# FenBrowser Known Gaps

Snapshot date: 2026-07-14. These are evidence-backed gaps from the current checkout and current local artifacts.

| Priority | Gap | Failure bucket | Status | Evidence |
| --- | --- | --- | --- | --- |
| 0 | Default runtime remains in-process, so the ordinary browser path does not contain a compromised renderer | L / K | BLOCKED_NEEDS_HUMAN_DECISION | `ProcessIsolationCoordinatorFactory` returns `InProcessIsolationCoordinator` when the environment variable is absent |
| 0 | Network coordinator permits an in-process `HttpClient` fallback and no active production caller of its `SendAsync` was found | B / K / L | BLOCKED_NEEDS_HUMAN_DECISION | `NetworkProcessCoordinator`; repository reference search |
| 0 | IPC envelopes have validation and size caps but no common schema version, sender, receiver, permission ID, or per-message timeout/crash policy | L / K | BLOCKED_NEEDS_HUMAN_DECISION | Renderer, network, and target IPC contracts |
| 0 | Host-object ownership and cycle collection are not closed; the table uses strong references while comments describe future weak tracking | E / K | BLOCKED_NEEDS_HUMAN_DECISION | `HostObjectTable`, `BrowserScriptEngineRuntime`, `MEMORY_MODEL.md` |
| 1 | First-fatal-blocker output ignores event-loop callback failures and does not rank evidence across lifecycle stages | G / C / D / E / F | STUBBED | Google has 8 callback failures, empty `exceptions.json`, and "none captured" fatal summary |
| 1 | Terminal lifecycle sources disagree: navigation detail retains `loading`/DCL 0/load 0 while the probe and event-loop snapshot are complete | A / G / L | STUBBED | Google `summary.md`, `lifecycle.json`, and `event_loop.json` |
| 1 | Async logging is copied into the bundle without an explicit drain contract | L | RESEARCHED | Google event-loop snapshot reports callback failures not found in copied raw trace/log files |
| 1 | Missing API export conflates Web APIs, wrong-receiver probes, legacy feature detection, and site expandos | E / F | STUBBED | Google `closure_*`, `$goog_Thenable`, `Document.getAttribute`, and standards candidates share one list |
| 1 | Diagnostic, Host, Rendering, and Architecture test directories are excluded from `FenBrowser.Tests` | L | RESEARCHED | Test discovery found no `EventLoopTraceTests`, `RealSiteRenderDiagnostics`, or `RendererChildLoopIoTests` |
| 1 | Google interaction acceptance is unproven even though the page renders | L / G / F | RESEARCHED | No automated click/type/submit trace in the current bundle |
| 1 | Generated WebIDL bindings are not compiled into FenEngine | E | STUBBED | Generator and IDLs exist; `FenBrowser.FenEngine.csproj` removes generated binding sources |
| 1 | Browser-page nursery collection is disabled because transient roots are incomplete | D / E | RESEARCHED | `YoungAllocationsPerMinorGc = 0` with an explicit stale-handle correctness comment |
| 2 | `ipc.json`, `sandbox_denials.json`, and `performance.json` are absent from the bundle contract | L / K | NOT_STARTED | Current artifact manifest and Tooling writer |
| 2 | Required standalone dump and selector-inspection commands are not exposed by Tooling | H / I / J | NOT_STARTED | Current Tooling command switch and usage text |
| 2 | Script discovery and execution counters use different populations for dynamic scripts | C | RESEARCHED | Google reports 13 script elements and 17 completed executions |
| 2 | A `data:` image is counted as a failed network request | B | RESEARCHED | Google `network.json` failed request record |
| 2 | Modules, dynamic import, CORS, cookies, and storage have no fresh boot-critical selected WPT baseline in this audit | B / C / D / K | RESEARCHED | `TEST_BASELINE.md` |
| 3 | Google frame render is far above a 16.67 ms frame budget | I / J | RESEARCHED | One 419.108 ms diagnostic frame; watchdog triggered during raster, but no repeated benchmark/profile exists |
| 3 | `display_list.txt` is a flattened Paint Tree proxy rather than a canonical display-list command stream | J | STUBBED | Google `style_layout.json` reports `paint-tree-flattened` |

Lower-priority Test262 failures remain tracked in `docs/test262_results.md`. They must not displace these integration blockers unless a real-site reduction proves a language root cause.
