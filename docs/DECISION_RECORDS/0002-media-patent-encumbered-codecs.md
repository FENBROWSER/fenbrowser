# ADR-0002: H.264, AAC and HEVC come from OS decoders only

Status: ACCEPTED
Date: 2026-09-17
Decision owners: project owner
Areas: Media, licensing

## Context and evidence
H.264, AAC and HEVC are covered by patents. Chromium builds them only when the `proprietary_codecs` flag is set. Firefox uses the operating system's decoders for H.264.

## Decision
FenBrowser does **not** compile these codecs into its libavcodec build. Only platform decoders provide them: Media Foundation (Windows), VideoToolbox (macOS) and VA-API (Linux, when present). They register through `DecoderRegistry` like every other decoder. `canPlayType` and Media Capabilities report what the running OS actually provides.

## Alternatives considered
- Ship them in libavcodec: rejected for licensing reasons.
- Never support them: rejected, because most MP4 content on the web would not play.

## Security boundary
OS decoders run in the media process (or the GPU process for hardware decode) under the same limits as every other decoder.

## Memory and lifetime
As in ADR-0001, with the platform adapter as the single owner of each native object.

## IPC/public contract
None beyond `IVideoDecoder` and `IAudioDecoder`.

## Failure and recovery behavior
With no platform decoder, the codec counts as unsupported: the element fires `error` with `MEDIA_ERR_SRC_NOT_SUPPORTED`, and a `Media` event names the missing component.

## Performance evidence
Platform decoders are usually hardware-backed, which is the preferred path in the design budgets.

## Compatibility and migration
MP4 playback (M4) starts with Media Foundation. On Linux it depends on whether VA-API is available.

## Verification
Per-OS capability tests; MP4 fixtures play wherever the platform decoder exists.

## Rollback/fallback
None is needed, because an unsupported codec is already handled as the fallback case.

## Consequences and residual risks
Linux systems without VA-API cannot play H.264 or AAC. This is accepted.
