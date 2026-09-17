# ADR-0006: Media logging uses LogCategory.Media (bit 30)

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Logging, diagnostics

## Context and evidence
`LogCategory` is an `int` flags enum with bits 0 to 29 already used. Media needs its own category so per-player diagnostics can be filtered.

## Decision
Add `LogCategory.Media = 1 << 30`, the last free positive bit. Media logging follows the existing call-site rules:
- no per-frame or per-packet lines;
- counters and state transitions only;
- every player's events carry a `PlayerId`.

`FenBrowser.Media` itself logs through `IMediaLogSink`, which the host adapts to `FenLogger`.

## Alternatives considered
- Reusing `Rendering` or `Network`: rejected, because media lines could no longer be filtered on their own.
- Widening the enum to `long` now: deferred until a change actually needs another bit.

## Security boundary
Log lines never contain URLs with credentials, media bytes or key material.

## Memory and lifetime
Not applicable.

## IPC/public contract
Media process logs travel over the existing `EngineLogIpc`.

## Failure and recovery behavior
Not applicable.

## Performance evidence
Hot paths only increment counters. Each active player emits one summary per second.

## Compatibility and migration
`All = int.MaxValue` already includes bit 30.

## Verification
A unit test checks that `Media` overlaps no other category and is included in `All`.

## Rollback/fallback
Not applicable.

## Consequences and residual risks
No free bits remain in the enum.
