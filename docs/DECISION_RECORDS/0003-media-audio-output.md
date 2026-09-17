# ADR-0003: Audio output uses direct platform backends

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Media, host, native interop

## Context and evidence
Audio is the master clock for A/V sync. That needs the exact position of the last played sample, the output latency, and notice when the audio device changes. Chromium and Firefox (through cubeb) both call platform audio APIs directly.

## Decision
`FenBrowser.Media.Audio` implements `IAudioOutput` for WASAPI (Windows), PulseAudio/PipeWire (Linux) and CoreAudio (macOS), plus a null/file sink for tests. Audio output runs in the browser process behind a single mixer.

## Alternatives considered
- OpenAL through Silk.NET: rejected, because it lacks reliable latency reporting and device-change events.
- Binding cubeb: deferred. It could later replace the per-OS backends behind the same interface.

## Security boundary
Only the browser process opens audio devices. Other processes deliver samples through a bounded shared-memory ring buffer.

## Memory and lifetime
Ring buffers are allocated up front, and the realtime audio callback never allocates (asserted in debug builds).

## IPC/public contract
An audio ring-buffer handle plus control messages (start, stop, volume, device), versioned as described in `IPC_MODEL.md`.

## Failure and recovery behavior
If the device is lost, the clock falls back to monotonic time, a `ClockDiscontinuity` event is logged, and output reopens on the new default device.

## Performance evidence
The target is zero underruns in steady state (design section 5).

## Compatibility and migration
Windows first (M2), then Linux and macOS.

## Verification
Clock tests against the null sink, an underrun-counter test, and simulated device changes.

## Rollback/fallback
With no working backend, playback uses the null sink with a monotonic clock, and a log event records this.

## Consequences and residual risks
Three native backends have to be maintained.
