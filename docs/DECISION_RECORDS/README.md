# FenBrowser Architecture Decision Records

Status: DESIGNED.

Create one immutable Markdown record per accepted architecture decision. Proposed records use `DESIGNED`; unresolved contract choices use `BLOCKED_NEEDS_HUMAN_DECISION`. Supersede an accepted record with a new record rather than rewriting its history.

Filename: `NNNN-short-decision-title.md`.

## Required record format

```text
# ADR-NNNN: Title

Status:
Date:
Decision owners:
Areas:

## Context and evidence
## Decision
## Alternatives considered
## Security boundary
## Memory and lifetime
## IPC/public contract
## Failure and recovery behavior
## Performance evidence
## Compatibility and migration
## Verification
## Rollback/fallback
## Consequences and residual risks
```

## Decisions required before architecture changes

| Proposed ADR | Status | Blocking record |
| --- | --- | --- |
| DOM/FenJS wrapper ownership and cycle collection | BLOCKED_NEEDS_HUMAN_DECISION | `BLOCK-MEM-001` |
| Default renderer process mode and startup fallback | BLOCKED_NEEDS_HUMAN_DECISION | `BLOCK-PROC-001` |
| Network broker enforcement and unavailable-child policy | BLOCKED_NEEDS_HUMAN_DECISION | `BLOCK-NET-001` |
| IPC schema versioning and migration policy | BLOCKED_NEEDS_HUMAN_DECISION | `BLOCK-IPC-001` |

## Accepted records

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-media-software-decoder-backend.md) | Media software decoder backend is libavcodec behind our own bindings | ACCEPTED |
| [0002](0002-media-patent-encumbered-codecs.md) | H.264, AAC and HEVC come from OS decoders only | ACCEPTED |
| [0003](0003-media-audio-output.md) | Audio output uses direct platform backends | ACCEPTED |
| [0004](0004-media-process-placement.md) | Media demux and decode run in a dedicated media process | ACCEPTED |
| [0005](0005-media-drm-scope.md) | EME supports Clear Key only | ACCEPTED |
| [0006](0006-media-log-category.md) | Media logging uses LogCategory.Media (bit 30) | ACCEPTED |
