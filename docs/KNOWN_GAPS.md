# FenBrowser Known Gaps

Snapshot date: 2026-07-16. These are evidence-backed gaps from the current checkout and current local artifacts.

| Priority | Gap | Failure bucket | Status | Evidence |
| --- | --- | --- | --- | --- |
| 0 | Default runtime remains in-process, so the ordinary browser path does not contain a compromised renderer | L / K | BLOCKED_NEEDS_HUMAN_DECISION | `ProcessIsolationCoordinatorFactory` returns `InProcessIsolationCoordinator` when the environment variable is absent |
| 0 | Network coordinator permits an in-process `HttpClient` fallback and no active production caller of its `SendAsync` was found | B / K / L | BLOCKED_NEEDS_HUMAN_DECISION | `NetworkProcessCoordinator`; repository reference search |
| 0 | IPC envelopes have validation and size caps but no common schema version, sender, receiver, permission ID, or per-message timeout/crash policy | L / K | BLOCKED_NEEDS_HUMAN_DECISION | Renderer, network, and target IPC contracts |
| 0 | Host-object ownership and cycle collection are not closed; the table uses strong references while comments describe future weak tracking | E / K | BLOCKED_NEEDS_HUMAN_DECISION | `HostObjectTable`, `BrowserScriptEngineRuntime`, `MEMORY_MODEL.md` |
| 1 | `Location.toString` and `Navigator.geolocation` are confirmed standard but absent from the active FenJS host surface | E / F | STUBBED | `20260716T115831Z` classifies both through local-WPT-derived IDL metadata; neither causes a callback failure or blocked milestone, so implementation is not yet selected |
| 1 | Broad `Engine/`, `DOM/`, `WebAPIs/`, `Workers/`, `Interaction/`, `Integration/`, `Host/`, `Rendering/`, `DevTools/`, and `Architecture/` test trees remain excluded from `FenBrowser.Tests`; only reviewed files are re-included. `Diagnostics/NavigationGlobalsProbeTests.cs` references the removed production probe type and needs a restore-or-retire decision | L | RESEARCHED | `Results/test-inventory/excluded_tests.json` (243/538 excluded, 16 groups); the TRACE-protecting subset is active and pinned by `RequiredBrowserIntegrationDiscoveryTests`; cookie PII-redaction tests are active, EngineLog-collection serialized, and green focused and in-suite |
| 1 | Full-suite `FenBrowser.Tests` runs are nondeterministic: ~108 of 1590 tests fail under parallelism while each passes focused; process-global state (EngineLog configuration, BrowserSettings, LogManager events) is reconfigured by tests outside the non-parallel collections | L | RESEARCHED | Three full-suite Release runs at the same tree produced 292/112/108 failures; clean-HEAD worktree baseline produced 282; all sampled failures are cross-test global-state races, not product regressions |
| 1 | Generated WebIDL bindings are not compiled into FenEngine | E | STUBBED | Generator and IDLs exist; `FenBrowser.FenEngine.csproj` removes generated binding sources |
| 1 | Browser-page nursery collection is disabled because transient roots are incomplete | D / E | RESEARCHED | `YoungAllocationsPerMinorGc = 0` with an explicit stale-handle correctness comment |
| 2 | Brokered IPC events, sandbox denials, and repeatable performance distributions are not yet populated in their now-mandatory bundle files | L / K | STUBBED | 2026-07-16 local bundle emits typed inactive IPC/sandbox records and a partial single performance sample |
| 2 | Required standalone dump and selector-inspection commands are not exposed by Tooling | H / I / J | NOT_STARTED | Current Tooling command switch and usage text |
| 2 | Script discovery and execution counters use different populations for dynamic scripts | C | RESEARCHED | Google reports 13 script elements and 17 completed executions |
| 2 | A `data:` image is counted as a failed network request | B | RESEARCHED | Google `network.json` failed request record |
| 2 | Modules, dynamic import, CORS, cookies, and storage have no fresh boot-critical selected WPT baseline in this audit | B / C / D / K | RESEARCHED | `TEST_BASELINE.md` |
| 3 | Google frame render is far above a 16.67 ms frame budget | I / J | RESEARCHED | One 419.108 ms diagnostic frame; watchdog triggered during raster, but no repeated benchmark/profile exists |
| 3 | `display_list.txt` is a flattened Paint Tree proxy rather than a canonical display-list command stream | J | STUBBED | Google `style_layout.json` reports `paint-tree-flattened` |

Lower-priority Test262 failures remain tracked in `docs/test262_results.md`. They must not displace these integration blockers unless a real-site reduction proves a language root cause.
