# FenBrowser Security Model

Status: RESEARCHED. Snapshot date: 2026-07-14.

## Threat model

Treat page HTML, script, CSS, images, fonts, media, network responses, extensions, dependencies, child processes, IPC payloads, and persisted origin data as untrusted. A compromised renderer must not gain ambient network, filesystem, clipboard, storage, window, GPU, or process-launch authority.

## Current trust boundaries

| Boundary | Current control | Status | Open risk |
| --- | --- | --- | --- |
| Page -> DOM/JS host bridge | Manual host-object dispatch and conversions | INTEGRATED | Broad binding surface lacks generated validation consistency |
| Renderer -> Host | Tokened, bounded renderer IPC in brokered mode | IMPLEMENTED | In-process mode is the default |
| Renderer -> Network | Network child/coordinator and capability token exist | STUBBED | Active resource path bypasses the coordinator |
| Host -> GPU/utility | Tokened target IPC and sandbox profiles | INTEGRATED | Current denial/recovery bundle evidence is absent |
| Native graphics/font/image | Managed wrappers over native libraries | INTEGRATED | Decode/raster remains inside trusted processes |
| Origin/network policy | Core network/security surfaces | INTEGRATED | Selected CORS/CSP/cookie/same-origin baselines are not current |
| Diagnostics | Local structured logs and bundles | INTEGRATED | Sensitive values need one enforced redaction/export policy |

## Required guarantees

- least privilege per process and capability;
- renderer network, persistent storage, clipboard, permission, GPU, decoder, filesystem, media, and download operations are brokered;
- same-origin, CORS, CSP, mixed-content, iframe sandbox, cookie, and permission checks fail explicitly;
- every IPC receiver validates untrusted input and records denials;
- process crashes invalidate tokens and native/shared-memory handles;
- hostile parsers and decoders have input, time, memory, and recursion ceilings;
- compatibility code cannot silently weaken a policy decision;
- raw trace bundles remain local unless redacted.

## Security task template

Every security-sensitive implementation records:

```text
Threat:
Trust boundary:
Failure mode:
Abuse case:
Validation and limits:
Test:
Logging event:
Regression protection:
Residual risk:
```

## Priority 0 audit queue

| Task | Status | Required evidence |
| --- | --- | --- |
| Establish selected same-origin/CORS/CSP/cookie/mixed-content baselines | NOT_STARTED | Local WPT categories and negative tests |
| Export sandbox denials and IPC rejections in every debug bundle | NOT_STARTED | Empty-or-populated typed artifacts plus rejection fixture |
| Remove or explicitly policy-gate network fallback | BLOCKED_NEEDS_HUMAN_DECISION | Accepted failure-mode ADR and integration tests |
| Verify brokered renderer has no ambient privileged API path | RESEARCHED | Capability inventory and brokered real-site run |
| Fuzz all IPC envelopes and payload validators | IMPLEMENTED | Existing baseline exists; sustained corpus result remains open |
| Isolate hostile image decoding | NOT_STARTED | Boundary design, memory ceiling, crash containment test |
| Define DOM/JS/native lifetime behavior | BLOCKED_NEEDS_HUMAN_DECISION | Memory ADR and teardown/stress tests |

## Security acceptance

No process/isolation capability is `DONE` until a malicious-input test, explicit deny path, trace evidence, and recovery behavior exist. Current architecture documentation does not authorize switching defaults or weakening failure behavior.
