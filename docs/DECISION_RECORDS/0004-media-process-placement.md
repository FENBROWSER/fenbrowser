# ADR-0004: Media demux and decode run in a dedicated media process

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Media, process isolation, security

## Context and evidence
Media parsers are a leading source of browser security bugs. Firefox isolates decoders in its separate RDD process, and Chromium runs hardware decode in its GPU process. `PROCESS_MODEL.md` already lists decoder crashes as a case that must be contained.

## Decision
Add `TargetProcessKind.Media` (profile `media_process`, capabilities `media-decode,shared-memory`), modelled on `UtilityProcessIpc`.
- The process has no network, filesystem or window access.
- Bytes arrive through a brokered `IByteSource`.
- Video frames leave over a shared-memory ring buffer, and audio goes to the browser process (ADR-0003).
- Hardware decoders later move to the GPU process behind the same `IVideoDecoder` contract.

Until `BLOCK-PROC-002` is resolved, the same contract also runs **in-process** over a direct transport. Parser limits and decoder quarantine behave identically in both modes.

## Alternatives considered
- Decode in the renderer: rejected, because a codec bug would compromise the page's process.
- Decode only in the GPU process: rejected, because that exposes more of a privileged process to hostile input.

## Security boundary
From the renderer, the media process receives only control messages and byte ranges. It sends the compositor only frame handles. Every handle is tied to the process's session generation.

## Memory and lifetime
Each player has a memory ceiling. When the process exits, all of its frame and ring-buffer handles are invalidated.

## IPC/public contract
A new media message channel, covered by `IpcFuzzHarness`.

## Failure and recovery behavior
A crash fails only the players in that process (`MEDIA_ERR_DECODE`). The tab survives, and the next load starts a new media process.

## Performance evidence
Frames cross the process boundary without being copied, through the shared ring buffer. Control messages never carry media data.

## Compatibility and migration
The process arrives with M2. Earlier phases use in-process mode.

## Verification
Crash-containment test, stale-handle rejection test, and fuzzing.

## Rollback/fallback
In-process mode, with the same limits.

## Consequences and residual risks
One more process type, and control operations pay extra IPC latency.
