# ADR-0001: Media software decoder backend is libavcodec behind our own bindings

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Media, native interop, security

## Context and evidence
FenBrowser has no media engine (`docs/MEDIA_ENGINE_DESIGN.md`). Ladybird started with a hand-written Matroska demuxer and VP9 decoder, then added an FFmpeg layer (`Libraries/LibMedia/FFmpeg`) because hand-written codecs could not keep up. Chromium and Firefox both ship FFmpeg-derived decoders (Firefox as "ffvpx") alongside dav1d.

## Decision
Software audio and video decoding uses **libavcodec only**, dynamically linked under the LGPL, reached through a minimal P/Invoke layer we own in `FenBrowser.Media.Codecs.Ffmpeg`. The shipped build enables only the decoders we register (VP8, VP9, AV1, Opus, Vorbis, FLAC, MP3, PCM variants). It includes no libavformat, no network protocols and no filters. dav1d may be added for AV1 if libavcodec's native decoder misses the performance budgets.

Container parsing is **ours**, in managed code (`FenBrowser.Media`). libavcodec only ever receives elementary-stream packets that have passed our validators.

## Alternatives considered
- Hand-written codecs: rejected. This is the path Ladybird moved away from.
- FFmpeg.AutoGen: rejected. It is a large generated surface we would have to audit and keep in step.
- Platform decoders only: rejected. Opus and AV1 are not available on every OS, and results would not be reproducible in CI.

## Security boundary
Decoding runs in the media process (ADR-0004). Packets are size-limited before they reach native code, every call has a timeout, and a decoder that crashes repeatedly is quarantined.

## Memory and lifetime
Each native context has one managed owner (a `SafeHandle`) and is disposed on flush, error or player teardown. Frames are copied once into pooled or shared memory, and no native pointer outlives the call that produced it.

## IPC/public contract
None directly. The adapter implements `IVideoDecoder` and `IAudioDecoder`.

## Failure and recovery behavior
If the library is missing or the wrong version, the adapter does not register, `canPlayType` answers `""` for its codecs, and one `Media` log event records why.

## Performance evidence
To be measured against the design budgets (section 5) at M3.

## Compatibility and migration
The library version is pinned and checksum-verified. It is recorded in `THIRD_PARTY_DEPENDENCIES.md` when first added.

## Verification
Golden frame-hash tests through `fenplay dump-frames`, plus fuzzing of the packet path.

## Rollback/fallback
Unregister the adapter. The element then behaves as for unsupported media.

## Consequences and residual risks
libavcodec can still have memory-safety bugs. They are contained by process isolation and by building only the decoders we need.
