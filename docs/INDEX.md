# The FenBrowser Codex

**The complete technical documentation for the FenBrowser Project.**

### [Code Cleanup Rules](rules/code_cleanup.md)

## I. The Core Volumes

### [Volume I: System Manifest & Architecture](VOLUME_I_SYSTEM_MANIFEST.md)

> The high-level overview, core philosophy, and system architecture. Start here to understand the "Why" and the "How".

### [Volume II: The Core Foundation](VOLUME_II_CORE.md)

> `FenBrowser.Core`: The data layer. DOM, Resource Management, Parsing, and Network.

### [Volume III: The Engine Room](VOLUME_III_FENENGINE.md)

> `FenBrowser.FenEngine`: The heavy lifting. Layout Engine, Skia Rendering Pipeline, Scripting.

### [Volume IV: The Host Application](VOLUME_IV_HOST.md)

> `FenBrowser.Host`: The OS integration. Silk.NET Windowing, Input handling, UI Glue.

### [Volume V: Developer Tools](VOLUME_V_DEVTOOLS.md)

> `FenBrowser.DevTools`: Inspection. Elements Panel, Remote Debugging Protocol (CDP).

### [Volume VI: Extensions & Verification](VOLUME_VI_EXTENSIONS_VERIFICATION.md)

> `FenBrowser.WebDriver` & `Tests`: Verification. WebDriver Server, WPT/Test262 Compliance Runners.

---

## II. Appendices & Specifications

### [Appendix A: Compliance Roadmap](COMPLIANCE.md)

Detailed feature status tracking against W3C/WHATWG specifications.

### [Appendix B: Event Loop Semantics](SPEC_EVENT_LOOP.md)

The authoritative specification for the FenBrowser Event Loop/Microtask model.

### [Appendix C: Engineering Constitution](ENGINEERING_CONSTITUTION.md)

The core principles, coding standards, and "Do's and Don'ts" of the project.

### [Appendix D: Third Party Dependencies](THIRD_PARTY_DEPENDENCIES.md)

Audit of external libraries (Silk.NET, SkiaSharp, etc.) and their licenses.

### [Appendix E: Glossary](GLOSSARY.md)

Definitions of standard terminology ("Box", "Node", "Bridge", etc.).

### [Appendix F: Architecture Audit (2026-02-18)](ARCHITECTURE_AUDIT_2026_02_18.md)

Deep source audit with 1-100 scoring, maturity buckets, security risks, and issue-to-fix mapping.

### [Appendix G: Web Compatibility Production Plan](WEB_COMPAT_PRODUCTION_PLAN.md)

Standards-first compatibility strategy and feature roadmap (no site-specific hacks).

### [Appendix H: Pipeline Production Blueprint (2026-02-20)](PIPELINE_PRODUCTION_BLUEPRINT_2026_02_20.md)

Cross-engine pipeline comparison (Chrome/Firefox/Ladybird patterns), Fen maturity scoring, and production hardening plan.

### [Appendix I: Final Gap System](final_gap_system.md)

Master execution control sheet for subsystem-by-subsystem gap closure with a strict 90+ gate.

### [Appendix J: Pipeline Comparison Snapshot (2026-02-20)](PIPELINE_COMPARISON_SNAPSHOT_2026_02_20.md)

Saved comparison snapshot (Fen vs Chrome/Firefox/Ladybird baseline) captured from project review artifact.

### Appendix K: Token Savior Workspace Configuration

No maintained source file is present in the current checkout; this legacy index entry is intentionally not linked.

### [Appendix L: Specification Ownership Map](SPECS.md)

Subsystem-to-spec ownership and required source header contract for capability tracking.

### [Appendix M: Compliance Matrix](COMPLIANCE_MATRIX.md)

Capability-level compliance ledger with status, severity, owners, and verification targets.

### [Appendix N: Process Ownership and IPC Contracts](PROCESS_OWNERSHIP.md)

Process-boundary ownership map and required fail-closed startup and message envelope rules.

### [Appendix O: Spec Governance Map](spec_governance_map.json)

Machine-readable mapping of governed source files and required capability IDs used by tests and CI validation.

### [Appendix P: Security Capability Contract](security_capability_contract.json)

Machine-readable security impact + reason-code contract for security-sensitive capability IDs.

---

## III. Live Execution State

These files are evidence-led audit and execution ledgers. The six volumes above remain the canonical subsystem documentation.

### Architecture and boundaries

- [Current Architecture](ARCHITECTURE.md)
- [Process Model](PROCESS_MODEL.md)
- [IPC Model](IPC_MODEL.md)
- [Security Model](SECURITY_MODEL.md)
- [Memory and Lifetime Model](MEMORY_MODEL.md)
- [Native Interop Model](NATIVE_INTEROP_MODEL.md)
- [Architecture Decision Records](DECISION_RECORDS/README.md)

### Reality, diagnostics, and real-site work

- [Engine State](ENGINE_STATE.md)
- [Test Baseline](TEST_BASELINE.md)
- [Known Gaps](KNOWN_GAPS.md)
- [Diagnostic Spine](DIAGNOSTICS.md)
- [Real-Site Debugging](REAL_SITE_DEBUGGING.md)
- [Real-Site Tracker](REAL_SITE_TRACKER.md)
- [Missing API Tracker](MISSING_API_TRACKER.md)

### Capability trackers and execution control

- [JavaScript Engine Tracker](JS_ENGINE_TRACKER.md)
- [WebIDL Bindings Tracker](WEBIDL_BINDINGS_TRACKER.md)
- [DOM API Tracker](DOM_API_TRACKER.md)
- [Event Loop Tracker](EVENT_LOOP_TRACKER.md)
- [Script Loading Tracker](SCRIPT_LOADING_TRACKER.md)
- [Network and Fetch Tracker](NETWORK_FETCH_TRACKER.md)
- [CSS, Layout, and Paint Tracker](CSS_LAYOUT_TRACKER.md)
- [Performance Dashboard](PERFORMANCE_DASHBOARD.md)
- [Risk Register](RISK_REGISTER.md)
- [Human-Decision Blockers](BLOCKERS.md)
- [Dependency-Ready Next Tasks](NEXT_TASKS.md)

---

_State as of 2026-07-14_
