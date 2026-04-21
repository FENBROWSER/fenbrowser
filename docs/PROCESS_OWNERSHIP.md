# FenBrowser Process Ownership and Contracts

State as of: 2026-04-21
Owner: Host + Core Architecture

## Purpose

Define hard ownership boundaries for process targets and IPC contracts so engine growth remains modular and fail-closed.

## Process Targets

| Target | Primary Responsibilities | Non-Responsibilities |
| --- | --- | --- |
| Browser/Host Process | Windowing, tab lifecycle, process orchestration, policy gates, user input routing | DOM/CSS/JS execution logic |
| Renderer Process | DOM, CSS, layout, paint tree, script runtime, frame output payloads | Direct OS window ownership, policy authority |
| Network Process | Request execution, CORS/CSP/cookie policy application, response envelope shaping | DOM mutation, paint/layout decisions |
| GPU Process | Raster/composite execution, graphics backend isolation | Document model decisions, security policy |
| Utility Process | Isolated helper workloads (bounded tools/services) | Core rendering and policy authority |

## Canonical IPC Envelope

All cross-process messages must include:

- `RequestId` (unique correlation id)
- `TargetKind` (`Renderer`/`Network`/`Gpu`/`Utility`)
- `TabId` and `FrameId` when frame-scoped
- `Origin` where policy-sensitive
- `AuthToken` for channel authentication
- `DeadlineUtc` for bounded operations
- `FailureCode` on non-success responses

Messages that do not carry required fields must be rejected by contract validators.

## Startup and Fail-Closed Contract

For each child target:

1. Host resolves sandbox profile.
2. Host launches child with scoped capabilities.
3. Child performs startup assertions and sends `Ready`.
4. Host marks target as active only after authenticated ready handshake.
5. Any sandbox or handshake failure terminates child and returns explicit startup failure.

No implicit unsandboxed fallback is allowed without explicit policy override.

## Ownership Rules

- Policy decisions are owned by `FenBrowser.Core` security contracts and enforced by `FenBrowser.Host`.
- Renderer/network/gpu/utility channels must not mutate policy state directly.
- Renderer-side compatibility behavior must not use domain or class-name hacks.
- Process runtime code must expose explicit deny-path logs for policy rejections.

## Required Verification

Per target process family:

- startup success path test
- startup handshake failure test
- sandbox resolution failure test
- auth token mismatch test
- channel read-loop termination recovery test

For contract changes:

- add IPC fuzz corpus cases for new message fields and invalid envelopes
- add at least one integration test with real coordinator wiring

