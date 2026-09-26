# ADR-0005: EME supports Clear Key only

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Media, security, licensing

## Context and evidence
Widevine and PlayReady require commercial licences and signed decryption modules (CDMs). Clear Key is the key system every EME implementation must support.

## Decision
EME (Encrypted Media Extensions, M9) supports **Clear Key only**. Requests for any other key system are rejected with `NotSupportedError`, and a `Media` event names the requested key system.

## Alternatives considered
- Widevine: not available to this project.

## Security boundary
Decryption happens in the media process. After the licence exchange, keys never enter the renderer.

## Memory and lifetime
Key sessions belong to their `MediaKeys` object and are zeroed when closed.

## IPC/public contract
Clear Key licence messages only.

## Failure and recovery behavior
For an unsupported key system, the promise is rejected as the spec requires and an event is logged.

## Performance evidence
Not applicable.

## Compatibility and migration
Sites that require DRM will not play. This is recorded in `KNOWN_GAPS.md` when M9 lands.

## Verification
The Clear Key tests in WPT `encrypted-media`.

## Rollback/fallback
Not applicable.

## Consequences and residual risks
Commercial streaming services remain unsupported.
