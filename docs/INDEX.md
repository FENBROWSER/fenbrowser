# The FenBrowser Codex

**The complete technical documentation for the FenBrowser Project.**

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

### [Appendix K: Token Savior Workspace Configuration](TOKEN_SAVIOR_WORKSPACE.md)

Project-local MCP setup for structural source navigation without indexing non-source fixture trees.

### [Appendix L: Specification Ownership Map](SPECS.md)

Subsystem-to-spec ownership and required source header contract for capability tracking.

### [Appendix M: Compliance Matrix](COMPLIANCE_MATRIX.md)

Capability-level compliance ledger with status, severity, owners, and verification targets.

### [Appendix N: Process Ownership and IPC Contracts](PROCESS_OWNERSHIP.md)

Process-boundary ownership map and required fail-closed startup and message envelope rules.

### [Appendix O: Spec Governance Map](spec_governance_map.json)

Machine-readable mapping of governed source files and required capability IDs used by tests and CI validation.

---

_State as of 2026-04-21_
